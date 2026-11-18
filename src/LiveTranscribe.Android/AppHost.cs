using Android.Content;
using LiveTranscribe.Core;
using LiveTranscribe.Core.Diagnostics;
using LiveTranscribe.Core.Dictation;
using LiveTranscribe.Core.Settings;
using LiveTranscribe.Core.Translation;
using LiveTranscribe.Droid.Platform;
using LiveTranscribe.Engines.Gemini;

namespace LiveTranscribe.Droid;

/// <summary>
/// The composition root, shared by the settings screen and the accessibility service because they run in
/// one process. The same Core and Gemini engine as on Windows; only the platform services differ.
/// Created on the main thread, which the dictation controller reports back to.
/// </summary>
public sealed class AppHost
{
    private static AppHost? _instance;

    private AppHost(Context context)
    {
        AppPaths.EnsureCreated();
        Log = new FileLogger(AppPaths.LogDirectory);
        Log.Info($"Starting on Android {Android.OS.Build.VERSION.Release} (API {(int)Android.OS.Build.VERSION.SdkInt}).");

        Settings = new JsonSettingsStore(AppPaths.SettingsFile, Log);
        Secrets = new KeystoreSecretStore(AppPaths.SecretsFile, Log);
        Engines = new GeminiEngineFactory();
        Translators = new GeminiTranslatorFactory(Engines);
        Translation = new TranslationService(Translators, Settings);

        var inserter = new AccessibilityTextInserter(context, Log);
        Dictation = new DictationController(
            new AndroidAudioCapture(context, Log),
            new PhoneDeviceCatalog(),
            Engines,
            inserter,
            new AndroidClipboardWriter(context),
            new AccessibilityTargetTracker(Log),
            Settings,
            Secrets,
            Log,
            inserter,
            Translation);
    }

    public IAppLogger Log { get; }
    public ISettingsStore Settings { get; }
    public KeystoreSecretStore Secrets { get; }
    public GeminiEngineFactory Engines { get; }
    public GeminiTranslatorFactory Translators { get; }
    public TranslationService Translation { get; }
    public DictationController Dictation { get; }

    /// <summary>Must be called on the main thread.</summary>
    public static AppHost Get(Context context) => _instance ??= new AppHost(context.ApplicationContext ?? context);
}
