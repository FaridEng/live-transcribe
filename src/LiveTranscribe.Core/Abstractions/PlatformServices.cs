namespace LiveTranscribe.Core.Abstractions;

/// <summary>Microphone capture delivering 16 kHz mono 16-bit PCM in ~100 ms chunks.</summary>
public interface IAudioCapture
{
    /// <summary>
    /// Which device to open. Null means "System default", so the capture follows Windows (FR-7.1).
    /// A device that has gone missing falls back to the default without clearing this (FR-7.3).
    /// </summary>
    string? PreferredDeviceId { get; set; }

    /// <summary>The device the last successful <see cref="Start"/> actually opened.</summary>
    AudioDevice? ActiveDevice { get; }

    /// <summary>True when <see cref="PreferredDeviceId"/> was missing and the default was used.</summary>
    bool UsedFallback { get; }

    /// <summary>
    /// Opens the microphone and starts delivering chunks. The microphone stays closed until this is
    /// called (FR-7.8). Throws <see cref="MicrophoneException"/> when capture cannot start.
    /// </summary>
    void Start(Action<byte[]> onChunk, Action<Exception> onFailure);

    /// <summary>Stops capture, flushes the last partial chunk, and releases the microphone.</summary>
    Task StopAsync();
}

/// <summary>Why the microphone failed, so the message can say what to do about it (FR-6.4).</summary>
public enum MicrophoneFailure
{
    /// <summary>No device, or it could not be opened.</summary>
    Unavailable,

    /// <summary>Windows privacy settings block microphone access for desktop apps (FR-7.5).</summary>
    AccessDenied,

    /// <summary>The device went away while recording (FR-7.4).</summary>
    Disconnected
}

public sealed class MicrophoneException : Exception
{
    public MicrophoneException(MicrophoneFailure reason, string message, Exception? inner = null) : base(message, inner)
    {
        Reason = reason;
    }

    public MicrophoneFailure Reason { get; }
}

public sealed record InsertResult(bool Success, string? Failure = null);

/// <summary>Puts text at the caret of whatever field currently has focus.</summary>
public interface ITextInserter
{
    Task<InsertResult> InsertAsync(string text, CancellationToken ct);
}
