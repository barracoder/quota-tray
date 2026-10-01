using System.Text.Json;
using QuotaTray.Core;

namespace QuotaTray.Core.Tests;

public class ConfigStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "quota-tray-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void LoadOrCreate_writes_a_default_file_when_missing()
    {
        var store = new ConfigStore(Path.Combine(_dir, "config.json"));

        var config = store.LoadOrCreate();

        Assert.True(File.Exists(store.Path));
        Assert.Single(config.Providers);
        Assert.Equal("claude-subscription", config.Providers[0].Type);
    }

    [Fact]
    public void Unknown_provider_fields_survive_a_round_trip_and_are_readable()
    {
        var store = new ConfigStore(Path.Combine(_dir, "config.json"));
        Directory.CreateDirectory(_dir);
        File.WriteAllText(store.Path, """
            {
              // comments are allowed
              "pollIntervalSeconds": 120,
              "providers": [
                { "type": "anthropic-cost", "name": "Console", "apiKeyEnv": "MY_KEY", "monthlyBudgetUsd": 250.5, "verbose": true, },
              ],
            }
            """);

        var config = store.Load();
        var provider = config.Providers.Single();

        Assert.Equal(120, config.PollIntervalSeconds);
        Assert.Equal("MY_KEY", provider.ApiKeyEnv);
        Assert.Equal(250.5, provider.GetDouble("monthlyBudgetUsd"));
        Assert.True(provider.GetBool("verbose"));
        Assert.Null(provider.GetString("monthlyBudgetUsd"));

        store.Save(config);
        using var doc = JsonDocument.Parse(File.ReadAllText(store.Path));
        var saved = doc.RootElement.GetProperty("providers")[0];
        Assert.Equal(250.5, saved.GetProperty("monthlyBudgetUsd").GetDouble());
        Assert.False(saved.TryGetProperty("apiKey", out _), "null fields should not be written");
    }

    [Fact]
    public void EffectiveName_falls_back_to_type()
    {
        Assert.Equal("x", new ProviderConfig { Type = "x" }.EffectiveName);
        Assert.Equal("Named", new ProviderConfig { Type = "x", Name = "Named" }.EffectiveName);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
