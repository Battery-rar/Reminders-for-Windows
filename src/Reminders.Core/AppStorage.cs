namespace Reminders.Core;

public static class AppStorage
{
    public const string FolderName = "RemindersForWindowsGCBD";
    internal const string CredentialPrefix = "Battery-rar.RemindersForWindowsGCBD";

    public static string RootDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), FolderName);
}
