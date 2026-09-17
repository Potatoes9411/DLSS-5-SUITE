#pragma once

// Creates the overlay window that hosts the swapchain and the "Input captured" badge above it.
void CreateOverlayWindows();

// Switches the overlay between passing clicks through to Roblox and receiving them itself.
void SetEditMode(bool enabled);

// Keeps the overlay exactly over Roblox while Roblox is the foreground window (or while editing).
void UpdateOverlay();
