using System.Security.Cryptography;
using System.Text;

namespace Vidra.Core.Storage;

/// <summary>
/// Sınırlı, en az son kullanılanı atan önbellek. Eskiden kapak önbelleği dolunca tamamen siliniyordu:
/// uzun bir listede aşağı kaydırırken ekrandaki kapaklar da düşüyor ve hepsi yeniden iniyordu.
/// </summary>
public sealed class LruCache<TKey, TValue>(int capacity) where TKey : notnull
{
    private readonly Dictionary<TKey, LinkedListNode<(TKey Key, TValue Value)>> _map = new();
    private readonly LinkedList<(TKey Key, TValue Value)> _order = new();
    private readonly Lock _gate = new();

    public int Count
    {
        get { lock (_gate) return _map.Count; }
    }

    public bool TryGet(TKey key, out TValue value)
    {
        lock (_gate)
        {
            if (_map.TryGetValue(key, out var node))
            {
                _order.Remove(node);
                _order.AddFirst(node);
                value = node.Value.Value;
                return true;
            }
        }
        value = default!;
        return false;
    }

    public TValue GetOrAdd(TKey key, Func<TKey, TValue> create)
    {
        if (TryGet(key, out var v)) return v;
        var created = create(key);
        Set(key, created);
        return created;
    }

    public void Set(TKey key, TValue value)
    {
        lock (_gate)
        {
            if (_map.TryGetValue(key, out var old)) _order.Remove(old);
            var node = _order.AddFirst((key, value));
            _map[key] = node;
            while (_map.Count > capacity && _order.Last is { } last)
            {
                _order.RemoveLast();
                _map.Remove(last.Value.Key);
            }
        }
    }

    public void Remove(TKey key)
    {
        lock (_gate)
            if (_map.Remove(key, out var node)) _order.Remove(node);
    }
}

/// <summary>
/// Kapak resimlerinin disk önbelleği (%LOCALAPPDATA%\Vidra\thumbs). Kapaklar istenen genişlikte küçültülmüş hâliyle
/// saklanır; uygulama yeniden açıldığında ya da aynı kanala dönüldüğünde ağdan tekrar inmez.
/// </summary>
public static class ThumbnailDiskCache
{
    public const long DefaultMaxBytes = 150L * 1024 * 1024;

    /// <summary>Adres ve genişlikten dosya adı: adresteki sorgu dizesi (sqp=…) de anahtarın parçası.</summary>
    public static string PathFor(string dir, string url, int width)
    {
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(url)))[..20].ToLowerInvariant();
        return Path.Combine(dir, $"{hash}_{width}.jpg");
    }

    /// <summary>Klasör sınırı aşarsa en eski yazılan dosyalardan siler. Açılışta arka planda çağrılır.</summary>
    /// <returns>Silinen dosya sayısı.</returns>
    public static int Prune(string dir, long maxBytes = DefaultMaxBytes)
    {
        if (!Directory.Exists(dir)) return 0;
        var files = new DirectoryInfo(dir).GetFiles("*.jpg");
        var total = files.Sum(f => f.Length);
        if (total <= maxBytes) return 0;
        var deleted = 0;
        // Sınırın biraz altına in: her açılışta birkaç dosya silmekle uğraşmasın
        var target = maxBytes * 8 / 10;
        foreach (var f in files.OrderBy(f => f.LastWriteTimeUtc))
        {
            if (total <= target) break;
            try
            {
                var len = f.Length;
                f.Delete();
                total -= len;
                deleted++;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* o an okunuyor olabilir */ }
        }
        return deleted;
    }
}
