using System.Collections.Frozen;

namespace Procexp.Model;

/// <summary>
/// A complete sample of every process plus system-wide stats at one instant —
/// the immutable "whole world" the UI renders from.
/// </summary>
public sealed class ProcessSnapshot
{
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>Wall-clock seconds since the previous snapshot, for rate maths.</summary>
    public required double Interval { get; init; }

    public required IReadOnlyDictionary<ProcessId, ProcessRecord> Processes { get; init; }

    /// <summary>Top-level processes — those whose parent is absent from the snapshot.</summary>
    public required IReadOnlyList<ProcessId> Roots { get; init; }

    /// <summary>Parent to children adjacency describing the process tree.</summary>
    public required IReadOnlyDictionary<ProcessId, IReadOnlyList<ProcessId>> Children { get; init; }

    public required SystemStats System { get; init; }

    public static readonly ProcessSnapshot Empty = new()
    {
        Timestamp = DateTimeOffset.MinValue,
        Interval = 0,
        Processes = FrozenDictionary<ProcessId, ProcessRecord>.Empty,
        Roots = [],
        Children = FrozenDictionary<ProcessId, IReadOnlyList<ProcessId>>.Empty,
        System = SystemStats.Zero,
    };

    public ProcessRecord? Info(ProcessId id) =>
        Processes.TryGetValue(id, out var record) ? record : null;

    public IReadOnlyList<ProcessId> ChildIds(ProcessId id) =>
        Children.TryGetValue(id, out var kids) ? kids : [];
}

/// <summary>
/// What changed between two consecutive snapshots — what the UI needs to fade
/// new and dead rows.
/// </summary>
public sealed record SnapshotDiff(
    IReadOnlySet<ProcessId> Added,
    IReadOnlySet<ProcessId> Removed,
    IReadOnlySet<ProcessId> Changed)
{
    public static SnapshotDiff Between(ProcessSnapshot old, ProcessSnapshot @new)
    {
        var added = new HashSet<ProcessId>();
        var changed = new HashSet<ProcessId>();

        foreach (var (id, record) in @new.Processes)
        {
            if (!old.Processes.TryGetValue(id, out var previous))
            {
                added.Add(id);
            }
            else if (previous != record)
            {
                changed.Add(id);
            }
        }

        var removed = new HashSet<ProcessId>();
        foreach (var id in old.Processes.Keys)
        {
            if (!@new.Processes.ContainsKey(id))
            {
                removed.Add(id);
            }
        }

        return new SnapshotDiff(added, removed, changed);
    }
}

/// <summary>
/// Builds the tree layout so every provider produces a consistent
/// roots/children shape from a flat process map.
/// </summary>
public static class ProcessTreeBuilder
{
    /// <summary>
    /// Build roots and children from a flat process map.
    /// </summary>
    /// <remarks>
    /// A process is a root when it has no parent, when it is its own parent, or
    /// when its parent is absent from the map. Children come back sorted by PID
    /// for stable ordering; callers re-sort by the active column.
    ///
    /// Absent parents matter more on Linux than on macOS: when a parent exits its
    /// children are reparented to init or to the nearest subreaper, and during the
    /// window before the kernel reparents them a sample can legitimately observe a
    /// PPID that no longer exists.
    /// </remarks>
    public static (IReadOnlyList<ProcessId> Roots, IReadOnlyDictionary<ProcessId, IReadOnlyList<ProcessId>> Children)
        Build(IReadOnlyDictionary<ProcessId, ProcessRecord> processes)
    {
        var children = new Dictionary<ProcessId, List<ProcessId>>();
        var roots = new List<ProcessId>();

        foreach (var (id, record) in processes)
        {
            if (record.Parent is { } parent && parent != id && processes.ContainsKey(parent))
            {
                if (!children.TryGetValue(parent, out var list))
                {
                    children[parent] = list = [];
                }

                list.Add(id);
            }
            else
            {
                roots.Add(id);
            }
        }

        foreach (var list in children.Values)
        {
            list.Sort(static (a, b) => a.Pid.CompareTo(b.Pid));
        }

        roots.Sort(static (a, b) => a.Pid.CompareTo(b.Pid));

        var frozen = children.ToDictionary(
            static kv => kv.Key,
            static kv => (IReadOnlyList<ProcessId>)kv.Value);

        return (roots, frozen);
    }
}
