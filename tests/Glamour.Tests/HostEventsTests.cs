using System.Net.Http.Json;
using Glamour.Core;

namespace Glamour.Tests;

public class HostEventsTests : IDisposable
{
    private readonly TestHost _host = new();

    public void Dispose() => _host.Dispose();

    internal static async Task<string> ReadEventAsync(StreamReader reader)
    {
        var lines = new List<string>();
        while (await reader.ReadLineAsync() is { } line)
        {
            if (line.Length == 0) { if (lines.Count > 0) break; continue; }
            lines.Add(line);
        }
        return string.Join('\n', lines);
    }

    [Fact]
    public async Task Stream_sends_snapshot_then_settings_changes()
    {
        var client = _host.Client(TestHost.Loopback);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var response = await client.GetAsync("/api/events", HttpCompletionOption.ResponseHeadersRead, cts.Token);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cts.Token));

        var snapshot = await ReadEventAsync(reader);
        Assert.Contains("event: snapshot", snapshot);
        Assert.Contains("\"Version\":1", snapshot, StringComparison.OrdinalIgnoreCase);
        // The web page reads these exact keys, so case matters.
        Assert.Contains("\"connected\":false", snapshot, StringComparison.Ordinal);
        Assert.Contains("\"readyCheck\":{\"state\":\"None\",\"playerResponse\":\"None\"", snapshot, StringComparison.Ordinal);
        Assert.Contains("\"dodge\":{\"state\":\"Invalid\"", snapshot, StringComparison.Ordinal);

        var put = await client.PutAsJsonAsync("/api/settings", new GlamourSettings { AutoBanChampionId = 238 }, cts.Token);
        put.EnsureSuccessStatusCode();

        var changed = await ReadEventAsync(reader);
        Assert.Contains("event: settings", changed);
        Assert.Contains("238", changed);
    }
}
