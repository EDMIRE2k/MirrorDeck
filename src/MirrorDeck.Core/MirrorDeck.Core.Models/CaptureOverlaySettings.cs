namespace MirrorDeck.Core.Models;

public sealed class CaptureOverlaySettings
{
	public bool ShowFramerate { get; set; }

	public OverlayCorner FramerateCorner { get; set; }

	public double FramerateScale { get; set; } = 1.0;

	public bool CaptureMousePointer { get; set; } = true;
}
