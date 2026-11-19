# DLSS 5 SUITE v1.3.0

The biggest SUITE update so far: four new editions, a pick-your-parts installer, nine new frame generators, a YouTube player that no longer freezes, and every install-flow improvement from DLSS 5 MANAGER 1.3.8 folded in. SUITE is now fully its own project.

## Four new editions

Open them from the new edition menu (the hamburger in the top corner).

- **Space Press** squishes games with Windows' own file compression to free drive space. It has Games, Apps and Statistics tabs, a drive graph and a progress strip, and an hourly check that keeps games squished. It pauses while you play. A new **Clean up** tab removes SUITE's own stale caches, old logs, downloads and leftover folders, and shows each item's size and files first.
- **Upscaler Vault** finds every game's DLSS, FSR, XeSS and XeLL DLLs. Swap any of them to another version and restore the original with one click.
- **Enabler Dock** installs DLSS Enabler with the right proxy for the game, a merged ini, and a tracked, clean uninstall.
- **Motion Forge** is a frame-generation overlay for any game or window. Only one overlay runs at a time, so it and the Screen Engine never clash.

## Frame generation, everywhere

The Screen Engine and Motion Forge now share every generator:

- **NVIDIA DLSS Frame Generation (MFG)** through Streamline, with a **version picker** for the DLSS-G build.
- **AMD FSR 3.1 Frame Generation**, using AMD's signed FidelityFX runtime.
- **Intel XeSS MFG**, **SMFG**, **SMT** and **SMAI** (AI-assisted).
- **New SUITE generators:**
  - **MEMC**: TV-style motion matching, the most accurate in testing.
  - **DIS**: dense optical flow.
  - **Spacewarp**: extrapolation, about one frame less delay.
  - **Motion-blur blend**, **Crossfade** and **Repeat**, for comparison.
- 2x to 6x on any D3D12 card. If a generator can't start, SUITE says why and falls back to the engine's blend.
- **Fast motion guard** in Motion Forge, off by default.

## Installing, from DLSS 5 MANAGER 1.3.8

- **Advanced install**: an "Advanced install..." button beside Install lists every part of a route, such as ReShade, effects, feed, RenoDX, OptiScaler, Streamline, DLSS and the model. Turn each one on or off by itself, or add your own files. SUITE's normal uninstall still removes everything.
- **RenoDX options**: MFG unlock add-on, the Multipass build, and deep-fried chicken (also on 32-bit DX9/DX11 through host64).
- **Emulator ReShade API**: Auto, DXGI or Vulkan.
- **Rename the proxy DLL** for OptiScaler, ReShade and AMD installs.
- **Hotkeys**: choose the keys that open ReShade and OptiScaler's menu.
- **RTX 40 OptiScaler build**, used automatically on RTX 40 cards, with Replace/Restore in Settings.
- **Install record in the game folder**: a game still shows as installed, and can still be removed, even if SUITE's data folder is lost.
- **Remove and never scan again** on the game's right-click menu.
- Uninstall also removes the effect files the overlay writes for itself.

## Library and settings

- Game cards show **every graphics API** a game ships, for example "DX12 +2". Routing still uses the main one.
- **Start with Windows** (a Startup-folder shortcut, no registry), **Minimise to tray**, and **Scan for games when SUITE starts**.
- **Custom background**: pick your own image, set its brightness, or reset it.
- **Restore original cover** after picking your own.
- The window reopens where you last closed it.
- Settings' back button returns to the page you came from.

## Fixes

- **YouTube and video player no longer freeze.** DLSS 5 takes 12-16 seconds to start (about a minute the very first time). Your video now waits behind a short "DLSS 5 is starting..." clip and plays from its first frame. Clicking in the player during start-up no longer hangs it.
- **SMAI download** no longer fails with "used by another process".
- **Did it work?** shares a settled answer when automatic sharing is on. Every result that isn't shared is logged with its reason.
- SUITE's own window runs at up to five times the frame rate.

## Included downloads

- **Setup**: the complete offline installer.
- **Web Setup**: small, and downloads the rest for you.
- **Portable**: a zip, nothing to install.
- **NeuralScreen add-on**.
- The add-ons, Lossless Scaling, ShaderGlass, FiveM plugin and GTA community packages that SUITE downloads in-app. You don't need to download these yourself.
