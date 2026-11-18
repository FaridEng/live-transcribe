using System.Diagnostics;
using System.Text.Json;
using LiveTranscribe.Core.Engines;

namespace LiveTranscribe.Engines.Gemini;

/// <summary>
/// The provider's model directory, shared by both Gemini engines: it answers "does this key reach this
/// model, and does the model take this API?" without starting a dictation (FR-13.2, FR-13.10).
/// </summary>
internal static class GeminiCatalog
{
    public const string LiveMethod = "bidiGenerateContent";
    public const string FileMethod = "generateContent";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>Confirms the model exists for this key and supports <paramref name="method"/>.</summary>
    public static async Task<EngineTestReport> TestAsync(string modelId, string apiKey, string method, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        var model = await GetAsync($"models/{modelId}", apiKey, ct).ConfigureAwait(false);
        var elapsed = clock.Elapsed;

        var methods = Methods(model);
        if (methods.Count > 0 && !methods.Contains(method))
        {
            throw new EngineException(EngineErrorKind.ModelNotFound,
                $"The model \"{modelId}\" does not support {method}. It offers: {string.Join(", ", methods)}.");
        }

        var name = model.TryGetProperty("displayName", out var display) ? display.GetString() : modelId;
        return new EngineTestReport($"{name} is reachable and accepts {method}.", elapsed);
    }

    /// <summary>Every model this key can reach that takes <paramref name="method"/>, newest names first.</summary>
    public static async Task<IReadOnlyList<EngineModel>> ListAsync(string apiKey, string method, CancellationToken ct) =>
        (await ListWithMethodsAsync(apiKey, ct).ConfigureAwait(false))
        .Where(entry => entry.Methods.Contains(method))
        .Select(entry => entry.Model)
        .ToList();

    /// <summary>Every model this key can reach, with the APIs each one takes, sorted by id.</summary>
    public static async Task<IReadOnlyList<(EngineModel Model, HashSet<string> Methods)>> ListWithMethodsAsync(
        string apiKey, CancellationToken ct)
    {
        var page = await GetAsync("models?pageSize=200", apiKey, ct).ConfigureAwait(false);
        if (!page.TryGetProperty("models", out var models))
        {
            return [];
        }

        var found = new List<(EngineModel, HashSet<string>)>();
        foreach (var model in models.EnumerateArray())
        {
            var id = model.TryGetProperty("name", out var name) ? name.GetString() ?? string.Empty : string.Empty;
            if (id.StartsWith("models/", StringComparison.Ordinal))
            {
                id = id["models/".Length..];
            }

            if (id.Length == 0)
            {
                continue;
            }

            found.Add((new EngineModel(
                id,
                model.TryGetProperty("displayName", out var display) ? display.GetString() ?? id : id,
                model.TryGetProperty("description", out var about) ? about.GetString() : null), Methods(model)));
        }

        return found.OrderBy(m => m.Item1.Id, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The APIs one model takes, straight from the directory; empty when the provider does not say.</summary>
    public static async Task<HashSet<string>> MethodsAsync(string modelId, string apiKey, CancellationToken ct) =>
        Methods(await GetAsync($"models/{modelId}", apiKey, ct).ConfigureAwait(false));

    private static HashSet<string> Methods(JsonElement model)
    {
        var methods = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (model.TryGetProperty("supportedGenerationMethods", out var supported) &&
            supported.ValueKind == JsonValueKind.Array)
        {
            foreach (var method in supported.EnumerateArray())
            {
                var value = method.GetString();
                if (value is not null)
                {
                    methods.Add(value);
                }
            }
        }

        return methods;
    }

    private static async Task<JsonElement> GetAsync(string path, string apiKey, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"https://generativelanguage.googleapis.com/v1beta/{path}");
        // Header rather than query string, so the key never appears in a URL (FR-11.3).
        request.Headers.Add("x-goog-api-key", apiKey);

        HttpResponseMessage response;
        try
        {
            response = await Http.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw GeminiErrors.FromConnectFailure(ex, apiKey);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw GeminiErrors.FromHttp(response.StatusCode, body, apiKey);
            }

            // The element outlives the document, so keep a copy rather than a view into freed memory.
            return JsonDocument.Parse(body).RootElement.Clone();
        }
    }
}
