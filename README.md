<div align="center">

# DLSS 5 SUITE

**A modified edition of DLSS 5 MANAGER for Windows.**

[![Download](https://img.shields.io/badge/DOWNLOAD-LATEST%20RELEASE-00e676?style=for-the-badge)](https://github.com/Potatoes9411/DLSS-5-SUITE/releases/latest)

Version 1.2.1-suite.4 · Windows x64 · .NET 8 · Avalonia UI

</div>

## Project status and ownership

DLSS 5 SUITE is a permitted modified version of **DLSS 5 MANAGER**, which was created by **NODIX TECH** and published by **Numidia Studios**. Potatoes9411 maintains this modified edition under the name Potatoes-dev.

Public access to this repository does **not** transfer ownership of DLSS 5 MANAGER, DLSS 5 SUITE, their artwork, bundled components, or third-party technology. It also does not grant permission to rename, rebrand, resell, or republish the application as someone else's product.

The permission granted for DLSS 5 SUITE is specific to this project and its maintainer. It is not a blanket license for anyone who discovers or downloads the repository. Refer to [NOTICE.txt](NOTICE.txt) and [Copyright.txt](Copyright.txt) before copying, modifying, or distributing any part of the project.

## Original application

The original and unchanged DLSS 5 MANAGER is available from its developer at:

**https://numidiastudios.com/dlss-5-manager/**

DLSS 5 SUITE uses its own GitHub releases for updates, but it does not replace or claim ownership of the original application or its download location.

## What this edition provides

- Detection of installed games and their likely rendering APIs.
- Supported DLSS, ReShade, RenoDX and OptiScaler installation routes.
- Backup-aware installation and removal workflows.
- Steam artwork and library handling improvements.
- Batch installation with per-game compatibility routing.
- A separately isolated ShaderGlass compatibility route for games such as Roblox and Minecraft.
- An optional Lossless Scaling readiness check for experimental external capture.
- Automatic, browser-free application and compatibility-component updates with visible progress.
- DLSS 5 SUITE themes, nebula and shooting-star effects, installer and update channel.

Compatibility varies by game, graphics hardware, anti-cheat system and game update. The project does not promise that every title will work, and it does not bypass anti-cheat protections.

## Safety notice

Only obtain releases from this repository or the original DLSS 5 MANAGER website. Do not use unofficial executables advertised as “MV 2.7” or named `eurotrucks2.exe`; supplied examples were detected as Trojans by numerous independent security products and are not part of DLSS 5 SUITE.

The ShaderGlass route only uses allowlisted official upstream projects. Release names and versions are checked, downloaded assets are SHA-256 verified, archives are extracted with path-traversal protection, and setup stops safely if verification fails. The unsafe MV 2.7 method is never downloaded or used. Third-party mod components retain their own ownership and licensing.

## Installation

Choose one of three versioned packages from this repository's Releases page:

- **Full Setup** — contains the application payload and can install offline.
- **Web Setup** — a smaller installer that downloads and verifies the Portable ZIP for the same release.
- **Portable ZIP** — extract and run without installing.

Existing installers are retained as separate historical downloads and are never silently overwritten with a different build.

## Updates

DLSS 5 SUITE checks this repository's stable GitHub releases. When an update is available, the app downloads the exactly matching Full Setup directly, displays progress, verifies GitHub's published SHA-256 digest, and starts Setup without opening a browser. A missing or mismatched release asset is rejected.

The ShaderGlass route independently checks the allowlisted official ShaderGlass and DLSS5-Feeder release sources when **Set up/update & launch ShaderGlass** is selected. This keeps the optional compatibility files current without bundling unverified YouTube packages or asking the user to assemble files manually.

For a valid release, use a version tag such as `v1.2.1-suite.4` and attach files with matching versioned names. GitHub may display spaces in uploaded filenames as periods; both exact forms are accepted:

```text
DLSS 5 SUITE Setup v1.2.1-suite.4.exe
DLSS 5 SUITE Web Setup v1.2.1-suite.4.exe
DLSS 5 SUITE Portable v1.2.1-suite.4.zip
```

Application data, cached artwork, settings, manifests and backups are stored under:

```text
%LOCALAPPDATA%\DLSS5Suite\
```

The existing folder name is retained for compatibility with installations of the original application.

## Source availability

The source is published for transparency, maintenance and permitted collaboration. Source availability should not be interpreted as public-domain status or as permission to redistribute proprietary payload files.

Large mod and NVIDIA runtime payloads are intentionally excluded from the Git repository. Their absence from a source checkout does not indicate a broken release; authorized release installers package the required files separately.

## Credits and trademarks

- DLSS 5 MANAGER: NODIX TECH / Numidia Studios
- DLSS 5 SUITE modifications: Potatoes9411 / Potatoes-dev
- ShaderGlass: Mausimus
- RenoDX and ReShade components belong to their respective developers

NVIDIA, DLSS and the NVIDIA eye logo are trademarks of NVIDIA Corporation. DLSS 5 SUITE is not affiliated with, endorsed by, or sponsored by NVIDIA.

For the exact conditions governing this modified edition, read [NOTICE.txt](NOTICE.txt).
