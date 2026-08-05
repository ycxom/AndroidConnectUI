using System.IO;
using System.Text.Json;

namespace AndroidConnectUI;

internal static class AppPreferences
{
    private static readonly object SyncRoot = new();
    private static readonly string PreferencesPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AndroidConnectUI",
        "preferences.json");
    private static PreferenceData _data = Load();

    public static bool WindowAnimationsEnabled
    {
        get
        {
            lock (SyncRoot)
                return _data.WindowAnimationsEnabled;
        }
        set
        {
            lock (SyncRoot)
            {
                if (_data.WindowAnimationsEnabled == value)
                    return;

                _data.WindowAnimationsEnabled = value;
                Save();
            }
        }
    }

    private static PreferenceData Load()
    {
        try
        {
            if (File.Exists(PreferencesPath))
                return JsonSerializer.Deserialize<PreferenceData>(File.ReadAllText(PreferencesPath))
                    ?? new PreferenceData();
        }
        catch
        {
            // A damaged or inaccessible preference file must not prevent startup.
        }

        return new PreferenceData();
    }

    private static void Save()
    {
        try
        {
            string? directory = Path.GetDirectoryName(PreferencesPath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);
            File.WriteAllText(PreferencesPath, JsonSerializer.Serialize(_data));
        }
        catch
        {
            // Keep the in-memory preference if persistence is unavailable.
        }
    }

    private sealed class PreferenceData
    {
        public bool WindowAnimationsEnabled { get; set; } = true;
    }
}
