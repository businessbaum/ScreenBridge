using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;

namespace ScreenBridge;

/// <summary>
/// Dekodiert den H.264-Strom über ffmpeg und reicht fertige Bilder weiter.
/// </summary>
/// <remarks>
/// <para>
/// ffmpeg bekommt den Annex-B-Strom auf die Standardeingabe und liefert rohe
/// BGRA-Bilder zurück. Warum ffmpeg und kein eigener Dekodierer: H.264 selbst zu
/// dekodieren ist ein eigenes Projekt, und ffmpeg liegt ohnehin auf dem Rechner.
/// </para>
/// <para>
/// <b>Zwei Vorgaben von ffmpeg sind für eine Fernsteuerung die falschen, und keine
/// davon sitzt, wo man sie vermutet.</b> Gemessen wurde jeweils, indem die echte
/// Aufnahme im echten Takt eingespeist und für jedes Bild getrennt genommen wurde, wann
/// das erste und wann das letzte Byte ankommt — jede Einstellung zweimal.
/// </para>
/// <para>
/// <b>Erstens <c>-threads 1</c>, und zwar hinter <c>-i</c>.</b> Alles davor —
/// <c>-thread_type slice</c>, <c>-threads 1</c>, beides zusammen — ändert nichts:
/// 38,2 ms in jeder Fassung. Dahinter gesetzt fällt das Warten von 32,5 auf
/// <b>2,3 ms</b>. Die Verzögerung sitzt in der Ausgabeseite, nicht im Dekodierer.
/// Schon <c>-threads 2</c> holt eine ganze Bildlänge zurück (22,7 ms).
/// </para>
/// <para>
/// <b>Zweitens der Rückweg.</b> Eine umgeleitete Standardausgabe ist eine namenlose
/// Rohrleitung mit 4 KB Puffer; ein rohes 1440p-Bild von 14,7 MB kommt darin in
/// 3600 Stücken an. Das kostete 16,6 ms allein fürs Durchreichen — das ganze Zeitbudget
/// eines Bildes — und die Leitung lief über: <b>115,8 ms</b> Durchlauf. Über eine
/// benannte Rohrleitung mit großem Puffer sind es <b>10,4 ms</b>. Bei 1080p 8,2 → 4,1,
/// bei 1292p 17,5 → 8,6.
/// </para>
/// <para>
/// <c>-fflags nobuffer</c> gehört nicht dazu: damit liefert ffmpeg aus diesem Strom
/// überhaupt keine Bilder — nachgemessen, auch gegen eine nachweislich gute Datei.
/// </para>
/// </remarks>
internal sealed class Decoder : IDisposable
{
    /// <summary>
    /// Der Puffer der Rückleitung. Ein 1440p-Bild sind 14,7 MB; zwei davon reichen,
    /// damit ffmpeg nie auf uns warten muss.
    /// </summary>
    private static readonly int[] BufferSizes = [32 * 1024 * 1024, 8 * 1024 * 1024, 1024 * 1024];

    /// <summary>
    /// Die Abschlussmarke hinter jedem Bild: ein Access Unit Delimiter (NAL-Art 9).
    /// </summary>
    /// <remarks>
    /// Ein roher H.264-Strom hat keine Paketgrenzen. Der Zerleger in ffmpeg erkennt das
    /// Ende eines Bildes deshalb erst daran, dass das nächste anfängt — und hält es bis
    /// dahin zurück. Nachgemessen mit dem echten Strom: bei 50 Bildern je Sekunde
    /// <b>45 ms</b>, bei 5 Bildern je Sekunde <b>402 ms</b>. Mit der Marke dahinter
    /// <b>25 ms</b> und <b>206 ms</b> — genau eine Bildlänge weniger.
    /// </remarks>
    private static readonly byte[] AccessUnitDelimiter = [0x00, 0x00, 0x00, 0x01, 0x09, 0xF0];

    private readonly Process _ffmpeg;
    private readonly NamedPipeServerStream _back;
    private readonly Action<string> _log;
    private readonly Action<double, int, int, double>? _onStats;

    /// <summary>
    /// Wie viele Bytes hineingingen — für die Datenrate, die als einzige Zahl sagt,
    /// wie viel Bild der Mac tatsächlich schickt.
    /// </summary>
    private long _bytesIn;
    private readonly int _frameBytes;
    private readonly CancellationTokenSource _cts = new();

    /// <summary>
    /// Wann welches Bildpaket hineinging und wie lange davor nichts kam — zum
    /// Nachmessen der Durchlaufzeit.
    /// </summary>
    private readonly ConcurrentQueue<(long At, long Gap)> _sent = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private long _lastWrite;

    /// <summary>Die letzten Messwerte, aus denen der stetige Wert gebildet wird.</summary>
    private readonly List<int> _recent = [];
    private readonly List<long> _gaps = [];
    private readonly object _recentGate = new();

    public Decoder(int width, int height, Action<byte[]> onFrame, Action<string> log,
                   Action<double, int, int, double>? onStats = null)
    {
        _log = log;
        _onStats = onStats;
        _frameBytes = width * height * 4;

        var name = $"screenbridge-mac-{Environment.ProcessId}-{Guid.NewGuid():N}";
        _back = OpenBackChannel(name);

        var info = new ProcessStartInfo("ffmpeg")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardError = true,
        };

        foreach (var a in new[]
                 {
                     "-hide_banner", "-loglevel", "error",
                     // Nicht erst Sekunden lang in den Strom hineinsehen, bevor das
                     // erste Bild kommt — die Art steht mit "-f h264" ohnehin fest.
                     "-probesize", "512", "-analyzeduration", "0",
                     // Keine Umsortierung abwarten.
                     "-flags", "low_delay",
                     // KEINE Hardware-Dekodierung, und das ist gemessen: h264_cuvid mit
                     // -resize auf derselben Karte, die aufnimmt, kostete 23 ms Durchlauf
                     // gegen 10 ms in Software und hielt dabei ein Bild zurück (299 von 300
                     // heraus). NVDEC hat eine tiefere Fließstrecke und der Rückweg in den
                     // Hauptspeicher kostet extra. Für Durchsatz wäre es richtig, für
                     // Verzögerung ist es falsch — und hier zählt nur die Verzögerung.
                     "-f", "h264", "-i", "pipe:0",
                     "-vf", $"scale={width}:{height}",
                     // Hinter dem Eingang, und nur hier wirksam. Siehe die Anmerkung
                     // an der Klasse.
                     "-threads", "1",
                     "-fps_mode", "passthrough",
                     "-f", "rawvideo", "-pix_fmt", "bgra", "-y", $@"\\.\pipe\{name}",
                 })
        {
            info.ArgumentList.Add(a);
        }

        _ffmpeg = Process.Start(info) ?? throw new InvalidOperationException("ffmpeg ließ sich nicht starten.");

        // Ein eigener Thread mit blockierenden Lesevorgängen, kein Task.Run: bei 60
        // Bildern je Sekunde wären das sonst Zehntausende Übergaben an den Threadpool.
        new Thread(() => ReadFrames(onFrame))
        {
            IsBackground = true,
            Name = "ScreenBridge-Dekodierer",
        }.Start();

        _ = Task.Run(ReadErrorsAsync);
    }

    /// <summary>
    /// Die Rückleitung anlegen. Verweigert Windows den großen Puffer, tut es auch ein
    /// kleinerer — nur eben langsamer.
    /// </summary>
    private NamedPipeServerStream OpenBackChannel(string name)
    {
        Exception? last = null;

        foreach (var size in BufferSizes)
        {
            try
            {
                // CurrentUserOnly: Rohrnamen sind lokal aufzählbar, und wer schneller
                // wäre als ffmpeg, könnte hier sonst eigene Bilder einspeisen. So
                // verbindet nur, wer als derselbe Benutzer läuft.
                return new NamedPipeServerStream(name, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                                                 PipeOptions.CurrentUserOnly, size, 0);
            }
            catch (Exception ex) { last = ex; }
        }

        throw new InvalidOperationException("Rückleitung ließ sich nicht anlegen.", last);
    }

    /// <summary>Rohdaten an den Dekodierer geben.</summary>
    /// <param name="isFrame">
    /// Ein Bildpaket, keine Kopfdaten. Nur dafür wird die Zeit genommen — sonst zählte
    /// SPS/PPS als Bild und verschöbe die Zuordnung dauerhaft um eins — und nur dahinter
    /// gehört die Abschlussmarke; hinter SPS/PPS schlösse sie eine leere Einheit.
    /// </param>
    public void Write(byte[] data, int length, bool isFrame = true)
    {
        try
        {
            var pipe = _ffmpeg.StandardInput.BaseStream;

            if (isFrame)
            {
                var now = _clock.ElapsedMilliseconds;
                _sent.Enqueue((now, _lastWrite == 0 ? 0 : now - _lastWrite));
                _lastWrite = now;
            }

            _bytesIn += length;

            pipe.Write(data, 0, length);
            if (isFrame) pipe.Write(AccessUnitDelimiter, 0, AccessUnitDelimiter.Length);
            pipe.Flush();
        }
        catch (Exception)
        {
            // ffmpeg ist weg; der Empfänger läuft weiter.
        }
    }

    /// <summary>Wie lange ein Bild zuletzt im Dekodierer lag, in Millisekunden.</summary>
    public int LastLatencyMs { get; private set; }

    /// <summary>
    /// Die Durchlaufzeit im laufenden Betrieb: der mittlere Wert der letzten Bilder,
    /// die einem anderen dicht folgten.
    /// </summary>
    /// <remarks>
    /// Der rohe Wert schwankt zwischen 39 ms und 17 Sekunden, und beides stimmt. Ein
    /// roher H.264-Strom hat keine Paketgrenzen: ein Bild kommt erst heraus, wenn das
    /// nächste anfängt. Schickt der Mac nichts, weil sich auf dem Schirm nichts rührt,
    /// liegt das letzte Bild eben liegen — zu sehen ist davon nichts, denn es gibt
    /// nichts Neues zu zeigen. Angezeigt wird deshalb nur, was bei laufender Übertragung
    /// gilt: Bilder, vor denen höchstens 100 ms Pause lag.
    /// </remarks>
    public int SteadyLatencyMs
    {
        get
        {
            lock (_recentGate)
            {
                if (_recent.Count < 3) return LastLatencyMs;
                var sorted = _recent.Order().ToArray();
                return sorted[sorted.Length / 2];
            }
        }
    }

    /// <summary>Wie viele Bilder gerade im Dekodierer stecken.</summary>
    public int InFlight => _sent.Count;

    private void Record(int latencyMs, long gapMs)
    {
        if (gapMs > 100) return;   // Stillstand sagt nichts über den Durchlauf

        lock (_recentGate)
        {
            _recent.Add(latencyMs);
            if (_recent.Count > 60) _recent.RemoveAt(0);

            _gaps.Add(gapMs);
            if (_gaps.Count > 60) _gaps.RemoveAt(0);
        }
    }

    /// <summary>
    /// Wie ungleichmäßig die Bilder eintreffen — die Streuung der Abstände, in
    /// Millisekunden.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Die reine Verzögerung sagt nichts darüber, ob eine Bewegung <i>gleichmäßig</i>
    /// aussieht. Kommen die Bilder abwechselnd nach 8 und nach 25 ms, ist die mittlere
    /// Verzögerung tadellos und der Zeiger ruckelt trotzdem — er springt dann mal
    /// halb, mal anderthalb Schritte weit.
    /// </para>
    /// <para>
    /// Bei 60 Bildern je Sekunde ist ein Abstand von 16,7 ms der Sollwert. Eine
    /// Streuung von wenigen Millisekunden ist unauffällig; wird sie so groß wie der
    /// Abstand selbst, liegt die Ursache des Ruckelns im Weg des Bildes und nicht in
    /// der Steuerung.
    /// </para>
    /// </remarks>
    public int JitterMs
    {
        get
        {
            lock (_recentGate)
            {
                if (_gaps.Count < 5) return 0;

                var mean = _gaps.Average();
                var variance = _gaps.Sum(g => (g - mean) * (g - mean)) / _gaps.Count;
                return (int)Math.Round(Math.Sqrt(variance));
            }
        }
    }

    /// <summary>Der mittlere Abstand zwischen zwei Bildern, in Millisekunden.</summary>
    public int BeatMs
    {
        get
        {
            lock (_recentGate) return _gaps.Count < 5 ? 0 : (int)Math.Round(_gaps.Average());
        }
    }

    /// <summary>
    /// Die größte einzelne Lücke zwischen zwei Bildern, in Millisekunden.
    /// </summary>
    /// <remarks>
    /// Neben der Streuung, weil sie etwas anderes zeigt: ein einzelnes Stocken von
    /// 200 ms verschwindet in einer Standardabweichung über sechzig Werte fast
    /// vollständig — zu sehen ist es trotzdem. Beide Zahlen zusammen unterscheiden
    /// "durchgehend leicht ungleichmäßig" von "meistens sauber, gelegentlich ein
    /// Aussetzer".
    /// </remarks>
    public int MaxGapMs
    {
        get
        {
            lock (_recentGate) return _gaps.Count < 5 ? 0 : (int)_gaps.Max();
        }
    }

    private void ReadFrames(Action<byte[]> onFrame)
    {
        // ffmpeg öffnet seinen Ausgang erst, wenn Eingabedaten da sind. Das Warten
        // gehört deshalb hierher und nicht vor den ersten Schreibvorgang.
        try
        {
            if (!_back.WaitForConnectionAsync(_cts.Token).Wait(TimeSpan.FromSeconds(10)))
            {
                _log("[Mac] ffmpeg hat die Rückleitung nicht geöffnet");
                return;
            }
        }
        catch (Exception ex)
        {
            _log($"[Mac] Rückleitung: {ex.Message}");
            return;
        }

        var frame = new byte[_frameBytes];
        var count = 0;
        var lastReport = 0L;
        var lastLogged = 0L;
        var lastCount = 0;
        var lastBytes = 0L;

        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var read = 0;
                while (read < _frameBytes)
                {
                    var n = _back.Read(frame, read, _frameBytes - read);
                    if (n == 0) return;
                    read += n;
                }

                if (_sent.TryDequeue(out var sent))
                {
                    LastLatencyMs = (int)(_clock.ElapsedMilliseconds - sent.At);
                    Record(LastLatencyMs, sent.Gap);
                }

                onFrame(frame);
                count++;

                var now = _clock.ElapsedMilliseconds;
                if (count == 1 || now - lastReport >= 1000)
                {
                    var fps = lastReport == 0 ? 0 : (count - lastCount) * 1000.0 / (now - lastReport);
                    var mbits = lastReport == 0 ? 0 : (_bytesIn - lastBytes) * 8.0 / (now - lastReport) / 1000.0;
                    _onStats?.Invoke(fps, SteadyLatencyMs, JitterMs, mbits);

                    // Ins Protokoll seltener als in die Anzeige — sonst steht dort
                    // nichts anderes mehr.
                    if (count == 1 || now - lastLogged >= 5000)
                    {
                        _log($"[Mac] {count} Bilder · {fps:0} je Sekunde · Durchlauf {SteadyLatencyMs} ms (roh {LastLatencyMs}) · Takt {BeatMs}±{JitterMs} ms, größte Lücke {MaxGapMs} ms · {mbits:0.00} Mbit/s · {InFlight} im Dekodierer");
                        lastLogged = now;
                    }

                    lastReport = now;
                    lastCount = count;
                    lastBytes = _bytesIn;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex) { _log($"[Mac] Dekodierer endete: {ex.Message}"); }
    }

    private async Task ReadErrorsAsync()
    {
        try
        {
            while (await _ffmpeg.StandardError.ReadLineAsync(_cts.Token) is { } line)
            {
                if (line.Length > 0) _log($"[ffmpeg] {line}");
            }
        }
        catch (Exception) { }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _ffmpeg.StandardInput.Close(); } catch { }
        try { if (!_ffmpeg.WaitForExit(1000)) _ffmpeg.Kill(entireProcessTree: true); } catch { }
        try { _back.Dispose(); } catch { }
        _ffmpeg.Dispose();
        _cts.Dispose();
    }
}
