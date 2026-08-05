using Procexp.Model;

namespace Procexp.App;

/// <summary>
/// Rolling record of system-wide statistics, collected once per sweep for as
/// long as the app runs.
/// </summary>
/// <remarks>
/// The System Information window used to start sampling only when it opened,
/// so its graphs began empty every time. The sweep now records here
/// continuously — the same read that feeds the toolbar sparklines — and the
/// window seeds its graphs from the entries on open, then follows
/// <see cref="Recorded"/>. Capacity is one more than the largest graph so the
/// oldest visible sample still has its hover label.
/// </remarks>
public sealed class SystemHistory
{
    public const int Capacity = 200;

    /// <summary>
    /// One sweep's statistics, plus the busiest processes at that instant
    /// pre-formatted for the CPU and memory graphs' hover readout.
    /// </summary>
    public readonly record struct Entry(SystemStats Stats, string? TopCpu, string? TopMemory);

    private readonly List<Entry> _entries = [];

    public IReadOnlyList<Entry> Entries => _entries;

    /// <summary>Raised after an entry lands, on the thread that recorded it.</summary>
    public event Action<Entry>? Recorded;

    public void Record(Entry entry)
    {
        _entries.Add(entry);
        while (_entries.Count > Capacity)
        {
            _entries.RemoveAt(0);
        }

        Recorded?.Invoke(entry);
    }
}
