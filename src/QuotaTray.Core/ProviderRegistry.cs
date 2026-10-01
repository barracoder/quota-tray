namespace QuotaTray.Core;

/// <summary>Maps config <c>type</c> strings to factories and builds the enabled providers.</summary>
public sealed class ProviderRegistry
{
    private readonly Dictionary<string, IUsageProviderFactory> _factories = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<IUsageProviderFactory> Factories => _factories.Values;

    public ProviderRegistry Register(IUsageProviderFactory factory)
    {
        if (!_factories.TryAdd(factory.Type, factory))
        {
            throw new InvalidOperationException($"A provider factory for type '{factory.Type}' is already registered.");
        }

        return this;
    }

    public bool TryGet(string type, out IUsageProviderFactory factory) => _factories.TryGetValue(type, out factory!);

    /// <summary>
    /// Builds one provider per enabled config entry. Unknown types become a provider that always
    /// reports an error, so the misconfiguration is visible in the menu instead of silently dropped.
    /// </summary>
    public IReadOnlyList<IUsageProvider> Build(AppConfig config, ProviderContext context)
    {
        var providers = new List<IUsageProvider>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in config.Providers.Where(p => p.Enabled))
        {
            var id = MakeUniqueId(entry, seenIds);
            if (!_factories.TryGetValue(entry.Type, out var factory))
            {
                providers.Add(new MisconfiguredProvider(id, entry.EffectiveName,
                    $"Unknown provider type '{entry.Type}'. Known types: {string.Join(", ", _factories.Keys.Order())}."));
                continue;
            }

            try
            {
                providers.Add(new NamedProvider(id, entry.EffectiveName, factory.Create(entry, context)));
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or FormatException)
            {
                providers.Add(new MisconfiguredProvider(id, entry.EffectiveName, ex.Message));
            }
        }

        return providers;
    }

    private static string MakeUniqueId(ProviderConfig entry, HashSet<string> seen)
    {
        var baseId = entry.EffectiveName.ToLowerInvariant().Replace(' ', '-');
        var id = baseId;
        var n = 2;
        while (!seen.Add(id))
        {
            id = $"{baseId}-{n++}";
        }

        return id;
    }

    /// <summary>Overrides the id/name a factory chose with the ones derived from config.</summary>
    private sealed class NamedProvider(string id, string name, IUsageProvider inner) : IUsageProvider
    {
        public string Id => id;
        public string DisplayName => name;
        public Task<UsageSnapshot> FetchAsync(CancellationToken cancellationToken) =>
            inner.FetchAsync(cancellationToken)
                 .ContinueWith(t => t.Result with { ProviderId = id, ProviderName = name },
                     cancellationToken, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
    }

    private sealed class MisconfiguredProvider(string id, string name, string error) : IUsageProvider
    {
        public string Id => id;
        public string DisplayName => name;
        public Task<UsageSnapshot> FetchAsync(CancellationToken cancellationToken) =>
            Task.FromException<UsageSnapshot>(new InvalidOperationException(error));
    }
}
