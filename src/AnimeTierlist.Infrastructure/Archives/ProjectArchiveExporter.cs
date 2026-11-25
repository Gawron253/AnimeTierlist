using System.Collections.Immutable;
using System.IO.Compression;
using System.Text.Json;
using AnimeTierlist.Application.Assets;
using AnimeTierlist.Core;
using AnimeTierlist.Infrastructure.Assets;
using AnimeTierlist.Infrastructure.Storage;
using Microsoft.Data.Sqlite;

namespace AnimeTierlist.Infrastructure.Archives;

/// <summary>
/// Exports one saved revision as a <c>.animerank</c> archive. A consistent database backup is pruned to exported
/// data (the live project is never modified), checked against the referenced originals and the shared limits,
/// written to a temporary sibling, validated by the same-release importer, and only then moved into place.
/// </summary>
public static class ProjectArchiveExporter
{
    /// <summary>
    /// Writes the archive. Without <paramref name="replaceExisting"/>, an existing destination is refused; with it,
    /// the old file is kept unless the validated archive replaces it atomically.
    /// </summary>
    public static async Task ExportAsync(SqliteProjectStore store, string destinationFile, bool replaceExisting, ArchiveLimits limits, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationFile);
        ArgumentNullException.ThrowIfNull(limits);
        var destination = Path.GetFullPath(destinationFile);
        if (!replaceExisting && File.Exists(destination))
        {
            throw new ProjectArchiveException("The destination file already exists.");
        }

        var directory = Path.GetDirectoryName(destination) ?? throw new ProjectArchiveException("The destination must be inside a folder.");
        var unique = Guid.NewGuid().ToString("N");
        var staging = Path.Combine(directory, ".animerank-export-" + unique);
        var temporary = Path.Combine(directory, ".animerank-export-" + unique + ".tmp");
        Directory.CreateDirectory(staging);
        try
        {
            var database = Path.Combine(staging, ProjectArchive.DatabaseEntry);
            await store.BackupToAsync(database, cancellationToken).ConfigureAwait(false);
            var assets = await Task.Run(() => PrepareCopy(database), cancellationToken).ConfigureAwait(false);

            var originals = new AssetStore(store.ProjectDirectory);
            var broken = ImmutableArray.CreateBuilder<AssetHash>();
            foreach (var asset in assets)
            {
                if (await originals.CheckAsync(asset, cancellationToken).ConfigureAwait(false) != AssetAvailability.Available)
                {
                    broken.Add(asset.Hash);
                }
            }

            if (broken.Count > 0)
            {
                throw new ProjectArchiveException(
                    $"{broken.Count} referenced cover(s) are missing or damaged. Repair them by retrying acquisition, or resolve them through cover recovery, before exporting.",
                    broken.ToImmutable());
            }

            await Task.Run(() => WriteArchive(temporary, database, assets, originals, limits), cancellationToken).ConfigureAwait(false);
            await ProjectArchiveImporter.ValidateAsync(temporary, limits, cancellationToken).ConfigureAwait(false);
            try
            {
                File.Move(temporary, destination, overwrite: replaceExisting);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new ProjectArchiveException("The archive could not be finalized; any existing file at the destination was left unchanged.", ex);
            }
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }

            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }
        }
    }

    /// <summary>
    /// Prunes the export copy only: unreferenced originals, snapshots that no exported entry needs (kept snapshots stay
    /// whole, with every occurrence), and unreferenced items. Then compacts it into a self-contained rollback-journal file.
    /// </summary>
    private static ImmutableArray<AssetRecord> PrepareCopy(string database)
    {
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = database,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
            ForeignKeys = true,
        }.ToString()))
        {
            connection.Open();
            Execute(connection, "PRAGMA journal_mode = DELETE; PRAGMA trusted_schema = OFF;");
            using (var transaction = connection.BeginTransaction())
            {
                Execute(connection, """
                    DELETE FROM assets WHERE hash NOT IN (
                        SELECT cover_asset_hash FROM collection_entries WHERE cover_asset_hash IS NOT NULL
                        UNION SELECT pinned_asset_hash FROM board_entries WHERE pinned_asset_hash IS NOT NULL);
                    CREATE TEMP TABLE kept_snapshots AS
                        SELECT applied_snapshot_id AS id FROM collections
                        UNION SELECT accepted_snapshot_id FROM collection_entries
                        UNION SELECT cover_snapshot_id FROM collection_entries WHERE cover_snapshot_id IS NOT NULL;
                    DELETE FROM image_candidates WHERE snapshot_id NOT IN (SELECT id FROM kept_snapshots);
                    DELETE FROM occurrences WHERE snapshot_id NOT IN (SELECT id FROM kept_snapshots);
                    DELETE FROM season_captures WHERE snapshot_id NOT IN (SELECT id FROM kept_snapshots);
                    DELETE FROM snapshots WHERE id NOT IN (SELECT id FROM kept_snapshots);
                    DROP TABLE kept_snapshots;
                    DELETE FROM item_extras WHERE livechart_id NOT IN (
                        SELECT livechart_id FROM occurrences
                        UNION SELECT livechart_id FROM collection_entries
                        UNION SELECT livechart_id FROM board_entries);
                    DELETE FROM items WHERE livechart_id NOT IN (
                        SELECT livechart_id FROM occurrences
                        UNION SELECT livechart_id FROM collection_entries
                        UNION SELECT livechart_id FROM board_entries);
                    """, transaction);
                transaction.Commit();
            }

            // Removes pruned content from free pages and leaves every change in the main file.
            Execute(connection, "VACUUM;");
            var assets = ProjectReader.Read(connection).Assets.Values.OrderBy(a => a.Hash.Value, StringComparer.Ordinal).ToImmutableArray();
            if (Scalar(connection, "PRAGMA journal_mode") as string != "delete")
            {
                throw new ProjectArchiveException("The export copy could not be converted to a rollback-journal database.");
            }

            connection.Close();
            if (File.Exists(database + "-wal") || File.Exists(database + "-shm") || File.Exists(database + "-journal"))
            {
                throw new ProjectArchiveException("The export copy left database sidecar files.");
            }

            return assets;
        }
    }

    private static void WriteArchive(string temporary, string database, ImmutableArray<AssetRecord> assets, AssetStore originals, ArchiveLimits limits)
    {
        var manifest = new ArchiveManifest(ProjectArchive.Format, ProjectArchive.FormatVersion, ProjectSchema.Version,
            assets.Select(a => new ArchiveManifestAsset(a.RelativePath, a.Hash.Value, a.ByteLength)).ToImmutableArray());
        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, ProjectArchive.JsonOptions);
        var databaseBytes = new FileInfo(database).Length;

        // Preflight with the importer's limits so every successful export can be imported by this release.
        var total = manifestBytes.LongLength + databaseBytes + assets.Sum(a => a.ByteLength);
        if (assets.Length + 2 > limits.MaxEntries || databaseBytes > limits.MaxDatabaseBytes || manifestBytes.LongLength > limits.MaxManifestBytes
            || assets.Any(a => a.ByteLength > limits.Image.MaxCompressedBytes) || total > limits.MaxTotalBytes)
        {
            throw new ProjectArchiveException("The project is larger than an archive of this release allows.");
        }

        using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.WriteThrough))
        {
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            {
                using (var entry = zip.CreateEntry(ProjectArchive.ManifestEntry, CompressionLevel.Optimal).Open())
                {
                    entry.Write(manifestBytes);
                }

                zip.CreateEntryFromFile(database, ProjectArchive.DatabaseEntry, CompressionLevel.Optimal);
                foreach (var asset in assets)
                {
                    // Originals are already compressed formats.
                    zip.CreateEntryFromFile(originals.PathOf(asset), asset.RelativePath, CompressionLevel.NoCompression);
                }
            }

            stream.Flush(flushToDisk: true);
        }
    }

    private static void Execute(SqliteConnection connection, string sql, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }
}
