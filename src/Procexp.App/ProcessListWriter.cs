using Procexp.App.Controls;
using Procexp.Model;

namespace Procexp.App;

/// <summary>
/// Formats the visible process list for File &gt; Save: a tab-separated header
/// of column titles, then one line per row with the tree indent folded into
/// the first cell. Pure text — the window owns the file dialog and the stream.
/// </summary>
public static class ProcessListWriter
{
    public static IEnumerable<string> Lines(
        IReadOnlyList<Column> columns,
        IEnumerable<VisibleRow> rows
    )
    {
        yield return string.Join('\t', columns.Select(Columns.Title));

        foreach (var row in rows)
        {
            var indent = new string(' ', row.Depth * 2);
            yield return string.Join(
                '\t',
                columns.Select(
                    (c, i) =>
                        i == 0
                            ? indent + Columns.Format(c, row.Process)
                            : Columns.Format(c, row.Process)
                )
            );
        }
    }
}
