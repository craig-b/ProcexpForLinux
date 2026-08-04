using Procexp.App;
using Procexp.App.Controls;
using Procexp.Model;
using Xunit;

namespace Procexp.App.Tests;

/// <summary>
/// Tests the new/dead highlighting, which cannot be verified from a screenshot
/// because it is defined by the transition between two snapshots rather than by
/// anything visible in one.
/// </summary>
public class ProcessListModelTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static ProcessRecord Record(int pid, ulong start = 100, string name = "proc", int? ppid = null) =>
        new()
        {
            Id = new ProcessId(pid, start),
            Name = name,
            Parent = ppid is { } p ? new ProcessId(p, 100) : null,
        };

    private static ProcessSnapshot Snapshot(params ProcessRecord[] processes)
    {
        var map = processes.ToDictionary(p => p.Id);
        var (roots, children) = ProcessTreeBuilder.Build(map);

        return new ProcessSnapshot
        {
            Timestamp = T0,
            Interval = 1,
            Processes = map,
            Roots = roots,
            Children = children,
            System = SystemStats.Zero,
        };
    }

    [Fact]
    public void NewProcessesAreFlagged()
    {
        var model = new ProcessListModel();

        model.Apply(Snapshot(Record(1)), T0);
        model.Apply(Snapshot(Record(1), Record(2)), T0);

        Assert.True(model.Displayed[new ProcessId(2, 100)].Flags.HasFlag(ProcessFlags.NewProcess));
        Assert.False(model.Displayed[new ProcessId(1, 100)].Flags.HasFlag(ProcessFlags.NewProcess));
    }

    /// <summary>
    /// The first snapshot is diffed against an empty one, so every process is
    /// technically "added". Flashing the whole list green at startup would say
    /// nothing — new is only meaningful against a list the user has already seen.
    /// </summary>
    [Fact]
    public void FirstSnapshotDoesNotFlagEverythingAsNew()
    {
        var model = new ProcessListModel();
        model.Apply(Snapshot(Record(1), Record(2), Record(3)), T0);

        Assert.All(model.Displayed.Values, p =>
            Assert.False(p.Flags.HasFlag(ProcessFlags.NewProcess)));
    }

    /// <summary>
    /// A dead process is gone from the kernel, so keeping it on screen means the
    /// displayed list must carry rows the current snapshot does not contain.
    /// </summary>
    [Fact]
    public void DeadProcessesLingerAsGhostRows()
    {
        var model = new ProcessListModel();

        model.Apply(Snapshot(Record(1), Record(2)), T0);
        model.Apply(Snapshot(Record(1)), T0);

        var ghost = new ProcessId(2, 100);

        Assert.True(model.Displayed.ContainsKey(ghost));
        Assert.True(model.Displayed[ghost].Flags.HasFlag(ProcessFlags.DeadProcess));
        Assert.True(model.IsDead(ghost));

        // The underlying snapshot must not be polluted by the ghost.
        Assert.False(model.Current.Processes.ContainsKey(ghost));
    }

    [Fact]
    public void HighlightsExpireAfterTheConfiguredDuration()
    {
        var model = new ProcessListModel { HighlightDuration = TimeSpan.FromSeconds(1) };

        model.Apply(Snapshot(Record(1), Record(2)), T0);
        model.Apply(Snapshot(Record(1)), T0);

        Assert.True(model.IsDead(new ProcessId(2, 100)));

        var changed = model.Tick(T0 + TimeSpan.FromSeconds(1.5));

        Assert.True(changed);
        Assert.False(model.IsDead(new ProcessId(2, 100)));
        Assert.False(model.Displayed.ContainsKey(new ProcessId(2, 100)));
    }

    [Fact]
    public void TickReportsNoChangeWhenNothingExpired()
    {
        var model = new ProcessListModel { HighlightDuration = TimeSpan.FromSeconds(5) };

        model.Apply(Snapshot(Record(1), Record(2)), T0);
        model.Apply(Snapshot(Record(1)), T0);

        Assert.False(model.Tick(T0 + TimeSpan.FromSeconds(1)));
    }

    /// <summary>
    /// A pid reused by a different process must not be mistaken for the old one
    /// coming back. Identity includes start time, so the ghost has to be dropped
    /// rather than merged.
    /// </summary>
    [Fact]
    public void RecycledPidReplacesTheGhostRatherThanResurrectingIt()
    {
        var model = new ProcessListModel();

        model.Apply(Snapshot(Record(1), Record(2, start: 100, name: "old")), T0);
        model.Apply(Snapshot(Record(1)), T0 + TimeSpan.FromSeconds(1));

        Assert.True(model.IsDead(new ProcessId(2, 100)));

        // Same pid, different start time: a new occupant.
        model.Apply(Snapshot(Record(1), Record(2, start: 999, name: "new")), T0 + TimeSpan.FromSeconds(1.2));

        var recycled = new ProcessId(2, 999);

        Assert.True(model.Displayed[recycled].Flags.HasFlag(ProcessFlags.NewProcess));
        Assert.Equal("new", model.Displayed[recycled].Name);

        // The old ghost is gone, not sitting alongside as a dead row.
        Assert.False(model.Displayed.ContainsKey(new ProcessId(2, 100)));
        Assert.False(model.IsDead(new ProcessId(2, 100)));
    }

    /// <summary>
    /// Killing a tree kills parents and children together, so a ghost's own
    /// parent is frequently also a ghost. The rebuilt tree has to keep them
    /// attached rather than scattering them to the root.
    /// </summary>
    [Fact]
    public void GhostRowsRemainParentedToGhostParents()
    {
        var model = new ProcessListModel();

        model.Apply(Snapshot(Record(1), Record(2, ppid: 1), Record(3, ppid: 2)), T0);
        model.Apply(Snapshot(Record(1)), T0);

        var parent = new ProcessId(2, 100);
        var child = new ProcessId(3, 100);

        Assert.True(model.Displayed.ContainsKey(parent));
        Assert.True(model.Displayed.ContainsKey(child));
        Assert.Contains(child, model.Children[parent]);
        Assert.DoesNotContain(child, model.Roots);
    }

    [Fact]
    public void DisablingHighlightingDropsGhostsImmediately()
    {
        var model = new ProcessListModel();

        model.Apply(Snapshot(Record(1), Record(2)), T0);
        model.Apply(Snapshot(Record(1)), T0);
        Assert.True(model.IsDead(new ProcessId(2, 100)));

        model.HighlightNewAndDead = false;
        model.Apply(Snapshot(Record(1)), T0);

        Assert.False(model.Displayed.ContainsKey(new ProcessId(2, 100)));
        Assert.Single(model.Displayed);
    }

    /// <summary>
    /// The flattener consumes a snapshot, so the model has to be able to present
    /// its ghost-augmented view as one.
    /// </summary>
    [Fact]
    public void AsSnapshotIncludesGhostRows()
    {
        var model = new ProcessListModel();

        model.Apply(Snapshot(Record(1), Record(2, ppid: 1)), T0);
        model.Apply(Snapshot(Record(1)), T0);

        var snapshot = model.AsSnapshot();
        var rows = RowFlattener.Flatten(snapshot, [], Column.Pid, descending: false, treeMode: true);

        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.Process.Flags.HasFlag(ProcessFlags.DeadProcess));
    }
}
