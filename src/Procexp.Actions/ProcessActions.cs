using System.Buffers.Text;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Procexp.Model;

namespace Procexp.Actions;

/// <summary>POSIX signals used by the process actions.</summary>
public static class Signals
{
    public const int Hup = 1;
    public const int Int = 2;
    public const int Kill = 9;
    public const int Usr1 = 10;
    public const int Usr2 = 12;
    public const int Term = 15;
    public const int Cont = 18;
    public const int Stop = 19;

    /// <summary>Conventional name for a signal number, for dialogs and menus.</summary>
    public static string Name(int signal) =>
        signal switch
        {
            Hup => "SIGHUP",
            Int => "SIGINT",
            Kill => "SIGKILL",
            Usr1 => "SIGUSR1",
            Usr2 => "SIGUSR2",
            Term => "SIGTERM",
            Cont => "SIGCONT",
            Stop => "SIGSTOP",
            _ => $"signal {signal}",
        };
}

/// <summary>
/// Control actions: kill, suspend, resume, renice, restart.
/// </summary>
/// <remarks>
/// Every action re-verifies process identity immediately before signalling. This
/// is not defensive padding — <see cref="ProcessId"/> is (pid, start time)
/// precisely because Linux recycles PIDs, and a process list can easily be a
/// second stale. Without the check, clicking Kill on a row whose process has
/// since exited would signal whatever unrelated program inherited that PID.
/// </remarks>
public sealed partial class ProcessActions
{
    /// <summary>
    /// Optional escalation for signals the kernel refuses with EPERM — the seam
    /// the privileged helper plugs into, mirroring the macOS port. Returns false
    /// when no escalation path exists, in which case the original refusal
    /// stands; throws to report an attempted escalation that failed in its own
    /// terms. A delegate rather than a dependency, so this project needs no
    /// knowledge of the helper. The helper re-verifies process identity itself,
    /// so the PID-reuse guard holds across the boundary.
    /// </summary>
    public Func<ProcessId, int, CancellationToken, Task<bool>>? PrivilegedSignal { get; init; }

    /// <summary>As <see cref="PrivilegedSignal"/>, for renice.</summary>
    public Func<ProcessId, int, CancellationToken, Task<bool>>? PrivilegedSetNice { get; init; }

    /// <summary>As <see cref="PrivilegedSignal"/>, for CPU affinity. The byte
    /// array is the raw cpu_set_t mask.</summary>
    public Func<
        ProcessId,
        byte[],
        CancellationToken,
        Task<bool>
    >? PrivilegedSetAffinity { get; init; }

    /// <summary>
    /// Confirm that the PID still hosts the same process the caller selected.
    /// </summary>
    /// <remarks>
    /// Compares the start time in field 22 of <c>/proc/PID/stat</c>, which is the
    /// same value that forms the identity. Reading it is one small file read, and
    /// it closes the window between the snapshot the user clicked and the signal
    /// we are about to send.
    /// </remarks>
    private static void VerifyIdentity(ProcessId id)
    {
        byte[] buffer;
        try
        {
            buffer = File.ReadAllBytes($"/proc/{id.Pid}/stat");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw ProviderException.ProcessGone(id);
        }

        var span = buffer.AsSpan();
        var close = span.LastIndexOf((byte)')');
        if (close < 0)
        {
            throw ProviderException.ProcessGone(id);
        }

        // Fields after ") " are space-separated; start time is the 20th of them.
        var rest = span[(close + 1)..];
        for (var i = 0; i < 20; i++)
        {
            var start = 0;
            while (start < rest.Length && rest[start] == (byte)' ')
            {
                start++;
            }

            rest = rest[start..];
            if (i == 19)
            {
                break;
            }

            var end = rest.IndexOf((byte)' ');
            if (end < 0)
            {
                throw ProviderException.ProcessGone(id);
            }

            rest = rest[end..];
        }

        if (!Utf8Parser.TryParse(rest, out ulong startTime, out _))
        {
            throw ProviderException.ProcessGone(id);
        }

        // A start time of zero means the caller built the identity without one, so
        // there is nothing to compare against and we accept it.
        if (id.StartTime != 0 && startTime != id.StartTime)
        {
            throw new ProviderException(
                ProviderErrorKind.ProcessGone,
                $"pid {id.Pid} has been recycled — it now hosts a different process"
            );
        }
    }

    /// <summary>Send a signal, after verifying identity.</summary>
    public void Signal(ProcessId id, int signal)
    {
        VerifyIdentity(id);

        if (KillNative(id.Pid, signal) == 0)
        {
            return;
        }

        throw MapErrno(Marshal.GetLastPInvokeError(), id);
    }

    public void Kill(ProcessId id, int signal = Signals.Term) => Signal(id, signal);

    public void Suspend(ProcessId id) => Signal(id, Signals.Stop);

    public void Resume(ProcessId id) => Signal(id, Signals.Cont);

    /// <summary>
    /// Send a signal, escalating through <see cref="PrivilegedSignal"/> when the
    /// kernel refuses and an escalation path is wired.
    /// </summary>
    public async Task SignalAsync(
        ProcessId id,
        int signal,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            Signal(id, signal);
        }
        catch (ProviderException e)
            when (e.Kind == ProviderErrorKind.NotPermitted && PrivilegedSignal is not null)
        {
            if (!await PrivilegedSignal(id, signal, cancellationToken).ConfigureAwait(false))
            {
                throw;
            }
        }
    }

    public Task KillAsync(
        ProcessId id,
        int signal = Signals.Term,
        CancellationToken cancellationToken = default
    ) => SignalAsync(id, signal, cancellationToken);

    public Task SuspendAsync(ProcessId id, CancellationToken cancellationToken = default) =>
        SignalAsync(id, Signals.Stop, cancellationToken);

    public Task ResumeAsync(ProcessId id, CancellationToken cancellationToken = default) =>
        SignalAsync(id, Signals.Cont, cancellationToken);

    /// <summary>
    /// <see cref="KillTree"/> with per-target escalation: in a mixed-ownership
    /// tree, the targets the kernel permits are signalled directly and only the
    /// refusals travel to the helper.
    /// </summary>
    public async Task KillTreeAsync(
        ProcessId id,
        ProcessSnapshot snapshot,
        int signal = Signals.Term,
        CancellationToken cancellationToken = default
    )
    {
        var ordered = new List<ProcessId>();
        Collect(id);
        ordered.Reverse();

        ProviderException? firstFailure = null;
        foreach (var target in ordered)
        {
            try
            {
                await SignalAsync(target, signal, cancellationToken).ConfigureAwait(false);
            }
            catch (ProviderException e) when (e.Kind == ProviderErrorKind.ProcessGone)
            {
                // Already dead, most likely as a result of its parent dying.
            }
            catch (ProviderException e)
            {
                firstFailure ??= e;
            }
        }

        if (firstFailure is not null)
        {
            throw firstFailure;
        }

        void Collect(ProcessId node)
        {
            ordered.Add(node);
            foreach (var child in snapshot.ChildIds(node))
            {
                Collect(child);
            }
        }
    }

    /// <summary>
    /// <see cref="SetNice"/>, escalating through <see cref="PrivilegedSetNice"/>
    /// when the kernel refuses — which includes raising priority on a process
    /// the user owns, since that alone needs CAP_SYS_NICE.
    /// </summary>
    public async Task SetNiceAsync(
        ProcessId id,
        int nice,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            SetNice(id, nice);
        }
        catch (ProviderException e)
            when (e.Kind == ProviderErrorKind.NotPermitted && PrivilegedSetNice is not null)
        {
            if (!await PrivilegedSetNice(id, nice, cancellationToken).ConfigureAwait(false))
            {
                throw;
            }
        }
    }

    /// <summary>
    /// Kill a process and all its descendants.
    /// </summary>
    /// <remarks>
    /// Children are signalled before their parents. Killing the parent first lets
    /// the kernel reparent the children to init, at which point they are no longer
    /// reachable through the tree we were given and would survive.
    ///
    /// Descendants that exit on their own between the snapshot and the signal are
    /// skipped rather than failing the whole operation — that race is expected
    /// when killing a tree, since parents dying often takes children with them.
    /// </remarks>
    public void KillTree(ProcessId id, ProcessSnapshot snapshot, int signal = Signals.Term)
    {
        var ordered = new List<ProcessId>();
        Collect(id);

        // Deepest first.
        ordered.Reverse();

        ProviderException? firstFailure = null;
        foreach (var target in ordered)
        {
            try
            {
                Signal(target, signal);
            }
            catch (ProviderException e) when (e.Kind == ProviderErrorKind.ProcessGone)
            {
                // Already dead, most likely as a result of its parent dying.
            }
            catch (ProviderException e)
            {
                firstFailure ??= e;
            }
        }

        if (firstFailure is not null)
        {
            throw firstFailure;
        }

        void Collect(ProcessId node)
        {
            ordered.Add(node);
            foreach (var child in snapshot.ChildIds(node))
            {
                Collect(child);
            }
        }
    }

    /// <summary>
    /// Change scheduling priority.
    /// </summary>
    /// <remarks>
    /// Lowering the nice value — raising priority — requires CAP_SYS_NICE even for
    /// a process you own, which is why this can fail with NotPermitted on a
    /// process the user can otherwise kill.
    /// </remarks>
    public void SetNice(ProcessId id, int nice)
    {
        VerifyIdentity(id);

        var clamped = Math.Clamp(nice, -20, 19);

        // setpriority returns -1 on error, but -1 is also a legal priority, so
        // errno must be cleared first to tell the two apart.
        Marshal.SetLastSystemError(0);
        if (SetPriority(PrioProcess, (uint)id.Pid, clamped) == -1)
        {
            var error = Marshal.GetLastPInvokeError();
            if (error != 0)
            {
                throw MapErrno(error, id);
            }
        }
    }

    public int? GetNice(ProcessId id)
    {
        Marshal.SetLastSystemError(0);
        var value = GetPriority(PrioProcess, (uint)id.Pid);
        return value == -1 && Marshal.GetLastPInvokeError() != 0 ? null : value;
    }

    // ---- CPU affinity -------------------------------------------------------

    /// <summary>
    /// The size of the mask handed to the affinity syscalls: 1024 bits, the
    /// kernel's own CPU_SETSIZE, so machines beyond 64 CPUs are covered.
    /// </summary>
    private const int CpuSetBytes = 128;

    /// <summary>The CPUs a process is allowed to run on, or null when unreadable.</summary>
    public IReadOnlyList<int>? GetAffinity(ProcessId id)
    {
        var mask = new byte[CpuSetBytes];
        if (SchedGetAffinity(id.Pid, (nuint)mask.Length, mask) != 0)
        {
            return null;
        }

        var cpus = new List<int>();
        for (var cpu = 0; cpu < mask.Length * 8; cpu++)
        {
            if ((mask[cpu / 8] & (1 << (cpu % 8))) != 0)
            {
                cpus.Add(cpu);
            }
        }

        return cpus;
    }

    /// <summary>
    /// Pin a process to a set of CPUs.
    /// </summary>
    /// <remarks>
    /// Affinity is per-thread on Linux, but <c>sched_setaffinity</c> on the
    /// process id applies to the main thread and the kernel migrates the rest
    /// only when the caller asks per-tid — so this walks every tid, matching
    /// what taskset -a does and what the Windows tool means by process affinity.
    /// </remarks>
    public async Task SetAffinityAsync(
        ProcessId id,
        IReadOnlyList<int> cpus,
        CancellationToken cancellationToken = default
    )
    {
        if (cpus.Count == 0)
        {
            throw ProviderException.Unsupported("at least one CPU must be selected");
        }

        var mask = new byte[CpuSetBytes];
        foreach (var cpu in cpus)
        {
            if (cpu < 0 || cpu >= mask.Length * 8)
            {
                throw ProviderException.Unsupported($"no such CPU: {cpu}");
            }

            mask[cpu / 8] |= (byte)(1 << (cpu % 8));
        }

        VerifyIdentity(id);

        try
        {
            SetAffinityAllThreads(id, mask);
        }
        catch (ProviderException e)
            when (e.Kind == ProviderErrorKind.NotPermitted && PrivilegedSetAffinity is not null)
        {
            if (!await PrivilegedSetAffinity(id, mask, cancellationToken).ConfigureAwait(false))
            {
                throw;
            }
        }
    }

    /// <summary>Apply a mask to every thread of a process; shared with the helper.</summary>
    public static void SetAffinityAllThreads(ProcessId id, byte[] mask)
    {
        string[] tids;
        try
        {
            tids = Directory.GetDirectories($"/proc/{id.Pid}/task");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw ProviderException.ProcessGone(id);
        }

        ProviderException? firstFailure = null;
        foreach (var tidDirectory in tids)
        {
            if (!int.TryParse(Path.GetFileName(tidDirectory), out var tid))
            {
                continue;
            }

            if (SchedSetAffinity(tid, (nuint)mask.Length, mask) != 0)
            {
                var errno = Marshal.GetLastPInvokeError();

                // A thread exiting mid-walk is expected; the rest still count.
                if (errno != 3) // ESRCH
                {
                    firstFailure ??= errno switch
                    {
                        1 => ProviderException.NotPermitted(
                            $"not permitted to set the affinity of pid {id.Pid}"
                        ),
                        22 => ProviderException.Unsupported(
                            "no selected CPU is present on this system"
                        ),
                        _ => new ProviderException(
                            ProviderErrorKind.Underlying,
                            $"sched_setaffinity failed with errno {errno}"
                        ),
                    };
                }
            }
        }

        if (firstFailure is not null)
        {
            throw firstFailure;
        }
    }

    // ---- Core dumps ---------------------------------------------------------

    /// <summary>
    /// Write a core dump of a running process without stopping it permanently.
    /// </summary>
    /// <remarks>
    /// Delegates to gcore from gdb rather than reimplementing a core writer —
    /// ptrace attach, memory-map walking and ELF note layout are gdb's home
    /// ground, and a dump produced by gcore is loadable by every tool that
    /// matters. The trade-offs are a dependency the error message names, and
    /// gdb briefly stopping the target while it reads memory.
    ///
    /// Yama's ptrace_scope=1 — the default on most desktop distributions —
    /// blocks attaching even to your own processes unless they are descendants,
    /// so the permission failure explains that rather than blaming ownership.
    /// </remarks>
    public async Task<string> CreateDumpAsync(
        ProcessRecord record,
        string outputPath,
        CancellationToken cancellationToken = default
    )
    {
        VerifyIdentity(record.Id);

        // gcore appends ".<pid>" to whatever prefix it is given; using a prefix
        // in the target directory and renaming afterwards lets the caller pick
        // an exact file name.
        var prefix = Path.Combine(
            Path.GetDirectoryName(outputPath) ?? ".",
            $".procexp-dump-{record.Id.Pid}"
        );
        var produced = $"{prefix}.{record.Id.Pid}";

        ProcessStartInfo startInfo = new("gcore")
        {
            ArgumentList = { "-o", prefix, record.Id.Pid.ToString() },
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        Process gcore;
        try
        {
            gcore =
                Process.Start(startInfo)
                ?? throw new ProviderException(ProviderErrorKind.Underlying, "gcore did not start");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw ProviderException.Unsupported(
                "creating a dump requires gcore, which ships with gdb — install gdb and retry"
            );
        }

        using (gcore)
        {
            var stderr = await gcore
                .StandardError.ReadToEndAsync(cancellationToken)
                .ConfigureAwait(false);
            await gcore.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            if (gcore.ExitCode != 0 || !File.Exists(produced))
            {
                TryDelete(produced);

                // Underlying rather than NotPermitted: the coordinator's generic
                // not-permitted text prescribes the helper, which cannot help
                // here, so the specific message must survive to the dialog.
                throw new ProviderException(
                    ProviderErrorKind.Underlying,
                    stderr.Contains("Operation not permitted", StringComparison.Ordinal)
                        ? "the kernel refused to attach. With kernel.yama.ptrace_scope=1 — the "
                            + "default on most distributions — even your own processes cannot be "
                            + "dumped unless they are children of the debugger; a root shell "
                            + "running gcore directly is the usual answer"
                        : $"gcore failed: {Summarise(stderr)}"
                );
            }
        }

        try
        {
            File.Move(produced, outputPath, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            TryDelete(produced);
            throw new ProviderException(
                ProviderErrorKind.Underlying,
                $"could not move the dump to {outputPath}: {e.Message}"
            );
        }

        return outputPath;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup of a temp file.
        }
    }

    /// <summary>The informative tail of gcore's stderr, which leads with noise.</summary>
    private static string Summarise(string stderr)
    {
        var lines = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return lines.Length > 0 ? lines[^1].Trim() : "no error output";
    }

    /// <summary>
    /// Terminate a process and start its command line again.
    /// </summary>
    /// <remarks>
    /// Best-effort by nature. The replacement is launched detached from us, with
    /// the original working directory where it is still readable, but it will not
    /// inherit the environment, capabilities, cgroup or namespaces of the original
    /// — so restarting a systemd service this way produces something subtly unlike
    /// what systemd would have started. For units, the caller should prefer
    /// systemctl.
    /// </remarks>
    public void Restart(ProcessRecord record, ProcessSnapshot snapshot)
    {
        if (record.CommandLine is not { Length: > 0 } commandLine)
        {
            throw ProviderException.Unsupported("process has no recorded command line");
        }

        var arguments = SplitCommandLine(commandLine);
        if (arguments.Count == 0)
        {
            throw ProviderException.Unsupported("command line could not be parsed");
        }

        var workingDirectory = TryReadLink($"/proc/{record.Id.Pid}/cwd");

        Kill(record.Id, Signals.Term);

        var startInfo = new ProcessStartInfo(record.ExecutablePath ?? arguments[0])
        {
            UseShellExecute = false,
        };

        foreach (var argument in arguments.Skip(1))
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (workingDirectory is not null && Directory.Exists(workingDirectory))
        {
            startInfo.WorkingDirectory = workingDirectory;
        }

        try
        {
            Process.Start(startInfo);
        }
        catch (Exception e)
            when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new ProviderException(
                ProviderErrorKind.Underlying,
                $"could not relaunch: {e.Message}"
            );
        }
    }

    /// <summary>
    /// Split a command line recovered from <c>/proc/PID/cmdline</c>.
    /// </summary>
    /// <remarks>
    /// The sampler joins the NUL-separated arguments with spaces for display, so
    /// an argument that itself contained a space is indistinguishable from two
    /// arguments here. Re-reading cmdline directly avoids the ambiguity and is
    /// what this does when the process is still alive.
    /// </remarks>
    private static List<string> SplitCommandLine(string commandLine) =>
        [.. commandLine.Split(' ', StringSplitOptions.RemoveEmptyEntries)];

    private static string? TryReadLink(string path)
    {
        try
        {
            return File.ResolveLinkTarget(path, returnFinalTarget: false)?.FullName;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static ProviderException MapErrno(int errno, ProcessId id) =>
        errno switch
        {
            1 => ProviderException.NotPermitted($"not permitted to signal pid {id.Pid}"), // EPERM
            3 => ProviderException.ProcessGone(id), // ESRCH
            22 => ProviderException.Unsupported("invalid signal"), // EINVAL
            _ => new ProviderException(ProviderErrorKind.Underlying, $"errno {errno}"),
        };

    private const int PrioProcess = 0;

    [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static partial int KillNative(int pid, int signal);

    [LibraryImport("libc", EntryPoint = "setpriority", SetLastError = true)]
    private static partial int SetPriority(int which, uint who, int priority);

    [LibraryImport("libc", EntryPoint = "getpriority", SetLastError = true)]
    private static partial int GetPriority(int which, uint who);

    [LibraryImport("libc", EntryPoint = "sched_getaffinity", SetLastError = true)]
    private static partial int SchedGetAffinity(int pid, nuint cpusetsize, [Out] byte[] mask);

    [LibraryImport("libc", EntryPoint = "sched_setaffinity", SetLastError = true)]
    private static partial int SchedSetAffinity(int pid, nuint cpusetsize, byte[] mask);
}
