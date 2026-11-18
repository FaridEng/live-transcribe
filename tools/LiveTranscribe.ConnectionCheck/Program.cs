using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LiveTranscribe.Core;
using LiveTranscribe.Core.Diagnostics;
using LiveTranscribe.Core.Settings;
using LiveTranscribe.Platform.Windows.Security;

// Checks the stored Gemini API key and the Live transcription protocol without ever printing the key.
// Usage: LiveTranscribe.ConnectionCheck <path-to-16kHz-mono-16bit.wav> [--engine] [--fast]
//        LiveTranscribe.ConnectionCheck --models              what the key can reach (M6 profiles)
//        LiveTranscribe.ConnectionCheck --generate <model> <prompt>   one text request to that model
//        LiveTranscribe.ConnectionCheck --corpus <folder> [--out <file>]  every recording through every profile (M8)
//        LiveTranscribe.ConnectionCheck <wav> --translate <model> [--topic "<note>"] [--tone casual|formal]  one translation (T14)

var listModels = args.Contains("--models");
var generate = args.Contains("--generate");
var corpus = args.Contains("--corpus");
var wavPath = listModels || generate || corpus
    ? string.Empty
    : args.Length > 0 ? args[0] : throw new ArgumentException("Pass a 16 kHz mono 16-bit WAV file.");

var log = new NullLogger();
var settings = new JsonSettingsStore(AppPaths.SettingsFile, log).Current;
var profile = settings.ActiveProfile ?? throw new InvalidOperationException("No engine profile in settings.");
var key = new DpapiSecretStore(AppPaths.SecretsFile, log).Get(profile.SecretRef);

if (string.IsNullOrWhiteSpace(key))
{
    Console.WriteLine("FAIL: no API key stored for the active profile.");
    return 1;
}

Console.WriteLine($"Key: found in encrypted store (profile '{profile.Name}', model '{profile.ModelId}').");

// --models: which models this key can reach, and which of them take generateContent or the Live API.
if (listModels)
{
    using var http = new HttpClient();
    http.DefaultRequestHeaders.Add("x-goog-api-key", key);
    using var response = await http.GetAsync("https://generativelanguage.googleapis.com/v1beta/models?pageSize=200");
    var body = await response.Content.ReadAsStringAsync();
    Console.WriteLine($"GET models -> HTTP {(int)response.StatusCode} {response.StatusCode}");
    using var document = JsonDocument.Parse(body);
    if (!document.RootElement.TryGetProperty("models", out var models))
    {
        Console.WriteLine(Shorten(Redact(body, key), 900));
        return 1;
    }

    foreach (var model in models.EnumerateArray())
    {
        var methods = model.TryGetProperty("supportedGenerationMethods", out var m)
            ? string.Join(", ", m.EnumerateArray().Select(x => x.GetString()))
            : "-";
        Console.WriteLine($"{model.GetProperty("name").GetString(),-52} {methods}");
    }

    return 0;
}

// --describe: what the provider says about one model (limits, description), for picking profiles.
if (args.Contains("--describe"))
{
    var model = args[Array.IndexOf(args, "--describe") + 1];
    using var http = new HttpClient();
    http.DefaultRequestHeaders.Add("x-goog-api-key", key);
    var body = await http.GetStringAsync($"https://generativelanguage.googleapis.com/v1beta/models/{model}");
    Console.OutputEncoding = Encoding.UTF8;
    Console.WriteLine(Shorten(Redact(body, key), 1500));
    return 0;
}

// --generate: one text request, to see what a non-transcription model does with the key (T1, T12).
if (generate)
{
    Console.OutputEncoding = Encoding.UTF8;
    var index = Array.IndexOf(args, "--generate");
    var model = args[index + 1];
    var prompt = args[index + 2];
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
    http.DefaultRequestHeaders.Add("x-goog-api-key", key);
    var payload = JsonSerializer.Serialize(new
    {
        contents = new[] { new { parts = new[] { new { text = prompt } } } }
    });
    var clock = System.Diagnostics.Stopwatch.StartNew();
    using var response = await http.PostAsync(
        $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent",
        new StringContent(payload, Encoding.UTF8, "application/json"));
    var body = await response.Content.ReadAsStringAsync();
    Console.WriteLine($"POST {model}:generateContent -> HTTP {(int)response.StatusCode} in {clock.ElapsedMilliseconds} ms");
    Console.WriteLine(Shorten(Redact(body, key), 2000));
    return response.IsSuccessStatusCode ? 0 : 1;
}

// --corpus: the benchmark half of FR-13.10. Sends every recording in a folder through each engine
// profile and writes one table, so switching engines is decided on evidence (M8, evaluation-corpus.md).
// The transcripts are the point of the run, so they go into the report file, never into the app log.
if (corpus)
{
    Console.OutputEncoding = Encoding.UTF8;
    var folder = args[Array.IndexOf(args, "--corpus") + 1];
    var outIndex = Array.IndexOf(args, "--out");
    var reportPath = outIndex >= 0 && outIndex + 1 < args.Length
        ? args[outIndex + 1]
        : Path.Combine(folder, $"corpus-{DateTime.Now:yyyyMMdd-HHmm}.md");

    var recordings = Directory.GetFiles(folder, "*.wav").OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
    if (recordings.Count == 0)
    {
        Console.WriteLine($"No .wav files in {folder}.");
        return 1;
    }

    // Every profile with a stored key, so the same recordings are compared across engines.
    var profiles = settings.EngineProfiles
        .Where(p => !string.IsNullOrWhiteSpace(new DpapiSecretStore(AppPaths.SecretsFile, log).Get(p.SecretRef)))
        .ToList();

    Console.WriteLine($"{recordings.Count} recordings against {profiles.Count} profile(s).");

    var factory = new LiveTranscribe.Engines.Gemini.GeminiEngineFactory();
    var secrets = new DpapiSecretStore(AppPaths.SecretsFile, log);
    var report = new StringBuilder();
    report.AppendLine($"# Corpus run {DateTime.Now:yyyy-MM-dd HH:mm}");
    report.AppendLine();
    report.AppendLine($"Language: {settings.Dictation.Language}. Folder: `{folder}`.");

    foreach (var runProfile in profiles)
    {
        var profileKey = secrets.Get(runProfile.SecretRef)!;
        var engine = factory.Create(runProfile, profileKey);
        var timings = new List<double>();

        report.AppendLine();
        report.AppendLine($"## {runProfile.Name} ({runProfile.EngineType}, `{runProfile.ModelId}`)");
        report.AppendLine();
        report.AppendLine("| Item | Seconds | Latency ms | Transcript |");
        report.AppendLine("|---|---|---|---|");
        Console.WriteLine($"\n{runProfile.Name}:");

        foreach (var path in recordings)
        {
            var id = Path.GetFileNameWithoutExtension(path);
            var clip = ReadPcm(path);
            var seconds = clip.Length / 32000.0;

            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(180));
                var options = new LiveTranscribe.Core.Engines.EngineSessionOptions(
                    settings.Dictation.Language, runProfile.Cleanup);
                await using var speech = await engine.StartSessionAsync(options, timeout.Token);

                for (var offset = 0; offset < clip.Length; offset += 3200)
                {
                    await speech.WriteAudioAsync(clip.AsMemory(offset, Math.Min(3200, clip.Length - offset)), timeout.Token);
                }

                var outcome = await speech.CompleteAsync(timeout.Token);
                var ms = outcome.ElapsedAfterEndOfSpeech.TotalMilliseconds;
                timings.Add(ms);
                report.AppendLine($"| {id} | {seconds:F1} | {ms:F0} | {outcome.Text.Replace("|", "\\|")} |");
                Console.WriteLine($"  {id}: {ms:F0} ms, {outcome.Text.Length} chars");
            }
            catch (Exception ex)
            {
                report.AppendLine($"| {id} | {seconds:F1} | - | **FAILED:** {Redact(ex.Message, profileKey)} |");
                Console.WriteLine($"  {id}: failed - {Redact(ex.Message, profileKey)}");
            }
        }

        if (timings.Count > 0)
        {
            var sorted = timings.OrderBy(t => t).ToList();
            report.AppendLine();
            report.AppendLine($"{timings.Count} of {recordings.Count} transcribed. " +
                              $"Latency after end of speech: median {sorted[sorted.Count / 2]:F0} ms, " +
                              $"mean {timings.Average():F0} ms, worst {sorted[^1]:F0} ms.");
        }
    }

    await File.WriteAllTextAsync(reportPath, report.ToString(), Encoding.UTF8);
    Console.WriteLine($"\nReport: {reportPath}");
    return 0;
}

// --file: exercise GeminiFileEngine, the record-then-send path (FR-13.11, M6). An optional model id
// follows, because the active profile is usually a Live model that generateContent will not accept.
if (args.Contains("--file"))
{
    var index = Array.IndexOf(args, "--file");
    var model = index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal)
        ? args[index + 1]
        : "gemini-3.5-transcribe";

    var fileProfile = new EngineProfile
    {
        Name = "file test",
        EngineType = EngineType.GeminiFile,
        ModelId = model,
        Cleanup = profile.Cleanup
    };

    var fileEngine = new LiveTranscribe.Engines.Gemini.GeminiEngineFactory().Create(fileProfile, key);
    var probe = await fileEngine.TestAsync(CancellationToken.None);
    Console.WriteLine($"Test connection: {probe.Summary} ({probe.RoundTrip.TotalMilliseconds:F0} ms)");

    var clip = ReadPcm(wavPath);
    await using var fileSpeech = await fileEngine.StartSessionAsync(
        new LiveTranscribe.Core.Engines.EngineSessionOptions(settings.Dictation.Language, fileProfile.Cleanup),
        CancellationToken.None);

    for (var offset = 0; offset < clip.Length; offset += 3200)
    {
        await fileSpeech.WriteAudioAsync(clip.AsMemory(offset, Math.Min(3200, clip.Length - offset)), CancellationToken.None);
    }

    var fileResult = await fileSpeech.CompleteAsync(new CancellationTokenSource(TimeSpan.FromSeconds(120)).Token);
    Console.WriteLine($"Sent {clip.Length / 32000.0:F1} s of audio to {model}.");
    Console.WriteLine($"Final ({fileResult.ElapsedAfterEndOfSpeech.TotalMilliseconds:F0} ms after end of speech): {fileResult.Text}");
    return 0;
}

// --translate: the app's own translator for one model, fed in real time as a dictation would be (T14).
// Optional --topic "<note>" and --tone casual|formal. The English goes to the console, never to the log.
if (args.Contains("--translate"))
{
    Console.OutputEncoding = Encoding.UTF8;
    var model = args[Array.IndexOf(args, "--translate") + 1];
    var topic = args.Contains("--topic") ? args[Array.IndexOf(args, "--topic") + 1] : null;
    var tone = args.Contains("--tone")
        ? Enum.Parse<LiveTranscribe.Core.Translation.TranslationTone>(args[Array.IndexOf(args, "--tone") + 1], ignoreCase: true)
        : LiveTranscribe.Core.Translation.TranslationTone.AsSpoken;

    var engines = new LiveTranscribe.Engines.Gemini.GeminiEngineFactory();
    var translator = await new LiveTranscribe.Engines.Gemini.GeminiTranslatorFactory(engines).CreateAsync(
        model, profile, new LiveTranscribe.Core.Engines.EngineSessionOptions(settings.Dictation.Language, profile.Cleanup),
        key, CancellationToken.None);
    Console.WriteLine($"{model}: {translator.Route}{(translator.UsesContext ? "" : ", ignores topic and tone")}");

    var clip = ReadPcm(wavPath);
    await using var session = await translator.StartAsync(
        new LiveTranscribe.Core.Translation.TranslationContext(topic, tone, []), CancellationToken.None);
    for (var offset = 0; offset < clip.Length; offset += 3200)
    {
        await session.WriteAudioAsync(clip.AsMemory(offset, Math.Min(3200, clip.Length - offset)), CancellationToken.None);
        await Task.Delay(100);
    }

    var translated = await session.CompleteAsync(new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token);
    Console.WriteLine($"Heard: {translated.Transcript}");
    Console.WriteLine($"English ({translated.AfterEndOfSpeech.TotalMilliseconds:F0} ms after end of speech): {translated.Text}");
    return 0;
}

// --engine: exercise the app's own GeminiLiveEngine class instead of the raw protocol.
if (args.Contains("--engine"))
{
    var engine = new LiveTranscribe.Engines.Gemini.GeminiEngineFactory().Create(profile, key);
    var options = new LiveTranscribe.Core.Engines.EngineSessionOptions(settings.Dictation.Language, profile.Cleanup);
    var clock = System.Diagnostics.Stopwatch.StartNew();

    await using var speech = await engine.StartSessionAsync(options, CancellationToken.None);
    Console.WriteLine($"Session ready in {clock.ElapsedMilliseconds} ms.");
    var interimCount = 0;
    var sending = new System.Diagnostics.Stopwatch();

    // When each preview arrives, measured from the first audio sent (T10).
    speech.InterimText += text =>
    {
        Interlocked.Increment(ref interimCount);
        Console.WriteLine($"  interim at {sending.Elapsed.TotalSeconds:F1} s: {text.Length} chars");
    };

    // --fast: send the whole clip as quickly as the socket takes it, as a retry of kept audio does (T3).
    var fast = args.Contains("--fast");
    var audio = ReadPcm(wavPath);
    sending.Start();
    for (var offset = 0; offset < audio.Length; offset += 3200)
    {
        await speech.WriteAudioAsync(audio.AsMemory(offset, Math.Min(3200, audio.Length - offset)), CancellationToken.None);
        if (!fast)
        {
            await Task.Delay(100);
        }
    }

    Console.WriteLine($"Sent {audio.Length / 32000.0:F1} s of audio in {sending.ElapsedMilliseconds} ms ({(fast ? "fast" : "real time")}).");
    var result = await speech.CompleteAsync(new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token);
    Console.WriteLine($"Interim updates: {interimCount}");
    Console.WriteLine($"Final ({result.ElapsedAfterEndOfSpeech.TotalMilliseconds:F0} ms after end of speech): {result.Text}");
    return 0;
}

// 1. REST: is the key valid, and can it see the model?
using (var http = new HttpClient())
{
    http.DefaultRequestHeaders.Add("x-goog-api-key", key);
    var modelUrl = $"https://generativelanguage.googleapis.com/v1beta/models/{profile.ModelId}";
    using var response = await http.GetAsync(modelUrl);
    var body = await response.Content.ReadAsStringAsync();
    Console.WriteLine($"\n[1] GET model -> HTTP {(int)response.StatusCode} {response.StatusCode}");
    Console.WriteLine(Shorten(Redact(body, key), 900));
}

// 2. Live API: setup, stream the WAV, collect transcription events.
Console.WriteLine("\n[2] Live API session");
var pcm = ReadPcm(wavPath);
Console.WriteLine($"Audio: {pcm.Length / 32000.0:F1} s of PCM.");

using var ws = new ClientWebSocket();
ws.Options.SetRequestHeader("x-goog-api-key", key);
var uri = new Uri("wss://generativelanguage.googleapis.com/ws/google.ai.generativelanguage.v1beta.GenerativeService.BidiGenerateContent");

var started = DateTime.UtcNow;
try
{
    await ws.ConnectAsync(uri, CancellationToken.None);
}
catch (Exception ex)
{
    Console.WriteLine($"FAIL: connect: {Redact(ex.Message, key)}");
    return 1;
}

Console.WriteLine($"Connected in {(DateTime.UtcNow - started).TotalMilliseconds:F0} ms.");

var setup = new
{
    setup = new
    {
        model = $"models/{profile.ModelId}",
        generationConfig = new { responseModalities = new[] { "TEXT" } },
        inputAudioTranscription = new { languageCodes = Array.Empty<string>(), mode = "SMART" },
        realtimeInputConfig = new { automaticActivityDetection = new { disabled = true } }
    }
};
await Send(ws, setup);

using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));
var receiver = Task.Run(() => ReceiveLoop(ws, key, cts.Token));

// Give setup a moment, then stream in 100 ms chunks at real-time pace.
await Task.Delay(500);
await Send(ws, new { realtimeInput = new { activityStart = new { } } });

const int chunkBytes = 3200; // 100 ms at 16 kHz, 16-bit mono
var streamStart = DateTime.UtcNow;
for (var offset = 0; offset < pcm.Length && ws.State == WebSocketState.Open; offset += chunkBytes)
{
    var count = Math.Min(chunkBytes, pcm.Length - offset);
    var data = Convert.ToBase64String(pcm, offset, count);
    await Send(ws, new { realtimeInput = new { audio = new { data, mimeType = "audio/pcm;rate=16000" } } });
    await Task.Delay(100);
}

if (ws.State == WebSocketState.Open)
{
    await Send(ws, new { realtimeInput = new { activityEnd = new { } } });
    Console.WriteLine($"Audio sent and activityEnd at +{(DateTime.UtcNow - streamStart).TotalMilliseconds:F0} ms.");
}

await receiver;

if (ws.State == WebSocketState.Open)
{
    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
}

return 0;

static async Task ReceiveLoop(ClientWebSocket ws, string key, CancellationToken ct)
{
    var buffer = new byte[64 * 1024];
    var finalSeenAt = (DateTime?)null;

    try
    {
        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            using var message = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await ws.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    Console.WriteLine($"Server closed: {result.CloseStatus} {Redact(result.CloseStatusDescription ?? "", key)}");
                    return;
                }

                message.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            var json = Encoding.UTF8.GetString(message.ToArray());
            var stamp = DateTime.Now.ToString("HH:mm:ss.fff");
            Console.WriteLine($"{stamp} <- {Shorten(Redact(json, key), 400)}");

            if (json.Contains("\"inputTranscription\""))
            {
                finalSeenAt ??= DateTime.UtcNow;
            }

            if (json.Contains("\"turnComplete\"") || (finalSeenAt is not null && DateTime.UtcNow - finalSeenAt > TimeSpan.FromSeconds(3)))
            {
                return;
            }
        }
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine("Timed out waiting for server messages.");
    }
    catch (WebSocketException ex)
    {
        Console.WriteLine($"Socket error: {ex.Message} (close: {ws.CloseStatus} {ws.CloseStatusDescription})");
    }
}

static async Task Send(ClientWebSocket ws, object payload)
{
    var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
    await ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
}

static byte[] ReadPcm(string path)
{
    var bytes = File.ReadAllBytes(path);
    // Find the "data" chunk instead of assuming a 44-byte header.
    for (var i = 12; i < bytes.Length - 8; i++)
    {
        if (bytes[i] == 'd' && bytes[i + 1] == 'a' && bytes[i + 2] == 't' && bytes[i + 3] == 'a')
        {
            var size = BitConverter.ToInt32(bytes, i + 4);
            return bytes.AsSpan(i + 8, Math.Min(size, bytes.Length - i - 8)).ToArray();
        }
    }

    throw new InvalidDataException("No data chunk in WAV file.");
}

static string Redact(string text, string key) =>
    Regex.Replace(text.Replace(key, "<key>"), @"AIza[0-9A-Za-z\-_]{10,}", "<key>");

static string Shorten(string text, int max) =>
    text.Length <= max ? text : text[..max] + $"... ({text.Length} chars)";

internal sealed class NullLogger : IAppLogger
{
    public void Info(string message) { }
    public void Warn(string message) { }
    public void Error(string message, Exception? ex = null) { }
}
