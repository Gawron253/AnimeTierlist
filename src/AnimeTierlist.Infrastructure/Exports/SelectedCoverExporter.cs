using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using AnimeTierlist.Application.Assets;
using AnimeTierlist.Core;
using AnimeTierlist.Infrastructure.Assets;

namespace AnimeTierlist.Infrastructure.Exports;

public sealed record SelectedCoverManifestEntry(int Order, int LiveChartId, string Title, string AssetHash, string File);

public sealed record SelectedCoverManifest(string Format, int Version, string Collection, ImmutableArray<SelectedCoverManifestEntry> Entries);

/// <summary>
/// Exports the covers of a collection's included shows, in effective collection order, into a new folder with a
/// JSON manifest. Search filters never apply. The folder is staged beside the destination and installed by rename,
/// so the outcome is either no destination or a complete new export.
/// </summary>
public static class SelectedCoverExporter
{
    public const string ManifestFile = "manifest.json";

    /// <summary>Longest full path written, so every file stays within the classic Windows path limit.</summary>
    public const int MaxPathLength = 259;

    /// <summary>Titles in file names are capped at this many characters even when the path budget allows more.</summary>
    public const int MaxTitleLength = 80;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static async Task<SelectedCoverManifest> ExportAsync(
        CollectionState collection,
        Func<CollectionEntry, string> resolveTitle,
        IReadOnlyDictionary<AssetHash, AssetRecord> assets,
        AssetStore store,
        string destinationDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(collection);
        ArgumentNullException.ThrowIfNull(resolveTitle);
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        var destination = Path.GetFullPath(destinationDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var parent = Path.GetDirectoryName(destination) ?? throw new ExportException("The destination must be inside a folder.");
        if (Directory.Exists(destination) || File.Exists(destination))
        {
            throw new ExportException("The destination already exists. Cover export always creates a new folder.");
        }

        var included = collection.IncludedEntries.ToList();
        if (included.Count == 0)
        {
            throw new ExportException("No shows are included in this collection, so there is nothing to export.");
        }

        var staging = Path.Combine(parent, $".{Path.GetFileName(destination)}.staging-{Guid.NewGuid().ToString("N")[..8]}");
        var titles = included.Select(resolveTitle).ToList();
        var records = included.Select(e => e.Cover is { } c && assets.TryGetValue(c.Asset, out var r) ? r : null).ToList();
        var names = FileNames(included.Select((e, i) => (e.Item, titles[i], records[i] is { } r ? Path.GetExtension(store.PathOf(r)) : ".jpg")).ToList(), Math.Max(staging.Length, destination.Length));
        var affected = ImmutableArray.CreateBuilder<string>();
        try
        {
            // Each original is revalidated and written into disposable staging one at a time, so memory stays bounded
            // by a single image; nothing is installed unless every entry passed.
            Directory.CreateDirectory(staging);
            for (var i = 0; i < included.Count; i++)
            {
                if (records[i] is not { } record)
                {
                    affected.Add($"{titles[i]} [{included[i].Item.Value}] (no cover)");
                    continue;
                }

                var bytes = await ReadValidatedAsync(store, record, cancellationToken).ConfigureAwait(false);
                if (bytes is null)
                {
                    affected.Add($"{titles[i]} [{included[i].Item.Value}] (cover file missing or damaged)");
                    continue;
                }

                if (affected.Count == 0)
                {
                    await WriteAsync(Path.Combine(staging, names[i]), bytes, cancellationToken).ConfigureAwait(false);
                }
            }

            if (affected.Count > 0)
            {
                throw new ExportException("These included shows have no usable cover, so nothing was exported. Fetch covers or use cover recovery, or exclude them.", affected.ToImmutable());
            }

            var manifest = new SelectedCoverManifest("animetierlist.selected-covers", 1, collection.Name,
                included.Select((e, i) => new SelectedCoverManifestEntry(i + 1, e.Item.Value, titles[i], records[i]!.Hash.Value, names[i])).ToImmutableArray());
            await WriteAsync(Path.Combine(staging, ManifestFile), JsonSerializer.SerializeToUtf8Bytes(manifest, Json), cancellationToken).ConfigureAwait(false);
            if (Directory.Exists(destination) || File.Exists(destination))
            {
                throw new ExportException("The destination was created during the export, so it was left untouched.");
            }

            Directory.Move(staging, destination);
            return manifest;
        }
        catch (IOException ex)
        {
            throw new ExportException("The covers could not be exported: " + ex.Message, ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new ExportException("The covers could not be exported: " + ex.Message, ex);
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }
        }
    }

    /// <summary>
    /// "0001 Title [12345].jpg": a zero-padded order prefix, the sanitized and truncated resolved title, the LiveChart
    /// ID for collision resistance, and the verified extension. The order prefix means a name can never equal a
    /// reserved Windows device name. Fails rather than truncate the prefix, ID, or extension.
    /// </summary>
    internal static IReadOnlyList<string> FileNames(IReadOnlyList<(LiveChartId Item, string Title, string Extension)> entries, int longestFolderPath)
    {
        var digits = Math.Max(4, entries.Count.ToString(CultureInfo.InvariantCulture).Length);
        var budget = MaxPathLength - longestFolderPath - 1;
        var names = new List<string>();
        for (var i = 0; i < entries.Count; i++)
        {
            var (item, title, extension) = entries[i];
            var prefix = (i + 1).ToString("D" + digits.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture) + " ";
            var id = $" [{item.Value.ToString(CultureInfo.InvariantCulture)}]";
            var room = Math.Min(MaxTitleLength, budget - prefix.Length - id.Length - extension.Length);
            if (room < 1)
            {
                throw new ExportException("The destination folder path is too long for the cover file names. Choose a shorter location.");
            }

            names.Add(prefix + Truncate(Sanitize(title), room) + id + extension);
        }

        return names;
    }

    internal static string Sanitize(string title)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(title.Length);
        foreach (var c in title.Normalize(NormalizationForm.FormC))
        {
            builder.Append(char.IsControl(c) || invalid.Contains(c) ? '_' : char.IsWhiteSpace(c) ? ' ' : c);
        }

        var cleaned = string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim().TrimEnd('.', ' ');
        return cleaned.Length == 0 ? "untitled" : cleaned;
    }

    private static string Truncate(string text, int maxLength)
    {
        if (text.Length <= maxLength)
        {
            return text;
        }

        // Cut on text-element boundaries so no surrogate pair or combining sequence is split.
        var result = new StringBuilder();
        var e = StringInfo.GetTextElementEnumerator(text);
        while (e.MoveNext() && result.Length + ((string)e.Current).Length <= maxLength)
        {
            result.Append((string)e.Current);
        }

        var cut = result.ToString().TrimEnd('.', ' ');
        return cut.Length == 0 ? "untitled"[..Math.Min(8, maxLength)] : cut;
    }

    private static async Task<byte[]?> ReadValidatedAsync(AssetStore store, AssetRecord record, CancellationToken cancellationToken)
    {
        try
        {
            if (await store.CheckAsync(record, cancellationToken).ConfigureAwait(false) != AssetAvailability.Available)
            {
                return null;
            }

            var bytes = await File.ReadAllBytesAsync(store.PathOf(record), cancellationToken).ConfigureAwait(false);
            return ImageValidator.Validate(bytes, ImageLimits.Default) is ImageValidation.Accepted accepted && accepted.Asset == record ? bytes : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static async Task WriteAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
