#pragma once
#include "effects/real/com.h"
#include "interior/units.h"

#include <wincodec.h>

#include <array>
#include <condition_variable>
#include <mutex>
#include <optional>
#include <thread>

namespace real {

// A picture ready to be written: its pixels, held in a bitmap of their own, and the file they go to.
struct PngJob
{
    Com<IWICBitmap> picture;
    interior::FilePath path;
};

// How many pictures may wait to be written before the frame loop is made to wait instead, which is what
// keeps memory in bounds: a picture of a 4K screen is about 33 megabytes.
constexpr std::size_t kPngQueueDepth = 3;

// Writes PNG files on a thread of its own, so the frame loop hands a picture over and goes on. A picture
// belongs to the frame loop until it is queued and to the writer after, so nothing is shared, only handed
// over under the lock. The first error the writer meets is answered to whoever hands over the next picture
// or drains the queue. Every wait is bounded, and the writer finishes what is queued before it stops.
class PngWriter final
{
public:
    PngWriter() noexcept;
    ~PngWriter();
    PngWriter(const PngWriter&) = delete;
    PngWriter& operator=(const PngWriter&) = delete;
    PngWriter(PngWriter&&) = delete;
    PngWriter& operator=(PngWriter&&) = delete;

    // Queues a picture, waiting for room when the writer is that far behind.
    [[nodiscard]] infra::Status<Error> Submit(PngJob job) noexcept;
    // Waits until every picture queued so far is on disk.
    [[nodiscard]] infra::Status<Error> Drain() noexcept;

private:
    void Run(std::stop_token stop) noexcept;
    void Served(std::stop_token stop) noexcept;
    [[nodiscard]] std::optional<PngJob> Taken(std::stop_token stop) noexcept;
    void Finished(const infra::Status<Error>& written) noexcept;

    std::mutex lock_;
    std::condition_variable_any changed_;
    std::array<std::optional<PngJob>, kPngQueueDepth> queue_; // WAIVER(R2): the pictures waiting, a ring the frame loop fills and the writer empties.
    std::size_t head_;                                        // WAIVER(R2): where the next picture to write sits in the ring.
    std::size_t count_;                                       // WAIVER(R2): how many pictures wait.
    bool writing_;                                            // WAIVER(R2): whether one is being written this moment.
    std::optional<Error> failed_;                             // WAIVER(R2): the first error met, kept until it is answered.
    std::jthread worker_;                                     // last, so everything it uses is there before it starts
};

} // namespace real
