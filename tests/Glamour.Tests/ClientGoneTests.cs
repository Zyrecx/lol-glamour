using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Glamour.Lcu;
using Microsoft.Extensions.DependencyInjection;

namespace Glamour.Tests;

public class ClientGoneTests
{
    // What the real client's HttpClient throws when League closes mid-request (glamour.log 2026-10-09 13:35, GET /api/queues while League restarted).
    private sealed class GoneClient(Exception e) : ILcuClient
    {
        public Task<LcuResponse> SendAsync(HttpMethod method, string path, object? body = null, CancellationToken ct = default) => Task.FromException<LcuResponse>(e);
    }

    [Theory]
    [InlineData(typeof(TaskCanceledException))]
    [InlineData(typeof(HttpRequestException))]
    [InlineData(typeof(ObjectDisposedException))]
    public async Task A_client_call_that_dies_with_League_answers_503(Type kind)
    {
        var e = kind == typeof(ObjectDisposedException) ? new ObjectDisposedException("HttpClient") : (Exception)Activator.CreateInstance(kind)!;
        using var host = new TestHost(configure: s => s.AddSingleton<ILcuClient>(new GoneClient(e)));
        var r = await host.Client(TestHost.Loopback).GetAsync("/api/queues");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, r.StatusCode);
        Assert.Equal("The League client is not running.", (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
    }
}
