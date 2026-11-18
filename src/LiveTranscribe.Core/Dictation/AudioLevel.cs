namespace LiveTranscribe.Core.Dictation;

/// <summary>Loudness of a PCM chunk, for the level meter and silence detection (FR-7.6, FR-7.7, FR-8).</summary>
public static class AudioLevel
{
    /// <summary>
    /// About -66 dBFS. A working microphone in a quiet room stays above this; a muted one, a wrong
    /// input or a blocked stream delivers less, often exact zeros (FR-7.7).
    /// </summary>
    public const float SilenceLevel = 0.0005f;

    /// <summary>Root mean square of 16-bit little-endian mono PCM, from 0 to 1.</summary>
    public static float Rms(ReadOnlySpan<byte> pcm16)
    {
        var samples = pcm16.Length / 2;
        if (samples == 0)
        {
            return 0f;
        }

        double sum = 0;
        for (var i = 0; i < samples; i++)
        {
            var sample = (short)(pcm16[i * 2] | (pcm16[i * 2 + 1] << 8)) / 32768.0;
            sum += sample * sample;
        }

        return (float)Math.Sqrt(sum / samples);
    }

    /// <summary>Decibels relative to full scale, for showing a level to the user.</summary>
    public static double ToDecibels(float level) => level <= 0 ? -96 : Math.Max(-96, 20 * Math.Log10(level));
}

/// <summary>
/// Measures the background noise of one recording and calls a chunk "voice" only when it is clearly
/// louder than that. The fixed -40 dBFS threshold of M3 was above ordinary speech on a quiet
/// microphone, so auto-stop cut hands-free sessions mid-sentence; a measured floor adapts to the
/// microphone and the room instead (FR-7.7, FR-8).
/// </summary>
public sealed class NoiseFloorTracker
{
    /// <summary>The floor is the quietest chunk of the last 5 s, so it follows a room that gets louder.</summary>
    private const int WindowChunks = 50;

    /// <summary>Voice must be about 10 dB above the floor.</summary>
    private const float VoiceMargin = 3f;

    /// <summary>-60 dBFS: below this nothing counts as voice, however quiet the room.</summary>
    public const float MinimumVoiceLevel = 0.001f;

    /// <summary>-30 dBFS: normal speech is louder than this, so a noisy room never hides it.</summary>
    public const float MaximumVoiceLevel = 0.03f;

    /// <summary>Noise gates in some drivers deliver exact zeros between words; those are not the floor.</summary>
    private const float GateLevel = 0.00002f;

    private readonly Queue<float> _recent = new();

    /// <summary>The measured background level, or null until there is something to measure.</summary>
    public float? Floor { get; private set; }

    public float VoiceThreshold =>
        Floor is { } floor ? Math.Clamp(floor * VoiceMargin, MinimumVoiceLevel, MaximumVoiceLevel) : MinimumVoiceLevel;

    /// <summary>Adds the next ~100 ms chunk and returns whether it was voice.</summary>
    public bool Add(float level)
    {
        // Judged against the floor before this chunk, so a recording that starts with speech still
        // counts its first word as voice.
        var voice = level >= VoiceThreshold;

        if (level > GateLevel)
        {
            _recent.Enqueue(level);
            if (_recent.Count > WindowChunks)
            {
                _recent.Dequeue();
            }

            Floor = _recent.Min();
        }

        return voice;
    }
}
