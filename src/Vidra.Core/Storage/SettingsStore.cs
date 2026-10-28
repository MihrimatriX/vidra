using Vidra.Core.Models;

namespace Vidra.Core.Storage;

/// <summary>Ayarları yükler ve değişiklikleri kısa bir gecikmeyle (debounce) diske yazar.</summary>
public sealed class SettingsStore(AppPaths paths)
{
    private CancellationTokenSource? _pending;

    public AppSettings Current { get; private set; } = new();

    public event EventHandler? Changed;

    public AppSettings Load()
    {
        var s = JsonStore.Read<AppSettings>(paths.SettingsFile) ?? new AppSettings();
        if (string.IsNullOrWhiteSpace(s.OutputDir)) s.OutputDir = AppPaths.DefaultOutputDir();
        // Giriş dosyası silinmişse (ör. elle) oturumu düşür
        if (s.Cookies is AppLoginCookies a && !File.Exists(a.Path)) s.Cookies = new NoCookies();
        Current = s;
        return s;
    }

    /// <summary>Ayarı değiştir ve kaydet. Ayar nesnesi yerinde değişir; dinleyiciler Changed ile haberdar olur.</summary>
    public void Update(Action<AppSettings> change)
    {
        change(Current);
        Changed?.Invoke(this, EventArgs.Empty);
        ScheduleSave();
    }

    public Task SaveNowAsync() => JsonStore.WriteAsync(paths.SettingsFile, Current);

    private void ScheduleSave()
    {
        _pending?.Cancel();
        var cts = _pending = new CancellationTokenSource();
        _ = Task.Delay(400, cts.Token).ContinueWith(t =>
        {
            if (!t.IsCanceled) _ = SaveNowAsync();
        }, TaskScheduler.Default);
    }
}
