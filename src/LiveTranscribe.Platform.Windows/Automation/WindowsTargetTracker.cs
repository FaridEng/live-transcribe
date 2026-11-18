using System.Diagnostics;
using System.Runtime.InteropServices;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;
using LiveTranscribe.Core.Abstractions;
using LiveTranscribe.Core.Diagnostics;

namespace LiveTranscribe.Platform.Windows.Automation;

/// <summary>
/// Identifies the dictation target (architecture §4.3). Window identity uses plain Win32, which is
/// fast enough for the activation path; password detection needs UI Automation, which can be slow or
/// hang in some apps (risk T7), so every UIA call runs on a worker thread behind a timeout and a
/// timeout is reported as "not a password field" only after it has been logged, and as unknown otherwise.
/// </summary>
public sealed class WindowsTargetTracker : ITargetTracker, IDisposable
{
    private readonly IAppLogger _log;
    private readonly int _ownProcessId = Environment.ProcessId;
    private readonly SemaphoreSlim _uiaLock = new(1, 1);

    private UIA3Automation? _automation;
    private bool _uiaUnavailable;

    public WindowsTargetTracker(IAppLogger log)
    {
        _log = log;
    }

    public TargetSnapshot? Capture()
    {
        var window = GetForegroundWindow();
        if (window == IntPtr.Zero)
        {
            return null;
        }

        _ = GetWindowThreadProcessId(window, out var processId);
        if (processId == 0 || processId == _ownProcessId)
        {
            // Started from our own UI (the tray menu takes focus), so there is nothing to lock on to.
            return null;
        }

        return new TargetSnapshot(window, (int)processId, ProcessName((int)processId));
    }

    /// <summary>
    /// Builds the UI Automation client in the background at startup. Without it the first password
    /// check would spend its whole budget on COM initialization and time out (FR-3.7).
    /// </summary>
    public void Warmup() =>
        _ = RunUiaAsync(automation => automation.FocusedElement() is not null, false, TimeSpan.FromSeconds(10), CancellationToken.None);

    public Task<bool> IsPasswordFieldAsync(TimeSpan timeout, CancellationToken ct) =>
        RunUiaAsync(automation => automation.FocusedElement().Properties.IsPassword.ValueOrDefault, false, timeout, ct);

    public async Task<TargetCheck> CheckAsync(TargetSnapshot? snapshot, TimeSpan timeout, CancellationToken ct)
    {
        var focus = await RunUiaAsync(ReadFocus, FocusKind.Unknown, timeout, ct).ConfigureAwait(false);
        if (focus == FocusKind.Password)
        {
            return TargetCheck.PasswordField;
        }

        if (snapshot is null)
        {
            return focus == FocusKind.NotText ? TargetCheck.NoTextField : TargetCheck.Unknown;
        }

        var current = GetForegroundWindow();
        if (current == IntPtr.Zero)
        {
            return TargetCheck.DifferentWindow;
        }

        // Our own floating control never activates, so the foreground window is still the user's app.
        if (current != snapshot.Window)
        {
            return TargetCheck.DifferentWindow;
        }

        return focus == FocusKind.NotText ? TargetCheck.NoTextField : TargetCheck.SameWindow;
    }

    private enum FocusKind { Unknown, Password, Text, NotText }

    /// <summary>
    /// What has keyboard focus. Anything that looks like it could take text counts as text, so NotText is only
    /// reported for things like the desktop, File Explorer's file list or a button (T13). Apps that take a paste
    /// without exposing a text field still get the paste; the controller only adds the clipboard copy.
    /// </summary>
    private static FocusKind ReadFocus(UIA3Automation automation)
    {
        var element = automation.FocusedElement();
        if (element is null)
        {
            return FocusKind.NotText;
        }

        if (element.Properties.IsPassword.ValueOrDefault)
        {
            return FocusKind.Password;
        }

        if (element.Properties.ControlType.ValueOrDefault is ControlType.Edit or ControlType.Document or ControlType.ComboBox)
        {
            return FocusKind.Text;
        }

        if (element.Patterns.Value.IsSupported && !element.Patterns.Value.Pattern.IsReadOnly.ValueOrDefault)
        {
            return FocusKind.Text;
        }

        return element.Patterns.Text.IsSupported ? FocusKind.Text : FocusKind.NotText;
    }

    public void Dispose()
    {
        _automation?.Dispose();
        _automation = null;
        _uiaLock.Dispose();
    }

    /// <summary>
    /// Runs one UI Automation query on a worker thread. A query that outlives the timeout is
    /// abandoned rather than awaited, because a hung UIA call can never be cancelled.
    /// </summary>
    private async Task<T> RunUiaAsync<T>(Func<UIA3Automation, T> query, T fallback, TimeSpan timeout, CancellationToken ct)
    {
        if (_uiaUnavailable || ct.IsCancellationRequested)
        {
            return fallback;
        }

        var work = Task.Run(() =>
        {
            _uiaLock.Wait(ct);
            try
            {
                var automation = _automation ??= new UIA3Automation();
                return query(automation);
            }
            finally
            {
                _uiaLock.Release();
            }
        }, ct);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var finished = await Task.WhenAny(work, Task.Delay(timeout, deadline.Token)).ConfigureAwait(false);
        deadline.Cancel();

        if (finished != work)
        {
            if (!ct.IsCancellationRequested)
            {
                _log.Warn($"UI Automation did not answer within {timeout.TotalMilliseconds:F0} ms; treating the target as unknown.");
            }

            return fallback;
        }

        try
        {
            return await work.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return fallback;
        }
        catch (Exception ex)
        {
            // Elements disappear mid-query and some apps expose no automation peer at all.
            _log.Warn($"UI Automation query failed: {ex.Message}");
            if (ex is COMException { ErrorCode: unchecked((int)0x80040154) } or TypeInitializationException)
            {
                _uiaUnavailable = true;
                _log.Error("UI Automation is unavailable; password fields cannot be detected.", ex);
            }

            return fallback;
        }
    }

    private string ProcessName(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.ProcessName;
        }
        catch (Exception ex)
        {
            _log.Warn($"Could not read the target process name: {ex.Message}");
            return "unknown";
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
}
