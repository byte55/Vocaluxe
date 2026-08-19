# Vocaluxe — lokaler Linux-Build (.NET 10)

Fork von [Vocaluxe/Vocaluxe](https://github.com/Vocaluxe/Vocaluxe), Remote ist
`byte55/Vocaluxe`. Gearbeitet wird auf **`feature/768-net10-crossplatform`**,
dem .NET-10-Cross-Platform-Port.

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

### Abhängigkeiten

Bereits installiert; hier nur zur Vollständigkeit, falls neu aufgesetzt wird:

```bash
sudo apt install -y dotnet-sdk-10.0 build-essential \
    libavcodec-dev libavformat-dev libswscale-dev libavutil-dev \
    libswresample-dev libportaudio2 libfontconfig1
```

`dotnet-sdk-10.0` kommt aus dem Ubuntu-Archiv, kein Microsoft-Repo nötig.

## Architektur, soweit für Änderungen relevant

Managed ist fast alles: **OpenTK 4** (Fenster + OpenGL, bringt GLFW als Native
mit), **SkiaSharp** (Rendering), **PortAudioSharp2** (Audio-Ausgabe),
Microsoft.Data.Sqlite, Roslyn für die zur Laufzeit kompilierten Party-Modes.

Nativ und selbst zu bauen sind nur zwei Dinge:

| Bibliothek | Zweck | Quelle |
|---|---|---|
| `libPitchTracker.dll.so` | Pitch-Erkennung, Grundlage des Scorings | `PitchTracker/` |
| `libacinerella.so` | Audio- **und** Video-Decode über ffmpeg | `Vocaluxe/Lib/Video/Acinerella/` |

Es gibt **kein zweites Decode-Backend**. Fällt Acinerella aus, gibt es weder
Ton noch Video.

Der ffmpeg-6-Port von Acinerella ist an echtem Material erprobt: Songs mit
Video und Tonausgabe laufen. Damit ist auch die Layout-Fallback-Logik in
`ac_create_audio_decoder` praktisch bestätigt, nicht nur kompilierbar.

Kein SDL2 — das taucht nur noch in Kommentaren auf.

## Lokale Fixes und wo es weh tut

Drei Fixes liegen als Commits auf dem Branch; sie sind nicht
maschinenspezifisch, sondern treffen jeden Linux-Build mit aktuellem ffmpeg:

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
- **PitchTracker mit `g++` statt `gcc` gelinkt.** Alle Objekte sind C++, `gcc`
  zieht libstdc++ nicht mit. Die `.so` hatte ~40 ungelöste Symbole und wäre
  erst beim `dlopen` zur Laufzeit gescheitert, nicht beim Build.
- **GLFW-Error-Callback in `COpenGL.cs`.** OpenTK macht per Default aus *jedem*
  GLFW-Fehler eine Exception. Unter Wayland fragt die Fenstererzeugung die
  Fensterposition ab, die das Protokoll Clients bewusst nicht gibt — der Start
  starb in „Init Draw". Jetzt wird geloggt statt geworfen. **Vocaluxe läuft
  damit nativ unter Wayland, XWayland ist nicht nötig.**

Bei weiteren Wayland-Themen (Fullscreen, Maus-Grab) zuerst ins Log schauen, ob
wieder eine `FeatureUnavailable`-Meldung dahintersteckt.

## Laufzeit-Konfiguration

Liegt **nicht** im Repo, sondern unter `~/.config/Vocaluxe/`:

- `Config.xml` — u. a. die `SongFolder`-Einträge. Die echte Bibliothek ist
  `~/UltraStar Songs` (Leerzeichen im Pfad, immer quoten). Die beiden
  Standard-Einträge daneben sind leer und harmlos.
- `Logs/Vocaluxe.log` — die einzige brauchbare Fehlerquelle. Das Programm
  schreibt **nichts** nach stdout/stderr und beendet sich bei einem Absturz
  mit Exit-Code 0. Ein stiller, schneller Exit heißt also nicht „ok".
- `Logs/Song.log` — Parser-Warnungen zu einzelnen Songdateien.

`Renderer` in der `Config.xml` kennt unter Linux nur `TR_CONFIG_OPENGL` und
`TR_CONFIG_SOFTWARE`. Direct3D steht im Enum hinter `#if WIN` und existiert
hier nicht.

### Abgebrochener Prozess blockiert den nächsten Start

Die Single-Instance-Sperre ist ein benannter Mutex, den .NET unter Linux als
Datei unter `/tmp/.dotnet/shm/` ablegt. Wird der Prozess **hart beendet**
(SIGTERM/SIGKILL, etwa durch `timeout` in einem Testskript), bleibt er als
*abandoned* zurück. Der nächste Start stirbt dann daran, **bevor das Logging
initialisiert ist**: Exit-Code 0 nach ~0,1 s, kein Log-Eintrag, und nicht einmal
die vorgesehene Meldung „Another Instance of Vocaluxe is already runnning!",
weil der reguläre Zweig gar nicht erreicht wird.

**Der genaue Pfad ist nicht stabil.** Beobachtet wurde
`/tmp/.dotnet/shm/global/<Hash>.server` — weder ein `session*`-Verzeichnis noch
ein lesbarer Name. Ein auf `session*` gemünztes Aufräumkommando greift also ins
Leere und die Sperre bleibt liegen; deshalb immer das ganze `shm`-Verzeichnis
entfernen.

Beim normalen Schließen des Fensters passiert das nicht. Falls es doch klemmt:

```bash
rm -rf /tmp/.dotnet/shm
```

Wer Vocaluxe automatisiert testet, sollte das einkalkulieren — zwei
aufeinanderfolgende `timeout`-Läufe sehen sonst wie ein sporadischer Absturz
aus.

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
- **Profilbilder nur aus dem mitgelieferten Bestand.** Zur Auswahl stehen die 23
  Avatare aus `Profiles/Vocaluxe Avatars 2024 (Official)/`, wählbar beim Anlegen
  und im Tab „Ich". **Hochladen ist bewusst abgeschaltet** — sowohl `/sendPhoto`
  (403) als auch der Bild-Teil von `/sendProfile`, der vorher *ohne jede Session*
  ein beliebiges Bild annahm. Hochgeladene Fotos landeten sonst als Vollbild in
  der Diashow des Score-Screens, also auf dem Beamer. Rückgängig zu machen an
  den beiden kommentierten Stellen in `CWebservice.cs` und
  `CVocaluxeServer.SendProfileData`.
- **Zu zweit singen geht bei jedem Song**, nicht nur bei Duetten — Vocaluxe
  wertet dann beide auf derselben Stimme.
- **Der Schwierigkeitsgrad** wird im Tab „Ich" pro Profil gesetzt und wirkt
  sofort, auch im laufenden Song.
- **Ein Song lässt sich nicht starten, solange einer läuft.** Das ist kein
  Komfortverzicht, sondern verhindert einen Absturz (Details in
  `docs/web-queue.md`). Warten, bis die Auswertung erscheint.
- **Die alte API ist entfernt.** `CWebservice` mit `/sendProfile`, `/sendPhoto`,
  `/sendKeyEvent`, den Playlist-Endpunkten und `/legacy` gibt es nicht mehr —
  sie war ein zweiter, weiter offener Zugang zum selben Spiel. Die Dateien unter
  `Vocaluxe/Website/` (index.html, css, img, js, locales) bleiben als Referenz
  im Repo, werden aber nicht mehr ausgeliefert und nicht mehr mitgebaut.
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

Die mitgelieferten Profile (Advanced, Beginner, Expert) liegen im
Programmordner unter `dist/Vocaluxe/Profiles/`, selbst angelegte in
`~/.config/Vocaluxe/Profiles/`.

### Falle: der erste Start nach einem Build scheitert oft

Mehrfach beobachtet: direkt nach `./.build/build-linux.sh` beendet sich der
erste Start sofort mit Exit-Code 0 — kein Fenster, keine Ausgabe, **kein**
Log-Eintrag, und anders als bei der Mutex-Falle liegt auch nichts in
`/tmp/.dotnet/shm`. Der zweite Start läuft dann normal. Ursache ungeklärt; wer
automatisiert testet, sollte einen Startversuch einkalkulieren statt daraus auf
einen kaputten Build zu schließen.

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

Interface: **Behringer Xenyx QX1002USB**, USB-Codec ist ein TI PCM2902
(`08bb:2902`). Meldet sich als ALSA-Card `CODEC` und in PipeWire als
„PCM2902 Audio Codec Analog Stereo". Kann **16 Bit, 48 kHz, Stereo** — mehr
nicht, das reicht aber für zwei Spieler.

Mikrofon: **the t.bone MB 45 II**, dynamisch, Superniere. Braucht **keine**
Phantomspeisung, +48 V bleibt aus.

### Falle: `USB/2-TR TO MAIN MIX` schaltet die Aufnahme stumm

Der Mixer hat zwei Taster in der Gruppe `USB/2-TR`. Der zweite,
**`TO MAIN MIX`, muss ausgerastet sein.** Aus dem Handbuch:

> USB/2-TR TO MAIN MIX button routes USB/2-Track playback to MAIN MIX and
> **mutes the 2-TR OUT/USB recording signal.**

Gedrückt verhält sich der Aufbau wie ein Defekt: Mixer arbeitet, alle Lampen
reagieren, der Kompressor zeigt Signal — und der Rechner bekommt trotzdem
digitale Stille bei −90 dBFS. Zum Mithören des Rechnertons ist der *erste*
Taster (`TO PHONES/CTRL RM`) zuständig, der den Aufnahmeweg nicht antastet.
Für Karaoke wird keiner von beiden gebraucht, der Ton kommt direkt aus dem
Rechner.

### Spielertrennung über Panorama

Der USB-Aufnahmeweg trägt die **Hauptmischung**, sein Pegel hängt am
MAIN-MIX-Fader. Beide Mikrofone landen deshalb per Default summiert auf beiden
Kanälen — für Vocaluxe unbrauchbar, beide Spieler sähen dasselbe Signal.

Trennung entsteht erst durch hartes Panning: **MIC 1 ganz nach links,
MIC 2 ganz nach rechts.** Gemessen mit MIC 1 hart links: 59 % Spitzenpegel
links gegen 0,9 % rechts, also **36 dB Kanaltrennung** — für die
Tonhöhenerkennung mehr als genug.

Pegel prüfen ohne Vocaluxe:

```bash
arecord -D pipewire -f S16_LE -c 2 -r 48000 -d 6 /tmp/mic.wav
```

`-D pipewire` statt `-D hw:CODEC,0`, dann kollidiert es nicht mit einer
laufenden Instanz — der PCM2902 lässt sich nur exklusiv öffnen. Bequemer geht
es mit `~/Desktop/messung.sh`, das eine Live-Aussteuerungsanzeige zeigt.

### Zuordnung in Vocaluxe

**Optionen → Aufnahme → „Aufnahmeeinstellungen"**. Pro Spieler werden zwei
Dinge gesetzt: **Soundkarte** (für beide dieselbe, der USB-Codec) und
**Eingang**, also die Kanalnummer.

| | Gerät | Kanal | am Mixer |
|---|---|---|---|
| Spieler 1 | USB Audio CODEC | **1** | MIC 1, PAN hart links |
| Spieler 2 | USB Audio CODEC | **2** | MIC 2, PAN hart rechts |

Die Kanalnummer zählt **pro Gerät**, nicht durchlaufend über alle Geräte —
MIC 2 ist also Kanal 2, nicht Kanal 4. Kanal 1 ist links, Kanal 2 ist rechts,
mehr hat der PCM2902 nicht.

Die Automatik trifft diesen Fall meist von selbst: `CConfig._CheckMics` sucht
ein Aufnahmegerät, dessen Name auf `Usb|Wireless` passt, und legt bei
mindestens zwei Kanälen Spieler 1 auf Kanal 1 und Spieler 2 auf Kanal 2
(`Vocaluxe/Base/CConfig.cs:668`).

Der Bildschirm zeigt je Spieler eine Pegelanzeige — damit lässt sich die
Zuordnung direkt gegenprüfen: beim Singen in MIC 1 darf sich nur der Balken
von Spieler 1 rühren. Bewegen sich beide, stimmt das Panorama am Mixer nicht.
Vocaluxe warnt zusätzlich selbst mit „Momentan sind einem Spieler zwei
Mikrofone zugeordnet!".

**Mikrofonverzögerung** im selben Bildschirm gleicht die Latenz zwischen Ton
und Erkennung aus. Wenn die Bewertung systematisch zu früh oder zu spät
anschlägt, wird hier justiert — nicht am Mixer.

## Offen

- **Zweites Mikrofon** noch nicht angeschlossen. Vorhanden ist bisher ein
  t.bone MB 45 II, damit sind beide Kanäle einzeln geprüft: MIC 1 hart links
  ergab 36 dB Trennung, MIC 2 hart rechts 41 dB. Sobald das zweite Mikrofon da
  ist, beide GAIN-Regler auf ähnliche Pegel bringen, damit Vocaluxe die Spieler
  gleich bewertet.
- **Pegel final einstellen**: beim *Singen* justieren, nicht beim Sprechen —
  Sprechen ist deutlich leiser und führt zu einer zu hohen Einstellung, die
  dann beim Singen clippt. Zielbereich 60–70 % Spitze.
- **Zuordnung in Vocaluxe** noch nicht durchgeführt, siehe Abschnitt oben.
- Theme-Videos (`BG_Video.mp4`, `IntroIn/Mid/Out.mp4`) fehlen im Repo, das Log
  meldet „Expect visual problems". Rein kosmetisch.
