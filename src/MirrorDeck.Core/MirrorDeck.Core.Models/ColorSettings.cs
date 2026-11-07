namespace MirrorDeck.Core.Models;

public sealed class ColorSettings
{
	public double Brightness { get; set; }

	public double Contrast { get; set; } = 1.0;

	public double Saturation { get; set; } = 1.0;

	public double Gamma { get; set; } = 1.0;

	public double Hue { get; set; }

	public RgbRangeMode RgbRange { get; set; }
}
