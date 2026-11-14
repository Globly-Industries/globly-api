using Xunit;
using Globly.Api.Users;
using ImageMagick;

namespace Globly.Api.Tests;

public class AvatarTests
{
    [Theory]
    [InlineData(800, 400, 400)]
    [InlineData(300, 900, 300)]
    [InlineData(2000, 1000, AvatarService.Size)]
    public void Pictures_become_centred_squares(int width, int height, int expected)
    {
        using var image = new MagickImage(MagickColors.Red, (uint)width, (uint)height);
        AvatarService.MakeSquare(image);
        Assert.Equal((uint)expected, image.Width);
        Assert.Equal((uint)expected, image.Height);
        Assert.Equal(MagickFormat.Jpeg, image.Format);
    }

    [Fact]
    public void Metadata_is_removed()
    {
        using var image = new MagickImage(MagickColors.Blue, 100, 100);
        var exif = new ExifProfile();
        exif.SetValue(ExifTag.GPSLatitudeRef, "N");
        image.SetProfile(exif);
        AvatarService.MakeSquare(image);
        Assert.Null(image.GetExifProfile());
    }
}
