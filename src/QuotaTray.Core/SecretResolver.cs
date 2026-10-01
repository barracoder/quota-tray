namespace QuotaTray.Core;

public enum SecretSource
{
    None,
    ConfigLiteral,
    ConfiguredEnvironmentVariable,
    DefaultEnvironmentVariable,
}

public sealed record SecretResolution(string Value, SecretSource Source, string? EnvironmentVariable);

public interface ISecretResolver
{
    /// <summary>
    /// Precedence: literal <c>apiKey</c> → env var named by <c>apiKeyEnv</c> → provider's default env var.
    /// Returns null when nothing is set. Blank values are treated as unset.
    /// </summary>
    SecretResolution? Resolve(ProviderConfig config, string? defaultEnvironmentVariable);
}

public sealed class SecretResolver : ISecretResolver
{
    private readonly Func<string, string?> _getEnvironmentVariable;

    public SecretResolver()
        : this(Environment.GetEnvironmentVariable)
    {
    }

    public SecretResolver(Func<string, string?> getEnvironmentVariable)
    {
        _getEnvironmentVariable = getEnvironmentVariable;
    }

    public SecretResolution? Resolve(ProviderConfig config, string? defaultEnvironmentVariable)
    {
        if (!string.IsNullOrWhiteSpace(config.ApiKey))
        {
            return new SecretResolution(config.ApiKey.Trim(), SecretSource.ConfigLiteral, null);
        }

        if (!string.IsNullOrWhiteSpace(config.ApiKeyEnv))
        {
            var value = _getEnvironmentVariable(config.ApiKeyEnv);
            return string.IsNullOrWhiteSpace(value)
                ? null
                : new SecretResolution(value.Trim(), SecretSource.ConfiguredEnvironmentVariable, config.ApiKeyEnv);
        }

        if (!string.IsNullOrWhiteSpace(defaultEnvironmentVariable))
        {
            var value = _getEnvironmentVariable(defaultEnvironmentVariable);
            return string.IsNullOrWhiteSpace(value)
                ? null
                : new SecretResolution(value.Trim(), SecretSource.DefaultEnvironmentVariable, defaultEnvironmentVariable);
        }

        return null;
    }
}
