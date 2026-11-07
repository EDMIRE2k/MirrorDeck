using System;

namespace MirrorDeck.Core.Models;

public sealed class PreviewRenderSettings
{
    public PreviewWindowSettings Window { get; set; } = new();
    public int WindowWidth { get; set; }
    public int WindowHeight { get; set; }
	public bool DisableProcessing { get; set; }

	public bool LowLatencyMode { get; set; } = true;

	public int FrameBufferCount { get; set; }

	public bool VSyncEnabled { get; set; }

	public int FpsCap { get; set; }

	public bool DropLateFrames { get; set; } = true;

	public OutputSettings Output { get; set; } = new OutputSettings();

	public ScalingSettings Scaling { get; set; } = new ScalingSettings();

	public ColorSettings Color { get; set; } = new ColorSettings();

	public EnhancementSettings Enhancement { get; set; } = new EnhancementSettings();

	public CaptureOverlaySettings CaptureOverlay { get; set; } = new CaptureOverlaySettings();

	public HotkeySettings Hotkeys { get; set; } = new HotkeySettings();

	public static PreviewRenderSettings From(MirrorDeckSettings settings)
	{
		return new PreviewRenderSettings
		{
            Window = new PreviewWindowSettings { AlwaysOnTop = settings.PreviewWindow.AlwaysOnTop, Borderless = settings.PreviewWindow.Borderless, Opacity = settings.PreviewWindow.Opacity },
			DisableProcessing = (!settings.Enhancement.Enabled || settings.Latency.DisableProcessing),
			LowLatencyMode = settings.Latency.LowLatencyMode,
			FrameBufferCount = settings.Latency.FrameBufferCount,
			VSyncEnabled = settings.Latency.VSyncEnabled,
			FpsCap = settings.Latency.FpsCap,
			DropLateFrames = settings.Latency.DropLateFrames,
			Output = new OutputSettings
			{
				ProcessingMaxHeight = settings.Output.ProcessingMaxHeight,
				ResolutionMode = settings.Output.ResolutionMode,
				CustomWidth = settings.Output.CustomWidth,
				CustomHeight = settings.Output.CustomHeight,
				PreserveAspectRatio = settings.Output.PreserveAspectRatio,
				AspectMode = settings.Output.AspectMode
			},
			Scaling = new ScalingSettings
			{
                Neural = (settings.Scaling.Neural ?? new()).Copy(),
				Filter = settings.Scaling.Filter,
				IntegerScaling = settings.Scaling.IntegerScaling,
				BlackBars = settings.Scaling.BlackBars,
				Sharpness = settings.Scaling.Sharpness,
				Debanding = settings.Scaling.Debanding,
				UpscaleStrength = settings.Scaling.UpscaleStrength,
                DlssOutputResolution = settings.Scaling.DlssOutputResolution,
                DlssSharpness = settings.Scaling.DlssSharpness,
                DlssModel = settings.Scaling.DlssModel,
                DlssMotionQuality = settings.Scaling.DlssMotionQuality,
                DlssDetailProtection = settings.Scaling.DlssDetailProtection
			},
			Color = new ColorSettings
			{
				Brightness = settings.Color.Brightness,
				Contrast = settings.Color.Contrast,
				Saturation = settings.Color.Saturation,
				Gamma = settings.Color.Gamma,
				Hue = settings.Color.Hue,
				RgbRange = settings.Color.RgbRange
			},
			Enhancement = new EnhancementSettings
			{
				Enabled = settings.Enhancement.Enabled,
				CleanUpscalingEnabled = settings.Enhancement.CleanUpscalingEnabled,
				CleanUpscalingStrength = settings.Enhancement.CleanUpscalingStrength,
				Denoise = settings.Enhancement.Denoise,
				EdgeRestore = settings.Enhancement.EdgeRestore
			},
			CaptureOverlay = new CaptureOverlaySettings
			{
				ShowFramerate = settings.CaptureOverlay.ShowFramerate,
				FramerateCorner = settings.CaptureOverlay.FramerateCorner,
				FramerateScale = Math.Clamp(settings.CaptureOverlay.FramerateScale, 0.75, 2.0),
				CaptureMousePointer = settings.CaptureOverlay.CaptureMousePointer
			},
			Hotkeys = new HotkeySettings
			{
				Fullscreen = (string.IsNullOrWhiteSpace(settings.Hotkeys.Fullscreen) ? "F11" : settings.Hotkeys.Fullscreen),
				Mute = (string.IsNullOrWhiteSpace(settings.Hotkeys.Mute) ? "Ctrl+M" : settings.Hotkeys.Mute),
				StatsOverlay = (string.IsNullOrWhiteSpace(settings.Hotkeys.StatsOverlay) ? "Ctrl+T" : settings.Hotkeys.StatsOverlay),
				AlwaysOnTop = (string.IsNullOrWhiteSpace(settings.Hotkeys.AlwaysOnTop) ? "Ctrl+P" : settings.Hotkeys.AlwaysOnTop),
				Screenshot = (string.IsNullOrWhiteSpace(settings.Hotkeys.Screenshot) ? "Ctrl+S" : settings.Hotkeys.Screenshot),
				RelaunchPreview = (string.IsNullOrWhiteSpace(settings.Hotkeys.RelaunchPreview) ? "Ctrl+R" : settings.Hotkeys.RelaunchPreview)
			}
		};
	}
}
