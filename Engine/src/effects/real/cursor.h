#pragma once
#include "effects/real/pixels.h"

#include <optional>

namespace real {

// Where the cursor was and what it looked like at one moment. The shape is the system's handle to the cursor
// as it was then, which is not owned here; a cursor that has since been destroyed is drawn as nothing.
struct CursorSample
{
    POINT position; // on the screen, in the physical pixels this program is told about
    HCURSOR shape;
    bool showing;
};

// A cursor to draw into a picture: the sample, and where the picture's top-left pixel is on the screen.
struct CursorOverlay
{
    CursorSample sample;
    POINT origin;
};

[[nodiscard]] CursorSample SampleCursor() noexcept;

// Draws the cursor onto a top-down picture of blue, green, red and a spare byte, `size` big with `stride`
// bytes between rows, which shows `source` pixels of the screen starting at the overlay's origin: a
// picture bigger than what it shows draws the cursor bigger by the same amount. A cursor that is hidden,
// gone or off the picture leaves the picture as it was.
void DrawCursorOnto(BYTE* rows, std::size_t stride, const FrameSize& size, const FrameSize& source, const CursorOverlay& overlay) noexcept;

} // namespace real
