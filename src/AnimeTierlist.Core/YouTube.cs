using System.Diagnostics.CodeAnalysis;

namespace AnimeTierlist.Core;

/// <summary>
/// A validated YouTube video ID: exactly 11 characters from <c>[A-Za-z0-9_-]</c>. IDs are case-sensitive.
/// Only IDs parsed from an allowed HTTPS YouTube link can reach the in-app player.
/// </summary>
public sealed record YouTubeVideoId
{
    public const int Length = 11;

    private YouTubeVideoId(string value) => Value = value;

    public string Value { get; }

    public override string ToString() => Value;

    /// <summary>True when <paramref name="value"/> has the exact YouTube video ID shape.</summary>
    public static bool IsValid([NotNullWhen(true)] string? value) =>
        value is { Length: Length } && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    /// <summary>
    /// Extracts the video ID from a supported YouTube link: <c>youtube.com/watch?v=ID</c>, <c>/embed/ID</c>,
    /// <c>/shorts/ID</c>, <c>/live/ID</c> on <c>youtube.com</c>, <c>www.youtube.com</c>, or <c>m.youtube.com</c>, and
    /// <c>youtu.be/ID</c>. The link must be HTTPS on the default port without credentials. Anything else is rejected.
    /// </summary>
    public static bool TryParse(Uri? url, [NotNullWhen(true)] out YouTubeVideoId? id)
    {
        id = null;
        if (!ExternalUrlPolicy.IsSafeHttpsUrl(url))
        {
            return false;
        }

        var host = url!.IdnHost.ToLowerInvariant();
        var segments = url.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        string? candidate = host switch
        {
            "youtu.be" when segments.Length == 1 => segments[0],
            "www.youtube.com" or "youtube.com" or "m.youtube.com" => segments switch
            {
                ["watch"] => SingleQueryValue(url.Query, "v"),
                ["embed" or "shorts" or "live", var value] => value,
                _ => null,
            },
            _ => null,
        };

        if (!IsValid(candidate))
        {
            return false;
        }

        id = new YouTubeVideoId(candidate);
        return true;
    }

    /// <summary>Returns the value of <paramref name="name"/> only when it occurs exactly once in the query.</summary>
    private static string? SingleQueryValue(string query, string name)
    {
        string? found = null;
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=', StringComparison.Ordinal);
            var key = separator < 0 ? pair : pair[..separator];
            if (!string.Equals(key, name, StringComparison.Ordinal))
            {
                continue;
            }

            if (found is not null)
            {
                return null;
            }

            found = separator < 0 ? string.Empty : pair[(separator + 1)..];
        }

        return found;
    }
}

/// <summary>Why the in-app player could not play a video. Every reason falls back to opening the link externally.</summary>
public enum YouTubePlayerFailure
{
    /// <summary>The uploader disabled playback on other sites (IFrame API errors 101 and 150).</summary>
    EmbeddingDisabled,

    /// <summary>The video was removed or made private (error 100).</summary>
    NotFound,

    /// <summary>YouTube rejected the player's client identification (error 153).</summary>
    IdentificationRejected,

    /// <summary>The request or the HTML5 player failed (errors 2 and 5), or YouTube reported an unknown code.</summary>
    PlaybackError,

    /// <summary>YouTube could not be reached, or the player did not report ready in time (usually offline).</summary>
    Unreachable,

    /// <summary>The Microsoft Edge WebView2 Runtime is not installed.</summary>
    RuntimeMissing,

    /// <summary>The embedded browser could not start or load the player page.</summary>
    StartFailed,
}

/// <summary>A status message posted by the packaged player page.</summary>
public abstract record YouTubePlayerMessage
{
    private YouTubePlayerMessage()
    {
    }

    /// <summary>The YouTube player loaded and can play.</summary>
    public sealed record Ready : YouTubePlayerMessage;

    /// <summary>The video started playing for the first time.</summary>
    public sealed record Started : YouTubePlayerMessage;

    /// <summary>The YouTube player reported an IFrame API error code.</summary>
    public sealed record Failed(int Code) : YouTubePlayerMessage;

    /// <summary>The page could not load YouTube's player script.</summary>
    public sealed record Unreachable : YouTubePlayerMessage;
}

/// <summary>
/// Rules for the sandboxed in-app YouTube player. The player is a packaged local page served from a virtual
/// HTTPS host. It embeds only the official privacy-enhanced YouTube player for one validated video ID.
/// </summary>
public static class YouTubePlayerPolicy
{
    /// <summary>
    /// Virtual host that serves the packaged player page. The <c>.invalid</c> top-level domain is reserved, so the name
    /// never resolves on a real network. The page origin is sent to YouTube as the client identification.
    /// </summary>
    public const string PlayerHost = "player.animetierlist.invalid";

    public const string PlayerPagePath = "/player.html";

    /// <summary>YouTube's Terms of Service, linked from the player as YouTube's developer policies require.</summary>
    public static Uri TermsOfServiceUrl { get; } = new("https://www.youtube.com/t/terms");

    /// <summary>How long the player may take to report ready before it falls back to the external link.</summary>
    public static TimeSpan ReadyTimeout { get; } = TimeSpan.FromSeconds(20);

    /// <summary>The packaged player page for one validated video.</summary>
    public static Uri PlayerPageUrl(YouTubeVideoId id)
    {
        ArgumentNullException.ThrowIfNull(id);
        return new Uri($"https://{PlayerHost}{PlayerPagePath}?v={id.Value}");
    }

    /// <summary>True only for the packaged player page. Every other top-level navigation is cancelled.</summary>
    public static bool IsPlayerPage(Uri? url) =>
        url is not null
        && url.IsAbsoluteUri
        && url.Scheme == Uri.UriSchemeHttps
        && url.IsDefaultPort
        && string.IsNullOrEmpty(url.UserInfo)
        && string.Equals(url.IdnHost, PlayerHost, StringComparison.OrdinalIgnoreCase)
        && string.Equals(url.AbsolutePath, PlayerPagePath, StringComparison.Ordinal);

    /// <summary>
    /// Parses a message from the player page. Accepted forms are exactly <c>ready</c>, <c>started</c>,
    /// <c>unreachable</c>, and <c>error:N</c> with a decimal code of one to four digits. Anything else returns null
    /// and is ignored.
    /// </summary>
    public static YouTubePlayerMessage? ParseMessage(string? message)
    {
        switch (message)
        {
            case "ready":
                return new YouTubePlayerMessage.Ready();
            case "started":
                return new YouTubePlayerMessage.Started();
            case "unreachable":
                return new YouTubePlayerMessage.Unreachable();
        }

        const string errorPrefix = "error:";
        if (message is null || !message.StartsWith(errorPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        var digits = message.AsSpan(errorPrefix.Length);
        if (digits.Length is < 1 or > 4)
        {
            return null;
        }

        foreach (var c in digits)
        {
            if (!char.IsAsciiDigit(c))
            {
                return null;
            }
        }

        return new YouTubePlayerMessage.Failed(int.Parse(digits, System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>Maps a YouTube IFrame API error code to a failure reason.</summary>
    public static YouTubePlayerFailure ClassifyError(int code) => code switch
    {
        101 or 150 => YouTubePlayerFailure.EmbeddingDisabled,
        100 => YouTubePlayerFailure.NotFound,
        153 => YouTubePlayerFailure.IdentificationRejected,
        _ => YouTubePlayerFailure.PlaybackError,
    };
}
