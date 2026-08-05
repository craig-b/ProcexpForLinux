using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace Procexp.App.Controls;

/// <summary>
/// Shared machinery for the hand-drawn tables: scrolling, virtualisation
/// bookkeeping, selection, keyboard navigation and paint timing.
/// </summary>
/// <remarks>
/// Extracted from the process list so the lower pane does not repeat it. What is
/// deliberately <em>not</em> here is the drawing itself — the process list has a
/// frozen pane and a tree indent, the lower pane has neither, and forcing both
/// through one render path would need more hooks than the duplication it saves.
/// Subclasses implement <see cref="RenderCore"/> and get everything around it.
/// </remarks>
public abstract class VirtualTableBase : Control
{
    protected const double RowHeight = 20;
    protected const double HeaderHeight = 24;
    protected const double CellPadding = 6;

    private readonly Queue<double> _renderTimes = new();

    private double _verticalOffset;
    private double _horizontalOffset;
    private int _selectedIndex = -1;

    protected VirtualTableBase()
    {
        Focusable = true;
        ClipToBounds = true;

        TextCache = new FormattedTextCache(new Typeface(FontFamily.Default), 12);
        HeaderTextCache = new FormattedTextCache(
            new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold),
            12
        );

        // Selection is drawn differently when the pane is not focused, so both
        // transitions have to repaint.
        GotFocus += OnFocusChanged;
        LostFocus += OnFocusChanged;
    }

    protected FormattedTextCache TextCache { get; }

    protected FormattedTextCache HeaderTextCache { get; }

    protected TablePalette Palette => TablePalette.For(IsDarkMode);

    public bool IsDarkMode { get; set; }

    /// <summary>Number of rows the table holds, whether visible or not.</summary>
    protected abstract int RowCount { get; }

    /// <summary>Total width of horizontally-scrollable content.</summary>
    protected abstract double ScrollableWidth { get; }

    /// <summary>Width of any fixed region the horizontal scroll does not move.</summary>
    protected virtual double FrozenWidth => 0;

    public event EventHandler? SelectionChanged;

    // ---- Geometry -----------------------------------------------------------

    public double ExtentHeight => RowCount * RowHeight;

    public double ViewportHeight => Math.Max(0, Bounds.Height - HeaderHeight);

    public double ScrollableViewportWidth => Math.Max(0, Bounds.Width - FrozenWidth);

    public double MaxVerticalOffset => Math.Max(0, ExtentHeight - ViewportHeight);

    public double MaxHorizontalOffset => Math.Max(0, ScrollableWidth - ScrollableViewportWidth);

    public double VerticalOffset
    {
        get => _verticalOffset;
        set
        {
            var clamped = Math.Clamp(value, 0, MaxVerticalOffset);
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
            var clamped = Math.Clamp(value, 0, MaxHorizontalOffset);
            if (Math.Abs(clamped - _horizontalOffset) > 0.01)
            {
                _horizontalOffset = clamped;
                InvalidateVisual();
            }
        }
    }

    public int SelectedIndex => _selectedIndex;

    // ---- Diagnostics --------------------------------------------------------

    /// <summary>Rolling average paint time, for the status bar readout.</summary>
    public double AverageRenderMilliseconds => _renderTimes.Count == 0 ? 0 : _renderTimes.Average();

    /// <summary>Rows painted in the last frame, as opposed to rows held.</summary>
    public int LastRenderedRowCount { get; private set; }

    // ---- Rendering ----------------------------------------------------------

    public sealed override void Render(DrawingContext context)
    {
        var watch = Stopwatch.StartNew();

        TextCache.BeginFrame();
        HeaderTextCache.BeginFrame();

        context.FillRectangle(Palette.Background, new Rect(Bounds.Size));

        if (RowCount > 0)
        {
            var first = Math.Max(0, (int)(_verticalOffset / RowHeight));
            var last = Math.Min(
                RowCount - 1,
                (int)((_verticalOffset + ViewportHeight) / RowHeight) + 1
            );
            LastRenderedRowCount = Math.Max(0, last - first + 1);

            RenderCore(context, first, last);
        }
        else
        {
            LastRenderedRowCount = 0;
            RenderEmpty(context);
        }

        _renderTimes.Enqueue(watch.Elapsed.TotalMilliseconds);
        while (_renderTimes.Count > 30)
        {
            _renderTimes.Dequeue();
        }
    }

    /// <summary>Draw the header and the rows in <c>[firstRow, lastRow]</c>.</summary>
    protected abstract void RenderCore(DrawingContext context, int firstRow, int lastRow);

    /// <summary>Draw whatever should appear when there is nothing to show.</summary>
    protected virtual void RenderEmpty(DrawingContext context) { }

    /// <summary>Y coordinate of a row, accounting for the header and scroll.</summary>
    protected double RowTop(int index) => HeaderHeight + (index * RowHeight) - _verticalOffset;

    /// <summary>Draw one cell of text, vertically centred and trimmed to fit.</summary>
    protected void DrawCell(
        DrawingContext context,
        string text,
        Rect rect,
        bool rightAligned,
        bool selected
    )
    {
        if (rect.Width <= 1 || text.Length == 0)
        {
            return;
        }

        var formatted = TextCache.Get(
            text,
            rect.Width,
            selected ? Palette.SelectedText : Palette.Text,
            emphasised: false,
            selected
        );

        var x = rightAligned ? rect.Right - Math.Min(formatted.Width, rect.Width) : rect.X;
        context.DrawText(formatted, new Point(x, rect.Y + ((RowHeight - formatted.Height) / 2)));
    }

    protected void DrawHeaderCell(
        DrawingContext context,
        string label,
        Rect rect,
        bool rightAligned
    )
    {
        if (rect.Width <= 1 || label.Length == 0)
        {
            return;
        }

        var formatted = HeaderTextCache.Get(
            label,
            rect.Width,
            Palette.HeaderText,
            emphasised: true,
            selected: false
        );

        var x = rightAligned ? rect.Right - Math.Min(formatted.Width, rect.Width) : rect.X;
        context.DrawText(formatted, new Point(x, (HeaderHeight - formatted.Height) / 2));
    }

    /// <summary>Fill a row background: selection, rule colour, or banding.</summary>
    protected void DrawRowBackground(
        DrawingContext context,
        Rect rect,
        int index,
        IBrush? ruleBrush
    )
    {
        if (index == _selectedIndex)
        {
            context.FillRectangle(IsFocused ? Palette.Selection : Palette.InactiveSelection, rect);
            return;
        }

        if (ruleBrush is not null)
        {
            context.FillRectangle(ruleBrush, rect);
        }
        else if (index % 2 == 1)
        {
            context.FillRectangle(Palette.AlternateRow, rect);
        }
    }

    // ---- Selection ----------------------------------------------------------

    /// <summary>Select a row, or clear the selection with -1.</summary>
    protected void SetSelectedIndex(int index, bool notify = true)
    {
        var clamped = index < 0 || index >= RowCount ? -1 : index;
        if (clamped == _selectedIndex)
        {
            return;
        }

        _selectedIndex = clamped;

        if (notify)
        {
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }

        InvalidateVisual();
    }

    /// <summary>
    /// Re-establish the selection after the row set changes.
    /// </summary>
    /// <remarks>
    /// Callers match on identity rather than index. A refresh that inserts or
    /// removes rows above the selection would otherwise silently move it to a
    /// different item — which matters when the next action is destructive.
    /// </remarks>
    protected void RestoreSelectedIndex(int index) => _selectedIndex = index;

    protected void ClampScrollAfterRowChange() =>
        _verticalOffset = Math.Clamp(_verticalOffset, 0, MaxVerticalOffset);

    /// <summary>Row index at a point, or -1 when the point is not over a row.</summary>
    protected int RowIndexAt(Point point)
    {
        if (point.Y < HeaderHeight)
        {
            return -1;
        }

        var index = (int)((point.Y - HeaderHeight + _verticalOffset) / RowHeight);
        return index >= 0 && index < RowCount ? index : -1;
    }

    // ---- Input --------------------------------------------------------------

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);

        // Shift converts the wheel to horizontal, the convention for panes that
        // scroll both ways.
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            HorizontalOffset -= e.Delta.Y * 40;
        }
        else
        {
            VerticalOffset -= e.Delta.Y * RowHeight * 3;
        }

        ScrollChanged?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    /// <summary>Raised when the control scrolls itself, so scrollbars can follow.</summary>
    public event EventHandler? ScrollChanged;

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (e.Handled || RowCount == 0)
        {
            return;
        }

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
                MoveSelectionTo(RowCount - 1);
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    // --- Type-to-select ----------------------------------------------------

    private string _typeBuffer = "";
    private DateTime _typeBufferAt;
    private static readonly TimeSpan TypeSelectTimeout = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The text a row is known by for type-to-select — the process name, the
    /// module name, the variable. Null (the default) leaves typing unhandled.
    /// </summary>
    protected virtual string? RowSearchText(int row) => null;

    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);

        // Space stays free: it is the pause key in the main window, and no
        // process is ever known by a name starting with a space.
        if (e.Handled || RowCount == 0 || string.IsNullOrWhiteSpace(e.Text))
        {
            return;
        }

        var now = DateTime.UtcNow;
        if (now - _typeBufferAt > TypeSelectTimeout)
        {
            _typeBuffer = "";
        }

        _typeBufferAt = now;
        _typeBuffer += e.Text;

        e.Handled = SelectByTyping(_typeBuffer);
    }

    private bool SelectByTyping(string buffer)
    {
        // Repeating one letter cycles through that letter's matches, starting
        // after the selection; anything else is a growing prefix from the top.
        var cycling = buffer.Length > 1 && buffer.All(c => c == buffer[0]);
        var needle = cycling ? buffer[..1] : buffer;
        var start = cycling ? SelectedIndex + 1 : 0;

        int? containsMatch = null;
        for (var i = 0; i < RowCount; i++)
        {
            var row = (start + i) % RowCount;
            var text = RowSearchText(row);
            if (text is null)
            {
                continue;
            }

            if (text.StartsWith(needle, StringComparison.OrdinalIgnoreCase))
            {
                MoveSelectionTo(row);
                return true;
            }

            // A substring hit is remembered but a prefix hit anywhere wins,
            // so "fire" finds firefox before it settles for aupdatefirmware.
            if (
                containsMatch is null
                && !cycling
                && text.Contains(needle, StringComparison.OrdinalIgnoreCase)
            )
            {
                containsMatch = row;
            }
        }

        if (containsMatch is { } fallback)
        {
            MoveSelectionTo(fallback);
            return true;
        }

        return false;
    }

    protected void MoveSelection(int delta) =>
        MoveSelectionTo(Math.Clamp(_selectedIndex + delta, 0, RowCount - 1));

    protected void MoveSelectionTo(int index)
    {
        SetSelectedIndex(Math.Clamp(index, 0, RowCount - 1));
        ScrollIntoView(_selectedIndex);
    }

    public void ScrollIntoView(int index)
    {
        if (index < 0)
        {
            return;
        }

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

        ScrollChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Focus follows a click, so keyboard navigation lands in the right pane.</summary>
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
    }

    // Subscribed rather than overridden: the focus event argument types have
    // moved between Avalonia versions, and the events have not.
    private void OnFocusChanged(object? sender, RoutedEventArgs e) => InvalidateVisual();
}
