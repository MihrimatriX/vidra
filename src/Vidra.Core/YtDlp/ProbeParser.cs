using System.Text.Json;
using System.Text.RegularExpressions;
using Vidra.Core.Models;

namespace Vidra.Core.YtDlp;

/// <summary>yt-dlp -J çıktısını uygulama modellerine çevirir. Saf; testlenebilir.</summary>
public static partial class ProbeParser
{
    [GeneratedRegex(@"^\[(private|deleted) video\]$", RegexOptions.IgnoreCase)]
    private static partial Regex HiddenTitle();

    public static CodecFamily Family(string vcodec) =>
        vcodec.StartsWith("avc1", StringComparison.Ordinal) || vcodec.StartsWith("h264", StringComparison.Ordinal) ? CodecFamily.H264 :
        vcodec.StartsWith("vp9", StringComparison.Ordinal) || vcodec.StartsWith("vp09", StringComparison.Ordinal) ? CodecFamily.Vp9 :
        vcodec.StartsWith("av01", StringComparison.Ordinal) ? CodecFamily.Av1 : CodecFamily.Other;

    public static bool IsPlaylist(JsonElement root) => Str(root, "_type") == "playlist";

    public static VideoInfo ParseVideo(JsonElement r)
    {
        var formats = Get(r, "formats", out var f) && f.ValueKind == JsonValueKind.Array ? f.EnumerateArray().ToList() : [];

        // -drc: dinamik aralık sıkıştırılmış kopyalar; orijinali tercih et.
        // Çok dilli videolarda (dublaj) varsayılan iz önde: yt-dlp de "ba"yı language_preference'a göre seçer.
        var audios = formats
            .Where(x => Str(x, "vcodec") == "none" && Str(x, "acodec") is { } ac && ac != "none" && !(Str(x, "format_id") ?? "").EndsWith("-drc"))
            .Select(x => new AudioInfo(Str(x, "format_id")!, Str(x, "acodec")!.Split('.')[0], Dbl(x, "abr") ?? Dbl(x, "tbr"), Dbl(x, "asr"), Size(x),
                Str(x, "language"),
                // yt-dlp: 10 = sayfanın asıl dili, -1 = bilinmiyor, -10 = diğer
                (Int(x, "language_preference") ?? -1) >= -1,
                (Str(x, "format_note") ?? "").Contains("dubbed", StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(a => a.IsDefault)
            .ThenByDescending(a => a.Abr ?? 0)
            .ToList();
        var audio = audios.FirstOrDefault();

        var qualities = formats
            .Where(x => Int(x, "height") is > 0 && Str(x, "vcodec") is { } vc && vc != "none")
            .Select(x => (Height: Int(x, "height")!.Value, Stream: new StreamInfo(
                Str(x, "format_id")!,
                Family(Str(x, "vcodec")!),
                Str(x, "vcodec")!,
                Int(x, "width"),
                Dbl(x, "fps"),
                Dbl(x, "tbr"),
                Size(x),
                Str(x, "dynamic_range") is { } dr && dr != "SDR")))
            .GroupBy(x => x.Height)
            .Select(g =>
            {
                var streams = g.Select(x => x.Stream).ToList();
                var best = streams.Where(s => s.Size is not null).OrderByDescending(s => s.Tbr ?? 0).FirstOrDefault();
                var maxFps = streams.Max(s => s.Fps ?? 0);
                return new QualityOption(g.Key, maxFps > 0 ? maxFps : null, streams.Any(s => s.Hdr),
                    best?.Size is { } sz ? sz + (audio?.Size ?? 0) : null, streams);
            })
            .OrderByDescending(q => q.Height)
            .ToList();

        var subs = new List<SubtitleTrack>();
        if (Get(r, "subtitles", out var manual) && manual.ValueKind == JsonValueKind.Object)
            foreach (var p in manual.EnumerateObject().Where(p => p.Name != "live_chat"))
                subs.Add(new SubtitleTrack(p.Name, FirstName(p.Value) ?? p.Name, false));

        var autoCount = 0;
        if (Get(r, "automatic_captions", out var auto) && auto.ValueKind == JsonValueKind.Object)
            foreach (var p in auto.EnumerateObject())
            {
                autoCount++;
                if (p.Name.EndsWith("-orig", StringComparison.Ordinal))
                    subs.Add(new SubtitleTrack(p.Name, StripParen(FirstName(p.Value) ?? p.Name), true));
            }

        var chapters = new List<Chapter>();
        if (Get(r, "chapters", out var ch) && ch.ValueKind == JsonValueKind.Array)
        {
            var i = 0;
            foreach (var c in ch.EnumerateArray())
            {
                i++;
                chapters.Add(new Chapter(Str(c, "title") ?? $"Bölüm {i}", Dbl(c, "start_time") ?? 0, Dbl(c, "end_time") ?? 0));
            }
        }

        var heatmap = new List<HeatPoint>();
        if (Get(r, "heatmap", out var hm) && hm.ValueKind == JsonValueKind.Array)
            foreach (var p in hm.EnumerateArray())
                if (Dbl(p, "start_time") is { } s && Dbl(p, "end_time") is { } e && e > s)
                    heatmap.Add(new HeatPoint(s, e, Math.Clamp(Dbl(p, "value") ?? 0, 0, 1)));

        var id = Str(r, "id") ?? "";
        return new VideoInfo
        {
            Id = id,
            Url = Str(r, "webpage_url") ?? YouTubeUrl.WatchUrl(id),
            Title = Str(r, "title") ?? id,
            Channel = Str(r, "channel") ?? Str(r, "uploader") ?? "",
            Thumbnail = Str(r, "thumbnail") ?? Thumb(r),
            Description = (Str(r, "description") ?? "").Trim(),
            Duration = Dbl(r, "duration"),
            UploadDate = Str(r, "upload_date"),
            Views = Lng(r, "view_count"),
            MembersOnly = Str(r, "availability") == "subscriber_only",
            Live = Bool(r, "is_live"),
            Chapters = chapters,
            Qualities = qualities,
            Audio = audio,
            AudioAac = audios.FirstOrDefault(x => x.Codec == "mp4a"),
            Audios = audios,
            AudioLanguages = AudioLanguages(audios),
            Heatmap = heatmap,
            Subtitles = subs,
            AutoTranslateCount = autoCount,
        };
    }

    public static PlaylistInfo ParsePlaylist(JsonElement r, LinkKind kind)
    {
        var entries = new List<PlaylistEntry>();
        if (Get(r, "entries", out var es) && es.ValueKind == JsonValueKind.Array)
            foreach (var e in es.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object || Str(e, "id") is not { Length: > 0 } id) continue;
                var title = Str(e, "title") ?? id;
                var url = Str(e, "url");
                // "Playlistlerim" gibi akışlarda öğeler video değil playlist'tir: açılabilir alt liste olarak işaretle
                var isList = Str(e, "ie_key") is "YoutubeTab" || (url?.Contains("/playlist?list=", StringComparison.Ordinal) ?? false);
                // YouTube'un kendi listelerinde (Beğenilenler, Daha sonra izle…) kanal yerine buton metni gelir.
                // ponytail: yalnızca İngilizce metin süzülüyor; hesap dili farklıysa o dildeki metin görünür
                var owner = Str(e, "channel") ?? Str(e, "uploader");
                entries.Add(new PlaylistEntry
                {
                    Id = id,
                    Url = url is not null && url.StartsWith("http", StringComparison.Ordinal) ? url
                        : isList ? $"https://www.youtube.com/playlist?list={id}" : YouTubeUrl.WatchUrl(id),
                    IsList = isList,
                    Title = title,
                    Duration = Dbl(e, "duration"),
                    UploadDate = Str(e, "upload_date"),
                    Views = Lng(e, "view_count"),
                    MembersOnly = Str(e, "availability") == "subscriber_only",
                    // Silinmiş/özel videolar flat listede "[Private video]" gibi başlıklarla döner
                    Unavailable = HiddenTitle().IsMatch(title) || Str(e, "availability") == "needs_auth",
                    // Düz listedeki "…/hq720.jpg?sqp=…" adresleri aslında WebP döndürür ve WPF WebP çözemez;
                    // mqdefault.jpg her videoda var, gerçek JPEG ve 16:9 (hqdefault 4:3 siyah bant taşır, hq720 eski videolarda yok)
                    Thumbnail = isList ? Thumb(e) : $"https://i.ytimg.com/vi/{id}/mqdefault.jpg",
                    Channel = owner is "View full playlist" or "Playlist" ? null : owner,
                    ChannelUrl = Str(e, "channel_url") ?? Str(e, "uploader_url")
                        ?? (Str(e, "channel_id") is { Length: > 0 } cid ? $"https://www.youtube.com/channel/{cid}" : null),
                });
            }

        return new PlaylistInfo
        {
            Kind = kind,
            Id = Str(r, "id") ?? "",
            Title = Str(r, "title") ?? "",
            Channel = Str(r, "channel") ?? Str(r, "uploader") ?? "",
            Url = Str(r, "webpage_url") ?? "",
            Entries = entries,
        };
    }

    /// <summary>Birden çok dilde ses izi varsa seçenekler: orijinal önce, sonra ada göre. Tek dilde boş.</summary>
    public static IReadOnlyList<AudioLanguage> AudioLanguages(IReadOnlyList<AudioInfo> audios)
    {
        var langs = audios.Where(a => !string.IsNullOrEmpty(a.Language))
            .GroupBy(a => a.Language!, StringComparer.OrdinalIgnoreCase)
            .Select(g => new AudioLanguage(g.Key, LanguageName(g.Key), g.Any(a => a.IsDefault), g.Any(a => a.AutoDub)))
            .ToList();
        if (langs.Count < 2) return [];
        return langs.OrderByDescending(l => l.Original).ThenBy(l => l.Name, StringComparer.Create(Fmt.Tr, true)).ToList();
    }

    /// <summary>"es-US" → "Español (Estados Unidos)": dilin kendi adı (herkes kendi dilini tanır); bilinmiyorsa kod.</summary>
    public static string LanguageName(string code)
    {
        try
        {
            var n = System.Globalization.CultureInfo.GetCultureInfo(code).NativeName;
            return n.Length > 0 && !n.StartsWith("Unknown", StringComparison.OrdinalIgnoreCase) ? char.ToUpper(n[0], Fmt.Tr) + n[1..] : code;
        }
        catch (System.Globalization.CultureNotFoundException) { return code; }
    }

    /// <summary>Seçilen kodek/fps tercihine göre yt-dlp'nin o çözünürlükte seçeceği akışı tahmin eder.</summary>
    public static StreamInfo? PickStream(QualityOption q, CodecPreference codec, ContainerFormat container, bool maxFps30)
    {
        IEnumerable<StreamInfo> pool = q.Streams;
        if (maxFps30)
        {
            var low = pool.Where(s => (s.Fps ?? 30) <= 30).ToList();
            if (low.Count > 0) pool = low;
        }
        CodecFamily? want = codec switch
        {
            CodecPreference.H264 => CodecFamily.H264,
            CodecPreference.Vp9 => CodecFamily.Vp9,
            CodecPreference.Av1 => CodecFamily.Av1,
            _ => container == ContainerFormat.Mp4 ? CodecFamily.H264 : null,
        };
        if (want is { } w)
        {
            var m = pool.Where(s => s.Codec == w).ToList();
            if (m.Count > 0) pool = m;
        }
        // yt-dlp'nin YouTube sıralaması: kare hızı → HDR → kodek (AV1 > VP9 > H.264). MP4'te H.264 yoksa (ör. 4K)
        // -S vcodec:h264 sınıra en yakın olanı, yani VP9'u seçer. HLS kopyalarının boyutu bilinmez; DASH hâlini öne al.
        return pool.OrderByDescending(s => s.Fps ?? 0)
            .ThenByDescending(s => want is null && s.Hdr)
            .ThenBy(s => CodecRank(s.Codec, want))
            .ThenByDescending(s => s.Size is not null)
            .ThenByDescending(s => s.Tbr ?? 0)
            .FirstOrDefault();
    }

    private static int CodecRank(CodecFamily c, CodecFamily? want) => want == CodecFamily.H264
        ? c switch { CodecFamily.H264 => 0, CodecFamily.Vp9 => 1, CodecFamily.Av1 => 2, _ => 3 }
        : c switch { CodecFamily.Av1 => 0, CodecFamily.Vp9 => 1, CodecFamily.H264 => 2, _ => 3 };

    private static string? Thumb(JsonElement e)
    {
        if (!Get(e, "thumbnails", out var t) || t.ValueKind != JsonValueKind.Array) return null;
        var list = t.EnumerateArray().Select(x => (Url: Str(x, "url"), W: Int(x, "width") ?? 0)).Where(x => x.Url is not null).ToList();
        return list.OrderBy(x => x.W).FirstOrDefault(x => x.W >= 320).Url ?? list.LastOrDefault().Url;
    }

    private static string? FirstName(JsonElement arr) =>
        arr.ValueKind == JsonValueKind.Array ? arr.EnumerateArray().Select(x => Str(x, "name")).FirstOrDefault(n => n is not null) : null;

    private static string StripParen(string s)
    {
        var i = s.IndexOf(" (", StringComparison.Ordinal);
        return i > 0 ? s[..i] : s;
    }

    // yt-dlp çıktısında birçok alan null olabilir (ör. "thumbnails": [null]); nesne olmayan öğede TryGetProperty patlar
    private static bool Get(JsonElement e, string name, out JsonElement v)
    {
        v = default;
        return e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out v);
    }

    private static string? Str(JsonElement e, string name) =>
        Get(e, name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double? Dbl(JsonElement e, string name) =>
        Get(e, name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private static int? Int(JsonElement e, string name) => Dbl(e, name) is { } d ? (int)d : null;
    private static long? Lng(JsonElement e, string name) => Dbl(e, name) is { } d ? (long)d : null;
    private static long? Size(JsonElement e) => Lng(e, "filesize") ?? Lng(e, "filesize_approx");

    private static bool Bool(JsonElement e, string name) =>
        Get(e, name, out var v) && v.ValueKind == JsonValueKind.True;
}
