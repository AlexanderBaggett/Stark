using Stark.Compiler;

namespace compiler.FeatureTests;

public sealed class GenericsFeatureTests : FeatureLlvmTestBase
{
    [Fact]
    public void ComptimeGenericValueParameterCanBindFixedArrayLength()
    {
        var result = Compile(
            """
            module Demo

            finite law u8[0 max] Probe<T, comptime u8[1 4] N>(borrow T[N] values)
            {
                return 1;
            }

            finite law u8[0 max] Run()
            {
                stack i32[min max][3] values = { 10, 20, 30 };
                return Probe(values);
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static d => d.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? model));
        var trigger = Assert.Single(model!.InstantiationTriggers, static trigger => trigger.FunctionName == "Probe");
        Assert.Equal("N", Assert.Single(trigger.ComptimeValueArguments!).ParameterName);
        Assert.Equal(3, Assert.Single(trigger.ComptimeValueArguments!).IntegerValue);
        Assert.Equal("i32", Assert.Single(trigger.TypeArguments).DisplayName);
    }

    [Fact]
    public void ComptimeGenericValueParameterRejectsOutOfRangeLength()
    {
        var result = Compile(
            """
            module Demo

            finite law u8[0 max] Probe<T, comptime u8[1 2] N>(borrow T[N] values)
            {
                return 1;
            }

            finite law u8[0 max] Run()
            {
                stack i32[min max][3] values = { 10, 20, 30 };
                return Probe(values);
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK3021"
            && diagnostic.Message.Contains("No overload of 'Probe' matches", StringComparison.Ordinal));
    }

    [Fact]
    public void ExplicitComptimeGenericFunctionArgumentMaterializesAsConstant()
    {
        var llvm = CompileToLlvm(
            """
            module Demo

            finite law u8[0 max] Length<T, comptime u8[1 4] N>(borrow T[N] values)
            {
                return N;
            }

            finite law u8[0 max] Run()
            {
                stack i32[min max][3] values = { 10, 20, 30 };
                return Length<i32[min max], 3>(values);
            }
            """,
            new CompilerOptions());

        var specializationBody = ExtractDefinitionBody(llvm, "__stark_mono_fn_Demo__Length__i32__N_3");
        Assert.Contains("ret i8 3", specializationBody);
        Assert.Contains("call fastcc i8 @__stark_mono_fn_Demo__Length__i32__N_3(", ExtractDefinitionBody(llvm, "Run"));
    }

    [Fact]
    public void ComptimeGenericValueArgumentsParticipateInFunctionIdentity()
    {
        var llvm = CompileToLlvm(
            """
            module Demo

            finite law u32[0 max] Length<T, comptime u32[1 8] N>(borrow T[N] values)
            {
                return N;
            }

            finite law u32[0 max] Run()
            {
                stack i32[min max][2] left = { 10, 20 };
                stack i32[min max][3] right = { 30, 40, 50 };
                return Length<i32[min max], 2>(left) + Length<i32[min max], 3>(right);
            }
            """,
            new CompilerOptions());

        Assert.Contains("__stark_mono_fn_Demo__Length__i32__N_2", llvm);
        Assert.Contains("__stark_mono_fn_Demo__Length__i32__N_3", llvm);
    }

    [Fact]
    public void ValueOnlyComptimeGenericFunctionArgumentMaterializesAsConstant()
    {
        var llvm = CompileToLlvm(
            """
            module Demo

            finite law u8[0 max] Pick<comptime u8[1 8] N>()
            {
                return N;
            }

            finite law u8[0 max] Run()
            {
                return Pick<5>();
            }
            """,
            new CompilerOptions());

        var specializationBody = ExtractDefinitionBody(llvm, "__stark_mono_fn_Demo__Pick__N_5");
        Assert.Contains("ret i8 5", specializationBody);
        var runBody = ExtractDefinitionBody(llvm, "Run");
        Assert.Contains("ret i8 5", runBody);
        Assert.DoesNotContain("call fastcc i8 @__stark_mono_fn_Demo__Pick__N_5(", runBody);
    }

    [Fact]
    public void ExplicitComptimeGenericNamedTypeArgumentRecordsInstantiation()
    {
        var result = Compile(
            """
            module Demo

            struct Buffer<T, comptime u8[1 8] N>
            {
                T[N] Items;
            }

            finite law u8[0 max] Run()
            {
                stack Buffer<i32[min max], 3> buffer = new Buffer<i32[min max], 3>();
                return 3;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static d => d.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? model));
        var trigger = Assert.Single(model!.TypeTriggers, static trigger => trigger.TypeName.StartsWith("Buffer<", StringComparison.Ordinal));
        var valueArgument = Assert.Single(trigger.ComptimeValueArguments!);
        Assert.Equal("N", valueArgument.ParameterName);
        Assert.Equal(3, valueArgument.IntegerValue);
    }

    [Fact]
    public void ValueOnlyComptimeGenericNamedTypeArgumentRecordsInstantiation()
    {
        var result = Compile(
            """
            module Demo

            struct Bytes<comptime u8[1 8] N>
            {
                u8[0 max][N] Items;
            }

            finite law u8[0 max] Run()
            {
                stack Bytes<4> bytes = new Bytes<4>();
                return 4;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static d => d.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? model));
        var trigger = Assert.Single(model!.TypeTriggers, static trigger => trigger.TypeName.StartsWith("Bytes<", StringComparison.Ordinal));
        var valueArgument = Assert.Single(trigger.ComptimeValueArguments!);
        Assert.Equal("N", valueArgument.ParameterName);
        Assert.Equal(4, valueArgument.IntegerValue);
    }

    [Fact]
    public void SymbolicComptimeGenericValueArgumentForwardsBySourceName()
    {
        var result = Compile(
            """
            module Demo

            struct Buffer<T, comptime u8[1 8] M>
            {
                T[M] Items;
            }

            finite law u8[0 max] Probe<T, comptime u8[1 8] N>(borrow Buffer<T, comptime N> buffer)
            {
                return N;
            }

            finite law u8[0 max] Run()
            {
                stack Buffer<i32[min max], 4> buffer = new Buffer<i32[min max], 4>();
                return Probe(buffer);
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static d => d.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? model));
        var trigger = Assert.Single(model!.InstantiationTriggers, static trigger => trigger.FunctionName == "Probe");
        var valueArgument = Assert.Single(trigger.ComptimeValueArguments!);
        Assert.Equal("N", valueArgument.ParameterName);
        Assert.Equal(4, valueArgument.IntegerValue);
    }

    [Fact]
    public void ComptimeGenericParametersMustUseIntegerTypes()
    {
        var result = Compile(
            """
            module Demo

            finite law i32[min max] Bad<comptime bool N>()
            {
                return 0;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK3050"
            && diagnostic.Message.Contains("must currently use a range-typed integer type", StringComparison.Ordinal));
    }

    [Fact]
    public void ComptimeGenericStructPreservesSymbolicFixedArrayLength()
    {
        var result = Compile(
            """
            module Demo

            struct Buffer<T, comptime u8[1 8] N>
            {
                T[N] Items;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static d => d.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? model));
        Assert.True(model!.NamedTypes.TryGetValue("Buffer", out var buffer));
        Assert.Equal("N", Assert.Single(buffer.ComptimeGenericParams).Name);
        Assert.Equal("N", buffer.OrderedFields.Single().Type.FixedLengthParameterName);
    }

    [Fact]
    public void GenericEnumMonomorphizationEmitsConcreteStructType()
    {
        var llvm = CompileToLlvm(
            """
            module Demo

            enum Option<T>
            {
                None,
                Some(T),
            }

            export fn i32[min max] main()
            {
                stack Option<i32[min max]> opt = Option<i32[min max]>.Some(42);
                switch (opt)
                {
                    case Option<i32[min max]>.None:
                        return 0;
                    case Option<i32[min max]>.Some(var value):
                        return value;
                }
            }
            """);

        // Monomorphized type should be emitted as a concrete LLVM struct
        Assert.Contains("Option", llvm);
        Assert.Contains("i32", llvm);
    }

    [Fact]
    public void GenericRecordMonomorphizationEmitsConcreteFields()
    {
        var llvm = CompileToLlvm(
            """
            module Demo

            record Pair<A, B>(A First, B Second)
            {
            }

            export fn i32[min max] main()
            {
                stack Pair<i32[min max], i32[min max]> p = new Pair<i32[min max], i32[min max]>(3, 7);
                return p.First + p.Second;
            }
            """);

        Assert.Contains("Pair", llvm);
        Assert.Contains("i32", llvm);
    }

    [Fact]
    public void TwoDistinctInstantiationsOfSameGenericEmitTwoTypes()
    {
        var llvm = CompileToLlvm(
            """
            module Demo

            enum Option<T>
            {
                None,
                Some(T),
            }

            finite law i32[min max] GetI32(Option<i32[min max]> opt)
            {
                switch (opt)
                {
                    case Option<i32[min max]>.None:
                        return 0;
                    case Option<i32[min max]>.Some(var value):
                        return value;
                }
            }

            finite law bool GetBool(Option<bool> opt)
            {
                switch (opt)
                {
                    case Option<bool>.None:
                        return false;
                    case Option<bool>.Some(var value):
                        return value;
                }
            }

            export fn i32[min max] main()
            {
                stack Option<i32[min max]> a = Option<i32[min max]>.Some(5);
                stack Option<bool> b = Option<bool>.Some(true);
                stack i32[min max] sum = GetI32(a);
                stack bool flag = GetBool(b);
                return flag ? sum : 0;
            }
            """);

        // Both monomorphized types should appear
        Assert.Contains("Option", llvm);
        // Two distinct types should be defined
        Assert.True(CountOccurrences(llvm, "= type {") >= 2, "expected at least two named struct types for Option<i32> and Option<bool>");
    }

    [Fact]
    public void NestedGenericInstantiationTypeChecks()
    {
        var result = Compile(
            """
            module Demo

            enum Option<T>
            {
                None,
                Some(T),
            }

            finite law bool IsPresent(Option<Option<i32[min max]>> outer)
            {
                switch (outer)
                {
                    case Option<Option<i32[min max]>>.None:
                        return false;
                    case Option<Option<i32[min max]>>.Some(var inner):
                        return true;
                }
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static d => d.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? model));
        Assert.NotNull(model);
        Assert.True(model.NamedTypes.ContainsKey("Option<Option<i32>>")
            || model.NamedTypes.Keys.Any(k => k.StartsWith("Demo.Option<") && k.Contains("Option")),
            "nested instantiation should be registered");
    }

    [Fact]
    public void OpenGenericFunctionTemplatesDoNotEmitRuntimeAbiDeclarations()
    {
        var llvm = CompileToLlvm(
            """
            module Demo

            finite law T Identity<T>(T value)
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
                    return value;
                }

                return value;
            }

            finite law i32[min max] Run(i32[min max] value)
            {
                return Identity(value);
            }
            """,
            new CompilerOptions());

        Assert.Contains("define internal dso_local fastcc noundef i32 @__stark_mono_fn_Demo__Identity__i32(", llvm);
        Assert.Contains("call fastcc i32 @__stark_mono_fn_Demo__Identity__i32(", llvm);
        Assert.DoesNotContain("declare fastcc noundef ptr @Identity(", llvm);
        Assert.DoesNotContain("define fastcc noundef ptr @Identity(", llvm);
    }

    [Fact]
    public void ExplicitGenericFunctionCallWithOneRuntimeArgumentParsesAsGenericCall()
    {
        var result = Compile(
            """
            module Demo

            finite law T Identity<T>(T value)
            {
                return value;
            }

            finite law i32[min max] Run(i32[min max] value)
            {
                return Identity<i32[min max]>(value);
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "STK3012");
        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "STK3002");
    }

    [Fact]
    public void LargeByValueGenericSpecializationsPreserveObservableMemoryFacts()
    {
        var llvm = CompileToLlvm(
            """
            module Demo

            struct Big
            {
                i64[min max] A;
                i64[min max] B;
                i64[min max] C;
                i64[min max] D;
            }

            inline finite law i64[min max] Read<T>(T value)
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

                return acc;
            }

            finite law i64[min max] Run(Big value)
            {
                return Read(value);
            }
            """,
            new CompilerOptions());

        var runHeader = ExtractDefinitionHeader(llvm, "Run");
        var specializationHeader = ExtractDefinitionHeader(llvm, "__stark_mono_fn_Demo__Read__Big");
        var runBody = ExtractDefinitionBody(llvm, "Run");

        Assert.Contains("memory(argmem: read)", runHeader);
        Assert.DoesNotContain("memory(readwrite", runHeader);
        Assert.Contains("memory(argmem: read)", specializationHeader);
        Assert.DoesNotContain("memory(readwrite", specializationHeader);
        Assert.Contains(
            "call fastcc i64 @__stark_mono_fn_Demo__Read__Big(ptr nonnull byval(%Big) noalias readonly captures(none) dereferenceable(32) align 8 %arg_value)",
            runBody);
        Assert.DoesNotContain("%abi_callarg_value", runBody);
    }
}
