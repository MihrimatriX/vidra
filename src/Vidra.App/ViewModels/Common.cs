using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using Vidra.Core.Models;

namespace Vidra.App.ViewModels;

/// <summary>Bölümlü kontroller ve açılır listeler için değer + etiket.</summary>
public sealed record Choice(object Value, string Label)
{
    public override string ToString() => Label;
}

public static class Choices
{
    public static readonly IReadOnlyList<Choice> Containers =
        [new(ContainerFormat.Original, "Orijinal"), new(ContainerFormat.Mp4, "MP4"), new(ContainerFormat.Mkv, "MKV"), new(ContainerFormat.Webm, "WebM")];

    public static readonly IReadOnlyList<Choice> Codecs =
        [new(CodecPreference.Auto, "Otomatik"), new(CodecPreference.H264, "H.264"), new(CodecPreference.Vp9, "VP9"), new(CodecPreference.Av1, "AV1")];

    public static readonly IReadOnlyList<Choice> Fps = [new(false, "En yüksek"), new(true, "≤ 30")];

    public static readonly IReadOnlyList<Choice> Modes = [new(DownloadMode.Video, "Video"), new(DownloadMode.Audio, "Ses")];

    public static readonly IReadOnlyList<Choice> Heights =
        [new(0, "En iyi"), new(2160, "2160p"), new(1440, "1440p"), new(1080, "1080p"), new(720, "720p"), new(480, "480p"), new(360, "360p")];

    /// <summary>Opus listede yok: Orijinal zaten Opus akışını dönüştürmeden kaydeder (eski ayarlarda kalmışsa yine çalışır).</summary>
    public static readonly IReadOnlyList<AudioFormat> AudioOrder = [AudioFormat.Original, AudioFormat.Mp3, AudioFormat.M4a, AudioFormat.Flac, AudioFormat.Wav];

    public static readonly IReadOnlyList<Choice> AudioFormats = AudioOrder.Select(a => new Choice(a, a.Label())).ToList();

    public static readonly IReadOnlyList<Choice> Themes =
        [new(ThemeMode.Light, "Açık"), new(ThemeMode.Dark, "Koyu"), new(ThemeMode.System, "Sistem")];

    public static readonly IReadOnlyList<Choice> Channels = [new(UpdateChannel.Stable, "Kararlı"), new(UpdateChannel.Nightly, "Nightly")];

    public static readonly IReadOnlyList<Choice> ChannelTabs =
        [new(ChannelTab.Videos, "Videolar"), new(ChannelTab.Shorts, "Shorts"), new(ChannelTab.Streams, "Canlı"), new(ChannelTab.Playlists, "Playlistler"), new(ChannelTab.Membership, "Üyeler")];

    public static readonly IReadOnlyList<Choice> RateLimits =
        [new("", "Sınırsız"), new("10M", "10 MB/s"), new("5M", "5 MB/s"), new("2M", "2 MB/s"), new("1M", "1 MB/s")];

    public static readonly IReadOnlyList<Choice> Concurrency = Enumerable.Range(1, 6).Select(n => new Choice(n, n.ToString(CultureInfo.InvariantCulture))).ToList();
    public static readonly IReadOnlyList<Choice> Fragments = new[] { 1, 2, 4, 8 }.Select(n => new Choice(n, n.ToString(CultureInfo.InvariantCulture))).ToList();
    public static readonly IReadOnlyList<Choice> Retries = new[] { 3, 10, 25 }.Select(n => new Choice(n, n.ToString(CultureInfo.InvariantCulture))).ToList();
    public static readonly IReadOnlyList<Choice> FollowIntervals =
        [new(0, "Kapalı"), new(1, "Saatte bir"), new(6, "6 saatte bir"), new(12, "12 saatte bir"), new(24, "Günde bir")];

    public static readonly IReadOnlyList<Choice> DurationBuckets =
        [new("all", "Hepsi"), new("short", "< 4 dk"), new("mid", "4–20 dk"), new("long", "> 20 dk")];
}

/// <summary>Seçilebilir etiket (altyazı dili, SponsorBlock kategorisi).</summary>
public sealed partial class Toggle(string id, string label, string? hint, bool on) : ObservableObject
{
    public string Id { get; } = id;
    public string Label { get; } = label;
    public string? Hint { get; } = hint;
    [ObservableProperty] private bool _isOn = on;
}

/// <summary>
/// Sanallaştırılmış kart ızgarası: öğeler satırlara bölünür, satırlar VirtualizingStackPanel'de çizilir.
/// WrapPanel sanallaştırmaz; binlerce videoluk bir kanalda binlerce kart ve aynı anda binlerce kapak indirmesi olurdu.
/// Görünüm genişlik değişince Resize çağırır.
/// </summary>
public sealed partial class CardGrid : ObservableObject
{
    private const double MinCard = 230;
    private IReadOnlyList<object> _items = [];

    public System.Collections.ObjectModel.ObservableCollection<object[]> Rows { get; } = [];
    [ObservableProperty] private int _columns = 4;
    /// <summary>16:9 kapak yüksekliği; kart genişliğinden (iç boşluk ve aralık düşülerek) hesaplanır.</summary>
    [ObservableProperty] private double _thumbHeight = 124;

    public void SetItems(IEnumerable<object> items)
    {
        _items = items.ToList();
        Rebuild();
    }

    public void Resize(double width)
    {
        if (width <= 0) return;
        var cols = Math.Max(1, (int)(width / MinCard));
        ThumbHeight = Math.Max(60, Math.Round((width / cols - 26) * 9 / 16));
        if (cols == Columns && Rows.Count > 0) return;
        Columns = cols;
        Rebuild();
    }

    private void Rebuild()
    {
        Rows.Clear();
        foreach (var r in _items.Chunk(Columns)) Rows.Add(r);
    }
}

public static class Ui
{
    /// <summary>
    /// Aynı arayüz turunda gelen çağrıları tek çağrıya indirir (ör. koleksiyona art arda 1000 ekleme → bir yenileme).
    /// Uygulama dışında (testler) doğrudan çağırır.
    /// </summary>
    public static Action Coalesce(Action action)
    {
        var pending = false;
        return () =>
        {
            var d = System.Windows.Application.Current?.Dispatcher;
            if (d is null) { action(); return; }
            if (pending) return;
            pending = true;
            d.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () =>
            {
                pending = false;
                action();
            });
        };
    }
}

/// <summary>RadioButton ↔ enum için iki yönlü dönüştürücü.</summary>
public sealed class EnumBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.Ordinal);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true && parameter is string s ? Enum.Parse(targetType, s) : Binding.DoNothing;
}
