using System.Windows.Controls;

namespace Vidra.App.Views;

public partial class LogView : UserControl
{
    public LogView() => InitializeComponent();

    /// <summary>Günlük canlı akarken en alta kaydır.</summary>
    private void Log_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is TextBox t) t.ScrollToEnd();
    }
}
