using System.Globalization;
using System.Text.RegularExpressions;

namespace Vidra.Core.Tools;

/// <summary>
/// yt-dlp sürümü tarihtir ("2026.11.20", nightly: "2026.11.20.234512"). YouTube sık değiştiği için
/// eski yt-dlp 403, bot doğrulaması ve "biçim yok" hatalarının en yaygın sebebidir.
/// </summary>
public static partial class YtDlpFreshness
{
    /// <summary>Kendiliğinden güncelleme denemeleri arasındaki en kısa süre.</summary>
    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(20);

    /// <summary>Bu kadar günden eski sürüm arayüzde "eski" diye işaretlenir.</summary>
    public const int StaleDays = 14;

    [GeneratedRegex(@"^(\d{4})\.(\d{1,2})\.(\d{1,2})")]
    private static partial Regex VersionRx();

    public static DateOnly? ReleaseDate(string? version)
    {
        if (version is null || VersionRx().Match(version.Trim()) is not { Success: true } m) return null;
        int Part(int i) => int.Parse(m.Groups[i].Value, CultureInfo.InvariantCulture);
        try { return new DateOnly(Part(1), Part(2), Part(3)); }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    public static int? AgeDays(string? version, DateTimeOffset now) =>
        ReleaseDate(version) is { } d ? Math.Max(0, now.DayNumber() - d.DayNumber) : null;

    public static bool IsStale(string? version, DateTimeOffset now) => AgeDays(version, now) is > StaleDays;

    public static bool IsCheckDue(DateTimeOffset? lastCheck, DateTimeOffset now) => lastCheck is null || now - lastCheck >= CheckInterval;

    /// <summary>"yt-dlp -U" çıktısından yeni sürüm (güncelleme olduysa).</summary>
    public static string? UpdatedTo(string output) =>
        UpdatedRx().Match(output) is { Success: true } m ? m.Groups[1].Value : null;

    [GeneratedRegex(@"Updated yt-dlp to\s+\S*?@?(\d{4}\.\d{1,2}\.\d{1,2}(?:\.\d+)?)")]
    private static partial Regex UpdatedRx();

    private static int DayNumber(this DateTimeOffset t) => DateOnly.FromDateTime(t.Date).DayNumber;
}
