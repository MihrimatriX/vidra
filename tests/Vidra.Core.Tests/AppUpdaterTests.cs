using System.Net;
using System.Security.Cryptography;
using Vidra.Core;

namespace Vidra.Core.Tests;

public class AppUpdaterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vidra-upd-" + Guid.NewGuid().ToString("N"));

    public AppUpdaterTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private const string ReleaseJson = """
        {"tag_name":"v0.7.0","html_url":"https://github.com/MihrimatriX/vidra/releases/tag/v0.7.0","body":"n",
         "assets":[
           {"name":"Vidra-Setup.exe","size":10,"digest":"sha256:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
            "browser_download_url":"https://github.com/MihrimatriX/vidra/releases/download/v0.7.0/Vidra-Setup.exe"},
           {"name":"Vidra-Portable.exe","size":20,
            "browser_download_url":"https://github.com/MihrimatriX/vidra/releases/download/v0.7.0/Vidra-Portable.exe"},
           {"name":"evil.exe","size":1,"browser_download_url":"https://example.com/MihrimatriX/vidra/releases/download/v0.7.0/evil.exe"}
         ]}
        """;

    [Fact]
    public void Parses_assets_and_digest_and_drops_foreign_urls()
    {
        var r = AppUpdate.Parse(ReleaseJson)!;
        Assert.Equal(2, r.Assets.Count);
        var setup = AppUpdate.AssetFor(r, InstallKind.Installed)!;
        Assert.Equal(10, setup.Size);
        Assert.Equal(new string('a', 64), setup.Sha256);
        var portable = AppUpdate.AssetFor(r, InstallKind.Portable)!;
        Assert.Null(portable.Sha256); // digest'siz eski sürüm: yalnızca boyut denetlenir
        Assert.Null(r.Asset("evil.exe"));
    }

    [Theory]
    [InlineData("sha256:abc", null)]
    [InlineData("md5:00", null)]
    [InlineData(null, null)]
    public void Bad_digests_are_null(string? digest, string? expected) => Assert.Equal(expected, AppUpdate.ParseDigest(digest));

    [Fact]
    public void Install_kind_follows_uninstaller()
    {
        Assert.Equal(InstallKind.Portable, AppUpdate.DetectInstallKind(_dir));
        File.WriteAllText(Path.Combine(_dir, "unins000.exe"), "");
        Assert.Equal(InstallKind.Installed, AppUpdate.DetectInstallKind(_dir));
    }

    [Fact]
    public void Setup_arguments_pin_privilege_mode()
    {
        Assert.EndsWith("/ALLUSERS", AppUpdate.SetupArguments(allUsers: true));
        Assert.EndsWith("/CURRENTUSER", AppUpdate.SetupArguments(allUsers: false));
        Assert.Contains("/VERYSILENT", AppUpdate.SetupArguments(false));
        Assert.Contains("/RELAUNCH", AppUpdate.SetupArguments(false));
    }

    private static ReleaseAsset AssetOf(byte[] data, bool withDigest = true) =>
        new("Vidra-Setup.exe", "https://github.com/MihrimatriX/vidra/releases/download/v0.7.0/Vidra-Setup.exe",
            data.Length, withDigest ? Convert.ToHexStringLower(SHA256.HashData(data)) : null);

    private sealed class FakeHandler(Func<byte[]> body) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body()) });
        }
    }

    [Fact]
    public async Task Downloads_verifies_and_reuses_verified_file()
    {
        var data = new byte[700_000];
        Random.Shared.NextBytes(data);
        var handler = new FakeHandler(() => data);
        using var http = new HttpClient(handler);
        var asset = AssetOf(data);
        var reports = new List<double?>();

        var path = await AppUpdater.DownloadAsync(http, asset, new Version(0, 7, 0), _dir, new SyncProgress(reports.Add));
        Assert.Equal("Vidra-Setup-0.7.0.exe", Path.GetFileName(path));
        Assert.Equal(data, File.ReadAllBytes(path));
        Assert.Equal(1, reports[^1]);

        var again = await AppUpdater.DownloadAsync(http, asset, new Version(0, 7, 0), _dir);
        Assert.Equal(path, again);
        Assert.Equal(1, handler.Calls); // doğrulanmış dosya yeniden indirilmez
    }

    [Fact]
    public async Task Tampered_download_is_rejected_and_deleted()
    {
        var data = new byte[1000];
        var tampered = (byte[])data.Clone();
        tampered[10] = 1;
        using var http = new HttpClient(new FakeHandler(() => tampered));
        await Assert.ThrowsAsync<InvalidDataException>(() => AppUpdater.DownloadAsync(http, AssetOf(data), new Version(0, 7, 0), _dir));
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task Without_digest_size_is_still_checked()
    {
        using var http = new HttpClient(new FakeHandler(() => new byte[999]));
        await Assert.ThrowsAsync<InvalidDataException>(() => AppUpdater.DownloadAsync(http, AssetOf(new byte[1000], withDigest: false), new Version(0, 7, 0), _dir));
    }

    [Fact]
    public void Cleanup_keeps_only_newer_downloads()
    {
        foreach (var n in new[] { "Vidra-Setup-0.6.0.exe", "Vidra-Setup-0.7.0.exe", "Vidra-Setup-0.8.0.exe", "Vidra-Setup-0.8.0.exe.download", "junk.txt" })
            File.WriteAllText(Path.Combine(_dir, n), "x");
        AppUpdater.CleanupDownloads(_dir, new Version(0, 7, 0, 0));
        Assert.Equal(["Vidra-Setup-0.8.0.exe"], Directory.GetFiles(_dir).Select(Path.GetFileName).ToArray());
    }

    [Fact]
    public void Portable_swap_replaces_exe_and_keeps_old_for_cleanup()
    {
        var exe = Path.Combine(_dir, "Vidra.exe");
        var fresh = Path.Combine(_dir, "new.exe");
        File.WriteAllText(exe, "old");
        File.WriteAllText(fresh, "new");

        AppUpdater.SwapPortable(exe, fresh);
        Assert.Equal("new", File.ReadAllText(exe));
        Assert.Equal("old", File.ReadAllText(exe + AppUpdater.OldExeSuffix));

        AppUpdater.CleanupPortable(exe);
        Assert.False(File.Exists(exe + AppUpdater.OldExeSuffix));
    }

    [Fact]
    public void Portable_swap_restores_exe_when_copy_fails()
    {
        var exe = Path.Combine(_dir, "Vidra.exe");
        File.WriteAllText(exe, "old");
        Assert.ThrowsAny<IOException>(() => AppUpdater.SwapPortable(exe, Path.Combine(_dir, "missing.exe")));
        Assert.Equal("old", File.ReadAllText(exe));
    }

    private sealed class SyncProgress(Action<double?> report) : IProgress<double?>
    {
        public void Report(double? value) => report(value);
    }
}
