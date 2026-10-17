using System.Threading.Channels;

namespace Glamour.Core;

/// <summary>One event for the UI stream. <see cref="Version"/> is the stream schema version, bumped on breaking changes.</summary>
public sealed record GlamourEvent(string Type, object? Data, int Version = 1);

/// <summary>Fans events out to every connected UI. A slow subscriber drops its oldest events instead of blocking the engine.</summary>
public sealed class EventHub
{
    private readonly object _gate = new();
    private readonly List<Channel<GlamourEvent>> _subscribers = [];

    public void Publish(string type, object? data = null)
    {
        var e = new GlamourEvent(type, data);
        lock (_gate)
            foreach (var s in _subscribers) s.Writer.TryWrite(e);
    }

    /// <summary>Starts listening immediately; dispose to stop. Events published after this call are never missed.</summary>
    public Subscription Subscribe()
    {
        var channel = Channel.CreateBounded<GlamourEvent>(new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.DropOldest });
        lock (_gate) _subscribers.Add(channel);
        return new Subscription(this, channel);
    }

    public sealed class Subscription(EventHub hub, Channel<GlamourEvent> channel) : IDisposable
    {
        public IAsyncEnumerable<GlamourEvent> ReadAllAsync(CancellationToken ct) => channel.Reader.ReadAllAsync(ct);

        public void Dispose()
        {
            lock (hub._gate) hub._subscribers.Remove(channel);
        }
    }
}
