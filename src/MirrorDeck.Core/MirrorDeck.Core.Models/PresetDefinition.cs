namespace MirrorDeck.Core.Models;

public sealed record PresetDefinition(string Name, ScalingFilter ScalingFilter, AspectMode AspectMode, bool IntegerScaling, int FrameBufferCount, bool VSyncEnabled, double Sharpness, int AudioDelayMilliseconds, bool DisableProcessing);
