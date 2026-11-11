using System.Text;
using System.Speech.Synthesis;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using CmdPalRealtimeTTS.Configuration;

namespace CmdPalRealtimeTTS.Services;

public sealed class NativeSpeechService
{
    public static IReadOnlyList<string> GetVoiceNames()
    {
        using var synthesizer = new SpeechSynthesizer();
        return [.. synthesizer.GetInstalledVoices()
            .Where(voice => voice.Enabled)
            .Select(voice => voice.VoiceInfo.Name)
            .Order(StringComparer.OrdinalIgnoreCase)];
    }

    public static IReadOnlyList<(string Id, string Name)> GetPlaybackDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        var devices = new List<(string Id, string Name)>();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            using (device)
            {
                devices.Add((device.ID, device.FriendlyName));
            }
        }

        return devices;
    }

    public static async Task<SpeechAudio> SynthesizeAsync(string text, TtsConfig config, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        text = PrepareSpeechText(text);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        return await Task.Run(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var synthesizer = new SpeechSynthesizer();
            if (!string.Equals(config.VoiceName, "auto", StringComparison.OrdinalIgnoreCase))
            {
                synthesizer.SelectVoice(config.VoiceName);
            }

            synthesizer.Rate = config.SpeechRate;
            synthesizer.Volume = config.SpeechVolume;

            var wordBoundaries = new List<TimeSpan>();
            synthesizer.SpeakProgress += (_, args) =>
            {
                if (args.AudioPosition > TimeSpan.Zero)
                {
                    lock (wordBoundaries)
                    {
                        wordBoundaries.Add(args.AudioPosition);
                    }
                }
            };

            using var audio = new MemoryStream();
            synthesizer.SetOutputToWaveStream(audio);

            var synthesisCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            synthesizer.SpeakCompleted += (_, args) =>
            {
                if (args.Error is not null)
                {
                    synthesisCompleted.TrySetException(args.Error);
                }
                else if (args.Cancelled)
                {
                    synthesisCompleted.TrySetCanceled();
                }
                else
                {
                    synthesisCompleted.TrySetResult();
                }
            };

            using var cancellationRegistration = cancellationToken.Register(() =>
            {
                try
                {
                    synthesizer.SpeakAsyncCancelAll();
                }
                catch (ObjectDisposedException)
                {
                }
            });
            
            synthesizer.SpeakAsync(text);
            await synthesisCompleted.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

            synthesizer.SetOutputToNull();

            cancellationToken.ThrowIfCancellationRequested();
            var bytes = audio.ToArray();
            ValidateWaveAudio(bytes);
            return new SpeechAudio(bytes, [.. wordBoundaries.Order()]);
        }, cancellationToken).ConfigureAwait(false);
    }

    public static async Task PlayAsync(
        SpeechAudio audio,
        string deviceId,
        Func<bool> abortAfterWordRequested,
        CancellationToken cancellationToken)
    {
        using var wave = new WaveFileReader(new MemoryStream(audio.Bytes, writable: false));
        using var device = GetDevice(deviceId);
        using var output = new WasapiOut(device, AudioClientShareMode.Shared, true, 100);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        output.PlaybackStopped += (_, args) =>
        {
            if (args.Exception is null)
            {
                completed.TrySetResult();
            }
            else
            {
                completed.TrySetException(args.Exception);
            }
        };
        output.Init(wave);
        output.Play();
        using var cancellationRegistration = cancellationToken.Register(() =>
        {
            try
            {
                output.Stop();
            }
            catch (ObjectDisposedException) {}
        });

        var stopAt = TimeSpan.MaxValue;
        while (output.PlaybackState == PlaybackState.Playing)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (stopAt == TimeSpan.MaxValue && abortAfterWordRequested())
            {
                stopAt = audio.WordBoundaries.FirstOrDefault(
                    boundary => boundary > wave.CurrentTime,
                    wave.TotalTime);
            }

            if (stopAt != TimeSpan.MaxValue && wave.CurrentTime >= stopAt)
            {
                output.Stop();
                break;
            }

            await Task.Delay(20, cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        await completed.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public static void ValidateWaveAudio(byte[] bytes)
    {
        using var wave = new WaveFileReader(new MemoryStream(bytes, writable: false));
        if (wave.Length == 0 || wave.TotalTime <= TimeSpan.Zero)
        {
            throw new InvalidDataException("The selected Windows voice produced no audio for this text.");
        }
    }

    private static string PrepareSpeechText(string text)
    {
        var output = new System.Text.StringBuilder(text.Length);
        foreach (var rune in text.EnumerateRunes())
        {
            if (Rune.IsControl(rune) && rune.Value is not ('\n' or '\r' or '\t'))
            {
                continue;
            }

            output.Append(rune.ToString());
        }

        return output.ToString();
    }

    public static async Task SaveAsync(string text, TtsConfig config, CancellationToken cancellationToken)
    {
        var audio = await SynthesizeAsync(text, config, cancellationToken).ConfigureAwait(false);
        Directory.CreateDirectory(config.SaveDirectory);

        var extension = config.SaveFormat;
        var path = Path.Combine(config.SaveDirectory, $"speech-{DateTime.Now:yyyyMMdd-HHmmss-fff}.{extension}");
        
        if (config.SaveFormat == "wav")
        {
            await File.WriteAllBytesAsync(path, audio.Bytes, cancellationToken).ConfigureAwait(false);
            return;
        }

        using var reader = new WaveFileReader(new MemoryStream(audio.Bytes, writable: false));
        MediaFoundationEncoder.EncodeToMp3(reader, path, 192000);
    }

    private static MMDevice GetDevice(string deviceId)
    {
        var enumerator = new MMDeviceEnumerator();
        try
        {
            if (string.Equals(deviceId, "auto", StringComparison.OrdinalIgnoreCase))
            {
                return enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            }

            return enumerator.GetDevice(deviceId);
        }
        finally
        {
            enumerator.Dispose();
        }
    }
}

public sealed record SpeechAudio(byte[] Bytes, IReadOnlyList<TimeSpan> WordBoundaries);
