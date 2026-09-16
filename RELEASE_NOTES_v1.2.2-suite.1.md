# DLSS 5 SUITE v1.2.2-suite.1

This release integrates the Full-Screen DLSS 5 Screen Engine and finishes its application controls without changing the established game installation logic.

## Highlights

- Adds two GPU-aware screen methods: Screen Engine for RTX 50 series and NeuralScreen for RTX 30/40 series, with RTX 50 support as an alternative.
- Labels RTX 20 series as unsupported because the NeuralScreen model can start without processing the picture on that generation.
- Integrates NeuralScreen controls into SUITE: Num2 opens and hides SUITE's styled control window, with a button to return to the main app.
- Keeps one-window NeuralScreen output in the normal window stack so Alt+Tab works without per-frame restacking flicker.
- Synchronizes NeuralScreen's real recording state back to SUITE, including start failures and externally triggered recording changes.
- Refreshes a rejected Windows monitor handle and retries monitor capture once, fixing stale-handle `CreateForMonitor` failures without changing capture methods.
- Adds full-monitor and selected-window capture with optional output-monitor selection.
- Adds an in-app running-window picker while retaining manual title/handle entry.
- Adds direct screenshot, recording and comparison-sweep controls without requiring the engine's separate panel.
- Adds Neural Rendering intensity, structure, tone, skin, style, masks and repeat-pass controls.
- Adds super resolution, comparison modes, VSync and the engine control panel.
- Adds advanced model presets, built-in/disabled motion selection and scale, cursor behavior, capture/window controls, NGX settings, adapter selection and diagnostics.
- Restricts the Optical Flow grid selector to the valid 1×1, 2×2 and 4×4 values.
- Stores captures and recordings in a writable, user-selectable Screen Engine capture folder with Browse and Open controls.
- Refreshes every visible control correctly after **Reset to defaults**.
- Expands Settings search so Screen Engine controls can be found by their actual names.
- Accepts the bundled neural-rendering compatibility model by one exact pinned SHA-256 while retaining NVIDIA Authenticode verification for every other runtime DLL.

## Compatibility-model trust policy

The bundled `nvngx_dlssnr.dll` compatibility model is accepted only when its SHA-256 is:

`E67DEE209320CDAFE0E93E45675D7AA34323A53ACC57A72B2E40A181581C989A`

Any changed or substituted unsigned DLL is rejected. This exception applies only to the Neural Rendering model and does not disable signature verification globally.

DLSS 5 SUITE is a modified version of DLSS 5 MANAGER by NODIX TECH / Numidia Studios, used with permission. The original remains available at https://numidiastudios.com/dlss-5-manager/.
