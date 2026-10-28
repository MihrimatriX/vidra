using System.Windows;
using System.Windows.Controls;
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

    /// <summary>Günlük kutusu canlı akarken en alta kaydır.</summary>
    private void Log_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is TextBox t) t.ScrollToEnd();
    }
}
