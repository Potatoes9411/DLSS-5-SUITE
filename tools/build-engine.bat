@echo off
rem Build the screen engine (Engine\) with MSVC, CMake and Ninja.
rem
rem Needs Visual Studio 2022 Build Tools (C++ workload) and the NVIDIA DLSS SDK.
rem The SDK is found through DLSS_SDK_DIR, or else ..\..\dlss-sdk beside the repo.
rem It is never committed: its licence does not allow that.
setlocal
set "REPO=%~dp0.."
if "%DLSS_SDK_DIR%"=="" set "DLSS_SDK_DIR=%~dp0..\..\dlss-sdk"
if not exist "%DLSS_SDK_DIR%\include\nvsdk_ngx.h" (
    echo DLSS SDK not found at "%DLSS_SDK_DIR%" - set DLSS_SDK_DIR
    exit /b 1
)

set "VCVARS="
for /f "usebackq delims=" %%i in (`"%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -find VC\Auxiliary\Build\vcvars64.bat`) do set "VCVARS=%%i"
if "%VCVARS%"=="" (
    echo Visual Studio C++ build tools not found
    exit /b 1
)
call "%VCVARS%" >nul || exit /b 1

cmake -S "%REPO%\Engine" -B "%REPO%\Engine\build" -G Ninja -DCMAKE_BUILD_TYPE=Release "-DDLSS_SDK_DIR=%DLSS_SDK_DIR%" || exit /b 2
cmake --build "%REPO%\Engine\build" || exit /b 3
"%REPO%\Engine\build\dscreen_tests.exe" 1000 || exit /b 4
echo ENGINE BUILD OK
