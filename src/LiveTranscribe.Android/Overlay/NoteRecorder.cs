using Android.Content;
using LiveTranscribe.Core.Abstractions;
using LiveTranscribe.Core.Engines;
using LiveTranscribe.Core.Text;
using LiveTranscribe.Droid.Platform;

namespace LiveTranscribe.Droid.Overlay;

/// <summary>
/// Records straight into the topic-note card's own text box (user's choice, 2026-09-28, "Option B" of
/// two proposed fixes for the note not being dictatable): a self-contained mic session, independent of
/// <c>DictationController</c>, that hands back plain text for the caller to insert in code. This is
/// deliberately not the accessibility insert-into-focused-field path the rest of the app uses, because
/// that depends on Android reporting focus for the app's own overlay window the same way it does for a
/// normal app's field, which was not something this session could verify on a device.
/// </summary>
public sealed class NoteRecorder
{
    private static readonly TimeSpan CompletionTimeout = TimeSpan.FromSeconds(20);

    private readonly AppHost _host;
    private readonly Context _context;
    private IAudioCapture? _capture;
    private ISpeechSession? _session;

    // Set by Cancel() and checked after the one genuinely slow step (connecting to the engine), so a
    // note closed while that connection is still in flight does not leave the microphone open once it
    // finally comes back - Cancel() only knew to stop a capture/session that did not exist yet.
    private volatile bool _cancelRequested;

    public NoteRecorder(AppHost host, Context context)
    {
        _host = host;
        _context = context;
    }

    public bool IsRecording { get; private set; }

    /// <summary>Opens the microphone and starts an engine session. Returns why it failed, or null on success.</summary>
    public async Task<string?> StartAsync()
    {
        _cancelRequested = false;
        var settings = _host.Settings.Current;
        var profile = settings.ActiveProfile;
        var key = profile is null ? null : _host.Secrets.Get(profile.SecretRef);
        if (profile is null || string.IsNullOrWhiteSpace(key))
        {
            return "Open LiveTranscribe and add your Gemini API key.";
        }

        ISpeechSession session;
        try
        {
            var engine = _host.Engines.Create(profile, key);
            var options = new EngineSessionOptions(settings.Dictation.Language, profile.Cleanup);
            session = await engine.StartSessionAsync(options, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _host.Log.Warn($"Note recording could not reach the engine: {ex.Message}");
            return "Could not reach the engine.";
        }

        if (_cancelRequested)
        {
            await session.DisposeAsync().ConfigureAwait(false);
            return null;
        }

        // Android silences the microphone of an app with nothing in the foreground (same reason the
        // bubble's own recording does this before DictationController.ToggleAsync).
        RecordingService.Start(_context);

        var capture = new AndroidAudioCapture(_context, _host.Log);
        try
        {
            capture.Start(
                chunk => _ = WriteChunkAsync(session, chunk),
                ex => _host.Log.Warn($"Note recording microphone stopped: {ex.Message}"));
        }
        catch (Exception ex)
        {
            _host.Log.Warn($"Note recording microphone could not start: {ex.Message}");
            RecordingService.Stop(_context);
            await session.DisposeAsync().ConfigureAwait(false);
            return "The microphone could not be opened.";
        }

        if (_cancelRequested)
        {
            _ = capture.StopAsync();
            RecordingService.Stop(_context);
            await session.DisposeAsync().ConfigureAwait(false);
            return null;
        }

        _capture = capture;
        _session = session;
        IsRecording = true;
        return null;
    }

    private static async Task WriteChunkAsync(ISpeechSession session, byte[] chunk)
    {
        try
        {
            await session.WriteAudioAsync(chunk, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Surfaced when StopAsync calls CompleteAsync below.
        }
    }

    /// <summary>Stops, transcribes, and returns the Persian text - null when there is nothing to add.</summary>
    public async Task<string?> StopAsync()
    {
        if (!IsRecording)
        {
            return null;
        }

        IsRecording = false;
        var capture = _capture!;
        var session = _session!;
        _capture = null;
        _session = null;

        try
        {
            await capture.StopAsync().ConfigureAwait(false);
            using var timeout = new CancellationTokenSource(CompletionTimeout);
            var result = await session.CompleteAsync(timeout.Token).ConfigureAwait(false);
            var text = TextPipeline.CreateDefault(_host.Settings.Current.Dictation.AllowLineBreaks).Process(result.Text);
            return text.Length == 0 ? null : text;
        }
        catch (Exception ex)
        {
            _host.Log.Warn($"Note recording failed: {ex.Message}");
            return null;
        }
        finally
        {
            await session.DisposeAsync().ConfigureAwait(false);
            RecordingService.Stop(_context);
        }
    }

    /// <summary>Discards without transcribing (the note was closed mid-recording, or while still starting).</summary>
    public void Cancel()
    {
        _cancelRequested = true;
        if (!IsRecording)
        {
            return;
        }

        IsRecording = false;
        var capture = _capture;
        var session = _session;
        _capture = null;
        _session = null;

        _ = capture?.StopAsync();
        _ = session?.DisposeAsync();
        RecordingService.Stop(_context);
    }
}
