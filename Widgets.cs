using System.Windows.Forms;

namespace ScreenBridge;

/// <summary>
/// Farben, Schriften und die drei Bausteine, aus denen das Fenster besteht.
/// </summary>
/// <remarks>
/// An einer Stelle, weil sonst jede Karte ihre eigenen Grautöne bekommt und der
/// Unterschied dann aussieht wie ein Fehler. Die Töne sind bewusst weit auseinander:
/// zwei Grau, die sich um ein paar Prozent unterscheiden, liest das Auge als eine
/// Fläche.
/// </remarks>
internal static class Skin
{
    public static readonly Color Ground = Color.FromArgb(0x14, 0x16, 0x1A);
    public static readonly Color Panel = Color.FromArgb(0x1C, 0x1F, 0x26);
    public static readonly Color Line = Color.FromArgb(0x2A, 0x2F, 0x3A);
    public static readonly Color Ink = Color.FromArgb(0xE8, 0xEA, 0xED);
    public static readonly Color Muted = Color.FromArgb(0x8B, 0x92, 0x9E);
    public static readonly Color Good = Color.FromArgb(0x34, 0xD3, 0x99);
    public static readonly Color Warn = Color.FromArgb(0xF5, 0xA5, 0x24);
    public static readonly Color Bad = Color.FromArgb(0xF2, 0x6D, 0x6D);
    public static readonly Color Accent = Color.FromArgb(0x5B, 0x9D, 0xFF);

    public static readonly Font Body = new("Segoe UI", 9f);
    public static readonly Font Small = new("Segoe UI", 8.5f);
    public static readonly Font Strong = new("Segoe UI Semibold", 9f);
    public static readonly Font Title = new("Segoe UI Semibold", 8.5f);
    public static readonly Font Headline = new("Segoe UI Semibold", 14f);
    public static readonly Font Mono = new("Consolas", 8.5f);

    /// <summary>Eine Karte: Überschrift oben, Inhalt darunter, dünner Rand darum.</summary>
    public static (Panel Card, Panel Body) Card(string title)
    {
        var card = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Panel,
            Margin = new Padding(7),
            Padding = new Padding(16, 10, 16, 12),
        };

        card.Paint += (s, e) =>
        {
            var me = (Control)s!;
            using var pen = new Pen(Line);
            e.Graphics.DrawRectangle(pen, 0, 0, me.Width - 1, me.Height - 1);
        };

        var body = new Panel { Dock = DockStyle.Fill, BackColor = Panel };
        var head = new Label
        {
            Text = title,
            Dock = DockStyle.Top,
            Height = 26,
            ForeColor = Muted,
            Font = Title,
            TextAlign = ContentAlignment.MiddleLeft,
        };

        // Zuerst der Inhalt, dann die Überschrift: WinForms dockt den zuletzt
        // hinzugefügten nach außen.
        card.Controls.Add(body);
        card.Controls.Add(head);

        return (card, body);
    }

    public static Button Button(string text, int width = 0) => new()
    {
        Text = text,
        Size = new Size(width > 0 ? width : text.Length * 8 + 30, 28),
        FlatStyle = FlatStyle.Flat,
        BackColor = Ground,
        ForeColor = Ink,
        Font = Small,
        FlatAppearance = { BorderColor = Line, MouseOverBackColor = Line },
        Cursor = Cursors.Hand,
    };
}

/// <summary>
/// Eine Liste aus Bezeichnung und Wert, selbst gezeichnet.
/// </summary>
/// <remarks>
/// <para>
/// Selbst gezeichnet, weil die Alternative — ein <c>TableLayoutPanel</c> voller Labels —
/// bei jedem Auffrischen ein Dutzend Steuerelemente neu vermisst und dabei flackert. Hier
/// ist ein Auffrischen ein <c>Invalidate</c>, und der Wert ändert sich zehnmal je Sekunde,
/// ohne dass etwas springt.
/// </para>
/// <para>
/// <see cref="Set"/> legt eine Zeile beim ersten Aufruf an. Damit steht die Reihenfolge
/// im Quelltext dort, wo die Werte gesetzt werden, und nicht in einem zweiten Aufbau, der
/// dann irgendwann nicht mehr dazu passt.
/// </para>
/// </remarks>
internal sealed class Fields : Control
{
    public const int RowHeight = 24;

    private sealed class Row
    {
        public string Name = "";
        public string Value = "—";
        public Color Colour = Skin.Ink;
    }

    private readonly List<Row> _rows = [];
    private readonly int _nameWidth;

    public Fields(int nameWidth)
    {
        _nameWidth = nameWidth;
        BackColor = Skin.Panel;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint
                 | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
    }

    /// <summary>Einen Wert setzen; die Zeile entsteht beim ersten Mal.</summary>
    public void Set(string name, string value, Color? colour = null)
    {
        var wanted = colour ?? Skin.Ink;
        var row = _rows.FirstOrDefault(r => r.Name == name);

        if (row is null)
        {
            _rows.Add(new Row { Name = name, Value = value, Colour = wanted });
            Invalidate();
            return;
        }

        // Nur zeichnen, wenn sich etwas geändert hat — sonst blinkt eine Anzeige,
        // die einmal je Sekunde denselben Wert bekommt.
        if (row.Value == value && row.Colour == wanted) return;

        row.Value = value;
        row.Colour = wanted;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(BackColor);

        const TextFormatFlags flags =
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis
            | TextFormatFlags.NoPrefix;

        var y = 0;
        foreach (var row in _rows)
        {
            TextRenderer.DrawText(e.Graphics, row.Name, Skin.Small,
                new Rectangle(0, y, _nameWidth - 12, RowHeight), Skin.Muted, flags);

            TextRenderer.DrawText(e.Graphics, row.Value, Skin.Strong,
                new Rectangle(_nameWidth, y, Math.Max(20, Width - _nameWidth), RowHeight), row.Colour, flags);

            y += RowHeight;
        }
    }
}

/// <summary>
/// Ein Balken mit Beschriftung — für die Datei, die gerade unterwegs ist.
/// </summary>
/// <remarks>
/// Der Text allein reicht nicht: „412 kB von 3,1 MB“ sagt nichts darüber, ob es
/// vorangeht. Der Balken tut das in einem Blick, und der Text daneben sagt, was genau
/// gerade passiert — bereitgelegt, wird abgeholt, angekommen.
/// </remarks>
internal sealed class Meter : Control
{
    private string _caption = "Noch nichts übertragen";
    private string _detail = "";
    private double _fraction = -1;
    private Color _colour = Skin.Accent;

    public Meter()
    {
        BackColor = Skin.Panel;
        Height = 52;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint
                 | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
    }

    /// <summary>Ein Anteil unter null lässt den Balken weg.</summary>
    public void Show(string caption, string detail, double fraction, Color colour)
    {
        if (_caption == caption && _detail == detail
            && Math.Abs(_fraction - fraction) < 0.001 && _colour == colour) return;

        _caption = caption;
        _detail = detail;
        _fraction = fraction;
        _colour = colour;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(BackColor);

        const TextFormatFlags left =
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis
            | TextFormatFlags.NoPrefix;
        const TextFormatFlags right =
            TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix;

        var detailWidth = TextRenderer.MeasureText(_detail, Skin.Small).Width + 8;

        TextRenderer.DrawText(e.Graphics, _caption, Skin.Strong,
            new Rectangle(0, 0, Math.Max(20, Width - detailWidth), 22), _colour, left);

        TextRenderer.DrawText(e.Graphics, _detail, Skin.Small,
            new Rectangle(Width - detailWidth, 0, detailWidth, 22), Skin.Muted, right);

        if (_fraction < 0) return;

        var track = new Rectangle(0, 30, Math.Max(1, Width), 7);
        using var back = new SolidBrush(Skin.Line);
        e.Graphics.FillRectangle(back, track);

        var done = (int)Math.Round(track.Width * Math.Clamp(_fraction, 0, 1));
        if (done <= 0) return;

        using var front = new SolidBrush(_colour);
        e.Graphics.FillRectangle(front, track.X, track.Y, done, track.Height);
    }
}
