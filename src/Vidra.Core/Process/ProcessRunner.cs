using System.Diagnostics;
using System.Text;

namespace Vidra.Core.Process;

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr);

/// <summary>Testlerde sahte süreçle değiştirilebilsin diye soyutlanmış süreç başlatıcı.</summary>
public interface IProcessRunner
{
    /// <summary>Uzun ömürlü süreç: satır satır geri çağırır; dönen tutamaç ile öldürülebilir.</summary>
    IRunningProcess Start(string exe, IReadOnlyList<string> args, Action<string> onLine);

    /// <summary>Kısa ömürlü çağrı (probe, --version, -U).</summary>
    Task<ProcessResult> RunAsync(string exe, IReadOnlyList<string> args, CancellationToken ct = default);
}

public interface IRunningProcess
{
    Task<int> Completion { get; }
    void Kill();
}

/// <param name="cookieCopyDir">Verilirse her sürecin <c>--cookies</c> dosyası buradaki özel bir kopyaya çevrilir.</param>
public sealed class ProcessRunner(string? cookieCopyDir = null) : IProcessRunner
{
    /// <summary>
    /// yt-dlp <c>--cookies</c> dosyasını okur ve çıkışta geri yazar; salt okunur seçeneği yok. Eşzamanlı süreçler aynı
    /// dosyayı ezer, YouTube'un tek bir yanıtta oturum çerezlerini silmesi de ana dosyaya kalıcı yazılır ("hesaptan atılma").
    /// Her sürece kendi kopyası verilir; ana dosyayı yalnızca giriş (ve tarayıcı eşitlemesi) yazar.
    /// </summary>
    /// <returns>Yeni argümanlar ve süreç bitince silinecek kopyanın yolu (kopya yoksa null).</returns>
    public static (IReadOnlyList<string> Args, string? Copy) WithPrivateCookies(IReadOnlyList<string> args, string dir)
    {
        var a = args.ToList();
        var i = a.IndexOf("--cookies");
        // Dosya yoksa dokunma: yt-dlp kendi hatasını verir, OutputParser onu oturum hatası sayar
        if (i < 0 || i + 1 >= a.Count || !File.Exists(a[i + 1])) return (args, null);
        Directory.CreateDirectory(dir);
        var copy = Path.Combine(dir, $"{Guid.NewGuid():N}.txt");
        File.Copy(a[i + 1], copy);
        a[i + 1] = copy;
        return (a, copy);
    }

    private (IReadOnlyList<string> Args, string? Copy) Private(IReadOnlyList<string> args) =>
        cookieCopyDir is null ? (args, null) : WithPrivateCookies(args, cookieCopyDir);

    private static void TryDelete(string? path)
    {
        if (path is null) return;
        try { File.Delete(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* çıkışta oturum temizliği siler */ }
    }

    private static ProcessStartInfo Info(string exe, IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(exe) ?? Environment.CurrentDirectory,
        };
        // ArgumentList: her eleman ayrı argüman, tırnaklama/enjeksiyon derdi yok
        foreach (var a in args) psi.ArgumentList.Add(a);
        // Windows konsol kod sayfası yüzünden Türkçe başlıkların bozulmasını önler
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["PYTHONUTF8"] = "1";
        return psi;
    }

    public IRunningProcess Start(string exe, IReadOnlyList<string> args, Action<string> onLine)
    {
        var (a, copy) = Private(args);
        var p = new System.Diagnostics.Process { StartInfo = Info(exe, a), EnableRaisingEvents = true };
        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var open = 2; // stdout + stderr kapanmadan Exited'a güvenme: son satırlar kaybolur

        void Closed()
        {
            if (Interlocked.Decrement(ref open) == 0)
            {
                // Bu satır bir okuma geri çağrısının İÇİNDE çalışıyor: zaman aşımısız WaitForExit()
                // akış EOF görevlerini (bu görev dahil) bekleyip kilitlenir. Asenkron bekle.
                _ = Task.Run(async () =>
                {
                    await p.WaitForExitAsync().ConfigureAwait(false);
                    TryDelete(copy);
                    tcs.TrySetResult(p.ExitCode);
                    p.Dispose();
                });
            }
        }

        p.OutputDataReceived += (_, e) => { if (e.Data is null) Closed(); else if (e.Data.Length > 0) onLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data is null) Closed(); else if (e.Data.Length > 0) onLine(e.Data); };

        try { if (!p.Start()) throw new InvalidOperationException($"{exe} başlatılamadı"); }
        catch { TryDelete(copy); throw; }
        KillOnCloseJob.Assign(p); // uygulama ölürse yt-dlp/ffmpeg de ölsün
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        return new Running(p, tcs.Task);
    }

    public async Task<ProcessResult> RunAsync(string exe, IReadOnlyList<string> args, CancellationToken ct = default)
    {
        var (a, copy) = Private(args);
        try { return await RunCoreAsync(exe, a, ct).ConfigureAwait(false); }
        finally { TryDelete(copy); }
    }

    private static async Task<ProcessResult> RunCoreAsync(string exe, IReadOnlyList<string> args, CancellationToken ct)
    {
        using var p = new System.Diagnostics.Process { StartInfo = Info(exe, args) };
        p.Start();
        var stdout = p.StandardOutput.ReadToEndAsync(ct);
        var stderr = p.StandardError.ReadToEndAsync(ct);
        try
        {
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { /* zaten bitti */ }
            throw;
        }
        return new ProcessResult(p.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
    }

    private sealed class Running(System.Diagnostics.Process p, Task<int> completion) : IRunningProcess
    {
        public Task<int> Completion { get; } = completion;

        public void Kill()
        {
            // yt-dlp (PyInstaller) kendi alt sürecini ve ffmpeg'i başlatır — tüm ağacı öldür
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); }
            catch (Exception e) when (e is InvalidOperationException or ObjectDisposedException) { /* bitmiş */ }
        }
    }
}
