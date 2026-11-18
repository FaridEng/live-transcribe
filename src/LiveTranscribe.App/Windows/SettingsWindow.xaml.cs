using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Media;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using LiveTranscribe.Core;
using LiveTranscribe.Core.Abstractions;
using LiveTranscribe.Core.Diagnostics;
using LiveTranscribe.Core.Dictation;
using LiveTranscribe.Core.Engines;
using LiveTranscribe.Core.Security;
using LiveTranscribe.Core.Settings;
using LiveTranscribe.Core.Translation;
using LiveTranscribe.Platform.Windows.Audio;
using LiveTranscribe.Platform.Windows.Input;
using LiveTranscribe.Platform.Windows.System;
using MessageBox = System.Windows.MessageBox;

namespace LiveTranscribe.App.Windows;

/// <summary>
/// Settings for the engine, microphone, dictation, shortcuts, hands-free safety and appearance (FR-8).
/// </summary>
public partial class SettingsWindow : Window
{
    private static readonly TimeSpan TestLength = TimeSpan.FromSeconds(4);

    /// <summary>The "no fallback" row: a profile with no id, never stored (FR-13.8 is opt-in).</summary>
    private static readonly EngineProfile NoFallback = new() { Id = string.Empty, Name = "Off" };

    private static readonly Option<EngineType>[] EngineTypes =
    [
        new("Gemini Live (streaming)", EngineType.GeminiLive),
        new("Gemini record-then-send", EngineType.GeminiFile)
    ];

    private static readonly Option<LanguageMode>[] Languages =
    [
        new("Auto (detect Persian / English)", LanguageMode.Auto),
        new("Persian only", LanguageMode.Persian),
        new("English only", LanguageMode.English)
    ];

    private static readonly Option<CleanupLevel>[] CleanupLevels =
    [
        new("Standard", CleanupLevel.Standard),
        new("Light", CleanupLevel.Light)
    ];

    private static readonly Option<PersianStyle>[] PersianStyles =
    [
        new("Colloquial (as spoken)", PersianStyle.Colloquial),
        new("Formal written", PersianStyle.Formal)
    ];

    private static readonly Option<int>[] MaxLengths =
        Enumerable.Range(1, 9).Select(m => new Option<int>(m == 1 ? "1 minute" : $"{m} minutes", m * 60)).ToArray();

    private static readonly Option<int>[] SilenceDurations =
        new[] { 3, 6, 10, 20 }.Select(s => new Option<int>($"after {s} seconds", s)).ToArray();

    private readonly ISettingsStore _settings;
    private readonly ISecretStore _secrets;
    private readonly IAudioDeviceCatalog _devices;
    private readonly ISpeechEngineFactory _engines;
    private readonly IEngineCatalog? _catalog;
    private readonly ISpeechTranslatorFactory _translators;
    private readonly HotkeyHook? _hook;

    // The shortcuts, captured by pressing keys rather than picked from a list (Settings → Shortcuts).
    private ToggleOption _holdKey;
    private ToggleOption _translateHoldKey;
    private ToggleOption _toggle;
    private ToggleOption _translateToggle;
    private ToggleOption _pasteLast;
    private System.Windows.Controls.Button? _capturingButton;

    /// <summary>Routes learned from "Load list", so the note can say what a typed model will do.</summary>
    private readonly Dictionary<string, TranslationRoute> _translationRoutes = new(StringComparer.OrdinalIgnoreCase);
    private readonly IAppLogger _log;

    /// <summary>The profiles being edited. Nothing reaches settings until Save (FR-13.1).</summary>
    private readonly List<EngineProfile> _profiles;
    private EngineProfile _editing;
    private bool _loadingProfile;

    private WasapiAudioCapture? _testCapture;
    private SoundPlayer? _playback;

    public SettingsWindow(ISettingsStore settings, ISecretStore secrets, IAudioDeviceCatalog devices,
        ISpeechEngineFactory engines, ISpeechTranslatorFactory translators, IAppLogger log, HotkeyHook? hook)
    {
        _settings = settings;
        _secrets = secrets;
        _devices = devices;
        _engines = engines;
        _catalog = engines as IEngineCatalog;
        _translators = translators;
        _log = log;
        _hook = hook;
        InitializeComponent();
        MaxHeight = SystemParameters.WorkArea.Height;
        Closed += (_, _) =>
        {
            StopTest();
            _hook?.CancelCapture();
        };

        var current = settings.Current;
        _profiles = current.EngineProfiles.Select(Clone).ToList();
        if (_profiles.Count == 0)
        {
            _profiles.Add(new EngineProfile());
        }

        _editing = _profiles.FirstOrDefault(p => p.Id == current.ActiveEngineProfileId) ?? _profiles[0];
        EngineTypeBox.ItemsSource = EngineTypes;
        RefreshProfileLists(active: _editing.Id, fallback: current.FallbackEngineProfileId);
        LoadProfileIntoEditor(_editing);
        LoadMicrophones(current.Microphone);

        LanguageBox.ItemsSource = Languages;
        LanguageBox.SelectedItem = Languages.First(o => o.Value == current.Dictation.Language);

        CleanupBox.ItemsSource = CleanupLevels;
        PersianStyleBox.ItemsSource = PersianStyles;
        PersianStyleBox.SelectedItem = PersianStyles.First(o => o.Value == current.Dictation.PersianStyle);

        LineBreaksBox.IsChecked = current.Dictation.AllowLineBreaks;
        TranslationModelBox.Text = current.Translation.ModelId;

        _holdKey = ToggleOption.Hold(current.Hotkeys);
        _toggle = ToggleOption.Toggle(current.Hotkeys);
        _translateHoldKey = ToggleOption.TranslateHold(current.Hotkeys);
        _translateToggle = ToggleOption.Translate(current.Hotkeys);
        _pasteLast = ToggleOption.PasteLast(current.Hotkeys);
        RefreshShortcutDisplays();

        if (_hook is null)
        {
            HoldKeyButton.IsEnabled = false;
            ToggleButton.IsEnabled = false;
            TranslateHoldButton.IsEnabled = false;
            TranslateToggleButton.IsEnabled = false;
            PasteLastButton.IsEnabled = false;
            ShortcutsNote.Text = "Shortcuts could not be changed in this session, because the keyboard hook did not start. Restart LiveTranscribe to try again.";
        }

        MaxLengthBox.ItemsSource = MaxLengths;
        MaxLengthBox.SelectedItem = MaxLengths.LastOrDefault(o => o.Value <= current.Dictation.MaxSessionSeconds) ?? MaxLengths[^1];

        StopOnSilenceBox.IsChecked = current.Dictation.StopOnSilence;
        SilenceBox.ItemsSource = SilenceDurations;
        SilenceBox.SelectedItem = SilenceDurations.FirstOrDefault(o => o.Value == current.Dictation.SilenceStopSeconds) ?? SilenceDurations[1];

        FloatingControlBox.IsChecked = current.Ui.FloatingControlEnabled;
        SoundsBox.IsChecked = current.Ui.SoundsEnabled;

        // What Windows actually has, not only what the settings file claims (FR-12.1).
        LaunchAtSignInBox.IsChecked = StartupRegistration.Current() is not null;

        PathsText.Text = $"Data: {AppPaths.Root}. The API key is stored encrypted for your Windows account.";
        UpdateKeyStatus();

        // Last, so the clean-up note ends up saying whatever the active engine can actually do.
        ApplyCapabilityGating();
    }

    private void RefreshShortcutDisplays()
    {
        HoldKeyText.Text = ChordName(_holdKey);
        ToggleText.Text = ChordName(_toggle);
        TranslateHoldText.Text = ChordName(_translateHoldKey);
        TranslateToggleText.Text = ChordName(_translateToggle);
        PasteLastText.Text = ChordName(_pasteLast);
    }

    private void OnCaptureHoldKey(object sender, RoutedEventArgs e) =>
        StartHoldCapture(HoldKeyButton, HoldKeyText, chord => { _holdKey = AsOption(chord); RefreshShortcutDisplays(); });

    private void OnCaptureTranslateHoldKey(object sender, RoutedEventArgs e) =>
        StartHoldCapture(TranslateHoldButton, TranslateHoldText, chord => { _translateHoldKey = AsOption(chord); RefreshShortcutDisplays(); });

    private void OnClearTranslateHoldKey(object sender, RoutedEventArgs e)
    {
        _hook?.CancelCapture();
        _translateHoldKey = new ToggleOption(0, false, false, false);
        RefreshShortcutDisplays();
    }

    private void OnCaptureToggle(object sender, RoutedEventArgs e) =>
        StartChordCapture(ToggleButton, ToggleText, chord => { _toggle = AsOption(chord); RefreshShortcutDisplays(); });

    private void OnClearToggle(object sender, RoutedEventArgs e)
    {
        _hook?.CancelCapture();
        _toggle = new ToggleOption(0, false, false, false);
        RefreshShortcutDisplays();
    }

    private void OnCaptureTranslateToggle(object sender, RoutedEventArgs e) =>
        StartChordCapture(TranslateToggleButton, TranslateToggleText, chord => { _translateToggle = AsOption(chord); RefreshShortcutDisplays(); });

    private void OnClearTranslateToggle(object sender, RoutedEventArgs e)
    {
        _hook?.CancelCapture();
        _translateToggle = new ToggleOption(0, false, false, false);
        RefreshShortcutDisplays();
    }

    private void OnCapturePasteLast(object sender, RoutedEventArgs e) =>
        StartChordCapture(PasteLastButton, PasteLastText, chord => { _pasteLast = AsOption(chord); RefreshShortcutDisplays(); });

    private void OnClearPasteLast(object sender, RoutedEventArgs e)
    {
        _hook?.CancelCapture();
        _pasteLast = new ToggleOption(0, false, false, false);
        RefreshShortcutDisplays();
    }

    private static ToggleOption AsOption(ToggleChord chord) => new(chord.VirtualKey, chord.Ctrl, chord.Alt, chord.Shift);

    /// <summary>
    /// Arms the hook to report the next hold shortcut: a bare modifier released on its own (the default,
    /// e.g. Right Ctrl), or a key held down together with whichever of Ctrl/Alt/Shift go with it (user
    /// request, 2026-09-28, so Alt + 1 can be held to talk, not just tapped). The instruction goes in the
    /// row's own display text, not the button: the button is a fixed 80px wide, too narrow for a sentence
    /// (this used to overflow onto the "Off" button next to it - user report, 2026-09-28).
    /// </summary>
    private void StartHoldCapture(System.Windows.Controls.Button button, TextBlock display, Action<ToggleChord> apply)
    {
        if (_hook is null)
        {
            return;
        }

        ResetCapturingButton();
        _capturingButton = button;
        button.Content = "Waiting...";
        display.Text = "Press a key, or hold a modifier and press a key (Esc cancels)";
        _hook.BeginCaptureHold(
            chord => Dispatcher.BeginInvoke(() =>
            {
                EndCapture(button);
                apply(chord);
            }),
            () => Dispatcher.BeginInvoke(() => EndCapture(button)));
    }

    /// <summary>Arms the hook to report the next chord (a key plus whichever modifiers go with it).</summary>
    private void StartChordCapture(System.Windows.Controls.Button button, TextBlock display, Action<ToggleChord> apply)
    {
        if (_hook is null)
        {
            return;
        }

        ResetCapturingButton();
        _capturingButton = button;
        button.Content = "Waiting...";
        display.Text = "Hold Ctrl/Alt/Shift, then press the key (Esc cancels)";
        _hook.BeginCaptureChord(
            chord => Dispatcher.BeginInvoke(() =>
            {
                EndCapture(button);
                apply(chord);
            }),
            () => Dispatcher.BeginInvoke(() => EndCapture(button)));
    }

    private void EndCapture(System.Windows.Controls.Button button)
    {
        button.Content = "Change";
        if (ReferenceEquals(_capturingButton, button))
        {
            _capturingButton = null;
        }

        // Restores whichever row's display text was showing the capture prompt (a no-op on the row that
        // just captured a value, since apply() already refreshes it right after this).
        RefreshShortcutDisplays();
    }

    /// <summary>Only one row records at a time: starting another finishes the previous one, unset.</summary>
    private void ResetCapturingButton()
    {
        if (_capturingButton is { } previous)
        {
            previous.Content = "Change";
            _capturingButton = null;
        }
    }

    private static string ChordName(ToggleOption chord) =>
        chord.VirtualKey == 0
            ? "Off"
            : string.Join(" + ", new[] { chord.Ctrl ? "Ctrl" : null, chord.Alt ? "Alt" : null, chord.Shift ? "Shift" : null, KeyName(chord.VirtualKey) }
                .Where(part => part is not null));

    /// <summary>A readable name for a virtual-key code, for keys this app already names specially plus any other key.</summary>
    private static string KeyName(int virtualKey)
    {
        if (virtualKey == 0)
        {
            return "Off";
        }

        var key = System.Windows.Input.KeyInterop.KeyFromVirtualKey(virtualKey);
        return key switch
        {
            System.Windows.Input.Key.RightCtrl => "Right Ctrl",
            System.Windows.Input.Key.LeftCtrl => "Left Ctrl",
            System.Windows.Input.Key.RightAlt => "Right Alt",
            System.Windows.Input.Key.LeftAlt => "Left Alt",
            System.Windows.Input.Key.RightShift => "Right Shift",
            System.Windows.Input.Key.LeftShift => "Left Shift",
            System.Windows.Input.Key.CapsLock => "Caps Lock",
            System.Windows.Input.Key.Scroll => "Scroll Lock",
            System.Windows.Input.Key.Pause => "Pause",
            System.Windows.Input.Key.Insert => "Insert",
            System.Windows.Input.Key.Space => "Space",
            >= System.Windows.Input.Key.D0 and <= System.Windows.Input.Key.D9 =>
                ((int)(key - System.Windows.Input.Key.D0)).ToString(),
            >= System.Windows.Input.Key.NumPad0 and <= System.Windows.Input.Key.NumPad9 =>
                $"Numpad {(int)(key - System.Windows.Input.Key.NumPad0)}",
            _ => key.ToString()
        };
    }

    /// <summary>What the engine does to the words, in the user's terms (FR-5.5).</summary>
    private void OnCleanupChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CleanupBox.SelectedItem is not Option<CleanupLevel> option || CleanupNote is null)
        {
            return;
        }

        CleanupNote.Text = option.Value == CleanupLevel.Standard
            ? "Standard: the engine drops fillers (um, اِ) and keeps only the corrected version when you change your mind mid-sentence. It also tends to tidy speech towards written Persian."
            : "Light: written down as spoken, with punctuation only. Nothing is removed or rephrased.";
    }

    private void OnLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LanguageBox.SelectedItem is not Option<LanguageMode> option || LanguageNote is null)
        {
            return;
        }

        LanguageNote.Text = LanguageModes.Describe(option.Value);
    }

    /// <summary>
    /// Formal Persian is rewriting, not transcription, so the engine alone cannot deliver it.
    /// The setting is kept and the truth is shown next to it until Q12 is decided (architecture T1).
    /// </summary>
    private void OnPersianStyleChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PersianStyleBox.SelectedItem is not Option<PersianStyle> option || PersianStyleNote is null)
        {
            return;
        }

        PersianStyleNote.Text = option.Value == PersianStyle.Formal
            ? "Formal style needs a second, text-rewriting model, which is not configured yet. Your choice is saved, but dictation still comes out the way the engine writes it."
            : "Colloquial keeps the spoken forms: نمی‌دونم, رو, کدوم.";
    }

    /// <summary>The profile dictation will use. Switching it takes effect on the next dictation (FR-13.3).</summary>
    private EngineProfile ActiveProfile => ActiveProfileBox.SelectedItem as EngineProfile ?? _editing;

    private static EngineProfile Clone(EngineProfile profile) => new()
    {
        Id = profile.Id,
        Name = profile.Name,
        EngineType = profile.EngineType,
        ModelId = profile.ModelId,
        SecretRef = profile.SecretRef,
        Cleanup = profile.Cleanup,
        PrivacyNote = profile.PrivacyNote
    };

    /// <summary>Rebuilds the three profile drop-downs from the working list without firing edits.</summary>
    private void RefreshProfileLists(string? active, string? fallback)
    {
        var wasLoading = _loadingProfile;
        _loadingProfile = true;
        try
        {
            ProfileBox.ItemsSource = null;
            ProfileBox.ItemsSource = _profiles;
            ProfileBox.SelectedItem = _editing;

            ActiveProfileBox.ItemsSource = null;
            ActiveProfileBox.ItemsSource = _profiles;
            ActiveProfileBox.SelectedItem = _profiles.FirstOrDefault(p => p.Id == active) ?? _profiles[0];

            // "Off" is a profile with no id, so the drop-down stays a plain list of names.
            var fallbackChoices = new List<EngineProfile> { NoFallback };
            fallbackChoices.AddRange(_profiles);
            FallbackProfileBox.ItemsSource = fallbackChoices;
            FallbackProfileBox.SelectedItem = _profiles.FirstOrDefault(p => p.Id == fallback) ?? NoFallback;

            DeleteProfileButton.IsEnabled = _profiles.Count > 1;
        }
        finally
        {
            _loadingProfile = wasLoading;
        }
    }

    private void LoadProfileIntoEditor(EngineProfile profile)
    {
        _loadingProfile = true;
        try
        {
            _editing = profile;
            ProfileNameBox.Text = profile.Name;
            EngineTypeBox.SelectedItem = EngineTypes.First(o => o.Value == profile.EngineType);
            ModelIdBox.ItemsSource = null;
            ModelIdBox.Text = profile.ModelId;
            TestConnectionText.Text = string.Empty;
        }
        finally
        {
            _loadingProfile = false;
        }

        ShowCapabilities();
    }

    /// <summary>Copies the editor back into the profile it belongs to, so switching profiles keeps edits.</summary>
    private void FlushEditor()
    {
        _editing.Name = ProfileNameBox.Text.Trim() is { Length: > 0 } name ? name : _editing.Name;
        _editing.ModelId = ModelIdBox.Text.Trim() is { Length: > 0 } model ? model : _editing.ModelId;
        if (EngineTypeBox.SelectedItem is Option<EngineType> type)
        {
            _editing.EngineType = type.Value;
        }
    }

    private void OnProfileChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingProfile || ProfileBox.SelectedItem is not EngineProfile profile || ReferenceEquals(profile, _editing))
        {
            return;
        }

        FlushEditor();
        LoadProfileIntoEditor(profile);
        RefreshProfileLists(ActiveProfile.Id, (FallbackProfileBox.SelectedItem as EngineProfile)?.Id);
    }

    private void OnAddProfile(object sender, RoutedEventArgs e)
    {
        FlushEditor();

        // A second profile is usually the other API against the same key, so that is what it starts as.
        var live = _editing.EngineType == EngineType.GeminiLive;
        var added = new EngineProfile
        {
            Name = live ? "Gemini 3.5 Transcribe (record-then-send)" : "Gemini 3.5 Transcribe Live",
            EngineType = live ? EngineType.GeminiFile : EngineType.GeminiLive,
            ModelId = live ? "gemini-3.5-transcribe" : "gemini-3.5-transcribe-live",
            SecretRef = _editing.SecretRef
        };

        _profiles.Add(added);
        LoadProfileIntoEditor(added);
        RefreshProfileLists(ActiveProfile.Id, (FallbackProfileBox.SelectedItem as EngineProfile)?.Id);
    }

    private void OnDeleteProfile(object sender, RoutedEventArgs e)
    {
        if (_profiles.Count <= 1)
        {
            return;
        }

        var removed = _editing;
        if (MessageBox.Show(this, $"Delete the profile \"{removed.Name}\"?", "LiveTranscribe",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        var active = ActiveProfile.Id;
        var fallback = (FallbackProfileBox.SelectedItem as EngineProfile)?.Id;
        _profiles.Remove(removed);
        LoadProfileIntoEditor(_profiles[0]);
        RefreshProfileLists(active == removed.Id ? _profiles[0].Id : active, fallback == removed.Id ? null : fallback);
    }

    private void OnEngineTypeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingProfile)
        {
            return;
        }

        // The model list belongs to the API that was selected before, so drop it rather than mislead.
        ModelIdBox.ItemsSource = null;
        TestConnectionText.Text = string.Empty;
        ShowCapabilities();
    }

    private void OnActiveProfileChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingProfile || CleanupBox is null)
        {
            return;
        }

        ApplyCapabilityGating();
    }

    /// <summary>One line saying what this engine does and does not do (FR-13.4).</summary>
    private void ShowCapabilities()
    {
        if (CapabilityNote is null || EngineTypeBox.SelectedItem is not Option<EngineType> type)
        {
            return;
        }

        var probe = Clone(_editing);
        probe.EngineType = type.Value;
        var can = _engines.Describe(probe);

        CapabilityNote.Text = can.Streaming
            ? $"Streams while you speak, with built-in clean-up, and allows up to {can.MaxSessionLength.TotalMinutes:F0} minutes per dictation."
            : $"Sends the recording after you stop, so the wait is longer and there is no clean-up mode; up to {can.MaxSessionLength.TotalMinutes:F0} minutes per dictation.";

        ApplyCapabilityGating();
    }

    /// <summary>
    /// Disables what the profile that dictation will use cannot do, with the reason next to it, rather
    /// than leaving a control that silently does nothing (FR-13.4).
    /// </summary>
    private void ApplyCapabilityGating()
    {
        if (CleanupBox is null || CleanupNote is null)
        {
            return;
        }

        var active = ActiveProfile;
        var can = _engines.Describe(active);
        CleanupBox.IsEnabled = can.SmartCleanup;
        CleanupBox.SelectedItem = CleanupLevels.First(o => o.Value == active.Cleanup);

        if (!can.SmartCleanup)
        {
            CleanupNote.Text = $"Clean-up is off because \"{active.Name}\" has no clean-up mode: it writes down what it hears. Switch to a Live profile to get it back.";
        }
        else
        {
            OnCleanupChanged(CleanupBox, null!);
        }
    }

    /// <summary>Fills the model drop-down from the provider, so the list is what the key can reach (FR-13.2).</summary>
    private async void OnLoadModels(object sender, RoutedEventArgs e)
    {
        if (_catalog is null)
        {
            return;
        }

        var key = CurrentKey();
        if (key.Length == 0)
        {
            TestConnectionText.Text = "Enter the API key first, then load the list.";
            return;
        }

        FlushEditor();
        LoadModelsButton.IsEnabled = false;
        TestConnectionText.Text = "Asking the provider which models this key can reach...";

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var models = await _catalog.ListModelsAsync(_editing.EngineType, key, timeout.Token);
            var typed = _editing.ModelId;
            ModelIdBox.ItemsSource = models.Select(m => m.Id).ToList();
            ModelIdBox.Text = typed;
            TestConnectionText.Text = models.Count == 0
                ? "The provider listed no model for this API."
                : $"{models.Count} models available for this API. Typing an id that is not listed is still allowed.";
        }
        catch (Exception ex)
        {
            TestConnectionText.Text = Describe(ex);
        }
        finally
        {
            LoadModelsButton.IsEnabled = true;
        }
    }

    /// <summary>Checks the key and the model without dictating (FR-13.10).</summary>
    private async void OnTestConnection(object sender, RoutedEventArgs e)
    {
        var key = CurrentKey();
        if (key.Length == 0)
        {
            TestConnectionText.Text = "No API key yet. Enter it above and save, or type it in the key box first.";
            return;
        }

        FlushEditor();
        TestConnectionButton.IsEnabled = false;
        TestConnectionText.Text = $"Testing {_editing.ModelId}...";

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var report = await _engines.Create(_editing, key).TestAsync(timeout.Token);
            TestConnectionText.Text = $"{report.Summary} ({report.RoundTrip.TotalMilliseconds:F0} ms)";
            _log.Info($"Connection test passed for profile '{_editing.Name}' in {report.RoundTrip.TotalMilliseconds:F0} ms.");
        }
        catch (Exception ex)
        {
            TestConnectionText.Text = Describe(ex);
            _log.Warn($"Connection test failed for profile '{_editing.Name}'.");
        }
        finally
        {
            TestConnectionButton.IsEnabled = true;
        }
    }

    /// <summary>Lists the models that can translate, with the route each would take (architecture T14).</summary>
    private async void OnLoadTranslationModels(object sender, RoutedEventArgs e)
    {
        var key = CurrentKey();
        if (key.Length == 0)
        {
            TranslationRouteNote.Text = "Enter the API key first, then load the list.";
            return;
        }

        LoadTranslationModelsButton.IsEnabled = false;
        TranslationRouteNote.Text = "Asking the provider which models this key can reach...";
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var models = await _translators.ListModelsAsync(key, timeout.Token);
            foreach (var model in models)
            {
                _translationRoutes[model.Id] = model.Route;
            }

            var typed = TranslationModelBox.Text;
            TranslationModelBox.ItemsSource = models.Select(m => m.Id).ToList();
            TranslationModelBox.Text = typed;
            ShowTranslationRoute();
        }
        catch (Exception ex)
        {
            TranslationRouteNote.Text = Describe(ex);
        }
        finally
        {
            LoadTranslationModelsButton.IsEnabled = true;
        }
    }

    private void OnTranslationModelChanged(object sender, TextChangedEventArgs e) => ShowTranslationRoute();

    /// <summary>What the chosen model will do with a dictation, in one line.</summary>
    private void ShowTranslationRoute()
    {
        if (TranslationRouteNote is null)
        {
            return;
        }

        var id = TranslationModelBox.Text.Trim();
        TranslationRouteNote.Text = !_translationRoutes.TryGetValue(id, out var route)
            ? "Live models hear the audio and answer in English (one step). Text models translate what your dictation profile wrote down (two steps). \"Load list\" shows which is which."
            : route == TranslationRoute.TwoStep
                ? $"Two steps: \"{ActiveProfile.Name}\" writes down what you said, then {id} translates the text. Uses the topic note and tone."
                : id.Contains("translate", StringComparison.OrdinalIgnoreCase)
                    ? $"One step: {id} is an interpreter. It translates while you speak, but takes no topic note or tone."
                    : $"One step: {id} hears you and answers in English. Uses the topic note and tone.";
    }

    /// <summary>Opens the side-by-side test with the settings as they are in this window, saved or not.</summary>
    private void OnTryTranslation(object sender, RoutedEventArgs e)
    {
        var key = CurrentKey();
        if (key.Length == 0)
        {
            TranslationRouteNote.Text = "Enter the API key first.";
            return;
        }

        FlushEditor();
        var transcriber = Clone(ActiveProfile);
        transcriber.Cleanup = ((Option<CleanupLevel>)CleanupBox.SelectedItem).Value;
        var options = new EngineSessionOptions(((Option<LanguageMode>)LanguageBox.SelectedItem).Value, transcriber.Cleanup);
        var model = TranslationModelBox.Text.Trim() is { Length: > 0 } typed ? typed : _settings.Current.Translation.ModelId;

        var window = new TranslationTestWindow(_settings, _translators, key, transcriber, options,
            (MicrophoneBox.SelectedItem as AudioDevice)?.Id, model, _log) { Owner = this };
        window.ModelChosen += chosen => TranslationModelBox.Text = chosen;
        window.Show();
    }

    /// <summary>The key typed in this window if there is one, otherwise the stored key for this profile.</summary>
    private string CurrentKey() =>
        ApiKeyBox.Password.Trim() is { Length: > 0 } typed ? typed : _secrets.Get(_editing.SecretRef) ?? string.Empty;

    private static string Describe(Exception ex) => ex switch
    {
        EngineException engine => engine.Message,
        OperationCanceledException => "The provider did not answer in time.",
        _ => ex.Message
    };

    private void UpdateKeyStatus()
    {
        var stored = _secrets.Get(_editing.SecretRef);
        // Never show the key in full once it is stored (FR-11.3).
        KeyStatusText.Text = string.IsNullOrEmpty(stored)
            ? "No key stored yet."
            : $"Key stored ({Mask(stored)}). Leave the box empty to keep it.";
        RemoveKeyButton.IsEnabled = !string.IsNullOrEmpty(stored);
    }

    private static string Mask(string key) =>
        key.Length <= 6 ? "****" : $"{key[..4]}...{key[^2..]}";

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (ModelIdBox.Text.Trim().Length == 0)
        {
            MessageBox.Show(this, "Model id cannot be empty.", "LiveTranscribe");
            return;
        }

        FlushEditor();
        _hook?.CancelCapture();
        var hold = _holdKey;
        var toggle = _toggle;
        var current = _settings.Current;

        var pasteLast = _pasteLast;
        var translateHold = _translateHoldKey;
        var translateToggle = _translateToggle;

        // Hold shortcuts can now carry modifiers too (user request, 2026-09-28), so a clash between any
        // two of the five - not just translate-hold against hold - is checked the same way.
        var chords = new[] { hold, translateHold, toggle, pasteLast, translateToggle }.Where(c => c.VirtualKey != 0).ToList();
        if (chords.Distinct().Count() != chords.Count)
        {
            MessageBox.Show(this, "Two shortcuts are set to the same keys. Give each one its own.",
                "LiveTranscribe", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // FR-2.5: refuse a shortcut another application has already registered.
        foreach (var (chord, stored) in new[]
                 {
                     (toggle, ToggleOption.Toggle(current.Hotkeys)), (pasteLast, ToggleOption.PasteLast(current.Hotkeys)),
                     (translateToggle, ToggleOption.Translate(current.Hotkeys))
                 })
        {
            if (chord != stored && chord.VirtualKey != 0 && !IsChordFree(chord))
            {
                MessageBox.Show(this, $"{ChordName(chord)} is already used by another application. Choose a different shortcut.",
                    "LiveTranscribe", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }

        // Only a real change is stored, so a saved device that is unplugged right now keeps its name (FR-7.3).
        if (MicrophoneBox.SelectedItem is AudioDevice microphone && microphone.Id != current.Microphone.DeviceId)
        {
            current.Microphone.DeviceId = microphone.Id;
            current.Microphone.DeviceName = microphone.Id is null ? null : microphone.Name;
        }

        var active = ActiveProfile;
        active.Cleanup = ((Option<CleanupLevel>)CleanupBox.SelectedItem).Value;
        current.EngineProfiles = _profiles.Select(Clone).ToList();
        current.ActiveEngineProfileId = active.Id;

        // A profile cannot fall back to itself: that is just the same failure twice (FR-13.8).
        var fallback = FallbackProfileBox.SelectedItem as EngineProfile;
        current.FallbackEngineProfileId =
            fallback is null || fallback.Id.Length == 0 || fallback.Id == active.Id ? null : fallback.Id;

        current.Dictation.Language = ((Option<LanguageMode>)LanguageBox.SelectedItem).Value;
        current.Dictation.PersianStyle = ((Option<PersianStyle>)PersianStyleBox.SelectedItem).Value;
        current.Dictation.AllowLineBreaks = LineBreaksBox.IsChecked == true;
        if (TranslationModelBox.Text.Trim() is { Length: > 0 } translationModel)
        {
            current.Translation.ModelId = translationModel;
        }

        current.Hotkeys.HoldVirtualKey = hold.VirtualKey;
        current.Hotkeys.HoldRequiresCtrl = hold.Ctrl;
        current.Hotkeys.HoldRequiresAlt = hold.Alt;
        current.Hotkeys.HoldRequiresShift = hold.Shift;
        current.Hotkeys.ToggleVirtualKey = toggle.VirtualKey;
        current.Hotkeys.ToggleRequiresCtrl = toggle.Ctrl;
        current.Hotkeys.ToggleRequiresAlt = toggle.Alt;
        current.Hotkeys.ToggleRequiresShift = toggle.Shift;
        current.Hotkeys.PasteLastVirtualKey = pasteLast.VirtualKey;
        current.Hotkeys.PasteLastRequiresCtrl = pasteLast.Ctrl;
        current.Hotkeys.PasteLastRequiresAlt = pasteLast.Alt;
        current.Hotkeys.PasteLastRequiresShift = pasteLast.Shift;
        current.Hotkeys.TranslateHoldVirtualKey = translateHold.VirtualKey;
        current.Hotkeys.TranslateHoldRequiresCtrl = translateHold.Ctrl;
        current.Hotkeys.TranslateHoldRequiresAlt = translateHold.Alt;
        current.Hotkeys.TranslateHoldRequiresShift = translateHold.Shift;
        current.Hotkeys.TranslateToggleVirtualKey = translateToggle.VirtualKey;
        current.Hotkeys.TranslateToggleRequiresCtrl = translateToggle.Ctrl;
        current.Hotkeys.TranslateToggleRequiresAlt = translateToggle.Alt;
        current.Hotkeys.TranslateToggleRequiresShift = translateToggle.Shift;
        current.Dictation.MaxSessionSeconds = ((Option<int>)MaxLengthBox.SelectedItem).Value;
        current.Dictation.StopOnSilence = StopOnSilenceBox.IsChecked == true;
        current.Dictation.SilenceStopSeconds = ((Option<int>)SilenceBox.SelectedItem).Value;
        current.Ui.FloatingControlEnabled = FloatingControlBox.IsChecked == true;
        current.Ui.SoundsEnabled = SoundsBox.IsChecked == true;

        current.LaunchAtSignIn = LaunchAtSignInBox.IsChecked == true;
        var startupApplied = StartupRegistration.Apply(current.LaunchAtSignIn, _log);
        LaunchAtSignInNote.Text = startupApplied
            ? current.LaunchAtSignIn
                ? "On. Windows will start LiveTranscribe for your account at sign-in."
                : "Off. Windows will not start LiveTranscribe on its own."
            : "Windows refused to change the sign-in entry. The setting is saved, but it did not take effect.";

        var key = ApiKeyBox.Password;
        if (key.Length > 0)
        {
            // One key per provider, shared by that provider's profiles (FR-13.7).
            foreach (var secretRef in _profiles.Select(p => p.SecretRef).Distinct())
            {
                _secrets.Set(secretRef, key.Trim());
            }

            ApiKeyBox.Clear();
        }

        _settings.Save(current);
        UpdateKeyStatus();
        RefreshProfileLists(active.Id, current.FallbackEngineProfileId);
        SaveStatusText.Text = $"Saved at {DateTime.Now:HH:mm:ss}. The next dictation uses \"{active.Name}\".";
    }

    private void OnRemoveKey(object sender, RoutedEventArgs e)
    {
        var confirm = MessageBox.Show(this, "Remove the stored API key?", "LiveTranscribe",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        _secrets.Remove(_editing.SecretRef);
        UpdateKeyStatus();
        _settings.Save(_settings.Current);
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// The Windows inputs plus "System default" (FR-7.1). A saved device that is not plugged in stays
    /// in the list, so opening and saving Settings never forgets it (FR-7.2, FR-7.3).
    /// </summary>
    private void LoadMicrophones(MicrophoneSettings saved)
    {
        var devices = _devices.List().ToList();
        if (saved.DeviceId is not null && devices.All(d => d.Id != saved.DeviceId))
        {
            devices.Add(new AudioDevice(saved.DeviceId, $"{saved.DeviceName ?? "Saved microphone"} (not connected)"));
        }

        MicrophoneBox.ItemsSource = devices;
        MicrophoneBox.SelectedItem = devices.FirstOrDefault(d => d.Id == saved.DeviceId) ?? devices[0];
    }

    /// <summary>
    /// Records a few seconds from the chosen microphone, shows the level, measures the background
    /// noise and plays the recording back (FR-7.6). The audio stays in memory and is dropped after.
    /// </summary>
    private async void OnTestMicrophone(object sender, RoutedEventArgs e)
    {
        var device = MicrophoneBox.SelectedItem as AudioDevice;
        TestMicrophoneButton.IsEnabled = false;
        PrivacyLink.Visibility = Visibility.Collapsed;
        MicrophoneTestText.Text = "Recording for 4 seconds - say something.";

        var recording = new MemoryStream();
        var noise = new NoiseFloorTracker();
        var peak = 0f;
        Exception? lost = null;
        WasapiAudioCapture? capture = null;

        try
        {
            _playback?.Stop();
            capture = _testCapture ??= new WasapiAudioCapture(_log);
            capture.PreferredDeviceId = device?.Id;
            capture.Start(
                chunk =>
                {
                    var level = AudioLevel.Rms(chunk);
                    lock (recording)
                    {
                        recording.Write(chunk);
                        noise.Add(level);
                        peak = Math.Max(peak, level);
                    }

                    Dispatcher.BeginInvoke(() => MicrophoneLevel.Value = Math.Clamp(Math.Sqrt(level / 0.25), 0, 1));
                },
                ex => lost = ex);

            await Task.Delay(TestLength);
            await capture.StopAsync();
        }
        catch (MicrophoneException ex)
        {
            _log.Warn($"Microphone test failed ({ex.Reason}).");
            ShowTestFailure(ex.Reason);
            return;
        }
        finally
        {
            TestMicrophoneButton.IsEnabled = true;
            MicrophoneLevel.Value = 0;
        }

        if (!ReferenceEquals(_testCapture, capture))
        {
            // The window was closed during the test.
            return;
        }

        if (lost is not null)
        {
            ShowTestFailure((lost as MicrophoneException)?.Reason ?? MicrophoneFailure.Disconnected);
            return;
        }

        byte[] pcm;
        lock (recording)
        {
            pcm = recording.ToArray();
        }

        var floor = AudioLevel.ToDecibels(noise.Floor ?? 0);
        var loudest = AudioLevel.ToDecibels(peak);
        _log.Info($"Microphone test: noise floor {floor:F0} dBFS, peak {loudest:F0} dBFS.");

        if (peak < AudioLevel.SilenceLevel)
        {
            MicrophoneTestText.Text = "No sound came through. Check that this microphone is not muted and is the one you speak into.";
            return;
        }

        MicrophoneTestText.Text = peak < noise.VoiceThreshold * 2
            ? $"Very quiet: your voice barely rises above the background ({loudest:F0} dB against {floor:F0} dB). Move closer or raise the input level in Windows. Playing it back..."
            : $"Working. Background noise {floor:F0} dB, your voice up to {loudest:F0} dB. Playing it back...";
        Play(pcm);
    }

    private void ShowTestFailure(MicrophoneFailure reason)
    {
        MicrophoneTestText.Text = reason switch
        {
            MicrophoneFailure.AccessDenied => "Windows is blocking microphone access for desktop apps.",
            MicrophoneFailure.Disconnected => "The microphone was disconnected during the test.",
            _ => "This microphone could not be opened. Check that it is connected."
        };
        PrivacyLink.Visibility = reason == MicrophoneFailure.AccessDenied ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Plays 16 kHz mono PCM from memory; nothing touches the disk.</summary>
    private void Play(byte[] pcm)
    {
        var wav = new MemoryStream(44 + pcm.Length);
        using (var writer = new BinaryWriter(wav, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            writer.Write("RIFF"u8);
            writer.Write(36 + pcm.Length);
            writer.Write("WAVEfmt "u8);
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)1);
            writer.Write(16000);
            writer.Write(32000);
            writer.Write((short)2);
            writer.Write((short)16);
            writer.Write("data"u8);
            writer.Write(pcm.Length);
            writer.Write(pcm);
        }

        wav.Position = 0;
        _playback?.Dispose();
        _playback = new SoundPlayer(wav);
        _playback.Play();
    }

    private void StopTest()
    {
        _playback?.Stop();
        _playback?.Dispose();
        _playback = null;

        if (_testCapture is { } capture)
        {
            _testCapture = null;
            _ = capture.StopAsync().ContinueWith(_ => capture.Dispose());
        }
    }

    private void OnOpenPrivacySettings(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(MicrophonePrivacy.SettingsUri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _log.Warn($"Could not open the privacy settings: {ex.Message}");
        }
    }

    /// <summary>Registers the chord for a moment: failure means another application owns it.</summary>
    private bool IsChordFree(ToggleOption chord)
    {
        const uint modAlt = 0x1;
        const uint modControl = 0x2;
        const uint modShift = 0x4;
        const uint modNoRepeat = 0x4000;
        const int probeId = 0x5A17;

        var modifiers = modNoRepeat | (chord.Ctrl ? modControl : 0) | (chord.Alt ? modAlt : 0) | (chord.Shift ? modShift : 0);
        var handle = new WindowInteropHelper(this).Handle;
        if (!RegisterHotKey(handle, probeId, modifiers, (uint)chord.VirtualKey))
        {
            return false;
        }

        UnregisterHotKey(handle, probeId);
        return true;
    }

    private sealed record Option<T>(string Name, T Value);

    private sealed record ToggleOption(int VirtualKey, bool Ctrl, bool Alt, bool Shift)
    {
        public static ToggleOption Hold(HotkeySettings h) =>
            Stored(h.HoldVirtualKey, h.HoldRequiresCtrl, h.HoldRequiresAlt, h.HoldRequiresShift);

        public static ToggleOption TranslateHold(HotkeySettings h) =>
            Stored(h.TranslateHoldVirtualKey, h.TranslateHoldRequiresCtrl, h.TranslateHoldRequiresAlt, h.TranslateHoldRequiresShift);

        public static ToggleOption Toggle(HotkeySettings h) =>
            Stored(h.ToggleVirtualKey, h.ToggleRequiresCtrl, h.ToggleRequiresAlt, h.ToggleRequiresShift);

        public static ToggleOption PasteLast(HotkeySettings h) =>
            Stored(h.PasteLastVirtualKey, h.PasteLastRequiresCtrl, h.PasteLastRequiresAlt, h.PasteLastRequiresShift);

        public static ToggleOption Translate(HotkeySettings h) =>
            Stored(h.TranslateToggleVirtualKey, h.TranslateToggleRequiresCtrl, h.TranslateToggleRequiresAlt, h.TranslateToggleRequiresShift);

        // With no key, the stored modifiers mean nothing, so every "off" compares equal.
        private static ToggleOption Stored(int key, bool ctrl, bool alt, bool shift) =>
            key == 0 ? new(0, false, false, false) : new(key, ctrl, alt, shift);
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
}
