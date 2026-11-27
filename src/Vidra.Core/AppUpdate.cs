using System.Text.Json;

namespace Vidra.Core;

/// <summary>Sürüme eklenmiş dosya. Sha256: GitHub'ın verdiği "digest" (sha256:hex), eski sürümlerde yok.</summary>
public sealed record ReleaseAsset(string Name, string Url, long Size, string? Sha256);

/// <summary>GitHub'daki son Vidra sürümü (taslak ve ön sürümler hariç: releases/latest).</summary>
public sealed record AppRelease(Version Version, string Tag, string Url, string? Notes, IReadOnlyList<ReleaseAsset> Assets)
{
    public AppRelease(Version version, string tag, string url, string? notes) : this(version, tag, url, notes, []) { }

    public ReleaseAsset? Asset(string name) => Assets.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Vidra nasıl çalışıyor: Inno kurulumu (yanında unins000.exe) ya da tek başına taşınabilir exe.</summary>
public enum InstallKind { Installed, Portable }

public static class AppUpdate
{
    public const string LatestApi = "https://api.github.com/repos/MihrimatriX/vidra/releases/latest";
    public const string SetupAsset = "Vidra-Setup.exe";
    public const string PortableAsset = "Vidra-Portable.exe";

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
        return new AppRelease(v, tag, url ?? "https://github.com/MihrimatriX/vidra/releases/latest", notes, ParseAssets(r));
    }

    private static List<ReleaseAsset> ParseAssets(JsonElement r)
    {
        var list = new List<ReleaseAsset>();
        if (!r.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return list;
        foreach (var a in assets.EnumerateArray())
        {
            if (a.ValueKind != JsonValueKind.Object) continue;
            var name = Str(a, "name");
            var url = Str(a, "browser_download_url");
            // Yalnızca GitHub'dan indir: API yanıtı bozuk ya da değiştirilmişse başka bir adrese gidilmesin
            if (name is null || url is null || !IsGitHubDownload(url)) continue;
            var size = a.TryGetProperty("size", out var s) && s.TryGetInt64(out var n) ? n : 0;
            list.Add(new ReleaseAsset(name, url, size, ParseDigest(Str(a, "digest"))));
        }
        return list;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static bool IsGitHubDownload(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps && u.Host == "github.com"
        && u.AbsolutePath.StartsWith("/MihrimatriX/vidra/releases/download/", StringComparison.OrdinalIgnoreCase);

    /// <summary>"sha256:ABC…" → "abc…" (64 onaltılık hane); başka algoritma ya da bozuk değer null.</summary>
    public static string? ParseDigest(string? digest)
    {
        if (digest is null || !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) return null;
        var hex = digest["sha256:".Length..].Trim().ToLowerInvariant();
        return hex.Length == 64 && hex.All(Uri.IsHexDigit) ? hex : null;
    }

    /// <summary>"v0.4.0", "0.4", "v1.2.3-beta" → 0.4.0 / 0.4.0 / 1.2.3 (ön ek ve ek atılır, eksik parça 0).</summary>
    public static Version? ParseVersion(string tag)
    {
        var s = tag.Trim().TrimStart('v', 'V').Split('-', '+')[0];
        return Version.TryParse(s.Contains('.') ? s : s + ".0", out var v) ? Normalize(v) : null;
    }

    public static bool IsNewer(AppRelease r, Version current) => r.Version > Normalize(current);

    /// <summary>Inno kurulumu exe'nin yanına unins000.exe bırakır; yoksa taşınabilir sürümdür.</summary>
    public static InstallKind DetectInstallKind(string exeDir) =>
        File.Exists(Path.Combine(exeDir, "unins000.exe")) ? InstallKind.Installed : InstallKind.Portable;

    /// <summary>Bu kurulum türü için indirilecek dosya; sürümde yoksa (ya da bağlantısı GitHub dışıysa) null.</summary>
    public static ReleaseAsset? AssetFor(AppRelease r, InstallKind kind) =>
        r.Asset(kind == InstallKind.Installed ? SetupAsset : PortableAsset);

    /// <summary>
    /// Sessiz güncelleme argümanları. Kurulumun yetki kipi açıkça verilir: Program Files'taki (tüm kullanıcılar) kurulum
    /// yönetici izni ister, kullanıcı kurulumu istemez; böylece yanlışlıkla ikinci bir kopya kurulmaz.
    /// /RELAUNCH: vidra.iss yalnızca bu argümanla kurulumdan sonra Vidra'yı yeniden açar (winget gibi sessiz kurulumlar açmaz).
    /// </summary>
    public static string SetupArguments(bool allUsers) =>
        "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /RELAUNCH " + (allUsers ? "/ALLUSERS" : "/CURRENTUSER");

    private static Version Normalize(Version v) => new(v.Major, Math.Max(0, v.Minor), Math.Max(0, v.Build));
}
