using Procexp.Model;

namespace Procexp.Sampling;

/// <summary>
/// Per-thread detail, from <c>/proc/PID/task/TID</c>.
/// </summary>
/// <remarks>
/// This is the clearest win of the whole port. The macOS implementation needs a
/// task port to enumerate threads, which the kernel refuses for protected and
/// other-user processes — so it falls back to emitting one empty stub row per
/// live thread. Linux exposes the whole thing to any reader, so the Threads tab
/// shows real per-thread CPU, state and wait reason for every process.
/// </remarks>
internal static class ProcThreads
{
    internal static IReadOnlyList<ThreadInfo> Read(ProcessId id)
    {
        var taskDirectory = $"/proc/{id.Pid}/task";

        string[] entries;
        try
        {
            entries = Directory.GetDirectories(taskDirectory);
        }
        catch (DirectoryNotFoundException)
        {
            // The process exited.
            return [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        var result = new List<ThreadInfo>(entries.Length);
        var buffer = ProcFile.RentBuffer();

        try
        {
            foreach (var entry in entries)
            {
                var name = Path.GetFileName(entry);
                if (!ulong.TryParse(name, out var tid))
                {
                    continue;
                }

                if (
                    !ProcFile.TryRead($"{entry}/stat", ref buffer, out var length)
                    || !ProcStat.TryParse(buffer.AsSpan(0, length), out var stat)
                )
                {
                    // Thread exited mid-enumeration.
                    continue;
                }

                string? wchan = null;
                if (ProcFile.TryRead($"{entry}/wchan", ref buffer, out length) && length > 0)
                {
                    var value = ProcFile.ToString(buffer.AsSpan(0, length)).Trim('\0', '\n');
                    // "0" means the thread is running rather than blocked.
                    if (value is not ("0" or ""))
                    {
                        wchan = value;
                    }
                }

                var userNanos = stat.UserTimeTicks * NativeMethods.NanosPerTick;
                var systemNanos = stat.SystemTimeTicks * NativeMethods.NanosPerTick;

                result.Add(
                    new ThreadInfo
                    {
                        Tid = tid,
                        Name = stat.Comm,
                        CpuTime = userNanos + systemNanos,
                        UserTime = userNanos,
                        KernelTime = systemNanos,
                        State = ValueFormat.ProcessState(stat.State),
                        StateChar = stat.State,
                        WaitChannel = wchan,
                        Priority = stat.Priority,
                        Nice = stat.Nice,
                        RealtimePriority = stat.RealtimePriority,
                        SchedulingPolicy = stat.Policy,
                        LastCpu = stat.Processor,
                        MinorFaults = stat.MinorFaults,
                        MajorFaults = stat.MajorFaults,
                    }
                );
            }
        }
        finally
        {
            ProcFile.ReturnBuffer(buffer);
        }

        result.Sort(static (a, b) => a.Tid.CompareTo(b.Tid));
        return result;
    }
}
