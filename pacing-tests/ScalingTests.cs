using System.Diagnostics;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using MirrorDeck.Core.Models;
using MirrorDeck.PreviewHost;
using MirrorDeck.Settings.Persistence;

internal static class ScalingTests
{
    private static readonly ScalingFilter[] NewFilters = [ScalingFilter.SharpBilinear, ScalingFilter.FsrEasu, ScalingFilter.AnimeLineArt];
    private static readonly string Evidence = Path.Combine(AppContext.BaseDirectory, "scaling-evidence");
    private static int passed;

    private static void Check(bool value, string message)
    {
        if (!value) throw new Exception("FAIL: " + message);
        passed++;
        Console.WriteLine("PASS: " + message);
    }

    internal static void Run(string[] args)
    {
        Directory.CreateDirectory(Evidence);
        Check((int)ScalingFilter.Lanczos == 3 && (int)ScalingFilter.SharpBilinear == 4, "existing serialized filter values unchanged");
        var old = JsonSerializer.Deserialize<ScalingSettings>("{\"Filter\":1}")!;
        Check(old.Filter == ScalingFilter.Bilinear && old.UpscaleStrength == 1, "old configurations retain their filter and get a safe strength default");
        foreach (var filter in NewFilters)
        {
            var settings = new MirrorDeckSettings { Scaling = new() { Filter = filter, UpscaleStrength = .65 } };
            var store = new JsonSettingsStore(Path.Combine(Evidence, filter + ".json"));
            store.SaveAsync(settings).GetAwaiter().GetResult();
            var loaded = store.LoadAsync().GetAwaiter().GetResult();
            var preview = PreviewRenderSettings.From(loaded);
            Check(preview.Scaling.Filter == filter && preview.Scaling.UpscaleStrength == .65, filter + " survives saved config and preview IPC");
        }

        Directory.SetCurrentDirectory(AppContext.BaseDirectory);
        if (args.Contains("--reshade"))
        {
            ReShadeBootstrapper.Initialize(Console.WriteLine);
            Check(ReShadeBootstrapper.LocalProxyLoaded, "ReShade loaded for scaler compatibility test");
        }
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        using var window = new Form { Text = "MirrorDeck upscaler verification", ClientSize = new Size(1280, 720), StartPosition = FormStartPosition.CenterScreen, TopMost = true };
        Exception? failure = null;
        Task? worker = null;
        window.Shown += (_, _) =>
        {
            var handle = window.Handle;
            worker = Task.Run(() =>
            {
                try
                {
                    bool dx11 = args.Contains("--dx11");
                    using IPreviewRenderer renderer = dx11
                        ? new D3D11PreviewRenderer(handle, new Size(1280, 720), Console.WriteLine)
                        : new D3D12PreviewRenderer(handle, new Size(1280, 720), Console.WriteLine);
                    var pixels = Pattern(320, 240);
                    var settings = new PreviewRenderSettings
                    {
                        VSyncEnabled = false, DisableProcessing = false,
                        Scaling = new() { Sharpness = 0 },
                        Enhancement = new() { CleanUpscalingEnabled = false }
                    };
                    var size = new Size(1280, 720);
                    void Draw(byte[] data, int width, int height, bool bottomUp = false)
                    {
                        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                        while (!renderer.WaitForPresentation(timeout.Token)) { }
                        renderer.Render(data, width, height, width * 4, bottomUp, size, settings);
                    }
                    Bitmap Capture(byte[] data, int width, int height, bool bottomUp = false)
                    {
                        for (int i = 0; i < 3; i++) Draw(data, width, height, bottomUp);
                        Thread.Sleep(100);
                        return (Bitmap)window.Invoke(() =>
                        {
                            var image = new Bitmap(1280, 720, PixelFormat.Format32bppArgb);
                            using var g = Graphics.FromImage(image);
                            g.CopyFromScreen(window.PointToScreen(Point.Empty), Point.Empty, image.Size);
                            return image;
                        });
                    }
                    settings.Scaling.Filter = ScalingFilter.Bilinear;
                    using var baseline = Capture(pixels, 320, 240);
                    baseline.Save(Path.Combine(Evidence, "Bilinear.png"));
                    foreach (var filter in NewFilters)
                    {
                        settings.Scaling.Filter = filter;
                        settings.Scaling.UpscaleStrength = 1;
                        using var scaled = Capture(pixels, 320, 240);
                        scaled.Save(Path.Combine(Evidence, filter + (dx11 ? "-DX11" : "") + ".png"));
                        Check(Difference(baseline, scaled) > .08, filter + " visibly changes low-resolution output");
                        Check(scaled.GetPixel(10, 360).R < 5 && scaled.GetPixel(10, 360).G < 5, filter + " preserves aspect-fit black bars");
                        settings.Scaling.UpscaleStrength = 0;
                        using var zero = Capture(pixels, 320, 240);
                        Check(Difference(baseline, zero) < .02, filter + " at zero strength matches bilinear");
                        settings.Scaling.UpscaleStrength = 1;
                        var inverted = new byte[pixels.Length];
                        for (int y = 0; y < 240; y++) Buffer.BlockCopy(pixels, y * 1280, inverted, (239 - y) * 1280, 1280);
                        using var flipped = Capture(inverted, 320, 240, true);
                        Check(Difference(scaled, flipped) < .1, filter + " handles bottom-up capture orientation");
                        settings.DisableProcessing = true;
                        using var disabled = Capture(pixels, 320, 240);
                        Check(Difference(baseline, disabled) < .02, filter + " obeys enhancement bypass");
                        settings.DisableProcessing = false;
                    }
                    foreach (var sourceSize in new[] { new Size(1280, 720), new Size(1600, 900) })
                    {
                        var input = Pattern(sourceSize.Width, sourceSize.Height);
                        settings.Scaling.Filter = ScalingFilter.Bilinear;
                        using var reference = Capture(input, sourceSize.Width, sourceSize.Height);
                        foreach (var filter in NewFilters)
                        {
                            settings.Scaling.Filter = filter;
                            using var actual = Capture(input, sourceSize.Width, sourceSize.Height);
                            Check(Difference(reference, actual) < .02, filter + " bypasses at source " + sourceSize);
                        }
                    }
                    if (!dx11 && !args.Contains("--reshade")) Benchmark((D3D12PreviewRenderer)renderer, settings);
                    Check(renderer.IsActive, "all scaler changes applied on the same live renderer");
                }
                catch (Exception ex) { failure = ex; }
                finally { window.BeginInvoke(() => window.Close()); }
            });
        };
        Application.Run(window);
        worker!.GetAwaiter().GetResult();
        if (failure != null) throw failure;
        Console.WriteLine($"PASS: {passed} scaling checks; evidence: {Evidence}");
    }

    private static void Benchmark(D3D12PreviewRenderer renderer, PreviewRenderSettings settings)
    {
        var waitForGpu = typeof(D3D12PreviewRenderer).GetMethod("WaitForGpu", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var input = Pattern(1280, 720);
        var results = new List<object>();
        settings.Output.PreserveAspectRatio = true;
        foreach (var size in new[] { new Size(1920, 1080), new Size(2560, 1440), new Size(3840, 2160) })
        foreach (var filter in new[] { ScalingFilter.Bilinear, ScalingFilter.SharpBilinear, ScalingFilter.FsrEasu, ScalingFilter.AnimeLineArt })
        {
            settings.Scaling.Filter = filter;
            var samples = new List<double>();
            for (int i = 0; i < 150; i++)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                while (!renderer.WaitForPresentation(timeout.Token)) { }
                long start = Stopwatch.GetTimestamp();
                renderer.Render(input, 1280, 720, 5120, false, size, settings);
                waitForGpu.Invoke(renderer, null);
                if (i >= 30) samples.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            }
            samples.Sort();
            var row = new { Filter = filter.ToString(), Output = $"{size.Width}x{size.Height}", MedianMs = samples[60], P95Ms = samples[114], MaxMs = samples[^1] };
            results.Add(row);
            Console.WriteLine("BENCHMARK upload/render/Present/fence wall time (not isolated GPU shader time): " + JsonSerializer.Serialize(row));
        }
        File.WriteAllText(Path.Combine(Evidence, "benchmark.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
    }

    internal static byte[] Pattern(int width, int height)
    {
        using var image = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(image))
        {
            graphics.Clear(Color.FromArgb(225, 235, 244));
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            graphics.ScaleTransform(width / 320f, height / 240f);
            using var ink = new Pen(Color.FromArgb(24, 28, 40), 2);
            using var fill = new SolidBrush(Color.FromArgb(115, 194, 201));
            graphics.FillEllipse(fill, 25, 20, 170, 170);
            graphics.DrawEllipse(ink, 25, 20, 170, 170);
            graphics.DrawBezier(ink, 10, 220, 100, 0, 210, 230, 300, 30);
            graphics.DrawLine(ink, 30, 200, 270, 25);
            for (int i = 0; i < 60; i++)
            {
                using var shade = new SolidBrush(Color.FromArgb(45 + i * 3, 60 + i * 2, 95));
                graphics.FillRectangle(shade, 220 + i, 145, 1, 70);
            }
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
            for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++)
                graphics.FillRectangle(((x + y) & 1) == 0 ? Brushes.Black : Brushes.White, 220 + x * 4, 40 + y * 4, 4, 4);
            using var font = new Font("Segoe UI", 12);
            graphics.DrawString("480p / 720p", font, Brushes.Black, 25, 215);
        }
        return Pixels(image);
    }

    private static byte[] Pixels(Bitmap bitmap)
    {
        var bits = bitmap.LockBits(new Rectangle(Point.Empty, bitmap.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var result = new byte[bitmap.Width * bitmap.Height * 4];
            for (int y = 0; y < bitmap.Height; y++) Marshal.Copy(bits.Scan0 + y * bits.Stride, result, y * bitmap.Width * 4, bitmap.Width * 4);
            return result;
        }
        finally { bitmap.UnlockBits(bits); }
    }

    internal static double Difference(Bitmap first, Bitmap second)
    {
        byte[] a = Pixels(first), b = Pixels(second);
        long difference = 0;
        for (int i = 0; i < a.Length; i++) if ((i & 3) != 3) difference += Math.Abs(a[i] - b[i]);
        return difference / (double)(first.Width * first.Height * 3);
    }
}
