using System.Diagnostics;

namespace Globly.Api.Moderation;

/// <summary>Samples small still frames from a video with ffmpeg, for the NSFW classifier.</summary>
public static class VideoFrames
{
    /// <summary>
    /// One 224x224 PNG every <paramref name="intervalSeconds"/>, at most <paramref name="maxFrames"/>,
    /// written to <paramref name="outputDir"/> and returned in time order. Very short clips still
    /// give their first frame. Throws when the video can't be read or has no picture.
    /// </summary>
    public static async Task<string[]> ExtractAsync(
        string ffmpegPath, TimeSpan timeout, string inputFormat, string file, string outputDir,
        int intervalSeconds, int maxFrames, CancellationToken ct)
    {
        string[] Frames() => Directory.GetFiles(outputDir, "frame-*.png").Order().ToArray();

        await RunAsync(ffmpegPath, timeout, inputFormat, file, outputDir,
            ["-vf", $"fps=1/{intervalSeconds},scale=224:224", "-frames:v", maxFrames.ToString()], ct);
        var frames = Frames();
        if (frames.Length > 0) return frames;

        await RunAsync(ffmpegPath, timeout, inputFormat, file, outputDir, ["-vf", "scale=224:224", "-frames:v", "1"], ct);
        frames = Frames();
        return frames.Length > 0 ? frames : throw new InvalidOperationException("The video has no frames.");
    }

    private static async Task RunAsync(
        string ffmpegPath, TimeSpan timeout, string inputFormat, string file, string outputDir, string[] filter,
        CancellationToken ct)
    {
        var start = new ProcessStartInfo(ffmpegPath)
        {
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in (string[])
                 [
                     "-nostdin", "-y", "-v", "error",
                     "-protocol_whitelist", "file",
                     "-f", inputFormat, "-i", file,
                     "-map", "0:v:0",
                     .. filter,
                     Path.Combine(outputDir, "frame-%03d.png"),
                 ])
            start.ArgumentList.Add(arg);

        using var process = Process.Start(start)!;
        var errors = process.StandardError.ReadToEndAsync(CancellationToken.None);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            ct.ThrowIfCancellationRequested();
            throw new TimeoutException("ffmpeg took too long to read frames.");
        }

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"ffmpeg could not read frames ({process.ExitCode}): {await errors}");
    }
}
