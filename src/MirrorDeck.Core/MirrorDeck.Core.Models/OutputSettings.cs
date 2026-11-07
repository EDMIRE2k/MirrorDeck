using System;

namespace MirrorDeck.Core.Models;

public sealed class OutputSettings
{
	public int ProcessingMaxHeight { get; set; }

	public OutputResolutionMode ResolutionMode { get; set; } = OutputResolutionMode.MatchInput;

	public int CustomWidth { get; set; } = 1280;

	public int CustomHeight { get; set; } = 720;

	public bool PreserveAspectRatio { get; set; } = true;

	public AspectMode AspectMode { get; set; }

	public static (int Width, int Height) GetProcessingSize(int width, int height, int maxHeight)
	{
		width = Math.Max(1, width);
		height = Math.Max(1, height);
		if (maxHeight <= 0 || height <= maxHeight)
		{
			return (Width: width, Height: height);
		}
		maxHeight = Math.Clamp(maxHeight, 240, 4320);
		return (Width: Math.Max(1, (int)Math.Round((double)width * (double)maxHeight / (double)height)), Height: maxHeight);
	}
}
