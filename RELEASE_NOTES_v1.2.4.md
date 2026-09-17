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

## Highlights

- **Manage Modal Scrolling Fixed:** The Manage window's internal scroll area now extends fully to the bottom padding, resolving an issue where the new GTA V Neural Rendering route panel would physically clip text at the bottom edge.
- **Screen Engine Method Selection Fix:** Switching away from the new RobloxShadeHost method in the Screen Engine tab now correctly de-activates its UI highlight, preventing two tabs from appearing selected simultaneously.
- **Rockstar Games Launcher Anti-Cheat Guidance:** Added detailed, accurate guidance regarding the Rockstar Games Launcher BattlEye toggle. Since the Rockstar Launcher uses an encrypted, binary settings file (settings_user.dat), its configuration cannot be safely modified programmatically. The automatic -nobattleye insertion in commandline.txt works natively, but manual user intervention (unchecking the box in Rockstar Settings) is required once if the launcher overrides the commandline config.

## Compatibility-model trust policy

The bundled 
vngx_dlssnr.dll compatibility model is accepted only when its SHA-256 is E67DEE209320CDAFE0E93E45675D7AA34323A53ACC57A72B2E40A181581C989A. Any changed or substituted unsigned DLL is rejected. This exception covers that Neural Rendering model alone and does not disable verification for other runtime files.

DLSS 5 SUITE is a modified version of DLSS 5 MANAGER by NODIX TECH / Numidia Studios, used with permission. The original remains available at https://numidiastudios.com/dlss-5-manager/.
