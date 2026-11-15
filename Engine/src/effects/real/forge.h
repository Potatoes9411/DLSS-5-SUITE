#pragma once
// Motion Forge's generators (MotionEngine/, MFG SUPER MOTION's engine by NODIX TECH, MIT - MotionEngine/LICENSE.txt)
// for the Screen Engine: SMFG, SMT, SMAI and Intel's XeSS Frame Generation, each making the pictures between two
// processed pictures. Their code is Motion Forge's own, unchanged, read from MotionEngine/ by forge.cpp; this
// header is the one door to it, in plain types, because that code is built to Motion Forge's rules rather than this
// project's (forge.cpp is a library of its own in cmake/WindowsApp.cmake for that reason).
//
// One thread at a time: the session's own, which records the frame. Motion Forge's generators keep their state in
// that code's globals, so there is one set of them in the process.

#include <d3d12.h>
#include <dxgi1_6.h>

#include <cstdint>

namespace forge {

enum class Kind : std::uint8_t { Smfg, Smt, Smai, Xess };

struct Settings
{
    std::uint32_t flow; // SMFG and SMT: percent of the picture the motion is looked for at, 10 to 100
    bool keepStill;     // read-outs and what stays on the screen are kept where they are
    bool lightSearch;   // SMFG: the lighter search
    bool rife;          // SMAI: RIFE 4.25 lite rather than SMFG Net
};

// What the generators run on. Nothing is held: the caller keeps all three alive until Stop.
struct Device
{
    ID3D12Device* device;
    ID3D12CommandQueue* queue; // the queue the session's frames are executed on
    IDXGIAdapter1* adapter;
};

constexpr std::uint32_t kMostBetween = 5;

// A pair of pictures, both the same size and format (DXGI_FORMAT_R8G8B8A8_UNORM, the back buffers'), and where the
// pictures between them go. Everything is in `rest` when handed over and is left there. motion and depth: the
// session's motion vectors (current to previous, in their own pixels) and its flat depth plane, for XeSS.
struct Pair
{
    ID3D12Resource* before;
    ID3D12Resource* after;
    ID3D12Resource* into[kMostBetween];
    std::uint32_t count; // the multiplier less one: into[k] is the picture at (k + 1) / (count + 1)
    bool reset;          // the two do not follow the pair before
    D3D12_RESOURCE_STATES rest;
    ID3D12Resource* motion;
    ID3D12Resource* depth;
};

// SMAI and XeSS hand work to the queue between the session's commands: `send` closes and executes what the list
// holds so far, `reopen` opens it again for recording. Both report whether they could.
struct ListControl
{
    void* context;
    bool (*send)(void* context);
    bool (*reopen)(void* context);
};

constexpr std::uint32_t kTextCapacity = 240;

struct Answer
{
    bool ok;
    char text[kTextCapacity]; // what runs, or why it cannot, in words for the log
};

// The generator made ready for pictures of width x height: its passes compiled, its libraries or network found,
// checked and loaded, and every texture it works in made. A second call replaces the first (Stop first).
[[nodiscard]] Answer Start(const Device& device, Kind kind, const Settings& settings, std::uint32_t width, std::uint32_t height) noexcept;
// The pictures between one pair, recorded into `list` (and, for SMAI and XeSS, handed to the queue in turn). The
// list is open, with whatever descriptor heap the generator set last, when this returns.
[[nodiscard]] bool Make(ID3D12GraphicsCommandList* list, const Pair& pair, const ListControl& control) noexcept;
// What the generator said since the last call, one line at a time; false when there is no more.
[[nodiscard]] bool NextLine(char (&line)[kTextCapacity]) noexcept;
// Everything the generator made let go. The queue must be idle.
void Stop() noexcept;

} // namespace forge
