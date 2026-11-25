namespace AnimeTierlist.Application.Acquisition;

/// <summary>
/// Cumulative backoff waiting allowed for one acquisition run (default two minutes). Productive requests and
/// transfers do not consume it. When a pending retry or Retry-After delay does not fit, the run pauses for manual
/// resume instead of retrying early; resuming starts a new budget.
/// </summary>
public sealed class RetryBudget
{
    private readonly Lock _gate = new();
    private TimeSpan _remaining;

    public RetryBudget(TimeSpan total)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(total, TimeSpan.Zero);
        _remaining = total;
    }

    public static TimeSpan DefaultTotal { get; } = TimeSpan.FromMinutes(2);

    public static RetryBudget CreateDefault() => new(DefaultTotal);

    public TimeSpan Remaining
    {
        get
        {
            lock (_gate)
            {
                return _remaining;
            }
        }
    }

    /// <summary>Reserves <paramref name="delay"/> if it fits in the remaining budget.</summary>
    public bool TryConsume(TimeSpan delay)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(delay, TimeSpan.Zero);
        lock (_gate)
        {
            if (delay > _remaining)
            {
                return false;
            }

            _remaining -= delay;
            return true;
        }
    }
}
