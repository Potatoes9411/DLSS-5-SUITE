#!/bin/sh
# Build DLSS 5 SUITE and package the installer.
#
# The setup is named for the version it contains and written into dist/ beside
# whatever is already there. Old setups are never deleted: a release that has
# been handed to someone has to stay reachable, and overwriting the file that a
# download link points at silently changes what that link serves.
#
#   ./build-setup.sh              publish + package
#   ./build-setup.sh --app-only   just the application publish
set -e

# Paths come from where this script lives (repo/tools/), not a hardcoded
# user folder, so it works from any checkout.
APP="$(cd "$(dirname "$0")/.." && (pwd -W 2>/dev/null || pwd))"
ROOT="$(cd "$APP/.." && (pwd -W 2>/dev/null || pwd))"
DIST="$ROOT/dist"

# The version lives in Directory.Build.props; everything else reads it from there.
VERSION=$(grep -o '<SuiteVersion>[^<]*</SuiteVersion>' "$APP/Directory.Build.props" | head -1 | sed 's/<[^>]*>//g')
if [ -z "$VERSION" ]; then
    echo "could not read <SuiteVersion> from Directory.Build.props" >&2
    exit 1
fi
echo "version: $VERSION"

# Windows version resources are four numbers and nothing else, so a semver
# suffix cannot go in them. Encode it instead: "1.2.0-suite.3" becomes 1.2.0.3,
# which keeps every suite build distinguishable in the file's own properties.
# Without this, two setups sitting side by side in dist/ both read 1.2.0.0 and
# the versioned filename is the only thing telling them apart.
BASE=$(printf '%s' "$VERSION" | sed 's/-.*//')
REV=$(printf '%s' "$VERSION" | sed -n 's/.*-suite[.]\([0-9][0-9]*\).*/\1/p')
if [ -z "$REV" ]; then REV=0; fi
NUMERIC="$BASE.$REV"
echo "file version: $NUMERIC"

FULL_TARGET="$DIST/DLSS 5 SUITE Setup v$VERSION.exe"
NEURALSCREEN_TARGET="$DIST/DLSS 5 SUITE NeuralScreen Add-on v$VERSION.zip"
WEB_TARGET="$DIST/DLSS 5 SUITE Web Setup v$VERSION.exe"
PORTABLE_TARGET="$DIST/DLSS 5 SUITE Portable v$VERSION.zip"
for TARGET in "$FULL_TARGET" "$WEB_TARGET" "$PORTABLE_TARGET" "$NEURALSCREEN_TARGET"; do
    if [ -e "$TARGET" ]; then
        echo "release artifact already exists; refusing to overwrite: $TARGET" >&2
        exit 1
    fi
done

VERSION_PROPS="-p:Version=$VERSION -p:FileVersion=$NUMERIC -p:AssemblyVersion=$NUMERIC -p:InformationalVersion=$VERSION"

# The app locks its own output while running.
powershell -NoProfile -Command "Get-Process -Name 'DLSS 5 SUITE' -ErrorAction SilentlyContinue | ForEach-Object { \$_.CloseMainWindow() | Out-Null }; Start-Sleep -Milliseconds 2500; Get-Process -Name 'DLSS 5 SUITE' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue" >/dev/null 2>&1 || true

# NeuralScreen (RTX 30/40 DLSS 5) ships in mod files/neuralscreen, controlled by
# SUITE through its bridge. Applying it is a no-op when already there.
if [ -f "$APP/mod files/neuralscreen/runtime/python.exe" ]; then
    echo "--- NeuralScreen SUITE bridge ---"
    "$APP/mod files/neuralscreen/runtime/python.exe" "$APP/tools/neuralscreen-bridge/apply_bridge.py" "$APP/mod files/neuralscreen"
else
    echo "warning: mod files/neuralscreen is missing - the NeuralScreen method will be unavailable" >&2
fi

echo "--- publishing application ---"
rm -rf "$APP/publish"
dotnet publish "$APP/DLSS 5 SUITE.fsproj" -c Release -r win-x64 --self-contained true $VERSION_PROPS -o "$APP/publish"

echo "ShaderGlass compatibility components download on demand"

# The screen engine ships in publish/engine/. A setup without it would show
# the Screen Engine page with nothing behind it, so a failed engine build stops
# here; SKIP_ENGINE=1 builds without it on purpose.
if [ "$SKIP_ENGINE" = "1" ]; then
    echo "--- screen engine skipped (SKIP_ENGINE=1) ---"
else
    echo "--- building screen engine ---"
    cmd //c "$(printf '%s' "$APP/tools/build-engine.bat" | sed 's#/#\\#g')"
    mkdir -p "$APP/publish/engine"
    cp "$APP/Engine/build/FullScreenWrapperForDLSS5.exe" "$APP/publish/engine/"
    cp "$APP/Engine/build/nvngx_dlss.dll" "$APP/publish/engine/"
    cp "$APP/Engine/LICENSE" "$APP/publish/engine/LICENSE.txt"
fi

# NeuralScreen is its own download: it is 435 MB, only RTX 30/40 cards need it,
# and it carries NVIDIA runtimes of its own. The setup ships without it and
# SUITE installs the zip on request, into the same "mod files/neuralscreen".
mkdir -p "$DIST"
if [ -d "$APP/publish/mod files/neuralscreen" ]; then
    echo "--- packaging NeuralScreen add-on ---"
    powershell -NoProfile -Command "Compress-Archive -Path '$APP/publish/mod files/neuralscreen' -DestinationPath '$NEURALSCREEN_TARGET' -CompressionLevel Optimal"
    rm -rf "$APP/publish/mod files/neuralscreen"
    echo "built: $NEURALSCREEN_TARGET"
fi

if [ "$1" = "--app-only" ]; then
    echo "done: $APP/publish"
    exit 0
fi

mkdir -p "$DIST"
echo "--- packaging portable ZIP ---"
powershell -NoProfile -Command "Compress-Archive -Path '$APP/publish/*' -DestinationPath '$PORTABLE_TARGET' -CompressionLevel Optimal"

echo "--- packaging installer ---"
rm -rf "$APP/Setup/bin" "$APP/Setup/obj" "$ROOT/setup-out" "$ROOT/web-setup-out"
dotnet publish "$APP/Setup/DLSS5SuiteSetup.csproj" \
    -c Release -r win-x64 --self-contained true \
    -p:PublishSingleFile=true \
    -p:EnableCompressionInSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    $VERSION_PROPS \
    -p:PayloadDir="$APP/publish" \
    -o "$ROOT/setup-out"

cp "$ROOT/setup-out/DLSS 5 SUITE Setup.exe" "$FULL_TARGET"

echo "--- packaging web installer ---"
rm -rf "$APP/Setup/bin" "$APP/Setup/obj"
dotnet publish "$APP/Setup/DLSS5SuiteSetup.csproj" \
    -c Release -r win-x64 --self-contained true \
    -p:PublishSingleFile=true \
    -p:EnableCompressionInSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:WebSetup=true \
    $VERSION_PROPS \
    -o "$ROOT/web-setup-out"
cp "$ROOT/web-setup-out/DLSS 5 SUITE Setup.exe" "$WEB_TARGET"

echo ""
echo "built: $FULL_TARGET"
echo "built: $WEB_TARGET"
echo "built: $PORTABLE_TARGET"
echo "built: $NEURALSCREEN_TARGET"
ls -1sh "$DIST" | sed 's/^/  /'
