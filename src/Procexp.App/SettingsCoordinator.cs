using Avalonia.Threading;
using Procexp.App.Settings;

namespace Procexp.App;

/// <summary>
/// Owns settings persistence: the values loaded at startup, and the debounced
/// save. What goes into a save is the window's knowledge, supplied as a gather
/// function over the loaded settings so unknown fields survive round-tripping.
/// </summary>
public sealed class SettingsCoordinator(Func<AppSettings, AppSettings> gather)
{
    /// <summary>The settings as they were at startup. Live state stays in the UI.</summary>
    public AppSettings Loaded { get; } = SettingsStore.Load();

    private CancellationTokenSource? _debounce;

    /// <summary>
    /// Queue a save, coalescing bursts.
    /// </summary>
    /// <remarks>
    /// Saving only on window close loses everything if the app is killed or
    /// crashes — and a process explorer is a tool people SIGTERM. Debounced so
    /// that dragging a splitter does not write the file on every frame.
    /// </remarks>
    public void ScheduleSave()
    {
        _debounce?.Cancel();
        _debounce = new CancellationTokenSource();
        var token = _debounce.Token;

        _ = Task.Run(
            async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), token).ConfigureAwait(false);
                    await Dispatcher.UIThread.InvokeAsync(SaveNow);
                }
                catch (OperationCanceledException)
                {
                    // Superseded by a later change.
                }
            },
            CancellationToken.None
        );
    }

    public void SaveNow() => SettingsStore.Save(gather(Loaded));
}
