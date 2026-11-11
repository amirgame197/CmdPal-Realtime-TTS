using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using CmdPalRealtimeTTS.Pages;
using CmdPalRealtimeTTS.Services;
using Windows.Storage;
using Windows.Storage.Streams;

namespace CmdPalRealtimeTTS;

internal sealed partial class RealtimeTtsCommandsProvider : CommandProvider, IDisposable
{
    private readonly ICommandItem[] _commands;
    private readonly RealtimeTtsPage _page;

    public RealtimeTtsCommandsProvider(TtsRuntime runtime)
    {
        DisplayName = "Realtime TTS";
        var settingsPage = new SettingsPage(runtime);
        var iconFile = StorageFile.GetFileFromApplicationUriAsync(
            new Uri("ms-appx:///Assets/Square44x44Logo.png"))
            .AsTask()
            .GetAwaiter()
            .GetResult();
        var icon = new IconInfo(new IconData(RandomAccessStreamReference.CreateFromFile(iconFile)));
        _page = new RealtimeTtsPage(runtime, settingsPage, icon);
        _commands =
        [
            new CommandItem(_page)
            {
                Title = DisplayName,
                Icon = icon,
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
