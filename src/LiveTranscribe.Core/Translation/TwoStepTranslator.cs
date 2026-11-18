using System.Diagnostics;
using LiveTranscribe.Core.Engines;

namespace LiveTranscribe.Core.Translation;

/// <summary>
/// Transcribes with the dictation engine, exactly as a normal dictation does, then translates the text.
/// The transcription streams while the user speaks, so the translation adds only its own round trip
/// (about 0.8 s on a small model, architecture T14).
/// </summary>
public sealed class TwoStepTranslator : ISpeechTranslator
{
    private readonly ISpeechEngine _engine;
    private readonly EngineSessionOptions _options;
    private readonly ITextTranslator _text;

    public TwoStepTranslator(ISpeechEngine engine, EngineSessionOptions options, ITextTranslator text)
    {
        _engine = engine;
        _options = options;
        _text = text;
    }

    public TranslationRoute Route => TranslationRoute.TwoStep;

    public bool UsesContext => true;

    public async Task<ITranslationSession> StartAsync(TranslationContext context, CancellationToken ct) =>
        new Session(await _engine.StartSessionAsync(_options, ct).ConfigureAwait(false), _text, context);

    private sealed class Session : ITranslationSession
    {
        private readonly ISpeechSession _speech;
        private readonly ITextTranslator _text;
        private readonly TranslationContext _context;

        public Session(ISpeechSession speech, ITextTranslator text, TranslationContext context)
        {
            _speech = speech;
            _text = text;
            _context = context;
        }

        public ValueTask WriteAudioAsync(ReadOnlyMemory<byte> pcm, CancellationToken ct) => _speech.WriteAudioAsync(pcm, ct);

        public async Task<TranslationResult> CompleteAsync(CancellationToken ct)
        {
            var clock = Stopwatch.StartNew();
            var transcript = await _speech.CompleteAsync(ct).ConfigureAwait(false);
            if (transcript.Text.Length == 0)
            {
                return new TranslationResult(string.Empty, string.Empty, clock.Elapsed);
            }

            var english = await _text.TranslateAsync(transcript.Text, _context, ct).ConfigureAwait(false);
            return new TranslationResult(english.Trim(), transcript.Text, clock.Elapsed);
        }

        public ValueTask DisposeAsync() => _speech.DisposeAsync();
    }
}
