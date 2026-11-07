using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;

namespace Globly.Api.Storage;

/// <summary>Cloudflare R2 via its S3-compatible API.</summary>
public sealed class R2MediaStorage : IMediaStorage, IDisposable
{
    private readonly AmazonS3Client _s3;
    private readonly StorageOptions.R2Options _opt;

    public R2MediaStorage(IOptions<StorageOptions> options)
    {
        _opt = options.Value.R2;
        var config = new AmazonS3Config
        {
            ServiceURL = $"https://{_opt.AccountId}.r2.cloudflarestorage.com",
            AuthenticationRegion = "auto",
            ForcePathStyle = true,
            // R2 does not support every checksum the AWS SDK sends by default.
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
        };
        _s3 = new AmazonS3Client(new BasicAWSCredentials(_opt.AccessKeyId, _opt.SecretAccessKey), config);
    }

    public async Task UploadAsync(string objectKey, Stream content, string contentType, CancellationToken ct = default)
    {
        await _s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _opt.Bucket,
            Key = objectKey,
            InputStream = content,
            ContentType = contentType,
            DisablePayloadSigning = true,
            AutoCloseStream = false,
        }, ct);
    }

    public async Task DeleteAsync(string objectKey, CancellationToken ct = default)
    {
        await _s3.DeleteObjectAsync(_opt.Bucket, objectKey, ct);
    }

    public string GetReadUrl(string objectKey)
    {
        if (!string.IsNullOrWhiteSpace(_opt.PublicBaseUrl))
            return $"{_opt.PublicBaseUrl.TrimEnd('/')}/{objectKey}";

        return _s3.GetPreSignedURL(new GetPreSignedUrlRequest
        {
            BucketName = _opt.Bucket,
            Key = objectKey,
            Verb = HttpVerb.GET,
            Expires = DateTime.UtcNow.AddMinutes(_opt.PresignedUrlMinutes),
        });
    }

    public void Dispose() => _s3.Dispose();
}
