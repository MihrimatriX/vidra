using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vidra.App.Services;
using Vidra.App.Views;
using Vidra.Core;
using Vidra.Core.Models;
using Vidra.Core.YtDlp;

namespace Vidra.App.ViewModels;

public sealed record FeedItem(string Key, string Label, string Hint);

/// <summary>Hesaplar listesindeki satır; ad kutusu odaktan çıkınca kaydedilir.</summary>
public sealed partial class AccountRow(SavedAccount account, bool active, Action<string, string> rename) : ObservableObject
{
    public SavedAccount Account { get; } = account;
    public bool Active { get; } = active;
    public string Detail => $"{Fmt.Relative(Account.ExportedAt)} giriş yapıldı" +
                            (Account.ExpiresAt is { } e ? $" · {e.LocalDateTime:d MMM yyyy} tarihine kadar geçerli" : "");
    [ObservableProperty] private string _name = account.Name;
    partial void OnNameChanged(string value) => rename(Account.Id, value);
}

public sealed partial class AccountViewModel : ObservableObject
{
    private readonly AppHost _host;
    private readonly MainViewModel _main;

    public AccountViewModel(AppHost host, MainViewModel main)
    {
        _host = host;
        _main = main;
        host.SettingsStore.Changed += (_, _) => RefreshState();
        RefreshState();
    }

    public IReadOnlyList<FeedItem> Feeds { get; } = YouTubeUrl.Feeds.Select(f => new FeedItem(f.Key, f.Label, f.Hint)).ToList();
    public ObservableCollection<FollowedSource> Follows => _host.Follow.Items;
    public ObservableCollection<AccountRow> Accounts { get; } = [];

    // Tarayıcı eklentisi
    [ObservableProperty] private bool _usesExtension;
    [ObservableProperty] private bool _extensionSynced;
    [ObservableProperty] private string _extensionState = "";
    [ObservableProperty] private bool _extensionHelp;
    public IReadOnlyList<Choice> FollowIntervals => Choices.FollowIntervals;

    [ObservableProperty] private bool _signedIn;
    [ObservableProperty] private bool _usesAppLogin;
    [ObservableProperty] private string _stateTitle = "";
    [ObservableProperty] private string _stateDetail = "";
    [ObservableProperty] private string? _testMessage;
    [ObservableProperty] private bool? _testOk;
    [ObservableProperty] private bool _busy;
    [ObservableProperty] private string? _notice;

    public bool PoliteMode
    {
        get => _host.Settings.PoliteMode;
        set { _host.SettingsStore.Update(s => s.PoliteMode = value); OnPropertyChanged(); }
    }

    public int FollowInterval
    {
        get => _host.Settings.FollowIntervalHours;
        set { _host.SettingsStore.Update(s => s.FollowIntervalHours = value); OnPropertyChanged(); }
    }

    private void RefreshState()
    {
        var c = _host.Settings.Cookies;
        SignedIn = c is not NoCookies;
        UsesAppLogin = c is AppLoginCookies;
        UsesExtension = c is ExtensionCookies;
        var ext = _host.Paths.ExtensionCookiesFile;
        ExtensionSynced = File.Exists(ext);
        ExtensionState = ExtensionSynced ? $"Son eşitleme {Fmt.Relative(File.GetLastWriteTime(ext))}" : "Eklenti henüz bağlanmadı";

        var active = _host.Account.Active?.Id;
        Accounts.Clear();
        foreach (var acc in _host.Settings.Accounts) Accounts.Add(new AccountRow(acc, acc.Id == active, _host.Account.Rename));

        (StateTitle, StateDetail) = c switch
        {
            ExtensionCookies => ("Tarayıcı oturumu kullanılıyor", $"Vidra eklentisi YouTube oturumunu tarayıcından eşitliyor · {ExtensionState}"),
            AppLoginCookies a => (_host.Account.Active is { } cur ? $"{cur.Name} bağlı" : "YouTube hesabı bağlı",
                $"Vidra oturumu · {Fmt.Relative(a.ExportedAt)} kaydedildi" +
                (a.ExpiresAt is { } e ? $" · {e.LocalDateTime:d MMM yyyy} tarihine kadar geçerli" : "")),
            BrowserCookies b => ($"{b.Browser.Label()} oturumu kullanılıyor",
                b.Browser.IsChromium() ? "Chromium tabanlı tarayıcılar açıkken çerezleri kilitleyebilir; indirmeden önce kapat." : "Tarayıcıda YouTube'a giriş yapılmış olmalı."),
            FileCookies f => ("cookies.txt kullanılıyor", f.Path),
            _ => ("Giriş yapılmadı", "Yalnızca herkese açık videolar indirilebilir. Üyelere özel videolar ve hesap akışları için giriş yap."),
        };
    }

    public async Task RefreshAsync()
    {
        RefreshState();
        if (SignedIn && TestOk is null) await TestSession();
    }

    /// <summary>Giriş ya da etkin hesabı yenileme. İlk hesap "default" kimliğini alır (eski kurulumlarla aynı dosyalar).</summary>
    [RelayCommand]
    private Task SignIn() => SignInTo(_host.Account.Active?.Id ?? (_host.Settings.Accounts.Count == 0 ? "default" : AccountService.NewId()));

    [RelayCommand] private Task AddAccount() => SignInTo(AccountService.NewId());

    [RelayCommand]
    private async Task UseAccount(object? p)
    {
        if (p is not AccountRow r) return;
        _host.Account.Use(r.Account);
        TestOk = null;
        await TestSession();
    }

    [RelayCommand] private Task RemoveAccount(object? p) => p is AccountRow r ? _host.Account.RemoveAsync(r.Account) : Task.CompletedTask;

    /// <summary>Gömülü eklentiyi klasöre çıkarır ve klasörü açar; tarayıcıda "Paketlenmemiş öğe yükle" ile seçilir.</summary>
    [RelayCommand]
    private void InstallExtension()
    {
        try
        {
            var dir = ExtensionService.ExportExtension(_host.Paths);
            Process.Start("explorer.exe", $"\"{dir}\"");
            ExtensionHelp = true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Notice = e.Message; }
    }

    [RelayCommand]
    private async Task UseExtension()
    {
        if (!ExtensionSynced) { Notice = "Eklenti henüz çerez göndermedi: tarayıcıda YouTube'a giriş yapmış olduğundan emin ol."; return; }
        _host.Account.UseExtension();
        TestOk = null;
        await TestSession();
    }

    private async Task SignInTo(string id)
    {
        Notice = null;
        var r = await _host.Account.SignInAsync(Application.Current.MainWindow!, id);
        switch (r.Outcome)
        {
            case LoginOutcome.Success:
                TestOk = null;
                await TestSession();
                break;
            case LoginOutcome.UseBrowser:
                _main.Go(Page.Settings);
                _main.Settings.Notice = "Google uygulama içi girişe izin vermedi. Aşağıdan YouTube'a giriş yaptığın tarayıcıyı seç.";
                break;
            case LoginOutcome.Failed:
                Notice = r.Error;
                break;
        }
    }

    [RelayCommand]
    private async Task SignOut()
    {
        await _host.Account.SignOutAsync();
        TestOk = null;
        TestMessage = null;
    }

    [RelayCommand]
    private async Task TestSession()
    {
        Busy = true;
        try
        {
            var r = await _host.YtDlp.TestSessionAsync();
            TestOk = r.Ok;
            TestMessage = r.Message;
        }
        catch (Exception e)
        {
            TestOk = false;
            TestMessage = e.Message;
        }
        finally { Busy = false; }
    }

    [RelayCommand]
    private async Task OpenFeed(object? p)
    {
        if (p is not FeedItem f) return;
        if (!SignedIn) { Notice = "Hesap akışları için önce giriş yapmalısın."; return; }
        _main.Go(Page.Playlist);
        await _main.Playlist.LoadAsync(f.Key);
    }

    [RelayCommand]
    private async Task CheckNow(object? p)
    {
        if (p is not FollowedSource f) return;
        Busy = true;
        try
        {
            var n = await _host.Follow.CheckAsync(f);
            Notice = n > 0 ? $"{f.Title}: {n} yeni video kuyruğa eklendi." : $"{f.Title}: yeni video yok.";
        }
        catch (Exception e) { Notice = e.Message; }
        finally { Busy = false; }
    }

    [RelayCommand] private Task Unfollow(object? p) => p is FollowedSource f ? _host.Follow.UnfollowAsync(f) : Task.CompletedTask;

    [RelayCommand]
    private async Task OpenFollowed(object? p)
    {
        if (p is not FollowedSource f) return;
        _main.Go(Page.Playlist);
        await _main.Playlist.LoadAsync(f.Url);
    }
}
