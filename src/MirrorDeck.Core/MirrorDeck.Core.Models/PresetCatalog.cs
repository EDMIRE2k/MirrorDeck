using System.Collections.Generic;

namespace MirrorDeck.Core.Models;

public static class PresetCatalog
{
	public static IReadOnlyList<PresetDefinition> All { get; } = new _003C_003Ez__ReadOnlyArray<PresetDefinition>(new PresetDefinition[7]
	{
		new PresetDefinition("Lowest Latency", ScalingFilter.Bilinear, AspectMode.Fit, IntegerScaling: false, 0, VSyncEnabled: false, 0.0, 0, DisableProcessing: true),
		new PresetDefinition("Balanced", ScalingFilter.Bilinear, AspectMode.Fit, IntegerScaling: false, 1, VSyncEnabled: true, 0.25, 10, DisableProcessing: false),
		new PresetDefinition("Best Quality", ScalingFilter.Lanczos, AspectMode.Fit, IntegerScaling: false, 2, VSyncEnabled: true, 0.45, 25, DisableProcessing: false),
		new PresetDefinition("Pixel Perfect", ScalingFilter.NearestNeighbor, AspectMode.Fit, IntegerScaling: true, 0, VSyncEnabled: false, 0.0, 0, DisableProcessing: true),
		new PresetDefinition("Handheld Console", ScalingFilter.Bicubic, AspectMode.Fit, IntegerScaling: false, 1, VSyncEnabled: true, 0.35, 15, DisableProcessing: false),
		new PresetDefinition("Retro Console", ScalingFilter.NearestNeighbor, AspectMode.Fit, IntegerScaling: true, 1, VSyncEnabled: false, 0.0, 0, DisableProcessing: true),
		new PresetDefinition("Streaming Preview", ScalingFilter.Bilinear, AspectMode.Fit, IntegerScaling: false, 2, VSyncEnabled: true, 0.2, 40, DisableProcessing: false)
	});
}
