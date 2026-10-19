using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace ScreenBridge;

/// <summary>
/// Wer dieser Rechner ist, und wen er kennt.
/// </summary>
/// <remarks>
/// <para>
/// Die Identität ist ein dauerhaftes Schlüsselpaar in Gestalt eines selbst
/// unterschriebenen Zertifikats. Es wird einmal erzeugt und bleibt; sein Fingerabdruck
/// ist der Name, unter dem die Gegenseite uns wiedererkennt.
/// </para>
/// <para>
/// <b>Keine Zertifizierungsstelle.</b> Es gibt niemanden, der hier etwas bestätigen
/// könnte, und es braucht auch niemanden: die beiden Geräte kennen einander direkt, so
/// wie zwei Bluetooth-Geräte nach dem Koppeln. Eine öffentliche Vertrauenskette wäre für
/// zwei Rechner im selben Zimmer der falsche Aufwand.
/// </para>
/// <para>
/// <b>Der private Schlüssel liegt mit DPAPI verschlüsselt auf der Platte.</b> Das
/// schützt nicht gegen jemanden, der ohnehin unter diesem Benutzerkonto arbeitet — wer
/// das kann, sieht den Bildschirm auch direkt. Es schützt gegen das Naheliegende: eine
/// kopierte Datei, ein Backup, ein zweites Betriebssystem auf derselben Platte.
/// </para>
/// </remarks>
internal sealed class Identity
{
    private static readonly string Folder =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ScreenBridge");

    private readonly string _keyPath = Path.Combine(Folder, "identity.bin");
    private readonly string _peerPath = Path.Combine(Folder, "peers.json");

    private List<Peer> _peers = [];

    public Identity()
    {
        Directory.CreateDirectory(Folder);
        Certificate = LoadOrCreate();
        LoadPeers();
    }

    public X509Certificate2 Certificate { get; }

    /// <summary>Unser Fingerabdruck — vierzig Zeichen, die uns eindeutig benennen.</summary>
    public string Fingerprint => ScreenBridge.Fingerprint.Of(Certificate);

    /// <summary>Der Fingerabdruck in lesbaren Vierergruppen, zum Vorlesen und Vergleichen.</summary>
    public string ReadableFingerprint => Readable(Fingerprint);

    /// <summary>
    /// Irgendeinen Fingerabdruck lesbar machen — auch den der Gegenstelle.
    /// </summary>
    /// <remarks>
    /// Die Vierergruppen sind Zierde und gehen in keine Rechnung ein; verglichen und
    /// gespeichert wird immer die ungetrennte Form. Gezeigt werden nur die ersten
    /// 16 Zeichen: zum Wiedererkennen genügen sie, und 64 liest niemand.
    /// </remarks>
    public static string Readable(string fingerprint) =>
        string.Join(" ", Chunk(fingerprint[..Math.Min(16, fingerprint.Length)], 4));

    public IReadOnlyList<Peer> Peers => _peers;

    public bool Knows(string fingerprint) =>
        _peers.Any(p => string.Equals(p.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase));

    public Peer? Find(string fingerprint) =>
        _peers.FirstOrDefault(p => string.Equals(p.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase));

    public void Remember(string fingerprint, string name, string address)
    {
        _peers.RemoveAll(p => string.Equals(p.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase));
        _peers.Add(new Peer(fingerprint, name, address, DateTime.UtcNow));
        SavePeers();
    }

    public void Forget(string fingerprint)
    {
        _peers.RemoveAll(p => string.Equals(p.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase));
        SavePeers();
    }

    /// <summary>
    /// Die sechs Ziffern, die beim Koppeln auf beiden Bildschirmen stehen müssen.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Sie entstehen aus beiden Fingerabdrücken <b>und</b> aus je einer frischen
    /// Zufallszahl, auf die sich beide Seiten vorher festgelegt haben. Die Reihenfolge
    /// ist am Fingerabdruck festgemacht, damit beide dasselbe errechnen.
    /// </para>
    /// <para>
    /// <b>Ohne die Zufallszahlen prüft der Vergleich nichts.</b> Fingerabdrücke sind
    /// öffentlich, und ein Angreifer bringt eigene mit — er kann so lange Zertifikate
    /// erzeugen, bis beide Seiten dieselbe Zahl sehen. Bei einer Million Werten genügen
    /// einige hundert Versuche. Erst die vorab festgelegte Zufallszahl macht das
    /// unmöglich: sie steht fest, bevor man weiß, worauf man hinarbeiten müsste.
    /// </para>
    /// </remarks>
    public static string PairingCode(string oneFingerprint, byte[] oneRandom,
                                     string otherFingerprint, byte[] otherRandom)
    {
        var one = (Fingerprint: oneFingerprint.ToUpperInvariant(), Random: oneRandom);
        var other = (Fingerprint: otherFingerprint.ToUpperInvariant(), Random: otherRandom);

        var (first, second) = string.CompareOrdinal(one.Fingerprint, other.Fingerprint) <= 0
            ? (one, other)
            : (other, one);

        var material = new List<byte>();
        material.AddRange(System.Text.Encoding.ASCII.GetBytes("ScreenBridge-Pair-v1"));
        material.AddRange(System.Text.Encoding.ASCII.GetBytes(first.Fingerprint));
        material.AddRange(first.Random);
        material.AddRange(System.Text.Encoding.ASCII.GetBytes(second.Fingerprint));
        material.AddRange(second.Random);

        var digest = SHA256.HashData(material.ToArray());
        return (BitConverter.ToUInt32(digest, 0) % 1_000_000).ToString("000000");
    }

    // ------------------------------------------------------------- Speichern

    private X509Certificate2 LoadOrCreate()
    {
        if (File.Exists(_keyPath))
        {
            try
            {
                var protectedBytes = File.ReadAllBytes(_keyPath);
                var pfx = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
                return X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.Exportable);
            }
            catch (Exception)
            {
                // Unlesbar geworden — dann lieber eine neue Identität als gar keine.
                // Die Kopplung muss danach erneuert werden, und das ist sichtbar.
            }
        }

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN=ScreenBridge {Environment.MachineName}", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid("1.3.6.1.5.5.7.3.1"), new Oid("1.3.6.1.5.5.7.3.2")], false));

        var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(20));

        // Schannel braucht den privaten Schlüssel aus einem PFX; ein unmittelbar
        // erzeugtes Zertifikat reicht ihm nicht.
        var exported = created.Export(X509ContentType.Pfx);
        File.WriteAllBytes(_keyPath, ProtectedData.Protect(exported, null, DataProtectionScope.CurrentUser));

        return X509CertificateLoader.LoadPkcs12(exported, null, X509KeyStorageFlags.Exportable);
    }

    private void LoadPeers()
    {
        try
        {
            if (File.Exists(_peerPath))
                _peers = JsonSerializer.Deserialize<List<Peer>>(File.ReadAllText(_peerPath)) ?? [];
        }
        catch (Exception) { _peers = []; }
    }

    private void SavePeers()
    {
        try
        {
            File.WriteAllText(_peerPath,
                JsonSerializer.Serialize(_peers, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception) { }
    }

    private static IEnumerable<string> Chunk(string text, int size)
    {
        for (var i = 0; i < text.Length; i += size)
            yield return text.Substring(i, Math.Min(size, text.Length - i));
    }
}

/// <summary>Ein gekoppeltes Gerät.</summary>
internal sealed record Peer(string Fingerprint, string Name, string Address, DateTime PairedAt);
