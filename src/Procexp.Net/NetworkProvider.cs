using System.Collections.Frozen;
using System.Globalization;
using System.Net;
using Procexp.Model;

namespace Procexp.Net;

/// <summary>
/// Per-process networking, from the kernel socket tables joined to process file
/// descriptors.
/// </summary>
/// <remarks>
/// The Linux counterpart of the macOS provider built on
/// <c>proc_pidfdinfo(PROC_PIDFDSOCKETINFO)</c>, which hands back a socket
/// description directly from the descriptor. Linux splits the same information in
/// two: <c>/proc/PID/fd</c> says which socket inodes a process holds, and
/// <c>/proc/net/*</c> says what each inode is. Joining them is this class's job.
/// </remarks>
public sealed class NetworkProvider : INetworkProvider
{
    private readonly Dictionary<string, string> _reverseDnsCache = new(StringComparer.Ordinal);
    private readonly Lock _dnsGate = new();

    public ValueTask<IReadOnlyList<SocketInfo>> SocketsAsync(
        ProcessId id, CancellationToken cancellationToken = default)
    {
        var inodes = ReadSocketInodes(id.Pid);
        if (inodes.Count == 0)
        {
            return ValueTask.FromResult<IReadOnlyList<SocketInfo>>([]);
        }

        var tables = ProcNetTables.ReadAll();
        var result = new List<SocketInfo>(inodes.Count);

        foreach (var (fd, inode) in inodes)
        {
            if (tables.TryGetValue(inode, out var socket))
            {
                result.Add(socket with { Fd = fd });
            }
        }

        result.Sort(static (a, b) => a.Fd.CompareTo(b.Fd));
        return ValueTask.FromResult<IReadOnlyList<SocketInfo>>(result);
    }

    /// <summary>
    /// Which socket inodes a process holds, keyed by the descriptor number.
    /// </summary>
    /// <remarks>
    /// Socket descriptors are symlinks whose target reads <c>socket:[12345]</c>.
    /// The link cannot be resolved as a path — there is no such file — so the
    /// target text is parsed directly.
    /// </remarks>
    private static List<(int Fd, ulong Inode)> ReadSocketInodes(int pid)
    {
        var result = new List<(int, ulong)>();

        string[] entries;
        try
        {
            entries = Directory.GetFiles($"/proc/{pid}/fd");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return result;
        }

        foreach (var entry in entries)
        {
            if (!int.TryParse(Path.GetFileName(entry), out var fd))
            {
                continue;
            }

            string? target;
            try
            {
                target = new FileInfo(entry).LinkTarget;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            if (target is null || !target.StartsWith("socket:[", StringComparison.Ordinal))
            {
                continue;
            }

            var close = target.IndexOf(']');
            if (close > 8 &&
                ulong.TryParse(target.AsSpan(8, close - 8), NumberStyles.None,
                    CultureInfo.InvariantCulture, out var inode))
            {
                result.Add((fd, inode));
            }
        }

        return result;
    }

    /// <summary>
    /// Per-process network byte rates.
    /// </summary>
    /// <remarks>
    /// Always empty, and the Network column is marked unsupported accordingly.
    ///
    /// Linux exposes no per-process byte counter. <c>/proc/PID/net/dev</c> looks
    /// like one but reports the process's <em>network namespace</em> totals, so
    /// every process outside a container reports identical host-wide figures —
    /// which is worse than showing nothing, because it looks plausible.
    ///
    /// Two real routes exist, both deferred:
    ///
    /// 1. netlink <c>sock_diag</c> with <c>INET_DIAG_INFO</c> returns per-socket
    ///    <c>bytes_acked</c> and <c>bytes_received</c>, which could be summed per
    ///    process through the inode join above and delta'd into a rate. It covers
    ///    TCP only, and the <c>tcp_info</c> struct it returns has grown across
    ///    kernel releases, so the offsets must be derived from the reported length
    ///    rather than hard-coded.
    ///
    /// 2. Packet capture attributed by connection, as nethogs does, which needs
    ///    CAP_NET_RAW and belongs in the privileged helper.
    ///
    /// The macOS build leaves this column unsupported for the same underlying
    /// reason, so this is parity rather than a regression.
    /// </remarks>
    public ValueTask<IReadOnlyDictionary<ProcessId, ulong>> NetworkRatesAsync(
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<IReadOnlyDictionary<ProcessId, ulong>>(
            FrozenDictionary<ProcessId, ulong>.Empty);

    /// <summary>
    /// Resolve a remote address to a host name for the TCP/IP tab.
    /// </summary>
    /// <remarks>
    /// Cached, because the tab re-renders on every refresh and an unresolvable
    /// address otherwise costs a full DNS timeout each time. Failures are cached
    /// as the address itself so they are not retried.
    /// </remarks>
    public async ValueTask<string> ResolveHostNameAsync(string address, CancellationToken cancellationToken = default)
    {
        lock (_dnsGate)
        {
            if (_reverseDnsCache.TryGetValue(address, out var cached))
            {
                return cached;
            }
        }

        var resolved = address;
        try
        {
            if (IPAddress.TryParse(address, out var ip) && !IPAddress.IsLoopback(ip))
            {
                var entry = await Dns.GetHostEntryAsync(ip.ToString(), cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(entry.HostName))
                {
                    resolved = entry.HostName;
                }
            }
        }
        catch (Exception e) when (e is System.Net.Sockets.SocketException or ArgumentException or OperationCanceledException)
        {
            // Unresolvable; cache the address so we do not pay the timeout twice.
        }

        lock (_dnsGate)
        {
            _reverseDnsCache[address] = resolved;
        }

        return resolved;
    }
}
