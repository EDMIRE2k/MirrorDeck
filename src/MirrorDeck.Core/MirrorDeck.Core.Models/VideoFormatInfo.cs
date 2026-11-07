using System;

namespace MirrorDeck.Core.Models;

public sealed record VideoFormatInfo(int Index, int Width, int Height, double FramesPerSecond, string PixelFormat, string ColorSpace)
{
	public string DisplayName => $"{Width}x{Height} @ {FramesPerSecond:0.##} FPS - {PixelFormat}" + ((string.IsNullOrWhiteSpace(ColorSpace) || ColorSpace == "Unknown") ? string.Empty : (" - " + ColorSpace));

	public bool MatchesMode(VideoFormatInfo other)
	{
		if (Width == other.Width && Height == other.Height && Math.Abs(FramesPerSecond - other.FramesPerSecond) < 0.02)
		{
			return string.Equals(PixelFormat, other.PixelFormat, StringComparison.OrdinalIgnoreCase);
		}
		return false;
	}

	public override string ToString()
	{
		return DisplayName;
	}
}
