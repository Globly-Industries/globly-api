using System.ComponentModel.DataAnnotations;
using Globly.Api.Auth;
using Globly.Api.Data;
using Globly.Api.Data.Entities;
using Globly.Api.Moderation;
using Globly.Api.Posts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Globly.Api.Ratings;

public record VoteRequest([Range(-1, 1)] int Value);
public record VoteResult(int Upvotes, int Downvotes, int MyVote);

public record CreateCommentRequest([StringLength(1000)] string? Body, [StringLength(16)] string? Emoji);
public record CommentDto(Guid Id, Guid PostId, AuthorDto Author, string? Body, string? Emoji, DateTime CreatedAt);
public record EmojiCount(string Emoji, int Count);

public record ReportRequest([StringLength(500)] string? Reason);

public enum CommentKind { All, Text, Emoji }

/// <summary>Publication rating module: votes, comments, emoji and "needs manual moderation" reports.</summary>
[ApiController]
[Route("api")]
public class RatingsController(AppDbContext db, IContentModerator moderator, IOptions<ModerationOptions> modOptions) : ControllerBase
{
    /// <summary>Set your vote: 1 = up, -1 = down, 0 = remove.</summary>
    [Authorize]
    [HttpPut("posts/{postId:guid}/vote")]
    public async Task<ActionResult<VoteResult>> Vote(Guid postId, VoteRequest req, CancellationToken ct)
    {
        var userId = User.RequireUserId();
        if (!await db.Posts.AnyAsync(p => p.Id == postId, ct)) return NotFound();

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

        // Recount from the votes table so concurrent votes can't drift the totals.
        await db.Posts.Where(p => p.Id == postId).ExecuteUpdateAsync(s => s
            .SetProperty(p => p.Upvotes, p => db.PostVotes.Count(v => v.PostId == postId && v.Value > 0))
            .SetProperty(p => p.Downvotes, p => db.PostVotes.Count(v => v.PostId == postId && v.Value < 0)), ct);

        var counts = await db.Posts.Where(p => p.Id == postId)
            .Select(p => new { p.Upvotes, p.Downvotes }).SingleAsync(ct);
        return new VoteResult(counts.Upvotes, counts.Downvotes, req.Value);
    }

    /// <summary>Comments of a post, newest first. Filter by kind (text comments only / emoji only).</summary>
    [HttpGet("posts/{postId:guid}/comments")]
    public async Task<ActionResult<PagedResult<CommentDto>>> GetComments(
        Guid postId,
        CommentKind kind = CommentKind.All,
        [Range(1, int.MaxValue)] int page = 1,
        [Range(1, 100)] int pageSize = 30,
        CancellationToken ct = default)
    {
        var q = db.Comments.Where(c => c.PostId == postId);
        q = kind switch
        {
            CommentKind.Text => q.Where(c => c.Body != null),
            CommentKind.Emoji => q.Where(c => c.Emoji != null),
            _ => q,
        };

        var items = await q.OrderByDescending(c => c.CreatedAt)
            .Skip((page - 1) * pageSize).Take(pageSize + 1)
            .Select(c => new CommentDto(c.Id, c.PostId, new AuthorDto(c.UserId, c.User!.UserName!), c.Body, c.Emoji, c.CreatedAt))
            .ToListAsync(ct);

        var hasMore = items.Count > pageSize;
        if (hasMore) items.RemoveAt(items.Count - 1);
        return new PagedResult<CommentDto>(items, page, pageSize, hasMore);
    }

    /// <summary>Emoji totals for a post, most used first.</summary>
    [HttpGet("posts/{postId:guid}/emoji")]
    public async Task<ActionResult<List<EmojiCount>>> GetEmojiSummary(Guid postId, CancellationToken ct)
        => await db.Comments.Where(c => c.PostId == postId && c.Emoji != null)
            .GroupBy(c => c.Emoji!)
            .OrderByDescending(g => g.Count())
            .Select(g => new EmojiCount(g.Key, g.Count()))
            .ToListAsync(ct);

    [Authorize]
    [HttpPost("posts/{postId:guid}/comments")]
    public async Task<ActionResult<CommentDto>> AddComment(Guid postId, CreateCommentRequest req, CancellationToken ct)
    {
        var body = string.IsNullOrWhiteSpace(req.Body) ? null : req.Body.Trim();
        var emoji = string.IsNullOrWhiteSpace(req.Emoji) ? null : req.Emoji.Trim();
        if (body is null && emoji is null)
            return Problem("A comment needs text or an emoji.", statusCode: StatusCodes.Status400BadRequest);

        var check = moderator.CheckText(null, body);
        if (!check.Allowed) return Problem(check.Reason, statusCode: StatusCodes.Status422UnprocessableEntity);

        if (!await db.Posts.AnyAsync(p => p.Id == postId, ct)) return NotFound();

        var userId = User.RequireUserId();
        var comment = new Comment { PostId = postId, UserId = userId, Body = body, Emoji = emoji };
        db.Comments.Add(comment);
        await db.SaveChangesAsync(ct);

        return new CommentDto(comment.Id, postId, new AuthorDto(userId, User.Identity?.Name ?? ""), body, emoji, comment.CreatedAt);
    }

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

    /// <summary>Vote for a manual moderation check. Enough votes flag the post for admins.</summary>
    [Authorize]
    [HttpPost("posts/{postId:guid}/report")]
    public async Task<IActionResult> Report(Guid postId, ReportRequest req, CancellationToken ct)
    {
        var userId = User.RequireUserId();
        if (!await db.Posts.AnyAsync(p => p.Id == postId, ct)) return NotFound();

        if (await db.PostReports.FindAsync([postId, userId], ct) is null)
        {
            db.PostReports.Add(new PostReport { PostId = postId, UserId = userId, Reason = req.Reason?.Trim() });
            await db.SaveChangesAsync(ct);
        }

        var threshold = modOptions.Value.ReportThreshold;
        await db.Posts.Where(p => p.Id == postId).ExecuteUpdateAsync(s => s
            .SetProperty(p => p.ReportCount, p => db.PostReports.Count(r => r.PostId == postId))
            .SetProperty(p => p.IsFlagged, p => p.IsFlagged || db.PostReports.Count(r => r.PostId == postId) >= threshold), ct);

        return NoContent();
    }
}
