#pragma once
#include "effects/real/cursor.h"
#include "effects/real/executor.h"
#include "effects/real/png_writer.h"
#include "interior/capture_name.h"

#include <optional>

namespace real {

// Which of a frame's two pictures a capture writes.
enum class Pictures : std::uint8_t { Both, Original, Processed };

// What a capture is asked for with: where to put it, what to call it, and what shaped the picture.
struct SnapshotOrder
{
    interior::DirectoryPath folder;
    interior::CaptureLabel label;
    interior::LiveSettings live;
    bool everything; // every parameter goes into the name, the ones at their defaults included
    interior::CaptureMoment when;
    Pictures pictures;
};

// The two files a capture is written to.
struct CaptureFiles
{
    interior::FilePath original;
    interior::FilePath processed;
};

// How a texture's rows lie in the buffer they are copied to: each row padded to the alignment the copy needs.
struct Layout
{
    D3D12_PLACED_SUBRESOURCE_FOOTPRINT footprint;
    std::uint64_t bytes;
};

// A texture copied out of the GPU, and where its rows are.
struct Readback
{
    Com<ID3D12Resource> buffer;
    Layout layout;
};

// The two files a screenshot became, and the fence the copies out of the GPU were waited for at.
struct Snapshot
{
    interior::FilePath original;
    interior::FilePath processed;
    interior::FenceValue fence;
};

[[nodiscard]] interior::CaptureMoment MomentNow() noexcept;

// A copied texture's size in pixels.
[[nodiscard]] FrameSize SizeOf(const Layout& layout) noexcept;

// The folder is made when it is not there; one that is there already is what was wanted.
[[nodiscard]] infra::Status<Error> EnsureCaptureFolder(const interior::DirectoryPath& folder) noexcept;

// A folder of its own under `parent`, made here and named for the stem, with a count when a folder of that
// name is there already.
[[nodiscard]] infra::Result<interior::DirectoryPath, Error> MadeCaptureFolder(const interior::DirectoryPath& parent, const interior::CaptureStem& stem) noexcept;

// Both files of a capture take the same count, so the pair stays a pair: a count is free only when neither
// file with it is there. A name that cannot be made ends the search with its own error.
[[nodiscard]] infra::Result<CaptureFiles, Error> FreeCaptureNames(const interior::DirectoryPath& folder, const interior::CaptureStem& stem, const wchar_t* originalSuffix,
                                                                  const wchar_t* processedSuffix) noexcept;

// Records a copy of the texture into a readback buffer on the open command list, handing the texture over
// and giving it back in the state the frame left it. A buffer kept from before is used again when it is the
// size the texture needs; otherwise one is made.
[[nodiscard]] infra::Result<Readback, Error> CopiedOut(const Gpu& gpu, const interior::ResourceId& id, const interior::StateTable& states, const std::optional<Readback>& kept) noexcept;

// The rows of a copy, once its fence has been waited for; unmapped when they have been read.
[[nodiscard]] infra::Result<void*, Error> MapReadback(const Readback& readback) noexcept;
void UnmapReadback(const Readback& readback) noexcept;

// The factory every WIC object here is made from. A thread makes its own.
[[nodiscard]] infra::Result<Com<IWICImagingFactory>, Error> MadeWicFactory() noexcept;

// Writes a picture held as 32-bit blue, green, red and a spare byte as a PNG file of plain 24-bit colour,
// at the encoder's own compression, which is what "standard" means to it.
[[nodiscard]] infra::Status<Error> WriteHeldPng(IWICImagingFactory* wic, const Com<IWICBitmap>& held, const interior::FilePath& path) noexcept;

// Copies the picture the model was given and the picture shown for it out of the GPU, as the frame just
// submitted left them, and hands the ones asked for to the writer as PNG files named for the settings and
// the moment; an original written alone is named for the source and the moment, since no setting shaped it.
// Waits for the copies, and for the writer only when it is that far behind. The cursor, when there is one
// to draw, goes into the pictures before they are handed over.
[[nodiscard]] infra::Result<Snapshot, Error> SaveSnapshot(const Gpu& gpu, const FrameContext& frame, const interior::FrameState& after, const SnapshotOrder& order,
                                                          const std::optional<CursorOverlay>& cursor, PngWriter& writer) noexcept;

} // namespace real
