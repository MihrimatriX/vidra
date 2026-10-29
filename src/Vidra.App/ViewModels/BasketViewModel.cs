using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vidra.Core;
using Vidra.Core.Models;
using Vidra.Core.Queue;
using Vidra.Core.Storage;
using Vidra.Core.YtDlp;

namespace Vidra.App.ViewModels;

/// <summary>
/// Sepet: farklı playlist, kanal ve aramalardan toplanan videolar; biçim en sonda bir kez seçilip hepsi birlikte indirilir.
/// Diskte durur (basket.json), uygulama kapansa da kaybolmaz.
/// </summary>
public sealed partial class BasketViewModel : ObservableObject
{
    private readonly AppHost _host;
    private readonly MainViewModel _main;

    public BasketViewModel(AppHost host, MainViewModel main)
    {
        _host = host;
        _main = main;
        foreach (var i in JsonStore.Read<List<BasketItem>>(host.Paths.BasketFile) ?? []) Items.Add(i);
        var d = host.Settings.Defaults;
        _mode = d.Mode;
        _height = d.Height ?? 0;
        _audioFormat = d.AudioFormat == AudioFormat.Opus ? AudioFormat.Original : d.AudioFormat;
        _musicMode = d.MusicMode;
        Items.CollectionChanged += (_, _) => OnPropertyChanged(nameof(Summary));
    }

    public ObservableCollection<BasketItem> Items { get; } = [];
    public IReadOnlyList<Choice> Modes => Choices.Modes;
    public IReadOnlyList<Choice> Heights => Choices.Heights;
    public IReadOnlyList<Choice> AudioFormats => Choices.AudioFormats;

    [ObservableProperty] private DownloadMode _mode;
    [ObservableProperty] private int _height;
    [ObservableProperty] private AudioFormat _audioFormat;
    [ObservableProperty] private bool _musicMode;

    public string Summary => Items.Count == 0 ? "Sepet boş" : $"{Items.Count} video · {TimeText.Format(Items.Sum(i => i.Duration ?? 0))}";

    /// <returns>Yeni eklenen sayısı (aynı video ikinci kez eklenmez).</returns>
    public int Add(IEnumerable<BasketItem> items)
    {
        var have = Items.Select(i => YouTubeUrl.VideoId(i.Url) ?? i.Url).ToHashSet(StringComparer.Ordinal);
        var n = 0;
        foreach (var i in items)
            if (have.Add(YouTubeUrl.VideoId(i.Url) ?? i.Url)) { Items.Add(i); n++; }
        if (n > 0) Save();
        return n;
    }

    [RelayCommand]
    private void Remove(BasketItem? i)
    {
        if (i is not null && Items.Remove(i)) Save();
    }

    [RelayCommand]
    private void Clear()
    {
        Items.Clear();
        Save();
    }

    [RelayCommand]
    private void DownloadAll()
    {
        if (Items.Count == 0) return;
        var o = _host.Settings.Defaults with { Mode = Mode, Height = Height == 0 ? null : Height, AudioFormat = AudioFormat, MusicMode = MusicMode };
        _host.Queue.Enqueue(Items.Select(i => i.PlaylistTitle is { } list
            ? new NewJob(i.Url, i.Title, o with { Playlist = new PlaylistRef(list, i.PlaylistIndex) }, i.Channel, i.Thumbnail, i.Duration,
                Templates.ResolvePlaylist(_host.Settings.PlaylistTemplate, list, i.PlaylistIndex), i.Source)
            : new NewJob(i.Url, i.Title, o, i.Channel, i.Thumbnail, i.Duration, Source: i.Source)).ToList());
        Clear();
        _main.Go(Page.Queue);
    }

    private void Save() => _ = JsonStore.WriteAsync(_host.Paths.BasketFile, Items.ToList());
}
