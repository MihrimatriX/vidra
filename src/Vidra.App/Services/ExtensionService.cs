using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Win32;
using Vidra.Core.Auth;
using Vidra.Core.Browser;
using Vidra.Core.Storage;
using Vidra.Core.YtDlp;

namespace Vidra.App.Services;

/// <summary>Tarayıcı eklentisi: native messaging host kaydı, eklenti dosyalarını dışa aktarma ve host modu.</summary>
public static class ExtensionService
{
    // Chromium tabanlı tarayıcıların kullanıcıya özel host kayıtları (yönetici izni gerekmez)
    private static readonly string[] HostKeys =
    [
        @"Software\Google\Chrome\NativeMessagingHosts",
        @"Software\Microsoft\Edge\NativeMessagingHosts",
        @"Software\BraveSoftware\Brave-Browser\NativeMessagingHosts",
        @"Software\Chromium\NativeMessagingHosts",
    ];

    /// <summary>Her açılışta: exe yolu değişmiş olabilir (taşınabilir sürüm, güncelleme).</summary>
    public static void RegisterHost(AppPaths paths)
    {
        if (Environment.ProcessPath is not { } exe) return;
        File.WriteAllText(paths.NativeHostManifest, JsonSerializer.Serialize(new
        {
            name = BrowserBridge.HostName,
            description = "Vidra",
            path = exe,
            type = "stdio",
            allowed_origins = new[] { BrowserBridge.Origin },
        }));
        foreach (var root in HostKeys)
        {
            using var k = Registry.CurrentUser.CreateSubKey($@"{root}\{BrowserBridge.HostName}");
            k.SetValue("", paths.NativeHostManifest);
        }
    }

    /// <summary>Gömülü eklenti dosyalarını "Paketlenmemiş öğe yükle" ile seçilecek klasöre çıkarır.</summary>
    public static string ExportExtension(AppPaths paths)
    {
        Directory.CreateDirectory(paths.ExtensionDir);
        var asm = typeof(ExtensionService).Assembly;
        foreach (var name in asm.GetManifestResourceNames().Where(n => n.StartsWith("ext/", StringComparison.Ordinal)))
        {
            using var src = asm.GetManifestResourceStream(name)!;
            using var dst = File.Create(Path.Combine(paths.ExtensionDir, name["ext/".Length..]));
            src.CopyTo(dst);
        }
        return paths.ExtensionDir;
    }

    /// <summary>Host modu: tarayıcı bağlantıyı kapatana kadar iletileri yanıtla. Pencere açılmaz.</summary>
    public static async Task<int> RunHostAsync()
    {
        var paths = new AppPaths();
        await using var stdin = Console.OpenStandardInput();
        await using var stdout = Console.OpenStandardOutput();
        while (await BrowserBridge.ReadAsync(stdin).ConfigureAwait(false) is { } doc)
            using (doc)
                await BrowserBridge.WriteAsync(stdout, await HandleAsync(doc.RootElement, paths).ConfigureAwait(false)).ConfigureAwait(false);
        return 0;
    }

    private static async Task<object> HandleAsync(JsonElement m, AppPaths paths)
    {
        switch (BrowserBridge.Str(m, "type"))
        {
            case "open":
                var url = BrowserBridge.Str(m, "url");
                if (url is null || YouTubeUrl.Classify(url) is null) return new { ok = false, error = "YouTube bağlantısı değil" };
                if (!App.ForwardToRunningInstance(url))
                    Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { ArgumentList = { url }, UseShellExecute = false });
                return new { ok = true };
            case "cookies":
                var cookies = m.TryGetProperty("cookies", out var arr) ? BrowserBridge.ParseCookies(arr) : new List<BrowserCookie>();
                var loggedIn = NetscapeCookies.LooksLoggedIn(cookies);
                // Döndürme sırasındaki ara durumlar (ya da tarayıcıdan çıkış) çalışan oturumu silmesin: yalnızca oturumlu kümeyi yaz
                if (loggedIn)
                {
                    await NetscapeCookies.SaveAsync(paths.ExtensionCookiesFile, cookies).ConfigureAwait(false);
                    App.ForwardToRunningInstance(BrowserBridge.CookiesSyncedSignal);
                }
                return new { ok = true, loggedIn };
            default:
                return new { ok = true };
        }
    }
}
