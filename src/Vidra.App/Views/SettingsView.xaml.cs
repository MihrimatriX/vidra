using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Vidra.App.Views;

public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();

    /// <summary>Sol menüdeki bölüme kaydır.</summary>
    private void Jump_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { CommandParameter: string name } || FindName(name) is not FrameworkElement target) return;
        var pos = target.TransformToAncestor((Visual)Scroller.Content).Transform(new Point(0, 0));
        Scroller.ScrollToVerticalOffset(pos.Y);
    }
}
