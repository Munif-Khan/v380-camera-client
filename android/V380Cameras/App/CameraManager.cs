using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using V380Cameras.Http;

namespace V380Cameras.App;

/// <summary>
/// Owns the camera list, one <see cref="CameraSession"/> plus one loopback
/// <see cref="CameraSite"/> per camera, and the manager site itself.
///
/// On the desktop each camera is a separate worker process on a fixed port.
/// Here they are all in-process on ephemeral ports, so the per-camera pages
/// stay isolated from each other exactly like the desktop build while there is
/// still only one app the user has to install.
/// </summary>
public sealed class CameraManager : IAsyncDisposable
{
    private readonly CameraStore store;
    private readonly string cacheDirectory;
    private readonly ManagerSite managerSite;
    private readonly object gate = new();
    private readonly Dictionary<string, CameraSite> sites = new();

    private HttpServer? managerServer;
    private bool active = true;
    private bool started;

    public CameraManager(CameraStore store, string cacheDirectory)
    {
        this.store = store;
        this.cacheDirectory = cacheDirectory;
        managerSite = new ManagerSite(this);
    }

    public void Start()
    {
        lock (gate)
        {
            if (started) return;
            started = true;

            managerServer = new HttpServer(managerSite.HandleAsync);
            managerServer.Start();
            managerSite.Server = managerServer;

            ReconcileLocked();
        }
    }

    public string Url => managerServer == null ? "http://127.0.0.1/" : managerServer.Origin + "/";

    public bool IsActive => active;

    /// <summary>Stops or resumes every camera stream without closing the pages.</summary>
    public void SetActive(bool value)
    {
        lock (gate)
        {
            active = value;
            foreach (var site in sites.Values)
            {
                if (value) site.Session.Start();
                else site.Session.Stop();
            }
        }
    }

    public IReadOnlyList<CameraSummary> Describe()
    {
        List<CameraSummary> result;
        lock (gate)
        {
            result = store.All.Select(cfg =>
            {
                var site = sites.TryGetValue(cfg.Id, out var found) ? found : null;
                var session = site?.Session;
                return new CameraSummary(
                    cfg.Id, cfg.Name, cfg.Host, cfg.DeviceId,
                    site?.Port ?? 0, cfg.Quality, site?.Url ?? "",
                    cfg.Username,
                    session?.VideoFresh == true,
                    session == null ? null : new HealthSnapshot(
                        session.Frames, session.VideoFresh, session.FrameAgeSeconds,
                        session.State, session.Restarts, session.LastError),
                    cfg.Capabilities);
            }).ToList();
        }
        return result;
    }

    public byte[]? Snapshot(string id)
    {
        lock (gate)
            return sites.TryGetValue(id, out var site) && site.Session.TryGetSnapshot(out var jpeg) ? jpeg : null;
    }

    public string? Health(string id)
    {
        lock (gate)
        {
            if (!sites.TryGetValue(id, out var site)) return null;
            var session = site.Session;
            return JsonSerializer.Serialize(new HealthSnapshot(
                session.Frames, session.VideoFresh, session.FrameAgeSeconds,
                session.State, session.Restarts, session.LastError));
        }
    }

    public void Add(string name, string host, int deviceId, string username, string password, string quality)
    {
        Validate(name, host, deviceId, quality);

        lock (gate)
        {
            if (store.FindByEndpoint(host.Trim(), deviceId) != null)
                throw new ArgumentException("Camera already added");

            store.Add(new CameraConfig
            {
                Name = name.Trim(),
                Host = host.Trim(),
                DeviceId = deviceId,
                Username = username,
                Password = password,
                Quality = quality,
                Port = 8800,
                Capabilities = CameraConfig.DefaultCapabilities()
            });

            ReconcileLocked();
        }
    }

    public void Edit(string id, string name, string host, int deviceId, string username, string password, string quality)
    {
        Validate(name, host, deviceId, quality);

        lock (gate)
        {
            var existing = store.Find(id) ?? throw new ArgumentException("Unknown camera");

            var clash = store.FindByEndpoint(host.Trim(), deviceId);
            if (clash != null && clash.Id != id) throw new ArgumentException("Camera already added");

            existing.Name = name.Trim();
            existing.Host = host.Trim();
            existing.DeviceId = deviceId;
            existing.Username = username;
            existing.Quality = quality;
            // An empty password field means "unchanged", like the desktop form.
            if (!string.IsNullOrEmpty(password)) existing.Password = password;

            store.Update(existing);
            ReconcileLocked();
        }
    }

    public Task RemoveAsync(string id)
    {
        CameraSite? doomed = null;
        lock (gate)
        {
            if (!store.Remove(id)) return Task.CompletedTask;
            if (sites.Remove(id, out var site)) doomed = site;
            ReconcileLocked();
        }
        return doomed?.DisposeAsync().AsTask() ?? Task.CompletedTask;
    }

    /// <summary>
    /// Brings the running sites in line with the stored list: creates what is
    /// new, rebuilds what changed (only a restart can pick up new credentials),
    /// and closes what was removed.
    /// </summary>
    private void ReconcileLocked()
    {
        var wanted = store.All;
        var wantedIds = wanted.Select(c => c.Id).ToHashSet();

        foreach (var id in sites.Keys.Where(id => !wantedIds.Contains(id)).ToList())
        {
            if (sites.Remove(id, out var gone)) Forget(gone.DisposeAsync().AsTask());
        }

        foreach (var config in wanted)
        {
            if (sites.TryGetValue(config.Id, out var existing))
            {
                var running = existing.Session.Config;
                bool unchanged =
                    running.Name == config.Name &&
                    running.Host == config.Host &&
                    running.DeviceId == config.DeviceId &&
                    running.Username == config.Username &&
                    running.Password == config.Password &&
                    running.Quality == config.Quality &&
                    running.Port == config.Port;

                if (unchanged) continue;

                sites.Remove(config.Id);
                Forget(existing.DisposeAsync().AsTask());
            }

            var session = new CameraSession(config);
            var site = new CameraSite(session, cacheDirectory, () => managerSite.SelfUrl);
            sites[config.Id] = site;
            if (active) session.Start();
        }
    }

    private static void Forget(Task task) => _ = task.ContinueWith(
        t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);

    private static void Validate(string name, string host, int deviceId, string quality)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 80)
            throw new ArgumentException("A name is required");
        if (!IPAddress.TryParse(host?.Trim(), out _))
            throw new ArgumentException("Enter the camera's IP address");
        if (deviceId < 1)
            throw new ArgumentException("Enter the device ID from the camera label");
        if (quality is not ("sd" or "hd"))
            throw new ArgumentException("Choose sd or hd");
    }

    public async ValueTask DisposeAsync()
    {
        List<CameraSite> all;
        lock (gate)
        {
            all = sites.Values.ToList();
            sites.Clear();
        }

        foreach (var site in all)
        {
            try { await site.DisposeAsync(); } catch (Exception) { }
        }

        if (managerServer != null)
        {
            try { await managerServer.DisposeAsync(); } catch (Exception) { }
        }
    }
}

public sealed record CameraSummary(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("host")] string Host,
    [property: JsonPropertyName("deviceId")] int DeviceId,
    [property: JsonPropertyName("webPort")] int WebPort,
    [property: JsonPropertyName("quality")] string Quality,
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("username")] string Username,
    [property: JsonPropertyName("online")] bool Online,
    [property: JsonPropertyName("health")] HealthSnapshot? Health,
    [property: JsonPropertyName("capabilities")] Dictionary<string, string> Capabilities);

public sealed record HealthSnapshot(
    [property: JsonPropertyName("frames")] long Frames,
    [property: JsonPropertyName("videoFresh")] bool VideoFresh,
    [property: JsonPropertyName("frameAgeSeconds")] double? FrameAgeSeconds,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("restarts")] int Restarts,
    [property: JsonPropertyName("lastError")] string? LastError);
