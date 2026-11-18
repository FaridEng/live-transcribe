namespace LiveTranscribe.Core.Settings;

/// <summary>Which implementation runs a profile. Adding a provider means adding a value plus a class (FR-13.1, D21).</summary>
public enum EngineType
{
    GeminiLive,
    GeminiFile
}

public enum CleanupLevel
{
    Light,
    Standard
}

/// <summary>A saved engine configuration: provider, model, credentials and options (FR-13.1).</summary>
public sealed class EngineProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public string Name { get; set; } = "Gemini Live";
    public EngineType EngineType { get; set; } = EngineType.GeminiLive;

    /// <summary>Free text on purpose: preview model ids change (FR-13.2).</summary>
    public string ModelId { get; set; } = "gemini-3.5-transcribe-live";

    /// <summary>Key into the secret store, never the key itself (FR-11.3, FR-13.7).</summary>
    public string SecretRef { get; set; } = "gemini";

    public CleanupLevel Cleanup { get; set; } = CleanupLevel.Standard;

    /// <summary>Shown next to the profile so the data terms are visible (FR-13.9).</summary>
    public string PrivacyNote { get; set; } =
        "Free tier: Google may use submitted audio to improve its products. Paid tier: it does not.";
}
