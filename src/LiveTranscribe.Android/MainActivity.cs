using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Provider;
using Android.Text;
using Android.Views;
using Android.Widget;
using LiveTranscribe.Core.Engines;
using LiveTranscribe.Core.Settings;

namespace LiveTranscribe.Droid;

/// <summary>
/// Setup and settings: the three switches the bubble needs (accessibility service, microphone, API key),
/// the models, and a field to try the bubble in. Built in code, so there are no layout files to keep in step.
/// </summary>
[Activity(Label = "LiveTranscribe", MainLauncher = true, Exported = true,
    WindowSoftInputMode = SoftInput.AdjustResize)]
public sealed class MainActivity : Activity
{
    private const int MicrophoneRequest = 1;

    private AppHost _host = null!;
    private TextView _serviceStatus = null!;
    private Button _serviceButton = null!;
    private TextView _microphoneStatus = null!;
    private Button _microphoneButton = null!;
    private TextView _keyStatus = null!;
    private EditText _key = null!;
    private EditText _dictationModel = null!;
    private EditText _translationModel = null!;
    private TextView _testResult = null!;
    private RadioButton _languageAuto = null!;
    private RadioButton _languagePersian = null!;
    private TextView _languageNote = null!;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        _host = AppHost.Get(this);

        var page = new LinearLayout(this) { Orientation = Orientation.Vertical };
        page.SetPadding(Px(20), Px(16), Px(20), Px(32));

        Heading(page, "1. Accessibility service");
        _serviceStatus = Line(page, "");
        _serviceButton = ButtonRow(page, "Open accessibility settings", () =>
            StartActivity(new Intent(Settings.ActionAccessibilitySettings)));
        Line(page, "Turn on \"LiveTranscribe dictation\". On Android 13 and later, a sideloaded app's switch is " +
                   "greyed out at first: open Settings → Apps → LiveTranscribe → ⋮ → \"Allow restricted settings\", then try again.",
            small: true);

        Heading(page, "2. Microphone");
        _microphoneStatus = Line(page, "");
        _microphoneButton = ButtonRow(page, "Allow microphone", RequestMicrophone);

        Heading(page, "3. Gemini API key");
        _keyStatus = Line(page, "");
        _key = new EditText(this)
        {
            Hint = "Paste the key here",
            InputType = InputTypes.ClassText | InputTypes.TextVariationPassword
        };
        page.AddView(_key);
        var keyButtons = Row(page);
        AddButton(keyButtons, "Save key", SaveKey);
        AddButton(keyButtons, "Test connection", () => _ = TestAsync());
        _testResult = Line(page, "", small: true);

        Heading(page, "Spoken language");
        var languages = new RadioGroup(this) { Orientation = Orientation.Vertical };
        _languageAuto = new RadioButton(this) { Text = "Auto (detect Persian / English)", Id = Android.Views.View.GenerateViewId() };
        _languagePersian = new RadioButton(this) { Text = "Persian only (فارسی)", Id = Android.Views.View.GenerateViewId() };
        languages.AddView(_languageAuto);
        languages.AddView(_languagePersian);
        page.AddView(languages);
        _languageNote = Line(page, "", small: true);
        _languageAuto.Click += (_, _) => SaveLanguage(LanguageMode.Auto);
        _languagePersian.Click += (_, _) => SaveLanguage(LanguageMode.Persian);

        Heading(page, "Models");
        Line(page, "Dictation model (Live):", small: true);
        _dictationModel = new EditText(this) { InputType = InputTypes.ClassText | InputTypes.TextFlagNoSuggestions };
        page.AddView(_dictationModel);
        Line(page, "Translation model (English mode):", small: true);
        _translationModel = new EditText(this) { InputType = InputTypes.ClassText | InputTypes.TextFlagNoSuggestions };
        page.AddView(_translationModel);
        AddButton(Row(page), "Save models", SaveModels);

        Heading(page, "Try it");
        Line(page, "Tap the field below. The button appears above the keyboard: tap to talk, ✓ to insert, ✕ to cancel. " +
                   "Long-press it for فارسی / English; you can slide onto an option and let go. Drag it to move it.",
            small: true);
        var trial = new EditText(this) { Hint = "Dictate here", Gravity = GravityFlags.Top | GravityFlags.Start };
        trial.SetMinLines(3);
        page.AddView(trial);

        var scroll = new ScrollView(this);
        scroll.AddView(page);
        SetContentView(scroll);
    }

    protected override void OnResume()
    {
        base.OnResume();
        Refresh();
    }

    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        Refresh();
    }

    private void Refresh()
    {
        var serviceOn = DictationAccessibilityService.Current is not null;
        _serviceStatus.Text = serviceOn ? "✓ On" : "Off - the button cannot appear until this is on.";
        _serviceButton.Enabled = true;

        var microphone = CheckSelfPermission(Android.Manifest.Permission.RecordAudio) == Permission.Granted;
        _microphoneStatus.Text = microphone ? "✓ Allowed" : "Not allowed yet.";
        _microphoneButton.Visibility = microphone ? ViewStates.Gone : ViewStates.Visible;

        var profile = _host.Settings.Current.ActiveProfile;
        var hasKey = profile is not null && _host.Secrets.Has(profile.SecretRef);
        _keyStatus.Text = hasKey
            ? "✓ A key is stored, encrypted with this phone's keystore. Paste a new one to replace it."
            : "No key yet. It stays on this phone, encrypted; it is never shown or logged.";

        _dictationModel.Text = profile?.ModelId ?? "";
        _translationModel.Text = _host.Settings.Current.Translation.ModelId;
        ShowLanguage(_host.Settings.Current.Dictation.Language);
        _host.Dictation.RefreshConfiguration();
    }

    private void ShowLanguage(LanguageMode language)
    {
        _languageAuto.Checked = language == LanguageMode.Auto;
        _languagePersian.Checked = language == LanguageMode.Persian;
        _languageNote.Text = LanguageModes.Describe(language);
    }

    /// <summary>Saved on tap; the next dictation picks it up, a running one keeps what it started with.</summary>
    private void SaveLanguage(LanguageMode language)
    {
        var settings = _host.Settings.Current;
        settings.Dictation.Language = language;
        _host.Settings.Save(settings);
        ShowLanguage(language);
    }

    private void RequestMicrophone()
    {
        var wanted = OperatingSystem.IsAndroidVersionAtLeast(33)
            ? new[] { Android.Manifest.Permission.RecordAudio, Android.Manifest.Permission.PostNotifications }
            : new[] { Android.Manifest.Permission.RecordAudio };
        RequestPermissions(wanted, MicrophoneRequest);
    }

    private void SaveKey()
    {
        var key = _key.Text?.Trim() ?? "";
        var profile = _host.Settings.Current.ActiveProfile;
        if (key.Length == 0 || profile is null)
        {
            return;
        }

        _host.Secrets.Set(profile.SecretRef, key);
        _key.Text = "";
        Toast.MakeText(this, "Key saved.", ToastLength.Short)!.Show();
        Refresh();
    }

    private void SaveModels()
    {
        var settings = _host.Settings.Current;
        if (settings.ActiveProfile is { } profile && _dictationModel.Text?.Trim() is { Length: > 0 } dictation)
        {
            profile.ModelId = dictation;
        }

        if (_translationModel.Text?.Trim() is { Length: > 0 } translation)
        {
            settings.Translation.ModelId = translation;
        }

        _host.Settings.Save(settings);
        Toast.MakeText(this, "Models saved.", ToastLength.Short)!.Show();
    }

    private async Task TestAsync()
    {
        var profile = _host.Settings.Current.ActiveProfile;
        var key = profile is null ? null : _host.Secrets.Get(profile.SecretRef);
        if (profile is null || string.IsNullOrWhiteSpace(key))
        {
            _testResult.Text = "Save a key first.";
            return;
        }

        _testResult.Text = "Testing…";
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var report = await _host.Engines.Create(profile, key).TestAsync(timeout.Token);
            _testResult.Text = $"✓ {report.Summary} ({report.RoundTrip.TotalMilliseconds:F0} ms)";
        }
        catch (EngineException ex)
        {
            _testResult.Text = $"✗ {ex.Kind}: {ex.Message}";
        }
        catch (Exception ex)
        {
            _testResult.Text = $"✗ {ex.GetType().Name}";
        }
    }

    private void Heading(LinearLayout page, string text)
    {
        var view = new TextView(this) { Text = text };
        view.SetTextSize(Android.Util.ComplexUnitType.Sp, 18);
        view.SetTypeface(null, Android.Graphics.TypefaceStyle.Bold);
        view.SetPadding(0, Px(20), 0, Px(4));
        page.AddView(view);
    }

    private TextView Line(LinearLayout page, string text, bool small = false)
    {
        var view = new TextView(this) { Text = text };
        view.SetTextSize(Android.Util.ComplexUnitType.Sp, small ? 13 : 15);
        view.SetPadding(0, Px(2), 0, Px(2));
        page.AddView(view);
        return view;
    }

    private LinearLayout Row(LinearLayout page)
    {
        var row = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        page.AddView(row);
        return row;
    }

    private Button ButtonRow(LinearLayout page, string text, Action action) => AddButton(Row(page), text, action);

    private Button AddButton(LinearLayout row, string text, Action action)
    {
        var button = new Button(this) { Text = text };
        button.Click += (_, _) => action();
        row.AddView(button);
        return button;
    }

    private int Px(float dp) => (int)(dp * Resources!.DisplayMetrics!.Density + 0.5f);
}
