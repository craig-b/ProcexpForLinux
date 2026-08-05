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
/// inventing a private argument-splitting dialect. That is the feature, not a
/// hole: the text is typed by the user, into their own session, and runs with
/// exactly the privileges they already have — this dialog grants nothing a
/// terminal would not.
///
/// The care is needed where text the user did <em>not</em> author reaches that
/// shell, which here is only the Browse path — see <see cref="ShellQuote"/>.
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

        if (files.Count > 0 && files[0].Path is { IsAbsoluteUri: true, Scheme: "file" } uri)
        {
            _command.Text = ShellQuote(uri.LocalPath);
            _command.CaretIndex = _command.Text.Length;
        }
    }

    /// <summary>
    /// Wrap a path so the shell sees exactly these bytes.
    /// </summary>
    /// <remarks>
    /// Single quotes rather than double: inside double quotes the shell still
    /// expands <c>$(…)</c>, backticks and <c>$VAR</c>, so a file named
    /// <c>$(rm -rf ~).sh</c> — a name the user did not choose, and one an
    /// attacker can plant in a download directory — would execute on Browse
    /// rather than merely being named. Inside single quotes nothing expands;
    /// the only character needing care is the single quote itself, closed and
    /// re-opened around an escaped one in the usual POSIX idiom.
    /// </remarks>
    internal static string ShellQuote(string path) =>
        "'" + path.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

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
