using Xunit;
using Globly.Api.Notifications;

namespace Globly.Api.Tests;

public class MentionTests
{
    [Theory]
    [InlineData("@anna nice one", new[] { "anna" })]
    [InlineData("thanks @anna and @Bob_1.", new[] { "anna", "Bob_1" })]
    [InlineData("@anna @ANNA @anna", new[] { "anna" })]
    [InlineData("mail me at x@example.com", new string[0])]
    [InlineData("@ab too short", new string[0])]
    [InlineData("(@john.doe)", new[] { "john.doe" })]
    public void ParsesMentions(string body, string[] expected) =>
        Assert.Equal(expected, NotificationService.ParseMentions(body));

    [Fact]
    public void CapsMentions()
    {
        var body = string.Join(' ', Enumerable.Range(0, 20).Select(i => $"@user{i}"));
        Assert.Equal(NotificationService.MaxMentions, NotificationService.ParseMentions(body).Count);
    }
}
