namespace V380Cameras.Http;

/// <summary>
/// The web UI is packaged as an asset exactly as it exists in <c>ui/</c>, so the
/// browser build and the app can never drift apart.
/// </summary>
public static class AssetFiles
{
    private static readonly Dictionary<string, string> ContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".css"] = "text/css; charset=utf-8",
        [".js"] = "application/javascript; charset=utf-8",
        [".html"] = "text/html; charset=utf-8",
        [".svg"] = "image/svg+xml",
        [".json"] = "application/json",
        [".webmanifest"] = "application/manifest+json",
        [".png"] = "image/png",
        [".ico"] = "image/x-icon"
    };

    public static string ContentType(string name)
        => ContentTypes.TryGetValue(Path.GetExtension(name), out var type) ? type : "application/octet-stream";

    /// <summary>
    /// Reads <c>ui/&lt;name&gt;</c> from the APK. Returns null for anything that
    /// is not a plain file inside the UI folder, so a crafted path cannot reach
    /// other assets.
    /// </summary>
    public static byte[]? ReadUi(string name)
    {
        var clean = name.Replace('\\', '/').TrimStart('/');
        if (clean.Length == 0 || clean.Contains("..", StringComparison.Ordinal)) return null;

        var assets = Android.App.Application.Context?.Assets;
        if (assets == null) return null;

        try
        {
            using var stream = assets.Open("ui/" + clean);
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            return memory.ToArray();
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static string ReadUiText(string name)
    {
        var bytes = ReadUi(name);
        return bytes == null ? "" : System.Text.Encoding.UTF8.GetString(bytes);
    }
}
