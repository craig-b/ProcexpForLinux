namespace Procexp.Model;

/// <summary>
/// What a provider can offer, so the UI degrades gracefully when something is
/// unavailable rather than showing wrong numbers.
/// </summary>
[Flags]
public enum ProviderCapabilities : uint
{
    None = 0,

    /// <summary>Can read detail for processes owned by other users.</summary>
    CrossUser = 1 << 0,

    /// <summary>CPU percentages are delta-based rather than kernel averages.</summary>
    AccurateCpu = 1 << 1,

    /// <summary>Can enumerate per-thread detail.</summary>
    Threads = 1 << 2,

    /// <summary>Can read process environments.</summary>
    Environment = 1 << 3,

    /// <summary>Can enumerate mapped modules.</summary>
    Modules = 1 << 4,

    /// <summary>Can read the owner-restricted per-process I/O counters.</summary>
    ProcessIo = 1 << 5,

    /// <summary>Can read proportional set size from smaps_rollup.</summary>
    ProportionalMemory = 1 << 6,
}

/// <summary>Why a provider call failed.</summary>
public enum ProviderErrorKind
{
    NotPermitted,
    ProcessGone,
    Unsupported,
    HelperUnavailable,
    Underlying,
}

/// <summary>Errors providers raise. Deliberately few and coarse.</summary>
public sealed class ProviderException(ProviderErrorKind kind, string? message = null)
    : Exception(message ?? kind.ToString())
{
    public ProviderErrorKind Kind { get; } = kind;

    public static ProviderException NotPermitted(string? detail = null) =>
        new(ProviderErrorKind.NotPermitted, detail);

    public static ProviderException ProcessGone(ProcessId id) =>
        new(ProviderErrorKind.ProcessGone, $"{id} is no longer running");

    public static ProviderException Unsupported(string? detail = null) =>
        new(ProviderErrorKind.Unsupported, detail);
}

/// <summary>
/// The primary source of process snapshots. The UI consumes
/// <see cref="Snapshots"/> and depends on nothing else about the implementation
/// — which is what lets the unprivileged and helper-backed providers be swapped
/// at runtime.
/// </summary>
public interface IProcessDataProvider
{
    /// <summary>A stream of snapshots produced at approximately <paramref name="interval"/>.</summary>
    IAsyncEnumerable<ProcessSnapshot> Snapshots(
        TimeSpan interval,
        CancellationToken cancellationToken = default
    );

    /// <summary>A one-shot snapshot, for tests and the initial paint.</summary>
    ValueTask<ProcessSnapshot> SnapshotAsync(CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<ThreadInfo>> ThreadsAsync(
        ProcessId id,
        CancellationToken cancellationToken = default
    );
    ValueTask<IReadOnlyList<ModuleInfo>> ModulesAsync(
        ProcessId id,
        CancellationToken cancellationToken = default
    );
    ValueTask<IReadOnlyList<FileDescriptorInfo>> FileDescriptorsAsync(
        ProcessId id,
        CancellationToken cancellationToken = default
    );
    ValueTask<string?> CommandLineAsync(
        ProcessId id,
        CancellationToken cancellationToken = default
    );
    ValueTask<IReadOnlyDictionary<string, string>> EnvironmentAsync(
        ProcessId id,
        CancellationToken cancellationToken = default
    );
    ValueTask<string?> CurrentDirectoryAsync(
        ProcessId id,
        CancellationToken cancellationToken = default
    );
    ValueTask<IReadOnlyList<string>> StringsAsync(
        ProcessId id,
        CancellationToken cancellationToken = default
    );

    ProviderCapabilities Capabilities { get; }
}

/// <summary>
/// A privileged provider that can also perform control actions. Optional at
/// runtime.
/// </summary>
/// <remarks>
/// Much narrower than the macOS counterpart. On Linux nearly all of <c>/proc</c>
/// is world-readable, so this exists only to reach the three owner-restricted
/// per-process files — <c>io</c>, <c>smaps_rollup</c> and <c>environ</c> — and to
/// signal processes belonging to other users.
/// </remarks>
public interface IPrivilegedProvider : IProcessDataProvider
{
    static abstract bool IsHelperInstalled();
    static abstract Task InstallHelperAsync(CancellationToken cancellationToken = default);
    static abstract Task UninstallHelperAsync(CancellationToken cancellationToken = default);

    Task SuspendAsync(ProcessId id, CancellationToken cancellationToken = default);
    Task ResumeAsync(ProcessId id, CancellationToken cancellationToken = default);
    Task SetNiceAsync(ProcessId id, int nice, CancellationToken cancellationToken = default);
    Task KillAsync(ProcessId id, int signal, CancellationToken cancellationToken = default);
}

/// <summary>
/// Image provenance and reputation — the Linux stand-in for code signing.
/// </summary>
public interface IProvenanceProvider
{
    ValueTask<ProvenanceInfo> ProvenanceAsync(
        string path,
        CancellationToken cancellationToken = default
    );
    ValueTask<VirusTotalResult?> VirusTotalAsync(
        string sha256,
        CancellationToken cancellationToken = default
    );
}

/// <summary>Per-process networking.</summary>
public interface INetworkProvider
{
    ValueTask<IReadOnlyList<SocketInfo>> SocketsAsync(
        ProcessId id,
        CancellationToken cancellationToken = default
    );

    /// <summary>Current per-process byte rates, keyed by process.</summary>
    ValueTask<IReadOnlyDictionary<ProcessId, ulong>> NetworkRatesAsync(
        CancellationToken cancellationToken = default
    );
}

/// <summary>System-wide statistics.</summary>
public interface ISystemStatsProvider
{
    ValueTask<SystemStats> StatsAsync(CancellationToken cancellationToken = default);
}

/// <summary>Resolves what caused a process to be started at boot or login.</summary>
public interface IAutostartProvider
{
    ValueTask<string?> AutostartLocationAsync(
        ProcessRecord process,
        CancellationToken cancellationToken = default
    );
}

/// <summary>Per-process GPU usage.</summary>
public interface IGpuProvider
{
    /// <summary>Per-process GPU busy percentages, keyed by process.</summary>
    ValueTask<IReadOnlyDictionary<ProcessId, double>> GpuUsageAsync(
        CancellationToken cancellationToken = default
    );

    /// <summary>Aggregate GPU busy percentage, or null when no GPU reports it.</summary>
    ValueTask<double?> TotalGpuPercentAsync(CancellationToken cancellationToken = default);
}
