using System;

namespace MirrorDeck.Core.Models;

public static class PreviewWindowSizing
{
    public static (int Width, int Height) Fit(int width, int height, int workWidth, int workHeight, bool launch)
    {
        width = Math.Clamp(width, 160, 7680);
        height = Math.Clamp(height, 120, 4320);
        // Leave room for borders, taskbar and moving the window. This never changes
        // capture resolution or the neural reconstruction target.
        int maxWidth = Math.Max(160, workWidth - 48);
        int maxHeight = Math.Max(120, workHeight - 80);
        if (launch) { maxWidth = Math.Min(1280, (int)(maxWidth * .8)); maxHeight = Math.Min(720, (int)(maxHeight * .8)); }
        double scale = Math.Min(1, Math.Min((double)maxWidth / width, (double)maxHeight / height));
        return (Math.Max(160, (int)Math.Round(width * scale)), Math.Max(120, (int)Math.Round(height * scale)));
    }
}
