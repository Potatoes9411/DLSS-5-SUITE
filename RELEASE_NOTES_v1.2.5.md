# DLSS 5 SUITE v1.2.5

## Discord Rich Presence

- Discord now shows **Playing DLSS 5 SUITE** with the DLSS 5 SUITE logo, the version you are running and how long it has been open.
- Your profile carries a **Download DLSS 5 SUITE** button linking to the latest release, so anyone who sees it can get it. Discord shows these buttons to other people, not on your own view of your profile.
- Works automatically for every download with no setup. Nothing happens if Discord is closed; it connects quietly once Discord is open.
- Can be switched off in Settings, next to Performance mode.

## Anti-cheat warnings

- Before installing into a game that ships a known anti-cheat, SUITE now warns you, so a mod is never placed into an online-protected game without you knowing.

## ReShade install control

- Added a DLSS 5 Feeder toggle to the DX12, DX11, and DX9 ReShade install flow.
- Feeder is off by default for standard RenoDX installs.
- When enabled, SUITE installs Feeder with its motion-vector configuration and helpers.
- The installed choice is recorded, so changing it later offers a proper route switch instead of leaving files behind.
- The 32-bit ReShade route keeps Feeder enabled because its host depends on it.

## Included downloads

The release provides the complete setup, web setup, portable package and NeuralScreen add-on, together with the add-ons, Lossless Scaling and ShaderGlass packages that SUITE downloads in-app. v1.2.4-suite.6 was published without those three packages, which broke those in-app downloads; this release carries them again.
