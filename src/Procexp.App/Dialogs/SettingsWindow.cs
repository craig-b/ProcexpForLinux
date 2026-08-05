using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Procexp.App.Settings;
using Procexp.Model;

namespace Procexp.App.Dialogs;

/// <summary>
/// The Options window: confirmations, highlight duration, and the row-colour
/// legend — the Linux analog of the macOS Settings General and Colors tabs.
/// </summary>
public sealed class SettingsWindow : Window
{
    /// <summary>What the user chose, or null on cancel.</summary>
    public sealed record Outcome(
        bool ConfirmActions,
        double HighlightSeconds,
        IReadOnlyList<ProcessColorRule> ColorRules
    );

    public Outcome? Result { get; private set; }

    private readonly CheckBox _confirm;
    private readonly ComboBox _highlight;
    private List<ProcessColorRule> _rules;
    private readonly StackPanel _rulesPanel = new() { Spacing = 6 };

    private static readonly double[] HighlightChoices = [1, 2, 3, 5];

    // A fixed palette rather than a colour-picker dependency: every package
    // pulled into the app must survive trimming and Native AOT, and eighteen
    // swatches plus a hex box cover what a row legend needs.
    private static readonly string[] Palette =
    [
        "#C6F6C6",
        "#F6C6C6",
        "#F6DCBE",
        "#C8C8C8",
        "#FFD0D0",
        "#D0D0FF",
        "#D0F6F6",
        "#E6D0F6",
        "#FFF3B0",
        "#B0E0FF",
        "#285A28",
        "#6E2828",
        "#694623",
        "#464646",
        "#5A3737",
        "#37375A",
        "#285050",
        "#46325A",
    ];

    public SettingsWindow(
        bool confirmActions,
        double highlightSeconds,
        IReadOnlyList<ProcessColorRule> rules
    )
    {
        Title = "Options";
        Width = 520;
        Height = 560;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _rules = [.. rules];

        _confirm = new CheckBox
        {
            Content = "Confirm before kill, suspend and renice",
            IsChecked = confirmActions,
        };

        _highlight = new ComboBox
        {
            ItemsSource = HighlightChoices.Select(s => $"{s:0} s").ToList(),
            SelectedIndex = Math.Max(0, Array.IndexOf(HighlightChoices, highlightSeconds)),
            MinWidth = 90,
        };

        var general = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 12,
            Children =
            {
                _confirm,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = "Highlight new and deleted for",
                            VerticalAlignment = VerticalAlignment.Center,
                        },
                        _highlight,
                    },
                },
            },
        };

        var restore = new Button { Content = "Restore Defaults" };
        restore.Click += (_, _) =>
        {
            _rules = [.. ProcessColorRule.Defaults];
            RebuildRules();
        };

        var colours = new DockPanel { Margin = new Thickness(16) };
        DockPanel.SetDock(restore, Dock.Bottom);
        restore.HorizontalAlignment = HorizontalAlignment.Left;
        restore.Margin = new Thickness(0, 12, 0, 0);
        colours.Children.Add(restore);
        colours.Children.Add(new ScrollViewer { Content = _rulesPanel });

        RebuildRules();

        var ok = new Button
        {
            Content = "OK",
            MinWidth = 80,
            IsDefault = true,
        };
        ok.Click += (_, _) =>
        {
            Result = new Outcome(
                _confirm.IsChecked == true,
                HighlightChoices[
                    Math.Clamp(_highlight.SelectedIndex, 0, HighlightChoices.Length - 1)
                ],
                _rules
            );
            Close();
        };

        var cancel = new Button
        {
            Content = "Cancel",
            MinWidth = 80,
            IsCancel = true,
        };
        cancel.Click += (_, _) => Close();

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(12),
            Children = { cancel, ok },
        };

        var tabs = new TabControl
        {
            FontSize = 13,
            Items =
            {
                new TabItem { Header = "General", Content = general },
                new TabItem { Header = "Colours", Content = colours },
            },
        };

        var root = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(tabs);
        Content = root;
    }

    private static string RuleName(ProcessFlags flag) =>
        flag switch
        {
            ProcessFlags.NewProcess => "New process",
            ProcessFlags.DeadProcess => "Deleted process",
            ProcessFlags.Zombie => "Zombie",
            ProcessFlags.Suspended => "Suspended",
            ProcessFlags.Service => "Service",
            ProcessFlags.OwnProcess => "Own process",
            ProcessFlags.Sandboxed => "Sandboxed",
            ProcessFlags.Packed => "Packed image",
            _ => flag.ToString(),
        };

    private void RebuildRules()
    {
        _rulesPanel.Children.Clear();

        for (var i = 0; i < _rules.Count; i++)
        {
            var index = i;
            var rule = _rules[index];

            var enabled = new CheckBox
            {
                IsChecked = rule.IsEnabled,
                VerticalAlignment = VerticalAlignment.Center,
            };
            enabled.IsCheckedChanged += (_, _) =>
                _rules[index] = _rules[index] with { IsEnabled = enabled.IsChecked == true };

            var row = new DockPanel();
            var swatches = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children =
                {
                    Swatch("light", index, dark: false),
                    Swatch("dark", index, dark: true),
                },
            };
            DockPanel.SetDock(swatches, Dock.Right);

            row.Children.Add(swatches);
            row.Children.Add(enabled);
            row.Children.Add(
                new TextBlock
                {
                    Text = RuleName(rule.Flag),
                    Margin = new Thickness(8, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                }
            );

            _rulesPanel.Children.Add(row);
        }
    }

    /// <summary>A clickable colour well opening a palette-and-hex flyout.</summary>
    private Button Swatch(string tip, int index, bool dark)
    {
        var current = dark ? _rules[index].BackgroundDark : _rules[index].BackgroundLight;

        var button = new Button
        {
            Width = 44,
            Height = 24,
            Padding = new Thickness(0),
            Background = ToBrush(current),
            BorderBrush = Brushes.Gray,
            BorderThickness = new Thickness(1),
        };
        ToolTip.SetTip(button, $"{tip} theme colour");

        // Declared before the palette loop: its click handlers call Apply,
        // which writes the hex box, and definite assignment is lexical.
        var hexBox = new TextBox { Text = ColorRuleSetting.FormatHex(current), MinWidth = 90 };

        var grid = new WrapPanel { MaxWidth = 200 };
        foreach (var hex in Palette)
        {
            var choice = ColorRuleSetting.ParseHex(hex)!.Value;
            var pick = new Button
            {
                Width = 22,
                Height = 22,
                Margin = new Thickness(2),
                Padding = new Thickness(0),
                Background = ToBrush(choice),
            };
            pick.Click += (_, _) => Apply(choice);
            grid.Children.Add(pick);
        }
        hexBox.KeyUp += (_, _) =>
        {
            if (ColorRuleSetting.ParseHex(hexBox.Text ?? "") is { } parsed)
            {
                Apply(parsed);
            }
        };

        button.Flyout = new Flyout
        {
            Content = new StackPanel { Spacing = 8, Children = { grid, hexBox } },
        };

        return button;

        void Apply(Rgba colour)
        {
            _rules[index] = dark
                ? _rules[index] with
                {
                    BackgroundDark = colour,
                }
                : _rules[index] with
                {
                    BackgroundLight = colour,
                };
            button.Background = ToBrush(colour);
            hexBox.Text = ColorRuleSetting.FormatHex(colour);
        }
    }

    private static SolidColorBrush ToBrush(Rgba c) =>
        new(
            Color.FromArgb(
                (byte)Math.Round(c.A * 255),
                (byte)Math.Round(c.R * 255),
                (byte)Math.Round(c.G * 255),
                (byte)Math.Round(c.B * 255)
            )
        );
}
