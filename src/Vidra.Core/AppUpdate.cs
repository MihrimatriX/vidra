using System.Text.Json;

namespace Vidra.Core;

/// <summary>GitHub'daki son Vidra sürümü (taslak ve ön sürümler hariç: releases/latest).</summary>
public sealed record AppRelease(Version Version, string Tag, string Url, string? Notes);

public static class AppUpdate
{
    public const string LatestApi = "https://api.github.com/repos/MihrimatriX/vidra/releases/latest";

    /// <summary>Hata (ağ, oran sınırı, sürüm yok) sessizce null: güncelleme denetimi uygulamayı hiçbir zaman bozmamalı.</summary>
    public static async Task<AppRelease?> CheckAsync(HttpClient http, CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, LatestApi);
            req.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var res = await http.SendAsync(req, ct).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode) return null;
            return Parse(await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException) { return null; }
    }

    public static AppRelease? Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        if (r.ValueKind != JsonValueKind.Object || !r.TryGetProperty("tag_name", out var t) || t.GetString() is not { } tag) return null;
        if (ParseVersion(tag) is not { } v) return null;
        var url = r.TryGetProperty("html_url", out var u) ? u.GetString() : null;
        var notes = r.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() : null;
        return new AppRelease(v, tag, url ?? "https://github.com/MihrimatriX/vidra/releases/latest", notes);
    }

    /// <summary>"v0.4.0", "0.4", "v1.2.3-beta" → 0.4.0 / 0.4.0 / 1.2.3 (ön ek ve ek atılır, eksik parça 0).</summary>
    public static Version? ParseVersion(string tag)
    {
        var s = tag.Trim().TrimStart('v', 'V').Split('-', '+')[0];
        return Version.TryParse(s.Contains('.') ? s : s + ".0", out var v) ? Normalize(v) : null;
    }

    public static bool IsNewer(AppRelease r, Version current) => r.Version > Normalize(current);

    private static Version Normalize(Version v) => new(v.Major, Math.Max(0, v.Minor), Math.Max(0, v.Build));
}
