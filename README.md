# quota-tray

A small gauge in the macOS menu bar / Windows system tray showing how much of your AI quota is **left**.

The icon is a ring: the filled arc is the headroom remaining, the number in the middle is the percent
left, and the colour (blue / amber / red / grey) is how worried you should be. Click it for a breakdown
per provider and per window (5-hour session, weekly, per-model weekly, month-to-date spend), when each
resets, and the usual Refresh / Open config / Quit.

<p align="center"><img src="docs/images/menu-macos.png" width="436" alt="quota-tray in the macOS menu bar: ring icon, and the open menu listing Claude session, weekly and per-model limits with reset times"></p>

Out of the box it reads your **Claude Pro/Max** limits using the login Claude Code already has
on the machine — no setup. Add an Admin API key and it will also track **Claude Developer Platform**
spend against a monthly budget. Other APIs plug in as providers (see below).

## Install

Grab a zip for your platform from [Releases](https://github.com/barracoder/quota-tray/releases).

| Platform | Steps |
| --- | --- |
| macOS (Apple Silicon / Intel) | Unzip, drag `QuotaTray.app` to Applications. It is ad-hoc signed, so the first launch is right-click → **Open**. Add to *System Settings → General → Login Items* to start at login. |
| Windows (x64 / ARM64) | Unzip anywhere, run `QuotaTray.exe`. To start at login, drop a shortcut in `shell:startup`. |

No installer, no admin rights, nothing written outside your user profile.

## Configuration

The config file is created on first run:

| OS | Path |
| --- | --- |
| macOS | `~/Library/Application Support/quota-tray/config.json` |
| Windows | `%APPDATA%\quota-tray\config.json` |
| Linux | `~/.config/quota-tray/config.json` |

Override the location with the `QUOTA_TRAY_CONFIG` environment variable. Comments and trailing commas are allowed.
**Open config file** in the menu opens it; **Reload config** applies changes without restarting.

```jsonc
{
  "pollIntervalSeconds": 60,          // minimum 10
  "warnAtRemainingPercent": 25,       // ring turns amber at or below this
  "criticalAtRemainingPercent": 10,   // ring turns red at or below this
  "providers": [
    {
      "type": "claude-subscription",
      "name": "Claude Max"
      // no key needed: uses Claude Code's login (Keychain on macOS, ~/.claude/.credentials.json elsewhere)
    },
    {
      "type": "anthropic-cost",
      "name": "Console",
      "apiKeyEnv": "ANTHROPIC_ADMIN_KEY",   // name of an env var holding the key …
      // "apiKey": "sk-ant-admin01-…",     // … or the key itself (not recommended)
      "monthlyBudgetUsd": 200
    }
  ]
}
```

### Provider fields common to every type

| Field | Meaning |
| --- | --- |
| `type` | Which provider. Unknown types show up as an error in the menu rather than being ignored. |
| `name` | Label in the menu. Defaults to `type`. |
| `enabled` | Default `true`. |
| `apiKey` | The secret itself. Wins over everything else. |
| `apiKeyEnv` | Name of an environment variable holding the secret. If you set this and the variable is empty, the provider errors rather than silently falling back. |
| *(neither)* | The provider's default variable is consulted (table below). |

The icon shows the single most-constrained window across all providers. The tooltip and menu show everything.

## Providers

| `type` | What it measures | Credential | Default env var |
| --- | --- | --- | --- |
| `claude-subscription` | Claude Pro/Max/Team plan limits: 5-hour session, weekly (all models), weekly per model, extra-usage credits if enabled | Claude Code's OAuth token, read from the macOS Keychain item `Claude Code-credentials` or `~/.claude/.credentials.json` (`CLAUDE_CONFIG_DIR` honoured). Run `claude` once and sign in. | `CLAUDE_CODE_OAUTH_TOKEN` |
| `anthropic-cost` | Claude Developer Platform (Console) spend this calendar month vs. `monthlyBudgetUsd`, via the [Usage & Cost Admin API](https://platform.claude.com/docs/en/manage-claude/usage-cost-api) | Admin API key (`sk-ant-admin…`). Workspace-scoped keys do not work. Individual (non-organisation) Console accounts have no Admin API. | `ANTHROPIC_ADMIN_KEY` |

Both accept an optional `baseUrl` setting for proxies.

### Caveats worth knowing

- **The subscription endpoint is not a documented public API.** It is the one Claude Code's `/usage` command uses.
  Anthropic can change or remove it at any time; when they do, the provider will show an error, not wrong numbers.
- **Token handling is read-only.** quota-tray never writes to the Keychain or credentials file and never refreshes
  the token itself (that could invalidate Claude Code's session). When the token expires, the menu tells you to run `claude`.
- **The cost provider lags.** Anthropic's cost report is updated periodically, not in real time.

## Adding a provider

Implement two small interfaces from `QuotaTray.Core` and register the factory in `AppHost`. Full walkthrough in
[docs/adding-a-provider.md](docs/adding-a-provider.md). The contract in one breath:

```csharp
public interface IUsageProviderFactory
{
    string Type { get; }                                   // config "type"
    string Description { get; }
    string? DefaultApiKeyEnvironmentVariable { get; }
    IUsageProvider Create(ProviderConfig config, ProviderContext context);
}

public interface IUsageProvider
{
    string Id { get; }
    string DisplayName { get; }
    Task<UsageSnapshot> FetchAsync(CancellationToken ct);  // throw on failure; the poller shows it
}
```

A `UsageSnapshot` is a list of `QuotaWindow(Label, UsedPercent, ResetsAt?, Detail?)`. That is the whole model;
the UI does not know or care what is behind a window.

## Development

Requires the .NET 10 SDK. Scripts exist in both bash and PowerShell and do the same thing.

```sh
scripts/build.sh            # restore, build, test
scripts/publish.sh          # self-contained build for this machine → artifacts/<rid>/ (+ .app on macOS)
scripts/publish.sh win-x64  # or any RID: win-arm64, osx-arm64, osx-x64
dotnet run --project src/QuotaTray.App
```

```powershell
scripts/build.ps1
scripts/publish.ps1 -Rid win-x64
```

Layout:

```
src/QuotaTray.Core                 contract, config, secret resolution, polling, severity
src/QuotaTray.Providers.Anthropic  claude-subscription, anthropic-cost
src/QuotaTray.App                  Avalonia tray app: icon renderer, menu, composition root
tests/                             xUnit; HTTP is faked, fixtures are real captured payloads
```

Two macOS rules learned the hard way, both documented in `TrayController`: never replace the tray icon's
`NativeMenu` instance after it is attached (Avalonia throws), and never churn native-backed objects
(menu items, icons) per refresh — they are released from the finalizer thread and AppKit crashes.
Menu items are a fixed pool updated in place; icons are cached.

CI builds, tests and publishes win-x64, win-arm64, osx-arm64 and osx-x64 on every push; a `v*` tag creates a GitHub release with the zips attached.

## License

[MIT](LICENSE)
