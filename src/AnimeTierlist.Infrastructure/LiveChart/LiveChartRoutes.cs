using System.Globalization;
using AnimeTierlist.Core;

namespace AnimeTierlist.Infrastructure.LiveChart;

/// <summary>LiveChart season chart routes.</summary>
public static class LiveChartRoutes
{
    public static readonly Uri Origin = new("https://www.livechart.me/");

    public static string Slug(CatalogueCategory category) => category switch
    {
        CatalogueCategory.Tv => "tv",
        CatalogueCategory.Movies => "movies",
        CatalogueCategory.Ovas => "ovas",
        CatalogueCategory.All => "all",
        _ => throw new ArgumentOutOfRangeException(nameof(category)),
    };

    /// <summary>Path such as <c>/summer-2025/tv</c>.</summary>
    public static string Path(SeasonKey season, CatalogueCategory category) =>
        string.Create(CultureInfo.InvariantCulture, $"/{season.Season.ToString().ToLowerInvariant()}-{season.Year}/{Slug(category)}");

    public static Uri SeasonPage(SeasonKey season, CatalogueCategory category) => new(Origin, Path(season, category));

    /// <summary>Public anime detail page URL on LiveChart, such as <c>https://www.livechart.me/anime/12979</c>.</summary>
    public static Uri AnimePage(LiveChartId item) => new(Origin, string.Create(CultureInfo.InvariantCulture, $"/anime/{item.Value}"));

    /// <summary>Public anime videos page URL on LiveChart, such as <c>https://www.livechart.me/anime/12979/videos</c>.</summary>
    public static Uri AnimeVideosPage(LiveChartId item) => new(Origin, string.Create(CultureInfo.InvariantCulture, $"/anime/{item.Value}/videos"));
}
