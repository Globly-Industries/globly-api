using System.ComponentModel.DataAnnotations;
using Globly.Api.Auth;
using Globly.Api.Common;
using Globly.Api.Data;
using Globly.Api.Data.Entities;
using Globly.Api.Moderation;
using Globly.Api.Storage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.EntityFrameworkCore;

namespace Globly.Api.Posts;

[ApiController]
[Route("api/posts")]
public class PostsController(
    AppDbContext db,
    PostService posts,
    GeoService geo,
    IMediaStorage storage,
    IMediaSanitizer sanitizer,
    IContentModerator moderator) : ControllerBase
{
    private const int MaxPageSize = 50;
    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    // ---------- Reading (anonymous allowed) ----------

    /// <summary>Points/clusters inside the visible map area.</summary>
    [HttpGet("map")]
    public async Task<ActionResult<List<MapPointDto>>> Map([FromQuery] MapQuery q, [FromQuery] PostFilter f, CancellationToken ct)
        => await geo.GetMapPointsAsync(q, f, ct);

    /// <summary>Closest posts to a location, nearest first.</summary>
    [HttpGet("nearby")]
    public async Task<ActionResult<List<PostDto>>> Nearby(
        [Range(-90, 90)] double lat,
        [Range(-180, 180)] double lng,
        [FromQuery] PostFilter f,
        [Range(0.1, 20000)] double radiusKm = 25,
        [Range(1, MaxPageSize)] int limit = 20,
        CancellationToken ct = default)
    {
        var q = PostService.ApplyFilter(geo.Nearby(lat, lng, radiusKm), f).Take(limit);
        return await posts.ToDtosAsync(q, User.GetUserId(), ct);
    }

    /// <summary>Chronological feed, newest first.</summary>
    [HttpGet("feed")]
    public async Task<ActionResult<PagedResult<PostDto>>> Feed(
        [FromQuery] PostFilter f,
        [Range(1, int.MaxValue)] int page = 1,
        [Range(1, MaxPageSize)] int pageSize = 20,
        CancellationToken ct = default)
    {
        var q = PostService.ApplyFilter(posts.Visible, f).OrderByDescending(p => p.CreatedAt);
        return await Page(q, page, pageSize, ct);
    }

    /// <summary>Posts of the signed-in user.</summary>
    [Authorize]
    [HttpGet("mine")]
    public async Task<ActionResult<PagedResult<PostDto>>> Mine(
        [Range(1, int.MaxValue)] int page = 1,
        [Range(1, MaxPageSize)] int pageSize = 20,
        CancellationToken ct = default)
    {
        var userId = User.RequireUserId();
        var q = db.Posts.Where(p => p.UserId == userId).OrderByDescending(p => p.CreatedAt);
        return await Page(q, page, pageSize, ct);
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<PostDto>> Get(Guid id, CancellationToken ct)
    {
        var post = await db.Posts.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);
        if (post is null || !PostService.CanSee(post, User.GetUserId(), User.IsInRole(Roles.Admin)))
            return NotFound();

        await db.Posts.Where(p => p.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.Views, p => p.Views + 1), ct);

        return (await posts.GetDtoAsync(id, User.GetUserId(), ct))!;
    }

    // ---------- Writing (registered users) ----------

    [Authorize]
    [HttpPost]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(100 * 1024 * 1024)]
    public async Task<ActionResult<PostDto>> Create([FromForm] CreatePostForm form, CancellationToken ct)
    {
        var userId = User.RequireUserId();

        var textCheck = moderator.CheckText(form.Title, form.Description);
        if (!textCheck.Allowed) return this.ProblemWithCode(StatusCodes.Status422UnprocessableEntity, textCheck.Code!, textCheck.Reason!);

        MediaFile? file = null;
        if (form.MediaType == MediaType.Text)
        {
            if (form.File is not null)
                return this.ProblemWithCode(StatusCodes.Status400BadRequest, ErrorCodes.InvalidFile, "Text posts cannot have a file.");
            if (string.IsNullOrWhiteSpace(form.Description))
                return this.ProblemWithCode(StatusCodes.Status400BadRequest, ErrorCodes.TextRequired, "Text posts need a description.");
        }
        else
        {
            if (form.File is null)
                return this.ProblemWithCode(StatusCodes.Status400BadRequest, ErrorCodes.FileRequired, $"{form.MediaType} posts need a file.");

            var contentType = ResolveContentType(form.File);
            var fileCheck = moderator.CheckFile(form.MediaType, contentType, form.File.Length);
            if (!fileCheck.Allowed) return this.ProblemWithCode(StatusCodes.Status422UnprocessableEntity, fileCheck.Code!, fileCheck.Reason!);

            var ext = Path.GetExtension(form.File.FileName).ToLowerInvariant();
            if (ext.Length > 10) ext = "";
            var key = $"posts/{DateTime.UtcNow:yyyy/MM}/{Guid.NewGuid():N}{ext}";

            // Only the cleaned copy is ever stored, so a failure here must stop the post.
            SanitizedMedia clean;
            try
            {
                await using var stream = form.File.OpenReadStream();
                clean = await sanitizer.SanitizeAsync(form.MediaType, contentType, stream, ct);
            }
            catch (MediaRejectedException ex)
            {
                return this.ProblemWithCode(StatusCodes.Status422UnprocessableEntity, ErrorCodes.InvalidFile, ex.Message);
            }

            await using (clean)
            {
                await storage.UploadAsync(key, clean.Content, contentType, ct);

                file = new MediaFile
                {
                    ObjectKey = key,
                    FileName = Path.GetFileName(form.File.FileName),
                    ContentType = contentType,
                    ContentLength = clean.Length,
                };
            }
        }

        var post = new Post
        {
            UserId = userId,
            Title = form.Title.Trim(),
            Description = form.Description?.Trim(),
            MediaType = form.MediaType,
            File = file,
            Location = GeoService.ToPoint(form.Latitude, form.Longitude),
            LocationRadiusMeters = form.LocationRadiusMeters,
        };
        db.Posts.Add(post);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch
        {
            if (file is not null) await posts.DeleteObjectQuietly(file.ObjectKey, CancellationToken.None);
            throw;
        }

        var dto = await posts.GetDtoAsync(post.Id, userId, ct);
        return CreatedAtAction(nameof(Get), new { id = post.Id }, dto);
    }

    /// <summary>Edit title/description. Allowed for the author and admins.</summary>
    [Authorize]
    [HttpPut("{id:guid}")]
    public async Task<ActionResult<PostDto>> Update(Guid id, UpdatePostRequest req, CancellationToken ct)
    {
        var post = await db.Posts.FindAsync([id], ct);
        if (post is null) return NotFound();
        if (!CanModify(post)) return Forbid();

        var check = moderator.CheckText(req.Title, req.Description);
        if (!check.Allowed) return this.ProblemWithCode(StatusCodes.Status422UnprocessableEntity, check.Code!, check.Reason!);

        post.Title = req.Title.Trim();
        post.Description = req.Description?.Trim();
        post.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        return (await posts.GetDtoAsync(id, User.GetUserId(), ct))!;
    }

    /// <summary>Delete a post and its media. Allowed for the author and admins.</summary>
    [Authorize]
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var post = await db.Posts.FindAsync([id], ct);
        if (post is null) return NotFound();
        if (!CanModify(post)) return Forbid();

        await posts.DeleteAsync(post, ct);
        return NoContent();
    }

    // ---------- Helpers ----------

    private bool CanModify(Post post) => post.UserId == User.GetUserId() || User.IsInRole(Roles.Admin);

    private async Task<PagedResult<PostDto>> Page(IQueryable<Post> q, int page, int pageSize, CancellationToken ct)
    {
        // Fetch one extra row to know whether another page exists.
        var items = await posts.ToDtosAsync(q.Skip((page - 1) * pageSize).Take(pageSize + 1), User.GetUserId(), ct);
        var hasMore = items.Count > pageSize;
        if (hasMore) items.RemoveAt(items.Count - 1);
        return new PagedResult<PostDto>(items, page, pageSize, hasMore);
    }

    private static string ResolveContentType(IFormFile file)
    {
        var ct = file.ContentType;
        if (string.IsNullOrWhiteSpace(ct) || ct == "application/octet-stream")
            ct = ContentTypes.TryGetContentType(file.FileName, out var guessed) ? guessed : "application/octet-stream";
        return ct.ToLowerInvariant();
    }
}
