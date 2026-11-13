using System.Text;
using System.Threading.Channels;
using FirebaseAdmin;
using FirebaseAdmin.Messaging;
using Globly.Api.Data;
using Globly.Api.Data.Entities;
using Google.Apis.Auth.OAuth2;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Globly.Api.Notifications;

public class PushOptions
{
    public const string Section = "Firebase";

    /// <summary>Service account key JSON, raw or base64. Empty disables push.</summary>
    public string? Credentials { get; set; }
}

/// <summary>Notification ids waiting to go out as pushes. Writes are dropped while push is disabled.</summary>
public sealed class PushQueue(IOptions<PushOptions> options)
{
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>();

    public bool Enabled { get; } = !string.IsNullOrWhiteSpace(options.Value.Credentials);

    public void Enqueue(IEnumerable<Data.Entities.Notification> notifications)
    {
        if (!Enabled) return;
        foreach (var n in notifications) _channel.Writer.TryWrite(n.Id);
    }

    public ChannelReader<Guid> Reader => _channel.Reader;
}

/// <summary>Sends queued notifications through Firebase Cloud Messaging and drops dead devices.</summary>
public sealed class PushSender(
    PushQueue queue,
    IOptions<PushOptions> options,
    IServiceScopeFactory scopes,
    ILogger<PushSender> logger) : BackgroundService
{
    private const int MaxBodyLength = 150;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!queue.Enabled)
        {
            logger.LogInformation("Push notifications disabled: Firebase:Credentials is not set.");
            return;
        }

        FirebaseMessaging messaging;
        try
        {
            var app = FirebaseApp.Create(new AppOptions
            {
                Credential = CredentialFactory.FromJson<ServiceAccountCredential>(ReadCredentials(options.Value.Credentials!))
                    .ToGoogleCredential(),
            }, "globly-push");
            messaging = FirebaseMessaging.GetMessaging(app);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Push notifications disabled: invalid Firebase:Credentials.");
            return;
        }

        await foreach (var id in queue.Reader.ReadAllAsync(ct))
        {
            try
            {
                await SendAsync(messaging, id, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Push for notification {Id} failed", id);
            }
        }
    }

    private async Task SendAsync(FirebaseMessaging messaging, Guid id, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var n = await db.Notifications.AsNoTracking().Where(x => x.Id == id)
            .Select(x => new { x.Id, x.RecipientId, x.Type, x.PostId, x.CreatedAt, Actor = x.Actor!.UserName!, Body = x.Comment!.Body })
            .FirstOrDefaultAsync(ct);
        if (n is null) return; // Comment deleted meanwhile.

        var devices = await db.PushDevices.AsNoTracking().Where(d => d.UserId == n.RecipientId).ToListAsync(ct);
        if (devices.Count == 0)
        {
            logger.LogInformation("Push for notification {Id} skipped: user {UserId} has no registered devices", id, n.RecipientId);
            return;
        }

        var body = n.Body.Length > MaxBodyLength ? n.Body[..MaxBodyLength] + "…" : n.Body;
        var messages = devices.Select(d => new Message
        {
            Fid = d.Fid,
            Notification = new FirebaseAdmin.Messaging.Notification { Title = Title(n.Type, n.Actor, d.Language), Body = body },
            Data = new Dictionary<string, string>
            {
                ["notificationId"] = n.Id.ToString(),
                ["postId"] = n.PostId.ToString(),
            },
            Android = new AndroidConfig
            {
                Notification = new AndroidNotification
                {
                    // Same tag per post: new comments replace the old push instead of piling up.
                    Tag = n.PostId.ToString(),
                    // Must be set: the SDK otherwise sends DateTime.MinValue, shown as year 1.
                    EventTimestamp = DateTime.SpecifyKind(n.CreatedAt, DateTimeKind.Utc),
                },
            },
        }).ToList();

        var result = await messaging.SendEachAsync(messages, ct);

        logger.LogInformation("Push for notification {Id}: {Sent}/{Total} sent", id, result.SuccessCount, devices.Count);
        var dead = new List<string>();
        for (var i = 0; i < devices.Count; i++)
        {
            var error = result.Responses[i].Exception;
            if (error is null) continue;
            logger.LogWarning("Push to device {Fid} failed: {Code} {Message}", devices[i].Fid, error.MessagingErrorCode, error.Message);
            // InvalidArgument isn't here: it can also mean a bad message, which must not drop the device.
            if (error.MessagingErrorCode is MessagingErrorCode.Unregistered or MessagingErrorCode.SenderIdMismatch)
                dead.Add(devices[i].Fid);
        }
        if (dead.Count > 0)
            await db.PushDevices.Where(d => dead.Contains(d.Fid)).ExecuteDeleteAsync(ct);
    }

    public static string Title(NotificationType type, string actor, string language) => (type, language) switch
    {
        (NotificationType.Mention, "lv") => $"{actor} tevi pieminēja",
        (NotificationType.Mention, _) => $"{actor} mentioned you",
        (_, "lv") => $"{actor} komentēja tavu ierakstu",
        _ => $"{actor} commented on your post",
    };

    private static string ReadCredentials(string value)
    {
        value = value.Trim();
        return value.StartsWith('{') ? value : Encoding.UTF8.GetString(Convert.FromBase64String(value));
    }
}
