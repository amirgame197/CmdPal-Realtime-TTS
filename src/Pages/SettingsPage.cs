using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using CmdPalRealtimeTTS.Configuration;
using CmdPalRealtimeTTS.Services;

namespace CmdPalRealtimeTTS.Pages;

internal sealed partial class SettingsPage : ContentPage
{
    private readonly TtsRuntime _runtime;

    public SettingsPage(TtsRuntime runtime)
    {
        _runtime = runtime;
        Id = "RealtimeTtsSettings";
        Name = "Settings";
        Icon = new IconInfo("\uE713");
    }

    public override IContent[] GetContent() => [new TtsSettingsForm(_runtime)];
}

internal sealed partial class TtsSettingsForm : FormContent
{
    private readonly TtsRuntime _runtime;
    private readonly TtsConfig _initialConfig;

    public TtsSettingsForm(TtsRuntime runtime)
    {
        _runtime = runtime;
        _initialConfig = runtime.GetConfig();
        Render(_initialConfig, null);
    }

    public override ICommandResult SubmitForm(string inputs, string data)
    {
        try
        {
            var inputValues = ParseValues(inputs);
            var actionValues = ParseValues(data);
            var action = OptionalString(inputValues, "action") ?? OptionalString(actionValues, "action");
            return action switch
            {
                "cancel" => Cancel(inputValues),
                "reset" => CommandResult.Confirm(new ConfirmationArgs
                {
                    Title = "Reset settings?",
                    Description = "This replaces every setting with its default value.",
                    IsPrimaryCommandCritical = true,
                    PrimaryCommand = new ResetSettingsCommand(_runtime),
                }),
                "save" => Save(inputValues),
                _ => CommandResult.ShowToast("Settings action was not recognized."),
            };
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceError($"Settings form submission failed: {exception}");
            return CommandResult.ShowToast($"Could not submit settings: {exception.Message}");
        }
    }

    private ICommandResult Cancel(JsonObject values)
    {
        var editedConfig = ParseConfig(values, Copy(_initialConfig));
        if (!HasChanges(_initialConfig, editedConfig))
        {
            return CommandResult.GoBack();
        }

        return CommandResult.Confirm(new ConfirmationArgs
        {
            Title = "Discard unsaved changes?",
            Description = "Your settings have changed. Discard these changes?",
            IsPrimaryCommandCritical = true,
            PrimaryCommand = new DiscardSettingsCommand(),
        });
    }

    private ICommandResult Save(JsonObject values)
    {
        try
        {
            var config = ParseConfig(values, Copy(_initialConfig));
            config.Validate();
            ValidateSelections(config);
            _runtime.SaveConfig(config);

            CommandResult.ShowToast("TTS settings saved");
            return CommandResult.GoBack();
        }

        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceError($"Could not save Realtime TTS settings: {exception}");
            return CommandResult.ShowToast($"Could not save settings: {exception.Message}");
        }
    }

    private static bool HasChanges(TtsConfig original, TtsConfig edited) =>
        !string.Equals(original.VoiceName, edited.VoiceName, StringComparison.Ordinal) ||
        original.DeltaOnEnter != edited.DeltaOnEnter ||
        original.DeltaOnPunctuation != edited.DeltaOnPunctuation ||
        original.DeltaOnThreeWords != edited.DeltaOnThreeWords ||
        original.DeltaOnInactivity != edited.DeltaOnInactivity ||
        original.AbortOnBackspace != edited.AbortOnBackspace ||
        original.ApiEnabled != edited.ApiEnabled ||
        !string.Equals(original.ApiAddress, edited.ApiAddress, StringComparison.Ordinal) ||
        original.ApiPort != edited.ApiPort ||
        !string.Equals(original.PlaybackDeviceId, edited.PlaybackDeviceId, StringComparison.Ordinal) ||
        !string.Equals(original.SaveDirectory, edited.SaveDirectory, StringComparison.Ordinal) ||
        !string.Equals(original.SaveFormat, edited.SaveFormat, StringComparison.Ordinal) ||
        original.SpeechRate != edited.SpeechRate ||
        original.SpeechVolume != edited.SpeechVolume;

    private static TtsConfig Copy(TtsConfig config) => new()
    {
        VoiceName = config.VoiceName,
        DeltaOnEnter = config.DeltaOnEnter,
        DeltaOnPunctuation = config.DeltaOnPunctuation,
        DeltaOnThreeWords = config.DeltaOnThreeWords,
        DeltaOnInactivity = config.DeltaOnInactivity,
        AbortOnBackspace = config.AbortOnBackspace,
        ApiEnabled = config.ApiEnabled,
        ApiAddress = config.ApiAddress,
        ApiPort = config.ApiPort,
        PlaybackDeviceId = config.PlaybackDeviceId,
        SaveDirectory = config.SaveDirectory,
        SaveFormat = config.SaveFormat,
        SpeechRate = config.SpeechRate,
        SpeechVolume = config.SpeechVolume,
    };

    private static TtsConfig ParseConfig(JsonObject values, TtsConfig config)
    {
        config.VoiceName = ReadString(values, "voiceName", config.VoiceName);
        config.DeltaOnEnter = ReadBool(values, "deltaOnEnter", config.DeltaOnEnter);
        config.DeltaOnPunctuation = ReadBool(values, "deltaOnPunctuation", config.DeltaOnPunctuation);
        config.DeltaOnThreeWords = ReadBool(values, "deltaOnThreeWords", config.DeltaOnThreeWords);
        config.DeltaOnInactivity = ReadBool(values, "deltaOnInactivity", config.DeltaOnInactivity);
        config.AbortOnBackspace = ReadBool(values, "abortOnBackspace", config.AbortOnBackspace);
        config.ApiEnabled = ReadBool(values, "apiEnabled", config.ApiEnabled);
        config.ApiAddress = ReadString(values, "apiAddress", config.ApiAddress);
        config.ApiPort = ReadInt(values, "apiPort", config.ApiPort);
        config.PlaybackDeviceId = ReadString(values, "playbackDeviceId", config.PlaybackDeviceId);
        config.SaveDirectory = ReadString(values, "saveDirectory", config.SaveDirectory);
        config.SaveFormat = ReadString(values, "saveFormat", config.SaveFormat).ToLowerInvariant();
        config.SpeechRate = ReadInt(values, "speechRate", config.SpeechRate);
        config.SpeechVolume = ReadInt(values, "speechVolume", config.SpeechVolume);
        return config;
    }

    private static void ValidateSelections(TtsConfig config)
    {
        var voices = TtsRuntime.GetVoiceNames();
        if (!string.Equals(config.VoiceName, "auto", StringComparison.OrdinalIgnoreCase) &&
            !voices.Contains(config.VoiceName, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Choose an installed Windows voice.");
        }

        var devices = TtsRuntime.GetPlaybackDevices();
        if (!string.Equals(config.PlaybackDeviceId, "auto", StringComparison.OrdinalIgnoreCase) &&
            !devices.Any(device => device.Id == config.PlaybackDeviceId))
        {
            throw new InvalidDataException("Choose an active Windows playback device.");
        }
    }

    private void Render(TtsConfig config, string? error)
    {
        var voices = new[] { new { title = "Auto (Windows default)", value = "auto" } }
            .Concat(TtsRuntime.GetVoiceNames().Select(name => new { title = name, value = name }))
            .ToArray();
        
        var devices = new[] { new { title = "Auto (current active device)", value = "auto" } }
            .Concat(TtsRuntime.GetPlaybackDevices().Select(device => new { title = device.Name, value = device.Id }))
            .ToArray();

        var body = new List<object>
        {
            new { type = "TextBlock", text = $"Configuration file: {_runtime.ConfigPath}", wrap = true },
            InputChoice("Windows voice", "voiceName", voices, config.VoiceName),
            InputToggle("Generate on Enter", "deltaOnEnter", config.DeltaOnEnter),
            InputToggle("Generate on punctuation or symbols", "deltaOnPunctuation", config.DeltaOnPunctuation),
            InputToggle("Generate at 3 words and 15 characters", "deltaOnThreeWords", config.DeltaOnThreeWords),
            InputToggle("Generate after 1.5 seconds of inactivity", "deltaOnInactivity", config.DeltaOnInactivity),
            InputToggle("Abort current delta on Backspace", "abortOnBackspace", config.AbortOnBackspace),
            InputToggle("Enable local API", "apiEnabled", config.ApiEnabled),
            InputText("API bind address (IPv4 or IPv6)", "apiAddress", config.ApiAddress),
            InputNumber("API port", "apiPort", config.ApiPort, 1, 65535),
            InputChoice("Playback device", "playbackDeviceId", devices, config.PlaybackDeviceId),
            InputText("Save folder", "saveDirectory", config.SaveDirectory),
            InputChoice("Save format", "saveFormat",
                new[] { new { title = "WAV", value = "wav" }, new { title = "MP3", value = "mp3" } },
                config.SaveFormat),
            InputNumber("Speech rate (-10 to 10)", "speechRate", config.SpeechRate, -10, 10),
            InputNumber("Speech volume (0 to 100)", "speechVolume", config.SpeechVolume, 0, 100),
        };

        if (!string.IsNullOrWhiteSpace(error))
        {
            body.Add(new { type = "TextBlock", text = error, color = "Attention", wrap = true });
        }

        TemplateJson = JsonSerializer.Serialize(new
        {
            type = "AdaptiveCard",
            version = "1.5",
            body,
            actions = new object[]
            {
                new { type = "Action.Submit", title = "Save", data = new { action = "save" } },
                new { type = "Action.Submit", title = "Cancel", data = new { action = "cancel" } },
                new { type = "Action.Submit", title = "Reset to default", data = new { action = "reset" } },
            },
        });
    }

    private static object InputChoice(string label, string id, object choices, string value) =>
        new { type = "Input.ChoiceSet", label, id, choices, value, style = "compact", isRequired = true };

    private static object InputToggle(string label, string id, bool value) =>
        new { type = "Input.Toggle", title = label, id, value = value ? "true" : "false", valueOn = "true", valueOff = "false" };

    private static object InputText(string label, string id, string value) =>
        new { type = "Input.Text", label, id, value, isRequired = true };

    private static object InputNumber(string label, string id, int value, int minimum, int maximum) =>
        new { type = "Input.Number", label, id, value, min = minimum, max = maximum, isRequired = true };

    private static JsonObject ParseValues(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        return JsonNode.Parse(json) as JsonObject
            ?? throw new InvalidDataException("Form submission data must be a JSON object.");
    }

    private static string? OptionalString(JsonObject values, string key)
    {
        var value = values[key];
        return value is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var text) ? text : null;
    }

    private static string ReadString(JsonObject values, string key, string fallback) =>
        OptionalString(values, key) ?? fallback;

    private static bool ReadBool(JsonObject values, string key, bool fallback)
    {
        var value = values[key];
        if (value is null)
        {
            return fallback;
        }

        return value.ToString() switch
        {
            "true" => true,
            "false" => false,
            _ => fallback,
        };
    }

    private static int ReadInt(JsonObject values, string key, int fallback)
    {
        var value = values[key];
        if (value is null)
        {
            return fallback;
        }

        if (value is JsonValue jsonValue && jsonValue.TryGetValue<int>(out var number))
        {
            return number;
        }

        return int.TryParse(value.ToString(), out number)
            ? number
            : fallback;
    }
}

internal sealed partial class ResetSettingsCommand : InvokableCommand
{
    private readonly TtsRuntime _runtime;

    public ResetSettingsCommand(TtsRuntime runtime)
    {
        _runtime = runtime;
    }

    public override string Name => "Reset settings";
    public override IconInfo Icon => new("\uE777");

    public override CommandResult Invoke()
    {
        try
        {
            _runtime.ResetConfig();
            CommandResult.ShowToast("TTS settings reset to defaults");
            return CommandResult.GoBack();
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceError($"Could not reset TTS settings: {exception}");
            return CommandResult.ShowToast($"Could not reset settings: {exception.Message}");
        }
    }
}

internal sealed partial class DiscardSettingsCommand : InvokableCommand
{
    public override string Name => "Discard changes";
    public override IconInfo Icon => new("\uE777");

    public override CommandResult Invoke() => CommandResult.GoBack();
}
