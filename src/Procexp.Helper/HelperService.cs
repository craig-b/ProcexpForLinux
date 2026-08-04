using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Procexp.Model;
using Procexp.Privileged;
using Procexp.Sampling;

namespace Procexp.Helper;

/// <summary>
/// The privileged daemon. Listens on a Unix socket and serves the handful of
/// operations that genuinely need root.
/// </summary>
/// <remarks>
/// Far smaller than the macOS XPC helper, because Linux restricts far less. Only
/// three per-process files are owner-only — <c>io</c>, <c>smaps_rollup</c> and
/// <c>environ</c> — and signalling another user's process needs privilege.
/// Everything else the UI shows is already world-readable, so it never comes here.
///
/// Deliberately narrow: there is no "read arbitrary path" operation, no shell,
/// and no way to name a file. The client picks an operation from a closed set and
/// supplies a process identity; the helper decides which path that maps to.
/// </remarks>
internal sealed partial class HelperService(Action<string> log)
{
    private const int MaxRequestBytes = 8192;

    /// <summary>
    /// The ordinary sampling engine, used for the detail reads. Running as root
    /// it succeeds where the client was refused, so there is no second parser.
    /// </summary>
    private readonly ProcSampler _sampler = new();

    /// <summary>
    /// A cap on environment size. A process can hold megabytes of environment,
    /// and streaming that back unbounded turns a read into a memory-exhaustion
    /// vector against the daemon.
    /// </summary>
    private const int MaxEnvironmentBytes = 512 * 1024;

    internal async Task RunAsync(CancellationToken cancellationToken)
    {
        PrepareSocketDirectory();

        if (File.Exists(HelperConstants.SocketPath))
        {
            File.Delete(HelperConstants.SocketPath);
        }

        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(HelperConstants.SocketPath));

        // Permissions are the real access gate. Group-readable and -writable,
        // world nothing: membership of the access group is what authorises a user,
        // and that is a deliberate administrative decision at install time.
        File.SetUnixFileMode(
            HelperConstants.SocketPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite |
            UnixFileMode.GroupRead | UnixFileMode.GroupWrite);

        ApplyGroupOwnership(HelperConstants.SocketPath);

        listener.Listen(16);
        log($"listening on {HelperConstants.SocketPath}");

        while (!cancellationToken.IsCancellationRequested)
        {
            Socket peer;
            try
            {
                peer = await listener.AcceptAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException e)
            {
                log($"accept failed: {e.Message}");
                continue;
            }

            // One connection at a time would be simpler, but a client that stalls
            // mid-request would then block every other client.
            _ = Task.Run(() => ServeAsync(peer, cancellationToken), CancellationToken.None);
        }
    }

    private async Task ServeAsync(Socket peer, CancellationToken cancellationToken)
    {
        using (peer)
        {
            var credentials = PeerCredentialReader.Read(peer);
            if (credentials is null)
            {
                log("rejecting peer: kernel would not supply credentials");
                return;
            }

            // Socket permissions have already gated this, but re-checking means a
            // misconfigured socket mode cannot silently open the daemon up, and it
            // gives the audit log a real identity to record.
            if (!IsAuthorised(credentials.Value))
            {
                log($"rejecting peer pid {credentials.Value.Pid} uid {credentials.Value.Uid}: not authorised");
                return;
            }

            try
            {
                using var stream = new NetworkStream(peer, ownsSocket: false);
                using var reader = new StreamReader(stream, Encoding.UTF8, false, 1024, leaveOpen: true);
                await using var writer = new StreamWriter(stream, new UTF8Encoding(false), 1024, leaveOpen: true)
                {
                    AutoFlush = true,
                };

                while (!cancellationToken.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                    if (line is null)
                    {
                        break;
                    }

                    if (line.Length > MaxRequestBytes)
                    {
                        await WriteAsync(writer, HelperResponse.Failure("request too large")).ConfigureAwait(false);
                        break;
                    }

                    var response = Handle(line, credentials.Value);
                    await WriteAsync(writer, response).ConfigureAwait(false);
                }
            }
            catch (Exception e) when (e is IOException or SocketException or OperationCanceledException)
            {
                // The client went away mid-conversation; nothing to do.
            }
        }
    }

    private static Task WriteAsync(StreamWriter writer, HelperResponse response) =>
        writer.WriteLineAsync(JsonSerializer.Serialize(response, HelperJsonContext.Default.HelperResponse));

    private HelperResponse Handle(string line, PeerCredentials peer)
    {
        HelperRequest? request;
        try
        {
            request = JsonSerializer.Deserialize(line, HelperJsonContext.Default.HelperRequest);
        }
        catch (JsonException)
        {
            return HelperResponse.Failure("malformed request");
        }

        if (request is null)
        {
            return HelperResponse.Failure("empty request");
        }

        if (request.Operation == HelperOperation.Hello)
        {
            return new HelperResponse { Ok = true, Version = HelperConstants.ProtocolVersion };
        }

        // Every remaining operation names a process, and every one of them
        // re-verifies identity before touching it.
        if (!ProcessIdentity.Verify(request.Pid, request.StartTime))
        {
            return HelperResponse.Failure("process identity does not match; it has exited or the pid was recycled");
        }

        return request.Operation switch
        {
            HelperOperation.ReadIo => ReadProcFile($"/proc/{request.Pid}/io"),
            HelperOperation.ReadProportionalMemory => ReadProcFile($"/proc/{request.Pid}/smaps_rollup"),
            HelperOperation.ReadEnvironment => ReadEnvironment(request.Pid, peer),
            HelperOperation.ReadModules => ReadModules(request),
            HelperOperation.ReadFileDescriptors => ReadFileDescriptors(request),
            HelperOperation.Signal => SendSignal(request, peer),
            HelperOperation.SetNice => SetNice(request, peer),
            _ => HelperResponse.Failure("unknown operation"),
        };
    }

    private static HelperResponse ReadProcFile(string path)
    {
        try
        {
            return HelperResponse.Success(File.ReadAllText(path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return HelperResponse.Failure($"could not read {path}");
        }
    }

    /// <summary>
    /// Read a process environment.
    /// </summary>
    /// <remarks>
    /// The most sensitive thing the helper does. Environments routinely hold API
    /// tokens, database passwords and session secrets, so this hands a member of
    /// the access group the credentials of every process on the machine.
    ///
    /// That is inherent to the feature — Process Explorer's Environment tab does
    /// exactly this — but it is why group membership must be treated as equivalent
    /// to root, and why every read is logged with the requesting uid.
    /// </remarks>
    private HelperResponse ReadEnvironment(int pid, PeerCredentials peer)
    {
        try
        {
            using var stream = new FileStream($"/proc/{pid}/environ", FileMode.Open, FileAccess.Read);
            var buffer = new byte[MaxEnvironmentBytes];
            var read = stream.ReadAtLeast(buffer, MaxEnvironmentBytes, throwOnEndOfStream: false);

            log($"uid {peer.Uid} (pid {peer.Pid}) read the environment of pid {pid}");

            return HelperResponse.Success(Convert.ToBase64String(buffer.AsSpan(0, read)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return HelperResponse.Failure($"could not read the environment of pid {pid}");
        }
    }

    /// <summary>
    /// Mapped files for a process the caller cannot read directly.
    /// </summary>
    /// <remarks>
    /// Reuses the ordinary sampling engine. Running as root it simply succeeds
    /// where the client was refused, so there is no separate privileged parser to
    /// keep in step with the unprivileged one.
    ///
    /// Unlike the environment, this is not logged per call: the lower pane
    /// re-reads it on every refresh, and an audit line a second would drown the
    /// entries that matter.
    /// </remarks>
    private HelperResponse ReadModules(HelperRequest request)
    {
        try
        {
            var modules = _sampler.ModulesAsync(new ProcessId(request.Pid, request.StartTime))
                .AsTask().GetAwaiter().GetResult();

            return HelperResponse.Success(
                JsonSerializer.Serialize(modules, HelperJsonContext.Default.IReadOnlyListModuleInfo));
        }
        catch (ProviderException e)
        {
            return HelperResponse.Failure(e.Message);
        }
    }

    private HelperResponse ReadFileDescriptors(HelperRequest request)
    {
        try
        {
            var descriptors = _sampler.FileDescriptorsAsync(new ProcessId(request.Pid, request.StartTime))
                .AsTask().GetAwaiter().GetResult();

            return HelperResponse.Success(
                JsonSerializer.Serialize(descriptors, HelperJsonContext.Default.IReadOnlyListFileDescriptorInfo));
        }
        catch (ProviderException e)
        {
            return HelperResponse.Failure(e.Message);
        }
    }

    private HelperResponse SendSignal(HelperRequest request, PeerCredentials peer)
    {
        // A closed set: enough to control a process, not enough to be a general
        // signal-injection primitive.
        if (request.Signal is not (1 or 2 or 9 or 15 or 18 or 19))
        {
            return HelperResponse.Failure($"signal {request.Signal} is not permitted");
        }

        if (request.Pid == 1)
        {
            return HelperResponse.Failure("refusing to signal pid 1");
        }

        log($"uid {peer.Uid} (pid {peer.Pid}) sent signal {request.Signal} to pid {request.Pid}");

        if (Kill(request.Pid, request.Signal) != 0)
        {
            return HelperResponse.Failure($"kill failed with errno {Marshal.GetLastPInvokeError()}");
        }

        return HelperResponse.Success();
    }

    private HelperResponse SetNice(HelperRequest request, PeerCredentials peer)
    {
        var nice = Math.Clamp(request.Nice, -20, 19);

        log($"uid {peer.Uid} (pid {peer.Pid}) set nice {nice} on pid {request.Pid}");

        Marshal.SetLastSystemError(0);
        if (SetPriority(0, (uint)request.Pid, nice) == -1 && Marshal.GetLastPInvokeError() != 0)
        {
            return HelperResponse.Failure($"setpriority failed with errno {Marshal.GetLastPInvokeError()}");
        }

        return HelperResponse.Success();
    }

    /// <summary>
    /// Whether a peer may be served.
    /// </summary>
    /// <remarks>
    /// root always; otherwise membership of the access group, read from the group
    /// database rather than trusting the gid the peer happens to be running under,
    /// since a process can hold the group as a supplementary one.
    /// </remarks>
    private static bool IsAuthorised(PeerCredentials peer)
    {
        if (peer.Uid == 0)
        {
            return true;
        }

        var group = ReadGroup(HelperConstants.AccessGroup);
        if (group is null)
        {
            // No group means nobody was authorised at install time.
            return false;
        }

        if (peer.Gid == group.Value.Gid)
        {
            return true;
        }

        var name = ReadUserName(peer.Uid);
        return name is not null && group.Value.Members.Contains(name, StringComparer.Ordinal);
    }

    private static (uint Gid, string[] Members)? ReadGroup(string name)
    {
        try
        {
            foreach (var line in File.ReadLines("/etc/group"))
            {
                // name:password:gid:member,member
                var fields = line.Split(':');
                if (fields.Length >= 4 && fields[0] == name && uint.TryParse(fields[2], out var gid))
                {
                    return (gid, fields[3].Split(',', StringSplitOptions.RemoveEmptyEntries));
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return null;
    }

    private static string? ReadUserName(uint uid)
    {
        try
        {
            foreach (var line in File.ReadLines("/etc/passwd"))
            {
                var fields = line.Split(':');
                if (fields.Length >= 3 && uint.TryParse(fields[2], out var candidate) && candidate == uid)
                {
                    return fields[0];
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return null;
    }

    private void PrepareSocketDirectory()
    {
        Directory.CreateDirectory(HelperConstants.SocketDirectory);
        File.SetUnixFileMode(
            HelperConstants.SocketDirectory,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

        ApplyGroupOwnership(HelperConstants.SocketDirectory);
    }

    private void ApplyGroupOwnership(string path)
    {
        var group = ReadGroup(HelperConstants.AccessGroup);
        if (group is null)
        {
            log($"warning: group '{HelperConstants.AccessGroup}' does not exist, so only root can connect");
            return;
        }

        if (Chown(path, 0, group.Value.Gid) != 0)
        {
            log($"warning: could not set group ownership on {path} (errno {Marshal.GetLastPInvokeError()})");
        }
    }

    [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static partial int Kill(int pid, int signal);

    [LibraryImport("libc", EntryPoint = "setpriority", SetLastError = true)]
    private static partial int SetPriority(int which, uint who, int priority);

    [LibraryImport("libc", EntryPoint = "chown", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int Chown(string path, uint owner, uint group);
}
