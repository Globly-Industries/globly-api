using System.Threading.Channels;
using Globly.Api.Data;
using Globly.Api.Data.Entities;
using Globly.Api.Storage;
using ImageMagick;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Globly.Api.Moderation;

public enum NsfwVerdict { Fine, Review, Hide }

public static class NsfwRules
{
    public static NsfwVerdict Decide(float score, NsfwOptions o) =>
        score >= o.HideThreshold ? NsfwVerdict.Hide
        : score >= o.ReviewThreshold ? NsfwVerdict.Review
        : NsfwVerdict.Fine;
}

/// <summary>Wakes the scanner as soon as a post with media is published.</summary>
public sealed class NsfwScanSignal
{
    private readonly Channel<bool> _channel = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    public void Notify() => _channel.Writer.TryWrite(true);

    /// <summary>Returns when notified or after <paramref name="timeout"/>.</summary>
    public async Task WaitAsync(TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await _channel.Reader.ReadAsync(cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
        }
    }
}

/// <summary>
/// Scores image and video posts after they are published, newest first, so uploads never wait for
/// it. Also picks up posts from before the scanner existed and any missed while the API was down.
/// Very likely explicit media is hidden for review; doubtful media is only queued for review.
/// Audio is not scanned (reports and downvotes cover it).
/// </summary>
public sealed class NsfwScanner(
    IServiceScopeFactory scopes,
    NsfwClassifier classifier,
    IMediaStorage storage,
    NsfwScanSignal signal,
    IOptions<NsfwOptions> options,
    IOptions<MediaOptions> mediaOptions,
    ILogger<NsfwScanner> logger) : BackgroundService
{
    private readonly NsfwOptions _o = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!classifier.IsAvailable) return;
        logger.LogInformation("NSFW scanner started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Keep going while there is a backlog; sleep once it is empty.
                while (await ScanBatchAsync(stoppingToken) > 0) { }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "NSFW scan batch failed");
            }
            await signal.WaitAsync(TimeSpan.FromSeconds(_o.PollSeconds), stoppingToken);
        }
    }

    private async Task<int> ScanBatchAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var batch = await db.Posts
            .Where(p => p.NsfwScannedAt == null && p.NsfwScanAttempts < _o.MaxAttempts && p.File != null
                && (p.MediaType == MediaType.Image || p.MediaType == MediaType.Video))
            .OrderByDescending(p => p.CreatedAt)
            .Select(p => new { p.Id, p.MediaType, p.File!.ObjectKey, p.File.ContentType })
            .Take(10)
            .ToListAsync(ct);

        foreach (var post in batch)
        {
            float score;
            try
            {
                score = await ScoreAsync(post.MediaType, post.ObjectKey, post.ContentType, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "NSFW scan of post {PostId} failed", post.Id);
                DateTime? now = DateTime.UtcNow;
                var maxAttempts = _o.MaxAttempts;
                await db.Posts.Where(p => p.Id == post.Id).ExecuteUpdateAsync(s => s
                    .SetProperty(p => p.NsfwScanAttempts, p => p.NsfwScanAttempts + 1)
                    // Out of attempts: leave it to reports instead of trying forever.
                    .SetProperty(p => p.NsfwScannedAt, p => p.NsfwScanAttempts + 1 >= maxAttempts ? now : null), ct);
                continue;
            }

            await ApplyAsync(db, post.Id, score, ct);
        }
        return batch.Count;
    }

    private async Task ApplyAsync(AppDbContext db, Guid postId, float score, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var verdict = NsfwRules.Decide(score, _o);
        await db.Posts.Where(p => p.Id == postId).ExecuteUpdateAsync(s => s
            .SetProperty(p => p.NsfwScore, score)
            .SetProperty(p => p.NsfwScannedAt, now)
            .SetProperty(p => p.NeedsReview, p => p.NeedsReview || (verdict == NsfwVerdict.Review && !p.ModeratorApproved)), ct);

        if (verdict == NsfwVerdict.Hide)
        {
            // Never overrides an admin's decision or another reason the post is already hidden for.
            var hidden = await db.Posts.Where(p => p.Id == postId && !p.IsHidden && !p.ModeratorApproved)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(p => p.IsHidden, true)
                    .SetProperty(p => p.HiddenReason, HiddenReason.Automatic)
                    .SetProperty(p => p.HiddenAt, now), ct);
            if (hidden > 0) logger.LogInformation("Post {PostId} hidden by the NSFW filter ({Score:P0})", postId, score);
        }
        else if (verdict == NsfwVerdict.Review)
        {
            logger.LogInformation("Post {PostId} queued for review by the NSFW filter ({Score:P0})", postId, score);
        }
        else
        {
            logger.LogInformation("Post {PostId} passed the NSFW filter ({Score:P0})", postId, score);
        }
    }

    private async Task<float> ScoreAsync(MediaType type, string objectKey, string contentType, CancellationToken ct)
    {
        var work = Path.Combine(Path.GetTempPath(), $"globly-scan-{Guid.NewGuid():N}");
        Directory.CreateDirectory(work);
        try
        {
            var file = Path.Combine(work, "media");
            await using (var stream = File.Create(file))
                await storage.DownloadAsync(objectKey, stream, ct);

            if (type == MediaType.Image)
            {
                var format = MediaSanitizer.ImageFormatOf(contentType)
                    ?? throw new InvalidOperationException($"Unexpected image type '{contentType}'.");
                return await Task.Run(() => classifier.ScoreImageFile(file, format, _o.MaxImageFrames), ct);
            }

            var input = MediaSanitizer.FfmpegFormatOf(contentType)
                ?? throw new InvalidOperationException($"Unexpected video type '{contentType}'.");
            var media = mediaOptions.Value;
            var frames = await VideoFrames.ExtractAsync(
                media.FfmpegPath, TimeSpan.FromSeconds(media.FfmpegTimeoutSeconds),
                input, file, work, _o.VideoFrameIntervalSeconds, _o.MaxVideoFrames, ct);

            var best = 0f;
            foreach (var frame in frames)
            {
                using var image = new MagickImage(frame, new MagickReadSettings { Format = MagickFormat.Png });
                best = Math.Max(best, await Task.Run(() => classifier.Score(image), ct));
                if (best >= _o.HideThreshold) break; // The verdict can't get any worse.
            }
            return best;
        }
        finally
        {
            try
            {
                Directory.Delete(work, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
