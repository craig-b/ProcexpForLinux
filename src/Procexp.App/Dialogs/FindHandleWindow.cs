using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Procexp.App.Controls;
using Procexp.Model;
using SortKey = Procexp.Model.SortKey;

namespace Procexp.App.Dialogs;

/// <summary>
/// Finds which processes have a given file mapped or open — Process Explorer's
/// "Find Handle or DLL".
/// </summary>
/// <remarks>
/// Answers the question the tool is most often reached for: what is holding this
/// file. That means walking every process's mapped files and descriptors, which
/// is expensive enough that it runs on demand and reports progress rather than
/// pretending to be instant.
///
/// Results are necessarily partial for an unprivileged user, since maps and fd
/// are readable only by the owning user — so the window says how many processes
/// it could not look inside rather than implying the search was exhaustive.
/// </remarks>
public sealed class FindHandleWindow : Window
{
    private readonly IProcessDataProvider _provider;
    private readonly Func<ProcessSnapshot> _snapshot;

    private readonly TextBox _query = new()
    {
        PlaceholderText = "Substring to search for",
        MinWidth = 320,
    };
    private readonly Button _search;
    private readonly TextBlock _status = new()
    {
        Margin = new Thickness(0, 8, 0, 0),
        FontSize = 11,
    };
    private readonly DataTableView<Match> _results = new();

    private CancellationTokenSource? _search_cancellation;

    public FindHandleWindow(
        IProcessDataProvider provider,
        Func<ProcessSnapshot> snapshot,
        bool darkMode
    )
    {
        _provider = provider;
        _snapshot = snapshot;

        Title = "Find Handle or Mapped File";
        Width = 900;
        Height = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _results.IsDarkMode = darkMode;
        _results.EmptyMessage = "Enter a substring and press Search.";
        _results.IdentityOf = m => $"{((Match)m).Pid}:{((Match)m).Detail}";
        _results.SetColumns([
            new("Process", 200, m => m.ProcessName),
            new(
                "PID",
                80,
                m => m.Pid.ToString(CultureInfo.InvariantCulture),
                true,
                m => SortKey.Number(m.Pid)
            ),
            new("Type", 120, m => m.Kind),
            new("Detail", 900, m => m.Detail),
        ]);

        _search = new Button
        {
            Content = "Search",
            MinWidth = 90,
            IsDefault = true,
        };
        _search.Click += (_, _) => _ = RunSearchAsync();

        _query.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                _ = RunSearchAsync();
                e.Handled = true;
            }
        };

        BuildLayout();

        Closed += (_, _) => _search_cancellation?.Cancel();
    }

    /// <summary>One process holding something that matched.</summary>
    public sealed record Match(int Pid, string ProcessName, string Kind, string Detail);

    private void BuildLayout()
    {
        var bar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { _query, _search },
        };

        var top = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Margin = new Thickness(16, 16, 16, 8),
            Children = { bar, _status },
        };

        var root = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);
        root.Children.Add(_results);

        Content = root;
    }

    private async Task RunSearchAsync()
    {
        var needle = _query.Text?.Trim();
        if (string.IsNullOrEmpty(needle))
        {
            return;
        }

        _search_cancellation?.Cancel();
        _search_cancellation = new CancellationTokenSource();
        var token = _search_cancellation.Token;

        _search.IsEnabled = false;
        _results.SetRows([]);
        _status.Text = "Searching...";

        var matches = new List<Match>();
        var refused = 0;
        var searched = 0;

        try
        {
            var processes = _snapshot()
                .Processes.Values.Where(p => !p.Flags.HasFlag(ProcessFlags.KernelThread))
                .ToList();

            foreach (var process in processes)
            {
                token.ThrowIfCancellationRequested();

                var permitted = await SearchProcessAsync(process, needle, matches, token)
                    .ConfigureAwait(true);
                if (permitted)
                {
                    searched++;
                }
                else
                {
                    refused++;
                }

                // Report as it goes. A search across several hundred processes
                // takes long enough that a frozen window would look like a hang.
                if (searched % 25 == 0)
                {
                    _status.Text =
                        $"Searched {searched} of {processes.Count} processes, {matches.Count} matches...";
                    _results.SetRows([.. matches]);
                }
            }

            _results.SetRows([.. matches]);

            _status.Text =
                refused > 0
                    ? $"{matches.Count} matches in {searched} processes. "
                        + $"{refused} could not be searched — they belong to other users; "
                        + "installing the privileged helper would include them."
                    : $"{matches.Count} matches in {searched} processes.";

            if (matches.Count == 0)
            {
                _results.EmptyMessage = "No process holds anything matching that.";
            }
        }
        catch (OperationCanceledException)
        {
            _status.Text = "Search cancelled.";
        }
        finally
        {
            _search.IsEnabled = true;
        }
    }

    /// <summary>
    /// Search one process.
    /// </summary>
    /// <returns>False when the process could not be examined at all.</returns>
    private async Task<bool> SearchProcessAsync(
        ProcessRecord process,
        string needle,
        List<Match> matches,
        CancellationToken token
    )
    {
        var permitted = false;

        try
        {
            foreach (
                var module in await _provider.ModulesAsync(process.Id, token).ConfigureAwait(true)
            )
            {
                if (module.Path.Contains(needle, StringComparison.OrdinalIgnoreCase))
                {
                    matches.Add(
                        new Match(process.Id.Pid, process.Name, "Mapped file", module.Path)
                    );
                }
            }

            permitted = true;
        }
        catch (ProviderException)
        {
            // Refused or gone; the descriptor pass may still succeed.
        }

        try
        {
            foreach (
                var descriptor in await _provider
                    .FileDescriptorsAsync(process.Id, token)
                    .ConfigureAwait(true)
            )
            {
                if (descriptor.Name.Contains(needle, StringComparison.OrdinalIgnoreCase))
                {
                    matches.Add(
                        new Match(
                            process.Id.Pid,
                            process.Name,
                            descriptor.Kind.ToString(),
                            $"fd {descriptor.Fd}: {descriptor.Name}"
                        )
                    );
                }
            }

            permitted = true;
        }
        catch (ProviderException)
        {
            // As above.
        }

        return permitted;
    }
}
