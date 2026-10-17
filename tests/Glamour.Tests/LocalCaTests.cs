using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using Glamour.Host.Network;

namespace Glamour.Tests;

public sealed class LocalCaTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "glamour-ca-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    private LocalCa Open(TimeProvider? time = null) => new(_dir, time ?? TimeProvider.System, 5170, 5171);

    private static bool Trusts(LocalCa ca, X509Certificate2 leaf)
    {
        using var root = X509CertificateLoader.LoadCertificate(ca.CaDer);
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(root);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        return chain.Build(leaf);
    }

    [Fact]
    public void Ca_is_created_once_and_reloaded_with_the_same_fingerprint()
    {
        var first = Open();
        var second = Open();
        Assert.Equal(first.Fingerprint, second.Fingerprint);
        Assert.Matches("^([0-9A-F]{2}:){31}[0-9A-F]{2}$", first.Fingerprint);
        Assert.True(File.Exists(Path.Combine(_dir, LocalCa.CaFileName)));
    }

    [Fact]
    public void Corrupt_ca_file_is_replaced()
    {
        var first = Open();
        File.WriteAllText(Path.Combine(_dir, LocalCa.CaFileName), "garbage");
        Assert.NotEqual(first.Fingerprint, Open().Fingerprint);
    }

    [Fact]
    public void Server_certificate_for_lan_names_validates_against_the_ca()
    {
        var ca = Open();
        Assert.True(ca.Refresh([IPAddress.Parse("192.168.1.50"), IPAddress.Parse("100.100.1.2")], "mypc.local"));
        var leaf = ca.ServerCertificate;
        Assert.True(Trusts(ca, leaf));
        var san = leaf.Extensions.OfType<X509SubjectAlternativeNameExtension>().Single();
        Assert.Contains("192.168.1.50", san.EnumerateIPAddresses().Select(i => i.ToString()));
        Assert.Contains("mypc.local", san.EnumerateDnsNames());
        Assert.Contains("localhost", san.EnumerateDnsNames());
        Assert.True((leaf.NotAfter - leaf.NotBefore).TotalDays <= 398); // Chrome's limit
    }

    [Fact]
    public void Refresh_reissues_only_when_the_addresses_change()
    {
        var ca = Open();
        var ips = new[] { IPAddress.Parse("192.168.1.50") };
        Assert.True(ca.Refresh(ips, null));
        var before = ca.ServerCertificate.Thumbprint;
        Assert.False(ca.Refresh(ips, null));
        Assert.Equal(before, ca.ServerCertificate.Thumbprint);
        Assert.True(ca.Refresh([IPAddress.Parse("192.168.43.7")], null)); // hotspot
        Assert.NotEqual(before, ca.ServerCertificate.Thumbprint);
        Assert.True(Trusts(ca, ca.ServerCertificate));
    }

    [Fact]
    public void Refresh_renews_a_certificate_near_expiry()
    {
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(DateTimeOffset.UtcNow);
        var ca = Open(time);
        var ips = new[] { IPAddress.Parse("192.168.1.50") };
        ca.Refresh(ips, null);
        var before = ca.ServerCertificate.Thumbprint;
        time.Advance(TimeSpan.FromDays(70)); // 20 days left of 90
        Assert.True(ca.Refresh(ips, null));
        Assert.NotEqual(before, ca.ServerCertificate.Thumbprint);
    }

    [Theory]
    [InlineData("example.com", null)]
    [InlineData(null, "8.8.8.8")]
    [InlineData(null, "172.32.0.1")] // just outside 172.16/12
    public void Ca_cannot_vouch_for_public_names_or_addresses(string? dns, string? ip)
    {
        var ca = Open();
        using var leaf = ca.Issue(dns is null ? [] : [dns], ip is null ? [] : [IPAddress.Parse(ip)]);
        Assert.False(Trusts(ca, leaf));
    }

    [Fact]
    public async Task Server_certificate_works_in_a_real_tls_handshake()
    {
        var ca = Open();
        ca.Refresh([IPAddress.Loopback], "mypc.local");
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var tcp = await listener.AcceptTcpClientAsync();
            await using var tls = new SslStream(tcp.GetStream());
            await tls.AuthenticateAsServerAsync(ca.ServerCertificate);
            tls.WriteByte(42);
            await tls.FlushAsync();
        });

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        await using var stream = new SslStream(client.GetStream(), false, (_, cert, _, errors) =>
            (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) == SslPolicyErrors.None && cert is not null && Trusts(ca, new X509Certificate2(cert)));
        await stream.AuthenticateAsClientAsync("localhost");
        Assert.Equal(42, stream.ReadByte());
        await server;
        listener.Stop();
    }
}
