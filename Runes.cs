using System.Globalization;
using System.Text;

namespace ScreenBridge;

/// <summary>
/// Was von fremdem Text sichtbar ist — an genau einer Stelle festgelegt.
/// </summary>
/// <remarks>
/// <para>
/// <b>Unsichtbar ist eine Unicode-Kategorie, keine Bereichsliste.</b> Der erste Anlauf
/// zählte Bereiche auf — U+200B bis U+200F, U+2028 bis U+202E, U+2066 bis U+2069 — und
/// übersah dabei U+FEFF, U+2060, U+061C, das weiche Trennzeichen und die
/// C1-Steuerzeichen. Wer aufzählt, verliert gegen den, der die nächste Lücke kennt.
/// Die Kategorien <i>Control</i> und <i>Format</i> umfassen sie alle; Zeilen- und
/// Absatztrenner fallen mit, weil sie in einer Zeile nichts verloren haben.
/// </para>
/// <para>
/// Gerechnet wird in Runen, nicht in <c>char</c>: ein Emoji ist zwei <c>char</c> und
/// ein Zeichen, und auch oberhalb der Grundebene liegen Formatzeichen — die
/// Tag-Zeichen ab U+E0000 zum Beispiel, allesamt unsichtbar.
/// </para>
/// <para>
/// Gebraucht wird das an zwei Stellen, die voneinander nichts wissen: beim Dateinamen,
/// der auf der Platte landet, und bei den Namen und Meldungen der Gegenstelle, die in
/// Dialog, Infobereich und Protokoll stehen. Zwei Umsetzungen liefen auseinander, und
/// die Lücke säße dann in genau einer.
/// </para>
/// </remarks>
internal static class Runes
{
    /// <summary>Trägt das Zeichen nichts Sichtbares bei, fällt es weg.</summary>
    public static bool Invisible(Rune rune) => Rune.GetUnicodeCategory(rune) is
        UnicodeCategory.Control
        or UnicodeCategory.Format
        or UnicodeCategory.LineSeparator
        or UnicodeCategory.ParagraphSeparator;

    /// <summary>Ein Leerzeichen gleich welcher Bauart — auch das geschützte.</summary>
    public static bool Space(Rune rune) =>
        Rune.GetUnicodeCategory(rune) == UnicodeCategory.SpaceSeparator;

    /// <summary>
    /// Fremden Text auf eine Zeile Anzeige eindampfen: nur Sichtbares, begrenzt.
    /// </summary>
    /// <remarks>
    /// Für Gerätenamen und Meldungen der Gegenstelle. Ein Zeilenumbruch im Namen
    /// schöbe eigene Sätze in den Kopplungsdialog — in eine Sicherheitsfrage —, ein
    /// Formatzeichen verdrehte die Anzeige, und eine beliebige Länge sprengte jede
    /// Zeile. Die Ziffern fälschen kann der Angreifer damit nicht (der Name geht
    /// hinaus, bevor die Zufallszahlen preisgegeben sind), aber ein Dialog, in dem
    /// fremder Text steht, muss ihn als das zeigen, was er ist: eine Zeile, keine
    /// Bühne.
    /// </remarks>
    public static string Label(string? text, int most = 64)
    {
        var clean = new StringBuilder();
        foreach (var rune in (text ?? "").EnumerateRunes())
        {
            if (Invisible(rune)) continue;
            if (clean.Length + rune.Utf16SequenceLength > most) break;
            clean.Append(Space(rune) ? " " : rune.ToString());
        }

        return clean.ToString().Trim();
    }
}
