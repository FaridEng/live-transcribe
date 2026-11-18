using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;

namespace LiveTranscribe.Droid;

/// <summary>
/// A microphone foreground service that runs only while the bubble is listening. Android silences the
/// microphone of an app with nothing in the foreground, and the notification also shows the user that
/// the microphone is live (FR-6.2, FR-11.1).
/// </summary>
[Service(Exported = false, ForegroundServiceType = ForegroundService.TypeMicrophone)]
public sealed class RecordingService : Service
{
    private const string ChannelId = "listening";
    private const int NotificationId = 1;

    public static void Start(Context context)
    {
        try
        {
            context.StartForegroundService(new Intent(context, typeof(RecordingService)));
        }
        catch (Exception ex)
        {
            // Android 12+ can refuse a start from the background. Recording is still tried; if the
            // microphone is silenced, the controller's silence notice says so.
            AppHost.Get(context).Log.Warn($"Microphone foreground service refused: {ex.GetType().Name}.");
        }
    }

    public static void Stop(Context context) => context.StopService(new Intent(context, typeof(RecordingService)));

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        var manager = (NotificationManager)GetSystemService(NotificationService)!;
        manager.CreateNotificationChannel(new NotificationChannel(ChannelId, "Listening", NotificationImportance.Low)
        {
            Description = "Shown while LiveTranscribe is using the microphone."
        });

        var notification = new Notification.Builder(this, ChannelId)
            .SetContentTitle("LiveTranscribe is listening")
            .SetSmallIcon(Android.Resource.Drawable.PresenceAudioOnline)
            .SetOngoing(true)
            .Build();

        try
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(30))
            {
                StartForeground(NotificationId, notification, ForegroundService.TypeMicrophone);
            }
            else
            {
                StartForeground(NotificationId, notification);
            }
        }
        catch (Exception ex)
        {
            AppHost.Get(this).Log.Warn($"Could not enter the foreground: {ex.GetType().Name}.");
            StopSelf();
        }

        return StartCommandResult.NotSticky;
    }
}
