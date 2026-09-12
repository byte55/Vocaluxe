# Build Vocaluxe on macOS (.NET 10)

Vocaluxe is a single cross-platform code base on **.NET 10**: the same sources
build on Windows, Linux and macOS (OpenGL + PortAudio everywhere; the old
Direct3D/DirectSound/WinForms paths were dropped in the cross-platform port,
see issue #768).

**Status: this has never been built on an actual Mac.** The macOS branches were
written and checked on Linux, as far as that is possible without the hardware:

* `dotnet publish -r osx-arm64` compiles cleanly and produces a Mach-O arm64
  binary with the macOS native assets (SkiaSharp, GLFW, PortAudio, SQLite) and
  no Linux `.so` in it,
* the platform symbols were verified per target (`osx-*` gets `LINUX;ARCH_X64;MACOS`,
  `win-*` gets `WIN`, everything else `LINUX`),
* the `Darwin` branches of the two makefiles were only evaluated dry — no Apple
  toolchain ran over them.

So expect to fix something. What breaks is worth reporting; see the last section.

## 1. Prerequisites

* **Xcode command line tools** — `xcode-select --install` (clang and make)
* **.NET 10 SDK** — `brew install --cask dotnet-sdk`, or the installer from
  https://dotnet.microsoft.com/download
* **ffmpeg** — `brew install ffmpeg`

ffmpeg is a build *and* a runtime dependency: the Acinerella wrapper links
against Homebrew's copies by absolute path, so a self-contained publish does not
bundle them. Everything else — the .NET runtime, SkiaSharp, GLFW, PortAudio,
SQLite — comes from NuGet and is copied next to the binary.

## 2. Build & run (one step)

```bash
./.build/build-macos.sh        # builds the native helpers + publishes into ./dist/Vocaluxe
./dist/Vocaluxe.command        # run it (or ./dist/Vocaluxe.sh — same launcher, two names)
```

The script checks the toolchain first and names what is missing instead of
failing halfway through. It picks the RID from `uname -m` (`osx-arm64` on Apple
Silicon, `osx-x64` on Intel); `DOTNET`, `RID` and `SELFCONTAINED` override the
defaults as in `build-linux.sh`.

## 3. Developing / manual build

```bash
make -C PitchTracker                       # -> libPitchTracker.dll.dylib
make -C Vocaluxe/Lib/Video/Acinerella      # -> libacinerella.dylib
dotnet build Vocaluxe/Vocaluxe.csproj -c Release
```

Build the **project file, not the solution**: `Vocaluxe.sln` also contains
`PitchTracker/PitchTracker.vcxproj`, a Visual Studio C++ project that imports
`$(VCTargetsPath)\Microsoft.Cpp.Default.props` — a file that only exists with
Visual Studio's C++ build tools. `dotnet build Vocaluxe.sln` therefore fails
with three `MSB4278` errors on macOS and Linux alike. Nothing is wrong with the
repository; outside Windows the native helper is built by `make`, as above.

Both makefiles copy their result into `Output/`. The game data lives there too
and is resolved relative to the executable, which is why `build-macos.sh` copies
`Output/` next to the published binaries.

The library names are not cosmetic. `[DllImport("PitchTracker.dll")]` is
resolved by .NET on Unix by adding a `lib` prefix and the platform's suffix,
which is why the file has to be `libPitchTracker.dll.dylib`; Acinerella picks
its name per platform in `CAcinerella.cs`.

## 4. What is different on macOS

* **Microphone access.** macOS gates it, and this build has no app bundle to ask
  on its own — the *terminal* running Vocaluxe gets asked instead. If no dialog
  appears and the singers' levels stay at zero, look in
  System Settings → Privacy & Security → Microphone.
* **OpenGL.** Vocaluxe asks for a 2.1 context with `ContextProfile.Any`
  (`COpenGL.cs`), because the renderer draws in immediate mode. macOS still
  provides that legacy context; Apple deprecated OpenGL in 10.14 but has not
  removed it. A core profile would not run this renderer at all.
* **No webcam.** The V4L2 backend is Linux-only, so `#if LINUX && !MACOS` leaves
  macOS with the no-op webcam. An AVFoundation backend would be the fix.
* **The direct ffmpeg backend** (`<AudioDecoder>FFmpeg</AudioDecoder>` /
  `<VideoBackend>FFmpeg</VideoBackend>` in `Config.xml`) looks for the exact
  soname its bindings were generated against — `libavformat.62.dylib`, i.e.
  ffmpeg 8 — in the Homebrew prefixes. A different major version is refused
  rather than bound at random, and both decoders fall back to Acinerella.
  `VOCALUXE_FFMPEG_PATH` puts a directory of your own first.
* **Not distributable.** No app bundle, no code signing, no notarization, and
  Acinerella's link to Homebrew is by absolute path. The build is good on the
  machine that made it.
* **A hard kill leaves the single-instance lock behind.** .NET implements the
  named mutex as a file under `/tmp/.dotnet/shm/`; after a SIGKILL the next
  start dies on the abandoned lock *before logging is up* — exit code 0 after a
  fraction of a second and not a line anywhere. `rm -rf /tmp/.dotnet/shm` clears
  it. Same as on Linux.

## 5. If it does not work

The log is the only useful source: `~/.config/Vocaluxe/Logs/Vocaluxe.log`
(macOS uses the same path as Linux). The program writes **nothing** to
stdout/stderr and exits with code 0 even when it crashes, so a quick silent exit
does not mean "fine".

Worth reporting either way: whether the window opens at all, whether the
microphones arrive as PortAudio devices under Options → Record, and whether song
playback and video decode work.
