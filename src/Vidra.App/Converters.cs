using System;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Vidra.Core;
using Vidra.Core.Storage;
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
    // Ekranda aynı anda ~100 kapak olur; 800 ile geri kaydırınca da bellekte kalırlar
    private static readonly LruCache<string, ImageSource> Cache = new(800);

    /// <summary>Disk önbelleği klasörü; açılışta AppHost ayarlar. Null ise yalnızca bellek önbelleği.</summary>
    public static string? DiskDir { get; set; }

    public object? Convert(object? value, Type t, object? p, CultureInfo c)
    {
        if (value is not string url || !Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        var width = p is string s && int.TryParse(s, out var w) ? w : 320;
        var key = $"{url}|{width}";
        return Cache.GetOrAdd(key, _ => FromDisk(url, width) ?? FromNetwork(uri, url, width, key));
    }

    /// <summary>Daha önce inmiş kapak: küçük yerel JPEG, ağ beklemesi yok.</summary>
    private static BitmapImage? FromDisk(string url, int width)
    {
        if (DiskDir is not { } dir) return null;
        var path = ThumbnailDiskCache.PathFor(dir, url, width);
        if (!File.Exists(path)) return null;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.StreamSource = fs;
            bmp.CacheOption = BitmapCacheOption.OnLoad; // akış kapanmadan çöz
            bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or FileFormatException)
        {
            // Yarım/bozuk dosya: sil, ağdan yeniden insin
            try { File.Delete(path); } catch (Exception) { /* yoksay */ }
            return null;
        }
    }

    private static BitmapImage FromNetwork(Uri uri, string url, int width, string key)
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.UriSource = uri;
        bmp.DecodePixelWidth = width;
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        bmp.EndInit();
        // Başarısız indirme önbellekte kalmasın; bir sonraki görüntülemede yeniden denenir
        bmp.DownloadFailed += (_, _) => Cache.Remove(key);
        bmp.DownloadCompleted += (_, _) => SaveToDisk(bmp, url, width);
        return bmp;
    }

    /// <summary>Küçültülmüş hâli JPEG olarak yazar: kapak başına birkaç KB.</summary>
    private static void SaveToDisk(BitmapSource bmp, string url, int width)
    {
        if (DiskDir is not { } dir) return;
        try
        {
            var enc = new JpegBitmapEncoder { QualityLevel = 85 };
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using var ms = new MemoryStream();
            enc.Save(ms);
            var bytes = ms.ToArray();
            var path = ThumbnailDiskCache.PathFor(dir, url, width);
            _ = Task.Run(() =>
            {
                try
                {
                    Directory.CreateDirectory(dir);
                    var tmp = path + ".tmp";
                    File.WriteAllBytes(tmp, bytes);
                    File.Move(tmp, path, overwrite: true);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* önbellek yazılamazsa ağdan iner */ }
            });
        }
        catch (Exception e) when (e is IOException or NotSupportedException or InvalidOperationException) { /* kodlanamadı: yalnızca bellekte kalır */ }
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

/// <summary>Icons.xaml anahtarı ("I.Download") → geometri; görünüm modelleri ikonu adıyla taşır (komut paleti).</summary>
public sealed class IconKeyConverter : IValueConverter
{
    public object? Convert(object? value, Type t, object? p, CultureInfo c) =>
        value is string key ? Application.Current.TryFindResource(key) as Geometry : null;

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}
