using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vidra.App.Services;
using Vidra.Core.Models;
using Vidra.Core.Queue;
using Vidra.Core.YtDlp;

namespace Vidra.App.ViewModels;

public sealed partial class QueueViewModel : ObservableObject
{
    private readonly AppHost _host;
    private readonly MainViewModel _main;
    private readonly Queue<double> _samples = new();

    public QueueViewModel(AppHost host, MainViewModel main)
    {
        _host = host;
        _main = main;
        HistoryView = CollectionViewSource.GetDefaultView(host.Queue.History);
        HistoryView.Filter = o => o is HistoryEntry h && MatchesHistory(h);
        host.Queue.Jobs.CollectionChanged += (_, _) => RefreshCounts();
        host.Queue.History.CollectionChanged += (_, _) => RefreshCounts();
        host.Queue.Drained += (_, _) => Application.Current?.Dispatcher.BeginInvoke(OnDrained);
        // Zamanlama kapanıştan sonra da geçerli: saat gelene kadar kuyruk başlamasın
        host.Queue.Held = host.Settings.ScheduledStart is not null;
        RefreshCounts();
    }

    // ---- Zamanlama: kuyruk bekletilir (sonradan eklenenler dahil), saat gelince kendiliğinden başlar
    [ObservableProperty] private string _scheduleTime = "02:00";
    [ObservableProperty] private string? _scheduleError;
    public bool IsScheduled => _host.Settings.ScheduledStart is not null;
    public string ScheduleLabel => _host.Settings.ScheduledStart is { } t ? $"{t.LocalDateTime:d MMM HH:mm} için zamanlandı" : "";

    [RelayCommand]
    private void SetSchedule()
    {
        if (!TimeOnly.TryParseExact(ScheduleTime.Trim(), ["H:mm", "HH:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var t))
        {
            ScheduleError = "Saati 02:00 biçiminde yaz";
            return;
        }
        ScheduleError = null;
        var at = DateTime.Today + t.ToTimeSpan();
        if (at <= DateTime.Now) at = at.AddDays(1);
        _host.Queue.Held = true;
        _host.Queue.PauseAll();
        _host.SettingsStore.Update(s => s.ScheduledStart = at);
        NotifySchedule();
    }

    [RelayCommand]
    private void CancelSchedule()
    {
        _host.Queue.Held = false;
        _host.SettingsStore.Update(s => s.ScheduledStart = null);
        NotifySchedule();
    }

    /// <summary>Saniyede bir (MainViewModel zamanlayıcısı): saat geldiyse bekleyen ve duraklatılmış işleri başlat.</summary>
    public void CheckSchedule()
    {
        if (_host.Settings.ScheduledStart is not { } t || DateTimeOffset.Now < t) return;
        CancelSchedule();
        _host.Queue.ResumeAll();
        if (_host.Settings.Notifications) _host.Tray?.Notify("Zamanlanmış indirme başladı", $"{_host.Queue.Jobs.Count} iş kuyrukta.");
    }

    private void NotifySchedule()
    {
        OnPropertyChanged(nameof(IsScheduled));
        OnPropertyChanged(nameof(ScheduleLabel));
    }

    public DownloadQueue Queue => _host.Queue;
    public ICollectionView HistoryView { get; }

    [ObservableProperty] private DownloadJob? _selected;
    [ObservableProperty] private string _historyFilter = "all";
    [ObservableProperty] private string _historyQuery = "";
    [ObservableProperty] private bool _sleepWhenDone;
    [ObservableProperty] private PointCollection _sparkline = [];

    [ObservableProperty] private int _runningCount;
    [ObservableProperty] private int _queuedCount;
    [ObservableProperty] private int _pausedCount;
    [ObservableProperty] private int _errorCount;
    [ObservableProperty] private int _hAll;
    [ObservableProperty] private int _hVideo;
    [ObservableProperty] private int _hAudio;
    [ObservableProperty] private int _hWarn;

    public int Concurrency
    {
        get => _host.Settings.Concurrency;
        set
        {
            var v = Math.Clamp(value, 1, 6);
            _host.SettingsStore.Update(s => s.Concurrency = v);
            OnPropertyChanged();
            _host.Queue.Schedule();
        }
    }

    partial void OnHistoryFilterChanged(string value) => HistoryView.Refresh();
    partial void OnHistoryQueryChanged(string value) => HistoryView.Refresh();

    private bool MatchesHistory(HistoryEntry h)
    {
        var f = HistoryFilter switch
        {
            "video" => h.Options.Mode == DownloadMode.Video,
            "audio" => h.Options.Mode == DownloadMode.Audio,
            "warn" => h.Warning is not null || h.Status != JobStatus.Done,
            _ => true,
        };
        var q = HistoryQuery.Trim();
        return f && (q.Length == 0 || h.Title.Contains(q, StringComparison.CurrentCultureIgnoreCase)
                     || (h.Channel?.Contains(q, StringComparison.CurrentCultureIgnoreCase) ?? false));
    }

    private void RefreshCounts()
    {
        var jobs = _host.Queue.Jobs;
        RunningCount = jobs.Count(j => j.Status is JobStatus.Running or JobStatus.Processing);
        QueuedCount = jobs.Count(j => j.Status == JobStatus.Queued);
        PausedCount = jobs.Count(j => j.Status == JobStatus.Paused);
        ErrorCount = jobs.Count(j => j.Status == JobStatus.Error);
        var h = _host.Queue.History;
        HAll = h.Count;
        HVideo = h.Count(x => x.Options.Mode == DownloadMode.Video);
        HAudio = h.Count(x => x.Options.Mode == DownloadMode.Audio);
        HWarn = h.Count(x => x.Warning is not null || x.Status != JobStatus.Done);
        if (Selected is not null && !jobs.Contains(Selected)) Selected = jobs.FirstOrDefault();
        Selected ??= jobs.FirstOrDefault();
    }

    /// <summary>Saniyede bir çağrılır: son 30 örnekten küçük hız grafiği.</summary>
    public void SampleSpeed(double speed)
    {
        _samples.Enqueue(speed);
        while (_samples.Count > 30) _samples.Dequeue();
        RefreshCounts();
        var max = Math.Max(_samples.Max(), 1);
        var arr = _samples.ToArray();
        var pts = new PointCollection(arr.Length);
        for (var i = 0; i < arr.Length; i++)
            pts.Add(new Point(arr.Length == 1 ? 0 : i * 120.0 / (arr.Length - 1), 20 - arr[i] / max * 18));
        pts.Freeze();
        Sparkline = pts;
        Selected?.Log.Flush(); // açık ayrıntı panelinin günlüğü canlı kalsın
    }

    private void OnDrained()
    {
        if (_host.Settings.Notifications)
        {
            var done = _host.Queue.History.Take(20).Count(h => h.FinishedAt > DateTimeOffset.Now.AddHours(-6));
            _host.Tray?.Notify("Kuyruk tamamlandı", $"{done} indirme bitti.");
        }
        if (SleepWhenDone)
        {
            SleepWhenDone = false;
            PowerService.Sleep();
        }
    }

    [RelayCommand] private void Select(object? p) { if (p is DownloadJob j) Selected = j; }
    [RelayCommand] private void Pause(object? p) { if (p is DownloadJob j) _host.Queue.Pause(j); }
    [RelayCommand] private void Resume(object? p) { if (p is DownloadJob j) _host.Queue.Resume(j); }
    [RelayCommand] private void Cancel(object? p) { if (p is DownloadJob j) _host.Queue.Cancel(j); }
    [RelayCommand] private void Retry(object? p) { if (p is DownloadJob j) _host.Queue.Retry(j); }
    [RelayCommand] private void MoveToFront(object? p) { if (p is DownloadJob j) _host.Queue.MoveToFront(j); }
    [RelayCommand] private void PauseAll() => _host.Queue.PauseAll();
    [RelayCommand] private void ResumeAll() => _host.Queue.ResumeAll();
    [RelayCommand] private void ClearHistory() => _host.Queue.ClearHistory();
    [RelayCommand] private void RemoveHistory(object? p) { if (p is HistoryEntry h) _host.Queue.RemoveHistory(h); }
    [RelayCommand] private void IncConcurrency() => Concurrency++;
    [RelayCommand] private void DecConcurrency() => Concurrency--;
    [RelayCommand] private void OpenAccount() => _main.Go(Page.Account);

    [RelayCommand]
    private void CopyCommand(object? p)
    {
        if (p is not DownloadJob j) return;
        if (j.LastArgs is null) return;
        var cookie = _host.Settings.Cookies switch { AppLoginCookies a => a.Path, FileCookies f => f.Path, ExtensionCookies x => x.Path, _ => null };
        CopyText(YtDlpArgs.ToCommandLine("yt-dlp", j.LastArgs, cookie));
    }

    [RelayCommand]
    private void CopyLog(object? p)
    {
        if (p is not DownloadJob j) return;
        var text = j.Log.Text;
        if (text.Length == 0) return;
        CopyText(text);
    }

    [RelayCommand]
    private void Open(object? p)
    {
        if (p is not HistoryEntry h) return;
        if (h.FilePath is { } path && File.Exists(path)) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    [RelayCommand]
    private void Reveal(object? p)
    {
        if (p is not HistoryEntry h) return;
        if (h.FilePath is { } path && File.Exists(path)) Process.Start("explorer.exe", $"/select,\"{path}\"");
        else if (Directory.Exists(_host.Settings.OutputDir)) Process.Start("explorer.exe", $"\"{_host.Settings.OutputDir}\"");
    }

    [RelayCommand]
    private void Redownload(object? p)
    {
        if (p is HistoryEntry h)
            _host.Queue.Enqueue([new NewJob(h.Url, h.Title, h.Options, h.Channel, h.Thumbnail, h.Duration, Source: h.Source)]);
    }

    /// <summary>Başka bir uygulama panoyu açık tutuyorsa SetText COMException atar: birkaç kez dene, olmazsa sessizce geç.</summary>
    private void CopyText(string text)
    {
        _host.Clipboard.Ignore(text);
        for (var i = 0; i < 5; i++)
        {
            try
            {
                Clipboard.SetDataObject(text, copy: true);
                return;
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                System.Threading.Thread.Sleep(50);
            }
        }
    }
}
