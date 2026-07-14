using Stark.Compiler;
using System.Text.Json.Nodes;

namespace compiler.IntegrationTests;

[Collection("SerialToolchain")]
public sealed class ProjectCliBundledPackagePrecedenceTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task VendorPackageSuppressesSameModuleSourceNativeFallback(bool targetScoped)
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out var targetInfo))
        {
            return;
        }

        var originalDirectory = Environment.CurrentDirectory;
        var tempDirectory = Directory.CreateTempSubdirectory("stark-project-cli-vendor-package-precedence-");

        try
        {
            await CreateVendorPackageAsync(tempDirectory.FullName, targetInfo.Triple, targetScoped);
            await CreateVendorSourceFallbackAsync(tempDirectory.FullName);
            await CreateApplicationAsync(tempDirectory.FullName);
            await CreateDevelopmentSdkManifestAsync(tempDirectory.FullName, targetInfo.Triple, targetScoped);
            Environment.CurrentDirectory = tempDirectory.FullName;

            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exitCode = await CompilerCli.RunAsync(
                ["build", "--target", targetInfo.Triple, "--sdk-root", tempDirectory.FullName],
                new StringReader(string.Empty),
                stdout,
                stderr);

            Assert.True(
                exitCode == 0,
                $"Expected the bundled package to suppress source native fallback. Exit: {exitCode}{Environment.NewLine}STDOUT:{Environment.NewLine}{stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{stderr}");
            Assert.Contains("Emitted executable:", stdout.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("native.paths.package-precedence-missing", stderr.ToString(), StringComparison.Ordinal);
            Assert.Equal(string.Empty, stderr.ToString());
        }
        finally
        {
            Environment.CurrentDirectory = originalDirectory;
            Cleanup(tempDirectory);
        }
    }

    private static async Task CreateVendorPackageAsync(string rootDirectory, string targetTriple, bool targetScoped)
    {
        var packageSourceDirectory = Path.Combine(rootDirectory, "vendor-package-source");
        var distDirectory = Path.Combine(rootDirectory, "vendor", "dist");
        var packageDirectory = targetScoped
            ? Path.Combine(distDirectory, NormalizeBuildPathSegment(targetTriple))
            : distDirectory;
        Directory.CreateDirectory(packageSourceDirectory);
        Directory.CreateDirectory(packageDirectory);
        if (!targetScoped)
        {
            var targetDistDirectory = Path.Combine(distDirectory, NormalizeBuildPathSegment(targetTriple));
            Directory.CreateDirectory(targetDistDirectory);
            await File.WriteAllTextAsync(Path.Combine(targetDistDirectory, "ignored.starkpkg.json"), "not a package image");
        }

        var packageSourcePath = Path.Combine(packageSourceDirectory, "PackagePrecedenceProbe.stark");
        await File.WriteAllTextAsync(
            packageSourcePath,
            """
            module Vendor.PackagePrecedenceProbe

            public finite law i32[min max] Value()
            {
                return 0;
            }
            """);

        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exitCode = await CompilerCli.RunAsync(
            [
                packageSourcePath,
                "--emit-lib",
                "-o",
                Path.Combine(packageDirectory, LibraryFileName("VendorPackagePrecedenceProbe")),
                "--package-image-output",
                Path.Combine(packageDirectory, "libVendorPackagePrecedenceProbe.starkpkg"),
                "--package-profile",
                "dev",
                "--target",
                targetTriple
            ],
            new StringReader(string.Empty),
            stdout,
            stderr);

        Assert.True(
            exitCode == 0,
            $"Expected package fixture emission to succeed. Exit: {exitCode}{Environment.NewLine}STDOUT:{Environment.NewLine}{stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{stderr}");
        Assert.Contains("Emitted static library:", stdout.ToString(), StringComparison.Ordinal);
        Assert.Contains("Emitted package image:", stdout.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, stderr.ToString());
    }

    private static async Task CreateVendorSourceFallbackAsync(string rootDirectory)
    {
        var vendorDirectory = Path.Combine(rootDirectory, "vendor");
        var vendorSourceDirectory = Path.Combine(vendorDirectory, "src", "Vendor");
        Directory.CreateDirectory(vendorSourceDirectory);

        await File.WriteAllTextAsync(
            Path.Combine(vendorDirectory, "Stark.toml"),
            """
            [project]
            name = "vendor-package-precedence-source"
            version = "0.1.0"
            kind = "library"

            [library]
            root = "src/Vendor/PackagePrecedenceProbe.stark"
            output = "VendorPackagePrecedenceProbeSource"

            [native.fallback.linux]
            include-dirs = ["${native.paths.package-precedence-missing}"]

            [native.fallback.macos]
            include-dirs = ["${native.paths.package-precedence-missing}"]

            [native.fallback.windows]
            include-dirs = ["${native.paths.package-precedence-missing}"]
            """);
        await File.WriteAllTextAsync(
            Path.Combine(vendorSourceDirectory, "PackagePrecedenceProbe.stark"),
            """
            module Vendor.PackagePrecedenceProbe

            public finite law i32[min max] Value()
            {
                return 1;
            }
            """);
    }

    private static async Task CreateApplicationAsync(string rootDirectory)
    {
        await File.WriteAllTextAsync(
            Path.Combine(rootDirectory, "Stark.toml"),
            """
            [project]
            name = "vendor-package-precedence-app"
            version = "0.1.0"
            kind = "executable"

            [executable]
            root = "App.stark"
            output = "vendor-package-precedence-app"
            """);
        await File.WriteAllTextAsync(
            Path.Combine(rootDirectory, "App.stark"),
            """
            import Vendor.PackagePrecedenceProbe
            module App

            export fn i32[min max] main()
            {
                return Value();
            }
            """);
    }

    private static async Task CreateDevelopmentSdkManifestAsync(
        string rootDirectory,
        string targetTriple,
        bool targetScoped)
    {
        Assert.True(
            DevelopmentSdkManifestWriter.TryWrite(rootDirectory, out var manifestPath, out var error),
            error);
        var packageDirectory = targetScoped
            ? Path.Combine("vendor", "dist", NormalizeBuildPathSegment(targetTriple))
            : Path.Combine("vendor", "dist");
        var manifest = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath))!.AsObject();
        manifest["modules"] = new JsonArray
        {
            new JsonObject
            {
                ["name"] = "Vendor.PackagePrecedenceProbe",
                ["package"] = "Vendor.PackagePrecedenceProbe"
            }
        };
        manifest["packages"] = new JsonArray
        {
            new JsonObject
            {
                ["id"] = "Vendor.PackagePrecedenceProbe",
                ["version"] = "0.1.0",
                ["profile"] = "dev",
                ["image"] = Path.Combine(packageDirectory, "libVendorPackagePrecedenceProbe.starkpkg").Replace('\\', '/'),
                ["library"] = Path.Combine(packageDirectory, LibraryFileName("VendorPackagePrecedenceProbe")).Replace('\\', '/'),
                ["dependencies"] = new JsonArray(),
                ["native"] = new JsonObject
                {
                    ["artifacts"] = new JsonArray(),
                    ["includeDirectories"] = new JsonArray(),
                    ["libraryDirectories"] = new JsonArray(),
                    ["runtimeFiles"] = new JsonArray(),
                    ["licenseFiles"] = new JsonArray(),
                    ["fileChecksums"] = new JsonArray(),
                    ["libraries"] = new JsonArray(),
                    ["linkArguments"] = new JsonArray()
                }
            }
        };
        await File.WriteAllTextAsync(
            manifestPath,
            manifest.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
    }

    private static string LibraryFileName(string outputName)
    {
        return OperatingSystem.IsWindows() ? $"{outputName}.lib" : $"lib{outputName}.a";
    }

    private static string NormalizeBuildPathSegment(string value)
    {
        var chars = value.Trim().Select(static ch =>
            char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.' or '+'
                ? ch
                : '_');
        var normalized = new string(chars.ToArray());
        return normalized.Length == 0 ? "_" : normalized;
    }

    private static void Cleanup(DirectoryInfo tempDirectory)
    {
        try
        {
            tempDirectory.Delete(recursive: true);
        }
        catch
        {
            // Best effort cleanup only.
        }
    }
}
