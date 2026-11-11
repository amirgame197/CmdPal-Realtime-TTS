using System.Threading.Channels;
using CmdPalRealtimeTTS.Configuration;

namespace CmdPalRealtimeTTS.Services;

public interface ISpeechPlaybackQueue
{
    bool Enqueue(string text);
    void AbortCurrent();
}

public sealed class PlaybackQueue : ISpeechPlaybackQueue, IAsyncDisposable
{
    private readonly Channel<SpeechRequest> _generationQueue = Channel.CreateUnbounded<SpeechRequest>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly Channel<PlaybackRequest> _playbackQueue = Channel.CreateUnbounded<PlaybackRequest>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private readonly Func<TtsConfig> _getConfig;
    private readonly Func<string, TtsConfig, CancellationToken, Task<SpeechAudio>> _synthesize;
    private readonly Func<SpeechAudio, string, Func<bool>, CancellationToken, Task> _play;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _generationWorker;
    private readonly Task _playbackWorker;
    private readonly object _activeGate = new();
    private CancellationTokenSource? _activePlayback;
    private PlaybackRequest? _activeItem;
    private int _abortAfterWord;

    public PlaybackQueue(Func<TtsConfig> getConfig)
        : this(getConfig, NativeSpeechService.SynthesizeAsync, NativeSpeechService.PlayAsync)
    {}

    public PlaybackQueue(
        Func<TtsConfig> getConfig,
        Func<string, TtsConfig, CancellationToken, Task<SpeechAudio>> synthesize,
        Func<SpeechAudio, string, Func<bool>, CancellationToken, Task> play)
    {
        _getConfig = getConfig;
        _synthesize = synthesize;
        _play = play;
        _generationWorker = GenerateAsync();
        _playbackWorker = PlayAsync();
    }

    public event Action<string>? ErrorOccurred;

    public bool Enqueue(string text) => Enqueue(text, liveDelta: false);

    public bool Enqueue(string text, bool liveDelta)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return _generationQueue.Writer.TryWrite(new SpeechRequest(text, liveDelta));
    }

    public void AbortCurrent()
    {
        lock (_activeGate)
        {
            _activePlayback?.Cancel();
        }
    }

    public void AbortCurrentAfterWord()
    {
        lock (_activeGate)
        {
            if (_activeItem?.LiveDelta == true)
            {
                Interlocked.Exchange(ref _abortAfterWord, 1);
            }
        }
    }

    private async Task GenerateAsync()
    {
        try
        {
            await foreach (var request in _generationQueue.Reader.ReadAllAsync(_shutdown.Token).ConfigureAwait(false))
            {
                try
                {
                    var config = _getConfig();
                    var audio = await _synthesize(request.Text, config, _shutdown.Token).ConfigureAwait(false);
                    await _playbackQueue.Writer.WriteAsync(
                        new PlaybackRequest(audio, config.PlaybackDeviceId, request.LiveDelta),
                        _shutdown.Token).ConfigureAwait(false);
                }
                
                catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    ReportError("Speech generation failed", exception);
                }
            }
        }

        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) {}
        finally
        {
            _playbackQueue.Writer.TryComplete();
        }
    }

    private async Task PlayAsync()
    {
        try
        {
            await foreach (var item in _playbackQueue.Reader.ReadAllAsync(_shutdown.Token).ConfigureAwait(false))
            {
                using var playback = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
                lock (_activeGate)
                {
                    _activePlayback = playback;
                    _activeItem = item;
                }

                try
                {
                    await _play(
                        item.Audio,
                        item.DeviceId,
                        () => item.LiveDelta && Interlocked.Exchange(ref _abortAfterWord, 0) == 1,
                        playback.Token
                    ).ConfigureAwait(false);
                }

                catch (OperationCanceledException) when (playback.IsCancellationRequested) {}
                catch (Exception exception)
                {
                    ReportError("Speech playback failed", exception);
                }
                finally
                {
                    lock (_activeGate)
                    {
                        if (ReferenceEquals(_activePlayback, playback))
                        {
                            _activePlayback = null;
                            _activeItem = null;
                        }
                    }

                    Interlocked.Exchange(ref _abortAfterWord, 0);
                }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) {}
    }

    private void ReportError(string operation, Exception exception)
    {
        var message = $"{operation}: {exception.Message}";
        System.Diagnostics.Trace.TraceError($"{operation}: {exception}");
        try
        {
            ErrorOccurred?.Invoke(message);
        }
        catch (Exception callbackException)
        {
            System.Diagnostics.Trace.TraceError($"Speech error notification failed: {callbackException}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        _generationQueue.Writer.TryComplete();
        _shutdown.Cancel();
        AbortCurrent();

        await Task.WhenAll(_generationWorker, _playbackWorker).ConfigureAwait(false);
        _shutdown.Dispose();
    }

    private sealed record SpeechRequest(string Text, bool LiveDelta);
    private sealed record PlaybackRequest(SpeechAudio Audio, string DeviceId, bool LiveDelta);
}
