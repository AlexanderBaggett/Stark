using Stark.Compiler;

namespace compiler.FeatureTests;

public sealed class CopyabilityStructuralFactTests : FeatureLlvmTestBase
{
    [Fact]
    public void CopyabilityFactUsesRecursiveOwnershipAndDestructorRules()
    {
        var llvm = CompileToLlvm("""
            module CopyProof
            struct Plain { u32[0 max][3] Values; }
            enum Choice { Empty, Value(Plain), }
            struct Resource { u32[0 max] Id; mut drop { self.Id = 0; } }
            struct Wrapper { Resource Value; }
            enum OwnedChoice { Empty, Value(Wrapper), }
            finite law bool Run()
            {
                return comptime (
                    System.Compiler.IsCopyable<u32[0 max]>()
                    && System.Compiler.IsCopyable<f64>()
                    && System.Compiler.IsCopyable<Plain>()
                    && System.Compiler.IsCopyable<Choice>()
                    && !System.Compiler.IsCopyable<Resource>()
                    && !System.Compiler.IsCopyable<Wrapper>()
                    && !System.Compiler.IsCopyable<OwnedChoice>()
                    && !System.Compiler.IsCopyable<Resource[2]>()
                    && !System.Compiler.IsCopyable<dynamic u32[0 max]>()
                    && !System.Compiler.IsCopyable<borrow u32[0 max]>());
            }
            """, new CompilerOptions());
        Assert.Contains("ret i1 true", ExtractDefinitionBody(llvm, "Run"));
    }
    [Fact]
    public void CopyabilityFactCannotBeCalledAtRuntime()
    {
        var result = DefaultCompilerPipeline.Create().Run(new CompilationInput("""
            module CopyProofRuntime
            finite law bool Run() { return System.Compiler.IsCopyable<u32[0 max]>(); }
            """), new CompilerOptions(StopAfterPassId: "type-check"));
        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "STK3054");
    }

}
