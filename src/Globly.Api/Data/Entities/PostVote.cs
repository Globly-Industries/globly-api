namespace Globly.Api.Data.Entities;

/// <summary>One up/down vote per user per post. Keeps Post.Upvotes/Downvotes honest.</summary>
public class PostVote
{
    public Guid PostId { get; set; }
    public required string UserId { get; set; }

    /// <summary>+1 or -1.</summary>
    public short Value { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>One "request manual moderation" vote per user per post.</summary>
public class PostReport
{
    public Guid PostId { get; set; }
    public required string UserId { get; set; }

    public string? Reason { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
