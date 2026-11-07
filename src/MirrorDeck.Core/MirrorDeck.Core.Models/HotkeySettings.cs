namespace MirrorDeck.Core.Models;

public sealed class HotkeySettings
{
	public string Fullscreen { get; set; } = "F11";

	public string Mute { get; set; } = "Ctrl+M";

	public string StatsOverlay { get; set; } = "Ctrl+T";

	public string AlwaysOnTop { get; set; } = "Ctrl+P";

	public string Screenshot { get; set; } = "Ctrl+S";

	public string RelaunchPreview { get; set; } = "Ctrl+R";
}
