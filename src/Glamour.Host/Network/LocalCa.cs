using System.Formats.Asn1;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Glamour.Host.Network;

/// <summary>The CA is made the first time someone picks "install as app" (or loaded when an earlier run made it), not at startup.</summary>
public sealed class LocalCaHolder(string dir, TimeProvider time, int httpPort, int httpsPort)
{
    private readonly object _gate = new();
    private volatile LocalCa? _ca;

    public int HttpPort { get; } = httpPort;
    public int HttpsPort { get; } = httpsPort;

    /// <summary>Null until the CA exists. Kestrel's HTTPS port has no certificate to offer until then.</summary>
    public LocalCa? Current => _ca;

    public LocalCa Ensure()
    {
        lock (_gate)
        {
            if (_ca is null)
            {
                var ca = new LocalCa(dir, time, HttpPort, HttpsPort);
                ca.Refresh(Auth.LanAddress.Candidates(), Auth.LanAddress.MdnsName());
                _ca = ca;
            }
            return _ca;
        }
    }

    public void LoadIfExists()
    {
        if (File.Exists(Path.Combine(dir, LocalCa.CaFileName))) Ensure();
    }
}

/// <summary>
/// Opt-in HTTPS for the LAN. Glamour makes its own certificate authority once (kept in the user's profile, never added to Windows' trust store)
/// and signs a short-lived server certificate for the PC's current addresses. A phone installs the CA's public certificate once; after that
/// every re-issued server certificate (new Wi-Fi, new IP) is trusted without touching the phone again.
/// The CA may only vouch for private addresses, <c>localhost</c> and <c>.local</c> names, so a leaked key cannot impersonate a real website.
/// </summary>
public sealed class LocalCa
{
    public const string CaFileName = "ca.pfx";
    public static string DefaultDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Glamour", "ca");
    private static readonly TimeSpan CaLifetime = TimeSpan.FromDays(3650), LeafLifetime = TimeSpan.FromDays(90), RenewBefore = TimeSpan.FromDays(30);

    private readonly TimeProvider _time;
    private readonly X509Certificate2 _ca; // with its private key; only ever used to sign
    private readonly object _gate = new();
    private string _sans = "";

    public int HttpPort { get; }
    public int HttpsPort { get; }

    /// <summary>The CA's public certificate (what a phone installs).</summary>
    public byte[] CaDer { get; }

    /// <summary>SHA-256 of the CA certificate as "AA:BB:...", shown on the PC and on the phone's install page so they can be compared.</summary>
    public string Fingerprint { get; }

    /// <summary>The server certificate Kestrel presents right now.</summary>
    public X509Certificate2 ServerCertificate { get; private set; }

    public LocalCa(string dir, TimeProvider time, int httpPort, int httpsPort)
    {
        _time = time;
        HttpPort = httpPort;
        HttpsPort = httpsPort;
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, CaFileName);
        _ca = File.Exists(path) && TryLoad(path) is { } existing ? existing : Create(path);
        CaDer = _ca.RawData;
        Fingerprint = string.Join(':', SHA256.HashData(CaDer).Select(b => b.ToString("X2")));
        ServerCertificate = Issue(["localhost"], [IPAddress.Loopback]);
        _sans = SanKey(["localhost"], [IPAddress.Loopback]);
    }

    /// <summary>Reissue the server certificate when the PC's addresses changed or it is close to expiring. True when it was replaced.</summary>
    public bool Refresh(IEnumerable<IPAddress> addresses, string? mdnsName)
    {
        var ips = addresses.Append(IPAddress.Loopback).Distinct().ToList();
        var dns = new List<string> { "localhost" };
        if (mdnsName is not null) dns.Add(mdnsName);
        var key = SanKey(dns, ips);
        lock (_gate)
        {
            if (key == _sans && ServerCertificate.NotAfter - _time.GetUtcNow() > RenewBefore) return false;
            ServerCertificate = Issue(dns, ips);
            _sans = key;
            return true;
        }
    }

    /// <summary>A server certificate signed by this CA for exactly these names.</summary>
    public X509Certificate2 Issue(IEnumerable<string> dnsNames, IEnumerable<IPAddress> addresses)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest("CN=Glamour", key, HashAlgorithmName.SHA256);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false)); // serverAuth
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));
        req.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(_ca, true, false));
        var san = new SubjectAlternativeNameBuilder();
        foreach (var d in dnsNames) san.AddDnsName(d);
        foreach (var a in addresses) san.AddIpAddress(a);
        req.CertificateExtensions.Add(san.Build());

        var now = _time.GetUtcNow();
        var serial = RandomNumberGenerator.GetBytes(16);
        serial[0] &= 0x7F; // positive
        using var signed = req.Create(_ca, now.AddHours(-1), now + LeafLifetime, serial);
        using var withKey = signed.CopyWithPrivateKey(key);
        // Round trip through PKCS#12: a key that only lives in memory is often refused by Windows' TLS stack.
        return X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pfx), null);
    }

    private static string SanKey(IEnumerable<string> dns, IEnumerable<IPAddress> ips) =>
        string.Join('|', dns.Order(StringComparer.Ordinal)) + "#" + string.Join('|', ips.Select(i => i.ToString()).Order(StringComparer.Ordinal));

    private static X509Certificate2? TryLoad(string path)
    {
        try
        {
            var cert = X509CertificateLoader.LoadPkcs12FromFile(path, null, X509KeyStorageFlags.EphemeralKeySet);
            return cert.HasPrivateKey && cert.NotAfter > DateTime.UtcNow.AddDays(30) ? cert : null; // an expiring CA is replaced (phones then install the new one)
        }
        catch (CryptographicException) { return null; }
    }

    private X509Certificate2 Create(string path)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest($"CN=Glamour local CA ({Environment.MachineName})", key, HashAlgorithmName.SHA256);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));
        req.CertificateExtensions.Add(NameConstraints());
        var now = _time.GetUtcNow();
        using var cert = req.CreateSelfSigned(now.AddDays(-1), now + CaLifetime);
        var pfx = cert.Export(X509ContentType.Pfx);
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, pfx);
        File.Move(tmp, path, overwrite: true);
        return X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.EphemeralKeySet);
    }

    // Permitted only: localhost, *.local, and the private ranges Glamour listens for (LAN, Tailscale, loopback, link-local).
    private static readonly (string Ip, int Prefix)[] PermittedRanges =
        [("127.0.0.0", 8), ("10.0.0.0", 8), ("172.16.0.0", 12), ("192.168.0.0", 16), ("100.64.0.0", 10), ("169.254.0.0", 16)];

    private static X509Extension NameConstraints()
    {
        var w = new AsnWriter(AsnEncodingRules.DER);
        using (w.PushSequence())
        using (w.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0, true)))
        {
            foreach (var dns in new[] { "localhost", "local" })
                using (w.PushSequence())
                    w.WriteCharacterString(UniversalTagNumber.IA5String, dns, new Asn1Tag(TagClass.ContextSpecific, 2));
            foreach (var (ip, prefix) in PermittedRanges)
            {
                var bytes = new byte[8];
                IPAddress.Parse(ip).GetAddressBytes().CopyTo(bytes, 0);
                for (var i = 0; i < 4; i++) bytes[4 + i] = (byte)(prefix >= (i + 1) * 8 ? 0xFF : prefix <= i * 8 ? 0 : 0xFF << (8 - (prefix - i * 8)));
                using (w.PushSequence())
                    w.WriteOctetString(bytes, new Asn1Tag(TagClass.ContextSpecific, 7));
            }
        }
        return new X509Extension(new Oid("2.5.29.30"), w.Encode(), true);
    }
}
