namespace Globly.Api.Data.Entities;

public enum PointsKind
{
    /// <summary>+1: someone liked the user's post. Once per voter per post, never taken back.</summary>
    LikeReceived = 0,
    /// <summary>Spent on a spotlight (a removed feature). Kept for history.</summary>
    Spotlight = 1,
    /// <summary>Given away as an award on someone else's post.</summary>
    AwardGiven = 2,
    /// <summary>An award on the user's post; SourceUserId is the giver.</summary>
    AwardReceived = 3,
}

/// <summary>One change to a user's points (PointsLedger table). AppUser.Points is the running sum.</summary>
public class PointsEntry
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public required string UserId { get; set; }

    /// <summary>Positive when earned, negative when spent.</summary>
    public int Amount { get; set; }
    public PointsKind Kind { get; set; }

    public Guid? PostId { get; set; }

    /// <summary>Who caused it (the liker or award giver), if anyone.</summary>
    public string? SourceUserId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
