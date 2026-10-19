using System.Windows.Forms;

namespace ScreenBridge;

/// <summary>
/// Einstiegspunkt. Eine Fensteranwendung ohne Konsole.
/// </summary>
internal static class Program
{
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern bool AttachConsole(int processId);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern bool AllocConsole();

    private static void OpenConsole()
    {
        const int AttachParentProcess = -1;
        if (!AttachConsole(AttachParentProcess)) AllocConsole();
    }

    [STAThread]
    private static void Main(string[] args)
    {
        // Abgleich der Ziffernrechnung gegen feste Werte. Laufen zwei Umsetzungen
        // auseinander, sieht der Unterschied später aus wie ein Angriff und ist ein
        // Programmierfehler — deshalb wird er hier gefunden, nicht dort.
        if (args.Contains("--pairvectors"))
        {
            var lines = new List<string>();
            foreach (var (fa, ra, fb, rb) in new[]
                     {
                         (new string('0', 64), new string('0', 32),
                          string.Concat(Enumerable.Repeat("11", 32)), string.Concat(Enumerable.Repeat("11", 16))),
                         (new string('A', 64), "0102030405060708090A0B0C0D0E0F10",
                          new string('B', 64), "F0E0D0C0B0A090807060504030201000"),
                         (new string('B', 64), "F0E0D0C0B0A090807060504030201000",
                          new string('A', 64), "0102030405060708090A0B0C0D0E0F10"),
                         ("CB0CF03558468464" + new string('0', 48), string.Concat(Enumerable.Repeat("DEADBEEF", 4)),
                          new string('F', 64), string.Concat(Enumerable.Repeat("0123456789ABCDEF", 2))),
                     })
            {
                lines.Add(Identity.PairingCode(fa, Convert.FromHexString(ra), fb, Convert.FromHexString(rb)));
            }

            File.WriteAllLines(Path.Combine(AppContext.BaseDirectory, "pairvectors.txt"), lines);
            return;
        }

        // Eine Instanz, nie zwei. Zwei nebeneinander hießen: zwei Symbole im
        // Infobereich, zwei Kämpfe um Port 7202, und eine Ziffernfrage, von der
        // niemand weiß, zu welcher sie gehört. Der Mutex gehört dem Kern und
        // verschwindet mit dem Prozess — auch nach einem Absturz klemmt hier nichts.
        //
        // Der Zweitstart endet, gibt aber vorher sein Anliegen ab: von Hand
        // gestartet heißt er „zeig das Fenster der laufenden“ — wer die EXE
        // doppelt anklickt, sucht die App, nicht eine zweite. Ein doppelter
        // Autostart dagegen will nichts und endet still.
        using var single = new Mutex(initiallyOwned: true, @"Local\ScreenBridge-EineInstanz", out var first);
        if (!first)
        {
            if (!args.Contains("--hintergrund"))
            {
                try
                {
                    using var running = EventWaitHandle.OpenExisting(@"Local\ScreenBridge-Zeigen");
                    running.Set();
                }
                catch (Exception) { }
            }

            return;
        }

        using var wake = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\ScreenBridge-Zeigen");

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

        // --hintergrund: so startet der Autostart-Eintrag — nur das Symbol im
        // Infobereich, kein Fenster. Von Hand gestartet kommt das Fenster wie immer.
        var form = new MainForm(args.Contains("--hintergrund"));

        // Der Lauscher für Zweitstarts: wartet im Kern, kostet nichts. Ein Signal
        // in der Lücke vor dem ersten Fensteraufbau geht verloren — dann zeigt der
        // dritte Klick das Fenster, und mehr stand nicht auf dem Spiel.
        new Thread(() =>
        {
            while (true)
            {
                wake.WaitOne();
                try { form.BeginInvoke(form.ShowWindow); } catch (Exception) { }
            }
        })
        { IsBackground = true, Name = "ScreenBridge-Zeigen" }.Start();

        Application.Run(form);
    }
}

