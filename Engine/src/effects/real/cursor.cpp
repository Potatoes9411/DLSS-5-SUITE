#include "effects/real/cursor.h"

#include <algorithm>
#include <cmath>
#include <cstring>

namespace real {
namespace {

// How big a cursor is and where its hot spot sits in it. A cursor without colour keeps its two masks in one
// bitmap of twice the height.
struct CursorShape
{
    int width;
    int height;
    int hotspotX;
    int hotspotY;
};

// Where the cursor lands on the picture before it is clipped: its top-left pixel and its size, both scaled
// as the picture is scaled from what it shows.
struct Placement
{
    int left;
    int top;
    int width;
    int height;
};

// The part of a placement that is on the picture.
struct Clip
{
    int left;
    int top;
    int right;
    int bottom;
};

// A bitmap of the cursor's size that the picture's pixels under the cursor are copied into, drawn on and
// copied back from. Drawing needs a device context, which the picture's rows are not; and a cursor's masks
// may invert what is under them, which only drawing over the real pixels gets right.
struct Canvas
{
    HDC dc;
    HBITMAP bitmap;
    HGDIOBJ previous;
    BYTE* bits;
};

[[nodiscard]] std::optional<CursorShape> ShapeOf(HCURSOR cursor) noexcept
{
    ICONINFO info{}; // WAIVER(R2): the answer of one query, read once after it.
    if (::GetIconInfo(cursor, &info) == FALSE)
        return std::nullopt;
    BITMAP mask{}; // WAIVER(R2): the answer of one query, read once after it.
    const bool measured = ::GetObjectW(info.hbmMask, sizeof(mask), &mask) != 0;
    const bool coloured = info.hbmColor != nullptr;
    // The query hands over copies of the cursor's bitmaps, which are the caller's to free.
    if (coloured)
        (void)::DeleteObject(info.hbmColor);
    (void)::DeleteObject(info.hbmMask);
    if (!measured || mask.bmWidth <= 0 || mask.bmHeight <= 0)
        return std::nullopt;
    return CursorShape{ .width = mask.bmWidth, .height = coloured ? mask.bmHeight : mask.bmHeight / 2, .hotspotX = static_cast<int>(info.xHotspot), .hotspotY = static_cast<int>(info.yHotspot) };
}

[[nodiscard]] Placement PlacementOf(const CursorShape& shape, const CursorOverlay& overlay, const FrameSize& size, const FrameSize& source) noexcept
{
    static constexpr auto Scaled = [] [[nodiscard]] (long value, UINT32 to, UINT32 from) noexcept -> int {
        return static_cast<int>(std::lround(static_cast<double>(value) * static_cast<double>(to) / static_cast<double>(from)));
    };
    const long x = overlay.sample.position.x - overlay.origin.x - shape.hotspotX;
    const long y = overlay.sample.position.y - overlay.origin.y - shape.hotspotY;
    return Placement{ .left = Scaled(x, size.width, source.width),
                      .top = Scaled(y, size.height, source.height),
                      .width = std::max(1, Scaled(shape.width, size.width, source.width)),
                      .height = std::max(1, Scaled(shape.height, size.height, source.height)) };
}

[[nodiscard]] std::optional<Clip> ClipOf(const Placement& placement, const FrameSize& size) noexcept
{
    const Clip clip{ .left = std::max(placement.left, 0),
                     .top = std::max(placement.top, 0),
                     .right = std::min(placement.left + placement.width, static_cast<int>(size.width)),
                     .bottom = std::min(placement.top + placement.height, static_cast<int>(size.height)) };
    if (clip.right <= clip.left || clip.bottom <= clip.top)
        return std::nullopt;
    return clip;
}

// A top-down bitmap of blue, green, red and a spare byte, which is how the picture's rows lie, so pixels
// pass between the two unchanged.
[[nodiscard]] BITMAPINFO DescriptionOf(const Placement& placement) noexcept
{
    return BITMAPINFO{ .bmiHeader = BITMAPINFOHEADER{ .biSize = sizeof(BITMAPINFOHEADER),
                                                      .biWidth = placement.width,
                                                      .biHeight = -placement.height,
                                                      .biPlanes = 1,
                                                      .biBitCount = 32,
                                                      .biCompression = BI_RGB,
                                                      .biSizeImage = 0,
                                                      .biXPelsPerMeter = 0,
                                                      .biYPelsPerMeter = 0,
                                                      .biClrUsed = 0,
                                                      .biClrImportant = 0 },
                       .bmiColors = { RGBQUAD{ .rgbBlue = 0, .rgbGreen = 0, .rgbRed = 0, .rgbReserved = 0 } } };
}

[[nodiscard]] std::optional<Canvas> CanvasOf(const Placement& placement) noexcept
{
    const BITMAPINFO description = DescriptionOf(placement);
    void* bits = nullptr; // WAIVER(R2): the answer of one call, read once after it.
    const HBITMAP bitmap = ::CreateDIBSection(nullptr, &description, DIB_RGB_COLORS, &bits, nullptr, 0);
    if (bitmap == nullptr || bits == nullptr)
        return std::nullopt;
    const HDC dc = ::CreateCompatibleDC(nullptr);
    if (dc == nullptr)
    {
        (void)::DeleteObject(bitmap);
        return std::nullopt;
    }
    return Canvas{ .dc = dc, .bitmap = bitmap, .previous = ::SelectObject(dc, bitmap), .bits = static_cast<BYTE*>(bits) };
}

void Freed(const Canvas& canvas) noexcept
{
    (void)::SelectObject(canvas.dc, canvas.previous);
    (void)::DeleteDC(canvas.dc);
    (void)::DeleteObject(canvas.bitmap);
}

// WAIVER(R2): the rows under the cursor are copied with a loop, one row at a time, in both directions.
void CopiedRows(const BYTE* from, std::size_t fromStride, BYTE* to, std::size_t toStride, std::size_t bytes, int rows) noexcept
{
    for (int y = 0; y < rows; ++y)
        std::memcpy(to + static_cast<std::size_t>(y) * toStride, from + static_cast<std::size_t>(y) * fromStride, bytes);
}

// The pixels under the cursor go onto the canvas, the cursor is drawn over them at the size it was placed
// at, and they come back. A drawing that fails leaves the picture as it was.
void Drawn(const Canvas& canvas, const Placement& placement, const Clip& clip, HCURSOR shape, BYTE* rows, std::size_t stride) noexcept
{
    const std::size_t canvasStride = static_cast<std::size_t>(placement.width) * kBytesPerPixel;
    BYTE* const picture = rows + static_cast<std::size_t>(clip.top) * stride + static_cast<std::size_t>(clip.left) * kBytesPerPixel;
    BYTE* const under = canvas.bits + static_cast<std::size_t>(clip.top - placement.top) * canvasStride + static_cast<std::size_t>(clip.left - placement.left) * kBytesPerPixel;
    const std::size_t bytes = static_cast<std::size_t>(clip.right - clip.left) * kBytesPerPixel;
    CopiedRows(picture, stride, under, canvasStride, bytes, clip.bottom - clip.top);
    if (::DrawIconEx(canvas.dc, 0, 0, shape, placement.width, placement.height, 0, nullptr, DI_NORMAL) == FALSE)
        return;
    (void)::GdiFlush();
    CopiedRows(under, canvasStride, picture, stride, bytes, clip.bottom - clip.top);
}

} // namespace

CursorSample SampleCursor() noexcept
{
    CURSORINFO info{ .cbSize = sizeof(CURSORINFO), .flags = 0, .hCursor = nullptr, .ptScreenPos = POINT{ .x = 0, .y = 0 } }; // WAIVER(R2): filled by the query, then read once.
    if (::GetCursorInfo(&info) == FALSE)
        return CursorSample{ .position = POINT{ .x = 0, .y = 0 }, .shape = nullptr, .showing = false };
    return CursorSample{ .position = info.ptScreenPos, .shape = info.hCursor, .showing = (info.flags & CURSOR_SHOWING) != 0 };
}

void DrawCursorOnto(BYTE* rows, std::size_t stride, const FrameSize& size, const FrameSize& source, const CursorOverlay& overlay) noexcept
{
    if (!overlay.sample.showing || overlay.sample.shape == nullptr || size.width == 0 || size.height == 0 || source.width == 0 || source.height == 0)
        return;
    const std::optional<CursorShape> shape = ShapeOf(overlay.sample.shape);
    if (!shape.has_value())
        return;
    const Placement placement = PlacementOf(*shape, overlay, size, source);
    const std::optional<Clip> clip = ClipOf(placement, size);
    if (!clip.has_value())
        return;
    const std::optional<Canvas> canvas = CanvasOf(placement);
    if (!canvas.has_value())
        return;
    Drawn(*canvas, placement, *clip, overlay.sample.shape, rows, stride);
    Freed(*canvas);
}

} // namespace real
