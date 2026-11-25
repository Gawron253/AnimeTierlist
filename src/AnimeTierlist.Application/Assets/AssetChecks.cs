using AnimeTierlist.Core;

namespace AnimeTierlist.Application.Assets;

public static class AssetChecks
{
    /// <summary>
    /// Returns the hashes of the given assets whose files match their recorded size and hash. Unreadable files count
    /// as not intact. Used to decide which covers a board may pin.
    /// </summary>
    public static async Task<IReadOnlySet<AssetHash>> IntactAsync(IAssetStore store, IEnumerable<AssetRecord> assets, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(assets);
        var intact = new HashSet<AssetHash>();
        foreach (var asset in assets.DistinctBy(a => a.Hash))
        {
            try
            {
                if (await store.CheckAsync(asset, cancellationToken).ConfigureAwait(false) == AssetAvailability.Available)
                {
                    intact.Add(asset.Hash);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Unreadable: not intact.
            }
        }

        return intact;
    }
}
