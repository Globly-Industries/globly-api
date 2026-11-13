namespace Globly.Api.Data.Entities;

public enum NotificationType
{
    /// <summary>Someone commented on the recipient's post.</summary>
    Comment = 0,
    /// <summary>Someone @mentioned the recipient in a comment.</summary>
    Mention = 1,
}

/// <summary>An in-app notification about a comment (Notifications table).</summary>
public class Notification
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public required string RecipientId { get; set; }
    public required string ActorId { get; set; }
    public AppUser? Actor { get; set; }

    public NotificationType Type { get; set; }

    public Guid PostId { get; set; }
    public Post? Post { get; set; }

    public Guid CommentId { get; set; }
    public Comment? Comment { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ReadAt { get; set; }
}
