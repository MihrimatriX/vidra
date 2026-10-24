using Vidra.Core.Models;

namespace Vidra.Core.YtDlp;

/// <summary>
/// Bir seçimin dosyayı YouTube'daki hâlinden ne kadar uzaklaştırdığını söyler: neyin yeniden kodlandığı
/// ve hangi uzantıyla kaydedileceği. Saf; arayüzdeki "dönüştürme" satırı ve dosya adı önizlemesi için.
/// </summary>
public static class Conversion
{
    // yt-dlp utils.get_compatible_ext ile aynı kümeler
    private static readonly HashSet<string> Mp4Codecs = ["av1", "hevc", "avc1", "mp4a", "ac-4", "h264", "aacl", "ec-3"];
    private static readonly HashSet<string> WebmCodecs = ["av1", "vp9", "vp8", "opus", "vrbs", "vp9x", "vp8x"];

    /// <summary>
    /// Yeniden kodlanan adımlar; boşsa akışlar yalnızca kopyalanır (kalite kaybı yok).
    /// <paramref name="audioCodec"/>: kopyalanacak ses akışının kodeği ("opus", "mp4a"); biliniyorsa M4A/Opus için
    /// kaynağın gerçekten o kodekte olup olmadığına bakılır (değilse yt-dlp dönüştürür).
    /// </summary>
    public static IReadOnlyList<string> Reencodes(DownloadOptions o, string? audioCodec = null)
    {
        var r = new List<string>();
        var src = Sanitize(audioCodec);
        var converts = o.AudioFormat switch
        {
            AudioFormat.Mp3 or AudioFormat.Flac or AudioFormat.Wav => true,
            AudioFormat.M4a => src is not null and not ("mp4a" or "aac"),
            AudioFormat.Opus => src is not null and not "opus",
            _ => false,
        };
        if (o.Mode == DownloadMode.Audio && converts)
        {
            r.Add($"ses {o.AudioFormat.Label()} biçimine dönüştürülür");
            if (o.MusicMode && o.AudioFormat is AudioFormat.Mp3 or AudioFormat.Flac or AudioFormat.Wav) r.Add("ses yüksekliği eşitlenir");
        }
        if (o.Clip is { Precise: true } c && YtDlpArgs.ClipArgs(c).Count > 0)
            r.Add(o.Mode == DownloadMode.Audio ? "klip kesin kesim için yeniden kodlanır" : "klip kesin kesim için video yeniden kodlanır");
        return r;
    }

    /// <summary>
    /// Kaydedilecek dosyanın uzantısı. Original'de yt-dlp'nin kuralı: kodekler MP4'e uyuyorsa mp4, WebM'e uyuyorsa
    /// webm (kapak gömülecekse mkv, WebM kapak taşımaz), karışıksa mkv.
    /// </summary>
    public static string PredictExt(DownloadOptions o, string? vcodec, string? acodec)
    {
        var a = Sanitize(acodec);
        if (o.Mode == DownloadMode.Audio)
            return o.AudioFormat != AudioFormat.Original ? o.AudioFormat.Ext() : a switch
            {
                "mp4a" or "aac" => "m4a",
                "vorbis" => "ogg",
                "mp3" or "flac" => a,
                _ => "opus",
            };

        var v = Sanitize(vcodec);
        bool Fits(HashSet<string> set) => v is not null && a is not null && set.Contains(v) && set.Contains(a);
        return o.Container switch
        {
            ContainerFormat.Original when Fits(Mp4Codecs) => "mp4",
            ContainerFormat.Original when Fits(WebmCodecs) => o.EmbedThumbnail ? "mkv" : "webm",
            ContainerFormat.Original => "mkv",
            ContainerFormat.Webm => v is null || a is null || Fits(WebmCodecs) ? "webm" : "mkv",
            _ => o.Container.Ext(),
        };
    }

    /// <summary>"vp09.00.40.08" → "vp9", "av01.0.08M" → "av1" (yt-dlp'nin sadeleştirmesi).</summary>
    private static string? Sanitize(string? codec) =>
        string.IsNullOrEmpty(codec) || codec == "none" ? null : codec.Split('.')[0].Replace("0", "").ToLowerInvariant();
}
