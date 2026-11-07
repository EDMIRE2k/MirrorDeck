using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using MirrorDeck.App.Interop;
using MirrorDeck.App.Services;
using MirrorDeck.App.ViewModels;
using MirrorDeck.Audio.Wasapi;
using MirrorDeck.Capture.Desktop;
using MirrorDeck.Capture.DirectShow;
using MirrorDeck.Core.Models;
using MirrorDeck.Core.Services;
using MirrorDeck.Rendering.Layout;
using MirrorDeck.Settings.Persistence;

namespace MirrorDeck.App;

public partial class MainWindow : Window, IComponentConnector, IStyleConnector
{
	private readonly MainViewModel _viewModel;

	private readonly IAppLogger _logger;

	private readonly DispatcherTimer _fullscreenChromeTimer;

	private readonly DispatcherTimer _videoRestartTimer;

	private readonly DispatcherTimer _audioRestartTimer;

	private readonly DispatcherTimer _previewSettingsPublishTimer;
    private readonly DispatcherTimer _autoSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(650) };
    private readonly DispatcherTimer _previewStateTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private bool _previewWasOpened, _startingPreview, _pendingPreviewStart;
    private bool _scheduledExplicitOpen;

	private readonly JsonSerializerOptions _previewSettingsJsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
	{
		WriteIndented = true
	};

	private CancellationTokenSource? _formatLoadCts;

	private bool _loaded;

	private bool _isFullscreen;

	private bool _comboDiagnosticsSubscribed;

	private WindowStyle _previousWindowStyle;

	private ResizeMode _previousResizeMode;

	private WindowState _previousWindowState;

	private bool _previousTopmost;

	private Rect _previousBounds;

	private bool _closingAfterShutdown;

	private bool _runtimeMaintenance;

	public MainWindow()
	{
		InitializeComponent();
		FileAppLogger fileAppLogger = (FileAppLogger)(_logger = FileAppLogger.CreateDefault());
		_viewModel = new MainViewModel(new CompositeVideoDeviceEnumerator(new DirectShowVideoDeviceEnumerator(), new DesktopCaptureDeviceEnumerator()), new ExternalPreviewSession(fileAppLogger), new WasapiAudioDeviceEnumerator(), new WasapiAudioPassthroughSession(fileAppLogger), JsonSettingsStore.CreateDefault(), fileAppLogger);
		DataContext = _viewModel;
        _autoSaveTimer.Tick += async (_, _) =>
        {
            _autoSaveTimer.Stop();
            try { await _viewModel.SaveAsync(); }
            catch (Exception ex) { _logger.Error("Autosaving settings failed.", ex); _viewModel.StatusText = "Settings could not be saved: " + ex.Message; }
        };
        _previewStateTimer.Tick += (_, _) => _viewModel.RefreshPreviewState();
        AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler(SettingsEdited), true);
        AddHandler(Selector.SelectionChangedEvent, new SelectionChangedEventHandler((s, e) => { if (e.OriginalSource is ComboBox) SettingsEdited(s, e); }), true);
        AddHandler(RangeBase.ValueChangedEvent, new RoutedPropertyChangedEventHandler<double>(SettingsEdited), true);
        AddHandler(LostFocusEvent, new RoutedEventHandler((s, e) => { if (e.OriginalSource is TextBox) SettingsEdited(s, e); }), true);
		fileAppLogger.MessageLogged += (object? _, AppLogEntryEventArgs args) =>
		{
			Dispatcher.BeginInvoke((Action)(() =>
			{
				_viewModel.AppendDiagnosticLine(args.Line);
			}), DispatcherPriority.Background);
		};
		_fullscreenChromeTimer = new DispatcherTimer
		{
			Interval = TimeSpan.FromSeconds(2.2)
		};
		_fullscreenChromeTimer.Tick += (object? _, EventArgs _) =>
		{
			HideFullscreenChrome();
		};
		_videoRestartTimer = new DispatcherTimer
		{
			Interval = TimeSpan.FromMilliseconds(350.0)
		};
		_videoRestartTimer.Tick += async (object? _, EventArgs _) =>
		{
			_videoRestartTimer.Stop();
            bool explicitOpen = _scheduledExplicitOpen;
            _scheduledExplicitOpen = false;
            if (!explicitOpen && _previewWasOpened && !_viewModel.IsVideoRunning && !_startingPreview) return;
			await StartPreviewFromUiAsync();
		};
		_audioRestartTimer = new DispatcherTimer
		{
			Interval = TimeSpan.FromMilliseconds(300.0)
		};
		_audioRestartTimer.Tick += async (object? _, EventArgs _) =>
		{
			_audioRestartTimer.Stop();
			await StartAudioFromUiAsync();
		};
		_previewSettingsPublishTimer = new DispatcherTimer
		{
			Interval = TimeSpan.FromMilliseconds(80.0)
		};
		_previewSettingsPublishTimer.Tick += (object? _, EventArgs _) =>
		{
			_previewSettingsPublishTimer.Stop();
			PublishPreviewRenderSettings();
		};
		Loaded += MainWindow_Loaded;
	}

	private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
	{
		await _viewModel.InitializeAsync();
		ApplyWindowSettings();
		_loaded = true;
        if (_viewModel.Settings.CheckForUpdatesOnStartup) _ = Updates.CheckAsync();
        _previewStateTimer.Start();
		SubscribeComboDiagnostics(this);
		UpdatePreviewLayout();
		PublishPreviewRenderSettings();
		Dispatcher.InvokeAsync((Func<Task>)(async () =>
		{
			await Task.Delay(250);
			if ((object)_viewModel.SelectedVideoDevice != null && _viewModel.Settings.OpenLastDeviceOnStartup)
			{
				await _viewModel.LoadFormatsForSelectionAsync();
				UpdatePreviewLayout();
				SchedulePreviewRestart();
			}
			else
			{
				_viewModel.StatusText = "Select a capture device to open the preview.";
			}
		}), DispatcherPriority.Background);
	}

	private void Window_SourceInitialized(object sender, EventArgs e)
	{
		Win11WindowStyler.Apply(this, !App.UseLightTheme);
	}

	private async void Window_Closing(object sender, CancelEventArgs e)
	{
		if (_runtimeMaintenance && !_closingAfterShutdown)
		{
			e.Cancel = true;
			return;
		}
		if (!_closingAfterShutdown)
		{
			e.Cancel = true;
			_closingAfterShutdown = true;
			IsEnabled = false;
			Cursor = Cursors.Wait;
			await ShutdownFromWindowAsync();
			Close();
		}
		else
		{
			CaptureWindowSettings();
		}
	}

	private async Task ShutdownFromWindowAsync()
	{
        Updates.Cancel();
		CaptureWindowSettings();
		_formatLoadCts?.Cancel();
		_videoRestartTimer.Stop();
		_audioRestartTimer.Stop();
		_previewSettingsPublishTimer.Stop();

        _autoSaveTimer.Stop();
        _previewStateTimer.Stop();
		await _viewModel.ShutdownAsync();
	}

	private void RefreshDevices_Click(object sender, RoutedEventArgs e)
	{
		RefreshDevicesFromUiAsync();
	}

	private async void RescanDevices_Click(object sender, RoutedEventArgs e)
	{
		_formatLoadCts?.Cancel();
		_videoRestartTimer.Stop();
		await _viewModel.RefreshDevicesAsync();
		await _viewModel.SaveAsync();
	}

	private void SourceTypeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		if (_loaded)
		{
			_logger.Info($"Capture source type changed: {_viewModel.Settings.CaptureSourceType}.");
			_viewModel.ApplyCaptureSourceType();
			_viewModel.SaveAsync();
			LoadFormatsAndRestartPreviewAsync();
		}
	}

	private void VideoDeviceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		if (_loaded && !_viewModel.IsRefreshingDevices)
		{
			_logger.Info("Video device selection changed: " + (_viewModel.SelectedVideoDevice?.Name ?? "none"));
			LoadFormatsAndRestartPreviewAsync();
		}
	}

	private void VideoFormatCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		if (_loaded && !_viewModel.IsLoadingFormats && !_viewModel.IsRefreshingDevices && (object)_viewModel.SelectedVideoFormat != null)
		{
			_logger.Info("Video format selection changed: " + (_viewModel.SelectedVideoFormat?.DisplayName ?? "auto"));
			_viewModel.SaveAsync();
			UpdatePreviewLayout();
			SchedulePreviewRestart();
		}
	}

	private async void AutoFormat_Click(object sender, RoutedEventArgs e)
	{
		_viewModel.SelectAutoLowLatencyFormat();
		UpdatePreviewLayout();
		if (_viewModel.IsVideoRunning)
		{
			SchedulePreviewRestart();
		}
	}

	private async Task StartPreviewFromUiAsync()
	{
		if (_runtimeMaintenance) return;
        if (_startingPreview) { _pendingPreviewStart = true; return; }
		if (_loaded)
		{
            _startingPreview = true;
            try
            {
            do
            {
            _pendingPreviewStart = false;
			_logger.Info("Preview start requested for " + (_viewModel.SelectedVideoDevice?.Name ?? "no device") + ".");
			PublishPreviewRenderSettings();
			UpdatePreviewLayout();
			await _viewModel.StartVideoAsync(IntPtr.Zero, (int)Math.Round(GetPreviewWindowSize().Width), (int)Math.Round(GetPreviewWindowSize().Height));
            _previewWasOpened |= _viewModel.IsVideoRunning;
			if (_viewModel.IsVideoRunning && _viewModel.Settings.Audio.Enabled && (object)_viewModel.SelectedAudioInputDevice != null)
			{
				await StartAudioWithRetryAsync();
			}
            } while (_pendingPreviewStart && !_runtimeMaintenance && !_closingAfterShutdown);
            }
            finally { _startingPreview = false; }
		}
	}

    private void SettingsEdited(object sender, RoutedEventArgs e)
    {
        if (!_loaded || _runtimeMaintenance || _closingAfterShutdown) return;
        _autoSaveTimer.Stop();
        _autoSaveTimer.Start();
    }

    private async void TogglePreview_Click(object sender, RoutedEventArgs e)
    {
        if (_startingPreview) return;
        if (_viewModel.IsVideoRunning)
        {
            _videoRestartTimer.Stop();
            _scheduledExplicitOpen = false;
            _previewWasOpened = true;
            await _viewModel.StopAudioAsync();
            await _viewModel.StopVideoAsync();
        }
        else SchedulePreviewRestart(true);
    }

	private void LayoutSettingChanged(object sender, RoutedEventArgs e)
	{
		if (_loaded)
		{
			UpdatePreviewLayout();
			SchedulePreviewSettingsPublish();
		}
	}

	private void PreviewProcessingSettingChanged(object sender, RoutedEventArgs e)
	{
		if (_loaded)
		{
			_viewModel.SyncLatencyProcessingFlag();
			SchedulePreviewSettingsPublish();
		}
	}

	private void ImageEnhancementSettingChanged(object sender, RoutedEventArgs e)
	{
		if (_loaded)
		{
			_viewModel.SyncLatencyProcessingFlag();
			_viewModel.RefreshSettingsBindings();
			SchedulePreviewSettingsPublish();
		}
	}

	private void PreviewProcessingComboChanged(object sender, SelectionChangedEventArgs e)
	{
		if (_loaded)
		{
			SchedulePreviewSettingsPublish();
		}
	}

	private void PreviewProcessingSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
	{
		if (_loaded)
		{
			SchedulePreviewSettingsPublish();
		}
	}

	private void PreviewLatencyTextBox_LostFocus(object sender, RoutedEventArgs e)
	{
		if (_loaded)
		{
			_viewModel.SyncLatencyProcessingFlag();
			SchedulePreviewSettingsPublish();
		}
	}

	private void ApplyOutputResolution_Click(object sender, RoutedEventArgs e)
	{
		ApplyRequestedOutputResolution();
	}

	private async void ApplyChanges_Click(object sender, RoutedEventArgs e)
	{
		await ApplyChangesFromUiAsync();
	}

	private void WindowSettingChanged(object sender, RoutedEventArgs e)
	{
		if (_loaded)
		{
			SchedulePreviewSettingsPublish();
		}
	}

	private void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
	{
		if (_loaded)
		{
			SchedulePreviewSettingsPublish();
		}
	}

	private void AudioSettingChanged(object sender, RoutedEventArgs e)
	{
		if (_loaded)
		{
			_viewModel.UpdateAudioSettings();
			_viewModel.RefreshSettingsBindings();
			if (_viewModel.Settings.Audio.Enabled && !_viewModel.IsAudioRunning)
			{
				ScheduleAudioRestart();
			}
			else if (!_viewModel.Settings.Audio.Enabled)
			{
				StopAudioFromUiAsync();
			}
		}
	}

	private void AudioSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
	{
		if (_loaded)
		{
			_viewModel.UpdateAudioSettings();
		}
	}

	private void AudioComboChanged(object sender, SelectionChangedEventArgs e)
	{
		if (_loaded)
		{
			_viewModel.UpdateAudioSettings();
			if (_viewModel.Settings.Audio.Enabled)
			{
				ScheduleAudioRestart();
			}
		}
	}

	private void AudioEndpointComboChanged(object sender, SelectionChangedEventArgs e)
	{
		if (_loaded && !_viewModel.IsRefreshingDevices && _viewModel.Settings.Audio.Enabled)
		{
			_logger.Info($"Audio endpoint selection changed: {_viewModel.SelectedAudioInputDevice?.Name ?? "no input"} -> {_viewModel.SelectedAudioOutputDevice?.Name ?? "default output"}.");
			ScheduleAudioRestart();
		}
	}

	private void ToggleMute_Click(object sender, RoutedEventArgs e)
	{
		ToggleMute();
	}

	private void ToggleMute()
	{
		_viewModel.Settings.Audio.Muted = !_viewModel.Settings.Audio.Muted;
		_viewModel.UpdateAudioSettings();
		_viewModel.RefreshSettingsBindings();
		if (!_viewModel.IsAudioRunning && (object)_viewModel.SelectedAudioInputDevice != null)
		{
			ScheduleAudioRestart();
		}
	}

	private void ToggleSettings_Click(object sender, RoutedEventArgs e)
	{
		UpdatePreviewLayout();
		SchedulePreviewSettingsPublish();
	}

	private void ToggleFullscreen_Click(object sender, RoutedEventArgs e)
	{
		ToggleFullscreen();
	}

	private void ToggleFullscreen()
	{
        _viewModel.SendPreviewCommand(PreviewCommand.Fullscreen);
	}

	private void Window_MouseMove(object sender, MouseEventArgs e)
	{
		if (_isFullscreen)
		{
			ShowFullscreenChromeTemporarily();
		}
	}

	private void ShowFullscreenChromeTemporarily()
	{
		if (_isFullscreen)
		{
			TopRow.Height = new GridLength(56.0);
			StatusRow.Height = new GridLength(30.0);
			TopBar.Visibility = Visibility.Visible;
			StatusBar.Visibility = Visibility.Visible;
			_fullscreenChromeTimer.Stop();
			_fullscreenChromeTimer.Start();
		}
	}

	private void HideFullscreenChrome()
	{
		if (_isFullscreen)
		{
			_fullscreenChromeTimer.Stop();
			TopRow.Height = new GridLength(0.0);
			StatusRow.Height = new GridLength(0.0);
			TopBar.Visibility = Visibility.Collapsed;
			StatusBar.Visibility = Visibility.Collapsed;
			UpdatePreviewLayout();
		}
	}

	private void PresetButton_Click(object sender, RoutedEventArgs e)
	{
		if ((sender as FrameworkElement)?.DataContext is PresetDefinition preset)
		{
			_viewModel.ApplyPreset(preset);
			UpdatePreviewLayout();
			SchedulePreviewSettingsPublish();
		}
	}

	private async void SaveConfig_Click(object sender, RoutedEventArgs e)
	{
		CaptureWindowSettings();
		await _viewModel.SaveCurrentConfigAsync();
	}

	private async void RefreshConfigs_Click(object sender, RoutedEventArgs e)
	{
		await _viewModel.RefreshSavedConfigsAsync();
		_viewModel.StatusText = "Configs refreshed";
	}

	private async void LoadConfig_Click(object sender, RoutedEventArgs e)
	{
		if ((sender as FrameworkElement)?.DataContext is SavedConfigInfo config)
		{
			await _viewModel.LoadConfigAsync(config);
			ApplyWindowSettings();
			UpdatePreviewLayout();
			SchedulePreviewRestart();
			if (_viewModel.Settings.Audio.Enabled && (object)_viewModel.SelectedAudioInputDevice != null)
			{
				ScheduleAudioRestart();
			}
		}
	}

	private async void DeleteConfig_Click(object sender, RoutedEventArgs e)
	{
		if ((sender as FrameworkElement)?.DataContext is SavedConfigInfo config)
		{
			await _viewModel.DeleteConfigAsync(config);
		}
	}

	private async void SaveSettings_Click(object sender, RoutedEventArgs e)
	{
		CaptureWindowSettings();
		await _viewModel.SaveAsync();
		_viewModel.StatusText = "Settings saved";
	}

	private void BeginRuntimeOperation()
	{
		_runtimeMaintenance = true;
		IsEnabled = false;
		Cursor = Cursors.Wait;
		_formatLoadCts?.Cancel();
		_videoRestartTimer.Stop();
		_audioRestartTimer.Stop();
		_previewSettingsPublishTimer.Stop();
	}

	private void EndRuntimeOperation()
	{
		_viewModel.EndRuntimeMaintenance();
		_runtimeMaintenance = false;
		IsEnabled = true;
		Cursor = null;
	}

	private void Screenshot_Click(object sender, RoutedEventArgs e)
	{
		SaveScreenshot();
	}

	private async void Window_KeyDown(object sender, KeyEventArgs e)
	{
		if (!(Keyboard.FocusedElement is TextBoxBase))
		{
			if (MatchesHotkey(e, _viewModel.Settings.Hotkeys.Fullscreen))
			{
				ToggleFullscreen();
				e.Handled = true;
			}
			else if (MatchesHotkey(e, _viewModel.Settings.Hotkeys.Mute))
			{
				ToggleMute();
				e.Handled = true;
			}
			else if (MatchesHotkey(e, _viewModel.Settings.Hotkeys.StatsOverlay))
			{
                _viewModel.Settings.CaptureOverlay.ShowFramerate = !_viewModel.Settings.CaptureOverlay.ShowFramerate;
                _viewModel.RefreshSettingsBindings();
                SchedulePreviewSettingsPublish();
				e.Handled = true;
			}
			else if (MatchesHotkey(e, _viewModel.Settings.Hotkeys.AlwaysOnTop))
			{
				_viewModel.Settings.PreviewWindow.AlwaysOnTop = !_viewModel.Settings.PreviewWindow.AlwaysOnTop;
                SchedulePreviewSettingsPublish();
				_viewModel.RefreshSettingsBindings();
				e.Handled = true;
			}
			else if (MatchesHotkey(e, _viewModel.Settings.Hotkeys.Screenshot))
			{
				SaveScreenshot();
				e.Handled = true;
			}
			else if (MatchesHotkey(e, _viewModel.Settings.Hotkeys.RelaunchPreview))
			{
                SchedulePreviewRestart(true);
				e.Handled = true;
			}
		}
	}

	private async Task RefreshDevicesFromUiAsync()
	{
		_logger.Info("Preview relaunch requested.");
		await _viewModel.SaveAsync();
		if ((object)_viewModel.SelectedVideoDevice == null)
		{
			await _viewModel.RefreshDevicesAsync();
		}
		UpdatePreviewLayout();
		SchedulePreviewRestart(true);
	}

	private async void HotkeyTextBox_LostFocus(object sender, RoutedEventArgs e)
	{

        SchedulePreviewSettingsPublish();
		await _viewModel.SaveAsync();
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
		ModifierKeys modifierKeys = ModifierKeys.None;
		for (int i = 0; i < array.Length - 1; i++)
		{
			ModifierKeys modifierKeys2 = modifierKeys;
			ModifierKeys modifierKeys3;
			switch (array[i].ToUpperInvariant())
			{
			case "CTRL":
			case "CONTROL":
				modifierKeys3 = ModifierKeys.Control;
				break;
			case "ALT":
				modifierKeys3 = ModifierKeys.Alt;
				break;
			case "SHIFT":
				modifierKeys3 = ModifierKeys.Shift;
				break;
			case "WIN":
			case "WINDOWS":
				modifierKeys3 = ModifierKeys.Windows;
				break;
			default:
				modifierKeys3 = ModifierKeys.None;
				break;
			}
			modifierKeys = modifierKeys2 | modifierKeys3;
		}
		if (!Enum.TryParse<Key>(array[^1], ignoreCase: true, out var result))
		{
			return false;
		}
		if (((e.Key == Key.System) ? e.SystemKey : e.Key) == result)
		{
			return (Keyboard.Modifiers & (ModifierKeys.Alt | ModifierKeys.Control | ModifierKeys.Shift | ModifierKeys.Windows)) == modifierKeys;
		}
		return false;
	}

	private async Task StartAudioFromUiAsync()
	{
		_logger.Info($"Audio start requested: {_viewModel.SelectedAudioInputDevice?.Name ?? "no input"} -> {_viewModel.SelectedAudioOutputDevice?.Name ?? "default output"}.");
		await _viewModel.StartAudioAsync();
	}

	private async Task StartAudioWithRetryAsync()
	{
		await StartAudioFromUiAsync();
		if (!_viewModel.IsAudioRunning)
		{
			await Task.Delay(650);
			if (_loaded && _viewModel.Settings.Audio.Enabled && (object)_viewModel.SelectedAudioInputDevice != null && !_viewModel.IsAudioRunning)
			{
				_logger.Info("Audio did not report running after preview launch; retrying once.");
				await StartAudioFromUiAsync();
			}
		}
	}

	private async Task StopAudioFromUiAsync()
	{
		_logger.Info("Audio stop requested.");
		await _viewModel.StopAudioAsync();
	}

	private async Task LoadFormatsAndRestartPreviewAsync()
	{
		_formatLoadCts?.Cancel();
		CancellationTokenSource cts = (_formatLoadCts = new CancellationTokenSource());
		try
		{
			await Task.Delay(200, cts.Token);
			await _viewModel.LoadFormatsForSelectionAsync(cts.Token);
			if (!cts.IsCancellationRequested)
			{
				await _viewModel.SaveAsync();
				UpdatePreviewLayout();
				SchedulePreviewRestart();
			}
		}
		catch (OperationCanceledException)
		{
			_logger.Info("Format load canceled because a newer selection arrived.");
		}
	}

	private async Task ApplyChangesFromUiAsync()
	{
		if (!_loaded)
		{
			return;
		}
		if ((object)_viewModel.SelectedVideoDevice == null)
		{
			_viewModel.StatusText = "Select a video device before applying changes.";
			return;
		}
		_logger.Info("Apply Changes requested for " + _viewModel.SelectedVideoDevice.Name + ".");
		_videoRestartTimer.Stop();
		_audioRestartTimer.Stop();
		_previewSettingsPublishTimer.Stop();
		CaptureWindowSettings();
		UpdatePreviewLayout();
		PublishPreviewRenderSettings();
		await _viewModel.SaveAsync();
		if (_viewModel.IsAudioRunning)
		{
			await _viewModel.StopAudioAsync();
		}
		await _viewModel.StopVideoAsync();
		await StartPreviewFromUiAsync();
		if (_viewModel.IsVideoRunning)
		{
			_viewModel.StatusText = "Changes applied to " + _viewModel.SelectedVideoDevice.Name;
		}
	}

	private void SchedulePreviewRestart(bool explicitOpen = false)
	{
		if (_runtimeMaintenance) return;
        SchedulePreviewSettingsPublish();
        if (!explicitOpen && _previewWasOpened && !_viewModel.IsVideoRunning && !_startingPreview) return;
		if ((object)_viewModel.SelectedVideoDevice == null)
		{
			_logger.Info("Preview restart skipped: no selected video device.");
			return;
		}
		_logger.Info("Preview restart scheduled.");
        _scheduledExplicitOpen |= explicitOpen;
		SchedulePreviewSettingsPublish();
		_videoRestartTimer.Stop();
		_videoRestartTimer.Start();
	}

	private void SchedulePreviewSettingsPublish()
	{
        // Throttle, not debounce: dragging a slider must update the live image.
        if (!_previewSettingsPublishTimer.IsEnabled) _previewSettingsPublishTimer.Start();
        SettingsEdited(this, new RoutedEventArgs());
	}

	private void PublishPreviewRenderSettings()
	{
		try
		{
			_viewModel.SyncLatencyProcessingFlag();
			string renderSettingsPath = ExternalPreviewSession.RenderSettingsPath;
			Directory.CreateDirectory(Path.GetDirectoryName(renderSettingsPath));
			PreviewRenderSettings value = PreviewRenderSettings.From(_viewModel.Settings);
            var size = GetPreviewWindowSize();
            value.WindowWidth = (int)Math.Clamp(size.Width, 320, 16384);
            value.WindowHeight = (int)Math.Clamp(size.Height, 240, 16384);
			string text = $"{renderSettingsPath}.{Environment.ProcessId}.tmp";
			File.WriteAllText(text, JsonSerializer.Serialize(value, _previewSettingsJsonOptions));
			File.Move(text, renderSettingsPath, overwrite: true);
			_logger.Info("Published live preview render settings.");
		}
		catch (Exception exception)
		{
			_logger.Error("Publishing live preview render settings failed.", exception);
		}
	}

	private void ScheduleAudioRestart()
	{
		if (_runtimeMaintenance) return;
		if ((object)_viewModel.SelectedAudioInputDevice == null)
		{
			_logger.Info("Audio restart skipped: no selected audio input device.");
			return;
		}
		_viewModel.Settings.Audio.Enabled = true;
		_logger.Info("Audio restart scheduled.");
		_audioRestartTimer.Stop();
		_audioRestartTimer.Start();
	}

	private void OpenLog_Click(object sender, RoutedEventArgs e)
	{
		try
		{
			Process.Start(new ProcessStartInfo
			{
				FileName = _viewModel.LogPath,
				UseShellExecute = true
			});
		}
		catch (Exception exception)
		{
			_logger.Error("Opening log file failed.", exception);
		}
	}

	private void UpdatePreviewLayout()
	{
		DisplaySize previewWindowSize = GetPreviewWindowSize();
		_viewModel.ResizeVideo((int)Math.Round(previewWindowSize.Width), (int)Math.Round(previewWindowSize.Height));
	}

	private DisplaySize GetPreviewWindowSize()
	{
		VideoFormatInfo currentInputFormat = _viewModel.GetCurrentInputFormat();
		DisplaySize displaySize = ViewportCalculator.ResolveOutputSize(((object)currentInputFormat == null) ? new DisplaySize(1920.0, 1080.0) : new DisplaySize(currentInputFormat.Width, currentInputFormat.Height), _viewModel.Settings.Output);
		return new DisplaySize(double.IsFinite(displaySize.Width) ? Math.Clamp(displaySize.Width, 320, 7680) : 1280,
            double.IsFinite(displaySize.Height) ? Math.Clamp(displaySize.Height, 240, 4320) : 720);
	}

	private void ApplyWindowSettings()
	{
		if (!double.IsNaN(_viewModel.Settings.Window.Width) && _viewModel.Settings.Window.Width > 0.0)
		{
			Width = _viewModel.Settings.Window.Width;
		}
		if (!double.IsNaN(_viewModel.Settings.Window.Height) && _viewModel.Settings.Window.Height > 0.0)
		{
			Height = _viewModel.Settings.Window.Height;
		}
		double? left = _viewModel.Settings.Window.Left;
		if (left.HasValue)
		{
			double valueOrDefault = left.GetValueOrDefault();
			Left = valueOrDefault;
		}
		left = _viewModel.Settings.Window.Top;
		if (left.HasValue)
		{
			double valueOrDefault2 = left.GetValueOrDefault();
			Top = valueOrDefault2;
		}
		Topmost = _viewModel.Settings.Window.AlwaysOnTop;
		Opacity = Math.Clamp(_viewModel.Settings.Window.Opacity, 0.35, 1.0);
		WindowStyle = ((!_viewModel.Settings.Window.Borderless) ? WindowStyle.SingleBorderWindow : WindowStyle.None);
		EnsureWindowIntersectsVirtualDesktop();
	}

	private void EnsureWindowIntersectsVirtualDesktop()
	{
		if (!double.IsNaN(Left) && !double.IsNaN(Top) && !(Width <= 0.0) && !(Height <= 0.0))
		{
			Rect rect = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
			if (!new Rect(Left, Top, Width, Height).IntersectsWith(rect))
			{
				Left = rect.Left + Math.Max(0.0, (rect.Width - Width) / 2.0);
				Top = rect.Top + Math.Max(0.0, (rect.Height - Height) / 2.0);
			}
		}
	}

	private void ApplyRequestedOutputResolution()
	{
		if (!_isFullscreen)
		{
			VideoFormatInfo currentInputFormat = _viewModel.GetCurrentInputFormat();
			DisplaySize displaySize = ViewportCalculator.ResolveOutputSize(((object)currentInputFormat == null) ? new DisplaySize(1920.0, 1080.0) : new DisplaySize(currentInputFormat.Width, currentInputFormat.Height), _viewModel.Settings.Output);
			UpdatePreviewLayout();
			SchedulePreviewSettingsPublish();
			_viewModel.StatusText = $"Preview window size applied for {displaySize.Width:0}x{displaySize.Height:0}";
		}
	}

	private void CaptureWindowSettings()
	{
		if (_isFullscreen)
		{
			_viewModel.Settings.Window.Left = _previousBounds.Left;
			_viewModel.Settings.Window.Top = _previousBounds.Top;
			_viewModel.Settings.Window.Width = _previousBounds.Width;
			_viewModel.Settings.Window.Height = _previousBounds.Height;
		}
		else
		{
			Rect rect = ((WindowState == WindowState.Normal) ? new Rect(Left, Top, Width, Height) : RestoreBounds);
			_viewModel.Settings.Window.Left = rect.Left;
			_viewModel.Settings.Window.Top = rect.Top;
			_viewModel.Settings.Window.Width = rect.Width;
			_viewModel.Settings.Window.Height = rect.Height;
		}
		_viewModel.Settings.Window.AlwaysOnTop = Topmost && !_isFullscreen;
		_viewModel.Settings.Window.Opacity = Opacity;
		_viewModel.Settings.Window.Borderless = WindowStyle == WindowStyle.None && !_isFullscreen;
		_viewModel.Settings.Window.SettingsPanelOpen = true;
	}

	private void SubscribeComboDiagnostics(DependencyObject root)
	{
		if (!_comboDiagnosticsSubscribed)
		{
			_comboDiagnosticsSubscribed = true;
			SubscribeComboDiagnosticsRecursive(root);
		}
	}

	private void SubscribeComboDiagnosticsRecursive(DependencyObject parent)
	{
		int childrenCount = VisualTreeHelper.GetChildrenCount(parent);
		for (int i = 0; i < childrenCount; i++)
		{
			DependencyObject child = VisualTreeHelper.GetChild(parent, i);
			if (child is ComboBox comboBox)
			{
				comboBox.DropDownOpened += ComboBox_DropDownOpened;
				comboBox.DropDownClosed += ComboBox_DropDownClosed;
			}
			SubscribeComboDiagnosticsRecursive(child);
		}
	}

	private void ComboBox_DropDownOpened(object? sender, EventArgs e)
	{
		if (sender is ComboBox combo)
		{
			_logger.Info("Dropdown opened: " + DescribeCombo(combo) + ".");
		}
	}

	private void ComboBox_DropDownClosed(object? sender, EventArgs e)
	{
		if (sender is ComboBox combo)
		{
			_logger.Info("Dropdown closed: " + DescribeCombo(combo) + ".");
		}
	}

	private static string DescribeCombo(ComboBox combo)
	{
		if (!string.IsNullOrWhiteSpace(combo.Name))
		{
			return combo.Name;
		}
		return combo.ToolTip?.ToString() ?? combo.SelectedItem?.ToString() ?? "unnamed combo";
	}

	private void SaveScreenshot()
	{
        _viewModel.SendPreviewCommand(PreviewCommand.Screenshot);
	}
}
