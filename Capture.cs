using System.Buffers.Binary;
using System.Diagnostics;

namespace ScreenBridge;

/// <summary>Was aus dem Kodierer herauskommt: Kopfdaten oder ein Bild.</summary>
internal enum PacketKind : byte
{
    Idle = 0,
    ParameterSets = 1,
    Frame = 2,
    KeyFrame = 3,
}

/// <summary>
/// Nimmt den Bildschirm auf, kodiert ihn und zerlegt das Ergebnis in Pakete.
/// </summary>
/// <remarks>
/// <para>
/// <c>ddagrab</c> greift über die Desktop Duplication API direkt auf der Grafikkarte ab,
/// <c>h264_nvenc</c> kodiert dort weiter — kein Bild sieht den Hauptspeicher, bis es
/// fertig kodiert ist.
/// </para>
/// <para>
/// <b>Die Bildgrenzen kommen aus dem Behälter, nicht aus einer Suche im Strom.</b>
/// Geschrieben wird fragmentiertes MP4 mit <c>+frag_every_frame</c>; daraus entsteht ein
/// <c>moof</c>+<c>mdat</c>-Paar je Bild, jedes mit ausdrücklicher Länge — nachgemessen
/// 118 Paare für 118 Bilder. Der <c>mdat</c>-Inhalt ist bereits AVCC, also genau das
/// Format, das VideoToolbox auf der Mac-Seite will.
/// </para>
/// <para>
/// Das ist die Lehre aus dem AirPlay-Empfänger, und dort kostete ihr Fehlen eine ganze
/// Bildlänge: ein roher H.264-Strom hat keine Paketgrenzen, und ein Zerleger gibt ein
/// Bild erst heraus, wenn das nächste anfängt.
/// </para>
/// </remarks>
internal sealed class Capture : IDisposable
{
    /// <summary>Nach einem Neustart des Kodierers ist das erste Bild ein Schlüsselbild.</summary>
    private static readonly TimeSpan RestartCooldown = TimeSpan.FromSeconds(3);

    private readonly Action<string> _log;
    private readonly Action<PacketKind, byte[], long> _onPacket;
    private readonly CancellationTokenSource _cts = new();

    private Process? _ffmpeg;
    private Thread? _reader;
    private byte[]? _avcC;
    private DateTime _lastStart = DateTime.MinValue;
    private readonly object _gate = new();

    public Capture(int screenIndex, int fps, Action<string> log, Action<PacketKind, byte[], long> onPacket)
    {
        ScreenIndex = screenIndex;
        Fps = fps;
        _log = log;
        _onPacket = onPacket;
    }

    public int ScreenIndex { get; }
    public int Fps { get; }

    /// <summary>Wie viele Bilder und Bytes bisher herausgingen.</summary>
    public long Frames { get; private set; }
    public long Bytes { get; private set; }

    /// <summary>
    /// Die letzte Taktmessung, damit sie im Fenster stehen kann statt nur im Protokoll.
    /// </summary>
    /// <remarks>
    /// Getrennt nach allen Bildern und nur denen mit Inhalt — aus demselben Grund, aus
    /// dem <see cref="Measure"/> trennt: solange <c>ddagrab</c> auffüllt, misst ein
    /// gemeinsamer Wert, wie unregelmäßig Platzhalter nachkommen, und nicht, ob eine
    /// Bewegung gleichmäßig aussieht. Sichtbar ist allein die zweite Gruppe.
    /// </remarks>
    public double Beat { get; private set; }
    public double Jitter { get; private set; }
    public double MaxGap { get; private set; }
    public int ContentFrames { get; private set; }
    public double ContentBeat { get; private set; }
    public double ContentJitter { get; private set; }
    public int MedianSize { get; private set; }

    public void Start() => StartEncoder("Aufnahme beginnt");

    /// <summary>
    /// Ein Schlüsselbild erzwingen.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Umgesetzt als Neustart des Kodierers</b>, und das ist eine Abweichung vom
    /// Vertrag, die eine Begründung verdient: die ffmpeg-Befehlszeile kennt keine
    /// Steuerung zur Laufzeit. Ein Schlüsselbild auf Zuruf gibt es dort nicht.
    /// </para>
    /// <para>
    /// Praktisch trifft das kaum: der Kodierer läuft ohnehin erst, wenn sich der Mac
    /// verbindet, und das erste Bild eines Laufs ist immer ein Schlüsselbild. Die
    /// Anforderung beim Verbinden ist damit von selbst erfüllt. Bleibt der Fall, dass
    /// der Mac mitten im Betrieb den Faden verliert — dann kostet es einen Neustart von
    /// einigen hundert Millisekunden, und der ist höchstens alle drei Sekunden möglich,
    /// damit eine kaputte Gegenseite nicht dauernd neu startet.
    /// </para>
    /// </remarks>
    public void RequestKeyFrame()
    {
        lock (_gate)
        {
            if (DateTime.UtcNow - _lastStart < RestartCooldown) return;
        }

        StartEncoder("Schlüsselbild angefordert");
    }

    private void StartEncoder(string reason)
    {
        lock (_gate)
        {
            StopEncoder();
            _lastStart = DateTime.UtcNow;
            _avcC = null;

            var info = new ProcessStartInfo("ffmpeg")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            foreach (var a in Arguments()) info.ArgumentList.Add(a);

            try
            {
                _ffmpeg = Process.Start(info) ?? throw new InvalidOperationException("ffmpeg ließ sich nicht starten.");
            }
            catch (Exception ex)
            {
                _log($"FEHLER: ffmpeg ließ sich nicht starten — {ex.Message}");
                return;
            }

            _log($"{reason}: Bildschirm {ScreenIndex}, {Fps} Bilder/s");

            var ffmpeg = _ffmpeg;
            _reader = new Thread(() => ReadBoxes(ffmpeg)) { IsBackground = true, Name = "ScreenBridge-Aufnahme" };
            _reader.Start();

            _ = Task.Run(() => DrainErrors(ffmpeg));
        }
    }

    private IEnumerable<string> Arguments() =>
    [
        "-hide_banner", "-loglevel", "error",
        // dup_frames=0: nur senden, was sich wirklich geändert hat.
        //
        // Voreingestellt füllt ddagrab auf die eingestellte Bildrate auf, indem es das
        // letzte Bild wiederholt — und tut das in Stößen. Nachgemessen: Takt
        // 17,0 ± 19,7 ms bei einem Median von 125 Byte je Bild, also fast nur
        // Wiederholungen; kleinster Abstand 1,8 ms, größter 93,7. Derselbe Kodierer
        // liefert an einer Quelle mit ständig neuem Inhalt 2,4 ± 0,1 ms — die
        // Ungleichmäßigkeit stammt also allein aus den Verdopplungen.
        //
        // Sie kosten Bandbreite und verfälschen jede Messung, und zu sehen ist von
        // ihnen nichts: ein wiederholtes Bild zeigt dasselbe wie das vorige.
        // draw_mouse=0: der Zeiger wird NICHT mitaufgenommen.
        //
        // Er wäre sonst Teil des Bildes und damit die ganze Kette zu spät — Aufnehmen,
        // Kodieren, Netz, Dekodieren, Zeichnen, zusammen 50 bis 90 ms. Nachlaufen darf
        // der Inhalt; der Zeiger nicht, denn an ihm misst die Hand, ob der Rechner
        // reagiert. Die Mac-Seite zeigt stattdessen ihren eigenen, und der ist sofort da.
        //
        // Nachgemessen in der Gegenrichtung: derselbe Wechsel hat aus „fühlt sich versetzt
        // an“ ein „perfekt“ gemacht, ohne dass eine einzige Millisekunde verschwunden wäre.
        "-f", "lavfi", "-i", $"ddagrab=output_idx={ScreenIndex}:framerate={Fps}:draw_mouse=0:dup_frames=0",
        "-c:v", "h264_nvenc", "-preset", "p1", "-tune", "ull",
        // VBR, nicht CBR: nachgemessen kostete CBR am selben Bild 22,4 Mbit/s mit
        // 4,6 MB reiner Füllbytes, VBR 3,7 Mbit/s ohne eine einzige.
        "-rc", "vbr", "-b:v", "12M", "-maxrate", "40M", "-bufsize", "1M",
        // Weiter Schlüsselbildabstand als Netz; angefordert wird über den Rückkanal.
        // Ein Schlüsselbild im Sekundentakt erzeugt im Sekundentakt einen Ausschlag
        // in der Datenrate — und damit genau die ungleichmäßige Ankunft, die als
        // Ruckeln zu sehen ist.
        "-g", "300", "-forced-idr", "1",
        // Ohne B-Bilder ist Übertragungs- gleich Anzeigereihenfolge.
        "-bf", "0", "-delay", "0",
        "-f", "mp4",
        "-movflags", "+frag_keyframe+empty_moov+default_base_moof+frag_every_frame",
        "pipe:1",
    ];

    // ------------------------------------------------------------------ MP4

    private void ReadBoxes(Process ffmpeg)
    {
        var stream = ffmpeg.StandardOutput.BaseStream;
        var header = new byte[8];

        try
        {
            while (!_cts.IsCancellationRequested)
            {
                if (!ReadExactly(stream, header, 8)) return;

                var size = (int)BinaryPrimitives.ReadUInt32BigEndian(header);
                var type = System.Text.Encoding.ASCII.GetString(header, 4, 4);

                if (size < 8 || size > 64 * 1024 * 1024)
                {
                    _log($"unsinnige Boxlänge {size} bei „{type}“ — Aufnahme endet");
                    return;
                }

                var body = new byte[size - 8];
                if (!ReadExactly(stream, body, body.Length)) return;

                switch (type)
                {
                    case "moov": ReadMoov(body); break;
                    case "mdat": ReadFrame(body); break;
                    default: break;   // ftyp, moof, mfra — nichts davon brauchen wir
                }
            }
        }
        catch (Exception ex)
        {
            if (!_cts.IsCancellationRequested) _log($"Aufnahme endete: {ex.Message}");
        }
    }

    /// <summary>
    /// Die <c>avcC</c>-Box aus dem <c>moov</c> holen.
    /// </summary>
    /// <remarks>
    /// Gesucht wird nach dem Namen, statt den Baum aus trak, mdia, minf, stbl, stsd und
    /// avc1 hinabzusteigen. Das ist zulässig, weil wir den Erzeuger kennen: es ist unser
    /// eigener ffmpeg-Aufruf mit genau einer Videospur, und ein zweites „avcC“ kann darin
    /// nicht vorkommen.
    /// </remarks>
    private void ReadMoov(byte[] moov)
    {
        var at = IndexOf(moov, "avcC"u8);
        if (at < 4)
        {
            _log("kein avcC im moov — die Mac-Seite bekommt kein Format");
            return;
        }

        var size = (int)BinaryPrimitives.ReadUInt32BigEndian(moov.AsSpan(at - 4));
        if (size < 8 || at - 4 + size > moov.Length)
        {
            _log("avcC-Länge unplausibel");
            return;
        }

        var record = moov[(at + 4)..(at - 4 + size)];
        _avcC = record;

        _log($"Format steht: avcC {record.Length} Byte");
    }

    private void ReadFrame(byte[] mdat)
    {
        var sets = _avcC;
        var (isKey, cleaned) = Inspect(mdat);

        // Vor jedem Schlüsselbild die Kopfdaten, damit die Mac-Seite jederzeit
        // aufsetzen kann — auch nach einem Wechsel der Auflösung.
        if (isKey && sets is not null) _onPacket(PacketKind.ParameterSets, sets, Elapsed());

        _onPacket(isKey ? PacketKind.KeyFrame : PacketKind.Frame, cleaned, Elapsed());

        Frames++;
        Bytes += cleaned.Length;

        Measure(cleaned.Length);
    }

    /// <summary>
    /// Wie gleichmäßig die Bilder entstehen — getrennt nach solchen mit viel und mit
    /// wenig Inhalt.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Die Trennung ist der Kern, und ohne sie sind wir schon einmal in die Irre
    /// gelaufen: solange <c>ddagrab</c> auf die Bildrate auffüllte, waren drei von vier
    /// Bildern bloße Wiederholungen von 125 Byte. Ein gemeinsamer Mittelwert darüber
    /// maß, wie unregelmäßig Platzhalter nachgereicht werden — und nicht, ob eine
    /// Bewegung gleichmäßig aussieht.
    /// </para>
    /// <para>
    /// Sichtbar ist allein die zweite Gruppe. Die Schwelle ist <b>absolut</b>, nicht
    /// relativ zum Median: eine Wiederholung ist ein paar hundert Byte groß, und das ist
    /// das verlässliche Merkmal. Ein Vielfaches des Medians wäre die naheliegende Wahl
    /// und die falsche — bei durchgehender Bewegung sind alle Bilder etwa gleich groß,
    /// der dreifache Median läge dann über allen, und die Gruppe wäre leer. Sie fiele
    /// genau in dem Fall aus, für den es sie gibt.
    /// </para>
    /// </remarks>
    private void Measure(int size)
    {
        var now = _clock.Elapsed.TotalMilliseconds;
        if (_lastFrameAt > 0) _recent.Add((now - _lastFrameAt, size));
        _lastFrameAt = now;

        if (_recent.Count > 240) _recent.RemoveAt(0);
        if (_recent.Count < 30 || now - _lastReport < 5000) return;

        _lastReport = (long)now;

        var sizes = _recent.Select(r => r.Size).Order().ToArray();
        var median = sizes[sizes.Length / 2];
        var threshold = Math.Max(1000, median / 4);
        var rich = _recent.Where(r => r.Size > threshold).Select(r => r.Gap).ToArray();

        var all = _recent.Select(r => r.Gap).ToArray();
        MedianSize = median;
        (Beat, Jitter, MaxGap) = Spread(all);
        ContentFrames = rich.Length;
        if (rich.Length >= 5) (ContentBeat, ContentJitter, _) = Spread(rich);

        _log($"[Takt] {_recent.Count} Bilder {Describe(_recent.Select(r => r.Gap))}"
             + $" · Median {median} Byte"
             + (rich.Length >= 5 ? $" · davon {rich.Length} mit Inhalt {Describe(rich)}" : ""));
    }

    private static (double Mean, double Spread, double Max) Spread(IReadOnlyCollection<double> values)
    {
        var mean = values.Average();
        return (mean, Math.Sqrt(values.Sum(v => (v - mean) * (v - mean)) / values.Count), values.Max());
    }

    private static string Describe(IEnumerable<double> values)
    {
        var (mean, spread, max) = Spread(values.ToArray());
        return $"Takt {mean:0.0}±{spread:0.0} ms, größte Lücke {max:0.0} ms";
    }

    /// <summary>
    /// Nachsehen, ob ein IDR darin steckt, und Füll-NALs entfernen.
    /// </summary>
    /// <remarks>
    /// Der Einstiegspunkt steht sonst nur in den Kopfdaten des Fragments, und die
    /// schicken wir nicht mit. Ihn hier zu lesen ist billig — die Zerlegung liegt
    /// ohnehin vor — und erspart der Mac-Seite dieselbe Arbeit.
    ///
    /// Füll-NALs (Art 12) entstehen bei CBR und sind reine Polsterung. Unter VBR kommen
    /// keine vor; die Prüfung kostet nichts und kopiert nur, wenn wirklich welche da sind.
    /// </remarks>
    private static (bool IsKey, byte[] Data) Inspect(byte[] au)
    {
        var isKey = false;
        var filler = false;
        var offset = 0;

        while (offset + 4 <= au.Length)
        {
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(au.AsSpan(offset));
            if (length <= 0 || offset + 4 + length > au.Length) break;

            var kind = au[offset + 4] & 0x1F;
            if (kind == 5) isKey = true;
            if (kind == 12) filler = true;

            offset += 4 + length;
        }

        if (!filler) return (isKey, au);

        var output = new MemoryStream(au.Length);
        offset = 0;
        while (offset + 4 <= au.Length)
        {
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(au.AsSpan(offset));
            if (length <= 0 || offset + 4 + length > au.Length) break;

            if ((au[offset + 4] & 0x1F) != 12) output.Write(au, offset, 4 + length);
            offset += 4 + length;
        }

        return (isKey, output.ToArray());
    }

    private readonly Stopwatch _clock = Stopwatch.StartNew();

    /// <summary>Abstand und Größe der letzten Bilder — für die Taktmessung.</summary>
    private readonly List<(double Gap, int Size)> _recent = [];
    private double _lastFrameAt;
    private long _lastReport;

    private long Elapsed() => _clock.ElapsedTicks * 1_000_000 / Stopwatch.Frequency;

    private static int IndexOf(byte[] haystack, ReadOnlySpan<byte> needle)
    {
        for (var i = 0; i + needle.Length <= haystack.Length; i++)
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle)) return i;

        return -1;
    }

    private bool ReadExactly(Stream stream, byte[] buffer, int count)
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

    private void DrainErrors(Process ffmpeg)
    {
        try
        {
            while (ffmpeg.StandardError.ReadLine() is { } line)
                if (line.Length > 0) _log($"[ffmpeg] {line}");
        }
        catch (Exception) { }
    }

    private void StopEncoder()
    {
        var ffmpeg = _ffmpeg;
        _ffmpeg = null;
        if (ffmpeg is null) return;

        try { if (!ffmpeg.HasExited) ffmpeg.Kill(entireProcessTree: true); } catch { }
        try { ffmpeg.Dispose(); } catch { }
    }

    public void Dispose()
    {
        _cts.Cancel();
        lock (_gate) StopEncoder();
        _cts.Dispose();
    }
}
