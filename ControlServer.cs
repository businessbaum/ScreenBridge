using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace ScreenBridge;

/// <summary>Eine Kopplungsanfrage, die auf die Bestätigung des Benutzers wartet.</summary>
internal sealed record PairingRequest(string Code, string Fingerprint, string Name, string Address)
{
    public TaskCompletionSource<bool> Answer { get; } = new();
}

/// <summary>
/// Der Steuerkanal: verschlüsselt, gegenseitig authentifiziert, und der einzige Weg,
/// auf dem die Gegenseite etwas anordnen darf.
/// </summary>
/// <remarks>
/// <para>
/// <b>Verschlüsseln und Authentifizieren sind ein Vorgang, nicht zwei.</b> Im
/// TLS-Handschlag tauschen beide Seiten flüchtige Schlüssel aus und errechnen daraus ein
/// gemeinsames Geheimnis, das nie über die Leitung geht. Unterschrieben wird dieser
/// Austausch mit dem dauerhaften Schlüssel — angenommen wird er nur, wenn die
/// Unterschrift vom angehefteten Fingerabdruck stammt. Wer sich dazwischenstellt, kann
/// nicht unterschreiben und bekommt das Geheimnis nicht.
/// </para>
/// <para>
/// Aus diesem Geheimnis entstehen die Sitzungsschlüssel; danach ist alles mit
/// AES-256-GCM verschlüsselt und zugleich gegen Veränderung gesichert. Die flüchtigen
/// Schlüssel werden verworfen — wer den Verkehr heute mitschneidet und später an unsere
/// Dateien kommt, kann ihn trotzdem nicht lesen.
/// </para>
/// <para>
/// Gerechnet wird von Schannel, nicht von uns. Ein selbstgebauter Handschlag ist der
/// Ort, an dem solche Fehler wohnen, und ein Fehler dort fällt nicht auf: es sieht
/// verschlüsselt aus und ist es nicht.
/// </para>
/// </remarks>
internal sealed class ControlServer : IDisposable
{
    public const int Port = 7202;

    /// <summary>So lange bleibt das Kopplungsfenster offen — und nur für einen Versuch.</summary>
    private static readonly TimeSpan PairingWindow = TimeSpan.FromMinutes(2);

    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Ruhe nach einem gescheiterten Kopplungsversuch — für das Fenster, das vor der
    /// Ersteinrichtung von selbst offen steht.
    /// </summary>
    /// <remarks>
    /// Solange nichts gekoppelt ist, nimmt dieser Rechner jede Anfrage an und zeigt
    /// die Ziffernfrage. Ohne Bremse wäre das eine Klingel, die jeder im Netz im
    /// Sekundentakt drücken kann — und irgendwann kommt ein „Ja, damit es aufhört“.
    /// Jeder Versuch spannt deshalb die Ruhezeit für den nächsten, jede weitere
    /// Störung verdoppelt sie; eine gelungene Kopplung setzt sie zurück. Ein Druck
    /// auf „Koppeln“ öffnet das Fenster ausdrücklich und setzt sich darüber hinweg.
    /// </remarks>
    private TimeSpan _quiet = TimeSpan.Zero;
    private DateTime _quietUntil = DateTime.MinValue;

    private readonly Identity _identity;
    private readonly Action<string> _log;
    private readonly TcpListener _listener = new(IPAddress.Any, Port);
    private readonly CancellationTokenSource _cts = new();

    private DateTime _pairingUntil = DateTime.MinValue;

    /// <summary>Der Strom der laufenden Steuerverbindung — dorthin gehen Angebote.</summary>
    private SslStream? _talking;
    private readonly SemaphoreSlim _talkGate = new(1, 1);

    public ControlServer(Identity identity, Action<string> log)
    {
        _identity = identity;
        _log = log;
    }

    public event Action<PairingRequest>? PairingRequested;

    /// <summary>
    /// Ein unbekanntes Gerät wurde abgewiesen.
    /// </summary>
    /// <remarks>
    /// Das gehört nicht nur ins Protokoll. Wer am Mac vergeblich auf „Koppeln“ drückt,
    /// sieht sonst nur, dass nichts passiert — und die Ursache steht auf dem anderen
    /// Rechner in einer Datei, die niemand offen hat.
    /// </remarks>
    public event Action<string>? Rejected;
    public event Action<Peer?>? PeerChanged;
    public event Action<string, JsonElement>? Command;

    /// <summary>Eine Bildverbindung eines gekoppelten Geräts — der Strom gehört dem Sender.</summary>
    public event Action<Stream, Peer>? VideoRequested;

    /// <summary>Eine Dateiübertragung eines gekoppelten Geräts.</summary>
    public event Action<Stream, Peer>? FileRequested;

    /// <summary>
    /// Die Gegenrichtung: der Mac schickt seinen Bildschirm hierher.
    /// </summary>
    /// <remarks>
    /// Dieselbe Rahmung wie <c>V</c>, nur andersherum. Es bleibt dabei, dass der Mac
    /// verbindet und der PC hört — auch für das Bild, das von dort kommt. Ein Listener
    /// auf der Mac-Seite wäre die naheliegende Antwort und die falsche: das wäre eine
    /// zweite Angriffsfläche, und wir haben sie gerade erst auf eine reduziert.
    /// </remarks>
    public event Action<Stream, Peer>? MacScreenRequested;

    /// <summary>Die Steuerverbindung steht — Zeitpunkt für alles, was einmal gesagt gehört.</summary>
    public event Action? Connected;

    // Die jeweils jüngste Verbindung je Art. Eine neue schließt die alte.
    private readonly object _live = new();
    private IDisposable? _liveControl;
    private IDisposable? _liveVideo;
    private IDisposable? _liveMac;

    /// <summary>
    /// Darf sich gerade ein unbekanntes Gerät koppeln?
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Die erste Kopplung braucht keinen Knopf.</b> Solange hier nichts angeheftet ist,
    /// kann eine Kopplungsanfrage nur von dem Gerät kommen, das man gerade einrichtet —
    /// und die Sicherheit hängt ohnehin nicht am Knopf, sondern am Ziffernvergleich. Wer
    /// sich dazwischenstellt, erzeugt auf beiden Seiten verschiedene Ziffern, ob das
    /// Fenster nun von Hand geöffnet wurde oder nicht.
    /// </para>
    /// <para>
    /// Das gleichzeitige Drücken auf beiden Geräten war deshalb eine Hürde ohne Gegenwert.
    /// Sobald ein Gerät gekoppelt ist, gilt wieder die strenge Regel: dann ist ein
    /// unbekanntes Zertifikat nichts, was man erwartet.
    /// </para>
    /// </remarks>
    public bool PairingOpen => DateTime.UtcNow < _pairingUntil || _identity.Peers.Count == 0;

    /// <summary>Was der Handschlag ausgehandelt hat — Anzeige, keine Rechnung.</summary>
    public string Protocol { get; private set; } = "";
    public string Cipher { get; private set; } = "";
    public string PeerAddress { get; private set; } = "";
    public DateTime Since { get; private set; }

    /// <summary>Steht gerade eine Steuerverbindung?</summary>
    public bool Talking => _talking is not null;

    /// <summary>Eine Kopplung zur Zeit. Sonst rückt ein Zweiter mitten hinein.</summary>
    private int _pairing;

    /// <summary>Das Kopplungsfenster öffnen. Ohne das wird Unbekanntes abgewiesen.</summary>
    public void OpenPairing()
    {
        _pairingUntil = DateTime.UtcNow + PairingWindow;
        _log($"Koppeln ist für {PairingWindow.TotalMinutes:0} Minuten möglich — für einen Versuch");
    }

    public void ClosePairing() => _pairingUntil = DateTime.MinValue;

    public void Start()
    {
        _listener.Start();
        _ = AcceptAsync();
        _log($"Steuerkanal hört auf Port {Port} · Fingerabdruck {_identity.ReadableFingerprint}");
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                _ = Task.Run(() => ServeAsync(client));
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _log($"[Steuerung] Annahme endete: {ex.Message}"); }
    }

    private async Task ServeAsync(TcpClient client)
    {
        var address = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "?";
        string? fingerprint = null;
        Peer? peer = null;

        // Nur die Steuerverbindung meldet Kommen und Gehen. Vorher tat es jede Art, und
        // ein beendeter Bildkanal färbte damit das Symbol grau, obwohl die Steuerung
        // weiterlief — die Anzeige log über etwas, das nebenan noch stand.
        var controlling = false;

        try
        {
            using (client)
            {
                client.NoDelay = true;

                await using var tls = new SslStream(client.GetStream(), false, (_, cert, _, _) =>
                {
                    if (cert is null) return false;
                    fingerprint = Fingerprint.Of(cert);
                    return _identity.Knows(fingerprint) || PairingOpen;
                });

                // Mit Frist, hier und beim Kennungs-Byte darunter: wer sich verbindet
                // und dann schweigt, hielte sonst auf unbestimmte Zeit einen Faden —
                // und davon beliebig viele nebeneinander.
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = _identity.Certificate,
                    ClientCertificateRequired = true,
                    EnabledSslProtocols = SslProtocols.Tls13,
                }).WaitAsync(StepTimeout);

                if (fingerprint is null) return;

                // Ein Byte sagt, wofür die Verbindung da ist. Es steht vor jeder
                // Rahmung und kann deshalb in keine geraten.
                var kind = new byte[1];
                if (await tls.ReadAsync(kind).AsTask().WaitAsync(StepTimeout) == 0) return;

                peer = _identity.Find(fingerprint);

                switch ((char)kind[0])
                {
                    case 'C':
                        // Nur hier darf gekoppelt werden. Die Bildverbindung benutzt
                        // dasselbe Zertifikat — ohne diese Trennung entstünde bei
                        // offenem Fenster aus einem Bildstrom eine Kopplung.
                        if (peer is null && !await PairAsync(tls, fingerprint, address)) return;
                        peer ??= _identity.Find(fingerprint);

                        Replace(ref _liveControl, client);
                        _talking = tls;
                        controlling = true;

                        Protocol = tls.SslProtocol.ToString();
                        Cipher = tls.NegotiatedCipherSuite.ToString();
                        PeerAddress = address;
                        Since = DateTime.UtcNow;

                        _log($"[Steuerung] verbunden mit {peer?.Name ?? "?"} ({address}), {tls.SslProtocol}, {tls.NegotiatedCipherSuite}");
                        PeerChanged?.Invoke(peer);

                        Connected?.Invoke();
                        try { await ConverseAsync(tls); }
                        finally { if (ReferenceEquals(_talking, tls)) _talking = null; }
                        break;

                    case 'V':
                        if (peer is null)
                        {
                            _log($"[Bild] {address} ist nicht gekoppelt — abgewiesen");
                            Rejected?.Invoke($"{address} wollte Bild senden, ist aber nicht gekoppelt.");
                            return;
                        }

                        Replace(ref _liveVideo, client);
                        _log($"[Bild] Kanal offen für {peer.Name}, {tls.SslProtocol}");
                        VideoRequested?.Invoke(tls, peer);
                        break;

                    case 'M':
                        if (peer is null)
                        {
                            _log($"[Mac] {address} ist nicht gekoppelt — abgewiesen");
                            Rejected?.Invoke($"{address} wollte sein Bild schicken, ist aber nicht gekoppelt.");
                            return;
                        }

                        Replace(ref _liveMac, client);
                        _log($"[Mac] Kanal offen von {peer.Name}, {tls.SslProtocol}");
                        MacScreenRequested?.Invoke(tls, peer);
                        break;

                    case 'F':
                        // Wie beim Bild: nur gekoppelt, und niemals koppelnd. Eine
                        // eigene Verbindung, damit eine große Datei weder Bild noch
                        // Steuerung ausbremst.
                        if (peer is null)
                        {
                            _log($"[Datei] {address} ist nicht gekoppelt — abgewiesen");
                            Rejected?.Invoke($"{address} wollte eine Datei schicken, ist aber nicht gekoppelt.");
                            return;
                        }

                        FileRequested?.Invoke(tls, peer);
                        break;

                    default:
                        _log($"[Steuerung] {address}: unbekannte Kennung 0x{kind[0]:X2} — getrennt");
                        return;
                }
            }
        }
        catch (Exception ex)
        {
            _log($"[Steuerung] {address}: {FirstLine(ex.Message)}");
        }
        finally
        {
            if (controlling)
            {
                Protocol = Cipher = PeerAddress = "";
                PeerChanged?.Invoke(null);
            }
        }
    }

    /// <summary>
    /// Die jüngste Verbindung einer Art gewinnt; die ältere wird geschlossen.
    /// </summary>
    /// <remarks>
    /// Der Fall tritt sicher ein: verliert der Mac kurz das WLAN, hängt seine alte
    /// Verbindung hier noch halboffen, und die neue käme nicht durch. Würde die ältere
    /// gewinnen, müsste man warten, bis das Betriebssystem die Leiche aufräumt — und
    /// solange sieht es aus, als sei die App kaputt.
    /// </remarks>
    private void Replace(ref IDisposable? slot, IDisposable fresh)
    {
        IDisposable? old;
        lock (_live)
        {
            old = slot;
            slot = fresh;
        }

        if (old is null || ReferenceEquals(old, fresh)) return;

        _log("[Steuerung] ältere Verbindung derselben Art geschlossen");
        try { old.Dispose(); } catch { }
    }

    /// <summary>
    /// Die Kopplung: beide Seiten legen sich auf eine Zufallszahl fest, geben sie dann
    /// preis, und errechnen daraus dieselben sechs Ziffern.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Die Festlegung vorab ist der Kern, und ohne sie prüft der Zahlenvergleich
    /// nichts.</b> Ein erster Entwurf leitete die Ziffern allein aus den beiden
    /// Fingerabdrücken ab. Die sind aber öffentlich, und ein Angreifer bringt seine
    /// eigenen mit: er stellt sich dazwischen, zeigt jeder Seite ein anderes Zertifikat
    /// und probiert so lange, bis beide Seiten <i>dieselbe</i> Zahl errechnen. Bei einer
    /// Million möglicher Werte genügen dafür einige hundert Versuche. Beide Benutzer
    /// bestätigen dann guten Gewissens, und beide reden mit dem Angreifer.
    /// </para>
    /// <para>
    /// Mit der Festlegung geht das nicht mehr: jede Seite muss den Hashwert ihrer
    /// Zufallszahl nennen, bevor sie die der anderen kennt. Danach kann keiner mehr
    /// nachbessern — die Zahl steht fest, bevor man weiß, worauf man hinarbeiten müsste.
    /// Dasselbe Verfahren benutzt Bluetooth beim Zahlenvergleich, und aus demselben
    /// Grund.
    /// </para>
    /// <para>
    /// <b>Beide Seiten müssen zustimmen.</b> Eine einseitige Bestätigung wäre wertlos:
    /// dann wüsste dieser Rechner nie, ob am anderen jemand hingesehen hat.
    /// </para>
    /// </remarks>
    private async Task<bool> PairAsync(SslStream tls, string fingerprint, string address)
    {
        if (!PairingOpen)
        {
            _log($"[Steuerung] unbekanntes Gerät von {address} abgewiesen — Koppeln ist nicht offen");
            Rejected?.Invoke($"{address} ist nicht gekoppelt. Zum Koppeln hier auf „Koppeln“ drücken.");
            return false;
        }

        // Die Bremse gilt nur für das von selbst offene Fenster; ein Druck auf
        // „Koppeln“ setzt sich darüber hinweg. Absichtlich ohne Meldung im
        // Infobereich — die wäre derselbe Spam mit anderem Gesicht.
        if (DateTime.UtcNow >= _pairingUntil && DateTime.UtcNow < _quietUntil)
        {
            var left = (int)Math.Ceiling((_quietUntil - DateTime.UtcNow).TotalSeconds);
            _log($"[Steuerung] Kopplungsanfrage von {address} abgewiesen — Ruhezeit nach gescheitertem Versuch, noch {left} s. „Koppeln“ öffnet sofort.");
            return false;
        }

        // Eine Kopplung zur Zeit — auch dann, wenn das Fenster offen steht, weil noch
        // nichts angeheftet ist. Zwei gleichzeitige Ziffernfragen wären nicht
        // auseinanderzuhalten.
        if (Interlocked.Exchange(ref _pairing, 1) == 1)
        {
            _log($"[Steuerung] {address} muss warten — eine Kopplung läuft schon");
            return false;
        }

        // Jeder Versuch spannt die Ruhezeit für den nächsten — wie auch immer er
        // endet. Erst eine gelungene Kopplung setzt sie zurück.
        try
        {
            var paired = await PairInnerAsync(tls, fingerprint, address);

            if (paired)
            {
                _quiet = TimeSpan.Zero;
                _quietUntil = DateTime.MinValue;
            }
            else
            {
                Quiet();
            }

            return paired;
        }
        catch
        {
            Quiet();
            throw;
        }
        finally { Interlocked.Exchange(ref _pairing, 0); }
    }

    private void Quiet()
    {
        _quiet = _quiet == TimeSpan.Zero
            ? TimeSpan.FromSeconds(10)
            : TimeSpan.FromTicks(Math.Min(_quiet.Ticks * 2, TimeSpan.FromMinutes(5).Ticks));
        _quietUntil = DateTime.UtcNow + _quiet;
    }

    private async Task<bool> PairInnerAsync(SslStream tls, string fingerprint, string address)
    {
        // Ein Versuch je Fenster. Sonst rückt im selben Fenster ein Zweiter nach.
        ClosePairing();

        var mine = RandomNumberGenerator.GetBytes(16);
        var myCommitment = Convert.ToHexString(SHA256.HashData(mine));

        await WriteAsync(tls, new
        {
            t = "pair.commit",
            hash = myCommitment,
            name = Environment.MachineName,
            fingerprint = _identity.Fingerprint,
        });

        var commit = await ExpectAsync(tls, "pair.commit");
        var theirCommitment = Text(commit, "hash");

        // Der Name kommt von einem noch ungekoppelten Gerät und landet im
        // Kopplungsdialog, im Infobereich, im Protokoll und in peers.json. Dort zeigt
        // er sich als eine Zeile Sichtbares — oder gar nicht.
        var theirName = Runes.Label(Text(commit, "name"));
        if (theirName.Length == 0) theirName = "unbenanntes Gerät";

        // Das mitgeschickte Feld ist nur eine Gegenprobe. Maßgeblich ist allein das
        // Zertifikat aus dem Handschlag; weicht die Angabe davon ab, stimmt etwas nicht.
        var claimed = Text(commit, "fingerprint");
        if (!string.Equals(claimed, fingerprint, StringComparison.OrdinalIgnoreCase))
        {
            _log("[Steuerung] Kopplung abgebrochen — genannter Fingerabdruck passt nicht zum Zertifikat");
            return false;
        }

        await WriteAsync(tls, new { t = "pair.reveal", random = Convert.ToHexString(mine) });

        var reveal = await ExpectAsync(tls, "pair.reveal");
        var theirs = Convert.FromHexString(Text(reveal, "random") ?? "");

        if (theirCommitment is null ||
            !CryptographicOperations.FixedTimeEquals(SHA256.HashData(theirs), Convert.FromHexString(theirCommitment)))
        {
            _log("[Steuerung] Kopplung abgebrochen — die Gegenseite hält sich nicht an ihre Festlegung");
            return false;
        }

        var code = Identity.PairingCode(_identity.Fingerprint, mine, fingerprint, theirs);

        var request = new PairingRequest(code, fingerprint, theirName, address);
        _log($"[Steuerung] Kopplung angefragt von {theirName} ({address}) — Ziffern {code}");
        PairingRequested?.Invoke(request);

        var here = await request.Answer.Task.WaitAsync(PairingWindow);
        await WriteAsync(tls, new { t = "pair.result", accepted = here });

        var result = await ExpectAsync(tls, "pair.result");
        var there = result.TryGetProperty("accepted", out var a) && a.ValueKind == JsonValueKind.True;

        if (!here || !there)
        {
            _log($"[Steuerung] Kopplung abgelehnt ({(here ? "" : "hier")}{(here || there ? "" : " und ")}{(there ? "" : "dort")})");
            return false;
        }

        _identity.Remember(fingerprint, theirName, address);
        _log($"[Steuerung] gekoppelt mit {theirName}");
        return true;
    }

    /// <summary>
    /// Eine Nachricht an die Gegenstelle schicken, wenn eine Steuerverbindung steht.
    /// </summary>
    /// <remarks>
    /// Der Sperrzähler ist nötig, weil Angebote und andere Meldungen aus verschiedenen
    /// Fäden kommen können: zwei halb geschriebene JSON-Zeilen ineinander wären für die
    /// Gegenseite unlesbar, und der Fehler sähe aus wie ein kaputtes Protokoll.
    /// </remarks>
    public async Task<bool> SendAsync(object message)
    {
        var tls = _talking;
        if (tls is null) return false;

        await _talkGate.WaitAsync();
        try
        {
            await WriteAsync(tls, message);
            return true;
        }
        catch (Exception ex)
        {
            _log($"[Steuerung] Senden fehlgeschlagen: {FirstLine(ex.Message)}");
            return false;
        }
        finally { _talkGate.Release(); }
    }

    private async Task ConverseAsync(SslStream tls)
    {
        while (!_cts.IsCancellationRequested)
        {
            var message = await ReadAsync(tls);
            if (message is null) return;

            if (message.Value.TryGetProperty("t", out var kind) && kind.GetString() is { } name)
                Command?.Invoke(name, message.Value);
        }
    }

    // ------------------------------------------------------ Zeilen im Kanal

    private static async Task WriteAsync(SslStream tls, object message)
    {
        var line = JsonSerializer.Serialize(message) + "\n";
        await tls.WriteAsync(Encoding.UTF8.GetBytes(line));
        await tls.FlushAsync();
    }

    /// <summary>Die nächste Zeile lesen und darauf bestehen, dass sie die erwartete ist.</summary>
    private static async Task<JsonElement> ExpectAsync(SslStream tls, string kind)
    {
        var message = await ReadAsync(tls).WaitAsync(StepTimeout)
                      ?? throw new InvalidOperationException($"Verbindung endete, erwartet war „{kind}“");

        var actual = message.TryGetProperty("t", out var t) ? t.GetString() : null;
        if (actual != kind) throw new InvalidOperationException($"„{actual}“ statt „{kind}“");

        return message;
    }

    private static async Task<JsonElement?> ReadAsync(SslStream tls)
    {
        var buffer = new byte[1];
        var line = new List<byte>(256);

        while (true)
        {
            var n = await tls.ReadAsync(buffer);
            if (n == 0) return null;
            if (buffer[0] == (byte)'\n') break;

            line.Add(buffer[0]);
            if (line.Count > 64 * 1024) throw new InvalidOperationException("Zeile zu lang");
        }

        try
        {
            using var document = JsonDocument.Parse(Encoding.UTF8.GetString(line.ToArray()));
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string FirstLine(string text)
    {
        var at = text.IndexOf('\n');
        return at < 0 ? text : text[..at].Trim();
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { }
        _cts.Dispose();
    }
}

/// <summary>
/// Wie ein Fingerabdruck gebildet wird — an genau einer Stelle festgelegt.
/// </summary>
/// <remarks>
/// SHA-256 über die DER-Darstellung des <b>ganzen</b> Zertifikats, 64 Hexzeichen in
/// Großbuchstaben, ohne Trenner. Das muss eindeutig sein: gäbe es zwei Auslegungen,
/// liefen die Umsetzungen auf beiden Seiten auseinander — und der Fehler sähe aus wie
/// „die Ziffern stimmen nicht“, also wie ein Angriff. Die Vierergruppen in der Anzeige
/// sind bloß Zierde und gehen in keine Rechnung ein.
/// </remarks>
internal static class Fingerprint
{
    public static string Of(X509Certificate certificate) =>
        Convert.ToHexString(SHA256.HashData(certificate.Export(X509ContentType.Cert)));

    public static string Of(X509Certificate2 certificate) =>
        Convert.ToHexString(SHA256.HashData(certificate.RawData));
}
