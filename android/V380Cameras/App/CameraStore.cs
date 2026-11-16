using System.Text.Json;
using System.Text.Json.Serialization;

namespace V380Cameras.App;

/// <summary>
/// The camera list, persisted in the app's private files directory as JSON with
/// the same shape as the desktop <c>cameras.local.json</c> ("cameras": [...]),
/// except that the password field is stored wrapped by <see cref="SecretProtector"/>.
/// </summary>
public sealed class CameraStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string path;
    private readonly object gate = new();
    private readonly List<CameraConfig> cameras = new();

    public CameraStore(string filesDirectory)
    {
        path = Path.Combine(filesDirectory, "cameras.json");
        Load();
    }

    public string FilePath => path;

    public IReadOnlyList<CameraConfig> All
    {
        get { lock (gate) return cameras.Select(c => c.Clone()).ToList(); }
    }

    public CameraConfig? Find(string id)
    {
        lock (gate)
            return cameras.FirstOrDefault(c => c.Id == id)?.Clone();
    }

    public CameraConfig? FindByEndpoint(string host, int deviceId)
    {
        lock (gate)
            return cameras.FirstOrDefault(c => c.Host == host && c.DeviceId == deviceId)?.Clone();
    }

    /// <summary>Adds a camera and returns the stored entry (with its new id).</summary>
    public CameraConfig Add(CameraConfig camera)
    {
        lock (gate)
        {
            camera.Id = NextIdLocked();
            if (camera.Capabilities.Count == 0) camera.Capabilities = CameraConfig.DefaultCapabilities();
            camera.Port = camera.Port > 0 ? camera.Port : 8800;
            cameras.Add(camera);
            SaveLocked();
            return camera.Clone();
        }
    }

    public CameraConfig? Update(CameraConfig camera)
    {
        lock (gate)
        {
            var index = cameras.FindIndex(c => c.Id == camera.Id);
            if (index < 0) return null;
            camera.Port = camera.Port > 0 ? camera.Port : 8800;
            cameras[index] = camera;
            SaveLocked();
            return camera.Clone();
        }
    }

    public bool Remove(string id)
    {
        lock (gate)
        {
            var removed = cameras.RemoveAll(c => c.Id == id) > 0;
            if (removed) SaveLocked();
            return removed;
        }
    }

    private string NextIdLocked()
    {
        for (int n = 1; ; n++)
        {
            var candidate = "camera-" + n;
            if (cameras.All(c => c.Id != candidate)) return candidate;
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(path)) return;
            var json = File.ReadAllText(path);
            var document = JsonSerializer.Deserialize<StoreFile>(json, Options);
            if (document?.Cameras == null) return;

            foreach (var camera in document.Cameras)
            {
                if (string.IsNullOrWhiteSpace(camera.Id) || string.IsNullOrWhiteSpace(camera.Host))
                    continue;
                camera.Password = SecretProtector.Unprotect(camera.PasswordSecret);
                if (camera.Capabilities.Count == 0) camera.Capabilities = CameraConfig.DefaultCapabilities();
                if (camera.Port <= 0) camera.Port = 8800;
                cameras.Add(camera);
            }
        }
        catch (Exception)
        {
            // A corrupt file must not brick the app; the user can re-add cameras.
        }
    }

    private void SaveLocked()
    {
        foreach (var camera in cameras)
            camera.PasswordSecret = SecretProtector.Protect(camera.Password);

        var payload = JsonSerializer.Serialize(new StoreFile { Cameras = cameras }, Options);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, payload);
        // Atomic replace so a kill mid-write cannot truncate the only copy.
        File.Move(temp, path, overwrite: true);
    }

    private sealed class StoreFile
    {
        public List<CameraConfig> Cameras { get; set; } = new();
    }
}
