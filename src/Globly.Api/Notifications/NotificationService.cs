using System.Text.RegularExpressions;
using Globly.Api.Data;
using Globly.Api.Data.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Globly.Api.Notifications;

/// <summary>Creates notifications for new comments: the post's author and any @mentioned users.</summary>
public partial class NotificationService(AppDbContext db, ILookupNormalizer normalizer)
{
    /// <summary>Caps how many users one comment can notify by mentions.</summary>
    public const int MaxMentions = 10;

    // "@name" not preceded by a user-name character, so e-mail addresses don't count.
    [GeneratedRegex(@"(?<![A-Za-z0-9._-])@([A-Za-z0-9._-]{3,32})")]
    private static partial Regex MentionRegex();

    /// <summary>Distinct user names @mentioned in [body], in order. Trailing dots are sentence punctuation.</summary>
    public static IReadOnlyList<string> ParseMentions(string body) =>
        MentionRegex().Matches(body)
            .Select(m => m.Groups[1].Value.TrimEnd('.'))
            .Where(n => n.Length >= 3)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxMentions)
            .ToList();

    /// <summary>Adds notifications for [comment] on [post] and returns them; the caller saves.</summary>
    public async Task<List<Notification>> AddForCommentAsync(Post post, Comment comment, CancellationToken ct)
    {
        var created = new List<Notification>();
        var notified = new HashSet<string> { comment.UserId };

        if (notified.Add(post.UserId))
            Add(post.UserId, NotificationType.Comment);

        // Others can't open a hidden post, so mentions there would lead nowhere.
        if (post.IsHidden) return created;

        var names = ParseMentions(comment.Body).Select(n => normalizer.NormalizeName(n)).ToList();
        if (names.Count == 0) return created;

        var mentioned = await db.Users.Where(u => names.Contains(u.NormalizedUserName!))
            .Select(u => u.Id).ToListAsync(ct);
        foreach (var id in mentioned)
            if (notified.Add(id)) Add(id, NotificationType.Mention);
        return created;

        void Add(string recipientId, NotificationType type)
        {
            var n = new Notification
            {
                RecipientId = recipientId,
                ActorId = comment.UserId,
                Type = type,
                PostId = post.Id,
                CommentId = comment.Id,
            };
            db.Notifications.Add(n);
            created.Add(n);
        }
    }
}
