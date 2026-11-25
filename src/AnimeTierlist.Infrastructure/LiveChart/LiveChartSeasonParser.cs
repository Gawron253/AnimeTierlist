using System.Collections.Immutable;
using System.Globalization;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using AnimeTierlist.Application.Catalogue;
using AnimeTierlist.Core;

namespace AnimeTierlist.Infrastructure.LiveChart;

/// <summary>
/// Reads a LiveChart season chart in anonymous popularity order.
/// Cards are returned in document order, which matched the rendered order on every verified route.
/// Visible ratings and other card metadata never influence order.
/// </summary>
public static class LiveChartSeasonParser
{
    public const string ParserVersion = "livechart-season-html/2026-11-17";

    /// <summary>Allowed image host for LiveChart cover posters.</summary>
    public static readonly ImmutableHashSet<string> ImageHosts = ["u.livechart.me"];

    private const string PopularitySortLabel = "Sort: Popularity";

    public static SeasonPageResult Parse(string html, SeasonKey season, CatalogueCategory category, Uri requestUrl, DateTimeOffset retrievedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(html);
        ArgumentNullException.ThrowIfNull(requestUrl);

        using var document = new HtmlParser().ParseDocument(html);

        var expectedPath = LiveChartRoutes.Path(season, category);
        var canonical = document.QuerySelector("link[rel=canonical]")?.GetAttribute("href");
        if (canonical is null || !CanonicalMatches(canonical, expectedPath))
        {
            return Invalid(SeasonPageFailure.WrongPage, $"Expected canonical route {expectedPath} but found '{canonical ?? "none"}'.");
        }

        var chart = document.QuerySelectorAll("[data-controller]").FirstOrDefault(e => HasToken(e.GetAttribute("data-controller"), "seasonal-chart"));
        if (chart is null)
        {
            return Invalid(SeasonPageFailure.ChangedLayout, "The seasonal chart container was not found.");
        }

        var sortLabels = chart.QuerySelectorAll(".lcm3-button__label").Select(e => e.TextContent.Trim()).Where(t => t.StartsWith("Sort:", StringComparison.Ordinal)).Distinct().ToList();
        if (sortLabels.Count != 1 || sortLabels[0] != PopularitySortLabel)
        {
            return Invalid(SeasonPageFailure.WrongSortContext, $"Expected the '{PopularitySortLabel}' control but found [{string.Join(", ", sortLabels)}].");
        }

        var occurrences = ImmutableArray.CreateBuilder<SourceOccurrence>();
        var cardNumber = 0;
        foreach (var article in chart.QuerySelectorAll("article"))
        {
            cardNumber++;
            if (IsKnownNonAnimeCard(article))
            {
                continue;
            }

            if (!IsAnimeCard(article))
            {
                return Invalid(SeasonPageFailure.ChangedLayout, $"Card {cardNumber} has unrecognized markup (class '{article.ClassName}').");
            }

            var card = ReadCard(article, occurrences.Count);
            if (card.Error is { } error)
            {
                return Invalid(SeasonPageFailure.InvalidCard, $"Card {cardNumber}: {error}");
            }

            occurrences.Add(card.Occurrence!);
        }

        return new SeasonPageResult.Valid(new SeasonCapture(season, category, requestUrl, retrievedAtUtc, ParserVersion, occurrences.ToImmutable()));
    }

    private static (SourceOccurrence? Occurrence, string? Error) ReadCard(IElement article, int ordinal)
    {
        var idText = article.GetAttribute("data-anime-id");
        if (!int.TryParse(idText, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0)
        {
            return (null, $"missing or invalid data-anime-id '{idText}'; unresolved card text: {Excerpt(article.TextContent)}");
        }

        var itemId = article.GetAttribute("data-anime-item-id");
        if (itemId is not null && itemId != idText)
        {
            return (null, $"data-anime-id {idText} disagrees with data-anime-item-id {itemId}");
        }

        var displayed = article.QuerySelector("[data-anime-item-target=preferredTitle]")?.TextContent.Trim();
        if (string.IsNullOrEmpty(displayed))
        {
            return (null, $"anime {id} has no displayed title");
        }

        var titles = new SourceTitles(
            displayed,
            article.GetAttribute("data-anime-item-english-title"),
            article.GetAttribute("data-anime-item-romanized-native-title"),
            article.GetAttribute("data-anime-item-native-title"));

        var poster = article.QuerySelector("img[data-anime-item-target=poster]");
        var details = ReadDetails(article);
        return (new SourceOccurrence(ordinal, new LiveChartId(id), titles, ImageCandidates(poster), details), null);
    }

    /// <summary>
    /// Extracts optional descriptive metadata from a LiveChart card (rating, premiere, studios, tags, metadata, synopsis).
    /// Purely informational: missing or unrecognized optional elements never fail card parsing or alter popularity order.
    /// </summary>
    private static SourceDetails ReadDetails(IElement article)
    {
        var ratingElement = article.QuerySelector(".lc-anime-poster-overlay-host__rating");
        var rating = CollapseWhitespace(ratingElement?.GetAttribute("title"))
            ?? CollapseWhitespace(ratingElement?.TextContent);

        var premiere = CollapseWhitespace(article.QuerySelector(".lc-anime-details-card__premiere")?.TextContent);
        if (premiere is null
            && long.TryParse(article.GetAttribute("data-anime-item-premiere-value"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var unixSeconds)
            && unixSeconds > 0
            && unixSeconds <= 4_102_444_800)
        {
            premiere = DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        var studios = ReadListItems(article, "ol.lc-anime-details-card__studios li");
        var tags = ReadListItems(article, "ol.lc-anime-details-card__tags li");

        var metadataElement = article.QuerySelector(".lc-anime-details-card__metadata");
        string? metadata = null;
        if (metadataElement is not null)
        {
            var parts = metadataElement.Children
                .Select(c => CollapseWhitespace(c.TextContent))
                .Where(t => t is not null)
                .Select(t => t!)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            metadata = parts.Count > 0 ? string.Join(" · ", parts) : CollapseWhitespace(metadataElement.TextContent);
        }

        var synopsisContainer = article.QuerySelector(".lc-anime-details-card__synopsis");
        var paragraphNodes = synopsisContainer is not null
            ? synopsisContainer.QuerySelectorAll("p")
            : article.Children.Where(c => c.TagName.Equals("P", StringComparison.OrdinalIgnoreCase));
        var paragraphs = paragraphNodes
            .Select(p => CollapseWhitespace(p.TextContent))
            .Where(t => t is not null)
            .Select(t => t!)
            .ToList();
        var synopsis = paragraphs.Count > 0
            ? string.Join("\n\n", paragraphs)
            : CollapseWhitespace(synopsisContainer?.TextContent);

        var (aniListUrl, myAnimeListUrl) = ReadExternalSiteLinks(article);
        return new SourceDetails(rating, premiere, metadata, synopsis, studios, tags, aniListUrl, myAnimeListUrl);
    }

    /// <summary>Extracts verified AniList and MyAnimeList links from a card's footer icons when present.</summary>
    private static (Uri? AniList, Uri? MyAnimeList) ReadExternalSiteLinks(IElement root)
    {
        Uri? aniList = null;
        Uri? myAnimeList = null;
        foreach (var anchor in root.QuerySelectorAll("a[href]"))
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

    private static ImmutableArray<string> ReadListItems(IElement article, string selector) =>
        article.QuerySelectorAll(selector)
            .Select(el => CollapseWhitespace(el.TextContent))
            .Where(t => t is not null)
            .Select(t => t!)
            .Distinct(StringComparer.Ordinal)
            .ToImmutableArray();

    private static string? CollapseWhitespace(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var collapsed = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Length == 0 ? null : collapsed;
    }

    /// <summary>Source-published variants from srcset, highest density first, falling back to src. URLs are never synthesized.</summary>
    private static ImmutableArray<ImageCandidate> ImageCandidates(IElement? poster)
    {
        if (poster is null)
        {
            return [];
        }

        var entries = new List<(double Density, string Url)>();
        foreach (var part in (poster.GetAttribute("srcset") ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pieces = part.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var density = 1.0;
            if (pieces.Length > 1 && pieces[1].EndsWith('x') && double.TryParse(pieces[1][..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            {
                density = parsed;
            }

            entries.Add((density, pieces[0]));
        }

        if (poster.GetAttribute("src") is { Length: > 0 } src)
        {
            entries.Add((0, src));
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var builder = ImmutableArray.CreateBuilder<ImageCandidate>();
        foreach (var (_, text) in entries.OrderByDescending(e => e.Density))
        {
            if (Uri.TryCreate(text, UriKind.Absolute, out var url) && url.Scheme == Uri.UriSchemeHttps && ImageHosts.Contains(url.Host) && seen.Add(url.AbsoluteUri))
            {
                builder.Add(new ImageCandidate(VariantName(url), url));
            }
        }

        return builder.ToImmutable();
    }

    private static string VariantName(Uri url)
    {
        var last = url.Segments[^1];
        var dot = last.IndexOf('.', StringComparison.Ordinal);
        return dot > 0 ? last[..dot] : last;
    }

    private static bool IsAnimeCard(IElement article) =>
        article.HasAttribute("data-anime-id")
        || article.ClassList.Contains("lc-anime-details-card")
        || HasToken(article.GetAttribute("data-controller"), "anime-item");

    private static bool IsKnownNonAnimeCard(IElement article) =>
        article.ClassList.Contains("lc-tutorial-card") && !IsAnimeCard(article);

    private static bool CanonicalMatches(string canonical, string expectedPath)
    {
        if (canonical == expectedPath)
        {
            return true;
        }

        return Uri.TryCreate(canonical, UriKind.Absolute, out var absolute)
            && absolute.Host == LiveChartRoutes.Origin.Host
            && absolute.AbsolutePath == expectedPath;
    }

    private static bool HasToken(string? list, string token) =>
        list is not null && list.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(token, StringComparer.Ordinal);

    private static string Excerpt(string text)
    {
        var collapsed = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Length <= 120 ? collapsed : collapsed[..120] + "…";
    }

    private static SeasonPageResult.Invalid Invalid(SeasonPageFailure failure, string detail) => new(failure, detail);
}
