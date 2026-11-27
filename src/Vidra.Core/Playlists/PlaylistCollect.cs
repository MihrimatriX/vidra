using Vidra.Core.Models;
using Vidra.Core.YtDlp;

namespace Vidra.Core.Playlists;

/// <summary>Kuyruğa ya da sepete gidecek video ve ait olduğu liste (klasör adı, sıra, liste bağlantısı, kanal).</summary>
/// <param name="Index">Kendi listesindeki 1 tabanlı sıra.</param>
/// <param name="WasDone">Daha önce indirilmiş ama elle seçilmiş: arşiv atlanır, yeniden iner.</param>
public sealed record CollectItem(PlaylistEntry Entry, string List, int Index, string? ListUrl, string? Channel, bool WasDone);

public static class PlaylistCollect
{
    /// <summary>
    /// Okunmuş bir alt listeden indirilecekler: alt listeler, erişilemeyenler ve arşivdekiler atlanır;
    /// <paramref name="onlyMembers"/> ise yalnızca üyelere özel videolar. Sıra numarası listedeki yeridir (atlananlar boşluk bırakır).
    /// </summary>
    /// <param name="list">Üst sayfadaki liste satırı (başlık ve bağlantı buradan).</param>
    /// <param name="resolved">Listenin okunmuş içeriği.</param>
    /// <param name="fallbackChannel">Ne liste ne satır kanal taşıyorsa (açık sayfanın kanalı).</param>
    public static List<CollectItem> Pending(PlaylistEntry list, PlaylistInfo resolved, string? fallbackChannel,
        IReadOnlySet<string> archived, bool onlyMembers = false)
    {
        var channel = !string.IsNullOrWhiteSpace(resolved.Channel) ? resolved.Channel : list.Channel ?? fallbackChannel;
        var listUrl = YouTubeUrl.CanonicalSourceUrl(list.Url);
        return resolved.Entries
            .Select((e, n) => new CollectItem(e, list.Title, n + 1, listUrl, channel, false))
            .Where(x => !x.Entry.IsList && !x.Entry.Unavailable && !archived.Contains(x.Entry.Id) && (!onlyMembers || x.Entry.MembersOnly))
            .ToList();
    }

    /// <summary>
    /// Kuyruktaki sıra numaraları. <paramref name="reverse"/> ise her liste kendi içinde ters çevrilip 1..N yeniden
    /// numaralanır (Üyeler sekmesi ve yeniden eskiye sıralı kurslar), listeler ilk göründükleri sırada kalır.
    /// </summary>
    public static List<(CollectItem Item, int Index)> Number(IEnumerable<CollectItem> items, bool reverse)
    {
        if (!reverse) return items.Select(x => (x, x.Index)).ToList();
        var result = new List<(CollectItem, int)>();
        foreach (var g in items.GroupBy(x => (x.List, x.ListUrl)))
        {
            var rev = g.OrderByDescending(x => x.Index).ToList();
            for (var i = 0; i < rev.Count; i++) result.Add((rev[i], i + 1));
        }
        return result;
    }
}
