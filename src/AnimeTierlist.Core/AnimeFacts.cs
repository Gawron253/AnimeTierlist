using System.Collections.Immutable;
using System.Globalization;
using System.Text.RegularExpressions;

namespace AnimeTierlist.Core;

/// <summary>
/// Filterable facts about one show, derived from LiveChart's descriptive text and cached videos. Purely
/// informational: facts never change item identity, collection order, curation, or board placements.
/// </summary>
public sealed partial record AnimeFacts
{
    private static readonly ImmutableHashSet<string> Formats = ImmutableHashSet.Create(
        StringComparer.OrdinalIgnoreCase, "TV", "TV Short", "TV Special", "Movie", "OVA", "ONA", "Special", "Music");

    private static readonly string[] SourceKeywords =
        ["manga", "novel", "game", "original", "book", "comic", "webtoon", "manhwa", "manhua", "mixed media", "radio"];

    private AnimeFacts(string? format, string? sourceMaterial, double? rating, ImmutableArray<string> studios, ImmutableArray<string> genres, bool? hasOpening)
    {
        Format = format;
        SourceMaterial = sourceMaterial;
        Rating = rating;
        Studios = studios;
        Genres = genres;
        HasOpening = hasOpening;
    }

    public static AnimeFacts Unknown { get; } = new(null, null, null, [], [], null);

    /// <summary>The release format LiveChart lists (TV, Movie, OVA, ...), or null when unknown.</summary>
    public string? Format { get; }

    /// <summary>The source material LiveChart lists (Manga, Light Novel, Original, ...), or null when unknown.</summary>
    public string? SourceMaterial { get; }

    /// <summary>LiveChart's user rating on a 0 to 10 scale, or null when the show has no rating yet.</summary>
    public double? Rating { get; }

    public ImmutableArray<string> Studios { get; }

    /// <summary>LiveChart's tags for the show (genres and themes).</summary>
    public ImmutableArray<string> Genres { get; }

    /// <summary>True or false once the show's videos were fetched; null while they have not been checked.</summary>
    public bool? HasOpening { get; }

    /// <summary>
    /// Combines the season card's details with any cached profile details and videos. Profile values win where both
    /// sources have one, and the card fills the gaps.
    /// </summary>
    public static AnimeFacts From(SourceDetails? card, AnimeExtraDetails? extras)
    {
        var profile = extras is { ProfileFetched: true } ? extras.Details : null;
        var sources = new[] { profile, card }.Where(d => d is not null && !d.IsEmpty).Select(d => d!).ToList();
        if (sources.Count == 0 && extras is not { VideosFetched: true })
        {
            return Unknown;
        }

        string? format = null;
        string? source = null;
        double? rating = null;
        foreach (var details in sources)
        {
            var (f, s) = ParseMetadata(details.Metadata);
            format ??= f;
            source ??= s;
            rating ??= ParseRating(details.Rating);
        }

        var merged = sources.Count == 0 ? SourceDetails.Empty : sources.Count == 1 ? sources[0] : sources[0].MergeWith(sources[1]);
        bool? hasOpening = extras is { VideosFetched: true } ? extras.Videos.Any(v => v.Kind == AnimeVideoKind.Opening) : null;
        return new AnimeFacts(format, source, rating, merged.Studios, merged.Tags, hasOpening);
    }

    /// <summary>
    /// Reads LiveChart's leading score from a rating text such as <c>8.12 out of 10 based on 120 user ratings</c> or
    /// <c>8.12 (Good)</c>. Returns null for missing or out-of-range scores.
    /// </summary>
    public static double? ParseRating(string? rating)
    {
        if (string.IsNullOrWhiteSpace(rating))
        {
            return null;
        }

        var match = LeadingScore().Match(rating);
        if (!match.Success
            || !double.TryParse(match.Groups["score"].Value.Replace(',', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var score)
            || score is < 0 or > 10)
        {
            return null;
        }

        return score;
    }

    /// <summary>
    /// Finds the format and source material in a metadata summary whose parts are joined with <c> · </c>, such as
    /// <c>TV · Light Novel · 12 eps × 24m · Finished</c>. Parts that are neither are ignored.
    /// </summary>
    public static (string? Format, string? SourceMaterial) ParseMetadata(string? metadata)
    {
        if (string.IsNullOrWhiteSpace(metadata))
        {
            return (null, null);
        }

        var parts = metadata.Split('·', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        string? format = null;
        string? source = null;
        var formatIndex = -1;
        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            if (format is null && Formats.Contains(part))
            {
                format = part;
                formatIndex = i;
                continue;
            }

            if (source is not null)
            {
                continue;
            }

            if (SourceKeywords.Any(k => part.Contains(k, StringComparison.OrdinalIgnoreCase))
                || (i == formatIndex + 1 && formatIndex >= 0 && part is "Music" or "Other"))
            {
                source = part;
            }
        }

        return (format, source);
    }

    [GeneratedRegex(@"^\s*(?<score>\d{1,2}(?:[.,]\d+)?)", RegexOptions.CultureInvariant)]
    private static partial Regex LeadingScore();
}
