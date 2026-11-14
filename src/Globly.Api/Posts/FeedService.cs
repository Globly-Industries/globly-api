using Globly.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Globly.Api.Posts;

/// <summary>Ranked feed tabs: picks candidate posts in the database, ranks them with <see cref="FeedRanking"/>.</summary>
public class FeedService(AppDbContext db, PostService posts, GeoService geo)
{
    /// <summary>Most posts a tab ranks; the feed ends after these.</summary>
    public const int MaxCandidates = 1000;

    public record NearbyQuery(double Lat, double Lng, double RadiusKm);

    public async Task<PagedResult<PostDto>> GetPageAsync(
        FeedTab tab, PostFilter f, NearbyQuery? near, string? userId, int page, int pageSize, CancellationToken ct)
    {
        IQueryable<Data.Entities.Post> q;
        switch (tab)
        {
            case FeedTab.Nearby:
                // GeoService.Nearby is ordered nearest first, so the cap keeps the closest.
                q = PostService.ApplyFilter(geo.Nearby(near!.Lat, near.Lng, near.RadiusKm), f);
                break;
            case FeedTab.Following:
                if (userId is null) return new PagedResult<PostDto>([], page, pageSize, false);
                q = PostService.ApplyFilter(posts.Visible, f)
                    .Where(p => db.Follows.Any(x => x.FollowerId == userId && x.FolloweeId == p.UserId))
                    .OrderByDescending(p => p.CreatedAt);
                break;
            default:
                q = PostService.ApplyFilter(posts.Visible, f).OrderByDescending(p => p.CreatedAt);
                break;
        }

        var rows = await q.Take(MaxCandidates)
            .Select(p => new { p.Id, p.UserId, p.Upvotes, p.Downvotes, p.Views, p.CreatedAt, p.Location })
            .ToListAsync(ct);

        var candidates = rows.Select(r => new RankCandidate(
            r.Id, r.UserId, r.Upvotes, r.Downvotes, r.Views, r.CreatedAt,
            near is null ? null : FeedRanking.HaversineKm(near.Lat, near.Lng, r.Location.Y, r.Location.X))).ToList();

        IReadOnlySet<string> followed = userId is null || tab == FeedTab.Following
            ? new HashSet<string>()
            : (await db.Follows.Where(x => x.FollowerId == userId).Select(x => x.FolloweeId).ToListAsync(ct)).ToHashSet();

        var ranked = FeedRanking.Rank(candidates, tab, DateTime.UtcNow, followed, near?.RadiusKm ?? 50);
        var ids = ranked.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        var hasMore = ranked.Count > page * pageSize;

        var dtos = await posts.ToDtosAsync(db.Posts.Where(p => ids.Contains(p.Id)), userId, ct);
        var order = ids.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i);
        dtos.Sort((a, b) => order[a.Id].CompareTo(order[b.Id]));
        return new PagedResult<PostDto>(dtos, page, pageSize, hasMore);
    }
}
