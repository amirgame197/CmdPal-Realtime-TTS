using CmdPalRealtimeTTS.Configuration;

namespace CmdPalRealtimeTTS.Services;

public sealed class TtsRuntime : IAsyncDisposable
{
    private readonly Lock _configGate = new();
    private readonly ConfigStore _store;
    private readonly PlaybackQueue _queue;
    private readonly LocalApiServer _api;
    private TtsConfig _config;

    public string ApiStatus { get; private set; } = "API status unavailable";
    public string ConfigPath => _store.ConfigPath;
    public event Action<string>? SpeechFailed;

    public TtsRuntime()
    {
        _store = new ConfigStore();
        _config = _store.Load();
        _queue = new PlaybackQueue(GetConfig);
        _queue.ErrorOccurred += OnSpeechFailed;
        _api = new LocalApiServer(_queue);
        RestartApi(throwOnFailure: false);
    }

    public TtsConfig GetConfig()
    {
        lock (_configGate)
        {
            return Copy(_config);
        }
    }

    public static IReadOnlyList<string> GetVoiceNames() => NativeSpeechService.GetVoiceNames();
    public static IReadOnlyList<(string Id, string Name)> GetPlaybackDevices() => NativeSpeechService.GetPlaybackDevices();
    public bool Enqueue(string text) => _queue.Enqueue(text);
    public void AbortCurrent() => _queue.AbortCurrent();
    public void AbortCurrentAfterWord() => _queue.AbortCurrentAfterWord();
    public bool EnqueueLiveDelta(string text) => _queue.Enqueue(text, liveDelta: true);

    public Task SaveAudioAsync(string text, CancellationToken cancellationToken = default)
    {
        var config = GetConfig();
        return NativeSpeechService.SaveAsync(text, config, cancellationToken);
    }

    public void SaveConfig(TtsConfig config)
    {
        config.Validate();
        var oldConfig = GetConfig();
        _store.Save(config);
        
        lock (_configGate)
        {
            _config = Copy(config);
        }

        RestartApi(throwOnFailure: true);
        if (!string.Equals(oldConfig.VoiceName, config.VoiceName, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(config.VoiceName, "auto", StringComparison.OrdinalIgnoreCase))
        {
            _queue.Enqueue("You selected me as your voice");
        }
    }

    public void ResetConfig() => SaveConfig(ConfigStore.Defaults());

    private void RestartApi(bool throwOnFailure)
    {
        var config = GetConfig();
        try
        {
            _api.RestartAsync(config).GetAwaiter().GetResult();
            ApiStatus = config.ApiEnabled
                ? $"Listening on {config.ApiAddress}:{config.ApiPort}"
                : "Disabled";
        }
        catch (Exception exception)
        {
            ApiStatus = $"API failed: {exception.Message}";
            System.Diagnostics.Trace.TraceError(ApiStatus);
            if (throwOnFailure)
            {
                throw new InvalidOperationException(
                    $"Settings were saved, but the API server could not restart: {exception.Message}",
                    exception);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _api.DisposeAsync().ConfigureAwait(false);
        await _queue.DisposeAsync().ConfigureAwait(false);
    }

    private void OnSpeechFailed(string message) => SpeechFailed?.Invoke(message);

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
}
