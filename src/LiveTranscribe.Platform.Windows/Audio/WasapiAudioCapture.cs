using System.Runtime.InteropServices;
using LiveTranscribe.Core.Abstractions;
using LiveTranscribe.Core.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace LiveTranscribe.Platform.Windows.Audio;

/// <summary>
/// Shared-mode WASAPI capture converted to 16 kHz mono 16-bit PCM in 100 ms chunks. The device is
/// opened per dictation and released afterwards, so the Windows microphone indicator is off while
/// Ready (FR-7.8). The device is the user's choice, or the Windows default when none is pinned or
/// the pinned one is gone (FR-7.1, FR-7.3).
/// </summary>
public sealed class WasapiAudioCapture : IAudioCapture, IDisposable
{
    private const int TargetRate = 16000;
    private const int ChunkBytes = TargetRate * 2 / 10; // 100 ms of 16-bit mono

    private const int AccessDenied = unchecked((int)0x80070005);

    private readonly IAppLogger _log;
    private readonly object _gate = new();

    // Kept for the app's lifetime: the subscription must outlive every recording it watches.
    private readonly MMDeviceEnumerator? _watchEnumerator;
    private readonly MMDeviceNotificationClient? _watcher;

    // WasapiRecorder (NAudio 3) is the newer API. WasapiCapture stays because it works, and it reports
    // an unplugged device through RecordingStopped, which FR-7.4 needs.
#pragma warning disable CS0618
    private WasapiCapture? _capture;
    private BufferedWaveProvider? _buffer;
    private IWaveProvider? _converted;
    private Action<byte[]>? _onChunk;
    private Action<Exception>? _onFailure;
    private MemoryStream _pending = new();
    private TaskCompletionSource? _stopped;
    private bool _stopRequested;
    private bool _deviceLost;

#pragma warning restore CS0618

    public WasapiAudioCapture(IAppLogger log)
    {
        _log = log;

        // A device that disappears does not always end the capture on its own, so removal is also
        // watched directly (FR-7.4). Set up here, off the activation path.
        try
        {
            _watchEnumerator = new MMDeviceEnumerator();
            _watcher = _watchEnumerator.CreateNotificationClient(useSynchronizationContext: false);
            _watcher.DeviceStateChanged += (_, e) =>
            {
                if (e.NewState != DeviceState.Active)
                {
                    OnDeviceGone(e.DeviceId);
                }
            };
            _watcher.DeviceRemoved += (_, e) => OnDeviceGone(e.DeviceId);
        }
        catch (Exception ex)
        {
            _log.Warn($"Microphone removal is not watched: {ex.Message}");
        }
    }

    public string? PreferredDeviceId { get; set; }

    public AudioDevice? ActiveDevice { get; private set; }

    public bool UsedFallback { get; private set; }

    public void Start(Action<byte[]> onChunk, Action<Exception> onFailure)
    {
        lock (_gate)
        {
            if (_capture is not null)
            {
                throw new InvalidOperationException("Capture is already running.");
            }

            // A blocked desktop app may get silence rather than an error, so ask Windows first (FR-7.5).
            if (MicrophonePrivacy.IsBlocked())
            {
                throw new MicrophoneException(MicrophoneFailure.AccessDenied,
                    "Microphone access is blocked in Windows privacy settings.");
            }

#pragma warning disable CS0618
            WasapiCapture capture;
            WaveFormat format;
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                var device = OpenPreferredDevice(enumerator);
                ActiveDevice = new AudioDevice(device.ID, device.FriendlyName);
                capture = new WasapiCapture(device, useEventSync: true, audioBufferMillisecondsLength: 50);
                format = capture.WaveFormat;
            }
            catch (Exception ex) when (ex is COMException or UnauthorizedAccessException)
            {
                throw Classify(ex, "No microphone is available.");
            }

#pragma warning restore CS0618
            _buffer = new BufferedWaveProvider(format)
            {
                DiscardOnBufferOverflow = true,
                ReadFully = false
            };

            ISampleProvider samples = _buffer.ToSampleProvider();
            if (samples.WaveFormat.Channels > 1)
            {
                samples = new DownmixToMono(samples);
            }

            if (samples.WaveFormat.SampleRate != TargetRate)
            {
                samples = new WdlResamplingSampleProvider(samples, TargetRate);
            }

            _converted = new SampleToWaveProvider16(samples);
            _onChunk = onChunk;
            _onFailure = onFailure;
            _pending = new MemoryStream();
            _stopRequested = false;
            _deviceLost = false;
            _stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            capture.DataAvailable += OnDataAvailable;
            capture.RecordingStopped += OnRecordingStopped;
            _capture = capture;

            try
            {
                capture.StartRecording();
            }
            catch (Exception ex)
            {
                Release();
                throw Classify(ex, "The microphone could not be started.");
            }

            _log.Info($"Microphone opened: {ActiveDevice?.Name}, {format.SampleRate} Hz, {format.Channels} ch, {format.Encoding}.");
        }
    }

    /// <summary>
    /// The chosen device when it is there, otherwise the Windows default with the preference left
    /// alone, so plugging the device back in silently restores it (FR-7.3).
    /// </summary>
    private MMDevice OpenPreferredDevice(MMDeviceEnumerator enumerator)
    {
        UsedFallback = false;
        if (PreferredDeviceId is null)
        {
            return enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
        }

        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
        {
            if (device.ID == PreferredDeviceId)
            {
                return device;
            }

            device.Dispose();
        }

        UsedFallback = true;
        _log.Warn("The saved microphone is not available; falling back to the system default.");
        return enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
    }

    /// <summary>Ends the removal watch. Stop any recording first.</summary>
    public void Dispose()
    {
        _watcher?.Dispose();
        _watchEnumerator?.Dispose();
    }

    public Task StopAsync()
    {
        lock (_gate)
        {
            if (_capture is null || _stopped is null)
            {
                return Task.CompletedTask;
            }

            if (!_stopRequested)
            {
                _stopRequested = true;
                _capture.StopRecording();
            }

            return _stopped.Task;
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        lock (_gate)
        {
            if (_buffer is null)
            {
                return;
            }

            _buffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
            Drain(flush: false);
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        Action<Exception>? failure = null;
        Exception? error = null;
        TaskCompletionSource? stopped;

        lock (_gate)
        {
            Drain(flush: true);

            // Stopped without being asked: device unplugged or taken away (FR-7.4).
            if (!_stopRequested || _deviceLost || e.Exception is not null)
            {
                error = e.Exception is { } cause
                    ? Classify(cause, "The microphone stopped unexpectedly.", MicrophoneFailure.Disconnected)
                    : new MicrophoneException(MicrophoneFailure.Disconnected, "The microphone was disconnected.");
                failure = _onFailure;
            }

            stopped = _stopped;
            Release();
        }

        _log.Info("Microphone released.");
        stopped?.TrySetResult();
        if (error is not null)
        {
            failure?.Invoke(error);
        }
    }

    /// <summary>
    /// Called on an audio system thread that holds a lock, so it only hands the stop to the pool.
    /// Stopping ends in <see cref="OnRecordingStopped"/>, which reports the loss (FR-7.4).
    /// </summary>
    private void OnDeviceGone(string? deviceId)
    {
        if (deviceId is null)
        {
            return;
        }

        Task.Run(() =>
        {
            lock (_gate)
            {
                if (_capture is null || _stopRequested || ActiveDevice?.Id != deviceId)
                {
                    return;
                }

                _log.Warn("The microphone in use was disconnected.");
                _deviceLost = true;
                _stopRequested = true;
                try
                {
                    _capture.StopRecording();
                }
                catch (Exception ex)
                {
                    _log.Warn($"Stopping the lost microphone failed: {ex.Message}");
                }
            }
        });
    }

    /// <summary>E_ACCESSDENIED means Windows privacy settings block the microphone (FR-7.5).</summary>
    private static MicrophoneException Classify(Exception ex, string message,
        MicrophoneFailure otherwise = MicrophoneFailure.Unavailable)
    {
        if (ex is MicrophoneException known)
        {
            return known;
        }

        return ex is UnauthorizedAccessException || ex.HResult == AccessDenied
            ? new MicrophoneException(MicrophoneFailure.AccessDenied, "Microphone access is blocked in Windows privacy settings.", ex)
            : new MicrophoneException(otherwise, message, ex);
    }

    private void Drain(bool flush)
    {
        if (_converted is null || _onChunk is null)
        {
            return;
        }

        var scratch = new byte[ChunkBytes];
        int read;
        while ((read = _converted.Read(scratch.AsSpan())) > 0)
        {
            _pending.Write(scratch, 0, read);
        }

        var pending = _pending.ToArray();
        var offset = 0;
        while (pending.Length - offset >= ChunkBytes)
        {
            _onChunk(pending.AsSpan(offset, ChunkBytes).ToArray());
            offset += ChunkBytes;
        }

        if (flush && pending.Length - offset > 0)
        {
            _onChunk(pending.AsSpan(offset).ToArray());
            offset = pending.Length;
        }

        _pending = new MemoryStream();
        _pending.Write(pending, offset, pending.Length - offset);
    }

    private void Release()
    {
        if (_capture is not null)
        {
            _capture.DataAvailable -= OnDataAvailable;
            _capture.RecordingStopped -= OnRecordingStopped;
            // Dispose off this thread: it may be the capture thread, which Dispose would wait on.
            var capture = _capture;
            Task.Run(capture.Dispose);
        }

        _capture = null;
        _buffer = null;
        _converted = null;
        _onChunk = null;
        _onFailure = null;
    }

    /// <summary>Averages any number of channels into one.</summary>
    private sealed class DownmixToMono : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly int _channels;
        private float[] _scratch = [];

        public DownmixToMono(ISampleProvider source)
        {
            _source = source;
            _channels = source.WaveFormat.Channels;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public int Read(Span<float> buffer)
        {
            var needed = buffer.Length * _channels;
            if (_scratch.Length < needed)
            {
                _scratch = new float[needed];
            }

            var read = _source.Read(_scratch.AsSpan(0, needed));
            var frames = read / _channels;
            for (var frame = 0; frame < frames; frame++)
            {
                var sum = 0f;
                for (var channel = 0; channel < _channels; channel++)
                {
                    sum += _scratch[frame * _channels + channel];
                }

                buffer[frame] = sum / _channels;
            }

            return frames;
        }
    }
}
