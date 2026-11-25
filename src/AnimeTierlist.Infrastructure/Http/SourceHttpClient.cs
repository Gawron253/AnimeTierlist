using System.Collections.Immutable;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using AnimeTierlist.Application.Acquisition;

namespace AnimeTierlist.Infrastructure.Http;

public enum RequestKind
{
    Catalogue,
    Image,
}

/// <summary>Request limits and host policy for LiveChart catalogue and cover downloads.</summary>
public sealed record SourceHttpPolicy(
    TimeSpan RequestTimeout,
    int MaxAttempts,
    TimeSpan BaseBackoff,
    TimeSpan CatalogueInterval,
    int ImageConcurrency,
    long MaxCatalogueBytes,
    int MaxRedirects,
    ImmutableHashSet<string> CatalogueHosts,
    ImmutableHashSet<string> ImageHosts)
{
    public static SourceHttpPolicy Default { get; } = new(
        RequestTimeout: TimeSpan.FromSeconds(30),
        MaxAttempts: 3,
        BaseBackoff: TimeSpan.FromSeconds(2),
        CatalogueInterval: TimeSpan.FromSeconds(5),
        ImageConcurrency: 2,
        MaxCatalogueBytes: 10L * 1024 * 1024,
        MaxRedirects: 3,
        CatalogueHosts: ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase, "www.livechart.me"),
        ImageHosts: ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase, "u.livechart.me"));
}

public enum HttpFetchFailure
{
    NotFound,

    /// <summary>The URL or a redirect target is outside the allowlist, or the host resolves to a non-public address.</summary>
    Disallowed,

    TooLarge,

    /// <summary>HTTP error, response timeout, or a connection lost mid-response, after the bounded attempts.</summary>
    Transport,

    /// <summary>
    /// No connection to the host could be established after the bounded attempts: name resolution failed, the
    /// connection was refused or reset while connecting, or connecting timed out. Connections are always direct
    /// (proxies are not used), so a network that only allows web access through a proxy also ends here.
    /// </summary>
    Unreachable,

    /// <summary>A retry delay exceeded the run's budget; requests to the host pause for manual resume.</summary>
    Paused,
}

public abstract record HttpFetchResult
{
    private HttpFetchResult()
    {
    }

    public sealed record Success(byte[] Body, Uri FinalUrl, string? MediaType) : HttpFetchResult;

    public sealed record Failure(HttpFetchFailure Kind, string Detail) : HttpFetchResult;
}

/// <summary>
/// The only HTTP path to the source. Allows HTTPS to explicitly allowlisted hosts per request kind, follows
/// redirects manually and revalidates each destination, sends no cookies, paces catalogue requests one at a time with
/// a minimum interval between starts, limits concurrent image requests, bounds response bytes while streaming, and
/// retries transient failures with exponential backoff and jitter or the server's Retry-After within a retry budget.
/// </summary>
public sealed class SourceHttpClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly TimeProvider _time;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Func<double> _jitter;
    private readonly SemaphoreSlim _catalogueGate = new(1, 1);
    private readonly SemaphoreSlim _imageGate;
    private readonly Lock _hostLock = new();
    private readonly Dictionary<string, DateTimeOffset> _notBefore = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset? _lastCatalogueStart;

    public SourceHttpClient(
        HttpMessageHandler handler,
        SourceHttpPolicy policy,
        string userAgent,
        TimeProvider time,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Func<double>? jitter = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentException.ThrowIfNullOrWhiteSpace(userAgent);
        ArgumentNullException.ThrowIfNull(time);
        Policy = policy;
        _time = time;
        _delay = delay ?? ((span, token) => Task.Delay(span, time, token));
        _jitter = jitter ?? Random.Shared.NextDouble;
        _imageGate = new SemaphoreSlim(policy.ImageConcurrency, policy.ImageConcurrency);
        _http = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
    }

    public SourceHttpPolicy Policy { get; }

    /// <summary>
    /// The production handler: no automatic redirects or cookies, and connections only to public addresses. It never
    /// uses a proxy: through a proxy the app could not verify the address it actually reaches. The connect
    /// timeout is shorter than the request timeout so that an unreachable host is reported as such.
    /// </summary>
    public static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = false,
        AutomaticDecompression = DecompressionMethods.All,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectTimeout = TimeSpan.FromSeconds(15),
        ConnectCallback = NetworkAddressPolicy.ConnectToPublicAddressAsync,
    };

    public bool IsAllowed(Uri url, RequestKind kind)
    {
        ArgumentNullException.ThrowIfNull(url);
        var hosts = kind == RequestKind.Catalogue ? Policy.CatalogueHosts : Policy.ImageHosts;
        return url.IsAbsoluteUri && url.Scheme == Uri.UriSchemeHttps && url.IsDefaultPort && string.IsNullOrEmpty(url.UserInfo) && hosts.Contains(url.IdnHost);
    }

    public async Task<HttpFetchResult> GetAsync(Uri url, RequestKind kind, string accept, long maxBytes, RetryBudget budget, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(url);
        ArgumentException.ThrowIfNullOrWhiteSpace(accept);
        ArgumentNullException.ThrowIfNull(budget);
        if (!IsAllowed(url, kind))
        {
            return new HttpFetchResult.Failure(HttpFetchFailure.Disallowed, $"{url} is not an allowed {kind.ToString().ToLowerInvariant()} URL.");
        }

        var gate = kind == RequestKind.Catalogue ? _catalogueGate : _imageGate;
        for (var attempt = 1; ; attempt++)
        {
            Attempt outcome;
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                outcome = await SendOnceAsync(url, kind, accept, maxBytes, budget, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }

            if (!outcome.Retryable || attempt >= Policy.MaxAttempts)
            {
                return outcome.Result;
            }

            if (outcome.RetryAfter is not null)
            {
                // The recorded not-before deadline is waited (and budgeted) before the next attempt is sent.
                continue;
            }

            var wait = Backoff(attempt);
            if (!budget.TryConsume(wait))
            {
                return new HttpFetchResult.Failure(HttpFetchFailure.Paused,
                    $"Requests to {url.IdnHost} are paused: the next retry needs a {wait.TotalSeconds:0}-second wait, beyond the remaining retry budget.");
            }

            await _delay(wait, cancellationToken).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        _http.Dispose();
        _catalogueGate.Dispose();
        _imageGate.Dispose();
    }

    private async Task PaceCatalogueAsync(CancellationToken cancellationToken)
    {
        if (_lastCatalogueStart is { } last)
        {
            var wait = last + Policy.CatalogueInterval - _time.GetUtcNow();
            if (wait > TimeSpan.Zero)
            {
                await _delay(wait, cancellationToken).ConfigureAwait(false);
            }
        }

        _lastCatalogueStart = _time.GetUtcNow();
    }

    /// <summary>
    /// Waits out the host's server-requested delay, rechecking after each wait because another worker may extend it.
    /// Returns a pause failure instead of sending early when the wait does not fit the retry budget.
    /// </summary>
    private async Task<HttpFetchResult.Failure?> WaitForHostAsync(string host, RetryBudget budget, CancellationToken cancellationToken)
    {
        for (var hold = HoldFor(host); hold > TimeSpan.Zero; hold = HoldFor(host))
        {
            if (!budget.TryConsume(hold))
            {
                return new HttpFetchResult.Failure(HttpFetchFailure.Paused,
                    $"Requests to {host} are paused: the server asked for a {hold.TotalSeconds:0}-second wait, beyond the remaining retry budget.");
            }

            await _delay(hold, cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    private TimeSpan HoldFor(string host)
    {
        lock (_hostLock)
        {
            return HoldForLocked(host);
        }
    }

    private TimeSpan HoldForLocked(string host)
    {
        var now = _time.GetUtcNow();
        return _notBefore.TryGetValue(host, out var until) && until > now ? until - now : TimeSpan.Zero;
    }

    private void RecordNotBefore(string host, DateTimeOffset until)
    {
        lock (_hostLock)
        {
            if (!_notBefore.TryGetValue(host, out var existing) || existing < until)
            {
                _notBefore[host] = until;
            }
        }
    }

    private TimeSpan Backoff(int attempt) =>
        (Policy.BaseBackoff * Math.Pow(2, attempt - 1)) + (Policy.BaseBackoff * _jitter());

    private async Task<Attempt> SendOnceAsync(Uri url, RequestKind kind, string accept, long maxBytes, RetryBudget budget, CancellationToken cancellationToken)
    {
        var current = url;
        try
        {
            for (var hop = 0; ; hop++)
            {
                // Every actual send, including a redirect hop, honours the host's server-requested delay and, for
                // the catalogue, the minimum interval between request starts. The request timeout starts afterwards.
                if (await WaitForHostAsync(current.IdnHost, budget, cancellationToken).ConfigureAwait(false) is { } paused)
                {
                    return new Attempt(paused, false, null);
                }

                if (kind == RequestKind.Catalogue)
                {
                    await PaceCatalogueAsync(cancellationToken).ConfigureAwait(false);
                }

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                using var request = new HttpRequestMessage(HttpMethod.Get, current);
                request.Headers.Accept.ParseAdd(accept);
                Task<HttpResponseMessage> sending;
                while (true)
                {
                    // The final deadline check and the start of the send are atomic with recording a deadline, so
                    // a hold recorded by another worker is either seen here or applies to a request already started.
                    lock (_hostLock)
                    {
                        if (HoldForLocked(current.IdnHost) <= TimeSpan.Zero)
                        {
                            sending = _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                            break;
                        }
                    }

                    if (await WaitForHostAsync(current.IdnHost, budget, cancellationToken).ConfigureAwait(false) is { } held)
                    {
                        return new Attempt(held, false, null);
                    }
                }

                timeout.CancelAfter(Policy.RequestTimeout);
                using var response = await sending.ConfigureAwait(false);
                var status = (int)response.StatusCode;
                if (status is 301 or 302 or 303 or 307 or 308)
                {
                    var location = response.Headers.Location;
                    var next = location is null ? null : location.IsAbsoluteUri ? location : new Uri(current, location);
                    if (next is null || hop >= Policy.MaxRedirects || !IsAllowed(next, kind))
                    {
                        return Final(HttpFetchFailure.Disallowed, $"{current} redirected to a destination that is not allowed ({next?.ToString() ?? "none"}).");
                    }

                    current = next;
                    continue;
                }

                if (status is 404 or 410)
                {
                    return Final(HttpFetchFailure.NotFound, $"{current} returned HTTP {status}.");
                }

                if (status is 408 or 429 || status >= 500)
                {
                    // Record the server's requested delay against the host that answered, before this slot is released.
                    var retryAfter = RetryAfter(response);
                    if (retryAfter is { } requested)
                    {
                        RecordNotBefore(current.IdnHost, _time.GetUtcNow() + requested);
                    }

                    return new Attempt(new HttpFetchResult.Failure(HttpFetchFailure.Transport, $"{current} returned HTTP {status}."), true, retryAfter);
                }

                // Only a complete 200 response is accepted; the client never asks for partial content.
                if (status != 200)
                {
                    return Final(HttpFetchFailure.Transport, $"{current} returned HTTP {status}.");
                }

                if (response.Content.Headers.ContentLength > maxBytes)
                {
                    return Final(HttpFetchFailure.TooLarge, $"{current} is larger than {maxBytes} bytes.");
                }

                var body = await ReadBoundedAsync(response.Content, maxBytes, timeout.Token).ConfigureAwait(false);
                return body is null
                    ? Final(HttpFetchFailure.TooLarge, $"{current} is larger than {maxBytes} bytes.")
                    : new Attempt(new HttpFetchResult.Success(body, current, response.Content.Headers.ContentType?.MediaType), false, null);
            }
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested && ex.InnerException is TimeoutException)
        {
            // SocketsHttpHandler.ConnectTimeout elapsed; the request's own timeout carries no TimeoutException.
            return new Attempt(new HttpFetchResult.Failure(HttpFetchFailure.Unreachable, $"Connecting to {current.IdnHost} timed out."), true, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new Attempt(new HttpFetchResult.Failure(HttpFetchFailure.Transport, $"{current} timed out."), true, null);
        }
        catch (HttpRequestException ex) when (ex.InnerException is DisallowedAddressException)
        {
            return Final(HttpFetchFailure.Disallowed, ex.InnerException.Message);
        }
        catch (HttpRequestException ex) when (ex.HttpRequestError is HttpRequestError.NameResolutionError or HttpRequestError.ConnectionError)
        {
            return new Attempt(new HttpFetchResult.Failure(HttpFetchFailure.Unreachable, $"Could not connect to {current.IdnHost}: {ex.Message}"), true, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            return new Attempt(new HttpFetchResult.Failure(HttpFetchFailure.Transport, $"{current}: {ex.Message}"), true, null);
        }
    }

    private TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header?.Delta is { } delta)
        {
            return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        }

        if (header?.Date is { } date)
        {
            var wait = date - _time.GetUtcNow();
            return wait < TimeSpan.Zero ? TimeSpan.Zero : wait;
        }

        return null;
    }

    private static async Task<byte[]?> ReadBoundedAsync(HttpContent content, long maxBytes, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static Attempt Final(HttpFetchFailure kind, string detail) => new(new HttpFetchResult.Failure(kind, detail), false, null);

    private readonly record struct Attempt(HttpFetchResult Result, bool Retryable, TimeSpan? RetryAfter);
}
