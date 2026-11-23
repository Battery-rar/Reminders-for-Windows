using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Reminders.Core;

internal static class ChinaMainlandChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        const string account = "mainland@example.com";
        const string home = "https://www.icloud.com.cn";
        const string authHost = "idmsa.apple.com.cn";
        const string setupHost = "setup.icloud.com.cn";
        const string cloudHost = "p01-ckdatabasews.icloud.com.cn";
        var state = J.Node(new
        {
            china_mainland = true,
            browser_cookie_session = true,
            cookies = new[]
            {
                new { name = "test", value = "mainland", domain = ".icloud.com.cn", path = "/" },
                new { name = "test", value = "global", domain = ".icloud.com", path = "/" },
                new { name = "test", value = "unrelated", domain = ".icloud.com.cn.example.com", path = "/" }
            }
        }).AsObject();

        using (var auth = new AppleAuth((JsonObject)state.DeepClone(), () => new ScriptHandler(request =>
        {
            check(request.RequestUri!.Host == authHost && request.RequestUri.AbsolutePath.EndsWith("/authorize/signin"), "mainland native login starts at the mainland Apple authentication host");
            check(request.Headers.GetValues("Origin").Single() == home && request.Headers.Referrer?.Host == authHost, "mainland native login uses the corresponding origin and referrer");
            check(request.Headers.GetValues("x-apple-oauth-redirect-uri").Single() == home && request.RequestUri.Query.Contains("redirect_uri=" + Uri.EscapeDataString(home)), "mainland native login uses the mainland redirect URI in headers and query");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("{}") });
        })))
        {
            try { await auth.SignIn(account, "mock-password", false, default); check(false, "mock bootstrap failure ends the native sign-in"); }
            catch (CoreException error) { check(error.Code == "NETWORK", "mock bootstrap failure is returned without retrying through another region"); }
        }

        var secrets = new SecretStore(new MemorySecrets());
        var validations = 0;
        var queries = 0;
        HttpMessageHandler Handler() => new ScriptHandler(request =>
        {
            check(request.Headers.GetValues("Origin").Single() == home, "mainland validation and CloudKit keep the mainland origin");
            JsonNode response;
            if (request.RequestUri!.AbsolutePath.EndsWith("/validate"))
            {
                validations++;
                check(request.RequestUri.Host == setupHost, "the mainland browser session validates via the mainland setup host");
                response = J.Node(new
                {
                    dsInfo = new { dsid = "1", appleId = account, hsaVersion = 2 },
                    hsaTrustedBrowser = false,
                    webservices = new { ckdatabasews = new { url = "https://" + cloudHost } }
                });
            }
            else
            {
                queries++;
                check(request.RequestUri.Host == cloudHost && request.RequestUri.AbsolutePath.EndsWith("/records/query"), "Reminders uses the mainland CloudKit host returned by Apple");
                response = J.Node(new { records = Array.Empty<object>() });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response.ToJsonString()) });
        });
        using (var imported = new ICloudClient("", secrets, Handler))
        {
            var result = await imported.ImportBrowserSession(account, state, default);
            check(result.Flag("authenticated"), "mainland browser session can be imported and checked");
        }
        var saved = J.Parse(secrets.Session(account, chinaMainland: true));
        check(saved.Flag("china_mainland") && saved.Array("cookies").Count == 1 && saved.Array("cookies")[0].Text("value") == "mainland", "mainland session retains the region and only Apple cookies");
        secrets.Password(account, "saved-global-password");
        using var restarted = new ICloudClient(account, secrets, Handler, cacheChinaMainland: true);
        check(!restarted.Status.Flag("can_restore"), "mainland sign-in cannot reuse a saved global password");
        try { await restarted.Connect(null, true, false, default); check(false, "mainland password fallback rejects a global saved credential"); }
        catch (CoreException error) { check(error.Code == "AUTH_REQUIRED", "password fallback requires a saved credential for the selected region"); }
        check(await restarted.Restore(default) && validations == 2 && queries == 2, "mainland sessions revalidate and reach CloudKit after restart");
        using var globalCache = new ICloudClient(account, secrets, Handler, cacheChinaMainland: false);
        check(globalCache.Status.Flag("can_restore"), "the existing global password remains available in its original region");
        // The stale mainland session must be ignored, independently of the saved password.
        secrets.Delete(account);
        secrets.Session(account, saved!.ToJsonString(), chinaMainland: true);
        check(!await globalCache.Restore(default) && validations == 2 && queries == 2, "a mainland vault session cannot restore into a global cache");

        await CheckRegionSwitchAsync(account, authHost, state, check);
    }

    private static async Task CheckRegionSwitchAsync(string account, string authHost, JsonObject state, Action<bool, string> check)
    {
        var directory = Path.Combine(Path.GetTempPath(), "reminders-region-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var cache = new Cache(Path.Combine(directory, "cache.db"));
            cache.SetMeta("apple_id", account);
            cache.Upsert(J.Node(new { id = "existing", list_id = "list", title = "Saved reminder" }), true);
            cache.Enqueue("existing", "update", J.Node(new { title = "Unsynced edit" }), "old");
            var requests = 0;
            var queueDuringProbe = true;
            var vault = new MemorySecrets();
            await using var service = new ReminderService(directory, vault, () => new ScriptHandler(request =>
            {
                requests++;
                var uri = request.RequestUri!;
                if (uri.AbsolutePath.EndsWith("/authorize/signin"))
                {
                    check(uri.Host == authHost, "the native sign-in request honors the selected mainland region");
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("{}") });
                }
                if (uri.AbsolutePath.EndsWith("/validate"))
                {
                    var body = J.Node(new
                    {
                        dsInfo = new { dsid = "1", appleId = account, hsaVersion = 2 },
                        hsaTrustedBrowser = false,
                        webservices = new { ckdatabasews = new { url = "https://ckdatabasews.icloud.com.cn" } }
                    });
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body.ToJsonString()) });
                }
                if (queueDuringProbe) cache.Enqueue("existing", "update", J.Node(new { title = "Edit during sign-in" }), "new");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"records\":[]}") });
            }));
            var login = JsonSerializer.SerializeToElement(new { apple_id = account, password = "mock-password", china_mainland = true });
            try { await service.CallAsync("login", login); check(false, "region switch must reject pending updates"); }
            catch (CoreException error) { check(error.Code == "BAD_REQUEST" && requests == 0 && cache.Pending(1).Count == 1, "switching regions cannot send pending edits to a different CloudKit region"); }

            cache.Acknowledge(cache.Pending(1)[0].Number("seq"), "existing", "new");
            try { await service.CallAsync("login", login); check(false, "mock mainland sign-in fails"); }
            catch (CoreException error) { check(error.Code == "NETWORK" && requests == 1, "failed regional sign-in returns the service error"); }
            check(cache.Reminder("existing").Text("title") == "Saved reminder" && cache.Meta("china_mainland") is null,
                "failed regional sign-in preserves the existing account's cache and region");

            var browserLogin = JsonSerializer.SerializeToElement(new { apple_id = account, state });
            try { await service.CallAsync("browser_login", browserLogin); check(false, "browser region switch must reject edits queued during validation"); }
            catch (CoreException error)
            {
                check(error.Code == "BAD_REQUEST" && cache.Pending(1).Count == 1 && new SecretStore(vault).Session(account, chinaMainland: true) is null,
                    "region switch rechecks the queue before saving credentials or replacing the cache");
            }

            cache.Acknowledge(cache.Pending(1)[0].Number("seq"), "existing", "newer");
            cache.Execute("INSERT INTO conflicts(reminder_id,local_json,remote_json,detected_at) VALUES('existing','{}','{}','test')");
            try { await service.CallAsync("browser_login", browserLogin); check(false, "browser region switch must retain unresolved conflicts"); }
            catch (CoreException error) { check(error.Code == "BAD_REQUEST" && cache.Conflicts.Count == 1, "conflicts remain available after a blocked region switch"); }

            cache.Execute("UPDATE conflicts SET resolved=1");
            queueDuringProbe = false;
            var signedIn = await service.CallAsync("browser_login", browserLogin);
            check(signedIn.GetProperty("authenticated").GetBoolean() && cache.Meta("china_mainland") == "1",
                "verified browser sign-in commits the new region after local edits and conflicts are resolved");
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(directory, true); }
    }
}
