using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace MirrorDeck.Core.Updates;

public sealed record UpdateRelease(Version Version, string Name, string Notes, Uri Download, string Sha256, long Size);
public sealed record UpdateProgress(string Status, double Percent, string Connection);

public sealed class GitHubUpdates : IDisposable
{
    public const string Repository = "https://github.com/EDMIRE2k/MirrorDeck";
    public const string Endpoint = "https://api.github.com/repos/EDMIRE2k/MirrorDeck/releases/latest";
    public const long MaxDownload = 2L * 1024 * 1024 * 1024;
    private readonly HttpClient _http;
    public GitHubUpdates(HttpMessageHandler? handler = null)
    {
        _http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false });
        _http.Timeout = TimeSpan.FromSeconds(30);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("MirrorDeck-Updater/1.0");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    public static Version ParseVersion(string tag)
    {
        if (!Regex.IsMatch(tag, "^v?[0-9]{1,5}\\.[0-9]{1,5}\\.[0-9]{1,5}$")) throw new InvalidDataException("Release tag must be vMAJOR.MINOR.PATCH.");
        var v = Version.Parse(tag.TrimStart('v'));
        return new Version(v.Major, v.Minor, v.Build, 0);
    }

    public static UpdateRelease ParseRelease(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean()) throw new InvalidDataException("Only published stable releases are supported.");
        var version = ParseVersion(root.GetProperty("tag_name").GetString()!);
        string filename = $"MirrorDeck-{version.ToString(3)}-win-x64.zip";
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            if (asset.GetProperty("name").GetString() != filename) continue;
            var url = new Uri(asset.GetProperty("browser_download_url").GetString()!);
            ValidateDownloadUri(url, false);
            if (!url.AbsolutePath.EndsWith("/" + filename, StringComparison.Ordinal)) throw new InvalidDataException("Unexpected release filename.");
            long size = asset.GetProperty("size").GetInt64();
            if (size <= 0 || size > MaxDownload) throw new InvalidDataException("Release exceeds the supported download size.");
            string? digest = asset.TryGetProperty("digest", out var hash) ? hash.GetString() : null;
            if (digest == null || !Regex.IsMatch(digest, "^sha256:[a-fA-F0-9]{64}$")) throw new InvalidDataException("GitHub has not provided a SHA-256 digest for this release. Installation is blocked.");
            return new(version, root.GetProperty("name").GetString() ?? version.ToString(3),
                root.GetProperty("body").GetString() ?? "", url, digest.Substring(7), size);
        }
        throw new InvalidDataException("This release has no " + filename + " asset.");
    }

    public static void ValidateDownloadUri(Uri uri, bool redirected)
    {
        if (uri.Scheme != "https" || !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0)
            throw new InvalidDataException("Updates require an HTTPS GitHub URL.");
        if (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) &&
            uri.AbsolutePath.StartsWith("/EDMIRE2k/MirrorDeck/releases/download/", StringComparison.Ordinal)) return;
        if (redirected && uri.Host.Equals("release-assets.githubusercontent.com", StringComparison.OrdinalIgnoreCase)) return;
        throw new InvalidDataException("Blocked update host/path: " + uri.GetLeftPart(UriPartial.Path));
    }

    public async Task<UpdateRelease?> CheckAsync(CancellationToken token)
    {
        using var response = await _http.GetAsync(Endpoint, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (response.StatusCode == HttpStatusCode.Forbidden || (int)response.StatusCode == 429)
            throw new HttpRequestException("GitHub rate limit reached. Try again later.");
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var bytes = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
        {
            if (bytes.Length + count > 2 * 1024 * 1024) throw new InvalidDataException("Release metadata is too large.");
            bytes.Write(buffer, 0, count);
        }
        return ParseRelease(System.Text.Encoding.UTF8.GetString(bytes.ToArray()));
    }

    public async Task DownloadAsync(UpdateRelease release, string destination, IProgress<UpdateProgress>? progress, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(30));
        token = deadline.Token;
        var url = release.Download;
        string temporary = destination + ".partial";
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        try
        {
            for (int redirect = 0; redirect < 6; redirect++)
            {
                ValidateDownloadUri(url, redirect > 0);
                progress?.Report(new("Connecting", 0, url.GetLeftPart(UriPartial.Path)));
                using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
                {
                    var location = response.Headers.Location ?? throw new InvalidDataException("Empty redirect.");
                    url = location.IsAbsoluteUri ? location : new Uri(url, location);
                    continue;
                }
                response.EnsureSuccessStatusCode();
                await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true))
                {
                    var buffer = new byte[131072];
                    long downloaded = 0;
                    int count;
                    var reported = DateTime.UtcNow;
                    while ((count = await input.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
                    {
                        downloaded += count;
                        if (downloaded > release.Size || downloaded > MaxDownload) throw new InvalidDataException("Download is larger than the release asset.");
                        hash.AppendData(buffer, 0, count);
                        await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                        if ((DateTime.UtcNow - reported).TotalMilliseconds > 150)
                        {
                            progress?.Report(new($"Downloading {downloaded / 1048576d:F1} / {release.Size / 1048576d:F1} MB", downloaded * 100d / release.Size, url.GetLeftPart(UriPartial.Path)));
                            reported = DateTime.UtcNow;
                        }
                    }
                    if (downloaded != release.Size || !Convert.ToHexString(hash.GetHashAndReset()).Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Download integrity check failed. Nothing was installed.");
                }
                File.Move(temporary, destination);
                progress?.Report(new("SHA-256 verified. Ready to install.", 100, url.GetLeftPart(UriPartial.Path)));
                return;
            }
            throw new InvalidDataException("Too many download redirects.");
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public void Dispose() => _http.Dispose();
}
