namespace Globly.Api.Data.Entities;

/// <summary>Follower follows Followee (Follows table).</summary>
public class Follow
{
    public required string FollowerId { get; set; }
    public required string FolloweeId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
