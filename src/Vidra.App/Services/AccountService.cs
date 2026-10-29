using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Vidra.App.Views;
using Vidra.Core.Auth;
using Vidra.Core.Models;
using Vidra.Core.Storage;

namespace Vidra.App.Services;

/// <summary>
/// YouTube oturumları: uygulama içi girişle eklenen hesaplar (her birinin kendi WebView2 profili ve çerez dosyası)
/// ve tarayıcı eklentisinin eşitlediği oturum. Etkin oturum AppSettings.Cookies'tir.
/// </summary>
public sealed class AccountService(AppPaths paths, SettingsStore settings)
{
    public static string NewId() => Guid.NewGuid().ToString("N")[..8];

    public SavedAccount? Active => settings.Current.Cookies is AppLoginCookies a
        ? settings.Current.Accounts.FirstOrDefault(x => string.Equals(paths.AccountCookiesFile(x.Id), a.Path, StringComparison.OrdinalIgnoreCase))
        : null;

    public async Task<LoginResult> SignInAsync(Window owner, string id)
    {
        var win = new LoginWindow(paths.AccountProfileDir(id)) { Owner = owner };
        win.ShowDialog();
        var r = win.Result;
        if (r.Outcome != LoginOutcome.Success) return r;

        var file = paths.AccountCookiesFile(id);
        await NetscapeCookies.SaveAsync(file, r.Cookies);
        var expires = NetscapeCookies.SessionExpiry(r.Cookies);
        settings.Update(s =>
        {
            var i = s.Accounts.FindIndex(a => a.Id == id);
            var acc = new SavedAccount(id, i >= 0 ? s.Accounts[i].Name : $"Hesap {s.Accounts.Count + 1}", DateTimeOffset.Now, expires);
            if (i >= 0) s.Accounts[i] = acc;
            else s.Accounts.Add(acc);
            s.Cookies = new AppLoginCookies(file, acc.ExportedAt, expires);
        });
        return r;
    }

    public void Use(SavedAccount a)
    {
        var file = paths.AccountCookiesFile(a.Id);
        if (File.Exists(file)) settings.Update(s => s.Cookies = new AppLoginCookies(file, a.ExportedAt, a.ExpiresAt));
    }

    public void Rename(string id, string name) => settings.Update(s =>
    {
        var i = s.Accounts.FindIndex(a => a.Id == id);
        if (i >= 0 && name.Trim().Length > 0) s.Accounts[i] = s.Accounts[i] with { Name = name.Trim() };
    });

    /// <summary>Hesabı ve dosyalarını siler; etkin hesapsa sıradaki hesaba (yoksa oturumsuz) geçer.</summary>
    public async Task RemoveAsync(SavedAccount a)
    {
        var wasActive = Active?.Id == a.Id;
        settings.Update(s =>
        {
            s.Accounts.RemoveAll(x => x.Id == a.Id);
            if (wasActive) s.Cookies = new NoCookies();
        });
        if (wasActive && settings.Current.Accounts.FirstOrDefault() is { } next) Use(next);
        TryDelete(paths.AccountCookiesFile(a.Id));
        DeleteCopies();
        await DeleteDirAsync(paths.AccountProfileDir(a.Id));
        if (a.Id != "default") await DeleteDirAsync(Path.GetDirectoryName(paths.AccountCookiesFile(a.Id))!);
    }

    /// <summary>Etkin oturumu bırak. Uygulama içi hesapta hesabı siler; eklenti oturumunda kendiliğinden geri geçişi kapatır.</summary>
    public async Task SignOutAsync()
    {
        if (Active is { } a)
        {
            await RemoveAsync(a);
            return;
        }
        var ext = settings.Current.Cookies is ExtensionCookies;
        settings.Update(s =>
        {
            s.Cookies = new NoCookies();
            if (ext) s.ExtensionSync = false;
        });
        DeleteCopies();
    }

    public void UseExtension() => settings.Update(s =>
    {
        s.Cookies = new ExtensionCookies(paths.ExtensionCookiesFile);
        s.ExtensionSync = true;
    });

    /// <summary>Eklenti çerez gönderdi: oturumsuzken eklenti oturumuna geç; kullanıcının başka bir seçimini ezme.</summary>
    public void OnExtensionSynced()
    {
        var s = settings.Current;
        if (s.ExtensionSync && s.Cookies is NoCookies && File.Exists(paths.ExtensionCookiesFile)) UseExtension();
        // Zaten eklentideyse "son eşitleme" satırları yenilensin
        else if (s.Cookies is ExtensionCookies) settings.Update(_ => { });
    }

    private void DeleteCopies()
    {
        // Süreç kopyaları da oturumu taşır; çalışan bir indirmenin açık tuttuğu kopyayı süreç bitince kendisi siler
        if (Directory.Exists(paths.CookieCopyDir))
            foreach (var f in Directory.EnumerateFiles(paths.CookieCopyDir)) TryDelete(f);
    }

    private static async Task DeleteDirAsync(string dir)
    {
        // WebView2 süreci kapanana kadar profil klasörü kilitli olabilir: birkaç kez dene
        for (var i = 0; i < 10 && Directory.Exists(dir); i++)
        {
            try { Directory.Delete(dir, recursive: true); }
            catch (IOException) { await Task.Delay(300); }
            catch (UnauthorizedAccessException) { await Task.Delay(300); }
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* yoksay */ }
    }
}
