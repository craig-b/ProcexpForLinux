using System.Diagnostics;
using Avalonia.Threading;
using Procexp.Model;
using Procexp.Sampling;

namespace Procexp.App;

/// <summary>
/// Owns the sampling cadence: the sweep loop, the highlight ticker, pause and
/// interval, and the rolling sweep-time average the status bar reports. What
/// happens to a snapshot once it arrives is the window's business — the two
/// callbacks run on the UI thread and the controller never touches a control.
/// </summary>
public sealed class SweepController(
    IProcessDataProvider sampler,
    Action<ProcessSnapshot> apply,
    Action tick,
    CancellationToken lifetime
)
{
    /// <summary>Pausing stops the loop from sampling; the loop itself keeps running.</summary>
    public bool Paused { get; set; }

    public double IntervalSeconds { get; set; } = 1.0;

    public double AverageSweepMilliseconds => _sweepTimes.Average;

    private readonly RollingAverage _sweepTimes = new();

    public void Start()
    {
        _ = RunSamplingLoopAsync();
        _ = RunHighlightTickerAsync();
    }

    /// <summary>
    /// Take one sample immediately. While paused this samples without resuming,
    /// which is how Process Explorer's Update Now behaves.
    /// </summary>
    public Task RefreshNowAsync() => SampleOnceAsync();

    private async Task RunSamplingLoopAsync()
    {
        while (!lifetime.IsCancellationRequested)
        {
            if (!Paused)
            {
                await SampleOnceAsync().ConfigureAwait(false);
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(IntervalSeconds), lifetime)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task SampleOnceAsync()
    {
        var watch = Stopwatch.StartNew();
        ProcessSnapshot snapshot;

        try
        {
            snapshot = await sampler.SnapshotAsync(lifetime).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        var sweep = watch.Elapsed.TotalMilliseconds;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            // Recorded here rather than on the sampling thread so the average is
            // only ever touched from the UI thread that reads it.
            _sweepTimes.Record(sweep);
            apply(snapshot);
        });
    }

    /// <summary>
    /// Fades highlights out between sweeps.
    /// </summary>
    /// <remarks>
    /// Runs faster than the sampling interval so a one-second tint does not
    /// linger for a whole slow refresh cycle.
    /// </remarks>
    private async Task RunHighlightTickerAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));

        try
        {
            while (await timer.WaitForNextTickAsync(lifetime).ConfigureAwait(false))
            {
                await Dispatcher.UIThread.InvokeAsync(tick);
            }
        }
        catch (OperationCanceledException)
        {
            // Window closed.
        }
    }
}
