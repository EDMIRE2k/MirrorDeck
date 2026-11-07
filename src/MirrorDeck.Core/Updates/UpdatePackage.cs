using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MirrorDeck.Core.Updates;

public static class UpdatePackage
{
    public static bool Preserve(string relative)
    {
        var p = relative.Replace('\\', '/');
        string name = Path.GetFileName(p);
        return name.Equals("MirrorDeck.settings.json", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("preview-render-settings.json", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("MirrorDeck.portable", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".ini", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".addon64", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".log", StringComparison.OrdinalIgnoreCase) ||
            p.Split('/').Any(part => part.Equals("configs", StringComparison.OrdinalIgnoreCase) ||
                part.Equals("presets", StringComparison.OrdinalIgnoreCase) || part.Equals("reshade-shaders", StringComparison.OrdinalIgnoreCase)) ||
            Regex.IsMatch(name, "^(dxgi|d3d11|d3d10|d3d9|opengl32|ReShade64|ReShade32)\\.dll$", RegexOptions.IgnoreCase);
    }

    public static void RejectLinks(string path)
    {
        for (var directory = new DirectoryInfo(Path.GetFullPath(path)); directory != null; directory = directory.Parent)
            if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Updates cannot traverse a junction or symbolic link: " + directory.FullName);
    }

    public static string SafeRelative(string entry)
    {
        string p = entry.Replace('\\', '/');
        if (p.StartsWith('/') || p.Contains(':') || p.Contains('\0')) throw new InvalidDataException("Unsafe archive path.");
        var parts = p.TrimEnd('/').Split('/');
        if (parts.Any(part => part is "" or "." or ".." || part.EndsWith('.') || part.EndsWith(' ') ||
            part.IndexOfAny(new[] { '<', '>', '"', '|', '?', '*' }) >= 0 ||
            Regex.IsMatch(part, "^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\\.|$)", RegexOptions.IgnoreCase)))
            throw new InvalidDataException("Unsafe archive path: " + entry);
        return string.Join(Path.DirectorySeparatorChar, parts);
    }

    public static void ExtractVerified(string zipPath, string digest, Version version, string destination)
    {
        RejectLinks(Path.GetDirectoryName(zipPath)!);
        using var file = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (!Convert.ToHexString(SHA256.HashData(file)).Equals(digest, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Package SHA-256 changed. Update rejected.");
        file.Position = 0;
        if (Directory.Exists(destination)) throw new IOException("Extraction directory already exists.");
        RejectLinks(Path.GetDirectoryName(destination)!);
        using var zip = new ZipArchive(file, ZipArchiveMode.Read);
        if (zip.Entries.Count > 20000) throw new InvalidDataException("Too many archive entries.");
        string? prefix = null;
        foreach (var e in zip.Entries)
        {
            string p = SafeRelative(e.FullName).Replace('\\', '/');
            if (p == "MirrorDeck.release.json") prefix = "";
            else if (p.EndsWith("/MirrorDeck.release.json", StringComparison.Ordinal) && p.Count(c => c == '/') == 1)
                prefix = p.Substring(0, p.IndexOf('/') + 1);
        }
        if (prefix == null) throw new InvalidDataException("This is not a MirrorDeck update package.");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        Directory.CreateDirectory(destination);
        foreach (var entry in zip.Entries)
        {
            string normalized = SafeRelative(entry.FullName).Replace('\\', '/');
            if (((entry.ExternalAttributes >> 16) & 0xf000) == 0xa000 || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Archive links are not permitted.");
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) continue;
            if (!normalized.StartsWith(prefix, StringComparison.Ordinal)) throw new InvalidDataException("Files outside the package root.");
            string relative = SafeRelative(normalized.Substring(prefix.Length));
            if (!seen.Add(relative)) throw new InvalidDataException("Duplicate archive path.");
            total += entry.Length;
            if (entry.Length > 2L * 1024 * 1024 * 1024 || total > 8L * 1024 * 1024 * 1024) throw new InvalidDataException("Archive is too large.");
            string target = Path.GetFullPath(Path.Combine(destination, relative));
            if (!target.StartsWith(Path.GetFullPath(destination) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Archive escaped its destination.");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target);
        }
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(destination, "MirrorDeck.release.json")));
        if (manifest.RootElement.GetProperty("schema").GetInt32() != 1 || manifest.RootElement.GetProperty("architecture").GetString() != "win-x64" ||
            GitHubUpdates.ParseVersion(manifest.RootElement.GetProperty("version").GetString()!) != version)
            throw new InvalidDataException("Package version or architecture mismatch.");
        foreach (string required in new[] { "MirrorDeck.App.exe", "MirrorDeck.App.dll", "MirrorDeck.App.runtimeconfig.json", "MirrorDeck.Core.dll", "PreviewHost/MirrorDeck.PreviewHost.exe" })
            if (!File.Exists(Path.Combine(destination, required))) throw new InvalidDataException("Incomplete application package: " + required);
    }

    private static void CopyTree(string from, string to, bool overlay)
    {
        RejectLinks(from); RejectLinks(to);
        Directory.CreateDirectory(to);
        void Visit(string directory)
        {
            foreach (var item in new DirectoryInfo(directory).EnumerateFileSystemInfos())
            {
                if ((item.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Update cannot copy linked files: " + item.FullName);
                string relative = Path.GetRelativePath(from, item.FullName);
                string target = Path.Combine(to, relative);
                if (item is DirectoryInfo) { Directory.CreateDirectory(target); Visit(item.FullName); }
                else if (!overlay || !Preserve(relative) || !File.Exists(target))
                {
                    // Installing an updater must never enable portable storage in
                    // a previously installed application.
                    if (overlay && relative.Equals("MirrorDeck.portable", StringComparison.OrdinalIgnoreCase)) continue;
                    File.Copy(item.FullName, target, true);
                }
            }
        }
        Visit(from);
    }

    public static string Install(string package, string destination)
    {
        destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination));
        RejectLinks(destination);
        if (destination == Path.GetPathRoot(destination) || !File.Exists(Path.Combine(destination, "MirrorDeck.App.exe")) || !File.Exists(Path.Combine(destination, "MirrorDeck.Core.dll")))
            throw new IOException("Update destination is not an existing MirrorDeck application.");
        string suffix = Guid.NewGuid().ToString("N");
        string stage = destination + ".update-" + suffix;
        string backup = destination + ".backup-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + suffix.Substring(0, 6);
        CopyTree(destination, stage, false);
        CopyTree(package, stage, true);
        Directory.Move(destination, backup);
        try { Directory.Move(stage, destination); }
        catch { Directory.Move(backup, destination); throw; }
        return backup;
    }
}
