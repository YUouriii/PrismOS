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

        Assert.True(MusicArt.SameBitmap(icon, copy));
        Assert.False(MusicArt.SameBitmap(icon, other));
        using var differentSize = new Bitmap(48, 48);
        Assert.False(MusicArt.SameBitmap(icon, differentSize));
    }
}
