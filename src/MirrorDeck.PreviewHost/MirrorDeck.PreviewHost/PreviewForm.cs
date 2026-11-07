using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using DirectShowLib;
using MirrorDeck.Capture.DirectShow;
using MirrorDeck.Core.Models;

namespace MirrorDeck.PreviewHost;

internal sealed partial class PreviewForm : Form
{
	private readonly PreviewHostOptions _options;

	private readonly PreviewHostLogger _logger;

	private readonly FramePanel _previewPanel = new FramePanel();

	private readonly ReShadeTipPanel _reshadeTipPanel = new ReShadeTipPanel();

	private readonly Label _fpsOverlayLabel = new Label();

	private readonly System.Windows.Forms.Timer _renderTimer;

	private readonly System.Windows.Forms.Timer _desktopCaptureTimer;

	private readonly System.Windows.Forms.Timer _reshadeTipTimer;

	private readonly System.Windows.Forms.Timer _parentWatchTimer;

	private readonly object _frameLock = new object();

	private readonly object _settingsLock = new object();

	private readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);

	private IFilterGraph2? _graph;

	private ICaptureGraphBuilder2? _builder;

	private IMediaControl? _mediaControl;

	private IBaseFilter? _source;

	private IBaseFilter? _sampleGrabberFilter;

	private IBaseFilter? _nullRenderer;

	private ISampleGrabber? _sampleGrabber;

	private SampleGrabberFrameCallback? _sampleCallback;

	private FileSystemWatcher? _settingsWatcher;
    private readonly System.Windows.Forms.Timer _settingsPoll = new() { Interval = 100 };
    private int _settingsDirty;
    private string? _lastSettingsJson;
    private int _settingsPollTicks;

	private IPreviewRenderer? _gpuRenderer;

	private IVideoWindow? _videoWindow;

	private WindowsGraphicsCaptureMirror? _graphicsCapture;

	private DesktopMirrorCapture? _desktopCapture;

	private PreviewRenderSettings _renderSettings = new PreviewRenderSettings();

	private Rectangle _previousBounds;

	private FormBorderStyle _previousBorderStyle;

	private FormWindowState _previousWindowState;

	private bool _previousTopMost;

	public PreviewForm(PreviewHostOptions options, PreviewHostLogger logger)
	{
		_options = options;
		_logger = logger;
		Text = "MirrorDeck Preview";
		using (Stream stream = typeof(PreviewForm).Assembly.GetManifestResourceStream("MirrorDeck.PreviewHost.Resources.MirrorDeck.PreviewHost.ico"))
		{
			if (stream != null)
			{
				Icon = new Icon(stream);
			}
		}
		BackColor = Color.Black;
		ForeColor = Color.White;
        var work = Screen.FromPoint(Cursor.Position).WorkingArea;
        var initial = PreviewWindowSizing.Fit(options.Width, options.Height, work.Width, work.Height, true);
		ClientSize = new Size(initial.Width, initial.Height);
		MinimumSize = new Size(180, 160);
		StartPosition = FormStartPosition.CenterScreen;
		TopMost = options.AlwaysOnTop;
		KeyPreview = true;
		_previewPanel.BackColor = Color.Black;
		_previewPanel.Dock = DockStyle.Fill;
		Controls.Add(_previewPanel);
		Activated += (object? _, EventArgs _) =>
		{
			_previewPanel.Focus();
		};
		_previewPanel.MouseDown += (object? _, MouseEventArgs _) =>
		{
			_previewPanel.Focus();
		};
		_fpsOverlayLabel.AutoSize = false;
		_fpsOverlayLabel.BackColor = Color.FromArgb(190, 0, 0, 0);
		_fpsOverlayLabel.ForeColor = Color.White;
		_fpsOverlayLabel.TextAlign = ContentAlignment.MiddleCenter;
		_fpsOverlayLabel.Visible = false;
		Controls.Add(_fpsOverlayLabel);
		_reshadeTipPanel.CloseRequested += (object? _, EventArgs _) =>
		{
			HideReShadeTip();
		};
		Controls.Add(_reshadeTipPanel);
		_renderTimer = new System.Windows.Forms.Timer
		{
			Interval = 1000
		};
		_renderTimer.Tick += (object? _, EventArgs _) =>
		{
			LogTelemetryIfNeeded();
		};
		_desktopCaptureTimer = new System.Windows.Forms.Timer
		{
			Interval = 1
		};
		_desktopCaptureTimer.Tick += (object? _, EventArgs _) =>
		{
			CaptureDesktopFrame();
		};
		_reshadeTipTimer = new System.Windows.Forms.Timer
		{
			Interval = 10000
		};
		_reshadeTipTimer.Tick += (object? _, EventArgs _) =>
		{
			HideReShadeTip();
		};
		_parentWatchTimer = new System.Windows.Forms.Timer
		{
			Interval = 1000
		};
		_parentWatchTimer.Tick += (object? _, EventArgs _) =>
		{
			CloseIfParentExited();
		};
		Shown += (object? _, EventArgs _) =>
		{
			StartPreview();
            FitLaunchWindow();
            if (options.StartFullscreen) ToggleFullscreen();
		};
        FormClosing += async (object? _, FormClosingEventArgs e) =>
        {
            if (_closeAllowed) return;
            e.Cancel = true;
            if (_closePending) return;
            _closePending = true;
            await StopPreviewAsync();
            _closeAllowed = true;
            Close();
        };
        Resize += (_, _) => PublishRenderSurface();
        _previewPanel.HandleCreated += (_, _) => PublishRenderSurface();
        _previewPanel.HandleDestroyed += (_, _) =>
        {
            Volatile.Write(ref _surface, new RenderSurface(Size.Empty, false, 0));
            _frameReady.Set();
        };
		KeyDown += OnKeyDown;
		DoubleClick += (object? _, EventArgs _) =>
		{
			ToggleFullscreen();
		};
		_previewPanel.DoubleClick += (object? _, EventArgs _) =>
		{
			ToggleFullscreen();
		};
		_previewPanel.Resize += (object? _, EventArgs _) =>
		{
			ApplyPreviewPanelResize();
			LayoutFpsOverlay();
			LayoutReShadeTip();
		};
	}

	private void StartPreview()
	{
		try
		{
			_previewPanel.Controls.Clear();
			LoadRenderSettings();
			StartSettingsWatcher();
			StartParentWatcher();
			ShowReShadeTip();
			if (string.IsNullOrWhiteSpace(_options.DeviceId))
			{
				ShowStatus("No video device selected.");
				_logger.Info("No device id was provided.");
				return;
			}
			if (DesktopCaptureSourceIds.IsDesktopSource(_options.DeviceId))
			{
				StartDesktopMirror(_options.DeviceId);
				return;
			}
			_logger.Info($"Starting processed preview. device={_options.DeviceId}, format={_options.FormatIndex?.ToString() ?? "auto"}, size={ClientSize.Width}x{ClientSize.Height}");
			DsDevice dsDevice = FindVideoDevice(_options.DeviceId) ?? throw new InvalidOperationException("Selected video device was not found.");
			_graph = (IFilterGraph2)new FilterGraph();
			_builder = (ICaptureGraphBuilder2)new CaptureGraphBuilder2();
			DsError.ThrowExceptionForHR(_builder.SetFiltergraph(_graph));
			_source = CreateFilter(dsDevice);
			DsError.ThrowExceptionForHR(_graph.AddFilter(_source, dsDevice.Name));
			int? formatIndex = _options.FormatIndex;
			if (formatIndex.HasValue)
			{
				int valueOrDefault = formatIndex.GetValueOrDefault();
				TryApplyFormat(_source, valueOrDefault);
			}
			if (GetRenderSettingsSnapshot().DisableProcessing)
			{
				_logger.Info("MirrorDeck image enhancements are disabled; keeping the D3D renderer active so live setting changes do not rebuild the preview.");
			}
			_sampleGrabberFilter = (IBaseFilter)new SampleGrabber();
			_sampleGrabber = (ISampleGrabber)_sampleGrabberFilter;
			AMMediaType mediaType = new AMMediaType
			{
				majorType = MediaType.Video,
				subType = MediaSubType.RGB32,
				formatType = FormatType.VideoInfo
			};
			DsError.ThrowExceptionForHR(_sampleGrabber.SetMediaType(mediaType));
			DsUtils.FreeAMMediaType(mediaType);
			DsError.ThrowExceptionForHR(_graph.AddFilter(_sampleGrabberFilter, "MirrorDeck Frame Processor"));
			_nullRenderer = (IBaseFilter)new NullRenderer();
			DsError.ThrowExceptionForHR(_graph.AddFilter(_nullRenderer, "MirrorDeck Frame Sink"));
			_sampleCallback = new SampleGrabberFrameCallback(OnFrameAvailable);
			DsError.ThrowExceptionForHR(_sampleGrabber.SetBufferSamples(BufferThem: false));
			DsError.ThrowExceptionForHR(_sampleGrabber.SetOneShot(OneShot: false));
			DsError.ThrowExceptionForHR(_sampleGrabber.SetCallback(_sampleCallback, 1));
			int num = _builder.RenderStream(PinCategory.Capture, MediaType.Video, _source, _sampleGrabberFilter, _nullRenderer);
			if (num < 0)
			{
				num = _builder.RenderStream(PinCategory.Preview, MediaType.Video, _source, _sampleGrabberFilter, _nullRenderer);
			}
			DsError.ThrowExceptionForHR(num);
			ReadConnectedMediaType();
			_mediaControl = (IMediaControl)_graph;
			DsError.ThrowExceptionForHR(_mediaControl.Run());
			EnableHighResolutionTimer();
			StartRenderWorker();
			_logger.Info($"Processed preview started for {dsDevice.Name}. Frames={_frameWidth}x{_frameHeight}, stride={_frameStride}, bottomUp={_frameBottomUp}.");
		}
		catch (Exception ex)
		{
			_logger.Error("Processed preview failed to start.", ex);
			ShowStatus(ex.Message);
		}
	}

	private void StartDesktopMirror(string sourceId)
	{
		try
		{
			_graphicsCapture = WindowsGraphicsCaptureMirror.Create(sourceId, _options.CaptureCursor, QueueGraphicsCaptureFrame, _logger.Info);
			_frameWidth = _graphicsCapture.Width;
			_frameHeight = _graphicsCapture.Height;
			_frameStride = _frameWidth * 4;
			_frameBottomUp = false;
			EnableHighResolutionTimer();
			StartRenderWorker();
			_graphicsCapture.Start();
			_rendererMode = "Windows Graphics Capture";
			_logger.Info($"Windows Graphics Capture started: {sourceId}, source={_frameWidth}x{_frameHeight}.");
		}
		catch (Exception exception)
		{
			_logger.Error("Windows Graphics Capture failed; falling back to compatibility desktop capture.", exception);
			_graphicsCapture?.Dispose();
			_graphicsCapture = null;
			_desktopCapture = DesktopMirrorCapture.Create(sourceId, _options.CaptureCursor);
			ClientSize = new Size(_desktopCapture.Bounds.Width, _desktopCapture.Bounds.Height);
			_frameWidth = _desktopCapture.Bounds.Width;
			_frameHeight = _desktopCapture.Bounds.Height;
			_frameStride = _frameWidth * 4;
			_frameBottomUp = false;
			EnableHighResolutionTimer();
			StartRenderWorker();
			StartCompatibilityCaptureWorker();
			_rendererMode = "desktop mirror compatibility";
			_logger.Info($"Compatibility desktop mirror started: {sourceId}, source={_frameWidth}x{_frameHeight}.");
		}
	}

	private void QueueGraphicsCaptureFrame(byte[] pixels, int width, int height, int stride, double timestamp)
	{
		QueueFrame(pixels, checked(stride * height), width, height, stride, false, timestamp);
	}

	private void CaptureDesktopFrame()
	{
		if (_desktopCapture == null || _capturingDesktopFrame)
		{
			return;
		}
		try
		{
			if (!_stopping)
			{
				_capturingDesktopFrame = true;
				_desktopCapture.RefreshBounds();
				int length = _desktopCapture.Bounds.Width * _desktopCapture.Bounds.Height * 4;
				byte[] array = RentFrameBuffer(length);
				DesktopMirrorFrame desktopMirrorFrame = _desktopCapture.CaptureFrame(array);
				if ((object)desktopMirrorFrame == null)
				{
					ReturnRenderFrame(array);
				}
				else
				{
					QueueFrame(desktopMirrorFrame.Pixels, desktopMirrorFrame.Pixels.Length, desktopMirrorFrame.Width, desktopMirrorFrame.Height, desktopMirrorFrame.Stride, bottomUp: false);
				}
			}
		}
		catch (Exception ex)
		{
			_logger.Error("Desktop mirror frame capture failed.", ex);
            _desktopCaptureFailed = true;
            PostUi(() => ShowStatus(ex.Message));
		}
		finally
		{
			_capturingDesktopFrame = false;
		}
	}

	private void StartDirectShowPassthrough(DsDevice device)
	{
		if (_builder == null || _graph == null || _source == null)
		{
			throw new InvalidOperationException("The DirectShow graph was not initialized.");
		}
		int num = _builder.RenderStream(PinCategory.Preview, MediaType.Video, _source, null, null);
		if (num < 0)
		{
			num = _builder.RenderStream(PinCategory.Capture, MediaType.Video, _source, null, null);
		}
		DsError.ThrowExceptionForHR(num);
		_videoWindow = (IVideoWindow)_graph;
		_videoWindow.put_Owner(_previewPanel.Handle);
		_videoWindow.put_MessageDrain(Handle);
		_videoWindow.put_WindowStyle(WindowStyle.Child | WindowStyle.ClipSiblings | WindowStyle.ClipChildren);
		_videoWindow.SetWindowPosition(0, 0, Math.Max(1, _previewPanel.Width), Math.Max(1, _previewPanel.Height));
		_videoWindow.put_Visible(OABool.True);
		_mediaControl = (IMediaControl)_graph;
		DsError.ThrowExceptionForHR(_mediaControl.Run());
		_previewPanel.SetGpuActive();
		_rendererMode = "DirectShow passthrough";
		_logger.Info("DirectShow passthrough preview started for " + device.Name + ". MirrorDeck image enhancements are disabled for the lowest-latency path.");
	}

	private async Task StopPreviewAsync()
    {
        _stopping = true;
        _renderStop.Cancel();
        _frameReady.Set();
        _renderTimer.Stop();
		_desktopCaptureTimer.Stop();
		_reshadeTipTimer.Stop();
		_parentWatchTimer.Stop();
		DisableHighResolutionTimer();
		_settingsWatcher?.Dispose();
		_settingsWatcher = null;
        _settingsPoll.Stop();
        _settingsPoll.Dispose();
		try
		{
			_sampleGrabber?.SetCallback(null, 0);
		}
		catch (Exception exception)
		{
			_logger.Error("Sample grabber callback release failed.", exception);
		}
		try
		{
			_mediaControl?.Stop();
		}
		catch (Exception exception2)
		{
			_logger.Error("Media control stop failed.", exception2);
		}
		try
		{
			if (_videoWindow != null)
			{
				_videoWindow.put_Visible(OABool.False);
				_videoWindow.put_MessageDrain(IntPtr.Zero);
				_videoWindow.put_Owner(IntPtr.Zero);
			}
		}
		catch (Exception exception3)
		{
			_logger.Error("DirectShow passthrough window release failed.", exception3);
		}
		Release(_sampleGrabberFilter);
		Release(_nullRenderer);
		Release(_source);
		Release(_videoWindow);
		Release(_mediaControl);
		Release(_builder);
		Release(_graph);
		_graphicsCapture?.Dispose();
        if (_desktopWorker != null &&
            await Task.WhenAny(_desktopStopped.Task, Task.Delay(8000)) != _desktopStopped.Task)
        {
            _logger.Error("Compatibility capture did not stop in eight seconds; terminating isolated preview host.");
            Environment.Exit(1);
            return;
        }
		_desktopCapture?.Dispose();
		_graphicsCapture = null;
		_desktopCapture = null;
		_rendererMode = "stopped";
		_sampleGrabber = null;
		_sampleGrabberFilter = null;
		_sampleCallback = null;
		_nullRenderer = null;
		_source = null;
		_videoWindow = null;
		_mediaControl = null;
		_builder = null;
		_graph = null;
        if (_renderThread != null)
        {
            // Keep the HWND thread pumping while DXGI/ReShade finish their work.
            if (await Task.WhenAny(_renderStopped.Task, Task.Delay(8000)) != _renderStopped.Task)
            {
                _logger.Error("Render worker did not stop in eight seconds; terminating isolated preview host.");
                Environment.Exit(1);
                return;
            }
        }
        lock (_frameLock) _frames.Dispose();
        Interlocked.Exchange(ref _pendingBitmap, null)?.Dispose();
		_previewPanel.ClearFrame();
		_logger.Info("Processed preview stopped.");
	}

	private void ReadConnectedMediaType()
	{
		if (_sampleGrabber == null)
		{
			throw new InvalidOperationException("Sample grabber was not initialized.");
		}
		AMMediaType aMMediaType = new AMMediaType();
		try
		{
			DsError.ThrowExceptionForHR(_sampleGrabber.GetConnectedMediaType(aMMediaType));
			if (aMMediaType.formatPtr == IntPtr.Zero || aMMediaType.formatType != FormatType.VideoInfo)
			{
				throw new InvalidOperationException("The capture graph did not expose a VideoInfo RGB32 format.");
			}
			VideoInfoHeader videoInfoHeader = ((VideoInfoHeader)Marshal.PtrToStructure(aMMediaType.formatPtr, typeof(VideoInfoHeader))) ?? throw new InvalidOperationException("The capture graph exposed an empty VideoInfo format.");
            if (videoInfoHeader.AvgTimePerFrame > 0)
                _sourceFps = 10000000.0 / videoInfoHeader.AvgTimePerFrame;
			_frameWidth = Math.Abs(videoInfoHeader.BmiHeader.Width);
			_frameHeight = Math.Abs(videoInfoHeader.BmiHeader.Height);
			_frameStride = Math.Abs((_frameWidth * videoInfoHeader.BmiHeader.BitCount + 31) / 32 * 4);
			_frameBottomUp = videoInfoHeader.BmiHeader.Height > 0;
			if (videoInfoHeader.BmiHeader.BitCount != 32 || _frameWidth <= 0 || _frameHeight <= 0)
			{
				throw new InvalidOperationException($"Unsupported processed preview format: {videoInfoHeader.BmiHeader.Width}x{videoInfoHeader.BmiHeader.Height}, {videoInfoHeader.BmiHeader.BitCount}-bit.");
			}
		}
		finally
		{
			DsUtils.FreeAMMediaType(aMMediaType);
		}
	}

    private PreviewRenderSettings GetRenderSettingsSnapshot()
    {
        // Objects are replaced, never edited, after publication.
        lock (_settingsLock) return _renderSettings;
    }

	private void LoadRenderSettings()
	{
		if (string.IsNullOrWhiteSpace(_options.SettingsPath) || !File.Exists(_options.SettingsPath))
		{
			return;
		}
		try
		{
            using var stream = File.Open(_options.SettingsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            string json = reader.ReadToEnd();
            if (json == _lastSettingsJson) return;
            var previewRenderSettings = JsonSerializer.Deserialize<PreviewRenderSettings>(json, _jsonOptions);
			if (previewRenderSettings != null)
			{
                ApplyLiveWindowSettings(previewRenderSettings);
				lock (_settingsLock)
				{
					_renderSettings = previewRenderSettings;
				}
                Interlocked.Increment(ref _settingsRevision);
                _frameReady.Set();
				_logger.Info($"Loaded live render settings: filter={previewRenderSettings.Scaling.Filter}, sharp={previewRenderSettings.Scaling.Sharpness:0.00}, clean={previewRenderSettings.Enhancement.CleanUpscalingEnabled}, enhancementsDisabled={previewRenderSettings.DisableProcessing}, fpsCap={previewRenderSettings.FpsCap}.");
				UpdateFpsOverlay(previewRenderSettings, null);
                _lastSettingsJson = json;
			}
		}
		catch (Exception exception)
		{
			_logger.Error("Loading live render settings failed.", exception);
            Interlocked.Exchange(ref _settingsDirty, 1);
		}
	}

	private void StartSettingsWatcher()
	{
		if (string.IsNullOrWhiteSpace(_options.SettingsPath))
		{
			return;
		}
		try
		{
			string directoryName = Path.GetDirectoryName(_options.SettingsPath);
			string fileName = Path.GetFileName(_options.SettingsPath);
			if (!string.IsNullOrWhiteSpace(directoryName) && !string.IsNullOrWhiteSpace(fileName))
			{
				Directory.CreateDirectory(directoryName);
				_settingsWatcher = new FileSystemWatcher(directoryName, fileName)
				{
					NotifyFilter = (NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite | NotifyFilters.CreationTime)
				};
				_settingsWatcher.Changed += (object _, FileSystemEventArgs _) =>
				{
                    Interlocked.Exchange(ref _settingsDirty, 1);
				};
				_settingsWatcher.Created += (object _, FileSystemEventArgs _) =>
				{
                    Interlocked.Exchange(ref _settingsDirty, 1);
				};
				_settingsWatcher.Renamed += (object _, RenamedEventArgs _) =>
				{
                    Interlocked.Exchange(ref _settingsDirty, 1);
				};
				_settingsWatcher.EnableRaisingEvents = true;
                _settingsWatcher.Error += (_, _) => Interlocked.Exchange(ref _settingsDirty, 1);
                _settingsPoll.Tick += (_, _) =>
                {
                    if (Interlocked.Exchange(ref _settingsDirty, 0) != 0 || ++_settingsPollTicks >= 10)
                    {
                        _settingsPollTicks = 0;
                        LoadRenderSettings();
                    }
                };
                _settingsPoll.Start();
			}
		}
		catch (Exception exception)
		{
			_logger.Error("Starting live render settings watcher failed.", exception);
		}
	}

    private void ApplyLiveWindowSettings(PreviewRenderSettings next, bool force = false)
    {
        var before = GetRenderSettingsSnapshot();
        if (!_fullscreen)
        {
            TopMost = next.Window.AlwaysOnTop;
            var border = next.Window.Borderless ? FormBorderStyle.None : FormBorderStyle.Sizable;
            if (FormBorderStyle != border) FormBorderStyle = border;
            if (next.WindowWidth >= 320 && next.WindowHeight >= 240 && (force || next.WindowWidth != before.WindowWidth || next.WindowHeight != before.WindowHeight))
            {
                var work = Screen.FromHandle(Handle).WorkingArea;
                var fitted = PreviewWindowSizing.Fit(next.WindowWidth, next.WindowHeight, work.Width, work.Height, _lastSettingsJson == null);
                ClientSize = new Size(fitted.Width, fitted.Height);
            }
        }
        Opacity = double.IsFinite(next.Window.Opacity) ? Math.Clamp(next.Window.Opacity, .35, 1) : 1;
        _graphicsCapture?.SetCaptureCursor(next.CaptureOverlay.CaptureMousePointer);
        _desktopCapture?.SetCaptureCursor(next.CaptureOverlay.CaptureMousePointer);
    }

    private void FitLaunchWindow()
    {
        var work = Screen.FromHandle(Handle).WorkingArea;
        var settings = GetRenderSettingsSnapshot();
        var fitted = PreviewWindowSizing.Fit(settings.WindowWidth > 0 ? settings.WindowWidth : _options.Width,
            settings.WindowHeight > 0 ? settings.WindowHeight : _options.Height, work.Width, work.Height, true);
        ClientSize = new Size(fitted.Width, fitted.Height);
        Location = new Point(work.Left + (work.Width - Width) / 2, work.Top + (work.Height - Height) / 2);
        ApplyLiveWindowSettings(settings);
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == 0x804d && !_closePending)
        {
            if ((PreviewCommand)(int)message.WParam == PreviewCommand.Fullscreen) ToggleFullscreen();
            if ((PreviewCommand)(int)message.WParam == PreviewCommand.Screenshot) SaveVisiblePreview();
            return;
        }
        base.WndProc(ref message);
    }

    private void SaveVisiblePreview()
    {
        try
        {
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "MirrorDeck");
            Directory.CreateDirectory(folder);
            var bounds = _previewPanel.RectangleToScreen(_previewPanel.ClientRectangle);
            using var bitmap = new Bitmap(bounds.Width, bounds.Height);
            using (var graphics = Graphics.FromImage(bitmap)) graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
            bitmap.Save(Path.Combine(folder, DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".png"), System.Drawing.Imaging.ImageFormat.Png);
            _logger.Info("Preview screenshot saved to " + folder);
        }
        catch (Exception ex) { _logger.Error("Saving preview screenshot failed.", ex); }
    }

	private void OnKeyDown(object? sender, KeyEventArgs e)
	{
		if (MatchesHotkey(e, GetRenderSettingsSnapshot().Hotkeys.Fullscreen))
		{
			ToggleFullscreen();
			e.Handled = true;
		}
		else if (e.KeyCode == Keys.Escape && _fullscreen)
		{
			ToggleFullscreen();
			e.Handled = true;
		}
	}

	private static bool MatchesHotkey(KeyEventArgs e, string? hotkey)
	{
		if (string.IsNullOrWhiteSpace(hotkey))
		{
			return false;
		}
		string[] array = hotkey.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		if (array.Length == 0)
		{
			return false;
		}
		Keys keys = Keys.None;
		for (int i = 0; i < array.Length - 1; i++)
		{
			Keys keys2 = keys;
			Keys keys3;
			switch (array[i].ToUpperInvariant())
			{
			case "CTRL":
			case "CONTROL":
				keys3 = Keys.Control;
				break;
			case "ALT":
				keys3 = Keys.Alt;
				break;
			case "SHIFT":
				keys3 = Keys.Shift;
				break;
			default:
				keys3 = Keys.None;
				break;
			}
			keys = keys2 | keys3;
		}
		if (!Enum.TryParse<Keys>(array[^1], ignoreCase: true, out var result))
		{
			return false;
		}
		if (e.KeyCode == result)
		{
			return (e.Modifiers & (Keys.Shift | Keys.Control | Keys.Alt)) == keys;
		}
		return false;
	}

	private void StartParentWatcher()
	{
		if (_options.ParentProcessId.HasValue)
		{
			_parentWatchTimer.Start();
		}
	}

	private void CloseIfParentExited()
	{
		int? parentProcessId = _options.ParentProcessId;
		if (!parentProcessId.HasValue)
		{
			return;
		}
		int valueOrDefault = parentProcessId.GetValueOrDefault();
		try
		{
			using Process process = Process.GetProcessById(valueOrDefault);
			if (process.HasExited)
			{
				Close();
			}
		}
		catch
		{
			Close();
		}
	}

	private void ShowReShadeTip()
	{
        if (!ReShadeBootstrapper.LocalProxyLoaded) return;
		LayoutReShadeTip();
		_reshadeTipPanel.Visible = true;
		_reshadeTipPanel.BringToFront();
		_reshadeTipTimer.Stop();
		_reshadeTipTimer.Start();
	}

	private void HideReShadeTip()
	{
		_reshadeTipTimer.Stop();
		_reshadeTipPanel.Visible = false;
		if (ContainsFocus)
		{
			_previewPanel.Focus();
		}
	}

	private void LayoutReShadeTip()
	{
		int num = Math.Min(560, Math.Max(320, ClientSize.Width - 40));
		int height = 118;
		_reshadeTipPanel.Bounds = new Rectangle(Math.Max(20, ClientSize.Width - num - 20), 20, num, height);
	}

	private void UpdateFpsOverlay(PreviewRenderSettings settings, double? fps)
	{
		if (!settings.CaptureOverlay.ShowFramerate)
		{
			_fpsOverlayLabel.Visible = false;
			return;
		}
		if (fps.HasValue)
		{
			_fpsOverlayLabel.Text = $"{Math.Max(0.0, fps.Value):0} fps";
		}
		else if (string.IsNullOrWhiteSpace(_fpsOverlayLabel.Text))
		{
			_fpsOverlayLabel.Text = "-- fps";
		}
		double num = Math.Clamp(settings.CaptureOverlay.FramerateScale, 0.75, 2.0);
		_fpsOverlayLabel.Font = new Font("Segoe UI Semibold", (float)(13.0 * num), FontStyle.Bold, GraphicsUnit.Pixel);
		LayoutFpsOverlay(settings.CaptureOverlay);
		_fpsOverlayLabel.Visible = true;
		_fpsOverlayLabel.BringToFront();
		_reshadeTipPanel.BringToFront();
	}

	private void LayoutFpsOverlay()
	{
		LayoutFpsOverlay(GetRenderSettingsSnapshot().CaptureOverlay);
	}

	private void LayoutFpsOverlay(CaptureOverlaySettings settings)
	{
		double num = Math.Clamp(settings.FramerateScale, 0.75, 2.0);
		int num2 = (int)Math.Round(78.0 * num);
		int num3 = (int)Math.Round(30.0 * num);
		int num4 = (int)Math.Round(12.0 * num);
		Rectangle bounds = _previewPanel.Bounds;
		OverlayCorner framerateCorner = settings.FramerateCorner;
		bool flag = ((framerateCorner == OverlayCorner.TopRight || framerateCorner == OverlayCorner.BottomRight) ? true : false);
		int val = (flag ? (bounds.Right - num2 - num4) : (bounds.Left + num4));
		framerateCorner = settings.FramerateCorner;
		flag = (uint)(framerateCorner - 2) <= 1u;
		int val2 = (flag ? (bounds.Bottom - num3 - num4) : (bounds.Top + num4));
		_fpsOverlayLabel.Bounds = new Rectangle(Math.Max(bounds.Left, val), Math.Max(bounds.Top, val2), num2, num3);
	}

	private void ApplyPreviewPanelResize()
	{
		PublishRenderSurface();
		try
		{
			_videoWindow?.SetWindowPosition(0, 0, Math.Max(1, _previewPanel.Width), Math.Max(1, _previewPanel.Height));
		}
		catch (Exception exception)
		{
			_logger.Error("Resizing DirectShow passthrough preview failed.", exception);
		}
	}

	private void EnableHighResolutionTimer()
	{
		if (!_highResolutionTimerEnabled && timeBeginPeriod(1u) == 0)
		{
			_highResolutionTimerEnabled = true;
			_logger.Info("High-resolution render timer enabled.");
		}
	}

	private void DisableHighResolutionTimer()
	{
		if (_highResolutionTimerEnabled)
		{
			timeEndPeriod(1u);
			_highResolutionTimerEnabled = false;
		}
	}

	private void ToggleFullscreen()
	{
		if (!_fullscreen)
		{
			_previousBounds = Bounds;
			_previousBorderStyle = FormBorderStyle;
			_previousWindowState = WindowState;
			_previousTopMost = TopMost;
			SuspendLayout();
			WindowState = FormWindowState.Normal;
			FormBorderStyle = FormBorderStyle.None;
			TopMost = true;
			Bounds = Screen.FromHandle(Handle).Bounds;
			ResumeLayout();
			BringToFront();
			Activate();
			_fullscreen = true;
			ApplyPreviewPanelResize();
		}
		else
		{
			SuspendLayout();
			WindowState = FormWindowState.Normal;
			FormBorderStyle = _previousBorderStyle;
			TopMost = _previousTopMost;
			Bounds = _previousBounds;
			WindowState = _previousWindowState;
			ResumeLayout();
			_fullscreen = false;
            ApplyLiveWindowSettings(GetRenderSettingsSnapshot());
			Activate();
			ApplyPreviewPanelResize();
		}
	}

	private void ShowStatus(string message)
	{
		_previewPanel.Controls.Clear();
		_previewPanel.Controls.Add(new Label
		{
			AutoSize = false,
			Dock = DockStyle.Fill,
			ForeColor = Color.White,
			BackColor = Color.Black,
			TextAlign = ContentAlignment.MiddleCenter,
			Text = message
		});
	}

	private static DsDevice? FindVideoDevice(string id)
	{
		return DsDevice.GetDevicesOfCat(FilterCategory.VideoInputDevice).FirstOrDefault((DsDevice device) => string.Equals(device.DevicePath, id, StringComparison.OrdinalIgnoreCase) || string.Equals(device.Name, id, StringComparison.OrdinalIgnoreCase));
	}

	private static IBaseFilter CreateFilter(DsDevice device)
	{
		Guid riidResult = typeof(IBaseFilter).GUID;
		device.Mon.BindToObject(null, null, ref riidResult, out object ppvResult);
		return (IBaseFilter)ppvResult;
	}

	private void TryApplyFormat(IBaseFilter source, int formatIndex)
	{
		IPin pin = null;
		AMMediaType ppmt = null;
		nint num = IntPtr.Zero;
		try
		{
			pin = DsFindPin.ByCategory(source, PinCategory.Capture, 0) ?? DsFindPin.ByCategory(source, PinCategory.Preview, 0) ?? DsFindPin.ByDirection(source, PinDirection.Output, 0);
			if (pin is IAMStreamConfig iAMStreamConfig)
			{
				DsError.ThrowExceptionForHR(iAMStreamConfig.GetNumberOfCapabilities(out var piCount, out var piSize));
				if (formatIndex >= 0 && formatIndex < piCount)
				{
					num = Marshal.AllocCoTaskMem(piSize);
					DsError.ThrowExceptionForHR(iAMStreamConfig.GetStreamCaps(formatIndex, out ppmt, num));
					DirectShowFrameRate.Apply(ppmt, _options.FramesPerSecond);
					DsError.ThrowExceptionForHR(iAMStreamConfig.SetFormat(ppmt));
					_logger.Info($"Applied DirectShow format index {formatIndex}, requested fps={_options.FramesPerSecond:0.###}.");
				}
			}
		}
		catch (Exception innerException)
		{
			throw new InvalidOperationException("The capture driver rejected this input format. Choose another resolution, frame rate, or pixel format.", innerException);
		}
		finally
		{
			if (ppmt != null)
			{
				DsUtils.FreeAMMediaType(ppmt);
			}
			if (num != IntPtr.Zero)
			{
				Marshal.FreeCoTaskMem(num);
			}
			Release(pin);
		}
	}

	private static void Release(object? comObject)
	{
		if (comObject != null && Marshal.IsComObject(comObject))
		{
			Marshal.ReleaseComObject(comObject);
		}
	}

	[DllImport("winmm.dll")]
	private static extern uint timeBeginPeriod(uint uPeriod);

	[DllImport("winmm.dll")]
	private static extern uint timeEndPeriod(uint uPeriod);
}
