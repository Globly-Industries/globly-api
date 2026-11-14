using Xunit;
using Globly.Api.Posts;

namespace Globly.Api.Tests;

public class FeedRankingTests
{
    private static readonly DateTime Now = new(2026, 11, 14, 12, 0, 0, DateTimeKind.Utc);
    private static readonly HashSet<string> NoOne = [];

    private static RankCandidate Post(string id, int up = 0, int down = 0, int views = 0, double hoursOld = 0,
        double? km = null, string author = "a", bool spotlit = false, int awards = 0) =>
        new(Guid.Parse($"00000000-0000-0000-0000-{id.PadLeft(12, '0')}"), author, up, down, views, Now.AddHours(-hoursOld), km, spotlit, awards);

    private static string Short(Guid id) => id.ToString()[^12..].TrimStart('0');

    private static List<string> Rank(FeedTab tab, IReadOnlySet<string>? followed = null, params RankCandidate[] posts) =>
        FeedRanking.Rank(posts, tab, Now, followed ?? NoOne).Select(Short).ToList();

    [Fact]
    public void Weights_add_up_to_one()
    {
        foreach (var tab in Enum.GetValues<FeedTab>())
        {
            var w = FeedRanking.WeightsFor(tab);
            Assert.Equal(1, w.Likes + w.Views + w.Tab, 6);
        }
    }

    [Fact]
    public void Recent_puts_liked_post_first_when_equally_old()
    {
        Assert.Equal(["2", "1"], Rank(FeedTab.Recent, null, Post("1", up: 1, hoursOld: 2), Post("2", up: 20, hoursOld: 2)));
    }

    [Fact]
    public void Recent_lets_new_posts_beat_week_old_hits()
    {
        Assert.Equal(["2", "1"], Rank(FeedTab.Recent, null,
            Post("1", up: 100, views: 1000, hoursOld: 24 * 8),
            Post("2", up: 3, views: 20, hoursOld: 1)));
    }

    [Fact]
    public void Recent_keeps_a_fresh_hit_above_an_empty_new_post()
    {
        Assert.Equal(["1", "2"], Rank(FeedTab.Recent, null,
            Post("1", up: 50, views: 500, hoursOld: 12),
            Post("2", hoursOld: 0)));
    }

    [Fact]
    public void Downvoted_posts_sink()
    {
        Assert.Equal(["1", "2"], Rank(FeedTab.Recent, null, Post("1", hoursOld: 1), Post("2", down: 8, hoursOld: 1)));
    }

    [Fact]
    public void Nearby_prefers_close_posts_over_new_ones()
    {
        Assert.Equal(["1", "2"], Rank(FeedTab.Nearby, null,
            Post("1", km: 0.5, hoursOld: 24 * 30),
            Post("2", km: 40, hoursOld: 0)));
    }

    [Fact]
    public void Nearby_lets_a_popular_post_beat_a_slightly_closer_one()
    {
        Assert.Equal(["2", "1"], Rank(FeedTab.Nearby, null, Post("1", km: 1), Post("2", up: 30, views: 300, km: 2)));
    }

    [Fact]
    public void Followed_authors_get_a_boost()
    {
        Assert.Equal(["2", "1"], Rank(FeedTab.Recent, new HashSet<string> { "friend" },
            Post("1", up: 1, hoursOld: 1),
            Post("2", hoursOld: 1, author: "friend")));
    }

    [Fact]
    public void Ties_go_to_the_newer_post()
    {
        Assert.Equal(["2", "1"], Rank(FeedTab.Nearby, null, Post("1", km: 1, hoursOld: 5), Post("2", km: 1, hoursOld: 1)));
    }

    [Fact]
    public void Signals_are_bounded()
    {
        Assert.Equal(1, FeedRanking.LogScaled(500, 10));
        Assert.Equal(0, FeedRanking.LogScaled(0, 10));
        Assert.Equal(-1, FeedRanking.LikesSignal(-50, 10));
        Assert.Equal(0.5, FeedRanking.Recency(Now.AddHours(-FeedRanking.RecencyHalfLifeHours), Now), 6);
        Assert.Equal(0.5, FeedRanking.Closeness(5, 50), 6);
    }

    [Fact]
    public void Haversine_matches_known_distance()
    {
        // Riga to Tallinn is about 280 km.
        Assert.InRange(FeedRanking.HaversineKm(56.9496, 24.1052, 59.4370, 24.7536), 275, 285);
    }

    [Fact]
    public void Spotlight_lifts_a_post_above_an_equal_one()
    {
        Assert.Equal(["1", "2"], Rank(FeedTab.Recent, null, Post("1", hoursOld: 3, spotlit: true), Post("2", hoursOld: 1)));
    }

    [Fact]
    public void Spotlight_does_not_beat_a_clearly_better_post()
    {
        Assert.Equal(["2", "1"], Rank(FeedTab.Recent, null,
            Post("1", hoursOld: 1, spotlit: true),
            Post("2", up: 40, views: 400, hoursOld: 1)));
    }

    [Fact]
    public void Spotlight_cost_rises_with_recent_use()
    {
        Assert.Equal(20, Globly.Api.Points.PointsService.SpotlightCost(0));
        Assert.Equal(40, Globly.Api.Points.PointsService.SpotlightCost(2));
    }

    [Fact]
    public void An_award_counts_as_several_likes()
    {
        Assert.Equal(["2", "1"], Rank(FeedTab.Recent, null, Post("1", up: 4, hoursOld: 1), Post("2", hoursOld: 1, awards: 1)));
        Assert.Equal(5, FeedRanking.Likes(Post("3", awards: 1)));
    }
}
