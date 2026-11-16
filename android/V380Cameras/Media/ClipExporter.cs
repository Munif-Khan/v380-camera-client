using Android.Media;
using Java.Nio;
using V380Decoder.src;

namespace V380Cameras.Media;

/// <summary>
/// Turns a camera SD-card recording into an MP4 file.
///
/// The camera serves at most 400 media packets (about 8-10 s) per TCP
/// connection, so a clip is assembled from consecutive chunks: each chunk opens
/// a fresh connection and asks the camera to seek to the last received time
/// plus one second. The camera starts a seek at the preceding key frame, so
/// chunks overlap; duplicate frames are dropped by timestamp.
///
/// Video is copied straight into the MP4 without re-encoding: the camera's
/// H.264/H.265 access units are handed to <see cref="MediaMuxer"/>, which is
/// also what keeps the phone cool while a long clip downloads. Audio is not
/// included yet (see README: "known gaps").
/// </summary>
public static class ClipExporter
{
    // This 2016 camera answers one playback session at a time. Serialise clip
    // exports so two requests cannot fight over it.
    private static readonly SemaphoreSlim Gate = new(1, 1);

    // The firmware stops after this many 0x7f packets per connection.
    private const int PacketsPerChunk = 400;

    public static async Task ExportAsync(
        string host, int port, uint deviceId, uint ticket,
        uint segmentId, uint startRaw, int durationSeconds,
        string outputPath, CancellationToken cancellationToken)
    {
        if (!await Gate.WaitAsync(0, cancellationToken))
            throw new InvalidOperationException("Another SD clip is already streaming from this camera");

        try
        {
            await ExportCoreAsync(host, port, deviceId, ticket, segmentId, startRaw,
                Math.Clamp(durationSeconds, 1, 600), outputPath, cancellationToken);
        }
        catch
        {
            TryDelete(outputPath);
            throw;
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task ExportCoreAsync(
        string host, int port, uint deviceId, uint ticket,
        uint segmentId, uint startRaw, int durationSeconds,
        string outputPath, CancellationToken cancellationToken)
    {
        MediaMuxer? muxer = null;
        int track = -1;
        long samples = 0;
        bool hevc = false;

        ulong stopAtMs = (ulong)startRaw * 1000UL + (ulong)durationSeconds * 1000UL;
        var wallDeadline = DateTime.UtcNow.AddSeconds(durationSeconds * 3 + 60);
        ulong baselineMs = 0;
        bool haveBaseline = false;
        ulong lastVideoMs = 0;
        ulong seekRaw = startRaw;
        bool reachedEnd = false;

        try
        {
            while (!cancellationToken.IsCancellationRequested && DateTime.UtcNow <= wallDeadline)
            {
                bool progressed = false;

                SdPlaybackSession? session = null;
                try
                {
                    session = await SdPlaybackSession.OpenAsync(
                        host, port, deviceId, ticket, segmentId, (uint)seekRaw, cancellationToken);

                    await foreach (var frame in session.ReadFramesAsync(cancellationToken, TimeSpan.FromSeconds(2.5)))
                    {
                        if (frame.IsVideo)
                        {
                            if (haveBaseline && frame.TimestampMs <= lastVideoMs) continue;
                            bool first = !haveBaseline;
                            if (first)
                            {
                                baselineMs = frame.TimestampMs;
                                haveBaseline = true;
                            }

                            // MediaMuxer refuses a track that does not begin on a
                            // key frame, so the parameter sets from the first
                            // key frame double as the track's csd.
                            if (muxer == null)
                            {
                                if (frame.Type != SdPlaybackFrame.VideoKeyFrame) continue;
                                hevc = !LooksLikeH264(frame.Data);
                                if (!NalUnits.TrySplitParameterSets(frame.Data, hevc, out var sps, out var pps, out var vps))
                                    throw new InvalidDataException("Clip did not start with SPS/PPS");

                                int w = frame.Width > 1 ? frame.Width : 1280;
                                int h = frame.Height > 1 ? frame.Height : 720;
                                var format = MediaFormat.CreateVideoFormat(
                                    hevc ? MediaFormat.MimetypeVideoHevc : MediaFormat.MimetypeVideoAvc, w, h)!;
                                if (hevc)
                                {
                                    var csd = new byte[vps!.Length + sps!.Length + pps!.Length];
                                    int at = 0;
                                    foreach (var part in new[] { vps, sps, pps })
                                    {
                                        Array.Copy(part, 0, csd, at, part.Length);
                                        at += part.Length;
                                    }
                                    format.SetByteBuffer("csd-0", ByteBuffer.Wrap(csd)!);
                                }
                                else
                                {
                                    format.SetByteBuffer("csd-0", ByteBuffer.Wrap(sps!)!);
                                    format.SetByteBuffer("csd-1", ByteBuffer.Wrap(pps!)!);
                                }

                                muxer = new MediaMuxer(outputPath, MuxerOutputType.Mpeg4);
                                track = muxer.AddTrack(format);
                                muxer.Start();
                            }

                            lastVideoMs = frame.TimestampMs;
                            // MediaMuxer expects AVCC (length-prefixed) NAL units,
                            // not Annex-B (start-code delimited) that the camera sends.
                            var avcc = NalUnits.AnnexBToAvcc(frame.Data);
                            var info = new MediaCodec.BufferInfo
                            {
                                Offset = 0,
                                Size = avcc.Length,
                                PresentationTimeUs = (long)(frame.TimestampMs - baselineMs) * 1000L,
                                Flags = frame.Type == SdPlaybackFrame.VideoKeyFrame
                                    ? MediaCodecBufferFlags.KeyFrame
                                    : 0
                            };
                            muxer!.WriteSampleData(track, ByteBuffer.Wrap(avcc)!, info);
                            samples++;
                            progressed = true;

                            if (frame.TimestampMs >= stopAtMs)
                            {
                                reachedEnd = true;
                                break;
                            }
                        }

                        if (session.MediaPackets >= PacketsPerChunk) break;
                    }
                }
                catch (IOException ex) when (!cancellationToken.IsCancellationRequested)
                {
                    LogUtils.debug($"[CLIP] chunk ended: {ex.Message}");
                }
                catch (InvalidDataException ex) when (!cancellationToken.IsCancellationRequested)
                {
                    LogUtils.debug($"[CLIP] chunk rejected: {ex.Message}");
                }
                finally
                {
                    if (session != null) await session.DisposeAsync();
                }

                if (reachedEnd || !progressed) break;

                // Ask for the exact second of the last received frame. The camera
                // rewinds to the key frame at or before that time, so the next
                // chunk always overlaps instead of skipping frames.
                seekRaw = lastVideoMs / 1000UL;
                if (seekRaw * 1000UL > stopAtMs) break;

                // Give the camera a moment to release the previous playback session.
                try { await Task.Delay(150, cancellationToken); }
                catch (OperationCanceledException) { break; }
            }
        }
        finally
        {
            if (muxer != null)
            {
                try { muxer.Stop(); } catch (Exception) { /* nothing usable was written */ }
                try { muxer.Release(); } catch (Exception) { }
                try { muxer.Dispose(); } catch (Exception) { }
            }
        }

        if (samples == 0)
            throw new InvalidOperationException("The camera did not send any video for this clip");
    }

    /// <summary>
    /// The camera only speaks one codec per session, but the SD stream does not
    /// announce which. An H.264 SPS has nal_unit_type 7, i.e. low five bits 111.
    /// </summary>
    private static bool LooksLikeH264(byte[] accessUnit)
    {
        int i = 0;
        while (i + 4 < accessUnit.Length)
        {
            if (accessUnit[i] == 0 && accessUnit[i + 1] == 0 && accessUnit[i + 2] == 1)
            {
                int type = accessUnit[i + 3] & 0x1F;
                return type is 7 or 8 or 5;
            }
            i++;
        }
        return true;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (Exception) { }
    }
}
