# DLSS 5 SUITE changelog

Every DLSS 5 SUITE release, newest first. Downloads are on the [releases page](https://github.com/Potatoes9411/DLSS-5-SUITE/releases).

## v1.3.0 - 2026-11-19

The biggest SUITE update so far: four new editions, a pick-your-parts installer, nine new frame generators, a YouTube player that no longer freezes, and every install-flow improvement from DLSS 5 MANAGER 1.3.8 folded in. SUITE is now fully its own project.

### Four new editions

Open them from the new edition menu (the hamburger in the top corner).

- **Space Press** squishes games with Windows' own file compression to free drive space. It has Games, Apps and Statistics tabs, a drive graph and a progress strip, and an hourly check that keeps games squished. It pauses while you play. A new **Clean up** tab removes SUITE's own stale caches, old logs, downloads and leftover folders, and shows each item's size and files first.
- **Upscaler Vault** finds every game's DLSS, FSR, XeSS and XeLL DLLs. Swap any of them to another version and restore the original with one click.
- **Enabler Dock** installs DLSS Enabler with the right proxy for the game, a merged ini, and a tracked, clean uninstall.
- **Motion Forge** is a frame-generation overlay for any game or window. Only one overlay runs at a time, so it and the Screen Engine never clash.

### Frame generation, everywhere

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

### Installing, from DLSS 5 MANAGER 1.3.8

- **Advanced install**: an "Advanced install..." button beside Install lists every part of a route, such as ReShade, effects, feed, RenoDX, OptiScaler, Streamline, DLSS and the model. Turn each one on or off by itself, or add your own files. SUITE's normal uninstall still removes everything.
- **RenoDX options**: MFG unlock add-on, the Multipass build, and deep-fried chicken (also on 32-bit DX9/DX11 through host64).
- **Emulator ReShade API**: Auto, DXGI or Vulkan.
- **Rename the proxy DLL** for OptiScaler, ReShade and AMD installs.
- **Hotkeys**: choose the keys that open ReShade and OptiScaler's menu.
- **RTX 40 OptiScaler build**, used automatically on RTX 40 cards, with Replace/Restore in Settings.
- **Install record in the game folder**: a game still shows as installed, and can still be removed, even if SUITE's data folder is lost.
- **Remove and never scan again** on the game's right-click menu.
- Uninstall also removes the effect files the overlay writes for itself.

### Library and settings

- Game cards show **every graphics API** a game ships, for example "DX12 +2". Routing still uses the main one.
- **Start with Windows** (a Startup-folder shortcut, no registry), **Minimise to tray**, and **Scan for games when SUITE starts**.
- **Custom background**: pick your own image, set its brightness, or reset it.
- **Restore original cover** after picking your own.
- The window reopens where you last closed it.
- Settings' back button returns to the page you came from.
- **RobloxShadeHost** now has its own Roblox card in Settings, beside ShaderGlass and Lossless Scaling, instead of a tab in the Screen Engine window.

### Fixes

- **YouTube and video player no longer freeze.** DLSS 5 takes 12-16 seconds to start (about a minute the very first time). Your video now waits behind a short "DLSS 5 is starting..." clip and plays from its first frame. Clicking in the player during start-up no longer hangs it.
- **SMAI download** no longer fails with "used by another process".
- **Did it work?** shares a settled answer when automatic sharing is on. Every result that isn't shared is logged with its reason.
- SUITE's own window runs at up to five times the frame rate.

### Included downloads

- **Setup**: the complete offline installer.
- **Web Setup**: small, and downloads the rest for you.
- **Portable**: a zip, nothing to install.
- **NeuralScreen add-on**.
- The add-ons, Lossless Scaling, ShaderGlass, FiveM plugin and GTA community packages that SUITE downloads in-app. You don't need to download these yourself.

## v1.2.6 - 2026-11-09

### Autopilot and "Did it work?"

- **Autopilot** installs a route, starts the game, checks what actually loaded, and moves on to the next route until one works.
- **Did it work?** reads the install's logs and Windows' crash record and says in plain words what happened. It answers by itself when a game closes.
- Results shared by DLSS 5 Autopilot users are used to try the route that worked for others first. You can share your own result ("It worked" / "It didn't") without opening a browser.

### More games, more ways in

- The library now finds EA, Ubisoft, Battle.net, Rockstar, Amazon, itch, Heroic and Xbox games.
- Vulkan games: the ReShade routes put ReShade in as a Vulkan layer.
- RTX Remix: DLSS 5 inside Remix runtimes, runtime swap and mod install.
- VR through ReShade's OpenXR layer (experimental).
- Emulators: Eden, Citron, Azahar and Play! added.
- OptiScaler DLSS-NR forks (multipass MFG, pre-SR, RTX 40 MFG), fetched from their own releases.
- Game fixes: NVIDIA GPU preference on hybrid laptops, and REFramework for RE Engine games. Uninstall undoes both.

### GTA V Enhanced

- A route for GTA V Enhanced with NaturalVision Enhanced: SUITE upgrades NVE's ReShade to one that can load DLSS 5, keeps the game's own DLSS files, and Remove puts everything back.
- With 12 GB of graphics memory or less, use about 1080p-1200p and 1 pass - DLSS 5 runs at the game's full resolution here.
- BattlEye switched off with `-nobattleye` is recognised as off.

### Screen Engine and video

- Frame generation 2x-6x in the overlay, and NVIDIA DLSS Frame Generation on RTX 40/50 (up to 6x on RTX 50).
- New **Video** tab: DLSS 5 and frame generation on a YouTube link or a video file, with download progress, speed and time left.
- Video, YouTube and webcam player: a portable MPC-HC set up as a DLSS 5 target. YouTube links stream through SUITE so they play instead of buffering.

### Tuning

- Quality / Balanced / Performance presets, a before/after image maker, RTX 40 multi-frame generation, and bring-your-own frame generation for RTX 20/30.
- The DLSS updater can keep a game's own DLSS files on NVIDIA's newest build, with a restore button.

### Included downloads

The release provides the complete setup, web setup, portable package and NeuralScreen add-on, together with the add-ons, Lossless Scaling and ShaderGlass packages that SUITE downloads in-app.

## v1.2.5 - 2026-10-09

### Discord Rich Presence

- Discord now shows **Playing DLSS 5 SUITE** with the DLSS 5 SUITE logo, the version you are running and how long it has been open.
- Your profile carries a **Download DLSS 5 SUITE** button linking to the latest release, so anyone who sees it can get it. Discord shows these buttons to other people, not on your own view of your profile.
- Works automatically for every download with no setup. Nothing happens if Discord is closed; it connects quietly once Discord is open.
- Can be switched off in Settings, next to Performance mode.

### Anti-cheat warnings

- Before installing into a game that ships a known anti-cheat, SUITE now warns you, so a mod is never placed into an online-protected game without you knowing.

### ReShade install control

- Added a DLSS 5 Feeder toggle to the DX12, DX11, and DX9 ReShade install flow.
- Feeder is off by default for standard RenoDX installs.
- When enabled, SUITE installs Feeder with its motion-vector configuration and helpers.
- The installed choice is recorded, so changing it later offers a proper route switch instead of leaving files behind.
- The 32-bit ReShade route keeps Feeder enabled because its host depends on it.

### Included downloads

The release provides the complete setup, web setup, portable package and NeuralScreen add-on, together with the add-ons, Lossless Scaling and ShaderGlass packages that SUITE downloads in-app. v1.2.4-suite.6 was published without those three packages, which broke those in-app downloads; this release carries them again.

## v1.2.4-suite.6 - 2026-09-18

### DLSS 5 SUITE v1.2.4-suite.6

### Enhancements
- **Overlay Sync:** When toggling the overlay setting globally, the suite will now properly push/pull the overlay to ALL installed games, correctly resolving a bug where games that were installed while the overlay was disabled would be ignored by the sync loop.

## v1.2.4-suite.5 - 2026-09-18

### DLSS 5 SUITE v1.2.4-suite.5

### Bug Fixes
- **Neural Rendering (GTA V):** Replaced the ReShade proxy name from \dxgi.dll\ to \d3d11.dll\ to fix fatal crashes when opening the Graphics settings menu.

## v1.2.4-suite.4 - 2026-09-18

### DLSS 5 SUITE v1.2.4-suite.4

### Bug Fixes
- **Neural Rendering (GTA V):** Fixed an issue where RenoDX would wait indefinitely for Neural Rendering because the required Lumenite motion vectors were not configured correctly in \ReShade.ini\.

## v1.2.4-suite.3 - 2026-09-18

### DLSS 5 SUITE v1.2.4-suite.3

### Bug Fixes
- **UI:** Restored the website links under the Support & Tutorials section.

## v1.2.4-suite.2 - 2026-09-18

### DLSS 5 SUITE v1.2.4-suite.2

### Bug Fixes
- **GTA V Neural Rendering Route:** Included the standard ReShade effects folder (
eshade-shaders) during installation and configured ReShade.ini automatically. This prevents the ReShade overlay from throwing "No effect files (.fx) found", getting stuck compiling, or forcing the built-in tutorial. The overlay toggle key is also cleanly mapped to End to avoid conflicting with the Rockstar Social Club overlay (which uses Home).

### Notes
- The internal data directory is now correctly migrated to %LOCALAPPDATA%\DLSS 5 SUITE.

## v1.2.4 - 2026-09-17

> [!IMPORTANT]
> **Which file do I download?**
> *(Qual arquivo baixar? / ¿Qué archivo descargo?)*
>
> **ENGLISH**
> * **Easiest install:** DLSS.5.SUITE.Web.Setup.v1.2.4.exe — small download; it fetches the application for you.
> * **Offline install:** DLSS.5.SUITE.Setup.v1.2.4.exe — large download with the application payload included.
> * **No install:** DLSS.5.SUITE.Portable.v1.2.4.zip — extract it and run DLSS 5 SUITE.
> * **RTX 30/40 owners:** also download DLSS.5.SUITE.NeuralScreen.Add-on.v1.2.4.zip. RTX 50 users may install it as an alternative method.
> * **Do not download** Lossless Scaling, add-ons, or ShaderGlass packages manually. SUITE fetches those components when needed.
>
> **PORTUGUÊS (PT-BR)**
> * **Instalação mais fácil:** DLSS.5.SUITE.Web.Setup.v1.2.4.exe — download pequeno; ele baixa o aplicativo para você.
> * **Instalação offline:** DLSS.5.SUITE.Setup.v1.2.4.exe — download grande com o aplicativo incluído.
> * **Sem instalação:** DLSS.5.SUITE.Portable.v1.2.4.zip — extraia e execute o DLSS 5 SUITE.
> * **Placas RTX 30/40:** baixe também DLSS.5.SUITE.NeuralScreen.Add-on.v1.2.4.zip. Usuários RTX 50 podem instalá-lo como método alternativo.
>
> **ESPAÑOL**
> * **Instalación más fácil:** DLSS.5.SUITE.Web.Setup.v1.2.4.exe — descarga pequeña; obtiene la aplicación por ti.
> * **Instalación sin conexión:** DLSS.5.SUITE.Setup.v1.2.4.exe — descarga grande con la aplicación incluida.
> * **Sin instalación:** DLSS.5.SUITE.Portable.v1.2.4.zip — extrae y ejecuta DLSS 5 SUITE.
> * **Tarjetas RTX 30/40:** descarga también DLSS.5.SUITE.NeuralScreen.Add-on.v1.2.4.zip. Los usuarios RTX 50 pueden instalarlo como método alternativo.

---

Version 1.2.4 resolves visual clipping and layout edge cases in the Manage and Screen Engine windows, and clarifies Rockstar Games Launcher expectations.

### Highlights

- **Manage Modal Scrolling Fixed:** The Manage window's internal scroll area now extends fully to the bottom padding, resolving an issue where the new GTA V Neural Rendering route panel would physically clip text at the bottom edge.
- **Screen Engine Method Selection Fix:** Switching away from the new RobloxShadeHost method in the Screen Engine tab now correctly de-activates its UI highlight, preventing two tabs from appearing selected simultaneously.
- **Rockstar Games Launcher Anti-Cheat Guidance:** Added detailed, accurate guidance regarding the Rockstar Games Launcher BattlEye toggle. Since the Rockstar Launcher uses an encrypted, binary settings file (settings_user.dat), its configuration cannot be safely modified programmatically. The automatic -nobattleye insertion in commandline.txt works natively, but manual user intervention (unchecking the box in Rockstar Settings) is required once if the launcher overrides the commandline config.

### Compatibility-model trust policy

The bundled 
vngx_dlssnr.dll compatibility model is accepted only when its SHA-256 is E67DEE209320CDAFE0E93E45675D7AA34323A53ACC57A72B2E40A181581C989A. Any changed or substituted unsigned DLL is rejected. This exception covers that Neural Rendering model alone and does not disable verification for other runtime files.

## v1.2.3 - 2026-09-16

> [!IMPORTANT]
> **Which file do I download?**
> *(Qual arquivo baixar? / ¿Qué archivo descargo?)*
>
> **ENGLISH**
> * **Easiest install:** `DLSS.5.SUITE.Web.Setup.v1.2.3.exe` — small download; it fetches the application for you.
> * **Offline install:** `DLSS.5.SUITE.Setup.v1.2.3.exe` — large download with the application payload included.
> * **No install:** `DLSS.5.SUITE.Portable.v1.2.3.zip` — extract it and run DLSS 5 SUITE.
> * **RTX 30/40 owners:** also download `DLSS.5.SUITE.NeuralScreen.Add-on.v1.2.3.zip`. RTX 50 users may install it as an alternative method.
> * **Do not download** Lossless Scaling, add-ons, or ShaderGlass packages manually. SUITE fetches those components when needed.
>
> **PORTUGUÊS (PT-BR)**
> * **Instalação mais fácil:** `DLSS.5.SUITE.Web.Setup.v1.2.3.exe` — download pequeno; ele baixa o aplicativo para você.
> * **Instalação offline:** `DLSS.5.SUITE.Setup.v1.2.3.exe` — download grande com o aplicativo incluído.
> * **Sem instalação:** `DLSS.5.SUITE.Portable.v1.2.3.zip` — extraia e execute o DLSS 5 SUITE.
> * **Placas RTX 30/40:** baixe também `DLSS.5.SUITE.NeuralScreen.Add-on.v1.2.3.zip`. Usuários RTX 50 podem instalá-lo como método alternativo.
>
> **ESPAÑOL**
> * **Instalación más fácil:** `DLSS.5.SUITE.Web.Setup.v1.2.3.exe` — descarga pequeña; obtiene la aplicación por ti.
> * **Instalación sin conexión:** `DLSS.5.SUITE.Setup.v1.2.3.exe` — descarga grande con la aplicación incluida.
> * **Sin instalación:** `DLSS.5.SUITE.Portable.v1.2.3.zip` — extrae y ejecuta DLSS 5 SUITE.
> * **Tarjetas RTX 30/40:** descarga también `DLSS.5.SUITE.NeuralScreen.Add-on.v1.2.3.zip`. Los usuarios RTX 50 pueden instalarlo como método alternativo.

---

Version 1.2.3 turns the library into a practical launch surface and makes the Screen Engine easier to tune for each game. It also strengthens the background work that keeps a large library responsive and recoverable.

### Highlights

- **Launch and stop games from their cards:** the play control shows startup progress, can cancel a launch, and becomes a stop control while the game is running. SUITE first asks whether a Steam title should start through Steam or through its executable, then remembers the choice.
- **Per-game launch controls:** launch arguments, **Ask again**, and **Open game folder** are available from Manage. Stopping asks the game window to close before using a forced stop as a last resort.
- **Per-game Screen Engine presets:** save the current capture and rendering settings for a game and have SUITE load them when that game starts.
- **Live Screen Engine tuning:** supported changes reach a running session without blanking the picture. Only source, output, or capture changes rebuild the session.
- **Complete NeuralScreen controls:** Boost, work scale, frame generation, frame multiplier, motion backend, unchanged-frame skipping, HDR capture, Spout2 output, recording indicator, adapter selection, and original-colour lock are available in the SUITE control window.
- **Better library tools:** search and filters work with the existing sort choices, scans can be cancelled, and keyboard shortcuts cover common actions.
- **Safer settings and diagnostics:** settings are replaced atomically, a persistent application log records failures, and startup recovers cleanly from an interrupted save.
- **Correct motion-vector setup:** ReShade receives the real Lumenite technique names, and SUITE removes the provider that was not selected.
- **DX11 OptiScaler DLSS-NR:** the Neural Rendering route enables NR and selects the D3D12 bridge that OptiScaler needs for DirectX 11 games.
- **Clean final UI behavior:** numeric fields have enough room for typed values, the play control stays above card hints, and the edge rubber band releases immediately when scrolling back into the library instead of fighting the new direction.
- **Reliable add-on download resume:** a fully downloaded partial file is verified and promoted instead of sending an invalid resume request.
- **DLSS 5 Neural Rendering for GTA V and FiveM, installed in-app:** a button in Manage deploys ReShade, RenoDX's DLSS5 Tool, the compatibility model and the Streamline runtime straight into the GTA V Enhanced or Legacy game folder, or into FiveM's own cache folder for FiveM Enhanced, with FiveM's classic client Plugins folder supported as a fallback. Every file it uses is already part of SUITE's own trusted payload - nothing is fetched from a browser or a Discord link, and a Remove button restores what it replaced.
- **An older OptiScaler build for RTX 20/30 series:** SUITE bundles the current OptiScaler build, which upstream testing found less stable on RTX 20/30 than the release right before it. That older build (0.7.9) can now be fetched from OptiScaler's own GitHub releases and verified in-app; once downloaded, it is used automatically for a qualifying card.
- **Update progress you can actually see:** checking for or downloading an update now shows a small floating progress card from any tab, not only inside Settings.
- **RobloxShadeHost:** the Roblox / ReShade method is compiled and bundled in this build.
- **The Manage sheet scrolls:** its header stays put; a tall route - GTA V's extra cards among them - scrolls underneath instead of running off the bottom of the window.
- **GTA V's BattlEye is disabled automatically:** installing the GTA V Enhanced fix or the DLSS 5 Neural Rendering route sets `-nobattleye` as the game's launch options, unless something is already typed there. No Rockstar Launcher setting to find by hand.

### Compatibility-model trust policy

The bundled `nvngx_dlssnr.dll` compatibility model is accepted only when its SHA-256 is `E67DEE209320CDAFE0E93E45675D7AA34323A53ACC57A72B2E40A181581C989A`. Any changed or substituted unsigned DLL is rejected. This exception covers that Neural Rendering model alone and does not disable verification for other runtime files.

## v1.2.2-suite.4 - 2026-09-16

> [!IMPORTANT]
> **Which file do I download?**
> *(Qual arquivo baixar? / ¿Qué archivo descargo?)*
>
> **ENGLISH**
> * **Easiest install:** DLSS.5.SUITE.Web.Setup.v1.2.2-suite.4.exe - small download, it fetches the rest for you.
> * **Offline install:** DLSS.5.SUITE.Setup.v1.2.2-suite.4.exe - large file, everything included.
> * **No install:** DLSS.5.SUITE.Portable.v1.2.2-suite.4.zip - extract and run.
> * **RTX 30/40 owners:** also take DLSS.5.SUITE.NeuralScreen.Add-on.v1.2.2-suite.4.zip and install it from the Screen Engine window. RTX 50 cards do not need it.
> * **Do not download** Lossless_Scaling.zip, addons.zip or ShaderGlass_DLSS_5.zip by hand. The application fetches them when you set those features up.
>
> **PORTUGUES (PT-BR)**
> * **Instalação mais fácil:** DLSS.5.SUITE.Web.Setup.v1.2.2-suite.4.exe - download pequeno, ele baixa o resto.
> * **Instalação offline:** DLSS.5.SUITE.Setup.v1.2.2-suite.4.exe - arquivo grande, já inclui tudo.
> * **Sem instalação:** DLSS.5.SUITE.Portable.v1.2.2-suite.4.zip - extraia e execute.
> * **Placas RTX 30/40:** baixe também DLSS.5.SUITE.NeuralScreen.Add-on.v1.2.2-suite.4.zip e instale pela janela Screen Engine. RTX 50 não precisa.
> * **Não baixe** Lossless_Scaling.zip, addons.zip ou ShaderGlass_DLSS_5.zip manualmente. O aplicativo faz isso por você.
>
> **ESPAÑOL**
> * **Instalación más fácil:** DLSS.5.SUITE.Web.Setup.v1.2.2-suite.4.exe - descarga pequeña, obtiene el resto por ti.
> * **Instalación sin conexión:** DLSS.5.SUITE.Setup.v1.2.2-suite.4.exe - archivo grande, incluye todo.
> * **Sin instalación:** DLSS.5.SUITE.Portable.v1.2.2-suite.4.zip - extrae y ejecuta.
> * **Tarjetas RTX 30/40:** descarga también DLSS.5.SUITE.NeuralScreen.Add-on.v1.2.2-suite.4.zip e instálalo desde la ventana Screen Engine. Las RTX 50 no lo necesitan.
> * **No descargues** Lossless_Scaling.zip, addons.zip ni ShaderGlass_DLSS_5.zip a mano. La aplicación los obtiene cuando configuras esas funciones.

---

This release takes DLSS 5 off the modded-game path and onto the whole desktop, and finishes the parts that still needed a browser or a manual step. Two screen methods are chosen by your graphics card, both driven from one window inside SUITE, and the motion vectors, downloads and window behaviour found during testing are fixed. Game installation logic is unchanged.

### Highlights
- **The add-on installs on a Program Files install:** v1.2.2-suite.4 unpacks NeuralScreen into your own user folder instead of the installation directory, which a standard user cannot write to. Installing to C:\Program Files no longer ends in "Access to the path ... is denied", and a copy already sitting beside the application is still used as before.
- **The NeuralScreen add-on really installs itself:** in v1.2.2-suite.2 the download code shipped but the button was still wired to a browser link. The button runs the download.
- **The NeuralScreen add-on installs itself:** the Screen Engine window fetches it from this release, shows the transfer in megabytes and percent, checks its SHA-256 and unpacks it, unlocking the method when it lands. No browser, no file picking. Choosing a zip by hand is still there for anyone offline.
- **DLSS 5 on any screen:** two methods, picked by your card. **Screen Engine** (RTX 50) runs SUITE's own full-screen wrapper; **NeuralScreen** (RTX 30/40, and RTX 50 as an alternative) runs the desktop overlay. RTX 20 is marked unsupported because that generation starts without processing the picture. A method your card cannot run is locked, with a tooltip pointing at the other one.
- **One control window:** capture source, window crosshair, Neural Rendering intensity, structure, tone, skin, style, auto mask, passes, super resolution, comparison modes, screenshots, recording, comparison sweeps and a capture folder, in a themed window opened from Settings. Changes apply on their own while a session runs, so the Apply button is gone, and every slider has a box that takes typed values past its range wherever the engine allows it.
- **Crosshair window picking:** drag the crosshair onto a window, or click it once to arm it, switch to anything with Alt+Tab, and the next click picks that window.
- **NeuralScreen driven from SUITE:** Num2 brings SUITE's controls up over whatever is on screen, with a button back to the main window. NeuralScreen's own menu never opens, and its taskbar button, tray icon, popups and window titles carry SUITE's name, icon and palette.
- **Multi-pass neural rendering:** NeuralScreen runs the model one to four times per frame through ping-pong targets, so the passes control works in both methods.
- **Windows stop flashing:** in one-window mode the processed picture sits in the normal window order instead of above everything, and hides while the captured application is not focused. Alt+Tab reaches other programs again.
- **Motion vectors for modded games:** every game install writes the DLSS5_MV_PROVIDER definition and puts the provider technique ahead of the DLSS 5 feed, so the add-on stops reporting that DLSS is getting no motion vectors.
- **Verified, resumable downloads:** ShaderGlass and Lossless Scaling are set up one at a time, with resumable transfers, SHA-256 verification, megabyte and percentage progress, stall detection and retries, instead of a status line that never changed.
- **Signed model bundled:** NVIDIA's signed neural-rendering model ships beside the pinned compatibility model and is preferred, so the Screen Engine starts on a current driver with no manual step. A model folder of your own can be chosen, and bundled files are never modified.
- **Monitor capture recovery:** a monitor handle Windows rejects is looked up again and capture retried once, which clears the stale-handle CreateForMonitor failures.
- **NeuralScreen ships separately:** it is its own download, because only RTX 30/40 cards need it. Where the method would be locked, SUITE offers to fetch it and to install it from the zip.

### Compatibility-model trust policy
The bundled nvngx_dlssnr.dll compatibility model is accepted only when its SHA-256 is E67DEE209320CDAFE0E93E45675D7AA34323A53ACC57A72B2E40A181581C989A. Any changed or substituted unsigned DLL is rejected. This exception covers the Neural Rendering model alone and does not disable signature verification anywhere else.

## v1.2.2-suite.3 - 2026-09-16

> [!IMPORTANT]
> **Which file do I download?**
> *(Qual arquivo baixar? / ¿Qué archivo descargo?)*
>
> **ENGLISH**
> * **Easiest install:** DLSS.5.SUITE.Web.Setup.v1.2.2-suite.3.exe - small download, it fetches the rest for you.
> * **Offline install:** DLSS.5.SUITE.Setup.v1.2.2-suite.3.exe - large file, everything included.
> * **No install:** DLSS.5.SUITE.Portable.v1.2.2-suite.3.zip - extract and run.
> * **RTX 30/40 owners:** also take DLSS.5.SUITE.NeuralScreen.Add-on.v1.2.2-suite.3.zip and install it from the Screen Engine window. RTX 50 cards do not need it.
> * **Do not download** Lossless_Scaling.zip, addons.zip or ShaderGlass_DLSS_5.zip by hand. The application fetches them when you set those features up.
>
> **PORTUGUES (PT-BR)**
> * **Instalação mais fácil:** DLSS.5.SUITE.Web.Setup.v1.2.2-suite.3.exe - download pequeno, ele baixa o resto.
> * **Instalação offline:** DLSS.5.SUITE.Setup.v1.2.2-suite.3.exe - arquivo grande, já inclui tudo.
> * **Sem instalação:** DLSS.5.SUITE.Portable.v1.2.2-suite.3.zip - extraia e execute.
> * **Placas RTX 30/40:** baixe também DLSS.5.SUITE.NeuralScreen.Add-on.v1.2.2-suite.3.zip e instale pela janela Screen Engine. RTX 50 não precisa.
> * **Não baixe** Lossless_Scaling.zip, addons.zip ou ShaderGlass_DLSS_5.zip manualmente. O aplicativo faz isso por você.
>
> **ESPAÑOL**
> * **Instalación más fácil:** DLSS.5.SUITE.Web.Setup.v1.2.2-suite.3.exe - descarga pequeña, obtiene el resto por ti.
> * **Instalación sin conexión:** DLSS.5.SUITE.Setup.v1.2.2-suite.3.exe - archivo grande, incluye todo.
> * **Sin instalación:** DLSS.5.SUITE.Portable.v1.2.2-suite.3.zip - extrae y ejecuta.
> * **Tarjetas RTX 30/40:** descarga también DLSS.5.SUITE.NeuralScreen.Add-on.v1.2.2-suite.3.zip e instálalo desde la ventana Screen Engine. Las RTX 50 no lo necesitan.
> * **No descargues** Lossless_Scaling.zip, addons.zip ni ShaderGlass_DLSS_5.zip a mano. La aplicación los obtiene cuando configuras esas funciones.

---

This release takes DLSS 5 off the modded-game path and onto the whole desktop, and finishes the parts that still needed a browser or a manual step. Two screen methods are chosen by your graphics card, both driven from one window inside SUITE, and the motion vectors, downloads and window behaviour found during testing are fixed. Game installation logic is unchanged.

### Highlights
- **The NeuralScreen add-on really installs itself now:** in v1.2.2-suite.2 the download code shipped but the button was still wired to a browser link. The button now runs the download.
- **The NeuralScreen add-on installs itself:** the Screen Engine window fetches it from this release, shows the transfer in megabytes and percent, checks its SHA-256 and unpacks it, unlocking the method when it lands. No browser, no file picking. Choosing a zip by hand is still there for anyone offline.
- **DLSS 5 on any screen:** two methods, picked by your card. **Screen Engine** (RTX 50) runs SUITE's own full-screen wrapper; **NeuralScreen** (RTX 30/40, and RTX 50 as an alternative) runs the desktop overlay. RTX 20 is marked unsupported because that generation starts without processing the picture. A method your card cannot run is locked, with a tooltip pointing at the other one.
- **One control window:** capture source, window crosshair, Neural Rendering intensity, structure, tone, skin, style, auto mask, passes, super resolution, comparison modes, screenshots, recording, comparison sweeps and a capture folder, in a themed window opened from Settings. Changes apply on their own while a session runs, so the Apply button is gone, and every slider has a box that takes typed values past its range wherever the engine allows it.
- **Crosshair window picking:** drag the crosshair onto a window, or click it once to arm it, switch to anything with Alt+Tab, and the next click picks that window.
- **NeuralScreen driven from SUITE:** Num2 brings SUITE's controls up over whatever is on screen, with a button back to the main window. NeuralScreen's own menu never opens, and its taskbar button, tray icon, popups and window titles carry SUITE's name, icon and palette.
- **Multi-pass neural rendering:** NeuralScreen runs the model one to four times per frame through ping-pong targets, so the passes control works in both methods.
- **Windows stop flashing:** in one-window mode the processed picture sits in the normal window order instead of above everything, and hides while the captured application is not focused. Alt+Tab reaches other programs again.
- **Motion vectors for modded games:** every game install writes the DLSS5_MV_PROVIDER definition and puts the provider technique ahead of the DLSS 5 feed, so the add-on stops reporting that DLSS is getting no motion vectors.
- **Verified, resumable downloads:** ShaderGlass and Lossless Scaling are set up one at a time, with resumable transfers, SHA-256 verification, megabyte and percentage progress, stall detection and retries, instead of a status line that never changed.
- **Signed model bundled:** NVIDIA's signed neural-rendering model ships beside the pinned compatibility model and is preferred, so the Screen Engine starts on a current driver with no manual step. A model folder of your own can be chosen, and bundled files are never modified.
- **Monitor capture recovery:** a monitor handle Windows rejects is looked up again and capture retried once, which clears the stale-handle CreateForMonitor failures.
- **NeuralScreen ships separately:** it is its own download, because only RTX 30/40 cards need it. Where the method would be locked, SUITE offers to fetch it and to install it from the zip.

### Compatibility-model trust policy
The bundled nvngx_dlssnr.dll compatibility model is accepted only when its SHA-256 is E67DEE209320CDAFE0E93E45675D7AA34323A53ACC57A72B2E40A181581C989A. Any changed or substituted unsigned DLL is rejected. This exception covers the Neural Rendering model alone and does not disable signature verification anywhere else.

## v1.2.2-suite.2 - 2026-09-16

> [!IMPORTANT]
> **Which file do I download?**
> *(Qual arquivo baixar? / ¿Qué archivo descargo?)*
>
> **ENGLISH**
> * **Easiest install:** DLSS.5.SUITE.Web.Setup.v1.2.2-suite.2.exe - small download, it fetches the rest for you.
> * **Offline install:** DLSS.5.SUITE.Setup.v1.2.2-suite.2.exe - large file, everything included.
> * **No install:** DLSS.5.SUITE.Portable.v1.2.2-suite.2.zip - extract and run.
> * **RTX 30/40 owners:** also take DLSS.5.SUITE.NeuralScreen.Add-on.v1.2.2-suite.2.zip and install it from the Screen Engine window. RTX 50 cards do not need it.
> * **Do not download** Lossless_Scaling.zip, addons.zip or ShaderGlass_DLSS_5.zip by hand. The application fetches them when you set those features up.
>
> **PORTUGUES (PT-BR)**
> * **Instalação mais fácil:** DLSS.5.SUITE.Web.Setup.v1.2.2-suite.2.exe - download pequeno, ele baixa o resto.
> * **Instalação offline:** DLSS.5.SUITE.Setup.v1.2.2-suite.2.exe - arquivo grande, já inclui tudo.
> * **Sem instalação:** DLSS.5.SUITE.Portable.v1.2.2-suite.2.zip - extraia e execute.
> * **Placas RTX 30/40:** baixe também DLSS.5.SUITE.NeuralScreen.Add-on.v1.2.2-suite.2.zip e instale pela janela Screen Engine. RTX 50 não precisa.
> * **Não baixe** Lossless_Scaling.zip, addons.zip ou ShaderGlass_DLSS_5.zip manualmente. O aplicativo faz isso por você.
>
> **ESPAÑOL**
> * **Instalación más fácil:** DLSS.5.SUITE.Web.Setup.v1.2.2-suite.2.exe - descarga pequeña, obtiene el resto por ti.
> * **Instalación sin conexión:** DLSS.5.SUITE.Setup.v1.2.2-suite.2.exe - archivo grande, incluye todo.
> * **Sin instalación:** DLSS.5.SUITE.Portable.v1.2.2-suite.2.zip - extrae y ejecuta.
> * **Tarjetas RTX 30/40:** descarga también DLSS.5.SUITE.NeuralScreen.Add-on.v1.2.2-suite.2.zip e instálalo desde la ventana Screen Engine. Las RTX 50 no lo necesitan.
> * **No descargues** Lossless_Scaling.zip, addons.zip ni ShaderGlass_DLSS_5.zip a mano. La aplicación los obtiene cuando configuras esas funciones.

---

This release takes DLSS 5 off the modded-game path and onto the whole desktop, and finishes the parts that still needed a browser or a manual step. Two screen methods are chosen by your graphics card, both driven from one window inside SUITE, and the motion vectors, downloads and window behaviour found during testing are fixed. Game installation logic is unchanged.

### Highlights
- **The NeuralScreen add-on installs itself:** the Screen Engine window fetches it from this release, shows the transfer in megabytes and percent, checks its SHA-256 and unpacks it, unlocking the method when it lands. No browser, no file picking. Choosing a zip by hand is still there for anyone offline.
- **DLSS 5 on any screen:** two methods, picked by your card. **Screen Engine** (RTX 50) runs SUITE's own full-screen wrapper; **NeuralScreen** (RTX 30/40, and RTX 50 as an alternative) runs the desktop overlay. RTX 20 is marked unsupported because that generation starts without processing the picture. A method your card cannot run is locked, with a tooltip pointing at the other one.
- **One control window:** capture source, window crosshair, Neural Rendering intensity, structure, tone, skin, style, auto mask, passes, super resolution, comparison modes, screenshots, recording, comparison sweeps and a capture folder, in a themed window opened from Settings. Changes apply on their own while a session runs, so the Apply button is gone, and every slider has a box that takes typed values past its range wherever the engine allows it.
- **Crosshair window picking:** drag the crosshair onto a window, or click it once to arm it, switch to anything with Alt+Tab, and the next click picks that window.
- **NeuralScreen driven from SUITE:** Num2 brings SUITE's controls up over whatever is on screen, with a button back to the main window. NeuralScreen's own menu never opens, and its taskbar button, tray icon, popups and window titles carry SUITE's name, icon and palette.
- **Multi-pass neural rendering:** NeuralScreen runs the model one to four times per frame through ping-pong targets, so the passes control works in both methods.
- **Windows stop flashing:** in one-window mode the processed picture sits in the normal window order instead of above everything, and hides while the captured application is not focused. Alt+Tab reaches other programs again.
- **Motion vectors for modded games:** every game install writes the DLSS5_MV_PROVIDER definition and puts the provider technique ahead of the DLSS 5 feed, so the add-on stops reporting that DLSS is getting no motion vectors.
- **Verified, resumable downloads:** ShaderGlass and Lossless Scaling are set up one at a time, with resumable transfers, SHA-256 verification, megabyte and percentage progress, stall detection and retries, instead of a status line that never changed.
- **Signed model bundled:** NVIDIA's signed neural-rendering model ships beside the pinned compatibility model and is preferred, so the Screen Engine starts on a current driver with no manual step. A model folder of your own can be chosen, and bundled files are never modified.
- **Monitor capture recovery:** a monitor handle Windows rejects is looked up again and capture retried once, which clears the stale-handle CreateForMonitor failures.
- **NeuralScreen ships separately:** it is its own download, because only RTX 30/40 cards need it. Where the method would be locked, SUITE offers to fetch it and to install it from the zip.

### Compatibility-model trust policy
The bundled nvngx_dlssnr.dll compatibility model is accepted only when its SHA-256 is E67DEE209320CDAFE0E93E45675D7AA34323A53ACC57A72B2E40A181581C989A. Any changed or substituted unsigned DLL is rejected. This exception covers the Neural Rendering model alone and does not disable signature verification anywhere else.

## v1.2.2-suite.1 - 2026-09-16

> [!IMPORTANT]
> **Which file do I download?**
> *(Qual arquivo baixar? / ¿Qué archivo descargo?)*
>
> **ENGLISH**
> * **Easiest install:** DLSS.5.SUITE.Web.Setup.v1.2.2-suite.1.exe - small download, it fetches the rest for you.
> * **Offline install:** DLSS.5.SUITE.Setup.v1.2.2-suite.1.exe - large file, everything included.
> * **No install:** DLSS.5.SUITE.Portable.v1.2.2-suite.1.zip - extract and run.
> * **RTX 30/40 owners:** also take DLSS.5.SUITE.NeuralScreen.Add-on.v1.2.2-suite.1.zip and install it from the Screen Engine window. RTX 50 cards do not need it.
> * **Do not download** Lossless_Scaling.zip, addons.zip or ShaderGlass_DLSS_5.zip by hand. The application fetches them when you set those features up.
>
> **PORTUGUES (PT-BR)**
> * **Instalação mais fácil:** DLSS.5.SUITE.Web.Setup.v1.2.2-suite.1.exe - download pequeno, ele baixa o resto.
> * **Instalação offline:** DLSS.5.SUITE.Setup.v1.2.2-suite.1.exe - arquivo grande, já inclui tudo.
> * **Sem instalação:** DLSS.5.SUITE.Portable.v1.2.2-suite.1.zip - extraia e execute.
> * **Placas RTX 30/40:** baixe também DLSS.5.SUITE.NeuralScreen.Add-on.v1.2.2-suite.1.zip e instale pela janela Screen Engine. RTX 50 não precisa.
> * **Não baixe** Lossless_Scaling.zip, addons.zip ou ShaderGlass_DLSS_5.zip manualmente. O aplicativo faz isso por você.
>
> **ESPAÑOL**
> * **Instalación más fácil:** DLSS.5.SUITE.Web.Setup.v1.2.2-suite.1.exe - descarga pequeña, obtiene el resto por ti.
> * **Instalación sin conexión:** DLSS.5.SUITE.Setup.v1.2.2-suite.1.exe - archivo grande, incluye todo.
> * **Sin instalación:** DLSS.5.SUITE.Portable.v1.2.2-suite.1.zip - extrae y ejecuta.
> * **Tarjetas RTX 30/40:** descarga también DLSS.5.SUITE.NeuralScreen.Add-on.v1.2.2-suite.1.zip e instálalo desde la ventana Screen Engine. Las RTX 50 no lo necesitan.
> * **No descargues** Lossless_Scaling.zip, addons.zip ni ShaderGlass_DLSS_5.zip a mano. La aplicación los obtiene cuando configuras esas funciones.

---

This release takes DLSS 5 off the modded-game path and onto the whole desktop. Two screen methods are chosen by your graphics card, both driven from one window inside SUITE, and the motion vectors, downloads and window behaviour found during testing are fixed. Game installation logic is unchanged.

### Highlights
- **DLSS 5 on any screen:** two methods, picked by your card. **Screen Engine** (RTX 50) runs SUITE's own full-screen wrapper; **NeuralScreen** (RTX 30/40, and RTX 50 as an alternative) runs the desktop overlay. RTX 20 is marked unsupported because that generation starts without processing the picture. A method your card cannot run is locked, with a tooltip pointing at the other one.
- **One control window:** capture source, window crosshair, Neural Rendering intensity, structure, tone, skin, style, auto mask, passes, super resolution, comparison modes, screenshots, recording, comparison sweeps and a capture folder, in a themed window opened from Settings. Changes apply on their own while a session runs, so the Apply button is gone, and every slider has a box that takes typed values past its range wherever the engine allows it.
- **Crosshair window picking:** drag the crosshair onto a window, or click it once to arm it, switch to anything with Alt+Tab, and the next click picks that window.
- **NeuralScreen driven from SUITE:** Num2 brings SUITE's controls up over whatever is on screen, with a button back to the main window. NeuralScreen's own menu never opens, and its taskbar button, tray icon, popups and window titles carry SUITE's name, icon and palette.
- **Multi-pass neural rendering:** NeuralScreen runs the model one to four times per frame through ping-pong targets, so the passes control works in both methods.
- **Windows stop flashing:** in one-window mode the processed picture sits in the normal window order instead of above everything, and hides while the captured application is not focused. Alt+Tab reaches other programs again.
- **Motion vectors for modded games:** every game install writes the DLSS5_MV_PROVIDER definition and puts the provider technique ahead of the DLSS 5 feed, so the add-on stops reporting that DLSS is getting no motion vectors.
- **Verified, resumable downloads:** ShaderGlass and Lossless Scaling are set up one at a time, with resumable transfers, SHA-256 verification, megabyte and percentage progress, stall detection and retries, instead of a status line that never changed.
- **Signed model bundled:** NVIDIA's signed neural-rendering model ships beside the pinned compatibility model and is preferred, so the Screen Engine starts on a current driver with no manual step. A model folder of your own can be chosen, and bundled files are never modified.
- **Monitor capture recovery:** a monitor handle Windows rejects is looked up again and capture retried once, which clears the stale-handle CreateForMonitor failures.
- **NeuralScreen ships separately:** it is its own download, because only RTX 30/40 cards need it. Where the method would be locked, SUITE offers to fetch it and to install it from the zip.

### Compatibility-model trust policy
The bundled nvngx_dlssnr.dll compatibility model is accepted only when its SHA-256 is E67DEE209320CDAFE0E93E45675D7AA34323A53ACC57A72B2E40A181581C989A. Any changed or substituted unsigned DLL is rejected. This exception covers the Neural Rendering model alone and does not disable signature verification anywhere else.

## v1.2.1-suite.6 - 2026-09-12

> [!IMPORTANT]
> 🛑 **STOP! READ THIS BEFORE DOWNLOADING!** 🛑
> *(Leia isto antes de baixar! / ¡Lee esto antes de descargar!)*
> 
> **🇺🇸 ENGLISH:**
> * ⚡ **Easiest Install:** Download DLSS.5.SUITE.Web.Setup.v1.2.1-suite.6.exe (Downloads everything else for you automatically!)
> * 💽 **Offline Install:** Download DLSS.5.SUITE.Setup.v1.2.1-suite.6.exe (Huge file, has everything included).
> * 📁 **No Install Required:** Download DLSS.5.SUITE.Portable.v1.2.1-suite.6.zip and just extract it.
> * ⚠️ **DO NOT DOWNLOAD** Lossless_Scaling.zip, addons.zip, or ShaderGlass_DLSS_5.zip manually. The application will fetch them for you!
> 
> **🇧🇷 PORTUGUES (PT-BR):**
> * ⚡ **Instalação mais fácil:** Baixe o DLSS.5.SUITE.Web.Setup.v1.2.1-suite.6.exe (Ele baixa o resto para você automaticamente!)
> * 💽 **Instalação Offline:** Baixe o DLSS.5.SUITE.Setup.v1.2.1-suite.6.exe (Arquivo grande, já inclui tudo).
> * 📁 **Não requer instalação:** Baixe o DLSS.5.SUITE.Portable.v1.2.1-suite.6.zip e apenas extraia.
> * ⚠️ **NÃO BAIXE** Lossless_Scaling.zip, addons.zip ou ShaderGlass_DLSS_5.zip manualmente. O aplicativo fará isso por você!
> 
> **🇪🇸 ESPAÑOL:**
> * ⚡ **Instalación más fácil:** Descarga DLSS.5.SUITE.Web.Setup.v1.2.1-suite.6.exe (¡Descarga todo lo demás por ti automáticamente!)
> * 💽 **Instalación sin conexión:** Descarga DLSS.5.SUITE.Setup.v1.2.1-suite.6.exe (Archivo grande, incluye todo).
> * 📁 **Sin instalación:** Descarga DLSS.5.SUITE.Portable.v1.2.1-suite.6.zip y simplemente extráelo.
> * ⚠️ **NO DESCARGUES** Lossless_Scaling.zip, addons.zip ni ShaderGlass_DLSS_5.zip manualmente. ¡La aplicación los descargará por ti!

---

This release finalizes the anti-cheat safe routing architecture and addresses multiple edge-cases with AppData caching and update fetching.

### Highlights
- **Lossless Scaling Anti-Cheat Routing:** Full implementation of WGC-safe injection natively bypassing the game directory entirely. The overlays now route their .ini configs and reshade-shaders into Lossless Scaling's AppData cache rather than polluting the local directory.
- **AppData Collision Fix:** Isolated the DLSS 5 SUITE AppData cache folder to prevent conflict with DLSS 5 MANAGER when updating or fetching icons.
- **GitHub API Digest Fix:** The UpdateChecker will no longer crash the UI when the GitHub API aggressively caches requests and omits the SHA-256 signature payload. 
- **Restored Mod Files Payload:** Correctly embedded the mod files directory to both the Offline Setup and Portable zips.

## v1.2.1-suite.5 - 2026-09-12

> [!IMPORTANT]
> 🚨 **STOP! READ THIS BEFORE DOWNLOADING!** 🚨
> *(Leia isto antes de baixar! / ¡Lee esto antes de descargar!)*
> 
> **🇬🇧 ENGLISH:**
> * 🟢 **Easiest Install:** Download DLSS.5.SUITE.Web.Setup.v1.2.1-suite.5.exe (Downloads everything else for you automatically!)
> * 🔵 **Offline Install:** Download DLSS.5.SUITE.Setup.v1.2.1-suite.5.exe (Huge file, has everything included).
> * 🟡 **No Install Required:** Download DLSS.5.SUITE.Portable.v1.2.1-suite.5.zip and just extract it.
> * ❌ **DO NOT DOWNLOAD** Lossless_Scaling.zip, addons.zip, or ShaderGlass_DLSS_5.zip manually. The application will fetch them for you!
> 
> **🇧🇷 PORTUGUÊS (PT-BR):**
> * 🟢 **Instalação mais fácil:** Baixe o DLSS.5.SUITE.Web.Setup.v1.2.1-suite.5.exe (Ele baixa o resto para você automaticamente!)
> * 🔵 **Instalação Offline:** Baixe o DLSS.5.SUITE.Setup.v1.2.1-suite.5.exe (Arquivo grande, já inclui tudo).
> * 🟡 **Não requer instalação:** Baixe o DLSS.5.SUITE.Portable.v1.2.1-suite.5.zip e apenas extraia.
> * ❌ **NÃO BAIXE** Lossless_Scaling.zip, addons.zip ou ShaderGlass_DLSS_5.zip manualmente. O aplicativo fará isso por você!
> 
> **🇪🇸 ESPAÑOL:**
> * 🟢 **Instalación más fácil:** Descarga DLSS.5.SUITE.Web.Setup.v1.2.1-suite.5.exe (¡Descarga todo lo demás por ti automáticamente!)
> * 🔵 **Instalación sin conexión:** Descarga DLSS.5.SUITE.Setup.v1.2.1-suite.5.exe (Archivo grande, incluye todo).
> * 🟡 **Sin instalación:** Descarga DLSS.5.SUITE.Portable.v1.2.1-suite.5.zip y simplemente extráelo.
> * ❌ **NO DESCARGUES** Lossless_Scaling.zip, addons.zip ni ShaderGlass_DLSS_5.zip manualmente. ¡La aplicación los descargará por ti!

---

This release overhauls the Lossless Scaling and ShaderGlass installation methods for complete anti-cheat safety, along with several update and filtering improvements.

### Highlights
- **Installers Rewritten:** Fully replaced the Lossless Scaling and ShaderGlass mod installation methods to use pre-compiled .zip payloads (Lossless_Scaling.zip, addons.zip, ShaderGlass_DLSS_5.zip).
- **Anti-Cheat Safe Architecture:** Overlays and addons are extracted directly into the Lossless Scaling Steam directory and the local AppData cache instead of game directories, strictly enforcing Windows Graphics Capture (WGC) for titles with aggressive anti-cheat systems like Roblox and Fortnite.
- **Dynamic Updates Check:** The Update Checker now dynamically reads the internal assembly version at runtime instead of relying on a hardcoded string.
- **Version Release Tags:** Fixed the Web Setup downloader to correctly fetch assets matching its own tag version (e.g. 1.2.1-suite.5) rather than generically downloading from the latest release which could cause version drift.
- **Non-Game App Filtering:** Enhanced the game scanner to properly filter out non-game desktop utility apps like Wallpaper Engine, preventing them from being mistakenly flagged for mod installations.
- **Fortnite Profiles Updated:** Added strict profile rules for Fortnite, actively warning users of Byfron / Easy Anti-Cheat bans and natively directing them to the Lossless Scaling method.
- **UI Enhancements:** Retained smooth kinetic scrolling improvements in the main list views.
- **README Enhancements:** Added proper GitHub version badges and official Ko-Fi SVG buttons.

## v1.2.1-suite.4 - 2026-09-11

This release adds a smooth, browser-free update experience for both DLSS 5 SUITE and its optional universal ShaderGlass route.

### Highlights

- Downloads application updates directly inside DLSS 5 SUITE, verifies the GitHub-published SHA-256 digest, and launches Setup automatically.
- Adds visible download and setup progress instead of sending users through a browser.
- Automatically discovers and installs current official ShaderGlass and DLSS5-Feeder releases from allowlisted upstream projects.
- Verifies release assets before use and stops safely if a source, filename, release tag, or digest does not match expectations.
- Adds Full Setup, compact Web Setup, and Portable ZIP release choices.
- Web Setup automatically downloads and verifies the matching Portable ZIP before installing.
- Excludes the unsafe MV 2.7 / `eurotrucks2.exe` package entirely.
- Preserves the existing game scanner, API detection, and per-game installation routes.

### Downloads

- **Full Setup:** includes the application payload for an offline installation.
- **Web Setup:** a smaller installer that retrieves the matching verified Portable ZIP.
- **Portable ZIP:** extract and run without the installer.

## v1.2.1-suite.3 - 2026-09-10

This release introduces verified on-demand compatibility setup and three distribution choices while keeping the existing game-management logic intact.

### Highlights

- Adds an automatic ShaderGlass compatibility setup intended for games such as Roblox and Minecraft.
- Retrieves official ShaderGlass and DLSS5-Feeder releases from allowlisted upstream projects.
- Verifies downloaded release assets with SHA-256 and rejects missing or unexpected files.
- Shows progress while downloading and configuring ShaderGlass components.
- Adds Full Setup, smaller Web Setup, and Portable ZIP build formats.
- Web Setup downloads and verifies the matching Portable ZIP before installation.
- Keeps compatibility downloads outside the main installer to reduce its size.
- Excludes the unsafe MV 2.7 / `eurotrucks2.exe` package entirely.
- Preserves existing game scanning, rendering-API detection, Steam artwork handling, and per-game installation logic.

### Update-flow testing

Suite.3 can be installed as the starting version when testing the automatic upgrade to suite.4. The browser-free in-app setup download is introduced in suite.4.

## v1.2.1-suite.2 - 2026-09-10

This release replaces the large embedded ShaderGlass payload with an automatic, update-aware compatibility setup.

### Changes

- Downloads the current stable ShaderGlass and DLSS5-Feeder releases on demand from allowlisted upstream repositories.
- Requires and validates the SHA-256 digest published for each selected GitHub release asset.
- Rejects unexpected asset names, missing digests, hash mismatches and unsafe archive paths.
- Uses the release-matched official DLSS5-Feeder installer with the RenoDX D3D route, no elevation and no system-wide changes.
- Displays setup and download progress inside DLSS 5 SUITE.
- Caches verified downloads for faster repair and future updates.
- Records installed ShaderGlass and Feeder release tags and hashes.
- Checks for current component releases whenever Setup/Update is selected.
- Removes the large compatibility payload from the main installer while preserving all existing game-scanning and installation logic.
- MV 2.7 and `eurotrucks2.exe` remain excluded.

## v1.2.0-suite.1 - 2026-09-10

### Highlights

- Full DLSS 5 SUITE visual identity, themed 4K nebula backgrounds, visible NVIDIA marks across themes, animated particles and occasional shooting stars.
- Improved game discovery, rendering API detection, Steam artwork handling and batch installation feedback.
- Multiple native installation routes for DirectX 9, DirectX 11, DirectX 12, Vulkan, OptiScaler and supported emulators.
- Installer, update checking, community features, payload management, settings search and original-project attribution.

