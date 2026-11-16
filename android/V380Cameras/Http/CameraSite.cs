using System.Text.Json;
using V380Cameras.App;
using V380Cameras.Media;
using V380Decoder.src;

namespace V380Cameras.Http;

/// <summary>
/// The per-camera web app. Mirrors the routes that <c>camera-web.py</c> exposed
/// on the desktop (page, /ui/*, /snapshot, /health, /api/*), so <c>ui/</c> is
/// bundled unchanged and the page cannot tell which build is serving it.
/// </summary>
public sealed class CameraSite : IAsyncDisposable
{
    private readonly CameraSession session;
    private readonly HttpServer server;
    private readonly string cacheDirectory;
    private readonly Func<string> managerUrl;

    public CameraSite(CameraSession session, string cacheDirectory, Func<string> managerUrl)
    {
        this.session = session;
        this.cacheDirectory = cacheDirectory;
        this.managerUrl = managerUrl;
        server = new HttpServer(HandleAsync);
        server.Start();
    }

    public CameraSession Session => session;

    public int Port => server.Port;

    public string Url => "http://127.0.0.1:" + Port + "/";

    private async Task HandleAsync(HttpRequest request, HttpResponse response)
    {
        var path = request.Path;

        if (request.Method == "POST")
        {
            if (!OriginAllowed(request)) { await response.SendErrorAsync(403, "Bad origin"); return; }
            await HandlePostAsync(request, response, path);
            return;
        }

        if (request.Method != "GET")
        {
            await response.SendErrorAsync(404, "Not found");
            return;
        }

        if (path == "/")
        {
            await response.SendTextAsync(Page(), 200, "text/html; charset=utf-8");
            return;
        }

        if (path.StartsWith("/ui/", StringComparison.Ordinal))
        {
            await ServeAssetAsync(response, path[4..]);
            return;
        }

        if (path == "/favicon.ico")
        {
            await ServeAssetAsync(response, "icons/icon-192.png");
            return;
        }

        if (path == "/snapshot")
        {
            if (!session.TryGetSnapshot(out var jpeg))
            {
                await response.SendErrorAsync(503, "Waiting for live video");
                return;
            }
            await response.SendBytesAsync(jpeg, "image/jpeg");
            return;
        }

        if (path == "/audio.wav")
        {
            await StreamAudioAsync(response);
            return;
        }

        if (path == "/health")
        {
            await response.SendJsonAsync(Health());
            return;
        }

        if (path == "/api/recordings") { await RecordingsAsync(request, response); return; }
        if (path == "/api/alarm") { await ReadAlarmAsync(response); return; }
        if (path == "/api/clip") { await ClipAsync(request, response); return; }

        if (path == "/api/status")
        {
            await response.SendJsonAsync(JsonSerializer.Serialize(new
            {
                status = "running",
                timestamp = DateTime.Now.ToString("O")
            }));
            return;
        }

        await response.SendErrorAsync(404, "Not found");
    }

    private async Task HandlePostAsync(HttpRequest request, HttpResponse response, string path)
    {
        if (path == "/api/alarm") { await WriteAlarmAsync(request, response); return; }

        if (path.StartsWith("/api/ptz/", StringComparison.Ordinal))
        {
            if (session.Ptz(path["/api/ptz/".Length..]))
                await response.SendTextAsync("OK");
            else
                await ProblemAsync(response, 503, "Camera command could not be sent");
            return;
        }

        if (path.StartsWith("/api/light/", StringComparison.Ordinal))
        {
            if (session.Light(path["/api/light/".Length..]))
                await response.SendTextAsync("OK");
            else
                await ProblemAsync(response, 503, "Camera command could not be sent");
            return;
        }

        if (path.StartsWith("/api/image/", StringComparison.Ordinal))
        {
            if (session.Image(path["/api/image/".Length..]))
                await response.SendTextAsync("OK");
            else
                await ProblemAsync(response, 503, "Camera command could not be sent");
            return;
        }

        await response.SendErrorAsync(404, "Not found");
    }

    private async Task ServeAssetAsync(HttpResponse response, string name)
    {
        var bytes = AssetFiles.ReadUi(name);
        if (bytes == null)
        {
            await response.SendErrorAsync(404, "Not found");
            return;
        }
        await response.SendBytesAsync(bytes, AssetFiles.ContentType(name));
    }

    private string Page()
    {
        var config = session.Config;
        var cfg = new
        {
            id = config.Id,
            name = config.Name,
            host = config.Host,
            deviceId = config.DeviceId,
            webPort = config.Port,
            quality = config.Quality,
            hasPtz = config.HasPtz,
            homeUrl = managerUrl(),
            capabilities = config.Capabilities
        };

        var json = JsonSerializer.Serialize(cfg).Replace("</", "<\\/");
        return AssetFiles.ReadUiText("camera.html").Replace("__CAMERA_CONFIG__", json);
    }

    private string Health()
    {
        return JsonSerializer.Serialize(new
        {
            frames = session.Frames,
            videoFresh = session.VideoFresh,
            frameAgeSeconds = session.FrameAgeSeconds,
            state = session.State,
            restarts = session.Restarts,
            lastError = session.LastError
        });
    }

    private async Task RecordingsAsync(HttpRequest request, HttpResponse response)
    {
        var text = request.QueryValue("date");
        if (!DateOnly.TryParseExact(text, "yyyy-MM-dd", out var date))
        {
            await ProblemAsync(response, 400, "Use date YYYY-MM-DD");
            return;
        }

        try
        {
            var list = await session.ListRecordingsAsync(date, CancellationToken.None);
            await response.SendJsonAsync(JsonSerializer.Serialize(list));
        }
        catch (Exception ex)
        {
            await ProblemAsync(response, 503, "Camera SD search unavailable: " + ex.Message);
        }
    }

    private async Task ReadAlarmAsync(HttpResponse response)
    {
        try
        {
            var alarm = await session.ReadAlarmConfigAsync(CancellationToken.None);
            await response.SendJsonAsync(JsonSerializer.Serialize(alarm));
        }
        catch (Exception ex)
        {
            await ProblemAsync(response, 503, "Camera alarm settings unavailable: " + ex.Message);
        }
    }

    private async Task WriteAlarmAsync(HttpRequest request, HttpResponse response)
    {
        try
        {
            using var body = JsonDocument.Parse(request.Body);
            var root = body.RootElement;

            var current = await session.ReadAlarmConfigAsync(CancellationToken.None);

            bool? Bool(string name) =>
                root.TryGetProperty(name, out var value) &&
                value.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? value.GetBoolean()
                    : null;

            var updated = current.WithSwitches(Bool("MainAlarmSwitch"), Bool("MotionAlarmSwitch"));

            if (root.TryGetProperty("Cells", out var cells) && cells.ValueKind == JsonValueKind.Array)
                updated = updated.WithCells(cells.EnumerateArray().Select(c => c.GetInt32()).ToArray());

            await session.WriteAlarmConfigAsync(updated, CancellationToken.None);

            // Re-read so the page shows exactly what the camera stored.
            var after = await session.ReadAlarmConfigAsync(CancellationToken.None);
            await response.SendJsonAsync(JsonSerializer.Serialize(after));
        }
        catch (JsonException)
        {
            await ProblemAsync(response, 400, "Expected a JSON object such as {\"MotionAlarmSwitch\": true}");
        }
        catch (ArgumentException ex)
        {
            await ProblemAsync(response, 400, ex.Message);
        }
        catch (Exception ex)
        {
            await ProblemAsync(response, 503, "Camera alarm settings unavailable: " + ex.Message);
        }
    }

    private async Task ClipAsync(HttpRequest request, HttpResponse response)
    {
        if (!uint.TryParse(request.QueryValue("id"), out var segmentId) || segmentId == 0)
        {
            await ProblemAsync(response, 400, "Use id (segment id from /api/recordings)");
            return;
        }
        if (!uint.TryParse(request.QueryValue("start"), out var startRaw) || startRaw == 0)
        {
            await ProblemAsync(response, 400, "Use start (segment start, camera epoch seconds)");
            return;
        }

        int duration = 120;
        if (request.QueryValue("duration") is { Length: > 0 } durationText &&
            (!int.TryParse(durationText, out duration) || duration < 1 || duration > 600))
        {
            await ProblemAsync(response, 400, "duration must be 1..600 seconds");
            return;
        }

        bool download = request.QueryValue("download") == "1";
        var file = Path.Combine(cacheDirectory, "clip-" + Guid.NewGuid().ToString("N") + ".mp4");

        try
        {
            // MediaMuxer needs a seekable file (the moov box is written at the
            // end), so the clip is assembled first and served with byte ranges
            // afterwards. A 120 s clip takes about as long as it played.
            await session.ExportClipAsync(segmentId, startRaw, duration, file, CancellationToken.None);

            string? disposition = null;
            if (download)
            {
                var name = "clip-" + SdCardProtocol.WallTime(startRaw).Replace(':', '-').Replace('T', '_') + ".mp4";
                disposition = "attachment; filename=\"" + name + "\"";
            }

            await response.SendFileAsync(file, "video/mp4", request.Header("Range"), disposition);
        }
        catch (InvalidOperationException ex)
        {
            await ProblemAsync(response, 409, "Clip unavailable: " + ex.Message);
        }
        catch (Exception ex)
        {
            await ProblemAsync(response, 503, "Clip unavailable: " + ex.Message);
        }
        finally
        {
            try { if (File.Exists(file)) File.Delete(file); } catch (Exception) { }
        }
    }

    private async Task StreamAudioAsync(HttpResponse response)
    {
        using var subscription = session.Audio.Subscribe();
        await response.StartStreamAsync(200, "audio/wav");
        await response.WriteAsync(AlawCodec.WavHeader());

        // The player keeps the connection open forever, so a silent camera must
        // not stall the socket: half a second of digital silence fills the gap.
        var silence = new byte[AlawCodec.SampleRate];

        while (true)
        {
            byte[] chunk;
            using (var idle = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            {
                try
                {
                    chunk = await subscription.Reader.ReadAsync(idle.Token);
                }
                catch (OperationCanceledException)
                {
                    chunk = silence;
                }
            }

            // A closed player throws here, which ends the pump.
            if (chunk.Length > 0) await response.WriteAsync(chunk);
        }
    }

    private bool OriginAllowed(HttpRequest request)
    {
        var origin = request.Header("Origin");
        if (string.IsNullOrEmpty(origin)) return true;
        return origin == server.Origin || origin == "http://localhost:" + server.Port;
    }

    private static Task ProblemAsync(HttpResponse response, int status, string message)
        => response.SendJsonAsync(
            JsonSerializer.Serialize(new { type = "about:blank", title = message, status, detail = message }),
            status);

    public async ValueTask DisposeAsync()
    {
        await session.DisposeAsync();
        await server.DisposeAsync();
    }
}
