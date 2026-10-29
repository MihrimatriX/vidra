using Vidra.Core.Models;

namespace Vidra.Core.Queue;

/// <summary>
/// Kuyruğun tamamı için tek bir ilerleme: görev çubuğu, tepsi ipucu ve kuyruk özeti bunu gösterir.
/// Video işlerinde önce video sonra ses akışı ayrı ayrı %0→100 gider; toplamda video akışı ağırlıklı sayılır,
/// böylece çubuk geri sarmaz.
/// </summary>
public sealed record QueueProgress(int Running, int Queued, int Paused, int Errors, double Fraction, double Speed)
{
    private const double VideoShare = 0.9;

    public bool IsEmpty => Running + Queued + Paused + Errors == 0;

    /// <summary>Tam sayı yüzde (aşağı yuvarlanır: bitmeden %100 görünmesin).</summary>
    public int Percent => (int)Math.Floor(Fraction * 100);

    public static QueueProgress From(IEnumerable<DownloadJob> jobs)
    {
        int running = 0, queued = 0, paused = 0, errors = 0, counted = 0;
        double sum = 0, speed = 0;
        foreach (var j in jobs)
        {
            switch (j.Status)
            {
                case JobStatus.Running or JobStatus.Processing: running++; speed += j.Speed ?? 0; break;
                case JobStatus.Queued: queued++; break;
                case JobStatus.Paused: paused++; break;
                case JobStatus.Error: errors++; continue;
                default: continue;
            }
            counted++;
            sum += JobFraction(j);
        }
        return new QueueProgress(running, queued, paused, errors, counted == 0 ? 0 : sum / counted, speed);
    }

    /// <summary>Tek işin 0..1 arası ilerlemesi (akışlar ağırlıklı, son işlem adımı ≈ bitti).</summary>
    public static double JobFraction(DownloadJob j)
    {
        if (j.Status == JobStatus.Processing) return 0.99;
        var p = Math.Clamp(j.Percent / 100, 0, 1);
        if (j.Options.Mode != DownloadMode.Video) return p;
        return j.Stream <= 1 ? p * VideoShare : VideoShare + p * (1 - VideoShare);
    }

    /// <summary>"2 indiriliyor · 3 sırada · %43" — boş kuyrukta "Kuyruk boş".</summary>
    public string Describe()
    {
        if (IsEmpty) return "Kuyruk boş";
        var parts = new List<string>();
        if (Running > 0) parts.Add($"{Running} indiriliyor");
        if (Queued > 0) parts.Add($"{Queued} sırada");
        if (Paused > 0) parts.Add($"{Paused} duraklatıldı");
        if (Errors > 0) parts.Add($"{Errors} hata");
        if (Running + Queued + Paused > 0) parts.Add($"%{Percent}");
        return string.Join(" · ", parts);
    }
}
