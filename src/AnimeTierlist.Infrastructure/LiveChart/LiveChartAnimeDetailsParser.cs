using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using AnimeTierlist.Core;

namespace AnimeTierlist.Infrastructure.LiveChart;

/// <summary>Result of parsing a single LiveChart <c>/anime/{id}/videos</c> page, including any validated next-page link.</summary>
public sealed record VideosPageParseResult(
    ImmutableArray<AnimeVideo> Videos,
    Uri? NextPageUrl,
    string? Error);

/// <summary>
/// Parses LiveChart's public per-anime profile page (<c>/anime/{id}</c>) and videos page
/// (<c>/anime/{id}/videos</c>) for on-demand inspection in the board entry details window.
/// </summary>
public static partial class LiveChartAnimeDetailsParser
{
    [GeneratedRegex(@"^(?:there are no videos|no videos(?: found| yet| have been added)?)(?: for this anime)?(?: yet)?\.?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RecognizedEmptyVideosRegex();
    /// <summary>Parses descriptive details and AniList/MyAnimeList links from a LiveChart anime profile page.</summary>
    public static (SourceDetails? Details, string? Error) ParseAnimePage(string html, LiveChartId expectedId)
    {
        ArgumentNullException.ThrowIfNull(html);
        using var document = new HtmlParser().ParseDocument(html);

        var expectedPath = $"/anime/{expectedId.Value.ToString(CultureInfo.InvariantCulture)}";
        var canonical = document.QuerySelector("link[rel=canonical]")?.GetAttribute("href");
        if (canonical is not null && !MatchesAnimePageCanonical(canonical, expectedPath))
        {
            return (null, $"Expected canonical route {expectedPath} but found '{canonical}'.");
        }

        var root = document.QuerySelector("[data-anime-details-id]");
        if (root is null)
        {
            return (null, $"The response did not contain a recognized LiveChart anime details container for {expectedPath}.");
        }

        var actualId = root.GetAttribute("data-anime-details-id");
        if (actualId != expectedId.Value.ToString(CultureInfo.InvariantCulture))
        {
            return (null, $"Expected LiveChart anime #{expectedId.Value} but found #{actualId ?? "unknown"}.");
        }

        var rating = ReadRating(root);
        var premiere = ReadLabeledValue(root, "Premiere")
            ?? CollapseWhitespace(root.QuerySelector(".lc-anime-info-header-layout__overline time")?.TextContent);
        var metadata = BuildMetadataSummary(root);
        var studios = root.QuerySelectorAll("a[href*='/studios/']")
            .Select(a => CollapseWhitespace(a.TextContent))
            .Where(t => t is not null)
            .Select(t => t!)
            .Distinct(StringComparer.Ordinal)
            .ToImmutableArray();

        var sourceAnchor = FindLabeledBlock(root, "Source")?.QuerySelector("a[href*='/tags/']");
        var tags = root.QuerySelectorAll("a[href*='/tags/']")
            .Where(a => !ReferenceEquals(a, sourceAnchor))
            .Select(a => CollapseWhitespace(a.TextContent))
            .Where(t => t is not null)
            .Select(t => t!)
            .Distinct(StringComparer.Ordinal)
            .ToImmutableArray();

        var synopsis = ReadSynopsis(root);
        var (aniList, myAnimeList) = ReadExternalSiteLinks(root);
        var details = new SourceDetails(rating, premiere, metadata, synopsis, studios, tags, aniList, myAnimeList);

        if (details.IsEmpty && !HasRecognizedAnimeProfileStructure(root))
        {
            return (null, $"The LiveChart anime page for {expectedPath} did not contain recognized anime details structure.");
        }

        return (details, null);
    }

    /// <summary>Parses verified external video links (OPs, EDs, PVs, teasers, spots) from a LiveChart videos page.</summary>
    public static (ImmutableArray<AnimeVideo> Videos, string? Error) ParseVideosPage(string html, LiveChartId expectedId)
    {
        var result = ParseVideosPageWithContinuation(html, expectedId);
        return (result.Videos, result.Error);
    }

    /// <summary>
    /// Parses verified external video links and any validated same-anime pagination continuation URL from a
    /// LiveChart <c>/anime/{id}/videos</c> page.
    /// </summary>
    public static VideosPageParseResult ParseVideosPageWithContinuation(string html, LiveChartId expectedId, Uri? pageUrl = null)
    {
        ArgumentNullException.ThrowIfNull(html);
        using var document = new HtmlParser().ParseDocument(html);

        var currentPageUrl = pageUrl ?? LiveChartRoutes.AnimeVideosPage(expectedId);
        var expectedVideosPath = $"/anime/{expectedId.Value.ToString(CultureInfo.InvariantCulture)}/videos";
        if (!TryReadPageNumber(currentPageUrl, out var requestedPage))
        {
            return new([], null, $"The requested LiveChart videos URL '{currentPageUrl}' has an invalid page number.");
        }

        var canonical = document.QuerySelector("link[rel=canonical]")?.GetAttribute("href");
        if (canonical is not null && !MatchesVideosCanonical(canonical, expectedVideosPath, requestedPage))
        {
            return new([], null, $"Expected canonical route {expectedVideosPath} (page {requestedPage ?? 1}) but found '{canonical}'.");
        }

        var hasVideosRouteAnchor = document.QuerySelector($"a[href='{expectedVideosPath}'], a[href^='{expectedVideosPath}?']") is not null;
        var hasRouteIdentity = MatchesVideosCanonical(canonical, expectedVideosPath, requestedPage) || hasVideosRouteAnchor;
        var cards = document.QuerySelectorAll(".lc-video");

        if (!hasRouteIdentity)
        {
            return new([], null, $"The response did not match LiveChart videos page {expectedVideosPath}.");
        }

        var (nextPageUrl, nextPageError) = ReadNextVideosPageUrl(document, expectedVideosPath, currentPageUrl);
        if (nextPageError is not null)
        {
            return new([], null, nextPageError);
        }

        if (cards.Length == 0)
        {
            if (nextPageUrl is not null)
            {
                return new([], null, $"The response for {expectedVideosPath} advertised a next page without any recognized video cards.");
            }

            if (!HasRecognizedEmptyVideosState(document, hasVideosRouteAnchor))
            {
                return new([], null, $"The response for {expectedVideosPath} did not contain video cards or a recognized empty videos state.");
            }

            return new([], null, null);
        }

        var seenUrls = new HashSet<string>(StringComparer.Ordinal);
        var videos = ImmutableArray.CreateBuilder<AnimeVideo>();
        foreach (var card in cards)
        {
            var title = ReadVideoTitle(card);
            var delegateUri = ReadVideoDelegateUri(card);
            if (title is null || delegateUri is null)
            {
                return new([], null, $"A video card on {expectedVideosPath} did not contain recognized LiveChart video markup.");
            }

            if (!ExternalUrlPolicy.IsAllowedVideoUrl(delegateUri) || !seenUrls.Add(NormalizeVideoDedupeKey(delegateUri)))
            {
                continue;
            }

            var duration = CollapseWhitespace(card.QuerySelector("[data-video-target='durationBadge']")?.TextContent)
                ?? FormatIsoDuration(card.GetAttribute("data-video-duration"));
            var audio = CleanTracklist(card.QuerySelector("[data-video-target='auralTracklist']")?.TextContent);
            var subtitles = CleanTracklist(card.QuerySelector("[data-video-target='textualTracklist']")?.TextContent);
            var hasSpoilers = card.QuerySelector(".lc-media-spoiler__mask") is not null;
            var kind = AnimeVideoClassifier.Classify(title);

            videos.Add(new AnimeVideo(title, delegateUri, kind, duration, audio, subtitles, hasSpoilers));
        }

        return new(videos.ToImmutable(), nextPageUrl, null);
    }

    /// <summary>Normalizes scheme/host to lowercase while preserving case-sensitive path and query tokens (such as YouTube video IDs).</summary>
    internal static string NormalizeVideoDedupeKey(Uri url) =>
        url.GetLeftPart(UriPartial.Authority).ToLowerInvariant() + url.PathAndQuery;

    /// <summary>
    /// Reads any explicit <c>page</c> query parameter from a LiveChart videos URL.
    /// Returns <see langword="true"/> with <paramref name="pageNumber"/> equal to <see langword="null"/> when no
    /// <c>page</c> parameter is present, <see langword="true"/> with a positive integer when valid and consistent,
    /// and <see langword="false"/> when any <c>page</c> parameter is invalid or conflicting.
    /// </summary>
    internal static bool TryReadPageNumber(Uri url, out int? pageNumber)
    {
        pageNumber = null;
        var query = url.Query;
        if (string.IsNullOrEmpty(query))
        {
            return true;
        }

        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            var key = eq >= 0 ? Uri.UnescapeDataString(part[..eq]) : Uri.UnescapeDataString(part);
            if (!key.Equals("page", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (eq < 0)
            {
                pageNumber = null;
                return false;
            }

            var rawValue = Uri.UnescapeDataString(part[(eq + 1)..]);
            if (rawValue.Length == 0
                || !rawValue.All(char.IsAsciiDigit)
                || !int.TryParse(rawValue, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
                || parsed <= 0)
            {
                pageNumber = null;
                return false;
            }

            if (pageNumber is { } existing && existing != parsed)
            {
                pageNumber = null;
                return false;
            }

            pageNumber = parsed;
        }

        return true;
    }

    private static string? ReadRating(IElement scope)
    {
        var bar = scope.QuerySelector("[data-anime-details-target='ratingBar']");
        if (bar is null)
        {
            return null;
        }

        var score = CollapseWhitespace(bar.QuerySelector("[data-rating-display-target='ratingA']")?.TextContent);
        if (score is null || score.StartsWith('–') || score.StartsWith('-'))
        {
            return null;
        }

        var context = CollapseWhitespace(bar.QuerySelector("[data-rating-display-target='contextA']")?.TextContent);
        return context is null ? score : $"{score} ({context})";
    }

    private static string? BuildMetadataSummary(IElement scope)
    {
        var parts = new List<string>();
        var format = ReadLabeledValue(scope, "Format");
        if (format is not null)
        {
            parts.Add(format);
        }

        var source = ReadLabeledValue(scope, "Source");
        if (source is not null)
        {
            parts.Add(source);
        }

        var episodes = ReadEpisodeCount(scope);
        var runtime = ReadLabeledValue(scope, "Run time");
        if (episodes is not null && runtime is not null)
        {
            parts.Add($"{episodes} × {runtime}");
        }
        else if (episodes is not null)
        {
            parts.Add(episodes);
        }
        else if (runtime is not null)
        {
            parts.Add(runtime);
        }

        var status = ReadLabeledValue(scope, "Status");
        if (status is not null)
        {
            parts.Add(status);
        }

        return parts.Count > 0 ? string.Join(" · ", parts) : null;
    }

    private static string? ReadEpisodeCount(IElement scope)
    {
        var progression = scope.QuerySelector("[data-viewing-progression-total-count-value]");
        if (progression is not null
            && int.TryParse(progression.GetAttribute("data-viewing-progression-total-count-value"), NumberStyles.None, CultureInfo.InvariantCulture, out var count)
            && count > 0)
        {
            return count == 1 ? "1 ep" : $"{count.ToString(CultureInfo.InvariantCulture)} eps";
        }

        var raw = ReadLabeledValue(scope, "Episodes");
        if (raw is not null && int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed > 0)
        {
            return parsed == 1 ? "1 ep" : $"{parsed.ToString(CultureInfo.InvariantCulture)} eps";
        }

        return null;
    }

    private static string? ReadLabeledValue(IElement scope, string label)
    {
        var block = FindLabeledBlock(scope, label);
        if (block is null || block.Children.Length < 2)
        {
            return null;
        }

        return CollapseWhitespace(block.Children[1].TextContent);
    }

    private static IElement? FindLabeledBlock(IElement scope, string label)
    {
        foreach (var div in scope.QuerySelectorAll("div"))
        {
            if (div.Children.Length < 2 || div.Children.Length > 3)
            {
                continue;
            }

            if (string.Equals(CollapseWhitespace(div.Children[0].TextContent), label, StringComparison.OrdinalIgnoreCase))
            {
                return div;
            }
        }

        return null;
    }

    private static string? ReadSynopsis(IElement scope)
    {
        var container = scope.QuerySelector("[data-anime-details-target='synopsis']");
        if (container is null)
        {
            return null;
        }

        var paragraphs = container.QuerySelectorAll("p")
            .Select(p => CollapseWhitespace(p.TextContent))
            .Where(t => t is not null)
            .Select(t => t!)
            .ToList();
        return paragraphs.Count > 0
            ? string.Join("\n\n", paragraphs)
            : CollapseWhitespace(container.QuerySelector(".lc-markdown-html")?.TextContent);
    }

    private static (Uri? AniList, Uri? MyAnimeList) ReadExternalSiteLinks(IElement scope)
    {
        Uri? aniList = null;
        Uri? myAnimeList = null;
        foreach (var anchor in scope.QuerySelectorAll("a[href]"))
        {
            var href = anchor.GetAttribute("href");
            if (!Uri.TryCreate(href, UriKind.Absolute, out var uri))
            {
                continue;
            }

            if (aniList is null && ExternalUrlPolicy.IsAllowedAniListUrl(uri))
            {
                aniList = uri;
            }
            else if (myAnimeList is null && ExternalUrlPolicy.IsAllowedMyAnimeListUrl(uri))
            {
                myAnimeList = uri;
            }

            if (aniList is not null && myAnimeList is not null)
            {
                break;
            }
        }

        return (aniList, myAnimeList);
    }

    /// <summary>Reads the primary LiveChart video-player delegate URL from a <c>.lc-video</c> card.</summary>
    private static Uri? ReadVideoDelegateUri(IElement card)
    {
        var primary = card.QuerySelector("a.lc-interactive-surface__interaction-delegate[href]")
            ?? card.QuerySelector("a[data-action*='video#openPlayer'][href]");
        if (primary is null)
        {
            return null;
        }

        return Uri.TryCreate(primary.GetAttribute("href"), UriKind.Absolute, out var direct)
            && (direct.Scheme == Uri.UriSchemeHttp || direct.Scheme == Uri.UriSchemeHttps)
            ? direct
            : null;
    }

    private static string? ReadVideoTitle(IElement card)
    {
        var title = CollapseWhitespace(card.QuerySelector(".lcm3-text--title-md")?.TextContent);
        if (title is not null)
        {
            return title;
        }

        var alt = CollapseWhitespace(card.QuerySelector("img[alt]")?.GetAttribute("alt"));
        if (alt is null)
        {
            return null;
        }

        const string prefix = "Play video:";
        return alt.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? CollapseWhitespace(alt[prefix.Length..])
            : alt;
    }

    private static string? FormatIsoDuration(string? iso)
    {
        if (string.IsNullOrWhiteSpace(iso))
        {
            return null;
        }

        try
        {
            var span = XmlConvert.ToTimeSpan(iso.Trim());
            if (span <= TimeSpan.Zero)
            {
                return null;
            }

            return span.TotalHours >= 1
                ? string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalHours}:{span.Minutes:D2}:{span.Seconds:D2}")
                : string.Create(CultureInfo.InvariantCulture, $"{span.Minutes}:{span.Seconds:D2}");
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static string? CleanTracklist(string? raw)
    {
        var text = CollapseWhitespace(raw);
        return text is null || string.Equals(text, "None", StringComparison.OrdinalIgnoreCase) ? null : text;
    }

    private static bool HasRecognizedAnimeProfileStructure(IElement root) =>
        root.QuerySelector("[data-anime-details-target='ratingBar'], [data-anime-details-target='synopsis'], .lc-anime-info-header-layout__overline") is not null
        || FindLabeledBlock(root, "Status") is not null
        || FindLabeledBlock(root, "Premiere") is not null
        || FindLabeledBlock(root, "Format") is not null
        || FindLabeledBlock(root, "Source") is not null
        || FindLabeledBlock(root, "Episodes") is not null
        || FindLabeledBlock(root, "Run time") is not null;

    private static (Uri? NextPageUrl, string? Error) ReadNextVideosPageUrl(IDocument document, string expectedVideosPath, Uri currentPageUrl)
    {
        IElement? activeNext = null;
        foreach (var selector in new[] { "link[rel~=next]", "a[rel~=next]", "a[aria-label='Next'], a[aria-label='Next page']" })
        {
            foreach (var candidate in document.QuerySelectorAll(selector))
            {
                if (IsDisabledPaginationElement(candidate))
                {
                    continue;
                }

                activeNext = candidate;
                break;
            }

            if (activeNext is not null)
            {
                break;
            }
        }

        if (activeNext is null)
        {
            return (null, null);
        }

        var nextHref = activeNext.GetAttribute("href");
        if (string.IsNullOrWhiteSpace(nextHref))
        {
            return (null, $"LiveChart advertised a next videos page on {expectedVideosPath} without a valid URL.");
        }

        if (!Uri.TryCreate(currentPageUrl, nextHref.Trim(), out var resolved)
            || resolved.Scheme != Uri.UriSchemeHttps
            || !string.Equals(resolved.Host, LiveChartRoutes.Origin.Host, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(resolved.AbsolutePath, expectedVideosPath, StringComparison.OrdinalIgnoreCase)
            || !TryReadPageNumber(resolved, out _))
        {
            return (null, $"Unexpected next-page URL '{nextHref}' on {expectedVideosPath}.");
        }

        return (resolved, null);
    }

    private static bool IsDisabledPaginationElement(IElement element) =>
        string.Equals(element.GetAttribute("aria-disabled"), "true", StringComparison.OrdinalIgnoreCase)
        || element.ClassList.Contains("disabled");

    private static bool HasRecognizedEmptyVideosState(IDocument document, bool hasVideosRouteAnchor)
    {
        var hasVideosPageStructure = hasVideosRouteAnchor
            || document.QuerySelector("[data-controller*='video'], .lc-videos, .lc-video-list") is not null
            || document.QuerySelectorAll("h1, h2, h3").Any(h => string.Equals(CollapseWhitespace(h.TextContent), "Videos", StringComparison.OrdinalIgnoreCase));
        if (!hasVideosPageStructure)
        {
            return false;
        }

        var emptyContainer = document.QuerySelector(".lc-empty-state, [data-empty-state]");
        if (emptyContainer is not null)
        {
            var containerText = CollapseWhitespace(ExtractVisibleText(emptyContainer));
            if (containerText is not null && RecognizedEmptyVideosRegex().IsMatch(containerText))
            {
                return true;
            }

            return false;
        }

        var body = document.Body;
        if (body is null)
        {
            return false;
        }

        foreach (var element in body.QuerySelectorAll("p, div, span, section"))
        {
            if (element.Children.Length > 0)
            {
                continue;
            }

            var text = CollapseWhitespace(ExtractVisibleText(element));
            if (text is not null && RecognizedEmptyVideosRegex().IsMatch(text))
            {
                return true;
            }
        }

        return false;
    }

    private static string ExtractVisibleText(INode node)
    {
        var builder = new StringBuilder();
        AppendVisibleText(node, builder);
        return builder.ToString();
    }

    private static void AppendVisibleText(INode node, StringBuilder builder)
    {
        if (node is IElement element
            && (element.TagName.Equals("SCRIPT", StringComparison.OrdinalIgnoreCase)
                || element.TagName.Equals("STYLE", StringComparison.OrdinalIgnoreCase)
                || element.TagName.Equals("NOSCRIPT", StringComparison.OrdinalIgnoreCase)
                || element.TagName.Equals("TEMPLATE", StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        if (node.NodeType == NodeType.Text)
        {
            builder.Append(node.TextContent);
            builder.Append(' ');
            return;
        }

        foreach (var child in node.ChildNodes)
        {
            AppendVisibleText(child, builder);
        }
    }

    private static bool MatchesAnimePageCanonical(string canonical, string expectedPath)
    {
        if (string.IsNullOrWhiteSpace(canonical))
        {
            return false;
        }

        if (string.Equals(canonical, expectedPath, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return Uri.TryCreate(canonical, UriKind.Absolute, out var uri)
            && string.Equals(uri.Host, LiveChartRoutes.Origin.Host, StringComparison.OrdinalIgnoreCase)
            && string.Equals(uri.AbsolutePath, expectedPath, StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesVideosCanonical(string? canonical, string expectedPath, int? requestedPage)
    {
        if (string.IsNullOrWhiteSpace(canonical))
        {
            return false;
        }

        if (!Uri.TryCreate(LiveChartRoutes.Origin, canonical.Trim(), out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !string.Equals(uri.Host, LiveChartRoutes.Origin.Host, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(uri.AbsolutePath, expectedPath, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!TryReadPageNumber(uri, out var canonicalPage))
        {
            return false;
        }

        if (requestedPage is { } expected && expected > 1)
        {
            return canonicalPage is null || canonicalPage == expected;
        }

        return canonicalPage is null || canonicalPage == 1;
    }

    private static string? CollapseWhitespace(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var collapsed = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Length == 0 ? null : collapsed;
    }
}
