using System.Drawing;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace ScreenBridge;

/// <summary>
/// Speist Maus und Tastatur vom Mac in Windows ein.
/// </summary>
/// <remarks>
/// <para>
/// <b>Ohne eigenen Port.</b> Bis zum Umzug in den gesicherten Kanal hörte diese Klasse
/// auf 7201 — und nahm dort von <i>jedem</i> Maus- und Tastaturbefehle für diesen
/// Rechner entgegen. Die Ereignisse kommen jetzt über die eine TLS-Verbindung, von
/// einem gekoppelten Gerät oder von niemandem.
/// </para>
/// <para>
/// Eingespeist wird über <c>SendInput</c> mit absoluten Koordinaten.
/// </para>
/// <para>
/// <b>Gehaltenes wird beim Trennen freigegeben.</b> Bricht die Verbindung ab, während
/// eine Maustaste oder die Umschalttaste unten ist, bliebe sie in Windows unten — der
/// Rechner markiert dann alles oder schreibt groß, und niemand sieht warum. Dieselbe
/// Falle gab es am Mac-Helfer schon einmal.
/// </para>
/// </remarks>
internal sealed class InputServer : IDisposable
{
    private readonly Action<string> _log;
    private readonly Func<Rectangle> _target;
    private readonly Action _onKeyFrameRequest;
    private readonly CancellationTokenSource _cts = new();

    private readonly HashSet<ushort> _keysDown = [];
    private bool _leftDown, _rightDown, _middleDown;
    private int _events;

    public InputServer(Func<Rectangle> target, Action onKeyFrameRequest, Action<string> log)
    {
        _target = target;
        _onKeyFrameRequest = onKeyFrameRequest;
        _log = log;
    }

    public int Events => _events;

    /// <summary>
    /// Ein Ereignis einspeisen — gleich, ob es über den gesicherten Kanal kam oder über
    /// den alten Port.
    /// </summary>
    /// <remarks>
    /// Die Einspeisung ist absichtlich dieselbe. Beim Umzug in den verschlüsselten Kanal
    /// wäre die naheliegende Versuchung gewesen, sie ein zweites Mal zu schreiben — und
    /// dann hätte man zwei Tabellen für Tastencodes, von denen eine irgendwann
    /// zurückbleibt.
    /// </remarks>
    public void Handle(System.Text.Json.JsonElement root)
    {
        try
        {
            if (!root.TryGetProperty("t", out var kind)) return;

            _events++;

            switch (kind.GetString())
            {
                case "move":
                    MoveTo(root);
                    break;

                case "down":
                case "up":
                    MoveTo(root);
                    Button(root.GetProperty("t").GetString() == "down", Text(root, "b") ?? "left");
                    break;

                case "scroll":
                    Scroll(Number(root, "dy"), Number(root, "dx"));
                    break;

                case "text":
                    foreach (var c in Text(root, "s") ?? "") TypeChar(c);
                    break;

                case "key":
                    Key(root);
                    break;

                case "reset":
                    ReleaseAll();
                    break;

                case "idr":
                    _onKeyFrameRequest();
                    break;
            }
        }
        catch (Exception) { /* ein kaputtes Ereignis ist kein Grund aufzuhören */ }
    }

    private static string? Text(System.Text.Json.JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double Number(System.Text.Json.JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;

    // --------------------------------------------------------------- Zeiger

    /// <summary>
    /// Die Position auf den aufgenommenen Bildschirm legen.
    /// </summary>
    /// <remarks>
    /// Absolute Koordinaten laufen in 0..65535 über den <b>gesamten virtuellen
    /// Schreibtisch</b>, nicht über einen Bildschirm. Ohne
    /// <c>MOUSEEVENTF_VIRTUALDESK</c> und ohne diese Umrechnung landet bei zwei
    /// Bildschirmen alles auf dem falschen.
    /// </remarks>
    private void MoveTo(System.Text.Json.JsonElement e)
    {
        if (!e.TryGetProperty("x", out _) || !e.TryGetProperty("y", out _)) return;

        var target = _target();
        var desktop = SystemInformation.VirtualScreen;

        var x = target.Left + Math.Clamp(Number(e, "x"), 0, 1) * target.Width;
        var y = target.Top + Math.Clamp(Number(e, "y"), 0, 1) * target.Height;

        var absoluteX = (int)Math.Round((x - desktop.Left) * 65535.0 / Math.Max(1, desktop.Width - 1));
        var absoluteY = (int)Math.Round((y - desktop.Top) * 65535.0 / Math.Max(1, desktop.Height - 1));

        Mouse(MouseEventF.Move | MouseEventF.Absolute | MouseEventF.VirtualDesk, absoluteX, absoluteY, 0);
    }

    private void Button(bool down, string which)
    {
        var flag = which switch
        {
            "right" => down ? MouseEventF.RightDown : MouseEventF.RightUp,
            "middle" => down ? MouseEventF.MiddleDown : MouseEventF.MiddleUp,
            _ => down ? MouseEventF.LeftDown : MouseEventF.LeftUp,
        };

        switch (which)
        {
            case "right": _rightDown = down; break;
            case "middle": _middleDown = down; break;
            default: _leftDown = down; break;
        }

        Mouse(flag, 0, 0, 0);
    }

    /// <summary>Der Mac zählt in Zeilen, Windows in Rastpunkten zu 120.</summary>
    /// <summary>
    /// Rollen, ohne die Reste zu verlieren.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Über die Leitung kommen <b>Zeilen</b>, Windows will Einheiten zu 120 je Rastpunkt.
    /// Der erste Anlauf rundete jede Nachricht für sich — und warf damit alles weg, was
    /// unter einer halben Einheit lag. Bei ganzen Rastpunkten fällt das nicht auf; ein
    /// Trackpad schickt aber viele kleine Beträge, und die verschwanden einzeln, obwohl
    /// sie zusammen eine Bewegung ergeben.
    /// </para>
    /// <para>
    /// Deshalb wird der Rest aufgehoben und auf die nächste Nachricht angerechnet. Der
    /// Fehler ist derselbe wie das Abschneiden auf der Mac-Seite, nur halb so groß und
    /// dadurch noch unauffälliger: ein stiller Verlust, der bei ganzen Zahlen unsichtbar
    /// ist und bei Bruchteilen alles auffrisst.
    /// </para>
    /// </remarks>
    private void Scroll(double lines, double columns)
    {
        _wheelRest += lines * 120;
        _hwheelRest += columns * 120;

        // Zur Null hin abschneiden und den Rest behalten — mit Vorzeichen, sonst
        // sammelten sich Aufwärts- und Abwärtsreste gegeneinander an.
        var vertical = (int)_wheelRest;
        var horizontal = (int)_hwheelRest;

        _wheelRest -= vertical;
        _hwheelRest -= horizontal;

        if (vertical != 0) Mouse(MouseEventF.Wheel, 0, 0, vertical);
        if (horizontal != 0) Mouse(MouseEventF.HWheel, 0, 0, horizontal);
    }

    private double _wheelRest;
    private double _hwheelRest;

    // ------------------------------------------------------------- Tastatur

    private void TypeChar(char c)
    {
        // Über den Zeichenwert, nicht über eine Taste: damit stimmen Umlaute und alles
        // Übrige, ohne dass irgendwo eine Belegungstabelle steht.
        Send(
            new Input { Type = 1, U = { Keyboard = new KeyboardInput { Scan = c, Flags = KeyEventF.Unicode } } },
            new Input { Type = 1, U = { Keyboard = new KeyboardInput { Scan = c, Flags = KeyEventF.Unicode | KeyEventF.KeyUp } } });
    }

    private void Key(System.Text.Json.JsonElement e)
    {
        if (!e.TryGetProperty("c", out var code) || code.ValueKind != JsonValueKind.Number) return;

        var vk = WindowsKey(code.GetInt32());
        if (vk == 0) return;

        var down = e.TryGetProperty("down", out var d) && d.ValueKind != JsonValueKind.False;

        if (down) _keysDown.Add(vk); else _keysDown.Remove(vk);

        Send(new Input
        {
            Type = 1,
            U = { Keyboard = new KeyboardInput { Vk = vk, Flags = down ? 0 : KeyEventF.KeyUp } },
        });
    }

    /// <summary>
    /// macOS-Tastencode auf Windows.
    /// </summary>
    /// <remarks>
    /// <b>Die Befehlstaste wird zu Strg</b> — nach dem Griff, nicht nach der Taste. Wer
    /// am Mac sitzt, kopiert mit Befehl-C; läge dort die Windows-Taste, täte das nichts.
    /// Die rechten Varianten sind mit aufgeführt, weil macOS sie getrennt meldet.
    /// </remarks>
    private static ushort WindowsKey(int mac) => mac switch
    {
        36 => 0x0D,    // Eingabe
        48 => 0x09,    // Tabulator
        49 => 0x20,    // Leertaste
        51 => 0x08,    // Rückschritt
        53 => 0x1B,    // Esc
        117 => 0x2E,   // Entfernen
        115 => 0x24,   // Pos1
        119 => 0x23,   // Ende
        116 => 0x21,   // Bild auf
        121 => 0x22,   // Bild ab
        123 => 0x25,   // links
        124 => 0x27,   // rechts
        125 => 0x28,   // unten
        126 => 0x26,   // oben

        55 or 54 => 0x11,   // Befehl  -> Strg
        59 or 62 => 0x11,   // Steuerung -> Strg
        58 or 61 => 0x12,   // Wahl -> Alt
        56 or 60 => 0x10,   // Umschalt

        122 => 0x70, 120 => 0x71, 99 => 0x72, 118 => 0x73,
        96 => 0x74, 97 => 0x75, 98 => 0x76, 100 => 0x77,
        101 => 0x78, 109 => 0x79, 103 => 0x7A, 111 => 0x7B,

        _ => 0,
    };

    private void ReleaseAll()
    {
        // Ein halber Rastpunkt von vorhin gehört nicht zur nächsten Sitzung.
        _wheelRest = 0;
        _hwheelRest = 0;

        if (_leftDown) { Mouse(MouseEventF.LeftUp, 0, 0, 0); _leftDown = false; }
        if (_rightDown) { Mouse(MouseEventF.RightUp, 0, 0, 0); _rightDown = false; }
        if (_middleDown) { Mouse(MouseEventF.MiddleUp, 0, 0, 0); _middleDown = false; }

        foreach (var vk in _keysDown.ToArray())
        {
            Send(new Input { Type = 1, U = { Keyboard = new KeyboardInput { Vk = vk, Flags = KeyEventF.KeyUp } } });
        }

        _keysDown.Clear();
    }

    // ---------------------------------------------------------------- Win32

    private void Mouse(MouseEventF flags, int x, int y, int data) =>
        Send(new Input
        {
            Type = 0,
            U = { Mouse = new MouseInput { X = x, Y = y, Data = (uint)data, Flags = flags } },
        });

    private void Send(params Input[] inputs)
    {
        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>()) == 0)
            _log($"[Eingabe] SendInput abgelehnt, Fehler {Marshal.GetLastWin32Error()}");
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, Input[] inputs, int size);

    /// <remarks>
    /// <b>Die Größe muss stimmen, sonst lehnt Windows still ab.</b> Auf x64 sind es
    /// 40 Byte: vier für die Art, vier Füllbytes wegen der Ausrichtung, und 32 für die
    /// größte Variante. Wer die Füllbytes vergisst, kommt auf 36 — <c>SendInput</c> gibt
    /// dann 0 zurück und nichts passiert.
    /// </remarks>
    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public KeyboardInput Keyboard;
        [FieldOffset(0)] private HardwareInput _hardware;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int X;
        public int Y;
        public uint Data;
        public MouseEventF Flags;
        public uint Time;
        public nint Extra;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort Vk;
        public ushort Scan;
        public KeyEventF Flags;
        public uint Time;
        public nint Extra;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HardwareInput
    {
        public uint Message;
        public ushort LowParam;
        public ushort HighParam;
    }

    [Flags]
    private enum MouseEventF : uint
    {
        Move = 0x0001,
        LeftDown = 0x0002,
        LeftUp = 0x0004,
        RightDown = 0x0008,
        RightUp = 0x0010,
        MiddleDown = 0x0020,
        MiddleUp = 0x0040,
        Wheel = 0x0800,
        HWheel = 0x1000,
        VirtualDesk = 0x4000,
        Absolute = 0x8000,
    }

    [Flags]
    private enum KeyEventF : uint
    {
        KeyUp = 0x0002,
        Unicode = 0x0004,
    }

    /// <summary>Beim Ende alles Gehaltene freigeben — sonst klemmt eine Taste.</summary>
    public void Dispose()
    {
        _cts.Cancel();
        ReleaseAll();
        _cts.Dispose();
    }
}
