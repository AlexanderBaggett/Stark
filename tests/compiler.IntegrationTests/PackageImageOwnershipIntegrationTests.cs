using Stark.Compiler;

namespace compiler.IntegrationTests;

[Collection("SerialToolchain")]
public sealed class PackageImageOwnershipIntegrationTests
{
    [Fact]
    public async Task EmitPackageExcludesTransitiveSystemAndSiblingVendorSourceModules()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        var tempDirectory = Directory.CreateTempSubdirectory("stark-cli-package-ownership-");

        try
        {
            var vendorSourceRoot = Path.Combine(tempDirectory.FullName, "vendor", "src");
            var stdlibSourceRoot = Path.Combine(tempDirectory.FullName, "stdlib", "src");
            var rootPath = Path.Combine(vendorSourceRoot, "Vendor", "Raylib.stark");
            var ownedPath = Path.Combine(vendorSourceRoot, "Vendor", "Raylib", "Core.stark");
            var siblingPath = Path.Combine(vendorSourceRoot, "Vendor", "Raymath.stark");
            var systemPath = Path.Combine(stdlibSourceRoot, "System", "BitOperations.stark");
            var libraryPath = Path.Combine(
                tempDirectory.FullName,
                OperatingSystem.IsWindows() ? "Raylib.lib" : "libRaylib.a");
            var packagePath = Path.Combine(tempDirectory.FullName, "libRaylib.starkpkg");
            var saveTempsPath = Path.Combine(tempDirectory.FullName, "temps");
            Directory.CreateDirectory(Path.GetDirectoryName(ownedPath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(systemPath)!);

            await File.WriteAllTextAsync(
                rootPath,
                """
                import System.BitOperations
                import Vendor.Raymath
                export import Vendor.Raylib.Core
                module Vendor.Raylib
                """);
            await File.WriteAllTextAsync(
                ownedPath,
                """
                module Vendor.Raylib.Core

                public finite law i32[min max] OwnedValue()
                {
                    return 7;
                }
                """);
            await File.WriteAllTextAsync(
                siblingPath,
                """
                module Vendor.Raymath

                public finite law i32[min max] SiblingValue()
                {
                    return 8;
                }
                """);
            await File.WriteAllTextAsync(
                systemPath,
                """
                module System.BitOperations

                public finite law i32[min max] SystemValue()
                {
                    return 9;
                }
                """);

            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exitCode = await CompilerCli.RunAsync(
                [
                    rootPath,
                    "--emit-lib",
                    "-o",
                    libraryPath,
                    "--package-image-output",
                    packagePath,
                    "--save-temps",
                    saveTempsPath,
                    "--no-stark-path",
                    "-I",
                    vendorSourceRoot,
                    "-I",
                    stdlibSourceRoot,
                ],
                new StringReader(string.Empty),
                stdout,
                stderr);

            Assert.Equal(0, exitCode);
            Assert.Equal(string.Empty, stderr.ToString());
            Assert.True(File.Exists(libraryPath));
            Assert.True(PackageImageLoader.TryLoadManifest(packagePath, out var manifest));
            Assert.Equal(
                ["Vendor.Raylib", "Vendor.Raylib.Core"],
                manifest.Modules.Select(static module => module.ModuleName).ToArray());

            var rootModule = Assert.Single(manifest.Modules, static module => module.ModuleName == "Vendor.Raylib");
            Assert.Contains(rootModule.EffectiveTypedInterface?.Imports ?? [], static import => import.ModuleName == "System.BitOperations");
            Assert.Contains(rootModule.EffectiveTypedInterface?.Imports ?? [], static import => import.ModuleName == "Vendor.Raymath");
            Assert.DoesNotContain(manifest.Modules, static module => module.ModuleName.StartsWith("System.", StringComparison.Ordinal));
            Assert.DoesNotContain(manifest.Modules, static module => module.ModuleName == "Vendor.Raymath");

            var objectExtension = OperatingSystem.IsWindows() ? ".obj" : ".o";
            Assert.True(File.Exists(Path.Combine(saveTempsPath, $"Vendor_Raylib_Core{objectExtension}")));
            Assert.False(File.Exists(Path.Combine(saveTempsPath, $"Vendor_Raymath{objectExtension}")));
            Assert.False(File.Exists(Path.Combine(saveTempsPath, $"System_BitOperations{objectExtension}")));
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup.
            }
        }
    }
}
