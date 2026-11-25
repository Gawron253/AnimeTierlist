using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AnimeTierlist.Core;
using AnimeTierlist.Infrastructure.Assets;

namespace AnimeTierlist.Desktop.Services;

/// <summary>
/// Loads frozen thumbnail bitmaps off the dispatcher with bounded concurrency, HiDPI resolution buckets,
/// and a byte-budgeted most-recently-used cache so large collections stay crisp within bounded memory.
/// </summary>
public sealed class ThumbnailLoader : IDisposable
{
    public const int PixelHeight = 160;
    public const int MediumPixelHeight = 240;
    public const int LargePixelHeight = 320;
    public const int ExtraLargePixelHeight = 480;
    private const long MaxDecodedBytes = 64L * 1024 * 1024;

    private readonly ThumbnailCache _cache;
    private readonly SemaphoreSlim _gate = new(2, 2);
    private readonly Lock _lock = new();
    private readonly Dictionary<(AssetHash Hash, int Height), LinkedListNode<CacheEntry>> _index = [];
    private readonly LinkedList<CacheEntry> _recent = new();
    private long _totalBytes;

    /// <summary>Creates a loader backed by the project's on-disk regenerable thumbnail cache.</summary>
    public ThumbnailLoader(ThumbnailCache cache)
    {
        _cache = cache;
    }

    /// <summary>Maps an arbitrary requested pixel height to a fixed thumbnail bucket (160, 240, 320, or 480 px).</summary>
    public static int BucketHeight(int requestedPixelHeight) => requestedPixelHeight switch
    {
        <= PixelHeight => PixelHeight,
        <= MediumPixelHeight => MediumPixelHeight,
        <= LargePixelHeight => LargePixelHeight,
        _ => ExtraLargePixelHeight,
    };

    /// <summary>Loads a standard 160 px thumbnail for list rows and compact displays.</summary>
    public Task<ImageSource?> LoadAsync(AssetRecord asset, CancellationToken cancellationToken) =>
        LoadAsync(asset, PixelHeight, cancellationToken);

    /// <summary>Loads a thumbnail sized for the requested physical pixel height using discrete cache buckets.</summary>
    public async Task<ImageSource?> LoadAsync(AssetRecord asset, int requestedPixelHeight, CancellationToken cancellationToken)
    {
        var bucket = BucketHeight(requestedPixelHeight);
        var key = (asset.Hash, bucket);
        if (TryGet(key, out var cached))
        {
            return cached;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (TryGet(key, out cached))
            {
                return cached;
            }

            var path = await _cache.GetAsync(asset, bucket, cancellationToken).ConfigureAwait(false);
            if (path is null)
            {
                return null;
            }

            (ImageSource Image, long Bytes) decoded;
            try
            {
                decoded = await Task.Run(() => DecodeBitmap(path, bucket), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException or FormatException or COMException or ArgumentException)
            {
                // Corrupt disposable cached thumbnail: invalidate and regenerate once from the validated original.
                _cache.Invalidate(asset.Hash, bucket);
                var regenerated = await _cache.GetAsync(asset, bucket, cancellationToken).ConfigureAwait(false);
                if (regenerated is null)
                {
                    return null;
                }

                try
                {
                    decoded = await Task.Run(() => DecodeBitmap(regenerated, bucket), cancellationToken).ConfigureAwait(false);
                }
                catch (Exception retryEx) when (retryEx is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException or FormatException or COMException or ArgumentException)
                {
                    return null;
                }
            }

            Add(key, decoded.Image, decoded.Bytes);
            return decoded.Image;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static (ImageSource Image, long Bytes) DecodeBitmap(string path, int bucket)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        bitmap.DecodePixelHeight = bucket;
        bitmap.UriSource = new Uri(path);
        bitmap.EndInit();
        bitmap.Freeze();
        var decodedBytes = Math.Max(4L * bucket * bucket / 2, (long)bitmap.PixelWidth * bitmap.PixelHeight * 4L);
        return (bitmap, decodedBytes);
    }

    /// <summary>Releases the loader's concurrency gate.</summary>
    public void Dispose() => _gate.Dispose();

    private bool TryGet((AssetHash Hash, int Height) key, out ImageSource? image)
    {
        lock (_lock)
        {
            if (_index.TryGetValue(key, out var node))
            {
                _recent.Remove(node);
                _recent.AddFirst(node);
                image = node.Value.Image;
                return true;
            }
        }

        image = null;
        return false;
    }

    private void Add((AssetHash Hash, int Height) key, ImageSource image, long decodedBytes)
    {
        lock (_lock)
        {
            if (_index.ContainsKey(key))
            {
                return;
            }

            _index[key] = _recent.AddFirst(new CacheEntry(key, image, decodedBytes));
            _totalBytes += decodedBytes;
            while (_totalBytes > MaxDecodedBytes && _recent.Count > 1)
            {
                var last = _recent.Last!;
                _recent.RemoveLast();
                _index.Remove(last.Value.Key);
                _totalBytes -= last.Value.DecodedBytes;
            }
        }
    }

    private sealed record CacheEntry((AssetHash Hash, int Height) Key, ImageSource Image, long DecodedBytes);
}
