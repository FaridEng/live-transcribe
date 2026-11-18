namespace LiveTranscribe.Core.Dictation;

/// <summary>The canonical states from FR-6.1. Transient ones fall back to <see cref="Ready"/> on their own.</summary>
public enum DictationState
{
    NotConfigured,
    Ready,
    Listening,
    Processing,
    Inserted,
    Cancelled,
    NoSpeech,
    Busy,
    Paused,
    BlockedPasswordField,
    NotInsertedTextKept,
    ErrorMicrophone,
    ErrorNetwork,
    ErrorAuthentication,
    ErrorQuota,
    ErrorTranscription
}

/// <summary>One-line news for the user that is not a state of its own.</summary>
public enum DictationNotice
{
    /// <summary>The microphone went away mid-dictation; what was said is being transcribed (FR-7.4).</summary>
    MicrophoneLostTranscribing,

    /// <summary>Nothing but silence comes from the microphone: muted, or the wrong device (FR-7.7).</summary>
    MicrophoneSilent,

    /// <summary>
    /// An overridden password dictation could not be inserted, so the text was thrown away instead of
    /// being kept for copying (FR-3.7).
    /// </summary>
    PasswordTextDiscarded,

    /// <summary>No text field had focus, so the dictation was put on the clipboard for a manual paste (T13).</summary>
    CopiedToClipboard
}

public static class DictationStateInfo
{
    public static bool IsTransient(this DictationState state) => state switch
    {
        DictationState.Inserted or DictationState.Cancelled or DictationState.NoSpeech or DictationState.Busy
            or DictationState.BlockedPasswordField => true,
        _ => false
    };

    public static bool IsError(this DictationState state) => state is
        DictationState.ErrorMicrophone or DictationState.ErrorNetwork or DictationState.ErrorAuthentication or
        DictationState.ErrorQuota or DictationState.ErrorTranscription;

    /// <summary>The microphone is live only here, and that must always be visible (FR-6.2, FR-11.1).</summary>
    public static bool IsMicrophoneLive(this DictationState state) => state == DictationState.Listening;

    public static string ToDisplayText(this DictationState state) => state switch
    {
        DictationState.NotConfigured => "Not configured",
        DictationState.Ready => "Ready",
        DictationState.Listening => "Listening",
        DictationState.Processing => "Processing",
        DictationState.Inserted => "Inserted",
        DictationState.Cancelled => "Cancelled",
        DictationState.NoSpeech => "No speech detected",
        DictationState.Busy => "Busy",
        DictationState.Paused => "Paused",
        DictationState.BlockedPasswordField => "Blocked - password field",
        DictationState.NotInsertedTextKept => "Not inserted - text kept",
        DictationState.ErrorMicrophone => "Microphone error",
        DictationState.ErrorNetwork => "Connection error",
        DictationState.ErrorAuthentication => "Invalid API key",
        DictationState.ErrorQuota => "Quota exceeded",
        DictationState.ErrorTranscription => "Transcription error",
        _ => state.ToString()
    };
}
