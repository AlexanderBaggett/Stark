using Stark.Compiler;

namespace compiler.Tests;

public sealed class PackageImageOwnershipTests
{
    [Fact]
    public void PackageImageKeepsCoLocatedFlatModulesButExcludesOtherSourceRoots()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-source-roots-");

        try
        {
            var packageSourceRoot = Path.Combine(tempDirectory.FullName, "package");
            var dependencySourceRoot = Path.Combine(tempDirectory.FullName, "dependency");
            var resolver = new InMemoryModuleResolver(
            [
                (
                    new ResolvedModuleReference("Math", Path.Combine(packageSourceRoot, "Math.stark")),
                    """
                    module Math

                    public finite law i32[min max] Double(i32[min max] value)
                    {
                        return value + value;
                    }
                    """,
                    (string?)null),
                (
                    new ResolvedModuleReference("Dependency", Path.Combine(dependencySourceRoot, "Dependency.stark")),
                    """
                    module Dependency

                    public finite law i32[min max] Value()
                    {
                        return 3;
                    }
                    """,
                    (string?)null)
            ]);
            var result = DefaultCompilerPipeline.Create().Run(
                new CompilationInput(
                    """
                    export import Math
                    import Dependency
                    module Facade

                    public finite law i32[min max] Use(i32[min max] value)
                    {
                        return Double(value) + Value();
                    }
                    """,
                    Path.Combine(packageSourceRoot, "Facade.stark")),
                new CompilerOptions(
                    StopAfterPassId: "lower-abi",
                    ModuleResolver: resolver));

            Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                result,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));

            Assert.Equal(
                ["Facade", "Math"],
                manifest.Modules.Select(static module => module.ModuleName).ToArray());
            var facadeModule = Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade");
            Assert.Contains(facadeModule.EffectiveTypedInterface?.Imports ?? [], static import => import.ModuleName == "Dependency");
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

    [Fact]
    public void PackageImageKeepsOnlySourceOwnedVendorModules()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-ownership-");

        try
        {
            var vendorSourceRoot = Path.Combine(tempDirectory.FullName, "vendor", "src");
            var stdlibSourceRoot = Path.Combine(tempDirectory.FullName, "stdlib", "src");
            var rootPath = Path.Combine(vendorSourceRoot, "Vendor", "Raylib.stark");
            var resolver = new InMemoryModuleResolver(
            [
                (
                    new ResolvedModuleReference(
                        "Vendor.Raylib.Core",
                        Path.Combine(vendorSourceRoot, "Vendor", "Raylib", "Core.stark")),
                    """
                    module Vendor.Raylib.Core

                    public finite law i32[min max] OwnedValue()
                    {
                        return 7;
                    }
                    """,
                    (string?)null),
                (
                    new ResolvedModuleReference(
                        "Vendor.Raymath",
                        Path.Combine(vendorSourceRoot, "Vendor", "Raymath.stark")),
                    """
                    module Vendor.Raymath

                    public finite law i32[min max] SiblingValue()
                    {
                        return 8;
                    }
                    """,
                    (string?)null),
                (
                    new ResolvedModuleReference(
                        "System.BitOperations",
                        Path.Combine(stdlibSourceRoot, "System", "BitOperations.stark")),
                    """
                    module System.BitOperations

                    public finite law i32[min max] SystemValue()
                    {
                        return 9;
                    }
                    """,
                    (string?)null)
            ]);
            var result = DefaultCompilerPipeline.Create().Run(
                new CompilationInput(
                    """
                    import System.BitOperations
                    import Vendor.Raymath
                    export import Vendor.Raylib.Core
                    module Vendor.Raylib
                    """,
                    rootPath),
                new CompilerOptions(
                    StopAfterPassId: "lower-abi",
                    ModuleResolver: resolver));

            Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                result,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Raylib.lib" : "libRaylib.a"));

            Assert.Equal(
                ["Vendor.Raylib", "Vendor.Raylib.Core"],
                manifest.Modules.Select(static module => module.ModuleName).ToArray());
            var rootModule = Assert.Single(manifest.Modules, static module => module.ModuleName == "Vendor.Raylib");
            Assert.Equal(
                ["System.BitOperations", "Vendor.Raylib.Core", "Vendor.Raymath"],
                (rootModule.EffectiveTypedInterface?.Imports ?? [])
                .Select(static import => import.ModuleName)
                .OrderBy(static moduleName => moduleName, StringComparer.Ordinal)
                .ToArray());

            var ownedModule = Assert.Single(manifest.Modules, static module => module.ModuleName == "Vendor.Raylib.Core");
            var ownedFunction = Assert.Single(ownedModule.EffectiveTypedInterface?.Functions ?? []);
            Assert.Equal("Vendor.Raylib.Core.OwnedValue", ownedFunction.QualifiedName);
            Assert.NotNull(ownedModule.EffectiveCompilerFacts);
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

    [Fact]
    public void SystemPackageOwnsTargetDispatchTemplateOutsideSourceDirectory()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-system-package-template-ownership-");

        try
        {
            var rootPath = Path.Combine(tempDirectory.FullName, "stdlib", "src", "System.stark");
            var templatePath = Path.Combine(
                tempDirectory.FullName,
                "stdlib",
                "templates",
                "System.Runtime.Platform.LinuxDispatch.stark");
            var resolver = new InMemoryModuleResolver(
            [
                (
                    new ResolvedModuleReference("System.Runtime.Platform", templatePath),
                    """
                    module System.Runtime.Platform

                    public finite law i32[min max] ProcessId()
                    {
                        return 42;
                    }
                    """,
                    (string?)null)
            ]);
            var result = DefaultCompilerPipeline.Create().Run(
                new CompilationInput(
                    """
                    export import System.Runtime.Platform
                    module System
                    """,
                    rootPath),
                new CompilerOptions(
                    StopAfterPassId: "lower-abi",
                    ModuleResolver: resolver));

            Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                result,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "System.lib" : "libSystem.a"));

            Assert.Equal(
                ["System", "System.Runtime.Platform"],
                manifest.Modules.Select(static module => module.ModuleName).ToArray());
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
