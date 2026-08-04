using System.Diagnostics;
using Procexp.Model;

namespace Procexp.SystemStats;

/// <summary>
/// System-wide statistics from <c>/proc</c> and <c>/sys</c>.
/// </summary>
/// <remarks>
/// The Linux counterpart of the macOS provider that reads Mach host statistics,
/// sysctl and IOKit.
///
/// What is real here:
/// <list type="bullet">
/// <item>CPU total and per-core, from <c>/proc/stat</c> jiffy counters, delta'd
///   against the previous sample. The first call returns zeros.</item>
/// <item>Memory, from <c>/proc/meminfo</c>. "Used" follows the same definition
///   as free(1): total minus available, which accounts for reclaimable cache.</item>
/// <item>Compressed memory, from zram's <c>mm_stat</c> or zswap, whichever is
///   active. Zero when neither is.</item>
/// <item>Disk and network throughput, from <c>/proc/diskstats</c> and
///   <c>/proc/net/dev</c>, delta'd.</item>
/// </list>
///
/// Process, thread and handle counts are left at zero here and filled by the
/// sampling engine, which already has them — the same split the macOS build makes
/// between its W1 and W4 workstreams.
/// </remarks>
public sealed class SystemStatsProvider : ISystemStatsProvider
{
    private readonly Lock _gate = new();

    private ulong[] _previousCpuTotal = [];
    private ulong[] _previousCpuIdle = [];
    private ulong _previousDiskBytes;
    private ulong _previousNetworkBytes;
    private long _previousTimestamp;

    public ValueTask<Model.SystemStats> StatsAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Read());

    public Model.SystemStats Read()
    {
        var buffer = ProcReader.Rent(32768);
        try
        {
            var (total, perCore) = ReadCpu(ref buffer);
            var memory = ReadMemory(ref buffer);
            var diskBytes = ReadDiskBytes(ref buffer);
            var networkBytes = ReadNetworkBytes(ref buffer);

            ulong diskRate;
            ulong networkRate;

            lock (_gate)
            {
                var now = Stopwatch.GetTimestamp();
                var seconds = _previousTimestamp == 0
                    ? 0
                    : Stopwatch.GetElapsedTime(_previousTimestamp, now).TotalSeconds;

                diskRate = Rate(diskBytes, _previousDiskBytes, seconds);
                networkRate = Rate(networkBytes, _previousNetworkBytes, seconds);

                _previousDiskBytes = diskBytes;
                _previousNetworkBytes = networkBytes;
                _previousTimestamp = now;
            }

            return new Model.SystemStats
            {
                CpuTotalPercent = total,
                PerCoreCpuPercent = perCore,
                MemoryUsed = memory.Used,
                MemoryTotal = memory.Total,
                MemoryKernel = memory.Kernel,
                MemoryCached = memory.Cached,
                MemoryCompressed = ReadCompressedMemory(),
                SwapUsed = memory.SwapUsed,
                SwapTotal = memory.SwapTotal,
                DiskBytesPerSec = diskRate,
                NetworkBytesPerSec = networkRate,
                HandleCount = ReadOpenFileCount(ref buffer),
            };
        }
        finally
        {
            ProcReader.Return(buffer);
        }
    }

    private static ulong Rate(ulong current, ulong previous, double seconds) =>
        seconds <= 0 || current < previous ? 0 : (ulong)((current - previous) / seconds);

    // ---- CPU ----------------------------------------------------------------

    /// <summary>
    /// Read <c>/proc/stat</c> and turn the cumulative jiffy counters into
    /// percentages.
    /// </summary>
    /// <remarks>
    /// Busy time is everything except idle and iowait. Counting iowait as busy is
    /// a common mistake — a core waiting on disk is not doing work, and including
    /// it makes an idle machine with a slow disk look pegged.
    /// </remarks>
    private (double Total, double[] PerCore) ReadCpu(ref byte[] buffer)
    {
        if (!ProcReader.TryRead("/proc/stat", ref buffer, out var length))
        {
            return (0, []);
        }

        var totals = new List<ulong>(Environment.ProcessorCount + 1);
        var idles = new List<ulong>(Environment.ProcessorCount + 1);

        foreach (var line in ProcReader.Lines(buffer.AsSpan(0, length)))
        {
            if (!line.StartsWith("cpu"u8))
            {
                break;   // The cpu lines come first; stop as soon as they end.
            }

            var rest = line;
            ProcReader.NextField(ref rest);   // "cpu" or "cpuN"

            // user nice system idle iowait irq softirq steal guest guest_nice
            ulong sum = 0;
            ulong idle = 0;
            for (var i = 0; i < 10; i++)
            {
                var field = ProcReader.NextField(ref rest);
                if (field.IsEmpty)
                {
                    break;
                }

                var value = ProcReader.ParseUInt64(field);

                // guest and guest_nice are already counted inside user and nice,
                // so adding them again would double-count virtualised time.
                if (i < 8)
                {
                    sum += value;
                }

                if (i is 3 or 4)
                {
                    idle += value;
                }
            }

            totals.Add(sum);
            idles.Add(idle);
        }

        if (totals.Count == 0)
        {
            return (0, []);
        }

        lock (_gate)
        {
            var previousTotal = _previousCpuTotal;
            var previousIdle = _previousCpuIdle;

            _previousCpuTotal = [.. totals];
            _previousCpuIdle = [.. idles];

            if (previousTotal.Length != totals.Count)
            {
                // First sample, or the core count changed under us (CPU hotplug).
                return (0, new double[Math.Max(0, totals.Count - 1)]);
            }

            var percentages = new double[totals.Count];
            for (var i = 0; i < totals.Count; i++)
            {
                var totalDelta = totals[i] - previousTotal[i];
                var idleDelta = idles[i] - previousIdle[i];
                percentages[i] = totalDelta == 0
                    ? 0
                    : Math.Clamp((totalDelta - idleDelta) * 100.0 / totalDelta, 0, 100);
            }

            // Index 0 is the aggregate "cpu" line; the rest are the cores.
            return (percentages[0], percentages[1..]);
        }
    }

    // ---- Memory -------------------------------------------------------------

    private readonly record struct MemoryFacts(
        ulong Total, ulong Used, ulong Cached, ulong Kernel, ulong SwapTotal, ulong SwapUsed);

    private static MemoryFacts ReadMemory(ref byte[] buffer)
    {
        if (!ProcReader.TryRead("/proc/meminfo", ref buffer, out var length))
        {
            return default;
        }

        ulong total = 0, available = 0, cached = 0, buffers = 0;
        ulong slab = 0, kernelStack = 0, pageTables = 0, unevictable = 0;
        ulong swapTotal = 0, swapFree = 0;

        foreach (var line in ProcReader.Lines(buffer.AsSpan(0, length)))
        {
            var colon = line.IndexOf((byte)':');
            if (colon < 0)
            {
                continue;
            }

            var key = line[..colon];
            var value = line[(colon + 1)..];

            if (key.SequenceEqual("MemTotal"u8)) total = Kilobytes(value);
            else if (key.SequenceEqual("MemAvailable"u8)) available = Kilobytes(value);
            else if (key.SequenceEqual("Cached"u8)) cached = Kilobytes(value);
            else if (key.SequenceEqual("Buffers"u8)) buffers = Kilobytes(value);
            else if (key.SequenceEqual("Slab"u8)) slab = Kilobytes(value);
            else if (key.SequenceEqual("KernelStack"u8)) kernelStack = Kilobytes(value);
            else if (key.SequenceEqual("PageTables"u8)) pageTables = Kilobytes(value);
            else if (key.SequenceEqual("Unevictable"u8)) unevictable = Kilobytes(value);
            else if (key.SequenceEqual("SwapTotal"u8)) swapTotal = Kilobytes(value);
            else if (key.SequenceEqual("SwapFree"u8)) swapFree = Kilobytes(value);
        }

        // MemAvailable is the kernel's own estimate of what a new allocation could
        // get without swapping, and is what free(1) subtracts. It is a far better
        // basis than MemFree, which treats all page cache as unavailable.
        var used = available > 0 && total >= available ? total - available : 0;

        return new MemoryFacts(
            total,
            used,
            cached + buffers,
            slab + kernelStack + pageTables + unevictable,
            swapTotal,
            swapTotal >= swapFree ? swapTotal - swapFree : 0);

        static ulong Kilobytes(ReadOnlySpan<byte> value)
        {
            var span = value;
            return ProcReader.ParseUInt64(ProcReader.NextField(ref span)) * 1024;
        }
    }

    /// <summary>
    /// Compressed memory, the analog of the macOS compressor statistic.
    /// </summary>
    /// <remarks>
    /// Two mutually-exclusive mechanisms exist. zram presents block devices whose
    /// <c>mm_stat</c> reports original and compressed sizes; zswap is a
    /// write-back cache in front of swap and reports through sysfs. Neither is
    /// enabled by default on most distributions, so zero here is the normal case
    /// rather than a failure.
    /// </remarks>
    private static ulong ReadCompressedMemory()
    {
        ulong total = 0;

        try
        {
            foreach (var device in Directory.EnumerateDirectories("/sys/block", "zram*"))
            {
                var text = ProcReader.ReadText(Path.Combine(device, "mm_stat"));
                if (text is null)
                {
                    continue;
                }

                // orig_data_size compr_data_size mem_used_total ...
                var fields = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length >= 2 && ulong.TryParse(fields[1], out var compressed))
                {
                    total += compressed;
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // No zram.
        }

        if (total == 0)
        {
            var poolTotal = ProcReader.ReadText("/sys/kernel/debug/zswap/pool_total_size");
            if (poolTotal is not null && ulong.TryParse(poolTotal, out var zswap))
            {
                total = zswap;
            }
        }

        return total;
    }

    // ---- Disk ---------------------------------------------------------------

    /// <summary>
    /// Total bytes transferred across physical block devices, from
    /// <c>/proc/diskstats</c>.
    /// </summary>
    /// <remarks>
    /// Counts only whole devices, skipping partitions and virtual stacking layers
    /// (dm, md, loop, ram, zram). Including them double-counts every byte, once
    /// for the partition and again for its parent — the classic way to report
    /// twice the real throughput.
    /// </remarks>
    private static ulong ReadDiskBytes(ref byte[] buffer)
    {
        if (!ProcReader.TryRead("/proc/diskstats", ref buffer, out var length))
        {
            return 0;
        }

        const int SectorSize = 512;
        ulong sectors = 0;

        foreach (var line in ProcReader.Lines(buffer.AsSpan(0, length)))
        {
            var rest = line;
            ProcReader.NextField(ref rest);                       // major
            ProcReader.NextField(ref rest);                       // minor
            var name = ProcReader.NextField(ref rest);

            if (!IsWholePhysicalDevice(name))
            {
                continue;
            }

            ProcReader.NextField(ref rest);                       // reads completed
            ProcReader.NextField(ref rest);                       // reads merged
            sectors += ProcReader.ParseUInt64(ProcReader.NextField(ref rest));   // sectors read
            ProcReader.NextField(ref rest);                       // ms reading
            ProcReader.NextField(ref rest);                       // writes completed
            ProcReader.NextField(ref rest);                       // writes merged
            sectors += ProcReader.ParseUInt64(ProcReader.NextField(ref rest));   // sectors written
        }

        return sectors * SectorSize;
    }

    private static bool IsWholePhysicalDevice(ReadOnlySpan<byte> name)
    {
        if (name.StartsWith("loop"u8) || name.StartsWith("ram"u8) ||
            name.StartsWith("zram"u8) || name.StartsWith("dm-"u8) ||
            name.StartsWith("md"u8))
        {
            return false;
        }

        // sda1, nvme0n1p2 and mmcblk0p1 are partitions of a device we already
        // count. A trailing digit alone is not enough to tell — nvme0n1 ends in a
        // digit and is a whole device — so test for the partition marker.
        if (name.StartsWith("nvme"u8) || name.StartsWith("mmcblk"u8))
        {
            return name.IndexOf((byte)'p') < 0;
        }

        return name.Length == 0 || !char.IsAsciiDigit((char)name[^1]);
    }

    // ---- Network ------------------------------------------------------------

    /// <summary>
    /// Total bytes across real network interfaces, from <c>/proc/net/dev</c>.
    /// Loopback is excluded: local traffic would otherwise be counted twice and
    /// makes an idle machine look busy.
    /// </summary>
    private static ulong ReadNetworkBytes(ref byte[] buffer)
    {
        if (!ProcReader.TryRead("/proc/net/dev", ref buffer, out var length))
        {
            return 0;
        }

        ulong total = 0;
        var lineNumber = 0;

        foreach (var line in ProcReader.Lines(buffer.AsSpan(0, length)))
        {
            // Two header lines precede the data.
            if (lineNumber++ < 2)
            {
                continue;
            }

            var colon = line.IndexOf((byte)':');
            if (colon < 0)
            {
                continue;
            }

            var name = line[..colon].TrimStart((byte)' ');
            if (name.SequenceEqual("lo"u8))
            {
                continue;
            }

            var rest = line[(colon + 1)..];
            total += ProcReader.ParseUInt64(ProcReader.NextField(ref rest));   // receive bytes

            for (var i = 0; i < 7; i++)
            {
                ProcReader.NextField(ref rest);
            }

            total += ProcReader.ParseUInt64(ProcReader.NextField(ref rest));   // transmit bytes
        }

        return total;
    }

    // ---- Handles ------------------------------------------------------------

    /// <summary>System-wide allocated file descriptors, from <c>/proc/sys/fs/file-nr</c>.</summary>
    private static int ReadOpenFileCount(ref byte[] buffer)
    {
        if (!ProcReader.TryRead("/proc/sys/fs/file-nr", ref buffer, out var length))
        {
            return 0;
        }

        // allocated  free  maximum
        ReadOnlySpan<byte> span = buffer.AsSpan(0, length);
        var allocated = ProcReader.ParseUInt64(ProcReader.NextField(ref span));
        var free = ProcReader.ParseUInt64(ProcReader.NextField(ref span));
        return (int)Math.Min(int.MaxValue, allocated - Math.Min(allocated, free));
    }
}
