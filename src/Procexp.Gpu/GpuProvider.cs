using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using Procexp.Model;

namespace Procexp.Gpu;

/// <summary>
/// Per-process and system-wide GPU usage.
/// </summary>
/// <remarks>
/// Replaces the Metal-based provider on macOS, and unlike it can attribute usage
/// to individual processes — the kernel's DRM fdinfo accounting has no macOS
/// counterpart.
///
/// The two halves have very different costs and are deliberately separate calls.
/// <see cref="TotalGpuPercentAsync"/> reads one sysfs file and is free.
/// <see cref="Sample"/> must inspect every open descriptor on the system to find
/// the few that are DRM clients — 47 out of roughly 30,000 on a desktop. A
/// readlink pre-filter brings that from ~980 ms to ~230 ms, but it remains far
/// too expensive to fold into the 1 Hz process sweep.
///
/// So per-process GPU runs on its own slower cadence, <see cref="RecommendedInterval"/>,
/// and the UI carries the last known value forward in between — the same thing
/// Process Explorer does for columns that refresh more slowly than the list.
/// </remarks>
public sealed class GpuProvider : IGpuProvider
{
    /// <summary>
    /// How often per-process GPU sampling should run.
    /// </summary>
    /// <remarks>
    /// Chosen so the ~230 ms walk costs under 5% of a core. Sampling at the
    /// list's 1 Hz would spend a quarter of a core discovering that nothing
    /// changed.
    /// </remarks>
    public static readonly TimeSpan RecommendedInterval = TimeSpan.FromSeconds(5);

    private readonly Lock _gate = new();
    private Dictionary<ProcessId, ulong> _previousEngineNanos = [];
    private long _previousTimestamp;

    /// <summary>Whether any DRM device is present at all.</summary>
    public bool IsAvailable { get; } = Directory.Exists("/sys/class/drm");

    /// <summary>
    /// Aggregate GPU busy percentage.
    /// </summary>
    /// <remarks>
    /// Read straight from the driver's own counter where one exists. amdgpu and
    /// several others publish <c>gpu_busy_percent</c>; drivers that do not simply
    /// yield null, and the graph shows a gap rather than a fabricated zero.
    /// </remarks>
    public ValueTask<double?> TotalGpuPercentAsync(CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
        {
            return ValueTask.FromResult<double?>(null);
        }

        double? best = null;

        try
        {
            foreach (var card in Directory.EnumerateDirectories("/sys/class/drm", "card*"))
            {
                if (Path.GetFileName(card).Contains('-'))
                {
                    continue;   // a connector, not a device
                }

                var text = TryReadText(Path.Combine(card, "device", "gpu_busy_percent"));
                if (text is not null &&
                    double.TryParse(text, CultureInfo.InvariantCulture, out var percent))
                {
                    // Several GPUs: report the busiest, which is what the user
                    // cares about when one card is saturated.
                    best = best is null ? percent : Math.Max(best.Value, percent);
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return ValueTask.FromResult<double?>(null);
        }

        return ValueTask.FromResult(best);
    }

    /// <summary>Video memory in use, and the total, for the System Information page.</summary>
    public (ulong Used, ulong Total)? VideoMemory()
    {
        try
        {
            foreach (var card in Directory.EnumerateDirectories("/sys/class/drm", "card*"))
            {
                if (Path.GetFileName(card).Contains('-'))
                {
                    continue;
                }

                var used = TryReadText(Path.Combine(card, "device", "mem_info_vram_used"));
                var total = TryReadText(Path.Combine(card, "device", "mem_info_vram_total"));

                if (used is not null && total is not null &&
                    ulong.TryParse(used, out var usedBytes) && ulong.TryParse(total, out var totalBytes))
                {
                    return (usedBytes, totalBytes);
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // No usable device.
        }

        return null;
    }

    public ValueTask<IReadOnlyDictionary<ProcessId, double>> GpuUsageAsync(
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Sample().Percentages);

    /// <summary>Per-process GPU busy percentage and resident video memory.</summary>
    public (IReadOnlyDictionary<ProcessId, double> Percentages, IReadOnlyDictionary<ProcessId, ulong> Memory) Sample()
    {
        var engineNanos = new Dictionary<ProcessId, ulong>();
        var memory = new Dictionary<ProcessId, ulong>();

        if (IsAvailable)
        {
            Collect(engineNanos, memory);
        }

        var percentages = new Dictionary<ProcessId, double>(engineNanos.Count);
        var now = Stopwatch.GetTimestamp();

        lock (_gate)
        {
            var elapsedNanos = _previousTimestamp == 0
                ? 0
                : Stopwatch.GetElapsedTime(_previousTimestamp, now).TotalNanoseconds;

            if (elapsedNanos > 0)
            {
                foreach (var (id, nanos) in engineNanos)
                {
                    if (_previousEngineNanos.TryGetValue(id, out var previous) && nanos >= previous)
                    {
                        // Engine time is wall-clock busy time on the device, so the
                        // ratio to elapsed time is directly a percentage. A process
                        // driving several engines at once can legitimately exceed
                        // 100, exactly as multi-core CPU usage does.
                        var percent = (nanos - previous) / elapsedNanos * 100.0;
                        if (percent > 0.005)
                        {
                            percentages[id] = percent;
                        }
                    }
                }
            }

            _previousEngineNanos = engineNanos;
            _previousTimestamp = now;
        }

        return (percentages, memory);
    }

    /// <summary>
    /// Walk every process's fdinfo looking for DRM clients.
    /// </summary>
    /// <remarks>
    /// Deduplicated by (device, client id): a client that holds several
    /// descriptors reports the same cumulative totals on each, so summing them
    /// naively multiplies usage by the descriptor count.
    /// </remarks>
    private static void Collect(Dictionary<ProcessId, ulong> engineNanos, Dictionary<ProcessId, ulong> memory)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(8192);
        var seen = new HashSet<(string Device, ulong ClientId)>();

        try
        {
            foreach (var (pid, startTime) in EnumerateProcesses())
            {
                string[] entries;
                try
                {
                    // Enumerate fd rather than fdinfo: the symlink target tells us
                    // whether this is a DRM client for the cost of one readlink,
                    // where fdinfo would need an open, a read and a close.
                    entries = Directory.GetFiles($"/proc/{pid}/fd");
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                ulong processNanos = 0;
                ulong processMemory = 0;
                var found = false;

                seen.Clear();

                foreach (var entry in entries)
                {
                    if (!NativeMethods.IsDrmDescriptor(entry))
                    {
                        continue;
                    }

                    var fdinfoPath = $"/proc/{pid}/fdinfo/{Path.GetFileName(entry)}";
                    if (!DrmFdInfo.TryRead(fdinfoPath, ref buffer, out var length) || length == 0)
                    {
                        continue;
                    }

                    var usage = DrmFdInfo.TryParse(buffer.AsSpan(0, length));
                    if (usage is null || !seen.Add((usage.Device, usage.ClientId)))
                    {
                        continue;
                    }

                    found = true;
                    processNanos += usage.EngineNanos;
                    processMemory += usage.MemoryBytes;
                }

                if (found)
                {
                    var id = new ProcessId(pid, startTime);
                    engineNanos[id] = processNanos;
                    if (processMemory > 0)
                    {
                        memory[id] = processMemory;
                    }
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Enumerate live processes with their start times, so results key on the
    /// same identity the sampler uses and survive PID reuse.
    /// </summary>
    private static IEnumerable<(int Pid, ulong StartTime)> EnumerateProcesses()
    {
        IEnumerable<string> directories;
        try
        {
            directories = Directory.EnumerateDirectories("/proc");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var directory in directories)
        {
            var name = Path.GetFileName(directory);
            if (name.Length == 0 || !char.IsAsciiDigit(name[0]) || !int.TryParse(name, out var pid))
            {
                continue;
            }

            var startTime = ReadStartTime(pid);
            if (startTime is not null)
            {
                yield return (pid, startTime.Value);
            }
        }
    }

    private static ulong? ReadStartTime(int pid)
    {
        byte[] content;
        try
        {
            content = File.ReadAllBytes($"/proc/{pid}/stat");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var span = content.AsSpan();
        var close = span.LastIndexOf((byte)')');
        if (close < 0)
        {
            return null;
        }

        // Start time is the 20th space-separated field after the comm field.
        var rest = span[(close + 1)..];
        for (var i = 0; i < 19; i++)
        {
            var start = 0;
            while (start < rest.Length && rest[start] == (byte)' ')
            {
                start++;
            }

            rest = rest[start..];
            var end = rest.IndexOf((byte)' ');
            if (end < 0)
            {
                return null;
            }

            rest = rest[end..];
        }

        var valueStart = 0;
        while (valueStart < rest.Length && rest[valueStart] == (byte)' ')
        {
            valueStart++;
        }

        return System.Buffers.Text.Utf8Parser.TryParse(rest[valueStart..], out ulong value, out _)
            ? value
            : null;
    }

    private static string? TryReadText(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
