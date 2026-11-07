// AMD FidelityFX FSR 1 EASU, using the unchanged upstream headers.
// Spatial reconstruction only for pixels rejected by the temporal DLSS path.
#define A_GPU 1
#define A_HLSL 1
#include "ffx_a.h"
#define FSR_EASU_F 1
#include "ffx_fsr1.h"

float4 FsrEasuRF(float2 p) { return currentInput.GatherRed(linearClamp, p); }
float4 FsrEasuGF(float2 p) { return currentInput.GatherGreen(linearClamp, p); }
float4 FsrEasuBF(float2 p) { return currentInput.GatherBlue(linearClamp, p); }

float3 ProtectedUpscale(uint2 pixel)
{
    float2 uv = (pixel + 0.5) / shape.zw;
    if (all(shape.xy == shape.zw)) return currentInput.Load(int3(pixel, 0)).rgb;
    float2 inverseSize = rcp(shape.xy);
    uint4 con0 = asuint(float4(1.0, 1.0, uv * shape.xy - 0.5));
    uint4 con1 = asuint(float4(inverseSize, inverseSize.x, -inverseSize.y));
    uint4 con2 = asuint(float4(-inverseSize.x, 2.0 * inverseSize.y, inverseSize.x, 2.0 * inverseSize.y));
    uint4 con3 = asuint(float4(0.0, 4.0 * inverseSize.y, 0.0, 0.0));
    float3 color;
    FsrEasuF(color, uint2(0, 0), con0, con1, con2, con3);
    return color;
}
