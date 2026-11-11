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
        Name = "Settings";
        Icon = new IconInfo("\uE713");
    }

    public override IContent[] GetContent() => [new TtsSettingsForm(_runtime)];
}

internal sealed partial class TtsSettingsForm : FormContent
{
    private readonly TtsRuntime _runtime;

    public TtsSettingsForm(TtsRuntime runtime)
    {
        _runtime = runtime;
        Render(_runtime.GetConfig(), null);
    }

    public override ICommandResult SubmitForm(string inputs)
    {
        var values = JsonNode.Parse(inputs)?.AsObject()
            ?? throw new InvalidDataException("The settings form did not return valid data.");
        var action = ReadString(values, "action");

        return action switch
        {
            "cancel" => CommandResult.GoBack(),
            "reset" => CommandResult.Confirm(new ConfirmationArgs
            {
                Title = "Reset settings?",
                Description = "This replaces every setting with its default value.",
                IsPrimaryCommandCritical = true,
                PrimaryCommand = new ResetSettingsCommand(_runtime),
            }),
            "save" => Save(inputs),
            _ => CommandResult.KeepOpen(),
        };
    }

    private ICommandResult Save(string inputs)
    {
        try
        {
            var config = ParseConfig(inputs, _runtime.GetConfig());
            config.Validate();
            ValidateSelections(config);
            _runtime.SaveConfig(config);

            return ToastNotificationService.TryShow(
                "TTS settings saved",
                "Your settings have been applied and the API server restarted.")
                ? CommandResult.GoHome()
                : CommandResult.ShowToast("TTS settings saved.");
        }

        catch (Exception exception) when (exception is InvalidDataException or JsonException or FormatException or ArgumentException)
        {
            Render(_runtime.GetConfig(), exception.Message);
            return CommandResult.KeepOpen();
        }

        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceError($"Could not save Realtime TTS settings: {exception}");
            Render(_runtime.GetConfig(), $"Could not apply settings: {exception.Message}");
            return CommandResult.KeepOpen();
        }
    }

    private static TtsConfig ParseConfig(string inputs, TtsConfig config)
    {
        var values = JsonNode.Parse(inputs)?.AsObject()
            ?? throw new InvalidDataException("The settings form did not return valid data.");

        config.VoiceName = ReadString(values, "voiceName");
        config.DeltaOnEnter = ReadBool(values, "deltaOnEnter");
        config.DeltaOnPunctuation = ReadBool(values, "deltaOnPunctuation");
        config.DeltaOnThreeWords = ReadBool(values, "deltaOnThreeWords");
        config.DeltaOnInactivity = ReadBool(values, "deltaOnInactivity");
        config.AbortOnBackspace = ReadBool(values, "abortOnBackspace");
        config.ApiEnabled = ReadBool(values, "apiEnabled");
        config.ApiAddress = ReadString(values, "apiAddress");
        config.ApiPort = ReadInt(values, "apiPort");
        config.PlaybackDeviceId = ReadString(values, "playbackDeviceId");
        config.SaveDirectory = ReadString(values, "saveDirectory");
        config.SaveFormat = ReadString(values, "saveFormat").ToLowerInvariant();
        config.SpeechRate = ReadInt(values, "speechRate");
        config.SpeechVolume = ReadInt(values, "speechVolume");
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

    private static string ReadString(JsonObject values, string key) =>
        values[key]?.GetValue<string>() ?? throw new InvalidDataException($"Missing value for {key}.");

    private static bool ReadBool(JsonObject values, string key)
    {
        var value = values[key] ?? throw new InvalidDataException($"Missing value for {key}.");
        return value.ToString() switch
        {
            "true" => true,
            "false" => false,
            _ => throw new InvalidDataException($"Invalid boolean value for {key}."),
        };
    }

    private static int ReadInt(JsonObject values, string key)
    {
        var value = values[key] ?? throw new InvalidDataException($"Missing value for {key}.");
        if (value is JsonValue jsonValue && jsonValue.TryGetValue<int>(out var number))
        {
            return number;
        }

        return int.TryParse(value.ToString(), out number)
            ? number
            : throw new InvalidDataException($"Invalid numeric value for {key}.");
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
            return ToastNotificationService.TryShow(
                "Settings reset",
                "All settings were restored to their default values.")
                ? CommandResult.GoHome()
                : CommandResult.ShowToast("TTS settings reset to defaults.");
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceError($"Could not reset TTS settings: {exception}");
            return CommandResult.ShowToast($"Could not reset settings: {exception.Message}");
        }
    }
}
