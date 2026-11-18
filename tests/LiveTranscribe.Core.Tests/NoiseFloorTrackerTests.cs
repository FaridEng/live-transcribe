using LiveTranscribe.Core.Dictation;
using Xunit;

namespace LiveTranscribe.Core.Tests;

public class NoiseFloorTrackerTests
{
    [Fact]
    public void Quiet_microphone_speech_counts_as_voice()
    {
        // A quiet microphone: room at -70 dBFS, speech around -45 dBFS. The old fixed
        // threshold (-40 dBFS) called this speech silence and cut the session short.
        var tracker = Feed(0.0003f, 20);

        Assert.True(tracker.Add(0.0056f));
    }

    [Fact]
    public void Background_noise_is_not_voice()
    {
        var tracker = Feed(0.01f, 20);

        Assert.False(tracker.Add(0.012f));
        Assert.InRange(tracker.Floor!.Value, 0.0099f, 0.0101f);
    }

    [Fact]
    public void First_chunk_of_speech_is_voice_before_any_floor_is_known()
    {
        var tracker = new NoiseFloorTracker();

        Assert.True(tracker.Add(0.05f));
    }

    [Fact]
    public void Floor_falls_to_the_quietest_recent_chunk()
    {
        var tracker = Feed(0.05f, 5);
        tracker.Add(0.001f);

        Assert.Equal(0.001f, tracker.Floor);
    }

    [Fact]
    public void Floor_follows_a_room_that_gets_louder()
    {
        var tracker = Feed(0.001f, 10);
        tracker = Feed(0.02f, 60, tracker);

        Assert.Equal(0.02f, tracker.Floor);
    }

    [Fact]
    public void Noise_gate_zeros_do_not_become_the_floor()
    {
        var tracker = Feed(0.002f, 10);
        Feed(0f, 10, tracker);

        Assert.Equal(0.002f, tracker.Floor);
    }

    [Fact]
    public void Noisy_room_never_hides_normal_speech()
    {
        var tracker = Feed(0.05f, 60);

        Assert.Equal(NoiseFloorTracker.MaximumVoiceLevel, tracker.VoiceThreshold);
        Assert.True(tracker.Add(0.2f));
    }

    private static NoiseFloorTracker Feed(float level, int chunks, NoiseFloorTracker? tracker = null)
    {
        tracker ??= new NoiseFloorTracker();
        for (var i = 0; i < chunks; i++)
        {
            tracker.Add(level);
        }

        return tracker;
    }
}
