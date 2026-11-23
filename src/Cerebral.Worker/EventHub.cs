using System.Collections.Concurrent;
using System.Threading.Channels;
using Cerebral.Core;

namespace Cerebral.Worker;

public sealed partial class EventHub
{
    private long sequence;
    private readonly ConcurrentDictionary<Guid, Channel<ServiceEvent>> subscribers = new();
    public void Publish(string type, string? jobId, object? data)
    {
        var item = new ServiceEvent(Interlocked.Increment(ref sequence), type, jobId, CerebralJson.Element(data), DateTimeOffset.UtcNow);
        foreach (var channel in subscribers.Values) channel.Writer.TryWrite(item);
    }
    public (Guid Id, ChannelReader<ServiceEvent> Reader) Subscribe()
    {
        var id = Guid.NewGuid();
        var channel = Channel.CreateBounded<ServiceEvent>(new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropOldest });
        subscribers[id] = channel; return (id, channel.Reader);
    }
    public void Unsubscribe(Guid id) { if (subscribers.TryRemove(id, out var channel)) channel.Writer.TryComplete(); }
}
