namespace Reminders.Core;

public static class ICloudEndpoints
{
    private static string ICloudDomain(bool chinaMainland) => chinaMainland ? "icloud.com.cn" : "icloud.com";

    public static string Home(bool chinaMainland) => "https://www." + ICloudDomain(chinaMainland);
    public static string SetupHost(bool chinaMainland) => "setup." + ICloudDomain(chinaMainland);
    public static string AuthHost(bool chinaMainland) => chinaMainland ? "idmsa.apple.com.cn" : "idmsa.apple.com";

    internal static bool IsICloudDomain(string host, bool chinaMainland) => IsDomain(host, ICloudDomain(chinaMainland));

    public static bool IsAppleDomain(string host) =>
        IsICloudDomain(host, false) || IsICloudDomain(host, true) || IsDomain(host, "apple.com") || IsDomain(host, "apple.com.cn");

    public static bool IsSessionDomain(string host, bool chinaMainland) =>
        IsICloudDomain(host, chinaMainland) || IsDomain(host, chinaMainland ? "apple.com.cn" : "apple.com");

    public static bool IsCloudKitWebServiceHost(string host, bool chinaMainland)
    {
        var suffix = "ckdatabasews." + ICloudDomain(chinaMainland);
        return host.Equals(suffix, StringComparison.OrdinalIgnoreCase) || host.EndsWith("-" + suffix, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDomain(string host, string domain) =>
        host.Equals(domain, StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);
}
