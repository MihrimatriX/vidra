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
using Vidra.Core.Queue;

namespace Vidra.App.ViewModels;

/// <summary>
/// Kütüphane: diskte hâlâ duran indirmeler.
/// "Dosyalar" görünümü tekil kartları, "Kurslar &amp; Listeler" görünümü playlist/kurs bazlı ders listesini ve izlenme takibini gösterir.
/// </summary>
public sealed partial class LibraryViewModel : ObservableObject
{
    private readonly AppHost _host;
    private readonly MainViewModel _main;

    public LibraryViewModel(AppHost host, MainViewModel main)
    {
        _host = host;
        _main = main;
        host.WatchProgress.Changed += (_, _) => AppHost.Dispatch(Refresh);
        _listView = host.Settings.LibraryList;
    }

    /// <summary>Dosyalar satır listesi olarak (yoğun görünüm); kart ızgarasıyla aynı sıralı öğeler.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowGrid), nameof(ShowList))] private bool _listView;
    partial void OnListViewChanged(bool value) => _host.SettingsStore.Update(s => s.LibraryList = value);
    public bool ShowGrid => ShowFiles && !ListView;
    public bool ShowList => ShowFiles && ListView;
    [RelayCommand] private void SetListView(string? mode) => ListView = mode == "list";

    /// <summary>Liste görünümünün öğeleri (Cards ile aynı sıra).</summary>
    [ObservableProperty] private IReadOnlyList<HistoryEntry> _items = [];

    public CardGrid Cards { get; } = new();
    public ObservableCollection<CourseGroup> Courses { get; } = [];

    public IReadOnlyList<Choice> Sections { get; } = [new("files", "Dosyalar"), new("courses", "Kurslar & Listeler")];
    public IReadOnlyList<Choice> Filters { get; } = [new("all", "Hepsi"), new("video", "Video"), new("audio", "Ses")];
    public IReadOnlyList<Choice> Sorts { get; } =
        [new("new", "En yeni"), new("old", "En eski"), new("size", "En büyük"), new("name", "Ada göre"), new("channel", "Kanala göre")];

    /// <summary>Kart düğmeleri ve sağ tık menüsü için: oynat, klasör, sil… (Kuyruk ekranı komutları).</summary>
    public QueueViewModel Actions => _main.QueueVm;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsCourses), nameof(ShowFiles), nameof(ShowCourseGrid), nameof(ShowCourseDetail), nameof(ShowGrid), nameof(ShowList))] private string _section = "files";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowCourseGrid), nameof(ShowCourseDetail))] private CourseGroup? _selectedCourse;

    public bool IsCourses => Section == "courses";
    public bool ShowFiles => Section == "files";
    public bool ShowCourseGrid => IsCourses && SelectedCourse is null;
    public bool ShowCourseDetail => IsCourses && SelectedCourse is not null;

    [ObservableProperty] private string _query = "";
    [ObservableProperty] private string _filter = "all";
    [ObservableProperty] private string _sort = "new";
    [ObservableProperty] private int _count;
    [ObservableProperty] private long _totalSize;
    [ObservableProperty] private int _courseCount;

    partial void OnQueryChanged(string value) => Refresh();
    partial void OnFilterChanged(string value) => Refresh();
    partial void OnSortChanged(string value) => Refresh();
    partial void OnSectionChanged(string value)
    {
        if (value != "courses") SelectedCourse = null;
        Refresh();
    }

    public void Refresh()
    {
        var q = Query.Trim();
        var existing = _host.Queue.History
            .Where(h => h.Status == JobStatus.Done && h.FilePath is { } p && File.Exists(p))
            // Aynı dosya birden çok kez indirildiyse (tekrar indir) tek kart
            .DistinctBy(h => h.FilePath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Dosyalar görünümü
        var items = existing
            .Where(h => Filter switch { "video" => h.Options.Mode == DownloadMode.Video, "audio" => h.Options.Mode == DownloadMode.Audio, _ => true })
            .Where(h => q.Length == 0 || h.Title.Contains(q, StringComparison.CurrentCultureIgnoreCase)
                        || (h.Channel?.Contains(q, StringComparison.CurrentCultureIgnoreCase) ?? false));
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
        Items = list;

        // Kurslar & Listeler görünümü
        var watched = _host.WatchProgress.Snapshot();
        var allCourses = LibraryCourses.Group(existing, watched);
        var filteredCourses = q.Length == 0 ? allCourses
            : allCourses.Where(c => c.Title.Contains(q, StringComparison.CurrentCultureIgnoreCase)
                                    || c.Channel.Contains(q, StringComparison.CurrentCultureIgnoreCase)).ToList();
        Courses.Clear();
        foreach (var c in filteredCourses) Courses.Add(c);
        CourseCount = Courses.Count;

        if (SelectedCourse is { } sel)
            SelectedCourse = allCourses.FirstOrDefault(c => string.Equals(c.Key, sel.Key, StringComparison.OrdinalIgnoreCase));
    }

    [RelayCommand] private void OpenCourse(CourseGroup? c) { if (c is not null) SelectedCourse = c; }
    [RelayCommand] private void CloseCourse() => SelectedCourse = null;

    [RelayCommand]
    private void PlayNextLesson(CourseGroup? c)
    {
        c ??= SelectedCourse;
        if (c?.NextUnwatched is { } lesson) Actions.OpenCommand.Execute(lesson.Entry);
    }

    [RelayCommand]
    private void PlayLesson(CourseLesson? l)
    {
        if (l is not null) Actions.OpenCommand.Execute(l.Entry);
    }

    [RelayCommand]
    private void ToggleWatched(CourseLesson? l)
    {
        if (l is null) return;
        _host.WatchProgress.Toggle(l.WatchKey);
    }

    [RelayCommand]
    private void MarkAllWatched()
    {
        if (SelectedCourse is not { } c) return;
        _host.WatchProgress.MarkAll(c.Lessons.Select(l => l.WatchKey), true);
    }

    [RelayCommand]
    private void MarkAllUnwatched()
    {
        if (SelectedCourse is not { } c) return;
        _host.WatchProgress.MarkAll(c.Lessons.Select(l => l.WatchKey), false);
    }

    [RelayCommand]
    private Task CheckCourseUpdates(CourseGroup? c)
    {
        c ??= SelectedCourse;
        return c?.PlaylistUrl is { Length: > 0 } u ? _main.OpenLinkAsync(u) : Task.CompletedTask;
    }

    [RelayCommand]
    private void OpenCourseFolder(CourseGroup? c)
    {
        c ??= SelectedCourse;
        if (c?.FolderPath is { } dir && Directory.Exists(dir)) Process.Start("explorer.exe", $"\"{dir}\"");
        else _main.ShowToast("Kurs klasörü bulunamadı");
    }

    [RelayCommand]
    private void ExportCourseM3u(CourseGroup? c)
    {
        c ??= SelectedCourse;
        if (c?.FolderPath is not { } dir || !Directory.Exists(dir)) { _main.ShowToast("Kurs klasörü bulunamadı"); return; }
        try
        {
            var path = Export.WriteCourseM3u(dir, c.Title, c.Lessons.Where(l => l.Entry.FilePath is not null)
                .Select(l => (l.Index, l.Entry.Title, l.Entry.Duration, l.Entry.FilePath!)));
            if (path is not null)
                _main.ShowToast($"Oynatma listesi kaydedildi: {Path.GetFileName(path)}", "Klasörü aç", () => Process.Start("explorer.exe", $"/select,\"{path}\""));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _main.ShowToast($"M3U8 yazılamadı: {e.Message}");
        }
    }
}
