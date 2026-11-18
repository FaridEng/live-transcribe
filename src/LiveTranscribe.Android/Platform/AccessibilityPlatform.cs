using Android.Content;
using Android.OS;
using Android.Views.Accessibility;
using LiveTranscribe.Core.Abstractions;
using LiveTranscribe.Core.Diagnostics;

namespace LiveTranscribe.Droid.Platform;

/// <summary>
/// Reads the focused field through the accessibility service. Node queries are IPC into the other app and
/// can hang, so they always run off the UI thread behind a timeout, and a timeout is "unknown", never "safe".
/// </summary>
internal static class FocusedField
{
    public static async Task<AccessibilityNodeInfo?> FindAsync(TimeSpan timeout, CancellationToken ct)
    {
        var service = DictationAccessibilityService.Current;
        if (service is null)
        {
            return null;
        }

        var query = Task.Run(() => service.FindFocus(NodeFocus.Input), ct);
        var finished = await Task.WhenAny(query, Task.Delay(timeout, ct)).ConfigureAwait(false);
        return finished == query ? await query.ConfigureAwait(false) : throw new TimeoutException();
    }
}

/// <summary>
/// The dictation target on Android: the app package and window that held the focused field. The package
/// stands in for the Windows process, so translation context resets when the user moves to another app.
/// </summary>
public sealed class AccessibilityTargetTracker : ITargetTracker
{
    private readonly IAppLogger _log;

    public AccessibilityTargetTracker(IAppLogger log)
    {
        _log = log;
    }

    public TargetSnapshot? Capture()
    {
        // Cheap on purpose: it runs on the activation path, so it uses what the last focus event reported.
        var field = DictationAccessibilityService.Current?.LastField;
        return field is null ? null : new TargetSnapshot(field.WindowId, StableId(field.Package), field.Package);
    }

    public async Task<bool> IsPasswordFieldAsync(TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            var node = await FocusedField.FindAsync(timeout, ct).ConfigureAwait(false);
            return node?.Password == true;
        }
        catch (Exception ex) when (ex is TimeoutException or System.OperationCanceledException)
        {
            return false;
        }
    }

    public async Task<TargetCheck> CheckAsync(TargetSnapshot? snapshot, TimeSpan timeout, CancellationToken ct)
    {
        AccessibilityNodeInfo? node;
        try
        {
            node = await FocusedField.FindAsync(timeout, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or System.OperationCanceledException)
        {
            _log.Info("Focused-field check timed out; treated as unknown.");
            return TargetCheck.Unknown;
        }

        if (node is null)
        {
            // The service answered and nothing has input focus: the dictation goes to the clipboard (T13).
            return DictationAccessibilityService.Current is null ? TargetCheck.Unknown : TargetCheck.NoTextField;
        }

        if (node.Password)
        {
            return TargetCheck.PasswordField;
        }

        if (snapshot is null)
        {
            return TargetCheck.Unknown;
        }

        return node.PackageName == snapshot.ProcessName ? TargetCheck.SameWindow : TargetCheck.DifferentWindow;
    }

    /// <summary>A package name as a number that stays the same for the whole run (string hashes are randomized per run, which is fine).</summary>
    private static int StableId(string package) => StringComparer.Ordinal.GetHashCode(package);
}

/// <summary>
/// Puts text at the caret of the focused field: splice it into the field's text with ACTION_SET_TEXT and
/// move the caret after it. When a field refuses that, fall back to the clipboard and ACTION_PASTE.
/// </summary>
public sealed class AccessibilityTextInserter : ITextInserter
{
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromMilliseconds(900);

    private readonly Context _context;
    private readonly IAppLogger _log;

    public AccessibilityTextInserter(Context context, IAppLogger log)
    {
        _context = context;
        _log = log;
    }

    public async Task<InsertResult> InsertAsync(string text, CancellationToken ct)
    {
        AccessibilityNodeInfo? node;
        try
        {
            node = await FocusedField.FindAsync(QueryTimeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return new InsertResult(false, "the focused field did not answer in time");
        }

        if (node is null)
        {
            return new InsertResult(false, DictationAccessibilityService.Current is null
                ? "the accessibility service is off"
                : "no text field has focus");
        }

        if (node.Password)
        {
            return new InsertResult(false, "password field");
        }

        if (node.Editable && TrySetText(node, text))
        {
            return new InsertResult(true);
        }

        return Paste(node, text);
    }

    private bool TrySetText(AccessibilityNodeInfo node, string text)
    {
        // A field that shows its hint reports the hint as its text.
        var current = node.ShowingHintText ? "" : node.TextFormatted?.ToString() ?? "";
        var start = node.TextSelectionStart;
        var end = node.TextSelectionEnd;
        if (start < 0 || end < 0 || start > current.Length || end > current.Length)
        {
            start = end = current.Length;
        }

        if (start > end)
        {
            (start, end) = (end, start);
        }

        // Dictated words should not run into the word before the caret.
        var insert = start > 0 && !char.IsWhiteSpace(current[start - 1]) && NeedsLeadingSpace(text) ? " " + text : text;
        var updated = current[..start] + insert + current[end..];

        var arguments = new Bundle();
        arguments.PutCharSequence(AccessibilityNodeInfo.ActionArgumentSetTextCharsequence, updated);
        if (!node.PerformAction(Android.Views.Accessibility.Action.SetText, arguments))
        {
            return false;
        }

        var caret = start + insert.Length;
        var selection = new Bundle();
        selection.PutInt(AccessibilityNodeInfo.ActionArgumentSelectionStartInt, caret);
        selection.PutInt(AccessibilityNodeInfo.ActionArgumentSelectionEndInt, caret);
        node.PerformAction(Android.Views.Accessibility.Action.SetSelection, selection);

        _log.Info($"Inserted {insert.Length} chars with SET_TEXT into a field of {current.Length} chars.");
        return true;
    }

    private InsertResult Paste(AccessibilityNodeInfo node, string text)
    {
        // Android does not let a background app read the clipboard back, so it cannot be restored here.
        var clipboard = (ClipboardManager?)_context.GetSystemService(Context.ClipboardService);
        if (clipboard is null)
        {
            return new InsertResult(false, "no clipboard");
        }

        clipboard.PrimaryClip = ClipData.NewPlainText("LiveTranscribe", text);
        if (node.PerformAction(Android.Views.Accessibility.Action.Paste))
        {
            _log.Info($"Inserted {text.Length} chars by paste (SET_TEXT refused).");
            return new InsertResult(true);
        }

        return new InsertResult(false, "the field refused both SET_TEXT and paste; the text is on the clipboard");
    }

    private static bool NeedsLeadingSpace(string text) =>
        text.Length > 0 && !char.IsWhiteSpace(text[0]) && ".,;:!?،؛؟)".IndexOf(text[0]) < 0;
}

public sealed class AndroidClipboardWriter : IClipboardWriter
{
    private readonly Context _context;

    public AndroidClipboardWriter(Context context)
    {
        _context = context;
    }

    public void SetText(string text)
    {
        var clipboard = (ClipboardManager)_context.GetSystemService(Context.ClipboardService)!;
        clipboard.PrimaryClip = ClipData.NewPlainText("LiveTranscribe", text);
    }
}
