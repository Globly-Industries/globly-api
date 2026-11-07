using System.ComponentModel.DataAnnotations;
using Globly.Api.Auth;
using Globly.Api.Data;
using Globly.Api.Data.Entities;
using Globly.Api.Posts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Globly.Api.Admin;

public record AdminUserDto(string Id, string UserName, bool IsAdmin, int PostCount, DateTime CreatedAt);
public record ReportDto(string UserId, string? UserName, string? Reason, DateTime CreatedAt);

/// <summary>Administration module: moderation queue, post removal, user removal.</summary>
[ApiController]
[Route("api/admin")]
[Authorize(Roles = Roles.Admin)]
public class AdminController(AppDbContext db, PostService posts, UserManager<AppUser> users) : ControllerBase
{
    /// <summary>Posts for review. By default only those flagged by user reports, most reported first.</summary>
    [HttpGet("posts")]
    public async Task<ActionResult<PagedResult<PostDto>>> GetPosts(
        bool flaggedOnly = true,
        [Range(1, int.MaxValue)] int page = 1,
        [Range(1, 100)] int pageSize = 30,
        CancellationToken ct = default)
    {
        IQueryable<Post> q = db.Posts;
        q = flaggedOnly
            ? q.Where(p => p.IsFlagged).OrderByDescending(p => p.ReportCount).ThenByDescending(p => p.CreatedAt)
            : q.OrderByDescending(p => p.CreatedAt);

        var items = await posts.ToDtosAsync(q.Skip((page - 1) * pageSize).Take(pageSize + 1), User.GetUserId(), ct);
        var hasMore = items.Count > pageSize;
        if (hasMore) items.RemoveAt(items.Count - 1);
        return new PagedResult<PostDto>(items, page, pageSize, hasMore);
    }

    [HttpGet("posts/{id:guid}/reports")]
    public async Task<ActionResult<List<ReportDto>>> GetReports(Guid id, CancellationToken ct)
        => await db.PostReports.Where(r => r.PostId == id)
            .OrderByDescending(r => r.CreatedAt)
            .Select(r => new ReportDto(r.UserId, db.Users.Where(u => u.Id == r.UserId).Select(u => u.UserName).FirstOrDefault(), r.Reason, r.CreatedAt))
            .ToListAsync(ct);

    /// <summary>Post reviewed and found acceptable: clear its reports and remove the flag.</summary>
    [HttpPost("posts/{id:guid}/dismiss-reports")]
    public async Task<IActionResult> DismissReports(Guid id, CancellationToken ct)
    {
        if (!await db.Posts.AnyAsync(p => p.Id == id, ct)) return NotFound();

        await db.PostReports.Where(r => r.PostId == id).ExecuteDeleteAsync(ct);
        await db.Posts.Where(p => p.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.ReportCount, 0).SetProperty(p => p.IsFlagged, false), ct);
        return NoContent();
    }

    [HttpDelete("posts/{id:guid}")]
    public async Task<IActionResult> DeletePost(Guid id, CancellationToken ct)
    {
        var post = await db.Posts.FindAsync([id], ct);
        if (post is null) return NotFound();
        await posts.DeleteAsync(post, ct);
        return NoContent();
    }

    [HttpGet("users")]
    public async Task<ActionResult<PagedResult<AdminUserDto>>> GetUsers(
        string? search,
        [Range(1, int.MaxValue)] int page = 1,
        [Range(1, 100)] int pageSize = 30,
        CancellationToken ct = default)
    {
        var adminRoleId = await db.Roles.Where(r => r.Name == Roles.Admin).Select(r => r.Id).FirstOrDefaultAsync(ct);

        IQueryable<AppUser> q = db.Users;
        if (!string.IsNullOrWhiteSpace(search))
        {
            var normalized = search.Trim().ToUpperInvariant();
            q = q.Where(u => u.NormalizedUserName!.Contains(normalized));
        }

        var items = await q.OrderBy(u => u.UserName)
            .Skip((page - 1) * pageSize).Take(pageSize + 1)
            .Select(u => new AdminUserDto(
                u.Id,
                u.UserName!,
                db.UserRoles.Any(ur => ur.UserId == u.Id && ur.RoleId == adminRoleId),
                u.Posts.Count,
                u.CreatedAt))
            .ToListAsync(ct);

        var hasMore = items.Count > pageSize;
        if (hasMore) items.RemoveAt(items.Count - 1);
        return new PagedResult<AdminUserDto>(items, page, pageSize, hasMore);
    }

    /// <summary>Remove a user's registration and everything they posted (including R2 media).</summary>
    [HttpDelete("users/{id}")]
    public async Task<IActionResult> DeleteUser(string id, CancellationToken ct)
    {
        if (id == User.GetUserId())
            return Problem("You cannot delete your own account here.", statusCode: StatusCodes.Status400BadRequest);

        var user = await users.FindByIdAsync(id);
        if (user is null) return NotFound();

        await posts.DeleteUserMediaAsync(id, ct);
        var result = await users.DeleteAsync(user);
        if (!result.Succeeded)
            return Problem(string.Join(" ", result.Errors.Select(e => e.Description)));

        return NoContent();
    }
}
