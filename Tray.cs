using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ScreenBridge;

/// <summary>
/// Das Symbol im Infobereich — damit die App im Hintergrund erreichbar bleibt.
/// </summary>
/// <remarks>
/// <para>
/// Der Zweck ist nicht Bequemlichkeit: die App muss laufen, damit sie eine
/// Verbindungsanfrage überhaupt annehmen kann. Ein Fenster, das man schließt, wäre dann
/// eine Falle — der Mac fände niemanden mehr, und niemand sähe, warum.
/// </para>
/// <para>
/// Deshalb versteckt das Schließen nur. Beenden geht ausdrücklich über das Menü, und
/// dann steht auch dabei, was es bedeutet.
/// </para>
/// <para>
/// Im Menü steht dasselbe wie im Fenster. Wer die App im Hintergrund laufen lässt, will
/// nicht erst ein Fenster aufmachen müssen, um den Bildschirm freizugeben — das Fenster
/// zeigt Messwerte, und die braucht man dafür nicht.
/// </para>
/// <para>
/// Das Symbol wird gezeichnet, nicht mitgeliefert. Zwei Farben genügen, und sie sagen
/// das Einzige, was man von außen wissen will: ob gerade jemand verbunden ist.
/// </para>
/// </remarks>
internal sealed class Tray : IDisposable
{
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr handle);

    private readonly NotifyIcon _icon;
    private readonly List<IntPtr> _handles = [];
    private readonly Dictionary<string, ToolStripItem> _items = [];

    private Icon? _idle;
    private Icon? _busy;

    /// <summary>Ein Eintrag ohne Handlung wird zur Trennlinie.</summary>
    public Tray(string title, Action open, params (string Text, Action? Do)[] items)
    {
        _idle = Draw(Color.FromArgb(0x8B, 0x92, 0x9E));
        _busy = Draw(Color.FromArgb(0x34, 0xD3, 0x99));

        var menu = new ContextMenuStrip();
        foreach (var (text, act) in items)
        {
            if (act is null) { menu.Items.Add(new ToolStripSeparator()); continue; }

            var item = menu.Items.Add(text, null, (_, _) => act());
            _items[text] = item;
        }

        _icon = new NotifyIcon
        {
            Icon = _idle,
            Text = title,
            Visible = true,
            ContextMenuStrip = menu,
        };

        _icon.DoubleClick += (_, _) => open();
    }

    /// <summary>Grün, sobald jemand verbunden ist.</summary>
    public void SetConnected(bool connected, string what)
    {
        _icon.Icon = connected ? _busy : _idle;

        // Der Text im Infobereich ist auf 63 Zeichen begrenzt; längeres wird
        // stillschweigend gar nicht angezeigt.
        _icon.Text = what.Length <= 63 ? what : what[..60] + "…";
    }

    /// <summary>Einen Eintrag ausgrauen, wenn er gerade nichts bewirken würde.</summary>
    public void Enable(string text, bool on)
    {
        if (_items.TryGetValue(text, out var item)) item.Enabled = on;
    }

    /// <summary>Ein Häkchen an einem Eintrag — für Schalter wie den Autostart.</summary>
    public void SetChecked(string text, bool on)
    {
        if (_items.TryGetValue(text, out var item) && item is ToolStripMenuItem menu) menu.Checked = on;
    }

    /// <summary>Eine Meldung, die auch ohne offenes Fenster ankommt.</summary>
    public void Notify(string title, string text) =>
        _icon.ShowBalloonTip(5000, title, text, ToolTipIcon.Info);

    private Icon Draw(Color color)
    {
        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            using var frame = new Pen(Color.FromArgb(0xE8, 0xEA, 0xED), 2.5f);
            g.DrawRectangle(frame, 3, 6, 26, 18);

            using var dot = new SolidBrush(color);
            g.FillEllipse(dot, 12, 12, 8, 8);
        }

        var handle = bitmap.GetHicon();
        _handles.Add(handle);
        return Icon.FromHandle(handle);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _idle?.Dispose();
        _busy?.Dispose();

        // Icon.FromHandle übernimmt den Griff nicht — ohne das bleibt er liegen.
        foreach (var handle in _handles) DestroyIcon(handle);
    }
}
