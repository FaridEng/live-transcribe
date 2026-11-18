namespace LiveTranscribe.Core;

/// <summary>Per-user file locations. Everything the app writes lives under %APPDATA%\LiveTranscribe (FR-11.7, T6).</summary>
public static class AppPaths
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LiveTranscribe");

    public static string SettingsFile => Path.Combine(Root, "settings.json");
    public static string SecretsFile => Path.Combine(Root, "secrets.bin");
    public static string LogDirectory => Path.Combine(Root, "logs");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(LogDirectory);
    }
}
