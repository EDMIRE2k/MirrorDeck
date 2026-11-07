using System;

namespace MirrorDeck.Core.Models;

public enum DlssOutputResolution
{
    PreviewWindow,
    CurrentMonitor,
    InputResolution,
    P1080,
    P1440,
    P4K
}

public static class DlssOutputSizing
{
    public static (int Width, int Height) Resolve(DlssOutputResolution mode, int inputWidth, int inputHeight,
        int viewportWidth, int viewportHeight, int monitorWidth, int monitorHeight)
    {
        inputWidth = Math.Max(1, inputWidth);
        inputHeight = Math.Max(1, inputHeight);
        if (mode == DlssOutputResolution.InputResolution) return (inputWidth, inputHeight);
        if (mode == DlssOutputResolution.PreviewWindow || !Enum.IsDefined(mode)) return (Math.Max(1, viewportWidth), Math.Max(1, viewportHeight));
        var box = mode switch
        {
            DlssOutputResolution.P1080 => (1920, 1080),
            DlssOutputResolution.P1440 => (2560, 1440),
            DlssOutputResolution.P4K => (3840, 2160),
            _ => (Math.Max(1, monitorWidth), Math.Max(1, monitorHeight))
        };
        // Preserve capture aspect ratio inside the selected resolution, independently
        // of the final preview layout (which may stretch, crop or letterbox it).
        double scale = Math.Min((double)box.Item1 / inputWidth, (double)box.Item2 / inputHeight);
        return (Math.Max(1, (int)Math.Round(inputWidth * scale)), Math.Max(1, (int)Math.Round(inputHeight * scale)));
    }
}
