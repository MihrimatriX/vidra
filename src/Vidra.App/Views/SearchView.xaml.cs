using System.Windows.Controls;

namespace Vidra.App.Views;

public partial class SearchView : UserControl
{
    public SearchView()
    {
        InitializeComponent();
        Loaded += (_, _) => QueryBox.Focus();
    }
}
