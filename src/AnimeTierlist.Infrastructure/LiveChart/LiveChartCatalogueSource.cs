using System.Text;
using AnimeTierlist.Application.Acquisition;
using AnimeTierlist.Application.Catalogue;
using AnimeTierlist.Core;
using AnimeTierlist.Infrastructure.Http;

namespace AnimeTierlist.Infrastructure.LiveChart;

/// <summary>Fetches anonymous LiveChart season pages through the paced source client and validates them with the parser.</summary>
public sealed class LiveChartCatalogueSource : ICatalogueSource
{
    private const string Accept = "text/html,application/xhtml+xml";

    private readonly SourceHttpClient _http;
    private readonly TimeProvider _time;

    public LiveChartCatalogueSource(SourceHttpClient http, TimeProvider time)
    {
        _http = http;
        _time = time;
    }

    public async Task<SeasonPageResult> FetchSeasonAsync(SeasonKey season, CatalogueCategory category, RetryBudget budget, CancellationToken cancellationToken)
    {
        var url = LiveChartRoutes.SeasonPage(season, category);
        var result = await _http.GetAsync(url, RequestKind.Catalogue, Accept, _http.Policy.MaxCatalogueBytes, budget, cancellationToken).ConfigureAwait(false);
        return result switch
        {
            HttpFetchResult.Success success => LiveChartSeasonParser.Parse(Encoding.UTF8.GetString(success.Body), season, category, url, _time.GetUtcNow()),
            HttpFetchResult.Failure { Kind: HttpFetchFailure.NotFound } failure =>
                new SeasonPageResult.Invalid(SeasonPageFailure.WrongPage, $"LiveChart has no {LiveChartRoutes.Path(season, category)} page ({failure.Detail})."),
            HttpFetchResult.Failure { Kind: HttpFetchFailure.TooLarge } failure => new SeasonPageResult.Invalid(SeasonPageFailure.TooLarge, failure.Detail),
            HttpFetchResult.Failure { Kind: HttpFetchFailure.Paused } failure => new SeasonPageResult.Invalid(SeasonPageFailure.Paused, failure.Detail),
            HttpFetchResult.Failure { Kind: HttpFetchFailure.Unreachable } failure => new SeasonPageResult.Invalid(SeasonPageFailure.Unreachable, failure.Detail),
            HttpFetchResult.Failure failure => new SeasonPageResult.Invalid(SeasonPageFailure.Transport, failure.Detail),
            _ => throw new InvalidOperationException("Unknown fetch result."),
        };
    }
}
