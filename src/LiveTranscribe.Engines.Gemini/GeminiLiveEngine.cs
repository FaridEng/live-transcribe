using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using LiveTranscribe.Core.Engines;
using LiveTranscribe.Core.Settings;

namespace LiveTranscribe.Engines.Gemini;

/// <summary>
/// Gemini Live API transcription over WebSocket with manual turn control, which matches
/// hold-to-talk and toggle dictation (D3, D9). Protocol verified on 2026-09-16 (architecture T2).
/// </summary>
public sealed class GeminiLiveEngine : ISpeechEngine
{
    private static readonly Uri Endpoint = new(
        "wss://generativelanguage.googleapis.com/ws/google.ai.generativelanguage.v1beta.GenerativeService.BidiGenerateContent");

    private readonly EngineProfile _profile;
    private readonly string _apiKey;

    public GeminiLiveEngine(EngineProfile profile, string apiKey)
    {
        _profile = profile;
        _apiKey = apiKey;
    }

    public EngineCapabilities Capabilities { get; } = new(
        Streaming: true,
        InterimResults: true,
        SmartCleanup: true,
        LanguageHints: true,
        ManualTurnControl: true,
        MaxSessionLength: TimeSpan.FromMinutes(10));

    /// <summary>
    /// Asks the directory instead of opening a socket: it separates a bad key from a model that is not
    /// live, and it costs no session quota (FR-13.10).
    /// </summary>
    public Task<EngineTestReport> TestAsync(CancellationToken ct) =>
        GeminiCatalog.TestAsync(_profile.ModelId, _apiKey, GeminiCatalog.LiveMethod, ct);

    public async Task<ISpeechSession> StartSessionAsync(EngineSessionOptions options, CancellationToken ct)
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

        var session = new GeminiLiveSession(socket, _apiKey);
        try
        {
            await session.OpenAsync(_profile.ModelId, options, ct).ConfigureAwait(false);
            return session;
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}

internal sealed class GeminiLiveSession : ISpeechSession
{
    private static readonly TimeSpan SetupTimeout = TimeSpan.FromSeconds(10);

    // Extra finals can follow the first one, but usually none does. Rather than always paying the
    // full grace, return as soon as one quiet window passes with no new segment.
    private static readonly TimeSpan QuietWindow = TimeSpan.FromMilliseconds(60);
    private static readonly TimeSpan CompletionGrace = TimeSpan.FromMilliseconds(250);

    // Silence ends with ACTIVITY_END and no generationComplete; allow a late transcript before giving up.
    private static readonly TimeSpan NoTranscriptGrace = TimeSpan.FromSeconds(1);

    private readonly ClientWebSocket _socket;
    private readonly string _apiKey;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _receiveCancellation = new();
    private readonly StringBuilder _finalText = new();
    private readonly object _textGate = new();
    private readonly TaskCompletionSource _setupComplete = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _generationComplete = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _activityEnded = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // End signals only count once we have sent activityEnd; a pause mid-dictation can also produce them.
    private volatile bool _endOfSpeechSent;

    // Counts final segments, so the completion wait can end as soon as they stop arriving.
    private int _segmentCount;

    private Task? _receiveLoop;
    private Exception? _serverFailure;

    public GeminiLiveSession(ClientWebSocket socket, string apiKey)
    {
        _socket = socket;
        _apiKey = apiKey;
    }

    public event Action<string>? InterimText;
    public event Action<string>? FinalSegment;

    public async Task OpenAsync(string modelId, EngineSessionOptions options, CancellationToken ct)
    {
        _receiveLoop = Task.Run(ReceiveLoopAsync);

        await SendAsync(new
        {
            setup = new
            {
                model = $"models/{modelId}",
                generationConfig = new { responseModalities = new[] { "TEXT" } },
                inputAudioTranscription = new
                {
                    languageCodes = LanguageCodes(options.Language),
                    // SMART removes fillers and resolves self-corrections (FR-5.2, FR-5.5).
                    mode = options.Cleanup == CleanupLevel.Standard ? "SMART" : "VERBATIM"
                },
                realtimeInputConfig = new { automaticActivityDetection = new { disabled = true } }
            }
        }, ct).ConfigureAwait(false);

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

        await SendAsync(new { realtimeInput = new { activityStart = new { } } }, ct).ConfigureAwait(false);
    }

    public async ValueTask WriteAudioAsync(ReadOnlyMemory<byte> pcm, CancellationToken ct)
    {
        ThrowIfServerFailed();
        var data = Convert.ToBase64String(pcm.Span);
        await SendAsync(new { realtimeInput = new { audio = new { data, mimeType = "audio/pcm;rate=16000" } } }, ct)
            .ConfigureAwait(false);
    }

    public async Task<TranscriptResult> CompleteAsync(CancellationToken ct)
    {
        ThrowIfServerFailed();
        var endOfSpeech = Stopwatch.StartNew();
        _endOfSpeechSent = true;
        await SendAsync(new { realtimeInput = new { activityEnd = new { } } }, ct).ConfigureAwait(false);

        var receiveLoop = _receiveLoop ?? Task.CompletedTask;
        var finished = await Task.WhenAny(_generationComplete.Task, _activityEnded.Task, receiveLoop)
            .WaitAsync(ct).ConfigureAwait(false);

        ThrowIfServerFailed();
        if (finished == receiveLoop)
        {
            throw new EngineException(EngineErrorKind.Network, "Connection closed before the transcript arrived.");
        }

        if (finished == _activityEnded.Task && !_generationComplete.Task.IsCompleted)
        {
            // Usually silence: the server ends the activity without generating anything.
            await Task.WhenAny(_generationComplete.Task, Task.Delay(NoTranscriptGrace, ct)).ConfigureAwait(false);
            ThrowIfServerFailed();
        }

        await WaitForLateSegmentsAsync(ct).ConfigureAwait(false);

        lock (_textGate)
        {
            return new TranscriptResult(_finalText.ToString().Trim(), endOfSpeech.Elapsed);
        }
    }

    /// <summary>
    /// Waits only as long as segments keep arriving. A dictation that is already complete costs one
    /// quiet window instead of the full grace, which is most of the wait after the last word.
    /// </summary>
    private async Task WaitForLateSegmentsAsync(CancellationToken ct)
    {
        var waited = Stopwatch.StartNew();
        while (waited.Elapsed < CompletionGrace)
        {
            var seen = Volatile.Read(ref _segmentCount);
            await Task.Delay(QuietWindow, ct).ConfigureAwait(false);
            if (Volatile.Read(ref _segmentCount) == seen)
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
        var buffer = new byte[16 * 1024];
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

                Handle(message.ToArray());
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

    private void Handle(byte[] json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        if (root.TryGetProperty("setupComplete", out _))
        {
            _setupComplete.TrySetResult();
        }

        HandleVoiceActivity(root);

        if (!root.TryGetProperty("serverContent", out var content))
        {
            return;
        }

        if (content.TryGetProperty("interimInputTranscription", out var interim) &&
            interim.TryGetProperty("text", out var interimText))
        {
            InterimText?.Invoke(interimText.GetString() ?? string.Empty);
        }

        if (content.TryGetProperty("inputTranscription", out var final) &&
            final.TryGetProperty("text", out var finalText))
        {
            var segment = finalText.GetString() ?? string.Empty;
            lock (_textGate)
            {
                if (_finalText.Length > 0 && segment.Length > 0)
                {
                    _finalText.Append(' ');
                }

                _finalText.Append(segment);
            }

            Interlocked.Increment(ref _segmentCount);
            FinalSegment?.Invoke(segment);
        }

        if (_endOfSpeechSent &&
            ((content.TryGetProperty("generationComplete", out var generation) && generation.ValueKind == JsonValueKind.True) ||
             (content.TryGetProperty("turnComplete", out var turn) && turn.ValueKind == JsonValueKind.True)))
        {
            _generationComplete.TrySetResult();
        }
    }

    private void HandleVoiceActivity(JsonElement root)
    {
        if (_endOfSpeechSent &&
            root.TryGetProperty("voiceActivity", out var activity) &&
            activity.TryGetProperty("type", out var type) &&
            type.GetString() == "ACTIVITY_END")
        {
            _activityEnded.TrySetResult();
        }
    }

    private void Fail(EngineException failure)
    {
        _serverFailure ??= failure;
        _setupComplete.TrySetException(failure);
        _generationComplete.TrySetException(failure);
        _activityEnded.TrySetException(failure);
    }

    private void ThrowIfServerFailed()
    {
        if (_serverFailure is not null)
        {
            throw _serverFailure;
        }
    }

    private static string[] LanguageCodes(LanguageMode mode) => mode switch
    {
        LanguageMode.Persian => ["fa-IR"],
        LanguageMode.English => ["en-US"],
        _ => []
    };
}
