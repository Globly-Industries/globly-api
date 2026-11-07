using Globly.Api.Data;
using Globly.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Globly.Api.Moderation;

/// <summary>
/// Moderation driven by user ratings. A post is hidden automatically when
///   - at least <see cref="ModerationOptions.ReportThreshold"/> users report it, or
///   - it has at least <see cref="ModerationOptions.DownvoteHideMinVotes"/> votes and
///     <see cref="ModerationOptions.DownvoteHidePercent"/>% or more of them are downvotes.
/// Hidden posts disappear from the map and feed (the author and admins still see them) and wait in
/// the admin queue. An admin can restore a post, which clears its reports and stops the downvote rule
/// from hiding it again; new reports can still hide it.
/// </summary>
public class CommunityModeration(AppDbContext db, IOptions<ModerationOptions> options)
{
    private readonly ModerationOptions _opt = options.Value;

    /// <summary>Recounts votes and reports from their tables, then hides the post if a rule is met.</summary>
    public async Task ReevaluateAsync(Guid postId, CancellationToken ct = default)
    {
        await db.Posts.Where(p => p.Id == postId).ExecuteUpdateAsync(s => s
            .SetProperty(p => p.Upvotes, p => db.PostVotes.Count(v => v.PostId == postId && v.Value > 0))
            .SetProperty(p => p.Downvotes, p => db.PostVotes.Count(v => v.PostId == postId && v.Value < 0))
            .SetProperty(p => p.ReportCount, p => db.PostReports.Count(r => r.PostId == postId)), ct);

        var reportThreshold = _opt.ReportThreshold;
        var minVotes = _opt.DownvoteHideMinVotes;
        var percent = _opt.DownvoteHidePercent;

        await db.Posts
            .Where(p => p.Id == postId && !p.IsHidden)
            .Where(p => p.ReportCount >= reportThreshold
                || (!p.ModeratorApproved
                    && p.Upvotes + p.Downvotes >= minVotes
                    && p.Downvotes * 100 >= percent * (p.Upvotes + p.Downvotes)))
            .ExecuteUpdateAsync(s => s
                .SetProperty(p => p.IsHidden, true)
                .SetProperty(p => p.HiddenReason, p => p.ReportCount >= reportThreshold ? HiddenReason.Reports : HiddenReason.Downvotes)
                .SetProperty(p => p.HiddenAt, DateTime.UtcNow), ct);
    }
}
