using System.ComponentModel.DataAnnotations;
using Globly.Api.Auth;
using Globly.Api.Common;
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
    /// <summary>
    /// Moderation queue. By default only hidden posts (hidden by community moderation or an admin),
    /// most reported first. With hiddenOnly=false: all posts, newest first.
    /// </summary>
    [HttpGet("posts")]
    public async Task<ActionResult<PagedResult<PostDto>>> GetPosts(
        bool hiddenOnly = true,
        [Range(1, int.MaxValue)] int page = 1,
        [Range(1, 100)] int pageSize = 30,
        CancellationToken ct = default)
    {
        IQueryable<Post> q = db.Posts;
        q = hiddenOnly
            ? q.Where(p => p.IsHidden).OrderByDescending(p => p.ReportCount).ThenByDescending(p => p.HiddenAt)
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

    /// <summary>
    /// Post reviewed and found acceptable: make it visible again, clear its reports, and stop the
    /// downvote rule from hiding it again. New reports can still hide it.
    /// </summary>
    [HttpPost("posts/{id:guid}/restore")]
    public async Task<IActionResult> Restore(Guid id, CancellationToken ct)
    {
        if (!await db.Posts.AnyAsync(p => p.Id == id, ct)) return NotFound();

        await db.PostReports.Where(r => r.PostId == id).ExecuteDeleteAsync(ct);
        await db.Posts.Where(p => p.Id == id).ExecuteUpdateAsync(s => s
            .SetProperty(p => p.ReportCount, 0)
            .SetProperty(p => p.IsHidden, false)
            .SetProperty(p => p.HiddenReason, HiddenReason.None)
            .SetProperty(p => p.HiddenAt, (DateTime?)null)
            .SetProperty(p => p.ModeratorApproved, true), ct);
        return NoContent();
    }

    /// <summary>Hide a post manually (it stays in the queue until restored or deleted).</summary>
    [HttpPost("posts/{id:guid}/hide")]
    public async Task<IActionResult> Hide(Guid id, CancellationToken ct)
    {
        var updated = await db.Posts.Where(p => p.Id == id).ExecuteUpdateAsync(s => s
            .SetProperty(p => p.IsHidden, true)
            .SetProperty(p => p.HiddenReason, HiddenReason.Admin)
            .SetProperty(p => p.HiddenAt, DateTime.UtcNow), ct);
        return updated == 0 ? NotFound() : NoContent();
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
            return this.ProblemWithCode(StatusCodes.Status400BadRequest, ErrorCodes.CannotDeleteSelf, "You cannot delete your own account here.");

        var user = await users.FindByIdAsync(id);
        if (user is null) return NotFound();

        await posts.DeleteUserMediaAsync(id, ct);
        var result = await users.DeleteAsync(user);
        if (!result.Succeeded)
            return Problem(string.Join(" ", result.Errors.Select(e => e.Description)));

        return NoContent();
    }
}
