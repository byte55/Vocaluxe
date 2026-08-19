# Web-Warteliste — Entwurf und Befunde

Neues Web-Frontend für Vocaluxe mit Songwunsch und Warteliste, gebaut auf
Branch `feature/web-queue` (abgezweigt von `feature/768-net10-crossplatform`).

> **Wie dieses Dokument zu lesen ist.** Die Abschnitte *Ziel* bis *Rechte* sind
> der ursprüngliche Entwurf und halten fest, **warum** die Dinge so entschieden
> wurden — sie werden nicht mehr nachgeführt. Was tatsächlich gebaut wurde und
> heute gilt, steht ab *Stand der Umsetzung* sowie in `CLAUDE.md`; die
> Bedienungsseite gehört ganz dorthin. Die Abschnitte zu den gefundenen Fehlern
> sind Messprotokolle und bleiben, wie sie sind.

## Ziel

Bei einem Event öffnen Gäste die URL des lokalen Servers, wählen mit einem Tap
ihr Profil, suchen einen Song, geben an mit wem sie singen, und tragen sich in
die Warteliste ein. Am Karaoke-Rechner läuft nach dem laufenden Song der
nächste Eintrag an — mit den richtigen Leuten auf den richtigen Mikrofonen.

## Ausgangslage

Der mitgelieferte Webserver ist auf diesem Branch bereits von WCF auf
ASP.NET Core/Kestrel portiert (`Vocaluxe/Base/Server/`), HTTP auf Port 3000.
Vorhanden sind Login gegen Profile, Songliste, Playlists, Rollen/Rechte und
eine Tastatur-Fernbedienung. Das Frontend (`Vocaluxe/Website/`) ist
jQuery Mobile 1.3.2 von 2013.

Ein Wunsch-, Anmelde- oder Wartelistenkonzept **existiert nicht**. Nirgends
wird ein Song mit einer Person verknüpft:

- `Vocaluxe/SongQueue/CSongQueue.cs` ist die Rundenliste der *laufenden*
  Partie, ohne Sängerbezug und über keinen Endpunkt erreichbar.
- Playlists kennen keine Person, die einen Song eingetragen hat.
- Die Party-Modi (Challenge, TicTacToe) haben null Webserver-Anbindung.

## Entscheidungen

| Frage | Entscheidung |
|---|---|
| Spielstart | **Halbautomatisch** — nächster Eintrag wird angezeigt, startet auf Tastendruck |
| Frontend | **Vanilla JS + modernes CSS**, kein Build-Schritt, kein node auf dem Rechner |
| Persistenz | Warteliste als **JSON** unter `~/.config/Vocaluxe/` |
| Profile | Gäste dürfen sich **selbst anlegen** (nur Name, kein Passwort) |

Halbautomatisch, weil der Sänger physisch ans Mikro treten muss und man
zwischendurch eingreifen können will, ohne gegen eine Automatik zu kämpfen.

## Datenmodell

```
SSongRequest {
    int       RequestId
    int       SongId
    EGameMode GameMode          // Normal oder Duet
    Guid[]    SingerProfileIds  // 1–2 Einträge
    DateTime  CreatedAt
    EState    State             // Waiting | Playing | Done | Skipped
}
```

**Die Position im `SingerProfileIds`-Array ist die Spielernummer und damit der
Mikrofonkanal.** Eintrag 0 wird Spieler 1 und singt in MIC 1 (am Mixer hart
links gepannt), Eintrag 1 wird Spieler 2 auf MIC 2 (hart rechts). Siehe
Abschnitt „Audio-Eingang" in `CLAUDE.md`. Das Frontend zeigt das auch so an.

Eigene Komponente, **nicht** `CPlaylists` erweitert: Playlists sind
persistente, benannte Sammlungen ohne Personenbezug und mit eigener
Theme-Darstellung. Die Warteliste ist flüchtig, personenbezogen und hat einen
Zustand.

## Anbindung an den Spielstart

Verifizierter Pfad (`CScreenSong._StartSong` → `CScreenNames._StartSong` →
`CScreenSing`):

```csharp
CGame.Reset();
CGame.ClearSongs();
CGame.AddSong(...);                    // siehe Eingriff 1
CGame.NumPlayers = singers.Length;
CGame.Players[i].ProfileID = ...;
CGame.Players[i].VoiceNr  = ...;       // nur bei Duetten
CGraphics.FadeTo(EScreen.Sing);        // CScreenSing.OnShow ruft CGame.Start()
```

### Eingriff 1: Songauswahl per ID

`CGame.AddSong(int absoluteIndex, …)` und `AddVisibleSong(int visibleIndex, …)`
nehmen **Indizes, keine SongIDs** — und `CSongs.VisibleSongs` hängt vom aktuell
eingestellten Kategorie- und Suchfilter am Spielrechner ab. Ein Wunsch, der als
Index gespeichert wird, startet nach einem Filterwechsel den falschen Song.

Die passende Methode existiert bereits privat als
`CSongQueue._AddSong(int songID, EGameMode)`
(`Vocaluxe/SongQueue/CSongQueue.cs:69`) und muss nur über `ISongQueue` und
`CGame` öffentlich durchgereicht werden.

### Eingriff 2: Threading

Web-Requests laufen auf Kestrel-Threads, Spielzustand darf nur der Hauptthread
anfassen. Das Muster steht schon: `CVocaluxeServer.DoTask(...)` reiht in eine
`ConcurrentQueue` ein, `ProcessServerTasks()` arbeitet sie im Game-Loop ab.
Der Kommentar dort weist darauf hin, dass eine einfache `Queue` die Game-Loop
schon mal zerlegt hat — also konsequent über `DoTask`.

### Eingriff 3: Rückweg nach dem Song

`Vocaluxe/Screens/CScreenScore.cs:476` blendet nach der Auswertung zurück zur
Songauswahl. Dort greift die Warteliste: gibt es einen nächsten Eintrag, geht
es stattdessen in die Wartelisten-Ansicht.

## API

Neue Endpunkte unter `/api/…` mit echten HTTP-Verben. Die bestehenden
Endpunkte sollten zunächst unangetastet bleiben, damit die alte Website
funktionsfähig bleibt, bis die neue steht.

> Inzwischen überholt: Die alte API wurde vollständig entfernt (`CWebservice`
> samt `/legacy`), nachdem das neue Frontend stand. Die brauchbaren Teile —
> Tastatur-Fernbedienung, Songabbruch — sind unter `/api/remote/…` und
> `/api/queue/abort-current` neu entstanden.

| Endpunkt | Zweck |
|---|---|
| `GET /api/profiles` | Liste zum Antippen |
| `POST /api/profiles` | Gast legt sich selbst an |
| `POST /api/session` | Profil wählen -> Session-GUID |
| `GET /api/songs?q=&offset=&limit=` | serverseitige Suche mit Paging |
| `GET /api/queue` | die Warteliste |
| `POST /api/queue` | eintragen: `{songId, singers[]}` |
| `DELETE /api/queue/{id}` | austragen |
| `POST /api/queue/{id}/position` | umsortieren (nur Admin) |
| `GET /api/events` | SSE für Live-Updates |

Zwei Altlasten fallen dabei mit weg: `getAllSongs` schiebt heute die komplette
Bibliothek in einem Rutsch raus (mit Paging erledigt), und das
**Session-Timeout von 120 s** (`Vocaluxe/Base/Server/SessionControl.cs`) muss
hoch — ein Gast, der sein Handy kurz weglegt, darf nicht rausfliegen.

Die handgeschriebenen Datei-Handler (`/js/{filename}`, `/css/{filename}`, …)
in `CWebservice.MapEndpoints` werden durch `UseStaticFiles` ersetzt, damit die
Ordnerstruktur des Frontends dem Server egal ist.

## Rechte

Das vorhandene Rollensystem (`UserRoles.cs`, `UserRights.cs`) trägt das:
Gäste dürfen sich eintragen und sich selbst wieder austragen, Admin darf
umsortieren, fremde Einträge löschen und überspringen.

## Stand der Umsetzung

Backend und Frontend sind gebaut und am laufenden Vocaluxe verifiziert.

### Was neu ist

| Datei | Inhalt |
|---|---|
| `Vocaluxe/Base/Server/CSongRequests.cs` | Warteliste samt JSON-Persistenz, thread-safe über `lock` |
| `Vocaluxe/Base/Server/CWebQueueApi.cs` | die `/api/`-Endpunkte inklusive SSE |
| `Vocaluxe/Website/app/` | das neue Frontend (`index.html`, `style.css`, `app.js`) |

Geändert wurden außerdem: `CVocaluxeServer` (Spielstart, Songsuche, Gastprofil,
`DoTask`-Timeout, `UseStaticFiles`), `CGame`/`ISongQueue`/`CSongQueue`
(`AddSongById`), `SessionControl` (Profil-Login, Timeout 4 h) und `CScreenScore`
(schließt den gespielten Eintrag ab). Die Fehlerbehandlung, die einen
Hauptthread-Timeout in ein HTTP 503 übersetzt, wanderte später aus `CWebservice`
in `CWebQueueApi`, als die alte API entfernt wurde.

### Verifiziert am laufenden System

Profil antippen → Song suchen → eintragen → Warteliste → starten → Song läuft
mit den richtigen Spielern → Auswertung schließt den Eintrag → Warteliste
übersteht den Neustart. Für Solo **und** Duett (`Started song request 2
(ABBA - Chiquitita) for 2 player(s)`), im Browser wie per `curl`.

Fehlerfälle geprüft: ohne Session 401, fremder Eintrag 403, unbekannter Song
404, mehr als zwei Sänger 400. Live-Updates über SSE senden bei Änderung sofort
einen neuen Frame.

### Nachgereicht

- **Zu zweit singen geht bei jedem Song**, nicht nur bei markierten Duetten.
  Vocaluxe wertet dann beide auf derselben Stimme — die Reihenfolge entscheidet
  weiterhin über die Mikrofonzuordnung.
- **Schwierigkeitsgrad pro Profil** im Tab „Ich". `CGame` liest ihn live pro
  Note (`CProfiles.GetDifficulty`, `Vocaluxe/Base/CGame.cs:321`), die Änderung
  wirkt also sofort — auch mitten im Song.

### Avatare — und warum kein Upload

Profilbilder kommen ausschließlich aus dem mitgelieferten Bestand (23 Stück in
`Profiles/Vocaluxe Avatars 2024 (Official)/`), wählbar beim Anlegen, im Tab
„Ich" und sichtbar neben jedem Namen auf dem Anmeldebildschirm. Vorher bekam
jedes selbst angelegte Profil kommentarlos den ersten Avatar der Liste.

Ausgeliefert wird über `GET /api/avatars/{id}/image`: auf 192 px skaliert, als
WebP, im Speicher gecacht und mit `max-age` versehen — 5 KB statt 58 KB pro
Bild, 143 KB statt 1,3 MB für die ganze Galerie.

Die neue API hat **keinen Upload-Pfad**. Zwei alte gab es, beide sind zu:

| Endpunkt | vorher | jetzt |
|---|---|---|
| `POST /sendPhoto` | Foto landet in der Diashow hinter dem Score-Screen | 403 |
| `POST /sendProfile` | mit leerer `ProfileId` greift die Rechteprüfung nicht — **jeder ohne Session** konnte ein Profil mit beliebigem Base64-Bild anlegen | Bild wird ignoriert, mitgelieferter Avatar wird gesetzt |

Profile lassen sich über `/sendProfile` weiterhin anlegen und bearbeiten, die
Legacy-Seite funktioniert also bis auf das Bild weiter. Beide Stellen sind im
Code kommentiert, falls eine Installation Uploads zurückholen will.

### Gefundener Absturz: Start auf einen laufenden Song

Wird ein Song gestartet, während schon einer läuft, **stirbt Vocaluxe**. Der
Sing-Screen bekommt die `CGame`-Queue unter den Füßen weggezogen, findet seinen
„aktuellen" Song nicht mehr, ruft `_FinishedSinging` und blendet damit aus
`CGraphics._FinishScreenFading` heraus erneut um — ein reentranter Fade, der mit
`NullReferenceException` endet:

```
CGraphics.Draw -> _FinishScreenFading -> CScreenSing.OnShowFinish
  -> _NextSong -> _LoadCurrentSong -> _FinishedSinging
  -> CParty.FinishedSinging -> CGraphics.FadeTo -> _FinishScreenFading  (!)
```

Der Prozess beendet sich mit Exit-Code 0, im Log steht nur eine `[Fatal]`-Zeile.
Bei einem Event passiert genau das, sobald jemand ungeduldig auf „starten"
tippt. `StartSongRequest` lehnt den Start deshalb ab, solange
`CGraphics.CurrentScreen` **oder** `NextScreen` der Sing-Screen ist — Letzteres
fängt den zweiten Tap während der Einblendung ab.

Nebenwirkung: Ein abgebrochener Song (Escape statt Durchsingen) erreicht den
Score-Screen nicht, sein Eintrag bleibt deshalb auf `Playing` stehen. Blockieren
tut das nichts — geprüft wird der echte Bildschirm, nicht der Eintragszustand,
und `MarkPlaying` schließt beim nächsten Start ohnehin auf.

### Bewusst anders als im Entwurf

**Starten darf jeder Angemeldete**, nicht nur ein Admin. Vocaluxe gibt Gästen
`EUserRights.None`, also hätte ohne vorherige Rollenvergabe *niemand* einen Song
starten können. Der Start ist hier auch kein Verwaltungsakt, sondern genau die
Bestätigung „wir stehen am Mikro". Umsortieren, Überspringen und das Löschen
fremder Einträge bleiben Admin-Sache.

**Die Beamer-Anzeige wurde zunächst nicht gebaut** (inzwischen nachgeholt, siehe
unten). Der halbautomatische Start läuft über
den Knopf im Web-Frontend, was ohne neuen Screen, ohne `EScreen`-Eintrag und
ohne Theme-XML auskommt — und den Sänger dort bedienen lässt, wo er ohnehin
hinschaut. Eine Anzeige am Beamer bleibt möglich und wäre der nächste sinnvolle
Schritt, ist aber für den Ablauf nicht nötig.

## Offen

- ~~Admin-Rolle vergeben~~ — geklärt: Rechte gelten nur für Profile **mit PIN**,
  vergeben wird die Rolle bewusst von Hand. Die Reihenfolge (erst PIN, dann
  Rolle) steht in `CLAUDE.md`.
- ~~Beamer-Anzeige~~ — erledigt: Der Score-Screen kündigt den nächsten Eintrag
  mit 30-Sekunden-Countdown an, Enter startet ihn, Hoch/Runter blättert durch
  die Wartenden. Details in `CLAUDE.md`.
- **Kein HTTPS**, keine Authentifizierung über das Profil hinaus. Das ist für
  ein Heimnetz gedacht und sollte nicht ins offene Netz.

## Befund aus dem Lasttest: der Server hängt am Renderloop

Beim ersten Test mit einem echten Browser (Chrome 151 über chrome-devtools MCP)
gegen den bestehenden Server ist der **gesamte Webserver dauerhaft blockiert**.
`curl` funktionierte vorher einwandfrei — die alte Website öffnen genügte, um
ihn festzufahren. Danach beantwortete er keinen einzigen Request mehr, auch
keinen von `curl`.

### Was gemessen wurde

- `GET /` liefert die HTML-Seite (200), **alle 20 Unterressourcen** (CSS, JS,
  Bilder) bleiben `pending` und laufen nie zu Ende.
- Der Vocaluxe-Prozess lebt, aber der Hauptthread steht in
  `poll_schedule_timeout` und verbraucht **1 CPU-Tick in 2 Sekunden**.
- Kein einziger Eintrag „A webserver task threw an exception" im Log — die
  Tasks werfen nicht, sie werden schlicht nie abgeholt.

### Warum

`CVocaluxeServer.DoTask(...)` legt einen **kalten** `Task` in die Queue und
blockiert den Request-Thread mit `task.Wait()`. Abgeholt und ausgeführt wird
er ausschließlich von `ProcessServerTasks()` — und das läuft genau einmal pro
Frame im Renderloop (`Vocaluxe/Lib/Draw/CDrawBase.cs:499`).

**Kein Frame heißt: keine Antwort, unbegrenzt lange.** Es gibt kein Timeout.
Praktisch jeder Endpunkt geht durch `DoTask`, also legt ein stehender
Renderloop den kompletten Server still. Zusätzlich blockiert jeder wartende
Request einen Threadpool-Thread; nach ~20 parallelen Requests ist der Pool
leer und selbst Endpunkte ohne `DoTask` kommen nicht mehr durch.

### Ursache, nachgemessen

Der Renderloop stand, weil das Fenster **minimiert** war: `SwapBuffers` wartet
mit VSync an auf einen Frame-Callback des Compositors, den ein minimiertes
Fenster unter Wayland nie bekommt.

Gegenprobe mit **VSync aus**: 50 von 50 Requests HTTP 200 bei minimiertem
Fenster, Hauptthread durchgehend aktiv (8–13 CPU-Ticks/s statt 0). Danach lud
Chrome die komplette alte Website fehlerfrei — 25 Requests, 23x 200 plus zwei
erwartbare 404 (`locales/en-US.json` mit Fallback auf `en.json`, `favicon.ico`).

Ein Abfangen über `WindowState == Minimized` wurde versucht und **funktioniert
nicht**: xdg-shell meldet dem Client den minimierten Zustand nicht, GLFW liefert
weiterhin `Fullscreen`. Der Versuch wurde zurückgebaut; geblieben ist eine
Warnung beim Serverstart, wenn VSync aktiv ist
(`Vocaluxe/Base/Server/CVocaluxeServer.cs`).

### Konsequenzen für die Umsetzung

1. **Die Warteliste darf nicht über `DoTask` gelesen werden.** Sie ist unsere
   eigene Datenstruktur — mit einem `lock` ist sie von jedem Thread sicher
   lesbar. Nur der tatsächliche Zugriff auf den Spielzustand (Songstart,
   `CGame.Players`) muss auf den Hauptthread marshallen. Damit funktionieren
   Songsuche, Warteliste und Anmeldung auch dann noch, wenn das Spielfenster
   gerade nicht rendert.
2. **`DoTask` braucht ein Timeout.** `task.Wait(n)` statt `task.Wait()`, bei
   Ablauf HTTP 503. Ein hängender Renderloop darf nicht den Server mitnehmen.
3. **Statische Dateien gehören nicht durch `DoTask`.** Mit `UseStaticFiles`
   (ohnehin geplant) liefert Kestrel sie direkt aus, ohne den Hauptthread.
4. **VSync bleibt aus, solange der Server benutzt wird.** Das ist derzeit die
   einzige wirksame Absicherung gegen den stehenden Renderloop, und sie hängt
   an einer Einstellung, die man in den Optionen versehentlich zurückdreht.
   Punkt 1 und 2 machen den Server davon unabhängig — bis dahin gilt: vor dem
   Event einmal nachsehen.

Punkt 1 bis 3 gehören in Phase 1. Ohne sie ist jedes Frontend unbenutzbar,
weil ein moderner Browser grundsätzlich parallel lädt.
