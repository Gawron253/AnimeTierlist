using AnimeTierlist.Core;

namespace AnimeTierlist.Application.Catalogue;

/// <summary>Outcome of reading one season page. A failure is never an empty season.</summary>
public abstract record SeasonPageResult
{
    private SeasonPageResult()
    {
    }

    /// <summary>A validated page with its cards in source order. Zero occurrences means a positively verified empty season.</summary>
    public sealed record Valid(SeasonCapture Capture) : SeasonPageResult
    {
        public bool IsVerifiedEmpty => Capture.Occurrences.IsEmpty;
    }

    /// <summary>The page could not be validated. Nothing from it may be committed.</summary>
    public sealed record Invalid(SeasonPageFailure Failure, string Detail) : SeasonPageResult;
}

public enum SeasonPageFailure
{
    /// <summary>The response was not the requested season/category page (for example HTTP 404 or a different canonical route).</summary>
    WrongPage,

    /// <summary>Markup no longer matches the verified source contract (missing container, unrecognized cards).</summary>
    ChangedLayout,

    /// <summary>The page was not in the verified popularity sort context.</summary>
    WrongSortContext,

    /// <summary>A card lacks a usable LiveChart ID or displayed title.</summary>
    InvalidCard,

    /// <summary>The response exceeded configured size limits.</summary>
    TooLarge,

    /// <summary>Network failure, HTTP error, or timeout after the bounded retry attempts.</summary>
    Transport,

    /// <summary>No connection to the source could be established (offline, DNS failure, or a network that requires a proxy).</summary>
    Unreachable,

    /// <summary>A required retry delay exceeded the run's retry budget; requests to the host are paused for manual resume.</summary>
    Paused,
}
