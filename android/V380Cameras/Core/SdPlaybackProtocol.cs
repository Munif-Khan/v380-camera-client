#nullable disable

using System.Buffers.Binary;
using System.Net.Sockets;
using System.Runtime.CompilerServices;

namespace V380Decoder.src;

/// <summary>
/// One media frame from the camera's TFV2 SD-card playback stream.
/// Type 50 is a video key frame, 51 a video delta frame, 22 an IMA-ADPCM
/// audio block. TimestampMs is the camera's millisecond wall clock and is
/// shared by the video and audio streams.
/// </summary>
public sealed record SdPlaybackFrame(ushort Type, ushort Width, ushort Height, ulong TimestampMs, byte[] Data)
{
    public const ushort VideoKeyFrame = 50;
    public const ushort VideoDeltaFrame = 51;
    public const ushort AudioAdpcm = 22;

    public bool IsVideo => Type is VideoKeyFrame or VideoDeltaFrame;
    public bool IsAudio => Type == AudioAdpcm;
}

/// <summary>
/// TFV2 (protocolType 0) SD-card playback. Reverse engineered from
/// libhsMediaLibrary.so: HSReplayDataTFV2Transmitter.
///
/// Sequence: login once (command 1167) to obtain the login handle, then for
/// each playback open a *new* TCP connection, send 363 (64 bytes), read the
/// 64-byte 463 reply, send 364 (32 bytes) and read 8-byte framed media
/// packets. The login socket may be closed while a playback is running; only
/// the numeric handle is needed.
/// </summary>
public sealed class SdPlaybackSession : IAsyncDisposable
{
    private readonly TcpClient client;
    private readonly NetworkStream stream;

    public ushort Width { get; }
    public ushort Height { get; }

    /// <summary>
    /// Number of 0x7f media packets received so far. This firmware serves at
    /// most 400 per playback session, after which it simply stops sending, so
    /// callers use this to finish a chunk without waiting for an idle timeout.
    /// </summary>
    public int MediaPackets { get; private set; }

    internal SdPlaybackSession(TcpClient client, NetworkStream stream, ushort width, ushort height)
    {
        this.client = client;
        this.stream = stream;
        Width = width;
        Height = height;
    }

    public static async Task<SdPlaybackSession> OpenAsync(
        string host, int port, uint deviceId, uint ticket,
        uint segmentId, uint startRaw, CancellationToken cancellationToken = default)
    {
        if (ticket == 0) throw new InvalidOperationException("Camera is not authenticated");

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        var ct = deadline.Token;

        var client = new TcpClient();
        try
        {
            await client.ConnectAsync(host, port, ct);
            var stream = client.GetStream();

            var request = new byte[64];
            Put32(request, 0, 363);
            Put32(request, 4, deviceId);
            Put32(request, 8, ticket);
            Put32(request, 12, segmentId);      // listing Id, NOT StartRaw
            ClockFields(request, 16, startRaw); // seek position inside the segment
            request[23] = 0;                    // event type; firmware ignores it

            await stream.WriteAsync(request, ct);

            var reply = new byte[64];
            await stream.ReadExactlyAsync(reply, ct);
            uint command = Get32(reply, 0);
            int result = Get32Signed(reply, 4);
            if (command != 463)
                throw new InvalidDataException($"Unsupported playback response {command}");
            if (result != 1000)
                throw new InvalidDataException($"Playback rejected ({result})");

            ushort width = Get16(reply, 14);
            ushort height = Get16(reply, 16);

            var play = new byte[32];
            Put32(play, 0, 364);
            // This 2016 firmware only starts streaming when the play-mode byte
            // is 2. The Android SDK writes 1, which the camera answers by
            // closing the connection. Verified against the hardware.
            play[4] = 2;
            Put16(play, 6, 200);
            Put32(play, 12, segmentId);
            ClockFields(play, 16, startRaw);

            await stream.WriteAsync(play, ct);
            LogUtils.debug($"[PLAYBACK] stream opened segment={segmentId} at={SdCardProtocol.WallTime(startRaw)} {width}x{height}");
            return new SdPlaybackSession(client, stream, width, height);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Reads media frames until the camera closes the connection or the
    /// cancellation token fires. An idle period longer than
    /// <paramref name="idleTimeout"/> aborts the read with an IOException; the
    /// export loop treats that as the end of a 400-packet chunk.
    /// </summary>
    public async IAsyncEnumerable<SdPlaybackFrame> ReadFramesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default,
        TimeSpan? idleTimeout = null)
    {
        var packets = new ByteQueue();
        var frames = new ByteQueue();
        var read = new byte[64 * 1024];
        var idleLimit = idleTimeout ?? TimeSpan.FromSeconds(15);

        while (!cancellationToken.IsCancellationRequested)
        {
            int n;
            using (var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                idle.CancelAfter(idleLimit);
                try
                {
                    n = await stream.ReadAsync(read, idle.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new IOException("Playback stream stalled");
                }
                catch (IOException)
                {
                    yield break;
                }
                catch (ObjectDisposedException)
                {
                    yield break;
                }
            }

            if (n <= 0) yield break;
            packets.Append(read.AsSpan(0, n));

            while (true)
            {
                var span = packets.Span;
                if (span.Length < 8) break;
                byte type = span[0];
                uint length = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(4, 4));
                if (length > 4 * 1024 * 1024)
                    throw new InvalidDataException($"Unexpected playback packet length {length}");
                if (span.Length < 8L + length) break;

                if (type == 0x7f)
                {
                    MediaPackets++;
                    frames.Append(span.Slice(8, (int)length));
                }
                else if (type != 0x6f)
                    LogUtils.debug($"[PLAYBACK] packet type 0x{type:x2} length {length}");

                packets.Consume(8 + (int)length);
            }

            while (true)
            {
                var span = frames.Span;
                if (span.Length < 22) break;
                ushort type = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(4, 2));
                ulong timestamp = BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(10, 8));
                uint length = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(18, 4));
                if (length > 16 * 1024 * 1024)
                    throw new InvalidDataException($"Unexpected playback frame length {length}");
                if (span.Length < 22L + length) break;

                var data = span.Slice(22, (int)length).ToArray();
                ushort width = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(6, 2));
                ushort height = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(8, 2));
                frames.Consume(22 + (int)length);
                yield return new SdPlaybackFrame(type, width, height, timestamp, data);
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        try { stream?.Dispose(); } catch { }
        try { client?.Dispose(); } catch { }
        return ValueTask.CompletedTask;
    }

    static void ClockFields(byte[] b, int offset, uint raw)
    {
        var t = DateTime.UnixEpoch.AddSeconds(raw);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(offset), (ushort)t.Year);
        b[offset + 2] = (byte)t.Month;
        b[offset + 3] = (byte)t.Day;
        b[offset + 4] = (byte)t.Hour;
        b[offset + 5] = (byte)t.Minute;
        b[offset + 6] = (byte)t.Second;
    }

    static void Put32(byte[] b, int o, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o), v);
    static void Put16(byte[] b, int o, ushort v) => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(o), v);
    static uint Get32(byte[] b, int o) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o));
    static int Get32Signed(byte[] b, int o) => BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(o));
    static ushort Get16(byte[] b, int o) => BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(o));
}

/// <summary>
/// IMA ADPCM (4-bit, WAV block layout) used by the camera's audio frames.
/// Each 256-byte block carries a 4-byte header (16-bit predictor, 8-bit step
/// index, 1 reserved byte) followed by 504 nibbles, and decodes to 505
/// samples at 8 kHz mono. Verified sample-for-sample against ffmpeg.
/// </summary>
public static class ImaAdpcm
{
    public const int SampleRate = 8000;
    public const int BlockBytes = 256;
    public const int BlockSamples = 505;

    private static readonly int[] StepTable =
    {
        7, 8, 9, 10, 11, 12, 13, 14, 16, 17, 19, 21, 23, 25, 28, 31,
        34, 37, 41, 45, 50, 55, 60, 66, 73, 80, 88, 97, 107, 118, 130, 143,
        157, 173, 190, 209, 230, 253, 279, 307, 337, 371, 408, 449, 494, 544,
        598, 658, 724, 796, 876, 963, 1060, 1166, 1282, 1411, 1552, 1707,
        1878, 2066, 2272, 2499, 2749, 3024, 3327, 3660, 4026, 4428, 4871,
        5358, 5894, 6484, 7132, 7845, 8630, 9493, 10442, 11487, 12635,
        13899, 15289, 16818, 18500, 20350, 22385, 24623, 27086, 29794, 32767
    };

    private static readonly int[] IndexTable =
    {
        -1, -1, -1, -1, 2, 4, 6, 8, -1, -1, -1, -1, 2, 4, 6, 8
    };

    /// <summary>Decodes one 256-byte IMA block into 505 little-endian PCM samples.</summary>
    public static byte[] DecodeBlock(ReadOnlySpan<byte> block)
    {
        if (block.Length < 4) return Array.Empty<byte>();

        int predictor = BinaryPrimitives.ReadInt16LittleEndian(block);
        int index = block[2];
        if (index > 88) index = 88;

        var pcm = new byte[BlockSamples * 2];
        int write = 0;

        void Emit(int sample)
        {
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(write), (short)sample);
            write += 2;
        }

        // The block header's predictor is the first output sample.
        Emit(predictor);

        for (int i = 4; i < block.Length && write < pcm.Length; i++)
        {
            int value = block[i];
            for (int half = 0; half < 2 && write < pcm.Length; half++)
            {
                int nibble = half == 0 ? value & 0x0f : value >> 4;
                int step = StepTable[index];
                int diff = step >> 3;
                if ((nibble & 1) != 0) diff += step >> 2;
                if ((nibble & 2) != 0) diff += step >> 1;
                if ((nibble & 4) != 0) diff += step;

                predictor = (nibble & 8) != 0 ? predictor - diff : predictor + diff;
                if (predictor > short.MaxValue) predictor = short.MaxValue;
                else if (predictor < short.MinValue) predictor = short.MinValue;

                index += IndexTable[nibble];
                if (index < 0) index = 0;
                else if (index > 88) index = 88;

                Emit(predictor);
            }
        }

        return pcm;
    }
}

/// <summary>Growable byte buffer with cheap FIFO consumption and periodic compaction.</summary>
internal sealed class ByteQueue
{
    private byte[] data = new byte[8192];
    private int start;
    private int end;

    public int Length => end - start;

    public ReadOnlySpan<byte> Span => data.AsSpan(start, end - start);

    public void Append(ReadOnlySpan<byte> source)
    {
        EnsureWritable(source.Length);
        source.CopyTo(data.AsSpan(end));
        end += source.Length;
    }

    public void Consume(int count)
    {
        start += count;
        if (start >= end)
        {
            start = 0;
            end = 0;
        }
        else if (start > 64 * 1024 && start > data.Length / 2)
        {
            Buffer.BlockCopy(data, start, data, 0, end - start);
            end -= start;
            start = 0;
        }
    }

    private void EnsureWritable(int needed)
    {
        if (data.Length - end >= needed) return;

        int live = end - start;
        if (start > 0 && live + needed <= data.Length)
        {
            Buffer.BlockCopy(data, start, data, 0, live);
            start = 0;
            end = live;
            return;
        }

        int required = live + needed;
        int size = data.Length;
        while (size < required) size *= 2;
        var grown = new byte[size];
        Buffer.BlockCopy(data, start, grown, 0, live);
        data = grown;
        start = 0;
        end = live;
    }
}

