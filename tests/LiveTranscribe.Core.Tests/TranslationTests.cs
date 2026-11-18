using LiveTranscribe.Core.Abstractions;
using LiveTranscribe.Core.Engines;
using LiveTranscribe.Core.Settings;
using LiveTranscribe.Core.Translation;
using Xunit;

namespace LiveTranscribe.Core.Tests;

/// <summary>
/// The provider-neutral half of translation (architecture T14): what the two-step route sends where,
/// and the rules every translation model is given.
/// </summary>
public class TranslationTests
{
    [Fact]
    public async Task Two_step_translates_the_transcript_with_the_context()
    {
        var text = new FakeTextTranslator("Hello there");
        var context = new TranslationContext("email to a client", TranslationTone.Formal, ["Earlier part."]);
        var translator = new TwoStepTranslator(new FakeEngine("سلام"), Options, text);

        await using var session = await translator.StartAsync(context, CancellationToken.None);
        await session.WriteAudioAsync(new byte[3200], CancellationToken.None);
        var result = await session.CompleteAsync(CancellationToken.None);

        Assert.Equal("Hello there", result.Text);
        Assert.Equal("سلام", result.Transcript);
        Assert.Equal("سلام", text.Received);
        Assert.Same(context, text.Context);
    }

    [Fact]
    public async Task Two_step_skips_the_translation_when_nothing_was_said()
    {
        var text = new FakeTextTranslator("should not be used");
        var translator = new TwoStepTranslator(new FakeEngine(string.Empty), Options, text);

        await using var session = await translator.StartAsync(TranslationContext.None, CancellationToken.None);
        var result = await session.CompleteAsync(CancellationToken.None);

        Assert.Equal(string.Empty, result.Text);
        Assert.Null(text.Received);
    }

    [Fact]
    public void Prompt_forbids_answering_what_was_dictated()
    {
        var prompt = TranslationPrompt.ForText(TranslationContext.None);

        Assert.Contains("Never answer", prompt);
        Assert.Contains("Output only the English translation", prompt);
    }

    [Fact]
    public void Topic_note_decides_the_tone_only_when_none_was_picked()
    {
        var asSpoken = TranslationPrompt.ForAudio(new TranslationContext("formal reply", TranslationTone.AsSpoken, []));
        var casual = TranslationPrompt.ForAudio(new TranslationContext("formal reply", TranslationTone.Casual, []));

        Assert.Contains("use that tone instead of the spoken register", asSpoken);
        Assert.Contains("ignore any tone it asks for", casual);
        Assert.Contains("formal reply", casual);
    }

    [Fact]
    public void Recent_translations_are_listed_for_consistency()
    {
        var prompt = TranslationPrompt.ForText(new TranslationContext(null, TranslationTone.AsSpoken, ["First part.", "Second part."]));

        Assert.Contains("- First part.", prompt);
        Assert.Contains("- Second part.", prompt);
        Assert.DoesNotContain("What the user is writing about", prompt);
    }

    [Fact]
    public void Remembered_translations_keep_the_last_three_for_the_same_app()
    {
        var (service, _) = Service();
        var mail = new TargetSnapshot(1, 100, "outlook");
        foreach (var sentence in new[] { "One.", "Two.", "Three.", "Four." })
        {
            service.Remember(sentence, mail);
        }

        Assert.Equal(["Two.", "Three.", "Four."], service.ContextFor(mail).Recent);
    }

    [Fact]
    public void Remembered_translations_are_dropped_for_another_app_or_after_a_pause()
    {
        var (service, clock) = Service();
        var mail = new TargetSnapshot(1, 100, "outlook");
        service.Remember("Dear Anna,", mail);

        Assert.Empty(service.ContextFor(new TargetSnapshot(2, 200, "slack")).Recent);

        service.Remember("Dear Anna,", mail);
        clock.Now += TranslationService.RecentLifetime + TimeSpan.FromSeconds(1);
        Assert.Empty(service.ContextFor(mail).Recent);
    }

    [Fact]
    public void Start_fresh_forgets_the_remembered_translations_but_keeps_the_note()
    {
        var (service, _) = Service();
        var mail = new TargetSnapshot(1, 100, "outlook");
        service.SetTopicNote("reply to a client");
        service.Remember("Dear Anna,", mail);

        service.StartFresh();

        var context = service.ContextFor(mail);
        Assert.Empty(context.Recent);
        Assert.Equal("reply to a client", context.TopicNote);
    }

    [Fact]
    public void The_topic_note_clears_itself_after_an_hour_unused_and_use_keeps_it_alive()
    {
        var (service, clock) = Service();
        var mail = new TargetSnapshot(1, 100, "outlook");
        service.SetTopicNote("reply to a client");

        clock.Now += TimeSpan.FromMinutes(50);
        service.Remember("Dear Anna,", mail);
        clock.Now += TimeSpan.FromMinutes(50);
        Assert.Equal("reply to a client", service.TopicNote);

        clock.Now += TranslationService.TopicNoteLifetime;
        Assert.Null(service.TopicNote);
        Assert.Null(service.ContextFor(mail).TopicNote);
    }

    private static (TranslationService Service, FakeClock Clock) Service()
    {
        var clock = new FakeClock();
        return (new TranslationService(new NoTranslators(), new MemorySettings(), () => clock.Now), clock);
    }

    private sealed class FakeClock
    {
        public DateTimeOffset Now { get; set; } = new(2026, 9, 21, 9, 0, 0, TimeSpan.Zero);
    }

    private sealed class MemorySettings : ISettingsStore
    {
        public AppSettings Current { get; } = AppSettings.CreateDefault();

        public event Action<AppSettings>? Changed { add { } remove { } }

        public void Save(AppSettings settings)
        {
        }
    }

    private sealed class NoTranslators : ISpeechTranslatorFactory
    {
        public Task<IReadOnlyList<TranslationModel>> ListModelsAsync(string apiKey, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<ISpeechTranslator> CreateAsync(string modelId, EngineProfile transcriber, EngineSessionOptions options,
            string apiKey, CancellationToken ct) => throw new NotSupportedException();
    }

    private static readonly EngineSessionOptions Options = new(LanguageMode.Auto, CleanupLevel.Standard);

    private sealed class FakeTextTranslator(string answer) : ITextTranslator
    {
        public string? Received { get; private set; }
        public TranslationContext? Context { get; private set; }

        public Task<string> TranslateAsync(string text, TranslationContext context, CancellationToken ct)
        {
            Received = text;
            Context = context;
            return Task.FromResult(answer);
        }
    }

    private sealed class FakeEngine(string transcript) : ISpeechEngine, ISpeechSession
    {
        public EngineCapabilities Capabilities { get; } = new(true, false, true, true, true, TimeSpan.FromMinutes(10));

        public Task<ISpeechSession> StartSessionAsync(EngineSessionOptions options, CancellationToken ct) =>
            Task.FromResult<ISpeechSession>(this);

        public Task<EngineTestReport> TestAsync(CancellationToken ct) => throw new NotSupportedException();

        public event Action<string>? InterimText { add { } remove { } }
        public event Action<string>? FinalSegment { add { } remove { } }

        public ValueTask WriteAudioAsync(ReadOnlyMemory<byte> pcm, CancellationToken ct) => ValueTask.CompletedTask;

        public Task<TranscriptResult> CompleteAsync(CancellationToken ct) =>
            Task.FromResult(new TranscriptResult(transcript, TimeSpan.FromMilliseconds(400)));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
