using System.Buffers.Binary;

namespace ScreenBridge;

/// <summary>
/// Die Gegenrichtung: der Mac-Bildschirm kommt hier an und wird dekodiert.
/// </summary>
/// <remarks>
/// <para>
/// <b>Diese Richtung hat gefehlt, und das war ein Konstruktionsfehler.</b> Der Knopf am
/// PC hieß „Verbinden“ und bewirkte, dass der eigene Bildschirm zum Mac ging — also
/// „Freigeben“. Wer am PC sitzt und dort verbindet, will den Mac sehen. Beide Richtungen
/// über dieselbe gesicherte Verbindung, nur mit einer anderen Kennung: <c>V</c> hinaus,
/// <c>M</c> herein.
/// </para>
/// <para>
/// Die Rahmung ist Byte für Byte dieselbe wie bei <c>V</c>. Das ist keine Bequemlichkeit,
/// sondern Absicht: eine zweite Beschreibung wäre eine zweite Gelegenheit, sie
/// auseinanderlaufen zu lassen, und der Fehler sähe dann aus wie ein kaputter Strom.
/// </para>
/// <para>
/// <b>Umgewandelt wird nur die Rahmung der NAL-Einheiten.</b> Über die Leitung kommt
/// AVCC — vier Byte Länge je Einheit, big-endian, wie MP4 es vorschreibt. ffmpeg will an
/// seiner Standardeingabe Annex B, also Startcodes. Beide sind vier Byte lang; die
/// Umwandlung geht deshalb im selben Puffer und kostet keine Kopie.
/// </para>
/// </remarks>
internal sealed class MacScreen : IDisposable
{
    /// <summary>Mehr kann kein Bild sein; die Gegenseite prüft dieselbe Grenze.</summary>
    private const int MaxPayload = 16 * 1024 * 1024;

    private static readonly byte[] StartCode = [0x00, 0x00, 0x00, 0x01];

    private readonly Action<string> _log;
    private Decoder? _decoder;

    public MacScreen(Action<string> log) => _log = log;

    /// <summary>
    /// Wohin die fertigen Bilder gehen.
    /// </summary>
    /// <remarks>
    /// Als veränderliche Eigenschaft und nicht als Konstruktorwert, weil das Fenster erst
    /// entsteht, wenn die Größe bekannt ist — und die steht im Strom, nicht vorher fest.
    /// Der Dekodierer liest sie bei jedem Bild neu; damit ist das Fenster austauschbar,
    /// ohne den Strom anzufassen.
    /// </remarks>
    public Action<byte[]>? Sink { get; set; }

    /// <summary>Der Strom steht: Quellgröße und Bildrate, für Fenster und Seitenverhältnis.</summary>
    public event Action<int, int, int>? Opened;

    /// <summary>Der Dekodierer liefert ab jetzt Bilder <b>dieser</b> Größe.</summary>
    public event Action<int, int>? Format;

    public event Action? Closed;

    /// <summary>
    /// Wie groß die Bilder herauskommen sollen — die Fläche, auf der sie landen.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Verkleinert wird in ffmpeg, nicht beim Zeichnen.</b> Der erste Anlauf ließ den
    /// Dekodierer in voller Größe liefern und rechnete beim Zeichnen herunter — mit
    /// <c>HALFTONE</c>, weil <c>COLORONCOLOR</c> beim Verkleinern jede zweite Zeile
    /// wegwirft. Nachgemessen kostet das <b>19,6 ms</b> je Bild bei 2940×1912 auf
    /// 1600×1040, gegen <b>0,76 ms</b> mit COLORONCOLOR. 19,6 ms sind 51 Bilder je
    /// Sekunde und damit weniger, als hereinkommen.
    /// </para>
    /// <para>
    /// Was dann geschieht, ist schlimmer als ein paar ausgelassene Bilder: das Zeichnen
    /// hält die Sperre, der Lesefaden kommt nicht an sie heran, die Rohrleitung läuft
    /// voll, ffmpeg hört auf zu liefern — und die Verzögerung wächst <b>stromaufwärts</b>
    /// weiter, solange die Übertragung läuft. Gemessen: 28 Bilder/s heraus bei 1250 ms
    /// Durchlauf und 43 Bildern im Dekodierer.
    /// </para>
    /// <para>
    /// swscale erledigt dieselbe Arbeit besser und nebenbei: der Dekodierweg schafft
    /// <b>212 Bilder/s</b> bei voller Auflösung. Danach ist das Zeichnen ein 1:1-BitBlt zu
    /// 0,32 ms, die Rohrleitung trägt ein Vielfaches weniger, und die Kopie in die
    /// Bildfläche schrumpft im selben Verhältnis.
    /// </para>
    /// </remarks>
    public Size Target { get; set; }

    public bool Receiving { get; private set; }
    public string Sender { get; private set; } = "";
    public int Width { get; private set; }
    public int Height { get; private set; }
    public int Fps { get; private set; }
    public DateTime Since { get; private set; }
    public long Frames { get; private set; }
    public long Bytes { get; private set; }

    public int LatencyMs => _decoder?.SteadyLatencyMs ?? 0;
    public int JitterMs => _decoder?.JitterMs ?? 0;
    public int BeatMs => _decoder?.BeatMs ?? 0;
    public int MaxGapMs => _decoder?.MaxGapMs ?? 0;

    private Stream? _stream;

    /// <summary>Den Empfang von hier aus beenden — „Trennen“ und Fenster zu.</summary>
    public void Stop()
    {
        var stream = _stream;
        if (stream is null) return;

        _log("[Mac] Bildempfang wird von hier aus beendet");
        try { stream.Dispose(); } catch { }
    }

    /// <summary>
    /// Einen bereits gesicherten Strom lesen, bis er endet.
    /// </summary>
    /// <remarks>
    /// Läuft auf dem Faden der Verbindung und blockiert dort — das ist gewollt: das Ende
    /// des Lesens ist zugleich das Ende der Verbindung, und beides an einer Stelle zu
    /// haben erspart die Frage, wer aufräumt.
    /// </remarks>
    public void Serve(Stream stream, string who)
    {
        Stop();
        _one.Wait();

        try { ServeCore(stream, who); }
        finally { _one.Release(); }
    }

    private readonly SemaphoreSlim _one = new(1, 1);

    private void ServeCore(Stream stream, string who)
    {
        try
        {
            _stream = stream;

            var head = new byte[20];
            ReadFully(stream, head, 20);

            if (head[0] != (byte)'S' || head[1] != (byte)'B' || head[2] != (byte)'R' || head[3] != (byte)'G')
            {
                _log("[Mac] kein SBRG im Kopf — der Strom passt nicht zum Protokoll");
                return;
            }

            var version = BinaryPrimitives.ReadInt32LittleEndian(head.AsSpan(4));
            if (version != 1)
            {
                _log($"[Mac] Version {version} statt 1 — getrennt, statt zu raten");
                return;
            }

            Width = BinaryPrimitives.ReadInt32LittleEndian(head.AsSpan(8));
            Height = BinaryPrimitives.ReadInt32LittleEndian(head.AsSpan(12));
            Fps = BinaryPrimitives.ReadInt32LittleEndian(head.AsSpan(16));

            // Dieselbe Grenze wie beim SPS: 8192 deckt jedes echte Display, und die
            // Werte gehen unten in Fenster- und Pufferrechnungen ein.
            if (Width is <= 0 or > 8192 || Height is <= 0 or > 8192)
            {
                _log($"[Mac] Kopfmaße {Width}×{Height} unplausibel — getrennt, statt zu raten");
                return;
            }

            Sender = who;
            Since = DateTime.UtcNow;
            Frames = 0;
            Bytes = 0;

            _log($"[Mac] Bild von {who}: {Width}×{Height}, {Fps} Bilder/s");

            Receiving = true;
            Opened?.Invoke(Width, Height, Fps);

            Pump(stream);
        }
        catch (Exception ex) { _log($"[Mac] Bildempfang endete: {FirstLine(ex.Message)}"); }
        finally
        {
            Receiving = false;
            _stream = null;

            _decoder?.Dispose();
            _decoder = null;

            Closed?.Invoke();
            _log("[Mac] Bildempfang zu");
        }
    }

    private void Pump(Stream stream)
    {
        var header = new byte[13];
        var payload = new byte[64 * 1024];

        while (true)
        {
            if (!TryReadFully(stream, header, 13)) return;

            var kind = (PacketKind)header[0];
            var length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(1));

            if (length < 0 || length > MaxPayload)
            {
                _log($"[Mac] Nutzlast {length} Byte über der Grenze — getrennt");
                return;
            }

            if (length > payload.Length) payload = new byte[Math.Max(length, payload.Length * 2)];
            if (length > 0 && !TryReadFully(stream, payload, length)) return;

            Bytes += 13 + length;

            switch (kind)
            {
                case PacketKind.Idle:
                    break;

                case PacketKind.ParameterSets:
                    StartDecoder(payload, length);
                    break;

                case PacketKind.Frame:
                case PacketKind.KeyFrame:
                    Frames++;
                    Feed(payload, length);
                    break;
            }
        }
    }

    /// <summary>
    /// Aus dem avcC die Parametersätze holen und den Dekodierer damit anwerfen.
    /// </summary>
    /// <remarks>
    /// <b>Die Größe kommt aus dem SPS, nicht aus dem Kopf.</b> Der Kopf ist ein
    /// Anfangshinweis für die Fenstergröße; maßgeblich ist, was der Kodierer wirklich
    /// erzeugt hat. Der Unterschied ist einmal teuer geworden: aus dem Kopf gelesen
    /// wurde ein 1080p-Bild auf 1440p hochgerechnet, und das sah aus wie eine schlechte
    /// Verbindung.
    /// </remarks>
    private void StartDecoder(byte[] record, int length)
    {
        _avcc = record[..length];
        Rebuild();
    }

    private byte[]? _avcc;
    private Size _decodingAt;

    /// <summary>
    /// Die Zielgröße hat sich geändert — der Dekodierer wird darauf neu gestellt.
    /// </summary>
    /// <remarks>
    /// Die Größe steht in den ffmpeg-Argumenten und lässt sich im Lauf nicht ändern; es
    /// bleibt nur ein neuer Lauf. Die Parametersätze sind gemerkt, es braucht also kein
    /// neues Art-1-Paket — wohl aber ein Schlüsselbild, und darum bittet der Aufrufer.
    /// </remarks>
    public bool Retarget(Size target)
    {
        Target = target;

        if (_decoder is null || _avcc is null) return false;
        if (!Differs(_decodingAt, Fit(target))) return false;

        _log($"[Mac] Zielfläche {target.Width}×{target.Height} — der Dekodierer wird darauf neu gestellt");
        Rebuild();
        return true;
    }

    /// <summary>Unter einem Zehntel Unterschied lohnt der Neuaufbau nicht.</summary>
    private static bool Differs(Size a, Size b) =>
        Math.Abs(a.Width - b.Width) > Math.Max(8, a.Width / 10)
        || Math.Abs(a.Height - b.Height) > Math.Max(8, a.Height / 10);

    /// <summary>
    /// Die Quelle in die Zielfläche einpassen, ohne sie zu verzerren und ohne sie
    /// hochzurechnen.
    /// </summary>
    /// <remarks>
    /// Hochrechnen wäre verschenkte Arbeit — mehr Bildpunkte, als die Quelle hat, entstehen
    /// dabei nicht. Gerade Kantenlängen, weil 4:2:0 auf ungeraden stolpert.
    /// </remarks>
    private Size Fit(Size box)
    {
        if (Width <= 0 || Height <= 0) return new Size(Math.Max(2, Width), Math.Max(2, Height));

        // Ohne brauchbare Zielfläche nicht einfach die Quellgröße durchreichen: die
        // stammt aus dem SPS der Gegenseite und darf nicht bestimmen, wie viel
        // Speicher hier entsteht. Mehr als 4K zeigt ohnehin kein Fenster.
        if (box.Width < 16 || box.Height < 16) box = new Size(3840, 2160);

        var scale = Math.Min(1.0, Math.Min(box.Width / (double)Width, box.Height / (double)Height));
        var w = Math.Max(2, (int)Math.Round(Width * scale) & ~1);
        var h = Math.Max(2, (int)Math.Round(Height * scale) & ~1);
        return new Size(w, h);
    }

    private void Rebuild()
    {
        var record = _avcc;
        if (record is null) return;

        var length = record.Length;

        if (ParseAvcc(record, length) is not { } parsed)
        {
            _log("[Mac] avcC unlesbar — ohne Parametersätze gibt es kein Bild");
            return;
        }

        var (sps, pps, lengthSize) = parsed;
        _lengthSize = lengthSize;

        var width = Width;
        var height = Height;

        if (H264Sps.TryGetSize(sps, out var spsWidth, out var spsHeight))
        {
            if (spsWidth != Width || spsHeight != Height)
                _log($"[Mac] das SPS sagt {spsWidth}×{spsHeight}, der Kopf {Width}×{Height} — das SPS gilt");

            width = spsWidth;
            height = spsHeight;
        }

        Width = width;
        Height = height;

        // Die Zielfläche entscheidet, was ffmpeg liefert. Ist keine gesetzt, bleibt es bei
        // der Quellgröße.
        var output = Fit(Target);

        _decoder?.Dispose();
        _decodingAt = output;
        _decoder = new Decoder(output.Width, output.Height, frame => Sink?.Invoke(frame), _log);

        _log($"[Mac] Format steht: avcC {length} Byte, Quelle {width}×{height}, "
             + $"dekodiert auf {output.Width}×{output.Height}, Längenfeld {lengthSize} Byte");

        Format?.Invoke(output.Width, output.Height);

        // SPS und PPS als Annex B voranstellen — ohne sie fängt ffmpeg mit dem ersten
        // Bild nichts an.
        var start = new byte[StartCode.Length * 2 + sps.Length + pps.Length];
        var at = 0;
        StartCode.CopyTo(start, at); at += 4;
        sps.CopyTo(start, at); at += sps.Length;
        StartCode.CopyTo(start, at); at += 4;
        pps.CopyTo(start, at);

        _decoder.Write(start, start.Length, isFrame: false);
    }

    private int _lengthSize = 4;

    private void Feed(byte[] payload, int length)
    {
        var decoder = _decoder;
        if (decoder is null) return;

        if (_lengthSize == 4)
        {
            // Vier Byte Länge werden zu vier Byte Startcode — dieselbe Größe, also im
            // selben Puffer und ohne Kopie.
            var at = 0;
            while (at + 4 <= length)
            {
                var n = BinaryPrimitives.ReadInt32BigEndian(payload.AsSpan(at));
                if (n < 0 || at + 4 + n > length) break;

                payload[at] = 0; payload[at + 1] = 0; payload[at + 2] = 0; payload[at + 3] = 1;
                at += 4 + n;
            }

            decoder.Write(payload, length);
            return;
        }

        // Andere Längenfeldbreiten kommen laut Protokoll nicht vor. Sie hier trotzdem
        // zu behandeln kostet eine Kopie und erspart im Ernstfall die Suche.
        using var stream = new MemoryStream(length + 16);
        var pos = 0;
        while (pos + _lengthSize <= length)
        {
            var n = 0;
            for (var i = 0; i < _lengthSize; i++) n = (n << 8) | payload[pos + i];
            pos += _lengthSize;
            if (n < 0 || pos + n > length) break;

            stream.Write(StartCode);
            stream.Write(payload, pos, n);
            pos += n;
        }

        var buffer = stream.GetBuffer();
        decoder.Write(buffer, (int)stream.Length);
    }

    /// <summary>
    /// Ein avcC-Datensatz: Profil, Stufe, Längenfeldbreite, dann SPS und PPS.
    /// </summary>
    /// <remarks>
    /// Aufbau: Byte 0 ist die Version (1), 1 bis 3 sind Profil, Kompatibilität und Stufe,
    /// Byte 4 hat in den unteren zwei Bit die Längenfeldbreite minus eins, Byte 5 in den
    /// unteren fünf Bit die Anzahl der SPS. Dann je SPS zwei Byte Länge und die Daten,
    /// danach ein Byte Anzahl der PPS und dieselbe Form.
    /// </remarks>
    private static (byte[] Sps, byte[] Pps, int LengthSize)? ParseAvcc(byte[] record, int length)
    {
        try
        {
            if (length < 7 || record[0] != 1) return null;

            var lengthSize = (record[4] & 0x03) + 1;
            var at = 5;

            var spsCount = record[at++] & 0x1F;
            byte[]? sps = null;

            for (var i = 0; i < spsCount; i++)
            {
                var n = BinaryPrimitives.ReadUInt16BigEndian(record.AsSpan(at)); at += 2;
                if (at + n > length) return null;
                sps ??= record[at..(at + n)];
                at += n;
            }

            if (at >= length) return null;

            var ppsCount = record[at++];
            byte[]? pps = null;

            for (var i = 0; i < ppsCount; i++)
            {
                var n = BinaryPrimitives.ReadUInt16BigEndian(record.AsSpan(at)); at += 2;
                if (at + n > length) return null;
                pps ??= record[at..(at + n)];
                at += n;
            }

            return sps is null || pps is null ? null : (sps, pps, lengthSize);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void ReadFully(Stream stream, byte[] buffer, int count)
    {
        if (!TryReadFully(stream, buffer, count))
            throw new IOException("Die Verbindung endete mitten in einem Paket");
    }

    private static bool TryReadFully(Stream stream, byte[] buffer, int count)
    {
        var read = 0;
        while (read < count)
        {
            var n = stream.Read(buffer, read, count - read);
            if (n == 0) return false;
            read += n;
        }

        return true;
    }

    private static string FirstLine(string text)
    {
        var at = text.IndexOf('\n');
        return at < 0 ? text : text[..at].Trim();
    }

    public void Dispose()
    {
        Stop();
        _decoder?.Dispose();
        _one.Dispose();
    }
}
