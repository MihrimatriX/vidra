using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Vidra.App.Services;
using Vidra.Core.Auth;

namespace Vidra.App.Views;

public enum LoginOutcome { Success, Canceled, UseBrowser, Failed }

public sealed record LoginResult(LoginOutcome Outcome, IReadOnlyList<BrowserCookie> Cookies, string? Error = null);

/// <summary>
/// WebView2 içinde Google/YouTube girişi. Ayrı bir kullanıcı veri klasörü kullanır, böylece oturum
/// kullanıcının tarayıcısından bağımsızdır ve YouTube'un çerez yenilemesi onu geçersiz kılmaz.
/// Başarılı girişte youtube.com/google.com çerezleri okunur ve pencere kapanır.
/// </summary>
public partial class LoginWindow : Window
{
    private const string StartUrl = "https://accounts.google.com/ServiceLogin?service=youtube&continue=https%3A%2F%2Fwww.youtube.com%2F";
    private const string RobotsPath = "/robots.txt";

    private readonly string _profileDir;
    private bool _finishing;

    public LoginResult Result { get; private set; } = new(LoginOutcome.Canceled, []);

    public LoginWindow(string profileDir)
    {
        _profileDir = profileDir;
        InitializeComponent();
        SourceInitialized += (_, _) => ThemeService.ApplyTitleBar(this);
        Loaded += async (_, _) => await InitAsync();
    }

    private async Task InitAsync()
    {
        try
        {
            var env = await CoreWebView2Environment.CreateAsync(userDataFolder: _profileDir);
            await Web.EnsureCoreWebView2Async(env);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            Finish(new LoginResult(LoginOutcome.Failed, [], "WebView2 çalışma zamanı bulunamadı. Microsoft Edge WebView2 Runtime'ı kur ya da tarayıcıdan çerez al."));
            return;
        }
        catch (Exception e)
        {
            // Ör. profil klasörü başka bir süreçte açık (COMException)
            Finish(new LoginResult(LoginOutcome.Failed, [], $"Giriş penceresi açılamadı: {e.Message}"));
            return;
        }
        if (_finishing || !IsLoaded) return;

        var core = Web.CoreWebView2;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
        // Açılır pencereleri (ör. "hesap seç") aynı görünümde aç
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            core.Navigate(e.Uri);
        };
        core.NavigationCompleted += async (_, _) => await OnNavigatedAsync();
        core.Navigate(StartUrl);
    }

    private bool _checking;

    private async Task OnNavigatedAsync()
    {
        Loading.Visibility = Visibility.Collapsed;
        // Yönlendirme zincirinde art arda birden çok NavigationCompleted gelir: aynı anda tek kontrol
        if (_finishing || _checking || Web.CoreWebView2 is null) return;
        _checking = true;
        try { await CheckPageAsync(); }
        catch (Exception) when (_finishing || !IsLoaded) { /* pencere bu arada kapandı */ }
        finally { _checking = false; }
    }

    private async Task CheckPageAsync()
    {
        var uri = new Uri(Web.CoreWebView2.Source);

        var rejected = await LooksRejectedAsync(uri);
        if (_finishing || !IsLoaded) return;
        if (rejected)
        {
            Rejected.Visibility = Visibility.Visible;
            Status.Text = "Giriş engellendi";
            return;
        }

        if (!uri.Host.EndsWith("youtube.com", StringComparison.OrdinalIgnoreCase)) return;

        var cookies = (await Web.CoreWebView2.CookieManager.GetCookiesAsync("https://www.youtube.com")).Select(Convert).ToList();
        if (_finishing || !IsLoaded) return;
        if (!NetscapeCookies.LooksLoggedIn(cookies))
        {
            // YouTube'a giriş yapmadan gelindi (ör. "atla"): giriş sayfasına geri yönlendir
            Status.Text = "YouTube açık ama oturum yok — giriş yap";
            return;
        }

        // YouTube sayfası açıkken JS oturum çerezlerini döndürür (__Secure-*PSIDTS); dışa aktarılan kopya hemen bayatlar
        // ve Google oturumu kapatır. Çerezleri JS çalışmayan robots.txt'de al (yt-dlp'nin önerdiği yöntem).
        if (!uri.AbsolutePath.Equals(RobotsPath, StringComparison.OrdinalIgnoreCase))
        {
            Status.Text = "Oturum hazırlanıyor…";
            Web.CoreWebView2.Navigate("https://www.youtube.com" + RobotsPath);
            return;
        }

        // google.com çerezleri de yt-dlp'nin bazı akışlarında gerekiyor
        var google = (await Web.CoreWebView2.CookieManager.GetCookiesAsync("https://accounts.google.com")).Select(Convert);
        if (_finishing || !IsLoaded) return;
        var all = cookies.Concat(google).GroupBy(c => (c.Domain, c.Name, c.Path)).Select(g => g.First()).ToList();
        Status.Text = "Giriş başarılı, oturum kaydediliyor…";
        Finish(new LoginResult(LoginOutcome.Success, all));
    }

    private async Task<bool> LooksRejectedAsync(Uri uri)
    {
        if (uri.AbsolutePath.Contains("/signin/rejected", StringComparison.OrdinalIgnoreCase)) return true;
        if (!uri.Host.EndsWith("accounts.google.com", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            var text = await Web.CoreWebView2.ExecuteScriptAsync("document.body ? document.body.innerText.slice(0, 4000) : ''");
            return text.Contains("may not be secure", StringComparison.OrdinalIgnoreCase)
                || text.Contains("güvenli olmayabilir", StringComparison.OrdinalIgnoreCase)
                || text.Contains("Couldn't sign you in", StringComparison.OrdinalIgnoreCase)
                || text.Contains("Oturumunuz açılamadı", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; } // sayfa geçişi sırasında betik çalışmayabilir
    }

    private static BrowserCookie Convert(CoreWebView2Cookie c) => new(
        c.Name, c.Value, c.Domain, c.Path, c.IsSecure, c.IsHttpOnly,
        c.IsSession ? null : new DateTimeOffset(c.Expires.ToUniversalTime(), TimeSpan.Zero));

    private void Finish(LoginResult r)
    {
        if (_finishing) return;
        _finishing = true;
        Result = r;
        // ShowDialog ile açıldığı için DialogResult atamak pencereyi kapatır
        DialogResult = r.Outcome == LoginOutcome.Success;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Finish(new LoginResult(LoginOutcome.Canceled, []));

    private void UseBrowser_Click(object sender, RoutedEventArgs e) => Finish(new LoginResult(LoginOutcome.UseBrowser, []));

    private void Retry_Click(object sender, RoutedEventArgs e)
    {
        Rejected.Visibility = Visibility.Collapsed;
        Status.Text = "Google hesabınla giriş yap";
        Web.CoreWebView2?.Navigate(StartUrl);
    }

    protected override void OnClosed(EventArgs e)
    {
        Web.Dispose();
        base.OnClosed(e);
    }
}
