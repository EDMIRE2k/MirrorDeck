Texture2D<float4> currentInput : register(t0);
Texture2D<float4> previousInput : register(t1);
Texture2D<float4> neuralOutput : register(t2);
Texture2D<float> sourceProtection : register(t3);
SamplerState linearClamp : register(s0);
RWTexture2D<float4> stableOutput : register(u0);
cbuffer Parameters : register(b0) { float4 shape; float4 control; }
#include "ProtectedUpscale.hlsl"

[numthreads(8, 8, 1)]
void Stabilize(uint3 id : SV_DispatchThreadID)
{
    if (any(id.xy >= (uint2)shape.zw)) return;
    float3 color = neuralOutput.Load(int3(id.xy, 0)).rgb;
    int2 source = int2((id.xy + 0.5) * shape.xy / shape.zw);
    bool unchanged = true;
    float detailProtection = 0;
    [unroll] for (int y = -1; y <= 1; y++)
    [unroll] for (int x = -1; x <= 1; x++)
    {
        int2 p = clamp(source + int2(x, y), int2(0, 0), int2(shape.xy) - 1);
        float3 current = currentInput.Load(int3(p, 0)).rgb;
        float3 priorInput = previousInput.Load(int3(p, 0)).rgb;
        unchanged = unchanged && all(current == priorInput);
        detailProtection = max(detailProtection, sourceProtection.Load(int3(p, 0)));
    }

    // Capture motion cannot reliably track individual glyph strokes. Preserve
    // high-contrast source detail spatially, including on the first transition
    // frame, rather than freezing a malformed neural reconstruction afterward.
    // The guide mask measures reprojected error and flow confidence. Raw
    // same-pixel change would reject even correctly tracked moving detail.
    if (detailProtection > 0)
        color = lerp(color, ProtectedUpscale(id.xy), detailProtection);
    if (control.x > 0.5)
    {
        stableOutput[id.xy] = float4(color, 0);
        return;
    }

    // Let the reconstruction settle, then hold unchanged source neighborhoods.
    // Exact comparison avoids freezing slow fades; movement releases immediately.
    // Alpha stores the settle counter; the presentation shader writes opaque alpha.
    float4 prior = stableOutput[id.xy];
    float age = unchanged ? min(prior.a + 1, 8) : 0;
    stableOutput[id.xy] = float4(unchanged && prior.a >= 8 ? prior.rgb : color, age);
}
