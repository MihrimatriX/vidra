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

    /// <summary>Satırın herhangi bir yerine tıklamak seçimi değiştirir; Shift basılıysa son tıklanan satırla arası seçilir.</summary>
    private void Row_Click(object sender, MouseButtonEventArgs e)
    {
        for (var d = e.OriginalSource as DependencyObject; d is not null && d != sender; d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d is ButtonBase) return;
        if (sender is FrameworkElement { DataContext: PlaylistRow { Selectable: true } r } && DataContext is PlaylistViewModel vm)
            vm.ToggleRow(r, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
    }

    /// <summary>Kart sütun sayısı ve kapak yüksekliği genişlikten (kaydırma çubuğu düşülerek) hesaplanır.</summary>
    private void Cards_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (DataContext is PlaylistViewModel vm) vm.Cards.Resize(e.NewSize.Width - SystemParameters.VerticalScrollBarWidth);
    }
}
