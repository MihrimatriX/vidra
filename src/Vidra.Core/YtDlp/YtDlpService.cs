using System.Text.Json;
using Vidra.Core.Models;
using Vidra.Core.Process;
using Vidra.Core.Tools;

namespace Vidra.Core.YtDlp;

public sealed class ProbeException(string message, ErrorKind kind) : Exception(message)
{
    public ErrorKind Kind { get; } = kind;
}

public sealed record SessionTestResult(bool Ok, string Message, ErrorKind? Kind = null);

/// <summary>Kısa ömürlü yt-dlp çağrıları: bilgi alma (probe) ve oturum testi.</summary>
public sealed class YtDlpService(IProcessRunner runner, ToolManager tools, Func<AppSettings> settings)
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

        using var doc = await RunJsonAsync(YtDlpArgs.Probe(url, kind, settings(), tools.Paths, limit), ct).ConfigureAwait(false);
        var root = doc.RootElement;
        if (kind == LinkKind.Video || !ProbeParser.IsPlaylist(root)) return ProbeParser.ParseVideo(root);
        return ProbeParser.ParsePlaylist(root, kind);
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

    private async Task<JsonDocument> RunJsonAsync(List<string> args, CancellationToken ct)
    {
        var r = await runner.RunAsync(tools.YtDlpPath, args, ct).ConfigureAwait(false);
        var json = r.StdOut.Trim();
        // Çıkarım başarısızsa yt-dlp stdout'a "null" basabilir: bunu da hata say
        if (json.Length == 0 || json == "null" || json[0] != '{')
        {
            var err = LastError(r.StdErr);
            if (OutputParser.MissingTab(err) is { } tab) throw new ProbeException($"Bu kanalda {tab.Label()} sekmesi yok.", ErrorKind.Unavailable);
            var k = OutputParser.Classify(err);
            throw new ProbeException(OutputParser.FriendlyError(k, string.IsNullOrWhiteSpace(err) ? "Video bilgisi alınamadı." : err), k);
        }
        return JsonDocument.Parse(json);
    }

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
