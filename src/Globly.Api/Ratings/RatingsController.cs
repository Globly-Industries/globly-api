using System.ComponentModel.DataAnnotations;
using Globly.Api.Auth;
using Globly.Api.Common;
using Globly.Api.Data;
using Globly.Api.Data.Entities;
using Globly.Api.Moderation;
using Globly.Api.Notifications;
using Globly.Api.Posts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Globly.Api.Ratings;

public record VoteRequest([Range(-1, 1)] int Value);
public record VoteResult(int Upvotes, int Downvotes, int MyVote, bool IsHidden);

public record CreateCommentRequest([StringLength(1000)] string? Body);
public record CommentDto(Guid Id, Guid PostId, AuthorDto Author, string Body, DateTime CreatedAt);

public record ReportRequest([StringLength(500)] string? Reason);
public record ReportResult(bool IsHidden);

/// <summary>
/// Publication rating module: votes, comments and "needs manual moderation" reports.
/// Votes and reports feed <see cref="CommunityModeration"/>, which hides posts the community rejects.
/// </summary>
[ApiController]
[Route("api")]
public class RatingsController(AppDbContext db, IContentModerator moderator, CommunityModeration community, NotificationService notifications, PushQueue push) : ControllerBase
{
    /// <summary>Set your vote: 1 = up, -1 = down, 0 = remove.</summary>
    [Authorize]
    [HttpPut("posts/{postId:guid}/vote")]
    public async Task<ActionResult<VoteResult>> Vote(Guid postId, VoteRequest req, CancellationToken ct)
    {
        var userId = User.RequireUserId();
        if (await FindVisibleAsync(postId, ct) is null) return NotFound();

        var vote = await db.PostVotes.FindAsync([postId, userId], ct);
        if (req.Value == 0)
        {
            if (vote is not null) db.PostVotes.Remove(vote);
        }
        else if (vote is null)
        {
            db.PostVotes.Add(new PostVote { PostId = postId, UserId = userId, Value = (short)req.Value });
        }
        else
        {
            vote.Value = (short)req.Value;
        }
        await db.SaveChangesAsync(ct);
        await community.ReevaluateAsync(postId, ct);

        var post = await db.Posts.AsNoTracking().Where(p => p.Id == postId)
            .Select(p => new { p.Upvotes, p.Downvotes, p.IsHidden }).SingleAsync(ct);
        return new VoteResult(post.Upvotes, post.Downvotes, req.Value, post.IsHidden);
    }

    /// <summary>Text comments of a post, newest first.</summary>
    [HttpGet("posts/{postId:guid}/comments")]
    public async Task<ActionResult<PagedResult<CommentDto>>> GetComments(
        Guid postId,
        [Range(1, int.MaxValue)] int page = 1,
        [Range(1, 100)] int pageSize = 30,
        CancellationToken ct = default)
    {
        if (await FindVisibleAsync(postId, ct) is null) return NotFound();

        var items = await db.Comments.Where(c => c.PostId == postId)
            .OrderByDescending(c => c.CreatedAt)
            .Skip((page - 1) * pageSize).Take(pageSize + 1)
            .Select(c => new CommentDto(c.Id, c.PostId, new AuthorDto(c.UserId, c.User!.UserName!), c.Body, c.CreatedAt))
            .ToListAsync(ct);

        var hasMore = items.Count > pageSize;
        if (hasMore) items.RemoveAt(items.Count - 1);
        return new PagedResult<CommentDto>(items, page, pageSize, hasMore);
    }

    [Authorize]
    [HttpPost("posts/{postId:guid}/comments")]
    public async Task<ActionResult<CommentDto>> AddComment(Guid postId, CreateCommentRequest req, CancellationToken ct)
    {
        var body = string.IsNullOrWhiteSpace(req.Body) ? null : req.Body.Trim();
        if (body is null)
            return this.ProblemWithCode(StatusCodes.Status400BadRequest, ErrorCodes.CommentEmpty, "A comment needs text.");

        var check = moderator.CheckText(null, body);
        if (!check.Allowed) return this.ProblemWithCode(StatusCodes.Status422UnprocessableEntity, check.Code!, check.Reason!);

        var post = await FindVisibleAsync(postId, ct);
        if (post is null) return NotFound();

        var userId = User.RequireUserId();
        var comment = new Comment { PostId = postId, UserId = userId, Body = body };
        db.Comments.Add(comment);
        var created = await notifications.AddForCommentAsync(post, comment, ct);
        await db.SaveChangesAsync(ct);
        push.Enqueue(created);

        return new CommentDto(comment.Id, postId, new AuthorDto(userId, User.Identity?.Name ?? ""), body, comment.CreatedAt);
    }

    /// <summary>Legacy: emoji reactions were removed. Always empty, so older app versions still load posts.</summary>
    [HttpGet("posts/{postId:guid}/emoji")]
    public ActionResult<object[]> GetEmojiSummary(Guid postId) => Array.Empty<object>();

    [Authorize]
    [HttpDelete("comments/{id:guid}")]
    public async Task<IActionResult> DeleteComment(Guid id, CancellationToken ct)
    {
        var comment = await db.Comments.FindAsync([id], ct);
        if (comment is null) return NotFound();
        if (comment.UserId != User.GetUserId() && !User.IsInRole(Roles.Admin)) return Forbid();

        db.Comments.Remove(comment);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    /// <summary>
    /// Vote for a manual moderation check. Once enough users report a post it is hidden
    /// automatically and waits for an admin.
    /// </summary>
    [Authorize]
    [HttpPost("posts/{postId:guid}/report")]
    public async Task<ActionResult<ReportResult>> Report(Guid postId, ReportRequest req, CancellationToken ct)
    {
        var userId = User.RequireUserId();
        var post = await FindVisibleAsync(postId, ct);
        if (post is null) return NotFound();
        if (post.UserId == userId) return Forbid();

        if (await db.PostReports.FindAsync([postId, userId], ct) is null)
        {
            db.PostReports.Add(new PostReport { PostId = postId, UserId = userId, Reason = req.Reason?.Trim() });
            await db.SaveChangesAsync(ct);
            await community.ReevaluateAsync(postId, ct);
        }

        var hidden = await db.Posts.Where(p => p.Id == postId).Select(p => p.IsHidden).SingleAsync(ct);
        return new ReportResult(hidden);
    }

    /// <summary>The post if the caller may see it (hidden posts only for author/admins), otherwise null.</summary>
    private async Task<Post?> FindVisibleAsync(Guid postId, CancellationToken ct)
    {
        var post = await db.Posts.AsNoTracking().FirstOrDefaultAsync(p => p.Id == postId, ct);
        return post is not null && PostService.CanSee(post, User.GetUserId(), User.IsInRole(Roles.Admin)) ? post : null;
    }
}
