using System.Text.Json;
using System.Text.Json.Serialization;

namespace QuotaTray.Core;

/// <summary>Top-level config file shape.</summary>
public sealed class AppConfig
{
    public int PollIntervalSeconds { get; set; } = 60;

    /// <summary>Icon turns amber when any window has at most this much headroom left.</summary>
    public double WarnAtRemainingPercent { get; set; } = 25;

    /// <summary>Icon turns red when any window has at most this much headroom left.</summary>
    public double CriticalAtRemainingPercent { get; set; } = 10;

    public List<ProviderConfig> Providers { get; set; } = [];

    public static AppConfig Default() => new()
    {
        Providers =
        [
            new ProviderConfig { Type = "claude-subscription", Name = "Claude" },
        ],
    };
}

/// <summary>
/// One provider entry. Known fields are typed; anything else lands in <see cref="Settings"/>
/// so providers can read their own options without the core knowing about them.
/// </summary>
public sealed class ProviderConfig
{
    public string Type { get; set; } = string.Empty;
    public string? Name { get; set; }
    public bool Enabled { get; set; } = true;

    /// <summary>Literal secret. Prefer <see cref="ApiKeyEnv"/>; this is here for people who insist.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Name of an environment variable holding the secret.</summary>
    public string? ApiKeyEnv { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Settings { get; set; }

    public string? GetString(string key) =>
        Settings is not null && Settings.TryGetValue(key, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;

    public double? GetDouble(string key) =>
        Settings is not null && Settings.TryGetValue(key, out var el) && el.ValueKind == JsonValueKind.Number
            ? el.GetDouble()
            : null;

    public bool? GetBool(string key) =>
        Settings is not null && Settings.TryGetValue(key, out var el) &&
        el.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? el.GetBoolean()
            : null;

    public string EffectiveName => string.IsNullOrWhiteSpace(Name) ? Type : Name;
}

/// <summary>Loads and saves <see cref="AppConfig"/> as JSON.</summary>
public sealed class ConfigStore
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public ConfigStore(string path)
    {
        Path = path;
    }

    public string Path { get; }

    /// <summary>
    /// %APPDATA%\quota-tray\config.json on Windows, ~/Library/Application Support/quota-tray/config.json
    /// on macOS, ~/.config/quota-tray/config.json on Linux.
    /// Override with QUOTA_TRAY_CONFIG.
    /// </summary>
    public static string DefaultPath()
    {
        var @override = Environment.GetEnvironmentVariable("QUOTA_TRAY_CONFIG");
        if (!string.IsNullOrWhiteSpace(@override))
        {
            return @override;
        }

        var baseDir = Environment.GetFolderPath(
            Environment.SpecialFolder.ApplicationData,
            Environment.SpecialFolderOption.DoNotVerify);
        return System.IO.Path.Combine(baseDir, "quota-tray", "config.json");
    }

    public AppConfig LoadOrCreate()
    {
        if (!File.Exists(Path))
        {
            var config = AppConfig.Default();
            Save(config);
            return config;
        }

        return Load();
    }

    public AppConfig Load()
    {
        using var stream = File.OpenRead(Path);
        return JsonSerializer.Deserialize<AppConfig>(stream, JsonOptions)
               ?? throw new InvalidDataException($"{Path} is empty or not a JSON object.");
    }

    public void Save(AppConfig config)
    {
        var dir = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        using var stream = File.Create(Path);
        JsonSerializer.Serialize(stream, config, JsonOptions);
    }
}
