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
        return await Task.Run(() =>
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
            synthesizer.Speak(text);
            synthesizer.SetOutputToNull();
            
            cancellationToken.ThrowIfCancellationRequested();
            return new SpeechAudio(audio.ToArray(), wordBoundaries.Order().ToArray());
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
        if (stopAt == TimeSpan.MaxValue)
        {
            await completed.Task.ConfigureAwait(false);
        }
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
