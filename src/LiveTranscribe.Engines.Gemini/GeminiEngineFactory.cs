using LiveTranscribe.Core.Engines;
using LiveTranscribe.Core.Settings;

namespace LiveTranscribe.Engines.Gemini;

public sealed class GeminiEngineFactory : ISpeechEngineFactory, IEngineCatalog
{
    public ISpeechEngine Create(EngineProfile profile, string apiKey) => profile.EngineType switch
    {
        EngineType.GeminiLive => new GeminiLiveEngine(profile, apiKey),
        EngineType.GeminiFile => new GeminiFileEngine(profile, apiKey),
        _ => throw new NotSupportedException($"Engine type {profile.EngineType} has no implementation.")
    };

    public EngineCapabilities Describe(EngineProfile profile) => Create(profile, string.Empty).Capabilities;

    /// <summary>Only the models that take the API this profile type uses; the rest would fail at dictation.</summary>
    public Task<IReadOnlyList<EngineModel>> ListModelsAsync(EngineType engineType, string apiKey, CancellationToken ct) =>
        GeminiCatalog.ListAsync(
            apiKey,
            engineType == EngineType.GeminiLive ? GeminiCatalog.LiveMethod : GeminiCatalog.FileMethod,
            ct);
}
