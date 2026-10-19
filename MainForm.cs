using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Forms;

namespace ScreenBridge;

/// <summary>
/// Das Fenster: vier Bereiche, jeder für eine Frage, die man wirklich hat.
/// </summary>
/// <remarks>
/// <para>
/// <b>Fernzugriff, Dateien, Sicherheit, Gegenstelle.</b> Der erste Entwurf hatte alles
/// in einer Zeile Messwerte und darunter ein Protokoll — und damit stand die wichtigste
/// Frage („sieht der Mac gerade zu, und wenn nein, wie fange ich an?“) nirgends, während
/// die Taktmessung fünf Zeilen bekam.
/// </para>
/// <para>
/// Der PC ist hier der Sender und zeigt selbst kein Bild — er hat ja das Original vor
/// sich. Zu sehen ist deshalb nur, was man sonst nirgends sähe.
/// </para>
/// <para>
/// <b>Nichts hier verlangt eine Reihenfolge.</b> Eine Datei kann man abschicken, ohne
/// vorher zu verbinden; koppeln muss man einmal und nie wieder. Was gerade nicht
/// hinausgehen kann, wartet, statt zu verfallen.
/// </para>
/// </remarks>
internal sealed class MainForm : Form
{
    private const int MaxLogLines = 600;
    private const int Fps = 60;
    private const string Version = "0.3";

    private readonly string _logPath;
    private readonly ListBox _log;
    private readonly Panel _logHost;

    private readonly Label _headline;
    private readonly Label _subline;

    private readonly Fields _macFields;
    private readonly Fields _fileInfo;
    private readonly Fields _security;
    private readonly Fields _peerInfo;
    private readonly Meter _meter;

    private readonly ComboBox _macScreens;
    private readonly Button _connect;
    private readonly Button _disconnect;
    private readonly Button _unpair;

    private readonly System.Windows.Forms.Timer _tick;

    private VideoServer? _video;
    private InputServer? _input;
    private Identity? _identity;
    private ControlServer? _control;
    private Tray? _tray;
    private FileChannel? _files;

    // Die Gegenrichtung: der Mac-Bildschirm hier.
    private MacScreen? _mac;
    private MacInput? _macInput;
    private volatile MacWindow? _macWindow;

    /// <summary>
    /// Was noch niemandem gesagt werden konnte.
    /// </summary>
    /// <remarks>
    /// Der Steuerkanal ist zwischendurch weg — die Mac-App verbindet sich alle paar
    /// Sekunden neu. Wer in diese Lücke klickt, hätte sonst nichts erreicht und müsste
    /// es erraten. Angebote sammeln sich; von den Bildschirmbefehlen zählt nur der
    /// letzte, weil zwei widersprüchliche nacheinander abzuarbeiten Unsinn wäre.
    /// </remarks>
    private readonly List<object> _waitingOffers = [];
    private readonly Dictionary<string, object> _waitingCommands = [];

    /// <summary>Schließen versteckt; beenden geht über das Menü am Symbol.</summary>
    private bool _reallyQuitting;

    /// <summary>Der gewählte Bildschirm, gespiegelt für die Fäden neben dem Fenster.</summary>
    private int _screen;

    /// <summary>Setzt der Mac die Auswahl, soll sie nicht als eigene Wahl zurückkommen.</summary>
    private bool _suppress;

    private long _lastFrames;
    private long _lastBytes;
    private DateTime _lastSample = DateTime.UtcNow;
    private double _framesPerSecond;
    private double _megabits;

    // Was die Gegenstelle über sich sagt.
    private string _peerApp = "";
    private string _peerSystem = "";
    private DateTime _lastHeard;
    private bool _macShowing;
    private bool _macFullscreen;
    private bool _macSaidState;

    /// <summary>
    /// Was der Mac über seinen eigenen Sender sagt — getrennt vom eigenen Empfangszustand.
    /// </summary>
    /// <remarks>
    /// Die beiden sind nicht dasselbe, und der Unterschied ist der interessante Fall:
    /// hört der Mac auf zu senden, weiß er warum, und ich weiß es nicht. Klappt der Nutzer den
    /// Deckel zu, schaltet macOS das eingebaute Display ab — von hier aus sieht das genauso
    /// aus wie ein Netzausfall. Der Grund kommt deshalb mit, und er gehört angezeigt.
    ///
    /// Diese drei Felder standen einen Umbau lang <b>ungelesen</b> da: gesetzt, nie
    /// gebraucht. Die Gegenseite schickte die Auskunft, und sie fiel hier auf den Boden.
    /// </remarks>
    private bool _macSending;
    private bool _macSaidSending;
    private string _macWhy = "";

    /// <summary>Welcher Mac-Bildschirm zuletzt angefordert wurde; −1 heißt: keiner.</summary>
    private int _macRequested = -1;

    /// <summary>
    /// Ein abgerissener Bildempfang wird von selbst wieder angefordert.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Es gibt genug Gründe, aus denen der Strom endet, ohne dass jemand ihn beendet hat:
    /// der Mac-Deckel geht zu und macOS schaltet das eingebaute Display ab, das WLAN
    /// blinzelt, die App drüben startet neu. In all diesen Fällen will der Nutzer das Bild
    /// zurück, sobald es wieder möglich ist — und nicht einen Knopf suchen.
    /// </para>
    /// <para>
    /// Der Abstand wächst, damit ein Mac mit zugeklapptem Deckel nicht im Sekundentakt
    /// gefragt wird. Aufgegeben wird nie: nur „Trennen“ beendet den Wunsch.
    /// </para>
    /// </remarks>
    private readonly System.Windows.Forms.Timer _resume = new() { Interval = 2000 };
    private int _resumeSeconds = 2;
    private DateTime _resumeAt;

    // Antwortzeit über den Steuerkanal.
    private int _pingNumber;
    private long _pingSentAt;
    private double _roundTrip = -1;
    private int _unanswered;
    private bool _everAnswered;
    private int _ticks;

    private Transfer? _transfer;

    /// <summary>Vom Autostart gestartet: nur das Symbol, kein Fenster.</summary>
    private readonly bool _startHidden;

    public MainForm(bool startHidden = false)
    {
        _startHidden = startHidden;
        _logPath = Path.Combine(AppContext.BaseDirectory, "screenbridge-log.txt");

        Text = "ScreenBridge";
        BackColor = Skin.Ground;
        ForeColor = Skin.Ink;
        ClientSize = new Size(1080, 832);
        MinimumSize = new Size(980, 780);
        StartPosition = FormStartPosition.CenterScreen;
        Font = Skin.Body;
        DoubleBuffered = true;

        // Das eigene Symbol statt des blauen WinForms-Standardfensters — die EXE
        // trägt es, hier bekommt es das Fenster in Leiste und Alt-Tab.
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch (Exception) { }

        if (_startHidden)
        {
            // Leise anfangen: durchsichtig aufbauen, nach dem Laden verstecken.
            // Unsichtbarkeit über SetVisibleCore wäre gründlicher — aber dann liefe
            // OnLoad nie, und dort startet alles.
            Opacity = 0;
            ShowInTaskbar = false;
        }

        // ------------------------------------------------------------ Kopf

        _headline = new Label
        {
            AutoSize = false, Dock = DockStyle.Top, Height = 40,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = Skin.Headline,
            Padding = new Padding(46, 0, 0, 0),
            ForeColor = Skin.Ink,
            Text = "Startet",
        };
        _headline.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var dot = new SolidBrush(_dotColour);
            e.Graphics.FillEllipse(dot, 22, _headline.Height / 2 - 6, 12, 12);
        };

        _subline = new Label
        {
            AutoSize = false, Dock = DockStyle.Top, Height = 26,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(47, 0, 0, 0),
            ForeColor = Skin.Muted,
        };

        var header = new Panel { Dock = DockStyle.Top, Height = 82, BackColor = Skin.Panel };
        header.Controls.Add(new Panel { Dock = DockStyle.Fill, BackColor = Skin.Panel });
        header.Controls.Add(_subline);
        header.Controls.Add(_headline);
        header.Paint += (s, e) =>
        {
            var me = (Control)s!;
            using var pen = new Pen(Skin.Line);
            e.Graphics.DrawLine(pen, 0, me.Height - 1, me.Width, me.Height - 1);
        };

        // -------------------------------- Karte: den Mac ansehen (Mac → PC)
        //
        // Die Gegenrichtung hat hier keine Bedienung mehr. Sie wird immer am MacBook
        // gestartet, und ein zweiter Knopf dafür wäre eine zweite Stelle, an der
        // derselbe Zustand steht. Sichtbar bleibt sie trotzdem — als eine Zeile unter
        // „Gegenstelle“, denn dass der Mac gerade zusieht, will man wissen.

        _macScreens = Chooser();
        _macScreens.Items.Add("Bildschirm 1");
        _macScreens.SelectedIndex = 0;

        _connect = Skin.Button("Verbinden", 100);
        _connect.Click += (_, _) => StartMac();

        _disconnect = Skin.Button("Trennen", 92);
        _disconnect.Click += (_, _) => { StopMac(); StopShare(); };

        // Erst hier anmelden: das Setzen der Auswahl oben wäre sonst schon eine Wahl.
        _macScreens.SelectedIndexChanged += (_, _) =>
        {
            if (_suppress) return;
            if (_mac is { Receiving: true }) StartMac();
            else Log($"[Mac] Bildschirm {_macScreens.SelectedIndex + 1} gewählt — wirksam beim nächsten Verbinden");
        };

        _macFields = new Fields(140) { Dock = DockStyle.Fill };

        var macStrip = Strip(_macScreens, _connect, _disconnect);
        var (macCard, macBody) = Skin.Card("DEN MAC ANSEHEN — hier steuern");
        macBody.Controls.Add(_macFields);
        macBody.Controls.Add(macStrip);

        // ---------------------------------------------------- Karte: Dateien

        _meter = new Meter { Dock = DockStyle.Top };
        _fileInfo = new Fields(150) { Dock = DockStyle.Fill };

        var sendButton = Skin.Button("Datei senden", 118);
        sendButton.Click += (_, _) =>
        {
            using var pick = new OpenFileDialog { Title = "An den Mac senden", Multiselect = true };
            if (pick.ShowDialog(this) != DialogResult.OK) return;
            foreach (var path in pick.FileNames) SendFile(path);
        };

        var folderButton = Skin.Button("Ordner öffnen", 118);
        folderButton.Click += (_, _) =>
        {
            Directory.CreateDirectory(FileChannel.Folder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{FileChannel.Folder}\"") { UseShellExecute = true });
        };

        var fileStrip = new Panel { Dock = DockStyle.Bottom, Height = 38, BackColor = Skin.Panel };
        fileStrip.Controls.Add(sendButton);
        fileStrip.Controls.Add(folderButton);
        Place(fileStrip, () =>
        {
            folderButton.Location = new Point(fileStrip.Width - folderButton.Width, (fileStrip.Height - folderButton.Height) / 2);
            sendButton.Location = new Point(folderButton.Left - sendButton.Width - 8, folderButton.Top);
        });

        var (fileCard, fileBody) = Skin.Card("DATEIEN");
        fileBody.Controls.Add(_fileInfo);
        fileBody.Controls.Add(fileStrip);
        fileBody.Controls.Add(_meter);

        // ------------------------------------- Karten: Sicherheit, Gegenstelle

        _security = new Fields(140) { Dock = DockStyle.Fill };
        var (securityCard, securityBody) = Skin.Card("SICHERHEIT UND KOPPLUNG");
        securityBody.Controls.Add(_security);

        _peerInfo = new Fields(140) { Dock = DockStyle.Fill };
        var (peerCard, peerBody) = Skin.Card("GEGENSTELLE");
        peerBody.Controls.Add(_peerInfo);

        // ------------------------------------------------------------ Raster

        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Skin.Ground,
            ColumnCount = 2,
            RowCount = 3,
            Padding = new Padding(9),
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 226));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 200));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        grid.Controls.Add(macCard, 0, 0);
        grid.SetColumnSpan(macCard, 2);
        grid.Controls.Add(fileCard, 0, 1);
        grid.SetColumnSpan(fileCard, 2);
        grid.Controls.Add(securityCard, 0, 2);
        grid.Controls.Add(peerCard, 1, 2);

        // ------------------------------------------------ Protokoll und Leiste

        _logHost = new Panel { Dock = DockStyle.Bottom, Height = 190, Visible = false, BackColor = Skin.Panel };
        _log = new ListBox
        {
            Dock = DockStyle.Fill,
            BackColor = Skin.Panel,
            ForeColor = Skin.Muted,
            BorderStyle = BorderStyle.None,
            Font = Skin.Mono,
            IntegralHeight = false,
        };
        _logHost.Controls.Add(_log);
        _logHost.Paint += (s, e) =>
        {
            using var pen = new Pen(Skin.Line);
            e.Graphics.DrawLine(pen, 0, 0, ((Control)s!).Width, 0);
        };

        var logButton = Skin.Button("Protokoll", 100);
        logButton.Click += (_, _) => _logHost.Visible = !_logHost.Visible;

        var pairButton = Skin.Button("Koppeln", 100);
        pairButton.Click += (_, _) =>
        {
            _control?.OpenPairing();
            MessageBox.Show(this,
                "Ein weiteres Gerät kann sich jetzt zwei Minuten lang koppeln.\n\n"
                + "Die sechs Ziffern erscheinen dann hier und müssen auf beiden Bildschirmen "
                + "gleich sein.",
                "Koppeln", MessageBoxButtons.OK, MessageBoxIcon.Information);
        };

        _unpair = Skin.Button("Entkoppeln", 100);
        _unpair.Click += (_, _) => Unpair();

        var bar = new Panel { Dock = DockStyle.Bottom, Height = 46, BackColor = Skin.Panel };
        bar.Controls.Add(logButton);
        bar.Controls.Add(pairButton);
        bar.Controls.Add(_unpair);
        Place(bar, () =>
        {
            logButton.Location = new Point(16, (bar.Height - logButton.Height) / 2);
            _unpair.Location = new Point(bar.Width - _unpair.Width - 16, logButton.Top);
            pairButton.Location = new Point(_unpair.Left - pairButton.Width - 8, logButton.Top);
        });
        bar.Paint += (s, e) =>
        {
            using var pen = new Pen(Skin.Line);
            e.Graphics.DrawLine(pen, 0, 0, ((Control)s!).Width, 0);
        };

        Controls.Add(grid);
        Controls.Add(_logHost);
        Controls.Add(bar);
        Controls.Add(header);

        // Ziehen und Ablegen auf das Fenster ist der kürzere Weg als der Knopf.
        AllowDrop = true;
        DragEnter += (_, e) => e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true
            ? DragDropEffects.Copy : DragDropEffects.None;
        DragDrop += (_, e) =>
        {
            if (e.Data?.GetData(DataFormats.FileDrop) is string[] paths)
                foreach (var path in paths.Where(File.Exists)) SendFile(path);
        };

        _tick = new System.Windows.Forms.Timer { Interval = 1000 };
        _tick.Tick += (_, _) => Sample();
    }

    private Color _dotColour = Skin.Muted;

    private static ComboBox Chooser() => new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList,
        FlatStyle = FlatStyle.Flat,
        BackColor = Skin.Ground,
        ForeColor = Skin.Ink,
        Font = Skin.Small,
        Width = 220,
    };

    /// <summary>Die Leiste am Fuß einer Karte: Auswahl links, zwei Knöpfe rechts.</summary>
    private static Panel Strip(Control chooser, Control first, Control second)
    {
        var strip = new Panel { Dock = DockStyle.Bottom, Height = 38, BackColor = Skin.Panel };
        strip.Controls.Add(chooser);
        strip.Controls.Add(first);
        strip.Controls.Add(second);

        Place(strip, () =>
        {
            chooser.Location = new Point(0, (strip.Height - chooser.Height) / 2);
            second.Location = new Point(strip.Width - second.Width, (strip.Height - second.Height) / 2);
            first.Location = new Point(second.Left - first.Width - 8, second.Top);
        });

        return strip;
    }

    /// <summary>Kinder einmal setzen und bei jeder Größenänderung erneut.</summary>
    private static void Place(Control host, Action arrange)
    {
        host.Resize += (_, _) => arrange();
        host.HandleCreated += (_, _) => arrange();
        arrange();
    }

    private static string[] ScreenLabels() =>
        [.. Screen.AllScreens.Select((s, i) =>
            $"Bildschirm {i + 1} — {s.Bounds.Width}×{s.Bounds.Height}{(s.Primary ? " (Haupt)" : "")}")];

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        const int UseImmersiveDarkMode = 20;
        var on = 1;
        try { DwmSetWindowAttribute(Handle, UseImmersiveDarkMode, ref on, sizeof(int)); } catch (Exception) { }
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);

        _identity = new Identity();

        // Den Eintrag anlegen oder auf den aktuellen Pfad nachführen — ohne ihn
        // wäre das Wecken über Netz eine Sackgasse am Anmeldebildschirm.
        Autostart.Ensure(Log);

        _tray = new Tray("ScreenBridge", ShowWindow,
            ("Fenster öffnen", ShowWindow),
            ("-", null),
            ("Den Mac ansehen", StartMac),
            ("Trennen", () => { StopMac(); StopShare(); }),
            ("Datei senden…", () => { ShowWindow(); PickFile(); }),
            ("-", null),
            ("Koppeln", () => { ShowWindow(); _control?.OpenPairing(); }),
            ("Entkoppeln", () => { ShowWindow(); Unpair(); }),
            ("-", null),
            ("Mit Windows starten", ToggleAutostart),
            ("ScreenBridge beenden", () => { _reallyQuitting = true; Close(); }));

        _tray.SetChecked("Mit Windows starten", Autostart.Enabled);

        _control = new ControlServer(_identity, Log);

        _control.PairingRequested += request => BeginInvoke(() =>
        {
            // Ohne Fenster wäre die Frage nicht zu sehen, und eine Sicherheitsfrage,
            // die niemand sieht, wird von selbst beantwortet — falsch.
            ShowWindow();
            AskToPair(request);
        });

        _control.Command += OnCommand;
        _control.VideoRequested += (stream, peer) => _video?.Serve(stream, peer.Name);

        _files = new FileChannel(Log, (title, text) => BeginInvoke(() => _tray?.Notify(title, text)));
        _files.Progress += transfer =>
        {
            _transfer = transfer;
            if (IsHandleCreated) BeginInvoke(UpdateFiles);
        };
        _control.FileRequested += (stream, peer) => _files.Serve(stream, peer.Name);

        // Beim Verbinden einmal alles sagen, was sich ändern kann. Ein gespeicherter
        // Wert veraltet still — und das merkt man erst, wenn das Wecken nicht klappt.
        _control.Connected += () => _ = Task.Run(async () =>
        {
            await _control.SendAsync(Hello());
            await _control.SendAsync(new { t = "wake.info", mac = MacAddress(), broadcast = Broadcast() });

            object[] commands;
            lock (_waitingCommands) { commands = [.. _waitingCommands.Values]; _waitingCommands.Clear(); }

            foreach (var command in commands)
            {
                await _control.SendAsync(command);
                Log("[Fernzugriff] wartender Befehl nachgereicht");
            }

            object[] pending;
            lock (_waitingOffers) { pending = [.. _waitingOffers]; _waitingOffers.Clear(); }

            foreach (var offer in pending)
            {
                await _control.SendAsync(offer);
                Log("[Datei] wartendes Angebot nachgereicht");
            }
        });

        _control.Rejected += why => BeginInvoke(() => _tray?.Notify("Verbindung abgewiesen", why));

        _control.PeerChanged += peer => BeginInvoke(() =>
        {
            if (peer is null)
            {
                _peerApp = _peerSystem = "";
                _macSaidState = false;
                _macSaidSending = false;
                _macWhy = "";
                _roundTrip = -1;
                _unanswered = 0;
                _pingSentAt = 0;
            }

            _tray?.SetConnected(peer is not null,
                peer is null ? "ScreenBridge — wartet" : $"ScreenBridge — verbunden mit {peer.Name}");

            if (peer is not null) _tray?.Notify("ScreenBridge", $"{peer.Name} ist verbunden.");
            Refresh(all: true);
        });

        _video = new VideoServer(() => _screen, Fps, Log);
        _video.ConnectionChanged += _ => BeginInvoke(() => Refresh(all: true));

        _input = new InputServer(TargetBounds, () => _video?.RequestKeyFrame(), Log);

        // ------------------------------------------------ Die Gegenrichtung

        _macInput = new MacInput(message => _control.SendAsync(message), Log);
        _mac = new MacScreen(Log);

        // Der Ausgang wird bei jedem Bild neu gelesen — so ist das Fenster
        // austauschbar, ohne den Strom anzufassen.
        _mac.Sink = frame => _macWindow?.Push(frame);

        // Invoke, nicht BeginInvoke: die Zielfläche muss stehen, BEVOR das avcC kommt
        // und den Dekodierer anwirft. Sonst liefe der erste Lauf in voller Größe und
        // würde gleich wieder verworfen.
        _mac.Opened += (width, height, _) => Invoke(() =>
        {
            var window = _macWindow;
            if (window is null || window.IsDisposed)
            {
                window = new MacWindow(_macInput, Log);
                window.Stopped += () => BeginInvoke(StopMac);
                window.FormClosed += (_, _) => _macWindow = null;
                window.Settled += size => BeginInvoke(() => Refit(size));
                _macWindow = window;
            }

            window.Prepare(width, height, _identity?.Peers.FirstOrDefault()?.Name ?? "Mac");
            window.Show();
            window.Activate();

            _mac.Target = window.Viewport;

            // Es steht wieder: den Abstand zurücksetzen, damit ein späterer Abriss
            // nicht mit den 15 Sekunden von vorhin anfängt.
            _resume.Stop();
            _resumeSeconds = 2;

            Refresh(all: false);
        });

        _mac.Format += (width, height) => BeginInvoke(() =>
        {
            if (_macWindow is { IsDisposed: false } window) window.Begin(width, height);
            Refresh(all: false);
        });

        _mac.Closed += () => BeginInvoke(() =>
        {
            if (_macWindow is { IsDisposed: false } window) window.End();

            // Nur wenn der Wunsch noch steht. Nach „Trennen“ ist _macRequested −1, und
            // dann wäre ein neuer Versuch das Gegenteil dessen, was gedrückt wurde.
            if (_macRequested >= 0) Later();

            Refresh(all: false);
        });

        _resume.Tick += (_, _) =>
        {
            _resume.Stop();
            if (_macRequested < 0 || _mac is { Receiving: true }) return;

            Ask("mac", new { t = "mac.start", index = _macRequested },
                "[Mac] erneut angefordert",
                "[Mac] erneute Anforderung wartet auf die Verbindung");

            // Beim nächsten Mal länger warten — aber erst, nachdem gefragt wurde.
            _resumeSeconds = Math.Min(15, _resumeSeconds * 2);
            Later();
        };

        _control.MacScreenRequested += (stream, peer) => _mac.Serve(stream, peer.Name);

        try
        {
            _control.Start();
            _video.Start();
        }
        catch (Exception ex)
        {
            Log("FEHLER beim Start: " + ex.Message);
            _headline.Text = "Start fehlgeschlagen";
            _dotColour = Skin.Bad;

            // Ein Fehlstart darf nicht unsichtbar sein — auch nicht im Hintergrundstart.
            Opacity = 1;
            return;
        }

        Log($"ScreenBridge {Version} bereit auf {LocalAddress()}");
        Refresh(all: true);
        _tick.Start();

        if (_startHidden)
        {
            // Beim Anmelden gestartet: nur das Symbol. Aufgebaut ist trotzdem alles —
            // die Server stehen; ShowWindow holt das Fenster jederzeit hervor.
            Log("leise gestartet — Fenster über das Symbol im Infobereich");
            BeginInvoke(() => { Hide(); Opacity = 1; });
        }
    }

    private void ToggleAutostart()
    {
        var on = !Autostart.Enabled;
        Autostart.Set(on, Log);
        _tray?.SetChecked("Mit Windows starten", on);
    }

    // ==================================================== Nachrichten vom Mac

    /// <summary>
    /// Eine Zeile aus dem Steuerkanal.
    /// </summary>
    /// <remarks>
    /// Was nicht als Zustandsmeldung erkannt wird, geht an die Eingabeeinspeisung — dort
    /// liegen Zeiger und Tastatur. Die Reihenfolge ist wichtig herum: ein <c>pong</c>
    /// dürfte nie als Ereignis gezählt werden, sonst stünde bei „Eingaben“ eine Zahl,
    /// die auch ohne jede Bedienung wächst.
    /// </remarks>
    private void OnCommand(string name, JsonElement message)
    {
        _lastHeard = DateTime.UtcNow;

        switch (name)
        {
            case "hello":
                _peerApp = $"{Word(message, "name") ?? "?"}"
                           + (Word(message, "version") is { } v ? $" · Fassung {v}" : "");
                _peerSystem = Word(message, "os") ?? "";
                Log($"[Gegenstelle] {_peerApp} meldet sich, {_peerSystem}");

                if (message.TryGetProperty("macScreens", out var list) && list.ValueKind == JsonValueKind.Array)
                {
                    var labels = list.EnumerateArray()
                        .Select((screen, i) => Word(screen, "label")
                                               ?? $"Bildschirm {i + 1} — {(int)Number(screen, "width")}×{(int)Number(screen, "height")}")
                        .ToArray();

                    var active = message.TryGetProperty("macActive", out var a) && a.ValueKind == JsonValueKind.Number
                        ? a.GetInt32() : 0;

                    BeginInvoke(() => ShowMacScreens(labels, active));
                }

                break;

            case "pong":
                if (Number(message, "n") == _pingNumber && _pingSentAt != 0)
                {
                    _roundTrip = (Stopwatch.GetTimestamp() - _pingSentAt) * 1000.0 / Stopwatch.Frequency;
                    _pingSentAt = 0;
                    _unanswered = 0;
                    _everAnswered = true;
                }
                break;

            case "ping":
                _ = _control?.SendAsync(new { t = "pong", n = Number(message, "n") });
                break;

            case "screen.select":
                var index = (int)Number(message, "index");
                BeginInvoke(() => ChooseScreen(index));
                break;

            case "screen.state":
                _macSaidState = true;
                _macShowing = Flag(message, "showing");
                _macFullscreen = Flag(message, "fullscreen");
                break;

            case "mac.state":
                var sending = Flag(message, "sending");
                var why = Word(message, "why") ?? Word(message, "reason") ?? "";

                if (_macSaidSending && sending == _macSending && why == _macWhy) break;

                _macSaidSending = true;
                _macSending = sending;
                _macWhy = why;

                Log(sending
                    ? "[Mac] der Mac sendet"
                    : $"[Mac] der Mac sendet nicht{(why.Length > 0 ? " — " + why : "")}");

                if (IsHandleCreated) BeginInvoke(() => Refresh(all: false));
                break;

            case "file.done":
                if (_transfer is { } done)
                    _transfer = done with { State = Flag(message, "ok") ? "beim Mac angekommen" : "beim Mac fehlgeschlagen" };
                if (IsHandleCreated) BeginInvoke(UpdateFiles);
                break;

            case "file.progress":
                break;   // der eigene Fortschritt aus FileChannel ist genauer

            default:
                _input?.Handle(message);
                break;
        }
    }

    private object Hello() => new
    {
        t = "hello",
        name = Environment.MachineName,
        os = $"Windows {Environment.OSVersion.Version.Build}",
        version = Version,
        screens = Screen.AllScreens.Select((s, i) => new
        {
            index = i,
            width = s.Bounds.Width,
            height = s.Bounds.Height,
            primary = s.Primary,
            label = $"Bildschirm {i + 1} — {s.Bounds.Width}×{s.Bounds.Height}{(s.Primary ? " (Haupt)" : "")}",
        }).ToArray(),
        active = _screen,
    };

    // ================================================== Fernzugriff bedienen

    /// <summary>
    /// Die Freigabe dieses PCs beenden.
    /// </summary>
    /// <remarks>
    /// Angefordert wird sie nur noch vom MacBook aus — wer diesen Bildschirm sehen will,
    /// sitzt ohnehin dort. Der Weg, sie von hier aus <i>abzubrechen</i>, bleibt: das ist
    /// keine Bedienung derselben Sache, sondern die Möglichkeit, sie zu beenden, ohne
    /// aufzustehen.
    /// </remarks>
    private void StopShare()
    {
        Ask("screen", new { t = "screen.stop" },
            "[Freigabe] beendet",
            "[Freigabe] wird beendet, sobald der Mac wieder da ist");

        // Auch selbst schließen: eine Bitte, die nicht ankommt, dürfte nicht bedeuten,
        // dass der Bildschirm weiter hinausgeht.
        _video?.Stop();
    }

    /// <summary>
    /// Den Mac-Bildschirm hierher holen.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Das ist die Richtung, die gefehlt hat.</b> Wer am PC sitzt und dort auf
    /// „Verbinden“ drückt, will den Mac sehen — nicht seinen eigenen Bildschirm
    /// hinausschicken. Der Knopf tat vorher das Zweite und hieß wie das Erste.
    /// </para>
    /// <para>
    /// Auch hier bittet der PC nur: der Mac baut die Verbindung auf, mit der Kennung
    /// <c>M</c>. An der Richtung des Verbindungsaufbaus ändert sich nichts, nur an der
    /// Richtung des Bildes.
    /// </para>
    /// </remarks>
    private void StartMac()
    {
        if (!Paired("Verbinden")) return;

        var index = Math.Max(0, _macScreens.SelectedIndex);

        // Der Kopf mit Breite und Höhe gilt je Verbindung. Ein ANDERER Bildschirm ist
        // deshalb ein neuer Kanal — derselbe schon nicht mehr.
        //
        // Genau hier saß in der Gegenrichtung eine Schleife: läuft der gewünschte
        // Bildschirm bereits, und man reisst den Kanal trotzdem ab, wirft der neue drüben
        // den alten hinaus, dessen Ende liest die Gegenseite als Abbruch, und sie verbindet
        // neu. Gemessen wurden 25 Neuverbindungen in 20 Sekunden.
        if (_mac is { Receiving: true } && index != _macRequested) _mac.Stop();

        // Steht das Bild schon, ist „Verbinden“ nur die Bitte, es wieder anzusehen.
        if (_mac is { Receiving: true } && index == _macRequested
            && _macWindow is { IsDisposed: false } open)
        {
            open.Show();
            open.Activate();
        }

        _macRequested = index;

        var room = Viewport();
        _told = room;

        Ask("mac", new { t = "mac.start", index, width = room.Width, height = room.Height },
            $"[Mac] Bildschirm {index + 1} des Macs angefordert, {room.Width}×{room.Height}",
            $"[Mac] Bildschirm {index + 1} des Macs wird angefordert, sobald er wieder da ist");
    }

    /// <summary>
    /// Wie viel Bild hier überhaupt zu sehen ist.
    /// </summary>
    /// <remarks>
    /// Ohne Fenster die Arbeitsfläche des Hauptbildschirms — mehr könnte auch dann nicht
    /// angezeigt werden.
    /// </remarks>
    private Size Viewport()
    {
        if (_macWindow is { IsDisposed: false } window && window.Viewport.Width > 16) return window.Viewport;

        var room = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
        return new Size(room.Width, room.Height);
    }

    private void StopMac()
    {
        Ask("mac", new { t = "mac.stop" },
            "[Mac] Bildempfang beendet",
            "[Mac] Bildempfang wird beendet, sobald der Mac wieder da ist");

        _mac?.Stop();
        _macRequested = -1;
        _told = Size.Empty;
        _resume.Stop();
        _resumeSeconds = 2;

        if (_macWindow is { IsDisposed: false } window && window.Visible) window.Hide();
        Refresh(all: false);
    }

    /// <summary>
    /// Die Bildschirme des Macs in die Auswahl übernehmen.
    /// </summary>
    /// <remarks>
    /// Bis der Mac sie nennt, steht dort ein einzelner Eintrag. Eine leere Auswahl wäre
    /// die ehrlichere Anzeige und die schlechtere Bedienung: der Knopf wäre dann tot,
    /// bevor irgendetwas schiefgegangen ist.
    /// </remarks>
    private void ShowMacScreens(string[] labels, int active)
    {
        if (labels.Length == 0) return;

        var same = _macScreens.Items.Count == labels.Length
                   && !labels.Where((label, i) => (string)_macScreens.Items[i]! != label).Any();

        if (same) return;

        _suppress = true;
        try
        {
            _macScreens.Items.Clear();
            foreach (var label in labels) _macScreens.Items.Add(label);
            _macScreens.SelectedIndex = Math.Clamp(active, 0, labels.Length - 1);
        }
        finally { _suppress = false; }

        Log($"[Mac] {labels.Length} Bildschirm(e) gemeldet");
    }

    /// <summary>Den nächsten Versuch ansetzen.</summary>
    private void Later()
    {
        _resumeAt = DateTime.UtcNow.AddSeconds(_resumeSeconds);
        _resume.Interval = _resumeSeconds * 1000;
        _resume.Start();
    }

    /// <summary>
    /// Das Fenster hat eine neue Größe und steht wieder still.
    /// </summary>
    /// <remarks>
    /// Der Dekodierer liefert genau in Fenstergröße; ändert sie sich, muss er neu
    /// aufgestellt werden. Danach fehlt ihm der Einstiegspunkt — deshalb gleich ein
    /// Schlüsselbild anfordern, sonst bleibt die Fläche schwarz, bis von selbst eines
    /// kommt.
    /// </remarks>
    private void Refit(Size viewport)
    {
        if (_mac is null || !_mac.Receiving) return;

        // Nur bei einer WIRKLICHEN Änderung, und das ist die Lehre aus dem Flackern.
        //
        // Die Gegenseite baut ihre Aufnahme neu auf, wenn sie eine neue Größe hört — das
        // ist richtig, denn die Maße stehen im Kodierer fest. Also darf hier nur etwas
        // hinausgehen, wenn sich auch etwas geändert hat. Eine Meldung „ist immer noch
        // 1600×1040“ kostet dort einen Neuaufbau und hier ein schwarzes Bild.
        //
        // Dieselbe Schwelle wie beim eigenen Dekodierer: unter einem Zehntel lohnt kein
        // Neuaufbau, und darunter sähe man den Unterschied ohnehin nicht.
        if (!Differs(_told, viewport)) return;

        _told = viewport;
        _ = _control?.SendAsync(new { t = "mac.size", width = viewport.Width, height = viewport.Height });
        Log($"[Mac] Fensterfläche {viewport.Width}×{viewport.Height} gemeldet");

        if (!_mac.Retarget(viewport)) return;
        _macInput?.KeyFrame();
    }

    /// <summary>Zuletzt der Gegenseite genannte Fläche; leer heißt: noch keine.</summary>
    private Size _told;

    /// <summary>Unter einem Zehntel Unterschied lohnt kein Neuaufbau.</summary>
    private static bool Differs(Size a, Size b) =>
        a.IsEmpty
        || Math.Abs(a.Width - b.Width) > Math.Max(8, a.Width / 10)
        || Math.Abs(a.Height - b.Height) > Math.Max(8, a.Height / 10);

    /// <summary>Ohne Kopplung geht nichts davon — und das gehört gesagt, nicht verschluckt.</summary>
    private bool Paired(string what)
    {
        if (_identity is { Peers.Count: > 0 }) return true;

        MessageBox.Show(this,
            "Es ist noch kein Gerät gekoppelt.\n\n"
            + "Die Mac-App einmal verbinden lassen — die sechs Ziffern erscheinen dann hier.",
            what, MessageBoxButtons.OK, MessageBoxIcon.Information);
        return false;
    }

    /// <summary>
    /// Der Mac hat einen Bildschirm gewählt.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Hier steht immer ein <c>screen.start</c> am Ende</b>, und das ist keine
    /// Höflichkeit. Der Mac schließt seinen Bildkanal, <i>bevor</i> er die Wahl meldet —
    /// zwei Kanäle nebeneinander läsen sonst denselben Kopf verschieden. Er wartet
    /// danach auf die Aufforderung und öffnet von sich aus nicht neu. Wer hier an
    /// „läuft gerade ein Bildkanal?“ knüpfte, ließe ihn genau dann warten, wenn er
    /// wartet: die Antwort ist zu diesem Zeitpunkt immer nein.
    /// </para>
    /// <para>
    /// Der laufende Kanal wird nur abgeräumt, wenn er einen anderen Bildschirm zeigt.
    /// Drückt der Nutzer drüben auf „Verbinden“, öffnet der Mac selbst und meldet dieselbe
    /// Wahl — den gerade aufgebauten Kanal dafür wieder abzureißen wäre ein Flackern
    /// ohne Anlass.
    /// </para>
    /// </remarks>
    private void ChooseScreen(int index)
    {
        if (index < 0 || index >= Screen.AllScreens.Length) return;

        Log($"[Freigabe] der Mac wählt Bildschirm {index + 1}");
        _screen = index;

        if (_video is { Connected: true } video && video.ScreenIndex != index) video.Stop();

        _ = _control?.SendAsync(Hello());
        Ask("screen", new { t = "screen.start", index },
            $"[Freigabe] Bildschirm {index + 1} freigegeben",
            $"[Freigabe] Bildschirm {index + 1} wird freigegeben, sobald der Mac wieder da ist");
    }

    /// <summary>
    /// Etwas hinausschicken — oder es merken, bis wieder jemand da ist.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Je Richtung bleibt nur der letzte Befehl liegen. „Verbinden“ und danach „Trennen“
    /// nacheinander abzuarbeiten hieße, dass der Bildschirm für einen Moment hinausgeht,
    /// den niemand wollte.
    /// </para>
    /// <para>
    /// <b>Getrennt nach Richtung, nicht in einem Fach.</b> Sonst löschte ein „den Mac
    /// ansehen“ das wartende „diesen PC freigeben“ — zwei Wünsche, die einander nicht
    /// widersprechen.
    /// </para>
    /// </remarks>
    private void Ask(string slot, object message, string done, string waiting)
    {
        _ = Task.Run(async () =>
        {
            if (_control is not null && await _control.SendAsync(message))
            {
                Log(done);
                return;
            }

            lock (_waitingCommands) _waitingCommands[slot] = message;
            Log(waiting);
        });
    }

    // ======================================================== Dateien senden

    private void PickFile()
    {
        using var pick = new OpenFileDialog { Title = "An den Mac senden", Multiselect = true };
        if (pick.ShowDialog(this) != DialogResult.OK) return;
        foreach (var path in pick.FileNames) SendFile(path);
    }

    /// <summary>
    /// Eine Datei zum Abholen bereitlegen und die Gegenstelle darauf hinweisen.
    /// </summary>
    /// <remarks>
    /// Angeboten, nicht geschickt: in dieser Richtung gibt es auf dem Mac niemanden, bei
    /// dem dieser Rechner anklopfen könnte. Der Mac holt ab.
    /// </remarks>
    private async void SendFile(string path)
    {
        if (_files is null || _control is null) return;

        if (_identity is { Peers.Count: 0 })
        {
            MessageBox.Show(this,
                "Es ist noch kein Gerät gekoppelt.\n\n"
                + "Die Mac-App einmal verbinden lassen — danach geht eine Datei ohne weiteres Zutun hinaus.",
                "Datei senden", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var offer = _files.Offer(path);
        if (offer is null) return;

        var (id, name, size, hash) = offer.Value;
        var message = new { t = "file.offer", id, name, size, sha256 = hash };

        if (await _control.SendAsync(message))
        {
            Log($"[Datei] „{name}“ liegt bereit, {FileChannel.Humanize(size)} — der Mac kann sie abholen");
            return;
        }

        lock (_waitingOffers) _waitingOffers.Add(message);

        _transfer = new Transfer(name, 0, size, false, "wartet auf die Verbindung");
        Log($"[Datei] „{name}“ liegt bereit — gerade ist nichts verbunden, "
            + "das Angebot geht hinaus, sobald der Mac wieder da ist");
        _tray?.Notify("Datei wartet", $"„{name}“ geht hinaus, sobald der Mac wieder verbunden ist.");

        UpdateFiles();
    }

    // ============================================================== Kopplung

    /// <summary>
    /// Die Kopplung bestätigen lassen.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Es geht hier nicht darum, ob der Benutzer koppeln <i>möchte</i>. Es geht darum, ob
    /// die sechs Ziffern auf beiden Bildschirmen <b>dieselben</b> sind. Sie entstehen aus
    /// beiden Fingerabdrücken und je einer vorab festgelegten Zufallszahl; wer sich
    /// dazwischenstellt, erzeugt auf jeder Seite eine andere Zahl. Deshalb steht in der
    /// Frage der Vergleich und nicht die Zustimmung.
    /// </para>
    /// <para>
    /// Bewusst ein schlichter Systemdialog: eine Sicherheitsfrage soll aussehen wie eine,
    /// und nicht wie ein Teil unserer Oberfläche, den man wegklickt.
    /// </para>
    /// </remarks>
    private void AskToPair(PairingRequest request)
    {
        var answer = MessageBox.Show(
            this,
            $"{request.Name} ({request.Address}) möchte sich koppeln.\n\n"
            + $"Ziffern:   {request.Code}\n\n"
            + "Stehen dieselben sechs Ziffern auf dem anderen Gerät?\n"
            + "Nur dann steht niemand dazwischen.",
            "Koppeln bestätigen",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);

        request.Answer.TrySetResult(answer == DialogResult.Yes);
        Refresh(all: true);
    }

    /// <summary>
    /// Eine Kopplung aufheben.
    /// </summary>
    /// <remarks>
    /// Der Weg zurück muss es geben, und zwar sichtbar: geht ein Gerät verloren oder
    /// wechselt den Besitzer, bleibt sein Schlüssel sonst für immer angeheftet. Nach dem
    /// Entkoppeln kommt es nur über eine neue Ziffernbestätigung wieder herein.
    /// </remarks>
    private void Unpair()
    {
        if (_identity is null || _identity.Peers.Count == 0)
        {
            MessageBox.Show(this, "Es ist nichts gekoppelt.", "Entkoppeln",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var names = string.Join(", ", _identity.Peers.Select(x => x.Name));
        var answer = MessageBox.Show(
            this,
            $"Kopplung mit {names} aufheben?\n\n"
            + "Danach kommt das Gerät nur über eine neue Ziffernbestätigung wieder herein.",
            "Entkoppeln",
            MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);

        if (answer != DialogResult.Yes) return;

        foreach (var peer in _identity.Peers.ToArray()) _identity.Forget(peer.Fingerprint);

        Log($"Kopplung mit {names} aufgehoben");
        Refresh(all: true);
    }

    // ============================================================== Anzeigen

    private void Sample()
    {
        // Verborgen malt niemand mit — dann braucht die Anzeige auch keine
        // Sekundenpflege. Der Ping darunter läuft weiter: er gehört der Verbindung,
        // nicht dem Fenster. Beim Hervorholen frischt ShowWindow einmal ganz auf.
        if (Visible && WindowState != FormWindowState.Minimized) Refresh(all: false);

        // Alle zwei Sekunden fragen, ob drüben noch jemand zuhört. Eine offene
        // TCP-Verbindung sagt darüber nichts: sie steht auch dann noch, wenn die
        // Gegenstelle längst hängt.
        if (++_ticks % 2 != 0) return;
        if (_control is not { Talking: true })
        {
            _pingSentAt = 0;
            return;
        }

        if (_pingSentAt != 0) _unanswered++;

        _pingNumber++;
        _pingSentAt = Stopwatch.GetTimestamp();
        _ = _control.SendAsync(new { t = "ping", n = _pingNumber });
    }

    private void Refresh(bool all)
    {
        UpdateMac();
        UpdatePeer();
        UpdateFiles();
        UpdateHeadline();
        if (all) UpdateSecurity();
    }

    private void UpdateHeadline()
    {
        string text;
        Color colour;

        var name = _identity?.Peers.FirstOrDefault()?.Name;
        var picture = _mac is { Receiving: true } || _video is { Connected: true };

        if (_identity is null || _identity.Peers.Count == 0)
        {
            text = "Noch nicht gekoppelt";
            colour = Skin.Warn;
        }
        else if (_control is not { Talking: true } && picture)
        {
            // Der unangenehmste Zustand von allen, und vorher stand hier „Wartet auf …“:
            // ein Bild läuft, aber der Kanal für Eingaben, Dateien und Zustand steht nicht.
            // Wer das sieht, würde sonst die Ursache beim Bild suchen, wo sie nicht liegt.
            text = "Bild läuft — aber ohne Steuerkanal";
            colour = Skin.Warn;
        }
        else if (_control is not { Talking: true })
        {
            text = $"Wartet auf {name}";
            colour = Skin.Muted;
        }
        else if (_mac is { Receiving: true } && _video is { Connected: true })
        {
            text = $"Beide Richtungen laufen mit {name}";
            colour = Skin.Good;
        }
        else if (_mac is { Receiving: true })
        {
            text = $"Sieht {name} zu";
            colour = Skin.Good;
        }
        else if (_video is { Connected: true })
        {
            text = $"{name} sieht zu";
            colour = Skin.Good;
        }
        else
        {
            text = $"Verbunden mit {name}";
            colour = Skin.Good;
        }

        if (_headline.Text != text) _headline.Text = text;
        if (_dotColour != colour) { _dotColour = colour; _headline.Invalidate(); }

        var line = $"{Environment.MachineName}   ·   {LocalAddress()}:{ControlServer.Port}   ·   ScreenBridge {Version}";
        if (_identity is { Peers.Count: 0 })
            line += "   ·   bereit zum Koppeln, ohne Knopfdruck";

        if (_subline.Text != line) _subline.Text = line;

        var talking = _control is { Talking: true };
        _connect.Enabled = talking;
        _disconnect.Enabled = picture || talking;

        _tray?.Enable("Den Mac ansehen", talking);
        _tray?.Enable("Trennen", _disconnect.Enabled);

        // Das Symbol folgt jedem lebenden Kanal, nicht nur der Steuerung. Ein laufender
        // Bildkanal bei grauem Symbol wäre dieselbe Lüge wie „Wartet auf …“ oben.
        _tray?.SetConnected(talking || picture,
            !talking && picture ? "ScreenBridge — Bild ohne Steuerkanal"
            : talking ? $"ScreenBridge — verbunden mit {name}"
            : "ScreenBridge — wartet");
    }

    /// <summary>
    /// Die Gegenrichtung: was vom Mac hereinkommt.
    /// </summary>
    /// <remarks>
    /// <b>Die Verzögerung steht hier und nicht bei der Freigabe</b>, weil sie nur in
    /// dieser Richtung messbar ist: der Weg vom Kodierer bis zum fertigen Bild liegt
    /// ganz auf diesem Rechner. Was der Mac von uns sieht, weiß nur er.
    /// </remarks>
    private void UpdateMac()
    {
        var mac = _mac;
        var running = mac is { Receiving: true };

        if (!running || mac is null)
        {
            _macLastFrames = 0;
            _macLastBytes = 0;
            _macLastSample = DateTime.UtcNow;

            var waiting = _macRequested >= 0 && _resume.Enabled;
            var left = Math.Max(0, (int)Math.Ceiling((_resumeAt - DateTime.UtcNow).TotalSeconds));

            // Der Grund von drüben hat Vorrang vor der eigenen Vermutung: nur der Mac
            // weiß, ob sein Display aus ist oder ob nur die Leitung blinzelt.
            var reason = _macSaidSending && !_macSending && _macWhy.Length > 0
                ? _macWhy
                : "abgerissen";

            _macFields.Set("Zustand",
                waiting ? $"{reason} — neuer Versuch in {left} s"
                : _control is { Talking: true } ? "steht — mit „Verbinden“ kommt das Bild des Macs"
                : "steht",
                waiting ? Skin.Warn : Skin.Muted);
            _macFields.Set("Bildschirm", "—", Skin.Muted);
            _macFields.Set("Durchsatz", "—", Skin.Muted);
            _macFields.Set("Verzögerung", "—", Skin.Muted);
            _macFields.Set("Steuerung", _macInput is null ? "—" : $"{_macInput.Sent} Ereignisse an den Mac");
            return;
        }

        var now = DateTime.UtcNow;
        var seconds = (now - _macLastSample).TotalSeconds;
        if (seconds > 0.2)
        {
            _macFps = (mac.Frames - _macLastFrames) / seconds;
            _macMegabits = (mac.Bytes - _macLastBytes) * 8 / seconds / 1_000_000;
            _macLastFrames = mac.Frames;
            _macLastBytes = mac.Bytes;
            _macLastSample = now;
        }

        _macFields.Set("Zustand", $"läuft — seit {Ago(mac.Since)}", Skin.Good);
        _macFields.Set("Bildschirm", $"{mac.Sender}, {mac.Width}×{mac.Height}"
                                    + (_macWindow is { IsDisposed: false } win && win.Viewport.Width > 0
                                        ? $"   ·   dekodiert auf {win.Viewport.Width}×{win.Viewport.Height}"
                                        : ""));
        _macFields.Set("Durchsatz", $"{_macFps:0}/s   ·   {_macMegabits:0.0} Mbit/s"
                                    + (_macWindow is { IsDisposed: false } w ? $"   ·   {w.Dropped} aus" : ""));

        _macFields.Set("Verzögerung", mac.BeatMs == 0
            ? "wird gemessen"
            : $"{mac.LatencyMs} ms   ·   Takt {mac.BeatMs}±{mac.JitterMs} ms   ·   Lücke {mac.MaxGapMs} ms",
            mac.LatencyMs <= 40 ? Skin.Good : Skin.Warn);

        _macFields.Set("Steuerung", _macInput is null ? "—" : $"{_macInput.Sent} Ereignisse an den Mac");
    }

    private long _macLastFrames;
    private long _macLastBytes;
    private DateTime _macLastSample = DateTime.UtcNow;
    private double _macFps;
    private double _macMegabits;

    private void UpdatePeer()
    {
        var talking = _control is { Talking: true };
        var picture = _mac is { Receiving: true } || _video is { Connected: true };

        // Ganz oben, weil an ihm alles andere hängt: ohne Steuerkanal gibt es keine
        // Eingaben, keine Dateien und keinen Zustand — auch dann nicht, wenn ein Bild
        // läuft. Dass beides unabhängig ist, sieht man sonst nirgends.
        _peerInfo.Set("Steuerkanal",
            talking ? $"steht seit {Ago(_control!.Since)}"
            : picture ? "getrennt — Bild läuft, Eingaben und Dateien gehen nicht"
            : "getrennt",
            talking ? Skin.Good : picture ? Skin.Bad : Skin.Muted);

        _peerInfo.Set("App drüben", _peerApp.Length > 0 ? _peerApp
            : talking ? "meldet sich nicht mit Namen" : "—",
            _peerApp.Length > 0 ? Skin.Ink : Skin.Muted);

        _peerInfo.Set("System", _peerSystem.Length > 0 ? _peerSystem : "—",
            _peerSystem.Length > 0 ? Skin.Ink : Skin.Muted);

        var address = talking ? _control!.PeerAddress
            : _identity?.Peers.FirstOrDefault()?.Address ?? "";
        _peerInfo.Set("Adresse", address.Length > 0 ? address + (talking ? "" : "   (zuletzt bekannt)") : "—",
            talking ? Skin.Ink : Skin.Muted);

        var (answer, answerColour) = AnswerTime(talking);
        _peerInfo.Set("Antwortzeit", answer, answerColour);

        _peerInfo.Set("Zuletzt gehört", talking ? Ago(_lastHeard) : "—", talking ? Skin.Ink : Skin.Muted);

        _peerInfo.Set("Fenster drüben", !talking ? "—"
            : !_macSaidState ? "sagt nichts dazu"
            : _macShowing ? (_macFullscreen ? "sichtbar, Vollbild" : "sichtbar")
            : "geschlossen",
            !talking || !_macSaidState ? Skin.Muted : _macShowing ? Skin.Good : Skin.Muted);

        UpdateShare();
    }

    /// <summary>
    /// Die andere Richtung: sieht der Mac gerade diesen PC?
    /// </summary>
    /// <remarks>
    /// Nur noch eine Zeile, seit die Karte dafür weg ist. Bedient wird sie am MacBook;
    /// hier steht sie, weil „der Bildschirm geht gerade hinaus“ nichts ist, das man
    /// erraten möchte. Der Weg, sie zu beenden, liegt auf demselben „Trennen“ wie die
    /// Gegenrichtung.
    /// </remarks>
    private void UpdateShare()
    {
        var video = _video;

        if (video is not { Connected: true })
        {
            _lastFrames = 0;
            _lastBytes = 0;
            _lastSample = DateTime.UtcNow;

            _peerInfo.Set("Sieht diesen PC", "nein", Skin.Muted);
            return;
        }

        var now = DateTime.UtcNow;
        var seconds = (now - _lastSample).TotalSeconds;
        if (seconds > 0.2)
        {
            _framesPerSecond = (video.Frames - _lastFrames) / seconds;
            _megabits = (video.Bytes - _lastBytes) * 8 / seconds / 1_000_000;
            _lastFrames = video.Frames;
            _lastBytes = video.Bytes;
            _lastSample = now;
        }

        _peerInfo.Set("Sieht diesen PC",
            $"Bildschirm {video.ScreenIndex + 1}   ·   {_framesPerSecond:0}/s   ·   {_megabits:0.0} Mbit/s"
            + $"   ·   {_input?.Events ?? 0} Eingaben",
            Skin.Good);
    }

    /// <summary>
    /// Die Antwortzeit — und die drei Fälle, in denen es keine gibt.
    /// </summary>
    /// <remarks>
    /// „Keine Antwort“ wäre für eine Mac-App, die <c>ping</c> gar nicht kennt, eine
    /// falsche Auskunft: dort ist nichts kaputt, dort fehlt nur eine Zeile im Protokoll.
    /// Erst wenn schon einmal eine Antwort kam, heißt Ausbleiben wirklich Ausbleiben.
    /// </remarks>
    private (string Text, Color Colour) AnswerTime(bool talking)
    {
        if (!talking) return ("—", Skin.Muted);

        if (_unanswered >= 3)
            return _everAnswered
                ? ("keine Antwort", Skin.Bad)
                : ("antwortet nicht auf ping — ältere Fassung?", Skin.Muted);

        if (_roundTrip < 0) return ("wird gemessen", Skin.Muted);

        return ($"{_roundTrip:0.0} ms", _roundTrip < 50 ? Skin.Good : Skin.Warn);
    }

    private void UpdateSecurity()
    {
        if (_identity is null) return;

        var peer = _identity.Peers.FirstOrDefault();
        var talking = _control is { Talking: true };

        _security.Set("Dieser PC", $"{Environment.MachineName}   ·   {_identity.ReadableFingerprint}");

        _security.Set("Gekoppelt mit", peer?.Name ?? "noch niemand", peer is null ? Skin.Warn : Skin.Good);
        _security.Set("Dessen Kennung", peer is null ? "—" : Identity.Readable(peer.Fingerprint),
            peer is null ? Skin.Muted : Skin.Ink);
        _security.Set("Gekoppelt seit", peer is null ? "—" : peer.PairedAt.ToLocalTime().ToString("dd.MM.yyyy HH:mm"),
            peer is null ? Skin.Muted : Skin.Ink);

        _security.Set("Verschlüsselung", talking ? $"{Pretty(_control!.Protocol)}   ·   {_control.Cipher}" : "—",
            talking ? Skin.Good : Skin.Muted);

        _security.Set("Prüfung", talking
            ? "beidseitig, Zertifikat angeheftet"
            : peer is null ? "—" : "gegen den angehefteten Fingerabdruck",
            talking ? Skin.Good : Skin.Muted);

        _unpair.Enabled = peer is not null;
        _tray?.Enable("Entkoppeln", peer is not null);
    }

    private void UpdateFiles()
    {
        _fileInfo.Set("Empfangene liegen in", FileChannel.Folder);

        int waiting;
        lock (_waitingOffers) waiting = _waitingOffers.Count;
        var open = (_files?.Waiting ?? 0);

        _fileInfo.Set("Bereitgelegt", open == 0 && waiting == 0
            ? "nichts"
            : $"{open} zum Abholen"
              + (waiting > 0 ? $"   ·   {waiting} wartet auf die Verbindung" : ""),
            open == 0 && waiting == 0 ? Skin.Muted : Skin.Ink);

        if (_transfer is not { } t)
        {
            _meter.Show("Noch nichts übertragen", "Datei hierher ziehen oder „Datei senden“", -1, Skin.Muted);
            return;
        }

        var finished = t.State.Contains("angekommen") || t.State.Contains("übergeben");
        var broken = t.State.Contains("abgebrochen") || t.State.Contains("falsch")
                     || t.State.Contains("fehlgeschlagen");

        var colour = broken ? Skin.Bad : finished ? Skin.Good : Skin.Accent;
        var arrow = t.Incoming ? "vom Mac" : "an den Mac";

        var detail = t.Size > 0 && !finished && t.Done > 0
            ? $"{FileChannel.Humanize(t.Done)} von {FileChannel.Humanize(t.Size)}   ·   {t.State}"
            : $"{FileChannel.Humanize(t.Size)}   ·   {t.State}";

        var fraction = finished ? 1 : t.Size > 0 ? (double)t.Done / t.Size : 0;
        _meter.Show($"{arrow}:  {t.Name}", detail, fraction, colour);
    }

    private static string Pretty(string protocol) => protocol switch
    {
        "Tls13" => "TLS 1.3",
        "Tls12" => "TLS 1.2",
        _ => protocol,
    };

    private static string ScreenName(int index)
    {
        var all = Screen.AllScreens;
        return index >= 0 && index < all.Length
            ? $"Bildschirm {index + 1} ({all[index].Bounds.Width}×{all[index].Bounds.Height})"
            : "—";
    }

    private static string Ago(DateTime utc)
    {
        if (utc == default) return "—";

        var span = DateTime.UtcNow - utc;
        if (span.TotalSeconds < 1) return "gerade eben";
        if (span.TotalSeconds < 60) return $"{span.TotalSeconds:0} s";
        if (span.TotalMinutes < 60) return $"{span.TotalMinutes:0} min";
        if (span.TotalHours < 24) return $"{span.TotalHours:0} h";
        return $"{span.TotalDays:0} Tagen";
    }

    // ================================================================ Umfeld

    /// <summary>Der Bereich, auf den die Eingaben gelegt werden — der aufgenommene Bildschirm.</summary>
    private Rectangle TargetBounds()
    {
        var all = Screen.AllScreens;
        return all[Math.Clamp(_screen, 0, all.Length - 1)].Bounds;
    }

    /// <summary>Die Kennung der Netzkarte, mit der der Mac uns wecken kann.</summary>
    private static string MacAddress()
    {
        var card = BestCard();
        return card is null ? "" : string.Concat(card.GetPhysicalAddress().GetAddressBytes().Select(b => b.ToString("X2")));
    }

    /// <summary>
    /// Unsere Rundrufadresse — als Rückfallwert.
    /// </summary>
    /// <remarks>
    /// Maßgeblich ist die des Macs: unsere gilt für dieses Netz zum Zeitpunkt des
    /// Sendens. Steckt der Mac später woanders, ginge ein Paket an diese Adresse lautlos
    /// ins Leere.
    /// </remarks>
    private static string Broadcast()
    {
        foreach (var card in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (card.OperationalStatus != OperationalStatus.Up) continue;

            foreach (var address in card.GetIPProperties().UnicastAddresses)
            {
                if (address.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                if (address.IPv4Mask is null) continue;

                var ip = address.Address.GetAddressBytes();
                var mask = address.IPv4Mask.GetAddressBytes();
                if (ip[0] == 127) continue;

                var cast = new byte[4];
                for (var i = 0; i < 4; i++) cast[i] = (byte)(ip[i] | ~mask[i]);
                return new IPAddress(cast).ToString();
            }
        }

        return "255.255.255.255";
    }

    /// <summary>Die echte Karte, nicht die virtuelle.</summary>
    private static NetworkInterface? BestCard() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up)
            .Where(n => n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
            .Where(n => n.GetIPProperties().GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork))
            .OrderBy(n => n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? 1 : 0)
            .ThenByDescending(n => n.Speed)
            .FirstOrDefault();

    private static string LocalAddress() =>
        BestCard()?.GetIPProperties().UnicastAddresses
            .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork)?.Address.ToString()
        ?? IPAddress.Loopback.ToString();

    /// <summary>
    /// Das Fenster wieder hervorholen, egal in welchem Zustand es war.
    /// </summary>
    /// <remarks>
    /// <c>internal</c>, weil auch ein Zweitstart der EXE hier landet: er endet
    /// sofort, reicht aber vorher „zeig dich“ an die laufende Instanz weiter.
    /// </remarks>
    internal void ShowWindow()
    {
        Show();
        WindowState = FormWindowState.Normal;
        ShowInTaskbar = true;
        Activate();

        // Während das Fenster verborgen war, hat der Sekundentakt es nicht gepflegt.
        Refresh(all: true);
    }

    /// <summary>
    /// Eine Zeile ins Protokoll.
    /// </summary>
    /// <remarks>
    /// Die Taktzeilen gehen nur in die Datei. Sie kommen alle fünf Sekunden und haben die
    /// Liste vorher so gefüllt, dass zwischen ihnen nichts anderes mehr zu finden war —
    /// und ihr Inhalt steht jetzt ohnehin in der Karte „Fernzugriff“.
    /// </remarks>
    public void Log(string line)
    {
        var stamped = $"{DateTime.Now:HH:mm:ss.fff}  {line}";

        try
        {
            // Beim Anlegen mit Stückliste: PowerShell 5.1 und Notepad lesen eine Datei
            // ohne sie als ANSI, und dann steht dort „hÃ¶rt“ statt „hört“. Angehängt
            // wird danach ohne — eine zweite Stückliste mitten in der Datei wäre selbst
            // ein Zeichen.
            if (!File.Exists(_logPath))
                File.WriteAllText(_logPath, "", new System.Text.UTF8Encoding(true));

            File.AppendAllText(_logPath, stamped + Environment.NewLine);
        }
        catch { }

        if (line.StartsWith("[Takt]", StringComparison.Ordinal)) return;
        if (IsDisposed || !IsHandleCreated) return;

        try
        {
            BeginInvoke(() =>
            {
                _log.BeginUpdate();
                _log.Items.Add(stamped);
                while (_log.Items.Count > MaxLogLines) _log.Items.RemoveAt(0);
                _log.TopIndex = _log.Items.Count - 1;
                _log.EndUpdate();
            });
        }
        catch (Exception) { }
    }

    /// <summary>
    /// Ein Textfeld der Gegenstelle — eingedampft auf eine Zeile Sichtbares.
    /// </summary>
    /// <remarks>
    /// Alles, was hier durchläuft, ist Anzeige: Namen, Systemangaben, Begründungen.
    /// Sie landen in Beschriftungen und im Protokoll; ein Zeilenumbruch oder ein
    /// Formatzeichen darin wäre fremder Text an einer Stelle, die wie eigene aussieht.
    /// Protokollwerte laufen nicht über diesen Weg. Ein Feld, das nach dem Eindampfen
    /// leer ist, gilt als nicht genannt.
    /// </remarks>
    private static string? Word(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
        && Runes.Label(v.GetString()) is { Length: > 0 } text ? text : null;

    private static double Number(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;

    private static bool Flag(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Ein geschlossenes Fenster darf die App nicht beenden: sie muss laufen, damit
        // sie eine Verbindungsanfrage überhaupt annehmen kann. Sonst fände der Mac
        // niemanden mehr, und niemand sähe, warum.
        if (!_reallyQuitting && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            ShowInTaskbar = false;
            _tray?.Notify("ScreenBridge läuft weiter",
                "Das Fenster ist zu, die Verbindung bleibt möglich. Beenden über das Symbol.");
            return;
        }

        _tick.Stop();
        _resume.Stop();
        _resume.Dispose();
        _macWindow?.Close();
        _mac?.Dispose();
        _macInput?.Dispose();
        _video?.Dispose();
        _input?.Dispose();
        _control?.Dispose();
        _tray?.Dispose();
        base.OnFormClosing(e);
    }
}
