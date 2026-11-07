using System.Buffers;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using MirrorDeck.Capture.DirectShow;
using MirrorDeck.Core.Models;
using MirrorDeck.PreviewHost;

internal static class Program
{
    private static int _passed;
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Contains("--debug-gpu"))
            {
                using var debug = Vortice.Direct3D12.D3D12.D3D12GetDebugInterface<Vortice.Direct3D12.Debug.ID3D12Debug>();
                debug.EnableDebugLayer();
            }
            QueueTests();
            ClockTests();
            WaitTests();
            DlssSizingTests();
            if (args.Contains("--dlss-stability")) DlssStabilityTests.Run(args);
            if (args.Contains("--dlss-text")) DlssTextTests.Run(args);
            if (args.Contains("--native-nr")) NeuralTests.Run(args);
            if (args.Contains("--native-scaling") || args.Contains("--native-fallback")) NativeDlssTests.Run(args);
            if (args.Contains("--scaling")) ScalingTests.Run(args);
            if (args.Contains("--gpu") || args.Contains("--wgc") || args.Contains("--usb")) Smoke(args);
            Console.WriteLine($"PASS: {_passed} pacing checks");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL: " + name);
        _passed++; Console.WriteLine("PASS: " + name);
    }
    private static CapturedFrame Frame(long tick, int width = 2) => new(ArrayPool<byte>.Shared.Rent(width * 8), width, 2, width * 4, false, tick, tick);

    private static void QueueTests()
    {
        using var queue = new CaptureFrameQueue();
        queue.Enqueue(Frame(10), 2);
        queue.Enqueue(Frame(20, 3), 2);
        Check(queue.Count == 2, "buffer count creates a real two-frame queue");
        Check(!queue.TryTake(15, 10, false, out _, out var wait) && wait == 5, "buffered frame waits for its source deadline");
        Check(queue.TryTake(20, 10, false, out var first, out _) && first!.SourceTicks == 10, "buffered frames retain FIFO ordering");
        first!.Dispose();
        Check(queue.TryTake(40, 0, false, out var resized, out _) && resized!.Width == 3 && resized.Stride == 12,
            "resize metadata stays attached to its own pixels");
        resized!.Dispose();
        queue.Enqueue(Frame(30), 2); queue.Enqueue(Frame(40), 2); queue.Enqueue(Frame(50), 2);
        Check(queue.Count == 2 && queue.Dropped == 1, "overflow is bounded and drops the oldest capture");
        Check(queue.TryTake(60, 0, true, out var latest, out _) && latest!.SourceTicks == 50 && queue.Dropped == 2,
            "low-latency mode selects the freshest capture");
        latest!.Dispose();
        queue.Enqueue(Frame(1), 2); queue.Enqueue(Frame(Stopwatch.Frequency), 2);
        Check(queue.TryTake(Stopwatch.Frequency + 1, 0, false, out var recovered, out _) && recovered!.SourceTicks == Stopwatch.Frequency,
            "stale backlog is discarded after a pause");
        recovered!.Dispose();
        Check(!queue.TryTake(0, 0, true, out _, out _), "empty queue does not invent frames");
    }

    private static void ClockTests()
    {
        var clock = new CaptureClock();
        long start = Stopwatch.Frequency * 10;
        double period = 1001.0 / 60000;
        long first = clock.Map(0, start);
        long second = clock.Map(period, start + (long)(period * Stopwatch.Frequency) + Stopwatch.Frequency / 500);
        Check(Math.Abs((second - first) / (double)Stopwatch.Frequency - period) < .000001,
            "59.94 Hz timestamps retain fractional cadence despite arrival jitter");
        long reset = clock.Map(0, start + Stopwatch.Frequency / 2);
        Check(reset == start + Stopwatch.Frequency / 2, "source timestamp reset reanchors the clock");
        Check(clock.Map(double.NaN, 123) == 123, "missing source timestamp falls back to monotonic arrival time");
        Check(clock.Map(300, start + Stopwatch.Frequency * 3) == start + Stopwatch.Frequency * 3,
            "large timestamp discontinuities do not stall playback");
    }

    private static void WaitTests()
    {
        using var wait = new FrameDeadlineWait();
        using var change = new AutoResetEvent(true);
        using var stop = new CancellationTokenSource();
        var timer = Stopwatch.StartNew();
        wait.Wait(Stopwatch.Frequency, stop.Token, change);
        Check(timer.ElapsedMilliseconds < 100, "new frame or resize interrupts a pending deadline");
        stop.Cancel();
        bool canceled = false;
        try { wait.Wait(Stopwatch.Frequency, stop.Token, change); }
        catch (OperationCanceledException) { canceled = true; }
        Check(canceled, "shutdown cancels a deadline wait immediately");
    }

    private static void DlssSizingTests()
    {
        Check(DlssOutputSizing.Resolve(DlssOutputResolution.P1440, 1920, 1080, 640, 360, 3840, 2160) == (2560, 1440), "1080p capture can target 1440p independently of window size");
        Check(DlssOutputSizing.Resolve(DlssOutputResolution.P4K, 1920, 1080, 640, 360, 2560, 1440) == (3840, 2160), "1080p capture can target 4K independently of monitor size");
        Check(DlssOutputSizing.Resolve(DlssOutputResolution.PreviewWindow, 1920, 1080, 2304, 1296, 3840, 2160) == (2304, 1296), "window mode follows the preview viewport");
        Check(DlssOutputSizing.Resolve(DlssOutputResolution.CurrentMonitor, 1920, 1080, 640, 360, 3840, 2160) == (3840, 2160), "monitor mode follows the current monitor");
        Check(DlssOutputSizing.Resolve(DlssOutputResolution.P1440, 640, 480, 640, 360, 3840, 2160) == (1920, 1440), "fixed DLSS target preserves 4:3 input aspect");
        var settings = new MirrorDeckSettings { Scaling = new() { Filter = ScalingFilter.Dlss45, DlssOutputResolution = DlssOutputResolution.P4K } };
        var saved = JsonSerializer.Deserialize<MirrorDeckSettings>(JsonSerializer.Serialize(settings))!;
        Check(PreviewRenderSettings.From(saved).Scaling.DlssOutputResolution == DlssOutputResolution.P4K, "DLSS target survives settings/config JSON and preview IPC");
        settings.Scaling.DlssSharpness = .63;
        saved = JsonSerializer.Deserialize<MirrorDeckSettings>(JsonSerializer.Serialize(settings))!;
        Check(PreviewRenderSettings.From(saved).Scaling.DlssSharpness == .63, "DLSS sharpness survives saved configurations and preview IPC");
        Check(JsonSerializer.Deserialize<ScalingSettings>("{\"Filter\":7}")!.DlssSharpness == 0, "older configurations default to no additional DLSS sharpening");
        var legacy = JsonSerializer.Deserialize<ScalingSettings>("{\"Filter\":7}")!;
        Check(legacy.DlssModel == DlssModel.M && legacy.DlssMotionQuality == DlssMotionQuality.Medium && legacy.DlssDetailProtection == 1,
            "older configurations retain M, medium motion and full detail protection");
        settings.Scaling.DlssModel = DlssModel.L;
        settings.Scaling.DlssMotionQuality = DlssMotionQuality.High;
        settings.Scaling.DlssDetailProtection = .37;
        saved = JsonSerializer.Deserialize<MirrorDeckSettings>(JsonSerializer.Serialize(settings))!;
        var preview = PreviewRenderSettings.From(saved).Scaling;
        Check(preview.DlssModel == DlssModel.L && preview.DlssMotionQuality == DlssMotionQuality.High && preview.DlssDetailProtection == .37,
            "model, motion quality and protection survive config JSON and preview IPC");
    }

    private static object? Get(object obj, string field) => obj.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(obj);
    private static void Call(object obj, string method, params object?[] args) => obj.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(obj, args);

    private static void Smoke(string[] args)
    {
        Directory.SetCurrentDirectory(AppContext.BaseDirectory);
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "MirrorDeck.portable"), "");
        if (args.Contains("--reshade"))
        {
            ReShadeBootstrapper.Initialize(Console.WriteLine);
            Check(ReShadeBootstrapper.LocalProxyLoaded, "ReShade runtime loads before the graphics device");
        }
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        using var logger = PreviewHostLogger.Create();
        bool wgc = args.Contains("--wgc");
        bool usb = args.Contains("--usb");
        using var source = new Form { Text = "MirrorDeck pacing test source", ClientSize = new Size(640, 360) };
        if (wgc) source.Show();
        nint sourceHandle = wgc ? source.Handle : 0;
        string? device = wgc ? $"mirrordeck:window:{source.Handle.ToInt64():X}" : null;
        int? formatIndex = null;
        if (usb)
        {
            string settingsFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MirrorDeck", "MirrorDeck.settings.json");
            using var settings = JsonDocument.Parse(File.ReadAllText(settingsFile));
            string? selected = settings.RootElement.GetProperty("SelectedVideoDeviceId").GetString();
            var enumerator = new DirectShowVideoDeviceEnumerator();
            var savedDevice = enumerator.GetVideoDevicesAsync().GetAwaiter().GetResult().FirstOrDefault(item => item.Id == selected);
            if (savedDevice == null) { Console.WriteLine("SKIP: saved USB capture device is not connected."); return; }
            var formats = enumerator.GetFormatsAsync(savedDevice).GetAwaiter().GetResult();
            var format = formats.FirstOrDefault(item => item.Width == 1920 && item.Height == 1080 && Math.Abs(item.FramesPerSecond - 60) < .1);
            if (format == null) { Console.WriteLine("SKIP: saved device does not expose a 1080p60 mode."); return; }
            device = savedDevice.Id;
            formatIndex = format.Index;
            Console.WriteLine("Hardware smoke: " + savedDevice.Name + " / " + format.DisplayName);
        }
        bool nativeDlss = args.Contains("--native-dlss");
        bool nativeNr = args.Contains("--nr-smoke");
        bool upscalers = args.Contains("--upscalers") || nativeDlss || nativeNr;
        ScalingFilter nativeFilter = nativeNr ? ScalingFilter.Dlss5 : ScalingFilter.Dlss45;
        string? livePath = upscalers ? Path.Combine(AppContext.BaseDirectory, "scaler-smoke-settings.json") : null;
        var liveSettings = new PreviewRenderSettings { VSyncEnabled = true, LowLatencyMode = true, Scaling = new() { Sharpness = 0 } };
        void ChangeFilter(ScalingFilter filter)
        {
            liveSettings.Scaling.Filter = filter;
            File.WriteAllText(livePath! + ".tmp", JsonSerializer.Serialize(liveSettings, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            File.Move(livePath + ".tmp", livePath!, true);
        }
        if (upscalers) ChangeFilter(nativeDlss || nativeNr ? nativeFilter : ScalingFilter.Bilinear);
        using var preview = new PreviewForm(new PreviewHostOptions(device, formatIndex, 640, 360, false, livePath, null, true, 60), logger);
        preview.Text = "MirrorDeck pacing smoke test";
        using var cancel = new CancellationTokenSource();
        Task? producer = null;
        Exception? error = null;
        string renderer = "";
        var stopwatch = Stopwatch.StartNew();
        int stage = 0;
        using var tick = new System.Windows.Forms.Timer { Interval = 16 };
        preview.Shown += (_, _) =>
        {
            if (!wgc && !usb)
            {
                ((Panel)Get(preview, "_previewPanel")!).Controls.Clear();
                Call(preview, "HideReShadeTip");
                Call(preview, "StartRenderWorker");
                producer = Task.Run(() =>
                {
                    using var wait = new FrameDeadlineWait();
                    using var changed = new AutoResetEvent(false);
                    long origin = Stopwatch.GetTimestamp();
                    for (int frame = 0; !cancel.IsCancellationRequested; frame++)
                    {
                        long due = origin + (long)(frame * Stopwatch.Frequency / 60.0);
                        while (Stopwatch.GetTimestamp() < due && !cancel.IsCancellationRequested)
                            wait.Wait(due - Stopwatch.GetTimestamp(), cancel.Token, changed);
                        byte[] pixels = ArrayPool<byte>.Shared.Rent(640 * 360 * 4);
                        for (int i = 0; i < 640 * 360; i++)
                        {
                            int p = i * 4;
                            pixels[p] = (byte)(i % 640 + frame);
                            pixels[p + 1] = 100;
                            pixels[p + 2] = 200;
                            pixels[p + 3] = 255;
                        }
                        Call(preview, "QueueFrame", pixels, 640 * 360 * 4, 640, 360, 640 * 4, false, frame / 60.0);
                    }
                }, cancel.Token);
            }
            else if (wgc)
            {
                producer = Task.Run(() =>
                {
                    using var renderer = new D3D12PreviewRenderer(sourceHandle, new Size(640, 360), Console.WriteLine);
                    using var wait = new FrameDeadlineWait();
                    using var changed = new AutoResetEvent(false);
                    byte[] pixels = new byte[640 * 360 * 4];
                    var settings = new PreviewRenderSettings { DisableProcessing = true, VSyncEnabled = true };
                    long origin = Stopwatch.GetTimestamp();
                    for (int frame = 0; !cancel.IsCancellationRequested; frame++)
                    {
                        long due = origin + (long)(frame * Stopwatch.Frequency / 60.0);
                        while (Stopwatch.GetTimestamp() < due)
                            wait.Wait(due - Stopwatch.GetTimestamp(), cancel.Token, changed);
                        while (!renderer.WaitForPresentation(cancel.Token)) { }
                        for (int i = 0; i < pixels.Length; i += 4)
                        {
                            pixels[i] = (byte)((i / 4) % 640 + frame * 2);
                            pixels[i + 1] = 100; pixels[i + 2] = 200; pixels[i + 3] = 255;
                        }
                        renderer.Render(pixels, 640, 360, 2560, false, new Size(640, 360), settings);
                    }
                }, cancel.Token);
            }
            stopwatch.Restart(); tick.Start();
        };
        tick.Tick += (_, _) =>
        {
            try
            {
                int second = (int)stopwatch.Elapsed.TotalSeconds;
                if (second < stage + 2) return;
                renderer = (string)Get(preview, "_rendererMode")!;
                if (stage == 0) { preview.ClientSize = new Size(960, 540); if (upscalers) ChangeFilter(nativeDlss || nativeNr ? nativeFilter : ScalingFilter.FsrEasu); stage = 2; }
                else if (stage == 2)
                {
                    Call(preview, "ToggleFullscreen");
                    if (nativeDlss)
                    {
                        liveSettings.Scaling.DlssSharpness = .63;
                        liveSettings.Scaling.DlssModel = DlssModel.Auto;
                        liveSettings.Scaling.DlssMotionQuality = args.Contains("--high-motion") ? DlssMotionQuality.High : DlssMotionQuality.Medium;
                        liveSettings.Scaling.DlssDetailProtection = .73;
                        liveSettings.Output.ProcessingMaxHeight = 720;
                    }
                    if (nativeNr) { liveSettings.Scaling.Neural.Intensity = .6; liveSettings.Scaling.Neural.Style = NeuralStyle.Cinematic; }
                    if (upscalers) ChangeFilter(nativeDlss || nativeNr ? nativeFilter : ScalingFilter.AnimeLineArt);
                    stage = 4;
                }
                else if (stage == 4)
                {
                    if (upscalers && second < 16) return;
                    if (!usb) using (var pixels = new Bitmap(64, 64))
                    {
                        using (var graphics = Graphics.FromImage(pixels))
                            graphics.CopyFromScreen(preview.PointToScreen(new Point(preview.ClientSize.Width / 2, preview.ClientSize.Height / 2)), Point.Empty, pixels.Size);
                        int lit = 0;
                        for (int y = 0; y < 64; y++) for (int x = 0; x < 64; x++)
                        {
                            var color = pixels.GetPixel(x, y);
                            if (color.R > 20 || color.G > 20 || color.B > 20) lit++;
                        }
                        pixels.Save(Path.Combine(AppContext.BaseDirectory, wgc ? "wgc-fullscreen.png" : "synthetic-fullscreen.png"));
                        Check(lit > 3000, "fullscreen preview contains rendered pixels, not a blank surface");
                    }
                    Call(preview, "ToggleFullscreen");
                    if (upscalers)
                    {
                        var current = (PreviewRenderSettings)Get(preview, "_renderSettings")!;
                        Check(current.Scaling.Filter == (nativeDlss || nativeNr ? nativeFilter : ScalingFilter.AnimeLineArt), "live settings watcher applied requested scaler without restarting");
                        if (nativeNr) Check(current.Scaling.Neural.Intensity == .6 && current.Scaling.Neural.Style == NeuralStyle.Cinematic && renderer.Contains("DLSS 5 NR"), "native NR style and intensity changed live through atomic settings replacement: " + renderer);
                        if (nativeDlss) Check(current.Scaling.DlssSharpness == .63, "DLSS sharpness changed through live settings IPC without relaunch");
                        if (nativeDlss) Check(current.Scaling.DlssModel == DlssModel.Auto && current.Scaling.DlssDetailProtection == .73 &&
                            current.Scaling.DlssMotionQuality == (args.Contains("--high-motion") ? DlssMotionQuality.High : DlssMotionQuality.Medium),
                            "model, motion and protection changed through live settings IPC without relaunch");
                        if (nativeDlss) Check(args.Contains("--reshade") ? renderer.Contains("native DLSS bypassed") : renderer.Contains("native DLSS") && renderer.Contains("preset"),
                            args.Contains("--reshade") ? "ReShade owns NGX without a conflicting native session: " + renderer : "native NGX DLSS is active, not a fallback: " + renderer);
                        if (nativeDlss && !args.Contains("--reshade"))
                        {
                            var panel = (Control)Get(preview, "_previewPanel")!;
                            // The window has just left fullscreen; compare against the
                            // monitor surface reported before that transition.
                            var fullSize = Screen.FromControl(panel).Bounds.Size;
                            Check(renderer.Contains($"surface={fullSize.Width}x{fullSize.Height}"), "fullscreen native output uses physical monitor pixels despite a 720p processing cap");
                        }
                        liveSettings.LowLatencyMode = false;
                        liveSettings.FrameBufferCount = 2;
                        ChangeFilter(ScalingFilter.SharpBilinear);
                        stage = 16;
                    }
                    else
                    {
                        var settings = (PreviewRenderSettings)Get(preview, "_renderSettings")!;
                        settings.LowLatencyMode = false;
                        stage = 6;
                    }
                }
                else { tick.Stop(); cancel.Cancel(); preview.Close(); }
            }
            catch (Exception ex) { error = ex; tick.Stop(); cancel.Cancel(); preview.Close(); }
        };
        Application.Run(preview);
        cancel.Cancel();
        try { producer?.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
        if (wgc) source.Close();
        if (error != null) throw error;
        Check(renderer.StartsWith("D3D12"), "DX12 rendered through resize/fullscreen/buffer-mode changes: " + renderer);
        Check(((TaskCompletionSource)Get(preview, "_renderStopped")!).Task.IsCompleted,
            "render worker shut down without blocking the window thread");
        Console.WriteLine("Smoke log: " + Path.Combine(AppContext.BaseDirectory, "MirrorDeck.log"));
    }
}
