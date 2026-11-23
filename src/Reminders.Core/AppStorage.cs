namespace Reminders.Core;

public static class AppStorage
{
    public const string FolderName = "Apple Reminders on Windows";
    private const string PreviousFolderName = "RemindersForWindowsGCBD";
    // Preserve the Credential Manager targets already associated with the user's cache.
    internal const string CredentialPrefix = "Battery-rar.RemindersForWindowsGCBD";

    private static readonly Lazy<string> Root = new(() => ResolveRootDirectory(DataParent, migrate: false));
    private static string DataParent => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    public static string RootDirectory => DevelopmentData ?? Root.Value;

    public static void MigrateLegacyData()
    {
        if (DevelopmentData is null)
            ResolveRootDirectory(DataParent);
    }

    private static string? DevelopmentData
    {
        get
        {
#if DEBUG
            if (Environment.GetEnvironmentVariable("REMINDERS_DATA_DIR") is { Length: > 0 } directory)
                return Path.GetFullPath(directory);
#endif
            return null;
        }
    }

    internal static string ResolveRootDirectory(string localAppData, bool migrate = true)
    {
        var current = Path.Combine(localAppData, FolderName);
        var previous = Path.Combine(localAppData, PreviousFolderName);
        if (Directory.Exists(current) || !Directory.Exists(previous))
            return current;
        if (!migrate)
            return previous;

        try
        {
            // Make a same-volume rename so the database, journals and preferences move together.
            Directory.Move(previous, current);
            return current;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Another process may have moved the directory or may still have its cache open.
            return Directory.Exists(current) ? current : previous;
        }
    }
}
