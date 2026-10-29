using System.Windows;
using System.Windows.Controls;
using Vidra.App.ViewModels;

namespace Vidra.App.Views;

public partial class LibraryView : UserControl
{
    public LibraryView() => InitializeComponent();

    private void Cards_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (DataContext is LibraryViewModel vm) vm.Cards.Resize(e.NewSize.Width - SystemParameters.VerticalScrollBarWidth);
    }
}
