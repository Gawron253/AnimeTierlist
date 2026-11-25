namespace AnimeTierlist.Core;

/// <summary>LiveChart's anime ID. Item identity never depends on titles, filenames, or image bytes.</summary>
public readonly record struct LiveChartId
{
    public LiveChartId(int value)
    {
        if (value <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, "LiveChart IDs are positive integers.");
        }

        Value = value;
    }

    public int Value { get; }

    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>Seasons in calendar order. A year starts with winter.</summary>
public enum Season
{
    Winter = 0,
    Spring = 1,
    Summer = 2,
    Fall = 3,
}

/// <summary>LiveChart anime chart categories.</summary>
public enum CatalogueCategory
{
    Tv,
    Movies,
    Ovas,
    All,
}

public readonly record struct SeasonKey(int Year, Season Season) : IComparable<SeasonKey>
{
    public int CompareTo(SeasonKey other)
    {
        var byYear = Year.CompareTo(other.Year);
        return byYear != 0 ? byYear : Season.CompareTo(other.Season);
    }

    public static bool operator <(SeasonKey left, SeasonKey right) => left.CompareTo(right) < 0;

    public static bool operator <=(SeasonKey left, SeasonKey right) => left.CompareTo(right) <= 0;

    public static bool operator >(SeasonKey left, SeasonKey right) => left.CompareTo(right) > 0;

    public static bool operator >=(SeasonKey left, SeasonKey right) => left.CompareTo(right) >= 0;

    public override string ToString() => $"{Season} {Year}";
}

/// <summary>
/// What a collection was acquired from: one season, or a whole calendar year when <see cref="Season"/> is null.
/// Changing this creates another collection rather than altering an existing one.
/// </summary>
public sealed record AcquisitionContext
{
    public const int MinimumYear = 1900;
    public const int MaximumYear = 2200;

    public AcquisitionContext(int year, Season? season, CatalogueCategory category)
    {
        if (year < MinimumYear || year > MaximumYear)
        {
            throw new ArgumentOutOfRangeException(nameof(year), year, "Year is outside the supported range.");
        }

        if (season is { } s && !Enum.IsDefined(s))
        {
            throw new ArgumentOutOfRangeException(nameof(season));
        }

        if (!Enum.IsDefined(category))
        {
            throw new ArgumentOutOfRangeException(nameof(category));
        }

        Year = year;
        Season = season;
        Category = category;
    }

    public int Year { get; }

    public Season? Season { get; }

    public CatalogueCategory Category { get; }

    public bool IsWholeYear => Season is null;

    /// <summary>The seasons this context requires, in calendar order.</summary>
    public IReadOnlyList<SeasonKey> RequiredSeasons =>
        Season is { } single
            ? [new SeasonKey(Year, single)]
            : [new SeasonKey(Year, Core.Season.Winter), new SeasonKey(Year, Core.Season.Spring), new SeasonKey(Year, Core.Season.Summer), new SeasonKey(Year, Core.Season.Fall)];
}
