using System.Runtime.CompilerServices;
using Procexp.Model;

namespace Procexp.Sampling;

/// <summary>
/// The unprivileged sampling engine: a <see cref="IProcessDataProvider"/> backed
/// by direct <c>/proc</c> parsing.
/// </summary>
/// <remarks>
/// Deliberately does not use <c>System.Diagnostics.Process</c>, which allocates
/// heavily, exposes only a fraction of what the Properties window needs, and
/// costs far too much to call several hundred times a second.
///
/// The Linux counterpart of the macOS <c>LibprocDataProvider</c>. It fills as
/// much of <see cref="ProcessRecord"/> as the kernel exposes to an ordinary user,
/// which is nearly everything: only per-process I/O counters, proportional memory
/// and environments are owner-restricted, and those degrade to null rather than
/// failing the sweep.
/// </remarks>
public sealed class ProcSampler : IProcessDataProvider
{
    private readonly ProcSamplerOptions _options;
    private readonly SystemContext _context;
    private readonly CpuDeltaTracker _cpuTracker = new();

    public ProcSampler(ProcSamplerOptions? options = null)
    {
        _options = options ?? ProcSamplerOptions.Default;
        _context = SystemContext.Create();
    }

    public ProviderCapabilities Capabilities
    {
        get
        {
            // Threads and modules need no privilege on Linux, unlike macOS where
            // both hinge on obtaining a task port.
            var capabilities = ProviderCapabilities.AccurateCpu |
                               ProviderCapabilities.Threads |
                               ProviderCapabilities.Modules |
                               ProviderCapabilities.CrossUser;

            // Running as root removes the only remaining restrictions.
            if (_context.OwnUid == 0)
            {
                capabilities |= ProviderCapabilities.ProcessIo |
                                ProviderCapabilities.ProportionalMemory |
                                ProviderCapabilities.Environment;
            }

            return capabilities;
        }
    }

    // ---- Streaming ----------------------------------------------------------

    public async IAsyncEnumerable<ProcessSnapshot> Snapshots(
        TimeSpan interval,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        _cpuTracker.Reset();

        // First frame immediately, so the UI paints without waiting a full
        // interval. CPU reads zero until there is a delta to compute.
        yield return Sample(interval.TotalSeconds);

        using var timer = new PeriodicTimer(interval);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return Sample(interval.TotalSeconds);
        }
    }

    public ValueTask<ProcessSnapshot> SnapshotAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Sample(0));

    // ---- One sweep ----------------------------------------------------------

    private ProcessSnapshot Sample(double interval)
    {
        var records = new Dictionary<ProcessId, ProcessRecord>(512);
        var pidToId = new Dictionary<int, ProcessId>(512);
        var parentPids = new Dictionary<ProcessId, int>(512);
        var cpuTimes = new Dictionary<ProcessId, ulong>(512);

        var totalThreads = 0;
        var totalFds = 0;

        var buffer = ProcFile.RentBuffer(8192);
        try
        {
            foreach (var pid in EnumeratePids())
            {
                var (record, ppid) = BuildRecord(pid, ref buffer);
                if (record is null)
                {
                    continue;
                }

                records[record.Id] = record;
                pidToId[pid] = record.Id;
                parentPids[record.Id] = ppid;
                cpuTimes[record.Id] = record.CpuTime;
                totalThreads += record.ThreadCount;
                totalFds += record.FileDescriptorCount ?? 0;
            }
        }
        finally
        {
            ProcFile.ReturnBuffer(buffer);
        }

        // Resolve parents now every PID-to-identity mapping is known. Doing this
        // in a second pass is what makes the tree correct when a parent appears
        // later in the directory order than its child.
        foreach (var (id, ppid) in parentPids)
        {
            if (ppid != 0 && pidToId.TryGetValue(ppid, out var parentId) && parentId != id)
            {
                records[id] = records[id] with { Parent = parentId };
            }
        }

        foreach (var (id, percent) in _cpuTracker.Percentages(cpuTimes))
        {
            if (percent > 0 && records.TryGetValue(id, out var record))
            {
                records[id] = record with { CpuPercent = percent };
            }
        }

        var (roots, children) = ProcessTreeBuilder.Build(records);

        return new ProcessSnapshot
        {
            Timestamp = DateTimeOffset.Now,
            Interval = interval,
            Processes = records,
            Roots = roots,
            Children = children,

            // Only the counts the sweep already knows. Full system statistics are
            // the SystemStats provider's job, matching how the macOS build splits
            // W1 from W4.
            System = SystemStats.Zero with
            {
                ProcessCount = records.Count,
                ThreadCount = totalThreads,
                HandleCount = totalFds,
            },
        };
    }

    /// <summary>Every numeric directory under <c>/proc</c> is a live process.</summary>
    private static IEnumerable<int> EnumeratePids()
    {
        IEnumerable<string> entries;
        try
        {
            entries = Directory.EnumerateDirectories("/proc");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var entry in entries)
        {
            var slash = entry.LastIndexOf('/');
            var name = slash < 0 ? entry.AsSpan() : entry.AsSpan(slash + 1);

            if (name.Length > 0 && char.IsAsciiDigit(name[0]) &&
                int.TryParse(name, out var pid))
            {
                yield return pid;
            }
        }
    }

    private (ProcessRecord? Record, int Ppid) BuildRecord(int pid, ref byte[] buffer)
    {
        var prefix = $"/proc/{pid}";

        if (!ProcFile.TryRead($"{prefix}/stat", ref buffer, out var length) ||
            !ProcStat.TryParse(buffer.AsSpan(0, length), out var stat))
        {
            // The process exited between the directory listing and this read.
            // Entirely routine on a busy system.
            return (null, 0);
        }

        var id = new ProcessId(pid, stat.StartTimeTicks);
        var flags = ProcessFlags.None;
        var limited = false;

        // Kernel threads are children of kthreadd (pid 2). On this machine that
        // is half of all processes, and none of them has an image, an argv, a
        // cgroup of interest, I/O counters or descriptors — so recognising them
        // from the ppid we already parsed skips five syscalls each.
        var isKernelThread = pid == 2 || stat.Ppid == 2;

        // ---- status: ownership, thread count, context switches, memory detail
        uint uid = 0;
        uint gid = 0;
        ulong? swap = null;
        ulong? shared = null;
        ulong? privateAnon = null;
        ulong? voluntary = null;
        ulong? involuntary = null;
        var threadCount = stat.NumThreads;

        if (ProcFile.TryRead($"{prefix}/status", ref buffer, out length))
        {
            var status = buffer.AsSpan(0, length);

            // "Uid: real effective saved fs" — the effective uid is what the
            // process actually acts as, so that is what we attribute it to.
            var uidLine = ProcFile.FindKeyedValue(status, "Uid"u8);
            if (!uidLine.IsEmpty)
            {
                var span = uidLine;
                ProcFile.NextField(ref span);
                uid = (uint)ProcFile.ParseUInt64(TrimTabs(ProcFile.NextField(ref span)));
                if (uid == 0)
                {
                    uid = (uint)ProcFile.ParseUInt64(TrimTabs(uidLine));
                }
            }

            var gidLine = ProcFile.FindKeyedValue(status, "Gid"u8);
            if (!gidLine.IsEmpty)
            {
                gid = (uint)ProcFile.ParseUInt64(TrimTabs(gidLine));
            }

            var threads = ProcFile.FindKeyedValue(status, "Threads"u8);
            if (!threads.IsEmpty)
            {
                threadCount = ProcFile.ParseInt32(threads);
            }

            var vmSwap = ProcFile.FindKeyedValue(status, "VmSwap"u8);
            if (!vmSwap.IsEmpty)
            {
                swap = ProcFile.ParseKilobytes(vmSwap);
            }

            // Free: this file is already open and world-readable.
            var rssAnon = ProcFile.FindKeyedValue(status, "RssAnon"u8);
            if (!rssAnon.IsEmpty)
            {
                privateAnon = ProcFile.ParseKilobytes(rssAnon);
            }

            var rssFile = ProcFile.FindKeyedValue(status, "RssFile"u8);
            var rssShmem = ProcFile.FindKeyedValue(status, "RssShmem"u8);
            if (!rssFile.IsEmpty || !rssShmem.IsEmpty)
            {
                shared = ProcFile.ParseKilobytes(rssFile) + ProcFile.ParseKilobytes(rssShmem);
            }

            var vctx = ProcFile.FindKeyedValue(status, "voluntary_ctxt_switches"u8);
            if (!vctx.IsEmpty)
            {
                voluntary = ProcFile.ParseUInt64(vctx);
            }

            var nvctx = ProcFile.FindKeyedValue(status, "nonvoluntary_ctxt_switches"u8);
            if (!nvctx.IsEmpty)
            {
                involuntary = ProcFile.ParseUInt64(nvctx);
            }
        }

        // ---- command line and image
        string? commandLine = null;
        string? executablePath = null;

        if (!isKernelThread)
        {
            commandLine = ReadCommandLine($"{prefix}/cmdline", ref buffer);
            executablePath = StripDeletedSuffix(ProcFile.ReadLink($"{prefix}/exe"));

            // Catch the few kernel threads that are not direct children of
            // kthreadd: no address space means no exe link and no argv.
            isKernelThread = executablePath is null && string.IsNullOrEmpty(commandLine);
        }

        if (isKernelThread)
        {
            flags |= ProcessFlags.KernelThread;
        }

        // Reading another user's /proc/PID/io always fails unless we are root, so
        // do not pay for the open just to be refused. The record is still marked
        // limited, which is what drives the blank I/O cells.
        var canReadRestricted = uid == _context.OwnUid || _context.OwnUid == 0;

        var name = stat.Comm.Length > 0
            ? stat.Comm
            : Path.GetFileName(executablePath) ?? $"pid {pid}";

        // ---- I/O counters (owner-restricted)
        ulong? ioRead = null;
        ulong? ioWritten = null;
        if (_options.IncludeIoCounters && !isKernelThread)
        {
            if (canReadRestricted && ProcFile.TryRead($"{prefix}/io", ref buffer, out length))
            {
                var io = buffer.AsSpan(0, length);
                ioRead = ProcFile.ParseUInt64(ProcFile.FindKeyedValue(io, "read_bytes"u8));
                ioWritten = ProcFile.ParseUInt64(ProcFile.FindKeyedValue(io, "write_bytes"u8));
            }
            else
            {
                limited = true;
            }
        }

        // ---- proportional set size (owner-restricted, and expensive)
        ulong? pss = null;
        if (_options.IncludeProportionalSetSize && !isKernelThread)
        {
            if (canReadRestricted && ProcFile.TryRead($"{prefix}/smaps_rollup", ref buffer, out length))
            {
                pss = ProcFile.ParseKilobytes(ProcFile.FindKeyedValue(buffer.AsSpan(0, length), "Pss"u8));
            }
            else
            {
                limited = true;
            }
        }

        // ---- cgroup classification
        var cgroup = default(CgroupClassification);
        if (_options.IncludeCgroup && !isKernelThread)
        {
            if (ProcFile.TryRead($"{prefix}/cgroup", ref buffer, out length))
            {
                cgroup = CgroupInfo.Parse(buffer.AsSpan(0, length));
            }
        }

        // ---- MAC label
        string? securityLabel = null;
        if (_options.IncludeSecurityLabel)
        {
            if (ProcFile.TryRead($"{prefix}/attr/current", ref buffer, out length) && length > 0)
            {
                securityLabel = ProcFile.ToString(buffer.AsSpan(0, length)).TrimEnd('\0', '\n');
                if (securityLabel is "unconfined" or "")
                {
                    securityLabel = null;
                }
            }
        }

        // ---- flags
        if (uid == _context.OwnUid)
        {
            flags |= ProcessFlags.OwnProcess;
        }

        if (cgroup.IsService)
        {
            flags |= ProcessFlags.Service;
        }

        if (stat.State is 'T' or 't')
        {
            flags |= ProcessFlags.Suspended;
        }

        if (stat.State == 'Z')
        {
            flags |= ProcessFlags.Zombie;
        }

        if (cgroup.ContainerKind != ImageKind.Unknown || securityLabel is not null)
        {
            flags |= ProcessFlags.Sandboxed;
        }

        if (limited)
        {
            flags |= ProcessFlags.LimitedInfo;
        }

        var cpuTicks = stat.UserTimeTicks + stat.SystemTimeTicks;

        var record = new ProcessRecord
        {
            Id = id,
            Parent = null,                       // resolved in the second pass
            Name = name,
            ExecutablePath = executablePath,
            ImageKind = ClassifyImage(cgroup.ContainerKind, isKernelThread, cgroup.IsService, executablePath),
            Uid = uid,
            Gid = gid,
            UserName = _context.UserName(uid),
            SessionTty = ProcStat.DecodeTty(stat.TtyNr),
            State = stat.State,
            KernelFlags = stat.Flags,
            HasControllingTty = stat.TtyNr != 0,
            IsSessionLeader = stat.SessionId == pid,
            CommandLine = commandLine,
            CpuTime = cpuTicks * NativeMethods.NanosPerTick,
            UserTime = stat.UserTimeTicks * NativeMethods.NanosPerTick,
            SystemTime = stat.SystemTimeTicks * NativeMethods.NanosPerTick,
            ThreadCount = threadCount,
            SchedulingPolicy = stat.Policy,
            VoluntaryContextSwitches = voluntary,
            InvoluntaryContextSwitches = involuntary,
            LastCpu = stat.Processor,
            ResidentSize = (ulong)Math.Max(0, stat.ResidentPages) * (ulong)SystemContext.PageSize,
            VirtualSize = stat.VirtualSize,
            ProportionalSetSize = pss,
            PrivateSize = privateAnon,
            SharedSize = shared,
            SwapSize = swap,
            MinorFaults = stat.MinorFaults,
            MajorFaults = stat.MajorFaults,
            DiskBytesRead = ioRead,
            DiskBytesWritten = ioWritten,
            FileDescriptorCount = _options.IncludeFileDescriptorCount && !isKernelThread
                ? ProcFile.TryCountEntries($"{prefix}/fd")
                : null,
            Nice = stat.Nice,
            Priority = stat.Priority,
            CgroupPath = cgroup.Path,
            SystemdUnit = cgroup.Unit,
            SecurityLabel = securityLabel,
            Flags = flags,
            StartTime = _context.BootTime.AddSeconds(
                stat.StartTimeTicks / (double)NativeMethods.ClockTicksPerSecond),
        };

        return (record, stat.Ppid);
    }

    private static ImageKind ClassifyImage(ImageKind containerKind, bool isKernelThread, bool isService, string? path)
    {
        if (isKernelThread)
        {
            return ImageKind.KernelThread;
        }

        if (containerKind != ImageKind.Unknown)
        {
            return containerKind;
        }

        if (isService)
        {
            return ImageKind.Daemon;
        }

        return path is null ? ImageKind.Unknown : ImageKind.CommandLine;
    }

    /// <summary>
    /// Read <c>/proc/PID/cmdline</c>, whose arguments are NUL-separated with a
    /// trailing NUL. Needs no privilege, unlike the macOS equivalent.
    /// </summary>
    private static string? ReadCommandLine(string path, ref byte[] buffer)
    {
        if (!ProcFile.TryRead(path, ref buffer, out var length) || length == 0)
        {
            return null;
        }

        var span = buffer.AsSpan(0, length);
        while (!span.IsEmpty && span[^1] == 0)
        {
            span = span[..^1];
        }

        if (span.IsEmpty)
        {
            return null;
        }

        var chars = ProcFile.ToString(span).ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (chars[i] == '\0')
            {
                chars[i] = ' ';
            }
        }

        return new string(chars);
    }

    /// <summary>
    /// The kernel appends " (deleted)" to the exe link when the image has been
    /// replaced underneath a running process — common right after an upgrade.
    /// </summary>
    private static string? StripDeletedSuffix(string? path) =>
        path is not null && path.EndsWith(" (deleted)", StringComparison.Ordinal)
            ? path[..^10]
            : path;

    private static ReadOnlySpan<byte> TrimTabs(ReadOnlySpan<byte> span)
    {
        var start = 0;
        while (start < span.Length && (span[start] == (byte)'\t' || span[start] == (byte)' '))
        {
            start++;
        }

        return span[start..];
    }

    // ---- Per-selection detail (filled in by the detail providers) ------------

    public ValueTask<IReadOnlyList<ThreadInfo>> ThreadsAsync(ProcessId id, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(ProcThreads.Read(id));

    public ValueTask<IReadOnlyList<ModuleInfo>> ModulesAsync(ProcessId id, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(ProcMaps.Read(id));

    public ValueTask<IReadOnlyList<FileDescriptorInfo>> FileDescriptorsAsync(ProcessId id, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(ProcFileDescriptors.Read(id));

    public ValueTask<string?> CommandLineAsync(ProcessId id, CancellationToken cancellationToken = default)
    {
        var buffer = ProcFile.RentBuffer();
        try
        {
            return ValueTask.FromResult(ReadCommandLine($"/proc/{id.Pid}/cmdline", ref buffer));
        }
        finally
        {
            ProcFile.ReturnBuffer(buffer);
        }
    }

    public ValueTask<IReadOnlyDictionary<string, string>> EnvironmentAsync(ProcessId id, CancellationToken cancellationToken = default)
    {
        var buffer = ProcFile.RentBuffer(16384);
        try
        {
            if (!ProcFile.TryRead($"/proc/{id.Pid}/environ", ref buffer, out var length))
            {
                throw ProviderException.NotPermitted($"/proc/{id.Pid}/environ is restricted to the owning user");
            }

            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            var span = buffer.AsSpan(0, length);

            while (!span.IsEmpty)
            {
                var nul = span.IndexOf((byte)0);
                var entry = nul < 0 ? span : span[..nul];

                if (!entry.IsEmpty)
                {
                    var eq = entry.IndexOf((byte)'=');
                    if (eq > 0)
                    {
                        result[ProcFile.ToString(entry[..eq])] = ProcFile.ToString(entry[(eq + 1)..]);
                    }
                }

                if (nul < 0)
                {
                    break;
                }

                span = span[(nul + 1)..];
            }

            return ValueTask.FromResult<IReadOnlyDictionary<string, string>>(result);
        }
        finally
        {
            ProcFile.ReturnBuffer(buffer);
        }
    }

    public ValueTask<string?> CurrentDirectoryAsync(ProcessId id, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(ProcFile.ReadLink($"/proc/{id.Pid}/cwd"));

    public ValueTask<IReadOnlyList<string>> StringsAsync(ProcessId id, CancellationToken cancellationToken = default)
    {
        var path = ProcFile.ReadLink($"/proc/{id.Pid}/exe");
        return path is null
            ? ValueTask.FromResult<IReadOnlyList<string>>([])
            : ValueTask.FromResult(ImageStrings.Extract(StripDeletedSuffix(path)!));
    }
}
