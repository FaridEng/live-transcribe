using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace LiveTranscribe.App.Interop;

/// <summary>
/// Makes a WPF window a tool window that never takes activation or keyboard focus, so clicking it
/// leaves the caret in the target field (FR-2.8).
/// </summary>
internal static class NoActivateWindow
{
    private const int GwlExStyle = -20;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExTopmost = 0x00000008;
    private const int WsExTransparent = 0x00000020;
    private const int WmMouseActivate = 0x0021;
    private const int WmGetMinMaxInfo = 0x0024;
    private const int MaNoActivate = 3;

    public static void Apply(Window window, bool clickThrough = false)
    {
        window.ShowActivated = false;
        window.Focusable = false;
        window.ShowInTaskbar = false;
        window.Topmost = true;

        window.SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(window).Handle;
            var style = GetWindowLongPtr(handle, GwlExStyle).ToInt64();
            style |= WsExNoActivate | WsExToolWindow | WsExTopmost;
            if (clickThrough)
            {
                style |= WsExTransparent;
            }

            SetWindowLongPtr(handle, GwlExStyle, new IntPtr(style));
            HwndSource.FromHwnd(handle)?.AddHook(WindowHook);
            KeepOnTop(window, handle);
        };
    }

    private const uint EventSystemForeground = 0x0003;
    private const uint EventObjectLocationChange = 0x800B;
    private const uint WinEventOutOfContext = 0x0000;
    private const int ObjIdWindow = 0;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoOwnerZOrder = 0x0200;
    private static readonly IntPtr HwndTopmost = new(-1);

    private static readonly List<IntPtr> Kept = [];
    private static WinEventProc? _onWinEvent;
    private static DispatcherTimer? _safetyTimer;
    private static bool _raisePending;

    /// <summary>
    /// A full-screen window (a web page using the Fullscreen API, a player) can end up above a topmost
    /// window, and the last topmost window raised wins. So the window is raised again, without
    /// activation, whenever the foreground window changes or moves, and every two seconds as a backstop.
    /// Exclusive-mode full-screen games cannot be drawn over at all.
    /// </summary>
    private static void KeepOnTop(Window window, IntPtr handle)
    {
        Kept.Add(handle);
        window.Closed += (_, _) => Kept.Remove(handle);

        if (_onWinEvent is not null)
        {
            return;
        }

        // Out-of-context hooks are delivered on this (UI) thread through its message loop.
        _onWinEvent = OnWinEvent;
        SetWinEventHook(EventSystemForeground, EventSystemForeground, IntPtr.Zero, _onWinEvent, 0, 0, WinEventOutOfContext);
        SetWinEventHook(EventObjectLocationChange, EventObjectLocationChange, IntPtr.Zero, _onWinEvent, 0, 0, WinEventOutOfContext);

        _safetyTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
        _safetyTimer.Tick += (_, _) => RaiseAll();
        _safetyTimer.Start();
    }

    private static void OnWinEvent(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != ObjIdWindow || Kept.Contains(hwnd))
        {
            return;
        }

        // Location changes fire for every window; only the foreground one can cover the pill.
        if (eventType == EventObjectLocationChange && hwnd != GetForegroundWindow())
        {
            return;
        }

        if (_raisePending)
        {
            return;
        }

        _raisePending = true;
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _raisePending = false;
            RaiseAll();
        });
    }

    private static void RaiseAll()
    {
        foreach (var handle in Kept)
        {
            SetWindowPos(handle, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate | SwpNoOwnerZOrder);
        }
    }

    private delegate void WinEventProc(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module, WinEventProc callback,
        uint processId, uint threadId, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    private static IntPtr WindowHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (message)
        {
            case WmMouseActivate:
                handled = true;
                return new IntPtr(MaNoActivate);

            case WmGetMinMaxInfo:
                // Windows enforces a minimum window width of ~130 px. Without this the small pill
                // sits off-centre inside an oversized window and appears to jump when it grows.
                var info = Marshal.PtrToStructure<MinMaxInfo>(lParam);
                info.MinTrackSize = new PointStruct { X = 1, Y = 1 };
                Marshal.StructureToPtr(info, lParam, false);
                handled = true;
                return IntPtr.Zero;

            default:
                return IntPtr.Zero;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PointStruct
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public PointStruct Reserved;
        public PointStruct MaxSize;
        public PointStruct MaxPosition;
        public PointStruct MinTrackSize;
        public PointStruct MaxTrackSize;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
}
