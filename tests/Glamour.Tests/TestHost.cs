using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Glamour.Host.Network;
using Microsoft.Extensions.DependencyInjection;

namespace Glamour.Tests;

/// <summary>The real host in memory. Each client claims an address with the X-Test-Ip header (the test server has none of its own).</summary>
public sealed class TestHost : IDisposable
{
    public const string Loopback = "127.0.0.1", Lan = "192.168.1.50";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "glamour-host-" + Guid.NewGuid().ToString("N"));
    private readonly WebApplicationFactory<Program> _factory;

    public IServiceProvider Services => _factory.Services;
    public FakeFirewall Firewall { get; } = new();
    public FakeWebPush WebPush { get; } = new();
    public FakeChampionSource Champions { get; } = new();
    public FakeSpellSource Spells { get; } = new();

    public FakeRiotClient RiotClient { get; } = new();
    public string RiotSessionFile => Path.Combine(_dir, "riot", "RiotGamesPrivateSettings.yaml");
    /// <summary>Where "Send a log" reads from (never the real logs).</summary>
    public string LogDir => Path.Combine(_dir, "logs");

    public TestHost(bool https = false, bool lan = false, Action<IServiceCollection>? configure = null, Dictionary<string, string>? settings = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(RiotSessionFile)!);
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Glamour:ProfilesPath", Path.Combine(_dir, "profiles"));
            b.UseSetting("Glamour:RiotSessionPath", RiotSessionFile);
            b.ConfigureServices(s => s.AddSingleton<Glamour.Host.Profiles.IRiotClient>(RiotClient)); // never the real processes
            b.UseSetting("Glamour:SettingsPath", Path.Combine(_dir, "settings.json"));
            b.UseSetting("Glamour:NoClient", "true");
            b.UseSetting("Glamour:HostSettingsPath", Path.Combine(_dir, "host.json"));
            b.UseSetting("Glamour:CaDir", Path.Combine(_dir, "ca"));
            b.UseSetting("Glamour:LogDir", LogDir);
            if (https || lan) b.UseSetting("Glamour:AllowLan", "true");
            if (https) b.UseSetting("Glamour:Https", "true"); // creates the certificate at startup
            b.UseSetting("Glamour:AuthPath", Path.Combine(_dir, "auth.json"));
            b.UseSetting("Glamour:PushPath", Path.Combine(_dir, "push.json"));
            b.ConfigureServices(s => s.AddSingleton<Glamour.Host.Push.IWebPushSender>(WebPush)); // never the real push services
            b.UseSetting("Glamour:ChampionsPath", Path.Combine(_dir, "champions.json"));
            b.ConfigureServices(s => s.AddSingleton<Glamour.Host.IChampionSource>(Champions));
            b.ConfigureServices(s => s.AddSingleton<Glamour.Host.ISpellSource>(Spells));
            b.ConfigureServices(s => s.AddSingleton<IFirewall>(Firewall));
            b.ConfigureServices(s => s.AddTransient<Microsoft.AspNetCore.Hosting.IStartupFilter, FakeIpFilter>());
            foreach (var (k, v) in settings ?? []) b.UseSetting(k, v);
            if (configure is not null) b.ConfigureServices(configure);
        });
    }

    /// <summary>A browser: own cookie jar, own address.</summary>
    public HttpClient Client(string ip = Lan, bool tls = false, bool follow = true, int? port = null)
    {
        var c = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = follow, BaseAddress = new Uri((tls ? "https://localhost" : "http://localhost") + (port is { } p ? ":" + p : "")) });
        c.DefaultRequestHeaders.Add("X-Test-Ip", ip);
        return c;
    }

    public void Dispose()
    {
        _factory.Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private sealed class FakeIpFilter : Microsoft.AspNetCore.Hosting.IStartupFilter
    {
        public Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> Configure(Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> next) => app =>
        {
            app.Use((ctx, n) =>
            {
                if (ctx.Request.Headers.TryGetValue("X-Test-Ip", out var v)) ctx.Connection.RemoteIpAddress = IPAddress.Parse(v.ToString());
                return n();
            });
            next(app);
        };
    }
}

public sealed class FakeFirewall : IFirewall
{
    public FirewallStatus Status { get; set; } = new(true, false, false, false);
    public bool UserAccepts { get; set; } = true;
    public List<int> Added { get; } = [];

    public FirewallStatus Check(int port) => Status;

    public Task<bool> AddRuleAsync(IReadOnlyCollection<int> ports, CancellationToken ct)
    {
        if (!UserAccepts) return Task.FromResult(false);
        Added.AddRange(ports);
        Status = new FirewallStatus(true, true, true, false);
        return Task.FromResult(true);
    }
}

public sealed class FakeChampionSource : Glamour.Host.IChampionSource
{
    public bool Fail { get; set; }
    public int Calls { get; private set; }
    public int VersionCalls { get; private set; }
    public string Version { get; set; } = "16.1.1";
    /// <summary>The list to answer with; null = Aatrox and Zed.</summary>
    public IReadOnlyList<Glamour.Host.ChampionInfo>? Champions { get; set; }

    public Task<string> LatestVersionAsync(CancellationToken ct)
    {
        VersionCalls++;
        if (Fail) throw new HttpRequestException("offline");
        return Task.FromResult(Version);
    }

    public Task<Glamour.Host.ChampionList> FetchAsync(CancellationToken ct)
    {
        Calls++;
        if (Fail) throw new HttpRequestException("offline");
        return Task.FromResult(new Glamour.Host.ChampionList(Version, Champions ?? [new(266, "Aatrox", "https://example.test/Aatrox.png"), new(238, "Zed", "https://example.test/Zed.png")]));
    }
}
