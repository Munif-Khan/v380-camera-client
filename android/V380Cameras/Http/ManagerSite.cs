using System.Net;
using System.Text.Json;
using V380Cameras.App;

namespace V380Cameras.Http;

/// <summary>
/// The camera-list page. Route-for-route the same surface as
/// <c>camera-manager.py</c>: the bundled <c>ui/home.html</c> and <c>ui/home.js</c>
/// run unmodified against it.
/// </summary>
public sealed class ManagerSite
{
    private readonly CameraManager cameras;

    public ManagerSite(CameraManager cameras) => this.cameras = cameras;

    public HttpServer? Server { get; set; }

    public string SelfUrl => Server == null ? "/" : Server.Origin + "/";

    public async Task HandleAsync(HttpRequest request, HttpResponse response)
    {
        if (request.Method == "POST")
        {
            if (!OriginAllowed(request)) { await response.SendErrorAsync(403, "Bad origin"); return; }
            await HandlePostAsync(request, response, request.Path);
            return;
        }

        if (request.Method != "GET")
        {
            await response.SendErrorAsync(404, "Not found");
            return;
        }

        var path = request.Path;

        if (path == "/")
        {
            var html = AssetFiles.ReadUiText("home.html");
            if (html.Length == 0) { await response.SendErrorAsync(500, "UI files missing"); return; }
            await response.SendTextAsync(html, 200, "text/html; charset=utf-8");
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

        if (path == "/api/cameras")
        {
            await response.SendJsonAsync(JsonSerializer.Serialize(cameras.Describe()));
            return;
        }

        if (path.StartsWith("/api/camera/", StringComparison.Ordinal))
        {
            await RelayCameraAsync(response, path["/api/camera/".Length..]);
            return;
        }

        await response.SendErrorAsync(404, "Not found");
    }

    private async Task RelayCameraAsync(HttpResponse response, string rest)
    {
        var slash = rest.IndexOf('/');
        if (slash <= 0) { await response.SendErrorAsync(404, "Not found"); return; }

        var id = Uri.UnescapeDataString(rest[..slash]);
        var leaf = rest[(slash + 1)..];

        if (leaf == "snapshot")
        {
            var jpeg = cameras.Snapshot(id);
            if (jpeg == null) { await response.SendErrorAsync(503, "Camera unavailable"); return; }
            await response.SendBytesAsync(jpeg, "image/jpeg");
            return;
        }

        if (leaf == "health")
        {
            var health = cameras.Health(id);
            if (health == null) { await response.SendErrorAsync(404, "Unknown camera"); return; }
            await response.SendJsonAsync(health);
            return;
        }

        await response.SendErrorAsync(404, "Not found");
    }

    private async Task HandlePostAsync(HttpRequest request, HttpResponse response, string path)
    {
        if (path == "/add")
        {
            try
            {
                cameras.Add(
                    Required(request, "name"),
                    Required(request, "host"),
                    (int)RequiredNumber(request, "deviceId"),
                    Required(request, "username"),
                    request.FormValue("password") ?? "",
                    Required(request, "quality"));
                await response.SendRedirectAsync("/");
            }
            catch (Exception)
            {
                await response.SendErrorAsync(400, "Check the fields; the camera may already be added");
            }
            return;
        }

        if (path == "/edit")
        {
            try
            {
                cameras.Edit(
                    Required(request, "id"),
                    Required(request, "name"),
                    Required(request, "host"),
                    (int)RequiredNumber(request, "deviceId"),
                    Required(request, "username"),
                    request.FormValue("password") ?? "",
                    Required(request, "quality"));
                await response.SendRedirectAsync("/");
            }
            catch (Exception)
            {
                await response.SendErrorAsync(400, "Check the fields; the camera may already be added");
            }
            return;
        }

        if (path == "/remove")
        {
            var id = request.FormValue("id");
            if (string.IsNullOrEmpty(id)) { await response.SendErrorAsync(400, "Missing id"); return; }
            await cameras.RemoveAsync(id);
            await response.SendJsonAsync("{\"ok\":true}");
            return;
        }

        await response.SendErrorAsync(404, "Not found");
    }

    private async Task ServeAssetAsync(HttpResponse response, string name)
    {
        var bytes = AssetFiles.ReadUi(name);
        if (bytes == null) { await response.SendErrorAsync(404, "Not found"); return; }
        await response.SendBytesAsync(bytes, AssetFiles.ContentType(name));
    }

    private static string Required(HttpRequest request, string name)
    {
        var value = request.FormValue(name);
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException(name + " is required");
        return value;
    }

    private static double RequiredNumber(HttpRequest request, string name)
    {
        if (!double.TryParse(Required(request, name), out var value)) throw new ArgumentException(name + " must be a number");
        return value;
    }

    private bool OriginAllowed(HttpRequest request)
    {
        var origin = request.Header("Origin");
        if (string.IsNullOrEmpty(origin)) return true;
        return origin == Server?.Origin || origin == "http://localhost:" + Server?.Port;
    }
}
