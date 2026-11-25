namespace AnimeTierlist.Infrastructure.Portable;

/// <summary>
/// The portable data root: <c>data/</c> beside the executable by default. When that location is not writable the
/// user chooses another root for the current session only; nothing outside the root records the choice.
/// </summary>
public static class PortableDataRoot
{
    public const string FolderName = "data";

    public static string DefaultFor(string applicationDirectory) => Path.Combine(applicationDirectory, FolderName);

    public static string ProjectsFolder(string dataRoot) => Path.Combine(dataRoot, "projects");

    /// <summary>Creates the folder if needed and proves a file can be written and removed there.</summary>
    public static bool TryProbeWritable(string directory, out string? failure)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, $".write-probe-{Guid.NewGuid():N}");
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
            {
            }

            failure = null;
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or NotSupportedException)
        {
            failure = ex.Message;
            return false;
        }
    }
}
