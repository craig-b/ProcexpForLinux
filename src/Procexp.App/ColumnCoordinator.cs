using Avalonia.Controls;
using Procexp.App.Controls;
using Procexp.App.Dialogs;
using Procexp.App.Settings;
using Procexp.Metrics;
using Procexp.Model;

namespace Procexp.App;

/// <summary>
/// Owns the column layout and the saved column sets: which columns show, their
/// widths, the chooser and organiser dialogs, and the Column Sets menu. The
/// window asks it for <see cref="Columns"/> whenever it lays out or saves.
/// </summary>
public sealed class ColumnCoordinator(
    Window owner,
    ProcessTreeView tree,
    IReadOnlyDictionary<string, double> savedWidths,
    Action rebuild,
    Action scheduleSave,
    Action saveNow
)
{
    public IReadOnlyList<(Column Column, double Width)> Columns { get; private set; } = [];

    public IReadOnlyList<ColumnSet> ColumnSets { get; private set; } = [];

    private MenuItem _menu = null!;
    private Separator _separator = null!;

    /// <summary>
    /// Rebuild the column layout, honouring any saved widths.
    /// </summary>
    /// <remarks>
    /// Widths are keyed by column name rather than position, so adding or
    /// reordering columns does not shuffle every stored width onto the wrong one.
    /// </remarks>
    public void Apply(IReadOnlyList<Column> columns)
    {
        var normalised = Columns_.Normalise(columns);

        Columns =
        [
            (Column.Name, tree.NamePaneWidth),
            .. normalised
                .Where(c => c != Column.Name)
                .Select(c =>
                    (
                        c,
                        savedWidths.TryGetValue(c.ToString(), out var w)
                            ? w
                            : Model.Columns.DefaultWidth(c)
                    )
                ),
        ];
    }

    /// <summary>
    /// Take back the layout after the user resized or reordered in the tree,
    /// which owns the interaction but not the record of it.
    /// </summary>
    public void AcceptLayout(IReadOnlyList<(Column Column, double Width)> columns) =>
        Columns = columns;

    public void SetColumnSets(IReadOnlyList<ColumnSet> sets) => ColumnSets = sets;

    /// <summary>
    /// Attach the Column Sets menu this coordinator maintains. The separator
    /// only shows once there is a saved set beneath it.
    /// </summary>
    public void WireMenu(MenuItem menu, Separator separator)
    {
        _menu = menu;
        _separator = separator;
        RebuildMenu();
    }

    /// <summary>
    /// Rebuild the Column Sets menu: the two commands, then one item per saved
    /// set. Rebuilt rather than bound, since the menu is the only view of them.
    /// </summary>
    private void RebuildMenu()
    {
        var fixedItems = _menu.Items.OfType<Control>().Take(3).ToList();

        _menu.Items.Clear();
        foreach (var item in fixedItems)
        {
            _menu.Items.Add(item);
        }

        _separator.IsVisible = ColumnSets.Count > 0;

        foreach (var set in ColumnSets)
        {
            var item = new MenuItem { Header = set.Name };
            var columns = set.Columns;
            item.Click += (_, _) =>
            {
                Apply(columns);
                tree.SetRows([], Columns);
                rebuild();
                scheduleSave();
            };
            _menu.Items.Add(item);
        }
    }

    public async Task ChooseAsync()
    {
        var chooser = new ColumnChooserWindow([.. Columns.Select(c => c.Column)]);
        await chooser.ShowDialog(owner).ConfigureAwait(true);

        if (chooser.Result is { } chosen)
        {
            Apply(chosen);
            rebuild();

            // Persist immediately. A column layout is deliberate work, and losing
            // it to a crash before the next clean exit would be irritating.
            saveNow();
        }
    }

    public async Task SaveSetAsync()
    {
        var prompt = new TextPromptDialog(
            "Save Column Set",
            "Name for this column layout:",
            $"Set {ColumnSets.Count + 1}"
        );
        await prompt.ShowDialog(owner).ConfigureAwait(true);

        if (prompt.Result is not { } name)
        {
            return;
        }

        // Saving over an existing name replaces it, which is what a user
        // re-saving a tweaked layout means.
        var columns = Columns.Select(c => c.Column).ToList();
        ColumnSets =
        [
            .. ColumnSets.Where(s => s.Name != name),
            new ColumnSet { Name = name, Columns = columns },
        ];

        RebuildMenu();
        scheduleSave();
    }

    public async Task OrganizeAsync()
    {
        var window = new ColumnSetsWindow(ColumnSets);
        await window.ShowDialog(owner).ConfigureAwait(true);

        if (window.Result is { } kept)
        {
            ColumnSets = kept;
            RebuildMenu();
            scheduleSave();
        }
    }
}
