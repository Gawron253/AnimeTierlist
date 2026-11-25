using System.Security.Cryptography;
using AnimeTierlist.Core;
using SkiaSharp;

namespace AnimeTierlist.Infrastructure.Assets;

/// <summary>Limits shared by downloads and archive imports.</summary>
public sealed record ImageLimits(long MaxCompressedBytes, long MaxDecodedPixels)
{
    /// <summary>Default safety limits: 20 MiB compressed, 40 million decoded pixels.</summary>
    public static ImageLimits Default { get; } = new(20L * 1024 * 1024, 40_000_000);
}

public enum ImageRejection
{
    Empty,
    TooLarge,
    UnsupportedFormat,
    Malformed,
    TooManyPixels,
}

public abstract record ImageValidation
{
    private ImageValidation()
    {
    }

    public sealed record Accepted(AssetRecord Asset) : ImageValidation;

    public sealed record Rejected(ImageRejection Reason, string Detail) : ImageValidation;
}

/// <summary>
/// Bounded image validation: the format comes from decoded bytes (never URLs), dimensions are checked before
/// pixels are allocated, and the image must decode completely.
/// </summary>
public static class ImageValidator
{
    public static ImageValidation Validate(ReadOnlySpan<byte> bytes, ImageLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        if (bytes.IsEmpty)
        {
            return new ImageValidation.Rejected(ImageRejection.Empty, "The image is empty.");
        }

        if (bytes.Length > limits.MaxCompressedBytes)
        {
            return new ImageValidation.Rejected(ImageRejection.TooLarge, $"The image has {bytes.Length} bytes; the limit is {limits.MaxCompressedBytes}.");
        }

        var copy = bytes.ToArray();
        using var data = SKData.CreateCopy(copy);
        using var codec = SKCodec.Create(data);
        if (codec is null)
        {
            return new ImageValidation.Rejected(ImageRejection.Malformed, "The bytes are not a recognizable image.");
        }

        AssetFormat? format = codec.EncodedFormat switch
        {
            SKEncodedImageFormat.Jpeg => AssetFormat.Jpeg,
            SKEncodedImageFormat.Png => AssetFormat.Png,
            SKEncodedImageFormat.Webp => AssetFormat.WebP,
            _ => null,
        };
        if (format is null)
        {
            return new ImageValidation.Rejected(ImageRejection.UnsupportedFormat, $"{codec.EncodedFormat} images are not supported.");
        }

        var (width, height) = (codec.Info.Width, codec.Info.Height);
        if (width <= 0 || height <= 0)
        {
            return new ImageValidation.Rejected(ImageRejection.Malformed, "The image reports no dimensions.");
        }

        if ((long)width * height > limits.MaxDecodedPixels)
        {
            return new ImageValidation.Rejected(ImageRejection.TooManyPixels, $"The image is {width}×{height}; the limit is {limits.MaxDecodedPixels} pixels.");
        }

        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var bitmap = new SKBitmap(info);
        var result = codec.GetPixels(info, bitmap.GetPixels());
        if (result != SKCodecResult.Success)
        {
            return new ImageValidation.Rejected(ImageRejection.Malformed, $"The image did not decode completely ({result}).");
        }

        var hash = new AssetHash(Convert.ToHexStringLower(SHA256.HashData(copy)));
        return new ImageValidation.Accepted(new AssetRecord(hash, format.Value, width, height, copy.Length));
    }
}
