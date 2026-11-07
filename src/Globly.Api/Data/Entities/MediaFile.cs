namespace Globly.Api.Data.Entities;

/// <summary>Metadata of a media object stored in Cloudflare R2 (Files table).</summary>
public class MediaFile
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>Object key inside the R2 bucket.</summary>
    public required string ObjectKey { get; set; }

    public required string FileName { get; set; }
    public required string ContentType { get; set; }
    public long ContentLength { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
