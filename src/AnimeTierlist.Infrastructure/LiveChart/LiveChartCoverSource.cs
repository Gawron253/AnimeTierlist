using AnimeTierlist.Application.Acquisition;
using AnimeTierlist.Application.Covers;
using AnimeTierlist.Core;
using AnimeTierlist.Infrastructure.Assets;
using AnimeTierlist.Infrastructure.Http;

namespace AnimeTierlist.Infrastructure.LiveChart;

/// <summary>
/// Downloads LiveChart posters. Candidates come only from a validated source occurrence, largest verified variant
/// first; each response is validated by decoding before it is installed. Size, decode, or format failures and
/// exhausted transient failures move on to the next smaller variant. Undocumented variant URLs are never synthesized.
/// </summary>
public sealed class LiveChartCoverSource : ICoverSource
{
    /// <summary>The CDN negotiates the format from Accept; every listed type is in the release allowlist.</summary>
    public const string AcceptHeader = "image/webp,image/png,image/jpeg";

    private readonly SourceHttpClient _http;
    private readonly AssetStore _assets;
    private readonly ImageLimits _limits;

    public LiveChartCoverSource(SourceHttpClient http, AssetStore assets, ImageLimits limits)
    {
        _http = http;
        _assets = assets;
        _limits = limits;
    }

    public string RequestContext => "Accept: " + AcceptHeader;

    public bool IsAllowedCandidate(Uri url) => _http.IsAllowed(url, RequestKind.Image);

    public async Task<CoverFetchResult> FetchAsync(SourceOccurrence occurrence, RetryBudget budget, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(occurrence);
        if (occurrence.ImageCandidates.IsEmpty)
        {
            return new CoverFetchResult.Missing("LiveChart lists no cover image for this entry.");
        }

        var reasons = new List<string>();
        var transient = false;
        var requested = 0;
        var unreachable = 0;
        foreach (var candidate in occurrence.ImageCandidates)
        {
            if (!IsAllowedCandidate(candidate.Url))
            {
                reasons.Add($"{candidate.Variant}: not an allowed image URL");
                continue;
            }

            var result = await _http.GetAsync(candidate.Url, RequestKind.Image, AcceptHeader, _limits.MaxCompressedBytes, budget, cancellationToken).ConfigureAwait(false);
            requested++;
            switch (result)
            {
                case HttpFetchResult.Success success:
                    switch (ImageValidator.Validate(success.Body, _limits))
                    {
                        case ImageValidation.Accepted accepted:
                            var installed = await _assets.InstallAsync(success.Body, accepted.Asset, cancellationToken).ConfigureAwait(false);
                            return new CoverFetchResult.Acquired(installed, candidate.Url, success.FinalUrl);
                        case ImageValidation.Rejected rejected:
                            reasons.Add($"{candidate.Variant}: {rejected.Reason} ({rejected.Detail})");
                            break;
                    }

                    break;
                case HttpFetchResult.Failure { Kind: HttpFetchFailure.Paused } paused:
                    return new CoverFetchResult.Paused(paused.Detail);
                case HttpFetchResult.Failure failure:
                    transient |= failure.Kind is HttpFetchFailure.Transport or HttpFetchFailure.Unreachable;
                    unreachable += failure.Kind == HttpFetchFailure.Unreachable ? 1 : 0;
                    reasons.Add($"{candidate.Variant}: {failure.Detail}");
                    break;
            }
        }

        var summary = string.Join("; ", reasons);
        return transient ? new CoverFetchResult.Failed(summary, Unreachable: requested > 0 && unreachable == requested) : new CoverFetchResult.Missing(summary);
    }
}
