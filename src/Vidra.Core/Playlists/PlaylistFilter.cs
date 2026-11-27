using System.Globalization;
using Vidra.Core.Models;

namespace Vidra.Core.Playlists;

/// <summary>Playlist ekranındaki bir satır: video ya da alt liste (kart). Arayüz katmanı kendi satır tipiyle uygular.</summary>
public interface IPlaylistItem
{
    PlaylistEntry Entry { get; }
    /// <summary>Listedeki 1 tabanlı sıra.</summary>
    int Index { get; }
    /// <summary>Daha önce indirilmiş (arşivde ya da geçmişte).</summary>
    bool Done { get; }
    /// <summary>Alt liste kartının özeti; liste henüz okunmadıysa null.</summary>
    ListStats? Stats { get; }
}

/// <summary>Arama kutusu, filtreler ve sıralama seçimi.</summary>
public sealed record PlaylistFilterCriteria
{
    public string Query { get; init; } = "";
    /// <summary>"1-20, 31" biçiminde sıra aralığı (bkz. <see cref="IndexRange"/>).</summary>
    public string Range { get; init; } = "";
    /// <summary>yyyy-MM-dd ya da yyyyMMdd; 8 haneye inmiyorsa yok sayılır.</summary>
    public string DateFrom { get; init; } = "";
    public string DateTo { get; init; } = "";
    public DurationBucket Bucket { get; init; }
    public bool OnlyNew { get; init; }
    public bool OnlyMembers { get; init; }
    public VideoSort VideoSort { get; init; }
    public ListSort ListSort { get; init; }

    /// <summary>Sonuç liste özetlerine (Stats) bağlı mı: özetler geldikçe filtre yeniden uygulanmalı.</summary>
    public bool DependsOnListStats => OnlyNew || OnlyMembers || ListSort is ListSort.Members or ListSort.Missing or ListSort.Count;
}

public static class PlaylistFilter
{
    private static readonly StringComparer TrName = StringComparer.Create(Fmt.Tr, ignoreCase: true);

    /// <summary>Tüm sayfa liste kartlarından oluşuyorsa (<paramref name="listOfLists"/>) kart filtresi, değilse video filtresi.</summary>
    public static IEnumerable<T> Apply<T>(IReadOnlyCollection<T> items, PlaylistFilterCriteria c, bool listOfLists) where T : IPlaylistItem =>
        listOfLists ? Lists(items, c) : Videos(items, c);

    public static IEnumerable<T> Videos<T>(IReadOnlyCollection<T> items, PlaylistFilterCriteria c) where T : IPlaylistItem
    {
        var q = c.Query.Trim();
        var set = IndexRange.Parse(c.Range, items.Count);
        var from = NormalizeDate(c.DateFrom);
        var to = NormalizeDate(c.DateTo);
        var (min, max) = BucketBounds(c.Bucket);

        var visible = items.Where(r =>
            (set is null || set.Contains(r.Index)) &&
            (from is null || r.Entry.UploadDate is null || string.CompareOrdinal(r.Entry.UploadDate, from) >= 0) &&
            (to is null || r.Entry.UploadDate is null || string.CompareOrdinal(r.Entry.UploadDate, to) <= 0) &&
            (c.Bucket == DurationBucket.All || (r.Entry.Duration is { } d && d >= min && d < max)) &&
            (q.Length == 0 || Matches(r.Entry.Title, q)) &&
            (!c.OnlyNew || !r.Done) &&
            (!c.OnlyMembers || r.Entry.MembersOnly));

        return c.VideoSort switch
        {
            VideoSort.Reverse => visible.OrderByDescending(r => r.Index),
            VideoSort.Newest => visible.OrderByDescending(r => r.Entry.UploadDate ?? "", StringComparer.Ordinal).ThenBy(r => r.Index),
            VideoSort.Longest => visible.OrderByDescending(r => r.Entry.Duration ?? 0).ThenBy(r => r.Index),
            VideoSort.Views => visible.OrderByDescending(r => r.Entry.Views ?? 0).ThenBy(r => r.Index),
            _ => visible.OrderBy(r => r.Index),
        };
    }

    /// <summary>Liste kartları. Özeti henüz gelmemiş liste "Yalnızca yeniler"de görünür kalır, sıralamalarda sona düşer.</summary>
    public static IEnumerable<T> Lists<T>(IEnumerable<T> items, PlaylistFilterCriteria c) where T : IPlaylistItem
    {
        var q = c.Query.Trim();
        var visible = items.Where(r =>
            (q.Length == 0 || Matches(r.Entry.Title, q) || (r.Entry.Channel is { } ch && Matches(ch, q))) &&
            (!c.OnlyNew || r.Stats is null || r.Stats.New > 0) &&
            (!c.OnlyMembers || r.Stats is { Members: > 0 }));

        return c.ListSort switch
        {
            ListSort.Members => visible.OrderByDescending(r => r.Stats?.Members ?? -1).ThenBy(r => r.Index),
            ListSort.Missing => visible.OrderByDescending(r => r.Stats?.New ?? -1).ThenBy(r => r.Index),
            ListSort.Count => visible.OrderByDescending(r => r.Stats?.Videos ?? -1).ThenBy(r => r.Index),
            ListSort.Name => visible.OrderBy(r => r.Entry.Title, TrName).ThenBy(r => r.Index),
            _ => visible.OrderBy(r => r.Index),
        };
    }

    /// <summary>Saniye cinsinden [Min, Max) aralığı.</summary>
    public static (double Min, double Max) BucketBounds(DurationBucket b) => b switch
    {
        DurationBucket.Short => (0, 240),
        DurationBucket.Mid => (240, 1200),
        DurationBucket.Long => (1200, double.MaxValue),
        _ => (0, double.MaxValue),
    };

    /// <summary>
    /// Büyük/küçük harf duyarsız arama; sonuç makinenin dil ayarına bağlı değildir. Önce Türkçe kurallar
    /// ("ılık" ↔ "ILIK", "istanbul" ↔ "İSTANBUL"), sonra aksan duyarsız değişmez kurallar denenir
    /// ("ISTANBUL" ↔ "İstanbul", "corba" ↔ "Çorba"): Türkçe klavyesi olmayan ya da İngilizce yazan da bulur.
    /// </summary>
    public static bool Matches(string text, string query) =>
        Fmt.Tr.CompareInfo.IndexOf(text, query, CompareOptions.IgnoreCase) >= 0 ||
        CultureInfo.InvariantCulture.CompareInfo.IndexOf(text, query, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0;

    /// <summary>"Tümü" düğmesinin seçeceği satır: indirilmemiş video ya da eksik videosu olan (veya henüz okunmamış) liste.</summary>
    public static bool IsBulkCandidate(IPlaylistItem r, bool listOfLists) =>
        !r.Entry.Unavailable && (listOfLists ? r.Stats is null || r.Stats.New > 0 : !r.Done);

    private static string? NormalizeDate(string s)
    {
        var d = s.Replace("-", "").Trim();
        return d.Length == 8 ? d : null;
    }
}
