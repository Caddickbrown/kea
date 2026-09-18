using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Kea.Web.Jobs;

/// <summary>
/// Fans job updates out to every connected browser.
/// </summary>
/// <remarks>
/// Server-sent events rather than websockets: updates only ever travel server to client, SSE needs
/// no extra dependency, and it reconnects on its own if the server restarts.
/// </remarks>
public sealed class JobEvents
{
    private readonly ConcurrentDictionary<Guid, Channel<JobEvent>> _subscribers = new();

    /// <summary>Sends an update to all listeners. Never blocks and never throws.</summary>
    public void Publish(JobEvent update)
    {
        foreach (Channel<JobEvent> channel in _subscribers.Values)
        {
            // A dropped update is acceptable: the browser also polls the job list on reconnect,
            // and a slow client must not be able to stall the download loop.
            channel.Writer.TryWrite(update);
        }
    }

    /// <summary>Opens a stream of updates for one client. Dispose the returned token to stop.</summary>
    public (Guid Id, ChannelReader<JobEvent> Reader) Subscribe()
    {
        Guid id = Guid.NewGuid();
        Channel<JobEvent> channel = Channel.CreateBounded<JobEvent>(new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

        _subscribers[id] = channel;
        return (id, channel.Reader);
    }

    public void Unsubscribe(Guid id)
    {
        if (_subscribers.TryRemove(id, out Channel<JobEvent>? channel)) channel.Writer.TryComplete();
    }
}
