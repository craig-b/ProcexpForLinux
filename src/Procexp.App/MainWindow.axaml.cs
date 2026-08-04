using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Procexp.App;

public partial class MainWindow : Window
{
    public MainWindow() => AvaloniaXamlLoader.Load(this);
}
