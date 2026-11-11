using Globly.Api.Data.Entities;

namespace Globly.Api.Posts;

public class CaptureOptions
{
    public const string Section = "Capture";

    /// <summary>
    /// Time from asking for a ticket to sealing it. Covers the camera UI, the longest recording
    /// (3 minutes) and a slow network, but not swapping in a file made somewhere else.
    /// </summary>
    public int SealWindowMinutes { get; set; } = 10;

    /// <summary>Time from sealing to publishing: writing the title and uploading.</summary>
    public int PublishWindowMinutes { get; set; } = 60;

    /// <summary>Tickets one user may ask for per hour.</summary>
    public int MaxTicketsPerHour { get; set; } = 60;

    public TimeSpan SealWindow => TimeSpan.FromMinutes(SealWindowMinutes);
    public TimeSpan PublishWindow => TimeSpan.FromMinutes(PublishWindowMinutes);
}

public enum CaptureProblem
{
    None,
    /// <summary>No such ticket, or it belongs to someone else.</summary>
    NotFound,
    Expired,
    /// <summary>Sealed already with a different file.</summary>
    AlreadySealed,
    NotSealed,
    Used,
    WrongType,
    /// <summary>The uploaded file is not the one that was sealed.</summary>
    HashMismatch,
}

/// <summary>The timing and ownership rules for capture tickets. Pure, so they are easy to test.</summary>
public static class CaptureRules
{
    /// <summary>Sealing twice with the same hash is allowed, so the app can retry after a lost response.</summary>
    public static CaptureProblem CheckSeal(CaptureTicket? ticket, string userId, string sha256, DateTime now, CaptureOptions o)
    {
        if (ticket is null || ticket.UserId != userId) return CaptureProblem.NotFound;
        if (ticket.UsedAt is not null) return CaptureProblem.Used;
        if (ticket.Sha256 is not null)
            return ticket.Sha256 == sha256 ? CaptureProblem.None : CaptureProblem.AlreadySealed;
        if (now - ticket.IssuedAt > o.SealWindow) return CaptureProblem.Expired;
        return CaptureProblem.None;
    }

    public static CaptureProblem CheckPublish(
        CaptureTicket? ticket, string userId, MediaType type, string sha256, DateTime now, CaptureOptions o)
    {
        if (ticket is null || ticket.UserId != userId) return CaptureProblem.NotFound;
        if (ticket.UsedAt is not null) return CaptureProblem.Used;
        if (ticket.SealedAt is null || ticket.Sha256 is null) return CaptureProblem.NotSealed;
        if (ticket.MediaType != type) return CaptureProblem.WrongType;
        if (now - ticket.SealedAt.Value > o.PublishWindow) return CaptureProblem.Expired;
        if (ticket.Sha256 != sha256) return CaptureProblem.HashMismatch;
        return CaptureProblem.None;
    }

    /// <summary>Lowercase hex, or null when the value is not a SHA-256 in hex.</summary>
    public static string? NormalizeSha256(string? value)
    {
        if (value is null || value.Length != 64 || !value.All(Uri.IsHexDigit)) return null;
        return value.ToLowerInvariant();
    }
}
