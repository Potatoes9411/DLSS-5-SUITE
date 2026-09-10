<div align="center">

# DLSS 5 SUITE

**A smart manager and one-click mod installer for DLSS 5, ReShade, OptiScaler and Streamline.**

[![Download](https://img.shields.io/badge/DOWNLOAD-LATEST%20RELEASE-00e676?style=for-the-badge&logoColor=white)](https://github.com/Potatoes9411/DLSS-5-SUITE/releases/latest)

[![Version](https://img.shields.io/badge/Version-1.2.0--suite.1-35d22b?style=flat-square)]()
[![Platform](https://img.shields.io/badge/Platform-Windows%20x64-24292e?style=flat-square)]()
[![Framework](https://img.shields.io/badge/Framework-.NET%208%20|%20Avalonia%20UI-512bd4?style=flat-square)]()
[![Language](https://img.shields.io/badge/Language-F%23-30b9db?style=flat-square)]()

</div>

---

## About this build

**DLSS 5 SUITE** is a modified version of **DLSS 5 MANAGER**, built by
**NODIX TECH** and published by **Numidia Studios**. The original work — the
scanner, the installer routes, the interface — is theirs.

This build is by **Potatoes9411**, published by **Potatoes-dev**
(<https://potatoes-dev.com>), and exists **with NODIX TECH's permission**.

### Get the original

**DLSS 5 MANAGER — <https://numidiastudios.com/dlss-5-manager/>**

If you want the original rather than this modified build, that is where it
comes from. The link is in the application too, under About.

See [NOTICE.txt](NOTICE.txt) for the terms this build was permitted under, and
[Copyright.txt](Copyright.txt) for the original author's own terms.

---

## What it does

- Finds installed games across Steam, Epic, GOG and standalone folders, and
  works out which executable in each install is actually the game.
- Detects the graphics API each title renders with by reading the
  executable's import tables and string data — not by guessing from the files
  lying in its folder.
- Installs and removes DLSS 5 through the route that suits the title:
  OptiScaler (DirectX 12, Vulkan or neural upstream), ReShade + RenoDX on
  DirectX 12 or 11, dgVoodoo on DirectX 9, or the AMD RDNA 4 payload.
- Keeps backups of everything it replaces, so a removal puts the game back
  exactly as it was.

---

## Installing

Download the setup from the
[releases page](https://github.com/Potatoes9411/DLSS-5-SUITE/releases/latest)
and run it. It offers a per-user or all-machine install, asks about shortcuts,
startup and the taskbar, and can update over a running copy — it will offer to
close the application for you.

Updates are checked against this repository's releases. A release must be
tagged with the plain version number (`v1.2.1` or `1.2.1`) for the check to
recognise it.

---

## Storage

Settings, the poster cache, install manifests and backups live in:

```text
%LOCALAPPDATA%\DLSS5Manager\
```

The folder name is deliberately unchanged from the original so an existing
install keeps its settings and library after updating.

---

## Community feature — temporarily hidden

Two controls are merged, compiled and working, but hidden in this build:

| Control | Where |
|---|---|
| **Community** tab | main tab strip (two places in `MainWindow.axaml`) |
| **Share result** button | the game Manage sheet |

Both talk to NODIX TECH's Cloudflare Worker. The shared 1.2.0 source ships
placeholders instead of the credentials (`CommunityApi.BaseUrl = "your-worker"`,
`AppSecret = "lol i can not give this"`), so the share button only ever returned
a 404. NODIX TECH is keeping the feature exclusive to DLSS 5 MANAGER for now -
his servers are at capacity - and has said he will grant access to this build
later.

**To switch it back on**, once he does: set the three `IsVisible="False"` flags
back to `True` and fill in `BaseUrl` and `AppSecret`. Nothing else is needed -
`CommunityApi.fs`, `SystemSpecs.fs` and `CommunityViewModel.fs` are all present
and compiling, and nothing contacts his server while the feature is hidden.

## Building

Requires the .NET 8 SDK.

```bash
# Both steps, with the setup named for the version it contains
./build-setup.sh
```

`build-setup.sh` reads `<Version>` from the fsproj and writes
`dist/DLSS 5 SUITE Setup v<version>.exe`. **Old setups are never deleted**, and
it refuses to overwrite a setup that already exists for the current version -
bump `<Version>` instead. A download link someone already has must keep serving
the build it served before.

The two steps by hand, if needed:

```bash
# The application
dotnet publish "DLSS 5 SUITE.fsproj" -c Release -r win-x64 --self-contained true -o publish

# The installer, with the application embedded in it
dotnet publish ../Setup/DLSS5SuiteSetup.csproj -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:PayloadDir=<full path to the publish folder above>
```

Release builds run the output through Obfuscar. The bundled tool targets
.NET 9, so on a machine without that runtime add `-p:SkipObfuscation=true` to
build without it — shipping builds should be made with it on.

### The mod payload

The `mod files` folder is **not in the repository** and cannot be: one file in
it, `dlss 5/nvngx_dlssnr.dll`, is 158 MB and GitHub rejects anything over 100
MB. The folder is 362 MB in total.

It ships inside the installer instead, so a release download has everything.
A fresh clone does not, and the app will say *"The mod files folder is missing
next to the application"* until you provide it — the build is fine, the payload
simply is not there.

To get it, copy the `mod files` folder out of an existing DLSS 5 MANAGER or
DLSS 5 SUITE installation (or out of a release archive) and drop it beside
`DLSS 5 SUITE.fsproj`. The project copies it into the build output from there,
and the installer picks it up from the publish output.
