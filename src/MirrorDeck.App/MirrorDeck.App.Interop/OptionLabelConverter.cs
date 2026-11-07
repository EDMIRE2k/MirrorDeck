using System;
using System.Globalization;
using System.Windows.Data;
using MirrorDeck.Core.Models;

namespace MirrorDeck.App.Interop;

public sealed class OptionLabelConverter : IValueConverter
{
	public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
	{
        if (value is DlssModel model)
            return model switch
            {
                DlssModel.Auto => "Auto - match upscale ratio",
                DlssModel.M => "M - DLSS 4.5 (Performance)",
                DlssModel.K => "K - DLSS 4 (Quality / DLAA)",
                DlssModel.L => "L - DLSS 4.5 (Ultra Performance)",
                _ => "M - DLSS 4.5 (Performance)"
            };
        if (value is DlssMotionQuality motion)
            return motion == DlssMotionQuality.High ? "High - higher GPU cost" : "Medium - default";
        if (value is DlssOutputResolution resolution)
            return resolution switch
            {
                DlssOutputResolution.PreviewWindow => "Preview window size",
                DlssOutputResolution.CurrentMonitor => "Current monitor size",
                DlssOutputResolution.InputResolution => "Input resolution (DLAA)",
                DlssOutputResolution.P1080 => "1080p (1920 x 1080)",
                DlssOutputResolution.P1440 => "1440p (2560 x 1440)",
                DlssOutputResolution.P4K => "4K (3840 x 2160)",
                _ => "Preview window size"
            };
		if (value is OutputResolutionMode)
		{
			switch ((OutputResolutionMode)value)
			{
			case OutputResolutionMode.MatchInput:
				return "Match input";
			case OutputResolutionMode.P720:
				return "720p (1280 x 720)";
			case OutputResolutionMode.P1080:
				return "1080p (1920 x 1080)";
			case OutputResolutionMode.P1440:
				return "1440p (2560 x 1440)";
			case OutputResolutionMode.P4K:
				return "4K (3840 x 2160)";
			case OutputResolutionMode.Custom:
				return "Custom size";
			}
		}
		else if (value is OverlayCorner)
		{
			switch ((OverlayCorner)value)
			{
			case OverlayCorner.TopLeft:
				return "Top left";
			case OverlayCorner.TopRight:
				return "Top right";
			case OverlayCorner.BottomLeft:
				return "Bottom left";
			case OverlayCorner.BottomRight:
				return "Bottom right";
			}
		}
		else if (value is ScalingFilter filter)
		{
			return filter switch
			{
				ScalingFilter.NearestNeighbor => "Nearest neighbor",
				ScalingFilter.SharpBilinear => "Sharp bilinear - pixel art",
				ScalingFilter.FsrEasu => "FSR 1 EASU - edge-adaptive",
				ScalingFilter.AnimeLineArt => "Anime line art - Anime4K lite",
				ScalingFilter.Dlss45 => "DLSS 4.5 - native RTX (experimental)",
                ScalingFilter.Dlss5 => "DLSS 5 - neural rendering (experimental)",
				_ => filter.ToString()
			};
		}
		return value?.ToString() ?? "";
	}

	public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
	{
		return Binding.DoNothing;
	}
}
