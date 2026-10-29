using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using Vidra.Core.YtDlp;

namespace Vidra.App.Services;

/// <summary>
/// Pencereye sürüklenen veriden YouTube bağlantılarını okur: tarayıcıdan sürüklenen bağlantı ya da metin,
/// masaüstündeki .url kısayolları ve bağlantı listesi .txt dosyaları.
/// </summary>
public static class LinkDrop
{
    private const string UrlFormat = "UniformResourceLocatorW";
    private const long MaxFileBytes = 1_000_000;

    /// <summary>Sürükleme girerken: dosyada yalnızca uzantıya bakılır (içerik bırakınca okunur).</summary>
    public static bool CanAccept(IDataObject d)
    {
        try
        {
            if (d.GetDataPresent(DataFormats.FileDrop)) return d.GetData(DataFormats.FileDrop) is string[] files && files.Any(IsLinkFile);
            return ReadText(d) is { } text && YouTubeUrl.ExtractLinks(text).Count > 0;
        }
        catch (COMException) { return false; } // bazı kaynaklar biçimi listeleyip vermez
    }

    public static string? ReadText(IDataObject d)
    {
        try
        {
            if (d.GetDataPresent(DataFormats.FileDrop))
            {
                if (d.GetData(DataFormats.FileDrop) is not string[] files) return null;
                var sb = new StringBuilder();
                foreach (var f in files.Where(IsLinkFile))
                {
                    try { if (new FileInfo(f).Length <= MaxFileBytes) sb.AppendLine(File.ReadAllText(f)); }
                    catch (IOException) { }
                    catch (System.UnauthorizedAccessException) { }
                }
                return sb.ToString();
            }
            if (d.GetDataPresent(DataFormats.UnicodeText) && d.GetData(DataFormats.UnicodeText) is string s) return s;
            if (d.GetDataPresent(UrlFormat) && d.GetData(UrlFormat) is MemoryStream ms) return Encoding.Unicode.GetString(ms.ToArray()).TrimEnd('\0');
            return d.GetDataPresent(DataFormats.Text) ? d.GetData(DataFormats.Text) as string : null;
        }
        catch (COMException) { return null; }
    }

    private static bool IsLinkFile(string f) => Path.GetExtension(f).ToLowerInvariant() is ".url" or ".txt";
}
