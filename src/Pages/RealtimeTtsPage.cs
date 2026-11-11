using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using CmdPalRealtimeTTS.Services;

namespace CmdPalRealtimeTTS.Pages;

internal sealed partial class RealtimeTtsPage : ListPage, IDisposable
{
    private readonly TtsRuntime _runtime;
    private readonly SettingsPage _settingsPage;
    private readonly DeltaGenerator _deltas;
    private string _status = "Type to speak.";

    public RealtimeTtsPage(TtsRuntime runtime, SettingsPage settingsPage)
    {
        _runtime = runtime;
        _settingsPage = settingsPage;
        _deltas = new DeltaGenerator(
            runtime.GetConfig,
            text => { runtime.EnqueueLiveDelta(text); },
            runtime.AbortCurrentAfterWord
        );
        
        Title = "Realtime TTS";
        Name = "Speak";
        PlaceholderText = "Type text to speak...";
        Icon = new IconInfo("\uE8D2");
    }

    public override string SearchText
    {
        get => base.SearchText;
        set
        {
            base.SearchText = value;
            _deltas.Update(value);
        }
    }

    public override IListItem[] GetItems()
    {
        var text = SearchText;
        var enterEnabled = _runtime.GetConfig().DeltaOnEnter;
        return
        [
            new ListItem(new SpeakNowCommand(_deltas, this, enterEnabled))
            {
                Title = enterEnabled ? "Speak now" : "Enter rule disabled",
                Subtitle = !enterEnabled
                    ? "Enable the Enter rule in Settings to speak on Enter"
                    : text.Length == 0 ? "Speak the pending text delta" : $"Speak: {text}",
            },

            new ListItem(new SaveAudioCommand(_runtime, this, text))
            {
                Title = "Save audio",
                Subtitle = text.Length == 0 ? "Type some text first" : $"Save audio for: {text}",
            },

            new ListItem(new OpenSettingsCommand(_settingsPage))
            {
                Title = "Settings",
                Subtitle = text.Length == 0 ? "Voices, delta rules, audio and API" : $"Settings: {text}",
            },

            new ListItem(new NoOpCommand())
            {
                Title = "Status",
                Subtitle = text.Length == 0
                    ? $"{_status} API: {_runtime.ApiStatus}"
                    : $"{text} {_status} API: {_runtime.ApiStatus}",
            },
        ];
    }

    public void ShowStatus(string message)
    {
        _status = message;
        RaiseItemsChanged();
    }

    public void Dispose() => _deltas.Dispose();

}

internal sealed partial class SpeakNowCommand(DeltaGenerator deltas, RealtimeTtsPage page, bool enabled) : InvokableCommand
{
    private readonly DeltaGenerator _deltas = deltas;
    private readonly RealtimeTtsPage _page = page;
    private readonly bool _enabled = enabled;

    public override string Name => "Speak now";
    public override IconInfo Icon => new("\uE768");

    public override CommandResult Invoke()
    {
        if (!_enabled)
        {
            _page.ShowStatus("Enter generation rule is disabled in Settings.");
            return CommandResult.KeepOpen();
        }

        _page.ShowStatus(_deltas.GeneratePending() ? "Text added to the speech queue." : "There is no new text to speak.");
        return CommandResult.KeepOpen();
    }
}

internal sealed partial class SaveAudioCommand : InvokableCommand
{
    private readonly TtsRuntime _runtime;
    private readonly RealtimeTtsPage _page;
    private readonly string _text;

    public SaveAudioCommand(TtsRuntime runtime, RealtimeTtsPage page, string text)
    {
        _runtime = runtime;
        _page = page;
        _text = text;
    }

    public override string Name => "Save audio";
    public override IconInfo Icon => new("\uE74E");

    public override CommandResult Invoke()
    {
        if (string.IsNullOrWhiteSpace(_text))
        {
            _page.ShowStatus("Type some text before saving audio.");
            return CommandResult.KeepOpen();
        }

        _page.ShowStatus("Generating audio file...");
        _ = SaveAsync();
        return CommandResult.KeepOpen();
    }

    private async Task SaveAsync()
    {
        try
        {
            await _runtime.SaveAudioAsync(_text).ConfigureAwait(false);
            _page.ShowStatus("Audio file saved successfully.");
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceError($"Could not save speech audio: {exception}");
            _page.ShowStatus($"Audio save failed: {exception.Message}");
        }
    }
}

internal sealed partial class OpenSettingsCommand : InvokableCommand
{
    private readonly SettingsPage _page;

    public OpenSettingsCommand(SettingsPage page)
    {
        _page = page;
    }

    public override string Name => "Settings";
    public override IconInfo Icon => new("\uE713");

    public override CommandResult Invoke() =>
        CommandResult.GoToPage(new GoToPageArgs { PageId = _page.Id });
}
