namespace MirrorDeck.Core.Models;

public sealed class LatencySettings
{
	public bool LowLatencyMode { get; set; } = true;

	public int FrameBufferCount { get; set; } = 2;

	public bool VSyncEnabled { get; set; } = true;

	public int FpsCap { get; set; }

	public bool DropLateFrames { get; set; }

	public bool DisableProcessing { get; set; }
}
