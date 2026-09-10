# DLSS 5 SUITE v1.2.1-suite.3

This release introduces verified on-demand compatibility setup and three distribution choices while keeping the existing game-management logic intact.

## Highlights

- Adds an automatic ShaderGlass compatibility setup intended for games such as Roblox and Minecraft.
- Retrieves official ShaderGlass and DLSS5-Feeder releases from allowlisted upstream projects.
- Verifies downloaded release assets with SHA-256 and rejects missing or unexpected files.
- Shows progress while downloading and configuring ShaderGlass components.
- Adds Full Setup, smaller Web Setup, and Portable ZIP build formats.
- Web Setup downloads and verifies the matching Portable ZIP before installation.
- Keeps compatibility downloads outside the main installer to reduce its size.
- Excludes the unsafe MV 2.7 / `eurotrucks2.exe` package entirely.
- Preserves existing game scanning, rendering-API detection, Steam artwork handling, and per-game installation logic.

## Update-flow testing

Suite.3 can be installed as the starting version when testing the automatic upgrade to suite.4. The browser-free in-app setup download is introduced in suite.4.

DLSS 5 SUITE is a modified version of DLSS 5 MANAGER by NODIX TECH / Numidia Studios, used with permission. The original remains available at https://numidiastudios.com/dlss-5-manager/.
