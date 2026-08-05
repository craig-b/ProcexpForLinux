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
        return await ProbeAsync(cancellationToken).ConfigureAwait(false) is null;
    }

    /// <summary>
    /// Handshake that keeps the reason: null on success, otherwise a
    /// human-readable explanation of why the helper cannot be used — the
    /// distinction <see cref="HandshakeAsync"/> flattens away.
    /// </summary>
    public async Task<string?> ProbeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await SendAsync(
                    new HelperRequest { Operation = HelperOperation.Hello },
                    cancellationToken
                )
                .ConfigureAwait(false);

            return response switch
            {
                { Ok: false } => response.Error ?? "the helper rejected the handshake",
                { Version: not HelperConstants.ProtocolVersion } =>
                    $"the helper speaks protocol version {response.Version}, "
                        + $"this build expects {HelperConstants.ProtocolVersion} — "
                        + "restart it to pick up the installed binary",
                _ => null,
            };
        }
        catch (ProviderException e)
        {
            return e.Message;
        }
    }

    /// <summary>Read <c>/proc/PID/io</c> for a process we do not own.</summary>
    public async Task<(ulong Read, ulong Written)?> ReadIoAsync(
        ProcessId id,
        CancellationToken cancellationToken = default
    )
    {
        var response = await SendAsync(
                new HelperRequest
                {
                    Operation = HelperOperation.ReadIo,
                    Pid = id.Pid,
                    StartTime = id.StartTime,
                },
                cancellationToken
            )
            .ConfigureAwait(false);

        if (!response.Ok || response.Content is null)
        {
            return null;
        }

        ulong read = 0,
            written = 0;
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
        ProcessId id,
        CancellationToken cancellationToken = default
    )
    {
        var response = await SendAsync(
                new HelperRequest
                {
                    Operation = HelperOperation.ReadProportionalMemory,
                    Pid = id.Pid,
                    StartTime = id.StartTime,
                },
                cancellationToken
            )
            .ConfigureAwait(false);

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
        ProcessId id,
        CancellationToken cancellationToken = default
    )
    {
        var response = await SendAsync(
                new HelperRequest
                {
                    Operation = HelperOperation.ReadEnvironment,
                    Pid = id.Pid,
                    StartTime = id.StartTime,
                },
                cancellationToken
            )
            .ConfigureAwait(false);

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
        foreach (
            var entry in Encoding
                .UTF8.GetString(raw)
                .Split('\0', StringSplitOptions.RemoveEmptyEntries)
        )
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
        ProcessId id,
        CancellationToken cancellationToken = default
    )
    {
        var response = await SendAsync(
                new HelperRequest
                {
                    Operation = HelperOperation.ReadModules,
                    Pid = id.Pid,
                    StartTime = id.StartTime,
                },
                cancellationToken
            )
            .ConfigureAwait(false);

        return response.Ok && response.Content is not null
            ? HelperPayload.Modules(response.Content)
            : null;
    }

    /// <summary>Descriptors for a process the caller cannot read directly.</summary>
    public async Task<IReadOnlyList<FileDescriptorInfo>?> ReadFileDescriptorsAsync(
        ProcessId id,
        CancellationToken cancellationToken = default
    )
    {
        var response = await SendAsync(
                new HelperRequest
                {
                    Operation = HelperOperation.ReadFileDescriptors,
                    Pid = id.Pid,
                    StartTime = id.StartTime,
                },
                cancellationToken
            )
            .ConfigureAwait(false);

        return response.Ok && response.Content is not null
            ? HelperPayload.FileDescriptors(response.Content)
            : null;
    }

    /// <summary>
    /// A thread's kernel stack, as the raw <c>/proc/PID/task/TID/stack</c> text.
    /// </summary>
    /// <remarks>
    /// Throws rather than returning null on failure: unlike the fallback reads,
    /// there is no unprivileged path to fall back to, so the caller needs the
    /// reason to show, not just the absence of an answer.
    /// </remarks>
    public async Task<string> ReadThreadKernelStackAsync(
        ProcessId id,
        int tid,
        CancellationToken cancellationToken = default
    )
    {
        var response = await SendAsync(
                new HelperRequest
                {
                    Operation = HelperOperation.ReadThreadKernelStack,
                    Pid = id.Pid,
                    StartTime = id.StartTime,
                    Tid = tid,
                },
                cancellationToken
            )
            .ConfigureAwait(false);

        if (!response.Ok)
        {
            throw new ProviderException(
                ProviderErrorKind.Underlying,
                response.Error switch
                {
                    // A helper predating this operation answers with its generic
                    // rejection; translate that into the actual remedy.
                    "unknown operation" => "the installed helper predates kernel stacks — "
                        + "reinstall it and 'systemctl restart procexp-helper'",
                    { } error => error,
                    null => "helper refused the request",
                }
            );
        }

        return response.Content ?? "";
    }

    public async Task SignalAsync(
        ProcessId id,
        int signal,
        CancellationToken cancellationToken = default
    )
    {
        var response = await SendAsync(
                new HelperRequest
                {
                    Operation = HelperOperation.Signal,
                    Pid = id.Pid,
                    StartTime = id.StartTime,
                    Signal = signal,
                },
                cancellationToken
            )
            .ConfigureAwait(false);

        if (!response.Ok)
        {
            throw new ProviderException(
                ProviderErrorKind.Underlying,
                response.Error ?? "helper refused the signal"
            );
        }
    }

    public async Task SetNiceAsync(
        ProcessId id,
        int nice,
        CancellationToken cancellationToken = default
    )
    {
        var response = await SendAsync(
                new HelperRequest
                {
                    Operation = HelperOperation.SetNice,
                    Pid = id.Pid,
                    StartTime = id.StartTime,
                    Nice = nice,
                },
                cancellationToken
            )
            .ConfigureAwait(false);

        if (!response.Ok)
        {
            throw new ProviderException(
                ProviderErrorKind.Underlying,
                response.Error ?? "helper refused the request"
            );
        }
    }

    private static async Task<HelperResponse> SendAsync(
        HelperRequest request,
        CancellationToken cancellationToken
    )
    {
        if (!IsAvailable)
        {
            throw new ProviderException(
                ProviderErrorKind.HelperUnavailable,
                "the privileged helper is not running"
            );
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);

        using var socket = new Socket(
            AddressFamily.Unix,
            SocketType.Stream,
            ProtocolType.Unspecified
        );

        try
        {
            await socket
                .ConnectAsync(
                    new UnixDomainSocketEndPoint(HelperConstants.SocketPath),
                    timeout.Token
                )
                .ConfigureAwait(false);

            await using var stream = new NetworkStream(socket, ownsSocket: false);
            await using var writer = new StreamWriter(
                stream,
                new UTF8Encoding(false),
                1024,
                leaveOpen: true
            )
            {
                AutoFlush = true,
            };
            using var reader = new StreamReader(
                stream,
                Encoding.UTF8,
                false,
                1024,
                leaveOpen: true
            );

            var json = JsonSerializer.Serialize(request, HelperJsonContext.Default.HelperRequest);
            await writer.WriteLineAsync(json.AsMemory(), timeout.Token).ConfigureAwait(false);

            var line = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false);
            if (line is null)
            {
                throw new ProviderException(
                    ProviderErrorKind.HelperUnavailable,
                    "the helper closed the connection"
                );
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
                    ? AccessDeniedMessage()
                    : $"could not reach the helper: {e.Message}"
            );
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ProviderException(
                ProviderErrorKind.HelperUnavailable,
                "the helper did not respond in time"
            );
        }
    }

    // Computed once: every branch's remedy is a new login session or a helper
    // restart, both of which replace this process, so the verdict cannot
    // usefully change within it — and the sweeps that hit EACCES do so once
    // per process per tick, which is no place for repeated file parsing.
    private static string? accessDeniedMessage;

    /// <summary>
    /// Explains an EACCES from the socket. The kernel judged the credentials
    /// this process is actually running with, so the diagnosis starts from
    /// those (via <c>/proc/self/status</c>) rather than guessing from the
    /// group database: holding the gid yet being refused points at the helper,
    /// membership on disk without the gid means the grant postdates this
    /// session, and neither means membership is genuinely absent — or managed
    /// somewhere NSS-only that the direct file read cannot see.
    /// </summary>
    private static string AccessDeniedMessage()
    {
        return accessDeniedMessage ??= BuildAccessDeniedMessage();
    }

    private static string BuildAccessDeniedMessage()
    {
        var generic =
            "not permitted to use the helper — membership of the "
            + $"'{HelperConstants.AccessGroup}' group is required";

        var group = GroupDatabase.ReadGroup(HelperConstants.AccessGroup);
        if (group is null)
        {
            return generic;
        }

        if (CurrentGids().Contains(group.Value.Gid))
        {
            return $"this session already holds the '{HelperConstants.AccessGroup}' group "
                + "yet the helper socket refused it — the helper may predate the group; "
                + "try 'systemctl restart procexp-helper'";
        }

        var user = Environment.UserName;
        var isMemberOnDisk =
            (user.Length > 0 && group.Value.Members.Contains(user, StringComparer.Ordinal))
            || GroupDatabase.ReadPrimaryGid(user) == group.Value.Gid;

        return isMemberOnDisk
            ? $"the '{HelperConstants.AccessGroup}' group was granted after this "
                + "session began — log out and back in to use the helper"
            : generic;
    }

    /// <summary>
    /// Every gid this process holds: real, effective, saved and fs from the
    /// <c>Gid:</c> line, plus the supplementary list from <c>Groups:</c>.
    /// </summary>
    private static uint[] CurrentGids()
    {
        try
        {
            var gids = new List<uint>();
            foreach (var line in File.ReadLines("/proc/self/status"))
            {
                if (
                    line.StartsWith("Gid:", StringComparison.Ordinal)
                    || line.StartsWith("Groups:", StringComparison.Ordinal)
                )
                {
                    foreach (
                        var field in line[(line.IndexOf(':') + 1)..]
                            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                    )
                    {
                        if (uint.TryParse(field, out var gid))
                        {
                            gids.Add(gid);
                        }
                    }
                }
            }

            return [.. gids];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
