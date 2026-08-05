using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Procexp.App.Controls;
using Procexp.Model;
using Procexp.Provenance;

namespace Procexp.App.Dialogs;

/// <summary>
/// Everything known about one mapped file or one descriptor — the Linux analog
/// of the macOS module and handle detail windows.
/// </summary>
/// <remarks>
/// A single window for both, because the two differ only in which rows they
/// fill in: a lower-pane row is a small thing, and two near-identical windows
/// would drift apart the first time either gained a field.
///
/// Provenance for a mapped file is fetched when the window opens rather than
/// with the row. The pane lists hundreds of modules for every selection
/// change; hashing each one to fill a field nobody opened would cost more than
/// the process sweep itself.
/// </remarks>
public sealed class DetailWindow : Window
{
    private readonly DetailList _details = new();
    private readonly ProvenanceProvider _provenance = new();
    private readonly CancellationTokenSource _lifetime = new();

    /// <summary>
    /// The fixed rows, replayed whenever the list is rebuilt — the list can
    /// only append, so an answer arriving later means redrawing from the top.
    /// </summary>
    private Action<DetailList> _fixedRows = _ => { };

    private DetailWindow(string title)
    {
        Title = title;
        Width = 620;
        Height = 460;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var close = new Button
        {
            Content = "Close",
            MinWidth = 88,
            IsCancel = true,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(12),
        };
        close.Click += (_, _) => Close();

        var root = new DockPanel();
        DockPanel.SetDock(close, Dock.Bottom);
        root.Children.Add(close);
        root.Children.Add(_details);
        Content = root;

        Closed += (_, _) => _lifetime.Cancel();
    }

    /// <summary>Detail for a mapped file, with its package provenance.</summary>
    public static DetailWindow ForModule(ModuleInfo module)
    {
        var window = new DetailWindow($"{module.Name} — Mapped File");

        window._fixedRows = list =>
        {
            list.AddSection("Mapped file");
            list.Add("Name", module.Name);
            list.Add("Path", module.Path);
            list.Add("Kind", module.IsSharedLibrary ? "shared library" : "main executable");
            list.Add("Load address", $"0x{module.LoadAddress:x}");
            list.Add("Mapped size", ValueFormat.Bytes(module.Size));
            list.Add("Permissions", module.Permissions);
            list.Add("Description", module.Description);
            list.Add("Company", module.Company);
            list.Add("Version", module.Version);
        };

        window.Rebuild(provenance: null, "Checking...");
        _ = window.LoadProvenanceAsync(module.Path);

        return window;
    }

    /// <summary>Detail for one descriptor, including its socket when it is one.</summary>
    public static DetailWindow ForDescriptor(FileDescriptorInfo descriptor)
    {
        var window = new DetailWindow($"fd {descriptor.Fd} — {descriptor.Kind}");

        window._fixedRows = list =>
        {
            list.AddSection("Descriptor");
            list.Add("Number", descriptor.Fd.ToString());
            list.Add("Kind", descriptor.Kind.ToString());
            list.Add("Name", descriptor.Name);
            list.Add("Access", descriptor.Access);
            list.Add(
                "Open flags",
                descriptor.OpenFlags is { } flags ? $"0o{Convert.ToString(flags, 8)}" : null
            );
            list.Add("Offset", descriptor.Offset?.ToString());
            list.Add(
                "Size",
                descriptor.Size is { } size and >= 0 ? ValueFormat.Bytes((ulong)size) : null
            );
            list.Add("Inode", descriptor.Inode?.ToString());
            list.Add("Device", descriptor.Device);

            if (descriptor.Socket is { } socket)
            {
                list.AddSection("Socket");
                list.Add("Protocol", socket.Protocol.ToString());
                list.Add("State", socket.State);
                list.Add(
                    "Local",
                    socket.LocalAddress.Length > 0
                        ? $"{socket.LocalAddress}:{socket.LocalPort}"
                        : null
                );
                list.Add(
                    "Remote",
                    socket.RemoteAddress.Length > 0
                        ? $"{socket.RemoteAddress}:{socket.RemotePort}"
                        : null
                );
            }
        };

        window.Rebuild(provenance: null, status: null);
        return window;
    }

    private async Task LoadProvenanceAsync(string path)
    {
        try
        {
            var info = await _provenance
                .DeepProvenanceAsync(path, _lifetime.Token)
                .ConfigureAwait(true);

            Rebuild(info, status: null);
        }
        catch (OperationCanceledException)
        {
            // Window closed while the hash was still running.
        }
        catch (ProviderException)
        {
            Rebuild(provenance: null, "Could not be determined.");
        }
    }

    /// <summary>
    /// Redraw: the fixed rows, then provenance — either the answer, or the
    /// placeholder text while one is on its way.
    /// </summary>
    private void Rebuild(ProvenanceInfo? provenance, string? status)
    {
        _details.Clear();
        _fixedRows(_details);

        if (provenance is null && status is null)
        {
            return;
        }

        _details.AddSection("Provenance");

        if (provenance is null)
        {
            _details.Add("Status", status, showWhenEmpty: true);
            return;
        }

        _details.Add("Status", ProvenanceText.Describe(provenance.Status), showWhenEmpty: true);
        _details.Add("Package", provenance.PackageName);
        _details.Add("Version", provenance.PackageVersion);
        _details.Add("Repository", provenance.Repository);
        _details.Add("Packager", provenance.Packager);
        _details.Add("Build ID", provenance.BuildId);
        _details.Add("SHA-256", provenance.Sha256);
        _details.Add("IMA signature", provenance.HasImaSignature ? "present" : null);

        if (provenance.VerificationError is { } error)
        {
            _details.Add("Note", error);
        }
    }
}
