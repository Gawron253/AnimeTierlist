using System.Security.Cryptography;
using AnimeTierlist.Application.Assets;
using AnimeTierlist.Core;

namespace AnimeTierlist.Infrastructure.Assets;

/// <summary>
/// The project's immutable original store. Files are written to a temporary sibling, flushed, and moved into the
/// canonical hash path only after validation, so a crash can leave an unreferenced temporary file but never a
/// partial final file. A hash path never receives different content.
/// </summary>
public sealed class AssetStore : IAssetStore
{
    private const string TemporaryPrefix = ".incoming-";

    private readonly string _projectDirectory;

    public AssetStore(string projectDirectory)
    {
        _projectDirectory = projectDirectory;
    }

    public string PathOf(AssetRecord asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        return Path.Combine(_projectDirectory, asset.RelativePath.Replace('/', Path.DirectorySeparatorChar));
    }

    /// <summary>
    /// Installs already-validated bytes. Reuses a valid existing file; repairs a missing or damaged file with these
    /// exact bytes (same hash). Returns the record to commit after installation.
    /// </summary>
    public async Task<AssetRecord> InstallAsync(ReadOnlyMemory<byte> bytes, AssetRecord validated, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(validated);
        if (Convert.ToHexStringLower(SHA256.HashData(bytes.Span)) != validated.Hash.Value || bytes.Length != validated.ByteLength)
        {
            throw new InvalidOperationException("The bytes do not match their validated asset record.");
        }

        var target = PathOf(validated);
        if (await CheckAsync(validated, cancellationToken).ConfigureAwait(false) == AssetAvailability.Available)
        {
            return validated;
        }

        var directory = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, TemporaryPrefix + Guid.NewGuid().ToString("N"));
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            // Same-hash repair may replace a damaged file; identical content makes the replacement safe.
            File.Move(temporary, target, overwrite: true);
            return validated;
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    /// <summary>Checks the installed file against its recorded size and hash. The record is never changed.</summary>
    public async Task<AssetAvailability> CheckAsync(AssetRecord asset, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(asset);
        var path = PathOf(asset);
        if (!File.Exists(path))
        {
            return AssetAvailability.Missing;
        }

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length != asset.ByteLength)
        {
            return AssetAvailability.Damaged;
        }

        var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
        return hash == asset.Hash.Value ? AssetAvailability.Available : AssetAvailability.Damaged;
    }

    /// <summary>Removes interrupted temporary files. Installed originals are never touched.</summary>
    public void DeleteAbandonedTemporaryFiles()
    {
        var directory = Path.Combine(_projectDirectory, "assets");
        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(directory, TemporaryPrefix + "*"))
        {
            File.Delete(file);
        }
    }
}
