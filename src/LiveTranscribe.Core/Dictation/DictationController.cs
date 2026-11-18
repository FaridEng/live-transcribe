using System.Diagnostics;
using System.Threading.Channels;
using LiveTranscribe.Core.Abstractions;
using LiveTranscribe.Core.Diagnostics;
using LiveTranscribe.Core.Engines;
using LiveTranscribe.Core.Security;
using LiveTranscribe.Core.Settings;
using LiveTranscribe.Core.Text;
using LiveTranscribe.Core.Translation;

namespace LiveTranscribe.Core.Dictation;

/// <summary>
/// Owns the dictation lifecycle and the FR-6.1 state machine. All public members must be called on
/// the synchronization context the controller was created on (the UI thread); engine and audio work
/// runs in the background and reports back through that context.
/// </summary>
public sealed class DictationController
{
    private static readonly TimeSpan TransientDuration = TimeSpan.FromMilliseconds(1200);
    private static readonly TimeSpan CompletionTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan HandsFreeWarningLead = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan HandsFreeCheckInterval = TimeSpan.FromMilliseconds(250);

    // Leave room for connection setup inside the engine's own session limit (FR-9.5).
    private static readonly TimeSpan EngineLimitMargin = TimeSpan.FromSeconds(20);

    // UI Automation is on the activation path, so it gets a budget that fits NFR-1.1 (150 ms to the
    // indicator). At insertion time there is no such hurry, and being sure matters more.
    private static readonly TimeSpan PasswordCheckTimeout = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan TargetCheckTimeout = TimeSpan.FromMilliseconds(900);

    // "A short timeout" in FR-9.1: long enough to fix a network or a key, short enough that spoken
    // audio does not sit in memory for long.
    private static readonly TimeSpan RetainedAudioLifetime = TimeSpan.FromMinutes(5);

    // An unanswered "Dictate anyway" is a decline (FR-3.7): long enough to read the warning, short
    // enough that a forgotten offer cannot be clicked by accident later.
    private static readonly TimeSpan PasswordOfferLifetime = TimeSpan.FromSeconds(20);

    // Silence from the very start for this long means a muted or wrong microphone (FR-7.7).
    private static readonly TimeSpan SilenceWarningAfter = TimeSpan.FromSeconds(4);

    private readonly IAudioCapture _audio;
    private readonly IAudioDeviceCatalog _devices;
    private readonly ISpeechEngineFactory _engines;
    private readonly ITextInserter _inserter;

    /// <summary>Types the text instead of pasting it, so a password never reaches the clipboard (FR-3.7).</summary>
    private readonly ITextInserter _secureInserter;

    private readonly IClipboardWriter _clipboard;
    private readonly ITargetTracker _tracker;
    private readonly ISettingsStore _settings;
    private readonly ISecretStore _secrets;
    private readonly IAppLogger _log;
    private readonly TranslationService? _translation;
    private readonly SynchronizationContext _context;

    private ActiveSession? _session;
    private RetainedAudio? _retained;
    private PasswordOffer? _passwordOffer;
    private bool _paused;
    private int _stateVersion;
    private string? _lastDeviceId;

    public DictationController(IAudioCapture audio, IAudioDeviceCatalog devices, ISpeechEngineFactory engines,
        ITextInserter inserter, IClipboardWriter clipboard, ITargetTracker tracker, ISettingsStore settings,
        ISecretStore secrets, IAppLogger log, ITextInserter? secureInserter = null,
        TranslationService? translation = null)
    {
        _audio = audio;
        _devices = devices;
        _engines = engines;
        _inserter = inserter;
        _secureInserter = secureInserter ?? inserter;
        _translation = translation;
        _clipboard = clipboard;
        _tracker = tracker;
        _settings = settings;
        _secrets = secrets;
        _log = log;
        _context = SynchronizationContext.Current
                   ?? throw new InvalidOperationException("DictationController must be created on a thread with a synchronization context.");

        State = IsConfigured() ? DictationState.Ready : DictationState.NotConfigured;

        // Remember what is current at startup, so the first dictation is not announced as a change.
        _lastDeviceId = _devices.Resolve(_settings.Current.Microphone.DeviceId)?.Id;
    }

    public DictationState State { get; private set; }

    /// <summary>How the current or last session was started.</summary>
    public ActivationMode? SessionMode => _session?.Mode;

    /// <summary>What the current session produces, so the pill can show EN while it translates.</summary>
    public DictationOutput? SessionOutput => _session?.Output;

    /// <summary>Latest dictation, kept in memory only for recovery (FR-9.3, D17).</summary>
    public string? LastDictation { get; private set; }

    /// <summary>True while there is text the user can still copy or paste (FR-9.3).</summary>
    public bool HasRecoverableText => !string.IsNullOrEmpty(LastDictation);

    /// <summary>
    /// The Persian behind <see cref="LastDictation"/> when that was a translation, or null. Kept in memory
    /// only and replaced with it, so a mishearing can be seen and the Persian used instead (T14).
    /// </summary>
    public string? LastHeard { get; private set; }

    public bool HasHeard => !string.IsNullOrEmpty(LastHeard);

    /// <summary>A translation was delivered; the argument is the Persian the engine heard. Raised on the UI thread.</summary>
    public event Action<string>? TranslationHeard;

    /// <summary>Why the last Error: microphone happened, so the message can say what to do.</summary>
    public MicrophoneFailure? MicrophoneIssue { get; private set; }

    /// <summary>Audio from a failed dictation is in memory and can be sent again (FR-9.1).</summary>
    public bool CanRetry => _retained is not null;

    /// <summary>
    /// A password field refused the last activation and the "Dictate anyway" offer is still open
    /// (FR-3.7). It covers one dictation only and expires on its own.
    /// </summary>
    public bool CanOverridePassword => _passwordOffer is not null;

    public event Action<DictationState>? StateChanged;

    /// <summary><see cref="CanRetry"/> changed: audio was kept, sent, replaced or expired.</summary>
    public event Action? RetryAvailabilityChanged;

    /// <summary><see cref="CanOverridePassword"/> changed: the offer appeared, was used or expired.</summary>
    public event Action? PasswordOverrideChanged;

    /// <summary>One-line news that is not a state change. Raised on the UI thread.</summary>
    public event Action<DictationNotice>? Notice;

    /// <summary>Announces a fallback to another engine profile, with its data terms (FR-13.8, FR-13.9).</summary>
    public event Action<string>? EngineSwitched;

    /// <summary>Microphone loudness 0..1 roughly every 100 ms. Raised on a background thread.</summary>
    public event Action<float>? LevelChanged;

    /// <summary>A hands-free session is about to stop at its maximum length (FR-9.5).</summary>
    public event Action<TimeSpan>? HandsFreeWarning;

    /// <summary>
    /// The microphone in use has changed since the last dictation, or the saved one was missing and
    /// the default stepped in. Raised as recording starts so the user is never left guessing which
    /// device is live (FR-7.3).
    /// </summary>
    public event Action<AudioDevice, bool>? MicrophoneChanged;

    /// <summary>Re-evaluates Ready vs Not configured after settings change.</summary>
    public void RefreshConfiguration()
    {
        if (State is DictationState.Ready or DictationState.NotConfigured)
        {
            SetState(RestingState());
        }
    }

    public void SetPaused(bool paused)
    {
        _paused = paused;
        if (paused)
        {
            // A recording in progress is discarded; a transcript already processing still lands.
            if (_session is not null && State == DictationState.Listening)
            {
                _ = AbortAsync(_session);
            }

            SetState(DictationState.Paused);
        }
        else if (State == DictationState.Paused)
        {
            SetState(RestingState());
        }
    }

    /// <summary>Hold key pressed.</summary>
    public void BeginHold(DictationOutput output = DictationOutput.Transcript) => Activate(ActivationMode.Hold, output, passwordOverride: false);

    /// <summary>Hold key released: only ends a session that the hold key started.</summary>
    public Task EndHoldAsync() =>
        _session?.Mode == ActivationMode.Hold ? StopAsync() : Task.CompletedTask;

    /// <summary>Toggle shortcut, floating control click, or tray menu: start, or stop what is running.</summary>
    public Task ToggleAsync(DictationOutput output = DictationOutput.Transcript)
    {
        if (State == DictationState.Listening)
        {
            return StopAsync();
        }

        Activate(ActivationMode.Toggle, output, passwordOverride: false);
        return Task.CompletedTask;
    }

    public void Activate(ActivationMode mode) => Activate(mode, DictationOutput.Transcript, passwordOverride: false);

    private void Activate(ActivationMode mode, DictationOutput output, bool passwordOverride)
    {
        switch (State)
        {
            case DictationState.Listening:
            case DictationState.Paused:
                return;
            case DictationState.Processing:
            case DictationState.Busy:
                // One dictation at a time (D6).
                ShowTransient(DictationState.Busy, then: DictationState.Processing);
                return;
        }

        var profile = _settings.Current.ActiveProfile;
        var key = profile is null ? null : _secrets.Get(profile.SecretRef);
        if (profile is null || string.IsNullOrWhiteSpace(key))
        {
            SetState(DictationState.NotConfigured);
            return;
        }

        // The target is locked here and checked again before insertion (FR-1.1, D5).
        var session = new ActiveSession(mode)
        {
            Target = _tracker.Capture(),
            PasswordOverride = passwordOverride,
            Output = _translation is null ? DictationOutput.Transcript : output
        };
        _session = session;

        // Any open offer belongs to the activation that was refused, not to this one (FR-3.7).
        ClearPasswordOffer();

        try
        {
            _audio.PreferredDeviceId = _settings.Current.Microphone.DeviceId;
            _audio.Start(
                chunk => OnChunk(session, chunk),
                ex => _context.Post(_ => OnCaptureFailed(session, ex), null));
        }
        catch (Exception ex)
        {
            MicrophoneIssue = (ex as MicrophoneException)?.Reason ?? MicrophoneFailure.Unavailable;
            _log.Error($"Microphone could not start ({MicrophoneIssue}).", ex);
            _session = null;
            session.Dispose();
            SetState(DictationState.ErrorMicrophone);
            return;
        }

        SetState(DictationState.Listening);
        AnnounceMicrophoneIfChanged();

        // Connect while audio is already buffering, so network setup never clips speech (NFR-1.3).
        var engine = CreateEngine(profile, key, session.Output, session.Target);
        var options = new EngineSessionOptions(_settings.Current.Dictation.Language, profile.Cleanup);
        session.Work = RunEngineAsync(session, engine, options);
        _ = WatchForEarlyFailureAsync(session);

        if (mode == ActivationMode.Toggle)
        {
            _ = WatchHandsFreeAsync(session, engine.Capabilities.MaxSessionLength);
        }
    }

    public Task StopAsync() => FinishAsync(microphoneLost: false);

    /// <summary>
    /// Ends listening and turns the recording into inserted text. Also runs when the microphone
    /// disappears mid-dictation, because what was said before that is still worth having (FR-7.4).
    /// </summary>
    private async Task FinishAsync(bool microphoneLost)
    {
        var session = _session;
        if (session is null || State != DictationState.Listening)
        {
            return;
        }

        if (!microphoneLost && session.Mode == ActivationMode.Hold &&
            session.Elapsed.TotalMilliseconds < _settings.Current.Hotkeys.MinimumHoldMilliseconds)
        {
            // Accidental tap: discard silently (FR-2.6).
            await AbortAsync(session);
            _log.Info("Short press discarded.");
            SetState(RestingState());
            return;
        }

        SetState(DictationState.Processing);
        var spoken = session.Elapsed;

        // A new dictation replaces whatever an earlier failure kept for retry (FR-9.1).
        ClearRetained();

        try
        {
            await _audio.StopAsync();
            session.Audio.Writer.TryComplete();

            var speech = await session.Work!;
            TranscriptResult result;
            try
            {
                using var timeout = new CancellationTokenSource(CompletionTimeout);
                result = await speech.CompleteAsync(timeout.Token);
            }
            finally
            {
                await speech.DisposeAsync();
            }

            await DeliverAsync(result, session.Target,
                $"{spoken.TotalSeconds:F1} s of speech ({session.Mode}{(session.PasswordOverride ? ", password override" : "")})",
                session.HeardSound, session.PasswordOverride, session.Output);
        }
        catch (TargetBlockedException)
        {
            _log.Info("Dictation discarded: the caret was in a password field.");
            ShowTransient(DictationState.BlockedPasswordField);
            OfferPasswordOverride(session.Target);
        }
        catch (Exception ex)
        {
            Fail(ex, Keep(session), "after end of speech");
        }
        finally
        {
            if (_session == session)
            {
                _session = null;
            }

            session.Dispose();
        }
    }

    /// <summary>
    /// The user accepted "Dictate anyway" on a password field (FR-3.7, D7). It starts one hands-free
    /// dictation that may go into the field: nothing was recorded while the offer was open, the text is
    /// typed rather than pasted, and it is neither kept as the last dictation nor retained for retry.
    /// The next password field asks again; there is no "don't ask again".
    /// </summary>
    public Task DictateIntoPasswordFieldAsync()
    {
        if (_passwordOffer is null)
        {
            return Task.CompletedTask;
        }

        _log.Info("Password override accepted for one dictation (FR-3.7).");
        ClearPasswordOffer();

        // Hands-free: the hold key was released long before the offer was answered.
        Activate(ActivationMode.Toggle, DictationOutput.Transcript, passwordOverride: true);
        return Task.CompletedTask;
    }

    /// <summary>Opens the one-dictation offer and lets it expire on its own if nothing answers it.</summary>
    private void OfferPasswordOverride(TargetSnapshot? target)
    {
        var offer = new PasswordOffer(target);
        _passwordOffer = offer;
        PasswordOverrideChanged?.Invoke();
        _ = ExpirePasswordOfferAsync(offer);
    }

    private async Task ExpirePasswordOfferAsync(PasswordOffer offer)
    {
        await Task.Delay(PasswordOfferLifetime);
        if (ReferenceEquals(_passwordOffer, offer))
        {
            _log.Info("Password override offer expired unanswered.");
            ClearPasswordOffer();
        }
    }

    /// <summary>Declining, ignoring, or starting anything else cancels the offer (FR-3.7).</summary>
    public void ClearPasswordOffer()
    {
        if (_passwordOffer is null)
        {
            return;
        }

        _passwordOffer = null;
        PasswordOverrideChanged?.Invoke();
    }

    /// <summary>
    /// Sends the audio kept from a failed dictation again, so the user does not have to repeat it
    /// (FR-9.1). Where the text lands follows the focus rule of the original dictation, checked now
    /// (FR-9.2, FR-3.6).
    /// </summary>
    public Task RetryAsync()
    {
        var retained = _retained;
        if (retained is null || State is DictationState.Listening or DictationState.Processing
                or DictationState.Busy or DictationState.Paused)
        {
            return Task.CompletedTask;
        }

        return SendAgainAsync(retained, _settings.Current.ActiveProfile, "on retry");
    }

    /// <summary>
    /// Transcribes audio that is already recorded, with whichever profile is named: the user's Retry
    /// uses the active one, an automatic fallback uses the secondary one (FR-9.1, FR-13.8).
    /// </summary>
    private async Task SendAgainAsync(RetainedAudio retained, EngineProfile? profile, string what)
    {
        var key = profile is null ? null : _secrets.Get(profile.SecretRef);
        if (profile is null || string.IsNullOrWhiteSpace(key))
        {
            SetState(DictationState.NotConfigured);
            return;
        }

        SetState(DictationState.Processing);
        var spoken = retained.Duration;

        try
        {
            var engine = CreateEngine(profile, key, retained.Output, retained.Target);
            var options = new EngineSessionOptions(_settings.Current.Dictation.Language, profile.Cleanup);

            // The engine works through sent audio at about four times real time (T3).
            using var timeout = new CancellationTokenSource(CompletionTimeout + spoken / 2);
            var speech = await engine.StartSessionAsync(options, timeout.Token);
            TranscriptResult result;
            try
            {
                // Sent as fast as the connection takes it: Gemini Live accepts that and returns the
                // same text as a real-time stream (verified 2026-09-18, T3).
                foreach (var chunk in retained.Chunks)
                {
                    await speech.WriteAudioAsync(chunk, timeout.Token);
                }

                result = await speech.CompleteAsync(timeout.Token);
            }
            finally
            {
                await speech.DisposeAsync();
            }

            // The words are text now; from here the recovery path keeps them (FR-9.3).
            ClearRetained();
            await DeliverAsync(result, retained.Target,
                $"{what} of {spoken.TotalSeconds:F1} s of kept audio with '{profile.Name}'", heardSound: true, output: retained.Output);
        }
        catch (Exception ex)
        {
            Fail(ex, retained, what, allowFallback: false);
        }
    }

    /// <summary>Discards the current recording: nothing is sent for transcription or inserted (FR-2.7).</summary>
    public async Task CancelAsync()
    {
        var session = _session;
        if (session is null || State != DictationState.Listening)
        {
            return;
        }

        await AbortAsync(session);
        _log.Info("Dictation cancelled.");
        ShowTransient(DictationState.Cancelled);
    }

    /// <summary>
    /// Recovery path: puts the kept text on the clipboard so the user can paste it themselves
    /// (FR-9.3). Returns false when there is nothing to recover.
    /// </summary>
    public bool CopyLastDictation() => CopyKept(LastDictation);

    /// <summary>Puts the Persian behind the last translation on the clipboard (T14).</summary>
    public bool CopyLastHeard() => CopyKept(LastHeard);

    private bool CopyKept(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        try
        {
            _clipboard.SetText(text);
            _log.Info($"Kept dictation of {text.Length} chars copied to the clipboard.");
            return true;
        }
        catch (Exception ex)
        {
            _log.Error("Could not copy the kept dictation.", ex);
            return false;
        }
    }

    /// <summary>
    /// Recovery path: inserts the kept text wherever the caret is now (FR-9.3). Nothing is locked
    /// here because the user is pointing at the destination, but a password field still refuses
    /// the text (FR-3.7, FR-9.2).
    /// </summary>
    public Task<bool> PasteLastDictationAsync() => PasteKeptAsync(LastDictation);

    /// <summary>Inserts the Persian behind the last translation at the caret, when the English went wrong (T14).</summary>
    public Task<bool> PasteLastHeardAsync() => PasteKeptAsync(LastHeard);

    private async Task<bool> PasteKeptAsync(string? text)
    {
        if (string.IsNullOrEmpty(text) || State is DictationState.Listening or DictationState.Processing)
        {
            return false;
        }

        if (await _tracker.CheckAsync(null, TargetCheckTimeout, CancellationToken.None) == TargetCheck.PasswordField)
        {
            ShowTransient(DictationState.BlockedPasswordField);
            return false;
        }

        var inserted = await _inserter.InsertAsync(text, CancellationToken.None);
        if (inserted.Success)
        {
            ShowTransient(DictationState.Inserted);
            return true;
        }

        _log.Warn($"Recovery insertion failed: {inserted.Failure}");
        SetState(DictationState.NotInsertedTextKept);
        return false;
    }

    /// <summary>Cleans up the transcript and puts it where the dictation started, if that is still safe.</summary>
    private async Task DeliverAsync(TranscriptResult result, TargetSnapshot? target, string what, bool heardSound,
        bool passwordOverride = false, DictationOutput output = DictationOutput.Transcript)
    {
        var text = TextPipeline.CreateDefault(_settings.Current.Dictation.AllowLineBreaks).Process(result.Text);

        // Timings and lengths only: transcripts never go to the log (FR-11.5).
        _log.Info($"{(output == DictationOutput.English ? "Translated" : "Transcribed")} {what} into {text.Length} chars; final text {result.ElapsedAfterEndOfSpeech.TotalMilliseconds:F0} ms after end of speech.");

        if (text.Length == 0)
        {
            ShowTransient(DictationState.NoSpeech);
            if (!heardSound)
            {
                // "No speech" alone would hide the likely cause: nothing came from the microphone.
                Notice?.Invoke(DictationNotice.MicrophoneSilent);
            }

            return;
        }

        if (passwordOverride)
        {
            await DeliverPasswordAsync(text, target);
            return;
        }

        // Kept before anything can go wrong, so the words are never lost (principle 1, FR-9.3).
        LastDictation = text;
        LastHeard = output == DictationOutput.English && !string.IsNullOrWhiteSpace(result.Heard)
            ? TextPipeline.CreateDefault(_settings.Current.Dictation.AllowLineBreaks).Process(result.Heard)
            : null;

        var check = await _tracker.CheckAsync(target, TargetCheckTimeout, CancellationToken.None);
        if (check is TargetCheck.DifferentWindow or TargetCheck.PasswordField)
        {
            // Not inserting is better than inserting in the wrong place (principle 2, D5, FR-3.7).
            _log.Info($"Not inserted: target is {check}. The text is kept for recovery.");
            SetState(DictationState.NotInsertedTextKept);
            return;
        }

        var inserted = await _inserter.InsertAsync(text, CancellationToken.None);
        if (check == TargetCheck.NoTextField)
        {
            DeliverToClipboard(text, inserted);
            return;
        }

        if (inserted.Success)
        {
            if (output == DictationOutput.English)
            {
                // What reached the field is context for the next translation (architecture T14).
                _translation?.Remember(text, target);
            }

            ShowTransient(DictationState.Inserted);

            // After the state change, so the bubble keeps the Persian instead of losing it to "Inserted".
            if (LastHeard is { } heard)
            {
                TranslationHeard?.Invoke(heard);
            }
        }
        else
        {
            _log.Warn($"Insertion failed: {inserted.Failure}");
            SetState(DictationState.NotInsertedTextKept);
        }
    }

    /// <summary>
    /// No text field had focus (T13, the user's request of 2026-09-21): the dictation goes to the clipboard and
    /// a short notice says so, for a manual paste. The paste was still tried first, because some apps take one
    /// without exposing a text field; when it failed, that was expected and is not an error.
    /// </summary>
    private void DeliverToClipboard(string text, InsertResult inserted)
    {
        if (!CopyKept(text))
        {
            if (inserted.Success)
            {
                ShowTransient(DictationState.Inserted);
            }
            else
            {
                SetState(DictationState.NotInsertedTextKept);
            }

            return;
        }

        // The «شنیدم» line of a translation is not shown here: it would replace the notice. The Persian stays
        // available from the menu.
        _log.Info($"No text field had focus; the dictation is on the clipboard (paste {(inserted.Success ? "sent too" : "not possible")}).");
        SetState(RestingState());
        Notice?.Invoke(DictationNotice.CopiedToClipboard);
    }

    /// <summary>
    /// The confirmed password dictation (FR-3.7): the password field it was meant for is the only place
    /// it may go, it is typed rather than pasted so it never reaches the clipboard, and it is thrown
    /// away rather than kept whenever it cannot be inserted. Nothing about it is logged but its length.
    /// </summary>
    private async Task DeliverPasswordAsync(string text, TargetSnapshot? target)
    {
        // The field itself is a password field, so only a move to another window stops the insertion.
        var check = await _tracker.CheckAsync(target, TargetCheckTimeout, CancellationToken.None);
        if (check == TargetCheck.DifferentWindow)
        {
            Discard("the foreground window changed");
            return;
        }

        var inserted = await _secureInserter.InsertAsync(text, CancellationToken.None);
        if (inserted.Success)
        {
            ShowTransient(DictationState.Inserted);
            return;
        }

        Discard($"insertion failed ({inserted.Failure})");

        void Discard(string why)
        {
            _log.Info($"Password dictation of {text.Length} chars discarded: {why} (FR-3.7).");
            Notice?.Invoke(DictationNotice.PasswordTextDiscarded);
            SetState(RestingState());
        }
    }

    /// <summary>Shows the error for a failure and keeps the audio, when there is any, for retry (FR-9.1).</summary>
    private void Fail(Exception ex, RetainedAudio? audio, string when, bool allowFallback = true)
    {
        DictationState state;
        switch (ex)
        {
            case EngineException engineError:
                _log.Error($"Engine error ({engineError.Kind}) {when}.", ex);
                state = ToState(engineError.Kind);
                break;
            case OperationCanceledException:
                _log.Error($"No transcript in time {when}.");
                state = DictationState.ErrorNetwork;
                break;
            case MicrophoneException microphoneError:
                _log.Error($"Microphone failed {when}.", ex);
                MicrophoneIssue = microphoneError.Reason;
                state = DictationState.ErrorMicrophone;
                break;
            default:
                _log.Error($"Dictation failed {when}.", ex);
                state = DictationState.ErrorTranscription;
                break;
        }

        if (audio is { Chunks.Count: > 0 })
        {
            Retain(audio);
            if (allowFallback && ex is EngineException failure && TryFallback(audio, failure))
            {
                return;
            }
        }

        SetState(state);
    }

    /// <summary>
    /// Sends the recording to the secondary profile when the active one cannot do it (FR-13.8). It is
    /// opt-in, it runs only for failures another engine could survive, and it says out loud which
    /// profile took over, because a different profile can mean different data-use terms (FR-13.9).
    /// </summary>
    private bool TryFallback(RetainedAudio audio, EngineException failure)
    {
        // Fallback profiles are dictation engines; a translation is retried with Retry instead.
        if (audio.Output == DictationOutput.English)
        {
            return false;
        }

        if (failure.Kind is not (EngineErrorKind.Quota or EngineErrorKind.Network
            or EngineErrorKind.Authentication or EngineErrorKind.ModelNotFound))
        {
            return false;
        }

        var settings = _settings.Current;
        var fallback = settings.EngineProfiles.FirstOrDefault(p => p.Id == settings.FallbackEngineProfileId);
        if (fallback is null || fallback.Id == settings.ActiveProfile?.Id ||
            string.IsNullOrWhiteSpace(_secrets.Get(fallback.SecretRef)))
        {
            return false;
        }

        _log.Info($"Falling back to profile '{fallback.Name}' after a {failure.Kind} failure.");
        EngineSwitched?.Invoke($"{settings.ActiveProfile?.Name ?? "The engine"} failed - trying {fallback.Name}. {fallback.PrivacyNote}");
        _ = SendAgainAsync(audio, fallback, "fallback");
        return true;
    }

    /// <summary>
    /// The recording, if it may be sent again. Audio from a session that never passed the password
    /// check is not kept, because a retry would send it to the engine (FR-3.7, D7).
    /// </summary>
    private static RetainedAudio? Keep(ActiveSession session) =>
        session.SafeToSend && !session.PasswordOverride
            ? new RetainedAudio(session.TakeRecording(), session.Target, session.Output)
            : null;

    private void Retain(RetainedAudio audio)
    {
        if (ReferenceEquals(_retained, audio))
        {
            return;
        }

        // In memory only, never on disk (FR-9.1, FR-11.4).
        _retained = audio;
        _log.Info($"Kept {audio.Duration.TotalSeconds:F1} s of audio in memory for retry.");
        RetryAvailabilityChanged?.Invoke();
        _ = ExpireAsync(audio);
    }

    private async Task ExpireAsync(RetainedAudio audio)
    {
        await Task.Delay(RetainedAudioLifetime);
        if (ReferenceEquals(_retained, audio))
        {
            _log.Info("Kept audio for retry expired.");
            ClearRetained();
        }
    }

    private void ClearRetained()
    {
        if (_retained is null)
        {
            return;
        }

        _retained = null;
        RetryAvailabilityChanged?.Invoke();
    }

    /// <summary>
    /// Says which microphone is live, but only when that has changed since the last dictation, or
    /// when the saved one was missing. Announcing every time would be noise (FR-7.3).
    /// </summary>
    private void AnnounceMicrophoneIfChanged()
    {
        if (_audio.ActiveDevice is not { } device)
        {
            return;
        }

        var changed = device.Id != _lastDeviceId;
        _lastDeviceId = device.Id;

        if (changed || _audio.UsedFallback)
        {
            MicrophoneChanged?.Invoke(device, _audio.UsedFallback);
        }
    }

    /// <summary>Runs on the capture thread for every ~100 ms chunk.</summary>
    private void OnChunk(ActiveSession session, byte[] chunk)
    {
        session.Audio.Writer.TryWrite(chunk);
        session.Record(chunk);

        var level = AudioLevel.Rms(chunk);
        if (session.NoiseFloor.Add(level))
        {
            session.MarkVoice();
        }

        if (level >= AudioLevel.SilenceLevel)
        {
            session.MarkSound();
        }
        else if (!session.HeardSound && !session.SilenceWarned && session.Elapsed >= SilenceWarningAfter)
        {
            // Said once, while there is still time to fix it (FR-7.7).
            session.SilenceWarned = true;
            _context.Post(_ =>
            {
                if (_session == session && State == DictationState.Listening)
                {
                    _log.Warn("No sound from the microphone.");
                    Notice?.Invoke(DictationNotice.MicrophoneSilent);
                }
            }, null);
        }

        LevelChanged?.Invoke(level);
    }

    /// <summary>
    /// The dictation engine, or for an English dictation one that answers in English with the context of the
    /// target it is going to (architecture T14). Retry and a translated dictation go the same way.
    /// </summary>
    private ISpeechEngine CreateEngine(EngineProfile profile, string key, DictationOutput output, TargetSnapshot? target) =>
        output == DictationOutput.English && _translation is not null
            ? _translation.CreateEngine(profile, key, _translation.ContextFor(target))
            : _engines.Create(profile, key);

    private async Task<ISpeechSession> RunEngineAsync(ActiveSession session, ISpeechEngine engine, EngineSessionOptions options)
    {
        // The microphone is already recording, so no syllable is clipped (NFR-1.3), but the audio stays
        // inside this process until the target is known not to be a password field: a spoken password
        // must never reach the engine (FR-3.7, D7).
        // A confirmed override skips the check: the user has already been told and said yes (FR-3.7).
        if (!session.PasswordOverride &&
            await _tracker.IsPasswordFieldAsync(PasswordCheckTimeout, session.Cancellation.Token).ConfigureAwait(false))
        {
            throw new TargetBlockedException();
        }

        session.SafeToSend = true;
        var speech = await engine.StartSessionAsync(options, session.Cancellation.Token).ConfigureAwait(false);
        session.Speech = speech;

        await foreach (var chunk in session.Audio.Reader.ReadAllAsync(session.Cancellation.Token).ConfigureAwait(false))
        {
            await speech.WriteAudioAsync(chunk, session.Cancellation.Token).ConfigureAwait(false);
        }

        return speech;
    }

    /// <summary>Hands-free safety: maximum length with a warning, and optional stop after silence (FR-9.5, FR-8).</summary>
    private async Task WatchHandsFreeAsync(ActiveSession session, TimeSpan engineLimit)
    {
        var dictation = _settings.Current.Dictation;
        var configured = TimeSpan.FromSeconds(Math.Max(30, dictation.MaxSessionSeconds));
        var limit = configured < engineLimit - EngineLimitMargin ? configured : engineLimit - EngineLimitMargin;
        var warned = false;

        while (_session == session && State == DictationState.Listening)
        {
            await Task.Delay(HandsFreeCheckInterval);
            if (_session != session || State != DictationState.Listening)
            {
                return;
            }

            var remaining = limit - session.Elapsed;
            if (!warned && remaining <= HandsFreeWarningLead)
            {
                warned = true;
                HandsFreeWarning?.Invoke(remaining);
            }

            if (remaining <= TimeSpan.Zero)
            {
                _log.Info("Hands-free session reached its maximum length.");
                await StopAsync();
                return;
            }

            if (dictation.StopOnSilence && session.SinceVoice.TotalSeconds >= dictation.SilenceStopSeconds)
            {
                _log.Info($"Hands-free session stopped after silence (noise floor {AudioLevel.ToDecibels(session.NoiseFloor.Floor ?? 0):F0} dBFS).");
                await StopAsync();
                return;
            }
        }
    }

    /// <summary>A bad key or a network failure should surface while the user is still speaking.</summary>
    private async Task WatchForEarlyFailureAsync(ActiveSession session)
    {
        try
        {
            await session.Work!;
        }
        catch (TargetBlockedException) when (_session == session && State == DictationState.Listening)
        {
            _log.Info("Dictation blocked: the caret is in a password field.");
            await AbortAsync(session);
            ShowTransient(DictationState.BlockedPasswordField);
            OfferPasswordOverride(session.Target);
        }
        catch (Exception ex) when (_session == session && State == DictationState.Listening)
        {
            // Listening ends, but what was said so far stays available for retry (FR-9.1).
            await AbortAsync(session);
            var failure = ex is EngineException or MicrophoneException
                ? ex
                : new EngineException(EngineErrorKind.Network, "The engine failed while listening.", ex);
            Fail(failure, Keep(session), "while listening");
        }
        catch
        {
            // Failures after Listening are handled by FinishAsync or were caused by an abort.
        }
    }

    private void OnCaptureFailed(ActiveSession session, Exception ex)
    {
        if (_session != session || State != DictationState.Listening)
        {
            return;
        }

        var reason = (ex as MicrophoneException)?.Reason ?? MicrophoneFailure.Disconnected;
        _log.Error($"Microphone stopped unexpectedly ({reason}).", ex);

        if (session.HeardSound)
        {
            // Listening stops, and what was captured before the device went is transcribed (FR-7.4).
            var finishing = FinishAsync(microphoneLost: true);
            Notice?.Invoke(DictationNotice.MicrophoneLostTranscribing);
            _ = finishing;
            return;
        }

        // The error shows at once; the abort releases the rest in the background.
        MicrophoneIssue = reason;
        var aborting = AbortAsync(session);
        SetState(DictationState.ErrorMicrophone);
        _ = aborting;
    }

    private async Task AbortAsync(ActiveSession session)
    {
        if (_session == session)
        {
            _session = null;
        }

        session.Cancel();
        session.Audio.Writer.TryComplete();

        try
        {
            await _audio.StopAsync();
        }
        catch (Exception ex)
        {
            _log.Warn($"Microphone stop failed during abort: {ex.Message}");
        }

        if (session.Speech is not null)
        {
            try
            {
                await session.Speech.DisposeAsync();
            }
            catch
            {
                // The session is being thrown away.
            }
        }

        session.Dispose();
    }

    private void ShowTransient(DictationState transient, DictationState? then = null)
    {
        SetState(transient);
        var version = _stateVersion;
        _ = ReturnAfterDelayAsync(version, then);
    }

    private async Task ReturnAfterDelayAsync(int version, DictationState? then)
    {
        await Task.Delay(TransientDuration);
        if (_stateVersion == version)
        {
            SetState(then ?? RestingState());
        }
    }

    private void SetState(DictationState state)
    {
        _stateVersion++;
        if (State == state)
        {
            return;
        }

        State = state;
        StateChanged?.Invoke(state);
    }

    private DictationState RestingState() =>
        _paused ? DictationState.Paused : IsConfigured() ? DictationState.Ready : DictationState.NotConfigured;

    private bool IsConfigured()
    {
        var profile = _settings.Current.ActiveProfile;
        return profile is not null && _secrets.Has(profile.SecretRef);
    }

    private static DictationState ToState(EngineErrorKind kind) => kind switch
    {
        EngineErrorKind.Authentication => DictationState.ErrorAuthentication,
        EngineErrorKind.Quota => DictationState.ErrorQuota,
        EngineErrorKind.Network => DictationState.ErrorNetwork,
        _ => DictationState.ErrorTranscription
    };

    /// <summary>Raised inside the session pipeline when the target turns out to be a password field.</summary>
    /// <summary>An open "Dictate anyway" offer and the field it was refused for (FR-3.7).</summary>
    private sealed record PasswordOffer(TargetSnapshot? Target);

    private sealed class TargetBlockedException : Exception
    {
        public TargetBlockedException() : base("The caret is in a password field.")
        {
        }
    }

    /// <summary>The audio of a failed dictation and where it was meant to go (FR-9.1, FR-9.2).</summary>
    private sealed record RetainedAudio(IReadOnlyList<byte[]> Chunks, TargetSnapshot? Target, DictationOutput Output)
    {
        public TimeSpan Duration { get; } = TimeSpan.FromSeconds(Chunks.Sum(c => c.Length) / 32000.0);
    }

    private sealed class ActiveSession : IDisposable
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly List<byte[]> _recording = new();
        private long _lastVoiceTicks;
        private int _heardSound;
        private int _disposed;
        private volatile bool _safeToSend;

        public ActiveSession(ActivationMode mode)
        {
            Mode = mode;
        }

        public ActivationMode Mode { get; }

        /// <summary>The window the dictation was started in, or null when nothing could be locked.</summary>
        public TargetSnapshot? Target { get; init; }

        /// <summary>The user confirmed one dictation into a password field (FR-3.7).</summary>
        public bool PasswordOverride { get; init; }

        /// <summary>The words as spoken, or their English translation (architecture T14).</summary>
        public DictationOutput Output { get; init; }

        public Channel<byte[]> Audio { get; } = Channel.CreateUnbounded<byte[]>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

        public CancellationTokenSource Cancellation { get; } = new();

        public Task<ISpeechSession>? Work { get; set; }

        public ISpeechSession? Speech { get; set; }

        /// <summary>Set once the password check has passed, so the audio may go to the engine (D7).</summary>
        public bool SafeToSend
        {
            get => _safeToSend;
            set => _safeToSend = value;
        }

        /// <summary>Only touched on the capture thread.</summary>
        public NoiseFloorTracker NoiseFloor { get; } = new();

        /// <summary>Only touched on the capture thread.</summary>
        public bool SilenceWarned { get; set; }

        /// <summary>Anything louder than digital silence arrived (FR-7.7).</summary>
        public bool HeardSound => Volatile.Read(ref _heardSound) == 1;

        public TimeSpan Elapsed => _clock.Elapsed;

        public void MarkSound() => Volatile.Write(ref _heardSound, 1);

        /// <summary>A copy of every chunk, in memory only, so a failure can be retried (FR-9.1).</summary>
        public void Record(byte[] chunk)
        {
            lock (_recording)
            {
                _recording.Add(chunk);
            }
        }

        public IReadOnlyList<byte[]> TakeRecording()
        {
            lock (_recording)
            {
                return _recording.ToArray();
            }
        }

        public TimeSpan SinceVoice => _clock.Elapsed - TimeSpan.FromTicks(Interlocked.Read(ref _lastVoiceTicks));

        public void MarkVoice() => Interlocked.Exchange(ref _lastVoiceTicks, _clock.Elapsed.Ticks);

        public void Cancel()
        {
            if (Volatile.Read(ref _disposed) == 0)
            {
                Cancellation.Cancel();
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                Cancellation.Dispose();
            }
        }
    }
}
