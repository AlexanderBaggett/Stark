using Stark.Compiler;

namespace compiler.FeatureTests;

public sealed class TraitsAndDoctrinesFeatureTests : FeatureLlvmTestBase
{
    [Fact]
    public void DoctrineLawCallsStayDirectAndPreserveBorrowFacts()
    {
        var llvm = CompileToLlvm(
            """
            module Demo

            struct Box
            {
                i32[min max] Value;
            }

            doctrine Inspect
            {
                law i32[min max] Read(borrow Box box)
                {
                    return box.Value;
                }
            }

            law i32[min max] Run(borrow Box box)
            {
                return Inspect.Read(box);
            }
            """,
            new CompilerOptions(OptimizationLevel: CompilerOptimizationLevel.O0));

        var inspectHeader = ExtractDefinitionHeader(llvm, "Inspect_Read");
        var runHeader = ExtractDefinitionHeader(llvm, "Run");
        var runBody = ExtractDefinitionBody(llvm, "Run");

        Assert.Contains("ptr noundef nonnull noalias readonly nocapture dereferenceable(4) align 4 %arg_box", inspectHeader);
        Assert.Contains("nounwind willreturn mustprogress nosync nofree memory(argmem: read)", inspectHeader);
        Assert.Contains("alwaysinline", inspectHeader);
        Assert.DoesNotContain("memory(readwrite", inspectHeader);

        Assert.Contains("ptr noundef nonnull noalias readonly nocapture dereferenceable(4) align 4 %arg_box", runHeader);
        Assert.Contains("nounwind willreturn mustprogress nosync nofree memory(argmem: read)", runHeader);
        Assert.Contains("alwaysinline", runHeader);
        Assert.DoesNotContain("memory(readwrite", runHeader);

        Assert.Contains("call fastcc i32 @Inspect_Read(ptr nonnull noalias readonly nocapture dereferenceable(4) align 4", runBody);
        Assert.DoesNotContain("vtable", llvm, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("fnptr", llvm, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("dispatch", llvm, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GenericDoctrineMethodsPreserveStaticFiniteLawBorrowContracts()
    {
        var llvm = CompileToLlvm(
            """
            module Demo

            struct Box<T>
            {
                T Value;
            }

            doctrine Inspect<T>
            {
                finite law T Read(borrow Box<T> box)
                {
                    return box.Value;
                }
            }

            finite law i32[min max] Run(borrow Box<i32[min max]> box)
            {
                return Inspect<i32[min max]>.Read(box);
            }
            """,
            new CompilerOptions(OptimizationLevel: CompilerOptimizationLevel.O0));

        var runHeader = ExtractDefinitionHeader(llvm, "Run");
        var specializationHeader = ExtractDefinitionHeader(llvm, "__stark_mono_fn_Demo__Inspect_Read__i32");
        var runBody = ExtractDefinitionBody(llvm, "Run");

        Assert.Contains("define internal dso_local fastcc noundef i32 @__stark_mono_fn_Demo__Inspect_Read__i32(", specializationHeader);
        Assert.Contains("ptr noundef nonnull noalias readonly nocapture dereferenceable(4) align 4 %arg_box", specializationHeader);
        Assert.Contains("nounwind willreturn mustprogress nosync nofree memory(argmem: read)", specializationHeader);
        Assert.Contains("alwaysinline", specializationHeader);
        Assert.DoesNotContain("memory(readwrite", specializationHeader);

        Assert.Contains("ptr noundef nonnull noalias readonly nocapture dereferenceable(4) align 4 %arg_box", runHeader);
        Assert.Contains("nounwind willreturn mustprogress nosync nofree memory(argmem: read)", runHeader);
        Assert.Contains("alwaysinline", runHeader);
        Assert.DoesNotContain("memory(readwrite", runHeader);

        Assert.Contains(
            "call fastcc i32 @__stark_mono_fn_Demo__Inspect_Read__i32(ptr nonnull noalias readonly nocapture dereferenceable(4) align 4",
            runBody);
        Assert.DoesNotContain("declare fastcc noundef ptr @Inspect_Read(", llvm);
        Assert.DoesNotContain("define fastcc noundef ptr @Inspect_Read(", llvm);
        Assert.DoesNotContain("define fastcc noundef i32 @Inspect_Read(", llvm);
        Assert.DoesNotContain("vtable", llvm, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("fnptr", llvm, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("dispatch", llvm, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TraitContractsDoNotEmitRuntimeDispatchSurface()
    {
        var llvm = CompileToLlvm(
            """
            module Demo

            struct Box
            {
                i32[min max] Value;
            }

            trait Reader<T>
            {
                finite law T Read(borrow Box box);
            }

            finite law i32[min max] Run(i32[min max] value)
            {
                return value;
            }
            """);

        var runHeader = ExtractDefinitionHeader(llvm, "Run");

        Assert.Contains("nounwind willreturn mustprogress nosync nofree memory(none)", runHeader);
        Assert.DoesNotContain("@Reader_Read", llvm);
        Assert.DoesNotContain("__stark_mono_fn_Demo__Reader_Read", llvm);
        Assert.DoesNotContain("vtable", llvm, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("dispatch", llvm, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("fnptr", llvm, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GenericTraitBoundDispatchLowersToDirectConcreteCall()
    {
        var llvm = CompileToLlvm(
            """
            module Demo

            trait Drawable
            {
                finite law i32[min max] Width(borrow Self self);
            }

            struct Widget : Drawable
            {
                i32[min max] W;

                finite law i32[min max] Width(borrow Widget self)
                {
                    return self.W;
                }
            }

            finite law i32[min max] DoubleWidth<T>(borrow T value) where T: Drawable
            {
                return value.Width() + value.Width();
            }

            export fn i32[min max] main()
            {
                stack Widget w = new Widget() { W = 5 };
                return DoubleWidth(w);
            }
            """,
            new CompilerOptions(OptimizationLevel: CompilerOptimizationLevel.O0));

        // A trait-method call on a `where T: Trait` generic must monomorphize to a
        // direct call to the concrete implementation -- no vtable, no indirect dispatch.
        Assert.Contains("call fastcc i32 @Widget_Width(", llvm);
        Assert.DoesNotContain("call fastcc i32 @Drawable_Width", llvm);
        Assert.DoesNotContain("vtable", llvm, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("dispatch", llvm, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("fnptr", llvm, StringComparison.OrdinalIgnoreCase);
    }
}
