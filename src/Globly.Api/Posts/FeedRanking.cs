namespace Globly.Api.Posts;

public enum FeedTab
{
    /// <summary>Popular posts, favouring new ones.</summary>
    Recent = 0,
    /// <summary>Popular posts, favouring close ones.</summary>
    Nearby = 1,
    /// <summary>Only posts by people the user follows, favouring new ones.</summary>
    Following = 2,
}

/// <summary>A post's ranking inputs.</summary>
public record RankCandidate(
    Guid Id,
    string AuthorId,
    int Upvotes,
    int Downvotes,
    int Views,
    DateTime CreatedAt,
    double? DistanceKm = null,
    bool Spotlit = false,
    int Awards = 0);

/// <summary>
/// How much each signal counts. Likes, Views and Tab add up to 1; FollowBonus is added on top
/// for posts by people the user follows, SpotlightBonus for spotlit posts.
/// </summary>
public record RankWeights(double Likes, double Views, double Tab, double FollowBonus, double SpotlightBonus);

/// <summary>
/// Scores feed posts. Every signal is turned into 0-1 (likes -1-1) and weighted:
/// likes = net votes, log-scaled against the most liked candidate;
/// views = log-scaled against the most viewed candidate;
/// tab = recency (halves every <see cref="RecencyHalfLifeHours"/>) or closeness (halves at a tenth of the radius).
/// </summary>
public static class FeedRanking
{
    // Views weigh less than likes: a like is a deliberate choice, a view mostly means the post was shown.
    // The tab's own signal gets half, otherwise an old hit would top "Recent" forever.
    // A spotlight gets a post seen, not liked: about a day of freshness, so a weak post still sinks.
    public static readonly RankWeights Recent = new(Likes: 0.35, Views: 0.15, Tab: 0.50, FollowBonus: 0.15, SpotlightBonus: 0.15);
    public static readonly RankWeights Nearby = new(Likes: 0.35, Views: 0.15, Tab: 0.50, FollowBonus: 0.15, SpotlightBonus: 0.15);
    // Everything here is followed already; people expect this one to be close to chronological.
    public static readonly RankWeights Following = new(Likes: 0.25, Views: 0.10, Tab: 0.65, FollowBonus: 0, SpotlightBonus: 0);

    public static RankWeights WeightsFor(FeedTab tab) => tab switch
    {
        FeedTab.Nearby => Nearby,
        FeedTab.Following => Following,
        _ => Recent,
    };

    public const double RecencyHalfLifeHours = 24;

    // Scale floors, so in a quiet app one like or a few views don't already count as "the most".
    private const int LikesFloor = 10;
    private const int ViewsFloor = 50;

    /// <summary>Returns the candidates' ids, best first.</summary>
    public static List<Guid> Rank(
        IReadOnlyList<RankCandidate> candidates,
        FeedTab tab,
        DateTime now,
        IReadOnlySet<string> followed,
        double radiusKm = 50)
    {
        if (candidates.Count == 0) return [];

        var w = WeightsFor(tab);
        var maxLikes = Math.Max(LikesFloor, candidates.Max(Likes));
        var maxViews = Math.Max(ViewsFloor, candidates.Max(c => c.Views));

        return candidates
            .Select(c => (c.Id, c.CreatedAt, Score:
                w.Likes * LikesSignal(Likes(c), maxLikes)
                + w.Views * LogScaled(c.Views, maxViews)
                + w.Tab * (tab == FeedTab.Nearby ? Closeness(c.DistanceKm ?? radiusKm, radiusKm) : Recency(c.CreatedAt, now))
                + (followed.Contains(c.AuthorId) ? w.FollowBonus : 0)
                + (c.Spotlit ? w.SpotlightBonus : 0)))
            .OrderByDescending(r => r.Score)
            .ThenByDescending(r => r.CreatedAt)
            .Select(r => r.Id)
            .ToList();
    }

    /// <summary>Net votes, with each award counting as the likes it cost the giver.</summary>
    public static int Likes(RankCandidate c) =>
        c.Upvotes - c.Downvotes + c.Awards * Points.PointsService.AwardLikeValue;

    /// <summary>-1..1: downvoted posts sink.</summary>
    public static double LikesSignal(int net, int max) => Math.Sign(net) * LogScaled(Math.Abs(net), max);

    public static double LogScaled(int value, int max) =>
        value <= 0 ? 0 : Math.Min(1, Math.Log(1 + value) / Math.Log(1 + max));

    public static double Recency(DateTime createdAt, DateTime now) =>
        Math.Pow(0.5, Math.Max(0, (now - createdAt).TotalHours) / RecencyHalfLifeHours);

    public static double Closeness(double distanceKm, double radiusKm) =>
        1 / (1 + Math.Max(0, distanceKm) / Math.Max(0.1, radiusKm / 10));

    /// <summary>Great-circle distance in km.</summary>
    public static double HaversineKm(double lat1, double lng1, double lat2, double lng2)
    {
        static double Rad(double d) => d * Math.PI / 180;
        var dLat = Rad(lat2 - lat1);
        var dLng = Rad(lng2 - lng1);
        var a = Math.Pow(Math.Sin(dLat / 2), 2) + Math.Cos(Rad(lat1)) * Math.Cos(Rad(lat2)) * Math.Pow(Math.Sin(dLng / 2), 2);
        return 6371.0088 * 2 * Math.Asin(Math.Min(1, Math.Sqrt(a)));
    }
}
