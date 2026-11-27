using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Vidra.App.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
        // Başka ekrandan "Ayarlar › Bileşenler" gibi bir bölüme gelindiyse oraya kaydır
        Loaded += (_, _) =>
        {
            if (DataContext is not ViewModels.SettingsViewModel { PendingSection: { } section } vm) return;
            vm.PendingSection = null;
            Dispatcher.BeginInvoke(() => JumpTo(section), System.Windows.Threading.DispatcherPriority.Loaded);
        };
    }

    /// <summary>Sol menüdeki bölüme kaydır.</summary>
    private void Jump_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { CommandParameter: string name }) JumpTo(name);
    }

    private void JumpTo(string name)
    {
        if (FindName(name) is not FrameworkElement { IsVisible: true } target) return;
        var pos = target.TransformToAncestor((Visual)Scroller.Content).Transform(new Point(0, 0));
        Scroller.ScrollToVerticalOffset(pos.Y);
    }
}
