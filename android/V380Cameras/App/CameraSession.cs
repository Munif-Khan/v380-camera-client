using V380Cameras.Media;
using V380Decoder.src;

namespace V380Cameras.App;

/// <summary>
/// One running camera: authenticates, keeps a live stream open, decodes the
/// newest frame to JPEG for the UI's snapshot poller, and exposes the SD-card
/// and alarm channels.
///
/// The connection is supervised rather than trusted: the phone changes Wi-Fi
/// networks, the camera reboots, and domestic routers drop idle NAT entries.
/// A stall watchdog cancels a silent stream after 20 s and the worker retries
/// with a capped backoff, which mirrors what the desktop worker does.
/// </summary>
public sealed class CameraSession : IAsyncDisposable
{
    private const int StallSeconds = 20;
    private const int JpegFreshSeconds = 10;
    private const int TargetFps = 5;
    private const int JpegQuality = 70;

    private readonly CameraConfig config;
    private readonly AudioFanout audio = new();
    private readonly object gate = new();

    private CancellationTokenSource? cts;
    private Task? worker;
    private Task? watchdog;
    private volatile V380Client? client;
    private volatile VideoDecoder? decoder;
    private volatile CancellationTokenSource? attemptCts;
    private volatile int activeQuality = -1;

    private volatile byte[]? latestJpeg;
    private long latestJpegTicks;
    private long lastFrameTicks;
    private long frames;
    private int restarts;
    private int decoderFailures;
    private volatile bool decoderBlocked;
    private volatile string state = "starting";
    private volatile string? lastError;

    public CameraSession(CameraConfig config)
    {
        this.config = config.Clone();
    }

    public CameraConfig Config => config;
    public AudioFanout Audio => audio;
    public string State => state;
    public string? LastError => lastError;
    public long Frames => Interlocked.Read(ref frames);
    public int Restarts => Volatile.Read(ref restarts);

    public double? FrameAgeSeconds
    {
        get
        {
            long ticks = Interlocked.Read(ref lastFrameTicks);
            if (ticks == 0) return null;
            return Math.Round((Environment.TickCount64 - ticks) / 1000.0, 1);
        }
    }

    public bool VideoFresh => latestJpeg != null && (Environment.TickCount64 - Interlocked.Read(ref latestJpegTicks)) < JpegFreshSeconds * 1000;

    /// <summary>
    /// Records the session state and mirrors it to logcat. The page and the
    /// manager already see these values through /health; the log copy is what
    /// makes an on-device failure diagnosable without a debug build
    /// (<c>adb logcat -s V380</c>).
    /// </summary>
    private void SetState(string next, string? error = null)
    {
        bool changed = !string.Equals(state, next, StringComparison.Ordinal);
        state = next;
        if (error != null) lastError = error;

        if (changed || error != null)
            Android.Util.Log.Info("V380", $"{config.Name}: {next}" + (error == null ? "" : " - " + error));
    }

    public void Start()
    {
        lock (gate)
        {
            if (worker is { IsCompleted: false }) return;

            cts?.Dispose();
            cts = new CancellationTokenSource();
            var token = cts.Token;

            Interlocked.Exchange(ref frames, 0);
            Volatile.Write(ref restarts, 0);
            Interlocked.Exchange(ref lastFrameTicks, 0);
            SetState("starting");
            lastError = null;
            activeQuality = config.IsHd ? 1 : 0;

            worker = Task.Run(() => RunAsync(token));
            watchdog = Task.Run(() => WatchdogAsync(token));
        }
    }

    public void Stop()
    {
        lock (gate)
        {
            try { cts?.Cancel(); } catch (Exception) { }
        }

        // A blocked socket read only unblocks when the socket closes.
        try { client?.Dispose(); } catch (Exception) { }

        VideoDecoder? d;
        lock (gate) { d = decoder; decoder = null; }
        try { d?.Dispose(); } catch (Exception) { }

        SetState("stopped");
    }

    private async Task RunAsync(CancellationToken stop)
    {
        int failures = 0;

        while (!stop.IsCancellationRequested)
        {
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(stop);
            attemptCts = attempt;

            V380Client? connection = null;
            try
            {
                SetState("connecting");
                int q = activeQuality >= 0 ? activeQuality : (config.IsHd ? 1 : 0);
                connection = new V380Client(
                    config.Host, config.Port, (uint)config.DeviceId,
                    config.Username, config.Password,
                    SourceStream.Lan, OutputMode.Video,
                    q);

                connection.VideoReceived += OnVideo;
                connection.AudioReceived += OnAudio;
                client = connection;

                SetState("authenticating");
                int auth = await Task.Run(connection.GetAuthTicket, stop);
                if (stop.IsCancellationRequested) break;

                if (auth < 0)
                {
                    // A rejected password will not fix itself by retrying fast.
                    SetState("error", "The camera rejected this username or password.");
                    await Task.Delay(TimeSpan.FromSeconds(30), stop);
                    continue;
                }
                if (auth == 0) throw new IOException("The camera did not answer the login request");

                if (!connection.StreamLogin()) throw new IOException("The camera refused the stream login");
                if (!connection.StartStream()) throw new IOException("The camera refused to start the stream");
                if (stop.IsCancellationRequested) break;

                SetState("live");
                lastError = null;
                failures = 0;
                decoderFailures = 0;
                decoderBlocked = false;
                Interlocked.Exchange(ref lastFrameTicks, Environment.TickCount64);

                // ReceiveFrames blocks until the socket closes or the attempt
                // token fires; it is the connection's whole lifetime.
                await Task.Run(() => connection.ReceiveFrames(attempt.Token), attempt.Token);
                if (stop.IsCancellationRequested) break;

                if (activeQuality == 1 && Interlocked.Read(ref frames) == 0)
                {
                    Android.Util.Log.Warn("V380", $"{config.Name}: HD stream closed with 0 frames; falling back to SD mode");
                    activeQuality = 0;
                }

                throw new IOException("The camera closed the stream");
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                if (stop.IsCancellationRequested) break;
                SetState("reconnecting", ex.Message);
                Interlocked.Increment(ref restarts);
                failures++;
            }
            finally
            {
                try { connection?.Dispose(); } catch (Exception) { }
                if (ReferenceEquals(client, connection)) client = null;
                attemptCts = null;
            }

            if (stop.IsCancellationRequested) break;

            var backoff = TimeSpan.FromSeconds(Math.Min(30, 1 << Math.Min(failures, 5)));
            try { await Task.Delay(backoff, stop); }
            catch (OperationCanceledException) { break; }
        }

        SetState("stopped");
    }

    /// <summary>Cancels a stream that has gone silent, so RunAsync reconnects.</summary>
    private async Task WatchdogAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try { await Task.Delay(2000, stop); }
            catch (OperationCanceledException) { return; }

            if (state != "live") continue;

            long last = Interlocked.Read(ref lastFrameTicks);
            if (last == 0) continue;

            // If the camera is in HD mode but produces 0 frames within 6 seconds, fall back to SD
            if (activeQuality == 1 && Interlocked.Read(ref frames) == 0 && (Environment.TickCount64 - last) > 6000)
            {
                Android.Util.Log.Warn("V380", $"{config.Name}: HD stream delivered 0 frames in 6s; falling back to SD mode");
                activeQuality = 0;
                SetState("reconnecting", "HD stream unsupported; falling back to SD");
                try { attemptCts?.Cancel(); } catch (Exception) { }
                try { client?.Dispose(); } catch (Exception) { }
                continue;
            }

            if (Environment.TickCount64 - last <= StallSeconds * 1000) continue;

            SetState("reconnecting", "No video for " + StallSeconds + " s");
            try { attemptCts?.Cancel(); } catch (Exception) { }
            try { client?.Dispose(); } catch (Exception) { }
        }
    }

    private void OnVideo(byte[] accessUnit, bool keyFrame, VideoCodec codec)
    {
        Interlocked.Exchange(ref lastFrameTicks, Environment.TickCount64);
        Interlocked.Increment(ref frames);

        var active = decoder;
        if (active == null)
        {
            if (!keyFrame || decoderBlocked) return;

            // Both the codec and the frame size are only trustworthy once the
            // first key frame has arrived, so the decoder is built lazily.
            try
            {
                int width = Math.Max(2, client?.Width ?? 1280);
                int height = Math.Max(2, client?.Height ?? 720);
                active = new VideoDecoder(
                    width,
                    height,
                    codec == VideoCodec.H265,
                    TargetFps, JpegQuality,
                    jpeg =>
                    {
                        if (latestJpeg == null)
                            Android.Util.Log.Info("V380", $"{config.Name}: first JPEG, {jpeg.Length} bytes");
                        latestJpeg = jpeg;
                        Interlocked.Exchange(ref latestJpegTicks, Environment.TickCount64);
                    });
                lock (gate) decoder = active;
                decoderFailures = 0;
                Android.Util.Log.Info("V380",
                    $"{config.Name}: decoder started, {width}x{height}, " +
                    (codec == VideoCodec.H265 ? "H.265" : "H.264"));
            }
            catch (Exception ex)
            {
                // An unsupported codec would otherwise throw on every key frame
                // for the life of the connection.
                SetState(state, "Video decoder unavailable: " + ex.Message);
                if (++decoderFailures >= 3) decoderBlocked = true;
                return;
            }
        }

        try
        {
            active.Push(accessUnit, keyFrame);
        }
        catch (Exception ex)
        {
            // A wedged decoder must not take the camera socket down with it.
            SetState(state, "Video decode failed: " + ex.Message);
            if (++decoderFailures >= 3) decoderBlocked = true;
            lock (gate)
            {
                if (ReferenceEquals(decoder, active)) decoder = null;
            }
            try { active.Dispose(); } catch (Exception) { }
        }
    }

    private void OnAudio(byte[] alaw)
    {
        if (alaw.Length == 0) return;
        try { audio.Publish(AlawCodec.Decode(alaw)); }
        catch (Exception) { }
    }

    public bool TryGetSnapshot(out byte[] jpeg)
    {
        var current = latestJpeg;
        long ticks = Interlocked.Read(ref latestJpegTicks);
        if (current == null || Environment.TickCount64 - ticks >= JpegFreshSeconds * 1000)
        {
            jpeg = Array.Empty<byte>();
            return false;
        }
        jpeg = current;
        return true;
    }

    private V380Client Connected()
    {
        var current = client;
        if (current == null || current.Ticket == 0)
            throw new InvalidOperationException("Camera is not connected");
        return current;
    }

    public Task<SdRecordingList> ListRecordingsAsync(DateOnly date, CancellationToken ct)
        => Connected().ListRecordingsAsync(date, ct);

    public Task WriteAlarmConfigAsync(AlarmConfig updated, CancellationToken ct)
        => Connected().WriteAlarmConfigAsync(updated, ct);

    public async Task<AlarmConfig> ReadAlarmConfigAsync(CancellationToken ct)
        => await Connected().ReadAlarmConfigAsync(ct);

    public Task ExportClipAsync(uint segmentId, uint startRaw, int durationSeconds, string outputPath, CancellationToken ct)
    {
        var connection = Connected();
        return ClipExporter.ExportAsync(
            config.Host, config.Port, (uint)config.DeviceId, connection.Ticket,
            segmentId, startRaw, durationSeconds, outputPath, ct);
    }

    public bool Ptz(string direction)
    {
        var current = client;
        if (current == null) return false;
        return direction switch
        {
            "right" => current.PtzRight(),
            "left" => current.PtzLeft(),
            "up" => current.PtzUp(),
            "down" => current.PtzDown(),
            "stop" => current.PtzStop(),
            _ => false
        };
    }

    public bool Light(string mode)
    {
        var current = client;
        if (current == null) return false;
        return mode switch
        {
            "on" => current.LightOn(),
            "off" => current.LightOff(),
            "auto" => current.LightAuto(),
            _ => false
        };
    }

    public bool Image(string mode)
    {
        var current = client;
        if (current == null) return false;
        return mode switch
        {
            "color" => current.ImageColor(),
            "bw" => current.ImageBW(),
            "auto" => current.ImageAuto(),
            "flip" => current.ImageFlip(),
            _ => false
        };
    }

    public async ValueTask DisposeAsync()
    {
        Stop();
        var w = worker;
        var d = watchdog;
        if (w != null)
        {
            try { await w.WaitAsync(TimeSpan.FromSeconds(5)); } catch (Exception) { }
        }
        if (d != null)
        {
            try { await d.WaitAsync(TimeSpan.FromSeconds(5)); } catch (Exception) { }
        }
        try { cts?.Dispose(); } catch (Exception) { }
    }
}

