// forge.cpp - Motion Forge's generators, shared with the Screen Engine.
//
// SMFG (msm_fg.h), SMT (msm_tri.h, SMFG's triangle mesh), SMAI (msm_ai.h), XeSS MFG (xe_mfg.inl with xe_api.h and
// xe_tap.h) and FSR FG (fsr_fg.inl, AMD's FidelityFX API from external/fidelityfx) are MotionEngine/'s own files, included here as they are: the Screen Engine and Motion Forge run the
// same code, and a fix in one is a fix in both. They lean on a few things of Motion Forge's engine - its device
// core, a barrier, its log, a clock, small helpers for a texture and a list of its own - which this file gives them
// over the Screen Engine's device and queue (the "engine's own" section below). lf_signature.h (the checks on a
// user's DLL), lf_shader_cache.inl (the compiled shaders, cached) and sm_options.h (SuiteMotionPath, the folder
// Motion Forge keeps its files in, and the SMAI network setting) are Motion Forge's too.
//
// Built as its own library (cmake/WindowsApp.cmake) to Motion Forge's flags, not the Screen Engine's: it is NODIX
// TECH's code, kept as it came (MotionEngine/README.md), and the Screen Engine's rules (AI_CODE_RULES.md) are for the
// Screen Engine's own. Its globals are its state, one set in the process, used from the session's thread only.
//
// The files the generators load are the ones Motion Forge uses, in SUITE's data folder (%LOCALAPPDATA%\DLSS5Suite\
// Motion): XeSS's two libraries in user\xess (the user's own, Intel's signature checked before they are mapped) and
// SMAI's runtime and network in ai (downloaded by SUITE on request, pinned by SHA-256 - Services/MotionEngine.fs).
// Nothing is shipped beside the Screen Engine, and nothing of anyone's is patched.

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#include <d3d12.h>
#include <dxgi1_4.h>
#include <dxgi1_6.h>
#include <d3dcompiler.h>
#include <winrt/base.h>

#include <algorithm>
#include <atomic>
#include <chrono>
#include <cmath>
#include <condition_variable>
#include <cstdarg>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <deque>
#include <functional>
#include <map>
#include <mutex>
#include <new>
#include <set>
#include <string>
#include <thread>
#include <vector>

#include "effects/real/forge.h"

// --- the engine's own, as Motion Forge's generators know it -------------------------------------------------

// lf_types.h's GpuCore, the part of it the generators use: the device, the queue their work goes to, and a list,
// an allocator and a fence of their own for what they set up once (XeSS's motion picture).
struct ForgeCore
{
    ID3D12Device *device = nullptr;
    ID3D12CommandQueue *direct_queue = nullptr;
    ID3D12GraphicsCommandList *cmd = nullptr;
    ID3D12CommandAllocator *allocator = nullptr;
    ID3D12Fence *timeline = nullptr;
    UINT64 timeline_value = 0;
    HANDLE timeline_event = nullptr;
};
static ForgeCore lfCore;

struct ForgeCard
{
    IDXGIAdapter1 *adapter = nullptr;
};
static ForgeCard lfCard;

// The log: Motion Forge's Trace, kept as lines for the Screen Engine's log to take (NextLine). XeSS's library calls
// it from threads of its own, so the lines are under a lock; a hundred and twenty-eight at most, the oldest let go.
static std::mutex g_forge_lines_lock;
static std::deque<std::string> g_forge_lines;
static void Trace(const char *format, ...)
{
    char text[1024];
    va_list args;
    va_start(args, format);
    vsnprintf(text, sizeof(text), format, args);
    va_end(args);
    std::lock_guard<std::mutex> hold(g_forge_lines_lock);
    if (g_forge_lines.size() >= 128) g_forge_lines.pop_front();
    g_forge_lines.emplace_back(text);
}

static double LatencyNow()
{
    using clock = std::chrono::steady_clock;
    return std::chrono::duration<double>(clock::now().time_since_epoch()).count();
}

static D3D12_RESOURCE_BARRIER Barrier(ID3D12Resource *r, D3D12_RESOURCE_STATES before, D3D12_RESOURCE_STATES after)
{
    D3D12_RESOURCE_BARRIER b = {};
    b.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
    b.Flags = D3D12_RESOURCE_BARRIER_FLAG_NONE;
    b.Transition.pResource = r;
    b.Transition.StateBefore = before;
    b.Transition.StateAfter = after;
    b.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
    return b;
}

// The Screen Engine stops a session between frames, never while a generator waits: nothing is asked to stop.
static bool StopAsked() { return false; }

static ID3D12Resource *NewTexture(UINT w, UINT h, DXGI_FORMAT format, bool uav)
{
    D3D12_HEAP_PROPERTIES heap = {};
    heap.Type = D3D12_HEAP_TYPE_DEFAULT;
    D3D12_RESOURCE_DESC desc = {};
    desc.Dimension = D3D12_RESOURCE_DIMENSION_TEXTURE2D;
    desc.Width = w;
    desc.Height = h;
    desc.DepthOrArraySize = 1;
    desc.MipLevels = 1;
    desc.Format = format;
    desc.SampleDesc.Count = 1;
    desc.Layout = D3D12_TEXTURE_LAYOUT_UNKNOWN;
    desc.Flags = uav ? D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS : D3D12_RESOURCE_FLAG_NONE;
    ID3D12Resource *t = nullptr;
    if (FAILED(lfCore.device->CreateCommittedResource(&heap, D3D12_HEAP_FLAG_NONE, &desc, D3D12_RESOURCE_STATE_COMMON, nullptr, __uuidof(ID3D12Resource),
                                                      reinterpret_cast<void **>(&t))))
        return nullptr;
    return t;
}

// lf_types.h's StagedTexture and lf_pipeline.inl's helpers for it: a texture and the upload buffer that fills it.
struct StagedTexture
{
    ID3D12Resource *gpu_tex = nullptr;
    ID3D12Resource *staging = nullptr;
    D3D12_PLACED_SUBRESOURCE_FOOTPRINT layout = {};
    UINT row_count = 0;
    UINT64 row_bytes = 0;
};

static bool StagedCreate(StagedTexture &staged, UINT w, UINT h, DXGI_FORMAT format, UINT tight_pitch, D3D12_RESOURCE_FLAGS flags = D3D12_RESOURCE_FLAG_NONE)
{
    D3D12_HEAP_PROPERTIES vram = {};
    vram.Type = D3D12_HEAP_TYPE_DEFAULT;
    D3D12_RESOURCE_DESC tex = {};
    tex.Dimension = D3D12_RESOURCE_DIMENSION_TEXTURE2D;
    tex.Width = w; tex.Height = h; tex.DepthOrArraySize = 1; tex.MipLevels = 1;
    tex.Format = format; tex.SampleDesc.Count = 1; tex.Layout = D3D12_TEXTURE_LAYOUT_UNKNOWN;
    tex.Flags = flags;
    if (FAILED(lfCore.device->CreateCommittedResource(&vram, D3D12_HEAP_FLAG_NONE, &tex, D3D12_RESOURCE_STATE_COPY_DEST, nullptr, __uuidof(ID3D12Resource),
                                                      reinterpret_cast<void **>(&staged.gpu_tex))))
        return false;
    UINT64 total = 0;
    lfCore.device->GetCopyableFootprints(&tex, 0, 1, 0, &staged.layout, &staged.row_count, &staged.row_bytes, &total);
    if (staged.row_count != h || staged.row_bytes < tight_pitch) return false;
    D3D12_HEAP_PROPERTIES upload = {};
    upload.Type = D3D12_HEAP_TYPE_UPLOAD;
    D3D12_RESOURCE_DESC buffer = {};
    buffer.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER;
    buffer.Width = total; buffer.Height = 1; buffer.DepthOrArraySize = 1; buffer.MipLevels = 1;
    buffer.SampleDesc.Count = 1; buffer.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
    return SUCCEEDED(lfCore.device->CreateCommittedResource(&upload, D3D12_HEAP_FLAG_NONE, &buffer, D3D12_RESOURCE_STATE_GENERIC_READ, nullptr,
                                                            __uuidof(ID3D12Resource), reinterpret_cast<void **>(&staged.staging)));
}

static bool StagedFill(StagedTexture &staged, const BYTE *from, UINT from_pitch, UINT h)
{
    BYTE *into = nullptr;
    if (FAILED(staged.staging->Map(0, nullptr, reinterpret_cast<void **>(&into)))) return false;
    for (UINT y = 0; y < h; ++y) memcpy(into + static_cast<size_t>(y) * staged.layout.Footprint.RowPitch, from + static_cast<size_t>(y) * from_pitch, from_pitch);
    staged.staging->Unmap(0, nullptr);
    return true;
}

static void StagedRelease(StagedTexture &staged)
{
    if (staged.gpu_tex) { staged.gpu_tex->Release(); staged.gpu_tex = nullptr; }
    if (staged.staging) { staged.staging->Release(); staged.staging = nullptr; }
}

static bool AwaitMark(ID3D12Fence *fence, UINT64 value, DWORD timeout_ms)
{
    if (value == 0 || fence == nullptr) return false;
    if (fence->GetCompletedValue() >= value) return true;
    if (FAILED(fence->SetEventOnCompletion(value, lfCore.timeline_event))) return false;
    return WaitForSingleObject(lfCore.timeline_event, timeout_ms) == WAIT_OBJECT_0 && fence->GetCompletedValue() >= value;
}

// The generators' own list: opened once its last use has run on the card, sent, and marked by the fence.
static bool CmdOpen()
{
    if (!lfCore.cmd) return false;
    if (lfCore.timeline_value && !AwaitMark(lfCore.timeline, lfCore.timeline_value, 4000)) return false;
    return SUCCEEDED(lfCore.allocator->Reset()) && SUCCEEDED(lfCore.cmd->Reset(lfCore.allocator, nullptr));
}

static UINT64 CmdSubmit()
{
    if (FAILED(lfCore.cmd->Close())) return 0;
    ID3D12CommandList *lists[] = { lfCore.cmd };
    lfCore.direct_queue->ExecuteCommandLists(1, lists);
    const UINT64 mark = ++lfCore.timeline_value;
    return SUCCEEDED(lfCore.direct_queue->Signal(lfCore.timeline, mark)) ? mark : 0;
}

// --- Motion Forge's files ----------------------------------------------------------------------------------

#include "sm_options.h"
#include "lf_signature.h"
typedef HRESULT(WINAPI *CompileFn)(LPCVOID, SIZE_T, LPCSTR, const void *, void *, LPCSTR, LPCSTR, UINT, UINT, ID3DBlob **, ID3DBlob **);   // (lf_types.h's)
#include "lf_shader_cache.inl"
#include "msm_fg.h"
#include "msm_ai.h"
#include "xe_mfg.inl"
#include "fsr_fg.inl"

// --- the Screen Engine's door to them ----------------------------------------------------------------------

namespace forge {
namespace {

struct Run
{
    bool live = false;
    Kind kind = Kind::Smfg;
    Settings settings = {};
    UINT w = 0, h = 0;
    bool smai_started = false; // SMAI's runtime stays as it was set up for the rest of the process (msm_ai.h has no end)
    unsigned pairs = 0, xe_short = 0;
    ULONGLONG told = 0;
};
Run g_run;

constexpr DXGI_FORMAT kFormat = DXGI_FORMAT_R8G8B8A8_UNORM;

Answer Said(bool ok, const char *text)
{
    Answer a = {};
    a.ok = ok;
    snprintf(a.text, sizeof(a.text), "%s", text);
    return a;
}

const char *NameOf(Kind kind)
{
    switch (kind)
    {
    case Kind::Smfg: return "SMFG 1.3";
    case Kind::Smt: return "SMT (SMFG's triangle mesh)";
    case Kind::Smai: return "SMAI 1.0";
    case Kind::Xess: return "XeSS MFG";
    case Kind::Fsr: return "FSR 3.1 FG";
    }
    return "Motion Forge";
}

// The generators' own list and fence, for what they set up once.
bool CoreReady()
{
    if (lfCore.cmd) return true;
    if (FAILED(lfCore.device->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT, IID_PPV_ARGS(&lfCore.allocator)))) return false;
    if (FAILED(lfCore.device->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT, lfCore.allocator, nullptr, IID_PPV_ARGS(&lfCore.cmd)))) return false;
    if (FAILED(lfCore.cmd->Close())) return false;
    if (FAILED(lfCore.device->CreateFence(0, D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(&lfCore.timeline)))) return false;
    lfCore.timeline_event = CreateEventW(nullptr, FALSE, FALSE, nullptr);
    return lfCore.timeline_event != nullptr;
}

// SMFG's passes and textures (SMT's too, with tri on): what SMAI falls back to and keeps its marks with as well.
bool MsmFor(const Settings &s, bool tri)
{
    g_msm.tune.tri = tri ? 1.0f : 0.0f;
    if (!MsmReady(g_run.w, g_run.h, kFormat, static_cast<int>(s.flow))) return false;
    return !tri || TriOn();
}

std::string Lines()
{
    std::lock_guard<std::mutex> hold(g_forge_lines_lock);
    return g_forge_lines.empty() ? std::string() : g_forge_lines.back();
}

void CopyWhole(ID3D12GraphicsCommandList *list, ID3D12Resource *into, ID3D12Resource *from, D3D12_RESOURCE_STATES into_rest, D3D12_RESOURCE_STATES from_rest)
{
    D3D12_RESOURCE_BARRIER open[] = { Barrier(into, into_rest, D3D12_RESOURCE_STATE_COPY_DEST), Barrier(from, from_rest, D3D12_RESOURCE_STATE_COPY_SOURCE) };
    list->ResourceBarrier(2, open);
    list->CopyResource(into, from);
    D3D12_RESOURCE_BARRIER shut[] = { Barrier(into, D3D12_RESOURCE_STATE_COPY_DEST, into_rest), Barrier(from, D3D12_RESOURCE_STATE_COPY_SOURCE, from_rest) };
    list->ResourceBarrier(2, shut);
}

// SMFG (and SMT): the pair's own work, then every picture between at its moment - lf_dlssg.inl's way.
void MakeMsm(ID3D12GraphicsCommandList *list, const Pair &p)
{
    MsmAsk ask;
    ask.marks = g_run.settings.keepStill;
    ask.fast = g_run.settings.lightSearch;
    // (The Screen Engine's pair is always the same two textures, the history and the current picture, with new content
    // in both: SMFG would take the history's brightness as the one it worked out for the current picture last time, the
    // same texture. Each pair starts afresh.)
    MsmForget();
    MsmPair(list, p.before, p.after, p.rest, ask);
    for (UINT k = 0; k < p.count; ++k)
        MsmFrame(list, p.before, p.after, p.rest, p.into[k], p.rest, static_cast<float>(k + 1) / static_cast<float>(p.count + 1));
}

// SMAI: the network makes them, SMFG's marks kept (as lf_dlssg.inl asks it); SMFG when the network cannot.
bool MakeAi(ID3D12GraphicsCommandList *list, const Pair &p, const ListControl &control)
{
    MsmAsk ask;
    ask.marks = g_run.settings.keepStill;
    ask.fast = g_run.settings.lightSearch;
    if (p.reset) { MsmForget(); msmai::Forget(); }
    msmai::MarksAsk marks;
    const bool with_marks = !g_run.settings.rife && ask.marks;
    if (with_marks)
    {
        marks.words = MsmNetWords(marks.search_w, marks.search_h, marks.blocks_w, marks.blocks_h);
        marks.holds = MsmMarkHolds();
        marks.make = [&](ID3D12GraphicsCommandList *l) { return MsmMarksFromWords(l, p.before, p.after, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE, ask); };
    }
    ID3D12GraphicsCommandList *open = list;
    bool lost = false;
    const bool made = msmai::Frames(open, p.before, p.after, p.rest, p.into, p.rest, std::min<UINT>(p.count, msmai::kMost), g_run.w, g_run.h, kFormat,
                                    [&] { return control.send(control.context); },
                                    [&] { lost = !control.reopen(control.context); return !lost; }, nullptr, with_marks ? &marks : nullptr);
    if (lost) return false;
    if (made) { MsmForget(); return true; }
    msmai::Forget();
    MakeMsm(list, p);
    return true;
}

// XeSS MFG: the current picture into the library's back buffer, the list sent, the library asked for the pictures
// between (they come into pictures of its tap's own) and the queue told to wait for them; then each picture between
// is the one the library made nearest its moment. Intel's library makes one a pair on another maker's card.
bool MakeXe(ID3D12GraphicsCommandList *list, const Pair &p, const ListControl &control)
{
    constexpr D3D12_RESOURCE_STATES kNonPixel = D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE;
    XeFeed(list, p.after, p.rest, nullptr);
    D3D12_RESOURCE_BARRIER told[] = { Barrier(p.motion, p.rest, kNonPixel), Barrier(p.depth, p.rest, kNonPixel) };
    list->ResourceBarrier(2, told);
    if (!control.send(control.context)) return false;
    const D3D12_RESOURCE_DESC motion = p.motion->GetDesc();
    ID3D12Resource *made[3] = {};
    UINT64 taken = 0;
    const unsigned asked = std::min<unsigned>(p.count, XeMost());
    const bool came = XeMake(asked, made, p.motion, p.depth, static_cast<UINT>(motion.Width), motion.Height, p.reset, 0.0, &taken);
    if (!control.reopen(control.context)) return false;
    D3D12_RESOURCE_BARRIER back[] = { Barrier(p.motion, kNonPixel, p.rest), Barrier(p.depth, kNonPixel, p.rest) };
    list->ResourceBarrier(2, back);
    if (!came)
    {
        // (A new start, or fewer than asked for: the nearer of the two pictures, as Motion Forge shows the pair then.)
        ++g_run.xe_short;
        for (UINT k = 0; k < p.count; ++k) CopyWhole(list, p.into[k], 2 * (k + 1) <= p.count ? p.before : p.after, p.rest, p.rest);
        return true;
    }
    XeAfter(lfCore.direct_queue, taken);
    for (UINT k = 0; k < p.count; ++k)
    {
        const double moment = static_cast<double>(k + 1) / static_cast<double>(p.count + 1);
        const int nearest = std::clamp(static_cast<int>(std::lround(moment * (asked + 1))) - 1, 0, static_cast<int>(asked) - 1);
        CopyWhole(list, p.into[k], made[nearest], p.rest, D3D12_RESOURCE_STATE_PRESENT);
    }
    return true;
}

// FSR 3.1 FG: the current picture handed to AMD's runtime with the motion vectors and the flat depth plane, and the
// picture between it and the one handed before drawn into the first picture between; FSR makes one a pair, so every
// other picture between is that one (as Motion Forge shows FSR FG, at x2).
bool MakeFsr(ID3D12GraphicsCommandList *list, const Pair &p)
{
    constexpr D3D12_RESOURCE_STATES kNonPixel = D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE;
    if (p.reset) fsrfg::Forget();
    D3D12_RESOURCE_BARRIER told[] = { Barrier(p.motion, p.rest, kNonPixel), Barrier(p.depth, p.rest, kNonPixel) };
    list->ResourceBarrier(2, told);
    const bool made = fsrfg::Make(list, p.after, p.rest, p.motion, p.depth, p.into[0], p.rest, p.reset, 1.0 / 30.0);
    D3D12_RESOURCE_BARRIER back[] = { Barrier(p.motion, kNonPixel, p.rest), Barrier(p.depth, kNonPixel, p.rest) };
    list->ResourceBarrier(2, back);
    if (!made)
    {
        ++g_run.xe_short;
        for (UINT k = 0; k < p.count; ++k) CopyWhole(list, p.into[k], 2 * (k + 1) <= p.count ? p.before : p.after, p.rest, p.rest);
        return true;
    }
    for (UINT k = 1; k < p.count; ++k) CopyWhole(list, p.into[k], p.into[0], p.rest, p.rest);
    return true;
}

void SayNow()
{
    const ULONGLONG now = GetTickCount64();
    if (g_run.told == 0) g_run.told = now;
    if (now - g_run.told < 10000) return;
    if (g_run.kind == Kind::Xess || g_run.kind == Kind::Fsr)
        Trace("[forge] %s: %u pairs in ten seconds, the library made %u a pair (%u pairs without - shown by the nearer picture)", NameOf(g_run.kind), g_run.pairs,
              g_run.kind == Kind::Xess ? XeMost() : 1u, g_run.xe_short);
    else
        Trace("[forge] %s: %u pairs in ten seconds", NameOf(g_run.kind), g_run.pairs);
    g_run.told = now;
    g_run.pairs = 0;
    g_run.xe_short = 0;
}

} // namespace

Answer Start(const Device &device, Kind kind, const Settings &settings, std::uint32_t width, std::uint32_t height) noexcept
{
    Stop();
    lfCore.device = device.device;
    lfCore.direct_queue = device.queue;
    lfCard.adapter = device.adapter;
    g_run.kind = kind;
    g_run.settings = settings;
    g_run.w = width;
    g_run.h = height;
    if (!CoreReady()) return Said(false, "its command list could not be made");
    char text[kTextCapacity] = {};
    switch (kind)
    {
    case Kind::Smfg:
    case Kind::Smt:
        if (!MsmFor(settings, kind == Kind::Smt))
        {
            snprintf(text, sizeof(text), "%s did not start: %s", NameOf(kind), Lines().c_str());
            return Said(false, text);
        }
        break;
    case Kind::Smai:
    {
        if (g_run.smai_started) return Said(false, "SMAI starts once each time the Screen Engine starts; restart the overlay");
        if (!MsmFor(settings, false)) { snprintf(text, sizeof(text), "SMFG, which SMAI keeps its marks with, did not start: %s", Lines().c_str()); return Said(false, text); }
        smOpt.msm_ai = settings.rife ? 2 : 1;
        msmai::InPlace(true);
        g_run.smai_started = true;
        if (!msmai::Ready(!settings.rife)) { snprintf(text, sizeof(text), "SMAI did not start: %s", msmai::g.why.c_str()); return Said(false, text); }
        break;
    }
    case Kind::Xess:
        if (!XeReady(width, height, kFormat)) { snprintf(text, sizeof(text), "XeSS MFG did not start: %s", Lines().c_str()); return Said(false, text); }
        break;
    case Kind::Fsr:
        wcsncpy_s(g_ffx_dir, settings.ffxDirectory ? settings.ffxDirectory : L"", _TRUNCATE);
        fsrfg::g.failed = false;
        if (!fsrfg::Ready(width, height, settings.motionWidth, settings.motionHeight, kFormat)) { snprintf(text, sizeof(text), "FSR 3.1 FG did not start: %s", Lines().c_str()); return Said(false, text); }
        break;
    }
    g_run.live = true;
    snprintf(text, sizeof(text), "%s makes the pictures between at %ux%u%s", NameOf(kind), width, height,
             kind == Kind::Xess ? (XeMost() > 1 ? "" : ", one a pair (Intel's library on this card); the others are the nearest of it")
             : kind == Kind::Fsr ? ", one a pair (FSR 3.1); the others are that one" : "");
    return Said(true, text);
}

bool Make(ID3D12GraphicsCommandList *list, const Pair &pair, const ListControl &control) noexcept
{
    if (!g_run.live || pair.count == 0 || pair.count > kMostBetween) return false;
    ++g_run.pairs;
    bool ok = true;
    switch (g_run.kind)
    {
    case Kind::Smfg:
    case Kind::Smt:
        if (!MsmFor(g_run.settings, g_run.kind == Kind::Smt)) return false;
        MakeMsm(list, pair);
        break;
    case Kind::Smai:
        if (!MsmFor(g_run.settings, false)) return false;
        ok = MakeAi(list, pair, control);
        break;
    case Kind::Xess:
        ok = MakeXe(list, pair, control);
        break;
    case Kind::Fsr:
        ok = MakeFsr(list, pair);
        break;
    }
    SayNow();
    return ok;
}

bool NextLine(char (&line)[kTextCapacity]) noexcept
{
    std::lock_guard<std::mutex> hold(g_forge_lines_lock);
    if (g_forge_lines.empty()) return false;
    snprintf(line, sizeof(line), "%s", g_forge_lines.front().c_str());
    g_forge_lines.pop_front();
    return true;
}

void Stop() noexcept
{
    if (lfCore.device == nullptr) return;
    XeClose();
    g_xe.fence = nullptr;
    fsrfg::Close();
    MsmClose();
    TriClose();
    // (Their passes were made on this device: made anew on the next one.)
    g_msm.~MsmState();
    new (&g_msm) MsmState();
    g_tri.~TriState();
    new (&g_tri) TriState();
    if (lfCore.cmd) { lfCore.cmd->Release(); lfCore.cmd = nullptr; }
    if (lfCore.allocator) { lfCore.allocator->Release(); lfCore.allocator = nullptr; }
    if (lfCore.timeline) { lfCore.timeline->Release(); lfCore.timeline = nullptr; }
    if (lfCore.timeline_event) { CloseHandle(lfCore.timeline_event); lfCore.timeline_event = nullptr; }
    lfCore.timeline_value = 0;
    lfCore.device = nullptr;
    lfCore.direct_queue = nullptr;
    lfCard.adapter = nullptr;
    g_run.live = false;
}

} // namespace forge
