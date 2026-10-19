using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ScreenBridge;

/// <summary>
/// Das Fenster, in dem der Mac-Bildschirm steht.
/// </summary>
/// <remarks>
/// <para>
/// Ein eigenes Fenster und keine Karte im Hauptfenster: hier wird gearbeitet, nicht
/// abgelesen. Es soll sich auf einen Bildschirm legen lassen, ins Vollbild gehen und im
/// Übrigen nichts zeigen als das Bild.
/// </para>
/// <para>
/// Geschlossen wird es vom Benutzer oder vom Ende des Stroms. Beides führt zum selben
/// Weg — <see cref="Stopped"/> —, damit „Trennen“ im Hauptfenster und ein Abriss
/// unterwegs nicht zwei verschiedene Zustände hinterlassen.
/// </para>
/// </remarks>
internal sealed class MacWindow : Form
{
    private readonly VideoPanel _panel;
    private readonly MacInput _input;
    private readonly Action<string> _log;

    private FormWindowState _before = FormWindowState.Normal;
    private bool _fullscreen;

    public MacWindow(MacInput input, Action<string> log)
    {
        _input = input;
        _log = log;

        Text = "Mac-Bildschirm";
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch (Exception) { }
        BackColor = Skin.Ground;
        ForeColor = Skin.Ink;
        ClientSize = new Size(1280, 800);
        MinimumSize = new Size(480, 320);
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = false;
        DoubleBuffered = true;

        _panel = new VideoPanel(input) { Dock = DockStyle.Fill };
        _panel.FullscreenRequested += ToggleFullscreen;
        Controls.Add(_panel);

        _settle.Tick += (_, _) =>
        {
            _settle.Stop();
            Settled?.Invoke(Viewport);
        };
    }

    /// <summary>Das Fenster ist zu — der Empfang soll enden.</summary>
    public event Action? Stopped;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        const int UseImmersiveDarkMode = 20;
        var on = 1;
        try { DwmSetWindowAttribute(Handle, UseImmersiveDarkMode, ref on, sizeof(int)); } catch (Exception) { }
    }

    /// <summary>
    /// Der Strom steht — das Fenster auf die <b>Quellgröße</b> einstellen.
    /// </summary>
    /// <remarks>
    /// Getrennt von <see cref="Begin"/>, weil zwei verschiedene Größen gemeint sind: hier
    /// die des Mac-Bildschirms, dort die, in der ffmpeg liefert. Aus der ersten folgt, wie
    /// groß das Fenster sein soll; aus der zweiten, wie groß die Bildfläche sein muss.
    /// </remarks>
    public void Prepare(int width, int height, string who)
    {
        Text = $"{who} — {width}×{height}";

        // NUR BEIM ERSTEN MAL, und das ist keine Sparsamkeit, sondern die Abhilfe gegen
        // eine Rückkopplung: die Gegenseite stellt ihre Aufnahme auf die hier gemeldete
        // Fenstergröße um und baut den Strom dafür neu auf. Jeder neue Strom liefe hier
        // wieder durch Prepare, würde das Fenster verstellen, das meldete eine neue
        // Größe, die Gegenseite baute wieder neu auf. Beobachtet als Aussetzer, Flackern
        // und schließlich Abbruch.
        //
        // Die Fenstergröße gehört dem Benutzer. Sie einmal sinnvoll zu setzen ist eine
        // Hilfe; sie danach anzufassen ist ein Eingriff.
        if (_sized || _fullscreen) return;
        _sized = true;

        var room = Screen.FromControl(this).WorkingArea;

        // Ins Arbeitsfeld einpassen, ohne zu verzerren. Größer als die Quelle bringt
        // nichts: mehr Bildpunkte entstehen dabei nicht.
        var scale = Math.Min(1.0, Math.Min(room.Width * 0.9 / width, room.Height * 0.9 / height));
        ClientSize = new Size(Math.Max(320, (int)(width * scale)), Math.Max(200, (int)(height * scale)));
    }

    private bool _sized;

    /// <summary>Der Dekodierer liefert ab jetzt diese Größe; die Bildfläche darauf einrichten.</summary>
    public void Begin(int width, int height)
    {
        _panel.Begin(width, height);
        _panel.Focus();
    }

    /// <summary>Die Fläche, in die das Bild passen soll — Ziel für die Skalierung in ffmpeg.</summary>
    public Size Viewport => _panel.ClientSize.IsEmpty ? ClientSize : _panel.ClientSize;

    /// <summary>
    /// Die Fläche hat sich geändert und steht seit einer Weile still.
    /// </summary>
    /// <remarks>
    /// Mit Verzögerung, weil beim Ziehen am Rand Dutzende Größen durchlaufen werden. Jede
    /// davon würde ffmpeg neu starten, und das Bild bliebe während des ganzen Ziehens
    /// schwarz.
    /// </remarks>
    public event Action<Size>? Settled;

    private readonly System.Windows.Forms.Timer _settle = new() { Interval = 400 };

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (!IsHandleCreated) return;

        _settle.Stop();
        _settle.Start();
    }

    public void Push(byte[] frame) => _panel.Push(frame);

    /// <summary>Der Strom ist zu Ende — Fläche freigeben, Fenster stehen lassen.</summary>
    public void End() => _panel.End();

    public int Dropped => _panel.Dropped;

    private void ToggleFullscreen()
    {
        if (_fullscreen)
        {
            FormBorderStyle = FormBorderStyle.Sizable;
            WindowState = _before;
            _fullscreen = false;
        }
        else
        {
            _before = WindowState;
            WindowState = FormWindowState.Normal;
            FormBorderStyle = FormBorderStyle.None;
            WindowState = FormWindowState.Maximized;
            _fullscreen = true;
        }

        _panel.Focus();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _settle.Dispose();
        base.Dispose(disposing);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Was gedrückt war, muss losgelassen werden. Sonst hält der Mac eine Taste
        // fest, die hier längst niemand mehr drückt — und das sieht dort aus wie eine
        // klemmende Tastatur.
        _input.Release();
        Stopped?.Invoke();

        base.OnFormClosing(e);
    }

    /// <summary>
    /// Die Bildfläche: ein DIB-Abschnitt, in den der Dekodierer schreibt.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Nicht über <c>Graphics.DrawImage</c>.</b> Ein <c>Bitmap</c> aus fremdem
    /// Speicher zu bauen und zu zeichnen kostete im Vollbild <b>104 ms</b> je Bild —
    /// mehr als sechs Bildlängen, und damit war das Vollbild unbrauchbar. Ein DIB-
    /// Abschnitt, in den hineinkopiert und aus dem mit <c>StretchBlt</c> herausgeblittet
    /// wird, kostet <b>1,9 ms</b>.
    /// </para>
    /// <para>
    /// <b>Und nicht aus einem <c>Graphics.FromImage</c>-Kontext heraus.</b> Ein
    /// <c>StretchBlt</c> von dort meldet Erfolg und zeichnet nichts. Eine Messung, die
    /// damit 1,6 ms ergab, maß in Wahrheit einen Aufruf ohne Wirkung.
    /// </para>
    /// </remarks>
    private sealed class VideoPanel : Control
    {
        private readonly object _gate = new();
        private readonly Cursor _blank = BlankCursor();
        private readonly MacInput _input;

        private IntPtr _dc;
        private IntPtr _surface;
        private IntPtr _previous;
        private IntPtr _pixels;
        private int _frameBytes;

        private int _width;
        private int _height;
        private bool _live;
        private volatile bool _dirty;
        private int _dropped;

        public int Dropped => _dropped;

        public event Action? FullscreenRequested;

        public VideoPanel(MacInput input)
        {
            _input = input;

            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint
                     | ControlStyles.UserPaint | ControlStyles.Selectable, true);
            BackColor = Skin.Ground;
            TabStop = true;
            HookInput();
        }

        protected override void OnPaintBackground(PaintEventArgs e) { }

        // ------------------------------------------------------------------ GDI

        [StructLayout(LayoutKind.Sequential)]
        private struct BitmapInfoHeader
        {
            public uint Size;
            public int Width;
            public int Height;
            public ushort Planes;
            public ushort BitCount;
            public uint Compression;
            public uint SizeImage;
            public int XPelsPerMeter;
            public int YPelsPerMeter;
            public uint ClrUsed;
            public uint ClrImportant;
        }

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BitmapInfoHeader header,
                                                      uint usage, out IntPtr pixels, IntPtr section, uint offset);

        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr handle);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr handle);
        [DllImport("gdi32.dll")] private static extern int SetStretchBltMode(IntPtr hdc, int mode);
        [DllImport("gdi32.dll")] private static extern bool SetBrushOrgEx(IntPtr hdc, int x, int y, IntPtr previous);

        [DllImport("gdi32.dll")]
        private static extern bool BitBlt(IntPtr dst, int x, int y, int width, int height,
                                          IntPtr src, int srcX, int srcY, int rop);

        [DllImport("gdi32.dll")]
        private static extern bool StretchBlt(IntPtr dst, int x, int y, int width, int height,
                                              IntPtr src, int srcX, int srcY, int srcWidth, int srcHeight, int rop);

        [DllImport("user32.dll")] private static extern IntPtr CreateIconIndirect(ref IconInfo info);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateBitmap(int width, int height, uint planes, uint bits, byte[] data);

        private const int SrcCopy = 0x00CC0020;

        /// <summary>
        /// Zeilen und Spalten wegwerfen — schnell, und beim Vergrößern ohne Nachteil.
        /// </summary>
        private const int ColorOnColor = 3;

        /// <summary>
        /// Mitteln statt wegwerfen — und deshalb hier <b>nicht</b> in Gebrauch.
        /// </summary>
        /// <remarks>
        /// Der naheliegende Griff, als der Mac auf volle Auflösung ging und
        /// <c>COLORONCOLOR</c> beim Verkleinern jede zweite Zeile wegwarf. Nachgemessen
        /// kostet er <b>19,6 ms</b> je Bild (2940×1912 auf 1600×1040) gegen 0,76 ms — und
        /// hat damit drei Sekunden Verzögerung erzeugt, weil er langsamer ist als der
        /// Bildtakt. Verkleinert wird jetzt in ffmpeg; hier bleibt ein 1:1-BitBlt.
        /// </remarks>
        private const int Halftone = 4;

        [StructLayout(LayoutKind.Sequential)]
        private struct IconInfo
        {
            public bool IsIcon;
            public int HotspotX;
            public int HotspotY;
            public IntPtr Mask;
            public IntPtr Color;
        }

        /// <summary>
        /// Ein Zeiger, der nichts zeichnet — <b>nicht mehr in Gebrauch, und warum.</b>
        /// </summary>
        /// <remarks>
        /// <para>
        /// Die Überlegung war: über der Bildfläche steht der Zeiger des Macs, zwei
        /// übereinander sind einer zu viel. Sie stimmt nur, solange der Zeiger des Macs
        /// <i>rechtzeitig</i> da ist — und das kann er nicht sein. Er liegt hinter der
        /// ganzen Schleife: Ereignis hin, einspeisen, zeichnen, aufnehmen, kodieren,
        /// senden, dekodieren, zeichnen. Das sind gute fünfzig bis neunzig Millisekunden,
        /// und keine Einstellung nimmt sie weg, weil das Bild selbst darin steckt.
        /// </para>
        /// <para>
        /// <b>Der eigene Zeiger ist sofort da.</b> Er zeigt, wohin gezeigt wird; der des
        /// Macs zeigt, wo der Mac gerade angekommen ist. Nachlaufen darf der Inhalt — der
        /// Zeiger nicht, denn an ihm misst die Hand, ob der Rechner reagiert. Deshalb
        /// zeigen alle Fernwartungen den eigenen Zeiger, und deshalb wird der leere hier
        /// nicht mehr gesetzt.
        /// </para>
        /// <para>
        /// Bleibt: zwei Zeiger, solange der Mac seinen mitschickt. Der richtige Ort dafür
        /// ist die Aufnahme — <c>showsCursor</c> aus, dann ist wieder genau einer da, und
        /// zwar der schnelle.
        /// </para>
        /// </remarks>
        private static Cursor BlankCursor()
        {
            const int Side = 32;
            var opaque = new byte[Side * Side / 8];
            Array.Fill(opaque, (byte)0xFF);

            var mask = CreateBitmap(Side, Side, 1, 1, opaque);
            var color = CreateBitmap(Side, Side, 1, 1, new byte[Side * Side / 8]);

            var info = new IconInfo { IsIcon = false, HotspotX = 0, HotspotY = 0, Mask = mask, Color = color };
            var handle = CreateIconIndirect(ref info);

            DeleteObject(mask);
            DeleteObject(color);
            return handle == IntPtr.Zero ? Cursors.Default : new Cursor(handle);
        }

        // ---------------------------------------------------------------- Strom

        public void Begin(int width, int height)
        {
            lock (_gate)
            {
                ReleaseSurface();

                // Negative Höhe: von oben nach unten, wie der Dekodierer liefert.
                var header = new BitmapInfoHeader
                {
                    Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                    Width = width,
                    Height = -height,
                    Planes = 1,
                    BitCount = 32,
                    Compression = 0,
                };

                _dc = CreateCompatibleDC(IntPtr.Zero);
                _surface = CreateDIBSection(_dc, ref header, 0, out _pixels, IntPtr.Zero, 0);
                _previous = SelectObject(_dc, _surface);

                _width = width;
                _height = height;
                _frameBytes = width * height * 4;
                _dropped = 0;
            }

            _live = true;

            // Der eigene Zeiger, nicht der leere: er ist der einzige ohne Verzögerung.
            Cursor = Cursors.Default;

            Invalidate();
            Focus();
        }

        public void End()
        {
            lock (_gate) ReleaseSurface();

            _live = false;
            Cursor = Cursors.Default;
            Invalidate();
        }

        /// <summary>Nur unter <c>_gate</c> aufrufen.</summary>
        private void ReleaseSurface()
        {
            if (_dc != IntPtr.Zero)
            {
                SelectObject(_dc, _previous);
                DeleteDC(_dc);
                _dc = IntPtr.Zero;
            }

            if (_surface != IntPtr.Zero)
            {
                DeleteObject(_surface);
                _surface = IntPtr.Zero;
            }

            _pixels = IntPtr.Zero;
            _frameBytes = 0;
        }

        /// <summary>
        /// Nimmt ein fertiges BGRA-Bild entgegen. Kam seit dem letzten Zeichnen schon
        /// eines, wird es überschrieben — gezeigt wird immer das neueste.
        /// </summary>
        public void Push(byte[] frame)
        {
            lock (_gate)
            {
                if (_pixels == IntPtr.Zero) return;

                if (_dirty) _dropped++;
                Marshal.Copy(frame, 0, _pixels, Math.Min(frame.Length, _frameBytes));
            }

            _dirty = true;

            // Die Anforderung geht bei jedem Bild heraus, ohne Bedingung — daran kann
            // nichts hängenbleiben. Ein Merker „steht schon aus“ hat hier einmal das
            // ganze Bild angehalten: ein Invalidate an einem Fenster, das gerade nicht
            // zeichnet, erzeugt kein WM_PAINT, und der Merker blieb für immer stehen.
            try
            {
                if (!IsDisposed && IsHandleCreated) BeginInvoke(Invalidate);
            }
            catch (Exception) { /* Fenster geht gerade zu */ }
        }

        // ------------------------------------------------------------- Zeichnen

        protected override void OnPaint(PaintEventArgs e)
        {
            _dirty = false;
            var g = e.Graphics;

            lock (_gate)
            {
                if (_dc != IntPtr.Zero)
                {
                    var box = ClientSize.Width == _width && ClientSize.Height == _height
                        ? new Rectangle(0, 0, _width, _height)
                        : Letterbox();

                    // Die Balken neben einem nicht passenden Seitenverhältnis.
                    if (box.Width != ClientSize.Width || box.Height != ClientSize.Height)
                        g.Clear(Skin.Ground);

                    var dst = g.GetHdc();
                    try
                    {
                        if (box.Width == _width && box.Height == _height)
                        {
                            BitBlt(dst, box.X, box.Y, _width, _height, _dc, 0, 0, SrcCopy);
                        }
                        else
                        {
                            // Der Normalfall ist 1:1 und kommt hier gar nicht an. Hierher
                            // führt nur der Moment zwischen einer Größenänderung und dem
                            // neu aufgestellten Dekodierer — und für den halben Wimpernschlag
                            // ist schnell wichtiger als sauber.
                            SetStretchBltMode(dst, ColorOnColor);
                            SetBrushOrgEx(dst, 0, 0, IntPtr.Zero);
                            StretchBlt(dst, box.X, box.Y, box.Width, box.Height,
                                       _dc, 0, 0, _width, _height, SrcCopy);
                        }
                    }
                    finally { g.ReleaseHdc(dst); }

                    return;
                }
            }

            g.Clear(Skin.Ground);
            using var brush = new SolidBrush(Skin.Muted);
            using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString("Wartet auf das Bild vom Mac.\n\nF11 oder Doppelklick schaltet auf Vollbild.",
                         Skin.Body, brush, ClientRectangle, format);
        }

        /// <summary>Beim Ziehen am Fenster das Seitenverhältnis halten, nicht verzerren.</summary>
        private Rectangle Letterbox()
        {
            if (_width <= 0 || _height <= 0) return ClientRectangle;

            var scale = Math.Min(ClientSize.Width / (double)_width, ClientSize.Height / (double)_height);
            var w = Math.Max(1, (int)(_width * scale));
            var h = Math.Max(1, (int)(_height * scale));
            return new Rectangle((ClientSize.Width - w) / 2, (ClientSize.Height - h) / 2, w, h);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            Invalidate();
        }

        // -------------------------------------------------------------- Eingabe

        private void HookInput()
        {
            DoubleClick += (_, _) => FullscreenRequested?.Invoke();

            MouseDown += (_, e) =>
            {
                Focus();
                var (x, y) = Normalize(e.X, e.Y);
                if (_live) _input.Button(true, ButtonName(e.Button), x, y);
            };

            MouseUp += (_, e) =>
            {
                var (x, y) = Normalize(e.X, e.Y);
                if (_live) _input.Button(false, ButtonName(e.Button), x, y);
            };

            MouseMove += (_, e) =>
            {
                var (x, y) = Normalize(e.X, e.Y);
                if (_live) _input.Move(x, y);
            };

            MouseWheel += (_, e) => { if (_live) _input.Scroll(Lines(e.Delta), 0); };

            // Beim Verlassen alles loslassen: sonst hält der Mac eine Taste fest, die
            // hier niemand mehr drückt.
            MouseLeave += (_, _) => { if (_live) _input.Release(); };

            KeyPress += (_, e) =>
            {
                if (char.IsControl(e.KeyChar)) return;
                if (_live) _input.Text(e.KeyChar);
                e.Handled = true;
            };

            KeyDown += (_, e) =>
            {
                if (e.KeyCode == Keys.F11)
                {
                    FullscreenRequested?.Invoke();
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                    return;
                }

                if (MacKeyCode(e.KeyCode) is not { } code) return;

                if (_live)
                {
                    _input.Key(code, true);
                    _input.Key(code, false);
                }

                e.Handled = true;
                e.SuppressKeyPress = true;
            };
        }

        /// <summary>
        /// Ein Rastpunkt des Rads in Zeilen — so viele, wie Windows hier auch blättert.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <c>e.Delta</c> zählt in Einheiten zu 120 je Rastpunkt; über die Leitung gehen
        /// <b>Zeilen</b>. Der erste Anlauf schickte den Rastpunkt selbst, also 1,0 statt
        /// 3,0 — ein Drittel dessen, was dieselbe Handbewegung hier bewirkt. Auf dem Mac
        /// kam entsprechend wenig an, und das sah nach einer trägen Verbindung aus statt
        /// nach einem Rechenfehler.
        /// </para>
        /// <para>
        /// Wie viele Zeilen ein Rastpunkt sind, sagt Windows selbst, und es ist
        /// einstellbar. Diesen Wert zu nehmen statt einer festen Drei ist der Unterschied
        /// zwischen „gleich schnell wie hier“ und „zufällig ähnlich“. Steht dort −1, ist
        /// seitenweises Blättern eingestellt; dafür gibt es auf der Gegenseite keine
        /// Entsprechung, also ein großzügiger Ersatz.
        /// </para>
        /// </remarks>
        private static double Lines(int delta)
        {
            var lines = SystemInformation.MouseWheelScrollLines;
            return delta / 120.0 * (lines > 0 ? lines : 16);
        }

        private static string ButtonName(MouseButtons button) => button switch
        {
            MouseButtons.Right => "right",
            MouseButtons.Middle => "middle",
            _ => "left",
        };

        /// <summary>Pfeiltasten und Tabulator gehören zum Mac, nicht zur Fensterführung.</summary>
        protected override bool IsInputKey(Keys keyData) =>
            MacKeyCode(keyData & Keys.KeyCode) is not null || base.IsInputKey(keyData);

        private (double X, double Y) Normalize(int x, int y)
        {
            // Beim Ziehen am Fenster liegt das Bild mittig mit Rändern — gerechnet wird
            // gegen das Bild, nicht gegen die Fläche.
            var box = ClientSize.Width == _width && ClientSize.Height == _height
                ? new Rectangle(0, 0, Math.Max(1, _width), Math.Max(1, _height))
                : Letterbox();

            return (Math.Clamp((x - box.X) / (double)Math.Max(1, box.Width), 0, 1),
                    Math.Clamp((y - box.Y) / (double)Math.Max(1, box.Height), 0, 1));
        }

        /// <summary>Tastencodes von macOS für die Tasten ohne Zeichen.</summary>
        private static int? MacKeyCode(Keys key) => key switch
        {
            Keys.Return => 36,
            Keys.Tab => 48,
            Keys.Space => 49,
            Keys.Back => 51,
            Keys.Escape => 53,
            Keys.Left => 123,
            Keys.Right => 124,
            Keys.Down => 125,
            Keys.Up => 126,
            Keys.Delete => 117,
            Keys.Home => 115,
            Keys.End => 119,
            Keys.PageUp => 116,
            Keys.PageDown => 121,
            _ => null,
        };

        protected override void Dispose(bool disposing)
        {
            if (disposing) { lock (_gate) ReleaseSurface(); }
            base.Dispose(disposing);
        }
    }
}
