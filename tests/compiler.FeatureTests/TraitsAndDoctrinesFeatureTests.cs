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

    [Fact]
    public void ImportedGenericTraitBoundDispatchLowersToDirectConcreteCall()
    {
        var llvm = CompileToLlvm(
            """
            import Contracts
            module Demo

            struct Widget : Contracts.Drawable
            {
                i32[min max] W;

                finite law i32[min max] Width(borrow Widget self)
                {
                    return self.W;
                }
            }

            finite law i32[min max] DoubleWidth<T>(borrow T value) where T: Contracts.Drawable
            {
                return value.Width() + value.Width();
            }

            export fn i32[min max] main()
            {
                stack Widget w = new Widget() { W = 5 };
                return DoubleWidth(w);
            }
            """,
            new CompilerOptions(
                OptimizationLevel: CompilerOptimizationLevel.O0,
                ModuleResolver: new InMemoryModuleResolver(
                [
                    (
                        new ResolvedModuleReference("Contracts", "/virtual/Contracts.stark", IsExternal: false),
                        """
                        module Contracts

                        public trait Drawable
                        {
                            finite law i32[min max] Width(borrow Self self);
                        }
                        """,
                        "/virtual/Contracts.stark"
                    )
                ])));

        Assert.Contains("call fastcc i32 @Widget_Width(", llvm);
        Assert.DoesNotContain("call fastcc i32 @Contracts_Drawable_Width", llvm);
        Assert.DoesNotContain("vtable", llvm, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("dispatch", llvm, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("fnptr", llvm, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TraitDefaultMethodsDispatchToMonomorphizedDirectCalls()
    {
        var llvm = CompileToLlvm(
            """
            module Demo

            trait Greeter
            {
                finite law i32[min max] Base(borrow Self self);

                finite law i32[min max] Doubled(borrow Self self)
                {
                    return self.Base() * 2;
                }
            }

            struct Widget : Greeter
            {
                i32[min max] V;

                finite law i32[min max] Base(borrow Widget self)
                {
                    return self.V;
                }
            }

            finite law i32[min max] CallIt<T>(borrow T value) where T: Greeter
            {
                return value.Doubled();
            }

            export fn i32[min max] main()
            {
                stack Widget w = new Widget() { V = 9 };
                return w.Doubled() + CallIt(w);
            }
            """,
            new CompilerOptions(OptimizationLevel: CompilerOptimizationLevel.O0));

        // A not-overridden default method dispatches to the default body monomorphized
        // for the concrete type (both directly and through a `where T: Trait` generic),
        // and the abstract method it calls resolves to the concrete override -- all
        // direct calls, no vtable/indirect dispatch.
        Assert.Contains("@__stark_mono_fn_Demo__Greeter_Doubled__Widget(", llvm);
        Assert.Contains("call fastcc i32 @Widget_Base(", llvm);
        Assert.DoesNotContain("vtable", llvm, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("dispatch", llvm, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("fnptr", llvm, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DynTraitObjectDispatchesThroughVtablePreservingEffectContract()
    {
        var llvm = CompileToLlvm(
            """
            module Demo

            dyn trait Speaker
            {
                finite law i32[min max] Speak(borrow Self self);
            }

            struct Dog : Speaker
            {
                i32[min max] Volume;

                finite law i32[min max] Speak(borrow Dog self)
                {
                    return self.Volume;
                }
            }

            export fn i32[min max] main()
            {
                stack Dog d = new Dog() { Volume = 7 };
                stack borrow dyn Speaker s = d;
                return s.Speak();
            }
            """,
            new CompilerOptions(OptimizationLevel: CompilerOptimizationLevel.O0));

        // A read-only vtable is synthesized for the (type, trait) pair: the Speak
        // slot points at the concrete implementation, followed by the type's drop
        // thunk in the drop slot (used by owning `heap dyn` objects, ignored by this
        // borrowed view).
        Assert.Contains("@__stark_vtable_Dog__Speaker = private unnamed_addr constant { ptr, ptr } { ptr @Dog_Speak, ptr @Dog___dyn_drop }", llvm);

        // The dynamic call is an INDIRECT call through the loaded vtable slot (target
        // is an SSA value, not a direct `@Dog_Speak`), and -- the cost-transparency
        // payoff -- the `finite law` effect contract survives erasure: the indirect
        // call site still carries the law/finite attributes.
        Assert.Contains("getelementptr ptr,", llvm);
        Assert.Matches(@"call fastcc i32 %\w+\(ptr[^\n]*nounwind willreturn mustprogress nosync nofree", llvm);
    }
}
