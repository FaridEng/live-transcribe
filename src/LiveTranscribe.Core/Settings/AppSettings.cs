namespace LiveTranscribe.Core.Settings;

public enum LanguageMode { Auto, Persian, English }

/// <summary>The caption shown under the spoken-language choice, shared by Windows and Android.</summary>
public static class LanguageModes
{
    public static string Describe(LanguageMode mode) => mode switch
    {
        LanguageMode.Persian =>
            "Persian only: the engine is told you speak Persian. Best when you mostly speak Persian; " +
            "English words you drop in may come out in Persian script.",
        LanguageMode.English =>
            "English only: the engine is told you speak English. Persian speech will come out wrong.",
        _ =>
            "Auto: the engine works out the language itself, so Persian and English can mix in one sentence. " +
            "Pick Persian only if it ever mistakes your Persian for another language."
    };
}
public enum PersianStyle { Colloquial, Formal }
public enum InsertionMode { AfterStop, Streaming }
public enum ActivationMode { Hold, Toggle }

/// <summary>What a dictation produces: the words as spoken, or their English translation (architecture T14).</summary>
public enum DictationOutput { Transcript, English }

public sealed class HotkeySettings
{
    /// <summary>Virtual-key code matched directly, so any keyboard layout works (FR-2.4).</summary>
    public int HoldVirtualKey { get; set; } = 0xA3;           // Right Ctrl

    /// <summary>
    /// Optional modifiers held together with <see cref="HoldVirtualKey"/> (user request, 2026-09-28):
    /// both the modifier and the key must be held down at once, and releasing either ends the session.
    /// False by default for every flag, which reproduces the old single-key behaviour exactly.
    /// </summary>
    public bool HoldRequiresCtrl { get; set; }
    public bool HoldRequiresAlt { get; set; }
    public bool HoldRequiresShift { get; set; }

    public int ToggleVirtualKey { get; set; } = 0x20;          // Space
    public bool ToggleRequiresCtrl { get; set; } = true;
    public bool ToggleRequiresAlt { get; set; } = true;
    public bool ToggleRequiresShift { get; set; }
    public ActivationMode DefaultMode { get; set; } = ActivationMode.Hold;

    /// <summary>
    /// Pastes the latest dictation again, for when it went to a window with no text field and was
    /// lost without an error (FR-9.3). Ctrl + Alt + Z by default; 0 turns it off. Not Alt + Shift,
    /// which switches the keyboard layout on Windows.
    /// </summary>
    public int PasteLastVirtualKey { get; set; } = 0x5A;       // Z
    public bool PasteLastRequiresCtrl { get; set; } = true;
    public bool PasteLastRequiresAlt { get; set; } = true;
    public bool PasteLastRequiresShift { get; set; }

    /// <summary>
    /// Hold to dictate in English (architecture T14). Right Shift by default, the user's pick; while it is
    /// the translate key, only Left Shift types capitals. 0 turns it off.
    /// </summary>
    public int TranslateHoldVirtualKey { get; set; } = 0xA1;   // Right Shift

    /// <summary>Modifiers held together with <see cref="TranslateHoldVirtualKey"/>; see <see cref="HoldRequiresCtrl"/>.</summary>
    public bool TranslateHoldRequiresCtrl { get; set; }
    public bool TranslateHoldRequiresAlt { get; set; }
    public bool TranslateHoldRequiresShift { get; set; }

    /// <summary>Hands-free translation: Ctrl + Alt + T by default; 0 turns it off.</summary>
    public int TranslateToggleVirtualKey { get; set; } = 0x54; // T
    public bool TranslateToggleRequiresCtrl { get; set; } = true;
    public bool TranslateToggleRequiresAlt { get; set; } = true;
    public bool TranslateToggleRequiresShift { get; set; }

    /// <summary>Shorter presses are discarded with no API call (FR-2.6).</summary>
    public int MinimumHoldMilliseconds { get; set; } = 300;
}

public sealed class MicrophoneSettings
{
    /// <summary>Null means "System default", which follows Windows (FR-7.1).</summary>
    public string? DeviceId { get; set; }
    public string? DeviceName { get; set; }
}

public sealed class DictationSettings
{
    public LanguageMode Language { get; set; } = LanguageMode.Auto;
    public PersianStyle PersianStyle { get; set; } = PersianStyle.Colloquial;
    public InsertionMode Insertion { get; set; } = InsertionMode.AfterStop;

    /// <summary>Capped by the engine session limit; Gemini Live allows 10 minutes (FR-9.5).</summary>
    public int MaxSessionSeconds { get; set; } = 540;

    /// <summary>
    /// Off by default: it cut hands-free dictation short on a quiet microphone, and being dropped
    /// mid-sentence is worse than a session that runs until the user stops it. The maximum session
    /// length still protects a forgotten session (FR-9.5).
    /// </summary>
    public bool StopOnSilence { get; set; }

    public int SilenceStopSeconds { get; set; } = 6;

    /// <summary>Off by default: a newline sends the message in chat apps (D8).</summary>
    public bool AllowLineBreaks { get; set; }
}

public sealed class UiSettings
{
    public bool FloatingControlEnabled { get; set; } = true;
    public bool SoundsEnabled { get; set; } = true;
    public Dictionary<string, ControlPosition> FloatingControlPositions { get; set; } = new();

    /// <summary>What a click on the pill produces, switched with the FA/EN chip (architecture T14).</summary>
    public DictationOutput PillOutput { get; set; } = DictationOutput.Transcript;
}

/// <summary>
/// Translation to English (architecture T14, beyond the PRD). The model decides the route: a Live model
/// hears the audio, a text model translates the dictation transcript.
/// </summary>
public sealed class TranslationSettings
{
    public string ModelId { get; set; } = "gemini-3.5-flash-lite";

    /// <summary>The models ticked for a side-by-side comparison in the translation test window.</summary>
    public List<string> CompareModelIds { get; set; } = ["gemini-3.5-flash-lite", "gemini-3.8-live", "gemini-3.5-live-translate-preview"];

    /// <summary>What the user is writing about, typed on the pill. Clears itself after an hour unused.</summary>
    public string? TopicNote { get; set; }

    /// <summary>When the note was last written or used; the hour of disuse counts from here.</summary>
    public DateTimeOffset? TopicNoteTouchedAt { get; set; }

    /// <summary>Picked on the pill; it wins over a tone the topic note asks for.</summary>
    public LiveTranscribe.Core.Translation.TranslationTone Tone { get; set; }
}

public sealed class ControlPosition
{
    public double X { get; set; }
    public double Y { get; set; }
}

/// <summary>Everything in FR-8. Secrets live in the secret store, never here.</summary>
public sealed class AppSettings
{
    /// <summary>Bumped when a stored file needs migrating; see <see cref="JsonSettingsStore"/>.</summary>
    public const int CurrentVersion = 2;

    public int Version { get; set; } = CurrentVersion;
    public HotkeySettings Hotkeys { get; set; } = new();
    public MicrophoneSettings Microphone { get; set; } = new();
    public DictationSettings Dictation { get; set; } = new();
    public UiSettings Ui { get; set; } = new();
    public TranslationSettings Translation { get; set; } = new();
    public List<EngineProfile> EngineProfiles { get; set; } = new();
    public string? ActiveEngineProfileId { get; set; }
    public string? FallbackEngineProfileId { get; set; }
    public bool LaunchAtSignIn { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public EngineProfile? ActiveProfile =>
        EngineProfiles.FirstOrDefault(p => p.Id == ActiveEngineProfileId) ?? EngineProfiles.FirstOrDefault();

    public static AppSettings CreateDefault()
    {
        var live = new EngineProfile
        {
            Name = "Gemini 3.5 Transcribe Live",
            EngineType = EngineType.GeminiLive,
            ModelId = "gemini-3.5-transcribe-live"
        };
        return new AppSettings
        {
            EngineProfiles = { live },
            ActiveEngineProfileId = live.Id
        };
    }
}
