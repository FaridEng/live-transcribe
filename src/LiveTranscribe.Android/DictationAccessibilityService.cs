using System.Diagnostics;
using Android.AccessibilityServices;
using Android.App;
using Android.Graphics;
using Android.OS;
using Android.Runtime;
using Android.Views.Accessibility;
using LiveTranscribe.Droid.Overlay;

namespace LiveTranscribe.Droid;

/// <summary>The field that last had input focus, as far as the target tracker needs it.</summary>
public sealed record FieldInfo(nint WindowId, string Package, bool Password);

/// <summary>
/// Watches which field has focus and where the keyboard is, shows the bubble for text fields that are
/// not passwords, and gives the inserter and target tracker access to the focused node.
/// </summary>
[Service(Name = "com.livetranscribe.app.DictationAccessibilityService", Label = "LiveTranscribe dictation",
    Permission = "android.permission.BIND_ACCESSIBILITY_SERVICE", Exported = true)]
[IntentFilter(["android.accessibilityservice.AccessibilityService"])]
[MetaData("android.accessibilityservice", Resource = "@xml/accessibility_service_config")]
public sealed class DictationAccessibilityService : AccessibilityService
{
    // Node queries are IPC into the other app; a hung app must not freeze the bubble.
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromMilliseconds(400);
    // A slow answer is still used when it comes, up to this long; after that the look is retried.
    private static readonly TimeSpan LateAnswerLimit = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(300);
    private const int MaxRetries = 3;
    // Focus and window events get a quick look. Content changes stream in while an app animates or a chat
    // reply arrives, so they wait longer; they matter for fields that gain focus without a focus event.
    private static readonly TimeSpan FocusDelay = TimeSpan.FromMilliseconds(60);
    private static readonly TimeSpan ContentDelay = TimeSpan.FromMilliseconds(250);

    private readonly Handler _ui = new(Looper.MainLooper!);
    private Java.Lang.Runnable? _evaluate;
    private BubbleOverlay? _overlay;
    private AppHost? _host;
    private int _evaluation;
    private bool _pending;
    private long _pendingDue;
    // The focused node seen while the keyboard was up, so the bubble stays on that field after the keyboard
    // is hidden even when the app does not mark it editable.
    private AccessibilityNodeInfo? _keyboardField;
    private string? _lastLogged;

    public static DictationAccessibilityService? Current { get; private set; }

    public FieldInfo? LastField { get; private set; }

    protected override void OnServiceConnected()
    {
        base.OnServiceConnected();
        Current = this;
        _host = AppHost.Get(this);
        _host.Log.Info("Accessibility service connected.");
        _evaluate = new Java.Lang.Runnable(() =>
        {
            _pending = false;
            _ = EvaluateAsync(0);
        });
        _overlay = new BubbleOverlay(this, _host);
        _ = EvaluateAsync(0);
    }

    public override void OnAccessibilityEvent(AccessibilityEvent? e)
    {
        if (_evaluate is null)
        {
            return;
        }

        // Events arrive in bursts. The first one schedules a look and later ones join it, so a steady stream
        // of content changes cannot keep pushing the look back.
        var delay = (long)(e?.EventType == EventTypes.WindowContentChanged ? ContentDelay : FocusDelay).TotalMilliseconds;
        var due = SystemClock.UptimeMillis() + delay;
        if (_pending && _pendingDue <= due)
        {
            return;
        }

        _ui.RemoveCallbacks(_evaluate);
        _ui.PostDelayed(_evaluate, delay);
        _pending = true;
        _pendingDue = due;
    }

    public override void OnInterrupt()
    {
    }

    public override bool OnUnbind(Android.Content.Intent? intent)
    {
        Shutdown();
        return base.OnUnbind(intent);
    }

    public override void OnDestroy()
    {
        Shutdown();
        base.OnDestroy();
    }

    private void Shutdown()
    {
        if (Current == this)
        {
            Current = null;
        }

        _overlay?.Remove();
        _overlay = null;
        _evaluate = null;
        _host?.Log.Info("Accessibility service disconnected.");
    }

    private async Task EvaluateAsync(int attempt)
    {
        var version = ++_evaluation;
        var previous = _keyboardField;
        var started = Stopwatch.GetTimestamp();
        var look = Task.Run(Look);
        var finished = await Task.WhenAny(look, Task.Delay(QueryTimeout));
        if (version != _evaluation || _overlay is null)
        {
            // A newer event already asked again.
            return;
        }

        if (finished != look)
        {
            // Unknown: the bubble keeps its current state (the inserter checks for a password field itself),
            // and the answer is still used if it arrives late. Hiding here left the bubble hidden in busy apps,
            // because no further event came once the keyboard was up.
            _host?.Log.Info($"Focused-field lookup passed {QueryTimeout.TotalMilliseconds:0} ms (attempt {attempt + 1}).");
            finished = await Task.WhenAny(look, Task.Delay(LateAnswerLimit - QueryTimeout));
            if (version != _evaluation || _overlay is null)
            {
                return;
            }

            if (finished != look)
            {
                if (attempt < MaxRetries)
                {
                    _ui.PostDelayed(() =>
                    {
                        if (version == _evaluation)
                        {
                            _ = EvaluateAsync(attempt + 1);
                        }
                    }, (long)RetryDelay.TotalMilliseconds);
                }

                return;
            }
        }

        var seen = await look;
        var keyboard = seen.KeyboardTop is not null;
        var node = seen.Node;

        // A field the app marks editable counts with or without the keyboard. While the keyboard is up, any
        // focused non-password node counts too (WebView and custom fields often report neither focus nor
        // editable), and that node keeps the bubble after the keyboard is hidden until focus moves off it.
        bool show;
        if (node is null)
        {
            show = keyboard;
        }
        else if (node.Password)
        {
            show = false;
        }
        else
        {
            show = node.Editable || keyboard || (previous is not null && node.Equals(previous));
        }

        _keyboardField = keyboard
            ? (node is { Password: false } ? node : null)
            : (show ? previous : null);

        if (node is not null && seen.Field is not null && (node.Editable || show))
        {
            LastField = seen.Field;
        }

        LogChange(seen, show, Stopwatch.GetElapsedTime(started));
        _overlay.Update(show, seen.KeyboardTop);
    }

    // Package names, flags and timings only: never the field's text (FR-11.5).
    private void LogChange(Observation seen, bool show, TimeSpan elapsed)
    {
        var node = seen.Node;
        var line = $"package={seen.Field?.Package ?? "-"} focused={node is not null} editable={node?.Editable == true} " +
                   $"password={node?.Password == true} keyboard={seen.KeyboardTop is not null} bubble={show}";
        if (line == _lastLogged)
        {
            return;
        }

        _lastLogged = line;
        _host?.Log.Info($"Field check: {line} ({elapsed.TotalMilliseconds:0} ms).");
    }

    private sealed record Observation(AccessibilityNodeInfo? Node, FieldInfo? Field, int? KeyboardTop);

    private Observation Look()
    {
        int? keyboardTop = null;
        foreach (var window in Windows ?? [])
        {
            if (window.Type == AccessibilityWindowType.InputMethod)
            {
                var bounds = new Rect();
                window.GetBoundsInScreen(bounds);
                keyboardTop = bounds.Top;
            }
        }

        var node = FindFocus(NodeFocus.Input);
        return node is null
            ? new Observation(null, null, keyboardTop)
            : new Observation(node, new FieldInfo(node.WindowId, node.PackageName ?? "", node.Password), keyboardTop);
    }
}
