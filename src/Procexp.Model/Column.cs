using System.Globalization;

namespace Procexp.Model;

/// <summary>
/// A sortable key derived from a column value, comparable within a single column.
/// </summary>
/// <remarks>
/// <see cref="None"/> means "no value for this process" and always sorts last,
/// regardless of direction, so restricted or not-applicable rows collect at the
/// bottom rather than interleaving with real zeros.
/// </remarks>
public readonly struct SortKey : IComparable<SortKey>
{
    private enum Kind : byte
    {
        None,
        Number,
        Text,
    }

    private readonly Kind _kind;
    private readonly double _number;
    private readonly string? _text;

    private SortKey(Kind kind, double number, string? text)
    {
        _kind = kind;
        _number = number;
        _text = text;
    }

    public static readonly SortKey None = new(Kind.None, 0, null);

    public static SortKey Number(double value) => new(Kind.Number, value, null);

    public static SortKey Text(string value) => new(Kind.Text, 0, value);

    /// <summary>
    /// Compare with a sort direction: real values flip under
    /// <paramref name="descending"/>, while <see cref="None"/> stays last
    /// either way.
    /// </summary>
    public int CompareTo(SortKey other, bool descending)
    {
        var result = CompareTo(other);
        return descending && _kind != Kind.None && other._kind != Kind.None ? -result : result;
    }

    public int CompareTo(SortKey other)
    {
        if (_kind == Kind.None)
        {
            return other._kind == Kind.None ? 0 : 1;
        }

        if (other._kind == Kind.None)
        {
            return -1;
        }

        if (_kind != other._kind)
        {
            return 0;
        }

        return _kind == Kind.Number
            ? _number.CompareTo(other._number)
            : string.Compare(_text, other._text, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>The user-selectable columns of the main process list.</summary>
public enum Column
{
    Name,
    Pid,
    Ppid,
    Cpu,
    CpuTime,
    PrivateBytes,
    WorkingSet,
    VirtualSize,
    SharedBytes,
    SwapBytes,
    Threads,
    Handles,
    Description,
    Company,
    Version,
    Path,
    CommandLine,
    User,
    Session,
    StartTime,
    Priority,
    Nice,
    IoRead,
    IoWrite,
    RunningThreads,
    MinorFaults,
    MajorFaults,
    VoluntaryContextSwitches,
    InvoluntaryContextSwitches,
    SchedulingPolicy,
    State,
    KernelFlags,
    LastCpu,
    OomScore,
    OomScoreAdj,
    CgroupPath,
    SystemdUnit,
    SecurityLabel,
    Network,
    Gpu,
    GpuMemory,
    Integrity,
    Provenance,
    VirusTotal,
    Autostart,
}

/// <summary>
/// Titles, widths, formatting and sort keys for <see cref="Column"/>. Pure
/// functions, so every renderer stays consistent and the behaviour is testable
/// without a UI.
/// </summary>
public static class Columns
{
    public static readonly IReadOnlyList<Column> All = Enum.GetValues<Column>();

    public static string Title(Column c) =>
        c switch
        {
            Column.Name => "Process",
            Column.Pid => "PID",
            Column.Ppid => "Parent PID",
            Column.Cpu => "CPU",
            Column.CpuTime => "CPU Time",
            Column.PrivateBytes => "Private Bytes",
            Column.WorkingSet => "Working Set",
            Column.VirtualSize => "Virtual Size",
            Column.SharedBytes => "Shared Bytes",
            Column.SwapBytes => "Swap",
            Column.Threads => "Threads",
            Column.Handles => "Handles",
            Column.Description => "Description",
            Column.Company => "Company Name",
            Column.Version => "Version",
            Column.Path => "Path",
            Column.CommandLine => "Command Line",
            Column.User => "User Name",
            Column.Session => "Session",
            Column.StartTime => "Start Time",
            Column.Priority => "Priority",
            Column.Nice => "Nice",
            Column.IoRead => "I/O Read Bytes",
            Column.IoWrite => "I/O Write Bytes",
            Column.RunningThreads => "Running Threads",
            Column.MinorFaults => "Minor Faults",
            Column.MajorFaults => "Major Faults",
            Column.VoluntaryContextSwitches => "Ctx Switches",
            Column.InvoluntaryContextSwitches => "Invol Ctx Switches",
            Column.SchedulingPolicy => "Policy",
            Column.State => "State",
            Column.KernelFlags => "Kernel Flags",
            Column.LastCpu => "CPU #",
            Column.OomScore => "OOM Score",
            Column.OomScoreAdj => "OOM Adj",
            Column.CgroupPath => "Cgroup",
            Column.SystemdUnit => "Unit",
            Column.SecurityLabel => "Security Label",
            Column.Network => "Network",
            Column.Gpu => "GPU",
            Column.GpuMemory => "GPU Memory",
            Column.Integrity => "Integrity",
            Column.Provenance => "Verified Signer",
            Column.VirusTotal => "VirusTotal",
            Column.Autostart => "Autostart Location",
            _ => "",
        };

    public static double DefaultWidth(Column c) =>
        c switch
        {
            Column.Name => 260,

            // Wider than the macOS defaults of 58 and 78. Darwin caps pids at 99999,
            // but Linux pid_max defaults to 4194304 on 64-bit — seven digits — so the
            // inherited widths truncate real pids on a long-running machine.
            Column.Pid => 78,
            Column.Ppid => 92,
            Column.Cpu => 54,
            Column.CpuTime => 88,
            Column.PrivateBytes
            or Column.WorkingSet
            or Column.VirtualSize
            or Column.SharedBytes
            or Column.SwapBytes => 96,
            Column.Threads => 62,
            Column.Handles => 68,
            Column.Description => 220,
            Column.Company => 170,
            Column.Version => 118,
            Column.Path or Column.CommandLine => 420,
            Column.User => 130,
            Column.Session => 76,
            Column.StartTime => 142,
            Column.Priority => 70,
            Column.Nice => 48,
            Column.IoRead or Column.IoWrite => 110,
            Column.RunningThreads => 108,
            Column.MinorFaults or Column.MajorFaults => 96,
            Column.VoluntaryContextSwitches => 106,
            Column.InvoluntaryContextSwitches => 132,
            Column.SchedulingPolicy => 82,
            Column.State => 70,
            Column.KernelFlags => 92,
            Column.LastCpu => 58,
            Column.OomScore => 84,
            Column.OomScoreAdj => 76,
            Column.CgroupPath => 300,
            Column.SystemdUnit => 200,
            Column.SecurityLabel => 200,
            Column.Network => 96,
            Column.Gpu => 58,
            Column.GpuMemory => 96,
            Column.Integrity => 110,
            Column.Provenance => 240,
            Column.VirusTotal => 82,
            Column.Autostart => 260,
            _ => 100,
        };

    public static bool IsRightAligned(Column c) =>
        c switch
        {
            Column.Name
            or Column.Description
            or Column.Company
            or Column.Version
            or Column.Path
            or Column.CommandLine
            or Column.User
            or Column.StartTime
            or Column.Provenance
            or Column.Autostart
            or Column.Integrity
            or Column.CgroupPath
            or Column.SystemdUnit
            or Column.SecurityLabel
            or Column.State
            or Column.SchedulingPolicy => false,
            _ => true,
        };

    /// <summary>
    /// Columns backed by a real Linux data source. Unsupported columns stay
    /// decodable so old saved settings still load, but are removed from menus and
    /// active column sets.
    /// </summary>
    /// <remarks>
    /// Per-process GPU <em>is</em> supported here, unlike the macOS build, since
    /// DRM fdinfo reports per-process engine busy time.
    ///
    /// Network is not. Linux exposes no per-process byte counter:
    /// <c>/proc/PID/net/dev</c> reports network-namespace totals, so every
    /// process outside a container would show identical host-wide figures.
    /// Sockets themselves enumerate fine and drive the TCP/IP tab — it is only
    /// the byte rate that has no unprivileged source. See NetworkProvider for the
    /// two routes that could supply it.
    ///
    /// GPU memory stays unsupported because only some DRM drivers report it.
    /// </remarks>
    public static bool IsSupported(Column c) => c is not (Column.GpuMemory or Column.Network);

    public static IEnumerable<Column> Supported => All.Where(IsSupported);

    /// <summary>Columns that cannot be removed — the tree needs them to render.</summary>
    public static readonly IReadOnlyList<Column> Pinned = [Column.Name, Column.Pid];

    /// <summary>A sensible default layout matching Process Explorer's defaults.</summary>
    public static readonly IReadOnlyList<Column> Default =
    [
        Column.Name,
        Column.Pid,
        Column.Cpu,
        Column.PrivateBytes,
        Column.WorkingSet,
        Column.Description,
        Column.Company,
        Column.Provenance,
    ];

    /// <summary>The display string for a column and process.</summary>
    public static string Format(Column c, ProcessRecord p) =>
        c switch
        {
            Column.Name => p.Name,
            Column.Pid => p.Id.Pid.ToString(CultureInfo.InvariantCulture),
            Column.Ppid => p.Parent is { } parent
                ? parent.Pid.ToString(CultureInfo.InvariantCulture)
                : "",
            Column.Cpu => p.CpuPercent < 0.01
                ? ""
                : p.CpuPercent.ToString("F2", CultureInfo.InvariantCulture),
            Column.CpuTime => ValueFormat.Duration(p.CpuTime),
            // Prefer PSS when something has gone to the trouble of measuring it,
            // otherwise the free anonymous figure.
            Column.PrivateBytes => ValueFormat.Bytes(p.ProportionalSetSize ?? p.PrivateSize),
            Column.WorkingSet => ValueFormat.Bytes(p.ResidentSize),
            Column.VirtualSize => ValueFormat.Bytes(p.VirtualSize),
            Column.SharedBytes => ValueFormat.Bytes(p.SharedSize),
            Column.SwapBytes => ValueFormat.Bytes(p.SwapSize),
            Column.Threads => p.ThreadCount.ToString(CultureInfo.InvariantCulture),
            Column.Handles => ValueFormat.Integer(p.FileDescriptorCount),
            Column.Description => p.Description ?? "",
            Column.Company => p.Company ?? "",
            Column.Version => p.Version ?? "",
            Column.Path => p.ExecutablePath ?? "",
            Column.CommandLine => p.CommandLine ?? "",
            Column.User => p.UserName ?? p.Uid.ToString(CultureInfo.InvariantCulture),
            Column.Session => p.SessionTty ?? "",
            Column.StartTime => ValueFormat.DateTime(p.StartTime),
            Column.Priority => p.Priority.ToString(CultureInfo.InvariantCulture),
            Column.Nice => p.Nice.ToString(CultureInfo.InvariantCulture),
            Column.IoRead => ValueFormat.Bytes(p.DiskBytesRead),
            Column.IoWrite => ValueFormat.Bytes(p.DiskBytesWritten),
            Column.RunningThreads => ValueFormat.Integer(p.RunningThreadCount),
            Column.MinorFaults => ValueFormat.Integer(p.MinorFaults),
            Column.MajorFaults => ValueFormat.Integer(p.MajorFaults),
            Column.VoluntaryContextSwitches => ValueFormat.Integer(p.VoluntaryContextSwitches),
            Column.InvoluntaryContextSwitches => ValueFormat.Integer(p.InvoluntaryContextSwitches),
            Column.SchedulingPolicy => ValueFormat.SchedulingPolicy(p.SchedulingPolicy),
            Column.State => ValueFormat.ProcessState(p.State),
            Column.KernelFlags => p.KernelFlags is { } f ? $"0x{f:X8}" : "",
            Column.LastCpu => ValueFormat.Integer(p.LastCpu),
            Column.OomScore => ValueFormat.Integer(p.OomScore),
            Column.OomScoreAdj => ValueFormat.Integer(p.OomScoreAdj),
            Column.CgroupPath => p.CgroupPath ?? "",
            Column.SystemdUnit => p.SystemdUnit ?? "",
            Column.SecurityLabel => p.SecurityLabel ?? "",
            Column.Network => p.NetworkBytesPerSec is { } n ? ValueFormat.Bytes(n) + "/s" : "",
            Column.Gpu => p.GpuPercent is { } g
                ? g.ToString("F1", CultureInfo.InvariantCulture)
                : "",
            Column.GpuMemory => ValueFormat.Bytes(p.GpuMemoryBytes),
            Column.Integrity => IntegrityLabel(p),
            Column.Provenance => p.Provenance?.DisplayName ?? "",
            Column.VirusTotal => p.Provenance?.VirusTotal is { } vt
                ? $"{vt.Positives}/{vt.Total}"
                : "",
            Column.Autostart => p.AutostartLocation ?? "",
            _ => "",
        };

    /// <summary>A comparable sort key for a column and process.</summary>
    public static SortKey SortValue(Column c, ProcessRecord p) =>
        c switch
        {
            Column.Name => SortKey.Text(p.Name),
            Column.Pid => SortKey.Number(p.Id.Pid),
            Column.Ppid => SortKey.Number(p.Parent?.Pid ?? -1),
            Column.Cpu => SortKey.Number(p.CpuPercent),
            Column.CpuTime => SortKey.Number(p.CpuTime),
            Column.PrivateBytes => Nullable(p.ProportionalSetSize ?? p.PrivateSize),
            Column.WorkingSet => SortKey.Number(p.ResidentSize),
            Column.VirtualSize => SortKey.Number(p.VirtualSize),
            Column.SharedBytes => Nullable(p.SharedSize),
            Column.SwapBytes => Nullable(p.SwapSize),
            Column.Threads => SortKey.Number(p.ThreadCount),
            Column.Handles => Nullable(p.FileDescriptorCount),
            Column.Description => SortKey.Text(p.Description ?? ""),
            Column.Company => SortKey.Text(p.Company ?? ""),
            Column.Version => SortKey.Text(p.Version ?? ""),
            Column.Path => SortKey.Text(p.ExecutablePath ?? ""),
            Column.CommandLine => SortKey.Text(p.CommandLine ?? ""),
            Column.User => SortKey.Text(p.UserName ?? p.Uid.ToString(CultureInfo.InvariantCulture)),
            Column.Session => SortKey.Text(p.SessionTty ?? ""),
            Column.StartTime => SortKey.Number(p.StartTime.ToUnixTimeMilliseconds()),
            Column.Priority => SortKey.Number(p.Priority),
            Column.Nice => SortKey.Number(p.Nice),
            Column.IoRead => Nullable(p.DiskBytesRead),
            Column.IoWrite => Nullable(p.DiskBytesWritten),
            Column.RunningThreads => Nullable(p.RunningThreadCount),
            Column.MinorFaults => Nullable(p.MinorFaults),
            Column.MajorFaults => Nullable(p.MajorFaults),
            Column.VoluntaryContextSwitches => Nullable(p.VoluntaryContextSwitches),
            Column.InvoluntaryContextSwitches => Nullable(p.InvoluntaryContextSwitches),
            Column.SchedulingPolicy => Nullable(p.SchedulingPolicy),
            Column.State => SortKey.Text(ValueFormat.ProcessState(p.State)),
            Column.KernelFlags => Nullable(p.KernelFlags),
            Column.LastCpu => Nullable(p.LastCpu),
            Column.OomScore => Nullable(p.OomScore),
            Column.OomScoreAdj => Nullable(p.OomScoreAdj),
            Column.CgroupPath => SortKey.Text(p.CgroupPath ?? ""),
            Column.SystemdUnit => SortKey.Text(p.SystemdUnit ?? ""),
            Column.SecurityLabel => SortKey.Text(p.SecurityLabel ?? ""),
            Column.Network => Nullable(p.NetworkBytesPerSec),
            Column.Gpu => p.GpuPercent is { } g ? SortKey.Number(g) : SortKey.None,
            Column.GpuMemory => Nullable(p.GpuMemoryBytes),
            Column.Integrity => SortKey.Text(IntegrityLabel(p)),
            Column.Provenance => SortKey.Text(p.Provenance?.DisplayName ?? ""),
            Column.VirusTotal => p.Provenance?.VirusTotal is { } vt
                ? SortKey.Number(vt.Positives)
                : SortKey.None,
            Column.Autostart => SortKey.Text(p.AutostartLocation ?? ""),
            _ => SortKey.None,
        };

    private static SortKey Nullable(ulong? v) => v is { } x ? SortKey.Number(x) : SortKey.None;

    private static SortKey Nullable(uint? v) => v is { } x ? SortKey.Number(x) : SortKey.None;

    private static SortKey Nullable(int? v) => v is { } x ? SortKey.Number(x) : SortKey.None;

    private static string IntegrityLabel(ProcessRecord p)
    {
        if (p.Flags.HasFlag(ProcessFlags.Sandboxed))
        {
            return p.ImageKind switch
            {
                ImageKind.Flatpak => "Flatpak",
                ImageKind.Snap => "Snap",
                ImageKind.Container => "Container",
                _ => "Confined",
            };
        }

        return p.Flags.HasFlag(ProcessFlags.PackagedBinary) ? "Packaged" : "";
    }
}

/// <summary>
/// Pure formatting helpers used by column rendering. Invariant-culture
/// throughout so output is stable across locales and testable.
/// </summary>
public static class ValueFormat
{
    private static readonly string[] Units = ["B", "K", "M", "G", "T", "P"];

    /// <summary>
    /// Format a byte count. Zero and null both render empty, matching the macOS
    /// behaviour of leaving uninteresting cells blank rather than showing "0 B".
    /// </summary>
    public static string Bytes(ulong value)
    {
        if (value == 0)
        {
            return "";
        }

        if (value < 1024)
        {
            return $"{value} B";
        }

        double v = value;
        var i = 0;
        while (v >= 1024 && i < Units.Length - 1)
        {
            v /= 1024;
            i++;
        }

        return string.Create(CultureInfo.InvariantCulture, $"{v:F1} {Units[i]}");
    }

    public static string Bytes(ulong? value) => value is { } v ? Bytes(v) : "";

    /// <summary>Format a nanosecond duration as <c>h:mm:ss.cc</c>.</summary>
    public static string Duration(ulong nanos)
    {
        var totalSeconds = nanos / 1_000_000_000.0;
        var whole = (long)totalSeconds;
        var h = whole / 3600;
        var m = whole % 3600 / 60;
        var s = whole % 60;
        var cs = (long)((totalSeconds - whole) * 100);
        return string.Create(CultureInfo.InvariantCulture, $"{h}:{m:D2}:{s:D2}.{cs:D2}");
    }

    public static string DateTime(DateTimeOffset value) =>
        value == DateTimeOffset.MinValue
            ? ""
            : value.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    public static string Integer(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "";

    public static string Integer(uint? value) =>
        value?.ToString(CultureInfo.InvariantCulture) ?? "";

    public static string Integer(ulong? value) =>
        value?.ToString(CultureInfo.InvariantCulture) ?? "";

    /// <summary>Decode the <c>/proc/PID/stat</c> state character.</summary>
    public static string ProcessState(char state) =>
        state switch
        {
            'R' => "Running",
            'S' => "Sleeping",
            'D' => "Disk Sleep",
            'Z' => "Zombie",
            'T' => "Stopped",
            't' => "Tracing Stop",
            'X' or 'x' => "Dead",
            'K' => "Wakekill",
            'W' => "Waking",
            'P' => "Parked",
            'I' => "Idle",
            '\0' => "",
            _ => state.ToString(),
        };

    /// <summary>Decode a <c>sched_getscheduler</c> policy number.</summary>
    public static string SchedulingPolicy(int? policy) =>
        policy switch
        {
            0 => "Normal",
            1 => "FIFO",
            2 => "RR",
            3 => "Batch",
            5 => "Idle",
            6 => "Deadline",
            null => "",
            _ => policy.Value.ToString(CultureInfo.InvariantCulture),
        };
}
