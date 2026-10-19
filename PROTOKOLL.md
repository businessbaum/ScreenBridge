# ScreenBridge — der PC-Bildschirm auf dem Mac

Die Gegenrichtung zum AirPlay-Empfänger: Windows nimmt seinen Bildschirm auf und
schickt ihn an eine Mac-App, die ihn anzeigt und zurücksteuert.

## Warum kein AirPlay

Ein AirPlay-*Sender* müsste FairPlay von der Clientseite bedienen. Genau diese Seite ist
die geschützte — beim Empfänger ließ sich die Antwort aus bekannten Tabellen nachbilden,
beim Sender nicht. Ein eigenes Protokoll ist deshalb nicht der Notbehelf, sondern der
bessere Weg: Aufnahme, Kodierung und Takt liegen vollständig bei uns.

## Ein Port, drei Arten von Verbindung

**Port 7202, TCP, TLS 1.3 mit beidseitigem Zertifikat.** Der PC hört, der Mac verbindet
sich — immer in dieser Richtung. Es gab einmal drei offene Ports ohne Prüfung und ohne
Verschlüsselung; daraus ist einer geworden, an dem sich beide Seiten ausweisen müssen.

Nach dem Handschlag schickt der Verbindende **ein Byte**, das sagt, wofür die Verbindung
da ist:

| Byte | Inhalt | Voraussetzung |
| --- | --- | --- |
| `C` | Steuerkanal: JSON-Zeilen, Eingaben, Zustand, Dateiangebote | koppelt, wenn nötig |
| `V` | Bildkanal PC → Mac: SBRG-Kopf und Pakete | muss gekoppelt sein |
| `M` | Bildkanal Mac → PC: dieselbe Rahmung, andere Richtung | muss gekoppelt sein |
| `F` | eine Dateiübertragung, eigene Verbindung | muss gekoppelt sein |

Das Byte steht **vor** jeder Rahmung und kann deshalb in keine geraten. **Gekoppelt wird
nur über `C`.** Ohne diese Trennung entstünde bei offenem Kopplungsfenster aus einem
Bildstrom eine Kopplung.

Eine neue Verbindung derselben Art schließt die ältere. Der Fall tritt sicher ein:
verliert der Mac kurz das WLAN, hängt seine alte Verbindung hier noch halboffen.

### Warum das zugleich die Verschlüsselung ist

Im TLS-Handschlag tauschen beide Seiten flüchtige Schlüssel aus und errechnen daraus ein
gemeinsames Geheimnis, das nie über die Leitung geht. Unterschrieben wird dieser Austausch
mit dem dauerhaften Schlüssel — angenommen wird er nur, wenn die Unterschrift vom
angehefteten Fingerabdruck stammt. Wer sich dazwischenstellt, kann nicht unterschreiben
und bekommt das Geheimnis nicht. Verschlüsseln und Authentifizieren sind ein Vorgang,
nicht zwei.

### Kopplung: sechs Ziffern, aber erst nach einer Festlegung

Beide Seiten nennen den SHA-256 einer 16-Byte-Zufallszahl, **bevor** sie die der anderen
kennen; erst danach geben sie die Zahl preis. Aus beiden Fingerabdrücken und beiden
Zufallszahlen entstehen sechs Ziffern, die auf beiden Bildschirmen stehen müssen.

Ohne die Festlegung prüft der Vergleich nichts: Fingerabdrücke sind öffentlich, ein
Angreifer bringt eigene mit und probiert, bis beide Seiten dieselbe Zahl errechnen — bei
einer Million Werten einige hundert Versuche. Dasselbe Verfahren benutzt Bluetooth beim
Zahlenvergleich, und aus demselben Grund.

**Die erste Kopplung braucht auf der PC-Seite keinen Knopfdruck.** Solange dort nichts
angeheftet ist, wird jede Anfrage angenommen und die Ziffernfrage sofort gezeigt. Die
Sicherheit hängt am Vergleich, nicht am Knopf. Ist einmal ein Gerät gekoppelt, gilt wieder
die strenge Regel: dann öffnet „Koppeln“ ein Fenster von zwei Minuten für **einen**
Versuch.

## Bildkanal — Kennung `V`

### Kopf, 20 Byte, little-endian, einmal nach dem Verbindungsaufbau

| Versatz | Länge | Inhalt |
| --- | --- | --- |
| 0 | 4 | `SBRG` |
| 4 | 4 | Version, zurzeit `1` |
| 8 | 4 | Breite |
| 12 | 4 | Höhe |
| 16 | 4 | Bilder je Sekunde |

Breite und Höhe sind ein **Anfangshinweis für die Fenstergröße**. Maßgeblich ist das SPS
aus Art 1 — wechselt die Auflösung, kommt Art 1 neu, und der Empfänger baut seine
Formatbeschreibung neu auf.

Passt die Version nicht, beendet der Empfänger die Verbindung mit einer Meldung, statt zu
raten.

### Pakete, little-endian

| Versatz | Länge | Inhalt |
| --- | --- | --- |
| 0 | 1 | Art |
| 1 | 4 | Länge der Nutzlast |
| 5 | 8 | Aufnahmezeit in Mikrosekunden seit Verbindungsbeginn |
| 13 | *n* | Nutzlast |

Beide Seiten prüfen die Länge gegen eine Obergrenze von **16 MB** und trennen bei
Unsinn, damit ein verrutschter Strom keine Gigabyte anfordert.

| Art | Nutzlast |
| --- | --- |
| **0** | leer — Lebenszeichen, jede Sekunde, wenn kein Bild anfiel |
| **1** | `avcC`-Inhalt: Profil, Stufe, Längenfeldbreite, SPS, PPS |
| **2** | eine Zugriffseinheit, **kein** Einstiegspunkt |
| **3** | eine Zugriffseinheit **mit IDR** — hier kann begonnen werden |

**Art 1 geht unmittelbar vor jedem Art 3 hinaus.** Der Empfänger verwirft alles bis zum
ersten Art 3; damit ist ein Verbindungsaufbau mitten in einer Bildgruppe unkritisch.

**Ein Schlüsselbild kostet einen Ausschlag.** Es ist ein Vielfaches eines P-Bildes groß
und belegt die Strecke länger als seine 16,7 ms Anzeigedauer — im Sekundentakt erzwungen
erzeugt es genau die ungleichmäßige Ankunft, die wir als Ruckelursache kennen.

Deshalb **auf Anforderung statt im engen Takt**: der Mac schickt `{"t":"idr"}` über den
Steuerkanal, wenn er eines braucht — beim Verbinden und wenn er den Faden verliert. Der feste Takt
bleibt als Netz, aber weit gestellt (`-g 300`, also alle fünf Sekunden). Der Einstieg
wird dadurch sogar schneller: ohne Anforderung wartet der Mac im Mittel eine halbe
Sekunde auf das nächste Schlüsselbild, mit Anforderung kommt es sofort.

### Das Format der Nutzlast

Zugriffseinheiten kommen im **AVCC-Format**: je NAL-Einheit vier Byte Länge und dann die
Daten. Kein Startcode, kein Annex B — die Frage nach drei- oder vierstelligen Startcodes
stellt sich damit nicht.

> **Achtung, gemischte Bytereihenfolge, und das ist Absicht:** der *Paketkopf* ist
> little-endian, die Längenfelder *innerhalb* der AVCC-Nutzlast sind big-endian. So
> schreibt MP4 das vor, und daher stammen die Daten unverändert. Wer das später
> „repariert", macht es kaputt.

Auf der Mac-Seite geht das ohne Umwandlung weiter: `CMBlockBuffer` über die Nutzlast,
`CMSampleBuffer` mit der Formatbeschreibung aus Art 1, in den
`AVSampleBufferDisplayLayer`.

Nachgemessen kommen vor: Slice (1), IDR (5), SEI (6). **Keine** AUD, keine Parametersätze
im Bild — die stehen in Art 1. Füll-NALs (12) werden auf der PC-Seite entfernt.

### Aufnahme und Kodierung

```
ffmpeg -f lavfi -i ddagrab=output_idx=0:framerate=60:draw_mouse=1
       -c:v h264_nvenc -preset p1 -tune ull
       -rc vbr -b:v 12M -maxrate 40M -bufsize 1M
       -g 300 -forced-idr 1 -bf 0 -delay 0
       -f mp4 -movflags +frag_keyframe+empty_moov+default_base_moof+frag_every_frame pipe:1
```

`ddagrab` nimmt über die Desktop Duplication API direkt auf der Grafikkarte auf,
`h264_nvenc` kodiert dort weiter — kein Bild sieht den Hauptspeicher.

`+frag_every_frame` erzeugt ein `moof`+`mdat`-Paar je Bild mit ausdrücklicher Länge —
nachgemessen 118 Paare für 118 Bilder. **Damit stehen die Bildgrenzen im Datenstrom.**
Das ist die Lehre aus dem AirPlay-Weg, wo sie fehlten und eine ganze Bildlänge kosteten:
ein roher H.264-Strom hat keine Paketgrenzen, und ein Zerleger gibt ein Bild erst heraus,
wenn das nächste anfängt.

`-bf 0`: ohne B-Bilder ist Übertragungs- gleich Anzeigereihenfolge.

**`-rc vbr`, nicht `cbr`.** Nachgemessen am gleichen Bildschirminhalt: CBR 22,4 Mbit/s
mit **4,6 MB Füllbytes**, VBR **3,7 Mbit/s ohne jede**. CBR polstert auf die eingestellte
Rate auf, auch wenn sich nichts bewegt.

`-bufsize 1M` sind bei 12 Mbit/s Ziel rund 80 ms Vorrat. Größer wäre besser für den
Durchsatz und schlechter für die Verzögerung: ein Ausschlag dürfte dann entsprechend
länger die Strecke belegen.

## Sieben Fehler an einem Tag, und was sie gemeinsam hatten

Keiner saß in der Kryptographie, im Kodierer oder in der Rahmung. **Jeder war eine
Auskunft, die nicht ankam.**

| Symptom | Ursache | Regel daraus |
| --- | --- | --- |
| 25 Neuverbindungen in 20 Sekunden | `screen.start` ist Aufforderung **und** Bestätigung; wer auf die Bestätigung hin abreisst, baut einen Kreis | Läuft der genannte Bildschirm schon, bleibt der Kanal stehen |
| Der Mac hängt beim Bildschirmwechsel | Der PC schickte `screen.start` nur, **wenn** ein Kanal lief — und der Mac schließt seinen vorher | Auf jedes `screen.select` folgt ein `screen.start`, ohne Bedingung |
| Klicks landen auf dem falschen Bildschirm | `x`/`y` sind 0..1 des gesendeten Schirms; der Empfänger rechnete auf den Hauptschirm | Der Sender nennt Maße und Lage des Schirms, den er aufnimmt |
| „Fenster drüben: sagt nichts dazu" | `screen.state` ging nur bei Änderung hinaus; nach einem Abriss wusste keiner mehr etwas | Jeder dauerhaft angezeigte Zustand wird **beim Verbinden** einmal genannt |
| Bildkanal kommt zurück, Steuerkanal nie | Ein `continue` verwarf Daten — richtig — und ließ dabei den Merker „verbunden" stehen | Eine Verwerfungsregel darf keine Zustandsaussage werden |
| Drei Sekunden Verzögerung, wachsend | `HALFTONE` beim Zeichnen kostet 19,6 ms statt 0,76 — mehr als ein Bildabstand, also staut es sich **stromaufwärts** bis zum Sender | Verkleinert wird in swscale, nicht in GDI |
| Der Grund für einen Abbruch kommt nie an | Eine Seite hatte ihn gebaut und nicht gesendet, die andere las das Feld nicht | Ein stummer Sender und ein tauber Empfänger sind von außen nicht zu unterscheiden |

Zwei Sätze fassen alle sieben:

> **Der Zwischenzustand muss dastehen, sonst sieht Warten aus wie Kaputtsein.**
>
> **Gemessen wird, was man für schwierig hält.** Ziffernableitung, Zertifikatsprüfung,
> Bildrahmung und Taktstreuung sind mit Prüfwerten und Kreismessungen abgesichert — und
> dort saß keiner der sieben. Sie saßen in dem, was zu einfach aussah, um es zu prüfen:
> ein Merker, eine Kopfzeile, ein Feld, ein `continue`.

### Was gemessen ist

| | |
| --- | --- |
| Dekodierweg allein, 2940×1912 | **212 Bilder/s** · `-threads 1` ist richtig, mehr bringt nichts (2: 208, 4: 199) |
| Empfangsweg im Kreis, echte Quellen | 2940×1912 ungeschrumpft: **0 von 300 Bildern verloren**, 14 ms Durchlauf, **1110 MB/s** |
| dasselbe auf 1598×1040 verkleinert | 0 verloren, **11 ms**, 328 MB/s |
| Zeichnen 1:1 | **0,32 ms** · mit `COLORONCOLOR` verkleinern 0,76 ms · mit `HALFTONE` **19,62 ms** |
| Steuerkanal nach hartem Abschuss | von selbst zurück nach **341 ms**, ohne Zutun |
| Antwortzeit über den Steuerkanal | **7,3 bis 9,6 ms** im WLAN |

Der Kreis-Prüfling bindet `MacScreen.cs`, `Decoder.cs` und `H264Sps.cs` über
`<Compile Include>` als **echte Quellen** ein — eine Kopie wäre eine zweite Gelegenheit,
dieselbe Falle zu übersehen. Er liegt bewusst nicht im Baum, ist aber in einer
Viertelstunde wieder aufgesetzt.

**Was er nicht misst: das Zeichnen.** Genau dort saß der sechste Fehler. Zwei Messungen
zusammenzuzählen ist nicht dasselbe wie eine.

### Die Verzögerung: drei offensichtliche Verbesserungen, keine davon eine

Gemessen über die ganze Kette, bei einem Fenster von 1600×1040:

| Etage | vorher | jetzt |
| --- | --- | --- |
| Ereignis zum Mac | ~4 ms | ~4 ms |
| Einspeisen und Zeichnen dort | ~5 ms | ~5 ms |
| Aufnehmen | ~17 ms | ~17 ms |
| **Kodieren** | **46,0 ms** | **12,8 ms** |
| Netz | ~4 ms | ~4 ms |
| **Dekodieren** | **10–12 ms** | **4,0 ms** |
| Zeichnen, Bildwechsel | bis 17 ms | bis 17 ms |
| | **103 ms** | **63 ms** |

**Was nicht geholfen hat, obwohl es naheliegt:**

| Griff | erwartet | gemessen |
| --- | --- | --- |
| `h264_cuvid` statt Software-Dekodierer | schneller | **23 statt 10 ms**, und ein Bild zurückgehalten |
| `EnableLowLatencyRateControl` beim Kodieren | schneller | **132 statt 46 ms** bei 2940×1912, wirkungslos bei 1920 |
| Volle Auflösung senden | schärfer | **+33 ms**, und die Bildpunkte werden am Ende weggerechnet |

**Was geholfen hat, war jedes Mal Weglassen, nie Beschleunigen:** keine Bildpunkte
kodieren, die das Fenster nicht zeigt (−33 ms); nicht verkleinern, was gar nicht erst zu
groß ankommt (−6 ms); den Zeiger nicht durch die Kette schicken, wenn er lokal schon da
ist (`draw_mouse=0`, `showsCursor=false`).

### Der Zeiger ist immer der lokale, das Bild ist immer das entfernte

Wer zusieht, sieht seinen eigenen Zeiger; die Gegenseite nimmt ihren nicht mit auf. Der
Grund ist nicht Geschmack: der Zeiger ist das einzige Element, an dem die Hand die
Verzögerung misst. Der Inhalt darf nachlaufen — er nicht.

Zuerst war es umgekehrt gebaut, mit der Begründung „zwei Zeiger sind einer zu viel“. Die
stimmt nur, solange der entfernte rechtzeitig da ist, und das kann er nicht sein: er
steckt in der ganzen Kette. Der Benutzer beschrieb es als „die Maus fühlt sich versetzt
an“ — nach dem Wechsel als „perfekt“, **ohne dass eine einzige Millisekunde verschwunden
wäre**.

### Zwei Rechner, die einander folgen, brauchen eine Schwelle

Der PC meldet seine Fenstergröße (`mac.size`), der Mac stellt die Aufnahme darauf um und
baut den Strom neu auf. Der erste Entwurf passte das Fenster bei **jedem** neuen Strom auf
die Quellgröße ein — also auch bei dem, der wegen der Meldung entstand:

```
21:52:57  Bild von MacBook Air: 1900×1236
21:52:58  Bild von MacBook Air: 1900×1234
21:52:58  Bild von MacBook Air: 1896×1234
21:52:59  Bild von MacBook Air: 1894×1232
21:52:59  Bild von MacBook Air: 1894×1230
21:52:59  Bildempfang endete
```

Fünf Neuaufbauten in zwei Sekunden, jeder ein paar Bildpunkte kleiner — keine Schwingung,
eine **Ratsche**. Sichtbar als Aussetzer, Flackern und Abbruch.

Zwei Sperren, jede allein würde genügen, und deshalb gibt es beide: das Fenster wird nur
**einmal** eingepasst, und eine Größe geht nur hinaus, wenn sie sich um mehr als ein
Zehntel geändert hat.

## Der Steuerkanal `C` — Zustand, Bildschirmwahl, Dateien

Eine JSON-Zeile je Nachricht, UTF-8, mit `
` abgeschlossen — dieselbe Rahmung wie bei
den Eingaben, derselbe Kanal.

**Ein unbekanntes `t` wird ignoriert, nie als Fehler behandelt.** Jede Seite muss
vollständig funktionieren, wenn die andere nichts davon schickt. Nur so lässt sich eine
Seite ändern, ohne die andere im selben Zug mitzuändern.

### PC → Mac

| `t` | Felder | Wann |
| --- | --- | --- |
| `hello` | `name`, `os`, `version`, `screens[]`, `active` | sofort nach dem Verbinden und bei jeder Änderung der Bildschirmanordnung |
| `screen.start` | `index` | „Verbinden“, und als Antwort auf **jedes** `screen.select` |
| `screen.stop` | — | „Trennen“; der Bildkanal wird zusätzlich selbst geschlossen |
| `ping` | `n` | alle zwei Sekunden, solange die Steuerung steht |
| `pong` | `n` | Antwort auf ein `ping` des Macs |
| `file.offer` | `id`, `name`, `size`, `sha256` | eine Datei liegt zum Abholen bereit |
| `wake.info` | `mac`, `broadcast` | beim Verbinden — für das Wecken über Netz |

Ein Eintrag in `screens[]`: `{"index":0,"width":2560,"height":1440,"primary":true,"label":"…"}`.

### Mac → PC

| `t` | Felder | Wirkung |
| --- | --- | --- |
| `hello` | `name`, `os`, `version` | steht als „Gegenstelle“ im Fenster |
| `pong` | `n` | daraus die Antwortzeit |
| `ping` | `n` | wird beantwortet |
| `screen.select` | `index` | Bildschirm wechseln |
| `screen.state` | `showing`, `fullscreen` | Zustand des Fensters drüben |
| `file.progress` | `id`, `got`, `size` | Fortschritt beim Abholen |
| `file.done` | `id`, `ok`, `why` | fertig |
| Eingaben | siehe unten | Maus, Tastatur, `idr`, `reset` |

### Der Bildschirmwechsel ist ein neuer Kanal, kein Umschalten

Der SBRG-Kopf mit Breite und Höhe steht am Anfang einer Bildverbindung und gilt für sie
ganz. Ein anderer Bildschirm heißt deshalb: alten Kanal schließen, neuen öffnen.

Der Ablauf ist festgelegt, weil zwei Kanäle nebeneinander denselben Kopf verschieden
läsen:

1. Der Mac schließt seinen Bildkanal und schickt `screen.select`.
2. Er öffnet **nicht** von sich aus neu, sondern wartet.
3. Der PC antwortet **immer** mit `screen.start` — auch dann, wenn bei ihm gerade kein
   Bildkanal läuft.
4. Der Mac öffnet neu und liest die neue Größe aus dem Kopf.

**Schritt 3 ist die Stelle, an der es einmal hängen blieb.** Der PC schickte
`screen.start` nur, wenn bei ihm ein Bildkanal lief — und weil der Mac ihn in Schritt 1
gerade geschlossen hatte, war die Antwort darauf immer nein. Der Mac hätte ewig gewartet,
und im Protokoll stünde nichts.

Drückt der Nutzer dagegen am **Mac** auf „Verbinden“, öffnet der Mac den Bildkanal selbst und
meldet die Wahl hinterher. Der PC nimmt eine `V`-Verbindung an, um die er nicht gebeten
hat; er schließt einen laufenden Kanal nur dann, wenn dieser einen **anderen** Bildschirm
zeigt.

### Die Gegenrichtung `M` — der Mac-Bildschirm auf dem PC

Rahmung Byte für Byte dieselbe wie `V`: SBRG-Kopf, dann Pakete aus Art, Länge,
Zeitstempel und AVCC-Nutzlast. Nur die Richtung ist umgekehrt.

**Diese Richtung hat gefehlt, und das war ein Konstruktionsfehler, kein Versehen.** Auf
beiden Seiten hieß ein Knopf „Verbinden“: am Mac holte er das Bild, am PC gab er eines
her. Zwei gleich benannte Knöpfe mit entgegengesetzter Wirkung — gefunden hat das der
Benutzer, weil jede Seite auf sich selbst geschaut hatte.

| Richtung | `t` | Felder |
| --- | --- | --- |
| PC → Mac | `mac.start` | `index` — diesen Bildschirm des Macs senden |
| PC → Mac | `mac.stop` | — |
| PC → Mac | `idr` | Schlüsselbild jetzt; ohne Antwort bleibt das Fenster schwarz |
| PC → Mac | `move`, `down`, `up`, `scroll`, `text`, `key`, `reset` | dieselben Formen wie in der Gegenrichtung, nur speist sie der Mac ein |
| Mac → PC | `hello` | zusätzlich `macScreens[]` und `macActive` |
| Mac → PC | `mac.state` | `sending`, `index` — gleich nach `hello` und bei jeder Änderung |

Auf der PC-Seite geht die Nutzlast durch ffmpeg. Umgewandelt wird nur die Rahmung der
NAL-Einheiten: vier Byte Länge werden zu vier Byte Startcode, also **im selben Puffer und
ohne Kopie**. Die Parametersätze aus Art 1 werden aus dem avcC gelöst und als Annex B
vorangestellt.

**Maßgeblich für die Größe ist das SPS, nicht der Kopf.** Der Kopf ist ein Anfangshinweis
für die Fenstergröße. Der Unterschied ist einmal teuer geworden: aus dem Kopf gelesen
wurde ein 1080p-Bild auf 1440p hochgerechnet, und das sah aus wie eine schlechte
Verbindung.

**Die Bedienung liegt ganz auf der Seite, die zusieht.** Wer den PC-Bildschirm sehen
will, sitzt am Mac und drückt dort; wer den Mac sehen will, sitzt am PC. Deshalb hat das
PC-Fenster keine Knöpfe mehr für die Freigabe des eigenen Bildschirms — nur noch die
Anzeige, dass sie läuft, und ein „Trennen“, das beide Richtungen beendet.

### Eine Nachricht, die Aufforderung und Bestätigung zugleich ist

`screen.start` und `mac.start` bedeuten „öffne den Kanal“ — und werden auch dann
geschickt, wenn er schon offen ist. Beide Seiten haben daraus unabhängig voneinander
dieselbe Schleife gebaut:

1. A fordert an, B öffnet.
2. A bestätigt mit derselben Nachricht.
3. B liest die Bestätigung als neue Aufforderung, reisst ab und öffnet neu.
4. Bei A gewinnt die jüngste Verbindung; das Ende der alten liest B als Abbruch und
   verbindet wieder.

Gemessen: **25 Neuverbindungen in 20 Sekunden.** Keiner der vier Schritte ist für sich
falsch — die Bestätigung ist richtig, „die jüngste gewinnt“ ist richtig, das
Neuverbinden nach einem Abbruch ist richtig.

**Die Regel gilt auf beiden Seiten gleich:** läuft der genannte Bildschirm schon, ist die
Nachricht nur eine Bestätigung und der Kanal bleibt stehen. Abgerissen wird ausschließlich
bei einem **anderen** Bildschirm.

### Die Eingaben gehören zu dem Schirm, der gesendet wird

`x` und `y` in `move`, `down` und `up` sind 0..1 **des gerade übertragenen Bildschirms**,
nicht des Hauptbildschirms und nicht des Gesamtdesktops. Der Empfänger rechnet auf Maße
und Lage genau dieses Schirms um.

Ohne das landet bei zwei Bildschirmen jeder Klick auf dem ersten — plausibel aussehend
und falsch. Das ist die unangenehmere Hälfte dieses Kapitels: eine Schleife merkt man
sofort, einen falschen Bildschirm erst, wenn etwas Unerwartetes passiert.

### Ein Zustand wird beim Verbinden genannt, nicht nur beim Ändern

`hello` und `screen.state` gehen auf **jeder** neuen Steuerverbindung hinaus, auch wenn
sich nichts geändert hat. Das ist keine Redundanz, sondern die Bedingung dafür, dass die
Anzeige stimmt.

Der Fall trat auf: der Mac schickte `screen.state` nur bei Verbinden, Trennen und
Vollbild — also bei Ereignissen. Der PC vergisst den Wert beim Verbindungsabriss. Nach
dem Neuverbinden hatte keine Seite mehr etwas zu sagen: der Mac hatte den Zustand schon
gemeldet, der PC hatte ihn schon vergessen. Im Fenster stand „sagt nichts dazu“, und das
sah aus wie ein Fehler in der Anzeige.

**Unbekanntheit ist von einem Fehler nicht zu unterscheiden.** Was dauerhaft angezeigt
wird, muss beim Verbinden einmal gesagt werden.

### Was wartet, statt zu verfallen

Der Steuerkanal ist zwischendurch weg — der Mac verbindet sich alle paar Sekunden neu.
Wer in diese Lücke klickt, hätte sonst nichts erreicht und müsste es erraten. Deshalb
sammeln sich Dateiangebote und gehen beim nächsten `Connected` hinaus. Von den
Bildschirmbefehlen bleibt nur der **letzte** liegen: „Verbinden“ und danach „Trennen“
nacheinander abzuarbeiten hieße, dass der Bildschirm für einen Moment hinausgeht, den
niemand wollte.

## Eingaben — im Steuerkanal `C`

Eine JSON-Zeile je Ereignis, UTF-8, mit `\n` abgeschlossen.

```json
{"t":"move","x":0.5,"y":0.5}
{"t":"down","b":"left","x":0.5,"y":0.5}
{"t":"up","b":"middle","x":0.5,"y":0.5}
{"t":"scroll","dy":-3.0,"dx":0}

**`scroll` zählt in ZEILEN**, nicht in Rastpunkten und nicht in Bildpunkten. Positiv ist
nach oben. Ein Rastpunkt des Windows-Rads sind so viele Zeilen, wie dort eingestellt ist
(`MouseWheelScrollLines`, hier vier) — nicht eine, und auch keine feste Drei.

Beide Seiten müssen **Bruchteile aufheben**. Der Empfänger rechnet Zeilen in seine eigene
Einheit um und behält, was dabei unter einer ganzen Einheit bleibt, für die nächste
Nachricht. Ohne das verschwindet jede feine Bewegung: die Mac-Seite schnitt ab
(`Int32(…)`), die PC-Seite rundete — beides fällt bei ganzen Rastpunkten nicht auf und
frisst bei einem Trackpad alles auf.

Gefunden wurde es an einem Rollen, das zu schwach war: der Sender schickte den Rastpunkt
statt der Zeilenzahl, also ein Viertel. Erst die Frage nach der **Einheit** brachte das
Abschneiden mit ans Licht.

{"t":"text","s":"a"}
{"t":"key","c":36,"down":true}
{"t":"reset"}
{"t":"idr"}
```

`x` und `y` sind auf 0..1 bezogen, gemessen am **übertragenen Bild** — nicht am
Mac-Fenster. Ränder rechnet der Mac vorher heraus.

`b` ist `left`, `right` oder `middle`.

**Scroll** zählt in **Zeilen**, Bruchteile erlaubt. **Positiv heißt: der Inhalt wandert
nach oben**, wie beim Drehen des Rads vom Benutzer weg. macOS dreht die Richtung je nach
Einstellung um; der Mac gleicht das aus, bevor er sendet. Der PC rechnet mit
`WHEEL_DELTA` (120).

**`idr`** fordert ein Schlüsselbild an — siehe oben. Der Mac schickt es beim Verbinden
und wenn er den Faden verliert.

**`reset`** gibt auf dem PC alles Gehaltene frei. Der Mac schickt es, wenn sein Fenster
den Fokus verliert. Bricht die Verbindung ab, gibt der PC von sich aus alles frei —
sonst bliebe eine Maustaste oder eine Umschalttaste gedrückt und der PC markiert alles
oder schreibt groß.

### Tastatur

`c` ist der **macOS**-Tastencode; der PC bildet ihn ab. Die Zuordnung bleibt damit dort,
wo die Tastatur steht.

- **Druckbares geht als `text`**, alles andere als `key`. Der PC setzt Text über
  `KEYEVENTF_UNICODE` ein — damit stimmen Umlaute ohne jede Belegungstabelle. Ohne diese
  Trennung käme ein „a" doppelt oder gar nicht an.
- **Modifikatoren nach dem Griff, nicht nach der Taste:** Befehl → **Strg**, Wahl → Alt,
  Steuerung → Strg, Umschalt → Umschalt. Sonst kopiert am Mac niemand mit dem Griff, den
  er kennt.

**Systemgriffe** wie Befehl-Tabulator oder Befehl-Leertaste fängt macOS vor jeder App ab.
Sie kommen nur an, wenn die Mac-App einen `CGEventTap` benutzt — und der verlangt die
Freigabe unter „Bedienungshilfen". Alles andere geht ohne jede Berechtigung.

### Zwei Punkte, die auf der Mac-Seite Zeit sparen

- **Nicht drosseln.** Am AirPlay-Weg hat eine Drossel auf 120 Positionen je Sekunde das
  Ruckeln nachweislich verstärkt: die Bildaufnahme greift 60-mal je Sekunde eine Position
  heraus, und je enger die Positionen liegen, desto kleiner ist der Fehler dieser
  Stichprobe.
- **Nicht auf dem Zeichenthread senden.** Ein blockierender Schreibvorgang hält sonst das
  Bild an. Eigener Thread, Bewegungen dürfen einander überschreiben, Klicks und Tasten
  nicht.

## Was der PC mit den Ereignissen macht

`SendInput` mit absoluten Koordinaten, `MOUSEEVENTF_VIRTUALDESK`. Der Zielbereich ist der
**aufgenommene** Bildschirm innerhalb des virtuellen Schreibtischs, nicht dieser selbst —
bei zwei Bildschirmen landen Klicks sonst auf dem falschen.

Die absoluten Koordinaten laufen in 0..65535 über den virtuellen Schreibtisch. Bei 2560
Bildpunkten Breite ist ein Schritt rund 0,04 Bildpunkte; die Quantisierung sitzt also
hier und nicht auf der Mac-Seite.

## Was die Zeitstempel können und was nicht

Sie zählen ab Verbindungsbeginn, und beide Uhren laufen getrennt. Damit lässt sich die
**Gleichmäßigkeit** messen, nicht die Gesamtverzögerung. Für die bräuchte es einen
Rückweg über den Steuerkanal — später, falls nötig.
