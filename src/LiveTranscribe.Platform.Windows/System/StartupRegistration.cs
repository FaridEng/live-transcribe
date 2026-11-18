using Microsoft.Win32;
using LiveTranscribe.Core.Diagnostics;

namespace LiveTranscribe.Platform.Windows.System;

/// <summary>
/// "Launch at sign-in" (FR-12.1), through the per-user Run key. That needs no administrator rights and
/// no UAC prompt, which suits an app that runs as the signed-in user (M7 and its elevated Task Scheduler
/// task were dropped, T15). The key holds the full path of the running executable, so a build in a new
/// folder corrects itself on the next start rather than launching a copy that no longer exists.
/// </summary>
public static class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "LiveTranscribe";

    /// <summary>The command the Run key currently holds, or null when sign-in launching is off.</summary>
    public static string? Current()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue(ValueName) as string;
    }

    /// <summary>
    /// Turns sign-in launching on or off for this user. Returns false, with the reason logged, when
    /// Windows refuses: the setting is saved either way, so Settings can say it did not take.
    /// </summary>
    public static bool Apply(bool enabled, IAppLogger log)
    {
        var command = Command();
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key is null)
            {
                log.Warn("Launch at sign-in: the Run key could not be opened.");
                return false;
            }

            if (!enabled)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                log.Info("Launch at sign-in turned off.");
                return true;
            }

            if ((key.GetValue(ValueName) as string) == command)
            {
                return true;
            }

            key.SetValue(ValueName, command, RegistryValueKind.String);
            log.Info("Launch at sign-in turned on.");
            return true;
        }
        catch (Exception ex)
        {
            log.Error("Launch at sign-in could not be changed.", ex);
            return false;
        }
    }

    /// <summary>
    /// Keeps the Run key in step with the setting at every start: it repairs a stale path after the app
    /// is moved, and clears an entry left behind if the setting was turned off elsewhere.
    /// </summary>
    public static void Reconcile(bool enabled, IAppLogger log)
    {
        var current = Current();
        if (enabled == (current == Command()))
        {
            return;
        }

        Apply(enabled, log);
    }

    /// <summary>Quoted, because the path contains spaces in most install locations.</summary>
    private static string Command() =>
        Environment.ProcessPath is { Length: > 0 } path ? $"\"{path}\"" : string.Empty;
}
