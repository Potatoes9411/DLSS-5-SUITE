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

## Highlights

- **Launch and stop games from their cards:** the play control shows startup progress, can cancel a launch, and becomes a stop control while the game is running. SUITE first asks whether a Steam title should start through Steam or through its executable, then remembers the choice.
- **Per-game launch controls:** launch arguments, **Ask again**, and **Open game folder** are available from Manage. Stopping asks the game window to close before using a forced stop as a last resort.
- **Per-game Screen Engine presets:** save the current capture and rendering settings for a game and have SUITE load them when that game starts.
- **Live Screen Engine tuning:** supported changes reach a running session without blanking the picture. Only source, output, or capture changes rebuild the session.
- **Complete NeuralScreen controls:** Boost, work scale, frame generation, frame multiplier, motion backend, unchanged-frame skipping, HDR capture, Spout2 output, recording indicator, adapter selection, and original-colour lock are available in the SUITE control window.
- **Better library tools:** search and filters work with the existing sort choices, scans can be cancelled, and keyboard shortcuts cover common actions.
- **Safer settings and diagnostics:** settings are replaced atomically, a persistent application log records failures, and startup recovers cleanly from an interrupted save.
- **Correct motion-vector setup:** ReShade receives the real Lumenite technique names, and SUITE removes the provider that was not selected.
- **DX11 OptiScaler DLSS-NR:** the Neural Rendering route enables NR and selects the D3D12 bridge that OptiScaler needs for DirectX 11 games.
- **Clean final UI behavior:** numeric fields have enough room for typed values, the play control stays above card hints, and scrolling stops cleanly at library edges without trapping movement in an overscroll band.
- **Reliable add-on download resume:** a fully downloaded partial file is verified and promoted instead of sending an invalid resume request.

## Compatibility-model trust policy

The bundled `nvngx_dlssnr.dll` compatibility model is accepted only when its SHA-256 is `E67DEE209320CDAFE0E93E45675D7AA34323A53ACC57A72B2E40A181581C989A`. Any changed or substituted unsigned DLL is rejected. This exception covers that Neural Rendering model alone and does not disable verification for other runtime files.

DLSS 5 SUITE is a modified version of DLSS 5 MANAGER by NODIX TECH / Numidia Studios, used with permission. The original remains available at https://numidiastudios.com/dlss-5-manager/.
