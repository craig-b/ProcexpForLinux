using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Procexp.App.Controls;
using Procexp.Gpu;
using Procexp.Metrics;
using Procexp.Model;

namespace Procexp.App.Dialogs;

/// <summary>
/// System-wide activity and hardware: CPU, memory, I/O, network and GPU.
/// </summary>
/// <remarks>
/// Ports the macOS System Information window. Graphs share
/// <see cref="HistoryGraphView"/> with the Properties window, and the numbers come
/// from the same providers the process list uses, so a figure here never
/// disagrees with the same figure there.
/// </remarks>
public sealed class SystemInfoWindow : Window
{
    private const int HistorySeconds = 120;

    private readonly SystemStatsProvider _stats;
    private readonly GpuProvider _gpu;
    private readonly HardwareInfo _hardware = HardwareInfo.Gather();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly bool _darkMode;

    private readonly HistoryGraphView _summaryCpu;
    private readonly HistoryGraphView _summaryMemory;
    private readonly HistoryGraphView _summaryIo;
    private readonly HistoryGraphView _summaryNetwork;

    private TabControl _tabs = null!;

    /// <summary>
    /// Bring a named tab forward — what the toolbar sparklines click through
    /// to, so a glance at a spike leads straight to the detail behind it.
    /// </summary>
    public void SelectTab(string header)
    {
        foreach (var item in _tabs.Items.OfType<TabItem>())
        {
            if (Equals(item.Header, header))
            {
                _tabs.SelectedItem = item;
                return;
            }
        }
    }

    private readonly HistoryGraphView _cpuDetail;
    private readonly List<HistoryGraphView> _coreGraphs = [];

    private readonly HistoryGraphView _memoryDetail;
    private readonly HistoryGraphView _swapDetail;
    private readonly HistoryGraphView _diskDetail;
    private readonly HistoryGraphView _networkDetail;
    private readonly HistoryGraphView _gpuDetail;

    private readonly DetailList _summaryNumbers = new();
    private readonly DetailList _cpuInfo = new();
    private readonly DetailList _memoryInfo = new();
    private readonly DetailList _diskInfo = new();
    private readonly DetailList _networkInfo = new();
    private readonly DetailList _gpuInfo = new();

    private int _processCount;
    private int _threadCount;

    public SystemInfoWindow(SystemStatsProvider stats, GpuProvider gpu, bool darkMode)
    {
        _stats = stats;
        _gpu = gpu;
        _darkMode = darkMode;

        Title = "System Information";
        Width = 900;
        Height = 700;

        _summaryCpu = Graph("CPU", percent: true);
        _summaryMemory = Graph("Physical Memory", percent: true);
        _summaryIo = Graph("Disk I/O", percent: false);
        _summaryNetwork = Graph("Network I/O", percent: false);

        _cpuDetail = Graph("CPU Usage", percent: true);
        _memoryDetail = Graph("Physical Memory", percent: true);

        // The CPU and memory graphs can say who was responsible; disk, network
        // and GPU have no per-process attribution to offer.
        _summaryCpu.DescribeSample = i => FromEnd(_topCpu, i);
        _cpuDetail.DescribeSample = i => FromEnd(_topCpu, i);
        _summaryMemory.DescribeSample = i => FromEnd(_topMemory, i);
        _memoryDetail.DescribeSample = i => FromEnd(_topMemory, i);
        _swapDetail = Graph("Swap", percent: true);
        _diskDetail = Graph("Disk Throughput", percent: false);
        _networkDetail = Graph("Network Throughput", percent: false);
        _gpuDetail = Graph("GPU", percent: true);

        BuildLayout();
        PopulateHardware();

        Opened += (_, _) => _ = RunAsync();
        Closed += (_, _) => _lifetime.Cancel();
    }

    /// <summary>Counts owned by the sampling engine rather than the stats provider.</summary>
    public void SetProcessCounts(int processes, int threads)
    {
        _processCount = processes;
        _threadCount = threads;
    }

    /// <summary>
    /// Remember who was busiest at this instant, so hovering a sample can say
    /// what caused the spike — the readout the macOS graphs have.
    /// </summary>
    public void RecordTopConsumers(string? cpu, string? memory)
    {
        Push(_topCpu, cpu);
        Push(_topMemory, memory);

        static void Push(List<string?> history, string? entry)
        {
            history.Add(entry);

            // One more than any graph's capacity: the oldest visible sample
            // must still have its label.
            while (history.Count > 200)
            {
                history.RemoveAt(0);
            }
        }
    }

    private readonly List<string?> _topCpu = [];
    private readonly List<string?> _topMemory = [];

    /// <summary>The label for a sample counted back from the newest.</summary>
    private static string? FromEnd(List<string?> history, int fromRight)
    {
        var index = history.Count - 1 - fromRight;
        return index >= 0 && index < history.Count ? history[index] : null;
    }

    private HistoryGraphView Graph(string title, bool percent)
    {
        var graph = new HistoryGraphView
        {
            Title = title,
            IsDarkMode = _darkMode,
            Capacity = HistorySeconds,
            Height = 110,
        };

        if (percent)
        {
            // A fixed ceiling keeps the height of a spike meaning the same thing
            // from one moment to the next.
            graph.FixedMaximum = 100;
            graph.FormatValue = v => string.Create(CultureInfo.InvariantCulture, $"{v:F0}%");
        }
        else
        {
            // ValueFormat.Bytes renders zero as empty, which is right in a table
            // cell and wrong here — it leaves the label reading just "/s".
            graph.FormatValue = v =>
            {
                var bytes = (ulong)Math.Max(0, v);
                return (bytes == 0 ? "0 B" : ValueFormat.Bytes(bytes)) + "/s";
            };
        }

        return graph;
    }

    private void BuildLayout()
    {
        _summaryCpu.AddSeries(Color.FromRgb(90, 200, 90));
        _summaryMemory.AddSeries(Color.FromRgb(200, 150, 60));
        _summaryIo.AddSeries(Color.FromRgb(200, 90, 200));
        _summaryNetwork.AddSeries(Color.FromRgb(90, 160, 220));

        _cpuDetail.AddSeries(Color.FromRgb(90, 200, 90));
        _memoryDetail.AddSeries(Color.FromRgb(200, 150, 60));
        _swapDetail.AddSeries(Color.FromRgb(220, 110, 90));
        _diskDetail.AddSeries(Color.FromRgb(200, 90, 200));
        _networkDetail.AddSeries(Color.FromRgb(90, 160, 220));
        _gpuDetail.AddSeries(Color.FromRgb(140, 200, 220));

        _tabs = new TabControl
        {
            FontSize = 13,
            Items =
            {
                new TabItem { Header = "Summary", Content = SummaryPage() },
                new TabItem { Header = "CPU", Content = CpuPage() },
                new TabItem
                {
                    Header = "Memory",
                    Content = Page([_memoryDetail, _swapDetail], _memoryInfo),
                },
                new TabItem { Header = "I/O", Content = Page([_diskDetail], _diskInfo) },
                new TabItem { Header = "Network", Content = Page([_networkDetail], _networkInfo) },
                new TabItem { Header = "GPU", Content = Page([_gpuDetail], _gpuInfo) },
            },
        };

        var close = new Button
        {
            Content = "Close",
            MinWidth = 88,
            IsCancel = true,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(12, 8, 12, 12),
        };

        close.Click += (_, _) => Close();

        var root = new DockPanel();
        DockPanel.SetDock(close, Dock.Bottom);
        root.Children.Add(close);
        root.Children.Add(_tabs);

        Content = root;
    }

    private Control SummaryPage()
    {
        var graphs = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*"),
            RowDefinitions = new RowDefinitions("Auto,Auto"),
            Margin = new Thickness(12),
        };

        Place(_summaryCpu, 0, 0);
        Place(_summaryMemory, 0, 1);
        Place(_summaryIo, 1, 0);
        Place(_summaryNetwork, 1, 1);

        var panel = new DockPanel();
        DockPanel.SetDock(graphs, Dock.Top);
        panel.Children.Add(graphs);
        panel.Children.Add(_summaryNumbers);

        return panel;

        void Place(Control control, int row, int column)
        {
            control.Margin = new Thickness(4);
            Grid.SetRow(control, row);
            Grid.SetColumn(control, column);
            graphs.Children.Add(control);
        }
    }

    /// <summary>
    /// The CPU page: aggregate usage above a grid of per-core graphs.
    /// </summary>
    /// <remarks>
    /// One graph per logical CPU, which on a 16-thread machine is 16 graphs
    /// updating each second. They are laid out in a scrolling grid rather than
    /// stretched to fit, so a 128-core machine produces a long page instead of
    /// slivers too thin to read.
    /// </remarks>
    private Control CpuPage()
    {
        var cores = new Grid { Margin = new Thickness(12, 0, 12, 12) };

        const int ColumnCount = 4;
        for (var c = 0; c < ColumnCount; c++)
        {
            cores.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        }

        for (var i = 0; i < _hardware.LogicalCores; i++)
        {
            if (i % ColumnCount == 0)
            {
                cores.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            }

            var graph = new HistoryGraphView
            {
                Title = $"CPU {i}",
                IsDarkMode = _darkMode,
                Capacity = HistorySeconds,
                FixedMaximum = 100,
                Height = 64,
                Margin = new Thickness(3),
                FormatValue = v => string.Create(CultureInfo.InvariantCulture, $"{v:F0}%"),
            };

            graph.AddSeries(Color.FromRgb(90, 200, 90));
            _coreGraphs.Add(graph);

            Grid.SetRow(graph, i / ColumnCount);
            Grid.SetColumn(graph, i % ColumnCount);
            cores.Children.Add(graph);
        }

        var stack = new StackPanel { Orientation = Orientation.Vertical };
        _cpuDetail.Margin = new Thickness(12, 12, 12, 6);
        stack.Children.Add(_cpuDetail);
        stack.Children.Add(cores);
        stack.Children.Add(_cpuInfo);

        return new ScrollViewer { Content = stack };
    }

    private static Control Page(IReadOnlyList<Control> graphs, Control detail)
    {
        var stack = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Margin = new Thickness(12, 12, 12, 0),
        };

        foreach (var graph in graphs)
        {
            graph.Margin = new Thickness(0, 0, 0, 8);
            stack.Children.Add(graph);
        }

        var panel = new DockPanel();
        DockPanel.SetDock(stack, Dock.Top);
        panel.Children.Add(stack);
        panel.Children.Add(detail);

        return panel;
    }

    // ---- Refresh ------------------------------------------------------------

    private async Task RunAsync()
    {
        // The first stats read only primes the deltas, so discard it rather than
        // plotting a zero that never happened.
        _stats.Read();

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));

        try
        {
            while (await timer.WaitForNextTickAsync(_lifetime.Token).ConfigureAwait(true))
            {
                Update(_stats.Read());
            }
        }
        catch (OperationCanceledException)
        {
            // Window closed.
        }
    }

    private void Update(SystemStats stats)
    {
        var memoryPercent =
            stats.MemoryTotal == 0 ? 0 : stats.MemoryUsed * 100.0 / stats.MemoryTotal;

        var swapPercent = stats.SwapTotal == 0 ? 0 : stats.SwapUsed * 100.0 / stats.SwapTotal;

        _summaryCpu.Append(stats.CpuTotalPercent);
        _summaryMemory.Append(memoryPercent);
        _summaryIo.Append(stats.DiskBytesPerSec);
        _summaryNetwork.Append(stats.NetworkBytesPerSec);

        _cpuDetail.Append(stats.CpuTotalPercent);
        _memoryDetail.Append(memoryPercent);
        _swapDetail.Append(swapPercent);
        _diskDetail.Append(stats.DiskBytesPerSec);
        _networkDetail.Append(stats.NetworkBytesPerSec);

        for (var i = 0; i < _coreGraphs.Count && i < stats.PerCoreCpuPercent.Count; i++)
        {
            _coreGraphs[i].Append(stats.PerCoreCpuPercent[i]);
        }

        _ = UpdateGpuAsync();
        PopulateNumbers(stats);
    }

    private async Task UpdateGpuAsync()
    {
        var percent = await _gpu.TotalGpuPercentAsync(_lifetime.Token).ConfigureAwait(true);
        if (percent is { } value)
        {
            _gpuDetail.Append(value);
        }
    }

    private void PopulateNumbers(SystemStats stats)
    {
        _summaryNumbers.Clear();
        _summaryNumbers.AddSection("Totals");
        _summaryNumbers.Add("Processes", _processCount.ToString(CultureInfo.InvariantCulture));
        _summaryNumbers.Add("Threads", _threadCount.ToString(CultureInfo.InvariantCulture));
        _summaryNumbers.Add(
            "Open descriptors",
            stats.HandleCount.ToString(CultureInfo.InvariantCulture)
        );
        _summaryNumbers.Add(
            "CPU",
            string.Create(CultureInfo.InvariantCulture, $"{stats.CpuTotalPercent:F1}%")
        );
        _summaryNumbers.Add(
            "Memory in use",
            $"{ValueFormat.Bytes(stats.MemoryUsed)} of {ValueFormat.Bytes(stats.MemoryTotal)}"
        );
        _summaryNumbers.Add("Uptime", Uptime());

        _memoryInfo.Clear();
        _memoryInfo.AddSection("Physical memory");
        _memoryInfo.Add("Total", ValueFormat.Bytes(stats.MemoryTotal));
        _memoryInfo.Add("In use", ValueFormat.Bytes(stats.MemoryUsed));
        _memoryInfo.Add("Cached", ValueFormat.Bytes(stats.MemoryCached));
        _memoryInfo.Add("Kernel", ValueFormat.Bytes(stats.MemoryKernel));
        _memoryInfo.Add(
            "Compressed",
            stats.MemoryCompressed > 0
                ? ValueFormat.Bytes(stats.MemoryCompressed)
                : "(no zram or zswap)",
            showWhenEmpty: true
        );
        _memoryInfo.Add("Page size", ValueFormat.Bytes((ulong)_hardware.PageSize));

        _memoryInfo.AddSection("Swap");
        _memoryInfo.Add(
            "Total",
            stats.SwapTotal > 0 ? ValueFormat.Bytes(stats.SwapTotal) : "(none configured)",
            true
        );
        _memoryInfo.Add("In use", ValueFormat.Bytes(stats.SwapUsed));

        _diskInfo.Clear();
        _diskInfo.AddSection("Throughput");
        _diskInfo.Add("Current", ValueFormat.Bytes(stats.DiskBytesPerSec) + "/s");
        _diskInfo.AddSection("Volumes");

        foreach (var volume in _hardware.Volumes)
        {
            var used = volume.TotalBytes - volume.AvailableBytes;
            _diskInfo.Add(
                volume.MountPoint,
                $"{ValueFormat.Bytes(used)} of {ValueFormat.Bytes(volume.TotalBytes)} used  ({volume.FileSystem})"
            );
        }

        _networkInfo.Clear();
        _networkInfo.AddSection("Throughput");
        _networkInfo.Add("Current", ValueFormat.Bytes(stats.NetworkBytesPerSec) + "/s");
        _networkInfo.AddSection("Interfaces");

        foreach (var nic in _hardware.NetworkInterfaces.Where(n => !n.IsLoopback))
        {
            var addresses =
                nic.Addresses.Count > 0 ? string.Join(", ", nic.Addresses) : "(no address)";
            var speed = nic.SpeedMbps is { } mbps ? $", {mbps} Mb/s" : "";
            _networkInfo.Add(nic.Name, $"{(nic.IsUp ? "up" : "down")}{speed} — {addresses}");
        }
    }

    private void PopulateHardware()
    {
        _cpuInfo.Clear();
        _cpuInfo.AddSection("Processor");
        _cpuInfo.Add("Model", _hardware.CpuModel);
        _cpuInfo.Add("Vendor", _hardware.CpuVendor);
        _cpuInfo.Add(
            "Cores",
            $"{_hardware.PhysicalCores} physical, {_hardware.LogicalCores} logical"
        );
        _cpuInfo.Add("Sockets", _hardware.Sockets.ToString(CultureInfo.InvariantCulture));
        _cpuInfo.Add("Base clock", _hardware.CpuMhz is { } mhz ? $"{mhz:F0} MHz" : null);
        _cpuInfo.Add("Maximum clock", _hardware.CpuMaxMhz is { } max ? $"{max:F0} MHz" : null);

        _cpuInfo.AddSection("Cache");
        _cpuInfo.Add("L1 data", ValueFormat.Bytes(_hardware.L1DataCache));
        _cpuInfo.Add("L1 instruction", ValueFormat.Bytes(_hardware.L1InstructionCache));
        _cpuInfo.Add("L2", ValueFormat.Bytes(_hardware.L2Cache));
        _cpuInfo.Add("L3", ValueFormat.Bytes(_hardware.L3Cache));

        _cpuInfo.AddSection("System");
        _cpuInfo.Add("Distribution", _hardware.DistributionName);
        _cpuInfo.Add("Kernel", _hardware.KernelVersion);
        _cpuInfo.Add("Architecture", _hardware.Architecture);
        _cpuInfo.Add("Hostname", _hardware.Hostname);
        _cpuInfo.Add("Booted", _hardware.BootTime is { } boot ? ValueFormat.DateTime(boot) : null);

        _gpuInfo.Clear();
        _gpuInfo.AddSection("Graphics");

        if (_hardware.Gpus.Count == 0)
        {
            _gpuInfo.Add("Devices", "None detected.", showWhenEmpty: true);
        }

        foreach (var device in _hardware.Gpus)
        {
            _gpuInfo.Add(
                device.Name,
                $"{device.Driver ?? "unknown driver"}  {device.PciId ?? ""}".Trim()
            );
        }

        if (_gpu.VideoMemory() is { } memory)
        {
            _gpuInfo.Add(
                "Video memory",
                $"{ValueFormat.Bytes(memory.Used)} of {ValueFormat.Bytes(memory.Total)} used"
            );
        }
        else
        {
            _gpuInfo.Add("Video memory", "Not reported by this driver.", showWhenEmpty: true);
        }
    }

    private string Uptime()
    {
        if (_hardware.BootTime is not { } boot)
        {
            return "";
        }

        var span = DateTimeOffset.Now - boot;
        return span.TotalDays >= 1
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"{(int)span.TotalDays}d {span.Hours}h {span.Minutes}m"
            )
            : string.Create(CultureInfo.InvariantCulture, $"{span.Hours}h {span.Minutes}m");
    }
}
