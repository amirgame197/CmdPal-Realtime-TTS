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
    private readonly NativeSpeechService _speech;
    private readonly Func<TtsConfig> _getConfig;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _generationWorker;
    private readonly Task _playbackWorker;
    private readonly object _activeGate = new();
    private CancellationTokenSource? _activePlayback;
    private PlaybackRequest? _activeItem;
    private int _abortAfterWord;

    public PlaybackQueue(NativeSpeechService speech, Func<TtsConfig> getConfig)
    {
        _speech = speech;
        _getConfig = getConfig;
        _generationWorker = GenerateAsync();
        _playbackWorker = PlayAsync();
    }

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
                var config = _getConfig();
                try
                {
                    var audio = await NativeSpeechService.SynthesizeAsync(request.Text, config, _shutdown.Token).ConfigureAwait(false);
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
                    System.Diagnostics.Trace.TraceError($"Speech generation failed: {exception}");
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
                    await NativeSpeechService.PlayAsync(
                        item.Audio,
                        item.DeviceId,
                        () => item.LiveDelta && Interlocked.Exchange(ref _abortAfterWord, 0) == 1,
                        playback.Token
                    ).ConfigureAwait(false);
                }

                catch (OperationCanceledException) when (playback.IsCancellationRequested) {}
                catch (Exception exception)
                {
                    System.Diagnostics.Trace.TraceError($"Speech playback failed: {exception}");
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
