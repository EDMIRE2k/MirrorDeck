using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using MirrorDeck.Core.Updates;

internal static class Program
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(nint hwnd, string text, string caption, uint type);

    private static int Main(string[] args)
    {
        if (args.Length != 6) return 2;
        string destination = Path.GetFullPath(args[0]);
        string log = Path.Combine(AppContext.BaseDirectory, "update-result.json");
        bool parentExited = false;
        try
        {
            using var guard = new Mutex(false, "Local\\MirrorDeckUpdate-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(destination.ToUpperInvariant()))));
            try { if (!guard.WaitOne(0)) throw new IOException("Another update is already running."); }
            catch (AbandonedMutexException) { }
            try
            {
                using var parent = Process.GetProcessById(int.Parse(args[4]));
                if (Math.Abs((parent.StartTime.ToUniversalTime() - new DateTime(long.Parse(args[5]), DateTimeKind.Utc)).TotalSeconds) > 1)
                    throw new IOException("Application process identity changed.");
                if (!parent.WaitForExit(90000)) throw new IOException("MirrorDeck did not close. Nothing has been installed.");
            }
            catch (ArgumentException) { }
            parentExited = true;
            var version = GitHubUpdates.ParseVersion(args[3]);
            string extracted = Path.Combine(AppContext.BaseDirectory, "payload");
            UpdatePackage.ExtractVerified(args[1], args[2], version, extracted);
            string backup = UpdatePackage.Install(extracted, destination);
            File.WriteAllText(log, JsonSerializer.Serialize(new { success = true, version = version.ToString(3), backup }));
            Process.Start(new ProcessStartInfo(Path.Combine(destination, "MirrorDeck.App.exe")) { UseShellExecute = true, WorkingDirectory = destination });
            return 0;
        }
        catch (Exception ex)
        {
            File.WriteAllText(log, JsonSerializer.Serialize(new { success = false, error = ex.ToString() }));
            MessageBox(0, "MirrorDeck could not complete the update. Your settings and the previous application have been retained.\n\n" + ex.Message + "\n\nDetails: " + log, "MirrorDeck update", 0x10);
            if (parentExited && File.Exists(Path.Combine(destination, "MirrorDeck.App.exe")))
                Process.Start(new ProcessStartInfo(Path.Combine(destination, "MirrorDeck.App.exe")) { UseShellExecute = true, WorkingDirectory = destination });
            return 1;
        }
    }
}
