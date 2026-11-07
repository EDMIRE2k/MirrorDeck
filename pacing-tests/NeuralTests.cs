using System.Diagnostics;
using System.Reflection;
using MirrorDeck.Core.Models;
using MirrorDeck.PreviewHost;
using MirrorDeck.Rendering;

internal static class NeuralTests
{
    internal static void Run(string[] args)
    {
        foreach (var gpu in new[] { (0x1002u, "AMD Radeon RX 7900", false), (0x8086u, "Intel Arc", false),
            (0x10deu, "NVIDIA GeForce GTX 1080", false), (0x10deu, "NVIDIA GeForce RTX 2060", true),
            (0x10deu, "NVIDIA RTX A2000", true), (0x10deu, "NVIDIA GeForce RTX 5090", true) })
            if (PreviewGpu.IsRtx(gpu.Item1, gpu.Item2) != gpu.Item3) throw new Exception("GPU classification: " + gpu.Item2);
        if (PreviewGpu.Filters(false).Any(PreviewGpu.IsDlss) || PreviewGpu.Validate(ScalingFilter.Dlss5, false) != ScalingFilter.FsrEasu)
            throw new Exception("Non-RTX filters/profiles not gated");
        Console.WriteLine("PASS: RTX-only filter and profile policy");
        Directory.SetCurrentDirectory(AppContext.BaseDirectory);
        using var window = new Form { Text = "MirrorDeck native NR test", ClientSize = new Size(1280, 720) };
        Exception? failure = null;
        window.Shown += (_, _) => Task.Run(() =>
        {
            try
            {
                using var renderer = new D3D12PreviewRenderer(window.Handle, new Size(1280, 720), Console.WriteLine);
                var s = new PreviewRenderSettings { DisableProcessing = false, VSyncEnabled = false };
                s.Scaling.Sharpness = 0;
                s.Scaling.Filter = ScalingFilter.Dlss5;
                s.Scaling.Neural.WorkHeight = 720;
                var drain = typeof(D3D12PreviewRenderer).GetMethod("WaitForGpu", BindingFlags.NonPublic | BindingFlags.Instance)!;
                var pixels = ScalingTests.Pattern(1920, 1080);
                var output = new Size(3840, 2160);
                var times = new List<double>();
                for (int i = 0; i < 100; i++)
                {
                    using var timeout = new CancellationTokenSource(15000);
                    while (!renderer.WaitForPresentation(timeout.Token)) { }
                    var sw = Stopwatch.StartNew();
                    renderer.Render(pixels, 1920, 1080, 7680, false, output, s);
                    drain.Invoke(renderer, null);
                    if (i > 20) times.Add(sw.Elapsed.TotalMilliseconds);
                    if (!renderer.BackendName.Contains("native DLSS 5")) throw new Exception(renderer.BackendName);
                }
                times.Sort();
                Console.WriteLine($"NR render milliseconds median={times[times.Count/2]:F2}; p95={times[(int)(times.Count*.95)]:F2}");
                using var active = NativeDlssTests.ReadBack(renderer);
                active.Save(Path.Combine(AppContext.BaseDirectory, "native-nr.png"));
                s.Scaling.Neural.Intensity = 0;
                var native = typeof(D3D12PreviewRenderer).GetField("_dlss", BindingFlags.NonPublic | BindingFlags.Instance)!;
                var context = native.GetValue(renderer);
                renderer.Render(pixels, 1920, 1080, 7680, false, output, s);
                using var bypass = NativeDlssTests.ReadBack(renderer);
                bypass.Save(Path.Combine(AppContext.BaseDirectory, "native-nr-bypass.png"));
                double difference = ScalingTests.Difference(active, bypass);
                if (difference < .05 || difference > 60) throw new Exception("NR output difference out of bounds: " + difference);
                Console.WriteLine("NR difference " + difference);
                if (!ReferenceEquals(context, native.GetValue(renderer))) throw new Exception("Intensity discarded NR context");
                s.Scaling.Filter = ScalingFilter.FsrEasu;
                renderer.Render(pixels, 1920, 1080, 7680, false, output, s);
                using var spatial = NativeDlssTests.ReadBack(renderer);
                if (ScalingTests.Difference(spatial, bypass) > .5) throw new Exception("Intensity zero does not match the unmodified source");
                Console.WriteLine("PASS: intensity zero retains resources and matches spatial-only output");
                foreach (var filter in new[] { ScalingFilter.Dlss45, ScalingFilter.Dlss5, ScalingFilter.Dlss45, ScalingFilter.Dlss5 })
                {
                    s.Scaling.Filter = filter;
                    s.Scaling.Neural.Intensity = 2;
                    renderer.Render(pixels, 1920, 1080, 7680, false, output, s);
                    drain.Invoke(renderer, null);
                    if (!renderer.BackendName.Contains(filter == ScalingFilter.Dlss5 ? "DLSS 5 NR" : "DLSS 4.5")) throw new Exception("Native switching failed: " + renderer.BackendName);
                }
                Console.WriteLine("PASS: repeated SR / NR switches release and rebuild the correct feature");
                foreach (int height in new[] { 360, 540, 720 })
                {
                    s.Scaling.Neural.WorkHeight = height;
                    foreach (var style in Enum.GetValues<NeuralStyle>())
                    {
                        s.Scaling.Neural.Style = style;
                        renderer.Render(pixels, 1920, 1080, 7680, false, output, s);
                        drain.Invoke(renderer, null);
                        if (!renderer.BackendName.Contains("DLSS 5 NR")) throw new Exception("NR reconfiguration failed");
                    }
                }
                Console.WriteLine("PASS: live work-size and style changes");
                s.Scaling.Neural.WorkHeight = 720;
                s.Scaling.Neural.Style = NeuralStyle.Default;
                foreach (int passes in new[] { 2, 3, 4, 1 })
                {
                    s.Scaling.Neural.Passes = passes;
                    var passTimes = new List<double>();
                    for (int frame = 0; frame < 30; frame++)
                    {
                        var timer = Stopwatch.StartNew();
                        renderer.Render(pixels, 1920, 1080, 7680, false, output, s);
                        drain.Invoke(renderer, null);
                        if (frame > 5) passTimes.Add(timer.Elapsed.TotalMilliseconds);
                        if (!renderer.BackendName.Contains("passes=" + passes)) throw new Exception("Pass count failed: " + renderer.BackendName);
                    }
                    passTimes.Sort();
                    using var repeated = NativeDlssTests.ReadBack(renderer);
                    repeated.Save(Path.Combine(AppContext.BaseDirectory, $"native-nr-{passes}passes.png"));
                    Console.WriteLine($"PASS: NR {passes} pass(es), median {passTimes[passTimes.Count/2]:F2} ms, p95 {passTimes[(int)(passTimes.Count*.95)]:F2} ms");
                }
                if (args.Contains("--debug-gpu"))
                {
                    var device = (Vortice.Direct3D12.ID3D12Device2)typeof(D3D12PreviewRenderer).GetField("_device", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(renderer)!;
                    using var info = device.QueryInterface<Vortice.Direct3D12.Debug.ID3D12InfoQueue>();
                    var messages = new List<string>();
                    for (ulong i = 0; i < info.NumStoredMessages; i++)
                    {
                        var message = info.GetMessage(i);
                        if (message.Severity is Vortice.Direct3D12.Debug.MessageSeverity.Error or Vortice.Direct3D12.Debug.MessageSeverity.Corruption)
                            messages.Add(message.Description);
                    }
                    File.WriteAllLines(Path.Combine(AppContext.BaseDirectory, "native-nr-debug-errors.txt"), messages);
                    if (messages.Count != 0) throw new Exception("NR debug layer errors: " + string.Join("\n", messages));
                    Console.WriteLine("PASS: NR debug layer reports no errors or corruption");
                }
                Console.WriteLine("PASS: native NR initialized, evaluated, and intensity updated live");
            }
            catch (Exception ex) { failure = ex; }
            finally { window.BeginInvoke(window.Close); }
        });
        Application.Run(window);
        if (failure != null) throw failure;
    }
}
