using Vidra.Core.Models;

namespace Vidra.Core;

/// <summary>YouTube'un "en çok tekrar oynatılan" grafiğinden klip önerisi.</summary>
public static class Heatmap
{
    /// <summary>
    /// Ortalama yoğunluğu en yüksek, birbiriyle çakışmayan <paramref name="count"/> pencere (saniye, tam sayıya yuvarlı),
    /// başlangıca göre sıralı. Pencere videodan uzunsa tüm video.
    /// </summary>
    public static IReadOnlyList<(double Start, double End)> BestWindows(IReadOnlyList<HeatPoint> points, double window, int count = 1)
    {
        if (points.Count == 0 || window <= 0 || count <= 0) return [];
        var duration = points.Max(p => p.End);
        if (window >= duration) return [(0, Math.Round(duration))];

        // Aday başlangıçlar: dilim başları ve pencerenin bir dilim sonunda bittiği yerler (optimum bunlardan birinde)
        var candidates = points.Select(p => p.Start)
            .Concat(points.Select(p => p.End - window))
            .Select(s => Math.Clamp(s, 0, duration - window))
            .Distinct()
            .Select(s => (Start: s, Score: Average(points, s, s + window)))
            .OrderByDescending(c => c.Score)
            .ThenBy(c => c.Start)
            .ToList();

        var chosen = new List<(double Start, double End)>();
        foreach (var c in candidates)
        {
            if (chosen.Count == count) break;
            if (chosen.Any(x => c.Start < x.End && c.Start + window > x.Start)) continue;
            chosen.Add((c.Start, c.Start + window));
        }
        return chosen
            .Select(x => (Math.Round(x.Start), Math.Min(Math.Round(duration), Math.Round(x.Start) + Math.Round(window))))
            .OrderBy(x => x.Item1)
            .ToList();
    }

    /// <summary>[from, to) aralığındaki ağırlıklı ortalama yoğunluk.</summary>
    public static double Average(IReadOnlyList<HeatPoint> points, double from, double to)
    {
        double sum = 0;
        foreach (var p in points)
        {
            var overlap = Math.Min(p.End, to) - Math.Max(p.Start, from);
            if (overlap > 0) sum += overlap * p.Value;
        }
        return to > from ? sum / (to - from) : 0;
    }
}
