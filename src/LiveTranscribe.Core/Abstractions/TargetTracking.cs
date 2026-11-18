namespace LiveTranscribe.Core.Abstractions;

/// <summary>
/// Where a dictation is meant to land: the foreground window captured when dictation was activated
/// (FR-1.1). A null snapshot means no target could be captured, which does not block dictation
/// (FR-1.2) but also locks nothing.
/// </summary>
public sealed record TargetSnapshot(nint Window, int ProcessId, string ProcessName);

/// <summary>Where the caret is at insertion time compared with the snapshot (FR-3.6, D5).</summary>
public enum TargetCheck
{
    /// <summary>Nothing was locked, so the text goes to whatever has the caret now (FR-1.2).</summary>
    Unknown,

    /// <summary>Same window as at activation; a different field inside it is still fine (FR-3.6).</summary>
    SameWindow,

    /// <summary>The foreground window changed: do not insert, keep the text instead (D5).</summary>
    DifferentWindow,

    /// <summary>The caret is in a password field: never insert there (FR-3.7, D7).</summary>
    PasswordField,

    /// <summary>
    /// Nothing that takes text has focus (and it is not another window than the snapshot). The dictation goes
    /// to the clipboard for a manual paste (T13). Only reported when the platform is sure; a timeout is Unknown.
    /// </summary>
    NoTextField
}

/// <summary>
/// Tracks the dictation target. Window identity comes from cheap Win32 calls; whether the focused
/// element is a password field needs UI Automation, which is slow in some apps and therefore always
/// runs with a timeout. A timeout means "unknown", never "safe".
/// </summary>
public interface ITargetTracker
{
    /// <summary>The foreground window and its process right now. Null when there is nothing to lock.</summary>
    TargetSnapshot? Capture();

    /// <summary>True when the focused element is a password field. Unknown within the timeout is false.</summary>
    Task<bool> IsPasswordFieldAsync(TimeSpan timeout, CancellationToken ct);

    /// <summary>Compares the current caret location with the snapshot.</summary>
    Task<TargetCheck> CheckAsync(TargetSnapshot? snapshot, TimeSpan timeout, CancellationToken ct);
}

/// <summary>Puts text on the clipboard for the recovery path, without pasting it (FR-9.3).</summary>
public interface IClipboardWriter
{
    void SetText(string text);
}
