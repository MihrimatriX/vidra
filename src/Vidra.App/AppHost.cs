using System;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using Vidra.App.Services;
using Vidra.Core.Follow;
using Vidra.Core.Models;
using Vidra.Core.Process;
using Vidra.Core.Queue;
using Vidra.Core.Storage;
using Vidra.Core.Tools;
using Vidra.Core.YtDlp;

namespace Vidra.App;

/// <summary>Bileşim kökü: servisler bir kez oluşturulur, görünüm modelleri buradan alır.</summary>
public sealed class AppHost : IAsyncDisposable
{
    public AppPaths Paths { get; }
    public SettingsStore SettingsStore { get; }
    public AppSettings Settings => SettingsStore.Current;
    public IProcessRunner Runner { get; }
    public HttpClient Http { get; }
    public ToolManager Tools { get; }
    public YtDlpService YtDlp { get; }
    public DownloadQueue Queue { get; }
    public FollowService Follow { get; }
    public AccountService Account { get; }
    public ClipboardWatcher Clipboard { get; } = new();
    public TrayService? Tray { get; set; }

    public AppHost()
    {
        Paths = new AppPaths();
        Runner = new ProcessRunner(Paths.CookieCopyDir);
        SettingsStore = new SettingsStore(Paths);
        SettingsStore.Load();
        Http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("Vidra/0.2 (+https://github.com/yt-dlp/yt-dlp)");
        Tools = new ToolManager(Paths, Runner, Http);
        YtDlp = new YtDlpService(Runner, Tools, () => SettingsStore.Current);
        Queue = new DownloadQueue(Runner, Tools, () => SettingsStore.Current, Paths, Dispatch);
        Follow = new FollowService(YtDlp, Queue, Paths, () => SettingsStore.Current, Dispatch);
        Account = new AccountService(Paths, SettingsStore);
    }

    /// <summary>Koleksiyon değişiklikleri UI thread'inde ve senkron yapılmalı (kuyruk anlık görüntüleri için).</summary>
    public static void Dispatch(Action a)
    {
        var d = Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess()) a();
        else d.Invoke(a);
    }

    public async ValueTask DisposeAsync()
    {
        Queue.BeginShutdown();
        Queue.PauseAll(); // .part dosyaları kalır; bir sonraki açılışta duraklatılmış olarak geri gelir
        await Queue.SavePendingAsync();
        await Follow.DisposeAsync();
        await SettingsStore.SaveNowAsync();
        Clipboard.Dispose();
        Tray?.Dispose();
        Http.Dispose();
    }
}
