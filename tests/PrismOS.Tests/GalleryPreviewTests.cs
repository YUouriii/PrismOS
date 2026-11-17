using PrismOS.Gallery;

namespace PrismOS.Tests;

/// <summary>The gallery window shows a still for every entry before anything is downloaded, so the
/// manifest has to carry a working thumbnail for each one, and the list has to be able to narrow
/// itself by kind and by search text.</summary>
public class GalleryPreviewTests
{
    private static readonly GalleryManifest Manifest = new GalleryService().Manifest;

    [Fact]
    public void EveryEntry_HasAThumbnailFromAStillHost()
    {
        foreach (var entry in Manifest.Entries)
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.Thumbnail), $"{entry.Id} has no thumbnail");
            var uri = new Uri(entry.Thumbnail);
            Assert.Equal("https", uri.Scheme);
            Assert.True(uri.Host is "thumb.wikimedia.org" or "images-assets.nasa.gov",
                $"{entry.Id} thumbnail points at unexpected host {uri.Host}");
        }
    }

    [Fact]
    public void Matches_FiltersByKind()
    {
        var video = new GalleryEntry { Type = "video", Title = "Moon" };
        var photo = new GalleryEntry { Type = "image", Title = "Moon" };

        Assert.True(video.Matches(null, GalleryKind.All));
        Assert.True(video.Matches(null, GalleryKind.Videos));
        Assert.False(video.Matches(null, GalleryKind.Photos));

        Assert.True(photo.Matches(null, GalleryKind.Photos));
        Assert.False(photo.Matches(null, GalleryKind.Videos));
    }

    [Fact]
    public void Matches_SearchesTitleDescriptionAuthorAndLicense()
    {
        var entry = new GalleryEntry
        {
            Title = "Aurora Borealis",
            Description = "Northern lights",
            Author = "Christer Olsen",
            License = "CC0",
        };

        Assert.True(entry.Matches("aurora", GalleryKind.All));
        Assert.True(entry.Matches("  LIGHTS  ", GalleryKind.All));
        Assert.True(entry.Matches("olsen", GalleryKind.All));
        Assert.True(entry.Matches("cc0", GalleryKind.All));
        Assert.False(entry.Matches("moon", GalleryKind.All));
        // A search must still respect the kind filter.
        Assert.False(entry.Matches("aurora", GalleryKind.Photos));
    }

    [Fact]
    public void KindAndResolutionLabels_ReadLikeSomethingToShow()
    {
        var entry = new GalleryEntry { Type = "image", Width = 1920, Height = 1080 };
        Assert.Equal("photo", entry.KindLabel);
        Assert.Equal("1920×1080", entry.ResolutionLabel);
        Assert.Equal("unknown size", new GalleryEntry().ResolutionLabel);
        Assert.Equal("video", new GalleryEntry { Type = "video" }.KindLabel);
    }

    [Fact]
    public void ThumbnailPath_IsPerEntry_AndKeepsAnImageExtension()
    {
        var service = new GalleryService();
        var path = service.ThumbnailPath(new GalleryEntry
        {
            Id = "wolf-moon",
            Thumbnail = "https://images-assets.nasa.gov/video/x/x~large.jpg",
        });
        Assert.EndsWith("wolf-moon.jpg", path.Replace('\\', '/'));
        Assert.StartsWith(service.ThumbnailDirectory.Replace('\\', '/'), path.Replace('\\', '/'));

        // A thumbnail with no usable extension still gets one, so it always decodes.
        Assert.EndsWith("wolf-moon.jpg", service.ThumbnailPath(new GalleryEntry
        {
            Id = "wolf-moon",
            Thumbnail = "not a url",
        }).Replace('\\', '/'));
    }

    [Fact]
    public void LocalThumbnailIfCached_IsNullUntilDownloaded()
    {
        var service = new GalleryService();
        var entry = new GalleryEntry { Id = "definitely-not-cached", Thumbnail = "https://thumb.wikimedia.org/x/y.jpg" };
        Assert.Null(service.LocalThumbnailIfCached(entry));
        Assert.Null(service.LocalThumbnailIfCached(new GalleryEntry { Id = "x" }));
    }
}
