#include "effects/real/png_writer.h"

#include "effects/real/snapshot.h"

#include <chrono>

namespace real {
namespace {

using infra::Fail;
using infra::Result;
using infra::Status;

// Longer than any one picture takes to write; past it the writer is taken to be stuck.
constexpr std::chrono::seconds kPictureWait{ 60 };

} // namespace

PngWriter::PngWriter() noexcept : lock_(), changed_(), queue_{}, head_(0), count_(0), writing_(false), failed_(std::nullopt), worker_([this](std::stop_token stop) { Run(stop); }) {}

// The thread is joined by its own destructor, after the pictures still queued are written.
PngWriter::~PngWriter()
{
    worker_.request_stop();
    changed_.notify_all();
}

Status<Error> PngWriter::Submit(PngJob job) noexcept
{
    std::unique_lock<std::mutex> guard(lock_);
    if (!changed_.wait_for(guard, kPictureWait, [this] { return count_ < kPngQueueDepth || failed_.has_value(); }))
        return Fail(Error{ ApiCall::PngWriteTimeout, 0 });
    if (failed_.has_value())
        return Fail(*failed_);
    queue_[(head_ + count_) % kPngQueueDepth] = std::move(job);
    count_ += 1;
    guard.unlock();
    changed_.notify_all();
    return {};
}

Status<Error> PngWriter::Drain() noexcept
{
    std::unique_lock<std::mutex> guard(lock_);
    if (!changed_.wait_for(guard, kPictureWait * (kPngQueueDepth + 1), [this] { return (count_ == 0 && !writing_) || failed_.has_value(); }))
        return Fail(Error{ ApiCall::PngWriteTimeout, 0 });
    if (failed_.has_value())
        return Fail(*failed_);
    return {};
}

// The next picture to write, once there is one; nothing once the writer is asked to stop and none is left.
std::optional<PngJob> PngWriter::Taken(std::stop_token stop) noexcept
{
    std::unique_lock<std::mutex> guard(lock_);
    (void)changed_.wait(guard, stop, [this] { return count_ > 0; });
    if (count_ == 0)
        return std::nullopt;
    std::optional<PngJob> job = std::move(queue_[head_]);
    queue_[head_] = std::nullopt;
    head_ = (head_ + 1) % kPngQueueDepth;
    count_ -= 1;
    writing_ = true;
    guard.unlock();
    changed_.notify_all();
    return job;
}

void PngWriter::Finished(const Status<Error>& written) noexcept
{
    const std::lock_guard<std::mutex> guard(lock_);
    writing_ = false;
    if (!written.has_value() && !failed_.has_value())
        failed_ = written.error();
    changed_.notify_all();
}

// The thread has a factory of its own: WIC's objects are handed over between threads here, never shared.
void PngWriter::Served(std::stop_token stop) noexcept
{
    static constexpr auto Written = [] [[nodiscard]] (const Result<Com<IWICImagingFactory>, Error>& wic, const PngJob& job) noexcept -> Status<Error> {
        return wic.and_then([&](const Com<IWICImagingFactory>& factory) { return WriteHeldPng(factory.Get(), job.picture, job.path); });
    };
    const Result<Com<IWICImagingFactory>, Error> wic = MadeWicFactory();
    // WAIVER(R2): the writer's loop, one picture at a time until it is stopped and the queue is empty.
    for (std::optional<PngJob> job = Taken(stop); job.has_value(); job = Taken(stop))
        Finished(Written(wic, *job));
}

// COM is set up for the thread, which WIC needs, and taken down after the last WIC object is let go of.
void PngWriter::Run(std::stop_token stop) noexcept
{
    const HRESULT initialised = ::CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    Served(stop);
    if (SUCCEEDED(initialised))
        ::CoUninitialize();
}

} // namespace real
