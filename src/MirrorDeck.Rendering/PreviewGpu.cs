using System;
using System.Linq;
using MirrorDeck.Core.Models;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;

namespace MirrorDeck.Rendering;

public sealed record PreviewGpuInfo(string Name, bool SupportsDlss);

public static class PreviewGpu
{
    // Both processes use DXGI's same high-performance ordering, not a WMI list
    // that can accidentally advertise the capabilities of an unused adapter.
    public static ID3D12Device2 CreateDevice(IDXGIFactory4 factory, out PreviewGpuInfo info)
    {
        using var preferred = factory.QueryInterfaceOrNull<IDXGIFactory6>();
        for (uint index = 0; ; index++)
        {
            IDXGIAdapter1 adapter;
            var result = preferred != null
                ? preferred.EnumAdapterByGpuPreference(index, GpuPreference.HighPerformance, out adapter)
                : factory.EnumAdapters1(index, out adapter);
            if (result.Failure) break;
            using (adapter)
            {
                var description = adapter.Description1;
                if ((description.Flags & AdapterFlags.Software) != 0) continue;
                if (D3D12.D3D12CreateDevice<ID3D12Device2>(adapter, FeatureLevel.Level_11_0, out var device).Failure || device == null) continue;
                info = new(description.Description, IsRtx(description.VendorId, description.Description));
                return device;
            }
        }
        throw new InvalidOperationException("No hardware Direct3D 12 device is available.");
    }

    public static PreviewGpuInfo Detect()
    {
        try
        {
            using var factory = DXGI.CreateDXGIFactory2<IDXGIFactory4>(false);
            using var device = CreateDevice(factory, out var info);
            return info;
        }
        catch { return new("Unavailable", false); }
    }

    public static bool IsRtx(uint vendorId, string name) => vendorId == 0x10de &&
        name.Split(new[] { ' ', '-', '_' }, StringSplitOptions.RemoveEmptyEntries)
            .Any(token => token.Equals("RTX", StringComparison.OrdinalIgnoreCase));

    public static bool IsDlss(ScalingFilter filter) => filter is ScalingFilter.Dlss45 or ScalingFilter.Dlss5;
    public static ScalingFilter[] Filters(bool supportsDlss) => Enum.GetValues<ScalingFilter>()
        .Where(filter => supportsDlss || !IsDlss(filter)).ToArray();
    public static ScalingFilter Validate(ScalingFilter filter, bool supportsDlss) =>
        !Enum.IsDefined(filter) || (IsDlss(filter) && !supportsDlss) ? ScalingFilter.FsrEasu : filter;
}
