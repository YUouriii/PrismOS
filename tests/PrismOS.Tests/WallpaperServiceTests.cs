using PrismOS.Rendering;

namespace PrismOS.Tests;

/// <summary>Which files are still images and which go through the video pipeline. The split is by
/// extension because the container does not identify the codec, and getting it wrong sends a GIF
/// to Media Foundation or an MP4 to GDI+ — either way a black desktop.</summary>
public class WallpaperServiceTests
{
    [Theory]
    [InlineData("wall.png", true)]
    [InlineData("wall.JPG", true)]
    [InlineData("wall.jpeg", true)]
    [InlineData("wall.bmp", true)]
    [InlineData("wall.gif", true)]
    [InlineData("wall.tif", true)]
    [InlineData("wall.tiff", true)]
    [InlineData("wall.mp4", false)]
    [InlineData("wall.mkv", false)]
    [InlineData("wall.webm", false)]
    [InlineData("wall", false)]
    [InlineData("wall.png.exe", false)]
    public void IsImage_ClassifiesByExtension(string path, bool expected) =>
        Assert.Equal(expected, WallpaperService.IsImage(path));

    [Fact]
    public void IsImage_IgnoresCase() =>
        Assert.True(WallpaperService.IsImage(@"C:\Users\Ali\Pictures\Lake.PNG"));
}
