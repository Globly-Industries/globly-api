namespace Globly.Api.Storage;

/// <summary>
/// Cloudflare content database module. The server is the only writer;
/// clients read media directly using the URL returned by <see cref="GetReadUrl"/>.
/// </summary>
public interface IMediaStorage
{
    Task UploadAsync(string objectKey, Stream content, string contentType, CancellationToken ct = default);
    Task DeleteAsync(string objectKey, CancellationToken ct = default);

    /// <summary>Copies a stored object into <paramref name="destination"/> (used by the NSFW scanner).</summary>
    Task DownloadAsync(string objectKey, Stream destination, CancellationToken ct = default);

    /// <summary>URL the app uses to stream the object directly (public R2 domain or a presigned URL).</summary>
    string GetReadUrl(string objectKey);
}

public class StorageOptions
{
    public const string Section = "Storage";

    /// <summary>"R2" (production) or "Local" (development, files saved to disk and served by the API).</summary>
    public string Provider { get; set; } = "Local";

    public R2Options R2 { get; set; } = new();
    public LocalOptions Local { get; set; } = new();

    public class R2Options
    {
        public string AccountId { get; set; } = "";
        public string AccessKeyId { get; set; } = "";
        public string SecretAccessKey { get; set; } = "";
        public string Bucket { get; set; } = "globly-media";

        /// <summary>
        /// Public bucket URL (custom domain or r2.dev), e.g. "https://media.globly.example".
        /// If empty, presigned GET URLs are generated instead.
        /// </summary>
        public string PublicBaseUrl { get; set; } = "";

        public int PresignedUrlMinutes { get; set; } = 60;
    }

    public class LocalOptions
    {
        public string RootPath { get; set; } = "media";

        /// <summary>Absolute base URL for served files, e.g. "http://10.0.2.2:5021/media".</summary>
        public string PublicBaseUrl { get; set; } = "http://localhost:5021/media";
    }
}
