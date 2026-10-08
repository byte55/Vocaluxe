# Vocaluxe — lokaler Linux-Build (.NET 10)

Fork von [Vocaluxe/Vocaluxe](https://github.com/Vocaluxe/Vocaluxe), Remote ist
`byte55/Vocaluxe`. Gearbeitet wird auf **`feature/web-queue`** — abgezweigt von
`feature/768-net10-crossplatform`, dem .NET-10-Cross-Platform-Port, der weiter
die Grundlage bildet.

## Priorität

**Es muss auf diesem Rechner laufen.** Das ist der Maßstab, nicht die
Upstream-Kompatibilität. Upstream-Merges dauern zu lange, deshalb werden Fixes
hier lokal gemacht und direkt auf den Fork gepusht. Pushes sind ausdrücklich in
Ordnung.

Der Rechner ist ein dedizierter Karaoke-Rechner — Hardware und Systemumgebung
stehen in `~/CLAUDE.md`. Kurzfassung: Haswell-i5, **nur Intel-iGPU**, Ubuntu
26.04, **GNOME auf Wayland**, PipeWire.

## Bauen

Kompletter Build inklusive nativer Helfer und fertiger Distribution:

```bash
./.build/build-linux.sh    # -> dist/Vocaluxe/, Launcher dist/Vocaluxe.sh
./dist/Vocaluxe.sh
```

Für schnelle Iteration bei reinen C#-Änderungen reicht der managed Build:

```bash
dotnet build Vocaluxe/Vocaluxe.csproj -c Release
```

Der landet aber in `Vocaluxe/bin/Release/net10.0/` **ohne** Spieldaten und ohne
die `.so`-Dateien. Zum Starten von dort einmalig danebenlegen:

```bash
cp -ru Output/. Vocaluxe/bin/Release/net10.0/
```

Auf dem Desktop und im Anwendungsmenü liegt ein Starter (`vocaluxe.desktop`)
auf `dist/Vocaluxe.sh`. Nach einem Rebuild zeigt der automatisch auf die neue
Version — solange der Pfad `dist/Vocaluxe.sh` bleibt, ist nichts anzupassen.

Die nativen Helfer nur neu bauen, wenn du an C-Code angefasst hast:

```bash
make -C PitchTracker                      # -> libPitchTracker.dll.so
make -C Vocaluxe/Lib/Video/Acinerella     # -> libacinerella.so
```

Beide kopieren sich selbst nach `Output/`. `dist/` und die `.so` in `Output/`
sind gitignored.

`build-linux.sh` kennt drei Stellschrauben als Umgebungsvariablen: `DOTNET`
(Pfad zum SDK), `RID` (Vorgabe `linux-x64`) und `SELFCONTAINED` (Vorgabe `true`,
bündelt die .NET-Laufzeit mit).

Zum Weitergeben gibt es ein AppImage:

```bash
./.build/make-appimage.sh    # -> dist/Vocaluxe-x86_64.AppImage
```

Das ist **vollständig eigenständig**: .NET-Laufzeit, die eigenen nativen
Bibliotheken *und* PortAudio, FFmpeg und fontconfig samt Abhängigkeiten stecken
darin. Es läuft also auf einem Rechner, auf dem nichts davon installiert ist.
Gebraucht wird `appimagetool` (lädt sich selbst nach, braucht Netz und FUSE).

Die drei Anleitungen im Repo — [Linux](HowToBuildLinux.md),
[macOS](HowToBuildMac.md), [Windows](HowToBuildWin.md) — beschreiben dasselbe für
ein frisches System. **Achtung, der Linux-Text ist an einer Stelle veraltet:** Er
behauptet unter „Notes / current limitations", der Webserver sei abgeschaltet.
Das stimmt seit der Web-Warteliste nicht mehr.

### Abhängigkeiten

Bereits installiert; hier nur zur Vollständigkeit, falls neu aufgesetzt wird:

```bash
sudo apt install -y dotnet-sdk-10.0 build-essential \
    libavcodec-dev libavformat-dev libswscale-dev libavutil-dev \
    libswresample-dev libportaudio2 libfontconfig1
```

`dotnet-sdk-10.0` kommt aus dem Ubuntu-Archiv, kein Microsoft-Repo nötig.

## Tests

Ein Testprojekt für alles: `Tests/Tests.csproj` (NUnit 3 mit
`NUnit3TestAdapter`), mit Projektverweisen auf `Vocaluxe`, `VocaluxeLib` und
`PartyModeChallenge`.

```bash
dotnet test Tests/Tests.csproj -c Release                     # alles, 227 Tests, ~5 s
dotnet test Tests/Tests.csproj -c Release --no-build \
    --filter "FullyQualifiedName~CXmlSerializerTest"          # eine Testklasse
dotnet test Tests/Tests.csproj -c Release --no-build \
    --filter "Name=TestBasic"                                 # ein einzelner Test
```

Abgedeckt sind Audio- und Video-Decoder, Imaging, das Laden der nativen
Bibliotheken, Log und Log-Rotation, Kombinatorik, der XML-Serialisierer und der
Challenge-Party-Mode. Nichts davon braucht eine Anzeige oder eine Soundkarte,
und übersprungen wird kein Test.

**Der XML-Serialisierer testet gegen echte Dateien.** `TestRealFiles` serialisiert
unter anderem `CConfig.SConfig` und vergleicht mit
`Tests/VocaluxeLib/XML/TestFiles/SConfig.xml`. Wer ein Feld zur `Config.xml`
hinzufügt, muss diese Referenzdatei mitziehen — sonst schlägt der Test fehl, und
zwar mit genau der Zeile, die fehlt.

### CI: drei Betriebssysteme, und sie ist das Tor

`.github/workflows/ci.yml` läuft bei **jedem Push**. Je Betriebssystem erst ein
Testjob (Linux, Windows, macOS), und nur wenn der grün ist, baut der zugehörige
Paketjob das Artefakt. Die gemeinsamen Schritte stehen in der lokalen Composite
Action `.github/actions/build-test/action.yml`:

```bash
dotnet restore Tests/Tests.csproj
dotnet build   Tests/Tests.csproj -c Release --no-restore
dotnet test    Tests/Tests.csproj -c Release --no-build --logger "trx;LogFileName=test-results.trx"
```

Der Sinn der drei Betriebssysteme: Die `WIN`/`LINUX`/`MACOS`-Zweige kompilieren
sonst nur hier. Was die CI **nicht** prüft, ist alles Sichtbare und Hörbare —
GUI, Audio und Rendering laufen auf keinem Runner, und die gepackten Builds
werden nirgends gestartet. Da hier direkt auf den Fork gepusht wird, lohnt sich
`dotnet test` vorher lokal.

## Architektur, soweit für Änderungen relevant

Managed ist fast alles: **OpenTK 4** (Fenster + OpenGL, bringt GLFW als Native
mit), **SkiaSharp** (Rendering), **PortAudioSharp2** (Audio-Ausgabe),
Microsoft.Data.Sqlite, Roslyn für die zur Laufzeit kompilierten Party-Modes.

Die Party-Modes werden beim Start aus Quelltext kompiliert — das dauerte 2,4 der
4,5 Sekunden Startzeit, jedes Mal aufs Neue. Das Ergebnis liegt jetzt unter
`~/.config/Vocaluxe/PartyModeCache/`, benannt nach einem Hash über die Quellen,
die Runtime-Version und die Modul-ID von Vocaluxe. Ein neuer Build oder eine
geänderte Party-Mode-Quelle entwertet den Cache also von selbst; die zehn
neuesten Einträge bleiben liegen. Damit startet Vocaluxe in rund 1,9 Sekunden.
Wenn du am Kompilierpfad zweifelst: Ordner löschen, dann wird neu übersetzt.

Nativ und selbst zu bauen sind nur zwei Dinge:

| Bibliothek | Zweck | Quelle |
|---|---|---|
| `libPitchTracker.dll.so` | Pitch-Erkennung, Grundlage des Scorings | `PitchTracker/` |
| `libacinerella.so` | Audio- **und** Video-Decode über ffmpeg | `Vocaluxe/Lib/Video/Acinerella/` |

Der ffmpeg-6-Port von Acinerella ist an echtem Material erprobt: Songs mit
Video und Tonausgabe laufen. Damit ist auch die Layout-Fallback-Logik in
`ac_create_audio_decoder` praktisch bestätigt, nicht nur kompilierbar.

### Zweites Decode-Backend: direkt gegen ffmpeg

Seit `feature/ffmpeg-decoder` gibt es Ton und Bild wahlweise **ohne** die
C-Zwischenschicht, über [FFmpeg.AutoGen](https://www.nuget.org/packages/FFmpeg.AutoGen)
direkt gegen `libavformat`/`libavcodec`/`libswscale`/`libswresample`.
**Acinerella ist weiter der Standard**, das neue Backend wird pro Bereich
eingeschaltet — jeweils in der `Config.xml`, wirkt nach einem Neustart:

```xml
<AudioDecoder>FFmpeg</AudioDecoder>   <!-- unter <Sound>, sonst Acinerella -->
<VideoBackend>FFmpeg</VideoBackend>   <!-- unter <Video>, sonst Acinerella -->
```

`VideoBackend` ist **nicht** dasselbe wie das ältere `VideoDecoder`: letzteres
wählt die Containerklasse und heißt seit jeher „FFmpeg", lange bevor irgendetwas
davon wirklich über ffmpeg lief.

Ausgeliefert wird nichts — `CFFmpegLoader` sucht die Bibliotheken des Systems und
nimmt **nur die exakten Sonames**, gegen die die Bindings erzeugt wurden (hier
`libavformat.so.62`, passend zu Ubuntus ffmpeg 8.0.1). Eine andere Hauptversion
wird abgelehnt statt riskant gebunden. Findet er nichts, fällt beides
automatisch auf Acinerella zurück, statt stumm oder schwarz zu bleiben.
`VOCALUXE_FFMPEG_PATH` setzt einen eigenen Suchpfad an die erste Stelle.
Nebenbei landen ffmpegs eigene Meldungen jetzt im `Vocaluxe.log` statt auf
stderr, wo sie niemand sieht.

**Beim Video steckt die Falle im Pixelformat.** Acinerella fordert
`AV_PIX_FMT_RGB32` an, und das ist auf Little-Endian **BGRA** im Speicher. Ein
Backend, das RGBA schreibt, dekodiert einwandfrei, verliert kein Frame, besteht
jede Zeitmessung — und wirft blaue Gesichter auf den Beamer. Deshalb vergleicht
`CVideoDecoderTest` die tatsächlichen Bytes beider Backends.

Umgebaut wurde nur, *wer* dekodiert: `CDecoderThread` mit Ringpuffer,
Frame-Dropping und Loop-Logik ist unverändert und wird von beiden Backends
geteilt (`IVideoStreamDecoder`, sieben Methoden und eine Eigenschaft).

**Gemessen** (257ers - Holland, 212 s mp3, 1920×1080 mp4):

| | Acinerella | direkt über ffmpeg |
|---|---|---|
| CPU im Song | 65 % eines Kerns | 65 % eines Kerns |
| bis zur Auswertung | 217 s | 217 s |
| Audio dekodiert | 212,10 s, 8839 Frames | identisch |
| Videoframes | — | byte-identisch |

Ein Wert, der mich korrigiert hat: mit `thread_count = 0` (ffmpeg verteilt die
Arbeit) kostete dasselbe Video **85 %** statt 65 %. Frame-Threading kauft Latenz,
die dieser Rechner nicht braucht, und bezahlt sie mit CPU, die er nicht hat.
Beide Decoder lassen den ffmpeg-Standard deshalb in Ruhe.

### ffmpeg selbst mitliefern — geprüft, nicht umgesetzt

Ubuntus ffmpeg **mitzukopieren ist keine Option**: die fünf Bibliotheken hängen
an 96 weiteren, zusammen ~76 MB, inklusive glib, cairo, librsvg und x264.

Ein eigener Minimal-Build dagegen schon. Gemessen mit ffmpeg 8.0,
`--disable-everything` plus genau die Demuxer, Decoder und Parser für
UltraStar-Material: **8,7 MB, keine Fremdabhängigkeiten außer `libc`**, zwei
Minuten Bauzeit, Lizenz **LGPL 2.1 or later** (wir dekodieren nur, brauchen also
weder x264 noch x265 und bleiben aus dem GPL-Zweig heraus).

**`nasm` ist Pflicht.** Ohne ist der Build ohne SIMD und dekodiert 1080p rund
**dreimal langsamer**. Ist installiert (3.01).

Der Gewinn wäre Unabhängigkeit von der Distribution: Steigt Ubuntu auf ffmpeg 9,
passt `libavformat.so.62` nicht mehr und das Backend fällt auf Acinerella
zurück. Offen bleibt die Patentfrage beim Weitergeben von H.264/AAC-Decodern —
für den privaten Rechner belanglos, für eine Veröffentlichung nicht.

Ein Nebenbefund daraus: Der Pixelvergleich im Test darf **nicht** byte-genau
sein, sobald die beiden Backends auf verschiedenen ffmpeg-Builds sitzen.
swscale rundet in C anders als in SIMD — gemessen: gleicher Build byte-identisch,
anderer Build schlimmstenfalls 3 daneben, im Mittel 0,52 von 255. Der Test prüft
deshalb den Abstand (Maximum 8, Mittel 1), was einen vertauschten Rot/Blau-Kanal
weiterhin sofort auffliegen lässt, weil der das Mittel in die Dutzende treibt.

**Und genau dieser Test hat einen echten Fehler gefunden — in beiden Backends.**
swscale rechnet in Blöcken und rührt die letzten Spalten einer Breite, die keine
Blockgrenze trifft, **gar nicht an**. Bei 854 Pixeln sind das die letzten sechs.
Was dort steht, ist schlicht der vorherige Inhalt des Speichers: `av_malloc`
liefert beim ersten Mal genullte Seiten, später recycelte. Sichtbar wäre das als
**sechs Pixel breiter Streifen mit Resten des vorigen Videos am rechten Rand, ab
dem zweiten Song einer Sitzung**.

Gemessen an `257ers - Holland.mp4` (854×480): frischer Prozess null abweichende
Bytes, nach einem vorher gelaufenen Dekoder rund 5000 — mal hatte die eine Seite
Müll, mal die andere. Behoben ist es, indem der Zielpuffer nach dem Reservieren
**einmal genullt** wird, in `acinerella.c` wie in `CFFmpegStreamDecoder`.

Die Lehre für den Test selbst: Er war monatelang grün, **weil NUnit die Tests in
wechselnder Reihenfolge ausführt**. Lief der Pixelvergleich zuerst, war der
Speicher noch sauber. Ein Test, der von der Reihenfolge abhängt, verschweigt
einen Fehler, statt ihn zu melden — beim Debuggen also immer auch einzeln laufen
lassen (`--filter "Name=…"`) und mit der ganzen Fixture vergleichen.

Kein SDL2 — das taucht nur noch in Kommentaren auf.

## Lokale Fixes und wo es weh tut

Diese Fixes liegen als Commits auf dem Branch. Keiner davon ist
maschinenspezifisch: die ersten drei treffen jeden Linux-Build mit aktuellem
ffmpeg, die letzten drei jeden unter Wayland.

- **Acinerella auf ffmpeg 6+ portiert.** Die `int64_t`-Channel-Layout-Bitmaske
  ist in ffmpeg 5.1/6.0 der `AVChannelLayout`-Struct gewichen. Ubuntu 26.04
  liefert **ffmpeg 8**, der alte Code kompilierte gar nicht mehr. Wenn du an
  `acinerella.c` arbeitest: die Datei stammt aus der ffmpeg-2/4-Ära, weitere
  API-Brüche sind wahrscheinlich. `av_init_packet` ist der nächste Kandidat,
  es warnt bereits als deprecated.
- **32-Bit-Überlauf im Acinerella-Zeitstempel.** `ac_package_data.pts` war ein
  `int`, ffmpegs 64-Bit-Wert wurde per Cast daraufgestutzt. Mit der Zeitbasis, die
  ffmpeg für MP3 nutzt (1/14112000), läuft das nach
  `INT32_MAX / 14112000 = 152,175 s` über und wird negativ — die Prüfung
  `pts > 0` schlägt danach für den Rest der Datei fehl. Der Decoder meldete dann
  bis zum Songende **denselben Zeitstempel**, obwohl er weiter Audio lieferte.
  Sichtbar als: **Bild friert ein, Ton läuft weiter**, Lyrics und Video stehen,
  die Notenbewertung flackert — und nach dem Ende der MP3 läuft alles normal
  weiter. Betroffen war **jeder Song über ~2,5 Minuten**, also praktisch die
  ganze Bibliothek. Wichtig beim Debuggen: Wer nur misst, *wann* ein Song endet,
  sieht nichts — der Ton läuft ja bis zum Schluss.
- **CP1252 war nach dem Port nicht mehr verfügbar.** .NET Framework hatte die
  Windows-Codepages eingebaut, .NET liefert nur UTF-8, ASCII und UTF-16. Ohne
  registrierten Provider wirft `Encoding.GetEncoding(1252)`, und `CSongLoader`
  ruft das für jede `.txt` mit `#ENCODING:CP1252` oder `CP1250` auf. Die Ausnahme
  wird pro Song gefangen — der Song **fehlt dann einfach in der Bibliothek**, mit
  einer Zeile in `Song.log` als einzigem Hinweis. Kein Song im aktuellen Bestand
  deklariert eine Kodierung, deshalb ist es nie aufgefallen; ältere
  UltraStar-Songs tun es regelmäßig. `Program.Main` registriert jetzt
  `CodePagesEncodingProvider`. Wenn ein Song scheinbar grundlos nicht auftaucht:
  zuerst `Song.log` lesen.
- **PitchTracker mit `g++` statt `gcc` gelinkt.** Alle Objekte sind C++, `gcc`
  zieht libstdc++ nicht mit. Die `.so` hatte ~40 ungelöste Symbole und wäre
  erst beim `dlopen` zur Laufzeit gescheitert, nicht beim Build.
- **App-ID für das Fenster gesetzt** (`COpenGL.cs`). Wayland kennt **kein
  Fenster-Icon-Protokoll** — eine Anwendung kann ihr Icon nicht selbst setzen.
  Die Shell nimmt die *App-ID* des Fensters, sucht die gleichnamige
  `.desktop`-Datei und zeigt deren `Icon=`. Ohne App-ID bleibt in der Taskleiste
  ein graues Zahnrad. Gesetzt wird `vocaluxe`, passend zu
  `~/.local/share/applications/vocaluxe.desktop`; die X11-Klassennamen bekommen
  denselben Wert für den XWayland-Fall. Der Hint heißt in OpenTK
  `WindowHintString.WaylandAppID` (großes D) und muss **vor** der
  Fenstererstellung gesetzt sein.
- **GLFW-Error-Callback in `COpenGL.cs`.** OpenTK macht per Default aus *jedem*
  GLFW-Fehler eine Exception. Unter Wayland fragt die Fenstererzeugung die
  Fensterposition ab, die das Protokoll Clients bewusst nicht gibt — der Start
  starb in „Init Draw". Jetzt wird geloggt statt geworfen. **Vocaluxe läuft
  damit nativ unter Wayland, XWayland ist nicht nötig.**
- **Viewport aus der Framebuffer-Größe** (`COpenGL.cs`). GLFW meldet
  `ClientSize` in *logischen* Pixeln. Mit Desktop-Skalierung — beim Einrichten
  zu Hause hängt ein LG UltraGear 4K mit **150 %** dran; im Einsatz ist es ein
  Full-HD-Beamer ohne Skalierung, dort fällt der Fehler nicht auf — ist der Framebuffer
  3840×2160, `ClientSize` aber 2560×1440. Sichtbar als: **das Spiel füllt nur
  die untere linke Ecke**, rund zwei Drittel, Rest schwarz (OpenGL zählt von
  unten links). Viewport, Screenshot und `CopyScreen` nehmen jetzt
  `FramebufferSize`; die Mausumrechnung bleibt bei `ClientSize`, weil auch die
  Cursorposition logisch ist. Im Log steht bei Skalierung
  `Framebuffer 3840x2160 for window 2560x1440`.

Bei weiteren Wayland-Themen (Fullscreen, Maus-Grab) zuerst ins Log schauen, ob
wieder eine `FeatureUnavailable`-Meldung dahintersteckt.

## Laufzeit-Konfiguration

Liegt **nicht** im Repo, sondern unter `~/.config/Vocaluxe/`:

- `Config.xml` — u. a. die `SongFolder`-Einträge. Die Bibliothek besteht aus
  `~/UltraStar Songs` (Leerzeichen im Pfad, immer quoten) und `/mnt/usb/Songs`, siehe „Zweite
  Bibliothek: die USB-Platte". Die beiden Standard-Einträge daneben sind leer und harmlos.
  Fehlt ein Ordner, steht `Song folder not found, skipping it` im Log; früher übersprang
  Vocaluxe ihn ohne jede Meldung.
- `Logs/Vocaluxe.log` — Vocaluxes eigenes Log; die 50 letzten Läufe bleiben als
  `Vocaluxe_1.log` … `_50.log` liegen. Das Programm schreibt Fehler **nur** hierhin,
  nicht nach stdout/stderr.
- `Logs/launcher.log`, `Logs/stderr/`, `Dumps/` — was der Launcher `dist/Vocaluxe.sh`
  von außen aufzeichnet: Start/Ende jedes Laufs mit Exit-Code und Signal, alles was
  die App auf stdout/stderr schreibt (native Bibliotheken, .NET-Runtime) und .NET-
  Crash-Dumps. Details unter „Falle: Vocaluxe kann auch im laufenden Betrieb
  lautlos verschwinden".
- `Logs/Marker` — liegt nur **während** eines Laufs da. Findet der nächste Start
  einen vor, war der Lauf davor unsauber beendet; das steht dann als Warnung im Log.
- `Logs/Song.log` — Parser-Warnungen zu einzelnen Songdateien. Seit dem
  Noten-Cache stehen dort nur noch Songs, die wirklich gelesen wurden — nach dem
  ersten Start also fast nichts mehr.
- `SongInfoDB.sqlite` und `CoverDB.sqlite` — reine Zwischenspeicher. Beide dürfen
  jederzeit gelöscht werden; der nächste Start baut sie neu auf (dauert dann
  einmalig so lange wie früher jeder Start).

`Renderer` in der `Config.xml` kennt unter Linux nur `TR_CONFIG_OPENGL` und
`TR_CONFIG_SOFTWARE`. Direct3D steht im Enum hinter `#if WIN` und existiert
hier nicht.

### Zweite Bibliothek: die USB-Platte

Die Songs liegen auf zwei Datenträgern: der lokalen SSD (`~/UltraStar Songs`, 2809 Ordner) und
einer externen NTFS-Platte (1,8 TB, Partition `/dev/sdb4`, **UUID `01D37C5534494DC0`**) mit
`Songs/` (2821 Ordner), gemeinsam 5578 Songs. Die Platte gehört an **`/mnt/usb`**
(`SongFolder` ist `/mnt/usb/Songs`). Der Gerätename `sdb` ist nicht stabil, deshalb nur über
die UUID ansprechen.

**Eingerichtet am 2026-10-08**, bei jedem Boot automatisch gemountet per `/etc/fstab` (Zeile von Hand mit Root eingetragen; getestet mit `sudo mount /mnt/usb`, ein echter Neustart steht noch aus):

```
UUID=01D37C5534494DC0  /mnt/usb  ntfs3  ro,nofail,x-systemd.device-timeout=10s,uid=1000,gid=1000,iocharset=utf8  0  0
```

`ro`, weil Vocaluxe die Songs nur liest und eine unter Windows nicht sauber getrennte Platte
(Dirty-Flag) so trotzdem eingebunden wird; zum Songs-Ändern `sudo mount -o remount,rw /mnt/usb`.
`nofail` und das Timeout sorgen dafür, dass der Rechner auch ohne angesteckte Platte bootet.
**Falle beim Testen:** Steckt die Platte schon, hat GNOME sie beim Einstecken selbst **schreibbar**
nach `/run/media/bytebeat/<UUID>` gemountet; dieselbe Partition lässt sich dann nicht noch einmal
mit `ro` an `/mnt/usb` hängen (`already mounted`). Erst `sudo umount /run/media/bytebeat/<UUID>`,
dann `sudo mount /mnt/usb`. Hält ein Prozess etwas darauf offen, scheitert das `umount` mit
`target is busy`; `fuser -vm <Pfad>` zeigt, wer. Nicht mit `lsof +D` suchen: das läuft rekursiv
über die ganze 1,8-TB-Platte und hält selbst ein Verzeichnis offen. Beim Booten kommt der
`fstab`-Mount vor dem Login, GNOME findet die Platte dann schon eingebunden vor.

**Fehlt der Mount, startet Vocaluxe ohne Meldung am Bildschirm mit der halben Bibliothek** (2807
statt 5578 Songs). Deshalb prüfen `karaoke-start` und `karaoke-status.sh` vorher alle
`SongFolder` (`~/.local/bin/karaoke-songfolders.sh`, Meldung `FEHLT:`/`LEER:`), und Vocaluxe
selbst loggt `Song folder not found, skipping it`.

### Einzelinstanz-Schutz: Mutex und `/tmp/.dotnet/shm`

Die Sperre ist ein benannter Mutex, den .NET unter Linux als Datei unter
`/tmp/.dotnet/shm/` ablegt.

**Behoben am 2026-10-08: Der Schutz galt nur innerhalb einer Sitzung.** Der Mutex hieß
`Vocaluxe-SingleInstanceMutex`; ein Name ohne Präfix ist unter Linux auf die Sitzung
(`getsid`) des Prozesses beschränkt, daher die Verzeichnisse `session<PID>` unter
`shm`. Der Starter startet Vocaluxe mit `setsid`, jeder Start bekam also eine **eigene**
Sitzung, und keiner sah den anderen: Ein zweiter Start (Desktop-Icon, während die
Instanz des Starters läuft) wurde nicht abgewiesen, zwei Vocaluxe teilten sich Fenster,
Mikrofone und Audioausgabe. Gemessen: zweite Instanz in anderer Sitzung lief weiter,
in derselben Sitzung endete sie mit Exit-Code 3. Jetzt heißt der Mutex
`Global\Vocaluxe-SingleInstanceMutex` (liegt unter `shm/global`), und die zweite Instanz
endet unabhängig von der Sitzung mit Exit-Code 3 und der Meldung „Another Instance of
Vocaluxe is already runnning!" auf stderr (also in `Logs/stderr/`).

Dazu fängt `_EnsureSingleInstance` eine `AbandonedMutexException` ab: Stirbt der Besitzer
mit `kill -9`, gehört die Sperre dem nächsten Start (gemessen: startet ohne Aufräumen,
das Log meldet den unsauberen Vorlauf).

> **Korrektur vom 2026-10-08 zur früheren Fassung dieses Abschnitts:** Dass der Start nach
> hartem Beenden still starb, lag nicht am Mutex, sondern am Crash-Marker, siehe „Falle:
> der erste Start nach einem unsauberen Ende scheitert". Liegengebliebene
> `session<PID>`-Verzeichnisse haben den nächsten Start nicht verhindert, und dass
> `rm -rf /tmp/.dotnet/shm` „half", lag daran, dass der fehlgeschlagene Start den Marker
> nebenbei gelöscht hatte. Ob die frühere Variante `global/<Hash>.server` unabhängig davon
> klemmen konnte, ist nicht nachgestellt; der Befehl schadet nicht:

```bash
rm -rf /tmp/.dotnet/shm
```

## Web-Warteliste (Songwünsche per Handy)

Gäste öffnen `http://<rechner>:3000/`, tippen ihr Profil an, suchen einen Song
und tragen sich in die Warteliste ein. Wer dran ist, drückt selbst „jetzt
starten" — Vocaluxe springt direkt in den Song, mit den richtigen Leuten auf den
richtigen Mikrofonen.

Voraussetzung ist ein aktiver Server: `<ServerActive>TR_CONFIG_ON</ServerActive>`
in der `Config.xml` (bzw. Optionen → Server), danach Vocaluxe neu starten.

Der Entwurf, die Messungen und alle Design-Entscheidungen stehen in
[`docs/web-queue.md`](docs/web-queue.md).

### Wichtige Details

- **Spieler 1 ist MIC 1.** Die Reihenfolge der Sänger in einem Eintrag ist die
  Spielernummer: wer sich einträgt, singt in Mikro 1 (am Mixer hart links), der
  ausgewählte Duettpartner in Mikro 2 (hart rechts). Passt zum Panorama-Aufbau
  im Audio-Abschnitt weiter unten.
- **Die Warteliste liegt in `~/.config/Vocaluxe/SongRequests.json`** und
  übersteht einen Neustart. Wegwerfen, wenn ein Abend zu Ende ist — Vocaluxe
  legt sie beim nächsten Eintrag neu an.
- **Gäste dürfen sich selbst anlegen.** Neue Profile landen als
  `TR_USERROLE_GUEST` in `~/.config/Vocaluxe/Profiles/` und sammeln sich dort
  über mehrere Events an; gelegentlich aufräumen.
- **Profilbilder nur aus dem mitgelieferten Bestand.** Zur Auswahl stehen die
  Avatare aus `Profiles/Vocaluxe Avatars 2024 (Official)/`, wählbar beim Anlegen
  und im Tab „Ich". Ursprünglich 23 (19 benannte + 4 Gast), seit 2026-09-18
  **63** (19 + 40 neue + 4 Gast) — die ursprünglichen sind fotorealistische
  Freisteller aus einem lizenzierten Bestand, echte Personen. Für Nachschub
  gilt das nicht mehr unbedenklich: Fotos "freier" Gesichter (auch KI-generierte)
  tragen trotz freier Bildlizenz oft weiter Persönlichkeitsrechte, die sich nicht
  einfach wegwünschen lassen — riskant für ein öffentlich gepushtes Repo. Die 40
  neuen sind deshalb **illustriert statt fotorealistisch**: Stil „Avataaars" von
  [DiceBear](https://www.dicebear.com/) (Original: Pablo Stanley,
  [avataaars.com](https://avataaars.com/)), Lizenz **„frei für private und
  kommerzielle Nutzung"** mit Namensnennung — keine reale Person, also kein
  Persönlichkeitsrechte-Thema, nur die Zuschreibung an Pablo Stanley/Avataaars
  gehört irgendwo hin (z. B. Credits-Bildschirm, noch nicht angelegt). Erzeugt
  per HTTP-API (`api.dicebear.com/10.x/avataaars/png?seed=<Name>&size=256`,
  256×256 statt 500×500 — die kostenlose API deckelt dort). Ein erster Versuch
  mit dem CC0-Stil „Open Peeps" (keine Namensnennung nötig) wurde wieder
  verworfen — optisch nicht überzeugend — und komplett durch Avataaars ersetzt,
  damit der Zuwachs stilistisch einheitlich bleibt. Bricht optisch mit dem
  Foto-Stil der ersten 23, ist dafür aber das einzige Kontingent, das sich
  risikofrei beliebig erweitern lässt: derselbe Aufruf mit neuem `seed` liefert
  einen neuen, deterministisch reproduzierbaren Avatar. **Es gibt keinen
  Upload-Weg** — die `/api`-Oberfläche bietet
  keinen an. Historisch: Zuerst wurden die beiden alten Wege abgeschaltet
  (`/sendPhoto` antwortete mit 403, `/sendProfile` ignorierte mitgeschickte
  Bilder — dort nahm der Server vorher *ohne jede Session* ein beliebiges Bild
  an); mit dem Entfernen der alten API sind sie ganz verschwunden. Der Grund:
  Hochgeladene Fotos landeten als Vollbild in der Diashow des Score-Screens,
  also auf dem Beamer. Ein Revert ist damit kein Auskommentieren mehr, sondern
  eine Neuentwicklung — die annehmende Seite (`SendProfileData`, `_AddAvatar`)
  existiert nicht mehr im Code, nur noch in der Historie.
- **Zu zweit singen geht bei jedem Song**, nicht nur bei Duetten — Vocaluxe
  wertet dann beide auf derselben Stimme.
- **Der Schwierigkeitsgrad** wird im Tab „Ich" pro Profil gesetzt und wirkt
  sofort, auch im laufenden Song.
- **Starten darf nur, wer dran ist.** Zwei Bedingungen, beide serverseitig
  geprüft: Der Eintrag muss **dir gehören** (du hast ihn erstellt oder stehst als
  Sänger drin) *und* er muss **oben in der Warteliste** stehen. Sonst kommt „Das
  ist nicht dein Song…" bzw. „Du bist noch nicht dran…". Wer die Warteliste
  verwalten darf (Admin **mit PIN**), umgeht beides — jemand muss einen Sänger
  überspringen können, der nicht auftaucht.
- **Ein Song lässt sich nicht starten, solange einer läuft.** Das ist kein
  Komfortverzicht, sondern verhindert einen Absturz (Details in
  `docs/web-queue.md`). Warten, bis die Auswertung erscheint.
- **Die alte API ist entfernt.** `CWebservice` mit `/sendProfile`, `/sendPhoto`,
  `/sendKeyEvent`, den Playlist-Endpunkten und `/legacy` gibt es nicht mehr —
  sie war ein zweiter, weiter offener Zugang zum selben Spiel. Mit ihr fielen in
  einem zweiten Durchgang auch die Reste: die 27 nur noch von ihr aufgerufenen
  Methoden in `CVocaluxeServer` (Playlists, Foto-Upload, Login über Benutzername,
  Cover als Base64), die zugehörigen DTOs in `VocaluxeStructs.cs` und die beiden
  Projekte `PhoneGap/` (Handy-Hülle um die alte Seite) und
  `WebserverInitalConfig/` (Windows-Werkzeug für Zertifikat, `netsh`-ACL und
  Firewall — auf Linux ohne Funktion), letzteres auch aus `Vocaluxe.sln`. Die
  Dateien unter `Vocaluxe/Website/` (index.html, css, img, js, locales) bleiben
  als Referenz im Repo, werden aber nicht mehr ausgeliefert und nicht mehr
  mitgebaut.
- **Fernbedienung**: `POST /api/remote/key` (Tasten ins Spiel, braucht
  `UseKeyboard`, also Admin **mit PIN**), `GET /api/remote/state` liefert den
  aktuellen Screen. Damit lässt sich das Spiel vom Handy steuern, wenn niemand
  an der Tastatur sitzt.
- **Laufenden Song abbrechen**: `POST /api/queue/abort-current` bzw. der Knopf
  auf der „Läuft gerade"-Karte (nur Admin). Blendet direkt zur Songauswahl
  zurück — nicht über simulierte Tasten, denn Escape schaltet im Sing-Screen nur
  die Pause um; es bräuchte Escape *und* Enter.
- **Live-Updates halten eine Dauerverbindung offen** (`/api/events`, Server-Sent
  Events). Beim Beenden wartet Kestrel auf laufende Anfragen — deshalb reagiert
  der Stream auf `ApplicationStopping` und der Shutdown-Timeout steht auf 2 s.
  Ohne beides hing das Beenden über das Hauptmenü 30 Sekunden, sobald auch nur
  ein Handy die Seite offen hatte.

### Gäste ohne WLAN: das Relay

Damit Gäste die Warteliste erreichen, **ohne im selben Netz zu sein**, kann sich
Vocaluxe bei einem externen Relay einwählen. Der Rechner nimmt dabei **keine**
Verbindung an — er wählt sich heraus, hängt in einem Long-Poll und führt
hereinkommende Gastanfragen gegen seinen **eigenen lokalen Webserver** aus. Damit
sind Relay-Weg und lokaler Weg derselbe Code; es gibt keine zweite Implementierung,
die auseinanderlaufen könnte. Portfreigaben und Zertifikate entfallen.

Der Server liegt in **`~/vocaluxe-relay`** (Node, ohne Abhängigkeiten, Docker +
Traefik), ein Klon von `byte55/vocaluxe-relay`. Aufbau, Protokoll und die
Sicherheitsabwägung stehen in dessen README.

**Dort wird von hier aus nichts geändert.** Das Repo dient dem Nachschlagen; der
ausgerollte Stand kommt von dort, und wenn er neuer ist, holt ein `git pull` ihn
her. Fällt bei der Fehlersuche etwas auf, das den Relay betrifft, gehört es dem
Nutzer gemeldet — geändert und ausgerollt wird es an anderer Stelle.

```xml
<RemoteRelay>TR_CONFIG_ON</RemoteRelay>
<RemoteRelayUrl>https://karaoke.example.com</RemoteRelayUrl>
<RemoteRelayToken>…</RemoteRelayToken>
```

- **Der Server lauscht nur noch auf `127.0.0.1`.** Gäste kommen ausschließlich über
  das Relay herein, und der Relay-Agent führt ihre Anfragen gegen genau diesen
  Loopback aus — im Netz muss also kein Port offen stehen. Am Veranstaltungsort ist
  die Anlage Gast in einem fremden Netz, und ein offener Port wäre dort nur ein
  Risiko. **Fällt das Relay aus, gibt es keinen Weg mehr über das WLAN**; bedient
  wird dann an der Tastatur vor dem Bildschirm. Wer den alten Zustand will, stellt
  `UseUrls` in `CVocaluxeServer.Init` auf `0.0.0.0` zurück.
- **Der Raumcode wird ausschließlich vom Relay vergeben**, sechs Ziffern. Vocaluxe
  speichert ihn nicht, sondern weist sich mit `RemoteAgentId` aus (wird beim ersten
  Start einmal erzeugt) und bekommt seinen Raum zurück. **Neustarts von Vocaluxe
  ändern den Code also nicht** — und seit dem 2026-09-12 auch Neustarts des Relays
  nicht mehr: Der Server hält die Zuordnung `RemoteAgentId` → Raumcode inzwischen
  auf der Platte. Ausgehängte QR-Codes bleiben damit über einen Abend hinweg gültig.
  Nachzulesen ist das in `~/vocaluxe-relay` (`src/store.js`), wo der ausgerollte
  Stand liegt.
- **Das QR-Popup zeigt ausschließlich den Relay-Link.** Steht keine Verbindung,
  erscheint kein QR-Code, sondern der Grund im Klartext („Noch keine Verbindung
  zum Relay…", bzw. Token abgelehnt, Adresse fehlt, Server nicht gestartet).
  Früher fiel es auf die lokale Adresse zurück, gebaut aus `Dns.GetHostName()` —
  das war schlechter als nichts: Der Rechnername löst hier **nur auf IPv6** auf,
  während der Server auf IPv4 lauscht, und im fremden Netz am Veranstaltungsort
  bedeutet er für Gästetelefone ohnehin nichts. Ein QR-Code, der ins Leere führt,
  kostet auf einer Feier mehr Zeit als ein ehrliches „nicht verbunden".
- **Die Fernbedienung ist für Gäste gesperrt** (`RELAY_BLOCKED_PREFIXES`), sie
  schiebt Tastendrücke ins Spiel und gehört nicht ins offene Internet.
- **Der Ereignisstrom wird wörtlich durchgereicht.** Die Seite liest die Warteliste
  direkt aus der Nachricht — eine Zusammenfassung statt der Nutzlast leert jedem
  Gast die Liste. Gemessen: 27 ms von der Änderung bis zur Anzeige über das Relay.
- Ein Nebenbefund: Die Revisionsmeldung darf **nicht** in der Poll-Schleife stecken.
  Die parkt fast durchgehend im Long-Poll, Änderungen kämen dann bis zu einem ganzen
  Poll-Fenster zu spät. Sie läuft in einer eigenen Aufgabe.

#### Falle: Ein Zeitablauf sieht aus wie ein Abbruch

`HttpClient` wirft bei Zeitablauf eine `TaskCanceledException`, und die **ist**
eine `OperationCanceledException` — obwohl niemand abgebrochen hat
(nachgemessen: der eigene Token steht dabei auf `IsCancellationRequested ==
false`). `CRelayAgent._Run` fing das als „wir fahren herunter" und verließ die
Schleife endgültig. Genau so sieht aber eine **gestorbene Leitung** aus: DSL-
Zwangstrennung, gewechselte LAN-Adresse, weggefallene NAT-Sitzung.

Sichtbar war das am 2026-09-12 so: Vocaluxe lief seit 25 Stunden, hatte **einen
einzigen Socket** (den eigenen Lauscher auf 3000), keine Zeile im Log seit dem
Start — und Gäste sahen trotzdem eine Warteliste. Der Relay beantwortete sie aus
seinem Zwischenspeicher. `Start()` steigt außerdem bei gesetztem `_Worker` sofort
aus, es versuchte es also nie wieder jemand.

Jetzt beendet nur ein **abgebrochener Token** die Schleife
(`catch (…) when (cancel.IsCancellationRequested)`), alles andere ist ein Fehler,
wird geloggt und mit dem vorhandenen Backoff erneut versucht. Der Raumcode bleibt
dabei gleich, weil die `RemoteAgentId` unverändert ist.

**Ob der Agent wirklich hängt, verrät nicht der Gastweg.** Eine 200er-Antwort über
den Relay kann aus dessen Zwischenspeicher kommen (erkennbar an ~40 ms
Antwortzeit). Verlässlich ist der Socket:

```bash
ss -tanp | grep Vocaluxe | grep -v LISTEN    # muss eine Verbindung nach :443 zeigen
```

#### Der Rechner reist mit: Abbrüche sind der Normalfall

Am Abend steht die Anlage in einem **fremden Netz mit anderer IP**. Ein
Verbindungsabbruch ist dort kein Zwischenfall, sondern Alltag, und zwei
Standardwerte von .NET stehen dem im Weg:

- **`HttpClient` wartet 100 s**, bevor er eine stille tote Leitung aufgibt. Der
  Poll bekommt deshalb ein eigenes Zeitlimit von **Poll-Fenster + 10 s** (das
  Fenster meldet der Relay bei der Anmeldung, Vorgabe 25 s), die Anmeldung eines
  von 20 s. Beides über `CancellationTokenSource.CreateLinkedTokenSource`, damit
  ein echter Abbruch weiter sofort wirkt.
- **Verbindungen im Pool leben unbegrenzt** — und mit ihnen die einmal aufgelöste
  Adresse. Da der Relay selbst hinter einer Leitung mit wechselnder IP hängt,
  bliebe Vocaluxe sonst auf einer toten Adresse kleben. `PooledConnectionLifetime`
  steht deshalb auf 2 Minuten, damit der Name neu aufgelöst wird.

**Gemessen** gegen einen Relay-Ersatz (Attrappe, die die Abfrage nie beantwortet
bzw. hart wegfällt):

| | |
|---|---|
| stille tote Leitung erkannt | nach 37 s (vorher: gar nicht, die Schleife starb) |
| hart weggefallener Relay erkannt | sofort |
| wieder angemeldet, **ohne Neustart** | 4 s später |
| Raumcode danach | unverändert, weil die `RemoteAgentId` gleich bleibt |

#### Falle: WLAN-Adresskonflikt (ACD) macht die Warteliste für ~19 Minuten unerreichbar

Post-Mortem vom 2026-09-18/19: Gleich beim Hochfahren für den Spielbetrieb
(22:46 Uhr) bekam der Rechner im Veranstaltungs-WLAN (`caffeebabe_Wi-Fi5`)
**von 22:46:10 bis 23:05:06 Uhr, also knapp 19 Minuten**, gar keine
brauchbare IPv4-Adresse — damit war weder die lokale Weboberfläche noch der
Relay-Weg für Gäste erreichbar.

`journalctl -u NetworkManager` zeigt die Ursache eindeutig: Der Router hat
zehnmal hintereinander dieselbe Adresse angeboten (`192.168.3.48`), und
NetworkManagers Adresskonflikt-Erkennung (ACD) hat sie jedes Mal abgelehnt
(`state changed new lease, address=192.168.3.48, acd conflict`) — ein
**anderes Gerät im selben Netz hatte zu dem Zeitpunkt bereits genau diese
Adresse**, vermutlich ein Gästehandy über die Warteliste. Eine echte
Link-Local-Fallback-Adresse (`169.254.x.x`) gab es dabei **nicht** — der
Rechner hatte in dem Fenster schlicht gar keine IPv4-Adresse, keine
„DNS-Fallback-Adresse".

Um 23:05:14 Uhr hat der Router dann eine andere Adresse aus einem **anderen
Subnetz** vergeben (`10.25.128.58` statt `192.168.3.x`), die sofort
akzeptiert wurde — danach lief es für den Rest des Abends mit
turnusmäßigen Lease-Erneuerungen alle 30 Minuten sauber durch.

**192.168.3.48 war dieselbe Adresse, die der Rechner schon am Nachmittag
desselben Tages in diesem Netz hatte** — DHCP-Clients fragen beim
Neuverbinden typischerweise ihre alte Adresse erneut an, und die war
inzwischen vergeben. Das ist ein Adresskonflikt am Router der
Veranstaltungstechnik, kein Fehler hier — aber man muss nicht erst den
automatischen Retry-Zyklus abwarten:

```bash
nmcli con down caffeebabe_Wi-Fi5 && nmcli con up caffeebabe_Wi-Fi5
```

Das erzwingt sofort eine neue DHCP-Anfrage (und damit meist eine andere,
freie Adresse), statt auf den nächsten automatischen Versuch zu warten.

### Am Bildschirm: wer als Nächstes dran ist

Nach jedem Song kündigt der **Score-Screen** den nächsten Wartenden an — Song,
Sänger und ein Countdown über 30 Sekunden. Läuft der ab, **blinkt** die Zeile
nur; es startet nichts von selbst, denn ob jemand am Mikro steht, kann nur ein
Mensch beurteilen.

| Taste | Wirkung |
|---|---|
| Enter | startet den angezeigten Eintrag mit seinen Sängern |
| Hoch / Runter | blättert durch die Wartenden, Countdown beginnt neu |
| Escape / Backspace | verlässt den Screen zur Songauswahl |
| Links / Rechts | wechselt die Runde (unverändert) |

Das Blättern ändert die Reihenfolge **nicht**: Wer übersprungen wurde, steht nach
dem nächsten Song wieder oben. Ist die Warteliste leer, wird nichts angezeigt und
Enter verlässt den Screen wie früher.

Gebaut in `Vocaluxe/Screens/CScreenScore.cs`; die Textelemente `TextNextUp*`
stehen im Theme (`ScreenScore.xml`, ScreenVersion 5).

**Die Musik im Score-Screen ist der gerade gesungene Song.** Das war nicht
selbstverständlich: Der Screen spielt, was im Vorschau-Player liegt
(`EMusicType.BackgroundPreview`), und den füllt sonst nur das Song-Menü mit
dem dort *markierten* Song. Ein Web-Start läuft am Menü vorbei, deshalb lief
nach Web-Songs der zuletzt von Hand gewählte Song. `CScreenScore.OnShowFinish`
lädt jetzt den Song der letzten Runde; im Log steht
`Score screen plays <Artist> - <Titel>`.

**Ein Song zählt erst ab 30 Sekunden als gesungen.** Wird früher abgebrochen,
geht der Eintrag zurück in die Warteliste — ein Fehlstart soll niemanden seinen
Platz kosten. Wer dagegen das ewige Outro mit Escape abkürzt, hat seinen Song
gesungen und der Eintrag ist erledigt.

### PIN: ein Profil für sich beanspruchen

Profile sind absichtlich offen — antippen genügt. Wer sein Profil für sich
haben will, setzt im Tab „Ich" eine **PIN** (4–10 Ziffern). Danach kommt ohne
sie niemand mehr rein, und beim Setzen fliegen alle anderen Sitzungen auf
diesem Profil sofort raus.

Falsche Eingaben werden langsamer: drei Versuche frei, danach 2, 4, 8 … Sekunden
bis maximal 30, gezählt pro Profil und nach einer erfolgreichen Anmeldung
zurückgesetzt. Niemand wird dauerhaft ausgesperrt.

**PIN vergessen?** Der einzige Weg zurück führt über die Profildatei: Vocaluxe
beenden, in `~/.config/Vocaluxe/Profiles/<Name>.xml` die Zeilen
`<PasswordHash>` und `<PasswordSalt>` löschen, neu starten.

### Profil-Backup

Beim Start legt Vocaluxe eine Kopie von `~/.config/Vocaluxe/Profiles/` unter
`~/.config/Vocaluxe/ProfileBackups/JJJJ-MM-TT/` an — **höchstens einmal am
Tag**, und bevor die Profile geladen werden. Gesichert wird also der Stand, den
die letzte Sitzung hinterlassen hat.

Die 30 neuesten Kopien bleiben liegen, ältere werden beim nächsten Backup
entfernt. Gelöscht wird nur, was als Datum lesbar ist — was du sonst dort
ablegst, bleibt unangetastet.

Wiederherstellen: Vocaluxe beenden, den Inhalt des gewünschten Datumsordners
zurück nach `~/.config/Vocaluxe/Profiles/` kopieren, neu starten.

### Admin werden

Adminrechte (umsortieren, überspringen, fremde Einträge löschen) werden von
Hand vergeben — es gibt bewusst keinen Weg über die Oberfläche.

**Die Reihenfolge ist wichtig, sonst manövrierst du dich in eine Sackgasse:**

1. Mit dem Profil anmelden und im Tab „Ich" **erst die PIN setzen**.
2. Vocaluxe beenden.
3. In `~/.config/Vocaluxe/Profiles/<Name>.xml` die Rolle setzen:
   `<UserRole>TR_USERROLE_ADMIN</UserRole>`
4. Vocaluxe starten.

Grund: **Rechte gelten nur für Profile mit PIN.** Ein Admin-Profil ohne PIN hat
keinerlei Rechte — sonst wäre es eine offene Tür, weil jeder den Namen antippen
und die Rechte erben könnte. Aus demselben Grund kann sich ein Admin-Profil ohne
PIN auch **selbst keine geben**; du müsstest die Rolle wieder herausnehmen, die
PIN setzen und die Rolle erneut eintragen. Eine gesetzte PIN lässt sich bei
Admin-Profilen zudem nicht mehr entfernen, nur ändern.

Mitgelieferte Profile gibt es nicht mehr: Beginner, Advanced und Expert
(`guest1.xml` bis `guest3.xml`, bisher in `Output/Profiles/`) wurden am 2026-10-08 entfernt,
sie tauchten bei Gästen nur als verwirrende Auswahl auf. Im Programmordner
`dist/Vocaluxe/Profiles/` liegen seitdem nur die Avatare; alle Profile entstehen in
`~/.config/Vocaluxe/Profiles/`. Im Code wird keines der drei Profile vorausgesetzt.

### Große Songbibliothek: was dabei passiert

Gemessen mit **2807 Songs (54 GB)** auf diesem Rechner, warmer Dateicache, eine
Instanz zur Zeit:

| | vorher | jetzt |
|---|---|---|
| Songdateien einlesen (`Read TXTs`) | 2,7 s | **0,7 s** |
| bis das Hauptmenü kommt (`Loaded Songs Full`) | 3,2 s | **1,2 s** |
| Cover laden, aus dem Cache (`Loaded Covers`) | 12,5–17,1 s | **3,3 s** |
| Speicher (RSS-Spitze) | 799 MiB | **501 MiB** |
| `CoverDB.sqlite` | 39 MB | 39 MB |
| `SongInfoDB.sqlite` | — | 0,8 MB |

Das `Loaded Covers` läuft im Hintergrund, blockiert das Menü also nicht; was man
als Ladebalken sieht, ist `Read TXTs` — die Zahl links in „Songs: X (Y geladen)".

Drei Dinge steckten dahinter:

- **Die Cover-Datenbank hatte keinen einzigen Index.** Ein Cover über seinen Pfad
  zu finden durchsuchte die ganze `Cover`-Tabelle, und seine Daten zu holen die
  ganze `CoverData` — also die 39 MB Bilddaten selbst, einmal pro Song. Bei 48
  Songs fällt das nicht auf, bei 2800 ist es fast die gesamte Ladezeit. Bestehende
  Datenbanken bekommen die Indizes beim Start, sie werden nicht neu gebaut.
- **Der Cache-Treffer lag komplett unter der Datenbanksperre**, WebP-Dekodieren
  eingeschlossen. Die vier Threads standen Schlange statt zu arbeiten.
- **Die Noten jedes Songs wurden beim Start gelesen**, obwohl die Songliste sie
  nie ansieht — siehe unten.

### Die Noten werden erst gelesen, wenn ein Song gesungen wird

Vom Notenlesen braucht die Songliste nur eine Handvoll Werte: Stimmenzahl und
-namen, `IsRap`, Medley, Preview und Short End. Die stehen jetzt pro Datei in
**`~/.config/Vocaluxe/SongInfoDB.sqlite`**, mit Änderungszeit und Größe als
Schlüssel. Die Noten selbst liest `CSongQueue._AddSong` nach — die eine Stelle,
durch die jeder Song muss, der tatsächlich gesungen wird.

Das spart 2,1 der 2,7 Sekunden und rund 300 MiB, denn über zwei Millionen
Notenobjekte lagen bis dahin ungenutzt im Speicher.

**Wenn du an `CSongLoader.ReadNotes` etwas änderst, muss `SSongInfo` mitziehen**
und `DatabaseSongInfoVersion` hochgezählt werden. Sonst verhalten sich gecachte
Songs anders als frisch gelesene, und das merkt man an einem Abend mit Gästen.
Zum Gegenprüfen gibt es einen Ende-zu-Ende-Vergleich:

```bash
rm -f ~/.config/Vocaluxe/SongInfoDB.sqlite
VOCALUXE_DUMP_SONGINFO=/tmp/gelesen.txt ./dist/Vocaluxe.sh   # baut den Cache auf
VOCALUXE_DUMP_SONGINFO=/tmp/gecacht.txt ./dist/Vocaluxe.sh   # nutzt ihn
diff /tmp/gelesen.txt /tmp/gecacht.txt                       # muss leer sein
```

Der Dump enthält alles aus den Noten Abgeleitete für jeden Song und zusätzlich
für jeden 50. die Zeilen-, Noten- und Punktzahl je Stimme, nachgeladen über
denselben Weg wie beim Singen. Geprüft mit 2807 und mit 5007 Songs: identisch.

Der Cache wird **übersprungen, wenn `SaveModifiedSongs` an ist** — wer defekte
Songdateien korrigieren lassen will, muss sie ganz lesen. Ein Fehltreffer, ein
veralteter Eintrag oder eine kaputte Cache-Datei führen alle zurück aufs
Volllesen; schlimmstenfalls ist es also so langsam wie vorher.

### Bei 5000 Songs wird die Platte zum Faktor

Gegengeprüft mit **5007 Songs** (die echte Bibliothek plus 2200 Kopien, Medien
als Symlink). Warm bleibt es gutmütig — `Read TXTs` 1,2 s, Cover 4,7 s. Aber die
Werte **schwanken stark**, je nachdem was gerade im Dateicache liegt: derselbe
Lauf kostete einmal 1,2 s und einmal 5,7 s, das Verzeichnisdurchlaufen
(`List Songs`) 0,3 s gegen 6,6 s. Bei 7,2 GB RAM reicht der Cache für 5000
Songordner nicht mehr zuverlässig.

Der Grund: Der **Header** jeder Datei wird weiterhin gelesen, das sind 5000
Dateiöffnungen. Wer das auch noch loswerden will, müsste die Header mitcachen —
dann bliebe nur das Verzeichnis-Listing plus ein `stat` je Datei. Gemessen wäre
dort noch rund eine Sekunde zu holen, im kalten Fall mehr.

**Die ältere Falle war der Speicher.** Cover liegen als unkomprimierte
Texturen im RAM. Bei der eingestellten Größe von 512 px ist das **1 MB pro Song** —
2807 Songs sind 2,9 GB, auf einem Rechner mit 7,2 GB und einer iGPU, die sich
denselben Speicher teilt. Der Kernel hat Vocaluxe beim Cover-Laden abgeschossen
(im Journal als `oom-kill`, GNOME meldete „device memory is nearly full"); im
`Vocaluxe.log` steht als letzte Zeile nur `Started "Loaded Covers"`.

Deshalb **richtet sich die Covergröße jetzt nach der Bibliotheksgröße**
(`CConfig.GetCoverSize()`): alle Cover zusammen dürfen 512 MB belegen, bei 2807
Songs sind das 218 px statt 512. Kleine Bibliotheken behalten die volle Größe. Was
verloren geht, ist Schärfe auf der Kachelansicht — der Rechner startet dafür.

Zwei weitere Stellen waren bei 48 Songs unsichtbar und bei 2800 fatal:

- **`CTextureProvider._CheckQueue` leerte die Texturwarteschlange komplett, in
  jedem Frame**, und hielt dabei ihre Sperre. Ein Frame musste also tausende
  Uploads erledigen — das Bild steht, Navigieren wirkt kaputt. Jetzt höchstens
  4 ms je Frame.
- **Die Cover wurden mit einer Task pro Song geladen**, alle gleichzeitig
  eingereiht. Jetzt auf die Kernanzahl begrenzt, wie das Einlesen der Songdateien.

**Cover liegen als WebP in der Datenbank**, nicht mehr roh: 39 MB statt 2214 MB für
denselben Bestand, also 57× weniger — und entsprechend weniger Plattenverkehr beim
Start. `DatabaseCoverVersion` steht deshalb auf 2; eine ältere Cover-Datenbank wird
**verworfen und neu aufgebaut** (samt `VACUUM`, sonst bliebe die Datei groß) statt
wie früher eine `NotImplementedException` zu werfen, die den Start verhindert hätte.

Mit der **echten** zweiten Bibliothek (die NTFS-Platte über USB, zusammen 5578 Songs,
gemessen 2026-09-18/19): `Read TXTs` **57 s** beim ersten Start, danach **25–29 s** bei warmem
Dateicache — gegen 2,7 s für die 2807 lokalen Songs. Bestimmend ist der Dateicache des Kernels
und der NTFS-Treiber, nicht der `SongInfoDB`-Cache (der hält nur die aus den Noten abgeleiteten
Werte, der Header jeder Datei wird bei jedem Start gelesen). Das Hauptmenü kommt je nach Cache erst
nach gut einer halben Minute bis einer Minute; die Cover-Größe sinkt dabei auf 155 px, das Working
Set liegt im Hauptmenü bei rund 960 MB.

### Falle: der erste Start nach einem unsauberen Ende scheitert

**Behoben am 2026-10-08.** Symptom, mehrfach beobachtet: Der Start endet sofort mit
Exit-Code 0 — kein Fenster, keine Ausgabe, **kein** Log-Eintrag. Der zweite Start
läuft normal.

Ursache: `CLog.Init` legt beim Start die Datei `Logs/Marker` an, `CLog.Close()`
löscht sie beim sauberen Beenden. Findet der nächste Start einen Marker vor, rief er
den Reporter-Delegate `_ShowReporterFunc` auf — der ist seit dem Cross-Platform-Port
`null`, der Aufruf stand ohne `?.`. Die `NullReferenceException` fing
`Program._Run` ab, **bevor der Logger existierte** (also ohne Eintrag), und
`_CloseProgram()` beendete das Programm mit `Environment.Exit(0)`. Der Fehlversuch
hatte den Marker bereits gelöscht, deshalb ging der zweite Start. Nachgestellt, indem
der Marker von Hand angelegt wurde; abgesichert durch `Tests/VocaluxeLib/Log/CLogMarkerTest`.

Der Fix: `?.Invoke`, dazu eine **Warnung im Log** („Previous run did not shut down
cleanly") mit dem Inhalt des Markers — Version, PID, Startzeit und Boot-ID des
toten Laufs. Ein stiller Fehlstart war also nie Zufall, sondern der Hinweis, dass der
Lauf davor **unsauber** geendet hat (Absturz, `kill -9`, Stromausfall). Builds vor dem
Fix verhalten sich weiter so. Der Retry im Starter `karaoke-start` bleibt als
harmloses Sicherheitsnetz stehen.

### Falle: Vocaluxe kann auch im laufenden Betrieb lautlos verschwinden

Post-Mortem vom 2026-09-18/19 (Spielbetrieb mit echten Gästen, 22:45 bis 01:08 Uhr):
Vocaluxe ist über den Abend mehrfach verschwunden — rekonstruiert über die von GNOME
vergebenen `xdg_surface`-IDs waren es 9 Prozessstarts. Die Ursache ist **ungeklärt**.

Was ausgeschlossen ist: Kernel-OOM, `systemd-oomd`-Kill, Segfault, Thermal-Throttling.
Was **nicht** ausgeschlossen ist, obwohl es zuerst so aussah: SIGABRT und jeder
Exit-Code. Das Journal sieht beides bei einem gewöhnlichen Benutzerprozess nicht, und
`apport` ignoriert Binaries ohne Paket. „Kein Signal im Journal" beweist deshalb nichts.

Ein Teil der Abende lässt sich nachträglich trennen, und zwar über den Marker (siehe
oben): Scheiterte der erste Start nach dem Verschwinden, war das Ende **unsauber**;
startete er sofort, war es **sauber** (Fenster geschlossen, Beenden im Menü). Beides
kam in der Nacht vor — die „Abstürze" waren also eine Mischung.

**Was seit 2026-10-08 aufgezeichnet wird.** Der Launcher `dist/Vocaluxe.sh`
(Quelle: `.build/vocaluxe-launcher.sh`, wird von `build-linux.sh` installiert) läuft
nicht mehr per `exec`, sondern als Elternprozess und hält fest:

| Wo | Was |
|---|---|
| `Logs/launcher.log` und `journalctl -t vocaluxe` | Start und Ende jedes Laufs: PID, Laufzeit, Exit-Code, Signalname, ggf. Dump-Dateien |
| `Logs/stderr/vocaluxe-<Zeit>.log` | alles auf stdout/stderr, die letzten 50 Läufe |
| `Dumps/` | .NET-Crash-Dump (~0,9 GB) plus `.crashreport.json` mit Stacktraces, die letzten 3 |
| `Logs/Vocaluxe.log` | Warnung „Previous run did not shut down cleanly" mit PID des toten Laufs |

Exit-Codes lesen: **0** = die App ging ihren eigenen Beenden-Pfad (Menü oder
Fenster-Close, siehe unten welcher); **1** unbehandelte Exception; **2** Startfehler;
**3** zweite Instanz; **129/130/131/143** SIGHUP/SIGINT/SIGQUIT/SIGTERM, von der App
selbst behandelt; **134** SIGABRT (.NET-Fail-Fast); **139** SIGSEGV (Absturz in nativem
Code); **137** SIGKILL (`kill -9`, OOM-Killer, oder der Selbstkill-Notausgang).

Nach einem Vorfall zuerst, **bevor** neu gestartet wird (neue Starts rollen die Logs):

```bash
tail ~/.config/Vocaluxe/Logs/launcher.log      # Exit-Code/Signal des letzten Laufs
ls -t ~/.config/Vocaluxe/Dumps/ | head         # gab es einen Dump?
grep -h "Exit requested\|Shutdown complete\|did not shut down" ~/.config/Vocaluxe/Logs/Vocaluxe*.log
```

**Warum Vocaluxe endet, steht seit 2026-10-08 im Log** (`Base/CExit.cs`). Jeder Weg ins
Beenden meldet sich dort, der erste gewinnt:

| Zeile im Log | Bedeutung |
|---|---|
| `Exit requested: MenuExit` | Exit-Button im Hauptmenü; das Detail sagt Tastatur oder Maus (Enter kann auch von der Web-Fernbedienung kommen, siehe `lastWebRemoteKey`) |
| `Exit requested: WindowClose` | das Fenstersystem hat das Fenster zu schließen verlangt: **Alt+F4, Schließen-Knopf, „Beenden" im Dock, Logout** — unter GNOME/Wayland verschluckt der Compositor Alt+F4 und schickt nur `xdg_toplevel.close`, die vier sind für Vocaluxe nicht zu unterscheiden. Der Kontext hilft: `lastKey`, `recentInput` (mit Alt+…), `focused` |
| `Exit requested: Signal` | SIGTERM/SIGINT/SIGHUP/SIGQUIT; Vocaluxe fährt dann **sauber** herunter (Exit-Code 128+n) |
| `Exit requested: StartupFailure` / `FatalException` | Startfehler (Exit 2) bzw. unbehandelte Exception (Exit 1), mit Typ und Meldung |
| `Shutdown without a recorded reason` | die Hauptschleife endete, ohne dass sich jemand meldete — fehlender Pfad in `CExit`, bitte melden |
| `Process exit without the normal shutdown sequence` | der Prozess ging weg, ohne die eigene Abschaltung zu durchlaufen |
| `Shutdown complete` | letzte Zeile jedes sauberen Laufs; fehlt sie, war es nicht sauber |
| `Previous run did not shut down cleanly` (Start des nächsten Laufs) | der Lauf davor hat keine `Shutdown complete`-Zeile geschrieben: Absturz, `kill -9` oder Stromausfall |

Der Kontext jeder Zeile: Laufzeit, aktueller Screen, Zeit seit der letzten Taste bzw.
Mausbewegung, die letzten acht Eingaben mit Modifikatoren (`recentInput`), die letzte Taste
der Web-Fernbedienung und die **letzten zwölf rohen Fensterereignisse** (`rawWindowEvents`:
Tastenname mit Drücken/Loslassen und Fokuswechsel, wie sie das Fenster erreichten). Das Spiel
kennt nicht jede Taste und zeigt sie als `None`; `rawWindowEvents` sagt, welche es war.

**Alt+F4 an dieser Tastatur (Logitech K400 Plus), gemessen am 2026-10-08:** Alt+F4 schließt
Vocaluxe, wenn ein echtes F4 ankommt (dann `Exit requested: WindowClose`,
`windowState=Fullscreen`, im `rawWindowEvents` nur `LeftAlt down` — das F4 verbraucht der
Compositor). Alt+**Windows**+F4 dagegen kam als `LeftSuper down`, `LeftAlt down`,
`Menu down +Alt` an, also als Taste „Menu", und schließt nichts; die F-Tasten der K400 haben
eine Zweitbelegung, die mit Fn zusammenhängt. Zum Beenden gilt: Beenden-Button im Hauptmenü,
oder Alt+Fn+F4.

**Signale.** SIGTERM beendete Vocaluxe früher gar nicht (nach 12 s lief es noch, nur
`kill -9` half — und hinterließ einen unsauberen Marker). Jetzt fährt es in etwa einer
Sekunde sauber herunter. Hängt der Weg dorthin (die Hauptschleife steht), beendet sich
der Prozess nach `CExit.SignalGraceSeconds` (15 s) mit einer Fehlerzeile selbst, damit ein
Logout oder Shutdown nicht ewig wartet. Der Selbstkill-Pfad ist nur als Negativfall
getestet (ein gesunder Shutdown wird in Ruhe gelassen); einen echten Hänger konnte ich hier
nicht erzeugen, `gdb` darf wegen `ptrace_scope` nicht an einen laufenden Prozess.
SIGINT und SIGQUIT kommen nur an, weil der Launcher mit `set -m` läuft — ohne
Job-Control startet bash Hintergrundjobs mit ignoriertem SIGINT/SIGQUIT, und die App erbt das.

Per `kill -ABRT` bzw. `-SEGV` von außen geschickt, schreibt .NET zwar Dump und
Crash-Report, **bleibt danach aber stehen** (`futex_do_wait`) statt zu enden; SIGTERM
beendet es dann trotzdem. Wie sich ein echter Absturz verhält, ist nicht geprüft.

**Herzschlag und Wächter** (`Base/CHeartbeat.cs`, seit 2026-10-08). Einmal pro Minute steht
eine `Heartbeat`-Zeile im Log: Laufzeit, FPS, CPU (in % eines Kerns), Working Set, verwalteter
Heap, GC-Zähler, Threads, **offene Dateien** (`/proc/self/fd`), ob die Hauptschleife steht, und
der Spielzustand (Screen, Audio-/Videostreams, Texturen, Warteliste mit laufendem Song und
Sängern). Nach einem Absturz ist die letzte Zeile der letzte bekannte Zustand; ein Trend bei
Speicher oder offenen Dateien zeigt ein Leck lange vor dem Ende.

Gemessen im Hauptmenü mit 2807 Songs: 61 FPS, 25 % eines Kerns, **963 MB Working Set**
(davon 481 MB verwalteter Heap), 355 offene Dateien, 30 Threads. Das ist der Ausgangswert,
an dem man spätere Zeilen misst.

Die Hauptschleife meldet sich jeden Frame. Steht sie länger als 5 s, kommt
`Main loop stalled` mit dem Zustand des Hauptthreads aus dem Kernel (`state=D` mit einer
Dateisystem-`wchan` = hängende Platte oder Netzlaufwerk, `S` in `futex_*` = eine Sperre, die
keiner freigibt, `R` = beschäftigt statt blockiert) und dem letzten bekannten Spielzustand
(höchstens 5 s alt, vom Hauptthread selbst gebaut, damit der Wächter nie in Spielstrukturen
greift). Alle 30 s folgt `Main loop still stalled`, am Ende `Main loop resumed` mit der Dauer.
Das ist der Fall „Vocaluxe lebt, ist aber eingefroren" (Web-Warteliste und Bild stehen, kein
Absturz) — etwa das minimierte Fenster mit VSync, siehe unten. Der Wächter beginnt erst mit
dem ersten Frame, das Laden beim Start (bis zu einer Minute mit der USB-Platte) zählt nicht.

Hängt Vocaluxe wirklich, liefert `kill -ABRT <pid>` die Stacktraces aller Threads im
`.crashreport.json` unter `Dumps/` (der Prozess bleibt danach stehen und lässt sich mit
`kill -TERM` beenden); das ist der Weg zur Ursache, den das Log allein nicht zeigt.

Getestet ist die Logik mit synthetischer Uhr (`CHeartbeatTest`) und live: Eine per SIGSTOP
12 s eingefrorene Instanz meldete danach Stillstand (12 s) und Wiederaufnahme (13 s).

### Falle: VSync + minimiertes Fenster killt den Webserver

Jeder Endpunkt des Webservers schiebt seine Arbeit per `CVocaluxeServer.DoTask`
auf den Hauptthread, und diese Queue wird **genau einmal pro gerendertem Frame**
geleert (`CDrawBase.MainLoop` -> `ProcessServerTasks`). Steht der Renderloop,
steht der Server — ohne Timeout, für alle Clients gleichzeitig.

Genau das passiert mit **VSync an, sobald das Fenster minimiert wird**:
`SwapBuffers` wartet auf einen Frame-Callback des Compositors, den ein
minimiertes Fenster unter Wayland nie bekommt. Der Hauptthread parkt in `poll`
bei 0 % CPU, das Spiel wirkt „am Leben" (Audio-Threads laufen weiter), aber
kein einziger HTTP-Request wird mehr beantwortet — auch `curl` nicht.

**Abhilfe: VSync aus** (`<VSync>TR_CONFIG_OFF</VSync>` bzw. Optionen ->
Grafik). Gemessen: mit VSync aus 50 von 50 Requests HTTP 200 bei minimiertem
Fenster, Renderloop durchgehend aktiv. Unter Wayland kostet das praktisch
nichts, weil der Compositor ohnehin ganze Buffer zeigt; die Frame-Bremse im
MainLoop greift dann über `CConfig.CalcCycleTime()`.

Ein Abfangen über `WindowState == Minimized` funktioniert **nicht**: xdg-shell
meldet dem Client den minimierten Zustand gar nicht, GLFW liefert weiterhin
`Fullscreen`. Verifiziert durch Logging.

## Audio-Eingang (Mikrofone)

Interface: **Steinberg UR22mkII** (Yamaha, USB `0499:170f`). Meldet sich als
ALSA-Card 2 `UR22mkII`, in PipeWire als „Steinberg UR22mkII". Zwei
Mikrofoneingänge mit getrennten Vorverstärkern, zwei Ausgänge, **24 Bit**
(`S32_LE`-Container), Raten von 44,1 bis 192 kHz — hier läuft alles auf
**44100 Hz**, siehe unten.

Mikrofon: **the t.bone MB 45 II**, dynamisch, Superniere. Braucht **keine**
Phantomspeisung, der `+48V`-Schalter am UR22 bleibt aus.

### Spielertrennung erledigt die Hardware

Die beiden Eingänge sind physisch getrennte Kanäle — keine Übersprechung,
nichts zu pannen, kein Summenweg, an dem etwas schiefgehen könnte. PipeWire
legt über das ALSA-UCM-Profil sogar zwei eigene Mono-Quellen an:

Welche Nodes dabei entstehen, hängt am **Profil** des Geräts
(`wpctl set-profile <card-id> <n>`):

| Profil | Nodes | brauchbar für |
|---|---|---|
| 1 `HiFi` | zwei **Mono**-Quellen `…HiFi__Line2__source` / `…Line3__source`, Sink `…HiFi__Line1__sink` | normalen Desktop-Betrieb |
| 3 `Pro Audio` | ein 2-Kanal-Paar `…pro-input-0:capture_AUX0/AUX1`, Sink `…pro-output-0:playback_AUX0/AUX1` | alles hier |

**Das Gerät steht dauerhaft auf `pro-audio`** und wird nicht mehr
umgeschaltet. Nur dort liegen die beiden Eingänge als getrenntes Paar an,
und nur so bleibt Vocaluxes Aufnahmegerät über beide Betriebsarten dasselbe
— ein Wechsel mitten im Abend braucht damit weder einen Neustart noch einen
Griff in die Optionen.

Der frühere Wechsel zwischen `HiFi` und `Pro Audio` hatte einen Grund: Unter
`Pro Audio` lässt WirePlumber das Gerät dauerhaft geöffnet, und PortAudio
listet ein `hw:`-Gerät nur, wenn es sich zum Prüfen öffnen lässt — **das UR22
verschwand dadurch komplett aus Vocaluxes Geräteliste**. Das sah aus wie ein
defektes Interface und war nur das Profil. Hinfällig ist es, seit Vocaluxe
nicht mehr über `hw:…` hört, sondern über die PulseAudio-Host-API (siehe
unten).

**Die ALSA-Kartennummer ist nicht stabil.** Früher stand hier `hw:2,0`,
inzwischen ist das UR22 Karte 1 (`/proc/asound/cards`). Deshalb steht in
Konfigurationen nirgends mehr eine Kartennummer.

Pegel prüfen ohne Vocaluxe:

```bash
arecord -D pipewire -f S16_LE -c 2 -r 44100 -d 6 /tmp/mic.wav
```

`-D pipewire` statt `-D hw:UR22mkII,0` — dann kollidiert es nicht mit einer
laufenden Vocaluxe-Instanz, die das Gerät exklusiv offen hält. Bequemer geht es
mit `~/Desktop/messung.sh`, das eine Live-Aussteuerungsanzeige zeigt.

### Zuordnung in Vocaluxe

**Optionen → Aufnahme → „Aufnahmeeinstellungen"**. Pro Spieler werden zwei
Dinge gesetzt: **Soundkarte** (für beide dieselbe) und **Eingang**, also die
Kanalnummer.

Konkret: **„Soundkarte"** auf `karaokemics`, dann **„Spieler 1"** auf `1`
und **„Spieler 2"** auf `2`.

| Slide | Wert |
|---|---|
| Soundkarte | `karaokemics` |
| Spieler 1 | **1** — UR22 INPUT 1 |
| Spieler 2 | **2** — UR22 INPUT 2 |

**Die Slides sind nach Spieler benannt, ihr Wert ist die Kanalnummer** (oder
„Aus"). Nicht andersherum — es gibt nur *ein* Gerät-Slide für alle Spieler,
und die Kanalnummer zählt pro Gerät: INPUT 2 ist Kanal 2, nicht Kanal 4.

#### Warum ein eigenes ALSA-Gerät nötig ist

**Vocaluxe bringt seine eigene PortAudio mit, und die kennt nur ALSA.** Sie
kommt als NuGet-Paket `org.k2fsa.portaudio.runtime.linux-x64` und liegt als
`libportaudio.so` neben der Anwendung — nicht zu verwechseln mit der
System-Bibliothek `libportaudio2`, die sehr wohl eine PulseAudio-Host-API hat.
Wer mit der falschen von beiden misst, sieht eine Geräteliste, die es in
Vocaluxe nie gibt. In der Praxis stehen dort nur vier Einträge: `HDA Intel
PCH`, `sysdefault`, `pipewire` und `default`.

Damit fallen zwei naheliegende Wege aus:

- **PipeWire-Quellen wie `KaraokeVocals.monitor` erscheinen nicht.** Die
  gibt es nur über die PulseAudio-Host-API, die diese Bibliothek nicht hat.
- **`hw:1,0` erscheint auch nicht.** Im Pro-Audio-Profil hält PipeWire die
  Karte dauerhaft offen, und PortAudio listet ein `hw:`-Gerät nur, wenn es
  sich zum Prüfen öffnen lässt.

Bleibt PipeWires ALSA-Plugin. `pipewire` und `default` melden allerdings
**128 Kanäle**, und Vocaluxe öffnet den Stream mit
`channelCount = device.Channels` (`CPortAudioRecord.Start`) — also mit 128.

Deshalb steht in **`~/.asoundrc`** ein eigenes PCM mit fest zwei Kanälen, das
direkt auf den UR22-Eingang zeigt:

```
pcm.karaokemics {
    type pipewire
    capture_node "alsa_input.usb-…Steinberg_UR22mkII-00.pro-input-0"
    playback_node "-1"
    channels 2
    hint { show on  description "Karaoke Mics (UR22 INPUT 1+2)" }
}
```

Nachgemessen: erscheint in Vocaluxes Geräteliste mit **2 Kanälen**, beide
führen getrennt Signal (−77,5 und −72,7 dBFS Grundrauschen).

Das Gerät geht **an Reaper vorbei** direkt auf die Hardware-Eingänge. Es
liegt damit in beiden Betriebsarten an, Vocaluxe bekommt immer das rohe
Signal, und ein Reaper-Absturz kostet nicht die Tonhöhenerkennung.

Am Hardware-Eingang hängen also **zwei unabhängige Abnehmer** — nachgesehen
im Graphen, während beide liefen:

```
UR22 INPUT 1 ─┬─► REAPER:in1                    → Fader → Master → Anlage
              └─► alsa_capture (karaokemics)    → Vocaluxe, Bewertung
```

Daraus folgt, welcher Regler worauf wirkt. Das ist im Betrieb die wichtigste
Tabelle des ganzen Aufbaus:

| Regler | Anlage | Bewertung in Vocaluxe |
|---|---|---|
| **GAIN 1 / GAIN 2** am UR22 | ja | **ja** |
| **Fader** in Reaper | ja | nein |
| **`MicAmplify`** in Vocaluxe | nein | ja |

Heißt konkret: Wer einen Sänger in der PA leiser dreht, tut das gefahrlos am
Reaper-Fader. Wer am GAIN dreht, ändert **auch** die Bewertung — deshalb wird
der Vorverstärker einmal richtig eingestellt und danach in Ruhe gelassen.

Gemessen zur Gegenprobe: Song-Fader von 0 auf −20 dB → Master von −10,3 auf
−29,2 dB. Der Fader wirkt also voll auf den Weg zur Anlage; was Vocaluxe
hört, bleibt davon unberührt.

**`~/.asoundrc` wird beim Prozessstart gelesen.** Ändert sich die Datei,
sieht eine laufende Vocaluxe-Instanz davon nichts — erst nach einem Neustart.

**Die Automatik ist hier eine Falle.** `CConfig.AutoAssignMics`
(`Vocaluxe/Base/CConfig.cs:722`) nimmt das **erste** Aufnahmegerät, dessen
Name auf `Usb|Wireless` passt (`IgnoreCase`) und mindestens zwei Kanäle hat.
Auf „usb" passt aber auch
`alsa_output.usb-…pro-output-0.monitor` — der **Ausgangs**-Monitor, und der
steht in der PortAudio-Liste *vor* dem Eingang. Vocaluxe würde sich dann
selbst zuhören und den Songton als Gesang bewerten. Die Automatik greift nur,
wenn gar keine gültige Zuordnung existiert (`CScreenLoad.OnShow`) — genau das
passiert aber, sobald sich das konfigurierte Gerät nicht mehr findet.

Deshalb steht die Zuordnung explizit in der `Config.xml`. Sie enthält
allerdings den **PortAudio-Index** (`DeviceDriver` ist `Name + Index`,
`CPortAudioRecord.Init`), und der verschiebt sich, sobald irgendein Gerät
dazukommt oder wegfällt — ein Bluetooth-Kopfhörer reicht, ein Profilwechsel
am UR22 auch. Von Hand in die `Config.xml` geschrieben ist der Index deshalb
bestenfalls eine Vermutung.

**Und dann beißt das Speicherverhalten des Screens.** `_UpdateChannels` ruft
am Ende `_SaveMicConfig` auf — **schon beim Öffnen** des Screens, ohne dass
jemand etwas verstellt hat. `_SaveMicConfig` setzt zuerst *alle* Kanäle auf 0
und baut sie dann allein aus dem Gerät wieder auf, das gerade im Slide steht.
Findet `_GetFirstConfiguredRecordDevice` das konfigurierte Gerät nicht (weil
der Index nicht mehr passt), steht dort Gerät 0 — die interne Soundkarte —
und die Zuordnung ist im selben Moment gelöscht. Beobachtet am 2026-09-14:
`<Channel>` stand nach einem Besuch des Screens bei beiden Spielern auf `0`.

**Verlässlich wird es nur, wenn Vocaluxe die Zuordnung selbst schreibt:** im
Screen die Soundkarte wählen und die Spieler-Slides setzen, dann steht der
richtige Index drin. Wenn die Mikrofone also scheinbar grundlos tot sind,
führt der Weg immer über **Optionen → Aufnahme**.

Der Bildschirm zeigt je Spieler eine Pegelanzeige — damit lässt sich die
Zuordnung gegenprüfen: beim Singen in INPUT 1 darf sich nur der Balken von
Spieler 1 rühren. Vocaluxe warnt zusätzlich selbst mit „Momentan sind einem
Spieler zwei Mikrofone zugeordnet!".

**Mikrofonverzögerung** im selben Bildschirm gleicht die Latenz zwischen Ton und
Erkennung aus, in 20-ms-Schritten von 0 bis 500 ms
(`Vocaluxe/Screens/CScreenOptionsRecord.cs:96`). Wenn die Bewertung systematisch
zu früh oder zu spät anschlägt, wird hier justiert. Messen statt raten:
`Vocaluxe/Lib/Sound/Record/CDelayTest.cs` steckt hinter dem Delay-Test im selben
Bildschirm.

**Mikrofonverstärkung** steht im selben Bildschirm darunter: `<MicAmplify>` unter
`<Record>`, **0 bis 30 dB in 1-dB-Schritten** (0 = aus). Gedacht ist sie für den
Fall, dass sich am UR22 nicht weiter aufdrehen lässt, ohne dass es koppelt — die
analoge Verstärkung bleibt dann niedrig und der Pegel wird digital nachgezogen.

Angewandt wird sie in `CBuffer.ProcessNewBuffer`, also **vor** der
Tonhöhenerkennung und nur auf dem Erkennungsweg; der Songton bleibt unberührt.
Der Faktor ist `10^(dB/20)`, und die Rechnung **sättigt**: Ein übersteuerter Wert
klippt, statt das Vorzeichen zu drehen. Andernfalls sähe der PitchTracker bei
lauten Stellen Müll statt eines zu lauten Tons.

Erst die Vorverstärker am Gerät ausreizen, dann hier nachhelfen — digital
verstärkt wird auch das Rauschen mit.

Der Regler heißt `SelectSlideAmplify`; weil er neu im Theme steckt, steht
`ScreenOptionsRecord.xml` auf **ScreenVersion 6**. Ein älteres Theme ohne diesen
Regler wird nicht mehr geladen.

### Ausgang: Vocaluxe folgt dem Standard-Sink

**Vocaluxe hat keine Ausgabegeräte-Auswahl.** `CPortAudioStream.cs:205` öffnet
fest `PortAudio.DefaultOutputDevice`, und das ist hier `default` über die
ALSA-Host-API — also PipeWires Brücke. Wohin der Ton geht, entscheidet damit
ausschließlich der **Standard-Sink von PipeWire**. In Vocaluxe selbst gibt es
dafür nichts einzustellen, und es braucht auch keinen Codeeingriff.

**Wohin, entscheidet die Betriebsart**, und `karaoke-mode.sh` setzt es:

| Betriebsart | Standard-Sink | warum |
|---|---|---|
| `direct` | `…pro-output-0` (UR22) | Songton direkt in die Anlage |
| `reaper` | `KaraokeSong` | Songton als dritter Fader durch Reaper |

Das Setzen des Standard-Sinks allein genügt nicht: Ein **bereits laufender**
Vocaluxe-Stream bleibt sonst am alten Ziel kleben, und genau das darf beim
Umschalten mitten im Abend nicht passieren. `route_playback_to` in
`~/.local/bin/karaoke-lib.sh` hängt laufende Wiedergabe-Streams deshalb über
`pw-metadata … target.object` aktiv mit um.

Die frühere Datei `~/.config/karaoke/output-sink` wird **nicht mehr gelesen**.
Sie stammt aus der Zeit, als das UR22 zwischen `HiFi` und `Pro Audio` hin und
her geschaltet wurde und der Sink deshalb je nach Profil anders hieß.
Für die Klinke am Rechner ginge `pci-0000_00_1b.0.analog` — dort hängt aber
nichts, der PA-Weg ist der UR22-Ausgang.

Die Buchsen des Onboard-Codecs haben Steckererkennung, sichtbar über die
`Route`-Parameter des Geräts:

| Route | Buchse | Zustand |
|---|---|---|
| 1 | Line Out | `available: no` — Kabel gezogen |
| 2 | Speakers | `available: no` |
| 3 | Headphones | `available: no` |

Steht die gewünschte Buchse auf `no`, ist schlicht kein Stecker drin — dann
hilft kein Umkonfigurieren. Testton zum Gegenhören lässt sich mit `pw-play` auf
den Standard-Sink schicken.

### Abtastrate ist absichtlich festgenagelt

`~/.config/pipewire/pipewire.conf.d/10-vocaluxe-44100.conf` setzt
`default.clock.rate = 44100` und `allowed-rates = [ 44100 ]`. Das steht dort für
Vocaluxe. Wer eine DAW dazustellt, stellt deren Projekt ebenfalls auf 44,1 kHz,
sonst wird die ganze Kette hindurch unnötig resampelt.

## Reaper als Mischpult: drei Fader

Ziel ist ein Mischpult für den Abend: **Mikro 1, Mikro 2 und der Songton
getrennt regelbar**, ohne an der Anlage zu drehen. Dazu läuft alles durch
Reaper (`~/opt/REAPER`, benutzerlokal aus dem Tarball, kein Repo).

Vocaluxe bleibt davon unberührt: Es hört die Mikrofone weiterhin direkt am
UR22 und bekommt damit das rohe Signal für die Tonhöhenerkennung. Der
Umweg über Reaper betrifft nur, **was in die Anlage geht**.

### Reaper läuft über JACK, nicht über PulseAudio

Das ist die Voraussetzung für drei Kanäle. Die PulseAudio-Anbindung
(`linux_audio_mode=3`) kann nur Stereo — `linux_audio_nch_out=4` wird
ignoriert — und lief hier mit **105 ms Puffer** (`QUANT 4630` bei 44100 Hz),
für Live-Gesang unbrauchbar. `pipewire-jack` ist installiert, damit fällt
diese Grenze weg: Reaper hat **vier Ein- und vier Ausgänge**.

Vier Dinge daran sind nicht offensichtlich und haben je eine Stunde
gekostet:

- **`linux_audio_mode=0` ist JACK.** Nicht 1, nicht 2. Gemessen, indem
  Reaper selbst gefragt wurde (`GetAudioDeviceInfo("MODE")`): `0` → `JACK`,
  `2` → `Dummy Audio`, `3` → PulseAudio, `1` und `4` → gar kein Backend,
  stillschweigend. In der Ultraschall-Config-Doku steht `linux_audio_mode`
  nicht, die ist Windows-zentriert.
- **Reaper muss über `pw-jack` starten.** Das systemweite `libjack.so.0`
  gehört `libjack-jackd2-0` und sucht einen `jackd`, den es hier nicht gibt.
  PipeWires eigene libjack liegt unter
  `/usr/lib/x86_64-linux-gnu/pipewire-0.3/jack/` und wird nur über
  `LD_LIBRARY_PATH` gefunden — genau das setzt `pw-jack`. Ohne das startet
  Reaper ohne jedes Audiogerät, **ohne Fehlermeldung**.
- **Aber nicht mit `pw-jack -p <n>`.** Die Option setzt `PIPEWIRE_QUANTUM`
  und lässt `jack_client_open` mit Status `0x11`
  (`JackFailure | JackServerFailed`) scheitern — bei *jedem* Wert, geprüft
  mit 64, 128, 256, 512 und 1024. Ohne `-p` verbindet dieselbe Bibliothek
  sofort. Die Puffergröße kommt deshalb vom PipeWire-Quantum, nicht von
  Reaper.
- **Die MIDI-Geräteliste hängt am Backend.** Im JACK-Modus führt Reaper
  `reaper-midihw-linux.ini`, unter ALSA dagegen `reaper-midihw-alsa.ini`. Wer
  in der falschen Datei nachsieht, findet das Gerät schlicht nicht: Die
  ALSA-Datei kennt hier nur `virtual` und `hw:UR22mkII`, die JACK-Datei
  dagegen sämtliche PipeWire-Ports. Dieselbe Falle
  gilt im Fenster — was dort unter „MIDI Inputs" steht, kommt aus der Datei
  zum gerade aktiven Backend. Einträge mit `<not present>` sind Karteileichen
  aus früheren Sitzungen und stören nicht.

Die Einstellungen stehen in `~/.config/REAPER/reaper.ini` (Abschnitt
`[reaper]`, Reaper **überschreibt die Datei beim Beenden** — also nur
ändern, wenn es nicht läuft):

```ini
linux_audio_mode=0        ; JACK
linux_audio_nch_in=4
linux_audio_nch_out=4
audiocloseinactive=0      ; Gerät NICHT schliessen, wenn das Fenster inaktiv ist
audioclosestop=0          ; und auch nicht, wenn der Transport steht
```

Die beiden `audioclose*` sind das Gegenstück zu Cubases „Release driver in
background": ohne sie fällt der Ton weg, sobald Reaper minimiert wird.

### Signalweg

```
Betriebsart "reaper" -- alles über Reaper:

  UR22 INPUT 1 ─ capture_AUX0 ─► REAPER:in1 ─► Spur 1 "Mikro 1" ─┐
  UR22 INPUT 2 ─ capture_AUX1 ─► REAPER:in2 ─► Spur 2 "Mikro 2" ─┼─► Master
  Vocaluxe ─► KaraokeSong ─────► REAPER:in3+4 ► Spur 3 "Song" ───┘     │
                                                                       ▼
                                          out1/out2 ─► UR22 Line Out ─► PA

  Daneben, trocken und VOR dem Fader (Reserve, siehe unten):
      Spur 1 ─► out3 ─► KaraokeVocals links
      Spur 2 ─► out4 ─► KaraokeVocals rechts

Betriebsart "direct" -- Rückfallweg, ohne Reaper:

  UR22 INPUT 1/2 ─► Direktmonitoring im Gerät ─► Line Out ─► PA   (0 ms)
  Vocaluxe ──────────────────────────────────► UR22 Line Out ─► PA
```

**Der UR22-Ausgang ist der PA-Weg**, am Klinkenausgang des Rechners hängt
nichts. Das hat eine Konsequenz, die im Betrieb zählt: In der Betriebsart
`reaper` läuft *auch die Musik* durch Reaper — **stürzt Reaper ab, ist die
Anlage stumm**. Deshalb ist `direct` bewusst so gebaut, dass er ohne Reaper
funktioniert.

### Umschalten: `~/.local/bin/karaoke-mode.sh direct|reaper`

| | `direct` | `reaper` |
|---|---|---|
| Sänger hören sich | über das Gerät, **0 ms** | über den Rechner |
| Mikropegel regeln | GAIN 1 / GAIN 2 am UR22 | drei Fader in Reaper |
| Songpegel regeln | in Vocaluxe | Fader „Song" |
| MIX-Regler am UR22 | Richtung **INPUT** | ganz auf **DAW** |
| PipeWire-Quantum | frei (spart CPU) | 256 Frames = 5,8 ms |
| Reaper nötig | nein | ja |

Auf dem Desktop liegen dafür drei Starter (`karaoke-direct.desktop`,
`karaoke-reaper.desktop`, `karaoke-status.desktop`).

**Den MIX-Regler dreht kein Skript** — der ist Hardware und entscheidet, ob
die Anlage das Mikrofon direkt hört oder nur das, was vom Rechner kommt.
Jeder Moduswechsel sagt deshalb an, wohin er gehört.

Weitere Skripte, alle in `~/.local/bin`:

```bash
karaoke-status.sh              # Betriebsart, Verkabelung, die drei Pegel
karaoke-pegel.py               # Fader anzeigen
karaoke-pegel.py song -6       # Songton auf -6 dB
karaoke-pegel.py mikro1 +2     # Mikro 1 um 2 dB lauter (führendes + = relativ)
karaoke-reaper.sh              # Reaper korrekt starten (pw-jack + Projekt)
karaoke-links.py list          # PipeWire-Verbindungen zeigen
reaper-call.py <Funktion> ...  # beliebige ReaScript-Funktion aufrufen
```

Gesetzte Pegel landen sofort im Arbeitsprojekt
`~/.config/karaoke/Karaoke.RPP` und überleben damit einen Reaper-Neustart;
`karaoke-reaper.sh` öffnet genau dieses Projekt. Ohne das startet Reaper
leer und die drei Spuren fehlen.

**`karaoke-links.py` gibt es, weil `pw-link -l PORT` nicht nach `PORT`
filtert**, sondern den ganzen Graphen ausgibt. Ein darauf gebautes `grep`
meldet jede Verbindung als vorhanden und ein darauf gebautes Lösen trifft
die falschen Ports. `pw-dump` liefert die Link-Objekte dagegen eindeutig.

### Beides zusammen starten: Reaper vor Vocaluxe

Für den Spielbetrieb reicht ein Klick: **„Karaoke: Reaper + Vocaluxe
starten"** auf dem Desktop (`~/Desktop/karaoke-start.desktop`, Wrapper unter
`~/.local/bin/starter/karaoke-start`). Er macht genau das, was sonst von Hand
nötig ist:

1. `karaoke-mode.sh reaper` — startet Reaper (mit dem Arbeitsprojekt) und
   setzt das komplette Audio-Routing.
2. `karaoke-status.sh` zur Kontrolle.
3. Vocaluxe starten.

**Die Reihenfolge Reaper-vor-Vocaluxe ist zwingend, nicht nur Gewohnheit.**
`karaoke-mode.sh` stellt das PipeWire-Quantum global um und verschiebt
laufende Wiedergabe-Streams (`route_playback_to`). Läuft Vocaluxe dabei schon,
kann es sich aufhängen (beobachtet am 2026-09-15) — deshalb startet der
Wrapper Reaper/PipeWire zuerst und erst danach Vocaluxe.

Voraussetzung, die der Starter **nicht** abnimmt: UR22-`MIX`-Regler auf
`DAW`, **bevor** der Starter läuft (siehe Startreihenfolge weiter oben bzw.
`~/Pictures/karaoke-start-dark.png`). Fehlt das, bricht der Starter mit
einer entsprechenden Meldung ab, statt stumm weiterzumachen.

**Vocaluxes erster Startversuch nach einem unsauberen Ende** scheiterte früher
wortlos (Ursache und Fix: „Falle: der erste Start nach einem unsauberen Ende
scheitert"). Der Wrapper räumt weiter vor jedem Versuch `/tmp/.dotnet/shm` auf und
probiert bei einem gescheiterten ersten Versuch ein zweites Mal — seit dem Fix
Sicherheitsnetz, nicht mehr nötig. Gibt er auf, verweist er auf
`~/.config/Vocaluxe/Logs/Vocaluxe.log`.

### Das Reaper-Projekt

Angelegt von `~/.config/REAPER/Scripts/karaoke-setup.lua` (ReaScript, ohne
SWS lauffähig). Drei Spuren, alle scharfgeschaltet mit Input-Monitoring:

| Spur | Eingang | Panorama |
|---|---|---|
| 1 „Mikro 1 (Spieler 1)" | JACK in1, mono | mittig |
| 2 „Mikro 2 (Spieler 2)" | JACK in2, mono | mittig |
| 3 „Song (Vocaluxe)" | JACK in3+4, stereo | mittig |

**Mittig, nicht hart links/rechts.** Der alte Zweispur-Aufbau pannte die
Sänger hart auseinander, weil das die Kanaltrennung für Vocaluxe war. Das
erledigt jetzt der getrennte Abgriff — beide Sänger gehören auf beide Boxen.

Die Hardware-Sends auf `out3`/`out4` stehen auf **pre-FX** und speisen
`KaraokeVocals`. Sie sind **Reserve** und im Normalbetrieb ungenutzt:
Vocaluxe hört über `karaokemics` direkt an der Hardware, weil seine
PortAudio PipeWire-Quellen gar nicht sehen kann (siehe oben).

Gebraucht würden sie, wenn der PitchTracker doch das *bearbeitete* Signal
bekommen soll — etwa hinter einem Noise-Gate. Dann müsste Vocaluxe auf ein
ALSA-PCM zeigen, das `KaraokeVocals` abgreift (zweiter Eintrag in
`~/.asoundrc` nach demselben Muster, `capture_node "KaraokeVocals"`), und der
Send-Modus in `karaoke-setup.lua` von pre-FX auf post-FX wechseln
(`I_SENDMODE`: `1` → `3`).

### Effektkette

Auf den Mikrospuren liegen **Gate, Hochpass und Kompressor**, dazu ein
**Hall-Bus** als vierter Fader und ein **Limiter** auf dem Master. Für die
Bewertung ist das folgenlos — Vocaluxe hört an Reaper vorbei.

```bash
karaoke-fx.sh an|aus|status|neu     # aus = alles auf Bypass
karaoke-pegel.py hall -12           # Hallmenge zuruecknehmen
```

Einstellungen, Messwerte und die Begründungen stehen in
[`docs/effekt.md`](docs/effekt.md). Drei Dinge daraus, die leicht Zeit kosten:

- Die ganze Kette hat **0,00 ms Zusatzlatenz**. Das ist kein Zufall, sondern
  Auswahlkriterium: **ReaLimit bringt 10 ms mit** und scheidet für den
  Live-Weg aus, `JS: Event Horizon` leistet dasselbe mit null.
- **ReaEQs „High Pass"-Band filtert nicht.** Es steht in der Parameterliste,
  nimmt eine Frequenz an und zeigt sie an — ist aber deaktiviert, und über
  Parameter nicht scharf zu schalten. Im Einsatz ist deshalb
  `JS: RBJ Highpass/Lowpass Filters`.
- **`Track_GetPeakInfo` misst bei Input-Monitoring *vor* den Effekten.** Wer
  damit prüft, ob ein Plugin wirkt, misst am falschen Ende. Messpunkt ist der
  Master.

### Reaper fernsteuern: MCP-Server und Bridge

Installiert ist `twelvetake-reaper-mcp` (über `uv tool install`, Binary in
`~/.local/bin`), registriert als MCP-Server `reaper`. Er redet mit Reaper
über ein Lua-Skript, das in Reapers `defer`-Schleife läuft und eine
Datei-Mailbox unter `~/.config/REAPER/Scripts/mcp_bridge_data/` pollt:
`request_N.json` rein, `response_N.json` raus. Kein Port, kein Netz.

Damit die Bridge immer läuft, lädt sie
`~/.config/REAPER/Scripts/__startup.lua` bei jedem Reaper-Start — sonst
müsste sie jedes Mal von Hand über die Action-Liste gestartet werden.

Ohne MCP-Client geht dasselbe über `reaper-call.py`, das dieselbe Mailbox
benutzt (eigener Slot 95, damit es dem MCP-Server nicht ins Gehege kommt):

```bash
reaper-call.py CountTracks 0
reaper-call.py GetMediaTrackInfo_Value 0 D_VOL
reaper-call.py Track_GetPeakInfo 0 0        # liefert bereits dB, nicht linear
```

Nützlich beim Debuggen: `reaper <skript>.lua` von der Kommandozeile führt
das Skript in der **laufenden** Instanz aus.

### Bedienung: die Web-UI „Karaoke-Mixer"

Die Fader bedient man über eine eigene Seite im Browser — am Tablet oder am Rechner unter
`http://<adresse>:8080/` (Adresse des jeweiligen Netzes, `localhost` am Rechner selbst). Reaper
liefert sie über seine eingebaute Weboberfläche aus: `csurf_0=HTTP 0 8080 '' 'index.html' 0 ''`
in `~/.config/REAPER/reaper.ini` (Port 8080, Seite `index.html`, Benutzer und Passwort leer).

Reaper nimmt den Ordner **`~/.config/REAPER/reaper_www_root/`**, falls es ihn gibt, sonst die
Standardseite aus `~/opt/REAPER/Plugins/reaper_www_root/`. Darin liegen zwei Dateien:

- `index.html` (Titel „Karaoke-Mixer"): Fader für **Master, Mikro 1, Mikro 2 und Song** mit
  Lautstärke in dB, Panorama und Schritttasten, ausgelegt für Touch (Tablet). Fragt `TRACK/0-3`
  alle 300 ms ab und setzt mit `SET/TRACK/<n>/VOL/…` bzw. `…/PAN/…`. Die Spuren sind **fest
  nummeriert** (0 Master, 1 Mikro 1, 2 Mikro 2, 3 Song) und müssen zu `Karaoke.RPP` passen;
  der Hall (Spur 4) fehlt auf der Seite.
- `main.js`: unveränderte Hilfsdatei von Reaper (`cmp` gegen das Original gleich), die
  `index.html` einbindet.

**Beides liegt in keinem Repo von Vocaluxe.** Gesichert wird es im Repo `~/karaoke-setup`
(zusammen mit `reaper.ini`, dem Projekt, den Reaper-Skripten, der Audio-Konfiguration, den
Starter-Skripten, den Desktop-Startern und der `CLAUDE.md` des Rechners): `./backup.sh` kopiert
den Live-Stand hinein und committet, `./restore.sh` spielt ihn zurück. Wer die Seite ändert,
ruft danach `backup.sh` auf. Das Repo ist bisher **nur lokal**; ein Plattenschaden nähme beides
mit.

**Zugangsschutz gibt es keinen.** Reaper lauscht auf allen Schnittstellen (`0.0.0.0:8080`), im
Netz am Veranstaltungsort kann jeder, der die Adresse kennt, die Fader bedienen. Abhilfe wäre
ein Passwort in den Weboberflächen-Einstellungen von Reaper (Preferences → Control/OSC/web) oder
eine `ufw`-Regel nur für das Netz des Tablets, wie beim Remote-Desktop. Bewusst vertagt (2026-10-08).

### Bedienpult: abgebaut

Vom 2026-09-15 bis 2026-10-08 steuerte ein **Korg nanoKONTROL Studio** per
Bluetooth (Mackie Control) die drei Fader. Abgebaut, weil die BLE-Verbindung
im Betrieb träge war und wiederholt ausfiel — unabhängig vom in diesem
Dokument an anderer Stelle beschriebenen Vocaluxe-Absturzverhalten. Das
Mischpult läuft seitdem über **Reapers eigenes Web-UI** (siehe oben, „Karaoke-Mixer"). Die
ganze Firmware-/Pairing-/Keepalive-Fehlersuche vom Korg ist damit hinfällig
und aus dieser Doku entfernt; der USB-Bluetooth-Dongle steckt noch, wird
aber nicht mehr gebraucht.

### Fallen

1. **Der Weg zu Vocaluxe muss trocken bleiben.** Hall auf dem Signal, das in
   den PitchTracker geht, verwischt die Tonhöhen — der Nachhall ist noch der
   alte Ton, während schon der nächste gesungen wird. Effekte gehören auf den
   PA-Weg. Gate, EQ und ein moderater Kompressor sind unbedenklich.
2. **Latenz doppelt rechnen.** In der Betriebsart `reaper` verzögert der
   Umweg Mikrofon *und* Songton. Nach jedem Wechsel den Delay-Test im
   Aufnahme-Screen laufen lassen und die Mikrofonverzögerung neu setzen —
   die beiden Betriebsarten haben unterschiedliche Latenz.
3. **CPU im Auge behalten.** Vier Kerne ohne HT, Vocaluxe zieht mit Video
   schon 65 % eines Kerns. Das Quantum steht im Reaper-Modus auf 256;
   knackst es, mit `KARAOKE_QUANTUM=512 karaoke-mode.sh reaper` neu schalten.
4. **Das UR22 bleibt dauerhaft auf `pro-audio`.** Nur dort liegen die beiden
   Eingänge als getrennte Ports an. Der alte Aufbau schaltete zwischen `HiFi`
   und `Pro Audio` hin und her, damit PortAudio `hw:…` sieht — das ist
   hinfällig, seit Vocaluxe über die PulseAudio-Host-API hört.


## Offen

- **GAIN-Regler angleichen.** Am Abend 2026-09-18 waren beide Mikrofoneingänge des UR22 in
  Benutzung (die Mikrofone stecken nur an Karaoke-Abenden, sonst sind die Eingänge stumm). Eine Kanaltrennung ist nicht einzustellen, die liefert die Hardware. Beide
  GAIN-Regler gehören auf ähnliche Pegel, damit Vocaluxe die Spieler gleich bewertet.
- **Pegel final einstellen**: beim *Singen* justieren, nicht beim Sprechen —
  Sprechen ist deutlich leiser und führt zu einer zu hohen Einstellung, die
  dann beim Singen clippt. Zielbereich 60–70 % Spitze.
- **Der Reaper-Aufbau ist im Betrieb erprobt** (Abend 2026-09-18, Gäste, zwei Mikrofone, drei
  Fader). Die Ausfälle dieses Abends waren **Hardware**, nicht Software. Kein Ton aus der Anlage:
  Reaper, Routing und PipeWire-Sink waren in Ordnung (Pegel am Master, Testton auf den UR22-Sink
  lief fehlerfrei durch), **auch der PHONES-Ausgang des UR22 blieb stumm**, und nach einem
  Kabeltausch ging es wieder. Welches Kabel das war, wurde nicht festgehalten; dass selbst PHONES
  stumm war, spricht für das **USB-Kabel zum UR22** statt für das Kabel zur Anlage. Später
  „erst Aussetzer, dann ganz weg" nach dem Muster eines Wackelkontakts, und Mikrofon 2 war
  zeitweise ohne Signal am Eingang (Mikrofon und Kabel getauscht, danach ok). **Ersatzkabel für USB
  (UR22), Ausgang zur Anlage und Mikrofone gehören in die Tasche.** Offen bleibt die Latenz beim
  Selbsthören über die PA (Quantum 256 = 5,8 ms je Block, Round-Trip ungemessen).
- **Neustart-Probe für die USB-Platte:** Der `fstab`-Eintrag ist gesetzt und per Hand getestet, ob die Platte nach einem echten Boot von selbst unter `/mnt/usb` hängt, ist noch nicht geprüft (`karaoke-songfolders.sh`).
- **Das Web-UI hat keinen Zugangsschutz** (siehe „Reaper fernsteuern") — am Veranstaltungsort
  im fremden Netz entweder in Reaper ein Passwort setzen oder den Port per `ufw` auf das Netz des
  Tablets beschränken. Bewusst vertagt.
- **Mikrofonverzögerung neu einmessen**, getrennt für beide Betriebsarten.
  `<MicDelay>` steht auf 200 ms und stammt aus dem alten Aufbau; der
  Delay-Test im Aufnahme-Screen liefert den richtigen Wert.
- **Das ffmpeg-Backend im Alltag erproben.** Ton und Bild laufen im Test
  gleichauf mit Acinerella, aber ein Testlauf ist kein Abend. Umschalten wie oben
  beschrieben; fällt über mehrere Abende nichts auf, kann Acinerella weg — dann
  fallen `acinerella.c` samt Header, die P/Invoke-Schicht, der `make`-Schritt im
  Build und `libav*-dev` als Build-Abhängigkeit weg, und nativ bleibt nur noch
  der PitchTracker.
- **Erst danach** lohnt es, ffmpeg selbst mitzuliefern (Zahlen oben). Zwei
  ungeprüfte Dinge gleichzeitig auf die Bühne zu schieben, wäre der falsche Weg.
- Theme-Videos (`BG_Video.mp4`, `IntroIn/Mid/Out.mp4`) fehlen im Repo, das Log
  meldet „Expect visual problems". Rein kosmetisch.
