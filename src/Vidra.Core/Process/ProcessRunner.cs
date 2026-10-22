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

public sealed class ProcessRunner : IProcessRunner
{
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
        var p = new System.Diagnostics.Process { StartInfo = Info(exe, args), EnableRaisingEvents = true };
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
                    tcs.TrySetResult(p.ExitCode);
                    p.Dispose();
                });
            }
        }

        p.OutputDataReceived += (_, e) => { if (e.Data is null) Closed(); else if (e.Data.Length > 0) onLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data is null) Closed(); else if (e.Data.Length > 0) onLine(e.Data); };

        if (!p.Start()) throw new InvalidOperationException($"{exe} başlatılamadı");
        KillOnCloseJob.Assign(p); // uygulama ölürse yt-dlp/ffmpeg de ölsün
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        return new Running(p, tcs.Task);
    }

    public async Task<ProcessResult> RunAsync(string exe, IReadOnlyList<string> args, CancellationToken ct = default)
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
