using AnimeTierlist.Core;

namespace AnimeTierlist.Application.Assets;

public enum AssetAvailability
{
    Available,
    Missing,
    Damaged,
}

/// <summary>The project's immutable original store, as seen by application workflows.</summary>
public interface IAssetStore
{
    /// <summary>Checks the installed file against its recorded size and hash. The record is never changed.</summary>
    Task<AssetAvailability> CheckAsync(AssetRecord asset, CancellationToken cancellationToken);
}
