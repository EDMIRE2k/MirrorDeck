using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json;
using MirrorDeck.Core.Models;
using MirrorDeck.PreviewHost;

internal static class DlssStabilityTests
{
    internal static void Run(string[] args)
    {
        bool baseline = args.Contains("--baseline");
        string output = Path.Combine(AppContext.BaseDirectory, "dlss-stability", baseline ? "before" : "after");
        Directory.CreateDirectory(output);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        using var window = new Form { Text = "DLSS temporal stability verification", ClientSize = new Size(1280, 720) };
        Task? worker = null;
        Exception? failure = null;
        window.Shown += (_, _) =>
        {
            var handle = window.Handle;
            worker = Task.Run(() =>
            {
                try
                {
                    using var renderer = new D3D12PreviewRenderer(handle, new Size(1280, 720), Console.WriteLine);
                    var settings = new PreviewRenderSettings { DisableProcessing = false, VSyncEnabled = false,
                        Scaling = new() { Filter = ScalingFilter.Dlss45, Sharpness = 0 }, Enhancement = new() { CleanUpscalingEnabled = false } };
                    var results = new List<object>();
                    foreach (var filter in new[] { ScalingFilter.Bilinear, ScalingFilter.Dlss45 })
                    foreach (bool moving in new[] { false, true })
                    {
                        settings.Scaling.Filter = filter;
                        byte[] original = Pattern();
                        byte[] frame = new byte[original.Length];
                        byte[]? previous = null;
                        var differences = new List<double>();
                        var motionDifferences = new List<double>();
                        for (int i = 0; i < 128; i++)
                        {
                            Buffer.BlockCopy(original, 0, frame, 0, frame.Length);
                            if (moving)
                            {
                                int start = 360 + i % 200;
                                for (int y = 50; y < 300; y++) for (int x = start; x < start + 32; x++)
                                {
                                    int p = (y * 640 + x) * 4;
                                    frame[p] = 20; frame[p + 1] = 220; frame[p + 2] = 80;
                                }
                            }
                            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                            while (!renderer.WaitForPresentation(timeout.Token)) { }
                            renderer.Render(frame, 640, 360, 2560, false, new Size(1280, 720), settings);
                            if (i < 64) continue;
                            using var image = NativeDlssTests.ReadBack(renderer);
                            var bytes = Pixels(image);
                            if (previous != null)
                            {
                                long delta = 0;
                                for (int y = 32; y < 688; y++) for (int x = 32; x < 600; x++) for (int c = 0; c < 3; c++)
                                {
                                    int p = (y * 1280 + x) * 4 + c;
                                    delta += Math.Abs(bytes[p] - previous[p]);
                                }
                                differences.Add(delta / (656.0 * 568 * 3));
                                long motionDelta = 0;
                                for (int y = 32; y < 688; y++) for (int x = 720; x < 1248; x++) for (int c = 0; c < 3; c++)
                                {
                                    int p = (y * 1280 + x) * 4 + c;
                                    motionDelta += Math.Abs(bytes[p] - previous[p]);
                                }
                                motionDifferences.Add(motionDelta / (656.0 * 528 * 3));
                            }
                            previous = bytes;
                            if (i < 80) image.Save(Path.Combine(output, $"{filter}-{moving}-{i:000}.png"));
                        }
                        var result = new { Filter = filter.ToString(), MovingBackground = moving, StaticMenuMeanFrameDelta = differences.Average(), MaxFrameDelta = differences.Max(), MotionMeanFrameDelta = motionDifferences.Average() };
                        Console.WriteLine(JsonSerializer.Serialize(result));
                        results.Add(result);
                        if (filter == ScalingFilter.Dlss45 && !renderer.BackendName.Contains("native DLSS")) throw new Exception("Stability test unexpectedly fell back from DLSS");
                        if (moving && motionDifferences.Average() < .05) throw new Exception("Stabilization froze moving content");
                        if (!baseline && filter == ScalingFilter.Dlss45 && (differences.Average() > .1 || differences.Max() > .25))
                            throw new Exception("Stationary menu is not temporally stable: " + JsonSerializer.Serialize(result));
                    }
                    if (!baseline)
                    {
                        // A one-code-value fade must not be mistaken for an unchanged region.
                        var fade = new byte[640 * 360 * 4];
                        Color first = default, last = default;
                        for (int i = 0; i < 96; i++)
                        {
                            for (int p = 0; p < fade.Length; p += 4) { fade[p] = fade[p+1] = fade[p+2] = (byte)(40 + i); fade[p+3] = 255; }
                            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                            while (!renderer.WaitForPresentation(timeout.Token)) { }
                            renderer.Render(fade, 640, 360, 2560, false, new Size(1280, 720), settings);
                            if (i == 16 || i == 95)
                            {
                                using var image = NativeDlssTests.ReadBack(renderer);
                                if (i == 16) first = image.GetPixel(200, 200); else last = image.GetPixel(200, 200);
                            }
                        }
                        if (last.R - first.R < 60) throw new Exception("Stabilization froze a slow fade");
                        Console.WriteLine($"PASS: one-code-value fade remains live ({first.R} -> {last.R})");
                    }
                    File.WriteAllText(Path.Combine(output, "metrics.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
                }
                catch (Exception ex) { failure = ex; }
                finally { window.BeginInvoke(() => window.Close()); }
            });
        };
        Application.Run(window);
        worker!.GetAwaiter().GetResult();
        if (failure != null) throw failure;
    }

    private static byte[] Pattern()
    {
        using var bitmap = new Bitmap(640, 360, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.FromArgb(32, 41, 28));
            using var font = new Font("Segoe UI", 14);
            for (int y = 20; y < 320; y += 40)
            {
                graphics.DrawString("MENU  1080p / 1440p", font, Brushes.White, 20, y);
                graphics.DrawRectangle(Pens.Gold, 22, y + 28, 260, 4);
            }
            for (int x = 330; x < 630; x += 7) graphics.DrawLine(Pens.Teal, x, 0, x - 60, 359);
        }
        return Pixels(bitmap);
    }

    private static byte[] Pixels(Bitmap bitmap)
    {
        var bits = bitmap.LockBits(new Rectangle(Point.Empty, bitmap.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var bytes = new byte[bitmap.Width * bitmap.Height * 4];
            for (int y = 0; y < bitmap.Height; y++) Marshal.Copy(bits.Scan0 + y * bits.Stride, bytes, y * bitmap.Width * 4, bitmap.Width * 4);
            return bytes;
        }
        finally { bitmap.UnlockBits(bits); }
    }
}
