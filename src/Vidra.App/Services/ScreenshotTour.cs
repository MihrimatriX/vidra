#if DEBUG
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Vidra.App.ViewModels;
using Vidra.Core;
using Vidra.Core.Models;
using Vidra.Core.Queue;
using Vidra.Core.YtDlp;
using Page = Vidra.App.ViewModels.Page;
using ThemeMode = Vidra.Core.Models.ThemeMode;

namespace Vidra.App.Services;

/// <summary>
/// Yalnızca Debug: <c>Vidra.exe --screenshot-tour &lt;klasör&gt;</c> örnek verilerle ekranları gezip PNG kaydeder
/// (README ve karşılaştırma sayfası için; .github/workflows/screenshots.yml Windows'ta çalıştırır).
/// Pencere ekrandan bağımsız çizilir (RenderTargetBitmap); sağ tık menüleri ayrıca çizilip üstüne bindirilir.
/// </summary>
internal static class ScreenshotTour
{
    private const double Scale = 1.25;
    private static string _out = "";
    private static StreamWriter? _log;

    public static async Task RunAsync(MainWindow w, MainViewModel vm, AppHost host, string outDir)
    {
        _out = Directory.CreateDirectory(outDir).FullName;
        _log = new StreamWriter(Path.Combine(_out, "tour.log")) { AutoFlush = true };
        try
        {
            w.WindowState = WindowState.Normal;
            w.Left = 0;
            w.Top = 0;
            w.Width = 1280;
            w.Height = 820;
            vm.Setup.IsVisible = false;
            Seed(vm, host);

            ThemeService.Apply(ThemeMode.Dark);
            await Shots(w, vm, host, "dark");
            ThemeService.Apply(ThemeMode.Light);
            await Shots(w, vm, host, "light");
            Log("bitti");
        }
        catch (Exception e)
        {
            Log("HATA: " + e);
        }
        finally
        {
            _log.Dispose();
            await ((App)Application.Current).ShutdownGracefullyAsync();
        }
    }

    private static async Task Shots(MainWindow w, MainViewModel vm, AppHost host, string theme)
    {
        // İndir: video kartı (kalite kartları, ses dili)
        vm.Go(Page.Download);
        vm.Download.Url = "https://www.youtube.com/watch?v=aqz-KE-bpKQ";
        vm.Download.ShowVideo(SampleVideo());
        vm.Download.AudioLanguage = "tr";
        await Settle(4000);
        await Save(w, $"{theme}-01-indir");

        // Klip: heatmap + en iyi 3 an (çoklu kesim)
        vm.Download.PickHighlightsCommand.Execute("3x20");
        await Settle(800);
        if (Find(w.PageHost, "ClipPanel") is { } clip) clip.BringIntoView(new Rect(0, -120, 10, 430));
        await Settle(800);
        await Save(w, $"{theme}-02-klip-heatmap");

        // Boş İndir ekranı: son indirilenler
        vm.Download.ShowEmptyForTour();
        vm.Go(Page.Search);
        vm.Go(Page.Download);
        await Settle(2500);
        await Save(w, $"{theme}-03-son-indirilenler");

        // Komut paleti
        vm.Palette.Open();
        await Settle(600);
        await Save(w, $"{theme}-04-komut-paleti");
        vm.Palette.Query = "bunny";
        await Settle(800);
        await Save(w, $"{theme}-05-palet-arama");
        vm.Palette.Close();

        // Kuyruk: işler, toplam ilerleme, geçmiş
        vm.Go(Page.Queue);
        for (var i = 0; i < 10; i++)
        {
            host.Queue.TotalSpeed = 4_200_000 + Math.Sin(i) * 1_300_000;
            await Settle(330);
        }
        await Save(w, $"{theme}-06-kuyruk");

        // Sağ tık: kuyruktaki iş ve geçmiş satırı
        await SaveMenu(w, b => b.DataContext is DownloadJob { Status: JobStatus.Running }, $"{theme}-07-is-menusu", 200, 26);
        await SaveMenu(w, b => b.DataContext is HistoryEntry, $"{theme}-08-gecmis-menusu", 260, 18);

        // Bildirim + geri al
        vm.ShowToast("3 iş iptal edildi", "Geri al", () => { });
        await Settle(700);
        await Save(w, $"{theme}-09-geri-al");
        vm.DismissToastCommand.Execute(null);

        // Kütüphane
        vm.Go(Page.Library);
        await Settle(3500);
        await Save(w, $"{theme}-10-kutuphane");
        await SaveMenu(w, b => b.DataContext is HistoryEntry && b.ContextMenu is not null, $"{theme}-11-kutuphane-menusu", 120, 90);

        // Sürükle-bırak katmanı
        w.DropLayer.Visibility = Visibility.Visible;
        await Settle(400);
        await Save(w, $"{theme}-12-surukle-birak");
        w.DropLayer.Visibility = Visibility.Collapsed;

        // Ayarlar: yeni anahtarlar, ek argümanlar; gizli mod ve güncelleme bağlantısı kenar çubuğunda
        vm.Go(Page.Settings);
        vm.Settings.ExtraArgs = "--limit-rate 5M --embed-chapters";
        vm.Update = new AppRelease(new Version(0, 4, 0), "v0.4.0", "https://github.com/MihrimatriX/vidra/releases", null);
        vm.Incognito = true;
        await Settle(1200);
        if (Find(w.PageHost, "SecNet") is { } net) net.BringIntoView(new Rect(0, 0, 10, 600));
        await Settle(900);
        await Save(w, $"{theme}-13-ayarlar");
        vm.Incognito = false;
        vm.Update = null;
        vm.DismissToastCommand.Execute(null);

        // İstatistik (örnek geçmişten)
        vm.Go(Page.Stats);
        await Settle(1200);
        await Save(w, $"{theme}-14-istatistik");
    }

    // ================= Örnek veri =================

    private static readonly (string Id, string Title, int Secs)[] Films =
    [
        ("aqz-KE-bpKQ", "Big Buck Bunny 60fps 4K - Official Blender Foundation Short Film", 635),
        ("eRsGyueVLvQ", "Sintel - Open Movie by Blender Foundation", 888),
        ("R6MlUcmOul8", "Tears of Steel - Blender VFX Open Movie", 734),
        ("WhWc3b3KhnY", "Spring - Blender Open Movie", 464),
        ("Y-rmzh0PI3c", "Cosmos Laundromat - First Cycle. Official Blender Foundation release.", 730),
        ("_cMxraX_5RE", "Sprite Fright - Blender Open Movie", 629),
        ("mN0zPOpADL4", "Agent 327: Operation Barbershop", 231),
        ("UXqq0ZvbOnk", "CHARGE - Blender Open Movie", 245),
    ];

    private static void Seed(MainViewModel vm, AppHost host)
    {
        host.Queue.Held = true; // örnek işler gerçek yt-dlp başlatmasın
        var files = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "vidra-tour")).FullName;
        var now = DateTimeOffset.Now;
        var history = Films.Select((f, i) =>
        {
            var audio = i % 4 == 3;
            var path = Path.Combine(files, $"{f.Title.Split(' ')[0]}-{i}.{(audio ? "mp3" : "mp4")}");
            File.WriteAllText(path, "örnek");
            return new HistoryEntry
            {
                Id = "h" + i, Url = YouTubeUrl.WatchUrl(f.Id), VideoId = f.Id, Title = f.Title, Channel = "Blender", Duration = f.Secs,
                Options = audio ? new DownloadOptions { Mode = DownloadMode.Audio, AudioFormat = AudioFormat.Mp3, MusicMode = true }
                    : new DownloadOptions { Height = i % 3 == 0 ? 2160 : 1080, Container = i % 2 == 0 ? ContainerFormat.Mp4 : ContainerFormat.Original },
                Status = JobStatus.Done, Size = (long)(f.Secs * (audio ? 30_000 : 1_100_000)), FilePath = path,
                Warning = i == 5 ? "Altyazı indirilemedi (HTTP 429)" : null,
                FinishedAt = now.AddHours(-(i * 19 + 2)),
            };
        }).ToList();
        host.Queue.RestoreHistory(history);

        var jobs = host.Queue.Enqueue(
        [
            new NewJob(YouTubeUrl.WatchUrl("eRsGyueVLvQ"), "Sintel - Open Movie by Blender Foundation", new DownloadOptions { Height = 1080, Container = ContainerFormat.Mp4 }, "Blender", null, 888, Source: "Playlist: Blender Open Movies"),
            new NewJob(YouTubeUrl.WatchUrl("R6MlUcmOul8"), "Tears of Steel - Blender VFX Open Movie", new DownloadOptions { Height = 2160 }, "Blender", null, 734, Source: "Playlist: Blender Open Movies"),
            new NewJob(YouTubeUrl.WatchUrl("WhWc3b3KhnY"), "Spring - Blender Open Movie", new DownloadOptions { Mode = DownloadMode.Audio, AudioFormat = AudioFormat.Mp3 }, "Blender", null, 464),
            new NewJob(YouTubeUrl.WatchUrl("UXqq0ZvbOnk"), "CHARGE - Blender Open Movie", new DownloadOptions(), "Blender", null, 245),
        ], startNow: false);
        Set(jobs[0], JobStatus.Running, 63, "Video akışı (1/2)", 2_900_000, 41);
        Set(jobs[1], JobStatus.Running, 18, "Video akışı (1/2)", 1_400_000, 260);
        jobs[2].Status = JobStatus.Paused;
        jobs[2].Stage = "Duraklatıldı";
        jobs[2].Percent = 40;
        jobs[3].Status = JobStatus.Error;
        jobs[3].Stage = "Hata";
        jobs[3].ErrorKind = ErrorKind.RateLimited;
        jobs[3].ErrorMessage = OutputParser.FriendlyError(ErrorKind.RateLimited, "");

        vm.Basket.Add(Films.Skip(5).Select(f => new BasketItem(YouTubeUrl.WatchUrl(f.Id), f.Title, "Blender", $"https://i.ytimg.com/vi/{f.Id}/mqdefault.jpg", f.Secs, "Arama: blender")));
    }

    private static void Set(DownloadJob j, JobStatus s, double percent, string stage, double speed, double eta)
    {
        j.Status = s;
        j.Percent = percent;
        j.Stage = stage;
        j.Speed = speed;
        j.Eta = eta;
        j.Total = 812_000_000;
        j.Downloaded = (long)(812_000_000 * percent / 100);
    }

    /// <summary>yt-dlp -J biçiminde örnek: 4K'ya kadar kaliteler, dublaj izleri, bölümler ve "en çok tekrar oynatılan" grafiği.</summary>
    private static VideoInfo SampleVideo()
    {
        var formats = new List<object>();
        foreach (var (h, w, kbps) in new[] { (2160, 3840, 17000), (1440, 2560, 9000), (1080, 1920, 4300), (720, 1280, 2500), (480, 854, 1200), (360, 640, 700) })
        {
            formats.Add(new { format_id = $"av-{h}", vcodec = "av01.0.08M.08", acodec = "none", height = h, width = w, fps = 60, tbr = kbps * 0.8, filesize = (long)(kbps * 0.8 * 1000 / 8 * 635) });
            formats.Add(new { format_id = $"vp-{h}", vcodec = "vp09.00.40.08", acodec = "none", height = h, width = w, fps = 60, tbr = (double)kbps, filesize = (long)(kbps * 1000 / 8 * 635) });
            if (h <= 1080) formats.Add(new { format_id = $"h264-{h}", vcodec = "avc1.640028", acodec = "none", height = h, width = w, fps = 60, tbr = kbps * 1.3, filesize = (long)(kbps * 1.3 * 1000 / 8 * 635) });
        }
        foreach (var (lang, pref, note, abr) in new[] { ("en", 10, "English original (default)", 135.0), ("tr", -10, "Turkish dubbed-auto", 128.0), ("es", -10, "Spanish dubbed-auto", 128.0), ("de", -10, "German dubbed-auto", 128.0), ("fr", -10, "French dubbed-auto", 128.0) })
        {
            formats.Add(new { format_id = $"251-{lang}", vcodec = "none", acodec = "opus", abr, asr = 48000, language = lang, language_preference = pref, format_note = note, filesize = (long)(abr * 1000 / 8 * 635) });
            formats.Add(new { format_id = $"140-{lang}", vcodec = "none", acodec = "mp4a.40.2", abr = 129.0, asr = 44100, language = lang, language_preference = pref, format_note = note, filesize = (long)(129.0 * 1000 / 8 * 635) });
        }
        var heat = Enumerable.Range(0, 100).Select(i =>
        {
            double t = i / 100.0;
            var v = 0.18 + 0.82 * Math.Exp(-Math.Pow((t - 0.37) / 0.05, 2)) + 0.55 * Math.Exp(-Math.Pow((t - 0.78) / 0.04, 2)) + 0.35 * Math.Exp(-Math.Pow((t - 0.08) / 0.03, 2));
            return new { start_time = i * 6.35, end_time = (i + 1) * 6.35, value = Math.Min(1, v) };
        });
        var json = JsonSerializer.Serialize(new
        {
            id = "aqz-KE-bpKQ",
            webpage_url = "https://www.youtube.com/watch?v=aqz-KE-bpKQ",
            title = "Big Buck Bunny 60fps 4K - Official Blender Foundation Short Film",
            channel = "Blender",
            thumbnail = "https://i.ytimg.com/vi/aqz-KE-bpKQ/hqdefault.jpg",
            duration = 635,
            upload_date = "20141110",
            view_count = 18_400_000,
            formats,
            subtitles = new Dictionary<string, object[]> { ["en"] = [new { name = "English" }], ["tr"] = [new { name = "Türkçe" }] },
            automatic_captions = Enumerable.Range(0, 157).ToDictionary(i => i == 0 ? "en-orig" : $"x{i}", _ => new object[] { new { name = "English (Original)" } }),
            chapters = new[]
            {
                new { title = "Uyanış", start_time = 0.0, end_time = 140.0 },
                new { title = "Kelebekler", start_time = 140.0, end_time = 330.0 },
                new { title = "İntikam planı", start_time = 330.0, end_time = 520.0 },
                new { title = "Final", start_time = 520.0, end_time = 635.0 },
            },
            heatmap = heat,
        });
        return ProbeParser.ParseVideo(JsonDocument.Parse(json).RootElement);
    }

    // ================= Çizim =================

    private static async Task Settle(int ms)
    {
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        await Task.Delay(ms);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }

    private static Task Save(Window w, string name, ContextMenu? menu = null)
    {
        w.UpdateLayout();
        var width = (int)Math.Ceiling(w.ActualWidth);
        var height = (int)Math.Ceiling(w.ActualHeight);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawImage(Render(w, width, height), new Rect(0, 0, width, height));
            if (menu is { IsOpen: true, ActualWidth: > 0 })
            {
                var dpi = PresentationSource.FromVisual(w)?.CompositionTarget?.TransformToDevice.M11 ?? 1;
                var at = menu.PointToScreen(new Point(0, 0)) - w.PointToScreen(new Point(0, 0));
                dc.DrawImage(Render(menu, (int)Math.Ceiling(menu.ActualWidth), (int)Math.Ceiling(menu.ActualHeight)),
                    new Rect(at.X / dpi, at.Y / dpi, menu.ActualWidth, menu.ActualHeight));
            }
        }
        var bmp = new RenderTargetBitmap((int)(width * Scale), (int)(height * Scale), 96 * Scale, 96 * Scale, PixelFormats.Pbgra32);
        bmp.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using (var fs = File.Create(Path.Combine(_out, name + ".png"))) enc.Save(fs);
        Log("kaydedildi: " + name);
        return Task.CompletedTask;
    }

    private static BitmapSource Render(Visual v, int width, int height)
    {
        var bmp = new RenderTargetBitmap((int)(width * Scale), (int)(height * Scale), 96 * Scale, 96 * Scale, PixelFormats.Pbgra32);
        bmp.Render(v);
        return bmp;
    }

    /// <summary>Koşulu sağlayan ilk öğenin sağ tık menüsünü (target'a göre konumla) açıp bindirilmiş görüntüsünü kaydeder.</summary>
    private static async Task SaveMenu(MainWindow w, Func<Border, bool> pick, string name, double x, double y)
    {
        var target = Descendants(w.PageHost).OfType<Border>().FirstOrDefault(b => b.ContextMenu is not null && b.IsVisible && pick(b));
        if (target?.ContextMenu is not { } menu)
        {
            Log("menü bulunamadı: " + name);
            return;
        }
        menu.PlacementTarget = target;
        menu.Placement = PlacementMode.Relative;
        menu.HorizontalOffset = x;
        menu.VerticalOffset = y;
        menu.IsOpen = true;
        await Settle(700);
        await Save(w, name, menu);
        menu.IsOpen = false;
        await Settle(300);
    }

    private static FrameworkElement? Find(DependencyObject root, string name) =>
        Descendants(root).OfType<FrameworkElement>().FirstOrDefault(e => e.Name == name);

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        var queue = new Queue<DependencyObject>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            var d = queue.Dequeue();
            yield return d;
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++) queue.Enqueue(VisualTreeHelper.GetChild(d, i));
        }
    }

    private static void Log(string s) => _log?.WriteLine($"[{DateTime.Now:HH:mm:ss}] {s}");
}
#endif
