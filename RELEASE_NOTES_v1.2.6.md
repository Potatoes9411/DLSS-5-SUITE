# DLSS 5 SUITE v1.2.6

## Autopilot and "Did it work?"

- **Autopilot** installs a route, starts the game, checks what actually loaded, and moves on to the next route until one works.
- **Did it work?** reads the install's logs and Windows' crash record and says in plain words what happened. It answers by itself when a game closes.
- Results shared by DLSS 5 Autopilot users are used to try the route that worked for others first. You can share your own result ("It worked" / "It didn't") without opening a browser.

## More games, more ways in

- The library now finds EA, Ubisoft, Battle.net, Rockstar, Amazon, itch, Heroic and Xbox games.
- Vulkan games: the ReShade routes put ReShade in as a Vulkan layer.
- RTX Remix: DLSS 5 inside Remix runtimes, runtime swap and mod install.
- VR through ReShade's OpenXR layer (experimental).
- Emulators: Eden, Citron, Azahar and Play! added.
- OptiScaler DLSS-NR forks (multipass MFG, pre-SR, RTX 40 MFG), fetched from their own releases.
- Game fixes: NVIDIA GPU preference on hybrid laptops, and REFramework for RE Engine games. Uninstall undoes both.

## GTA V Enhanced

- A route for GTA V Enhanced with NaturalVision Enhanced: SUITE upgrades NVE's ReShade to one that can load DLSS 5, keeps the game's own DLSS files, and Remove puts everything back.
- With 12 GB of graphics memory or less, use about 1080p-1200p and 1 pass - DLSS 5 runs at the game's full resolution here.
- BattlEye switched off with `-nobattleye` is recognised as off.

## Screen Engine and video

- Frame generation 2x-6x in the overlay, and NVIDIA DLSS Frame Generation on RTX 40/50 (up to 6x on RTX 50).
- New **Video** tab: DLSS 5 and frame generation on a YouTube link or a video file, with download progress, speed and time left.
- Video, YouTube and webcam player: a portable MPC-HC set up as a DLSS 5 target. YouTube links stream through SUITE so they play instead of buffering.

## Tuning

- Quality / Balanced / Performance presets, a before/after image maker, RTX 40 multi-frame generation, and bring-your-own frame generation for RTX 20/30.
- The DLSS updater can keep a game's own DLSS files on NVIDIA's newest build, with a restore button.

## Included downloads

The release provides the complete setup, web setup, portable package and NeuralScreen add-on, together with the add-ons, Lossless Scaling and ShaderGlass packages that SUITE downloads in-app.
