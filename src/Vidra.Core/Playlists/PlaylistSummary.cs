using Vidra.Core.Models;
using Vidra.Core.YtDlp;

namespace Vidra.Core.Playlists;

/// <summary>Playlist ekranındaki başlık altı ve seçim çubuğu metinleri.</summary>
public static class PlaylistSummary
{
    /// <summary>"Kanal · 12 video · 1:02:03". Kanal sekmesinde başlık zaten kanal adıdır, <paramref name="channel"/> boş geçilir.</summary>
    public static string MetaLine(string? channel, int count, bool listOfLists, double seconds) =>
        Join(channel ?? "", listOfLists ? $"{count} liste" : $"{count} video", seconds > 0 ? TimeText.Format(seconds) : "");

    public static string SelectedLabel(int lists, int videos) => (lists, videos) switch
    {
        (0, var v) => $"{v} video seçili",
        (var l, 0) => $"{l} liste seçili",
        var (l, v) => $"{l} liste + {v} video seçili",
    };

    public static string SelectedLabel(IReadOnlyCollection<IPlaylistItem> selected)
    {
        var lists = selected.Count(r => r.Entry.IsList);
        return SelectedLabel(lists, selected.Count - lists);
    }

    /// <summary>
    /// Seçim çubuğundaki özet: süre ve tahmini boyut. Seçili listelerin içeriği okunduysa (Stats) yalnızca
    /// indirilmemiş videoları sayılır; biri bile okunmadıysa toplam bilinmez.
    /// </summary>
    public static string SelectedSummary(IReadOnlyCollection<IPlaylistItem> selected, bool listOfLists, double kbps)
    {
        if (selected.Count == 0) return listOfLists ? "İndirmek istediğin listeleri işaretle" : "Listeden video seç";
        var lists = selected.Where(r => r.Entry.IsList).ToList();
        var secs = selected.Where(r => !r.Entry.IsList).Sum(r => r.Entry.Duration ?? 0);
        if (lists.Count == 0) return $"{TimeText.Format(secs)} · ≈ {SizeEstimate.Text(secs, kbps)}";
        if (lists.Any(r => r.Stats is null)) return "Listelerin videoları indirirken okunur, indirilmişler atlanır";
        var count = selected.Count - lists.Count + lists.Sum(r => r.Stats!.New);
        secs += lists.Sum(r => r.Stats!.NewSeconds);
        return count == 0 ? "Seçili listelerde indirilmemiş video yok"
            : $"{count} video · {TimeText.Format(secs)} · ≈ {SizeEstimate.Text(secs, kbps)}";
    }

    internal static string Join(params string[] parts) => string.Join(" · ", parts.Where(s => s.Length > 0));
}

/// <summary>Alt liste kartındaki etiketler.</summary>
public static class ListStatsText
{
    public static string? Count(ListStats? s) => s is null ? null : $"{s.Videos} video";

    public static string? Members(ListStats? s) => s switch
    {
        { AllMembers: true } => "ÜYELERE ÖZEL",
        { Members: > 0 } => $"{s.Members} ÜYE",
        _ => null,
    };

    public static string? MembersHint(ListStats? s) => s is { Members: > 0 } ? $"Üyelere özel: {s.Members} / {s.Videos} video" : null;

    /// <param name="error">Liste okunamadıysa hata metni.</param>
    /// <param name="selectable">Erişilemeyen listede "Okunuyor…" yazmaz (hiç okunmayacak).</param>
    public static string Status(ListStats? s, string? error, bool selectable)
    {
        if (error is not null) return "Okunamadı";
        if (s is null) return selectable ? "Okunuyor…" : "";
        var time = s.Seconds > 0 ? TimeText.Format(s.Seconds) : "";
        return s.Complete ? PlaylistSummary.Join(time, "Tümü indirildi")
            : PlaylistSummary.Join(time, s.Done > 0 ? $"{s.Done}/{s.Videos} indirildi" : "");
    }
}
