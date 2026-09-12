#!/usr/bin/env bash
#
# Builds Vocaluxe for macOS into ./dist/Vocaluxe and writes the launchers ./dist/Vocaluxe.sh and
# ./dist/Vocaluxe.command (Finder runs the .command one on a double click).
#
# Step for step the same as .build/build-linux.sh:
#   1. compiles the native helpers (libPitchTracker.dll.dylib, libacinerella.dylib)
#   2. publishes the managed app with dotnet
#   3. assembles the game data (Output/) next to the executable
#   4. drops in the native helper libraries
#   5. copies the party-mode sources Roslyn compiles at runtime
#
# Environment overrides:
#   DOTNET         dotnet command/path         (default: dotnet)
#   RID            runtime identifier          (default: from uname -m, osx-arm64 or osx-x64)
#   SELFCONTAINED  bundle the .NET runtime     (default: true)
#
# Build dependencies:
#   Xcode command line tools   xcode-select --install
#   .NET 10 SDK                brew install --cask dotnet-sdk    (or Microsoft's installer)
#   ffmpeg                     brew install ffmpeg
#
# ffmpeg is a build *and* a runtime dependency: Acinerella links against Homebrew's copies with
# absolute paths, so a self-contained publish does not bundle them. The build is therefore good for
# the machine that made it (and any other with the same Homebrew prefix), not for handing around.
#
# Windowing and GL go through OpenTK 4, which ships its own GLFW; SkiaSharp, PortAudio and SQLite
# come as dylibs from NuGet. Nothing else needs installing.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
DIST_ROOT="$ROOT/dist"
DIST="$DIST_ROOT/Vocaluxe"
DOTNET="${DOTNET:-dotnet}"
SELFCONTAINED="${SELFCONTAINED:-true}"

if [ -z "${RID:-}" ]; then
    case "$(uname -m)" in
        arm64)  RID="osx-arm64" ;;   # Apple Silicon
        x86_64) RID="osx-x64" ;;     # Intel
        *)      echo "Unknown architecture $(uname -m); set RID=osx-arm64 or RID=osx-x64" >&2; exit 1 ;;
    esac
fi

echo ">> [0/5] Checking the toolchain"
_missing=0
_require() {
    # $1 = what to look for, $2 = how to install it
    if ! command -v "$1" >/dev/null 2>&1; then
        echo "   missing: $1 -- $2" >&2
        _missing=1
    fi
}
_require "$DOTNET" "install the .NET 10 SDK (brew install --cask dotnet-sdk)"
_require make "run: xcode-select --install"
_require clang "run: xcode-select --install"
# Acinerella needs ffmpeg's headers and libraries. The makefile asks pkg-config first and falls back
# to the Homebrew prefix, so either one has to be able to answer.
if ! pkg-config --exists libavformat 2>/dev/null; then
    if ! command -v brew >/dev/null 2>&1 || [ ! -d "$(brew --prefix 2>/dev/null)/include/libavformat" ]; then
        echo "   missing: ffmpeg headers -- run: brew install ffmpeg" >&2
        _missing=1
    fi
fi
[ "$_missing" -eq 0 ] || { echo ">> Install the above and run this again." >&2; exit 1; }

echo ">> [1/5] Building native helpers (PitchTracker, Acinerella)"
make -C "$ROOT/PitchTracker"
make -C "$ROOT/Vocaluxe/Lib/Video/Acinerella"

echo ">> [2/5] Publishing managed app ($RID, self-contained=$SELFCONTAINED)"
rm -rf "$DIST"
"$DOTNET" publish "$ROOT/Vocaluxe/Vocaluxe.csproj" \
    -c Release -r "$RID" --self-contained "$SELFCONTAINED" \
    -p:DebugType=none -o "$DIST"

echo ">> [3/5] Copying game data (Themes, Graphics, Fonts, Languages, ...)"
cp -R "$ROOT/Output/." "$DIST/"
# Windows-only leftovers, and any Linux libraries a shared checkout may have left in Output/
rm -f "$DIST"/*.ico "$DIST"/*.so 2>/dev/null || true

echo ">> [4/5] Native helper libraries"
cp "$ROOT/PitchTracker/libPitchTracker.dll.dylib" "$DIST/"
cp "$ROOT/Vocaluxe/Lib/Video/Acinerella/libacinerella.dylib" "$DIST/"

echo ">> [5/5] Party-mode sources (compiled at runtime via Roslyn -> PartyModes/<Mode>/Code)"
# CParty._CompileFiles compiles these .cs at runtime; the build must place them next to the mode's
# data as <Mode>/Code (mirrors the original Windows build step).
_copy_pm_sources() {
    local src="$1" dst="$2"
    mkdir -p "$DIST/PartyModes/$dst/Code"
    cp "$ROOT/PartyModes/$src"/*.cs "$DIST/PartyModes/$dst/Code/"
}
_copy_pm_sources PartyModeChallenge Challenge
_copy_pm_sources PartyModeTicTacToe TicTacToe

# Launcher that runs from the dist directory regardless of CWD. Written twice under different names:
# .sh to match the Linux build, .command because that is the extension Finder opens in Terminal.
_write_launcher() {
    cat > "$1" <<'LAUNCH'
#!/usr/bin/env bash
DIR="$(cd "$(dirname "$0")/Vocaluxe" && pwd)"
if [ -x "$DIR/Vocaluxe" ]; then
    exec "$DIR/Vocaluxe" "$@"      # self-contained build
else
    exec dotnet "$DIR/Vocaluxe.dll" "$@"  # framework-dependent build
fi
LAUNCH
    chmod +x "$1"
}
_write_launcher "$DIST_ROOT/Vocaluxe.sh"
_write_launcher "$DIST_ROOT/Vocaluxe.command"

echo ">> Done."
echo "   App:       $DIST"
echo "   Launchers: $DIST_ROOT/Vocaluxe.sh (terminal), $DIST_ROOT/Vocaluxe.command (Finder)"
echo
echo "   On the first run macOS asks the *terminal* for microphone access, not Vocaluxe -- there is"
echo "   no app bundle to ask on its own. If no dialog appears and the singers' levels stay at zero,"
echo "   check System Settings > Privacy & Security > Microphone."
