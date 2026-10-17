using Glamour.Core;
using Glamour.Host;
using Microsoft.Extensions.Configuration;

namespace Glamour.Tests;

public class NtfyClickTests
{
    private sealed class CaptureHandler : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Request = request;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler);
    }

    private static IConfiguration Config(params (string, string)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Item1, v.Item2))).Build();

    [Fact]
    public void Link_is_the_public_url_when_set_and_nothing_when_the_host_is_PC_only()
    {
        Assert.Equal("https://glamour.example/", NtfyNotifier.UiUrl(Config(("Glamour:PublicUrl", "https://glamour.example"))));
        Assert.Null(NtfyNotifier.UiUrl(Config())); // no LAN access: a phone could not open it
    }

    [Fact]
    public async Task Notification_carries_the_click_target_so_a_tap_opens_the_web_ui()
    {
        var handler = new CaptureHandler();
        var notifier = new NtfyNotifier(new Factory(handler), Config(("Glamour:PublicUrl", "http://192.168.1.20:5170")));
        await notifier.SendAsync("https://ntfy.sh", "topic", new PhoneNotification("t", "b", 3), default);
        Assert.Equal("http://192.168.1.20:5170/", handler.Request!.Headers.GetValues("Click").Single());
    }

    [Fact]
    public async Task No_click_header_when_there_is_no_address_to_open()
    {
        var handler = new CaptureHandler();
        await new NtfyNotifier(new Factory(handler), Config()).SendAsync("https://ntfy.sh", "topic", new PhoneNotification("t", "b", 3), default);
        Assert.False(handler.Request!.Headers.Contains("Click"));
    }
}
