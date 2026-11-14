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
    bool IsFollowing,
    string? AvatarUrl = null);

public record FollowResult(bool IsFollowing, int FollowerCount);

/// <summary>A user in a followers/following list.</summary>
public record FollowUserDto(
    string Id,
    string UserName,
    /// <summary>Whether the caller follows this user.</summary>
    bool IsFollowing,
    string? AvatarUrl = null);

public record AvatarResult(string? AvatarUrl);

public class AvatarForm
{
    public IFormFile? File { get; set; }
}

/// <summary>Public user profiles and following.</summary>
[ApiController]
[Route("api/users")]
public class UsersController(
    AppDbContext db,
    PostService posts,
    NotificationService notifications,
    PushQueue push,
    AvatarService avatars,
    Storage.IMediaStorage storage) : ControllerBase
{
    private const int MaxPageSize = 50;

    [HttpGet("{id}")]
    public async Task<ActionResult<UserProfileDto>> Get(string id, CancellationToken ct)
    {
        var me = User.GetUserId();
        var seesHidden = me == id || User.IsInRole(Roles.Admin);
        var profile = await db.Users.Where(u => u.Id == id)
            .Select(u => new
            {
                Dto = new UserProfileDto(
                    u.Id,
                    u.UserName ?? "",
                    u.CreatedAt,
                    u.Posts.Count(p => seesHidden || !p.IsHidden),
                    db.Follows.Count(f => f.FolloweeId == u.Id),
                    db.Follows.Count(f => f.FollowerId == u.Id),
                    me != null && db.Follows.Any(f => f.FollowerId == me && f.FolloweeId == u.Id),
                    null),
                u.AvatarKey,
            })
            .FirstOrDefaultAsync(ct);
        return profile is null ? NotFound() : profile.Dto with { AvatarUrl = storage.AvatarUrl(profile.AvatarKey) };
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
        var rows = await db.Follows
            .Where(f => followers ? f.FolloweeId == id : f.FollowerId == id)
            .OrderByDescending(f => f.CreatedAt)
            .Select(f => followers ? f.FollowerId : f.FolloweeId)
            .Select(uid => new
            {
                Id = uid,
                UserName = db.Users.Where(u => u.Id == uid).Select(u => u.UserName).FirstOrDefault() ?? "",
                AvatarKey = db.Users.Where(u => u.Id == uid).Select(u => u.AvatarKey).FirstOrDefault(),
                IsFollowing = me != null && db.Follows.Any(x => x.FollowerId == me && x.FolloweeId == uid),
            })
            .Skip((page - 1) * pageSize).Take(pageSize + 1)
            .ToListAsync(ct);
        var items = rows.Select(r => new FollowUserDto(r.Id, r.UserName, r.IsFollowing, storage.AvatarUrl(r.AvatarKey))).ToList();
        var hasMore = items.Count > pageSize;
        if (hasMore) items.RemoveAt(items.Count - 1);
        return new PagedResult<FollowUserDto>(items, page, pageSize, hasMore);
    }

    /// <summary>Sets the profile picture from a photo just taken in the app.</summary>
    [Authorize]
    [HttpPut("me/avatar")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(AvatarService.MaxUploadBytes)]
    public async Task<ActionResult<AvatarResult>> SetAvatar([FromForm] AvatarForm form, CancellationToken ct)
    {
        if (form.File is not { Length: > 0 } file)
            return this.ProblemWithCode(StatusCodes.Status400BadRequest, ErrorCodes.FileRequired, "Send a photo.");
        if (!file.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            return this.ProblemWithCode(StatusCodes.Status422UnprocessableEntity, ErrorCodes.InvalidFile, "Only photos can be profile pictures.");

        await using var stream = file.OpenReadStream();
        return AvatarOutcome(await avatars.SetFromUploadAsync(User.RequireUserId(), stream, ct));
    }

    /// <summary>Sets the profile picture from one of your own photo or video posts.</summary>
    [Authorize]
    [HttpPut("me/avatar/from-post/{postId:guid}")]
    public async Task<ActionResult<AvatarResult>> SetAvatarFromPost(Guid postId, CancellationToken ct) =>
        AvatarOutcome(await avatars.SetFromPostAsync(User.RequireUserId(), postId, ct));

    [Authorize]
    [HttpDelete("me/avatar")]
    public async Task<IActionResult> RemoveAvatar(CancellationToken ct)
    {
        await avatars.RemoveAsync(User.RequireUserId(), ct);
        return NoContent();
    }

    private ActionResult<AvatarResult> AvatarOutcome((AvatarProblem Problem, string? Url) r) => r.Problem switch
    {
        AvatarProblem.None => new AvatarResult(r.Url),
        AvatarProblem.NotFound => NotFound(),
        AvatarProblem.NotOwner => Forbid(),
        AvatarProblem.NoPicture => this.ProblemWithCode(StatusCodes.Status422UnprocessableEntity, ErrorCodes.InvalidFile,
            "Only photo and video posts can be profile pictures."),
        AvatarProblem.Hidden => this.ProblemWithCode(StatusCodes.Status409Conflict, ErrorCodes.PostHidden,
            "Hidden or flagged posts can't be profile pictures."),
        AvatarProblem.Rejected => this.ProblemWithCode(StatusCodes.Status422UnprocessableEntity, ErrorCodes.ContentRejected,
            "This picture can't be used."),
        _ => this.ProblemWithCode(StatusCodes.Status422UnprocessableEntity, ErrorCodes.InvalidFile, "The picture couldn't be read."),
    };

    private async Task<FollowResult> Result(string id, bool following, CancellationToken ct) =>
        new(following, await db.Follows.CountAsync(f => f.FolloweeId == id, ct));
}
