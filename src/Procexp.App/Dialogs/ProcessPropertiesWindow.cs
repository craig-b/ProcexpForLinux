using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Procexp.App.Controls;
using Procexp.Model;
using Procexp.Net;
using Procexp.Provenance;
using SortKey = Procexp.Model.SortKey;

namespace Procexp.App.Dialogs;

/// <summary>
/// Per-process detail: image, performance, threads, TCP/IP, provenance,
/// environment and strings.
/// </summary>
/// <remarks>
/// Ports the macOS Properties window. The tabs load lazily and refresh only while
/// visible — the Strings tab in particular reads and scans the whole executable,
/// which is far too expensive to do on a timer for a tab nobody is looking at.
/// </remarks>
public sealed class ProcessPropertiesWindow : Window
{
    private readonly IProcessDataProvider _provider;
    private readonly NetworkProvider _network = new();
    private readonly ProvenanceProvider _provenance = new();
    private readonly ProcessId _id;
    private readonly CancellationTokenSource _lifetime = new();

    private readonly DetailList _image = new();
    private readonly DetailList _performance = new();
    private readonly DetailList _provenanceDetail = new();
    private readonly DataTableView<ThreadInfo> _threads = new();
    private readonly DataTableView<SocketInfo> _sockets = new();
    private readonly DataTableView<EnvironmentEntry> _environment = new();
    private readonly DataTableView<StringEntry> _strings = new();

    private readonly HistoryGraphView _cpuGraph = new()
    {
        Title = "CPU",
        FixedMaximum = 100,
        Height = 90,
        FormatValue = v => string.Create(CultureInfo.InvariantCulture, $"{v:F1}%"),
    };

    private readonly HistoryGraphView _memoryGraph = new()
    {
        Title = "Working Set",
        Height = 90,
        FormatValue = v => (ulong)Math.Max(0, v) is var b && b == 0 ? "0 B" : ValueFormat.Bytes(b),
    };

    private TabControl _tabs = null!;
    private ProcessRecord _record;
    private bool _stringsLoaded;
    private bool _provenanceLoaded;

    public ProcessPropertiesWindow(IProcessDataProvider provider, ProcessRecord record, bool darkMode)
    {
        _provider = provider;
        _record = record;
        _id = record.Id;

        Title = $"{record.Name} (pid {record.Id.Pid}) Properties";
        Width = 900;
        Height = 640;

        _cpuGraph.IsDarkMode = darkMode;
        _memoryGraph.IsDarkMode = darkMode;

        foreach (var table in new VirtualTableBase[] { _threads, _sockets, _environment, _strings })
        {
            table.IsDarkMode = darkMode;
        }

        ConfigureTables();
        BuildLayout();

        PopulateImage();
        PopulatePerformance();

        Opened += (_, _) => _ = RunRefreshLoopAsync();
        Closed += (_, _) => _lifetime.Cancel();
    }

    /// <summary>A string from the image, numbered so the order is stable when sorted.</summary>
    public sealed record StringEntry(int Index, string Value);

    /// <summary>One environment variable. A record rather than KeyValuePair
    /// because the table needs a reference type to track selection identity.</summary>
    public sealed record EnvironmentEntry(string Key, string Value);

    private void BuildLayout()
    {
        var performancePanel = new DockPanel();

        var graphs = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Vertical,
            Margin = new Thickness(12, 12, 12, 0),
            Spacing = 8,
            Children = { _cpuGraph, _memoryGraph },
        };

        DockPanel.SetDock(graphs, Dock.Top);
        performancePanel.Children.Add(graphs);
        performancePanel.Children.Add(_performance);

        _tabs = new TabControl
        {
            // The default tab typography wraps seven tabs onto two rows at any
            // sensible window width.
            FontSize = 13,
            Items =
            {
                new TabItem { Header = "Image", Content = _image },
                new TabItem { Header = "Performance", Content = performancePanel },
                new TabItem { Header = "Threads", Content = _threads },
                new TabItem { Header = "TCP/IP", Content = _sockets },
                new TabItem { Header = "Provenance", Content = _provenanceDetail },
                new TabItem { Header = "Environment", Content = _environment },
                new TabItem { Header = "Strings", Content = _strings },
            },
        };

        _tabs.SelectionChanged += (_, _) => _ = RefreshActiveTabAsync();

        var close = new Button
        {
            Content = "Close",
            MinWidth = 88,
            IsCancel = true,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            Margin = new Thickness(12, 8, 12, 12),
        };

        close.Click += (_, _) => Close();

        var root = new DockPanel();
        DockPanel.SetDock(close, Dock.Bottom);
        root.Children.Add(close);
        root.Children.Add(_tabs);

        Content = root;
    }

    // ---- Tab configuration --------------------------------------------------

    private void ConfigureTables()
    {
        _threads.EmptyMessage = "No threads.";
        _threads.IdentityOf = t => ((ThreadInfo)t).Tid;
        _threads.SetColumns(
        [
            new("TID", 90, t => t.Tid.ToString(CultureInfo.InvariantCulture), true, t => SortKey.Number(t.Tid)),
            new("Name", 150, t => t.Name),
            new("State", 110, t => t.State),
            new("CPU Time", 100, t => ValueFormat.Duration(t.CpuTime), true, t => SortKey.Number(t.CpuTime)),
            new("User Time", 100, t => ValueFormat.Duration(t.UserTime), true, t => SortKey.Number(t.UserTime)),
            new("Kernel Time", 100, t => ValueFormat.Duration(t.KernelTime), true, t => SortKey.Number(t.KernelTime)),
            new("Wait Reason", 160, t => t.WaitChannel ?? ""),
            new("Priority", 76, t => t.Priority.ToString(CultureInfo.InvariantCulture), true, t => SortKey.Number(t.Priority)),
        ]);

        _sockets.EmptyMessage = "No sockets open.";
        _sockets.IdentityOf = s => ((SocketInfo)s).Fd;
        _sockets.SetColumns(
        [
            new("Protocol", 80, s => s.Protocol.ToString()),
            new("Local Address", 190, s => Endpoint(s.LocalAddress, s.LocalPort)),
            new("Remote Address", 190, s => s.RemoteHostName ?? Endpoint(s.RemoteAddress, s.RemotePort)),
            new("State", 120, s => s.State),
            new("FD", 56, s => s.Fd.ToString(CultureInfo.InvariantCulture), true, s => SortKey.Number(s.Fd)),
        ]);

        _environment.EmptyMessage = "Environment not readable for this process.";
        _environment.IdentityOf = e => ((EnvironmentEntry)e).Key;
        _environment.SetColumns(
        [
            new("Variable", 240, e => e.Key),
            new("Value", 900, e => e.Value),
        ]);

        _strings.EmptyMessage = "No printable strings found.";
        _strings.IdentityOf = s => ((StringEntry)s).Index;
        _strings.SetColumns(
        [
            new("#", 70, s => s.Index.ToString(CultureInfo.InvariantCulture), true, s => SortKey.Number(s.Index)),
            new("String", 1100, s => s.Value),
        ]);

        static string Endpoint(string address, ushort port) =>
            address.Length == 0 ? "" : $"{address}:{port}";
    }

    // ---- Population ---------------------------------------------------------

    private void PopulateImage()
    {
        _image.Clear();

        _image.AddSection("Image");
        _image.Add("Name", _record.Name);
        // A null path is not "no path": readlink on /proc/PID/exe is gated by
        // ptrace_may_access, so it comes back empty for other users' processes.
        // Showing a blank field there implies the process has no image.
        _image.Add(
            "Path",
            _record.ExecutablePath
                ?? (_record.Flags.HasFlag(ProcessFlags.KernelThread)
                    ? "(kernel thread — no image)"
                    : "(not readable — owned by another user)"),
            showWhenEmpty: true);
        _image.Add("Command line", _record.CommandLine);
        _image.Add("Kind", _record.ImageKind.ToString());
        _image.Add("Description", _record.Description);
        _image.Add("Company", _record.Company);
        _image.Add("Version", _record.Version);

        _image.AddSection("Process");
        _image.Add("PID", _record.Id.Pid.ToString(CultureInfo.InvariantCulture));
        _image.Add("Parent", _record.Parent is { } p ? p.Pid.ToString(CultureInfo.InvariantCulture) : "(none)");
        _image.Add("Started", ValueFormat.DateTime(_record.StartTime));
        _image.Add("User", _record.UserName ?? _record.Uid.ToString(CultureInfo.InvariantCulture));
        _image.Add("Session", _record.SessionTty);
        _image.Add("State", ValueFormat.ProcessState(_record.State));

        _image.AddSection("Confinement");
        _image.Add("Cgroup", _record.CgroupPath);
        _image.Add("systemd unit", _record.SystemdUnit);
        _image.Add("Security label", _record.SecurityLabel);
        _image.Add("Autostart", _record.AutostartLocation);
    }

    private void PopulatePerformance()
    {
        _performance.Clear();

        _performance.AddSection("CPU");
        _performance.Add("Usage", string.Create(CultureInfo.InvariantCulture, $"{_record.CpuPercent:F2}%"));
        _performance.Add("Total time", ValueFormat.Duration(_record.CpuTime));
        _performance.Add("User time", ValueFormat.Duration(_record.UserTime));
        _performance.Add("Kernel time", ValueFormat.Duration(_record.SystemTime));
        _performance.Add("Threads", _record.ThreadCount.ToString(CultureInfo.InvariantCulture));
        _performance.Add("Priority", _record.Priority.ToString(CultureInfo.InvariantCulture));
        _performance.Add("Nice", _record.Nice.ToString(CultureInfo.InvariantCulture));
        _performance.Add("Policy", ValueFormat.SchedulingPolicy(_record.SchedulingPolicy));
        _performance.Add("Context switches", ValueFormat.Integer(_record.VoluntaryContextSwitches));
        _performance.Add("Involuntary switches", ValueFormat.Integer(_record.InvoluntaryContextSwitches));

        _performance.AddSection("Memory");
        _performance.Add("Working set", ValueFormat.Bytes(_record.ResidentSize));
        _performance.Add("Virtual size", ValueFormat.Bytes(_record.VirtualSize));

        // PSS is the honest Private Bytes analog but is owner-restricted and
        // expensive, so it is frequently absent — say so rather than showing zero.
        _performance.Add(
            "Private bytes (PSS)",
            _record.ProportionalSetSize is { } pss ? ValueFormat.Bytes(pss) : "(not available)",
            showWhenEmpty: true);

        _performance.Add("Shared", ValueFormat.Bytes(_record.SharedSize));
        _performance.Add("Swap", ValueFormat.Bytes(_record.SwapSize));
        _performance.Add("Minor faults", ValueFormat.Integer(_record.MinorFaults));
        _performance.Add("Major faults", ValueFormat.Integer(_record.MajorFaults));

        _performance.AddSection("I/O");
        _performance.Add(
            "Read",
            _record.DiskBytesRead is { } read ? ValueFormat.Bytes(read) : "(not available)",
            showWhenEmpty: true);
        _performance.Add(
            "Written",
            _record.DiskBytesWritten is { } written ? ValueFormat.Bytes(written) : "(not available)",
            showWhenEmpty: true);
        _performance.Add("Open descriptors", ValueFormat.Integer(_record.FileDescriptorCount));

        _performance.AddSection("Kernel");
        _performance.Add("OOM score", ValueFormat.Integer(_record.OomScore));
        _performance.Add("OOM adjustment", ValueFormat.Integer(_record.OomScoreAdj));
        _performance.Add("Last CPU", ValueFormat.Integer(_record.LastCpu));
    }

    // ---- Refresh ------------------------------------------------------------

    private async Task RunRefreshLoopAsync()
    {
        _cpuGraph.AddSeries(Color.FromRgb(90, 200, 90));
        _memoryGraph.AddSeries(Color.FromRgb(200, 150, 60));

        await RefreshActiveTabAsync().ConfigureAwait(true);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));

        try
        {
            while (await timer.WaitForNextTickAsync(_lifetime.Token).ConfigureAwait(true))
            {
                await RefreshAsync().ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException)
        {
            // Window closed.
        }
    }

    private async Task RefreshAsync()
    {
        var snapshot = await _provider.SnapshotAsync(_lifetime.Token).ConfigureAwait(true);

        if (snapshot.Processes.TryGetValue(_id, out var updated))
        {
            _record = updated;
            _cpuGraph.Append(updated.CpuPercent);
            _memoryGraph.Append(updated.ResidentSize);
        }
        else
        {
            // The process exited. Keep the last known values on screen rather than
            // blanking the window, but stop claiming they are current.
            Title = $"{_record.Name} (pid {_id.Pid}) Properties — exited";
            _lifetime.Cancel();
            return;
        }

        PopulateImage();
        PopulatePerformance();

        await RefreshActiveTabAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// Reload only the visible tab.
    /// </summary>
    /// <remarks>
    /// Threads and sockets are cheap enough to re-read each second. Strings and
    /// provenance are not — one scans the whole image, the other hashes it and may
    /// shell out to the package manager — so they load once and stay.
    /// </remarks>
    private async Task RefreshActiveTabAsync()
    {
        var header = (_tabs.SelectedItem as TabItem)?.Header as string;

        try
        {
            switch (header)
            {
                case "Threads":
                    _threads.SetRows(await _provider.ThreadsAsync(_id, _lifetime.Token).ConfigureAwait(true));
                    break;

                case "TCP/IP":
                    _sockets.SetRows(await _network.SocketsAsync(_id, _lifetime.Token).ConfigureAwait(true));
                    _ = ResolveHostNamesAsync();
                    break;

                case "Environment":
                    var environment = await _provider.EnvironmentAsync(_id, _lifetime.Token).ConfigureAwait(true);
                    _environment.SetRows(
                        [.. environment
                            .OrderBy(e => e.Key, StringComparer.Ordinal)
                            .Select(e => new EnvironmentEntry(e.Key, e.Value))]);
                    break;

                case "Strings" when !_stringsLoaded:
                    _stringsLoaded = true;
                    var strings = await _provider.StringsAsync(_id, _lifetime.Token).ConfigureAwait(true);
                    _strings.SetRows([.. strings.Select((s, i) => new StringEntry(i, s))]);
                    break;

                case "Provenance" when !_provenanceLoaded:
                    _provenanceLoaded = true;
                    await LoadProvenanceAsync().ConfigureAwait(true);
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            // Window closing.
        }
        catch (ProviderException e)
        {
            ReportTabFailure(header, e);
        }
    }

    private void ReportTabFailure(string? header, ProviderException e)
    {
        var message = e.Kind == ProviderErrorKind.NotPermitted
            ? "Not permitted — readable only by the process owner. Installing the privileged helper would allow this."
            : e.Message;

        switch (header)
        {
            case "Threads":
                _threads.EmptyMessage = message;
                _threads.SetRows([]);
                break;
            case "Environment":
                _environment.EmptyMessage = message;
                _environment.SetRows([]);
                break;
            case "Strings":
                _strings.EmptyMessage = message;
                _strings.SetRows([]);
                break;
        }
    }

    /// <summary>
    /// Resolve remote addresses to names in the background.
    /// </summary>
    /// <remarks>
    /// Off the refresh path deliberately: a single unresolvable address costs a
    /// full DNS timeout, and doing that inline would stall the tab for seconds.
    /// </remarks>
    private async Task ResolveHostNamesAsync()
    {
        var rows = _sockets.Rows.ToList();
        var resolved = new List<SocketInfo>(rows.Count);

        foreach (var socket in rows)
        {
            if (socket.RemoteAddress.Length == 0 || socket.RemotePort == 0)
            {
                resolved.Add(socket);
                continue;
            }

            var name = await _network.ResolveHostNameAsync(socket.RemoteAddress, _lifetime.Token)
                .ConfigureAwait(true);

            resolved.Add(name == socket.RemoteAddress
                ? socket
                : socket with { RemoteHostName = $"{name}:{socket.RemotePort}" });
        }

        if (!_lifetime.IsCancellationRequested)
        {
            await Dispatcher.UIThread.InvokeAsync(() => _sockets.SetRows(resolved));
        }
    }

    private async Task LoadProvenanceAsync()
    {
        _provenanceDetail.Clear();

        if (_record.ExecutablePath is not { Length: > 0 } path)
        {
            _provenanceDetail.AddSection("Provenance");
            _provenanceDetail.Add("Status", "No image on disk (kernel thread).", showWhenEmpty: true);
            return;
        }

        _provenanceDetail.AddSection("Provenance");
        _provenanceDetail.Add("Status", "Checking...", showWhenEmpty: true);

        var info = await _provenance.DeepProvenanceAsync(path, _lifetime.Token).ConfigureAwait(true);

        _provenanceDetail.Clear();
        _provenanceDetail.AddSection("Package");
        _provenanceDetail.Add("Status", DescribeStatus(info.Status), showWhenEmpty: true);
        _provenanceDetail.Add("Package", info.PackageName);
        _provenanceDetail.Add("Version", info.PackageVersion);
        _provenanceDetail.Add("Repository", info.Repository);
        _provenanceDetail.Add("Packager", info.Packager);
        _provenanceDetail.Add("Bundle", info.BundleId);

        _provenanceDetail.AddSection("Image");
        _provenanceDetail.Add("Path", path);
        _provenanceDetail.Add("Build ID", info.BuildId);
        _provenanceDetail.Add("SHA-256", info.Sha256);
        _provenanceDetail.Add("IMA signature", info.HasImaSignature ? "present" : null);

        if (info.VerificationError is { } error)
        {
            _provenanceDetail.Add("Note", error);
        }

        if (info.VirusTotal is { } vt)
        {
            _provenanceDetail.AddSection("VirusTotal");
            _provenanceDetail.Add("Detections", $"{vt.Positives} of {vt.Total}");
            _provenanceDetail.Add("Checked", ValueFormat.DateTime(vt.CheckedAt));
            _provenanceDetail.Add("Report", vt.Permalink);
        }
    }

    private static string DescribeStatus(ProvenanceStatus status) => status switch
    {
        ProvenanceStatus.PackageVerified => "Shipped by the distribution, and unmodified on disk.",
        ProvenanceStatus.PackageModified => "Owned by a package, but the file on disk no longer matches it.",
        ProvenanceStatus.Unpackaged => "Not owned by any package — built locally, downloaded, or installed by hand.",
        ProvenanceStatus.SandboxedBundle => "Shipped inside a Flatpak or Snap, which carries its own signing.",
        ProvenanceStatus.Unknown => "Could not be determined.",
        _ => status.ToString(),
    };
}
