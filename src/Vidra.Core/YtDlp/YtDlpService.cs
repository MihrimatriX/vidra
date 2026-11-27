using System.Text.Json;
using Vidra.Core.Models;
using Vidra.Core.Process;
using Vidra.Core.Tools;

namespace Vidra.Core.YtDlp;

/// <param name="detail">yt-dlp stderr'inin son satırları: kısa açıklamanın altında "ayrıntı" olarak gösterilir.</param>
public sealed class ProbeException(string message, ErrorKind kind, string? detail = null) : Exception(message)
{
    public ErrorKind Kind { get; } = kind;
    public string? Detail { get; } = detail;
}

public sealed record SessionTestResult(bool Ok, string Message, ErrorKind? Kind = null);

/// <summary>Kısa ömürlü yt-dlp çağrıları: bilgi alma (probe) ve oturum testi.</summary>
/// <param name="guard">Verilirse analizde alınan YouTube sınırı (429, bot doğrulaması) kuyruğu da soğumaya sokar.</param>
/// <param name="infoCache">Verilirse tek video analizlerinin bilgisi indirmede yeniden kullanılmak üzere saklanır.</param>
public sealed class YtDlpService(IProcessRunner runner, ToolManager tools, Func<AppSettings> settings, Queue.RateGuard? guard = null,
    InfoJsonCache? infoCache = null)
{
    public async Task<object> ProbeAsync(string rawUrl, ChannelTab tab = ChannelTab.Videos, int? limit = null, CancellationToken ct = default)
    {
        var kind = YouTubeUrl.Classify(rawUrl)
            ?? throw new ProbeException("Bu bir YouTube video, Shorts, playlist, kanal ya da hesap akışı bağlantısı değil.", ErrorKind.Unknown);
        var url = kind == LinkKind.Channel ? YouTubeUrl.ChannelTabUrl(rawUrl, tab) : rawUrl.Trim();
        if (kind == LinkKind.Channel && tab == ChannelTab.Membership)
        {
            // URL zaten /channel/UC… içeriyorsa ek sorguya gerek yok
            var id = YouTubeUrl.ChannelId(rawUrl);
            if (id is null)
            {
                // @tutamaçtan kanal kimliği çıkmaz; sekme sorgusunun kök "id" (veya "channel_id") alanı kimliğin kendisidir.
                // Herkese açık videosu olmayan kanallarda /videos yerine kanal köküne düş.
                id = await ResolveChannelIdAsync(rawUrl, ct).ConfigureAwait(false);
            }
            if (id is not { Length: > 2 } || !id.StartsWith("UC", StringComparison.Ordinal))
                throw new ProbeException("Kanal kimliği alınamadı.", ErrorKind.Unknown);
            url = YouTubeUrl.MembersPlaylistUrl(id);
        }

        var videoId = kind == LinkKind.Video ? YouTubeUrl.VideoId(url) : null;
        var step = tools.Fallback.For(videoId);
        var first = await RunJsonRawAsync(YtDlpArgs.Probe(url, kind, settings(), tools.Paths, limit, step), ct).ConfigureAwait(false);
        using (first.Doc)
        {
            var root = first.Doc.RootElement;
            if (kind != LinkKind.Video && ProbeParser.IsPlaylist(root)) return ProbeParser.ParsePlaylist(root, kind);
            return await VideoWithFallbackAsync(url, ProbeParser.ParseVideo(root), first.Json, first.Warnings, step, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// YouTube yalnızca 360p (çoğu zaman tek birleşik format 18) verdiyse sırayla başka yolları dener: TV istemcileri,
    /// sonra (oturum varsa ve video üyelik istemiyorsa) çerezsiz. En yüksek kaliteyi veren sonuç kullanılır ve hatırlanır;
    /// hiçbiri işe yaramazsa neden kullanıcıya ve günlüğe yazılır.
    /// </summary>
    private async Task<VideoInfo> VideoWithFallbackAsync(string url, VideoInfo v, string json, IReadOnlyList<string> warnings, FallbackStep step,
        CancellationToken ct)
    {
        var issues = FormatDiagnostics.Detect(warnings);
        var best = (Info: v, Json: json, Step: step);
        if (FormatDiagnostics.LooksLimited(v, issues))
        {
            AppLog.Write($"Kısıtlı biçim listesi: {url} · en yüksek {FormatDiagnostics.MaxHeight(v)}p · yol {step}",
                warnings.Count > 0 ? string.Join(Environment.NewLine, warnings) : "(uyarı yok)");
            var s = settings();
            foreach (var next in new[] { FallbackStep.TvClients, FallbackStep.NoCookies })
            {
                if (next <= step) continue;
                // Üyelik/özel videoda çerezsiz deneme anlamsız; oturum yoksa zaten çerezsizdi
                if (next == FallbackStep.NoCookies && (s.Cookies is NoCookies || v.MembersOnly)) continue;
                try
                {
                    var r = await RunJsonRawAsync(YtDlpArgs.Probe(url, LinkKind.Video, s, tools.Paths, null, next), ct).ConfigureAwait(false);
                    using (r.Doc)
                    {
                        var alt = ProbeParser.ParseVideo(r.Doc.RootElement);
                        AppLog.Write($"Kısıt yolu {next}: en yüksek {FormatDiagnostics.MaxHeight(alt)}p", r.Warnings.Count > 0 ? string.Join(Environment.NewLine, r.Warnings) : null);
                        if (FormatDiagnostics.MaxHeight(alt) > FormatDiagnostics.MaxHeight(best.Info)) best = (alt, r.Json, next);
                        if (!FormatDiagnostics.LooksLimited(alt, FormatDiagnostics.Detect(r.Warnings))) break;
                    }
                }
                catch (ProbeException e)
                {
                    AppLog.Write($"Kısıt yolu {next} başarısız: {e.Message}", e.Detail);
                }
            }
            if (best.Step != step) tools.Fallback.Remember(best.Info.Id, best.Step);
        }

        var result = best.Info;
        if (FormatDiagnostics.MaxHeight(result) <= FormatDiagnostics.MaxHeight(v) && FormatDiagnostics.LooksLimited(v, issues))
            result = result with
            {
                FormatNote = FormatDiagnostics.Note(FormatDiagnostics.MaxHeight(result), issues),
                FormatDetail = warnings.Count > 0 ? string.Join(Environment.NewLine, warnings) : null,
            };
        // Canlı yayının format adresleri kısa ömürlü ve değişken: yeniden kullanma
        if (!result.Live) infoCache?.Save(result.Id, best.Json);
        return result;
    }

    private async Task<string?> ResolveChannelIdAsync(string rawUrl, CancellationToken ct)
    {
        foreach (var probeUrl in new[] { YouTubeUrl.ChannelTabUrl(rawUrl, ChannelTab.Videos), YouTubeUrl.ChannelRootUrl(rawUrl) }.Distinct(StringComparer.Ordinal))
        {
            try
            {
                using var head = await RunJsonAsync(YtDlpArgs.Probe(probeUrl, LinkKind.Channel, settings(), tools.Paths, 1), ct).ConfigureAwait(false);
                var root = head.RootElement;
                foreach (var prop in new[] { "id", "channel_id" })
                {
                    if (root.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String &&
                        v.GetString() is { Length: > 2 } cid && cid.StartsWith("UC", StringComparison.Ordinal))
                        return cid;
                }
            }
            catch (ProbeException e) when (e.Kind == ErrorKind.Unavailable) { /* sekme yoksa kök adresle dene */ }
        }
        return null;
    }

    /// <summary>YouTube'da arama; sonuçlar düz liste (format bilgisi yok, indirmeden önce ayrıca analiz edilir).</summary>
    public async Task<PlaylistInfo> SearchAsync(string query, int count = 24, CancellationToken ct = default)
    {
        using var doc = await RunJsonAsync(YtDlpArgs.Search(query, count, settings(), tools.Paths), ct).ConfigureAwait(false);
        return ProbeParser.ParsePlaylist(doc.RootElement, LinkKind.Playlist);
    }

    private async Task<JsonDocument> RunJsonAsync(List<string> args, CancellationToken ct) =>
        (await RunJsonRawAsync(args, ct).ConfigureAwait(false)).Doc;

    private async Task<(JsonDocument Doc, string Json, IReadOnlyList<string> Warnings)> RunJsonRawAsync(List<string> args, CancellationToken ct)
    {
        ProcessResult r;
        using (Perf.Measure("probe", args[^1]))
            r = await runner.RunAsync(tools.YtDlpPath, args, ct).ConfigureAwait(false);
        var json = r.StdOut.Trim();
        // Çıkarım başarısızsa yt-dlp stdout'a "null" basabilir: bunu da hata say
        if (json.Length == 0 || json == "null" || json[0] != '{')
        {
            var err = LastError(r.StdErr);
            var detail = AppLog.Tail(r.StdErr);
            if (OutputParser.MissingTab(err) is { } tab) throw new ProbeException($"Bu kanalda {tab.Label()} sekmesi yok.", ErrorKind.Unavailable, detail);
            var k = OutputParser.Classify(err);
            if (k is ErrorKind.RateLimited or ErrorKind.BotCheck) guard?.Trip(k);
            var friendly = OutputParser.FriendlyError(k, string.IsNullOrWhiteSpace(err) ? "Video bilgisi alınamadı." : err);
            AppLog.Write($"Analiz hatası ({k}, çıkış {r.ExitCode}): {friendly}",
                "Komut: " + YtDlpArgs.ToCommandLine("yt-dlp", args, CookiePath(args)) + Environment.NewLine + (detail.Length > 0 ? detail : "(stderr boş)"));
            throw new ProbeException(friendly, k, detail.Length > 0 ? detail : null);
        }
        return (JsonDocument.Parse(json), json, FormatDiagnostics.Warnings(r.StdErr));
    }

    /// <summary>Günlüğe yazılan komutta çerez dosyasının yolu gizlenir.</summary>
    private static string? CookiePath(List<string> args) => args.IndexOf("--cookies") is var i and >= 0 && i + 1 < args.Count ? args[i + 1] : null;

    public async Task<SessionTestResult> TestSessionAsync(CancellationToken ct = default)
    {
        var s = settings();
        if (s.Cookies is NoCookies) return new(false, "Çerez kullanılmıyor — yalnızca herkese açık videolar indirilebilir.");
        var r = await runner.RunAsync(tools.YtDlpPath, YtDlpArgs.SessionTest(s, tools.Paths), ct).ConfigureAwait(false);
        if (r.ExitCode == 0 && !string.IsNullOrWhiteSpace(r.StdOut))
            return new(true, $"Abonelik akışı okunabildi · {DateTime.Now:HH:mm}");
        var err = LastError(r.StdErr);
        var kind = OutputParser.Classify(err);
        return kind == ErrorKind.Auth
            ? new(false, "Oturum bulunamadı ya da süresi doldu. Yeniden giriş yap.", kind)
            : new(false, string.IsNullOrWhiteSpace(err) ? "Oturum doğrulanamadı." : err, kind);
    }

    private static string LastError(string stderr)
    {
        var lines = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).ToList();
        return lines.LastOrDefault(l => l.StartsWith("ERROR:", StringComparison.Ordinal))?["ERROR:".Length..].Trim()
            // ERROR yoksa en anlamlı uyarı (ör. 429) kullanıcıya gösterilir
            ?? lines.FirstOrDefault(l => l.Contains("HTTP Error", StringComparison.Ordinal))?.Replace("WARNING:", "").Trim()
            ?? stderr.Trim();
    }
}
