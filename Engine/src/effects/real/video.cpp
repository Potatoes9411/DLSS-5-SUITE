#include "effects/real/video.h"

#include "effects/real/capture.h"
#include "infrastructure/checked.h"
#include "infrastructure/fold.h"

#include <algorithm>
#include <cstring>
#include <ranges>

namespace real {
namespace {

using infra::Fail;
using infra::Result;
using infra::Status;

constexpr std::uint32_t kBytesPerPixel = 4;
// One ReadFile or WriteFile is asked for no more than this; a picture takes as many as it needs.
constexpr DWORD kMaxTransfer = DWORD{ 1 } << 30;

[[nodiscard]] std::size_t PictureBytes(const interior::Extent& extent) noexcept
{
    return static_cast<std::size_t>(extent.width.Get()) * extent.height.Get() * kBytesPerPixel;
}

[[nodiscard]] DWORD ChunkOf(std::size_t remaining) noexcept
{
    return static_cast<DWORD>(std::min<std::size_t>(remaining, kMaxTransfer));
}

// The end of the video is the writer closing its end: ReadFile then answers with nothing, or with a broken pipe.
[[nodiscard]] bool IsEndOfStream(BOOL ok, DWORD read) noexcept
{
    return (ok != FALSE && read == 0) || (ok == FALSE && ::GetLastError() == ERROR_BROKEN_PIPE);
}

// True when a whole picture was read, false when the video had ended before its first byte.
// WAIVER(R2): draining a pipe, which offers no collection form; every turn reads at least one byte, so a picture
// takes at most its own size in turns.
// WAIVER(R20): the read waits for the program feeding the pipe; it ends the wait by writing or by closing the pipe,
// which the program that started this one does when the video is cancelled.
[[nodiscard]] Result<bool, Error> ReadPicture(HANDLE input, std::span<std::byte> into) noexcept
{
    std::size_t got = 0;
    while (got < into.size())
    {
        DWORD read = 0;
        const BOOL ok = ::ReadFile(input, into.data() + got, ChunkOf(into.size() - got), &read, nullptr);
        if (IsEndOfStream(ok, read))
            break;
        if (ok == FALSE)
            return Fail(LastError(ApiCall::ReadVideoFrame));
        got += read;
    }
    if (got == 0)
        return false;
    if (got != into.size())
        return Fail(Error{ ApiCall::ReadVideoFrame, static_cast<std::uint32_t>(got) });
    return true;
}

// WAIVER(R2): draining into a pipe; every turn writes at least one byte or fails, so a picture takes at most its
// own size in turns.
// WAIVER(R20): the write waits for the program reading the pipe, which ends it by reading or by closing the pipe.
[[nodiscard]] Status<Error> WritePicture(HANDLE output, std::span<const std::byte> picture) noexcept
{
    std::size_t put = 0;
    while (put < picture.size())
    {
        DWORD written = 0;
        if (::WriteFile(output, picture.data() + put, ChunkOf(picture.size() - put), &written, nullptr) == FALSE || written == 0)
            return Fail(LastError(ApiCall::WriteVideoFrame));
        put += written;
    }
    return {};
}

[[nodiscard]] Status<Error> WrittenTimes(HANDLE output, std::span<const std::byte> picture, std::uint32_t copies) noexcept
{
    return infra::ForEach(std::views::iota(std::uint32_t{ 0 }, copies), Status<Error>{}, [output, picture](std::uint32_t) { return WritePicture(output, picture); });
}

// The rows of a picture copied out of the GPU, each padded to the copy's alignment, packed one after another.
void Packed(const void* rows, const Layout& layout, std::span<std::byte> into, std::uint32_t width, std::uint32_t height) noexcept
{
    const std::size_t rowBytes = static_cast<std::size_t>(width) * kBytesPerPixel;
    const std::byte* const from = static_cast<const std::byte*>(rows) + layout.footprint.Offset;
    // WAIVER(R2): the picture is this environment's own buffer, written row by row once per picture.
    std::ranges::for_each(std::views::iota(std::uint32_t{ 0 }, height),
                          [&](std::uint32_t row) { std::memcpy(into.data() + row * rowBytes, from + static_cast<std::size_t>(row) * layout.footprint.Footprint.RowPitch, rowBytes); });
}

[[nodiscard]] bool HasPresent(const interior::StepList& steps) noexcept
{
    static constexpr auto IsPresent = [] [[nodiscard]] (const interior::Step& step) noexcept -> bool { return std::holds_alternative<interior::Present>(step); };
    return std::ranges::any_of(steps.Items(), IsPresent);
}

[[nodiscard]] bool IsGenerating(interior::FrameGen multiplier) noexcept
{
    return multiplier.Get() > 1;
}

// With frame generation the screen shows each picture one picture late: the frames planned while the first picture
// is the newest show nothing new yet, so they are not written; from the second picture on every frame is.
[[nodiscard]] std::uint32_t CopiesFor(interior::FrameGen multiplier, std::uint64_t read) noexcept
{
    return !IsGenerating(multiplier) || read >= 2 ? 1u : 0u;
}

[[nodiscard]] std::uint32_t StepAfter(std::uint32_t step, interior::FrameGen multiplier) noexcept
{
    return step + 1 == multiplier.Get() ? 0u : step + 1;
}

[[nodiscard]] Result<interior::Instant, Error> TimeOf(std::uint64_t picture, interior::Microseconds period, std::uint32_t step, interior::FrameGen multiplier) noexcept
{
    static constexpr auto FromArithmetic = [] [[nodiscard]] (infra::ArithmeticError error) noexcept -> Error { return Error{ ApiCall::PlanFrame, 200u + static_cast<std::uint32_t>(error) }; };
    return infra::CheckedMul(picture, period.Get())
        .and_then([&](std::uint64_t start) {
            return infra::CheckedMul(period.Get(), std::uint64_t{ step }).and_then([&](std::uint64_t partway) {
                return infra::CheckedAdd(start, partway / multiplier.Get()).transform([](std::uint64_t at) { return interior::InstantTag::Parse(at); });
            });
        })
        .transform_error(FromArithmetic);
}

[[nodiscard]] Result<interior::BackBufferIndex, Error> BufferFor(interior::FrameNumber number) noexcept
{
    return interior::BackBufferIndexTag::Parse(static_cast<std::uint32_t>(number.Get() % interior::kBackBufferCount)).transform_error([](interior::UnitError) {
        return Error{ ApiCall::GetCurrentBackBufferIndex, 2 };
    });
}

[[nodiscard]] FrameStart StartOf(bool fresh, interior::BackBufferIndex buffer, std::optional<interior::Fraction> unmatched, interior::Instant now, bool quit) noexcept
{
    return FrameStart{ interior::FrameInput{ fresh, buffer, unmatched, now, false, false, std::nullopt, std::nullopt, std::nullopt, quit } };
}

} // namespace

VideoEnvironment::VideoEnvironment(VideoGpu gpu, const interior::SessionPlan& plan, interior::Microseconds period, VideoStreams streams, const Console& console) noexcept
    : gpu_(std::move(gpu.gpu)), finestPixels_(gpu.finestPixels), multiplier_(plan.frameGen), period_(period), streams_(streams), console_(console), picture_(PictureBytes(plan.source)),
      finished_(PictureBytes(plan.target)),
      frame_(FrameContext{ interior::FrameNumberTag::Parse(0), interior::SlotOfFrame(interior::FrameNumberTag::Parse(0)), *interior::SetIndexTag::Parse(0), false, gpu.fence }),
      readback_(std::nullopt), read_(0), written_(0), step_(0), tail_(std::nullopt), copies_(0), drawnTo_(*interior::BackBufferIndexTag::Parse(0))
{
}

Error VideoEnvironment::FromPlanError(interior::PlanFrameError error) noexcept
{
    return RealEnvironment::FromPlanError(error);
}

Result<FrameStart, Error> VideoEnvironment::BeginFrame(const interior::FrameState& state) noexcept
{
    static constexpr auto AwaitSlot = [] [[nodiscard]] (const Gpu& gpu, const interior::FrameState& state, interior::FrameSlot slot) noexcept -> Status<Error> {
        return WaitForFence(gpu.device, state.slotFences[slot.Get()], interior::MicrosecondsTag::Parse(kFenceTimeoutMicroseconds)).and_then([&] {
            return Check(gpu.allocators[slot.Get()]->Reset(), ApiCall::ResetAllocator);
        });
    };
    const interior::FrameSlot slot = interior::SlotOfFrame(state.number);
    frame_ = FrameContext{ state.number, slot, state.currentSet, state.hasPrevious, frame_.fence }; // WAIVER(R2): replaced whole each frame.
    return AwaitSlot(gpu_, state, slot).and_then([&] { return UnmatchedShare(gpu_, finestPixels_, state, slot); }).and_then([&](std::optional<interior::Fraction> unmatched) {
        if (tail_.has_value())
            return BufferFor(state.number).transform([&](interior::BackBufferIndex buffer) { return StartOf(false, buffer, unmatched, interior::InstantTag::Parse(0), true); });
        if (step_ == 0)
            return Fresh(state, unmatched);
        return Between(state, unmatched);
    });
}

Result<FrameStart, Error> VideoEnvironment::Fresh(const interior::FrameState& state, std::optional<interior::Fraction> unmatched) noexcept
{
    static constexpr auto Uploaded = [] [[nodiscard]] (const Gpu& gpu, std::span<const std::byte> picture, interior::FrameNumber number) noexcept -> Status<Error> {
        return UploadFrame(gpu.capture, picture, number);
    };
    const Result<bool, Error> whole = ReadPicture(streams_.input, picture_);
    if (!whole.has_value())
        return Fail(whole.error());
    if (!*whole)
        return Ended(state, unmatched);
    return Uploaded(gpu_, picture_, state.number).and_then([&] { return TimeOf(read_, period_, 0, multiplier_); }).and_then([&](interior::Instant now) {
        return BufferFor(state.number).transform([&](interior::BackBufferIndex buffer) {
            read_ += 1;                              // WAIVER(R2): a count, replaced each picture.
            copies_ = CopiesFor(multiplier_, read_); // WAIVER(R2): replaced each frame.
            step_ = StepAfter(0, multiplier_);       // WAIVER(R2): replaced each frame.
            drawnTo_ = buffer;                       // WAIVER(R2): replaced each frame.
            return StartOf(true, buffer, unmatched, now, false);
        });
    });
}

Result<FrameStart, Error> VideoEnvironment::Between(const interior::FrameState& state, std::optional<interior::Fraction> unmatched) noexcept
{
    return TimeOf(read_ - 1, period_, step_, multiplier_).and_then([&](interior::Instant now) {
        return BufferFor(state.number).transform([&](interior::BackBufferIndex buffer) {
            copies_ = CopiesFor(multiplier_, read_); // WAIVER(R2): replaced each frame.
            step_ = StepAfter(step_, multiplier_);   // WAIVER(R2): replaced each frame.
            drawnTo_ = buffer;                       // WAIVER(R2): replaced each frame.
            return StartOf(false, buffer, unmatched, now, false);
        });
    });
}

// The pictures have run out. With frame generation the last one has not been shown yet: one more frame, a whole
// picture after it, shows it, and is written as many times as a picture's frames are so the video keeps its length.
Result<FrameStart, Error> VideoEnvironment::Ended(const interior::FrameState& state, std::optional<interior::Fraction> unmatched) noexcept
{
    static constexpr auto IsOver = [] [[nodiscard]] (std::uint64_t read, interior::FrameGen multiplier) noexcept -> bool { return read == 0 || !IsGenerating(multiplier); };
    tail_ = Tail{ multiplier_.Get() }; // WAIVER(R2): set once, when the pictures run out.
    if (IsOver(read_, multiplier_))
        return BufferFor(state.number).transform([&](interior::BackBufferIndex buffer) { return StartOf(false, buffer, unmatched, interior::InstantTag::Parse(0), true); });
    return TimeOf(read_, period_, 0, multiplier_).and_then([&](interior::Instant now) {
        return BufferFor(state.number).transform([&](interior::BackBufferIndex buffer) {
            copies_ = tail_->copies; // WAIVER(R2): replaced each frame.
            drawnTo_ = buffer;       // WAIVER(R2): replaced each frame.
            return StartOf(false, buffer, unmatched, now, false);
        });
    });
}

Result<ExecutionReport, Error> VideoEnvironment::Execute(const interior::FramePlan& plan) noexcept
{
    static constexpr auto WritesFrame = [] [[nodiscard]] (std::uint32_t copies, const interior::FramePlan& plan) noexcept -> bool { return copies > 0 && HasPresent(plan.steps); };
    const Result<interior::FenceValue, Error> fence = ExecuteSteps(gpu_, frame_, plan.steps);
    if (!fence.has_value())
        return Fail(fence.error());
    frame_ = ContextAt(frame_, *fence); // WAIVER(R2): the last signalled fence, replaced whole per frame.
    if (!WritesFrame(copies_, plan))
        return ExecutionReport{ frame_.fence, static_cast<std::uint32_t>(plan.steps.Size()) };
    return Written(plan, frame_.fence).transform([&] { return ExecutionReport{ frame_.fence, static_cast<std::uint32_t>(plan.steps.Size()) }; });
}

// The frame's picture copied out of its back buffer once the frame has run, and written out as many times as asked.
Status<Error> VideoEnvironment::Written(const interior::FramePlan& plan, interior::FenceValue fence) noexcept
{
    static constexpr auto CopiedAndWaited = [] [[nodiscard]] (const Gpu& gpu, const FrameContext& frame, interior::BackBufferIndex buffer, const interior::StateTable& states,
                                                              const std::optional<Readback>& kept, interior::FenceValue fence) noexcept -> Result<std::pair<Readback, interior::FenceValue>, Error> {
        return OpenList(gpu, frame.slot).and_then([&] { return CopiedOut(gpu, interior::BackBufferId(buffer), states, kept); }).and_then([&](Readback readback) {
            return FlushList(gpu, fence).and_then([&](interior::FenceValue copied) {
                return WaitForFence(gpu.device, copied, interior::MicrosecondsTag::Parse(kFenceTimeoutMicroseconds)).transform([&] { return std::pair{ readback, copied }; });
            });
        });
    };
    return CopiedAndWaited(gpu_, frame_, drawnTo_, plan.next.states, readback_, fence).and_then([&](const std::pair<Readback, interior::FenceValue>& done) {
        readback_ = done.first;                  // WAIVER(R2): the buffer is kept for the next picture.
        frame_ = ContextAt(frame_, done.second); // WAIVER(R2): the last signalled fence, replaced whole.
        return MapReadback(done.first).and_then([&](void* rows) {
            Packed(rows, done.first.layout, finished_, gpu_.presenter.extent.width.Get(), gpu_.presenter.extent.height.Get());
            UnmapReadback(done.first);
            written_ += copies_; // WAIVER(R2): a count, replaced each picture written.
            return WrittenTimes(streams_.output, finished_, copies_);
        });
    });
}

Result<VideoEnvironment, Error> CreateVideoEnvironment(VideoGpu gpu, const interior::SessionPlan& plan, interior::Microseconds period, VideoStreams streams, const Console& console) noexcept
{
    static constexpr auto IsStream = [] [[nodiscard]] (HANDLE handle) noexcept -> bool { return handle != nullptr && handle != INVALID_HANDLE_VALUE; };
    if (!IsStream(streams.input))
        return Fail(Error{ ApiCall::ReadVideoFrame, ERROR_INVALID_HANDLE });
    if (!IsStream(streams.output))
        return Fail(Error{ ApiCall::WriteVideoFrame, ERROR_INVALID_HANDLE });
    return VideoEnvironment(std::move(gpu), plan, period, streams, console);
}

} // namespace real
