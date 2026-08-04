using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Procexp.Model;

namespace Procexp.App.Controls;

/// <summary>One column of a <see cref="DataTableView{T}"/>.</summary>
public sealed record TableColumn<T>(
    string Title,
    double Width,
    Func<T, string> Format,
    bool RightAligned = false,
    Func<T, SortKey>? Sort = null
)
{
    /// <summary>
    /// Sort key for a row. Falls back to sorting on the displayed text, which is
    /// right for the string columns and wrong for numeric ones — so numeric
    /// columns supply <see cref="Sort"/> explicitly rather than sorting "10"
    /// before "9".
    /// </summary>
    public SortKey SortValue(T item) => Sort is null ? SortKey.Text(Format(item)) : Sort(item);
}

/// <summary>
/// A flat virtualised table: columns, rows, sortable headers, no hierarchy and
/// no frozen pane.
/// </summary>
/// <remarks>
/// Backs the lower pane and the list-shaped tabs of the Properties window. All
/// the scrolling, selection and virtualisation comes from
/// <see cref="VirtualTableBase"/>; what remains here is a straightforward
/// left-to-right draw.
/// </remarks>
public sealed class DataTableView<T> : VirtualTableBase
    where T : class
{
    private IReadOnlyList<T> _rows = [];
    private IReadOnlyList<TableColumn<T>> _columns = [];

    private int _sortColumn;
    private bool _sortDescending;

    /// <summary>Identity for a row, so selection survives a refresh.</summary>
    public Func<T, object>? IdentityOf { get; set; }

    /// <summary>Row tint, for the new and deleted highlighting.</summary>
    public Func<T, Rgba?>? RowColour { get; set; }

    /// <summary>Message shown when there are no rows.</summary>
    public string EmptyMessage { get; set; } = "";

    public event EventHandler? SortChanged;

    protected override int RowCount => _rows.Count;

    protected override double ScrollableWidth => _columns.Sum(c => c.Width);

    public T? SelectedItem =>
        SelectedIndex >= 0 && SelectedIndex < _rows.Count ? _rows[SelectedIndex] : null;

    public IReadOnlyList<T> Rows => _rows;

    public void SetColumns(IReadOnlyList<TableColumn<T>> columns)
    {
        _columns = columns;
        _sortColumn = Math.Clamp(_sortColumn, 0, Math.Max(0, columns.Count - 1));
        InvalidateVisual();
    }

    /// <summary>Replace the rows, preserving selection by identity where possible.</summary>
    public void SetRows(IReadOnlyList<T> rows)
    {
        var previous = SelectedItem is { } item && IdentityOf is not null ? IdentityOf(item) : null;

        _rows = Sort(rows);

        if (previous is not null && IdentityOf is not null)
        {
            var index = -1;
            for (var i = 0; i < _rows.Count; i++)
            {
                if (Equals(IdentityOf(_rows[i]), previous))
                {
                    index = i;
                    break;
                }
            }

            RestoreSelectedIndex(index);
        }

        ClampScrollAfterRowChange();
        InvalidateVisual();
    }

    private IReadOnlyList<T> Sort(IReadOnlyList<T> rows)
    {
        if (_columns.Count == 0 || rows.Count == 0)
        {
            return rows;
        }

        var column = _columns[Math.Clamp(_sortColumn, 0, _columns.Count - 1)];
        var sorted = rows.ToList();

        sorted.Sort(
            (a, b) =>
            {
                var result = column.SortValue(a).CompareTo(column.SortValue(b));
                return _sortDescending ? -result : result;
            }
        );

        return sorted;
    }

    // ---- Rendering ----------------------------------------------------------

    protected override void RenderCore(DrawingContext context, int firstRow, int lastRow)
    {
        using (context.PushTransform(Matrix.CreateTranslation(-HorizontalOffset, 0)))
        {
            for (var i = firstRow; i <= lastRow; i++)
            {
                var item = _rows[i];
                var y = RowTop(i);

                var colour = RowColour?.Invoke(item);
                DrawRowBackground(
                    context,
                    new Rect(0, y, Math.Max(ScrollableWidth, Bounds.Width), RowHeight),
                    i,
                    colour is { } rgba ? Palette.RowBrush(rgba) : null
                );

                double x = 0;
                foreach (var column in _columns)
                {
                    if (
                        x + column.Width >= HorizontalOffset
                        && x <= HorizontalOffset + ScrollableViewportWidth
                    )
                    {
                        DrawCell(
                            context,
                            column.Format(item),
                            new Rect(
                                x + CellPadding,
                                y,
                                Math.Max(0, column.Width - (CellPadding * 2)),
                                RowHeight
                            ),
                            column.RightAligned,
                            i == SelectedIndex
                        );
                    }

                    x += column.Width;
                }
            }

            RenderHeader(context);
        }
    }

    private void RenderHeader(DrawingContext context)
    {
        var width = Math.Max(ScrollableWidth, Bounds.Width + HorizontalOffset);
        context.FillRectangle(Palette.HeaderBackground, new Rect(0, 0, width, HeaderHeight));
        context.DrawLine(
            Palette.Divider,
            new Point(0, HeaderHeight),
            new Point(width, HeaderHeight)
        );

        double x = 0;
        for (var c = 0; c < _columns.Count; c++)
        {
            var column = _columns[c];

            if (
                x + column.Width >= HorizontalOffset
                && x <= HorizontalOffset + ScrollableViewportWidth
            )
            {
                var label =
                    c == _sortColumn
                        ? $"{column.Title} {(_sortDescending ? "▾" : "▴")}"
                        : column.Title;

                DrawHeaderCell(
                    context,
                    label,
                    new Rect(
                        x + CellPadding,
                        0,
                        Math.Max(0, column.Width - (CellPadding * 2)),
                        HeaderHeight
                    ),
                    column.RightAligned
                );

                context.DrawLine(
                    Palette.Divider,
                    new Point(x + column.Width, 0),
                    new Point(x + column.Width, HeaderHeight)
                );
            }

            x += column.Width;
        }
    }

    protected override void RenderEmpty(DrawingContext context)
    {
        RenderHeader(context);

        if (EmptyMessage.Length == 0)
        {
            return;
        }

        var formatted = TextCache.Get(
            EmptyMessage,
            Math.Max(40, Bounds.Width - 40),
            Palette.HeaderText,
            emphasised: false,
            selected: false
        );

        context.DrawText(
            formatted,
            new Point(
                Math.Max(CellPadding, (Bounds.Width - formatted.Width) / 2),
                HeaderHeight + 16
            )
        );
    }

    // ---- Input --------------------------------------------------------------

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        var point = e.GetPosition(this);

        if (point.Y < HeaderHeight)
        {
            HandleHeaderClick(point);
            return;
        }

        SetSelectedIndex(RowIndexAt(point));
    }

    private void HandleHeaderClick(Point point)
    {
        var x = point.X + HorizontalOffset;
        double accumulated = 0;

        for (var c = 0; c < _columns.Count; c++)
        {
            accumulated += _columns[c].Width;
            if (x < accumulated)
            {
                if (_sortColumn == c)
                {
                    _sortDescending = !_sortDescending;
                }
                else
                {
                    _sortColumn = c;

                    // Numeric columns start descending so the largest is on top,
                    // text columns ascending so they read alphabetically.
                    _sortDescending = _columns[c].RightAligned;
                }

                SetRows(_rows);
                SortChanged?.Invoke(this, EventArgs.Empty);
                return;
            }
        }
    }
}
