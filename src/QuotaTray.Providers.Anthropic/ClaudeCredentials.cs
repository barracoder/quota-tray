using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace QuotaTray.Providers.Anthropic;

/// <summary>The OAuth token Claude Code stores after <c>claude login</c>.</summary>
public sealed record ClaudeOAuthCredentials(
    string AccessToken,
    DateTimeOffset? ExpiresAt,
    string? SubscriptionType);

/// <summary>Somewhere Claude Code might have left its token. Read-only; never writes.</summary>
public interface IClaudeCredentialSource
{
    string Description { get; }
    Task<ClaudeOAuthCredentials?> ReadAsync(CancellationToken cancellationToken);
}

public static class ClaudeCredentialSources
{
    /// <summary>Keychain first on macOS, then the credentials file everywhere.</summary>
    public static IReadOnlyList<IClaudeCredentialSource> Default()
    {
        var sources = new List<IClaudeCredentialSource>();
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            sources.Add(new MacKeychainCredentialSource());
        }

        sources.Add(new CredentialsFileSource(CredentialsFileSource.DefaultPath()));
        return sources;
    }

    /// <summary>Parses the JSON blob Claude Code writes (same shape in Keychain and on disk).</summary>
    public static ClaudeOAuthCredentials? Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("claudeAiOauth", out var oauth) ||
            !oauth.TryGetProperty("accessToken", out var tokenEl) ||
            tokenEl.GetString() is not { Length: > 0 } token)
        {
            return null;
        }

        DateTimeOffset? expires = null;
        if (oauth.TryGetProperty("expiresAt", out var exp) && exp.ValueKind == JsonValueKind.Number)
        {
            expires = DateTimeOffset.FromUnixTimeMilliseconds(exp.GetInt64());
        }

        var subscription = oauth.TryGetProperty("subscriptionType", out var sub) ? sub.GetString() : null;
        return new ClaudeOAuthCredentials(token, expires, subscription);
    }
}

/// <summary>~/.claude/.credentials.json (or $CLAUDE_CONFIG_DIR/.credentials.json). Windows and Linux.</summary>
public sealed class CredentialsFileSource(string path) : IClaudeCredentialSource
{
    public string Description => path;

    public static string DefaultPath()
    {
        var configDir = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        if (string.IsNullOrWhiteSpace(configDir))
        {
            configDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
        }

        return Path.Combine(configDir, ".credentials.json");
    }

    public async Task<ClaudeOAuthCredentials?> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        return ClaudeCredentialSources.Parse(json);
    }
}

/// <summary>
/// macOS: Claude Code keeps its token in the login keychain under the service
/// "Claude Code-credentials". We shell out to /usr/bin/security rather than P/Invoke Security.framework.
/// </summary>
public sealed class MacKeychainCredentialSource : IClaudeCredentialSource
{
    public const string ServiceName = "Claude Code-credentials";

    public string Description => $"macOS Keychain item \"{ServiceName}\"";

    public async Task<ClaudeOAuthCredentials?> ReadAsync(CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo("/usr/bin/security")
        {
            ArgumentList = { "find-generic-password", "-s", ServiceName, "-w" },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        using var process = Process.Start(psi);
        if (process is null)
        {
            return null;
        }

        var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(stdout))
        {
            return null;
        }

        return ClaudeCredentialSources.Parse(stdout.Trim());
    }
}
