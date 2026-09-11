# DLSS 5 SUITE v1.2.1-suite.4

This release adds a smooth, browser-free update experience for both DLSS 5 SUITE and its optional universal ShaderGlass route.

## Highlights

- Downloads application updates directly inside DLSS 5 SUITE, verifies the GitHub-published SHA-256 digest, and launches Setup automatically.
- Adds visible download and setup progress instead of sending users through a browser.
- Automatically discovers and installs current official ShaderGlass and DLSS5-Feeder releases from allowlisted upstream projects.
- Verifies release assets before use and stops safely if a source, filename, release tag, or digest does not match expectations.
- Adds Full Setup, compact Web Setup, and Portable ZIP release choices.
- Web Setup automatically downloads and verifies the matching Portable ZIP before installing.
- Excludes the unsafe MV 2.7 / `eurotrucks2.exe` package entirely.
- Preserves the existing game scanner, API detection, and per-game installation routes.
- Automates the working Lossless Scaling Discord method without redistributing the paid application.
- Preserves the original Lossless Scaling engine and verifies official LosslessProxy/LSP assets before installation.
- Fixes update detection between Suite revisions such as suite.3 and suite.4.
- Adds Need for Speed-aware compatibility guidance: the verified DXVK-to-Vulkan Feeder route for classic D3D9 games, and correct D3D11/D3D12 Feeder guidance for newer titles such as Heat and Unbound.

## Downloads

- **Full Setup:** includes the application payload for an offline installation.
- **Web Setup:** a smaller installer that retrieves the matching verified Portable ZIP.
- **Portable ZIP:** extract and run without the installer.

DLSS 5 SUITE is a modified version of DLSS 5 MANAGER by NODIX TECH / Numidia Studios, used with permission. The original remains available at https://numidiastudios.com/dlss-5-manager/.
