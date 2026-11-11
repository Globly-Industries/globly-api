using Xunit;
using Globly.Api.Data.Entities;
using Globly.Api.Posts;

namespace Globly.Api.Tests;

public class CaptureRulesTests
{
    private static readonly CaptureOptions O = new() { SealWindowMinutes = 10, PublishWindowMinutes = 60 };
    private static readonly DateTime T0 = new(2026, 11, 11, 12, 0, 0, DateTimeKind.Utc);
    private const string User = "u1";
    private static readonly string HashA = new('a', 64);
    private static readonly string HashB = new('b', 64);

    private static CaptureTicket Issued() => new() { UserId = User, MediaType = MediaType.Image, IssuedAt = T0 };

    private static CaptureTicket Sealed(string hash, DateTime at)
    {
        var t = Issued();
        t.Sha256 = hash;
        t.SealedAt = at;
        return t;
    }

    [Fact]
    public void Seal_within_window_passes() =>
        Assert.Equal(CaptureProblem.None, CaptureRules.CheckSeal(Issued(), User, HashA, T0.AddMinutes(9), O));

    [Fact]
    public void Seal_after_window_is_expired() =>
        Assert.Equal(CaptureProblem.Expired, CaptureRules.CheckSeal(Issued(), User, HashA, T0.AddMinutes(11), O));

    [Fact]
    public void Seal_of_someone_elses_ticket_is_not_found()
    {
        Assert.Equal(CaptureProblem.NotFound, CaptureRules.CheckSeal(Issued(), "u2", HashA, T0, O));
        Assert.Equal(CaptureProblem.NotFound, CaptureRules.CheckSeal(null, User, HashA, T0, O));
    }

    [Fact]
    public void Resealing_with_same_hash_is_a_harmless_retry_even_late()
    {
        var t = Sealed(HashA, T0.AddMinutes(1));
        Assert.Equal(CaptureProblem.None, CaptureRules.CheckSeal(t, User, HashA, T0.AddMinutes(30), O));
        Assert.Equal(CaptureProblem.AlreadySealed, CaptureRules.CheckSeal(t, User, HashB, T0.AddMinutes(2), O));
    }

    [Fact]
    public void Publish_of_sealed_matching_file_passes() =>
        Assert.Equal(CaptureProblem.None,
            CaptureRules.CheckPublish(Sealed(HashA, T0.AddMinutes(2)), User, MediaType.Image, HashA, T0.AddMinutes(30), O));

    [Fact]
    public void Publish_rejects_a_different_file() =>
        Assert.Equal(CaptureProblem.HashMismatch,
            CaptureRules.CheckPublish(Sealed(HashA, T0), User, MediaType.Image, HashB, T0.AddMinutes(1), O));

    [Fact]
    public void Publish_rejects_unsealed_used_wrong_type_and_late()
    {
        Assert.Equal(CaptureProblem.NotSealed, CaptureRules.CheckPublish(Issued(), User, MediaType.Image, HashA, T0, O));

        var used = Sealed(HashA, T0);
        used.UsedAt = T0.AddMinutes(1);
        Assert.Equal(CaptureProblem.Used, CaptureRules.CheckPublish(used, User, MediaType.Image, HashA, T0.AddMinutes(2), O));

        Assert.Equal(CaptureProblem.WrongType,
            CaptureRules.CheckPublish(Sealed(HashA, T0), User, MediaType.Video, HashA, T0.AddMinutes(1), O));

        Assert.Equal(CaptureProblem.Expired,
            CaptureRules.CheckPublish(Sealed(HashA, T0), User, MediaType.Image, HashA, T0.AddMinutes(61), O));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("abc", null)]
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz", null)]
    [InlineData("ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789",
        "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789")]
    public void NormalizeSha256(string? input, string? expected) =>
        Assert.Equal(expected, CaptureRules.NormalizeSha256(input));
}
