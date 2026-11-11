namespace Globly.Api.Data.Entities;

/// <summary>A text comment on a post (Comments table).</summary>
public class Comment
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid PostId { get; set; }
    public Post? Post { get; set; }

    public required string UserId { get; set; }
    public AppUser? User { get; set; }

    public required string Body { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
