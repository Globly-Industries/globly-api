using Xunit;
using Globly.Api.Data.Entities;
using Globly.Api.Notifications;

namespace Globly.Api.Tests;

public class PushTitleTests
{
    [Theory]
    [InlineData(NotificationType.Follow, "en", "anna started following you")]
    [InlineData(NotificationType.Follow, "lv", "anna sāka tev sekot")]
    [InlineData(NotificationType.Comment, "en", "anna commented on your post")]
    [InlineData(NotificationType.Mention, "lv", "anna tevi pieminēja")]
    public void Titles_are_translated(NotificationType type, string language, string expected) =>
        Assert.Equal(expected, PushSender.Title(type, "anna", language));
}
