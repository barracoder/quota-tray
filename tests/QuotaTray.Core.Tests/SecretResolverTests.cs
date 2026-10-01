using QuotaTray.Core;

namespace QuotaTray.Core.Tests;

public class SecretResolverTests
{
    private static SecretResolver Resolver(params (string Name, string? Value)[] env) =>
        new(name => env.FirstOrDefault(e => e.Name == name).Value);

    [Fact]
    public void Literal_api_key_wins_over_everything()
    {
        var resolver = Resolver(("CUSTOM", "from-custom"), ("DEFAULT", "from-default"));
        var config = new ProviderConfig { ApiKey = "  literal  ", ApiKeyEnv = "CUSTOM" };

        var result = resolver.Resolve(config, "DEFAULT");

        Assert.NotNull(result);
        Assert.Equal("literal", result.Value);
        Assert.Equal(SecretSource.ConfigLiteral, result.Source);
    }

    [Fact]
    public void Configured_env_var_beats_default_env_var()
    {
        var resolver = Resolver(("CUSTOM", "from-custom"), ("DEFAULT", "from-default"));
        var result = resolver.Resolve(new ProviderConfig { ApiKeyEnv = "CUSTOM" }, "DEFAULT");

        Assert.Equal("from-custom", result?.Value);
        Assert.Equal(SecretSource.ConfiguredEnvironmentVariable, result?.Source);
        Assert.Equal("CUSTOM", result?.EnvironmentVariable);
    }

    [Fact]
    public void Configured_env_var_that_is_unset_does_not_fall_through_to_default()
    {
        // If the user named a variable, silently using a different one would be a surprise.
        var resolver = Resolver(("DEFAULT", "from-default"));
        var result = resolver.Resolve(new ProviderConfig { ApiKeyEnv = "MISSING" }, "DEFAULT");

        Assert.Null(result);
    }

    [Fact]
    public void Default_env_var_used_when_nothing_configured()
    {
        var resolver = Resolver(("DEFAULT", "from-default"));
        var result = resolver.Resolve(new ProviderConfig(), "DEFAULT");

        Assert.Equal("from-default", result?.Value);
        Assert.Equal(SecretSource.DefaultEnvironmentVariable, result?.Source);
    }

    [Fact]
    public void Blank_values_count_as_unset()
    {
        var resolver = Resolver(("DEFAULT", "   "));
        Assert.Null(resolver.Resolve(new ProviderConfig { ApiKey = " " }, "DEFAULT"));
        Assert.Null(resolver.Resolve(new ProviderConfig(), null));
    }
}
