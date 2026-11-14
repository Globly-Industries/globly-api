using System.ComponentModel.DataAnnotations;
using Globly.Api.Auth;
using Globly.Api.Data;
using Globly.Api.Data.Entities;
using Globly.Api.Posts;
using Globly.Api.Storage;
using Globly.Api.Users;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Globly.Api.Notifications;

/// <summary>Post and comment fields are null for Follow notifications.</summary>
public record NotificationDto(
    Guid Id,
    NotificationType Type,
    AuthorDto Actor,
    Guid? PostId,
    string? PostTitle,
    Guid? CommentId,
    string? CommentBody,
    DateTime CreatedAt,
    bool IsRead);

public record UnreadCountDto(int Count);

[ApiController]
[Route("api/notifications")]
[Authorize]
public class NotificationsController(AppDbContext db, IMediaStorage storage) : ControllerBase
{
    /// <summary>
    /// The caller's notifications, newest first. Follow and award notifications only when asked for
    /// (follows/awards=true): older apps can't show them.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<PagedResult<NotificationDto>>> Get(
        bool follows = false,
        bool awards = false,
        [Range(1, int.MaxValue)] int page = 1,
        [Range(1, 100)] int pageSize = 30,
        CancellationToken ct = default)
    {
        var items = await Mine(follows, awards)
            .OrderByDescending(n => n.CreatedAt)
            .Skip((page - 1) * pageSize).Take(pageSize + 1)
            .Select(n => new
            {
                n.Id, n.Type, n.ActorId, ActorName = n.Actor!.UserName!, n.Actor.AvatarKey,
                n.PostId, PostTitle = n.Post != null ? n.Post.Title : null,
                n.CommentId, CommentBody = n.Comment != null ? n.Comment.Body : null,
                n.CreatedAt, IsRead = n.ReadAt != null,
            })
            .ToListAsync(ct);
        var dtos = items.Select(n => new NotificationDto(
            n.Id, n.Type, new AuthorDto(n.ActorId, n.ActorName, storage.AvatarUrl(n.AvatarKey)),
            n.PostId, n.PostTitle, n.CommentId, n.CommentBody, n.CreatedAt, n.IsRead)).ToList();

        var hasMore = dtos.Count > pageSize;
        if (hasMore) dtos.RemoveAt(dtos.Count - 1);
        return new PagedResult<NotificationDto>(dtos, page, pageSize, hasMore);
    }

    [HttpGet("unread-count")]
    public async Task<UnreadCountDto> UnreadCount(bool follows = false, bool awards = false, CancellationToken ct = default) =>
        new(await Mine(follows, awards).CountAsync(n => n.ReadAt == null, ct));

    [HttpPost("{id:guid}/read")]
    public async Task<IActionResult> MarkRead(Guid id, CancellationToken ct)
    {
        var userId = User.RequireUserId();
        await db.Notifications.Where(n => n.Id == id && n.RecipientId == userId && n.ReadAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.ReadAt, DateTime.UtcNow), ct);
        return NoContent();
    }

    [HttpPost("read-all")]
    public async Task<IActionResult> MarkAllRead(CancellationToken ct)
    {
        var userId = User.RequireUserId();
        await db.Notifications.Where(n => n.RecipientId == userId && n.ReadAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.ReadAt, DateTime.UtcNow), ct);
        return NoContent();
    }

    /// <summary>The caller's notifications on posts they can still see, and follows/awards if asked for.</summary>
    private IQueryable<Notification> Mine(bool follows, bool awards)
    {
        var userId = User.RequireUserId();
        var isAdmin = User.IsInRole(Roles.Admin);
        return db.Notifications.Where(n => n.RecipientId == userId
            && (n.Type == NotificationType.Follow
                ? follows
                : (n.Type != NotificationType.Award || awards)
                  && (!n.Post!.IsHidden || isAdmin || n.Post.UserId == userId)));
    }
}
