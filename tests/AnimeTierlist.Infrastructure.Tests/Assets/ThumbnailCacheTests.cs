using AnimeTierlist.Core;
using AnimeTierlist.Infrastructure.Assets;
using SkiaSharp;

namespace AnimeTierlist.Infrastructure.Tests.Assets;

public sealed class ThumbnailCacheTests : IDisposable
{
    private readonly string _project = Path.Combine(Path.GetTempPath(), "animetierlist-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_project))
        {
            Directory.Delete(_project, recursive: true);
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(SKEncodedImageFormat.Jpeg)]
    [InlineData(SKEncodedImageFormat.Png)]
    [InlineData(SKEncodedImageFormat.Webp)]
    public async Task Thumbnail_is_bounded_proportional_and_cached_in_the_regenerable_folder(SKEncodedImageFormat format)
    {
        var (cache, asset) = await InstallAsync(format, 350, 500);

        var path = await cache.GetAsync(asset, 160, Ct);
        var again = await cache.GetAsync(asset, 160, Ct);

        Assert.Equal(path, again);
        Assert.StartsWith(Path.Combine(_project, "cache"), path, StringComparison.OrdinalIgnoreCase);
        using var codec = SKCodec.Create(path);
        Assert.Equal(SKEncodedImageFormat.Jpeg, codec.EncodedFormat);
        Assert.Equal(160, codec.Info.Height);
        Assert.InRange(codec.Info.Width, 111, 113);
    }

    [Fact]
    public async Task Small_originals_are_not_upscaled_and_missing_originals_return_null()
    {
        var (cache, asset) = await InstallAsync(SKEncodedImageFormat.Png, 40, 60);

        using (var codec = SKCodec.Create(await cache.GetAsync(asset, 160, Ct)))
        {
            Assert.Equal((40, 60), (codec.Info.Width, codec.Info.Height));
        }

        var absent = new AssetRecord(new AssetHash(new string('e', 64)), AssetFormat.Jpeg, 10, 10, 10);
        Assert.Null(await cache.GetAsync(absent, 160, Ct));
    }

    private async Task<(ThumbnailCache, AssetRecord)> InstallAsync(SKEncodedImageFormat format, int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(new SKColor(0x20, 0x80, 0xC0));
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, 90);
        var bytes = data.ToArray();
        var store = new AssetStore(_project);
        var asset = Assert.IsType<ImageValidation.Accepted>(ImageValidator.Validate(bytes, ImageLimits.Default)).Asset;
        await store.InstallAsync(bytes, asset, Ct);
        return (new ThumbnailCache(_project, store), asset);
    }
}
