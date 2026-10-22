using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vidra.Core.Storage;

/// <summary>Atomik JSON okuma/yazma: önce .tmp'ye yazar, sonra yer değiştirir — çökme yarım dosya bırakmaz.</summary>
public static class JsonStore
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // null da yazılmalı: ör. Height = null "en iyi" demek, atlanırsa varsayılan 1080 geri gelir
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private static readonly SemaphoreSlim WriteGate = new(1, 1);

    public static T? Read<T>(string path)
    {
        try
        {
            if (!File.Exists(path)) return default;
            using var fs = File.OpenRead(path);
            return JsonSerializer.Deserialize<T>(fs, Options);
        }
        catch (Exception e) when (e is JsonException or IOException or NotSupportedException)
        {
            // Bozuk dosya: yedekle ve varsayılanlarla devam et
            TryBackup(path);
            return default;
        }
    }

    public static async Task WriteAsync<T>(string path, T value, CancellationToken ct = default)
    {
        await WriteGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var tmp = path + ".tmp";
            await using (var fs = File.Create(tmp))
                await JsonSerializer.SerializeAsync(fs, value, Options, ct).ConfigureAwait(false);
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            WriteGate.Release();
        }
    }

    private static void TryBackup(string path)
    {
        try { File.Copy(path, path + ".bad", overwrite: true); } catch { /* yoksay */ }
    }
}
