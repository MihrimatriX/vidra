using System.Text.RegularExpressions;
using Vidra.Core.Models;

namespace Vidra.Core.YtDlp;

public static partial class YouTubeUrl
{
    /// <summary>
    /// yt-dlp'nin hesap akışı kısayolları — yalnızca oturum açıkken çalışır.
    /// </summary>
    public static readonly IReadOnlyList<(string Key, string Label, string Hint)> Feeds =
    [
        (":ytsubs", "Abonelikler", "Takip ettiğin kanalların yeni videoları"),
        (":ytwatchlater", "Daha sonra izle", "Kaydettiğin videolar"),
        (":ytfav", "Beğenilenler", "Beğendiğin videolar"),
        (":ythis", "Geçmiş", "İzleme geçmişin"),
        ("https://www.youtube.com/feed/playlists", "Playlistlerim", "Özel ve liste dışı dahil"),
    ];

    [GeneratedRegex(@"^/(@[^/]+|channel/[^/]+|c/[^/]+|user/[^/]+)", RegexOptions.IgnoreCase)]
    private static partial Regex ChannelPath();

    public static LinkKind? Classify(string raw)
    {
        var s = raw.Trim();
        if (s.StartsWith(":yt", StringComparison.Ordinal)) return LinkKind.Feed;
        if (!Uri.TryCreate(s, UriKind.Absolute, out var u) || (u.Scheme != "https" && u.Scheme != "http")) return null;

        var host = u.Host.ToLowerInvariant();
        foreach (var p in new[] { "www.", "m.", "music." })
            if (host.StartsWith(p, StringComparison.Ordinal)) host = host[p.Length..];

        if (host == "youtu.be") return u.AbsolutePath.Length > 1 ? LinkKind.Video : null;
        if (host != "youtube.com") return null;

        var path = u.AbsolutePath;
        if (path == "/watch") return QueryValue(u, "v") is not null ? LinkKind.Video : null;
        if (path.StartsWith("/shorts/") || path.StartsWith("/live/") || path.StartsWith("/embed/")) return LinkKind.Video;
        if (path == "/playlist") return LinkKind.Playlist;
        if (path.StartsWith("/feed/")) return LinkKind.Feed;
        if (ChannelPath().IsMatch(path)) return LinkKind.Channel;
        return null;
    }

    /// <summary>Video kimliğini çıkarır (arşiv karşılaştırması için).</summary>
    public static string? VideoId(string raw)
    {
        if (!Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var u)) return null;
        if (u.Host.EndsWith("youtu.be", StringComparison.OrdinalIgnoreCase)) return u.AbsolutePath.Trim('/').Split('/')[0];
        if (QueryValue(u, "v") is { } v) return v;
        var parts = u.AbsolutePath.Trim('/').Split('/');
        return parts.Length == 2 && parts[0] is "shorts" or "live" or "embed" ? parts[1] : null;
    }

    /// <summary>Kanal kök URL'sini istenen sekmeye çevirir (yt-dlp sekmeleri ayrı liste olarak döndürür).</summary>
    public static string ChannelTabUrl(string raw, ChannelTab tab)
    {
        if (!Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var u)) return raw;
        var m = ChannelPath().Match(u.AbsolutePath);
        if (!m.Success) return raw;
        return $"{u.Scheme}://{u.Host}/{m.Groups[1].Value}/{tab.Path()}";
    }

    /// <summary>Kanal bağlantısındaki sekme (…/@kanal/playlists → Playlists); sekmesiz ya da tanınmayan yolda null.</summary>
    public static ChannelTab? TabOf(string raw)
    {
        if (!Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var u)) return null;
        var m = ChannelPath().Match(u.AbsolutePath);
        if (!m.Success) return null;
        var rest = u.AbsolutePath[m.Length..].Trim('/');
        foreach (var t in Enum.GetValues<ChannelTab>())
            if (t.Path() == rest) return t;
        return null;
    }

    public static string WatchUrl(string id) => $"https://www.youtube.com/watch?v={id}";

    /// <summary>Kanalın üyelere özel videoları: yt-dlp /membership sekmesini boş okur, UC… kimliğinin UUMO… listesi ise açılır.</summary>
    public static string MembersPlaylistUrl(string channelId) => $"https://www.youtube.com/playlist?list=UUMO{channelId[2..]}";

    /// <summary>Bağlantıdaki list= kimliği. Otomatik mix'ler (RD…) videosuz liste olarak açılamadığı için yok sayılır.</summary>
    public static string? PlaylistId(string raw) =>
        Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var u) && QueryValue(u, "list") is { } id && !id.StartsWith("RD", StringComparison.Ordinal)
            ? id
            : null;

    /// <summary>Metindeki tüm video bağlantıları (çoklu yapıştırma).</summary>
    public static IReadOnlyList<string> ExtractVideoLinks(string text) =>
        text.Split((char[])[' ', '\n', '\r', '\t', ','], StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim())
            .Where(t => Classify(t) == LinkKind.Video)
            .Distinct()
            .ToList();

    // Solda harf/nokta/tire olmamalı: "notyoutube.com" ya da "evil.youtube.com.example" eşleşmesin
    [GeneratedRegex(@"(?<![\w.-])(?:https?://)?(?:(?:www|m|music)\.)?(?:youtube\.com|youtu\.be)/[^\s""'<>]+", RegexOptions.IgnoreCase)]
    private static partial Regex LinkInText();

    /// <summary>
    /// Serbest metindeki her türden YouTube bağlantısı (sürükle-bırak, .url/.txt dosyaları): "URL=…" gibi önekler,
    /// parantez/nokta gibi sondaki noktalama ve eksik şema tolere edilir. Sıra korunur; aynı video farklı
    /// biçimde (youtu.be / watch?v=) iki kez geçse de bir kez alınır.
    /// </summary>
    public static IReadOnlyList<string> ExtractLinks(string text)
    {
        var list = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in LinkInText().Matches(text))
        {
            var s = m.Value.TrimEnd(')', ']', '>', '.', ',', ';', '!', '?');
            if (!s.StartsWith("http", StringComparison.OrdinalIgnoreCase)) s = "https://" + s;
            if (Classify(s) is not { } kind) continue;
            if (seen.Add(kind == LinkKind.Video && VideoId(s) is { } id ? "v:" + id : s)) list.Add(s);
        }
        return list;
    }

    private static string? QueryValue(Uri u, string key)
    {
        foreach (var pair in u.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = pair.Split('=', 2);
            if (kv[0] == key && kv.Length == 2 && kv[1].Length > 0) return Uri.UnescapeDataString(kv[1]);
        }
        return null;
    }
}
