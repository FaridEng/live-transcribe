namespace LiveTranscribe.Core.Security;

/// <summary>
/// API keys, encrypted and bound to the Windows user. Values are never logged, shown in full,
/// or copied to the clipboard (FR-11.3, FR-13.7).
/// </summary>
public interface ISecretStore
{
    bool Has(string key);
    string? Get(string key);
    void Set(string key, string value);
    void Remove(string key);
}
