using System.Diagnostics;
using System.Globalization;

namespace Vidra.Core;

/// <summary>
/// Hafif süre ölçümü: açılış aşamaları, analiz ve indirme süreleri %LOCALAPPDATA%\Vidra\perf.log dosyasına yazılır.
/// Amaç, "yavaş" şikâyetinde tahmin yerine sayıya bakmak. Dosya 512 KB'ı geçince baştan başlar.
/// </summary>
public static class Perf
{
    private const long MaxBytes = 512 * 1024;
    private static readonly Lock Gate = new();
    private static readonly DateTime ProcessStart = StartTime();

    /// <summary>Günlük dosyası; null ise ölçümler yalnızca bellekte kalır (testler).</summary>
    public static string? LogFile { get; set; }

    /// <summary>Süreç başladığından beri geçen süre (açılış aşamaları için; .NET çalışma zamanının yüklenmesi dahil).</summary>
    public static TimeSpan Uptime => DateTime.UtcNow - ProcessStart;

    private static DateTime StartTime()
    {
        try { using var p = System.Diagnostics.Process.GetCurrentProcess(); return p.StartTime.ToUniversalTime(); }
        catch (Exception e) when (e is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception) { return DateTime.UtcNow; }
    }

    /// <summary><c>using (Perf.Measure("probe playlist")) { … }</c>: blok bitince süreyi yazar.</summary>
    public static Scope Measure(string what, string? detail = null) => new(what, detail, Stopwatch.GetTimestamp());

    /// <returns>Yazılan satır.</returns>
    public static string Write(string what, TimeSpan elapsed, string? detail = null) =>
        WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {what,-28} {elapsed.TotalMilliseconds,9:0} ms{(detail is null ? "" : "  " + detail)}"));

    /// <summary>Açılıştan bu yana geçen süreyi aşama adıyla yazar.</summary>
    public static void Mark(string phase) => _ = Write("startup:" + phase, Uptime);

    private static string WriteLine(string line)
    {
        var file = LogFile;
        if (file is null) return line;
        try
        {
            lock (Gate)
            {
                var fi = new FileInfo(file);
                if (fi.Exists && fi.Length > MaxBytes) fi.Delete();
                File.AppendAllText(file, line + Environment.NewLine);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* ölçüm yazılamazsa iş durmasın */ }
        return line;
    }

    public readonly struct Scope(string what, string? detail, long started) : IDisposable
    {
        public void Dispose() => _ = Write(what, Stopwatch.GetElapsedTime(started), detail);
    }
}
