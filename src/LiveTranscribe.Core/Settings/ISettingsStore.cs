namespace LiveTranscribe.Core.Settings;

/// <summary>Settings apply without a restart, so consumers listen to <see cref="Changed"/> (FR-8).</summary>
public interface ISettingsStore
{
    AppSettings Current { get; }
    event Action<AppSettings>? Changed;
    void Save(AppSettings settings);
}
