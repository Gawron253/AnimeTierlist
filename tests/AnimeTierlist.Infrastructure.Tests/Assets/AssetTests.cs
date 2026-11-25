using System.Security.Cryptography;
using AnimeTierlist.Application.Assets;
using AnimeTierlist.Core;
using AnimeTierlist.Infrastructure.Assets;
using SkiaSharp;

namespace AnimeTierlist.Infrastructure.Tests.Assets;

/// <summary>Image validation and immutable asset installation with real decoders.</summary>
public sealed class AssetTests : IDisposable
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
    [InlineData(SKEncodedImageFormat.Jpeg, AssetFormat.Jpeg, "jpg")]
    [InlineData(SKEncodedImageFormat.Png, AssetFormat.Png, "png")]
    [InlineData(SKEncodedImageFormat.Webp, AssetFormat.WebP, "webp")]
    public void Allowlisted_formats_decode_and_get_extension_from_bytes(SKEncodedImageFormat encoded, AssetFormat expected, string extension)
    {
        var bytes = Encode(encoded, 350, 500);

        var asset = Assert.IsType<ImageValidation.Accepted>(ImageValidator.Validate(bytes, ImageLimits.Default)).Asset;

        Assert.Equal(expected, asset.Format);
        Assert.Equal((350, 500), (asset.Width, asset.Height));
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), asset.Hash.Value);
        Assert.Equal($"assets/{asset.Hash.Value}.{extension}", asset.RelativePath);
    }

    [Fact]
    public void Unsupported_but_decodable_format_is_rejected()
    {
        Assert.Equal(ImageRejection.UnsupportedFormat, Reject(OnePixelBmp(), ImageLimits.Default));
    }

    [Fact]
    public void Truncated_image_is_rejected()
    {
        var bytes = Encode(SKEncodedImageFormat.Jpeg, 350, 500);

        Assert.Equal(ImageRejection.Malformed, Reject(bytes[..(bytes.Length / 2)], ImageLimits.Default));
    }

    [Fact]
    public void Non_image_bytes_are_rejected()
    {
        Assert.Equal(ImageRejection.Malformed, Reject("<html>not an image</html>"u8.ToArray(), ImageLimits.Default));
        Assert.Equal(ImageRejection.Empty, Reject([], ImageLimits.Default));
    }

    [Fact]
    public void Compressed_size_limit_is_enforced_at_the_boundary()
    {
        var bytes = Encode(SKEncodedImageFormat.Png, 20, 20);

        Assert.IsType<ImageValidation.Accepted>(ImageValidator.Validate(bytes, new ImageLimits(bytes.Length, 1_000_000)));
        Assert.Equal(ImageRejection.TooLarge, Reject(bytes, new ImageLimits(bytes.Length - 1, 1_000_000)));
    }

    [Fact]
    public void Pixel_limit_is_enforced_before_decoding_at_the_boundary()
    {
        var bytes = Encode(SKEncodedImageFormat.Png, 20, 10);

        Assert.IsType<ImageValidation.Accepted>(ImageValidator.Validate(bytes, new ImageLimits(1_000_000, 200)));
        Assert.Equal(ImageRejection.TooManyPixels, Reject(bytes, new ImageLimits(1_000_000, 199)));
    }

    [Fact]
    public async Task Install_writes_canonical_file_and_reuses_valid_copy()
    {
        var store = new AssetStore(_project);
        var bytes = Encode(SKEncodedImageFormat.Jpeg, 35, 50);
        var asset = Accept(bytes);

        await store.InstallAsync(bytes, asset, Ct);
        var written = File.GetLastWriteTimeUtc(store.PathOf(asset));
        await store.InstallAsync(bytes, asset, Ct);

        Assert.Equal(AssetAvailability.Available, await store.CheckAsync(asset, Ct));
        Assert.Equal(written, File.GetLastWriteTimeUtc(store.PathOf(asset)));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_project, "assets"), ".incoming-*"));
    }

    [Fact]
    public async Task Damaged_file_is_reported_and_repaired_only_with_identical_bytes()
    {
        var store = new AssetStore(_project);
        var bytes = Encode(SKEncodedImageFormat.Png, 35, 50);
        var asset = Accept(bytes);
        await store.InstallAsync(bytes, asset, Ct);

        await File.WriteAllBytesAsync(store.PathOf(asset), bytes[..10], Ct);
        Assert.Equal(AssetAvailability.Damaged, await store.CheckAsync(asset, Ct));

        var different = Encode(SKEncodedImageFormat.Png, 36, 50);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.InstallAsync(different, asset, Ct));
        Assert.Equal(AssetAvailability.Damaged, await store.CheckAsync(asset, Ct));

        await store.InstallAsync(bytes, asset, Ct);
        Assert.Equal(AssetAvailability.Available, await store.CheckAsync(asset, Ct));
    }

    [Fact]
    public async Task Missing_file_is_reported_without_changing_the_record()
    {
        var store = new AssetStore(_project);
        var asset = Accept(Encode(SKEncodedImageFormat.Jpeg, 10, 10));

        Assert.Equal(AssetAvailability.Missing, await store.CheckAsync(asset, Ct));
    }

    [Fact]
    public void Abandoned_temporary_files_are_removed_but_originals_are_kept()
    {
        var assets = Directory.CreateDirectory(Path.Combine(_project, "assets")).FullName;
        File.WriteAllText(Path.Combine(assets, ".incoming-interrupted"), "partial");
        File.WriteAllText(Path.Combine(assets, new string('a', 64) + ".jpg"), "kept");

        new AssetStore(_project).DeleteAbandonedTemporaryFiles();

        Assert.Equal([new string('a', 64) + ".jpg"], Directory.EnumerateFiles(assets).Select(Path.GetFileName));
    }

    private static AssetRecord Accept(byte[] bytes) => Assert.IsType<ImageValidation.Accepted>(ImageValidator.Validate(bytes, ImageLimits.Default)).Asset;

    private static ImageRejection Reject(byte[] bytes, ImageLimits limits) => Assert.IsType<ImageValidation.Rejected>(ImageValidator.Validate(bytes, limits)).Reason;

    private static byte[] Encode(SKEncodedImageFormat format, int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(new SKColor(0x30, 0x60, 0x90));
            using var paint = new SKPaint { Color = new SKColor(0xF0, 0xC0, 0x20) };
            canvas.DrawCircle(width / 2f, height / 2f, Math.Min(width, height) / 3f, paint);
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, 90);
        Assert.NotNull(data);
        return data.ToArray();
    }

    /// <summary>A valid 1×1 24-bit BMP: decodable by Skia but outside the release allowlist.</summary>
    private static byte[] OnePixelBmp() =>
    [
        0x42, 0x4D, 0x3A, 0, 0, 0, 0, 0, 0, 0, 0x36, 0, 0, 0,
        0x28, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0x18, 0, 0, 0, 0, 0, 4, 0, 0, 0, 0x13, 0x0B, 0, 0, 0x13, 0x0B, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0xFF, 0x00, 0x00, 0x00,
    ];
}
