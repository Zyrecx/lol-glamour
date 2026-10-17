using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Glamour.Core;
using Glamour.Host.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Glamour.Tests;

/// <summary>Keeps what the host logs, so a test can read the lines the log file would get.</summary>
public sealed class CapturedLog : ILoggerProvider
{
    public ConcurrentQueue<(string Category, LogLevel Level, string Message)> Lines { get; } = new();
    public ILogger CreateLogger(string category) => new Logger(this, category);
    public void Dispose() { }
    public IEnumerable<string> In(string category) => Lines.Where(l => l.Category == category).Select(l => l.Message);

    private sealed class Logger(CapturedLog log, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex, Func<TState, Exception?, string> format) => log.Lines.Enqueue((category, level, format(state, ex)));
    }
}

public class LoggingTests : IDisposable
{
    private readonly CapturedLog _log = new();
    private readonly TestHost _host;

    public LoggingTests() => _host = new TestHost(lan: true, configure: s => s.AddSingleton<ILoggerProvider>(_log));

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task Actions_and_refusals_are_logged_but_plain_reads_are_not()
    {
        await _host.Client(TestHost.Loopback).GetAsync("/api/state");
        await _host.Client(TestHost.Loopback).PutAsJsonAsync("/api/automation/pause", new { paused = true });
        await _host.Client().GetAsync("/api/state"); // not paired: 401

        var lines = _log.In("Glamour.Requests").ToList();
        Assert.DoesNotContain(lines, l => l.StartsWith("GET /api/state -> 200"));
        Assert.Contains(lines, l => l.StartsWith("PUT /api/automation/pause -> 200 for this PC"));
        Assert.Contains(lines, l => l.StartsWith("GET /api/state -> 401 for 192.168.1.50"));
    }

    [Fact]
    public async Task A_flood_of_refusals_writes_one_line()
    {
        for (var i = 0; i < 20; i++) await _host.Client().GetAsync("/api/state");
        Assert.Single(_log.In("Glamour.Requests"), l => l.StartsWith("GET /api/state -> 401"));
    }

    [Fact]
    public async Task A_rebound_host_name_is_logged_with_the_name()
    {
        var r = new HttpRequestMessage(HttpMethod.Get, "/api/state");
        r.Headers.Host = "evil.example";
        Assert.Equal(HttpStatusCode.Forbidden, (await _host.Client().SendAsync(r)).StatusCode);
        Assert.Contains(_log.In("Glamour.Requests"), l => l.Contains("-> 403") && l.Contains("host evil.example"));
    }

    [Fact]
    public async Task Ui_script_errors_reach_the_log_and_need_a_signed_in_caller()
    {
        var entry = new { kind = "error", message = "x is undefined", stack = "at Board.svelte:12", page = "/?view=phone" };
        Assert.Equal(HttpStatusCode.Unauthorized, (await _host.Client().PostAsJsonAsync("/api/client-log", entry)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await _host.Client(TestHost.Loopback).PostAsJsonAsync("/api/client-log", entry)).StatusCode);

        var line = Assert.Single(_log.In("Glamour.Web"));
        Assert.Contains("this PC on /?view=phone: error: x is undefined", line);
        Assert.Contains("Board.svelte:12", line);
    }

    [Fact]
    public async Task Ui_events_are_logged_once_each_and_settings_by_name_only()
    {
        var hub = _host.Services.GetRequiredService<EventHub>();
        await Task.Delay(100); // the event logger subscribes when the host starts
        hub.Publish("parties", new { allies = new[] { 2 } });
        hub.Publish("parties", new { allies = new[] { 2 } });
        hub.Publish("settings", new { ntfyTopic = "secret-topic" });

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (_log.In("Glamour.Events").Count() < 2 && DateTime.UtcNow < deadline) await Task.Delay(20);
        var lines = _log.In("Glamour.Events").ToList();
        Assert.Single(lines, l => l == "parties {\"allies\":[2]}");
        Assert.Contains("settings (changed)", lines);
        Assert.DoesNotContain(lines, l => l.Contains("secret-topic"));
    }
}

public class FileLogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "glamour-log-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    [Fact]
    public void A_full_file_becomes_the_old_one_and_only_two_are_kept()
    {
        using (var log = new FileLog(_dir, maxBytes: 1000))
        {
            var logger = log.CreateLogger("t");
            for (var i = 0; i < 100; i++) logger.LogInformation("line {I} {Pad}", i, new string('x', 50));
        }
        Assert.Equal(["glamour.1.log", "glamour.log"], Directory.GetFiles(_dir).Select(Path.GetFileName).Order().ToArray());
        Assert.All(Directory.GetFiles(_dir), f => Assert.True(new FileInfo(f).Length < 1200));
        Assert.Contains("line 99", File.ReadAllText(Path.Combine(_dir, "glamour.log")));
    }
}
