using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Vidra.App.Services;
using Vidra.App.ViewModels;

[assembly: ThemeInfo(ResourceDictionaryLocation.None, ResourceDictionaryLocation.SourceAssembly)]

namespace Vidra.App;

public partial class App : Application
{
    private const string InstanceName = "Vidra.SingleInstance.v1";
    private Mutex? _mutex;
    private bool _ownsMutex;
    private AppHost? _host;
    private CancellationTokenSource? _pipeCts;

    public static AppHost Host => ((App)Current)._host!;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Tarayıcı eklentisi Vidra'yı native messaging host'u olarak başlatır (ilk argüman: chrome-extension://kimlik/).
        // Pencere açmadan iletileri yanıtla; tek örnek kilidine dokunma. Task.Run: arayüz thread'inde beklemek kilitlenir.
        if (e.Args.Length > 0 && e.Args[0].StartsWith("chrome-extension://", StringComparison.Ordinal))
        {
            var code = 1;
            try { code = Task.Run(ExtensionService.RunHostAsync).GetAwaiter().GetResult(); }
            catch (Exception ex) { Log(ex); }
            Shutdown(code);
            return;
        }

        // Tek örnek: ikinci açılış (ör. "Vidra ile aç" ya da kısayol) bağlantıyı mevcut pencereye iletir
        _mutex = new Mutex(true, InstanceName, out var first);
        _ownsMutex = first;
        if (!first)
        {
            ForwardToRunningInstance(e.Args.Length > 0 ? e.Args[0] : "");
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnUnhandled;
        base.OnStartup(e);
        try
        {
            StartApp(e);
        }
        catch (Exception ex)
        {
            // Açılışta hata: görünmez bir süreç bırakma (mutex'i tutup sonraki açılışları yutar)
            Log(ex);
            MessageBox.Show($"Vidra başlatılamadı:\n{ex.Message}", "Vidra", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private void StartApp(StartupEventArgs e)
    {
        _host = new AppHost();
#if DEBUG
        // Ekran görüntüsü turu: kendi ayarlarıyla, dış etkiler (güncelleme bildirimi, pano, tepsi balonu) kapalı
        var tour = e.Args.Length > 1 && e.Args[0] == "--screenshot-tour";
        if (tour)
            _host.SettingsStore.Update(s =>
            {
                s.CheckAppUpdates = false;
                s.WatchClipboard = false;
                s.Notifications = false;
                s.Window = null;
            });
#endif
        ThemeService.Apply(_host.Settings.Theme);

        var vm = new MainViewModel(_host);
        var window = new MainWindow(vm, _host);
        MainWindow = window;
        window.Show();

        _host.Tray = new TrayService(window, _host.Queue.PauseAll, _host.Queue.ResumeAll, () => window.ExitApp());
        _host.Clipboard.Attach(window);
        _host.Clipboard.Enabled = _host.Settings.WatchClipboard;
        _host.Follow.StartBackgroundLoop();
        try { ExtensionService.RegisterHost(_host.Paths); }
        catch (Exception ex) { Log(ex); } // kayıt olmazsa yalnızca eklenti bağlanamaz
        // Kuyruk olayları arka plan thread'inden gelir: pencereye dokunmadan önce UI thread'ine geç
        _host.Queue.JobFinished += (_, j) => Dispatcher.BeginInvoke(() =>
        {
            if (!_host.Settings.Notifications || window.IsActive) return;
            if (j.Status == Core.Models.JobStatus.Done) _host.Tray?.Notify("İndirme tamamlandı", j.Title, warning: j.Warning is not null);
        });
        _host.Follow.Checked += (_, r) =>
        {
            if (r.NewCount > 0 && _host.Settings.Notifications)
                Dispatcher.BeginInvoke(() => _host.Tray?.Notify("Takip edilen liste", $"{r.Source.Title}: {r.NewCount} yeni video kuyruğa eklendi"));
        };

        // Oturum kapanışı / Windows kapanışı: kapanış akışı çalışmaz, bekleyen işleri burada kaydet
        SessionEnding += (_, _) =>
        {
            try
            {
                window.SavePlacement(); // OnClosing yolu çalışmaz; ayarlar aşağıda hemen yazılır
                _host.Queue.BeginShutdown();
                _host.Queue.PauseAll();
                _host.Queue.SavePendingAsync().GetAwaiter().GetResult();
                _host.SettingsStore.SaveNowAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex) { Log(ex); }
        };

        _pipeCts = new CancellationTokenSource();
        _ = ListenForLinksAsync(vm, _pipeCts.Token);

#if DEBUG
        if (tour)
        {
            _ = ScreenshotTour.RunAsync(window, vm, _host, e.Args[1]);
            return;
        }
#endif
        if (e.Args.Length > 0 && e.Args[0].Length > 0) _ = vm.OpenLinkAsync(e.Args[0]);
    }

    /// <returns>Çalışan bir Vidra'ya iletildiyse true.</returns>
    internal static bool ForwardToRunningInstance(string arg)
    {
        // Çalışan örnek yoksa boru da yoktur: Connect'in zaman aşımını bekleme
        if (!Mutex.TryOpenExisting(InstanceName, out var m)) return false;
        m.Dispose();
        try
        {
            using var client = new NamedPipeClientStream(".", InstanceName, PipeDirection.Out);
            client.Connect(1500);
            using var w = new StreamWriter(client);
            w.WriteLine(arg);
            return true;
        }
        catch { return false; /* çalışan örnek yanıt vermiyor */ }
    }

    private async Task ListenForLinksAsync(MainViewModel vm, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(InstanceName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(ct);
                using var r = new StreamReader(server);
                var line = (await r.ReadLineAsync(ct))?.Trim();
                await Dispatcher.InvokeAsync(async () =>
                {
                    // Eklentinin çerez eşitlemesi arka planda olur: pencereyi öne getirme
                    if (line == Core.Browser.BrowserBridge.CookiesSyncedSignal) { Host.Account.OnExtensionSynced(); return; }
                    Host.Tray?.Show();
                    if (!string.IsNullOrEmpty(line)) await vm.OpenLinkAsync(line);
                });
            }
            catch (OperationCanceledException) { return; }
            catch (IOException) { await Task.Delay(500, ct); }
        }
    }

    private static void Log(Exception ex)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Vidra");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "vidra.log"), $"[{DateTime.Now:O}] {ex}\n");
        }
        catch { /* günlük yazılamazsa da devam */ }
    }

    private bool _showingError;

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log(e.Exception);
        e.Handled = true;
        // Pencere hiç yüklenmediyse kurtarılacak bir şey yok: çık
        if (MainWindow is null || !MainWindow.IsLoaded)
        {
            Shutdown(1);
            return;
        }
        // Yerleşim sırasında tekrar eden hatalar üst üste kutu açmasın
        if (_showingError) return;
        _showingError = true;
        MessageBox.Show($"Beklenmeyen bir hata oluştu:\n{e.Exception.Message}\n\nAyrıntılar günlüğe yazıldı.", "Vidra",
            MessageBoxButton.OK, MessageBoxImage.Warning);
        _showingError = false;
    }

    /// <summary>Kapanış: önce servisleri temizle (süreçleri durdur, ayarları yaz), sonra uygulamayı kapat.</summary>
    public async Task ShutdownGracefullyAsync()
    {
        _pipeCts?.Cancel();
        try
        {
            if (_host is not null) await _host.DisposeAsync();
        }
        catch (Exception ex) { Log(ex); }
        finally
        {
            Shutdown();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_ownsMutex) _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
