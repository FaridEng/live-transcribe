using LiveTranscribe.Core.Abstractions;
using LiveTranscribe.Core.Diagnostics;
using NAudio.CoreAudioApi;

namespace LiveTranscribe.Platform.Windows.Audio;

/// <summary>
/// The active capture endpoints, read fresh every time. Devices appear and disappear while the app
/// runs (a headset is plugged in, Bluetooth reconnects), so nothing here is cached (FR-7.1, FR-7.3).
/// </summary>
public sealed class WasapiDeviceCatalog : IAudioDeviceCatalog
{
    private readonly IAppLogger _log;

    public WasapiDeviceCatalog(IAppLogger log)
    {
        _log = log;
    }

    public IReadOnlyList<AudioDevice> List()
    {
        var devices = new List<AudioDevice> { new(null, SystemDefaultName()) };

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                using (device)
                {
                    devices.Add(new AudioDevice(device.ID, device.FriendlyName));
                }
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"Could not list the microphones: {ex.Message}");
        }

        return devices;
    }

    public AudioDevice? Resolve(string? preferredId)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            if (preferredId is not null)
            {
                foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
                {
                    using (device)
                    {
                        if (device.ID == preferredId)
                        {
                            return new AudioDevice(device.ID, device.FriendlyName);
                        }
                    }
                }
            }

            using var fallback = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
            return new AudioDevice(fallback.ID, fallback.FriendlyName);
        }
        catch (Exception ex)
        {
            _log.Warn($"Could not resolve a microphone: {ex.Message}");
            return null;
        }
    }

    /// <summary>Names the default entry after the device behind it, so the menu is not a riddle.</summary>
    private string SystemDefaultName()
    {
        var current = Resolve(null);
        return current is null ? "System default" : $"System default ({current.Name})";
    }
}
