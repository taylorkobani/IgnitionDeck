using System.Text.Json;
using System.Text.Json.Nodes;

namespace IgnitionDeck.Core;

/// <summary>Machine-wide IgnitionDeck settings, separate from worker configuration.</summary>
public sealed class ApplicationSettings
{
    public const int DefaultPollingIntervalSeconds = 5;
    public const int MinPollingIntervalSeconds = 1;
    public const int MaxPollingIntervalSeconds = 3600;
    public static string DefaultFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "IgnitionDeck", "settings.json");

    public string FilePath { get; }
    public string LaunchPadRoot { get; private set; } = @"C:\LaunchPad";
    public string BootPath { get; private set; } = string.Empty;
    public int PollingIntervalSeconds { get; private set; } = DefaultPollingIntervalSeconds;

    private ApplicationSettings(string filePath) => FilePath = Path.GetFullPath(filePath);

    public static ApplicationSettings Load(string? filePath = null)
    {
        var settings = new ApplicationSettings(filePath ?? DefaultFilePath);
        var root = settings.Read();
        if (root["LaunchPadRoot"] is JsonValue rootValue)
        {
            if (!rootValue.TryGetValue<string>(out var path) || string.IsNullOrWhiteSpace(path))
                throw new InvalidDataException("IgnitionDeck LaunchPad root must be a nonempty path.");
            settings.LaunchPadRoot = Path.GetFullPath(path);
        }
        if (root["BootPath"] is JsonValue bootValue)
            settings.BootPath = bootValue.GetValue<string>();
        if (root["PollingIntervalSeconds"] is JsonValue value
            && value.TryGetValue<int>(out var seconds)
            && seconds >= MinPollingIntervalSeconds && seconds <= MaxPollingIntervalSeconds)
            settings.PollingIntervalSeconds = seconds;
        return settings;
    }

    public void SaveLaunchPadRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("LaunchPad folder is required.");
        var normalized = Path.GetFullPath(path);
        Save("LaunchPadRoot", JsonValue.Create(normalized)!);
        LaunchPadRoot = normalized;
    }

    public void SaveBootPath(string path)
    {
        var normalized = Path.GetFullPath(path);
        Save("BootPath", JsonValue.Create(normalized)!);
        BootPath = normalized;
    }

    public void SavePollingInterval(int seconds)
    {
        if (seconds < MinPollingIntervalSeconds || seconds > MaxPollingIntervalSeconds)
            throw new ArgumentOutOfRangeException(nameof(seconds), "Polling interval must be between 1 and 3,600 seconds.");
        Save("PollingIntervalSeconds", JsonValue.Create(seconds)!);
        PollingIntervalSeconds = seconds;
    }

    public static void ValidateRecoveryFile(string filePath)
    {
        if (!File.Exists(filePath)) throw new FileNotFoundException("Recovery requires an existing IgnitionDeck settings file.", filePath);
        var root = JsonNode.Parse(File.ReadAllText(filePath)) as JsonObject
            ?? throw new InvalidDataException("IgnitionDeck settings must contain a JSON object.");
        if (root["LaunchPadRoot"] is not JsonValue value
            || !value.TryGetValue<string>(out var path) || string.IsNullOrWhiteSpace(path))
            throw new InvalidDataException("Recovery requires an explicit LaunchPadRoot in IgnitionDeck settings; refusing to use the default root.");
        _ = Path.GetFullPath(path);
    }

    private void Save(string key, JsonNode value)
    {
        var root = Read();
        root[key] = value;
        // Every saved app file contains an explicit root for headless recovery.
        if (root["LaunchPadRoot"] is null) root["LaunchPadRoot"] = LaunchPadRoot;
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, FilePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private JsonObject Read() => File.Exists(FilePath)
        ? JsonNode.Parse(File.ReadAllText(FilePath)) as JsonObject
            ?? throw new InvalidDataException("IgnitionDeck settings must contain a JSON object.")
        : new JsonObject();
}
