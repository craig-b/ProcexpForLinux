using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Procexp.App.Controls;

/// <summary>
/// A label-and-value list, for the tabs of the Properties window that are
/// descriptions rather than tables.
/// </summary>
/// <remarks>
/// Values are selectable. Half the point of a properties window is to copy a path
/// or a command line out of it, and a plain TextBlock cannot be selected.
/// </remarks>
public sealed class DetailList : UserControl
{
    private readonly Grid _grid = new()
    {
        ColumnDefinitions = new ColumnDefinitions("Auto,*"),
        Margin = new Thickness(12),
    };

    private int _row;

    public DetailList() =>
        Content = new ScrollViewer
        {
            Content = _grid,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        };

    /// <summary>Remove every row, for repopulating on refresh.</summary>
    public void Clear()
    {
        _grid.Children.Clear();
        _grid.RowDefinitions.Clear();
        _row = 0;
    }

    /// <summary>Add a label and value. Empty values are skipped by default.</summary>
    public void Add(string label, string? value, bool showWhenEmpty = false)
    {
        if (string.IsNullOrEmpty(value) && !showWhenEmpty)
        {
            return;
        }

        _grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        var caption = new TextBlock
        {
            Text = label,
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(0, 3, 16, 3),
            VerticalAlignment = VerticalAlignment.Top,
            Opacity = 0.75,
        };

        Grid.SetRow(caption, _row);
        Grid.SetColumn(caption, 0);
        _grid.Children.Add(caption);

        var content = new SelectableTextBlock
        {
            Text = value ?? "",
            Margin = new Thickness(0, 3, 0, 3),
            TextWrapping = TextWrapping.Wrap,
        };

        Grid.SetRow(content, _row);
        Grid.SetColumn(content, 1);
        _grid.Children.Add(content);

        _row++;
    }

    /// <summary>
    /// Remove the most recently added row, for replacing a provisional value —
    /// a "Checking..." that has since resolved — without rebuilding the list.
    /// </summary>
    public void RemoveLast()
    {
        if (_row == 0)
        {
            return;
        }

        _row--;

        for (var i = _grid.Children.Count - 1; i >= 0; i--)
        {
            if (Grid.GetRow(_grid.Children[i]) == _row)
            {
                _grid.Children.RemoveAt(i);
            }
        }

        _grid.RowDefinitions.RemoveAt(_grid.RowDefinitions.Count - 1);
    }

    /// <summary>Add a spacer and a heading, to group related rows.</summary>
    public void AddSection(string title)
    {
        _grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        var heading = new TextBlock
        {
            Text = title,
            FontWeight = FontWeight.Bold,
            Margin = new Thickness(0, _row == 0 ? 0 : 14, 0, 4),
            Opacity = 0.6,
        };

        Grid.SetRow(heading, _row);
        Grid.SetColumn(heading, 0);
        Grid.SetColumnSpan(heading, 2);
        _grid.Children.Add(heading);

        _row++;
    }
}
