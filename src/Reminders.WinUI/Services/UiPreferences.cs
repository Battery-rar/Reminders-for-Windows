using System.Text.Json;
using Reminders.Core;

namespace Reminders.Windows.Services;

internal static class UiPreferences
{
    private static readonly object Gate = new();
    private static readonly string DirectoryPath = AppStorage.RootDirectory;
#if DEBUG
    static UiPreferences()
    {
        if (Environment.GetEnvironmentVariable("REMINDERS_DATA_DIR") is { Length: > 0 } developmentData)
            DirectoryPath = Path.Combine(Path.GetFullPath(developmentData), "ui");
    }
#endif
    private static string FilePath(bool localDemo) => Path.Combine(DirectoryPath, localDemo ? "demo-ui-settings.json" : "ui-settings.json");

    public static bool LoadNavigationPaneOpen(bool localDemo = false)
    {
        var filePath = FilePath(localDemo);
        try
        {
            lock (Gate)
            {
                if (!File.Exists(filePath)) return true;
                using var document = JsonDocument.Parse(File.ReadAllText(filePath));
                return !document.RootElement.TryGetProperty("navigation_pane_open", out var value) || value.GetBoolean();
            }
        }
        catch (Exception error)
        {
            AppLog.Error("Could not load UI preferences", error);
            return true;
        }
    }

    public static void SaveNavigationPaneOpen(bool isOpen, bool localDemo = false)
    {
        var filePath = FilePath(localDemo);
        try
        {
            lock (Gate)
            {
                var settings = Load(localDemo);
                settings.NavigationPaneOpen = isOpen;
                Write(filePath, settings);
            }
        }
        catch (Exception error) { AppLog.Error("Could not save UI preferences", error); }
    }

    public static UiPreferenceData Load(bool localDemo = false)
    {
        var filePath = FilePath(localDemo);
        try
        {
            lock (Gate)
                return File.Exists(filePath) ? JsonSerializer.Deserialize<UiPreferenceData>(File.ReadAllText(filePath)) ?? new() : new();
        }
        catch (Exception error) { AppLog.Error("Could not load UI preferences", error); return new(); }
    }

    public static void Save(UiPreferenceData settings, bool localDemo = false)
    {
        try
        {
            lock (Gate) Write(FilePath(localDemo), settings);
        }
        catch (Exception error) { AppLog.Error("Could not save UI preferences", error); }
    }

    public static void SaveLanguage(string language)
    {
        lock (Gate)
        {
            var settings = Load();
            settings.Language = language;
            Write(FilePath(localDemo: false), settings);
        }
    }

    private static void Write(string path, UiPreferenceData settings)
    {
        Directory.CreateDirectory(DirectoryPath);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(settings));
        File.Move(temporary, path, true);
    }
}

internal sealed class UiPreferenceData
{
    [System.Text.Json.Serialization.JsonPropertyName("navigation_pane_open")]
    public bool NavigationPaneOpen { get; set; } = true;
    public List<string> ListOrder { get; set; } = [];
    public string ListSort { get; set; } = "manual";
    public string ReminderSort { get; set; } = "due";
    public Dictionary<string, ListAppearance> Lists { get; set; } = [];
    public double? DetailPaneFraction { get; set; }
    public bool CompletionAnimation { get; set; } = true;
    public bool ICloudSetupCompleted { get; set; }
    public string Language { get; set; } = "";
}

internal sealed class ListAppearance
{
    public string? Glyph { get; set; }
    public string? Color { get; set; }
}
