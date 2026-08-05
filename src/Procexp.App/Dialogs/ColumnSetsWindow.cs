using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Procexp.App.Settings;

namespace Procexp.App.Dialogs;

/// <summary>
/// Organise saved column sets: rename, delete, reorder — the Linux analog of
/// the macOS OrganizeColumnSetsSheet.
/// </summary>
public sealed class ColumnSetsWindow : Window
{
    private readonly ListBox _list = new();
    private readonly List<ColumnSet> _sets;

    /// <summary>The kept sets, or null when cancelled.</summary>
    public IReadOnlyList<ColumnSet>? Result { get; private set; }

    public ColumnSetsWindow(IReadOnlyList<ColumnSet> sets)
    {
        Title = "Organize Column Sets";
        Width = 420;
        Height = 380;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _sets = [.. sets];
        Refresh();

        var rename = new Button { Content = "Rename…", MinWidth = 96 };
        rename.Click += (_, _) => _ = RenameAsync();

        var delete = new Button { Content = "Delete", MinWidth = 96 };
        delete.Click += (_, _) =>
        {
            if (_list.SelectedIndex >= 0)
            {
                _sets.RemoveAt(_list.SelectedIndex);
                Refresh();
            }
        };

        var up = new Button { Content = "Move Up", MinWidth = 96 };
        up.Click += (_, _) => Move(-1);

        var down = new Button { Content = "Move Down", MinWidth = 96 };
        down.Click += (_, _) => Move(1);

        var ok = new Button
        {
            Content = "OK",
            MinWidth = 80,
            IsDefault = true,
        };
        ok.Click += (_, _) =>
        {
            Result = _sets;
            Close();
        };

        var cancel = new Button
        {
            Content = "Cancel",
            MinWidth = 80,
            IsCancel = true,
        };
        cancel.Click += (_, _) => Close();

        var side = new StackPanel
        {
            Spacing = 8,
            Margin = new Thickness(12, 0, 0, 0),
            Children = { rename, delete, up, down },
        };

        var body = new DockPanel { Margin = new Thickness(16) };
        DockPanel.SetDock(side, Dock.Right);
        body.Children.Add(side);
        body.Children.Add(_list);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(16, 0, 16, 16),
            Children = { cancel, ok },
        };

        var root = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(body);
        Content = root;
    }

    private void Move(int delta)
    {
        var index = _list.SelectedIndex;
        var target = index + delta;
        if (index < 0 || target < 0 || target >= _sets.Count)
        {
            return;
        }

        (_sets[index], _sets[target]) = (_sets[target], _sets[index]);
        Refresh();
        _list.SelectedIndex = target;
    }

    private async Task RenameAsync()
    {
        var index = _list.SelectedIndex;
        if (index < 0)
        {
            return;
        }

        var prompt = new TextPromptDialog("Rename Column Set", "Name:", _sets[index].Name);
        await prompt.ShowDialog(this).ConfigureAwait(true);

        if (prompt.Result is { } name)
        {
            _sets[index] = _sets[index] with { Name = name };
            Refresh();
            _list.SelectedIndex = index;
        }
    }

    private void Refresh() =>
        _list.ItemsSource = _sets.Select(s => $"{s.Name}  ({s.Columns.Count} columns)").ToList();
}
