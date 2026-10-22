using System.Globalization;

namespace Vidra.Core;

/// <summary>Türkçe biçimlendirme yardımcıları (arayüz ve günlükler için).</summary>
public static class Fmt
{
    public static readonly CultureInfo Tr = CultureInfo.GetCultureInfo("tr-TR");

    public static string Bytes(double? n)
    {
        if (n is not { } v || double.IsNaN(v) || v < 0) return "—";
        string[] u = ["B", "KB", "MB", "GB", "TB"];
        var i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        var fmt = v < 10 && i > 1 ? "0.#" : "0";
        return $"{v.ToString(fmt, Tr)} {u[i]}";
    }

    public static string Speed(double? bps) => bps is > 0 ? Bytes(bps) + "/s" : "—";

    public static string Kbps(double? kbps) =>
        kbps is not { } k ? "" : k >= 1000 ? $"{(k / 1000).ToString("0.#", Tr)} Mbps" : $"{Math.Round(k)} kbps";

    public static string Date(string? yyyymmdd)
    {
        if (yyyymmdd is not { Length: 8 } s ||
            !DateTime.TryParseExact(s, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return "";
        return d.ToString("d MMM yyyy", Tr);
    }

    public static string Relative(DateTimeOffset ts)
    {
        var now = DateTimeOffset.Now;
        if (ts.Date == now.Date) return "Bugün " + ts.ToString("HH:mm", Tr);
        if (ts.Date == now.Date.AddDays(-1)) return "Dün " + ts.ToString("HH:mm", Tr);
        var days = (int)(now.Date - ts.Date).TotalDays;
        return days < 30 ? $"{days} gün önce" : ts.ToString("d MMM yyyy", Tr);
    }

    public static string Count(long? n) => n is { } v ? v.ToString("N0", Tr) : "—";
}
