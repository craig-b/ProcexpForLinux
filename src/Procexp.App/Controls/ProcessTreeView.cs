using System.Globalization;
using Avalonia;
using Avalonia.Controls;
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
///
/// Cost is governed by how many rows are visible, not by how many exist. At a
/// typical window size that is around 40 rows regardless of whether the machine
/// is running 600 processes or 6,000.
/// </remarks>
public sealed class ProcessTreeView : Control
{
    private const double RowHeight = 20;
    private const double HeaderHeight = 24;
    private const double IndentPerLevel = 14;
    private const double ExpanderSize = 9;
    private const double CellPadding = 6;

    private readonly Typeface _typeface = new(FontFamily.Default);
    private readonly Typeface _boldTypeface = new(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);

    private readonly FormattedTextCache _textCache;
    private readonly FormattedTextCache _headerTextCache;

    // Brushes are held rather than rebuilt per access. Constructing a
    // SolidColorBrush per cell is cheap individually and not cheap several
    // hundred times a frame.
    private readonly Dictionary<Rgba, IBrush> _rowBrushes = [];

    private IReadOnlyList<VisibleRow> _rows = [];
    private IReadOnlyList<(Column Column, double Width)> _columns = [];

    private double _verticalOffset;
    private double _horizontalOffset;
    private int _selectedIndex = -1;

    /// <summary>Width of the frozen pane, including the tree indent area.</summary>
    public double NamePaneWidth { get; set; } = 260;

    public bool IsDarkMode { get; set; }

    public IReadOnlyList<ProcessColorRule> ColorRules { get; set; } = ProcessColorRule.Defaults;

    public event EventHandler<ProcessRecord?>? SelectionChanged;
    public event EventHandler<ProcessId>? ToggleRequested;
    public event EventHandler<Column>? HeaderClicked;

    public Column SortColumn { get; set; } = Column.Cpu;
    public bool SortDescending { get; set; } = true;

    public ProcessTreeView()
    {
        Focusable = true;
        ClipToBounds = true;

        _textCache = new FormattedTextCache(_typeface, 12);
        _headerTextCache = new FormattedTextCache(_boldTypeface, 12);
    }

    public ProcessRecord? SelectedProcess =>
        _selectedIndex >= 0 && _selectedIndex < _rows.Count ? _rows[_selectedIndex].Process : null;

    /// <summary>Total scrollable height, for the vertical scrollbar.</summary>
    public double ExtentHeight => _rows.Count * RowHeight;

    /// <summary>Total width of the metric columns, for the horizontal scrollbar.</summary>
    public double MetricsExtentWidth => _columns.Skip(1).Sum(c => c.Width);

    public double ViewportHeight => Math.Max(0, Bounds.Height - HeaderHeight);

    public double MetricsViewportWidth => Math.Max(0, Bounds.Width - NamePaneWidth);

    public double VerticalOffset
    {
        get => _verticalOffset;
        set
        {
            var clamped = Math.Clamp(value, 0, Math.Max(0, ExtentHeight - ViewportHeight));
            if (Math.Abs(clamped - _verticalOffset) > 0.01)
            {
                _verticalOffset = clamped;
                InvalidateVisual();
            }
        }
    }

    public double HorizontalOffset
    {
        get => _horizontalOffset;
        set
        {
            var clamped = Math.Clamp(value, 0, Math.Max(0, MetricsExtentWidth - MetricsViewportWidth));
            if (Math.Abs(clamped - _horizontalOffset) > 0.01)
            {
                _horizontalOffset = clamped;
                InvalidateVisual();
            }
        }
    }

    /// <summary>
    /// Replace the displayed rows.
    /// </summary>
    /// <remarks>
    /// Selection is re-anchored by process identity rather than by row index. A
    /// refresh can insert or remove rows above the selection, and keeping the
    /// index would silently move the selection to a different process — which
    /// matters a great deal when the next click is Kill.
    /// </remarks>
    public void SetRows(IReadOnlyList<VisibleRow> rows, IReadOnlyList<(Column Column, double Width)> columns)
    {
        var previouslySelected = SelectedProcess?.Id;

        _rows = rows;
        _columns = columns;

        if (previouslySelected is { } id)
        {
            _selectedIndex = -1;
            for (var i = 0; i < rows.Count; i++)
            {
                if (rows[i].Process.Id == id)
                {
                    _selectedIndex = i;
                    break;
                }
            }
        }

        // Rows may have gone away beneath the current offset.
        _verticalOffset = Math.Clamp(_verticalOffset, 0, Math.Max(0, ExtentHeight - ViewportHeight));

        InvalidateVisual();
    }

    // ---- Rendering ----------------------------------------------------------

    /// <summary>
    /// Rolling average paint time. Exposed so the shell can show it, and because
    /// proving this stays flat as the process count grows is the whole point of
    /// drawing the table by hand.
    /// </summary>
    public double AverageRenderMilliseconds =>
        _renderTimes.Count == 0 ? 0 : _renderTimes.Average();

    /// <summary>Rows actually painted in the last frame, as opposed to rows held.</summary>
    public int LastRenderedRowCount { get; private set; }

    private readonly Queue<double> _renderTimes = new();

    public override void Render(DrawingContext context)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();

        _textCache.BeginFrame();
        _headerTextCache.BeginFrame();

        RenderCore(context);

        _renderTimes.Enqueue(watch.Elapsed.TotalMilliseconds);
        while (_renderTimes.Count > 30)
        {
            _renderTimes.Dequeue();
        }
    }

    private void RenderCore(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        context.FillRectangle(Background, bounds);

        if (_columns.Count == 0)
        {
            return;
        }

        var firstRow = Math.Max(0, (int)(_verticalOffset / RowHeight));
        var lastRow = Math.Min(_rows.Count - 1, (int)((_verticalOffset + ViewportHeight) / RowHeight) + 1);
        LastRenderedRowCount = Math.Max(0, lastRow - firstRow + 1);

        // The metric columns are drawn into a clipped, translated region so they
        // scroll under the frozen pane rather than over it.
        var metricsRegion = new Rect(NamePaneWidth, 0, MetricsViewportWidth, Bounds.Height);

        using (context.PushClip(metricsRegion))
        using (context.PushTransform(Matrix.CreateTranslation(NamePaneWidth - _horizontalOffset, 0)))
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

        // The divider marks where the frozen pane ends, which is otherwise
        // ambiguous once the metrics are scrolled.
        context.DrawLine(DividerPen, new Point(NamePaneWidth, 0), new Point(NamePaneWidth, Bounds.Height));
    }

    private void RenderNameRows(DrawingContext context, int firstRow, int lastRow)
    {
        for (var i = firstRow; i <= lastRow; i++)
        {
            var row = _rows[i];
            var y = HeaderHeight + (i * RowHeight) - _verticalOffset;

            var rowRect = new Rect(0, y, NamePaneWidth, RowHeight);
            DrawRowBackground(context, rowRect, row, i);

            var indent = CellPadding + (row.Depth * IndentPerLevel);

            if (row.HasChildren)
            {
                DrawExpander(context, new Point(indent, y + (RowHeight / 2)), row.IsExpanded);
            }

            var textLeft = indent + ExpanderSize + 6;
            DrawText(
                context,
                row.Process.Name,
                new Rect(textLeft, y, Math.Max(0, NamePaneWidth - textLeft - CellPadding), RowHeight),
                rightAligned: false,
                selected: i == _selectedIndex);
        }
    }

    private void RenderMetricRows(DrawingContext context, int firstRow, int lastRow)
    {
        for (var i = firstRow; i <= lastRow; i++)
        {
            var row = _rows[i];
            var y = HeaderHeight + (i * RowHeight) - _verticalOffset;

            DrawRowBackground(context, new Rect(0, y, MetricsExtentWidth, RowHeight), row, i);

            double x = 0;
            for (var c = 1; c < _columns.Count; c++)
            {
                var (column, width) = _columns[c];

                // Skip columns scrolled entirely out of view. With twenty-odd
                // columns configured this is most of them.
                if (x + width >= _horizontalOffset && x <= _horizontalOffset + MetricsViewportWidth)
                {
                    var text = Columns.Format(column, row.Process);
                    if (text.Length > 0)
                    {
                        DrawText(
                            context,
                            text,
                            new Rect(x + CellPadding, y, Math.Max(0, width - (CellPadding * 2)), RowHeight),
                            Columns.IsRightAligned(column),
                            i == _selectedIndex);
                    }
                }

                x += width;
            }
        }
    }

    private void RenderNameHeader(DrawingContext context)
    {
        var rect = new Rect(0, 0, NamePaneWidth, HeaderHeight);
        context.FillRectangle(HeaderBackground, rect);
        context.DrawLine(DividerPen, new Point(0, HeaderHeight), new Point(NamePaneWidth, HeaderHeight));

        DrawHeaderText(context, Columns.Title(Column.Name), new Rect(CellPadding, 0, NamePaneWidth, HeaderHeight),
            rightAligned: false, isSortColumn: SortColumn == Column.Name);
    }

    private void RenderMetricHeaders(DrawingContext context)
    {
        context.FillRectangle(HeaderBackground, new Rect(0, 0, MetricsExtentWidth, HeaderHeight));
        context.DrawLine(DividerPen, new Point(0, HeaderHeight), new Point(MetricsExtentWidth, HeaderHeight));

        double x = 0;
        for (var c = 1; c < _columns.Count; c++)
        {
            var (column, width) = _columns[c];

            if (x + width >= _horizontalOffset && x <= _horizontalOffset + MetricsViewportWidth)
            {
                DrawHeaderText(
                    context,
                    Columns.Title(column),
                    new Rect(x + CellPadding, 0, Math.Max(0, width - (CellPadding * 2)), HeaderHeight),
                    Columns.IsRightAligned(column),
                    SortColumn == column);

                context.DrawLine(DividerPen, new Point(x + width, 0), new Point(x + width, HeaderHeight));
            }

            x += width;
        }
    }

    private void DrawRowBackground(DrawingContext context, Rect rect, VisibleRow row, int index)
    {
        if (index == _selectedIndex)
        {
            context.FillRectangle(SelectionBrush, rect);
            return;
        }

        var colour = ProcessColorRule.Background(row.Process.Flags, ColorRules, IsDarkMode);
        if (colour is { } rgba)
        {
            if (!_rowBrushes.TryGetValue(rgba, out var brush))
            {
                brush = new SolidColorBrush(Color.FromArgb(
                    (byte)(rgba.A * 255), (byte)(rgba.R * 255), (byte)(rgba.G * 255), (byte)(rgba.B * 255)));
                _rowBrushes[rgba] = brush;
            }

            context.FillRectangle(brush, rect);
        }
        else if (index % 2 == 1)
        {
            context.FillRectangle(AlternateRowBrush, rect);
        }
    }

    private void DrawExpander(DrawingContext context, Point centre, bool expanded)
    {
        var half = ExpanderSize / 2;

        // A filled triangle, pointing down when expanded and right when not.
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

        context.DrawGeometry(ExpanderBrush, null, geometry);
    }

    private void DrawText(DrawingContext context, string text, Rect rect, bool rightAligned, bool selected)
    {
        if (rect.Width <= 1 || text.Length == 0)
        {
            return;
        }

        var formatted = _textCache.Get(
            text, rect.Width, selected ? SelectedTextBrush : TextBrush, emphasised: false, selected);

        var x = rightAligned ? rect.Right - Math.Min(formatted.Width, rect.Width) : rect.X;
        context.DrawText(formatted, new Point(x, rect.Y + ((RowHeight - formatted.Height) / 2)));
    }

    private void DrawHeaderText(DrawingContext context, string text, Rect rect, bool rightAligned, bool isSortColumn)
    {
        var label = isSortColumn ? $"{text} {(SortDescending ? "▾" : "▴")}" : text;

        var formatted = _headerTextCache.Get(
            label, rect.Width, HeaderTextBrush, emphasised: true, selected: false);

        var x = rightAligned ? rect.Right - Math.Min(formatted.Width, rect.Width) : rect.X;
        context.DrawText(formatted, new Point(x, (HeaderHeight - formatted.Height) / 2));
    }

    // ---- Interaction --------------------------------------------------------

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();

        var point = e.GetPosition(this);

        if (point.Y < HeaderHeight)
        {
            HandleHeaderClick(point);
            return;
        }

        var index = (int)((point.Y - HeaderHeight + _verticalOffset) / RowHeight);
        if (index < 0 || index >= _rows.Count)
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

        Select(index);
    }

    private void HandleHeaderClick(Point point)
    {
        if (point.X < NamePaneWidth)
        {
            HeaderClicked?.Invoke(this, Column.Name);
            return;
        }

        var x = point.X - NamePaneWidth + _horizontalOffset;
        double accumulated = 0;

        for (var c = 1; c < _columns.Count; c++)
        {
            accumulated += _columns[c].Width;
            if (x < accumulated)
            {
                HeaderClicked?.Invoke(this, _columns[c].Column);
                return;
            }
        }
    }

    private void Select(int index)
    {
        if (index == _selectedIndex)
        {
            return;
        }

        _selectedIndex = index;
        SelectionChanged?.Invoke(this, SelectedProcess);
        InvalidateVisual();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);

        // Shift converts the wheel to horizontal, which is the convention for
        // panes that scroll both ways.
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            HorizontalOffset -= e.Delta.Y * 40;
        }
        else
        {
            VerticalOffset -= e.Delta.Y * RowHeight * 3;
        }

        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        var pageRows = Math.Max(1, (int)(ViewportHeight / RowHeight) - 1);

        switch (e.Key)
        {
            case Key.Down:
                MoveSelection(1);
                break;
            case Key.Up:
                MoveSelection(-1);
                break;
            case Key.PageDown:
                MoveSelection(pageRows);
                break;
            case Key.PageUp:
                MoveSelection(-pageRows);
                break;
            case Key.Home:
                MoveSelectionTo(0);
                break;
            case Key.End:
                MoveSelectionTo(_rows.Count - 1);
                break;
            case Key.Left when _selectedIndex >= 0 && _rows[_selectedIndex].IsExpanded:
                ToggleRequested?.Invoke(this, _rows[_selectedIndex].Process.Id);
                break;
            case Key.Right when _selectedIndex >= 0 && !_rows[_selectedIndex].IsExpanded:
                ToggleRequested?.Invoke(this, _rows[_selectedIndex].Process.Id);
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private void MoveSelection(int delta) =>
        MoveSelectionTo(Math.Clamp(_selectedIndex + delta, 0, _rows.Count - 1));

    private void MoveSelectionTo(int index)
    {
        if (_rows.Count == 0)
        {
            return;
        }

        Select(Math.Clamp(index, 0, _rows.Count - 1));
        ScrollIntoView(_selectedIndex);
    }

    private void ScrollIntoView(int index)
    {
        var top = index * RowHeight;
        var bottom = top + RowHeight;

        if (top < _verticalOffset)
        {
            VerticalOffset = top;
        }
        else if (bottom > _verticalOffset + ViewportHeight)
        {
            VerticalOffset = bottom - ViewportHeight;
        }
    }

    // ---- Brushes ------------------------------------------------------------

    // Frozen per theme rather than constructed per use. These are read several
    // hundred times a frame.
    private static readonly IBrush LightBackground = Brushes.White;
    private static readonly IBrush DarkBackground = new SolidColorBrush(Color.FromRgb(30, 30, 30));
    private static readonly IBrush LightHeaderBackground = new SolidColorBrush(Color.FromRgb(240, 240, 240));
    private static readonly IBrush DarkHeaderBackground = new SolidColorBrush(Color.FromRgb(45, 45, 45));
    private static readonly IBrush LightText = Brushes.Black;
    private static readonly IBrush DarkText = new SolidColorBrush(Color.FromRgb(230, 230, 230));
    private static readonly IBrush LightHeaderText = new SolidColorBrush(Color.FromRgb(40, 40, 40));
    private static readonly IBrush DarkHeaderText = new SolidColorBrush(Color.FromRgb(210, 210, 210));
    private static readonly IBrush LightAlternateRow = new SolidColorBrush(Color.FromRgb(248, 248, 248));
    private static readonly IBrush DarkAlternateRow = new SolidColorBrush(Color.FromRgb(36, 36, 36));
    private static readonly IBrush LightExpander = new SolidColorBrush(Color.FromRgb(80, 80, 80));
    private static readonly IBrush DarkExpander = new SolidColorBrush(Color.FromRgb(180, 180, 180));
    private static readonly IPen LightDivider = new Pen(new SolidColorBrush(Color.FromRgb(210, 210, 210)), 1);
    private static readonly IPen DarkDivider = new Pen(new SolidColorBrush(Color.FromRgb(60, 60, 60)), 1);

    private static readonly IBrush Selection = new SolidColorBrush(Color.FromRgb(0, 92, 168));

    private IBrush Background => IsDarkMode ? DarkBackground : LightBackground;
    private IBrush HeaderBackground => IsDarkMode ? DarkHeaderBackground : LightHeaderBackground;
    private IBrush TextBrush => IsDarkMode ? DarkText : LightText;
    private IBrush HeaderTextBrush => IsDarkMode ? DarkHeaderText : LightHeaderText;
    private IBrush AlternateRowBrush => IsDarkMode ? DarkAlternateRow : LightAlternateRow;
    private IBrush ExpanderBrush => IsDarkMode ? DarkExpander : LightExpander;
    private IPen DividerPen => IsDarkMode ? DarkDivider : LightDivider;

    private static IBrush SelectedTextBrush => Brushes.White;
    private static IBrush SelectionBrush => Selection;
}
