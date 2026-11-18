using LiveTranscribe.Core.Abstractions;
using LiveTranscribe.Core.Engines;
using LiveTranscribe.Core.Settings;

namespace LiveTranscribe.Core.Translation;

/// <summary>
/// Translation as the dictation controller sees it (architecture T14): an engine that answers in English,
/// plus the context it carries from one dictation to the next.
/// <list type="bullet">
/// <item>The topic note lives in settings and clears itself after <see cref="TopicNoteLifetime"/> unused.</item>
/// <item>The last few translations are kept in memory only, like the last dictation (D17), and are dropped
/// after <see cref="RecentLifetime"/> without translating, when the target is another app, or on "Start
/// fresh".</item>
/// </list>
/// All members run on the UI thread, as the controller's do.
/// </summary>
public sealed class TranslationService
{
    public static readonly TimeSpan TopicNoteLifetime = TimeSpan.FromHours(1);
    public static readonly TimeSpan RecentLifetime = TimeSpan.FromMinutes(5);
    public const int RecentCount = 3;

    private readonly ISpeechTranslatorFactory _translators;
    private readonly ISettingsStore _settings;
    private readonly Func<DateTimeOffset> _now;
    private readonly List<string> _recent = new();

    private DateTimeOffset _recentAt;
    private int? _recentProcessId;

    public TranslationService(ISpeechTranslatorFactory translators, ISettingsStore settings, Func<DateTimeOffset>? now = null)
    {
        _translators = translators;
        _settings = settings;
        _now = now ?? (() => DateTimeOffset.Now);
    }

    /// <summary>The note, tone or remembered sentences changed; the pill shows whether a note is on.</summary>
    public event Action? ContextChanged;

    /// <summary>The topic note if it is still fresh; an expired one is cleared here.</summary>
    public string? TopicNote
    {
        get
        {
            var translation = _settings.Current.Translation;
            if (string.IsNullOrWhiteSpace(translation.TopicNote))
            {
                return null;
            }

            if (translation.TopicNoteTouchedAt is { } touched && _now() - touched > TopicNoteLifetime)
            {
                SetTopicNote(null);
                return null;
            }

            return translation.TopicNote;
        }
    }

    public TranslationTone Tone => _settings.Current.Translation.Tone;

    public int RecentTranslations => _recent.Count;

    public void SetTopicNote(string? note)
    {
        var settings = _settings.Current;
        settings.Translation.TopicNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        settings.Translation.TopicNoteTouchedAt = settings.Translation.TopicNote is null ? null : _now();
        _settings.Save(settings);
        ContextChanged?.Invoke();
    }

    public void SetTone(TranslationTone tone)
    {
        var settings = _settings.Current;
        settings.Translation.Tone = tone;
        _settings.Save(settings);
        ContextChanged?.Invoke();
    }

    /// <summary>"Start fresh": the next translation does not lean on the earlier ones.</summary>
    public void StartFresh()
    {
        _recent.Clear();
        ContextChanged?.Invoke();
    }

    /// <summary>
    /// What the next translation into <paramref name="target"/> may use. Remembered sentences that are
    /// stale or belong to another app are dropped first.
    /// </summary>
    public TranslationContext ContextFor(TargetSnapshot? target)
    {
        if (_recent.Count > 0 && (_now() - _recentAt > RecentLifetime || target?.ProcessId != _recentProcessId))
        {
            _recent.Clear();
        }

        return new TranslationContext(TopicNote, Tone, _recent.ToList());
    }

    /// <summary>A translation reached the user's field: it becomes context for the next one.</summary>
    public void Remember(string english, TargetSnapshot? target)
    {
        if (target?.ProcessId != _recentProcessId)
        {
            _recent.Clear();
        }

        _recent.Add(english);
        if (_recent.Count > RecentCount)
        {
            _recent.RemoveAt(0);
        }

        _recentAt = _now();
        _recentProcessId = target?.ProcessId;

        // Using the note counts as touching it, so the hour runs from the last use (architecture T14).
        var settings = _settings.Current;
        if (settings.Translation.TopicNote is not null)
        {
            settings.Translation.TopicNoteTouchedAt = _now();
        }

        ContextChanged?.Invoke();
    }

    /// <summary>
    /// An engine that hears <paramref name="transcriber"/>'s audio and answers in English with the model
    /// chosen in settings, so the controller runs a translation exactly like a dictation.
    /// </summary>
    public ISpeechEngine CreateEngine(EngineProfile transcriber, string apiKey, TranslationContext context) =>
        new TranslatingEngine(_translators, _settings.Current.Translation.ModelId, transcriber, apiKey, context);

    private sealed class TranslatingEngine : ISpeechEngine
    {
        private readonly ISpeechTranslatorFactory _translators;
        private readonly string _modelId;
        private readonly EngineProfile _transcriber;
        private readonly string _apiKey;
        private readonly TranslationContext _context;

        public TranslatingEngine(ISpeechTranslatorFactory translators, string modelId, EngineProfile transcriber,
            string apiKey, TranslationContext context)
        {
            _translators = translators;
            _modelId = modelId;
            _transcriber = transcriber;
            _apiKey = apiKey;
            _context = context;
        }

        /// <summary>Both routes are Live sessions underneath, so the Live session limit applies.</summary>
        public EngineCapabilities Capabilities { get; } = new(
            Streaming: false,
            InterimResults: false,
            SmartCleanup: true,
            LanguageHints: true,
            ManualTurnControl: true,
            MaxSessionLength: TimeSpan.FromMinutes(10));

        public async Task<ISpeechSession> StartSessionAsync(EngineSessionOptions options, CancellationToken ct)
        {
            var translator = await _translators.CreateAsync(_modelId, _transcriber, options, _apiKey, ct).ConfigureAwait(false);
            return new Session(await translator.StartAsync(_context, ct).ConfigureAwait(false));
        }

        public Task<EngineTestReport> TestAsync(CancellationToken ct) => throw new NotSupportedException();
    }

    /// <summary>A translation session dressed as a dictation session: its "transcript" is the English.</summary>
    private sealed class Session : ISpeechSession
    {
        private readonly ITranslationSession _translation;

        public Session(ITranslationSession translation)
        {
            _translation = translation;
        }

        public event Action<string>? InterimText { add { } remove { } }
        public event Action<string>? FinalSegment { add { } remove { } }

        public ValueTask WriteAudioAsync(ReadOnlyMemory<byte> pcm, CancellationToken ct) => _translation.WriteAudioAsync(pcm, ct);

        public async Task<TranscriptResult> CompleteAsync(CancellationToken ct)
        {
            var result = await _translation.CompleteAsync(ct).ConfigureAwait(false);
            return new TranscriptResult(result.Text, result.AfterEndOfSpeech, result.Transcript);
        }

        public ValueTask DisposeAsync() => _translation.DisposeAsync();
    }
}
