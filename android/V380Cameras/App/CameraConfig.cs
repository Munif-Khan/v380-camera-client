using System.Text.Json.Serialization;

namespace V380Cameras.App;

/// <summary>
/// One camera as the user entered it. Mirrors the desktop
/// <c>cameras.local.json</c> entry, minus the per-process web/api ports: on
/// Android each camera gets an ephemeral bridge port at runtime instead.
/// </summary>
public sealed class CameraConfig
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Host { get; set; } = "";
    public int DeviceId { get; set; }
    public string Username { get; set; } = "";
    public string Quality { get; set; } = "sd";

    /// <summary>Camera command/media port. Every V380 seen so far uses 8800.</summary>
    public int Port { get; set; } = 8800;

    public Dictionary<string, string> Capabilities { get; set; } = new();

    /// <summary>Keystore-wrapped password. The plaintext never leaves memory.</summary>
    public string PasswordSecret { get; set; } = "";

    [JsonIgnore]
    public string Password { get; set; } = "";

    public bool IsHd => string.Equals(Quality, "hd", StringComparison.OrdinalIgnoreCase);

    public CameraConfig Clone() => new()
    {
        Id = Id,
        Name = Name,
        Host = Host,
        DeviceId = DeviceId,
        Username = Username,
        Quality = Quality,
        Port = Port,
        Capabilities = new Dictionary<string, string>(Capabilities),
        Password = Password,
        PasswordSecret = PasswordSecret
    };

    /// <summary>
    /// A camera is treated as having a capability unless the entry says
    /// otherwise, which matches the desktop manager's rule for PTZ.
    /// </summary>
    public bool HasPtz =>
        !Capabilities.TryGetValue("ptz", out var ptz) ||
        !string.Equals(ptz, "unsupported", StringComparison.OrdinalIgnoreCase);

    public static Dictionary<string, string> DefaultCapabilities() => new()
    {
        ["light"] = "unverified",
        ["audioListen"] = "unverified",
        ["ptz"] = "unknown",
        ["sdPlayback"] = "unverified",
        ["alarmSettings"] = "unverified"
    };
}
