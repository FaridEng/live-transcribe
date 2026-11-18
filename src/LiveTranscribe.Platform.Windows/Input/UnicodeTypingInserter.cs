using System.Runtime.InteropServices;
using LiveTranscribe.Core.Abstractions;
using LiveTranscribe.Core.Diagnostics;
using LiveTranscribe.Core.Text;

namespace LiveTranscribe.Platform.Windows.Input;

/// <summary>
/// Types the text with <c>SendInput</c> and <c>KEYEVENTF_UNICODE</c>, one UTF-16 unit at a time, so
/// nothing ever reaches the clipboard. That is what a confirmed password dictation needs (FR-3.7), and
/// it is also the strategy for apps where pasting does not work (§4.7). The characters are sent as
/// Unicode, not as key presses, so the active keyboard layout does not change the result (FR-3.3).
/// </summary>
public sealed class UnicodeTypingInserter : ITextInserter
{
    // One SendInput call per batch: small enough not to overflow the target's input queue, large
    // enough that a normal dictation is a handful of calls.
    private const int BatchUnits = 100;

    private const uint InputKeyboard = 1;
    private const uint KeyEventUnicode = 0x0004;
    private const uint KeyEventKeyUp = 0x0002;

    private readonly IAppLogger _log;

    public UnicodeTypingInserter(IAppLogger log) => _log = log;

    public async Task<InsertResult> InsertAsync(string text, CancellationToken ct)
    {
        if (text.Any(ControlCharacterFilter.IsUnsafeForInsertion))
        {
            // The pipeline strips these; reaching here means a caller bypassed it (FR-1.6, D8).
            return new InsertResult(false, "The text contains control characters and was not inserted.");
        }

        // A still-held modifier from the shortcut would turn the typed characters into commands.
        await WaitForModifiersReleasedAsync(ct).ConfigureAwait(false);

        var sent = 0;
        while (sent < text.Length)
        {
            var units = Math.Min(BatchUnits, text.Length - sent);
            var inputs = new Input[units * 2];
            for (var i = 0; i < units; i++)
            {
                inputs[i * 2] = KeyUnit(text[sent + i], up: false);
                inputs[i * 2 + 1] = KeyUnit(text[sent + i], up: true);
            }

            var accepted = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
            if (accepted != inputs.Length)
            {
                var error = Marshal.GetLastWin32Error();
                _log.Warn($"Typing stopped after {sent} of {text.Length} characters (SendInput error {error}).");
                return new InsertResult(false, $"SendInput failed ({error}).");
            }

            sent += units;
        }

        return new InsertResult(true);
    }

    private static Input KeyUnit(char unit, bool up) => new()
    {
        Type = InputKeyboard,
        VirtualKey = 0,
        ScanCode = unit,
        Flags = KeyEventUnicode | (up ? KeyEventKeyUp : 0)
    };

    private static async Task WaitForModifiersReleasedAsync(CancellationToken ct)
    {
        int[] modifiers = [0x10, 0x11, 0x12, 0x5B, 0x5C]; // Shift, Ctrl, Alt, left and right Win
        for (var waited = 0; waited < 1000; waited += 15)
        {
            if (!modifiers.Any(key => (GetAsyncKeyState(key) & 0x8000) != 0))
            {
                return;
            }

            await Task.Delay(15, ct).ConfigureAwait(false);
        }
    }

    // Keyboard-only INPUT layout: padded to the size of the full union on 64-bit Windows.
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct Input
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public ushort VirtualKey;
        [FieldOffset(10)] public ushort ScanCode;
        [FieldOffset(12)] public uint Flags;
        [FieldOffset(16)] public uint Time;
        [FieldOffset(24)] public IntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, Input[] inputs, int size);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);
}
