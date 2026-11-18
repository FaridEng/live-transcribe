using LiveTranscribe.Core.Engines;
using LiveTranscribe.Core.Settings;
using LiveTranscribe.Core.Translation;

namespace LiveTranscribe.Engines.Gemini;

/// <summary>
/// Picks the translation route from what the model offers: a Live model hears the audio (one-step),
/// a <c>generateContent</c> model translates the dictation engine's transcript (two-step).
/// </summary>
public sealed class GeminiTranslatorFactory : ISpeechTranslatorFactory
{
    private readonly ISpeechEngineFactory _engines;

    // The route of a model does not change, and asking the directory costs a round trip at the start of
    // every translated dictation, so each model is looked up once per run.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, HashSet<string>> _methods = new(StringComparer.OrdinalIgnoreCase);

    public GeminiTranslatorFactory(ISpeechEngineFactory engines)
    {
        _engines = engines;
    }

    public async Task<IReadOnlyList<TranslationModel>> ListModelsAsync(string apiKey, CancellationToken ct)
    {
        var models = await GeminiCatalog.ListWithMethodsAsync(apiKey, ct).ConfigureAwait(false);
        var usable = new List<TranslationModel>();
        foreach (var (model, methods) in models)
        {
            if (!CanTranslate(model.Id))
            {
                continue;
            }

            if (methods.Contains(GeminiCatalog.LiveMethod))
            {
                usable.Add(new TranslationModel(model.Id, model.DisplayName, TranslationRoute.OneStep));
            }
            else if (methods.Contains(GeminiCatalog.FileMethod))
            {
                usable.Add(new TranslationModel(model.Id, model.DisplayName, TranslationRoute.TwoStep));
            }
        }

        return usable;
    }

    public async Task<ISpeechTranslator> CreateAsync(string modelId, EngineProfile transcriber,
        EngineSessionOptions options, string apiKey, CancellationToken ct)
    {
        if (!_methods.TryGetValue(modelId, out var methods))
        {
            methods = await GeminiCatalog.MethodsAsync(modelId, apiKey, ct).ConfigureAwait(false);
            _methods[modelId] = methods;
        }

        if (methods.Contains(GeminiCatalog.LiveMethod))
        {
            return new GeminiLiveTranslator(modelId, apiKey);
        }

        if (methods.Count > 0 && !methods.Contains(GeminiCatalog.FileMethod))
        {
            throw new EngineException(EngineErrorKind.ModelNotFound,
                $"The model \"{modelId}\" takes neither the Live API nor generateContent, so it cannot translate.");
        }

        return new TwoStepTranslator(_engines.Create(transcriber, apiKey), options,
            new GeminiTextTranslator(modelId, apiKey));
    }

    /// <summary>
    /// Leaves out the models that cannot take a translation instruction or write text: the transcription
    /// models reject a system instruction, and the rest make images, speech or vectors.
    /// </summary>
    private static bool CanTranslate(string id) =>
        !new[] { "transcribe", "tts", "image", "embedding", "imagen", "veo", "aqa", "robotics" }
            .Any(part => id.Contains(part, StringComparison.OrdinalIgnoreCase));
}
