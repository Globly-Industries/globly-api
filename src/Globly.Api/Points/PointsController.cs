using Globly.Api.Auth;
using Globly.Api.Common;
using Globly.Api.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Globly.Api.Points;

public record PointsDto(
    int Balance,
    /// <summary>Points one award gives away.</summary>
    int AwardCost,
    int AwardsLeftToday);

public record AwardResult(int Balance, int AwardCount);

[ApiController]
[Route("api")]
[Authorize]
public class PointsController(AppDbContext db, PointsService points, Notifications.PushQueue push) : ControllerBase
{
    [HttpGet("points")]
    public async Task<ActionResult<PointsDto>> Get(CancellationToken ct)
    {
        var userId = User.RequireUserId();
        var now = DateTime.UtcNow;
        var balance = await db.Users.Where(u => u.Id == userId).Select(u => u.Points).SingleAsync(ct);
        var awardsLeft = Math.Max(0, PointsService.AwardsPerDay - await points.AwardsGivenTodayAsync(userId, now, ct));
        return new PointsDto(balance, PointsService.AwardPoints, awardsLeft);
    }

    /// <summary>Gives some of your points to the author of a post you think is great.</summary>
    [HttpPost("posts/{postId:guid}/award")]
    public async Task<ActionResult<AwardResult>> Award(Guid postId, CancellationToken ct)
    {
        var r = await points.AwardAsync(postId, User.RequireUserId(), ct);
        if (r.Notification is { } n) push.Enqueue([n]);
        return r.Problem switch
        {
            AwardProblem.None => new AwardResult(r.Balance, r.AwardCount),
            AwardProblem.NotFound => NotFound(),
            AwardProblem.OwnPost => this.ProblemWithCode(StatusCodes.Status409Conflict, ErrorCodes.CannotAwardOwn,
                "You can't award your own post."),
            AwardProblem.Hidden => this.ProblemWithCode(StatusCodes.Status409Conflict, ErrorCodes.PostHidden,
                "Hidden posts can't get awards."),
            AwardProblem.AccountTooNew => this.ProblemWithCode(StatusCodes.Status409Conflict, ErrorCodes.AccountTooNew,
                $"Accounts can give awards after {PointsService.AwardMinAccountAge.TotalDays} days."),
            AwardProblem.AlreadyAwarded => this.ProblemWithCode(StatusCodes.Status409Conflict, ErrorCodes.AlreadyAwarded,
                "You already gave this post an award."),
            AwardProblem.DailyLimit => this.ProblemWithCode(StatusCodes.Status429TooManyRequests, ErrorCodes.AwardLimit,
                $"You can give {PointsService.AwardsPerDay} awards a day."),
            _ => this.ProblemWithCode(StatusCodes.Status409Conflict, ErrorCodes.NotEnoughPoints,
                $"An award costs {PointsService.AwardPoints} points; you have {r.Balance}."),
        };
    }
}
