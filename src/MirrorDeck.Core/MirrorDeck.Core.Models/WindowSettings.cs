namespace MirrorDeck.Core.Models;

public sealed class WindowSettings
{
	public double? Left { get; set; }

	public double? Top { get; set; }

	public double Width { get; set; } = 1280.0;

	public double Height { get; set; } = 760.0;

	public bool AlwaysOnTop { get; set; }

	public double Opacity { get; set; } = 1.0;

	public bool Borderless { get; set; }

	public bool SettingsPanelOpen { get; set; } = true;
}
