using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.CommandPalette.Extensions;
using CmdPalRealtimeTTS.Services;

namespace CmdPalRealtimeTTS;

[Guid("B29325E9-2896-4D47-9200-3642CD845A31")]
public sealed partial class RealtimeTtsExtension : IExtension, IDisposable
{
    private readonly ManualResetEvent _disposedEvent;
    private readonly TtsRuntime _runtime = new();
    private readonly RealtimeTtsCommandsProvider _provider;

    public RealtimeTtsExtension(ManualResetEvent disposedEvent)
    {
        _disposedEvent = disposedEvent;
        _provider = new RealtimeTtsCommandsProvider(_runtime);
    }

    public object? GetProvider(ProviderType providerType) =>
        providerType == ProviderType.Commands ? _provider : null;

    public void Dispose()
    {
        _provider.Dispose();
        _runtime.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _disposedEvent.Set();
    }
}
