using System.Globalization;

namespace Vidra.Core.Playlists;

public static class IndexRange
{
    /// <summary>
    /// "1-20, 31" → {1..20, 31}; 1 tabanlı, <paramref name="max"/> ile sınırlı. "5-" sona kadar demektir.
    /// Tire çevresindeki boşluklar ("1 - 5") yok sayılır. Boşsa ya da geçersizse null (filtre yok).
    /// </summary>
    public static HashSet<int>? Parse(string? input, int max)
    {
        var s = input?.Trim() ?? "";
        if (s.Length == 0) return null;
        var set = new HashSet<int>();
        foreach (var raw in s.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var part = raw.Trim();
            var dash = part.IndexOf('-');
            if (dash >= 0)
            {
                if (!TryInt(part[..dash], out var a)) return null;
                var rest = part[(dash + 1)..].Trim();
                int b;
                if (rest.Length == 0) b = max;
                else if (!TryInt(rest, out b)) return null;
                for (var i = Math.Max(1, a); i <= Math.Min(b, max); i++) set.Add(i);
            }
            else if (TryInt(part, out var n)) { if (n >= 1 && n <= max) set.Add(n); }
            else return null;
        }
        return set;
    }

    private static bool TryInt(string s, out int n) =>
        int.TryParse(s.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out n);
}
