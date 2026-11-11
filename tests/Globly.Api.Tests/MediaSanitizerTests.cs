using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Globly.Api.Data.Entities;
using Globly.Api.Storage;
using ImageMagick;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Globly.Api.Tests;

public class MediaSanitizerTests
{
    private const string Secret = "GLOBLY-SECRET-METADATA";

    private static readonly MediaSanitizer Sanitizer =
        new(Options.Create(new MediaOptions()), NullLogger<MediaSanitizer>.Instance);

    /// <summary>A small photo tagged the way a phone camera tags one, including a GPS position.</summary>
    private static byte[] TaggedImage(MagickFormat format, OrientationType orientation = OrientationType.TopLeft)
    {
        using var image = new MagickImage(MagickColors.Red, 40, 20);
        var exif = new ExifProfile();
        exif.SetValue(ExifTag.Orientation, (ushort)orientation);
        exif.SetValue(ExifTag.GPSLatitude, [new Rational(56, 1), new Rational(57, 1), new Rational(0, 1)]);
        exif.SetValue(ExifTag.GPSLongitude, [new Rational(24, 1), new Rational(6, 1), new Rational(0, 1)]);
        exif.SetValue(ExifTag.GPSLatitudeRef, "N");
        exif.SetValue(ExifTag.Make, Secret);
        image.SetProfile(exif);
        image.Orientation = orientation;
        image.SetAttribute("comment", Secret);
        image.Format = format;
        return image.ToByteArray();
    }

    private static async Task<byte[]> Clean(MediaType type, string contentType, byte[] input)
    {
        await using var clean = await Sanitizer.SanitizeAsync(type, contentType, new MemoryStream(input), CancellationToken.None);
        using var copy = new MemoryStream();
        await clean.Content.CopyToAsync(copy);
        Assert.Equal(copy.Length, clean.Length);
        return copy.ToArray();
    }

    private static bool ContainsSecret(byte[] bytes) => Encoding.Latin1.GetString(bytes).Contains(Secret);

    [Theory]
    [InlineData(MagickFormat.Jpeg, "image/jpeg")]
    [InlineData(MagickFormat.Png, "image/png")]
    [InlineData(MagickFormat.WebP, "image/webp")]
    public async Task Images_lose_gps_and_other_metadata(MagickFormat format, string contentType)
    {
        var input = TaggedImage(format);
        using (var before = new MagickImage(input))
            Assert.NotNull(before.GetExifProfile()?.GetValue(ExifTag.GPSLatitude)); // The test file really is tagged.
        Assert.True(ContainsSecret(input));

        var output = await Clean(MediaType.Image, contentType, input);

        Assert.False(ContainsSecret(output));
        using var after = new MagickImage(output);
        Assert.Null(after.GetExifProfile());
        Assert.Equal(format, after.Format);
    }

    [Fact]
    public async Task Images_stay_upright_after_losing_the_rotation_tag()
    {
        // Stored sideways with a tag saying "rotate me", as phones do for portrait photos.
        var input = TaggedImage(MagickFormat.Jpeg, OrientationType.RightTop);
        using (var before = new MagickImage(input))
        {
            Assert.Equal(OrientationType.RightTop, before.Orientation);
            Assert.Equal(40u, before.Width);
        }

        var output = await Clean(MediaType.Image, "image/jpeg", input);

        using var after = new MagickImage(output);
        Assert.Equal(20u, after.Width);
        Assert.Equal(40u, after.Height);
    }

    [Fact]
    public async Task Colour_profiles_are_kept()
    {
        using var image = new MagickImage(MagickColors.Red, 10, 10);
        image.SetProfile(ColorProfiles.SRGB);
        image.Format = MagickFormat.Jpeg;

        var output = await Clean(MediaType.Image, "image/jpeg", image.ToByteArray());

        using var after = new MagickImage(output);
        Assert.NotNull(after.GetColorProfile());
    }

    [Fact]
    public async Task A_file_that_is_not_the_claimed_image_is_rejected()
    {
        var notAnImage = Encoding.UTF8.GetBytes("<svg xmlns='http://www.w3.org/2000/svg'><script>alert(1)</script></svg>");

        await Assert.ThrowsAsync<MediaRejectedException>(() => Clean(MediaType.Image, "image/jpeg", notAnImage));
    }

    [Fact]
    public async Task Unknown_image_types_are_rejected()
    {
        await Assert.ThrowsAsync<MediaRejectedException>(
            () => Clean(MediaType.Image, "image/svg+xml", TaggedImage(MagickFormat.Png)));
    }

    // ---------- Video and audio need ffmpeg; these show as skipped on machines without it. ----------

    private static async Task<byte[]> Generate(string extension, params string[] args)
    {
        var path = Path.Combine(Path.GetTempPath(), $"globly-test-{Guid.NewGuid():N}{extension}");
        try
        {
            var start = new ProcessStartInfo("ffmpeg") { UseShellExecute = false, RedirectStandardError = true };
            foreach (var arg in new[] { "-v", "error", "-y" }.Concat(args).Append(path)) start.ArgumentList.Add(arg);
            using var process = Process.Start(start)!;
            var errors = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            Assert.True(process.ExitCode == 0, errors);
            return await File.ReadAllBytesAsync(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>The video's display rotation as ffprobe reports it, or null if it has none.</summary>
    private static async Task<string?> RotationOf(byte[] video)
    {
        var path = Path.Combine(Path.GetTempPath(), $"globly-test-{Guid.NewGuid():N}.mp4");
        await File.WriteAllBytesAsync(path, video);
        try
        {
            var probe = new ProcessStartInfo("ffprobe") { RedirectStandardOutput = true, UseShellExecute = false };
            foreach (var arg in new[] { "-v", "error", "-show_entries", "stream_side_data=rotation", path })
                probe.ArgumentList.Add(arg);
            using var process = Process.Start(probe)!;
            var info = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            return info.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("rotation="));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [FfmpegFact]
    public async Task Videos_lose_their_location_and_keep_their_rotation()
    {
        // Two steps, the way a phone file looks: encoded video, then a rotation tag on the track.
        // (Newer ffmpeg ignores -display_rotation on a generated source that it re-encodes.)
        var plain = await Generate(".mp4", "-f", "lavfi", "-i", "testsrc=duration=1:size=64x64:rate=5", "-c:v", "mpeg4");
        var source = Path.Combine(Path.GetTempPath(), $"globly-test-{Guid.NewGuid():N}.mp4");
        await File.WriteAllBytesAsync(source, plain);
        byte[] input;
        try
        {
            input = await Generate(".mp4",
                "-display_rotation", "90", "-i", source,
                "-metadata", $"location=+56.9500+024.1000/",
                "-metadata", $"title={Secret}",
                "-c", "copy", "-movflags", "use_metadata_tags");
        }
        finally
        {
            File.Delete(source);
        }
        Assert.True(ContainsSecret(input));
        Assert.Contains("+56.9500", Encoding.Latin1.GetString(input));
        Assert.Equal("rotation=90", await RotationOf(input));

        var output = await Clean(MediaType.Video, "video/mp4", input);

        var text = Encoding.Latin1.GetString(output);
        Assert.DoesNotContain(Secret, text);
        Assert.DoesNotContain("+56.9500", text);

        // The rotation lives in the track header, not in the removed metadata.
        Assert.Equal("rotation=90", await RotationOf(output));
    }

    [FfmpegFact]
    public async Task Audio_loses_its_metadata()
    {
        var input = await Generate(".m4a",
            "-f", "lavfi", "-i", "sine=duration=1",
            "-metadata", $"title={Secret}");
        Assert.True(ContainsSecret(input));

        var output = await Clean(MediaType.Audio, "audio/mp4", input);

        Assert.False(ContainsSecret(output));
        Assert.NotEmpty(output);
    }

    [FfmpegFact]
    public async Task A_file_that_is_not_the_claimed_video_is_rejected()
    {
        // A playlist pointing at a local file: must not be followed.
        var playlist = Encoding.UTF8.GetBytes("#EXTM3U\n#EXTINF:1,\nfile:///etc/passwd\n#EXT-X-ENDLIST\n");

        await Assert.ThrowsAsync<MediaRejectedException>(() => Clean(MediaType.Video, "video/mp4", playlist));
    }
}

/// <summary>A test that needs ffmpeg on PATH, and is reported as skipped without it.</summary>
public sealed class FfmpegFactAttribute : FactAttribute
{
    private static readonly bool Available = Find();

    public FfmpegFactAttribute()
    {
        if (!Available) Skip = "ffmpeg is not on PATH";
    }

    private static bool Find()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("ffmpeg", "-version")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
            })!;
            process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode == 0;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }
}
