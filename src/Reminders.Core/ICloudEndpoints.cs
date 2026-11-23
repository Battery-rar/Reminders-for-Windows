namespace Reminders.Core;

public static class ICloudEndpoints
{
    private static string ICloudDomain(bool chinaMainland) => chinaMainland ? "icloud.com.cn" : "icloud.com";

    public static string Home(bool chinaMainland) => "https://www." + ICloudDomain(chinaMainland);
    public static string SetupHost(bool chinaMainland) => "setup." + ICloudDomain(chinaMainland);
    public static string AuthHost(bool chinaMainland) => chinaMainland ? "idmsa.apple.com.cn" : "idmsa.apple.com";

    internal static bool IsICloudDomain(string host) =>
        IsDomain(host, ICloudDomain(false)) || IsDomain(host, ICloudDomain(true));

    public static bool IsAppleDomain(string host) =>
        IsICloudDomain(host) || IsDomain(host, "apple.com") || IsDomain(host, "apple.com.cn");

    public static bool IsCloudKitWebServiceHost(string host) =>
        IsCloudKitWebServiceHost(host, ICloudDomain(false)) || IsCloudKitWebServiceHost(host, ICloudDomain(true));

    private static bool IsCloudKitWebServiceHost(string host, string domain) =>
        host.Equals("ckdatabasews." + domain, StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith("-ckdatabasews." + domain, StringComparison.OrdinalIgnoreCase);

    private static bool IsDomain(string host, string domain) =>
        host.Equals(domain, StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);
}
