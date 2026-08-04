using System.Diagnostics;
using System.Globalization;
using Procexp.Model;
using Procexp.Sampling;
using Procexp.SystemStats;

// Headless smoke-checker for the data layer, mirroring Sources/ProcexpSmoke in
// the macOS project. It exercises the providers without a GUI so the sampling
// engine is provable, and measurable, on its own.

var failures = 0;

void Check(string what, bool ok, string? detail = null)
{
    Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}{(detail is null ? "" : $" — {detail}")}");
    if (!ok)
    {
        failures++;
    }
}

Console.WriteLine("procexp-smoke: /proc sampling engine\n");

// ---------------------------------------------------------------------------
Console.WriteLine("Snapshot");
// ---------------------------------------------------------------------------

var sampler = new ProcSampler();
var sw = Stopwatch.StartNew();
var snapshot = await sampler.SnapshotAsync();
var firstSweep = sw.Elapsed;

Check("sweep returns processes", snapshot.Processes.Count > 0,
    $"{snapshot.Processes.Count} processes in {firstSweep.TotalMilliseconds:F1} ms");
Check("tree has roots", snapshot.Roots.Count > 0, $"{snapshot.Roots.Count} roots");
Check("thread count is plausible", snapshot.System.ThreadCount >= snapshot.Processes.Count,
    $"{snapshot.System.ThreadCount} threads");

// pid 1 is always present and is always the tree root on a systemd system.
var init = snapshot.Processes.Values.FirstOrDefault(p => p.Id.Pid == 1);
Check("pid 1 present", init is not null, init?.Name);
Check("pid 1 is a root", init is not null && snapshot.Roots.Contains(init.Id));

// Our own process must be discoverable and correctly attributed.
var self = snapshot.Processes.Values.FirstOrDefault(p => p.Id.Pid == Environment.ProcessId);
Check("own process present", self is not null, self?.Name);
Check("own process flagged as ours", self?.Flags.HasFlag(ProcessFlags.OwnProcess) == true);
Check("own process has a command line", !string.IsNullOrEmpty(self?.CommandLine));
Check("own executable path resolves", self?.ExecutablePath is not null, self?.ExecutablePath);
Check("own start time is sane",
    self is not null && self.StartTime > DateTimeOffset.Now.AddHours(-1) && self.StartTime <= DateTimeOffset.Now,
    self?.StartTime.ToString("O", CultureInfo.InvariantCulture));

// ---------------------------------------------------------------------------
Console.WriteLine("\nCross-check against ps");
// ---------------------------------------------------------------------------

var psCount = RunCommand("ps", "-e --no-headers")?.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
if (psCount is { } expected)
{
    // The two sweeps happen microseconds apart, so exact equality is wrong to
    // demand; processes genuinely start and exit in between.
    var delta = Math.Abs(snapshot.Processes.Count - expected);
    Check("process count matches ps", delta <= Math.Max(5, expected / 50),
        $"ours {snapshot.Processes.Count}, ps {expected}, delta {delta}");
}
else
{
    Console.WriteLine("  [SKIP] ps unavailable");
}

// Verify a specific process's parentage against ps, which is the thing most
// likely to be silently wrong if the stat parser mis-counts fields.
var psPpid = RunCommand("ps", $"-o ppid= -p {Environment.ProcessId}")?.Trim();
if (int.TryParse(psPpid, out var expectedPpid) && self is not null)
{
    var ourPpid = self.Parent?.Pid;
    Check("parent pid matches ps", ourPpid == expectedPpid || (ourPpid is null && expectedPpid == 0),
        $"ours {ourPpid?.ToString(CultureInfo.InvariantCulture) ?? "none"}, ps {expectedPpid}");
}

// ---------------------------------------------------------------------------
Console.WriteLine("\nCPU deltas");
// ---------------------------------------------------------------------------

// A rate needs two samples, so the first frame must read zero everywhere.
var enumerator = sampler.Snapshots(TimeSpan.FromMilliseconds(600)).GetAsyncEnumerator();
await enumerator.MoveNextAsync();
var frame1 = enumerator.Current;
Check("first frame has no CPU rates", frame1.Processes.Values.All(p => p.CpuPercent == 0));

await enumerator.MoveNextAsync();
var frame2 = enumerator.Current;
await enumerator.DisposeAsync();

var busy = frame2.Processes.Values.Where(p => p.CpuPercent > 0).ToList();
Check("second frame has CPU rates", busy.Count > 0, $"{busy.Count} processes with measurable CPU");

var cores = Environment.ProcessorCount;
var overrun = frame2.Processes.Values.Where(p => p.CpuPercent > cores * 100.0 + 50).ToList();
Check("no process exceeds total machine capacity", overrun.Count == 0,
    overrun.Count > 0 ? $"{overrun[0].Name} at {overrun[0].CpuPercent:F1}%" : $"{cores} cores");

Console.WriteLine("\n  Top consumers:");
foreach (var p in frame2.Processes.Values.OrderByDescending(p => p.CpuPercent).Take(8))
{
    Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
        $"    {p.CpuPercent,7:F2}%  {p.Id.Pid,7}  {Truncate(p.Name, 24),-24}  " +
        $"{ValueFormat.Bytes(p.ResidentSize),10}  {p.UserName ?? p.Uid.ToString(CultureInfo.InvariantCulture)}"));
}

// ---------------------------------------------------------------------------
Console.WriteLine("\nDetail providers");
// ---------------------------------------------------------------------------

var selfId = self?.Id ?? new ProcessId(Environment.ProcessId, 0);

var threads = await sampler.ThreadsAsync(selfId);
Check("threads enumerate", threads.Count > 0, $"{threads.Count} threads");
Check("threads carry real detail, not stubs",
    threads.Any(t => !string.IsNullOrEmpty(t.Name)) && threads.Any(t => t.CpuTime > 0));
Check("main thread tid equals pid", threads.Any(t => (int)t.Tid == selfId.Pid));

var modules = await sampler.ModulesAsync(selfId);
Check("modules enumerate", modules.Count > 0, $"{modules.Count} mapped files");
// Match libc.so precisely: a bare "libc" substring also matches libclrjit.so,
// which made this check pass for the wrong reason and hid a truncated map list.
var libc = modules.FirstOrDefault(m =>
    m.Name.StartsWith("libc.so", StringComparison.Ordinal) ||
    m.Name.StartsWith("ld-linux", StringComparison.Ordinal) ||
    m.Name.StartsWith("ld-musl", StringComparison.Ordinal));
Check("libc is mapped", libc is not null, libc?.Path);
Check("module list is not truncated", modules.Count > 20, $"{modules.Count} modules");
Check("module sizes are non-zero", modules.All(m => m.Size > 0));

var fds = await sampler.FileDescriptorsAsync(selfId);
Check("file descriptors enumerate", fds.Count > 0, $"{fds.Count} descriptors");
Check("stdin/stdout/stderr present", fds.Any(f => f.Fd == 0) && fds.Any(f => f.Fd == 1));

var environment = await sampler.EnvironmentAsync(selfId);
Check("environment reads for own process", environment.Count > 0, $"{environment.Count} variables");
Check("PATH is present", environment.ContainsKey("PATH"));

var cwd = await sampler.CurrentDirectoryAsync(selfId);
Check("current directory resolves", cwd is not null, cwd);

// ---------------------------------------------------------------------------
Console.WriteLine("\nRestricted-file handling");
// ---------------------------------------------------------------------------

// pid 1 is root-owned, so on an unprivileged run its io and environ must be
// refused cleanly rather than throwing out of the sweep.
var runningAsRoot = Environment.IsPrivilegedProcess;
if (init is not null && !runningAsRoot)
{
    Check("other-user process still enumerates", init.Name.Length > 0, init.Name);
    Check("other-user process has a readable command line", !string.IsNullOrEmpty(init.CommandLine),
        "/proc/PID/cmdline is world-readable, unlike macOS");
    Check("other-user I/O counters degrade to null", init.DiskBytesRead is null);
    Check("other-user process flagged as limited", init.Flags.HasFlag(ProcessFlags.LimitedInfo));

    var threw = false;
    try
    {
        await sampler.EnvironmentAsync(init.Id);
    }
    catch (ProviderException e)
    {
        threw = e.Kind == ProviderErrorKind.NotPermitted;
    }

    Check("restricted environment raises NotPermitted", threw);
}
else
{
    Console.WriteLine("  [SKIP] running as root, restriction paths not exercised");
}

// ---------------------------------------------------------------------------
Console.WriteLine("\nClassification");
// ---------------------------------------------------------------------------

var services = snapshot.Processes.Values.Where(p => p.Flags.HasFlag(ProcessFlags.Service)).ToList();
Check("systemd services detected", services.Count > 0, $"{services.Count} services");
Check("services name their unit", services.Any(p => p.SystemdUnit is not null),
    services.FirstOrDefault(p => p.SystemdUnit is not null)?.SystemdUnit);

var kernelThreads = snapshot.Processes.Values.Where(p => p.Flags.HasFlag(ProcessFlags.KernelThread)).ToList();
Check("kernel threads detected", kernelThreads.Count > 0, $"{kernelThreads.Count} kernel threads");
Check("kernel threads have no image", kernelThreads.All(p => p.ExecutablePath is null));

// ---------------------------------------------------------------------------
Console.WriteLine("\nSystem statistics");
// ---------------------------------------------------------------------------

var statsProvider = new SystemStatsProvider();
statsProvider.Read();                       // first read primes the deltas
await Task.Delay(500);
var stats = await statsProvider.StatsAsync();

Check("memory total is plausible", stats.MemoryTotal > 1024UL * 1024 * 256,
    ValueFormat.Bytes(stats.MemoryTotal));
Check("memory used is below total", stats.MemoryUsed > 0 && stats.MemoryUsed < stats.MemoryTotal,
    $"{ValueFormat.Bytes(stats.MemoryUsed)} used");
Check("per-core CPU matches core count", stats.PerCoreCpuPercent.Count == Environment.ProcessorCount,
    $"{stats.PerCoreCpuPercent.Count} cores reported, {Environment.ProcessorCount} expected");
Check("CPU percentages are in range",
    stats.CpuTotalPercent is >= 0 and <= 100 && stats.PerCoreCpuPercent.All(c => c is >= 0 and <= 100),
    $"total {stats.CpuTotalPercent:F1}%");
Check("open file count is plausible", stats.HandleCount > 0, $"{stats.HandleCount} descriptors");

// MemTotal is the one figure we can check exactly against another tool.
var memTotalKb = File.ReadLines("/proc/meminfo").FirstOrDefault(l => l.StartsWith("MemTotal:", StringComparison.Ordinal));
if (memTotalKb is not null)
{
    var expectedBytes = ulong.Parse(memTotalKb.Split(':')[1].Trim().Split(' ')[0], CultureInfo.InvariantCulture) * 1024;
    Check("memory total matches /proc/meminfo exactly", stats.MemoryTotal == expectedBytes);
}

Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
    $"    CPU {stats.CpuTotalPercent,5:F1}%   RAM {ValueFormat.Bytes(stats.MemoryUsed)}/{ValueFormat.Bytes(stats.MemoryTotal)}   " +
    $"swap {ValueFormat.Bytes(stats.SwapUsed)}/{ValueFormat.Bytes(stats.SwapTotal)}"));
Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
    $"    disk {ValueFormat.Bytes(stats.DiskBytesPerSec)}/s   net {ValueFormat.Bytes(stats.NetworkBytesPerSec)}/s   " +
    $"cached {ValueFormat.Bytes(stats.MemoryCached)}   kernel {ValueFormat.Bytes(stats.MemoryKernel)}"));

// ---------------------------------------------------------------------------
Console.WriteLine("\nHardware");
// ---------------------------------------------------------------------------

var hardware = HardwareInfo.Gather();
Check("CPU model identified", hardware.CpuModel.Length > 0 && hardware.CpuModel != "Unknown CPU", hardware.CpuModel);
Check("logical cores match runtime", hardware.LogicalCores == Environment.ProcessorCount,
    $"{hardware.PhysicalCores} physical / {hardware.LogicalCores} logical");
Check("physical cores do not exceed logical", hardware.PhysicalCores <= hardware.LogicalCores);
Check("cache sizes read", hardware.L1DataCache is > 0 && hardware.L3Cache is > 0,
    $"L1d {ValueFormat.Bytes(hardware.L1DataCache)}, L2 {ValueFormat.Bytes(hardware.L2Cache)}, L3 {ValueFormat.Bytes(hardware.L3Cache)}");
Check("kernel version read", hardware.KernelVersion is { Length: > 0 }, hardware.KernelVersion);
Check("distribution identified", hardware.DistributionName is { Length: > 0 }, hardware.DistributionName);
Check("boot volume present", hardware.Volumes.Any(v => v.IsBootVolume),
    $"{hardware.Volumes.Count} real volumes");
Check("no pseudo filesystems listed", hardware.Volumes.All(v => v.FileSystem is not ("tmpfs" or "proc" or "sysfs" or "cgroup2")));
Check("network interfaces enumerated", hardware.NetworkInterfaces.Count > 0,
    string.Join(", ", hardware.NetworkInterfaces.Where(n => !n.IsLoopback).Select(n => n.Name)));
Check("GPU enumerated", hardware.Gpus.Count > 0,
    string.Join(", ", hardware.Gpus.Select(g => g.Name)));

var nproc = RunCommand("nproc", "")?.Trim();
if (int.TryParse(nproc, out var expectedCores))
{
    Check("core count matches nproc", hardware.LogicalCores == expectedCores, $"nproc says {expectedCores}");
}

// ---------------------------------------------------------------------------
Console.WriteLine("\nSweep cost");
// ---------------------------------------------------------------------------

Measure("default options", ProcSamplerOptions.Default);
Measure("without fd counting", ProcSamplerOptions.Default with { IncludeFileDescriptorCount = false });
Measure("with PSS (smaps_rollup)", ProcSamplerOptions.Default with { IncludeProportionalSetSize = true });
Measure("everything", ProcSamplerOptions.Full);

void Measure(string label, ProcSamplerOptions options)
{
    var s = new ProcSampler(options);
    s.SnapshotAsync().AsTask().GetAwaiter().GetResult();   // warm caches

    var timer = Stopwatch.StartNew();
    const int iterations = 3;
    for (var i = 0; i < iterations; i++)
    {
        s.SnapshotAsync().AsTask().GetAwaiter().GetResult();
    }

    timer.Stop();
    Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
        $"    {label,-28} {timer.Elapsed.TotalMilliseconds / iterations,7:F1} ms/sweep"));
}

// ---------------------------------------------------------------------------

Console.WriteLine();
Console.WriteLine(failures == 0 ? "All checks passed." : $"{failures} check(s) FAILED.");
return failures == 0 ? 0 : 1;

static string Truncate(string value, int max) =>
    value.Length <= max ? value : value[..(max - 1)] + "…";

static string? RunCommand(string file, string arguments)
{
    try
    {
        using var process = Process.Start(new ProcessStartInfo(file, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });

        if (process is null)
        {
            return null;
        }

        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit(5000);
        return output;
    }
    catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception)
    {
        return null;
    }
}
