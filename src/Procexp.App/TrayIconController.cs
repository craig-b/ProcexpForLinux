using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;

namespace Procexp.App;

/// <summary>
/// The tray icon: a live CPU history graph in the classic Process Explorer
/// style, with a menu to raise the window or quit.
/// </summary>
/// <remarks>
/// The icon is a tiny bitmap redrawn from the same per-sweep stats that feed
/// the toolbar sparklines — green columns on black, the exact idiom the
/// Windows tray icon has used for twenty years, which is what makes it
/// recognisable at 24 pixels.
///
/// On Linux the tray itself is not a given: Avalonia speaks the
/// StatusNotifierItem D-Bus protocol, which KDE and most bars implement but
/// stock GNOME needs an extension for. When no host is listening the icon
/// simply never appears; nothing here can detect that, so nothing tries.
/// </remarks>
public sealed class TrayIconController : IDisposable
{
    private const int Size = 24;

    /// <summary>One sample per drawn column, so the graph spans Size sweeps.</summary>
    private readonly double[] _history = new double[Size];

    private readonly TrayIcon _icon;
    private readonly int[] _pixels = new int[Size * Size];

    /// <summary>
    /// One bitmap reused for every redraw. WindowIcon converts to the platform
    /// representation on construction, but nothing documents that it must, so
    /// the backing bitmap outlives every icon handed out rather than being
    /// disposed while one might still reference it.
    /// </summary>
    private readonly Avalonia.Media.Imaging.WriteableBitmap _bitmap = new(
        new PixelSize(Size, Size),
        new Vector(96, 96),
        PixelFormat.Bgra8888,
        AlphaFormat.Premul
    );

    public TrayIconController(Window owner, Action showSystemInfo)
    {
        var show = new NativeMenuItem("Show Process Explorer");
        show.Click += (_, _) => Restore(owner);

        var systemInfo = new NativeMenuItem("System Information...");
        systemInfo.Click += (_, _) =>
        {
            Restore(owner);
            showSystemInfo();
        };

        var exit = new NativeMenuItem("Exit");
        exit.Click += (_, _) => owner.Close();

        _icon = new TrayIcon
        {
            ToolTipText = "Process Explorer",
            Menu = new NativeMenu
            {
                Items = { show, systemInfo, new NativeMenuItemSeparator(), exit },
            },
        };

        _icon.Clicked += (_, _) => Restore(owner);

        if (Application.Current is { } app)
        {
            TrayIcon.SetIcons(app, [_icon]);
        }

        Redraw();
    }

    public bool IsVisible
    {
        get => _icon.IsVisible;
        set => _icon.IsVisible = value;
    }

    /// <summary>Append this sweep's readings and redraw the icon.</summary>
    public void Update(double cpuPercent, double memoryPercent)
    {
        Array.Copy(_history, 1, _history, 0, _history.Length - 1);
        _history[^1] = Math.Clamp(cpuPercent, 0, 100);

        _icon.ToolTipText = $"CPU {cpuPercent:F0}%  Memory {memoryPercent:F0}%";
        Redraw();
    }

    /// <summary>
    /// A minimised window on Linux cannot always be raised by Show alone;
    /// resetting WindowState first covers the common tiling and stacking cases.
    /// </summary>
    private static void Restore(Window owner)
    {
        owner.WindowState = WindowState.Normal;
        owner.Show();
        owner.Activate();
    }

    private void Redraw()
    {
        const int Background = unchecked((int)0xFF000000);
        const int Border = unchecked((int)0xFF606060);
        const int Column = unchecked((int)0xFF00C020);

        Array.Fill(_pixels, Background);

        for (var x = 0; x < Size; x++)
        {
            _pixels[x] = Border;
            _pixels[(Size - 1) * Size + x] = Border;
            _pixels[x * Size] = Border;
            _pixels[x * Size + Size - 1] = Border;
        }

        for (var x = 1; x < Size - 1; x++)
        {
            var height = (int)Math.Round(_history[x] / 100.0 * (Size - 2));
            for (var y = 0; y < height; y++)
            {
                _pixels[(Size - 2 - y) * Size + x] = Column;
            }
        }

        using (var framebuffer = _bitmap.Lock())
        {
            for (var row = 0; row < Size; row++)
            {
                Marshal.Copy(
                    _pixels,
                    row * Size,
                    framebuffer.Address + row * framebuffer.RowBytes,
                    Size
                );
            }
        }

        _icon.Icon = new WindowIcon(_bitmap);
    }

    public void Dispose()
    {
        _icon.Dispose();
        _bitmap.Dispose();
    }
}
