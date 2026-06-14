using Stark.Compiler;

namespace compiler.Tests;

/// <summary>
/// Regression for bare-name ambiguity inside an imported source module. A name
/// exported by two of the module's own imports once crashed lower-mir with STK9999
/// ("Named operand could not be resolved"). Imported source bodies are now
/// type-checked, so an ambiguous CALL is reported by overload resolution as a located
/// STK3022 naming the candidate overloads. The workaround ambiguity scan no longer
/// double-reports calls; it still covers non-call references (member access, indexing,
/// a bare function value) as STK3003. Qualifying the name compiles cleanly.
/// </summary>
public sealed class ImportedModuleAmbiguityRegressionTests
{
    private const string ModuleASource =
        """
        module ModA

        public fn i32[min max] Shared()
        {
            return 1;
        }
        """;

    private const string ModuleBSource =
        """
        module ModB

        public fn i32[min max] Shared()
        {
            return 2;
        }
        """;

    private const string AppSource =
        """
        import Mid

        module Main

        export fn i32[min max] main()
        {
            return UseShared();
        }
        """;

    [Fact]
    public void BareNameAmbiguousAcrossImportsReportsLocatedDiagnosticInsteadOfCrashing()
    {
        const string ambiguousMid =
            """
            import ModA
            import ModB

            module Mid

            public fn i32[min max] UseShared()
            {
                return Shared();
            }
            """;

        var result = Run(ambiguousMid, emitLlvm: true);

        Assert.False(result.Succeeded);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "STK9999");
        // The ambiguous call is now reported by overload resolution (STK3022) with the
        // candidate overloads; the workaround ambiguity scan no longer double-reports calls.
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "STK3003");
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "STK3022"
                && diagnostic.Message.Contains("'Shared' is ambiguous", StringComparison.Ordinal)
                && diagnostic.Message.Contains("ModA.Shared", StringComparison.Ordinal)
                && diagnostic.Message.Contains("ModB.Shared", StringComparison.Ordinal));
        // The diagnostic is attributed to the imported module, not the root file.
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "STK3022"
                && diagnostic.Location?.FilePath?.EndsWith("Mid.stark", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void QualifyingTheAmbiguousNameCompilesCleanly()
    {
        const string qualifiedMid =
            """
            import ModA
            import ModB

            module Mid

            public fn i32[min max] UseShared()
            {
                return ModA.Shared();
            }
            """;

        var result = Run(qualifiedMid, emitLlvm: true);

        Assert.True(
            result.Succeeded,
            string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }

    [Fact]
    public void LocalShadowingAnAmbiguousNameIsNotFlagged()
    {
        const string shadowedMid =
            """
            import ModA
            import ModB

            module Mid

            public fn i32[min max] UseShared()
            {
                stack i32[min max] Shared = 7;
                return Shared;
            }
            """;

        var result = Run(shadowedMid, emitLlvm: false);

        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "STK3003");
    }

    private static CompilationResult Run(string midSource, bool emitLlvm)
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-imported-ambiguity-");
        try
        {
            File.WriteAllText(Path.Combine(tempDirectory.FullName, "ModA.stark"), ModuleASource);
            File.WriteAllText(Path.Combine(tempDirectory.FullName, "ModB.stark"), ModuleBSource);
            File.WriteAllText(Path.Combine(tempDirectory.FullName, "Mid.stark"), midSource);
            var appPath = Path.Combine(tempDirectory.FullName, "Main.stark");
            File.WriteAllText(appPath, AppSource);

            return DefaultCompilerPipeline.Create().Run(
                new CompilationInput(AppSource, appPath),
                new CompilerOptions(
                    EmitLlvmIr: emitLlvm,
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
