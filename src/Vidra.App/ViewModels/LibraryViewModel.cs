using System;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Vidra.Core.Models;

namespace Vidra.App.ViewModels;

/// <summary>Kütüphane: diskte hâlâ duran indirmeler, kart olarak. Oynat/klasör komutları Kuyruk ekranınınkilerdir.</summary>
public sealed partial class LibraryViewModel(AppHost host) : ObservableObject
{
    public CardGrid Cards { get; } = new();
    public System.Collections.Generic.IReadOnlyList<Choice> Filters { get; } = [new("all", "Hepsi"), new("video", "Video"), new("audio", "Ses")];

    [ObservableProperty] private string _query = "";
    [ObservableProperty] private string _filter = "all";
    [ObservableProperty] private int _count;
    [ObservableProperty] private long _totalSize;

    partial void OnQueryChanged(string value) => Refresh();
    partial void OnFilterChanged(string value) => Refresh();

    public void Refresh()
    {
        var q = Query.Trim();
        // ponytail: her açılışta en fazla 1000 geçmiş kaydı için File.Exists; yavaşlarsa FileSystemWatcher ile önbellek
        var items = host.Queue.History
            .Where(h => h.Status == JobStatus.Done && h.FilePath is { } p && File.Exists(p))
            .Where(h => Filter switch { "video" => h.Options.Mode == DownloadMode.Video, "audio" => h.Options.Mode == DownloadMode.Audio, _ => true })
            .Where(h => q.Length == 0 || h.Title.Contains(q, StringComparison.CurrentCultureIgnoreCase)
                        || (h.Channel?.Contains(q, StringComparison.CurrentCultureIgnoreCase) ?? false))
            .ToList();
        Count = items.Count;
        TotalSize = items.Sum(h => h.Size ?? 0);
        Cards.SetItems(items);
    }
}
