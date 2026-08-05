using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Procexp.Model;

namespace Procexp.App.Dialogs;

/// <summary>
/// Pick which CPUs a process may run on — the counterpart of Process Explorer's
/// Set Affinity dialog.
/// </summary>
/// <remarks>
/// One checkbox per logical CPU, pre-checked from the process's current mask so
/// opening and confirming is a no-op. The OK button rather than the checkboxes
/// enforces the at-least-one rule: a mask with no CPUs is not a pause button,
/// it is an error the kernel would reject anyway.
/// </remarks>
public sealed class AffinityDialog : Window
{
    private readonly List<CheckBox> _boxes = [];
    private readonly Button _ok = new()
    {
        Content = "OK",
        MinWidth = 88,
        IsDefault = true,
    };

    private AffinityDialog(ProcessRecord process, IReadOnlyList<int> current)
    {
        Title = $"{process.Name} (pid {process.Id.Pid}) — CPU Affinity";
        SizeToContent = SizeToContent.WidthAndHeight;
        MaxWidth = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = false;

        var caption = new TextBlock
        {
            Text =
                "The process and all its threads will be allowed to run only on the checked CPUs.",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Opacity = 0.75,
            Margin = new Thickness(12, 12, 12, 4),
        };

        var grid = new WrapPanel { Margin = new Thickness(12, 4), ItemWidth = 96 };
        var checkedSet = new HashSet<int>(current);
        for (var cpu = 0; cpu < Environment.ProcessorCount; cpu++)
        {
            var box = new CheckBox
            {
                Content = $"CPU {cpu}",
                IsChecked = checkedSet.Contains(cpu),
                Margin = new Thickness(0, 0, 8, 0),
            };
            box.IsCheckedChanged += (_, _) => _ok.IsEnabled = _boxes.Any(b => b.IsChecked == true);
            _boxes.Add(box);
            grid.Children.Add(box);
        }

        var all = new Button { Content = "All" };
        all.Click += (_, _) => SetAll(true);
        var none = new Button { Content = "None" };
        none.Click += (_, _) => SetAll(false);

        _ok.Click += (_, _) =>
            Close(
                (IReadOnlyList<int>?)
                    _boxes
                        .Select((box, cpu) => (box, cpu))
                        .Where(x => x.box.IsChecked == true)
                        .Select(x => x.cpu)
                        .ToList()
            );

        var cancel = new Button
        {
            Content = "Cancel",
            MinWidth = 88,
            IsCancel = true,
        };
        cancel.Click += (_, _) => Close(null);

        var buttons = new DockPanel { Margin = new Thickness(12) };
        var selectors = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { all, none },
        };
        var confirmers = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Children = { _ok, cancel },
        };
        DockPanel.SetDock(selectors, Dock.Left);
        buttons.Children.Add(selectors);
        buttons.Children.Add(confirmers);

        Content = new StackPanel { Children = { caption, grid, buttons } };
    }

    private void SetAll(bool value)
    {
        foreach (var box in _boxes)
        {
            box.IsChecked = value;
        }
    }

    /// <summary>The chosen CPU list, or null when cancelled.</summary>
    public static Task<IReadOnlyList<int>?> ShowAsync(
        Window owner,
        ProcessRecord process,
        IReadOnlyList<int> current
    ) => new AffinityDialog(process, current).ShowDialog<IReadOnlyList<int>?>(owner);
}
