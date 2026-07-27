using Stark.Compiler;

namespace compiler.Tests;

/// <summary>
/// `try` propagation inside an imported source module, exercised end-to-end through
/// LLVM emission. `Demo.Lib` is compiled from source (non-root); since the
/// cross-module monomorphization fix its bodies are fully type-checked in the
/// consuming build, so each `try` gets a normal TryPropagationTypingRecord and the
/// generic `GenericResult<i32[min max]>` used only inside `Demo.Lib` is planned from
/// its (now type-checked) use sites. Covers same-error binding propagation, the
/// `from` funnel, unit failure in bare-statement position, and a generic-operand
/// instantiation bound through a local.
/// </summary>
public sealed class TryPropagationImportedModuleTests
{
    private const string LibrarySource =
        """
        module Demo.Lib

        public enum InnerError
        {
            Bad,
        }

        public enum InnerResult
        {
            [Ok] Ok(i32[min max]),
            [Err] Err(InnerError),
        }

        public enum UnitResult
        {
            [Ok] Done,
            [Err] Stopped,
        }

        public enum OuterError
        {
            Wrapped from InnerError,
            Other,
        }

        public enum OuterResult
        {
            [Ok] Ok(i32[min max]),
            [Err] Err(OuterError),
        }

        public enum GenericResult<T>
        {
            [Ok] Ok(T),
            [Err] Err(InnerError),
        }

        fn InnerResult Produce(i32[min max] value)
        {
            if (value < 0)
            {
                return InnerResult.Err(InnerError.Bad);
            }

            return InnerResult.Ok(value);
        }

        fn UnitResult CheckUnit(i32[min max] value)
        {
            if (value < 0)
            {
                return UnitResult.Stopped;
            }

            return UnitResult.Done;
        }

        fn GenericResult<i32[min max]> ProduceGeneric(i32[min max] value)
        {
            if (value < 0)
            {
                return GenericResult<i32[min max]>.Err(InnerError.Bad);
            }

            return GenericResult<i32[min max]>.Ok(value);
        }

        // Same-error propagation through a binding initializer.
        public fn InnerResult ConsumeSameError(i32[min max] value)
        {
            stack i32[min max] inner = try Produce(value);
            return InnerResult.Ok(inner + 1);
        }

        // Cross-family propagation through the `from` funnel, binding position.
        public fn OuterResult ConsumeFunnel(i32[min max] value)
        {
            stack i32[min max] inner = try Produce(value);
            return OuterResult.Ok(inner + 1);
        }

        // Unit failure propagation in bare expression-statement position.
        public fn UnitResult ConsumeUnit(i32[min max] value)
        {
            try CheckUnit(value);
            return UnitResult.Done;
        }

        // Propagation from a generic operand instantiation, bound through a local.
        // (`try` may not be nested inside another expression such as `Ok(try ...)`.)
        public fn InnerResult ConsumeGenericOperand(i32[min max] value)
        {
            stack i32[min max] produced = try ProduceGeneric(value);
            return InnerResult.Ok(produced);
        }
        """;

    private const string AppSource =
        """
        import Demo.Lib
        module Demo.App

        export fn i32[min max] main()
        {
            stack mut i32[min max] total = 0;
            switch (Demo.Lib.ConsumeSameError(2))
            {
                case Demo.Lib.InnerResult.Err(var sameError):
                    return 1;
                case Demo.Lib.InnerResult.Ok(var sameValue):
                    total += sameValue;
            }

            switch (Demo.Lib.ConsumeFunnel(-1))
            {
                case Demo.Lib.OuterResult.Ok(var unexpected):
                    return 2;
                case Demo.Lib.OuterResult.Err(var funnelError):
                    switch (funnelError)
                    {
                        case Demo.Lib.OuterError.Wrapped(var wrapped):
                            total += 1;
                        case Demo.Lib.OuterError.Other:
                            return 3;
                    }
            }

            switch (Demo.Lib.ConsumeUnit(5))
            {
                case Demo.Lib.UnitResult.Stopped:
                    return 4;
                case Demo.Lib.UnitResult.Done:
                    total += 1;
            }

            switch (Demo.Lib.ConsumeGenericOperand(7))
            {
                case Demo.Lib.InnerResult.Err(var genericError):
                    return 5;
                case Demo.Lib.InnerResult.Ok(var genericValue):
                    total += genericValue;
            }

            // 3 + 1 + 1 + 7; GenericResult<i32[min max]> is instantiated only
            // inside the imported module and must be planned from its use sites.
            return total - 12;
        }
        """;

    [Fact]
    public void TryInsideImportedSourceModuleLowersThroughTheWholePipeline()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-imported-try-");
        try
        {
            Directory.CreateDirectory(Path.Combine(tempDirectory.FullName, "Demo"));
            File.WriteAllText(Path.Combine(tempDirectory.FullName, "Demo", "Lib.stark"), LibrarySource);
            var appPath = Path.Combine(tempDirectory.FullName, "App.stark");
            File.WriteAllText(appPath, AppSource);

            var result = DefaultCompilerPipeline.Create().Run(
                new CompilationInput(AppSource, appPath),
                new CompilerOptions(
                    EmitLlvmIr: true,
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.LlvmIrModule, out LlvmIrModule? llvmModule));
            Assert.NotNull(llvmModule);
            var llvm = llvmModule!.Text;

            Assert.Contains("@Demo_Lib_ConsumeSameError(", llvm, StringComparison.Ordinal);
            Assert.Contains("@Demo_Lib_ConsumeFunnel(", llvm, StringComparison.Ordinal);
            Assert.Contains("@Demo_Lib_ConsumeUnit(", llvm, StringComparison.Ordinal);
            Assert.Contains("@Demo_Lib_ConsumeGenericOperand(", llvm, StringComparison.Ordinal);
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
