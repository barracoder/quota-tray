using System.Diagnostics;

namespace QuotaTray.Providers.GitHub;

/// <summary>Somewhere a GitHub token might already exist on the machine. Read-only.</summary>
public interface IGitHubTokenSource
{
    string Description { get; }
    Task<string?> ReadAsync(CancellationToken cancellationToken);
}

public static class GitHubTokenSources
{
    /// <summary>GH_TOKEN (the gh CLI's own override), then whatever <c>gh auth token</c> prints.</summary>
    public static IReadOnlyList<IGitHubTokenSource> Default() =>
    [
        new EnvironmentTokenSource("GH_TOKEN"),
        new GhCliTokenSource(),
    ];
}

public sealed class EnvironmentTokenSource(string variable, Func<string, string?>? getEnvironmentVariable = null) : IGitHubTokenSource
{
    private readonly Func<string, string?> _get = getEnvironmentVariable ?? Environment.GetEnvironmentVariable;

    public string Description => $"${variable}";

    public Task<string?> ReadAsync(CancellationToken cancellationToken)
    {
        var value = _get(variable);
        return Task.FromResult(string.IsNullOrWhiteSpace(value) ? null : value.Trim());
    }
}

/// <summary>
/// Runs <c>gh auth token</c>; returns null when gh is missing or nobody is logged in.
/// Apps launched from Finder or Login Items get a minimal PATH, so the usual install
/// locations are probed when the bare name is not found.
/// </summary>
public sealed class GhCliTokenSource(string executable = "gh") : IGitHubTokenSource
{
    private static readonly string[] WellKnownPaths =
    [
        "/opt/homebrew/bin/gh",
        "/usr/local/bin/gh",
        "/home/linuxbrew/.linuxbrew/bin/gh",
        "/usr/bin/gh",
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "GitHub CLI", "gh.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "GitHub CLI", "gh.exe"),
    ];

    public string Description => $"`{executable} auth token`";

    public async Task<string?> ReadAsync(CancellationToken cancellationToken)
    {
        foreach (var candidate in Candidates())
        {
            var token = await TryRunAsync(candidate, cancellationToken).ConfigureAwait(false);
            if (token is not null)
            {
                return token;
            }
        }

        return null;
    }

    private IEnumerable<string> Candidates()
    {
        yield return executable;
        if (executable != "gh")
        {
            yield break;
        }

        foreach (var path in WellKnownPaths.Where(File.Exists))
        {
            yield return path;
        }
    }

    private static async Task<string?> TryRunAsync(string executable, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo(executable)
        {
            ArgumentList = { "auth", "token" },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        Process? process;
        try
        {
            process = Process.Start(psi);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null; // gh not installed
        }

        if (process is null)
        {
            return null;
        }

        using (process)
        {
            var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return process.ExitCode == 0 && !string.IsNullOrWhiteSpace(stdout) ? stdout.Trim() : null;
        }
    }
}
