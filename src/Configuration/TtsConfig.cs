using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace CmdPalRealtimeTTS.Configuration;

public sealed class TtsConfig
{
    public string VoiceName { get; set; } = "auto";
    public bool DeltaOnEnter { get; set; } = true;
    public bool DeltaOnPunctuation { get; set; } = true;
    public bool DeltaOnThreeWords { get; set; } = true;
    public bool DeltaOnInactivity { get; set; } = true;
    public bool AbortOnBackspace { get; set; } = true;
    public bool ApiEnabled { get; set; } = true;
    public string ApiAddress { get; set; } = "127.0.0.1";
    public int ApiPort { get; set; } = 3219;
    public string PlaybackDeviceId { get; set; } = "auto";
    public string SaveDirectory { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
    public string SaveFormat { get; set; } = "wav";
    public int SpeechRate { get; set; }
    public int SpeechVolume { get; set; } = 100;

    public void Validate()
    {
        // ? Basic config value validation

        if (!IPAddress.TryParse(ApiAddress, out _))
            throw new InvalidDataException("API address must be an IPv4 or IPv6 address.");

        if (ApiPort is < 1 or > 65535)
            throw new InvalidDataException("API port must be between 1 and 65535.");

        if (string.IsNullOrWhiteSpace(SaveDirectory))
            throw new InvalidDataException("Save folder cannot be empty.");

        if (SaveFormat is not ("wav" or "mp3"))
            throw new InvalidDataException("Save format must be WAV or MP3.");

        if (SpeechRate is < -10 or > 10)
            throw new InvalidDataException("Speech rate must be between -10 and 10.");

        if (SpeechVolume is < 0 or > 100)
            throw new InvalidDataException("Speech volume must be between 0 and 100.");
    }
}

public sealed class ConfigStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly Lock _gate = new();

    public string ConfigPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".config", "CmdPalRealtimeTTS", "config.json"
    );

    public TtsConfig Load()
    {
        lock (_gate)
        {
            if (!File.Exists(ConfigPath))
            {
                var defaults = new TtsConfig();
                defaults.Validate();
                SaveCore(defaults);
                return defaults;
            }

            var json = File.ReadAllText(ConfigPath);
            var config = JsonSerializer.Deserialize<TtsConfig>(json, Options)
                ?? throw new InvalidDataException($"The configuration file at {ConfigPath} is incorrect.");
            config.Validate();
            return config;
        }
    }

    public void Save(TtsConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        config.Validate();
        
        lock (_gate)
        {
            SaveCore(config);
        }
    }

    private void SaveCore(TtsConfig config)
    {
        var directory = Path.GetDirectoryName(ConfigPath)
            ?? throw new InvalidOperationException("The configuration path has no parent directory.");
        Directory.CreateDirectory(directory);
        
        // ? Prevent potential data loss by replacing the config file instantly
        var temporaryPath = ConfigPath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(config, Options));
        File.Move(temporaryPath, ConfigPath, overwrite: true);
    }

    public static TtsConfig Defaults() => new();
}
