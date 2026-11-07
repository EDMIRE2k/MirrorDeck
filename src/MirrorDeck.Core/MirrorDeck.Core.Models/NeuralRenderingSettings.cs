namespace MirrorDeck.Core.Models;

public enum NeuralStyle { Default, Natural, Cinematic }
public enum NeuralPreset { Default, A, B, C }

public sealed class NeuralRenderingSettings
{
    public int Passes { get; set; } = 1;
    public NeuralPreset Preset { get; set; }
    public NeuralStyle Style { get; set; }
    public int WorkHeight { get; set; } = 720;
    public double Intensity { get; set; } = 2;
    public double LocalTone { get; set; } = 2;
    public double LocalStructure { get; set; } = 2;
    public double SkinStructure { get; set; } = -1;
    public bool AutomaticMask { get; set; }
    public bool UiCorrection { get; set; }
    public double ColorStrength { get; set; } = 1;
    public double Blend { get; set; } = 1;

    public NeuralRenderingSettings Copy() => (NeuralRenderingSettings)MemberwiseClone();
}
