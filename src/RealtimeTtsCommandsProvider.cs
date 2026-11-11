using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using CmdPalRealtimeTTS.Pages;
using CmdPalRealtimeTTS.Services;

namespace CmdPalRealtimeTTS;

internal sealed partial class RealtimeTtsCommandsProvider : CommandProvider, IDisposable
{
    private readonly ICommandItem[] _commands;
    private readonly RealtimeTtsPage _page;

    public RealtimeTtsCommandsProvider(TtsRuntime runtime)
    {
        DisplayName = "Realtime TTS";
        var settingsPage = new SettingsPage(runtime);
        _page = new RealtimeTtsPage(runtime, settingsPage);
        _commands =
        [
            new CommandItem(_page)
            {
                Title = DisplayName,
                MoreCommands = [new CommandContextItem(settingsPage)],
            },
        ];
    }

    public override ICommandItem[] TopLevelCommands() => _commands;

    public override void Dispose()
    {
        _page.Dispose();
        base.Dispose();
    }
}
