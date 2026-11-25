using AnimeTierlist.Application.Storage;

namespace AnimeTierlist.Infrastructure.Tests.Session;

/// <summary>Wraps a real store to fail chosen commits or hold commits until released.</summary>
internal sealed class FaultInjectingStore(IProjectStore inner) : IProjectStore
{
    private int _failNext;
    private int _committed;

    /// <summary>When set, each commit waits for this task before proceeding.</summary>
    public Task? Gate { get; set; }

    public int CommittedCount => Volatile.Read(ref _committed);

    public void FailNextCommits(int count) => Volatile.Write(ref _failNext, count);

    public Task<ProjectData> LoadAsync(CancellationToken cancellationToken) => inner.LoadAsync(cancellationToken);

    public async Task CommitAsync(ProjectChange change, CancellationToken cancellationToken)
    {
        if (Gate is { } gate)
        {
            await gate.WaitAsync(cancellationToken);
        }

        if (Interlocked.Decrement(ref _failNext) >= 0)
        {
            throw new ProjectStoreException("Injected commit failure.");
        }

        Interlocked.Exchange(ref _failNext, 0);
        await inner.CommitAsync(change, cancellationToken);
        Interlocked.Increment(ref _committed);
    }

    public ValueTask DisposeAsync() => inner.DisposeAsync();
}
