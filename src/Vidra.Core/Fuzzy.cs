namespace Vidra.Core;

/// <summary>
/// Komut paleti için bulanık eşleştirme. Sorgudaki her kelime metinde ya bütün olarak ya da harfleri sırayla geçmeli;
/// bütün eşleşme, kelime başı ve ardışık harfler daha yüksek puan alır. Türkçe harfler sadeleştirilir
/// (ş→s, ı/İ→i…), böylece "gecmis" "Geçmiş"i, "kyrk" "Kuyruk"u bulur.
/// </summary>
public static class Fuzzy
{
    /// <returns>Eşleşmiyorsa null; eşleşiyorsa büyük olan daha iyi.</returns>
    public static int? Score(string query, string text)
    {
        var words = Fold(query).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return 0;
        var t = Fold(text);
        var total = 0;
        foreach (var w in words)
        {
            if (WordScore(w, t) is not { } s) return null;
            total += s;
        }
        return total;
    }

    private static int? WordScore(string q, string t)
    {
        var idx = t.IndexOf(q, StringComparison.Ordinal);
        if (idx >= 0) return 1000 + (IsWordStart(t, idx) ? 200 : 0) - idx - (t.Length - q.Length) / 8;

        int score = 0, prev = -2, qi = 0;
        for (var i = 0; i < t.Length && qi < q.Length; i++)
        {
            if (t[i] != q[qi]) continue;
            score += 10 + (i == prev + 1 ? 15 : 0) + (IsWordStart(t, i) ? 20 : 0);
            if (prev >= 0) score -= Math.Min(i - prev - 1, 10);
            prev = i;
            qi++;
        }
        return qi == q.Length ? score : null;
    }

    private static bool IsWordStart(string t, int i) => i == 0 || !char.IsLetterOrDigit(t[i - 1]);

    /// <summary>Küçük harf + Türkçe/aksanlı harfleri ASCII karşılığına indirger.</summary>
    public static string Fold(string s)
    {
        var a = new char[s.Length];
        for (var i = 0; i < s.Length; i++)
        {
            a[i] = s[i] switch
            {
                'I' or 'İ' or 'ı' => 'i',
                'Ç' or 'ç' => 'c',
                'Ğ' or 'ğ' => 'g',
                'Ö' or 'ö' => 'o',
                'Ş' or 'ş' => 's',
                'Ü' or 'ü' => 'u',
                var c => char.ToLowerInvariant(c),
            };
        }
        return new string(a);
    }
}
