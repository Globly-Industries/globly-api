using Globly.Api.Data;
using Globly.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Globly.Api.Posts;

/// <summary>Ranked feed tabs: picks candidate posts in the database, ranks them with <see cref="FeedRanking"/>.</summary>
public class FeedService(AppDbContext db, PostService posts, GeoService geo)
{
    /// <summary>Most posts a tab ranks; the feed ends after these. Following takes this many of each kind.</summary>
    public const int MaxCandidates = 1000;

    /// <summary>Names a "Liked by" label shows.</summary>
    public const int LikedByNames = 2;

    public record NearbyQuery(double Lat, double Lng, double RadiusKm);

    public async Task<PagedResult<PostDto>> GetPageAsync(
        FeedTab tab, PostFilter f, NearbyQuery? near, string? userId, int page, int pageSize, CancellationToken ct)
    {
        // Nearby is about the place, so likes from follows don't count there.
        var likers = Likers(tab == FeedTab.Nearby ? null : userId);

        IQueryable<Data.Entities.Post> q;
        switch (tab)
        {
            case FeedTab.Nearby:
                // GeoService.Nearby is ordered nearest first, so the cap keeps the closest.
                q = PostService.ApplyFilter(geo.Nearby(near!.Lat, near.Lng, near.RadiusKm), f).Take(MaxCandidates);
                break;
            case FeedTab.Following:
                if (userId is null) return new PagedResult<PostDto>([], page, pageSize, false);
                var visible = PostService.ApplyFilter(posts.Visible, f);
                var authored = visible
                    .Where(p => db.Follows.Any(x => x.FollowerId == userId && x.FolloweeId == p.UserId))
                    .OrderByDescending(p => p.CreatedAt)
                    .Select(p => p.Id)
                    .Take(MaxCandidates);
                var liked = db.PostVotes.Where(v => v.Value > 0 && likers.Contains(v.UserId))
                    .GroupBy(v => v.PostId)
                    .OrderByDescending(g => g.Max(v => v.CreatedAt))
                    .Select(g => g.Key)
                    .Take(MaxCandidates);
                // Liked posts you made or already voted on aren't news to you.
                q = visible.Where(p => authored.Contains(p.Id)
                    || (liked.Contains(p.Id) && p.UserId != userId
                        && !db.PostVotes.Any(v => v.PostId == p.Id && v.UserId == userId)));
                break;
            default:
                q = PostService.ApplyFilter(posts.Visible, f).OrderByDescending(p => p.CreatedAt).Take(MaxCandidates);
                break;
        }

        var now = DateTime.UtcNow;
        var rows = await q
            .Select(p => new
            {
                p.Id, p.UserId, p.Upvotes, p.Downvotes, p.Views, p.CreatedAt, p.Location,
                Awards = db.PointsLedger.Count(x => x.Kind == PointsKind.AwardReceived && x.PostId == p.Id),
                FollowedLikes = db.PostVotes.Count(v => v.PostId == p.Id && v.Value > 0 && likers.Contains(v.UserId)),
                FollowedLikedAt = db.PostVotes.Where(v => v.PostId == p.Id && v.Value > 0 && likers.Contains(v.UserId))
                    .Max(v => (DateTime?)v.CreatedAt),
            })
            .ToListAsync(ct);

        var candidates = rows.Select(r => new RankCandidate(
            r.Id, r.UserId, r.Upvotes, r.Downvotes, r.Views, r.CreatedAt,
            near is null ? null : FeedRanking.HaversineKm(near.Lat, near.Lng, r.Location.Y, r.Location.X),
            r.Awards,
            r.FollowedLikes,
            r.FollowedLikedAt)).ToList();

        IReadOnlySet<string> followed = userId is null
            ? new HashSet<string>()
            : (await db.Follows.Where(x => x.FollowerId == userId).Select(x => x.FolloweeId).ToListAsync(ct)).ToHashSet();

        var ranked = FeedRanking.Rank(candidates, tab, now, followed, near?.RadiusKm ?? 50);
        var ids = ranked.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        var hasMore = ranked.Count > page * pageSize;

        var dtos = await posts.ToDtosAsync(db.Posts.Where(p => ids.Contains(p.Id)), userId, ct);
        var order = ids.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i);
        dtos.Sort((a, b) => order[a.Id].CompareTo(order[b.Id]));

        var likedBy = await LikedByAsync(ids, likers, ct);
        for (var i = 0; i < dtos.Count; i++)
            if (likedBy.TryGetValue(dtos[i].Id, out var lb)) dtos[i] = dtos[i] with { LikedBy = lb };

        return new PagedResult<PostDto>(dtos, page, pageSize, hasMore);
    }

    /// <summary>People the user follows who share their likes; none for guests.</summary>
    private IQueryable<string> Likers(string? userId) =>
        db.Users.Where(u => userId != null && u.ShareLikes
                && db.Follows.Any(x => x.FollowerId == userId && x.FolloweeId == u.Id))
            .Select(u => u.Id);

    private async Task<Dictionary<Guid, LikedByDto>> LikedByAsync(List<Guid> ids, IQueryable<string> likers, CancellationToken ct)
    {
        var rows = await db.PostVotes
            .Where(v => ids.Contains(v.PostId) && v.Value > 0 && likers.Contains(v.UserId))
            .OrderByDescending(v => v.CreatedAt)
            .Select(v => new { v.PostId, UserName = db.Users.Where(u => u.Id == v.UserId).Select(u => u.UserName).FirstOrDefault() })
            .ToListAsync(ct);
        return rows.GroupBy(r => r.PostId).ToDictionary(
            g => g.Key,
            g => new LikedByDto(g.Select(r => r.UserName ?? "").Take(LikedByNames).ToList(), g.Count()));
    }
}
