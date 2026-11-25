using System.Text.Json;

namespace AnimeTierlist.Infrastructure.Portable;

/// <summary>
/// Persists portable UI preferences strictly inside the resolved <c>data/</c> root as <c>settings.json</c>.
/// Reads are tolerant of missing, corrupt, or newer files, and writes use a temporary sibling file with atomic move.
/// </summary>
public sealed class PortableSettingsStore
{
    public const string FileName = "settings.json";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _settingsPath;

    /// <summary>Creates a settings store anchored strictly inside <paramref name="dataRoot"/>.</summary>
    public PortableSettingsStore(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        var fullRoot = Path.GetFullPath(dataRoot);
        _settingsPath = Path.Combine(fullRoot, FileName);
    }

    /// <summary>Full path to the portable settings file inside the resolved data root.</summary>
    public string SettingsPath => _settingsPath;

    /// <summary>
    /// Reads settings from disk, returning <paramref name="fallback"/> if the file does not exist or cannot be parsed.
    /// Never modifies a corrupt or unrecognized file on read.
    /// </summary>
    public T Load<T>(T fallback)
        where T : class
    {
        try
        {
            if (!File.Exists(_settingsPath))
            {
                return fallback;
            }

            var json = File.ReadAllText(_settingsPath);
            return JsonSerializer.Deserialize<T>(json, SerializerOptions) ?? fallback;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return fallback;
        }
    }

    /// <summary>Writes settings atomically via a temporary sibling file. Returns false on I/O failure without throwing.</summary>
    public bool TrySave<T>(T settings, out string? failure)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(settings);
        var directory = Path.GetDirectoryName(_settingsPath)!;
        var temporary = Path.Combine(directory, $".{FileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            Directory.CreateDirectory(directory);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(settings, SerializerOptions);
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, _settingsPath, overwrite: true);
            failure = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            failure = ex.Message;
            return false;
        }
        finally
        {
            try
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
