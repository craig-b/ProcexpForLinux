using System.Globalization;
using Procexp.Model;

namespace Procexp.Sampling;

/// <summary>
/// The security posture of a process: its four uids and gids, supplementary
/// groups, capability sets, and the switches that constrain what it may become.
/// </summary>
/// <remarks>
/// The Linux answer to the macOS Security tab, and a good deal more detailed —
/// entitlements are a bundle's static claim, whereas these are the kernel's
/// live view of what this process can actually do right now.
///
/// Everything here is world-readable in <c>/proc/PID/status</c>, so no
/// privilege is needed for any process on the machine.
/// </remarks>
public static class ProcSecurity
{
    /// <summary>A uid or gid quadruple: real, effective, saved, filesystem.</summary>
    public sealed record IdSet(uint Real, uint Effective, uint Saved, uint FileSystem);

    public sealed record SecurityInfo
    {
        public IdSet? Uids { get; init; }
        public IdSet? Gids { get; init; }
        public IReadOnlyList<uint> Groups { get; init; } = [];

        /// <summary>Effective, permitted, inheritable, bounding, ambient.</summary>
        public ulong? CapabilitiesEffective { get; init; }
        public ulong? CapabilitiesPermitted { get; init; }
        public ulong? CapabilitiesInheritable { get; init; }
        public ulong? CapabilitiesBounding { get; init; }
        public ulong? CapabilitiesAmbient { get; init; }

        public bool? NoNewPrivileges { get; init; }
        public int? SeccompMode { get; init; }
        public int? SeccompFilters { get; init; }
    }

    public static SecurityInfo Read(int pid)
    {
        var info = new SecurityInfo();

        try
        {
            foreach (var line in File.ReadLines($"/proc/{pid}/status"))
            {
                var colon = line.IndexOf(':');
                if (colon < 0)
                {
                    continue;
                }

                var key = line[..colon];
                var value = line[(colon + 1)..].Trim();

                info = key switch
                {
                    "Uid" => info with { Uids = ParseIdSet(value) },
                    "Gid" => info with { Gids = ParseIdSet(value) },
                    "Groups" => info with { Groups = ParseGroups(value) },
                    "CapEff" => info with { CapabilitiesEffective = ParseHex(value) },
                    "CapPrm" => info with { CapabilitiesPermitted = ParseHex(value) },
                    "CapInh" => info with { CapabilitiesInheritable = ParseHex(value) },
                    "CapBnd" => info with { CapabilitiesBounding = ParseHex(value) },
                    "CapAmb" => info with { CapabilitiesAmbient = ParseHex(value) },
                    "NoNewPrivs" => info with { NoNewPrivileges = value == "1" },
                    "Seccomp" => info with { SeccompMode = ParseInt(value) },
                    "Seccomp_filters" => info with { SeccompFilters = ParseInt(value) },
                    _ => info,
                };
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A process that exited mid-read reports what was gathered.
        }

        return info;
    }

    /// <summary>Human-readable seccomp mode, as the kernel numbers them.</summary>
    public static string DescribeSeccomp(int? mode, int? filters) =>
        mode switch
        {
            0 => "disabled",
            1 => "strict",
            2 => filters is > 0 ? $"filtered ({filters} filters)" : "filtered",
            _ => "unknown",
        };

    /// <summary>
    /// Names for the capability bits present in a mask, most significant
    /// meaning first. Unknown bits — a kernel newer than this table — are
    /// reported by number rather than dropped.
    /// </summary>
    public static IReadOnlyList<string> DescribeCapabilities(ulong mask)
    {
        if (mask == 0)
        {
            return [];
        }

        var names = new List<string>();
        for (var bit = 0; bit < 64; bit++)
        {
            if ((mask & (1UL << bit)) == 0)
            {
                continue;
            }

            names.Add(
                bit < CapabilityNames.Length
                    ? CapabilityNames[bit]
                    : $"CAP_{bit.ToString(CultureInfo.InvariantCulture)}"
            );
        }

        return names;
    }

    /// <summary>Whether a mask is the full set a container-less root holds.</summary>
    public static bool IsFullSet(ulong mask, ulong bounding) => mask != 0 && mask == bounding;

    private static IdSet? ParseIdSet(string value)
    {
        var fields = value.Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
        );

        if (fields.Length < 4)
        {
            return null;
        }

        return
            uint.TryParse(fields[0], out var real)
            && uint.TryParse(fields[1], out var effective)
            && uint.TryParse(fields[2], out var saved)
            && uint.TryParse(fields[3], out var fs)
            ? new IdSet(real, effective, saved, fs)
            : null;
    }

    private static IReadOnlyList<uint> ParseGroups(string value) =>
        [
            .. value
                .Split(
                    (char[]?)null,
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
                )
                .Select(f => uint.TryParse(f, out var g) ? g : (uint?)null)
                .Where(g => g is not null)
                .Select(g => g!.Value),
        ];

    private static ulong? ParseHex(string value) =>
        ulong.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)
            ? v
            : null;

    private static int? ParseInt(string value) =>
        int.TryParse(value, CultureInfo.InvariantCulture, out var v) ? v : null;

    /// <summary>
    /// Capability names by bit, from <c>include/uapi/linux/capability.h</c>.
    /// </summary>
    private static readonly string[] CapabilityNames =
    [
        "CAP_CHOWN",
        "CAP_DAC_OVERRIDE",
        "CAP_DAC_READ_SEARCH",
        "CAP_FOWNER",
        "CAP_FSETID",
        "CAP_KILL",
        "CAP_SETGID",
        "CAP_SETUID",
        "CAP_SETPCAP",
        "CAP_LINUX_IMMUTABLE",
        "CAP_NET_BIND_SERVICE",
        "CAP_NET_BROADCAST",
        "CAP_NET_ADMIN",
        "CAP_NET_RAW",
        "CAP_IPC_LOCK",
        "CAP_IPC_OWNER",
        "CAP_SYS_MODULE",
        "CAP_SYS_RAWIO",
        "CAP_SYS_CHROOT",
        "CAP_SYS_PTRACE",
        "CAP_SYS_PACCT",
        "CAP_SYS_ADMIN",
        "CAP_SYS_BOOT",
        "CAP_SYS_NICE",
        "CAP_SYS_RESOURCE",
        "CAP_SYS_TIME",
        "CAP_SYS_TTY_CONFIG",
        "CAP_MKNOD",
        "CAP_LEASE",
        "CAP_AUDIT_WRITE",
        "CAP_AUDIT_CONTROL",
        "CAP_SETFCAP",
        "CAP_MAC_OVERRIDE",
        "CAP_MAC_ADMIN",
        "CAP_SYSLOG",
        "CAP_WAKE_ALARM",
        "CAP_BLOCK_SUSPEND",
        "CAP_AUDIT_READ",
        "CAP_PERFMON",
        "CAP_BPF",
        "CAP_CHECKPOINT_RESTORE",
    ];
}
