using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Vidra.Core;
using Vidra.Core.Queue;

namespace Vidra.App.ViewModels;

/// <summary>Günlük çubuk: yükseklik piksel cinsinden (grafik alanı ChartHeight).</summary>
public sealed record DayBar(string Label, string Tip, double Height, bool IsToday);

public sealed record ChannelBar(string Channel, string Detail, double Fraction);

public sealed partial class StatsViewModel : ObservableObject
{
    public const double ChartHeight = 150;
    private readonly AppHost _host;

    public StatsViewModel(AppHost host)
    {
        _host = host;
        // Geçmişi geri almak yüzlerce ekleme üretir: her biri için tüm istatistiği yeniden hesaplama
        var refresh = Ui.Coalesce(Refresh);
        host.Queue.History.CollectionChanged += (_, _) => refresh();
        Refresh();
    }

    [ObservableProperty] private bool _hasData;
    [ObservableProperty] private string _countText = "0";
    [ObservableProperty] private string _bytesText = "—";
    [ObservableProperty] private string _durationText = "—";
    [ObservableProperty] private string _weekText = "0";
    [ObservableProperty] private string _weekBytesText = "—";
    [ObservableProperty] private int _videoCount;
    [ObservableProperty] private int _audioCount;
    [ObservableProperty] private IReadOnlyList<DayBar> _days = [];
    [ObservableProperty] private IReadOnlyList<ChannelBar> _channels = [];

    public void Refresh()
    {
        var s = HistoryStats.From(_host.Queue.History, DateTimeOffset.Now);
        HasData = s.Count > 0;
        CountText = Fmt.Count(s.Count);
        BytesText = Fmt.Bytes(s.Bytes);
        var t = TimeSpan.FromSeconds(s.Seconds);
        DurationText = t.TotalHours >= 1 ? $"{(int)t.TotalHours} sa {t.Minutes} dk" : $"{t.Minutes} dk";
        WeekText = Fmt.Count(s.WeekCount);
        WeekBytesText = Fmt.Bytes(s.WeekBytes);
        VideoCount = s.VideoCount;
        AudioCount = s.AudioCount;

        var max = Math.Max(1, s.Days.Max(d => d.Count));
        var today = s.Days[^1].Day;
        // Boş gün de 2px'lik bir taban çizgisi olarak görünür: eksenin sürekliliği okunur
        Days = s.Days.Select(d => new DayBar(
            d.Day.ToString("dd", Fmt.Tr),
            $"{d.Day.ToString("d MMMM ddd", Fmt.Tr)} · {d.Count} indirme · {Fmt.Bytes(d.Bytes)}",
            d.Count == 0 ? 2 : Math.Max(6, d.Count / (double)max * ChartHeight),
            d.Day == today)).ToList();

        var top = Math.Max(1, s.TopChannels.FirstOrDefault()?.Count ?? 1);
        Channels = s.TopChannels.Select(c => new ChannelBar(c.Channel, $"{c.Count} video · {Fmt.Bytes(c.Bytes)}", c.Count / (double)top)).ToList();
    }
}
