using LiveTranscribe.Core.Abstractions;
using LiveTranscribe.Core.Diagnostics;
using LiveTranscribe.Core.Dictation;
using LiveTranscribe.Core.Engines;
using LiveTranscribe.Core.Security;
using LiveTranscribe.Core.Settings;
using LiveTranscribe.Core.Translation;
using Xunit;

namespace LiveTranscribe.Core.Tests;

/// <summary>Retry with kept audio (FR-9.1, FR-9.2) and a microphone lost mid-dictation (FR-7.4).</summary>
public class DictationRecoveryTests
{
    private readonly FakeCapture _capture = new();
    private readonly FakeEngines _engines = new();
    private readonly FakeTracker _tracker = new();
    private readonly FakeInserter _inserter = new();
    private readonly FakeInserter _secure = new();
    private readonly DictationController _controller;
    private readonly List<DictationNotice> _notices = new();
    private readonly FakeSettings _settings = new();
    private readonly FakeSecrets _secrets = new();
    private readonly FakeTranslators _translators = new();
    private readonly TranslationService _translation;

    public DictationRecoveryTests()
    {
        SynchronizationContext.SetSynchronizationContext(new InlineContext());
        _secrets.Set(_settings.Current.ActiveProfile!.SecretRef, "test-key");

        _translation = new TranslationService(_translators, _settings);
        _controller = new DictationController(_capture, new FakeDevices(), _engines, _inserter, _inserter,
            _tracker, _settings, _secrets, new NullLog(), _secure, _translation);
        _controller.Notice += _notices.Add;
    }

    [Fact]
    public async Task Network_failure_keeps_the_audio_and_retry_sends_it_again()
    {
        _engines.Outcomes.Enqueue(_ => throw new EngineException(EngineErrorKind.Network, "offline"));
        _engines.Outcomes.Enqueue(_ => "hello world");

        _controller.Activate(ActivationMode.Toggle);
        var spoken = Speak(3);
        await _controller.StopAsync();

        Assert.Equal(DictationState.ErrorNetwork, _controller.State);
        Assert.True(_controller.CanRetry);
        Assert.Null(_inserter.Inserted);

        await _controller.RetryAsync();

        Assert.Equal(DictationState.Inserted, _controller.State);
        Assert.Contains("hello world", _inserter.Inserted);
        Assert.False(_controller.CanRetry);
        Assert.Equal(spoken, _engines.Received[1]);
    }

    [Fact]
    public async Task A_quota_failure_falls_back_to_the_second_profile_and_says_so()
    {
        var fallback = AddFallbackProfile();
        _engines.Outcomes.Enqueue(_ => throw new EngineException(EngineErrorKind.Quota, "quota"));
        _engines.Outcomes.Enqueue(_ => "hello world");

        var announcements = new List<string>();
        _controller.EngineSwitched += announcements.Add;

        _controller.Activate(ActivationMode.Toggle);
        var spoken = Speak(3);
        await _controller.StopAsync();

        Assert.Equal(DictationState.Inserted, _controller.State);
        Assert.Contains("hello world", _inserter.Inserted);
        Assert.Equal(spoken, _engines.Received[1]);

        // Never silent: the name of the profile that took over and its data terms are both shown.
        var announced = Assert.Single(announcements);
        Assert.Contains(fallback.Name, announced);
        Assert.Contains(fallback.PrivacyNote, announced);
    }

    [Fact]
    public async Task A_fallback_that_fails_too_stops_there_and_keeps_the_audio()
    {
        AddFallbackProfile();
        _engines.Outcomes.Enqueue(_ => throw new EngineException(EngineErrorKind.Quota, "quota"));
        _engines.Outcomes.Enqueue(_ => throw new EngineException(EngineErrorKind.Network, "offline"));

        _controller.Activate(ActivationMode.Toggle);
        Speak(2);
        await _controller.StopAsync();

        // Two attempts, not a loop between the two profiles.
        Assert.Equal(2, _engines.Received.Count);
        Assert.Equal(DictationState.ErrorNetwork, _controller.State);
        Assert.True(_controller.CanRetry);
    }

    [Fact]
    public async Task Text_that_the_engine_could_not_transcribe_does_not_fall_back()
    {
        AddFallbackProfile();
        _engines.Outcomes.Enqueue(_ => throw new EngineException(EngineErrorKind.Transcription, "bad audio"));

        _controller.Activate(ActivationMode.Toggle);
        Speak(2);
        await _controller.StopAsync();

        // A second engine would make the same nothing of it, so the quota is not spent twice.
        Assert.Single(_engines.Received);
        Assert.Equal(DictationState.ErrorTranscription, _controller.State);
    }

    [Fact]
    public async Task An_English_dictation_is_translated_and_remembered_for_the_next_one()
    {
        _translators.Outcomes.Enqueue(_ => "Hello there.");
        _translators.Outcomes.Enqueue(_ => "How are you?");

        await _controller.ToggleAsync(DictationOutput.English);
        var spoken = Speak(3);
        await _controller.StopAsync();

        Assert.Equal(DictationState.Inserted, _controller.State);
        Assert.Equal("Hello there.", _inserter.Inserted);
        Assert.Equal(spoken, _translators.Received[0]);
        Assert.Empty(_engines.Received);
        Assert.Empty(_translators.Contexts[0].Recent);

        await _controller.ToggleAsync(DictationOutput.English);
        Speak(2);
        await _controller.StopAsync();

        // The first translation is context for the second, so names and terms stay the same (T14).
        Assert.Equal(["Hello there."], _translators.Contexts[1].Recent);
        Assert.Equal("How are you?", _inserter.Inserted);
    }

    [Fact]
    public async Task A_failed_translation_is_retried_as_a_translation_and_never_falls_back()
    {
        AddFallbackProfile();
        _translators.Outcomes.Enqueue(_ => throw new EngineException(EngineErrorKind.Quota, "quota"));
        _translators.Outcomes.Enqueue(_ => "Hello there.");

        await _controller.ToggleAsync(DictationOutput.English);
        var spoken = Speak(3);
        await _controller.StopAsync();

        // The fallback profile is a dictation engine: Persian text must not arrive in place of English.
        Assert.Equal(DictationState.ErrorQuota, _controller.State);
        Assert.Empty(_engines.Received);
        Assert.True(_controller.CanRetry);

        await _controller.RetryAsync();

        Assert.Equal("Hello there.", _inserter.Inserted);
        Assert.Equal(spoken, _translators.Received[1]);
        Assert.Empty(_engines.Received);
    }

    [Fact]
    public async Task The_Persian_behind_a_translation_is_shown_kept_and_can_be_pasted_instead()
    {
        var shown = new List<string>();
        _controller.TranslationHeard += shown.Add;
        _translators.Outcomes.Enqueue(_ => "Hello there.");
        _engines.Outcomes.Enqueue(_ => "سلام");

        await _controller.ToggleAsync(DictationOutput.English);
        Speak(2);
        await _controller.StopAsync();

        Assert.Equal(["صدای شنیده‌شده"], shown);
        Assert.Equal("صدای شنیده‌شده", _controller.LastHeard);

        Assert.True(await _controller.PasteLastHeardAsync());
        Assert.Equal("صدای شنیده‌شده", _inserter.Inserted);

        // A plain dictation replaces the last text, and with it the Persian of the old translation.
        await _controller.ToggleAsync();
        Speak(2);
        await _controller.StopAsync();

        Assert.Null(_controller.LastHeard);
        Assert.False(_controller.HasHeard);
    }

    [Fact]
    public async Task A_plain_dictation_does_not_touch_the_translator()
    {
        _engines.Outcomes.Enqueue(_ => "سلام");

        await _controller.ToggleAsync();
        Speak(2);
        await _controller.StopAsync();

        Assert.Equal("سلام", _inserter.Inserted);
        Assert.Empty(_translators.Received);
        Assert.Equal(0, _translation.RecentTranslations);
    }

    private EngineProfile AddFallbackProfile()
    {
        var fallback = new EngineProfile
        {
            Name = "Record-then-send",
            EngineType = EngineType.GeminiFile,
            ModelId = "gemini-3.5-transcribe"
        };

        _settings.Current.EngineProfiles.Add(fallback);
        _settings.Current.FallbackEngineProfileId = fallback.Id;
        _secrets.Set(fallback.SecretRef, "test-key");
        return fallback;
    }

    [Fact]
    public async Task Retry_that_fails_again_keeps_the_audio()
    {
        _engines.Outcomes.Enqueue(_ => throw new EngineException(EngineErrorKind.Quota, "quota"));
        _engines.Outcomes.Enqueue(_ => throw new EngineException(EngineErrorKind.Network, "offline"));

        _controller.Activate(ActivationMode.Toggle);
        Speak(2);
        await _controller.StopAsync();
        await _controller.RetryAsync();

        Assert.Equal(DictationState.ErrorNetwork, _controller.State);
        Assert.True(_controller.CanRetry);
    }

    [Fact]
    public async Task Retry_obeys_the_focus_rule_at_insertion()
    {
        _engines.Outcomes.Enqueue(_ => throw new EngineException(EngineErrorKind.Network, "offline"));
        _engines.Outcomes.Enqueue(_ => "hello world");

        _controller.Activate(ActivationMode.Toggle);
        Speak(2);
        await _controller.StopAsync();

        _tracker.Check = TargetCheck.DifferentWindow;
        await _controller.RetryAsync();

        Assert.Equal(DictationState.NotInsertedTextKept, _controller.State);
        Assert.Null(_inserter.Inserted);
        Assert.Contains("hello world", _controller.LastDictation);
    }

    [Fact]
    public async Task Without_a_text_field_the_dictation_goes_to_the_clipboard_with_a_notice()
    {
        _engines.Outcomes.Enqueue(_ => "hello world");
        _tracker.Check = TargetCheck.NoTextField;

        _controller.Activate(ActivationMode.Toggle);
        Speak(2);
        await _controller.StopAsync();

        Assert.Contains("hello world", _inserter.Copied);
        Assert.Contains(DictationNotice.CopiedToClipboard, _notices);
        Assert.NotEqual(DictationState.NotInsertedTextKept, _controller.State);
        Assert.Contains("hello world", _controller.LastDictation);
    }

    [Fact]
    public void Audio_blocked_by_a_password_field_is_never_kept()
    {
        _tracker.Password = true;

        _controller.Activate(ActivationMode.Toggle);

        Assert.Equal(DictationState.BlockedPasswordField, _controller.State);
        Assert.False(_controller.CanRetry);
    }

    [Fact]
    public async Task Confirming_a_password_field_types_one_dictation_and_keeps_nothing()
    {
        _engines.Outcomes.Enqueue(_ => "secret words");
        _tracker.Password = true;
        _controller.Activate(ActivationMode.Toggle);

        Assert.Equal(DictationState.BlockedPasswordField, _controller.State);
        Assert.True(_controller.CanOverridePassword);
        await WaitUntil(() => _controller.State != DictationState.BlockedPasswordField);

        await _controller.DictateIntoPasswordFieldAsync();
        Assert.False(_controller.CanOverridePassword);
        Assert.Equal(DictationState.Listening, _controller.State);

        _tracker.Check = TargetCheck.PasswordField;
        Speak(2);
        await _controller.StopAsync();

        // Typed, not pasted, and gone afterwards: no last dictation, no audio for retry (FR-3.7).
        Assert.Equal(DictationState.Inserted, _controller.State);
        Assert.Equal("secret words", _secure.Inserted);
        Assert.Null(_inserter.Inserted);
        Assert.Null(_controller.LastDictation);
        Assert.False(_controller.CanRetry);
    }

    [Fact]
    public async Task A_password_dictation_that_cannot_be_inserted_is_discarded()
    {
        _engines.Outcomes.Enqueue(_ => "secret words");
        _tracker.Password = true;
        _controller.Activate(ActivationMode.Toggle);
        await WaitUntil(() => _controller.State != DictationState.BlockedPasswordField);

        await _controller.DictateIntoPasswordFieldAsync();
        _tracker.Check = TargetCheck.DifferentWindow;
        Speak(2);
        await _controller.StopAsync();

        Assert.NotEqual(DictationState.NotInsertedTextKept, _controller.State);
        Assert.Null(_controller.LastDictation);
        Assert.Null(_secure.Inserted);
        Assert.Contains(DictationNotice.PasswordTextDiscarded, _notices);
    }

    [Fact]
    public async Task The_password_offer_covers_one_dictation_only()
    {
        _engines.Outcomes.Enqueue(_ => "secret words");
        _tracker.Password = true;
        _controller.Activate(ActivationMode.Toggle);
        await WaitUntil(() => _controller.State != DictationState.BlockedPasswordField);

        await _controller.DictateIntoPasswordFieldAsync();
        Speak(2);
        await _controller.StopAsync();
        await WaitUntil(() => _controller.State == DictationState.Ready);

        // The next activation is blocked again: there is no "don't ask again" (FR-3.7).
        _controller.Activate(ActivationMode.Toggle);
        Assert.Equal(DictationState.BlockedPasswordField, _controller.State);
    }

    [Fact]
    public async Task A_new_dictation_replaces_the_kept_audio()
    {
        _engines.Outcomes.Enqueue(_ => throw new EngineException(EngineErrorKind.Network, "offline"));
        _engines.Outcomes.Enqueue(_ => "second");

        _controller.Activate(ActivationMode.Toggle);
        Speak(2);
        await _controller.StopAsync();
        Assert.True(_controller.CanRetry);

        _controller.Activate(ActivationMode.Toggle);
        Speak(2);
        await _controller.StopAsync();

        Assert.False(_controller.CanRetry);
        Assert.Contains("second", _inserter.Inserted);
    }

    [Fact]
    public async Task Microphone_lost_mid_dictation_still_transcribes_what_was_said()
    {
        _engines.Outcomes.Enqueue(_ => "said before unplugging");

        _controller.Activate(ActivationMode.Hold);
        Speak(3);
        _capture.Lose();
        await WaitUntil(() => _controller.State != DictationState.Processing);

        Assert.Equal(DictationState.Inserted, _controller.State);
        Assert.Contains("said before unplugging", _inserter.Inserted);
        Assert.Contains(DictationNotice.MicrophoneLostTranscribing, _notices);
    }

    [Fact]
    public void Microphone_lost_before_any_sound_is_a_microphone_error()
    {
        _engines.Outcomes.Enqueue(_ => "");

        _controller.Activate(ActivationMode.Hold);
        _capture.Lose();

        Assert.Equal(DictationState.ErrorMicrophone, _controller.State);
        Assert.Equal(MicrophoneFailure.Disconnected, _controller.MicrophoneIssue);
    }

    [Fact]
    public void Blocked_microphone_is_named_as_such()
    {
        _capture.StartFailure = new MicrophoneException(MicrophoneFailure.AccessDenied, "blocked");

        _controller.Activate(ActivationMode.Hold);

        Assert.Equal(DictationState.ErrorMicrophone, _controller.State);
        Assert.Equal(MicrophoneFailure.AccessDenied, _controller.MicrophoneIssue);
    }

    /// <summary>Sends loud 100 ms chunks, as a microphone would, and returns them.</summary>
    private List<byte[]> Speak(int chunks)
    {
        var sent = new List<byte[]>();
        for (var i = 0; i < chunks; i++)
        {
            var chunk = new byte[3200];
            for (var s = 0; s < 1600; s++)
            {
                var sample = (short)(3000 * Math.Sin(s * 0.3 + i));
                chunk[s * 2] = (byte)sample;
                chunk[s * 2 + 1] = (byte)(sample >> 8);
            }

            sent.Add(chunk);
            _capture.Emit(chunk);
        }

        return sent;
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10);
        }
    }

    private sealed class InlineContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => d(state);
    }

    private sealed class FakeCapture : IAudioCapture
    {
        private Action<byte[]>? _onChunk;
        private Action<Exception>? _onFailure;

        public MicrophoneException? StartFailure { get; set; }
        public string? PreferredDeviceId { get; set; }
        public AudioDevice? ActiveDevice => null;
        public bool UsedFallback => false;

        public void Start(Action<byte[]> onChunk, Action<Exception> onFailure)
        {
            if (StartFailure is not null)
            {
                throw StartFailure;
            }

            _onChunk = onChunk;
            _onFailure = onFailure;
        }

        public Task StopAsync()
        {
            _onChunk = null;
            return Task.CompletedTask;
        }

        public void Emit(byte[] chunk) => _onChunk?.Invoke(chunk);

        public void Lose()
        {
            var failure = _onFailure;
            _onChunk = null;
            failure?.Invoke(new MicrophoneException(MicrophoneFailure.Disconnected, "unplugged"));
        }
    }

    private sealed class FakeEngines : ISpeechEngineFactory, ISpeechEngine
    {
        public Queue<Func<List<byte[]>, string>> Outcomes { get; } = new();
        public List<List<byte[]>> Received { get; } = new();

        public EngineCapabilities Capabilities { get; } = new(true, true, true, true, true, TimeSpan.FromMinutes(10));

        public ISpeechEngine Create(EngineProfile profile, string apiKey) => this;

        public EngineCapabilities Describe(EngineProfile profile) => Capabilities;

        public Task<EngineTestReport> TestAsync(CancellationToken ct) =>
            Task.FromResult(new EngineTestReport("fake", TimeSpan.Zero));

        public Task<ISpeechSession> StartSessionAsync(EngineSessionOptions options, CancellationToken ct)
        {
            var received = new List<byte[]>();
            Received.Add(received);
            return Task.FromResult<ISpeechSession>(new FakeSpeech(received, Outcomes.Dequeue()));
        }
    }

    private sealed class FakeSpeech(List<byte[]> received, Func<List<byte[]>, string> outcome) : ISpeechSession
    {
        public event Action<string>? InterimText { add { } remove { } }
        public event Action<string>? FinalSegment { add { } remove { } }

        public ValueTask WriteAudioAsync(ReadOnlyMemory<byte> pcm, CancellationToken ct)
        {
            received.Add(pcm.ToArray());
            return ValueTask.CompletedTask;
        }

        public Task<TranscriptResult> CompleteAsync(CancellationToken ct) =>
            Task.FromResult(new TranscriptResult(outcome(received), TimeSpan.Zero));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeTranslators : ISpeechTranslatorFactory, ISpeechTranslator
    {
        public Queue<Func<List<byte[]>, string>> Outcomes { get; } = new();
        public List<List<byte[]>> Received { get; } = new();
        public List<TranslationContext> Contexts { get; } = new();

        public TranslationRoute Route => TranslationRoute.OneStep;
        public bool UsesContext => true;

        public Task<IReadOnlyList<TranslationModel>> ListModelsAsync(string apiKey, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<TranslationModel>>([]);

        public Task<ISpeechTranslator> CreateAsync(string modelId, EngineProfile transcriber, EngineSessionOptions options,
            string apiKey, CancellationToken ct) => Task.FromResult<ISpeechTranslator>(this);

        public Task<ITranslationSession> StartAsync(TranslationContext context, CancellationToken ct)
        {
            var received = new List<byte[]>();
            Received.Add(received);
            Contexts.Add(context);
            return Task.FromResult<ITranslationSession>(new FakeTranslation(received, Outcomes.Dequeue()));
        }
    }

    private sealed class FakeTranslation(List<byte[]> received, Func<List<byte[]>, string> outcome) : ITranslationSession
    {
        public ValueTask WriteAudioAsync(ReadOnlyMemory<byte> pcm, CancellationToken ct)
        {
            received.Add(pcm.ToArray());
            return ValueTask.CompletedTask;
        }

        public Task<TranslationResult> CompleteAsync(CancellationToken ct) =>
            Task.FromResult(new TranslationResult(outcome(received), "صدای شنیده‌شده", TimeSpan.Zero));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeTracker : ITargetTracker
    {
        public bool Password { get; set; }
        public TargetCheck Check { get; set; } = TargetCheck.SameWindow;

        public TargetSnapshot? Capture() => new(1, 1, "test");

        public Task<bool> IsPasswordFieldAsync(TimeSpan timeout, CancellationToken ct) => Task.FromResult(Password);

        public Task<TargetCheck> CheckAsync(TargetSnapshot? snapshot, TimeSpan timeout, CancellationToken ct) =>
            Task.FromResult(Check);
    }

    private sealed class FakeInserter : ITextInserter, IClipboardWriter
    {
        public string? Inserted { get; private set; }

        public string? Copied { get; private set; }

        public Task<InsertResult> InsertAsync(string text, CancellationToken ct)
        {
            Inserted = text;
            return Task.FromResult(new InsertResult(true));
        }

        public void SetText(string text) => Copied = text;
    }

    private sealed class FakeDevices : IAudioDeviceCatalog
    {
        public IReadOnlyList<AudioDevice> List() => [];

        public AudioDevice? Resolve(string? preferredId) => null;
    }

    private sealed class FakeSettings : ISettingsStore
    {
        public AppSettings Current { get; } = AppSettings.CreateDefault();

        public event Action<AppSettings>? Changed { add { } remove { } }

        public void Save(AppSettings settings)
        {
        }
    }

    private sealed class FakeSecrets : ISecretStore
    {
        private readonly Dictionary<string, string> _values = new();

        public bool Has(string key) => _values.ContainsKey(key);
        public string? Get(string key) => _values.GetValueOrDefault(key);
        public void Set(string key, string value) => _values[key] = value;
        public void Remove(string key) => _values.Remove(key);
    }

    private sealed class NullLog : IAppLogger
    {
        public void Info(string message)
        {
        }

        public void Warn(string message)
        {
        }

        public void Error(string message, Exception? ex = null)
        {
        }
    }
}
