using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vidra.App.Views;
using Vidra.Core;
using Vidra.Core.Models;
using Vidra.Core.YtDlp;

namespace Vidra.App.ViewModels;

public sealed record FeedItem(string Key, string Label, string Hint);

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
        (StateTitle, StateDetail) = c switch
        {
            AppLoginCookies a => ("YouTube hesabı bağlı",
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

    [RelayCommand]
    private async Task SignIn()
    {
        Notice = null;
        var r = await _host.Account.SignInAsync(Application.Current.MainWindow!);
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
