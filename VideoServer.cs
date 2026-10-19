using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace ScreenBridge;

/// <summary>
/// Schickt das Bild an die Mac-App — im gesicherten Kanal, Kennung „V“.
/// </summary>
/// <remarks>
/// <para>
/// Die Aufnahme läuft <b>nur, solange jemand zusieht</b>. Das spart nicht nur Strom und
/// Grafikkarte — es löst auch die Anforderung eines Schlüsselbildes beim Verbinden von
/// selbst: das erste Bild eines Kodierlaufs ist immer eines.
/// </para>
/// <para>
/// Geschrieben wird auf einem eigenen Thread mit einer Warteschlange dazwischen. Ein
/// blockierender Schreibvorgang würde sonst die Aufnahme anhalten, und ffmpeg staut sich
/// dahinter auf. Kommt die Leitung nicht nach, werden <b>Bilder verworfen statt
/// angestaut</b> — ein Rückstau macht aus Verzögerung nur mehr Verzögerung.
/// </para>
/// </remarks>
internal sealed class VideoServer : IDisposable
{
    /// <summary>Mehr als das kann kein Bild sein; die Gegenseite prüft dieselbe Grenze.</summary>
    private const int MaxPayload = 16 * 1024 * 1024;

    /// <summary>So viele Bilder dürfen warten, bevor das älteste fällt.</summary>
    private const int Backlog = 4;

    private readonly Action<string> _log;
    private readonly Func<int> _screen;
    private readonly int _fps;
    private readonly CancellationTokenSource _cts = new();

    private Capture? _capture;
    private Stream? _stream;
    private readonly Queue<(PacketKind Kind, byte[] Data, long At)> _queue = new();
    private readonly object _gate = new();
    private readonly AutoResetEvent _signal = new(false);
    private Thread? _sender;

    private int _dropped;

    public VideoServer(Func<int> screen, int fps, Action<string> log)
    {
        _screen = screen;
        _fps = fps;
        _log = log;
    }

    /// <summary>Ist gerade ein Mac verbunden?</summary>
    public bool Connected { get; private set; }

    public event Action<bool>? ConnectionChanged;

    public long Frames => _capture?.Frames ?? 0;
    public long Bytes => _capture?.Bytes ?? 0;
    public int Dropped => _dropped;

    /// <summary>Was gerade übertragen wird — für die Anzeige, nicht für die Rechnung.</summary>
    public string Viewer { get; private set; } = "";
    public int Width { get; private set; }
    public int Height { get; private set; }
    public int ScreenIndex { get; private set; } = -1;
    public DateTime Since { get; private set; }
    public Capture? Encoder => _capture;

    /// <summary>
    /// Den Bildkanal von hier aus beenden.
    /// </summary>
    /// <remarks>
    /// Zwei Fälle brauchen das: „Trennen“ im Fenster und der Bildschirmwechsel. Der Kopf
    /// mit Breite und Höhe steht am Anfang einer Verbindung und gilt für sie ganz — ein
    /// anderer Bildschirm ist deshalb kein Umschalten, sondern ein neuer Kanal. Den Strom
    /// zu schließen ist zugleich das Zeichen dafür an die Gegenseite; sie muss auf keine
    /// Nachricht hören.
    /// </remarks>
    public void Stop()
    {
        var stream = _stream;
        if (stream is null) return;

        _log("Bildkanal wird von hier aus geschlossen");
        try { stream.Dispose(); } catch { }
    }

    public void Start() => _log("Wartet auf den Mac — der Bildstrom läuft im gesicherten Kanal");

    /// <summary>Ein Schlüsselbild anfordern — kommt über den Rückkanal von der Mac-App.</summary>
    public void RequestKeyFrame() => _capture?.RequestKeyFrame();

    /// <summary>
    /// Einen bereits gesicherten Strom bedienen.
    /// </summary>
    /// <remarks>
    /// Der Bildstrom hat keinen eigenen Port mehr. Er kommt über dieselbe Verbindung
    /// wie die Steuerung, nur mit der Kennung „V“ — verschlüsselt, gegenseitig
    /// authentifiziert, und offen ist am Ende genau ein Port statt drei.
    ///
    /// Bricht er ab, läuft die Steuerung weiter: der Nutzer soll nicht die Maus verlieren,
    /// weil ein Bild fehlt.
    /// </remarks>
    public void Serve(Stream stream, string who)
    {
        // Ein zweiter Bildkanal darf den ersten nicht überschreiben. Der Fall entsteht
        // von selbst: beim Bildschirmwechsel öffnet der Mac neu, während der alte Kanal
        // noch abgebaut wird. Ohne diese Sperre setzte der neue Lauf _stream und
        // _capture, und gleich darauf räumte das „finally“ des alten beides wieder ab —
        // ein Bildkanal, der als offen gilt und nichts sendet.
        Stop();
        _one.Wait();

        try { ServeCore(stream, who); }
        finally { _one.Release(); }
    }

    private readonly SemaphoreSlim _one = new(1, 1);

    private void ServeCore(Stream stream, string who)
    {
        _log($"Bildkanal offen für {who}");

        var screen = System.Windows.Forms.Screen.AllScreens;
        var index = Math.Clamp(_screen(), 0, screen.Length - 1);
        var bounds = screen[index].Bounds;

        try
        {
            _stream = stream;
            _dropped = 0;

            // Kopf: SBRG, Version, Breite, Höhe, Bilder je Sekunde — little-endian.
            var head = new byte[20];
            "SBRG"u8.CopyTo(head);
            BinaryPrimitives.WriteInt32LittleEndian(head.AsSpan(4), 1);
            BinaryPrimitives.WriteInt32LittleEndian(head.AsSpan(8), bounds.Width);
            BinaryPrimitives.WriteInt32LittleEndian(head.AsSpan(12), bounds.Height);
            BinaryPrimitives.WriteInt32LittleEndian(head.AsSpan(16), _fps);
            stream.Write(head);
            stream.Flush();

            Viewer = who;
            Width = bounds.Width;
            Height = bounds.Height;
            ScreenIndex = index;
            Since = DateTime.UtcNow;

            Connected = true;
            ConnectionChanged?.Invoke(true);

            _sender = new Thread(SendLoop) { IsBackground = true, Name = "ScreenBridge-Versand" };
            _sender.Start();

            _capture = new Capture(index, _fps, _log, Enqueue);
            _capture.Start();

            // Die Gegenseite schickt hier nichts; ein Lesen von null Bytes ist damit
            // das Zeichen, dass sie gegangen ist.
            var scratch = new byte[16];
            while (stream.Read(scratch, 0, scratch.Length) > 0) { }
        }
        catch (Exception ex) { _log($"Bildkanal endete: {ex.Message}"); }
        finally
        {
            Connected = false;
            ScreenIndex = -1;
            ConnectionChanged?.Invoke(false);

            _capture?.Dispose();
            _capture = null;
            _stream = null;
            _signal.Set();

            lock (_gate) _queue.Clear();
            _log("Bildkanal zu — Aufnahme steht");
        }
    }

    private void Enqueue(PacketKind kind, byte[] data, long at)
    {
        if (data.Length > MaxPayload)
        {
            _log($"Nutzlast {data.Length} Byte über der Grenze — verworfen");
            return;
        }

        lock (_gate)
        {
            // Lieber ein Bild auslassen als eines nachreichen: ein Rückstau macht aus
            // Verzögerung nur mehr Verzögerung. Kopfdaten und Schlüsselbilder bleiben,
            // sonst verlöre die Gegenseite ihren Einstiegspunkt.
            while (_queue.Count >= Backlog && kind == PacketKind.Frame)
            {
                if (_queue.Peek().Kind != PacketKind.Frame) break;
                _queue.Dequeue();
                _dropped++;
            }

            _queue.Enqueue((kind, data, at));
        }

        _signal.Set();
    }

    private void SendLoop()
    {
        var header = new byte[13];
        var lastPacket = DateTime.UtcNow;

        while (!_cts.IsCancellationRequested && _stream is not null)
        {
            _signal.WaitOne(TimeSpan.FromMilliseconds(500));

            while (true)
            {
                (PacketKind Kind, byte[] Data, long At) item;
                lock (_gate)
                {
                    if (_queue.Count == 0) break;
                    item = _queue.Dequeue();
                }

                if (!Write(header, item.Kind, item.Data, item.At)) return;
                lastPacket = DateTime.UtcNow;
            }

            // Lebenszeichen, damit die Gegenseite „steht still“ von „hängt“
            // unterscheiden kann.
            if (DateTime.UtcNow - lastPacket > TimeSpan.FromSeconds(1))
            {
                if (!Write(header, PacketKind.Idle, [], 0)) return;
                lastPacket = DateTime.UtcNow;
            }
        }
    }

    private bool Write(byte[] header, PacketKind kind, byte[] payload, long at)
    {
        var stream = _stream;
        if (stream is null) return false;

        try
        {
            header[0] = (byte)kind;
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(1), payload.Length);
            BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(5), at);

            stream.Write(header);
            if (payload.Length > 0) stream.Write(payload);
            return true;
        }
        catch (Exception)
        {
            return false;   // die Gegenseite ist weg; das Aufräumen erledigt Serve
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _signal.Set();
        _capture?.Dispose();
        _signal.Dispose();
        _one.Dispose();
        _cts.Dispose();
    }
}
