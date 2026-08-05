using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;

namespace Procexp.App.Dialogs;

/// <summary>
/// File ▸ Run: launch a command detached from the app — the Linux analog of
/// the macOS RunProcessSheet.
/// </summary>
/// <remarks>
/// The command runs through <c>sh -c</c>, so quoting, globbing, $HOME and
/// pipelines behave the way the user expects from a terminal, rather than
/// inventing a private argument-splitting dialect.
/// </remarks>
public sealed class RunDialog : Window
{
    private readonly TextBox _command = new() { PlaceholderText = "Command line", MinWidth = 380 };
    private readonly TextBlock _error = new()
    {
        Foreground = Avalonia.Media.Brushes.IndianRed,
        FontSize = 12,
        IsVisible = false,
    };

    public RunDialog()
    {
        Title = "Run";
        SizeToContent = SizeToContent.WidthAndHeight;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var browse = new Button { Content = "Browse…" };
        browse.Click += (_, _) => _ = BrowseAsync();

        var run = new Button
        {
            Content = "Run",
            MinWidth = 80,
            IsDefault = true,
        };
        run.Click += (_, _) => Launch();

        var cancel = new Button
        {
            Content = "Cancel",
            MinWidth = 80,
            IsCancel = true,
        };
        cancel.Click += (_, _) => Close();

        _command.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Launch();
                e.Handled = true;
            }
        };

        Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 10,
            Children =
            {
                new TextBlock { Text = "Run a command, detached from Process Explorer:" },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children = { _command, browse },
                },
                _error,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { cancel, run },
                },
            },
        };

        Opened += (_, _) => _command.Focus();
    }

    private async Task BrowseAsync()
    {
        var files = await StorageProvider
            .OpenFilePickerAsync(new() { Title = "Choose Program" })
            .ConfigureAwait(true);

        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path)
        {
            // Quoted so a path with spaces survives the shell.
            _command.Text = $"\"{path}\"";
            _command.CaretIndex = _command.Text.Length;
        }
    }

    private void Launch()
    {
        var command = _command.Text?.Trim();
        if (string.IsNullOrEmpty(command))
        {
            return;
        }

        try
        {
            Process.Start(
                new ProcessStartInfo("/bin/sh")
                {
                    ArgumentList = { "-c", command },
                    UseShellExecute = false,
                }
            );
            Close();
        }
        catch (Exception e)
            when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _error.Text = $"Could not run: {e.Message}";
            _error.IsVisible = true;
        }
    }
}
