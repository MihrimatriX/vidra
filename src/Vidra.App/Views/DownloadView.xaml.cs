using System.Windows;
using System.Windows.Controls;
using Vidra.App.ViewModels;

namespace Vidra.App.Views;

public partial class DownloadView : UserControl
{
    public DownloadView()
    {
        InitializeComponent();
        Loaded += (_, _) => UrlBox.Focus();
    }

    /// <summary>
    /// Yapıştırma: birden çok video bağlantısı → hepsi kuyruğa; tek bağlantı → kutuyu değiştir ve hemen analiz et.
    /// </summary>
    private void UrlBox_Pasting(object sender, DataObjectPastingEventArgs e)
    {
        if (DataContext is not DownloadViewModel vm || !e.DataObject.GetDataPresent(DataFormats.UnicodeText)) return;
        var text = (e.DataObject.GetData(DataFormats.UnicodeText) as string ?? "").Trim();
        if (vm.TryEnqueueMany(text))
        {
            e.CancelCommand();
            return;
        }
        if (Vidra.Core.YtDlp.YouTubeUrl.Classify(text) is null) return;
        e.CancelCommand();
        vm.Url = text;
        vm.AnalyzeCommand.Execute(null);
    }
}
