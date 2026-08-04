namespace Procexp.Model;

/// <summary>How the image was packaged and launched.</summary>
public enum ImageKind
{
    Unknown,
    /// <summary>A desktop application with a <c>.desktop</c> entry.</summary>
    Application,
    /// <summary>An ordinary command-line executable.</summary>
    CommandLine,
    /// <summary>A systemd-managed daemon.</summary>
    Daemon,
    /// <summary>A kernel thread — no user-space image.</summary>
    KernelThread,
    /// <summary>Runs inside a Flatpak sandbox.</summary>
    Flatpak,
    /// <summary>Runs inside a Snap confinement.</summary>
    Snap,
    /// <summary>Runs inside a container (Docker, Podman, LXC).</summary>
    Container,
}

/// <summary>
/// One process sampled at one instant. Immutable; snapshots cross thread
/// boundaries freely.
/// </summary>
/// <remarks>
/// Record equality is structural, which is what <see cref="SnapshotDiff"/> relies
/// on to detect changed rows.
///
/// Nullable properties mean "not available", either because the kernel does not
/// expose the value for this process or because reading it needs privilege we do
/// not have. Renderers show an empty cell rather than a zero, so a restricted
/// process is visibly distinct from one that genuinely did no I/O.
/// </remarks>
public sealed record ProcessRecord
{
    // ---- Identity -----------------------------------------------------------

    public required ProcessId Id { get; init; }
    public ProcessId? Parent { get; init; }
    public required string Name { get; init; }
    public string? ExecutablePath { get; init; }

    /// <summary>Freedesktop desktop-entry id, e.g. <c>org.gnome.Nautilus</c>.</summary>
    public string? DesktopEntryId { get; init; }

    /// <summary>Icon theme name or absolute icon path, for row icons.</summary>
    public string? IconName { get; init; }

    public ImageKind ImageKind { get; init; } = ImageKind.Unknown;

    // ---- Ownership ----------------------------------------------------------

    public uint Uid { get; init; }
    public uint Gid { get; init; }
    public string? UserName { get; init; }

    /// <summary>Controlling terminal, e.g. <c>pts/3</c>, or null when detached.</summary>
    public string? SessionTty { get; init; }

    /// <summary>Raw state character from <c>/proc/PID/stat</c>: R, S, D, Z, T, I.</summary>
    public char State { get; init; }

    /// <summary>Kernel per-task flags, field 9 of <c>/proc/PID/stat</c>.</summary>
    public uint? KernelFlags { get; init; }

    public bool HasControllingTty { get; init; }
    public bool IsSessionLeader { get; init; }

    /// <summary>ELF class of the image; null for kernel threads.</summary>
    public bool? Is64Bit { get; init; }

    // ---- Descriptive --------------------------------------------------------

    /// <summary>Human-readable description, from the desktop entry or owning package.</summary>
    public string? Description { get; init; }

    /// <summary>
    /// Vendor. On Linux the closest honest equivalent is the owning distribution
    /// package's packager, so this is populated from the package database.
    /// </summary>
    public string? Company { get; init; }

    /// <summary>Version, from the owning package or the desktop entry.</summary>
    public string? Version { get; init; }

    /// <summary>
    /// Full command line. Unlike macOS this needs no privilege —
    /// <c>/proc/PID/cmdline</c> is world-readable.
    /// </summary>
    public string? CommandLine { get; init; }

    // ---- CPU ----------------------------------------------------------------

    /// <summary>
    /// Instantaneous CPU usage, normalised so 100.0 is one fully-busy core. A
    /// process saturating two cores reads ~200.0, matching both Process Explorer
    /// and top in Irix mode.
    /// </summary>
    public double CpuPercent { get; init; }

    /// <summary>Cumulative user + system CPU time in nanoseconds.</summary>
    public ulong CpuTime { get; init; }

    public ulong UserTime { get; init; }
    public ulong SystemTime { get; init; }

    public int ThreadCount { get; init; }
    public int? RunningThreadCount { get; init; }

    /// <summary>Scheduling policy: SCHED_OTHER, SCHED_FIFO, SCHED_RR, and so on.</summary>
    public int? SchedulingPolicy { get; init; }

    public ulong? VoluntaryContextSwitches { get; init; }
    public ulong? InvoluntaryContextSwitches { get; init; }

    /// <summary>CPU this process last ran on, field 39 of <c>/proc/PID/stat</c>.</summary>
    public int? LastCpu { get; init; }

    // ---- Memory (bytes) -----------------------------------------------------

    /// <summary>Resident set size — the Working Set analog.</summary>
    public ulong ResidentSize { get; init; }

    public ulong VirtualSize { get; init; }

    /// <summary>
    /// Proportional set size from <c>/proc/PID/smaps_rollup</c>, which divides
    /// shared pages by the number of sharers. This is the honest Private Bytes
    /// analog and the closest counterpart to the macOS phys-footprint. Restricted
    /// to the owning uid, so null for other users' processes without the helper.
    /// </summary>
    public ulong? ProportionalSetSize { get; init; }

    public ulong? SharedSize { get; init; }
    public ulong? SwapSize { get; init; }
    public ulong? MinorFaults { get; init; }
    public ulong? MajorFaults { get; init; }

    // ---- I/O (cumulative bytes) ---------------------------------------------

    /// <summary>From <c>/proc/PID/io</c>; owner-restricted, so null without the helper.</summary>
    public ulong? DiskBytesRead { get; init; }

    /// <summary>From <c>/proc/PID/io</c>; owner-restricted, so null without the helper.</summary>
    public ulong? DiskBytesWritten { get; init; }

    // ---- Handles ------------------------------------------------------------

    /// <summary>Open file descriptors — the Handles analog.</summary>
    public int? FileDescriptorCount { get; init; }

    // ---- Scheduling ---------------------------------------------------------

    public int Nice { get; init; }
    public int Priority { get; init; }

    // ---- Linux-specific -----------------------------------------------------

    /// <summary>Current OOM-killer badness score.</summary>
    public int? OomScore { get; init; }

    /// <summary>User-supplied OOM adjustment, -1000 to 1000.</summary>
    public int? OomScoreAdj { get; init; }

    /// <summary>Full unified-hierarchy cgroup path.</summary>
    public string? CgroupPath { get; init; }

    /// <summary>Owning systemd unit, derived from the cgroup path.</summary>
    public string? SystemdUnit { get; init; }

    /// <summary>AppArmor profile or SELinux context from <c>/proc/PID/attr/current</c>.</summary>
    public string? SecurityLabel { get; init; }

    // ---- Colouring / badges -------------------------------------------------

    public ProcessFlags Flags { get; init; }

    // ---- Filled asynchronously by other providers ---------------------------

    public ProvenanceInfo? Provenance { get; init; }
    public ulong? NetworkBytesPerSec { get; init; }
    public double? GpuPercent { get; init; }
    public ulong? GpuMemoryBytes { get; init; }
    public string? AutostartLocation { get; init; }

    // ---- Timing -------------------------------------------------------------

    /// <summary>Wall-clock start time, for display and for the Start Time column.</summary>
    public DateTimeOffset StartTime { get; init; }

    /// <summary>
    /// Whether the restricted per-process files were readable. Drives the empty
    /// cells for I/O and PSS columns rather than showing a misleading zero.
    /// </summary>
    public bool HasFullInfo => !Flags.HasFlag(ProcessFlags.LimitedInfo);
}
