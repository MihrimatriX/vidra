using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Vidra.App.ViewModels;

namespace Vidra.App.Views;

public partial class QueueView : UserControl
{
    public QueueView() => InitializeComponent();

    private void Filter_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is QueueViewModel vm && sender is System.Windows.Controls.Primitives.ToggleButton { Tag: string f } t)
        {
            vm.HistoryFilter = f;
            t.IsChecked = true; // etkin hapa tekrar tıklamak onu söndürmesin
        }
    }

    /// <summary>Geçmiş satırındaki "⋯": satırın sağ tık menüsünü fare konumunda açar (menü komutları satırın Tag'ini kullanır).</summary>
    private void More_Click(object sender, RoutedEventArgs e)
    {
        for (var d = VisualTreeHelper.GetParent((DependencyObject)sender); d is not null; d = VisualTreeHelper.GetParent(d))
        {
            if (d is not FrameworkElement { ContextMenu: { } menu } row) continue;
            menu.PlacementTarget = row;
            menu.Placement = PlacementMode.MousePoint;
            menu.IsOpen = true;
            return;
        }
    }
}
