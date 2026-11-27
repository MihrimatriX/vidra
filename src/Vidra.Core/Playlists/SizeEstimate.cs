using Vidra.Core.Models;

namespace Vidra.Core.Playlists;

/// <summary>Flat listede format bilgisi yok: boyutu süre × tipik YouTube bit hızıyla tahmin ederiz.</summary>
public static class SizeEstimate
{
    /// <summary>Çözünürlüğe göre tipik video bit hızı (kbps); 0 = "En iyi".</summary>
    private static readonly Dictionary<int, double> TypicalVideoKbps = new()
    {
        [0] = 9000, [2160] = 18000, [1440] = 9000, [1080] = 4300, [720] = 2500, [480] = 1200, [360] = 700,
    };

    /// <summary>Videoya eklenen ses akışı (kbps).</summary>
    private const double AudioTrackKbps = 130;

    /// <summary>Tabloda olmayan çözünürlükte 1080p varsayılır.</summary>
    public static double Kbps(DownloadMode mode, int height, AudioFormat audio) => mode == DownloadMode.Audio
        ? audio switch { AudioFormat.Mp3 => 245, AudioFormat.Flac => 900, AudioFormat.Wav => 1411, _ => 150 }
        : (TypicalVideoKbps.TryGetValue(height, out var k) ? k : TypicalVideoKbps[1080]) + AudioTrackKbps;

    public static double Bytes(double seconds, double kbps) => seconds * kbps * 1000 / 8;

    /// <summary>"≈ 1,2 GB" içindeki boyut; süre bilinmiyorsa "—".</summary>
    public static string Text(double seconds, double kbps) => seconds > 0 ? Fmt.Bytes(Bytes(seconds, kbps)) : "—";
}
