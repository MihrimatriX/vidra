using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vidra.Core;
using Vidra.Core.Models;
using Vidra.Core.YtDlp;

namespace Vidra.App.ViewModels;

/// <summary>Paletteki tek satır. Icon: Icons.xaml'daki geometri anahtarı; Shortcut varsa sağda tuş olarak, yoksa Group yazar.</summary>
public sealed record PaletteItem(string Title, string? Subtitle, string Icon, string Group, string? Shortcut, Func<Task> Run, string Keywords = "");

/// <summary>
/// Komut paleti (Ctrl+K): sayfalar, eylemler, hesap akışları ve geçmiş tek kutudan bulanık aranır.
/// Kutuya bağlantı yazılırsa ilk satır onu açar; eşleşme yoksa son satır YouTube'da arar.
/// </summary>
public sealed partial class CommandPaletteViewModel(AppHost host, MainViewModel main) : ObservableObject
{
    private const int MaxItems = 12;

    /// <summary>Fuzzy.Score'da kelimenin bütün geçtiği eşleşmenin alt sınırı (dağınık harf eşleşmeleri bunun çok altında kalır).</summary>
    private const int WholeWordScore = 700;

    public ObservableCollection<PaletteItem> Items { get; } = [];

    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private string _query = "";
    [ObservableProperty] private PaletteItem? _selected;

    partial void OnQueryChanged(string value) => Refresh();

    public void Open()
    {
        Query = "";
        Refresh();
        IsOpen = true;
    }

    [RelayCommand] public void Close() => IsOpen = false;

    public void Move(int delta)
    {
        if (Items.Count == 0) return;
        var i = Selected is null ? -1 : Items.IndexOf(Selected);
        // Seçim yokken ↓ ilkine, ↑ sonuncuya gider
        Selected = i < 0 ? Items[delta > 0 ? 0 : Items.Count - 1] : Items[(i + delta + Items.Count) % Items.Count];
    }

    /// <summary>Eşzamanlı: önceki komut (ör. bağlantı analizi) sürerken palet yeniden açılıp başka komut çalıştırılabilsin.</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task Run(PaletteItem? item)
    {
        item ??= Selected;
        if (item is null) return;
        Close();
        await item.Run();
    }

    private void Refresh()
    {
        var q = Query.Trim();
        var list = new List<PaletteItem>();

        // Bağlantı yazıldıysa/yapıştırıldıysa en üstte aç
        var links = YouTubeUrl.ExtractLinks(q);
        if (links.Count > 0)
            list.Add(new PaletteItem("Bağlantıyı aç", links[0], "I.Link", "Bağlantı", "Enter", () => main.OpenLinkAsync(links[0])));

        // Boş sorguda her şey (liste kayar), aranınca en iyi birkaçı; sıralama kararlı: eşit puanda tanım sırası
        // Geçmiş uzun başlıklarla dolu: dağınık harf eşleşmesi gürültü olur, orada her kelime bütün geçmeli
        var words = q.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        var ranked = Commands().Select(c => (Item: c, Score: Fuzzy.Score(q, $"{c.Title} {c.Keywords}")))
            .Concat(HistoryItems(q).Select(c => (Item: c, Score: Fuzzy.Score(q, $"{c.Title} {c.Keywords}") is { } s && s >= WholeWordScore * words ? s : (int?)null)))
            .Where(x => x.Score is not null)
            .OrderByDescending(x => x.Score)
            .Select(x => x.Item);
        list.AddRange(q.Length == 0 ? ranked : ranked.Take(MaxItems));

        if (q.Length > 1 && links.Count == 0)
            list.Add(new PaletteItem($"YouTube'da ara: “{q}”", null, "I.Search", "Arama", null, () => SearchAsync(q)));

        Items.Clear();
        foreach (var i in list) Items.Add(i);
        Selected = Items.FirstOrDefault();
    }

    private async Task SearchAsync(string q)
    {
        main.Search.Query = q;
        main.Go(Page.Search);
        await main.Search.SearchCommand.ExecuteAsync(null);
    }

    private static Func<Task> Sync(Action a) => () => { a(); return Task.CompletedTask; };

    private IEnumerable<PaletteItem> Commands()
    {
        PaletteItem Go(Page p, string title, string icon, string key, string keywords) =>
            new(title, null, icon, "Sayfa", $"Ctrl+{key}", Sync(() => main.Go(p)), keywords);

        yield return Go(Page.Download, "İndir", "I.Download", "1", "bağlantı video analiz getir");
        yield return Go(Page.Search, "Ara", "I.Search", "2", "youtube arama");
        yield return Go(Page.Playlist, "Playlist & kanal", "I.Playlist", "3", "liste kanal");
        yield return Go(Page.Basket, "Sepet", "I.Basket", "4", "toplu");
        yield return Go(Page.Queue, "Kuyruk & geçmiş", "I.Clock", "5", "indirmeler history");
        yield return Go(Page.Library, "Kütüphane", "I.Library", "6", "dosyalar indirilenler");
        yield return Go(Page.Stats, "İstatistik", "I.Chart", "7", "grafik");
        yield return Go(Page.Account, "Hesabım", "I.User", "8", "giriş oturum çerez eklenti takip");
        yield return Go(Page.Settings, "Ayarlar", "I.Settings", "9", "tercihler klasör tema");

        var q = host.Queue;
        yield return new("Panodaki bağlantıyı aç", "Kopyaladığın video, playlist ya da kanal", "I.Clipboard", "Eylem", "Ctrl+V", main.PasteAsync, "yapıştır");
        if (q.Jobs.Any(j => j.Status is JobStatus.Running or JobStatus.Queued or JobStatus.Processing))
            yield return new("Tümünü duraklat", null, "I.Pause", "Kuyruk", null, Sync(q.PauseAll), "durdur");
        if (q.Jobs.Any(j => j.Status == JobStatus.Paused))
            yield return new("Tümünü sürdür", null, "I.Play", "Kuyruk", null, Sync(q.ResumeAll), "devam başlat");
        if (q.Jobs.Any(j => j.Status == JobStatus.Error))
            yield return new("Hatalıları tekrar dene", null, "I.Refresh", "Kuyruk", null, Sync(() => main.QueueVm.RetryFailedCommand.Execute(null)), "yeniden hata");
        if (q.Jobs.Count > 0)
            yield return new("Kuyruktakilerin hepsini iptal et", "Geri alınabilir", "I.X", "Kuyruk", null, Sync(() => main.QueueVm.CancelAllCommand.Execute(null)), "temizle durdur");
        yield return new(main.QueueVm.SleepWhenDone ? "Bitince uyutmayı kapat" : "Kuyruk bitince bilgisayarı uyut", null, "I.Clock", "Kuyruk", null,
            Sync(() => main.QueueVm.SleepWhenDone = !main.QueueVm.SleepWhenDone), "uyku kapat");
        yield return new("İndirme klasörünü aç", host.Settings.OutputDir, "I.Folder", "Eylem", null, Sync(OpenOutputDir), "dosyalar explorer");
        yield return new(main.Incognito ? "Gizli modu kapat" : "Gizli modu aç", "Yeni indirmeler geçmişe ve arşive yazılmaz", "I.Lock", "Eylem", null,
            Sync(() => main.ToggleIncognitoCommand.Execute(null)), "incognito iz bırakma özel");
        yield return new("Vidra güncellemelerini denetle", $"Şu an {main.Version}", "I.Refresh", "Eylem", null, () => main.CheckUpdatesAsync(manual: true), "sürüm yeni");
        yield return new("Açık / koyu tema", null, "I.Sun", "Eylem", null, Sync(() => main.ToggleThemeCommand.Execute(null)), "karanlık aydınlık görünüm");
        yield return new("yt-dlp'yi güncelle", "Ayarlar › Bileşenler", "I.Refresh", "Eylem", null, UpdateYtDlpAsync, "bileşen sürüm");
        yield return new("Veri klasörünü aç", "Ayarlar, geçmiş, günlük", "I.Folder", "Eylem", null, Sync(() => main.Settings.OpenDataDirCommand.Execute(null)), "log günlük");

        foreach (var f in main.Account.Feeds)
            yield return new(f.Label, f.Hint, "I.User", "Hesap", null, () => OpenFeedAsync(f), "akış hesap feed");
    }

    private IEnumerable<PaletteItem> HistoryItems(string q)
    {
        // Boş sorguda yalnızca son birkaçı; aranınca başlık/kanal eşleşenler
        var done = host.Queue.History.Where(h => h.Status == JobStatus.Done);
        foreach (var h in done.Take(q.Length == 0 ? 3 : 400))
        {
            // Her tuşta yüzlerce File.Exists yapma: dosya yoksa Oynat kendisi "tekrar indir" önerir
            var hasFile = h.FilePath is not null;
            var sub = string.Join(" · ", new[] { h.Channel, h.Options.Describe(), Fmt.Relative(h.FinishedAt) }.Where(s => !string.IsNullOrEmpty(s)));
            yield return new(h.Title, sub, h.Options.Mode == DownloadMode.Audio ? "I.Music" : "I.Video", hasFile ? "Oynat" : "Yeniden aç", null,
                hasFile ? Sync(() => main.QueueVm.OpenCommand.Execute(h)) : () => main.OpenLinkAsync(h.Url), h.Channel ?? "");
        }
    }

    private void OpenOutputDir()
    {
        var dir = host.Settings.OutputDir;
        if (Directory.Exists(dir)) Process.Start("explorer.exe", $"\"{dir}\"");
        else main.ShowToast("İndirme klasörü henüz oluşturulmadı");
    }

    private async Task UpdateYtDlpAsync()
    {
        main.Go(Page.Settings);
        await main.Settings.UpdateYtDlpCommand.ExecuteAsync(null);
    }

    private async Task OpenFeedAsync(FeedItem f)
    {
        if (!main.CookieActive)
        {
            main.ShowToast("Hesap akışları için önce giriş yapmalısın", "Giriş yap", () => main.AccountStatusCommand.Execute(null));
            return;
        }
        await main.Account.OpenFeedCommand.ExecuteAsync(f);
    }
}
