using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using LiveTranscribe.App.Interop;
using LiveTranscribe.Core.Abstractions;
using LiveTranscribe.Core.Dictation;
using LiveTranscribe.Core.Settings;
using LiveTranscribe.Core.Translation;
using Brush = System.Windows.Media.Brush;
using Cursors = System.Windows.Input.Cursors;
using FontFamily = System.Windows.Media.FontFamily;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;
using Rectangle = System.Windows.Shapes.Rectangle;
using Screen = System.Windows.Forms.Screen;

namespace LiveTranscribe.App.FloatingControl;

/// <summary>
/// The small always-on-top control (FR-2.8 to FR-2.12). It never takes focus, shows the state with
/// a distinct shape per state (FR-6.2), shows state details and warnings above itself, and can be dragged.
/// It is a view only: the app wires its events to the dictation controller.
/// </summary>
public partial class FloatingControlWindow : Window
{
    private const double DragThreshold = 4;

    /// <summary>Height of the transparent band on each side of the pill, as in the XAML.</summary>
    private const double FlyoutBand = 200;
    private const double PillHeight = 36;

    private static readonly Brush White = Freeze(new SolidColorBrush(Colors.White));
    private static readonly Brush Red = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0x5A, 0x5A)));
    private static readonly Brush Green = Freeze(new SolidColorBrush(Color.FromRgb(0x5C, 0xD6, 0x8A)));
    private static readonly Brush Amber = Freeze(new SolidColorBrush(Color.FromRgb(0xF2, 0xB1, 0x3D)));
    private static readonly Brush Grey = Freeze(new SolidColorBrush(Color.FromRgb(0xA0, 0xA0, 0xA8)));
    private static readonly Brush Blue = Freeze(new SolidColorBrush(Color.FromRgb(0x3D, 0x7B, 0xF2)));
    private static readonly Brush ChipIdle = Freeze(new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)));

    private static readonly Geometry Microphone = Geometry.Parse(
        "M12,3 A3,3 0 0 1 15,6 V11 A3,3 0 0 1 9,11 V6 A3,3 0 0 1 12,3 Z M18.5,11 A6.5,6.5 0 0 1 5.5,11 M12,17.5 V21 M8.5,21 H15.5");
    private static readonly Geometry Dot = Geometry.Parse("M6,12 A6,6 0 1 1 18,12 A6,6 0 1 1 6,12 Z");
    private static readonly Geometry Arc = Geometry.Parse("M12,3 A9,9 0 1 1 3,12");
    private static readonly Geometry Check = Geometry.Parse("M5,12.5 L10,17.5 L19,7");
    private static readonly Geometry Dash = Geometry.Parse("M6,12 H18");
    private static readonly Geometry Warning = Geometry.Parse("M12,3 L22,20 H2 Z M12,9.5 V13.5 M12,16.8 V17");
    private static readonly Geometry PauseBars = Geometry.Parse("M9,6 V18 M15,6 V18");

    private readonly Rectangle[] _bars;
    private readonly DispatcherTimer _messageTimer = new() { Interval = TimeSpan.FromSeconds(4) };

    private DictationState _state = DictationState.NotConfigured;
    private string? _message;
    private bool _hovering;

    private Point? _pressScreen;
    private Point _pressWindow;
    private bool _dragging;
    private bool _cancelPressed;
    private object? _recoveryPressed;
    private object? _microphonePressed;
    private Point? _anchor;

    private bool _keptTextOffer;
    private bool _retryOffer;
    private bool _privacyOffer;
    private bool _passwordOffer;
    private bool _flyoutsBelow;

    private DictationOutput _pillOutput;
    private DictationOutput? _sessionOutput;
    private string? _topicNote;
    private bool _chipPressed;
    private bool _holdMessageOnHover;
    private bool _fadeMessage;

    public FloatingControlWindow()
    {
        InitializeComponent();
        NoActivateWindow.Apply(this);

        _bars = Enumerable.Range(0, 5).Select(_ => new Rectangle
        {
            Width = 3,
            Height = 3,
            RadiusX = 1.5,
            RadiusY = 1.5,
            Margin = new Thickness(1, 0, 1, 0),
            Fill = White,
            VerticalAlignment = VerticalAlignment.Center
        }).ToArray();
        foreach (var bar in _bars)
        {
            LevelBars.Children.Add(bar);
        }

        _messageTimer.Tick += (_, _) =>
        {
            if (_holdMessageOnHover && _hovering)
            {
                return;   // Still being read; the next tick after the mouse leaves clears it.
            }

            _messageTimer.Stop();
            if (_fadeMessage)
            {
                FadeOutMessage();
                return;
            }

            _message = null;
            UpdateBubble();
        };

        SetState(DictationState.NotConfigured, hint: null);
    }

    /// <summary>Click on the control: start or stop dictation.</summary>
    public event Action? Clicked;

    public event Action? CancelClicked;

    /// <summary>Recovery offer: put the kept dictation on the clipboard (FR-9.3).</summary>
    public event Action? CopyClicked;

    /// <summary>Recovery offer: insert the kept dictation where the caret is now (FR-9.3).</summary>
    public event Action? PasteClicked;

    /// <summary>Recovery offer: send the kept audio of a failed dictation again (FR-9.1).</summary>
    public event Action? RetryClicked;

    /// <summary>Recovery offer: open the Windows microphone privacy page (FR-7.5).</summary>
    public event Action? PrivacySettingsClicked;

    /// <summary>The user confirmed one dictation into a password field (FR-3.7).</summary>
    public event Action? PasswordOverrideClicked;

    /// <summary>The user turned down the password field offer (FR-3.7).</summary>
    public event Action? PasswordDeclineClicked;

    /// <summary>The user right-clicked the pill: the app answers with <see cref="ShowMicrophones"/>.</summary>
    public event Action? MicrophonesRequested;

    /// <summary>A microphone was picked from the list. Null is "System default" (FR-7.1).</summary>
    public event Action<string?>? MicrophoneSelected;

    /// <summary>Bottom-centre of the control after a drag, in device-independent pixels.</summary>
    public event Action<ControlPosition>? Moved;

    /// <summary>Places the control by its bottom-centre point, or at the default spot when none is stored.</summary>
    public void PlaceAt(ControlPosition? position)
    {
        var workArea = SystemParameters.WorkArea;
        var anchor = position is not null && IsOnScreen(position)
            ? new Point(position.X, position.Y)
            : new Point(workArea.Left + workArea.Width / 2, workArea.Bottom - 24);

        _anchor = anchor;
        ApplyAnchor();
    }

    public void SetState(DictationState state, string? hint)
    {
        _state = state;

        StopAnimations();
        Glyph.Fill = null;
        Glyph.Stroke = White;
        LevelBars.Visibility = Visibility.Collapsed;
        CancelButton.Visibility = Visibility.Collapsed;

        switch (state)
        {
            case DictationState.Listening:
                Glyph.Data = Dot;
                Glyph.Fill = Red;
                Glyph.Stroke = null;
                LevelBars.Visibility = Visibility.Visible;
                CancelButton.Visibility = Visibility.Visible;
                StartPulse();
                break;
            case DictationState.Processing:
            case DictationState.Busy:
                Glyph.Data = Arc;
                Glyph.Stroke = state == DictationState.Busy ? Amber : White;
                StartSpin();
                break;
            case DictationState.Inserted:
                Glyph.Data = Check;
                Glyph.Stroke = Green;
                break;
            case DictationState.Cancelled:
            case DictationState.NoSpeech:
                Glyph.Data = Dash;
                Glyph.Stroke = Grey;
                break;
            case DictationState.Paused:
                Glyph.Data = PauseBars;
                Glyph.Stroke = Grey;
                break;
            case DictationState.NotConfigured:
                Glyph.Data = Warning;
                Glyph.Stroke = Grey;
                break;
            case DictationState.NotInsertedTextKept:
            case DictationState.BlockedPasswordField:
                Glyph.Data = Warning;
                Glyph.Stroke = Amber;
                break;
            default:
                if (state.IsError())
                {
                    Glyph.Data = Warning;
                    Glyph.Stroke = Amber;
                }
                else
                {
                    Glyph.Data = Microphone;
                }

                break;
        }

        if (state != DictationState.Listening)
        {
            SetLevel(0);
        }

        // The kept text stays one click away until it lands somewhere (FR-9.3).
        _keptTextOffer = state == DictationState.NotInsertedTextKept;
        UpdateOffers();

        if (state is DictationState.Listening or DictationState.Processing)
        {
            HideMicrophones();
        }

        // Problems are announced once, without needing a hover (FR-6.4).
        if (state.IsError() || state is DictationState.NotInsertedTextKept or DictationState.NoSpeech
            or DictationState.Busy or DictationState.BlockedPasswordField)
        {
            ShowMessage(hint ?? state.ToDisplayText());
        }
        else if (state is DictationState.Listening or DictationState.Processing)
        {
            _message = null;
            _messageTimer.Stop();
            Bubble.BeginAnimation(OpacityProperty, null);
            Bubble.Opacity = 1;
        }

        UpdateBubble();
    }

    public void SetLevel(float level)
    {
        // Speech RMS rarely exceeds 0.3; scale so normal speech fills most of the meter.
        var scaled = Math.Clamp(Math.Sqrt(level / 0.25), 0, 1);
        for (var i = 0; i < _bars.Length; i++)
        {
            var weight = 1 - Math.Abs(i - 2) * 0.22;
            _bars[i].Height = 3 + 15 * scaled * weight;
        }
    }

    public void ShowMessage(string message) => ShowMessage(message, TimeSpan.FromSeconds(4), holdOnHover: false);

    /// <summary>
    /// After a translation: the Persian the engine heard, so a mishearing shows as a mishearing and not as a
    /// bad translation (T14). Longer than a notice, and it stays while the pointer is on the pill.
    /// </summary>
    public void ShowHeard(string heard) => ShowMessage($"شنیدم: {heard}", TimeSpan.FromSeconds(7), holdOnHover: true);

    /// <summary>A short confirmation that fades away on its own, such as "Copied to clipboard" (T13).</summary>
    public void ShowToast(string message) => ShowMessage(message, TimeSpan.FromSeconds(1.8), holdOnHover: false, fade: true);

    private void ShowMessage(string message, TimeSpan duration, bool holdOnHover, bool fade = false)
    {
        // A newer message cancels a fade still running.
        Bubble.BeginAnimation(OpacityProperty, null);
        Bubble.Opacity = 1;
        _message = message;
        _holdMessageOnHover = holdOnHover;
        _fadeMessage = fade;
        _messageTimer.Stop();
        _messageTimer.Interval = duration;
        _messageTimer.Start();
        UpdateBubble();
    }

    private void FadeOutMessage()
    {
        var shown = _message;
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(400));
        fade.Completed += (_, _) =>
        {
            if (!ReferenceEquals(_message, shown))
            {
                return;   // Replaced while fading; ShowMessage already reset the opacity.
            }

            _message = null;
            Bubble.BeginAnimation(OpacityProperty, null);
            Bubble.Opacity = 1;
            UpdateBubble();
        };
        Bubble.BeginAnimation(OpacityProperty, fade);
    }

    private void UpdateBubble()
    {
        string? text;
        if (_message is not null)
        {
            text = _message;
        }
        else if (_hovering)
        {
            text = _state switch
            {
                DictationState.Ready => _pillOutput == DictationOutput.English
                    ? "Click to dictate in English"
                    : "Click to dictate",
                DictationState.Listening => _sessionOutput == DictationOutput.English
                    ? "Translating - click to finish, Esc to cancel"
                    : "Listening - click to finish, Esc to cancel",
                DictationState.NotConfigured => "Add your API key in Settings",
                _ => _state.ToDisplayText()
            };
        }
        else
        {
            text = null;
        }

        if (string.IsNullOrEmpty(text))
        {
            Bubble.Visibility = Visibility.Collapsed;
            return;
        }

        BubbleText.Text = text;
        var rightToLeft = StartsRightToLeft(text);
        BubbleText.FlowDirection = rightToLeft ? System.Windows.FlowDirection.RightToLeft : System.Windows.FlowDirection.LeftToRight;
        BubbleText.TextAlignment = TextAlignment.Left;
        Bubble.Visibility = Visibility.Visible;
    }

    /// <summary>Direction follows the first strong character, so Persian messages read right to left.</summary>
    private static bool StartsRightToLeft(string text)
    {
        foreach (var c in text)
        {
            if (c is >= '֐' and <= 'ࣿ' or >= 'יִ' and <= 'ﻼ')
            {
                return true;
            }

            if (char.IsLetter(c))
            {
                return false;
            }
        }

        return false;
    }

    private void StartPulse()
    {
        var duration = new Duration(TimeSpan.FromMilliseconds(1100));
        var grow = new DoubleAnimation(1, 1.7, duration) { RepeatBehavior = RepeatBehavior.Forever };
        var fade = new DoubleAnimation(0.9, 0, duration) { RepeatBehavior = RepeatBehavior.Forever };
        PulseScale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        PulseScale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        PulseRing.BeginAnimation(OpacityProperty, fade);
    }

    private void StartSpin()
    {
        var spin = new DoubleAnimation(0, 360, new Duration(TimeSpan.FromMilliseconds(900)))
        {
            RepeatBehavior = RepeatBehavior.Forever
        };
        GlyphRotation.BeginAnimation(RotateTransform.AngleProperty, spin);
    }

    private void StopAnimations()
    {
        PulseScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        PulseScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        PulseRing.BeginAnimation(OpacityProperty, null);
        PulseRing.Opacity = 0;
        GlyphRotation.BeginAnimation(RotateTransform.AngleProperty, null);
        GlyphRotation.Angle = 0;
    }

    private void OnPillMouseDown(object sender, MouseButtonEventArgs e)
    {
        _pressScreen = CursorInDips(e);
        _pressWindow = new Point(Left, Top);
        _dragging = false;
        Pill.CaptureMouse();
        e.Handled = true;
    }

    private void OnPillMouseMove(object sender, MouseEventArgs e)
    {
        if (_pressScreen is not { } press || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var current = CursorInDips(e);
        var delta = current - press;
        if (!_dragging && (Math.Abs(delta.X) > DragThreshold || Math.Abs(delta.Y) > DragThreshold))
        {
            _dragging = true;
        }

        if (_dragging)
        {
            Left = _pressWindow.X + delta.X;
            Top = _pressWindow.Y + delta.Y;
        }
    }

    private void OnPillMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_pressScreen is null)
        {
            return;
        }

        _pressScreen = null;
        Pill.ReleaseMouseCapture();
        e.Handled = true;

        if (_dragging)
        {
            _dragging = false;
            _anchor = new Point(Left + Width / 2, Top + FlyoutBand + PillHeight);
            UpdateFlyoutSide();
            Moved?.Invoke(new ControlPosition { X = _anchor.Value.X, Y = _anchor.Value.Y });
            return;
        }

        Clicked?.Invoke();
    }

    /// <summary>The FA/EN chip was clicked: the app flips what a click on the pill produces (architecture T14).</summary>
    public event Action? OutputToggled;

    /// <summary>Menu: insert the Persian behind the last translation at the caret.</summary>
    public event Action? PasteHeardClicked;

    /// <summary>Menu: copy the Persian behind the last translation.</summary>
    public event Action? CopyHeardClicked;

    /// <summary>Menu: start a hands-free dictation in English, whatever the chip says.</summary>
    public event Action? TranslateClicked;

    /// <summary>Menu: write or clear the topic note.</summary>
    public event Action? TopicNoteClicked;

    /// <summary>Menu: the tone for translations.</summary>
    public event Action<TranslationTone>? ToneSelected;

    /// <summary>Menu: forget the remembered translations, so the next one starts clean.</summary>
    public event Action? StartFreshClicked;

    /// <summary>
    /// What the chip shows: the running session's output while there is one, otherwise what a click
    /// would produce. The dot means a topic note is on; its text is in the tooltip.
    /// </summary>
    public void SetOutput(DictationOutput pillOutput, DictationOutput? sessionOutput, string? topicNote)
    {
        _pillOutput = pillOutput;
        _sessionOutput = sessionOutput;
        _topicNote = topicNote;

        var shown = sessionOutput ?? pillOutput;
        OutputChipText.Text = shown == DictationOutput.English ? "EN" : "FA";
        OutputChip.Background = shown == DictationOutput.English ? Blue : ChipIdle;
        NoteDot.Visibility = topicNote is null ? Visibility.Collapsed : Visibility.Visible;
        OutputChip.ToolTip = (shown == DictationOutput.English
                                 ? "Translates to English. Click for Persian dictation."
                                 : "Writes what you say. Click to translate to English instead.")
                             + (topicNote is null ? string.Empty : $"\nTopic: {topicNote}");
        UpdateBubble();
    }

    private void OnChipMouseDown(object sender, MouseButtonEventArgs e)
    {
        // Swallowed so the press never reaches the pill underneath and starts a dictation.
        _chipPressed = true;
        e.Handled = true;
    }

    private void OnChipMouseUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (!_chipPressed)
        {
            return;
        }

        _chipPressed = false;

        // A running session keeps the output it started with; the chip only changes the next one.
        if (_sessionOutput is null)
        {
            OutputToggled?.Invoke();
        }
    }

    /// <summary>The translation rows at the top of the pill menu (architecture T14).</summary>
    private void AddTranslationRows(TranslationTone tone, int remembered)
    {
        // What a click on the pill produces, ticked, with the other one a click away: the same switch
        // as the FA/EN chip, which is easy to miss. A running session keeps its output, as with the chip.
        var current = _sessionOutput ?? _pillOutput;
        foreach (var (name, value) in new[]
                 {
                     ("Output: Persian (speech to text)", DictationOutput.Transcript),
                     ("Output: English (translate)", DictationOutput.English)
                 })
        {
            var selected = value == current;
            var row = MenuRow((selected ? "✓ " : "    ") + name, selected);
            row.MouseLeftButtonUp += (sender, e) => RaiseMenuAction(sender, e, () =>
            {
                if (!selected && _sessionOutput is null)
                {
                    OutputToggled?.Invoke();
                }
            });
            MicrophoneList.Children.Add(row);
        }

        MicrophoneList.Children.Add(MenuSeparator());

        var translate = MenuRow("Translate to English (hands-free)", selected: false);
        translate.MouseLeftButtonUp += (sender, e) => RaiseMenuAction(sender, e, TranslateClicked);
        MicrophoneList.Children.Add(translate);

        var note = MenuRow(_topicNote is null ? "Topic note…" : $"Topic: {_topicNote}", selected: _topicNote is not null);
        note.ToolTip = "What you are writing about, used by every translation for about an hour";
        note.MouseLeftButtonUp += (sender, e) => RaiseMenuAction(sender, e, TopicNoteClicked);
        MicrophoneList.Children.Add(note);

        var tones = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        tones.Children.Add(new TextBlock
        {
            Text = "Tone",
            Foreground = Grey,
            FontSize = 12,
            FontFamily = new FontFamily("Segoe UI"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 4, 0)
        });
        foreach (var (name, value) in new[]
                 {
                     ("As spoken", TranslationTone.AsSpoken), ("Casual", TranslationTone.Casual), ("Formal", TranslationTone.Formal)
                 })
        {
            var choice = MenuRow(name, selected: value == tone);
            choice.MouseLeftButtonUp += (sender, e) => RaiseMenuAction(sender, e, () => ToneSelected?.Invoke(value));
            tones.Children.Add(choice);
        }

        MicrophoneList.Children.Add(tones);

        if (remembered > 0)
        {
            var fresh = MenuRow($"Start fresh (forget {remembered} earlier {(remembered == 1 ? "sentence" : "sentences")})", selected: false);
            fresh.ToolTip = "Translations lean on the last few sentences for names and terms; this drops them";
            fresh.MouseLeftButtonUp += (sender, e) => RaiseMenuAction(sender, e, StartFreshClicked);
            MicrophoneList.Children.Add(fresh);
        }

        MicrophoneList.Children.Add(MenuSeparator());
    }

    /// <summary>Hides the copy and paste offer once the kept text has gone where the user wanted it.</summary>
    public void HideRecovery()
    {
        _keptTextOffer = false;
        UpdateOffers();
    }

    /// <summary>Shows or hides Retry for kept audio (FR-9.1).</summary>
    public void SetRetryOffer(bool available)
    {
        _retryOffer = available;
        UpdateOffers();
    }

    /// <summary>Shows or hides the way to the Windows microphone privacy page (FR-7.5).</summary>
    public void SetPrivacyOffer(bool available)
    {
        _privacyOffer = available;
        UpdateOffers();
    }

    /// <summary>The one-dictation password override (FR-3.7). "Not now" is the same as ignoring it.</summary>
    public void SetPasswordOffer(bool available)
    {
        _passwordOffer = available;
        UpdateOffers();
    }

    private void UpdateOffers()
    {
        CopyButton.Visibility = _keptTextOffer ? Visibility.Visible : Visibility.Collapsed;
        PasteButton.Visibility = _keptTextOffer ? Visibility.Visible : Visibility.Collapsed;
        RetryButton.Visibility = _retryOffer ? Visibility.Visible : Visibility.Collapsed;
        PrivacyButton.Visibility = _privacyOffer ? Visibility.Visible : Visibility.Collapsed;
        PasswordButton.Visibility = _passwordOffer ? Visibility.Visible : Visibility.Collapsed;
        PasswordDeclineButton.Visibility = _passwordOffer ? Visibility.Visible : Visibility.Collapsed;
        Recovery.Visibility = _keptTextOffer || _retryOffer || _privacyOffer || _passwordOffer
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    /// <summary>
    /// Fills and opens the microphone list. The rows are plain borders inside this window, so
    /// clicking one never moves the keyboard focus out of the field being dictated into (FR-2.8).
    /// </summary>
    public void ShowMicrophones(IReadOnlyList<AudioDevice> devices, string? selectedId, bool hasLastDictation,
        bool hasHeard, TranslationTone tone, int rememberedTranslations)
    {
        MicrophoneList.Children.Clear();
        AddTranslationRows(tone, rememberedTranslations);

        // The latest dictation, for when it went to a window with no text field and vanished without
        // an error (FR-9.3). Pasting from here is safe: this window never takes the focus.
        if (hasLastDictation)
        {
            var paste = MenuRow("Paste last dictation", selected: false);
            paste.MouseLeftButtonUp += (sender, e) => RaiseMenuAction(sender, e, PasteClicked);
            MicrophoneList.Children.Add(paste);

            var copy = MenuRow("Copy last dictation", selected: false);
            copy.MouseLeftButtonUp += (sender, e) => RaiseMenuAction(sender, e, CopyClicked);
            MicrophoneList.Children.Add(copy);

            // The Persian behind the last translation, for when the English went wrong (T14).
            if (hasHeard)
            {
                var pasteHeard = MenuRow("Paste the Persian", selected: false);
                pasteHeard.ToolTip = "Inserts what was heard at the caret. Press Ctrl+Z first to take the English out.";
                pasteHeard.MouseLeftButtonUp += (sender, e) => RaiseMenuAction(sender, e, PasteHeardClicked);
                MicrophoneList.Children.Add(pasteHeard);

                var copyHeard = MenuRow("Copy the Persian", selected: false);
                copyHeard.MouseLeftButtonUp += (sender, e) => RaiseMenuAction(sender, e, CopyHeardClicked);
                MicrophoneList.Children.Add(copyHeard);
            }

            MicrophoneList.Children.Add(MenuSeparator());
        }

        foreach (var device in devices)
        {
            var selected = device.Id == selectedId;
            var row = MenuRow(selected ? "• " + device.Name : device.Name, selected);
            row.Tag = device.Id;
            row.MouseLeftButtonUp += OnMicrophoneRowUp;
            MicrophoneList.Children.Add(row);
        }

        MicrophoneList.Children.Add(MenuSeparator());

        var settings = MenuRow("Settings…", selected: false);
        settings.MouseLeftButtonUp += (sender, e) => RaiseMenuAction(sender, e, SettingsClicked);
        MicrophoneList.Children.Add(settings);

        MicrophoneMenu.Visibility = Visibility.Visible;
    }

    private static Border MenuSeparator() => new()
    {
        Height = 1,
        Margin = new Thickness(4, 4, 4, 4),
        Background = Freeze(new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)))
    };

    /// <summary>Opens the Settings window from the pill menu.</summary>
    public event Action? SettingsClicked;

    private Border MenuRow(string text, bool selected)
    {
        var row = new Border
        {
            Background = selected ? Freeze(new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF))) : null,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 4, 8, 4),
            Cursor = Cursors.Hand,
            Child = new TextBlock
            {
                Text = text,
                Foreground = White,
                FontSize = 12,
                FontFamily = new FontFamily("Segoe UI"),
                TextTrimming = TextTrimming.CharacterEllipsis
            }
        };

        row.MouseLeftButtonDown += OnMicrophoneRowDown;
        return row;
    }

    /// <summary>A menu row other than a microphone: close the menu, then act (Settings, paste, copy).</summary>
    private void RaiseMenuAction(object sender, MouseButtonEventArgs e, Action? action)
    {
        e.Handled = true;
        if (!ReferenceEquals(_microphonePressed, sender))
        {
            return;
        }

        _microphonePressed = null;
        HideMicrophones();
        action?.Invoke();
    }

    public void HideMicrophones() => MicrophoneMenu.Visibility = Visibility.Collapsed;

    private void OnPillRightClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (MicrophoneMenu.Visibility == Visibility.Visible)
        {
            HideMicrophones();
            return;
        }

        MicrophonesRequested?.Invoke();
    }

    private void OnMicrophoneRowDown(object sender, MouseButtonEventArgs e)
    {
        _microphonePressed = sender;
        e.Handled = true;
    }

    private void OnMicrophoneRowUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (!ReferenceEquals(_microphonePressed, sender) || sender is not Border row)
        {
            return;
        }

        _microphonePressed = null;
        HideMicrophones();
        MicrophoneSelected?.Invoke(row.Tag as string);
    }

    private void OnMicrophoneMenuLeave(object sender, MouseEventArgs e)
    {
        _microphonePressed = null;
        HideMicrophones();
    }

    private void OnRecoveryMouseDown(object sender, MouseButtonEventArgs e)
    {
        // Swallowed so the press never reaches the pill underneath and starts a dictation.
        _recoveryPressed = sender;
        e.Handled = true;
    }

    private void OnCopyMouseUp(object sender, MouseButtonEventArgs e) => RaiseRecovery(sender, e, CopyClicked);

    private void OnPasteMouseUp(object sender, MouseButtonEventArgs e) => RaiseRecovery(sender, e, PasteClicked);

    private void OnRetryMouseUp(object sender, MouseButtonEventArgs e) => RaiseRecovery(sender, e, RetryClicked);

    private void OnPrivacyMouseUp(object sender, MouseButtonEventArgs e) => RaiseRecovery(sender, e, PrivacySettingsClicked);

    private void OnPasswordOverrideMouseUp(object sender, MouseButtonEventArgs e) => RaiseRecovery(sender, e, PasswordOverrideClicked);

    private void OnPasswordDeclineMouseUp(object sender, MouseButtonEventArgs e) => RaiseRecovery(sender, e, PasswordDeclineClicked);

    private void RaiseRecovery(object sender, MouseButtonEventArgs e, Action? action)
    {
        e.Handled = true;
        if (!ReferenceEquals(_recoveryPressed, sender))
        {
            return;
        }

        _recoveryPressed = null;
        action?.Invoke();
    }

    private void OnCancelMouseDown(object sender, MouseButtonEventArgs e)
    {
        _cancelPressed = true;
        e.Handled = true;
    }

    private void OnCancelMouseUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_cancelPressed)
        {
            _cancelPressed = false;
            CancelClicked?.Invoke();
        }
    }

    private void OnPillMouseEnter(object sender, MouseEventArgs e)
    {
        _hovering = true;
        UpdateBubble();
    }

    private void OnPillMouseLeave(object sender, MouseEventArgs e)
    {
        _hovering = false;
        _cancelPressed = false;
        UpdateBubble();
    }

    /// <summary>
    /// Places the window so the pill's bottom-centre is the anchor. The window has a fixed size and the
    /// pill keeps its row, so the pill stays exactly where the user put it whatever is shown.
    /// </summary>
    private void ApplyAnchor()
    {
        if (_anchor is not { } anchor)
        {
            return;
        }

        Left = anchor.X - Width / 2;
        Top = anchor.Y - FlyoutBand - PillHeight;
        UpdateFlyoutSide();
    }

    /// <summary>
    /// Keeps the flyouts on screen: they hang above the pill, and below it when the pill sits too close
    /// to the top of its screen for the band above to fit (T11).
    /// </summary>
    private void UpdateFlyoutSide()
    {
        if (_anchor is not { } anchor)
        {
            return;
        }

        var below = anchor.Y - PillHeight - FlyoutBand < WorkAreaTop(anchor);
        if (below == _flyoutsBelow)
        {
            return;
        }

        _flyoutsBelow = below;

        // Above, the order away from the pill is Recovery, the menu, then the bubble; below it mirrors.
        Border[] awayFromPill = [Recovery, MicrophoneMenu, Bubble];
        AboveStack.Children.Clear();
        BelowStack.Children.Clear();

        foreach (var flyout in awayFromPill)
        {
            flyout.Margin = below ? new Thickness(0, 6, 0, 0) : new Thickness(0, 0, 0, 6);
            if (below)
            {
                BelowStack.Children.Add(flyout);
            }
            else
            {
                AboveStack.Children.Insert(0, flyout);
            }
        }
    }

    /// <summary>Top of the work area of the screen the pill is on, in device-independent pixels.</summary>
    private double WorkAreaTop(Point anchor)
    {
        if (PresentationSource.FromVisual(this)?.CompositionTarget is not { } target)
        {
            return SystemParameters.WorkArea.Top;
        }

        var device = target.TransformToDevice.Transform(anchor);
        var screen = Screen.FromPoint(new System.Drawing.Point((int)device.X, (int)device.Y));
        return target.TransformFromDevice.Transform(new Point(screen.WorkingArea.Left, screen.WorkingArea.Top)).Y;
    }

    private Point CursorInDips(MouseEventArgs e)
    {
        var device = PointToScreen(e.GetPosition(this));
        var source = PresentationSource.FromVisual(this);
        return source?.CompositionTarget is { } target ? target.TransformFromDevice.Transform(device) : device;
    }

    private static bool IsOnScreen(ControlPosition position) =>
        position.X >= SystemParameters.VirtualScreenLeft &&
        position.X <= SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth &&
        position.Y >= SystemParameters.VirtualScreenTop + 20 &&
        position.Y <= SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight;

    private static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
    }
}
