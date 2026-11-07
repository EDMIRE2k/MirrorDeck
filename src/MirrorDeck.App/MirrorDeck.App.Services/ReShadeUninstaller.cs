using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace MirrorDeck.App.Services;

public static class ReShadeUninstaller
{
    // These are the only proxy names loaded by MirrorDeck's preview bootstrapper.
    private static readonly string[] ProxyNames = { "dxgi.dll", "d3d11.dll", "d3d10.dll" };

    public static int Uninstall(string previewHostDirectory)
    {
        string directory = Path.GetFullPath(previewHostDirectory);
        var runtimes = new List<string>();
        foreach (string name in ProxyNames)
        {
            string path = Path.Combine(directory, name);
            if (!File.Exists(path))
                continue;

            var version = FileVersionInfo.GetVersionInfo(path);
            bool isReShade = string.Equals(version.ProductName, "ReShade", StringComparison.OrdinalIgnoreCase)
                || string.Equals(version.OriginalFilename, "ReShade64.dll", StringComparison.OrdinalIgnoreCase);
            if (isReShade)
                runtimes.Add(path);
            else if (name == "dxgi.dll")
                throw new InvalidOperationException("PreviewHost's dxgi.dll is not identifiable as ReShade. It was left untouched.");
        }

        if (runtimes.Count == 0)
            return 0;

        // Check every runtime before moving any, so a locked file cannot cause a partial uninstall.
        var locks = new List<FileStream>();
        try
        {
            foreach (string path in runtimes)
                locks.Add(new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None));
        }
        finally
        {
            foreach (var handle in locks)
                handle.Dispose();
        }

        // Keep a recovery copy outside the loader's search path. Never touch configuration or add-ons.
        string backup = Path.Combine(directory, "ReShade-runtime-backups",
            DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(backup);
        var moved = new List<string>();
        try
        {
            foreach (string path in runtimes)
            {
                File.Move(path, Path.Combine(backup, Path.GetFileName(path)));
                moved.Add(path);
            }
        }
        catch (Exception error)
        {
            var errors = new List<Exception> { error };
            foreach (string path in moved.AsEnumerable().Reverse())
            {
                try { File.Move(Path.Combine(backup, Path.GetFileName(path)), path); }
                catch (Exception rollbackError) { errors.Add(rollbackError); }
            }
            throw new AggregateException("ReShade removal could not finish. Recovery files are in " + backup, errors);
        }
        return moved.Count;
    }
}
