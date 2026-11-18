using System.Net.WebSockets;
using LiveTranscribe.Core.Engines;

namespace LiveTranscribe.Engines.Gemini;

/// <summary>Maps Gemini failures to provider-neutral error kinds (architecture §4.5).</summary>
internal static class GeminiErrors
{
    public static EngineException FromConnectFailure(Exception ex, string apiKey)
    {
        var text = Clean(ex.ToString(), apiKey);
        var kind = Classify(text) ?? EngineErrorKind.Network;
        return new EngineException(kind, $"Could not connect to Gemini: {Clean(ex.Message, apiKey)}");
    }

    public static EngineException FromClose(WebSocketCloseStatus? status, string? description, string apiKey)
    {
        var text = Clean(description ?? string.Empty, apiKey);
        var kind = Classify(text) ?? (status == WebSocketCloseStatus.NormalClosure
            ? EngineErrorKind.Network
            : EngineErrorKind.Transcription);
        return new EngineException(kind, $"Gemini closed the session ({status}): {text}");
    }

    /// <summary>A failed REST call: the status alone decides, and the body only sharpens the message.</summary>
    public static EngineException FromHttp(System.Net.HttpStatusCode status, string body, string apiKey)
    {
        var text = Clean(body, apiKey);
        var kind = (int)status switch
        {
            400 or 401 or 403 => EngineErrorKind.Authentication,
            404 => EngineErrorKind.ModelNotFound,
            429 => EngineErrorKind.Quota,
            >= 500 => EngineErrorKind.Network,
            _ => Classify(text) ?? EngineErrorKind.Transcription
        };

        return new EngineException(kind, $"Gemini refused the request (HTTP {(int)status}): {Message(text)}");
    }

    /// <summary>Pulls `error.message` out of the provider's JSON, so the user sees one line, not a payload.</summary>
    private static string Message(string body)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("error", out var error) &&
                error.TryGetProperty("message", out var message))
            {
                return message.GetString() ?? body;
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // Not JSON: show what came back, trimmed.
        }

        return body.Length > 300 ? body[..300] : body;
    }

    public static EngineException FromSocketFailure(WebSocketException ex, WebSocket socket, string apiKey)
    {
        if (socket.CloseStatus is not null)
        {
            return FromClose(socket.CloseStatus, socket.CloseStatusDescription, apiKey);
        }

        return new EngineException(EngineErrorKind.Network, $"Connection to Gemini failed: {Clean(ex.Message, apiKey)}", ex);
    }

    private static EngineErrorKind? Classify(string text)
    {
        var lower = text.ToLowerInvariant();

        if (lower.Contains("api key") || lower.Contains("api_key") || lower.Contains("permission") ||
            lower.Contains("unauthenticated") || lower.Contains("401") || lower.Contains("403"))
        {
            return EngineErrorKind.Authentication;
        }

        if (lower.Contains("quota") || lower.Contains("resource_exhausted") || lower.Contains("rate limit") ||
            lower.Contains("429"))
        {
            return EngineErrorKind.Quota;
        }

        if (lower.Contains("not found") || lower.Contains("not supported for bidigeneratecontent") || lower.Contains("404"))
        {
            return EngineErrorKind.ModelNotFound;
        }

        return null;
    }

    private static string Clean(string text, string apiKey) =>
        string.IsNullOrEmpty(apiKey) ? text : text.Replace(apiKey, "<key>");
}
