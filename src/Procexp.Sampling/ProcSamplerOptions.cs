namespace Procexp.Sampling;

/// <summary>
/// Knobs for how much a sweep collects. Each of these costs real time across
/// several hundred processes, so the defaults favour a responsive one-second
/// refresh and the expensive ones are opt-in.
/// </summary>
public sealed record ProcSamplerOptions
{
    /// <summary>
    /// Read proportional set size from <c>/proc/PID/smaps_rollup</c>.
    /// </summary>
    /// <remarks>
    /// PSS is the honest Private Bytes analog, but it is by far the most
    /// expensive thing here: the kernel walks every VMA of the process to compute
    /// it. Off by default for the bulk sweep; the Properties window asks for it
    /// per-process where the cost is trivial.
    /// </remarks>
    public bool IncludeProportionalSetSize { get; init; }

    /// <summary>Count open descriptors by enumerating <c>/proc/PID/fd</c>.</summary>
    public bool IncludeFileDescriptorCount { get; init; } = true;

    /// <summary>Read <c>/proc/PID/io</c>. Silently yields nothing for other users' processes.</summary>
    public bool IncludeIoCounters { get; init; } = true;

    /// <summary>Read <c>/proc/PID/cgroup</c> to classify services and containers.</summary>
    public bool IncludeCgroup { get; init; } = true;

    /// <summary>Read the MAC label from <c>/proc/PID/attr/current</c>.</summary>
    public bool IncludeSecurityLabel { get; init; }

    public static readonly ProcSamplerOptions Default = new();

    /// <summary>Everything, for the smoke checker and for one-off deep samples.</summary>
    public static readonly ProcSamplerOptions Full = new()
    {
        IncludeProportionalSetSize = true,
        IncludeFileDescriptorCount = true,
        IncludeIoCounters = true,
        IncludeCgroup = true,
        IncludeSecurityLabel = true,
    };
}
