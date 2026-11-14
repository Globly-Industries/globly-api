using Globly.Api.Data;
using Globly.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Globly.Api.Points;

public enum SpotlightProblem { None, NotFound, NotOwner, Hidden, AlreadyActive, NotEnoughPoints }

public record SpotlightOutcome(SpotlightProblem Problem, int Balance, int Cost, DateTime? Until = null);

public enum AwardProblem { None, NotFound, OwnPost, Hidden, AccountTooNew, AlreadyAwarded, DailyLimit, NotEnoughPoints }

public record AwardOutcome(AwardProblem Problem, int Balance, int AwardCount = 0, Notification? Notification = null);

/// <summary>Points: earned from likes on one's posts, spent on spotlighting a post.</summary>
public class PointsService(AppDbContext db)
{
    public static readonly TimeSpan SpotlightDuration = TimeSpan.FromHours(24);
    public const int SpotlightBaseCost = 20;

    /// <summary>Each spotlight within this window makes the next one cost more.</summary>
    public static readonly TimeSpan SpotlightCostWindow = TimeSpan.FromDays(7);
    public const int SpotlightCostStep = 10;

    public static int SpotlightCost(int recentSpotlights) => SpotlightBaseCost + SpotlightCostStep * recentSpotlights;

    /// <summary>Points an award moves from the giver to the post's author.</summary>
    public const int AwardPoints = 5;
    public const int AwardsPerDay = 3;

    /// <summary>Accounts younger than this can't give awards, so fresh alt accounts can't feed each other.</summary>
    public static readonly TimeSpan AwardMinAccountAge = TimeSpan.FromDays(3);

    /// <summary>How many likes one award counts as in the feed ranking (the points it moved).</summary>
    public const int AwardLikeValue = AwardPoints;

    public async Task<int> AwardsGivenTodayAsync(string userId, DateTime now, CancellationToken ct)
    {
        var since = now.AddDays(-1);
        return await db.PointsLedger.CountAsync(x => x.UserId == userId && x.Kind == PointsKind.AwardGiven && x.CreatedAt >= since, ct);
    }

    /// <summary>
    /// Moves <see cref="AwardPoints"/> from [giverId] to the post's author and notifies them (the caller
    /// queues the push). One award per giver per post, a few per day.
    /// </summary>
    public async Task<AwardOutcome> AwardAsync(Guid postId, string giverId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var post = await db.Posts.AsNoTracking().FirstOrDefaultAsync(p => p.Id == postId, ct);

        // Locks giver and author in id order: parallel awards can't overspend or pass the limits
        // together, and two people awarding each other at once can't deadlock.
        var authorId = post?.UserId ?? giverId;
        var rows = await db.Database.SqlQuery<UserRow>($"""
            SELECT "Id", "Points", "CreatedAt" FROM "AspNetUsers"
            WHERE "Id" = {giverId} OR "Id" = {authorId}
            ORDER BY "Id" FOR UPDATE
            """).ToListAsync(ct);
        var giver = rows.Single(r => r.Id == giverId);

        AwardProblem problem;
        if (post is null) problem = AwardProblem.NotFound;
        else if (post.UserId == giverId) problem = AwardProblem.OwnPost;
        else if (post.IsHidden) problem = AwardProblem.Hidden;
        else if (now - giver.CreatedAt < AwardMinAccountAge) problem = AwardProblem.AccountTooNew;
        else if (await db.PointsLedger.AnyAsync(x => x.UserId == giverId && x.Kind == PointsKind.AwardGiven && x.PostId == postId, ct))
            problem = AwardProblem.AlreadyAwarded;
        else if (await AwardsGivenTodayAsync(giverId, now, ct) >= AwardsPerDay) problem = AwardProblem.DailyLimit;
        else if (giver.Points < AwardPoints) problem = AwardProblem.NotEnoughPoints;
        else problem = AwardProblem.None;
        if (problem != AwardProblem.None) return new AwardOutcome(problem, giver.Points);

        db.PointsLedger.Add(new PointsEntry { UserId = giverId, Amount = -AwardPoints, Kind = PointsKind.AwardGiven, PostId = postId });
        db.PointsLedger.Add(new PointsEntry
        {
            UserId = post!.UserId, Amount = AwardPoints, Kind = PointsKind.AwardReceived, PostId = postId, SourceUserId = giverId,
        });
        var notification = new Notification { RecipientId = post.UserId, ActorId = giverId, Type = NotificationType.Award, PostId = postId };
        db.Notifications.Add(notification);
        await db.SaveChangesAsync(ct);
        await db.Users.Where(u => u.Id == giverId)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.Points, u => u.Points - AwardPoints), ct);
        await db.Users.Where(u => u.Id == post.UserId)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.Points, u => u.Points + AwardPoints), ct);
        await tx.CommitAsync(ct);

        var count = await db.PointsLedger.CountAsync(x => x.Kind == PointsKind.AwardReceived && x.PostId == postId, ct);
        return new AwardOutcome(AwardProblem.None, giver.Points - AwardPoints, count, notification);
    }

    private sealed class UserRow
    {
        public string Id { get; set; } = "";
        public int Points { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    /// <summary>
    /// Pays the author 1 point for [voterId]'s like, the first time that voter likes that post.
    /// Own posts and hidden posts earn nothing.
    /// </summary>
    public async Task CreditLikeAsync(Post post, string voterId, CancellationToken ct)
    {
        if (post.UserId == voterId || post.IsHidden) return;

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var inserted = await db.Database.ExecuteSqlAsync($"""
            INSERT INTO "PointsLedger" ("Id", "UserId", "Amount", "Kind", "PostId", "SourceUserId", "CreatedAt")
            VALUES ({Guid.CreateVersion7()}, {post.UserId}, 1, {(int)PointsKind.LikeReceived}, {post.Id}, {voterId}, {DateTime.UtcNow})
            ON CONFLICT ("PostId", "SourceUserId") WHERE "Kind" = 0 DO NOTHING
            """, ct);
        if (inserted == 1)
            await db.Users.Where(u => u.Id == post.UserId)
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.Points, u => u.Points + 1), ct);
        await tx.CommitAsync(ct);
    }

    public async Task<int> RecentSpotlightsAsync(string userId, DateTime now, CancellationToken ct)
    {
        var since = now - SpotlightCostWindow;
        return await db.PointsLedger.CountAsync(x => x.UserId == userId && x.Kind == PointsKind.Spotlight && x.CreatedAt >= since, ct);
    }

    /// <summary>Spends points to spotlight one of the user's posts. One spotlight at a time.</summary>
    public async Task<SpotlightOutcome> SpotlightAsync(Guid postId, string userId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // Locks the user's row, so two spotlights at once can't both pass the checks below.
        var balance = await db.Database
            .SqlQuery<int>($"""SELECT "Points" AS "Value" FROM "AspNetUsers" WHERE "Id" = {userId} FOR UPDATE""")
            .SingleAsync(ct);
        var cost = SpotlightCost(await RecentSpotlightsAsync(userId, now, ct));

        var post = await db.Posts.FirstOrDefaultAsync(p => p.Id == postId, ct);
        SpotlightProblem problem;
        if (post is null) problem = SpotlightProblem.NotFound;
        else if (post.UserId != userId) problem = SpotlightProblem.NotOwner;
        else if (post.IsHidden) problem = SpotlightProblem.Hidden;
        else if (await db.Posts.AnyAsync(p => p.UserId == userId && p.SpotlightUntil > now, ct)) problem = SpotlightProblem.AlreadyActive;
        else if (balance < cost) problem = SpotlightProblem.NotEnoughPoints;
        else problem = SpotlightProblem.None;
        if (problem != SpotlightProblem.None) return new SpotlightOutcome(problem, balance, cost);

        post!.SpotlightUntil = now + SpotlightDuration;
        db.PointsLedger.Add(new PointsEntry { UserId = userId, Amount = -cost, Kind = PointsKind.Spotlight, PostId = postId });
        await db.SaveChangesAsync(ct);
        await db.Users.Where(u => u.Id == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.Points, u => u.Points - cost), ct);
        await tx.CommitAsync(ct);

        return new SpotlightOutcome(SpotlightProblem.None, balance - cost, cost, post.SpotlightUntil);
    }
}
