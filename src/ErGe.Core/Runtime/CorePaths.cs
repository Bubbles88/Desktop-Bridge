using ErGe.Core.Policy;

namespace ErGe.Core.Runtime;

public static class CorePaths
{
    public static string PolicyPath =>
        GetEnvironmentPath("ERGE_POLICY_PATH", FilePolicyStore.GetDefaultPath());

    public static string StatusPath =>
        GetEnvironmentPath("ERGE_STATUS_PATH", CoreStatusStore.GetDefaultPath());

    private static string GetEnvironmentPath(string variableName, string fallback)
    {
        var configured = Environment.GetEnvironmentVariable(variableName);
        return string.IsNullOrWhiteSpace(configured)
            ? fallback
            : System.IO.Path.GetFullPath(configured);
    }
}
