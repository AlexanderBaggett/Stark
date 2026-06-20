using Stark.Compiler;

namespace compiler.FeatureTests;

public sealed class TraitsAndDoctrinesFeatureTests : FeatureLlvmTestBase
{
    [Fact]
    public void ExplicitOpsTableDispatchStaysVisibleAndIndirect()
    {
        var llvm = CompileToLlvm(
            """
            module Demo

            struct ModuleResolverOps
            {
                fnptr<unsafe finite law i32[min max](rawptr<i8[min max]>, ascii)> Resolve;
                fnptr<unsafe finite law void(rawptr<i8[min max]>)> Reset;
            }

            struct ModuleResolverHandle
            {
                rawptr<i8[min max]> Context;
                ModuleResolverOps Ops;
            }

            unsafe finite law i32[min max] ResolveFile(rawptr<i8[min max]> context, ascii moduleName)
            {
                return 40;
            }

            unsafe finite law i32[min max] ResolveMemory(rawptr<i8[min max]> context, ascii moduleName)
            {
                return 2;
            }

            unsafe finite law void ResetMemory(rawptr<i8[min max]> context)
            {
                return;
            }

            finite law i32[min max] Dispatch(borrow ModuleResolverHandle resolver, ascii moduleName)
            {
                unsafe
                {
                    resolver.Ops.Reset(resolver.Context);
                    return resolver.Ops.Resolve(resolver.Context, moduleName);
                }
            }

            finite law i32[min max] Run()
            {
                stack ModuleResolverOps ops = new ModuleResolverOps()
                {
                    Resolve = ResolveMemory,
                    Reset = ResetMemory
                };
                stack ModuleResolverHandle resolver = new ModuleResolverHandle()
                {
                    Context = null,
                    Ops = ops
                };
                return Dispatch(resolver, "core");
            }
            """,
            new CompilerOptions());

        var dispatchHeader = ExtractDefinitionHeader(llvm, "Dispatch");
        var dispatchBody = ExtractDefinitionBody(llvm, "Dispatch");

        Assert.Contains("nounwind willreturn mustprogress nosync nofree memory(read)", dispatchHeader);
        Assert.DoesNotContain("memory(readwrite", dispatchHeader);
        Assert.Contains("call fastcc i32 %", dispatchBody);
        Assert.Contains("call fastcc void %", dispatchBody);
        Assert.Contains("nounwind willreturn mustprogress nosync nofree memory(read)", dispatchBody);
        Assert.Contains("load ptr", dispatchBody);
        Assert.DoesNotContain("call fastcc i32 @ResolveFile", dispatchBody);
        Assert.DoesNotContain("call fastcc i32 @ResolveMemory", dispatchBody);
        Assert.DoesNotContain("call fastcc void @ResetMemory", dispatchBody);
        Assert.Contains("@ResolveMemory", llvm);
        Assert.DoesNotContain("__stark_vtable", llvm);
    }

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
                    stack i32[min max] a = box.Value + (box.Value / 3);
                    stack i32[min max] b = a - (a / 5);
                    stack i32[min max] c = b + (b / 7);
                    stack i32[min max] d = c - (c / 11);
                    stack i32[min max] e = d + (d / 13);
                    stack i32[min max] f = e - (e / 17);
                    stack i32[min max] g = f + (f / 19);
                    stack i32[min max] h = g - (g / 23);
                    stack i32[min max] i = h + (h / 29);
                    stack i32[min max] j = i - (i / 31);
                    stack i32[min max] k = j + (j / 37);
                    stack i32[min max] l = k - (k / 41);
                    stack i32[min max] m = l + (l / 43);
                    return m;
                }
            }

            law i32[min max] Run(borrow Box box)
            {
                return Inspect.Read(box);
            }
            """,
            new CompilerOptions());

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
                    stack mut i64[min max] acc = 977;
                    stack mut i64[min max] step = 0;
                    while willexit (step < 2)
                    {
                        acc = acc + (acc / 3);
                        acc = acc - (acc / 5);
                        acc = acc + (acc / 7);
                        acc = acc - (acc / 11);
                        acc = acc + (acc / 13);
                        acc = acc - (acc / 17);
                        acc = acc + (acc / 19);
                        acc = acc - (acc / 23);
                        acc = acc + (acc / 29);
                        acc = acc - (acc / 31);
                        step = step + 1;
                    }

                    if (acc > -4000000000000000000)
                    {
                        return box.Value;
                    }

                    return box.Value;
                }
            }

            finite law i32[min max] Run(borrow Box<i32[min max]> box)
            {
                return Inspect<i32[min max]>.Read(box);
            }
            """,
            new CompilerOptions());

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
            new CompilerOptions());

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
            new CompilerOptions());

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
        // The dyn object crosses an export boundary so the closed-world
        // devirtualizer cannot prove a single target; a locally-built dyn whose
        // vtable is statically known is (correctly) devirtualized to a direct
        // call instead.
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

            export fn i32[min max] Announce(borrow dyn Speaker speaker)
            {
                return speaker.Speak();
            }

            export fn i32[min max] main()
            {
                stack Dog d = new Dog() { Volume = 7 };
                stack borrow dyn Speaker s = d;
                return Announce(s);
            }
            """,
            new CompilerOptions());

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

    [Fact]
    public void DynTraitVtableTypeIsNameableAsReadonlyRawPointer()
    {
        var result = Compile(
            """
            module Demo

            dyn trait Speaker
            {
                finite law i32[min max] Speak(borrow Self self);
            }

            dyn trait Reader<T>
            {
                finite law T Read(borrow Self self);
            }

            unsafe finite law i32[min max] Use(
                rawptr<Speaker.Vtable> speakerTable,
                rawptr<Reader.Vtable<i32[min max]>> readerTable)
            {
                return 1;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static d => d.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? model));
        Assert.NotNull(model);
        Assert.True(model.Functions.TryGetValue("Use", out var signature));
        Assert.Collection(
            signature.Parameters,
            static parameter => Assert.Equal("rawptr<Speaker.Vtable>", parameter.Type.DisplayName),
            static parameter => Assert.Equal("rawptr<Reader.Vtable<i32>>", parameter.Type.DisplayName));
    }

    [Fact]
    public void DynTraitFatPointerCarriesTypedVtablePointerInMir()
    {
        var result = Compile(
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
            new CompilerOptions(StopAfterPassId: "lower-mir"));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static d => d.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
        Assert.NotNull(mir);

        var vtableExtract = Assert.Single(
            mir.Functions
                .SelectMany(static function => function.Blocks)
                .SelectMany(static block => block.Statements)
                .Select(static statement => statement.Value)
                .OfType<MidLevelIrExtractIndexRValue>(),
            static value => value.OperationFamily == IndexedElementOperationFamily.DynTraitComponent
                && value.ElementIndex == 1);
        Assert.Equal("rawptr<Speaker.Vtable>", vtableExtract.Type.DisplayName);
    }

    [Fact]
    public void DynTraitVtableTypeRejectsByValueAndMutablePointerUse()
    {
        var result = Compile(
            """
            module Demo

            dyn trait Speaker
            {
                finite law i32[min max] Speak(borrow Self self);
            }

            struct BadHolder
            {
                Speaker.Vtable Table;
            }

            unsafe finite law i32[min max] Bad(rawmutptr<Speaker.Vtable> table)
            {
                return 1;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.True(
            result.Diagnostics.Count(static diagnostic =>
                diagnostic.Code == "STK3035"
                && diagnostic.Message.Contains("compiler-owned dyn-trait vtable", StringComparison.Ordinal)) >= 2,
            string.Join(Environment.NewLine, result.Diagnostics.Select(static d => d.ToString())));
    }

    [Fact]
    public void UnsafeDynTraitFromPartsCanRoundTripContextAndVtable()
    {
        var result = Compile(
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

            unsafe finite law i32[min max] RoundTrip()
            {
                stack Dog d = new Dog() { Volume = 9 };
                stack borrow dyn Speaker natural = d;
                stack rawmutptr<i8[min max]> context = natural.Context;
                stack rawptr<Speaker.Vtable> table = natural.Vtable;
                stack borrow dyn Speaker explicitView = dynview<Speaker>(context, table);
                stack borrow dyn Speaker inferredView = dynview(context, table);
                return explicitView.Speak() + inferredView.Speak();
            }

            export fn i32[min max] main()
            {
                unsafe
                {
                    return RoundTrip();
                }
            }
            """,
            new CompilerOptions(StopAfterPassId: "lower-mir"));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static d => d.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? model));
        Assert.NotNull(model);
        Assert.Contains(
            model.BoundOperations,
            static operation => operation is BoundDynTraitFromPartsOperation { OperationName: "dynview", TargetType.DisplayName: "borrow dyn Speaker" });

        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
        Assert.NotNull(mir);
        var statements = mir.Functions
            .SelectMany(static function => function.Blocks)
            .SelectMany(static block => block.Statements)
            .Select(static statement => statement.Value)
            .ToArray();

        Assert.Contains(
            statements.OfType<MidLevelIrExtractIndexRValue>(),
            static value => value.OperationFamily == IndexedElementOperationFamily.DynTraitComponent
                && value.ElementIndex == 0
                && value.Type.DisplayName == "rawmutptr<i8>");
        Assert.Contains(
            statements.OfType<MidLevelIrExtractIndexRValue>(),
            static value => value.OperationFamily == IndexedElementOperationFamily.DynTraitComponent
                && value.ElementIndex == 1
                && value.Type.DisplayName == "rawptr<Speaker.Vtable>");
        Assert.Contains(
            statements.OfType<MidLevelIrInsertIndexRValue>(),
            static value => value.OperationFamily == IndexedElementOperationFamily.DynTraitComponent
                && value.ElementIndex == 0
                && value.Type.DisplayName == "borrow dyn Speaker");
        Assert.Contains(
            statements.OfType<MidLevelIrInsertIndexRValue>(),
            static value => value.OperationFamily == IndexedElementOperationFamily.DynTraitComponent
                && value.ElementIndex == 1
                && value.Type.DisplayName == "borrow dyn Speaker");
    }

    [Fact]
    public void UnsafeDynTraitFromPartsRejectsSafeUseAndWrongVtable()
    {
        var result = Compile(
            """
            module Demo

            dyn trait Speaker
            {
                finite law i32[min max] Speak(borrow Self self);
            }

            dyn trait Reader
            {
                finite law i32[min max] Read(borrow Self self);
            }

            finite law i32[min max] SafeConstruction(rawmutptr<i8[min max]> context, rawptr<Speaker.Vtable> table)
            {
                stack borrow dyn Speaker value = dynview<Speaker>(context, table);
                return 1;
            }

            finite law i32[min max] SafeDecompose(borrow dyn Speaker value)
            {
                stack rawmutptr<i8[min max]> context = value.Context;
                return 1;
            }

            unsafe finite law i32[min max] WrongTable(rawmutptr<i8[min max]> context, rawptr<Reader.Vtable> table)
            {
                stack borrow dyn Speaker value = dynview<Speaker>(context, table);
                return 1;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3024"
                && diagnostic.Message.Contains("dynview(context, vtable)", StringComparison.Ordinal));
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3024"
                && diagnostic.Message.Contains("representation member '.Context'", StringComparison.Ordinal));
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3002"
                && diagnostic.Message.Contains("rawptr<Speaker.Vtable>", StringComparison.Ordinal)
                && diagnostic.Message.Contains("rawptr<Reader.Vtable>", StringComparison.Ordinal));
    }

    [Fact]
    public void UnsafeDynBoxFromPartsConstructsOwnedDynTraitObject()
    {
        var result = Compile(
            """
            module Demo

            dyn trait Speaker
            {
                finite law i32[min max] Speak(borrow Self self);
            }

            unsafe finite law heap dyn Speaker Adopt(rawmutptr<i8[min max]> context, rawptr<Speaker.Vtable> table)
            {
                return dynbox<Speaker>(context, table);
            }
            """,
            new CompilerOptions(StopAfterPassId: "lower-mir"));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static d => d.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? model));
        Assert.NotNull(model);
        Assert.Contains(
            model.BoundOperations,
            static operation => operation is BoundDynTraitFromPartsOperation { OperationName: "dynbox", TargetType.DisplayName: "heap dyn Speaker" });
    }

    [Fact]
    public void TraitAssociatedTypeRequirementSubstitutesIntoConcreteImplementation()
    {
        var llvm = CompileToLlvm(
            """
            module Demo

            trait Reader
            {
                alias Item;

                finite law Self.Item Read(borrow Self self);
            }

            struct Counter : Reader
            {
                alias Item = i32[min max];

                i32[min max] Value;

                finite law i32[min max] Read(borrow Counter self)
                {
                    return self.Value;
                }
            }

            finite law T.Item ReadGeneric<T>(borrow T value) where T: Reader
            {
                return value.Read();
            }

            export fn i32[min max] main()
            {
                stack Counter c = new Counter() { Value = 41 };
                return ReadGeneric(c) + 1;
            }
            """,
            new CompilerOptions());

        Assert.Contains("call fastcc i32 @Counter_Read(", llvm);
        Assert.Contains("__stark_mono_fn_Demo__ReadGeneric__Counter", llvm);
        Assert.DoesNotContain("vtable", llvm, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("fnptr", llvm, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TraitAssociatedTypeRequirementMustBeDefinedByImplementer()
    {
        var result = Compile(
            """
            module Demo

            trait Reader
            {
                alias Item;

                finite law Self.Item Read(borrow Self self);
            }

            struct Counter : Reader
            {
                i32[min max] Value;

                finite law i32[min max] Read(borrow Counter self)
                {
                    return self.Value;
                }
            }
            """);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "STK3052");
    }

    [Fact]
    public void TraitAssociatedTypeMismatchIsAConformanceError()
    {
        var result = Compile(
            """
            module Demo

            trait Reader
            {
                alias Item;

                finite law Self.Item Read(borrow Self self);
            }

            struct Counter : Reader
            {
                alias Item = i64[min max];

                i32[min max] Value;

                finite law i32[min max] Read(borrow Counter self)
                {
                    return self.Value;
                }
            }
            """);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "STK3033");
    }

    [Fact]
    public void TraitDefaultAssociatedTypeDoesNotRequireImplementationAlias()
    {
        var llvm = CompileToLlvm(
            """
            module Demo

            trait HasHash
            {
                alias Code = u64[0 max];

                finite law Self.Code Hash(borrow Self self);
            }

            struct Key : HasHash
            {
                u64[0 max] Value;

                finite law u64[0 max] Hash(borrow Key self)
                {
                    return self.Value;
                }
            }

            export fn u64[0 max] main()
            {
                stack Key key = new Key() { Value = 99 };
                return key.Hash();
            }
            """,
            new CompilerOptions());

        Assert.Contains("call fastcc i64 @Key_Hash(", llvm);
    }
}
