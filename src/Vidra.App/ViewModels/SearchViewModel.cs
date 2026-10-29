using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vidra.Core;
using Vidra.Core.Models;
using Vidra.Core.Queue;
using Vidra.Core.YtDlp;

namespace Vidra.App.ViewModels;

public sealed partial class SearchResult(PlaylistEntry entry) : ObservableObject
{
    public PlaylistEntry Entry { get; } = entry;
    public string DurationText => TimeText.Format(Entry.Duration);

    /// <summary>Süresi olmayan sonuç canlı yayındır: hızlı indirme bitmez, yalnızca İndir ekranında açılabilir.</summary>
    public bool IsLive => Entry.Duration is null;
    /// <summary>Kanal bağlantısı varsa kanal adı ayrı, tıklanabilir satırda gösterilir.</summary>
    public string Meta => string.Join(" · ", new[] { Entry.ChannelUrl is null ? Entry.Channel : null, Entry.Views is { } v ? $"{Fmt.Count(v)} görüntülenme" : null }
        .Where(s => !string.IsNullOrEmpty(s)));
    [ObservableProperty] private bool _queued;
    [ObservableProperty] private bool _inBasket;
}

public sealed partial class SearchViewModel(AppHost host, MainViewModel main) : ObservableObject
{
    private CancellationTokenSource? _cts;

    public ObservableCollection<SearchResult> Results { get; } = [];

    [ObservableProperty] private string _query = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowEmpty))] private bool _isLoading;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private string _resultInfo = "";
    public bool ShowEmpty => !IsLoading && Results.Count == 0;

    [RelayCommand]
    private async Task Search()
    {
        var q = Query.Trim();
        if (q.Length == 0) return;
        // Kutuya bağlantı yapıştırıldıysa aramak yerine doğrudan aç
        if (YouTubeUrl.Classify(q) is not null) { await main.OpenLinkAsync(q); return; }

        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        IsLoading = true;
        Error = null;
        Results.Clear();
        try
        {
            var r = await host.YtDlp.SearchAsync(q, ct: cts.Token);
            if (cts.IsCancellationRequested) return;
            foreach (var e in r.Entries.Where(e => !e.IsList && !e.Unavailable)) Results.Add(new SearchResult(e));
            ResultInfo = $"“{q}” için {Results.Count} sonuç";
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { Error = e.Message; }
        finally
        {
            if (_cts == cts) IsLoading = false;
            OnPropertyChanged(nameof(ShowEmpty));
        }
    }

    /// <summary>Kalite/altyazı seçmek için İndir ekranında aç.</summary>
    [RelayCommand] private Task Open(SearchResult? r) => r is null ? Task.CompletedTask : main.OpenLinkAsync(r.Entry.Url);

    [RelayCommand]
    private void QuickAudio(SearchResult? r) =>
        Enqueue(r, DownloadProfile.BuiltIns.First(p => p.Options.Mode == DownloadMode.Audio).ApplyTo(host.Settings.Defaults));

    [RelayCommand] private void QuickVideo(SearchResult? r) => Enqueue(r, host.Settings.Defaults with { Mode = DownloadMode.Video });

    [RelayCommand]
    private void AddToBasket(SearchResult? r)
    {
        if (r is null || r.IsLive) return;
        main.Basket.Add([new BasketItem(r.Entry.Url, r.Entry.Title, r.Entry.Channel, r.Entry.Thumbnail, r.Entry.Duration, $"Arama: {Query.Trim()}")]);
        r.InBasket = true;
    }

    private void Enqueue(SearchResult? r, DownloadOptions o)
    {
        if (r is null || r.IsLive) return;
        host.Queue.Enqueue([new NewJob(r.Entry.Url, r.Entry.Title, o, r.Entry.Channel, r.Entry.Thumbnail, r.Entry.Duration, Source: $"Arama: {Query.Trim()}")]);
        r.Queued = true;
    }
}
