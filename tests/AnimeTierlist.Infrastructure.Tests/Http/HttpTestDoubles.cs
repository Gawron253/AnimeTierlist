using System.Net;
using System.Net.Http;

namespace AnimeTierlist.Infrastructure.Tests.Http;

/// <summary>Serves scripted responses per absolute URL (404 when unscripted) and records every request.</summary>
internal sealed class ScriptedHandler : HttpMessageHandler
{
    private readonly Dictionary<string, Queue<Func<HttpResponseMessage>>> _responses = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public List<HttpRequestMessage> Requests { get; } = [];

    /// <summary>Runs before each response with the request's cancellation token, for example to hold a request open.</summary>
    public Func<HttpRequestMessage, CancellationToken, Task>? BeforeResponse { get; set; }

    public ScriptedHandler Add(string url, Func<HttpResponseMessage> response)
    {
        lock (_gate)
        {
            if (!_responses.TryGetValue(url, out var queue))
            {
                _responses[url] = queue = new Queue<Func<HttpResponseMessage>>();
            }

            queue.Enqueue(response);
        }

        return this;
    }

    public ScriptedHandler Add(string url, HttpStatusCode status, byte[]? body = null, string? mediaType = null) =>
        Add(url, () => Response(status, body, mediaType));

    public int RequestCount
    {
        get
        {
            lock (_gate)
            {
                return Requests.Count;
            }
        }
    }

    public int CountFor(string url)
    {
        lock (_gate)
        {
            return Requests.Count(r => r.RequestUri!.AbsoluteUri == url);
        }
    }

    public static HttpResponseMessage Response(HttpStatusCode status, byte[]? body = null, string? mediaType = null)
    {
        var response = new HttpResponseMessage(status) { Content = new ByteArrayContent(body ?? []) };
        if (mediaType is not null)
        {
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(mediaType);
        }

        return response;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Func<HttpResponseMessage>? next = null;
        lock (_gate)
        {
            Requests.Add(request);
            if (_responses.TryGetValue(request.RequestUri!.AbsoluteUri, out var queue) && queue.Count > 0)
            {
                next = queue.Count == 1 ? queue.Peek() : queue.Dequeue();
            }
        }

        if (BeforeResponse is { } hook)
        {
            await hook(request, cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return next?.Invoke() ?? Response(HttpStatusCode.NotFound);
    }
}

/// <summary>A clock that only moves when a recorded delay advances it.</summary>
internal sealed class ManualTime : TimeProvider
{
    private readonly Lock _gate = new();
    private DateTimeOffset _now = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);

    public List<TimeSpan> Delays { get; } = [];

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _now;
        }
    }

    public void Advance(TimeSpan span)
    {
        lock (_gate)
        {
            _now += span;
        }
    }

    public Task DelayAsync(TimeSpan span, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            Delays.Add(span);
            _now += span;
        }

        return Task.CompletedTask;
    }
}
