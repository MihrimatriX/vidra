using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vidra.Core.Models;
using Vidra.Core.Queue;

namespace Vidra.App.ViewModels;

/// <summary>
/// Günlük ekranı: kuyruktaki işlerin ham yt-dlp çıktısı. Kuyruk ekranı sade kalsın diye ayrı ve kenar çubuğunda yok;
/// iş kartının menüsünden, hata kutusundan, ayrıntı panelinden, komut paletinden ve Ayarlar'dan açılır.
/// </summary>
public sealed partial class LogViewModel : ObservableObject
{
    private readonly AppHost _host;
    private readonly MainViewModel _main;

    public LogViewModel(AppHost host, MainViewModel main)
    {
        _host = host;
        _main = main;
        // Seçili iş bitip kuyruktan düşünce ekran boş kalmasın
        var refresh = Ui.Coalesce(() => Selected ??= host.Queue.Jobs.FirstOrDefault());
        host.Queue.Jobs.CollectionChanged += (_, _) => refresh();
    }

    public DownloadQueue Queue => _host.Queue;

    /// <summary>Kopyalama komutları kuyruk ekranıyla ortak.</summary>
    public QueueViewModel QueueVm => _main.QueueVm;

    [ObservableProperty] private DownloadJob? _selected;

    /// <summary>İşin günlüğünü aç; iş verilmezse kuyrukta seçili olan (o da yoksa ilk iş).</summary>
    public void Show(DownloadJob? job)
    {
        Selected = job ?? _main.QueueVm.Selected ?? Queue.Jobs.FirstOrDefault();
        _main.Go(Page.Log);
    }

    /// <summary>Ayarlar'daki bağlantı: kuyrukta seçili işin günlüğü.</summary>
    [RelayCommand] private void Open() => Show(null);

    [RelayCommand] private void Back() => _main.Go(Page.Queue);
}
