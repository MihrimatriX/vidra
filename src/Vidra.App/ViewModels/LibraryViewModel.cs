using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Vidra.Core;
using Vidra.Core.Models;

namespace Vidra.App.ViewModels;

/// <summary>Kütüphane: diskte hâlâ duran indirmeler, kart olarak. Oynat/klasör/sil komutları Kuyruk ekranınınkilerdir (Actions).</summary>
public sealed partial class LibraryViewModel(AppHost host, MainViewModel main) : ObservableObject
{
    public CardGrid Cards { get; } = new();
    public IReadOnlyList<Choice> Filters { get; } = [new("all", "Hepsi"), new("video", "Video"), new("audio", "Ses")];

    public IReadOnlyList<Choice> Sorts { get; } =
        [new("new", "En yeni"), new("old", "En eski"), new("size", "En büyük"), new("name", "Ada göre"), new("channel", "Kanala göre")];

    /// <summary>Kart düğmeleri ve sağ tık menüsü için: oynat, klasör, sil… (Kuyruk ekranı komutları).</summary>
    public QueueViewModel Actions => main.QueueVm;

    [ObservableProperty] private string _query = "";
    [ObservableProperty] private string _filter = "all";
    [ObservableProperty] private string _sort = "new";
    [ObservableProperty] private int _count;
    [ObservableProperty] private long _totalSize;

    partial void OnQueryChanged(string value) => Refresh();
    partial void OnFilterChanged(string value) => Refresh();
    partial void OnSortChanged(string value) => Refresh();

    public void Refresh()
    {
        var q = Query.Trim();
        // ponytail: her açılışta en fazla 1000 geçmiş kaydı için File.Exists; yavaşlarsa FileSystemWatcher ile önbellek
        var items = host.Queue.History
            .Where(h => h.Status == JobStatus.Done && h.FilePath is { } p && File.Exists(p))
            .Where(h => Filter switch { "video" => h.Options.Mode == DownloadMode.Video, "audio" => h.Options.Mode == DownloadMode.Audio, _ => true })
            .Where(h => q.Length == 0 || h.Title.Contains(q, StringComparison.CurrentCultureIgnoreCase)
                        || (h.Channel?.Contains(q, StringComparison.CurrentCultureIgnoreCase) ?? false))
            // Aynı dosya birden çok kez indirildiyse (tekrar indir) tek kart
            .DistinctBy(h => h.FilePath, StringComparer.OrdinalIgnoreCase);
        var title = StringComparer.Create(Fmt.Tr, ignoreCase: true);
        var list = (Sort switch
        {
            "old" => items.OrderBy(h => h.FinishedAt),
            "size" => items.OrderByDescending(h => h.Size ?? 0),
            "name" => items.OrderBy(h => h.Title, title),
            "channel" => items.OrderBy(h => h.Channel ?? "", title).ThenByDescending(h => h.FinishedAt),
            _ => items.OrderByDescending(h => h.FinishedAt),
        }).ToList();
        Count = list.Count;
        TotalSize = list.Sum(h => h.Size ?? 0);
        Cards.SetItems(list);
    }
}
