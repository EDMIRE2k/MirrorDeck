using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json;
using MirrorDeck.Core.Models;
using MirrorDeck.PreviewHost;

internal static class DlssTextTests
{
    internal static void Run(string[] args)
    {
        bool baseline = args.Contains("--baseline");
        string evidence = Path.Combine(AppContext.BaseDirectory, "dlss-text", baseline ? "before" : "after");
        Directory.CreateDirectory(evidence);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        using var window = new Form { Text = "DLSS menu transition verification", ClientSize = new Size(1280, 720) };
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
                        Scaling = new() { Filter = ScalingFilter.Bilinear, Sharpness = 0 }, Enhancement = new() { CleanUpscalingEnabled = false } };
                    var a = Menu(false); var b = Menu(true);
                    void Draw(byte[] input)
                    {
                        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                        while (!renderer.WaitForPresentation(timeout.Token)) { }
                        renderer.Render(input, 640, 360, 2560, false, new Size(1280, 720), settings);
                    }
                    Draw(b);
                    using var bilinear = NativeDlssTests.ReadBack(renderer);
                    var bilinearBytes = Pixels(bilinear);
                    bilinear.Save(Path.Combine(evidence, "bilinear-reference.png"));
                    // Protected strokes now use edge-adaptive reconstruction. Compare
                    // shape against that reference, while still checking loss against
                    // the original bilinear stroke pixels (not a relaxed threshold).
                    if (!baseline) settings.Scaling.Filter = ScalingFilter.FsrEasu;
                    Draw(b);
                    using var reference = NativeDlssTests.ReadBack(renderer);
                    reference.Save(Path.Combine(evidence, "reference.png"));
                    var referenceBytes = Pixels(reference);
                    settings.Scaling.Filter = ScalingFilter.Dlss45;
                    for (int i = 0; i < 64; i++) Draw(a);
                    var results = new List<object>();
                    for (int i = 0; i < 32; i++)
                    {
                        Draw(b);
                        if (i != 0 && i != 1 && i != 7 && i != 15 && i != 31) continue;
                        using var actual = NativeDlssTests.ReadBack(renderer);
                        actual.Save(Path.Combine(evidence, $"transition-{i:00}.png"));
                        var bytes = Pixels(actual);
                        long delta = 0; int count = 0; int lost = 0;
                        for (int y = 48; y < 660; y++) for (int x = 40; x < 1180; x++)
                        {
                            int p = (y * 1280 + x) * 4;
                            if (bilinearBytes[p] < 110 || bilinearBytes[p + 1] < 110) continue;
                            count++;
                            for (int c = 0; c < 3; c++) delta += Math.Abs(bytes[p+c] - referenceBytes[p+c]);
                            if (bytes[p] < bilinearBytes[p] * .70) lost++;
                        }
                        var row = new { Frame = i, TextMeanError = delta / (3.0 * count), LostTextFraction = (double)lost / count };
                        results.Add(row);
                        Console.WriteLine(JsonSerializer.Serialize(row));
                        if (!baseline && (row.TextMeanError > 8 || row.LostTextFraction > .005)) throw new Exception("Text strokes damaged during screen transition");
                    }
                    if (!renderer.BackendName.Contains("native DLSS")) throw new Exception("Test fell back from native DLSS");
                    File.WriteAllText(Path.Combine(evidence, "metrics.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
                }
                catch (Exception ex) { failure = ex; }
                finally { window.BeginInvoke(() => window.Close()); }
            });
        };
        Application.Run(window);
        worker!.GetAwaiter().GetResult();
        if (failure != null) throw failure;
    }

    private static byte[] Menu(bool second)
    {
        using var bitmap = new Bitmap(640, 360, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.Clear(second ? Color.FromArgb(49, 44, 25) : Color.FromArgb(20, 44, 39));
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using var font = new Font("Segoe UI", 8, FontStyle.Regular, GraphicsUnit.Pixel);
            using var larger = new Font("Segoe UI", 10, FontStyle.Regular, GraphicsUnit.Pixel);
            string text = second ? "Quick Select Items  |  Open Map  |  Jump  |  Reset Camera (Press)" : "Equipment  36 / 99  Collection  Select a different item";
            for (int y = 30; y < 320; y += 24)
            {
                g.DrawString(text, y % 48 == 30 ? font : larger, Brushes.White, second ? 40 : 72, y);
                g.DrawLine(Pens.Gray, 40, y + 15, 580, y + 15);
            }
        }
        return Pixels(bitmap);
    }

    private static byte[] Pixels(Bitmap image)
    {
        var bits = image.LockBits(new Rectangle(Point.Empty, image.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var data = new byte[image.Width * image.Height * 4];
            for (int y = 0; y < image.Height; y++) Marshal.Copy(bits.Scan0 + y * bits.Stride, data, y * image.Width * 4, image.Width * 4);
            return data;
        }
        finally { image.UnlockBits(bits); }
    }
}
