#pragma once

#include <d3d11.h>

// Loads the depth model from beside the exe and registers the host as a ReShade add-on that supplies
// the DEPTH texture. Prints why depth is unavailable and returns false otherwise.
bool InitDepth();

// Estimates depth for the captured frame on a worker thread and publishes the newest result to ReShade.
// Frames that arrive while an estimate is in progress are skipped.
void UpdateDepth(ID3D11Texture2D* frame);

void ShutdownDepth();
