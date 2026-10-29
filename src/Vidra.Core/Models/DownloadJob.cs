using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Vidra.Core.Models;

/// <summary>
/// Kuyruktaki tek iş. Arayüz doğrudan bağlanabilsin diye gözlemlenebilir;
/// skaler özellik değişimleri WPF tarafından UI thread'ine otomatik taşınır.
/// </summary>
public sealed partial class DownloadJob : ObservableObject
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public required string Url { get; init; }
    public string? VideoId { get; init; }

    [ObservableProperty] private string _title = "";
    public string? Channel { get; set; }
    public string? Thumbnail { get; set; }
    public double? Duration { get; set; }
    public required DownloadOptions Options { get; init; }

    /// <summary>Ayarlardaki şablonu ezer (playlist işleri).</summary>
    public string? OutputTemplate { get; init; }

    /// <summary>"Playlist: X", "Takip: Y" gibi; kuyruk kartında ve geçmişte gösterilir.</summary>
    public string? Source { get; init; }

    [ObservableProperty] private JobStatus _status = JobStatus.Queued;
    [ObservableProperty] private double _percent;
    [ObservableProperty] private long _downloaded;
    [ObservableProperty] private long? _total;
    [ObservableProperty] private double? _speed;
    [ObservableProperty] private double? _eta;

    /// <summary>Video + ses ayrı iner: 1 = video, 2 = ses.</summary>
    [ObservableProperty] private int _stream = 1;
    [ObservableProperty] private string _stage = "Sırada";
    [ObservableProperty] private ErrorKind? _errorKind;
    [ObservableProperty] private string? _errorMessage;

    /// <summary>Dosya indi ama yan adımlardan biri (ör. altyazı) başarısız oldu.</summary>
    [ObservableProperty] private string? _warning;
    [ObservableProperty] private string? _filePath;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
    [ObservableProperty] private DateTimeOffset? _startedAt;
    [ObservableProperty] private DateTimeOffset? _finishedAt;

    /// <summary>Son yt-dlp satırları — yalnızca canlı hata ayıklama için, diske yazılmaz.</summary>
    [JsonIgnore] public LogBuffer Log { get; } = new(300);

    [JsonIgnore] public IReadOnlyList<string>? LastArgs { get; set; }

    public void ResetProgress(string stage)
    {
        Percent = 0;
        Downloaded = 0;
        Total = null;
        Speed = null;
        Eta = null;
        Stream = 1;
        Stage = stage;
    }
}

/// <summary>Sabit kapasiteli, thread-safe satır tamponu.</summary>
public sealed class LogBuffer(int capacity) : ObservableObject
{
    private readonly Queue<string> _lines = new();
    private readonly Lock _gate = new();
    private long _lastNotify;
    private bool _dirty;

    public void Add(string line)
    {
        lock (_gate)
        {
            _lines.Enqueue(line);
            while (_lines.Count > capacity) _lines.Dequeue();
            _dirty = true;
        }
        // yt-dlp saniyede onlarca satır basar: arayüzü en fazla ~4 Hz güncelle
        var now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _lastNotify) >= 250) Flush();
    }

    /// <summary>Bekleyen değişikliği hemen bildir (iş bittiğinde son satırlar kaybolmasın).</summary>
    public void Flush()
    {
        lock (_gate)
        {
            if (!_dirty) return;
            _dirty = false;
        }
        Interlocked.Exchange(ref _lastNotify, Environment.TickCount64);
        OnPropertyChanged(nameof(Text));
    }

    public string Text
    {
        get
        {
            lock (_gate) return string.Join(Environment.NewLine, _lines);
        }
    }

    public int Count
    {
        get
        {
            lock (_gate) return _lines.Count;
        }
    }
}
