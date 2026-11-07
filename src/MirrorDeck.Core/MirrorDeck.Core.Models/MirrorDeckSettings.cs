using System;
using System.Collections.Generic;

namespace MirrorDeck.Core.Models;

public sealed class MirrorDeckSettings
{
    public PreviewWindowSettings PreviewWindow { get; set; } = new();
	public CaptureSourceType CaptureSourceType { get; set; }

	public string? SelectedVideoDeviceId { get; set; }

	public int? SelectedVideoFormatIndex { get; set; }

	public Dictionary<string, VideoFormatInfo> VideoFormatPreferences { get; set; } = new Dictionary<string, VideoFormatInfo>(StringComparer.OrdinalIgnoreCase);

	public string? SelectedAudioInputDeviceId { get; set; }

	public string? SelectedAudioOutputDeviceId { get; set; }

	public OutputSettings Output { get; set; } = new OutputSettings();

	public ScalingSettings Scaling { get; set; } = new ScalingSettings();

	public ColorSettings Color { get; set; } = new ColorSettings();

	public EnhancementSettings Enhancement { get; set; } = new EnhancementSettings();

	public AudioSettings Audio { get; set; } = new AudioSettings();

	public LatencySettings Latency { get; set; } = new LatencySettings();

	public WindowSettings Window { get; set; } = new WindowSettings();

	public HotkeySettings Hotkeys { get; set; } = new HotkeySettings();

	public CaptureOverlaySettings CaptureOverlay { get; set; } = new CaptureOverlaySettings();

	public bool OpenLastDeviceOnStartup { get; set; } = true;
    public bool CheckForUpdatesOnStartup { get; set; } = true;

	public bool StartFullscreen { get; set; }

	public bool StartMuted { get; set; }
}
