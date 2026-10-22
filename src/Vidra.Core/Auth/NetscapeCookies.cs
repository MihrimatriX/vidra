using System.Globalization;
using System.Text;

namespace Vidra.Core.Auth;

/// <summary>Tarayıcıdan bağımsız çerez kaydı (WebView2'nin CoreWebView2Cookie'si buna çevrilir).</summary>
public sealed record BrowserCookie(
    string Name,
    string Value,
    string Domain,
    string Path,
    bool Secure,
    bool HttpOnly,
    DateTimeOffset? Expires);

/// <summary>
/// yt-dlp'nin --cookies ile okuduğu Netscape/Mozilla cookies.txt biçimi.
/// Satır: domain \t alt-alan-adları \t yol \t güvenli \t bitiş(unix) \t ad \t değer
/// </summary>
public static class NetscapeCookies
{
    /// <summary>yt-dlp'nin YouTube oturumunu tanıması için gereken ana çerezler.</summary>
    public static readonly string[] SessionCookieNames = ["SAPISID", "__Secure-3PAPISID", "__Secure-3PSID", "SID", "LOGIN_INFO"];

    public static bool LooksLoggedIn(IEnumerable<BrowserCookie> cookies)
    {
        var names = cookies.Where(c => IsRelevant(c.Domain)).Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
        return (names.Contains("SAPISID") || names.Contains("__Secure-3PAPISID")) && (names.Contains("SID") || names.Contains("__Secure-3PSID"));
    }

    /// <summary>Yalnızca YouTube/Google çerezlerini dışa aktar — gereksiz oturum bilgisi taşıma.</summary>
    public static bool IsRelevant(string domain)
    {
        var d = domain.TrimStart('.').ToLowerInvariant();
        return d is "youtube.com" or "google.com" || d.EndsWith(".youtube.com", StringComparison.Ordinal) || d.EndsWith(".google.com", StringComparison.Ordinal);
    }

    public static string Write(IEnumerable<BrowserCookie> cookies)
    {
        var sb = new StringBuilder();
        sb.Append("# Netscape HTTP Cookie File\n");
        sb.Append("# Vidra tarafından oluşturuldu — bu dosya YouTube oturumuna tam erişim verir, paylaşma.\n\n");
        foreach (var c in cookies.Where(c => IsRelevant(c.Domain)).OrderBy(c => c.Domain).ThenBy(c => c.Name))
        {
            // Sekme ve satır sonu biçimi bozar; çerez değerlerinde olmamalı ama yine de temizle
            static string Clean(string s) => s.Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');
            var domain = c.Domain;
            var includeSub = domain.StartsWith('.');
            var expiry = c.Expires is { } e ? e.ToUnixTimeSeconds() : 0; // 0 = oturum çerezi
            if (c.HttpOnly) sb.Append("#HttpOnly_");
            sb.Append(domain).Append('\t')
              .Append(includeSub ? "TRUE" : "FALSE").Append('\t')
              .Append(string.IsNullOrEmpty(c.Path) ? "/" : c.Path).Append('\t')
              .Append(c.Secure ? "TRUE" : "FALSE").Append('\t')
              .Append(expiry.ToString(CultureInfo.InvariantCulture)).Append('\t')
              .Append(Clean(c.Name)).Append('\t')
              .Append(Clean(c.Value)).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>Oturumun en erken sona erecek ana çerezinin bitişi — arayüzde "oturum şu tarihe kadar geçerli".</summary>
    public static DateTimeOffset? SessionExpiry(IEnumerable<BrowserCookie> cookies) =>
        cookies.Where(c => IsRelevant(c.Domain) && SessionCookieNames.Contains(c.Name) && c.Expires is not null)
               .Select(c => c.Expires!.Value)
               .DefaultIfEmpty()
               .Min() is var m && m != default ? m : null;

    /// <summary>Dosyayı yalnızca geçerli kullanıcının okuyabileceği şekilde yazar (Unix'te 600).</summary>
    public static async Task SaveAsync(string path, IEnumerable<BrowserCookie> cookies, CancellationToken ct = default)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        await File.WriteAllTextAsync(tmp, Write(cookies), new UTF8Encoding(false), ct).ConfigureAwait(false);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(tmp, path, overwrite: true);
    }
}
