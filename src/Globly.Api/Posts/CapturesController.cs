using System.ComponentModel.DataAnnotations;
using Globly.Api.Auth;
using Globly.Api.Common;
using Globly.Api.Data;
using Globly.Api.Data.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Globly.Api.Posts;

public record CaptureTicketRequest([Required] MediaType MediaType);

/// <summary>SealBy: the capture must be sealed before this time (UTC).</summary>
public record CaptureTicketDto(Guid Id, DateTime SealBy);

public record SealCaptureRequest([Required] string Sha256);

/// <summary>Capture tickets: see <see cref="CaptureTicket"/>.</summary>
[ApiController]
[Authorize]
[Route("api/captures")]
public class CapturesController(AppDbContext db, IOptions<CaptureOptions> options) : ControllerBase
{
    private CaptureOptions O => options.Value;

    /// <summary>Asked for right before the camera or recorder opens.</summary>
    [HttpPost]
    public async Task<ActionResult<CaptureTicketDto>> Issue(CaptureTicketRequest req, CancellationToken ct)
    {
        var userId = User.RequireUserId();
        if (req.MediaType == MediaType.Text)
            return this.ProblemWithCode(StatusCodes.Status400BadRequest, ErrorCodes.CaptureInvalid, "Text posts are not captured.");

        var now = DateTime.UtcNow;

        // Tickets that were never used and can no longer be: only their hashes would linger.
        var stale = now - O.SealWindow - O.PublishWindow;
        await db.CaptureTickets.Where(t => t.UserId == userId && t.UsedAt == null && t.IssuedAt < stale)
            .ExecuteDeleteAsync(ct);

        var lastHour = now.AddHours(-1);
        if (await db.CaptureTickets.CountAsync(t => t.UserId == userId && t.IssuedAt > lastHour, ct) >= O.MaxTicketsPerHour)
            return Problem("Too many captures. Try again later.", statusCode: StatusCodes.Status429TooManyRequests);

        var ticket = new CaptureTicket { UserId = userId, MediaType = req.MediaType, IssuedAt = now };
        db.CaptureTickets.Add(ticket);
        await db.SaveChangesAsync(ct);

        return new CaptureTicketDto(ticket.Id, now + O.SealWindow);
    }

    /// <summary>Sent as soon as the capture ends, with the SHA-256 of the file that will be uploaded.</summary>
    [HttpPost("{id:guid}/seal")]
    public async Task<IActionResult> Seal(Guid id, SealCaptureRequest req, CancellationToken ct)
    {
        var userId = User.RequireUserId();
        var sha256 = CaptureRules.NormalizeSha256(req.Sha256);
        if (sha256 is null)
            return this.ProblemWithCode(StatusCodes.Status400BadRequest, ErrorCodes.CaptureInvalid, "Sha256 must be 64 hex characters.");

        var ticket = await db.CaptureTickets.FindAsync([id], ct);
        var now = DateTime.UtcNow;
        var problem = CaptureRules.CheckSeal(ticket, userId, sha256, now, O);
        if (problem != CaptureProblem.None) return CaptureProblemResult(this, problem);
        if (ticket!.Sha256 is not null) return NoContent(); // Retry of a seal that already went through.

        // A fresh capture never matches an earlier file byte for byte.
        if (await db.CaptureTickets.AnyAsync(t => t.Sha256 == sha256, ct))
            return CaptureProblemResult(this, CaptureProblem.HashMismatch);

        ticket.Sha256 = sha256;
        ticket.SealedAt = now;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Lost the race on the unique hash index.
            return CaptureProblemResult(this, CaptureProblem.HashMismatch);
        }
        return NoContent();
    }

    internal static ObjectResult CaptureProblemResult(ControllerBase c, CaptureProblem problem) => problem switch
    {
        CaptureProblem.Expired => c.ProblemWithCode(StatusCodes.Status422UnprocessableEntity, ErrorCodes.CaptureExpired,
            "This capture is too old. Capture it again."),
        CaptureProblem.TooLong or CaptureProblem.NotFresh => c.ProblemWithCode(StatusCodes.Status422UnprocessableEntity,
            ErrorCodes.CaptureInvalid, $"This media was not captured just now in the app ({problem})."),
        _ => c.ProblemWithCode(StatusCodes.Status422UnprocessableEntity, ErrorCodes.CaptureInvalid,
            $"The media could not be verified as captured in the app ({problem})."),
    };
}
