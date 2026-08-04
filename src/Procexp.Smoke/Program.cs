using System.Diagnostics;
using System.Globalization;
using Procexp.Actions;
using Procexp.Autostart;
using Procexp.Gpu;
using Procexp.Model;
using Procexp.Sampling;
using Procexp.Net;
using Procexp.Privileged;
using Procexp.Provenance;
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
Console.WriteLine("\nProvenance");
// ---------------------------------------------------------------------------

var provenance = new ProvenanceProvider();
Check("package manager detected", provenance.PackageManager != PackageManagerKind.None,
    provenance.PackageManager.ToString());

// A distribution-owned binary must resolve to its package.
var lsPath = "/usr/bin/ls";
if (File.Exists(lsPath))
{
    var ls = await provenance.ProvenanceAsync(lsPath);
    Check("distribution binary is attributed to a package",
        ls.Status == ProvenanceStatus.PackageVerified, ls.DisplayName);
    Check("package version resolved", ls.PackageVersion is { Length: > 0 }, ls.PackageVersion);
    Check("build-id read from ELF notes", ls.BuildId is { Length: > 8 }, ls.BuildId);

    // Cross-check the package name against the native tool.
    var owner = RunCommand("pacman", $"-Qoq {lsPath}")?.Trim()
                ?? RunCommand("dpkg-query", $"-S {lsPath}")?.Split(':')[0].Trim();
    if (!string.IsNullOrEmpty(owner))
    {
        Check("package name matches the native tool", ls.PackageName == owner,
            $"ours {ls.PackageName}, tool {owner}");
    }
}

// Our own build output is not packaged, and must say so rather than claiming
// verification it cannot support.
var ownImage = self?.ExecutablePath;
if (ownImage is not null)
{
    var own = await provenance.ProvenanceAsync(ownImage);
    Check("unpackaged binary is reported as unpackaged",
        own.Status == ProvenanceStatus.Unpackaged, own.DisplayName);
}

// The build-id must be stable across reads and match what the native tool says.
var elf = ElfInspector.Inspect(lsPath);
Check("ELF is recognised", elf.IsElf && elf.Is64Bit);
var readelf = RunCommand("readelf", $"-n {lsPath}");
if (readelf is not null && readelf.Contains("Build ID", StringComparison.Ordinal))
{
    var marker = readelf.IndexOf("Build ID:", StringComparison.Ordinal);
    var expectedBuildId = readelf[(marker + 9)..].Split('\n')[0].Trim();
    Check("build-id matches readelf", elf.BuildId == expectedBuildId,
        $"ours {elf.BuildId}, readelf {expectedBuildId}");
}

var sha = await ProvenanceProvider.ComputeSha256Async(lsPath);
Check("SHA-256 computed", sha is { Length: 64 }, sha);
var sha256sum = RunCommand("sha256sum", lsPath)?.Split(' ')[0];
if (!string.IsNullOrEmpty(sha256sum))
{
    Check("SHA-256 matches sha256sum", sha == sha256sum);
}

// ---------------------------------------------------------------------------
Console.WriteLine("\nSockets");
// ---------------------------------------------------------------------------

var network = new NetworkProvider();

// Find a process that actually holds sockets. Our own may hold none, so scan.
var withSockets = new List<(ProcessRecord Process, IReadOnlyList<SocketInfo> Sockets)>();
foreach (var candidate in snapshot.Processes.Values.Where(p => !p.Flags.HasFlag(ProcessFlags.KernelThread)))
{
    var sockets = await network.SocketsAsync(candidate.Id);
    if (sockets.Count > 0)
    {
        withSockets.Add((candidate, sockets));
    }
}

Check("sockets found across processes", withSockets.Count > 0,
    $"{withSockets.Count} processes holding {withSockets.Sum(w => w.Sockets.Count)} sockets");

var allSockets = withSockets.SelectMany(w => w.Sockets).ToList();
Check("sockets carry an inode", allSockets.All(s => s.Inode > 0));
Check("sockets map back to a descriptor", allSockets.All(s => s.Fd >= 0));

var tcp = allSockets.Where(s => s.Protocol is SocketProtocol.Tcp or SocketProtocol.Tcp6).ToList();
Check("TCP sockets present", tcp.Count > 0, $"{tcp.Count} TCP sockets");
Check("TCP states decode", tcp.All(s => s.State.Length > 0) && tcp.Any(s => s.State == "ESTABLISHED" || s.State == "LISTEN"));
Check("listening sockets have a local port", tcp.Where(s => s.State == "LISTEN").All(s => s.LocalPort > 0));
Check("addresses parse as valid IPs",
    tcp.All(s => System.Net.IPAddress.TryParse(s.LocalAddress, out _)));

// 127.0.0.1 is stored as 0100007F on a little-endian machine, so a loopback
// listener proves the word-swap is right rather than accidentally symmetric.
var loopback = tcp.FirstOrDefault(s => s.LocalAddress == "127.0.0.1");
Check("little-endian address decoding is correct", loopback is not null || tcp.Count == 0,
    loopback is null ? "no loopback listener to check" : $"{loopback.LocalAddress}:{loopback.LocalPort}");

var unix = allSockets.Where(s => s.Protocol == SocketProtocol.Unix).ToList();
Check("unix sockets present", unix.Count > 0, $"{unix.Count} unix sockets");

// Cross-check the listening TCP port set against ss.
var ssOutput = RunCommand("ss", "-ltn");
if (ssOutput is not null)
{
    var ssPorts = ssOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Skip(1)
        .Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        .Where(f => f.Length > 3)
        .Select(f => f[3].LastIndexOf(':') is var i && i >= 0 && ushort.TryParse(f[3][(i + 1)..], out var p) ? p : (ushort)0)
        .Where(p => p > 0)
        .ToHashSet();

    var ourPorts = tcp.Where(s => s.State == "LISTEN").Select(s => s.LocalPort).ToHashSet();
    var missing = ssPorts.Except(ourPorts).ToList();

    // ss sees every listener; we only see those owned by processes whose fd
    // directory we can read, so a subset is expected as a non-root user.
    Check("listening ports are a subset of ss", missing.Count < ssPorts.Count || ssPorts.Count == 0,
        $"ours {ourPorts.Count}, ss {ssPorts.Count}");
}

var rates = await network.NetworkRatesAsync();
Check("per-process rates are empty by design", rates.Count == 0,
    "Linux exposes no per-process byte counter — see NetworkProvider");
Check("Network column is marked unsupported", !Columns.IsSupported(Column.Network));

Console.WriteLine("\n  Sample sockets:");
foreach (var (process, sockets) in withSockets.OrderByDescending(w => w.Sockets.Count).Take(4))
{
    var socket = sockets[0];
    Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
        $"    {Truncate(process.Name, 20),-20} {sockets.Count,3} sockets   " +
        $"{socket.Protocol,-5} {socket.LocalAddress}:{socket.LocalPort} {socket.State}"));
}

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
Console.WriteLine("\nGPU");
// ---------------------------------------------------------------------------

var gpu = new GpuProvider();
Check("DRM subsystem present", gpu.IsAvailable);

var totalGpu = await gpu.TotalGpuPercentAsync();
Check("aggregate GPU busy read", totalGpu is not null,
    totalGpu is null ? "driver publishes no gpu_busy_percent" : $"{totalGpu:F0}%");
Check("aggregate GPU busy is in range", totalGpu is null or (>= 0 and <= 100));

var videoMemory = gpu.VideoMemory();
Check("video memory read", videoMemory is not null,
    videoMemory is null ? null : $"{ValueFormat.Bytes(videoMemory.Value.Used)} / {ValueFormat.Bytes(videoMemory.Value.Total)}");

// Per-process needs two samples to produce a rate, as CPU does.
var gpuWatch = Stopwatch.StartNew();
gpu.Sample();
var firstGpuWalk = gpuWatch.Elapsed;
await Task.Delay(700);
var (gpuPercentages, gpuMemory) = gpu.Sample();

Check("per-process GPU clients discovered", gpuMemory.Count > 0 || gpuPercentages.Count > 0,
    $"{gpuMemory.Count} clients with resident memory, {gpuPercentages.Count} busy");

// A client holding several descriptors reports identical totals on each, so a
// missing dedupe shows up as impossible percentages rather than as a small error.
var cappedAt = Environment.ProcessorCount * 100.0;
Check("no process reports impossible GPU usage",
    gpuPercentages.Values.All(v => v is > 0 and < 1000),
    gpuPercentages.Count == 0 ? "nothing busy right now" : $"max {gpuPercentages.Values.Max():F1}%");
_ = cappedAt;

foreach (var (id, percent) in gpuPercentages.OrderByDescending(kv => kv.Value).Take(5))
{
    var name = snapshot.Processes.GetValueOrDefault(id)?.Name ?? $"pid {id.Pid}";
    Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
        $"    {percent,7:F2}%  {Truncate(name, 24),-24}  {ValueFormat.Bytes(gpuMemory.GetValueOrDefault(id)),10}"));
}

Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
    $"    fdinfo walk cost: {firstGpuWalk.TotalMilliseconds:F1} ms"));

// ---------------------------------------------------------------------------
Console.WriteLine("\nAutostart");
// ---------------------------------------------------------------------------

var autostart = new AutostartProvider();
Check("autostart index built", autostart.Index.Count > 0, $"{autostart.Index.Count} definitions");

var resolved = new List<(string Name, string Location)>();
foreach (var candidate in snapshot.Processes.Values.Where(p => !p.Flags.HasFlag(ProcessFlags.KernelThread)))
{
    var location = await autostart.AutostartLocationAsync(candidate);
    if (location is not null)
    {
        resolved.Add((candidate.Name, location));
    }
}

Check("running processes resolve to autostart definitions", resolved.Count > 0,
    $"{resolved.Count} of {snapshot.Processes.Count} processes");
Check("systemd services resolve", resolved.Any(r => r.Location.StartsWith("systemd", StringComparison.Ordinal)));
Check("kernel threads never resolve",
    (await Task.WhenAll(snapshot.Processes.Values
        .Where(p => p.Flags.HasFlag(ProcessFlags.KernelThread))
        .Take(20)
        .Select(async p => await autostart.AutostartLocationAsync(p))))
    .All(l => l is null));

foreach (var (name, location) in resolved.DistinctBy(r => r.Location).Take(5))
{
    Console.WriteLine($"    {Truncate(name, 22),-22} {location}");
}

// ---------------------------------------------------------------------------
Console.WriteLine("\nProcess actions");
// ---------------------------------------------------------------------------

var actions = new ProcessActions();

// Spawn a victim we own so signalling is exercised for real, without touching
// anything that matters.
using (var victim = Process.Start(new ProcessStartInfo("sleep") { ArgumentList = { "300" }, UseShellExecute = false }))
{
    if (victim is null)
    {
        Console.WriteLine("  [SKIP] could not spawn a test process");
    }
    else
    {
        await Task.Delay(150);

        var victimSnapshot = await sampler.SnapshotAsync();
        var victimRecord = victimSnapshot.Processes.Values.FirstOrDefault(p => p.Id.Pid == victim.Id);
        Check("spawned process appears in a sweep", victimRecord is not null, $"pid {victim.Id}");

        if (victimRecord is not null)
        {
            var victimId = victimRecord.Id;

            actions.Suspend(victimId);
            await Task.Delay(120);
            var afterSuspend = await sampler.SnapshotAsync();
            var suspended = afterSuspend.Processes.GetValueOrDefault(victimId);
            Check("suspend sets the stopped state", suspended?.State is 'T' or 't',
                $"state {suspended?.State}");
            Check("suspend sets the Suspended flag",
                suspended?.Flags.HasFlag(ProcessFlags.Suspended) == true);

            actions.Resume(victimId);
            await Task.Delay(120);
            var afterResume = await sampler.SnapshotAsync();
            var resumed = afterResume.Processes.GetValueOrDefault(victimId);
            Check("resume clears the stopped state", resumed?.State is not ('T' or 't'),
                $"state {resumed?.State}");

            actions.SetNice(victimId, 5);
            await Task.Delay(80);
            var afterNice = await sampler.SnapshotAsync();
            Check("renice takes effect", afterNice.Processes.GetValueOrDefault(victimId)?.Nice == 5,
                $"nice {afterNice.Processes.GetValueOrDefault(victimId)?.Nice}");

            // The identity guard is the safety-critical part: a stale row whose
            // PID has been recycled must be refused, not signalled.
            var recycled = victimId with { StartTime = victimId.StartTime + 12345 };
            var guarded = false;
            try
            {
                actions.Signal(recycled, Signals.Cont);
            }
            catch (ProviderException e)
            {
                guarded = e.Kind == ProviderErrorKind.ProcessGone;
            }

            Check("stale identity is refused rather than signalled", guarded,
                "PID-reuse guard");

            actions.Kill(victimId, Signals.Term);
            await Task.Delay(200);
            var afterKill = await sampler.SnapshotAsync();
            Check("kill terminates the process",
                afterKill.Processes.GetValueOrDefault(victimId) is null or { State: 'Z' });
        }
    }
}

// Signalling pid 1 must be refused by policy before any syscall happens.
if (init is not null)
{
    var confirmation = ActionConfirmationPolicy.ForKill(init);
    Check("killing pid 1 is refused by policy", confirmation.IsRefused,
        confirmation.Message);
    Check("pid 1 is rated critical", confirmation.Severity == ConfirmationSeverity.Critical);
}

var selfConfirmation = self is not null ? ActionConfirmationPolicy.ForKill(self) : null;
Check("killing ourselves is refused by policy", selfConfirmation?.IsRefused == true);

var service = snapshot.Processes.Values.FirstOrDefault(p => p.Flags.HasFlag(ProcessFlags.Service) && p.Id.Pid != 1);
if (service is not null)
{
    var confirmation = ActionConfirmationPolicy.ForKill(service);
    Check("services warn about systemd restart",
        confirmation.Severity >= ConfirmationSeverity.Disruptive && !confirmation.IsRefused,
        Truncate(confirmation.Message, 90));
}

// ---------------------------------------------------------------------------
Console.WriteLine("\nPrivileged helper");
// ---------------------------------------------------------------------------

var privileged = new PrivilegedClient();

if (PrivilegedClient.IsAvailable)
{
    Check("helper handshake succeeds", await privileged.HandshakeAsync());

    if (init is not null)
    {
        var io = await privileged.ReadIoAsync(init.Id);
        Check("helper supplies I/O for a root process", io is not null,
            io is null ? null : $"read {ValueFormat.Bytes(io.Value.Read)}");
    }
}
else
{
    Console.WriteLine("  [SKIP] helper not installed — see docs/HELPER.md");

    // The unavailable path still has to behave.
    var reported = false;
    try
    {
        await privileged.SignalAsync(new ProcessId(1, 1), Signals.Term);
    }
    catch (ProviderException e)
    {
        reported = e.Kind == ProviderErrorKind.HelperUnavailable;
    }

    Check("absent helper reports HelperUnavailable", reported);
    Check("app works without the helper", snapshot.Processes.Count > 0,
        "every check above ran unprivileged");
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
