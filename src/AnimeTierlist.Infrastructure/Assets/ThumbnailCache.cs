using AnimeTierlist.Core;
using SkiaSharp;

namespace AnimeTierlist.Infrastructure.Assets;

/// <summary>
/// Regenerable thumbnails under the project's <c>cache/</c> folder, keyed by original hash and size. Originals are
/// decoded at a reduced scale where the codec supports it, so the interface never holds full-resolution covers.
/// Thumbnails are disposable: a damaged or missing one is simply regenerated from a valid original.
/// </summary>
public sealed class ThumbnailCache
{
    private readonly string _directory;
    private readonly AssetStore _originals;

    public ThumbnailCache(string projectDirectory, AssetStore originals)
    {
        _directory = Path.Combine(projectDirectory, "cache", "thumbnails");
        _originals = originals;
    }

    /// <summary>Returns the path of a JPEG thumbnail at most <paramref name="maxHeight"/> pixels tall, or null when the original is unavailable.</summary>
    public async Task<string?> GetAsync(AssetRecord asset, int maxHeight, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxHeight, 16);
        var target = Path.Combine(_directory, $"{asset.Hash.Value}-{maxHeight}.jpg");
        if (File.Exists(target))
        {
            return target;
        }

        var source = _originals.PathOf(asset);
        if (!File.Exists(source))
        {
            return null;
        }

        return await Task.Run(() => Generate(source, target, maxHeight), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Deletes a corrupt or stale disposable cached thumbnail so the next request regenerates it from the original.</summary>
    public void Invalidate(AssetHash hash, int maxHeight)
    {
        var target = Path.Combine(_directory, $"{hash.Value}-{maxHeight}.jpg");
        try
        {
            if (File.Exists(target))
            {
                File.Delete(target);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private string? Generate(string source, string target, int maxHeight)
    {
        using var codec = SKCodec.Create(source);
        if (codec is null)
        {
            return null;
        }

        var info = codec.Info;
        var scale = Math.Min(1f, (float)maxHeight / info.Height);
        var decodeSize = codec.GetScaledDimensions(scale);
        if (decodeSize.Width <= 0 || decodeSize.Height <= 0)
        {
            decodeSize = info.Size;
        }

        using var decoded = new SKBitmap(new SKImageInfo(decodeSize.Width, decodeSize.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        var result = codec.GetPixels(decoded.Info, decoded.GetPixels());
        if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
        {
            return null;
        }

        var width = Math.Max(1, (int)Math.Round(info.Width * scale));
        var height = Math.Max(1, (int)Math.Round(info.Height * scale));

        // Resample into the final size on white, so transparent PNG covers stay legible as JPEG.
        using var decodedImage = SKImage.FromBitmap(decoded);
        using var surface = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(surface))
        {
            canvas.Clear(SKColors.White);
            canvas.DrawImage(decodedImage, SKRect.Create(width, height), new SKSamplingOptions(SKCubicResampler.Mitchell));
        }

        using var image = SKImage.FromBitmap(surface);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 85);
        Directory.CreateDirectory(_directory);
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = File.Create(temporary))
            {
                data.SaveTo(stream);
            }

            File.Move(temporary, target, overwrite: true);
            return target;
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }
}
