using LiveTranscribe.Core.Engines;
using LiveTranscribe.Core.Settings;

namespace LiveTranscribe.Core.Translation;

/// <summary>How the English should sound (architecture T14). "As spoken" keeps the register the user spoke in.</summary>
public enum TranslationTone { AsSpoken, Casual, Formal }

/// <summary>
/// How a model gets from speech to English. Two-step transcribes with the dictation engine and then
/// translates the text; one-step hears the audio and answers in English (architecture T14).
/// </summary>
public enum TranslationRoute { TwoStep, OneStep }

/// <summary>
/// What the translation may use besides the words: a topic note the user typed, the tone, and the last
/// few translations of the same text, so names and terms stay consistent (architecture T14).
/// </summary>
public sealed record TranslationContext(string? TopicNote, TranslationTone Tone, IReadOnlyList<string> Recent)
{
    public static TranslationContext None { get; } = new(null, TranslationTone.AsSpoken, []);
}

/// <summary>One model offered for translation, with the route it would take.</summary>
public sealed record TranslationModel(string Id, string DisplayName, TranslationRoute Route);

/// <summary>
/// The English, the transcript it came from when there was one (two-step only), and the wait after the
/// last word, which is the number the user feels.
/// </summary>
public sealed record TranslationResult(string Text, string? Transcript, TimeSpan AfterEndOfSpeech);

/// <summary>One dictation translated as it streams: audio in while the user speaks, English out at the end.</summary>
public interface ITranslationSession : IAsyncDisposable
{
    /// <summary>16 kHz, 16-bit, mono, little-endian PCM, as it is captured.</summary>
    ValueTask WriteAudioAsync(ReadOnlyMemory<byte> pcm, CancellationToken ct);

    /// <summary>Signals end of speech and returns the English.</summary>
    Task<TranslationResult> CompleteAsync(CancellationToken ct);
}

public interface ISpeechTranslator
{
    TranslationRoute Route { get; }

    /// <summary>
    /// Whether the model reads the topic note and tone. A dedicated interpreting model takes only a
    /// target language, so the settings say so rather than pretending the context was used.
    /// </summary>
    bool UsesContext { get; }

    Task<ITranslationSession> StartAsync(TranslationContext context, CancellationToken ct);
}

/// <summary>Translates text that is already written down: the second half of the two-step route.</summary>
public interface ITextTranslator
{
    Task<string> TranslateAsync(string text, TranslationContext context, CancellationToken ct);
}

public interface ISpeechTranslatorFactory
{
    /// <summary>Every model the key reaches that could translate, tagged with the route it would take.</summary>
    Task<IReadOnlyList<TranslationModel>> ListModelsAsync(string apiKey, CancellationToken ct);

    /// <summary>
    /// Builds the translator for <paramref name="modelId"/>. The route follows from the API the model
    /// offers; a two-step model transcribes with <paramref name="transcriber"/>, the dictation profile.
    /// </summary>
    Task<ISpeechTranslator> CreateAsync(string modelId, EngineProfile transcriber, EngineSessionOptions options,
        string apiKey, CancellationToken ct);
}
