using System.Globalization;

namespace Procexp.Sampling;

/// <summary>
/// Process-wide constants that a sweep needs but that never change: boot time,
/// page size, our own uid, and the uid-to-name map.
/// </summary>
internal sealed class SystemContext
{
    private readonly Dictionary<uint, string> _userNames;

    private SystemContext(DateTimeOffset bootTime, uint ownUid, Dictionary<uint, string> userNames)
    {
        BootTime = bootTime;
        OwnUid = ownUid;
        _userNames = userNames;
    }

    /// <summary>Wall-clock time the system booted, used to date process starts.</summary>
    internal DateTimeOffset BootTime { get; }

    internal uint OwnUid { get; }

    internal static int PageSize { get; } = Environment.SystemPageSize;

    internal static SystemContext Create() =>
        new(ReadBootTime(), NativeMethods.GetUid(), ReadUserNames());

    /// <summary>
    /// Resolve a uid to a login name.
    /// </summary>
    /// <remarks>
    /// Reads <c>/etc/passwd</c> directly rather than calling getpwuid, which
    /// keeps the sweep free of blocking NSS lookups. The trade-off is that users
    /// defined only in a directory service (LDAP, SSSD, AD) will not resolve and
    /// fall back to the numeric uid.
    /// </remarks>
    internal string? UserName(uint uid) => _userNames.GetValueOrDefault(uid);

    private static DateTimeOffset ReadBootTime()
    {
        var buffer = ProcFile.RentBuffer(8192);
        try
        {
            if (ProcFile.TryRead("/proc/stat", ref buffer, out var length))
            {
                var value = ProcFile.FindKeyedValue(buffer.AsSpan(0, length), "btime"u8);
                if (!value.IsEmpty)
                {
                    return DateTimeOffset.FromUnixTimeSeconds((long)ProcFile.ParseUInt64(value));
                }
            }
        }
        finally
        {
            ProcFile.ReturnBuffer(buffer);
        }

        // Fall back to deriving it from uptime, which is less precise but never
        // leaves every process stamped with the epoch.
        return DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);
    }

    private static Dictionary<uint, string> ReadUserNames()
    {
        var result = new Dictionary<uint, string>();

        try
        {
            foreach (var line in File.ReadLines("/etc/passwd"))
            {
                // name:password:uid:gid:gecos:home:shell
                var first = line.IndexOf(':');
                if (first <= 0)
                {
                    continue;
                }

                var second = line.IndexOf(':', first + 1);
                if (second < 0)
                {
                    continue;
                }

                var third = line.IndexOf(':', second + 1);
                if (third < 0)
                {
                    continue;
                }

                if (uint.TryParse(line.AsSpan(second + 1, third - second - 1),
                        NumberStyles.None, CultureInfo.InvariantCulture, out var uid))
                {
                    result.TryAdd(uid, line[..first]);
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A system without a readable /etc/passwd still shows numeric uids.
        }

        return result;
    }
}
