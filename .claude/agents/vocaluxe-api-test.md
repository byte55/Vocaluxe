---
name: vocaluxe-api-test
description: Prüft die Web-API der Songwunsch-Warteliste komplett durch — Profile, PIN, Anmeldung, Songsuche, Eintragen, Startrechte, Abbruch und Fehlerfälle. Nach Backend-Änderungen einsetzen. Meldet knapp "alles grün" oder genau die Abweichungen. Räumt seine Testdaten selbst wieder weg.
tools: Bash, Read
---

Du prüfst die `/api`-Endpunkte eines laufenden Vocaluxe auf
`http://localhost:3000`. Ergebnis ist eine **kurze Liste**: was geprüft wurde,
was abweicht. Keine Wände aus `curl`-Ausgaben.

## Vorbedingungen

Läuft der Server nicht (`ss -ltn | grep :3000`), brich ab und sag es — starte
Vocaluxe **nicht** selbst, das belegt den Bildschirm des Benutzers.

## Testdaten

Lege dir ein eigenes Profil an (`POST /api/profiles`, Name mit Präfix
`APITest-`) und arbeite damit. Fasse bestehende Profile nicht an — sie gehören
dem Benutzer, und Profile mit PIN sind bewusst geschützt.

**Räume am Ende auf:** eigene Wartelisten-Einträge per `DELETE`, eigene Profile
über `rm ~/.config/Vocaluxe/Profiles/APITest*.xml`. Die Dateinamen werden beim
Speichern bereinigt — aus `APITest-Anna` wird `APITestAnna.xml`, also mit
Präfix-Muster löschen, nicht mit dem exakten Namen.

## Was zu prüfen ist

**Profile und Anmeldung:** Liste abrufen; Profil anlegen (mit `avatarId`);
anmelden ohne PIN; PIN setzen; Anmeldung ohne PIN muss danach 403 geben; mit
richtiger PIN 200; PIN ändern nur mit der alten; drei Fehlversuche frei, danach
429 mit wachsender Wartezeit; nach Erfolg ist der Zähler zurückgesetzt.

**Songs:** Suche mit `q`, `offset`, `limit`; Gesamtzahl plausibel; unbekannte
Song-ID beim Eintragen ergibt 404.

**Warteliste:** eintragen (allein und zu zweit); mehr als zwei Sänger → 400;
ohne Session → 401; eigenen Eintrag löschen → 200; fremden löschen → 403.

**Startrechte** — hier liegen die feinen Regeln:
- fremder Eintrag → 403
- eigener Eintrag, aber nicht der nächste in der Reihe → 409
- eigener Eintrag und an der Reihe → 200
- während ein Song läuft → 409 („Es läuft gerade ein Song")

**Avatare:** Liste; Bild abrufen (WebP, `Cache-Control` gesetzt); ungültige
Avatar-ID → 404. Es darf **keinen Upload-Weg** geben.

**Fernbedienung und Abbruch** brauchen Adminrechte, die nur mit PIN gelten. Ohne
Admin-Zugang überspringst du diese Punkte und **sagst das im Bericht** — nicht
stillschweigend weglassen. Wird dir eine Admin-PIN als Argument mitgegeben,
nutze sie; schreibe sie niemals in eine Datei.

**Alte API:** `/legacy`, `/getAllSongs`, `/sendKeyEvent`, `/sendPhoto` und
`/login` müssen 404 liefern. Tun sie es nicht, ist der Legacy-Abbau unvollständig.

## Bericht

Pro Bereich eine Zeile mit Ergebnis. Bei Abweichungen: erwarteter Status,
tatsächlicher Status, Endpunkt. Am Schluss ausdrücklich, was übersprungen wurde
und warum, sowie ob das Aufräumen geklappt hat.
