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
    public const int Term = 15;
    public const int Cont = 18;
    public const int Stop = 19;
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
}
