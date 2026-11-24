using Reminders.Core;

internal static class AppStorageChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var root = Directory.CreateTempSubdirectory("reminders-app-storage-");
        try
        {
            var current = Path.Combine(root.FullName, "Apple Reminders on Windows");
            var previous = Path.Combine(root.FullName, "RemindersForWindowsGCBD");
            check(AppStorage.ResolveRootDirectory(root.FullName) == current, "clean install uses the new data folder");
            Directory.CreateDirectory(previous);
            File.WriteAllText(Path.Combine(previous, "cache.db"), "cache fixture");
            File.WriteAllText(Path.Combine(previous, "cache.db-wal"), "pending transaction fixture");
            check(AppStorage.ResolveRootDirectory(root.FullName, migrate: false) == previous && Directory.Exists(previous),
                "an offline preview can resolve the previous directory without moving it");
            if (OperatingSystem.IsWindows())
            {
                using var lease = File.Open(Path.Combine(previous, "cache.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                check(AppStorage.ResolveRootDirectory(root.FullName) == previous && !Directory.Exists(current),
                    "an active previous instance keeps the existing data folder");
            }

            check(AppStorage.ResolveRootDirectory(root.FullName) == current && !Directory.Exists(previous),
                "existing data folder is renamed once it is available");
            check(File.ReadAllText(Path.Combine(current, "cache.db")) == "cache fixture"
                && File.ReadAllText(Path.Combine(current, "cache.db-wal")) == "pending transaction fixture",
                "cache and pending transaction stay together after the rename");

            Directory.CreateDirectory(previous);
            File.WriteAllText(Path.Combine(previous, "cache.db"), "other instance fixture");
            check(AppStorage.ResolveRootDirectory(root.FullName) == current
                && File.ReadAllText(Path.Combine(current, "cache.db")) == "cache fixture",
                "a later startup preserves the active data directory");
        }
        finally { root.Delete(recursive: true); }
    }
}
