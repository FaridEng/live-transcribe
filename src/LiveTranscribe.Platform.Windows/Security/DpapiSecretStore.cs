using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using LiveTranscribe.Core.Diagnostics;
using LiveTranscribe.Core.Security;

namespace LiveTranscribe.Platform.Windows.Security;

/// <summary>
/// Secrets encrypted with DPAPI in the current user's scope, so the file is useless to another
/// Windows account (FR-11.3). CryptProtectData is called directly to avoid an extra dependency.
/// </summary>
public sealed class DpapiSecretStore : ISecretStore
{
    private const uint CryptprotectUiForbidden = 0x1;

    private readonly string _path;
    private readonly IAppLogger _log;
    private readonly object _gate = new();
    private Dictionary<string, string> _secrets;

    public DpapiSecretStore(string path, IAppLogger log)
    {
        _path = path;
        _log = log;
        _secrets = Load();
    }

    public bool Has(string key) => Get(key) is { Length: > 0 };

    public string? Get(string key)
    {
        lock (_gate)
        {
            return _secrets.TryGetValue(key, out var value) ? value : null;
        }
    }

    public void Set(string key, string value)
    {
        lock (_gate)
        {
            _secrets[key] = value;
            Persist();
        }
    }

    public void Remove(string key)
    {
        lock (_gate)
        {
            if (_secrets.Remove(key))
            {
                Persist();
            }
        }
    }

    private Dictionary<string, string> Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return new Dictionary<string, string>(StringComparer.Ordinal);
            }

            var plain = Unprotect(File.ReadAllBytes(_path));
            var json = Encoding.UTF8.GetString(plain);
            Array.Clear(plain);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                   ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            // A key that cannot be decrypted (copied profile, corrupt file) must not block startup.
            _log.Error("Stored secrets could not be read; the API key must be entered again.", ex);
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private void Persist()
    {
        Core.AppPaths.EnsureCreated();
        var json = JsonSerializer.Serialize(_secrets);
        var cipher = Protect(Encoding.UTF8.GetBytes(json));

        var temp = _path + ".tmp";
        File.WriteAllBytes(temp, cipher);
        File.Move(temp, _path, overwrite: true);
    }

    private static byte[] Protect(byte[] plain)
    {
        var input = new DataBlob(plain);
        var output = default(DataBlob);
        try
        {
            if (!CryptProtectData(ref input, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptprotectUiForbidden, ref output))
            {
                throw new InvalidOperationException($"CryptProtectData failed ({Marshal.GetLastWin32Error()}).");
            }

            return output.ToArray();
        }
        finally
        {
            input.Free();
            output.Free();
        }
    }

    private static byte[] Unprotect(byte[] cipher)
    {
        var input = new DataBlob(cipher);
        var output = default(DataBlob);
        try
        {
            if (!CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptprotectUiForbidden, ref output))
            {
                throw new InvalidOperationException($"CryptUnprotectData failed ({Marshal.GetLastWin32Error()}).");
            }

            return output.ToArray();
        }
        finally
        {
            input.Free();
            output.Free();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;

        public DataBlob(byte[] bytes)
        {
            Size = bytes.Length;
            Data = Marshal.AllocHGlobal(bytes.Length);
            Marshal.Copy(bytes, 0, Data, bytes.Length);
        }

        public readonly byte[] ToArray()
        {
            var bytes = new byte[Size];
            Marshal.Copy(Data, bytes, 0, Size);
            return bytes;
        }

        public void Free()
        {
            if (Data != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(Data);
                Data = IntPtr.Zero;
                Size = 0;
            }
        }
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob input, string? description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, uint flags, ref DataBlob output);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, uint flags, ref DataBlob output);
}
