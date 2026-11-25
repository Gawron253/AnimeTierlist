using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using AnimeTierlist.Application.Session;
using AnimeTierlist.Application.Storage;
using AnimeTierlist.Core;
using AnimeTierlist.Infrastructure.Assets;
using AnimeTierlist.Infrastructure.Storage;
using Microsoft.Data.Sqlite;

namespace AnimeTierlist.Infrastructure.Archives;

/// <summary>
/// Imports <c>.animerank</c> archives. Everything is extracted into a staging folder beside the destination and
/// validated there (container, manifest, database file mode, exact schema objects, integrity, domain invariants, and
/// every original through the bounded image validator) before one directory rename installs the project.
/// Any failure leaves the destination absent and removes the staging folder.
/// </summary>
public static class ProjectArchiveImporter
{
    private const string StagingPrefix = ".animerank-import-";

    /// <summary>Imports into a new project folder, which must not exist yet.</summary>
    public static async Task ImportAsync(string archivePath, string destinationDirectory, ArchiveLimits limits, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        ArgumentNullException.ThrowIfNull(limits);
        var destination = Path.GetFullPath(destinationDirectory);
        if (Directory.Exists(destination) || File.Exists(destination))
        {
            throw new ProjectArchiveException("The destination already exists. Choose a new project folder; existing projects are never overwritten.");
        }

        var parent = Path.GetDirectoryName(destination) ?? throw new ProjectArchiveException("The destination must be inside a folder.");
        Directory.CreateDirectory(parent);
        var staging = Path.Combine(parent, StagingPrefix + Guid.NewGuid().ToString("N"));
        try
        {
            await Task.Run(() => ExtractAndValidate(archivePath, staging, limits, cancellationToken), cancellationToken).ConfigureAwait(false);
            Directory.CreateDirectory(Path.Combine(staging, "assets"));
            Directory.CreateDirectory(Path.Combine(staging, "cache"));
            try
            {
                Directory.Move(staging, destination);
            }
            catch (IOException ex)
            {
                throw new ProjectArchiveException("The project could not be installed because the destination was created or is unavailable.", ex);
            }
        }
        finally
        {
            DeleteDirectory(staging);
        }
    }

    /// <summary>Runs the full import validation without installing anything.</summary>
    public static async Task ValidateAsync(string archivePath, ArchiveLimits limits, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        ArgumentNullException.ThrowIfNull(limits);
        var parent = Path.GetDirectoryName(Path.GetFullPath(archivePath)) ?? Path.GetTempPath();
        var staging = Path.Combine(parent, StagingPrefix + Guid.NewGuid().ToString("N"));
        try
        {
            await Task.Run(() => ExtractAndValidate(archivePath, staging, limits, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            DeleteDirectory(staging);
        }
    }

    private static void ExtractAndValidate(string archivePath, string staging, ArchiveLimits limits, CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(Path.Combine(staging, "assets"));
            var assetSizes = Extract(archivePath, staging, limits, cancellationToken);
            var manifest = ReadManifest(Path.Combine(staging, ProjectArchive.ManifestEntry), limits);
            ValidateInventory(manifest, assetSizes);
            var database = Path.Combine(staging, ProjectArchive.DatabaseEntry);
            RequireRollbackJournalHeader(database);
            if (manifest.SchemaVersion is 1 or 2)
            {
                ValidatePriorSchemaDatabase(database, manifest.SchemaVersion);
                ProjectSchemaUpgrader.UpgradeToLatest(staging, manifest.SchemaVersion);
                RequireRollbackJournalHeader(database);
            }

            var data = InspectDatabase(database);
            ValidateDomain(data, manifest);
            ValidateOriginals(staging, data, limits, cancellationToken);
        }
        catch (Exception ex) when (ex is InvalidDataException or SqliteException or JsonException or FormatException or ArgumentException
            or InvalidOperationException or KeyNotFoundException or InvalidCastException or OverflowException or NotSupportedException
            or ProjectStoreException)
        {
            throw new ProjectArchiveException($"The archive is not a valid project: {ex.Message}", ex);
        }
    }

    /// <summary>Extracts the exact allowed entries with bounded copies. Returns actual byte counts of asset entries by path.</summary>
    private static Dictionary<string, long> Extract(string archivePath, string staging, ArchiveLimits limits, CancellationToken cancellationToken)
    {
        using var file = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan);
        // A quick bound only: stored (uncompressed) entries plus ZIP headers can make a valid file slightly larger
        // than its expanded content. The authoritative limits are the streamed byte counts below.
        if (file.Length > limits.MaxTotalBytes + (1024L * limits.MaxEntries) + 65_536)
        {
            throw new ProjectArchiveException("The archive is larger than this release accepts.");
        }

        using var zip = new ZipArchive(file, ZipArchiveMode.Read);
        if (zip.Entries.Count > limits.MaxEntries)
        {
            throw new ProjectArchiveException($"The archive has {zip.Entries.Count} entries; at most {limits.MaxEntries} are allowed.");
        }

        var root = Path.GetFullPath(staging) + Path.DirectorySeparatorChar;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var assets = new Dictionary<string, long>(StringComparer.Ordinal);
        long total = 0;
        foreach (var entry in zip.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = entry.FullName;
            if (!seen.Add(name))
            {
                throw new ProjectArchiveException($"The archive contains a duplicate entry '{name}'.");
            }

            if (entry.IsEncrypted)
            {
                throw new ProjectArchiveException($"The archive entry '{name}' is encrypted.");
            }

            // Unix-created entries carry the file type in the high 16 bits; anything but a regular file (for example a
            // symbolic link) is rejected even though extraction only ever writes regular files. Windows-created entries
            // carry file attributes in the low bits; a reparse point (junction or symbolic link) is rejected likewise.
            const int windowsReparsePoint = 0x400;
            var unixType = (entry.ExternalAttributes >> 16) & 0xF000;
            if ((unixType != 0 && unixType != 0x8000) || (entry.ExternalAttributes & windowsReparsePoint) != 0)
            {
                throw new ProjectArchiveException($"The archive entry '{name}' is a link or special file.");
            }

            long limit;
            if (name == ProjectArchive.ManifestEntry)
            {
                limit = limits.MaxManifestBytes;
            }
            else if (name == ProjectArchive.DatabaseEntry)
            {
                limit = limits.MaxDatabaseBytes;
            }
            else if (ProjectArchive.AssetPath().IsMatch(name))
            {
                limit = limits.Image.MaxCompressedBytes;
            }
            else
            {
                throw new ProjectArchiveException($"The archive contains an unexpected entry '{name}'.");
            }

            var target = Path.GetFullPath(Path.Combine(staging, name.Replace('/', Path.DirectorySeparatorChar)));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                throw new ProjectArchiveException($"The archive entry '{name}' resolves outside the project.");
            }

            var written = CopyBounded(entry, target, limit, ref total, limits.MaxTotalBytes);
            if (name.StartsWith("assets/", StringComparison.Ordinal))
            {
                assets[name] = written;
            }
        }

        if (!seen.Contains(ProjectArchive.ManifestEntry) || !seen.Contains(ProjectArchive.DatabaseEntry))
        {
            throw new ProjectArchiveException("The archive must contain manifest.json and project.sqlite.");
        }

        return assets;
    }

    private static long CopyBounded(ZipArchiveEntry entry, string target, long limit, ref long total, long totalLimit)
    {
        if (entry.Length > limit)
        {
            throw new ProjectArchiveException($"The archive entry '{entry.FullName}' is larger than allowed.");
        }

        using var input = entry.Open();
        using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920);
        var buffer = new byte[81920];
        long written = 0;
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            written += read;
            total += read;
            if (written > limit || total > totalLimit)
            {
                throw new ProjectArchiveException($"The archive entry '{entry.FullName}' expands beyond the allowed size.");
            }

            output.Write(buffer, 0, read);
        }

        if (written != entry.Length)
        {
            throw new ProjectArchiveException($"The archive entry '{entry.FullName}' does not match its declared size.");
        }

        return written;
    }

    private static ArchiveManifest ReadManifest(string path, ArchiveLimits limits)
    {
        if (new FileInfo(path).Length > limits.MaxManifestBytes)
        {
            throw new ProjectArchiveException("The manifest is larger than allowed.");
        }

        var manifest = JsonSerializer.Deserialize<ArchiveManifest>(File.ReadAllBytes(path), ProjectArchive.JsonOptions)
            ?? throw new ProjectArchiveException("The manifest is empty.");
        if (manifest.Format != ProjectArchive.Format)
        {
            throw new ProjectArchiveException("The file is not an AnimeTierlist project archive.");
        }

        if (manifest.FormatVersion > ProjectArchive.FormatVersion || manifest.SchemaVersion > ProjectSchema.Version)
        {
            throw new ProjectArchiveException("The archive was created by a newer release and cannot be imported by this version.");
        }

        if (manifest.FormatVersion != ProjectArchive.FormatVersion || manifest.SchemaVersion is not (1 or 2 or ProjectSchema.Version))
        {
            throw new ProjectArchiveException("The archive version is not supported.");
        }

        return manifest;
    }

    /// <summary>The manifest inventory and the asset entries must match one to one, including byte counts.</summary>
    private static void ValidateInventory(ArchiveManifest manifest, Dictionary<string, long> assetEntries)
    {
        var listed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var asset in manifest.Assets)
        {
            var match = ProjectArchive.AssetPath().Match(asset.Path);
            if (!match.Success || match.Groups["hash"].Value != asset.Sha256 || !listed.Add(asset.Path))
            {
                throw new ProjectArchiveException($"The manifest lists an invalid or duplicate original '{asset.Path}'.");
            }

            if (!assetEntries.TryGetValue(asset.Path, out var bytes) || bytes != asset.Bytes)
            {
                throw new ProjectArchiveException($"The manifest entry '{asset.Path}' does not match the archive contents.");
            }
        }

        var unlisted = assetEntries.Keys.FirstOrDefault(path => !listed.Contains(path));
        if (unlisted is not null)
        {
            throw new ProjectArchiveException($"The archive contains '{unlisted}', which the manifest does not list.");
        }
    }

    /// <summary>Archive databases must use rollback-journal header mode; WAL-mode files are rejected before any SQL runs.</summary>
    private static void RequireRollbackJournalHeader(string database)
    {
        Span<byte> header = stackalloc byte[100];
        using (var stream = File.OpenRead(database))
        {
            if (stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) < header.Length)
            {
                throw new ProjectArchiveException("The archive database is truncated.");
            }
        }

        if (!header[..16].SequenceEqual("SQLite format 3\0"u8))
        {
            throw new ProjectArchiveException("The archive database is not an SQLite database.");
        }

        if (header[18] != 1 || header[19] != 1)
        {
            throw new ProjectArchiveException("The archive database uses write-ahead logging; archives must contain a self-contained rollback-journal database.");
        }
    }

    /// <summary>
    /// Validates a prior-version (v1 or v2) archive database read-only against its exact schema before staging upgrade.
    /// </summary>
    private static void ValidatePriorSchemaDatabase(string database, int schemaVersion)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = database,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        connection.Open();
        var ddl = schemaVersion == 1 ? ProjectSchema.CreateV1 : ProjectSchema.CreateV2;
        VerifySchemaAndIntegrity(connection, schemaVersion, ddl);
    }

    /// <summary>
    /// Opens the untrusted database read-only with untrusted schema features disabled, requires the exact schema
    /// objects of this release, checks integrity and foreign keys, and reads it into validated domain objects.
    /// </summary>
    private static ProjectData InspectDatabase(string database)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = database,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        connection.Open();
        VerifySchemaAndIntegrity(connection, ProjectSchema.Version, ProjectSchema.Create);
        return ProjectReader.Read(connection);
    }

    /// <summary>Verifies untrusted schema safety, exact DDL match, version, metadata, integrity, and references.</summary>
    private static void VerifySchemaAndIntegrity(SqliteConnection connection, int expectedVersion, string expectedDdl)
    {
        Execute(connection, "PRAGMA trusted_schema = OFF; PRAGMA query_only = ON;");

        var expected = ReferenceSchema(expectedDdl);
        var actual = SchemaObjects(connection);
        if (!expected.SequenceEqual(actual))
        {
            var unexpected = actual.Except(expected).Select(o => $"{o.Type} {o.Name}").FirstOrDefault() ?? "a missing object";
            throw new ProjectArchiveException($"The archive database does not have this release's exact schema (unexpected: {unexpected}).");
        }

        var version = Convert.ToInt32(Scalar(connection, "PRAGMA user_version"), CultureInfo.InvariantCulture);
        if (version != expectedVersion)
        {
            throw new ProjectArchiveException($"The archive database schema version {version} is not supported.");
        }

        var meta = Rows(connection, "SELECT key, value FROM project_meta ORDER BY key").ToList();
        if (meta.Count != 1 || meta[0] != ("application", ProjectSchema.ApplicationId))
        {
            throw new ProjectArchiveException("The archive database is not an AnimeTierlist project.");
        }

        if (Scalar(connection, "PRAGMA integrity_check") as string != "ok")
        {
            throw new ProjectArchiveException("The archive database failed its integrity check.");
        }

        if (Rows(connection, "PRAGMA foreign_key_check").Any())
        {
            throw new ProjectArchiveException("The archive database has broken references.");
        }

        if (Rows(connection, "SELECT CAST(livechart_id AS TEXT), '' FROM items WHERE livechart_id NOT IN (SELECT livechart_id FROM occurrences UNION SELECT livechart_id FROM collection_entries UNION SELECT livechart_id FROM board_entries)").Any())
        {
            throw new ProjectArchiveException("The archive database contains unreferenced items.");
        }
    }

    /// <summary>Validates domain and cross-record rules independently of the database's declared constraints.</summary>
    private static void ValidateDomain(ProjectData data, ArchiveManifest manifest)
    {
        var state = ProjectState.FromData(data);
        var referenced = new HashSet<AssetHash>();
        foreach (var collection in state.Collections.Values)
        {
            foreach (var entry in collection.Entries)
            {
                if (entry.Cover is not { } cover)
                {
                    continue;
                }

                referenced.Add(cover.Asset);
                if (!state.Observation(cover.Occurrence).ImageCandidates.Any(c => c.Url == cover.RequestedUrl) || string.IsNullOrWhiteSpace(cover.RequestContext))
                {
                    throw new ProjectArchiveException($"The cover provenance for item {entry.Item} does not belong to its cited source occurrence.");
                }
            }
        }

        foreach (var board in state.Boards.Values)
        {
            referenced.UnionWith(board.Entries.Where(e => e.PinnedCover is not null).Select(e => e.PinnedCover!.Value));
        }

        if (state.Assets.Keys.Any(hash => !referenced.Contains(hash)))
        {
            throw new ProjectArchiveException("The archive database contains unreferenced originals.");
        }

        var inventory = manifest.Assets.ToDictionary(a => a.Path, StringComparer.Ordinal);
        if (inventory.Count != state.Assets.Count)
        {
            throw new ProjectArchiveException("The manifest inventory does not match the database's originals.");
        }

        foreach (var asset in state.Assets.Values)
        {
            if (!inventory.TryGetValue(asset.RelativePath, out var listed) || listed.Bytes != asset.ByteLength)
            {
                throw new ProjectArchiveException($"Original {asset.Hash} does not match the manifest inventory.");
            }
        }
    }

    /// <summary>Every original passes the same bounded decoder validation as a download and matches its recorded properties.</summary>
    private static void ValidateOriginals(string staging, ProjectData data, ArchiveLimits limits, CancellationToken cancellationToken)
    {
        foreach (var asset in data.Assets.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bytes = File.ReadAllBytes(Path.Combine(staging, asset.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
            if (ImageValidator.Validate(bytes, limits.Image) is not ImageValidation.Accepted { Asset: var observed })
            {
                throw new ProjectArchiveException($"Original {asset.Hash} is not a valid supported image.");
            }

            if (observed != asset)
            {
                throw new ProjectArchiveException($"Original {asset.Hash} does not match its recorded hash, format, or dimensions.");
            }
        }
    }

    private static List<SchemaObject> ReferenceSchema(string ddl)
    {
        using var reference = new SqliteConnection("Data Source=:memory:;Pooling=False");
        reference.Open();
        Execute(reference, ddl);
        return SchemaObjects(reference);
    }

    /// <summary>
    /// Reads the schema objects. SQLite stores each object's CREATE text as written, and the reference text comes from
    /// a raw string literal whose line endings follow the building checkout (LF or CRLF), so line endings are
    /// normalized; any other difference in the text still counts.
    /// </summary>
    private static List<SchemaObject> SchemaObjects(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT type, name, tbl_name, sql FROM sqlite_schema ORDER BY type, name";
        using var reader = command.ExecuteReader();
        var objects = new List<SchemaObject>();
        while (reader.Read())
        {
            objects.Add(new SchemaObject(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3).ReplaceLineEndings("\n")));
        }

        return objects;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static IEnumerable<(string, string)> Rows(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            yield return (Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture) ?? string.Empty,
                reader.FieldCount > 1 ? Convert.ToString(reader.GetValue(1), CultureInfo.InvariantCulture) ?? string.Empty : string.Empty);
        }
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private readonly record struct SchemaObject(string Type, string Name, string Table, string? Sql);
}
