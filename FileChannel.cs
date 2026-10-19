using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ScreenBridge;

/// <summary>
/// Wie weit eine Übertragung ist.
/// </summary>
/// <remarks>
/// Nicht fürs Protokoll, sondern fürs Fenster: eine Datei, die eine Minute lang
/// unterwegs ist, sieht ohne Fortschritt genauso aus wie eine, die hängt. Der Zustand
/// steht als Text daneben, weil „0 von 900 kB“ zwei verschiedene Dinge heißen kann —
/// wartet noch, oder es geht gerade nicht.
/// </remarks>
internal sealed record Transfer(string Name, long Done, long Size, bool Incoming, string State);

/// <summary>
/// Nimmt eine Datei vom gekoppelten Gerät entgegen.
/// </summary>
/// <remarks>
/// <para>
/// Eine eigene Kanalart, damit eine große Datei weder das Bild noch die Steuerung
/// ausbremst — sie hat ihre eigene Verbindung und ihre eigene Warteschlange im
/// Betriebssystem.
/// </para>
/// <para>
/// <b>Die Gefahr liegt nicht in der Übertragung, sondern im Namen.</b> Verschlüsselt und
/// authentifiziert ist der Kanal längst; das eigentliche Risiko ist ein Dateiname wie
/// <c>..\..\Windows\System32\…</c> oder <c>C:\Autostart\…</c>. Ein Empfänger, der den
/// gesendeten Namen als Pfad behandelt, lässt die Gegenseite an jede Stelle der Platte
/// schreiben — und das wäre schlimmer als alles, wovor der Rest hier schützt.
/// </para>
/// <para>
/// Deshalb: nur der letzte Namensbestandteil, nur unbedenkliche Zeichen, immer in einen
/// festen Ordner, und geschrieben wird erst unter einem Behelfsnamen. Erst wenn die
/// Prüfsumme stimmt, bekommt die Datei ihren Namen.
/// </para>
/// </remarks>
internal sealed class FileChannel
{
    /// <summary>Mehr nimmt niemand entgegen, ohne dass jemand gefragt hätte.</summary>
    private const long MaxSize = 16L * 1024 * 1024 * 1024;

    private readonly Action<string> _log;
    private readonly Action<string, string> _notify;

    /// <summary>
    /// Was hier bereitliegt und abgeholt werden darf.
    /// </summary>
    /// <remarks>
    /// <b>Holen statt Bringen.</b> Der PC hört, der Mac verbindet — in dieser Richtung
    /// gibt es also niemanden, bei dem der PC anklopfen könnte. Ein Listener auf dem Mac
    /// wäre die naheliegende Antwort und die falsche: das wäre eine zweite
    /// Angriffsfläche, und wir haben sie gerade erst auf eine reduziert. Stattdessen
    /// kündigt der PC über den Steuerkanal an, und der Mac holt ab.
    ///
    /// Eine Kennung gilt einmal und verfällt; sonst sammelt sich hier eine Liste von
    /// Angeboten, die niemand je abruft.
    /// </remarks>
    private readonly Dictionary<string, (string Path, DateTime Until)> _offers = [];
    private readonly object _offerGate = new();

    public FileChannel(Action<string> log, Action<string, string> notify)
    {
        _log = log;
        _notify = notify;
    }

    /// <summary>Der Stand der letzten oder laufenden Übertragung.</summary>
    public event Action<Transfer>? Progress;

    /// <summary>Höchstens zehnmal je Sekunde melden — die Anzeige kommt sonst nicht nach.</summary>
    private static readonly TimeSpan ReportEvery = TimeSpan.FromMilliseconds(100);

    /// <summary>Wohin die Dateien gehen — ein eigener Ordner, kein gewachsener Haufen.</summary>
    public static string Folder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                     "Downloads", "ScreenBridge");

    /// <summary>Eine Datei bereitlegen und die Kennung zurückgeben.</summary>
    public (string Id, string Name, long Size, string Hash)? Offer(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxSize) return null;

            using var file = File.OpenRead(path);
            using var sha = SHA256.Create();
            var hash = Convert.ToHexString(sha.ComputeHash(file));

            var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

            lock (_offerGate)
            {
                Sweep();
                _offers[id] = (path, DateTime.UtcNow.AddMinutes(5));
            }

            Progress?.Invoke(new Transfer(info.Name, 0, info.Length, false, "bereitgelegt"));
            return (id, info.Name, info.Length, hash);
        }
        catch (Exception ex)
        {
            _log($"[Datei] „{Path.GetFileName(path)}“ ließ sich nicht bereitlegen: {ex.Message}");
            return null;
        }
    }

    /// <summary>Wie viele Angebote noch abgeholt werden können.</summary>
    public int Waiting
    {
        get { lock (_offerGate) { Sweep(); return _offers.Count; } }
    }

    private void Sweep()
    {
        foreach (var stale in _offers.Where(o => o.Value.Until < DateTime.UtcNow).Select(o => o.Key).ToArray())
            _offers.Remove(stale);
    }

    public void Serve(Stream stream, string who)
    {
        try
        {
            var header = ReadLine(stream);
            if (header is null) return;

            using var document = JsonDocument.Parse(header);
            var root = document.RootElement;

            // Dieselbe Kennung, zwei mögliche erste Sätze: „hier kommt eine“ oder
            // „gib mir die mit dieser Kennung“.
            if (root.TryGetProperty("t", out var kind) && kind.GetString() == "file.get")
            {
                Deliver(stream, who, root.TryGetProperty("id", out var i) ? i.GetString() : null);
                return;
            }

            var name = Safe(root.TryGetProperty("name", out var n) ? n.GetString() : null);
            var size = root.TryGetProperty("size", out var s) && s.TryGetInt64(out var value) ? value : -1;
            var expected = root.TryGetProperty("sha256", out var h) ? h.GetString() : null;

            if (size < 0 || size > MaxSize)
            {
                Refuse(stream, $"Größe {size} nicht annehmbar");
                return;
            }

            Directory.CreateDirectory(Folder);

            var target = Unique(Path.Combine(Folder, name));

            // Der Behelfsname trägt einen Zufallsteil und wird unten mit CreateNew
            // geöffnet: zwei gleichzeitige Übertragungen desselben Namens schrieben
            // sonst verschränkt in dieselbe „.teil“-Datei — und die Prüfsumme der
            // einen löschte die halbe Arbeit der anderen.
            var partial = $"{target}.{Convert.ToHexString(RandomNumberGenerator.GetBytes(4))}.teil";

            _log($"[Datei] {who} schickt „{name}“, {Humanize(size)}");

            var free = new DriveInfo(Path.GetPathRoot(Folder)!).AvailableFreeSpace;
            if (size > free - 64L * 1024 * 1024)
            {
                Refuse(stream, $"nicht genug Platz: {Humanize(size)} gegen {Humanize(free)} frei");
                return;
            }

            string actual;
            try
            {
            using (var file = new FileStream(partial, FileMode.CreateNew))
            using (var digest = SHA256.Create())
            {
                var buffer = new byte[128 * 1024];
                var left = size;
                var last = DateTime.UtcNow;

                Progress?.Invoke(new Transfer(name, 0, size, true, $"kommt an von {who}"));

                while (left > 0)
                {
                    var want = (int)Math.Min(buffer.Length, left);
                    var read = stream.Read(buffer, 0, want);
                    if (read == 0) throw new IOException("Verbindung endete mitten in der Datei");

                    digest.TransformBlock(buffer, 0, read, null, 0);
                    file.Write(buffer, 0, read);
                    left -= read;

                    if (DateTime.UtcNow - last < ReportEvery) continue;
                    last = DateTime.UtcNow;
                    Progress?.Invoke(new Transfer(name, size - left, size, true, $"kommt an von {who}"));
                }

                digest.TransformFinalBlock([], 0, 0);
                actual = Convert.ToHexString(digest.Hash!);
            }
            }
            catch (Exception)
            {
                // Bricht die Verbindung mitten in der Datei ab, bleibt sonst eine
                // halbe „.teil“ liegen, die niemand mehr einordnen kann.
                try { File.Delete(partial); } catch { }
                Progress?.Invoke(new Transfer(name, 0, size, true, "abgebrochen"));
                throw;
            }

            // Die Prüfsumme entscheidet, ob die Datei ihren Namen bekommt. Eine halb
            // übertragene Datei mit richtigem Namen wäre schlimmer als gar keine.
            if (expected is not null && !string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(partial);
                Refuse(stream, "Prüfsumme stimmt nicht — verworfen");
                _log($"[Datei] „{name}“ verworfen: Prüfsumme {actual} statt {expected}");
                Progress?.Invoke(new Transfer(name, size, size, true, "Prüfsumme falsch, verworfen"));
                return;
            }

            // Kurz vor dem Umbenennen noch einmal ausweichen: eine zweite Übertragung
            // desselben Namens kann inzwischen fertig geworden sein.
            target = Unique(target);
            File.Move(partial, target);
            Answer(stream, true, null);

            Progress?.Invoke(new Transfer(Path.GetFileName(target), size, size, true,
                expected is null ? "angekommen" : "angekommen, Prüfsumme stimmt"));
            _log($"[Datei] „{Path.GetFileName(target)}“ angekommen, {Humanize(size)}");
            _notify("Datei angekommen", $"{Path.GetFileName(target)} von {who}");
        }
        catch (Exception ex)
        {
            _log($"[Datei] von {who} fehlgeschlagen: {ex.Message}");
        }
    }

    /// <summary>Eine bereitgelegte Datei herausgeben — einmal, dann ist sie weg.</summary>
    private void Deliver(Stream stream, string who, string? id)
    {
        string? path = null;

        lock (_offerGate)
        {
            Sweep();
            if (id is not null && _offers.Remove(id, out var offer)) path = offer.Path;
        }

        if (path is null || !File.Exists(path))
        {
            _log($"[Datei] {who} wollte eine Kennung abholen, die es nicht (mehr) gibt");
            Answer(stream, false, "Kennung unbekannt oder verfallen");
            return;
        }

        var name = Path.GetFileName(path);
        var sent = 0L;
        var size = 0L;

        try
        {
            using var file = File.OpenRead(path);
            size = file.Length;

            // Von Hand kopiert statt mit CopyTo, weil nur so ein Fortschritt entsteht.
            // Ohne ihn sieht eine Datei, die eine Minute lang läuft, aus wie eine, die
            // hängt — und genau das ist der Fall, den man sehen will.
            var buffer = new byte[128 * 1024];
            var last = DateTime.UtcNow;

            Progress?.Invoke(new Transfer(name, 0, size, false, $"{who} holt sie ab"));

            int read;
            while ((read = file.Read(buffer, 0, buffer.Length)) > 0)
            {
                stream.Write(buffer, 0, read);
                sent += read;

                if (DateTime.UtcNow - last < ReportEvery) continue;
                last = DateTime.UtcNow;
                Progress?.Invoke(new Transfer(name, sent, size, false, $"{who} holt sie ab"));
            }

            stream.Flush();

            Progress?.Invoke(new Transfer(name, size, size, false, $"an {who} übergeben"));
            _log($"[Datei] „{name}“ an {who} übergeben, {Humanize(size)}");
        }
        catch (Exception ex)
        {
            Progress?.Invoke(new Transfer(name, sent, size, false, "abgebrochen"));
            _log($"[Datei] Übergabe an {who} abgebrochen: {ex.Message}");
        }
    }

    /// <summary>Windows schneidet diese Namen ab oder belegt sie selbst.</summary>
    private static readonly string[] Reserved =
    [
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    ];

    /// <summary>
    /// Aus dem gesendeten Namen einen machen, der nur eine Datei benennen kann — und
    /// der zeigt, was er ist.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>GetFileName</c> wirft jeden Pfadanteil weg; damit sind <c>..</c>, führende
    /// Trenner und Laufwerksbuchstaben erledigt.
    /// </para>
    /// <para>
    /// <b>Das reicht nicht, und die zweite Lücke ist die gefährlichere.</b> Die von
    /// Windows verbotenen Zeichen sind <c>&lt; &gt; : " / \ | ? *</c> — <b>U+202E ist
    /// keines davon</b>. Es schaltet die Anzeigerichtung auf rechts-nach-links und dreht
    /// alles Folgende um: aus <c>Foto[U+202E]gnp.exe</c> wird im Explorer
    /// <c>Fotoexe.png</c>. Wer das anklickt, glaubt ein Bild zu öffnen und startet ein
    /// Programm.
    /// </para>
    /// <para>
    /// Solche Zeichen fallen deshalb <b>nach Unicode-Kategorie</b>, nicht nach einer
    /// Bereichsliste — warum, steht an <see cref="Runes"/>. Jede Art Leerzeichen wird
    /// zu einem gewöhnlichen, und die Namen, die Windows für sich beansprucht,
    /// bekommen einen Zusatz. Punkte und Leerzeichen am Ende schneidet Windows still
    /// ab — also schneiden wir sie selbst, damit gespeicherter und angezeigter Name
    /// derselbe sind.
    /// </para>
    /// </remarks>
    private static string Safe(string? name)
    {
        var bare = Path.GetFileName(name ?? "").Trim();

        var clean = new StringBuilder(bare.Length);
        foreach (var rune in bare.EnumerateRunes())
        {
            if (Runes.Invisible(rune)) continue;
            if (Runes.Space(rune)) { clean.Append(' '); continue; }

            if (rune.IsBmp && Array.IndexOf(Path.GetInvalidFileNameChars(), (char)rune.Value) >= 0)
                clean.Append('_');
            else
                clean.Append(rune.ToString());
        }

        // Windows wirft Punkte und Leerzeichen am Ende weg — dann hieße die Datei
        // anders, als hier steht. Nach dem Kürzen noch einmal: der Schnitt kann neue
        // freilegen — und er darf kein Emoji halbieren.
        bare = clean.ToString().TrimEnd(' ', '.');
        if (bare.Length > 180)
            bare = bare[..(char.IsHighSurrogate(bare[179]) ? 179 : 180)].TrimEnd(' ', '.');

        if (bare.Length == 0 || bare is "." or "..") bare = $"datei-{DateTime.Now:yyyyMMdd-HHmmss}";

        var stem = Path.GetFileNameWithoutExtension(bare);
        if (Reserved.Contains(stem, StringComparer.OrdinalIgnoreCase)) bare = "_" + bare;

        return bare;
    }

    /// <summary>Nie etwas überschreiben — daneben legen.</summary>
    private static string Unique(string path)
    {
        if (!File.Exists(path)) return path;

        var folder = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var suffix = Path.GetExtension(path);

        for (var i = 2; i < 1000; i++)
        {
            var candidate = Path.Combine(folder, $"{stem} ({i}){suffix}");
            if (!File.Exists(candidate)) return candidate;
        }

        return Path.Combine(folder, $"{stem} ({Guid.NewGuid():N}){suffix}");
    }

    private void Refuse(Stream stream, string why)
    {
        _log($"[Datei] abgelehnt: {why}");
        Answer(stream, false, why);
    }

    private static void Answer(Stream stream, bool ok, string? why)
    {
        var message = JsonSerializer.Serialize(new { t = "file.result", ok, why }) + "\n";
        stream.Write(Encoding.UTF8.GetBytes(message));
        stream.Flush();
    }

    private static string? ReadLine(Stream stream)
    {
        var line = new List<byte>(512);
        var one = new byte[1];

        while (true)
        {
            if (stream.Read(one, 0, 1) == 0) return null;
            if (one[0] == (byte)'\n') break;

            line.Add(one[0]);
            if (line.Count > 8192) throw new InvalidOperationException("Kopfzeile zu lang");
        }

        return Encoding.UTF8.GetString(line.ToArray());
    }

    public static string Humanize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.##} GB",
    };
}
