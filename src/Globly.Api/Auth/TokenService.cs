using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Globly.Api.Data.Entities;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Globly.Api.Auth;

public static class Roles
{
    public const string Admin = "Admin";
}

public class JwtOptions
{
    public const string Section = "Jwt";

    public string Issuer { get; set; } = "globly-api";
    public string Audience { get; set; } = "globly-app";

    /// <summary>HMAC signing key, at least 32 characters. Keep it in user-secrets / environment variables.</summary>
    public string Key { get; set; } = "";

    public int ExpiryDays { get; set; } = 30;

    public SymmetricSecurityKey SigningKey => new(Encoding.UTF8.GetBytes(Key));
}

public class TokenService(IOptions<JwtOptions> options)
{
    private readonly JwtOptions _opt = options.Value;

    public (string Token, DateTime ExpiresAt) Create(AppUser user, IEnumerable<string> roles)
    {
        var expires = DateTime.UtcNow.AddDays(_opt.ExpiryDays);
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id),
            new(JwtRegisteredClaimNames.UniqueName, user.UserName!),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new("security_stamp", user.SecurityStamp ?? ""),
        };
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));

        var token = new JwtSecurityToken(
            issuer: _opt.Issuer,
            audience: _opt.Audience,
            claims: claims,
            expires: expires,
            signingCredentials: new SigningCredentials(_opt.SigningKey, SecurityAlgorithms.HmacSha256));

        return (new JwtSecurityTokenHandler().WriteToken(token), expires);
    }
}

public static class ClaimsPrincipalExtensions
{
    public static string? GetUserId(this ClaimsPrincipal user) =>
        user.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? user.FindFirstValue(ClaimTypes.NameIdentifier);

    public static string RequireUserId(this ClaimsPrincipal user) =>
        user.GetUserId() ?? throw new InvalidOperationException("Authenticated user has no id claim.");
}
