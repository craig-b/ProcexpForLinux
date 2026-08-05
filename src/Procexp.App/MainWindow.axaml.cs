using System.Diagnostics;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Markup.Xaml;
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

    private readonly ActionCoordinator _actions = null!;
    private readonly ProcessTreeView _tree = null!;
    private readonly ScrollBar _verticalScroll = null!;
    private readonly ScrollBar _horizontalScroll = null!;
    private readonly TextBlock _statusText = null!;
    private readonly TextBlock _timingText = null!;
    private readonly LowerPaneView _lowerPane = null!;
    private SystemInfoWindow? _systemInfo;

    private readonly SystemStatsProvider _systemStats = new();
    private readonly GpuProvider _gpu = new();

    /// <summary>
    /// Fills the columns the sweep is too fast to gather: description, company,
    /// version, provenance, autostart location and per-process GPU.
    /// </summary>
    private readonly ProcessEnricher _enricher = new();

    private bool _paused;
    private double _intervalSeconds = 1.0;
    private string _filter = "";
    private bool _confirmActions = true;
    private IReadOnlyList<ProcessColorRule> _colorRules = ProcessColorRule.Defaults;

    private readonly AppSettings _settings = SettingsStore.Load();
    private bool _enrichmentDirty;
    private CancellationTokenSource? _saveDebounce;

    private readonly Queue<double> _sweepTimes = new();
    private readonly Queue<double> _layoutTimes = new();

    private IReadOnlyList<(Column Column, double Width)> _columns = [];

    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);

        _actions = new ActionCoordinator(this, () => _confirmActions);
        _tree = Get<ProcessTreeView>("Tree");
        _verticalScroll = Get<ScrollBar>("VerticalScroll");
        _horizontalScroll = Get<ScrollBar>("HorizontalScroll");
        _statusText = Get<TextBlock>("StatusText");
        _timingText = Get<TextBlock>("TimingText");

        Get<Border>("ScrollGutter").Width = _tree.NamePaneWidth;

        ApplyColumns(_settings.Columns);

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
        SizeChanged += (_, _) => ScheduleSave();

        // Enrichment arrives out of band, so the list has to be told. Marshalled
        // and coalesced: a first sweep queues several hundred image lookups, and
        // rebuilding per completion would be hundreds of rebuilds.
        _enricher.Updated += (_, _) =>
            Dispatcher.UIThread.Post(() => _enrichmentDirty = true, DispatcherPriority.Background);

        Opened += (_, _) =>
        {
            SetLowerPaneVisible(_settings.ShowLowerPane);
            _lowerPane.Mode = _settings.LowerPaneMode;
            Get<ComboBox>("PaneModeCombo").SelectedIndex = (int)_settings.LowerPaneMode;

            _ = RunSamplingLoopAsync();
            _ = RunHighlightTickerAsync();
        };

        Closed += (_, _) =>
        {
            _lifetime.Cancel();
            _enricher.Dispose();
            SaveSettings();
        };
    }

    private T Get<T>(string name)
        where T : Control => this.FindControl<T>(name)!;

    /// <summary>
    /// Rebuild the column layout, honouring any saved widths.
    /// </summary>
    /// <remarks>
    /// Widths are keyed by column name rather than position, so adding or
    /// reordering columns does not shuffle every stored width onto the wrong one.
    /// </remarks>
    private void ApplyColumns(IReadOnlyList<Column> columns)
    {
        var normalised = Columns_.Normalise(columns);

        _columns =
        [
            (Column.Name, _tree.NamePaneWidth),
            .. normalised
                .Where(c => c != Column.Name)
                .Select(c =>
                    (
                        c,
                        _settings.ColumnWidths.TryGetValue(c.ToString(), out var w)
                            ? w
                            : Columns.DefaultWidth(c)
                    )
                ),
        ];
    }

    private void ApplySettings()
    {
        _tree.SortColumn = _settings.SortColumn;
        _tree.SortDescending = _settings.SortDescending;
        _tree.NamePaneWidth = _settings.NamePaneWidth;
        _list.HighlightNewAndDead = _settings.HighlightNewAndDead;
        _intervalSeconds = _settings.RefreshSeconds;
        _confirmActions = _settings.ConfirmActions;
        _columnSets = _settings.ColumnSets;
        ApplyOptions(
            _settings.ConfirmActions,
            _settings.HighlightSeconds,
            ColorRuleSetting.ToRules(_settings.ColorRules)
        );

        Width = _settings.WindowWidth;
        Height = _settings.WindowHeight;
        Topmost = _settings.AlwaysOnTop;

        Get<ToggleSwitch>("TreeToggle").IsChecked = _settings.TreeMode;
        Get<MenuItem>("MenuTreeMode").IsChecked = _settings.TreeMode;
        Get<MenuItem>("MenuHighlight").IsChecked = _settings.HighlightNewAndDead;
        Get<MenuItem>("MenuAlwaysOnTop").IsChecked = _settings.AlwaysOnTop;
        Get<Border>("ScrollGutter").Width = _tree.NamePaneWidth;

        // The speed radio group has to agree with the interval that was restored,
        // or the menu claims one rate while the loop runs at another.
        Get<MenuItem>("MenuSpeedFast").IsChecked = _settings.RefreshSeconds <= 0.5;
        Get<MenuItem>("MenuSpeedNormal").IsChecked =
            Math.Abs(_settings.RefreshSeconds - 1.0) < 0.01;
        Get<MenuItem>("MenuSpeedSlow").IsChecked = Math.Abs(_settings.RefreshSeconds - 2.0) < 0.01;
        Get<MenuItem>("MenuSpeedVerySlow").IsChecked = _settings.RefreshSeconds >= 5.0;
    }

    /// <summary>
    /// Queue a settings save, coalescing bursts.
    /// </summary>
    /// <remarks>
    /// Saving only on window close loses everything if the app is killed or
    /// crashes — and a process explorer is a tool people SIGTERM. Debounced so
    /// that dragging a splitter does not write the file on every frame.
    /// </remarks>
    private void ScheduleSave()
    {
        _saveDebounce?.Cancel();
        _saveDebounce = new CancellationTokenSource();
        var token = _saveDebounce.Token;

        _ = Task.Run(
            async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), token).ConfigureAwait(false);
                    await Dispatcher.UIThread.InvokeAsync(SaveSettings);
                }
                catch (OperationCanceledException)
                {
                    // Superseded by a later change.
                }
            },
            CancellationToken.None
        );
    }

    private void SaveSettings()
    {
        SettingsStore.Save(
            _settings with
            {
                Columns = [.. _columns.Select(c => c.Column)],
                ColumnWidths = _columns.ToDictionary(c => c.Column.ToString(), c => c.Width),
                SortColumn = _tree.SortColumn,
                SortDescending = _tree.SortDescending,
                TreeMode = Get<ToggleSwitch>("TreeToggle").IsChecked == true,
                ShowLowerPane = Get<ContentControl>("LowerPaneHost").IsVisible,
                LowerPaneMode = _lowerPane.Mode,
                RefreshSeconds = _intervalSeconds,
                HighlightNewAndDead = _list.HighlightNewAndDead,
                HighlightSeconds = _list.HighlightDuration.TotalSeconds,
                ConfirmActions = _confirmActions,
                ColorRules = ColorRuleSetting.FromRules(_colorRules),
                ColumnSets = _columnSets,
                AlwaysOnTop = Topmost,
                NamePaneWidth = _tree.NamePaneWidth,
                WindowWidth = Width,
                WindowHeight = Height,
            }
        );
    }

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
            ScheduleSave();
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
        ScheduleSave();

        if (visible)
        {
            _lowerPane.SetProcess(_tree.SelectedProcess?.Id);
        }
    }

    private void WireTree()
    {
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
            _columns = columns;
            Get<Border>("ScrollGutter").Width = _tree.NamePaneWidth;
            SyncScrollBars();
            ScheduleSave();
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
            ScheduleSave();
        };

        _verticalScroll.Scroll += (_, _) => _tree.VerticalOffset = _verticalScroll.Value;
        _horizontalScroll.Scroll += (_, _) => _tree.HorizontalOffset = _horizontalScroll.Value;
    }

    private void WireToolbar()
    {
        Get<Button>("PauseButton").Click += (_, _) => TogglePause();
        Get<Button>("RefreshButton").Click += (_, _) => _ = RefreshNowAsync();
        Get<Button>("KillButton").Click += (_, _) => _ = KillSelectedAsync(tree: false);
        Get<Button>("SuspendButton").Click += (_, _) => _ = SuspendSelectedAsync();

        var treeToggle = Get<ToggleSwitch>("TreeToggle");
        treeToggle.IsCheckedChanged += (_, _) =>
        {
            Get<MenuItem>("MenuTreeMode").IsChecked = treeToggle.IsChecked == true;
            Rebuild();
            ScheduleSave();
        };

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

    private void WireMenus()
    {
        Get<MenuItem>("MenuExit").Click += (_, _) => Close();
        Get<MenuItem>("MenuRun").Click += (_, _) => new RunDialog().ShowDialog(this);
        Get<MenuItem>("MenuSave").Click += (_, _) => _ = SaveProcessListAsync();

        var alwaysOnTop = Get<MenuItem>("MenuAlwaysOnTop");
        alwaysOnTop.Click += (_, _) =>
        {
            Topmost = alwaysOnTop.IsChecked;
            ScheduleSave();
        };

        var highlight = Get<MenuItem>("MenuHighlight");
        highlight.Click += (_, _) =>
        {
            _list.HighlightNewAndDead = highlight.IsChecked;
            ScheduleSave();
        };

        Get<MenuItem>("MenuInstallHelper").Click += (_, _) => _ = ShowHelperStatusAsync();

        var pause = Get<MenuItem>("MenuPause");
        pause.Click += (_, _) => SetPaused(pause.IsChecked);

        Get<MenuItem>("MenuRefreshNow").Click += (_, _) => _ = RefreshNowAsync();

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

        Get<MenuItem>("MenuKill").Click += (_, _) => _ = KillSelectedAsync(tree: false);
        Get<MenuItem>("MenuKillTree").Click += (_, _) => _ = KillSelectedAsync(tree: true);
        Get<MenuItem>("MenuSuspend").Click += (_, _) => _ = SuspendSelectedAsync();
        Get<MenuItem>("MenuResume").Click += (_, _) => _ = ResumeSelectedAsync();
        Get<MenuItem>("MenuRestart").Click += (_, _) => _ = RestartSelectedAsync();

        WireNice("MenuNiceHighest", -20);
        WireNice("MenuNiceHigh", -5);
        WireNice("MenuNiceNormal", 0);
        WireNice("MenuNiceLow", 5);
        WireNice("MenuNiceLowest", 19);

        Get<MenuItem>("MenuAbout").Click += (_, _) => ShowAbout();
        Get<MenuItem>("MenuProperties").Click += (_, _) => ShowProperties();
        Get<MenuItem>("MenuSystemInfo").Click += (_, _) => ShowSystemInfo();
        Get<MenuItem>("MenuColumns").Click += (_, _) => _ = ChooseColumnsAsync();
        Get<MenuItem>("MenuSettings").Click += (_, _) => _ = ShowSettingsAsync();
        Get<MenuItem>("MenuSaveColumnSet").Click += (_, _) => _ = SaveColumnSetAsync();
        Get<MenuItem>("MenuOrganizeColumnSets").Click += (_, _) => _ = OrganizeColumnSetsAsync();
        RebuildColumnSetsMenu();
        Get<MenuItem>("MenuFind").Click += (_, _) => ShowFind();

        void WireSpeed(string name, double seconds) =>
            Get<MenuItem>(name).Click += (_, _) =>
            {
                _intervalSeconds = seconds;
                ScheduleSave();
            };

        void WireNice(string name, int nice) =>
            Get<MenuItem>(name).Click += (_, _) => _ = SetNiceSelectedAsync(nice);
    }

    private void WireContextMenu()
    {
        Get<MenuItem>("CtxProperties").Click += (_, _) => ShowProperties();
        Get<MenuItem>("CtxKill").Click += (_, _) => _ = KillSelectedAsync(tree: false);
        Get<MenuItem>("CtxKillTree").Click += (_, _) => _ = KillSelectedAsync(tree: true);
        Get<MenuItem>("CtxSuspend").Click += (_, _) => _ = SuspendSelectedAsync();
        Get<MenuItem>("CtxResume").Click += (_, _) => _ = ResumeSelectedAsync();
        Get<MenuItem>("CtxRestart").Click += (_, _) => _ = RestartSelectedAsync();

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

    private async Task RunSamplingLoopAsync()
    {
        while (!_lifetime.IsCancellationRequested)
        {
            if (!_paused)
            {
                await SampleOnceAsync().ConfigureAwait(false);
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(_intervalSeconds), _lifetime.Token)
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
            snapshot = await _sampler.SnapshotAsync(_lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        var sweep = watch.Elapsed.TotalMilliseconds;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            Record(_sweepTimes, sweep);
            _list.Apply(_enricher.Enrich(snapshot), DateTimeOffset.Now);
            Rebuild();

            if (Get<ContentControl>("LowerPaneHost").IsVisible)
            {
                _ = _lowerPane.ReloadAsync();
            }

            _systemInfo?.SetProcessCounts(snapshot.Processes.Count, snapshot.System.ThreadCount);
        });
    }

    /// <summary>
    /// Fades highlights out between sweeps.
    /// </summary>
    /// <remarks>
    /// Runs faster than the sampling interval so a one-second tint does not
    /// linger for a whole slow refresh cycle. It repaints only when something
    /// actually expired.
    /// </remarks>
    private async Task RunHighlightTickerAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));

        try
        {
            while (await timer.WaitForNextTickAsync(_lifetime.Token).ConfigureAwait(false))
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    var now = DateTimeOffset.Now;

                    if (_list.Tick(now) || _enrichmentDirty)
                    {
                        _enrichmentDirty = false;
                        _list.Apply(_enricher.Enrich(_list.Current), now);
                        Rebuild();
                    }

                    _lowerPane.Tick(now);
                });
            }
        }
        catch (OperationCanceledException)
        {
            // Window closed.
        }
    }

    private async Task RefreshNowAsync()
    {
        if (!_paused)
        {
            await SampleOnceAsync().ConfigureAwait(true);
            return;
        }

        // Refreshing while paused takes one sample without resuming, which is
        // how Process Explorer's Update Now behaves.
        await SampleOnceAsync().ConfigureAwait(true);
    }

    private void TogglePause() => SetPaused(!_paused);

    private void SetPaused(bool paused)
    {
        _paused = paused;
        Get<Button>("PauseButton").Content = paused ? "Resume" : "Pause";
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

        _tree.SetRows(rows, _columns);
        Record(_layoutTimes, watch.Elapsed.TotalMilliseconds);

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

        var prefix = _paused ? "PAUSED    —    " : "";

        _statusText.Text = selected is null
            ? $"{prefix}{snapshot.Processes.Count} processes, {snapshot.System.ThreadCount} threads"
            : $"{prefix}{snapshot.Processes.Count} processes, {snapshot.System.ThreadCount} threads    —    "
                + $"{selected.Name} (pid {selected.Id.Pid}), {selected.ThreadCount} threads, "
                + $"{ValueFormat.Bytes(selected.ResidentSize)}";

        _timingText.Text = string.Create(
            CultureInfo.InvariantCulture,
            $"sweep {Average(_sweepTimes), 6:F1} ms   layout {Average(_layoutTimes), 5:F2} ms   "
                + $"paint {_tree.AverageRenderMilliseconds, 5:F2} ms   "
                + $"{_tree.LastRenderedRowCount}/{snapshot.Processes.Count} rows drawn"
        );
    }

    // ---- Actions ------------------------------------------------------------

    /// <summary>
    /// The selected process, or null when the selection refers to a row that is
    /// only still on screen because it is fading out.
    /// </summary>
    private ProcessRecord? ActionableSelection()
    {
        var selected = _tree.SelectedProcess;
        if (selected is null)
        {
            return null;
        }

        // Acting on a ghost row would signal a process that has already exited,
        // or worse, whatever has since inherited its pid.
        return _list.IsDead(selected.Id) ? null : selected;
    }

    private async Task KillSelectedAsync(bool tree)
    {
        if (ActionableSelection() is { } process)
        {
            await _actions.KillAsync(process, tree, _list.Current).ConfigureAwait(true);
            await RefreshNowAsync().ConfigureAwait(true);
        }
    }

    private async Task SuspendSelectedAsync()
    {
        if (ActionableSelection() is { } process)
        {
            await _actions.SuspendAsync(process).ConfigureAwait(true);
            await RefreshNowAsync().ConfigureAwait(true);
        }
    }

    private async Task ResumeSelectedAsync()
    {
        if (ActionableSelection() is { } process)
        {
            await _actions.ResumeAsync(process).ConfigureAwait(true);
            await RefreshNowAsync().ConfigureAwait(true);
        }
    }

    private async Task RestartSelectedAsync()
    {
        if (ActionableSelection() is { } process)
        {
            await _actions.RestartAsync(process, _list.Current).ConfigureAwait(true);
            await RefreshNowAsync().ConfigureAwait(true);
        }
    }

    private async Task SetNiceSelectedAsync(int nice)
    {
        if (ActionableSelection() is { } process)
        {
            await _actions.SetNiceAsync(process, nice).ConfigureAwait(true);
            await RefreshNowAsync().ConfigureAwait(true);
        }
    }

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
        if (ActionableSelection() is not { } process)
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
    /// Single-instance, unlike Properties. Two copies would each run their own
    /// one-second stats loop and disagree on every delta, since the counters are
    /// consumed rather than sampled.
    /// </remarks>
    private void ShowSystemInfo()
    {
        if (_systemInfo is { } existing)
        {
            existing.Activate();
            return;
        }

        _systemInfo = new SystemInfoWindow(_systemStats, _gpu, _tree.IsDarkMode);
        _systemInfo.SetProcessCounts(
            _list.Current.Processes.Count,
            _list.Current.System.ThreadCount
        );
        _systemInfo.Closed += (_, _) => _systemInfo = null;
        _systemInfo.Show(this);
    }

    // ---- Column sets --------------------------------------------------------

    private IReadOnlyList<ColumnSet> _columnSets = [];

    /// <summary>
    /// Rebuild the Column Sets menu: the two commands, then one item per saved
    /// set. Rebuilt rather than bound, since the menu is the only view of them.
    /// </summary>
    private void RebuildColumnSetsMenu()
    {
        var menu = Get<MenuItem>("MenuColumnSets");
        var fixedItems = menu.Items.OfType<Control>().Take(3).ToList();

        menu.Items.Clear();
        foreach (var item in fixedItems)
        {
            menu.Items.Add(item);
        }

        Get<Separator>("MenuColumnSetsSeparator").IsVisible = _columnSets.Count > 0;

        foreach (var set in _columnSets)
        {
            var item = new MenuItem { Header = set.Name };
            var columns = set.Columns;
            item.Click += (_, _) =>
            {
                ApplyColumns(columns);
                _tree.SetRows([], _columns);
                Rebuild();
                ScheduleSave();
            };
            menu.Items.Add(item);
        }
    }

    private async Task SaveColumnSetAsync()
    {
        var prompt = new TextPromptDialog(
            "Save Column Set",
            "Name for this column layout:",
            $"Set {_columnSets.Count + 1}"
        );
        await prompt.ShowDialog(this).ConfigureAwait(true);

        if (prompt.Result is not { } name)
        {
            return;
        }

        // Saving over an existing name replaces it, which is what a user
        // re-saving a tweaked layout means.
        var columns = _columns.Select(c => c.Column).ToList();
        _columnSets =
        [
            .. _columnSets.Where(s => s.Name != name),
            new ColumnSet { Name = name, Columns = columns },
        ];

        RebuildColumnSetsMenu();
        ScheduleSave();
    }

    private async Task OrganizeColumnSetsAsync()
    {
        var window = new ColumnSetsWindow(_columnSets);
        await window.ShowDialog(this).ConfigureAwait(true);

        if (window.Result is { } kept)
        {
            _columnSets = kept;
            RebuildColumnSetsMenu();
            ScheduleSave();
        }
    }

    private async Task ChooseColumnsAsync()
    {
        var chooser = new ColumnChooserWindow([.. _columns.Select(c => c.Column)]);
        await chooser.ShowDialog(this).ConfigureAwait(true);

        if (chooser.Result is { } chosen)
        {
            ApplyColumns(chosen);
            Rebuild();

            // Persist immediately. A column layout is deliberate work, and losing
            // it to a crash before the next clean exit would be irritating.
            SaveSettings();
        }
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

            await writer
                .WriteLineAsync(string.Join('\t', _columns.Select(c => Columns.Title(c.Column))))
                .ConfigureAwait(true);

            foreach (var row in rows)
            {
                var indent = new string(' ', row.Depth * 2);
                var cells = _columns.Select(
                    (c, i) =>
                        i == 0
                            ? indent + Columns.Format(c.Column, row.Process)
                            : Columns.Format(c.Column, row.Process)
                );

                await writer.WriteLineAsync(string.Join('\t', cells)).ConfigureAwait(true);
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
            ScheduleSave();
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

    // ---- Helpers ------------------------------------------------------------

    private static void Record(Queue<double> samples, double value)
    {
        samples.Enqueue(value);
        while (samples.Count > 10)
        {
            samples.Dequeue();
        }
    }

    private static double Average(Queue<double> samples) =>
        samples.Count == 0 ? 0 : samples.Average();
}
