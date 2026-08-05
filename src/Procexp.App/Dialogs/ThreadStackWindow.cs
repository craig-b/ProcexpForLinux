using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Procexp.Model;
using Procexp.Privileged;

namespace Procexp.App.Dialogs;

/// <summary>
/// The kernel stack of one thread — the Linux counterpart of Process Explorer's
/// thread stack dialog.
/// </summary>
/// <remarks>
/// Kernel-side frames only. The kernel gates <c>/proc/PID/task/TID/stack</c>
/// behind CAP_SYS_ADMIN — not even the owning user may read it — so every read
/// goes through the privileged helper, and without the helper this window
/// explains that rather than opening empty. User-mode frames would need ptrace
/// and a symbolizer, which is a different feature with different risks.
///
/// The stack is a point-in-time sample and goes stale the moment it is read, so
/// it refreshes only on demand, never on a timer.
/// </remarks>
public sealed class ThreadStackWindow : Window
{
    private readonly ProcessId _id;
    private readonly ThreadInfo _thread;
    private readonly CancellationTokenSource _lifetime = new();

    private readonly SelectableTextBlock _stack = new()
    {
        FontFamily = new FontFamily("monospace"),
        FontSize = 12,
        Margin = new Thickness(12),
    };

    public ThreadStackWindow(ProcessId id, string processName, ThreadInfo thread)
    {
        _id = id;
        _thread = thread;

        Title = $"{processName} (pid {id.Pid}) — tid {thread.Tid} Kernel Stack";
        Width = 560;
        Height = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var caption = new TextBlock
        {
            Text =
                thread.Name.Length > 0
                    ? $"{thread.Name} — {thread.State}"
                        + (thread.WaitChannel is { Length: > 0 } w ? $", waiting in {w}" : "")
                    : thread.State,
            Opacity = 0.75,
            Margin = new Thickness(12, 12, 12, 0),
        };

        var refresh = new Button { Content = "Refresh", MinWidth = 88 };
        refresh.Click += (_, _) => _ = LoadAsync();

        var close = new Button
        {
            Content = "Close",
            MinWidth = 88,
            IsCancel = true,
        };
        close.Click += (_, _) => Close();

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(12),
            Children = { refresh, close },
        };

        var root = new DockPanel();
        DockPanel.SetDock(caption, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(caption);
        root.Children.Add(buttons);
        root.Children.Add(
            new ScrollViewer
            {
                Content = _stack,
                HorizontalScrollBarVisibility = Avalonia
                    .Controls
                    .Primitives
                    .ScrollBarVisibility
                    .Auto,
            }
        );
        Content = root;

        Closed += (_, _) => _lifetime.Cancel();
        Opened += (_, _) => _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        if (!PrivilegedClient.IsAvailable)
        {
            _stack.Text =
                "Kernel stacks require the privileged helper.\n\n"
                + "The kernel restricts /proc/PID/task/TID/stack to CAP_SYS_ADMIN, so not\n"
                + "even your own processes can be read without it. See docs/HELPER.md.";
            return;
        }

        _stack.Text = "Reading...";

        try
        {
            var text = await new PrivilegedClient()
                .ReadThreadKernelStackAsync(_id, (int)_thread.Tid, _lifetime.Token)
                .ConfigureAwait(true);

            _stack.Text =
                text.Trim().Length > 0
                    ? text
                    : "(no kernel stack — the thread was running in user mode when sampled)";
        }
        catch (ProviderException e)
        {
            _stack.Text = e.Message;
        }
        catch (OperationCanceledException)
        {
            // Window closed mid-read.
        }
    }
}
