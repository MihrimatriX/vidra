using Vidra.Core.Models;
using Vidra.Core.YtDlp;

namespace Vidra.Core.Storage;

/// <summary>Önbellekteki tek playlist/kurs kaydı.</summary>
public sealed record CachedList(PlaylistInfo Info, DateTimeOffset CachedAt);

/// <summary>
/// Playlist ve kurs içeriklerinin kalıcı disk önbelleği (%LOCALAPPDATA%\Vidra\list-cache.json).
/// YouTube liste ızgarasında video sayısı ve üyelere özel bilgisini vermediği için bir kez okunan
/// listeler burada tutulur; başka kanala geçince ya da uygulama kapanınca baştan okunmaz.
/// İndirilmiş video sayıları her seferinde güncel arşivden hesaplanır.
/// </summary>
public sealed class ListCacheStore
{
    private const int MaxEntries = 300;
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromDays(7);

    private readonly string _path;
    private readonly Lock _gate = new();
    private readonly Task<Dictionary<string, CachedList>> _load;
    private CancellationTokenSource? _pendingSave;

    /// <summary>
    /// Dosya arka planda okunur: 300 listelik önbellek onlarca MB olabilir ve açılışta arayüz thread'ini bekletirdi.
    /// İlk erişim (genellikle bir playlist açılınca) okuma bitmediyse yalnızca kalan süre kadar bekler.
    /// </summary>
    public ListCacheStore(string path)
    {
        _path = path;
        _load = Task.Run(() =>
        {
            var raw = JsonStore.Read<Dictionary<string, CachedList>>(path);
            return raw is not null
                ? new Dictionary<string, CachedList>(raw, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, CachedList>(StringComparer.OrdinalIgnoreCase);
        });
    }

    /// <summary>Arka plandaki ilk okuma (testler ve ısınma için).</summary>
    public Task Loaded => _load;

    private Dictionary<string, CachedList> Items => _load.GetAwaiter().GetResult();

    public int Count
    {
        get { lock (_gate) return Items.Count; }
    }

    public PlaylistInfo? TryGet(string url, TimeSpan? maxAge = null)
    {
        var key = Key(url);
        var ttl = maxAge ?? DefaultTtl;
        lock (_gate)
        {
            if (!Items.TryGetValue(key, out var c)) return null;
            if (DateTimeOffset.Now - c.CachedAt > ttl) return null;
            return c.Info;
        }
    }

    public void Put(string url, PlaylistInfo info)
    {
        var key = Key(url);
        lock (_gate)
        {
            Items[key] = new CachedList(info, DateTimeOffset.Now);
            if (Items.Count > MaxEntries)
            {
                var drop = Items.OrderBy(kv => kv.Value.CachedAt).Take(Items.Count - MaxEntries).Select(kv => kv.Key).ToList();
                foreach (var k in drop) Items.Remove(k);
            }
        }
        ScheduleSave();
    }

    public void Remove(string url)
    {
        var key = Key(url);
        bool removed;
        lock (_gate) removed = Items.Remove(key);
        if (removed) ScheduleSave();
    }

    public void RemoveMany(IEnumerable<string> urls)
    {
        var changed = false;
        lock (_gate)
            foreach (var u in urls)
                changed |= Items.Remove(Key(u));
        if (changed) ScheduleSave();
    }

    public void Clear()
    {
        lock (_gate) Items.Clear();
        _ = SaveNowAsync();
    }

    public Task SaveNowAsync()
    {
        Dictionary<string, CachedList> snapshot;
        lock (_gate) snapshot = new Dictionary<string, CachedList>(Items, StringComparer.OrdinalIgnoreCase);
        return JsonStore.WriteAsync(_path, snapshot, JsonStore.Compact);
    }

    private void ScheduleSave()
    {
        _pendingSave?.Cancel();
        var cts = _pendingSave = new CancellationTokenSource();
        _ = Task.Delay(500, cts.Token).ContinueWith(t =>
        {
            if (!t.IsCanceled) _ = SaveNowAsync();
        }, TaskScheduler.Default);
    }

    private static string Key(string url) => YouTubeUrl.CanonicalSourceUrl(url);
}
