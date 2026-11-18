using System;
using System.Drawing;
using System.Windows.Forms;
using LiveTranscribe.Core.Diagnostics;
using LiveTranscribe.Core.Dictation;

namespace LiveTranscribe.App.Tray;

/// <summary>Tray presence: the current state plus the menu required by FR-2.13.</summary>
public sealed class TrayIconHost : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _stateItem;
    private readonly ToolStripMenuItem _pauseItem;
    private readonly ToolStripMenuItem _startStopItem;
    private readonly ToolStripMenuItem _retryItem;
    private readonly ToolStripMenuItem _copyLastItem;
    private readonly IAppLogger _log;
    private Icon? _currentIcon;

    public TrayIconHost(IAppLogger log)
    {
        _log = log;

        _stateItem = new ToolStripMenuItem("Starting...") { Enabled = false };
        _pauseItem = new ToolStripMenuItem("Pause dictation", null, (_, _) => PauseToggled?.Invoke());
        _startStopItem = new ToolStripMenuItem("Start dictation", null, (_, _) => StartStopRequested?.Invoke());

        // Also here, because the floating control may be turned off (FR-9.1).
        _retryItem = new ToolStripMenuItem("Retry last dictation", null, (_, _) => RetryRequested?.Invoke()) { Visible = false };

        // Copy rather than paste: opening this menu moves the focus away from the text field. Pasting
        // is on the shortcut and the pill menu, which leave the focus where it is (FR-9.3).
        _copyLastItem = new ToolStripMenuItem("Copy last dictation", null, (_, _) => CopyLastRequested?.Invoke()) { Visible = false };

        var menu = new ContextMenuStrip();
        menu.Items.Add(_stateItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_startStopItem);
        menu.Items.Add(_retryItem);
        menu.Items.Add(_copyLastItem);
        menu.Items.Add(_pauseItem);
        menu.Items.Add(new ToolStripMenuItem("Settings...", null, (_, _) => SettingsRequested?.Invoke()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Quit", null, (_, _) => QuitRequested?.Invoke()));

        _icon = new NotifyIcon
        {
            Text = "LiveTranscribe",
            ContextMenuStrip = menu,
            Visible = true
        };
        _icon.DoubleClick += (_, _) => SettingsRequested?.Invoke();

        SetState(DictationState.NotConfigured);
    }

    public event Action? SettingsRequested;
    public event Action? QuitRequested;
    public event Action? PauseToggled;
    public event Action? StartStopRequested;
    public event Action? RetryRequested;
    public event Action? CopyLastRequested;

    public DictationState State { get; private set; } = DictationState.NotConfigured;

    public void SetState(DictationState state)
    {
        State = state;
        var text = state.ToDisplayText();

        _stateItem.Text = text;
        _icon.Text = $"LiveTranscribe - {text}";
        _pauseItem.Text = state == DictationState.Paused ? "Resume dictation" : "Pause dictation";
        _startStopItem.Text = state == DictationState.Listening ? "Stop dictation" : "Start dictation";
        _startStopItem.Enabled = state is not (DictationState.Paused or DictationState.NotConfigured or DictationState.Processing);

        var previous = _currentIcon;
        _currentIcon = StateIconFactory.Create(state);
        _icon.Icon = _currentIcon;
        previous?.Dispose();

        _log.Info($"State -> {text}");
    }

    /// <summary>Shows the retry item while kept audio can be sent again.</summary>
    public void SetRetryAvailable(bool available) => _retryItem.Visible = available;

    /// <summary>Shows the copy item while the latest dictation is still in memory.</summary>
    public void SetCopyLastAvailable(bool available) => _copyLastItem.Visible = available;

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _currentIcon?.Dispose();
    }
}
