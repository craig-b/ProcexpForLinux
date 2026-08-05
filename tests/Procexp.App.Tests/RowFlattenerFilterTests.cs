using Procexp.App.Controls;
using Procexp.Model;
using Xunit;

namespace Procexp.App.Tests;

/// <summary>
/// The filter semantics of the row flattener.
/// </summary>
/// <remarks>
/// The subtle behaviours are the ones a paint-time check cannot catch: a match
/// deep in the tree must keep its ancestors so it stays reachable, and a
/// collapsed parent must not hide the match the user just typed to find.
/// </remarks>
public class RowFlattenerFilterTests
{
    /// <summary>init(1) → daemon(10) → worker(100); shell(2) → editor(20).</summary>
    private static ProcessSnapshot Tree()
    {
        var records = new[]
        {
            Make(1, null, "init", "root"),
            Make(10, 1, "daemon", "root"),
            Make(100, 10, "worker", "svc", "worker --queue jobs"),
            Make(2, 1, "shell", "craig"),
            Make(20, 2, "editor", "craig"),
        };

        var processes = records.ToDictionary(r => r.Id, r => r);
        var children = records
            .Where(r => r.Parent is not null)
            .GroupBy(r => r.Parent!.Value)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<ProcessId>)[.. g.Select(r => r.Id)]);

        return new ProcessSnapshot
        {
            Timestamp = DateTimeOffset.MinValue,
            Interval = 1,
            Processes = processes,
            Roots = [new ProcessId(1, 1)],
            Children = children,
            System = SystemStats.Zero,
        };
    }

    private static ProcessRecord Make(
        int pid,
        int? parent,
        string name,
        string user,
        string? commandLine = null
    ) =>
        new()
        {
            Id = new ProcessId(pid, 1),
            Parent = parent is { } p ? new ProcessId(p, 1) : null,
            Name = name,
            UserName = user,
            CommandLine = commandLine ?? name,
            Uid = 0,
        };

    private static List<VisibleRow> Rows(string? filter, bool treeMode = true) =>
        RowFlattener.Flatten(Tree(), [], Column.Pid, descending: false, treeMode, filter);

    [Fact]
    public void NoFilterShowsEverything() => Assert.Equal(5, Rows(null).Count);

    [Fact]
    public void MatchKeepsItsAncestors()
    {
        var names = Rows("worker").Select(r => r.Process.Name).ToList();
        Assert.Equal(["init", "daemon", "worker"], names);
    }

    [Fact]
    public void FlatModeShowsOnlyMatches()
    {
        var names = Rows("worker", treeMode: false).Select(r => r.Process.Name).ToList();
        Assert.Equal(["worker"], names);
    }

    [Fact]
    public void MatchesByPidUserAndCommandLine()
    {
        Assert.Contains(Rows("20"), r => r.Process.Name == "editor");
        Assert.Contains(Rows("svc"), r => r.Process.Name == "worker");
        Assert.Contains(Rows("--queue"), r => r.Process.Name == "worker");
    }

    [Fact]
    public void CollapseIsSuspendedWhileFiltering()
    {
        var collapsed = new HashSet<ProcessId> { new(10, 1) };

        var filtered = RowFlattener.Flatten(
            Tree(),
            collapsed,
            Column.Pid,
            descending: false,
            treeMode: true,
            "worker"
        );
        Assert.Contains(filtered, r => r.Process.Name == "worker");

        var unfiltered = RowFlattener.Flatten(
            Tree(),
            collapsed,
            Column.Pid,
            descending: false,
            treeMode: true
        );
        Assert.DoesNotContain(unfiltered, r => r.Process.Name == "worker");
    }
}
