using Globly.Api.Moderation;
using ImageMagick;
using Xunit;

namespace Globly.Api.Tests;

public class NsfwTests
{
    private static readonly NsfwOptions O = new() { HideThreshold = 0.9f, ReviewThreshold = 0.6f };

    [Theory]
    [InlineData(0.0f, NsfwVerdict.Fine)]
    [InlineData(0.59f, NsfwVerdict.Fine)]
    [InlineData(0.6f, NsfwVerdict.Review)]
    [InlineData(0.89f, NsfwVerdict.Review)]
    [InlineData(0.9f, NsfwVerdict.Hide)]
    [InlineData(1.0f, NsfwVerdict.Hide)]
    public void Verdict_follows_thresholds(float score, NsfwVerdict expected) =>
        Assert.Equal(expected, NsfwRules.Decide(score, O));

    [Fact]
    public void Softmax_gives_probability_of_nsfw()
    {
        Assert.Equal(0.5f, NsfwClassifier.Softmax(1f, 1f), 3);
        Assert.True(NsfwClassifier.Softmax(-5f, 5f) > 0.99f);
        Assert.True(NsfwClassifier.Softmax(5f, -5f) < 0.01f);
    }

    // ---------- With the real model (skipped when models/nsfw.onnx has not been downloaded) ----------

    private static readonly Lazy<NsfwClassifier> Classifier = new(() => new NsfwClassifier(ModelFactAttribute.ModelPath!, 2));

    [ModelFact]
    public void Harmless_pictures_score_low()
    {
        using var gray = new MagickImage(MagickColors.Gray, 640, 480);
        using var gradient = new MagickImage("gradient:skyblue-green", new MagickReadSettings { Width = 300, Height = 500 });
        using var pattern = new MagickImage("pattern:checkerboard", new MagickReadSettings { Width = 400, Height = 400 });

        foreach (var image in new[] { gray, gradient, pattern })
        {
            var score = Classifier.Value.Score(image);
            Assert.InRange(score, 0f, 0.5f);
        }
    }

    [ModelFact]
    public void Scores_an_image_file_like_the_scanner_does()
    {
        var logo = Path.Combine(ModelFactAttribute.RepoRoot!, "globly-flutter", "assets", "logo.png");
        Assert.True(File.Exists(logo), logo);
        Assert.InRange(Classifier.Value.ScoreImageFile(logo, MagickFormat.Png, 8), 0f, 0.5f);
    }

    [ModelFact]
    public void Animated_images_are_scored_frame_by_frame()
    {
        var path = Path.Combine(Path.GetTempPath(), $"globly-test-{Guid.NewGuid():N}.gif");
        try
        {
            using (var frames = new MagickImageCollection())
            {
                frames.Add(new MagickImage(MagickColors.Red, 64, 64) { AnimationDelay = 10 });
                frames.Add(new MagickImage(MagickColors.Blue, 64, 64) { AnimationDelay = 10 });
                frames.Write(path, MagickFormat.Gif);
            }
            Assert.InRange(Classifier.Value.ScoreImageFile(path, MagickFormat.Gif, 8), 0f, 0.5f);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---------- Video frames (need ffmpeg) ----------

    private static async Task<string[]> FramesOf(double seconds, int interval, int max)
    {
        var video = await MediaSanitizerTests.Generate(".mp4",
            "-f", "lavfi", "-i", $"testsrc=duration={seconds}:size=320x240:rate=10", "-c:v", "mpeg4");
        var dir = Path.Combine(Path.GetTempPath(), $"globly-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var file = Path.Combine(dir, "media");
            await File.WriteAllBytesAsync(file, video);
            var frames = await VideoFrames.ExtractAsync(
                "ffmpeg", TimeSpan.FromSeconds(60), "mov", file, dir, interval, max, CancellationToken.None);

            foreach (var frame in frames)
            {
                using var image = new MagickImage(frame);
                Assert.Equal(224u, image.Width);
                Assert.Equal(224u, image.Height);
            }
            return frames.Select(Path.GetFileName).ToArray()!;
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [FfmpegFact]
    public async Task Video_is_sampled_every_few_seconds()
    {
        var frames = await FramesOf(seconds: 10, interval: 3, max: 60);
        Assert.InRange(frames.Length, 3, 4); // Around 0, 3, 6 and 9 s.
        Assert.Equal("frame-001.png", frames[0]);
    }

    [FfmpegFact]
    public async Task Sampling_stops_at_the_frame_limit() =>
        Assert.Equal(2, (await FramesOf(seconds: 10, interval: 1, max: 2)).Length);

    [FfmpegFact]
    public async Task Very_short_video_still_gives_a_frame() =>
        Assert.NotEmpty(await FramesOf(seconds: 0.5, interval: 3, max: 60));
}

/// <summary>A test that needs the downloaded NSFW model, and is reported as skipped without it.</summary>
public sealed class ModelFactAttribute : FactAttribute
{
    public static readonly string? RepoRoot = FindRepoRoot();

    public static string? ModelPath =>
        RepoRoot is null ? null : Path.Combine(RepoRoot, "globly-api", "src", "Globly.Api", "models", "nsfw.onnx");

    public ModelFactAttribute()
    {
        if (ModelPath is null || !File.Exists(ModelPath)) Skip = "models/nsfw.onnx has not been downloaded (see README)";
    }

    private static string? FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "globly-api", "src")))
                return dir.FullName;
        return null;
    }
}
