using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Glamour.Host.Logging;

namespace Glamour.Host.Telemetry;

/// <summary>
/// "Help improve Glamour" (SPEC "Publishing"): errors to Sentry and usage counts to Aptabase, both written here by hand so what leaves the PC is exactly
/// what this file builds. No user, no machine name, no install id: an error is the exception, its frames and the scrubbed message; a usage event
/// is a word, a few counts and the app version, under a session id made fresh per run. One per process (it outlives the desktop app's host restarts).
/// Never made under Glamour:NoClient (tests, the demo host).
/// </summary>
public sealed partial class Telemetry : IDisposable
{
    // Public client keys (SPEC "Publishing"): they can only send, never read.
    private const string SentryKey = "f0181b222464c4b84763609528912557";
    private const string SentryUrl = "https://o4512245239578624.ingest.de.sentry.io/api/4512245249933392/envelope/";
    private const string AptabaseKey = "A-EU-3112221579";
    private const string AptabaseUrl = "https://eu.aptabase.com/api/v0/event";

    /// <summary>Errors per run at most; a loop of one failure counts once anyway (<see cref="_seen"/>).</summary>
    private const int MaxErrors = 25;
    private const int RecentEvents = 200;

    private readonly HttpClient _http;
    private readonly Channel<HttpRequestMessage> _queue = System.Threading.Channels.Channel.CreateBounded<HttpRequestMessage>(new BoundedChannelOptions(100) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly HashSet<string> _seen = [];
    private readonly Queue<string> _recent = new(); // the last event lines, so the scrubber knows this session's names
    private readonly Lock _gate = new();
    private readonly string _kind;
    private readonly Task _sender;
    private int _errors, _pending, _started;
    private DateTimeOffset _sentryPausedUntil;
    private string _session = NewSession();
    private DateTimeOffset _lastUsage = DateTimeOffset.UtcNow;

    /// <summary>Read before every send: the "Help improve Glamour" switch (host.json). Off until a host says otherwise.</summary>
    public Func<bool> IsOn { get; set; } = () => false;

    /// <summary>Riot IDs this PC is known by (saved accounts): scrubbed from an error even when no event of this run named them.</summary>
    public Func<IEnumerable<string>> KnownNames { get; set; } = () => [];

    /// <param name="kind">"desktop" or "console", sent as a tag.</param>
    /// <param name="handler">Tests pass a fake; null is the real network.</param>
    public Telemetry(string kind, HttpMessageHandler? handler = null)
    {
        _kind = kind;
        _http = handler is null ? new HttpClient { Timeout = TimeSpan.FromSeconds(15) } : new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        _sender = Task.Run(SendLoopAsync);
    }

    public static string Version { get; } = typeof(Telemetry).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

    /// <summary>"release" for a build of the release workflow, "dev" for every local build (the user's own runs included).</summary>
    public static string Channel { get; } = typeof(Telemetry).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "GlamourChannel")?.Value ?? "dev";

    /// <summary>The version with the commit cut to 7 characters ("0.1.0+0c3198a").</summary>
    public static string ShortVersion => Version.IndexOf('+') is var plus and > 0 && Version.Length > plus + 8 ? Version[..(plus + 8)] : Version;

    /// <summary>Remembers an event line ("Glamour.Events: kind {json}") for the scrubber.</summary>
    public void Saw(string line)
    {
        lock (_gate)
        {
            _recent.Enqueue(line);
            if (_recent.Count > RecentEvents) _recent.Dequeue();
        }
    }

    /// <summary>An error for Sentry: once per kind per run, at most <see cref="MaxErrors"/>.</summary>
    /// <param name="template">The log message before its values went in (groups the same error together); the formatted one is sent scrubbed.</param>
    public void Error(string category, string template, string message, Exception? ex, bool fatal = false)
    {
        if (!IsOn() || DateTimeOffset.UtcNow < _sentryPausedUntil) return;
        var key = category + "|" + (ex is null ? template : ex.GetType().FullName + "|" + TopFrame(ex));
        lock (_gate)
        {
            if (_errors >= MaxErrors || !_seen.Add(key)) return;
            _errors++;
        }
        Enqueue(SentryRequest(BuildError(category, template, message, ex, fatal)));
    }

    /// <summary>A crash: sent right away on this thread (the process is about to end), within a few seconds.</summary>
    public void Crash(string category, Exception ex)
    {
        if (!IsOn()) return;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            using var r = _http.Send(SentryRequest(BuildError(category, "Unhandled exception", "Unhandled exception", ex, fatal: true)), cts.Token);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or IOException) { /* offline: nothing to do */ }
    }

    /// <summary>A usage event for Aptabase. Props are words and numbers only.</summary>
    public void Track(string name, IReadOnlyDictionary<string, object> props)
    {
        if (!IsOn()) return;
        var now = DateTimeOffset.UtcNow;
        string session;
        lock (_gate)
        {
            if (now - _lastUsage > TimeSpan.FromHours(1)) _session = NewSession(); // as Aptabase's own SDKs: an hour of quiet starts a new session
            _lastUsage = now;
            session = _session;
        }
        var body = new JsonObject
        {
            ["timestamp"] = now.ToString("o"),
            ["sessionId"] = session,
            ["eventName"] = name,
            ["systemProps"] = new JsonObject
            {
                ["isDebug"] = Channel != "release",
                ["osName"] = "Windows",
                ["osVersion"] = Environment.OSVersion.Version.ToString(),
                ["locale"] = System.Globalization.CultureInfo.CurrentUICulture.Name,
                ["appVersion"] = ShortVersion,
                ["appBuildNumber"] = "",
                ["sdkVersion"] = "glamour@1",
            },
            ["props"] = new JsonObject(props.Select(p => KeyValuePair.Create(p.Key, Prop(p.Value)))),
        };
        var request = new HttpRequestMessage(HttpMethod.Post, AptabaseUrl) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        request.Headers.Add("App-Key", AptabaseKey);
        Enqueue(request);
    }

    /// <summary>Aptabase takes words and numbers.</summary>
    private static JsonNode? Prop(object value) => value switch
    {
        int i => JsonValue.Create(i),
        long l => JsonValue.Create(l),
        double d => JsonValue.Create(d),
        bool b => JsonValue.Create(b ? "on" : "off"),
        _ => JsonValue.Create(value.ToString()),
    };

    /// <summary>True the first time only: the "start" event goes once per run, not again when the desktop app restarts its host.</summary>
    public bool FirstStart() => Interlocked.Exchange(ref _started, 1) == 0;

    /// <summary>Waits until what is queued has gone (or the time is up). For exit.</summary>
    public void Flush(TimeSpan within)
    {
        var until = DateTime.UtcNow + within;
        while (Volatile.Read(ref _pending) > 0 && DateTime.UtcNow < until) Thread.Sleep(50);
    }

    private void Enqueue(HttpRequestMessage request)
    {
        Interlocked.Increment(ref _pending);
        if (!_queue.Writer.TryWrite(request)) { Interlocked.Decrement(ref _pending); request.Dispose(); }
    }

    private async Task SendLoopAsync()
    {
        await foreach (var request in _queue.Reader.ReadAllAsync())
        {
            try
            {
                using var response = await _http.SendAsync(request);
                // Sentry asks to wait when over its limits: no more errors until then.
                if (request.RequestUri?.Host.EndsWith("sentry.io", StringComparison.Ordinal) == true && (int)response.StatusCode == 429)
                    _sentryPausedUntil = DateTimeOffset.UtcNow + (response.Headers.RetryAfter?.Delta ?? TimeSpan.FromMinutes(10));
            }
            catch (Exception e) when (e is HttpRequestException or OperationCanceledException or IOException) { /* offline: dropped, never retried */ }
            finally
            {
                request.Dispose();
                Interlocked.Decrement(ref _pending);
            }
        }
    }

    private static HttpRequestMessage SentryRequest(JsonObject e)
    {
        var header = new JsonObject { ["event_id"] = e["event_id"]!.GetValue<string>(), ["sent_at"] = DateTimeOffset.UtcNow.ToString("o") };
        var envelope = header.ToJsonString() + "\n" + """{"type":"event"}""" + "\n" + e.ToJsonString() + "\n";
        var request = new HttpRequestMessage(HttpMethod.Post, SentryUrl) { Content = new StringContent(envelope, Encoding.UTF8, "application/x-sentry-envelope") };
        request.Headers.TryAddWithoutValidation("X-Sentry-Auth", $"Sentry sentry_version=7, sentry_key={SentryKey}, sentry_client=glamour/1");
        return request;
    }

    /// <summary>The Sentry event: everything in it is listed here, and every text in it went through a <see cref="Scrubber"/> that read this session's events first.</summary>
    internal JsonObject BuildError(string category, string template, string message, Exception? ex, bool fatal)
    {
        var scrub = Scrubber.ForThisPc();
        string[] recent;
        lock (_gate) recent = [.. _recent];
        foreach (var line in recent) scrub.Learn(line);
        foreach (var name in KnownNames()) scrub.Learn("Glamour.Profiles: " + name);
        var e = new JsonObject
        {
            ["event_id"] = Guid.NewGuid().ToString("N"),
            ["timestamp"] = DateTimeOffset.UtcNow.ToString("o"),
            ["platform"] = "csharp",
            ["level"] = fatal ? "fatal" : "error",
            ["logger"] = category,
            ["release"] = "glamour@" + Version,
            ["environment"] = Channel,
            ["logentry"] = new JsonObject { ["message"] = scrub.Text(template), ["formatted"] = scrub.Text(message) },
            ["tags"] = new JsonObject { ["app"] = _kind },
            ["contexts"] = new JsonObject
            {
                ["os"] = new JsonObject { ["name"] = "Windows", ["version"] = Environment.OSVersion.Version.ToString() },
                ["runtime"] = new JsonObject { ["name"] = ".NET", ["version"] = Environment.Version.ToString() },
            },
            ["sdk"] = new JsonObject { ["name"] = "glamour.telemetry", ["version"] = "1" },
        };
        if (ex is not null) e["exception"] = new JsonObject { ["values"] = Exceptions(ex, scrub) };
        return e;
    }

    /// <summary>The exception and its inner ones, innermost first (Sentry's order), each with its frames oldest first.</summary>
    private static JsonArray Exceptions(Exception ex, Scrubber scrub)
    {
        var chain = new List<Exception>();
        for (var e = ex; e is not null && chain.Count < 6; e = e is AggregateException { InnerExceptions.Count: > 0 } a ? a.InnerExceptions[0] : e.InnerException) chain.Add(e);
        chain.Reverse();
        var values = new JsonArray();
        foreach (var e in chain)
        {
            var frames = new JsonArray();
            foreach (var f in (new StackTrace(e, true).GetFrames() ?? []).Reverse())
            {
                var (module, function) = Name(f.GetMethod());
                var frame = new JsonObject { ["module"] = module, ["function"] = function, ["in_app"] = module.StartsWith("Glamour", StringComparison.Ordinal) };
                if (f.GetFileName() is { Length: > 0 } file) frame["filename"] = scrub.Text(file);
                if (f.GetFileLineNumber() is > 0 and var line) frame["lineno"] = line;
                frames.Add(frame);
            }
            var value = new JsonObject { ["type"] = e.GetType().FullName, ["value"] = scrub.Text(e.Message) };
            if (frames.Count > 0) value["stacktrace"] = new JsonObject { ["frames"] = frames };
            values.Add(value);
        }
        return values;
    }

    /// <summary>Type and method, with the compiler's async and lambda wrappers named after the method they came from.</summary>
    private static (string Module, string Function) Name(MethodBase? m)
    {
        if (m is null) return ("?", "?");
        var type = m.DeclaringType;
        var function = m.Name;
        if (type is not null && Generated().Match(type.Name) is { Success: true } g)
        {
            function = g.Groups[1].Value;
            type = type.DeclaringType ?? type;
        }
        return (type?.FullName ?? "?", function);
    }

    private static string TopFrame(Exception ex) =>
        (new StackTrace(ex, false).GetFrames() ?? []).Select(f => Name(f.GetMethod())).FirstOrDefault(n => n.Module.StartsWith("Glamour", StringComparison.Ordinal)) is var (module, function) && module is not null
            ? module + "." + function : "";

    private static string NewSession() => (DateTimeOffset.UtcNow.ToUnixTimeSeconds() * 100_000_000L + Random.Shared.NextInt64(0, 99_999_999)).ToString(System.Globalization.CultureInfo.InvariantCulture);

    [GeneratedRegex(@"^<(\w+)>")]
    private static partial Regex Generated();

    public void Dispose()
    {
        _queue.Writer.TryComplete();
        _sender.Wait(TimeSpan.FromSeconds(1));
        _http.Dispose();
    }
}
