using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;
using Vidra.Core.Models;
using ThemeMode = Vidra.Core.Models.ThemeMode;

namespace Vidra.App.Services;

/// <summary>
/// Açık/koyu/sistem tema. Renk sözlüğü App.Resources'un ilk birleşik sözlüğüdür;
/// stiller DynamicResource kullandığı için değiştirmek tüm pencereleri anında günceller.
/// </summary>
public static class ThemeService
{
    private static readonly Uri Light = new("pack://application:,,,/Themes/Light.xaml");
    private static readonly Uri Dark = new("pack://application:,,,/Themes/Dark.xaml");

    public static bool IsDark { get; private set; }

    public static void Apply(ThemeMode mode)
    {
        IsDark = mode == ThemeMode.Dark || (mode == ThemeMode.System && SystemPrefersDark());
        var dicts = Application.Current.Resources.MergedDictionaries;
        var palette = new ResourceDictionary { Source = IsDark ? Dark : Light };
        // App.xaml'dan gelen sözlüğün Source'u göreli ("Themes/Light.xaml"), bizimki pack URI — dosya adına göre bul
        var old = dicts.FirstOrDefault(d => d.Source?.OriginalString is { } o &&
            (o.EndsWith("Light.xaml", StringComparison.OrdinalIgnoreCase) || o.EndsWith("Dark.xaml", StringComparison.OrdinalIgnoreCase)));
        if (old is not null) dicts[dicts.IndexOf(old)] = palette;
        else dicts.Insert(0, palette);

        foreach (Window w in Application.Current.Windows) ApplyTitleBar(w);
    }

    /// <summary>Windows 10 1809+ / 11'de başlık çubuğunu temaya uydurur.</summary>
    public static void ApplyTitleBar(Window w)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero) return;
        var on = IsDark ? 1 : 0;
        _ = DwmSetWindowAttribute(hwnd, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref on, sizeof(int));
        // Windows 11: yuvarlatılmış köşeler
        var round = 2; // DWMWCP_ROUND
        _ = DwmSetWindowAttribute(hwnd, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref round, sizeof(int));
    }

    /// <summary>Görev çubuğu açık temada mı (küçük resim düğmesi ikonlarının rengi için; uygulama temasından ayrı bir ayar).</summary>
    public static bool TaskbarIsLight()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("SystemUsesLightTheme") is int v && v == 1;
        }
        catch { return false; }
    }

    public static bool SystemPrefersDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch { return false; }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
