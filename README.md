<div align="center">

# DLSS 5 SUITE

**A modified edition of DLSS 5 MANAGER for Windows.**

[![Download](https://img.shields.io/badge/DOWNLOAD-LATEST%20RELEASE-00e676?style=for-the-badge)](https://github.com/Potatoes9411/DLSS-5-SUITE/releases/latest)

[![Version](https://img.shields.io/badge/Version-1.2.1--suite.5-35d22b?style=flat-square)]()
[![Platform](https://img.shields.io/badge/Platform-Windows%20x64-24292e?style=flat-square)]()
[![Framework](https://img.shields.io/badge/Framework-.NET%208%20|%20Avalonia%20UI-512bd4?style=flat-square)]()
[![Language](https://img.shields.io/badge/Language-F%23-30b9db?style=flat-square)]()

</div>

## Project status and ownership

DLSS 5 SUITE is a permitted modified version of **DLSS 5 MANAGER**, which was created by **NODIX TECH** and published by **Numidia Studios**. Potatoes9411 maintains this modified edition under the name Potatoes-dev.

Public access to this repository does **not** transfer ownership of DLSS 5 MANAGER, DLSS 5 SUITE, their artwork, bundled components, or third-party technology. It also does not grant permission to rename, rebrand, resell, or republish the application as someone else's product.

The permission granted for DLSS 5 SUITE is specific to this project and its maintainer. It is not a blanket license for anyone who discovers or downloads the repository. Refer to [NOTICE.txt](NOTICE.txt) and [Copyright.txt](Copyright.txt) before copying, modifying, or distributing any part of the project.

## Original application

The original and unchanged DLSS 5 MANAGER is available from its developer at:

**https://numidiastudios.com/dlss-5-manager/**

**GitHub:** [https://github.com/NODIX-TECH/DLSS-5-MANAGER](https://github.com/NODIX-TECH/DLSS-5-MANAGER)

DLSS 5 SUITE uses its own GitHub releases for updates, but it does not replace or claim ownership of the original application or its download location.

## What this edition provides

- Detection of installed games and their likely rendering APIs.
- Supported DLSS, ReShade, RenoDX and OptiScaler installation routes.
- Backup-aware installation and removal workflows.
- Steam artwork and library handling improvements.
- Batch installation with per-game compatibility routing.
- A separately isolated ShaderGlass compatibility route for games such as Roblox and Minecraft.
- Automated Lossless Scaling setup using official LosslessProxy/LSP releases and the verified FeedKit route.
- Need for Speed game-aware Feeder guidance, including the verified DXVK-to-Vulkan path for classic 32-bit D3D9 titles and appropriate D3D11/D3D12 profiles for newer games.
- Automatic, browser-free application and compatibility-component updates with visible progress.
- DLSS 5 SUITE themes, nebula and shooting-star effects, installer and update channel.

Compatibility varies by game, graphics hardware, anti-cheat system and game update. The project does not promise that every title will work, and it does not bypass anti-cheat protections.

## Safety notice

Only obtain releases from this repository or the original DLSS 5 MANAGER website. Do not use unofficial executables advertised as “MV 2.7” or named `eurotrucks2.exe`; supplied examples were detected as Trojans by numerous independent security products and are not part of DLSS 5 SUITE.

The ShaderGlass route only uses allowlisted official upstream projects. Release names and versions are checked, downloaded assets are SHA-256 verified, archives are extracted with path-traversal protection, and setup stops safely if verification fails. The unsafe MV 2.7 method is never downloaded or used. Third-party mod components retain their own ownership and licensing.

## Installation

> [!IMPORTANT]
> 🚨 **STOP! READ THIS BEFORE DOWNLOADING!** 🚨
> *(Leia isto antes de baixar! / ¡Lee esto antes de descargar!)*
> 
> **🇬🇧 ENGLISH:**
> * 🟢 **Easiest Install:** Download DLSS.5.SUITE.Web.Setup.exe (Downloads everything else for you automatically!)
> * 🔵 **Offline Install:** Download DLSS.5.SUITE.Setup.exe (Huge file, has everything included).
> * 🟡 **No Install Required:** Download DLSS.5.SUITE.Portable.zip and just extract it.
> * ❌ **DO NOT DOWNLOAD** Lossless_Scaling.zip, addons.zip, or ShaderGlass_DLSS_5.zip manually. The application will fetch them for you!
> 
> **🇧🇷 PORTUGUÊS (PT-BR):**
> * 🟢 **Instalação mais fácil:** Baixe o DLSS.5.SUITE.Web.Setup.exe (Ele baixa o resto para você automaticamente!)
> * 🔵 **Instalação Offline:** Baixe o DLSS.5.SUITE.Setup.exe (Arquivo grande, já inclui tudo).
> * 🟡 **Não requer instalação:** Baixe o DLSS.5.SUITE.Portable.zip e apenas extraia.
> * ❌ **NÃO BAIXE** Lossless_Scaling.zip, addons.zip ou ShaderGlass_DLSS_5.zip manualmente. O aplicativo fará isso por você!
> 
> **🇪🇸 ESPAÑOL:**
> * 🟢 **Instalación más fácil:** Descarga DLSS.5.SUITE.Web.Setup.exe (¡Descarga todo lo demás por ti automáticamente!)
> * 🔵 **Instalación sin conexión:** Descarga DLSS.5.SUITE.Setup.exe (Archivo grande, incluye todo).
> * 🟡 **Sin instalación:** Descarga DLSS.5.SUITE.Portable.zip y simplemente extráelo.
> * ❌ **NO DESCARGUES** Lossless_Scaling.zip, addons.zip ni ShaderGlass_DLSS_5.zip manualmente. ¡La aplicación los descargará por ti!

*(Existing installers are retained as separate historical downloads and are never silently overwritten with a different build.)*

## Updates

DLSS 5 SUITE checks this repository's stable GitHub releases. When an update is available, the app downloads the exactly matching Full Setup directly, displays progress, verifies GitHub's published SHA-256 digest, and starts Setup without opening a browser. A missing or mismatched release asset is rejected.

The ShaderGlass route independently checks the allowlisted official ShaderGlass and DLSS5-Feeder release sources when **Set up/update & launch ShaderGlass** is selected. This keeps the optional compatibility files current without bundling unverified YouTube packages or asking the user to assemble files manually.

The Lossless Scaling route requires the user's licensed Steam installation. It preserves the original `Lossless.dll`, installs SHA-256-verified official LosslessProxy, LSP-ReShade, and LSP-Windowed releases, then configures current FeedKit components. The app checks the display count and presents the required Discord workflow: game on display 1, Lossless Scaling visible on display 2, apply scaling, select the Lossless Scaling window, and press Home. A virtual phone display may be used as the second display.

For Need for Speed, the app labels the route rather than pretending the games have native DLSS. Community-verified classic titles — Underground, Underground 2, Most Wanted (2005), Shift and ProStreet — use DXVK 3.0.2 x86 to translate D3D9 to Vulkan, then ReShade's Vulkan layer with DLSS5-Feeder addon32, host64 and Lumenite. Modern Frostbite titles use the corresponding D3D11 or D3D12 Feeder route and should be tested offline first. The app never treats a rendering hook as safe for online multiplayer.


Application data, cached artwork, settings, manifests and backups are stored under:

```text
%LOCALAPPDATA%\DLSS5Manager\
```

The existing folder name is retained for compatibility with installations of the original application.

## Source availability

The source is published for transparency, maintenance and permitted collaboration. Source availability should not be interpreted as public-domain status or as permission to redistribute proprietary payload files.

Large mod and NVIDIA runtime payloads are intentionally excluded from the Git repository. Their absence from a source checkout does not indicate a broken release; authorized release installers package the required files separately.

## Support the Developers

DLSS 5 SUITE is a free project. If you find this modified edition useful, you can support the developers through Ko-fi. Tips are split 50-50 between this fork and the original author of DLSS 5 MANAGER.

- **Potatoes9411 (Potatoes-dev)**
  - Ko-fi: [![Support this build's dev](https://img.shields.io/badge/Ko--fi-Support%20this%20build's%20dev%20(Potatoes9411)-F16061?style=for-the-badge&logo=ko-fi&logoColor=white)](https://ko-fi.com/potatoes9411)
  - Website: [potatoes-dev.com](https://potatoes-dev.com)
- **NODIX TECH (Numidia Studios)**
  - Ko-fi: [![Support the original creator](https://img.shields.io/badge/Ko--fi-Support%20the%20original%20creator%20(NODIX%20TECH)-F16061?style=for-the-badge&logo=ko-fi&logoColor=white)](https://ko-fi.com/nodix)
  - Website: [numidiastudios.com](https://numidiastudios.com)

## Credits and trademarks

- DLSS 5 MANAGER: NODIX TECH / Numidia Studios
- DLSS 5 SUITE modifications: Potatoes9411 / Potatoes-dev
- ShaderGlass: Mausimus
- RenoDX and ReShade components belong to their respective developers

NVIDIA, DLSS and the NVIDIA eye logo are trademarks of NVIDIA Corporation. DLSS 5 SUITE is not affiliated with, endorsed by, or sponsored by NVIDIA.

For the exact conditions governing this modified edition, read [NOTICE.txt](NOTICE.txt).
