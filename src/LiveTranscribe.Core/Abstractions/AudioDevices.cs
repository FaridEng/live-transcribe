namespace LiveTranscribe.Core.Abstractions;

/// <summary>
/// A microphone the user can pick. A null <paramref name="Id"/> is "System default", which follows
/// whatever Windows is set to rather than pinning one device (FR-7.1).
/// </summary>
public sealed record AudioDevice(string? Id, string Name)
{
    public bool IsSystemDefault => Id is null;
}

/// <summary>Lists the microphones and says which one a preference would actually open (FR-7.1, FR-7.3).</summary>
public interface IAudioDeviceCatalog
{
    /// <summary>"System default" first, then every active capture device.</summary>
    IReadOnlyList<AudioDevice> List();

    /// <summary>
    /// The device that would be opened for this preference: the preferred one when it is present,
    /// the Windows default when it is not, and null when there is no microphone at all.
    /// </summary>
    AudioDevice? Resolve(string? preferredId);
}
