namespace Globly.Api.Data.Entities;

/// <summary>A phone that gets push notifications, owned by the user last signed in on it (PushDevices table).</summary>
public class PushDevice
{
    /// <summary>Firebase Installation ID, the FCM send target.</summary>
    public required string Fid { get; set; }

    public required string UserId { get; set; }

    /// <summary>App language ("en", "lv"), so pushes match it.</summary>
    public required string Language { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
