using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Vidra.Core;
using Vidra.Core.YtDlp;

namespace Vidra.App;

/// <summary>true → Visible (Parameter="!" ile tersine).</summary>
public sealed class BoolVisConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c)
    {
        var b = value switch
        {
            bool x => x,
            int n => n != 0,
            string s => s.Length > 0,
            null => false,
            _ => true,
        };
        if (p is "!") b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) => value is not true;
    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => value is not true;
}

public sealed class BytesConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) => value switch
    {
        long l => Fmt.Bytes(l),
        double d => p is "speed" ? Fmt.Speed(d) : Fmt.Bytes(d),
        int i => Fmt.Bytes(i),
        _ => "—",
    };

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class DurationConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) => TimeText.Format(value as double?);
    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class RelativeTimeConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) => value is DateTimeOffset d ? Fmt.Relative(d) : "";
    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>
/// Küçük resim URL'si → önbellekli BitmapImage. Aynı kapak listede birçok kez geçer;
/// WPF'in kendi indiricisi asenkron çalışır, arayüzü bloklamaz.
/// </summary>
public sealed class ThumbConverter : IValueConverter
{
    private static readonly ConcurrentDictionary<string, ImageSource> Cache = new();

    public object? Convert(object? value, Type t, object? p, CultureInfo c)
    {
        if (value is not string url || !Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        var width = p is string s && int.TryParse(s, out var w) ? w : 320;
        // Binlerce satırlık kanallarda sınırsız büyümesin
        if (Cache.Count > 600) Cache.Clear();
        return Cache.GetOrAdd($"{url}|{width}", _ =>
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = uri;
            bmp.DecodePixelWidth = width;
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bmp.EndInit();
            // Başarısız indirme önbellekte kalmasın; bir sonraki görüntülemede yeniden denenir
            bmp.DownloadFailed += (s2, e2) => Cache.TryRemove($"{url}|{width}", out ImageSource? _);
            return bmp;
        });
    }

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>Değer parametreye eşitse true (enum/string karşılaştırması).</summary>
public sealed class EqualsConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) => string.Equals(value?.ToString(), p?.ToString(), StringComparison.Ordinal);
    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>Değer parametreye eşitse Visible (sekme içerikleri için).</summary>
public sealed class EqualsVisConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) =>
        string.Equals(value?.ToString(), p?.ToString(), StringComparison.Ordinal) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>0..1 oranı → yıldızlı sütun genişliği (zaman çizelgesi).</summary>
public sealed class StarConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) =>
        new GridLength(Math.Max(0, value is double d ? d : 0), GridUnitType.Star);

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>DownloadOptions → "1080p · MP4" gibi kısa açıklama.</summary>
public sealed class DescribeConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) => value is Vidra.Core.Models.DownloadOptions o ? o.Describe() : "";
    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}
