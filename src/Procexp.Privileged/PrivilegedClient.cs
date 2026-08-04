using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Procexp.Model;

namespace Procexp.Privileged;

/// <summary>
/// Client for the privileged helper.
/// </summary>
/// <remarks>
/// Optional at runtime. Everything the app shows works without it; the helper
/// only adds the owner-restricted fields for other users' processes — I/O
/// counters, proportional memory and environments — and the ability to signal
/// them.
///
/// Connections are made per call rather than held open. The helper is contacted
/// rarely, and a long-lived connection to a root daemon is a liability that buys
/// nothing at this frequency.
/// </remarks>
public sealed class PrivilegedClient
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    /// <summary>Whether the helper socket exists and we can reach it.</summary>
    public static bool IsAvailable => File.Exists(HelperConstants.SocketPath);

    /// <summary>Verify the helper is reachable and speaks a protocol we understand.</summary>
    public async Task<bool> HandshakeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await SendAsync(
                new HelperRequest { Operation = HelperOperation.Hello }, cancellationToken).ConfigureAwait(false);

            return response.Ok && response.Version == HelperConstants.ProtocolVersion;
        }
        catch (ProviderException)
        {
            return false;
        }
    }

    /// <summary>Read <c>/proc/PID/io</c> for a process we do not own.</summary>
    public async Task<(ulong Read, ulong Written)?> ReadIoAsync(
        ProcessId id, CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(
            new HelperRequest
            {
                Operation = HelperOperation.ReadIo,
                Pid = id.Pid,
                StartTime = id.StartTime,
            },
            cancellationToken).ConfigureAwait(false);

        if (!response.Ok || response.Content is null)
        {
            return null;
        }

        ulong read = 0, written = 0;
        foreach (var line in response.Content.Split('\n'))
        {
            if (line.StartsWith("read_bytes:", StringComparison.Ordinal))
            {
                _ = ulong.TryParse(line["read_bytes:".Length..].Trim(), out read);
            }
            else if (line.StartsWith("write_bytes:", StringComparison.Ordinal))
            {
                _ = ulong.TryParse(line["write_bytes:".Length..].Trim(), out written);
            }
        }

        return (read, written);
    }

    /// <summary>Read proportional set size from <c>smaps_rollup</c>.</summary>
    public async Task<ulong?> ReadProportionalMemoryAsync(
        ProcessId id, CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(
            new HelperRequest
            {
                Operation = HelperOperation.ReadProportionalMemory,
                Pid = id.Pid,
                StartTime = id.StartTime,
            },
            cancellationToken).ConfigureAwait(false);

        if (!response.Ok || response.Content is null)
        {
            return null;
        }

        foreach (var line in response.Content.Split('\n'))
        {
            if (line.StartsWith("Pss:", StringComparison.Ordinal))
            {
                var value = line["Pss:".Length..].Trim().Split(' ')[0];
                return ulong.TryParse(value, out var kilobytes) ? kilobytes * 1024 : null;
            }
        }

        return null;
    }

    /// <summary>Read the environment of a process we do not own.</summary>
    public async Task<IReadOnlyDictionary<string, string>?> ReadEnvironmentAsync(
        ProcessId id, CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(
            new HelperRequest
            {
                Operation = HelperOperation.ReadEnvironment,
                Pid = id.Pid,
                StartTime = id.StartTime,
            },
            cancellationToken).ConfigureAwait(false);

        if (!response.Ok || response.Content is null)
        {
            return null;
        }

        // Sent base64 because the raw content is NUL-separated and not valid
        // text, which JSON cannot carry intact.
        byte[] raw;
        try
        {
            raw = Convert.FromBase64String(response.Content);
        }
        catch (FormatException)
        {
            return null;
        }

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in Encoding.UTF8.GetString(raw).Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = entry.IndexOf('=');
            if (equals > 0)
            {
                result[entry[..equals]] = entry[(equals + 1)..];
            }
        }

        return result;
    }

    /// <summary>Mapped files for a process the caller cannot read directly.</summary>
    public async Task<IReadOnlyList<ModuleInfo>?> ReadModulesAsync(
        ProcessId id, CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(
            new HelperRequest
            {
                Operation = HelperOperation.ReadModules,
                Pid = id.Pid,
                StartTime = id.StartTime,
            },
            cancellationToken).ConfigureAwait(false);

        return response.Ok && response.Content is not null
            ? HelperPayload.Modules(response.Content)
            : null;
    }

    /// <summary>Descriptors for a process the caller cannot read directly.</summary>
    public async Task<IReadOnlyList<FileDescriptorInfo>?> ReadFileDescriptorsAsync(
        ProcessId id, CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(
            new HelperRequest
            {
                Operation = HelperOperation.ReadFileDescriptors,
                Pid = id.Pid,
                StartTime = id.StartTime,
            },
            cancellationToken).ConfigureAwait(false);

        return response.Ok && response.Content is not null
            ? HelperPayload.FileDescriptors(response.Content)
            : null;
    }

    public async Task SignalAsync(ProcessId id, int signal, CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(
            new HelperRequest
            {
                Operation = HelperOperation.Signal,
                Pid = id.Pid,
                StartTime = id.StartTime,
                Signal = signal,
            },
            cancellationToken).ConfigureAwait(false);

        if (!response.Ok)
        {
            throw new ProviderException(ProviderErrorKind.Underlying, response.Error ?? "helper refused the signal");
        }
    }

    public async Task SetNiceAsync(ProcessId id, int nice, CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(
            new HelperRequest
            {
                Operation = HelperOperation.SetNice,
                Pid = id.Pid,
                StartTime = id.StartTime,
                Nice = nice,
            },
            cancellationToken).ConfigureAwait(false);

        if (!response.Ok)
        {
            throw new ProviderException(ProviderErrorKind.Underlying, response.Error ?? "helper refused the request");
        }
    }

    private static async Task<HelperResponse> SendAsync(
        HelperRequest request, CancellationToken cancellationToken)
    {
        if (!IsAvailable)
        {
            throw new ProviderException(ProviderErrorKind.HelperUnavailable, "the privileged helper is not running");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);

        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);

        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(HelperConstants.SocketPath), timeout.Token)
                .ConfigureAwait(false);

            await using var stream = new NetworkStream(socket, ownsSocket: false);
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false), 1024, leaveOpen: true)
            {
                AutoFlush = true,
            };
            using var reader = new StreamReader(stream, Encoding.UTF8, false, 1024, leaveOpen: true);

            var json = JsonSerializer.Serialize(request, HelperJsonContext.Default.HelperRequest);
            await writer.WriteLineAsync(json.AsMemory(), timeout.Token).ConfigureAwait(false);

            var line = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false);
            if (line is null)
            {
                throw new ProviderException(ProviderErrorKind.HelperUnavailable, "the helper closed the connection");
            }

            return JsonSerializer.Deserialize(line, HelperJsonContext.Default.HelperResponse)
                   ?? HelperResponse.Failure("unreadable response");
        }
        catch (SocketException e)
        {
            // Permission denied here means the user is not in the access group,
            // which is an install-time decision rather than a bug.
            throw new ProviderException(
                ProviderErrorKind.HelperUnavailable,
                e.SocketErrorCode == SocketError.AccessDenied
                    ? $"not permitted to use the helper — membership of the '{HelperConstants.AccessGroup}' group is required"
                    : $"could not reach the helper: {e.Message}");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ProviderException(ProviderErrorKind.HelperUnavailable, "the helper did not respond in time");
        }
    }
}
