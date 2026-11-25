using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AnimeTierlist.Core;
using AnimeTierlist.Infrastructure.Assets;

namespace AnimeTierlist.Infrastructure.Archives;

/// <summary>
/// Limits shared by export preflight and import, so every exported archive is importable by the same release.
/// Byte limits count actual (uncompressed) entry bytes, including the database and manifest.
/// </summary>
public sealed record ArchiveLimits(int MaxEntries, long MaxDatabaseBytes, long MaxManifestBytes, long MaxTotalBytes, ImageLimits Image)
{
    /// <summary>Default safety limits: at most 20,000 files and 1 GiB expanded, including the manifest and database.</summary>
    public static ArchiveLimits Default { get; } = new(
        MaxEntries: 20_000,
        MaxDatabaseBytes: 512L * 1024 * 1024,
        MaxManifestBytes: 8L * 1024 * 1024,
        MaxTotalBytes: 1024L * 1024 * 1024,
        Image: ImageLimits.Default);
}

/// <summary>An archive could not be exported or imported. Nothing was installed or replaced.</summary>
public sealed class ProjectArchiveException : Exception
{
    public ProjectArchiveException()
    {
        BrokenAssets = [];
    }

    public ProjectArchiveException(string message)
        : base(message)
    {
        BrokenAssets = [];
    }

    public ProjectArchiveException(string message, Exception innerException)
        : base(message, innerException)
    {
        BrokenAssets = [];
    }

    public ProjectArchiveException(string message, ImmutableArray<AssetHash> brokenAssets)
        : base(message)
    {
        BrokenAssets = brokenAssets;
    }

    /// <summary>Referenced originals that are missing or damaged and must be repaired or explicitly resolved before export.</summary>
    public ImmutableArray<AssetHash> BrokenAssets { get; }
}

/// <summary>The <c>.animerank</c> container: a ZIP with exactly a manifest, the database, and referenced originals.</summary>
internal static partial class ProjectArchive
{
    public const string ManifestEntry = "manifest.json";
    public const string DatabaseEntry = "project.sqlite";
    public const string Format = "AnimeTierlist.Archive";
    public const int FormatVersion = 1;

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        AllowDuplicateProperties = false,
        MaxDepth = 8,
        WriteIndented = true,
    };

    /// <summary>The only accepted spelling of an original's path: lowercase SHA-256 and an allowlisted extension.</summary>
    [GeneratedRegex("^assets/(?<hash>[0-9a-f]{64})\\.(?<ext>jpg|png|webp)$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    public static partial Regex AssetPath();
}

internal sealed record ArchiveManifest(string Format, int FormatVersion, int SchemaVersion, ImmutableArray<ArchiveManifestAsset> Assets);

internal sealed record ArchiveManifestAsset(string Path, string Sha256, long Bytes);
