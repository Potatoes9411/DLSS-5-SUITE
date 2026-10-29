// Draws the processed frame into the swap chain, with optional comparison views.
#include "Common.hlsli"

cbuffer Constants : register(b0)
{
    uint   g_Mode;       // 0: processed, 1: original, 2: split (original left, processed right)
    float  g_Split;      // split position in [0,1]
    float  g_Blend;      // frame generation: where between the history (0) and the processed picture (1) to draw
    uint   g_HasHistory; // 1 when t2 and t3 hold the history and the motion to draw between them
};

Texture2D<float4> g_Processed : register(t0);
Texture2D<float4> g_Original  : register(t1);
Texture2D<float4> g_History   : register(t2); // the processed picture before the current one
Texture2D<float2> g_Motion    : register(t3); // per pixel, current -> previous, in the motion texture's pixels

struct VsOut
{
    float4 position : SV_Position;
    float2 uv       : TEXCOORD0;
};

VsOut VS_Blit(uint vertexId : SV_VertexID)
{
    VsOut o;
    float2 uv = float2((vertexId << 1) & 2, vertexId & 2);
    o.position = float4(uv * float2(2.0, -2.0) + float2(-1.0, 1.0), 0.0, 1.0);
    o.uv = uv;
    return o;
}

// A picture between the history and the processed one: each is fetched from where the content at this pixel
// sits in it, the processed one a (1 - t) share of the motion back and the history a t share of it forward.
float3 Interpolated(float2 uv)
{
    float width, height;
    g_Motion.GetDimensions(width, height);
    float2 mv = g_Motion.SampleLevel(g_LinearClamp, uv, 0).xy / float2(width, height);
    float3 current = g_Processed.SampleLevel(g_LinearClamp, uv - mv * (1.0 - g_Blend), 0).rgb;
    float3 previous = g_History.SampleLevel(g_LinearClamp, uv + mv * g_Blend, 0).rgb;
    return lerp(previous, current, g_Blend);
}

float3 ProcessedAt(float2 uv)
{
    if (g_HasHistory != 0u && g_Blend < 1.0)
        return Interpolated(uv);
    return g_Processed.SampleLevel(g_LinearClamp, uv, 0).rgb;
}

float4 PS_Blit(VsOut i) : SV_Target
{
    float3 processed = ProcessedAt(i.uv);
    if (g_Mode == 0u)
        return float4(processed, 1.0);

    float3 original = g_Original.SampleLevel(g_LinearClamp, i.uv, 0).rgb;
    if (g_Mode == 1u)
        return float4(original, 1.0);

    if (abs(i.uv.x - g_Split) < 0.0008)
        return float4(1.0, 0.8, 0.0, 1.0);
    return float4(i.uv.x < g_Split ? original : processed, 1.0);
}
