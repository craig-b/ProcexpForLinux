using Procexp.Model;

namespace Procexp.App.Controls;

/// <summary>
/// Tracks which rows of a list are newly arrived or recently gone, so the lower
/// pane can tint them the way the process list does.
/// </summary>
/// <remarks>
/// The same shape as the process list's highlighting, and for the same reason —
/// a row is only "new" relative to a list the viewer has already seen. Watching a
/// process load and unload libraries, or open and close descriptors, is one of
/// the main things the lower pane is for, and those events are invisible without
/// this.
/// </remarks>
public sealed class RowChangeTracker<TKey>
    where TKey : notnull
{
    private readonly Dictionary<TKey, DateTimeOffset> _appeared = [];
    private readonly Dictionary<TKey, DateTimeOffset> _departed = [];

    private HashSet<TKey> _previous = [];
    private bool _hasBaseline;

    public TimeSpan HighlightDuration { get; set; } = TimeSpan.FromSeconds(2);

    public bool IsEnabled { get; set; } = true;

    /// <summary>Forget everything, for when the pane switches to another process.</summary>
    public void Reset()
    {
        _appeared.Clear();
        _departed.Clear();
        _previous = [];
        _hasBaseline = false;
    }

    /// <summary>
    /// Record the current row set.
    /// </summary>
    /// <returns>Keys of rows that have gone but should still be shown.</returns>
    public IReadOnlyCollection<TKey> Observe(IEnumerable<TKey> keys, DateTimeOffset now)
    {
        if (!IsEnabled)
        {
            return [];
        }

        var current = keys.ToHashSet();

        // The first observation establishes the baseline. Without this every row
        // would flash green the moment a process is selected, which says nothing.
        if (!_hasBaseline)
        {
            _hasBaseline = true;
            _previous = current;
            return [];
        }

        foreach (var key in current.Except(_previous))
        {
            _departed.Remove(key);
            _appeared[key] = now;
        }

        foreach (var key in _previous.Except(current))
        {
            _departed[key] = now;
        }

        _previous = current;
        Expire(now);

        return _departed.Keys.ToList();
    }

    /// <summary>Drop expired highlights. True when anything changed.</summary>
    public bool Expire(DateTimeOffset now)
    {
        var before = _appeared.Count + _departed.Count;

        foreach (
            var key in _appeared
                .Where(e => now - e.Value > HighlightDuration)
                .Select(e => e.Key)
                .ToList()
        )
        {
            _appeared.Remove(key);
        }

        foreach (
            var key in _departed
                .Where(e => now - e.Value > HighlightDuration)
                .Select(e => e.Key)
                .ToList()
        )
        {
            _departed.Remove(key);
        }

        return _appeared.Count + _departed.Count != before;
    }

    public bool IsNew(TKey key) => _appeared.ContainsKey(key);

    public bool IsGone(TKey key) => _departed.ContainsKey(key);

    /// <summary>Row tint for a key, or null to leave the default background.</summary>
    public Rgba? Colour(TKey key, IReadOnlyList<ProcessColorRule> rules, bool darkMode)
    {
        if (IsGone(key))
        {
            return ProcessColorRule.Background(ProcessFlags.DeadProcess, rules, darkMode);
        }

        return IsNew(key)
            ? ProcessColorRule.Background(ProcessFlags.NewProcess, rules, darkMode)
            : null;
    }
}
