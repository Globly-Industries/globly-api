using Microsoft.Extensions.Options;

namespace Globly.Api.Storage;

/// <summary>Development stand-in for R2: writes to disk, files are served at /media.</summary>
public sealed class LocalMediaStorage(IOptions<StorageOptions> options, IWebHostEnvironment env) : IMediaStorage
{
    private readonly StorageOptions.LocalOptions _opt = options.Value.Local;

    public string RootPath => Path.GetFullPath(Path.Combine(env.ContentRootPath, _opt.RootPath));

    public async Task UploadAsync(string objectKey, Stream content, string contentType, CancellationToken ct = default)
    {
        var path = ResolvePath(objectKey);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var file = File.Create(path);
        await content.CopyToAsync(file, ct);
    }

    public Task DeleteAsync(string objectKey, CancellationToken ct = default)
    {
        var path = ResolvePath(objectKey);
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    public async Task DownloadAsync(string objectKey, Stream destination, CancellationToken ct = default)
    {
        await using var file = File.OpenRead(ResolvePath(objectKey));
        await file.CopyToAsync(destination, ct);
    }

    public string GetReadUrl(string objectKey) => $"{_opt.PublicBaseUrl.TrimEnd('/')}/{objectKey}";

    private string ResolvePath(string objectKey)
    {
        var path = Path.GetFullPath(Path.Combine(RootPath, objectKey));
        if (!path.StartsWith(RootPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Invalid object key.");
        return path;
    }
}
