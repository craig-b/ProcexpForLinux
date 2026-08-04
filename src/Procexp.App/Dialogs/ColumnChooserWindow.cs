using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Procexp.Model;

namespace Procexp.App.Dialogs;

/// <summary>
/// Chooses which columns the process list shows, and in what order.
/// </summary>
/// <remarks>
/// Two lists with move buttons rather than a checkbox list, because order matters
/// as much as membership and a checkbox list cannot express it.
/// </remarks>
public sealed class ColumnChooserWindow : Window
{
    private readonly ListBox _available = new() { SelectionMode = SelectionMode.Multiple };
    private readonly ListBox _selected = new() { SelectionMode = SelectionMode.Multiple };

    private readonly List<Column> _availableColumns = [];
    private readonly List<Column> _selectedColumns = [];

    /// <summary>The chosen columns, or null when the dialog was cancelled.</summary>
    public IReadOnlyList<Column>? Result { get; private set; }

    public ColumnChooserWindow(IReadOnlyList<Column> current)
    {
        Title = "Select Columns";
        Width = 620;
        Height = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _selectedColumns.AddRange(current.Where(Columns.IsSupported));
        _availableColumns.AddRange(Columns.Supported.Except(_selectedColumns));

        Refresh();
        BuildLayout();
    }

    private void BuildLayout()
    {
        var add = Button("Add →", () => Move(_available, _availableColumns, _selectedColumns));
        var remove = Button("← Remove", () => Move(_selected, _selectedColumns, _availableColumns));
        var up = Button("Move Up", () => Reorder(-1));
        var down = Button("Move Down", () => Reorder(1));

        var middle = new StackPanel
        {
            Orientation = Orientation.Vertical,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 6,
            Margin = new Thickness(10, 0),
            Children =
            {
                add,
                remove,
                new Border { Height = 16 },
                up,
                down,
            },
        };

        var lists = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,*"),
            RowDefinitions = new RowDefinitions("Auto,*"),
            Margin = new Thickness(16),
        };

        AddHeader("Available", 0);
        AddHeader("Shown", 2);

        Grid.SetRow(_available, 1);
        Grid.SetColumn(_available, 0);
        lists.Children.Add(_available);

        Grid.SetRow(middle, 1);
        Grid.SetColumn(middle, 1);
        lists.Children.Add(middle);

        Grid.SetRow(_selected, 1);
        Grid.SetColumn(_selected, 2);
        lists.Children.Add(_selected);

        var ok = Button(
            "OK",
            () =>
            {
                Result = [.. _selectedColumns];
                Close();
            }
        );

        ok.IsDefault = true;
        ok.MinWidth = 88;

        var cancel = Button("Cancel", Close);
        cancel.IsCancel = true;
        cancel.MinWidth = 88;

        var reset = Button(
            "Defaults",
            () =>
            {
                _selectedColumns.Clear();
                _selectedColumns.AddRange(Columns.Default);
                _availableColumns.Clear();
                _availableColumns.AddRange(Columns.Supported.Except(_selectedColumns));
                Refresh();
            }
        );

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(16, 0, 16, 16),
            Children =
            {
                reset,
                new Border { Width = 24 },
                cancel,
                ok,
            },
        };

        var root = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(lists);

        Content = root;

        void AddHeader(string text, int column)
        {
            var header = new TextBlock
            {
                Text = text,
                FontWeight = FontWeight.SemiBold,
                Margin = new Thickness(0, 0, 0, 6),
            };

            Grid.SetRow(header, 0);
            Grid.SetColumn(header, column);
            lists.Children.Add(header);
        }
    }

    private static Avalonia.Controls.Button Button(string content, Action onClick)
    {
        var button = new Avalonia.Controls.Button { Content = content, MinWidth = 110 };
        button.Click += (_, _) => onClick();
        return button;
    }

    private void Move(ListBox source, List<Column> from, List<Column> to)
    {
        var moving = source.SelectedItems?.Cast<ColumnEntry>().Select(e => e.Column).ToList() ?? [];

        foreach (var column in moving)
        {
            // Pinned columns are what the tree renders rows with; removing them
            // would leave a list of blank rows.
            if (to == _availableColumns && Columns.Pinned.Contains(column))
            {
                continue;
            }

            from.Remove(column);
            if (!to.Contains(column))
            {
                to.Add(column);
            }
        }

        Refresh();
    }

    private void Reorder(int delta)
    {
        if (_selected.SelectedItem is not ColumnEntry entry)
        {
            return;
        }

        var index = _selectedColumns.IndexOf(entry.Column);
        var target = index + delta;

        if (index < 0 || target < 0 || target >= _selectedColumns.Count)
        {
            return;
        }

        (_selectedColumns[index], _selectedColumns[target]) = (
            _selectedColumns[target],
            _selectedColumns[index]
        );

        Refresh();
        _selected.SelectedIndex = target;
    }

    private void Refresh()
    {
        _availableColumns.Sort(
            (a, b) => string.Compare(Columns.Title(a), Columns.Title(b), StringComparison.Ordinal)
        );

        _available.ItemsSource = _availableColumns.Select(c => new ColumnEntry(c)).ToList();
        _selected.ItemsSource = _selectedColumns.Select(c => new ColumnEntry(c)).ToList();
    }

    /// <summary>Wraps a column so the list boxes show its title rather than its enum name.</summary>
    private sealed record ColumnEntry(Column Column)
    {
        public override string ToString() =>
            Columns.Pinned.Contains(Column)
                ? $"{Columns.Title(Column)}  (required)"
                : Columns.Title(Column);
    }
}
