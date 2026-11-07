namespace MirrorDeck.Core.Models;

public sealed class EnhancementSettings
{
	public bool Enabled { get; set; } = true;

	public bool CleanUpscalingEnabled { get; set; }

	public double CleanUpscalingStrength { get; set; } = 0.35;

	public double Denoise { get; set; } = 0.12;

	public double EdgeRestore { get; set; } = 0.35;
}
