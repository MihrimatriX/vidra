using System.Globalization;
using System.Text.RegularExpressions;

namespace Vidra.Core.YtDlp;

/// <summary>yt-dlp çıktı şablonu (%(alan)s) yardımcıları.</summary>
public static partial class Templates
{
    [GeneratedRegex(@"%\(playlist(?:_title)?\)s")]
    private static partial Regex PlaylistField();

    [GeneratedRegex(@"%\(playlist_(?:channel|uploader)\)s")]
    private static partial Regex PlaylistChannelField();

    [GeneratedRegex(@"%\(playlist_index\)(0?\d*)d")]
    private static partial Regex PlaylistIndexD();

    [GeneratedRegex(@"%\((\w+)\)(0?\d*)([sd])")]
    private static partial Regex AnyField();

    [GeneratedRegex(@"[<>:""/\\|?*\x00-\x1f]")]
    private static partial Regex InvalidChars();

    /// <summary>Ayarlar ekranında tek tıkla seçilebilen playlist/kurs klasör şablonları.</summary>
    public static readonly IReadOnlyList<(string Label, string Template)> PlaylistPresets =
    [
        ("Liste / 001 - Başlık (varsayılan)", "%(playlist)s/%(playlist_index)03d - %(title)s.%(ext)s"),
        ("Kanal / Liste / 001 - Başlık", "%(playlist_channel)s/%(playlist)s/%(playlist_index)03d - %(title)s.%(ext)s"),
        ("Kanal / Liste / 001 - Başlık [id]", "%(playlist_channel)s/%(playlist)s/%(playlist_index)03d - %(title)s [%(id)s].%(ext)s"),
    ];

    /// <summary>Windows dosya adında geçersiz karakterleri temizler.</summary>
    public static string SanitizeName(string s)
    {
        var r = InvalidChars().Replace(s, "_").TrimEnd('.', ' ').Trim();
        return r.Length == 0 ? "_" : r;
    }

    private static string Escape(string s) => s.Replace("%", "%%");

    /// <summary>
    /// Playlist videoları tek tek indirildiği için yt-dlp %(playlist)s ve %(playlist_channel)s bilgilerini bilmez;
    /// bu alanları kuyruğa eklerken gerçek değerlerle doldururuz.
    /// </summary>
    public static string ResolvePlaylist(string template, string playlistTitle, int index, string? channel = null)
    {
        var t = PlaylistField().Replace(template, Escape(SanitizeName(playlistTitle)));
        var ch = !string.IsNullOrWhiteSpace(channel) ? Escape(SanitizeName(channel)) : "%(channel,uploader)s";
        t = PlaylistChannelField().Replace(t, ch);
        t = PlaylistIndexD().Replace(t, m => index.ToString(CultureInfo.InvariantCulture).PadLeft(ParsePad(m.Groups[1].Value), '0'));
        return t.Replace("%(playlist_index)s", index.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Arayüz önizlemesi — yt-dlp'nin yaptığının basitleştirilmiş hâli.</summary>
    public static string Preview(string template, IReadOnlyDictionary<string, object?> fields)
    {
        var r = AnyField().Replace(template, m =>
        {
            if (!fields.TryGetValue(m.Groups[1].Value, out var v) || v is null) return $"[{m.Groups[1].Value}]";
            var s = Convert.ToString(v, CultureInfo.InvariantCulture) ?? "";
            return m.Groups[3].Value == "d" ? s.PadLeft(ParsePad(m.Groups[2].Value), '0') : SanitizeName(s);
        });
        return r.Replace("%%", "%");
    }

    private static int ParsePad(string s) => int.TryParse(s, out var n) ? n : 0;
}

public static partial class TimeText
{
    [GeneratedRegex(@"^\d+(:\d{1,2}){0,2}(\.\d+)?$")]
    private static partial Regex TimePattern();

    public static bool IsTime(string? t) => t is not null && TimePattern().IsMatch(t.Trim());

    /// <summary>"2:15" → 135 saniye.</summary>
    public static double? ToSeconds(string? t)
    {
        if (!IsTime(t)) return null;
        double acc = 0;
        foreach (var part in t!.Trim().Split(':'))
            acc = acc * 60 + double.Parse(part, CultureInfo.InvariantCulture);
        return acc;
    }

    public static string Format(double? seconds)
    {
        if (seconds is not { } s || double.IsNaN(s)) return "—";
        var ts = TimeSpan.FromSeconds(Math.Round(s));
        return ts.TotalHours >= 1 ? $"{(int)ts.TotalHours}:{ts.Minutes:00}:{ts.Seconds:00}" : $"{ts.Minutes}:{ts.Seconds:00}";
    }
}
