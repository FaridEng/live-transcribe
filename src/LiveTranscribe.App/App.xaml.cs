using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using LiveTranscribe.App.FloatingControl;
using LiveTranscribe.App.Indicator;
using LiveTranscribe.App.Sounds;
using LiveTranscribe.App.Tray;
using LiveTranscribe.App.Windows;
using LiveTranscribe.Core;
using LiveTranscribe.Core.Abstractions;
using LiveTranscribe.Core.Diagnostics;
using LiveTranscribe.Core.Dictation;
using LiveTranscribe.Core.Security;
using LiveTranscribe.Core.Settings;
using LiveTranscribe.Core.Translation;
using LiveTranscribe.Engines.Gemini;
using LiveTranscribe.Platform.Windows.Audio;
using LiveTranscribe.Platform.Windows.Automation;
using LiveTranscribe.Platform.Windows.Input;
using LiveTranscribe.Platform.Windows.Security;
using LiveTranscribe.Platform.Windows.System;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace LiveTranscribe.App;

/// <summary>
/// Composition root. The app lives in the tray and has no main window (FR-12.1).
/// </summary>
public partial class App : Application
{
    private SingleInstanceGuard? _instance;
    private IAppLogger? _log;
    private ISettingsStore? _settings;
    private ISecretStore? _secrets;
    private TrayIconHost? _tray;
    private DictationController? _dictation;
    private HotkeyHook? _hook;
    private ClipboardPasteInserter? _inserter;
    private WindowsTargetTracker? _tracker;
    private WasapiDeviceCatalog? _devices;
    private GeminiEngineFactory? _engines;
    private GeminiTranslatorFactory? _translators;
    private TranslationService? _translation;
    private FloatingControlWindow? _floating;
    private ListeningIndicatorWindow? _indicator;
    private SoundCues? _sounds;
    private DictationState _lastState;
    private int _levelUpdatePending;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _instance = SingleInstanceGuard.Acquire("LiveTranscribe.SingleInstance");
        if (!_instance.IsOwner)
        {
            MessageBox.Show("LiveTranscribe is already running. Look for its icon in the notification area.",
                "LiveTranscribe", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        AppPaths.EnsureCreated();
        _log = new FileLogger(AppPaths.LogDirectory);
        _log.Info($"Starting. Elevated={ElevationInfo.IsElevated}.");

        DispatcherUnhandledException += OnUnhandledException;
        if (SynchronizationContext.Current is null)
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher));
        }

        _settings = new JsonSettingsStore(AppPaths.SettingsFile, _log);

        // Repairs the sign-in entry when the app has moved since it was set (FR-12.1).
        StartupRegistration.Reconcile(_settings.Current.LaunchAtSignIn, _log);
        _secrets = new DpapiSecretStore(AppPaths.SecretsFile, _log);
        _sounds = new SoundCues { Enabled = _settings.Current.Ui.SoundsEnabled };

        _tray = new TrayIconHost(_log);
        _tray.SettingsRequested += ShowSettings;
        _tray.QuitRequested += Shutdown;

        _inserter = new ClipboardPasteInserter(_log);
        var typing = new UnicodeTypingInserter(_log);
        _tracker = new WindowsTargetTracker(_log);
        _tracker.Warmup();
        _devices = new WasapiDeviceCatalog(_log);
        _engines = new GeminiEngineFactory();
        _translators = new GeminiTranslatorFactory(_engines);
        _translation = new TranslationService(_translators, _settings);
        _dictation = new DictationController(
            new WasapiAudioCapture(_log),
            _devices,
            _engines,
            _inserter,
            _inserter,
            _tracker,
            _settings,
            _secrets,
            _log,
            typing,
            _translation);

        _tray.StartStopRequested += () => _ = _dictation.ToggleAsync();
        _tray.RetryRequested += () => _ = _dictation.RetryAsync();
        _tray.CopyLastRequested += OnCopyKeptText;
        _tray.PauseToggled += () => _dictation.SetPaused(_dictation.State != DictationState.Paused);

        CreateFloatingControl();
        _indicator = new ListeningIndicatorWindow();

        _dictation.StateChanged += OnStateChanged;
        _dictation.LevelChanged += OnLevelChanged;
        _dictation.HandsFreeWarning += remaining => Dispatcher.BeginInvoke(() =>
            _floating?.ShowMessage($"Hands-free stops in {Math.Max(1, (int)remaining.TotalSeconds)} s"));
        _dictation.MicrophoneChanged += (device, fallback) => Dispatcher.BeginInvoke(() =>
            _floating?.ShowMessage(fallback
                ? $"Saved microphone missing - using {device.Name}"
                : $"Microphone: {device.Name}"));
        _dictation.Notice += OnNotice;
        _dictation.EngineSwitched += message => _floating?.ShowMessage(message);
        _dictation.RetryAvailabilityChanged += UpdateOffers;
        _dictation.PasswordOverrideChanged += OnPasswordOverrideChanged;
        _translation.ContextChanged += UpdateOutputChip;
        _dictation.TranslationHeard += heard => _floating?.ShowHeard(heard);

        _lastState = _dictation.State;
        OnStateChanged(_dictation.State);

        StartHotkeys();
        _settings.Changed += ApplySettings;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _log?.Info("Shutting down.");
        _hook?.Dispose();
        _tracker?.Dispose();
        _inserter?.Dispose();
        _sounds?.Dispose();
        _tray?.Dispose();
        _instance?.Dispose();
        base.OnExit(e);
    }

    private void CreateFloatingControl()
    {
        _floating = new FloatingControlWindow();
        _floating.Clicked += () => _ = _dictation!.ToggleAsync(_settings!.Current.Ui.PillOutput);
        _floating.OutputToggled += OnOutputToggled;
        _floating.PasteHeardClicked += () => _ = _dictation!.PasteLastHeardAsync();
        _floating.CopyHeardClicked += () =>
            _floating!.ShowMessage(_dictation!.CopyLastHeard() ? "Persian copied" : "There is nothing to copy");
        _floating.TranslateClicked += () => _ = _dictation!.ToggleAsync(DictationOutput.English);
        _floating.TopicNoteClicked += ShowTopicNote;
        _floating.ToneSelected += tone => _translation!.SetTone(tone);
        _floating.StartFreshClicked += () =>
        {
            _translation!.StartFresh();
            _floating!.ShowMessage("The next translation starts fresh");
        };
        _floating.CancelClicked += () => _ = _dictation!.CancelAsync();
        _floating.CopyClicked += OnCopyKeptText;
        _floating.PasteClicked += PasteLastDictation;
        _floating.RetryClicked += () => _ = _dictation!.RetryAsync();
        _floating.PrivacySettingsClicked += OpenPrivacySettings;
        _floating.PasswordOverrideClicked += () => _ = _dictation!.DictateIntoPasswordFieldAsync();
        _floating.PasswordDeclineClicked += () => _dictation!.ClearPasswordOffer();
        _floating.SettingsClicked += ShowSettings;
        _floating.MicrophonesRequested += () =>
            _floating!.ShowMicrophones(_devices!.List(), _settings!.Current.Microphone.DeviceId,
                _dictation!.HasRecoverableText, _dictation.HasHeard, _translation!.Tone, _translation.RecentTranslations);
        _floating.MicrophoneSelected += OnMicrophoneSelected;
        _floating.Moved += position =>
        {
            var settings = _settings!.Current;
            settings.Ui.FloatingControlPositions["default"] = position;
            _settings.Save(settings);
        };

        _settings!.Current.Ui.FloatingControlPositions.TryGetValue("default", out var saved);
        _floating.Show();
        _floating.PlaceAt(saved);
        _floating.Visibility = _settings.Current.Ui.FloatingControlEnabled ? Visibility.Visible : Visibility.Hidden;
    }

    /// <summary>The FA/EN chip: what a click on the pill produces from now on (architecture T14).</summary>
    private void OnOutputToggled()
    {
        var settings = _settings!.Current;
        settings.Ui.PillOutput = settings.Ui.PillOutput == DictationOutput.English
            ? DictationOutput.Transcript
            : DictationOutput.English;
        _settings.Save(settings);
        UpdateOutputChip();
    }

    private void UpdateOutputChip() =>
        _floating?.SetOutput(_settings!.Current.Ui.PillOutput, _dictation?.SessionOutput, _translation?.TopicNote);

    private void ShowTopicNote()
    {
        var existing = Current.Windows.OfType<TopicNoteWindow>().FirstOrDefault();
        if (existing is not null)
        {
            existing.Activate();
            return;
        }

        new TopicNoteWindow(_translation!).Show();
    }

    private void OnStateChanged(DictationState state)
    {
        var previous = _lastState;
        _lastState = state;

        _tray?.SetState(state);
        _floating?.SetState(state, HintFor(state));
        UpdateOutputChip();
        UpdateOffers();

        if (_hook is not null)
        {
            _hook.EscapeArmed = state == DictationState.Listening;
        }

        // Recording must always be visible, even with the floating control turned off (FR-6.3).
        if (state == DictationState.Listening && _settings?.Current.Ui.FloatingControlEnabled == false)
        {
            _indicator?.ShowListening();
        }
        else if (_indicator?.IsVisible == true)
        {
            _indicator.HideListening();
        }

        if (state == DictationState.Listening && previous != DictationState.Listening)
        {
            _sounds?.PlayStart();
        }
        else if (previous == DictationState.Listening && state == DictationState.Processing)
        {
            _sounds?.PlayStop();
        }
        else if ((state.IsError() || state is DictationState.NotInsertedTextKept or DictationState.BlockedPasswordField)
                 && state != previous)
        {
            _sounds?.PlayError();
        }
    }

    /// <summary>
    /// Stores the pick and confirms it on the control. The name is stored beside the id so a later
    /// build can say which device is missing by name rather than by a device string (FR-7.2).
    /// </summary>
    private void OnMicrophoneSelected(string? deviceId)
    {
        var settings = _settings!.Current;
        var chosen = _devices!.List().FirstOrDefault(d => d.Id == deviceId);

        settings.Microphone.DeviceId = deviceId;
        settings.Microphone.DeviceName = chosen?.Name;
        _settings.Save(settings);

        _floating?.ShowMessage(chosen is null ? "Microphone updated" : $"Microphone: {chosen.Name}");
    }

    private void OnCopyKeptText()
    {
        if (_dictation?.CopyLastDictation() == true)
        {
            // The offer has done its job; the text is on the clipboard now (FR-9.3).
            _floating?.HideRecovery();
            _floating?.ShowMessage("Copied - paste it wherever you want it");
        }
        else
        {
            _floating?.ShowMessage("There is nothing to copy");
        }
    }

    /// <summary>
    /// Pastes the latest dictation at the caret again: from the shortcut, the pill menu or the kept-text
    /// offer. It covers the silent loss, where the paste went to a window with no text field (FR-9.3).
    /// </summary>
    private void PasteLastDictation()
    {
        if (_dictation?.HasRecoverableText != true)
        {
            _floating?.ShowMessage("No dictation to paste yet");
            return;
        }

        _ = _dictation.PasteLastDictationAsync();
    }

    /// <summary>
    /// Retry shows while kept audio can be sent again and nothing is running (FR-9.1); the privacy
    /// page only when Windows is what blocks the microphone (FR-7.5).
    /// </summary>
    private void UpdateOffers()
    {
        if (_dictation is null)
        {
            return;
        }

        var state = _dictation.State;
        var retry = _dictation.CanRetry &&
                    state is not (DictationState.Listening or DictationState.Processing or DictationState.Busy);
        _floating?.SetRetryOffer(retry);
        _floating?.SetPrivacyOffer(state == DictationState.ErrorMicrophone &&
                                   _dictation.MicrophoneIssue == MicrophoneFailure.AccessDenied);
        _tray?.SetRetryAvailable(retry);
        _tray?.SetCopyLastAvailable(_dictation.HasRecoverableText);
    }

    /// <summary>
    /// The password-field confirmation (FR-3.7): the offer buttons plus the one-time warning about
    /// where the audio goes and what automatic formatting may do to a password.
    /// </summary>
    private void OnPasswordOverrideChanged()
    {
        if (_dictation is null || _floating is null)
        {
            return;
        }

        _floating.SetPasswordOffer(_dictation.CanOverridePassword);
        if (_dictation.CanOverridePassword)
        {
            _floating.ShowMessage(
                "This is a password field. Dictate anyway? The audio goes to Gemini, and on the free tier Google may use it. " +
                "Automatic formatting can change capitals, spacing and punctuation. This allows one dictation only.");
        }
    }

    private void OnNotice(DictationNotice notice)
    {
        var message = notice switch
        {
            DictationNotice.MicrophoneLostTranscribing => "Microphone disconnected - transcribing what was captured",
            DictationNotice.MicrophoneSilent =>
                $"No sound from {_devices?.Resolve(_settings?.Current.Microphone.DeviceId)?.Name ?? "the microphone"} - is it muted?",
            DictationNotice.PasswordTextDiscarded => "Not inserted - a password dictation is never kept",
            _ => null
        };

        if (notice == DictationNotice.CopiedToClipboard)
        {
            _floating?.ShowToast("Copied to clipboard");
            return;
        }

        if (message is not null)
        {
            _floating?.ShowMessage(message);
        }
    }

    private void OpenPrivacySettings()
    {
        try
        {
            Process.Start(new ProcessStartInfo(MicrophonePrivacy.SettingsUri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _log?.Warn($"Could not open the privacy settings: {ex.Message}");
            _floating?.ShowMessage("Open Settings > Privacy > Microphone in Windows");
        }
    }

    /// <summary>What to do next, in one line (FR-6.4).</summary>
    private string? HintFor(DictationState state) => state switch
    {
        DictationState.ErrorAuthentication => "Invalid API key - check it in Settings",
        DictationState.ErrorQuota => "Gemini quota reached - try again later",
        DictationState.ErrorNetwork => "No connection to Gemini - check your network",
        DictationState.ErrorMicrophone => _dictation?.MicrophoneIssue switch
        {
            MicrophoneFailure.AccessDenied => "Windows is blocking the microphone - allow it in Privacy settings",
            MicrophoneFailure.Disconnected => "Microphone disconnected - plug it back in or pick another",
            _ => "Microphone unavailable - check it is connected"
        },
        DictationState.ErrorTranscription => "Transcription failed - try again",
        DictationState.NotInsertedTextKept => "Not inserted - the text is kept, copy or paste it",
        DictationState.BlockedPasswordField => "Password field - confirm below to dictate once",
        DictationState.NoSpeech => "No speech detected",
        DictationState.Busy => "Still processing the last dictation",
        _ => null
    };

    private void OnLevelChanged(float level)
    {
        // Audio arrives every ~100 ms on a background thread; never queue more than one UI update.
        if (Interlocked.Exchange(ref _levelUpdatePending, 1) == 1)
        {
            return;
        }

        Dispatcher.BeginInvoke(() =>
        {
            Interlocked.Exchange(ref _levelUpdatePending, 0);
            if (_dictation?.State == DictationState.Listening)
            {
                _floating?.SetLevel(level);
            }
        });
    }

    private void StartHotkeys()
    {
        try
        {
            var hotkeys = _settings!.Current.Hotkeys;
            _hook = new HotkeyHook(ToHoldChord(hotkeys), ToChord(hotkeys), _log!)
            {
                PasteLast = ToPasteLastChord(hotkeys),
                TranslateHold = ToTranslateHoldChord(hotkeys),
                TranslateToggle = ToTranslateChord(hotkeys)
            };
        }
        catch (Exception ex)
        {
            _log!.Error("Hotkey unavailable.", ex);
            MessageBox.Show("The dictation shortcuts could not be registered. Use the floating control or the tray menu.",
                "LiveTranscribe", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // The hook thread must not wait on dictation work: post and return immediately.
        _hook.HoldPressed += () => Dispatcher.BeginInvoke(() => _dictation!.BeginHold());
        _hook.HoldReleased += () => Dispatcher.BeginInvoke(async () => await _dictation!.EndHoldAsync());
        _hook.TranslateHoldPressed += () => Dispatcher.BeginInvoke(() => _dictation!.BeginHold(DictationOutput.English));
        _hook.TranslateHoldReleased += () => Dispatcher.BeginInvoke(async () => await _dictation!.EndHoldAsync());
        _hook.TranslateTogglePressed += () => Dispatcher.BeginInvoke(async () => await _dictation!.ToggleAsync(DictationOutput.English));
        _hook.TogglePressed += () => Dispatcher.BeginInvoke(async () => await _dictation!.ToggleAsync());
        _hook.EscapePressed += () => Dispatcher.BeginInvoke(async () => await _dictation!.CancelAsync());
        _hook.PasteLastPressed += () => Dispatcher.BeginInvoke(PasteLastDictation);
    }

    private void ApplySettings(AppSettings settings)
    {
        if (_hook is not null)
        {
            _hook.Hold = ToHoldChord(settings.Hotkeys);
            _hook.Toggle = ToChord(settings.Hotkeys);
            _hook.PasteLast = ToPasteLastChord(settings.Hotkeys);
            _hook.TranslateHold = ToTranslateHoldChord(settings.Hotkeys);
            _hook.TranslateToggle = ToTranslateChord(settings.Hotkeys);
        }

        if (_sounds is not null)
        {
            _sounds.Enabled = settings.Ui.SoundsEnabled;
        }

        if (_floating is not null)
        {
            _floating.Visibility = settings.Ui.FloatingControlEnabled ? Visibility.Visible : Visibility.Hidden;
        }

        UpdateOutputChip();
        _dictation?.RefreshConfiguration();
    }

    private static ToggleChord ToHoldChord(HotkeySettings hotkeys) =>
        new(hotkeys.HoldVirtualKey, hotkeys.HoldRequiresCtrl, hotkeys.HoldRequiresAlt, hotkeys.HoldRequiresShift);

    private static ToggleChord ToTranslateHoldChord(HotkeySettings hotkeys) =>
        hotkeys.TranslateHoldVirtualKey == 0
            ? ToggleChord.None
            : new ToggleChord(hotkeys.TranslateHoldVirtualKey, hotkeys.TranslateHoldRequiresCtrl,
                hotkeys.TranslateHoldRequiresAlt, hotkeys.TranslateHoldRequiresShift);

    private static ToggleChord ToChord(HotkeySettings hotkeys) =>
        hotkeys.ToggleVirtualKey == 0
            ? ToggleChord.None
            : new ToggleChord(hotkeys.ToggleVirtualKey, hotkeys.ToggleRequiresCtrl, hotkeys.ToggleRequiresAlt, hotkeys.ToggleRequiresShift);

    private static ToggleChord ToTranslateChord(HotkeySettings hotkeys) =>
        hotkeys.TranslateToggleVirtualKey == 0
            ? ToggleChord.None
            : new ToggleChord(hotkeys.TranslateToggleVirtualKey, hotkeys.TranslateToggleRequiresCtrl,
                hotkeys.TranslateToggleRequiresAlt, hotkeys.TranslateToggleRequiresShift);

    private static ToggleChord ToPasteLastChord(HotkeySettings hotkeys) =>
        hotkeys.PasteLastVirtualKey == 0
            ? ToggleChord.None
            : new ToggleChord(hotkeys.PasteLastVirtualKey, hotkeys.PasteLastRequiresCtrl, hotkeys.PasteLastRequiresAlt, hotkeys.PasteLastRequiresShift);

    private void ShowSettings()
    {
        if (_settings is null || _secrets is null)
        {
            return;
        }

        var existing = Current.Windows.OfType<SettingsWindow>().FirstOrDefault();
        if (existing is not null)
        {
            existing.Activate();
            return;
        }

        new SettingsWindow(_settings, _secrets, _devices!, _engines!, _translators!, _log!, _hook).Show();
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _log?.Error("Unhandled UI exception.", e.Exception);
        e.Handled = true;
    }
}
