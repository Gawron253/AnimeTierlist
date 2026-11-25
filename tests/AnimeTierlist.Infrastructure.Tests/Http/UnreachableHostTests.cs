using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using AnimeTierlist.Application.Acquisition;
using AnimeTierlist.Infrastructure.Http;

namespace AnimeTierlist.Infrastructure.Tests.Http;

/// <summary>
/// Connections are direct, so a host that cannot be reached at all (offline, DNS failure, refused connection,
/// connect timeout, or a network that only allows traffic through a proxy) is reported as Unreachable rather than as a
/// generic transport failure. The real SocketsHttpHandler is used so the classification follows its actual exceptions;
/// no socket is opened.
/// </summary>
public sealed class UnreachableHostTests
{
    private static readonly Uri Page = new("https://www.livechart.me/summer-2025/tv");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(SocketError.ConnectionRefused)]
    [InlineData(SocketError.HostNotFound)]
    [InlineData(SocketError.NetworkUnreachable)]
    [InlineData(SocketError.ConnectionReset)]
    public async Task Failing_to_connect_is_unreachable_after_the_bounded_attempts(SocketError error)
    {
        var attempts = 0;
        var time = new ManualTime();
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = (_, _) =>
            {
                Interlocked.Increment(ref attempts);
                throw new SocketException((int)error);
            },
        };
        using var client = new SourceHttpClient(handler, SourceHttpPolicy.Default, "AnimeTierlist/0.1.0", time, time.DelayAsync, jitter: () => 0);

        var failure = Assert.IsType<HttpFetchResult.Failure>(await client.GetAsync(Page, RequestKind.Catalogue, "text/html", 1024, RetryBudget.CreateDefault(), Ct));

        Assert.Equal(HttpFetchFailure.Unreachable, failure.Kind);
        Assert.Contains("www.livechart.me", failure.Detail, StringComparison.Ordinal);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task Connect_timeout_is_unreachable_but_a_slow_response_stays_a_transport_failure()
    {
        var time = new ManualTime();
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromMilliseconds(100),
            ConnectCallback = async (_, token) =>
            {
                await Task.Delay(Timeout.Infinite, token);
                throw new InvalidOperationException("unreachable");
            },
        };
        using var client = new SourceHttpClient(handler, SourceHttpPolicy.Default, "AnimeTierlist/0.1.0", time, time.DelayAsync, jitter: () => 0);

        var connect = Assert.IsType<HttpFetchResult.Failure>(await client.GetAsync(Page, RequestKind.Catalogue, "text/html", 1024, RetryBudget.CreateDefault(), Ct));

        Assert.Equal(HttpFetchFailure.Unreachable, connect.Kind);
        Assert.Contains("timed out", connect.Detail, StringComparison.Ordinal);

        // A host that accepts the connection but never answers within the request timeout is not "unreachable".
        var policy = SourceHttpPolicy.Default with { RequestTimeout = TimeSpan.FromMilliseconds(100) };
        using var slowClient = new SourceHttpClient(new NeverResponds(), policy, "AnimeTierlist/0.1.0", time, time.DelayAsync, jitter: () => 0);

        var response = Assert.IsType<HttpFetchResult.Failure>(await slowClient.GetAsync(Page, RequestKind.Catalogue, "text/html", 1024, RetryBudget.CreateDefault(), Ct));

        Assert.Equal(HttpFetchFailure.Transport, response.Kind);
    }

    [Fact]
    public async Task Private_address_refusal_still_wins_over_unreachable()
    {
        var time = new ManualTime();
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = (_, _) => throw new DisallowedAddressException("www.livechart.me does not resolve to a public network address."),
        };
        using var client = new SourceHttpClient(handler, SourceHttpPolicy.Default, "AnimeTierlist/0.1.0", time, time.DelayAsync, jitter: () => 0);

        var failure = Assert.IsType<HttpFetchResult.Failure>(await client.GetAsync(Page, RequestKind.Catalogue, "text/html", 1024, RetryBudget.CreateDefault(), Ct));

        Assert.Equal(HttpFetchFailure.Disallowed, failure.Kind);
    }

    [Fact]
    public void Production_handler_never_uses_a_proxy_and_reports_connect_timeouts_first()
    {
        using var handler = SourceHttpClient.CreateHandler();

        Assert.False(handler.UseProxy);
        Assert.True(handler.ConnectTimeout < SourceHttpPolicy.Default.RequestTimeout);
    }

    /// <summary>A connected host that never sends response headers; only the request's token ends the wait.</summary>
    private sealed class NeverResponds : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }
    }
}
