using System.Diagnostics;
using System.Drawing.Imaging;
using System.Reflection;
using System.Text.Json;
using MirrorDeck.Core.Models;
using MirrorDeck.PreviewHost;
using Vortice.Direct3D12;
using Vortice.DXGI;

internal static class NativeDlssTests
{
    internal static void Run(string[] args)
    {
        bool fallback = args.Contains("--native-fallback");
        var evidence = Path.Combine(AppContext.BaseDirectory, "native-dlss-evidence");
        Directory.CreateDirectory(evidence);
        Directory.SetCurrentDirectory(AppContext.BaseDirectory);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        using var window = new Form { Text = "MirrorDeck native DLSS verification", ClientSize = new Size(1280, 720), StartPosition = FormStartPosition.CenterScreen, TopMost = true };
        Exception? failure = null;
        Task? worker = null;
        int passed = 0;
        void Check(bool ok, string name)
        {
            if (!ok) throw new Exception("FAIL: " + name);
            passed++;
            Console.WriteLine("PASS: " + name);
        }
        window.Shown += (_, _) =>
        {
            var handle = window.Handle;
            worker = Task.Run(() =>
            {
                try
                {
                    using var renderer = new D3D12PreviewRenderer(handle, new Size(1280, 720), Console.WriteLine);
                    var drain = typeof(D3D12PreviewRenderer).GetMethod("WaitForGpu", BindingFlags.Instance | BindingFlags.NonPublic)!;
                    var settings = new PreviewRenderSettings { VSyncEnabled = false, DisableProcessing = false, Enhancement = new() { CleanUpscalingEnabled = false }, Scaling = new() { Sharpness = 0 } };
                    Size size = new(1280, 720);
                    void Draw(byte[] input, int w, int h, bool bottomUp = false)
                    {
                        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                        while (!renderer.WaitForPresentation(timeout.Token)) { }
                        renderer.Render(input, w, h, w * 4, bottomUp, size, settings);
                    }
                    Bitmap Capture(byte[] input, int w, int h, bool bottomUp = false)
                    {
                        for (int i = 0; i < 64; i++) Draw(input, w, h, bottomUp);
                        drain.Invoke(renderer, null);
                        return ReadBack(renderer);
                    }
                    Check((int)ScalingFilter.Dlss45 == 7 && JsonSerializer.Deserialize<ScalingSettings>(JsonSerializer.Serialize(new ScalingSettings { Filter = ScalingFilter.Dlss45 }))!.Filter == ScalingFilter.Dlss45, "native filter persists without renumbering older filters");
                    var input = ScalingTests.Pattern(640, 480);
                    settings.Scaling.Filter = ScalingFilter.Bilinear;
                    using var baseline = Capture(input, 640, 480);
                    baseline.Save(Path.Combine(evidence, "bilinear.png"));
                    settings.Scaling.Filter = ScalingFilter.Dlss45;
                    using var native = Capture(input, 640, 480);
                    native.Save(Path.Combine(evidence, fallback ? "fallback.png" : "native.png"));
                    if (fallback)
                    {
                        Check(renderer.BackendName.Contains("unavailable"), "missing native runtime reports fallback, not active DLSS");
                        Check(ScalingTests.Difference(baseline, native) < .05, "missing runtime continues rendering bilinear");
                        settings.Scaling.Filter = ScalingFilter.Bilinear;
                        Draw(input, 640, 480);
                        settings.Scaling.Filter = ScalingFilter.Dlss45;
                        Draw(input, 640, 480);
                        Check(renderer.IsActive && renderer.BackendName.Contains("unavailable"), "failed initialization can be retried without crashing");
                    }
                    else
                    {
                        double difference = ScalingTests.Difference(baseline, native);
                        Check(renderer.BackendName.Contains("native DLSS 4.5"), "NGX feature evaluated successfully without ReShade");
                        Check(difference > .05 && difference < 20, "native output is distinct and retains the source image: mean difference " + difference.ToString("F3"));
                        Check(native.GetPixel(10, 360).R < 5 && native.GetPixel(640, 360).G > 20, "native output preserves black bars and nonblank image");
                        var liveContext = typeof(D3D12PreviewRenderer).GetField("_dlss", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(renderer);
                        settings.Scaling.DlssSharpness = 1;
                        using var sharpened = Capture(input, 640, 480);
                        Check(ScalingTests.Difference(native, sharpened) > .05, "DLSS sharpness visibly changes the native output live");
                        Check(ReferenceEquals(liveContext, typeof(D3D12PreviewRenderer).GetField("_dlss", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(renderer)), "sharpness retains the current native DLSS context");
                        settings.Scaling.DlssSharpness = 0;
                        settings.Scaling.Sharpness = 1;
                        using var independent = Capture(input, 640, 480);
                        Check(ScalingTests.Difference(native, independent) < .05, "DLSS sharpness is independent of the hidden general sharpness setting");
                        settings.Scaling.Sharpness = 0;
                        settings.Scaling.DlssSharpness = double.NaN;
                        using var sanitized = Capture(input, 640, 480);
                        Check(ScalingTests.Difference(native, sanitized) < .05, "invalid DLSS sharpness safely becomes zero");
                        settings.Scaling.DlssSharpness = 0;
                        var inverted = new byte[input.Length];
                        for (int y = 0; y < 480; y++) Buffer.BlockCopy(input, y * 2560, inverted, (479 - y) * 2560, 2560);
                        using var oriented = Capture(inverted, 640, 480, true);
                        Check(ScalingTests.Difference(native, oriented) < 1, "bottom-up frames retain native image orientation");
                        settings.Scaling.DlssDetailProtection = 0;
                        using var unprotected = Capture(input, 640, 480);
                        Check(ScalingTests.Difference(native, unprotected) > .05, "lower protection changes static detail after history was held");
                        Check(ReferenceEquals(liveContext, typeof(D3D12PreviewRenderer).GetField("_dlss", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(renderer)), "live protection retains the native context");
                        settings.Scaling.DlssDetailProtection = double.NaN;
                        using var protectedAgain = Capture(input, 640, 480);
                        Check(ScalingTests.Difference(native, protectedAgain) < .05, "invalid protection restores the full default, including on static frames");
                        settings.Scaling.DlssDetailProtection = 1;
                        foreach (var model in new[] { DlssModel.K, DlssModel.L, DlssModel.M, DlssModel.Auto })
                        foreach (var quality in new[] { DlssMotionQuality.High, DlssMotionQuality.Medium })
                        {
                            settings.Scaling.DlssModel = model;
                            settings.Scaling.DlssMotionQuality = quality;
                            using var result = Capture(input, 640, 480);
                            string expected = model == DlssModel.Auto ? "K" : model.ToString(); // 1.5x aspect-fit output
                            Check(renderer.BackendName.Contains("preset " + expected) && renderer.BackendName.Contains("(" + quality + ")"), "live model / motion switch: " + model + " / " + quality);
                            Check(result.GetPixel(640, 360).G > 20, "model switch produces a nonblank image");
                        }
                        settings.Scaling.DlssModel = (DlssModel)999;
                        settings.Scaling.DlssMotionQuality = (DlssMotionQuality)999;
                        Draw(input, 640, 480);
                        Check(renderer.BackendName.Contains("preset M") && renderer.BackendName.Contains("(Medium)"), "unknown model / motion settings fall back to current defaults");
                        settings.Scaling.DlssModel = DlssModel.M;
                        settings.Scaling.DlssMotionQuality = DlssMotionQuality.Medium;
                        settings.DisableProcessing = true;
                        using var bypass = Capture(input, 640, 480);
                        Check(ScalingTests.Difference(baseline, bypass) < .05 && !renderer.BackendName.Contains("native"), "enhancement bypass releases native processing live");
                        settings.DisableProcessing = false;
                        using var restarted = Capture(input, 640, 480);
                        Check(renderer.BackendName.Contains("native DLSS"), "native processing restarts after bypass");
                        settings.Scaling.IntegerScaling = true;
                        using var integer = Capture(input, 640, 480);
                        Check(integer.GetPixel(400, 80).R < 5 && integer.GetPixel(640, 360).G > 20, "integer layout is calculated from original capture dimensions");
                        settings.Scaling.IntegerScaling = false;
                        settings.Scaling.BlackBars = false;
                        using var fill = Capture(input, 640, 480);
                        Check(fill.GetPixel(10, 360).G > 20, "fill layout does not acquire extra native black bars");
                        settings.Scaling.BlackBars = true;
                        var down = ScalingTests.Pattern(1600, 900);
                        Draw(down, 1600, 900);
                        Check(renderer.BackendName.Contains("downscaling"), "DLSS bypasses downscaling explicitly");
                        var hd = ScalingTests.Pattern(1280, 720);
                        var results = new List<object>();
                        foreach (var target in new[] { new Size(1920, 1080), new Size(2560, 1440) })
                        foreach (var filter in new[] { ScalingFilter.Bilinear, ScalingFilter.Dlss45 })
                        {
                            size = target;
                            settings.Scaling.Filter = filter;
                            var times = new List<double>();
                            for (int i = 0; i < 150; i++)
                            {
                                var start = Stopwatch.GetTimestamp();
                                Draw(hd, 1280, 720);
                                drain.Invoke(renderer, null);
                                if (i >= 30) times.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                            }
                            times.Sort();
                            var row = new { Filter = filter.ToString(), Output = target.ToString(), MedianMs = times[60], P95Ms = times[114], MaxMs = times[^1] };
                            results.Add(row);
                            Console.WriteLine("BENCHMARK total CPU wait/upload/GPU/Present wall time: " + JsonSerializer.Serialize(row));
                            Check(filter != ScalingFilter.Dlss45 || renderer.BackendName.Contains("native DLSS"), "native path remains active at " + target);
                        }
                        File.WriteAllText(Path.Combine(evidence, "benchmark.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
                        var fullHd = ScalingTests.Pattern(1920, 1080);
                        size = new Size(1280, 720);
                        settings.Scaling.DlssModel = DlssModel.Auto;
                        foreach (var target in new[] { DlssOutputResolution.P1440, DlssOutputResolution.P4K, DlssOutputResolution.InputResolution, DlssOutputResolution.CurrentMonitor })
                        {
                            settings.Scaling.DlssOutputResolution = target;
                            for (int i = 0; i < 32; i++) Draw(fullHd, 1920, 1080);
                            var monitor = Screen.FromHandle(handle).Bounds;
                            var expected = DlssOutputSizing.Resolve(target, 1920, 1080, 1280, 720, monitor.Width, monitor.Height);
                            var nativeContext = (NativeDlss)typeof(D3D12PreviewRenderer).GetField("_dlss", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(renderer)!;
                            Check(nativeContext.Status.Contains($"1920x1080 -> {expected.Width}x{expected.Height}"), "live " + target + " target processes 1080p capture at " + expected);
                            string expectedModel = (double)expected.Width / 1920 > 2.6 ? "L" : (double)expected.Width / 1920 > 1.8 ? "M" : "K";
                            Check(nativeContext.Status.Contains("preset " + expectedModel), "Auto selects " + expectedModel + " for " + target);
                        }
                        settings.Scaling.DlssOutputResolution = DlssOutputResolution.P4K;
                        Draw(hd, 1280, 720);
                        Check(renderer.BackendName.Contains("preset L"), "Auto selects L for 720p to 4K Ultra Performance");
                        settings.Output.ProcessingMaxHeight = 720;
                        foreach (var target in new[] { DlssOutputResolution.PreviewWindow, DlssOutputResolution.P1440, DlssOutputResolution.P4K })
                        foreach (var surface in new[] { new Size(1920, 1080), new Size(2560, 1440), new Size(3840, 2160) })
                        {
                            size = surface;
                            settings.Scaling.DlssOutputResolution = target;
                            Draw(fullHd, 1920, 1080);
                            using var image = ReadBack(renderer);
                            Check(image.Size == surface, "native final surface bypasses 720p cap: " + target + " / " + surface);
                            var expected = DlssOutputSizing.Resolve(target, 1920, 1080, surface.Width, surface.Height, 3840, 2160);
                            Check(renderer.BackendName.Contains($"1920x1080 -> {expected.Width}x{expected.Height}") && renderer.BackendName.Contains($"surface={surface.Width}x{surface.Height}"),
                                "native target and final surface update together across fullscreen sizes");
                        }
                        settings.DisableProcessing = true;
                        Draw(fullHd, 1920, 1080);
                        using (var capped = ReadBack(renderer)) Check(capped.Height == 720, "processing cap still applies with native enhancements disabled");
                        settings.DisableProcessing = false;
                        settings.Scaling.Filter = ScalingFilter.Bilinear;
                        Draw(fullHd, 1920, 1080);
                        using (var capped = ReadBack(renderer)) Check(capped.Height == 720, "processing cap still applies to other filters");
                        settings.Scaling.Filter = ScalingFilter.Dlss45;
                        settings.Output.ProcessingMaxHeight = 0;
                        size = new Size(1280, 720);
                        settings.Scaling.DlssOutputResolution = DlssOutputResolution.P4K;
                        foreach (var model in new[] { DlssModel.M, DlssModel.K, DlssModel.L })
                        foreach (var quality in new[] { DlssMotionQuality.Medium, DlssMotionQuality.High })
                        {
                            settings.Scaling.DlssModel = model;
                            settings.Scaling.DlssMotionQuality = quality;
                            var times = new List<double>();
                            for (int i = 0; i < 90; i++)
                            {
                                // Exercise changing frames, not just a held static history.
                                fullHd[(i * 1920 * 4 + i * 4) % fullHd.Length] ^= 127;
                                var start = Stopwatch.GetTimestamp();
                                Draw(fullHd, 1920, 1080);
                                drain.Invoke(renderer, null);
                                if (i >= 30) times.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                            }
                            times.Sort();
                            var row = new { Model = model.ToString(), MotionQuality = quality.ToString(), Input = "1920x1080", Output = "3840x2160", MedianMs = times[30], P95Ms = times[57], MaxMs = times[^1] };
                            results.Add(row);
                            Console.WriteLine("BENCHMARK 1080p -> 4K total wait/upload/GPU/Present: " + JsonSerializer.Serialize(row));
                            Check(renderer.BackendName.Contains("preset " + model) && renderer.BackendName.Contains("(" + quality + ")"), "1080p to 4K remains active: " + model + " / " + quality);
                        }
                        File.WriteAllText(Path.Combine(evidence, "benchmark.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
                        settings.Scaling.DlssOutputResolution = DlssOutputResolution.PreviewWindow;
                        Draw(fullHd, 1920, 1080);
                        Check(renderer.BackendName.Contains("downscaling"), "returning to smaller window mode bypasses native upscaling live");
                    }
                    if (args.Contains("--debug-gpu"))
                    {
                        var device = (ID3D12Device2)typeof(D3D12PreviewRenderer).GetField("_device", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(renderer)!;
                        using var info = device.QueryInterface<Vortice.Direct3D12.Debug.ID3D12InfoQueue>();
                        var messages = new List<string>();
                        bool errors = false;
                        for (ulong i = 0; i < info.NumStoredMessages; i++)
                        {
                            var message = info.GetMessage(i);
                            messages.Add(message.Severity + ": " + message.Description);
                            errors |= message.Severity == Vortice.Direct3D12.Debug.MessageSeverity.Error || message.Severity == Vortice.Direct3D12.Debug.MessageSeverity.Corruption;
                        }
                        File.WriteAllLines(Path.Combine(evidence, "debug-gpu.txt"), messages);
                        Check(!errors, "D3D12 debug layer reports no errors or corruption");
                    }
                }
                catch (Exception ex) { failure = ex; }
                finally { window.BeginInvoke(() => window.Close()); }
            });
        };
        Application.Run(window);
        worker!.GetAwaiter().GetResult();
        if (failure != null) throw failure;
        Console.WriteLine($"PASS: {passed} native DLSS checks; evidence: {evidence}");
    }

    internal static unsafe Bitmap ReadBack(D3D12PreviewRenderer renderer)
    {
        T Field<T>(string name) => (T)typeof(D3D12PreviewRenderer).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(renderer)!;
        var device = Field<ID3D12Device2>("_device");
        var queue = Field<ID3D12CommandQueue>("_commandQueue");
        var swap = Field<IDXGISwapChain3>("_swapChain");
        using var texture = swap.GetBuffer<ID3D12Resource>(1 - swap.CurrentBackBufferIndex);
        int width = (int)texture.Description.Width, height = (int)texture.Description.Height;
        uint pitch = ((uint)width * 4 + 255) & ~255u;
        using var readback = device.CreateCommittedResource(HeapType.Readback, ResourceDescription.Buffer((ulong)pitch * (uint)height), ResourceStates.CopyDest);
        using var allocator = device.CreateCommandAllocator(CommandListType.Direct);
        using var list = device.CreateCommandList<ID3D12GraphicsCommandList>(CommandListType.Direct, allocator);
        var footprint = new PlacedSubresourceFootPrint { Footprint = new SubresourceFootPrint { Format = Format.B8G8R8A8_UNorm, Width = (uint)width, Height = (uint)height, Depth = 1, RowPitch = pitch } };
        list.ResourceBarrierTransition(texture, ResourceStates.Common, ResourceStates.CopySource);
        list.CopyTextureRegion(new TextureCopyLocation(readback, footprint), 0, 0, 0, new TextureCopyLocation(texture, 0));
        list.ResourceBarrierTransition(texture, ResourceStates.CopySource, ResourceStates.Common);
        list.Close();
        queue.ExecuteCommandList(list);
        using var fence = device.CreateFence(0);
        using var done = new AutoResetEvent(false);
        queue.Signal(fence, 1).CheckError();
        fence.SetEventOnCompletion(1, done).CheckError();
        if (!done.WaitOne(TimeSpan.FromSeconds(10))) throw new TimeoutException("GPU output readback");
        void* data = null;
        readback.Map(0, null, &data).CheckError();
        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        var bits = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < height; y++) Buffer.MemoryCopy((byte*)data + y * pitch, (byte*)bits.Scan0 + y * bits.Stride, width * 4, width * 4);
        }
        finally { bitmap.UnlockBits(bits); readback.Unmap(0); }
        return bitmap;
    }
}
