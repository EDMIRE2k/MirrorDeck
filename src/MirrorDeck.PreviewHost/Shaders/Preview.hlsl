Texture2D sourceTex : register(t0);
SamplerState pointSampler : register(s0);
SamplerState linearSampler : register(s1);

cbuffer Params : register(b0)
{
    float4 sourceTarget;
    float4 viewport;
    float4 colorA;
    float4 colorB;
    float4 processA;
    float4 processB;
};

struct VSOut
{
    float4 position : SV_Position;
    float2 uv : TEXCOORD0;
};

VSOut VSMain(uint vertexId : SV_VertexID)
{
    float2 positions[3] =
    {
        float2(-1.0, -1.0),
        float2(-1.0,  3.0),
        float2( 3.0, -1.0)
    };

    VSOut output;
    output.position = float4(positions[vertexId], 0.0, 1.0);
    output.uv = float2((positions[vertexId].x + 1.0) * 0.5, 1.0 - ((positions[vertexId].y + 1.0) * 0.5));
    return output;
}

float3 ReadTexel(float2 coord)
{
    float2 size = sourceTarget.xy;
    float2 clamped = clamp(floor(coord), float2(0.0, 0.0), size - 1.0);
    return sourceTex.SampleLevel(pointSampler, (clamped + 0.5) / size, 0).rgb;
}

float CubicWeight(float x)
{
    x = abs(x);
    return x <= 1.0
        ? ((1.5 * x - 2.5) * x * x + 1.0)
        : (x < 2.0 ? (((-0.5 * x + 2.5) * x - 4.0) * x + 2.0) : 0.0);
}

float Sinc(float x)
{
    if (abs(x) < 0.0001)
    {
        return 1.0;
    }

    x *= 3.14159265;
    return sin(x) / x;
}

float LanczosWeight(float x)
{
    x = abs(x);
    return x < 2.0 ? Sinc(x) * Sinc(x * 0.5) : 0.0;
}

float3 SampleBicubic(float2 uv)
{
    float2 coord = uv * sourceTarget.xy - 0.5;
    float2 baseCoord = floor(coord);
    float3 sum = float3(0.0, 0.0, 0.0);
    float weightSum = 0.0;

    [unroll]
    for (int yy = -1; yy <= 2; yy++)
    {
        float wy = CubicWeight(coord.y - (baseCoord.y + yy));
        [unroll]
        for (int xx = -1; xx <= 2; xx++)
        {
            float wx = CubicWeight(coord.x - (baseCoord.x + xx));
            float weight = wx * wy;
            sum += ReadTexel(baseCoord + float2(xx, yy)) * weight;
            weightSum += weight;
        }
    }

    return weightSum > 0.0001 ? sum / weightSum : sourceTex.SampleLevel(linearSampler, uv, 0).rgb;
}

float3 SampleLanczos(float2 uv)
{
    float2 coord = uv * sourceTarget.xy - 0.5;
    float2 baseCoord = floor(coord);
    float3 sum = float3(0.0, 0.0, 0.0);
    float weightSum = 0.0;

    [unroll]
    for (int yy = -1; yy <= 2; yy++)
    {
        float wy = LanczosWeight(coord.y - (baseCoord.y + yy));
        [unroll]
        for (int xx = -1; xx <= 2; xx++)
        {
            float wx = LanczosWeight(coord.x - (baseCoord.x + xx));
            float weight = wx * wy;
            sum += ReadTexel(baseCoord + float2(xx, yy)) * weight;
            weightSum += weight;
        }
    }

    return weightSum > 0.0001 ? sum / weightSum : sourceTex.SampleLevel(linearSampler, uv, 0).rgb;
}

// UPSCALER_IMPLEMENTATION

float3 SampleFiltered(float2 uv)
{
    float filter = processB.x;
    if (filter < 0.5)
    {
        return sourceTex.SampleLevel(pointSampler, uv, 0).rgb;
    }

    if (filter < 1.5)
    {
        return sourceTex.SampleLevel(linearSampler, uv, 0).rgb;
    }

    if (filter < 2.5)
    {
        return SampleBicubic(uv);
    }

    if (filter < 3.5) return SampleLanczos(uv);

    float3 bilinearColor = sourceTex.SampleLevel(linearSampler, uv, 0).rgb;
    // Spatial upscalers are bypassed at native size and when either axis shrinks.
    if (any(viewport.zw < sourceTarget.xy) || all(viewport.zw <= sourceTarget.xy) || processB.w <= 0.0)
        return bilinearColor;

    float3 scaled;
    if (filter < 4.5) scaled = SampleSharpBilinear(uv);
    else if (filter < 5.5) scaled = SampleEasu(uv);
    else if (filter < 6.5) scaled = SampleAnimeLineArt(uv);
    else return bilinearColor;
    return lerp(bilinearColor, scaled, saturate(processB.w));
}

float Bayer4(int2 p)
{
    int x = p.x & 3;
    int y = p.y & 3;
    int index = y * 4 + x;
    static const float values[16] =
    {
        0.0, 8.0, 2.0, 10.0,
        12.0, 4.0, 14.0, 6.0,
        3.0, 11.0, 1.0, 9.0,
        15.0, 7.0, 13.0, 5.0
    };
    return values[index];
}

float3 ApplyClean(float3 color, float2 uv)
{
    if (processB.z > 0.5)
    {
        return color;
    }

    float clean = saturate(colorB.w);
    float sharpness = saturate(colorB.z);
    if (clean <= 0.001 && sharpness <= 0.001)
    {
        return color;
    }

    float2 texel = 1.0 / sourceTarget.xy;
    float3 blur =
        sourceTex.SampleLevel(linearSampler, uv + float2(texel.x, 0.0), 0).rgb +
        sourceTex.SampleLevel(linearSampler, uv - float2(texel.x, 0.0), 0).rgb +
        sourceTex.SampleLevel(linearSampler, uv + float2(0.0, texel.y), 0).rgb +
        sourceTex.SampleLevel(linearSampler, uv - float2(0.0, texel.y), 0).rgb;
    blur *= 0.25;

    float3 detail = color - blur;
    float edge = saturate(max(max(abs(detail.r), abs(detail.g)), abs(detail.b)) * 6.0);
    float denoise = saturate(processA.x * clean) * (1.0 - edge);
    float edgeRestore = saturate(processA.y * clean);
    float sharpenAmount = sharpness * 0.85 + edgeRestore * 1.25;
    float3 denoised = lerp(color, blur, denoise);
    return denoised + detail * sharpenAmount * (0.35 + edge * 0.65);
}

float3 ApplyColor(float3 color)
{
    if (processB.z > 0.5)
    {
        return color;
    }

    if (processA.z > 0.5)
    {
        color = (color - (16.0 / 255.0)) * (255.0 / 219.0);
    }

    color = ((color - 0.5) * colorA.y) + 0.5 + colorA.x;
    float luma = dot(color, float3(0.299, 0.587, 0.114));
    color = luma + (color - luma) * colorA.z;

    if (abs(colorB.x) > 0.0001)
    {
        float y = dot(color, float3(0.299, 0.587, 0.114));
        float i = dot(color, float3(0.596, -0.274, -0.322));
        float q = dot(color, float3(0.211, -0.523, 0.312));
        float rotatedI = i * colorB.y - q * colorB.x;
        float rotatedQ = i * colorB.x + q * colorB.y;
        color.r = y + 0.956 * rotatedI + 0.621 * rotatedQ;
        color.g = y - 0.272 * rotatedI - 0.647 * rotatedQ;
        color.b = y - 1.106 * rotatedI + 1.703 * rotatedQ;
    }

    float gammaValue = max(colorA.w, 0.05);
    if (abs(gammaValue - 1.0) > 0.001)
    {
        color = pow(saturate(color), 1.0 / gammaValue);
    }

    return color;
}

float4 PSMain(VSOut input) : SV_Target
{
    float2 pixel = input.uv * sourceTarget.zw;
    if (pixel.x < viewport.x || pixel.y < viewport.y ||
        pixel.x >= viewport.x + viewport.z || pixel.y >= viewport.y + viewport.w)
    {
        return float4(0.0, 0.0, 0.0, 1.0);
    }

    float2 localUv = saturate((pixel - viewport.xy) / viewport.zw);
    if (processA.w > 0.5)
    {
        localUv.y = 1.0 - localUv.y;
    }

    float3 color = SampleFiltered(localUv);
    color = ApplyClean(color, localUv);
    color = ApplyColor(color);

    if (processB.z < 0.5 && processB.y > 0.5)
    {
        float dither = (Bayer4(int2(pixel)) - 7.5) / 255.0 * 0.55;
        color += dither;
    }

    return float4(saturate(color), 1.0);
}
