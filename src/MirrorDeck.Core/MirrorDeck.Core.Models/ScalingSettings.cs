namespace MirrorDeck.Core.Models;

public sealed class ScalingSettings
{
    public NeuralRenderingSettings Neural { get; set; } = new();
	public ScalingFilter Filter { get; set; } = ScalingFilter.Bilinear;

	public bool IntegerScaling { get; set; }

	public bool BlackBars { get; set; } = true;

	public double Sharpness { get; set; } = 0.25;

	public bool Debanding { get; set; }

	public double UpscaleStrength { get; set; } = 1.0;

    public DlssOutputResolution DlssOutputResolution { get; set; } = DlssOutputResolution.PreviewWindow;

    public double DlssSharpness { get; set; }

    public DlssModel DlssModel { get; set; } = DlssModel.M;

    public DlssMotionQuality DlssMotionQuality { get; set; } = DlssMotionQuality.Medium;

    public double DlssDetailProtection { get; set; } = 1.0;
}
