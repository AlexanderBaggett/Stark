using Stark.Compiler;

namespace compiler.Tests;

/// <summary>
/// Intra-package module-privacy enforcement (F3). A source submodule registers its
/// full declaration surface — including module-private members — into the type-check
/// tables so its own body can resolve them (the cross-module monomorphization fix).
/// A call-site visibility check (IsModuleMemberAccessible, mirroring IsFieldAccessible)
/// then keeps a SIBLING source submodule from reaching another submodule's
/// module-private functions/globals/types within a single whole-package build, while
/// same-module, public, and internal-same-package access keep working. Privacy across
/// the package-image boundary was already enforced (such members are never registered).
/// </summary>
public sealed class ModulePrivacyEnforcementTests
{
    private const string LibrarySource =
        """
        module Demo.Lib

        public fn i32[min max] Shared()
        {
            return 1;
        }

        internal fn i32[min max] PackageHelper()
        {
            return 2;
        }

        fn i32[min max] Secret()
        {
            return 3;
        }

        // Same-module access to a module-private member must still compile.
        public fn i32[min max] UseOwnSecret()
        {
            return Secret();
        }

        public const SharedNumber = 7;

        const SecretNumber = 99;

        struct HiddenType
        {
            i32[min max] value;
        }

        public struct OpenType
        {
            i32[min max] value;
        }
        """;

    [Fact]
    public void SiblingCallingModulePrivateFunctionIsRejected()
    {
        var result = RunApp(
            """
            import Demo.Lib
            module Demo.App
            export fn i32[min max] main()
            {
                return Demo.Lib.Secret();
            }
            """);

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "STK3015"
                && diagnostic.Message.Contains("Demo.Lib.Secret", StringComparison.Ordinal)
                && diagnostic.Message.Contains("not visible from module 'Demo.App'", StringComparison.Ordinal));
    }

    [Fact]
    public void SiblingCallingPublicFunctionSucceeds()
    {
        var result = RunApp(
            """
            import Demo.Lib
            module Demo.App
            export fn i32[min max] main()
            {
                return Demo.Lib.Shared();
            }
            """);

        Assert.True(
            result.Succeeded,
            string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }

    [Fact]
    public void SiblingCallingInternalFunctionInSamePackageSucceeds()
    {
        var result = RunApp(
            """
            import Demo.Lib
            module Demo.App
            export fn i32[min max] main()
            {
                return Demo.Lib.PackageHelper();
            }
            """);

        Assert.True(
            result.Succeeded,
            string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }

    [Fact]
    public void SameModulePrivateAccessStillCompiles()
    {
        // UseOwnSecret (in Demo.Lib) calls the module-private Secret; reaching it
        // through the public entry point must compile cleanly.
        var result = RunApp(
            """
            import Demo.Lib
            module Demo.App
            export fn i32[min max] main()
            {
                return Demo.Lib.UseOwnSecret();
            }
            """);

        Assert.True(
            result.Succeeded,
            string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }

    [Fact]
    public void SiblingReadingModulePrivateGlobalIsRejected()
    {
        var result = RunApp(
            """
            import Demo.Lib
            module Demo.App
            export fn i32[min max] main()
            {
                return Demo.Lib.SecretNumber;
            }
            """);

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "STK3015"
                && diagnostic.Message.Contains("Demo.Lib.SecretNumber", StringComparison.Ordinal));
    }

    [Fact]
    public void SiblingReadingPublicGlobalSucceeds()
    {
        var result = RunApp(
            """
            import Demo.Lib
            module Demo.App
            export fn i32[min max] main()
            {
                return Demo.Lib.SharedNumber;
            }
            """);

        Assert.True(
            result.Succeeded,
            string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }

    [Fact]
    public void SiblingNamingModulePrivateTypeInAnnotationIsRejected()
    {
        var result = RunApp(
            """
            import Demo.Lib
            module Demo.App
            fn i32[min max] Use(borrow Demo.Lib.HiddenType t)
            {
                return 0;
            }
            export fn i32[min max] main() { return 0; }
            """);

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "STK3015"
                && diagnostic.Message.Contains("Demo.Lib.HiddenType", StringComparison.Ordinal)
                && diagnostic.Message.Contains("not visible from module 'Demo.App'", StringComparison.Ordinal));
    }

    [Fact]
    public void SiblingNamingPublicTypeInAnnotationSucceeds()
    {
        var result = RunApp(
            """
            import Demo.Lib
            module Demo.App
            fn i32[min max] Use(borrow Demo.Lib.OpenType t)
            {
                return 0;
            }
            export fn i32[min max] main() { return 0; }
            """);

        Assert.True(
            result.Succeeded,
            string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }

    private static CompilationResult RunApp(string appSource)
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-module-privacy-");
        try
        {
            Directory.CreateDirectory(Path.Combine(tempDirectory.FullName, "Demo"));
            File.WriteAllText(Path.Combine(tempDirectory.FullName, "Demo", "Lib.stark"), LibrarySource);
            var appPath = Path.Combine(tempDirectory.FullName, "App.stark");
            File.WriteAllText(appPath, appSource);

            return DefaultCompilerPipeline.Create().Run(
                new CompilationInput(appSource, appPath),
                new CompilerOptions(
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));
        }
        finally
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
}
