using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GwentCompanion.Core.Data;

public sealed record AvailableRelease(Version Version, string Tag, Uri Page, Uri Package, Uri Checksum);

/// <summary>Opt-in public release checks. Never sends local files, edits an installation or stops capture.</summary>
public sealed class ReleaseUpdates(HttpClient http)
{
    public const string Repository = "JackRangooo/GwentChronicle";
    public const string PackageName = "GwentChronicle-Windows-x64.zip";
    public static readonly Uri ReleasesPage = new($"https://github.com/{Repository}/releases/latest");
    private const long MaximumPackageBytes = 1024L * 1024 * 1024;

    public async Task<AvailableRelease?> CheckAsync(Version installed, CancellationToken cancellation = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repository}/releases/latest");
        request.Headers.UserAgent.ParseAdd("GwentVision-zhCN/" + installed);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            return await CheckViaPublicPageAsync(installed, cancellation);
        response.EnsureSuccessStatusCode();
        var data = await ReadLimitedAsync(response.Content, 1024 * 1024, cancellation);
        using var json = JsonDocument.Parse(data);
        var root = json.RootElement;
        if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean()) return null;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Regex.IsMatch(tag, @"^v\d+\.\d+\.\d+$") || !Version.TryParse(tag[1..], out var latest))
            throw new InvalidDataException("发行版本号格式无效。");
        if (latest <= new Version(installed.Major, installed.Minor, Math.Max(0, installed.Build))) return null;
        Uri Asset(string name)
        {
            var assets = root.GetProperty("assets").EnumerateArray().Where(a => a.GetProperty("name").GetString() == name).ToArray();
            if (assets.Length != 1) throw new InvalidDataException("发行包或校验文件尚未准备齐全，请稍后重试。");
            var expected = $"https://github.com/{Repository}/releases/download/{tag}/{name}";
            if (assets[0].GetProperty("browser_download_url").GetString() != expected)
                throw new InvalidDataException("下载地址与本项目发行仓库不一致。");
            return new Uri(expected);
        }
        return new(latest, tag, new Uri($"https://github.com/{Repository}/releases/tag/{tag}"), Asset(PackageName), Asset(PackageName + ".sha256"));
    }

    private async Task<AvailableRelease?> CheckViaPublicPageAsync(Version installed, CancellationToken cancellation)
    {
        // GitHub's anonymous API quota is shared by public IP. The canonical
        // latest-release redirect is a second public endpoint; no token is needed.
        using var request = new HttpRequestMessage(HttpMethod.Get, ReleasesPage);
        request.Headers.UserAgent.ParseAdd("GwentChronicle-update-check");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        var address = response.RequestMessage?.RequestUri;
        var prefix = $"https://github.com/{Repository}/releases/tag/";
        if (address is null || !address.AbsoluteUri.StartsWith(prefix, StringComparison.Ordinal) || address.Query.Length > 0 || address.Fragment.Length > 0)
            throw new InvalidDataException("GitHub 发行页没有返回本项目的正式版本地址。");
        var tag = address.AbsoluteUri[prefix.Length..];
        if (!Regex.IsMatch(tag, @"^v\d+\.\d+\.\d+$") || !Version.TryParse(tag[1..], out var latest))
            throw new InvalidDataException("发行版本号格式无效。");
        if (latest <= new Version(installed.Major, installed.Minor, Math.Max(0, installed.Build))) return null;
        var downloads = $"https://github.com/{Repository}/releases/download/{tag}/";
        var release = new AvailableRelease(latest, tag, address, new(downloads + PackageName), new(downloads + PackageName + ".sha256"));
        using var checksum = await http.GetAsync(release.Checksum, HttpCompletionOption.ResponseHeadersRead, cancellation);
        checksum.EnsureSuccessStatusCode();
        var text = System.Text.Encoding.UTF8.GetString(await ReadLimitedAsync(checksum.Content, 1024, cancellation)).Trim('\uFEFF', ' ', '\r', '\n');
        if (!Regex.IsMatch(text, @"\A[a-fA-F0-9]{64}[ \t]+\*?" + Regex.Escape(PackageName) + @"\z"))
            throw new InvalidDataException("发行版的校验附件尚未准备齐全，请稍后重试。");
        return release;
    }

    public async Task<string> DownloadAsync(AvailableRelease release, string downloadsDirectory,
        IProgress<long>? progress = null, CancellationToken cancellation = default)
    {
        var prefix = $"https://github.com/{Repository}/releases/download/{release.Tag}/";
        if (!Regex.IsMatch(release.Tag, @"^v\d+\.\d+\.\d+$") || release.Package.AbsoluteUri != prefix + PackageName ||
            release.Checksum.AbsoluteUri != prefix + PackageName + ".sha256")
            throw new InvalidDataException("下载来源不可信。");
        using var checksumResponse = await http.GetAsync(release.Checksum, HttpCompletionOption.ResponseHeadersRead, cancellation);
        checksumResponse.EnsureSuccessStatusCode();
        var checksumText = System.Text.Encoding.UTF8.GetString(await ReadLimitedAsync(checksumResponse.Content, 1024, cancellation)).Trim('\uFEFF', ' ', '\r', '\n');
        var match = Regex.Match(checksumText, @"\A([a-fA-F0-9]{64})[ \t]+\*?" + Regex.Escape(PackageName) + @"\z");
        if (!match.Success) throw new InvalidDataException("SHA256 校验文件格式无效。");
        var expected = Convert.FromHexString(match.Groups[1].Value);
        var directory = Path.Combine(Path.GetFullPath(downloadsDirectory), release.Tag);
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, PackageName);
        var partial = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".part");
        try
        {
            using (var response = await http.GetAsync(release.Package, HttpCompletionOption.ResponseHeadersRead, cancellation))
            {
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength > MaximumPackageBytes) throw new InvalidDataException("更新包超出大小限制。");
                await using var input = await response.Content.ReadAsStreamAsync(cancellation);
                await using var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[81920]; long total = 0; int read;
                while ((read = await input.ReadAsync(buffer, cancellation)) > 0)
                {
                    total += read;
                    if (total > MaximumPackageBytes) throw new InvalidDataException("更新包超出大小限制。");
                    hash.AppendData(buffer, 0, read); await output.WriteAsync(buffer.AsMemory(0, read), cancellation); progress?.Report(total);
                }
                if (!CryptographicOperations.FixedTimeEquals(expected, hash.GetHashAndReset()))
                    throw new InvalidDataException("更新包 SHA256 校验失败，请重新下载。");
            }
            using (var archive = ZipFile.OpenRead(partial))
            {
                if (!archive.Entries.Any(e => e.FullName == "GwentVision/GwentVision.exe") ||
                    !archive.Entries.Any(e => e.FullName == "使用说明.md"))
                    throw new InvalidDataException("更新包缺少程序或使用说明。");
                foreach (var entry in archive.Entries)
                {
                    var path = entry.FullName.Replace('\\', '/');
                    if (path.StartsWith('/') || path.Split('/').Any(p => p is ".." or "." || p.Contains(':')) ||
                        path.StartsWith("GwentVision/cache/", StringComparison.OrdinalIgnoreCase) ||
                        Regex.IsMatch(path, @"(?i)(^|/)(match-data|sessions|snapshots|diagnostics|pinned-views|deck-scans|updates|opponent-memory|observed-decks|\.git)(/|$)|\.gvm$|(^|/)(settings|installation|workspace-appearance|appearance|match-reviews|local-player-profiles)\.json$"))
                        throw new InvalidDataException("更新包包含不允许覆盖的路径。");
                    if (path.StartsWith("GwentVision/defaults/cache/", StringComparison.Ordinal))
                    {
                        var seed = path["GwentVision/defaults/cache/".Length..];
                        if (seed.Length > 0 && !seed.EndsWith('/') && seed is not ("gwent-one-cards.json" or "create-point-profiles.json" or "deck-library.json" or "data-service.json") &&
                            !Regex.IsMatch(seed, @"^(portraits/[0-9]+|premium-frames/[0-9]+/[0-9]+|observed-art/[0-9]+/[a-zA-Z0-9_-]+)\.(jpg|png)$"))
                            throw new InvalidDataException("更新包的出厂资料包含未允许的文件。");
                    }
                }
            }
            cancellation.ThrowIfCancellationRequested();
            File.Move(partial, destination, true); return destination;
        }
        finally { if (File.Exists(partial)) File.Delete(partial); }
    }

    private static async Task<byte[]> ReadLimitedAsync(HttpContent content, int limit, CancellationToken cancellation)
    {
        if (content.Headers.ContentLength > limit) throw new InvalidDataException("服务器响应超出大小限制。");
        await using var input = await content.ReadAsStreamAsync(cancellation);
        using var output = new MemoryStream(); var buffer = new byte[8192]; int read;
        while ((read = await input.ReadAsync(buffer, cancellation)) > 0)
        { if (output.Length + read > limit) throw new InvalidDataException("服务器响应超出大小限制。"); output.Write(buffer, 0, read); }
        return output.ToArray();
    }
}
