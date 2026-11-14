using Microsoft.AspNetCore.Identity;

namespace Globly.Api.Data.Entities;

/// <summary>Registered user (AspNetUsers table).</summary>
public class AppUser : IdentityUser
{
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Spendable points: the sum of the user's PointsLedger entries, never below 0.</summary>
    public int Points { get; set; }

    /// <summary>Storage key of the profile picture (a square JPEG); null shows initials.</summary>
    public string? AvatarKey { get; set; }

    /// <summary>Whether followers see this user's likes in their feeds.</summary>
    public bool ShareLikes { get; set; } = true;

    public List<Post> Posts { get; set; } = [];
}
