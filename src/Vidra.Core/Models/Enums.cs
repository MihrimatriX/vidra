namespace Vidra.Core.Models;

public enum DownloadMode { Video, Audio }

/// <summary>Original: birleştirme kabı zorlanmaz; yt-dlp akışlara uyanı seçer (VP9/AV1+Opus → WebM/MKV, H.264+AAC → MP4).</summary>
public enum ContainerFormat { Mp4, Mkv, Webm, Original }

/// <summary>Auto: MP4'te uyumluluk için H.264, diğerlerinde YouTube'un tercihi.</summary>
public enum CodecPreference { Auto, H264, Vp9, Av1 }

/// <summary>Original: en iyi ses akışı dönüştürülmeden (genelde Opus, bazen AAC/M4A).</summary>
public enum AudioFormat { Mp3, M4a, Opus, Flac, Wav, Original }

public enum BrowserKind { Firefox, Chrome, Edge, Brave, Opera, Vivaldi, Chromium }

public enum ThemeMode { Light, Dark, System }

public enum UpdateChannel { Stable, Nightly }

public enum JobStatus { Queued, Running, Processing, Paused, Done, Error, Canceled }

/// <summary>Upcoming: canlı yayın/prömiyer henüz başlamadı (beklenerek indirilebilir).</summary>
public enum ErrorKind { Auth, BotCheck, Unavailable, Network, Ffmpeg, RateLimited, Unknown, Upcoming }

public enum LinkKind { Video, Playlist, Channel, Feed }

public enum ChannelTab { Videos, Shorts, Streams, Membership, Playlists }

public static class EnumText
{
    /// <summary>Original için uzantı akışlara göre belirlenir: bkz. Conversion.PredictExt.</summary>
    public static string Ext(this ContainerFormat c) => c.ToString().ToLowerInvariant();
    public static string Ext(this AudioFormat a) => a.ToString().ToLowerInvariant();

    public static string Label(this ContainerFormat c) => c switch
    {
        ContainerFormat.Original => "Orijinal",
        ContainerFormat.Webm => "WebM",
        _ => c.ToString().ToUpperInvariant(),
    };

    public static string Label(this AudioFormat a) => a == AudioFormat.Original ? "Orijinal" : a.ToString().ToUpperInvariant();
    public static string Arg(this BrowserKind b) => b.ToString().ToLowerInvariant();

    public static string Label(this BrowserKind b) => b switch
    {
        BrowserKind.Firefox => "Firefox",
        BrowserKind.Chrome => "Chrome",
        BrowserKind.Edge => "Edge",
        BrowserKind.Brave => "Brave",
        BrowserKind.Opera => "Opera",
        BrowserKind.Vivaldi => "Vivaldi",
        _ => "Chromium",
    };

    public static bool IsChromium(this BrowserKind b) => b != BrowserKind.Firefox;

    public static string Path(this ChannelTab t) => t switch
    {
        ChannelTab.Shorts => "shorts",
        ChannelTab.Streams => "streams",
        ChannelTab.Membership => "membership",
        ChannelTab.Playlists => "playlists",
        _ => "videos",
    };
}
