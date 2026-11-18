using LiveTranscribe.Core.Settings;

namespace LiveTranscribe.Core.Engines;

/// <summary>What an engine can do, so features it cannot support are disabled rather than broken (FR-13.4).</summary>
public sealed record EngineCapabilities(
    bool Streaming,
    bool InterimResults,
    bool SmartCleanup,
    bool LanguageHints,
    bool ManualTurnControl,
    TimeSpan MaxSessionLength);

public sealed record EngineSessionOptions(LanguageMode Language, CleanupLevel Cleanup);

/// <summary>
/// The final text. <paramref name="Heard"/> is set only when <paramref name="Text"/> is a translation: the
/// Persian the engine heard, so the user can tell a mishearing from a mistranslation (architecture T14).
/// </summary>
public sealed record TranscriptResult(string Text, TimeSpan ElapsedAfterEndOfSpeech, string? Heard = null);

/// <summary>What a passing "Test connection" found: the key works and the model takes this API (FR-13.10).</summary>
public sealed record EngineTestReport(string Summary, TimeSpan RoundTrip);

/// <summary>One model the provider offers, for the profile's model list (FR-13.2).</summary>
public sealed record EngineModel(string Id, string DisplayName, string? Description);

/// <summary>A speech-to-text provider behind one engine profile. Nothing outside an engine may assume Gemini (D21).</summary>
public interface ISpeechEngine
{
    EngineCapabilities Capabilities { get; }

    Task<ISpeechSession> StartSessionAsync(EngineSessionOptions options, CancellationToken ct);

    /// <summary>
    /// Checks the credentials and the model without dictating (FR-13.10). Throws an
    /// <see cref="EngineException"/> with the reason when the profile cannot work.
    /// </summary>
    Task<EngineTestReport> TestAsync(CancellationToken ct);
}

/// <summary>One dictation's conversation with the engine.</summary>
public interface ISpeechSession : IAsyncDisposable
{
    /// <summary>Partial hypotheses, for preview only; never inserted (FR-2.12).</summary>
    event Action<string>? InterimText;

    /// <summary>Finalized text segments, used by streaming insertion (FR-3.2).</summary>
    event Action<string>? FinalSegment;

    /// <summary>16 kHz, 16-bit, mono, little-endian PCM.</summary>
    ValueTask WriteAudioAsync(ReadOnlyMemory<byte> pcm, CancellationToken ct);

    /// <summary>Signals end of speech and returns the complete final text.</summary>
    Task<TranscriptResult> CompleteAsync(CancellationToken ct);
}

public interface ISpeechEngineFactory
{
    ISpeechEngine Create(EngineProfile profile, string apiKey);

    /// <summary>What a profile's engine can do, without credentials, so settings can gate features (FR-13.4).</summary>
    EngineCapabilities Describe(EngineProfile profile);
}

/// <summary>
/// Lists the models a provider offers, so a profile can pick from what the key actually reaches instead
/// of a list baked into the app. The user may still type an id that is not listed (FR-13.2).
/// </summary>
public interface IEngineCatalog
{
    Task<IReadOnlyList<EngineModel>> ListModelsAsync(EngineType engineType, string apiKey, CancellationToken ct);
}

/// <summary>Provider-neutral failure kinds; each maps to an FR-6.1 error state.</summary>
public enum EngineErrorKind
{
    Authentication,
    Quota,
    Network,
    ModelNotFound,
    Transcription
}

public sealed class EngineException : Exception
{
    public EngineException(EngineErrorKind kind, string message, Exception? inner = null)
        : base(message, inner)
    {
        Kind = kind;
    }

    public EngineErrorKind Kind { get; }
}
