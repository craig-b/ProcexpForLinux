using System.Diagnostics;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Procexp.App.Controls;
using Procexp.Model;
using Procexp.Sampling;

namespace Procexp.App;

public partial class MainWindow : Window
{
    private static readonly double[] Intervals = [0.5, 1.0, 2.0, 5.0];

    private readonly ProcSampler _sampler = new();
    private readonly HashSet<ProcessId> _collapsed = [];
    private readonly CancellationTokenSource _lifetime = new();

    private ProcessTreeView _tree = null!;
    private ScrollBar _verticalScroll = null!;
    private ScrollBar _horizontalScroll = null!;
    private TextBlock _statusText = null!;
    private TextBlock _timingText = null!;
    private Button _pauseButton = null!;
    private ToggleSwitch _treeToggle = null!;
    private ComboBox _intervalCombo = null!;

    private ProcessSnapshot _snapshot = ProcessSnapshot.Empty;
    private bool _paused;

    // Rolling measurements, which are the entire point of this prototype.
    private readonly Queue<double> _flattenTimes = new();
    private readonly Queue<double> _sweepTimes = new();

    private IReadOnlyList<(Column Column, double Width)> _columns = [];

    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);

        _tree = this.FindControl<ProcessTreeView>("Tree")!;
        _verticalScroll = this.FindControl<ScrollBar>("VerticalScroll")!;
        _horizontalScroll = this.FindControl<ScrollBar>("HorizontalScroll")!;
        _statusText = this.FindControl<TextBlock>("StatusText")!;
        _timingText = this.FindControl<TextBlock>("TimingText")!;
        _pauseButton = this.FindControl<Button>("PauseButton")!;
        _treeToggle = this.FindControl<ToggleSwitch>("TreeToggle")!;
        _intervalCombo = this.FindControl<ComboBox>("IntervalCombo")!;

        _intervalCombo.ItemsSource = Intervals
            .Select(i => string.Create(CultureInfo.InvariantCulture, $"{i:0.#} s"))
            .ToList();

        // A deliberately wide default set, so the horizontal scrolling this
        // prototype is testing has something to scroll.
        _columns =
        [
            (Column.Name, _tree.NamePaneWidth),
            .. new[]
            {
                Column.Pid, Column.Cpu, Column.PrivateBytes, Column.WorkingSet,
                Column.VirtualSize, Column.Threads, Column.Handles, Column.State,
                Column.User, Column.Nice, Column.MinorFaults, Column.MajorFaults,
                Column.StartTime, Column.SystemdUnit, Column.Description, Column.Path,
            }.Select(c => (c, Columns.DefaultWidth(c))),
        ];

        _tree.IsDarkMode = ActualThemeVariant == ThemeVariant.Dark;

        // Keep the horizontal scrollbar aligned with the metric columns it
        // actually scrolls, rather than letting it run under the frozen pane.
        this.FindControl<Border>("ScrollGutter")!.Width = _tree.NamePaneWidth;

        _tree.SelectionChanged += (_, process) => UpdateStatus(process);
        _tree.ToggleRequested += (_, id) =>
        {
            if (!_collapsed.Remove(id))
            {
                _collapsed.Add(id);
            }

            Rebuild();
        };

        _tree.HeaderClicked += (_, column) =>
        {
            if (_tree.SortColumn == column)
            {
                _tree.SortDescending = !_tree.SortDescending;
            }
            else
            {
                _tree.SortColumn = column;
                _tree.SortDescending = column != Column.Name;
            }

            Rebuild();
        };

        _verticalScroll.Scroll += (_, _) => _tree.VerticalOffset = _verticalScroll.Value;
        _horizontalScroll.Scroll += (_, _) => _tree.HorizontalOffset = _horizontalScroll.Value;

        _pauseButton.Click += (_, _) =>
        {
            _paused = !_paused;
            _pauseButton.Content = _paused ? "Resume" : "Pause";
        };

        _treeToggle.IsCheckedChanged += (_, _) => Rebuild();
        _intervalCombo.SelectionChanged += (_, _) => { /* picked up by the sampling loop */ };

        Opened += (_, _) => _ = RunSamplingLoopAsync();
        Closed += (_, _) => _lifetime.Cancel();
    }

    private async Task RunSamplingLoopAsync()
    {
        while (!_lifetime.IsCancellationRequested)
        {
            if (!_paused)
            {
                var watch = Stopwatch.StartNew();
                var snapshot = await _sampler.SnapshotAsync(_lifetime.Token).ConfigureAwait(false);
                var sweep = watch.Elapsed.TotalMilliseconds;

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    Record(_sweepTimes, sweep);
                    _snapshot = snapshot;
                    Rebuild();
                });
            }

            var seconds = Intervals[Math.Clamp(_intervalCombo.SelectedIndex, 0, Intervals.Length - 1)];

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(seconds), _lifetime.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void Rebuild()
    {
        var watch = Stopwatch.StartNew();

        var rows = RowFlattener.Flatten(
            _snapshot,
            _collapsed,
            _tree.SortColumn,
            _tree.SortDescending,
            treeMode: _treeToggle.IsChecked == true);

        _tree.SetRows(rows, _columns);
        Record(_flattenTimes, watch.Elapsed.TotalMilliseconds);

        SyncScrollBars();
        UpdateStatus(_tree.SelectedProcess);
    }

    private void SyncScrollBars()
    {
        _verticalScroll.Minimum = 0;
        _verticalScroll.Maximum = Math.Max(0, _tree.ExtentHeight - _tree.ViewportHeight);
        _verticalScroll.ViewportSize = _tree.ViewportHeight;
        _verticalScroll.Value = _tree.VerticalOffset;

        _horizontalScroll.Minimum = 0;
        _horizontalScroll.Maximum = Math.Max(0, _tree.MetricsExtentWidth - _tree.MetricsViewportWidth);
        _horizontalScroll.ViewportSize = _tree.MetricsViewportWidth;
        _horizontalScroll.Value = _tree.HorizontalOffset;
    }

    private void UpdateStatus(ProcessRecord? selected)
    {
        var processes = _snapshot.Processes.Count;
        var threads = _snapshot.System.ThreadCount;

        _statusText.Text = selected is null
            ? $"{processes} processes, {threads} threads"
            : $"{processes} processes, {threads} threads    —    " +
              $"{selected.Name} (pid {selected.Id.Pid}), {selected.ThreadCount} threads, " +
              $"{ValueFormat.Bytes(selected.ResidentSize)}";

        var timing = string.Create(
            CultureInfo.InvariantCulture,
            $"sweep {Average(_sweepTimes),6:F1} ms   layout {Average(_flattenTimes),5:F2} ms   " +
            $"paint {_tree.AverageRenderMilliseconds,5:F2} ms   " +
            $"{_tree.LastRenderedRowCount}/{processes} rows drawn");

        _timingText.Text = timing;

        // Also to stdout, so the prototype's numbers can be captured without
        // having to read them off the screen.
        Console.WriteLine(timing);
    }

    /// <summary>Keep a short rolling window so the readout is stable but current.</summary>
    private static void Record(Queue<double> samples, double value)
    {
        samples.Enqueue(value);
        while (samples.Count > 10)
        {
            samples.Dequeue();
        }
    }

    private static double Average(Queue<double> samples) => samples.Count == 0 ? 0 : samples.Average();
}
