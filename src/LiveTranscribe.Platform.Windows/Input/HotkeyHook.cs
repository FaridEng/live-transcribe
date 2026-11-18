using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using LiveTranscribe.Core.Diagnostics;

namespace LiveTranscribe.Platform.Windows.Input;

/// <summary>A chord shortcut (hands-free toggle, paste last): a key plus modifiers. A key of 0 turns it off.</summary>
public sealed record ToggleChord(int VirtualKey, bool Ctrl, bool Alt, bool Shift)
{
    public static ToggleChord None { get; } = new(0, false, false, false);
}

/// <summary>
/// Low-level keyboard hook for all dictation shortcuts. <c>RegisterHotKey</c> reports no key-up, so it
/// cannot drive hold-to-talk (FR-2.2). Keys are matched by virtual-key code, which is independent of
/// the keyboard layout (FR-2.4), and matched keys are swallowed so they never reach the target app
/// (FR-2.1). Callbacks run on the hook thread and must return quickly; handlers should only post work.
/// </summary>
public sealed class HotkeyHook : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WmKeydown = 0x0100;
    private const int WmKeyup = 0x0101;
    private const int WmSyskeydown = 0x0104;
    private const int WmSyskeyup = 0x0105;
    private const int WmQuit = 0x0012;
    private const uint LlkhfInjected = 0x10;
    private const int VkEscape = 0x1B;
    private const int VkShift = 0x10;
    private const int VkControl = 0x11;
    private const int VkMenu = 0x12;

    // How long every key event is swallowed after a capture finishes, to absorb the auto-repeat and
    // key-up of the key that completed it (and of any modifiers held with it) without leaking into the
    // shortcut it may have just replaced (Settings → Shortcuts capture).
    private static readonly TimeSpan CaptureDrain = TimeSpan.FromMilliseconds(300);

    private readonly IAppLogger _log;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private readonly LowLevelKeyboardProc _proc;

    private IntPtr _hook;
    private uint _threadId;
    private volatile ToggleChord _hold;
    private volatile ToggleChord _translateHold = ToggleChord.None;
    private volatile ToggleChord _translateToggle = ToggleChord.None;
    private volatile ToggleChord _toggle;
    private volatile ToggleChord _pasteLast = ToggleChord.None;
    private volatile bool _escapeArmed;
    private volatile Action<ToggleChord>? _captureHold;
    private volatile Action? _captureHoldCancel;
    private volatile Action<ToggleChord>? _captureChord;
    private volatile Action? _captureChordCancel;
    private long _captureDrainUntil;
    // The physically-held final key of a hold shortcut (dictate or translate), or 0. One hold at a time:
    // pressing the other hold key meanwhile is ignored, so a session always ends on the key that started
    // it. Kept set - and every event for it swallowed - until its own key-up, even after a required
    // modifier's release has already ended the session (§ EndsActiveHold), so the OS auto-repeat of a
    // key still physically down never leaks into the app once the modifier is gone.
    private int _heldKey;
    private bool _heldIsTranslate;
    private bool _heldActive;
    private int _swallowKeyUp;
    private Exception? _startFailure;

    // Tracked from the hook's own keydown/keyup events rather than queried from GetAsyncKeyState: a
    // modifier this same hook is about to swallow (return 1 for, e.g. while a Settings capture is armed)
    // can leave GetAsyncKeyState reporting it as still up, since the state table it reads is fed by the
    // normal input pipeline this hook is short-circuiting. Self-tracking removes that dependency (found
    // 2026-09-28: this is why a captured "Alt + 1" was coming through as a bare "1").
    private bool _ctrlDown;
    private bool _altDown;
    private bool _shiftDown;

    public HotkeyHook(ToggleChord hold, ToggleChord toggle, IAppLogger log)
    {
        _hold = hold;
        _toggle = toggle;
        _log = log;
        _proc = HookCallback; // Keep a reference so the delegate is not collected.

        _thread = new Thread(Run) { IsBackground = true, Name = "LiveTranscribe keyboard hook" };
        _thread.Start();
        _ready.Wait();

        if (_startFailure is not null)
        {
            throw _startFailure;
        }
    }

    public event Action? HoldPressed;
    public event Action? HoldReleased;
    public event Action? TranslateHoldPressed;
    public event Action? TranslateHoldReleased;
    public event Action? TranslateTogglePressed;
    public event Action? TogglePressed;
    public event Action? EscapePressed;
    public event Action? PasteLastPressed;

    /// <summary>
    /// Hold to dictate: the key plus whichever of Ctrl/Alt/Shift are required (user request, 2026-09-28)
    /// must all be held down together; releasing any one of them ends the session.
    /// </summary>
    public ToggleChord Hold
    {
        get => _hold;
        set => _hold = value;
    }

    /// <summary>Hold to dictate in English (architecture T14); a key of 0 turns it off.</summary>
    public ToggleChord TranslateHold
    {
        get => _translateHold;
        set => _translateHold = value;
    }

    public ToggleChord TranslateToggle
    {
        get => _translateToggle;
        set => _translateToggle = value;
    }

    public ToggleChord Toggle
    {
        get => _toggle;
        set => _toggle = value;
    }

    public ToggleChord PasteLast
    {
        get => _pasteLast;
        set => _pasteLast = value;
    }

    /// <summary>Esc is only taken while a recording can be cancelled; otherwise it belongs to the app (FR-2.7).</summary>
    public bool EscapeArmed
    {
        get => _escapeArmed;
        set => _escapeArmed = value;
    }

    /// <summary>
    /// Reports the next key as a hold shortcut (Settings → Shortcuts, "hold" rows), which may be a bare
    /// modifier on its own (the default, Right Ctrl) or a key held together with whichever of Ctrl/Alt/
    /// Shift go with it (user request, 2026-09-28, so Alt + 1 can be held to talk, not just tapped): a
    /// modifier released before any other key was pressed completes the capture as that modifier alone;
    /// a non-modifier key pressed while modifiers are held completes it as a chord. Escape cancels.
    /// </summary>
    public void BeginCaptureHold(Action<ToggleChord> onCaptured, Action onCancelled)
    {
        CancelCapture();
        _captureHoldCancel = onCancelled;
        _captureHold = onCaptured;
    }

    /// <summary>
    /// Reports the next non-modifier key together with whichever of Ctrl/Alt/Shift are held with it, as a
    /// chord shortcut. Modifier-only key-downs keep waiting; Escape cancels.
    /// </summary>
    public void BeginCaptureChord(Action<ToggleChord> onCaptured, Action onCancelled)
    {
        CancelCapture();
        _captureChordCancel = onCancelled;
        _captureChord = onCaptured;
    }

    /// <summary>Ends a capture in progress, if any, and notifies whichever "cancelled" callback was armed.</summary>
    public void CancelCapture()
    {
        var hold = _captureHoldCancel;
        var chord = _captureChordCancel;
        _captureHold = null;
        _captureHoldCancel = null;
        _captureChord = null;
        _captureChordCancel = null;
        hold?.Invoke();
        chord?.Invoke();
    }

    public void Dispose()
    {
        if (_threadId != 0)
        {
            PostThreadMessage(_threadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
            _thread.Join(TimeSpan.FromSeconds(2));
        }

        _ready.Dispose();
    }

    private void Run()
    {
        _threadId = GetCurrentThreadId();
        using (var module = Process.GetCurrentProcess().MainModule)
        {
            _hook = SetWindowsHookEx(WhKeyboardLl, _proc, GetModuleHandle(module?.ModuleName), 0);
        }

        if (_hook == IntPtr.Zero)
        {
            _startFailure = new Win32Exception(Marshal.GetLastWin32Error(), "Keyboard hook could not be installed.");
            _ready.Set();
            return;
        }

        _ready.Set();
        _log.Info("Keyboard hook installed.");

        while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref message);
            DispatchMessage(ref message);
        }

        UnhookWindowsHookEx(_hook);
        _log.Info("Keyboard hook removed.");
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0)
        {
            return CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        var data = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);

        // Ignore synthetic input, including our own Ctrl+V during insertion.
        if ((data.Flags & LlkhfInjected) != 0)
        {
            return CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        var message = wParam.ToInt32();
        var isDown = message is WmKeydown or WmSyskeydown;
        var isUp = message is WmKeyup or WmSyskeyup;
        var vk = data.VkCode;

        if (isDown || isUp)
        {
            if (IsCtrlVk(vk))
            {
                _ctrlDown = isDown;
            }
            else if (IsAltVk(vk))
            {
                _altDown = isDown;
            }
            else if (IsShiftVk(vk))
            {
                _shiftDown = isDown;
            }
        }

        // Settings → Shortcuts capture takes every key while it is armed, and for a short drain afterwards
        // (§ CaptureDrain), so a key just captured (or the modifiers held with it) is never also acted on
        // by whatever it used to mean, or by an OS auto-repeat of the same physical press.
        if (_captureHold is not null || _captureChord is not null || Environment.TickCount64 < _captureDrainUntil)
        {
            HandleCapture(isDown, vk);
            return 1;
        }

        if (_heldKey != 0)
        {
            // The key that started the session: its own key-up (or auto-repeat while still held) always
            // ends things here, whatever the modifiers are doing.
            if (vk == _heldKey)
            {
                if (isUp)
                {
                    _heldKey = 0;
                    EndHold();
                }

                return 1;
            }

            // A required modifier let go before the key did: the session ends now, but the modifier's own
            // key-up is not swallowed - unlike the toggle chords, it was never swallowed going down either
            // (only the final key was), so letting its release through keeps the app's own idea of
            // whether Alt is still held consistent with what actually happened physically.
            if (_heldActive && isUp && EndsActiveHold(vk))
            {
                EndHold();
            }
        }
        else
        {
            var hold = _hold;
            var translateHold = _translateHold;
            if (isDown && hold.VirtualKey != 0 && vk == hold.VirtualKey && ModifiersHeldFor(hold))
            {
                StartHold(vk, hold, translate: false);
                return 1;
            }

            if (isDown && translateHold.VirtualKey != 0 && vk == translateHold.VirtualKey && ModifiersHeldFor(translateHold))
            {
                StartHold(vk, translateHold, translate: true);
                return 1;
            }
        }

        // The key-up of a chord or Esc we consumed must not reach the app on its own.
        if (isUp && vk == _swallowKeyUp)
        {
            _swallowKeyUp = 0;
            return 1;
        }

        if (isDown && vk == VkEscape && _escapeArmed)
        {
            _swallowKeyUp = vk;
            Raise(EscapePressed);
            return 1;
        }

        if (isDown && (TryChord(_toggle, vk, TogglePressed) || TryChord(_translateToggle, vk, TranslateTogglePressed) ||
                       TryChord(_pasteLast, vk, PasteLastPressed)))
        {
            return 1;
        }

        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    /// <summary>Advances or finishes a capture. Most of it only cares about key-downs; the hold capture also
    /// watches key-ups, since a bare modifier's release (with nothing else pressed meanwhile) is itself a
    /// valid result for that one.</summary>
    private void HandleCapture(bool isDown, int vk)
    {
        if (_captureHold is { } hold)
        {
            if (isDown)
            {
                if (vk == VkEscape)
                {
                    CompleteHoldCapture(null);
                    return;
                }

                if (IsModifierKey(vk))
                {
                    // Still waiting to see whether another key follows while this is held.
                    return;
                }

                CompleteHoldCapture(new ToggleChord(vk, _ctrlDown, _altDown, _shiftDown));
            }
            else if (IsModifierKey(vk))
            {
                // Released before any other key went down: that modifier alone is the whole shortcut,
                // matching the pre-2026-09-28 single-key behaviour (e.g. the default, Right Ctrl alone).
                CompleteHoldCapture(new ToggleChord(vk, false, false, false));
            }

            return;

            void CompleteHoldCapture(ToggleChord? captured)
            {
                var cancel = _captureHoldCancel;
                _captureHold = null;
                _captureHoldCancel = null;
                _captureDrainUntil = Environment.TickCount64 + (long)CaptureDrain.TotalMilliseconds;
                Raise(captured is { } c ? () => hold(c) : cancel);
            }
        }

        if (!isDown)
        {
            return;
        }

        if (_captureChord is { } chord)
        {
            if (vk == VkEscape)
            {
                var cancel = _captureChordCancel;
                _captureChord = null;
                _captureChordCancel = null;
                _captureDrainUntil = Environment.TickCount64 + (long)CaptureDrain.TotalMilliseconds;
                Raise(cancel);
                return;
            }

            if (IsModifierKey(vk))
            {
                // Still waiting for the key that goes with it.
                return;
            }

            _captureChord = null;
            _captureChordCancel = null;
            _captureDrainUntil = Environment.TickCount64 + (long)CaptureDrain.TotalMilliseconds;
            var captured = new ToggleChord(vk, _ctrlDown, _altDown, _shiftDown);
            Raise(() => chord(captured));
        }
    }

    private static bool IsModifierKey(int vk) =>
        vk is VkShift or VkControl or VkMenu or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5;

    /// <summary>
    /// Starts a hold-to-talk/translate session for a key plus modifiers (user request, 2026-09-28): the
    /// same "another key happened" trick as <see cref="TryChord"/> for a chord that includes Alt, done
    /// here at match time rather than at release, since either works - the mask key only needs to land
    /// somewhere between Alt going down and its eventual release.
    /// </summary>
    private void StartHold(int vk, ToggleChord chord, bool translate)
    {
        _heldKey = vk;
        _heldIsTranslate = translate;
        _heldActive = true;
        if (chord.Alt)
        {
            SendMenuMaskKey();
        }

        Raise(translate ? TranslateHoldPressed : HoldPressed);
    }

    private void EndHold()
    {
        _heldActive = false;
        Raise(_heldIsTranslate ? TranslateHoldReleased : HoldReleased);
    }

    /// <summary>Whether <paramref name="vk"/> is a modifier the active hold chord requires, so its release ends the session.</summary>
    private bool EndsActiveHold(int vk)
    {
        var chord = _heldIsTranslate ? _translateHold : _hold;
        return (chord.Ctrl && IsCtrlVk(vk)) || (chord.Alt && IsAltVk(vk)) || (chord.Shift && IsShiftVk(vk));
    }

    /// <summary>
    /// Whether every modifier a hold chord requires is currently held ("at least", not "exactly" like
    /// <see cref="ModifiersMatch"/>): a hold chord's own key can itself be a modifier (Right Ctrl, the
    /// default), in which case that modifier is naturally down the moment the key is - matching it
    /// exactly against "no modifiers required" would make the default hold key never match its own press.
    /// </summary>
    private bool ModifiersHeldFor(ToggleChord chord) =>
        (!chord.Ctrl || _ctrlDown) && (!chord.Alt || _altDown) && (!chord.Shift || _shiftDown);

    private static bool IsCtrlVk(int vk) => vk is VkControl or 0xA2 or 0xA3;
    private static bool IsAltVk(int vk) => vk is VkMenu or 0xA4 or 0xA5;
    private static bool IsShiftVk(int vk) => vk is VkShift or 0xA0 or 0xA1;

    /// <summary>Raises the chord's event and swallows its key when the chord matches.</summary>
    private bool TryChord(ToggleChord chord, int vk, Action? handler)
    {
        if (chord.VirtualKey == 0 || vk != chord.VirtualKey || !ModifiersMatch(chord))
        {
            return false;
        }

        _swallowKeyUp = vk;
        if (chord.Alt)
        {
            SendMenuMaskKey();
        }

        Raise(handler);
        return true;
    }

    /// <summary>
    /// Releasing Alt without another key in between opens the menu bar in many apps, which would steal
    /// the paste. An unassigned virtual key (0xE8) in between prevents that.
    /// </summary>
    private static void SendMenuMaskKey()
    {
        var inputs = new[]
        {
            new MaskInput { Type = 1, VirtualKey = 0xE8 },
            new MaskInput { Type = 1, VirtualKey = 0xE8, Flags = 0x0002 }
        };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<MaskInput>());
    }

    // Keyboard-only INPUT layout: padded to the size of the full union on 64-bit Windows.
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct MaskInput
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public ushort VirtualKey;
        [FieldOffset(10)] public ushort ScanCode;
        [FieldOffset(12)] public uint Flags;
        [FieldOffset(16)] public uint Time;
        [FieldOffset(24)] public IntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, MaskInput[] inputs, int size);

    private bool ModifiersMatch(ToggleChord chord) =>
        _ctrlDown == chord.Ctrl && _altDown == chord.Alt && _shiftDown == chord.Shift;

    private void Raise(Action? handler)
    {
        try
        {
            handler?.Invoke();
        }
        catch (Exception ex)
        {
            // An exception escaping the hook callback would take the hook down.
            _log.Error("Hotkey handler failed.", ex);
        }
    }

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdLlHookStruct
    {
        public int VkCode;
        public int ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public IntPtr Hwnd;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int PointX;
        public int PointY;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc callback, IntPtr module, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out Msg message, IntPtr hwnd, uint filterMin, uint filterMax);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref Msg message);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref Msg message);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint threadId, int message, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);
}
