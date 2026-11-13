using System.ComponentModel.DataAnnotations;
using Globly.Api.Auth;
using Globly.Api.Data;
using Globly.Api.Data.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Globly.Api.Notifications;

public record RegisterDeviceRequest(
    [Required, StringLength(64, MinimumLength = 1)] string Fid,
    [StringLength(8)] string? Language);

[ApiController]
[Route("api/devices")]
[Authorize]
public class DevicesController(AppDbContext db) : ControllerBase
{
    /// <summary>Registers the device for the caller's pushes; a device last used by someone else moves over.</summary>
    [HttpPut]
    public async Task<IActionResult> Register(RegisterDeviceRequest req, CancellationToken ct)
    {
        var userId = User.RequireUserId();
        var language = req.Language is "lv" ? "lv" : "en";

        var device = await db.PushDevices.FindAsync([req.Fid], ct);
        if (device is null)
        {
            db.PushDevices.Add(new PushDevice { Fid = req.Fid, UserId = userId, Language = language });
        }
        else
        {
            device.UserId = userId;
            device.Language = language;
            device.UpdatedAt = DateTime.UtcNow;
        }
        await db.SaveChangesAsync(ct);
        return NoContent();
    }
}
