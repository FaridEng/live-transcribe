using System.ComponentModel;
using System.Runtime.InteropServices;
using LiveTranscribe.Core.Abstractions;
using LiveTranscribe.Core.Diagnostics;
using LiveTranscribe.Core.Text;

namespace LiveTranscribe.Platform.Windows.Input;

/// <summary>
/// Inserts text by pasting it: one edit, one undo, and friendly to autocomplete fields (FR-1.5, FR-3.5).
/// The user's clipboard is snapshotted and restored, and the dictated text is marked so it stays out
/// of clipboard history and cloud clipboard (FR-3.4). Must be used from a single thread (the UI thread).
/// </summary>
public sealed class ClipboardPasteInserter : ITextInserter, IClipboardWriter, IDisposable
{
    private const uint CfUnicodeText = 13;
    private const uint GmemMoveable = 0x0002;
    private const int SnapshotLimitBytes = 64 * 1024 * 1024;

    // Handle-based formats cannot be copied as memory blocks. Windows re-synthesizes most of them
    // (for example CF_BITMAP from CF_DIB) when the memory formats are restored.
    private static readonly HashSet<uint> HandleFormats = [2, 3, 9, 14, 0x80, 0x82, 0x83, 0x8E];

    private static readonly TimeSpan PasteSettleTime = TimeSpan.FromMilliseconds(350);

    private readonly IAppLogger _log;
    private IntPtr _window;
    private List<(uint Format, byte[] Data)>? _pendingSnapshot;
    private uint _pendingSequence;

    public ClipboardPasteInserter(IAppLogger log)
    {
        _log = log;
    }

    /// <summary>
    /// Recovery path: leaves the text on the clipboard for the user to paste (FR-9.3). Nothing is
    /// restored afterwards, because here the clipboard content is what the user asked for. The
    /// history and cloud exclusions still apply, so a dictation never lingers in Win+V (FR-3.4).
    /// </summary>
    public void SetText(string text)
    {
        EnsureWindow();
        WriteClipboard([(CfUnicodeText, UnicodeBytes(text))]);
    }

    public async Task<InsertResult> InsertAsync(string text, CancellationToken ct)
    {
        EnsureWindow();

        if (text.Any(ControlCharacterFilter.IsUnsafeForInsertion))
        {
            // The pipeline strips these; reaching here means a caller bypassed it (FR-1.6, D8).
            return new InsertResult(false, "The text contains control characters and was not inserted.");
        }

        // A restore still waiting from the previous insertion has to happen before this snapshot,
        // otherwise the snapshot would capture the previous dictation instead of the user's content.
        RestorePending();

        List<(uint Format, byte[] Data)> snapshot;
        try
        {
            snapshot = Snapshot();
        }
        catch (Exception ex)
        {
            return new InsertResult(false, $"Could not read the clipboard: {ex.Message}");
        }

        try
        {
            WriteClipboard([(CfUnicodeText, UnicodeBytes(text))]);
        }
        catch (Exception ex)
        {
            return new InsertResult(false, $"Could not write the clipboard: {ex.Message}");
        }

        var ourSequence = GetClipboardSequenceNumber();

        // A still-held Alt or Shift from a shortcut would turn Ctrl+V into another command.
        await WaitForModifiersReleasedAsync();

        if (!SendCtrlV())
        {
            TryRestore(snapshot, ourSequence);
            return new InsertResult(false, $"SendInput failed ({Marshal.GetLastWin32Error()}).");
        }

        // The text is in the field as soon as Ctrl+V lands. The target still needs time to read the
        // clipboard before the original content comes back, but the caller does not have to wait for
        // that, so the app is ready for the next dictation straight away.
        _pendingSnapshot = snapshot;
        _pendingSequence = ourSequence;
        _ = RestoreAfterSettleAsync();

        return new InsertResult(true);
    }

    /// <summary>
    /// Deliberately not <c>ConfigureAwait(false)</c>: the clipboard owner window belongs to the
    /// calling thread, so the restore has to come back to it.
    /// </summary>
    private async Task RestoreAfterSettleAsync()
    {
        await Task.Delay(PasteSettleTime);
        RestorePending();
    }

    private void RestorePending()
    {
        if (_pendingSnapshot is not { } snapshot)
        {
            return;
        }

        _pendingSnapshot = null;
        TryRestore(snapshot, _pendingSequence);
    }

    public void Dispose()
    {
        // Never leave the user's clipboard holding a dictation because we shut down mid-wait.
        RestorePending();

        if (_window != IntPtr.Zero)
        {
            DestroyWindow(_window);
            _window = IntPtr.Zero;
        }
    }

    private void TryRestore(List<(uint Format, byte[] Data)> snapshot, uint ourSequence)
    {
        // If something else wrote the clipboard in the meantime, that content wins.
        if (GetClipboardSequenceNumber() != ourSequence)
        {
            _log.Info("Clipboard changed during insertion; not restoring.");
            return;
        }

        try
        {
            WriteClipboard(snapshot);
        }
        catch (Exception ex)
        {
            _log.Warn($"Clipboard restore failed: {ex.Message}");
        }
    }

    private List<(uint Format, byte[] Data)> Snapshot()
    {
        var saved = new List<(uint, byte[])>();
        var total = 0L;

        using var open = OpenWithRetry();
        uint format = 0;
        while ((format = EnumClipboardFormats(format)) != 0)
        {
            if (HandleFormats.Contains(format))
            {
                continue;
            }

            var handle = GetClipboardData(format);
            if (handle == IntPtr.Zero)
            {
                continue;
            }

            var size = (long)GlobalSize(handle);
            if (size <= 0 || total + size > SnapshotLimitBytes)
            {
                continue;
            }

            var pointer = GlobalLock(handle);
            if (pointer == IntPtr.Zero)
            {
                continue;
            }

            try
            {
                var bytes = new byte[size];
                Marshal.Copy(pointer, bytes, 0, (int)size);
                saved.Add((format, bytes));
                total += size;
            }
            finally
            {
                GlobalUnlock(handle);
            }
        }

        return saved;
    }

    private void WriteClipboard(IEnumerable<(uint Format, byte[] Data)> items)
    {
        using var open = OpenWithRetry();
        if (!EmptyClipboard())
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "EmptyClipboard failed.");
        }

        foreach (var (format, data) in items)
        {
            SetData(format, data);
        }

        // Keep this write out of Win+V history and cloud clipboard (FR-3.4).
        var zero = BitConverter.GetBytes(0);
        SetData(RegisterClipboardFormat("ExcludeClipboardContentFromMonitorProcessing"), zero);
        SetData(RegisterClipboardFormat("CanIncludeInClipboardHistory"), zero);
        SetData(RegisterClipboardFormat("CanUploadToCloudClipboard"), zero);
    }

    private static void SetData(uint format, byte[] data)
    {
        var handle = GlobalAlloc(GmemMoveable, (UIntPtr)Math.Max(data.Length, 1));
        if (handle == IntPtr.Zero)
        {
            throw new OutOfMemoryException("GlobalAlloc failed.");
        }

        var pointer = GlobalLock(handle);
        Marshal.Copy(data, 0, pointer, data.Length);
        GlobalUnlock(handle);

        // On success the system owns the memory.
        if (SetClipboardData(format, handle) == IntPtr.Zero)
        {
            GlobalFree(handle);
        }
    }

    private ClipboardScope OpenWithRetry()
    {
        // Another app may hold the clipboard briefly.
        for (var attempt = 0; attempt < 10; attempt++)
        {
            if (OpenClipboard(_window))
            {
                return new ClipboardScope();
            }

            Thread.Sleep(20);
        }

        throw new Win32Exception(Marshal.GetLastWin32Error(), "The clipboard is in use by another application.");
    }

    private void EnsureWindow()
    {
        if (_window != IntPtr.Zero)
        {
            return;
        }

        // EmptyClipboard needs an owner window, otherwise SetClipboardData fails.
        _window = CreateWindowEx(0, "STATIC", "LiveTranscribe clipboard", 0, 0, 0, 0, 0,
            new IntPtr(-3) /* HWND_MESSAGE */, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (_window == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create the clipboard owner window.");
        }
    }

    private static byte[] UnicodeBytes(string text)
    {
        var bytes = new byte[(text.Length + 1) * 2];
        global::System.Text.Encoding.Unicode.GetBytes(text, 0, text.Length, bytes, 0);
        return bytes;
    }

    private static async Task WaitForModifiersReleasedAsync()
    {
        int[] modifiers = [0x10, 0x11, 0x12, 0x5B, 0x5C]; // Shift, Ctrl, Alt, left and right Win
        for (var attempt = 0; attempt < 40; attempt++)
        {
            if (!modifiers.Any(key => (GetAsyncKeyState(key) & 0x8000) != 0))
            {
                return;
            }

            await Task.Delay(25);
        }
    }

    private static bool SendCtrlV()
    {
        const ushort vkControl = 0x11;
        const ushort vkV = 0x56;
        const uint keyUp = 0x0002;

        var inputs = new[]
        {
            Key(vkControl, 0),
            Key(vkV, 0),
            Key(vkV, keyUp),
            Key(vkControl, keyUp)
        };

        return SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>()) == inputs.Length;
    }

    private static Input Key(ushort virtualKey, uint flags) => new()
    {
        Type = 1, // INPUT_KEYBOARD
        Union = new InputUnion { Keyboard = new KeybdInput { VirtualKey = virtualKey, Flags = flags } }
    };

    private readonly struct ClipboardScope : IDisposable
    {
        public void Dispose() => CloseClipboard();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion Union;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public KeybdInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeybdInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, Input[] inputs, int size);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(IntPtr owner);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint EnumClipboardFormats(uint format);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetClipboardData(uint format);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint format, IntPtr memory);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint RegisterClipboardFormat(string name);

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern UIntPtr GlobalSize(IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr memory);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr window);
}
