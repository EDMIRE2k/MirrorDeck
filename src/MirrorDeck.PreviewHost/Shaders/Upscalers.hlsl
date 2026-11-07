// AMD FidelityFX FSR 1 EASU. Upstream headers are embedded without modification.
// See Vendor/FSR-LICENSE.txt and THIRD-PARTY-UPSCALERS.txt.
#define A_GPU 1
#define A_HLSL 1
// FFX_A_IMPLEMENTATION
#define FSR_EASU_F 1
// FFX_FSR_IMPLEMENTATION

float4 FsrEasuRF(float2 p) { return sourceTex.GatherRed(pointSampler, p); }
float4 FsrEasuGF(float2 p) { return sourceTex.GatherGreen(pointSampler, p); }
float4 FsrEasuBF(float2 p) { return sourceTex.GatherBlue(pointSampler, p); }

float3 SampleEasu(float2 uv)
{
    float2 inverseSize = rcp(sourceTarget.xy);
    // EASU accepts an integer output coordinate. Supplying zero and an exact
    // source offset preserves fractional aspect-fit/fill viewports and bottom-up inputs.
    uint4 con0 = asuint(float4(1.0, 1.0, uv * sourceTarget.xy - 0.5));
    uint4 con1 = asuint(float4(inverseSize, inverseSize.x, -inverseSize.y));
    uint4 con2 = asuint(float4(-inverseSize.x, 2.0 * inverseSize.y, inverseSize.x, 2.0 * inverseSize.y));
    uint4 con3 = asuint(float4(0.0, 4.0 * inverseSize.y, 0.0, 0.0));
    float3 color;
    FsrEasuF(color, uint2(0, 0), con0, con1, con2, con3);
    return color;
}

float3 SampleSharpBilinear(float2 uv)
{
    float2 coord = uv * sourceTarget.xy;
    float2 integerScale = max(floor(viewport.zw / sourceTarget.xy), 1.0);
    float2 phase = saturate((frac(coord) - 0.5) * integerScale + 0.5);
    return sourceTex.SampleLevel(linearSampler, (floor(coord) + phase) / sourceTarget.xy, 0).rgb;
}

// Adapted from bloc97's MIT-licensed Anime4K v0.9 luminance push.
// This is only the lightweight line-thinning stage, not the full Anime4K pipeline.
// Luma is computed inline and the bounded correction is combined with EASU.
float4 LineSample(float2 uv)
{
    float3 rgb = sourceTex.SampleLevel(linearSampler, uv, 0).rgb;
    return float4(rgb, dot(rgb, float3(0.299, 0.587, 0.114)));
}

float MinLuma(float4 a, float4 b, float4 c) { return min(a.w, min(b.w, c.w)); }
float MaxLuma(float4 a, float4 b, float4 c) { return max(a.w, max(b.w, c.w)); }

void PushLine(float4 center, inout float4 best, float4 a, float4 b, float4 c)
{
    float4 candidate = lerp(center, (a + b + c) / 3.0, 0.3);
    if (candidate.w > best.w) best = candidate;
}

float3 SampleAnimeLineArt(float2 uv)
{
    float2 stepSize = rcp(viewport.zw);
    float4 cc = LineSample(uv);
    float4 t = LineSample(uv + float2(0, -stepSize.y));
    float4 tl = LineSample(uv - stepSize);
    float4 tr = LineSample(uv + float2(stepSize.x, -stepSize.y));
    float4 l = LineSample(uv - float2(stepSize.x, 0));
    float4 r = LineSample(uv + float2(stepSize.x, 0));
    float4 b = LineSample(uv + float2(0, stepSize.y));
    float4 bl = LineSample(uv + float2(-stepSize.x, stepSize.y));
    float4 br = LineSample(uv + stepSize);
    float4 best = cc;
    const float threshold = 1.0 / 255.0;

    if (MinLuma(tl, t, tr) > max(cc.w, MaxLuma(br, b, bl)) + threshold)
        PushLine(cc, best, tl, t, tr);
    else if (MinLuma(br, b, bl) > max(cc.w, MaxLuma(tl, t, tr)) + threshold)
        PushLine(cc, best, br, b, bl);
    if (MinLuma(r, t, tr) > MaxLuma(cc, l, b) + threshold)
        PushLine(cc, best, r, t, tr);
    else if (MinLuma(bl, l, b) > MaxLuma(cc, r, t) + threshold)
        PushLine(cc, best, bl, l, b);
    if (MinLuma(r, br, tr) > max(cc.w, MaxLuma(l, tl, bl)) + threshold)
        PushLine(cc, best, r, br, tr);
    else if (MinLuma(l, tl, bl) > max(cc.w, MaxLuma(r, br, tr)) + threshold)
        PushLine(cc, best, l, tl, bl);
    if (MinLuma(r, br, b) > MaxLuma(cc, l, t) + threshold)
        PushLine(cc, best, r, br, b);
    else if (MinLuma(t, l, tl) > MaxLuma(cc, r, b) + threshold)
        PushLine(cc, best, t, l, tl);

    float3 low = min(cc.rgb, min(min(t.rgb, b.rgb), min(l.rgb, r.rgb)));
    float3 high = max(cc.rgb, max(max(t.rgb, b.rgb), max(l.rgb, r.rgb)));
    return clamp(SampleEasu(uv) + best.rgb - cc.rgb, low, high);
}
