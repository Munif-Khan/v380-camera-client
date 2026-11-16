using Android.Graphics;
using Android.Media;
using Java.Nio;
using V380Decoder.src;

namespace V380Cameras.Media;

/// <summary>
/// Hardware H.264/H.265 decoder. MediaCodec decodes directly into YUV_420_888
/// buffers (GetOutputImage), converted to NV21 and compressed to JPEG for
/// the UI's snapshot poller.
///
/// Running buffer-to-buffer avoids device-specific driver crashes in
/// ImageReader surface plane creation (common on MediaTek / OnePlus chipsets).
/// </summary>
public sealed class VideoDecoder : IDisposable
{
    private readonly object gate = new();
    private readonly Action<byte[]> onJpeg;
    private readonly int targetFps;
    private readonly int jpegQuality;
    private readonly int downscale;

    private MediaCodec? codec;
    private int width;
    private int height;
    private bool hevc;
    private byte[]? cachedSps;
    private byte[]? cachedPps;
    private long frameIndex;
    private long lastEncodeTicks;
    private bool disposed;

    public VideoDecoder(int width, int height, bool hevc, int targetFps, int jpegQuality, Action<byte[]> onJpeg)
    {
        this.width = Math.Max(2, width);
        this.height = Math.Max(2, height);
        this.hevc = hevc;
        this.targetFps = Math.Clamp(targetFps, 1, 15);
        this.jpegQuality = Math.Clamp(jpegQuality, 30, 95);
        this.onJpeg = onJpeg;
        // 1080p and above are halved: the phone screen shows ~360 px wide, so
        // a 540p JPEG is both sharper than needed and several times cheaper.
        downscale = this.width > 960 ? 2 : 1;
    }

    /// <summary>True once a codec instance has been configured and started.</summary>
    public bool IsConfigured { get { lock (gate) return codec != null; } }

    /// <summary>
    /// Feeds one access unit. The first key frame configures the decoder from
    /// its SPS/PPS; a changed parameter set rebuilds it (resolution changes,
    /// or a different camera codec profile).
    /// </summary>
    public void Push(byte[] accessUnit, bool keyFrame)
    {
        lock (gate)
        {
            if (disposed || accessUnit.Length == 0) return;

            if (codec == null)
            {
                if (!keyFrame || !ConfigureLocked(accessUnit)) return;
            }
            else if (keyFrame && ParametersChanged(accessUnit))
            {
                RebuildLocked(accessUnit);
                if (codec == null) return;
            }

            try
            {
                int inputIndex = codec!.DequeueInputBuffer(4_000);
                if (inputIndex < 0) return; // codec is busy; drop this frame
                var buffer = codec.GetInputBuffer(inputIndex);
                if (buffer == null) return;

                buffer.Clear();
                buffer.Put(accessUnit);
                long pts = frameIndex++ * (1_000_000L / targetFps);
                codec.QueueInputBuffer(inputIndex, 0, accessUnit.Length, pts,
                    keyFrame ? MediaCodecBufferFlags.KeyFrame : 0);
                DrainLocked();
            }
            catch (Exception ex)
            {
                LogUtils.debug($"[DECODE] push error: {ex.Message}");
                // A decoder that throws is usually wedged; rebuild on the next
                // key frame rather than tearing the whole session down.
                ReleaseLocked();
            }
        }
    }

    private void DrainLocked()
    {
        if (codec == null) return;
        var info = new MediaCodec.BufferInfo();
        for (int i = 0; i < 8; i++)
        {
            int index = codec.DequeueOutputBuffer(info, 0);
            if (index >= 0)
            {
                try
                {
                    long now = Environment.TickCount64;
                    long interval = 1000L / targetFps;
                    if (now - lastEncodeTicks >= interval)
                    {
                        using var image = codec.GetOutputImage(index);
                        if (image != null)
                        {
                            ProcessImage(image);
                            lastEncodeTicks = now;
                        }
                    }
                }
                catch (Exception ex)
                {
                    LogUtils.debug($"[DECODE] process image error: {ex.Message}");
                }
                finally
                {
                    try { codec.ReleaseOutputBuffer(index, false); } catch { }
                }
            }
            else if (index == (int)MediaCodecInfoState.OutputFormatChanged)
            {
                var newFormat = codec.OutputFormat;
                if (newFormat != null)
                {
                    if (newFormat.ContainsKey(MediaFormat.KeyWidth))
                        width = newFormat.GetInteger(MediaFormat.KeyWidth);
                    if (newFormat.ContainsKey(MediaFormat.KeyHeight))
                        height = newFormat.GetInteger(MediaFormat.KeyHeight);
                }
            }
            else
            {
                break;
            }
        }
    }

    private bool ConfigureLocked(byte[] keyFrame)
    {
        if (!TrySplitParameterSets(keyFrame, hevc, out var sps, out var pps, out var vps)) return false;

        StopLocked();
        cachedSps = sps;
        cachedPps = pps;

        string mime = hevc ? MediaFormat.MimetypeVideoHevc : MediaFormat.MimetypeVideoAvc;
        var format = MediaFormat.CreateVideoFormat(mime, width, height)!
            ?? throw new InvalidOperationException("Could not create the video format");
        format.SetInteger(MediaFormat.KeyMaxInputSize, 2 * 1024 * 1024);
        format.SetInteger(MediaFormat.KeyColorFormat, (int)MediaCodecCapabilities.Formatyuv420flexible);

        if (hevc)
        {
            var csd = new byte[vps!.Length + sps!.Length + pps!.Length];
            int at = 0;
            foreach (var part in new[] { vps, sps, pps })
            {
                Array.Copy(part, 0, csd, at, part.Length);
                at += part.Length;
            }
            format.SetByteBuffer("csd-0", ByteBuffer.Wrap(csd));
        }
        else
        {
            format.SetByteBuffer("csd-0", ByteBuffer.Wrap(sps!));
            format.SetByteBuffer("csd-1", ByteBuffer.Wrap(pps!));
        }

        codec = MediaCodec.CreateDecoderByType(mime);
        codec.Configure(format, null, null, (MediaCodecConfigFlags)0);
        codec.Start();
        return true;
    }

    private bool ParametersChanged(byte[] keyFrame)
    {
        if (!TrySplitParameterSets(keyFrame, hevc, out var sps, out var pps, out _)) return false;
        return !Same(cachedSps, sps!) || !Same(cachedPps, pps!);
    }

    private void RebuildLocked(byte[] keyFrame)
    {
        StopLocked();
        ConfigureLocked(keyFrame);
    }

    private static bool Same(byte[]? a, byte[] b)
    {
        if (a == null || a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }

    private void ReleaseLocked()
    {
        try { codec?.Stop(); } catch { }
        try { codec?.Release(); } catch { }
        try { codec?.Dispose(); } catch { }
        codec = null;
    }

    private void StopLocked()
    {
        ReleaseLocked();
        cachedSps = null;
        cachedPps = null;
    }

    private void ProcessImage(Image image)
    {
        try
        {
            var planes = image.GetPlanes();
            if (planes == null || planes.Length < 3) return;

            if (!TryCopyPlane(planes[0].Buffer, out var yPlane)) return;
            if (!TryCopyPlane(planes[1].Buffer, out var uPlane)) return;
            if (!TryCopyPlane(planes[2].Buffer, out var vPlane)) return;

            int factor = downscale;
            int outWidth = Math.Max(2, (image.Width / factor) & ~1);
            int outHeight = Math.Max(2, (image.Height / factor) & ~1);
            var nv21 = ToNv21(image.Width, image.Height, outWidth, outHeight,
                yPlane, planes[0].RowStride, planes[0].PixelStride,
                uPlane, planes[1].RowStride, planes[1].PixelStride,
                vPlane, planes[2].RowStride, planes[2].PixelStride);
            if (nv21 == null) return;

            using var jpeg = new MemoryStream(nv21.Length / 6);
            using var yuv = new YuvImage(nv21, ImageFormatType.Nv21, outWidth, outHeight, null);
            var rect = new Rect(0, 0, outWidth, outHeight);
            if (!yuv.CompressToJpeg(rect, jpegQuality, jpeg) || jpeg.Length == 0) return;
            onJpeg(jpeg.ToArray());
        }
        catch (Exception ex)
        {
            LogUtils.debug($"[DECODE] error compressing jpeg: {ex.Message}");
        }
    }

    private static bool TryCopyPlane(ByteBuffer? buffer, out byte[] data)
    {
        data = Array.Empty<byte>();
        if (buffer == null) return false;
        int size = buffer.Remaining();
        if (size <= 0) return false;
        data = new byte[size];
        var duplicate = buffer.Duplicate();
        duplicate?.Get(data);
        return true;
    }

    /// <summary>
    /// Packs CbCr into the single-plane NV21 layout YuvImage wants. Works for
    /// both planar (pixel stride 1) and semi-planar (stride 2) codec output.
    /// </summary>
    private static byte[]? ToNv21(
        int srcWidth, int srcHeight, int outWidth, int outHeight,
        byte[] y, int yRow, int yPixel,
        byte[] u, int uRow, int uPixel,
        byte[] v, int vRow, int vPixel)
    {
        var nv21 = new byte[outWidth * outHeight * 3 / 2];

        for (int row = 0; row < outHeight; row++)
        {
            int srcRow = (int)((long)row * srcHeight / outHeight);
            int at = srcRow * yRow;
            int dst = row * outWidth;
            for (int col = 0; col < outWidth; col++)
            {
                int sx = (int)((long)col * srcWidth / outWidth);
                int index = at + sx * yPixel;
                if (index >= y.Length) return null;
                nv21[dst + col] = y[index];
            }
        }

        int chromaWidth = outWidth / 2;
        int chromaHeight = outHeight / 2;
        int srcChromaHeight = srcHeight / 2;
        int srcChromaWidth = srcWidth / 2;
        int offset = outWidth * outHeight;
        for (int row = 0; row < chromaHeight; row++)
        {
            int srcRow = (int)((long)row * srcChromaHeight / chromaHeight);
            int uAt = srcRow * uRow;
            int vAt = srcRow * vRow;
            for (int col = 0; col < chromaWidth; col++)
            {
                int sx = (int)((long)col * srcChromaWidth / chromaWidth);
                int uIndex = uAt + sx * uPixel;
                int vIndex = vAt + sx * vPixel;
                if (uIndex >= u.Length || vIndex >= v.Length) return null;
                nv21[offset++] = v[vIndex]; // NV21 stores V before U
                nv21[offset++] = u[uIndex];
            }
        }

        return nv21;
    }

    private static bool TrySplitParameterSets(
        byte[] accessUnit, bool hevc,
        out byte[]? sps, out byte[]? pps, out byte[]? vps)
        => NalUnits.TrySplitParameterSets(accessUnit, hevc, out sps, out pps, out vps);

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            StopLocked();
        }
    }
}
