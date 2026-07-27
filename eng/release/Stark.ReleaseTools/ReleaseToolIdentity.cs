using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace Stark.ReleaseTools;

internal static class ReleaseToolIdentity
{
    public const string Implementation = "Stark.ReleaseTools";
    public const string TargetFramework = "net10.0";
    public const string DotNetSdkVersion = "10.0.302";
    public const string DotNetRuntimeVersion = "10.0.10";

    public static JsonObject Current()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var location = assembly.Location;
        return new JsonObject
        {
            ["policy"] = new JsonObject
            {
                ["implementation"] = Implementation,
                ["targetFramework"] = TargetFramework,
                ["dotnetSdkVersion"] = DotNetSdkVersion,
                ["dotnetRuntimeVersion"] = DotNetRuntimeVersion,
            },
            ["observed"] = new JsonObject
            {
                ["implementation"] = Implementation,
                ["targetFramework"] = TargetFramework,
                ["frameworkDescription"] = RuntimeInformation.FrameworkDescription,
                ["dotnetRuntimeVersion"] = Environment.Version.ToString(3),
                ["processArchitecture"] = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
                ["assemblyBytes"] = new FileInfo(location).Length,
                ["assemblySha256"] = JsonIO.Sha256File(location),
            },
            ["matchesPolicy"] = Environment.Version.ToString(3) == DotNetRuntimeVersion,
        };
    }
}
