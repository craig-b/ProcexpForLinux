using System.Diagnostics;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Procexp.App.Controls;
using Procexp.App.Dialogs;
using Procexp.App.Settings;
using Procexp.Gpu;
using Procexp.Metrics;
using Procexp.Model;
using Procexp.Privileged;
using Procexp.Sampling;

namespace Procexp.App;

public partial class MainWindow : Window
{
    /// <summary>
    /// What both the sweep and the detail views read through. Falls back to the
    /// privileged helper for the ptrace-gated reads when one is installed, so
    /// the UI never has to know whether a given process needed it — and the
    /// sweep's I/O columns fill in for other users' processes too, from the
    /// provider's helper-fed cache.
    /// </summary>
    private readonly IProcessDataProvider _sampler = PrivilegedClient.IsAvailable
        ? new HelperBackedProvider(new ProcSampler(), new PrivilegedClient())
        : new ProcSampler();
    private readonly ProcessListModel _list = new();
    private readonly HashSet<ProcessId> _collapsed = [];
    private readonly CancellationTokenSource _lifetime = new();

    private readonly SelectionActions _actions = null!;
    private readonly ProcessTreeView _tree = null!;
    private readonly ScrollBar _verticalScroll = null!;
    private readonly ScrollBar _horizontalScroll = null!;
    private readonly TextBlock _statusText = null!;
    private readonly TextBlock _timingText = null!;
    private readonly LowerPaneView _lowerPane = null!;
    private SystemInfoWindow? _systemInfo;

    private readonly SystemStatsProvider _systemStats = new();
    private readonly SystemHistory _systemHistory = new();
    private bool _statsPrimed;
    private readonly GpuProvider _gpu = new();

    /// <summary>
    /// Toolbar sparklines, fed from the sweep's system stats. Small enough to
    /// be a glance rather than a reading, which is what the click-through to
    /// System Information is for.
    /// </summary>
    private readonly HistoryGraphView _cpuSpark = MakeSpark("CPU");
    private readonly HistoryGraphView _memorySpark = MakeSpark("Memory");
    private readonly HistoryGraphView _ioSpark = MakeSpark("I/O");
    private readonly TextBlock _cpuValue = MakeSparkValue(30);
    private readonly TextBlock _memoryValue = MakeSparkValue(30);
    private readonly TextBlock _ioValue = MakeSparkValue(56);

    private static HistoryGraphView MakeSpark(string title) =>
        new()
        {
            Title = title,
            Width = 90,
            Height = 28,
            Capacity = 60,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };

    /// <summary>
    /// The live value shown beside each sparkline. Min-width keeps the toolbar
    /// from breathing as digits come and go.
    /// </summary>
    private static TextBlock MakeSparkValue(double minWidth) =>
        new()
        {
            FontSize = 11,
            MinWidth = minWidth,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };

    /// <summary>
    /// Fills the columns the sweep is too fast to gather: description, company,
    /// version, provenance, autostart location and per-process GPU.
    /// </summary>
    private readonly ProcessEnricher _enricher = new();

    private readonly SweepController _sweep = null!;
    private string _filter = "";
    private bool _confirmActions = true;
    private IReadOnlyList<ProcessColorRule> _colorRules = ProcessColorRule.Defaults;

    private readonly SettingsCoordinator _settings = null!;
    private bool _enrichmentDirty;

    private readonly RollingAverage _layoutTimes = new();

    private readonly ColumnCoordinator _columns = null!;

    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);

        _settings = new SettingsCoordinator(GatherSettings);
        _sweep = new SweepController(_sampler, ApplySnapshot, OnHighlightTick, _lifetime.Token);
        _actions = new SelectionActions(
            this,
            () => _confirmActions,
            _list,
            () => _tree.SelectedProcess,
            _sweep.RefreshNowAsync
        );
        _tree = Get<ProcessTreeView>("Tree");
        _verticalScroll = Get<ScrollBar>("VerticalScroll");
        _horizontalScroll = Get<ScrollBar>("HorizontalScroll");
        _statusText = Get<TextBlock>("StatusText");
        _timingText = Get<TextBlock>("TimingText");

        Get<Border>("ScrollGutter").Width = _tree.NamePaneWidth;

        _columns = new ColumnCoordinator(
            this,
            _tree,
            _settings.Loaded.ColumnWidths,
            Rebuild,
            _settings.ScheduleSave,
            _settings.SaveNow
        );
        _columns.Apply(_settings.Loaded.Columns);

        _tree.IsDarkMode = ActualThemeVariant == ThemeVariant.Dark;

        _lowerPane = new LowerPaneView(_sampler) { IsDarkMode = _tree.IsDarkMode };
        _lowerPane.ShowDetail += (module, descriptor) =>
        {
            var window =
                module is not null ? DetailWindow.ForModule(module)
                : descriptor is not null ? DetailWindow.ForDescriptor(descriptor)
                : null;
            window?.Show(this);
        };
        Get<ContentControl>("LowerPaneHost").Content = _lowerPane;

        ApplySettings();

        WireTree();
        WireLowerPane();
        WireToolbar();
        WireMenus();
        WireContextMenu();

        // Window geometry changes constantly while dragging, so it rides the same
        // debounce as everything else.
        SizeChanged += (_, _) => _settings.ScheduleSave();

        // Enrichment arrives out of band, so the list has to be told. Marshalled
        // and coalesced: a first sweep queues several hundred image lookups, and
        // rebuilding per completion would be hundreds of rebuilds.
        _enricher.Updated += (_, _) =>
            Dispatcher.UIThread.Post(() => _enrichmentDirty = true, DispatcherPriority.Background);

        Opened += (_, _) =>
        {
            SetLowerPaneVisible(_settings.Loaded.ShowLowerPane);
            _lowerPane.Mode = _settings.Loaded.LowerPaneMode;
            Get<ComboBox>("PaneModeCombo").SelectedIndex = (int)_settings.Loaded.LowerPaneMode;

            _sweep.Start();
        };

        Closed += (_, _) =>
        {
            _lifetime.Cancel();
            _enricher.Dispose();
            _settings.SaveNow();
        };
    }

    private T Get<T>(string name)
        where T : Control => this.FindControl<T>(name)!;

    private void ApplySettings()
    {
        _tree.SortColumn = _settings.Loaded.SortColumn;
        _tree.SortDescending = _settings.Loaded.SortDescending;
        _tree.NamePaneWidth = _settings.Loaded.NamePaneWidth;
        _list.HighlightNewAndDead = _settings.Loaded.HighlightNewAndDead;
        _sweep.IntervalSeconds = _settings.Loaded.RefreshSeconds;
        _confirmActions = _settings.Loaded.ConfirmActions;
        _columns.SetColumnSets(_settings.Loaded.ColumnSets);
        ApplyOptions(
            _settings.Loaded.ConfirmActions,
            _settings.Loaded.HighlightSeconds,
            ColorRuleSetting.ToRules(_settings.Loaded.ColorRules)
        );

        Width = _settings.Loaded.WindowWidth;
        Height = _settings.Loaded.WindowHeight;
        Topmost = _settings.Loaded.AlwaysOnTop;

        Get<ToggleSwitch>("TreeToggle").IsChecked = _settings.Loaded.TreeMode;
        Get<MenuItem>("MenuTreeMode").IsChecked = _settings.Loaded.TreeMode;
        Get<MenuItem>("MenuHighlight").IsChecked = _settings.Loaded.HighlightNewAndDead;
        Get<MenuItem>("MenuAlwaysOnTop").IsChecked = _settings.Loaded.AlwaysOnTop;
        Get<Border>("ScrollGutter").Width = _tree.NamePaneWidth;

        // The speed radio group has to agree with the interval that was restored,
        // or the menu claims one rate while the loop runs at another.
        Get<MenuItem>("MenuSpeedFast").IsChecked = _settings.Loaded.RefreshSeconds <= 0.5;
        Get<MenuItem>("MenuSpeedNormal").IsChecked =
            Math.Abs(_settings.Loaded.RefreshSeconds - 1.0) < 0.01;
        Get<MenuItem>("MenuSpeedSlow").IsChecked =
            Math.Abs(_settings.Loaded.RefreshSeconds - 2.0) < 0.01;
        Get<MenuItem>("MenuSpeedVerySlow").IsChecked = _settings.Loaded.RefreshSeconds >= 5.0;
    }

    /// <summary>Everything a save records, gathered from the live controls.</summary>
    private AppSettings GatherSettings(AppSettings loaded) =>
        loaded with
        {
            Columns = [.. _columns.Columns.Select(c => c.Column)],
            ColumnWidths = _columns.Columns.ToDictionary(c => c.Column.ToString(), c => c.Width),
            SortColumn = _tree.SortColumn,
            SortDescending = _tree.SortDescending,
            TreeMode = Get<ToggleSwitch>("TreeToggle").IsChecked == true,
            ShowLowerPane = Get<ContentControl>("LowerPaneHost").IsVisible,
            LowerPaneMode = _lowerPane.Mode,
            RefreshSeconds = _sweep.IntervalSeconds,
            HighlightNewAndDead = _list.HighlightNewAndDead,
            HighlightSeconds = _list.HighlightDuration.TotalSeconds,
            ConfirmActions = _confirmActions,
            ColorRules = ColorRuleSetting.FromRules(_colorRules),
            ColumnSets = _columns.ColumnSets,
            AlwaysOnTop = Topmost,
            NamePaneWidth = _tree.NamePaneWidth,
            WindowWidth = Width,
            WindowHeight = Height,
        };

    // ---- Wiring -------------------------------------------------------------

    private void WireLowerPane()
    {
        var combo = Get<ComboBox>("PaneModeCombo");
        combo.ItemsSource = new[] { "Mapped Files", "Handles", "Threads" };

        combo.SelectionChanged += (_, _) =>
        {
            var mode = (LowerPaneMode)Math.Clamp(combo.SelectedIndex, 0, 2);
            _lowerPane.Mode = mode;

            Get<MenuItem>("MenuPaneModules").IsChecked = mode == LowerPaneMode.Modules;
            Get<MenuItem>("MenuPaneHandles").IsChecked = mode == LowerPaneMode.Handles;
            Get<MenuItem>("MenuPaneThreads").IsChecked = mode == LowerPaneMode.Threads;
            _settings.ScheduleSave();
        };

        WirePaneMode("MenuPaneModules", LowerPaneMode.Modules);
        WirePaneMode("MenuPaneHandles", LowerPaneMode.Handles);
        WirePaneMode("MenuPaneThreads", LowerPaneMode.Threads);

        var toggle = Get<ToggleButton>("LowerPaneToggle");
        toggle.IsCheckedChanged += (_, _) => SetLowerPaneVisible(toggle.IsChecked == true);

        var menuToggle = Get<MenuItem>("MenuLowerPane");
        menuToggle.Click += (_, _) => SetLowerPaneVisible(menuToggle.IsChecked);

        void WirePaneMode(string name, LowerPaneMode mode) =>
            Get<MenuItem>(name).Click += (_, _) => combo.SelectedIndex = (int)mode;
    }

    /// <summary>
    /// Show or hide the lower pane.
    /// </summary>
    /// <remarks>
    /// The splitter and the pane's row collapse together. Hiding only the pane
    /// would leave a draggable divider with nothing beneath it.
    /// </remarks>
    private void SetLowerPaneVisible(bool visible)
    {
        var split = Get<Grid>("PaneSplit");

        split.RowDefinitions[1].Height = visible ? GridLength.Auto : new GridLength(0);
        split.RowDefinitions[2].Height = visible
            ? new GridLength(2, GridUnitType.Star)
            : new GridLength(0);

        Get<GridSplitter>("PaneSplitter").IsVisible = visible;
        Get<ContentControl>("LowerPaneHost").IsVisible = visible;
        Get<ToggleButton>("LowerPaneToggle").IsChecked = visible;
        Get<MenuItem>("MenuLowerPane").IsChecked = visible;
        _settings.ScheduleSave();

        if (visible)
        {
            _lowerPane.SetProcess(_tree.SelectedProcess?.Id);
        }
    }

    private void WireTree()
    {
        _tree.RowActivated += (_, _) => ShowProperties();

        _tree.SelectionChanged += (_, _) =>
        {
            UpdateStatus();

            // The lower pane follows the selection, and does nothing at all when
            // it is hidden — enumerating a process's descriptors costs more than
            // the whole sweep, so it must not happen invisibly.
            if (Get<ContentControl>("LowerPaneHost").IsVisible)
            {
                _lowerPane.SetProcess(_tree.SelectedProcess?.Id);
            }
        };

        // Resize and reorder rewrite the layout the window owns, so it takes
        // the control's version back and persists it.
        _tree.ColumnsChanged += (_, columns) =>
        {
            _columns.AcceptLayout(columns);
            Get<Border>("ScrollGutter").Width = _tree.NamePaneWidth;
            SyncScrollBars();
            _settings.ScheduleSave();
        };

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

                // Text sorts ascending and numbers descending by default, which is
                // what puts the busiest process at the top on the first click.
                _tree.SortDescending = Columns.IsRightAligned(column);
            }

            Rebuild();
            _settings.ScheduleSave();
        };

        _verticalScroll.Scroll += (_, _) => _tree.VerticalOffset = _verticalScroll.Value;
        _horizontalScroll.Scroll += (_, _) => _tree.HorizontalOffset = _horizontalScroll.Value;
    }

    private void WireToolbar()
    {
        Get<Button>("PauseButton").Click += (_, _) => TogglePause();
        Get<Button>("RefreshButton").Click += (_, _) => _ = _sweep.RefreshNowAsync();
        Get<Button>("KillButton").Click += (_, _) => _ = _actions.KillAsync(tree: false);
        Get<Button>("SuspendButton").Click += (_, _) => _ = _actions.SuspendAsync();

        var treeToggle = Get<ToggleSwitch>("TreeToggle");
        treeToggle.IsCheckedChanged += (_, _) =>
        {
            Get<MenuItem>("MenuTreeMode").IsChecked = treeToggle.IsChecked == true;
            Rebuild();
            _settings.ScheduleSave();
        };

        BuildSparklines();

        var filterBox = Get<TextBox>("FilterBox");
        filterBox.TextChanged += (_, _) =>
        {
            _filter = filterBox.Text ?? "";
            Rebuild();
        };
        filterBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                filterBox.Text = "";
                _tree.Focus();
                e.Handled = true;
            }
        };
    }

    /// <summary>
    /// Put the sparklines in the toolbar and make each one a way in to the
    /// System Information tab that explains it.
    /// </summary>
    private void BuildSparklines()
    {
        var host = Get<StackPanel>("Sparklines");
        var dark = _tree.IsDarkMode;

        foreach (
            var (spark, value, colour, tab, format) in new (
                HistoryGraphView,
                TextBlock,
                Color,
                string,
                Func<double, string>
            )[]
            {
                (_cpuSpark, _cpuValue, Color.FromRgb(90, 200, 90), "CPU", v => $"{v:F0}% CPU"),
                (
                    _memorySpark,
                    _memoryValue,
                    Color.FromRgb(200, 150, 60),
                    "Memory",
                    v => $"{v:F0}% memory"
                ),
                (
                    _ioSpark,
                    _ioValue,
                    Color.FromRgb(200, 90, 200),
                    "I/O",
                    v => $"{ValueFormat.Bytes((ulong)Math.Max(0, v))}/s"
                ),
            }
        )
        {
            spark.IsDarkMode = dark;
            spark.AddSeries(colour);
            spark.FormatValue = format;
            spark.SecondsPerSample = _sweep.IntervalSeconds;

            // The graph is too small to share pixels with text: the live value
            // sits beside it, the name lives in the tooltip, and hovering the
            // graph gives exact historical readings.
            spark.ShowInlineLabels = false;
            spark.Title = "";

            var group = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                Spacing = 4,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            };
            group.Children.Add(spark);
            group.Children.Add(value);

            ToolTip.SetTip(group, $"{tab} — click for System Information");
            group.PointerPressed += (_, _) =>
            {
                ShowSystemInfo();
                _systemInfo?.SelectTab(tab);
            };

            host.Children.Add(group);
        }
    }

    private void WireMenus()
    {
        Get<MenuItem>("MenuExit").Click += (_, _) => Close();
        Get<MenuItem>("MenuRun").Click += (_, _) => new RunDialog().ShowDialog(this);
        Get<MenuItem>("MenuSave").Click += (_, _) => _ = SaveProcessListAsync();

        var alwaysOnTop = Get<MenuItem>("MenuAlwaysOnTop");
        alwaysOnTop.Click += (_, _) =>
        {
            Topmost = alwaysOnTop.IsChecked;
            _settings.ScheduleSave();
        };

        var highlight = Get<MenuItem>("MenuHighlight");
        highlight.Click += (_, _) =>
        {
            _list.HighlightNewAndDead = highlight.IsChecked;
            _settings.ScheduleSave();
        };

        Get<MenuItem>("MenuInstallHelper").Click += (_, _) => _ = ShowHelperStatusAsync();

        var pause = Get<MenuItem>("MenuPause");
        pause.Click += (_, _) => SetPaused(pause.IsChecked);

        Get<MenuItem>("MenuRefreshNow").Click += (_, _) => _ = _sweep.RefreshNowAsync();

        var treeMode = Get<MenuItem>("MenuTreeMode");
        treeMode.Click += (_, _) =>
        {
            Get<ToggleSwitch>("TreeToggle").IsChecked = treeMode.IsChecked;
            Rebuild();
        };

        WireSpeed("MenuSpeedFast", 0.5);
        WireSpeed("MenuSpeedNormal", 1.0);
        WireSpeed("MenuSpeedSlow", 2.0);
        WireSpeed("MenuSpeedVerySlow", 5.0);

        Get<MenuItem>("MenuKill").Click += (_, _) => _ = _actions.KillAsync(tree: false);
        Get<MenuItem>("MenuKillTree").Click += (_, _) => _ = _actions.KillAsync(tree: true);
        Get<MenuItem>("MenuSuspend").Click += (_, _) => _ = _actions.SuspendAsync();
        Get<MenuItem>("MenuResume").Click += (_, _) => _ = _actions.ResumeAsync();
        Get<MenuItem>("MenuRestart").Click += (_, _) => _ = _actions.RestartAsync();

        WireNice("MenuNiceHighest", -20);
        WireNice("MenuNiceHigh", -5);
        WireNice("MenuNiceNormal", 0);
        WireNice("MenuNiceLow", 5);
        WireNice("MenuNiceLowest", 19);

        Get<MenuItem>("MenuAbout").Click += (_, _) => ShowAbout();
        Get<MenuItem>("MenuProperties").Click += (_, _) => ShowProperties();
        Get<MenuItem>("MenuSystemInfo").Click += (_, _) => ShowSystemInfo();
        Get<MenuItem>("MenuColumns").Click += (_, _) => _ = _columns.ChooseAsync();
        Get<MenuItem>("MenuSettings").Click += (_, _) => _ = ShowSettingsAsync();
        Get<MenuItem>("MenuSaveColumnSet").Click += (_, _) => _ = _columns.SaveSetAsync();
        Get<MenuItem>("MenuOrganizeColumnSets").Click += (_, _) => _ = _columns.OrganizeAsync();
        _columns.WireMenu(
            Get<MenuItem>("MenuColumnSets"),
            Get<Separator>("MenuColumnSetsSeparator")
        );
        Get<MenuItem>("MenuFind").Click += (_, _) => ShowFind();

        void WireSpeed(string name, double seconds) =>
            Get<MenuItem>(name).Click += (_, _) =>
            {
                _sweep.IntervalSeconds = seconds;
                _settings.ScheduleSave();
            };

        void WireNice(string name, int nice) =>
            Get<MenuItem>(name).Click += (_, _) => _ = _actions.SetNiceAsync(nice);
    }

    private void WireContextMenu()
    {
        Get<MenuItem>("CtxProperties").Click += (_, _) => ShowProperties();
        Get<MenuItem>("CtxKill").Click += (_, _) => _ = _actions.KillAsync(tree: false);
        Get<MenuItem>("CtxKillTree").Click += (_, _) => _ = _actions.KillAsync(tree: true);
        Get<MenuItem>("CtxSuspend").Click += (_, _) => _ = _actions.SuspendAsync();
        Get<MenuItem>("CtxResume").Click += (_, _) => _ = _actions.ResumeAsync();
        Get<MenuItem>("CtxRestart").Click += (_, _) => _ = _actions.RestartAsync();

        Get<MenuItem>("CtxCopyPath").Click += (_, _) =>
            _ = CopyAsync(_tree.SelectedProcess?.ExecutablePath);

        Get<MenuItem>("CtxCopyCommandLine").Click += (_, _) =>
            _ = CopyAsync(_tree.SelectedProcess?.CommandLine);
    }

    // ---- Keyboard -----------------------------------------------------------

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.F && e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift))
        {
            var filterBox = Get<TextBox>("FilterBox");
            filterBox.Focus();
            filterBox.SelectAll();
            e.Handled = true;
            return;
        }

        // Typing in the filter box must stay typing: a space there is a
        // character, not the pause shortcut.
        if (e.Source is TextBox)
        {
            base.OnKeyDown(e);
            return;
        }

        // Space pauses, as it does in Process Explorer. Handled here rather than
        // as a menu gesture so it works whatever has focus inside the window.
        if (e.Key == Key.Space && e.KeyModifiers == KeyModifiers.None)
        {
            TogglePause();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None)
        {
            ShowProperties();
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    // ---- Sampling -----------------------------------------------------------

    /// <summary>
    /// What a fresh snapshot does to the window. Called by the sweep controller
    /// on the UI thread.
    /// </summary>
    private void ApplySnapshot(ProcessSnapshot snapshot)
    {
        _list.Apply(_enricher.Enrich(snapshot), DateTimeOffset.Now);
        Rebuild();

        if (Get<ContentControl>("LowerPaneHost").IsVisible)
        {
            _ = _lowerPane.ReloadAsync();
        }

        _systemInfo?.SetProcessCounts(snapshot.Processes.Count, snapshot.System.ThreadCount);

        // The sampler only fills counts; the system-wide rates come from the
        // stats provider, read once per sweep and shared by the toolbar
        // sparklines, the history, and the System Information window.
        var system = _systemStats.Read();

        if (!_statsPrimed)
        {
            // The first read only primes the deltas — plotting it would draw a
            // zero that never happened.
            _statsPrimed = true;
            return;
        }

        var memoryPercent =
            system.MemoryTotal > 0 ? system.MemoryUsed * 100.0 / system.MemoryTotal : 0;

        _cpuSpark.Append(system.CpuTotalPercent);
        _memorySpark.Append(memoryPercent);
        _ioSpark.Append(system.DiskBytesPerSec);

        _cpuValue.Text = $"{system.CpuTotalPercent:F0}%";
        _memoryValue.Text = $"{memoryPercent:F0}%";

        // Bytes(0) formats as "", which would leave a dangling "/s".
        var ioRate = system.DiskBytesPerSec;
        _ioValue.Text = ioRate > 0 ? $"{ValueFormat.Bytes(ioRate)}/s" : "0 B/s";

        // Who was busiest this second, for the CPU and memory graphs' hover
        // readout. CpuPercent is Irix-style — 100 per core — so it is scaled
        // to the whole machine here to match the 0–100 graphs it annotates.
        var byCpu = snapshot.Processes.Values.OrderByDescending(p => p.CpuPercent).FirstOrDefault();
        var byMemory = snapshot
            .Processes.Values.OrderByDescending(p => p.ResidentSize)
            .FirstOrDefault();
        var topCpuPercent = byCpu is null
            ? 0
            : byCpu.CpuPercent / Math.Max(1, Environment.ProcessorCount);

        _systemHistory.Record(
            new SystemHistory.Entry(
                system,
                byCpu is not null && topCpuPercent >= 0.5
                    ? $"{byCpu.Name} — {topCpuPercent:F0}%"
                    : null,
                byMemory is not null
                    ? $"{byMemory.Name} — {ValueFormat.Bytes(byMemory.ResidentSize)}"
                    : null
            )
        );
    }

    /// <summary>
    /// The 250 ms tick between sweeps. Repaints only when a highlight actually
    /// expired or enrichment arrived.
    /// </summary>
    private void OnHighlightTick()
    {
        var now = DateTimeOffset.Now;

        if (_list.Tick(now) || _enrichmentDirty)
        {
            _enrichmentDirty = false;
            _list.Apply(_enricher.Enrich(_list.Current), now);
            Rebuild();
        }

        _lowerPane.Tick(now);
    }

    private void TogglePause() => SetPaused(!_sweep.Paused);

    private void SetPaused(bool paused)
    {
        _sweep.Paused = paused;
        Get<TextBlock>("PauseLabel").Text = paused ? "Resume" : "Pause";
        Get<PathIcon>("PauseIcon").Data = Geometry.Parse(
            paused ? "M8,5.14V19.14L19,12.14L8,5.14Z" : "M14,19H18V5H14M6,19H10V5H6V19Z"
        );
        Get<MenuItem>("MenuPause").IsChecked = paused;
        UpdateStatus();
    }

    // ---- Rendering ----------------------------------------------------------

    private void Rebuild()
    {
        var watch = Stopwatch.StartNew();

        var rows = RowFlattener.Flatten(
            _list.AsSnapshot(),
            _collapsed,
            _tree.SortColumn,
            _tree.SortDescending,
            treeMode: Get<ToggleSwitch>("TreeToggle").IsChecked == true,
            filter: _filter
        );

        _tree.SetRows(rows, _columns.Columns);
        _layoutTimes.Record(watch.Elapsed.TotalMilliseconds);

        SyncScrollBars();
        UpdateStatus();
    }

    private void SyncScrollBars()
    {
        _verticalScroll.Minimum = 0;
        _verticalScroll.Maximum = Math.Max(0, _tree.ExtentHeight - _tree.ViewportHeight);
        _verticalScroll.ViewportSize = _tree.ViewportHeight;
        _verticalScroll.Value = _tree.VerticalOffset;

        _horizontalScroll.Minimum = 0;
        _horizontalScroll.Maximum = Math.Max(
            0,
            _tree.MetricsExtentWidth - _tree.MetricsViewportWidth
        );
        _horizontalScroll.ViewportSize = _tree.MetricsViewportWidth;
        _horizontalScroll.Value = _tree.HorizontalOffset;
    }

    private void UpdateStatus()
    {
        var snapshot = _list.Current;
        var selected = _tree.SelectedProcess;

        var prefix = _sweep.Paused ? "PAUSED    —    " : "";

        _statusText.Text = selected is null
            ? $"{prefix}{snapshot.Processes.Count} processes, {snapshot.System.ThreadCount} threads"
            : $"{prefix}{snapshot.Processes.Count} processes, {snapshot.System.ThreadCount} threads    —    "
                + $"{selected.Name} (pid {selected.Id.Pid}), {selected.ThreadCount} threads, "
                + $"{ValueFormat.Bytes(selected.ResidentSize)}";

        _timingText.Text = string.Create(
            CultureInfo.InvariantCulture,
            $"sweep {_sweep.AverageSweepMilliseconds, 6:F1} ms   layout {_layoutTimes.Average, 5:F2} ms   "
                + $"paint {_tree.AverageRenderMilliseconds, 5:F2} ms   "
                + $"{_tree.LastRenderedRowCount}/{snapshot.Processes.Count} rows drawn"
        );
    }

    // ---- Actions ------------------------------------------------------------

    /// <summary>
    /// Open the Properties window for the selection.
    /// </summary>
    /// <remarks>
    /// Non-modal, and one window per process rather than one shared window, so
    /// two processes can be compared side by side — which is most of the reason
    /// to open it at all.
    /// </remarks>
    private void ShowProperties()
    {
        if (_actions.Actionable() is not { } process)
        {
            return;
        }

        var window = new ProcessPropertiesWindow(_sampler, process, _tree.IsDarkMode);
        window.Show(this);
    }

    /// <summary>
    /// Open the System Information window, or bring the existing one forward.
    /// </summary>
    /// <remarks>
    /// Single-instance, unlike Properties: it is a view over the one shared
    /// history the sweep records, so a second copy would show the same thing.
    /// </remarks>
    private void ShowSystemInfo()
    {
        if (_systemInfo is { } existing)
        {
            existing.Activate();
            return;
        }

        _systemInfo = new SystemInfoWindow(
            _systemHistory,
            _gpu,
            _tree.IsDarkMode,
            _sweep.IntervalSeconds
        );
        _systemInfo.SetProcessCounts(
            _list.Current.Processes.Count,
            _list.Current.System.ThreadCount
        );
        _systemInfo.Closed += (_, _) => _systemInfo = null;
        _systemInfo.Show(this);
    }

    private void ShowFind()
    {
        var find = new FindHandleWindow(_sampler, () => _list.Current, _tree.IsDarkMode);
        find.MatchActivated += (id, kind) =>
        {
            if (!_tree.SelectProcess(id))
            {
                return;
            }

            // Show the match where it lives: the lower pane, in the mode the
            // hit came from.
            Get<ToggleButton>("LowerPaneToggle").IsChecked = true;
            Get<ComboBox>("PaneModeCombo").SelectedIndex = (int)(
                kind == "Mapped file" ? LowerPaneMode.Modules : LowerPaneMode.Handles
            );
            Activate();
        };
        find.Show(this);
    }

    private async Task CopyAsync(string? text)
    {
        if (!string.IsNullOrEmpty(text) && Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(text).ConfigureAwait(true);
        }
    }

    // ---- Dialogs ------------------------------------------------------------

    private async Task SaveProcessListAsync()
    {
        var file = await StorageProvider
            .SaveFilePickerAsync(
                new() { Title = "Save Process List", SuggestedFileName = "processes.txt" }
            )
            .ConfigureAwait(true);

        if (file is null)
        {
            return;
        }

        try
        {
            var rows = RowFlattener.Flatten(
                _list.AsSnapshot(),
                _collapsed,
                _tree.SortColumn,
                _tree.SortDescending,
                treeMode: true
            );

            await using var stream = await file.OpenWriteAsync().ConfigureAwait(true);
            await using var writer = new StreamWriter(stream);

            var columns = _columns.Columns.Select(c => c.Column).ToList();
            foreach (var line in ProcessListWriter.Lines(columns, rows))
            {
                await writer.WriteLineAsync(line).ConfigureAwait(true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            await ConfirmationDialog
                .ShowMessageAsync(
                    this,
                    "Save Process List",
                    $"Could not write the file: {e.Message}"
                )
                .ConfigureAwait(true);
        }
    }

    private async Task ShowHelperStatusAsync()
    {
        var available = PrivilegedClient.IsAvailable;
        var failure = available
            ? await new PrivilegedClient().ProbeAsync().ConfigureAwait(true)
            : null;

        var message = (available, failure) switch
        {
            (false, _) => "The privileged helper is not installed.\n\n"
                + "Without it, I/O counters, proportional memory and environments are blank "
                + "for other users' processes, and cross-user actions fail. Everything else works.\n\n"
                + "See docs/HELPER.md to install it.",
            (true, not null) => "The helper socket exists but could not be used:\n\n"
                + $"{failure}.\n\n"
                + "See docs/HELPER.md.",
            _ => "The privileged helper is installed and responding.",
        };

        await ConfirmationDialog
            .ShowMessageAsync(this, "Privileged Helper", message)
            .ConfigureAwait(true);
    }

    private void ShowAbout() => new AboutWindow().ShowDialog(this);

    private async Task ShowSettingsAsync()
    {
        var window = new SettingsWindow(
            _confirmActions,
            _list.HighlightDuration.TotalSeconds,
            _colorRules
        );
        await window.ShowDialog(this).ConfigureAwait(true);

        if (window.Result is { } chosen)
        {
            ApplyOptions(chosen.ConfirmActions, chosen.HighlightSeconds, chosen.ColorRules);
            Rebuild();
            _settings.ScheduleSave();
        }
    }

    /// <summary>Push the option values into everything that consumes them.</summary>
    private void ApplyOptions(
        bool confirmActions,
        double highlightSeconds,
        IReadOnlyList<ProcessColorRule> rules
    )
    {
        _confirmActions = confirmActions;
        _colorRules = rules;

        var duration = TimeSpan.FromSeconds(highlightSeconds);
        _list.HighlightDuration = duration;
        _lowerPane.HighlightDuration = duration;

        _tree.ColorRules = rules;
        _lowerPane.ColorRules = rules;
        _tree.InvalidateVisual();
    }
}
