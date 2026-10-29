using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Vidra.Core.Models;
using Vidra.Core.Process;
using Vidra.Core.Storage;
using Vidra.Core.Tools;
using Vidra.Core.YtDlp;

namespace Vidra.Core.Queue;

public sealed record NewJob(
    string Url,
    string Title,
    DownloadOptions Options,
    string? Channel = null,
    string? Thumbnail = null,
    double? Duration = null,
    string? OutputTemplate = null,
    string? Source = null)
{
    /// <summary>Kuyruktaki işi aynı ayarlarla yeniden oluşturmak için (bekleyen işler, iptali geri alma).</summary>
    public static NewJob From(DownloadJob j) => new(j.Url, j.Title, j.Options, j.Channel, j.Thumbnail, j.Duration, j.OutputTemplate, j.Source);
}

/// <summary>
/// Eşzamanlılık sınırlı indirme kuyruğu.
/// Duraklatma = süreci öldürmek; .part dosyası kalır, yeniden başlatınca yt-dlp kaldığı yerden devam eder.
/// Koleksiyon değişiklikleri <paramref name="dispatch"/> üzerinden (WPF'te UI thread) yapılır.
/// </summary>
public sealed partial class DownloadQueue : ObservableObject
{
    private const int HistoryLimit = 1000;

    private readonly IProcessRunner _runner;
    private readonly ToolManager _tools;
    private readonly Func<AppSettings> _settings;
    private readonly AppPaths _paths;
    private readonly Action<Action> _dispatch;
    private readonly Dictionary<string, IRunningProcess> _running = new();
    private readonly Dictionary<string, DownloadJob> _jobsById = new();
    private readonly Dictionary<string, JobStatus> _intentional = new();
    private readonly Lock _gate = new();
    private bool _wasBusy;
    private bool _lastEndNatural;
    private volatile bool _shuttingDown;

    /// <summary>Kapanış başladı: kalan duraklatmalar "kuyruk bitti" sayılmasın.</summary>
    public void BeginShutdown() => _shuttingDown = true;

    public ObservableCollection<DownloadJob> Jobs { get; } = [];
    public ObservableCollection<HistoryEntry> History { get; } = [];

    /// <summary>Tüm işler bitince (kuyruk boşalınca) tetiklenir — "bitince uyut" ve bildirim için.</summary>
    public event EventHandler? Drained;
    public event EventHandler<DownloadJob>? JobFinished;

    [ObservableProperty] private double _totalSpeed;

    public DownloadQueue(IProcessRunner runner, ToolManager tools, Func<AppSettings> settings, AppPaths paths, Action<Action>? dispatch = null)
    {
        _runner = runner;
        _tools = tools;
        _settings = settings;
        _paths = paths;
        _dispatch = dispatch ?? (a => a());
        foreach (var h in JsonStore.Read<List<HistoryEntry>>(paths.HistoryFile) ?? []) History.Add(h);
        // Önceki oturumda bitmemiş işler duraklatılmış olarak geri gelir; .part dosyalarından devam ederler
        foreach (var n in JsonStore.Read<List<NewJob>>(paths.PendingFile) ?? [])
            Jobs.Add(CreateJob(n with { Source = n.Source ?? "Önceki oturum" }, startNow: false));
    }

    private static DownloadJob CreateJob(NewJob n, bool startNow) => new()
    {
        Url = n.Url,
        VideoId = YouTubeUrl.VideoId(n.Url),
        Title = n.Title,
        Channel = n.Channel,
        Thumbnail = n.Thumbnail,
        Duration = n.Duration,
        Options = n.Options,
        OutputTemplate = n.OutputTemplate,
        Source = n.Source,
        Status = startNow ? JobStatus.Queued : JobStatus.Paused,
        Stage = startNow ? "Sırada" : "Bekletiliyor",
    };

    /// <summary>Kapanışta bitmemiş işleri yazar (uygulama kapanırken çağrılır).</summary>
    public Task SavePendingAsync()
    {
        var pending = Snapshot()
            .Where(j => j.Status is not (JobStatus.Done or JobStatus.Canceled))
            .Select(NewJob.From)
            .ToList();
        return JsonStore.WriteAsync(_paths.PendingFile, pending);
    }

    public int ActiveCount => Jobs.Count(j => j.Status is JobStatus.Running or JobStatus.Processing);

    public IReadOnlyList<DownloadJob> Enqueue(IEnumerable<NewJob> items, bool startNow = true)
    {
        var created = items.Select(n => CreateJob(n, startNow)).ToList();
        _dispatch(() => { foreach (var j in created) Jobs.Add(j); });
        Schedule();
        return created;
    }

    public void Pause(DownloadJob j)
    {
        lock (_gate)
        {
            if (_running.TryGetValue(j.Id, out var p))
            {
                _intentional[j.Id] = JobStatus.Paused;
                p.Kill();
                return;
            }
        }
        if (j.Status is JobStatus.Queued) { j.Status = JobStatus.Paused; j.Stage = "Duraklatıldı"; }
    }

    public void Resume(DownloadJob j)
    {
        if (j.Status is not (JobStatus.Paused or JobStatus.Error)) return;
        j.Status = JobStatus.Queued;
        j.Stage = "Sırada";
        Schedule();
    }

    public void Retry(DownloadJob j)
    {
        j.ErrorKind = null;
        j.ErrorMessage = null;
        j.ResetProgress("Sırada");
        j.Status = JobStatus.Queued;
        Schedule();
    }

    public void Cancel(DownloadJob j)
    {
        lock (_gate)
        {
            if (_running.TryGetValue(j.Id, out var p))
            {
                _intentional[j.Id] = JobStatus.Canceled;
                p.Kill();
                return;
            }
        }
        j.Status = JobStatus.Canceled;
        Finish(j);
    }

    public void MoveToFront(DownloadJob j) => _dispatch(() =>
    {
        var i = Jobs.IndexOf(j);
        if (i > 0) Jobs.Move(i, 0);
    });

    public void MoveToEnd(DownloadJob j) => _dispatch(() =>
    {
        var i = Jobs.IndexOf(j);
        if (i >= 0 && i < Jobs.Count - 1) Jobs.Move(i, Jobs.Count - 1);
    });

    /// <summary>Hata almış işlerin hepsini yeniden sıraya koyar.</summary>
    public int RetryFailed()
    {
        var failed = Snapshot().Where(j => j.Status == JobStatus.Error).ToList();
        foreach (var j in failed) Retry(j);
        return failed.Count;
    }

    /// <summary>Bitmemiş işlerin hepsini iptal eder; geri alabilmek için iptal edilenleri döndürür.</summary>
    public IReadOnlyList<DownloadJob> CancelAll()
    {
        var jobs = Snapshot().Where(j => j.Status is not (JobStatus.Done or JobStatus.Canceled)).ToList();
        // Önce bekleyenler: koşan süreçler öldürülünce Schedule sıradaki işi başlatmasın
        var running = jobs.Where(j => j.Status is JobStatus.Running or JobStatus.Processing).ToList();
        foreach (var j in jobs.Except(running)) Cancel(j);
        foreach (var j in running) Cancel(j);
        return jobs;
    }

    public void PauseAll()
    {
        foreach (var j in Snapshot())
            if (j.Status is JobStatus.Running or JobStatus.Queued or JobStatus.Processing) Pause(j);
    }

    public void ResumeAll()
    {
        foreach (var j in Snapshot().Where(j => j.Status == JobStatus.Paused))
        {
            j.Status = JobStatus.Queued;
            j.Stage = "Sırada";
        }
        Schedule();
    }

    public void ClearHistory()
    {
        _dispatch(() => History.Clear());
        _ = JsonStore.WriteAsync(_paths.HistoryFile, new List<HistoryEntry>());
    }

    public void RemoveHistory(HistoryEntry h)
    {
        _dispatch(() => History.Remove(h));
        _ = SaveHistoryAsync();
    }

    /// <summary>İptali geri alma: iptal kayıtları geçmişten düşer.</summary>
    public void RemoveHistory(IEnumerable<string> ids)
    {
        var set = ids.ToHashSet(StringComparer.Ordinal);
        _dispatch(() =>
        {
            for (var i = History.Count - 1; i >= 0; i--)
                if (set.Contains(History[i].Id)) History.RemoveAt(i);
        });
        _ = SaveHistoryAsync();
    }

    /// <summary>Silinen kayıtları geri koyar (geri al): tarih sırasına yerleşir, zaten olanlar atlanır.</summary>
    public void RestoreHistory(IEnumerable<HistoryEntry> entries)
    {
        var items = entries.ToList();
        _dispatch(() =>
        {
            var have = History.Select(h => h.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var e in items)
            {
                if (!have.Add(e.Id)) continue;
                var i = 0;
                while (i < History.Count && History[i].FinishedAt >= e.FinishedAt) i++;
                History.Insert(i, e);
            }
            while (History.Count > HistoryLimit) History.RemoveAt(History.Count - 1);
        });
        _ = SaveHistoryAsync();
    }

    /// <summary>Zamanlanmış başlangıç bekleniyor: sıradaki işler (sonradan eklenenler dahil) başlamaz.</summary>
    public bool Held { get; set; }

    /// <summary>Eşzamanlılık sınırına kadar sıradaki işleri başlatır.</summary>
    public void Schedule()
    {
        if (Held) return;
        var limit = Math.Max(1, _settings().Concurrency);
        // Anlık görüntüyü kilit DIŞINDA al: dispatch UI thread'ine senkron gider, kilit içinde çağırmak kilitlenme riski
        var jobs = Snapshot();
        List<DownloadJob> toStart = [];
        bool idle;
        lock (_gate)
        {
            var active = _running.Count;
            foreach (var j in jobs)
            {
                if (active >= limit) break;
                if (j.Status != JobStatus.Queued) continue;
                // Aynı anda iki kez başlamasın: kilit içinde işaretle
                j.Status = JobStatus.Running;
                active++;
                toStart.Add(j);
            }
            idle = toStart.Count == 0 && _running.Count == 0;
            if (!idle) _wasBusy = true;
        }
        foreach (var j in toStart) Start(j);
        // Yalnızca gerçek bitişte tetikle: son iş kendiliğinden bitti (duraklatma/iptal değil), bekleyen ya da duraklatılmış iş yok
        if (idle && _wasBusy && _lastEndNatural && !_shuttingDown &&
            jobs.All(j => j.Status is not (JobStatus.Queued or JobStatus.Running or JobStatus.Processing or JobStatus.Paused)))
        {
            _wasBusy = false;
            Drained?.Invoke(this, EventArgs.Empty);
        }
    }

    private void Start(DownloadJob job)
    {
        var s = _settings();
        var args = YtDlpArgs.Download(job.Url, job.Options, s, _tools.Paths, job.OutputTemplate);
        job.LastArgs = args;
        job.ErrorKind = null;
        job.ErrorMessage = null;
        job.Warning = null;
        job.StartedAt ??= DateTimeOffset.Now;
        job.ResetProgress("Bilgi alınıyor");

        string? lastFormat = null;
        ErrorLine? lastError = null;
        var gotFile = false;

        var lineGate = new Lock(); // stdout ve stderr farklı thread'lerden aynı anda gelir

        void OnLine(string raw)
        {
            lock (lineGate) HandleLine(raw);
        }

        void HandleLine(string raw)
        {
            job.Log.Add(raw);
            switch (OutputParser.Parse(raw))
            {
                case DownloadLine d when d.FormatId is not null:
                    // bv*+ba: önce video sonra ses akışı iner; format_id değişimi = yeni akış
                    if (lastFormat is not null && d.FormatId != lastFormat) job.Stream = Math.Min(job.Stream + 1, 2);
                    lastFormat = d.FormatId;
                    job.Status = JobStatus.Running;
                    job.Downloaded = d.Downloaded;
                    job.Total = d.Total;
                    job.Speed = d.Speed;
                    job.Eta = d.Eta;
                    job.Percent = d.Total is > 0 ? Math.Min(100, d.Downloaded * 100.0 / d.Total.Value) : 0;
                    job.Stage = job.Options.Mode == DownloadMode.Video
                        ? (job.Stream == 1 ? "Video akışı (1/2)" : "Ses akışı (2/2)")
                        : "İndiriliyor";
                    UpdateTotalSpeed();
                    break;
                case PostprocessLine p when p.Status == "started":
                    job.Status = JobStatus.Processing;
                    job.Speed = null;
                    job.Eta = null;
                    job.Stage = OutputParser.PostprocessorLabel(p.Name);
                    UpdateTotalSpeed();
                    break;
                case DoneLine done:
                    gotFile = true;
                    job.FilePath = done.FilePath;
                    break;
                case ErrorLine e:
                    lastError = e;
                    break;
                case OtherLine o when o.Text.Contains("has already been recorded in the archive", StringComparison.Ordinal):
                    job.Percent = 100;
                    job.Stage = "Daha önce indirilmiş — atlandı";
                    job.Warning = "Arşivde kayıtlı olduğu için atlandı";
                    break;
            }
        }

        IRunningProcess proc;
        try
        {
            proc = _runner.Start(_tools.YtDlpPath, args, OnLine);
        }
        catch (Exception ex)
        {
            // Durum en son: Status'u izleyen (arayüz, testler) hata türünü dolu görsün
            job.ErrorKind = ErrorKind.Unknown;
            job.ErrorMessage = $"yt-dlp başlatılamadı: {ex.Message}";
            job.Status = JobStatus.Error;
            Schedule();
            return;
        }
        lock (_gate)
        {
            _running[job.Id] = proc;
            _jobsById[job.Id] = job;
        }

        proc.Completion.ContinueWith(t =>
        {
            JobStatus? why;
            lock (_gate)
            {
                _running.Remove(job.Id);
                _jobsById.Remove(job.Id);
                why = _intentional.Remove(job.Id, out var w) ? w : null;
            }
            var code = t.IsCompletedSuccessfully ? t.Result : -1;
            lock (_gate) _lastEndNatural = why is null;
            job.Log.Flush();
            job.Speed = null;
            job.Eta = null;

            if (why == JobStatus.Paused)
            {
                job.Status = JobStatus.Paused;
                job.Stage = "Duraklatıldı";
            }
            else if (why == JobStatus.Canceled)
            {
                job.Status = JobStatus.Canceled;
                Finish(job);
            }
            else if (code == 0 || gotFile)
            {
                job.Status = JobStatus.Done;
                job.Percent = 100;
                job.Stage = "Tamamlandı";
                // İlerleme satırları son inen akışın (çoğu zaman ses) boyutunu taşır; geçmişe birleştirilmiş/dönüştürülmüş dosyanın gerçek boyutu yazılır
                if (job.FilePath is { } fp && File.Exists(fp)) job.Total = new FileInfo(fp).Length;
                if (code != 0 && lastError is not null) job.Warning ??= lastError.Message;
                if (job.Options.Transcript && job.FilePath is { } tp) WriteTranscript(job, tp);
                Finish(job);
            }
            else
            {
                job.ErrorKind = lastError?.Kind ?? ErrorKind.Unknown;
                job.ErrorMessage = lastError is not null
                    ? OutputParser.FriendlyError(lastError.Kind, lastError.Message)
                    : $"yt-dlp {code} koduyla çıktı";
                job.Stage = "Hata";
                job.Status = JobStatus.Error;
            }
            UpdateTotalSpeed();
            Schedule();
        }, TaskScheduler.Default);
    }

    private static void WriteTranscript(DownloadJob job, string mediaPath)
    {
        var o = job.Options;
        // Altyazı da istendiyse .vtt dosyaları kalır; yalnızca döküm için indirildiyse silinir
        var keepSubs = o.Subtitles && !(o.Mode == DownloadMode.Audio && o.MusicMode);
        try
        {
            if (Export.WriteTranscripts(mediaPath, deleteSubs: !keepSubs) == 0)
                job.Warning ??= "Metin döküm için seçili dillerde altyazı bulunamadı";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            job.Warning ??= $"Metin döküm yazılamadı: {e.Message}";
        }
    }

    private void Finish(DownloadJob job)
    {
        job.FinishedAt = DateTimeOffset.Now;
        var entry = HistoryEntry.From(job);
        _dispatch(() =>
        {
            Jobs.Remove(job);
            History.Insert(0, entry);
            while (History.Count > HistoryLimit) History.RemoveAt(History.Count - 1);
        });
        _ = SaveHistoryAsync();
        JobFinished?.Invoke(this, job);
    }

    private Task SaveHistoryAsync()
    {
        List<HistoryEntry> snapshot = [];
        _dispatch(() => snapshot = History.ToList());
        return JsonStore.WriteAsync(_paths.HistoryFile, snapshot);
    }

    private List<DownloadJob> Snapshot()
    {
        List<DownloadJob> list = [];
        _dispatch(() => list = Jobs.ToList());
        return list;
    }

    private void UpdateTotalSpeed()
    {
        // Sık çağrılır: _running anahtarlarından hesapla, UI thread'ine gitme
        double sum = 0;
        lock (_gate)
            foreach (var j in _jobsById.Values)
                if (j.Status == JobStatus.Running) sum += j.Speed ?? 0;
        TotalSpeed = sum;
    }
}
