#!/bin/sh
# Rebuild and re-upload releases from their branches, one after another.
#
#   ./release_all.sh fix-updater-4:1.2.1-suite.4 fix-updater-5:1.2.1-suite.5 ...
#
# Stops at the first failure. Never deletes an existing artifact - anything
# already in dist/ for a version is moved into dist/superseded first.
set -e

# Paths come from where this script lives (repo/tools/), not a hardcoded
# user folder, so it works from any checkout.
APP="$(cd "$(dirname "$0")/.." && (pwd -W 2>/dev/null || pwd))"
ROOT="$(cd "$APP/.." && (pwd -W 2>/dev/null || pwd))"
STAMP=$(date +%Y%m%d-%H%M%S)

for pair in "$@"; do
    BRANCH="${pair%%:*}"
    VER="${pair##*:}"
    echo ""
    echo "================ $BRANCH -> v$VER ================"

    git -C "$APP" checkout -q -- bin obj 2>/dev/null || true
    if [ -n "$(git -C "$APP" status --short | grep -v '^??')" ]; then
        echo "working tree not clean; stopping" >&2
        git -C "$APP" status --short | grep -v '^??' >&2
        exit 1
    fi
    git -C "$APP" checkout -q "$BRANCH"
    echo "on $(git -C "$APP" rev-parse --abbrev-ref HEAD) @ $(git -C "$APP" rev-parse --short HEAD)"

    FSV=$(grep -o '<SuiteVersion>[^<]*' "$APP/Directory.Build.props" 2>/dev/null | sed 's/<SuiteVersion>//')
    if [ "$FSV" != "$VER" ]; then
        echo "Directory.Build.props says '$FSV', expected $VER; stopping" >&2
        exit 1
    fi

    mkdir -p "$ROOT/dist/superseded"
    for f in "DLSS 5 SUITE Setup v$VER.exe" "DLSS 5 SUITE Web Setup v$VER.exe" "DLSS 5 SUITE Portable v$VER.zip"; do
        if [ -f "$ROOT/dist/$f" ]; then
            mv "$ROOT/dist/$f" "$ROOT/dist/superseded/${f%.*} ($STAMP).${f##*.}"
            echo "moved aside: $f"
        fi
    done

    "$APP/tools/build-setup.sh"

    HEAD_AFTER=$(git -C "$APP" rev-parse --abbrev-ref HEAD)
    if [ "$HEAD_AFTER" != "$BRANCH" ]; then
        echo "branch changed during the build ($HEAD_AFTER); not uploading" >&2
        exit 1
    fi

    powershell -NoProfile -ExecutionPolicy Bypass -Command "& '$APP/tools/upload_verified.ps1' -Tag 'v$VER' -Files @('$ROOT/dist/DLSS 5 SUITE Setup v$VER.exe','$ROOT/dist/DLSS 5 SUITE Web Setup v$VER.exe','$ROOT/dist/DLSS 5 SUITE Portable v$VER.zip'); exit \$LASTEXITCODE"
done

git -C "$APP" checkout -q -- bin obj 2>/dev/null || true
echo ""
echo "ALL RELEASES DONE"
