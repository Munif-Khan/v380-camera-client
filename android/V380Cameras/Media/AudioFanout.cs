using System.Collections.Concurrent;
using System.Threading.Channels;

namespace V380Cameras.Media;

/// <summary>
/// Fan-out for the live audio stream. Each /audio.wav listener gets its own
/// small queue; a slow or paused listener simply drops old chunks instead of
/// stalling the camera socket.
/// </summary>
public sealed class AudioFanout
{
    private readonly ConcurrentDictionary<Guid, Channel<byte[]>> sinks = new();

    public void Publish(byte[] pcm)
    {
        foreach (var sink in sinks)
        {
            // DropOldest keeps latency bounded when the browser stops reading.
            sink.Value.Writer.TryWrite(pcm);
        }
    }

    public Subscription Subscribe()
    {
        var id = Guid.NewGuid();
        var channel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(64)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.DropOldest
        });
        sinks[id] = channel;
        return new Subscription(this, id, channel.Reader);
    }

    public sealed class Subscription : IDisposable
    {
        private readonly AudioFanout owner;
        private readonly Guid id;
        private bool disposed;

        public ChannelReader<byte[]> Reader { get; }

        internal Subscription(AudioFanout owner, Guid id, ChannelReader<byte[]> reader)
        {
            this.owner = owner;
            this.id = id;
            Reader = reader;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            owner.sinks.TryRemove(id, out _);
        }
    }
}
