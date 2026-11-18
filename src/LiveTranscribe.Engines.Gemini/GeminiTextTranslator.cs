using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using LiveTranscribe.Core.Engines;
using LiveTranscribe.Core.Translation;

namespace LiveTranscribe.Engines.Gemini;

/// <summary>
/// The second step of two-step translation: one <c>generateContent</c> call to a general text model
/// (for example <c>gemini-3.5-flash-lite</c>, about 0.8 s per sentence, architecture T14).
/// </summary>
public sealed class GeminiTextTranslator : ITextTranslator
{
    // One client for the process: a new one per dictation would pay for a fresh TLS handshake.
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private readonly string _modelId;
    private readonly string _apiKey;

    public GeminiTextTranslator(string modelId, string apiKey)
    {
        _modelId = modelId;
        _apiKey = apiKey;
    }

    public async Task<string> TranslateAsync(string text, TranslationContext context, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"https://generativelanguage.googleapis.com/v1beta/models/{_modelId}:generateContent");
        // Header rather than query string, so the key never appears in a URL (FR-11.3).
        request.Headers.Add("x-goog-api-key", _apiKey);
        request.Content = new StringContent(JsonSerializer.Serialize(new
        {
            // The rules go in the system instruction and the words in the user turn, so dictated text
            // cannot pass itself off as an instruction.
            systemInstruction = new { parts = new[] { new { text = TranslationPrompt.ForText(context) } } },
            contents = new[] { new { role = "user", parts = new[] { new { text } } } },
            generationConfig = new { temperature = 0 }
        }), Encoding.UTF8);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        HttpResponseMessage response;
        try
        {
            response = await Http.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw GeminiErrors.FromConnectFailure(ex, _apiKey);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw GeminiErrors.FromHttp(response.StatusCode, body, _apiKey);
            }

            return ReadText(body);
        }
    }

    /// <summary>The first candidate's text parts, skipping any thought summaries.</summary>
    private static string ReadText(string body)
    {
        using var document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("candidates", out var candidates) || candidates.GetArrayLength() == 0)
        {
            throw new EngineException(EngineErrorKind.Transcription, "The translation model returned no answer.");
        }

        var text = new StringBuilder();
        var first = candidates[0];
        if (first.TryGetProperty("content", out var content) && content.TryGetProperty("parts", out var parts))
        {
            foreach (var part in parts.EnumerateArray())
            {
                if (part.TryGetProperty("thought", out var thought) && thought.ValueKind == JsonValueKind.True)
                {
                    continue;
                }

                if (part.TryGetProperty("text", out var value))
                {
                    text.Append(value.GetString());
                }
            }
        }

        return text.ToString().Trim();
    }
}
