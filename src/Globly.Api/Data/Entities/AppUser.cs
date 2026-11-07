using Microsoft.AspNetCore.Identity;

namespace Globly.Api.Data.Entities;

/// <summary>Registered user (AspNetUsers table).</summary>
public class AppUser : IdentityUser
{
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public List<Post> Posts { get; set; } = [];
}
