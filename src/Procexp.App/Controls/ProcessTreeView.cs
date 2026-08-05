using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Procexp.Model;

namespace Procexp.App.Controls;

/// <summary>
/// The process list: a virtualised tree-table with a frozen name pane beside
/// independently horizontally-scrolling metric columns.
/// </summary>
/// <remarks>
/// Drawn directly rather than composed from per-cell controls, for the same
/// reason the macOS build hand-rolls its table instead of using NSOutlineView:
/// no stock control produces this layout. The defining behaviour is that the
/// process-name column stays put while the metric columns scroll horizontally
/// under their own scrollbar, with both halves sharing one vertical scroll.
/// </remarks>
public sealed class ProcessTreeView : VirtualTableBase
{
    private const double IndentPerLevel = 14;
    private const double ExpanderSize = 9;

    private IReadOnlyList<VisibleRow> _rows = [];
    private IReadOnlyList<(Column Column, double Width)> _columns = [];

    /// <summary>Width of the frozen pane, including the tree indent area.</summary>
    public double NamePaneWidth { get; set; } = 260;

    public IReadOnlyList<ProcessColorRule> ColorRules { get; set; } = ProcessColorRule.Defaults;

    public Column SortColumn { get; set; } = Column.Cpu;
    public bool SortDescending { get; set; } = true;

    public event EventHandler<ProcessId>? ToggleRequested;
    public event EventHandler<Column>? HeaderClicked;

    /// <summary>Raised on double-click; the window opens Properties.</summary>
    public event EventHandler? RowActivated;

    /// <summary>
    /// Raised when the user resizes a column or drags one to a new position.
    /// Carries the new layout, so the window can persist it.
    /// </summary>
    public event EventHandler<IReadOnlyList<(Column Column, double Width)>>? ColumnsChanged;

    // --- Header drag state --------------------------------------------------
    //
    // Two gestures share the header: dragging an edge resizes the column to its
    // left, dragging the body moves the column. Which one begins is decided by
    // where the press lands — inside EdgeGrip pixels of an edge is a resize —
    // and a move only starts once the pointer has travelled far enough to rule
    // out a click, so sorting by clicking a header still works.

    private const double EdgeGrip = 4;
    private const double DragThreshold = 4;
    private const double MinColumnWidth = 40;

    private int _resizingColumn = -1;
    private int _draggingColumn = -1;
    private int _dropTarget = -1;
    private double _pressX;
    private double _pressWidth;
    private bool _dragArmed;

    protected override int RowCount => _rows.Count;

    protected override string? RowSearchText(int row) =>
        row >= 0 && row < _rows.Count ? _rows[row].Process.Name : null;

    /// <summary>
    /// The name column explains the process — path and command line, which are
    /// the two things never fully visible in it. Metric columns only speak up
    /// when their value is actually trimmed.
    /// </summary>
    protected override string? TooltipFor(int row, Point point)
    {
        if (row < 0 || row >= _rows.Count)
        {
            return null;
        }

        var process = _rows[row].Process;

        if (point.X < NamePaneWidth)
        {
            var lines = new List<string> { $"{process.Name} (pid {process.Id.Pid})" };

            if (process.ExecutablePath is { Length: > 0 } path)
            {
                lines.Add(path);
            }

            if (process.CommandLine is { Length: > 0 } commandLine && commandLine != process.Name)
            {
                lines.Add(commandLine);
            }

            if (process.UserName is { Length: > 0 } user)
            {
                lines.Add($"user: {user}");
            }

            return string.Join("\n", lines);
        }

        var x = point.X - NamePaneWidth + HorizontalOffset;
        double accumulated = 0;

        for (var c = 1; c < _columns.Count; c++)
        {
            var (column, width) = _columns[c];
            accumulated += width;

            if (x < accumulated)
            {
                var text = Columns.Format(column, process);
                return IsTruncated(text, width) ? text : null;
            }
        }

        return null;
    }

    /// <summary>
    /// Select and reveal the row for a process, if it is still on screen.
    /// Matched by full identity — pid and start time — so a pid recycled since
    /// the caller captured it selects nothing rather than the wrong process.
    /// </summary>
    public bool SelectProcess(ProcessId id)
    {
        for (var i = 0; i < _rows.Count; i++)
        {
            if (_rows[i].Process.Id == id)
            {
                MoveSelectionTo(i);
                return true;
            }
        }

        return false;
    }

    protected override double FrozenWidth => NamePaneWidth;

    protected override double ScrollableWidth => _columns.Skip(1).Sum(c => c.Width);

    public double MetricsExtentWidth => ScrollableWidth;

    public double MetricsViewportWidth => ScrollableViewportWidth;

    public ProcessRecord? SelectedProcess =>
        SelectedIndex >= 0 && SelectedIndex < _rows.Count ? _rows[SelectedIndex].Process : null;

    /// <summary>
    /// Replace the displayed rows.
    /// </summary>
    /// <remarks>
    /// Selection is re-anchored by process identity rather than by row index. A
    /// refresh can insert or remove rows above the selection, and keeping the
    /// index would silently move the selection to a different process — which
    /// matters a great deal when the next click is Kill.
    /// </remarks>
    public void SetRows(
        IReadOnlyList<VisibleRow> rows,
        IReadOnlyList<(Column Column, double Width)> columns
    )
    {
        var previouslySelected = SelectedProcess?.Id;

        _rows = rows;

        // A sweep lands every second, including mid-drag — and the window only
        // learns the new layout when the gesture ends. Taking its stale copy
        // now would snap the column back under the pointer.
        if (_resizingColumn < 0 && _draggingColumn < 0)
        {
            _columns = columns;
        }

        if (previouslySelected is { } id)
        {
            var index = -1;
            for (var i = 0; i < rows.Count; i++)
            {
                if (rows[i].Process.Id == id)
                {
                    index = i;
                    break;
                }
            }

            RestoreSelectedIndex(index);
        }
        else if (rows.Count > 0)
        {
            // Start with the first row selected. The lower pane and the Process
            // menu are both inert without a selection, so opening to none makes
            // the window look broken until the user guesses to click something.
            SelectFirstRow();
        }

        ClampScrollAfterRowChange();
        InvalidateVisual();
        RefreshTooltip();
    }

    private void SelectFirstRow() => SetSelectedIndex(0);

    // ---- Rendering ----------------------------------------------------------

    protected override void RenderCore(DrawingContext context, int firstRow, int lastRow)
    {
        // Metric columns draw into a clipped, translated region so they scroll
        // under the frozen pane rather than over it.
        var metrics = new Rect(NamePaneWidth, 0, ScrollableViewportWidth, Bounds.Height);

        using (context.PushClip(metrics))
        using (context.PushTransform(Matrix.CreateTranslation(NamePaneWidth - HorizontalOffset, 0)))
        {
            RenderMetricRows(context, firstRow, lastRow);
            RenderMetricHeaders(context);
        }

        // The frozen pane paints last so it always wins over scrolled content.
        using (context.PushClip(new Rect(0, 0, NamePaneWidth, Bounds.Height)))
        {
            RenderNameRows(context, firstRow, lastRow);
            RenderNameHeader(context);
        }

        // The divider marks where the frozen pane ends, otherwise ambiguous once
        // the metrics are scrolled.
        context.DrawLine(
            Palette.Divider,
            new Point(NamePaneWidth, 0),
            new Point(NamePaneWidth, Bounds.Height)
        );
    }

    private void RenderNameRows(DrawingContext context, int firstRow, int lastRow)
    {
        for (var i = firstRow; i <= lastRow; i++)
        {
            var row = _rows[i];
            var y = RowTop(i);

            DrawRowBackground(context, new Rect(0, y, NamePaneWidth, RowHeight), i, RuleBrush(row));

            var indent = CellPadding + (row.Depth * IndentPerLevel);

            if (row.HasChildren)
            {
                DrawExpander(context, new Point(indent, y + (RowHeight / 2)), row.IsExpanded);
            }

            var textLeft = indent + ExpanderSize + 6;
            DrawCell(
                context,
                row.Process.Name,
                new Rect(
                    textLeft,
                    y,
                    Math.Max(0, NamePaneWidth - textLeft - CellPadding),
                    RowHeight
                ),
                rightAligned: false,
                selected: i == SelectedIndex
            );
        }
    }

    private void RenderMetricRows(DrawingContext context, int firstRow, int lastRow)
    {
        for (var i = firstRow; i <= lastRow; i++)
        {
            var row = _rows[i];
            var y = RowTop(i);

            DrawRowBackground(
                context,
                new Rect(0, y, ScrollableWidth, RowHeight),
                i,
                RuleBrush(row)
            );

            double x = 0;
            for (var c = 1; c < _columns.Count; c++)
            {
                var (column, width) = _columns[c];

                // Skip columns scrolled out of view. With twenty-odd configured
                // that is most of them.
                if (
                    x + width >= HorizontalOffset
                    && x <= HorizontalOffset + ScrollableViewportWidth
                )
                {
                    var text = Columns.Format(column, row.Process);
                    if (text.Length > 0)
                    {
                        DrawCell(
                            context,
                            text,
                            new Rect(
                                x + CellPadding,
                                y,
                                Math.Max(0, width - (CellPadding * 2)),
                                RowHeight
                            ),
                            Columns.IsRightAligned(column),
                            i == SelectedIndex
                        );
                    }
                }

                x += width;
            }
        }
    }

    private IBrush? RuleBrush(VisibleRow row)
    {
        var colour = ProcessColorRule.Background(row.Process.Flags, ColorRules, IsDarkMode);
        return colour is { } rgba ? Palette.RowBrush(rgba) : null;
    }

    private void RenderNameHeader(DrawingContext context)
    {
        context.FillRectangle(
            Palette.HeaderBackground,
            new Rect(0, 0, NamePaneWidth, HeaderHeight)
        );
        context.DrawLine(
            Palette.Divider,
            new Point(0, HeaderHeight),
            new Point(NamePaneWidth, HeaderHeight)
        );

        DrawHeaderCell(
            context,
            Label(Column.Name),
            new Rect(CellPadding, 0, NamePaneWidth - (CellPadding * 2), HeaderHeight),
            rightAligned: false
        );
    }

    private void RenderMetricHeaders(DrawingContext context)
    {
        context.FillRectangle(
            Palette.HeaderBackground,
            new Rect(0, 0, ScrollableWidth, HeaderHeight)
        );
        context.DrawLine(
            Palette.Divider,
            new Point(0, HeaderHeight),
            new Point(ScrollableWidth, HeaderHeight)
        );

        double x = 0;
        for (var c = 1; c < _columns.Count; c++)
        {
            var (column, width) = _columns[c];

            if (x + width >= HorizontalOffset && x <= HorizontalOffset + ScrollableViewportWidth)
            {
                DrawHeaderCell(
                    context,
                    Label(column),
                    new Rect(
                        x + CellPadding,
                        0,
                        Math.Max(0, width - (CellPadding * 2)),
                        HeaderHeight
                    ),
                    Columns.IsRightAligned(column)
                );

                context.DrawLine(
                    Palette.Divider,
                    new Point(x + width, 0),
                    new Point(x + width, HeaderHeight)
                );

                // Where a dragged column would land, drawn as an insertion bar
                // so the drop is predictable rather than a guess.
                if (_dropTarget == c && _draggingColumn > 0 && _dragArmed)
                {
                    var edge = _dropTarget > _draggingColumn ? x + width : x;
                    context.DrawLine(
                        Palette.DropIndicator,
                        new Point(edge, 0),
                        new Point(edge, HeaderHeight)
                    );
                }
            }

            x += width;
        }
    }

    private string Label(Column column) =>
        SortColumn == column
            ? $"{Columns.Title(column)} {(SortDescending ? "▾" : "▴")}"
            : Columns.Title(column);

    private void DrawExpander(DrawingContext context, Point centre, bool expanded)
    {
        var half = ExpanderSize / 2;

        var geometry = new StreamGeometry();
        using (var sink = geometry.Open())
        {
            if (expanded)
            {
                sink.BeginFigure(new Point(centre.X - half, centre.Y - (half / 2)), true);
                sink.LineTo(new Point(centre.X + half, centre.Y - (half / 2)));
                sink.LineTo(new Point(centre.X, centre.Y + (half / 2) + 1));
            }
            else
            {
                sink.BeginFigure(new Point(centre.X - (half / 2), centre.Y - half), true);
                sink.LineTo(new Point(centre.X + (half / 2) + 1, centre.Y));
                sink.LineTo(new Point(centre.X - (half / 2), centre.Y + half));
            }

            sink.EndFigure(true);
        }

        context.DrawGeometry(Palette.Expander, null, geometry);
    }

    // ---- Input --------------------------------------------------------------

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        var point = e.GetPosition(this);

        if (point.Y < HeaderHeight)
        {
            // A press on the header may become a resize, a reorder or a sort;
            // which it is depends on where it lands and what happens next.
            var edge = EdgeAt(point);
            if (edge >= 0)
            {
                _resizingColumn = edge;
                _pressX = point.X;
                _pressWidth = edge == 0 ? NamePaneWidth : _columns[edge].Width;
                e.Pointer.Capture(this);
                return;
            }

            var column = ColumnAt(point);
            if (column > 0)
            {
                _draggingColumn = column;
                _dragArmed = false;
                _pressX = point.X;
                e.Pointer.Capture(this);
            }

            return;
        }

        var index = RowIndexAt(point);
        if (index < 0)
        {
            return;
        }

        var row = _rows[index];

        // Clicking the expander toggles rather than selects, so the two do not
        // fight over the same pixel.
        if (row.HasChildren && point.X < NamePaneWidth)
        {
            var expanderX = CellPadding + (row.Depth * IndentPerLevel);
            if (point.X >= expanderX - 4 && point.X <= expanderX + ExpanderSize + 4)
            {
                ToggleRequested?.Invoke(this, row.Process.Id);
                return;
            }
        }

        SetSelectedIndex(index);

        // Left only: a fast double right-click should not drag Properties into
        // what was meant as a context-menu gesture.
        if (e.ClickCount == 2 && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            RowActivated?.Invoke(this, EventArgs.Empty);
        }
    }

    private void HandleHeaderClick(Point point)
    {
        var column = ColumnAt(point);
        if (column >= 0)
        {
            HeaderClicked?.Invoke(this, _columns[column].Column);
        }
    }

    /// <summary>Index of the column whose header contains this point, or -1.</summary>
    private int ColumnAt(Point point)
    {
        if (point.X < NamePaneWidth)
        {
            return 0;
        }

        var x = point.X - NamePaneWidth + HorizontalOffset;
        double accumulated = 0;

        for (var c = 1; c < _columns.Count; c++)
        {
            accumulated += _columns[c].Width;
            if (x < accumulated)
            {
                return c;
            }
        }

        return -1;
    }

    /// <summary>
    /// Index of the column whose right edge this point grips, or -1. The name
    /// pane's edge is column 0's: dragging it resizes the frozen pane, which is
    /// the same gesture users expect from the splitter.
    /// </summary>
    private int EdgeAt(Point point)
    {
        if (Math.Abs(point.X - NamePaneWidth) <= EdgeGrip)
        {
            return 0;
        }

        if (point.X < NamePaneWidth)
        {
            return -1;
        }

        var x = point.X - NamePaneWidth + HorizontalOffset;
        double accumulated = 0;

        for (var c = 1; c < _columns.Count; c++)
        {
            accumulated += _columns[c].Width;
            if (Math.Abs(x - accumulated) <= EdgeGrip)
            {
                return c;
            }
        }

        return -1;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        var point = e.GetPosition(this);

        if (_resizingColumn >= 0)
        {
            var width = Math.Max(MinColumnWidth, _pressWidth + (point.X - _pressX));

            if (_resizingColumn == 0)
            {
                NamePaneWidth = Math.Clamp(width, 120, 900);
            }
            else
            {
                var updated = _columns.ToList();
                updated[_resizingColumn] = (updated[_resizingColumn].Column, width);
                _columns = updated;
            }

            InvalidateVisual();
            return;
        }

        if (_draggingColumn > 0)
        {
            if (!_dragArmed && Math.Abs(point.X - _pressX) < DragThreshold)
            {
                return;
            }

            _dragArmed = true;
            var target = ColumnAt(point);
            _dropTarget = target > 0 ? target : _dropTarget;
            InvalidateVisual();
            return;
        }

        // Not dragging: the cursor advertises what the header edge under it does.
        Cursor =
            point.Y < HeaderHeight && EdgeAt(point) >= 0
                ? new Cursor(StandardCursorType.SizeWestEast)
                : Cursor.Default;

        // Tooltips only when no gesture owns the pointer; one during a drag
        // would follow the pointer around explaining the wrong row.
        UpdateTooltip(point);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);

        var wasResizing = _resizingColumn >= 0;
        var wasDragging = _draggingColumn > 0 && _dragArmed;
        var dragged = _draggingColumn;
        var target = _dropTarget;

        _resizingColumn = -1;
        _draggingColumn = -1;
        _dropTarget = -1;
        e.Pointer.Capture(null);

        if (wasResizing)
        {
            ColumnsChanged?.Invoke(this, _columns);
            InvalidateVisual();
            return;
        }

        if (wasDragging && target > 0 && target != dragged)
        {
            var updated = _columns.ToList();
            var moved = updated[dragged];
            updated.RemoveAt(dragged);
            updated.Insert(target, moved);
            _columns = updated;

            ColumnsChanged?.Invoke(this, _columns);
            InvalidateVisual();
            return;
        }

        // A press that neither resized nor moved anything is a sort click.
        if (!_dragArmed && e.GetPosition(this).Y < HeaderHeight)
        {
            HandleHeaderClick(e.GetPosition(this));
        }

        _dragArmed = false;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        // Left and right collapse and expand before the base class treats them
        // as navigation.
        if (SelectedIndex >= 0 && SelectedIndex < _rows.Count)
        {
            var row = _rows[SelectedIndex];

            if (e.Key == Key.Left && row.IsExpanded && row.HasChildren)
            {
                ToggleRequested?.Invoke(this, row.Process.Id);
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Right && !row.IsExpanded && row.HasChildren)
            {
                ToggleRequested?.Invoke(this, row.Process.Id);
                e.Handled = true;
                return;
            }
        }

        base.OnKeyDown(e);
    }
}
