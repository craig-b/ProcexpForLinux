using System.Diagnostics;
using Procexp.Model;

namespace Procexp.Sampling;

/// <summary>
/// Turns the kernel's monotonically-increasing per-process CPU counters into an
/// instantaneous percentage, by remembering the previous sweep.
/// </summary>
/// <remarks>
/// The result is normalised so 100.0 is one fully-busy core, matching Process
/// Explorer and top's Irix mode: a process saturating four cores reads ~400.
///
/// Keyed on <see cref="ProcessId"/> rather than the bare PID, so a recycled PID
/// starts from zero instead of inheriting the previous occupant's counter and
/// producing a nonsense spike.
/// </remarks>
internal sealed class CpuDeltaTracker
{
    private readonly Lock _gate = new();
    private Dictionary<ProcessId, ulong> _previous = [];
    private long _previousTimestamp;

    /// <summary>
    /// Given the current cumulative CPU times, return per-process percentages.
    /// The first call after construction or <see cref="Reset"/> yields zeros,
    /// because a rate needs two samples.
    /// </summary>
    internal Dictionary<ProcessId, double> Percentages(Dictionary<ProcessId, ulong> current)
    {
        var now = Stopwatch.GetTimestamp();
        var result = new Dictionary<ProcessId, double>(current.Count);

        lock (_gate)
        {
            var elapsedNanos = _previousTimestamp == 0
                ? 0
                : (ulong)(Stopwatch.GetElapsedTime(_previousTimestamp, now).TotalNanoseconds);

            foreach (var (id, cpu) in current)
            {
                if (elapsedNanos > 0 && _previous.TryGetValue(id, out var previous) && cpu >= previous)
                {
                    result[id] = (cpu - previous) / (double)elapsedNanos * 100.0;
                }
                else
                {
                    result[id] = 0;
                }
            }

            _previous = current;
            _previousTimestamp = now;
        }

        return result;
    }

    internal void Reset()
    {
        lock (_gate)
        {
            _previous = [];
            _previousTimestamp = 0;
        }
    }
}
