using System.Collections.Immutable;
using System.Globalization;
using AnimeTierlist.Application.Storage;
using AnimeTierlist.Core;
using Microsoft.Data.Sqlite;

namespace AnimeTierlist.Infrastructure.Storage;

/// <summary>
/// A project folder's SQLite database. One instance owns the project for writing (enforced with an exclusive
/// lock file). Commits are serialized and each <see cref="ProjectChange"/> is one transaction.
/// </summary>
public sealed class SqliteProjectStore : IProjectStore
{
    public const string DatabaseFileName = "project.sqlite";
    public const string LockFileName = "project.lock";

    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly SqliteConnection _connection;
    private readonly FileStream _lock;
    private bool _disposed;

    private SqliteProjectStore(string projectDirectory, SqliteConnection connection, FileStream projectLock)
    {
        ProjectDirectory = projectDirectory;
        _connection = connection;
        _lock = projectLock;
    }

    public string ProjectDirectory { get; }

    /// <summary>Creates a new project in an empty or missing directory.</summary>
    public static Task<SqliteProjectStore> CreateAsync(string projectDirectory, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            Directory.CreateDirectory(projectDirectory);
            if (File.Exists(Path.Combine(projectDirectory, DatabaseFileName)))
            {
                throw new ProjectStoreException("A project already exists in this folder.");
            }

            Directory.CreateDirectory(Path.Combine(projectDirectory, "assets"));
            Directory.CreateDirectory(Path.Combine(projectDirectory, "cache"));
            var projectLock = AcquireLock(projectDirectory);
            try
            {
                var connection = Connect(projectDirectory, SqliteOpenMode.ReadWriteCreate);
                using (var transaction = connection.BeginTransaction())
                {
                    Execute(connection, transaction, ProjectSchema.Create);
                    Execute(connection, transaction, "INSERT INTO project_meta (key, value) VALUES ('application', $app)", ("$app", ProjectSchema.ApplicationId));
                    Execute(connection, transaction, $"PRAGMA user_version = {ProjectSchema.Version}");
                    transaction.Commit();
                }

                return new SqliteProjectStore(projectDirectory, connection, projectLock);
            }
            catch
            {
                projectLock.Dispose();
                throw;
            }
        }, cancellationToken);

    /// <summary>Opens an existing project for exclusive writing. Rejects newer or unknown schemas without modifying them; upgrades v1 projects safely.</summary>
    public static Task<SqliteProjectStore> OpenAsync(string projectDirectory, CancellationToken cancellationToken) =>
        OpenAsync(projectDirectory, beforeUpgradeInstall: null, cancellationToken);

    /// <summary>Internal overload supporting interrupted-upgrade rollback verification in tests.</summary>
    internal static Task<SqliteProjectStore> OpenAsync(string projectDirectory, Action? beforeUpgradeInstall, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            if (!File.Exists(Path.Combine(projectDirectory, DatabaseFileName)))
            {
                throw new ProjectStoreException("The folder does not contain a project.");
            }

            var projectLock = AcquireLock(projectDirectory);
            SqliteConnection? connection = null;
            try
            {
                int version;
                // Inspect read-only first so an unsupported database is never modified (no journal-mode change).
                using (var inspection = new SqliteConnection(new SqliteConnectionStringBuilder
                {
                    DataSource = Path.Combine(projectDirectory, DatabaseFileName),
                    Mode = SqliteOpenMode.ReadOnly,
                    Pooling = false,
                }.ToString()))
                {
                    inspection.Open();
                    version = Convert.ToInt32(Scalar(inspection, "PRAGMA user_version"), CultureInfo.InvariantCulture);
                    if (version > ProjectSchema.Version)
                    {
                        throw new ProjectStoreException($"This project uses schema version {version}, which is newer than this release supports ({ProjectSchema.Version}).");
                    }

                    var hasMeta = Convert.ToInt32(Scalar(inspection, "SELECT count(*) FROM sqlite_schema WHERE type = 'table' AND name = 'project_meta'"), CultureInfo.InvariantCulture) == 1;
                    var application = hasMeta ? Scalar(inspection, "SELECT value FROM project_meta WHERE key = 'application'") as string : null;
                    if (version is not (1 or 2 or ProjectSchema.Version) || application != ProjectSchema.ApplicationId)
                    {
                        throw new ProjectStoreException("The database is not a supported project.");
                    }
                }

                if (version < ProjectSchema.Version)
                {
                    ProjectSchemaUpgrader.UpgradeToLatest(projectDirectory, version, beforeUpgradeInstall);
                }

                connection = Connect(projectDirectory, SqliteOpenMode.ReadWrite);
                return new SqliteProjectStore(projectDirectory, connection, projectLock);
            }
            catch (SqliteException ex)
            {
                connection?.Dispose();
                projectLock.Dispose();
                throw new ProjectStoreException("The project database could not be opened.", ex);
            }
            catch
            {
                connection?.Dispose();
                projectLock.Dispose();
                throw;
            }
        }, cancellationToken);

    public async Task<ProjectData> LoadAsync(CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return await Task.Run(() => ProjectReader.Read(_connection), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task CommitAsync(ProjectChange change, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (change.IsEmpty)
            {
                return;
            }

            await Task.Run(() =>
            {
                using var transaction = _connection.BeginTransaction(deferred: false);
                try
                {
                    ProjectWriter.Apply(_connection, transaction, change);
                    transaction.Commit();
                }
                catch (SqliteException ex)
                {
                    transaction.Rollback();
                    throw new ProjectStoreException(Describe(ex), ex);
                }
            }, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>
    /// Writes a consistent copy of the last committed revision to a new database file, using SQLite's backup API
    /// between serialized commits. The live project is not modified.
    /// </summary>
    public async Task BackupToAsync(string destinationFile, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationFile);
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await Task.Run(() =>
            {
                if (File.Exists(destinationFile))
                {
                    throw new IOException("The backup destination already exists.");
                }

                using var destination = new SqliteConnection(new SqliteConnectionStringBuilder
                {
                    DataSource = destinationFile,
                    Mode = SqliteOpenMode.ReadWriteCreate,
                    Pooling = false,
                }.ToString());
                destination.Open();
                _connection.BackupDatabase(destination);
            }, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _writeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            await _connection.DisposeAsync().ConfigureAwait(false);
            await _lock.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }

        _writeGate.Dispose();
    }

    internal static SqliteConnection Connect(string projectDirectory, SqliteOpenMode mode)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(projectDirectory, DatabaseFileName),
            Mode = mode,
            Pooling = false,
            ForeignKeys = true,
        };
        var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        Execute(connection, null, "PRAGMA journal_mode = WAL; PRAGMA synchronous = FULL; PRAGMA trusted_schema = OFF;");
        return connection;
    }

    internal static void Execute(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        command.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static FileStream AcquireLock(string projectDirectory)
    {
        try
        {
            return new FileStream(Path.Combine(projectDirectory, LockFileName), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
        }
        catch (IOException ex)
        {
            throw new ProjectStoreException("The project is already open in another window or process.", ex);
        }
    }

    private static string Describe(SqliteException ex) => ex.SqliteErrorCode switch
    {
        19 => $"The change was rejected because it would break a project rule ({ex.Message}).",
        13 => "The disk is full.",
        _ => $"The project could not be saved ({ex.Message}).",
    };
}
