using Globly.Api.Data;
using Globly.Api.Data.Entities;
using Globly.Api.Moderation;
using Globly.Api.Storage;
using ImageMagick;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Globly.Api.Users;

public enum AvatarProblem { None, NotFound, NotOwner, NoPicture, Hidden, Rejected, Unreadable }

/// <summary>Profile pictures: a square JPEG made from a fresh photo or one of the user's own posts.</summary>
public class AvatarService(
    AppDbContext db,
    IMediaStorage storage,
    NsfwClassifier classifier,
    IOptions<NsfwOptions> nsfw,
    IOptions<MediaOptions> media,
    ILogger<AvatarService> logger)
{
    public const int Size = 512;
    public const long MaxUploadBytes = 15 * 1024 * 1024;

    /// <summary>Uses an uploaded photo (taken in the app's camera).</summary>
    public async Task<(AvatarProblem, string? Url)> SetFromUploadAsync(string userId, Stream upload, CancellationToken ct)
    {
        MagickImage image;
        try
        {
            image = new MagickImage(upload);
        }
        catch (MagickException)
        {
            return (AvatarProblem.Unreadable, null);
        }
        using (image) return await StoreAsync(userId, image, ct);
    }

    /// <summary>Uses the picture of one of the user's own photo posts, or the first frame of a video post.</summary>
    public async Task<(AvatarProblem, string? Url)> SetFromPostAsync(string userId, Guid postId, CancellationToken ct)
    {
        var post = await db.Posts.AsNoTracking().Include(p => p.File).FirstOrDefaultAsync(p => p.Id == postId, ct);
        if (post is null) return (AvatarProblem.NotFound, null);
        if (post.UserId != userId) return (AvatarProblem.NotOwner, null);
        if (post.File is null || post.MediaType is not (MediaType.Image or MediaType.Video)) return (AvatarProblem.NoPicture, null);
        // A picture shown next to every comment must be one moderation has nothing against.
        if (post.IsHidden || post.NeedsReview || post.NsfwScore >= nsfw.Value.ReviewThreshold) return (AvatarProblem.Hidden, null);

        var work = Path.Combine(Path.GetTempPath(), $"globly-avatar-{Guid.NewGuid():N}");
        Directory.CreateDirectory(work);
        try
        {
            var file = Path.Combine(work, "media");
            await using (var stream = File.Create(file))
                await storage.DownloadAsync(post.File.ObjectKey, stream, ct);

            MagickImage image;
            if (post.MediaType == MediaType.Image)
            {
                var format = MediaSanitizer.ImageFormatOf(post.File.ContentType);
                if (format is null) return (AvatarProblem.Unreadable, null);
                image = new MagickImage(file, new MagickReadSettings { Format = format.Value, FrameIndex = 0, FrameCount = 1 });
            }
            else
            {
                var input = MediaSanitizer.FfmpegFormatOf(post.File.ContentType);
                if (input is null) return (AvatarProblem.Unreadable, null);
                var o = media.Value;
                var frame = await VideoFrames.FirstFrameAsync(
                    o.FfmpegPath, TimeSpan.FromSeconds(o.FfmpegTimeoutSeconds), input, file, work, ct);
                image = new MagickImage(frame, new MagickReadSettings { Format = MagickFormat.Png });
            }
            using (image) return await StoreAsync(userId, image, ct);
        }
        catch (Exception ex) when (ex is MagickException or InvalidOperationException)
        {
            logger.LogWarning(ex, "Avatar from post {PostId} failed", postId);
            return (AvatarProblem.Unreadable, null);
        }
        finally
        {
            try
            {
                Directory.Delete(work, recursive: true);
            }
            catch (IOException) { /* temp dir; cleaned up by the OS eventually */ }
        }
    }

    public async Task RemoveAsync(string userId, CancellationToken ct)
    {
        var old = await db.Users.Where(u => u.Id == userId).Select(u => u.AvatarKey).SingleAsync(ct);
        await db.Users.Where(u => u.Id == userId).ExecuteUpdateAsync(s => s.SetProperty(u => u.AvatarKey, (string?)null), ct);
        if (old is not null) await DeleteQuietly(old);
    }

    /// <summary>Deletes a user's picture object; their row is going away.</summary>
    public async Task DeleteForUserAsync(string userId, CancellationToken ct)
    {
        var key = await db.Users.Where(u => u.Id == userId).Select(u => u.AvatarKey).SingleOrDefaultAsync(ct);
        if (key is not null) await DeleteQuietly(key);
    }

    /// <summary>Upright, centre-cropped to a square of at most <see cref="Size"/>, JPEG without metadata.</summary>
    public static void MakeSquare(MagickImage image)
    {
        image.AutoOrient();
        // Re-encoding without metadata drops EXIF, including any GPS position.
        image.Strip();
        var side = Math.Min(image.Width, image.Height);
        image.Crop(new MagickGeometry((int)(image.Width - side) / 2, (int)(image.Height - side) / 2, side, side));
        image.ResetPage();
        if (side > Size) image.Resize(Size, Size);
        image.Format = MagickFormat.Jpeg;
        image.Quality = 85;
    }

    private async Task<(AvatarProblem, string?)> StoreAsync(string userId, MagickImage image, CancellationToken ct)
    {
        MakeSquare(image);

        if (classifier.IsAvailable)
        {
            var score = await Task.Run(() => classifier.Score(image), ct);
            if (score >= nsfw.Value.ReviewThreshold)
            {
                logger.LogInformation("Avatar for user {UserId} rejected by the NSFW filter ({Score:P0})", userId, score);
                return (AvatarProblem.Rejected, null);
            }
        }

        // A new key each time, so caches never show the old picture.
        var key = $"avatars/{userId}/{Guid.NewGuid():N}.jpg";
        using var content = new MemoryStream();
        await image.WriteAsync(content, ct);
        content.Position = 0;
        await storage.UploadAsync(key, content, "image/jpeg", ct);

        var old = await db.Users.Where(u => u.Id == userId).Select(u => u.AvatarKey).SingleAsync(ct);
        await db.Users.Where(u => u.Id == userId).ExecuteUpdateAsync(s => s.SetProperty(u => u.AvatarKey, key), ct);
        if (old is not null) await DeleteQuietly(old);
        return (AvatarProblem.None, storage.GetReadUrl(key));
    }

    private async Task DeleteQuietly(string key)
    {
        try
        {
            await storage.DeleteAsync(key, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to delete avatar object {ObjectKey}", key);
        }
    }
}

public static class AvatarUrls
{
    public static string? AvatarUrl(this IMediaStorage storage, string? key) => key is null ? null : storage.GetReadUrl(key);
}
