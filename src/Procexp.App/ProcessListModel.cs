using Procexp.Model;

namespace Procexp.App;

/// <summary>
/// Owns the displayed process list: the current snapshot, which rows are
/// collapsed, and the new/dead highlighting that Process Explorer fades in and
/// out.
/// </summary>
/// <remarks>
/// The sampler cannot set the new and dead flags itself, because both are
/// properties of the <em>transition</em> between two snapshots rather than of any
/// one of them. Dead processes are the interesting case: they are gone from the
/// kernel entirely, so keeping them visible means the list has to carry rows the
/// current snapshot no longer contains.
/// </remarks>
public sealed class ProcessListModel
{
    /// <summary>
    /// How long a row stays tinted. Process Explorer's default is one second for
    /// each, which is long enough to catch a process that starts and immediately
    /// exits — the case the highlighting exists for.
    /// </summary>
    public TimeSpan HighlightDuration { get; set; } = TimeSpan.FromSeconds(1);

    public bool HighlightNewAndDead { get; set; } = true;

    private readonly Dictionary<ProcessId, DateTimeOffset> _appearedAt = [];
    private readonly Dictionary<ProcessId, (ProcessRecord Record, DateTimeOffset DiedAt)> _recentlyDead = [];

    private ProcessSnapshot _current = ProcessSnapshot.Empty;
    private bool _hasBaseline;

    public ProcessSnapshot Current => _current;

    /// <summary>Processes currently shown, including any lingering dead rows.</summary>
    public IReadOnlyDictionary<ProcessId, ProcessRecord> Displayed { get; private set; } =
        new Dictionary<ProcessId, ProcessRecord>();

    public IReadOnlyList<ProcessId> Roots { get; private set; } = [];

    public IReadOnlyDictionary<ProcessId, IReadOnlyList<ProcessId>> Children { get; private set; } =
        new Dictionary<ProcessId, IReadOnlyList<ProcessId>>();

    /// <summary>Feed in a fresh snapshot and recompute what should be on screen.</summary>
    public void Apply(ProcessSnapshot snapshot, DateTimeOffset now)
    {
        var previous = _current;
        _current = snapshot;

        if (!HighlightNewAndDead)
        {
            _appearedAt.Clear();
            _recentlyDead.Clear();
            Displayed = snapshot.Processes;
            Roots = snapshot.Roots;
            Children = snapshot.Children;
            return;
        }

        var diff = SnapshotDiff.Between(previous, snapshot);

        // The first snapshot is diffed against an empty one, so every process
        // comes through as added. Flagging them would flash the entire list green
        // at startup, which says nothing — "new" is only meaningful relative to a
        // list the user has already seen.
        if (!_hasBaseline)
        {
            _hasBaseline = true;
            Displayed = snapshot.Processes;
            Roots = snapshot.Roots;
            Children = snapshot.Children;
            return;
        }

        foreach (var id in diff.Added)
        {
            // Drop any ghost occupying this pid. The match has to be on the pid
            // alone, not on the full identity: a recycled pid arrives with a
            // different start time, so removing by identity would never find the
            // ghost and the list would show a live row and a dead row with the
            // same pid at once.
            foreach (var ghost in _recentlyDead.Keys.Where(g => g.Pid == id.Pid).ToList())
            {
                _recentlyDead.Remove(ghost);
            }

            _appearedAt[id] = now;
        }

        foreach (var id in diff.Removed)
        {
            if (previous.Processes.TryGetValue(id, out var record))
            {
                _recentlyDead[id] = (record, now);
            }

            _appearedAt.Remove(id);
        }

        Expire(now);
        Rebuild(snapshot, now);
    }

    /// <summary>
    /// Recompute purely to let highlights fade, without a new snapshot.
    /// </summary>
    /// <returns>True when something changed and the view needs repainting.</returns>
    public bool Tick(DateTimeOffset now)
    {
        if (!HighlightNewAndDead || (_appearedAt.Count == 0 && _recentlyDead.Count == 0))
        {
            return false;
        }

        var before = _appearedAt.Count + _recentlyDead.Count;
        Expire(now);

        if (_appearedAt.Count + _recentlyDead.Count == before)
        {
            return false;
        }

        Rebuild(_current, now);
        return true;
    }

    private void Expire(DateTimeOffset now)
    {
        foreach (var id in _appearedAt
                     .Where(e => now - e.Value > HighlightDuration)
                     .Select(e => e.Key)
                     .ToList())
        {
            _appearedAt.Remove(id);
        }

        foreach (var id in _recentlyDead
                     .Where(e => now - e.Value.DiedAt > HighlightDuration)
                     .Select(e => e.Key)
                     .ToList())
        {
            _recentlyDead.Remove(id);
        }
    }

    private void Rebuild(ProcessSnapshot snapshot, DateTimeOffset now)
    {
        var displayed = new Dictionary<ProcessId, ProcessRecord>(
            snapshot.Processes.Count + _recentlyDead.Count);

        foreach (var (id, record) in snapshot.Processes)
        {
            displayed[id] = _appearedAt.ContainsKey(id)
                ? record with { Flags = record.Flags | ProcessFlags.NewProcess }
                : record;
        }

        foreach (var (id, (record, _)) in _recentlyDead)
        {
            // Ghost rows keep their last known values but are marked dead, so the
            // colouring rules tint them and nothing tries to act on them.
            displayed[id] = record with { Flags = record.Flags | ProcessFlags.DeadProcess };
        }

        Displayed = displayed;

        if (_recentlyDead.Count == 0)
        {
            Roots = snapshot.Roots;
            Children = snapshot.Children;
        }
        else
        {
            // Ghost rows have to be re-parented into the tree, and their own
            // parent may itself be a ghost — killing a tree produces exactly that.
            var (roots, children) = ProcessTreeBuilder.Build(displayed);
            Roots = roots;
            Children = children;
        }

        _ = now;
    }

    /// <summary>A snapshot view of what is displayed, for the row flattener.</summary>
    public ProcessSnapshot AsSnapshot() => new()
    {
        Timestamp = _current.Timestamp,
        Interval = _current.Interval,
        Processes = Displayed,
        Roots = Roots,
        Children = Children,
        System = _current.System,
    };

    /// <summary>Whether a row refers to a process that no longer exists.</summary>
    public bool IsDead(ProcessId id) => _recentlyDead.ContainsKey(id);
}
