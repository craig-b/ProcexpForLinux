using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Procexp.App.Dialogs;

/// <summary>
/// The About window: what this is, which build it is, where it lives.
/// </summary>
public sealed class AboutWindow : Window
{
    private const string RepositoryUrl = "https://github.com/craig-b/ProcexpForLinux";

    public AboutWindow()
    {
        Title = "About Process Explorer";
        Width = 460;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var link = new Button
        {
            Content = RepositoryUrl,
            HorizontalAlignment = HorizontalAlignment.Center,
            Background = Brushes.Transparent,
            Foreground = new SolidColorBrush(Color.FromRgb(0x35, 0x82, 0xd6)),
            Padding = new Thickness(0),
        };
        link.Click += (_, _) => _ = Launcher.LaunchUriAsync(new Uri(RepositoryUrl));

        var close = new Button
        {
            Content = "Close",
            MinWidth = 88,
            IsCancel = true,
            IsDefault = true,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 16, 0, 0),
        };
        close.Click += (_, _) => Close();

        Content = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 6,
            Children =
            {
                new TextBlock
                {
                    Text = "Sysinternals Process Explorer",
                    FontSize = 20,
                    FontWeight = FontWeight.SemiBold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                },
                new TextBlock
                {
                    Text = "for Linux",
                    Opacity = 0.7,
                    HorizontalAlignment = HorizontalAlignment.Center,
                },
                new TextBlock
                {
                    Text = BuildVersion(),
                    Margin = new Thickness(0, 10, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Center,
                },
                new TextBlock
                {
                    Text =
                        $"Running on {Environment.OSVersion.VersionString}  ·  .NET {Environment.Version}",
                    FontSize = 12,
                    Opacity = 0.7,
                    HorizontalAlignment = HorizontalAlignment.Center,
                },
                link,
                close,
            },
        };
    }

    /// <summary>
    /// The build's version: the git describe stamped by the release scripts,
    /// or a plain "development build" for anything else.
    /// </summary>
    private static string BuildVersion()
    {
        var informational = Assembly
            .GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        if (informational is null)
        {
            return "development build";
        }

        // The SDK appends "+<commit>" source-link metadata; the describe
        // string already identifies the commit, so the suffix is noise.
        var plus = informational.IndexOf('+');
        var version = plus >= 0 ? informational[..plus] : informational;

        return version is "1.0.0" or "" ? "development build" : version;
    }
}
