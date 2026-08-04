using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Procexp.Actions;

namespace Procexp.App.Dialogs;

/// <summary>
/// Confirmation and message dialogs.
/// </summary>
/// <remarks>
/// Built by hand because Avalonia ships no message box. That is no loss here:
/// the severity from <see cref="ActionConfirmationPolicy"/> has to drive the
/// presentation — a refused action must not offer a confirm button at all, and a
/// critical one should not have its confirm button as the comfortable default.
/// </remarks>
public static class ConfirmationDialog
{
    /// <summary>
    /// Present a confirmation and wait for the answer.
    /// </summary>
    /// <returns>True when the user chose to proceed.</returns>
    public static async Task<bool> ShowAsync(Window owner, ActionConfirmation confirmation)
    {
        var result = false;

        var message = new TextBlock
        {
            Text = confirmation.Message,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 460,
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
        };

        var dialog = new Window
        {
            Title = confirmation.Title,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            ShowInTaskbar = false,
        };

        if (confirmation.IsRefused)
        {
            // Nothing to confirm — the action is not on offer. Only a way out.
            var close = new Button
            {
                Content = "Close",
                MinWidth = 88,
                IsDefault = true,
            };
            close.Click += (_, _) => dialog.Close();
            buttons.Children.Add(close);
        }
        else
        {
            var cancel = new Button
            {
                Content = "Cancel",
                MinWidth = 88,
                IsCancel = true,
            };
            cancel.Click += (_, _) => dialog.Close();

            var confirm = new Button
            {
                Content = confirmation.Title,
                MinWidth = 88,

                // Only routine actions get Enter as a shortcut. Making a critical
                // action the default invites confirming it by reflex.
                IsDefault = confirmation.Severity == ConfirmationSeverity.Routine,
            };

            if (confirmation.Severity == ConfirmationSeverity.Critical)
            {
                confirm.Foreground = Brushes.White;
                confirm.Background = new SolidColorBrush(Color.FromRgb(176, 42, 42));
            }

            confirm.Click += (_, _) =>
            {
                result = true;
                dialog.Close();
            };

            // Cancel first, so the destructive button is not under the cursor's
            // resting position after the dialog appears.
            buttons.Children.Add(cancel);
            buttons.Children.Add(confirm);
        }

        dialog.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 16,
            Children =
            {
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 12,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = confirmation.Severity switch
                            {
                                ConfirmationSeverity.Critical => "⛔",
                                ConfirmationSeverity.Disruptive => "⚠",
                                _ => "❓",
                            },
                            FontSize = 24,
                            VerticalAlignment = VerticalAlignment.Top,
                        },
                        message,
                    },
                },
                buttons,
            },
        };

        await dialog.ShowDialog(owner);
        return result;
    }

    /// <summary>Report something that already happened, usually a failure.</summary>
    public static async Task ShowMessageAsync(Window owner, string title, string message)
    {
        var dialog = new Window
        {
            Title = title,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            ShowInTaskbar = false,
        };

        var close = new Button
        {
            Content = "Close",
            MinWidth = 88,
            IsDefault = true,
            IsCancel = true,
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        close.Click += (_, _) => dialog.Close();

        dialog.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 16,
            Children =
            {
                new TextBlock
                {
                    Text = message,
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 460,
                },
                close,
            },
        };

        await dialog.ShowDialog(owner);
    }
}
