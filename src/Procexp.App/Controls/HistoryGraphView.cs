using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Procexp.App.Controls;

/// <summary>One plotted series.</summary>
public sealed class GraphSeries(Color colour, bool filled = true)
{
    public Color Colour { get; } = colour;
    public bool Filled { get; } = filled;
    public List<double> Values { get; } = [];
}

/// <summary>
/// A scrolling history graph, in the Process Explorer style: newest sample at
/// the right, a fixed grid behind, and stacked or overlaid series.
/// </summary>
/// <remarks>
/// Drawn directly rather than pulled from a charting library. The requirements
/// are narrow and unusual — a fixed-capacity ring rendered right-to-left, several
/// of them updating once a second, with no interaction — and a general charting
/// package would bring layout, animation and hit-testing machinery that all has
/// to be switched off.
/// </remarks>
public sealed class HistoryGraphView : Control
{
    private const double GridSpacing = 20;

    private readonly List<GraphSeries> _series = [];

    public HistoryGraphView()
    {
        ClipToBounds = true;

        // Hovering reads a sample out of the history rather than only the
        // newest one, which is what makes a spike two seconds ago legible.
        PointerMoved += (_, e) =>
        {
            _hoverX = e.GetPosition(this).X;
            InvalidateVisual();
        };

        PointerExited += (_, _) =>
        {
            _hoverX = null;
            InvalidateVisual();
        };
    }

    private double? _hoverX;

    /// <summary>
    /// Extra text for the hovered sample — on the system graphs, what was
    /// using the resource at that moment. Given the sample's index from the
    /// right, where zero is the newest.
    /// </summary>
    public Func<int, string?>? DescribeSample { get; set; }

    /// <summary>Seconds per sample, so a hover can say how long ago it was.</summary>
    public double SecondsPerSample { get; set; } = 1;

    public bool IsDarkMode { get; set; }

    /// <summary>Number of samples kept, which sets the visible time span.</summary>
    public int Capacity { get; set; } = 120;

    /// <summary>
    /// Fixed upper bound for the vertical axis, or null to scale to the data.
    /// </summary>
    /// <remarks>
    /// Percentages set this to 100 so the height of a spike means the same thing
    /// from one moment to the next. Byte rates leave it null and autoscale, since
    /// there is no meaningful ceiling — but then the axis label has to say what
    /// the top of the graph currently represents.
    /// </remarks>
    public double? FixedMaximum { get; set; }

    /// <summary>Formats the current value for the corner label.</summary>
    public Func<double, string> FormatValue { get; set; } =
        v => v.ToString("F1", CultureInfo.InvariantCulture);

    public string Title { get; set; } = "";

    public IReadOnlyList<GraphSeries> Series => _series;

    public GraphSeries AddSeries(Color colour, bool filled = true)
    {
        var series = new GraphSeries(colour, filled);
        _series.Add(series);
        return series;
    }

    /// <summary>Append one sample to each series, in the order they were added.</summary>
    public void Append(params double[] values)
    {
        for (var i = 0; i < values.Length && i < _series.Count; i++)
        {
            var samples = _series[i].Values;
            samples.Add(values[i]);

            while (samples.Count > Capacity)
            {
                samples.RemoveAt(0);
            }
        }

        InvalidateVisual();
    }

    public void Clear()
    {
        foreach (var series in _series)
        {
            series.Values.Clear();
        }

        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        if (bounds.Width <= 2 || bounds.Height <= 2)
        {
            return;
        }

        context.FillRectangle(Background, bounds);
        DrawGrid(context, bounds);

        var maximum = ResolveMaximum();

        foreach (var series in _series)
        {
            DrawSeries(context, bounds, series, maximum);
        }

        context.DrawRectangle(null, BorderPen, bounds);
        DrawLabels(context, bounds, maximum);
        DrawHover(context, bounds, maximum);
    }

    /// <summary>
    /// The hovered sample: a marker line, the value, how long ago it was, and
    /// whatever the owner can say about that moment.
    /// </summary>
    private void DrawHover(DrawingContext context, Rect bounds, double maximum)
    {
        if (
            _hoverX is not { } hoverX
            || _series.Count == 0
            || _series[0].Values.Count < 2
            || bounds.Width <= 2
        )
        {
            return;
        }

        var values = _series[0].Values;
        var step = bounds.Width / (Capacity - 1);
        var firstX = bounds.Width - ((values.Count - 1) * step);

        var index = (int)Math.Round((hoverX - firstX) / step);
        if (index < 0 || index >= values.Count)
        {
            return;
        }

        var x = firstX + (index * step);
        context.DrawLine(HoverPen, new Point(x, 0), new Point(x, bounds.Height));

        var fromRight = values.Count - 1 - index;
        var age = fromRight * SecondsPerSample;

        var lines = new List<string>
        {
            string.Join(
                "   ",
                _series.Select(s => FormatValue(index < s.Values.Count ? s.Values[index] : 0))
            ),
            age < 1 ? "now" : $"{age:F0}s ago",
        };

        if (DescribeSample?.Invoke(fromRight) is { Length: > 0 } detail)
        {
            lines.Add(detail);
        }

        var text = new FormattedText(
            string.Join("\n", lines),
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface(FontFamily.Default),
            11,
            LabelBrush
        );

        // Flipped to the other side near the right edge, so the readout is
        // never clipped by the graph it belongs to.
        var boxX = x + 8 + text.Width > bounds.Width ? x - 8 - text.Width : x + 8;
        var boxY = Math.Min(8, bounds.Height - text.Height - 4);

        context.FillRectangle(
            HoverBackground,
            new Rect(boxX - 4, boxY - 2, text.Width + 8, text.Height + 4)
        );
        context.DrawText(text, new Point(boxX, boxY));
    }

    private double ResolveMaximum()
    {
        if (FixedMaximum is { } fixedMaximum)
        {
            return fixedMaximum;
        }

        var peak = 0.0;
        foreach (var series in _series)
        {
            foreach (var value in series.Values)
            {
                peak = Math.Max(peak, value);
            }
        }

        // Never scale to zero, or an idle graph divides by nothing and a single
        // byte later fills the whole height.
        return peak <= 0 ? 1 : peak * 1.1;
    }

    private void DrawGrid(DrawingContext context, Rect bounds)
    {
        for (var y = GridSpacing; y < bounds.Height; y += GridSpacing)
        {
            context.DrawLine(GridPen, new Point(0, y), new Point(bounds.Width, y));
        }

        // The vertical grid scrolls with the data so it reads as movement rather
        // than as a static backdrop the line slides over.
        var offset =
            (_series.Count > 0 ? _series[0].Values.Count : 0) % 5 * (bounds.Width / Capacity);
        for (var x = bounds.Width - offset; x > 0; x -= GridSpacing)
        {
            context.DrawLine(GridPen, new Point(x, 0), new Point(x, bounds.Height));
        }
    }

    private void DrawSeries(DrawingContext context, Rect bounds, GraphSeries series, double maximum)
    {
        var values = series.Values;
        if (values.Count < 2)
        {
            return;
        }

        var step = bounds.Width / (Capacity - 1);

        // Right-aligned: the newest sample sits at the right edge and older ones
        // march left, so a partly-filled history grows from the right rather than
        // stretching to fit.
        var firstX = bounds.Width - ((values.Count - 1) * step);

        var geometry = new StreamGeometry();
        using (var sink = geometry.Open())
        {
            var start = new Point(firstX, ValueToY(values[0], maximum, bounds));
            sink.BeginFigure(start, series.Filled);

            for (var i = 1; i < values.Count; i++)
            {
                sink.LineTo(new Point(firstX + (i * step), ValueToY(values[i], maximum, bounds)));
            }

            if (series.Filled)
            {
                sink.LineTo(new Point(bounds.Width, bounds.Height));
                sink.LineTo(new Point(firstX, bounds.Height));
            }

            sink.EndFigure(series.Filled);
        }

        if (series.Filled)
        {
            context.DrawGeometry(
                new SolidColorBrush(series.Colour, 0.35),
                new Pen(new SolidColorBrush(series.Colour), 1.2),
                geometry
            );
        }
        else
        {
            context.DrawGeometry(null, new Pen(new SolidColorBrush(series.Colour), 1.4), geometry);
        }
    }

    private static double ValueToY(double value, double maximum, Rect bounds) =>
        bounds.Height - (Math.Clamp(value / maximum, 0, 1) * bounds.Height);

    private void DrawLabels(DrawingContext context, Rect bounds, double maximum)
    {
        if (Title.Length > 0)
        {
            Draw(Title, new Point(4, 2), LabelBrush);
        }

        // The scale label matters most when autoscaling: without it a spike has
        // no magnitude, only a shape.
        var scale = FormatValue(maximum);
        var scaleText = Format(scale, ScaleBrush);
        Draw(scale, new Point(bounds.Width - scaleText.Width - 4, 2), ScaleBrush);

        var latest =
            _series.Count > 0 && _series[0].Values.Count > 0
                ? _series[0].Values[^1]
                : (double?)null;

        if (latest is { } current)
        {
            var text = FormatValue(current);
            var formatted = Format(text, LabelBrush);
            Draw(
                text,
                new Point(bounds.Width - formatted.Width - 4, bounds.Height - formatted.Height - 2),
                LabelBrush
            );
        }

        void Draw(string text, Point origin, IBrush brush) =>
            context.DrawText(Format(text, brush), origin);
    }

    private FormattedText Format(string text, IBrush brush) =>
        new(
            text,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface(FontFamily.Default),
            10,
            brush
        );

    private IBrush Background =>
        IsDarkMode
            ? new SolidColorBrush(Color.FromRgb(20, 20, 20))
            : new SolidColorBrush(Color.FromRgb(12, 12, 12));

    private IPen GridPen => new Pen(new SolidColorBrush(Color.FromRgb(40, 70, 40)), 1);

    private IPen BorderPen => new Pen(new SolidColorBrush(Color.FromRgb(80, 80, 80)), 1);

    private static IBrush LabelBrush => new SolidColorBrush(Color.FromRgb(220, 220, 220));

    private static IPen HoverPen => new Pen(new SolidColorBrush(Color.FromRgb(200, 200, 200)), 1);

    // Opaque enough to keep the readout legible over a filled series.
    private static IBrush HoverBackground => new SolidColorBrush(Color.FromArgb(210, 20, 20, 20));

    private static IBrush ScaleBrush => new SolidColorBrush(Color.FromRgb(150, 150, 150));
}
