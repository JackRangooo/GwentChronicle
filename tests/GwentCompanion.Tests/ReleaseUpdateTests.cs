using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GwentCompanion.Core.Data;

internal static class ReleaseUpdateTests
{
    public static async Task RunPackageAsync(string path)
    {
        var source = Path.GetFullPath(path);
        await using var input = File.OpenRead(source);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(input));
        var root = Path.Combine(Path.GetTempPath(), "gwent-package-gate-" + Guid.NewGuid().ToString("N"));
        var tag = "v99.0.0"; var prefix = $"https://github.com/{ReleaseUpdates.Repository}/releases/download/{tag}/";
        var release = new AvailableRelease(new Version(99,0,0),tag,new Uri(prefix),new Uri(prefix+ReleaseUpdates.PackageName),new Uri(prefix+ReleaseUpdates.PackageName+".sha256"));
        using var http = new HttpClient(new Handler(request => request.RequestUri!.AbsoluteUri.EndsWith(".sha256",StringComparison.Ordinal)
            ? new(HttpStatusCode.OK) {Content=new StringContent(hash+"  "+ReleaseUpdates.PackageName)}
            : new(HttpStatusCode.OK) {Content=new StreamContent(File.OpenRead(source))}));
        try
        {
            var downloaded = await new ReleaseUpdates(http).DownloadAsync(release,root);
            if(new FileInfo(downloaded).Length != input.Length) throw new InvalidDataException("Complete release changed during update validation.");
            Console.WriteLine("PASS complete release package: actual ZIP accepted by the production updater with SHA256 and every archive path checked.");
        }
        finally {if(Directory.Exists(root))Directory.Delete(root,true);}
    }
    public static async Task RunAsync()
    {
        var checks = 0;
        void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); checks++; }
        var tag = "v0.4.1";
        var baseUrl = $"https://github.com/{ReleaseUpdates.Repository}/releases/download/{tag}/";
        string Manifest(string version = "v0.4.1", bool preview = false, bool draft = false, string? assetBase = null, bool checksum = true) =>
            JsonSerializer.Serialize(new { tag_name = version, prerelease = preview, draft, assets = new[] {
                new {name=ReleaseUpdates.PackageName,browser_download_url=(assetBase??baseUrl)+ReleaseUpdates.PackageName},
                new {name=checksum?ReleaseUpdates.PackageName+".sha256":"unrelated",browser_download_url=(assetBase??baseUrl)+ReleaseUpdates.PackageName+".sha256"} } });
        static HttpResponseMessage Text(string data) => new(HttpStatusCode.OK) { Content = new StringContent(data) };
        async Task<AvailableRelease?> Probe(string body, Version? current = null)
        {
            using var http = new HttpClient(new Handler(request => {
                Check(request.RequestUri!.Host == "api.github.com", "Checks must use the fixed GitHub API host.");
                Check(request.Method == HttpMethod.Get && request.Content is null && request.Headers.Authorization is null,
                    "Checking must be an anonymous GET with no local data.");
                return Text(body);
            }));
            return await new ReleaseUpdates(http).CheckAsync(current ?? new Version(0, 4, 0, 0));
        }
        var found = await Probe(Manifest());
        Check(found is { Version.Major: 0, Version.Minor: 4, Version.Build: 1 }, "A newer release was not found.");
        Check(await Probe(Manifest(), new Version(0, 4, 1, 0)) is null, "Equal versions must not be offered again.");
        Check(await Probe(Manifest(), new Version(0, 5, 0)) is null, "Older versions must not downgrade the installation.");
        Check(await Probe(Manifest(preview: true)) is null && await Probe(Manifest(draft: true)) is null, "Drafts and previews must be ignored.");
        using (var missing = new HttpClient(new Handler(_ => new(HttpStatusCode.NotFound))))
            Check(await new ReleaseUpdates(missing).CheckAsync(new Version(0, 4, 0)) is null, "Empty release channels must be handled.");
        async Task RejectManifest(string body)
        { try { await Probe(body); throw new Exception("Unsafe manifest accepted."); } catch (InvalidDataException) { checks++; } }
        await RejectManifest(Manifest(assetBase: "https://example.org/"));
        await RejectManifest(Manifest(checksum: false));
        await RejectManifest(Manifest(version: "v0.4.1/../invalid"));
        using (var denied = new HttpClient(new Handler(_ => new(HttpStatusCode.Forbidden))))
        {
            try { await new ReleaseUpdates(denied).CheckAsync(new Version(0, 4, 0)); throw new Exception("API failure was hidden."); }
            catch (HttpRequestException) { checks++; }
        }
        var fallbackRequests = 0;
        using (var limited = new HttpClient(new Handler(request => {
            fallbackRequests++;
            if (request.RequestUri!.Host == "api.github.com") return new(HttpStatusCode.Forbidden);
            if (request.RequestUri.AbsoluteUri.EndsWith("/releases/latest", StringComparison.Ordinal))
                return new(HttpStatusCode.OK) { RequestMessage = new(HttpMethod.Get, $"https://github.com/{ReleaseUpdates.Repository}/releases/tag/{tag}"), Content = new StringContent("") };
            return Text(new string('a', 64) + "  " + ReleaseUpdates.PackageName);
        })))
        {
            Check((await new ReleaseUpdates(limited).CheckAsync(new Version(0, 4, 0)))?.Tag == tag && fallbackRequests >= 2,
                "Anonymously rate-limited API checks must use the fixed GitHub public latest-release redirect.");
        }
        foreach (var unsafePage in new[] { "https://example.org/releases/tag/v0.4.1", $"https://github.com/{ReleaseUpdates.Repository}/releases/tag/v0.4.1-preview" })
        {
            using var unsafeFallback = new HttpClient(new Handler(request => request.RequestUri!.Host == "api.github.com"
                ? new(HttpStatusCode.TooManyRequests)
                : new(HttpStatusCode.OK) { RequestMessage = new(HttpMethod.Get, unsafePage), Content = new StringContent("") }));
            try { await new ReleaseUpdates(unsafeFallback).CheckAsync(new Version(0, 4, 0)); throw new Exception("Unsafe fallback redirect accepted."); }
            catch (InvalidDataException) { checks++; }
        }

        static byte[] Zip(params string[] extra)
        {
            using var output = new MemoryStream();
            using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true))
                foreach (var path in new[] { "GwentVision/GwentVision.exe", "使用说明.md", "GwentVision/defaults/cache/deck-library.json" }.Concat(extra))
                { using var text = new StreamWriter(archive.CreateEntry(path).Open()); text.Write("isolated fixture"); }
            return output.ToArray();
        }
        var root = Path.Combine(Path.GetTempPath(), "gwent-release-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            async Task<string> Download(byte[] archive, string? hash = null, CancellationToken token = default)
            {
                using var http = new HttpClient(new Handler(request => request.RequestUri!.AbsoluteUri.EndsWith(".sha256", StringComparison.Ordinal)
                    ? Text((hash ?? Convert.ToHexString(SHA256.HashData(archive))) + "  " + ReleaseUpdates.PackageName + "\n")
                    : new(HttpStatusCode.OK) { Content = new ByteArrayContent(archive) }));
                return await new ReleaseUpdates(http).DownloadAsync(found!, root, cancellation: token);
            }
            var bytes = Zip("GwentVision/defaults/cache/observed-art/201579/frame-000610133-shinmiri-phoenix-return.jpg"); var file = await Download(bytes);
            Check(File.ReadAllBytes(file).SequenceEqual(bytes), "Verified package bytes were not saved correctly.");
            var originalHash = SHA256.HashData(File.ReadAllBytes(file));
            async Task RejectDownload(byte[] content, string? hash = null)
            {
                try { await Download(content, hash); throw new Exception("Unsafe archive accepted."); }
                catch (InvalidDataException) { checks++; }
                Check(!Directory.GetFiles(root, "*.part", SearchOption.AllDirectories).Any(), "Failed downloads left incomplete files.");
                Check(originalHash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(file))), "Failed downloads replaced a previously verified package.");
            }
            await RejectDownload(bytes, new string('0', 64));
            await RejectDownload(Zip("../escape.txt"));
            await RejectDownload(Zip("GwentVision/cache/settings.json"));
            await RejectDownload(Zip("GwentVision/match-data/private.gvm"));
            await RejectDownload(Zip("GwentVision/sessions/capture.jpg"));
            await RejectDownload(Zip("GwentVision/defaults/cache/custom-data.json"));
            await RejectDownload(Encoding.UTF8.GetBytes("not a ZIP"));
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            try { await Download(bytes, token: cancel.Token); throw new Exception("Cancellation was ignored."); }
            catch (OperationCanceledException) { checks++; }
            using var midCancel = new CancellationTokenSource();
            using (var midHttp = new HttpClient(new Handler(request => request.RequestUri!.AbsoluteUri.EndsWith(".sha256", StringComparison.Ordinal)
                ? Text(Convert.ToHexString(SHA256.HashData(bytes)) + "  " + ReleaseUpdates.PackageName)
                : new(HttpStatusCode.OK) { Content = new StreamContent(new CancelDuringRead(bytes, midCancel)) })))
            {
                try { await new ReleaseUpdates(midHttp).DownloadAsync(found!, root, cancellation: midCancel.Token); throw new Exception("Mid-transfer cancellation was ignored."); }
                catch (OperationCanceledException) { checks++; }
            }
            Check(!Directory.GetFiles(root, "*.part", SearchOption.AllDirectories).Any(), "Cancelled download left partial files.");
            Check(originalHash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(file))), "Mid-transfer cancellation replaced the verified package.");
            Check(!Directory.GetFiles(root, "*.gvm", SearchOption.AllDirectories).Any(), "Download wrote match data.");
        }
        finally { Directory.Delete(root, true); }
        Console.WriteLine($"PASS release update channel: {checks} checks, anonymous GET, version ordering, trusted assets, SHA256, safe ZIP, failure and cancellation preservation.");
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation) { cancellation.ThrowIfCancellationRequested(); return Task.FromResult(respond(request)); } }
    private sealed class CancelDuringRead(byte[] data, CancellationTokenSource cancel) : MemoryStream(data)
    {
        private int _reads;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellation = default)
        { if (++_reads == 2) cancel.Cancel(); cancellation.ThrowIfCancellationRequested(); return base.ReadAsync(buffer[..Math.Min(buffer.Length, 32)], cancellation); }
    }
}
