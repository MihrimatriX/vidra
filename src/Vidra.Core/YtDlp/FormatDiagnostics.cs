using System.Collections.Concurrent;
using Vidra.Core.Models;

namespace Vidra.Core.YtDlp;

/// <summary>YouTube'un bazı kaliteleri vermemesinin yt-dlp uyarılarından okunan nedeni.</summary>
public enum FormatIssue
{
    /// <summary>Deno bulunamadı/çalışmadı: JS doğrulaması yapılamıyor.</summary>
    NoJsRuntime,

    /// <summary>"n challenge" / imza çözülemedi: Deno var ama çözücü betik (yt-dlp-ejs) eksik ya da eski.</summary>
    ChallengeFailed,

    /// <summary>İstemcinin akışları GVS PO token istiyor ve verilmedi.</summary>
    PoToken,

    /// <summary>YouTube bu istemciye yalnızca SABR akışı veriyor (yt-dlp indiremez).</summary>
    Sabr,

    /// <summary>Yaş sınırlı video; hesapta yaş doğrulaması yok.</summary>
    AgeRestricted,

    /// <summary>Hiç video akışı yok, yalnızca kapak görselleri.</summary>
    ImagesOnly,
}

/// <summary>
/// yt-dlp'nin stderr'indeki uyarılardan "neden yalnızca 360p geldi" sorusunun cevabını çıkarır.
/// YouTube diğer akışları vermeyince geriye çoğu zaman yalnızca birleşik 360p akışı (format 18) kalır.
/// </summary>
public static class FormatDiagnostics
{
    /// <summary>Stderr'deki WARNING satırları (önek olmadan), sırayla ve tekrarsız.</summary>
    public static IReadOnlyList<string> Warnings(string stderr) =>
        stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("WARNING:", StringComparison.Ordinal))
            .Select(l => l["WARNING:".Length..].Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();

    public static IReadOnlyList<FormatIssue> Detect(IEnumerable<string> warnings)
    {
        var set = new List<FormatIssue>();
        void Add(FormatIssue i) { if (!set.Contains(i)) set.Add(i); }
        foreach (var w in warnings)
        {
            var m = w.ToLowerInvariant();
            if (m.Contains("no supported javascript runtime")) Add(FormatIssue.NoJsRuntime);
            if (m.Contains("challenge solving failed") || m.Contains("signature solving failed") || m.Contains("nsig extraction failed") ||
                m.Contains("challenge solver"))
                Add(FormatIssue.ChallengeFailed);
            if (m.Contains("po token")) Add(FormatIssue.PoToken);
            if (m.Contains("sabr")) Add(FormatIssue.Sabr);
            if (m.Contains("age-restricted") || m.Contains("age-verification")) Add(FormatIssue.AgeRestricted);
            if (m.Contains("only images are available")) Add(FormatIssue.ImagesOnly);
        }
        return set;
    }

    /// <summary>Kullanıcı diliyle tek cümle: ne oldu, ne yapılabilir.</summary>
    public static string Explain(FormatIssue issue) => issue switch
    {
        FormatIssue.NoJsRuntime => "Deno bulunamadı ya da çalışmadı; YouTube'un JS doğrulaması yapılamadı. Ayarlar › Bileşenler'den Deno'yu yeniden kur.",
        FormatIssue.ChallengeFailed => "YouTube'un JS doğrulaması (n/imza) çözülemedi. yt-dlp'yi güncelle; Vidra çözücü betiği gerekirse GitHub'dan indirir.",
        FormatIssue.PoToken => "YouTube bu istemcinin akışları için PO token istiyor. Sürerse Ayarlar › Bileşenler'den PO token sağlayıcısını aç.",
        FormatIssue.Sabr => "YouTube bu oturuma yalnızca SABR akışı veriyor (yt-dlp indiremez). Çoğu zaman geçicidir; farklı hesapla ya da oturumsuz dene.",
        FormatIssue.AgeRestricted => "Video yaş sınırlı ve hesapta yaş doğrulaması yok; YouTube yüksek kaliteleri vermiyor.",
        FormatIssue.ImagesOnly => "YouTube hiç video akışı vermedi (yalnızca görsel). yt-dlp'yi güncelle, sürerse PO token sağlayıcısını aç.",
        _ => issue.ToString(),
    };

    /// <summary>
    /// YouTube'un akışları kısıtladığına dair işaret: en yüksek kalite 360p ya da altı ve ya bir kısıt uyarısı var
    /// ya da tek video akışı birleşik format 18. Gerçekten 360p olan eski videolarda DASH 360p akışları (134, 243) da bulunur.
    /// </summary>
    public static bool LooksLimited(VideoInfo v, IReadOnlyList<FormatIssue> issues)
    {
        if (v.Live) return false;
        var max = v.Qualities.Count == 0 ? 0 : v.Qualities.Max(q => q.Height);
        if (max > 360) return false;
        if (issues.Count > 0) return true;
        var ids = v.Qualities.SelectMany(q => q.Streams).Select(s => s.Id).ToList();
        return ids.Count == 0 || ids.All(id => id == "18");
    }

    public static int MaxHeight(VideoInfo v) => v.Qualities.Count == 0 ? 0 : v.Qualities.Max(q => q.Height);

    /// <summary>Kısıtın açıklaması (kullanıcıya), uyarı yoksa genel neden.</summary>
    public static string Note(int maxHeight, IReadOnlyList<FormatIssue> issues)
    {
        var head = maxHeight > 0 ? $"YouTube yalnızca {maxHeight}p'ye kadar akış verdi; daha yüksek kaliteler alınamadı." : "YouTube video akışı vermedi.";
        var why = issues.Count > 0
            ? string.Join(" ", issues.Select(Explain))
            : "yt-dlp neden belirtmedi; genelde YouTube'daki bir değişiklikten olur. yt-dlp'yi güncelleyip tekrar dene.";
        return head + " " + why;
    }
}

/// <summary>Kısıta takılınca denenen yol. Sıra önemli: önce oturum korunarak başka istemciler, sonra oturumsuz.</summary>
public enum FallbackStep
{
    None,

    /// <summary>TV istemcileri (PO token istemez, çerez destekler) + çözücü betiği GitHub'dan alma izni.</summary>
    TvClients,

    /// <summary>Çerezsiz çıkarım: oturuma özgü kısıtlar (SABR deneyi, işaretlenmiş hesap) yoksa varsayılan istemciler çalışır.</summary>
    NoCookies,
}

/// <summary>
/// Analizde işe yarayan yolu hatırlar ki indirme de aynı akışları alsın. TV istemcisi geneldir (bir süre tüm işlere
/// uygulanır: aynı kısıt playlistteki diğer videolarda da vardır); çerezsiz yol yalnızca o video için.
/// </summary>
public sealed class FormatFallback(Func<DateTimeOffset>? clock = null)
{
    public static readonly TimeSpan GlobalTtl = TimeSpan.FromHours(6);

    private readonly Func<DateTimeOffset> _now = clock ?? (() => DateTimeOffset.UtcNow);
    private readonly ConcurrentDictionary<string, FallbackStep> _perVideo = new(StringComparer.Ordinal);
    private DateTimeOffset _globalUntil;

    public FallbackStep Global => _now() < _globalUntil ? FallbackStep.TvClients : FallbackStep.None;

    public FallbackStep For(string? videoId) =>
        videoId is not null && _perVideo.TryGetValue(videoId, out var s) && s > Global ? s : Global;

    public void Remember(string? videoId, FallbackStep step)
    {
        if (step == FallbackStep.TvClients) _globalUntil = _now() + GlobalTtl;
        if (videoId is not null && step != FallbackStep.None) _perVideo[videoId] = step;
    }
}
