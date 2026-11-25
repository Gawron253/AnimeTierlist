using System.Collections.Immutable;
using System.Globalization;
using System.Text.RegularExpressions;

namespace AnimeTierlist.Core;

public readonly record struct SnapshotId(Guid Value)
{
    public static SnapshotId New() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString("D");
}

/// <summary>Titles observed on one source card. <see cref="Displayed"/> is the title LiveChart showed in the captured context.</summary>
public sealed record SourceTitles
{
    public SourceTitles(string displayed, string? english, string? romanized, string? native)
    {
        if (string.IsNullOrWhiteSpace(displayed))
        {
            throw new ArgumentException("A source card must display a title.", nameof(displayed));
        }

        Displayed = displayed;
        English = Blank(english);
        Romanized = Blank(romanized);
        Native = Blank(native);
    }

    public string Displayed { get; }

    public string? English { get; }

    public string? Romanized { get; }

    public string? Native { get; }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}

/// <summary>
/// Optional descriptive metadata captured from a LiveChart card or anime page. Purely informational:
/// never affects item identity, popularity order, or collection and board invariants.
/// </summary>
public sealed record SourceDetails
{
    public static SourceDetails Empty { get; } = new();

    public SourceDetails(
        string? rating = null,
        string? premiere = null,
        string? metadata = null,
        string? synopsis = null,
        ImmutableArray<string> studios = default,
        ImmutableArray<string> tags = default,
        Uri? aniListUrl = null,
        Uri? myAnimeListUrl = null)
    {
        Rating = Blank(rating);
        Premiere = Blank(premiere);
        Metadata = Blank(metadata);
        Synopsis = Blank(synopsis);
        Studios = studios.IsDefault ? [] : studios.Select(s => s.Trim()).Where(s => s.Length > 0).Distinct(StringComparer.Ordinal).ToImmutableArray();
        Tags = tags.IsDefault ? [] : tags.Select(t => t.Trim()).Where(t => t.Length > 0).Distinct(StringComparer.Ordinal).ToImmutableArray();
        AniListUrl = ValidateSiteUrl(aniListUrl, ExternalUrlPolicy.IsAllowedAniListUrl, nameof(aniListUrl), "AniList");
        MyAnimeListUrl = ValidateSiteUrl(myAnimeListUrl, ExternalUrlPolicy.IsAllowedMyAnimeListUrl, nameof(myAnimeListUrl), "MyAnimeList");
    }

    public string? Rating { get; }

    public string? Premiere { get; }

    public string? Metadata { get; }

    public string? Synopsis { get; }

    public ImmutableArray<string> Studios { get; }

    public ImmutableArray<string> Tags { get; }

    public Uri? AniListUrl { get; }

    public Uri? MyAnimeListUrl { get; }

    public bool IsEmpty =>
        Rating is null && Premiere is null && Metadata is null && Synopsis is null
        && Studios.IsEmpty && Tags.IsEmpty && AniListUrl is null && MyAnimeListUrl is null;

    /// <summary>Combines this record with <paramref name="fallback"/>, filling only blank or empty fields.</summary>
    public SourceDetails MergeWith(SourceDetails? fallback)
    {
        if (fallback is null || fallback.IsEmpty)
        {
            return this;
        }

        if (IsEmpty)
        {
            return fallback;
        }

        return new SourceDetails(
            Rating ?? fallback.Rating,
            Premiere ?? fallback.Premiere,
            Metadata ?? fallback.Metadata,
            Synopsis ?? fallback.Synopsis,
            Studios.IsEmpty ? fallback.Studios : Studios,
            Tags.IsEmpty ? fallback.Tags : Tags,
            AniListUrl ?? fallback.AniListUrl,
            MyAnimeListUrl ?? fallback.MyAnimeListUrl);
    }

    public bool Equals(SourceDetails? other) =>
        other is not null
        && Rating == other.Rating
        && Premiere == other.Premiere
        && Metadata == other.Metadata
        && Synopsis == other.Synopsis
        && Studios.SequenceEqual(other.Studios)
        && Tags.SequenceEqual(other.Tags)
        && AniListUrl == other.AniListUrl
        && MyAnimeListUrl == other.MyAnimeListUrl;

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Rating);
        hash.Add(Premiere);
        hash.Add(Metadata);
        hash.Add(Synopsis);
        foreach (var studio in Studios)
        {
            hash.Add(studio);
        }

        foreach (var tag in Tags)
        {
            hash.Add(tag);
        }

        hash.Add(AniListUrl);
        hash.Add(MyAnimeListUrl);
        return hash.ToHashCode();
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static Uri? ValidateSiteUrl(Uri? url, Func<Uri, bool> predicate, string paramName, string siteName)
    {
        if (url is null)
        {
            return null;
        }

        if (!predicate(url))
        {
            throw new ArgumentException($"The {siteName} URL must be a valid HTTPS {siteName} anime link.", paramName);
        }

        return url;
    }
}

/// <summary>Allowlist rules for external HTTPS links surfaced in the desktop UI and persisted in projects.</summary>
public static class ExternalUrlPolicy
{
    private static readonly ImmutableHashSet<string> VideoHosts = ImmutableHashSet.Create(
        StringComparer.OrdinalIgnoreCase,
        "www.youtube.com",
        "youtube.com",
        "m.youtube.com",
        "youtu.be",
        "x.com",
        "twitter.com",
        "www.livechart.me");

    /// <summary>True for HTTPS URLs with default port and no embedded credentials.</summary>
    public static bool IsSafeHttpsUrl(Uri? url) =>
        url is not null && url.IsAbsoluteUri && url.Scheme == Uri.UriSchemeHttps && url.IsDefaultPort && string.IsNullOrEmpty(url.UserInfo);

    /// <summary>True for public LiveChart pages on <c>www.livechart.me</c>.</summary>
    public static bool IsAllowedLiveChartUrl(Uri? url) =>
        IsSafeHttpsUrl(url) && string.Equals(url!.IdnHost, "www.livechart.me", StringComparison.OrdinalIgnoreCase);

    /// <summary>True for public AniList anime pages.</summary>
    public static bool IsAllowedAniListUrl(Uri? url) =>
        IsSafeHttpsUrl(url)
        && (string.Equals(url!.IdnHost, "anilist.co", StringComparison.OrdinalIgnoreCase)
            || string.Equals(url.IdnHost, "www.anilist.co", StringComparison.OrdinalIgnoreCase))
        && url.AbsolutePath.StartsWith("/anime/", StringComparison.OrdinalIgnoreCase);

    /// <summary>True for public MyAnimeList anime pages.</summary>
    public static bool IsAllowedMyAnimeListUrl(Uri? url) =>
        IsSafeHttpsUrl(url)
        && (string.Equals(url!.IdnHost, "myanimelist.net", StringComparison.OrdinalIgnoreCase)
            || string.Equals(url.IdnHost, "www.myanimelist.net", StringComparison.OrdinalIgnoreCase))
        && url.AbsolutePath.StartsWith("/anime/", StringComparison.OrdinalIgnoreCase);

    /// <summary>True for supported external video links extracted from LiveChart video listings.</summary>
    public static bool IsAllowedVideoUrl(Uri? url) =>
        IsSafeHttpsUrl(url) && VideoHosts.Contains(url!.IdnHost);

    /// <summary>True for any external URL the desktop shell is permitted to open in the user's default browser.</summary>
    public static bool IsAllowedBrowserLaunchUrl(Uri? url) =>
        IsAllowedLiveChartUrl(url) || IsAllowedAniListUrl(url) || IsAllowedMyAnimeListUrl(url) || IsAllowedVideoUrl(url);
}

/// <summary>Broad video category used to prioritize openings and endings above promotional clips.</summary>
public enum AnimeVideoKind
{
    Opening = 0,
    Ending = 1,
    Other = 2,
}

/// <summary>Classifies and orders LiveChart video entries so openings and endings appear first in natural order.</summary>
public static partial class AnimeVideoClassifier
{
    [GeneratedRegex(@"^(?:OP(?:\s*(?<n>\d+))?(?:\b|$)|Opening\b)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OpeningPrefix();

    [GeneratedRegex(@"^(?:ED(?:\s*(?<n>\d+))?(?:\b|$)|Ending\b)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EndingPrefix();

    [GeneratedRegex(@"\b(?:Opening|OP)\s+(?:Theme|Song|Creditless)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OpeningPhrase();

    [GeneratedRegex(@"\b(?:Ending|ED)\s+(?:Theme|Song|Creditless)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EndingPhrase();

    /// <summary>Categorizes a LiveChart video title as Opening, Ending, or Other.</summary>
    public static AnimeVideoKind Classify(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return AnimeVideoKind.Other;
        }

        var trimmed = title.Trim();
        if (OpeningPrefix().IsMatch(trimmed) || OpeningPhrase().IsMatch(trimmed))
        {
            return AnimeVideoKind.Opening;
        }

        if (EndingPrefix().IsMatch(trimmed) || EndingPhrase().IsMatch(trimmed))
        {
            return AnimeVideoKind.Ending;
        }

        return AnimeVideoKind.Other;
    }

    /// <summary>True when <paramref name="kind"/> is an opening or ending theme.</summary>
    public static bool IsThemeSong(AnimeVideoKind kind) => kind is AnimeVideoKind.Opening or AnimeVideoKind.Ending;

    /// <summary>
    /// Orders theme songs (openings first, then endings) in natural sequence (OP, OP 2, ..., then music videos),
    /// while leaving other videos in their captured source order.
    /// </summary>
    public static ImmutableArray<AnimeVideo> OrderThemeSongs(IEnumerable<AnimeVideo> videos)
    {
        ArgumentNullException.ThrowIfNull(videos);
        return videos
            .Select((video, index) => (Video: video, Index: index))
            .Where(x => IsThemeSong(x.Video.Kind))
            .OrderBy(x => x.Video.Kind == AnimeVideoKind.Opening ? 0 : 1)
            .ThenBy(x => IsShortThemeLabel(x.Video.Title) ? 0 : 1)
            .ThenBy(x => ThemeSequenceNumber(x.Video.Title))
            .ThenBy(x => x.Video.Title.Length)
            .ThenBy(x => x.Index)
            .Select(x => x.Video)
            .ToImmutableArray();
    }

    [GeneratedRegex(@"^(?:OP|ED)(?:\s|\d|\(|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ShortThemePrefix();

    private static bool IsShortThemeLabel(string title) => ShortThemePrefix().IsMatch(title);

    private static int ThemeSequenceNumber(string title)
    {
        var match = OpeningPrefix().Match(title);
        if (!match.Success)
        {
            match = EndingPrefix().Match(title);
        }

        if (match.Success && match.Groups["n"].Success && int.TryParse(match.Groups["n"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
        {
            return number;
        }

        return 1;
    }
}

/// <summary>One external video link scraped on demand from LiveChart's anime videos page.</summary>
public sealed record AnimeVideo
{
    public AnimeVideo(
        string title,
        Uri url,
        AnimeVideoKind kind,
        string? duration = null,
        string? audioLanguage = null,
        string? subtitles = null,
        bool hasSpoilers = false)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new ArgumentException("A video must have a title.", nameof(title));
        }

        if (!ExternalUrlPolicy.IsAllowedVideoUrl(url))
        {
            throw new ArgumentException("The video URL must be a valid HTTPS link on an allowed video host.", nameof(url));
        }

        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        Title = title.Trim();
        Url = url;
        Kind = kind;
        Duration = Blank(duration);
        AudioLanguage = Blank(audioLanguage);
        Subtitles = Blank(subtitles);
        HasSpoilers = hasSpoilers;
    }

    public string Title { get; }

    public Uri Url { get; }

    public AnimeVideoKind Kind { get; }

    public string? Duration { get; }

    public string? AudioLanguage { get; }

    public string? Subtitles { get; }

    public bool HasSpoilers { get; }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>
/// On-demand LiveChart metadata and video links cached inside the project database per anime item.
/// Never alters collection order, curation, or pinned board presentation.
/// </summary>
public sealed record AnimeExtraDetails
{
    public AnimeExtraDetails(
        LiveChartId item,
        DateTimeOffset retrievedAtUtc,
        SourceDetails details,
        ImmutableArray<AnimeVideo> videos,
        bool videosFetched = true,
        bool profileFetched = true)
    {
        ArgumentNullException.ThrowIfNull(details);
        var normalizedVideos = videos.IsDefault ? [] : videos;
        if (!videosFetched && !profileFetched)
        {
            throw new ArgumentException("Item extras must have fetched videos, profile details, or both.", nameof(videosFetched));
        }

        if (!videosFetched && !normalizedVideos.IsEmpty)
        {
            throw new ArgumentException("Item extras cannot store videos when videos_fetched is false.", nameof(videos));
        }

        if (!profileFetched && !details.IsEmpty)
        {
            throw new ArgumentException("Item extras cannot store profile details when profile_fetched is false.", nameof(details));
        }

        Item = item;
        RetrievedAtUtc = retrievedAtUtc;
        Details = details;
        Videos = normalizedVideos;
        VideosFetched = videosFetched;
        ProfileFetched = profileFetched;
    }

    public LiveChartId Item { get; }

    public DateTimeOffset RetrievedAtUtc { get; }

    public SourceDetails Details { get; }

    public ImmutableArray<AnimeVideo> Videos { get; }

    public bool VideosFetched { get; }

    public bool ProfileFetched { get; }

    public bool Equals(AnimeExtraDetails? other) =>
        other is not null
        && Item == other.Item
        && RetrievedAtUtc == other.RetrievedAtUtc
        && Details == other.Details
        && VideosFetched == other.VideosFetched
        && ProfileFetched == other.ProfileFetched
        && Videos.SequenceEqual(other.Videos);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Item);
        hash.Add(RetrievedAtUtc);
        hash.Add(Details);
        hash.Add(VideosFetched);
        hash.Add(ProfileFetched);
        foreach (var video in Videos)
        {
            hash.Add(video);
        }

        return hash.ToHashCode();
    }
}

/// <summary>A source-published cover variant URL, ordered from most to least preferred.</summary>
public sealed record ImageCandidate(string Variant, Uri Url);

/// <summary>Points at one occurrence inside a snapshot: its season capture and zero-based source ordinal.</summary>
public readonly record struct OccurrenceRef(SnapshotId Snapshot, SeasonKey Season, int Ordinal);

/// <summary>One card as observed in the source, at its zero-based popularity ordinal.</summary>
public sealed record SourceOccurrence
{
    public SourceOccurrence(int ordinal, LiveChartId item, SourceTitles titles, ImmutableArray<ImageCandidate> imageCandidates, SourceDetails? details = null)
    {
        ArgumentNullException.ThrowIfNull(titles);
        Ordinal = ordinal;
        Item = item;
        Titles = titles;
        ImageCandidates = imageCandidates.IsDefault ? [] : imageCandidates;
        Details = details ?? SourceDetails.Empty;
    }

    public int Ordinal { get; init; }

    public LiveChartId Item { get; init; }

    public SourceTitles Titles { get; init; }

    public ImmutableArray<ImageCandidate> ImageCandidates { get; init; }

    public SourceDetails Details { get; init; }

    public bool Equals(SourceOccurrence? other) =>
        other is not null
        && Ordinal == other.Ordinal
        && Item == other.Item
        && Titles == other.Titles
        && ImageCandidates.SequenceEqual(other.ImageCandidates)
        && Details == other.Details;

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Ordinal);
        hash.Add(Item);
        hash.Add(Titles);
        foreach (var candidate in ImageCandidates)
        {
            hash.Add(candidate);
        }

        hash.Add(Details);
        return hash.ToHashCode();
    }
}

/// <summary>One complete, validated season page in the captured order.</summary>
public sealed record SeasonCapture
{
    public SeasonCapture(SeasonKey season, CatalogueCategory category, Uri requestUrl, DateTimeOffset retrievedAtUtc, string parserVersion, ImmutableArray<SourceOccurrence> occurrences)
    {
        ArgumentNullException.ThrowIfNull(requestUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(parserVersion);
        if (occurrences.IsDefault)
        {
            throw new ArgumentException("Occurrences must be initialized.", nameof(occurrences));
        }

        for (var i = 0; i < occurrences.Length; i++)
        {
            if (occurrences[i].Ordinal != i)
            {
                throw new ArgumentException($"Occurrence ordinals must be contiguous from zero; position {i} has ordinal {occurrences[i].Ordinal}.", nameof(occurrences));
            }
        }

        Season = season;
        Category = category;
        RequestUrl = requestUrl;
        RetrievedAtUtc = retrievedAtUtc;
        ParserVersion = parserVersion;
        Occurrences = occurrences;
    }

    public SeasonKey Season { get; }

    public CatalogueCategory Category { get; }

    public Uri RequestUrl { get; }

    public DateTimeOffset RetrievedAtUtc { get; }

    public string ParserVersion { get; }

    /// <summary>Every card in source order, including repeated IDs.</summary>
    public ImmutableArray<SourceOccurrence> Occurrences { get; }
}

/// <summary>An item's place in a snapshot's collection sequence, bound to its first occurrence.</summary>
public readonly record struct SequencedItem(LiveChartId Item, OccurrenceRef FirstOccurrence);

/// <summary>
/// A complete acquisition: every required season for its context, each in captured order.
/// Only a fully validated acquisition can become a snapshot; partial staging never does.
/// </summary>
public sealed class SourceSnapshot
{
    private readonly Dictionary<SeasonKey, SeasonCapture> _bySeason;
    private readonly Dictionary<LiveChartId, OccurrenceRef> _firstOccurrences;

    public SourceSnapshot(SnapshotId id, AcquisitionContext context, IEnumerable<SeasonCapture> captures)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(captures);

        var ordered = captures.OrderBy(c => c.Season).ToImmutableArray();
        var required = context.RequiredSeasons;
        if (ordered.Length != required.Count || !ordered.Select(c => c.Season).SequenceEqual(required))
        {
            throw new ArgumentException($"A snapshot for {Describe(context)} needs exactly the seasons {string.Join(", ", required)}.", nameof(captures));
        }

        if (ordered.Any(c => c.Category != context.Category))
        {
            throw new ArgumentException("Every season capture must use the snapshot's category.", nameof(captures));
        }

        Id = id;
        Context = context;
        Captures = ordered;
        _bySeason = ordered.ToDictionary(c => c.Season);
        Sequence = BuildSequence();
        _firstOccurrences = Sequence.ToDictionary(s => s.Item, s => s.FirstOccurrence);
    }

    public SnapshotId Id { get; }

    public AcquisitionContext Context { get; }

    /// <summary>Season captures in calendar order, winter first.</summary>
    public ImmutableArray<SeasonCapture> Captures { get; }

    /// <summary>
    /// The collection sequence: unique items in calendar-season traversal, each at its first occurrence.
    /// All occurrences remain available through <see cref="Captures"/>.
    /// </summary>
    public ImmutableArray<SequencedItem> Sequence { get; }

    public int OccurrenceCount => Captures.Sum(c => c.Occurrences.Length);

    public SourceOccurrence Occurrence(OccurrenceRef reference)
    {
        if (reference.Snapshot != Id)
        {
            throw new ArgumentException("The occurrence belongs to a different snapshot.", nameof(reference));
        }

        if (!_bySeason.TryGetValue(reference.Season, out var capture) || reference.Ordinal < 0 || reference.Ordinal >= capture.Occurrences.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(reference), "The occurrence is not part of this snapshot.");
        }

        return capture.Occurrences[reference.Ordinal];
    }

    public bool TryGetFirstOccurrence(LiveChartId item, out OccurrenceRef occurrence) =>
        _firstOccurrences.TryGetValue(item, out occurrence);

    public bool Contains(LiveChartId item) => _firstOccurrences.ContainsKey(item);

    private ImmutableArray<SequencedItem> BuildSequence()
    {
        var seen = new HashSet<LiveChartId>();
        var builder = ImmutableArray.CreateBuilder<SequencedItem>();
        foreach (var capture in Captures)
        {
            foreach (var occurrence in capture.Occurrences)
            {
                if (seen.Add(occurrence.Item))
                {
                    builder.Add(new SequencedItem(occurrence.Item, new OccurrenceRef(Id, capture.Season, occurrence.Ordinal)));
                }
            }
        }

        return builder.ToImmutable();
    }

    private static string Describe(AcquisitionContext context) =>
        context.Season is { } s ? $"{s} {context.Year} {context.Category}" : $"{context.Year} {context.Category}";
}
