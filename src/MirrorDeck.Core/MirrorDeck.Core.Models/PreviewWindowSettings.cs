namespace MirrorDeck.Core.Models;

public sealed class PreviewWindowSettings
{
    public bool AlwaysOnTop { get; set; }
    public bool Borderless { get; set; }
    public double Opacity { get; set; } = 1;
}

public enum PreviewCommand { Fullscreen = 1, Screenshot = 2 }
