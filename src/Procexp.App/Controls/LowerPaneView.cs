using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Procexp.Model;
using SortKey = Procexp.Model.SortKey;

namespace Procexp.App.Controls;

/// <summary>What the lower pane is showing.</summary>
public enum LowerPaneMode
{
    /// <summary>Mapped files — the Linux equivalent of the DLLs view.</summary>
    Modules,

    /// <summary>Open file descriptors — the Handles view.</summary>
    Handles,

    /// <summary>Threads of the selected process.</summary>
    Threads,
}

/// <summary>
/// The lower pane: modules, handles or threads for the selected process.
/// </summary>
/// <remarks>
/// Three flat tables sharing one pane, matching Process Explorer's DLL and handle
/// views with a threads view added — Linux exposes per-thread detail without
/// privilege, so there is no reason to hide it behind the Properties window as
/// the macOS build must.
///
/// Detail is loaded off the UI thread and only for the selected process, which is
/// what keeps this affordable: enumerating every process's descriptors would cost
/// far more than the process sweep itself.
/// </remarks>
public sealed class LowerPaneView : UserControl
{
    private readonly IProcessDataProvider _sampler;

    private readonly DataTableView<ModuleInfo> _modules = new();
    private readonly DataTableView<FileDescriptorInfo> _handles = new();
    private readonly DataTableView<ThreadInfo> _threads = new();

    private readonly RowChangeTracker<string> _moduleChanges = new();
    private readonly RowChangeTracker<int> _handleChanges = new();
    private readonly RowChangeTracker<ulong> _threadChanges = new();

    private readonly Panel _host = new();
    private readonly TextBlock _summary = new()
    {
        FontSize = 11,
        VerticalAlignment = VerticalAlignment.Center,
    };
    private readonly ScrollBar _verticalScroll = new() { Orientation = Orientation.Vertical };
    private readonly ScrollBar _horizontalScroll = new() { Orientation = Orientation.Horizontal };

    private ProcessId? _process;
    private LowerPaneMode _mode = LowerPaneMode.Modules;
    private CancellationTokenSource? _loadCancellation;

    /// <summary>True when the last load was refused rather than genuinely empty.</summary>
    private bool _denied;

    /// <summary>Gone rows kept on screen for the fade, keyed by mode.</summary>
    private IReadOnlyList<ModuleInfo> _lastModules = [];
    private IReadOnlyList<FileDescriptorInfo> _lastHandles = [];
    private IReadOnlyList<ThreadInfo> _lastThreads = [];

    public LowerPaneView(IProcessDataProvider sampler)
    {
        _sampler = sampler;

        ConfigureModules();
        ConfigureHandles();
        ConfigureThreads();

        _host.Children.Add(_modules);
        _host.Children.Add(_handles);
        _host.Children.Add(_threads);

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            RowDefinitions = new RowDefinitions("Auto,*,Auto"),
        };

        var header = new Border
        {
            Padding = new Thickness(8, 4),
            BorderThickness = new Thickness(0, 1, 0, 1),
            BorderBrush = Avalonia.Media.Brushes.Gray,
            Opacity = 0.999,
            Child = _summary,
        };

        Grid.SetColumnSpan(header, 2);
        grid.Children.Add(header);

        Grid.SetRow(_host, 1);
        grid.Children.Add(_host);

        Grid.SetRow(_verticalScroll, 1);
        Grid.SetColumn(_verticalScroll, 1);
        grid.Children.Add(_verticalScroll);

        Grid.SetRow(_horizontalScroll, 2);
        grid.Children.Add(_horizontalScroll);

        Content = grid;

        _verticalScroll.Scroll += (_, _) => Active.VerticalOffset = _verticalScroll.Value;
        _horizontalScroll.Scroll += (_, _) => Active.HorizontalOffset = _horizontalScroll.Value;

        foreach (var table in new VirtualTableBase[] { _modules, _handles, _threads })
        {
            table.ScrollChanged += (_, _) => SyncScrollBars();
            table.SelectionChanged += (_, _) => UpdateSummary();
        }

        ApplyMode();
    }

    public bool IsDarkMode
    {
        get => _modules.IsDarkMode;
        set
        {
            _modules.IsDarkMode = value;
            _handles.IsDarkMode = value;
            _threads.IsDarkMode = value;
        }
    }

    public IReadOnlyList<ProcessColorRule> ColorRules { get; set; } = ProcessColorRule.Defaults;

    public LowerPaneMode Mode
    {
        get => _mode;
        set
        {
            if (_mode == value)
            {
                return;
            }

            _mode = value;
            ApplyMode();
            _ = ReloadAsync();
        }
    }

    private VirtualTableBase Active =>
        _mode switch
        {
            LowerPaneMode.Handles => _handles,
            LowerPaneMode.Threads => _threads,
            _ => _modules,
        };

    private void ApplyMode()
    {
        _modules.IsVisible = _mode == LowerPaneMode.Modules;
        _handles.IsVisible = _mode == LowerPaneMode.Handles;
        _threads.IsVisible = _mode == LowerPaneMode.Threads;

        SyncScrollBars();
        UpdateSummary();
    }

    /// <summary>Point the pane at a process, or at nothing.</summary>
    public void SetProcess(ProcessId? process)
    {
        if (_process == process)
        {
            return;
        }

        _process = process;

        // Highlighting is relative to the list the viewer has been watching, so
        // switching process starts a fresh baseline rather than reporting every
        // row of the new process as new.
        _moduleChanges.Reset();
        _handleChanges.Reset();
        _threadChanges.Reset();

        _lastModules = [];
        _lastHandles = [];
        _lastThreads = [];

        _ = ReloadAsync();
    }

    /// <summary>Reload the active view for the current process.</summary>
    public async Task ReloadAsync()
    {
        // A slow load must not stack up behind a faster selection change.
        _loadCancellation?.Cancel();
        _loadCancellation = new CancellationTokenSource();
        var token = _loadCancellation.Token;

        if (_process is not { } id)
        {
            _modules.SetRows([]);
            _handles.SetRows([]);
            _threads.SetRows([]);
            UpdateSummary();
            return;
        }

        var mode = _mode;
        var now = DateTimeOffset.Now;

        // A previous refusal may have left an explanatory message behind.
        ResetEmptyMessages();
        _denied = false;

        try
        {
            switch (mode)
            {
                case LowerPaneMode.Modules:
                    await LoadModulesAsync(id, now, token).ConfigureAwait(true);
                    break;
                case LowerPaneMode.Handles:
                    await LoadHandlesAsync(id, now, token).ConfigureAwait(true);
                    break;
                case LowerPaneMode.Threads:
                    await LoadThreadsAsync(id, now, token).ConfigureAwait(true);
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (ProviderException e)
        {
            // An empty table under the default message would claim the process
            // has no libraries or descriptors, when in fact we were not allowed
            // to look. Unlike most of /proc, maps and fd are gated by
            // ptrace_may_access and readable only by the owner.
            ClearActive(
                e.Kind switch
                {
                    ProviderErrorKind.NotPermitted =>
                        "Not permitted. Mapped files and descriptors are readable only by the process "
                            + "owner; run as that user, or install the privileged helper.",
                    ProviderErrorKind.ProcessGone => "The process has exited.",
                    _ => e.Message,
                }
            );

            _denied = true;
        }

        UpdateSummary();
    }

    private void ClearActive(string message)
    {
        switch (_mode)
        {
            case LowerPaneMode.Modules:
                _modules.EmptyMessage = message;
                _modules.SetRows([]);
                break;
            case LowerPaneMode.Handles:
                _handles.EmptyMessage = message;
                _handles.SetRows([]);
                break;
            case LowerPaneMode.Threads:
                _threads.EmptyMessage = message;
                _threads.SetRows([]);
                break;
        }
    }

    /// <summary>Restore the ordinary empty text after a successful load.</summary>
    private void ResetEmptyMessages()
    {
        _modules.EmptyMessage = "No mapped files.";
        _handles.EmptyMessage = "No open descriptors.";
        _threads.EmptyMessage = "No threads.";
    }

    private async Task LoadModulesAsync(ProcessId id, DateTimeOffset now, CancellationToken token)
    {
        var live = await _sampler.ModulesAsync(id, token).ConfigureAwait(true);
        token.ThrowIfCancellationRequested();

        var gone = _moduleChanges.Observe(live.Select(m => m.Path), now);

        // Rows that have gone are kept briefly, taken from the previous list
        // since the provider no longer reports them.
        var ghosts = _lastModules.Where(m =>
            gone.Contains(m.Path) && live.All(l => l.Path != m.Path)
        );

        _lastModules = live;
        _modules.SetRows([.. live, .. ghosts]);
    }

    private async Task LoadHandlesAsync(ProcessId id, DateTimeOffset now, CancellationToken token)
    {
        var live = await _sampler.FileDescriptorsAsync(id, token).ConfigureAwait(true);
        token.ThrowIfCancellationRequested();

        var gone = _handleChanges.Observe(live.Select(h => h.Fd), now);
        var ghosts = _lastHandles.Where(h => gone.Contains(h.Fd) && live.All(l => l.Fd != h.Fd));

        _lastHandles = live;
        _handles.SetRows([.. live, .. ghosts]);
    }

    private async Task LoadThreadsAsync(ProcessId id, DateTimeOffset now, CancellationToken token)
    {
        var live = await _sampler.ThreadsAsync(id, token).ConfigureAwait(true);
        token.ThrowIfCancellationRequested();

        var gone = _threadChanges.Observe(live.Select(t => t.Tid), now);
        var ghosts = _lastThreads.Where(t => gone.Contains(t.Tid) && live.All(l => l.Tid != t.Tid));

        _lastThreads = live;
        _threads.SetRows([.. live, .. ghosts]);
    }

    /// <summary>Let highlights fade between reloads.</summary>
    public void Tick(DateTimeOffset now)
    {
        var changed = _mode switch
        {
            LowerPaneMode.Handles => _handleChanges.Expire(now),
            LowerPaneMode.Threads => _threadChanges.Expire(now),
            _ => _moduleChanges.Expire(now),
        };

        if (changed)
        {
            Active.InvalidateVisual();
        }
    }

    // ---- Column definitions -------------------------------------------------

    private void ConfigureModules()
    {
        _modules.EmptyMessage = "No mapped files.";
        _modules.IdentityOf = m => ((ModuleInfo)m).Path;
        _modules.RowColour = m =>
            _moduleChanges.Colour(((ModuleInfo)m).Path, ColorRules, IsDarkMode);

        _modules.SetColumns([
            new(
                ModuleColumns.Title(ModuleColumn.Name),
                ModuleColumns.DefaultWidth(ModuleColumn.Name),
                m => m.Name
            ),
            new(
                ModuleColumns.Title(ModuleColumn.Path),
                ModuleColumns.DefaultWidth(ModuleColumn.Path),
                m => m.Path
            ),
            new(
                ModuleColumns.Title(ModuleColumn.Base),
                ModuleColumns.DefaultWidth(ModuleColumn.Base),
                m => $"0x{m.LoadAddress:x}",
                RightAligned: true,
                Sort: m => SortKey.Number(m.LoadAddress)
            ),
            new(
                ModuleColumns.Title(ModuleColumn.Size),
                ModuleColumns.DefaultWidth(ModuleColumn.Size),
                m => ValueFormat.Bytes(m.Size),
                RightAligned: true,
                Sort: m => SortKey.Number(m.Size)
            ),
            new(
                ModuleColumns.Title(ModuleColumn.Permissions),
                ModuleColumns.DefaultWidth(ModuleColumn.Permissions),
                m => m.Permissions
            ),
        ]);
    }

    private void ConfigureHandles()
    {
        _handles.EmptyMessage = "No open descriptors, or they belong to another user.";
        _handles.IdentityOf = h => ((FileDescriptorInfo)h).Fd;
        _handles.RowColour = h =>
            _handleChanges.Colour(((FileDescriptorInfo)h).Fd, ColorRules, IsDarkMode);

        _handles.SetColumns([
            new(
                HandleColumns.Title(HandleColumn.Fd),
                HandleColumns.DefaultWidth(HandleColumn.Fd),
                h => h.Fd.ToString(CultureInfo.InvariantCulture),
                RightAligned: true,
                Sort: h => SortKey.Number(h.Fd)
            ),
            new(
                HandleColumns.Title(HandleColumn.Kind),
                HandleColumns.DefaultWidth(HandleColumn.Kind),
                h => h.Kind.ToString()
            ),
            new(
                HandleColumns.Title(HandleColumn.Name),
                HandleColumns.DefaultWidth(HandleColumn.Name),
                h => h.Name
            ),
            new(
                HandleColumns.Title(HandleColumn.Access),
                HandleColumns.DefaultWidth(HandleColumn.Access),
                h => h.Access ?? ""
            ),
            new(
                HandleColumns.Title(HandleColumn.Offset),
                HandleColumns.DefaultWidth(HandleColumn.Offset),
                h => h.Offset?.ToString(CultureInfo.InvariantCulture) ?? "",
                RightAligned: true,
                Sort: h => h.Offset is { } o ? SortKey.Number(o) : SortKey.None
            ),
            new(
                HandleColumns.Title(HandleColumn.Inode),
                HandleColumns.DefaultWidth(HandleColumn.Inode),
                h => h.Inode?.ToString(CultureInfo.InvariantCulture) ?? "",
                RightAligned: true,
                Sort: h => h.Inode is { } i ? SortKey.Number(i) : SortKey.None
            ),
        ]);
    }

    private void ConfigureThreads()
    {
        _threads.EmptyMessage = "No threads.";
        _threads.IdentityOf = t => ((ThreadInfo)t).Tid;
        _threads.RowColour = t =>
            _threadChanges.Colour(((ThreadInfo)t).Tid, ColorRules, IsDarkMode);

        _threads.SetColumns([
            new(
                ThreadColumns.Title(ThreadColumn.Tid),
                ThreadColumns.DefaultWidth(ThreadColumn.Tid),
                t => t.Tid.ToString(CultureInfo.InvariantCulture),
                RightAligned: true,
                Sort: t => SortKey.Number(t.Tid)
            ),
            new(
                ThreadColumns.Title(ThreadColumn.Name),
                ThreadColumns.DefaultWidth(ThreadColumn.Name),
                t => t.Name
            ),
            new(
                ThreadColumns.Title(ThreadColumn.State),
                ThreadColumns.DefaultWidth(ThreadColumn.State),
                t => t.State
            ),
            new(
                ThreadColumns.Title(ThreadColumn.CpuTime),
                ThreadColumns.DefaultWidth(ThreadColumn.CpuTime),
                t => ValueFormat.Duration(t.CpuTime),
                RightAligned: true,
                Sort: t => SortKey.Number(t.CpuTime)
            ),
            new(
                ThreadColumns.Title(ThreadColumn.UserTime),
                ThreadColumns.DefaultWidth(ThreadColumn.UserTime),
                t => ValueFormat.Duration(t.UserTime),
                RightAligned: true,
                Sort: t => SortKey.Number(t.UserTime)
            ),
            new(
                ThreadColumns.Title(ThreadColumn.KernelTime),
                ThreadColumns.DefaultWidth(ThreadColumn.KernelTime),
                t => ValueFormat.Duration(t.KernelTime),
                RightAligned: true,
                Sort: t => SortKey.Number(t.KernelTime)
            ),
            // wchan has no macOS counterpart at all — this is Process Explorer's
            // Wait Reason column, which the macOS build has to leave out.
            new(
                ThreadColumns.Title(ThreadColumn.WaitChannel),
                ThreadColumns.DefaultWidth(ThreadColumn.WaitChannel),
                t => t.WaitChannel ?? ""
            ),
            new(
                ThreadColumns.Title(ThreadColumn.Priority),
                ThreadColumns.DefaultWidth(ThreadColumn.Priority),
                t => t.Priority.ToString(CultureInfo.InvariantCulture),
                RightAligned: true,
                Sort: t => SortKey.Number(t.Priority)
            ),
            new(
                ThreadColumns.Title(ThreadColumn.LastCpu),
                ThreadColumns.DefaultWidth(ThreadColumn.LastCpu),
                t => t.LastCpu.ToString(CultureInfo.InvariantCulture),
                RightAligned: true,
                Sort: t => SortKey.Number(t.LastCpu)
            ),
        ]);
    }

    // ---- Chrome -------------------------------------------------------------

    private void SyncScrollBars()
    {
        var table = Active;

        _verticalScroll.Minimum = 0;
        _verticalScroll.Maximum = table.MaxVerticalOffset;
        _verticalScroll.ViewportSize = table.ViewportHeight;
        _verticalScroll.Value = table.VerticalOffset;

        _horizontalScroll.Minimum = 0;
        _horizontalScroll.Maximum = table.MaxHorizontalOffset;
        _horizontalScroll.ViewportSize = table.ScrollableViewportWidth;
        _horizontalScroll.Value = table.HorizontalOffset;
    }

    private void UpdateSummary()
    {
        SyncScrollBars();

        if (_process is null)
        {
            _summary.Text = "Select a process to inspect its modules, handles and threads.";
            return;
        }

        // Reporting "0 mapped files" after refusing to look would contradict the
        // message in the table below it.
        if (_denied)
        {
            _summary.Text = _mode switch
            {
                LowerPaneMode.Handles => "Descriptors not readable for this process.",
                LowerPaneMode.Threads => "Threads not readable for this process.",
                _ => "Mapped files not readable for this process.",
            };

            return;
        }

        _summary.Text = _mode switch
        {
            LowerPaneMode.Modules => Describe(
                _modules.Rows.Count,
                "mapped file",
                _modules.SelectedItem?.Path
            ),
            LowerPaneMode.Handles => Describe(
                _handles.Rows.Count,
                "descriptor",
                _handles.SelectedItem?.Name
            ),
            LowerPaneMode.Threads => Describe(
                _threads.Rows.Count,
                "thread",
                _threads.SelectedItem is { } t ? $"tid {t.Tid} — {t.State}" : null
            ),
            _ => "",
        };

        static string Describe(int count, string noun, string? selected)
        {
            var plural = count == 1 ? noun : noun + "s";
            var head = $"{count} {plural}";
            return selected is null ? head : $"{head}    —    {selected}";
        }
    }
}
