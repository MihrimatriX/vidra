using System.Collections.ObjectModel;
using Vidra.Core.Models;
using Vidra.Core.Queue;
using Vidra.Core.Storage;
using Vidra.Core.YtDlp;

namespace Vidra.Core.Follow;

/// <summary>
/// Takip edilen playlist/kanal/akışları belirli aralıklarla kontrol eder; arşivde ve kuyrukta
/// olmayan yeni videoları kuyruğa ekler. İlk takipte mevcut videolar "görüldü" sayılır —
/// kullanıcı geçmişi toplu indirmek istiyorsa bunu playlist ekranından yapar.
/// </summary>
public sealed class FollowService : IAsyncDisposable
{
    private readonly YtDlpService _ytdlp;
    private readonly DownloadQueue _queue;
    private readonly AppPaths _paths;
    private readonly ArchiveStore _archive;
    private readonly Func<AppSettings> _settings;
    private readonly Action<Action> _dispatch;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _checkGate = new(1, 1);
    private Task? _loop;

    public ObservableCollection<FollowedSource> Items { get; } = [];

    /// <summary>"Görüldü" kimlikleri: ilk takipte var olanlar, tekrar indirilmesin.</summary>
    private readonly HashSet<string> _seen;

    public event EventHandler<(FollowedSource Source, int NewCount)>? Checked;

    public FollowService(YtDlpService ytdlp, DownloadQueue queue, AppPaths paths, Func<AppSettings> settings, Action<Action>? dispatch = null)
    {
        _ytdlp = ytdlp;
        _queue = queue;
        _paths = paths;
        _archive = new ArchiveStore(paths.ArchiveFile);
        _settings = settings;
        _dispatch = dispatch ?? (a => a());
        var state = JsonStore.Read<FollowState>(paths.FollowsFile) ?? new FollowState();
        foreach (var f in state.Items) Items.Add(f);
        _seen = new HashSet<string>(state.Seen, StringComparer.Ordinal);
    }

    public void StartBackgroundLoop()
    {
        _loop ??= Task.Run(async () =>
        {
            // Açılıştan biraz sonra başla; ilk ekran yüklenirken ağ/CPU'yu meşgul etme
            await Task.Delay(TimeSpan.FromSeconds(30), _cts.Token).ConfigureAwait(false);
            while (!_cts.IsCancellationRequested)
            {
                var hours = _settings().FollowIntervalHours;
                if (hours > 0) await CheckDueAsync(TimeSpan.FromHours(hours), _cts.Token).ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromMinutes(10), _cts.Token).ConfigureAwait(false);
            }
        });
    }

    public async Task<FollowedSource> FollowAsync(string url, string title, DownloadOptions options, IEnumerable<string> existingIds)
    {
        var f = new FollowedSource { Url = url, Title = title, Options = options, LastChecked = DateTimeOffset.Now };
        lock (_seen) foreach (var id in existingIds) _seen.Add(id);
        _dispatch(() => Items.Add(f));
        await SaveAsync().ConfigureAwait(false);
        return f;
    }

    public async Task UnfollowAsync(FollowedSource f)
    {
        _dispatch(() => Items.Remove(f));
        await SaveAsync().ConfigureAwait(false);
    }

    public bool IsFollowed(string url) => Items.Any(i => string.Equals(i.Url, url, StringComparison.OrdinalIgnoreCase));

    private async Task CheckDueAsync(TimeSpan interval, CancellationToken ct)
    {
        List<FollowedSource> due = [];
        _dispatch(() => due = Items.Where(i => i.Enabled && (i.LastChecked is null || DateTimeOffset.Now - i.LastChecked > interval)).ToList());
        foreach (var f in due)
        {
            try { await CheckAsync(f, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch { /* tek kaynak hatası diğerlerini durdurmasın; bir sonraki turda tekrar denenir */ }
        }
    }

    /// <summary>Tek kaynağı kontrol eder; eklenen yeni video sayısını döndürür.</summary>
    public async Task<int> CheckAsync(FollowedSource f, CancellationToken ct = default)
    {
        await _checkGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Yeni videolar listenin başında olur; son 50 öğe yeterli ve hızlı
            var result = await _ytdlp.ProbeAsync(f.Url, ChannelTab.Videos, limit: 50, ct).ConfigureAwait(false);
            if (result is not PlaylistInfo pl) return 0;

            var archived = _archive.ReadIds();
            HashSet<string> queued = [];
            _dispatch(() => queued = _queue.Jobs.Select(j => j.VideoId).OfType<string>().ToHashSet());

            List<PlaylistEntry> fresh;
            lock (_seen)
            {
                fresh = pl.Entries.Where(e => !e.Unavailable && !archived.Contains(e.Id) && !queued.Contains(e.Id) && !_seen.Contains(e.Id)).ToList();
                foreach (var e in fresh) _seen.Add(e.Id);
            }

            if (fresh.Count > 0)
            {
                // En eskiden yeniye indir
                _queue.Enqueue(fresh.AsEnumerable().Reverse().Select(e => new NewJob(
                    e.Url, e.Title, f.Options, pl.Channel, e.Thumbnail, e.Duration,
                    Source: $"Takip: {f.Title}")));
            }

            f.LastChecked = DateTimeOffset.Now;
            f.LastNewCount = fresh.Count;
            await SaveAsync().ConfigureAwait(false);
            Checked?.Invoke(this, (f, fresh.Count));
            return fresh.Count;
        }
        finally
        {
            _checkGate.Release();
        }
    }

    private Task SaveAsync()
    {
        List<FollowedSource> items = [];
        _dispatch(() => items = Items.ToList());
        List<string> seen;
        lock (_seen) seen = _seen.ToList();
        return JsonStore.WriteAsync(_paths.FollowsFile, new FollowState { Items = items, Seen = seen });
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
        _cts.Dispose();
    }

    internal sealed class FollowState
    {
        public List<FollowedSource> Items { get; set; } = [];
        public List<string> Seen { get; set; } = [];
    }
}
