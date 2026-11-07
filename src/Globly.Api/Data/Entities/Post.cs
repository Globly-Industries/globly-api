using NetTopologySuite.Geometries;

namespace Globly.Api.Data.Entities;

public enum HiddenReason
{
    None = 0,
    /// <summary>Hidden automatically: too many user reports.</summary>
    Reports = 1,
    /// <summary>Hidden automatically: overwhelmingly downvoted.</summary>
    Downvotes = 2,
    /// <summary>Hidden by an administrator.</summary>
    Admin = 3,
}

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

    /// <summary>
    /// Hidden from the map and feed (visible only to the author and admins) and waiting in the
    /// admin queue. Set automatically by community moderation or manually by an admin.
    /// </summary>
    public bool IsHidden { get; set; }
    public HiddenReason HiddenReason { get; set; }
    public DateTime? HiddenAt { get; set; }

    /// <summary>An admin reviewed and restored this post; the downvote rule no longer hides it.</summary>
    public bool ModeratorApproved { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }

    public List<Comment> Comments { get; set; } = [];
}
