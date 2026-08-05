using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;

namespace Procexp.App.Dialogs;

/// <summary>A one-line text prompt — "name this column set", and the like.</summary>
public sealed class TextPromptDialog : Window
{
    private readonly TextBox _input = new() { MinWidth = 280 };

    /// <summary>What was typed, or null when cancelled or left blank.</summary>
    public string? Result { get; private set; }

    public TextPromptDialog(string title, string prompt, string initial = "")
    {
        Title = title;
        SizeToContent = SizeToContent.WidthAndHeight;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _input.Text = initial;

        var ok = new Button
        {
            Content = "OK",
            MinWidth = 80,
            IsDefault = true,
        };
        ok.Click += (_, _) => Accept();

        var cancel = new Button
        {
            Content = "Cancel",
            MinWidth = 80,
            IsCancel = true,
        };
        cancel.Click += (_, _) => Close();

        _input.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Accept();
                e.Handled = true;
            }
        };

        Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 10,
            Children =
            {
                new TextBlock { Text = prompt },
                _input,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { cancel, ok },
                },
            },
        };

        Opened += (_, _) =>
        {
            _input.Focus();
            _input.SelectAll();
        };
    }

    private void Accept()
    {
        var text = _input.Text?.Trim();
        Result = string.IsNullOrEmpty(text) ? null : text;
        Close();
    }
}
