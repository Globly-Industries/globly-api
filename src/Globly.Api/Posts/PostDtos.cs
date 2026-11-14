using System.ComponentModel.DataAnnotations;
using Globly.Api.Data.Entities;

namespace Globly.Api.Posts;

public record AuthorDto(string Id, string UserName, string? AvatarUrl = null);

public record MediaDto(string Url, string ContentType, long ContentLength);

public record PostDto(
    Guid Id,
    AuthorDto Author,
    string Title,
    string? Description,
    MediaType MediaType,
    MediaDto? Media,
    double Latitude,
    double Longitude,
    /// <summary>Null when precise; otherwise the location is only known to within this many meters.</summary>
    int? LocationRadiusMeters,
    int Views,
    int Upvotes,
    int Downvotes,
    int CommentCount,
    /// <summary>The caller's vote on this post: 1, -1 or 0 (always 0 for anonymous users).</summary>
    int MyVote,
    int ReportCount,
    /// <summary>Hidden by moderation. Only the author and admins ever receive hidden posts.</summary>
    bool IsHidden,
    HiddenReason HiddenReason,
    DateTime CreatedAt,
    DateTime? UpdatedAt,
    /// <summary>Admins only (null for everyone else): the NSFW filter's score, 0-1.</summary>
    float? NsfwScore = null,
    /// <summary>Admins only: the NSFW filter queued this visible post for review.</summary>
    bool NeedsReview = false,
    int AwardCount = 0,
    /// <summary>Whether the caller gave this post an award.</summary>
    bool MyAward = false,
    /// <summary>Feeds only: people the caller follows who liked this post.</summary>
    LikedByDto? LikedBy = null);

/// <summary>The latest few names, newest first, and how many liked it in all.</summary>
public record LikedByDto(IReadOnlyList<string> UserNames, int Count);

public record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, bool HasMore);

/// <summary>Multipart form for creating a post. File is required unless MediaType is Text.</summary>
public class CreatePostForm
{
    [Required, StringLength(120, MinimumLength = 1)]
    public string Title { get; set; } = "";

    [StringLength(2000)]
    public string? Description { get; set; }

    [Required]
    public MediaType MediaType { get; set; }

    [Range(-90, 90)]
    public double Latitude { get; set; }

    [Range(-180, 180)]
    public double Longitude { get; set; }

    /// <summary>Send when the device gave only an approximate location. Omit when precise.</summary>
    [Range(1, 50000)]
    public int? LocationRadiusMeters { get; set; }

    public IFormFile? File { get; set; }

    /// <summary>The sealed capture ticket of File (see CapturesController). Required with a file.</summary>
    public Guid? CaptureId { get; set; }

    /// <summary>Android attestation chain: base64 DER certs, comma separated, leaf first.</summary>
    [StringLength(64 * 1024)]
    public string? Attestation { get; set; }

    /// <summary>Why the app couldn't attest, for the logs.</summary>
    [StringLength(1000)]
    public string? AttestationError { get; set; }
}

public record UpdatePostRequest(
    [Required, StringLength(120, MinimumLength = 1)] string Title,
    [StringLength(2000)] string? Description);

/// <summary>Common filters for map/feed/nearby queries.</summary>
public class PostFilter
{
    /// <summary>Only include these media types. Empty = all.</summary>
    public MediaType[]? Types { get; set; }

    /// <summary>Only posts created after this time (UTC).</summary>
    public DateTime? Since { get; set; }

    /// <summary>Only posts with (upvotes - downvotes) at least this value.</summary>
    public int? MinScore { get; set; }
}

public record MapQuery(
    [Range(-90, 90)] double MinLat,
    [Range(-180, 180)] double MinLng,
    [Range(-90, 90)] double MaxLat,
    [Range(-180, 180)] double MaxLng,
    [Range(0, 22)] double Zoom);

/// <summary>
/// A map point. When Count == 1 it is a single post (PostId/MediaType/Title set);
/// otherwise it is a cluster placed on its best post.
/// Score is the feed-style rank of the (best) post, for drawing better posts on top.
/// </summary>
public record MapPointDto(
    double Latitude,
    double Longitude,
    int Count,
    Guid? PostId,
    MediaType? MediaType,
    string? Title,
    int? LocationRadiusMeters = null,
    double Score = 0,
    MapBoundsDto? Bounds = null);

/// <summary>The area a cluster's posts cover.</summary>
public record MapBoundsDto(double MinLat, double MinLng, double MaxLat, double MaxLng);
