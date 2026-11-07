using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MirrorDeck.Core.Models;
using MirrorDeck.Core.Updates;

string root = Path.Combine(Path.GetTempPath(), "MirrorDeck-update-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
int checks = 0;
void Check(bool yes, string name) { if (!yes) throw new Exception(name); checks++; Console.WriteLine("PASS: " + name); }
void Reject(Action action, string name) { try { action(); } catch (InvalidDataException) { Check(true, name); return; } throw new Exception("Accepted " + name); }
string Hash(string file) { using var s = File.OpenRead(file); return Convert.ToHexString(SHA256.HashData(s)); }
string Metadata(string digest, string url = "https://github.com/EDMIRE2k/MirrorDeck/releases/download/v1.0.36/MirrorDeck-1.0.36-win-x64.zip", bool prerelease = false) =>
    JsonSerializer.Serialize(new { draft = false, prerelease, tag_name = "v1.0.36", name = "MirrorDeck", body = "Notes", assets = new[] { new { name = "MirrorDeck-1.0.36-win-x64.zip", browser_download_url = url, size = 3, digest } } });
var v = GitHubUpdates.ParseVersion("v1.0.36");
Check(GitHubUpdates.ParseVersion("1.0.10") > GitHubUpdates.ParseVersion("1.0.9"), "versions compare numerically");
Reject(() => GitHubUpdates.ParseVersion("v1.0.36-beta"), "prerelease tags rejected");
string digest = Convert.ToHexString(SHA256.HashData(new byte[] { 1, 2, 3 }));
var release = GitHubUpdates.ParseRelease(Metadata("sha256:" + digest));
Check(release.Version == v && release.Sha256 == digest, "release version and GitHub digest parsed");
Reject(() => GitHubUpdates.ParseRelease(Metadata("")), "missing digest blocked");
Reject(() => GitHubUpdates.ParseRelease(Metadata("sha256:" + digest, prerelease: true)), "prereleases blocked");
foreach (string url in new[] { "https://github.com.evil.test/x", "http://github.com/EDMIRE2k/MirrorDeck/releases/download/x", "https://github.com/other/repo/releases/download/x", "https://evil.test/a", "https://github.com:444/a" })
    Reject(() => GitHubUpdates.ValidateDownloadUri(new Uri(url), true), "unexpected host/path blocked: " + url);
using (var client = new GitHubUpdates(new Mock(_ => new(HttpStatusCode.NotFound))))
    Check(await client.CheckAsync(default) == null, "no stable release is a normal state");
using (var client = new GitHubUpdates(new Mock(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 1, 2, 3 }) })))
{
    string path = Path.Combine(root, "download.zip");
    await client.DownloadAsync(release, path, null, default);
    Check(Hash(path) == digest && !File.Exists(path + ".partial"), "download verified before promotion");
    try { await client.DownloadAsync(release with { Sha256 = new string('0', 64) }, path + "-bad", null, default); throw new Exception("bad digest accepted"); }
    catch (InvalidDataException) { Check(!File.Exists(path + "-bad.partial") && !File.Exists(path + "-bad"), "bad hash leaves no usable package"); }
}
using (var client = new GitHubUpdates(new Mock(_ => { var response = new HttpResponseMessage(HttpStatusCode.Redirect); response.Headers.Location = new Uri("https://evil.test/file"); return response; })))
{
    try { await client.DownloadAsync(release, Path.Combine(root, "redirect.zip"), null, default); throw new Exception("unsafe redirect accepted"); }
    catch (InvalidDataException) { Check(true, "foreign download redirect blocked"); }
}
foreach (string path in new[] { "../outside", "/absolute", "C:/outside", "foo:stream", "CON.txt", "x/../y", "a//b", "trailing. ", "NUL" })
    Reject(() => UpdatePackage.SafeRelative(path), "unsafe zip path rejected: " + path);
string MakeZip(string name, params (string path, string value)[] additions)
{
    string path = Path.Combine(root, name + ".zip");
    using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
    foreach (var entry in new[] {
        ("MirrorDeck.release.json", "{\"schema\":1,\"version\":\"1.0.36\",\"architecture\":\"win-x64\"}"),
        ("MirrorDeck.App.exe", "new exe"), ("MirrorDeck.App.dll", "new dll"), ("MirrorDeck.App.runtimeconfig.json", "{}"),
        ("MirrorDeck.Core.dll", "new core"), ("PreviewHost/MirrorDeck.PreviewHost.exe", "new preview"),
        ("MirrorDeck.portable", ""), ("MirrorDeck.settings.json", "default"), ("PreviewHost/ReShade.ini", "default"),
        ("PreviewHost/sample.addon64", "new addon"), ("configs/mine.json", "default") }.Concat(additions))
    { using var writer = new StreamWriter(archive.CreateEntry("MirrorDeck/" + entry.Item1).Open()); writer.Write(entry.Item2); }
    return path;
}
string zip = MakeZip("valid"), payload = Path.Combine(root, "payload");
UpdatePackage.ExtractVerified(zip, Hash(zip), v, payload);
Check(File.Exists(Path.Combine(payload, "MirrorDeck.App.exe")), "verified archive extracted");
Reject(() => UpdatePackage.ExtractVerified(zip, new string('0', 64), v, Path.Combine(root, "bad-hash")), "changed archive blocked");
Reject(() => UpdatePackage.ExtractVerified(zip, Hash(zip), new Version(1, 0, 37, 0), Path.Combine(root, "wrong-version")), "manifest version mismatch blocked");
foreach (var entry in new[] { ("../outside", "x"), ("MIRRORDECK.APP.EXE", "x") })
{
    string bad = MakeZip("bad-" + Guid.NewGuid().ToString("N"), entry);
    Reject(() => UpdatePackage.ExtractVerified(bad, Hash(bad), v, Path.Combine(root, Guid.NewGuid().ToString("N"))), "traversal or case collision blocked");
}
foreach (bool portable in new[] { false, true })
{
    string app = Path.Combine(root, portable ? "portable-app" : "installed-app");
    foreach (string name in new[] { "MirrorDeck.App.exe", "MirrorDeck.Core.dll", "MirrorDeck.settings.json", "PreviewHost/ReShade.ini", "PreviewHost/sample.addon64", "PreviewHost/dxgi.dll", "configs/mine.json", "private.txt" })
    { string path = Path.Combine(app, name); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, "original"); }
    if (portable) File.WriteAllText(Path.Combine(app, "MirrorDeck.portable"), "");
    string backup = UpdatePackage.Install(payload, app);
    Check(File.ReadAllText(Path.Combine(app, "MirrorDeck.App.exe")) == "new exe", "owned application updated");
    Check(File.ReadAllText(Path.Combine(backup, "MirrorDeck.App.exe")) == "original", "rollback preserves original executable");
    foreach (string name in new[] { "MirrorDeck.settings.json", "PreviewHost/ReShade.ini", "PreviewHost/sample.addon64", "PreviewHost/dxgi.dll", "configs/mine.json", "private.txt" })
        Check(File.ReadAllText(Path.Combine(app, name)) == "original", "retained " + name);
    Check(File.Exists(Path.Combine(app, "MirrorDeck.portable")) == portable, "storage mode retained");
}
var defaults = new MirrorDeckSettings();
Check(defaults.OpenLastDeviceOnStartup && defaults.CheckForUpdatesOnStartup && defaults.Scaling.Neural.Passes == 1, "fresh defaults auto-start and keep repeated NR off");
foreach (var screen in new[] { (1366, 768), (1920, 1080), (3840, 2160) })
{
    var size = PreviewWindowSizing.Fit(3840, 2160, screen.Item1, screen.Item2, true);
    Check(size.Width <= 1280 && size.Height <= 720 && size.Width < screen.Item1 && size.Height < screen.Item2, "launch fits monitor " + screen);
}
if (args.Contains("--live"))
{
    using var client = new GitHubUpdates();
    var live = await client.CheckAsync(default);
    Console.WriteLine("Live GitHub check: " + (live?.Version.ToString(3) ?? "No stable release published"));
}
Console.WriteLine($"PASS: {checks} update/default checks. Fixtures: {root}");

sealed class Mock(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(respond(request));
}
