namespace ScreenBridge;

/// <summary>
/// Maus und Tastatur vom PC zum Mac — über den Steuerkanal, aber nicht vom UI-Thread.
/// </summary>
/// <remarks>
/// <para>
/// <b>Ein eigener Faden mit einer Warteschlange davor, und das aus zwei Gründen.</b>
/// Erstens hielte ein blockierender Netzwerkschreibvorgang das Fenster an, und zwar genau
/// dann, wenn die Leitung klemmt — also wenn man am wenigsten möchte, dass die Maus
/// stehenbleibt. Zweitens wäre die naheliegende Abkürzung falsch: <c>_ =
/// SendAsync(…)</c> für jedes Ereignis erzeugt Aufgaben, die den Sperrzähler in
/// beliebiger Reihenfolge nehmen. Bei einer Zeigerbewegung sieht man das nicht, bei
/// einem Tastendruck vor dem zugehörigen Loslassen schon.
/// </para>
/// <para>
/// <b>Bewegungen werden am Ende der Schlange zusammengefasst, nicht verworfen.</b>
/// Steht dort schon eine Bewegung und kommt eine neue, ersetzt sie diese — die
/// Reihenfolge zu allem anderen bleibt damit erhalten. Ein Klick schiebt sich nie vor
/// die Bewegung, die ihn hinführt.
/// </para>
/// </remarks>
internal sealed class MacInput : IDisposable
{
    /// <summary>Mehr staut sich nur an, wenn die Leitung ohnehin verloren ist.</summary>
    private const int MaxQueue = 256;

    private readonly Func<object, Task<bool>> _send;
    private readonly Action<string> _log;

    private readonly List<(bool IsMove, object Message)> _queue = [];
    private readonly object _gate = new();
    private readonly AutoResetEvent _signal = new(false);
    private readonly Thread _thread;

    private volatile bool _stop;

    public MacInput(Func<object, Task<bool>> send, Action<string> log)
    {
        _send = send;
        _log = log;

        _thread = new Thread(Loop) { IsBackground = true, Name = "ScreenBridge-Eingaben" };
        _thread.Start();
    }

    /// <summary>Wie viele Ereignisse hinausgegangen sind — für die Anzeige.</summary>
    public long Sent { get; private set; }

    public void Move(double x, double y) => Add(true, new { t = "move", x, y });

    public void Button(bool down, string button, double x, double y) =>
        Add(false, new { t = down ? "down" : "up", b = button, x, y });

    public void Scroll(double dy, double dx) => Add(false, new { t = "scroll", dy, dx });

    public void Text(char c) => Add(false, new { t = "text", s = c.ToString() });

    public void Key(int code, bool down) => Add(false, new { t = "key", c = code, down });

    /// <summary>Alle gedrückten Tasten loslassen — beim Verlassen des Fensters.</summary>
    public void Release() => Add(false, new { t = "reset" });

    /// <summary>Ein Schlüsselbild anfordern; ohne eines bleibt das Fenster schwarz.</summary>
    public void KeyFrame() => Add(false, new { t = "idr" });

    private void Add(bool isMove, object message)
    {
        lock (_gate)
        {
            // Nur die letzte Bewegung zählt: sie enthält den Zielpunkt, und alle davor
            // sind bereits überholt. Ersetzt wird ausschließlich am Ende — alles, was
            // davor steht, behält seinen Platz.
            if (isMove && _queue.Count > 0 && _queue[^1].IsMove)
            {
                _queue[^1] = (true, message);
            }
            else
            {
                if (_queue.Count >= MaxQueue) _queue.RemoveAt(0);
                _queue.Add((isMove, message));
            }
        }

        _signal.Set();
    }

    private void Loop()
    {
        while (!_stop)
        {
            // Ohne Frist: geweckt wird über das Signal — von Add und von Dispose.
            // Eine Frist von 100 ms hieße zehn Aufwachen je Sekunde, rund um die
            // Uhr, auch wenn monatelang nichts zu senden ist.
            _signal.WaitOne();

            while (!_stop)
            {
                object message;
                lock (_gate)
                {
                    if (_queue.Count == 0) break;
                    message = _queue[0].Message;
                    _queue.RemoveAt(0);
                }

                try
                {
                    if (!_send(message).GetAwaiter().GetResult()) continue;
                    Sent++;
                }
                catch (Exception ex)
                {
                    _log($"[Mac] Eingabe ging nicht hinaus: {ex.Message}");
                }
            }
        }
    }

    public void Dispose()
    {
        _stop = true;
        _signal.Set();

        try { _thread.Join(500); } catch { }
        _signal.Dispose();
    }
}
