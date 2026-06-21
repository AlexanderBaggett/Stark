using Stark.Compiler;
using System.Text.RegularExpressions;

namespace compiler.Tests;

public sealed class LlvmIrEmissionTests
{
    [Fact]
    public void StraightLineFunctionEmitsOptimizedLlvmBody()
    {
        var result = Compile(
            """
            module Demo

            unsafe finite law i32[min max] Run()
            {
                stack mut i32[min max] value = 1;
                value = value + 1;
                return value;
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvmRaw(result);

        Assert.Contains("define fastcc noundef i32 @Run()", llvm);
        Assert.Contains("ret i32 2", llvm);
        Assert.DoesNotContain("add i32", llvm);
        Assert.DoesNotContain("alloca i32", llvm);
        Assert.DoesNotContain("declare fastcc i32 @Run()", llvm);
    }

    [Fact]
    public void DynamicRawPointerStorageSliceLowersToSlicePairInLocalAndArgumentPositions()
    {
        // Regression: the assignment-target place resolvers used to treat the
        // start/count bracket over a dynamic of raw pointers as two chained
        // single-index steps ((argv[0])[count]), emitting a pointee-typed load
        // that was then stored as the slice pair.
        var result = Compile(
            """
            module Demo

            unsafe fn u64[0 2 ** 63 - 1] CountEntries(borrow rawptr<i8[min max]>[] entries)
            {
                return entries.Length;
            }

            export unsafe fn i32[min max] main()
            {
                stack mut dynamic rawptr<i8[min max]> pointers = new();
                if (!pointers.TryReserve(2))
                {
                    return 1;
                }

                stack mut i8[min max] storage = 7;
                init pointers[pointers.Length] = &storage;
                init pointers[pointers.Length] = null;

                stack rawptr<i8[min max]>[] view = pointers[0, pointers.Length];
                if (view.Length != 2)
                {
                    return 2;
                }

                if (CountEntries(pointers[0, pointers.Length]) != 2)
                {
                    return 3;
                }

                if (*(view[0]) != 7)
                {
                    return 4;
                }

                return 0;
            }
            """);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);

        // Both slice uses (local binding and direct call argument) must build the
        // { ptr, i64 } pair from the element address and the count.
        Assert.Equal(2, Regex.Matches(llvm, @"insertvalue \{ ptr, i64 \} zeroinitializer, ptr ").Count);
        // The broken chained form loaded a pointee byte and stored it as the pair.
        Assert.DoesNotMatch(new Regex(@"%(\w+) = load i8[^\n]*\n\s*store \{ ptr, i64 \} %\1[,\s]"), llvm);
    }

    [Fact]
    public void DynamicRawPointerStorageSliceReassignmentLowersToSlicePair()
    {
        // Same regression family for plain reassignment of an existing slice
        // local from a start/count access over a dynamic of raw pointers.
        var result = Compile(
            """
            module Demo

            export unsafe fn i32[min max] main()
            {
                stack mut dynamic rawptr<i8[min max]> pointers = new();
                if (!pointers.TryReserve(1))
                {
                    return 1;
                }

                init pointers[pointers.Length] = null;
                stack mut rawptr<i8[min max]>[] view = pointers[0, 0];
                view = pointers[0, pointers.Length];
                if (view.Length != 1)
                {
                    return 2;
                }

                return 0;
            }
            """);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);

        // The dead initial slice store may be eliminated; the live reassignment
        // must still build the pair.
        Assert.True(
            Regex.Matches(llvm, @"insertvalue \{ ptr, i64 \} zeroinitializer, ptr ").Count >= 1,
            "Expected the dynamic start/count reassignment to lower to a slice-pair construction.");
        Assert.DoesNotMatch(new Regex(@"%(\w+) = load i8[^\n]*\n\s*store \{ ptr, i64 \} %\1[,\s]"), llvm);
    }

    [Fact]
    public void FunctionPointerCallsEmitFastccIndirectCall()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn i32[min max] Add(i32[min max] left, i32[min max] right)
            {
                stack i32[min max] a = left + (right / 3);
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

            unsafe fn i32[min max] Run()
            {
                stack fnptr<unsafe fn i32[min max](i32[min max], i32[min max])> op = Add;
                return op(40, 2);
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);

        Assert.Contains("call fastcc i32 @Add(", llvm);
    }

    [Fact]
    public void DynTraitObjectDispatchLoadsVtableSlotAndCallsIndirectly()
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
            options: new CompilerOptions());

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);
        var announceBody = ExtractDefinitionBody(llvm, "Announce");

        // The vtable global, the slot load (SsaDynVTableSlotRValue -> getelementptr+load),
        // and the indirect call carrying the trait method's law/finite attributes. The
        // dyn object crosses an export boundary so the closed-world devirtualizer
        // cannot collapse the dispatch to a direct call.
        Assert.Contains("@__stark_vtable_Dog__Speaker", llvm, StringComparison.Ordinal);
        Assert.Contains("getelementptr ptr,", announceBody, StringComparison.Ordinal);
        Assert.Contains("load ptr,", announceBody, StringComparison.Ordinal);
        Assert.Contains("call fastcc i32 %", announceBody, StringComparison.Ordinal);
        Assert.Contains("nosync nofree", announceBody, StringComparison.Ordinal);
    }

    [Fact]
    public void NonCapturingClosureValuesLowerToInvokeAndEnvironmentPair()
    {
        var result = Compile(
            """
            module Demo

            export fn i32[min max] Apply(closure<finite law i32[min max](i32[min max])> op)
            {
                stack i32[min max] r = op(41);
                stack i32[min max] a = r + (r / 3);
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

            fn i32[min max] Run()
            {
                stack closure<finite law i32[min max](i32[min max])> op =
                    (i32[min max] value) => value + 1;

                return Apply(op);
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);
        var applyBody = ExtractDefinitionBody(llvm, "Apply");

        // The closure crosses an export boundary so the devirtualizer cannot
        // collapse the invoke-pointer call to a direct lambda call.
        Assert.Contains("{ ptr @Run___lambda_", llvm, StringComparison.Ordinal);
        Assert.Contains("ptr null", llvm, StringComparison.Ordinal);
        Assert.Contains("extractvalue { ptr, ptr }", applyBody, StringComparison.Ordinal);
        Assert.Contains("call fastcc i32 %", applyBody, StringComparison.Ordinal);
        Assert.Contains("willreturn", applyBody, StringComparison.Ordinal);
        Assert.Contains("mustprogress", applyBody, StringComparison.Ordinal);
    }

    [Fact]
    public void OptimizedFunctionItemClosurePromotionDevirtualizesAndPrunesAdapter()
    {
        var result = Compile(
            """
            module Demo

            fn i32[min max] AddOne(i32[min max] value)
            {
                return value + 1;
            }

            fn i32[min max] Apply(borrow closure<fn i32[min max](i32[min max])> op, i32[min max] value)
            {
                return op(value);
            }

            fn i32[min max] Run()
            {
                return Apply(AddOne, 41);
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);
        var runBody = ExtractDefinitionBody(llvm, "Run");

        Assert.Contains("ret i32 42", runBody, StringComparison.Ordinal);
        Assert.DoesNotContain("closure_adapter", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("alloca { ptr, ptr }", runBody, StringComparison.Ordinal);
        Assert.DoesNotContain("extractvalue { ptr, ptr }", runBody, StringComparison.Ordinal);
        Assert.DoesNotContain("call fastcc", runBody, StringComparison.Ordinal);
    }

    [Fact]
    public void CapturingClosureCopyLowersThroughEnvironmentStorage()
    {
        var result = Compile(
            """
            module Demo

            export fn i32[min max] Apply(closure<finite law i32[min max](i32[min max])> op)
            {
                stack i32[min max] r = op(35);
                stack i32[min max] a = r + (r / 3);
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

            fn i32[min max] Run()
            {
                stack i32[min max] offset = 7;
                stack closure<finite law i32[min max](i32[min max])> op =
                    capture(copy offset) (i32[min max] value) => value + offset;

                return Apply(op);
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);
        var runBody = ExtractDefinitionBody(llvm, "Run");
        var applyBody = ExtractDefinitionBody(llvm, "Apply");
        var lambdaBody = Regex.Match(
            llvm,
            @"define [^{]+@Run___lambda_\d+_\d+\([^)]*\)[\s\S]*?^}",
            RegexOptions.Multiline).Value;

        Assert.Contains("alloca %Run___lambda_", runBody, StringComparison.Ordinal);
        Assert.Contains("insertvalue { ptr, ptr }", runBody, StringComparison.Ordinal);
        Assert.DoesNotContain("ptr null", runBody, StringComparison.Ordinal);
        Assert.Contains("getelementptr", lambdaBody, StringComparison.Ordinal);
        Assert.Contains("load i32", lambdaBody, StringComparison.Ordinal);
        Assert.Contains("call fastcc i32 %", applyBody, StringComparison.Ordinal);

        var lambdaHeader = Regex.Match(lambdaBody, @"define[^\n]+").Value;
        Assert.Contains("nonnull", lambdaHeader, StringComparison.Ordinal);
        Assert.Contains("dereferenceable(4)", lambdaHeader, StringComparison.Ordinal);
        Assert.Contains("align 4", lambdaHeader, StringComparison.Ordinal);
        Assert.Contains("noalias", lambdaHeader, StringComparison.Ordinal);
        Assert.Contains("readonly", lambdaHeader, StringComparison.Ordinal);
        Assert.Contains("captures(none)", lambdaHeader, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadCapturingClosureIndirectCallEmitsReadProvenanceCapture()
    {
        var result = Compile(
            """
            module Demo

            struct Box
            {
                i32[min max] Value;
            }

            export finite law i32[min max] Apply(closure<finite law i32[min max]()> op)
            {
                stack i32[min max] r = op();
                stack i32[min max] a = r + (r / 3);
                stack i32[min max] b = a - (a / 5);
                stack i32[min max] c = b + (b / 7);
                stack i32[min max] d = c - (c / 11);
                return d;
            }

            finite law i32[min max] Run()
            {
                stack Box box = new Box()
                {
                    Value = 42
                };
                stack closure<finite law i32[min max]()> op =
                    capture(read box) () => box.Value;

                return Apply(op);
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);
        var applyBody = ExtractDefinitionBody(llvm, "Apply");
        var indirectCall = Regex.Match(
            applyBody,
            @"call fastcc i32 %[^(]+\([^\n]*\)[^\n]*",
            RegexOptions.CultureInvariant);

        Assert.True(indirectCall.Success, applyBody);
        Assert.Contains("ptr readonly captures(address, read_provenance)", indirectCall.Value, StringComparison.Ordinal);
        Assert.DoesNotContain("captures(none)", indirectCall.Value, StringComparison.Ordinal);
        Assert.Contains("memory(argmem: read)", indirectCall.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void MutCapturingClosureWritesThroughCapturedAddress()
    {
        var result = Compile(
            """
            module Demo

            fn i32[min max] Run()
            {
                stack mut i32[min max] total = 1;
                stack mut closure<mut fn void(i32[min max])> sink =
                    capture(mut total) (i32[min max] value) =>
                    {
                        total = total + value;
                        return;
                    };

                sink(5);
                return total;
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);
        var lambdaBody = Regex.Match(
            llvm,
            @"define [^{]+@Run___lambda_\d+_\d+\([^)]*\)[\s\S]*?^}",
            RegexOptions.Multiline).Value;

        Assert.Contains("getelementptr", lambdaBody, StringComparison.Ordinal);
        Assert.Contains("load ptr", lambdaBody, StringComparison.Ordinal);
        Assert.Contains("store i32", lambdaBody, StringComparison.Ordinal);
    }

    [Fact]
    public void HeapCapturingClosureAllocatesHeapEnvironmentAndClearsNoFreeAttributes()
    {
        var result = Compile(
            """
            module Demo

            fn heap closure<fn i32[min max](i32[min max])> MakeAdder(i32[min max] offset)
            {
                return heap capture(copy offset) (i32[min max] value) => value + offset;
            }

            fn i32[min max] Run()
            {
                stack heap closure<fn i32[min max](i32[min max])> add = MakeAdder(7);
                return add(35);
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);
        var makeAdderHeader = ExtractDefinitionHeader(llvm, "MakeAdder");
        var makeAdderBody = ExtractDefinitionBody(llvm, "MakeAdder");
        var runHeader = ExtractDefinitionHeader(llvm, "Run");
        var runBody = ExtractDefinitionBody(llvm, "Run");

        Assert.Contains("@__stark_heap_alloc", makeAdderBody, StringComparison.Ordinal);
        Assert.Contains("insertvalue { ptr, ptr, ptr }", makeAdderBody, StringComparison.Ordinal);
        Assert.Contains("___drop", makeAdderBody, StringComparison.Ordinal);
        Assert.Contains("___drop(ptr", llvm, StringComparison.Ordinal);
        Assert.Contains("call fastcc void %", runBody, StringComparison.Ordinal);
        Assert.Contains("@__stark_heap_free", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("llvm.lifetime.end", makeAdderBody, StringComparison.Ordinal);
        Assert.DoesNotContain("nofree", makeAdderHeader, StringComparison.Ordinal);
        Assert.DoesNotContain("memory(none)", makeAdderHeader, StringComparison.Ordinal);
        Assert.DoesNotContain("nofree", runHeader, StringComparison.Ordinal);
        Assert.DoesNotContain("memory(none)", runHeader, StringComparison.Ordinal);
    }

    [Fact]
    public void HeapClosureMoveCaptureDropReleasesOwnedFieldsBeforeEnvironment()
    {
        var result = Compile(
            """
            module Demo

            fn heap closure<fn u64[0 max]()> Make()
            {
                stack mut dynamic u32[0 2 ** 31 - 1] values = new(4);
                return heap capture(move values) () => values.Length;
            }

            fn u64[0 max] Run()
            {
                stack heap closure<fn u64[0 max]()> op = Make();
                return op();
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);
        var dropBody = Regex.Match(
            llvm,
            @"define [^{]+@Make___lambda_\d+_\d+___drop\([^)]*\)[\s\S]*?^}",
            RegexOptions.Multiline).Value;

        Assert.Contains("@__stark_runtime_free", dropBody, StringComparison.Ordinal);
        Assert.Contains("@__stark_heap_free", dropBody, StringComparison.Ordinal);
        Assert.True(
            dropBody.IndexOf("@__stark_runtime_free", StringComparison.Ordinal)
            < dropBody.IndexOf("@__stark_heap_free", StringComparison.Ordinal));
    }

    [Fact]
    public void OnceHeapClosureMoveCaptureTransfersOwnedFieldAndFreesEnvironment()
    {
        var result = Compile(
            """
            module Demo

            fn dynamic u32[0 2 ** 31 - 1] RunOnce(heap closure<once fn dynamic u32[0 2 ** 31 - 1]()> producer)
            {
                return producer();
            }

            fn dynamic u32[0 2 ** 31 - 1] Build()
            {
                stack mut dynamic u32[0 2 ** 31 - 1] values = new(4);
                init values[0] = 42;
                stack heap closure<once fn dynamic u32[0 2 ** 31 - 1]()> producer =
                    heap capture(move values) () =>
                    {
                        return values;
                    };
                return RunOnce(producer);
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);
        var runOnceBody = ExtractDefinitionBody(llvm, "RunOnce");
        var lambdaBody = Regex.Match(
            llvm,
            @"define[^\n]+@Build___lambda_\d+_\d+\([^)]*\)[\s\S]*?^}",
            RegexOptions.Multiline).Value;
        var dropBody = Regex.Match(
            llvm,
            @"define [^{]+@Build___lambda_\d+_\d+___drop\([^)]*\)[\s\S]*?^}",
            RegexOptions.Multiline).Value;

        Assert.Contains("@__stark_heap_free", lambdaBody, StringComparison.Ordinal);
        Assert.DoesNotContain("@__stark_runtime_free", lambdaBody, StringComparison.Ordinal);
        Assert.DoesNotContain("call fastcc void %", runOnceBody, StringComparison.Ordinal);
        Assert.Contains("@__stark_runtime_free", dropBody, StringComparison.Ordinal);
        Assert.Contains("@__stark_heap_free", dropBody, StringComparison.Ordinal);
    }

    [Fact]
    public void OnceHeapClosureDropsUnmovedOwnedCapturesBeforeFreeingEnvironment()
    {
        var result = Compile(
            """
            module Demo

            fn u64[0 max] RunOnce(heap closure<once fn u64[0 max]()> producer)
            {
                return producer();
            }

            fn u64[0 max] Build()
            {
                stack mut dynamic u32[0 2 ** 31 - 1] values = new(4);
                init values[0] = 42;
                stack heap closure<once fn u64[0 max]()> producer =
                    heap capture(move values) () =>
                    {
                        return values.Length;
                    };
                return RunOnce(producer);
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);
        var runOnceBody = ExtractDefinitionBody(llvm, "RunOnce");
        var lambdaBody = Regex.Match(
            llvm,
            @"define[^\n]+@Build___lambda_\d+_\d+\([^)]*\)[\s\S]*?^}",
            RegexOptions.Multiline).Value;

        Assert.Contains("@__stark_runtime_free", lambdaBody, StringComparison.Ordinal);
        Assert.Contains("@__stark_heap_free", lambdaBody, StringComparison.Ordinal);
        Assert.True(
            lambdaBody.IndexOf("@__stark_runtime_free", StringComparison.Ordinal)
            < lambdaBody.IndexOf("@__stark_heap_free", StringComparison.Ordinal));
        Assert.DoesNotContain("call fastcc void %", runOnceBody, StringComparison.Ordinal);
    }

    [Fact]
    public void OptimizedInlineClosureCallSpecializesAwayRuntimeClosure()
    {
        var result = Compile(
            """
            module Demo

            inline fn i32[min max] Apply(
                inline closure<fn i32[min max](i32[min max])> op,
                i32[min max] x)
                {
                    return op(x);
            }

            fn i32[min max] Run(i32[min max] offset)
            {
                return Apply(capture(copy offset) (i32[min max] value) => value + offset, 4);
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);
        var runBody = ExtractDefinitionBody(llvm, "Run");

        Assert.Contains("add nsw i32 4, %arg_offset", runBody, StringComparison.Ordinal);
        Assert.DoesNotContain("alloca %Run___lambda_", runBody, StringComparison.Ordinal);
        Assert.DoesNotContain("insertvalue { ptr, ptr }", runBody, StringComparison.Ordinal);
        Assert.DoesNotContain("extractvalue { ptr, ptr }", runBody, StringComparison.Ordinal);
        Assert.DoesNotContain("call fastcc", runBody, StringComparison.Ordinal);
        Assert.DoesNotContain("@Run___lambda_", llvm, StringComparison.Ordinal);
    }

    [Fact]
    public void OptimizedInlineClosureWithNamedBorrowParameterSpecializesAwayRuntimeClosure()
    {
        var result = Compile(
            """
            module Demo

            struct Ui
            {
                i32[min max] Value;
            }

            inline fn void Row(
                mut borrow Ui ui,
                inline closure<fn void(mut borrow Ui)> body)
                {
                    body(ui);
                return;
            }

            fn i32[min max] Run(mut borrow Ui ui, i32[min max] offset)
            {
                Row(
                    ui,
                    capture(copy offset) (mut borrow Ui row) =>
                    {
                        row.Value = offset + 1;
                        return;
                    });
                return ui.Value;
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);
        var runBody = ExtractDefinitionBody(llvm, "Run");

        Assert.Contains("add nsw i32 %arg_offset, 1", runBody, StringComparison.Ordinal);
        Assert.DoesNotContain("alloca %Run___lambda_", runBody, StringComparison.Ordinal);
        Assert.DoesNotContain("insertvalue { ptr, ptr }", runBody, StringComparison.Ordinal);
        Assert.DoesNotContain("extractvalue { ptr, ptr }", runBody, StringComparison.Ordinal);
        Assert.DoesNotContain("call fastcc void %", runBody, StringComparison.Ordinal);
        Assert.DoesNotContain("@Row(", runBody, StringComparison.Ordinal);
        Assert.DoesNotContain("@Run___lambda_", llvm, StringComparison.Ordinal);
    }

    [Fact]
    public void OptimizedInlineClosureThroughNestedWrapperSpecializesAwayRuntimeClosure()
    {
        var result = Compile(
            """
            module Demo

            inline fn i32[min max] Inner(
                inline closure<fn i32[min max](i32[min max])> op,
                i32[min max] value)
                {
                    return op(value);
            }

            inline fn i32[min max] Outer(
                inline closure<fn i32[min max](i32[min max])> op,
                i32[min max] value)
                {
                    return Inner(op, value + 1);
            }

            fn i32[min max] Run(i32[min max] offset)
            {
                return Outer(capture(copy offset) (i32[min max] value) => value + offset, 4);
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);
        var runBody = ExtractDefinitionBody(llvm, "Run");

        Assert.Matches(@"add nsw i32 (?:%arg_offset, 5|5, %arg_offset)", runBody);
        Assert.DoesNotContain("insertvalue { ptr, ptr }", runBody, StringComparison.Ordinal);
        Assert.DoesNotContain("extractvalue { ptr, ptr }", runBody, StringComparison.Ordinal);
        Assert.DoesNotContain("call fastcc", runBody, StringComparison.Ordinal);
        Assert.DoesNotContain("@Run___lambda_", llvm, StringComparison.Ordinal);
    }

    [Fact]
    public void OptimizedInlineClosureInsideControlFlowSpecializesAwayRuntimeClosure()
    {
        var result = Compile(
            """
            module Demo

            inline fn i32[min max] ApplyIf(
                bool enabled,
                inline closure<fn i32[min max](i32[min max])> op,
                i32[min max] value)
                {
                    if (enabled)
                {
                    return op(value);
                }

                return value;
            }

            fn i32[min max] Run(bool enabled, i32[min max] offset)
            {
                return ApplyIf(enabled, capture(copy offset) (i32[min max] value) => value + offset, 4);
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);
        var runBody = ExtractDefinitionBody(llvm, "Run");

        Assert.Contains("add nsw i32 4, %arg_offset", runBody, StringComparison.Ordinal);
        Assert.DoesNotContain("insertvalue { ptr, ptr }", runBody, StringComparison.Ordinal);
        Assert.DoesNotContain("extractvalue { ptr, ptr }", runBody, StringComparison.Ordinal);
        Assert.DoesNotContain("call fastcc", runBody, StringComparison.Ordinal);
        Assert.DoesNotContain("@Run___lambda_", llvm, StringComparison.Ordinal);
    }

    [Fact]
    public void OptimizedMutableInlineClosureSpecializesAwayRuntimeClosure()
    {
        var result = Compile(
            """
            module Demo

            inline fn void Twice(inline closure<mut fn void()> op)
            {
                op();
                op();
                return;
            }

            fn i32[min max] Run(i32[min max] seed)
            {
                stack mut i32[min max] value = seed;
                Twice(capture(mut value) () =>
                {
                    value = value + 1;
                    return;
                });

                return value;
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);
        var runBody = ExtractDefinitionBody(llvm, "Run");

        Assert.Equal(2, Regex.Matches(runBody, @"add nsw i32").Count);
        Assert.DoesNotContain("insertvalue { ptr, ptr }", runBody, StringComparison.Ordinal);
        Assert.DoesNotContain("extractvalue { ptr, ptr }", runBody, StringComparison.Ordinal);
        Assert.DoesNotContain("call fastcc", runBody, StringComparison.Ordinal);
        Assert.DoesNotContain("@Run___lambda_", llvm, StringComparison.Ordinal);
    }

    [Fact]
    public void OptimizedOnceInlineClosureSpecializesAwayRuntimeClosure()
    {
        var result = Compile(
            """
            module Demo

            inline fn i32[min max] RunOnce(inline closure<once fn i32[min max]()> producer)
            {
                return producer();
            }

            fn i32[min max] Run(i32[min max] seed)
            {
                return RunOnce(capture(copy seed) () => seed + 1);
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);
        var runBody = ExtractDefinitionBody(llvm, "Run");

        Assert.Contains("add nsw i32 %arg_seed, 1", runBody, StringComparison.Ordinal);
        Assert.DoesNotContain("insertvalue { ptr, ptr }", runBody, StringComparison.Ordinal);
        Assert.DoesNotContain("extractvalue { ptr, ptr }", runBody, StringComparison.Ordinal);
        Assert.DoesNotContain("call fastcc", runBody, StringComparison.Ordinal);
        Assert.DoesNotContain("@Run___lambda_", llvm, StringComparison.Ordinal);
    }

    [Fact]
    public void FiniteLawFunctionPointerCallsEmitIndirectCallEffectAttributes()
    {
        var result = Compile(
            """
            module Demo

            finite law i32[min max] Apply(fnptr<finite law i32[min max](i32[min max])> f, i32[min max] x)
            {
                return f(x);
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);
        var applyBody = ExtractDefinitionBody(llvm, "Apply");
        var indirectCall = Regex.Match(applyBody, @"call fastcc i32 %arg_f\([^\n]*\)[^\n]*").Value;

        Assert.NotEmpty(indirectCall);
        Assert.Contains("nounwind", indirectCall, StringComparison.Ordinal);
        Assert.Contains("willreturn", indirectCall, StringComparison.Ordinal);
        Assert.Contains("mustprogress", indirectCall, StringComparison.Ordinal);
        Assert.Contains("nosync", indirectCall, StringComparison.Ordinal);
        Assert.Contains("nofree", indirectCall, StringComparison.Ordinal);
        Assert.Contains("memory(none)", indirectCall, StringComparison.Ordinal);
    }

    [Fact]
    public void FunctionPointerCallSiteEffectAttributesFollowPointerKind()
    {
        var result = Compile(
            """
            module Demo

            fn i32[min max] ApplyPlain(fnptr<fn i32[min max](i32[min max])> f, i32[min max] x)
            {
                return f(x);
            }

            finite i32[min max] ApplyFinite(fnptr<finite i32[min max](i32[min max])> f, i32[min max] x)
            {
                return f(x);
            }

            law i32[min max] ApplyLaw(fnptr<law i32[min max](i32[min max])> f, i32[min max] x)
            {
                return f(x);
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);
        var plainCall = ExtractFirstIndirectCall(llvm, "ApplyPlain", "f");
        var finiteCall = ExtractFirstIndirectCall(llvm, "ApplyFinite", "f");
        var lawCall = ExtractFirstIndirectCall(llvm, "ApplyLaw", "f");

        Assert.DoesNotContain("willreturn", plainCall, StringComparison.Ordinal);
        Assert.DoesNotContain("mustprogress", plainCall, StringComparison.Ordinal);
        Assert.DoesNotContain("nosync", plainCall, StringComparison.Ordinal);
        Assert.DoesNotContain("nofree", plainCall, StringComparison.Ordinal);
        Assert.DoesNotContain("memory(", plainCall, StringComparison.Ordinal);

        Assert.Contains("willreturn", finiteCall, StringComparison.Ordinal);
        Assert.Contains("mustprogress", finiteCall, StringComparison.Ordinal);
        Assert.DoesNotContain("nosync", finiteCall, StringComparison.Ordinal);
        Assert.DoesNotContain("nofree", finiteCall, StringComparison.Ordinal);
        Assert.DoesNotContain("memory(", finiteCall, StringComparison.Ordinal);

        Assert.DoesNotContain("willreturn", lawCall, StringComparison.Ordinal);
        Assert.DoesNotContain("mustprogress", lawCall, StringComparison.Ordinal);
        Assert.Contains("nosync", lawCall, StringComparison.Ordinal);
        Assert.Contains("nofree", lawCall, StringComparison.Ordinal);
        Assert.Contains("memory(none)", lawCall, StringComparison.Ordinal);
    }

    [Fact]
    public void UnusedPlainFunctionPointerCallsAreNotRemovedBySsaCleanup()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn void Apply(fnptr<fn void(i32[min max])> op, i32[min max] value)
            {
                op(value);
                return;
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var applyBody = ExtractDefinitionBody(GetLlvmRaw(result), "Apply");

        Assert.Contains("call fastcc void %arg_op(i32 %arg_value)", applyBody, StringComparison.Ordinal);
    }

    [Fact]
    public void FunctionPointerCallsWithFiniteKnownTargetSetsEmitCalleesMetadata()
    {
        var result = Compile(
            """
            module Demo

            noinline finite law i32[min max] Left(i32[min max] value)
            {
                return value;
            }

            noinline finite law i32[min max] Right(i32[min max] value)
            {
                return value;
            }

            finite law i32[min max] Apply(bool chooseRight, i32[min max] value)
            {
                stack mut fnptr<finite law i32[min max](i32[min max])> op = Left;
                if (chooseRight)
                {
                    op = Right;
                }

                return op(value);
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);
        var applyBody = ExtractDefinitionBody(llvm, "Apply");
        var indirectCall = Regex.Match(applyBody, @"call fastcc i32 %[^(]+\([^\n]*\)[^\n]*!callees !(\d+)[^\n]*");

        Assert.True(indirectCall.Success, applyBody);
        Assert.Contains("willreturn", indirectCall.Value, StringComparison.Ordinal);
        Assert.Contains("mustprogress", indirectCall.Value, StringComparison.Ordinal);
        Assert.Contains("memory(none)", indirectCall.Value, StringComparison.Ordinal);
        Assert.Matches(
            $@"!{indirectCall.Groups[1].Value} = !\{{ptr @Left, ptr @Right\}}",
            llvm);
    }

    [Fact]
    public void ClosureFunctionItemLocalDevirtualizesAndInlinesAdapterAtO3()
    {
        var result = Compile(
            """
            module Demo

            noinline finite law i32[min max] Left(i32[min max] value)
            {
                return value;
            }

            finite law i32[min max] Apply(i32[min max] value)
            {
                stack closure<finite law i32[min max](i32[min max])> op = Left;
                return op(value);
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var applyBody = ExtractDefinitionBody(GetLlvmRaw(result), "Apply");

        Assert.Contains("@Left(", applyBody, StringComparison.Ordinal);
        Assert.DoesNotContain("closure_adapter", applyBody, StringComparison.Ordinal);
        Assert.DoesNotContain("extractvalue { ptr, ptr }", applyBody, StringComparison.Ordinal);
        Assert.DoesNotContain("call fastcc i32 %", applyBody, StringComparison.Ordinal);
    }

    [Fact]
    public void ClosureCallsWithFiniteKnownTargetSetsEmitCalleesMetadata()
    {
        var result = Compile(
            """
            module Demo

            noinline finite law i32[min max] Left(i32[min max] value)
            {
                return value;
            }

            noinline finite law i32[min max] Right(i32[min max] value)
            {
                return value + 1;
            }

            finite law i32[min max] Apply(bool chooseRight, i32[min max] value)
            {
                stack mut closure<finite law i32[min max](i32[min max])> op = Left;
                if (chooseRight)
                {
                    op = Right;
                }

                return op(value);
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);
        var applyBody = ExtractDefinitionBody(llvm, "Apply");
        var indirectCall = Regex.Match(applyBody, @"call fastcc i32 %[^(]+\([^\n]*\)[^\n]*!callees !(\d+)[^\n]*");

        Assert.True(indirectCall.Success, applyBody);
        Assert.Contains("willreturn", indirectCall.Value, StringComparison.Ordinal);
        Assert.Contains("mustprogress", indirectCall.Value, StringComparison.Ordinal);
        Assert.Contains("memory(argmem: read)", indirectCall.Value, StringComparison.Ordinal);

        var metadataLine = Regex.Match(
            llvm,
            $@"!{indirectCall.Groups[1].Value} = !\{{[^\n]*\}}").Value;
        Assert.Equal(2, Regex.Matches(metadataLine, @"ptr @Apply___closure_adapter_\d+_\d+").Count);
    }

    [Fact]
    public void FunctionPointerCallsWithOpaqueTargetsDoNotEmitCalleesMetadata()
    {
        var result = Compile(
            """
            module Demo

            finite law i32[min max] Apply(fnptr<finite law i32[min max](i32[min max])> op, i32[min max] value)
            {
                return op(value);
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);
        var header = ExtractDefinitionHeader(llvm, "Apply");
        var indirectCall = ExtractFirstIndirectCall(llvm, "Apply", "op");

        Assert.Contains("ptr noundef nonnull %arg_op", header, StringComparison.Ordinal);
        Assert.DoesNotContain("!callees", indirectCall, StringComparison.Ordinal);
    }

    [Fact]
    public void FunctionPointerCallsWithSingletonKnownTargetExpressionsBecomeDirectCalls()
    {
        var result = Compile(
            """
            module Demo

            noinline finite law i32[min max] Left(i32[min max] value)
            {
                return value;
            }

            finite law i32[min max] Apply(bool choose, i32[min max] value)
            {
                stack fnptr<finite law i32[min max](i32[min max])> op = choose ? Left : Left;
                return op(value);
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var applyBody = ExtractDefinitionBody(GetLlvmRaw(result), "Apply");

        Assert.Contains("call fastcc i32 @Left(", applyBody, StringComparison.Ordinal);
        Assert.DoesNotContain("call fastcc i32 %", applyBody, StringComparison.Ordinal);
        Assert.DoesNotContain("!callees", applyBody, StringComparison.Ordinal);
    }

    [Fact]
    public void LawFunctionPointerCallsWithBorrowParametersEmitReadonlyMemoryEffects()
    {
        var result = Compile(
            """
            module Demo

            struct Box
            {
                i32[min max] Value;
            }

            law i32[min max] Apply(fnptr<law i32[min max](borrow Box)> f, borrow Box box)
            {
                return f(box);
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);
        var indirectCall = ExtractFirstIndirectCall(llvm, "Apply", "f");

        Assert.Contains("ptr nonnull noalias readonly", indirectCall, StringComparison.Ordinal);
        Assert.Contains("captures(address, read_provenance)", indirectCall, StringComparison.Ordinal);
        Assert.Contains("nosync", indirectCall, StringComparison.Ordinal);
        Assert.Contains("nofree", indirectCall, StringComparison.Ordinal);
        Assert.Contains("memory(argmem: read)", indirectCall, StringComparison.Ordinal);
    }

    [Fact]
    public void FunctionPointerCallSiteEffectAttributesComposeWithOutAndAggregateAbi()
    {
        var outResult = Compile(
            """
            module Demo

            finite bool Apply(fnptr<finite bool(out u32[0 max])> f, out u32[0 max] value)
            {
                return f(value);
            }
            """,
            options: new CompilerOptions());

        Assert.True(outResult.Succeeded, string.Join(", ", outResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var outCall = ExtractFirstIndirectCall(GetLlvmRaw(outResult), "Apply", "f");

        Assert.Contains("writeonly", outCall, StringComparison.Ordinal);
        Assert.Contains("willreturn", outCall, StringComparison.Ordinal);
        Assert.Contains("mustprogress", outCall, StringComparison.Ordinal);

        var initResult = Compile(
            """
            module Demo

            finite void Apply(fnptr<finite void(init u32[0 max][])> f, init u32[0 max][] values)
            {
                f(values);
                return;
            }
            """,
            options: new CompilerOptions());

        Assert.True(initResult.Succeeded, string.Join(", ", initResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var initCall = ExtractFirstIndirectCall(GetLlvmRaw(initResult), "Apply", "f");

        Assert.Contains("noalias", initCall, StringComparison.Ordinal);
        Assert.Contains("willreturn", initCall, StringComparison.Ordinal);
        Assert.Contains("mustprogress", initCall, StringComparison.Ordinal);

        var aggregateResult = Compile(
            """
            module Demo

            struct Big
            {
                i64[min max] A;
                i64[min max] B;
                i64[min max] C;
            }

            finite law Big Apply(fnptr<finite law Big(Big)> f, Big value)
            {
                return f(value);
            }
            """,
            options: new CompilerOptions());

        Assert.True(aggregateResult.Succeeded, string.Join(", ", aggregateResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var aggregateCall = ExtractFirstIndirectCall(GetLlvmRaw(aggregateResult), "Apply", "f");

        Assert.Contains("sret(%Big)", aggregateCall, StringComparison.Ordinal);
        Assert.Contains("byval(%Big)", aggregateCall, StringComparison.Ordinal);
        Assert.Contains("willreturn", aggregateCall, StringComparison.Ordinal);
        Assert.Contains("mustprogress", aggregateCall, StringComparison.Ordinal);
        Assert.Contains("nosync", aggregateCall, StringComparison.Ordinal);
        Assert.Contains("nofree", aggregateCall, StringComparison.Ordinal);
        Assert.Contains("memory(argmem: readwrite)", aggregateCall, StringComparison.Ordinal);
    }

    [Fact]
    public void FunctionPointerCallsWithBorrowParametersUsePointerAbi()
    {
        var result = Compile(
            """
            module Demo

            struct Box
            {
                i32[min max] Value;
            }

            unsafe fn i32[min max] Read(borrow Box box)
            {
                return box.Value;
            }

            unsafe fn i32[min max] Apply(fnptr<unsafe fn i32[min max](borrow Box)> op, borrow Box box)
            {
                return op(box);
            }

            unsafe fn i32[min max] Run()
            {
                stack Box box = new Box()
                {
                    Value = 42
                };
                return Apply(Read, box);
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);
        var header = ExtractDefinitionHeader(llvm, "Apply");
        var applyBody = Regex.Match(llvm, @"define fastcc[^{]+ @Apply\([^\n]*\)[\s\S]*?^}", RegexOptions.Multiline).Value;

        Assert.DoesNotContain("; LLVM body emission fallback for Apply", llvm);
        Assert.Contains("ptr noundef nonnull %arg_op", header, StringComparison.Ordinal);
        Assert.Matches(
            @"call fastcc i32 %arg_op\(ptr nonnull noalias readonly captures\(address, read_provenance\) dereferenceable\(4\) align 4 %arg_box\)",
            applyBody);
    }

    [Fact]
    public void FunctionPointerReturnsEmitNonnullAbiAttribute()
    {
        var result = Compile(
            """
            module Demo

            fn i32[min max] Target()
            {
                return 7;
            }

            noinline fn fnptr<fn i32[min max]()> Factory()
            {
                stack fnptr<fn i32[min max]()> callback = Target;
                return callback;
            }

            fn i32[min max] Use(fnptr<fn fnptr<fn i32[min max]()>()> producer)
            {
                stack fnptr<fn i32[min max]()> callback = producer();
                return callback();
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);
        var factoryHeader = ExtractDefinitionHeader(llvm, "Factory");
        var useBody = ExtractDefinitionBody(llvm, "Use");

        Assert.Contains("define fastcc noundef nonnull ptr @Factory()", factoryHeader, StringComparison.Ordinal);
        Assert.Matches(@"call fastcc noundef nonnull ptr %arg_producer\(\)", useBody);
    }

    [Fact]
    public void FunctionPointerCallsWithOutParametersUseCallerStorage()
    {
        var result = Compile(
            """
            module Demo

            fn bool Write(out u32[0 max] value)
            {
                value = 7;
                return true;
            }

            unsafe fn bool Apply(fnptr<fn bool(out u32[0 max])> op, out u32[0 max] value)
            {
                return op(value);
            }

            unsafe fn u32[0 max] Run()
            {
                stack mut u32[0 max] value = 1;
                if (!Apply(Write, value))
                {
                    return 0;
                }

                return value;
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);
        var header = ExtractDefinitionHeader(llvm, "Apply");
        var applyBody = Regex.Match(llvm, @"define fastcc[^{]+ @Apply\([^\n]*\)[\s\S]*?^}", RegexOptions.Multiline).Value;

        Assert.DoesNotContain("; LLVM body emission fallback for Apply", llvm);
        Assert.Contains("ptr noundef nonnull %arg_op", header, StringComparison.Ordinal);
        Assert.Matches(
            @"call fastcc i1 %arg_op\(ptr nonnull noalias writeonly dereferenceable\(4\) writable initializes\(\(0, 4\)\) captures\(address, provenance\) align 4 %arg_value\)",
            applyBody);
    }

    [Fact]
    public void OutDestinationParametersEmitInitializesAndWritableButNotDeadOnReturn()
    {
        var result = Compile(
            """
            module Demo

            fn bool Write(out u32[0 max] value)
            {
                value = 7;
                return true;
            }

            unsafe fn bool Apply(fnptr<fn bool(out u32[0 max])> op, out u32[0 max] value)
            {
                return op(value);
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);
        var writeHeader = ExtractDefinitionHeader(llvm, "Write");
        var applyBody = ExtractDefinitionBody(llvm, "Apply");

        Assert.Contains("writeonly", writeHeader, StringComparison.Ordinal);
        Assert.Contains("writable", writeHeader, StringComparison.Ordinal);
        Assert.Contains("initializes((0, 4))", writeHeader, StringComparison.Ordinal);
        Assert.DoesNotContain("dead_on_return", writeHeader, StringComparison.Ordinal);
        Assert.Contains("writeonly dereferenceable(4) writable initializes((0, 4))", applyBody, StringComparison.Ordinal);
        Assert.DoesNotContain("dead_on_return", applyBody, StringComparison.Ordinal);
    }

    [Fact]
    public void ExplicitDeadOnReturnDestinationParametersEmitLlvmAttribute()
    {
        var result = Compile(
            """
            module Demo

            fn bool WriteAndConsume(out u32[0 max] value) where dead_on_return(value)
            {
                value = 7;
                return true;
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);
        var header = ExtractDefinitionHeader(llvm, "WriteAndConsume");

        Assert.Contains("writeonly", header, StringComparison.Ordinal);
        Assert.Contains("writable", header, StringComparison.Ordinal);
        Assert.Contains("initializes((0, 4))", header, StringComparison.Ordinal);
        Assert.Contains("dead_on_return", header, StringComparison.Ordinal);
    }

    [Fact]
    public void FunctionPointerDeadOnReturnContractsEmitCallSiteAttribute()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn bool Apply(
                fnptr<fn bool(out u32[0 max]) where dead_on_return(arg0)> op,
                out u32[0 max] value)
                where dead_on_return(value)
            {
                return op(value);
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);
        var applyHeader = ExtractDefinitionHeader(llvm, "Apply");
        var indirectCall = ExtractFirstIndirectCall(llvm, "Apply", "op");

        Assert.Contains("dead_on_return", applyHeader, StringComparison.Ordinal);
        Assert.Contains("dead_on_return", indirectCall, StringComparison.Ordinal);
        Assert.Contains("writable initializes((0, 4))", indirectCall, StringComparison.Ordinal);
    }

    [Fact]
    public void FunctionPointerCallsWithInitParametersUseCallerStorage()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn void Fill(init u32[0 max][] destination)
            {
                init destination[0] = 7;
                init destination[1] = 8;
                return;
            }

            unsafe fn void Apply(fnptr<unsafe fn void(init u32[0 max][])> op, init u32[0 max][] destination)
            {
                op(destination);
                return;
            }

            unsafe fn u64[0 max] Run()
            {
                stack mut dynamic u32[0 max] values = new(4);
                stack init u32[0 max][] spare = init values[values.Length, 2];
                Apply(Fill, spare);
                return values.Length;
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);
        var header = ExtractDefinitionHeader(llvm, "Apply");
        var applyBody = Regex.Match(llvm, @"define fastcc[^{]+ @Apply\([^\n]*\)[\s\S]*?^}", RegexOptions.Multiline).Value;

        Assert.DoesNotContain("; LLVM body emission fallback for Apply", llvm);
        Assert.Contains("ptr noundef nonnull %arg_op", header, StringComparison.Ordinal);
        Assert.Matches(
            @"call fastcc void %arg_op\(ptr nonnull noalias captures\(address, provenance\) dereferenceable\(16\) align 8 %arg_destination\)",
            applyBody);
    }

    [Fact]
    public void FunctionPointerCallsWithRawPointerParametersEmitNoAliasCallAttributes()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn void Touch(rawmutptr<i32[min max]> left, rawmutptr<i32[min max]> right)
            {
                *left = *right;
                return;
            }

            unsafe fn void Apply(
                fnptr<unsafe fn void(rawmutptr<i32[min max]>, rawmutptr<i32[min max]>)> op,
                rawmutptr<i32[min max]> left,
                rawmutptr<i32[min max]> right)
                {
                    op(left, right);
                return;
            }

            unsafe fn void Run()
            {
                stack mut i32[min max] left = 1;
                stack mut i32[min max] right = 2;
                Apply(Touch, &left, &right);
                return;
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);
        var applyBody = Regex.Match(llvm, @"define fastcc[^{]+ @Apply\([^\n]*\)[\s\S]*?^}", RegexOptions.Multiline).Value;

        Assert.DoesNotContain("; LLVM body emission fallback for Apply", llvm);
        Assert.Matches(
            @"call fastcc void %arg_op\(ptr noalias captures\(address, provenance\) %arg_left, ptr noalias captures\(address, provenance\) %arg_right\)",
            applyBody);
    }

    [Fact]
    public void FunctionPointerOverlapContractsSuppressIndirectNoAliasCallAttributes()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn void Touch(rawmutptr<i32[min max]> left, rawmutptr<i32[min max]> right)
                where overlap(left, right)
                {
                    *left = *right;
                return;
            }

            unsafe fn void Apply(
                fnptr<unsafe fn void(rawmutptr<i32[min max]>, rawmutptr<i32[min max]>) where overlap(arg0, arg1)> op,
                rawmutptr<i32[min max]> left,
                rawmutptr<i32[min max]> right)
                where overlap(left, right)
                {
                    op(left, right);
                return;
            }

            unsafe fn void Run(rawmutptr<i32[min max]> ptr)
            {
                Apply(Touch, ptr, ptr);
                return;
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);
        var applyBody = Regex.Match(llvm, @"define fastcc[^{]+ @Apply\([^\n]*\)[\s\S]*?^}", RegexOptions.Multiline).Value;
        var indirectCall = Regex.Match(applyBody, @"call fastcc void %arg_op\([^\n]+\)").Value;

        Assert.DoesNotContain("; LLVM body emission fallback for Apply", llvm);
        Assert.Contains("call fastcc void %arg_op(", indirectCall, StringComparison.Ordinal);
        Assert.DoesNotContain("noalias", indirectCall, StringComparison.Ordinal);
    }

    [Fact]
    public void FunctionPointerSameContractsSuppressIndirectNoAliasBetweenSameArguments()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn void Touch(rawmutptr<i32[min max]> left, rawmutptr<i32[min max]> right)
                where same(left, right)
                {
                    *left = *right;
                return;
            }

            unsafe fn void Apply(
                fnptr<unsafe fn void(rawmutptr<i32[min max]>, rawmutptr<i32[min max]>) where same(arg0, arg1)> op,
                rawmutptr<i32[min max]> left,
                rawmutptr<i32[min max]> right)
                where same(left, right)
                {
                    op(left, right);
                return;
            }

            unsafe fn void Run(rawmutptr<i32[min max]> ptr)
            {
                Apply(Touch, ptr, ptr);
                return;
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);
        var applyBody = Regex.Match(llvm, @"define fastcc[^{]+ @Apply\([^\n]*\)[\s\S]*?^}", RegexOptions.Multiline).Value;
        var indirectCall = Regex.Match(applyBody, @"call fastcc void %arg_op\([^\n]+\)").Value;

        Assert.DoesNotContain("; LLVM body emission fallback for Apply", llvm);
        Assert.Contains("call fastcc void %arg_op(", indirectCall, StringComparison.Ordinal);
        Assert.DoesNotContain("noalias", indirectCall, StringComparison.Ordinal);
    }

    [Fact]
    public void FunctionPointerCallsWithLargeByValueParametersUseByvalAbi()
    {
        var result = Compile(
            """
            module Demo

            struct Big
            {
                i64[min max] A;
                i64[min max] B;
                i64[min max] C;
            }

            unsafe fn i64[min max] Sum(Big value)
            {
                return value.A + value.B + value.C;
            }

            unsafe fn i64[min max] Apply(fnptr<unsafe fn i64[min max](Big)> op, Big value)
            {
                return op(value);
            }

            unsafe fn i64[min max] Run()
            {
                stack Big value = new Big()
                {
                    A = 1, B = 2, C = 3
                };
                return Apply(Sum, value);
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);
        var header = ExtractDefinitionHeader(llvm, "Apply");
        var applyBody = Regex.Match(llvm, @"define fastcc[^{]+ @Apply\([^\n]*\)[\s\S]*?^}", RegexOptions.Multiline).Value;

        Assert.DoesNotContain("; LLVM body emission fallback for Apply", llvm);
        Assert.Contains("ptr noundef nonnull %arg_op", header, StringComparison.Ordinal);
        Assert.Matches(@"call fastcc i64 %arg_op\(ptr nonnull byval\(%Big\) noalias readonly dereferenceable\(24\) align 8 %arg_value\)", applyBody);
    }

    [Fact]
    public void FunctionPointerCallsWithLargeReturnValuesUseSretAbi()
    {
        var result = Compile(
            """
            module Demo

            struct Big
            {
                i64[min max] A;
                i64[min max] B;
                i64[min max] C;
            }

            unsafe fn Big Make(i64[min max] seed)
            {
                return new Big()
                {
                    A = seed, B = seed + 1, C = seed + 2
                };
            }

            unsafe fn Big Apply(fnptr<unsafe fn Big(i64[min max])> op, i64[min max] seed)
            {
                return op(seed);
            }

            unsafe fn i64[min max] Run()
            {
                stack Big value = Apply(Make, 4);
                return value.C;
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);
        var applyBody = Regex.Match(llvm, @"define fastcc[^{]+ @Apply\([^\n]*\)[\s\S]*?^}", RegexOptions.Multiline).Value;

        Assert.DoesNotContain("; LLVM body emission fallback for Apply", llvm);
        Assert.Contains("define fastcc void @Apply(ptr noundef noalias sret(%Big)", llvm);
        Assert.Matches(@"call fastcc void %arg_op\(ptr noalias sret\(%Big\) nonnull dereferenceable\(24\) align 8 %ret", applyBody);
        Assert.DoesNotContain("%abi_indirect_callret_slot_", applyBody);
        Assert.DoesNotContain("call void @llvm.memcpy.inline.p0.p0.i64(ptr align 8 %ret", applyBody);
    }

    [Fact]
    public void DirectVoidStatementCallsWithOutParametersUseCallerStorage()
    {
        var result = Compile(
            """
            module Demo

            fn void Touch(out i32[min max] value, i32[min max] seed)
            {
                value = seed;
                value = value + (value / 3);
                value = value - (value / 5);
                value = value + (value / 7);
                value = value - (value / 11);
                value = value + (value / 13);
                value = value - (value / 17);
                value = value + (value / 19);
                value = value - (value / 23);
                value = value + (value / 29);
                value = value - (value / 31);
                value = value + (value / 37);
                value = value - (value / 41);
                value = value + (value / 43);
                return;
            }

            unsafe fn i32[min max] Run(i32[min max] seed)
            {
                stack mut i32[min max] value = 0;
                Touch(value, seed);
                return value;
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);
        var runBody = ExtractDefinitionBody(llvm, "Run");

        Assert.DoesNotContain("; LLVM body emission fallback for Run", llvm);
        Assert.Contains("define fastcc void @Touch(ptr noundef nonnull", llvm);
        Assert.Matches(@"call fastcc void @Touch\(ptr [^\n]*\)", runBody);
        Assert.DoesNotContain("%abi_callarg_value", runBody);
    }

    [Fact]
    public void DirectCallsWithLargeByValueTemporaryConstructDirectlyIntoByvalCallSlot()
    {
        var result = Compile(
            """
            module Demo

            struct Big
            {
                i64[min max] A;
                i64[min max] B;
                i64[min max] C;
                i64[min max] D;
            }

            unsafe fn i64[min max] Read(Big value)
            {
                stack i64[min max] a = value.A + (value.D / 3);
                stack i64[min max] b = a - (a / 5);
                stack i64[min max] c = b + (b / 7);
                stack i64[min max] d = c - (c / 11);
                stack i64[min max] e = d + (d / 13);
                stack i64[min max] f = e - (e / 17);
                stack i64[min max] g = f + (f / 19);
                stack i64[min max] h = g - (g / 23);
                stack i64[min max] i = h + (h / 29);
                stack i64[min max] j = i - (i / 31);
                stack i64[min max] k = j + (j / 37);
                stack i64[min max] l = k - (k / 41);
                stack i64[min max] m = l + (l / 43);
                return m;
            }

            unsafe fn i64[min max] Run()
            {
                stack Big value = new Big()
                {
                    A = 1, B = 2, C = 3, D = 4
                };
                return Read(value);
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);
        var runHeader = ExtractDefinitionHeader(llvm, "Run");
        var runBody = ExtractDefinitionBody(llvm, "Run");

        Assert.DoesNotContain("; LLVM body emission fallback for Run", llvm);
        Assert.Contains("memory(none)", runHeader);
        Assert.DoesNotContain("memory(readwrite", runHeader);
        Assert.Contains("define fastcc noundef i64 @Read(ptr noundef nonnull byval(%Big)", llvm);
        Assert.Matches(@"call fastcc i64 @Read\(ptr nonnull byval\(%Big\) noalias readonly captures\(none\) dereferenceable\(32\) align 8 %abi_callarg_value_\d+\)", runBody);
        Assert.DoesNotContain("%slot_value", runBody);
        Assert.DoesNotContain("load %Big", runBody);
        Assert.DoesNotContain("store %Big", runBody);
    }

    [Fact]
    public void DirectCallsWithLargeByValueCurrentParameterUseByvalParameterPointer()
    {
        var result = Compile(
            """
            module Demo

            struct Big
            {
                i64[min max] A;
                i64[min max] B;
                i64[min max] C;
                i64[min max] D;
            }

            unsafe fn i64[min max] Read(Big value)
            {
                stack i64[min max] a = value.A + (value.D / 3);
                stack i64[min max] b = a - (a / 5);
                stack i64[min max] c = b + (b / 7);
                stack i64[min max] d = c - (c / 11);
                stack i64[min max] e = d + (d / 13);
                stack i64[min max] f = e - (e / 17);
                stack i64[min max] g = f + (f / 19);
                stack i64[min max] h = g - (g / 23);
                stack i64[min max] i = h + (h / 29);
                stack i64[min max] j = i - (i / 31);
                stack i64[min max] k = j + (j / 37);
                stack i64[min max] l = k - (k / 41);
                stack i64[min max] m = l + (l / 43);
                return m;
            }

            unsafe fn i64[min max] Forward(Big value)
            {
                return Read(value);
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);
        var forwardHeader = ExtractDefinitionHeader(llvm, "Forward");
        var forwardBody = ExtractDefinitionBody(llvm, "Forward");

        Assert.DoesNotContain("; LLVM body emission fallback for Forward", llvm);
        Assert.Contains("define fastcc noundef i64 @Forward(ptr noundef nonnull byval(%Big)", llvm);
        Assert.Contains("memory(argmem: read)", forwardHeader);
        Assert.DoesNotContain("memory(readwrite", forwardHeader);
        Assert.Matches(@"call fastcc i64 @Read\(ptr nonnull byval\(%Big\) noalias readonly captures\(none\) dereferenceable\(32\) align 8 %arg_value\)", forwardBody);
        Assert.DoesNotContain("%abi_callarg_value", forwardBody);
        Assert.DoesNotContain("load %Big", forwardBody);
    }

    [Fact]
    public void DynamicStorageAllocationIndexInitAndDropEmitRuntimeAllocatorCalls()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn u64[0 2 ** 63 - 1] Run()
            {
                stack mut dynamic u32[0 2 ** 31 - 1] values = new(4);
                init values[0] = 7;
                return values.Capacity;
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);

        Assert.DoesNotContain("; LLVM body emission fallback for Run", llvm);
        Assert.Contains("@__stark_runtime_alloc", llvm);
        Assert.Contains("@__stark_runtime_free", llvm);
        Assert.Contains("insertvalue { ptr, i64, i64 }", llvm);
        Assert.Contains("extractvalue { ptr, i64, i64 }", llvm);
        Assert.Contains("getelementptr", llvm);
        Assert.Contains("i32 1", llvm);
        Assert.Contains("store i64", llvm);

        var runSignature = Regex.Match(llvm, @"define fastcc[^\n]+ @Run\(\)[^\n]*").Value;
        Assert.Contains("memory(readwrite", runSignature);
        Assert.DoesNotContain("nofree", runSignature);
        Assert.DoesNotContain("nosync", runSignature);
    }

    [Fact]
    public void PositiveDynamicStorageAllocationElidesZeroAndOverflowBranches()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn u64[0 2 ** 63 - 1] Run()
            {
                stack mut dynamic u32[0 2 ** 31 - 1] values = new(4);
                return values.Capacity;
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var runBody = ExtractDefinitionBody(GetLlvmRaw(result), "Run");

        Assert.Contains("@__stark_runtime_alloc", runBody, StringComparison.Ordinal);
        Assert.DoesNotContain("dynamic_alloc_is_zero", runBody, StringComparison.Ordinal);
        Assert.DoesNotContain("dynamic_alloc_overflow", runBody, StringComparison.Ordinal);
        Assert.DoesNotContain("@__stark_dynamic_alloc", runBody, StringComparison.Ordinal);
    }

    [Fact]
    public void ArenaDynamicStorageAllocationUsesFrameAllocatorAndDropsWithoutFreeingOwner()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn u64[0 2 ** 63 - 1] Run()
            {
                stack mut dynamic u32[0 2 ** 31 - 1] values = new(arena, 4);
                return values.Capacity;
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);
        var runBody = ExtractDefinitionBody(llvm, "Run");

        Assert.DoesNotContain("; LLVM body emission fallback for Run", llvm);
        Assert.Contains("%__stark_arena_frame = alloca { ptr, ptr, ptr }, align 8", runBody, StringComparison.Ordinal);
        Assert.Contains("call void @__stark_arena_enter(ptr nonnull %__stark_arena_frame)", runBody, StringComparison.Ordinal);
        Assert.Contains("call void @__stark_arena_leave(ptr nonnull %__stark_arena_frame)", runBody, StringComparison.Ordinal);
        Assert.Contains("call noalias nonnull noundef align 4 ptr @__stark_arena_alloc(ptr nonnull %__stark_arena_frame", runBody, StringComparison.Ordinal);
        Assert.Contains("insertvalue { ptr, i64, i64 }", runBody, StringComparison.Ordinal);
        Assert.DoesNotContain("dynamic_alloc_is_zero", runBody, StringComparison.Ordinal);
        Assert.DoesNotContain("dynamic_alloc_overflow", runBody, StringComparison.Ordinal);
        Assert.DoesNotContain("@__stark_dynamic_alloc", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("@__stark_arena_dynamic_alloc", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("dynamic_free_ptr", runBody, StringComparison.Ordinal);
        Assert.DoesNotContain("@__stark_runtime_free", runBody, StringComparison.Ordinal);
        Assert.Contains("define linkonce_odr hidden noalias nonnull noundef ptr @__stark_arena_alloc(ptr captures(none) %frame, i64 noundef %size, i64 noundef allocalign %alignment) unnamed_addr allocsize(1) allockind(\"alloc,uninitialized,aligned\") \"alloc-family\"=\"__stark_arena_alloc\" nounwind comdat", llvm, StringComparison.Ordinal);
    }

    [Fact]
    public void DynamicStorageDropEmitsElementDestructorLoopBeforeFree()
    {
        var result = Compile(
            """
            module Demo

            static mut i32[min max] Counter = 0;

            unsafe fn void Bump(i32[min max] value)
            {
                stack i32[min max] a = value + (value / 3);
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
                Counter = Counter + m;
                return;
            }

            struct Token
            {
                i32[min max] Value;

                drop
                {
                    Bump(self.Value);
                }
            }

            unsafe fn void Run()
            {
                stack mut dynamic Token values = new(2);
                init values[0] = new Token()
                {
                    Value = 1
                };
                init values[1] = new Token()
                {
                    Value = 2
                };
                return;
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);
        var runBody = ExtractDefinitionBody(llvm, "Run");

        Assert.DoesNotContain("; LLVM body emission fallback for Run", llvm);
        Assert.Contains("call fastcc void @Bump", runBody, StringComparison.Ordinal);
        Assert.Contains("@__stark_runtime_free", runBody, StringComparison.Ordinal);
        Assert.True(
            runBody.IndexOf("call fastcc void @Bump", StringComparison.Ordinal)
            < runBody.IndexOf("@__stark_runtime_free", StringComparison.Ordinal),
            runBody);
    }

    [Fact]
    public void DynamicStorageElementLoadsAndStoresCarryElementAlignment()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn u64[0 2 ** 63 - 1] Run()
            {
                stack mut dynamic u64[0 2 ** 63 - 1] values = new(2);
                init values[0] = 7;
                init values[1] = 11;
                return values[1];
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);
        var runBody = Regex.Match(llvm, @"define fastcc[^{]+ @Run\([^\n]*\)[\s\S]*?^}", RegexOptions.Multiline).Value;

        Assert.DoesNotContain("; LLVM body emission fallback for Run", llvm);
        Assert.True(Regex.Matches(runBody, @"store i64 (?:%v\d+|\d+), ptr %v\d+, align 8").Count >= 2, runBody);
        Assert.Matches(@"load i64, ptr %v\d+, align 8", runBody);
    }

    [Fact]
    public void DynamicStorageReserveEmitsDirectReallocatePath()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn u64[0 2 ** 63 - 1] Run()
            {
                stack mut dynamic u32[0 2 ** 31 - 1] values = new(4);
                values.Reserve(8);
                return values.Capacity;
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);

        Assert.DoesNotContain("; LLVM body emission fallback for Run", llvm);
        Assert.Contains("@__stark_runtime_realloc", llvm);
        Assert.Contains("dynamic_reserve_needed", llvm);
        Assert.Contains("extractvalue { ptr, i64, i64 }", llvm);
        Assert.Contains("insertvalue { ptr, i64, i64 }", llvm);
        Assert.Contains("store { ptr, i64, i64 }", llvm);
    }

    [Fact]
    public void OptimizedDynamicStorageReserveNoopElidesRuntimeReservePath()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn u64[0 2 ** 63 - 1] Run()
            {
                stack mut dynamic u32[0 2 ** 31 - 1] values = new(8);
                values.Reserve(4);
                return values.Capacity;
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);
        var runBody = Regex.Match(llvm, @"define fastcc[^{]+ @Run\([^\n]*\)[\s\S]*?^}", RegexOptions.Multiline).Value;

        Assert.DoesNotContain("; LLVM body emission fallback for Run", llvm);
        Assert.DoesNotContain("dynamic_reserve_needed", runBody);
        Assert.DoesNotContain("@__stark_runtime_realloc", runBody);
        Assert.DoesNotContain("@__stark_dynamic_reserve", runBody);
    }

    [Fact]
    public void OptimizedDynamicStorageZeroCapacityDropElidesRuntimeFreePath()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn u64[0 2 ** 63 - 1] Run()
            {
                stack mut dynamic u32[0 max] values = new(0);
                return values.Capacity;
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);
        var runBody = Regex.Match(llvm, @"define fastcc[^{]+ @Run\([^\n]*\)[\s\S]*?^}", RegexOptions.Multiline).Value;

        Assert.DoesNotContain("; LLVM body emission fallback for Run", llvm);
        Assert.DoesNotContain("dynamic_free_ptr", runBody);
        Assert.DoesNotContain("@__stark_runtime_free", runBody);
    }

    [Fact]
    public void DynamicStorageTryReserveEmitsDirectFallibleReallocatePath()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn u64[0 2 ** 63 - 1] Run()
            {
                stack mut dynamic u32[0 2 ** 31 - 1] values = new(4);
                if (!values.TryReserve(8))
                {
                    return 0;
                }

                return values.Capacity;
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);
        var runBody = Regex.Match(llvm, @"define fastcc[^{]+ @Run\([^\n]*\)[\s\S]*?^}", RegexOptions.Multiline).Value;

        Assert.DoesNotContain("; LLVM body emission fallback for Run", llvm);
        Assert.Contains("@__stark_runtime_try_realloc", llvm);
        Assert.Contains("define linkonce_odr hidden noalias noundef ptr @__stark_runtime_try_alloc", llvm);
        Assert.Contains("define linkonce_odr hidden i1 @__stark_dynamic_try_reserve", llvm);
        Assert.Contains("dynamic_try_reserve_needed", runBody);
        Assert.Contains("call ptr @__stark_runtime_try_realloc", runBody);
        Assert.Contains("phi i1", runBody);
        Assert.Contains("dynamic_try_reserve_failed", runBody);
    }

    [Fact]
    public void OptimizedDynamicStorageTryReserveSuccessPrunesImpossibleCapacityBranch()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn u64[0 2 ** 63 - 1] Run()
            {
                stack mut dynamic u32[0 2 ** 31 - 1] values = new(4);
                if (!values.TryReserve(8))
                {
                    return 1;
                }

                if (values.Capacity < 8)
                {
                    return 2;
                }

                return 3;
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);
        var runBody = Regex.Match(llvm, @"define fastcc[^{]+ @Run\([^\n]*\)[\s\S]*?^}", RegexOptions.Multiline).Value;

        Assert.DoesNotContain("; LLVM body emission fallback for Run", llvm);
        Assert.Contains("dynamic_try_reserve_done", runBody);
        Assert.Contains("ret i64 1", runBody);
        Assert.DoesNotContain("ret i64 2", runBody);
        Assert.Contains("ret i64 3", runBody);
    }

    [Fact]
    public void DynamicStorageTryReserveCapacityEmitsExactFallibleReallocatePath()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn u64[0 2 ** 63 - 1] Run()
            {
                stack mut dynamic u32[0 2 ** 31 - 1] values = new(4);
                if (!values.TryReserveCapacity(8))
                {
                    return 0;
                }

                return values.Capacity;
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);
        var runBody = Regex.Match(llvm, @"define fastcc[^{]+ @Run\([^\n]*\)[\s\S]*?^}", RegexOptions.Multiline).Value;

        Assert.DoesNotContain("; LLVM body emission fallback for Run", llvm);
        Assert.Contains("@__stark_runtime_try_realloc", llvm);
        Assert.Contains("define linkonce_odr hidden i1 @__stark_dynamic_try_reserve_capacity", llvm);
        Assert.Contains("dynamic_try_reserve_capacity_realloc", runBody);
        Assert.Contains("call ptr @__stark_runtime_try_realloc", runBody);
        Assert.Contains("phi i1", runBody);
        Assert.Contains("dynamic_try_reserve_capacity_failed", runBody);
        Assert.DoesNotContain("dynamic_try_reserve_needed", runBody);
        Assert.DoesNotContain("dynamic_try_reserve_minimum", runBody);
        Assert.DoesNotContain("dynamic_try_reserve_new_capacity", runBody);
    }

    [Fact]
    public void ArenaDynamicStorageReserveEmitsGrowCopyWithoutRuntimeReallocate()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn u64[0 max] Run()
            {
                stack mut dynamic u64[0 max] values = new(arena, 1);
                init values[0] = 41;
                values.Reserve(8);
                return values[0] +% (u64[0 max])values.Capacity;
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);
        var runBody = ExtractDefinitionBody(llvm, "Run");

        Assert.DoesNotContain("; LLVM body emission fallback for Run", llvm);
        Assert.Contains("dynamic_reserve_arena", runBody, StringComparison.Ordinal);
        Assert.Contains("call noalias nonnull noundef align 8 ptr @__stark_arena_alloc", runBody, StringComparison.Ordinal);
        Assert.Contains("call void @llvm.memcpy.p0.p0.i64(ptr align 8", runBody, StringComparison.Ordinal);
        Assert.DoesNotContain("@__stark_runtime_realloc", runBody, StringComparison.Ordinal);
        Assert.DoesNotContain("@__stark_runtime_try_realloc", runBody, StringComparison.Ordinal);
        Assert.DoesNotContain("@__stark_runtime_free", runBody, StringComparison.Ordinal);
    }

    [Fact]
    public void ArenaDynamicStorageTryReserveEmitsFallibleGrowCopy()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn u64[0 max] Run()
            {
                stack mut dynamic u64[0 max] values = new(arena, 1);
                init values[0] = 41;
                if (!values.TryReserve(8))
                {
                    return 0;
                }

                return values[0] +% (u64[0 max])values.Capacity;
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);
        var runBody = ExtractDefinitionBody(llvm, "Run");

        Assert.DoesNotContain("; LLVM body emission fallback for Run", llvm);
        Assert.Contains("dynamic_try_reserve_arena", runBody, StringComparison.Ordinal);
        Assert.Contains("call noalias noundef ptr @__stark_arena_try_alloc", runBody, StringComparison.Ordinal);
        Assert.Contains("call void @llvm.memcpy.p0.p0.i64(ptr align 8", runBody, StringComparison.Ordinal);
        Assert.Contains("phi i1", runBody, StringComparison.Ordinal);
        Assert.DoesNotContain("@__stark_runtime_realloc", runBody, StringComparison.Ordinal);
        Assert.DoesNotContain("@__stark_runtime_try_realloc", runBody, StringComparison.Ordinal);
        Assert.DoesNotContain("@__stark_runtime_free", runBody, StringComparison.Ordinal);
    }

    [Fact]
    public void ArenaDynamicStorageTryReserveCapacityEmitsExactFallibleGrowCopy()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn u64[0 max] Run()
            {
                stack mut dynamic u64[0 max] values = new(arena, 1);
                init values[0] = 41;
                if (!values.TryReserveCapacity(8))
                {
                    return 0;
                }

                return values[0] +% (u64[0 max])values.Capacity;
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);
        var runBody = ExtractDefinitionBody(llvm, "Run");

        Assert.DoesNotContain("; LLVM body emission fallback for Run", llvm);
        Assert.Contains("dynamic_try_reserve_capacity_arena", runBody, StringComparison.Ordinal);
        Assert.Contains("call noalias noundef ptr @__stark_arena_try_alloc", runBody, StringComparison.Ordinal);
        Assert.Contains("call void @llvm.memcpy.p0.p0.i64(ptr align 8", runBody, StringComparison.Ordinal);
        Assert.Contains("phi i1", runBody, StringComparison.Ordinal);
        Assert.DoesNotContain("dynamic_try_reserve_minimum", runBody, StringComparison.Ordinal);
        Assert.DoesNotContain("dynamic_try_reserve_new_capacity", runBody, StringComparison.Ordinal);
        Assert.DoesNotContain("@__stark_runtime_realloc", runBody, StringComparison.Ordinal);
        Assert.DoesNotContain("@__stark_runtime_try_realloc", runBody, StringComparison.Ordinal);
        Assert.DoesNotContain("@__stark_runtime_free", runBody, StringComparison.Ordinal);
    }

    [Fact]
    public void ArenaDynamicStorageDropEmitsElementDestructorLoopBeforeFrameCleanupWithoutFreeingOwner()
    {
        var result = Compile(
            """
            module Demo

            static mut i32[min max] Counter = 0;

            unsafe fn void Bump(i32[min max] value)
            {
                stack i32[min max] a = value + (value / 3);
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
                Counter = Counter + m;
                return;
            }

            struct Token
            {
                i32[min max] Value;

                drop
                {
                    Bump(self.Value);
                }
            }

            unsafe fn void Run()
            {
                stack mut dynamic Token values = new(arena, 1);
                init values[0] = new Token()
                {
                    Value = 1
                };
                values.Reserve(4);
                init values[1] = new Token()
                {
                    Value = 2
                };
                return;
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);
        var runBody = ExtractDefinitionBody(llvm, "Run");

        Assert.DoesNotContain("; LLVM body emission fallback for Run", llvm);
        Assert.Contains("dynamic_reserve_arena", runBody, StringComparison.Ordinal);
        Assert.Contains("call noalias nonnull noundef align 4 ptr @__stark_arena_alloc", runBody, StringComparison.Ordinal);
        Assert.Contains("call void @llvm.memcpy.p0.p0.i64(ptr align 4", runBody, StringComparison.Ordinal);
        Assert.Contains("call fastcc void @Bump", runBody, StringComparison.Ordinal);
        Assert.Contains("call void @__stark_arena_leave(ptr nonnull %__stark_arena_frame)", runBody, StringComparison.Ordinal);
        Assert.True(
            runBody.IndexOf("call fastcc void @Bump", StringComparison.Ordinal)
            < runBody.IndexOf("@__stark_arena_leave", StringComparison.Ordinal),
            runBody);
        Assert.DoesNotContain("dynamic_free_ptr", runBody, StringComparison.Ordinal);
        Assert.DoesNotContain("@__stark_runtime_realloc", runBody, StringComparison.Ordinal);
        Assert.DoesNotContain("@__stark_runtime_try_realloc", runBody, StringComparison.Ordinal);
        Assert.DoesNotContain("@__stark_runtime_free", runBody, StringComparison.Ordinal);
    }

    [Fact]
    public void DynamicStorageInitSliceWritesCommitOwnerLength()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn u64[0 2 ** 63 - 1] Run()
            {
                stack mut dynamic u32[0 2 ** 31 - 1] values = new(4);
                stack init u32[0 2 ** 31 - 1][] spare = init values[values.Length, 2];
                init spare[0] = 7;
                init spare[1] = 8;
                return values.Length;
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);
        var runBody = Regex.Match(llvm, @"define fastcc[^{]+ @Run\(\)[\s\S]*?^}", RegexOptions.Multiline).Value;

        Assert.DoesNotContain("; LLVM body emission fallback for Run", llvm);
        Assert.True(
            Regex.Matches(runBody, @"store i64 %v\d+, ptr %v\d+").Count >= 2,
            runBody);
    }

    [Fact]
    public void OptimizedDynamicStorageInitSlicePreservesPointerDefinitionAndAlignment()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn u64[0 2 ** 63 - 1] Run()
            {
                stack mut dynamic u64[0 2 ** 63 - 1] values = new(4);
                stack init u64[0 2 ** 63 - 1][] spare = init values[values.Length, 2];
                init spare[0] = 7;
                return values.Length;
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);
        var runBody = Regex.Match(llvm, @"define fastcc[^{]+ @Run\(\)[\s\S]*?^}", RegexOptions.Multiline).Value;

        Assert.DoesNotContain("; LLVM body emission fallback for Run", llvm);
        var slicePointer = Regex.Match(
            runBody,
            @"insertvalue \{ ptr, i64 \} zeroinitializer, ptr (?<pointer>%v\d+), 0");
        Assert.True(slicePointer.Success, runBody);

        var pointerName = slicePointer.Groups["pointer"].Value;
        var definitionIndex = runBody.IndexOf($"{pointerName} = getelementptr", StringComparison.Ordinal);
        Assert.True(definitionIndex >= 0 && definitionIndex < slicePointer.Index, runBody);
        Assert.Matches(@"getelementptr inbounds nuw i64, ptr %v\d+_data, i8 0", runBody);
        Assert.Matches(@"store i64 7, ptr %v\d+, align 8", runBody);
    }

    [Fact]
    public void InitSliceParametersKeepReadableSliceHeaderContract()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn void Fill(init u64[0 2 ** 63 - 1][] dest)
            {
                init dest[0] = 1;
                return;
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);
        var fillHeader = ExtractDefinitionHeader(llvm, "Fill");

        Assert.Contains(
            "ptr noundef nonnull noalias captures(none) dereferenceable(16) align 8 %arg_dest",
            fillHeader);
        Assert.DoesNotContain("writeonly", fillHeader, StringComparison.Ordinal);
        Assert.DoesNotContain("writable", fillHeader, StringComparison.Ordinal);
        Assert.DoesNotContain("initializes(", fillHeader, StringComparison.Ordinal);
        Assert.DoesNotContain("dead_on_return", fillHeader, StringComparison.Ordinal);
    }

    [Fact]
    public void InitSliceElementStoresDoNotReadDestinationElementBeforeWrite()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn void Fill(init u64[0 2 ** 63 - 1][] dest)
            {
                init dest[0] = 1;
                return;
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);
        var fillBody = ExtractDefinitionBody(llvm, "Fill");

        Assert.Contains("store i64", fillBody, StringComparison.Ordinal);
        Assert.DoesNotContain("load i64, ptr", fillBody, StringComparison.Ordinal);
    }

    [Fact]
    public void DynamicStorageMoveLastEmitsDirectLengthUpdateAndLoad()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn i64[min max] Run(bool drain)
            {
                stack mut dynamic i64[min max] values = new(2);
                init values[0] = 10;
                init values[1] = 20;
                if (drain)
                {
                    stack i64[min max] a = values.MoveLast();
                    stack i64[min max] b = values.MoveLast();
                }
                stack i64[min max] moved = values.MoveLast();
                return moved + values.Length;
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);

        Assert.DoesNotContain("; LLVM body emission fallback for Run", llvm);
        Assert.Contains("dynamic_move_is_empty", llvm);
        Assert.Contains("dynamic_move_new_length", llvm);
        Assert.Contains("call void @llvm.trap()", llvm);
        Assert.Contains("getelementptr inbounds nuw i64", llvm);
        Assert.Contains("load i64", llvm);
        Assert.Contains("store { ptr, i64, i64 }", llvm);
    }

    [Fact]
    public void OptimizedDynamicStorageMoveLastSkipsEmptyCheckWhenPrefixFactsProveNonEmpty()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn i64[min max] Run()
            {
                stack mut dynamic i64[min max] values = new(1);
                init values[0] = 10;
                return values.MoveLast();
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);
        var runBody = Regex.Match(llvm, @"define fastcc[^{]+ @Run\([^\n]*\)[\s\S]*?^}", RegexOptions.Multiline).Value;

        Assert.DoesNotContain("; LLVM body emission fallback for Run", llvm);
        Assert.DoesNotContain("dynamic_move_is_empty", runBody);
        Assert.DoesNotContain("dynamic_move_empty", runBody);
        Assert.Contains("dynamic_move_new_length", runBody);
    }

    [Fact]
    public void DynamicStorageMoveAtEmitsDirectLengthUpdateLoadAndMemmove()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn i64[min max] Run(u8[0 5] index)
            {
                stack mut dynamic i64[min max] values = new(3);
                init values[0] = 10;
                init values[1] = 20;
                init values[2] = 30;
                stack i64[min max] moved = values.MoveAt(index);
                return moved + values[0] + values.Length;
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);
        var runBody = Regex.Match(llvm, @"define fastcc[^{]+ @Run\([^\n]*\)[\s\S]*?^}", RegexOptions.Multiline).Value;

        Assert.DoesNotContain("; LLVM body emission fallback for Run", llvm);
        Assert.Contains("dynamic_move_at_in_bounds", runBody);
        Assert.Contains("dynamic_move_at_new_length", runBody);
        Assert.True(Regex.Matches(runBody, @"getelementptr inbounds nuw i64").Count >= 2, runBody);
        Assert.Contains("call void @llvm.memmove.p0.p0.i64", runBody);
        Assert.DoesNotContain("llvm.memcpy", runBody, StringComparison.Ordinal);
        Assert.Contains("store { ptr, i64, i64 }", runBody);
    }

    [Fact]
    public void OptimizedDynamicStorageMoveAtSkipsBoundsCheckWhenPrefixFactsProveInBounds()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn i64[min max] Run()
            {
                stack mut dynamic i64[min max] values = new(2);
                init values[0] = 10;
                init values[1] = 20;
                return values.MoveAt(0);
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);
        var runBody = Regex.Match(llvm, @"define fastcc[^{]+ @Run\([^\n]*\)[\s\S]*?^}", RegexOptions.Multiline).Value;

        Assert.DoesNotContain("; LLVM body emission fallback for Run", llvm);
        Assert.DoesNotContain("dynamic_move_at_in_bounds", runBody);
        Assert.DoesNotContain("dynamic_move_at_bad_index", runBody);
        Assert.Contains("dynamic_move_at_new_length", runBody);
    }

    [Fact]
    public void LargeDynamicStorageMoveLastIntoLocalUsesDestinationSlot()
    {
        var result = Compile(
            """
            module Demo

            struct Big
            {
                i8[min max][512] Data;
                i64[min max] Len;
            }

            unsafe fn i64[min max] Run()
            {
                stack mut dynamic Big values = new(1);
                init values[0] = new Big()
                {
                    Len = 7
                };
                stack Big moved = values.MoveLast();
                return moved.Len;
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);
        var runBody = Regex.Match(llvm, @"define fastcc[^{]+ @Run\([^\n]*\)[\s\S]*?^}", RegexOptions.Multiline).Value;

        Assert.Contains("%slot_moved = alloca %Big", runBody);
        Assert.Matches(
            @"call void @llvm\.memcpy\.p0\.p0\.i64\(ptr align 8 %slot_moved, ptr align 8 %abi_dynamic_move_element_ptr_\d+, i64 520, i1 false\)",
            runBody);
        Assert.DoesNotContain("%abi_dynamic_move_result_slot_", runBody);
        Assert.DoesNotContain("load %Big", runBody);
        Assert.DoesNotContain("store %Big", runBody);
        Assert.DoesNotContain("phi %Big", runBody);
    }

    [Fact]
    public void LargeDynamicStorageMoveLastReturnCopiesDirectlyIntoSRetBuffer()
    {
        var result = Compile(
            """
            module Demo

            struct Big
            {
                i8[min max][512] Data;
                i64[min max] Len;
            }

            unsafe fn Big Run()
            {
                stack mut dynamic Big values = new(1);
                init values[0] = new Big()
                {
                    Len = 7
                };
                return values.MoveLast();
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);
        var runBody = Regex.Match(llvm, @"define fastcc[^{]+ @Run\([^\n]*\)[\s\S]*?^}", RegexOptions.Multiline).Value;

        Assert.Contains("define fastcc void @Run(ptr noundef noalias sret(%Big) nonnull dereferenceable(520) align 8 %ret)", llvm);
        Assert.Matches(
            @"call void @llvm\.memcpy\.p0\.p0\.i64\(ptr align 8 %ret, ptr align 8 %abi_dynamic_move_element_ptr_\d+, i64 520, i1 false\)",
            runBody);
        Assert.DoesNotContain("%abi_dynamic_move_result_slot_", runBody);
        Assert.DoesNotContain("load %Big", runBody);
        Assert.DoesNotContain("store %Big", runBody);
        Assert.DoesNotContain("phi %Big", runBody);
    }

    [Fact]
    public void LargeDynamicStorageMoveAtReturnCopiesBeforeTailShift()
    {
        var result = Compile(
            """
            module Demo

            struct Big
            {
                i8[min max][512] Data;
                i64[min max] Len;
            }

            unsafe fn Big Run()
            {
                stack mut dynamic Big values = new(2);
                init values[0] = new Big()
                {
                    Len = 7
                };
                init values[1] = new Big()
                {
                    Len = 9
                };
                return values.MoveAt(0);
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);
        var runBody = Regex.Match(llvm, @"define fastcc[^{]+ @Run\([^\n]*\)[\s\S]*?^}", RegexOptions.Multiline).Value;

        var copy = Regex.Match(
            runBody,
            @"call void @llvm\.memcpy\.p0\.p0\.i64\(ptr align 8 %ret, ptr align 8 %abi_dynamic_move_at_element_ptr_\d+, i64 520, i1 false\)");
        Assert.True(copy.Success, runBody);
        var move = runBody.IndexOf("call void @llvm.memmove.p0.p0.i64", StringComparison.Ordinal);
        Assert.True(move > copy.Index, runBody);
        Assert.DoesNotContain("%abi_dynamic_move_at_result_slot_", runBody);
        Assert.DoesNotContain("load %Big", runBody);
        Assert.DoesNotContain("store %Big", runBody);
        Assert.DoesNotContain("phi %Big", runBody);
    }

    [Fact]
    public void LargeDynamicStorageMoveLastIntoOutParameterUsesOutStorage()
    {
        var result = Compile(
            """
            module Demo

            struct Big
            {
                i8[min max][512] Data;
                i64[min max] Len;
            }

            unsafe fn bool Pop(mut borrow dynamic Big values, out Big value)
            {
                value = values.MoveLast();
                return true;
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);
        var popBody = Regex.Match(llvm, @"define fastcc[^{]+ @Pop\([^\n]*\)[\s\S]*?^}", RegexOptions.Multiline).Value;

        Assert.Contains("ptr noundef nonnull noalias writeonly dereferenceable(520) writable initializes((0, 520)) captures(none) align 8 %arg_value", llvm);
        Assert.Matches(
            @"call void @llvm\.memcpy\.p0\.p0\.i64\(ptr align 8 %arg_value, ptr align 8 %abi_dynamic_move_element_ptr_\d+, i64 520, i1 false\)",
            popBody);
        Assert.DoesNotContain("%abi_dynamic_move_result_slot_", popBody);
        Assert.DoesNotContain("load %Big", popBody);
        Assert.DoesNotContain("store %Big", popBody);
        Assert.DoesNotContain("phi %Big", popBody);
    }

    [Fact]
    public void LargeDynamicStorageMoveAtIntoOutParameterCopiesBeforeTailShift()
    {
        var result = Compile(
            """
            module Demo

            struct Big
            {
                i8[min max][512] Data;
                i64[min max] Len;
            }

            unsafe fn bool PopAt(mut borrow dynamic Big values, out Big value)
            {
                value = values.MoveAt(0);
                return true;
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);
        var popBody = Regex.Match(llvm, @"define fastcc[^{]+ @PopAt\([^\n]*\)[\s\S]*?^}", RegexOptions.Multiline).Value;

        var copy = Regex.Match(
            popBody,
            @"call void @llvm\.memcpy\.p0\.p0\.i64\(ptr align 8 %arg_value, ptr align 8 %abi_dynamic_move_at_element_ptr_\d+, i64 520, i1 false\)");
        Assert.True(copy.Success, popBody);
        var move = popBody.IndexOf("call void @llvm.memmove.p0.p0.i64", StringComparison.Ordinal);
        Assert.True(move > copy.Index, popBody);
        Assert.DoesNotContain("%abi_dynamic_move_at_result_slot_", popBody);
        Assert.DoesNotContain("load %Big", popBody);
        Assert.DoesNotContain("store %Big", popBody);
        Assert.DoesNotContain("phi %Big", popBody);
    }

    [Fact]
    public void LargeDynamicStorageMoveLastIntoGlobalUsesGlobalStorage()
    {
        var result = Compile(
            """
            module Demo

            struct Big
            {
                i8[min max][512] Data;
                i64[min max] Len;
            }

            static mut Big Target = new Big();

            unsafe fn void PopGlobal(mut borrow dynamic Big values)
            {
                Target = values.MoveLast();
                return;
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);
        var popBody = Regex.Match(llvm, @"define fastcc[^{]+ @PopGlobal\([^\n]*\)[\s\S]*?^}", RegexOptions.Multiline).Value;

        Assert.Matches(
            @"call void @llvm\.memcpy\.p0\.p0\.i64\(ptr align 8 @Target, ptr align 8 %abi_dynamic_move_element_ptr_\d+, i64 520, i1 false\)",
            popBody);
        Assert.DoesNotContain("%abi_dynamic_move_result_slot_", popBody);
        Assert.DoesNotContain("load %Big", popBody);
        Assert.DoesNotContain("store %Big", popBody);
        Assert.DoesNotContain("phi %Big", popBody);
    }

    [Fact]
    public void UnsafeDynamicStorageNonTailMoveEmitsDirectElementLoad()
    {
        var result = Compile(
            """
            module Demo

            struct Token
            {
                u64[0 2 ** 63 - 1] Value;
            }

            unsafe fn u64[0 2 ** 63 - 1] Run()
            {
                stack mut dynamic Token values = new(2);
                init values[0] = new Token()
                {
                    Value = 10
                };
                init values[1] = new Token()
                {
                    Value = 20
                };
                unsafe
                {
                    stack Token moved = values[0];
                    return moved.Value;
                }
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);
        var runBody = Regex.Match(llvm, @"define fastcc[^{]+ @Run\(\)[\s\S]*?^}", RegexOptions.Multiline).Value;

        Assert.DoesNotContain("; LLVM body emission fallback for Run", llvm);
        Assert.DoesNotContain("dynamic_move_is_empty", runBody);
        Assert.DoesNotContain("dynamic_move_new_length", runBody);
        Assert.Contains("getelementptr", runBody);
        Assert.Contains("load %Token", runBody);
    }

    [Fact]
    public void AggregateLoadedFromDynamicSlotKeepsSnapshotAfterSlotReplacement()
    {
        var result = Compile(
            """
            module Demo

            enum Slot
            {
                Empty,
                Full(u32[0 2 ** 31 - 1]),
            }

            unsafe fn u32[0 2 ** 31 - 1] Run()
            {
                stack mut dynamic Slot slots = new(1);
                init slots[0] = Slot.Full(7);
                unsafe
                {
                    stack Slot snapshot = slots[0];
                    slots[0] = Slot.Empty;
                    switch (snapshot)
                    {
                        case Slot.Full(var value):
                            return value;
                        case Slot.Empty:
                            return 0;
                    }
                }
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);
        var runBody = Regex.Match(llvm, @"define fastcc[^{]+ @Run\(\)[\s\S]*?^}", RegexOptions.Multiline).Value;

        Assert.DoesNotContain("; LLVM body emission fallback for Run", llvm);
        var snapshotLoad = Regex.Match(runBody, @"(?<value>%v\d+) = load %Slot, ptr %v\d+");
        Assert.True(snapshotLoad.Success, runBody);
        Assert.Contains($"extractvalue %Slot {snapshotLoad.Groups["value"].Value}, 0", runBody);
        Assert.Contains($"extractvalue %Slot {snapshotLoad.Groups["value"].Value}, 1", runBody);
    }

    [Fact]
    public void SmallRangeUnsignedIntegerOperationsUseUnsignedLlvmOpcodes()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn bool Less(u8[0 127] left, u8[0 127] right)
            {
                return left < right;
            }

            unsafe fn u8[0 127] Divide(u8[0 127] left, u8[0 127] right)
            {
                return left / right;
            }

            unsafe fn u8[0 127] Remainder(u8[0 127] left, u8[0 127] right)
            {
                return left % right;
            }

            unsafe fn u8[0 127] ShiftDown(u8[0 127] left, u8[0 127] right)
            {
                return left >> right;
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);

        Assert.Contains("icmp ult i8", llvm);
        Assert.Contains("udiv i8", llvm);
        Assert.Contains("urem i8", llvm);
        Assert.Contains("lshr i8", llvm);
        Assert.DoesNotContain("icmp slt i8", llvm);
        Assert.DoesNotContain("sdiv i8", llvm);
        Assert.DoesNotContain("srem i8", llvm);
        Assert.DoesNotContain("ashr i8", llvm);
    }

    [Fact]
    public void NonCapturingLambdaFunctionPointersEmitSyntheticFunctionDefinitions()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn i32[min max] Run(i32[min max] seed)
            {
                stack fnptr<fn i32[min max](i32[min max])> increment = (i32[min max] value) =>
                {
                    stack i32[min max] a = value + (value / 3);
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
                };
                return increment(seed);
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);

        Assert.Contains("; synthetic definition: Run.__lambda_", llvm);
        Assert.Contains("define internal dso_local fastcc noundef i32 @Run___lambda_", llvm);
        Assert.Contains("call fastcc i32 @Run___lambda_", llvm);
    }

    [Fact]
    public void NonCapturingLambdaArgumentsLowerToFunctionPointers()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn i32[min max] Apply(fnptr<fn i32[min max](i32[min max])> op)
            {
                stack i32[min max] r = op(41);
                stack i32[min max] a = r + (r / 3);
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

            unsafe fn i32[min max] Run()
            {
                return Apply((i32[min max] value) => value + 1);
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);

        Assert.Contains("; synthetic definition: Run.__lambda_", llvm);
        Assert.Contains("call fastcc i32 @Apply(ptr nonnull @Run___lambda_", llvm);
    }

    [Fact]
    public void GenericSizeofAndAlignofLowerAfterConcreteInstantiation()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn i64[min max] Layout<T>(T value)
            {
                return sizeof(T) + alignof(T);
            }

            unsafe fn i64[min max] Run()
            {
                return Layout(1);
            }
            """);

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvm(result);
        var runBody = ExtractDefinitionBody(llvm, "Run");

        Assert.Contains("ret i64 2", runBody);
        Assert.DoesNotContain("LLVM body emission fallback", llvm);
    }

    [Fact]
    public void DebugMetadataIsEmittedForFunctionsParametersAndStackLocals()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn i32[min max] Run(i32[min max] input)
            {
                stack mut i32[min max] value = input;
                stack rawmutptr<i32[min max]> ptr = &value;
                *ptr = input + 1;
                return value;
            }
            """,
            "/virtual/Demo.stark");

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);

        Assert.Contains("declare void @llvm.dbg.declare(metadata, metadata, metadata)", llvm);
        Assert.Contains("declare void @llvm.dbg.value(metadata, metadata, metadata)", llvm);
        Assert.Contains("!llvm.dbg.cu = !{!", llvm);
        Assert.Contains("distinct !DICompileUnit(", llvm);
        Assert.Contains("distinct !DISubprogram(name: \"Run\", linkageName: \"Run\"", llvm);
        Assert.Contains("!DILocalVariable(name: \"input\", arg: 1", llvm);
        Assert.Contains("!DILocalVariable(name: \"value\"", llvm);
        Assert.Contains("call void @llvm.dbg.value(metadata i32 %arg_input, metadata !", llvm);
        Assert.Contains("call void @llvm.dbg.declare(metadata ptr %slot_value, metadata !", llvm);
        Assert.Contains("!DILocation(line: 5, column:", llvm);
        Assert.Contains(", !dbg !", llvm);
    }

    [Fact]
    public void DebugMetadataMarksBuildsAsOptimized()
    {
        var optimized = Compile(
            """
            module Demo

            unsafe fn i32[min max] Run()
            {
                return 1;
            }
            """);

        Assert.True(optimized.Succeeded);

        Assert.Contains("isOptimized: true", GetLlvm(optimized));
        Assert.DoesNotContain("isOptimized: false", GetLlvm(optimized));
    }

    [Fact]
    public void ConstantBranchConditionsCanFoldAllTheWayToReturn()
    {
        var result = Compile(
            """
            module Demo

            unsafe finite law i32[min max] Run()
            {
                if (true)
                {
                    return 1;
                }

                return 2;
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvmRaw(result);

        Assert.Contains("ret i32 1", llvm);
        Assert.DoesNotContain("br i1", llvm);
        Assert.DoesNotContain("br label", llvm);
    }

    [Fact]
    public void BranchJoinCanOptimizeToDirectReturns()
    {
        var result = Compile(
            """
            module Demo

            unsafe finite law i32[min max] Run(bool flag)
            {
                stack mut i32[min max] value = 0;
                if (flag)
                {
                    value = 1;
                }
                else
                {
                    value = 2;
                }

                return value;
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("select i1 %arg_flag, i32 1, i32 2", llvm);
        Assert.Contains("ret i32 %select_", llvm);
        Assert.DoesNotContain("br i1 %arg_flag", llvm);
        Assert.DoesNotContain("phi i32", llvm);
    }

    [Fact]
    public void IfWeightAnnotationEmitsBranchWeightMetadata()
    {
        var result = Compile(
            """
            module Demo

            unsafe finite law i32[min max] Run(bool flag)
            {
                stack mut i32[min max] value = 0;
                if w99 (flag)
                {
                    value = 1;
                }
                else
                {
                    value = 2;
                }

                return value;
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvmRaw(result);

        Assert.Matches(@"br i1 %arg_flag, label %bb\d+, label %bb\d+, !prof !\d+", llvm);
        Assert.Matches(@"!\d+ = !\{!""branch_weights"", i32 99, i32 1\}", llvm);
    }

    [Fact]
    public void SwitchWeightAnnotationEmitsDistributedBranchWeightMetadata()
    {
        var result = Compile(
            """
            module Demo

            unsafe finite law i32[min max] Pick(u8[0 5] value)
            {
                switch w75 (value)
                {
                    case 0:
                        return 10;
                    case 1:
                        return 11;
                    case 2:
                        return 12;
                    case 3:
                        return 13;
                    case 4:
                        return 14;
                    default:
                        return 99;
                }
            }
            """);

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);

        Assert.Matches(@"switch i8 %arg_value, label %bb\d+ \[ .* \], !prof !\d+", llvm);
        Assert.Matches(@"!\d+ = !\{!""branch_weights"", i32 25, i32 15, i32 15, i32 15, i32 15, i32 15\}", llvm);
    }

    [Fact]
    public void ColdCallBranchTargetsInferUnlikelyBranchWeightMetadata()
    {
        var result = Compile(
            """
            module Demo

            cold noinline unsafe fn i32[min max] Slow(i32[min max] value)
            {
                return value + 1;
            }

            unsafe finite law i32[min max] Run(bool fail, i32[min max] value)
            {
                if (fail)
                {
                    return Slow(value);
                }

                return value;
            }
            """);

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);

        Assert.Matches(@"br i1 %arg_fail, label %bb\d+, label %bb\d+, !prof !\d+", llvm);
        Assert.Matches(@"!\d+ = !\{!""branch_weights"", i32 1, i32 2000\}", llvm);
    }

    [Fact]
    public void GlobalsUseVisibilityAwareLinkageAndConstantKinds()
    {
        var result = Compile(
            """
            module Globals

            public const Answer = 42;
            internal static rawptr<i8[min max]> Buffer = null;
            export static mut rawptr<i8[min max]> Visible = null;

            unsafe finite law i32[min max] Run()
            {
                return 0;
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("; visibility: public", llvm);
        Assert.Contains("@Answer = local_unnamed_addr constant i8 42", llvm);
        Assert.Contains("; visibility: internal", llvm);
        Assert.Contains("@Buffer = local_unnamed_addr constant ptr null", llvm);
        Assert.Contains("; visibility: export", llvm);
        Assert.Contains("@Visible = global ptr null", llvm);
    }

    [Fact]
    public void MutableGlobalsEmitRealDefinitionsStoresAndLoads()
    {
        var result = Compile(
            """
            module Demo

            static mut i32[min max] Counter = 0;

            unsafe finite i32[min max] Run()
            {
                Counter = Counter + 7;
                return Counter;
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("@Counter = global i32 0", llvm);
        Assert.Contains("load i32, ptr @Counter", llvm);
        Assert.Matches(@"store i32 %[^,]+, ptr @Counter", llvm);
    }

    [Fact]
    public void MutableGlobalLoadsAreNotForwardedAcrossDestructorBackedCalls()
    {
        var result = Compile(
            """
            module Demo

            static mut i32[min max] Counter = 0;

            struct LargeResource
            {
                i8[min max][512] Data;
                i32[min max] Value;

                drop
                {
                    Counter = Counter + self.Value;
                }
            }

            enum Result
            {
                Ok(LargeResource),
                Err(i32[min max])
            }

            unsafe fn LargeResource MakeResource(i32[min max] value)
            {
                stack mut LargeResource resource = new LargeResource();
                resource.Value = value;
                return resource;
            }

            unsafe fn Result MakeOk(i32[min max] value)
            {
                stack LargeResource resource = MakeResource(value);
                return Result.Ok(resource);
            }

            unsafe fn void RunSuccessPayloadMove()
            {
                stack Result result = MakeOk(7);
                switch (result)
                {
                    case Result.Ok(var payload):
                        stack LargeResource value = payload;
                    case Result.Err(var code):
                }

                return;
            }

            export unsafe fn i32[min max] main()
            {
                Counter = 0;
                RunSuccessPayloadMove();
                if (Counter != 7)
                {
                    return 1;
                }

                return 0;
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);
        var mainBody = Regex.Match(llvm, @"define [^{]+ @main\(\)[^{]*\{(?<body>[\s\S]*?)^}", RegexOptions.Multiline).Groups["body"].Value;

        var callIndex = mainBody.IndexOf("call fastcc void @RunSuccessPayloadMove()", StringComparison.Ordinal);
        Assert.True(callIndex >= 0, mainBody);
        var postCallLoadIndex = mainBody.IndexOf("load i32, ptr @Counter", callIndex, StringComparison.Ordinal);
        Assert.True(postCallLoadIndex > callIndex, mainBody);
        Assert.DoesNotContain("ret i32 1", mainBody);
    }

    [Fact]
    public void LibraryBuildQualifiesRootGlobalSymbolsAndPreservesExportNames()
    {
        var result = Compile(
            """
            module Math

            public const Answer = 42;
            internal static mut i32[min max] Counter = 0;
            static i32[min max] Hidden = 1;
            export static mut i32[min max] Visible = 0;

            unsafe finite i32[min max] Run()
            {
                Counter = Counter + 7;
                return Counter;
            }
            """,
            new CompilerOptions(
                EmitLlvmIr: true,
                QualifyModuleSymbols: true));

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("@Math_Answer = local_unnamed_addr constant i8 42", llvm);
        Assert.Contains("@Math_Counter = global i32 0", llvm);
        Assert.Contains("@Math_Hidden = internal unnamed_addr constant i32 1", llvm);
        Assert.Contains("@Visible = global i32 0", llvm);
        Assert.Contains("load i32, ptr @Math_Counter", llvm);
        Assert.Matches(@"store i32 %[^,]+, ptr @Math_Counter", llvm);
    }

    [Fact]
    public void ExecutableInternalizationCanMakeRootModulePrivateFunctionsLocal()
    {
        var result = Compile(
            """
            module Demo

            noinline unsafe fn i32[min max] Helper(i32[min max] value)
            {
                return value + 1;
            }

            export unsafe ffi fn i32[min max] main(i32[min max] value)
            {
                return Helper(value);
            }
            """,
            new CompilerOptions(
                EmitLlvmIr: true,
                InternalizeModulePrivate: true));

        Assert.True(result.Succeeded);
        var llvm = GetLlvmRaw(result);

        Assert.Contains("define internal dso_local fastcc noundef range(i32 -2147483647, -2147483648) i32 @Helper(i32 noundef %arg_value)", llvm);
        Assert.Contains("define i32 @main(i32 %arg_value)", llvm);
        Assert.Contains("call fastcc i32 @Helper(i32 %arg_value)", llvm);
        Assert.DoesNotContain("define internal dso_local i32 @main", llvm);
    }

    [Fact]
    public void RootFunctionSymbolIsQualifiedWhenItWouldCollideWithFfiImport()
    {
        var result = Compile(
            """
            import Win32
            module System.Runtime.Platform

            internal unsafe fn void ExitProcess(i32[min max] code)
            {
                Win32.ExitProcess(code);
                return;
            }
            """,
            new CompilerOptions(
                EmitLlvmIr: true,
                ModuleResolver: new InMemoryModuleResolver(
                [
                    (
                        new ResolvedModuleReference("Win32", "/virtual/Win32.stark", IsExternal: false),
                        """
                        module Win32

                        internal unsafe ffi fn void ExitProcess(i32[min max] code);
                        """,
                        "/virtual/Win32.stark"
                    )
                ])));

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("define fastcc void @System_Runtime_Platform_ExitProcess(i32 %arg_code)", llvm);
        Assert.Contains("declare void @ExitProcess(i32)", llvm);
        Assert.Contains("call void @ExitProcess(i32 %arg_code)", llvm);
        Assert.DoesNotContain("define fastcc void @ExitProcess(", llvm);
    }

    [Fact]
    public void FfiVarargsDeclarationsAndCallsUseLlvmVarargFunctionType()
    {
        var result = Compile(
            """
            module Demo

            public ffi varargs unsafe fn i32[min max] printf(ascii format);

            export unsafe fn i32[min max] main()
            {
                stack i32[min max] score = 42;
                return printf("score=%d", score);
            }
            """,
            new CompilerOptions(
                EmitLlvmIr: true));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvm(result);

        Assert.Contains("declare i32 @printf(ptr readonly, ...)", llvm);
        Assert.Contains("call i32 (ptr, ...) @printf(", llvm);
        Assert.Contains("i32 %", llvm);
    }

    [Fact]
    public void FfiVarargsPercentSAcceptsRawCCharPointer()
    {
        var result = Compile(
            """
            module Demo

            public ffi varargs unsafe fn i32[min max] printf(ascii format);

            export unsafe fn i32[min max] main()
            {
                stack mut System.C.c_char[6] name =
                {
                    83, 116, 97, 114, 107, 0
                };

                return printf("name=%s", &name[0]);
            }
            """,
            new CompilerOptions(
                EmitLlvmIr: true));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvm(result);

        Assert.Contains("declare i32 @printf(ptr readonly, ...)", llvm);
        Assert.Contains("call i32 (ptr, ...) @printf(", llvm);
    }

    [Fact]
    public void InternalizedImmutableGlobalsUseUnnamedAddrOnlyWhenAddressesStayInsignificant()
    {
        var result = Compile(
            """
            module Demo

            static i32[min max] Hidden = 1;
            static i32[min max] Exposed = 2;

            unsafe fn rawptr<i32[min max]> ExposedPtr()
            {
                return &Exposed;
            }
            """,
            new CompilerOptions(
                EmitLlvmIr: true,
                QualifyModuleSymbols: true));

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("@Demo_Hidden = internal unnamed_addr constant i32 1", llvm);
        Assert.Contains("@Demo_Exposed = internal constant i32 2", llvm);
        Assert.DoesNotContain("@Demo_Exposed = internal unnamed_addr constant i32 2", llvm);
        Assert.DoesNotContain("@Demo_Exposed = internal local_unnamed_addr constant i32 2", llvm);
        Assert.Contains("ret ptr @Demo_Exposed", llvm);
    }

    [Fact]
    public void AggregateAndArrayGlobalsEmitConcreteInitializers()
    {
        var result = Compile(
            """
            module Demo

            struct Pair
            {
                i32[min max] Left;
                i32[min max] Right;
            }

            const Pair Origin = new Pair()
            {
                Left = 1, Right = 2
            };
            static i32[min max][3] Values =
            {
                4, 7, 9
            };

            unsafe finite i32[min max] Run()
            {
                return Origin.Right + Values[1];
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("%Pair = type { i32, i32 }", llvm);
        Assert.Contains("@Origin = local_unnamed_addr constant %Pair { i32 1, i32 2 }", llvm);
        Assert.Contains("@Values = local_unnamed_addr constant [3 x i32] [i32 4, i32 7, i32 9]", llvm);
    }

    [Fact]
    public void IntegralAndScientificFloatConstantsEmitValidHexFloatLiterals()
    {
        // Regression: f64/f32 constant emission used to render invalid LLVM
        // IR — integral f64 constants used to print as bare integers
        // (`double 1`, `double 10`) and >= 1e17 magnitudes as bare scientific
        // notation (`double 1E+17`); LLVM rejects both ("integer constant must
        // have integer type"). Both the global-initializer array path and the
        // function-body scalar path now emit bit-exact hex floats (`double
        // 0xH...`), which round-trip every value.
        var result = Compile(
            """
            module Demo

            const f64[23] Pow10 =
            {
                1e0, 1e1, 1e2, 1e3, 1e4, 1e5, 1e6, 1e7, 1e8, 1e9, 1e10, 1e11,
                1e12, 1e13, 1e14, 1e15, 1e16, 1e17, 1e18, 1e19, 1e20, 1e21, 1e22
            };

            unsafe finite f64 ScalarScientific()
            {
                return 1e17;
            }

            unsafe finite f64 Run()
            {
                return Pow10[17] + ScalarScientific();
            }
            """);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvm(result);

        // The array constant must render every element as a hex float, in
        // particular the integral elements (1e0 = double 1) and the scientific
        // ones (1e17 .. 1e22). 1e0 = 0x3FF0..., 1e1 = 0x4024..., 1e17 = 0x4376....
        Assert.Contains(
            "@Pow10 = local_unnamed_addr constant [23 x double] [double 0x3FF0000000000000, double 0x4024000000000000",
            llvm);
        Assert.Contains("double 0x4376345785D8A000", llvm); // 1e17 element
        Assert.Contains("double 0x4480F0CF064DD592", llvm); // 1e22 element (largest)

        // The scalar scientific return must also be a hex float, not `1E+17`.
        Assert.Contains("ret double 0x4376345785D8A000", llvm);

        // No bug-form float literal survives anywhere: a `double`/`float`
        // constant operand that is a bare integer or bare scientific form.
        Assert.False(
            Regex.IsMatch(llvm, @"\b(?:double|float)\s+-?\d+(?:[eE][+-]?\d+)?(?=[\s,\]\)])"),
            $"Emitted IR still contains an invalid bare-integer / scientific float literal:{Environment.NewLine}{llvm}");
    }

    [Fact]
    public void GlobalsAndUnicodeStringConstantsEmitConcreteAlignment()
    {
        var result = Compile(
            """
            module Demo

            struct Pair
            {
                i32[min max] Left;
                i32[min max] Right;
            }

            static mut i32[min max] Counter = 0;
            const Pair Origin = new Pair()
            {
                Left = 1, Right = 2
            };
            const i32[min max][3] Values =
            {
                4, 7, 9
            };

            unsafe finite law unicode Greek()
            {
                return (unicode)"\u03B1";
            }

            unsafe finite i32[min max] Run()
            {
                return Counter + Origin.Right + Values[1];
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("@Counter = global i32 0, align 4", llvm);
        Assert.Contains("@Origin = local_unnamed_addr constant %Pair { i32 1, i32 2 }, align 4", llvm);
        Assert.Contains("@Values = local_unnamed_addr constant [3 x i32] [i32 4, i32 7, i32 9], align 4", llvm);
        Assert.Contains("private unnamed_addr constant [2 x i32] [i32 945, i32 0], align 4", llvm);
    }

    [Fact]
    public void LongTextLiteralDataUsesVectorizationFriendlyAlignment()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn ascii Label()
            {
                return "abcdefghijklmnop";
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("private unnamed_addr constant [17 x i8] c\"abcdefghijklmnop\\00\", align 16", llvm);
    }

    [Fact]
    public void PointerAndViewGlobalsEmitTargetAwareAlignmentOnX86_64()
    {
        var result = Compile(
            """
            module Demo

            static rawptr<i8[min max]> Buffer = null;
            const ascii Label = "Hi";

            unsafe finite i32[min max] Run()
            {
                return 0;
            }
            """,
            new CompilerOptions(
                EmitLlvmIr: true,
                TargetInfo: new LlvmTargetInfo(
                    "x86_64-unknown-linux-gnu",
                    "e-m:e-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-f80:128-n8:16:32:64-S128")));

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("@Buffer = local_unnamed_addr constant ptr null, align 8", llvm);
        Assert.Contains("@Label = local_unnamed_addr constant %stark_ascii", llvm);
        Assert.Contains("i64 2 }, align 8", llvm);
    }

    [Fact]
    public void I386GlobalsUseTargetAwareScalarAndViewAlignment()
    {
        var result = Compile(
            """
            module Demo

            const Bits = 7;
            const Value = 3.5;
            const FloatValue = 3.5f;
            static rawptr<i8[min max]> Buffer = null;
            const ascii Label = "Hi";
            """,
            new CompilerOptions(
                EmitLlvmIr: true,
                TargetInfo: new LlvmTargetInfo(
                    "i386-unknown-linux-gnu",
                    "e-m:e-p:32:32-p270:32:32-p271:32:32-p272:64:64-i128:128-f64:32:64-f80:32-n8:16:32-S128")));

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("@Bits = local_unnamed_addr constant i8 7", llvm);
        // f64/f32 3.5 both emit as the bit-exact hex float 0x400C000000000000.
        Assert.Contains("@Value = local_unnamed_addr constant double 0x400C000000000000, align 4", llvm);
        Assert.Contains("@FloatValue = local_unnamed_addr constant float 0x400C000000000000, align 4", llvm);
        Assert.Contains("@Buffer = local_unnamed_addr constant ptr null, align 4", llvm);
        Assert.Contains("@Label = local_unnamed_addr constant %stark_ascii", llvm);
        Assert.Contains("i64 2 }, align 4", llvm);
    }

    [Fact]
    public void ImmutableGlobalsWithoutAddressTakenEmitLocalUnnamedAddr()
    {
        var result = Compile(
            """
            module Demo

            struct Pair
            {
                i32[min max] Left;
                i32[min max] Right;
            }

            internal const Pair Origin = new Pair()
            {
                Left = 1, Right = 2
            };
            internal static i32[min max][3] Values =
            {
                4, 7, 9
            };

            unsafe finite i32[min max] Run()
            {
                return Origin.Right + Values[1];
            }
            """,
            new CompilerOptions(
                EmitLlvmIr: true,
                QualifyModuleSymbols: true));

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("@Demo_Origin = local_unnamed_addr constant %Pair { i32 1, i32 2 }", llvm);
        Assert.Contains("@Demo_Values = local_unnamed_addr constant [3 x i32] [i32 4, i32 7, i32 9]", llvm);
    }

    [Fact]
    public void ConstArithmeticGlobalsEmitConcreteInitializers()
    {
        var result = Compile(
            """
            module Demo

            const Answer = (1 + 2) * 3;
            const i32[min max][1 + 2] Values =
            {
                4, 7, 9
            };

            unsafe finite i32[min max] Run()
            {
                return Answer + Values[2];
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("@Answer = local_unnamed_addr constant i8 9", llvm);
        Assert.Contains("@Values = local_unnamed_addr constant [3 x i32] [i32 4, i32 7, i32 9]", llvm);
    }

    [Fact]
    public void RecordPrimaryConstructorGlobalsEmitConcreteInitializers()
    {
        var result = Compile(
            """
            module Demo

            record Point(i32[min max] X)
            {
                i32[min max] Y;
            }

            const Point Origin = new Point(3)
            {
                Y = 9
            };

            unsafe finite i32[min max] Run()
            {
                return Origin.Y;
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("%Point = type { i32, i32 }", llvm);
        Assert.Contains("@Origin = local_unnamed_addr constant %Point { i32 3, i32 9 }", llvm);
    }

    [Fact]
    public void ConstFixedArrayGlobalsEmitFrozenDefinitions()
    {
        var result = Compile(
            """
            module Demo

            const i32[min max][3] Values =
            {
                4, 7, 9
            };

            unsafe finite i32[min max] Run()
            {
                return Values[1];
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("@Values = local_unnamed_addr constant [3 x i32] [i32 4, i32 7, i32 9]", llvm);
    }

    [Fact]
    public void NestedConstObjectGraphsEmitConcreteConstantInitializers()
    {
        var result = Compile(
            """
            module Demo

            struct Inner
            {
                i32[min max] Value;
            }

            struct Outer
            {
                Inner Item;
                ascii Label;
            }

            const Outer Graph = new Outer()
            {
                Item = new Inner()
                {
                    Value = 7
                },
                Label = "ok"
            };

            unsafe finite i32[min max] Run()
            {
                return Graph.Item.Value;
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("%stark_ascii = type { ptr, i64 }", llvm);
        Assert.Contains("%Inner = type { i32 }", llvm);
        Assert.Contains("%Outer = type { %Inner, %stark_ascii }", llvm);
        Assert.Contains("@Graph = local_unnamed_addr constant %Outer { %Inner { i32 7 }, %stark_ascii { ptr getelementptr inbounds nuw (", llvm);
        Assert.Contains("i64 2 } }", llvm);
    }

    [Fact]
    public void NestedAggregateLiteralsFoldIntoFrozenGlobalInitializers()
    {
        var result = Compile(
            """
            module Demo

            struct Inner
            {
                i32[min max][2] Pair;
            }

            struct Outer
            {
                Inner Node;
                i32[min max][3] View;
            }

            const Outer Frozen =
            {
                Node =
                {
                    Pair =
                    {
                        4, 7
                    }
                },
                View =
                {
                    1, 2, 3
                }
            };

            unsafe finite i32[min max] Run()
            {
                return Frozen.Node.Pair[1] + Frozen.View[0];
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("%Inner = type { [2 x i32] }", llvm);
        Assert.Contains("%Outer = type { %Inner, [3 x i32] }", llvm);
        Assert.Contains("@Frozen = local_unnamed_addr constant %Outer { %Inner { [2 x i32] [i32 4, i32 7] }, [3 x i32] [i32 1, i32 2, i32 3] }", llvm);
    }

    [Fact]
    public void MutableAggregateGlobalsEmitConcreteInitializersAndStores()
    {
        var result = Compile(
            """
            module Demo

            struct Pair
            {
                i32[min max] Left;
                i32[min max] Right;
            }

            static mut Pair Current = new Pair()
            {
                Left = 5, Right = 8
            };
            static mut i32[min max][3] Values =
            {
                1, 2, 3
            };

            unsafe finite i32[min max] Run()
            {
                Current.Right = 9;
                Values[1] = 7;
                return Current.Right + Values[1];
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("%Pair = type { i32, i32 }", llvm);
        Assert.Contains("@Current = global %Pair { i32 5, i32 8 }", llvm);
        Assert.Contains("@Values = global [3 x i32] [i32 1, i32 2, i32 3]", llvm);
        Assert.Contains("getelementptr inbounds nuw %Pair, ptr @Current, i32 0, i32 1", llvm);
        Assert.Contains("store i32 9", llvm);
        Assert.DoesNotContain("store %Pair", llvm);
        Assert.Contains("ptr @Current", llvm);
        Assert.Contains("getelementptr inbounds nuw [3 x i32], ptr @Values, i32 0, i32 1", llvm);
        Assert.Contains("store i32 7", llvm);
        Assert.Contains("load i32", llvm);
        Assert.DoesNotContain("load [3 x i32]", llvm);
        Assert.DoesNotContain("store [3 x i32]", llvm);
        Assert.Contains("ptr @Values", llvm);
    }

    [Fact]
    public void StaticGenericConstructorInitializersCanForwardAggregateArguments()
    {
        var result = Compile(
            """
            module Demo

            struct Counter
            {
                i32[min max] Value;
            }

            struct Box<T>
            {
                T Value;

                Box(T initial)
                {
                    self.Value = initial;
                }
            }

            static mut Box<Counter> Current = new Box<Counter>(new Counter()
            {
                Value = 42
            });

            unsafe finite i32[min max] Run()
            {
                return Current.Value.Value;
            }
            """);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvm(result);

        Assert.Contains("%Counter = type { i32 }", llvm);
        Assert.Contains("@Current = global %Box_Counter_ { %Counter { i32 42 } }", llvm);
    }

    [Fact]
    public void AggregateArrayFieldsEmitConcreteInitializers()
    {
        var result = Compile(
            """
            module Demo

            struct Buffer
            {
                i32[min max][2] Values;
            }

            static Buffer Shared =
            {
                Values =
                {
                    5, 8
                }
            };

            unsafe finite i32[min max] Run()
            {
                return Shared.Values[1];
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("%Buffer = type { [2 x i32] }", llvm);
        Assert.Contains("@Shared = local_unnamed_addr constant %Buffer { [2 x i32] [i32 5, i32 8] }", llvm);
    }

    [Fact]
    public void ImmutableGlobalLoadsEmitInvariantMetadataThroughFieldAndIndexChains()
    {
        var result = Compile(
            """
            module Demo

            struct Box
            {
                i32[min max] Value;
            }

            static Box Current = new Box()
            {
                Value = 5
            };
            const Box Frozen = new Box()
            {
                Value = 7
            };
            static i32[min max][3] Values =
            {
                1, 2, 3
            };
            static mut i32[min max] Counter = 1;

            unsafe fn i32[min max] ReadStatic()
            {
                return Current.Value;
            }

            unsafe fn i32[min max] ReadConst()
            {
                return Frozen.Value;
            }

            unsafe fn i32[min max] ReadIndex(u8[0 2] index)
            {
                return Values[index];
            }

            unsafe fn i32[min max] ReadMutable()
            {
                return Counter;
            }
            """);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);

        Assert.Matches(@"load %Box, ptr @Current, align 4, !invariant\.load !\d+", llvm);
        Assert.Contains("ret i32 7", ExtractDefinitionBody(llvm, "ReadConst"));
        Assert.DoesNotContain("load %Box, ptr @Frozen", ExtractDefinitionBody(llvm, "ReadConst"));
        Assert.Matches(@"load i32, ptr %v\d+, align 4, !invariant\.load !\d+", llvm);
        Assert.DoesNotContain("load i32, ptr @Counter, !invariant.load", llvm);
    }

    [Fact]
    public void OnceInitializedReadonlyStackStorageEmitsInvariantStartWithoutInvariantLoadMetadata()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn void Observe(rawptr<i32[min max]> ptr)
            {
                stack i32[min max] ignored = *ptr;
                return;
            }

            unsafe fn i32[min max] ReadOnce(i32[min max] input)
            {
                stack i32[min max] value = input;
                stack rawptr<i32[min max]> ptr = &value;
                return *ptr;
            }

            unsafe fn i32[min max] ReadEscaped(i32[min max] input)
            {
                stack i32[min max] escaped = input;
                Observe(&escaped);
                return *(&escaped);
            }

            unsafe fn i32[min max] ReadMutable(i32[min max] input)
            {
                stack mut i32[min max] mutable = input;
                stack rawmutptr<i32[min max]> ptr = &mutable;
                *ptr = input + 1;
                return *ptr;
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);

        Assert.Contains("declare ptr @llvm.invariant.start.p0(i64 immarg, ptr captures(none))", llvm);
        Assert.Matches(@"call ptr @llvm\.invariant\.start\.p0\(i64 4, ptr %slot_value\)", llvm);
        Assert.DoesNotContain("!invariant.load", llvm, StringComparison.Ordinal);
        // The escaped slot is immutable and only escapes through a read-only raw
        // pointer, so the optimizer can prove it invariant as well.
        Assert.Contains("call ptr @llvm.invariant.start.p0(i64 4, ptr %slot_escaped)", llvm);
        Assert.DoesNotContain("call ptr @llvm.invariant.start.p0(i64 4, ptr %slot_mutable)", llvm);

        var mutableDefinitionIndex = llvm.IndexOf("define fastcc noundef i32 @ReadMutable", StringComparison.Ordinal);
        Assert.True(mutableDefinitionIndex >= 0);
        Assert.DoesNotContain("!invariant.load", llvm[mutableDefinitionIndex..]);
    }

    [Fact]
    public void ReadonlyStackDropTempsPassedByAddressDoNotEmitInvariantMetadata()
    {
        var result = Compile(
            """
            module Demo

            struct Box
            {
                i32[min max] Value;

                fn void Clear(mut borrow Box self)
                {
                    self.Value = 0;
                    return;
                }

                mut drop
                {
                    self.Clear();
                }
            }

            fn i32[min max] Read(Box value)
            {
                return value.Value;
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);
        var readBody = ExtractDefinitionBody(llvm, "Read");

        Assert.Contains("call fastcc void @Box_Clear(ptr nonnull noalias writeonly captures(none) dereferenceable(4) align 4 %slot__tmp1_drop)", readBody, StringComparison.Ordinal);
        Assert.DoesNotContain("llvm.invariant.start.p0(i64 4, ptr %slot__tmp1_drop)", readBody, StringComparison.Ordinal);
        Assert.DoesNotContain("!invariant.load", readBody, StringComparison.Ordinal);
    }

    [Fact]
    public void FrozenRawPointerLoadsDoNotEmitInvariantMetadataWithoutConstProvenance()
    {
        var result = Compile(
            """
            module Demo

            struct Big
            {
                i64[min max] A;
                i64[min max] B;
                i64[min max] C;
            }

            unsafe fn i64[min max] Read(rawptr<frozen Big> box)
            {
                return (*box).B;
            }
            """);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);

        Assert.DoesNotMatch(@"load %Big, ptr %arg_box, !invariant\.load !\d+", llvm);
    }

    [Fact]
    public void StaticReadonlyPointerLoadsDoNotConferDeepConstProvenance()
    {
        var result = Compile(
            """
            module Demo

            static rawptr<i32[min max]> Cached = null;

            unsafe fn i32[min max] Read()
            {
                return *Cached;
            }
            """);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);
        var readBody = ExtractDefinitionBody(llvm, "Read");

        Assert.Matches(@"load ptr, ptr @Cached, align \d+, !invariant\.load !\d+", readBody);
        Assert.DoesNotMatch(@"load i32, ptr %v\d+, align \d+, !invariant\.load !\d+", readBody);
    }

    [Fact]
    public void ImmutableRawPointerLocalsDoNotConferDeepConstProvenance()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn i32[min max] Read(rawmutptr<i32[min max]> source)
            {
                stack rawmutptr<i32[min max]> local = source;
                return *local;
            }
            """);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);
        var readBody = ExtractDefinitionBody(llvm, "Read");

        Assert.DoesNotMatch(@"load i32, ptr %v\d+, align \d+, !invariant\.load !\d+", readBody);
    }

    [Fact]
    public void ReadonlyRawPointerLocalsDoNotConferDeepConstProvenance()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn i32[min max] Read(rawmutptr<i32[min max]> source)
            {
                stack rawptr<i32[min max]> local = (rawptr<i32[min max]>)source;
                return *local;
            }
            """);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);
        var readBody = ExtractDefinitionBody(llvm, "Read");

        Assert.DoesNotMatch(@"load i32, ptr %v\d+, align \d+, !invariant\.load !\d+", readBody);
    }

    [Fact]
    public void IntegerLaunderedMutablePointersDoNotEmitInvariantMetadata()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn i32[min max] Read()
            {
                stack mut i32[min max] value = 7;
                stack rawmutptr<i32[min max]> ptr = &value;
                stack i64[min max] address = (i64[min max])ptr;
                stack rawmutptr<i32[min max]> roundTrip = (rawmutptr<i32[min max]>)address;
                return *roundTrip;
            }
            """);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);
        var readBody = ExtractDefinitionBody(llvm, "Read");

        Assert.Contains("ptrtoint", readBody, StringComparison.Ordinal);
        Assert.Contains("inttoptr", readBody, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"load i32, ptr %v\d+, align \d+, !invariant\.load !\d+", readBody);
    }

    [Fact]
    public void TypedScalarLoadsAndStoresEmitConservativeStarkTbaa()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn u8[0 10] Run(u8[0 9] input)
            {
                stack mut u8[0 10] value = input;
                return *(&value);
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);

        Assert.Contains("!\"Stark TBAA\"", llvm);
        Assert.Contains("!\"stark.i8\"", llvm);
        Assert.Matches(@"store i8 [^,]+, ptr %slot_value, !tbaa !\d+", llvm);
        Assert.Matches(@"load i8, ptr (?:%v\d+|%slot_value), !range !\d+, !tbaa !\d+", llvm);
    }

    [Fact]
    public void TypedFieldAndFixedArrayElementLoadsEmitStructPathTbaa()
    {
        var result = Compile(
            """
            module Demo

            struct Buffer
            {
                u8[0 100] Header;
                u8[0 100][4] Values;
            }

            unsafe fn u8[0 100] ReadHeader(borrow Buffer buffer)
            {
                return *(&buffer.Header);
            }

            unsafe fn u8[0 100] ReadElement(borrow Buffer buffer)
            {
                return *(&buffer.Values[2]);
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);

        Assert.Contains("!\"Stark TBAA\"", llvm);
        Assert.Contains("!\"stark.Buffer\"", llvm);
        Assert.Contains("!\"stark.array.u8[0 100][4]\"", llvm);
        Assert.Matches(@"load i8, ptr %v\d+, !range !\d+, !tbaa !\d+", llvm);
        Assert.Matches(@"!\d+ = !\{!\d+, !\d+, i64 0\}", llvm);
        Assert.Matches(@"!\d+ = !\{!\d+, !\d+, i64 3\}", llvm);
    }

    [Fact]
    public void RawPointerAndPointerIntegerEscapesSuppressTbaa()
    {
        var rawPointerResult = Compile(
            """
            module Demo

            unsafe fn u8[0 10] Read(rawptr<u8[0 10]> ptr)
            {
                return *ptr;
            }
            """,
            options: new CompilerOptions());

        Assert.True(rawPointerResult.Succeeded, string.Join(Environment.NewLine, rawPointerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var rawPointerLlvm = GetLlvmRaw(rawPointerResult);

        Assert.DoesNotContain("!tbaa", rawPointerLlvm);
        Assert.DoesNotContain("!\"Stark TBAA\"", rawPointerLlvm);

        var rawCastResult = Compile(
            """
            module Demo

            unsafe fn u8[0 10] ReadCast()
            {
                stack mut u8[0 10] value = 1;
                stack rawmutptr<u8[0 10]> mutablePtr = &value;
                stack rawptr<u8[0 10]> readonlyPtr = (rawptr<u8[0 10]>)mutablePtr;
                return *readonlyPtr;
            }
            """,
            options: new CompilerOptions());

        Assert.True(rawCastResult.Succeeded, string.Join(Environment.NewLine, rawCastResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var rawCastLlvm = GetLlvmRaw(rawCastResult);

        Assert.Contains("load i8, ptr %slot_value", rawCastLlvm);
        Assert.DoesNotContain("ptrtoint", rawCastLlvm);
        Assert.DoesNotContain("inttoptr", rawCastLlvm);
        Assert.DoesNotContain("!tbaa", rawCastLlvm);

        var escapedResult = Compile(
            """
            module Demo

            unsafe fn u8[0 10] ReadEscaped()
            {
                stack mut u8[0 10] value = 1;
                stack rawmutptr<u8[0 10]> ptr = &value;
                if ((i64[min max])ptr == 0)
                {
                    return 0;
                }

                return value;
            }
            """,
            options: new CompilerOptions());

        Assert.True(escapedResult.Succeeded, string.Join(Environment.NewLine, escapedResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var escapedLlvm = GetLlvmRaw(escapedResult);

        Assert.Contains("ptrtoint", escapedLlvm);
        Assert.DoesNotContain("!tbaa", escapedLlvm);
    }

    [Fact]
    public void ExclusiveBorrowMemoryAccessesEmitScopedNoAliasMetadata()
    {
        var result = Compile(
            """
            module Demo

            struct Box
            {
                u8[0 100] Value;
            }

            unsafe fn u8[0 100] Sum(borrow mut Box left, borrow mut Box right)
            {
                left.Value = 7;
                return right.Value;
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);

        Assert.Contains("!\"stark.noalias.Sum\"", llvm);
        Assert.Matches(@"store i8 .* ptr %v\d+, !tbaa !\d+, !alias\.scope !\d+, !noalias !\d+", llvm);
        Assert.Matches(@"load i8, ptr %v\d+, .* !alias\.scope !\d+, !noalias !\d+", llvm);
        Assert.Matches(@"!\d+ = distinct !\{!\d+, !""stark\.noalias\.Sum""\}", llvm);
        Assert.Matches(@"!\d+ = distinct !\{!\d+, !\d+, !""stark\.noalias\.Sum\.param\.left""\}", llvm);
        Assert.Matches(@"!\d+ = distinct !\{!\d+, !\d+, !""stark\.noalias\.Sum\.param\.right""\}", llvm);
    }

    [Fact]
    public void SameRawPointerParametersShareAliasScopeAgainstDefaultDisjointParameters()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn i32[min max] Check(
                rawmutptr<i32[min max]> left,
                rawmutptr<i32[min max]> mirror,
                rawmutptr<i32[min max]> other)
                where same(left, mirror)
                {
                    *left = 7;
                *mirror = 9;
                return *other;
            }
            """,
            options: new CompilerOptions(EmitLlvmIr: true));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);
        var header = ExtractDefinitionHeader(llvm, "Check");

        Assert.DoesNotMatch(@"noalias [^)]*%arg_left", header);
        Assert.DoesNotMatch(@"noalias [^)]*%arg_mirror", header);
        Assert.Contains("noalias", header, StringComparison.Ordinal);
        Assert.Contains("declare void @llvm.assume(i1 noundef)", llvm, StringComparison.Ordinal);
        Assert.Matches(@"call void @llvm\.assume\(i1 %abi_same_ptr_\d+\)", llvm);
        Assert.Contains("!\"stark.noalias.Check.same.param.left.param.mirror\"", llvm, StringComparison.Ordinal);
        Assert.Contains("!\"stark.noalias.Check.param.other\"", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("!\"stark.noalias.Check.param.left\"", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("!\"stark.noalias.Check.param.mirror\"", llvm, StringComparison.Ordinal);
        Assert.True(Regex.Matches(llvm, @"store i32 .* !alias\.scope !\d+, !noalias !\d+").Count >= 2, llvm);
        Assert.Matches(@"load i32, ptr .* !alias\.scope !\d+, !noalias !\d+", llvm);
    }

    [Fact]
    public void SameSliceParametersEmitDataAndLengthAssumes()
    {
        var result = Compile(
            """
            module Demo

            struct Cell
            {
                u8[0 100] Value;
            }

            fn u8[0 200] Check(
                Cell[] view,
                Cell[] backing,
                Cell[] other)
                where same(view, backing)
                {
                    return view[0].Value + backing[0].Value + other[0].Value;
            }
            """,
            options: new CompilerOptions(EmitLlvmIr: true));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);

        Assert.Contains("declare void @llvm.assume(i1 noundef)", llvm, StringComparison.Ordinal);
        Assert.Matches(@"%abi_same_data_\d+ = icmp eq ptr %abi_same_left_data_\d+, %abi_same_right_data_\d+", llvm);
        Assert.Matches(@"%abi_same_len_\d+ = icmp eq i64 %abi_same_left_len_\d+, %abi_same_right_len_\d+", llvm);
        Assert.Matches(@"call void @llvm\.assume\(i1 %abi_same_data_\d+\)", llvm);
        Assert.Matches(@"call void @llvm\.assume\(i1 %abi_same_len_\d+\)", llvm);
        Assert.Contains("!\"stark.noalias.Check.same.param.backing.param.view\"", llvm, StringComparison.Ordinal);
        Assert.Contains("!\"stark.noalias.Check.param.other\"", llvm, StringComparison.Ordinal);
    }

    [Fact]
    public void DynamicLocalStorageAccessesEmitScopedNoAliasMetadata()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn u64[0 2 ** 63 - 1] Run()
            {
                stack mut dynamic u64[0 2 ** 63 - 1] left = new(2);
                stack mut dynamic u64[0 2 ** 63 - 1] right = new(2);
                init left[0] = 10;
                init right[0] = 20;
                return left[0] + right[0];
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);

        Assert.Contains("!\"stark.noalias.Run.dynamic.left\"", llvm);
        Assert.Contains("!\"stark.noalias.Run.dynamic.right\"", llvm);
        Assert.Contains("\"separate_storage\"", llvm, StringComparison.Ordinal);
        Assert.Matches(
            @"call void @llvm\.assume\(i1 true\) \[""separate_storage""\(ptr %[^,]+, ptr %[^)]+\)\]",
            llvm);
        Assert.Matches(@"store i64 .* ptr %v\d+, align 8, !alias\.scope !\d+, !noalias !\d+", llvm);
        Assert.Matches(@"load i64, ptr %v\d+, align 8, !range !\d+, !alias\.scope !\d+, !noalias !\d+", llvm);
    }

    [Fact]
    public void RawPointerMemoryAccessesDoNotEmitScopedNoAliasMetadata()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn u8[0 100] Read(rawptr<u8[0 100]> left, rawptr<u8[0 100]> right)
                where overlap(left, right)
                {
                    return *left + *right;
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);

        Assert.DoesNotContain("!alias.scope", llvm);
        Assert.DoesNotContain("!noalias", llvm);
        Assert.DoesNotContain("!\"stark.noalias", llvm);
        Assert.DoesNotContain("\"separate_storage\"", llvm, StringComparison.Ordinal);
    }

    [Fact]
    public void OverlappingConstViewsDoNotInferNoAliasFromConstness()
    {
        var result = Compile(
            """
            module Demo

            fn i32[min max] Read(const i32[min max][] left, const i32[min max][] right)
                where overlap(left, right)
                {
                    return left[0] + right[0];
            }
            """,
            options: new CompilerOptions(EmitLlvmIr: true));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);
        var header = ExtractDefinitionHeader(llvm, "Read");

        Assert.DoesNotContain("noalias", header, StringComparison.Ordinal);
        Assert.DoesNotContain("!noalias", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("!\"stark.noalias.Read", llvm, StringComparison.Ordinal);
    }

    [Fact]
    public void RawPointerEscapesSuppressScopedNoAliasMetadataForAffectedAccesses()
    {
        var result = Compile(
            """
            module Demo

            public unsafe ffi fn void Capture(rawmutptr<i32[min max]> ptr);

            unsafe fn i32[min max] Run(rawmutptr<i32[min max]> left, rawmutptr<i32[min max]> right)
            {
                Capture(left);
                *left = 7;
                return *right;
            }
            """,
            options: new CompilerOptions(EmitLlvmIr: true));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);

        Assert.DoesNotContain("!alias.scope", llvm);
        Assert.DoesNotContain("!noalias", llvm);
        Assert.DoesNotContain("!\"stark.noalias.Run", llvm);
    }

    [Fact]
    public void IntegerLaunderedRawPointersSuppressScopedNoAliasMetadataForAffectedAccesses()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn i32[min max] Run(rawmutptr<i32[min max]> left, rawmutptr<i32[min max]> right)
            {
                stack u64[min max] bits = (u64[min max])left;
                stack rawmutptr<i32[min max]> hidden = (rawmutptr<i32[min max]>)bits;
                *hidden = 7;
                return *right;
            }
            """,
            options: new CompilerOptions(EmitLlvmIr: true));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);

        Assert.DoesNotContain("!alias.scope", llvm);
        Assert.DoesNotContain("!noalias", llvm);
        Assert.DoesNotContain("!\"stark.noalias.Run", llvm);
    }

    [Fact]
    public void RawPointerConstNullGlobalsRemainExternalPlaceholders()
    {
        var result = Compile(
            """
            module Demo

            const rawptr<i8[min max]> stdout = null;

            unsafe finite law i32[min max] Run()
            {
                return 0;
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("@stdout = external constant ptr", llvm);
    }

    [Fact]
    public void BitwiseXorEmitsConcreteLlvmXorInstruction()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn i32[min max] Run(i32[min max] left, i32[min max] right)
            {
                return left ^ right;
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("define fastcc i32 @Run(i32 %arg_left, i32 %arg_right)", llvm);
        Assert.Contains("xor i32", llvm);
    }

    [Fact]
    public void BitwiseAndShiftExpressionsEmitConcreteLlvmInstructions()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn i32[min max] Run(i32[min max] left, i32[min max] middle, i32[min max] right, i32[min max] mask)
            {
                return left | middle ^ right & mask << 1 >> 1;
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("shl i32", llvm);
        Assert.Contains("ashr exact i32", llvm);
        Assert.Contains("and i32", llvm);
        Assert.Contains("xor i32", llvm);
        Assert.Contains("or i32", llvm);
    }

    [Fact]
    public void IntegerAndBoolUnaryOperatorsEmitConcreteLlvmInstructions()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn i32[min max] Negate(i32[min max] value)
            {
                return -value;
            }

            unsafe fn bool LogicalNot(bool value)
            {
                return !value;
            }

            unsafe fn i32[min max] BitwiseNot(i32[min max] value)
            {
                return ~value;
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);

        Assert.Contains("sub i32 0, %arg_value", llvm);
        Assert.Contains("xor i1 %arg_value, true", llvm);
        Assert.Contains("xor i32 %arg_value, -1", llvm);
        Assert.DoesNotContain("; LLVM body emission fallback", llvm);
    }

    [Fact]
    public void OrdinaryIntegerArithmeticEmitsSignedNoWrapFlags()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn i32[min max] Add(i32[min max] left, i32[min max] right)
            {
                return left + right;
            }

            unsafe fn i32[min max] Sub(i32[min max] left, i32[min max] right)
            {
                return left - right;
            }

            unsafe fn i32[min max] Mul(i32[min max] left, i32[min max] right)
            {
                return left * right;
            }
            """);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvm(result);

        Assert.Contains("add nsw i32", llvm);
        Assert.Contains("sub nsw i32", llvm);
        Assert.Contains("mul nsw i32", llvm);
        Assert.DoesNotContain("add nuw", llvm);
        Assert.DoesNotContain("sub nuw", llvm);
        Assert.DoesNotContain("mul nuw", llvm);
    }

    [Fact]
    public void UnconstrainedUnsignedOrdinaryArithmeticEmitsUnsignedNoWrapByContract()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn u32[0 max] Add(u32[0 max] left, u32[0 max] right)
            {
                return left + right;
            }

            unsafe fn u32[0 max] Sub(u32[0 max] left, u32[0 max] right)
            {
                return left - right;
            }

            unsafe fn u32[0 max] Mul(u32[0 max] left, u32[0 max] right)
            {
                return left * right;
            }
            """);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvm(result);

        Assert.Contains("add nuw i32", llvm);
        Assert.Contains("sub nuw i32", llvm);
        Assert.Contains("mul nuw i32", llvm);
        Assert.DoesNotContain("add nuw nsw i32", llvm);
        Assert.DoesNotContain("sub nuw nsw i32", llvm);
        Assert.DoesNotContain("mul nuw nsw i32", llvm);
    }

    [Fact]
    public void NonNegativeConstrainedOrdinaryArithmeticEmitsUnsignedNoWrapWhenProven()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn i32[min max] Add(u8[0 10] left, u8[0 20] right)
            {
                return left + right;
            }

            unsafe fn i32[min max] Sub(u8[20 30] left, u8[0 10] right)
            {
                return left - right;
            }

            unsafe fn i32[min max] Mul(u8[0 10] left, u8[0 5] right)
            {
                return left * right;
            }
            """);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvm(result);

        Assert.Contains("add nuw nsw i8", llvm);
        Assert.Contains("sub nuw nsw i8", llvm);
        Assert.Contains("mul nuw nsw i32", llvm);
    }

    [Fact]
    public void UnsignedOrdinaryArithmeticDoesNotInventSignedNoWrapForHighBitResults()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn u32[0 max] AddIntoHighBit(u32[2147483647 2147483647] left)
            {
                return left + (u32[0 max])1;
            }
            """);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvm(result);

        Assert.Contains("add nuw i32", llvm);
        Assert.DoesNotContain("add nuw nsw i32", llvm);
        Assert.DoesNotContain("add nsw i32", llvm);
    }

    [Fact]
    public void PropagatedValueFactsEmitUnsignedNoWrapForJoinedRanges()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn i32[min max] AddAfterJoin(bool flag, u8[0 10] left, u8[20 30] right)
            {
                stack mut i32[min max] value = left;
                if (flag)
                {
                    value = right;
                }

                return value + 1;
            }
            """);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvm(result);

        Assert.Contains("add nuw nsw i32", llvm);
    }

    [Fact]
    public void PropagatedValueFactsEmitNarrowerReturnRangeAttributes()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn i32[min max] Mask(u8[0 15] value)
            {
                return value & 7;
            }
            """);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvm(result);

        Assert.Contains("define fastcc range(i32 0, 8) i32 @Mask(i8 range(i8 0, 16) %arg_value)", llvm);
    }

    [Fact]
    public void SameWidthSignednessChangingIntegerConversionsTranslateReturnRangeFacts()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn i32[min max] Full(u32[0 max] value)
            {
                return (i32[min max])value;
            }

            unsafe fn i32[min max] Low(u32[0 2147483647] value)
            {
                return (i32[min max])value;
            }

            unsafe fn i32[min max] High(u32[2147483648 max] value)
            {
                return (i32[min max])value;
            }

            unsafe fn u32[0 max] NegativeToUnsigned(i32[min -1] value)
            {
                return (u32[0 max])value;
            }
            """);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvm(result);

        Assert.Contains("define fastcc i32 @Full(i32 %arg_value)", llvm);
        Assert.DoesNotContain("define fastcc range(i32 0, -2147483648) i32 @Full", llvm);
        Assert.Contains("define fastcc range(i32 0, -2147483648) i32 @Low(i32 range(i32 0, -2147483648) %arg_value)", llvm);
        Assert.Contains("define fastcc range(i32 -2147483648, 0) i32 @High(i32 range(i32 -2147483648, 0) %arg_value)", llvm);
        Assert.Contains("define fastcc range(i32 -2147483648, 0) i32 @NegativeToUnsigned(i32 range(i32 -2147483648, 0) %arg_value)", llvm);
    }

    [Fact]
    public void ProvenNonNegativeSignedDivisionAndModuloUseUnsignedLlvmOpcodes()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn i32[min max] DivideMasked(i32[min max] value, u8[1 10] divisor)
            {
                return (value & 255) / divisor;
            }

            unsafe fn i32[min max] ModuloMasked(i32[min max] value, u8[1 10] divisor)
            {
                return (value & 255) % divisor;
            }

            unsafe fn bool LessMasked(i32[min max] value, u8[0 max] threshold)
            {
                return (value & 255) < threshold;
            }

            unsafe fn bool GreaterMasked(i32[min max] value, u8[0 max] threshold)
            {
                return (value & 255) > threshold;
            }
            """);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvm(result);

        Assert.Contains("udiv i32", llvm);
        Assert.Contains("urem i32", llvm);
        Assert.Contains("icmp ult i32", llvm);
        Assert.Contains("icmp ugt i32", llvm);
        Assert.Contains("define fastcc range(i32 0, 256) i32 @DivideMasked", llvm);
        Assert.Contains("define fastcc range(i32 0, 10) i32 @ModuloMasked", llvm);
        Assert.DoesNotContain("sdiv i32", llvm);
        Assert.DoesNotContain("srem i32", llvm);
        Assert.DoesNotContain("icmp slt i32", llvm);
        Assert.DoesNotContain("icmp sgt i32", llvm);
    }

    [Fact]
    public void OrdinaryIntegerArithmeticDoesNotEmitUnsignedNoWrapWhenNegativeValuesArePossible()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn i32[min max] Add(i8[-1 10] left, u8[0 20] right)
            {
                return left + right;
            }
            """);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvm(result);

        Assert.Contains("add nsw i8", llvm);
        Assert.DoesNotContain("add nuw", llvm);
    }

    [Fact]
    public void ProvenShiftLeftRangesEmitNoWrapFlags()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn i32[min max] Both(u32[0 2 ** 30 - 1] value)
            {
                return value << 1;
            }

            unsafe fn i32[min max] UnsignedOnly(u32[0 2 ** 31 - 1] value)
            {
                return value << 1;
            }

            unsafe fn i32[min max] SignedOnly(i8[-4 -1] value)
            {
                return value << 1;
            }
            """);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvm(result);

        Assert.Contains("shl nuw nsw i32", llvm);
        Assert.Contains("shl nuw i32", llvm);
        Assert.Contains("shl nsw i32", llvm);
    }

    [Fact]
    public void UnsignedIntegerOperationsUseUnsignedLlvmOpcodes()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn u64[0 max] Divide(u64[0 max] left, u64[0 max] right)
            {
                return left / right;
            }

            unsafe fn u64[0 max] Remainder(u64[0 max] left, u64[0 max] right)
            {
                return left % right;
            }

            unsafe fn bool Greater(u64[0 max] left, u64[0 max] right)
            {
                return left > right;
            }

            unsafe fn u64[0 max] ShiftRight(u64[0 max] value)
            {
                return value >> 1;
            }

            unsafe fn u64[0 max] Widen(u32[0 max] value)
            {
                return (u64[0 max])value;
            }

            unsafe fn f64 ToFloat(u64[0 max] value)
            {
                return (f64)value;
            }

            unsafe fn u64[0 max] FromFloat(f64 value)
            {
                return (u64[0 max])value;
            }
            """);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvm(result);

        Assert.Contains("udiv i64", llvm);
        Assert.Contains("urem i64", llvm);
        Assert.Contains("icmp ugt i64", llvm);
        Assert.Contains("lshr i64", llvm);
        Assert.Contains("zext i32", llvm);
        Assert.Contains("uitofp i64", llvm);
        Assert.Contains("fptoui double", llvm);
    }

    [Fact]
    public void ShiftRightAndSignedDivisionEmitExactWhenDivisibilityIsProven()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn i32[min max] ShiftBack(i32[-536870912 536870911] value)
            {
                return (value << 2) >> 1;
            }

            unsafe fn i32[min max] DivideProduct(i16[-1000 1000] value)
            {
                return (value * 6) / 3;
            }

            unsafe fn i32[min max] DivideShift(i16[-1000 1000] value)
            {
                return (value << 3) / 8;
            }
            """);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvm(result);

        Assert.Contains("ashr exact i32", llvm);
        Assert.Equal(2, CountOccurrences(llvm, "sdiv exact i32"));
    }

    [Fact]
    public void ShiftAndDivisionFlagsAreOmittedWhenProofFactsAreInsufficient()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn i32[min max] ShiftLeft(i32[min max] value)
            {
                return value << 1;
            }

            unsafe fn i32[min max] ShiftRight(i32[min max] value)
            {
                return value >> 1;
            }

            unsafe fn i32[min max] Divide(i32[min max] value, u8[2 2] divisor)
            {
                return value / divisor;
            }

            unsafe fn i32[min max] Remainder(i32[min max] value, u8[2 2] divisor)
            {
                return value % divisor;
            }
            """);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvm(result);

        Assert.Contains("shl i32", llvm);
        Assert.Contains("ashr i32", llvm);
        Assert.Contains("sdiv i32", llvm);
        Assert.Contains("srem i32", llvm);
        Assert.DoesNotContain("shl nuw", llvm);
        Assert.DoesNotContain("shl nsw", llvm);
        Assert.DoesNotContain("ashr exact", llvm);
        Assert.DoesNotContain("sdiv exact", llvm);
    }

    [Fact]
    public void WrappingArithmeticEmitsConcreteLlvmInstructions()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn i32[min max] Run(i32[min max] left, i32[min max] right)
            {
                return -%left +% right *% 2 -% right;
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("sub i32 0, %arg_left", llvm);
        Assert.Contains("mul i32", llvm);
        Assert.Contains("add i32", llvm);
        Assert.DoesNotContain("sub nsw", llvm);
        Assert.DoesNotContain("sub nuw", llvm);
        Assert.DoesNotContain("mul nsw", llvm);
        Assert.DoesNotContain("mul nuw", llvm);
        Assert.DoesNotContain("add nsw", llvm);
        Assert.DoesNotContain("add nuw", llvm);
    }

    [Fact]
    public void SaturatingArithmeticEmitsWideClampSequence()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn i32[min max] Run(i32[min max] left, i32[min max] right)
            {
                return left +| right *| 2 -| 1;
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("sext i32", llvm);
        Assert.Contains("mul i64", llvm);
        Assert.Contains("add i64", llvm);
        Assert.Contains("sub i64", llvm);
        Assert.Contains("icmp sgt i64", llvm);
        Assert.Contains("icmp slt i64", llvm);
        Assert.Contains("select i1", llvm);
        Assert.Contains("trunc i64", llvm);
        Assert.DoesNotContain("add nsw i64", llvm);
        Assert.DoesNotContain("add nuw i64", llvm);
        Assert.DoesNotContain("sub nsw i64", llvm);
        Assert.DoesNotContain("sub nuw i64", llvm);
        Assert.DoesNotContain("mul nsw i64", llvm);
        Assert.DoesNotContain("mul nuw i64", llvm);
    }

    [Fact]
    public void ExplicitWrappingAndSaturatingArithmeticStayFlagFreeEvenWhenOrdinaryProofsExist()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn i32[min max] Wrap(u8[0 10] left, u8[0 5] right)
            {
                return left +% right *% 2 -% 1;
            }

            unsafe fn i32[min max] Sat(u8[0 10] left, u8[0 5] right)
            {
                return left +| right *| 2 -| 1;
            }
            """);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvm(result);

        Assert.Contains("mul i8", llvm);
        Assert.Contains("add i8", llvm);
        Assert.Contains("sub i8", llvm);
        Assert.Contains("mul i16", llvm);
        Assert.Contains("add i16", llvm);
        Assert.Contains("sub i16", llvm);
        Assert.DoesNotContain("nuw", llvm);
        Assert.DoesNotContain("nsw", llvm);
        Assert.DoesNotContain("exact", llvm);
    }

    [Fact]
    public void ExplicitIntegerArithmeticConstantsFoldBeforeLlvmEmission()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn i32[min max] Wrap()
            {
                return 2147483647 +% 1;
            }

            unsafe fn i32[min max] SatAdd()
            {
                return 2147483647 +| 1;
            }

            unsafe fn i32[min max] SatMul()
            {
                return 1073741824 *| 4;
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("define fastcc i32 @Wrap()", llvm);
        Assert.Contains("ret i32 -2147483648", llvm);
        Assert.Contains("define fastcc i32 @SatAdd()", llvm);
        Assert.Contains("ret i32 2147483647", llvm);
        Assert.Contains("define fastcc i32 @SatMul()", llvm);
        Assert.Equal(2, llvm.Split('\n').Count(static line => line.Contains("ret i32 2147483647", StringComparison.Ordinal)));
    }

    [Fact]
    public void ConstantFloatExponentExpressionsFoldBeforeLlvmEmission()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn f32 Run()
            {
                return 2.0 ** 3.0;
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("define fastcc float @Run()", llvm);
        // 2.0 ** 3.0 folds to 8.0; f32 8.0 emits as a bit-exact hex float.
        Assert.Contains("ret float 0x4020000000000000", llvm);
        Assert.DoesNotContain("@llvm.pow.f32", llvm);
    }

    [Fact]
    public void IntegerExponentExpressionsEmitInternalPowHelpers()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn i32[min max] Run(i32[min max] left, i32[min max] right)
            {
                return left ** right;
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        var helperHeader = ExtractDefinitionHeader(llvm, "__stark_int_pow_i32");
        Assert.Contains("define internal dso_local i32 @__stark_int_pow_i32(i32 %base, i32 %exponent)", helperHeader);
        Assert.Contains("unnamed_addr", helperHeader);
        Assert.Contains("call i32 @__stark_int_pow_i32(", llvm);
        Assert.Contains("select i1 %pow_is_odd", llvm);
        Assert.Contains("lshr i32 %pow_exp, 1", llvm);
        Assert.DoesNotContain("@llvm.pow.i32", llvm);
    }

    [Fact]
    public void SmallConstantIntegerExponentExpressionsLowerToStraightLineMultiplies()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn i32[min max] Run(i32[min max] value)
            {
                return value ** 4;
            }
            """);

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvm(result);
        var runBody = ExtractDefinitionBody(llvm, "Run");

        Assert.Equal(3, Regex.Matches(runBody, @"\bmul\b").Count);
        Assert.DoesNotContain("@__stark_int_pow_i32", llvm, StringComparison.Ordinal);
    }

    [Fact]
    public void FloatLiteralArgumentsEmitValidHexFloatConstants()
    {
        var result = Compile(
            """
            module Demo

            noinline unsafe fn f64 Echo(f64 value)
            {
                return value;
            }

            unsafe fn i32[min max] Run()
            {
                if (Echo(0.0) != 0.0)
                {
                    return 1;
                }

                if (Echo(3.0) != 3.0)
                {
                    return 2;
                }

                return 0;
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        // f64 0.0 and 3.0 emit as bit-exact hex floats (0.0 = all-zero bits,
        // 3.0 = 0x4008...). The old bare-integer form (`double 0`, `double 3`)
        // is invalid LLVM IR.
        Assert.Contains("call fast fastcc double @Echo(double 0x0000000000000000)", llvm);
        Assert.Contains("fcmp fast one double %", llvm);
        Assert.Contains(", 0x0000000000000000", llvm);
        Assert.Contains("call fast fastcc double @Echo(double 0x4008000000000000)", llvm);
        Assert.Contains(", 0x4008000000000000", llvm);
        Assert.DoesNotContain("call fast fastcc double @Echo(double 0)", llvm);
        Assert.DoesNotContain("call fast fastcc double @Echo(double 3)", llvm);
        Assert.DoesNotContain("double 3.0", llvm);
    }

    [Fact]
    public void LoopHeaderEmitsBackedgePhi()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn i32[min max] Run()
            {
                stack mut i32[min max] i = 0;
                while willexit (i < 4)
                {
                    i = i + 1;
                }

                return i;
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("phi i32", llvm);
        Assert.Contains("%bb2", llvm);
        Assert.Contains("icmp slt i32", llvm);
    }

    [Fact]
    public void ConstantLiteralSwitchesFoldBeforeLlvmEmission()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn i32[min max] Run()
            {
                stack i32[min max] value = 1;
                switch (value)
                {
                    case 1:
                        return 1;
                    default:
                        return 2;
                }
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("define fastcc i32 @Run()", llvm);
        Assert.Contains("ret i32 1", llvm);
        Assert.DoesNotContain("switch i32", llvm);
        Assert.DoesNotContain("icmp eq i32", llvm);
        Assert.DoesNotContain("declare fastcc i32 @Run()", llvm);
    }

    [Fact]
    public void GuardedSwitchBodyEmitsCompareAndGuardBranches()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn i32[min max] Run(i32[min max] value, bool allow)
            {
                switch (value)
                {
                    case 1 when allow:
                        return 1;
                    default:
                        return 2;
                }
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("@Run(i32", llvm);
        Assert.Contains("%arg_value, i1", llvm);
        Assert.Contains("%arg_allow)", llvm);
        Assert.Contains("icmp eq i32", llvm);
        Assert.Contains("select i1 %arg_allow, i32 1, i32 2", llvm);
        Assert.DoesNotContain("declare fastcc i32 @Run(i32, i1)", llvm);
    }

    [Fact]
    public void EnumSwitchExpressionCallEmitsSingleEvaluation()
    {
        var result = Compile(
            """
            module Demo

            enum Status
            {
                Ok,
                Err(i32[min max]),
            }

            unsafe fn Status Next()
            {
                stack mut i32[min max] acc = 977;
                stack mut i32[min max] step = 0;
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

                if (acc < 0)
                {
                    return Status.Err(acc);
                }

                return Status.Ok;
            }

            unsafe fn i32[min max] Run()
            {
                switch (Next())
                {
                    case Status.Ok:
                        return 1;
                    case Status.Err(var error):
                        return error;
                }
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Equal(1, llvm.Split("call fastcc %Status @Next()", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void LargeEnumSwitchLoadsTagWithRangeMetadataAndZeroTagsUnitCase()
    {
        var result = Compile(
            """
            module Demo

            enum Token
            {
                Full(i32[min max], i32[min max], i32[min max], i32[min max], i32[min max]),
                Empty,
            }

            unsafe fn Token MakeEmpty()
            {
                return Token.Empty;
            }

            unsafe fn Token MakeFull(i32[min max] value)
            {
                return Token.Full(value, value, value, value, value);
            }

            unsafe fn i32[min max] Score(Token token)
            {
                switch (token)
                {
                    case Token.Empty:
                        return 0;
                    case Token.Full(var first, _, _, _, _):
                        return first;
                }
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);

        Assert.Contains("%Token = type { i8, i32, i32, i32, i32, i32 }", llvm);
        // The unit case's all-zero payload (tag 0 included) now materializes as a
        // zeroinitializing memset instead of per-field insertvalues.
        Assert.Contains("call void @llvm.memset.inline.p0.i64(ptr align 4 %ret, i8 0, i64 24, i1 false)", llvm);
        Assert.Contains("insertvalue %Token zeroinitializer, i8 1, 0", llvm);
        Assert.Contains("!{i8 0, i8 2}", llvm);
        Assert.Matches(@"load i8, ptr %[A-Za-z0-9_]+, !range !\d+", llvm);
        Assert.DoesNotContain("load %Token, ptr %arg_token", llvm);
    }

    [Fact]
    public void CaptureSwitchPatternEmitsConcreteBody()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn i32[min max] Run(i32[min max] value, bool allow)
            {
                switch (value)
                {
                    case var capture when allow:
                        return capture;
                    default:
                        return 0;
                }
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("@Run(i32", llvm);
        Assert.Contains("%arg_value, i1", llvm);
        Assert.Contains("%arg_allow)", llvm);
        Assert.Contains("select i1 %arg_allow, i32 %arg_value, i32 0", llvm);
        Assert.Contains("ret i32 %select_", llvm);
        Assert.DoesNotContain("declare fastcc i32 @Run(i32, i1)", llvm);
    }

    [Fact]
    public void MultiLabelGuardedSwitchEmitsDecisionTreeBody()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn i32[min max] Run(i32[min max] value, bool allow)
            {
                switch (value)
                {
                    case 1:
                    case 2 when allow:
                        return 10;
                    default:
                        return 20;
                }
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Matches(@"define fastcc .* i32 @Run\(i32 .*%arg_value, i1 .*%arg_allow\)", llvm);
        Assert.True(CountOccurrences(llvm, "icmp eq i32") >= 2);
        Assert.Contains("select i1 %arg_allow", llvm);
        Assert.DoesNotContain("switch i32", llvm);
        Assert.DoesNotContain("declare fastcc i32 @Run(i32, i1)", llvm);
    }

    [Fact]
    public void OrPatternLiteralSwitchPreservesNativeSwitchLowering()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn i32[min max] Run(u8[0 3] value)
            {
                switch (value)
                {
                    case 0 | 2:
                        return 10;
                    case 1 | 3:
                        return 20;
                }
            }
            """,
            options: new CompilerOptions(EmitLlvmIr: true));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);

        Assert.Contains("switch i8 %arg_value", llvm, StringComparison.Ordinal);
        Assert.Contains("i8 0, label", llvm, StringComparison.Ordinal);
        Assert.Contains("i8 1, label", llvm, StringComparison.Ordinal);
        Assert.Contains("i8 2, label", llvm, StringComparison.Ordinal);
        Assert.Contains("i8 3, label", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("declare fastcc i32 @Run(i8)", llvm, StringComparison.Ordinal);
    }

    [Fact]
    public void RangePatternIntegerSwitchEmitsGuardedComparisons()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn i32[min max] Run(u8[0 30] value)
            {
                switch (value)
                {
                    case 0..9:
                        return 10;
                    case 12..20:
                        return 20;
                    default:
                        return 30;
                }
            }
            """,
            options: new CompilerOptions(EmitLlvmIr: true));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);

        Assert.DoesNotContain("switch i8 %arg_value", llvm, StringComparison.Ordinal);
        Assert.Contains("icmp ule i8 %arg_value, 9", llvm, StringComparison.Ordinal);
        Assert.Contains("icmp uge i8 %arg_value, 12", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("declare fastcc i32 @Run(i8)", llvm, StringComparison.Ordinal);
    }

    [Fact]
    public void RangePatternEnumPayloadSwitchEmitsPayloadComparison()
    {
        var result = Compile(
            """
            module Demo

            enum Token
            {
                Number(i32[min max]),
                End,
            }

            unsafe fn i32[min max] Run(Token token)
            {
                switch (token)
                {
                    case Token.Number(0..9):
                        return 1;
                    case Token.Number(_):
                        return 2;
                    case Token.End:
                        return 0;
                }
            }
            """,
            options: new CompilerOptions(EmitLlvmIr: true));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);

        Assert.Contains("@Run(%Token", llvm, StringComparison.Ordinal);
        Assert.Contains("icmp eq i8", llvm, StringComparison.Ordinal);
        Assert.Contains("icmp sge i32", llvm, StringComparison.Ordinal);
        Assert.Contains("icmp sle i32", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("declare fastcc i32 @Run(%Token)", llvm, StringComparison.Ordinal);
    }

    [Fact]
    public void OrPatternEnumCapturesShareBodyLocal()
    {
        var result = Compile(
            """
            module Demo

            enum Token
            {
                Left(i32[min max]),
                Right(i32[min max]),
                End,
            }

            unsafe fn i32[min max] Run(Token token)
            {
                switch (token)
                {
                    case Token.Left(var value) | Token.Right(var value):
                        return value;
                    case Token.End:
                        return 0;
                }
            }
            """,
            options: new CompilerOptions(EmitLlvmIr: true));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);

        Assert.Contains("@Run(%Token", llvm, StringComparison.Ordinal);
        Assert.Contains("icmp eq i8", llvm, StringComparison.Ordinal);
        Assert.Contains("ret i32", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("declare fastcc i32 @Run(%Token)", llvm, StringComparison.Ordinal);
    }

    [Fact]
    public void ComparisonChainEmitsShortCircuitBranchesAndSingleSharedEvaluation()
    {
        var result = Compile(
            """
            module Demo

            noinline unsafe fn i32[min max] Next()
            {
                return 1;
            }

            unsafe fn bool Run()
            {
                return 0 < Next() < 3;
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Equal(1, CountOccurrences(llvm, "call fastcc i32 @Next()"));
        Assert.Equal(2, CountOccurrences(llvm, "icmp slt i32"));
        Assert.Contains("br i1", llvm);
        Assert.DoesNotContain("declare fastcc i1 @Run()", llvm);
    }

    [Fact]
    public void FloatComparisonChainsUseOrderedPredicates()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn bool Run(f32 low, f32 value, f32 high)
            {
                return low < value <= high;
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("fcmp fast olt float", llvm);
        Assert.Contains("fcmp fast ole float", llvm);
        Assert.Contains("br i1", llvm);
    }

    [Fact]
    public void TextLiteralSwitchEmitsLengthAndByteComparisons()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn i32[min max] Run(ascii value, bool allow)
            {
                switch (value)
                {
                    case "ab":
                        return 1;
                    case "cd" when allow:
                        return 2;
                    default:
                        return 3;
                }
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("extractvalue %stark_ascii %arg_value, 0", llvm);
        Assert.Contains("extractvalue %stark_ascii %arg_value, 1", llvm);
        Assert.Contains("icmp eq i64", llvm);
        Assert.Contains("load i8, ptr", llvm);
        Assert.Contains("select i1 %arg_allow", llvm);
        Assert.DoesNotContain("switch %stark_ascii", llvm);
        Assert.DoesNotContain("declare fastcc i32 @Run(%stark_ascii, i1)", llvm);
    }

    [Fact]
    public void LargeTextLiteralSwitchEmitsLengthPartitionedDispatch()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn i32[min max] Run(ascii value)
            {
                switch (value)
                {
                    case "":
                        return 0;
                    case "a":
                        return 1;
                    case "b":
                        return 2;
                    case "cc":
                        return 3;
                    case "dd":
                        return 4;
                    case "eee":
                        return 5;
                    case "fff":
                        return 6;
                    case "gggg":
                        return 7;
                    case "hhhh":
                        return 8;
                    case "iiiii":
                        return 9;
                    default:
                        return 10;
                }
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Equal(1, CountOccurrences(llvm, "extractvalue %stark_ascii %arg_value, 1"));
        Assert.Contains("switch i64", llvm);
        Assert.Contains("icmp eq i8", llvm);
        Assert.DoesNotContain("declare fastcc i32 @Run(%stark_ascii)", llvm);
    }

    [Fact]
    public void UnicodeTextLiteralSwitchEmitsConcreteBody()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn i32[min max] Run(unicode value)
            {
                switch (value)
                {
                    case "\u03c0":
                        return 1;
                    case "\u03bb":
                        return 2;
                    default:
                        return 3;
                }
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Matches(@"define fastcc .* i32 @Run\(%stark_unicode %arg_value\)", llvm);
        Assert.Contains("extractvalue %stark_unicode %arg_value, 0", llvm);
        Assert.Contains("load i32, ptr", llvm);
        Assert.DoesNotContain("declare fastcc i32 @Run(%stark_unicode)", llvm);
    }

    [Fact]
    public void TextSlicesEmitShiftedAsciiAndUnicodeViews()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn ascii SliceAscii(ascii text, i32[min max] start, i32[min max] length)
            {
                return text[start, length];
            }

            unsafe fn unicode SliceUnicode(unicode text, i32[min max] start, i32[min max] length)
            {
                return text[start, length];
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("define fastcc %stark_ascii @SliceAscii(%stark_ascii %arg_text, i32 %arg_start, i32 %arg_length)", llvm);
        Assert.Contains("extractvalue %stark_ascii %arg_text, 0", llvm);
        Assert.Contains("getelementptr i8, ptr", llvm);
        Assert.DoesNotContain("getelementptr inbounds i8, ptr", llvm);
        Assert.Contains("insertvalue %stark_ascii", llvm);
        Assert.Contains("i64 %", llvm);

        Assert.Contains("define fastcc %stark_unicode @SliceUnicode(%stark_unicode %arg_text, i32 %arg_start, i32 %arg_length)", llvm);
        Assert.Contains("extractvalue %stark_unicode %arg_text, 0", llvm);
        Assert.Contains("getelementptr i32, ptr", llvm);
        Assert.DoesNotContain("getelementptr inbounds i32, ptr", llvm);
        Assert.Contains("insertvalue %stark_unicode", llvm);
    }

    [Fact]
    public void ExplicitAsciiLiteralToUnicodeConversionEmitsUnicodeConstant()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn unicode Run()
            {
                return (unicode)"Hello";
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("define fastcc %stark_unicode @Run()", llvm);
        Assert.Contains("ret %stark_unicode { ptr getelementptr inbounds nuw ([6 x i32], ptr @.str.", llvm);
        Assert.DoesNotContain("Unsupported SSA conversion from 'ascii' to 'unicode'", llvm);
        Assert.DoesNotContain("declare fastcc %stark_unicode @Run()", llvm);
    }

    [Fact]
    public void CompileTimeTextConcatenationEmitsSingleTextConstant()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn ascii Run()
            {
                return "Score: " + "100";
            }
            """);

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvm(result);

        Assert.Contains("private unnamed_addr constant [11 x i8] c\"Score: 100\\00\"", llvm);
        Assert.Contains("ret %stark_ascii { ptr getelementptr inbounds nuw ([11 x i8], ptr @.str.", llvm);
    }

    [Fact]
    public void CompileTimeInterpolatedTextEmitsFoldedTextConstant()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn ascii Run()
            {
                return $"Score: {100}";
            }
            """);

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvm(result);

        Assert.Contains("private unnamed_addr constant [11 x i8] c\"Score: 100\\00\"", llvm);
        Assert.DoesNotContain("Score: {100}", llvm);
    }

    [Fact]
    public void RawAndMultilineTextLiteralsEmitExactTextConstants()
    {
        var result = Compile(
            """""
            module Demo

            unsafe fn ascii RawPath()
            {
                return raw"c:\temp\next";
            }

            unsafe fn ascii Multiline()
            {
                return raw"""
                    first
                    second
                    """;
            }

            unsafe fn ascii Interpolated()
            {
                return $raw"Score: {100}\n";
            }
            """"");

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvm(result);

        Assert.Contains("private unnamed_addr constant [13 x i8] c\"c:\\5Ctemp\\5Cnext\\00\"", llvm);
        Assert.Contains("private unnamed_addr constant [13 x i8] c\"first\\0Asecond\\00\"", llvm);
        Assert.Contains("private unnamed_addr constant [13 x i8] c\"Score: 100\\5Cn\\00\"", llvm);
    }

    [Fact]
    public void SystemTextOwnedConcatAndViewBuiltinsEmitConcreteDefinitions()
    {
        var result = Compile(
            """
            module System.Text

            public unsafe finite law ascii AsciiView(Ascii source);
            public unsafe finite law unicode UnicodeView(Unicode source);
            public unsafe fn bool TryConcatAscii(rawmutptr<Ascii> destination, ascii left, ascii right);
            public unsafe fn bool TryConcatUnicode(rawmutptr<Unicode> destination, unicode left, unicode right);

            public unsafe fn unicode Run()
            {
                stack mut i8[min max][16] asciiBuffer =
                {
                    0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0
                };
                stack mut Ascii ownedAscii = new Ascii()
                {
                    Data = &asciiBuffer[0],
                    Length = 0,
                    Capacity = 16
                };

                stack mut i32[min max][8] unicodeBuffer =
                {
                    0, 0, 0, 0, 0, 0, 0, 0
                };
                stack mut Unicode ownedUnicode = new Unicode()
                {
                    Data = &unicodeBuffer[0],
                    Length = 0,
                    Capacity = 8
                };
                if (!TryConcatAscii(&ownedAscii, "Stark", " IO"))
                {
                    return (unicode)"";
                }

                if (!TryConcatUnicode(&ownedUnicode, (unicode)"Hi", (unicode)" \u03B1"))
                {
                    return (unicode)"";
                }

                return UnicodeView(ownedUnicode);
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("%Ascii = type { ptr, i64, i64 }", llvm);
        Assert.Contains("%Unicode = type { ptr, i64, i64 }", llvm);
        Assert.Contains("define fastcc %stark_ascii @AsciiView(", llvm);
        Assert.Contains("define fastcc %stark_unicode @UnicodeView(", llvm);
        Assert.Contains("define fastcc i1 @TryConcatAscii(", llvm);
        Assert.Contains("define fastcc i1 @TryConcatUnicode(", llvm);
        Assert.Contains("%concat_left_nonnegative = icmp sge i64 %concat_left_length, 0", llvm);
        Assert.Contains("%concat_right_nonnegative = icmp sge i64 %concat_right_length, 0", llvm);
        Assert.Contains("%concat_capacity_nonnegative = icmp sge i64 %concat_capacity, 0", llvm);
        Assert.Contains("%concat_no_length_overflow = icmp sle i64 %concat_right_length, %concat_max_after_left", llvm);
        Assert.Contains("call void @llvm.memcpy.p0.p0.i64(ptr %concat_data, ptr %concat_left_data, i64 %concat_left_bytes, i1 false)", llvm);
        Assert.Contains("call void @llvm.memcpy.p0.p0.i64(ptr %concat_right_dest, ptr %concat_right_data, i64 %concat_right_bytes, i1 false)", llvm);
        Assert.Contains("declare void @llvm.memcpy.p0.p0.i64(", llvm);
        Assert.DoesNotContain("%concat_left_index = phi i64", llvm);
        Assert.Contains("getelementptr inbounds nuw i32, ptr %concat_data, i64 %concat_left_length", llvm);
        Assert.Contains("call fastcc %stark_unicode @UnicodeView(", llvm);
        Assert.DoesNotContain("declare fastcc i1 @TryConcatAscii(", llvm);
        Assert.DoesNotContain("declare fastcc i1 @TryConcatUnicode(", llvm);
    }

    [Fact]
    public void SystemTextAsciiToUnicodeBuiltinEmitsDirectWideningLoop()
    {
        var result = Compile(
            """
            module System.Text

            public inline unsafe finite bool TryConvertAsciiToUnicode(rawmutptr<Unicode> destination, ascii source);

            public unsafe fn bool Run()
            {
                stack mut i32[min max][16] unicodeBuffer =
                {
                    0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0
                };
                stack mut Unicode ownedUnicode = new Unicode()
                {
                    Data = &unicodeBuffer[0],
                    Length = 0,
                    Capacity = 16
                };

                return TryConvertAsciiToUnicode(&ownedUnicode, "Stark");
            }
            """);

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvm(result);

        Assert.Contains("define fastcc i1 @TryConvertAsciiToUnicode(", llvm);
        Assert.Contains("alwaysinline", llvm);
        Assert.Contains("%convert_source_data = extractvalue %stark_ascii %arg_source, 0", llvm);
        Assert.Contains("%convert_fast_wide = zext i8 %convert_fast_unit to i32", llvm);
        Assert.Contains("%convert_fast_disjoint = or i1 %convert_fast_source_before_dest, %convert_fast_dest_before_source", llvm);
        Assert.Contains("!\"llvm.loop.parallel_accesses\"", llvm);
        Assert.Contains("convert_fallback_loop:", llvm);
        Assert.DoesNotContain("call fastcc i64 @AsciiLength(", llvm);
        Assert.DoesNotContain("call fastcc ptr @AsciiData(", llvm);
        Assert.DoesNotContain("call fastcc i1 @TryDecodeUtf8CodePoint(", llvm);
        Assert.DoesNotContain("call fastcc i32 @NormalizeUnicodeCodePoint(", llvm);
        Assert.DoesNotContain("declare fastcc i1 @TryConvertAsciiToUnicode(", llvm);
    }

    [Fact]
    public void SystemTextAsciiToUnicodeLiteralCallSpecializesAtCallSite()
    {
        var result = Compile(
            """
            module System.Text

            public inline unsafe finite bool TryConvertAsciiToUnicode(rawmutptr<Unicode> destination, ascii source);

            public unsafe fn bool Run()
            {
                stack mut i32[min max][16] unicodeBuffer =
                {
                    0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0
                };
                stack mut Unicode ownedUnicode = new Unicode()
                {
                    Data = &unicodeBuffer[0],
                    Length = 0,
                    Capacity = 16
                };

                return TryConvertAsciiToUnicode(&ownedUnicode, "Stark");
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvm(result);

        Assert.Contains("ascii2unicode_capacity_too_small", llvm);
        Assert.Contains("ascii2unicode_unit_", llvm);
        Assert.Contains("store i32 83, ptr", llvm);
        Assert.DoesNotContain("call fastcc i1 @TryConvertAsciiToUnicode(", llvm);
    }

    [Fact]
    public void SystemTextAsciiToUnicodeSlicedLiteralSourceSpecializesAtCallSite()
    {
        var result = Compile(
            """
            module System.Text

            public inline unsafe finite bool TryConvertAsciiToUnicode(rawmutptr<Unicode> destination, ascii source);

            public unsafe fn bool Run()
            {
                stack mut i32[min max][16] unicodeBuffer =
                {
                    0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0
                };
                stack mut Unicode ownedUnicode = new Unicode()
                {
                    Data = &unicodeBuffer[0],
                    Length = 0,
                    Capacity = 16
                };

                stack ascii source = "abcdef"[2, 3];
                return TryConvertAsciiToUnicode(&ownedUnicode, source);
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvm(result);

        Assert.Contains("ascii2unicode_capacity_too_small", llvm);
        Assert.Contains("store i32 99, ptr", llvm);
        Assert.Contains("store i32 100, ptr", llvm);
        Assert.Contains("store i32 101, ptr", llvm);
        Assert.DoesNotContain("call fastcc i1 @TryConvertAsciiToUnicode(", llvm);
    }

    [Fact]
    public void SystemTextAsciiToUnicodeDynamicSliceSourceKeepsBuiltinCall()
    {
        var result = Compile(
            """
            module System.Text

            public inline unsafe finite bool TryConvertAsciiToUnicode(rawmutptr<Unicode> destination, ascii source);

            public unsafe fn bool Run(u8[0 3] start)
            {
                stack mut i32[min max][16] unicodeBuffer =
                {
                    0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0
                };
                stack mut Unicode ownedUnicode = new Unicode()
                {
                    Data = &unicodeBuffer[0],
                    Length = 0,
                    Capacity = 16
                };

                return TryConvertAsciiToUnicode(&ownedUnicode, "abcdef"[start, 3]);
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvm(result);

        Assert.Contains("call fastcc i1 @TryConvertAsciiToUnicode(", llvm);
    }

    [Fact]
    public void SystemTextAsciiToUnicodeLargeLiteralSpecializationUsesMemcpy()
    {
        var result = Compile(
            """
            module System.Text

            public inline unsafe finite bool TryConvertAsciiToUnicode(rawmutptr<Unicode> destination, ascii source);

            public unsafe fn bool Run()
            {
                stack mut i32[min max][64] unicodeBuffer =
                {
                    0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0
                };
                stack mut Unicode ownedUnicode = new Unicode()
                {
                    Data = &unicodeBuffer[0],
                    Length = 0,
                    Capacity = 64
                };

                return TryConvertAsciiToUnicode(&ownedUnicode, "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789stark");
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvm(result);

        Assert.Contains("ascii2unicode_data", llvm);
        Assert.Contains("declare void @llvm.memcpy.inline.p0.p0.i64(", llvm);
        Assert.Contains("constant [42 x i32]", llvm);
        Assert.Contains("i64 164, i1 false)", llvm);
        Assert.DoesNotContain("call fastcc i1 @TryConvertAsciiToUnicode(", llvm);
        Assert.DoesNotContain("ascii2unicode_unit", llvm);
    }

    [Fact]
    public void SystemTextAsciiToUnicodeDynamicSourceKeepsBuiltinCall()
    {
        var result = Compile(
            """
            module System.Text

            public inline unsafe finite bool TryConvertAsciiToUnicode(rawmutptr<Unicode> destination, ascii source);

            public unsafe fn bool Run(ascii source)
            {
                stack mut i32[min max][16] unicodeBuffer =
                {
                    0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0
                };
                stack mut Unicode ownedUnicode = new Unicode()
                {
                    Data = &unicodeBuffer[0],
                    Length = 0,
                    Capacity = 16
                };

                return TryConvertAsciiToUnicode(&ownedUnicode, source);
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvm(result);

        Assert.Contains("call fastcc i1 @TryConvertAsciiToUnicode(", llvm);
        Assert.DoesNotContain("abi_ascii2unicode_store", llvm);
    }

    [Fact]
    public void SystemTextAsciiToUnicodeLocalLiteralSourceSpecializesAtCallSite()
    {
        var result = Compile(
            """
            module System.Text

            public inline unsafe finite bool TryConvertAsciiToUnicode(rawmutptr<Unicode> destination, ascii source);

            public unsafe fn bool Run()
            {
                stack mut i32[min max][16] unicodeBuffer =
                {
                    0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0
                };
                stack mut Unicode ownedUnicode = new Unicode()
                {
                    Data = &unicodeBuffer[0],
                    Length = 0,
                    Capacity = 16
                };
                stack ascii source = "Stark";

                return TryConvertAsciiToUnicode(&ownedUnicode, source);
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvm(result);

        Assert.Contains("ascii2unicode_unit_", llvm);
        Assert.Contains("store i32 83, ptr", llvm);
        Assert.DoesNotContain("call fastcc i1 @TryConvertAsciiToUnicode(", llvm);
    }

    [Fact]
    public void SystemTextAsciiToUnicodeConstLiteralSourceSpecializesAtCallSite()
    {
        var result = Compile(
            """
            module System.Text

            public inline unsafe finite bool TryConvertAsciiToUnicode(rawmutptr<Unicode> destination, ascii source);

            public unsafe fn bool Run()
            {
                stack mut i32[min max][16] unicodeBuffer =
                {
                    0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0
                };
                stack mut Unicode ownedUnicode = new Unicode()
                {
                    Data = &unicodeBuffer[0],
                    Length = 0,
                    Capacity = 16
                };
                const ascii source = "Stark";

                return TryConvertAsciiToUnicode(&ownedUnicode, source);
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvm(result);

        Assert.Contains("ascii2unicode_unit_", llvm);
        Assert.Contains("store i32 83, ptr", llvm);
        Assert.DoesNotContain("call fastcc i1 @TryConvertAsciiToUnicode(", llvm);
    }

    [Fact]
    public void SystemTextAsciiToUnicodePhiJoinedIdenticalLiteralSourceSpecializesAtCallSite()
    {
        var result = Compile(
            """
            module System.Text

            public inline unsafe finite bool TryConvertAsciiToUnicode(rawmutptr<Unicode> destination, ascii source);

            public unsafe fn bool Run(bool choose)
            {
                stack mut i32[min max][16] unicodeBuffer =
                {
                    0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0
                };
                stack mut Unicode ownedUnicode = new Unicode()
                {
                    Data = &unicodeBuffer[0],
                    Length = 0,
                    Capacity = 16
                };
                stack ascii source = choose ? "Stark" : "Stark";

                return TryConvertAsciiToUnicode(&ownedUnicode, source);
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvm(result);

        Assert.Contains("ascii2unicode_unit_", llvm);
        Assert.Contains("store i32 83, ptr", llvm);
        Assert.DoesNotContain("call fastcc i1 @TryConvertAsciiToUnicode(", llvm);
    }

    [Fact]
    public void SystemTextAsciiToUnicodeEscapedAsciiLiteralSpecializesAtCallSite()
    {
        var result = Compile(
            """
            module System.Text

            public inline unsafe finite bool TryConvertAsciiToUnicode(rawmutptr<Unicode> destination, ascii source);

            public unsafe fn bool Run()
            {
                stack mut i32[min max][16] unicodeBuffer =
                {
                    0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0
                };
                stack mut Unicode ownedUnicode = new Unicode()
                {
                    Data = &unicodeBuffer[0],
                    Length = 0,
                    Capacity = 16
                };

                return TryConvertAsciiToUnicode(&ownedUnicode, "A\nZ");
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvm(result);

        Assert.Contains("ascii2unicode_unit_", llvm);
        Assert.Contains("store i32 65, ptr", llvm);
        Assert.Contains("store i32 10, ptr", llvm);
        Assert.Contains("store i32 90, ptr", llvm);
        Assert.DoesNotContain("call fastcc i1 @TryConvertAsciiToUnicode(", llvm);
    }

    [Fact]
    public void SystemTextAsciiToUnicodeEmptyLiteralSpecializationAvoidsDataLoad()
    {
        var result = Compile(
            """
            module System.Text

            public inline unsafe finite bool TryConvertAsciiToUnicode(rawmutptr<Unicode> destination, ascii source);

            public unsafe fn bool Run()
            {
                stack mut Unicode ownedUnicode = new Unicode()
                {
                    Data = null,
                    Length = 7,
                    Capacity = 0
                };

                return TryConvertAsciiToUnicode(&ownedUnicode, "");
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvm(result);

        Assert.Contains("ascii2unicode_capacity_too_small", llvm);
        Assert.Contains("store i64 0, ptr", llvm);
        Assert.DoesNotContain("ascii2unicode_data_is_null", llvm);
        Assert.DoesNotContain("call fastcc i1 @TryConvertAsciiToUnicode(", llvm);
    }

    [Fact]
    public void SystemTextAsciiToUnicodeLiteralSpecializationHandlesNullDestination()
    {
        var result = Compile(
            """
            module System.Text

            public inline unsafe finite bool TryConvertAsciiToUnicode(rawmutptr<Unicode> destination, ascii source);

            public unsafe fn bool Run()
            {
                return TryConvertAsciiToUnicode(null, "Stark");
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvm(result);

        // The constant-null destination folds the specialized null check all the
        // way to the failure result.
        Assert.Contains("ret i1 false", llvm);
        Assert.DoesNotContain("call fastcc i1 @TryConvertAsciiToUnicode(", llvm);
    }

    [Fact]
    public void SystemTextAsciiToUnicodeLiteralSpecializationChecksCapacity()
    {
        var result = Compile(
            """
            module System.Text

            public inline unsafe finite bool TryConvertAsciiToUnicode(rawmutptr<Unicode> destination, ascii source);

            public unsafe fn bool Run()
            {
                stack mut i32[min max][4] unicodeBuffer =
                {
                    0, 0, 0, 0
                };
                stack mut Unicode ownedUnicode = new Unicode()
                {
                    Data = &unicodeBuffer[0],
                    Length = 7,
                    Capacity = 4
                };

                return TryConvertAsciiToUnicode(&ownedUnicode, "Stark");
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvm(result);

        Assert.Contains("ascii2unicode_capacity_too_small", llvm);
        Assert.Contains("icmp slt i64", llvm);
        Assert.Contains("store i64 0, ptr", llvm);
        Assert.DoesNotContain("call fastcc i1 @TryConvertAsciiToUnicode(", llvm);
    }

    [Fact]
    public void SystemTextAsciiToUnicodeLiteralSpecializationChecksNullDestinationData()
    {
        var result = Compile(
            """
            module System.Text

            public inline unsafe finite bool TryConvertAsciiToUnicode(rawmutptr<Unicode> destination, ascii source);

            public unsafe fn bool Run()
            {
                stack mut Unicode ownedUnicode = new Unicode()
                {
                    Data = null,
                    Length = 7,
                    Capacity = 16
                };

                return TryConvertAsciiToUnicode(&ownedUnicode, "Stark");
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvm(result);

        Assert.Contains("ascii2unicode_data_is_null", llvm);
        Assert.Contains("store i64 0, ptr", llvm);
        Assert.DoesNotContain("call fastcc i1 @TryConvertAsciiToUnicode(", llvm);
    }

    [Fact]
    public void SystemTextAsciiToUnicodeLiteralSpecializationRewritesForwardPhiIncomingLabel()
    {
        var result = Compile(
            """
            module System.Text

            public inline unsafe finite bool TryConvertAsciiToUnicode(rawmutptr<Unicode> destination, ascii source);

            public unsafe fn i32[min max] Run(bool choose)
            {
                stack mut i32[min max][64] unicodeBuffer =
                {
                    0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0
                };
                stack mut Unicode ownedUnicode = new Unicode()
                {
                    Data = &unicodeBuffer[0],
                    Length = 0,
                    Capacity = 64
                };
                stack mut i32[min max] result = 0;
                if (choose)
                {
                    TryConvertAsciiToUnicode(&ownedUnicode, "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789stark");
                    result = 2;
                }
                else
                {
                    result = 3;
                }

                return result;
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvm(result);

        Assert.Contains("abi_ascii2unicode_store", llvm);
        Assert.Contains("declare void @llvm.memcpy.p0.p0.i64(", llvm);
        Assert.Matches(@"phi i32 \[ (?:%v\d+|2), %abi_ascii2unicode_done_\d+ \]", llvm);
        Assert.DoesNotContain("call fastcc i1 @TryConvertAsciiToUnicode(", llvm);
    }

    [Fact]
    public void SystemTextAsciiToUnicodeNonAsciiLiteralKeepsBuiltinCall()
    {
        var result = Compile(
            """
            module System.Text

            public inline unsafe finite bool TryConvertAsciiToUnicode(rawmutptr<Unicode> destination, ascii source);

            public unsafe fn bool Run()
            {
                stack mut i32[min max][16] unicodeBuffer =
                {
                    0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0
                };
                stack mut Unicode ownedUnicode = new Unicode()
                {
                    Data = &unicodeBuffer[0],
                    Length = 0,
                    Capacity = 16
                };

                return TryConvertAsciiToUnicode(&ownedUnicode, "caf\u00E9");
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvm(result);

        Assert.Contains("call fastcc i1 @TryConvertAsciiToUnicode(", llvm);
        Assert.DoesNotContain("abi_ascii2unicode_store", llvm);
    }

    [Fact]
    public void SystemMathBuiltinsEmitConcreteDefinitionsAndLlvmIntrinsics()
    {
        var result = Compile(
            """
            module System.Math

            public struct SinCosF64
            {
                f64 Sin;
                f64 Cos;
            }

            public unsafe finite law f32 Sin(f32 value);
            public unsafe finite law f64 Cos(f64 value);
            public unsafe finite law f64 Atan2(f64 y, f64 x);
            public unsafe finite law f64 Pow(f64 value, f64 exponent);
            public unsafe finite law f32 Tanh(f32 value);
            public unsafe finite law SinCosF64 SinCos(f64 value);
            public unsafe finite law f32 Min(f32 left, f32 right);
            public unsafe finite law f64 Max(f64 left, f64 right);
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("define fastcc float @Sin(float %arg_value)", llvm);
        Assert.Contains("define fastcc double @Cos(double %arg_value)", llvm);
        Assert.Contains("define fastcc double @Atan2(double %arg_y, double %arg_x)", llvm);
        Assert.Contains("define fastcc double @Pow(double %arg_value, double %arg_exponent)", llvm);
        Assert.Contains("define fastcc float @Tanh(float %arg_value)", llvm);
        Assert.Contains("define fastcc %SinCosF64 @SinCos(double %arg_value)", llvm);
        Assert.Contains("define fastcc float @Min(float %arg_left, float %arg_right)", llvm);
        Assert.Contains("define fastcc double @Max(double %arg_left, double %arg_right)", llvm);
        Assert.Contains("call fast float @llvm.sin.f32(float %arg_value)", llvm);
        Assert.Contains("call fast double @llvm.cos.f64(double %arg_value)", llvm);
        Assert.Contains("call fast double @llvm.atan2.f64(double %arg_y, double %arg_x)", llvm);
        Assert.Contains("call fast double @llvm.pow.f64(double %arg_value, double %arg_exponent)", llvm);
        Assert.Contains("call fast float @llvm.tanh.f32(float %arg_value)", llvm);
        Assert.Contains("call fast { double, double } @llvm.sincos.f64(double %arg_value)", llvm);
        Assert.Contains("call fast float @llvm.minnum.f32(float %arg_left, float %arg_right)", llvm);
        Assert.Contains("call fast double @llvm.maxnum.f64(double %arg_left, double %arg_right)", llvm);
        Assert.Contains("extractvalue { double, double } %math_pair, 0", llvm);
        Assert.Contains("extractvalue { double, double } %math_pair, 1", llvm);
        Assert.Contains("insertvalue %SinCosF64 zeroinitializer, double %math_sin, 0", llvm);
        Assert.Contains("insertvalue %SinCosF64 %math_with_sin, double %math_cos, 1", llvm);
        Assert.Contains("declare float @llvm.sin.f32(float)", llvm);
        Assert.Contains("declare double @llvm.cos.f64(double)", llvm);
        Assert.Contains("declare double @llvm.atan2.f64(double, double)", llvm);
        Assert.Contains("declare double @llvm.pow.f64(double, double)", llvm);
        Assert.Contains("declare float @llvm.tanh.f32(float)", llvm);
        Assert.Contains("declare { double, double } @llvm.sincos.f64(double)", llvm);
        Assert.Contains("declare float @llvm.minnum.f32(float, float)", llvm);
        Assert.Contains("declare double @llvm.maxnum.f64(double, double)", llvm);
        Assert.DoesNotContain("declare fastcc float @Sin(", llvm);
        Assert.DoesNotContain("declare fastcc double @Cos(", llvm);
        Assert.DoesNotContain("declare fastcc double @Atan2(", llvm);
        Assert.DoesNotContain("declare fastcc double @Pow(", llvm);
        Assert.DoesNotContain("declare fastcc float @Tanh(", llvm);
        Assert.DoesNotContain("declare fastcc %SinCosF64 @SinCos(", llvm);
        Assert.DoesNotContain("declare fastcc float @Min(", llvm);
        Assert.DoesNotContain("declare fastcc double @Max(", llvm);
    }

    [Fact]
    public void SystemBitOperationsBuiltinsEmitConcreteDefinitionsAndLlvmIntrinsics()
    {
        var result = Compile(
            """
            module System.BitOperations

            public unsafe finite law i32[min max] LeadingZeroCount(i32[min max] value);
            public unsafe finite law i64[min max] TrailingZeroCount(i64[min max] value);
            public unsafe finite law i32[min max] PopCount(i32[min max] value);
            public unsafe finite law i64[min max] RotateLeft(i64[min max] value, i64[min max] amount);
            public unsafe finite law i32[min max] RotateRight(i32[min max] value, i32[min max] amount);
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("define fastcc i32 @LeadingZeroCount(i32 %arg_value)", llvm);
        Assert.Contains("define fastcc i64 @TrailingZeroCount(i64 %arg_value)", llvm);
        Assert.Contains("define fastcc i32 @PopCount(i32 %arg_value)", llvm);
        Assert.Contains("define fastcc i64 @RotateLeft(i64 %arg_value, i64 %arg_amount)", llvm);
        Assert.Contains("define fastcc i32 @RotateRight(i32 %arg_value, i32 %arg_amount)", llvm);
        Assert.Contains("call i32 @llvm.ctlz.i32(i32 %arg_value, i1 false)", llvm);
        Assert.Contains("call i64 @llvm.cttz.i64(i64 %arg_value, i1 false)", llvm);
        Assert.Contains("call i32 @llvm.ctpop.i32(i32 %arg_value)", llvm);
        Assert.Contains("call i64 @llvm.fshl.i64(i64 %arg_value, i64 %arg_value, i64 %arg_amount)", llvm);
        Assert.Contains("call i32 @llvm.fshr.i32(i32 %arg_value, i32 %arg_value, i32 %arg_amount)", llvm);
        Assert.Contains("declare i32 @llvm.ctlz.i32(i32, i1 immarg)", llvm);
        Assert.Contains("declare i64 @llvm.cttz.i64(i64, i1 immarg)", llvm);
        Assert.Contains("declare i32 @llvm.ctpop.i32(i32)", llvm);
        Assert.Contains("declare i64 @llvm.fshl.i64(i64, i64, i64)", llvm);
        Assert.Contains("declare i32 @llvm.fshr.i32(i32, i32, i32)", llvm);
        Assert.DoesNotContain("declare fastcc i32 @LeadingZeroCount(", llvm);
        Assert.DoesNotContain("declare fastcc i64 @TrailingZeroCount(", llvm);
        Assert.DoesNotContain("declare fastcc i32 @PopCount(", llvm);
        Assert.DoesNotContain("declare fastcc i64 @RotateLeft(", llvm);
        Assert.DoesNotContain("declare fastcc i32 @RotateRight(", llvm);
    }

    [Fact]
    public void SystemMathHardwareBuiltinsEmitInlineAsmForX86_64()
    {
        var result = Compile(
            """
            module System.Math

            public unsafe finite law f64 Sqrt(f64 value);
            public unsafe finite law f32 FusedMultiplyAdd(f32 left, f32 right, f32 addend);
            public unsafe finite law f64 FusedMultiplyAdd(f64 left, f64 right, f64 addend);
            public unsafe finite law f32 ReciprocalEstimate(f32 value);
            public unsafe finite law f32 ReciprocalSqrtEstimate(f32 value);
            public unsafe finite law f32 Ceiling(f32 value);
            public unsafe finite law f64 Floor(f64 value);
            public unsafe finite law f64 Truncate(f64 value);
            public unsafe finite law f64 Round(f64 value);
            """,
            new CompilerOptions(
                TargetInfo: new LlvmTargetInfo("x86_64-unknown-linux-gnu", null)));

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("define fastcc double @Sqrt(double %arg_value)", llvm);
        Assert.Contains("call fast double asm \"sqrtsd %xmm0, %xmm0\", \"={xmm0},0\"(double %arg_value)", llvm);
        Assert.Contains("call fast float asm \"vfmadd213ss %xmm2, %xmm1, %xmm0\", \"={xmm0},0,{xmm1},{xmm2}\"(", llvm);
        Assert.Contains("call fast double asm \"vfmadd213sd %xmm2, %xmm1, %xmm0\", \"={xmm0},0,{xmm1},{xmm2}\"(", llvm);
        Assert.Contains("define fastcc float @ReciprocalEstimate(float %arg_value)", llvm);
        Assert.Contains("call fast float asm \"rcpss %xmm0, %xmm0\", \"={xmm0},0\"(float %arg_value)", llvm);
        Assert.Contains("define fastcc float @ReciprocalSqrtEstimate(float %arg_value)", llvm);
        Assert.Contains("call fast float asm \"rsqrtss %xmm0, %xmm0\", \"={xmm0},0\"(float %arg_value)", llvm);
        Assert.Contains("define fastcc float @Ceiling(float %arg_value)", llvm);
        Assert.Contains("call fast float asm \"roundss $$2, %xmm0, %xmm0\", \"={xmm0},0\"(float %arg_value)", llvm);
        Assert.Contains("call fast double asm \"roundsd $$1, %xmm0, %xmm0\", \"={xmm0},0\"(double %arg_value)", llvm);
        Assert.Contains("call fast double asm \"roundsd $$3, %xmm0, %xmm0\", \"={xmm0},0\"(double %arg_value)", llvm);
        Assert.Contains("call fast double asm \"roundsd $$0, %xmm0, %xmm0\", \"={xmm0},0\"(double %arg_value)", llvm);
        Assert.DoesNotContain("@llvm.sqrt.", llvm);
        Assert.DoesNotContain("@llvm.ceil.", llvm);
        Assert.DoesNotContain("@llvm.floor.", llvm);
        Assert.DoesNotContain("@llvm.trunc.", llvm);
        Assert.DoesNotContain("@llvm.roundeven.", llvm);
    }

    [Fact]
    public void SystemMathHardwareBuiltinsEmitInlineAsmForAArch64()
    {
        var result = Compile(
            """
            module System.Math

            public unsafe finite law f64 Sqrt(f64 value);
            public unsafe finite law f32 FusedMultiplyAdd(f32 left, f32 right, f32 addend);
            public unsafe finite law f64 FusedMultiplyAdd(f64 left, f64 right, f64 addend);
            public unsafe finite law f32 ReciprocalEstimate(f32 value);
            public unsafe finite law f32 ReciprocalSqrtEstimate(f32 value);
            public unsafe finite law f32 Ceiling(f32 value);
            public unsafe finite law f64 Floor(f64 value);
            public unsafe finite law f64 Truncate(f64 value);
            public unsafe finite law f64 Round(f64 value);
            """,
            new CompilerOptions(
                TargetInfo: new LlvmTargetInfo("aarch64-unknown-linux-gnu", null)));

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("call fast double asm \"fsqrt d0, d0\", \"={d0},0\"(double %arg_value)", llvm);
        Assert.Contains("call fast float asm \"fmadd s0, s0, s1, s2\", \"={s0},0,{s1},{s2}\"(", llvm);
        Assert.Contains("call fast double asm \"fmadd d0, d0, d1, d2\", \"={d0},0,{d1},{d2}\"(", llvm);
        Assert.Contains("call fast float asm \"frecpe s0, s0\", \"={s0},0\"(float %arg_value)", llvm);
        Assert.Contains("call fast float asm \"frsqrte s0, s0\", \"={s0},0\"(float %arg_value)", llvm);
        Assert.Contains("call fast float asm \"frintp s0, s0\", \"={s0},0\"(float %arg_value)", llvm);
        Assert.Contains("call fast double asm \"frintm d0, d0\", \"={d0},0\"(double %arg_value)", llvm);
        Assert.Contains("call fast double asm \"frintz d0, d0\", \"={d0},0\"(double %arg_value)", llvm);
        Assert.Contains("call fast double asm \"frintn d0, d0\", \"={d0},0\"(double %arg_value)", llvm);
    }

    [Fact]
    public void ImportedSystemMathHardwareBuiltinsAreInternalizedIntoConsumerIr()
    {
        var result = Compile(
            """
            import System.Math
            module Demo

            unsafe fn f64 Run(f64 value)
            {
                return System.Math.Sqrt(value);
            }
            """,
            new CompilerOptions(
                TargetInfo: new LlvmTargetInfo("x86_64-unknown-linux-gnu", null),
                ModuleResolver: new InMemoryModuleResolver(
                [
                    (
                        new ResolvedModuleReference("System.Math", "/virtual/System/Math.stark", IsExternal: false),
                        """
                        module System.Math

                        public unsafe finite law f64 Sqrt(f64 value);
                        """,
                        "/virtual/System/Math.stark"
                    )
                ])));

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        var sqrtHeader = ExtractDefinitionHeader(llvm, "System_Math_Sqrt");
        Assert.Contains("define internal dso_local fastcc double @System_Math_Sqrt(double %arg_value)", sqrtHeader);
        Assert.Contains("unnamed_addr", sqrtHeader);
        Assert.Contains("call fast fastcc double @System_Math_Sqrt(double %arg_value)", llvm);
        Assert.DoesNotContain("declare fastcc double @System_Math_Sqrt(", llvm);
    }

    [Fact]
    public void UnusedImportedSystemMathHardwareBuiltinsDoNotMaterializeIntoConsumerIr()
    {
        var result = Compile(
            """
            import System.Math
            module Demo

            unsafe fn i32[min max] Run()
            {
                return 0;
            }
            """,
            new CompilerOptions(
                TargetInfo: new LlvmTargetInfo("x86_64-unknown-linux-gnu", null),
                ModuleResolver: new InMemoryModuleResolver(
                [
                    (
                        new ResolvedModuleReference("System.Math", "/virtual/System/Math.stark", IsExternal: false),
                        """
                        module System.Math

                        public unsafe finite law f64 Sqrt(f64 value);
                        public unsafe finite law f64 Round(f64 value);
                        """,
                        "/virtual/System/Math.stark"
                    )
                ])));

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.DoesNotContain("define internal dso_local fastcc double @System_Math_Sqrt(", llvm);
        Assert.DoesNotContain("define internal dso_local fastcc double @System_Math_Round(", llvm);
        Assert.DoesNotContain("call double asm \"sqrtsd %xmm0, %xmm0\", \"={xmm0},0\"", llvm);
        Assert.DoesNotContain("call double asm \"roundsd $$0, %xmm0, %xmm0\", \"={xmm0},0\"", llvm);
    }

    [Fact]
    public void ImportedSystemBitOperationsBuiltinsAreInternalizedIntoConsumerIr()
    {
        var result = Compile(
            """
            import System.BitOperations
            module Demo

            unsafe fn i32[min max] Run(i32[min max] value)
            {
                return System.BitOperations.PopCount(value);
            }
            """,
            new CompilerOptions(
                TargetInfo: new LlvmTargetInfo("x86_64-unknown-linux-gnu", null),
                ModuleResolver: new InMemoryModuleResolver(
                [
                    (
                        new ResolvedModuleReference("System.BitOperations", "/virtual/System/BitOperations.stark", IsExternal: false),
                        """
                        module System.BitOperations

                        public unsafe finite law i32[min max] PopCount(i32[min max] value);
                        """,
                        "/virtual/System/BitOperations.stark"
                    )
                ])));

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        var popCountHeader = ExtractDefinitionHeader(llvm, "System_BitOperations_PopCount");
        Assert.Contains("define internal dso_local fastcc i32 @System_BitOperations_PopCount(i32 %arg_value)", popCountHeader);
        Assert.Contains("unnamed_addr", popCountHeader);
        Assert.Contains("call fastcc i32 @System_BitOperations_PopCount(i32 %arg_value)", llvm);
        Assert.DoesNotContain("declare fastcc i32 @System_BitOperations_PopCount(", llvm);
    }

    [Fact]
    public void HelloWorldStyleFfiPutsEmitsStringGlobalAndMainBody()
    {
        var result = Compile(
            """
            module Hello

            unsafe ffi fn i32[min max] puts(ascii text);
            export unsafe fn i32[min max] main()
            {
                puts("Hello, world!\n");
                return 0;
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("@.str.0 = private unnamed_addr constant", llvm);
        Assert.Contains("%stark_ascii = type { ptr, i64 }", llvm);
        Assert.Contains("declare i32 @puts(ptr readonly) nounwind", llvm);
        Assert.Contains("define i32 @main()", llvm);
        Assert.Contains("call i32 @puts(ptr getelementptr inbounds nuw ([15 x i8], ptr @.str.0, i32 0, i32 0))", llvm);
        Assert.DoesNotContain("invoke i32 @puts", llvm);
    }

    [Fact]
    public void FfiLinkNameControlsLlvmDeclarationAndCallTarget()
    {
        var result = Compile(
            """
            module Demo

            [LinkName("native_add_i32")]
            unsafe ffi(c) fn i32[min max] AddAlias(i32[min max] left, i32[min max] right);

            export unsafe fn i32[min max] main(i32[min max] value)
            {
                return AddAlias(value, 1);
            }
            """);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvm(result);

        Assert.Contains("declare i32 @native_add_i32(i32, i32) nounwind", llvm);
        Assert.Contains("call i32 @native_add_i32(i32 %arg_value", llvm);
        Assert.DoesNotContain("@AddAlias", llvm, StringComparison.Ordinal);
    }

    [Fact]
    public void FfiLinkNameControlsFunctionAddressSymbol()
    {
        var result = Compile(
            """
            module Demo

            [LinkName("native_scale_i32")]
            unsafe ffi(c) fn i32[min max] ScaleAlias(i32[min max] value);

            export unsafe fn i32[min max] main(i32[min max] value)
            {
                stack fnptr<unsafe ffi(c) fn i32[min max](i32[min max])> op = ScaleAlias;
                return op(value);
            }
            """);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);

        Assert.Contains("@native_scale_i32", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("@ScaleAlias", llvm, StringComparison.Ordinal);
    }

    [Fact]
    public void FfiCLayoutAggregatesUseNativeCAbiCarriersWithLinkName()
    {
        var result = Compile(
            """
            module Demo

            [StructLayout(C)]
            struct Color
            {
                u8[0 max] R;
                u8[0 max] G;
                u8[0 max] B;
                u8[0 max] A;
            }

            [StructLayout(C)]
            struct Image
            {
                rawmutptr<i8[min max]> Data;
                i32[min max] Width;
                i32[min max] Height;
                i32[min max] Mipmaps;
                i32[min max] Format;
            }

            [LinkName("ClearBackground")]
            unsafe ffi(c) fn void NativeClear(Color color);

            [LinkName("LoadImage")]
            unsafe ffi(c) fn Image NativeLoad(ascii fileName);

            export unsafe fn i32[min max] main()
            {
                stack Color color = new Color()
                {
                    R = 1,
                    G = 2,
                    B = 3,
                    A = 4
                };

                NativeClear(color);

                stack Image image = NativeLoad("asset.png");
                return image.Width;
            }
            """);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvm(result);

        Assert.Contains("declare void @ClearBackground(i32) nounwind", llvm);
        Assert.Contains("declare void @LoadImage(ptr noalias sret(%Image)", llvm);
        Assert.Contains("call void @ClearBackground(i32", llvm);
        Assert.Contains("call void @LoadImage(ptr noalias sret(%Image)", llvm);
        Assert.DoesNotContain("@NativeClear", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("@NativeLoad", llvm, StringComparison.Ordinal);
    }

    [Fact]
    public void FfiCLayoutVector2UsesSysVVectorCarrierForReturnAndParameters()
    {
        var result = Compile(
            """
            module Demo

            [StructLayout(C)]
            struct Vector2
            {
                f32 X;
                f32 Y;
            }

            [LinkName("GetMonitorPosition")]
            unsafe ffi(c) fn Vector2 NativeGet(i32[min max] monitor);

            [LinkName("DrawLineV")]
            unsafe ffi(c) fn void NativeDraw(Vector2 start, Vector2 end);

            export unsafe fn i32[min max] main()
            {
                stack Vector2 position = NativeGet(0);
                NativeDraw(position, position);
                return 0;
            }
            """);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvm(result);

        Assert.Contains("declare <2 x float> @GetMonitorPosition(i32) nounwind", llvm);
        Assert.Contains("declare void @DrawLineV(<2 x float>, <2 x float>) nounwind", llvm);
        Assert.Contains("call <2 x float> @GetMonitorPosition(i32", llvm);
        Assert.Contains("call void @DrawLineV(<2 x float>", llvm);
        Assert.DoesNotContain("@NativeGet", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("@NativeDraw", llvm, StringComparison.Ordinal);
    }

    [Fact]
    public void FfiCLayoutVector2UsesWin64IntegerCarrierWhenTargetIsWin64()
    {
        var result = Compile(
            """
            module Demo

            [StructLayout(C)]
            struct Vector2
            {
                f32 X;
                f32 Y;
            }

            [LinkName("GetMonitorPosition")]
            unsafe ffi(c) fn Vector2 NativeGet(i32[min max] monitor);

            [LinkName("DrawLineV")]
            unsafe ffi(c) fn void NativeDraw(Vector2 start, Vector2 end);

            export unsafe fn i32[min max] main()
            {
                stack Vector2 position = NativeGet(0);
                NativeDraw(position, position);
                return 0;
            }
            """,
            options: new CompilerOptions(TargetInfo: new LlvmTargetInfo("x86_64-pc-windows-msvc", null)));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvm(result);

        Assert.Contains("target triple = \"x86_64-pc-windows-msvc\"", llvm);
        Assert.Contains("declare i64 @GetMonitorPosition(i32) nounwind", llvm);
        Assert.Contains("declare void @DrawLineV(i64, i64) nounwind", llvm);
        Assert.Contains("call i64 @GetMonitorPosition(i32", llvm);
        Assert.Contains("call void @DrawLineV(i64", llvm);
        Assert.DoesNotContain("declare <2 x float> @GetMonitorPosition", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("@NativeGet", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("@NativeDraw", llvm, StringComparison.Ordinal);
    }

    [Fact]
    public void FfiCLayoutRectangleParameterExpandsToTwoSysVVectorCarriers()
    {
        var result = Compile(
            """
            module Demo

            [StructLayout(C)]
            struct Rectangle
            {
                f32 X;
                f32 Y;
                f32 Width;
                f32 Height;
            }

            [StructLayout(C)]
            struct Color
            {
                u8[0 max] R;
                u8[0 max] G;
                u8[0 max] B;
                u8[0 max] A;
            }

            [LinkName("DrawRectangleRec")]
            unsafe ffi(c) fn void NativeDraw(Rectangle rec, Color tint);

            export unsafe fn i32[min max] main()
            {
                stack Rectangle rec = new Rectangle()
                {
                    X = 1.0,
                    Y = 2.0,
                    Width = 3.0,
                    Height = 4.0
                };
                stack Color color = new Color()
                {
                    R = 255,
                    G = 64,
                    B = 32,
                    A = 255
                };
                NativeDraw(rec, color);
                return 0;
            }
            """);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvm(result);

        Assert.Contains("declare void @DrawRectangleRec(<2 x float>, <2 x float>, i32) nounwind", llvm);
        Assert.Contains("call void @DrawRectangleRec(<2 x float>", llvm);
        Assert.DoesNotContain("@NativeDraw", llvm, StringComparison.Ordinal);
    }

    [Fact]
    public void FfiCLayoutVector3ReturnUsesSysVCarrierStruct()
    {
        var result = Compile(
            """
            module Demo

            [StructLayout(C)]
            struct Vector3
            {
                f32 X;
                f32 Y;
                f32 Z;
            }

            [LinkName("GetCameraPosition")]
            unsafe ffi(c) fn Vector3 NativeGet();

            export unsafe fn i32[min max] main()
            {
                stack Vector3 position = NativeGet();
                return 0;
            }
            """);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvm(result);

        Assert.Contains("declare { <2 x float>, float } @GetCameraPosition() nounwind", llvm);
        Assert.Contains("call { <2 x float>, float } @GetCameraPosition()", llvm);
        Assert.DoesNotContain("@NativeGet", llvm, StringComparison.Ordinal);
    }

    [Fact]
    public void FfiCLayoutLargeAggregateUsesIndirectSysVCarriers()
    {
        var result = Compile(
            """
            module Demo

            [StructLayout(C)]
            struct Matrix
            {
                f32 M0;
                f32 M1;
                f32 M2;
                f32 M3;
                f32 M4;
                f32 M5;
                f32 M6;
                f32 M7;
                f32 M8;
                f32 M9;
                f32 M10;
                f32 M11;
                f32 M12;
                f32 M13;
                f32 M14;
                f32 M15;
            }

            [LinkName("MakeMatrix")]
            unsafe ffi(c) fn Matrix NativeMake();

            [LinkName("TakeMatrix")]
            unsafe ffi(c) fn void NativeTake(Matrix matrix);

            export unsafe fn i32[min max] main()
            {
                stack Matrix matrix = NativeMake();
                NativeTake(matrix);
                return 0;
            }
            """);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvm(result);

        Assert.Contains("declare void @MakeMatrix(ptr noalias sret(%Matrix)", llvm);
        Assert.Contains("declare void @TakeMatrix(ptr nonnull byval(%Matrix)", llvm);
        Assert.Contains("call void @MakeMatrix(ptr noalias sret(%Matrix)", llvm);
        Assert.Contains("call void @TakeMatrix(ptr nonnull byval(%Matrix)", llvm);
        Assert.DoesNotContain("@NativeMake", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("@NativeTake", llvm, StringComparison.Ordinal);
    }

    [Fact]
    public void FfiCLayoutSixteenByteIntegerAggregateUsesSplitSysVIntegerCarriers()
    {
        var result = Compile(
            """
            module Demo

            [StructLayout(C)]
            struct Pair
            {
                u64[0 max] Left;
                u64[0 max] Right;
            }

            [LinkName("GetPair")]
            unsafe ffi(c) fn Pair NativeGet();

            [LinkName("TakePair")]
            unsafe ffi(c) fn void NativeTake(Pair pair);

            export unsafe fn i32[min max] main()
            {
                stack Pair pair = NativeGet();
                NativeTake(pair);
                return 0;
            }
            """);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvm(result);

        Assert.Contains("declare { i64, i64 } @GetPair() nounwind", llvm);
        Assert.Contains("declare void @TakePair(i64, i64) nounwind", llvm);
        Assert.Contains("call { i64, i64 } @GetPair()", llvm);
        Assert.Contains("call void @TakePair(i64", llvm);
        Assert.DoesNotContain("[2 x i64] @GetPair", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("@NativeGet", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("@NativeTake", llvm, StringComparison.Ordinal);
    }

    [Fact]
    public void IncompatibleFfiDeclarationsForSameLinkNameAreRejectedBeforeLlvmEmission()
    {
        var result = Compile(
            """
            module Demo

            [LinkName("native_read")]
            unsafe ffi(c) fn i32[min max] ReadI32(i32[min max] value);

            [LinkName("native_read")]
            unsafe ffi(c) fn i64[min max] ReadI64(i64[min max] value);
            """,
            new CompilerOptions(StopAfterPassId: "lower-abi"));

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK4122"
                && diagnostic.Message.Contains("native_read", StringComparison.Ordinal)
                && diagnostic.Message.Contains("incompatible LLVM ABI signatures", StringComparison.Ordinal));
    }

    [Fact]
    public void ExplicitFfiAbiModifiersEmitLlvmCallingConventions()
    {
        var result = Compile(
            """
            module Demo

            unsafe ffi(win64) fn i32[min max] NativeRead(i32[min max] value);

            export unsafe fn i32[min max] main(i32[min max] value)
            {
                return NativeRead(value);
            }
            """,
            new CompilerOptions(
                TargetInfo: new LlvmTargetInfo("x86_64-pc-windows-msvc", null)));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvm(result);

        Assert.Contains("declare win64cc i32 @NativeRead(i32) nounwind", llvm);
        Assert.Contains("define i32 @main(i32 %arg_value)", llvm);
        Assert.Contains("call win64cc i32 @NativeRead(i32 %arg_value)", llvm);
        Assert.DoesNotContain("call fastcc i32 @NativeRead", llvm);
    }

    [Fact]
    public void PlatformSelectedFfiAbiModifiersResolvePerTarget()
    {
        var result = Compile(
            """
            module Demo

            unsafe ffi(platform(windows.x64: win64, linux.x64: sysv, default: c)) fn i32[min max] NativeRead(i32[min max] value);

            export unsafe fn i32[min max] main(i32[min max] value)
            {
                return NativeRead(value);
            }
            """,
            new CompilerOptions(
                TargetInfo: new LlvmTargetInfo("x86_64-unknown-linux-gnu", null)));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvm(result);

        Assert.Contains("declare x86_64_sysvcc i32 @NativeRead(i32) nounwind", llvm);
        Assert.Contains("call x86_64_sysvcc i32 @NativeRead(i32 %arg_value)", llvm);
        Assert.DoesNotContain("call fastcc i32 @NativeRead", llvm);
    }

    [Theory]
    [InlineData("x86_64-unknown-linux-gnu", "declare i64 @NativeLong(i64) nounwind", "call i64 @NativeLong(i64 1)")]
    [InlineData("x86_64-pc-windows-msvc", "declare i32 @NativeLong(i64) nounwind", "call i32 @NativeLong(i64 1)")]
    public void SystemCPrimitiveAliasesLowerToTargetLlvmTypes(
        string targetTriple,
        string expectedNativeLongDeclaration,
        string expectedNativeLongCall)
    {
        var result = Compile(
            """
            module Demo

            unsafe ffi(c) fn System.C.c_long NativeLong(System.C.c_size_t bytes);
            unsafe ffi(c) fn void NativeFree(rawmutptr<System.C.c_void> ptr);

            unsafe fn i32[min max] Run(rawmutptr<System.C.c_void> ptr)
            {
                NativeFree(ptr);
                return (i32[min max])NativeLong(1);
            }
            """,
            new CompilerOptions(
                TargetInfo: new LlvmTargetInfo(targetTriple, null)));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvm(result);

        Assert.Contains(expectedNativeLongDeclaration, llvm);
        Assert.Contains(expectedNativeLongCall, llvm);
        Assert.Contains("declare void @NativeFree(ptr) nounwind", llvm);
        Assert.Contains("call void @NativeFree(ptr %arg_ptr)", llvm);
    }

    [Fact]
    public void ExplicitFfiFunctionPointerAbiEmitsIndirectCallConvention()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn i32[min max] Apply(fnptr<ffi(win64) fn i32[min max](i32[min max])> op, i32[min max] value)
            {
                return op(value);
            }
            """,
            new CompilerOptions(
                TargetInfo: new LlvmTargetInfo("x86_64-pc-windows-msvc", null)));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var applyBody = ExtractDefinitionBody(GetLlvmRaw(result), "Apply");

        Assert.Contains("call win64cc i32 %arg_op(i32 %arg_value)", applyBody, StringComparison.Ordinal);
        Assert.DoesNotContain("call fastcc i32 %arg_op", applyBody, StringComparison.Ordinal);
    }

    [Fact]
    public void UnsafeFfiFunctionItemsPromoteToUnsafeFfiFunctionPointerArguments()
    {
        var result = Compile(
            """
            module Demo

            unsafe ffi(win64) fn i32[min max] Callback(i32[min max] value);
            unsafe ffi(win64) fn i32[min max] Register(fnptr<unsafe ffi(win64) fn i32[min max](i32[min max])> callback, i32[min max] value);

            unsafe fn i32[min max] Run(i32[min max] value)
            {
                return Register(Callback, value);
            }
            """,
            new CompilerOptions(
                TargetInfo: new LlvmTargetInfo("x86_64-pc-windows-msvc", null)));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvm(result);

        Assert.Contains("declare win64cc i32 @Callback(i32) nounwind", llvm);
        Assert.Matches(@"declare win64cc i32 @Register\(ptr(?: [^,\)]*)?, i32\) nounwind", llvm);
        Assert.Matches(@"call win64cc i32 @Register\(ptr(?: [^,\)]*)? @Callback, i32 %arg_value\)", llvm);
    }

    [Fact]
    public void InternalStringFunctionsUseConcreteStringAbi()
    {
        var result = Compile(
            """
            module Demo

            unsafe finite law ascii Echo(ascii text)
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
                    return text;
                }

                return text;
            }

            unsafe finite law ascii Run(ascii input)
            {
                return Echo(input);
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("%stark_ascii = type { ptr, i64 }", llvm);
        Assert.Contains("define fastcc %stark_ascii @Echo(%stark_ascii %arg_text)", llvm);
        Assert.Contains("ret %stark_ascii %arg_text", llvm);
        Assert.Contains("define fastcc %stark_ascii @Run(%stark_ascii %arg_input)", llvm);
        Assert.Contains("call fastcc %stark_ascii @Echo(%stark_ascii %arg_input)", llvm);
    }

    [Fact]
    public void CharacterLiteralsEmitConcreteStringValues()
    {
        var result = Compile(
            """
            module Demo

            unsafe finite law ascii AsciiChar()
            {
                return 'a';
            }

            unsafe finite law unicode UnicodeChar()
            {
                return (unicode)'\u03B1';
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("%stark_ascii = type { ptr, i64 }", llvm);
        Assert.Contains("%stark_unicode = type { ptr, i64 }", llvm);
        Assert.Contains("@.str.0 = private unnamed_addr constant [2 x i8] c\"a\\00\"", llvm);
        Assert.Contains("private unnamed_addr constant [2 x i32] [i32 945, i32 0]", llvm);
        Assert.Contains("define fastcc %stark_ascii @AsciiChar()", llvm);
        Assert.Contains("define fastcc %stark_unicode @UnicodeChar()", llvm);
        Assert.Contains("ret %stark_ascii { ptr getelementptr inbounds nuw ([2 x i8], ptr @.str.0, i32 0, i32 0), i64 1 }", llvm);
        Assert.Contains("ret %stark_unicode { ptr getelementptr inbounds nuw ([2 x i32], ptr @.str.", llvm);
        Assert.Contains(", i64 1 }", llvm);
    }

    [Fact]
    public void UnicodeStringLiteralsUseUtf32CodeUnitLengthInRuntimeValues()
    {
        var result = Compile(
            """
            module Demo

            unsafe finite law unicode Greek()
            {
                return (unicode)"\u03B1";
            }

            unsafe finite law unicode Accented()
            {
                return (unicode)"\xC9";
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("%stark_unicode = type { ptr, i64 }", llvm);
        Assert.Contains("private unnamed_addr constant [2 x i32] [i32 945, i32 0]", llvm);
        Assert.Contains("private unnamed_addr constant [2 x i32] [i32 201, i32 0]", llvm);
        Assert.Equal(2, CountOccurrences(llvm, "ret %stark_unicode { ptr getelementptr inbounds nuw ([2 x i32], ptr @.str."));
        Assert.Equal(2, CountOccurrences(llvm, ", i64 1 }"));
    }

    [Fact]
    public void EquivalentAsciiLiteralPayloadsShareOneHelperGlobalAcrossGlobalAndFunctionUses()
    {
        var result = Compile(
            """
            module Demo

            const ascii Label = '\x41';

            unsafe finite law ascii Run()
            {
                return "A";
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Equal(1, CountOccurrences(llvm, "private unnamed_addr constant [2 x i8] c\"A\\00\""));
        Assert.Equal(2, CountOccurrences(llvm, "getelementptr inbounds nuw ([2 x i8], ptr @.str."));
    }

    [Fact]
    public void EquivalentUnicodeLiteralPayloadsShareOneHelperGlobal()
    {
        var result = Compile(
            """
            module Demo

            unsafe finite law unicode One()
            {
                return (unicode)"\u03B1";
            }

            unsafe finite law unicode Two()
            {
                return (unicode)'\u03B1';
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Equal(1, CountOccurrences(llvm, "private unnamed_addr constant [2 x i32] [i32 945, i32 0]"));
        Assert.Equal(2, CountOccurrences(llvm, "getelementptr inbounds nuw ([2 x i32], ptr @.str."));
    }

    [Fact]
    public void PlainFnsEmitInferredPureAndFiniteAttributes()
    {
        var result = Compile(
            """
            module Demo

            struct Box
            {
                i32[min max] Value;
            }

            unsafe fn i32[min max] Add(i32[min max] left, i32[min max] right)
            {
                return left + right;
            }

            unsafe fn i32[min max] Read(borrow Box box)
            {
                return box.Value;
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("define fastcc i32 @Add(i32 %arg_left, i32 %arg_right) nounwind willreturn mustprogress nosync nofree memory(none)", llvm);
        Assert.Contains("memory(argmem: read)", llvm);
    }

    [Fact]
    public void ClosedWorldNoRecurseEmitsForKnownNonRecursiveBodiesOnly()
    {
        var result = Compile(
            """
            module Demo

            unsafe ffi fn i32[min max] Native(i32[min max] value);

            unsafe fn i32[min max] Leaf(i32[min max] value)
            {
                return value;
            }

            unsafe fn i32[min max] Caller(i32[min max] value)
            {
                return Leaf(value);
            }

            unsafe fn i32[min max] SelfRecursive(i32[min max] value)
            {
                return SelfRecursive(value);
            }

            unsafe fn i32[min max] MutualA(i32[min max] value)
            {
                return MutualB(value);
            }

            unsafe fn i32[min max] MutualB(i32[min max] value)
            {
                return MutualA(value);
            }

            unsafe fn i32[min max] Apply(
                fnptr<unsafe fn i32[min max](i32[min max])> op,
                i32[min max] value)
            {
                return op(value);
            }

            unsafe fn i32[min max] CallsApply(i32[min max] value)
            {
                return Apply(Leaf, value);
            }

            unsafe fn i32[min max] CallsFfi(i32[min max] value)
            {
                return Native(value);
            }
            """);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);

        Assert.Contains("norecurse", ExtractDefinitionHeader(llvm, "Leaf"), StringComparison.Ordinal);
        Assert.Contains("norecurse", ExtractDefinitionHeader(llvm, "Caller"), StringComparison.Ordinal);
        Assert.DoesNotContain("norecurse", ExtractDefinitionHeader(llvm, "SelfRecursive"), StringComparison.Ordinal);
        Assert.DoesNotContain("norecurse", ExtractDefinitionHeader(llvm, "MutualA"), StringComparison.Ordinal);
        Assert.DoesNotContain("norecurse", ExtractDefinitionHeader(llvm, "MutualB"), StringComparison.Ordinal);
        Assert.DoesNotContain("norecurse", ExtractDefinitionHeader(llvm, "Apply"), StringComparison.Ordinal);
        Assert.DoesNotContain("norecurse", ExtractDefinitionHeader(llvm, "CallsApply"), StringComparison.Ordinal);
        Assert.DoesNotContain("norecurse", ExtractDefinitionHeader(llvm, "CallsFfi"), StringComparison.Ordinal);
    }

    [Fact]
    public void BorrowParametersEmitCapturesAttributes()
    {
        var result = Compile(
            """
            module Demo

            struct Box
            {
                i32[min max] Value;
            }

            finite law i32[min max] Read(borrow Box value)
            {
                return value.Value;
            }

            unsafe fn retborrow Box Echo(retborrow Box value)
            {
                return value;
            }

            unsafe fn storeborrow Box Hold(storeborrow Box value)
            {
                return value;
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvmRaw(result);
        var readHeader = ExtractDefinitionHeader(llvm, "Read");
        var echoHeader = ExtractDefinitionHeader(llvm, "Echo");
        var holdHeader = ExtractDefinitionHeader(llvm, "Hold");

        Assert.Contains("ptr noundef nonnull noalias readonly captures(none) dereferenceable(4) align 4 %arg_value", readHeader);
        Assert.DoesNotContain("captures(address", readHeader, StringComparison.Ordinal);
        Assert.DoesNotContain("captures(ret:", readHeader, StringComparison.Ordinal);
        Assert.Contains("define fastcc noundef nonnull dereferenceable(4) align 4 ptr @Echo(ptr noundef", echoHeader);
        Assert.Contains("captures(ret: address, read_provenance)", echoHeader);
        Assert.DoesNotContain("captures(none)", echoHeader, StringComparison.Ordinal);
        Assert.Contains("define fastcc noundef nonnull dereferenceable(4) align 4 ptr @Hold(ptr noundef", holdHeader);
        Assert.Contains("captures(address, read_provenance)", holdHeader);
        Assert.DoesNotContain("captures(none)", holdHeader, StringComparison.Ordinal);
        Assert.DoesNotContain("captures(ret:", holdHeader, StringComparison.Ordinal);
    }

    [Fact]
    public void StoreBorrowParametersEmitEscapingCaptureAttributes()
    {
        var result = Compile(
            """
            module Demo

            struct Box
            {
                i32[min max] Value;
            }

            unsafe fn storeborrow Box Hold(storeborrow Box value)
            {
                return value;
            }
            """);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);
        var holdHeader = ExtractDefinitionHeader(llvm, "Hold");

        Assert.Contains("define fastcc noundef nonnull dereferenceable(4) align 4 ptr @Hold(ptr noundef", holdHeader);
        Assert.Contains("captures(address, read_provenance)", holdHeader);
        Assert.DoesNotContain("captures(none)", holdHeader, StringComparison.Ordinal);
        Assert.DoesNotContain("captures(ret:", holdHeader, StringComparison.Ordinal);
    }

    [Fact]
    public void RetborrowScalarReturnsUsePointerAbiAndCanBeWrittenThrough()
    {
        var result = Compile(
            """
            module Demo

            struct Box
            {
                i32[min max] Value;

                unsafe fn retborrow mut i32[min max] Field(mut borrow Box self)
                {
                    return self.Value;
                }
            }

            unsafe fn i32[min max] Run()
            {
                stack mut Box box = new Box()
                {
                    Value = 0
                };
                box.Field() = 7;
                return box.Field();
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvmRaw(result);
        var fieldHeader = ExtractDefinitionHeader(llvm, "Box_Field");

        Assert.Contains("define fastcc noundef nonnull dereferenceable(4) align 4 ptr @Box_Field(ptr noundef", fieldHeader);
        Assert.Contains("call fastcc noundef nonnull dereferenceable(4) align 4 ptr @Box_Field(", llvm);
        Assert.Contains("ret ptr", llvm);
        Assert.Contains("store i32 7, ptr", llvm);
        Assert.Contains("load i32, ptr", llvm);
    }

    [Fact]
    public void ScalarAbiValuesEmitNoundefOnParametersAndReturns()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn i32[min max] Run(i32[min max] value, bool allow)
            {
                if (allow)
                {
                    return value;
                }

                return 0;
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvmRaw(result);
        var header = ExtractDefinitionHeader(llvm, "Run");

        Assert.Contains("define fastcc noundef i32 @Run(i32 noundef %arg_value, i1 noundef %arg_allow)", header);
        Assert.Contains("ret i32", llvm);
    }

    [Fact]
    public void BorrowedPointerAbiValuesEmitNoundefOnParametersAndReturns()
    {
        var result = Compile(
            """
            module Demo

            struct Box
            {
                i32[min max] Value;
            }

            unsafe fn i32[min max] Read(borrow Box box)
            {
                return box.Value;
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvmRaw(result);
        var header = ExtractDefinitionHeader(llvm, "Read");

        Assert.Contains("define fastcc noundef i32 @Read(ptr noundef", header);
        Assert.Contains("ret i32", llvm);
    }

    [Fact]
    public void FullyDefinedNonFfiAbiSurfacesEmitNoundef()
    {
        var result = Compile(
            """
            module Demo

            record Pair(u8[0 10] Left, u8[0 10] Right)
            {
            }
            record Big(
                i64[min max] A,
                i64[min max] B,
                i64[min max] C)
                {
                }

            unsafe fn rawptr<i8[min max]> Pick(rawptr<i8[min max]> ptr)
            {
                return ptr;
            }

            unsafe fn ascii Echo(ascii text)
            {
                return text;
            }

            unsafe fn Pair Keep(Pair pair)
            {
                return pair;
            }

            unsafe fn Big Copy(Big value)
            {
                return value;
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);

        Assert.Contains("define fastcc noundef ptr @Pick(ptr noundef", ExtractDefinitionHeader(llvm, "Pick"));
        Assert.Contains("define fastcc noundef %stark_ascii @Echo(%stark_ascii noundef %arg_text)", ExtractDefinitionHeader(llvm, "Echo"));
        Assert.Contains("define fastcc noundef %Pair @Keep(%Pair noundef %arg_pair)", ExtractDefinitionHeader(llvm, "Keep"));

        var copyHeader = ExtractDefinitionHeader(llvm, "Copy");
        Assert.Contains("define fastcc void @Copy(ptr noundef noalias sret(%Big)", copyHeader);
        Assert.Contains("ptr noundef nonnull byval(%Big)", copyHeader);
    }

    [Fact]
    public void PointerAbiContractsDistinguishSafeBorrowsAndNullableRawPointers()
    {
        var result = Compile(
            """
            module Demo

            struct Box
            {
                i32[min max] Value;
            }

            unsafe fn i32[min max] ReadBorrow(borrow Box box)
            {
                return box.Value;
            }

            unsafe fn i32[min max] ReadView(borrow i32[min max][] view, i32[min max] index)
            {
                return view[index];
            }

            unsafe fn void RawInternal(rawptr<Box> ptr)
            {
                return;
            }

            unsafe ffi fn void RawForeign(rawptr<Box> ptr);
            """);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);

        Assert.Contains(
            "define fastcc noundef i32 @ReadBorrow(ptr noundef nonnull noalias readonly captures(none) dereferenceable(4) align 4 %arg_box)",
            ExtractDefinitionHeader(llvm, "ReadBorrow"));
        Assert.Contains(
            "define fastcc noundef i32 @ReadView(ptr noundef nonnull noalias readonly captures(none) dereferenceable(16) align 8 %arg_view, i32 noundef %arg_index)",
            ExtractDefinitionHeader(llvm, "ReadView"));

        var rawHeader = ExtractDefinitionHeader(llvm, "RawInternal");
        Assert.Contains("ptr noundef readonly captures(none) %arg_ptr", rawHeader);
        Assert.DoesNotContain("nonnull", rawHeader);
        Assert.DoesNotContain("dereferenceable", rawHeader);
        Assert.DoesNotContain("dereferenceable_or_null", rawHeader);

        Assert.Contains("declare void @RawForeign(ptr readonly)", llvm);
        Assert.DoesNotContain("declare void @RawForeign(ptr nonnull", llvm);
        Assert.DoesNotContain("declare void @RawForeign(ptr dereferenceable", llvm);
        Assert.DoesNotContain("declare void @RawForeign(ptr dereferenceable_or_null", llvm);
    }

    [Fact]
    public void RawMutablePointerDeclarationsMayReadAndWriteArgumentMemory()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn void Fill(rawmutptr<i32[min max]> ptr);

            unsafe fn void Run(rawmutptr<i32[min max]> ptr)
            {
                Fill(ptr);
                return;
            }
            """);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);
        var fillDeclaration = llvm
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Single(line => line.StartsWith("declare fastcc void @Fill(", StringComparison.Ordinal));

        Assert.Contains("declare fastcc void @Fill(ptr noundef)", fillDeclaration);
        Assert.DoesNotContain("memory(argmem: read)", fillDeclaration);
        Assert.DoesNotContain("readonly", fillDeclaration);
        Assert.DoesNotContain("writeonly", fillDeclaration);
    }

    [Fact]
    public void FfiRawMutablePointerCallsPropagateWrapperArgumentWrites()
    {
        var result = Compile(
            """
            module Demo

            unsafe ffi fn i32[min max] Fill(rawmutptr<i32[min max]> ptr);

            unsafe fn i32[min max] Run(rawmutptr<i32[min max]> ptr)
            {
                return Fill(ptr);
            }
            """);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);
        var fillDeclaration = llvm
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Single(line => line.StartsWith("declare i32 @Fill(", StringComparison.Ordinal));
        var runHeader = ExtractDefinitionHeader(llvm, "Run");

        Assert.Contains("declare i32 @Fill(ptr)", fillDeclaration);
        Assert.DoesNotContain("readonly", fillDeclaration);
        Assert.DoesNotContain("writeonly", fillDeclaration);
        Assert.DoesNotContain("readonly", runHeader);
        Assert.DoesNotContain("writeonly", runHeader);
        Assert.DoesNotContain("memory(read, argmem: none)", runHeader);
    }

    [Fact]
    public void ConstrainedIntegerLoadsAndCallResultsEmitRangeMetadata()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn u8[0 10] Bounded()
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

                if (acc > 0)
                {
                    return 7;
                }

                return 3;
            }

            unsafe fn u8[0 10] ReadThroughPointer(u8[0 10] input)
            {
                stack mut u8[0 10] value = input;
                stack rawmutptr<u8[0 10]> ptr = &value;
                return *ptr;
            }

            unsafe fn u8[0 10] CallBounded()
            {
                return Bounded();
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);

        Assert.Matches(@"load i8, ptr (?:%v\d+|%slot_value), !range !\d+", llvm);
        Assert.Matches(@"call fastcc i8 @Bounded\(\), !range !\d+", llvm);
        Assert.Contains("!{i8 0, i8 11}", llvm);
    }

    [Fact]
    public void ConstrainedIntegerAbiSurfacesEmitRangeAttributes()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn u8[0 10] Bounded(u8[0 10] input)
            {
                stack i32[min max] a = input + (input / 3);
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
                if (m < -2000000000)
                {
                    return 3;
                }

                return 7;
            }

            unsafe fn u8[0 10] CallBounded(u8[0 10] input)
            {
                return Bounded(input);
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);

        Assert.Contains("define fastcc noundef range(i8 0, 11) i8 @Bounded(i8 noundef range(i8 0, 11) %arg_input)", llvm);
        Assert.Contains("define fastcc noundef range(i8 0, 11) i8 @CallBounded(i8 noundef range(i8 0, 11) %arg_input)", llvm);
        Assert.Matches(@"call fastcc i8 @Bounded\(i8 range\(i8 0, 11\) %arg_input\), !range !\d+", llvm);
        Assert.Contains("!{i8 0, i8 11}", llvm);
    }

    [Fact]
    public void IntegerRangeFactsCoverGlobalsAndNarrowActualCallArguments()
    {
        var result = Compile(
            """
            module Demo

            static mut u8[0 10] GlobalValue = 3;

            unsafe noinline fn u8[0 max] UseWide(u8[0 max] value)
            {
                return value;
            }

            unsafe fn u8[0 max] CallWithNarrowActual(u8[0 10] value)
            {
                return UseWide(value);
            }

            unsafe fn u8[0 10] ReadGlobal()
            {
                return GlobalValue;
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);
        var useWideHeader = ExtractDefinitionHeader(llvm, "UseWide");
        var callWithNarrowActualBody = ExtractDefinitionBody(llvm, "CallWithNarrowActual");
        var readGlobalBody = ExtractDefinitionBody(llvm, "ReadGlobal");

        Assert.DoesNotContain("range(i8 0, 11) %arg_value", useWideHeader, StringComparison.Ordinal);
        Assert.Contains("call fastcc i8 @UseWide(i8 range(i8 0, 11) %arg_value)", callWithNarrowActualBody, StringComparison.Ordinal);
        Assert.Matches(@"load i8, ptr @GlobalValue(?:, align \d+)?, !range !\d+", readGlobalBody);
        Assert.Contains("!{i8 0, i8 11}", llvm);
    }

    [Fact]
    public void BranchRefinedIntegerComparisonsEmitTargetedAssumes()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn i32[min max] Range(rawmutptr<i32[min max]> sink, i32[min max] value)
            {
                if (value >= 0)
                {
                    *sink = value;
                    return value;
                }

                return 0;
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);

        Assert.Contains("declare void @llvm.assume(i1 noundef)", llvm);
        Assert.Matches(@"call void @llvm\.assume\(i1 %v\d+\)", llvm);
        Assert.Matches(@"%abi_assume_not_\d+_\d+ = xor i1 %v\d+, true", llvm);
        Assert.Matches(@"call void @llvm\.assume\(i1 %abi_assume_not_\d+_\d+\)", llvm);
    }

    [Fact]
    public void BranchRefinedRawPointerNullChecksEmitNonnullAssumeBundle()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn i32[min max] Read(rawptr<i32[min max]> ptr)
            {
                if (ptr != null)
                {
                    return *ptr;
                }

                return 0;
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);
        var header = ExtractDefinitionHeader(llvm, "Read");

        Assert.DoesNotContain("nonnull", header);
        Assert.Contains("declare void @llvm.assume(i1 noundef)", llvm);
        Assert.Contains(@"call void @llvm.assume(i1 true) [""nonnull""(ptr %arg_ptr)]", llvm);
    }

    [Fact]
    public void BranchRefinedPointerEqualityToKnownAddressEmitsAlignmentAssumeBundle()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn i64[min max] ReadIfSame(rawptr<i64[min max]> ptr)
            {
                stack i64[min max] value = 9;
                if (ptr == &value)
                {
                    return *ptr;
                }

                return 0;
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);

        Assert.Contains("declare void @llvm.assume(i1 noundef)", llvm);
        Assert.Contains(@"call void @llvm.assume(i1 true) [""nonnull""(ptr %arg_ptr), ""align""(ptr %arg_ptr, i64 8)]", llvm);
    }

    [Fact]
    public void BoundaryFactsAndPlainBoolBranchesDoNotEmitAssumeIntrinsic()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn u8[0 10] BoundaryOnly(u8[0 10] value)
            {
                return value;
            }

            unsafe fn i32[min max] Plain(bool flag, i32[min max] value)
            {
                if (flag)
                {
                    return value;
                }

                return 0;
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);

        Assert.DoesNotContain("@llvm.assume", llvm);
        Assert.Contains("define fastcc noundef range(i8 0, 11) i8 @BoundaryOnly(i8 noundef range(i8 0, 11) %arg_value)", llvm);
    }

    [Fact]
    public void SignedConstrainedIntegerLoadsEmitWrappingRangeMetadata()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn i8[-10 10] ReadThroughPointer(i8[-10 10] input)
            {
                stack mut i8[-10 10] value = input;
                stack rawmutptr<i8[-10 10]> ptr = &value;
                return *ptr;
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);

        Assert.Matches(@"load i8, ptr (?:%v\d+|%slot_value), !range !\d+", llvm);
        Assert.Contains("!{i8 -10, i8 11}", llvm);
    }

    [Fact]
    public void MemoryAttributesDistinguishArgumentAndOtherMemoryEffects()
    {
        var result = Compile(
            """
            module Demo

            struct Box
            {
                i32[min max] Value;
            }

            static mut i32[min max] Counter = 0;

            unsafe fn i32[min max] ReadGlobal()
            {
                return Counter;
            }

            unsafe fn void TouchArg(borrow mut Box box)
            {
                box.Value = 1;
                return;
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("define fastcc i32 @ReadGlobal()", llvm);
        Assert.Contains("memory(read, argmem: none)", llvm);
        Assert.Contains("define fastcc void @TouchArg(ptr nonnull noalias writeonly captures(none) dereferenceable(4) align 4 %arg_box)", llvm);
        Assert.Contains("memory(argmem: readwrite)", llvm);
        Assert.DoesNotContain("load %Box, ptr %arg_box", llvm);
    }

    [Fact]
    public void FunctionMemoryEffectsUseStructuredMemoryAttributesInsteadOfLegacyForms()
    {
        var result = Compile(
            """
            module Demo

            struct Box
            {
                i32[min max] Value;
            }

            static mut i32[min max] Counter = 0;

            unsafe fn i32[min max] Add(i32[min max] left, i32[min max] right)
            {
                return left + right;
            }

            unsafe fn i32[min max] ReadGlobal()
            {
                return Counter;
            }

            unsafe fn void TouchArg(borrow mut Box box)
            {
                box.Value = 1;
                return;
            }
            """);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);

        var addHeader = ExtractDefinitionHeader(llvm, "Add");
        Assert.Contains("memory(none)", addHeader, StringComparison.Ordinal);
        Assert.DoesNotContain("readnone", addHeader, StringComparison.Ordinal);
        Assert.DoesNotContain("readonly", addHeader, StringComparison.Ordinal);
        Assert.DoesNotContain("argmemonly", addHeader, StringComparison.Ordinal);

        var readGlobalHeader = ExtractDefinitionHeader(llvm, "ReadGlobal");
        Assert.Contains("memory(read, argmem: none)", readGlobalHeader, StringComparison.Ordinal);
        Assert.DoesNotContain("readnone", readGlobalHeader, StringComparison.Ordinal);
        Assert.DoesNotContain("readonly", readGlobalHeader, StringComparison.Ordinal);
        Assert.DoesNotContain("argmemonly", readGlobalHeader, StringComparison.Ordinal);

        var touchArgHeader = ExtractDefinitionHeader(llvm, "TouchArg");
        Assert.Contains("memory(argmem: readwrite)", touchArgHeader, StringComparison.Ordinal);
        Assert.DoesNotContain("readnone", touchArgHeader, StringComparison.Ordinal);
        Assert.DoesNotContain("readonly", touchArgHeader, StringComparison.Ordinal);
        Assert.DoesNotContain("argmemonly", touchArgHeader, StringComparison.Ordinal);
    }

    [Fact]
    public void MemoryAttributesKeepArgumentEffectsWhenSyntheticTemporariesAreNeeded()
    {
        var result = Compile(
            """
            module Demo

            struct Big
            {
                i64[min max] A;
                i64[min max] B;
                i64[min max] C;
                i64[min max] D;
            }

            struct Box
            {
                Big Value;
            }

            unsafe fn Big MakeBig()
            {
                return new Big()
                {
                    A = 1,
                    B = 2,
                    C = 3,
                    D = 4
                };
            }

            unsafe fn void Store(borrow mut Box box)
            {
                box.Value = MakeBig();
                return;
            }
            """);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvm(result);
        var storeHeader = ExtractDefinitionHeader(llvm, "Store");

        Assert.DoesNotContain("argmem: none", storeHeader);
    }

    [Fact]
    public void EscapedTextLiteralsEmitDecodedBytes()
    {
        var result = Compile(
            """
            module Demo

            unsafe finite law ascii Controls()
            {
                return "\0\b\t\n\f\r\\\"\'";
            }

            unsafe finite law ascii HexChar()
            {
                return '\x41';
            }

            unsafe finite law unicode Wide()
            {
                return (unicode)"\xC9";
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvmRaw(result);

        Assert.Contains("@.str.0 = private unnamed_addr constant [10 x i8] c\"\\00\\08\\09\\0A\\0C\\0D\\5C\\22'\\00\"", llvm);
        Assert.Contains("@.str.1 = private unnamed_addr constant [2 x i8] c\"A\\00\"", llvm);
        Assert.Contains("private unnamed_addr constant [2 x i32] [i32 201, i32 0]", llvm);
    }

    [Fact]
    public void FfiStringCallsExtractPointerFromConcreteStringValues()
    {
        var result = Compile(
            """
            module Demo

            unsafe ffi fn i32[min max] puts(ascii text);

            unsafe fn ascii Message()
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
                    return "Hello";
                }

                return "Hello";
            }

            export unsafe fn i32[min max] main()
            {
                stack ascii message = Message();
                puts(message);
                return 0;
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("%stark_ascii = type { ptr, i64 }", llvm);
        Assert.Contains("define fastcc %stark_ascii @Message()", llvm);
        Assert.Contains("define i32 @main()", llvm);
        Assert.Contains("call fastcc %stark_ascii @Message()", llvm);
        Assert.Contains("extractvalue %stark_ascii", llvm);
        Assert.Contains("call i32 @puts(ptr %", llvm);
    }

    [Fact]
    public void StructFieldAccessCanFoldToScalarReturn()
    {
        var result = Compile(
            """
            module Demo

            struct Box
            {
                i32[min max] Value;
            }

            unsafe fn i32[min max] Run()
            {
                stack Box box = new Box()
                {
                    Value = 41
                };
                return box.Value;
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("%Box = type { i32 }", llvm);
        Assert.Contains("ret i32 41", llvm);
        Assert.DoesNotContain("declare fastcc i32 @Run()", llvm);
    }

    [Fact]
    public void FieldAssignmentCanFoldToScalarReturn()
    {
        var result = Compile(
            """
            module Demo

            struct Box
            {
                i32[min max] Value;
            }

            unsafe fn i32[min max] Run()
            {
                stack mut Box box = new Box()
                {
                    Value = 1
                };
                box.Value = 2;
                return box.Value;
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("%Box = type { i32 }", llvm);
        Assert.Contains("ret i32 2", llvm);
        Assert.DoesNotContain("; LLVM body emission pending for Run", llvm);
    }

    [Fact]
    public void RegisterObjectCreationCanFoldToScalarReturn()
    {
        var result = Compile(
            """
            module Demo

            struct Box
            {
                i32[min max] Value;
            }

            unsafe fn i32[min max] Run()
            {
                register Box box = new Box()
                {
                    Value = 7
                };
                return box.Value;
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("define fastcc i32 @Run()", llvm);
        Assert.Contains("ret i32 7", llvm);
        Assert.DoesNotContain("alloca %Box", llvm);
        Assert.DoesNotContain("; LLVM body emission pending for Run", llvm);
        Assert.DoesNotContain("; LLVM body emission fallback for Run", llvm);
    }

    [Fact]
    public void NonStrictFpFunctionsEmitFastMathFlagsOnBinaryOpsAndCalls()
    {
        var result = Compile(
            """
            module Demo

            noinline unsafe fn f32 Add(f32 left, f32 right)
            {
                return left + right;
            }

            unsafe fn f32 Run(f32 left, f32 right)
            {
                return Add(left, right);
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("fadd fast float %arg_left, %arg_right", llvm);
        Assert.Contains("call fast fastcc float @Add(float %arg_left, float %arg_right)", llvm);
        Assert.DoesNotContain(" strictfp ", llvm);
    }

    [Fact]
    public void TailPositionDirectCallsEmitTailMarkerWhenAbiCompatible()
    {
        var result = Compile(
            """
            module Demo

            noinline unsafe fn i32[min max] Step(i32[min max] value)
            {
                return value + 1;
            }

            unsafe fn i32[min max] Run(i32[min max] value)
            {
                return Step(value);
            }
            """);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvm(result);

        Assert.Contains("define fastcc i32 @Run(i32 %arg_value)", llvm);
        Assert.Contains("tail call fastcc i32 @Step(i32 %arg_value)", llvm);
    }

    [Fact]
    public void NonStrictFpFunctionsEmitFastMathFlagsOnRemainingFloatInstructions()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn f32 Narrow(f64 value)
            {
                return (f32)value;
            }

            unsafe fn f64 Promote(f32 value)
            {
                return (f64)value;
            }

            unsafe fn f32 Negate(f32 value)
            {
                return -value;
            }

            unsafe fn f32 Pick(bool flag, f32 left, f32 right)
            {
                stack mut f32 value = left;
                if (flag)
                {
                    value = right;
                }

                return value;
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);

        Assert.Contains("fptrunc fast double %arg_value to float", llvm);
        Assert.Contains("fpext fast float %arg_value to double", llvm);
        Assert.Contains("fneg fast float %arg_value", llvm);
        Assert.Matches(@"phi fast float \[ %arg_left, %bb\d+ \], \[ %arg_right, %bb\d+ \]", llvm);
    }

    [Fact]
    public void NonStrictFpMultiplyAddExpressionsUseContractionFriendlyIntrinsic()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn f32 Accumulate(f32 acc, f32 left, f32 right)
            {
                return acc + left * right;
            }

            unsafe fn f32 AccumulateProductFirst(f32 acc, f32 left, f32 right)
            {
                return left * right + acc;
            }

            unsafe fn f32 SubtractAddend(f32 acc, f32 left, f32 right)
            {
                return left * right - acc;
            }

            unsafe fn f32 SubtractProduct(f32 acc, f32 left, f32 right)
            {
                return acc - left * right;
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);

        Assert.Contains("declare float @llvm.fmuladd.f32(float, float, float)", llvm);
        Assert.Contains("call fast float @llvm.fmuladd.f32(float %arg_left, float %arg_right, float %arg_acc)", llvm);
        Assert.Contains("fneg fast float %arg_acc", llvm);
        Assert.Matches(@"call fast float @llvm\.fmuladd\.f32\(float %arg_left, float %arg_right, float %abi_fmuladd_neg_\d+\)", llvm);
        Assert.Contains("fneg fast float %arg_left", llvm);
        Assert.Matches(@"call fast float @llvm\.fmuladd\.f32\(float %abi_fmuladd_neg_\d+, float %arg_right, float %arg_acc\)", llvm);
        Assert.DoesNotContain("fadd fast float", llvm);
        Assert.DoesNotContain("@llvm.fma.", llvm);
    }

    [Fact]
    public void StrictFpFunctionsOptOutOfFastMathFlags()
    {
        var result = Compile(
            """
            module Demo

            noinline strictfp fn f32 Add(f32 left, f32 right)
            {
                return left + right;
            }

            strictfp fn f32 Run(f32 left, f32 right)
            {
                return Add(left, right);
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("define fastcc float @Run(float %arg_left, float %arg_right)", llvm);
        Assert.Contains(" strictfp ", llvm);
        Assert.Contains("declare float @llvm.experimental.constrained.fadd.f32(float, float, metadata, metadata)", llvm);
        Assert.Contains("call float @llvm.experimental.constrained.fadd.f32(float %arg_left, float %arg_right, metadata !\"round.dynamic\", metadata !\"fpexcept.strict\") strictfp", llvm);
        Assert.DoesNotContain("fadd fast float %arg_left, %arg_right", llvm);
        Assert.Contains("call fastcc float @Add(float %arg_left, float %arg_right) strictfp", llvm);
        Assert.DoesNotContain("call fast fastcc float @Add(float %arg_left, %arg_right)", llvm);
    }

    [Fact]
    public void StrictFpFunctionPointerCallsComposeWithKindedCallSiteAttributes()
    {
        var result = Compile(
            """
            module Demo

            strictfp finite law f32 Apply(fnptr<finite law f32(f32, f32)> op, f32 left, f32 right)
            {
                return op(left, right);
            }
            """);

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvm(result);

        Assert.Contains(
            "call fastcc float %arg_op(float %arg_left, float %arg_right) nounwind willreturn mustprogress nosync nofree memory(none) strictfp",
            llvm);
        Assert.DoesNotContain("call fast fastcc float %arg_op", llvm);
    }

    [Fact]
    public void StrictFpMultiplyAddExpressionsDoNotUseContractionIntrinsic()
    {
        var result = Compile(
            """
            module Demo

            strictfp fn f32 Accumulate(f32 acc, f32 left, f32 right)
            {
                return acc + left * right;
            }
            """);

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);

        Assert.Contains("declare float @llvm.experimental.constrained.fmul.f32(float, float, metadata, metadata)", llvm);
        Assert.Contains("declare float @llvm.experimental.constrained.fadd.f32(float, float, metadata, metadata)", llvm);
        Assert.Contains("call float @llvm.experimental.constrained.fmul.f32(float %arg_left, float %arg_right, metadata !\"round.dynamic\", metadata !\"fpexcept.strict\") strictfp", llvm);
        Assert.Matches(@"call float @llvm\.experimental\.constrained\.fadd\.f32\(float %arg_acc, float %v\d+, metadata !""round\.dynamic"", metadata !""fpexcept\.strict""\) strictfp", llvm);
        Assert.DoesNotContain("@llvm.fmuladd.", llvm);
        Assert.DoesNotContain("@llvm.fma.", llvm);
    }

    [Fact]
    public void StrictFpConversionsUseConstrainedFloatingPointIntrinsics()
    {
        var result = Compile(
            """
            module Demo

            strictfp fn f64 Run(f32 small, f64 wide, i32[min max] integer)
            {
                stack f64 promoted = (f64)small;
                stack f32 narrowed = (f32)wide;
                stack f64 fromInteger = (f64)integer;
                stack i32[min max] backToInteger = (i32[min max])narrowed;
                return promoted + fromInteger + backToInteger;
            }
            """);

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);

        Assert.Contains("call double @llvm.experimental.constrained.fpext.f64.f32(float %arg_small, metadata !\"fpexcept.strict\") strictfp", llvm);
        Assert.Contains("call float @llvm.experimental.constrained.fptrunc.f32.f64(double %arg_wide, metadata !\"round.dynamic\", metadata !\"fpexcept.strict\") strictfp", llvm);
        Assert.Contains("call double @llvm.experimental.constrained.sitofp.f64.i32(i32 %arg_integer, metadata !\"round.dynamic\", metadata !\"fpexcept.strict\") strictfp", llvm);
        Assert.Contains("call i32 @llvm.experimental.constrained.fptosi.i32.f32(float %v", llvm);
        Assert.Contains("metadata !\"fpexcept.strict\") strictfp", llvm);
        Assert.DoesNotContain("fpext fast", llvm);
        Assert.DoesNotContain("fptrunc fast", llvm);
    }

    [Fact]
    public void UnrecoverableUnreachablePathsLowerThroughColdTrapHelper()
    {
        // Definite-return analysis (STK3045) and switch exhaustiveness (STK3044) make
        // user-reachable fall-off-the-end paths a compile error, so the only remaining
        // unreachable paths are compiler-proven impossible ones — e.g. the mandatory
        // default destination of an LLVM `switch` over an exhaustively-covered enum.
        // Those still lower through the cold trap helper (never exception machinery).
        var result = Compile(
            """
            module Demo

            enum Color { Red, Green, Blue }

            unsafe fn i32[min max] Run(Color c)
            {
                switch (c)
                {
                    case Color.Red: return 1;
                    case Color.Green: return 2;
                    case Color.Blue: return 3;
                }
            }
            """);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);

        Assert.Contains("declare void @llvm.trap() cold noreturn nounwind", llvm);
        Assert.Contains("define internal dso_local coldcc void @__stark_unreachable_trap() unnamed_addr cold noreturn nounwind", llvm);
        Assert.Contains("call void @llvm.trap()", llvm);
        Assert.Contains("call coldcc void @__stark_unreachable_trap()", llvm);
        Assert.Contains("unreachable", llvm);
        Assert.DoesNotContain("invoke ", llvm);
        Assert.DoesNotContain("landingpad", llvm);
        Assert.DoesNotContain("personality", llvm);
        Assert.DoesNotContain("resume ", llvm);
        Assert.DoesNotContain("@__stark_heap_alloc", llvm);
    }

    [Fact]
    public void HeapObjectCreationUsesAllocatorLowering()
    {
        var result = Compile(
            """
            module Demo

            struct Box
            {
                i32[min max] Value;
            }

            unsafe fn i32[min max] Run()
            {
                heap Box box = new Box()
                {
                    Value = 7
                };
                return box.Value;
            }
            """,
            options: new CompilerOptions(TargetInfo: new LlvmTargetInfo("x86_64-unknown-linux-gnu", null)));

        Assert.True(result.Succeeded);
        var llvm = GetLlvmRaw(result);

        Assert.DoesNotContain("@malloc(", llvm);
        Assert.DoesNotContain("@realloc(", llvm);
        Assert.DoesNotContain("@free(", llvm);
        Assert.Contains("define internal dso_local noalias nonnull noundef ptr @__stark_heap_alloc(i64 noundef %size, i64 noundef allocalign %alignment) unnamed_addr allocsize(0) allockind(\"alloc,uninitialized,aligned\") \"alloc-family\"=\"__stark_heap_alloc\" nounwind", llvm);
        Assert.Contains("define internal dso_local void @__stark_heap_free(ptr %ptr) unnamed_addr allockind(\"free\") \"alloc-family\"=\"__stark_heap_alloc\" nounwind", llvm);
        Assert.Contains("define linkonce_odr hidden noalias nonnull noundef ptr @__stark_runtime_alloc(i64 noundef %size, i64 noundef allocalign %alignment) unnamed_addr allocsize(0) allockind(\"alloc,uninitialized,aligned\") \"alloc-family\"=\"__stark_runtime_alloc\" nounwind comdat", llvm);
        Assert.Contains("define internal dso_local noalias noundef ptr @__stark_os_allocate(i64 noundef %size) unnamed_addr allocsize(0) allockind(\"alloc,uninitialized\") \"alloc-family\"=\"__stark_os_allocate\" nounwind", llvm);
        Assert.Contains("@__stark_alloc_lock = linkonce_odr hidden global i32 0, comdat, align 4", llvm);
        Assert.Contains("@__stark_alloc_bucket_16 = linkonce_odr hidden thread_local(localexec) global ptr null, comdat, align 8", llvm);
        Assert.Contains("@__stark_alloc_bucket_4096 = linkonce_odr hidden thread_local(localexec) global ptr null, comdat, align 8", llvm);
        Assert.Contains("define linkonce_odr hidden void @__stark_alloc_lock_acquire() unnamed_addr nounwind comdat", llvm);
        Assert.Contains("atomicrmw xchg ptr @__stark_alloc_lock, i32 1 acquire, align 4", llvm);
        Assert.Contains("store atomic i32 0, ptr @__stark_alloc_lock release, align 4", llvm);
        Assert.DoesNotContain("call void @__stark_alloc_lock_acquire()", llvm);
        Assert.DoesNotContain("call void @__stark_alloc_lock_release()", llvm);
        Assert.Contains("%bucket_alignment_ok = icmp ule i64 %effective_alignment, 16", llvm);
        Assert.Contains("%bucket_size_ok = icmp ule i64 %requested_size, 4096", llvm);
        Assert.Contains("br i1 %can_bucket, label %bucket_select_16, label %large_allocate", llvm);
        Assert.Contains("br label %bucket_16_refill", llvm);
        Assert.Contains("bucket_16_refill:", llvm);
        Assert.Contains("%bucket_16_slab_base = call noalias noundef ptr @__stark_os_allocate(i64 noundef", llvm);
        Assert.Contains("%bucket_16_refill_index = phi i64 [1, %bucket_16_slab_ok], [%bucket_16_refill_next, %bucket_16_refill_body]", llvm);
        Assert.Contains("%bucket_16_refill_offset = mul i64 %bucket_16_refill_index, 48", llvm);
        Assert.Contains("store i64 16, ptr %bucket_16_block_bucket_size_slot, align 8", llvm);
        Assert.Contains("store ptr %bucket_16_block, ptr @__stark_alloc_bucket_16, align 8", llvm);
        Assert.Contains("ret ptr %bucket_16_first", llvm);
        Assert.Contains("store i64 0, ptr %bucket_size_slot, align 8", llvm);
        Assert.Contains("store ptr %ptr, ptr @__stark_alloc_bucket_16, align 8", llvm);
        Assert.Contains("br i1 %bucket_is_4096, label %bucket_4096_push, label %free_os", llvm);
        Assert.Contains("call i64 asm sideeffect \"syscall\"", llvm);
        Assert.Contains("define internal dso_local coldcc void @__stark_oom_trap() unnamed_addr cold noreturn nounwind", llvm);
        Assert.Contains("call void @llvm.trap()", llvm);
        Assert.Contains("call coldcc void @__stark_oom_trap()", llvm);
        Assert.Matches(@"br i1 %is_null, label %oom, label %ok, !prof !\d+", llvm);
        Assert.Matches(@"!\d+ = !\{!""branch_weights"", i32 1, i32 2000\}", llvm);
        Assert.Contains("call noalias nonnull noundef align 4 dereferenceable(4) ptr @__stark_heap_alloc(i64 noundef", llvm);
        Assert.Contains(", i64 noundef 4)", llvm);
        Assert.Contains("call void @__stark_heap_free(ptr %slot_box)", llvm);
        Assert.DoesNotContain("alloca %Box", llvm);
        Assert.DoesNotContain("; LLVM body emission fallback for Run", llvm);
    }

    [Fact]
    public void ArenaObjectCreationUsesFrameAllocatorLowering()
    {
        var result = Compile(
            """
            module Demo

            struct Box
            {
                i32[min max] Value;
            }

            unsafe fn i32[min max] Run()
            {
                arena Box box = new Box()
                {
                    Value = 7
                };
                return box.Value;
            }
            """,
            options: new CompilerOptions(TargetInfo: new LlvmTargetInfo("x86_64-unknown-linux-gnu", null)));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);
        var runBody = ExtractDefinitionBody(llvm, "Run");

        Assert.DoesNotContain("; LLVM body emission fallback for Run", llvm);
        Assert.DoesNotContain("@malloc(", llvm);
        Assert.DoesNotContain("@realloc(", llvm);
        Assert.DoesNotContain("@free(", llvm);
        Assert.DoesNotContain("@__stark_heap_alloc", llvm);
        Assert.DoesNotContain("@__stark_heap_free", llvm);
        Assert.DoesNotContain("alloca %Box", runBody, StringComparison.Ordinal);
        Assert.Contains("%__stark_arena_frame = alloca { ptr, ptr, ptr }, align 8", runBody, StringComparison.Ordinal);
        Assert.Contains("call void @__stark_arena_enter(ptr nonnull %__stark_arena_frame)", runBody, StringComparison.Ordinal);
        Assert.Contains("call void @__stark_arena_leave(ptr nonnull %__stark_arena_frame)", runBody, StringComparison.Ordinal);
        Assert.Contains("call noalias nonnull noundef align 4 dereferenceable(4) ptr @__stark_arena_alloc(ptr nonnull %__stark_arena_frame", runBody, StringComparison.Ordinal);
        Assert.Contains("define linkonce_odr hidden void @__stark_arena_enter(ptr captures(none) %frame) unnamed_addr nounwind comdat", llvm, StringComparison.Ordinal);
        Assert.Contains("define linkonce_odr hidden void @__stark_arena_leave(ptr captures(none) %frame) unnamed_addr nounwind comdat", llvm, StringComparison.Ordinal);
        Assert.Contains("define linkonce_odr hidden noalias nonnull noundef ptr @__stark_arena_alloc(ptr captures(none) %frame, i64 noundef %size, i64 noundef allocalign %alignment) unnamed_addr allocsize(1) allockind(\"alloc,uninitialized,aligned\") \"alloc-family\"=\"__stark_arena_alloc\" nounwind comdat", llvm, StringComparison.Ordinal);
    }

    [Fact]
    public void HeapFieldInitializationDoesNotReadUninitializedAggregateStorage()
    {
        var result = Compile(
            """
            module Demo

            struct Pair
            {
                i32[min max] Left;
                i32[min max] Right;
            }

            unsafe ffi fn void Touch(rawptr<Pair> pair);

            unsafe fn i32[min max] Run(i32[min max] left, i32[min max] right)
            {
                heap mut Pair pair;
                pair.Left = left;
                pair.Right = right;
                Touch(&pair);
                return pair.Left + pair.Right;
            }
            """,
            options: new CompilerOptions(TargetInfo: new LlvmTargetInfo("x86_64-unknown-linux-gnu", null)));

        Assert.True(result.Succeeded);
        var llvm = GetLlvmRaw(result);

        Assert.Contains("call noalias nonnull noundef align 4 dereferenceable(8) ptr @__stark_heap_alloc", llvm);
        Assert.Contains("store i32 %arg_left", llvm);
        Assert.Contains("store i32 %arg_right", llvm);
        Assert.DoesNotContain("load %Pair, ptr %slot_pair", llvm);
        Assert.DoesNotContain("insertvalue %Pair", llvm);
        Assert.Contains("call void @__stark_heap_free(ptr %slot_pair)", llvm);
        Assert.DoesNotContain("; LLVM body emission fallback for Run", llvm);
    }

    [Fact]
    public void SystemMemoryInternalAllocatorBuiltinsLowerToRuntimeAllocatorContract()
    {
        var result = Compile(
            """
            module System.Memory

            public struct Allocator
            {
                u8[0 127] Kind;

                static unsafe finite law Allocator Default()
                {
                    return new Allocator()
                    {
                        Kind = 0
                    };
                }

                unsafe finite law bool IsDefault(borrow Allocator self)
                {
                    return self.Kind == 0;
                }
            }

            internal struct Allocation
            {
                rawmutptr<i8[min max]> Pointer;
                u64[0 2 ** 63 - 1] ByteLength;
                u64[1 2 ** 63 - 1] Alignment;
                Allocator Allocator;
            }

            internal unsafe fn Allocation Allocate(Allocator allocator, u64[0 2 ** 63 - 1] byteLength, u64[1 2 ** 63 - 1] alignment);
            internal unsafe fn Allocation Reallocate(Allocation allocation, u64[0 2 ** 63 - 1] byteLength, u64[1 2 ** 63 - 1] alignment);
            internal unsafe fn void Free(Allocation allocation);

            export unsafe fn i32[min max] main()
            {
                stack Allocator allocator = Allocator.Default();
                stack Allocation allocation = Allocate(allocator, 16, 8);
                stack Allocation grown = Reallocate(allocation, 32, 8);
                Free(grown);
                return 0;
            }
            """,
            options: new CompilerOptions(TargetInfo: new LlvmTargetInfo("x86_64-unknown-linux-gnu", null)));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);

        Assert.Contains("declare void @llvm.trap() cold noreturn nounwind", llvm);
        Assert.DoesNotContain("@malloc(", llvm);
        Assert.DoesNotContain("@realloc(", llvm);
        Assert.DoesNotContain("@free(", llvm);
        Assert.Contains("define linkonce_odr hidden noalias nonnull noundef ptr @__stark_runtime_alloc(i64 noundef %size, i64 noundef allocalign %alignment) unnamed_addr allocsize(0) allockind(\"alloc,uninitialized,aligned\") \"alloc-family\"=\"__stark_runtime_alloc\" nounwind comdat", llvm);
        Assert.Contains("define linkonce_odr hidden noalias noundef ptr @__stark_runtime_try_alloc(i64 noundef %size, i64 noundef allocalign %alignment) unnamed_addr allocsize(0) allockind(\"alloc,uninitialized,aligned\") \"alloc-family\"=\"__stark_runtime_alloc\" nounwind comdat", llvm);
        Assert.Contains("define linkonce_odr hidden nonnull noundef ptr @__stark_runtime_realloc(ptr %old_ptr, i64 noundef %old_size, i64 noundef %new_size, i64 noundef allocalign %alignment) unnamed_addr allocsize(2) allockind(\"realloc,aligned\") \"alloc-family\"=\"__stark_runtime_alloc\" nounwind comdat", llvm);
        Assert.DoesNotContain("define linkonce_odr hidden noalias nonnull noundef ptr @__stark_runtime_realloc", llvm);
        Assert.Contains("define linkonce_odr hidden ptr @__stark_runtime_try_realloc(ptr %old_ptr, i64 noundef %old_size, i64 noundef %new_size, i64 noundef allocalign %alignment) unnamed_addr allocsize(2) allockind(\"realloc,aligned\") \"alloc-family\"=\"__stark_runtime_alloc\" nounwind comdat", llvm);
        Assert.Contains("define linkonce_odr hidden void @__stark_runtime_free(ptr %ptr) unnamed_addr allockind(\"free\") \"alloc-family\"=\"__stark_runtime_alloc\" nounwind comdat", llvm);
        Assert.Contains("define internal dso_local noalias noundef ptr @__stark_os_allocate(i64 noundef %size) unnamed_addr allocsize(0) allockind(\"alloc,uninitialized\") \"alloc-family\"=\"__stark_os_allocate\" nounwind", llvm);
        Assert.Contains("call i64 asm sideeffect \"syscall\"", llvm);
        Assert.Contains("@Allocate(", llvm);
        var allocateHeader = ExtractDefinitionHeader(llvm, "Allocate");
        var reallocateHeader = ExtractDefinitionHeader(llvm, "Reallocate");
        var freeHeader = ExtractDefinitionHeader(llvm, "Free");
        Assert.Contains("memory(readwrite, argmem: write)", allocateHeader);
        Assert.DoesNotContain("memory(argmem:", allocateHeader);
        Assert.DoesNotContain("memory(argmem:", reallocateHeader);
        Assert.Contains("memory(readwrite, argmem: read)", freeHeader);
        Assert.DoesNotContain("memory(argmem:", freeHeader);
        Assert.Contains("@Reallocate(", llvm);
        Assert.Contains("call noalias nonnull noundef ptr @__stark_runtime_alloc(i64 noundef", llvm);
        Assert.Contains("call nonnull noundef ptr @__stark_runtime_realloc(ptr %memory_old_ptr, i64 noundef %memory_old_byte_length, i64 noundef", llvm);
        Assert.DoesNotContain("call noalias nonnull noundef ptr @__stark_runtime_realloc", llvm);
        Assert.Contains("%realloc_bucket_size = load i64, ptr %realloc_bucket_size_slot, align 8", llvm);
        Assert.Contains("%realloc_bucket_size_fits = icmp ule i64 %new_size, %realloc_bucket_size", llvm);
        Assert.Contains("%realloc_bucket_alignment_fits = icmp ule i64 %realloc_effective_alignment, 16", llvm);
        Assert.Contains("br i1 %realloc_bucket_can_reuse, label %reuse_old, label %fallback", llvm);
        Assert.Contains("reuse_old:", llvm);
        Assert.Contains("ret ptr %old_ptr", llvm);
        Assert.Contains("fallback:", llvm);
        Assert.Contains("call void @llvm.memcpy.p0.p0.i64(ptr align 8 %new_ptr, ptr align 8 %old_ptr, i64 %copy_length, i1 false)", llvm);
        Assert.Contains("call void @__stark_runtime_free(ptr %memory_old_ptr)", llvm);
        Assert.Contains("call void @__stark_runtime_free(ptr %memory_ptr)", llvm);
        Assert.Contains("extractvalue", llvm);
        Assert.Contains(", 3", llvm);
        Assert.Contains("insertvalue", llvm);
        Assert.Contains("call void @llvm.trap()", llvm);
        Assert.DoesNotContain("LLVM body emission fallback", llvm);
    }

    [Fact]
    public void RuntimeAllocatorUsesWindowsHeapApisForWindowsTargets()
    {
        var result = Compile(
            """
            module Demo

            struct Box
            {
                i32[min max] Value;
            }

            unsafe fn i32[min max] Run()
            {
                heap Box box = new Box()
                {
                    Value = 7
                };
                return box.Value;
            }
            """,
            options: new CompilerOptions(TargetInfo: new LlvmTargetInfo("x86_64-pc-windows-msvc", null)));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);

        Assert.Contains("declare ptr @GetProcessHeap() nounwind", llvm);
        Assert.Contains("declare noalias noundef ptr @HeapAlloc(ptr, i32, i64 noundef) allocsize(2) allockind(\"alloc,uninitialized\") \"alloc-family\"=\"__stark_os_allocate\" nounwind", llvm);
        Assert.Contains("declare noundef ptr @HeapReAlloc(ptr, i32, ptr, i64 noundef) allocsize(3) allockind(\"realloc\") \"alloc-family\"=\"__stark_os_allocate\" nounwind", llvm);
        Assert.Contains("declare i32 @HeapFree(ptr, i32, ptr) allockind(\"free\") \"alloc-family\"=\"__stark_os_allocate\" nounwind", llvm);
        Assert.Contains("call noalias noundef ptr @HeapAlloc(ptr %heap, i32 0, i64 noundef %size)", llvm);
        Assert.Contains("define internal dso_local noundef ptr @__stark_os_reallocate(ptr %ptr, i64 noundef %size) unnamed_addr allocsize(1) allockind(\"realloc\") \"alloc-family\"=\"__stark_os_allocate\" nounwind", llvm);
        Assert.Contains("call noundef ptr @HeapReAlloc(ptr %heap, i32 0, ptr %ptr, i64 noundef %size)", llvm);
        Assert.Contains("os_realloc_check:", llvm);
        Assert.Contains("%realloc_header_is_base = icmp eq ptr %realloc_base, %realloc_header", llvm);
        Assert.Contains("%realloc_os_alignment_ok = icmp ule i64 %realloc_effective_alignment, 8", llvm);
        Assert.Contains("br i1 %realloc_can_os_realloc, label %try_os_reallocate, label %fallback", llvm);
        Assert.Contains("call noundef ptr @__stark_os_reallocate(ptr %realloc_base, i64 noundef %os_total)", llvm);
        Assert.Contains("br i1 %os_realloc_failed, label %fallback, label %os_reallocated", llvm);
        Assert.Contains("%os_realloc_ptr = getelementptr i8, ptr %os_realloc_base, i64 24", llvm);
        Assert.Contains("call i32 @HeapFree(ptr %heap, i32 0, ptr %ptr)", llvm);
        Assert.DoesNotContain("@malloc(", llvm);
        Assert.DoesNotContain("@realloc(", llvm);
        Assert.DoesNotContain("@free(", llvm);
        Assert.DoesNotContain("asm sideeffect \"syscall\"", llvm);
        Assert.DoesNotContain("; LLVM body emission fallback for Run", llvm);
    }

    [Fact]
    public void RuntimeAllocatorUsesMmap2ForThirtyTwoBitLinuxTargets()
    {
        var result = Compile(
            """
            module Demo

            struct Box
            {
                i32[min max] Value;
            }

            unsafe fn i32[min max] Run()
            {
                heap Box box = new Box()
                {
                    Value = 7
                };
                return box.Value;
            }
            """,
            options: new CompilerOptions(TargetInfo: new LlvmTargetInfo("i686-unknown-linux-gnu", null)));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);

        Assert.Equal(2, CountOccurrences(llvm, "call i32 asm sideeffect \"int $$0x80\""));
        Assert.Contains("(i32 192, i32 0, i32 %size, i32 3, i32 34, i32 -1, i32 0)", llvm);
        Assert.Contains("(i32 91, i32 %ptr_int, i32 %size, i32 0, i32 0, i32 0, i32 0)", llvm);
        Assert.DoesNotContain("@malloc(", llvm);
        Assert.DoesNotContain("@realloc(", llvm);
        Assert.DoesNotContain("@free(", llvm);
        Assert.DoesNotContain("; LLVM body emission fallback for Run", llvm);
    }

    [Fact]
    public void SystemMemoryAllocatorBoundsChecksRuntimeSizesForThirtyTwoBitTargets()
    {
        var result = Compile(
            """
            module System.Memory

            public struct Allocator
            {
                u8[0 127] Kind;

                static unsafe finite law Allocator Default()
                {
                    return new Allocator()
                    {
                        Kind = 0
                    };
                }

                unsafe finite law bool IsDefault(borrow Allocator self)
                {
                    return self.Kind == 0;
                }
            }

            internal struct Allocation
            {
                rawmutptr<i8[min max]> Pointer;
                u64[0 2 ** 63 - 1] ByteLength;
                u64[1 2 ** 63 - 1] Alignment;
                Allocator Allocator;
            }

            internal unsafe fn Allocation Allocate(Allocator allocator, u64[0 2 ** 63 - 1] byteLength, u64[1 2 ** 63 - 1] alignment);
            internal unsafe fn Allocation Reallocate(Allocation allocation, u64[0 2 ** 63 - 1] byteLength, u64[1 2 ** 63 - 1] alignment);
            internal unsafe fn void Free(Allocation allocation);

            export unsafe fn i32[min max] main()
            {
                stack Allocator allocator = Allocator.Default();
                stack Allocation allocation = Allocate(allocator, 16, 8);
                stack Allocation grown = Reallocate(allocation, 32, 8);
                Free(grown);
                return 0;
            }
            """,
            options: new CompilerOptions(TargetInfo: new LlvmTargetInfo("i686-unknown-linux-gnu", null)));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);

        Assert.Contains("%memory_byte_length_too_large = icmp ugt i64 %arg_byteLength, 4294967295", llvm);
        Assert.Contains("%memory_alignment_too_large = icmp ugt i64 %arg_alignment, 4294967295", llvm);
        Assert.Contains("%memory_byte_length_runtime = trunc i64 %arg_byteLength to i32", llvm);
        Assert.Contains("%memory_alignment_runtime = trunc i64 %arg_alignment to i32", llvm);
        Assert.Contains("%memory_old_byte_length_runtime = trunc i64 %memory_old_byte_length to i32", llvm);
        Assert.Contains("call noalias nonnull noundef ptr @__stark_runtime_alloc(i32 noundef %memory_byte_length_runtime, i32 noundef %memory_alignment_runtime)", llvm);
        Assert.Contains("call nonnull noundef ptr @__stark_runtime_realloc(ptr %memory_old_ptr, i32 noundef %memory_old_byte_length_runtime, i32 noundef %memory_byte_length_runtime, i32 noundef %memory_alignment_runtime)", llvm);
        Assert.DoesNotContain("call noalias nonnull noundef ptr @__stark_runtime_realloc", llvm);
        Assert.DoesNotContain("@malloc(", llvm);
        Assert.DoesNotContain("@realloc(", llvm);
        Assert.DoesNotContain("@free(", llvm);
        Assert.DoesNotContain("LLVM body emission fallback", llvm);
    }

    [Fact]
    public void StackLocalsEmitInstructionAlignmentWhenLayoutsAreKnown()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn i32[min max] Run(i32[min max] input)
            {
                stack mut i32[min max] value = input;
                stack rawmutptr<i32[min max]> ptr = &value;
                *ptr = input + 1;
                return value;
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvmRaw(result);

        Assert.Contains("alloca i32, align 4", llvm);
        Assert.Contains("store i32", llvm);
        Assert.Contains("ptr %slot_value, align 4", llvm);
        Assert.Contains("load i32, ptr %slot_value, align 4", llvm);
    }

    [Fact]
    public void AddressValueFactsEmitKnownPointeeAlignmentForIndirectLoads()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn i64[min max] Read()
            {
                stack mut i64[min max] value = 7;
                stack rawmutptr<i64[min max]> ptr = &value;
                return *ptr;
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvmRaw(result);

        Assert.Contains("load i64, ptr %slot_value, align 8", llvm);
    }

    [Fact]
    public void RawPointerParametersDoNotInventPointeeAlignment()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn i32[min max] Read(rawptr<i32[min max]> ptr)
            {
                return *ptr;
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvmRaw(result);

        Assert.Contains("load i32, ptr %arg_ptr", llvm);
        Assert.DoesNotContain("load i32, ptr %arg_ptr, align", llvm);
    }

    [Fact]
    public void I386ViewTypedLocalAddressesUseTargetAwareAlignment()
    {
        var result = Compile(
            """
            module Demo

            unsafe ffi fn void Sink(rawptr<ascii> ptr);

            unsafe fn i32[min max] Probe(ascii input)
            {
                stack ascii slot = input;
                stack rawptr<ascii> ptr = &slot;
                Sink(ptr);
                return 0;
            }
            """,
            new CompilerOptions(
                EmitLlvmIr: true,
                TargetInfo: new LlvmTargetInfo(
                    "i386-unknown-linux-gnu",
                    "e-m:e-p:32:32-p270:32:32-p271:32:32-p272:64:64-i128:128-f64:32:64-f80:32-n8:16:32-S128")));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);

        Assert.Contains("alloca %stark_ascii, align 4", llvm);
        Assert.Contains("align 4", llvm);
        Assert.DoesNotContain("align 8", llvm);
    }

    [Fact]
    public void OwnedScalarArrayStoragePreservesVectorizationFriendlyAlignment()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn i32[min max] ReadStack(
                i32[min max][4] input,
                u8[0 3] index)
                {
                    stack mut i32[min max][4] stackValues = input;
                return stackValues[index];
            }

            unsafe fn i32[min max] ReadHeap(
                i32[min max][4] input,
                u8[0 3] index)
                {
                    heap i32[min max][4] heapValues = input;
                return heapValues[index];
            }
            """);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);

        Assert.Contains("%slot_stackValues = alloca [4 x i32], align 16", llvm);
        Assert.Contains("store [4 x i32] %arg_input, ptr %slot_stackValues, align 16", llvm);
        Assert.Contains("call noalias nonnull noundef align 16 dereferenceable(16) ptr @__stark_heap_alloc", llvm);
        Assert.Contains(", i64 noundef 16)", llvm);
        Assert.Contains("store [4 x i32] %arg_input, ptr %slot_heapValues, align 16", llvm);
    }

    [Fact]
    public void StaticStackAndAbiTemporaryAllocasEmitInEntryBlock()
    {
        var result = Compile(
            """
            module Demo

            struct Big
            {
                i64[min max] A;
                i64[min max] B;
                i64[min max] C;
            }

            unsafe ffi fn void Drain(rawmutptr<i32[min max]> ptr);

            unsafe fn Big Make()
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

                return new Big()
                {
                    A = acc, B = acc + 1, C = acc + 2
                };
            }

            unsafe fn i64[min max] Run(bool flag)
            {
                if (flag)
                {
                    stack mut i32[min max] value = 41;
                    stack rawmutptr<i32[min max]> ptr = &value;
                    Drain(ptr);
                    stack Big made = Make();
                    return (i64[min max])*ptr + made.A;
                }

                return 0;
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = NormalizeLlvm(GetLlvmRaw(result));
        var runIndex = llvm.IndexOf("define fastcc i64 @Run", StringComparison.Ordinal);
        Assert.True(runIndex >= 0);

        var entryIndex = llvm.IndexOf("bb0:", runIndex, StringComparison.Ordinal);
        var stackAllocaIndex = llvm.IndexOf("%slot_value = alloca i32, align 4", runIndex, StringComparison.Ordinal);
        var callretAlloca = Regex.Match(llvm[runIndex..], @"%abi_callret_slot_\d+ = alloca %Big, align 8");
        var branchIndex = llvm.IndexOf("  br i1 %arg_flag", runIndex, StringComparison.Ordinal);
        var branchBlockIndex = llvm.IndexOf("bb1:", runIndex, StringComparison.Ordinal);

        Assert.True(entryIndex >= 0);
        Assert.True(stackAllocaIndex > entryIndex && stackAllocaIndex < branchIndex);
        Assert.True(callretAlloca.Success && runIndex + callretAlloca.Index > entryIndex && runIndex + callretAlloca.Index < branchIndex);
        Assert.True(branchIndex < branchBlockIndex);
        Assert.Contains("bb1:\n  call void @llvm.dbg.declare(metadata ptr %slot_value", llvm);
        Assert.DoesNotContain("bb1:\n  %slot_value = alloca", llvm);
    }

    [Fact]
    public void SmallRecordEqualityAndInequalityEmitScalarLeafComparisons()
    {
        var result = Compile(
            """
            module Demo

            record Pair(i32[min max] Left, i32[min max] Right)
            {
            }

            unsafe fn bool Equal(Pair left, Pair right)
            {
                return left == right;
            }

            unsafe fn bool NotEqual(Pair left, Pair right)
            {
                return left != right;
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("define fastcc i1 @Equal(%Pair %arg_left, %Pair %arg_right)", llvm);
        Assert.Contains("define fastcc i1 @NotEqual(%Pair %arg_left, %Pair %arg_right)", llvm);
        Assert.Contains("extractvalue %Pair %arg_left, 0", llvm);
        Assert.Contains("extractvalue %Pair %arg_right, 1", llvm);
        Assert.Contains("icmp eq i32", llvm);
        Assert.Contains("icmp ne i32", llvm);
        Assert.Contains("and i1", llvm);
        Assert.Contains("or i1", llvm);
        Assert.DoesNotContain("; LLVM body emission fallback for Equal", llvm);
        Assert.DoesNotContain("; LLVM body emission fallback for NotEqual", llvm);
    }

    [Fact]
    public void SmallFixedArrayEqualityAndInequalityEmitScalarLeafComparisons()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn bool Equal(i32[min max][2] left, i32[min max][2] right)
            {
                return left == right;
            }

            unsafe fn bool NotEqual(i32[min max][2] left, i32[min max][2] right)
            {
                return left != right;
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("define fastcc i1 @Equal([2 x i32] %arg_left, [2 x i32] %arg_right)", llvm);
        Assert.Contains("define fastcc i1 @NotEqual([2 x i32] %arg_left, [2 x i32] %arg_right)", llvm);
        Assert.Contains("extractvalue [2 x i32] %arg_left, 0", llvm);
        Assert.Contains("extractvalue [2 x i32] %arg_right, 1", llvm);
        Assert.Contains("icmp eq i32", llvm);
        Assert.Contains("icmp ne i32", llvm);
        Assert.Contains("and i1", llvm);
        Assert.Contains("or i1", llvm);
        Assert.DoesNotContain("; LLVM body emission fallback for Equal", llvm);
        Assert.DoesNotContain("; LLVM body emission fallback for NotEqual", llvm);
    }

    [Fact]
    public void SmallEnumEqualityAndInequalityEmitScalarLeafComparisons()
    {
        var result = Compile(
            """
            module Demo

            enum Token
            {
                None,
                Number(i32[min max]),
            }

            unsafe fn bool Equal(Token left, Token right)
            {
                return left == right;
            }

            unsafe fn bool NotEqual(Token left, Token right)
            {
                return left != right;
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("%Token = type { i8, i32 }", llvm);
        Assert.Contains("define fastcc i1 @Equal(%Token %arg_left, %Token %arg_right)", llvm);
        Assert.Contains("define fastcc i1 @NotEqual(%Token %arg_left, %Token %arg_right)", llvm);
        Assert.Contains("extractvalue %Token %arg_left, 0", llvm);
        Assert.Contains("extractvalue %Token %arg_right, 1", llvm);
        Assert.Contains("icmp eq i8", llvm);
        Assert.Contains("icmp eq i32", llvm);
        Assert.Contains("icmp ne i8", llvm);
        Assert.Contains("icmp ne i32", llvm);
        Assert.Contains("and i1", llvm);
        Assert.Contains("or i1", llvm);
        Assert.DoesNotContain("; LLVM body emission fallback for Equal", llvm);
        Assert.DoesNotContain("; LLVM body emission fallback for NotEqual", llvm);
    }

    [Fact]
    public void LargerRecordEqualityAndInequalityEmitScalarLeafComparisons()
    {
        var result = Compile(
            """
            module Demo

            record Many(i32[min max] A, i32[min max] B, i32[min max] C, i32[min max] D, i32[min max] E)
            {
            }

            unsafe fn bool Equal(Many left, Many right)
            {
                return left == right;
            }

            unsafe fn bool NotEqual(Many left, Many right)
            {
                return left != right;
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Equal(1, CountOccurrences(llvm, "define fastcc i1 @Equal("));
        Assert.Equal(1, CountOccurrences(llvm, "define fastcc i1 @NotEqual("));
        Assert.True(CountOccurrences(llvm, "icmp eq i32") >= 5);
        Assert.True(CountOccurrences(llvm, "icmp ne i32") >= 5);
        Assert.True(CountOccurrences(llvm, "and i1") >= 4);
        Assert.True(CountOccurrences(llvm, "or i1") >= 4);
        Assert.DoesNotContain("; LLVM body emission fallback for Equal", llvm);
        Assert.DoesNotContain("; LLVM body emission fallback for NotEqual", llvm);
    }

    [Fact]
    public void LargerRecordOrderedComparisonsEmitScalarLeafLexicographicHelperCalls()
    {
        var result = Compile(
            """
            module Demo

            record Many(i32[min max] A, i32[min max] B, i32[min max] C, i32[min max] D, i32[min max] E)
            {
            }

            unsafe fn bool Less(Many left, Many right)
            {
                return left < right;
            }

            unsafe fn bool LessOrEqual(Many left, Many right)
            {
                return left <= right;
            }

            unsafe fn bool Greater(Many left, Many right)
            {
                return left > right;
            }

            unsafe fn bool GreaterOrEqual(Many left, Many right)
            {
                return left >= right;
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("define internal dso_local i32 @__stark_named_compare_", llvm);
        Assert.Contains("call i32 @__stark_named_compare_", llvm);
        Assert.True(CountOccurrences(llvm, "icmp slt i32") >= 5);
        Assert.True(CountOccurrences(llvm, "icmp sgt i32") >= 5);
        Assert.Contains("icmp sle i32", llvm);
        Assert.Contains("icmp sge i32", llvm);
        Assert.DoesNotContain("; LLVM body emission fallback for Less", llvm);
        Assert.DoesNotContain("; LLVM body emission fallback for LessOrEqual", llvm);
        Assert.DoesNotContain("; LLVM body emission fallback for Greater", llvm);
        Assert.DoesNotContain("; LLVM body emission fallback for GreaterOrEqual", llvm);
    }

    [Fact]
    public void ScalarizableEnumOrderedComparisonsEmitScalarLeafLexicographicHelperCalls()
    {
        var result = Compile(
            """
            module Demo

            enum Token
            {
                None,
                Many(i32[min max], i32[min max], i32[min max], i32[min max], i32[min max]),
            }

            unsafe fn bool Less(Token left, Token right)
            {
                return left < right;
            }

            unsafe fn bool LessOrEqual(Token left, Token right)
            {
                return left <= right;
            }

            unsafe fn bool Greater(Token left, Token right)
            {
                return left > right;
            }

            unsafe fn bool GreaterOrEqual(Token left, Token right)
            {
                return left >= right;
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("define internal dso_local i32 @__stark_named_compare_", llvm);
        Assert.Contains("call i32 @__stark_named_compare_", llvm);
        Assert.Contains("icmp slt i8", llvm);
        Assert.Contains("icmp sgt i8", llvm);
        Assert.True(CountOccurrences(llvm, "icmp slt i32") >= 1);
        Assert.True(CountOccurrences(llvm, "icmp sgt i32") >= 1);
        Assert.Contains("icmp sle i32", llvm);
        Assert.Contains("icmp sge i32", llvm);
        Assert.DoesNotContain("; LLVM body emission fallback for Less", llvm);
        Assert.DoesNotContain("; LLVM body emission fallback for LessOrEqual", llvm);
        Assert.DoesNotContain("; LLVM body emission fallback for Greater", llvm);
        Assert.DoesNotContain("; LLVM body emission fallback for GreaterOrEqual", llvm);
    }

    [Fact]
    public void LargerFixedArrayEqualityAndInequalityEmitScalarLeafComparisons()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn bool Equal(i32[min max][5] left, i32[min max][5] right)
            {
                return left == right;
            }

            unsafe fn bool NotEqual(i32[min max][5] left, i32[min max][5] right)
            {
                return left != right;
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Equal(1, CountOccurrences(llvm, "define fastcc i1 @Equal("));
        Assert.Equal(1, CountOccurrences(llvm, "define fastcc i1 @NotEqual("));
        Assert.True(CountOccurrences(llvm, "icmp eq i32") >= 5);
        Assert.True(CountOccurrences(llvm, "icmp ne i32") >= 5);
        Assert.True(CountOccurrences(llvm, "and i1") >= 4);
        Assert.True(CountOccurrences(llvm, "or i1") >= 4);
        Assert.DoesNotContain("; LLVM body emission fallback for Equal", llvm);
        Assert.DoesNotContain("; LLVM body emission fallback for NotEqual", llvm);
    }

    [Fact]
    public void LargerEnumEqualityAndInequalityEmitScalarLeafComparisons()
    {
        var result = Compile(
            """
            module Demo

            enum Token
            {
                None,
                Many(i32[min max], i32[min max], i32[min max], i32[min max], i32[min max]),
            }

            unsafe fn bool Equal(Token left, Token right)
            {
                return left == right;
            }

            unsafe fn bool NotEqual(Token left, Token right)
            {
                return left != right;
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Equal(1, CountOccurrences(llvm, "define fastcc i1 @Equal("));
        Assert.Equal(1, CountOccurrences(llvm, "define fastcc i1 @NotEqual("));
        Assert.Contains("icmp eq i8", llvm);
        Assert.Contains("icmp ne i8", llvm);
        Assert.True(CountOccurrences(llvm, "icmp eq i32") >= 5);
        Assert.True(CountOccurrences(llvm, "icmp ne i32") >= 5);
        Assert.True(CountOccurrences(llvm, "and i1") >= 5);
        Assert.True(CountOccurrences(llvm, "or i1") >= 5);
        Assert.DoesNotContain("; LLVM body emission fallback for Equal", llvm);
        Assert.DoesNotContain("; LLVM body emission fallback for NotEqual", llvm);
    }

    [Fact]
    public void TextEqualityAndInequalityEmitHelperCalls()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn bool SameAscii(ascii left, ascii right)
            {
                return left == right;
            }

            unsafe fn bool DifferentUnicode(unicode left, unicode right)
            {
                return left != right;
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        var asciiEqualHeader = ExtractDefinitionHeader(llvm, "__stark_ascii_equal");
        var unicodeEqualHeader = ExtractDefinitionHeader(llvm, "__stark_unicode_equal");
        Assert.Contains("define internal dso_local i1 @__stark_ascii_equal(%stark_ascii %left, %stark_ascii %right)", asciiEqualHeader);
        Assert.Contains("unnamed_addr", asciiEqualHeader);
        Assert.Contains("define internal dso_local i1 @__stark_unicode_equal(%stark_unicode %left, %stark_unicode %right)", unicodeEqualHeader);
        Assert.Contains("unnamed_addr", unicodeEqualHeader);
        Assert.Contains("call i1 @__stark_ascii_equal(%stark_ascii %arg_left, %stark_ascii %arg_right)", llvm);
        Assert.Contains("call i1 @__stark_unicode_equal(%stark_unicode %arg_left, %stark_unicode %arg_right)", llvm);
        Assert.Contains("getelementptr inbounds nuw i8, ptr %left_data, i64 %textcmp_index", llvm);
        Assert.Contains("getelementptr inbounds nuw i32, ptr %left_data, i64 %textcmp_index", llvm);
        Assert.Contains("xor i1", llvm);
        Assert.DoesNotContain("; LLVM body emission fallback for SameAscii", llvm);
        Assert.DoesNotContain("; LLVM body emission fallback for DifferentUnicode", llvm);
    }

    [Fact]
    public void AggregatesWithTextFieldsEmitScalarLeafTextComparisons()
    {
        var result = Compile(
            """
            module Demo

            record Label(ascii Tag, unicode Word)
            {
            }

            unsafe fn bool Same(Label left, Label right)
            {
                return left == right;
            }

            unsafe fn bool Different(Label left, Label right)
            {
                return left != right;
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("call i1 @__stark_ascii_equal(", llvm);
        Assert.Contains("call i1 @__stark_unicode_equal(", llvm);
        Assert.Contains("and i1", llvm);
        Assert.Contains("or i1", llvm);
        Assert.DoesNotContain("; LLVM body emission fallback for Same", llvm);
        Assert.DoesNotContain("; LLVM body emission fallback for Different", llvm);
    }

    [Fact]
    public void SliceEqualityAndInequalityEmitPointerAndLengthComparisons()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn bool Same(i32[min max][] left, i32[min max][] right)
            {
                return left == right;
            }

            unsafe fn bool Different(i32[min max][] left, i32[min max][] right)
            {
                return left != right;
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("define fastcc i1 @Same({ ptr, i64 } %arg_left, { ptr, i64 } %arg_right)", llvm);
        Assert.Contains("define fastcc i1 @Different({ ptr, i64 } %arg_left, { ptr, i64 } %arg_right)", llvm);
        Assert.Contains("extractvalue { ptr, i64 } %arg_left, 0", llvm);
        Assert.Contains("extractvalue { ptr, i64 } %arg_right, 1", llvm);
        Assert.Contains("icmp eq ptr", llvm);
        Assert.Contains("icmp eq i64", llvm);
        Assert.Contains("icmp ne ptr", llvm);
        Assert.Contains("icmp ne i64", llvm);
        Assert.Contains("and i1", llvm);
        Assert.Contains("or i1", llvm);
        Assert.DoesNotContain("; LLVM body emission fallback for Same", llvm);
        Assert.DoesNotContain("; LLVM body emission fallback for Different", llvm);
    }

    [Fact]
    public void AggregatesWithSliceFieldsEmitScalarLeafSliceComparisons()
    {
        var result = Compile(
            """
            module Demo

            record Window(i32[min max][] Items, i32[min max] Count)
            {
            }

            unsafe fn bool Same(Window left, Window right)
            {
                return left == right;
            }

            unsafe fn bool Different(Window left, Window right)
            {
                return left != right;
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("icmp eq ptr", llvm);
        Assert.Contains("icmp eq i64", llvm);
        Assert.Contains("icmp eq i32", llvm);
        Assert.Contains("icmp ne ptr", llvm);
        Assert.Contains("icmp ne i64", llvm);
        Assert.Contains("icmp ne i32", llvm);
        Assert.Contains("and i1", llvm);
        Assert.Contains("or i1", llvm);
        Assert.DoesNotContain("; LLVM body emission fallback for Same", llvm);
        Assert.DoesNotContain("; LLVM body emission fallback for Different", llvm);
    }

    [Fact]
    public void MixedCallMemberAndIndexPostfixChainsEmitCallAndExtracts()
    {
        var result = Compile(
            """
            module Demo

            struct Cell
            {
                i32[min max] Value;
            }

            struct Holder
            {
                Cell[2] Cells;
            }

            unsafe fn Holder Make()
            {
                return new Holder()
                {
                    Cells =
                    {
                        new Cell()
                        {
                            Value = 3
                        },
                        new Cell()
                        {
                            Value = 5
                        }
                    }
                };
            }

            unsafe fn i32[min max] Run()
            {
                return Make().Cells[1].Value;
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("%Cell = type { i32 }", llvm);
        Assert.Contains("%Holder = type { [2 x %Cell] }", llvm);
        Assert.Contains("call fastcc %Holder @Make()", llvm);
        Assert.Contains("extractvalue %Holder", llvm);
        Assert.Contains("extractvalue [2 x %Cell]", llvm);
        Assert.Contains("extractvalue %Cell", llvm);
    }

    [Fact]
    public void RecordTypeUsesConcreteAggregateLayoutAndCanFoldFieldReads()
    {
        var result = Compile(
            """
            module Demo

            record Point(i32[min max] X, i32[min max] Y)
            {
            }

            unsafe fn i32[min max] Run()
            {
                stack Point point = new Point(3, 4);
                return point.Y;
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("%Point = type { i32, i32 }", llvm);
        Assert.Contains("ret i32 4", llvm);
    }

    [Fact]
    public void PlainObjectCreationWithoutInitializerReturnsZeroInitializedAggregate()
    {
        var result = Compile(
            """
            module Demo

            struct Box
            {
                i32[min max] Value;
            }

            unsafe fn Box Make()
            {
                return new Box();
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("define fastcc %Box @Make()", llvm);
        Assert.Contains("ret %Box zeroinitializer", llvm);
    }

    [Fact]
    public void PrimaryRecordConstructorArgumentsEmitOrderedAggregateUpdates()
    {
        var result = Compile(
            """
            module Demo

            record Point(i32[min max] X)
            {
                i32[min max] Y;
            }

            unsafe fn Point Make()
            {
                return new Point(3)
                {
                    Y = 9
                };
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("%Point = type { i32, i32 }", llvm);
        Assert.True(CountOccurrences(llvm, "insertvalue %Point") >= 2);
        Assert.Contains("ret %Point", llvm);
        Assert.DoesNotContain("declare fastcc %Point @Make()", llvm);
    }

    [Fact]
    public void InternalAggregateParameterUsesDirectValueAbi()
    {
        var result = Compile(
            """
            module Demo

            struct Box
            {
                i32[min max] Value;
            }

            unsafe fn i32[min max] Read(Box box)
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

            unsafe fn i32[min max] Run()
            {
                return Read(new Box()
                {
                    Value = 7
                });
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("define fastcc i32 @Read(%Box %arg_box)", llvm);
        Assert.Contains("extractvalue %Box %arg_box, 0", llvm);
        Assert.Contains("call fastcc i32 @Read(%Box", llvm);
        Assert.DoesNotContain("load %Box, ptr %arg_box", llvm);
    }

    [Fact]
    public void BorrowedPaddedAggregateEmitsDerivedAlignmentAndLayoutFacts()
    {
        var result = Compile(
            """
            module Demo

            struct Pair
            {
                i8[min max] Tag;
                i32[min max] Value;
            }

            unsafe fn void Touch(borrow Pair pair)
            {
                return;
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("define fastcc void @Touch(ptr nonnull noalias readonly captures(none) dereferenceable(8) align 4 %arg_pair)", llvm);
    }

    [Fact]
    public void PackedCStructLayoutEmitsPackedPhysicalTypeAndUnalignedFieldAccess()
    {
        var result = Compile(
            """
            module Demo

            [StructLayout(C), Pack(1)]
            struct Packet
            {
                u8[0 max] Tag;
                i32[min max] Value;
            }

            unsafe fn i32[min max] Read(borrow Packet packet)
            {
                return packet.Value;
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);
        var header = ExtractDefinitionHeader(llvm, "Read");
        var readBody = ExtractDefinitionBody(llvm, "Read");

        Assert.Contains("%Packet = type <{ i8, i32 }>", llvm, StringComparison.Ordinal);
        Assert.Contains("dereferenceable(5)", header, StringComparison.Ordinal);
        Assert.Contains("getelementptr inbounds nuw i8, ptr %", readBody, StringComparison.Ordinal);
        Assert.Contains(", i64 1", readBody, StringComparison.Ordinal);
        Assert.Contains("load i32, ptr", readBody, StringComparison.Ordinal);
        Assert.Contains("align 1", readBody, StringComparison.Ordinal);
    }

    [Fact]
    public void ExplicitStructLayoutEmitsByteStorageAndFieldOffsetAccess()
    {
        var result = Compile(
            """
            module Demo

            [StructLayout(Explicit), Align(4)]
            struct WordParts
            {
                [FieldOffset(0)] u32[0 max] Whole;
                [FieldOffset(0)] u16[0 max] Low;
                [FieldOffset(2)] u16[0 max] High;
            }

            unsafe fn u16[0 max] ReadHigh(borrow WordParts value)
            {
                return value.High;
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);
        var header = ExtractDefinitionHeader(llvm, "ReadHigh");
        var readBody = ExtractDefinitionBody(llvm, "ReadHigh");

        Assert.Contains("%WordParts = type { [4 x i8] }", llvm, StringComparison.Ordinal);
        Assert.Contains("dereferenceable(4)", header, StringComparison.Ordinal);
        Assert.Contains("align 4", header, StringComparison.Ordinal);
        Assert.Contains("getelementptr inbounds nuw i8, ptr %", readBody, StringComparison.Ordinal);
        Assert.Contains(", i64 2", readBody, StringComparison.Ordinal);
        Assert.Contains("load i16, ptr", readBody, StringComparison.Ordinal);
        Assert.Contains("align 2", readBody, StringComparison.Ordinal);
    }

    [Fact]
    public void FfiCStructLayoutArgumentsUseX64SysVCAbiCarriers()
    {
        var result = Compile(
            """
            module Demo

            [StructLayout(C)]
            struct NormalRecord
            {
                u8[0 max] Tag;
                u32[0 max] Length;
                u16[0 max] Code;
            }

            [StructLayout(C), Pack(1), Align(4)]
            struct PackedRecord
            {
                u8[0 max] Tag;
                u32[0 max] Length;
                u16[0 max] Code;
            }

            [StructLayout(Explicit), Align(4)]
            struct WordParts
            {
                [FieldOffset(0)] u32[0 max] Whole;
                [FieldOffset(0)] u16[0 max] Low;
                [FieldOffset(2)] u16[0 max] High;
            }

            unsafe ffi(c) fn i32[min max] stark_check_layout(
                NormalRecord normal,
                PackedRecord packed,
                WordParts word);

            export unsafe fn i32[min max] main()
            {
                stack NormalRecord normal = new NormalRecord()
                {
                    Tag = 17,
                    Length = 287454020,
                    Code = 21862
                };
                stack PackedRecord packed = new PackedRecord()
                {
                    Tag = 34,
                    Length = 1432778632,
                    Code = 30600
                };
                stack WordParts word = new WordParts()
                {
                    Whole = 305419896
                };

                return stark_check_layout(normal, packed, word);
            }
            """,
            new CompilerOptions(
                TargetInfo: new LlvmTargetInfo(
                    "x86_64-unknown-linux-gnu",
                    "e-m:e-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-f80:128-n8:16:32:64-S128")));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);
        var mainBody = ExtractDefinitionBody(llvm, "main");

        Assert.Contains("declare i32 @stark_check_layout(i64, i32, ptr", llvm, StringComparison.Ordinal);
        Assert.Contains("byval(%PackedRecord)", llvm, StringComparison.Ordinal);
        Assert.Contains(", i32)", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("[2 x i64]", llvm, StringComparison.Ordinal);
        Assert.Contains("load i64, ptr", mainBody, StringComparison.Ordinal);
        Assert.Contains("load i32, ptr", mainBody, StringComparison.Ordinal);
        Assert.Contains("call i32 @stark_check_layout(i64", mainBody, StringComparison.Ordinal);
    }

    [Fact]
    public void TypeAliasesPreserveTheUnderlyingAggregateAbi()
    {
        var result = Compile(
            """
            module Demo

            struct Pair
            {
                i64[min max] Left;
                i64[min max] Right;
            }

            alias PairAlias = Pair;

            unsafe fn PairAlias Step(PairAlias value)
            {
                return value;
            }
            """);

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvm(result);

        Assert.Contains("%Pair = type { i64, i64 }", llvm);
        Assert.Contains("define fastcc %Pair @Step(%Pair %arg_value)", llvm);
        Assert.DoesNotContain("sret(%Pair)", llvm);
        Assert.DoesNotContain("byval(%Pair)", llvm);
    }

    [Fact]
    public void SmallPackedAddressableAggregateCopyUsesScalarFieldLoadsAndStores()
    {
        var result = Compile(
            """
            module Demo

            struct Pair
            {
                i32[min max] Left;
                i32[min max] Right;
            }

            unsafe fn i32[min max] Run()
            {
                stack mut Pair source = new Pair()
                {
                    Left = 1, Right = 2
                };
                stack mut Pair dest = new Pair()
                {
                    Left = 0, Right = 0
                };
                stack rawptr<Pair> sourcePtr = &source;
                stack rawptr<Pair> destPtr = &dest;
                dest = source;
                return dest.Right;
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("%Pair = type { i32, i32 }", llvm);
        Assert.Equal(2, llvm.Split('\n').Count(static line => line.Contains("load i32, ptr %abi_copy_src_", StringComparison.Ordinal)));
        Assert.Equal(2, llvm.Split('\n').Count(static line => line.Contains("store i32 %abi_copy_scalar_load_", StringComparison.Ordinal)));
        Assert.DoesNotContain("load %Pair, ptr %v", llvm);
        Assert.DoesNotContain("store %Pair %abi_copy_load_", llvm);
        Assert.DoesNotContain("@llvm.memcpy.p0.p0.i64", llvm);
    }

    [Fact]
    public void SmallPaddedAggregateCopyPreservesWholeAggregateTransfer()
    {
        var result = Compile(
            """
            module Demo

            struct Pair
            {
                i8[min max] Tag;
                i32[min max] Value;
            }

            unsafe fn i32[min max] Run()
            {
                stack Pair source = new Pair()
                {
                    Tag = 1, Value = 2
                };
                stack mut Pair dest = new Pair()
                {
                    Tag = 0, Value = 0
                };
                stack rawptr<Pair> sourcePtr = &source;
                stack rawptr<Pair> destPtr = &dest;
                dest = source;
                return dest.Value;
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("%Pair = type { i8, i32 }", llvm);
        Assert.Contains("load %Pair, ptr %slot_source", llvm);
        Assert.Contains("store %Pair %abi_copy_load_", llvm);
        Assert.DoesNotContain("@llvm.memcpy.p0.p0.i64", llvm);
    }

    [Fact]
    public void LargeAddressableAggregateCopyUsesInlineMemcpy()
    {
        var result = Compile(
            """
            module Demo

            struct Large
            {
                i32[min max] A0;
                i32[min max] A1;
                i32[min max] A2;
                i32[min max] A3;
                i32[min max] A4;
                i32[min max] A5;
                i32[min max] A6;
                i32[min max] A7;
                i32[min max] A8;
            }

            unsafe fn i32[min max] Run()
            {
                stack Large source = new Large()
                {
                    A0 = 1,
                    A1 = 2,
                    A2 = 3,
                    A3 = 4,
                    A4 = 5,
                    A5 = 6,
                    A6 = 7,
                    A7 = 8,
                    A8 = 9
                };
                stack mut Large dest = new Large()
                {
                    A0 = 0,
                    A1 = 0,
                    A2 = 0,
                    A3 = 0,
                    A4 = 0,
                    A5 = 0,
                    A6 = 0,
                    A7 = 0,
                    A8 = 0
                };
                stack rawptr<Large> sourcePtr = &source;
                stack rawptr<Large> destPtr = &dest;
                dest = source;
                return dest.A8;
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("declare void @llvm.memcpy.inline.p0.p0.i64", llvm);
        Assert.Contains("call void @llvm.memcpy.inline.p0.p0.i64(ptr align 4 %slot_dest, ptr align 4 %slot_source", llvm);
        Assert.Contains("i64 36, i1 false)", llvm);
    }

    [Fact]
    public void VeryLargeAddressableAggregateCopyUsesRegularMemcpy()
    {
        var result = Compile(
            """
            module Demo

            struct Big
            {
                i32[min max][128] Data;
            }

            unsafe ffi fn void Consume(rawptr<Big> value);

            unsafe fn void Run()
            {
                stack Big source = new Big();
                stack mut Big dest = new Big();
                Consume(&source);
                dest = source;
                Consume(&dest);
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("declare void @llvm.memcpy.p0.p0.i64", llvm);
        Assert.Contains("call void @llvm.memcpy.p0.p0.i64(", llvm);
        Assert.Contains("i64 512, i1 false)", llvm);
        Assert.DoesNotContain("@llvm.memcpy.inline.p0.p0.i64", llvm);
    }

    [Fact]
    public void LargeLocalFixedArrayConstantIndexUpdateUsesScalarProjectionAccess()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn i8[min max] Run(i8[min max] seed, u8[0 max] index)
            {
                stack mut i8[min max][256] values;
                values[7] = seed;
                return values[index];
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvm(result);

        Assert.Contains("%slot_values = alloca [256 x i8]", llvm);
        Assert.Contains("getelementptr inbounds nuw [256 x i8], ptr %slot_values, i32 0", llvm);
        Assert.Contains("getelementptr inbounds nuw [256 x i8], ptr %slot_values, i32 0, i32 7", llvm);
        Assert.Contains("i32 7", llvm);
        Assert.Contains("store i8", llvm);
        Assert.Contains("load i8", llvm);
        Assert.DoesNotContain("store [256 x i8]", llvm);
        Assert.DoesNotContain("load [256 x i8]", llvm);
    }

    [Fact]
    public void LargeEnumPayloadProjectionIntoAddressableLocalUsesAddressCopy()
    {
        var result = Compile(
            """
            module Demo

            struct Big
            {
                i8[min max][512] Data;
                i64[min max] Len;
            }

            enum Result
            {
                Ok(Big),
                Err(i32[min max])
            }

            unsafe ffi fn void Touch(rawptr<Big> value);

            unsafe fn Result Make()
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

                stack mut Big value = new Big();
                value.Len = acc;
                return Result.Ok(value);
            }

            unsafe fn i64[min max] Use()
            {
                stack Result result = Make();
                switch (result)
                {
                    case Result.Ok(var payload):
                        stack mut Big local = payload;
                        Touch(&local);
                        return local.Len;
                    case Result.Err(var code):
                        return code;
                }
            }
            """,
            options: new CompilerOptions(
                EmitLlvmIr: true));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvm(result);

        Assert.Contains("declare void @llvm.memcpy.p0.p0.i64", llvm);
        Assert.Matches(
            @"call void @llvm\.memcpy\.p0\.p0\.i64\(ptr align 8 %slot_local, ptr align 8 %abi_[^,]+, i64 520, i1 false\)",
            llvm);
        Assert.DoesNotContain("load %Big, ptr %abi_extract_field_load_", llvm);
        Assert.DoesNotContain("store %Big %v", llvm);
        Assert.DoesNotContain("extractvalue %Big", llvm);
    }

    [Fact]
    public void LargeEnumPayloadImmediateMoveIntoMethodLocalAliasesPayloadStorage()
    {
        var result = Compile(
            """
            module Demo

            struct Big
            {
                i8[min max][512] Data;
                i64[min max] Len;

                fn i64[min max] ReadLen(mut borrow Big self)
                {
                    stack i64[min max] a = self.Len + (self.Len / 3);
                    stack i64[min max] b = a - (a / 5);
                    stack i64[min max] c = b + (b / 7);
                    stack i64[min max] d = c - (c / 11);
                    stack i64[min max] e = d + (d / 13);
                    stack i64[min max] f = e - (e / 17);
                    stack i64[min max] g = f + (f / 19);
                    stack i64[min max] h = g - (g / 23);
                    stack i64[min max] i = h + (h / 29);
                    stack i64[min max] j = i - (i / 31);
                    stack i64[min max] k = j + (j / 37);
                    stack i64[min max] l = k - (k / 41);
                    stack i64[min max] m = l + (l / 43);
                    return m;
                }
            }

            enum Result
            {
                Ok(Big),
                Err(i32[min max])
            }

            unsafe fn Result Make()
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

                stack mut Big value = new Big();
                value.Len = acc;
                return Result.Ok(value);
            }

            unsafe fn i64[min max] Use()
            {
                stack Result result = Make();
                switch (result)
                {
                    case Result.Ok(var payload):
                        stack mut Big local = payload;
                        return local.ReadLen();
                    case Result.Err(var code):
                        return code;
                }
            }
            """,
            options: new CompilerOptions(
                EmitLlvmIr: true));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvm(result);
        var useBody = Regex.Match(llvm, @"define fastcc[^{]+ @Use\(\)[\s\S]*?^}", RegexOptions.Multiline).Value;

        Assert.DoesNotContain("%slot_local = alloca %Big", useBody, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"llvm\.memcpy[^\n]+%slot_local", useBody);
        Assert.DoesNotContain("load %Big", useBody, StringComparison.Ordinal);
        Assert.Matches(
            @"call fastcc i64 @Big_ReadLen\(ptr [^\n]*%abi_aggregate_source_field_",
            useBody);
    }

    [Fact]
    public void LargeLocalAggregateInsertedIntoEnumPayloadUsesAddressCopy()
    {
        var result = Compile(
            """
            module Demo

            struct Big
            {
                i8[min max][512] Data;
                i64[min max] Len;
            }

            enum Result
            {
                Ok(Big),
                Err(i32[min max])
            }

            unsafe ffi fn void Touch(rawptr<Big> value);

            unsafe fn Result Make(i64[min max] seed)
            {
                stack mut Big value = new Big();
                value.Len = seed;
                Touch(&value);
                return Result.Ok(value);
            }
            """,
            options: new CompilerOptions(EmitLlvmIr: true));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvm(result);

        Assert.Matches(
            @"call void @llvm\.memcpy\.p0\.p0\.i64\(ptr align 8 %abi_insert_field_store_[^,]+, ptr align 8 %slot_value, i64 520, i1 false\)",
            llvm);
        Assert.DoesNotMatch(@"store %Big %v\d+, ptr %abi_insert_field_store_", llvm);
    }

    [Fact]
    public void SmallEnumPayloadProjectionKeepsScalarLowering()
    {
        var result = Compile(
            """
            module Demo

            struct Pair
            {
                i32[min max] Left;
                i32[min max] Right;
            }

            enum Result
            {
                Ok(Pair),
                Err(i32[min max])
            }

            unsafe fn i32[min max] Use(Result result)
            {
                switch (result)
                {
                    case Result.Ok(var payload):
                        stack Pair local = payload;
                        return local.Left + local.Right;
                    case Result.Err(var code):
                        return code;
                }
            }
            """,
            options: new CompilerOptions(EmitLlvmIr: true));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvm(result);

        Assert.DoesNotContain("@llvm.memcpy", llvm, StringComparison.Ordinal);
        Assert.Contains("%Pair", llvm, StringComparison.Ordinal);
    }

    [Fact]
    public void AddressForwardedAggregateStoreDoesNotReadEndedLocalLifetime()
    {
        var result = Compile(
            """
            module Demo

            struct Inner
            {
                i64[min max] A;
                i64[min max] B;
                i64[min max] C;
            }

            struct Outer
            {
                Inner Payload;
                i64[min max] Kind;
            }

            enum Result
            {
                Ok(Outer),
                Err,
            }

            unsafe ffi fn void Touch(rawptr<Inner> value);

            unsafe fn Result Make(i64[min max] seed)
            {
                stack mut Inner inner = new Inner()
                {
                    A = seed, B = seed + 1, C = seed + 2
                };
                Touch(&inner);
                stack Outer outer = new Outer()
                {
                    Payload = inner, Kind = seed
                };
                return Result.Ok(outer);
            }
            """,
            options: new CompilerOptions(EmitLlvmIr: true));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvm(result);

        var lifetimeEndIndex = llvm.IndexOf("call void @llvm.lifetime.end.p0(i64 24, ptr %slot_inner)", StringComparison.Ordinal);
        var forwardedCopyIndex = llvm.IndexOf("ptr align 8 %slot_inner", StringComparison.Ordinal);

        Assert.True(forwardedCopyIndex >= 0, llvm);
        Assert.True(
            lifetimeEndIndex < 0 || forwardedCopyIndex < lifetimeEndIndex,
            "Address-forwarded aggregate stores must not read a local slot after its LLVM lifetime has ended.");
    }

    [Fact]
    public void LargeZeroInitializedAggregateStoresUseInlineMemset()
    {
        var result = Compile(
            """
            module Demo

            struct Buffer
            {
                i32[min max][16] Data;
            }

            unsafe ffi fn void Consume(rawptr<Buffer> buffer);

            unsafe fn void Run()
            {
                stack mut Buffer buffer = new Buffer();
                Consume(&buffer);
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("declare void @llvm.memset.inline.p0.i64", llvm);
        Assert.Contains("call void @llvm.memset.inline.p0.i64(ptr align 4 %slot_buffer, i8 0, i64 64, i1 false)", llvm);
        Assert.DoesNotContain("store %Buffer zeroinitializer", llvm);
    }

    [Fact]
    public void AggregateMoveInvalidatesAddressableSourceStorage()
    {
        var result = Compile(
            """
            module Demo

            struct Pair
            {
                i32[min max] Left;
                i32[min max] Right;

                drop
                {
                }
            }

            unsafe fn i32[min max] Run()
            {
                stack mut Pair source = new Pair()
                {
                    Left = 1, Right = 2
                };
                stack mut Pair dest = new Pair()
                {
                    Left = 0, Right = 0
                };
                stack rawptr<Pair> sourcePtr = &source;
                stack rawptr<Pair> destPtr = &dest;
                dest = source;
                source = new Pair()
                {
                    Left = 3, Right = 4
                };
                return source.Right + dest.Right;
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Equal(2, llvm.Split('\n').Count(static line => line.Contains("store i32 undef, ptr %abi_store_dest_", StringComparison.Ordinal)));
    }

    [Fact]
    public void AddressableAggregateConditionalUsesSingleAggregateAlloca()
    {
        var result = Compile(
            """
            module Demo

            struct Pair
            {
                i32[min max] Left;
                i32[min max] Right;
            }

            unsafe fn i32[min max] Run(bool flag)
            {
                stack Pair value = flag ? new Pair()
                {
                    Left = 1, Right = 2
                }:
                new Pair()
                {
                    Left = 3, Right = 4
                };
                stack rawptr<Pair> ptr = &value;
                return value.Right;
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Equal(1, llvm.Split('\n').Count(static line => line.Contains("alloca %Pair", StringComparison.Ordinal)));
        Assert.DoesNotContain("slot__tmp", llvm, StringComparison.Ordinal);
    }

    [Fact]
    public void InternalAggregateReturnUsesDirectValueAbi()
    {
        var result = Compile(
            """
            module Demo

            struct Box
            {
                i32[min max] Value;
            }

            unsafe fn Box Make()
            {
                stack mut i32[min max] acc = 977;
                stack mut i32[min max] step = 0;
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

                return new Box()
                {
                    Value = acc
                };
            }

            unsafe fn i32[min max] Run()
            {
                stack Box box = Make();
                return box.Value;
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("define fastcc %Box @Make()", llvm);
        Assert.Contains("ret %Box", llvm);
        Assert.Contains("call fastcc %Box @Make()", llvm);
        Assert.DoesNotContain("sret(%Box)", llvm);
    }

    [Fact]
    public void LargeAggregateReturnUsesSRetAbi()
    {
        var result = Compile(
            """
            module Demo

            struct Big
            {
                i64[min max] A;
                i64[min max] B;
                i64[min max] C;
            }

            unsafe fn Big Make()
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

                return new Big()
                {
                    A = acc, B = acc + 1, C = acc + 2
                };
            }

            unsafe fn i64[min max] Run()
            {
                stack Big value = Make();
                return (i64[min max])value.C;
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("define fastcc void @Make(ptr noalias sret(%Big) nonnull dereferenceable(24) align 8 %ret)", llvm);
        Assert.Contains("call fastcc void @Make(ptr noalias sret(%Big)", llvm);
        Assert.DoesNotContain("define fastcc %Big @Make()", llvm);
        Assert.DoesNotContain("call fastcc %Big @Make()", llvm);
    }

    [Fact]
    public void LargeAggregateReturnedThroughSRetMaterializesBeforeNestedInsert()
    {
        var result = Compile(
            """
            module Demo

            struct Texture
            {
                i32[min max] Id;
                i32[min max] Width;
                i32[min max] Height;
                i32[min max] Mipmaps;
                i32[min max] Format;
            }

            struct MaterialMap
            {
                Texture Texture;
                i32[min max] Color;
                i32[min max] Value;
            }

            unsafe fn Texture DefaultTexture()
            {
                stack mut i32[min max] acc = 977;
                stack mut i32[min max] step = 0;
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

                return new Texture()
                {
                    Id = acc,
                    Width = acc + 1,
                    Height = acc + 2,
                    Mipmaps = acc + 3,
                    Format = acc + 4
                };
            }

            unsafe fn MaterialMap DefaultMaterialMap()
            {
                return new MaterialMap()
                {
                    Texture = DefaultTexture(),
                    Color = 1,
                    Value = 2
                };
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvm(result);
        var body = ExtractDefinitionBody(llvm, "DefaultMaterialMap");

        Assert.Contains("call fastcc void @DefaultTexture(ptr noalias sret(%Texture)", body, StringComparison.Ordinal);
        Assert.Matches(@"%[A-Za-z0-9_]+insert_field_value_[0-9]+ = load %Texture, ptr %[A-Za-z0-9_]+callret_slot_[0-9]+", body);
        Assert.Matches(@"insertvalue %MaterialMap zeroinitializer, %Texture %[A-Za-z0-9_]+insert_field_value_[0-9]+, 0", body);
    }

    [Fact]
    public void LargeAggregateInitializerReturnMaterializesDirectlyIntoSRetBuffer()
    {
        var result = Compile(
            """
            module Demo

            struct Big
            {
                i64[min max] A;
                i64[min max] B;
                i64[min max] C;
            }

            unsafe fn Big Make()
            {
                return new Big()
                {
                    A = 1, B = 2, C = 3
                };
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("define fastcc void @Make(ptr noalias sret(%Big) nonnull dereferenceable(24) align 8 %ret)", llvm);
        Assert.Contains("call void @llvm.memset.inline.p0.i64(ptr align 8 %ret, i8 0, i64 24, i1 false)", llvm);
        Assert.Contains("store i64 1, ptr %abi_insert_field_store_", llvm);
        Assert.Contains("store i64 2, ptr %abi_insert_field_store_", llvm);
        Assert.Contains("store i64 3, ptr %abi_insert_field_store_", llvm);
        Assert.DoesNotContain("store %Big", llvm);
    }

    [Fact]
    public void LargeGenericEnumAggregateReturnMaterializesDirectlyIntoSRetBuffer()
    {
        var result = Compile(
            """
            module Demo

            struct Big
            {
                i8[min max][512] Data;
                i64[min max] Len;
            }

            enum IOResult<T>
            {
                Ok(T),
                Err(i64[min max])
            }

            unsafe fn IOResult<Big> Make()
            {
                return IOResult<Big>.Ok(new Big()
                {
                    Len = 7
                });
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);
        var makeBody = ExtractDefinitionBody(llvm, "Make");

        Assert.Contains("define fastcc void @Make(ptr noalias sret(%IOResult_Big_) nonnull dereferenceable(536) align 8 %ret)", llvm);
        Assert.Contains("call void @llvm.memset.inline.p0.i64(ptr align 8 %ret, i8 0, i64 536, i1 false)", makeBody);
        // The zero tag is covered by the zeroinitializing memset; only the payload
        // projection needs an explicit field address.
        Assert.Contains("getelementptr inbounds nuw %IOResult_Big_, ptr %ret, i32 0, i32 1", makeBody);
        Assert.Contains("store i64 7, ptr %abi_insert_field_store_", makeBody);
        Assert.DoesNotContain("insertvalue %IOResult_Big_", makeBody);
        Assert.DoesNotContain("insertvalue %Big", makeBody);
        Assert.DoesNotContain("store %IOResult_Big_", makeBody);
        Assert.DoesNotContain("store %Big", makeBody);
    }

    [Fact]
    public void LargeAggregateForwardReturnForwardsDirectCallIntoSRetBuffer()
    {
        var result = Compile(
            """
            module Demo

            struct Big
            {
                i64[min max] A;
                i64[min max] B;
                i64[min max] C;
                i64[min max] D;
                i64[min max] E;
            }

            unsafe fn Big Make()
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

                return new Big()
                {
                    A = acc, B = acc + 1, C = acc + 2, D = acc + 3, E = acc + 4
                };
            }

            unsafe fn Big Forward()
            {
                return Make();
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);
        var forwardBody = Regex.Match(llvm, @"define fastcc[^{]+ @Forward\([^\n]*\)[\s\S]*?^}", RegexOptions.Multiline).Value;

        Assert.Contains("define fastcc void @Forward(ptr noalias sret(%Big) nonnull dereferenceable(40) align 8 %ret)", llvm);
        Assert.Contains("call fastcc void @Make(ptr noalias sret(%Big) nonnull dereferenceable(40) align 8 %ret)", forwardBody);
        Assert.DoesNotContain("%abi_callret_slot_", forwardBody);
        Assert.DoesNotContain("call void @llvm.memcpy.inline.p0.p0.i64(ptr align 8 %ret", forwardBody);
        Assert.DoesNotContain("load %Big, ptr %abi_callret_slot_", forwardBody);
        Assert.DoesNotContain("store %Big", forwardBody);
    }

    [Fact]
    public void LargeAggregateLocalForwardReturnKeepsDirectCallInSRetBuffer()
    {
        var result = Compile(
            """
            module Demo

            struct Big
            {
                i64[min max] A;
                i64[min max] B;
                i64[min max] C;
                i64[min max] D;
                i64[min max] E;
            }

            unsafe fn Big Make()
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

                return new Big()
                {
                    A = acc, B = acc + 1, C = acc + 2, D = acc + 3, E = acc + 4
                };
            }

            unsafe fn Big Forward()
            {
                stack Big value = Make();
                return value;
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);
        var forwardBody = Regex.Match(llvm, @"define fastcc[^{]+ @Forward\([^\n]*\)[\s\S]*?^}", RegexOptions.Multiline).Value;

        Assert.Contains("define fastcc void @Forward(ptr noalias sret(%Big) nonnull dereferenceable(40) align 8 %ret)", llvm);
        Assert.Contains("call fastcc void @Make(ptr noalias sret(%Big) nonnull dereferenceable(40) align 8 %ret)", forwardBody);
        Assert.DoesNotContain("%abi_callret_slot_", forwardBody);
        Assert.DoesNotContain("call void @llvm.memcpy.inline.p0.p0.i64(ptr align 8 %ret", forwardBody);
        Assert.DoesNotContain("load %Big, ptr %abi_callret_slot_", forwardBody);
        Assert.DoesNotContain("store %Big", forwardBody);
    }

    [Fact]
    public void LargeAggregateReturnOfIndirectParameterSkipsEntryLoad()
    {
        var result = Compile(
            """
            module Demo

            struct Big
            {
                i64[min max] A;
                i64[min max] B;
                i64[min max] C;
                i64[min max] D;
            }

            unsafe fn Big Forward(Big value)
            {
                return value;
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("define fastcc void @Forward(ptr noalias sret(%Big) nonnull dereferenceable(32) align 8 %ret, ptr nonnull byval(%Big) noalias readonly captures(none) dereferenceable(32) align 8 %arg_value)", llvm);
        Assert.True(
            Regex.IsMatch(
                llvm,
                @"call void @llvm\.memcpy\.inline\.p0\.p0\.i64\(ptr(?: align \d+)? %ret, ptr(?: align \d+)? %arg_value, i64 32, i1 false\)",
                RegexOptions.CultureInvariant),
            "Expected Forward to copy directly from the indirect parameter pointer into the sret buffer.");
        Assert.DoesNotContain("load %Big, ptr %arg_value", llvm);
        Assert.DoesNotContain("%abi_arg_value_value", llvm);
        Assert.DoesNotContain("store %Big", llvm);
    }

    [Fact]
    public void LargeAggregateIndirectParameterForwardingUsesOriginalPointer()
    {
        var result = Compile(
            """
            module Demo

            struct Big
            {
                i64[min max] A;
                i64[min max] B;
                i64[min max] C;
                i64[min max] D;
            }

            unsafe fn Big Step(Big value)
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

                if (acc < -4000000000000000000)
                {
                    return new Big()
                    {
                        A = value.A + acc, B = value.B, C = value.C, D = value.D
                    };
                }

                return value;
            }

            unsafe fn Big Forward(Big value)
            {
                return Step(value);
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);
        var forwardBody = Regex.Match(llvm, @"define fastcc[^{]+ @Forward\([^\n]*\)[\s\S]*?^}", RegexOptions.Multiline).Value;

        Assert.Contains("define fastcc void @Forward(ptr noalias sret(%Big) nonnull dereferenceable(32) align 8 %ret, ptr nonnull byval(%Big) noalias readonly captures(none) dereferenceable(32) align 8 %arg_value)", llvm);
        Assert.Contains("call fastcc void @Step(ptr noalias sret(%Big) nonnull dereferenceable(32) align 8 %ret", forwardBody);
        Assert.Contains("ptr nonnull byval(%Big) noalias readonly captures(none) dereferenceable(32) align 8 %arg_value", forwardBody);
        Assert.DoesNotContain("%abi_callret_slot_", forwardBody);
        Assert.DoesNotContain("call void @llvm.memcpy.inline.p0.p0.i64(ptr align 8 %ret", forwardBody);
        Assert.DoesNotContain("load %Big, ptr %abi_callret_slot_", forwardBody);
        Assert.DoesNotContain("load %Big, ptr %arg_value", forwardBody);
        Assert.DoesNotContain("%abi_callarg_value", forwardBody);
    }

    [Fact]
    public void LargeAggregateParametersUseByValueIndirectAbi()
    {
        var result = Compile(
            """
            module Demo

            struct Big
            {
                i64[min max] A;
                i64[min max] B;
                i64[min max] C;
            }

            unsafe fn i64[min max] Read(Big value)
            {
                stack i64[min max] a = value.A + (value.C / 3);
                stack i64[min max] b = a - (a / 5);
                stack i64[min max] c = b + (b / 7);
                stack i64[min max] d = c - (c / 11);
                stack i64[min max] e = d + (d / 13);
                stack i64[min max] f = e - (e / 17);
                stack i64[min max] g = f + (f / 19);
                stack i64[min max] h = g - (g / 23);
                stack i64[min max] i = h + (h / 29);
                stack i64[min max] j = i - (i / 31);
                stack i64[min max] k = j + (j / 37);
                stack i64[min max] l = k - (k / 41);
                stack i64[min max] m = l + (l / 43);
                return m;
            }

            unsafe fn i64[min max] Run()
            {
                return Read(new Big()
                {
                    A = 1, B = 2, C = 3
                });
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("define fastcc i64 @Read(", llvm);
        Assert.Contains("ptr nonnull byval(%Big) noalias readonly captures(none) dereferenceable(24) align 8 %arg_value", llvm);
        Assert.Contains("memory(argmem: read)", llvm);
        Assert.Contains("call fastcc i64 @Read(ptr nonnull byval(%Big) noalias readonly captures(none) dereferenceable(24) align 8", llvm);
        Assert.DoesNotContain("define fastcc i64 @Read(%Big", llvm);
    }

    [Fact]
    public void AggregateCallReturnRegressionBenchmarkKeepsSmallAggregatesOnDirectAbi()
    {
        var result = Compile(
            """
            module Demo

            struct Pair
            {
                i32[min max] Left;
                i32[min max] Right;
            }

            unsafe fn Pair Step(Pair value, i32[min max] delta)
            {
                stack i32[min max] a = value.Left + (delta / 3);
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
                return new Pair()
                {
                    Left = m,
                    Right = value.Right + delta
                };
            }

            unsafe fn i32[min max] Run()
            {
                stack Pair current = Step(
                    Step(
                        Step(new Pair()
                        {
                            Left = 1, Right = 2
                        },
                        1),
                        1),
                    1);
                return current.Left + current.Right;
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("define fastcc %Pair @Step(%Pair %arg_value, i32 %arg_delta)", llvm);
        Assert.Contains("call fastcc %Pair @Step(%Pair", llvm);
        Assert.DoesNotContain("sret(%Pair)", llvm);
        Assert.DoesNotContain("byval(%Pair)", llvm);
    }

    [Fact]
    public void AggregateCallReturnRegressionBenchmarkKeepsLargeAggregatesOnIndirectAbi()
    {
        var result = Compile(
            """
            module Demo

            struct Big
            {
                i64[min max] A;
                i64[min max] B;
                i64[min max] C;
                i64[min max] D;
            }

            unsafe fn Big Step(Big value, i64[min max] delta)
            {
                stack i64[min max] a = value.A + (delta / 3);
                stack i64[min max] b = a - (a / 5);
                stack i64[min max] c = b + (b / 7);
                stack i64[min max] d = c - (c / 11);
                stack i64[min max] e = d + (d / 13);
                stack i64[min max] f = e - (e / 17);
                stack i64[min max] g = f + (f / 19);
                stack i64[min max] h = g - (g / 23);
                stack i64[min max] i = h + (h / 29);
                stack i64[min max] j = i - (i / 31);
                stack i64[min max] k = j + (j / 37);
                stack i64[min max] l = k - (k / 41);
                stack i64[min max] m = l + (l / 43);
                return new Big()
                {
                    A = m,
                    B = value.B + delta,
                    C = value.C + delta,
                    D = value.D + delta
                };
            }

            unsafe fn i64[min max] Run()
            {
                stack Big current = Step(
                    Step(
                        new Big()
                        {
                            A = 1, B = 2, C = 3, D = 4
                        },
                        1),
                    1);
                return current.A + current.D;
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("define fastcc void @Step(ptr noalias sret(%Big) nonnull dereferenceable(32) align 8 %ret, ptr nonnull byval(%Big) noalias readonly captures(none) dereferenceable(32) align 8 %arg_value, i64 %arg_delta)", llvm);
        Assert.Contains("call fastcc void @Step(ptr noalias sret(%Big)", llvm);
        Assert.Contains("ptr nonnull byval(%Big) noalias readonly captures(none) dereferenceable(32) align 8", llvm);
        Assert.DoesNotContain("define fastcc %Big @Step(", llvm);
    }

    [Fact]
    public void AggregateBranchJoinEmitsByValuePhiNode()
    {
        var result = Compile(
            """
            module Demo

            struct Box
            {
                i32[min max] Value;
            }

            unsafe fn i32[min max] Run(bool flag)
            {
                stack mut Box box = new Box()
                {
                    Value = 0
                };
                if (flag)
                {
                    box = new Box()
                    {
                        Value = 1
                    };
                }
                else
                {
                    box = new Box()
                    {
                        Value = 2
                    };
                }

                return box.Value;
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("phi %Box", llvm);
        Assert.Contains("extractvalue %Box", llvm);
        Assert.DoesNotContain("load %Box, ptr %slot_box", llvm);
    }

    [Fact]
    public void ValueReceiverMethodsLowerToDirectAggregateCalls()
    {
        var result = Compile(
            """
            module Demo

            struct Box
            {
                i32[min max] Value;

                unsafe fn i32[min max] Read(Box box)
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

            unsafe fn i32[min max] Run()
            {
                stack Box box = new Box()
                {
                    Value = 7
                };
                return box.Read();
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("define fastcc i32 @Box_Read(%Box %arg_box)", llvm);
        Assert.Contains("extractvalue %Box %arg_box, 0", llvm);
        Assert.Contains("call fastcc i32 @Box_Read(%Box", llvm);
        Assert.DoesNotContain("load %Box, ptr %arg_box", llvm);
    }

    [Fact]
    public void BorrowReceiverMethodsLowerToPointerReceiverCalls()
    {
        var result = Compile(
            """
            module Demo

            struct Box
            {
                i32[min max] Value;

                unsafe fn i32[min max] Read(borrow Box box)
                {
                    return box.Value;
                }
            }

            unsafe fn i32[min max] Run(borrow Box box)
            {
                return box.Read();
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("define fastcc i32 @Box_Read(ptr nonnull noalias readonly captures(none) dereferenceable(4) align 4 %arg_box)", llvm);
        Assert.Contains("define fastcc i32 @Run(ptr nonnull noalias readonly captures(none) dereferenceable(4) align 4 %arg_box)", llvm);
        Assert.Contains("call fastcc i32 @Box_Read(ptr nonnull", llvm);
        Assert.DoesNotContain("tail call fastcc i32 @Box_Read", llvm);
    }

    [Fact]
    public void IndexedFieldAddressBehindRawPointerEmitsDirectParameterGeps()
    {
        var result = Compile(
            """
            module Demo

            struct Buffer
            {
                i8[min max][16] Storage;
                i64[min max] WritePos;
            }

            unsafe fn i32[min max] Touch(rawmutptr<Buffer> buffer, i64[min max] index, i8[min max] value)
            {
                *(&(*buffer).Storage[index]) = value;
                return (i32[min max])*(&(*buffer).Storage[index]);
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("define fastcc range(i32 -128, 128) i32 @Touch(ptr captures(none) %arg_buffer, i64 %arg_index, i8 %arg_value)", llvm);
        Assert.Contains("getelementptr inbounds nuw %Buffer, ptr %arg_buffer, i32 0, i32 0", llvm);
        Assert.Contains("getelementptr [16 x i8], ptr", llvm);
        Assert.DoesNotContain("getelementptr inbounds [16 x i8], ptr", llvm);
        Assert.DoesNotContain("getelementptr inbounds nuw [16 x i8], ptr", llvm);
        Assert.DoesNotContain("alloca %Buffer", llvm);
    }

    [Fact]
    public void IndexedRawPointerElementsEmitDirectParameterGeps()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn i32[min max] Touch(rawmutptr<i8[min max]> data, i64[min max] index, i8[min max] value)
            {
                *(&data[index]) = value;
                return (i32[min max])*(&data[index]);
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("define fastcc range(i32 -128, 128) i32 @Touch(ptr captures(none) %arg_data, i64 %arg_index, i8 %arg_value)", llvm);
        Assert.Contains("getelementptr i8, ptr %arg_data, i64 %arg_index", llvm);
        Assert.DoesNotContain("getelementptr inbounds i8, ptr %arg_data, i64 %arg_index", llvm);
        Assert.DoesNotContain("alloca ptr", llvm);
    }

    [Fact]
    public void BorrowReceiverIndexedFieldAddressesEmitDirectReceiverGeps()
    {
        var result = Compile(
            """
            module Demo

            struct Buffer
            {
                i8[min max][16] Storage;

                unsafe fn void Put(borrow mut Buffer self, i64[min max] index, i8[min max] value)
                {
                    *(&self.Storage[index]) = value;
                    return;
                }

                unsafe fn i32[min max] Read(borrow Buffer self, i64[min max] index)
                {
                    return (i32[min max])*(&self.Storage[index]);
                }
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("define fastcc void @Buffer_Put(ptr nonnull noalias captures(none) dereferenceable(16) %arg_self, i64 %arg_index, i8 %arg_value)", llvm);
        Assert.Contains("define fastcc range(i32 -128, 128) i32 @Buffer_Read(ptr nonnull noalias readonly captures(none) dereferenceable(16) %arg_self, i64 %arg_index)", llvm);
        Assert.Contains("getelementptr inbounds nuw %Buffer, ptr %arg_self, i32 0, i32 0", llvm);
        Assert.Contains("getelementptr [16 x i8], ptr %v", llvm);
        Assert.DoesNotContain("alloca %Buffer", llvm);
    }

    [Fact]
    public void MutableBorrowedAggregateWriterEmitsWriteOnlyNoCaptureParameterFacts()
    {
        var result = Compile(
            """
            module Demo

            struct Box
            {
                i32[min max] Value;
            }

            unsafe fn void Touch(borrow mut Box box)
            {
                box.Value = 7;
                return;
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("define fastcc void @Touch(ptr nonnull noalias writeonly captures(none) dereferenceable(4) align 4 %arg_box)", llvm);
        Assert.DoesNotContain("define fastcc void @Touch(ptr nonnull noalias readonly", llvm);
    }

    [Fact]
    public void MutableBorrowReturnedSliceWritesDoNotEmitReadonlyParameterFacts()
    {
        var result = Compile(
            """
            module Demo

            struct Buffer
            {
                i8[min max][8] Storage;

                unsafe fn retborrow mut i8[min max][] View(borrow mut Buffer self)
                {
                    unsafe
                    {
                        return slice(&self.Storage[0], 8);
                    }
                }
            }

            unsafe fn void Touch(borrow mut Buffer buffer)
            {
                buffer.View()[0] = 7;
                return;
            }
            """);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);
        var touchHeader = ExtractDefinitionHeader(llvm, "Touch");

        Assert.DoesNotContain("readonly", touchHeader, StringComparison.Ordinal);
        Assert.Contains("store i8 7", llvm, StringComparison.Ordinal);
    }

    [Fact]
    public void FixedArrayInitializerAndIndexCanFoldToScalarReturn()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn i32[min max] Run()
            {
                stack i32[min max][3] values =
                {
                    1, 2, 3
                };
                return values[1];
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("define fastcc i32 @Run()", llvm);
        Assert.Contains("ret i32 2", llvm);
        Assert.DoesNotContain("alloca [3 x i32]", llvm);
        Assert.DoesNotContain("declare fastcc i32 @Run()", llvm);
    }

    [Fact]
    public void FixedArrayIndexAssignmentCanFoldToScalarReturn()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn i32[min max] Run()
            {
                stack mut i32[min max][3] values =
                {
                    1, 2, 3
                };
                values[1] = 9;
                return values[1];
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("ret i32 9", llvm);
        Assert.DoesNotContain("alloca [3 x i32]", llvm);
    }

    [Fact]
    public void DynamicArrayIndexEmitsAddressBasedLoad()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn i32[min max] Run(i32[min max] index)
            {
                stack i32[min max][3] values =
                {
                    1, 2, 3
                };
                return values[index];
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("define fastcc i32 @Run(i32 %arg_index)", llvm);
        Assert.Contains("alloca [3 x i32]", llvm);
        Assert.Contains("declare void @llvm.lifetime.start.p0(i64 immarg, ptr captures(none))", llvm);
        Assert.Contains("declare void @llvm.lifetime.end.p0(i64 immarg, ptr captures(none))", llvm);
        Assert.Contains("call void @llvm.lifetime.start.p0(i64 12, ptr %slot_values)", llvm);
        Assert.Contains("call void @llvm.lifetime.end.p0(i64 12, ptr %slot_values)", llvm);
        Assert.Matches(@"getelementptr \[3 x i32\], ptr %slot_values, i32 0, i32 %arg_index", llvm);
        Assert.DoesNotContain("getelementptr inbounds [3 x i32], ptr %v", llvm);
        Assert.DoesNotContain("getelementptr inbounds nuw [3 x i32], ptr %v", llvm);
        Assert.Contains("load i32, ptr", llvm);
        Assert.DoesNotContain("declare fastcc i32 @Run(i32)", llvm);
    }

    [Fact]
    public void NonAddressableFixedArrayDynamicIndexSpillsOnlyTheTemporarySource()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn i32[min max][3] Make(i32[min max] left, i32[min max] middle, i32[min max] right)
            {
                stack i32[min max][3] values =
                {
                    left, middle, right
                };
                return values;
            }

            unsafe fn i32[min max] Run(i32[min max] index, i32[min max] seed)
            {
                return Make(seed, seed + 1, seed + 2)[index];
            }
            """);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvm(result);
        var runBody = ExtractDefinitionBody(llvm, "Run");

        Assert.Contains("call fastcc [3 x i32] @Make", runBody);
        Assert.Contains("alloca [3 x i32]", runBody);
        Assert.Contains("extractvalue [3 x i32]", runBody);
        Assert.Contains("getelementptr inbounds nuw [3 x i32], ptr %slot_", runBody);
        Assert.Matches(@"getelementptr \[3 x i32\], ptr %slot_[^,]+, i32 0, i32 %arg_index", runBody);
        Assert.Contains("load i32, ptr", runBody);
        Assert.DoesNotContain("declare fastcc i32 @Run", llvm);
    }

    [Fact]
    public void SliceParameterUsesConcreteSliceAbiAndDynamicIndexLoad()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn i32[min max] Read(i32[min max][] view, i32[min max] index)
            {
                return view[index];
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("define fastcc i32 @Read({ ptr, i64 } %arg_view, i32 %arg_index)", llvm);
        Assert.Contains("extractvalue { ptr, i64 } %arg_view, 0", llvm);
        Assert.Contains("getelementptr i32, ptr", llvm);
        Assert.DoesNotContain("getelementptr inbounds i32, ptr", llvm);
        Assert.Contains("load i32, ptr", llvm);
    }

    [Fact]
    public void FixedArrayBackedSliceAndKnownTextSliceCarryProvenGepFlags()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn i32[min max] ReadView(i32[min max][3] values, u8[0 2] index)
            {
                stack i32[min max][3] local = values;
                stack i32[min max][] view = local;
                return view[index];
            }

            unsafe fn ascii SliceLiteral(u8[0 4] start, u8[0 4] length)
            {
                return "abcd"[start, length];
            }
            """);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvm(result);

        Assert.Contains("getelementptr inbounds nuw i32, ptr", llvm);
        Assert.Contains("ptr getelementptr inbounds nuw ([5 x i8], ptr @.str.0, i32 0, i32 0)", llvm);
        Assert.Matches(@"getelementptr inbounds nuw i8, ptr %v\d+_data, i64 %v\d+", llvm);
    }

    [Fact]
    public void PropagatedSliceAndTextLengthFactsCarryProvenGepFlags()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn i32[min max] ReadJoinedSlice(bool flag, u8[0 1] index)
            {
                stack i32[min max][3] left =
                {
                    1, 2, 3
                };
                stack i32[min max][5] right =
                {
                    4, 5, 6, 7, 8
                };
                stack mut i32[min max][] view = left;
                if (flag)
                {
                    view = right;
                }

                return view[index];
            }

            unsafe fn ascii ResliceText(ascii source, u8[2 5] length, u8[0 2] start)
            {
                stack ascii first = source[0, length];
                return first[start, 0];
            }
            """);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvm(result);

        Assert.Matches(@"getelementptr inbounds nuw i32, ptr %v\d+_data, i8 %arg_index", llvm);
        Assert.Matches(@"getelementptr inbounds nuw i8, ptr %v\d+_data, i64 %v\d+", llvm);
    }

    [Fact]
    public void FixedArrayParameterUsesDirectValueAbi()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn i32[min max] Read(i32[min max][2] values)
            {
                return values[0] + values[1];
            }

            unsafe fn i32[min max] Run()
            {
                stack i32[min max][2] values =
                {
                    4, 7
                };
                return Read(values);
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("define fastcc i32 @Read([2 x i32] %arg_values)", llvm);
        Assert.Contains("extractvalue [2 x i32] %arg_values, 0", llvm);
        Assert.Contains("extractvalue [2 x i32] %arg_values, 1", llvm);
        Assert.Contains("call fastcc i32 @Read([2 x i32]", llvm);
    }

    [Fact]
    public void FixedArrayParameterDynamicIndexUsesParameterSlotAddressing()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn i32[min max] Read(i32[min max][3] values, i32[min max] index)
            {
                return values[index];
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("define fastcc i32 @Read([3 x i32] %arg_values, i32 %arg_index)", llvm);
        Assert.Contains("%slot_param_values = alloca [3 x i32]", llvm);
        Assert.Contains("store [3 x i32] %arg_values, ptr %slot_param_values", llvm);
        Assert.Matches(@"getelementptr \[3 x i32\], ptr %slot_param_values, i32 0, i32 %arg_index", llvm);
        Assert.DoesNotContain("getelementptr inbounds [3 x i32], ptr %v", llvm);
        Assert.DoesNotContain("getelementptr inbounds nuw [3 x i32], ptr %v", llvm);
        Assert.Contains("load i32, ptr", llvm);
    }

    [Fact]
    public void FixedArrayDynamicIndexEmitsGepFlagsOnlyWhenRangeProvesObjectBounds()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn i32[min max] Bounded(u8[0 2] index)
            {
                stack i32[min max][3] values =
                {
                    1, 2, 3
                };
                return values[index];
            }

            unsafe fn i32[min max] Unbounded(i32[min max] index)
            {
                stack i32[min max][3] values =
                {
                    1, 2, 3
                };
                return values[index];
            }
            """);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvm(result);

        Assert.Matches(@"getelementptr inbounds nuw \[3 x i32\], ptr %slot_values, i32 0, i8 %arg_index", llvm);
        Assert.Matches(@"getelementptr \[3 x i32\], ptr %slot_values, i32 0, i32 %arg_index", llvm);
        Assert.Single(Regex.Matches(llvm, @"getelementptr inbounds nuw \[3 x i32\], ptr %slot_values, i32 0, i8 %arg_index").Cast<Match>());
        Assert.DoesNotContain("getelementptr inbounds [3 x i32], ptr %v", llvm);
    }

    [Fact]
    public void FixedArrayReturnUsesDirectValueAbi()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn i32[min max][2] Make()
            {
                stack i32[min max][2] values =
                {
                    4, 7
                };
                return values;
            }

            unsafe fn i32[min max] Run()
            {
                stack i32[min max][2] values = Make();
                return values[1];
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("define fastcc [2 x i32] @Make()", llvm);
        Assert.Contains("call fastcc [2 x i32] @Make()", llvm);
        Assert.DoesNotContain("sret([2 x i32])", llvm);
    }

    [Fact]
    public void DynamicArrayIndexMutationEmitsAddressBasedStoreAndLoad()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn i32[min max] Run(i32[min max] index)
            {
                stack mut i32[min max][3] values =
                {
                    1, 2, 3
                };
                values[index] = 9;
                return values[index];
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("define fastcc i32 @Run(i32 %arg_index)", llvm);
        Assert.Matches(@"getelementptr \[3 x i32\], ptr %slot_values, i32 0, i32 %arg_index", llvm);
        Assert.DoesNotContain("getelementptr inbounds [3 x i32], ptr %v", llvm);
        Assert.DoesNotContain("getelementptr inbounds nuw [3 x i32], ptr %v", llvm);
        Assert.Contains("call void @llvm.lifetime.start.p0(i64 12, ptr %slot_values)", llvm);
        Assert.Contains("call void @llvm.lifetime.end.p0(i64 12, ptr %slot_values)", llvm);
        Assert.Contains("store i32", llvm);
        Assert.Contains("load i32, ptr", llvm);
        Assert.DoesNotContain("declare fastcc i32 @Run(i32)", llvm);
    }

    [Fact]
    public void SliceMutationEmitsIndirectStoreAndLoad()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn i32[min max] Run(mut i32[min max][] view, i32[min max] index)
            {
                view[index] = 9;
                return view[index];
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("define fastcc i32 @Run({ ptr, i64 } %arg_view, i32 %arg_index)", llvm);
        Assert.Contains("extractvalue { ptr, i64 } %arg_view, 0", llvm);
        Assert.Contains("store i32", llvm);
        Assert.Contains("load i32, ptr", llvm);
    }

    [Fact]
    public void ImportedStarkFunctionUsesQualifiedDependencySymbol()
    {
        var result = Compile(
            """
            import Math
            module Demo

            unsafe ffi fn void Touch();

            unsafe fn i32[min max] Run(i32[min max] left, i32[min max] right)
            {
                Touch();
                return Math.Add(left, right);
            }
            """,
            new CompilerOptions(
                ModuleResolver: new InMemoryModuleResolver(
                [
                    (
                        new ResolvedModuleReference("Math", "/virtual/Math.stark", IsExternal: false),
                        """
                        module Math

                        public unsafe finite law i32[min max] Add(i32[min max] left, i32[min max] right)
                        {
                            return left + right;
                        }
                        """,
                        "/virtual/Math.stark"
                    )
                ])));

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("declare fastcc i32 @Math_Add(i32, i32)", llvm);
        Assert.Contains("call fastcc i32 @Math_Add(i32", llvm);
    }

    [Fact]
    public void RootAsmFunctionsEmitInlineAsmBodiesForTheSyscallSubset()
    {
        var result = Compile(
            """
            module Demo

            public unsafe ffi asm(x86_64) fn i64[min max] Syscall2(i64[min max] number, rawptr<i8[min max]> path)
                in("rax") number,
                in("rdi") path,
                out("rax") return,
                clobber("rcx", "r11")
            {
                "syscall"
            }

            unsafe fn i64[min max] Run(rawptr<i8[min max]> path)
            {
                return Syscall2(2, path);
            }
            """,
            new CompilerOptions(
                TargetInfo: new LlvmTargetInfo("x86_64-unknown-linux-gnu", null)));

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("define i64 @Syscall2(i64 %arg_number, ptr readonly %arg_path) nounwind inlinehint", llvm);
        Assert.Contains("call i64 asm sideeffect \"syscall\", \"={rax},0,{rdi},~{rcx},~{r11},~{memory},~{dirflag},~{fpsr},~{flags}\"(i64 %arg_number, ptr %arg_path)", llvm);
        Assert.Contains("ret i64 %asm_result", llvm);
        Assert.DoesNotContain("declare i64 @Syscall2", llvm);
    }

    [Fact]
    public void ImportedSourceAsmFunctionsEmitExternalDeclarationsAndCalls()
    {
        var result = Compile(
            """
            import Syscall
            module Demo

            unsafe fn i64[min max] Run(rawptr<i8[min max]> path)
            {
                return Syscall.Syscall2(2, path);
            }
            """,
            new CompilerOptions(
                TargetInfo: new LlvmTargetInfo("x86_64-unknown-linux-gnu", null),
                ModuleResolver: new InMemoryModuleResolver(
                [
                    (
                        new ResolvedModuleReference("Syscall", "/virtual/Syscall.stark", IsExternal: false),
                        """
                        module Syscall

                        public unsafe ffi asm(x86_64) fn i64[min max] Syscall2(i64[min max] number, rawptr<i8[min max]> path)
                            in("rax") number,
                            in("rdi") path,
                            out("rax") return,
                            clobber("rcx", "r11")
                        {
                            "syscall"
                        }
                        """,
                        "/virtual/System/Syscall.stark"
                    )
                ])));

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("declare i64 @Syscall2", llvm);
        Assert.Contains("call i64 @Syscall2(i64 2, ptr", llvm);
        Assert.DoesNotContain("; imported asm definition: Syscall.Syscall2", llvm);
        Assert.DoesNotContain("define i64 @Syscall2", llvm);
    }

    [Fact]
    public void RootAsmFunctionsEmitFloatingPointRegisterBindings()
    {
        var result = Compile(
            """
            module Demo

            public unsafe ffi asm(x86_64) fn f64 Identity(f64 value)
                in("xmm0") value,
                out("xmm0") return
            {
                "nop"
            }

            unsafe fn f64 Run(f64 value)
            {
                return Identity(value);
            }
            """,
            new CompilerOptions(
                TargetInfo: new LlvmTargetInfo("x86_64-unknown-linux-gnu", null)));

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("define double @Identity(double %arg_value) nounwind inlinehint", llvm);
        Assert.Contains("call double asm sideeffect \"nop\", \"={xmm0},0,~{memory},~{dirflag},~{fpsr},~{flags}\"(double %arg_value)", llvm);
        Assert.Contains("ret double %asm_result", llvm);
        Assert.DoesNotContain("declare double @Identity", llvm);
    }

    [Fact]
    public void ImportedGlobalsUseQualifiedDependencySymbolsAndFoldConstValues()
    {
        var result = Compile(
            """
            import Math
            module Demo

            unsafe fn i32[min max] Run()
            {
                Math.Counter = Math.Counter + 7;
                return Math.Counter + Math.Answer + Math.Hidden;
            }
            """,
            new CompilerOptions(
                ModuleResolver: new InMemoryModuleResolver(
                [
                    (
                        new ResolvedModuleReference("Math", "/virtual/Math.stark", IsExternal: false),
                        """
                        module Math

                        public const Answer = 3;
                        public static i32[min max] Hidden = 2;
                        public static mut i32[min max] Counter = 1;
                        """,
                        "/virtual/Math.stark"
                    )
                ])));

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("; imported declaration: Math.Answer", llvm);
        Assert.Contains("@Math_Answer = external local_unnamed_addr constant i8", llvm);
        Assert.Contains("; imported declaration: Math.Hidden", llvm);
        Assert.Contains("@Math_Hidden = external local_unnamed_addr constant i32", llvm);
        Assert.Contains("; imported declaration: Math.Counter", llvm);
        Assert.Contains("@Math_Counter = external global i32", llvm);
        Assert.Contains("load i32, ptr @Math_Counter", llvm);
        Assert.Matches(@"store i32 %[^,]+, ptr @Math_Counter", llvm);
        Assert.DoesNotContain("load i8, ptr @Math_Answer", llvm);
    }

    [Fact]
    public void ImportedReadonlyScalarArrayDeclarationsCarryVectorizationFriendlyAlignment()
    {
        var result = Compile(
            """
            import Tables
            module Demo

            unsafe fn i32[min max] Read(u8[0 3] index)
            {
                return Tables.Lookup[index];
            }
            """,
            new CompilerOptions(
                ModuleResolver: new InMemoryModuleResolver(
                [
                    (
                        new ResolvedModuleReference("Tables", "/virtual/Tables.stark", IsExternal: false),
                        """
                        module Tables

                        public static i32[min max][4] Lookup =
                        {
                            1, 2, 3, 4
                        };
                        """,
                        "/virtual/Tables.stark"
                    )
                ])));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvm(result);

        Assert.Contains("@Tables_Lookup = external constant [4 x i32], align 16", llvm);
        Assert.Matches(@"load i32, ptr %v\d+, align 4, !invariant\.load !\d+", llvm);
    }

    [Fact]
    public void PackageImageBackedImportedReadonlyDataPreservesInvariantMetadata()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-readonly-invariant-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public static i32[min max][3] Values =
                {
                    1, 2, 3
                };
                public static mut i32[min max] Counter = 1;
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));

            File.WriteAllText(manifestPath, manifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    unsafe fn i32[min max] Read(u8[0 2] index)
                    {
                        return Facade.Values[index] + Facade.Counter;
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    EmitLlvmIr: true,
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            var llvm = GetLlvmRaw(consumerResult);

            Assert.Contains("@Facade_Values = external constant [3 x i32]", llvm);
            Assert.Contains("external global i32", llvm);
            Assert.Matches(@"load i32, ptr %v\d+, align 4, !invariant\.load !\d+", llvm);
            Assert.DoesNotMatch(@"load i32, ptr @\w*Counter, !invariant\.load", llvm);
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

    [Fact]
    public void ImmutableGlobalAddressesLowerWithoutPointerCasts()
    {
        var result = Compile(
            """
            module Demo

            struct Box
            {
                i32[min max] Value;
            }

            static i32[min max] Counter = 0;
            static Box Current = new Box()
            {
                Value = 5
            };

            unsafe fn rawptr<i32[min max]> CounterPtr()
            {
                return &Counter;
            }

            unsafe fn rawptr<i32[min max]> FieldPtr()
            {
                return &(Current.Value);
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("@Counter = constant i32 0", llvm);
        Assert.Contains("@Current = constant %Box { i32 5 }", llvm);
        Assert.DoesNotContain("@Counter = local_unnamed_addr constant i32 0", llvm);
        Assert.DoesNotContain("@Current = local_unnamed_addr constant %Box { i32 5 }", llvm);
        Assert.Contains("ret ptr @Counter", llvm);
        Assert.Contains("getelementptr inbounds nuw %Box, ptr @Current, i32 0, i32 0", llvm);
        Assert.Equal(0, CountOccurrences(llvm, "getelementptr inbounds i8"));
    }

    [Fact]
    public void ImportedAggregateFunctionsUseCrossModuleAbiDeclarations()
    {
        var result = Compile(
            """
            import Geometry
            module Demo

            export unsafe fn i32[min max] main()
            {
                return Geometry.Read(Geometry.Make());
            }
            """,
            new CompilerOptions(
                ModuleResolver: new InMemoryModuleResolver(
                [
                    (
                        new ResolvedModuleReference("Geometry", "/virtual/Geometry.stark", IsExternal: false),
                        """
                        module Geometry

                        public struct Box
                        {
                            i32[min max] Value;
                        }

                        public unsafe fn Box Make()
                        {
                            return new Box()
                            {
                                Value = 7
                            };
                        }

                        public unsafe fn i32[min max] Read(Box box)
                        {
                            return box.Value;
                        }
                        """,
                        "/virtual/Geometry.stark"
                    )
                ])));

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("declare fastcc %Geometry_Box @Geometry_Make()", llvm);
        Assert.Contains("declare fastcc i32 @Geometry_Read(%Geometry_Box)", llvm);
        Assert.Contains("call fastcc %Geometry_Box @Geometry_Make()", llvm);
        Assert.Contains("call fastcc i32 @Geometry_Read(%Geometry_Box", llvm);
    }

    [Fact]
    public void ConstantImportedLawCallsFoldBeforeClosedWorldCloneEmission()
    {
        var result = Compile(
            """
            import Math
            module Demo

            unsafe law i32[min max] Use()
            {
                return Math.UseLaw();
            }
            """,
            new CompilerOptions(
                ModuleResolver: new InMemoryModuleResolver(
                [
                    (
                        new ResolvedModuleReference("Math", "/virtual/Math.stark", IsExternal: false),
                        """
                        module Math

                        unsafe law i32[min max] LawOnly()
                        {
                            return 1;
                        }

                        public unsafe law i32[min max] UseLaw()
                        {
                            return LawOnly();
                        }
                        """,
                        "/virtual/Math.stark"
                    )
                ])));

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.DoesNotContain("__stark_law_clone_Math_UseLaw", llvm);
        Assert.DoesNotContain("call fastcc i32 @Math_UseLaw()", llvm);
        Assert.Contains("ret i32 1", llvm);
    }

    [Fact]
    public void ConstantImportedLawCallsStillFoldInsideImpureCallers()
    {
        var result = Compile(
            """
            import Math
            module Demo

            unsafe law i32[min max] UseLawClone()
            {
                return Math.UseLaw();
            }

            unsafe fn i32[min max] UseDirect()
            {
                Touch();
                return Math.UseLaw();
            }

            unsafe ffi fn void Touch();
            """,
            new CompilerOptions(
                ModuleResolver: new InMemoryModuleResolver(
                [
                    (
                        new ResolvedModuleReference("Math", "/virtual/Math.stark", IsExternal: false),
                        """
                        module Math

                        public unsafe law i32[min max] UseLaw()
                        {
                            return 1;
                        }
                        """,
                        "/virtual/Math.stark"
                    )
                ])));

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.DoesNotContain("__stark_law_clone_Math_UseLaw", llvm);
        Assert.DoesNotContain("call fastcc i32 @Math_UseLaw()", llvm);
        Assert.Contains("call void @Touch()", llvm);
        Assert.Contains("ret i32 1", llvm);
    }

    [Fact]
    public void MixedLawAndNonLawRootCallersUseSelectiveImportedDoctrineLawClones()
    {
        var result = Compile(
            """
            import Math
            module Demo

            unsafe law i32[min max] UseLawClone(i32[min max] left, i32[min max] right)
            {
                return Math.Numbers.Add(left, right);
            }

            unsafe fn i32[min max] UseDirect(i32[min max] left, i32[min max] right)
            {
                Touch();
                return Math.Numbers.Add(left, right);
            }

            unsafe ffi fn void Touch();
            """,
            new CompilerOptions(
                ModuleResolver: new InMemoryModuleResolver(
                [
                    (
                        new ResolvedModuleReference("Math", "/virtual/Math.stark", IsExternal: false),
                        """
                        module Math

                        public doctrine Numbers
                        {
                            unsafe finite law i32[min max] Add(i32[min max] left, i32[min max] right)
                            {
                                return left + right;
                            }
                        }
                        """,
                        "/virtual/Math.stark"
                    )
                ])));

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("define internal dso_local fastcc i32 @__stark_law_clone_Math_Numbers_Add(", llvm);
        Assert.Contains("call fastcc i32 @__stark_law_clone_Math_Numbers_Add(i32 %arg_left, i32 %arg_right)", llvm);
        Assert.Contains("call fastcc i32 @Math_Numbers_Add(i32 %arg_left, i32 %arg_right)", llvm);
    }

    [Fact]
    public void MixedLawAndNonLawRootCallersUseSelectiveImportedTopLevelLawClones()
    {
        var result = Compile(
            """
            import Math
            module Demo

            unsafe law i32[min max] UseLawClone(i32[min max] left, i32[min max] right)
            {
                return Math.Add(left, right);
            }

            unsafe fn i32[min max] UseDirect(i32[min max] left, i32[min max] right)
            {
                Touch();
                return Math.Add(left, right);
            }

            unsafe ffi fn void Touch();
            """,
            new CompilerOptions(
                ModuleResolver: new InMemoryModuleResolver(
                [
                    (
                        new ResolvedModuleReference("Math", "/virtual/Math.stark", IsExternal: false),
                        """
                        module Math

                        public unsafe finite law i32[min max] Add(i32[min max] left, i32[min max] right)
                        {
                            return left + right;
                        }
                        """,
                        "/virtual/Math.stark"
                    )
                ])));

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("define internal dso_local fastcc i32 @__stark_law_clone_Math_Add(", llvm);
        Assert.Contains("call fastcc i32 @__stark_law_clone_Math_Add(i32 %arg_left, i32 %arg_right)", llvm);
        Assert.Contains("call fastcc i32 @Math_Add(i32 %arg_left, i32 %arg_right)", llvm);
    }

    [Fact]
    public void ImpureRootFunctionsDoNotCloneImportedLawBodiesIntoRootLlvm()
    {
        var result = Compile(
            """
            import Math
            module Demo

            unsafe fn i32[min max] Use(i32[min max] value)
            {
                Touch();
                return Math.UseLaw(value);
            }

            unsafe ffi fn void Touch();
            """,
            new CompilerOptions(
                ModuleResolver: new InMemoryModuleResolver(
                [
                    (
                        new ResolvedModuleReference("Math", "/virtual/Math.stark", IsExternal: false),
                        """
                        module Math

                        unsafe law i32[min max] LawOnly(i32[min max] value)
                        {
                            return value + 1;
                        }

                        public unsafe law i32[min max] UseLaw(i32[min max] value)
                        {
                            return LawOnly(value);
                        }
                        """,
                        "/virtual/Math.stark"
                    )
                ])));

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.DoesNotContain("__stark_law_clone_Math_UseLaw", llvm);
        Assert.Contains("call fastcc i32 @Math_UseLaw(i32 %arg_value)", llvm);
    }

    [Fact]
    public void ImportedLawEntrypointsWithExplicitInlineHintDoNotSpecializeIntoClones()
    {
        var result = Compile(
            """
            import Math
            module Demo

            unsafe law i32[min max] Use(i32[min max] value)
            {
                return Math.UseLaw(value);
            }
            """,
            new CompilerOptions(
                ModuleResolver: new InMemoryModuleResolver(
                [
                    (
                        new ResolvedModuleReference("Math", "/virtual/Math.stark", IsExternal: false),
                        """
                        module Math

                        public inlinehint unsafe law i32[min max] UseLaw(i32[min max] value)
                        {
                            return value + 1;
                        }
                        """,
                        "/virtual/Math.stark"
                    )
                ])));

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.DoesNotContain("__stark_law_clone_Math_UseLaw", llvm);
        Assert.Contains("call fastcc i32 @Math_UseLaw(i32 %arg_value)", llvm);
    }

    [Fact]
    public void ClosedWorldModulePrivateLawHelpersEmitAlwaysInline()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn i32[min max] Add(i32[min max] left, i32[min max] right)
            {
                return left + right;
            }

            unsafe law i32[min max] Use()
            {
                return Add(1, 2);
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("define fastcc i32 @Add(i32 %arg_left, i32 %arg_right) nounwind willreturn mustprogress nosync nofree memory(none) alwaysinline", llvm);
        Assert.Contains("define fastcc i32 @Use() nounwind willreturn mustprogress nosync nofree memory(none) alwaysinline", llvm);
        Assert.DoesNotContain("call fastcc i32 @Add(i32 1, i32 2)", llvm);
        Assert.Contains("ret i32 3", llvm);
    }

    [Fact]
    public void ModulePrivateDirectCallForwardersEmitAlwaysInline()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn i32[min max] Core(i32[min max] value)
            {
                return value + 1;
            }

            unsafe fn i32[min max] Forward(i32[min max] value)
            {
                return Core(value);
            }

            unsafe fn i32[min max] Use(i32[min max] value)
            {
                return Forward(value);
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.True(
            System.Text.RegularExpressions.Regex.IsMatch(
                llvm,
                @"define[^\r\n]*@Forward\(i32 %arg_value\)[^\r\n]*alwaysinline",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant),
            "Expected the module-private direct-call forwarder to emit with alwaysinline.");
        Assert.DoesNotContain("call fastcc i32 @Forward(i32 %arg_value)", llvm);
        Assert.DoesNotContain("call fastcc i32 @Core(", llvm);
    }

    [Fact]
    public void ModulePrivateMemberCallForwardersEmitAlwaysInline()
    {
        var result = Compile(
            """
            module Demo

            record Box(i32[min max] Value)
            {
                unsafe fn i32[min max] Bump(borrow Box self, i32[min max] delta)
                {
                    return self.Value + delta;
                }
            }

            unsafe fn i32[min max] Forward(borrow Box box, i32[min max] delta)
            {
                return box.Bump(delta);
            }

            unsafe fn i32[min max] Use(borrow Box box, i32[min max] delta)
            {
                return Forward(box, delta);
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.True(
            System.Text.RegularExpressions.Regex.IsMatch(
                llvm,
                @"define[^\r\n]*@Forward\([^\r\n]*alwaysinline",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant),
            "Expected the module-private member-call forwarder to emit with alwaysinline.");
        Assert.DoesNotContain("call fastcc i32 @Forward(", llvm, StringComparison.Ordinal);
    }

    [Fact]
    public void ModulePrivateFieldAccessWrappersEmitAlwaysInline()
    {
        var result = Compile(
            """
            module Demo

            record Inner(i32[min max] Value)
            {
            }
            record Box(Inner Inner)
            {
            }

            unsafe fn i32[min max] Read(borrow Box box)
            {
                return box.Inner.Value;
            }

            unsafe fn i32[min max] Use(borrow Box box)
            {
                return Read(box);
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.True(
            System.Text.RegularExpressions.Regex.IsMatch(
                llvm,
                @"define[^\r\n]*@Read\([^\r\n]*alwaysinline",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant),
            "Expected the module-private field-access wrapper to emit with alwaysinline.");
        Assert.DoesNotContain("call fastcc i32 @Read(", llvm, StringComparison.Ordinal);
    }

    [Fact]
    public void ModulePrivateIndexAccessWrappersEmitAlwaysInline()
    {
        var result = Compile(
            """
            module Demo

            record Box(i32[min max] Value)
            {
            }

            unsafe fn i32[min max] Read(Box[2] boxes, i32[min max] index)
            {
                return boxes[index].Value;
            }

            unsafe fn i32[min max] Use(Box[2] boxes, i32[min max] index)
            {
                return Read(boxes, index);
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.True(
            System.Text.RegularExpressions.Regex.IsMatch(
                llvm,
                @"define[^\r\n]*@Read\([^\r\n]*alwaysinline",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant),
            "Expected the module-private index-access wrapper to emit with alwaysinline.");
        Assert.Contains("call fastcc i32 @Read(", llvm, StringComparison.Ordinal);
    }

    [Fact]
    public void ModulePrivateConversionWrappersEmitAlwaysInline()
    {
        var result = Compile(
            """
            module Demo

            record Inner(i32[min max] Value)
            {
            }
            record Box(Inner Inner)
            {
            }

            unsafe fn i64[min max] Read(borrow Box box)
            {
                return (i64[min max])box.Inner.Value;
            }

            unsafe fn i64[min max] Use(borrow Box box)
            {
                return Read(box);
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.True(
            System.Text.RegularExpressions.Regex.IsMatch(
                llvm,
                @"define[^\r\n]*@Read\([^\r\n]*alwaysinline",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant),
            "Expected the module-private conversion wrapper to emit with alwaysinline.");
        Assert.DoesNotContain("call fastcc i64 @Read(", llvm, StringComparison.Ordinal);
    }

    [Fact]
    public void ModulePrivateAddressOfWrappersEmitAlwaysInline()
    {
        var result = Compile(
            """
            module Demo

            record Buffer(i32[min max][2] Values)
            {
            }

            unsafe fn rawptr<i32[min max]> Pin(borrow Buffer buffer, i32[min max] index)
            {
                return &buffer.Values[index];
            }

            unsafe fn rawptr<i32[min max]> Use(borrow Buffer buffer, i32[min max] index)
            {
                return Pin(buffer, index);
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.True(
            System.Text.RegularExpressions.Regex.IsMatch(
                llvm,
                @"define[^\r\n]*@Pin\([^\r\n]*alwaysinline",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant),
            "Expected the module-private address-of wrapper to emit with alwaysinline.");
        Assert.DoesNotContain("call fastcc ptr @Pin(", llvm, StringComparison.Ordinal);
    }

    [Fact]
    public void ModulePrivateBinaryOperatorWrappersEmitAlwaysInline()
    {
        var result = Compile(
            """
            module Demo

            record Inner(i32[min max] Value)
            {
            }
            record Box(Inner Inner)
            {
            }

            unsafe fn i32[min max] AddDelta(borrow Box box, i32[min max] delta)
            {
                return box.Inner.Value + delta;
            }

            unsafe fn i32[min max] Use(borrow Box box, i32[min max] delta)
            {
                return AddDelta(box, delta);
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.True(
            System.Text.RegularExpressions.Regex.IsMatch(
                llvm,
                @"define[^\r\n]*@AddDelta\([^\r\n]*alwaysinline",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant),
            "Expected the module-private binary-operator wrapper to emit with alwaysinline.");
        Assert.DoesNotContain("call fastcc i32 @AddDelta(", llvm, StringComparison.Ordinal);
    }

    [Fact]
    public void ModulePrivateComparisonWrappersEmitAlwaysInline()
    {
        var result = Compile(
            """
            module Demo

            record Inner(i32[min max] Value)
            {
            }
            record Box(Inner Inner)
            {
            }

            unsafe fn bool IsBelow(borrow Box box, i32[min max] limit)
            {
                return box.Inner.Value < limit;
            }

            unsafe fn bool Use(borrow Box box, i32[min max] limit)
            {
                return IsBelow(box, limit);
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.True(
            System.Text.RegularExpressions.Regex.IsMatch(
                llvm,
                @"define[^\r\n]*@IsBelow\([^\r\n]*alwaysinline",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant),
            "Expected the module-private comparison wrapper to emit with alwaysinline.");
        Assert.DoesNotContain("call fastcc i1 @IsBelow(", llvm, StringComparison.Ordinal);
    }

    [Fact]
    public void ModulePrivateTerminalIfSelectionWrappersEmitAlwaysInline()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn i32[min max] ChooseBranch(bool takeLeft, bool takeMiddle, i32[min max] left, i32[min max] middle, i32[min max] right)
            {
                if (takeLeft)
                {
                    return left;
                }
                else if (takeMiddle)
                {
                    return middle;
                }
                else
                {
                    return right;
                }
            }

            unsafe fn i32[min max] Use(bool takeLeft, bool takeMiddle, i32[min max] left, i32[min max] middle, i32[min max] right)
            {
                return ChooseBranch(takeLeft, takeMiddle, left, middle, right);
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.True(
            System.Text.RegularExpressions.Regex.IsMatch(
                llvm,
                @"define[^\r\n]*@ChooseBranch\([^\r\n]*alwaysinline",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant),
            "Expected the module-private terminal-if selection wrapper to emit with alwaysinline.");
        Assert.DoesNotContain("call fastcc i32 @ChooseBranch(", llvm, StringComparison.Ordinal);
    }

    [Fact]
    public void ModulePrivateTerminalSwitchSelectionWrappersEmitAlwaysInline()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn i32[min max] ChooseSwitch(i32[min max] selector, i32[min max] left, i32[min max] middle, i32[min max] right)
            {
                switch (selector)
                {
                    case 0:
                        return left;
                    case 1:
                        return middle;
                    default:
                        return right;
                }
            }

            unsafe fn i32[min max] Use(i32[min max] selector, i32[min max] left, i32[min max] middle, i32[min max] right)
            {
                return ChooseSwitch(selector, left, middle, right);
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.True(
            System.Text.RegularExpressions.Regex.IsMatch(
                llvm,
                @"define[^\r\n]*@ChooseSwitch\([^\r\n]*alwaysinline",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant),
            "Expected the module-private terminal-switch selection wrapper to emit with alwaysinline.");
        Assert.DoesNotContain("call fastcc i32 @ChooseSwitch(", llvm, StringComparison.Ordinal);
    }

    [Fact]
    public void ModulePrivateObjectConstructionWrappersEmitAlwaysInline()
    {
        var result = Compile(
            """
            module Demo

            struct Inner
            {
                i32[min max] Value;
            }

            struct Outer
            {
                Inner Item;
                i32[min max] Count;
            }

            unsafe fn Outer Wrap(i32[min max] value, i32[min max] count)
            {
                return new Outer()
                {
                    Item =
                    {
                        Value = value
                    },
                    Count = count
                };
            }

            unsafe fn Outer Use(i32[min max] value, i32[min max] count)
            {
                return Wrap(value, count);
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.True(
            System.Text.RegularExpressions.Regex.IsMatch(
                llvm,
                @"define[^\r\n]*@Wrap\([^\r\n]*alwaysinline",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant),
            "Expected the module-private object-construction wrapper to emit with alwaysinline.");
    }

    [Fact]
    public void ModulePrivateEnumConstructionWrappersEmitAlwaysInline()
    {
        var result = Compile(
            """
            module Demo

            enum Boxed
            {
                None,
                Value
                {
                    Data: i32[min max], Tag: i32[min max]
                },
            }

            unsafe fn Boxed Wrap(i32[min max] value, i32[min max] tag)
            {
                return Boxed.Value
                {
                    Data: value, Tag: tag
                };
            }

            unsafe fn Boxed Use(i32[min max] value, i32[min max] tag)
            {
                return Wrap(value, tag);
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.True(
            System.Text.RegularExpressions.Regex.IsMatch(
                llvm,
                @"define[^\r\n]*@Wrap\([^\r\n]*alwaysinline",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant),
            "Expected the module-private enum-construction wrapper to emit with alwaysinline.");
    }

    [Fact]
    public void ModulePrivateLocalUpdateWrappersEmitAlwaysInline()
    {
        var result = Compile(
            """
            module Demo

            record Inner(i32[min max] Value)
            {
            }
            record Box(Inner Inner)
            {
            }

            unsafe fn i32[min max] Bump(borrow Box box, i32[min max] delta)
            {
                stack mut i32[min max] current = box.Inner.Value;
                current += delta;
                return current;
            }

            unsafe fn i32[min max] Use(borrow Box box, i32[min max] delta)
            {
                return Bump(box, delta);
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.True(
            System.Text.RegularExpressions.Regex.IsMatch(
                llvm,
                @"define[^\r\n]*@Bump\([^\r\n]*alwaysinline",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant),
            "Expected the module-private local-update wrapper to emit with alwaysinline.");
    }

    [Fact]
    public void HotFunctionsEmitHotAttribute()
    {
        var result = Compile(
            """
            module Demo

            hot unsafe fn i32[min max] Add(i32[min max] left, i32[min max] right)
            {
                return left + right;
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("define fastcc i32 @Add(i32 %arg_left, i32 %arg_right)", llvm);
        Assert.Contains(" hot ", llvm);
    }

    [Fact]
    public void LibraryBuildQualifiesPublicRootSymbols()
    {
        var result = Compile(
            """
            module Math

            public unsafe finite law i32[min max] Add(i32[min max] left, i32[min max] right)
            {
                return left + right;
            }
            """,
            new CompilerOptions(
                EmitLlvmIr: true,
                QualifyModuleSymbols: true));

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("define fastcc i32 @Math_Add(i32 %arg_left, i32 %arg_right)", llvm);
    }

    [Fact]
    public void ModulePrivateFunctionsLowerWithInternalLinkage()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn i32[min max] Helper()
            {
                return 7;
            }
            """,
            new CompilerOptions(
                EmitLlvmIr: true,
                QualifyModuleSymbols: true));

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        var privateHelperHeader = ExtractDefinitionHeader(llvm, "Demo_Helper");
        Assert.Contains("define internal dso_local fastcc i32 @Demo_Helper()", privateHelperHeader);
        Assert.Contains("unnamed_addr", privateHelperHeader);
    }

    [Fact]
    public void SourceBackedGenericCallsInlineSmallConcreteMonomorphizedSymbols()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn T Identity<T>(T value)
            {
                return value;
            }

            unsafe fn i32[min max] Run(i32[min max] value)
            {
                return Identity(value);
            }
            """);

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvm(result);

        Assert.Contains("define internal dso_local fastcc i32 @__stark_mono_fn_Demo__Identity__i32(", llvm);
        Assert.DoesNotContain("declare internal fastcc i32 @__stark_mono_fn_Demo__Identity__i32(", llvm);
        Assert.DoesNotContain("call fastcc i32 @__stark_mono_fn_Demo__Identity__i32(", llvm);
        Assert.DoesNotContain("call fastcc i32 @Identity(", llvm);
    }

    [Fact]
    public void BackendOpaqueGenericFunctionsRemainOptimizationBoundariesAfterMonomorphization()
    {
        var result = Compile(
            """
            module Demo

            [Backend(Opaque)]
            unsafe fn T Identity<T>(T value)
            {
                return value;
            }

            unsafe fn i32[min max] Run(i32[min max] value)
            {
                return Identity(value);
            }
            """);

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvm(result);

        var specializationHeader = ExtractDefinitionHeader(llvm, "__stark_mono_fn_Demo__Identity__i32");
        Assert.Contains("optnone", specializationHeader);
        Assert.Contains("noinline", specializationHeader);
        Assert.DoesNotContain("alwaysinline", specializationHeader);
        Assert.Contains("call fastcc i32 @__stark_mono_fn_Demo__Identity__i32(", llvm);
    }

    [Fact]
    public void BackendOpaqueGenericTypeMethodsRemainOptimizationBoundariesAfterMonomorphization()
    {
        var result = Compile(
            """
            module Demo

            [Backend(Opaque)]
            struct Box<T>
            {
                T Value;

                unsafe fn T Read(borrow Box<T> self)
                {
                    return self.Value;
                }
            }

            unsafe fn i32[min max] Run()
            {
                stack Box<i32[min max]> box = new Box<i32[min max]>()
                {
                    Value = 7
                };
                return box.Read();
            }
            """);

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvm(result);

        var specializationHeader = llvm
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Single(static line => line.StartsWith("define ", StringComparison.Ordinal)
                && line.Contains("@__stark_mono_fn_Demo__", StringComparison.Ordinal)
                && line.Contains("Box_Read", StringComparison.Ordinal));
        var symbolMatch = Regex.Match(specializationHeader, @"@(?<symbol>__stark_mono_fn_Demo__[^(]+)\(", RegexOptions.CultureInvariant);
        Assert.True(symbolMatch.Success, specializationHeader);

        Assert.Contains("optnone", specializationHeader);
        Assert.Contains("noinline", specializationHeader);
        Assert.DoesNotContain("alwaysinline", specializationHeader);
        Assert.Contains($"call fastcc i32 @{symbolMatch.Groups["symbol"].Value}(", llvm);
    }

    [Fact]
    public void BackendOpaqueStructAndRecordMethodsRemainNarrowOptimizationBoundaries()
    {
        var result = Compile(
            """
            module Demo

            [Backend(Opaque)]
            struct Box
            {
                i32[min max] Value;

                unsafe finite law i32[min max] Read(borrow Box self)
                {
                    return self.Value;
                }
            }

            struct FastBox
            {
                i32[min max] Value;

                unsafe finite law i32[min max] Read(borrow FastBox self)
                {
                    return self.Value;
                }
            }

            [Backend(Opaque)]
            record Point(i32[min max] X)
            {
                unsafe finite law i32[min max] Read(borrow Point self)
                {
                    return self.X;
                }
            }

            record FastPoint(i32[min max] X)
            {
                unsafe finite law i32[min max] Read(borrow FastPoint self)
                {
                    return self.X;
                }
            }

            unsafe fn i32[min max] Run()
            {
                stack Box box = new Box()
                {
                    Value = 7
                };
                stack FastBox fastBox = new FastBox()
                {
                    Value = 3
                };
                stack Point point = new Point(11);
                stack FastPoint fastPoint = new FastPoint(5);
                return box.Read() + fastBox.Read() + point.Read() + fastPoint.Read();
            }
            """);

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvm(result);

        var boxReadHeader = ExtractDefinitionHeader(llvm, "Box_Read");
        Assert.Contains("optnone", boxReadHeader);
        Assert.Contains("noinline", boxReadHeader);
        Assert.Contains("call fastcc i32 @Box_Read(", llvm);

        var fastBoxReadHeader = ExtractDefinitionHeader(llvm, "FastBox_Read");
        Assert.Contains("alwaysinline", fastBoxReadHeader);
        Assert.DoesNotContain("optnone", fastBoxReadHeader);
        Assert.DoesNotContain("noinline", fastBoxReadHeader);

        var pointReadHeader = ExtractDefinitionHeader(llvm, "Point_Read");
        Assert.Contains("optnone", pointReadHeader);
        Assert.Contains("noinline", pointReadHeader);
        Assert.Contains("call fastcc i32 @Point_Read(", llvm);

        var fastPointReadHeader = ExtractDefinitionHeader(llvm, "FastPoint_Read");
        Assert.Contains("alwaysinline", fastPointReadHeader);
        Assert.DoesNotContain("optnone", fastPointReadHeader);
        Assert.DoesNotContain("noinline", fastPointReadHeader);
    }

    [Fact]
    public void BackendOpaqueDoctrineMethodsRemainNarrowOptimizationBoundaries()
    {
        var result = Compile(
            """
            module Demo

            [Backend(Opaque)]
            doctrine SlowNumbers
            {
                unsafe finite law i32[min max] Add(i32[min max] left, i32[min max] right)
                {
                    return left + right;
                }
            }

            doctrine FastNumbers
            {
                unsafe finite law i32[min max] Add(i32[min max] left, i32[min max] right)
                {
                    return left + right;
                }
            }

            unsafe fn i32[min max] Run(i32[min max] value)
            {
                return SlowNumbers.Add(value, 1) + FastNumbers.Add(value, 2);
            }
            """);

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvm(result);

        var slowAddHeader = ExtractDefinitionHeader(llvm, "SlowNumbers_Add");
        Assert.Contains("optnone", slowAddHeader);
        Assert.Contains("noinline", slowAddHeader);
        Assert.Contains("call fastcc i32 @SlowNumbers_Add(", llvm);

        var fastAddHeader = ExtractDefinitionHeader(llvm, "FastNumbers_Add");
        Assert.Contains("alwaysinline", fastAddHeader);
        Assert.DoesNotContain("optnone", fastAddHeader);
        Assert.DoesNotContain("noinline", fastAddHeader);
        Assert.DoesNotContain("call fastcc i32 @FastNumbers_Add(", llvm);
    }

    [Fact]
    public void NestedSourceBackedGenericCallsInlineTransitiveSmallConcreteMonomorphizedSymbols()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn T Identity<T>(T value)
            {
                return value;
            }

            unsafe fn T Forward<T>(T value)
            {
                return Identity(value);
            }

            unsafe fn i32[min max] Run(i32[min max] value)
            {
                return Forward(value);
            }
            """);

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvm(result);

        Assert.Contains("define internal dso_local fastcc i32 @__stark_mono_fn_Demo__Forward__i32(", llvm);
        Assert.Contains("define internal dso_local fastcc i32 @__stark_mono_fn_Demo__Identity__i32(", llvm);
        Assert.DoesNotContain("call fastcc i32 @__stark_mono_fn_Demo__Forward__i32(", llvm);
        Assert.DoesNotContain("call fastcc i32 @__stark_mono_fn_Demo__Identity__i32(", llvm);
        Assert.DoesNotContain("call fastcc i32 @Forward(", llvm);
        Assert.DoesNotContain("call fastcc i32 @Identity(", llvm);
    }

    [Fact]
    public void ImportedSourceBackedGenericSpecializationsUseLinkOnceOdrComdatDefinitionsAndInlineSmallCalls()
    {
        var result = Compile(
            """
            import Facade
            module Demo

            unsafe fn i32[min max] Run(i32[min max] value)
            {
                return Facade.Identity(value);
            }
            """,
            new CompilerOptions(
                ModuleResolver: new InMemoryModuleResolver(
                [
                    (
                        new ResolvedModuleReference("Facade", "/virtual/Facade.stark", IsExternal: false),
                        """
                        module Facade

                        public unsafe fn T Identity<T>(T value)
                        {
                            return value;
                        }
                        """,
                        "/virtual/Facade.stark"
                    )
                ])));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvm(result);

        Assert.Contains("$__stark_mono_fn_Facade__Facade_Identity__i32 = comdat any", llvm);
        var specializationHeader = ExtractDefinitionHeader(llvm, "__stark_mono_fn_Facade__Facade_Identity__i32");
        Assert.Contains("define linkonce_odr dso_local fastcc i32 @__stark_mono_fn_Facade__Facade_Identity__i32(", specializationHeader);
        Assert.Contains("local_unnamed_addr", specializationHeader);
        Assert.Contains("comdat", specializationHeader);
        Assert.DoesNotContain("call fastcc i32 @__stark_mono_fn_Facade__Facade_Identity__i32(", llvm);
        Assert.DoesNotContain("define internal dso_local fastcc i32 @__stark_mono_fn_Facade__Facade_Identity__i32(", llvm);
    }

    [Fact]
    public void ImportedSourceBackedMutableBorrowGenericSpecializationsDoNotDenyArgumentMemoryAccess()
    {
        var result = Compile(
            """
            import Facade
            module Demo

            unsafe fn void Run()
            {
                stack mut Facade.Box<i32[min max]> box = new Facade.Box<i32[min max]>()
                {
                    Value = 1
                };
                box.Store(2);
                return;
            }
            """,
            new CompilerOptions(
                ModuleResolver: new InMemoryModuleResolver(
                [
                    (
                        new ResolvedModuleReference("Facade", "/virtual/Facade.stark", IsExternal: false),
                        """
                        module Facade

                        public struct Box<T>
                        {
                            T Value;

                            public unsafe fn void Store(mut borrow Box<T> self, T value)
                            {
                                TouchOther();
                                self.Value = value;
                                return;
                            }
                        }

                        public unsafe ffi fn void TouchOther();
                        """,
                        "/virtual/Facade.stark"
                    )
                ])));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvm(result);

        var specializationHeader = llvm
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Single(static line => line.StartsWith("define ", StringComparison.Ordinal)
                && line.Contains("@__stark_mono_fn_Facade__", StringComparison.Ordinal)
                && line.Contains("Box_Store", StringComparison.Ordinal));
        Assert.DoesNotContain("argmem: none", specializationHeader);
    }

    [Fact]
    public void ManifestBackedGenericCallsInlineSmallConcreteMonomorphizedSymbolsFromPackageImageTemplates()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-manifest-generic-template-llvm-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public unsafe fn T Identity<T>(T value)
                {
                    return value;
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static d => d.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));

            var typedOnlyManifest = manifest with
            {
                Modules = manifest.Modules
                    .Select(module => module.ModuleName == "Facade"
                        ? module with
                        {
                            Functions = [],
                            Types = [],
                            Globals = [],
                            TypeAliases = [],
                            TypedInterface = facadeModule.TypedInterface,
                            CompilerFacts = facadeModule.CompilerFacts,
                            GenericTemplates = facadeModule.GenericTemplates
                        }
                        : module)
                    .ToArray()
            };

            File.WriteAllText(manifestPath, typedOnlyManifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    unsafe fn i32[min max] Run(i32[min max] value)
                    {
                        return Facade.Identity(value);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    EmitLlvmIr: true,
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static d => d.ToString())));
            var llvm = GetLlvm(consumerResult);

            var specializationHeader = ExtractDefinitionHeader(llvm, "__stark_mono_fn_Demo__Facade_Identity__i32");
            Assert.Contains("define internal dso_local fastcc", specializationHeader);
            Assert.Contains("@__stark_mono_fn_Demo__Facade_Identity__i32(", specializationHeader);
            Assert.DoesNotContain("available_externally", specializationHeader);
            Assert.DoesNotContain("define internal dso_local fastcc i32 @__stark_law_clone___stark_mono_fn_Demo__Facade_Identity__i32(", llvm);
            Assert.DoesNotContain("call fastcc i32 @__stark_mono_fn_Demo__Facade_Identity__i32(", llvm);
            Assert.DoesNotContain("call fastcc i32 @__stark_law_clone___stark_mono_fn_Demo__Facade_Identity__i32(", llvm);
            Assert.DoesNotContain("call fastcc i32 @Facade_Identity(", llvm);
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

    [Fact]
    public void ManifestBackedGenericInlineClosureCallSpecializesFromPackageImageTypedBody()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-manifest-inline-closure-template-llvm-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public inline fn T Apply<T>(T value, inline closure<fn T(T)> op)
                {
                    return op(value);
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static d => d.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));
            var applyTemplate = Assert.Single(
                facadeModule.GenericTemplates!.Functions,
                static template => template.QualifiedResolvedName == "Facade.Apply");
            var returnStatement = Assert.Single(applyTemplate.TypedBody!.Statements);
            Assert.Equal("return", returnStatement.Kind);
            Assert.Equal("closure-call", returnStatement.Expression.Kind);

            var typedOnlyManifest = manifest with
            {
                Modules = manifest.Modules
                    .Select(module => module.ModuleName == "Facade"
                        ? module with
                        {
                            Functions = [],
                            Types = [],
                            Globals = [],
                            TypeAliases = [],
                            TypedInterface = facadeModule.TypedInterface,
                            CompilerFacts = facadeModule.CompilerFacts,
                            GenericTemplates = facadeModule.GenericTemplates
                        }
                        : module)
                    .ToArray()
            };

            File.WriteAllText(manifestPath, typedOnlyManifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    inline fn i32[min max] LocalApply(inline closure<fn i32[min max](i32[min max])> op)
                    {
                        stack i32[min max] value = 4;
                        return Facade.Apply(value, op);
                    }

                    fn i32[min max] Run(i32[min max] offset)
                    {
                        return LocalApply(capture(copy offset) (i32[min max] value) => value + offset);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    EmitLlvmIr: true,
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static d => d.ToString())));
            var llvm = GetLlvm(consumerResult);
            var runBody = ExtractDefinitionBody(llvm, "Run");

            Assert.Contains("add nsw i32 4, %arg_offset", runBody, StringComparison.Ordinal);
            Assert.DoesNotContain("insertvalue { ptr, ptr }", runBody, StringComparison.Ordinal);
            Assert.DoesNotContain("extractvalue { ptr, ptr }", runBody, StringComparison.Ordinal);
            Assert.DoesNotContain("call fastcc", runBody, StringComparison.Ordinal);
            Assert.DoesNotContain("@Run___lambda_", llvm, StringComparison.Ordinal);
            Assert.DoesNotContain("call fastcc i32 @__stark_mono_fn_Demo__Facade_Apply__i32(", llvm, StringComparison.Ordinal);
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

    [Fact]
    public void ManifestBackedNestedGenericCallsInlineTransitiveSmallConcreteMonomorphizedSymbolsFromPackageImageTemplates()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-manifest-nested-generic-template-llvm-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public unsafe fn T Identity<T>(T value)
                {
                    return value;
                }

                public unsafe fn T Forward<T>(T value)
                {
                    return Identity(value);
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static d => d.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));

            var typedOnlyManifest = manifest with
            {
                Modules = manifest.Modules
                    .Select(module => module.ModuleName == "Facade"
                        ? module with
                        {
                            Functions = [],
                            Types = [],
                            Globals = [],
                            TypeAliases = [],
                            TypedInterface = facadeModule.TypedInterface,
                            CompilerFacts = facadeModule.CompilerFacts,
                            GenericTemplates = facadeModule.GenericTemplates
                        }
                        : module)
                    .ToArray()
            };

            File.WriteAllText(manifestPath, typedOnlyManifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    unsafe fn i32[min max] Run(i32[min max] value)
                    {
                        return Facade.Forward(value);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    EmitLlvmIr: true,
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static d => d.ToString())));
            var llvm = GetLlvm(consumerResult);

            var forwardHeader = ExtractDefinitionHeader(llvm, "__stark_mono_fn_Demo__Facade_Forward__i32");
            var identityHeader = ExtractDefinitionHeader(llvm, "__stark_mono_fn_Demo__Facade_Identity__i32");
            Assert.Contains("define internal dso_local fastcc", forwardHeader);
            Assert.Contains("@__stark_mono_fn_Demo__Facade_Forward__i32(", forwardHeader);
            Assert.DoesNotContain("available_externally", forwardHeader);
            Assert.Contains("define internal dso_local fastcc", identityHeader);
            Assert.Contains("@__stark_mono_fn_Demo__Facade_Identity__i32(", identityHeader);
            Assert.DoesNotContain("available_externally", identityHeader);
            Assert.DoesNotContain("define internal dso_local fastcc i32 @__stark_law_clone___stark_mono_fn_Demo__Facade_Forward__i32(", llvm);
            Assert.DoesNotContain("define internal dso_local fastcc i32 @__stark_law_clone___stark_mono_fn_Demo__Facade_Identity__i32(", llvm);
            Assert.DoesNotContain("call fastcc i32 @__stark_mono_fn_Demo__Facade_Forward__i32(", llvm);
            Assert.DoesNotContain("call fastcc i32 @__stark_mono_fn_Demo__Facade_Identity__i32(", llvm);
            Assert.DoesNotContain("call fastcc i32 @__stark_law_clone___stark_mono_fn_Demo__Facade_Forward__i32(", llvm);
            Assert.DoesNotContain("call fastcc i32 @__stark_law_clone___stark_mono_fn_Demo__Facade_Identity__i32(", llvm);
            Assert.DoesNotContain("call fastcc i32 @Facade_Forward(", llvm);
            Assert.DoesNotContain("call fastcc i32 @Facade_Identity(", llvm);
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

    [Fact]
    public void RepeatedManifestBackedNestedGenericCallsStayInternalAndDeduplicatedAtLlvmEmission()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-manifest-nested-generic-dedup-llvm-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public unsafe fn T Identity<T>(T value)
                {
                    return value;
                }

                public unsafe fn T Forward<T>(T value)
                {
                    return Identity(value);
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static d => d.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));

            var typedOnlyManifest = manifest with
            {
                Modules = manifest.Modules
                    .Select(module => module.ModuleName == "Facade"
                        ? module with
                        {
                            Functions = [],
                            Types = [],
                            Globals = [],
                            TypeAliases = [],
                            TypedInterface = facadeModule.TypedInterface,
                            CompilerFacts = facadeModule.CompilerFacts,
                            GenericTemplates = facadeModule.GenericTemplates
                        }
                        : module)
                    .ToArray()
            };

            File.WriteAllText(manifestPath, typedOnlyManifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    unsafe fn i32[min max] Run(i32[min max] left, i32[min max] right)
                    {
                        stack i32[min max] first = Facade.Forward(left);
                        stack i32[min max] second = Facade.Forward(right);
                        return first + second;
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    EmitLlvmIr: true,
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static d => d.ToString())));
            var llvm = GetLlvm(consumerResult);

            Assert.Matches(
                @"define internal[^\r\n]*@__stark_mono_fn_Demo__Facade_Forward__i32\(",
                llvm);
            Assert.Matches(
                @"define internal[^\r\n]*@__stark_mono_fn_Demo__Facade_Identity__i32\(",
                llvm);
            Assert.Equal(0, CountOccurrences(llvm, "define internal dso_local fastcc i32 @__stark_law_clone___stark_mono_fn_Demo__Facade_Forward__i32("));
            Assert.Equal(0, CountOccurrences(llvm, "define internal dso_local fastcc i32 @__stark_law_clone___stark_mono_fn_Demo__Facade_Identity__i32("));
            Assert.Equal(0, CountOccurrences(llvm, "available_externally"));
            Assert.Equal(0, CountOccurrences(llvm, "call fastcc i32 @__stark_mono_fn_Demo__Facade_Forward__i32("));
            Assert.Equal(0, CountOccurrences(llvm, "call fastcc i32 @__stark_mono_fn_Demo__Facade_Identity__i32("));
            Assert.Equal(0, CountOccurrences(llvm, "call fastcc i32 @__stark_law_clone___stark_mono_fn_Demo__Facade_Forward__i32("));
            Assert.Equal(0, CountOccurrences(llvm, "call fastcc i32 @__stark_law_clone___stark_mono_fn_Demo__Facade_Identity__i32("));
            Assert.DoesNotContain("define linkonce_odr dso_local fastcc i32 @__stark_mono_fn_Demo__Facade_Forward__i32(", llvm);
            Assert.DoesNotContain("define linkonce_odr dso_local fastcc i32 @__stark_mono_fn_Demo__Facade_Identity__i32(", llvm);
            Assert.DoesNotContain("define linkonce_odr dso_local fastcc i32 @__stark_law_clone___stark_mono_fn_Demo__Facade_Forward__i32(", llvm);
            Assert.DoesNotContain("define linkonce_odr dso_local fastcc i32 @__stark_law_clone___stark_mono_fn_Demo__Facade_Identity__i32(", llvm);
            Assert.DoesNotContain("call fastcc i32 @Facade_Forward(", llvm);
            Assert.DoesNotContain("call fastcc i32 @Facade_Identity(", llvm);
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

    [Fact]
    public void LocalFixedArrayCanBeCoercedToSliceForCalls()
    {
        var result = Compile(
            """
            module Demo

            noinline unsafe fn i32[min max] Read(i32[min max][] view, i32[min max] index)
            {
                return view[index];
            }

            unsafe fn i32[min max] Run(i32[min max] index)
            {
                stack i32[min max][3] values =
                {
                    4, 7, 9
                };
                return Read(values, index);
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("alloca [3 x i32]", llvm);
        Assert.Equal(3, llvm.Split('\n').Count(static line => line.Contains("store i32 %abi_scalar_extract_", StringComparison.Ordinal)));
        Assert.DoesNotContain("store [3 x i32]", llvm);
        Assert.Contains("getelementptr inbounds nuw [3 x i32], ptr %slot_values, i32 0, i32 0", llvm);
        Assert.Contains("insertvalue { ptr, i64 } zeroinitializer, ptr", llvm);
        Assert.Contains("call fastcc i32 @Read({ ptr, i64 }", llvm);
    }

    [Fact]
    public void BorrowedSliceParametersAcceptReusableSliceViews()
    {
        var result = Compile(
            """
            module Demo

            noinline unsafe fn void Fill(borrow mut i32[min max][] view)
            {
                view[0] = 9;
                return;
            }

            noinline unsafe fn i32[min max] Read(borrow i32[min max][] view)
            {
                return view[0];
            }

            unsafe fn i32[min max] Run()
            {
                stack mut i32[min max][3] values =
                {
                    1, 2, 3
                };
                stack mut i32[min max][] view = values;
                Fill(view);
                Fill(values);
                return Read(view);
            }
            """);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvm(result);

        Assert.Contains("define fastcc void @Fill(ptr", llvm);
        Assert.Contains("define fastcc i32 @Read(ptr", llvm);
        Assert.Contains("call fastcc void @Fill(ptr", llvm);
        Assert.Contains("call fastcc i32 @Read(ptr", llvm);
    }

    [Fact]
    public void BorrowedAggregateCallReusesPromotedLocalSlot()
    {
        var result = Compile(
            """
            module Demo

            struct Box
            {
                i32[min max] Value;
            }

            unsafe noinline fn void Touch(borrow Box box)
            {
                return;
            }

            unsafe fn void Forward(borrow Box box)
            {
                stack borrow Box aliasBox = box;
                Touch(aliasBox);
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("define fastcc void @Touch(ptr nonnull noalias readonly captures(none) dereferenceable(4) align 4 %arg_box)", llvm);
        Assert.Contains("define fastcc void @Forward(ptr nonnull noalias readonly captures(none) dereferenceable(4) align 4 %arg_box)", llvm);
        // The alias slot is fully promoted away; the forwarded call uses the
        // original parameter pointer directly.
        Assert.Matches(@"call fastcc void @Touch\(ptr [^\n]*%arg_box\)", llvm);
        Assert.DoesNotContain("abi_borrow_ptr_load", llvm);
        Assert.DoesNotContain("callarg_box", llvm);
    }

    [Fact]
    public void MutableBorrowReceiverForwardingReusesOriginalParameterPointer()
    {
        var result = Compile(
            """
            module Demo

            struct Counter
            {
                i32[min max] Value;

                unsafe fn void Reset(borrow mut Counter self)
                {
                    self.Value = 0;
                    return;
                }

                unsafe fn void ResetThenAdd(borrow mut Counter self, i32[min max] value)
                {
                    self.Reset();
                    self.Value += value;
                    return;
                }
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("define fastcc void @Counter_Reset(ptr nonnull noalias writeonly captures(none) dereferenceable(4) align 4 %arg_self)", llvm);
        Assert.Contains("define fastcc void @Counter_ResetThenAdd(ptr nonnull noalias writeonly captures(none) dereferenceable(4) align 4 %arg_self, i32 %arg_value)", llvm);
        Assert.Matches(@"call fastcc void @Counter_Reset\(ptr [^\n]*%arg_self\)", llvm);
        Assert.DoesNotContain("callarg_self", llvm);
    }

    [Fact]
    public void MutableBorrowFieldReceiverUsesOriginalFieldAddress()
    {
        var result = Compile(
            """
            module Demo

            struct Inner
            {
                i32[min max] Value;

                unsafe fn void Set(borrow mut Inner self, i32[min max] value)
                {
                    stack i32[min max] a = value + (value / 3);
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
                    self.Value = m;
                    return;
                }
            }

            struct Outer
            {
                Inner Item;

                unsafe fn void SetItem(borrow mut Outer self, i32[min max] value)
                {
                    self.Item.Set(value);
                    return;
                }
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static d => d.ToString())));
        var llvm = GetLlvm(result);

        Assert.Contains("define fastcc void @Inner_Set(ptr nonnull noalias writeonly captures(none) dereferenceable(4) align 4 %arg_self", llvm);
        Assert.Contains("call fastcc void @Inner_Set(ptr nonnull", llvm);
        Assert.DoesNotContain("call fastcc void @Inner_Set(ptr %slot_", llvm);
        Assert.DoesNotContain("alloca %Inner", llvm);
    }

    [Fact]
    public void StoreBorrowFieldsUsePointerStorageAndProjectThroughBorrowedValue()
    {
        var result = Compile(
            """
            module Demo

            struct Counter
            {
                i32[min max] Value;

                fn Holder Hold(storeborrow mut Counter self)
                {
                    return new Holder(self);
                }
            }

            struct Holder
            {
                storeborrow mut Counter Owner;
                bool Held;

                Holder(storeborrow mut Counter owner)
                {
                    self.Owner = owner;
                    self.Held = true;
                }

                unsafe fn void Set(mut borrow Holder self, i32[min max] value)
                {
                    self.Owner.Value = value;
                    return;
                }
            }

            unsafe fn i32[min max] Run()
            {
                stack mut Counter counter = new Counter()
                {
                    Value = 1
                };
                stack mut Holder holder = counter.Hold();
                holder.Set(42);
                if (!*(&holder.Held))
                {
                    return 0;
                }

                return counter.Value;
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static d => d.ToString())));
        var llvm = GetLlvm(result);
        var holdHeader = ExtractDefinitionHeader(llvm, "Counter_Hold");
        var setHeader = ExtractDefinitionHeader(llvm, "Holder_Set");
        var runHeader = ExtractDefinitionHeader(llvm, "Run");

        Assert.Contains("%Counter = type { i32 }", llvm);
        Assert.Contains("%Holder = type { ptr, i1 }", llvm);
        Assert.Contains("!\"Stark TBAA\"", llvm);
        Assert.Contains("load ptr", llvm);
        Assert.Contains("getelementptr inbounds nuw %Counter", llvm);
        Assert.Contains("captures(address", holdHeader);
        Assert.DoesNotContain("captures(none)", holdHeader, StringComparison.Ordinal);
        Assert.DoesNotContain("noalias", holdHeader, StringComparison.Ordinal);
        Assert.DoesNotContain("noalias", setHeader, StringComparison.Ordinal);
        Assert.DoesNotContain("writeonly", setHeader, StringComparison.Ordinal);
        Assert.DoesNotContain("memory(none)", runHeader, StringComparison.Ordinal);
        Assert.DoesNotContain("%Holder = type { %Counter", llvm);

        var pointerDescriptor = Regex.Match(
            llvm,
            @"!(\d+) = !\{!""stark\.ptr"", !\d+, i64 0\}",
            RegexOptions.CultureInvariant);
        var boolDescriptor = Regex.Match(
            llvm,
            @"!(\d+) = !\{!""stark\.bool"", !\d+, i64 0\}",
            RegexOptions.CultureInvariant);
        var holderDescriptor = Regex.Match(
            llvm,
            @"!\d+ = !\{!""stark\.Holder"", (?<fields>[^}]*)\}",
            RegexOptions.CultureInvariant);
        Assert.True(pointerDescriptor.Success, llvm);
        Assert.True(boolDescriptor.Success, llvm);
        Assert.True(holderDescriptor.Success, llvm);
        Assert.Contains($"!{pointerDescriptor.Groups[1].Value}, i64 0", holderDescriptor.Groups["fields"].Value);
        Assert.Contains($"!{boolDescriptor.Groups[1].Value}, i64 8", holderDescriptor.Groups["fields"].Value);
    }

    [Fact]
    public void RawPointerLoopBackedgeCastsMaterializePhiIncomingValues()
    {
        var result = Compile(
            """
            module Demo

            struct Node
            {
                rawmutptr<Node> Next;
            }

            unsafe fn rawmutptr<Node> Walk(rawmutptr<Node> head)
            {
                stack mut rawmutptr<Node> current = head;
                while willexit (current != null)
                {
                    stack rawmutptr<Node> next = (*current).Next;
                    stack rawmutptr<i8[min max]> erased = (rawmutptr<i8[min max]>)next;
                    current = (rawmutptr<Node>)erased;
                }

                return current;
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static d => d.ToString())));
        var llvm = GetLlvm(result);

        Assert.Contains("phi ptr", llvm);
        Assert.DoesNotContain("select i1 true, ptr", llvm);
        Assert.DoesNotContain("ptrtoint", llvm);
        Assert.DoesNotContain("inttoptr", llvm);
    }

    [Fact]
    public void ManifestBackedMutableBorrowMethodsStayWritableAtImportedDeclarations()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-manifest-borrow-llvm-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public struct Counter
                {
                    i32[min max] Value;

                    unsafe fn void Reset(borrow mut Counter self)
                    {
                        self.Value = 0;
                        return;
                    }
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static d => d.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));

            File.WriteAllText(manifestPath, manifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    unsafe fn void Run()
                    {
                        stack mut Facade.Counter counter = new Facade.Counter()
                        {
                            Value = 1
                        };
                        counter.Reset();
                        return;
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    EmitLlvmIr: true,
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static d => d.ToString())));
            var llvm = GetLlvm(consumerResult);

            Assert.Contains(
                "declare fastcc void @Facade_Counter_Reset(ptr nonnull noalias writeonly captures(none) dereferenceable(4) align 4)",
                llvm);
            Assert.DoesNotContain(
                "declare fastcc void @Facade_Counter_Reset(ptr nonnull noalias readonly",
                llvm,
                StringComparison.Ordinal);
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

    [Fact]
    public void DoctrineLawCallsEmitDirectReadonlyNoCaptureSignatures()
    {
        var result = Compile(
            """
            module Demo

            struct Box
            {
                i32[min max] Value;
            }

            doctrine Inspect
            {
                unsafe finite law i32[min max] Read(borrow mut Box box)
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

            unsafe finite law i32[min max] Run(borrow mut Box box)
            {
                return Inspect.Read(box);
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("define fastcc i32 @Inspect_Read(ptr nonnull noalias readonly captures(none) dereferenceable(4) align 4 %arg_box)", llvm);
        Assert.Contains("define fastcc i32 @Run(ptr nonnull noalias readonly captures(none) dereferenceable(4) align 4 %arg_box)", llvm);
        Assert.Contains("call fastcc i32 @Inspect_Read(ptr nonnull", llvm);
    }

    [Fact]
    public void ConfiguredTargetInfoIsEmittedInHeader()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn i32[min max] Run()
            {
                return 1;
            }
            """,
            new CompilerOptions(
                EmitLlvmIr: true,
                TargetInfo: new LlvmTargetInfo(
                    "test-triple",
                    "e-test-layout")));

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("target datalayout = \"e-test-layout\"", llvm);
        Assert.Contains("target triple = \"test-triple\"", llvm);
    }

    [Fact]
    public void ShortCircuitAndTernaryEmitBranchesAndPhi()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn i32[min max] Run(bool left, bool right)
            {
                return left && right ? 1 : 2;
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Matches(@"define fastcc .* i32 @Run\(i1 .*%arg_left, i1 .*%arg_right\)", llvm);
        Assert.Contains("phi i1", llvm);
        Assert.Contains("br i1", llvm);
        Assert.Contains("select i1", llvm);
        Assert.Contains("ret i32 %select_", llvm);
    }

    [Fact]
    public void PointerOperatorsAndExplicitConversionsEmitRawMemoryAccess()
    {
        var result = Compile(
            """
            module Demo

            static mut i32[min max] Counter = 0;

            unsafe fn i32[min max] Run(i64[min max] bits)
            {
                stack mut i32[min max] value = 1;
                stack rawmutptr<i32[min max]> ptr = &value;
                stack rawptr<i32[min max]> readonlyPtr = (rawptr<i32[min max]>)ptr;
                *ptr = (i32[min max])bits;
                Counter = *readonlyPtr;
                return *(&Counter);
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("trunc i64 %arg_bits to i32", llvm);
        Assert.Contains("store i32 %v2, ptr %slot_value", llvm);
        Assert.DoesNotContain("getelementptr inbounds nuw i8, ptr %", llvm);
        Assert.DoesNotContain("ptrtoint", llvm);
        Assert.DoesNotContain("inttoptr", llvm);
        Assert.Contains("store i32", llvm);
        Assert.Contains("ptr @Counter", llvm);
        Assert.Contains("load i32, ptr @Counter", llvm);
    }

    private static StarkPackageModuleManifest WithEffectiveLegacyCompilerSectionCopies(StarkPackageModuleManifest module)
    {
        return module with
        {
            TypedInterface = module.EffectiveTypedInterface,
            CompilerFacts = module.EffectiveCompilerFacts,
            GenericTemplates = module.EffectiveGenericTemplates
        };
    }

    [Fact]
    public void RuntimeDisjointRawPointerConditionEmitsByteRangeComparisons()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn u8[0 1] Check(rawptr<i32[min max]> left, rawptr<i32[min max]> right)
            {
                if disjoint(left, right)
                {
                    return 1;
                }

                return 0;
            }
            """,
            options: new CompilerOptions(EmitLlvmIr: true));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = NormalizeLlvm(GetLlvmRaw(result));

        Assert.Contains("getelementptr i8, ptr", llvm, StringComparison.Ordinal);
        Assert.Contains("icmp ule ptr", llvm, StringComparison.Ordinal);
        Assert.Contains("or i1", llvm, StringComparison.Ordinal);
        Assert.Contains("select i1", llvm, StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeDisjointRawPointerRegionsUseElementCounts()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn bool Check(
                rawptr<i32[min max]>[count] left,
                rawptr<i32[min max]>[count] right,
                u8[0 10] count)
                {
                    if disjoint(left[0, count], right[0, count])
                {
                    return true;
                }

                return false;
            }
            """,
            options: new CompilerOptions(EmitLlvmIr: true));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);

        Assert.Matches(@"getelementptr(?: inbounds nuw)? i32, ptr", llvm);
        Assert.Matches(@"mul(?: nuw nsw)? i64", llvm);
        Assert.Contains("icmp ule ptr", llvm, StringComparison.Ordinal);
        Assert.Contains("or i1", llvm, StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeDisjointBorrowConditionEmitsAddressRangeComparisons()
    {
        var result = Compile(
            """
            module Demo

            struct Box
            {
                i32[min max] Value;
            }

            unsafe fn u8[0 1] Check(borrow Box left, borrow Box right)
            {
                if disjoint(left, right)
                {
                    return 1;
                }

                return 0;
            }
            """,
            options: new CompilerOptions(EmitLlvmIr: true));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);

        Assert.Contains("getelementptr i8, ptr", llvm, StringComparison.Ordinal);
        Assert.Contains("icmp ule ptr", llvm, StringComparison.Ordinal);
        Assert.Contains("or i1", llvm, StringComparison.Ordinal);
        // The two constant-returning branches fold into a select over the
        // disjointness comparison.
        Assert.Contains("select i1", llvm, StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeDisjointSliceConditionUsesViewDataAndLength()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn bool Check(i32[min max][] left, i32[min max][] right)
            {
                if disjoint(left, right)
                {
                    return true;
                }

                return false;
            }
            """,
            options: new CompilerOptions(EmitLlvmIr: true));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);

        Assert.Contains("extractvalue", llvm, StringComparison.Ordinal);
        Assert.Contains("getelementptr i8, ptr", llvm, StringComparison.Ordinal);
        Assert.Contains("icmp ule ptr", llvm, StringComparison.Ordinal);
        Assert.Contains("or i1", llvm, StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeDisjointTrueBranchEmitsScopedNoAliasMetadata()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn i32[min max] Check(
                rawmutptr<i32[min max]> left,
                rawmutptr<i32[min max]> right)
                {
                    if disjoint(left, right)
                {
                    *left = 7;
                    return *right;
                }

                return *right;
            }
            """,
            options: new CompilerOptions(EmitLlvmIr: true));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);

        Assert.Contains("stark.noalias.Check.runtime-disjoint-0.param.left", llvm, StringComparison.Ordinal);
        Assert.Contains("stark.noalias.Check.runtime-disjoint-0.param.right", llvm, StringComparison.Ordinal);
        Assert.Matches(@"store i32 .* !alias\.scope !\d+, !noalias !\d+", llvm);
        Assert.Matches(@"load i32, ptr .* !alias\.scope !\d+, !noalias !\d+", llvm);
    }

    [Fact]
    public void RuntimeDisjointSameBaseRawPointerSubregionsEmitScopedNoAliasMetadata()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn i32[min max] Check(rawmutptr<i32[min max]>[8] ptr)
            {
                if disjoint(ptr[0, 4], ptr[4, 4])
                {
                    *(&ptr[0]) = 7;
                    return *(&ptr[4]);
                }

                return 0;
            }
            """,
            options: new CompilerOptions(EmitLlvmIr: true));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);

        Assert.Contains("stark.noalias.Check.runtime-disjoint-0.param.ptr[0..3]", llvm, StringComparison.Ordinal);
        Assert.Contains("stark.noalias.Check.runtime-disjoint-0.param.ptr[4..7]", llvm, StringComparison.Ordinal);
        Assert.Matches(@"store i32 .* !alias\.scope !\d+, !noalias !\d+", llvm);
        Assert.Matches(@"load i32, ptr .* !alias\.scope !\d+, !noalias !\d+", llvm);
        Assert.DoesNotContain("\"separate_storage\"", llvm, StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeDisjointSameBaseSubregionScopedNoAliasDoesNotEscapeTrueBranch()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn i32[min max] Check(rawmutptr<i32[min max]>[8] ptr)
            {
                if disjoint(ptr[0, 4], ptr[4, 4])
                {
                    *(&ptr[0]) = 7;
                }

                return *(&ptr[4]);
            }
            """,
            options: new CompilerOptions(EmitLlvmIr: true));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);

        Assert.Equal(1, CountOccurrences(llvm, "!alias.scope"));
        Assert.Matches(@"store i32 .* !alias\.scope !\d+, !noalias !\d+", llvm);
        Assert.DoesNotMatch(@"load i32, ptr .* !alias\.scope !\d+, !noalias !\d+", llvm);
    }

    [Fact]
    public void UnsafeAssumeDisjointEmitsScopedNoAliasMetadataWithoutRuntimeCheck()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn i32[min max] Check(
                rawmutptr<i32[min max]> left,
                rawmutptr<i32[min max]> right)
                where overlap(left, right)
                {
                    assume disjoint(left, right)
                {
                    *left = 7;
                    return *right;
                }

                return 0;
            }
            """,
            options: new CompilerOptions(EmitLlvmIr: true));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);

        Assert.Contains("stark.noalias.Check.unsafe-assume-disjoint-0.param.left", llvm, StringComparison.Ordinal);
        Assert.Contains("stark.noalias.Check.unsafe-assume-disjoint-0.param.right", llvm, StringComparison.Ordinal);
        Assert.Matches(@"store i32 .* !alias\.scope !\d+, !noalias !\d+", llvm);
        Assert.Matches(@"load i32, ptr .* !alias\.scope !\d+, !noalias !\d+", llvm);
        Assert.DoesNotContain("icmp ule ptr", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("\"separate_storage\"", llvm, StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeScopedNoAliasFactsAttachToEligibleDirectCalls()
    {
        var result = Compile(
            """
            module Demo

            struct Box
            {
                i32[min max] Value;
            }

            noinline law i32[min max] Read(borrow Box box)
            {
                return box.Value;
            }

            unsafe fn i32[min max] Check(borrow Box left, borrow Box right)
                where overlap(left, right)
                {
                    assume disjoint(left, right)
                {
                    return Read(left);
                }

                return 0;
            }
            """,
            options: new CompilerOptions(EmitLlvmIr: true));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);
        var checkBody = ExtractDefinitionBody(llvm, "Check");

        Assert.Contains("stark.noalias.Check.unsafe-assume-disjoint-0.param.left", llvm, StringComparison.Ordinal);
        Assert.Contains("stark.noalias.Check.unsafe-assume-disjoint-0.param.right", llvm, StringComparison.Ordinal);
        Assert.Matches(
            @"call fastcc i32 @Read\(ptr [^\n]*\), !alias\.scope !\d+, !noalias !\d+",
            checkBody);
    }

    [Fact]
    public void IndependentLoopLawCallsEmitAccessGroupMetadata()
    {
        var result = Compile(
            """
            module Demo

            struct Cell
            {
                i32[min max] Value;
            }

            noinline law i32[min max] Read(borrow Cell cell)
            {
                return cell.Value;
            }

            unsafe fn void Copy(
                borrow Cell[] input,
                borrow mut Cell[] output,
                u8[0 10] count)
                {
                    for willexit independent (stack mut u8[0 10] index = 0; index < count; index += 1)
                {
                    output[index].Value = Read(input[index]);
                }

                return;
            }
            """,
            options: new CompilerOptions(EmitLlvmIr: true));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);
        var copyBody = ExtractDefinitionBody(llvm, "Copy");

        Assert.Matches(@"call fastcc i32 @Read\(ptr [^\n]*\).*!llvm\.access\.group !\d+", copyBody);
        Assert.Contains("!\"llvm.loop.parallel_accesses\"", llvm, StringComparison.Ordinal);
    }

    [Fact]
    public void FfiCallsDoNotReceiveScopedNoAliasOrAccessGroupMetadata()
    {
        var result = Compile(
            """
            module Demo

            public unsafe ffi fn void Foreign(rawmutptr<i32[min max]> ptr);

            unsafe fn void Run(rawmutptr<i32[min max]> left, rawmutptr<i32[min max]> right)
                where overlap(left, right)
                {
                    assume disjoint(left, right)
                {
                    Foreign(left);
                }

                return;
            }
            """,
            options: new CompilerOptions(EmitLlvmIr: true));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var runBody = ExtractDefinitionBody(GetLlvmRaw(result), "Run");
        var foreignCall = Regex.Match(runBody, @"call void @Foreign\([^\n]+\)").Value;

        Assert.NotEmpty(foreignCall);
        Assert.DoesNotContain("!alias.scope", foreignCall, StringComparison.Ordinal);
        Assert.DoesNotContain("!noalias", foreignCall, StringComparison.Ordinal);
        Assert.DoesNotContain("!llvm.access.group", foreignCall, StringComparison.Ordinal);
    }

    [Fact]
    public void DisjointRawPointerParametersEmitNoAliasAttributes()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn void Touch(rawmutptr<i32[min max]> left, rawmutptr<i32[min max]> right)
            {
                return;
            }
            """,
            options: new CompilerOptions(EmitLlvmIr: true));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var header = ExtractDefinitionHeader(GetLlvmRaw(result), "Touch");

        Assert.Contains("ptr noundef noalias captures(none) %arg_left", header, StringComparison.Ordinal);
        Assert.Contains("ptr noundef noalias captures(none) %arg_right", header, StringComparison.Ordinal);
    }

    [Fact]
    public void DefaultNonOverlapRawPointerParametersEmitNoAliasAttributes()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn void Touch(rawmutptr<i32[min max]> left, rawmutptr<i32[min max]> right)
            {
                *left = *right;
                return;
            }
            """,
            options: new CompilerOptions(EmitLlvmIr: true));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var header = ExtractDefinitionHeader(GetLlvmRaw(result), "Touch");

        Assert.Contains("ptr noundef noalias captures(none) %arg_left", header, StringComparison.Ordinal);
        Assert.Contains("ptr noundef noalias captures(none) %arg_right", header, StringComparison.Ordinal);
    }

    [Fact]
    public void OverlapRawPointerParametersDoNotEmitNoAliasAttributes()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn void Touch(rawmutptr<i32[min max]> left, rawmutptr<i32[min max]> right)
                where overlap(left, right)
                {
                    *left = *right;
                return;
            }
            """,
            options: new CompilerOptions(EmitLlvmIr: true));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var header = ExtractDefinitionHeader(GetLlvmRaw(result), "Touch");

        Assert.Contains("ptr noundef captures(none) %arg_left", header, StringComparison.Ordinal);
        Assert.Contains("ptr noundef captures(none) %arg_right", header, StringComparison.Ordinal);
        Assert.DoesNotContain("noalias", header, StringComparison.Ordinal);
    }

    [Fact]
    public void ConstantBoundedRawPointerParametersEmitDereferenceabilityAttributes()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn void Touch(rawptr<i32[min max]>[4] input)
            {
                return;
            }
            """,
            options: new CompilerOptions(EmitLlvmIr: true));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var header = ExtractDefinitionHeader(GetLlvmRaw(result), "Touch");

        Assert.Contains("ptr noundef nonnull dereferenceable(16) align 4 readonly captures(none) %arg_input", header, StringComparison.Ordinal);
    }

    [Fact]
    public void PositiveVariableBoundedRawPointerParametersEmitMinimumDereferenceabilityAttributes()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn void TouchPositive(rawptr<i32[min max]>[count] input, u8[1 10] count)
            {
                return;
            }

            unsafe fn void TouchMaybeEmpty(rawptr<i32[min max]>[count] input, u8[0 10] count)
            {
                return;
            }
            """,
            options: new CompilerOptions(EmitLlvmIr: true));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);
        var positiveHeader = ExtractDefinitionHeader(llvm, "TouchPositive");
        var maybeEmptyHeader = ExtractDefinitionHeader(llvm, "TouchMaybeEmpty");

        Assert.Contains("ptr noundef nonnull dereferenceable(4) align 4 readonly captures(none) %arg_input", positiveHeader, StringComparison.Ordinal);
        Assert.Contains("ptr noundef readonly captures(none) %arg_input", maybeEmptyHeader, StringComparison.Ordinal);
        Assert.DoesNotContain("nonnull", maybeEmptyHeader, StringComparison.Ordinal);
        Assert.DoesNotContain("dereferenceable(", maybeEmptyHeader, StringComparison.Ordinal);
        Assert.DoesNotContain("declare void @llvm.assume(i1 noundef)", llvm, StringComparison.Ordinal);
    }

    [Fact]
    public void ZeroLengthBoundedRawPointerParametersDoNotEmitNonnullOrDereferenceabilityAttributes()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn void TouchEmpty(rawptr<i32[min max]>[0] input)
            {
                return;
            }
            """,
            options: new CompilerOptions(EmitLlvmIr: true));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var header = ExtractDefinitionHeader(GetLlvmRaw(result), "TouchEmpty");

        Assert.Contains("ptr noundef readonly captures(none) %arg_input", header, StringComparison.Ordinal);
        Assert.DoesNotContain("nonnull", header, StringComparison.Ordinal);
        Assert.DoesNotContain("dereferenceable(", header, StringComparison.Ordinal);
    }

    [Fact]
    public void FunctionPointerCallsWithPositiveVariableBoundedRawPointerParametersEmitCallAttributes()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn void Touch(rawptr<i32[min max]>[count] input, u8[1 10] count)
            {
                return;
            }

            unsafe fn void Apply(
                fnptr<unsafe fn void(rawptr<i32[min max]>[arg1], u8[1 10])> op,
                rawptr<i32[min max]>[count] input,
                u8[1 10] count)
                {
                    op(input, count);
                return;
            }

            unsafe fn void Run(rawptr<i32[min max]>[count] input, u8[1 10] count)
            {
                Apply(Touch, input, count);
                return;
            }
            """,
            options: new CompilerOptions());

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);
        var applyHeader = ExtractDefinitionHeader(llvm, "Apply");
        var applyBody = ExtractDefinitionBody(llvm, "Apply");

        Assert.Contains("ptr noundef nonnull %arg_op", applyHeader, StringComparison.Ordinal);
        Assert.Contains("ptr noundef nonnull dereferenceable(4) align 4 readonly captures(none) %arg_input", applyHeader, StringComparison.Ordinal);
        Assert.Contains(
            "call fastcc void %arg_op(ptr nonnull dereferenceable(4) align 4 readonly captures(address, read_provenance) %arg_input, i8 range(i8 1, 11) %arg_count)",
            applyBody,
            StringComparison.Ordinal);
    }

    [Fact]
    public void BoundedRawPointerArgumentFactsStrengthenDirectAndIndirectCallAttributes()
    {
        var result = Compile(
            """
            module Demo

            noinline unsafe fn i32[min max] Touch(rawptr<i32[min max]>[count] input, u8[0 10] count)
            {
                return *input;
            }

            unsafe fn i32[min max] Direct(rawptr<i32[min max]>[count] input, u8[4 10] count)
            {
                return Touch(input, count);
            }

            unsafe fn i32[min max] Indirect(
                fnptr<fn i32[min max](rawptr<i32[min max]>[arg1], u8[0 10])> op,
                rawptr<i32[min max]>[count] input,
                u8[4 10] count)
                {
                    return op(input, count);
            }
            """,
            options: new CompilerOptions(EmitLlvmIr: true));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);
        var touchHeader = ExtractDefinitionHeader(llvm, "Touch");
        var directBody = ExtractDefinitionBody(llvm, "Direct");
        var indirectBody = ExtractDefinitionBody(llvm, "Indirect");

        Assert.Contains("ptr noundef readonly captures(none) %arg_input", touchHeader, StringComparison.Ordinal);
        Assert.DoesNotContain("nonnull", touchHeader, StringComparison.Ordinal);
        Assert.Matches(
            @"call fastcc i32 @Touch\(ptr nonnull dereferenceable\(16\) align 4 readonly captures\(none\) %arg_input, i8 range\(i8 0, 11\) %arg_count\)",
            directBody);
        Assert.Matches(
            @"call fastcc i32 %arg_op\(ptr nonnull dereferenceable\(16\) align 4 readonly captures\(address, read_provenance\) %arg_input, i8 range\(i8 0, 11\) %arg_count\)",
            indirectBody);
    }

    [Fact]
    public void BoundedRawPointerRegionFactsEmitInboundsGepForProvenParameterIndexes()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn i32[min max] Read(rawptr<i32[min max]>[4] input, u8[0 3] index)
            {
                return *(&input[index]);
            }
            """,
            options: new CompilerOptions(EmitLlvmIr: true));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);

        Assert.Matches(@"getelementptr inbounds nuw i32, ptr %arg_input, i8 %arg_index", llvm);
    }

    [Fact]
    public void RawSlicesCarryBoundedRegionFactsIntoSliceElementGepFlags()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn i32[min max] Read(
                rawptr<i32[min max]>[count] input,
                u8[4 10] count,
                u8[0 3] index)
                {
                    unsafe
                {
                    stack i32[min max][] view = slice(input, count);
                    return view[index];
                }
            }
            """,
            options: new CompilerOptions(EmitLlvmIr: true));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);

        Assert.Matches(@"getelementptr inbounds nuw i32, ptr %v\d+_data, i8 %arg_index", llvm);
    }

    [Fact]
    public void DisjointRawPointerParameterAccessesEmitScopedNoAliasMetadata()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn i32[min max] Touch(
                rawmutptr<i32[min max]> left,
                rawmutptr<i32[min max]> right)
                {
                    *left = 11;
                return *right;
            }
            """,
            options: new CompilerOptions(EmitLlvmIr: true));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);

        Assert.Contains("stark.noalias.Touch.param.left", llvm, StringComparison.Ordinal);
        Assert.Contains("stark.noalias.Touch.param.right", llvm, StringComparison.Ordinal);
        Assert.Matches(@"store i32 .* !alias\.scope !\d+, !noalias !\d+", llvm);
        Assert.Matches(@"load i32, ptr .* !alias\.scope !\d+, !noalias !\d+", llvm);
    }

    [Fact]
    public void DefaultNonOverlapRawPointerParameterAccessesEmitScopedNoAliasMetadata()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn i32[min max] Touch(
                rawmutptr<i32[min max]> left,
                rawmutptr<i32[min max]> right)
                {
                    *left = 11;
                return *right;
            }
            """,
            options: new CompilerOptions(EmitLlvmIr: true));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);

        Assert.Contains("stark.noalias.Touch.param.left", llvm, StringComparison.Ordinal);
        Assert.Contains("stark.noalias.Touch.param.right", llvm, StringComparison.Ordinal);
        Assert.Matches(@"store i32 .* !alias\.scope !\d+, !noalias !\d+", llvm);
        Assert.Matches(@"load i32, ptr .* !alias\.scope !\d+, !noalias !\d+", llvm);
    }

    [Fact]
    public void ConstRawPointerParametersEmitReadonlyAttributes()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn void Inspect(const rawmutptr<i32[min max]> ptr)
            {
                return;
            }
            """,
            options: new CompilerOptions(EmitLlvmIr: true));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var header = ExtractDefinitionHeader(GetLlvmRaw(result), "Inspect");

        Assert.Contains("ptr noundef readonly captures(none) %arg_ptr", header, StringComparison.Ordinal);
    }

    [Fact]
    public void ConstRawPointerParameterLoadsDoNotEmitInvariantLoadMetadata()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn i32[min max] Read(const rawmutptr<i32[min max]> ptr)
            {
                return *ptr;
            }
            """,
            options: new CompilerOptions(EmitLlvmIr: true));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);

        Assert.DoesNotMatch(@"load i32, ptr .* !invariant\.load !\d+", llvm);
    }

    [Fact]
    public void RawSlicesPreserveRuntimeDisjointScopedNoAliasMetadata()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn i32[min max] CopyFirst(
                rawmutptr<i32[min max]>[count] left,
                rawptr<i32[min max]>[count] right,
                u8[1 10] count)
                {
                    if disjoint(left[0, count], right[0, count])
                {
                    unsafe
                    {
                        stack mut mut i32[min max][] leftView = slice(left, count);
                        stack i32[min max][] rightView = slice(right, count);
                        leftView[0] = rightView[0];
                        return rightView[0];
                    }
                }

                return 0;
            }
            """,
            options: new CompilerOptions(EmitLlvmIr: true));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);

        Assert.Contains("stark.noalias.CopyFirst.runtime-disjoint-0.param.left[0..9]", llvm, StringComparison.Ordinal);
        Assert.Contains("stark.noalias.CopyFirst.runtime-disjoint-0.param.right[0..9]", llvm, StringComparison.Ordinal);
        Assert.Matches(@"store i32 .* !alias\.scope !\d+, !noalias !\d+", llvm);
        Assert.Matches(@"load i32, ptr .* !alias\.scope !\d+, !noalias !\d+", llvm);
    }

    [Fact]
    public void RawSlicesFromConstPointersDoNotEmitInvariantLoadMetadata()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn i32[min max] Read(
                const rawmutptr<i32[min max]>[count] pointer,
                u8[1 10] count)
                {
                    unsafe
                {
                    stack frozen i32[min max][] view = slice(pointer, count);
                    return view[0];
                }
            }
            """,
            options: new CompilerOptions(EmitLlvmIr: true));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);

        Assert.DoesNotMatch(@"load i32, ptr .* !invariant\.load !\d+", llvm);
    }

    [Fact]
    public void RawSlicesFromConstPointerLocalsDoNotEmitInvariantLoadMetadata()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn i32[min max] Read(
                const rawmutptr<i32[min max]>[count] pointer,
                u8[1 10] count)
                {
                    stack rawptr<frozen i32[min max]> local = pointer;
                unsafe
                {
                    stack frozen i32[min max][] view = slice(local, count);
                    return view[0];
                }
            }
            """,
            options: new CompilerOptions(EmitLlvmIr: true));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);

        Assert.DoesNotMatch(@"load i32, ptr .* !invariant\.load !\d+", llvm);
    }

    [Fact]
    public void WillexitScalarLoopsEmitMustProgressLoopMetadata()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn u8[0 10] Run(u8[0 10] limit)
            {
                stack mut u8[0 10] value = 0;
                while willexit (value < limit)
                {
                    value += 1;
                }

                return value;
            }
            """,
            options: new CompilerOptions(EmitLlvmIr: true));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);

        Assert.Contains("!llvm.loop !", llvm, StringComparison.Ordinal);
        Assert.Contains("!\"llvm.loop.mustprogress\"", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("!\"llvm.loop.parallel_accesses\"", llvm, StringComparison.Ordinal);
    }

    [Fact]
    public void NonDeterministicScalarLoopsDoNotEmitMustProgressLoopMetadata()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn u8[0 10] Run(u8[0 10] limit)
            {
                stack mut u8[0 10] value = 0;
                while non-deterministic (value < limit)
                {
                    value += 1;
                }

                return value;
            }
            """,
            options: new CompilerOptions(EmitLlvmIr: true));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);

        Assert.DoesNotContain("!\"llvm.loop.mustprogress\"", llvm, StringComparison.Ordinal);
    }

    [Fact]
    public void IndependentScalarLoopsEmitMustProgressLoopMetadata()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn u8[0 10] Run()
            {
                stack mut u8[0 10] value = 0;
                while willexit independent (value < 4)
                {
                    value += 1;
                }

                return value;
            }
            """,
            options: new CompilerOptions(EmitLlvmIr: true));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);

        Assert.Contains("!llvm.loop !", llvm, StringComparison.Ordinal);
        Assert.Contains("!\"llvm.loop.mustprogress\"", llvm, StringComparison.Ordinal);
    }

    [Fact]
    public void IndependentForLoopsEmitMustProgressLoopMetadata()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn u8[0 10] Run()
            {
                stack mut u8[0 10] sum = 0;
                for willexit independent (stack mut u8[0 10] index = 0; index < 4; index += 1)
                {
                    sum += index;
                }

                return sum;
            }
            """,
            options: new CompilerOptions(EmitLlvmIr: true));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);

        Assert.Contains("!llvm.loop !", llvm, StringComparison.Ordinal);
        Assert.Contains("!\"llvm.loop.mustprogress\"", llvm, StringComparison.Ordinal);
    }

    [Fact]
    public void IndependentSliceLoopsEmitAccessGroupAndParallelLoopMetadata()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn void Add(
                borrow i32[min max][] left,
                borrow i32[min max][] right,
                borrow mut i32[min max][] output,
                u8[0 10] count)
                {
                    for willexit independent (stack mut u8[0 10] index = 0; index < count; index += 1)
                {
                    output[index] = left[index] + right[index];
                }

                return;
            }
            """,
            options: new CompilerOptions(EmitLlvmIr: true));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);

        Assert.Contains("!llvm.access.group !", llvm, StringComparison.Ordinal);
        Assert.Contains("!\"llvm.loop.parallel_accesses\"", llvm, StringComparison.Ordinal);
        Assert.Contains("!\"llvm.loop.mustprogress\"", llvm, StringComparison.Ordinal);
    }

    [Fact]
    public void FixedArrayToSliceCallArgumentsMaterializeSliceAbiSlots()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn void Touch(borrow i64[min max][] values)
            {
                return;
            }

            unsafe fn void Run()
            {
                stack mut i64[min max][2] values =
                {
                    1, 2
                };
                Touch(values);
                return;
            }
            """,
            options: new CompilerOptions(EmitLlvmIr: true));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);

        Assert.Contains("store { ptr, i64 }", llvm, StringComparison.Ordinal);
        Assert.Matches(@"call fastcc void @Touch\(ptr [^\n]*%slot__tmp\d+_slice\)", llvm);
        Assert.DoesNotMatch(@"call fastcc void @Touch\(ptr %v\d+\)", llvm);
    }

    [Fact]
    public void IndependentSliceLoopsWithConditionalsEmitAccessGroupAndParallelLoopMetadata()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn void SelectPositive(
                borrow i32[min max][] left,
                borrow i32[min max][] right,
                borrow mut i32[min max][] output,
                u8[0 10] count)
                {
                    for willexit independent (stack mut u8[0 10] index = 0; index < count; index += 1)
                {
                    if (left[index] > 0)
                    {
                        output[index] = left[index];
                    }
                    else
                    {
                        output[index] = right[index];
                    }
                }

                return;
            }
            """,
            options: new CompilerOptions(EmitLlvmIr: true));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);

        Assert.Contains("!llvm.access.group !", llvm, StringComparison.Ordinal);
        Assert.Contains("!\"llvm.loop.parallel_accesses\"", llvm, StringComparison.Ordinal);
        Assert.Contains("!\"llvm.loop.mustprogress\"", llvm, StringComparison.Ordinal);
    }

    [Fact]
    public void IndependentSliceLoopsWithMemberProjectionsEmitAccessGroupAndParallelLoopMetadata()
    {
        var result = Compile(
            """
            module Demo

            struct Cell
            {
                i32[min max] Value;
            }

            unsafe fn void Copy(
                borrow Cell[] input,
                borrow mut Cell[] output,
                u8[0 10] count)
                {
                    for willexit independent (stack mut u8[0 10] index = 0; index < count; index += 1)
                {
                    output[index].Value = input[index].Value;
                }

                return;
            }
            """,
            options: new CompilerOptions(EmitLlvmIr: true));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);

        Assert.Contains("!llvm.access.group !", llvm, StringComparison.Ordinal);
        Assert.Contains("!\"llvm.loop.parallel_accesses\"", llvm, StringComparison.Ordinal);
        Assert.Contains("!\"llvm.loop.mustprogress\"", llvm, StringComparison.Ordinal);
    }

    [Fact]
    public void IndependentBoundedRawPointerLoopsEmitAccessGroupAndParallelLoopMetadata()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn void Copy(
                rawptr<i32[min max]>[count] input,
                rawmutptr<i32[min max]>[count] output,
                u8[0 10] count)
                where disjoint(input[0, count], output[0, count])
                {
                    for willexit independent (stack mut u8[0 10] index = 0; index < count; index += 1)
                {
                    *(&output[index]) = *(&input[index]) + 1;
                }

                return;
            }
            """,
            options: new CompilerOptions(EmitLlvmIr: true));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);

        Assert.Contains("!llvm.access.group !", llvm, StringComparison.Ordinal);
        Assert.Contains("!\"llvm.loop.parallel_accesses\"", llvm, StringComparison.Ordinal);
        Assert.Contains("!\"llvm.loop.mustprogress\"", llvm, StringComparison.Ordinal);
    }

    [Fact]
    public void IndependentBoundedRawPointerLoopsUseRuntimeRegionFactsForParallelMetadata()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn void Copy(
                rawptr<i32[min max]>[count] input,
                rawmutptr<i32[min max]>[count] output,
                u8[0 10] count)
                where overlap(input, output)
                {
                    if disjoint(input[0, count], output[0, count])
                {
                    for willexit independent (stack mut u8[0 10] index = 0; index < count; index += 1)
                    {
                        *(&output[index]) = *(&input[index]);
                    }
                }

                return;
            }
            """,
            options: new CompilerOptions(EmitLlvmIr: true));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);

        Assert.Contains("icmp ule ptr", llvm, StringComparison.Ordinal);
        Assert.Contains("!llvm.access.group !", llvm, StringComparison.Ordinal);
        Assert.Contains("!\"llvm.loop.parallel_accesses\"", llvm, StringComparison.Ordinal);
        Assert.Contains("!\"llvm.loop.mustprogress\"", llvm, StringComparison.Ordinal);
    }

    [Fact]
    public void BoundedRawPointerCopyLoopLowersToMemcpyWhenNoAliasIsProven()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn void Copy(
                rawptr<i32[min max]>[count] input,
                rawmutptr<i32[min max]>[count] output,
                u8[0 10] count)
                where disjoint(input[0, count], output[0, count])
                {
                    for willexit independent (stack mut u8[0 10] index = 0; index < count; index += 1)
                {
                    *(&output[index]) = *(&input[index]);
                }

                return;
            }
            """,
            options: new CompilerOptions(EmitLlvmIr: true));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);

        Assert.Contains("declare void @llvm.memcpy.p0.p0.i64(ptr captures(none) writeonly, ptr captures(none) readonly, i64, i1 immarg)", llvm, StringComparison.Ordinal);
        Assert.Matches(@"%abi_rawptr_loop_count_\d+ = zext i8 %arg_count to i64", llvm);
        Assert.Matches(@"%abi_rawptr_loop_bytes_\d+ = mul i64 %abi_rawptr_loop_count_\d+, 4", llvm);
        Assert.Matches(@"call void @llvm\.memcpy\.p0\.p0\.i64\(ptr align 4 %arg_output, ptr align 4 %arg_input, i64 %abi_rawptr_loop_bytes_\d+, i1 false\)", llvm);
        Assert.Matches(@"call void @llvm\.memcpy\.p0\.p0\.i64\(.*\), !alias\.scope !\d+", llvm);
        Assert.DoesNotContain("!\"llvm.loop.parallel_accesses\"", llvm, StringComparison.Ordinal);
    }

    [Fact]
    public void BoundedRawPointerCopyLoopInsideNontrivialFunctionLowersToMemcpy()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn void Copy(
                rawptr<i32[min max]>[count] input,
                rawmutptr<i32[min max]>[count] output,
                u8[0 10] count)
                where disjoint(input[0, count], output[0, count])
                {
                    for willexit independent (stack mut u8[0 10] index = 0; index < count; index += 1)
                {
                    *(&output[index]) = *(&input[index]);
                }

                if (count > 0)
                {
                    *(&output[0]) = 17;
                }

                return;
            }
            """,
            options: new CompilerOptions(EmitLlvmIr: true));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);

        Assert.Contains("declare void @llvm.memcpy.p0.p0.i64(ptr captures(none) writeonly, ptr captures(none) readonly, i64, i1 immarg)", llvm, StringComparison.Ordinal);
        Assert.Matches(@"call void @llvm\.memcpy\.p0\.p0\.i64\(ptr align 4 %arg_output, ptr align 4 %arg_input, i64 %abi_rawptr_loop_bytes_\d+, i1 false\)", llvm);
        Assert.DoesNotContain("!\"llvm.loop.parallel_accesses\"", llvm, StringComparison.Ordinal);
    }

    [Fact]
    public void BoundedRawPointerCopyLoopWithoutNoAliasProofKeepsScalarLowering()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn void Copy(
                rawptr<i32[min max]>[count] input,
                rawmutptr<i32[min max]>[count] output,
                u8[0 10] count)
                where overlap(input, output)
                {
                    for willexit (stack mut u8[0 10] index = 0; index < count; index += 1)
                {
                    *(&output[index]) = *(&input[index]);
                }

                return;
            }
            """,
            options: new CompilerOptions(EmitLlvmIr: true));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);

        Assert.DoesNotMatch(@"call void @llvm\.memcpy\.p0\.p0\.i64", llvm);
        Assert.Matches(@"%v\d+ = load i32, ptr %v\d+", llvm);
        Assert.Matches(@"store i32 %v\d+, ptr %v\d+", llvm);
    }

    [Fact]
    public void BoundedRawPointerCopyLoopInsideNontrivialFunctionWithoutNoAliasProofKeepsScalarLowering()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn void Copy(
                rawptr<i32[min max]>[count] input,
                rawmutptr<i32[min max]>[count] output,
                u8[0 10] count)
                where overlap(input, output)
                {
                    for willexit (stack mut u8[0 10] index = 0; index < count; index += 1)
                {
                    *(&output[index]) = *(&input[index]);
                }

                if (count > 0)
                {
                    *(&output[0]) = 17;
                }

                return;
            }
            """,
            options: new CompilerOptions(EmitLlvmIr: true));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);

        Assert.DoesNotMatch(@"call void @llvm\.memcpy\.p0\.p0\.i64", llvm);
        Assert.Matches(@"%v\d+ = load i32, ptr %v\d+", llvm);
        Assert.Matches(@"store i32 %v\d+, ptr %v\d+", llvm);
    }

    [Fact]
    public void BoundedRawPointerOverlapSafeTemporaryCopyLowersToMemmove()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn void CopyOverlapSafe(
                rawptr<i32[min max]>[4] input,
                rawmutptr<i32[min max]>[4] output)
                {
                    stack mut i32[min max][4] temporary =
                {
                    0, 0, 0, 0
                };

                for willexit (stack mut u8[0 4] index = 0; index < 4; index += 1)
                {
                    temporary[index] = *(&input[index]);
                }

                for willexit (stack mut u8[0 4] index = 0; index < 4; index += 1)
                {
                    *(&output[index]) = temporary[index];
                }

                return;
            }
            """,
            options: new CompilerOptions(EmitLlvmIr: true));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);

        Assert.Contains("declare void @llvm.memmove.p0.p0.i64(ptr captures(none) writeonly, ptr captures(none) readonly, i64, i1 immarg)", llvm, StringComparison.Ordinal);
        Assert.Matches(@"call void @llvm\.memmove\.p0\.p0\.i64\(ptr align 4 %arg_output, ptr align 4 %arg_input, i64 16, i1 false\)", llvm);
        Assert.DoesNotContain("@llvm.memcpy.p0.p0.i64", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("%slot_temporary", llvm, StringComparison.Ordinal);
    }

    [Fact]
    public void BoundedRawPointerOverlapSafeTemporaryCopyInsideNontrivialFunctionLowersToMemmove()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn void CopyOverlapSafe(
                rawptr<i32[min max]>[4] input,
                rawmutptr<i32[min max]>[4] output)
                {
                    stack mut i32[min max][4] temporary =
                {
                    0, 0, 0, 0
                };

                for willexit (stack mut u8[0 4] index = 0; index < 4; index += 1)
                {
                    temporary[index] = *(&input[index]);
                }

                for willexit (stack mut u8[0 4] index = 0; index < 4; index += 1)
                {
                    *(&output[index]) = temporary[index];
                }

                *(&output[0]) = 17;
                return;
            }
            """,
            options: new CompilerOptions(EmitLlvmIr: true));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);

        Assert.Contains("declare void @llvm.memmove.p0.p0.i64(ptr captures(none) writeonly, ptr captures(none) readonly, i64, i1 immarg)", llvm, StringComparison.Ordinal);
        Assert.Matches(@"call void @llvm\.memmove\.p0\.p0\.i64\(ptr align 4 %arg_output, ptr align 4 %arg_input, i64 16, i1 false\)", llvm);
        Assert.Contains("store i32 17", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("@llvm.memcpy.p0.p0.i64", llvm, StringComparison.Ordinal);
    }

    [Fact]
    public void BoundedRawPointerForwardBackwardMoveLoopLowersToMemmove()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn void MoveOverlapSafe(
                rawptr<i32[min max]>[count] input,
                rawmutptr<i32[min max]>[count] output,
                u8[0 10] count)
                {
                    if (count == 0)
                {
                    return;
                }

                stack rawptr<i32[min max]> sourceStart = input;
                stack rawptr<i32[min max]> destinationStart = (rawptr<i32[min max]>)output;
                if (destinationStart < sourceStart)
                {
                    for willexit (stack mut u8[0 10] index = 0; index < count; index += 1)
                    {
                        *(&output[index]) = *(&input[index]);
                    }

                    return;
                }

                stack mut u8[0 10] remaining = count;
                while willexit (remaining > 0)
                {
                    remaining -= 1;
                    *(&output[remaining]) = *(&input[remaining]);
                }

                return;
            }
            """,
            options: new CompilerOptions(EmitLlvmIr: true));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);

        Assert.Contains("declare void @llvm.memmove.p0.p0.i64(ptr captures(none) writeonly, ptr captures(none) readonly, i64, i1 immarg)", llvm, StringComparison.Ordinal);
        Assert.Matches(@"%abi_rawptr_loop_bytes_\d+ = mul i64 %abi_rawptr_loop_count_\d+, 4", llvm);
        Assert.Matches(@"call void @llvm\.memmove\.p0\.p0\.i64\(ptr align 4 %arg_output, ptr align 4 %arg_input, i64 %abi_rawptr_loop_bytes_\d+, i1 false\)", llvm);
        Assert.DoesNotContain("icmp ult ptr", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("_phi = phi", llvm, StringComparison.Ordinal);
    }

    [Fact]
    public void SliceForwardBackwardMoveLoopLowersToMemmoveWhenByteLengthIsRepresentable()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn void MoveOverlapSafe(
                borrow i32[min max][] input,
                borrow mut i32[min max][] output,
                u64[0 2 ** 61 - 1] count)
                {
                    if (count == 0)
                {
                    return;
                }

                stack rawptr<i32[min max]> sourceStart = &input[0];
                stack rawptr<i32[min max]> destinationStart = (rawptr<i32[min max]>)(&output[0]);
                if (destinationStart < sourceStart)
                {
                    for willexit (stack mut u64[0 2 ** 61 - 1] index = 0; index < count; index += 1)
                    {
                        output[index] = input[index];
                    }

                    return;
                }

                stack mut u64[0 2 ** 61 - 1] remaining = count;
                while willexit (remaining > 0)
                {
                    remaining -= 1;
                    output[remaining] = input[remaining];
                }

                return;
            }
            """,
            options: new CompilerOptions(EmitLlvmIr: true));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);

        Assert.Contains("declare void @llvm.memmove.p0.p0.i64(ptr captures(none) writeonly, ptr captures(none) readonly, i64, i1 immarg)", llvm, StringComparison.Ordinal);
        Assert.Matches(@"%abi_rawptr_loop_bytes_\d+ = mul i64 %arg_count, 4", llvm);
        Assert.Matches(@"call void @llvm\.memmove\.p0\.p0\.i64\(ptr align 4 %abi_rawptr_loop_destination_data_\d+, ptr align 4 %abi_rawptr_loop_source_data_\d+, i64 %abi_rawptr_loop_bytes_\d+, i1 false\)", llvm);
        Assert.DoesNotContain("icmp ult ptr", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("_phi = phi", llvm, StringComparison.Ordinal);
    }

    [Fact]
    public void BoundedRawPointerOverlapSafeTemporaryCopyWithTemporaryEpilogueUseKeepsScalarLowering()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn void CopyOverlapSafe(
                rawptr<i32[min max]>[4] input,
                rawmutptr<i32[min max]>[4] output)
                {
                    stack mut i32[min max][4] temporary =
                {
                    0, 0, 0, 0
                };

                for willexit (stack mut u8[0 4] index = 0; index < 4; index += 1)
                {
                    temporary[index] = *(&input[index]);
                }

                for willexit (stack mut u8[0 4] index = 0; index < 4; index += 1)
                {
                    *(&output[index]) = temporary[index];
                }

                *(&output[0]) = temporary[0];
                return;
            }
            """,
            options: new CompilerOptions(EmitLlvmIr: true));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);

        Assert.DoesNotContain("@llvm.memmove.p0.p0.i64", llvm, StringComparison.Ordinal);
        Assert.Matches(@"%v\d+ = load i32, ptr %v\d+", llvm);
        Assert.Matches(@"store i32 %v\d+, ptr %v\d+", llvm);
    }

    [Fact]
    public void BoundedRawPointerByteFillLoopLowersToMemset()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn void Fill(
                rawmutptr<i8[min max]>[count] output,
                i8[min max] value,
                u8[0 10] count)
                {
                    for willexit independent (stack mut u8[0 10] index = 0; index < count; index += 1)
                {
                    *(&output[index]) = value;
                }

                return;
            }
            """,
            options: new CompilerOptions(EmitLlvmIr: true));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);

        Assert.Contains("declare void @llvm.memset.p0.i64(ptr captures(none) writeonly, i8, i64, i1 immarg)", llvm, StringComparison.Ordinal);
        Assert.Matches(@"%abi_rawptr_loop_count_\d+ = zext i8 %arg_count to i64", llvm);
        Assert.Matches(@"call void @llvm\.memset\.p0\.i64\(ptr %arg_output, i8 %arg_value, i64 %abi_rawptr_loop_count_\d+, i1 false\)", llvm);
        Assert.DoesNotContain("!\"llvm.loop.parallel_accesses\"", llvm, StringComparison.Ordinal);
    }

    [Fact]
    public void InitSliceByteFillLoopLowersToMemset()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn void Fill(init i8[min max][] output, i8[min max] value, u8[0 10] count)
            {
                for willexit independent (stack mut u8[0 10] index = 0; index < count; index += 1)
                {
                    init output[index] = value;
                }

                return;
            }
            """,
            options: new CompilerOptions(EmitLlvmIr: true));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);

        Assert.Contains("declare void @llvm.memset.p0.i64(ptr captures(none) writeonly, i8, i64, i1 immarg)", llvm, StringComparison.Ordinal);
        Assert.Matches(@"%abi_rawptr_loop_count_\d+ = zext i8 %arg_count to i64", llvm);
        Assert.Matches(@"call void @llvm\.memset\.p0\.i64\(ptr %abi_rawptr_loop_destination_data_\d+, i8 %arg_value, i64 %abi_rawptr_loop_count_\d+, i1 false\)", llvm);
        Assert.DoesNotContain("%v0_phi = phi", llvm, StringComparison.Ordinal);
    }

    [Fact]
    public void DynamicTailInitByteFillLoopLowersToMemsetAndCommitsLength()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn u64[0 2 ** 63 - 1] Fill(i8[min max] value, u8[0 10] count)
            {
                stack mut dynamic i8[min max] values = new(count);
                stack init i8[min max][] spare = init values[values.Length, count];
                for willexit independent (stack mut u8[0 10] index = 0; index < count; index += 1)
                {
                    init spare[index] = value;
                }

                return values.Length;
            }
            """,
            options: new CompilerOptions(EmitLlvmIr: true));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);
        var fillBody = ExtractDefinitionBody(llvm, "Fill");

        Assert.Contains("declare void @llvm.memset.p0.i64(ptr captures(none) writeonly, i8, i64, i1 immarg)", llvm, StringComparison.Ordinal);
        Assert.Matches(@"call void @llvm\.memset\.p0\.i64\(ptr %abi_rawptr_loop_destination_data_\d+, i8 %arg_value, i64 %abi_rawptr_loop_count_\d+, i1 false\)", fillBody);
        Assert.Contains("dynamic_length_final", fillBody, StringComparison.Ordinal);
        Assert.DoesNotContain("%v0_phi = phi", fillBody, StringComparison.Ordinal);
    }

    [Fact]
    public void DynamicAppendThroughLengthByteFillLoopLowersToMemsetAndCommitsLengthOnce()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn u64[0 2 ** 63 - 1] Fill(i8[min max] value, u8[0 10] count)
            {
                stack mut dynamic i8[min max] values = new(count);
                if (!values.TryReserve(count))
                {
                    return 0;
                }

                for willexit (stack mut u8[0 10] index = 0; index < count; index += 1)
                {
                    init values[values.Length] = value;
                }

                return values.Length;
            }
            """,
            options: new CompilerOptions(EmitLlvmIr: true));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);
        var fillBody = ExtractDefinitionBody(llvm, "Fill");

        Assert.Contains("declare void @llvm.memset.p0.i64(ptr captures(none) writeonly, i8, i64, i1 immarg)", llvm, StringComparison.Ordinal);
        Assert.Matches(@"call void @llvm\.memset\.p0\.i64\(ptr %dynamic_append_tail, i8 %arg_value, i64 %abi_rawptr_loop_count_\d+, i1 false\)", fillBody);
        Assert.Contains("%dynamic_append_final_length = add", fillBody, StringComparison.Ordinal);
        Assert.DoesNotContain("_for_body", fillBody, StringComparison.Ordinal);
        Assert.DoesNotContain("v7_phi", fillBody, StringComparison.Ordinal);
    }

    [Fact]
    public void DisjointInitSliceCopyLoopLowersToMemcpy()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn void Copy(
                borrow i32[min max][] input,
                init i32[min max][] output,
                u8[0 10] count)
                {
                    for willexit independent (stack mut u8[0 10] index = 0; index < count; index += 1)
                {
                    init output[index] = input[index];
                }

                return;
            }
            """,
            options: new CompilerOptions(EmitLlvmIr: true));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);

        Assert.Contains("declare void @llvm.memcpy.p0.p0.i64(ptr captures(none) writeonly, ptr captures(none) readonly, i64, i1 immarg)", llvm, StringComparison.Ordinal);
        Assert.Matches(@"%abi_rawptr_loop_bytes_\d+ = mul i64 %abi_rawptr_loop_count_\d+, 4", llvm);
        Assert.Matches(@"call void @llvm\.memcpy\.p0\.p0\.i64\(ptr align 4 %abi_rawptr_loop_destination_data_\d+, ptr align 4 %abi_rawptr_loop_source_data_\d+, i64 %abi_rawptr_loop_bytes_\d+, i1 false\)", llvm);
        Assert.DoesNotContain("%v0_phi = phi", llvm, StringComparison.Ordinal);
    }

    [Fact]
    public void InitSliceOverlapSafeTemporaryCopyLowersToMemmove()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn void CopyOverlapSafe(
                borrow i32[min max][] input,
                init i32[min max][] output)
                {
                    stack mut i32[min max][4] temporary =
                {
                    0, 0, 0, 0
                };

                for willexit (stack mut u8[0 4] index = 0; index < 4; index += 1)
                {
                    temporary[index] = input[index];
                }

                for willexit (stack mut u8[0 4] index = 0; index < 4; index += 1)
                {
                    init output[index] = temporary[index];
                }

                return;
            }
            """,
            options: new CompilerOptions(EmitLlvmIr: true));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);

        Assert.Contains("declare void @llvm.memmove.p0.p0.i64(ptr captures(none) writeonly, ptr captures(none) readonly, i64, i1 immarg)", llvm, StringComparison.Ordinal);
        Assert.Matches(@"call void @llvm\.memmove\.p0\.p0\.i64\(ptr align 4 %abi_rawptr_loop_destination_data_\d+, ptr align 4 %abi_rawptr_loop_source_data_\d+, i64 16, i1 false\)", llvm);
        Assert.DoesNotContain("%slot_temporary", llvm, StringComparison.Ordinal);
    }

    [Fact]
    public void BoundedRawPointerByteFillLoopInsideNontrivialFunctionLowersToMemset()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn void Fill(
                rawmutptr<i8[min max]>[count] output,
                i8[min max] value,
                u8[0 10] count)
                {
                    for willexit independent (stack mut u8[0 10] index = 0; index < count; index += 1)
                {
                    *(&output[index]) = value;
                }

                if (count > 0)
                {
                    *(&output[0]) = 0;
                }

                return;
            }
            """,
            options: new CompilerOptions(EmitLlvmIr: true));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);

        Assert.Contains("declare void @llvm.memset.p0.i64(ptr captures(none) writeonly, i8, i64, i1 immarg)", llvm, StringComparison.Ordinal);
        Assert.Matches(@"call void @llvm\.memset\.p0\.i64\(ptr %arg_output, i8 %arg_value, i64 %abi_rawptr_loop_count_\d+, i1 false\)", llvm);
        Assert.DoesNotContain("!\"llvm.loop.parallel_accesses\"", llvm, StringComparison.Ordinal);
    }

    [Fact]
    public void IndependentBoundedRawPointerFillAndTransformLoopsEmitParallelMetadata()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn void Fill(rawmutptr<i64[min max]>[32] output, i64[min max] value)
            {
                for willexit independent (stack mut u8[0 32] index = 0; index < 32; index += 1)
                {
                    *(&output[index]) = value;
                }

                return;
            }

            unsafe fn void Transform(
                rawptr<i64[min max]>[32] input,
                rawmutptr<i64[min max]>[32] output)
                where disjoint(input[0, 32], output[0, 32])
                {
                    for willexit independent (stack mut u8[0 32] index = 0; index < 32; index += 1)
                {
                    *(&output[index]) = *(&input[index]) + 1;
                }

                return;
            }
            """,
            options: new CompilerOptions(EmitLlvmIr: true));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);

        Assert.True(Regex.Matches(llvm, "!\\\"llvm.loop.parallel_accesses\\\"").Count >= 2, llvm);
        Assert.True(Regex.Matches(llvm, "!llvm.access.group !").Count >= 2, llvm);
        Assert.Contains("!\"llvm.loop.mustprogress\"", llvm, StringComparison.Ordinal);
    }

    [Fact]
    public void IntegerArithmeticFoldEmitsSingleMultiplyForRepeatedUnknownAdds()
    {
        var result = Compile(
            """
            module Demo

            fn i32[min max] Run(i32[min max] value)
            {
                return value + value + value + value + value + value;
            }
            """,
            new CompilerOptions(StopAfterPassId: "emit-llvm"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var runBody = ExtractDefinitionBody(GetLlvmRaw(result), "Run");

        Assert.Matches(@"mul(?:\s+\w+)*\s+i32\s+%arg_value,\s+6", runBody);
        Assert.DoesNotContain(" add ", runBody, StringComparison.Ordinal);
    }

    [Fact]
    public void IntegerArithmeticFoldEmitsMultiplySubtractForRepeatedSubtractionChains()
    {
        var result = Compile(
            """
            module Demo

            fn i16[min max] Run(i16[-8000 8000] x, i16[-3000 3000] y)
            {
                return x + x + x - y - y + 4;
            }
            """,
            new CompilerOptions(StopAfterPassId: "emit-llvm"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var runBody = ExtractDefinitionBody(GetLlvmRaw(result), "Run");

        Assert.Matches(@"mul(?:\s+\w+)*\s+i16\s+%arg_x,\s+3", runBody);
        Assert.Matches(@"mul(?:\s+\w+)*\s+i16\s+%arg_y,\s+2", runBody);
        Assert.Contains(" sub ", runBody, StringComparison.Ordinal);
    }

    [Fact]
    public void IntegerArithmeticFoldLowersRepeatedProductRunsWithoutPowHelper()
    {
        var result = Compile(
            """
            module Demo

            fn i32[min max] Run(i32[min max] value)
            {
                return value * value * value * value;
            }
            """,
            new CompilerOptions(StopAfterPassId: "emit-llvm"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);
        var runBody = ExtractDefinitionBody(llvm, "Run");

        Assert.Equal(3, Regex.Matches(runBody, @"\bmul\b").Count);
        Assert.DoesNotContain("@__stark_int_pow_i32", llvm, StringComparison.Ordinal);
    }

    private static CompilationResult Compile(string source, CompilerOptions? options = null)
    {
        return DefaultCompilerPipeline.Create().Run(new CompilationInput(source), options);
    }

    private static CompilationResult Compile(string source, string filePath, CompilerOptions? options = null)
    {
        return DefaultCompilerPipeline.Create().Run(new CompilationInput(source, filePath), options);
    }

    private static string GetLlvm(CompilationResult result)
    {
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.LlvmIrModule, out LlvmIrModule? llvmModule));
        Assert.NotNull(llvmModule);
        return NormalizeLlvm(llvmModule.Text);
    }

    private static string GetLlvmRaw(CompilationResult result)
    {
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.LlvmIrModule, out LlvmIrModule? llvmModule));
        Assert.NotNull(llvmModule);
        return llvmModule.Text;
    }

    private static int CountOccurrences(string text, string needle)
    {
        var count = 0;
        var index = 0;

        while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    private static string ExtractDefinitionHeader(string llvm, string symbolName)
    {
        var prefix = $"@{symbolName}(";

        foreach (var line in llvm.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith("define ", StringComparison.Ordinal)
                && line.Contains(prefix, StringComparison.Ordinal))
            {
                return line;
            }
        }

        throw new Xunit.Sdk.XunitException($"Expected a definition header for symbol '{symbolName}'.");
    }

    private static string ExtractDefinitionBody(string llvm, string symbolName)
    {
        var header = ExtractDefinitionHeader(llvm, symbolName);
        var start = llvm.IndexOf(header, StringComparison.Ordinal);
        if (start < 0)
        {
            throw new Xunit.Sdk.XunitException($"Expected a definition body for symbol '{symbolName}'.");
        }

        var nextDefinition = llvm.IndexOf("\ndefine ", start + header.Length, StringComparison.Ordinal);
        return nextDefinition < 0
            ? llvm[start..]
            : llvm[start..nextDefinition];
    }

    private static string ExtractFirstIndirectCall(string llvm, string symbolName, string parameterName)
    {
        var body = ExtractDefinitionBody(llvm, symbolName);
        var match = Regex.Match(
            body,
            $@"call fastcc [^\n]+ %arg_{Regex.Escape(parameterName)}\([^\n]*\)[^\n]*",
            RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            throw new Xunit.Sdk.XunitException($"Expected an indirect call through parameter '{parameterName}' in '{symbolName}'. Body:\n{body}");
        }

        return match.Value;
    }

    private static string NormalizeLlvm(string llvm)
    {
        var normalized = llvm.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        normalized = Regex.Replace(normalized, @"\bnoundef\b", string.Empty, RegexOptions.CultureInvariant);
        return Regex.Replace(
            normalized,
            @"^(?:define|declare)\b[^\n]*$",
            static match =>
            {
                var line = Regex.Replace(match.Value, @" {2,}", " ", RegexOptions.CultureInvariant);
                line = Regex.Replace(line, @"\s+,", ",", RegexOptions.CultureInvariant);
                line = Regex.Replace(line, @"\(\s+", "(", RegexOptions.CultureInvariant);
                line = Regex.Replace(line, @"\s+\)", ")", RegexOptions.CultureInvariant);
                return line;
            },
            RegexOptions.Multiline | RegexOptions.CultureInvariant);
    }
}
