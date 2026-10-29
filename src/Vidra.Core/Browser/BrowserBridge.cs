using System.Buffers.Binary;
using System.Text.Json;
using Vidra.Core.Auth;

namespace Vidra.Core.Browser;

/// <summary>
/// Tarayıcı eklentisiyle konuşma (Chrome/Edge native messaging): tarayıcı Vidra.exe'yi başlatır ve stdin/stdout
/// üzerinden [4 bayt uzunluk, little-endian][UTF-8 JSON] çerçeveleriyle konuşur. Hangi eklentinin bağlanabileceğini
/// tarayıcı, host manifest'indeki allowed_origins ile kendisi denetler.
/// </summary>
public static class BrowserBridge
{
    public const string HostName = "com.vidra.app";

    /// <summary>Eklenti manifest'indeki "key"den türeyen sabit kimlik (bkz. Vidra.App/Extension/manifest.json).</summary>
    public const string ExtensionId = "ailoipppadkfdjklbpkmapmafihclmjd";

    public static string Origin => $"chrome-extension://{ExtensionId}/";

    /// <summary>Eklenti açık uygulamaya bu satırı iletir: "çerezler yenilendi, eklenti oturumuna geç".</summary>
    public const string CookiesSyncedSignal = "vidra:cookies-synced";

    /// <summary>Çerez listesi birkaç KB tutar; sınır bozuk/kötü niyetli bir uzunlukla dev bellek ayırmayı önler.</summary>
    private const int MaxMessage = 1 << 20;

    /// <returns>İleti; tarayıcı bağlantıyı kapattıysa null.</returns>
    public static async Task<JsonDocument?> ReadAsync(Stream s, CancellationToken ct = default)
    {
        var head = new byte[4];
        if (!await ReadExactAsync(s, head, ct).ConfigureAwait(false)) return null;
        var len = BinaryPrimitives.ReadInt32LittleEndian(head);
        if (len is <= 0 or > MaxMessage) throw new InvalidDataException($"Geçersiz ileti uzunluğu: {len}");
        var body = new byte[len];
        if (!await ReadExactAsync(s, body, ct).ConfigureAwait(false)) throw new EndOfStreamException();
        return JsonDocument.Parse(body);
    }

    public static async Task WriteAsync(Stream s, object message, CancellationToken ct = default)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(message);
        var head = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(head, body.Length);
        await s.WriteAsync(head, ct).ConfigureAwait(false);
        await s.WriteAsync(body, ct).ConfigureAwait(false);
        await s.FlushAsync(ct).ConfigureAwait(false);
    }

    private static async Task<bool> ReadExactAsync(Stream s, byte[] buf, CancellationToken ct)
    {
        var read = 0;
        while (read < buf.Length)
        {
            var n = await s.ReadAsync(buf.AsMemory(read), ct).ConfigureAwait(false);
            if (n == 0) return read == 0 ? false : throw new EndOfStreamException();
            read += n;
        }
        return true;
    }

    /// <summary>
    /// chrome.cookies.Cookie dizisi → BrowserCookie. Chrome alan adını zaten Netscape kuralıyla verir
    /// (alt alan adlarını kapsayan çerezde baştaki nokta); expirationDate ondalıklı saniye, oturum çerezinde yok.
    /// </summary>
    public static List<BrowserCookie> ParseCookies(JsonElement arr)
    {
        var list = new List<BrowserCookie>();
        if (arr.ValueKind != JsonValueKind.Array) return list;
        foreach (var c in arr.EnumerateArray())
        {
            if (c.ValueKind != JsonValueKind.Object || Str(c, "name") is not { Length: > 0 } name || Str(c, "domain") is not { Length: > 0 } domain) continue;
            DateTimeOffset? exp = c.TryGetProperty("expirationDate", out var e) && e.ValueKind == JsonValueKind.Number
                ? DateTimeOffset.FromUnixTimeMilliseconds((long)(e.GetDouble() * 1000))
                : null;
            list.Add(new BrowserCookie(name, Str(c, "value") ?? "", domain, Str(c, "path") ?? "/", Bool(c, "secure"), Bool(c, "httpOnly"), exp));
        }
        return list;
    }

    public static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Bool(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}
