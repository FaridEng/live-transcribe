using System;
using System.Windows;
using System.Windows.Media.Animation;
using LiveTranscribe.App.Interop;

namespace LiveTranscribe.App.Indicator;

/// <summary>
/// Minimal "Listening" pill shown while the microphone is live and the floating control is turned off,
/// so recording is never invisible (FR-6.3, FR-11.1). Click-through and never focused.
/// </summary>
public partial class ListeningIndicatorWindow : Window
{
    public ListeningIndicatorWindow()
    {
        InitializeComponent();
        NoActivateWindow.Apply(this, clickThrough: true);
        SizeChanged += (_, _) => PlaceTopCentre();
    }

    public void ShowListening()
    {
        if (!IsVisible)
        {
            Show();
        }

        PlaceTopCentre();
        var blink = new DoubleAnimation(1, 0.35, new Duration(TimeSpan.FromMilliseconds(700)))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever
        };
        Dot.BeginAnimation(OpacityProperty, blink);
    }

    public void HideListening()
    {
        Dot.BeginAnimation(OpacityProperty, null);
        Hide();
    }

    private void PlaceTopCentre()
    {
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Left + (workArea.Width - ActualWidth) / 2;
        Top = workArea.Top + 12;
    }
}
