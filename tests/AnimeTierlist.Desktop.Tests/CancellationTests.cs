using System.Diagnostics;
using System.Net.Http;
using AnimeTierlist.Desktop.ViewModels;

namespace AnimeTierlist.Desktop.Tests;

/// <summary>
/// Cancellation: with LiveChart requests held open, the Cancel and Stop covers commands cancel the active
/// requests through their tokens, start no new request, and finish (the UI's acknowledgement) within 200 ms.
/// </summary>
public sealed class CancellationTests
{
    private static readonly TimeSpan Acknowledgement = TimeSpan.FromMilliseconds(200);

    [Fact]
    public Task Cancelling_a_whole_year_fetch_cancels_the_active_request_and_requests_no_further_season() => Ui.RunAsync(async () =>
    {
        await using var harness = await DesktopHarness.CreateAsync();
        var held = Hold(harness, "www.livechart.me");
        var acquisition = harness.Project.Acquisition;
        acquisition.YearText = "2025";
        acquisition.Season = acquisition.SeasonOptions.Single(o => o.Label == "Whole year");
        acquisition.Category = acquisition.CategoryOptions.Single(o => o.Label == "Movies");

        acquisition.FetchCommand.Execute(null);
        await Ui.UntilAsync(() => held.Active == 1, "the first season request in flight");
        var elapsed = await CancelAsync(acquisition.FetchCancelCommand.Execute, acquisition.FetchCommand.ExecutionTask!);

        Assert.True(elapsed < Acknowledgement, $"Cancel took {elapsed.TotalMilliseconds:F0} ms.");
        Assert.Equal(1, held.Cancelled);
        Assert.StartsWith("Cancelled.", acquisition.ProgressText, StringComparison.Ordinal);
        Assert.False(acquisition.HasPreview);
        await Task.Delay(300);
        Assert.Equal(1, harness.Handler.RequestCount);
        TestContext.Current.TestOutputHelper?.WriteLine($"Catalogue cancel acknowledged after {elapsed.TotalMilliseconds:F1} ms.");
    });

    [Fact]
    public Task Stopping_covers_cancels_both_active_downloads_and_starts_no_other() => Ui.RunAsync(async () =>
    {
        await using var harness = await DesktopHarness.CreateAsync();
        var acquisition = harness.Project.Acquisition;
        acquisition.YearText = "2025";
        acquisition.Season = acquisition.SeasonOptions.Single(o => o.Label == "Summer");
        acquisition.Category = acquisition.CategoryOptions.Single(o => o.Label == "Movies");
        await acquisition.FetchCommand.ExecuteAsync(null);
        var held = Hold(harness, "u.livechart.me");
        var before = harness.Handler.RequestCount;

        // Creating the collection starts its cover run, at most two requests at a time.
        acquisition.CreateCommand.Execute(null);
        var view = Assert.IsType<CollectionViewModel>(harness.Project.Content);
        await Ui.UntilAsync(() => held.Active == 2, "two cover requests in flight");
        var elapsed = await CancelAsync(view.FetchCoversCancelCommand.Execute, view.FetchCoversCommand.ExecutionTask!);

        Assert.True(elapsed < Acknowledgement, $"Stop covers took {elapsed.TotalMilliseconds:F0} ms.");
        Assert.Equal(2, held.Cancelled);
        Assert.Contains("cancelled", view.CoverStatus, StringComparison.Ordinal);
        await Task.Delay(300);
        Assert.Equal(2, harness.Handler.RequestCount - before);
        Assert.All(view.Entries, e => Assert.False(e.HasCover));
        TestContext.Current.TestOutputHelper?.WriteLine($"Cover cancel acknowledged after {elapsed.TotalMilliseconds:F1} ms; exactly two cover requests were made, both cancelled.");
    });

    /// <summary>
    /// Invokes the cancel command and returns the time until the running command has finished. A cancellation path
    /// that does not reach the held requests fails after 5 s instead of waiting for HTTP timeouts.
    /// </summary>
    private static async Task<TimeSpan> CancelAsync(Action<object?> cancel, Task running)
    {
        var clock = Stopwatch.StartNew();
        cancel(null);
        await running.WaitAsync(TimeSpan.FromSeconds(5));
        return clock.Elapsed;
    }

    /// <summary>Holds every request to <paramref name="host"/> open until its cancellation token fires.</summary>
    private static HeldRequests Hold(DesktopHarness harness, string host)
    {
        var held = new HeldRequests();
        harness.Handler.BeforeResponse = async (request, cancellationToken) =>
        {
            if (request.RequestUri!.Host != host)
            {
                return;
            }

            Interlocked.Increment(ref held.ActiveCount);
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Interlocked.Increment(ref held.CancelledCount);
                throw;
            }
        };
        return held;
    }

    private sealed class HeldRequests
    {
#pragma warning disable SA1401, CA1051 // Fields are updated with Interlocked.
        public int ActiveCount;
        public int CancelledCount;
#pragma warning restore SA1401, CA1051

        public int Active => Volatile.Read(ref ActiveCount);

        public int Cancelled => Volatile.Read(ref CancelledCount);
    }
}
