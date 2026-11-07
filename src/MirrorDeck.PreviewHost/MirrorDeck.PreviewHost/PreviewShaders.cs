using System;
using System.IO;

namespace MirrorDeck.PreviewHost;

internal static class PreviewShaders
{
    private static readonly Lazy<string> SharedSource = new(() =>
        Read("Preview.hlsl").Replace("// UPSCALER_IMPLEMENTATION", Read("Upscalers.hlsl")
            .Replace("// FFX_A_IMPLEMENTATION", Read("Vendor.ffx_a.h"))
            .Replace("// FFX_FSR_IMPLEMENTATION", Read("Vendor.ffx_fsr1.h"))));

    internal static string Source => SharedSource.Value;

    private static string Read(string name)
    {
        using var stream = typeof(PreviewShaders).Assembly.GetManifestResourceStream("Shaders." + name)
            ?? throw new InvalidOperationException("Missing embedded preview shader: " + name);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
