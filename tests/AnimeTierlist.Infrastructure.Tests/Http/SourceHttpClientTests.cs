using System.Net;
using System.Net.Http;
using AnimeTierlist.Application.Acquisition;
using AnimeTierlist.Infrastructure.Http;

namespace AnimeTierlist.Infrastructure.Tests.Http;

/// <summary>Allowlist, redirects, pacing, bounded retries with Retry-After, budget pause, and size limits.</summary>
public sealed class SourceHttpClientTests : IDisposable
{
    private const string Page = "https://www.livechart.me/summer-2025/tv";
    private const string Other = "https://www.livechart.me/fall-2025/tv";
    private const string Image = "https://u.livechart.me/anime/1/poster_image/x.webp/large.jpg";

    private readonly ScriptedHandler _handler = new();
    private readonly ManualTime _time = new();
    private readonly SourceHttpClient _client;

    public SourceHttpClientTests()
    {
        _client = new SourceHttpClient(_handler, SourceHttpPolicy.Default, "AnimeTierlist/0.1.0", _time, _time.DelayAsync, jitter: () => 0);
    }

    public void Dispose() => _client.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("http://www.livechart.me/summer-2025/tv")]
    [InlineData("https://livechart.me/summer-2025/tv")]
    [InlineData("https://www.livechart.me:8443/summer-2025/tv")]
    [InlineData("https://user@www.livechart.me/summer-2025/tv")]
    [InlineData("https://u.livechart.me/anime/1/x.jpg")]
    [InlineData("file:///C:/Windows/win.ini")]
    public async Task Urls_outside_the_catalogue_allowlist_are_refused_without_a_request(string url)
    {
        var result = await Get(new Uri(url));

        Assert.Equal(HttpFetchFailure.Disallowed, Assert.IsType<HttpFetchResult.Failure>(result).Kind);
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task Requests_identify_the_app_send_no_cookies_and_return_the_body()
    {
        _handler.Add(Page, HttpStatusCode.OK, "<html></html>"u8.ToArray(), "text/html");

        var success = Assert.IsType<HttpFetchResult.Success>(await Get(new Uri(Page)));

        Assert.Equal("<html></html>"u8.ToArray(), success.Body);
        var request = Assert.Single(_handler.Requests);
        Assert.Contains("AnimeTierlist/0.1.0", request.Headers.UserAgent.ToString(), StringComparison.Ordinal);
        Assert.False(request.Headers.Contains("Cookie"));
        Assert.Equal("text/html", request.Headers.Accept.ToString());
    }

    [Fact]
    public async Task Catalogue_requests_start_at_least_five_seconds_apart()
    {
        _handler.Add(Page, HttpStatusCode.OK).Add(Other, HttpStatusCode.OK);

        await Get(new Uri(Page));
        _time.Advance(TimeSpan.FromSeconds(2));
        await Get(new Uri(Other));

        Assert.Equal([TimeSpan.FromSeconds(3)], _time.Delays);
    }

    [Fact]
    public async Task Image_requests_never_exceed_two_at_a_time()
    {
        var active = 0;
        var peak = 0;
        _handler.BeforeResponse = async (_, _) =>
        {
            var now = Interlocked.Increment(ref active);
            InterlockedMax(ref peak, now);
            await Task.Delay(20, Ct);
            Interlocked.Decrement(ref active);
        };
        for (var i = 0; i < 6; i++)
        {
            _handler.Add($"https://u.livechart.me/anime/{i}/large.jpg", HttpStatusCode.OK, [1]);
        }

        await Task.WhenAll(Enumerable.Range(0, 6).Select(i =>
            _client.GetAsync(new Uri($"https://u.livechart.me/anime/{i}/large.jpg"), RequestKind.Image, "image/webp", 100, RetryBudget.CreateDefault(), Ct)));

        Assert.Equal(2, peak);
    }

    [Fact]
    public async Task Transient_failures_retry_with_exponential_backoff_up_to_three_attempts()
    {
        _handler.Add(Page, HttpStatusCode.ServiceUnavailable);

        var failure = Assert.IsType<HttpFetchResult.Failure>(await Get(new Uri(Page)));

        Assert.Equal(HttpFetchFailure.Transport, failure.Kind);
        Assert.Equal(3, _handler.CountFor(Page));
        // Backoff 2 s and 4 s; each retry is still a catalogue start, so pacing adds 3 s and 1 s to reach 5 s spacing.
        Assert.Equal([TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(1)], _time.Delays);
    }

    [Fact]
    public async Task Retry_after_is_honoured_and_consumes_the_budget()
    {
        _handler.Add(Image, () => WithRetryAfter(HttpStatusCode.TooManyRequests, TimeSpan.FromSeconds(10)))
            .Add(Image, HttpStatusCode.OK, [7]);
        var budget = new RetryBudget(TimeSpan.FromSeconds(30));

        var success = Assert.IsType<HttpFetchResult.Success>(await _client.GetAsync(new Uri(Image), RequestKind.Image, "image/webp", 100, budget, Ct));

        Assert.Equal([7], success.Body);
        Assert.Equal([TimeSpan.FromSeconds(10)], _time.Delays);
        Assert.Equal(TimeSpan.FromSeconds(20), budget.Remaining);
    }

    [Fact]
    public async Task Retry_beyond_the_budget_pauses_instead_of_retrying_early()
    {
        _handler.Add(Image, () => WithRetryAfter(HttpStatusCode.ServiceUnavailable, TimeSpan.FromMinutes(10)));

        var failure = Assert.IsType<HttpFetchResult.Failure>(await _client.GetAsync(new Uri(Image), RequestKind.Image, "image/webp", 100, RetryBudget.CreateDefault(), Ct));

        Assert.Equal(HttpFetchFailure.Paused, failure.Kind);
        Assert.Equal(1, _handler.CountFor(Image));
        Assert.Empty(_time.Delays);
    }

    [Fact]
    public async Task Retry_after_on_the_last_attempt_delays_the_next_request_to_that_host()
    {
        var other = "https://u.livechart.me/anime/1/poster_image/x.webp/small.jpg";
        _handler.Add(Image, () => WithRetryAfter(HttpStatusCode.ServiceUnavailable, TimeSpan.FromSeconds(20)));
        _handler.Add(other, HttpStatusCode.OK, [1]);
        var budget = new RetryBudget(TimeSpan.FromMinutes(2));

        var failed = Assert.IsType<HttpFetchResult.Failure>(await _client.GetAsync(new Uri(Image), RequestKind.Image, "image/webp", 100, budget, Ct));
        Assert.Equal(HttpFetchFailure.Transport, failed.Kind);
        Assert.Equal(3, _handler.CountFor(Image));
        Assert.Equal([TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(20)], _time.Delays);

        // The fallback variant on the same CDN host waits for the server's last requested delay.
        Assert.IsType<HttpFetchResult.Success>(await _client.GetAsync(new Uri(other), RequestKind.Image, "image/webp", 100, budget, Ct));
        Assert.Equal([TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(20)], _time.Delays);
    }

    [Fact]
    public async Task A_paused_host_stays_paused_on_resume_until_its_deadline()
    {
        var recovered = false;
        _handler.Add(Image, () => recovered ? ScriptedHandler.Response(HttpStatusCode.OK, [1]) : WithRetryAfter(HttpStatusCode.ServiceUnavailable, TimeSpan.FromMinutes(10)));

        Assert.Equal(HttpFetchFailure.Paused, Assert.IsType<HttpFetchResult.Failure>(await _client.GetAsync(new Uri(Image), RequestKind.Image, "image/webp", 100, RetryBudget.CreateDefault(), Ct)).Kind);
        Assert.Equal(HttpFetchFailure.Paused, Assert.IsType<HttpFetchResult.Failure>(await _client.GetAsync(new Uri(Image), RequestKind.Image, "image/webp", 100, RetryBudget.CreateDefault(), Ct)).Kind);
        Assert.Equal(1, _handler.CountFor(Image));

        _time.Advance(TimeSpan.FromMinutes(9));
        recovered = true;
        Assert.IsType<HttpFetchResult.Success>(await _client.GetAsync(new Uri(Image), RequestKind.Image, "image/webp", 100, RetryBudget.CreateDefault(), Ct));
        Assert.Equal([TimeSpan.FromMinutes(1)], _time.Delays);
    }

    [Fact]
    public async Task The_host_deadline_is_rechecked_after_each_wait_before_sending()
    {
        // The first wait ends early, as if another worker had extended the deadline meanwhile.
        var waits = new List<TimeSpan>();
        Task Delay(TimeSpan span, CancellationToken token)
        {
            waits.Add(span);
            _time.Advance(waits.Count == 1 ? span / 2 : span);
            return Task.CompletedTask;
        }

        using var client = new SourceHttpClient(_handler, SourceHttpPolicy.Default, "AnimeTierlist/0.1.0", _time, Delay, jitter: () => 0);
        var sent = new List<DateTimeOffset>();
        _handler.BeforeResponse = (_, _) =>
        {
            sent.Add(_time.GetUtcNow());
            return Task.CompletedTask;
        };
        var first = true;
        _handler.Add(Image, () =>
        {
            var response = first ? WithRetryAfter(HttpStatusCode.ServiceUnavailable, TimeSpan.FromSeconds(10)) : ScriptedHandler.Response(HttpStatusCode.OK, [1]);
            first = false;
            return response;
        });

        Assert.IsType<HttpFetchResult.Success>(await client.GetAsync(new Uri(Image), RequestKind.Image, "image/webp", 100, RetryBudget.CreateDefault(), Ct));

        Assert.Equal([TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5)], waits);
        Assert.Equal(TimeSpan.FromSeconds(10), sent[1] - sent[0]);
    }

    [Fact]
    public async Task A_deadline_extended_by_another_worker_during_a_wait_is_honoured()
    {
        const string first = "https://u.livechart.me/anime/1/a.jpg";
        const string hold = "https://u.livechart.me/anime/1/x.jpg";
        const string waiting = "https://u.livechart.me/anime/1/b.jpg";
        var waits = new List<TimeSpan>();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task Delay(TimeSpan span, CancellationToken token)
        {
            lock (waits)
            {
                waits.Add(span);
            }

            if (waits.Count == 1)
            {
                blocked.SetResult();
                await release.Task.WaitAsync(token);
            }

            _time.Advance(span);
        }

        var respondFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sent = new Dictionary<string, DateTimeOffset>();
        _handler.BeforeResponse = async (request, _) =>
        {
            lock (sent)
            {
                sent[request.RequestUri!.AbsoluteUri] = _time.GetUtcNow();
            }

            if (request.RequestUri!.AbsoluteUri == first)
            {
                await respondFirst.Task;
            }
        };
        _handler.Add(first, () => WithRetryAfter(HttpStatusCode.TooManyRequests, TimeSpan.FromSeconds(60)));
        _handler.Add(hold, () => WithRetryAfter(HttpStatusCode.ServiceUnavailable, TimeSpan.FromSeconds(10)));
        _handler.Add(waiting, HttpStatusCode.OK, [1]);
        var policy = SourceHttpPolicy.Default with { MaxAttempts = 1 };
        using var client = new SourceHttpClient(_handler, policy, "AnimeTierlist/0.1.0", _time, Delay, jitter: () => 0);
        var start = _time.GetUtcNow();
        Task<HttpFetchResult> Get(string url) => client.GetAsync(new Uri(url), RequestKind.Image, "image/webp", 100, RetryBudget.CreateDefault(), Ct);

        var worker1 = Get(first);                 // in flight; its 429 arrives later
        await Get(hold);                          // records a 10-second hold
        var worker2 = Get(waiting);               // waits out the 10-second hold
        await blocked.Task;
        respondFirst.SetResult();                 // worker 1's 429 extends the hold to 60 seconds meanwhile
        await worker1;
        release.SetResult();

        Assert.IsType<HttpFetchResult.Success>(await worker2);
        Assert.Equal([TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(50)], waits);
        Assert.Equal(start + TimeSpan.FromSeconds(60), sent[waiting]);
    }

    [Fact]
    public async Task Catalogue_redirect_hops_are_paced_like_any_other_request_start()
    {
        _handler.Add(Page, () => Redirect("/summer-2025/tv?moved=1")).Add(Page + "?moved=1", HttpStatusCode.OK, [1]);

        Assert.IsType<HttpFetchResult.Success>(await Get(new Uri(Page)));

        Assert.Equal([TimeSpan.FromSeconds(5)], _time.Delays);
    }

    [Fact]
    public async Task Partial_content_is_not_accepted_as_a_complete_response()
    {
        _handler.Add(Page, HttpStatusCode.PartialContent, "<html>looks complete</html>"u8.ToArray(), "text/html");

        var failure = Assert.IsType<HttpFetchResult.Failure>(await Get(new Uri(Page)));

        Assert.Equal(HttpFetchFailure.Transport, failure.Kind);
        Assert.Equal(1, _handler.CountFor(Page));
    }

    [Fact]
    public async Task A_body_without_a_declared_length_is_bounded_while_streaming()
    {
        _handler.Add(Image, () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new UnknownLengthStream(new byte[101])) });

        var failure = Assert.IsType<HttpFetchResult.Failure>(await _client.GetAsync(new Uri(Image), RequestKind.Image, "image/webp", 100, RetryBudget.CreateDefault(), Ct));

        Assert.Equal(HttpFetchFailure.TooLarge, failure.Kind);
    }

    [Fact]
    public async Task Not_found_is_reported_without_retrying()
    {
        var failure = Assert.IsType<HttpFetchResult.Failure>(await Get(new Uri(Page)));

        Assert.Equal(HttpFetchFailure.NotFound, failure.Kind);
        Assert.Equal(1, _handler.CountFor(Page));
    }

    [Fact]
    public async Task Redirects_are_followed_only_to_allowlisted_destinations()
    {
        _handler.Add(Page, () => Redirect("/summer-2025/tv?moved=1"))
            .Add(Page + "?moved=1", HttpStatusCode.OK, [1]);
        _handler.Add(Other, () => Redirect("https://evil.example/summer"));

        var followed = Assert.IsType<HttpFetchResult.Success>(await Get(new Uri(Page)));
        var refused = Assert.IsType<HttpFetchResult.Failure>(await Get(new Uri(Other)));

        Assert.Equal(new Uri(Page + "?moved=1"), followed.FinalUrl);
        Assert.Equal(HttpFetchFailure.Disallowed, refused.Kind);
        Assert.DoesNotContain(_handler.Requests, r => r.RequestUri!.Host == "evil.example");
    }

    [Fact]
    public async Task Image_redirect_to_an_unverified_host_is_refused()
    {
        _handler.Add(Image, () => Redirect("https://au.livechart.me/anime/1/x.jpg"));

        var failure = Assert.IsType<HttpFetchResult.Failure>(await _client.GetAsync(new Uri(Image), RequestKind.Image, "image/webp", 100, RetryBudget.CreateDefault(), Ct));

        Assert.Equal(HttpFetchFailure.Disallowed, failure.Kind);
    }

    [Fact]
    public async Task Responses_are_bounded_while_streaming_and_by_declared_length()
    {
        _handler.Add(Image, HttpStatusCode.OK, new byte[101]);

        var failure = Assert.IsType<HttpFetchResult.Failure>(await _client.GetAsync(new Uri(Image), RequestKind.Image, "image/webp", 100, RetryBudget.CreateDefault(), Ct));
        var exact = new SourceHttpClient(new ScriptedHandler().Add(Image, HttpStatusCode.OK, new byte[100]), SourceHttpPolicy.Default, "AnimeTierlist/0.1.0", _time, _time.DelayAsync);
        var accepted = await exact.GetAsync(new Uri(Image), RequestKind.Image, "image/webp", 100, RetryBudget.CreateDefault(), Ct);
        exact.Dispose();

        Assert.Equal(HttpFetchFailure.TooLarge, failure.Kind);
        Assert.Equal(100, Assert.IsType<HttpFetchResult.Success>(accepted).Body.Length);
    }

    [Fact]
    public async Task Cancellation_stops_without_retrying()
    {
        using var cancel = new CancellationTokenSource();
        _handler.BeforeResponse = (_, _) =>
        {
            cancel.Cancel();
            return Task.CompletedTask;
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _client.GetAsync(new Uri(Page), RequestKind.Catalogue, "text/html", 100, RetryBudget.CreateDefault(), cancel.Token));

        Assert.Equal(1, _handler.CountFor(Page));
    }

    private Task<HttpFetchResult> Get(Uri url) =>
        _client.GetAsync(url, RequestKind.Catalogue, "text/html", SourceHttpPolicy.Default.MaxCatalogueBytes, RetryBudget.CreateDefault(), Ct);

    private static HttpResponseMessage WithRetryAfter(HttpStatusCode status, TimeSpan delay)
    {
        var response = ScriptedHandler.Response(status);
        response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(delay);
        return response;
    }

    private static HttpResponseMessage Redirect(string location)
    {
        var response = ScriptedHandler.Response(HttpStatusCode.Found);
        response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
        return response;
    }

    /// <summary>A readable stream that cannot report its length, so the content has no declared length.</summary>
    private sealed class UnknownLengthStream(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while ((current = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }
}
