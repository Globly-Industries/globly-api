using System.ComponentModel.DataAnnotations;
using Globly.Api.Auth;
using Globly.Api.Common;
using Globly.Api.Data;
using Globly.Api.Notifications;
using Globly.Api.Posts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Globly.Api.Users;

public record UserProfileDto(
    string Id,
    string UserName,
    DateTime CreatedAt,
    int PostCount,
    int FollowerCount,
    int FollowingCount,
    /// <summary>Whether the caller follows this user (always false for guests and oneself).</summary>
    bool IsFollowing);

public record FollowResult(bool IsFollowing, int FollowerCount);

/// <summary>A user in a followers/following list.</summary>
public record FollowUserDto(
    string Id,
    string UserName,
    /// <summary>Whether the caller follows this user.</summary>
    bool IsFollowing);

/// <summary>Public user profiles and following.</summary>
[ApiController]
[Route("api/users")]
public class UsersController(AppDbContext db, PostService posts, NotificationService notifications, PushQueue push) : ControllerBase
{
    private const int MaxPageSize = 50;

    [HttpGet("{id}")]
    public async Task<ActionResult<UserProfileDto>> Get(string id, CancellationToken ct)
    {
        var me = User.GetUserId();
        var seesHidden = me == id || User.IsInRole(Roles.Admin);
        var profile = await db.Users.Where(u => u.Id == id)
            .Select(u => new UserProfileDto(
                u.Id,
                u.UserName ?? "",
                u.CreatedAt,
                u.Posts.Count(p => seesHidden || !p.IsHidden),
                db.Follows.Count(f => f.FolloweeId == u.Id),
                db.Follows.Count(f => f.FollowerId == u.Id),
                me != null && db.Follows.Any(f => f.FollowerId == me && f.FolloweeId == u.Id)))
            .FirstOrDefaultAsync(ct);
        return profile is null ? NotFound() : profile;
    }

    /// <summary>A user's posts, newest first. Hidden ones only for the user themself and admins.</summary>
    [HttpGet("{id}/posts")]
    public async Task<ActionResult<PagedResult<PostDto>>> Posts(
        string id,
        [Range(1, int.MaxValue)] int page = 1,
        [Range(1, MaxPageSize)] int pageSize = 20,
        CancellationToken ct = default)
    {
        var me = User.GetUserId();
        var source = me == id || User.IsInRole(Roles.Admin) ? db.Posts : posts.Visible;
        var q = source.Where(p => p.UserId == id).OrderByDescending(p => p.CreatedAt);
        return await posts.PageAsync(q, page, pageSize, me, ct);
    }

    [Authorize]
    [HttpPut("{id}/follow")]
    public async Task<ActionResult<FollowResult>> Follow(string id, CancellationToken ct)
    {
        var me = User.RequireUserId();
        if (me == id) return this.ProblemWithCode(StatusCodes.Status400BadRequest, ErrorCodes.CannotFollowSelf, "You can't follow yourself.");
        if (!await db.Users.AnyAsync(u => u.Id == id, ct)) return NotFound();

        // Following twice is a no-op, also when two requests race.
        var inserted = await db.Database.ExecuteSqlAsync($"""
            INSERT INTO "Follows" ("FollowerId", "FolloweeId", "CreatedAt")
            VALUES ({me}, {id}, {DateTime.UtcNow})
            ON CONFLICT DO NOTHING
            """, ct);
        if (inserted == 1 && await notifications.AddForFollowAsync(me, id, ct) is { } n)
        {
            await db.SaveChangesAsync(ct);
            push.Enqueue([n]);
        }
        return await Result(id, true, ct);
    }

    [Authorize]
    [HttpDelete("{id}/follow")]
    public async Task<ActionResult<FollowResult>> Unfollow(string id, CancellationToken ct)
    {
        var me = User.RequireUserId();
        await db.Follows.Where(f => f.FollowerId == me && f.FolloweeId == id).ExecuteDeleteAsync(ct);
        return await Result(id, false, ct);
    }

    /// <summary>Who follows this user, most recent first.</summary>
    [HttpGet("{id}/followers")]
    public Task<ActionResult<PagedResult<FollowUserDto>>> Followers(
        string id,
        [Range(1, int.MaxValue)] int page = 1,
        [Range(1, MaxPageSize)] int pageSize = 30,
        CancellationToken ct = default) =>
        FollowList(id, followers: true, page, pageSize, ct);

    /// <summary>Who this user follows, most recent first.</summary>
    [HttpGet("{id}/following")]
    public Task<ActionResult<PagedResult<FollowUserDto>>> Following(
        string id,
        [Range(1, int.MaxValue)] int page = 1,
        [Range(1, MaxPageSize)] int pageSize = 30,
        CancellationToken ct = default) =>
        FollowList(id, followers: false, page, pageSize, ct);

    private async Task<ActionResult<PagedResult<FollowUserDto>>> FollowList(
        string id, bool followers, int page, int pageSize, CancellationToken ct)
    {
        if (!await db.Users.AnyAsync(u => u.Id == id, ct)) return NotFound();
        var me = User.GetUserId();
        var items = await db.Follows
            .Where(f => followers ? f.FolloweeId == id : f.FollowerId == id)
            .OrderByDescending(f => f.CreatedAt)
            .Select(f => followers ? f.FollowerId : f.FolloweeId)
            .Select(uid => new FollowUserDto(
                uid,
                db.Users.Where(u => u.Id == uid).Select(u => u.UserName).FirstOrDefault() ?? "",
                me != null && db.Follows.Any(x => x.FollowerId == me && x.FolloweeId == uid)))
            .Skip((page - 1) * pageSize).Take(pageSize + 1)
            .ToListAsync(ct);
        var hasMore = items.Count > pageSize;
        if (hasMore) items.RemoveAt(items.Count - 1);
        return new PagedResult<FollowUserDto>(items, page, pageSize, hasMore);
    }

    private async Task<FollowResult> Result(string id, bool following, CancellationToken ct) =>
        new(following, await db.Follows.CountAsync(f => f.FolloweeId == id, ct));
}
