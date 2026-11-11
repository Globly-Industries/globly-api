namespace Globly.Api.Data.Entities;

/// <summary>
/// Proof that media was captured in the app just now (CaptureTickets table).
/// The app asks for a ticket right before opening the camera or recorder, seals it with the file's
/// SHA-256 as soon as the capture ends, and the post upload must then carry exactly that file.
/// Used tickets are kept so the same file can never be posted twice.
/// </summary>
public class CaptureTicket
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public required string UserId { get; set; }

    public MediaType MediaType { get; set; }

    public DateTime IssuedAt { get; set; } = DateTime.UtcNow;

    /// <summary>When the app reported the finished capture's hash.</summary>
    public DateTime? SealedAt { get; set; }

    /// <summary>Lowercase hex SHA-256 of the captured file, as uploaded.</summary>
    public string? Sha256 { get; set; }

    /// <summary>When a post was published with this capture.</summary>
    public DateTime? UsedAt { get; set; }
}
