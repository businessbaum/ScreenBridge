using Microsoft.Win32;

namespace ScreenBridge;

/// <summary>
/// Der Eintrag, mit dem Windows die App beim Anmelden startet — leise, als Symbol.
/// </summary>
/// <remarks>
/// <para>
/// <b>Ohne ihn ist das Wecken über Netz eine Sackgasse.</b> Der Mac weckt den PC,
/// Der Nutzer meldet sich an — und ScreenBridge läuft nicht, weil es niemand gestartet
/// hat. Vom Mac aus sieht das aus wie ein toter Rechner, und die Ursache steht auf
/// keinem der beiden Bildschirme.
/// </para>
/// <para>
/// <b>Nachgeführt wird bei jedem Start.</b> Die EXE kann umziehen — ein neuer Build,
/// ein anderer Ordner — und ein Eintrag auf einen alten Pfad ist schlimmer als
/// keiner: er sieht eingerichtet aus und tut nichts. Der Merker unter
/// <c>Software\ScreenBridge</c> unterscheidet „nie eingerichtet“ von „ausdrücklich
/// abgeschaltet“ — sonst käme der Eintrag nach jedem Abschalten von selbst wieder,
/// und der Schalter im Menü wäre eine Attrappe.
/// </para>
/// </remarks>
internal static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppKey = @"Software\ScreenBridge";
    private const string Name = "ScreenBridge";

    /// <summary>Womit gestartet wird: diese EXE, leise im Hintergrund.</summary>
    private static string Command => $"\"{Environment.ProcessPath}\" --hintergrund";

    /// <summary>Steht der Eintrag gerade?</summary>
    public static bool Enabled
    {
        get
        {
            try
            {
                using var run = Registry.CurrentUser.OpenSubKey(RunKey);
                return run?.GetValue(Name) is string;
            }
            catch (Exception) { return false; }
        }
    }

    /// <summary>
    /// Beim Start: den Eintrag anlegen oder auf den aktuellen Pfad nachführen —
    /// es sei denn, er wurde ausdrücklich abgeschaltet.
    /// </summary>
    public static void Ensure(Action<string> log)
    {
        try
        {
            using var app = Registry.CurrentUser.CreateSubKey(AppKey);
            if (Equals(app.GetValue("Autostart"), 0)) return;
            app.SetValue("Autostart", 1);

            using var run = Registry.CurrentUser.CreateSubKey(RunKey);
            if (run.GetValue(Name) as string == Command) return;

            run.SetValue(Name, Command);
            log("Autostart eingerichtet — startet beim Anmelden leise als Symbol");
        }
        catch (Exception ex) { log($"Autostart ließ sich nicht einrichten: {ex.Message}"); }
    }

    /// <summary>Der Schalter im Menü.</summary>
    public static void Set(bool on, Action<string> log)
    {
        try
        {
            using var app = Registry.CurrentUser.CreateSubKey(AppKey);
            app.SetValue("Autostart", on ? 1 : 0);

            using var run = Registry.CurrentUser.CreateSubKey(RunKey);
            if (on) run.SetValue(Name, Command);
            else run.DeleteValue(Name, throwOnMissingValue: false);

            log(on ? "Autostart eingeschaltet" : "Autostart ausgeschaltet — Wecken über Netz endet dann am Anmeldebildschirm");
        }
        catch (Exception ex) { log($"Autostart ließ sich nicht umstellen: {ex.Message}"); }
    }
}
