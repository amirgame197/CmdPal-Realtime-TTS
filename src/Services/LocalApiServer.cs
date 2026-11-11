using System.Net;
using System.Text;
using System.Text.Json;
using CmdPalRealtimeTTS.Configuration;

namespace CmdPalRealtimeTTS.Services;

public sealed class LocalApiServer : IAsyncDisposable
{
    private const int MaximumBodyBytes = 1024 * 1024;
    private readonly ISpeechPlaybackQueue _queue;
    private readonly SemaphoreSlim _restartGate = new(1, 1);
    private HttpListener? _listener;
    private CancellationTokenSource? _runCancellation;
    private Task? _runTask;

    public LocalApiServer(ISpeechPlaybackQueue queue) => _queue = queue;

    public async Task RestartAsync(TtsConfig config)
    {
        await _restartGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopAsync().ConfigureAwait(false);
            if (!config.ApiEnabled)
            {
                return;
            }

            config.Validate();
            var address = IPAddress.Parse(config.ApiAddress);
            var host = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
                ? $"[{address}]"
                : address.ToString();
            var listener = new HttpListener();

            // ! Use http:// prefix for the listener
            listener.Prefixes.Add($"http://{host}:{config.ApiPort}/");
            listener.Start();

            // ? Setup cancellation and run the listener in the background
            var cancellation = new CancellationTokenSource();
            _listener = listener;
            _runCancellation = cancellation;
            _runTask = RunAsync(listener, cancellation.Token);
        }
        
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceError($"Speech API could not start: {exception}");
            throw;
        }
        finally
        {
            _restartGate.Release();
        }
    }

    private async Task RunAsync(HttpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (HttpListenerException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                System.Diagnostics.Trace.TraceError($"Speech API listener failed: {exception}");
                return;
            }

            _ = HandleRequestAsync(context, cancellationToken);
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        try
        {
            var path = context.Request.Url?.AbsolutePath.TrimEnd('/') ?? string.Empty;

            // # Base url path returns API info
            if (path.Equals(string.Empty, StringComparison.OrdinalIgnoreCase) ||
                path.Equals("/", StringComparison.OrdinalIgnoreCase))
            {
                await WriteJsonAsync(context.Response, 200, @"
                {
                    ""name"": ""CmdPal Realtime TTS"",
                    ""description"": ""Windows native speech API"",
                    ""status"": ""running"",
                    
                    ""endpoints"": {
                        ""GET /generate?text=Hello"": ""Generate speech for the given text"",
                        ""POST /generate"": ""Generate speech for the given text in the request body"",
                        ""POST /abort"": ""Abort the current speech playback""
                    }
                }").ConfigureAwait(false);
                return;
            }

            // # Main generator path handles both GET and POST requests for speech generation
            if (path.Equals("/generate", StringComparison.OrdinalIgnoreCase))
            {
                var text = await GetTextAsync(context.Request, cancellationToken).ConfigureAwait(false);
                if (!_queue.Enqueue(text))
                {
                    // ! If the enqueue failed
                    await WriteJsonAsync(context.Response, 400, """{"error":"text must not be empty"}""").ConfigureAwait(false);
                    return;
                }

                await WriteJsonAsync(context.Response, 202, """{"queued":true}""").ConfigureAwait(false);
                return;
            }

            // # Abort path stops the current playback
            if (path.Equals("/abort", StringComparison.OrdinalIgnoreCase))
            {
                _queue.AbortCurrent();
                await WriteJsonAsync(context.Response, 200, """{"aborted":true}""").ConfigureAwait(false);
                return;
            }

            // * Fallback for unknown routes
            await WriteJsonAsync(context.Response, 404, """{"error":"route not found"}""").ConfigureAwait(false);
        }

        catch (Exception exception) when (exception is InvalidDataException or JsonException)
        {
            await WriteJsonAsync(context.Response, 400, JsonSerializer.Serialize(new { error = exception.Message })).ConfigureAwait(false);
        }

        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceError($"Speech API request failed: {exception}");
            try
            {
                await WriteJsonAsync(context.Response, 500, """{"error":"request failed. see extension logs"}""").ConfigureAwait(false);
            }
            catch (Exception responseException)
            {
                System.Diagnostics.Trace.TraceError($"Could not write API error response: {responseException}");
            }
        }

        finally
        {
            try
            {
                context.Response.Close();
            }
            catch (Exception exception)
            {
                System.Diagnostics.Trace.TraceError($"Could not close API response: {exception}");
            }
        }
    }

    private static async Task<string> GetTextAsync(HttpListenerRequest request, CancellationToken cancellationToken)
    {
        if (request.HttpMethod == "GET")
        {
            return request.QueryString["text"] ?? string.Empty;
        }

        if (request.HttpMethod != "POST")
        {
            throw new InvalidDataException("Use GET or POST for /generate.");
        }

        var body = await ReadBodyAsync(request, cancellationToken).ConfigureAwait(false);
        var contentType = request.ContentType?.Split(';', 2)[0].Trim();

        // ? If the content type is JSON
        if (string.Equals(contentType, "application/json", StringComparison.OrdinalIgnoreCase))
        {
            using var json = JsonDocument.Parse(body);
            if (json.RootElement.ValueKind == JsonValueKind.String)
            {
                return json.RootElement.GetString() ?? string.Empty;
            }

            if (json.RootElement.ValueKind == JsonValueKind.Object &&
                json.RootElement.TryGetProperty("text", out var text) &&
                text.ValueKind == JsonValueKind.String)
            {
                return text.GetString() ?? string.Empty;
            }

            throw new InvalidDataException("JSON request body must be a string or an object with a string 'text' field.");
        }

        // ? If the content type is form-urlencoded
        if (string.Equals(contentType, "application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase))
        {
            var form = System.Web.HttpUtility.ParseQueryString(body);
            return form["text"] ?? string.Empty;
        }

        return body;
    }

    private static async Task<string> ReadBodyAsync(HttpListenerRequest request, CancellationToken cancellationToken)
    {
        if (request.ContentLength64 > MaximumBodyBytes)
        {
            throw new InvalidDataException("Request body exceeds the 1 MB limit.");
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;

        while ((read = await request.InputStream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaximumBodyBytes)
            {
                throw new InvalidDataException("Request body exceeds the 1 MB limit.");
            }

            buffer.Write(chunk, 0, read);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static async Task WriteJsonAsync(HttpListenerResponse response, int statusCode, string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        response.StatusCode = statusCode;
        response.ContentType = "application/json; charset=utf-8";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
    }

    private async Task StopAsync()
    {
        var listener = _listener;
        var cancellation = _runCancellation;
        var runTask = _runTask;

        _listener = null;
        _runCancellation = null;
        _runTask = null;

        if (listener is null)
        {
            return;
        }

        cancellation!.Cancel();
        listener.Close();

        try
        {
            await runTask!.ConfigureAwait(false);
        }
        catch (HttpListenerException) {}
        cancellation.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await _restartGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopAsync().ConfigureAwait(false);
        }
        finally
        {
            _restartGate.Release();
            _restartGate.Dispose();
        }
    }
}
