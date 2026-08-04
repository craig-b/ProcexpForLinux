using Procexp.Model;

namespace Procexp.App.Controls;

/// <summary>
/// One row as it appears on screen: a process, its depth in the tree, and
/// whether it can be collapsed.
/// </summary>
/// <remarks>
/// The tree is flattened into a list once per snapshot so that rendering and
/// hit-testing are both plain indexed lookups. Walking the tree during a paint
/// would make the cost of drawing depend on tree shape rather than on how many
/// rows are actually visible.
/// </remarks>
public readonly record struct VisibleRow(
    ProcessRecord Process,
    int Depth,
    bool HasChildren,
    bool IsExpanded
);

/// <summary>
/// Flattens a snapshot's tree into the visible row list, honouring collapse
/// state and the active sort.
/// </summary>
public static class RowFlattener
{
    public static List<VisibleRow> Flatten(
        ProcessSnapshot snapshot,
        HashSet<ProcessId> collapsed,
        Column sortColumn,
        bool descending,
        bool treeMode
    )
    {
        var rows = new List<VisibleRow>(snapshot.Processes.Count);

        if (!treeMode)
        {
            // Flat list: every process at depth zero, sorted as a whole.
            var all = snapshot.Processes.Values.ToList();
            Sort(all, sortColumn, descending);

            foreach (var process in all)
            {
                rows.Add(new VisibleRow(process, 0, false, false));
            }

            return rows;
        }

        var roots = snapshot
            .Roots.Select(id => snapshot.Processes.GetValueOrDefault(id))
            .Where(p => p is not null)
            .Select(p => p!)
            .ToList();

        Sort(roots, sortColumn, descending);

        foreach (var root in roots)
        {
            Walk(root, 0);
        }

        return rows;

        void Walk(ProcessRecord process, int depth)
        {
            var childIds = snapshot.ChildIds(process.Id);
            var isCollapsed = collapsed.Contains(process.Id);

            rows.Add(new VisibleRow(process, depth, childIds.Count > 0, !isCollapsed));

            if (childIds.Count == 0 || isCollapsed)
            {
                return;
            }

            var children = childIds
                .Select(id => snapshot.Processes.GetValueOrDefault(id))
                .Where(p => p is not null)
                .Select(p => p!)
                .ToList();

            Sort(children, sortColumn, descending);

            foreach (var child in children)
            {
                Walk(child, depth + 1);
            }
        }
    }

    /// <summary>
    /// Sort a sibling group.
    /// </summary>
    /// <remarks>
    /// PID is the tie-break regardless of the active column. Without it, rows
    /// with equal values — every process showing no CPU, say — would shuffle on
    /// each refresh, because the sort is not stable across separately-built
    /// lists.
    /// </remarks>
    private static void Sort(List<ProcessRecord> processes, Column column, bool descending)
    {
        processes.Sort(
            (a, b) =>
            {
                var result = Columns.SortValue(column, a).CompareTo(Columns.SortValue(column, b));
                if (result == 0)
                {
                    return a.Id.Pid.CompareTo(b.Id.Pid);
                }

                return descending ? -result : result;
            }
        );
    }
}
