#pragma once
#include "effects/real/cursor.h"
#include "effects/real/snapshot.h"

#include <mfidl.h>
#include <mfreadwrite.h>

#include <array>
#include <optional>

namespace real {

// One stream of a recording: the file it is written to, and the writer that encodes into it.
struct Track
{
    Com<IMFSinkWriter> writer;
    DWORD stream;
    interior::FilePath file;
};

struct Tracks
{
    Track original;
    Track processed;
};

// A frame's two pictures copied out of the GPU, waiting for the copies to land before they are encoded.
struct Pending
{
    Readback original;
    Readback processed;
    interior::Instant when;
    bool waiting;                        // copies were recorded and not yet encoded
    std::optional<CursorOverlay> cursor; // drawn into both pictures as they are encoded, when the capture left it out and it was asked for
};

// A recording under way. The tracks open with the first frame, which is what says how big the pictures are;
// each frame slot keeps a copy pair, used again frame after frame, that is encoded once its fence has passed.
struct VideoRecording
{
    CaptureFiles files;
    std::optional<Tracks> tracks;
    interior::Instant started;
    std::optional<interior::Instant> last; // when the last frame encoded was taken, which is what the next one's length is measured from
    std::array<std::optional<Pending>, interior::kFrameSlotCount> slots;
};

// A frame's copies recorded, and the fence that says when they have landed.
struct Recorded
{
    VideoRecording recording;
    interior::FenceValue fence;
};

// A recording finished: the two files, and the fence the GPU was waited for at.
struct Stopped
{
    CaptureFiles files;
    interior::FenceValue fence;
};

// Names the two files and starts Media Foundation. Nothing is written until the first frame is recorded.
[[nodiscard]] infra::Result<VideoRecording, Error> StartRecording(const SnapshotOrder& order, interior::Instant now) noexcept;

// Copies this frame's two pictures into the slot's buffers on a list of their own, after the frame's, and
// signals a fence for them; the copies are encoded once that fence has passed and the slot comes round.
// The cursor, when there is one to draw, is kept with the copies and drawn in then.
[[nodiscard]] infra::Result<Recorded, Error> RecordFrame(const Gpu& gpu, const FrameContext& frame, const interior::FrameState& after, interior::Instant now,
                                                         const std::optional<CursorOverlay>& cursor, VideoRecording recording) noexcept;

// Encodes the slot's waiting copies, whose fence the caller has waited for, into both tracks with the same
// time, so the two pictures of a frame stay together.
[[nodiscard]] infra::Result<VideoRecording, Error> DrainedSlot(VideoRecording recording, interior::FrameSlot slot) noexcept;

// Waits for the GPU, encodes whatever is still waiting, and finishes both files.
[[nodiscard]] infra::Result<Stopped, Error> StopRecording(const Gpu& gpu, interior::FenceValue fence, VideoRecording recording) noexcept;

[[nodiscard]] interior::Microseconds Elapsed(const VideoRecording& recording, interior::Instant now) noexcept;

} // namespace real
