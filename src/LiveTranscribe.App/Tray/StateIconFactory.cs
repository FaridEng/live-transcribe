using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using LiveTranscribe.Core.Dictation;

namespace LiveTranscribe.App.Tray;

/// <summary>
/// Draws the tray icon per state. The shape differs as well as the colour, so the state is
/// readable without relying on colour alone (FR-6.2).
/// </summary>
internal static class StateIconFactory
{
    public static Icon Create(DictationState state)
    {
        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            var (fill, shape) = Describe(state);
            using var brush = new SolidBrush(fill);
            using var pen = new Pen(fill, 3f);

            switch (shape)
            {
                case IconShape.FilledCircle:
                    g.FillEllipse(brush, 4, 4, 24, 24);
                    break;
                case IconShape.Square:
                    g.FillRectangle(brush, 6, 6, 20, 20);
                    break;
                case IconShape.Triangle:
                    g.FillPolygon(brush, new[] { new Point(16, 4), new Point(29, 27), new Point(3, 27) });
                    break;
                default:
                    g.DrawEllipse(pen, 5, 5, 22, 22);
                    break;
            }
        }

        var handle = bitmap.GetHicon();
        try
        {
            // Clone so the icon survives DestroyIcon on the temporary handle.
            using var temp = Icon.FromHandle(handle);
            return (Icon)temp.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    private static (Color Fill, IconShape Shape) Describe(DictationState state) => state switch
    {
        DictationState.Listening => (Color.FromArgb(220, 60, 60), IconShape.FilledCircle),
        DictationState.Processing => (Color.FromArgb(230, 160, 40), IconShape.Ring),
        DictationState.Paused => (Color.FromArgb(120, 120, 120), IconShape.Square),
        DictationState.NotConfigured => (Color.FromArgb(150, 150, 150), IconShape.Triangle),
        _ when state.IsError() => (Color.FromArgb(200, 70, 30), IconShape.Triangle),
        _ => (Color.FromArgb(60, 130, 200), IconShape.Ring)
    };

    private enum IconShape
    {
        Ring,
        FilledCircle,
        Square,
        Triangle
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);
}
