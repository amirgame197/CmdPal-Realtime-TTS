using CmdPalRealtimeTTS.Configuration;
using CmdPalRealtimeTTS.Services;
using System.Net;
using System.Net.Sockets;
using System.Text;

var generated = new List<string>();
var aborts = 0;
var config = new TtsConfig
{
    DeltaOnPunctuation = false,
    DeltaOnThreeWords = true,
    DeltaOnInactivity = false,
};

using (var deltas = new DeltaGenerator(() => config, text => generated.Add(text), () => aborts++))
{
    deltas.Update("one hundred cats");
    Check(generated.Count == 0, "The three-word trigger waits until three complete words and 15 characters.");
    deltas.Update("one hundred cats today");
    Check(generated.Count == 0, "The three-word trigger does not interrupt an unfinished word.");
    deltas.Update("one hundred cats today ");
    Check(generated.SequenceEqual(["one hundred cats today"]), "Three complete words and 15 characters trigger pending text.");

    deltas.Update("prefix");
    deltas.Update("prefi");
    Check(aborts == 1, "Backspace requests one active-delta abort.");

    deltas.Update("pending text");
    Check(deltas.GeneratePending(), "Manual/Enter generation enqueues pending text.");
    Check(generated.Last() == "pending text", "Manual/Enter generation uses only the pending delta.");
}

generated.Clear();
config.DeltaOnThreeWords = false;
config.DeltaOnPunctuation = true;
using (var deltas = new DeltaGenerator(() => config, text => generated.Add(text), () => { }))
{
    deltas.Update("Question?");
    Check(generated.SequenceEqual(["Question?"]), "Punctuation triggers immediately.");

    deltas.Update("Question?.");
    Check(generated.Count == 1, "A punctuation-only delta does not enqueue a silent playback request.");
}

generated.Clear();
config.DeltaOnThreeWords = true;
config.DeltaOnPunctuation = false;
using (var deltas = new DeltaGenerator(() => config, text => generated.Add(text), () => { }))
{
    deltas.Update("متاسفانه شما هنوز زندگی نمی کنید ");
    Check(generated.SequenceEqual(["متاسفانه شما هنوز زندگی نمی کنید"]), "Unicode words count toward the complete-word trigger.");
}

var emptyWave = Convert.FromHexString("524946462600000057415645666D742012000000010001002256000044AC00000200100000006461746100000000");
CheckThrows<InvalidDataException>(
    () => NativeSpeechService.ValidateWaveAudio(emptyWave),
    "A valid WAV container with no audio samples is rejected before queueing or saving.");
var nonEmptyWave = Convert.FromHexString("524946462800000057415645666D742012000000010001002256000044AC000002001000000064617461020000000100");
NativeSpeechService.ValidateWaveAudio(nonEmptyWave);

generated.Clear();
config.DeltaOnThreeWords = false;
config.DeltaOnPunctuation = false;
config.DeltaOnInactivity = true;
using (var deltas = new DeltaGenerator(() => config, text => generated.Add(text), () => { }))
{
    deltas.Update("idle text");
    await Task.Delay(1700);
    Check(generated.SequenceEqual(["idle text"]), "Inactivity triggers after 1.5 seconds.");
}

config.SpeechRate = 11;
CheckThrows<InvalidDataException>(config.Validate, "Speech rate outside -10..10 is rejected.");
config.SpeechRate = 0;
config.SpeechVolume = 101;
CheckThrows<InvalidDataException>(config.Validate, "Speech volume outside 0..100 is rejected.");

var portProbe = new TcpListener(IPAddress.Loopback, 0);
portProbe.Start();
var apiPort = ((IPEndPoint)portProbe.LocalEndpoint).Port;
portProbe.Stop();
var fakeQueue = new FakePlaybackQueue();

await using (var api = new LocalApiServer(fakeQueue))
{
    var apiConfig = new TtsConfig { ApiAddress = "127.0.0.1", ApiPort = apiPort };
    await api.RestartAsync(apiConfig);
    using var client = new HttpClient();

    using var getResponse = await client.GetAsync($"http://127.0.0.1:{apiPort}/generate?text=Hello%20there");
    Check(getResponse.StatusCode == HttpStatusCode.Accepted, "GET /generate returns 202.");
    Check(await getResponse.Content.ReadAsStringAsync() == """{"queued":true}""", "GET /generate returns the expected queue response.");
    Check(fakeQueue.Texts.Last() == "Hello there", "GET /generate decodes query text.");

    const string persianText = "سلام";
    using var persianGetResponse = await client.GetAsync(
        $"http://127.0.0.1:{apiPort}/generate?text={Uri.EscapeDataString(persianText)}");
    Check(persianGetResponse.StatusCode == HttpStatusCode.Accepted, "GET /generate accepts UTF-8 text.");
    Check(fakeQueue.Texts.Last() == persianText, "GET /generate preserves Persian text.");

    using var jsonResponse = await client.PostAsync(
        $"http://127.0.0.1:{apiPort}/generate",
        new StringContent("""{"text":"JSON text"}""", Encoding.UTF8, "application/json"));
    Check(jsonResponse.StatusCode == HttpStatusCode.Accepted, "JSON POST /generate returns 202.");
    Check(fakeQueue.Texts.Last() == "JSON text", "JSON POST extracts the text field.");

    using var formResponse = await client.PostAsync(
        $"http://127.0.0.1:{apiPort}/generate",
        new FormUrlEncodedContent(new Dictionary<string, string> { ["text"] = "Form text" }));
    Check(formResponse.StatusCode == HttpStatusCode.Accepted, "Form POST /generate returns 202.");
    Check(fakeQueue.Texts.Last() == "Form text", "Form POST extracts the text field.");

    using var persianJsonResponse = await client.PostAsync(
        $"http://127.0.0.1:{apiPort}/generate",
        new StringContent("""{"text":"سلام"}""", Encoding.UTF8, "application/json"));
    Check(persianJsonResponse.StatusCode == HttpStatusCode.Accepted, "JSON POST accepts UTF-8 text.");
    Check(fakeQueue.Texts.Last() == persianText, "JSON POST preserves Persian text.");

    using var abortResponse = await client.GetAsync($"http://127.0.0.1:{apiPort}/abort");
    Check(abortResponse.StatusCode == HttpStatusCode.OK && fakeQueue.AbortCount == 1, "GET /abort stops current playback.");

    using var emptyResponse = await client.GetAsync($"http://127.0.0.1:{apiPort}/generate?text=");
    Check(emptyResponse.StatusCode == HttpStatusCode.BadRequest, "Empty generation input returns 400.");

    using var missingRouteResponse = await client.GetAsync($"http://127.0.0.1:{apiPort}/missing");
    Check(missingRouteResponse.StatusCode == HttpStatusCode.NotFound, "Unknown API routes return 404.");
}

var playbackCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var queueError = string.Empty;
await using (var queue = new PlaybackQueue(
    () => new TtsConfig(),
    (text, _, _) => text == "سلام"
        ? Task.FromException<SpeechAudio>(new InvalidOperationException("unsupported test voice"))
        : Task.FromResult(new SpeechAudio([], [])),
    (_, _, _, _) =>
    {
        playbackCompleted.TrySetResult();
        return Task.CompletedTask;
    }))
{
    queue.ErrorOccurred += message => queueError = message;
    Check(queue.Enqueue("سلام"), "Unicode speech request enters the generation queue.");
    Check(queue.Enqueue("following request"), "Following request enters the generation queue.");
    await playbackCompleted.Task.WaitAsync(TimeSpan.FromSeconds(3));
    Check(queueError.Contains("unsupported test voice", StringComparison.Ordinal), "Speech generation failures are reported.");
}

var firstPlaybackStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var nextPlaybackCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var playbackCount = 0;
await using (var queue = new PlaybackQueue(
    () => new TtsConfig(),
    (_, _, _) => Task.FromResult(new SpeechAudio([], [])),
    async (_, _, _, cancellationToken) =>
    {
        if (Interlocked.Increment(ref playbackCount) == 1)
        {
            firstPlaybackStarted.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
        else
        {
            nextPlaybackCompleted.TrySetResult();
        }
    }))
{
    queue.Enqueue("first");
    await firstPlaybackStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
    queue.AbortCurrent();
    queue.Enqueue("after abort");
    await nextPlaybackCompleted.Task.WaitAsync(TimeSpan.FromSeconds(3));
}

Console.WriteLine("All core checks passed.");

static void Check(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static void CheckThrows<TException>(Action action, string message)
    where TException : Exception
{
    try
    {
        action();
    }
    catch (TException)
    {
        return;
    }

    throw new InvalidOperationException(message);
}

sealed class FakePlaybackQueue : ISpeechPlaybackQueue
{
    public List<string> Texts { get; } = [];

    public int AbortCount { get; private set; }

    public bool Enqueue(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        Texts.Add(text);
        return true;
    }

    public void AbortCurrent() => AbortCount++;
}
