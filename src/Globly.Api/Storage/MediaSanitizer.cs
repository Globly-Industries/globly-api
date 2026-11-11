using System.Diagnostics;
using Globly.Api.Data.Entities;
using ImageMagick;
using Microsoft.Extensions.Options;

namespace Globly.Api.Storage;

public class MediaOptions
{
    public const string Section = "Media";

    /// <summary>ffmpeg executable. It is installed in the Docker image; for local runs it must be on PATH.</summary>
    public string FfmpegPath { get; set; } = "ffmpeg";

    public int FfmpegTimeoutSeconds { get; set; } = 180;
}

/// <summary>The upload could not be read as the media type it claims to be.</summary>
public class MediaRejectedException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>A cleaned copy of an upload, in a temporary file that is deleted on dispose.</summary>
public sealed class SanitizedMedia(FileStream content) : IAsyncDisposable
{
    public Stream Content => content;
    public long Length => content.Length;
    public ValueTask DisposeAsync() => content.DisposeAsync();
}

/// <summary>
/// Removes metadata from uploads before they are published. Camera files can carry the exact GPS
/// position, which would otherwise be readable by anyone who downloads the public file, whatever
/// location the post itself shows.
/// </summary>
public interface IMediaSanitizer
{
    /// <exception cref="MediaRejectedException">The file is not valid media of the given type.</exception>
    Task<SanitizedMedia> SanitizeAsync(MediaType mediaType, string contentType, Stream input, CancellationToken ct);
}

public class MediaSanitizer : IMediaSanitizer
{
    private readonly MediaOptions _options;
    private readonly ILogger<MediaSanitizer> _logger;

    static MediaSanitizer()
    {
        // Far above any real camera; stops a small file from claiming enormous dimensions.
        ResourceLimits.Width = 20_000;
        ResourceLimits.Height = 20_000;
    }

    public MediaSanitizer(IOptions<MediaOptions> options, ILogger<MediaSanitizer> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    // The decoder is always set from the validated content type, never detected from the file, so an
    // upload cannot reach ImageMagick's or ffmpeg's riskier formats (SVG scripts, HLS playlists...).
    private static readonly Dictionary<string, MagickFormat> ImageFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        ["image/jpeg"] = MagickFormat.Jpeg,
        ["image/png"] = MagickFormat.Png,
        ["image/webp"] = MagickFormat.WebP,
        ["image/gif"] = MagickFormat.Gif,
        ["image/heic"] = MagickFormat.Heic,
    };

    /// <summary>Content type to ffmpeg input format and output file extension (which selects the output format).</summary>
    private static readonly Dictionary<string, (string Input, string Extension)> StreamFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        ["video/mp4"] = ("mov", ".mp4"),
        ["video/quicktime"] = ("mov", ".mov"),
        ["video/3gpp"] = ("mov", ".3gp"),
        ["video/webm"] = ("matroska", ".webm"),
        ["audio/mp4"] = ("mov", ".m4a"),
        ["audio/x-m4a"] = ("mov", ".m4a"),
        ["audio/mpeg"] = ("mp3", ".mp3"),
        ["audio/aac"] = ("aac", ".aac"),
        ["audio/ogg"] = ("ogg", ".ogg"),
        ["audio/wav"] = ("wav", ".wav"),
        ["audio/x-wav"] = ("wav", ".wav"),
        ["audio/webm"] = ("matroska", ".webm"),
    };

    /// <summary>ImageMagick decoder for a stored image, so reading it back never relies on format detection.</summary>
    public static MagickFormat? ImageFormatOf(string contentType) =>
        ImageFormats.TryGetValue(contentType, out var format) ? format : null;

    /// <summary>ffmpeg input format (demuxer) for stored video or audio.</summary>
    public static string? FfmpegFormatOf(string contentType) =>
        StreamFormats.TryGetValue(contentType, out var format) ? format.Input : null;

    public Task<SanitizedMedia> SanitizeAsync(MediaType mediaType, string contentType, Stream input, CancellationToken ct) =>
        mediaType == MediaType.Image
            ? Task.Run(() => SanitizeImage(contentType, input), ct)
            : SanitizeStreamAsync(contentType, input, ct);

    private static SanitizedMedia SanitizeImage(string contentType, Stream input)
    {
        if (!ImageFormats.TryGetValue(contentType, out var format))
            throw new MediaRejectedException($"Unsupported image type '{contentType}'.");

        var output = TempFile();
        try
        {
            using var frames = new MagickImageCollection(input, new MagickReadSettings { Format = format });
            foreach (var frame in frames)
            {
                // Turns the pixels upright first: stripping also drops the tag saying how to rotate them.
                frame.AutoOrient();
                var colors = frame.GetColorProfile();
                frame.Strip();
                if (colors is not null) frame.SetProfile(colors);
            }
            frames.Write(output);
            output.Position = 0;
            return new SanitizedMedia(output);
        }
        catch (MagickException ex)
        {
            output.Dispose();
            throw new MediaRejectedException("The image could not be read.", ex);
        }
        catch
        {
            output.Dispose();
            throw;
        }
    }

    private async Task<SanitizedMedia> SanitizeStreamAsync(string contentType, Stream input, CancellationToken ct)
    {
        if (!StreamFormats.TryGetValue(contentType, out var format))
            throw new MediaRejectedException($"Unsupported media type '{contentType}'.");

        var name = Path.Combine(Path.GetTempPath(), $"globly-{Guid.NewGuid():N}");
        var inputPath = name + "-in";
        var outputPath = name + "-out" + format.Extension;
        try
        {
            await using (var file = File.Create(inputPath))
                await input.CopyToAsync(file, ct);

            await RunFfmpegAsync(format.Input, inputPath, outputPath, ct);

            if (!File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
                throw new MediaRejectedException("The media file could not be read.");

            // On Linux an open file stays readable after being unlinked; DeleteOnClose handles it either way.
            return new SanitizedMedia(new FileStream(
                outputPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 81920,
                FileOptions.Asynchronous | FileOptions.DeleteOnClose));
        }
        catch
        {
            DeleteQuietly(outputPath);
            throw;
        }
        finally
        {
            DeleteQuietly(inputPath);
        }
    }

    private async Task RunFfmpegAsync(string inputFormat, string inputPath, string outputPath, CancellationToken ct)
    {
        var start = new ProcessStartInfo(_options.FfmpegPath)
        {
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in new[]
                 {
                     "-nostdin", "-y", "-v", "error",
                     "-protocol_whitelist", "file",
                     "-f", inputFormat, "-i", inputPath,
                     // Audio and video only: this also leaves out data tracks, which can hold GPS logs.
                     "-map", "0:v?", "-map", "0:a?",
                     "-map_metadata", "-1", "-map_chapters", "-1",
                     // No re-encoding, so quality is untouched and even large videos take seconds.
                     "-c", "copy",
                     outputPath,
                 })
            start.ArgumentList.Add(arg);

        // Failing to start throws: that is a server problem (ffmpeg missing), not a bad upload.
        using var process = Process.Start(start)!;
        var errors = process.StandardError.ReadToEndAsync(CancellationToken.None);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(_options.FfmpegTimeoutSeconds));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            ct.ThrowIfCancellationRequested();
            throw new MediaRejectedException("Processing the media file took too long.");
        }

        if (process.ExitCode != 0)
        {
            _logger.LogInformation("ffmpeg rejected an upload ({ExitCode}): {Errors}", process.ExitCode, await errors);
            throw new MediaRejectedException("The media file could not be read.");
        }
    }

    private static FileStream TempFile() => new(
        Path.Combine(Path.GetTempPath(), $"globly-{Guid.NewGuid():N}"),
        FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.DeleteOnClose);

    private static void DeleteQuietly(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // A leftover temp file is harmless.
        }
    }
}
