namespace MirrorDeck.PreviewHost;

internal sealed record DesktopMirrorFrame(byte[] Pixels, int Width, int Height, int Stride);
