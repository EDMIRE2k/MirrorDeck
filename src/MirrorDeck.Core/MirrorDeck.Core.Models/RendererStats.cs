namespace MirrorDeck.Core.Models;

public sealed record RendererStats(int InputWidth, int InputHeight, int OutputWidth, int OutputHeight, double FramesPerSecond, double FrameTimeMilliseconds, long DroppedFrames, double? EstimatedLatencyMilliseconds);
