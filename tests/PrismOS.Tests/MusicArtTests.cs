using System.Drawing;
using PrismOS.Widgets;

namespace PrismOS.Tests;

public class MusicArtTests
{
    [Fact]
    public void SameBitmap_DistinguishesAppIconFromAlbumArt()
    {
        using var icon = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(icon)) g.Clear(Color.Orange);
        using var copy = new Bitmap(icon);
        using var other = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(other)) g.Clear(Color.CornflowerBlue);

        Assert.True(MusicArt.SameBitmap(icon, copy), "identical bitmap must match");
        using var large = new Bitmap(icon, new Size(48, 48));
        Assert.True(MusicArt.SameBitmap(icon, large), "upscaled icon must match");
        Assert.False(MusicArt.SameBitmap(icon, other), "different colour must not match");
        using var differentSize = new Bitmap(48, 48);
        using (var g = Graphics.FromImage(differentSize)) g.Clear(Color.CornflowerBlue);
        Assert.False(MusicArt.SameBitmap(icon, differentSize), "different size must not match");
    }
}
