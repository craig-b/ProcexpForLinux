namespace Procexp.Model;

/// <summary>
/// Stable identity for a process across refreshes.
/// </summary>
/// <remarks>
/// PIDs are recycled by the kernel, so identity is the pair
/// (<paramref name="Pid"/>, <paramref name="StartTime"/>). All diffing — new and
/// dead row highlighting, tree stability, CPU deltas — keys on this rather than
/// on the PID alone.
///
/// On Linux <see cref="StartTime"/> is field 22 of <c>/proc/PID/stat</c>, the
/// process start time in clock ticks since boot. It is used only to disambiguate
/// recycled PIDs and is never displayed; <see cref="ProcessRecord.StartTime"/>
/// carries the wall-clock value for that.
/// </remarks>
public readonly record struct ProcessId(int Pid, ulong StartTime)
{
    public override string ToString() => $"pid {Pid}@{StartTime}";
}
