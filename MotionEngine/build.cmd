@echo off
rem Motion Forge's engine (MotionEngine\), built from source with MSVC.
rem
rem   build.cmd              into MotionEngine\build
rem   build.cmd <folder>     into the folder named
rem
rem It is one translation unit (sm_engine.cpp); its shaders are HLSL strings
rem compiled at run time, so there is no shader step here.
rem
rem Needs Visual Studio 2022 Build Tools (C++ workload) and the NVIDIA DLSS SDK
rem for the NGX headers and nvsdk_ngx_s.lib (the static C runtime build, so
rem the program needs no Visual C++ runtime installed beside it). The SDK is found through
rem DLSS_SDK_DIR, or else ..\..\dlss-sdk beside the repo - the same place
rem tools\build-engine.bat looks. It is never committed: its licence does not
rem allow that.
rem
rem DLSS MFG goes through NVIDIA Streamline: its headers are the Screen Engine's
rem copy (Engine\external\streamline\include, MIT); its DLLs are loaded at run
rem time from SUITE's engine\ folder, where tools\build-setup.sh puts them for
rem the Screen Engine (from STREAMLINE_SDK_DIR - never committed).
rem
rem FSR FG goes through AMD's FidelityFX API: its headers are the repo's shared
rem copy (external\fidelityfx\include, MIT); its runtime, amd_fidelityfx_dx12.dll,
rem is loaded at run time from SUITE's engine\ folder, where tools\build-setup.sh
rem puts it (fetched by tools\get-fidelityfx.ps1 - never committed).
rem
rem Nothing else is copied beside the program. Intel's XeSS libraries are the
rem user's own files (SUITE keeps them in its data folder), and SMAI's ONNX
rem Runtime, DirectML and network are downloaded by SUITE on request - see
rem Services\MotionEngine.fs.
setlocal
set "HERE=%~dp0"
set "OUT=%~1"
if "%OUT%"=="" set "OUT=%HERE%build"
if "%DLSS_SDK_DIR%"=="" set "DLSS_SDK_DIR=%HERE%..\..\dlss-sdk"
if not exist "%DLSS_SDK_DIR%\include\nvsdk_ngx.h" (
    echo DLSS SDK not found at "%DLSS_SDK_DIR%" - set DLSS_SDK_DIR
    exit /b 1
)
set "NGX_LIB=%DLSS_SDK_DIR%\lib\Windows_x86_64\x64\nvsdk_ngx_s.lib"
if not exist "%NGX_LIB%" set "NGX_LIB=%DLSS_SDK_DIR%\lib\nvsdk_ngx_s.lib"

set "VCVARS="
for /f "usebackq delims=" %%i in (`"%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -find VC\Auxiliary\Build\vcvars64.bat`) do set "VCVARS=%%i"
if "%VCVARS%"=="" (
    echo Visual Studio C++ build tools not found
    exit /b 1
)
call "%VCVARS%" >nul || exit /b 1

if not exist "%OUT%" mkdir "%OUT%"
if not exist "%OUT%\obj" mkdir "%OUT%\obj"
pushd "%HERE%"
cl /nologo /O2 /EHsc /W3 /MT /utf-8 /std:c++17 /I"%DLSS_SDK_DIR%\include" /Iinclude /Iinclude\ort /I"%HERE%..\Engine\external\streamline\include" /I"%HERE%..\external\fidelityfx\include" sm_engine.cpp /Fo"%OUT%\obj\\" /Fe:"%OUT%\MotionForgeEngine.exe" /link "%NGX_LIB%" version.lib kernel32.lib user32.lib gdi32.lib advapi32.lib ole32.lib d3d11.lib d3d12.lib dxgi.lib d3dcompiler.lib WindowsApp.lib dwmapi.lib setupapi.lib
set "CODE=%ERRORLEVEL%"
popd
if not "%CODE%"=="0" exit /b 2
del /q "%OUT%\MotionForgeEngine.exp" "%OUT%\MotionForgeEngine.lib" 2>nul
echo MOTION ENGINE BUILD OK
exit /b 0
