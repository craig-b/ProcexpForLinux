using Avalonia.Media;
using Procexp.Model;

namespace Procexp.App.Controls;

/// <summary>
/// The brushes and pens every table uses, frozen per theme.
/// </summary>
/// <remarks>
/// Held rather than constructed on demand. A table paint reads these several
/// hundred times a frame, and allocating a brush per read measurably showed up
/// in the process list's paint time.
/// </remarks>
public sealed class TablePalette
{
    private static readonly TablePalette LightPalette = new(false);
    private static readonly TablePalette DarkPalette = new(true);

    private readonly Dictionary<Rgba, IBrush> _rowBrushes = [];

    private TablePalette(bool dark)
    {
        IsDark = dark;

        Background = dark ? Rgb(30, 30, 30) : Brushes.White;
        HeaderBackground = dark ? Rgb(45, 45, 45) : Rgb(240, 240, 240);
        Text = dark ? Rgb(230, 230, 230) : Brushes.Black;
        HeaderText = dark ? Rgb(210, 210, 210) : Rgb(40, 40, 40);
        AlternateRow = dark ? Rgb(36, 36, 36) : Rgb(248, 248, 248);
        Expander = dark ? Rgb(180, 180, 180) : Rgb(80, 80, 80);
        Divider = new Pen(dark ? Rgb(60, 60, 60) : Rgb(210, 210, 210), 1);

        // Selection stays the same in both themes: it must read as selected
        // regardless of the row tint underneath it.
        Selection = Rgb(0, 92, 168);
        SelectedText = Brushes.White;

        // A selected row that is not focused should still be distinguishable
        // from an unselected one, without competing with the focused pane.
        InactiveSelection = dark ? Rgb(70, 70, 70) : Rgb(205, 205, 205);
    }

    public static TablePalette For(bool dark) => dark ? DarkPalette : LightPalette;

    public bool IsDark { get; }

    public IBrush Background { get; }
    public IBrush HeaderBackground { get; }
    public IBrush Text { get; }
    public IBrush HeaderText { get; }
    public IBrush AlternateRow { get; }
    public IBrush Expander { get; }
    public IBrush Selection { get; }
    public IBrush InactiveSelection { get; }
    public IBrush SelectedText { get; }
    public IPen Divider { get; }

    /// <summary>Brush for a row-colouring rule, cached by colour.</summary>
    public IBrush RowBrush(Rgba colour)
    {
        if (_rowBrushes.TryGetValue(colour, out var brush))
        {
            return brush;
        }

        brush = new SolidColorBrush(Color.FromArgb(
            (byte)(colour.A * 255), (byte)(colour.R * 255), (byte)(colour.G * 255), (byte)(colour.B * 255)));

        _rowBrushes[colour] = brush;
        return brush;
    }

    private static IBrush Rgb(byte r, byte g, byte b) => new SolidColorBrush(Color.FromRgb(r, g, b));
}
