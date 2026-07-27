using Stark.Compiler;

namespace compiler.Tests;

/// <summary>
/// Alias-aware memory optimization regression: load-indirect (field)
/// forwarding dropped the defining load using a per-block replacement map
/// without checking that all uses live in the same block — unlike the
/// load-local and load-global cases, which require
/// ValueUsesAreConfinedToBlock. A result consumed from another block (the
/// classic case: an inlined call's continuation phi referencing the inlined
/// return value) kept referencing the deleted load, and validate-ssa failed
/// with STK5002 "value reference '%vN_inlK' is not defined in this SSA
/// function". Surfaced by nested inlining: an imported law inlined twice
/// into a caller that is itself inlined into main.
/// </summary>
public sealed class SsaCrossBlockLoadForwardingRegressionTests
{
    [Fact]
    public void InlinedLawReturnValueSurvivesFieldLoadForwardingAcrossBlocks()
    {
        const string dependencySource =
            """
            module Dep

            public struct Holder
            {
                i64[min max] Value;

                Holder()
                {
                    self.Value = SeedValue();
                }

                public finite law i64[min max] CountValue(borrow Holder self)
                {
                    return self.Value;
                }
            }

            public finite law i64[min max] SeedValue()
            {
                return 7;
            }
            """;
        const string rootSource =
            """
            import Dep
            module Demo

            fn i64[min max] ReadHolder(borrow Dep.Holder holder)
            {
                if (holder.CountValue() < 0)
                {
                    return 0;
                }

                return holder.CountValue();
            }

            export fn i32[min max] main()
            {
                stack Dep.Holder holder = new Dep.Holder();
                if (ReadHolder(holder) < 0)
                {
                    return 1;
                }

                return 0;
            }
            """;

        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(rootSource),
            new CompilerOptions(
                EmitLlvmIr: true,
                ModuleResolver: new InMemoryModuleResolver(
                [
                    (
                        new ResolvedModuleReference("Dep", "/virtual/Dep.stark", IsExternal: false),
                        dependencySource,
                        "/virtual/Dep.stark"
                    )
                ])));

        Assert.True(
            result.Succeeded,
            string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }
}
