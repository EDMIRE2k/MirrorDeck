Texture2D<float4> currentImage : register(t0);
Texture2D<float4> previousImage : register(t1);
Texture2D<int2> blockFlow : register(t2);
Texture2D<uint> blockCost : register(t3);
SamplerState linearClamp : register(s0);
RWTexture2D<float4> colorOut : register(u0);
RWTexture2D<float2> motionOut : register(u1);
RWTexture2D<float> depthOut : register(u2);
RWTexture2D<float> biasOut : register(u3);
cbuffer Parameters : register(b0) { float4 shape; float4 control; }

float LumaAt(int2 p)
{
    p = clamp(p, int2(0, 0), int2(shape.xy) - 1);
    return dot(currentImage.Load(int3(p, 0)).rgb, float3(0.299, 0.587, 0.114));
}

[numthreads(8, 8, 1)]
void Orient(uint3 id : SV_DispatchThreadID)
{
    if (any(id.xy >= (uint2)shape.xy)) return;
    float2 uv = (id.xy + 0.5) / shape.xy;
    if (control.y > 0.5) uv.y = 1.0 - uv.y;
    colorOut[id.xy] = float4(currentImage.SampleLevel(linearClamp, uv, 0).rgb, 1);
}

[numthreads(8, 8, 1)]
void Guides(uint3 id : SV_DispatchThreadID)
{
    if (any(id.xy >= (uint2)shape.xy)) return;
    float2 uv = (id.xy + 0.5) / shape.xy;
    uint2 cell = min((uint2)max(uv * shape.xy, 0) / 4, ((uint2)shape.xy + 3) / 4 - 1);
    float2 motion = blockFlow.Load(int3(cell, 0)) / 32.0;
    // Optical flow is ambiguous on flat fills and repeated UI strokes. Keep
    // unchanged neighborhoods anchored instead of applying a guessed vector.
    float stationaryError = 0;
    [unroll] for (int y = -1; y <= 1; y++)
    [unroll] for (int x = -1; x <= 1; x++)
    {
        int2 p = clamp(int2(id.xy) + int2(x, y), int2(0, 0), int2(shape.xy) - 1);
        float3 delta = abs(currentImage.Load(int3(p, 0)).rgb - previousImage.Load(int3(p, 0)).rgb);
        stationaryError = max(stationaryError, max(delta.r, max(delta.g, delta.b)));
    }
    bool stationary = stationaryError <= (1.0 / 255.0);
    if (stationary) motion = 0;
    float2 prevUv = uv + motion / shape.xy;
    float3 color = currentImage.Load(int3(id.xy, 0)).rgb;
    float3 prior = previousImage.SampleLevel(linearClamp, prevUv, 0).rgb;
    float error = max(abs(color.r - prior.r), max(abs(color.g - prior.g), abs(color.b - prior.b)));
    float cost = blockCost.Load(int3(cell, 0)) / 255.0;
    // No geometric depth exists in a captured frame. Use an explicit flat-depth
    // approximation and reject unreliable optical-flow history instead of inventing depth.
    float rejection = max(smoothstep(0.08, 0.3, error), smoothstep(0.15, 0.6, cost));
    if (stationary) rejection = 0;
    // Opposing gradients identify narrow bright/dark strokes, not broad edges.
    // Feed this into NGX's current-color bias and the output detail protection.
    float centerLuma = dot(color, float3(0.299, 0.587, 0.114));
    float left = centerLuma - LumaAt(int2(id.xy) + int2(-2, 0));
    float right = centerLuma - LumaAt(int2(id.xy) + int2(2, 0));
    float top = centerLuma - LumaAt(int2(id.xy) + int2(0, -2));
    float bottom = centerLuma - LumaAt(int2(id.xy) + int2(0, 2));
    float ridge = max(left * right > 0 ? min(abs(left), abs(right)) : 0,
                      top * bottom > 0 ? min(abs(top), abs(bottom)) : 0);
    rejection = max(rejection, control.z * smoothstep(0.025, 0.08, ridge));
    if (control.x > 0.5 || any(prevUv < 0) || any(prevUv > 1)) rejection = 1;
    motionOut[id.xy] = rejection >= 0.95 ? float2(0, 0) : motion;
    depthOut[id.xy] = 0.5;
    biasOut[id.xy] = rejection;
    colorOut[id.xy] = float4(color, 1);
}
