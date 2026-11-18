using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using LiveTranscribe.Core.Engines;
using LiveTranscribe.Core.Translation;

namespace LiveTranscribe.Engines.Gemini;

/// <summary>
/// One-step translation over the Live API: the model hears the dictation and answers in English. Two
/// kinds of model take this route, and they need different setups (verified 2026-09-20):
/// <list type="bullet">
/// <item>A general Live model (<c>gemini-3.8-live</c>) takes a system instruction, so the topic note and
/// tone reach it. It only answers in audio, so the English is read from <c>outputTranscription</c> and
/// the audio is ignored. Turns are ours to end, as in dictation.</item>
/// <item>An interpreting model (<c>gemini-3.5-live-translate-preview</c>) takes only a target language, no
/// instruction and no manual turns. It translates while the user speaks and never says it has finished,
/// so the end is when its output has been quiet for a moment after the last word.</item>
/// </list>
/// </summary>
public sealed class GeminiLiveTranslator : ISpeechTranslator
{
    private static readonly Uri Endpoint = new(
        "wss://generativelanguage.googleapis.com/ws/google.ai.generativelanguage.v1beta.GenerativeService.BidiGenerateContent");

    private readonly string _modelId;
    private readonly string _apiKey;

    public GeminiLiveTranslator(string modelId, string apiKey)
    {
        _modelId = modelId;
        _apiKey = apiKey;
    }

    /// <summary>The interpreting models are named for it; nothing in the directory tells them apart otherwise.</summary>
    public static bool IsInterpreter(string modelId) =>
        modelId.Contains("translate", StringComparison.OrdinalIgnoreCase);

    public TranslationRoute Route => TranslationRoute.OneStep;

    public bool UsesContext => !IsInterpreter(_modelId);

    public async Task<ITranslationSession> StartAsync(TranslationContext context, CancellationToken ct)
    {
        var socket = new ClientWebSocket();
        // Header rather than query string, so the key never appears in a URL (FR-11.3).
        socket.Options.SetRequestHeader("x-goog-api-key", _apiKey);

        try
        {
            await socket.ConnectAsync(Endpoint, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            socket.Dispose();
            throw GeminiErrors.FromConnectFailure(ex, _apiKey);
        }

        var session = new GeminiLiveTranslationSession(socket, _apiKey, IsInterpreter(_modelId));
        try
        {
            await session.OpenAsync(_modelId, context, ct).ConfigureAwait(false);
            return session;
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}

internal sealed class GeminiLiveTranslationSession : ITranslationSession
{
    private static readonly TimeSpan SetupTimeout = TimeSpan.FromSeconds(10);

    // A general model can send one more piece of the English just after it says it is done.
    private static readonly TimeSpan LateOutputWindow = TimeSpan.FromMilliseconds(150);

    // The interpreter lags the speaker by a second or two and sends no end marker, so it is done when
    // it has been quiet this long after the last word (measured lag between pieces: 0.5-1.2 s).
    private static readonly TimeSpan InterpreterQuiet = TimeSpan.FromMilliseconds(1300);
    private static readonly TimeSpan InterpreterNoOutputLimit = TimeSpan.FromSeconds(5);
    private static readonly byte[] SilenceChunk = new byte[3200];   // 100 ms of 16 kHz 16-bit mono

    private readonly ClientWebSocket _socket;
    private readonly string _apiKey;
    private readonly bool _interpreter;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _receiveCancellation = new();
    private readonly StringBuilder _english = new();
    private readonly StringBuilder _heard = new();
    private readonly object _textGate = new();
    private readonly TaskCompletionSource _setupComplete = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _generationComplete = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Environment.TickCount64 of the last piece of English; 0 until the first one.
    private long _lastOutputAt;

    private volatile bool _endOfSpeechSent;
    private int _outputCount;
    private Task? _receiveLoop;
    private Exception? _serverFailure;

    public GeminiLiveTranslationSession(ClientWebSocket socket, string apiKey, bool interpreter)
    {
        _socket = socket;
        _apiKey = apiKey;
        _interpreter = interpreter;
    }

    public async Task OpenAsync(string modelId, TranslationContext context, CancellationToken ct)
    {
        _receiveLoop = Task.Run(ReceiveLoopAsync);

        object setup = _interpreter
            ? new
            {
                model = $"models/{modelId}",
                generationConfig = new
                {
                    responseModalities = new[] { "AUDIO" },
                    // Echo: English parts of a mixed dictation must come through, not be dropped.
                    translationConfig = new { targetLanguageCode = "en", echoTargetLanguage = true }
                },
                inputAudioTranscription = new { },
                outputAudioTranscription = new { }
            }
            : new
            {
                model = $"models/{modelId}",
                // These models refuse TEXT output ("The requested combination of response modalities
                // (TEXT) is not supported"), so they speak and the transcript of that speech is the text.
                generationConfig = new { responseModalities = new[] { "AUDIO" } },
                systemInstruction = new { parts = new[] { new { text = TranslationPrompt.ForAudio(context) } } },
                inputAudioTranscription = new { },
                outputAudioTranscription = new { },
                realtimeInputConfig = new { automaticActivityDetection = new { disabled = true } }
            };

        await SendAsync(new { setup }, ct).ConfigureAwait(false);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(SetupTimeout);
        try
        {
            await _setupComplete.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw _serverFailure as EngineException
                  ?? new EngineException(EngineErrorKind.Network, "Gemini did not confirm the session setup in time.");
        }

        if (!_interpreter)
        {
            await SendAsync(new { realtimeInput = new { activityStart = new { } } }, ct).ConfigureAwait(false);
        }
    }

    public async ValueTask WriteAudioAsync(ReadOnlyMemory<byte> pcm, CancellationToken ct)
    {
        ThrowIfServerFailed();
        await SendAsync(new
        {
            realtimeInput = new { audio = new { data = Convert.ToBase64String(pcm.Span), mimeType = "audio/pcm;rate=16000" } }
        }, ct).ConfigureAwait(false);
    }

    public async Task<TranslationResult> CompleteAsync(CancellationToken ct)
    {
        ThrowIfServerFailed();
        var endOfSpeech = Stopwatch.StartNew();
        _endOfSpeechSent = true;

        if (_interpreter)
        {
            await WaitForInterpreterAsync(endOfSpeech, ct).ConfigureAwait(false);
        }
        else
        {
            await SendAsync(new { realtimeInput = new { activityEnd = new { } } }, ct).ConfigureAwait(false);
            var receiveLoop = _receiveLoop ?? Task.CompletedTask;
            var finished = await Task.WhenAny(_generationComplete.Task, receiveLoop).WaitAsync(ct).ConfigureAwait(false);
            ThrowIfServerFailed();
            if (finished == receiveLoop)
            {
                throw new EngineException(EngineErrorKind.Network, "Connection closed before the translation arrived.");
            }

            await Task.Delay(LateOutputWindow, ct).ConfigureAwait(false);
        }

        lock (_textGate)
        {
            return new TranslationResult(_english.ToString().Trim(), _heard.ToString().Trim(), endOfSpeech.Elapsed);
        }
    }

    /// <summary>
    /// Keeps the stream alive with silence, which the interpreter needs to finish the sentence it is on,
    /// and stops once its output has gone quiet (or never started).
    /// </summary>
    private async Task WaitForInterpreterAsync(Stopwatch endOfSpeech, CancellationToken ct)
    {
        var outputAtEnd = Volatile.Read(ref _outputCount);
        while (true)
        {
            ThrowIfServerFailed();
            await SendAsync(new
            {
                realtimeInput = new { audio = new { data = Convert.ToBase64String(SilenceChunk), mimeType = "audio/pcm;rate=16000" } }
            }, ct).ConfigureAwait(false);
            await Task.Delay(100, ct).ConfigureAwait(false);

            var outputs = Volatile.Read(ref _outputCount);
            if (outputs == 0 && endOfSpeech.Elapsed > InterpreterNoOutputLimit)
            {
                return;
            }

            // Quiet counts only once something arrived after the last word, or if everything already had.
            var lastOutputAt = Interlocked.Read(ref _lastOutputAt);
            var quiet = lastOutputAt != 0 && Environment.TickCount64 - lastOutputAt > InterpreterQuiet.TotalMilliseconds;
            if (outputs > 0 && quiet && (outputs > outputAtEnd || endOfSpeech.Elapsed > InterpreterQuiet))
            {
                return;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _receiveCancellation.Cancel();
        try
        {
            if (_socket.State == WebSocketState.Open)
            {
                using var closeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", closeTimeout.Token)
                    .ConfigureAwait(false);
            }
        }
        catch
        {
            // Closing is best effort.
        }

        if (_receiveLoop is not null)
        {
            try
            {
                await _receiveLoop.ConfigureAwait(false);
            }
            catch
            {
                // Already surfaced through _serverFailure.
            }
        }

        _socket.Dispose();
        _sendLock.Dispose();
        _receiveCancellation.Dispose();
    }

    private async Task SendAsync(object payload, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct).ConfigureAwait(false);
        }
        catch (WebSocketException ex)
        {
            throw _serverFailure as EngineException ?? GeminiErrors.FromSocketFailure(ex, _socket, _apiKey);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task ReceiveLoopAsync()
    {
        // The spoken answer arrives as large audio frames; they are read and dropped.
        var buffer = new byte[64 * 1024];
        var token = _receiveCancellation.Token;

        try
        {
            while (_socket.State == WebSocketState.Open && !token.IsCancellationRequested)
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await _socket.ReceiveAsync(buffer, token).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        Fail(GeminiErrors.FromClose(_socket.CloseStatus, _socket.CloseStatusDescription, _apiKey));
                        return;
                    }

                    message.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                Handle(message.GetBuffer().AsMemory(0, (int)message.Length));
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (WebSocketException ex)
        {
            Fail(GeminiErrors.FromSocketFailure(ex, _socket, _apiKey));
        }
    }

    private void Handle(ReadOnlyMemory<byte> json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        if (root.TryGetProperty("setupComplete", out _))
        {
            _setupComplete.TrySetResult();
        }

        if (!root.TryGetProperty("serverContent", out var content))
        {
            return;
        }

        if (content.TryGetProperty("inputTranscription", out var heard) && heard.TryGetProperty("text", out var heardText))
        {
            lock (_textGate)
            {
                _heard.Append(heardText.GetString());
            }
        }

        if (content.TryGetProperty("outputTranscription", out var output) && output.TryGetProperty("text", out var outputText))
        {
            lock (_textGate)
            {
                _english.Append(outputText.GetString());
            }

            Interlocked.Increment(ref _outputCount);
            Interlocked.Exchange(ref _lastOutputAt, Environment.TickCount64);
        }

        if (_endOfSpeechSent &&
            ((content.TryGetProperty("generationComplete", out var generation) && generation.ValueKind == JsonValueKind.True) ||
             (content.TryGetProperty("turnComplete", out var turn) && turn.ValueKind == JsonValueKind.True)))
        {
            _generationComplete.TrySetResult();
        }
    }

    private void Fail(EngineException failure)
    {
        _serverFailure ??= failure;
        _setupComplete.TrySetException(failure);
        _generationComplete.TrySetException(failure);
    }

    private void ThrowIfServerFailed()
    {
        if (_serverFailure is not null)
        {
            throw _serverFailure;
        }
    }
}
