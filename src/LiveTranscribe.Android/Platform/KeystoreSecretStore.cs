using System.Text;
using System.Text.Json;
using Android.Security.Keystore;
using Java.Security;
using Javax.Crypto;
using Javax.Crypto.Spec;
using LiveTranscribe.Core.Diagnostics;
using LiveTranscribe.Core.Security;

namespace LiveTranscribe.Droid.Platform;

/// <summary>
/// The Android counterpart of the Windows DPAPI store: each value is sealed with an AES-GCM key that lives
/// in the Android Keystore and never leaves it, so the file is useless on any other phone or after a
/// reinstall (FR-11.3). Values are never logged.
/// </summary>
public sealed class KeystoreSecretStore : ISecretStore
{
    private const string KeyAlias = "LiveTranscribe.Secrets";
    private const int IvLength = 12;

    private readonly string _path;
    private readonly IAppLogger _log;
    private readonly object _gate = new();
    private Dictionary<string, string> _sealed;

    public KeystoreSecretStore(string path, IAppLogger log)
    {
        _path = path;
        _log = log;
        _sealed = Load();
    }

    public bool Has(string key)
    {
        lock (_gate)
        {
            return _sealed.ContainsKey(key);
        }
    }

    public string? Get(string key)
    {
        lock (_gate)
        {
            if (!_sealed.TryGetValue(key, out var stored))
            {
                return null;
            }

            try
            {
                var blob = Convert.FromBase64String(stored);
                var cipher = Cipher.GetInstance("AES/GCM/NoPadding")!;
                cipher.Init(CipherMode.DecryptMode, GetOrCreateKey(), new GCMParameterSpec(128, blob[..IvLength]));
                return Encoding.UTF8.GetString(cipher.DoFinal(blob[IvLength..])!);
            }
            catch (Exception ex)
            {
                // A keystore reset (new lock screen, restored backup) makes old values unreadable.
                _log.Error($"Stored secret '{key}' could not be decrypted.", ex);
                return null;
            }
        }
    }

    public void Set(string key, string value)
    {
        lock (_gate)
        {
            var cipher = Cipher.GetInstance("AES/GCM/NoPadding")!;
            cipher.Init(CipherMode.EncryptMode, GetOrCreateKey());
            var iv = cipher.GetIV()!;
            var sealedBytes = cipher.DoFinal(Encoding.UTF8.GetBytes(value))!;
            _sealed[key] = Convert.ToBase64String([.. iv, .. sealedBytes]);
            Save();
            _log.Info($"Secret '{key}' stored ({value.Length} chars).");
        }
    }

    public void Remove(string key)
    {
        lock (_gate)
        {
            if (_sealed.Remove(key))
            {
                Save();
                _log.Info($"Secret '{key}' removed.");
            }
        }
    }

    private static IKey GetOrCreateKey()
    {
        var store = KeyStore.GetInstance("AndroidKeyStore")!;
        store.Load(null);
        if (store.GetKey(KeyAlias, null) is { } existing)
        {
            return existing;
        }

        var generator = KeyGenerator.GetInstance(KeyProperties.KeyAlgorithmAes, "AndroidKeyStore")!;
        generator.Init(new KeyGenParameterSpec.Builder(KeyAlias, KeyStorePurpose.Encrypt | KeyStorePurpose.Decrypt)
            .SetBlockModes(KeyProperties.BlockModeGcm)
            .SetEncryptionPaddings(KeyProperties.EncryptionPaddingNone)
            .SetKeySize(256)
            .Build());
        return generator.GenerateKey()!;
    }

    private Dictionary<string, string> Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_path)) ?? new();
            }
        }
        catch (Exception ex)
        {
            _log.Error("Secrets file unreadable; starting without stored keys.", ex);
        }

        return new();
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_sealed));
        File.Move(temp, _path, overwrite: true);
    }
}
