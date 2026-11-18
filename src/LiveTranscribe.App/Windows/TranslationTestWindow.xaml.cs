using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LiveTranscribe.Core.Abstractions;
using LiveTranscribe.Core.Diagnostics;
using LiveTranscribe.Core.Dictation;
using LiveTranscribe.Core.Engines;
using LiveTranscribe.Core.Settings;
using LiveTranscribe.Core.Translation;
using LiveTranscribe.Platform.Windows.Audio;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;
using TextBox = System.Windows.Controls.TextBox;

namespace LiveTranscribe.App.Windows;

/// <summary>
/// Records one utterance and translates it with several models at once, side by side, so the user can
/// pick the translation model by ear and by clock (architecture T14). Every ticked model hears the
/// audio while the user speaks, as it would in a real dictation, so the times compare fairly. The
/// recording and the results stay in memory and on screen; the log gets model names, lengths and times.
/// </summary>
public partial class TranslationTestWindow : Window
{
    private static readonly TimeSpan MaxRecording = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan CompleteTimeout = TimeSpan.FromSeconds(30);
    private const int ReplayChunkBytes = 3200;   // 100 ms of 16 kHz 16-bit mono

    private static readonly (string Name, TranslationTone Value)[] Tones =
    [
        ("As spoken", TranslationTone.AsSpoken),
        ("Casual", TranslationTone.Casual),
        ("Formal", TranslationTone.Formal)
    ];

    private readonly ISettingsStore _settings;
    private readonly ISpeechTranslatorFactory _translators;
    private readonly string _apiKey;
    private readonly EngineProfile _transcriber;
    private readonly EngineSessionOptions _options;
    private readonly string? _microphoneId;
    private readonly IAppLogger _log;
    private readonly CancellationTokenSource _closing = new();
    private readonly List<string> _models = new();
    private readonly Dictionary<string, CheckBox> _checks = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TranslationRoute> _routes = new(StringComparer.OrdinalIgnoreCase);

    private WasapiAudioCapture? _capture;
    private List<Lane>? _live;
    private List<byte[]> _recording = new();
    private CancellationTokenSource? _autoStop;

    public TranslationTestWindow(ISettingsStore settings, ISpeechTranslatorFactory translators, string apiKey,
        EngineProfile transcriber, EngineSessionOptions options, string? microphoneId, string selectedModel,
        IAppLogger log)
    {
        _settings = settings;
        _translators = translators;
        _apiKey = apiKey;
        _transcriber = transcriber;
        _options = options;
        _microphoneId = microphoneId;
        _log = log;
        InitializeComponent();
        MaxHeight = SystemParameters.WorkArea.Height;

        ToneBox.ItemsSource = Tones.Select(t => new { t.Name, t.Value }).ToList();
        ToneBox.SelectedIndex = 0;

        AddModel(selectedModel, ticked: true);
        foreach (var id in settings.Current.Translation.CompareModelIds)
        {
            AddModel(id, ticked: true);
        }

        Closed += OnClosed;
    }

    /// <summary>"Use this model" on a result: the settings window takes it as the translation model.</summary>
    public event Action<string>? ModelChosen;

    private TranslationContext Context => new(
        TopicBox.Text,
        ToneBox.SelectedIndex >= 0 ? Tones[ToneBox.SelectedIndex].Value : TranslationTone.AsSpoken,
        []);

    private void AddModel(string id, bool ticked)
    {
        id = id.Trim();
        if (id.Length == 0)
        {
            return;
        }

        if (_checks.TryGetValue(id, out var existing))
        {
            existing.IsChecked = true;
            return;
        }

        var check = new CheckBox { IsChecked = ticked, Margin = new Thickness(0, 4, 18, 4), Tag = id };
        _models.Add(id);
        _checks[id] = check;
        ModelList.Children.Add(check);
        LabelModel(id);
    }

    private void LabelModel(string id)
    {
        if (!_checks.TryGetValue(id, out var check))
        {
            return;
        }

        check.Content = _routes.TryGetValue(id, out var route) ? $"{id}  ({RouteName(id, route)})" : id;
    }

    private static string RouteName(string id, TranslationRoute route) => route switch
    {
        TranslationRoute.TwoStep => "two-step",
        _ when id.Contains("translate", StringComparison.OrdinalIgnoreCase) => "one-step interpreter",
        _ => "one-step"
    };

    private void OnAddModel(object sender, RoutedEventArgs e)
    {
        AddModel(AddModelBox.Text, ticked: true);
        AddModelBox.Text = string.Empty;
    }

    private async void OnLoadList(object sender, RoutedEventArgs e)
    {
        LoadListButton.IsEnabled = false;
        ModelNote.Text = "Asking the provider which models this key can reach...";
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var models = await _translators.ListModelsAsync(_apiKey, timeout.Token);
            foreach (var model in models)
            {
                _routes[model.Id] = model.Route;
                LabelModel(model.Id);
            }

            AddModelBox.ItemsSource = models.Select(m => m.Id).ToList();
            ModelNote.Text = $"{models.Count(m => m.Route == TranslationRoute.OneStep)} one-step and " +
                             $"{models.Count(m => m.Route == TranslationRoute.TwoStep)} two-step models available. Pick one above and press Add.";
        }
        catch (Exception ex)
        {
            ModelNote.Text = Describe(ex);
        }
        finally
        {
            LoadListButton.IsEnabled = true;
        }
    }

    private async void OnRecord(object sender, RoutedEventArgs e)
    {
        if (_capture is not null)
        {
            await StopRecordingAsync();
            return;
        }

        var models = TickedModels();
        if (models.Count == 0)
        {
            StatusText.Text = "Tick at least one model first.";
            return;
        }

        var recording = new List<byte[]>();
        var lanes = StartLanes(models);
        var capture = new WasapiAudioCapture(_log) { PreferredDeviceId = _microphoneId };

        try
        {
            capture.Start(
                chunk =>
                {
                    lock (recording)
                    {
                        recording.Add(chunk);
                    }

                    foreach (var lane in lanes)
                    {
                        lane.Audio.Writer.TryWrite(chunk);
                    }

                    var level = AudioLevel.Rms(chunk);
                    Dispatcher.BeginInvoke(() => LevelBar.Value = Math.Clamp(Math.Sqrt(level / 0.25), 0, 1));
                },
                ex => Dispatcher.BeginInvoke(async () =>
                {
                    StatusText.Text = "The microphone stopped during the recording.";
                    await StopRecordingAsync();
                }));
        }
        catch (Exception ex)
        {
            capture.Dispose();
            StatusText.Text = ex is MicrophoneException mic && mic.Reason == MicrophoneFailure.AccessDenied
                ? "Windows is blocking microphone access for desktop apps."
                : "The microphone could not be opened.";
            await FinishLanesAsync(lanes);
            return;
        }

        _capture = capture;
        _live = lanes;
        _recording = recording;
        SetBusy(true);
        RecordButton.Content = "Stop";
        RecordButton.IsEnabled = true;
        StatusText.Text = "Recording - speak now, then press Stop.";

        _autoStop = new CancellationTokenSource();
        var autoStop = _autoStop.Token;
        _ = Task.Delay(MaxRecording, autoStop).ContinueWith(
            _ => Dispatcher.BeginInvoke(async () => await StopRecordingAsync()),
            autoStop, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
    }

    private async Task StopRecordingAsync()
    {
        var capture = _capture;
        var lanes = _live;
        if (capture is null || lanes is null)
        {
            return;
        }

        _capture = null;
        _live = null;
        _autoStop?.Cancel();
        RecordButton.IsEnabled = false;
        LevelBar.Value = 0;

        await capture.StopAsync();
        capture.Dispose();

        double seconds;
        lock (_recording)
        {
            seconds = _recording.Sum(c => c.Length) / 32000.0;
        }

        StatusText.Text = $"Recorded {seconds:F1} s. Waiting for the translations...";
        await FinishLanesAsync(lanes);
        StatusText.Text = $"Recorded {seconds:F1} s. Run again sends the same recording to the ticked models.";
        RecordButton.Content = "Record";
        SetBusy(false);
    }

    /// <summary>Replays the kept recording at speaking pace, so the times match a live dictation.</summary>
    private async void OnRunAgain(object sender, RoutedEventArgs e)
    {
        var models = TickedModels();
        if (models.Count == 0 || _recording.Count == 0)
        {
            StatusText.Text = models.Count == 0 ? "Tick at least one model first." : "Record something first.";
            return;
        }

        SetBusy(true);
        RecordButton.IsEnabled = false;
        StatusText.Text = "Sending the recording again at speaking pace...";

        byte[] audio;
        lock (_recording)
        {
            audio = _recording.SelectMany(c => c).ToArray();
        }

        var lanes = StartLanes(models);
        try
        {
            for (var offset = 0; offset < audio.Length; offset += ReplayChunkBytes)
            {
                var chunk = audio.AsSpan(offset, Math.Min(ReplayChunkBytes, audio.Length - offset)).ToArray();
                foreach (var lane in lanes)
                {
                    lane.Audio.Writer.TryWrite(chunk);
                }

                await Task.Delay(100, _closing.Token);
            }
        }
        catch (OperationCanceledException)
        {
        }

        StatusText.Text = "Waiting for the translations...";
        await FinishLanesAsync(lanes);
        StatusText.Text = "Done. Change the note, tone or models and run again to compare.";
        RecordButton.IsEnabled = true;
        SetBusy(false);
    }

    private void SetBusy(bool busy)
    {
        RunAgainButton.IsEnabled = !busy && _recording.Count > 0;
        LoadListButton.IsEnabled = !busy;
        foreach (var check in _checks.Values)
        {
            check.IsEnabled = !busy;
        }
    }

    private List<string> TickedModels() =>
        _models.Where(id => _checks[id].IsChecked == true).ToList();

    /// <summary>
    /// Opens one session per model and lets each drain its own audio queue. Audio arriving before a
    /// session is ready waits in the queue, as it does in a dictation that is still connecting.
    /// </summary>
    private List<Lane> StartLanes(IReadOnlyList<string> models)
    {
        Results.Children.Clear();
        var context = Context;
        return models.Select(model =>
        {
            var lane = new Lane(model, AddResultRow(model));
            lane.Session = OpenAsync(lane, context);
            lane.Pump = PumpAsync(lane);
            return lane;
        }).ToList();
    }

    private async Task<ITranslationSession?> OpenAsync(Lane lane, TranslationContext context)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
            timeout.CancelAfter(StartTimeout);
            var translator = await _translators.CreateAsync(lane.Model, _transcriber, _options, _apiKey, timeout.Token);
            lane.Route = translator.Route;
            lane.UsesContext = translator.UsesContext;
            _routes[lane.Model] = translator.Route;
            await Dispatcher.InvokeAsync(() =>
            {
                LabelModel(lane.Model);
                lane.Row.Title.Text = $"{lane.Model}  ({RouteName(lane.Model, translator.Route)}" +
                                      $"{(translator.UsesContext ? "" : ", ignores the note and tone")})";
                lane.Row.Status.Text = "listening...";
            });
            return await translator.StartAsync(context, timeout.Token);
        }
        catch (Exception ex)
        {
            lane.Failure = ex;
            return null;
        }
    }

    private static async Task PumpAsync(Lane lane)
    {
        var session = await lane.Session!;
        await foreach (var chunk in lane.Audio.Reader.ReadAllAsync())
        {
            if (session is null || lane.Failure is not null)
            {
                continue;   // Keep draining, so the writer never blocks on a failed lane.
            }

            try
            {
                await session.WriteAudioAsync(chunk, CancellationToken.None);
            }
            catch (Exception ex)
            {
                lane.Failure = ex;
            }
        }
    }

    private async Task FinishLanesAsync(IReadOnlyList<Lane> lanes)
    {
        foreach (var lane in lanes)
        {
            lane.Audio.Writer.TryComplete();
        }

        await Task.WhenAll(lanes.Select(FinishLaneAsync));
    }

    private async Task FinishLaneAsync(Lane lane)
    {
        await lane.Pump!;
        var session = await lane.Session!;
        try
        {
            if (session is null || lane.Failure is not null)
            {
                throw lane.Failure ?? new InvalidOperationException("The session did not start.");
            }

            lane.Row.Status.Text = "translating...";
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
            timeout.CancelAfter(CompleteTimeout);
            var result = await session.CompleteAsync(timeout.Token);

            // Lengths and times only: what was said never goes to the log (FR-11.5).
            _log.Info($"Translation test: '{lane.Model}' ({lane.Route}) gave {result.Text.Length} chars " +
                      $"{result.AfterEndOfSpeech.TotalMilliseconds:F0} ms after end of speech.");
            ShowResult(lane, result);
        }
        catch (Exception ex)
        {
            _log.Warn($"Translation test: '{lane.Model}' failed ({(ex as EngineException)?.Kind.ToString() ?? ex.GetType().Name}).");
            lane.Row.Status.Text = "failed";
            lane.Row.Status.Foreground = Brushes.Firebrick;
            lane.Row.English.Text = Describe(ex);
        }
        finally
        {
            if (session is not null)
            {
                await session.DisposeAsync();
            }
        }
    }

    private void ShowResult(Lane lane, TranslationResult result)
    {
        lane.Row.Status.Text = $"{result.AfterEndOfSpeech.TotalSeconds:F1} s";
        lane.Row.English.Text = result.Text.Length > 0 ? result.Text : "(nothing came back)";
        lane.Row.Use.Visibility = Visibility.Visible;

        if (!string.IsNullOrWhiteSpace(result.Transcript))
        {
            lane.Row.Heard.Text = result.Transcript;
            lane.Row.Heard.FlowDirection = result.Transcript.Any(IsArabicScript)
                ? System.Windows.FlowDirection.RightToLeft
                : System.Windows.FlowDirection.LeftToRight;
            lane.Row.HeardPanel.Visibility = Visibility.Visible;
        }
    }

    private static bool IsArabicScript(char c) => c is >= '؀' and <= 'ۿ';

    private ResultRow AddResultRow(string model)
    {
        var title = new TextBlock { Text = model, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        var status = new TextBlock { Text = "connecting...", Foreground = Brushes.DimGray, VerticalAlignment = VerticalAlignment.Center };
        var use = new Button
        {
            Content = "Use this model",
            Height = 24,
            Padding = new Thickness(8, 0, 8, 0),
            Margin = new Thickness(12, 0, 0, 0),
            Visibility = Visibility.Collapsed
        };

        var header = new DockPanel();
        DockPanel.SetDock(use, Dock.Right);
        DockPanel.SetDock(status, Dock.Right);
        header.Children.Add(use);
        header.Children.Add(status);
        header.Children.Add(title);

        var heard = new TextBlock { TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Vazirmatn, Segoe UI"), Foreground = Brushes.DimGray };
        var heardPanel = new DockPanel { Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed };
        var heardLabel = new TextBlock { Text = "Heard:", Width = 60, Foreground = Brushes.DimGray };
        DockPanel.SetDock(heardLabel, Dock.Left);
        heardPanel.Children.Add(heardLabel);
        heardPanel.Children.Add(heard);

        var english = new TextBox
        {
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            FontSize = 13
        };
        var englishPanel = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
        var englishLabel = new TextBlock { Text = "English:", Width = 60, Foreground = Brushes.DimGray };
        DockPanel.SetDock(englishLabel, Dock.Left);
        englishPanel.Children.Add(englishLabel);
        englishPanel.Children.Add(english);

        var body = new StackPanel();
        body.Children.Add(header);
        body.Children.Add(heardPanel);
        body.Children.Add(englishPanel);

        Results.Children.Add(new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 0, 0, 8),
            Child = body
        });

        use.Click += (_, _) =>
        {
            ModelChosen?.Invoke(model);
            StatusText.Text = $"\"{model}\" is now the translation model in Settings. Press Save there to keep it.";
        };

        return new ResultRow(title, status, heard, heardPanel, english, use);
    }

    private static string Describe(Exception ex) => ex switch
    {
        EngineException engine => engine.Message,
        OperationCanceledException => "No answer in time.",
        _ => ex.Message
    };

    /// <summary>Keeps the ticked models for next time, and drops the recording with the window.</summary>
    private async void OnClosed(object? sender, EventArgs e)
    {
        var current = _settings.Current;
        current.Translation.CompareModelIds = TickedModels();
        _settings.Save(current);

        _closing.Cancel();
        _autoStop?.Cancel();
        if (_capture is { } capture)
        {
            _capture = null;
            await capture.StopAsync();
            capture.Dispose();
        }

        if (_live is { } lanes)
        {
            _live = null;
            await FinishLanesAsync(lanes);
        }

        _recording = new List<byte[]>();
    }

    private sealed record ResultRow(TextBlock Title, TextBlock Status, TextBlock Heard, DockPanel HeardPanel,
        TextBox English, Button Use);

    private sealed class Lane
    {
        public Lane(string model, ResultRow row)
        {
            Model = model;
            Row = row;
        }

        public string Model { get; }
        public ResultRow Row { get; }
        public Channel<byte[]> Audio { get; } = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
        public Task<ITranslationSession?>? Session { get; set; }
        public Task? Pump { get; set; }
        public TranslationRoute Route { get; set; }
        public bool UsesContext { get; set; }
        public volatile Exception? Failure;
    }
}
