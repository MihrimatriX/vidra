using System.Globalization;
using System.Text.RegularExpressions;
using Vidra.Core.Models;

namespace Vidra.Core.YtDlp;

public abstract record OutputLine;
public sealed record DownloadLine(string Status, long Downloaded, long? Total, double? Speed, double? Eta, string? FormatId) : OutputLine;
public sealed record PostprocessLine(string Status, string Name) : OutputLine;
public sealed record DoneLine(string FilePath) : OutputLine;
public sealed record MetaLine(string? Channel, string Title) : OutputLine;
public sealed record ErrorLine(ErrorKind Kind, string Message) : OutputLine;
public sealed record WarningLine(string Message) : OutputLine;
public sealed record OtherLine(string Text) : OutputLine;

/// <summary>--progress-template ile ürettiğimiz makine okunur satırları ve ERROR/WARNING satırlarını ayrıştırır.</summary>
public static partial class OutputParser
{
    [GeneratedRegex(@"does not have an? (\w+) tab", RegexOptions.IgnoreCase)]
    private static partial Regex MissingTabText();

    /// <summary>"This channel does not have a courses tab" → Courses. Kanalların çoğunda Kurslar, Shorts ya da Canlı sekmesi yoktur.</summary>
    public static ChannelTab? MissingTab(string message)
    {
        var m = MissingTabText().Match(message);
        if (!m.Success) return null;
        foreach (var t in Enum.GetValues<ChannelTab>())
            if (string.Equals(t.Path(), m.Groups[1].Value, StringComparison.OrdinalIgnoreCase)) return t;
        return null;
    }

    public static OutputLine Parse(string raw)
    {
        var line = raw.TrimEnd('\r');

        if (line.StartsWith(YtDlpArgs.MarkDone, StringComparison.Ordinal))
            return new DoneLine(line[YtDlpArgs.MarkDone.Length..]);

        if (line.StartsWith(YtDlpArgs.MarkMeta, StringComparison.Ordinal))
        {
            // kanal|başlık — başlık '|' içerebilir, kanal içermez
            var p = line[YtDlpArgs.MarkMeta.Length..].Split('|', 2);
            return new MetaLine(p[0] is "" or "NA" ? null : p[0], p.Length > 1 ? p[1] : "");
        }

        if (line.StartsWith(YtDlpArgs.MarkDl, StringComparison.Ordinal))
        {
            var p = line[YtDlpArgs.MarkDl.Length..].Split('|');
            string At(int i) => i < p.Length ? p[i] : "";
            return new DownloadLine(
                At(0),
                (long)(Num(At(1)) ?? 0),
                (long?)(Num(At(2)) ?? Num(At(3))),
                Num(At(4)),
                Num(At(5)),
                At(6) is "" or "NA" ? null : At(6));
        }

        if (line.StartsWith(YtDlpArgs.MarkPp, StringComparison.Ordinal))
        {
            var p = line[YtDlpArgs.MarkPp.Length..].Split('|', 2);
            return new PostprocessLine(p[0], p.Length > 1 ? p[1] : "");
        }

        if (line.StartsWith("ERROR:", StringComparison.Ordinal))
        {
            var msg = line["ERROR:".Length..].Trim();
            return new ErrorLine(Classify(msg), msg);
        }

        if (line.StartsWith("WARNING:", StringComparison.Ordinal))
            return new WarningLine(line["WARNING:".Length..].Trim());

        return new OtherLine(line);
    }

    public static ErrorKind Classify(string message)
    {
        var m = message.ToLowerInvariant();
        // YouTube'un bot doğrulaması: üyelik değil, IP/oturum güveni sorunu (veri merkezi, VPN, yoğun kullanım)
        if (m.Contains("not a bot")) return ErrorKind.BotCheck;
        // Henüz başlamamış canlı yayın / prömiyer: "This live event will begin in 2 hours", "Premieres in 3 hours"
        if (m.Contains("live event will begin") || m.Contains("premieres in") || m.Contains("premiere will begin") ||
            m.Contains("will begin in a few moments") || m.Contains("waiting for scheduled"))
            return ErrorKind.Upcoming;
        if (m.Contains("members") || m.Contains("join this channel") || m.Contains("sign in to confirm") ||
            m.Contains("cookies") || m.Contains("private video") || m.Contains("login required") || m.Contains("sign in"))
            return ErrorKind.Auth;
        if (m.Contains("http error 429") || m.Contains("too many requests") || m.Contains("rate-limited") || m.Contains("rate limited"))
            return ErrorKind.RateLimited;
        if (m.Contains("video unavailable") || m.Contains("has been removed") || m.Contains("not available"))
            return ErrorKind.Unavailable;
        if (m.Contains("ffmpeg") || m.Contains("postprocessing"))
            return ErrorKind.Ffmpeg;
        if (m.Contains("timed out") || m.Contains("connection") || m.Contains("http error 5") || m.Contains("getaddrinfo") || m.Contains("http error 403"))
            return ErrorKind.Network;
        return ErrorKind.Unknown;
    }

    /// <summary>ffmpeg postprocessor adlarını kullanıcı diline çevirir.</summary>
    public static string PostprocessorLabel(string name) => name switch
    {
        "Merger" or "FFmpegMerger" => "Video ve ses birleştiriliyor",
        "ExtractAudio" or "FFmpegExtractAudio" => "Ses dönüştürülüyor",
        "EmbedThumbnail" => "Kapak gömülüyor",
        "ThumbnailsConvertor" or "FFmpegThumbnailsConvertor" => "Kapak hazırlanıyor",
        "FFmpegEmbedSubtitle" => "Altyazı gömülüyor",
        "FFmpegSubtitlesConvertor" => "Altyazı dönüştürülüyor",
        "FFmpegMetadata" => "Meta veri yazılıyor",
        "SponsorBlock" => "Sponsor bölümleri bulunuyor",
        "ModifyChapters" => "Sponsor bölümleri çıkarılıyor",
        "FFmpegSplitChapters" => "Bölümlere ayrılıyor",
        "FFmpegVideoRemuxer" or "FFmpegVideoConvertor" => "Video dönüştürülüyor",
        "MoveFiles" or "MoveFilesAfterDownload" => "Dosya taşınıyor",
        _ => name,
    };

    public static string FriendlyError(ErrorKind kind, string message) => kind switch
    {
        ErrorKind.Auth => "Bu video giriş veya kanal üyeliği istiyor. Hesabım ekranından giriş yap ya da oturumu yenile.",
        ErrorKind.BotCheck => "YouTube bu bağlantıdan bot doğrulaması istiyor. Hesabım ekranından giriş yap; VPN kullanıyorsan kapatmayı ya da bir süre beklemeyi dene.",
        ErrorKind.RateLimited => "YouTube çok fazla istek algıladı (429). Biraz bekleyip tekrar dene; Ayarlar'da nazik modu açık tut.",
        ErrorKind.Unavailable => "Video kaldırılmış, özel ya da bölgende erişilemiyor.",
        ErrorKind.Ffmpeg => "ffmpeg işlemi başarısız oldu: " + message,
        ErrorKind.Network => "Bağlantı hatası: " + message,
        ErrorKind.Upcoming => "Yayın henüz başlamadı. Başlayınca otomatik indirmek için kuyruğa alabilirsin.",
        _ => message,
    };

    private static double? Num(string s) =>
        s is "" or "NA" or "None" ? null :
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
}
