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
    private readonly Dictionary<string, CachedList> _items;
    private CancellationTokenSource? _pendingSave;

    public ListCacheStore(string path)
    {
        _path = path;
        var raw = JsonStore.Read<Dictionary<string, CachedList>>(path);
        _items = raw is not null
            ? new Dictionary<string, CachedList>(raw, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, CachedList>(StringComparer.OrdinalIgnoreCase);
    }

    public int Count
    {
        get { lock (_gate) return _items.Count; }
    }

    public PlaylistInfo? TryGet(string url, TimeSpan? maxAge = null)
    {
        var key = Key(url);
        var ttl = maxAge ?? DefaultTtl;
        lock (_gate)
        {
            if (!_items.TryGetValue(key, out var c)) return null;
            if (DateTimeOffset.Now - c.CachedAt > ttl) return null;
            return c.Info;
        }
    }

    public void Put(string url, PlaylistInfo info)
    {
        var key = Key(url);
        lock (_gate)
        {
            _items[key] = new CachedList(info, DateTimeOffset.Now);
            if (_items.Count > MaxEntries)
            {
                var drop = _items.OrderBy(kv => kv.Value.CachedAt).Take(_items.Count - MaxEntries).Select(kv => kv.Key).ToList();
                foreach (var k in drop) _items.Remove(k);
            }
        }
        ScheduleSave();
    }

    public void Remove(string url)
    {
        var key = Key(url);
        bool removed;
        lock (_gate) removed = _items.Remove(key);
        if (removed) ScheduleSave();
    }

    public void RemoveMany(IEnumerable<string> urls)
    {
        var changed = false;
        lock (_gate)
            foreach (var u in urls)
                changed |= _items.Remove(Key(u));
        if (changed) ScheduleSave();
    }

    public void Clear()
    {
        lock (_gate) _items.Clear();
        _ = SaveNowAsync();
    }

    public Task SaveNowAsync()
    {
        Dictionary<string, CachedList> snapshot;
        lock (_gate) snapshot = new Dictionary<string, CachedList>(_items, StringComparer.OrdinalIgnoreCase);
        return JsonStore.WriteAsync(_path, snapshot);
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
