using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Vidra.App.ViewModels;

namespace Vidra.App.Views;

public partial class PlaylistView : UserControl
{
    public PlaylistView() => InitializeComponent();

    /// <summary>Satırın herhangi bir yerine tıklamak seçimi değiştirir (onay kutusu ve butonlar kendi tıklamasını işler).</summary>
    private void Row_Click(object sender, MouseButtonEventArgs e)
    {
        for (var d = e.OriginalSource as DependencyObject; d is not null && d != sender; d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d is ButtonBase) return;
        if (sender is FrameworkElement { DataContext: PlaylistRow { Selectable: true } r }) r.IsSelected = !r.IsSelected;
    }
}
