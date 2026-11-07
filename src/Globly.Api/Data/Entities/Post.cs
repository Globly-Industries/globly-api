using NetTopologySuite.Geometries;

namespace Globly.Api.Data.Entities;

public enum MediaType
{
    Text = 0,
    Image = 1,
    Video = 2,
    Audio = 3,
}

/// <summary>A geolocated publication (Posts table).</summary>
public class Post
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public required string UserId { get; set; }
    public AppUser? User { get; set; }

    public required string Title { get; set; }
    public string? Description { get; set; }

    public MediaType MediaType { get; set; }

    /// <summary>Media stored in Cloudflare R2. Null for text-only posts.</summary>
    public Guid? FileId { get; set; }
    public MediaFile? File { get; set; }

    /// <summary>WGS84 point (SRID 4326). X = longitude, Y = latitude.</summary>
    public required Point Location { get; set; }

    public int Views { get; set; }
    public int Upvotes { get; set; }
    public int Downvotes { get; set; }

    /// <summary>Number of "needs manual moderation" votes from users.</summary>
    public int ReportCount { get; set; }

    /// <summary>Set when ReportCount reaches the threshold; shows up in the admin queue.</summary>
    public bool IsFlagged { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }

    public List<Comment> Comments { get; set; } = [];
}
