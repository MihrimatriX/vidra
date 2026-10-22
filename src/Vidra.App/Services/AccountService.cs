using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using Vidra.App.Views;
using Vidra.Core.Auth;
using Vidra.Core.Models;
using Vidra.Core.Storage;

namespace Vidra.App.Services;

/// <summary>Uygulama içi YouTube girişi: pencereyi açar, çerezleri dışa aktarır, ayarlara bağlar; çıkışta hepsini siler.</summary>
public sealed class AccountService(AppPaths paths, SettingsStore settings)
{
    public async Task<LoginResult> SignInAsync(Window owner)
    {
        var win = new LoginWindow(paths.LoginProfileDir) { Owner = owner };
        win.ShowDialog();
        var r = win.Result;
        if (r.Outcome != LoginOutcome.Success) return r;

        await NetscapeCookies.SaveAsync(paths.AppCookiesFile, r.Cookies);
        var expires = NetscapeCookies.SessionExpiry(r.Cookies);
        settings.Update(s => s.Cookies = new AppLoginCookies(paths.AppCookiesFile, DateTimeOffset.Now, expires));
        return r;
    }

    public async Task SignOutAsync()
    {
        settings.Update(s => s.Cookies = new NoCookies());
        TryDelete(paths.AppCookiesFile);
        // WebView2 süreci kapanana kadar profil klasörü kilitli olabilir: birkaç kez dene
        for (var i = 0; i < 10 && Directory.Exists(paths.LoginProfileDir); i++)
        {
            try { Directory.Delete(paths.LoginProfileDir, recursive: true); }
            catch (IOException) { await Task.Delay(300); }
            catch (UnauthorizedAccessException) { await Task.Delay(300); }
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* yoksay */ }
    }
}
