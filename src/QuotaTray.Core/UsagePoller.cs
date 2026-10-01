namespace QuotaTray.Core;

/// <summary>
/// Polls every provider on a fixed interval. Providers run concurrently; a failure in one
/// becomes an error snapshot rather than aborting the round.
/// </summary>
public sealed class UsagePoller
{
    private readonly IReadOnlyList<IUsageProvider> _providers;
    private readonly TimeSpan _interval;
    private readonly TimeSpan _perProviderTimeout;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public UsagePoller(
        IReadOnlyList<IUsageProvider> providers,
        TimeSpan interval,
        TimeProvider? clock = null,
        TimeSpan? perProviderTimeout = null)
    {
        if (interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(interval), "Poll interval must be positive.");
        }

        _providers = providers;
        _interval = interval;
        _clock = clock ?? TimeProvider.System;
        _perProviderTimeout = perProviderTimeout ?? TimeSpan.FromSeconds(20);
    }

    public IReadOnlyList<UsageSnapshot> Latest { get; private set; } = [];

    /// <summary>Raised on the calling thread of the poll loop; marshal to the UI thread yourself.</summary>
    public event Action<IReadOnlyList<UsageSnapshot>>? Updated;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_interval, _clock);
        do
        {
            await RefreshAsync(cancellationToken).ConfigureAwait(false);
        }
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Poll every provider once. Concurrent calls coalesce into the in-flight round.</summary>
    public async Task<IReadOnlyList<UsageSnapshot>> RefreshAsync(CancellationToken cancellationToken)
    {
        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            _gate.Release();
            return Latest;
        }

        try
        {
            var results = await Task.WhenAll(_providers.Select(p => FetchOneAsync(p, cancellationToken)))
                                    .ConfigureAwait(false);
            Latest = results;
            Updated?.Invoke(results);
            return results;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<UsageSnapshot> FetchOneAsync(IUsageProvider provider, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_perProviderTimeout);
        try
        {
            return await provider.FetchAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return UsageSnapshot.Failed(provider, _clock.GetUtcNow(),
                $"Timed out after {_perProviderTimeout.TotalSeconds:0}s.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return UsageSnapshot.Failed(provider, _clock.GetUtcNow(), ex.Message);
        }
    }
}
