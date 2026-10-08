# Effekte in Reaper — was sinnvoll wäre, und was es kostet

Stand 2026-09-14. **Eingebaut und gemessen** — die Kette läuft, die
Latenzbilanz steht bei 0,00 ms. Was hier an Abwägungen steht, ist die
Begründung dafür, nicht mehr bloß eine Planung.

Angelegt wird sie von `~/.config/REAPER/Scripts/karaoke-fx.lua`,
ein-/ausgeschaltet mit `karaoke-fx.sh an|aus|status|neu`.

Der Aufbau, um den es geht, steht im Reaper-Abschnitt von
[`../CLAUDE.md`](../CLAUDE.md): drei Spuren in Reaper (Mikro 1, Mikro 2,
Songton), Master über das UR22 in die Anlage.

## Warum Effekte überhaupt gehen — und früher nicht

In diesem Projekt galt lange: **kein Hall auf dem Gesang.** Der Grund war
richtig: Vocaluxe bekam den Gesang über Reaper, und Nachhall verwischt die
Tonhöhen — der Hall ist noch der alte Ton, während schon der nächste
gesungen wird. Der PitchTracker sieht dann Matsch.

Das gilt nicht mehr. Vocaluxe hört seit dem 2026-09-14 über das ALSA-Gerät
`karaokemics` **direkt an der Hardware**, an Reaper vorbei. Am UR22-Eingang
hängen zwei unabhängige Abnehmer:

```
UR22 INPUT 1 ─┬─► REAPER:in1                  → Effekte → Fader → Anlage
              └─► alsa_capture (karaokemics)  → Vocaluxe, Bewertung
```

**Alles, was in Reaper passiert, erreicht die Bewertung nicht.** Effekte auf
den Mikrospuren sind damit für die Punktevergabe folgenlos — das war die
Bedingung, unter der sie überhaupt in Frage kommen.

## Gemessen: die Eigenlatenz der Plugins

Erhoben am 2026-09-14 über `TrackFX_GetNamedConfigParm(..., "pdc")` bei
44100 Hz, auf einer temporären Spur:

| Plugin | Zusatzlatenz |
|---|---|
| ReaGate, ReaEQ, ReaComp, ReaXcomp | **0 ms** |
| ReaVerbate, ReaVerb | **0 ms** |
| JS: Event Horizon, JS: 1175 | **0 ms** |
| **ReaLimit** | **10,00 ms** (441 Samples) |

**Das ist der wichtigste Wert im ganzen Dokument.** Die Sänger hören sich im
Reaper-Modus über die PA, also über den Rechner. Das PipeWire-Quantum steht
auf 256 Frames = 5,8 ms je Block; ReaLimit würde zu dieser Grundlatenz
nochmal 10 ms addieren und sie damit mehr als verdoppeln.

**Also kein ReaLimit auf dem Live-Weg.** `JS: Event Horizon` ist ein
vollwertiger Limiter ohne Lookahead-Latenz und die richtige Wahl. ReaLimit
bliebe nur für einen späteren Mitschnitt-Mixdown interessant, wo Latenz egal
ist.

## Die Effekte einzeln

Sortiert nach Gewinn pro Aufwand, nicht nach Auffälligkeit.

### 1. Hochpassfilter — der unterschätzte Gewinn

**ReaEQ**, ein Hochpass bei etwa 80–100 Hz auf beiden Mikrospuren.

Entfernt Trittschall, Poppen, Handgeräusche am Mikrofongehäuse und das
allgemeine Rumpeln, das eine Gesangsstimme nie braucht. Kostet 0 ms und
praktisch keine CPU, und der Unterschied ist sofort hörbar. Wenn nur eine
Sache eingebaut wird, dann diese.

Vorsicht nach unten: Unter 80 Hz liegt bei einer Männerstimme nichts
Nützliches mehr, über 120 Hz wird es dünn.

### 2. Noise Gate — der wirksamste Feedback-Schutz

**ReaGate** auf beiden Mikrospuren.

Bei zwei Mikrofonen ist fast immer eines gerade unbenutzt — und nimmt
trotzdem die PA auf. Das ist der klassische Weg in die Rückkopplung und
verwäscht nebenbei den Klang. Das Gate schließt den ungenutzten Kanal.

Heikel ist die Schwelle: zu hoch, und leise Passagen werden abgeschnitten;
zu niedrig, und es öffnet auf die PA. Sie gehört im Raum eingestellt, mit
laufender Anlage, nicht am Schreibtisch. Attack kurz, Release eher lang
(150–300 ms), sonst klappert es zwischen den Silben.

### 3. Kompressor — gegen wandernde Mikrofonabstände

**ReaComp**, moderat (Ratio etwa 3:1, wenige dB Gain Reduction).

Gäste halten das Mikrofon mal an den Mund und mal 30 cm weg. Der Kompressor
fängt das ab und macht die Lautstärke gleichmäßig — für den Gesamteindruck
wichtiger als ein Limiter.

Zwei Nebenwirkungen: Er hebt in den Pausen das Rauschen mit an (deshalb
gehört das Gate **vor** ihn), und er erhöht die effektive Verstärkung leiser
Signale, was das Feedback-Risiko steigert.

### 4. Hall — der eigentliche Karaoke-Effekt

**ReaVerbate** reicht völlig, 0 ms Latenz, günstig in der CPU.

Für Karaoke der wirksamste Effekt überhaupt: Er kaschiert Unsicherheit,
macht dünne Stimmen voller, und die Leute trauen sich mehr. Trocken klingt
eine ungeübte Stimme über eine PA schonungslos.

**Als Send auf eine eigene Hall-Spur, nicht als Insert.** Dann gibt es einen
vierten Fader nur für die Hallmenge, der sich in einer Sekunde zurückziehen
lässt, ohne das Trockensignal anzufassen — und genau das braucht man, wenn
es anfängt zu koppeln.

Der Haken: Hall erhöht die Energie im Raum und damit das Feedback-Risiko. In
einem Raum, in dem offene Mikrofone vor den Boxen stehen, ist das die reale
Gefahr, nicht die CPU.

### 5. Limiter — Sicherheitsnetz, kein Klangwerkzeug

**JS: Event Horizon** (nicht ReaLimit, siehe Latenz oben).

Gegen den einen Gast, der ins Mikrofon brüllt. Er schützt vor digitalem
Clipping, nicht vor „zu laut für den Raum" — das bleibt Sache der Anlage.

Auf dem **Master** duckt ein Schrei auch die Musik mit (Pumpen). Sauberer:
je einer auf den Mikrospuren, der die Spitzen abfängt, plus einer auf dem
Master, der nur ganz oben überhaupt eingreift.

## Reihenfolge in der Kette

```
Mikrospur:   Gate  →  Hochpass (EQ)  →  Kompressor  →  [Send: Hall]  →  Limiter
Master:                                                                 Limiter
```

Das Gate steht vorn, damit es das rohe Signal beurteilt und nicht das vom
Kompressor angehobene Rauschen. Der Hall geht als Send ab, damit er die
Dynamikbearbeitung nicht mitverhallt.

**Der Send an Vocaluxe ist davon nicht betroffen** — er läuft ohnehin nicht
über Reaper. Die Hardware-Sends auf `out3`/`out4` (pre-FX, Reserve) blieben
selbst dann trocken, wenn sie wieder in Gebrauch kämen.

## Was tatsächlich eingebaut ist

```
Mikro 1 / Mikro 2:   ReaGate  →  JS: RBJ Highpass  →  ReaComp
Hall-Bus (Spur 4):   ReaVerbate, gespeist per Post-Fader-Send (−9 dB)
Master:              JS: Event Horizon
Song (Spur 3):       unbearbeitet
```

| Baustein | Einstellung |
|---|---|
| Gate | Threshold −45 dB, Attack 3 ms, Hold 50 ms, Release 200 ms |
| Hochpass | 100 Hz |
| Kompressor | Threshold −18 dB, Ratio 3:1, Attack 5 ms, Release 150 ms |
| Hall | Room 40, Dampening 60, eigener Hochpass 250 Hz, Wet 0 dB |
| Limiter | Threshold −2 dB, Ceiling −0,5 dB, Soft Clip 2 dB |

Die Werte sind bewusst zurückhaltend. Das Gate steht tief, weil ein zu hoch
angesetztes Gate leise Stellen abschneidet — und das fällt mehr auf als
etwas Raumgeräusch. Gate-Schwelle und Hallmenge gehören **im Raum** mit
laufender Anlage nachgezogen, nicht am Schreibtisch.

Der Hall hängt an einem eigenen Fader (`karaoke-pegel.py hall -12`), damit
er sich zurücknehmen lässt, ohne das Trockensignal anzufassen — genau das
braucht man, wenn es anfängt zu koppeln.

### Gemessen nach dem Einbau

| | |
|---|---|
| Zusatzlatenz Mikrospur (Gate + Hochpass + Comp) | **0,00 ms** |
| Zusatzlatenz Master (Limiter) | **0,00 ms** |
| Hochpass bei 60 Hz | **−9,4 dB** gegenüber 1 kHz |

Der Hochpass wurde gegengeprüft, indem zwei gleich laute Töne (60 Hz und
1 kHz, je −25 dBFS — über dem Gate, unter dem Kompressor) durch Spur 1
geschickt und am Master gemessen wurden.

### Falle: ReaEQs Hochpass-Band ist nicht scharf zu schalten

Der erste Versuch nutzte **ReaEQ**, das ein fertiges Band „High Pass 5" in
seiner Parameterliste führt — samt Frequenz, die sich auf 100 Hz setzen
lässt und danach auch so angezeigt wird. **Es filtert trotzdem nichts.** Das
Band ist deaktiviert, und über die Parameter-Schnittstelle gibt es keinen
Schalter dafür; die Bandaktivierung steckt im FX-Chunk.

Nachgemessen: 60 Hz kam mit gesetztem Hochpass, mit ReaEQ auf Bypass und
ganz ohne ReaEQ jeweils mit **identisch −27,9 dB** am Master an.

Das ist heimtückisch, weil die Oberfläche einen eingestellten Filter zeigt.
Im Einsatz ist deshalb `JS: RBJ Highpass/Lowpass Filters` (Stillwell), das
die Frequenz als schlichten Slider in Hz nimmt.

Zweite Falle aus demselben Anlauf: **`Track_GetPeakInfo` misst bei
aktivem Input-Monitoring den Pegel *vor* den Effekten.** Wer damit prüfen
will, ob ein Plugin wirkt, misst am falschen Ende — die Spur zeigt
unverändert den Eingangspegel. Messpunkt ist der **Master**, und die übrigen
Spuren gehören dafür stummgeschaltet.

## Bedienung im Betrieb

```bash
karaoke-fx.sh status        # was liegt an, und ist es scharf
karaoke-fx.sh aus           # alles auf Bypass, Signal laeuft roh durch
karaoke-fx.sh an
karaoke-fx.sh neu           # Kette neu aufbauen (nach Aenderung am Lua-Skript)

karaoke-pegel.py            # vier Fader: Mikro 1, Mikro 2, Song, Hall
karaoke-pegel.py hall -12   # Hallmenge zuruecknehmen
```

`karaoke-fx.sh aus` ist für den Zweifelsfall mitten im Abend gedacht: Wenn
etwas klingt, wie es nicht soll, ist in zwei Sekunden geklärt, ob es an den
Effekten liegt.

## Wie es angelegt ist

Die Kette entsteht in `~/.config/REAPER/Scripts/karaoke-fx.lua`, getrennt von
`karaoke-setup.lua` — das legt die Spuren an und würde dabei die eingestellten
Pegel zurücksetzen. Die Effekte lassen sich also neu aufbauen, ohne den Mix zu
verlieren.

Die ReaPlugs nehmen **normalisierte** Parameter (0..1 bzw. 0..2), deren
Bedeutung sich aus dem Wertebereich nicht ablesen lässt: Bei ReaComp ist
`Threshold = 1.0` gleich 0 dB, bei `0.0` steht dort `-inf`. Statt diese Skalen
zu raten, sucht `set_display()` den Zielwert: setzen, den *angezeigten* Wert
lesen, eingrenzen. Deshalb stehen im Protokoll exakt die gewünschten Werte
(`-45.0`, `3.00`, `150`) und nicht das, was eine geratene Umrechnung ergeben
hätte.

JS-Effekte sind die Ausnahme — sie nehmen echte Werte (`Event Horizon`:
Threshold in dB, `hpflpf`: Frequenz in Hz) und brauchen die Suche nicht.

Zum Ausprobieren im laufenden Betrieb:

```bash
reaper-call.py TrackFX_GetCount 0
reaper-call.py TrackFX_GetFXName 0 1
reaper-call.py TrackFX_SetEnabled 0 1 0      # einzelnes Plugin bypassen
```

## Offen

- **Im Betrieb ungehört.** Die Kette ist gemessen, aber noch nie einen Abend
  gelaufen. Gate-Schwelle und Hallmenge sind Startwerte, keine eingemessenen.
- **CPU-Verbrauch ungemessen.** Vier Haswell-Kerne ohne HT, Vocaluxe zieht
  mit Video schon 65 % eines Kerns. Wie viel Luft bei Quantum 256 noch für
  eine Effektkette bleibt, muss gemessen werden — am besten mit laufendem
  Video, nicht im Leerlauf.
- **Feedback-Verhalten des Raums unbekannt.** Gate-Schwelle und Hallmenge
  lassen sich nur vor Ort einstellen, mit der Anlage, die wirklich dasteht.
- **Zweites Mikrofon.** Solange nur eines hängt, lässt sich die
  Kanaltrennung mit Gate nicht sinnvoll erproben.
