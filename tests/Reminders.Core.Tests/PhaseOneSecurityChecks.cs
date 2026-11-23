using System.Net;
using Reminders.Core;

internal static class PhaseOneSecurityChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        const string account = "isolation@example.com";
        const string legacyPassword = "com.paulsavvas.reminders-sync.password";
        const string legacySession = "com.paulsavvas.reminders-sync.session";
        var vault = new MemorySecrets();
        vault.Write(legacyPassword, account, "legacy-password");
        vault.Write(legacySession, account, "legacy-session");
        var secrets = new SecretStore(vault);
        check(secrets.Password(account) is null && secrets.Session(account) is null,
            "the fork cannot load the upstream installation's credentials");
        secrets.Delete(account);
        check(vault.Read(legacyPassword, account) == "legacy-password" && vault.Read(legacySession, account) == "legacy-session",
            "sign-out in the fork leaves upstream credentials available for rollback");
        secrets.Session(account, new string('g', 2300));
        secrets.Session(account, new string('c', 2300), chinaMainland: true);
        check(secrets.Session(account) == new string('g', 2300) && secrets.Session(account, chinaMainland: true) == new string('c', 2300),
            "global and mainland sessions have separate manifests and chunks");
        secrets.Delete(account);
        check(secrets.Session(account) is null && secrets.Session(account, chinaMainland: true) == new string('c', 2300),
            "signing out of global iCloud preserves the separate mainland session");

        var directory = Path.Combine(Path.GetTempPath(), "reminders-exclusive-cache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await using (var first = new ReminderService(directory, vault))
            {
                try
                {
                    await using var duplicate = new ReminderService(directory, vault);
                    check(false, "two service instances cannot own the same cache");
                }
                catch (CoreException error) { check(error.Code == "CACHE_IN_USE", "duplicate cache opening reports the active instance"); }
            }
            await using var reopened = new ReminderService(directory, vault);
            check(File.Exists(Path.Combine(directory, "cache.db")), "normal shutdown releases the cache for an upgrade or restart");
            using var cache = new Cache(Path.Combine(directory, "cache.db"));
            check(Version.TryParse(cache.Query("SELECT sqlite_version() AS version")[0].Text("version"), out var version) && version >= new Version(3, 54, 1),
                "the application opens its database with the patched native SQLite engine");
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(directory, true); }

        foreach (var chinaMainland in new[] { false, true })
        {
            var oppositeHost = chinaMainland ? "ckdatabasews.icloud.com" : "ckdatabasews.icloud.com.cn";
            var state = J.Node(new { china_mainland = chinaMainland, webservices = new { ckdatabasews = new { url = "https://" + oppositeHost } } }).AsObject();
            using var auth = new AppleAuth(state, () => new ResponseHandler(() =>
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"records\":[]}") }));
            try { await new CloudKit(auth).CheckAccess(default); check(false, "the cache cannot sync through the other iCloud region"); }
            catch (CoreException error) { check(error.Code == "ERROR", "cross-region CloudKit destinations are rejected before sync"); }
        }
    }
}
