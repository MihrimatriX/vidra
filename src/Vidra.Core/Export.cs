using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Vidra.Core.Models;

namespace Vidra.Core;

/// <summary>Dışa aktarma: playlist tablosu (CSV) ve altyazıdan düz metin döküm.</summary>
public static partial class Export
{
    /// <summary>
    /// Excel'de açılabilen CSV. Ayırıcıyı çağıran bölgesel ayardan verir: Türkçe Windows'ta liste ayırıcı ';'
    /// (ondalık virgül olduğu için), İngilizcede ','.
    /// </summary>
    public static string PlaylistCsv(IEnumerable<(int Index, PlaylistEntry Entry, bool Done)> rows, string sep)
    {
        var sb = new StringBuilder();
        Line(sb, sep, "Sıra", "Başlık", "Bağlantı", "Kanal", "Yükleme tarihi", "Süre (sn)", "Görüntülenme", "Üyelere özel", "İndirildi");
        foreach (var (i, e, done) in rows)
            Line(sb, sep,
                i.ToString(CultureInfo.InvariantCulture), e.Title, e.Url, e.Channel ?? "",
                e.UploadDate is { Length: 8 } d ? $"{d[..4]}-{d[4..6]}-{d[6..]}" : "",
                e.Duration is { } s ? ((long)s).ToString(CultureInfo.InvariantCulture) : "",
                e.Views?.ToString(CultureInfo.InvariantCulture) ?? "",
                e.MembersOnly ? "evet" : "",
                done ? "evet" : "");
        return sb.ToString();
    }

    private static void Line(StringBuilder sb, string sep, params string[] fields) =>
        sb.AppendJoin(sep, fields.Select(f => Field(f, sep))).Append("\r\n");

    private static string Field(string f, string sep)
    {
        // Başlıklar YouTube'dan gelir: "=HYPERLINK(…)" gibi bir başlık Excel'de formül olarak çalışmasın
        if (f.Length > 0 && f[0] is '=' or '+' or '-' or '@' or '\t' or '\r') f = "'" + f;
        return f.Contains(sep, StringComparison.Ordinal) || f.Contains('"') || f.Contains('\n') || f.Contains('\r')
            ? $"\"{f.Replace("\"", "\"\"")}\""
            : f;
    }

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex Tag();

    /// <summary>
    /// WebVTT → düz metin. YouTube'un otomatik altyazılarında her ipucu bir öncekinin satırını tekrarlar ve kelime
    /// zamanlamaları &lt;00:00:01.000&gt;&lt;c&gt; etiketleri taşır: etiketler atılır, art arda aynı satır bir kez yazılır.
    /// </summary>
    public static string TranscriptFromVtt(string vtt)
    {
        var sb = new StringBuilder();
        string? last = null;
        var skipBlock = false;
        foreach (var raw in vtt.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) { skipBlock = false; continue; }
            if (skipBlock) continue;
            if (line.StartsWith("WEBVTT", StringComparison.Ordinal) || line.StartsWith("Kind:", StringComparison.Ordinal)
                || line.StartsWith("Language:", StringComparison.Ordinal)) continue;
            if (line.StartsWith("NOTE", StringComparison.Ordinal) || line is "STYLE" or "REGION") { skipBlock = true; continue; }
            if (line.Contains("-->", StringComparison.Ordinal)) continue;
            var text = WebUtility.HtmlDecode(Tag().Replace(line, "")).Trim();
            if (text.Length == 0 || text == last) continue;
            sb.Append(text).Append('\n');
            last = text;
        }
        return sb.ToString();
    }

    /// <summary>İndirilen dosyanın yanındaki "ad.dil.vtt" altyazılarından "ad.dil.txt" dökümleri yazar.</summary>
    /// <param name="deleteSubs">Altyazı istenmeyip yalnızca döküm için indirildiyse .vtt dosyalarını sil.</param>
    public static int WriteTranscripts(string mediaPath, bool deleteSubs)
    {
        var dir = Path.GetDirectoryName(mediaPath);
        if (dir is null || !Directory.Exists(dir)) return 0;
        var n = 0;
        foreach (var vtt in Directory.EnumerateFiles(dir, Path.GetFileNameWithoutExtension(mediaPath) + ".*.vtt").ToList())
        {
            File.WriteAllText(Path.ChangeExtension(vtt, ".txt"), TranscriptFromVtt(File.ReadAllText(vtt)), new UTF8Encoding(false));
            if (deleteSubs) File.Delete(vtt);
            n++;
        }
        return n;
    }

    /// <summary>
    /// VLC, MPV, PotPlayer gibi oynatıcıların açabildiği M3U8 oynatma listesi.
    /// baseDir verilirse aynı kökteki yollar göreli yazılır (kurs klasörü taşınsa da çalışır).
    /// </summary>
    public static string CourseM3u(string playlistTitle, IEnumerable<(int Index, string Title, double? Duration, string FilePath)> items, string? baseDir = null)
    {
        var sb = new StringBuilder();
        sb.Append("#EXTM3U\n");
        var cleanPlaylist = playlistTitle.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (cleanPlaylist.Length > 0) sb.Append("#PLAYLIST:").Append(cleanPlaylist).Append('\n');
        var ordered = items
            .Where(x => !string.IsNullOrWhiteSpace(x.FilePath))
            .DistinctBy(x => x.FilePath, StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x.Index > 0 ? x.Index : int.MaxValue)
            .ThenBy(x => x.FilePath, StringComparer.OrdinalIgnoreCase);
        foreach (var (_, title, duration, path) in ordered)
        {
            var secs = duration is { } d && d > 0 && !double.IsNaN(d) ? (long)Math.Round(d) : -1;
            var cleanTitle = title.Replace('\r', ' ').Replace('\n', ' ').Trim();
            sb.Append("#EXTINF:").Append(secs.ToString(CultureInfo.InvariantCulture)).Append(',').Append(cleanTitle).Append('\n');
            var rel = baseDir is { Length: > 0 } ? SafeRelativePath(baseDir, path) : path;
            sb.Append(rel).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>Kurs klasörüne &lt;KursAdı&gt;.m3u8 dosyası yazar; yazılan dosya yolunu döndürür.</summary>
    public static string? WriteCourseM3u(string folderPath, string playlistTitle, IEnumerable<(int Index, string Title, double? Duration, string FilePath)> items)
    {
        var list = items.Where(x => !string.IsNullOrWhiteSpace(x.FilePath)).ToList();
        if (list.Count == 0 || !Directory.Exists(folderPath)) return null;
        var name = YtDlp.Templates.SanitizeName(string.IsNullOrWhiteSpace(playlistTitle) ? "playlist" : playlistTitle);
        var path = Path.Combine(folderPath, name + ".m3u8");
        File.WriteAllText(path, CourseM3u(playlistTitle, list, folderPath), new UTF8Encoding(false));
        return path;
    }

    private static string SafeRelativePath(string baseDir, string targetPath)
    {
        try
        {
            var rel = Path.GetRelativePath(baseDir, targetPath);
            // Farklı sürücüde GetRelativePath tam yolu döndürür; aynı klasör/alt klasörde göreli yol gelir
            return rel.Replace('\\', '/');
        }
        catch (ArgumentException) { return targetPath; }
    }
}
