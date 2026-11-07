using System.ComponentModel.DataAnnotations;
using Globly.Api.Data.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Globly.Api.Auth;

public record AuthRequest(
    [Required, StringLength(32, MinimumLength = 3)] string UserName,
    [Required, StringLength(128, MinimumLength = 8)] string Password);

public record UserDto(string Id, string UserName, bool IsAdmin, DateTime CreatedAt);

public record AuthResponse(string Token, DateTime ExpiresAt, UserDto User);

/// <summary>User authentication module: registration and login.</summary>
[ApiController]
[Route("api/auth")]
[EnableRateLimiting("auth")]
public class AuthController(UserManager<AppUser> users, TokenService tokens) : ControllerBase
{
    [HttpPost("register")]
    public async Task<ActionResult<AuthResponse>> Register(AuthRequest req)
    {
        if (await users.FindByNameAsync(req.UserName) is not null)
            return Problem("Username is already taken.", statusCode: StatusCodes.Status409Conflict);

        var user = new AppUser { UserName = req.UserName };
        var result = await users.CreateAsync(user, req.Password);
        if (!result.Succeeded)
            return ValidationProblem(ToModelState(result));

        return await IssueToken(user);
    }

    [HttpPost("login")]
    public async Task<ActionResult<AuthResponse>> Login(AuthRequest req)
    {
        var user = await users.FindByNameAsync(req.UserName);
        if (user is null)
            return Problem("Incorrect username or password.", statusCode: StatusCodes.Status401Unauthorized);

        if (await users.IsLockedOutAsync(user))
            return Problem("Too many failed attempts. Try again later.", statusCode: StatusCodes.Status423Locked);

        if (!await users.CheckPasswordAsync(user, req.Password))
        {
            await users.AccessFailedAsync(user);
            return Problem("Incorrect username or password.", statusCode: StatusCodes.Status401Unauthorized);
        }

        await users.ResetAccessFailedCountAsync(user);
        return await IssueToken(user);
    }

    [Authorize]
    [HttpGet("me")]
    public async Task<ActionResult<UserDto>> Me()
    {
        var user = await users.FindByIdAsync(User.RequireUserId());
        if (user is null) return Unauthorized();
        return await ToDto(user);
    }

    private async Task<AuthResponse> IssueToken(AppUser user)
    {
        var roles = await users.GetRolesAsync(user);
        var (token, expires) = tokens.Create(user, roles);
        return new AuthResponse(token, expires, new UserDto(user.Id, user.UserName!, roles.Contains(Roles.Admin), user.CreatedAt));
    }

    private async Task<UserDto> ToDto(AppUser user) =>
        new(user.Id, user.UserName!, await users.IsInRoleAsync(user, Roles.Admin), user.CreatedAt);

    private static Microsoft.AspNetCore.Mvc.ModelBinding.ModelStateDictionary ToModelState(IdentityResult result)
    {
        var ms = new Microsoft.AspNetCore.Mvc.ModelBinding.ModelStateDictionary();
        foreach (var e in result.Errors) ms.AddModelError(e.Code, e.Description);
        return ms;
    }
}
