using global::System.Security.Principal;

namespace LiveTranscribe.Platform.Windows.System;

/// <summary>Insertion into elevated apps only works from an elevated process (D18, architecture §2).</summary>
public static class ElevationInfo
{
    public static bool IsElevated
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }
}
