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

## Highlights
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

## Compatibility-model trust policy
The bundled nvngx_dlssnr.dll compatibility model is accepted only when its SHA-256 is E67DEE209320CDAFE0E93E45675D7AA34323A53ACC57A72B2E40A181581C989A. Any changed or substituted unsigned DLL is rejected. This exception covers the Neural Rendering model alone and does not disable signature verification anywhere else.

DLSS 5 SUITE is a modified version of DLSS 5 MANAGER by NODIX TECH / Numidia Studios, used with permission. The original remains available at https://numidiastudios.com/dlss-5-manager/.
