using Microsoft.AspNetCore.Identity;

namespace Globly.Api.Data.Entities;

/// <summary>Registered user (AspNetUsers table).</summary>
public class AppUser : IdentityUser
{
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Spendable points: the sum of the user's PointsLedger entries, never below 0.</summary>
    public int Points { get; set; }

    public List<Post> Posts { get; set; } = [];
}
