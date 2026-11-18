using System;
using System.IO;
using System.Media;

namespace LiveTranscribe.App.Sounds;

/// <summary>
/// Short start, stop and error tones (FR-6.5). Generated in memory, so there are no audio assets.
/// The start tone is deliberately quiet and short because the microphone is already open.
/// </summary>
internal sealed class SoundCues : IDisposable
{
    private const int SampleRate = 44100;

    private readonly SoundPlayer _start;
    private readonly SoundPlayer _stop;
    private readonly SoundPlayer _error;

    public SoundCues()
    {
        _start = Create((660, 0.05), (880, 0.07));
        _stop = Create((880, 0.05), (660, 0.07));
        _error = Create((330, 0.09), (0, 0.04), (330, 0.09));
    }

    public bool Enabled { get; set; } = true;

    public void PlayStart() => Play(_start);

    public void PlayStop() => Play(_stop);

    public void PlayError() => Play(_error);

    public void Dispose()
    {
        _start.Dispose();
        _stop.Dispose();
        _error.Dispose();
    }

    private void Play(SoundPlayer player)
    {
        if (!Enabled)
        {
            return;
        }

        try
        {
            player.Play();
        }
        catch
        {
            // No audio output device: cues are optional.
        }
    }

    private static SoundPlayer Create(params (double Frequency, double Seconds)[] tones)
    {
        var stream = new MemoryStream();
        var samples = 0;
        foreach (var tone in tones)
        {
            samples += (int)(tone.Seconds * SampleRate);
        }

        using (var writer = new BinaryWriter(stream, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            writer.Write("RIFF"u8.ToArray());
            writer.Write(36 + samples * 2);
            writer.Write("WAVEfmt "u8.ToArray());
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)1);
            writer.Write(SampleRate);
            writer.Write(SampleRate * 2);
            writer.Write((short)2);
            writer.Write((short)16);
            writer.Write("data"u8.ToArray());
            writer.Write(samples * 2);

            foreach (var (frequency, seconds) in tones)
            {
                var count = (int)(seconds * SampleRate);
                for (var i = 0; i < count; i++)
                {
                    // Short fade in and out avoids clicks.
                    var envelope = Math.Min(1.0, Math.Min(i, count - i) / (SampleRate * 0.008));
                    var value = frequency <= 0 ? 0 : Math.Sin(2 * Math.PI * frequency * i / SampleRate) * envelope * 0.18;
                    writer.Write((short)(value * short.MaxValue));
                }
            }
        }

        stream.Position = 0;
        var player = new SoundPlayer(stream);
        player.Load();
        return player;
    }
}
