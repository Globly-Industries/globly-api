using Globly.Api.Data.Entities;
using Microsoft.Extensions.Options;

namespace Globly.Api.Moderation;

public class ModerationOptions
{
    public const string Section = "Moderation";

    /// <summary>Number of user reports after which a post is flagged for admin review.</summary>
    public int ReportThreshold { get; set; } = 5;

    public long MaxImageBytes { get; set; } = 15 * 1024 * 1024;
    public long MaxAudioBytes { get; set; } = 30 * 1024 * 1024;

    /// <summary>Keep below Cloudflare's proxied request body limit (100 MB on Free/Pro).</summary>
    public long MaxVideoBytes { get; set; } = 95 * 1024 * 1024;

    public List<string> BannedWords { get; set; } = [];
}

public record ModerationResult(bool Allowed, string? Reason = null)
{
    public static readonly ModerationResult Ok = new(true);
    public static ModerationResult Reject(string reason) => new(false, reason);
}

/// <summary>
/// Automatic check that runs before a post is stored. Currently rule based (file type/size, banned words);
/// swap the implementation to plug in an external moderation service.
/// </summary>
public interface IContentModerator
{
    ModerationResult CheckText(string? title, string? description);
    ModerationResult CheckFile(MediaType mediaType, string contentType, long length);
}

public class RuleBasedContentModerator(IOptions<ModerationOptions> options) : IContentModerator
{
    private readonly ModerationOptions _opt = options.Value;

    private static readonly Dictionary<MediaType, string[]> AllowedContentTypes = new()
    {
        [MediaType.Image] = ["image/jpeg", "image/png", "image/webp", "image/gif", "image/heic"],
        [MediaType.Video] = ["video/mp4", "video/quicktime", "video/webm", "video/3gpp"],
        [MediaType.Audio] = ["audio/mpeg", "audio/mp4", "audio/aac", "audio/ogg", "audio/wav", "audio/x-wav", "audio/webm", "audio/x-m4a"],
    };

    public ModerationResult CheckText(string? title, string? description)
    {
        var text = $"{title} {description}";
        foreach (var word in _opt.BannedWords)
        {
            if (text.Contains(word, StringComparison.OrdinalIgnoreCase))
                return ModerationResult.Reject("Content contains prohibited words.");
        }
        return ModerationResult.Ok;
    }

    public ModerationResult CheckFile(MediaType mediaType, string contentType, long length)
    {
        if (mediaType == MediaType.Text)
            return ModerationResult.Reject("Text posts cannot have a file.");

        if (!AllowedContentTypes[mediaType].Contains(contentType, StringComparer.OrdinalIgnoreCase))
            return ModerationResult.Reject($"File type '{contentType}' is not allowed for {mediaType} posts.");

        var max = mediaType switch
        {
            MediaType.Image => _opt.MaxImageBytes,
            MediaType.Video => _opt.MaxVideoBytes,
            _ => _opt.MaxAudioBytes,
        };
        if (length <= 0 || length > max)
            return ModerationResult.Reject($"File must be between 1 byte and {max / 1024 / 1024} MB.");

        return ModerationResult.Ok;
    }
}
