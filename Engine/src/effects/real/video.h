#pragma once
#include "effects/real/environment.h"

#include <vector>

namespace real {

// Where a video's pictures come from and where the finished ones go: pipes the program that started this one holds.
struct VideoStreams
{
    HANDLE input;  // 32-bit BGRA rows, one picture after another, until it ends
    HANDLE output; // 32-bit RGBA rows, one picture after another
};

// What is left of a video once its pictures have been read: copies of the last one still to write, so the
// video is as long as it was. Only frame generation, which shows each picture one picture late, leaves any.
struct Tail
{
    std::uint32_t copies;
};

// A session over a video rather than the screen. Each picture read is a fresh capture at the time it is shown in
// the video; with frame generation the frames between are planned at the times between, so the engine's own
// blend draws them; and every frame drawn is read back and written out, in order, as the screen would have shown it.
class VideoEnvironment final
{
public:
    VideoEnvironment(VideoGpu gpu, const interior::SessionPlan& plan, interior::Microseconds period, VideoStreams streams, const Console& console) noexcept;

    [[nodiscard]] infra::Result<FrameStart, Error> BeginFrame(const interior::FrameState& state) noexcept;
    [[nodiscard]] infra::Result<ExecutionReport, Error> Execute(const interior::FramePlan& plan) noexcept;
    [[nodiscard]] static Error FromPlanError(interior::PlanFrameError error) noexcept;
    [[nodiscard]] std::uint64_t PicturesRead() const noexcept { return read_; }
    [[nodiscard]] std::uint64_t PicturesWritten() const noexcept { return written_; }

private:
    [[nodiscard]] infra::Result<FrameStart, Error> Fresh(const interior::FrameState& state, std::optional<interior::Fraction> unmatched) noexcept;
    [[nodiscard]] infra::Result<FrameStart, Error> Between(const interior::FrameState& state, std::optional<interior::Fraction> unmatched) noexcept;
    [[nodiscard]] infra::Result<FrameStart, Error> Ended(const interior::FrameState& state, std::optional<interior::Fraction> unmatched) noexcept;
    [[nodiscard]] infra::Status<Error> Written(const interior::FramePlan& plan, interior::FenceValue fence) noexcept;

    Gpu gpu_;
    std::uint32_t finestPixels_;
    interior::FrameGen multiplier_; // frames planned for each picture read: the session's frame generation
    interior::Microseconds period_;
    VideoStreams streams_;
    Console console_;
    std::vector<std::byte> picture_;    // one picture read, kept for the life of the session
    std::vector<std::byte> finished_;   // one picture to write, its rows packed
    FrameContext frame_;                // WAIVER(R2): per-frame bookkeeping of the effect layer, replaced whole each frame.
    std::optional<Readback> readback_;  // WAIVER(R2): the buffer pictures are copied out through, kept while it is the size needed.
    std::uint64_t read_;                // WAIVER(R2): pictures read so far.
    std::uint64_t written_;             // WAIVER(R2): pictures written so far.
    std::uint32_t step_;                // WAIVER(R2): which of the multiplier's frames comes next; 0 reads a picture.
    std::optional<Tail> tail_;          // WAIVER(R2): set once, when the pictures run out.
    std::uint32_t copies_;              // WAIVER(R2): how many times the frame being executed is written: 0, 1 or the tail.
    interior::BackBufferIndex drawnTo_; // WAIVER(R2): the back buffer the frame being executed draws into.
};

[[nodiscard]] infra::Result<VideoEnvironment, Error> CreateVideoEnvironment(VideoGpu gpu, const interior::SessionPlan& plan, interior::Microseconds period, VideoStreams streams,
                                                                            const Console& console) noexcept;

} // namespace real
