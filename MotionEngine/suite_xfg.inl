// suite_xfg.inl - DLSS 5 SUITE: more frame generators, shared by Motion Forge (generators 5..10) and the Screen
// Engine (--frame-gen-method repeat|crossfade|memc|dis|blur|spacewarp). Written for SUITE from scratch; nothing in
// it is taken from another project's code.
//
// Six ways to make the frames between two real ones, each a set of compute passes on the card (any D3D12 card):
//   Repeat     the nearer real frame again - what "no frame generation" looks like at the same frame rate, for
//              comparison.
//   Crossfade  the two real frames mixed at the moment - the oldest trick, smooth in brightness, doubled edges.
//   MEMC       motion-compensated interpolation as a television's "motion smoothing" does it: the picture cut into
//              8x8 blocks, each block's motion found by block matching on a pyramid of the picture (a full search
//              at the coarsest size, then the coarser size's answer and its neighbours' tried and refined to a
//              quarter of a pixel, then two rounds of each block taking its neighbours' motion when it fits better
//              - a parallel form of 3D recursive search), both ways (before -> after and after -> before). Each
//              frame in between is then drawn from overlapping blocks' motions (see Synth below).
//   DIS        dense optical flow in the manner of Kroeger et al.'s Dense Inverse Search (ECCV 2016): on each
//              size of the pyramid, coarse to fine, 8x8 patches every 4 pixels each find their motion by inverse
//              compositional Lucas-Kanade from the coarser size's flow, and every pixel's flow is the patches over
//              it weighted by how well each fits there; a 3x3 median at the end. A motion for every pixel.
//   Blur       DIS's motion, and each frame in between drawn as a camera with a half-open shutter would have seen
//              it: five moments around its own, averaged along the motion - a cinematic look, softer judder.
//   Spacewarp  extrapolation, as VR runtimes' spacewarp: the frames after the newest real frame are drawn from it
//              alone, moved on along the motion of the last pair. Nothing waits for the next real frame, so the
//              picture is a frame of the game sooner; where something comes into view it is guessed (stretched).
//
// Synth (every flow-based mode): for each pixel of a frame at moment t, fifteen candidate motions - the before ->
// after motion at the pixel and its eight neighbours in the field, the after -> before motion (reversed) at it and
// its four neighbours, and none. Each is checked both ways: the place it comes from in the frame before must itself
// move so (else that place is hidden by then), and the place it goes to in the frame after must have come so (else
// it was just uncovered). Seen in both: the two mixed at t, judged by how alike they are; seen in one: that one; in
// neither: a poor guess. The candidates are mixed by a soft minimum of those costs - overlapped block motion
// compensation with occlusion handling.
//
// The motion is looked for at "Flow"'s share of the picture (Motion Forge's Flow, the Screen Engine's
// --forge-flow), the frames are drawn at the picture's own size. Fields are kept in shares of the picture, so every
// size of the pyramid and the frames read them alike.

static const char kXfgHlsl[] = R"hlsl(
cbuffer K : register(b0) { uint4 ka; float4 kb; float4 kc; float4 kd; };
Texture2D<float4> FA : register(t0);   // the real frame before
Texture2D<float4> FB : register(t1);   // the real frame after
Texture2D<float> LA : register(t2);    // their brightness, at one size of the pyramid
Texture2D<float> LB : register(t3);
Texture2D<float2> F0 : register(t4);   // motion fields (shares of the picture)
Texture2D<float2> F1 : register(t5);
RWTexture2D<float> UL0 : register(u0);
RWTexture2D<float> UL1 : register(u1);
RWTexture2D<float2> UF0 : register(u2);
RWTexture2D<float2> UF1 : register(u3);
RWTexture2D<float4> UOut : register(u4);
SamplerState Lin : register(s0);

float Luma(float3 c) { return saturate(dot(c, float3(0.299, 0.587, 0.114))); }

// The pyramid's first size. ka.xy: its size; kb.x: taps across a pixel of it.
[numthreads(8, 8, 1)]
void LumaMain(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= ka.x || id.y >= ka.y) return;
    const int n = clamp((int)kb.x, 1, 4);
    const float2 size = float2(ka.xy);
    float sa = 0, sb = 0;
    for (int j = 0; j < n; ++j)
        for (int i = 0; i < n; ++i)
        {
            const float2 uv = (float2(id.xy) + (float2(i, j) + 0.5) / n) / size;
            sa += Luma(FA.SampleLevel(Lin, uv, 0).rgb);
            sb += Luma(FB.SampleLevel(Lin, uv, 0).rgb);
        }
    UL0[id.xy] = sa / (n * n);
    UL1[id.xy] = sb / (n * n);
}

// Half the size. ka.xy: the smaller size.
[numthreads(8, 8, 1)]
void DownMain(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= ka.x || id.y >= ka.y) return;
    const float2 uv = (float2(id.xy) + 0.5) / float2(ka.xy);
    UL0[id.xy] = LA.SampleLevel(Lin, uv, 0);
    UL1[id.xy] = LB.SampleLevel(Lin, uv, 0);
}

// One size of the pyramid, ka.xy its size. rev: after -> before (the frame after is the template).
float Tpl(bool rev, int2 x)
{
    x = clamp(x, int2(0, 0), int2(ka.xy) - 1);
    return rev ? LB.Load(int3(x, 0)) : LA.Load(int3(x, 0));
}
float Tgt(bool rev, float2 p)
{
    const float2 uv = p / float2(ka.xy);
    return rev ? LA.SampleLevel(Lin, uv, 0) : LB.SampleLevel(Lin, uv, 0);
}
float2 Field(bool rev, float2 uv) { return rev ? F1.SampleLevel(Lin, uv, 0) : F0.SampleLevel(Lin, uv, 0); }
float2 FieldAt(bool rev, int2 cell, int2 cells)
{
    cell = clamp(cell, int2(0, 0), cells - 1);
    return rev ? F1.Load(int3(cell, 0)) : F0.Load(int3(cell, 0));
}
void Put(bool rev, uint2 at, float2 v) { if (rev) UF1[at] = v; else UF0[at] = v; }

// --- DIS ---------------------------------------------------------------------------------------------------

// A patch's misfit at motion v (pixels): the mean squared difference, each patch's mean brightness taken out.
float PatchErr(bool rev, int2 o, float2 v, float mt)
{
    float mi = 0;
    [loop] for (int k = 0; k < 64; ++k) mi += Tgt(rev, float2(o + int2(k & 7, k >> 3)) + 0.5 + v);
    mi /= 64.0;
    float e = 0;
    [loop] for (int k2 = 0; k2 < 64; ++k2)
    {
        const int2 x = o + int2(k2 & 7, k2 >> 3);
        const float d = (Tgt(rev, float2(x) + 0.5 + v) - mi) - (Tpl(rev, x) - mt);
        e += d * d;
    }
    return e / 64.0;
}

// Each patch (8x8, every 4 pixels) finds its motion. ka.xy: the size; ka.zw: patches across, down; kb.x: 1 with a
// coarser flow to start from (F0 before -> after, F1 after -> before); kb.y: rounds. z: 0 before -> after, 1 back.
[numthreads(8, 8, 1)]
void PatchMain(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= ka.z || id.y >= ka.w) return;
    const bool rev = id.z == 1;
    const float2 size = float2(ka.xy);
    const int2 o = max(min(int2(id.xy) * 4, int2(ka.xy) - 8), int2(0, 0));
    float2 v = 0;
    if (kb.x > 0.5) v = Field(rev, (float2(o) + 4.0) / size) * size;
    float h11 = 1e-6, h12 = 0, h22 = 1e-6, mt = 0;
    [loop] for (int k = 0; k < 64; ++k)
    {
        const int2 x = o + int2(k & 7, k >> 3);
        const float gx = 0.5 * (Tpl(rev, x + int2(1, 0)) - Tpl(rev, x - int2(1, 0)));
        const float gy = 0.5 * (Tpl(rev, x + int2(0, 1)) - Tpl(rev, x - int2(0, 1)));
        h11 += gx * gx; h12 += gx * gy; h22 += gy * gy;
        mt += Tpl(rev, x);
    }
    mt /= 64.0;
    const float2 v0 = v;
    const float det = h11 * h22 - h12 * h12;
    if (det > 1e-9)
    {
        const float inv = 1.0 / det;
        const int rounds = (int)kb.y;
        [loop] for (int r = 0; r < rounds; ++r)
        {
            float mi = 0;
            [loop] for (int k1 = 0; k1 < 64; ++k1) mi += Tgt(rev, float2(o + int2(k1 & 7, k1 >> 3)) + 0.5 + v);
            mi /= 64.0;
            float b1 = 0, b2 = 0;
            [loop] for (int k2 = 0; k2 < 64; ++k2)
            {
                const int2 x = o + int2(k2 & 7, k2 >> 3);
                const float gx = 0.5 * (Tpl(rev, x + int2(1, 0)) - Tpl(rev, x - int2(1, 0)));
                const float gy = 0.5 * (Tpl(rev, x + int2(0, 1)) - Tpl(rev, x - int2(0, 1)));
                const float d = (Tgt(rev, float2(x) + 0.5 + v) - mi) - (Tpl(rev, x) - mt);
                b1 += gx * d; b2 += gy * d;
            }
            const float2 dv = inv * float2(h22 * b1 - h12 * b2, h11 * b2 - h12 * b1);
            v -= clamp(dv, -4.0, 4.0);
            if (dot(dv, dv) < 1e-4) break;
        }
    }
    // (As DIS does: a patch that came out worse than it started keeps where it started.)
    if (any(isnan(v)) || PatchErr(rev, o, v, mt) > PatchErr(rev, o, v0, mt)) v = v0;
    Put(rev, id.xy, v / size);
}

// Every pixel's flow from the patches over it. ka.xy: the size; ka.zw: patches across, down; F0, F1: the patches'.
[numthreads(8, 8, 1)]
void DensifyMain(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= ka.x || id.y >= ka.y) return;
    const bool rev = id.z == 1;
    const float2 size = float2(ka.xy);
    const int2 x = int2(id.xy);
    const int2 cells = int2(ka.zw);
    const float i0 = Tpl(rev, x);
    float2 sum = 0;
    float ws = 0;
    const int2 p0 = x / 4;
    [unroll] for (int j = -2; j <= 0; ++j)
        [unroll] for (int i = -2; i <= 0; ++i)
        {
            const int2 p = p0 + int2(i, j);
            if (any(p < 0) || any(p >= cells)) continue;
            const int2 o = max(min(p * 4, int2(ka.xy) - 8), int2(0, 0));
            if (any(x < o) || any(x >= o + 8)) continue;
            const float2 u = FieldAt(rev, p, cells);
            const float d = abs(Tgt(rev, float2(x) + 0.5 + u * size) - i0);
            const float w = 1.0 / max(d, 0.01);
            sum += w * u;
            ws += w;
        }
    if (ws <= 0) { sum = FieldAt(rev, clamp(p0, int2(0, 0), cells - 1), cells); ws = 1; }
    Put(rev, id.xy, sum / ws);
}

float Med9(float v[9])
{
    // (A partial sort: after four rounds the middle one is in place.)
    [unroll] for (int i = 0; i < 5; ++i)
        [unroll] for (int j = i + 1; j < 9; ++j)
        {
            const float a = min(v[i], v[j]), b = max(v[i], v[j]);
            v[i] = a; v[j] = b;
        }
    return v[4];
}

// A 3x3 median of both fields. ka.xy: their size.
[numthreads(8, 8, 1)]
void MedianMain(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= ka.x || id.y >= ka.y) return;
    const bool rev = id.z == 1;
    const int2 cells = int2(ka.xy);
    float xs[9], ys[9];
    [unroll] for (int k = 0; k < 9; ++k)
    {
        const float2 u = FieldAt(rev, int2(id.xy) + int2(k % 3 - 1, k / 3 - 1), cells);
        xs[k] = u.x; ys[k] = u.y;
    }
    Put(rev, id.xy, float2(Med9(xs), Med9(ys)));
}

// --- MEMC ---------------------------------------------------------------------------------------------------

// A block's misfit at motion v (pixels): mean absolute difference over 12x12 around the 8x8 block (overlapped).
float Sad(bool rev, int2 o, float2 v)
{
    float s = 0;
    [loop] for (int j = -2; j < 10; ++j)
        [loop] for (int i = -2; i < 10; ++i)
        {
            const int2 x = o + int2(i, j);
            s += abs(Tgt(rev, float2(x) + 0.5 + v) - Tpl(rev, x));
        }
    return s / 144.0;
}

// The block alone (8x8), for the full search at the coarsest size, where it is tried at every place in reach.
float SadBlock(bool rev, int2 o, float2 v)
{
    float s = 0;
    [loop] for (int j = 0; j < 8; ++j)
        [loop] for (int i = 0; i < 8; ++i)
        {
            const int2 x = o + int2(i, j);
            s += abs(Tgt(rev, float2(x) + 0.5 + v) - Tpl(rev, x));
        }
    return s / 64.0;
}

float BlockCost(bool rev, int2 o, float2 v, float2 pred)
{
    const float2 d = abs(v - pred);
    // (kb.z: what a pixel away from the prediction costs; kb.w: none is a little preferred - what stands still.)
    return Sad(rev, o, v) + kb.z * (d.x + d.y) - (all(v == 0) ? kb.w : 0.0);
}

void Refine(bool rev, int2 o, float2 pred, inout float2 best, inout float bc, float step)
{
    const float2 centre = best;
    [loop] for (int k = 0; k < 9; ++k)
    {
        if (k == 4) continue;
        const float2 v = centre + float2(k % 3 - 1, k / 3 - 1) * step;
        const float c = BlockCost(rev, o, v, pred);
        if (c < bc) { bc = c; best = v; }
    }
}

// Each 8x8 block's motion. ka.xy: the size; ka.zw: blocks across, down; kb.x: 1 with the coarser size's blocks
// (F0, F1); kb.y: the full search's reach without them, pixels. z: 0 before -> after, 1 back.
[numthreads(8, 8, 1)]
void BlockMain(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= ka.z || id.y >= ka.w) return;
    const bool rev = id.z == 1;
    const float2 size = float2(ka.xy);
    const int2 o = int2(id.xy) * 8;
    const float2 c = (float2(o) + 4.0) / size;
    float2 pred = 0, best = 0;
    float bc = 1e9;
    if (kb.x > 0.5)
    {
        const float2 step = 16.0 / size;
        pred = Field(rev, c) * size;
        const float2 cands[6] = { pred, Field(rev, c + float2(step.x, 0)) * size, Field(rev, c - float2(step.x, 0)) * size,
                                  Field(rev, c + float2(0, step.y)) * size, Field(rev, c - float2(0, step.y)) * size, float2(0, 0) };
        [unroll] for (int k = 0; k < 6; ++k)
        {
            const float cost = BlockCost(rev, o, cands[k], pred);
            if (cost < bc) { bc = cost; best = cands[k]; }
        }
    }
    else
    {
        const int reach = (int)kb.y;
        [loop] for (int y = -reach; y <= reach; ++y)
            [loop] for (int x = -reach; x <= reach; ++x)
            {
                const float cost = SadBlock(rev, o, float2(x, y)) + kb.z * (abs(x) + abs(y)) - (x == 0 && y == 0 ? kb.w : 0.0);
                if (cost < bc) { bc = cost; best = float2(x, y); }
            }
    }
    Refine(rev, o, pred, best, bc, 1.0);
    Refine(rev, o, pred, best, bc, 0.5);
    Refine(rev, o, pred, best, bc, 0.25);
    Put(rev, id.xy, best / size);
}

// A round of blocks taking a neighbour's motion where it fits better; the median of the nine is what they are
// held to. ka.xy: the size; ka.zw: blocks; F0, F1: the blocks' motion so far.
[numthreads(8, 8, 1)]
void BlockSpreadMain(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= ka.z || id.y >= ka.w) return;
    const bool rev = id.z == 1;
    const float2 size = float2(ka.xy);
    const int2 cells = int2(ka.zw);
    const int2 o = int2(id.xy) * 8;
    float2 n[9];
    float xs[9], ys[9];
    [unroll] for (int k = 0; k < 9; ++k)
    {
        n[k] = FieldAt(rev, int2(id.xy) + int2(k % 3 - 1, k / 3 - 1), cells) * size;
        xs[k] = n[k].x; ys[k] = n[k].y;
    }
    const float2 pred = float2(Med9(xs), Med9(ys));
    float2 best = n[4];
    float bc = BlockCost(rev, o, best, pred);
    [loop] for (int k2 = 0; k2 < 9; ++k2)
    {
        if (k2 == 4) continue;
        const float cost = BlockCost(rev, o, n[k2], pred);
        if (cost < bc) { bc = cost; best = n[k2]; }
    }
    Refine(rev, o, pred, best, bc, 0.25);
    Put(rev, id.xy, best / size);
}

// --- the frames ---------------------------------------------------------------------------------------------

// ka.xy: the frame's size; ka.zw: the fields' size; kb.x: the moment t; kb.y: 0 between, 1 blurred between,
// 2 after the frame after (extrapolated, t past it), 3 the nearer frame, 4 the two mixed; kb.zw: the size the
// motion was found at (its pixels, for how far apart two motions are); kc.x: the blur's span, in moments;
// kc.y: how far apart two motions may be and still be one, those pixels; kc.z: the soft minimum's width.

float3 ColA(float2 uv) { return FA.SampleLevel(Lin, uv, 0).rgb; }
float3 ColB(float2 uv) { return FB.SampleLevel(Lin, uv, 0).rgb; }
bool Inside(float2 uv) { return all(uv >= 0.0) && all(uv <= 1.0); }

// What candidate motion m (before -> after, shares) shows at p at moment t, and what that costs.
float3 Eval(float2 p, float2 m, float t, out float cost)
{
    const float2 pa = p - t * m, pb = p + (1.0 - t) * m;
    const float3 a = ColA(pa), b = ColB(pb);
    const float2 work = kb.zw;
    const float s2 = 2.0 * kc.y * kc.y;
    const float2 da = (F0.SampleLevel(Lin, pa, 0) - m) * work;
    const float2 db = (-F1.SampleLevel(Lin, pb, 0) - m) * work;
    const float va = Inside(pa) ? exp(-dot(da, da) / s2) : 0.0;
    const float vb = Inside(pb) ? exp(-dot(db, db) / s2) : 0.0;
    const float both = va * vb, onlyA = va * (1.0 - vb), onlyB = vb * (1.0 - va), none = (1.0 - va) * (1.0 - vb);
    const float3 mixed = lerp(a, b, t);
    const float e = dot(abs(a - b), 1.0 / 3.0);
    cost = both * e + (onlyA + onlyB) * 0.04 + none * (0.12 + e);
    return both * mixed + onlyA * a + onlyB * b + none * mixed;
}

[numthreads(8, 8, 1)]
void SynthMain(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= ka.x || id.y >= ka.y) return;
    const float2 p = (float2(id.xy) + 0.5) / float2(ka.xy);
    const float t = kb.x;
    const int mode = (int)kb.y;
    if (mode == 3) { UOut[id.xy] = float4(t < 0.5 ? ColA(p) : ColB(p), 1.0); return; }
    if (mode == 4) { UOut[id.xy] = float4(lerp(ColA(p), ColB(p), t), 1.0); return; }
    const int2 cells = int2(ka.zw);
    const int2 cell = clamp(int2(p * float2(cells)), int2(0, 0), cells - 1);
    const float width = max(kc.z, 1e-4);
    if (mode == 2)
    {
        // After the frame after: from it alone, along its own motion (the reverse of after -> before).
        float2 cands[10];
        cands[0] = -F1.SampleLevel(Lin, p, 0);
        [unroll] for (int k = 0; k < 9; ++k) cands[k + 1] = k == 4 ? float2(0, 0) : -FieldAt(true, cell + int2(k % 3 - 1, k / 3 - 1), cells);
        float costs[10];
        float least = 1e9;
        [unroll] for (int c = 0; c < 10; ++c)
        {
            const float2 q = p - t * cands[c];
            const float2 d = (-F1.SampleLevel(Lin, q, 0) - cands[c]) * kb.zw;
            costs[c] = sqrt(dot(d, d)) + (Inside(q) ? 0.0 : 8.0);
            least = min(least, costs[c]);
        }
        float3 sum = 0;
        float ws = 0;
        [unroll] for (int c2 = 0; c2 < 10; ++c2)
        {
            const float w = exp(-(costs[c2] - least) / 0.5);
            sum += w * ColB(p - t * cands[c2]);
            ws += w;
        }
        UOut[id.xy] = float4(sum / ws, 1.0);
        return;
    }
    float2 cands[15];
    cands[0] = F0.SampleLevel(Lin, p, 0);
    cands[1] = -F1.SampleLevel(Lin, p, 0);
    cands[2] = float2(0, 0);
    [unroll] for (int k = 0; k < 8; ++k)
    {
        const int at = k < 4 ? k : k + 1;
        cands[3 + k] = FieldAt(false, cell + int2(at % 3 - 1, at / 3 - 1), cells);
    }
    cands[11] = -FieldAt(true, cell + int2(1, 0), cells);
    cands[12] = -FieldAt(true, cell + int2(-1, 0), cells);
    cands[13] = -FieldAt(true, cell + int2(0, 1), cells);
    cands[14] = -FieldAt(true, cell + int2(0, -1), cells);
    float3 cols[15];
    float costs[15];
    float least = 1e9;
    [unroll] for (int c = 0; c < 15; ++c)
    {
        cols[c] = Eval(p, cands[c], t, costs[c]);
        least = min(least, costs[c]);
    }
    float3 sum = 0;
    float2 motion = 0;
    float ws = 0;
    [unroll] for (int c2 = 0; c2 < 15; ++c2)
    {
        const float w = exp(-(costs[c2] - least) / width);
        sum += w * cols[c2];
        motion += w * cands[c2];
        ws += w;
    }
    if (mode == 1)
    {
        // The motion found, and five moments around t along it.
        const float2 m = motion / ws;
        float3 blur = 0;
        [unroll] for (int s = 0; s < 5; ++s)
        {
            float unused;
            blur += Eval(p, m, saturate(t + (s / 4.0 - 0.5) * kc.x), unused);
        }
        UOut[id.xy] = float4(lerp(sum / ws, blur / 5.0, 0.75), 1.0);
        return;
    }
    UOut[id.xy] = float4(sum / ws, 1.0);
}
)hlsl";

namespace xfg
{
    enum class Mode : int { Repeat = 0, Crossfade = 1, Memc = 2, Dis = 3, Blur = 4, Spacewarp = 5 };
    static constexpr int kModes = 6;
    static constexpr UINT kLevels = 6;
    static constexpr UINT kTables = 1024;    // descriptor tables used in turn (a pair takes about twenty)
    static constexpr UINT kSlots = 13;       // a table: 6 read, 5 written (and two spare)
    enum Pass { kLuma, kDown, kPatch, kDensify, kMedian, kBlock, kSpread, kSynth, kPasses };
    static const char *const kEntries[kPasses] = { "LumaMain", "DownMain", "PatchMain", "DensifyMain", "MedianMain", "BlockMain", "BlockSpreadMain", "SynthMain" };

    static const char *NameOf(Mode mode)
    {
        switch (mode)
        {
        case Mode::Repeat: return "Repeat";
        case Mode::Crossfade: return "Crossfade";
        case Mode::Memc: return "SUITE MEMC (block matching)";
        case Mode::Dis: return "SUITE DIS (dense optical flow)";
        case Mode::Blur: return "Motion-blur blend";
        case Mode::Spacewarp: return "SUITE Spacewarp (extrapolation)";
        }
        return "SUITE extra";
    }
    static bool NeedsMotion(Mode mode) { return mode != Mode::Repeat && mode != Mode::Crossfade; }
    static bool UsesBlocks(Mode mode) { return mode == Mode::Memc; }
    // Spacewarp's frames come after the newest real frame, not between it and the one before.
    static bool Extrapolates(Mode mode) { return mode == Mode::Spacewarp; }

    struct Tex
    {
        winrt::com_ptr<ID3D12Resource> r;
        D3D12_RESOURCE_STATES s = D3D12_RESOURCE_STATE_COMMON;
        UINT w = 0, h = 0;
    };

    static struct State
    {
        winrt::com_ptr<ID3D12RootSignature> root;
        winrt::com_ptr<ID3D12PipelineState> pso[kPasses];
        winrt::com_ptr<ID3D12DescriptorHeap> heap;
        UINT stride = 0, next = 0;
        bool failed = false;
        Mode mode = Mode::Dis;
        UINT frame_w = 0, frame_h = 0, work_w = 0, work_h = 0, levels = 0;
        Tex la[kLevels], lb[kLevels];                 // the pyramid
        Tex fab[kLevels], fba[kLevels];               // DIS: each size's flow; MEMC: each size's blocks
        Tex pab[kLevels], pba[kLevels];               // DIS: each size's patches; MEMC: the spreading's other half
        Tex mab, mba;                                 // DIS: the median's
        unsigned pairs = 0;
    } g;

    static bool Compiled()
    {
        if (g.root) return true;
        winrt::com_ptr<ID3DBlob> codes[kPasses];
        for (int pass = 0; pass < kPasses; ++pass)
        {
            winrt::com_ptr<ID3DBlob> errors;
            if (FAILED(CachedCompile(kXfgHlsl, sizeof(kXfgHlsl) - 1, "suite-xfg.hlsl", nullptr, nullptr, kEntries[pass], "cs_5_0", D3DCOMPILE_OPTIMIZATION_LEVEL3, 0,
                                     codes[pass].put(), errors.put())))
            {
                Trace("[xfg] %s did not compile: %s", kEntries[pass], errors ? static_cast<const char *>(errors->GetBufferPointer()) : "(no log)");
                return false;
            }
        }
        D3D12_DESCRIPTOR_RANGE ranges[2] = { { D3D12_DESCRIPTOR_RANGE_TYPE_SRV, 6, 0, 0, 0 }, { D3D12_DESCRIPTOR_RANGE_TYPE_UAV, 5, 0, 0, 6 } };
        D3D12_ROOT_PARAMETER params[2] = {};
        params[0].ParameterType = D3D12_ROOT_PARAMETER_TYPE_32BIT_CONSTANTS;
        params[0].Constants.Num32BitValues = 16;
        params[1].ParameterType = D3D12_ROOT_PARAMETER_TYPE_DESCRIPTOR_TABLE;
        params[1].DescriptorTable = { 2, ranges };
        D3D12_STATIC_SAMPLER_DESC sampler = {};
        sampler.Filter = D3D12_FILTER_MIN_MAG_MIP_LINEAR;
        sampler.AddressU = sampler.AddressV = sampler.AddressW = D3D12_TEXTURE_ADDRESS_MODE_CLAMP;
        sampler.MaxAnisotropy = 1;
        sampler.ComparisonFunc = D3D12_COMPARISON_FUNC_ALWAYS;
        sampler.MaxLOD = D3D12_FLOAT32_MAX;
        const D3D12_ROOT_SIGNATURE_DESC desc = { 2, params, 1, &sampler, D3D12_ROOT_SIGNATURE_FLAG_NONE };
        winrt::com_ptr<ID3DBlob> signature, errors;
        winrt::com_ptr<ID3D12RootSignature> root;
        if (FAILED(D3D12SerializeRootSignature(&desc, D3D_ROOT_SIGNATURE_VERSION_1, signature.put(), errors.put())) ||
            FAILED(lfCore.device->CreateRootSignature(0, signature->GetBufferPointer(), signature->GetBufferSize(), IID_PPV_ARGS(root.put()))))
        { Trace("[xfg] its root signature was refused"); return false; }
        for (int pass = 0; pass < kPasses; ++pass)
        {
            D3D12_COMPUTE_PIPELINE_STATE_DESC pso = {};
            pso.pRootSignature = root.get();
            pso.CS = { codes[pass]->GetBufferPointer(), codes[pass]->GetBufferSize() };
            if (FAILED(lfCore.device->CreateComputePipelineState(&pso, IID_PPV_ARGS(g.pso[pass].put()))))
            {
                Trace("[xfg] the pipeline of %s was refused", kEntries[pass]);
                for (auto &p : g.pso) p = nullptr;
                return false;
            }
        }
        D3D12_DESCRIPTOR_HEAP_DESC heap = {};
        heap.Type = D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV;
        heap.NumDescriptors = kTables * kSlots;
        heap.Flags = D3D12_DESCRIPTOR_HEAP_FLAG_SHADER_VISIBLE;
        if (FAILED(lfCore.device->CreateDescriptorHeap(&heap, IID_PPV_ARGS(g.heap.put()))))
        {
            Trace("[xfg] no descriptor heap");
            for (auto &p : g.pso) p = nullptr;
            return false;
        }
        g.stride = lfCore.device->GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV);
        g.root = root;
        return true;
    }

    static bool Made(Tex &t, UINT w, UINT h, DXGI_FORMAT format)
    {
        t.r.attach(NewTexture(w, h, format, true));
        t.s = D3D12_RESOURCE_STATE_COMMON;
        t.w = w; t.h = h;
        return t.r != nullptr;
    }

    static void Release()
    {
        for (UINT l = 0; l < kLevels; ++l)
        {
            g.la[l] = {}; g.lb[l] = {}; g.fab[l] = {}; g.fba[l] = {}; g.pab[l] = {}; g.pba[l] = {};
        }
        g.mab = {}; g.mba = {};
        g.levels = 0;
        g.frame_w = g.frame_h = 0;
    }

    static void Close()
    {
        Release();
        for (auto &p : g.pso) p = nullptr;
        g.root = nullptr; g.heap = nullptr;
        g.next = 0; g.failed = false; g.pairs = 0;
    }

    static void Forget() {}

    // The size the motion is looked for at: Flow's share of the frame, at least 160 pixels across (all of it when
    // the frame is smaller), whole pixels.
    static void WorkSize(UINT w, UINT h, int flow, UINT &ww, UINT &wh)
    {
        const double share = std::clamp(flow, 10, 100) / 100.0;
        ww = std::min(w, std::max(160u, static_cast<UINT>(std::lround(w * share))));
        wh = std::max(1u, static_cast<UINT>(std::lround(double(h) * ww / std::max(1u, w))));
    }

    static UINT PatchesAcross(UINT size) { return size <= 8 ? 1u : (size - 8 + 3) / 4 + 1; }
    static UINT BlocksAcross(UINT size) { return (size + 7) / 8; }

    static bool Live(Mode mode, UINT w, UINT h, int flow)
    {
        UINT ww = 0, wh = 0;
        WorkSize(w, h, flow, ww, wh);
        return g.root && g.mode == mode && g.frame_w == w && g.frame_h == h && g.work_w == ww && g.work_h == wh;
    }

    // Made ready for frames of w x h: its passes compiled and what it works in made. False, with the reason in the
    // log, when the card refuses any of it.
    static bool Ready(Mode mode, UINT w, UINT h, int flow)
    {
        if (Live(mode, w, h, flow)) return true;
        if (g.failed) return false;
        g.failed = true;
        if (!Compiled()) return false;
        Release();
        g.mode = mode;
        if (NeedsMotion(mode))
        {
            UINT ww = 0, wh = 0;
            WorkSize(w, h, flow, ww, wh);
            g.work_w = ww; g.work_h = wh;
            UINT lw = ww, lh = wh, levels = 0;
            while (levels < kLevels && lw >= 24 && lh >= 16)
            {
                const UINT pw = UsesBlocks(mode) ? BlocksAcross(lw) : PatchesAcross(lw);
                const UINT ph = UsesBlocks(mode) ? BlocksAcross(lh) : PatchesAcross(lh);
                if (!Made(g.la[levels], lw, lh, DXGI_FORMAT_R32_FLOAT) || !Made(g.lb[levels], lw, lh, DXGI_FORMAT_R32_FLOAT)) return false;
                if (UsesBlocks(mode))
                {
                    if (!Made(g.fab[levels], pw, ph, DXGI_FORMAT_R32G32_FLOAT) || !Made(g.fba[levels], pw, ph, DXGI_FORMAT_R32G32_FLOAT) ||
                        !Made(g.pab[levels], pw, ph, DXGI_FORMAT_R32G32_FLOAT) || !Made(g.pba[levels], pw, ph, DXGI_FORMAT_R32G32_FLOAT)) return false;
                }
                else if (!Made(g.fab[levels], lw, lh, DXGI_FORMAT_R32G32_FLOAT) || !Made(g.fba[levels], lw, lh, DXGI_FORMAT_R32G32_FLOAT) ||
                         !Made(g.pab[levels], pw, ph, DXGI_FORMAT_R32G32_FLOAT) || !Made(g.pba[levels], pw, ph, DXGI_FORMAT_R32G32_FLOAT)) return false;
                ++levels;
                lw = (lw + 1) / 2;
                lh = (lh + 1) / 2;
            }
            if (levels == 0) { Trace("[xfg] the picture is too small to look for motion in (%ux%u)", w, h); return false; }
            g.levels = levels;
            if (!UsesBlocks(mode) && (!Made(g.mab, ww, wh, DXGI_FORMAT_R32G32_FLOAT) || !Made(g.mba, ww, wh, DXGI_FORMAT_R32G32_FLOAT))) return false;
        }
        else
        {
            g.work_w = 0; g.work_h = 0;
            UINT ww = 0, wh = 0;
            WorkSize(w, h, flow, ww, wh);
            g.work_w = ww; g.work_h = wh;
            // (Nothing to look for: the frames' pass still binds a field, a small one of noughts never read.)
            if (!Made(g.mab, 1, 1, DXGI_FORMAT_R32G32_FLOAT) || !Made(g.mba, 1, 1, DXGI_FORMAT_R32G32_FLOAT)) return false;
        }
        g.frame_w = w; g.frame_h = h;
        g.failed = false;
        Trace("[xfg] %s ready: frames at %ux%u%s", NameOf(mode), w, h, NeedsMotion(mode) ? "" : ", no motion looked for");
        if (NeedsMotion(mode)) Trace("[xfg] motion looked for at %ux%u, %u sizes", g.work_w, g.work_h, g.levels);
        return true;
    }

    static DXGI_FORMAT Typed(DXGI_FORMAT f)
    {
        switch (f)
        {
        case DXGI_FORMAT_R8G8B8A8_TYPELESS: return DXGI_FORMAT_R8G8B8A8_UNORM;
        case DXGI_FORMAT_B8G8R8A8_TYPELESS: return DXGI_FORMAT_B8G8R8A8_UNORM;
        case DXGI_FORMAT_R10G10B10A2_TYPELESS: return DXGI_FORMAT_R10G10B10A2_UNORM;
        case DXGI_FORMAT_R16G16B16A16_TYPELESS: return DXGI_FORMAT_R16G16B16A16_FLOAT;
        default: return f;
        }
    }

    struct Bind
    {
        ID3D12Resource *srv[6] = {};
        ID3D12Resource *uav[5] = {};
    };
    struct Constants
    {
        UINT a[4] = {};
        float b[4] = {}, c[4] = {}, d[4] = {};
    };

    static void To(ID3D12GraphicsCommandList *list, Tex &t, D3D12_RESOURCE_STATES state)
    {
        if (t.s == state) return;
        auto b = Barrier(t.r.get(), t.s, state);
        list->ResourceBarrier(1, &b);
        t.s = state;
    }

    static void Run(ID3D12GraphicsCommandList *list, Pass pass, const Bind &bind, const Constants &k, UINT gx, UINT gy, UINT gz)
    {
        static const DXGI_FORMAT kSrvNull[6] = { DXGI_FORMAT_R8G8B8A8_UNORM, DXGI_FORMAT_R8G8B8A8_UNORM, DXGI_FORMAT_R32_FLOAT, DXGI_FORMAT_R32_FLOAT,
                                                 DXGI_FORMAT_R32G32_FLOAT, DXGI_FORMAT_R32G32_FLOAT };
        static const DXGI_FORMAT kUavNull[5] = { DXGI_FORMAT_R32_FLOAT, DXGI_FORMAT_R32_FLOAT, DXGI_FORMAT_R32G32_FLOAT, DXGI_FORMAT_R32G32_FLOAT,
                                                 DXGI_FORMAT_R8G8B8A8_UNORM };
        const UINT set = g.next;
        g.next = (g.next + 1) % kTables;
        D3D12_CPU_DESCRIPTOR_HANDLE cpu = g.heap->GetCPUDescriptorHandleForHeapStart();
        D3D12_GPU_DESCRIPTOR_HANDLE gpu = g.heap->GetGPUDescriptorHandleForHeapStart();
        cpu.ptr += SIZE_T(set) * kSlots * g.stride;
        gpu.ptr += UINT64(set) * kSlots * g.stride;
        for (int i = 0; i < 6; ++i)
        {
            D3D12_SHADER_RESOURCE_VIEW_DESC srv = {};
            srv.Format = bind.srv[i] ? Typed(bind.srv[i]->GetDesc().Format) : kSrvNull[i];
            srv.ViewDimension = D3D12_SRV_DIMENSION_TEXTURE2D;
            srv.Shader4ComponentMapping = D3D12_DEFAULT_SHADER_4_COMPONENT_MAPPING;
            srv.Texture2D.MipLevels = 1;
            lfCore.device->CreateShaderResourceView(bind.srv[i], &srv, cpu);
            cpu.ptr += g.stride;
        }
        for (int i = 0; i < 5; ++i)
        {
            D3D12_UNORDERED_ACCESS_VIEW_DESC uav = {};
            uav.Format = bind.uav[i] ? Typed(bind.uav[i]->GetDesc().Format) : kUavNull[i];
            uav.ViewDimension = D3D12_UAV_DIMENSION_TEXTURE2D;
            lfCore.device->CreateUnorderedAccessView(bind.uav[i], nullptr, &uav, cpu);
            cpu.ptr += g.stride;
        }
        UINT words[16];
        memcpy(words, &k, sizeof(words));
        list->SetPipelineState(g.pso[pass].get());
        list->SetComputeRoot32BitConstants(0, 16, words, 0);
        list->SetComputeRootDescriptorTable(1, gpu);
        list->Dispatch(gx, gy, gz);
    }

    static UINT Groups(UINT n) { return (n + 7) / 8; }

    // The motion of the pair, both ways, into g.mab / g.mba (DIS) or g.fab[0] / g.fba[0] (MEMC). before and after
    // are readable (NON_PIXEL_SHADER_RESOURCE).
    static void Motion(ID3D12GraphicsCommandList *list, ID3D12Resource *before, ID3D12Resource *after, Tex *&ab, Tex *&ba)
    {
        constexpr D3D12_RESOURCE_STATES kRead = D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE, kWrite = D3D12_RESOURCE_STATE_UNORDERED_ACCESS;
        const UINT levels = g.levels;
        {
            To(list, g.la[0], kWrite); To(list, g.lb[0], kWrite);
            Bind b; b.srv[0] = before; b.srv[1] = after; b.uav[0] = g.la[0].r.get(); b.uav[1] = g.lb[0].r.get();
            Constants k; k.a[0] = g.la[0].w; k.a[1] = g.la[0].h;
            k.b[0] = static_cast<float>(std::clamp<UINT>((g.frame_w + g.la[0].w - 1) / g.la[0].w, 1u, 4u));
            Run(list, kLuma, b, k, Groups(k.a[0]), Groups(k.a[1]), 1);
        }
        for (UINT l = 1; l < levels; ++l)
        {
            To(list, g.la[l - 1], kRead); To(list, g.lb[l - 1], kRead);
            To(list, g.la[l], kWrite); To(list, g.lb[l], kWrite);
            Bind b; b.srv[2] = g.la[l - 1].r.get(); b.srv[3] = g.lb[l - 1].r.get(); b.uav[0] = g.la[l].r.get(); b.uav[1] = g.lb[l].r.get();
            Constants k; k.a[0] = g.la[l].w; k.a[1] = g.la[l].h;
            Run(list, kDown, b, k, Groups(k.a[0]), Groups(k.a[1]), 1);
        }
        for (UINT l = 0; l < levels; ++l) { To(list, g.la[l], kRead); To(list, g.lb[l], kRead); }
        for (int l = static_cast<int>(levels) - 1; l >= 0; --l)
        {
            const bool coarser = l + 1 < static_cast<int>(levels);
            const UINT lw = g.la[l].w, lh = g.la[l].h;
            if (UsesBlocks(g.mode))
            {
                if (coarser) { To(list, g.fab[l + 1], kRead); To(list, g.fba[l + 1], kRead); }
                To(list, g.fab[l], kWrite); To(list, g.fba[l], kWrite);
                Bind b; b.srv[2] = g.la[l].r.get(); b.srv[3] = g.lb[l].r.get();
                if (coarser) { b.srv[4] = g.fab[l + 1].r.get(); b.srv[5] = g.fba[l + 1].r.get(); }
                b.uav[2] = g.fab[l].r.get(); b.uav[3] = g.fba[l].r.get();
                Constants k; k.a[0] = lw; k.a[1] = lh; k.a[2] = g.fab[l].w; k.a[3] = g.fab[l].h;
                k.b[0] = coarser ? 1.0f : 0.0f; k.b[1] = 6.0f; k.b[2] = 0.004f; k.b[3] = 0.003f;
                Run(list, kBlock, b, k, Groups(k.a[2]), Groups(k.a[3]), 2);
                // Two rounds of spreading: fab -> pab, pab -> fab.
                for (int round = 0; round < 2; ++round)
                {
                    Tex &from_ab = round == 0 ? g.fab[l] : g.pab[l], &from_ba = round == 0 ? g.fba[l] : g.pba[l];
                    Tex &to_ab = round == 0 ? g.pab[l] : g.fab[l], &to_ba = round == 0 ? g.pba[l] : g.fba[l];
                    To(list, from_ab, kRead); To(list, from_ba, kRead); To(list, to_ab, kWrite); To(list, to_ba, kWrite);
                    Bind s; s.srv[2] = g.la[l].r.get(); s.srv[3] = g.lb[l].r.get(); s.srv[4] = from_ab.r.get(); s.srv[5] = from_ba.r.get();
                    s.uav[2] = to_ab.r.get(); s.uav[3] = to_ba.r.get();
                    Run(list, kSpread, s, k, Groups(k.a[2]), Groups(k.a[3]), 2);
                }
            }
            else
            {
                if (coarser) { To(list, g.fab[l + 1], kRead); To(list, g.fba[l + 1], kRead); }
                To(list, g.pab[l], kWrite); To(list, g.pba[l], kWrite);
                Bind b; b.srv[2] = g.la[l].r.get(); b.srv[3] = g.lb[l].r.get();
                if (coarser) { b.srv[4] = g.fab[l + 1].r.get(); b.srv[5] = g.fba[l + 1].r.get(); }
                b.uav[2] = g.pab[l].r.get(); b.uav[3] = g.pba[l].r.get();
                Constants k; k.a[0] = lw; k.a[1] = lh; k.a[2] = g.pab[l].w; k.a[3] = g.pab[l].h;
                k.b[0] = coarser ? 1.0f : 0.0f; k.b[1] = coarser ? 8.0f : 12.0f;
                Run(list, kPatch, b, k, Groups(k.a[2]), Groups(k.a[3]), 2);
                To(list, g.pab[l], kRead); To(list, g.pba[l], kRead);
                To(list, g.fab[l], kWrite); To(list, g.fba[l], kWrite);
                Bind d; d.srv[2] = g.la[l].r.get(); d.srv[3] = g.lb[l].r.get(); d.srv[4] = g.pab[l].r.get(); d.srv[5] = g.pba[l].r.get();
                d.uav[2] = g.fab[l].r.get(); d.uav[3] = g.fba[l].r.get();
                Run(list, kDensify, d, k, Groups(lw), Groups(lh), 2);
            }
        }
        if (UsesBlocks(g.mode))
        {
            To(list, g.fab[0], kRead); To(list, g.fba[0], kRead);
            ab = &g.fab[0]; ba = &g.fba[0];
            return;
        }
        To(list, g.fab[0], kRead); To(list, g.fba[0], kRead);
        To(list, g.mab, kWrite); To(list, g.mba, kWrite);
        Bind m; m.srv[4] = g.fab[0].r.get(); m.srv[5] = g.fba[0].r.get(); m.uav[2] = g.mab.r.get(); m.uav[3] = g.mba.r.get();
        Constants k; k.a[0] = g.mab.w; k.a[1] = g.mab.h;
        Run(list, kMedian, m, k, Groups(k.a[0]), Groups(k.a[1]), 2);
        To(list, g.mab, kRead); To(list, g.mba, kRead);
        ab = &g.mab; ba = &g.mba;
    }

    // The frames of one pair, each at its moment: between `before` and `after` (0 the frame before, 1 the frame
    // after), or for Spacewarp past `after` (0 it, 1 a whole frame of the game on). before and after in in_state,
    // each of `outputs` in out_state; all are left as they came.
    static bool Frames(ID3D12GraphicsCommandList *list, ID3D12Resource *before, ID3D12Resource *after, D3D12_RESOURCE_STATES in_state,
                       ID3D12Resource *const *outputs, D3D12_RESOURCE_STATES out_state, const float *moments, unsigned count)
    {
        if (!g.root || g.frame_w == 0 || before == nullptr || after == nullptr || count == 0) return false;
        constexpr D3D12_RESOURCE_STATES kRead = D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE;
        if (in_state != kRead)
        {
            D3D12_RESOURCE_BARRIER in[] = { Barrier(before, in_state, kRead), Barrier(after, in_state, kRead) };
            list->ResourceBarrier(2, in);
        }
        ID3D12DescriptorHeap *heaps[] = { g.heap.get() };
        list->SetDescriptorHeaps(1, heaps);
        list->SetComputeRootSignature(g.root.get());
        Tex *ab = &g.mab, *ba = &g.mba;
        if (NeedsMotion(g.mode)) Motion(list, before, after, ab, ba);
        else { To(list, g.mab, kRead); To(list, g.mba, kRead); }
        for (unsigned i = 0; i < count; ++i)
        {
            ID3D12Resource *const out = outputs[i];
            if (out == nullptr) continue;
            const D3D12_RESOURCE_DESC od = out->GetDesc();
            auto to_write = Barrier(out, out_state, D3D12_RESOURCE_STATE_UNORDERED_ACCESS);
            list->ResourceBarrier(1, &to_write);
            Bind b; b.srv[0] = before; b.srv[1] = after; b.srv[4] = ab->r.get(); b.srv[5] = ba->r.get(); b.uav[4] = out;
            Constants k;
            k.a[0] = static_cast<UINT>(od.Width); k.a[1] = od.Height; k.a[2] = ab->w; k.a[3] = ab->h;
            k.b[0] = std::clamp(moments[i], 0.0f, 1.0f);
            k.b[1] = g.mode == Mode::Repeat ? 3.0f : g.mode == Mode::Crossfade ? 4.0f : g.mode == Mode::Spacewarp ? 2.0f : g.mode == Mode::Blur ? 1.0f : 0.0f;
            k.b[2] = static_cast<float>(g.work_w); k.b[3] = static_cast<float>(g.work_h);
            k.c[0] = 1.0f / static_cast<float>(count + 1);   // the shutter: half of a frame shown, either side
            k.c[1] = 1.0f;
            k.c[2] = 0.015f;
            Run(list, kSynth, b, k, Groups(k.a[0]), Groups(k.a[1]), 1);
            auto written = Barrier(out, D3D12_RESOURCE_STATE_UNORDERED_ACCESS, out_state);
            list->ResourceBarrier(1, &written);
        }
        if (in_state != kRead)
        {
            D3D12_RESOURCE_BARRIER back[] = { Barrier(before, kRead, in_state), Barrier(after, kRead, in_state) };
            list->ResourceBarrier(2, back);
        }
        ++g.pairs;
        return true;
    }
}
