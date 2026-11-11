using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using CmdPalRealtimeTTS.Services;

namespace CmdPalRealtimeTTS.Pages;

internal sealed partial class RealtimeTtsPage : DynamicListPage, IDisposable
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
        runtime.SpeechFailed += ShowStatus;
        
        Title = "Realtime TTS";
        Name = "Speak";
        PlaceholderText = "Type text to speak...";
        Icon = new IconInfo("\uE8D2");
    }

    public override void UpdateSearchText(string oldSearch, string newSearch)
    {
        _deltas.Update(newSearch);
    }

    public override IListItem[] GetItems()
    {
        var enterEnabled = _runtime.GetConfig().DeltaOnEnter;

        return
        [
            new ListItem(new SpeakNowCommand(_deltas, this, enterEnabled))
            {
                Title = enterEnabled ? "Speak now" : "Enter rule disabled",
                Subtitle = !enterEnabled
                    ? "Enable the Enter rule in Settings to speak on Enter"
                    : "Speak the pending text delta",
            },

            new ListItem(new SaveAudioCommand(_runtime, this))
            {
                Title = "Save audio",
                Subtitle = string.IsNullOrWhiteSpace(SearchText) ? "Type some text first" : "Save the current text as an audio file",
            },

            new ListItem(_settingsPage)
            {
                Title = "Settings",
                Subtitle = "Voices, delta rules, audio and API",
            },

            new ListItem(new NoOpCommand())
            {
                Title = "",
                Subtitle = $"{_status}",
            },
        ];
    }

    public void ShowStatus(string message)
    {
        _status = message;
        RaiseItemsChanged();
    }

    public void Dispose()
    {
        _runtime.SpeechFailed -= ShowStatus;
        _deltas.Dispose();
    }

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

    public SaveAudioCommand(TtsRuntime runtime, RealtimeTtsPage page)
    {
        _runtime = runtime;
        _page = page;
    }

    public override string Name => "Save audio";
    public override IconInfo Icon => new("\uE74E");

    public override CommandResult Invoke()
    {
        var text = _page.SearchText;
        if (string.IsNullOrWhiteSpace(text))
        {
            _page.ShowStatus("Type some text before saving audio.");
            return CommandResult.KeepOpen();
        }

        _page.ShowStatus("Generating audio file...");
        _ = SaveAsync(text);
        return CommandResult.KeepOpen();
    }

    private async Task SaveAsync(string text)
    {
        try
        {
            await _runtime.SaveAudioAsync(text).ConfigureAwait(false);
            _page.ShowStatus("Audio file saved successfully.");
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceError($"Could not save speech audio: {exception}");
            _page.ShowStatus($"Audio save failed: {exception.Message}");
        }
    }
}
