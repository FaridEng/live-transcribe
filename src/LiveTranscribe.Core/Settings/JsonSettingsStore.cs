using System.Text.Json;
using System.Text.Json.Serialization;
using LiveTranscribe.Core.Diagnostics;

namespace LiveTranscribe.Core.Settings;

public sealed class JsonSettingsStore : ISettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _path;
    private readonly IAppLogger _log;

    public JsonSettingsStore(string path, IAppLogger log)
    {
        _path = path;
        _log = log;
        Current = Load();
    }

    public AppSettings Current { get; private set; }

    public event Action<AppSettings>? Changed;

    public void Save(AppSettings settings)
    {
        AppPaths.EnsureCreated();
        var json = JsonSerializer.Serialize(settings, Options);

        // Write through a temp file so a crash mid-write cannot leave truncated settings.
        var temp = _path + ".tmp";
        File.WriteAllText(temp, json);
        File.Move(temp, _path, overwrite: true);

        Current = settings;
        _log.Info("Settings saved.");
        Changed?.Invoke(settings);
    }

    private AppSettings Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                var defaults = AppSettings.CreateDefault();
                Save(defaults);
                return defaults;
            }

            var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), Options);
            if (loaded is not null)
            {
                return Migrate(loaded);
            }

            _log.Warn("Settings file was empty; using defaults.");
        }
        catch (Exception ex)
        {
            // Never start without settings: keep the broken file for inspection and carry on.
            _log.Error("Settings file unreadable; using defaults.", ex);
            TryBackupCorruptFile();
        }

        return AppSettings.CreateDefault();
    }

    /// <summary>
    /// Brings a stored file up to <see cref="AppSettings.CurrentVersion"/>. Each step runs once and
    /// only where the old default was itself the problem, so a deliberate choice is never overwritten.
    /// </summary>
    private AppSettings Migrate(AppSettings settings)
    {
        if (settings.Version >= AppSettings.CurrentVersion)
        {
            return settings;
        }

        if (settings.Version < 2 && settings.Dictation.StopOnSilence)
        {
            // Version 1 shipped this on, and it ended hands-free dictation mid-sentence on a quiet
            // microphone. It stays in Settings for anyone who wants it back.
            settings.Dictation.StopOnSilence = false;
            _log.Info("Settings migrated to version 2: stop-on-silence turned off.");
        }

        settings.Version = AppSettings.CurrentVersion;
        Save(settings);
        return settings;
    }

    private void TryBackupCorruptFile()
    {
        try
        {
            if (File.Exists(_path))
            {
                File.Move(_path, _path + $".corrupt-{DateTime.Now:yyyyMMdd-HHmmss}", overwrite: true);
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"Could not back up the unreadable settings file: {ex.Message}");
        }
    }
}
