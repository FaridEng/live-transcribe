using Android.Content;
using Android.Content.PM;
using Android.Media;
using LiveTranscribe.Core.Abstractions;
using LiveTranscribe.Core.Diagnostics;
using Encoding = Android.Media.Encoding;

namespace LiveTranscribe.Droid.Platform;

/// <summary>
/// Records 16 kHz mono 16-bit PCM with <see cref="AudioRecord"/> in 100 ms chunks. The microphone is open
/// only between <see cref="Start"/> and <see cref="StopAsync"/>, and nothing is written to disk.
/// </summary>
public sealed class AndroidAudioCapture : IAudioCapture
{
    private const int SampleRate = 16000;
    private const int ChunkBytes = SampleRate * 2 / 10;

    private readonly Context _context;
    private readonly IAppLogger _log;
    private AudioRecord? _recorder;
    private Task? _reader;
    private volatile bool _stopping;

    public AndroidAudioCapture(Context context, IAppLogger log)
    {
        _context = context;
        _log = log;
    }

    public static AudioDevice PhoneMicrophone { get; } = new(null, "Phone microphone");

    // A phone has one microphone as far as dictation is concerned; Android routes a headset on its own.
    public string? PreferredDeviceId { get; set; }

    public AudioDevice? ActiveDevice { get; private set; }

    public bool UsedFallback => false;

    public void Start(Action<byte[]> onChunk, Action<Exception> onFailure)
    {
        if (_context.CheckSelfPermission(Android.Manifest.Permission.RecordAudio) != Permission.Granted)
        {
            throw new MicrophoneException(MicrophoneFailure.AccessDenied,
                "Microphone permission is off. Open LiveTranscribe and allow it.");
        }

        var minimum = AudioRecord.GetMinBufferSize(SampleRate, ChannelIn.Mono, Encoding.Pcm16bit);
        var recorder = new AudioRecord(AudioSource.VoiceRecognition, SampleRate, ChannelIn.Mono, Encoding.Pcm16bit,
            Math.Max(minimum, ChunkBytes * 4));
        if (recorder.State != State.Initialized)
        {
            recorder.Release();
            throw new MicrophoneException(MicrophoneFailure.Unavailable, "The microphone could not be opened.");
        }

        recorder.StartRecording();
        if (recorder.RecordingState != RecordState.Recording)
        {
            recorder.Release();
            throw new MicrophoneException(MicrophoneFailure.Unavailable,
                "The microphone is busy - another app may be using it.");
        }

        _stopping = false;
        _recorder = recorder;
        ActiveDevice = PhoneMicrophone;
        _reader = Task.Factory.StartNew(() => ReadLoop(recorder, onChunk, onFailure), TaskCreationOptions.LongRunning);
    }

    public async Task StopAsync()
    {
        var recorder = _recorder;
        if (recorder is null)
        {
            return;
        }

        _stopping = true;
        try
        {
            // Stop lets the reader drain what is buffered, then its reads return 0 and it flushes.
            recorder.Stop();
        }
        catch (Exception ex)
        {
            _log.Warn($"Stopping the recorder failed: {ex.Message}");
        }

        if (_reader is { } reader)
        {
            await reader.ConfigureAwait(false);
        }

        recorder.Release();
        _recorder = null;
        _reader = null;
    }

    private void ReadLoop(AudioRecord recorder, Action<byte[]> onChunk, Action<Exception> onFailure)
    {
        var buffer = new byte[ChunkBytes];
        var filled = 0;
        while (true)
        {
            int read;
            try
            {
                read = recorder.Read(buffer, filled, buffer.Length - filled);
            }
            catch (Exception ex)
            {
                if (!_stopping)
                {
                    onFailure(new MicrophoneException(MicrophoneFailure.Disconnected, "The microphone stopped.", ex));
                }

                break;
            }

            if (read > 0)
            {
                filled += read;
                if (filled == buffer.Length)
                {
                    onChunk(buffer);
                    buffer = new byte[ChunkBytes];
                    filled = 0;
                }

                continue;
            }

            if (read < 0 && !_stopping)
            {
                // ERROR_DEAD_OBJECT and friends: the audio route went away mid-dictation (FR-7.4).
                _log.Warn($"Microphone read failed with {read}.");
                onFailure(new MicrophoneException(MicrophoneFailure.Disconnected, "The microphone stopped."));
            }

            break;
        }

        if (filled > 0)
        {
            onChunk(buffer.AsSpan(0, filled).ToArray());
        }
    }
}

/// <summary>The only microphone choice on the phone (FR-7.1 does not apply there).</summary>
public sealed class PhoneDeviceCatalog : IAudioDeviceCatalog
{
    public IReadOnlyList<AudioDevice> List() => [AndroidAudioCapture.PhoneMicrophone];

    public AudioDevice? Resolve(string? preferredId) => AndroidAudioCapture.PhoneMicrophone;
}
