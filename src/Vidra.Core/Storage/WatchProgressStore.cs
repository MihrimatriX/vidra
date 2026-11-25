namespace Vidra.Core.Storage;

/// <summary>
/// Kütüphanedeki kurs ve derslerin "İzlendi" durumları (%APPDATA%\Vidra\watched.json).
/// Anahtar: VideoId varsa o, yoksa dosya yolunun küçük harfli hâli.
/// </summary>
public sealed class WatchProgressStore
{
    private readonly string _path;
    private readonly Lock _gate = new();
    private readonly HashSet<string> _watched;

    public event EventHandler? Changed;

    public WatchProgressStore(string path)
    {
        _path = path;
        var list = JsonStore.Read<List<string>>(path) ?? [];
        _watched = new HashSet<string>(list, StringComparer.OrdinalIgnoreCase);
    }

    public static string KeyFor(string? videoId, string? filePath) =>
        !string.IsNullOrWhiteSpace(videoId) ? videoId.Trim() : (filePath ?? "").Trim();

    public bool IsWatched(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return false;
        lock (_gate) return _watched.Contains(key);
    }

    public HashSet<string> Snapshot()
    {
        lock (_gate) return new HashSet<string>(_watched, StringComparer.OrdinalIgnoreCase);
    }

    public bool Toggle(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return false;
        bool now;
        lock (_gate) now = _watched.Add(key) || !_watched.Remove(key);
        Changed?.Invoke(this, EventArgs.Empty);
        Save();
        return now;
    }

    public void SetWatched(string key, bool watched)
    {
        if (string.IsNullOrWhiteSpace(key)) return;
        bool changed;
        lock (_gate) changed = watched ? _watched.Add(key) : _watched.Remove(key);
        if (!changed) return;
        Changed?.Invoke(this, EventArgs.Empty);
        Save();
    }

    public void MarkAll(IEnumerable<string> keys, bool watched)
    {
        var changed = false;
        lock (_gate)
            foreach (var k in keys.Where(x => !string.IsNullOrWhiteSpace(x)))
                changed |= watched ? _watched.Add(k) : _watched.Remove(k);
        if (!changed) return;
        Changed?.Invoke(this, EventArgs.Empty);
        Save();
    }

    public Task SaveNowAsync()
    {
        List<string> snap;
        lock (_gate) snap = _watched.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        return JsonStore.WriteAsync(_path, snap);
    }

    private void Save() => _ = SaveNowAsync();
}
