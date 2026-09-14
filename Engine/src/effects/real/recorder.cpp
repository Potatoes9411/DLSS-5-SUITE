#include "effects/real/recorder.h"

#include "infrastructure/array_util.h"
#include "infrastructure/fold.h"

#include <mfapi.h>
#include <mferror.h>
#include <mfidl.h>

#include <algorithm>
#include <cmath>
#include <cstring>
#include <ranges>

namespace real {
namespace {

using infra::Fail;
using infra::Result;
using infra::Status;

constexpr wchar_t kOriginalSuffix[] = L"_original.mp4";
constexpr wchar_t kProcessedSuffix[] = L"_processed.mp4";
constexpr UINT32 kNominalFrameRate = 60;
constexpr LONGLONG kTicksPerMicrosecond = 10; // Media Foundation counts time in 100 ns
constexpr LONGLONG kNominalDuration = 10000000 / kNominalFrameRate;
constexpr UINT64 kBitsPerPixelPerSecond = 7; // 1080p comes to about 14 Mbit/s, 4K to about 58
constexpr UINT64 kMaxBitrate = 100000000;
constexpr std::size_t kHalvesPerPixel = 4;

// What is drawn over a picture once it is converted: the cursor, placed for the screen area the picture shows,
// which is the original picture's size for both pictures of a frame.
struct Overlaid
{
    FrameSize source;
    std::optional<CursorOverlay> cursor;
};

// The encoder takes no odd size, so a picture with one loses its last column or row.
[[nodiscard]] UINT32 Even(UINT32 size) noexcept
{
    return size & ~1u;
}

[[nodiscard]] FrameSize EvenSizeOf(const Layout& layout) noexcept
{
    return FrameSize{ Even(layout.footprint.Footprint.Width), Even(layout.footprint.Footprint.Height) };
}

[[nodiscard]] UINT32 BitrateOf(const FrameSize& size) noexcept
{
    return static_cast<UINT32>(std::min(static_cast<UINT64>(size.width) * size.height * kBitsPerPixelPerSecond, kMaxBitrate));
}

[[nodiscard]] Result<Com<IMFMediaType>, Error> VideoType(const GUID& subtype, const FrameSize& size) noexcept
{
    Com<IMFMediaType> type; // WAIVER(R2): the answer of one call, filled by the ones after it.
    return Check(::MFCreateMediaType(&type), ApiCall::MfConfigureStream)
        .and_then([&] { return Check(type->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video), ApiCall::MfConfigureStream); })
        .and_then([&] { return Check(type->SetGUID(MF_MT_SUBTYPE, subtype), ApiCall::MfConfigureStream); })
        .and_then([&] { return Check(::MFSetAttributeSize(type.Get(), MF_MT_FRAME_SIZE, size.width, size.height), ApiCall::MfConfigureStream); })
        .and_then([&] { return Check(::MFSetAttributeRatio(type.Get(), MF_MT_FRAME_RATE, kNominalFrameRate, 1), ApiCall::MfConfigureStream); })
        .and_then([&] { return Check(type->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive), ApiCall::MfConfigureStream); })
        .transform([&type] { return type; });
}

[[nodiscard]] Result<Com<IMFMediaType>, Error> OutputType(const FrameSize& size) noexcept
{
    return VideoType(MFVideoFormat_H264, size).and_then([&](const Com<IMFMediaType>& type) {
        return Check(type->SetUINT32(MF_MT_AVG_BITRATE, BitrateOf(size)), ApiCall::MfConfigureStream).transform([&type] { return type; });
    });
}

// The pictures are handed over as rows of blue, green, red and a byte the encoder ignores, top row first: a
// positive stride is how Media Foundation is told the rows run top-down.
[[nodiscard]] Result<Com<IMFMediaType>, Error> InputType(const FrameSize& size) noexcept
{
    return VideoType(MFVideoFormat_RGB32, size).and_then([&](const Com<IMFMediaType>& type) {
        return Check(type->SetUINT32(MF_MT_DEFAULT_STRIDE, size.width * static_cast<UINT32>(kBytesPerPixel)), ApiCall::MfConfigureStream)
            .and_then([&] { return Check(type->SetUINT32(MF_MT_ALL_SAMPLES_INDEPENDENT, TRUE), ApiCall::MfConfigureStream); })
            .transform([&type] { return type; });
    });
}

[[nodiscard]] Result<Track, Error> OpenedTrack(const interior::FilePath& file, const Layout& layout) noexcept
{
    static constexpr auto WriterFor = [] [[nodiscard]] (const interior::FilePath& file) noexcept -> Result<Com<IMFSinkWriter>, Error> {
        Com<IMFSinkWriter> writer; // WAIVER(R2): the answer of one call, read once after it.
        return Check(::MFCreateSinkWriterFromURL(file.CString(), nullptr, nullptr, &writer), ApiCall::MfCreateSinkWriter).transform([&writer] { return writer; });
    };

    static constexpr auto StreamOf = [] [[nodiscard]] (const Com<IMFSinkWriter>& writer, const FrameSize& size) noexcept -> Result<DWORD, Error> {
        DWORD stream = 0; // WAIVER(R2): the answer of one call, read once after it.
        return OutputType(size)
            .and_then([&](const Com<IMFMediaType>& output) { return Check(writer->AddStream(output.Get(), &stream), ApiCall::MfConfigureStream); })
            .and_then([&] { return InputType(size); })
            .and_then([&](const Com<IMFMediaType>& input) { return Check(writer->SetInputMediaType(stream, input.Get(), nullptr), ApiCall::MfConfigureStream); })
            .transform([&stream] { return stream; });
    };
    const FrameSize size = EvenSizeOf(layout);
    return WriterFor(file).and_then([&](const Com<IMFSinkWriter>& writer) {
        return StreamOf(writer, size).and_then([&](DWORD stream) { return Check(writer->BeginWriting(), ApiCall::MfBeginWriting).transform([&] { return Track{ writer, stream, file }; }); });
    });
}

// A half-precision float as a byte of 0 to 255: anything at or past 1 is 255, and anything below 0, and the
// tiny values with no exponent, are 0.
[[nodiscard]] BYTE ByteOfHalf(std::uint16_t half) noexcept
{
    const bool negative = (half & 0x8000u) != 0;
    const int exponent = static_cast<int>((half >> 10) & 0x1Fu);
    const int mantissa = static_cast<int>(half & 0x3FFu);
    if (negative || exponent == 0)
        return 0;
    if (exponent >= 15)
        return 255;
    const float value = std::ldexp(1.0f + static_cast<float>(mantissa) / 1024.0f, exponent - 15);
    return static_cast<BYTE>(std::lround(value * 255.0f));
}

// WAIVER(R2): every pixel of both pictures passes through here each frame, so the rows are converted with
// loops rather than ranges, which is what keeps a frame's worth within a frame.
void ConvertRow(const std::byte* row, DXGI_FORMAT format, UINT32 width, BYTE* out) noexcept
{
    if (format == DXGI_FORMAT_B8G8R8A8_UNORM || format == DXGI_FORMAT_B8G8R8A8_UNORM_SRGB)
    {
        std::memcpy(out, row, width * kBytesPerPixel);
        return;
    }
    if (format == DXGI_FORMAT_R16G16B16A16_FLOAT)
    {
        const std::uint16_t* halves = reinterpret_cast<const std::uint16_t*>(row);
        for (UINT32 x = 0; x < width; ++x)
        {
            out[x * kBytesPerPixel + 0] = ByteOfHalf(halves[x * kHalvesPerPixel + 2]);
            out[x * kBytesPerPixel + 1] = ByteOfHalf(halves[x * kHalvesPerPixel + 1]);
            out[x * kBytesPerPixel + 2] = ByteOfHalf(halves[x * kHalvesPerPixel + 0]);
            out[x * kBytesPerPixel + 3] = 255;
        }
        return;
    }
    const BYTE* bytes = reinterpret_cast<const BYTE*>(row);
    for (UINT32 x = 0; x < width; ++x)
    {
        out[x * kBytesPerPixel + 0] = bytes[x * kBytesPerPixel + 2];
        out[x * kBytesPerPixel + 1] = bytes[x * kBytesPerPixel + 1];
        out[x * kBytesPerPixel + 2] = bytes[x * kBytesPerPixel + 0];
        out[x * kBytesPerPixel + 3] = 255;
    }
}

// WAIVER(R2): the rows are walked with a loop, for the reason given above.
void ConvertRows(const std::byte* rows, const Layout& layout, const FrameSize& size, BYTE* out) noexcept
{
    const UINT pitch = layout.footprint.Footprint.RowPitch;
    const DXGI_FORMAT format = layout.footprint.Footprint.Format;
    for (UINT32 y = 0; y < size.height; ++y)
        ConvertRow(rows + static_cast<std::size_t>(y) * pitch, format, size.width, out + static_cast<std::size_t>(y) * size.width * kBytesPerPixel);
}

// WAIVER(R12): DXGI's formats are an open set; the ones a picture of ours can be in are named, and the rest refused.
[[nodiscard]] Status<Error> Convertible(DXGI_FORMAT format) noexcept
{
    switch (format)
    {
    case DXGI_FORMAT_R8G8B8A8_UNORM:
    case DXGI_FORMAT_R8G8B8A8_UNORM_SRGB:
    case DXGI_FORMAT_B8G8R8A8_UNORM:
    case DXGI_FORMAT_B8G8R8A8_UNORM_SRGB:
    case DXGI_FORMAT_R16G16B16A16_FLOAT: return {};
    default: return Fail(Error{ ApiCall::SnapshotFormat, static_cast<std::uint32_t>(format) });
    }
}

[[nodiscard]] Status<Error> WrittenSample(const Track& track, const Readback& readback, const void* rows, const Overlaid& overlaid, LONGLONG time, LONGLONG duration) noexcept
{
    static constexpr auto BufferOf = [] [[nodiscard]] (const Readback& r, const void* rows, const FrameSize& size, const Overlaid& overlaid) noexcept -> Result<Com<IMFMediaBuffer>, Error> {
        static constexpr auto Drawn = [](BYTE* out, const FrameSize& size, const Overlaid& overlaid) noexcept -> void {
            if (overlaid.cursor.has_value())
                DrawCursorOnto(out, size.width * kBytesPerPixel, size, overlaid.source, *overlaid.cursor);
        };

        static constexpr auto Filled = [] [[nodiscard]] (const Com<IMFMediaBuffer>& buffer, const Readback& r, const void* rows, const FrameSize& size, const Overlaid& overlaid,
                                                         DWORD bytes) noexcept -> Status<Error> {
            BYTE* out = nullptr; // WAIVER(R2): the answers of one call, read once after it.
            DWORD most = 0;
            DWORD current = 0;
            return Check(buffer->Lock(&out, &most, &current), ApiCall::MfCreateSample).and_then([&] {
                ConvertRows(static_cast<const std::byte*>(rows), r.layout, size, out);
                Drawn(out, size, overlaid);
                (void)buffer->Unlock();
                return Check(buffer->SetCurrentLength(bytes), ApiCall::MfCreateSample);
            });
        };
        const DWORD bytes = size.width * size.height * static_cast<DWORD>(kBytesPerPixel);
        Com<IMFMediaBuffer> buffer; // WAIVER(R2): the answer of one call, filled by the ones after it.
        return Check(::MFCreateMemoryBuffer(bytes, &buffer), ApiCall::MfCreateSample).and_then([&] { return Filled(buffer, r, rows, size, overlaid, bytes); }).transform([&buffer] { return buffer; });
    };

    static constexpr auto SampleOf = [] [[nodiscard]] (const Com<IMFMediaBuffer>& buffer, LONGLONG time, LONGLONG duration) noexcept -> Result<Com<IMFSample>, Error> {
        Com<IMFSample> sample; // WAIVER(R2): the answer of one call, filled by the ones after it.
        return Check(::MFCreateSample(&sample), ApiCall::MfCreateSample)
            .and_then([&] { return Check(sample->AddBuffer(buffer.Get()), ApiCall::MfCreateSample); })
            .and_then([&] { return Check(sample->SetSampleTime(time), ApiCall::MfCreateSample); })
            .and_then([&] { return Check(sample->SetSampleDuration(duration), ApiCall::MfCreateSample); })
            .transform([&sample] { return sample; });
    };
    return Convertible(readback.layout.footprint.Footprint.Format)
        .and_then([&] { return BufferOf(readback, rows, EvenSizeOf(readback.layout), overlaid); })
        .and_then([&](const Com<IMFMediaBuffer>& buffer) { return SampleOf(buffer, time, duration); })
        .and_then([&](const Com<IMFSample>& sample) { return Check(track.writer->WriteSample(track.stream, sample.Get()), ApiCall::MfWriteSample); });
}

// The copy is mapped for as long as the encoder takes to be handed it, and unmapped either way.
[[nodiscard]] Status<Error> EncodedCopy(const Track& track, const Readback& readback, const Overlaid& overlaid, LONGLONG time, LONGLONG duration) noexcept
{
    static constexpr auto WrittenThenUnmapped = [] [[nodiscard]] (const Track& track, const Readback& readback, const void* rows, const Overlaid& overlaid, LONGLONG time,
                                                                  LONGLONG duration) noexcept -> Status<Error> {
        const Status<Error> written = WrittenSample(track, readback, rows, overlaid, time, duration);
        UnmapReadback(readback);
        return written;
    };
    return MapReadback(readback).and_then([&](void* rows) { return WrittenThenUnmapped(track, readback, rows, overlaid, time, duration); });
}

[[nodiscard]] Result<Tracks, Error> TracksOf(const VideoRecording& recording, const Pending& pending) noexcept
{
    if (recording.tracks.has_value())
        return *recording.tracks;
    return OpenedTrack(recording.files.original, pending.original.layout).and_then([&](const Track& original) {
        return OpenedTrack(recording.files.processed, pending.processed.layout).transform([&](const Track& processed) { return Tracks{ original, processed }; });
    });
}

// Each frame's time is its own clock reading from the start, the same on both tracks. Its length is what
// went by since the frame before, since what will go by until the next is not known yet; the first is a
// frame at the nominal rate.
[[nodiscard]] LONGLONG TimeOf(const VideoRecording& recording, interior::Instant when) noexcept
{
    return static_cast<LONGLONG>(when.Get() - std::min(when.Get(), recording.started.Get())) * kTicksPerMicrosecond;
}

[[nodiscard]] LONGLONG DurationOf(const VideoRecording& recording, interior::Instant when) noexcept
{
    if (!recording.last.has_value() || when.Get() <= recording.last->Get())
        return kNominalDuration;
    return static_cast<LONGLONG>(when.Get() - recording.last->Get()) * kTicksPerMicrosecond;
}

[[nodiscard]] VideoRecording WithSlot(const VideoRecording& recording, std::size_t slot, const Pending& pending, const std::optional<Tracks>& tracks,
                                      const std::optional<interior::Instant>& last) noexcept
{
    return VideoRecording{ recording.files, tracks, recording.started, last, infra::WithElement(recording.slots, slot, std::optional<Pending>{ pending }) };
}

[[nodiscard]] Pending Encoded(const Pending& pending) noexcept
{
    return Pending{ pending.original, pending.processed, pending.when, false, pending.cursor };
}

[[nodiscard]] std::optional<Readback> KeptOf(const std::optional<Pending>& slot, bool original) noexcept
{
    if (!slot.has_value())
        return std::nullopt;
    return original ? slot->original : slot->processed;
}

// The list is closed, run and given a fence, and not waited for: the frame loop goes on, and the copies are
// read when the slot comes round and its fence has passed.
[[nodiscard]] Result<interior::FenceValue, Error> SubmittedList(const Gpu& gpu, interior::FenceValue previous) noexcept
{
    return Check(gpu.list->Close(), ApiCall::CloseCommandList).and_then([&] { return ExecuteList(gpu.device, gpu.list.Get()); }).and_then([&] { return SignalFence(gpu.device, previous); });
}

[[nodiscard]] Status<Error> Finalized(const std::optional<Tracks>& tracks) noexcept
{
    if (!tracks.has_value())
        return {};
    return Check(tracks->original.writer->Finalize(), ApiCall::MfFinalize).and_then([&] { return Check(tracks->processed.writer->Finalize(), ApiCall::MfFinalize); });
}

} // namespace

Result<VideoRecording, Error> StartRecording(const SnapshotOrder& order, interior::Instant now) noexcept
{
    return EnsureCaptureFolder(order.folder)
        .and_then([&] { return FreeCaptureNames(order.folder, interior::CaptureStemOf(order.label, order.live, order.everything, order.when), kOriginalSuffix, kProcessedSuffix); })
        .and_then([&](const CaptureFiles& files) {
            return Check(::MFStartup(MF_VERSION, MFSTARTUP_LITE), ApiCall::MfStartup).transform([&] { return VideoRecording{ files, std::nullopt, now, std::nullopt, {} }; });
        });
}

Result<Recorded, Error> RecordFrame(const Gpu& gpu, const FrameContext& frame, const interior::FrameState& after, interior::Instant now, const std::optional<CursorOverlay>& cursor,
                                    VideoRecording recording) noexcept
{
    const std::size_t slot = frame.slot.Get();
    return OpenList(gpu, frame.slot)
        .and_then([&] { return CopiedOut(gpu, interior::SimpleId(interior::ResourceKind::ModelColor), after.states, KeptOf(recording.slots[slot], true)); })
        .and_then([&](const Readback& original) {
            return CopiedOut(gpu, interior::SimpleId(after.displaySource), after.states, KeptOf(recording.slots[slot], false)).transform([&](const Readback& processed) {
                return Pending{ original, processed, now, true, cursor };
            });
        })
        .and_then([&](const Pending& pending) {
            return SubmittedList(gpu, frame.fence).transform([&](interior::FenceValue fence) { return Recorded{ WithSlot(recording, slot, pending, recording.tracks, recording.last), fence }; });
        });
}

Result<VideoRecording, Error> DrainedSlot(VideoRecording recording, interior::FrameSlot slot) noexcept
{
    const std::optional<Pending>& pending = recording.slots[slot.Get()];
    if (!pending.has_value() || !pending->waiting)
        return recording;
    const LONGLONG time = TimeOf(recording, pending->when);
    const LONGLONG duration = DurationOf(recording, pending->when);
    const Overlaid overlaid{ EvenSizeOf(pending->original.layout), pending->cursor };
    return TracksOf(recording, *pending).and_then([&](const Tracks& tracks) {
        return EncodedCopy(tracks.original, pending->original, overlaid, time, duration)
            .and_then([&] { return EncodedCopy(tracks.processed, pending->processed, overlaid, time, duration); })
            .transform([&] { return WithSlot(recording, slot.Get(), Encoded(*pending), tracks, pending->when); });
    });
}

Result<Stopped, Error> StopRecording(const Gpu& gpu, interior::FenceValue fence, VideoRecording recording) noexcept
{
    static constexpr auto DrainedAll = [] [[nodiscard]] (const VideoRecording& recording) noexcept -> Result<VideoRecording, Error> {
        const auto slots = std::views::iota(std::uint32_t{ 0 }, interior::kFrameSlotCount);
        return infra::FoldResult(slots, Result<VideoRecording, Error>(recording),
                                 [](const VideoRecording& so, std::uint32_t slot) { return DrainedSlot(so, interior::FrameSlotTag::Parse(slot).value_or(*interior::FrameSlotTag::Parse(0))); });
    };
    return WaitIdle(gpu.device, fence).and_then([&](interior::FenceValue idle) {
        return DrainedAll(recording).and_then([&](const VideoRecording& drained) {
            return Finalized(drained.tracks).transform([&] {
                (void)::MFShutdown();
                return Stopped{ drained.files, idle };
            });
        });
    });
}

interior::Microseconds Elapsed(const VideoRecording& recording, interior::Instant now) noexcept
{
    return interior::MicrosecondsTag::Parse(now.Get() - std::min(now.Get(), recording.started.Get()));
}

} // namespace real
