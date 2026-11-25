using System.Collections.Immutable;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using AnimeTierlist.Application.Acquisition;
using AnimeTierlist.Application.Catalogue;
using AnimeTierlist.Application.Covers;
using AnimeTierlist.Core;
using AnimeTierlist.Infrastructure.Assets;
using AnimeTierlist.Infrastructure.Http;
using AnimeTierlist.Infrastructure.LiveChart;
using AnimeTierlist.Infrastructure.Tests.Http;
using SkiaSharp;

namespace AnimeTierlist.Infrastructure.Tests.LiveChart;

/// <summary>Season pages and covers through the HTTP adapters with frozen fixtures and real image bytes; no live requests.</summary>
public sealed class LiveChartSourceTests : IDisposable
{
    private static readonly SeasonKey Summer2025 = new(2025, Season.Summer);
    private static readonly Uri Large = new("https://u.livechart.me/anime/1/poster_image/x.webp/large.jpg");
    private static readonly Uri Small = new("https://u.livechart.me/anime/1/poster_image/x.webp/small.jpg");

    private readonly string _project = Path.Combine(Path.GetTempPath(), "animetierlist-tests", Guid.NewGuid().ToString("N"));
    private readonly ScriptedHandler _handler = new();
    private readonly ManualTime _time = new();
    private readonly SourceHttpClient _http;

    public LiveChartSourceTests()
    {
        _http = new SourceHttpClient(_handler, SourceHttpPolicy.Default, "AnimeTierlist/0.1.0", _time, _time.DelayAsync, jitter: () => 0);
    }

    public void Dispose()
    {
        _http.Dispose();
        if (Directory.Exists(_project))
        {
            Directory.Delete(_project, recursive: true);
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Fetched_season_page_matches_the_rendered_oracle_exactly()
    {
        _handler.Add("https://www.livechart.me/summer-2025/tv", HttpStatusCode.OK, Fixture("summer-2025-tv.html"), "text/html");
        var source = new LiveChartCatalogueSource(_http, _time);

        var valid = Assert.IsType<SeasonPageResult.Valid>(await source.FetchSeasonAsync(Summer2025, CatalogueCategory.Tv, RetryBudget.CreateDefault(), Ct));

        Assert.Equal(Oracle("summer-2025-tv"), valid.Capture.Occurrences.Select(o => o.Item.Value));
        Assert.Equal(new Uri("https://www.livechart.me/summer-2025/tv"), valid.Capture.RequestUrl);
        Assert.Equal(_time.GetUtcNow(), valid.Capture.RetrievedAtUtc);
    }

    [Fact]
    public async Task Unknown_season_is_a_wrong_page_failure_not_an_empty_season()
    {
        var source = new LiveChartCatalogueSource(_http, _time);

        var invalid = Assert.IsType<SeasonPageResult.Invalid>(await source.FetchSeasonAsync(new SeasonKey(2030, Season.Winter), CatalogueCategory.Ovas, RetryBudget.CreateDefault(), Ct));

        Assert.Equal(SeasonPageFailure.WrongPage, invalid.Failure);
    }

    [Fact]
    public async Task Exhausted_server_errors_are_transport_failures()
    {
        _handler.Add("https://www.livechart.me/summer-2025/tv", HttpStatusCode.BadGateway);
        var source = new LiveChartCatalogueSource(_http, _time);

        var invalid = Assert.IsType<SeasonPageResult.Invalid>(await source.FetchSeasonAsync(Summer2025, CatalogueCategory.Tv, RetryBudget.CreateDefault(), Ct));

        Assert.Equal(SeasonPageFailure.Transport, invalid.Failure);
        Assert.Equal(3, _handler.CountFor("https://www.livechart.me/summer-2025/tv"));
    }

    [Fact]
    public async Task A_page_that_cannot_be_connected_to_is_unreachable_not_a_transport_failure()
    {
        _handler.Add("https://www.livechart.me/summer-2025/tv", Unreachable);
        var source = new LiveChartCatalogueSource(_http, _time);

        var invalid = Assert.IsType<SeasonPageResult.Invalid>(await source.FetchSeasonAsync(Summer2025, CatalogueCategory.Tv, RetryBudget.CreateDefault(), Ct));

        Assert.Equal(SeasonPageFailure.Unreachable, invalid.Failure);
        Assert.Equal(3, _handler.CountFor("https://www.livechart.me/summer-2025/tv"));
    }

    [Fact]
    public async Task Cover_is_marked_unreachable_only_when_no_variant_request_reached_the_host()
    {
        var source = new LiveChartCoverSource(_http, new AssetStore(_project), ImageLimits.Default);
        _handler.Add(Large.AbsoluteUri, Unreachable);
        _handler.Add(Small.AbsoluteUri, Unreachable);

        var none = Assert.IsType<CoverFetchResult.Failed>(await source.FetchAsync(Occurrence(Large, Small), RetryBudget.CreateDefault(), Ct));

        Assert.True(none.Unreachable);

        var other = new Uri("https://u.livechart.me/anime/2/poster_image/x.webp/small.jpg");
        _handler.Add(other.AbsoluteUri, HttpStatusCode.ServiceUnavailable);

        var mixed = Assert.IsType<CoverFetchResult.Failed>(await source.FetchAsync(Occurrence(Large, other), RetryBudget.CreateDefault(), Ct));

        Assert.False(mixed.Unreachable);
    }

    [Fact]
    public async Task Largest_valid_variant_is_installed_with_its_request_details()
    {
        var bytes = Encode(SKEncodedImageFormat.Webp, 350, 500);
        _handler.Add(Large.AbsoluteUri, HttpStatusCode.OK, bytes, "image/webp");
        var source = new LiveChartCoverSource(_http, new AssetStore(_project), ImageLimits.Default);

        var acquired = Assert.IsType<CoverFetchResult.Acquired>(await source.FetchAsync(Occurrence(Large, Small), RetryBudget.CreateDefault(), Ct));

        Assert.Equal((AssetFormat.WebP, 350, 500), (acquired.Asset.Format, acquired.Asset.Width, acquired.Asset.Height));
        Assert.Equal(Large, acquired.RequestedUrl);
        Assert.True(File.Exists(Path.Combine(_project, acquired.Asset.RelativePath)));
        Assert.Equal("Accept: image/webp,image/png,image/jpeg", source.RequestContext);
        Assert.Equal("image/webp, image/png, image/jpeg", _handler.Requests.Single().Headers.Accept.ToString());
        Assert.Equal(0, _handler.CountFor(Small.AbsoluteUri));
    }

    [Theory]
    [InlineData("oversized")]
    [InlineData("malformed")]
    [InlineData("unsupported")]
    [InlineData("missing")]
    public async Task Invalid_large_variant_falls_back_to_the_next_verified_variant(string problem)
    {
        var smallBytes = Encode(SKEncodedImageFormat.Jpeg, 175, 250);
        var largeBytes = problem switch
        {
            "oversized" => Encode(SKEncodedImageFormat.Png, 350, 500),
            "malformed" => Encode(SKEncodedImageFormat.Jpeg, 350, 500)[..200],
            "unsupported" => OnePixelBmp(),
            _ => null,
        };
        if (largeBytes is not null)
        {
            _handler.Add(Large.AbsoluteUri, HttpStatusCode.OK, largeBytes);
        }

        _handler.Add(Small.AbsoluteUri, HttpStatusCode.OK, smallBytes);

        // The pixel limit admits the small variant only, so a valid large PNG is "oversized" for this release.
        var limits = problem == "oversized" ? ImageLimits.Default with { MaxDecodedPixels = 175 * 250 } : ImageLimits.Default;
        var source = new LiveChartCoverSource(_http, new AssetStore(_project), limits);

        var acquired = Assert.IsType<CoverFetchResult.Acquired>(await source.FetchAsync(Occurrence(Large, Small), RetryBudget.CreateDefault(), Ct));

        Assert.Equal(Small, acquired.RequestedUrl);
        Assert.Equal((175, 250), (acquired.Asset.Width, acquired.Asset.Height));
    }

    [Fact]
    public async Task Transient_failure_retries_before_falling_back()
    {
        _handler.Add(Large.AbsoluteUri, HttpStatusCode.ServiceUnavailable);
        _handler.Add(Small.AbsoluteUri, HttpStatusCode.OK, Encode(SKEncodedImageFormat.Jpeg, 175, 250));
        var source = new LiveChartCoverSource(_http, new AssetStore(_project), ImageLimits.Default);

        var acquired = Assert.IsType<CoverFetchResult.Acquired>(await source.FetchAsync(Occurrence(Large, Small), RetryBudget.CreateDefault(), Ct));

        Assert.Equal(3, _handler.CountFor(Large.AbsoluteUri));
        Assert.Equal(Small, acquired.RequestedUrl);
    }

    [Fact]
    public async Task No_candidate_or_all_invalid_is_missing_and_transport_exhaustion_is_failed()
    {
        var source = new LiveChartCoverSource(_http, new AssetStore(_project), ImageLimits.Default);
        _handler.Add(Large.AbsoluteUri, HttpStatusCode.OK, "<html/>"u8.ToArray());
        _handler.Add(Small.AbsoluteUri, HttpStatusCode.InternalServerError);

        Assert.IsType<CoverFetchResult.Missing>(await source.FetchAsync(Occurrence(), RetryBudget.CreateDefault(), Ct));
        Assert.IsType<CoverFetchResult.Missing>(await source.FetchAsync(Occurrence(Large), RetryBudget.CreateDefault(), Ct));
        Assert.IsType<CoverFetchResult.Failed>(await source.FetchAsync(Occurrence(Large, Small), RetryBudget.CreateDefault(), Ct));
        Assert.False(Directory.Exists(Path.Combine(_project, "assets")) && Directory.EnumerateFiles(Path.Combine(_project, "assets")).Any());
    }

    [Fact]
    public async Task Stored_candidate_outside_the_image_allowlist_is_never_requested()
    {
        var source = new LiveChartCoverSource(_http, new AssetStore(_project), ImageLimits.Default);

        var missing = Assert.IsType<CoverFetchResult.Missing>(await source.FetchAsync(Occurrence(new Uri("https://evil.example/x.jpg")), RetryBudget.CreateDefault(), Ct));

        Assert.Contains("not an allowed image URL", missing.Reason, StringComparison.Ordinal);
        Assert.Empty(_handler.Requests);
    }

    private static HttpResponseMessage Unreachable() =>
        throw new HttpRequestException(HttpRequestError.ConnectionError, "No connection could be made.");

    private static SourceOccurrence Occurrence(params Uri[] candidates) =>
        new(0, new LiveChartId(1), new SourceTitles("Title", null, "Romaji", null),
            candidates.Select(c => new ImageCandidate(c.Segments[^1].Split('.')[0], c)).ToImmutableArray());

    private static byte[] Fixture(string file) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "livechart", file));

    private static List<int> Oracle(string name)
    {
        using var json = JsonDocument.Parse(Fixture(name + ".expected.json"));
        return json.RootElement.GetProperty("oracle").GetProperty("orderedIds").EnumerateArray().Select(e => e.GetInt32()).ToList();
    }

    private static byte[] Encode(SKEncodedImageFormat format, int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(new SKColor(0x40, (byte)width, (byte)height));
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, 90);
        return data.ToArray();
    }

    private static byte[] OnePixelBmp() =>
    [
        0x42, 0x4D, 0x3A, 0, 0, 0, 0, 0, 0, 0, 0x36, 0, 0, 0,
        0x28, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0x18, 0, 0, 0, 0, 0, 4, 0, 0, 0, 0x13, 0x0B, 0, 0, 0x13, 0x0B, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0xFF, 0x00, 0x00, 0x00,
    ];
}
