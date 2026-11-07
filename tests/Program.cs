using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MirrorDeck.App;
using MirrorDeck.App.Services;
using MirrorDeck.App.ViewModels;
using MirrorDeck.Core.Models;
using MirrorDeck.Core.Services;

internal static class Program
{
    private static int passed;
    private static string root = "";
    private static string runtime = "";

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            runtime = Path.GetFullPath(args[0]);
            root = Path.GetFullPath(args[1]);
            Directory.CreateDirectory(root);
            CheckUninstall();
            CheckLockedFiles();
            CheckUnrelatedDll();
            CheckReinstall();
            CheckOptionalInstall();
            CheckGpuPolicy();
            CheckAtomicSettingsAsync().GetAwaiter().GetResult();
            CheckMaintenanceAsync().GetAwaiter().GetResult();
            CheckWindow();
            Console.WriteLine($"PASS: {passed} regression checks. Evidence: {root}");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static string Fixture(string name)
    {
        string folder = Path.Combine(root, name + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        Console.WriteLine("PASS: " + message);
        passed++;
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static void CheckGpuPolicy()
    {
        foreach (var gpu in new[] { new MirrorDeck.Rendering.PreviewGpuInfo("AMD Radeon", false), new("Intel Arc", false), new("NVIDIA GTX 1080", false), new("NVIDIA RTX 2060", true) })
        {
            var vm = new MainViewModel(null!, new TestVideo(), null!, new TestAudio(), null!, new TestLogger(), gpu);
            Assert(vm.ScalingFilters.Contains(ScalingFilter.Dlss45) == gpu.SupportsDlss && vm.ScalingFilters.Contains(ScalingFilter.Dlss5) == gpu.SupportsDlss, gpu.Name + " has the correct DLSS filter list");
            foreach (var filter in new[] { ScalingFilter.Dlss45, ScalingFilter.Dlss5 })
            {
                vm.Settings = new() { Scaling = new() { Filter = filter } };
                Assert(vm.Settings.Scaling.Filter == (gpu.SupportsDlss ? filter : ScalingFilter.FsrEasu), gpu.Name + " profile loading validates " + filter);
            }
        }
    }

    private static async Task CheckAtomicSettingsAsync()
    {
        string path = Path.Combine(Fixture("autosave"), "MirrorDeck.settings.json");
        var store = new MirrorDeck.Settings.Persistence.JsonSettingsStore(path);
        var settings = new MirrorDeckSettings();
        var saves = new List<Task>();
        for (int i = 0; i < 12; i++)
        {
            settings.Scaling.Neural.Intensity = i / 10.0;
            saves.Add(store.SaveAsync(settings));
        }
        await Task.WhenAll(saves);
        var restored = await store.LoadAsync();
        Assert(restored.Scaling.Neural.Intensity == 1.1, "overlapping autosaves persist the latest complete snapshot");
        string before = Hash(path);
        settings.Scaling.Sharpness = double.NaN;
        try { await store.SaveAsync(settings); throw new Exception("Invalid JSON value unexpectedly saved"); }
        catch (ArgumentException) { }
        Assert(Hash(path) == before, "invalid settings cannot truncate the last good save");
    }

    private static void CheckOptionalInstall()
    {
        string path = Fixture("optional-reshade");
        foreach (string name in new[] { "dlss5-feed.addon64", "renodx-dlss5.addon64", "nvngx_dlss.dll", "nvngx_dlssnr.dll", "ReShade.ini", "ReShadePreset.ini", "reshade-shaders/Shaders/DLSS5_Feed.fx" })
        {
            string file = Path.Combine(path, name);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, "fixture");
        }
        var service = new DlssInstallerService(path);
        Assert(!service.CheckStatus().IsPresent && service.CheckStatus().Message.Contains("optional"), "no ReShade runtime is a normal optional-install state");
        File.Copy(runtime, Path.Combine(path, "dxgi.dll"));
        Assert(service.CheckStatus().IsPresent, "installing ReShade reuses the preserved components");
    }

    private static void CheckUninstall()
    {
        string path = Fixture("preservation");
        var preserved = new Dictionary<string, string>();
        foreach (string name in new[] { "dlss5-feed.addon64", "renodx-dlss5.addon64", "nvngx_dlss.dll", "nvngx_dlssnr.dll",
            "ReShade.ini", "ReShadePreset.ini", "MirrorDeck.settings.json", "preview-render-settings.json",
            "reshade-shaders/Shaders/DLSS5_Feed.fx", "reshade-shaders/Textures/test.png", "presets/custom.ini", "d3dcompiler_47.dll" })
        {
            string file = Path.Combine(path, name);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, "preserved payload " + name);
            preserved.Add(file, Hash(file));
        }
        foreach (string name in new[] { "dxgi.dll", "d3d11.dll", "d3d10.dll" })
            File.Copy(runtime, Path.Combine(path, name));
        Assert(ReShadeUninstaller.Uninstall(path) == 3, "all three recognized ReShade proxy names removed");
        Assert(new[] { "dxgi.dll", "d3d11.dll", "d3d10.dll" }.All(name => !File.Exists(Path.Combine(path, name))), "no active ReShade proxy remains");
        Assert(preserved.All(pair => File.Exists(pair.Key) && Hash(pair.Key) == pair.Value), "every add-on, preset, shader, settings and model file preserved byte-for-byte");
        Assert(Directory.GetFiles(Path.Combine(path, "ReShade-runtime-backups"), "*.dll", SearchOption.AllDirectories)
            .All(file => Hash(file) == Hash(runtime)), "recoverable runtime backup preserved byte-for-byte");
        Assert(ReShadeUninstaller.Uninstall(path) == 0, "repeated uninstall is harmless");
        Assert(!new DlssInstallerService(path).CheckStatus().IsPresent, "status no longer reports ReShade installed");
    }

    private static void CheckLockedFiles()
    {
        string path = Fixture("locked");
        File.Copy(runtime, Path.Combine(path, "dxgi.dll"));
        File.Copy(runtime, Path.Combine(path, "d3d11.dll"));
        bool failed = false;
        using (var locked = new FileStream(Path.Combine(path, "d3d11.dll"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            try { ReShadeUninstaller.Uninstall(path); }
            catch (IOException) { failed = true; }
        }
        Assert(failed && File.Exists(Path.Combine(path, "dxgi.dll")) && File.Exists(Path.Combine(path, "d3d11.dll")),
            "locked runtime fails without removing any proxy");
    }

    private static void CheckUnrelatedDll()
    {
        string path = Fixture("unrelated");
        File.Copy(typeof(Program).Assembly.Location, Path.Combine(path, "dxgi.dll"));
        string hash = Hash(Path.Combine(path, "dxgi.dll"));
        bool failed = false;
        try { ReShadeUninstaller.Uninstall(path); }
        catch (InvalidOperationException) { failed = true; }
        Assert(failed && Hash(Path.Combine(path, "dxgi.dll")) == hash, "unrecognized dxgi.dll is never removed");
        File.Move(Path.Combine(path, "dxgi.dll"), Path.Combine(path, "d3d11.dll"));
        File.Copy(runtime, Path.Combine(path, "dxgi.dll"));
        Assert(ReShadeUninstaller.Uninstall(path) == 1 && Hash(Path.Combine(path, "d3d11.dll")) == hash,
            "unrelated Direct3D DLL is preserved while ReShade is removed");
    }

    private static void CheckReinstall()
    {
        string path = Fixture("reinstall");
        var files = new[] { "dlss5-feed.addon64", "renodx-dlss5.addon64", "nvngx_dlss.dll", "nvngx_dlssnr.dll",
            "ReShade.ini", "ReShadePreset.ini", "reshade-shaders/Shaders/DLSS5_Feed.fx" };
        foreach (string name in files)
        {
            string file = Path.Combine(path, name);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, "existing custom content");
        }
        File.Copy(runtime, Path.Combine(path, "dxgi.dll"));
        var installer = new DlssInstallerService(path);
        Assert(installer.CheckStatus().IsPresent, "complete installation recognized");
        ReShadeUninstaller.Uninstall(path);
        File.Copy(runtime, Path.Combine(path, "dxgi.dll"));
        Assert(installer.CheckStatus().IsPresent && files.All(name => File.ReadAllText(Path.Combine(path, name)) == "existing custom content"),
            "restoring runtime reuses all existing settings and add-ons");
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject node) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            var child = VisualTreeHelper.GetChild(node, i);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    private static async Task CheckMaintenanceAsync()
    {
        var video = new TestVideo();
        var audio = new TestAudio();
        var vm = new MainViewModel(null!, video, null!, audio, null!, new TestLogger());
        vm.SelectedVideoDevice = new VideoDeviceInfo("test", "Test capture");
        vm.SelectedAudioInputDevice = new AudioDeviceInfo("test-audio", "Test audio", AudioDeviceDirection.Input);
        Task starting = vm.StartVideoAsync(0, 1280, 720);
        Task maintaining = vm.BeginRuntimeMaintenanceAsync();
        Assert(!maintaining.IsCompleted, "runtime maintenance waits for an in-flight preview start");
        video.Ready.SetResult();
        await Task.WhenAll(starting, maintaining);
        Assert(!video.IsRunning, "preview is stopped after its in-flight start finishes");
        int starts = video.Starts;
        await vm.StartVideoAsync(0, 1280, 720);
        await vm.StartAudioAsync();
        Assert(video.Starts == starts && audio.Starts == 0, "preview and audio cannot restart during runtime maintenance");
        vm.EndRuntimeMaintenance();
        await vm.StartVideoAsync(0, 1280, 720);
        Assert(video.IsRunning && video.Starts == starts + 1, "explicit preview relaunch works after maintenance");
        await vm.StopVideoAsync();
    }

    private sealed class TestVideo : IVideoPreviewSession
    {
        public bool IsRunning { get; private set; }
        public int Starts { get; private set; }
        public TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task StartAsync(VideoDeviceInfo device, VideoFormatInfo? format, nint handle, MirrorDeckSettings settings, CancellationToken token = default)
        { Starts++; await Ready.Task; IsRunning = true; }
        public Task StopAsync(CancellationToken token = default) { IsRunning = false; return Task.CompletedTask; }
        public void Resize(int width, int height) { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TestAudio : IAudioPassthroughSession
    {
        public bool IsRunning { get; private set; }
        public int Starts { get; private set; }
        public Task StartAsync(AudioDeviceInfo input, AudioDeviceInfo? output, AudioSettings settings, CancellationToken token = default)
        { Starts++; IsRunning = true; return Task.CompletedTask; }
        public Task StopAsync(CancellationToken token = default) { IsRunning = false; return Task.CompletedTask; }
        public void UpdateSettings(AudioSettings settings) { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TestLogger : IAppLogger
    {
        public string LogPath => "test.log";
        public event EventHandler<AppLogEntryEventArgs>? MessageLogged { add { } remove { } }
        public void Info(string message) { }
        public void Error(string message, Exception? exception = null) { }
    }

    private static void CheckWindow()
    {
        // Keep logging and storage inside the test output; do not connect to a capture device.
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "MirrorDeck.portable"), "");
        var app = new App();
        app.InitializeComponent();
        App.ApplySystemTheme();
        var window = new MainWindow();
        var loaded = typeof(MainWindow).GetMethod("MainWindow_Loaded", BindingFlags.Instance | BindingFlags.NonPublic)!;
        window.Loaded -= (RoutedEventHandler)Delegate.CreateDelegate(typeof(RoutedEventHandler), window, loaded);
        var content = (FrameworkElement)window.Content;
        foreach (var size in new[] { new Size(1120, 780), new Size(800, 540) })
        {
            content.Measure(size);
            content.Arrange(new Rect(size));
            content.UpdateLayout();
            foreach (var icon in Descendants<TextBlock>((Border)window.FindName("TopBar")).Where(text => text.FontFamily.Source.Contains("Segoe Fluent Icons")))
            {
                DependencyObject parent = icon;
                while (parent is not Button) parent = VisualTreeHelper.GetParent(parent);
                var button = (Button)parent;
                Rect bounds = icon.TransformToAncestor(button).TransformBounds(new Rect(icon.RenderSize));
                Assert(new Rect(button.RenderSize).Contains(bounds) && icon.ActualWidth >= 16,
                    $"toolbar icon has unclipped space: {button.ToolTip} at {size.Width}x{size.Height}");
            }
            var tabs = Descendants<TabControl>(content).First();
            tabs.SelectedItem = tabs.Items.Cast<TabItem>().Single(tab => (string)tab.Header == "Advanced");
            content.UpdateLayout();
            Assert(window.FindName("UninstallReShadeButton") == null &&
                !Descendants<TextBlock>(content).Any(text => text.Text.Contains("Install ReShade") || text.Text.Contains("Optional ReShade") || text.Text.Contains("Checking ReShade")),
                $"ReShade installation controls are absent at {size.Width}x{size.Height}");
            Assert(typeof(MainWindow).GetMethod("CheckDlssInstallStatus", BindingFlags.Instance | BindingFlags.NonPublic) == null,
                "startup no longer prompts to install ReShade");
            var image = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
            image.Render(content);
            var png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(image));
            using var file = File.Create(Path.Combine(root, $"advanced-{size.Width}x{size.Height}.png"));
            png.Save(file);
        }
        var navigation = Descendants<TabControl>(content).First();
        navigation.SelectedIndex = 1;
        content.UpdateLayout();
        var filters = (ComboBox)window.FindName("ScalingFilterCombo");
        var strength = (Slider)window.FindName("UpscaleStrengthSlider");
        foreach (var filter in new[] { ScalingFilter.SharpBilinear, ScalingFilter.FsrEasu, ScalingFilter.AnimeLineArt })
        {
            filters.SelectedItem = filter;
            content.UpdateLayout();
            Assert(((StackPanel)strength.Parent).Visibility == Visibility.Visible && strength.ActualHeight > 0,
                filter + " shows its strength control immediately");
            strength.Value = .65;
            Assert(((MainViewModel)window.DataContext).Settings.Scaling.UpscaleStrength == .65,
                filter + " strength binds to saved settings");
        }
        foreach (var size in new[] { new Size(1120, 780), new Size(800, 540) })
        {
            content.Measure(size);
            content.Arrange(new Rect(size));
            content.UpdateLayout();
            var bounds = filters.TransformToAncestor(content).TransformBounds(new Rect(filters.RenderSize));
            Assert(new Rect(size).Contains(bounds), $"scaling filter fits at {size.Width}x{size.Height}");
            var image = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
            image.Render(content);
            var png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(image));
            using var file = File.Create(Path.Combine(root, $"scaling-{size.Width}x{size.Height}.png"));
            png.Save(file);
        }
        filters.SelectedItem = ScalingFilter.Bilinear;
        content.UpdateLayout();
        Assert(((StackPanel)strength.Parent).Visibility == Visibility.Collapsed, "legacy filters hide the inapplicable upscale strength control");
        filters.SelectedItem = ScalingFilter.Dlss45;
        content.UpdateLayout();
        Assert(((MainViewModel)window.DataContext).Settings.Scaling.Filter == ScalingFilter.Dlss45 && ((StackPanel)strength.Parent).Visibility == Visibility.Collapsed, "native DLSS selection persists and hides the inapplicable spatial strength slider");
        var label = new MirrorDeck.App.Interop.OptionLabelConverter().Convert(ScalingFilter.Dlss45, typeof(string), null!, System.Globalization.CultureInfo.InvariantCulture).ToString()!;
        Assert(label.Contains("DLSS 4.5") && label.Contains("experimental"), "native DLSS has an explicit experimental label");
        var target = (ComboBox)window.FindName("DlssOutputResolutionCombo");
        var dlssSharpness = (Slider)window.FindName("DlssSharpnessSlider");
        var modelCombo = (ComboBox)window.FindName("DlssModelCombo");
        var motionCombo = (ComboBox)window.FindName("DlssMotionQualityCombo");
        var protectionSlider = (Slider)window.FindName("DlssDetailProtectionSlider");
        modelCombo.SelectedItem = DlssModel.L;
        motionCombo.SelectedItem = DlssMotionQuality.High;
        protectionSlider.Value = .37;
        var scaling = ((MainViewModel)window.DataContext).Settings.Scaling;
        Assert(scaling.DlssModel == DlssModel.L && scaling.DlssMotionQuality == DlssMotionQuality.High && scaling.DlssDetailProtection == .37,
            "DLSS model, motion and protection controls bind to settings live");
        dlssSharpness.Value = .63;
        Assert(((MainViewModel)window.DataContext).Settings.Scaling.DlssSharpness == .63, "DLSS sharpness slider binds to saved settings live");
        target.SelectedItem = DlssOutputResolution.P4K;
        content.UpdateLayout();
        Assert(((StackPanel)target.Parent).Visibility == Visibility.Visible && ((MainViewModel)window.DataContext).Settings.Scaling.DlssOutputResolution == DlssOutputResolution.P4K,
            "native output resolution is visible and binds to saved settings");
        foreach (var size in new[] { new Size(1120, 780), new Size(800, 540) })
        {
            content.Measure(size);
            content.Arrange(new Rect(size));
            content.UpdateLayout();
            Assert(new Rect(size).Contains(target.TransformToAncestor(content).TransformBounds(new Rect(target.RenderSize))), "native target selector fits at " + size);
            Assert(dlssSharpness.Visibility == Visibility.Visible && dlssSharpness.ActualHeight > 0 && new Rect(size).Contains(dlssSharpness.TransformToAncestor(content).TransformBounds(new Rect(dlssSharpness.RenderSize))), "DLSS sharpness slider fits at " + size);
            var image = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
            image.Render(content);
            var png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(image));
            using var file = File.Create(Path.Combine(root, $"native-dlss-{size.Width}x{size.Height}.png"));
            png.Save(file);
            protectionSlider.BringIntoView();
            content.UpdateLayout();
            Assert(new Rect(size).Contains(protectionSlider.TransformToAncestor(content).TransformBounds(new Rect(protectionSlider.RenderSize))), "DLSS detail protection is reachable by scrolling at " + size);
            var controlsImage = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
            controlsImage.Render(content);
            var controlsPng = new PngBitmapEncoder();
            controlsPng.Frames.Add(BitmapFrame.Create(controlsImage));
            using var controlsFile = File.Create(Path.Combine(root, $"dlss-controls-{size.Width}x{size.Height}.png"));
            controlsPng.Save(controlsFile);
            target.BringIntoView();
            content.UpdateLayout();
        }
        filters.SelectedItem = ScalingFilter.Dlss5;
        content.UpdateLayout();
        var intensity = (Slider)window.FindName("NrIntensity");
        intensity.Value = 1.25;
        Assert(((MainViewModel)window.DataContext).Settings.Scaling.Neural.Intensity == 1.25, "NR intensity binds live");
        Assert(((StackPanel)window.FindName("NeuralControls")).Visibility == Visibility.Visible, "NR controls appear only for DLSS 5");
        foreach (var size in new[] { new Size(1120, 780), new Size(800, 540) })
        {
            content.Measure(size); content.Arrange(new Rect(size)); content.UpdateLayout();
            filters.BringIntoView(); content.UpdateLayout();
            var image = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
            image.Render(content);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image));
            using var file = File.Create(Path.Combine(root, $"native-nr-{size.Width}x{size.Height}.png")); png.Save(file);
            var blend = (Slider)window.FindName("NrBlend");
            blend.BringIntoView(); content.UpdateLayout();
            Assert(new Rect(size).Contains(blend.TransformToAncestor(content).TransformBounds(new Rect(blend.RenderSize))), "NR controls reachable by scrolling at " + size);
        }
        var passesCombo = (ComboBox)window.FindName("NeuralPassesCombo");
        foreach (int passes in new[] { 2, 3, 4, 1 })
        {
            passesCombo.SelectedValue = passes;
            Assert(((MainViewModel)window.DataContext).Settings.Scaling.Neural.Passes == passes, "NR passes bind live: " + passes);
        }
        navigation.SelectedItem = navigation.Items.Cast<TabItem>().Single(tab => (string)tab.Header == "Updates");
        foreach (var size in new[] { new Size(1120, 780), new Size(800, 540) })
        {
            content.Measure(size); content.Arrange(new Rect(size)); content.UpdateLayout();
            var check = Descendants<Button>(content).Single(button => button.Content as string == "Check for updates");
            Assert(new Rect(size).Contains(check.TransformToAncestor(content).TransformBounds(new Rect(check.RenderSize))), "Updates controls fit at " + size);
            Assert(!window.Updates.CanInstall && !window.Updates.CanDownload, "cannot install before a verified update");
            var image = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
            image.Render(content);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image));
            using var file = File.Create(Path.Combine(root, $"updates-{size.Width}x{size.Height}.png")); png.Save(file);
        }
        foreach (bool light in new[] { false, true })
        {
            app.Resources["TextBrush"] = new SolidColorBrush(light ? Colors.Black : Colors.White);
            app.Resources["PopupBrush"] = new SolidColorBrush(light ? Colors.White : Color.FromRgb(36, 36, 36));
            var tip = new ToolTip { Content = "Preview fullscreen", Style = (Style)app.FindResource(typeof(ToolTip)) };
            tip.Measure(new Size(400, 100)); tip.Arrange(new Rect(tip.DesiredSize)); tip.UpdateLayout();
            var text = Descendants<TextBlock>(tip).First();
            Assert(text.Text == "Preview fullscreen" && ((SolidColorBrush)text.Foreground).Color != ((SolidColorBrush)tip.Background).Color,
                "tooltip text has contrasting foreground in " + (light ? "light" : "dark") + " theme");
            var image = new RenderTargetBitmap((int)Math.Ceiling(tip.ActualWidth), (int)Math.Ceiling(tip.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            image.Render(tip);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image));
            using var file = File.Create(Path.Combine(root, "tooltip-" + light + ".png")); png.Save(file);
        }
        var vm = (MainViewModel)window.DataContext;
        vm.SelectedVideoDevice = new VideoDeviceInfo("fixture", "Fixture device");
        typeof(MainWindow).GetField("_previewWasOpened", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, true);
        var schedule = typeof(MainWindow).GetMethod("SchedulePreviewRestart", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var timer = (System.Windows.Threading.DispatcherTimer)typeof(MainWindow).GetField("_videoRestartTimer", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
        schedule.Invoke(window, new object[] { false });
        Assert(!timer.IsEnabled, "settings changes do not reopen a manually closed preview");
        schedule.Invoke(window, new object[] { true });
        Assert(timer.IsEnabled, "explicit open can relaunch a closed preview");
        timer.Stop();
        ((System.Windows.Threading.DispatcherTimer)typeof(MainWindow).GetField("_previewSettingsPublishTimer", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!).Stop();
    }
}
