using Procexp.App.Controls;
using Procexp.Model;
using Xunit;

namespace Procexp.App.Tests;

/// <summary>
/// The File &gt; Save text format: a tab-separated header of column titles,
/// then one line per row with the tree indent folded into the first cell.
/// </summary>
public class ProcessListWriterTests
{
    private static ProcessRecord Make(int pid, string name) =>
        new()
        {
            Id = new ProcessId(pid, 1),
            Parent = null,
            Name = name,
            UserName = "craig",
            CommandLine = name,
            Uid = 0,
        };

    private static readonly IReadOnlyList<Column> TwoColumns = [Column.Name, Column.Pid];

    [Fact]
    public void HeaderIsTabSeparatedTitles()
    {
        var lines = ProcessListWriter.Lines(TwoColumns, []).ToList();

        Assert.Equal(["Process\tPID"], lines);
    }

    [Fact]
    public void RowsFormatEveryColumn()
    {
        var rows = new[] { new VisibleRow(Make(42, "editor"), 0, false, false) };

        var lines = ProcessListWriter.Lines(TwoColumns, rows).ToList();

        Assert.Equal("editor\t42", lines[1]);
    }

    [Fact]
    public void TreeDepthIndentsTheFirstCellOnly()
    {
        var rows = new[]
        {
            new VisibleRow(Make(1, "init"), 0, true, true),
            new VisibleRow(Make(10, "daemon"), 1, true, true),
            new VisibleRow(Make(100, "worker"), 2, false, false),
        };

        var lines = ProcessListWriter.Lines(TwoColumns, rows).ToList();

        Assert.Equal("init\t1", lines[1]);
        Assert.Equal("  daemon\t10", lines[2]);
        Assert.Equal("    worker\t100", lines[3]);
    }
}
