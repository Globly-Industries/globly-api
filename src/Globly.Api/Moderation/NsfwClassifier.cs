using ImageMagick;
using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;
using SessionOptions = Microsoft.ML.OnnxRuntime.SessionOptions;

namespace Globly.Api.Moderation;

public class NsfwOptions
{
    public const string Section = "Moderation:Nsfw";

    public bool Enabled { get; set; } = true;

    /// <summary>
    /// ONNX image classifier with outputs [normal, nsfw]: onnx-community/nsfw_image_detection-ONNX
    /// (model_quantized.onnx), an export of Falconsai/nsfw_image_detection (Apache-2.0). Relative paths
    /// are resolved from the content root. The Docker image downloads it; see README for local runs.
    /// </summary>
    public string ModelPath { get; set; } = "models/nsfw.onnx";

    /// <summary>At or above this score the post is hidden until an admin reviews it.</summary>
    public float HideThreshold { get; set; } = 0.9f;

    /// <summary>At or above this score the post stays visible but goes to the admin queue.</summary>
    public float ReviewThreshold { get; set; } = 0.6f;

    /// <summary>Videos are sampled at one frame every this many seconds...</summary>
    public int VideoFrameIntervalSeconds { get; set; } = 3;

    /// <summary>...up to this many frames (60 x 3 s covers the 3-minute maximum).</summary>
    public int MaxVideoFrames { get; set; } = 60;

    /// <summary>Animated images: at most this many frames, spread evenly.</summary>
    public int MaxImageFrames { get; set; } = 8;

    /// <summary>CPU threads per inference, so scanning never takes the whole server.</summary>
    public int Threads { get; set; } = 2;

    /// <summary>How often the scanner looks for unscanned posts when not woken by a new post.</summary>
    public int PollSeconds { get; set; } = 60;

    /// <summary>Failed attempts (storage or ffmpeg errors) before a post is left unscanned.</summary>
    public int MaxAttempts { get; set; } = 3;
}

/// <summary>
/// Probability that a picture is explicit, from a ViT classifier (224x224 input, normalized to
/// [-1, 1] like its Hugging Face preprocessor). Runs on the CPU with ONNX Runtime.
/// </summary>
public sealed class NsfwClassifier : IDisposable
{
    private const int Size = 224;
    private readonly InferenceSession? _session;
    private readonly string _input = "";
    private readonly string _output = "";

    /// <summary>Not loaded: <see cref="IsAvailable"/> is false.</summary>
    private NsfwClassifier()
    {
    }

    /// <summary>Loads the model at <paramref name="modelPath"/>.</summary>
    public NsfwClassifier(string modelPath, int threads)
    {
        using var sessionOptions = new SessionOptions
        {
            IntraOpNumThreads = threads,
            InterOpNumThreads = 1,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        };
        _session = new InferenceSession(modelPath, sessionOptions);
        _input = _session.InputMetadata.Keys.First();
        _output = _session.OutputMetadata.Keys.First();
    }

    /// <summary>The configured model, or an unavailable classifier when it is disabled or missing.</summary>
    public static NsfwClassifier Create(IServiceProvider sp)
    {
        var o = sp.GetRequiredService<IOptions<NsfwOptions>>().Value;
        var logger = sp.GetRequiredService<ILogger<NsfwClassifier>>();
        if (!o.Enabled) return new NsfwClassifier();

        var env = sp.GetRequiredService<IWebHostEnvironment>();
        var path = Path.GetFullPath(Path.Combine(env.ContentRootPath, o.ModelPath));
        if (!File.Exists(path))
        {
            logger.LogWarning("NSFW model not found at {Path}: media is not scanned until it is there", path);
            return new NsfwClassifier();
        }

        logger.LogInformation("Loading NSFW model from {Path}", path);
        return new NsfwClassifier(path, o.Threads);
    }

    public bool IsAvailable => _session is not null;

    /// <summary>Highest score among the frames of an image file (one frame unless animated).</summary>
    public float ScoreImageFile(string path, MagickFormat format, int maxFrames)
    {
        using var frames = new MagickImageCollection(path, new MagickReadSettings { Format = format });
        if (frames.Count > 1) frames.Coalesce(); // Animated: make each frame a full picture.

        var step = Math.Max(1, frames.Count / Math.Max(1, maxFrames));
        var best = 0f;
        for (var i = 0; i < frames.Count; i += step)
            best = Math.Max(best, Score(frames[i]));
        return best;
    }

    public float Score(IMagickImage<byte> image)
    {
        if (_session is null) throw new InvalidOperationException("The NSFW model is not loaded.");

        using var img = image.Clone();
        img.AutoOrient();
        if (img.HasAlpha)
        {
            img.BackgroundColor = MagickColors.White;
            img.Alpha(AlphaOption.Remove);
        }
        img.ColorSpace = ColorSpace.sRGB;
        img.FilterType = FilterType.Triangle; // Bilinear, as in the model's preprocessor.
        img.Resize(new MagickGeometry(Size, Size) { IgnoreAspectRatio = true });

        using var pixels = img.GetPixelsUnsafe();
        var rgb = pixels.ToByteArray(PixelMapping.RGB)!;

        // HWC bytes to CHW floats, (x / 255 - 0.5) / 0.5.
        const int plane = Size * Size;
        var data = new float[3 * plane];
        for (var i = 0; i < plane; i++)
            for (var c = 0; c < 3; c++)
                data[c * plane + i] = rgb[i * 3 + c] / 127.5f - 1f;

        using var input = OrtValue.CreateTensorValueFromMemory(data, [1, 3, Size, Size]);
        using var runOptions = new RunOptions();
        using var results = _session.Run(runOptions, [_input], [input], [_output]);
        var logits = results[0].GetTensorDataAsSpan<float>();
        return Softmax(logits[0], logits[1]);
    }

    /// <summary>Probability of the second class.</summary>
    public static float Softmax(float normal, float nsfw) => 1f / (1f + MathF.Exp(normal - nsfw));

    public void Dispose() => _session?.Dispose();
}
