using System.Globalization;

namespace Vidra.Core;

/// <summary>
/// Hata günlüğü: %LOCALAPPDATA%\Vidra\vidra.log. Analiz ve indirme hatalarında yt-dlp'nin komutu (çerez yolu gizli)
/// ve stderr'inin sonu yazılır; "neden hata verdi" sorusu ekrandaki kısa açıklamayla bitmesin. 1 MB'ı geçince baştan başlar.
/// </summary>
public static class AppLog
{
    private const long MaxBytes = 1024 * 1024;
    private static readonly Lock Gate = new();

    /// <summary>Günlük dosyası; null ise yazılmaz (testler).</summary>
    public static string? File { get; set; }

    /// <summary>Son yazılan kayıtlar (testler ve hata ayıklama için).</summary>
    public static string? Last { get; private set; }

    public static void Write(string title, string? detail = null)
    {
        var text = string.Create(CultureInfo.InvariantCulture, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {title}") +
                   (string.IsNullOrWhiteSpace(detail) ? "" : Environment.NewLine + Indent(detail.TrimEnd()));
        Last = text;
        var file = File;
        if (file is null) return;
        try
        {
            lock (Gate)
            {
                var fi = new FileInfo(file);
                if (fi.Exists && fi.Length > MaxBytes) fi.Delete();
                System.IO.File.AppendAllText(file, text + Environment.NewLine);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* günlük yazılamazsa iş durmasın */ }
    }

    private static string Indent(string s) =>
        string.Join(Environment.NewLine, s.Split('\n').Select(l => "    " + l.TrimEnd('\r')));

    /// <summary>
    /// stderr'in anlamlı son satırları: [debug] ve indirme ilerlemesi atlanır, en fazla <paramref name="max"/> satır.
    /// </summary>
    public static string Tail(string stderr, int max = 15) =>
        string.Join(Environment.NewLine, stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimEnd('\r'))
            .Where(l => l.Trim().Length > 0 && !l.StartsWith("[debug]", StringComparison.Ordinal) && !l.StartsWith("[download]", StringComparison.Ordinal))
            .TakeLast(max));
}
