using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel.__Internals;
using MirrorDeck.Core.Models;
using MirrorDeck.Core.Services;
using MirrorDeck.Rendering;

namespace MirrorDeck.App.ViewModels;

public sealed class MainViewModel : ObservableObject
{
	private static readonly TimeSpan DeviceEnumerationTimeout = TimeSpan.FromSeconds(4.0);

	private static readonly TimeSpan FormatEnumerationTimeout = TimeSpan.FromSeconds(5.0);

	private static readonly TimeSpan StreamStartTimeout = TimeSpan.FromSeconds(5.0);

	private static readonly JsonSerializerOptions ConfigJsonOptions = new JsonSerializerOptions
	{
		WriteIndented = true,
		Converters = { (JsonConverter)new JsonStringEnumConverter() }
	};

	private readonly IVideoDeviceEnumerator _videoEnumerator;

	private readonly IVideoPreviewSession _videoPreview;

	private readonly IAudioDeviceEnumerator _audioEnumerator;

	private readonly IAudioPassthroughSession _audioPassthrough;

	private readonly ISettingsStore _settingsStore;

	private readonly IAppLogger _logger;

	private readonly SemaphoreSlim _videoStartGate = new SemaphoreSlim(1, 1);

	private readonly SemaphoreSlim _audioStartGate = new SemaphoreSlim(1, 1);

	private bool _runtimeMaintenance;

	public async Task BeginRuntimeMaintenanceAsync()
	{
		_runtimeMaintenance = true;
		bool videoHeld = false;
		bool audioHeld = false;
		try
		{
			videoHeld = await _videoStartGate.WaitAsync(TimeSpan.FromSeconds(15));
			if (!videoHeld)
				throw new TimeoutException("The preview is still starting. Try again when it has finished.");
			audioHeld = await _audioStartGate.WaitAsync(TimeSpan.FromSeconds(15));
			if (!audioHeld)
				throw new TimeoutException("Audio is still starting. Try again when it has finished.");
			await _audioPassthrough.StopAsync();
			await _videoPreview.StopAsync();
			if (_videoPreview.IsRunning)
				throw new InvalidOperationException("Close the preview before changing ReShade.");
			OnPropertyChanged(nameof(IsVideoRunning));
			OnPropertyChanged(nameof(IsAudioRunning));
		}
		finally
		{
			if (audioHeld) _audioStartGate.Release();
			if (videoHeld) _videoStartGate.Release();
		}
	}

	public void EndRuntimeMaintenance() => _runtimeMaintenance = false;

	private IReadOnlyList<VideoDeviceInfo> _allVideoDevices = Array.Empty<VideoDeviceInfo>();

	private int _formatLoadVersion;

	private string? _formatsDeviceId;

	[ObservableProperty]
	private MirrorDeckSettings settings = new MirrorDeckSettings();

	[ObservableProperty]
	private VideoDeviceInfo? selectedVideoDevice;

	[ObservableProperty]
	private VideoFormatInfo? selectedVideoFormat;

	[ObservableProperty]
	private AudioDeviceInfo? selectedAudioInputDevice;

	[ObservableProperty]
	private AudioDeviceInfo? selectedAudioOutputDevice;

	[ObservableProperty]
	private string statusText = "Ready";

	[ObservableProperty]
	private bool settingsPanelOpen = true;

	[ObservableProperty]
	private bool statsOverlayVisible = true;

	[ObservableProperty]
	private bool isBusy;

	[ObservableProperty]
	private string diagnosticText = "Diagnostics will appear here when devices, menus, audio, or preview operations run.";

	[ObservableProperty]
	private string configName = "My config";

	[ObservableProperty]
	private string dlssInstallStatusText = "Checking ReShade/DLSS 5...";

	[ObservableProperty]
	private bool dlssInstallStatusOk;

	public bool IsLoadingFormats { get; private set; }

	public bool IsRefreshingDevices { get; private set; }

	public ObservableCollection<VideoDeviceInfo> VideoDevices { get; } = new ObservableCollection<VideoDeviceInfo>();

	public ObservableCollection<VideoFormatInfo> VideoFormats { get; } = new ObservableCollection<VideoFormatInfo>();

	public IReadOnlyList<ProcessingResolutionOption> ProcessingResolutions { get; } = new _003C_003Ez__ReadOnlyArray<ProcessingResolutionOption>(new ProcessingResolutionOption[5]
	{
		new ProcessingResolutionOption("Native window resolution", 0),
		new ProcessingResolutionOption("720p", 720),
		new ProcessingResolutionOption("1080p", 1080),
		new ProcessingResolutionOption("1440p", 1440),
		new ProcessingResolutionOption("2160p / 4K", 2160)
	});

	public ObservableCollection<AudioDeviceInfo> AudioInputDevices { get; } = new ObservableCollection<AudioDeviceInfo>();

	public ObservableCollection<AudioDeviceInfo> AudioOutputDevices { get; } = new ObservableCollection<AudioDeviceInfo>();

	public ObservableCollection<SavedConfigInfo> SavedConfigs { get; } = new ObservableCollection<SavedConfigInfo>();

	public IReadOnlyList<CaptureSourceTypeOption> CaptureSourceTypes { get; } = new _003C_003Ez__ReadOnlyArray<CaptureSourceTypeOption>(new CaptureSourceTypeOption[3]
	{
		new CaptureSourceTypeOption("Video Devices", CaptureSourceType.VideoDevices),
		new CaptureSourceTypeOption("Desktop", CaptureSourceType.Desktop),
		new CaptureSourceTypeOption("Window", CaptureSourceType.Window)
	});

	public IReadOnlyList<OutputResolutionMode> OutputResolutionModes { get; } = Enum.GetValues<OutputResolutionMode>();

	public IReadOnlyList<AspectMode> AspectModes { get; } = Enum.GetValues<AspectMode>();

    public PreviewGpuInfo Gpu { get; }
    public IReadOnlyList<ScalingFilter> ScalingFilters => PreviewGpu.Filters(Gpu.SupportsDlss);
    public IReadOnlyList<NeuralStyle> NeuralStyles { get; } = Enum.GetValues<NeuralStyle>();
    public IReadOnlyList<NeuralPreset> NeuralPresets { get; } = Enum.GetValues<NeuralPreset>();
    public IReadOnlyList<int> NeuralWorkHeights { get; } = new[] { 360, 540, 720, 1080, 1440, 2160 };
    public sealed record NeuralPassOption(string Name, int Count);
    public IReadOnlyList<NeuralPassOption> NeuralPassOptions { get; } = new[] { new NeuralPassOption("Off - single pass", 1), new("2x", 2), new("3x", 3), new("4x", 4) };

    public IReadOnlyList<DlssOutputResolution> DlssOutputResolutions { get; } = Enum.GetValues<DlssOutputResolution>();
    public IReadOnlyList<DlssModel> DlssModels { get; } = Enum.GetValues<DlssModel>();
    public IReadOnlyList<DlssMotionQuality> DlssMotionQualities { get; } = Enum.GetValues<DlssMotionQuality>();

	public IReadOnlyList<RgbRangeMode> RgbRangeModes { get; } = Enum.GetValues<RgbRangeMode>();

	public IReadOnlyList<AudioChannelMode> AudioChannelModes { get; } = Enum.GetValues<AudioChannelMode>();

	public IReadOnlyList<int> FrameBufferOptions { get; } = new _003C_003Ez__ReadOnlyArray<int>(new int[3] { 0, 1, 2 });

	public IReadOnlyList<OverlayCorner> OverlayCorners { get; } = Enum.GetValues<OverlayCorner>();

	public IReadOnlyList<PresetDefinition> Presets => PresetCatalog.All;

	public bool IsVideoRunning => _videoPreview.IsRunning;
    public void RefreshPreviewState() => OnPropertyChanged(nameof(IsVideoRunning));

	public bool IsAudioRunning => _audioPassthrough.IsRunning;

	public string SettingsPath => _settingsStore.SettingsPath;

	public string LogPath => _logger.LogPath;

	public string ConfigsPath => Path.Combine(Path.GetDirectoryName(SettingsPath) ?? AppContext.BaseDirectory, "configs");

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public MirrorDeckSettings Settings
	{
		get
		{
			return settings;
		}
		[MemberNotNull("settings")]
		set
		{

            value.Scaling ??= new();
            value.Scaling.Neural ??= new();
            value.PreviewWindow ??= new();
            value.Scaling.Filter = PreviewGpu.Validate(value.Scaling.Filter, Gpu.SupportsDlss);

			if (!EqualityComparer<MirrorDeckSettings>.Default.Equals(settings, value))
			{
				OnPropertyChanging(__KnownINotifyPropertyChangingArgs.Settings);
				settings = value;
				OnPropertyChanged(__KnownINotifyPropertyChangedArgs.Settings);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public VideoDeviceInfo? SelectedVideoDevice
	{
		get
		{
			return selectedVideoDevice;
		}
		set
		{
			if (!EqualityComparer<VideoDeviceInfo>.Default.Equals(selectedVideoDevice, value))
			{
				OnPropertyChanging(__KnownINotifyPropertyChangingArgs.SelectedVideoDevice);
				selectedVideoDevice = value;
				OnPropertyChanged(__KnownINotifyPropertyChangedArgs.SelectedVideoDevice);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public VideoFormatInfo? SelectedVideoFormat
	{
		get
		{
			return selectedVideoFormat;
		}
		set
		{
			if (!EqualityComparer<VideoFormatInfo>.Default.Equals(selectedVideoFormat, value))
			{
				OnPropertyChanging(__KnownINotifyPropertyChangingArgs.SelectedVideoFormat);
				selectedVideoFormat = value;
				OnPropertyChanged(__KnownINotifyPropertyChangedArgs.SelectedVideoFormat);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public AudioDeviceInfo? SelectedAudioInputDevice
	{
		get
		{
			return selectedAudioInputDevice;
		}
		set
		{
			if (!EqualityComparer<AudioDeviceInfo>.Default.Equals(selectedAudioInputDevice, value))
			{
				OnPropertyChanging(__KnownINotifyPropertyChangingArgs.SelectedAudioInputDevice);
				selectedAudioInputDevice = value;
				OnPropertyChanged(__KnownINotifyPropertyChangedArgs.SelectedAudioInputDevice);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public AudioDeviceInfo? SelectedAudioOutputDevice
	{
		get
		{
			return selectedAudioOutputDevice;
		}
		set
		{
			if (!EqualityComparer<AudioDeviceInfo>.Default.Equals(selectedAudioOutputDevice, value))
			{
				OnPropertyChanging(__KnownINotifyPropertyChangingArgs.SelectedAudioOutputDevice);
				selectedAudioOutputDevice = value;
				OnPropertyChanged(__KnownINotifyPropertyChangedArgs.SelectedAudioOutputDevice);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public string StatusText
	{
		get
		{
			return statusText;
		}
		[MemberNotNull("statusText")]
		set
		{
			if (!EqualityComparer<string>.Default.Equals(statusText, value))
			{
				OnPropertyChanging(__KnownINotifyPropertyChangingArgs.StatusText);
				statusText = value;
				OnPropertyChanged(__KnownINotifyPropertyChangedArgs.StatusText);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public bool SettingsPanelOpen
	{
		get
		{
			return settingsPanelOpen;
		}
		set
		{
			if (!EqualityComparer<bool>.Default.Equals(settingsPanelOpen, value))
			{
				OnPropertyChanging(__KnownINotifyPropertyChangingArgs.SettingsPanelOpen);
				settingsPanelOpen = value;
				OnPropertyChanged(__KnownINotifyPropertyChangedArgs.SettingsPanelOpen);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public bool StatsOverlayVisible
	{
		get
		{
			return statsOverlayVisible;
		}
		set
		{
			if (!EqualityComparer<bool>.Default.Equals(statsOverlayVisible, value))
			{
				OnPropertyChanging(__KnownINotifyPropertyChangingArgs.StatsOverlayVisible);
				statsOverlayVisible = value;
				OnPropertyChanged(__KnownINotifyPropertyChangedArgs.StatsOverlayVisible);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public bool IsBusy
	{
		get
		{
			return isBusy;
		}
		set
		{
			if (!EqualityComparer<bool>.Default.Equals(isBusy, value))
			{
				OnPropertyChanging(__KnownINotifyPropertyChangingArgs.IsBusy);
				isBusy = value;
				OnPropertyChanged(__KnownINotifyPropertyChangedArgs.IsBusy);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public string DiagnosticText
	{
		get
		{
			return diagnosticText;
		}
		[MemberNotNull("diagnosticText")]
		set
		{
			if (!EqualityComparer<string>.Default.Equals(diagnosticText, value))
			{
				OnPropertyChanging(__KnownINotifyPropertyChangingArgs.DiagnosticText);
				diagnosticText = value;
				OnPropertyChanged(__KnownINotifyPropertyChangedArgs.DiagnosticText);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public string ConfigName
	{
		get
		{
			return configName;
		}
		[MemberNotNull("configName")]
		set
		{
			if (!EqualityComparer<string>.Default.Equals(configName, value))
			{
				OnPropertyChanging(__KnownINotifyPropertyChangingArgs.ConfigName);
				configName = value;
				OnPropertyChanged(__KnownINotifyPropertyChangedArgs.ConfigName);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public string DlssInstallStatusText
	{
		get
		{
			return dlssInstallStatusText;
		}
		[MemberNotNull("dlssInstallStatusText")]
		set
		{
			if (!EqualityComparer<string>.Default.Equals(dlssInstallStatusText, value))
			{
				OnPropertyChanging(__KnownINotifyPropertyChangingArgs.DlssInstallStatusText);
				dlssInstallStatusText = value;
				OnPropertyChanged(__KnownINotifyPropertyChangedArgs.DlssInstallStatusText);
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public bool DlssInstallStatusOk
	{
		get
		{
			return dlssInstallStatusOk;
		}
		set
		{
			if (!EqualityComparer<bool>.Default.Equals(dlssInstallStatusOk, value))
			{
				OnPropertyChanging(__KnownINotifyPropertyChangingArgs.DlssInstallStatusOk);
				dlssInstallStatusOk = value;
				OnPropertyChanged(__KnownINotifyPropertyChangedArgs.DlssInstallStatusOk);
			}
		}
	}

	public MainViewModel(IVideoDeviceEnumerator videoEnumerator, IVideoPreviewSession videoPreview, IAudioDeviceEnumerator audioEnumerator, IAudioPassthroughSession audioPassthrough, ISettingsStore settingsStore, IAppLogger logger, PreviewGpuInfo? gpu = null)
	{
        Gpu = gpu ?? PreviewGpu.Detect();
		_videoEnumerator = videoEnumerator;
		_videoPreview = videoPreview;
		_audioEnumerator = audioEnumerator;
		_audioPassthrough = audioPassthrough;
		_settingsStore = settingsStore;
		_logger = logger;
	}

	public void AppendDiagnosticLine(string line)
	{
		IEnumerable<string> values = DiagnosticText.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).Concat(new _003C_003Ez__ReadOnlySingleElementList<string>(line)).TakeLast(220);
		DiagnosticText = string.Join(Environment.NewLine, values);
	}

	public async Task InitializeAsync(CancellationToken cancellationToken = default(CancellationToken))
	{
		IsBusy = true;
		try
		{
			_logger.Info("Initializing MirrorDeck view model.");
			Settings = await _settingsStore.LoadAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext: true);
			NormalizeSettings(syncEnhancementFromLegacyFlag: true);
			Settings.Audio.Enabled = true;
			SettingsPanelOpen = Settings.Window.SettingsPanelOpen;
			if (Settings.StartMuted)
			{
				Settings.Audio.Muted = true;
			}
			await RefreshDevicesAsync(loadFormats: false, cancellationToken).ConfigureAwait(continueOnCapturedContext: true);
			await RefreshSavedConfigsAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext: true);
			StatusText = (((object)SelectedVideoDevice == null) ? "Devices loaded. Select a capture device to open the preview." : "Devices loaded. Preview and audio will start automatically.");
			_logger.Info("Initialization completed.");
		}
		catch (Exception ex)
		{
			_logger.Error("MirrorDeck initialization failed.", ex);
			StatusText = ex.Message;
		}
		finally
		{
			IsBusy = false;
		}
	}

	public async Task RefreshDevicesAsync(CancellationToken cancellationToken = default(CancellationToken))
	{
		await RefreshDevicesAsync(loadFormats: true, cancellationToken).ConfigureAwait(continueOnCapturedContext: true);
	}

	public async Task RefreshDevicesAsync(bool loadFormats, CancellationToken cancellationToken = default(CancellationToken))
	{
		if (IsRefreshingDevices)
		{
			return;
		}
		if ((object)SelectedVideoDevice != null)
		{
			RememberSelections();
		}
		IsRefreshingDevices = true;
		try
		{
			StatusText = "Refreshing devices...";
			_logger.Info($"Refreshing devices. loadFormats={loadFormats}");
			Task<IReadOnlyList<VideoDeviceInfo>> task = TryWithTimeout(_videoEnumerator.GetVideoDevicesAsync(cancellationToken), DeviceEnumerationTimeout, "video device enumeration", Array.Empty<VideoDeviceInfo>());
			Task<IReadOnlyList<AudioDeviceInfo>> audioInputsTask = TryWithTimeout(_audioEnumerator.GetInputDevicesAsync(cancellationToken), DeviceEnumerationTimeout, "audio input enumeration", Array.Empty<AudioDeviceInfo>());
			Task<IReadOnlyList<AudioDeviceInfo>> audioOutputsTask = TryWithTimeout(_audioEnumerator.GetOutputDevicesAsync(cancellationToken), DeviceEnumerationTimeout, "audio output enumeration", Array.Empty<AudioDeviceInfo>());
			IReadOnlyList<VideoDeviceInfo> videos = await task.ConfigureAwait(continueOnCapturedContext: true);
			IReadOnlyList<AudioDeviceInfo> audioInputs = await audioInputsTask.ConfigureAwait(continueOnCapturedContext: true);
			IReadOnlyList<AudioDeviceInfo> items = await audioOutputsTask.ConfigureAwait(continueOnCapturedContext: true);
			_allVideoDevices = videos;
			ApplyVideoDeviceFilter();
			Replace(AudioInputDevices, audioInputs);
			Replace(AudioOutputDevices, items);
			ApplyDeviceSelections();
			_logger.Info($"Selected audio path: {SelectedAudioInputDevice?.Name ?? "none"} -> {SelectedAudioOutputDevice?.Name ?? "default"}.");
			if (loadFormats)
			{
				await LoadFormatsForSelectionAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext: true);
			}
		}
		finally
		{
			IsRefreshingDevices = false;
		}
	}

	public async Task LoadFormatsForSelectionAsync(CancellationToken cancellationToken = default(CancellationToken))
	{
		int version = ++_formatLoadVersion;
		VideoDeviceInfo device = SelectedVideoDevice;
		int? savedIndex = ((Settings.SelectedVideoDeviceId == device?.Id) ? Settings.SelectedVideoFormatIndex : ((int?)null));
		MirrorDeckSettings mirrorDeckSettings = Settings;
		if (mirrorDeckSettings.VideoFormatPreferences == null)
		{
			mirrorDeckSettings.VideoFormatPreferences = new Dictionary<string, VideoFormatInfo>();
		}
		VideoFormatInfo preference = (((object)device == null) ? null : Settings.VideoFormatPreferences.GetValueOrDefault(device.Id));
		IsLoadingFormats = true;
		try
		{
			VideoFormats.Clear();
			SelectedVideoFormat = null;
			_formatsDeviceId = null;
			if ((object)device == null)
			{
				return;
			}
			StatusText = "Loading formats for " + device.Name + "...";
			_logger.Info("Loading video formats for " + device.Name + ".");
			IReadOnlyList<VideoFormatInfo> readOnlyList = await TryWithTimeout(_videoEnumerator.GetFormatsAsync(device, cancellationToken), FormatEnumerationTimeout, "format query for " + device.Name, Array.Empty<VideoFormatInfo>()).ConfigureAwait(continueOnCapturedContext: true);
			cancellationToken.ThrowIfCancellationRequested();
			if (version != _formatLoadVersion || device.Id != SelectedVideoDevice?.Id)
			{
				return;
			}
			foreach (VideoFormatInfo item in readOnlyList)
			{
				VideoFormats.Add(item);
			}
			_formatsDeviceId = device.Id;
			MainViewModel mainViewModel = this;
			object obj;
			if ((object)preference == null)
			{
				if (savedIndex.HasValue)
				{
					int index = savedIndex.GetValueOrDefault();
					obj = VideoFormats.FirstOrDefault((VideoFormatInfo format) => format.Index == index);
				}
				else
				{
					obj = null;
				}
			}
			else
			{
				obj = VideoFormats.FirstOrDefault((VideoFormatInfo format) => format.MatchesMode(preference));
			}
			if (obj == null)
			{
				obj = SelectAutoFormatCandidate();
			}
			mainViewModel.SelectedVideoFormat = (VideoFormatInfo?)obj;
			StatusText = ((VideoFormats.Count == 0) ? "Format query unavailable; Start will use the device default mode." : $"Loaded {VideoFormats.Count} formats.");
			_logger.Info($"Video format load completed: {VideoFormats.Count} formats.");
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception ex2)
		{
			_logger.Error("Loading video formats failed.", ex2);
			StatusText = "Format query failed: " + ex2.Message;
		}
		finally
		{
			if (version == _formatLoadVersion)
			{
				IsLoadingFormats = false;
			}
		}
	}

	public async Task StartVideoAsync(nint previewHandle, int width, int height, CancellationToken cancellationToken = default(CancellationToken))
	{
		if (_runtimeMaintenance) return;
		if ((object)SelectedVideoDevice == null)
		{
			StatusText = "Select a video device first";
			return;
		}
        await _videoStartGate.WaitAsync(cancellationToken);
		IsBusy = true;
		bool releaseGate = true;
		try
		{
			_videoPreview.Resize(width, height);
            if (_runtimeMaintenance || SelectedVideoDevice == null) return;
			Task startTask = _videoPreview.StartAsync(SelectedVideoDevice, SelectedVideoFormat, previewHandle, Settings, cancellationToken);
			bool flag = await TryWithTimeout(startTask, StreamStartTimeout, "video preview start for " + SelectedVideoDevice.Name).ConfigureAwait(continueOnCapturedContext: true);
			if (!startTask.IsCompleted)
			{
				releaseGate = false;
				ReleaseGateWhenCompleteAsync(startTask, _videoStartGate, "video start");
			}
			if (flag)
			{
				StatusText = "Previewing " + SelectedVideoDevice.Name;
				OnPropertyChanged("IsVideoRunning");
				RememberSelections();
			}
			else
			{
				StatusText = "Video start timed out. Try another device or format.";
			}
		}
		catch (Exception ex)
		{
			_logger.Error("Starting video preview failed.", ex);
			StatusText = "Video failed: " + ex.Message;
		}
		finally
		{
			IsBusy = false;
			if (releaseGate)
			{
				_videoStartGate.Release();
			}
		}
	}

	public async Task StopVideoAsync(CancellationToken cancellationToken = default(CancellationToken))
	{
		try
		{
			await _videoPreview.StopAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext: true);
			StatusText = "Video stopped";
			OnPropertyChanged("IsVideoRunning");
		}
		catch (Exception ex)
		{
			_logger.Error("Stopping video preview failed.", ex);
			StatusText = "Stop failed: " + ex.Message;
		}
	}

    public void SendPreviewCommand(PreviewCommand command) => _videoPreview.SendCommand(command);

	public async Task StartAudioAsync(CancellationToken cancellationToken = default(CancellationToken))
	{
		if (_runtimeMaintenance) return;
		Settings.Audio.Enabled = true;
		if ((object)SelectedAudioInputDevice == null)
		{
			StatusText = "Select an audio input first";
			return;
		}
		if (!_audioStartGate.Wait(0))
		{
			StatusText = "Audio operation already in progress.";
			_logger.Info("Skipped audio start because another audio operation is already in progress.");
			return;
		}
		bool releaseGate = true;
		try
		{
			Task startTask = _audioPassthrough.StartAsync(SelectedAudioInputDevice, SelectedAudioOutputDevice, Settings.Audio, cancellationToken);
			bool flag = await TryWithTimeout(startTask, StreamStartTimeout, "audio passthrough start for " + SelectedAudioInputDevice.Name).ConfigureAwait(continueOnCapturedContext: true);
			if (!startTask.IsCompleted)
			{
				releaseGate = false;
				ReleaseGateWhenCompleteAsync(startTask, _audioStartGate, "audio start");
			}
			if (flag)
			{
				StatusText = "Audio passthrough: " + SelectedAudioInputDevice.Name;
				OnPropertyChanged("IsAudioRunning");
				RememberSelections();
			}
			else
			{
				StatusText = "Audio start timed out. Try another input or output device.";
			}
		}
		catch (Exception ex)
		{
			_logger.Error("Starting audio passthrough failed.", ex);
			StatusText = "Audio failed: " + ex.Message;
		}
		finally
		{
			if (releaseGate)
			{
				_audioStartGate.Release();
			}
		}
	}

	public async Task StopAudioAsync(CancellationToken cancellationToken = default(CancellationToken))
	{
		try
		{
			await _audioPassthrough.StopAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext: true);
			StatusText = "Audio stopped";
			OnPropertyChanged("IsAudioRunning");
		}
		catch (Exception ex)
		{
			_logger.Error("Stopping audio passthrough failed.", ex);
			StatusText = "Audio stop failed: " + ex.Message;
		}
	}

	public void UpdateAudioSettings()
	{
		_audioPassthrough.UpdateSettings(Settings.Audio);
	}

	public void ResizeVideo(int width, int height)
	{
		_videoPreview.Resize(width, height);
	}

	public void RefreshSettingsBindings()
	{
		OnPropertyChanged("Settings");
	}

	public void SyncLatencyProcessingFlag()
	{
		Settings.Latency.DisableProcessing = !Settings.Enhancement.Enabled;
	}

	public void ApplyPreset(PresetDefinition preset)
	{
		Settings.Scaling.Filter = preset.ScalingFilter;
		Settings.Scaling.IntegerScaling = preset.IntegerScaling;
		Settings.Scaling.Sharpness = preset.Sharpness;
		Settings.Output.AspectMode = preset.AspectMode;
		Settings.Latency.FrameBufferCount = preset.FrameBufferCount;
		Settings.Latency.VSyncEnabled = preset.VSyncEnabled;
		Settings.Latency.DisableProcessing = preset.DisableProcessing;
		Settings.Enhancement.Enabled = !preset.DisableProcessing;
		Settings.Latency.DropLateFrames = preset.FrameBufferCount == 0;
		Settings.Audio.DelayMilliseconds = preset.AudioDelayMilliseconds;
		Settings.Latency.LowLatencyMode = preset.Name.Contains("Latency", StringComparison.OrdinalIgnoreCase) || preset.FrameBufferCount == 0;
		OnPropertyChanged("Settings");
		StatusText = "Preset applied: " + preset.Name;
	}

	public void SelectAutoLowLatencyFormat()
	{
		SelectedVideoFormat = SelectAutoFormatCandidate();
		Settings.SelectedVideoFormatIndex = SelectedVideoFormat?.Index;
		StatusText = (((object)SelectedVideoFormat == null) ? "Auto format will use the device default" : ("Auto format selected: " + SelectedVideoFormat.DisplayName));
	}

	public void ApplyCaptureSourceType()
	{
		ApplyVideoDeviceFilter();
		ApplyDeviceSelections();
		OnPropertyChanged("Settings");
		StatusText = $"Source type: {Settings.CaptureSourceType}";
	}

	public async Task SaveAsync(CancellationToken cancellationToken = default(CancellationToken))
	{
		SyncLatencyProcessingFlag();
		RememberSelections();
		Settings.Window.SettingsPanelOpen = SettingsPanelOpen;
		await _settingsStore.SaveAsync(Settings, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
	}

	public Task RefreshSavedConfigsAsync(CancellationToken cancellationToken = default(CancellationToken))
	{
		cancellationToken.ThrowIfCancellationRequested();
		Directory.CreateDirectory(ConfigsPath);
		SavedConfigInfo[] items = (from path in Directory.EnumerateFiles(ConfigsPath, "*.mirrordeck.json", SearchOption.TopDirectoryOnly)
			select new FileInfo(path) into file
			orderby file.LastWriteTimeUtc descending
			select new SavedConfigInfo(Path.GetFileNameWithoutExtension(file.Name).Replace(".mirrordeck", string.Empty), file.FullName, new DateTimeOffset(file.LastWriteTime))).ToArray();
		Replace(SavedConfigs, items);
		return Task.CompletedTask;
	}

	public async Task SaveCurrentConfigAsync(CancellationToken cancellationToken = default(CancellationToken))
	{
		SyncLatencyProcessingFlag();
		RememberSelections();
		Directory.CreateDirectory(ConfigsPath);
		string name = SanitizeConfigName(ConfigName);
		string path = Path.Combine(ConfigsPath, name + ".mirrordeck.json");
		await using (FileStream stream = File.Create(path))
		{
			await JsonSerializer.SerializeAsync(stream, Settings, ConfigJsonOptions, cancellationToken).ConfigureAwait(continueOnCapturedContext: true);
		}
		ConfigName = name;
		await RefreshSavedConfigsAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext: true);
		StatusText = "Config saved: " + name;
	}

	public async Task LoadConfigAsync(SavedConfigInfo config, CancellationToken cancellationToken = default(CancellationToken))
	{
		await using FileStream stream = File.OpenRead(config.Path);
		MirrorDeckSettings mirrorDeckSettings = await JsonSerializer.DeserializeAsync<MirrorDeckSettings>(stream, ConfigJsonOptions, cancellationToken).ConfigureAwait(continueOnCapturedContext: true);
		if (mirrorDeckSettings == null)
		{
			StatusText = "Config could not be loaded: " + config.Name;
			return;
		}
		Settings = mirrorDeckSettings;
		NormalizeSettings(syncEnhancementFromLegacyFlag: true);
		SettingsPanelOpen = Settings.Window.SettingsPanelOpen;
		ApplyDeviceSelections();
		await LoadFormatsForSelectionAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext: true);
		UpdateAudioSettings();
		ConfigName = config.Name;
		OnPropertyChanged("Settings");
		StatusText = "Config loaded: " + config.Name;
	}

	public async Task DeleteConfigAsync(SavedConfigInfo config, CancellationToken cancellationToken = default(CancellationToken))
	{
		if (File.Exists(config.Path))
		{
			File.Delete(config.Path);
		}
		await RefreshSavedConfigsAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext: true);
		StatusText = "Config deleted: " + config.Name;
	}

	public async ValueTask ShutdownAsync()
	{
		await SaveAsync().ConfigureAwait(continueOnCapturedContext: false);
		await _audioPassthrough.DisposeAsync().ConfigureAwait(continueOnCapturedContext: false);
		await _videoPreview.DisposeAsync().ConfigureAwait(continueOnCapturedContext: false);
	}

	public VideoFormatInfo? GetCurrentInputFormat()
	{
		return SelectedVideoFormat;
	}

	private void RememberSelections()
	{
		if (IsRefreshingDevices || IsLoadingFormats)
		{
			return;
		}
		VideoDeviceInfo videoDeviceInfo = SelectedVideoDevice;
		if ((object)videoDeviceInfo != null)
		{
			Settings.SelectedVideoDeviceId = videoDeviceInfo.Id;
			VideoFormatInfo videoFormatInfo = SelectedVideoFormat;
			if ((object)videoFormatInfo != null && VideoFormats.Contains(videoFormatInfo) && _formatsDeviceId == videoDeviceInfo.Id)
			{
				Settings.SelectedVideoFormatIndex = videoFormatInfo.Index;
				MirrorDeckSettings mirrorDeckSettings = Settings;
				if (mirrorDeckSettings.VideoFormatPreferences == null)
				{
					Dictionary<string, VideoFormatInfo> dictionary = (mirrorDeckSettings.VideoFormatPreferences = new Dictionary<string, VideoFormatInfo>());
				}
				Settings.VideoFormatPreferences[videoDeviceInfo.Id] = videoFormatInfo;
			}
		}
		Settings.SelectedAudioInputDeviceId = SelectedAudioInputDevice?.Id;
		Settings.SelectedAudioOutputDeviceId = SelectedAudioOutputDevice?.Id;
	}

	private void NormalizeSettings(bool syncEnhancementFromLegacyFlag)
	{
		MirrorDeckSettings mirrorDeckSettings = Settings;
		if (mirrorDeckSettings.Output == null)
		{
			OutputSettings outputSettings = (mirrorDeckSettings.Output = new OutputSettings());
		}
		mirrorDeckSettings = Settings;
		if (mirrorDeckSettings.Scaling == null)
		{
			ScalingSettings scalingSettings = (mirrorDeckSettings.Scaling = new ScalingSettings());
		}
		mirrorDeckSettings = Settings;
		if (mirrorDeckSettings.Color == null)
		{
			ColorSettings colorSettings = (mirrorDeckSettings.Color = new ColorSettings());
		}
		mirrorDeckSettings = Settings;
		if (mirrorDeckSettings.Enhancement == null)
		{
			EnhancementSettings enhancementSettings = (mirrorDeckSettings.Enhancement = new EnhancementSettings());
		}
		mirrorDeckSettings = Settings;
		if (mirrorDeckSettings.Audio == null)
		{
			AudioSettings audioSettings = (mirrorDeckSettings.Audio = new AudioSettings());
		}
		mirrorDeckSettings = Settings;
		if (mirrorDeckSettings.Latency == null)
		{
			LatencySettings latencySettings = (mirrorDeckSettings.Latency = new LatencySettings());
		}
		mirrorDeckSettings = Settings;
		if (mirrorDeckSettings.Window == null)
		{
			WindowSettings windowSettings = (mirrorDeckSettings.Window = new WindowSettings());
		}
		mirrorDeckSettings = Settings;
		if (mirrorDeckSettings.Hotkeys == null)
		{
			HotkeySettings hotkeySettings = (mirrorDeckSettings.Hotkeys = new HotkeySettings());
		}
		mirrorDeckSettings = Settings;
		if (mirrorDeckSettings.CaptureOverlay == null)
		{
			CaptureOverlaySettings captureOverlaySettings = (mirrorDeckSettings.CaptureOverlay = new CaptureOverlaySettings());
		}
		Settings.CaptureOverlay.FramerateScale = Math.Clamp(Settings.CaptureOverlay.FramerateScale, 0.75, 2.0);
		if (syncEnhancementFromLegacyFlag)
		{
			Settings.Enhancement.Enabled = !Settings.Latency.DisableProcessing;
		}
		SyncLatencyProcessingFlag();
	}

	private void ApplyDeviceSelections()
	{
		SelectedVideoDevice = Pick(VideoDevices, Settings.SelectedVideoDeviceId);
        if (SelectedVideoDevice == null && string.IsNullOrWhiteSpace(Settings.SelectedVideoDeviceId) && Settings.OpenLastDeviceOnStartup)
            SelectedVideoDevice = VideoDevices.FirstOrDefault();
		if ((object)SelectedVideoDevice != null && DesktopCaptureSourceIds.GetSourceType(SelectedVideoDevice.Id) != Settings.CaptureSourceType)
		{
			SelectedVideoDevice = null;
		}
		SelectedAudioInputDevice = Pick(AudioInputDevices, Settings.SelectedAudioInputDeviceId) ?? PickPreferred(AudioInputDevices, "Digital Audio Interface", "USB Audio") ?? AudioInputDevices.FirstOrDefault();
		SelectedAudioOutputDevice = Pick(AudioOutputDevices, Settings.SelectedAudioOutputDeviceId) ?? AudioOutputDevices.FirstOrDefault();
	}

	private void ApplyVideoDeviceFilter()
	{
		string id = SelectedVideoDevice?.Id ?? Settings.SelectedVideoDeviceId;
		VideoDeviceInfo[] items = _allVideoDevices.Where((VideoDeviceInfo device) => DesktopCaptureSourceIds.GetSourceType(device.Id) == Settings.CaptureSourceType).ToArray();
		Replace(VideoDevices, items);
		SelectedVideoDevice = Pick(VideoDevices, id);
	}

	private static T? Pick<T>(IEnumerable<T> items, string? id) where T : class
	{
		if (string.IsNullOrWhiteSpace(id))
		{
			return null;
		}
		return items.FirstOrDefault((T item) =>
		{
			if (item is VideoDeviceInfo videoDeviceInfo)
			{
				return string.Equals(videoDeviceInfo.Id, id, StringComparison.OrdinalIgnoreCase);
			}
			return item is AudioDeviceInfo audioDeviceInfo && string.Equals(audioDeviceInfo.Id, id, StringComparison.OrdinalIgnoreCase);
		});
	}

	private static AudioDeviceInfo? PickPreferred(IEnumerable<AudioDeviceInfo> items, params string[] nameParts)
	{
		foreach (string namePart in nameParts)
		{
			AudioDeviceInfo audioDeviceInfo = items.FirstOrDefault((AudioDeviceInfo item) => item.Name.Contains(namePart, StringComparison.OrdinalIgnoreCase));
			if ((object)audioDeviceInfo != null)
			{
				return audioDeviceInfo;
			}
		}
		return null;
	}

	private static void Replace<T>(ObservableCollection<T> collection, IEnumerable<T> items)
	{
		collection.Clear();
		foreach (T item in items)
		{
			collection.Add(item);
		}
	}

	private static string SanitizeConfigName(string name)
	{
		string text = $"Config-{DateTimeOffset.Now:yyyyMMdd-HHmmss}";
		string source = (string.IsNullOrWhiteSpace(name) ? text : name.Trim());
		char[] invalid = Path.GetInvalidFileNameChars();
		string text2 = new string(source.Select((char ch) => (!Enumerable.Contains(invalid, ch)) ? ch : '-').ToArray()).Trim(' ', '.', '-');
		if (!string.IsNullOrWhiteSpace(text2))
		{
			return text2;
		}
		return text;
	}

	private VideoFormatInfo? SelectAutoFormatCandidate()
	{
		return (from format in VideoFormats
			orderby IsCompressed(format.PixelFormat), format.FramesPerSecond descending, format.Width * format.Height descending
			select format).FirstOrDefault();
	}

	private static int IsCompressed(string pixelFormat)
	{
		if (!pixelFormat.Contains("MJPEG", StringComparison.OrdinalIgnoreCase) && !pixelFormat.Contains("MJPG", StringComparison.OrdinalIgnoreCase))
		{
			return 0;
		}
		return 1;
	}

	private async Task<T> TryWithTimeout<T>(Task<T> task, TimeSpan timeout, string operation, T fallback)
	{
		Stopwatch stopwatch = Stopwatch.StartNew();
		_logger.Info(operation + " started.");
		try
		{
			if (await Task.WhenAny(task, Task.Delay(timeout)).ConfigureAwait(continueOnCapturedContext: true) == task)
			{
				T result = await task.ConfigureAwait(continueOnCapturedContext: true);
				_logger.Info($"{operation} completed in {stopwatch.ElapsedMilliseconds} ms.");
				return result;
			}
			_logger.Error($"{operation} timed out after {timeout.TotalSeconds:0.#} seconds.");
			task.ContinueWith((Task<T> completed) =>
			{
				if (completed.Exception != null)
				{
					_logger.Error(operation + " eventually failed after timeout.", completed.Exception);
				}
			}, TaskContinuationOptions.ExecuteSynchronously);
			return fallback;
		}
		catch (Exception exception)
		{
			_logger.Error(operation + " failed.", exception);
			return fallback;
		}
	}

	private async Task<bool> TryWithTimeout(Task task, TimeSpan timeout, string operation)
	{
		Stopwatch stopwatch = Stopwatch.StartNew();
		_logger.Info(operation + " started.");
		try
		{
			if (await Task.WhenAny(task, Task.Delay(timeout)).ConfigureAwait(continueOnCapturedContext: true) == task)
			{
				await task.ConfigureAwait(continueOnCapturedContext: true);
				_logger.Info($"{operation} completed in {stopwatch.ElapsedMilliseconds} ms.");
				return true;
			}
			_logger.Error($"{operation} timed out after {timeout.TotalSeconds:0.#} seconds.");
			task.ContinueWith((Task completed) =>
			{
				if (completed.Exception != null)
				{
					_logger.Error(operation + " eventually failed after timeout.", completed.Exception);
				}
			}, TaskContinuationOptions.ExecuteSynchronously);
			return false;
		}
		catch (Exception exception)
		{
			_logger.Error(operation + " failed.", exception);
			return false;
		}
	}

	private async Task ReleaseGateWhenCompleteAsync(Task task, SemaphoreSlim gate, string operation)
	{
		try
		{
			await task.ConfigureAwait(continueOnCapturedContext: false);
			_logger.Info(operation + " eventually completed after timeout.");
		}
		catch (Exception exception)
		{
			_logger.Error(operation + " eventually failed after timeout.", exception);
		}
		finally
		{
			gate.Release();
		}
	}
}
