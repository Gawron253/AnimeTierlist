namespace AnimeTierlist.Core;

/// <summary>Per-collection title display choice. <see cref="SourceDefault"/> is LiveChart's displayed title.</summary>
public enum TitleLanguage
{
    SourceDefault,
    English,
    Romanized,
    Native,
}

/// <summary>An effective title and whether a requested language was unavailable.</summary>
public readonly record struct ResolvedTitle(string Text, TitleSource Source, bool IsLanguageFallback);

public enum TitleSource
{
    UserOverride,
    SelectedLanguage,
    SourceDefault,
}

public static class TitleResolver
{
    /// <summary>Resolves user override, then the selected source language, then the captured source-default title.</summary>
    public static ResolvedTitle Resolve(SourceTitles observed, TitleLanguage language, string? userOverride)
    {
        ArgumentNullException.ThrowIfNull(observed);

        if (!string.IsNullOrWhiteSpace(userOverride))
        {
            return new ResolvedTitle(userOverride, TitleSource.UserOverride, false);
        }

        if (language == TitleLanguage.SourceDefault)
        {
            return new ResolvedTitle(observed.Displayed, TitleSource.SourceDefault, false);
        }

        var selected = language switch
        {
            TitleLanguage.English => observed.English,
            TitleLanguage.Romanized => observed.Romanized,
            TitleLanguage.Native => observed.Native,
            _ => throw new ArgumentOutOfRangeException(nameof(language)),
        };

        return selected is null
            ? new ResolvedTitle(observed.Displayed, TitleSource.SourceDefault, true)
            : new ResolvedTitle(selected, TitleSource.SelectedLanguage, false);
    }
}
