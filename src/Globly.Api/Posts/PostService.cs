using Globly.Api.Data;
using Globly.Api.Data.Entities;
using Globly.Api.Storage;
using Microsoft.EntityFrameworkCore;

namespace Globly.Api.Posts;

/// <summary>Content processing: shared queries, DTO mapping and deletion (DB + R2).</summary>
public class PostService(AppDbContext db, IMediaStorage storage, ILogger<PostService> logger)
{
    /// <summary>Posts everyone may see (not hidden by moderation).</summary>
    public IQueryable<Post> Visible => db.Posts.Where(p => !p.IsHidden);

    /// <summary>Hidden posts are only shown to their author and admins.</summary>
    public static bool CanSee(Post post, string? userId, bool isAdmin) =>
        !post.IsHidden || isAdmin || post.UserId == userId;

    public static IQueryable<Post> ApplyFilter(IQueryable<Post> q, PostFilter? f)
    {
        if (f is null) return q;
        if (f.Types is { Length: > 0 } types) q = q.Where(p => types.Contains(p.MediaType));
        if (f.Since is { } since)
        {
            var utc = since.ToUniversalTime();
            q = q.Where(p => p.CreatedAt >= utc);
        }
        if (f.MinScore is { } min) q = q.Where(p => p.Upvotes - p.Downvotes >= min);
        return q;
    }

    /// <summary>Projects posts to DTOs in one query. Ordering/paging must already be applied.</summary>
    public async Task<List<PostDto>> ToDtosAsync(IQueryable<Post> q, string? currentUserId, CancellationToken ct = default)
    {
        var rows = await q.Select(p => new
        {
            p.Id,
            p.UserId,
            UserName = p.User!.UserName,
            p.Title,
            p.Description,
            p.MediaType,
            ObjectKey = p.File != null ? p.File.ObjectKey : null,
            ContentType = p.File != null ? p.File.ContentType : null,
            ContentLength = p.File != null ? p.File.ContentLength : 0,
            p.Location,
            p.Views,
            p.Upvotes,
            p.Downvotes,
            CommentCount = p.Comments.Count,
            MyVote = currentUserId == null
                ? 0
                : db.PostVotes.Where(v => v.PostId == p.Id && v.UserId == currentUserId).Select(v => (int)v.Value).FirstOrDefault(),
            p.ReportCount,
            p.IsHidden,
            p.HiddenReason,
            p.CreatedAt,
            p.UpdatedAt,
        }).ToListAsync(ct);

        return rows.Select(r => new PostDto(
            r.Id,
            new AuthorDto(r.UserId, r.UserName ?? "[deleted]"),
            r.Title,
            r.Description,
            r.MediaType,
            r.ObjectKey is null ? null : new MediaDto(storage.GetReadUrl(r.ObjectKey), r.ContentType!, r.ContentLength),
            r.Location.Y,
            r.Location.X,
            r.Views,
            r.Upvotes,
            r.Downvotes,
            r.CommentCount,
            r.MyVote,
            r.ReportCount,
            r.IsHidden,
            r.HiddenReason,
            r.CreatedAt,
            r.UpdatedAt)).ToList();
    }

    public async Task<PostDto?> GetDtoAsync(Guid id, string? currentUserId, CancellationToken ct = default) =>
        (await ToDtosAsync(db.Posts.Where(p => p.Id == id), currentUserId, ct)).FirstOrDefault();

    /// <summary>Deletes the post row (comments/votes/reports cascade) and its media object.</summary>
    public async Task DeleteAsync(Post post, CancellationToken ct = default)
    {
        var file = post.FileId is { } fid ? await db.Files.FindAsync([fid], ct) : null;

        db.Posts.Remove(post);
        if (file is not null) db.Files.Remove(file);
        await db.SaveChangesAsync(ct);

        if (file is not null) await DeleteObjectQuietly(file.ObjectKey, ct);
    }

    /// <summary>Deletes all media objects belonging to a user's posts (DB rows are removed by cascade).</summary>
    public async Task DeleteUserMediaAsync(string userId, CancellationToken ct = default)
    {
        var files = await db.Posts.Where(p => p.UserId == userId && p.File != null)
            .Select(p => p.File!).ToListAsync(ct);

        db.Files.RemoveRange(files);
        await db.SaveChangesAsync(ct);

        foreach (var f in files) await DeleteObjectQuietly(f.ObjectKey, ct);
    }

    public async Task DeleteObjectQuietly(string objectKey, CancellationToken ct = default)
    {
        try
        {
            await storage.DeleteAsync(objectKey, ct);
        }
        catch (Exception ex)
        {
            // An orphaned object is harmless; don't fail the request after the DB change committed.
            logger.LogWarning(ex, "Failed to delete media object {ObjectKey}", objectKey);
        }
    }
}
