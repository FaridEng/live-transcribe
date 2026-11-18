using Microsoft.Win32;

namespace LiveTranscribe.Platform.Windows.Audio;

/// <summary>
/// Reads the Windows microphone privacy switches (FR-7.5). Depending on the Windows build, a blocked
/// desktop app either fails to open the device or receives silence, so the switches are checked
/// before opening, and the failure is named instead of being guessed from silence.
/// </summary>
public static class MicrophonePrivacy
{
    private const string ConsentStore =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";

    /// <summary>Opens the Windows page where the user can allow the microphone.</summary>
    public const string SettingsUri = "ms-settings:privacy-microphone";

    /// <summary>
    /// True when any of the three switches is off: microphone access for this device (all users),
    /// for apps in this account, or for desktop apps in this account.
    /// </summary>
    public static bool IsBlocked()
    {
        try
        {
            return IsDenied(Registry.LocalMachine, ConsentStore)
                   || IsDenied(Registry.CurrentUser, ConsentStore)
                   || IsDenied(Registry.CurrentUser, ConsentStore + @"\NonPackaged");
        }
        catch
        {
            // Unreadable means unknown; opening the device will tell.
            return false;
        }
    }

    private static bool IsDenied(RegistryKey hive, string path)
    {
        using var key = hive.OpenSubKey(path);
        return string.Equals(key?.GetValue("Value") as string, "Deny", StringComparison.OrdinalIgnoreCase);
    }
}
