using Stark.Compiler;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace compiler.IntegrationTests;

public sealed partial class ReleaseArchiveVendorSmokeTests
{
    private static readonly string[] ExpectedPackageIds =
    [
        "Vendor.Cgltf",
        "Vendor.GLFW",
        "Vendor.Miniaudio",
        "Vendor.Raylib",
        "Vendor.Raymath",
        "Vendor.Rlgl",
        "Vendor.SDL3",
        "Vendor.SQLite",
        "Vendor.STB.Image",
    ];

    [Fact]
    public void ArchiveSmokeOwnsAProbeForEveryOfficialVendorPackage()
    {
        var repositoryRoot = FindRepositoryRoot();
        var script = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "smoke-release-archive.ps1"));
        using var catalog = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(repositoryRoot, "eng", "release", "vendor-packages.json")));
        var catalogIds = catalog.RootElement.GetProperty("packages")
            .EnumerateArray()
            .Select(static package => package.GetProperty("id").GetString()!)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(ExpectedPackageIds, catalogIds);
        foreach (var packageId in catalogIds)
        {
            Assert.Contains($"\"{packageId}\"", script, StringComparison.Ordinal);
        }

        Assert.Contains("Get-SdkPackageIds -SdkManifest $sdkManifest", script, StringComparison.Ordinal);
        Assert.Contains("SDK advertises official package '$id', but the archive smoke has no native link/runtime probe", script, StringComparison.Ordinal);
        Assert.Contains("SDK advertises an incomplete Raylib package family", script, StringComparison.Ordinal);
        Assert.Contains("$legacyRaylibOnly", script, StringComparison.Ordinal);
        Assert.Contains("$advertisedRaylibFamilyIds[0] -ceq \"Vendor.Raylib\"", script, StringComparison.Ordinal);
    }

    [Fact]
    public void ArchiveSmokeBuildsEveryProbeThroughExtractedSdkDiscoveryAndRunsOnlyHeadlessOnes()
    {
        var repositoryRoot = FindRepositoryRoot();
        var script = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "smoke-release-archive.ps1"));

        Assert.Contains("Invoke-StarkFromPath", script, StringComparison.Ordinal);
        Assert.Contains("-Arguments (@(\"build\") + $TargetArguments)", script, StringComparison.Ordinal);
        Assert.Contains("-Arguments (@(\"run\") + $TargetArguments)", script, StringComparison.Ordinal);
        Assert.Contains("Vendor.Raylib project linked successfully; graphical execution intentionally skipped.", script, StringComparison.Ordinal);
        Assert.Contains("$pathEntries = @($compilerBinRoot)", script, StringComparison.Ordinal);
        Assert.Contains("STARK_SDK_ROOT", script, StringComparison.Ordinal);
        Assert.DoesNotContain("pkg-config", script, StringComparison.OrdinalIgnoreCase);

        var sources = ExtractAdditionalProbeSources(script);
        Assert.Equal(8, sources.Count);
        Assert.Contains("GetVersion();", sources["Vendor.GLFW"], StringComparison.Ordinal);
        Assert.Contains("ClearEvents();", sources["Vendor.GLFW"], StringComparison.Ordinal);
        Assert.Contains("DroppedEventCount()", sources["Vendor.GLFW"], StringComparison.Ordinal);
        Assert.Contains("Vector2Length(Vector2Zero())", sources["Vendor.Raymath"], StringComparison.Ordinal);
        Assert.Contains("rlGetVersion()", sources["Vendor.Rlgl"], StringComparison.Ordinal);
        Assert.Contains("GetVersion();", sources["Vendor.SDL3"], StringComparison.Ordinal);
        Assert.Contains("LoadFromMemory(bytes, ImageChannels.Rgb)", sources["Vendor.STB.Image"], StringComparison.Ordinal);
        Assert.Contains(
            "case ImageResult.Ok(var image):\n            return 1;",
            NormalizeLineEndings(sources["Vendor.STB.Image"]),
            StringComparison.Ordinal);
        Assert.Contains("OpenDecoderFromMemory(bytes, SampleFormat.F32, 1, 8000)", sources["Vendor.Miniaudio"], StringComparison.Ordinal);
        Assert.Contains(
            "case DecoderResult.Ok(var decoder):\n            return 1;",
            NormalizeLineEndings(sources["Vendor.Miniaudio"]),
            StringComparison.Ordinal);
        Assert.Contains("ParseFromMemory(bytes, false)", sources["Vendor.Cgltf"], StringComparison.Ordinal);
        Assert.Contains(
            "case DocumentResult.Ok(var document):\n            return 1;",
            NormalizeLineEndings(sources["Vendor.Cgltf"]),
            StringComparison.Ordinal);
        Assert.Contains("LibraryVersionNumber()", sources["Vendor.SQLite"], StringComparison.Ordinal);

        Assert.Contains("import Vendor.Raylib", ExtractRaylibProbe(script), StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmbeddedProbeSourcesCheckAgainstCurrentVendorApi()
    {
        var repositoryRoot = FindRepositoryRoot();
        var script = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "smoke-release-archive.ps1"));
        var sources = ExtractAdditionalProbeSources(script).Values
            .Append(ExtractRaylibProbe(script))
            .ToArray();
        var temporaryRoot = Path.Combine(Path.GetTempPath(), "stark-release-vendor-smoke-contract-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryRoot);
        try
        {
            for (var index = 0; index < sources.Length; index++)
            {
                var sourcePath = Path.Combine(temporaryRoot, $"Probe{index}.stark");
                await File.WriteAllTextAsync(sourcePath, sources[index]);
                var stdout = new StringWriter();
                var stderr = new StringWriter();
                var exitCode = await CompilerCli.RunAsync(
                    [
                        sourcePath,
                        "--check",
                        "--no-stark-path",
                        "-I", Path.Combine(repositoryRoot, "vendor", "src"),
                        "-I", Path.Combine(repositoryRoot, "stdlib", "src"),
                    ],
                    new StringReader(string.Empty),
                    stdout,
                    stderr);

                Assert.True(
                    exitCode == 0,
                    $"Embedded release probe {index} no longer matches the Vendor API.{Environment.NewLine}{stdout}{Environment.NewLine}{stderr}");
                Assert.Contains("Check succeeded.", stdout.ToString(), StringComparison.Ordinal);
            }
        }
        finally
        {
            Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    private static Dictionary<string, string> ExtractAdditionalProbeSources(string script)
    {
        var sources = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match match in AdditionalProbeRegex().Matches(script))
        {
            Assert.True(sources.TryAdd(match.Groups["id"].Value, match.Groups["source"].Value));
        }
        return sources;
    }

    private static string ExtractRaylibProbe(string script)
    {
        var match = RaylibProbeRegex().Match(script);
        Assert.True(match.Success, "The Raylib archive probe was not found.");
        return match.Groups["source"].Value;
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Stark.slnx")))
            {
                return directory.FullName;
            }
        }
        throw new DirectoryNotFoundException("Could not find the Stark repository root.");
    }

    private static string NormalizeLineEndings(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);

    [GeneratedRegex(
        "PackageId\\s*=\\s*\"(?<id>Vendor\\.[^\"]+)\"(?:(?!PackageId\\s*=).)*?SourceText\\s*=\\s*@'\\r?\\n(?<source>.*?)\\r?\\n'@",
        RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex AdditionalProbeRegex();

    [GeneratedRegex(
        "\\$raylibProjectDir\\s*=.*?Write-SmokeExecutableProject.*?-SourceText\\s+@'\\r?\\n(?<source>.*?)\\r?\\n'@",
        RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex RaylibProbeRegex();
}
