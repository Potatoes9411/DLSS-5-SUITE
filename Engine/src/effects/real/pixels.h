#pragma once
#include <windows.h>

#include <cstddef>

namespace real {

// A picture's size in pixels, as the rows of it laid out in memory are counted.
struct FrameSize
{
    UINT32 width;
    UINT32 height;
};

// The pictures handed to the encoder and to the cursor are rows of blue, green, red and a byte that is ignored.
constexpr std::size_t kBytesPerPixel = 4;

} // namespace real
