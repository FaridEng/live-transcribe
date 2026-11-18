using Android.AccessibilityServices;
using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.OS;
using Android.Runtime;
using Android.Views;
using Android.Views.InputMethods;
using Android.Widget;
using LiveTranscribe.Core.Abstractions;
using LiveTranscribe.Core.Dictation;
using LiveTranscribe.Core.Settings;
using LiveTranscribe.Core.Translation;

namespace LiveTranscribe.Droid.Overlay;

/// <summary>
/// The floating dictation button (the Android bubble design in next-steps, 2026-09-21). It docks at the
/// screen edge just above the keyboard and can be dragged up, down and to the other edge. Tap: start;
/// tap again or ✓: insert; ✕: cancel. Long press opens side options (Persian / English and recovery),
/// and sliding onto one before letting go picks it, like the AnyDesk radial menu.
/// It is an accessibility overlay that is never focusable, so the field keeps focus and the keyboard stays.
/// </summary>
public sealed class BubbleOverlay : Java.Lang.Object, View.IOnTouchListener
{
    private static readonly TimeSpan LongPressDelay = TimeSpan.FromMilliseconds(420);
    private static readonly TimeSpan MenuLifetime = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MessageLifetime = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan HeardLifetime = TimeSpan.FromSeconds(7);
    private static readonly TimeSpan ToastLifetime = TimeSpan.FromSeconds(1.8);

    private readonly AccessibilityService _service;
    private readonly AppHost _host;
    private readonly IWindowManager _windows;
    private readonly Handler _ui = new(Looper.MainLooper!);
    private readonly ISharedPreferences _prefs;
    private readonly float _dp;

    private readonly LinearLayout _root;
    private readonly TextView _message;
    private readonly LinearLayout _row;
    private readonly BubbleView _bubble;
    private readonly GlyphButton _accept;
    private readonly GlyphButton _cancel;
    private readonly LevelBars _bars;
    private readonly LinearLayout _menu;
    private readonly LinearLayout _menuMain;
    private readonly LinearLayout _menuTone;
    private readonly List<TextView> _chips = new();
    private readonly WindowManagerLayoutParams _params;

    // The topic note card, added as its own focusable overlay window only while open (§ OpenTopicNote).
    private View? _noteRoot;
    private WindowManagerLayoutParams? _noteParams;
    private EditText? _noteBox;
    private TextView? _noteHint;
    private Button? _noteMicButton;
    private readonly NoteRecorder _noteRecorder;

    private bool _dockRight;
    private int _userOffset;
    private int _anchor;
    private bool _fieldPresent;
    private bool _menuOpen;
    private int _messageVersion;
    private int _menuVersion;

    // Gesture state.
    private float _downX;
    private float _downY;
    private int _startX;
    private int _startY;
    private bool _dragging;
    private bool _longPressed;
    private TextView? _highlighted;
    private readonly Java.Lang.Runnable _longPress;

    public BubbleOverlay(AccessibilityService service, AppHost host)
    {
        _service = service;
        _host = host;
        _windows = service.GetSystemService(Context.WindowService).JavaCast<IWindowManager>()!;
        _prefs = service.GetSharedPreferences("bubble", FileCreationMode.Private)!;
        _dp = service.Resources!.DisplayMetrics!.Density;
        _dockRight = _prefs.GetBoolean("dock_right", true);
        _userOffset = _prefs.GetInt("offset", 0);
        _longPress = new Java.Lang.Runnable(OnLongPress);
        _noteRecorder = new NoteRecorder(host, service);

        _bubble = new BubbleView(service) { ContentDescription = "Dictate" };
        _bubble.SetOnTouchListener(this);
        _accept = new GlyphButton(service, check: true);
        _accept.Click += (_, _) => _ = _host.Dictation.StopAsync();
        _cancel = new GlyphButton(service, check: false);
        _cancel.Click += (_, _) => _ = _host.Dictation.CancelAsync();
        _bars = new LevelBars(service);

        // Two rows, one above the other: the usual chips, and - only while English output is on - the
        // tone chips, right there next to it rather than behind a second long-press (user request).
        _menu = new LinearLayout(service) { Orientation = Orientation.Vertical };
        _menuMain = new LinearLayout(service) { Orientation = Orientation.Horizontal };
        _menuTone = new LinearLayout(service) { Orientation = Orientation.Horizontal, Visibility = ViewStates.Gone };
        _menu.AddView(_menuMain);
        _menu.AddView(_menuTone, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { TopMargin = Px(4) });
        _row = new LinearLayout(service) { Orientation = Orientation.Horizontal };
        _row.SetGravity(GravityFlags.CenterVertical);

        _message = new TextView(service)
        {
            Visibility = ViewStates.Gone,
            TextDirection = TextDirection.FirstStrong
        };
        _message.SetTextColor(Color.White);
        _message.SetTextSize(Android.Util.ComplexUnitType.Sp, 14);
        _message.SetPadding(Px(12), Px(8), Px(12), Px(8));
        _message.Background = Rounded(Color.ParseColor("#E6212121"), 12);

        _root = new LinearLayout(service) { Orientation = Orientation.Vertical, Visibility = ViewStates.Gone };
        _root.AddView(_message, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent)
        {
            BottomMargin = Px(6)
        });
        _root.AddView(_row);
        BuildRow();

        _params = new WindowManagerLayoutParams(
            ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent,
            WindowManagerTypes.AccessibilityOverlay,
            WindowManagerFlags.NotFocusable | WindowManagerFlags.NotTouchModal,
            Format.Translucent)
        {
            Gravity = GravityFlags.Bottom | (_dockRight ? GravityFlags.Right : GravityFlags.Left),
            X = Px(4)
        };
        _windows.AddView(_root, _params);

        _host.Dictation.StateChanged += OnStateChanged;
        _host.Dictation.LevelChanged += OnLevel;
        _host.Dictation.Notice += OnNotice;
        _host.Dictation.TranslationHeard += OnHeard;
        _host.Dictation.EngineSwitched += OnEngineSwitched;
        _host.Settings.Changed += OnSettingsChanged;
        Refresh();
    }

    private DictationController Dictation => _host.Dictation;

    private bool English => _host.Settings.Current.Ui.PillOutput == DictationOutput.English;

    /// <summary>
    /// Called by the service on focus and window changes. <paramref name="keyboardTop"/> is the top of the
    /// keyboard in screen coordinates, or null when no keyboard is showing.
    /// </summary>
    public void Update(bool fieldPresent, int? keyboardTop)
    {
        _fieldPresent = fieldPresent;
        var height = ScreenSize().Y;
        _anchor = keyboardTop is { } top && top < height ? height - top : height / 4;
        Place();
        Refresh();
    }

    public void Remove()
    {
        _host.Dictation.StateChanged -= OnStateChanged;
        _host.Dictation.LevelChanged -= OnLevel;
        _host.Dictation.Notice -= OnNotice;
        _host.Dictation.TranslationHeard -= OnHeard;
        _host.Dictation.EngineSwitched -= OnEngineSwitched;
        _host.Settings.Changed -= OnSettingsChanged;
        _ui.RemoveCallbacksAndMessages(null);
        CloseTopicNote();
        try
        {
            _windows.RemoveView(_root);
        }
        catch (Exception)
        {
            // Already gone with the service.
        }
    }

    public bool OnTouch(View? v, MotionEvent? e)
    {
        if (e is null)
        {
            return false;
        }

        switch (e.ActionMasked)
        {
            case MotionEventActions.Down:
                _downX = e.RawX;
                _downY = e.RawY;
                _startX = _params.X;
                _startY = _params.Y;
                _dragging = false;
                _longPressed = false;
                _ui.PostDelayed(_longPress, (long)LongPressDelay.TotalMilliseconds);
                return true;

            case MotionEventActions.Move:
                var dx = e.RawX - _downX;
                var dy = e.RawY - _downY;
                if (_longPressed)
                {
                    Highlight(ChipAt(e.RawX, e.RawY));
                }
                else if (_dragging || Math.Sqrt(dx * dx + dy * dy) > ViewConfiguration.Get(_service)!.ScaledTouchSlop * 1.5)
                {
                    _dragging = true;
                    _ui.RemoveCallbacks(_longPress);
                    _params.Y = ClampY(_startY - (int)dy);
                    _params.X = Math.Max(0, _startX + (int)(_dockRight ? -dx : dx));
                    _windows.UpdateViewLayout(_root, _params);
                }

                return true;

            case MotionEventActions.Up:
                _ui.RemoveCallbacks(_longPress);
                if (_dragging)
                {
                    FinishDrag(e.RawX);
                }
                else if (_longPressed)
                {
                    if (ChipAt(e.RawX, e.RawY) is { } chip)
                    {
                        chip.CallOnClick();
                    }

                    Highlight(null);
                }
                else
                {
                    OnTap();
                }

                return true;

            case MotionEventActions.Cancel:
                _ui.RemoveCallbacks(_longPress);
                Highlight(null);
                return true;
        }

        return false;
    }

    private void OnTap()
    {
        if (_noteRoot is not null)
        {
            CloseTopicNote();
            return;
        }

        if (_menuOpen)
        {
            CloseMenu();
            return;
        }

        switch (Dictation.State)
        {
            case DictationState.Listening:
                _ = Dictation.StopAsync();
                break;
            case DictationState.Processing:
            case DictationState.Busy:
            case DictationState.Paused:
                break;
            default:
                // Start the microphone service first: Android silences the microphone of an app that
                // has nothing in the foreground.
                RecordingService.Start(_service);
                _ = Dictation.ToggleAsync(_host.Settings.Current.Ui.PillOutput);
                break;
        }
    }

    private void OnLongPress()
    {
        if (Dictation.State is DictationState.Listening or DictationState.Processing)
        {
            return;
        }

        _longPressed = true;
        _bubble.PerformHapticFeedback(FeedbackConstants.LongPress);
        OpenMenu();
    }

    private void OpenMenu()
    {
        _menuMain.RemoveAllViews();
        _menuTone.RemoveAllViews();
        _chips.Clear();

        AddChip("فارسی", !English, () => SetOutput(DictationOutput.Transcript));
        AddChip("English", English, () => SetOutput(DictationOutput.English));
        AddChip($"Topic note{(_host.Translation.TopicNote is not null ? " •" : "")}", false, OpenTopicNote);
        if (Dictation.CanRetry)
        {
            AddChip("Retry", false, () =>
            {
                RecordingService.Stop(_service);
                _ = Dictation.RetryAsync();
            });
        }

        if (Dictation.HasRecoverableText)
        {
            AddChip("Paste last", false, () => _ = Dictation.PasteLastDictationAsync());
        }

        if (Dictation.HasHeard)
        {
            AddChip("Paste Persian", false, () => _ = Dictation.PasteLastHeardAsync());
        }

        // Right next to English rather than behind a second long-press, since it only means anything there.
        if (English)
        {
            var tone = _host.Translation.Tone;
            AddChip("As spoken", tone == TranslationTone.AsSpoken, () => SetTone(TranslationTone.AsSpoken), _menuTone);
            AddChip("Casual", tone == TranslationTone.Casual, () => SetTone(TranslationTone.Casual), _menuTone);
            AddChip("Formal", tone == TranslationTone.Formal, () => SetTone(TranslationTone.Formal), _menuTone);
            _menuTone.Visibility = ViewStates.Visible;
        }
        else
        {
            _menuTone.Visibility = ViewStates.Gone;
        }

        _menuOpen = true;
        var version = ++_menuVersion;
        _ui.PostDelayed(() =>
        {
            if (version == _menuVersion)
            {
                CloseMenu();
            }
        }, (long)MenuLifetime.TotalMilliseconds);
        Refresh();
    }

    private void CloseMenu()
    {
        _menuOpen = false;
        _menuVersion++;
        Refresh();
    }

    private void AddChip(string label, bool selected, Action action, LinearLayout? row = null)
    {
        var chip = new TextView(_service) { Text = label, Gravity = GravityFlags.Center };
        chip.SetTextColor(selected ? Color.White : Color.ParseColor("#212121"));
        chip.SetTextSize(Android.Util.ComplexUnitType.Sp, 14);
        chip.SetPadding(Px(14), Px(10), Px(14), Px(10));
        chip.Tag = selected ? "selected" : null;
        chip.Background = Rounded(selected ? Color.ParseColor("#1565C0") : Color.ParseColor("#F5F5F5"), 20);
        chip.Click += (_, _) =>
        {
            CloseMenu();
            action();
        };
        (row ?? _menuMain).AddView(chip, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent)
        {
            LeftMargin = Px(3),
            RightMargin = Px(3)
        });
        _chips.Add(chip);
    }

    /// <summary>
    /// Opens the topic note as a small focusable card over whatever app is behind it - not an Activity, so
    /// there is no task switch: the app the user was dictating into stays exactly where it was (user
    /// report, 2026-09-25: launching a separate screen felt like "leaving the app"). It reuses the same
    /// accessibility-overlay window type as the bubble itself, just without <c>NotFocusable</c>, so it can
    /// take input focus and show the keyboard like any other window.
    /// </summary>
    private void OpenTopicNote()
    {
        CloseTopicNote();

        var card = new LinearLayout(_service) { Orientation = Orientation.Vertical };
        card.SetPadding(Px(16), Px(14), Px(16), Px(14));
        card.Background = Rounded(Color.ParseColor("#FAFAFA"), 16);

        var heading = new TextView(_service) { Text = "What are you writing about?" };
        heading.SetTypeface(null, Android.Graphics.TypefaceStyle.Bold);
        heading.SetTextColor(Color.Black);
        card.AddView(heading);

        var hint = new TextView(_service)
        {
            Text = "Used for word choice and tone in translations. Clears itself after an hour unused."
        };
        hint.SetTextColor(Color.ParseColor("#666666"));
        hint.SetTextSize(Android.Util.ComplexUnitType.Sp, 12);
        hint.SetPadding(0, Px(4), 0, Px(8));
        card.AddView(hint);
        _noteHint = hint;

        var inputRow = new LinearLayout(_service) { Orientation = Orientation.Horizontal };
        inputRow.SetGravity(GravityFlags.CenterVertical);
        _noteBox = new EditText(_service)
        {
            Text = _host.Translation.TopicNote ?? "",
            TextDirection = TextDirection.FirstStrong
        };
        inputRow.AddView(_noteBox, new LinearLayout.LayoutParams(Px(220), ViewGroup.LayoutParams.WrapContent));

        // A dedicated mic, separate from the bubble's own recording (user's choice, 2026-09-28, "Option
        // B"): it appends recognized text straight into this box in code, so it works regardless of
        // whether Android reports focus for this overlay window the way it does for a normal app field.
        _noteMicButton = new Button(_service) { Text = "🎤" };
        _noteMicButton.Click += (_, _) => _ = ToggleNoteRecordingAsync();
        inputRow.AddView(_noteMicButton, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { LeftMargin = Px(6) });
        card.AddView(inputRow);

        var buttons = new LinearLayout(_service) { Orientation = Orientation.Horizontal };
        buttons.SetGravity(GravityFlags.Right);
        buttons.SetPadding(0, Px(10), 0, 0);
        AddNoteButton(buttons, "Save", () =>
        {
            _host.Translation.SetTopicNote(_noteBox!.Text);
            CloseTopicNote();
        });
        AddNoteButton(buttons, "Clear", () =>
        {
            _host.Translation.SetTopicNote(null);
            CloseTopicNote();
        });
        AddNoteButton(buttons, "Cancel", CloseTopicNote);
        card.AddView(buttons);

        _noteRoot = card;
        _noteParams = new WindowManagerLayoutParams(
            Px(292), ViewGroup.LayoutParams.WrapContent,
            WindowManagerTypes.AccessibilityOverlay,
            WindowManagerFlags.NotTouchModal, // no NotFocusable: this one window takes input focus and the keyboard
            Format.Translucent)
        {
            Gravity = GravityFlags.Center,
            SoftInputMode = SoftInput.StateVisible
        };
        _windows.AddView(card, _noteParams);
        _noteBox.RequestFocus();
        ((InputMethodManager)_service.GetSystemService(Context.InputMethodService)!).ShowSoftInput(_noteBox, ShowFlags.Implicit);
    }

    private void CloseTopicNote()
    {
        if (_noteRoot is null)
        {
            return;
        }

        _noteRecorder.Cancel();
        ((InputMethodManager)_service.GetSystemService(Context.InputMethodService)!).HideSoftInputFromWindow(_noteRoot.WindowToken, HideSoftInputFlags.None);
        try
        {
            _windows.RemoveView(_noteRoot);
        }
        catch (Exception)
        {
            // Already gone.
        }

        _noteRoot = null;
        _noteParams = null;
        _noteBox = null;
        _noteHint = null;
        _noteMicButton = null;
    }

    private void AddNoteButton(LinearLayout row, string text, Action action)
    {
        var button = new Button(_service) { Text = text };
        button.Click += (_, _) => action();
        row.AddView(button, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { LeftMargin = Px(6) });
    }

    /// <summary>Starts or stops the note's own mic session and appends whatever it recognized (§ NoteRecorder).</summary>
    private async Task ToggleNoteRecordingAsync()
    {
        if (_noteMicButton is null || _noteBox is null)
        {
            return;
        }

        if (_noteRecorder.IsRecording)
        {
            _noteMicButton.Text = "🎤";
            var text = await _noteRecorder.StopAsync();
            if (_noteBox is not null && text is { Length: > 0 })
            {
                AppendToNote(text);
            }

            return;
        }

        if (Dictation.State is DictationState.Listening or DictationState.Processing or DictationState.Busy)
        {
            // The bubble's own dictation already owns the microphone.
            return;
        }

        _noteMicButton.Text = "⏹";
        var failure = await _noteRecorder.StartAsync();
        if (_noteBox is null)
        {
            // The note was closed while the engine session was starting.
            _noteRecorder.Cancel();
            return;
        }

        if (failure is not null)
        {
            _noteMicButton.Text = "🎤";
            ShowNoteHint(failure);
        }
    }

    private void AppendToNote(string text)
    {
        if (_noteBox is null)
        {
            return;
        }

        var current = _noteBox.Text ?? "";
        var start = _noteBox.SelectionStart;
        if (start < 0 || start > current.Length)
        {
            start = current.Length;
        }

        var insert = start > 0 && !char.IsWhiteSpace(current[start - 1]) ? " " + text : text;
        _noteBox.Text = current[..start] + insert + current[start..];
        _noteBox.SetSelection(start + insert.Length);
    }

    /// <summary>Shows a failure where the user is already looking, instead of the bubble's own toast far away.</summary>
    private void ShowNoteHint(string message)
    {
        if (_noteHint is null)
        {
            return;
        }

        var hint = _noteHint;
        var original = hint.Text;
        hint.Text = message;
        hint.SetTextColor(Color.ParseColor("#C62828"));
        _ui.PostDelayed(() =>
        {
            if (ReferenceEquals(_noteHint, hint))
            {
                hint.Text = original;
                hint.SetTextColor(Color.ParseColor("#666666"));
            }
        }, (long)MessageLifetime.TotalMilliseconds);
    }

    private void SetTone(TranslationTone tone)
    {
        if (_host.Translation.Tone == tone)
        {
            return;
        }

        _host.Translation.SetTone(tone);
        ShowMessage(tone switch
        {
            TranslationTone.Casual => "Tone: casual",
            TranslationTone.Formal => "Tone: formal",
            _ => "Tone: as spoken"
        }, MessageLifetime);
    }

    private TextView? ChipAt(float rawX, float rawY)
    {
        var location = new int[2];
        foreach (var chip in _chips)
        {
            chip.GetLocationOnScreen(location);
            if (rawX >= location[0] && rawX <= location[0] + chip.Width &&
                rawY >= location[1] - Px(24) && rawY <= location[1] + chip.Height + Px(24))
            {
                return chip;
            }
        }

        return null;
    }

    private void Highlight(TextView? chip)
    {
        if (ReferenceEquals(chip, _highlighted))
        {
            return;
        }

        if (_highlighted is { } old)
        {
            var selected = (string?)old.Tag == "selected";
            old.Background = Rounded(selected ? Color.ParseColor("#1565C0") : Color.ParseColor("#F5F5F5"), 20);
        }

        _highlighted = chip;
        if (chip is not null)
        {
            chip.Background = Rounded(Color.ParseColor("#FFB300"), 20);
            chip.PerformHapticFeedback(FeedbackConstants.ClockTick);
        }
    }

    private void SetOutput(DictationOutput output)
    {
        var settings = _host.Settings.Current;
        if (settings.Ui.PillOutput == output)
        {
            return;
        }

        settings.Ui.PillOutput = output;
        _host.Settings.Save(settings);
        ShowMessage(output == DictationOutput.English ? "Translate to English" : "Persian dictation", MessageLifetime);
    }

    private void FinishDrag(float rawX)
    {
        var dockRight = rawX > ScreenSize().X / 2f;
        if (dockRight != _dockRight)
        {
            _dockRight = dockRight;
            _params.Gravity = GravityFlags.Bottom | (_dockRight ? GravityFlags.Right : GravityFlags.Left);
            BuildRow();
        }

        _params.X = Px(4);
        _userOffset = _params.Y - _anchor - Px(8);
        _prefs.Edit()!.PutBoolean("dock_right", _dockRight)!.PutInt("offset", _userOffset)!.Apply();
        _windows.UpdateViewLayout(_root, _params);
    }

    private void Place()
    {
        _params.Y = ClampY(_anchor + Px(8) + _userOffset);
        if (_root.IsAttachedToWindow)
        {
            _windows.UpdateViewLayout(_root, _params);
        }
    }

    private int ClampY(int y) => Math.Clamp(y, Px(8), ScreenSize().Y - Px(160));

    /// <summary>
    /// Children run from the screen edge inwards. At rest: the bubble, then the options. While listening the
    /// bubble gives way to ✓ at the edge, where the thumb already is, then the level bars, then ✕.
    /// </summary>
    private void BuildRow()
    {
        _row.RemoveAllViews();
        _row.SetMinimumHeight(Px(58));
        var inwards = new View[] { _bubble, _accept, _bars, _cancel, _menu };
        foreach (var view in _dockRight ? Enumerable.Reverse(inwards) : inwards)
        {
            _row.AddView(view, view switch
            {
                _ when view == _bubble => new LinearLayout.LayoutParams(Px(58), Px(58)),
                _ when view == _accept => new LinearLayout.LayoutParams(Px(52), Px(52)) { LeftMargin = Px(3), RightMargin = Px(3) },
                _ when view == _bars => new LinearLayout.LayoutParams(Px(76), Px(40)) { LeftMargin = Px(3), RightMargin = Px(3) },
                _ when view == _cancel => new LinearLayout.LayoutParams(Px(44), Px(44)) { LeftMargin = Px(3), RightMargin = Px(3) },
                _ => new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent)
            });
        }

        _root.SetGravity(_dockRight ? GravityFlags.Right : GravityFlags.Left);
    }

    private void Refresh()
    {
        var state = Dictation.State;
        var listening = state == DictationState.Listening;
        var english = Dictation.SessionOutput is { } output ? output == DictationOutput.English : English;
        _bubble.Show(state switch
        {
            DictationState.Processing or DictationState.Busy => BubbleLook.Processing,
            _ when state.IsError() || state is DictationState.NotConfigured or DictationState.NotInsertedTextKept => BubbleLook.Error,
            _ => BubbleLook.Idle
        }, english);

        if (listening && _bars.Visibility != ViewStates.Visible)
        {
            _bars.Reset();
        }

        _bars.English = english;
        _bubble.Visibility = listening ? ViewStates.Gone : ViewStates.Visible;
        _accept.Visibility = listening ? ViewStates.Visible : ViewStates.Gone;
        _bars.Visibility = listening ? ViewStates.Visible : ViewStates.Gone;
        _cancel.Visibility = listening ? ViewStates.Visible : ViewStates.Gone;
        _menu.Visibility = _menuOpen ? ViewStates.Visible : ViewStates.Gone;

        // Nothing shows without a field, except while dictation or its outcome still needs the user.
        var busy = state is DictationState.Listening or DictationState.Processing or DictationState.Busy;
        var visible = _fieldPresent || busy || _menuOpen || _message.Visibility == ViewStates.Visible;
        _root.Visibility = visible ? ViewStates.Visible : ViewStates.Gone;
    }

    private void OnStateChanged(DictationState state)
    {
        if (state != DictationState.Listening)
        {
            RecordingService.Stop(_service);
        }

        var text = state switch
        {
            DictationState.NotConfigured => "Open LiveTranscribe and add your Gemini API key.",
            DictationState.NoSpeech => "No speech detected.",
            DictationState.Busy => "Busy - still processing the last dictation.",
            DictationState.BlockedPasswordField => "Not inserted: password field.",
            DictationState.NotInsertedTextKept => "Not inserted - text kept. Long-press → Paste last.",
            DictationState.ErrorMicrophone => Dictation.MicrophoneIssue == MicrophoneFailure.AccessDenied
                ? "Microphone permission is off. Open LiveTranscribe to allow it."
                : "The microphone could not be used.",
            DictationState.ErrorNetwork => "Connection error." + RetryHint(),
            DictationState.ErrorAuthentication => "Invalid API key.",
            DictationState.ErrorQuota => "Quota exceeded." + RetryHint(),
            DictationState.ErrorTranscription => "Transcription error." + RetryHint(),
            _ => null
        };

        if (text is not null)
        {
            ShowMessage(text, MessageLifetime);
        }
        else if (state == DictationState.Listening)
        {
            HideMessage();
        }

        Refresh();
    }

    private string RetryHint() => Dictation.CanRetry ? " Long-press → Retry." : "";

    private void OnLevel(float level) => _ui.Post(() => _bars.SetLevel(level));

    private void OnNotice(DictationNotice notice)
    {
        if (notice == DictationNotice.CopiedToClipboard)
        {
            // Short, and it fades out on its own (T13).
            ShowMessage("Copied to clipboard", ToastLifetime, fade: true);
            return;
        }

        var text = notice switch
        {
            DictationNotice.MicrophoneSilent => "No sound from the microphone.",
            DictationNotice.MicrophoneLostTranscribing => "Microphone lost - transcribing what was captured.",
            _ => null
        };
        if (text is not null)
        {
            ShowMessage(text, MessageLifetime);
        }
    }

    // On screen only, never logged (FR-11.5).
    private void OnHeard(string heard) => ShowMessage("شنیدم: " + heard, HeardLifetime);

    private void OnEngineSwitched(string message) => ShowMessage(message, MessageLifetime);

    private void OnSettingsChanged(AppSettings settings) => _ui.Post(Refresh);

    private void ShowMessage(string text, TimeSpan lifetime, bool fade = false)
    {
        // A newer message cancels a fade still running.
        _message.Animate()!.Cancel();
        _message.Alpha = 1;
        _message.Text = text;
        _message.SetMaxWidth((int)(ScreenSize().X * 0.8));
        _message.Visibility = ViewStates.Visible;
        var version = ++_messageVersion;
        _ui.PostDelayed(() =>
        {
            if (version != _messageVersion)
            {
                return;
            }

            if (!fade)
            {
                HideMessage();
                return;
            }

            _message.Animate()!.Alpha(0)!.SetDuration(400)!.WithEndAction(new Java.Lang.Runnable(() =>
            {
                if (version == _messageVersion)
                {
                    HideMessage();
                }
            }));
        }, (long)lifetime.TotalMilliseconds);
        Refresh();
    }

    private void HideMessage()
    {
        _messageVersion++;
        _message.Animate()!.Cancel();
        _message.Alpha = 1;
        _message.Visibility = ViewStates.Gone;
        Refresh();
    }

    private Point ScreenSize()
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(30))
        {
            var bounds = _windows.CurrentWindowMetrics.Bounds;
            return new Point(bounds.Width(), bounds.Height());
        }

        // Same coordinates as the keyboard's screen bounds: the full display, bars included.
        var size = new Point();
#pragma warning disable CA1422 // The replacement above is used wherever it exists.
        _windows.DefaultDisplay!.GetRealSize(size);
#pragma warning restore CA1422
        return size;
    }

    private int Px(float dp) => (int)(dp * _dp + 0.5f);

    private GradientDrawable Rounded(Color color, float radiusDp)
    {
        var drawable = new GradientDrawable();
        drawable.SetColor(color);
        drawable.SetCornerRadius(radiusDp * _dp);
        return drawable;
    }
}
