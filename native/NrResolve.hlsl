Texture2D<float4> original : register(t0);
Texture2D<float4> proxy : register(t1);
Texture2D<float4> neural : register(t2);
SamplerState linearClamp : register(s0);
RWTexture2D<float4> output : register(u0);
cbuffer Parameters : register(b0) { float4 shape; float4 control; }
[numthreads(8,8,1)]
void Stabilize(uint3 id : SV_DispatchThreadID) {
    if (any(id.xy >= (uint2)shape.zw)) return;
    float2 uv = (id.xy + 0.5) / shape.zw;
    float2 rawUv = float2(uv.x, control.w > 0.5 ? 1.0 - uv.y : uv.y);
    float3 source = original.SampleLevel(linearClamp, rawUv, 0).rgb;
    if (control.x < 0.5) { output[id.xy] = float4(source, 1); return; }
    float3 base = proxy.SampleLevel(linearClamp, uv, 0).rgb;
    float3 nr = neural.SampleLevel(linearClamp, uv, 0).rgb;
    // Preserve high-frequency source detail when NR runs below capture size.
    float3 reconstructed = saturate(nr + source - base);
    float originalLuma = dot(source, float3(.2126,.7152,.0722));
    float newLuma = dot(reconstructed, float3(.2126,.7152,.0722));
    float3 toneOnly = source * clamp(newLuma / max(originalLuma, .001), 0, 4);
    float3 result = lerp(toneOnly, reconstructed, saturate(control.z));
    output[id.xy] = float4(saturate(lerp(source, result, saturate(control.y))), 1);
}
