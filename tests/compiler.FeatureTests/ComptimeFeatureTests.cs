using Stark.Compiler;

namespace compiler.FeatureTests;

public sealed class ComptimeFeatureTests : FeatureLlvmTestBase
{
    [Fact]
    public void ComptimeExpressionEvaluatesFiniteLawCallAndLowersToImmediate()
    {
        var llvm = CompileToLlvm(
            """
            module Demo

            finite law i32[min max] Build(i32[min max] seed)
            {
                stack mut i32[min max] value = seed;
                value = value + 1;
                if (value == 21)
                {
                    return value * 2;
                }

                return 0;
            }

            finite law i32[min max] Run()
            {
                return comptime Build(20);
            }
            """,
            new CompilerOptions(OptimizationLevel: CompilerOptimizationLevel.O0));

        var runBody = ExtractDefinitionBody(llvm, "Run");
        Assert.Contains("ret i32 42", runBody);
        Assert.DoesNotContain("call fastcc i32 @Build", runBody);
    }

    [Fact]
    public void ComptimeExpressionEvaluatesFiniteFunctionCallAndLowersToImmediate()
    {
        var llvm = CompileToLlvm(
            """
            module Demo

            finite i32[min max] Build(i32[min max] seed)
            {
                stack mut i32[min max] value = seed;
                value = value + 7;
                return value * 2;
            }

            finite law i32[min max] Run()
            {
                return comptime Build(14);
            }
            """,
            new CompilerOptions(OptimizationLevel: CompilerOptimizationLevel.O0));

        var runBody = ExtractDefinitionBody(llvm, "Run");
        Assert.Contains("ret i32 42", runBody);
        Assert.DoesNotContain("call fastcc i32 @Build", runBody);
    }

    [Fact]
    public void ComptimeExpressionEvaluatesGenericFiniteFunctionCall()
    {
        var llvm = CompileToLlvm(
            """
            module Demo

            finite u8[0 max] Pick<comptime u8[1 8] N>()
            {
                return N;
            }

            finite law u8[0 max] Run()
            {
                return comptime Pick<6>();
            }
            """,
            new CompilerOptions(OptimizationLevel: CompilerOptimizationLevel.O0));

        var runBody = ExtractDefinitionBody(llvm, "Run");
        Assert.Contains("ret i8 6", runBody);
        Assert.DoesNotContain("call", runBody);
    }

    [Fact]
    public void ComptimeExpressionEvaluatesComptimeGenericArithmeticAfterSpecialization()
    {
        var llvm = CompileToLlvm(
            """
            module Demo

            finite u8[0 max] Pick<comptime u8[1 8] N>()
            {
                return comptime (N + 2);
            }

            finite law u8[0 max] Run()
            {
                return Pick<5>();
            }
            """,
            new CompilerOptions(OptimizationLevel: CompilerOptimizationLevel.O0));

        var specializationBody = ExtractDefinitionBody(llvm, "__stark_mono_fn_Demo__Pick__N_5");
        Assert.Contains("ret i8 7", specializationBody);
        var runBody = ExtractDefinitionBody(llvm, "Run");
        Assert.Contains("ret i8 7", runBody);
    }

    [Fact]
    public void ComptimeGenericArithmeticStillRejectsRuntimeValues()
    {
        var result = Compile(
            """
            module Demo

            finite u8[0 max] Pick<comptime u8[1 8] N>(u8[0 max] runtimeValue)
            {
                return comptime (N + runtimeValue);
            }

            finite law u8[0 max] Run()
            {
                return Pick<5>(2);
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK3053"
            && diagnostic.Message.Contains("`comptime` expression must evaluate during compilation", StringComparison.Ordinal));
    }

    [Fact]
    public void ComptimeGenericFiniteCallPreservesSymbolicValueUntilSpecialization()
    {
        var llvm = CompileToLlvm(
            """
            module Demo

            finite law u8[3 10] Add2<comptime u8[1 8] N>()
            {
                return comptime (N + 2);
            }

            finite law u8[3 10] Forward<comptime u8[1 8] M>()
            {
                return comptime Add2<comptime M>();
            }

            finite law u8[0 max] Run()
            {
                return Forward<3>();
            }
            """,
            new CompilerOptions(OptimizationLevel: CompilerOptimizationLevel.O0));

        var addBody = ExtractDefinitionBody(llvm, "__stark_mono_fn_Demo__Add2__N_3");
        Assert.Contains("ret i8 5", addBody);
        var forwardBody = ExtractDefinitionBody(llvm, "__stark_mono_fn_Demo__Forward__M_3");
        Assert.Contains("ret i8 5", forwardBody);
        var runBody = ExtractDefinitionBody(llvm, "Run");
        Assert.Contains("ret i8 5", runBody);
    }

    [Fact]
    public void ComptimeExpressionEvaluatesExplicitNumericConversions()
    {
        var llvm = CompileToLlvm(
            """
            module Demo

            finite law i32[min max] Build()
            {
                stack i32[min max] seed = 40;
                stack f64 lifted = (f64)seed;
                return (i32[min max])lifted + (i32[min max])2.75;
            }

            finite law i32[min max] Run()
            {
                return comptime Build();
            }
            """,
            new CompilerOptions(OptimizationLevel: CompilerOptimizationLevel.O0));

        var runBody = ExtractDefinitionBody(llvm, "Run");
        Assert.Contains("ret i32 42", runBody);
        Assert.DoesNotContain("call", runBody);
        Assert.DoesNotContain("fptosi", runBody);
        Assert.DoesNotContain("sitofp", runBody);
    }

    [Fact]
    public void ComptimeBlockEvaluatesExplicitNumericConversions()
    {
        var llvm = CompileToLlvm(
            """
            module Demo

            finite law i32[min max] Run()
            {
                return comptime
                {
                    stack i32[min max] seed = 40;
                    stack f64 lifted = (f64)seed;
                    return (i32[min max])lifted + (i32[min max])2.75;
                };
            }
            """,
            new CompilerOptions(OptimizationLevel: CompilerOptimizationLevel.O0));

        var runBody = ExtractDefinitionBody(llvm, "Run");
        Assert.Contains("ret i32 42", runBody);
        Assert.DoesNotContain("call", runBody);
        Assert.DoesNotContain("fptosi", runBody);
        Assert.DoesNotContain("sitofp", runBody);
    }

    [Fact]
    public void ComptimeExpressionEvaluatesTrySuccessPath()
    {
        var llvm = CompileToLlvm(
            """
            module Demo

            enum ParseError
            {
                Bad
            }

            enum ParseOutcome<T>
            {
                [Ok] Found(T),
                [Err] Failed(ParseError),
            }

            finite law ParseOutcome<i32[min max]> Parse(bool ok)
            {
                if (ok)
                {
                    return ParseOutcome<i32[min max]>.Found(40);
                }

                return ParseOutcome<i32[min max]>.Failed(ParseError.Bad);
            }

            finite law ParseOutcome<i32[min max]> Build(bool ok)
            {
                stack i32[min max] value = try Parse(ok);
                return ParseOutcome<i32[min max]>.Found(value + 2);
            }

            finite law i32[min max] Score(ParseOutcome<i32[min max]> outcome)
            {
                switch (outcome)
                {
                    case ParseOutcome<i32[min max]>.Found(var value): return value;
                    case ParseOutcome<i32[min max]>.Failed(var error): return 0;
                }
            }

            finite law i32[min max] Run()
            {
                return comptime Score(Build(true));
            }
            """,
            new CompilerOptions(OptimizationLevel: CompilerOptimizationLevel.O0));

        var runBody = ExtractDefinitionBody(llvm, "Run");
        Assert.Contains("ret i32 42", runBody);
        Assert.DoesNotContain("call", runBody);
    }

    [Fact]
    public void ComptimeExpressionEvaluatesTrySameErrorEarlyReturn()
    {
        var llvm = CompileToLlvm(
            """
            module Demo

            enum ParseError
            {
                Bad
            }

            enum ParseOutcome<T>
            {
                [Ok] Found(T),
                [Err] Failed(ParseError),
            }

            finite law ParseOutcome<i32[min max]> Parse(bool ok)
            {
                if (ok)
                {
                    return ParseOutcome<i32[min max]>.Found(40);
                }

                return ParseOutcome<i32[min max]>.Failed(ParseError.Bad);
            }

            finite law ParseOutcome<i32[min max]> Build(bool ok)
            {
                stack i32[min max] value = try Parse(ok);
                return ParseOutcome<i32[min max]>.Found(value + 2);
            }

            finite law i32[min max] Score(ParseOutcome<i32[min max]> outcome)
            {
                switch (outcome)
                {
                    case ParseOutcome<i32[min max]>.Found(var value): return value;
                    case ParseOutcome<i32[min max]>.Failed(var error):
                        switch (error)
                        {
                            case ParseError.Bad: return 42;
                        }
                }
            }

            finite law i32[min max] Run()
            {
                return comptime Score(Build(false));
            }
            """,
            new CompilerOptions(OptimizationLevel: CompilerOptimizationLevel.O0));

        var runBody = ExtractDefinitionBody(llvm, "Run");
        Assert.Contains("ret i32 42", runBody);
        Assert.DoesNotContain("call", runBody);
    }

    [Fact]
    public void ComptimeExpressionEvaluatesTryFromFunnelEarlyReturn()
    {
        var llvm = CompileToLlvm(
            """
            module Demo

            enum LowError
            {
                Bad
            }

            enum HighError
            {
                Low from LowError,
                Other,
            }

            enum LowOutcome<T>
            {
                [Ok] Found(T),
                [Err] Failed(LowError),
            }

            enum HighOutcome<T>
            {
                [Ok] Found(T),
                [Err] Failed(HighError),
            }

            finite law LowOutcome<i32[min max]> Read(bool ok)
            {
                if (ok)
                {
                    return LowOutcome<i32[min max]>.Found(40);
                }

                return LowOutcome<i32[min max]>.Failed(LowError.Bad);
            }

            finite law HighOutcome<i32[min max]> Build(bool ok)
            {
                stack i32[min max] value = try Read(ok);
                return HighOutcome<i32[min max]>.Found(value + 2);
            }

            finite law i32[min max] Score(HighOutcome<i32[min max]> outcome)
            {
                switch (outcome)
                {
                    case HighOutcome<i32[min max]>.Found(var value): return value;
                    case HighOutcome<i32[min max]>.Failed(var error):
                        switch (error)
                        {
                            case HighError.Low(var low):
                                switch (low)
                                {
                                    case LowError.Bad: return 42;
                                }
                            case HighError.Other: return 0;
                        }
                }
            }

            finite law i32[min max] Run()
            {
                return comptime Score(Build(false));
            }
            """,
            new CompilerOptions(OptimizationLevel: CompilerOptimizationLevel.O0));

        var runBody = ExtractDefinitionBody(llvm, "Run");
        Assert.Contains("ret i32 42", runBody);
        Assert.DoesNotContain("call", runBody);
    }

    [Fact]
    public void ComptimeExpressionEvaluatesTryUnitStatusPropagation()
    {
        var llvm = CompileToLlvm(
            """
            module Demo

            enum Status
            {
                [Ok] Ok,
                [Err] Bad,
            }

            finite law Status Check(bool ok)
            {
                if (ok)
                {
                    return Status.Ok;
                }

                return Status.Bad;
            }

            finite law Status Build(bool ok)
            {
                try Check(ok);
                return Status.Ok;
            }

            finite law i32[min max] Score(Status status)
            {
                switch (status)
                {
                    case Status.Ok: return 0;
                    case Status.Bad: return 42;
                }
            }

            finite law i32[min max] Run()
            {
                return comptime Score(Build(false));
            }
            """,
            new CompilerOptions(OptimizationLevel: CompilerOptimizationLevel.O0));

        var runBody = ExtractDefinitionBody(llvm, "Run");
        Assert.Contains("ret i32 42", runBody);
        Assert.DoesNotContain("call", runBody);
    }

    [Fact]
    public void ComptimeExpressionEvaluatesStaticFiniteMethodCall()
    {
        var llvm = CompileToLlvm(
            """
            module Demo

            struct Calculator
            {
                static finite i32[min max] Build(i32[min max] seed)
                {
                    return seed + 22;
                }
            }

            finite law i32[min max] Run()
            {
                return comptime Calculator.Build(20);
            }
            """,
            new CompilerOptions(OptimizationLevel: CompilerOptimizationLevel.O0));

        var runBody = ExtractDefinitionBody(llvm, "Run");
        Assert.Contains("ret i32 42", runBody);
        Assert.DoesNotContain("call", runBody);
    }

    [Fact]
    public void ComptimeBlockEvaluatesReceiverFiniteMethodCall()
    {
        var llvm = CompileToLlvm(
            """
            module Demo

            struct Counter
            {
                i32[min max] Value;

                finite i32[min max] Read(borrow Counter self, i32[min max] bonus)
                {
                    return self.Value + bonus;
                }
            }

            finite law i32[min max] Run()
            {
                return comptime
                {
                    stack Counter counter = new Counter { Value = 40 };
                    return counter.Read(2);
                };
            }
            """,
            new CompilerOptions(OptimizationLevel: CompilerOptimizationLevel.O0));

        var runBody = ExtractDefinitionBody(llvm, "Run");
        Assert.Contains("ret i32 42", runBody);
        Assert.DoesNotContain("call", runBody);
    }

    [Fact]
    public void ComptimeBlockEvaluatesReceiverMethodCallOnCallResult()
    {
        var llvm = CompileToLlvm(
            """
            module Demo

            struct Counter
            {
                i32[min max] Value;

                finite i32[min max] Read(borrow Counter self, i32[min max] bonus)
                {
                    return self.Value + bonus;
                }
            }

            finite Counter BuildCounter()
            {
                return new Counter { Value = 40 };
            }

            finite law i32[min max] Run()
            {
                return comptime
                {
                    return BuildCounter().Read(2);
                };
            }
            """,
            new CompilerOptions(OptimizationLevel: CompilerOptimizationLevel.O0));

        var runBody = ExtractDefinitionBody(llvm, "Run");
        Assert.Contains("ret i32 42", runBody);
        Assert.DoesNotContain("call", runBody);
    }

    [Fact]
    public void ComptimeBlockEvaluatesReceiverMethodChain()
    {
        var llvm = CompileToLlvm(
            """
            module Demo

            struct Counter
            {
                i32[min max] Value;

                finite Counter Add(borrow Counter self, i32[min max] bonus)
                {
                    return new Counter { Value = self.Value + bonus };
                }

                finite i32[min max] Read(borrow Counter self)
                {
                    return self.Value;
                }
            }

            finite Counter BuildCounter()
            {
                return new Counter { Value = 40 };
            }

            finite law i32[min max] Run()
            {
                return comptime
                {
                    return BuildCounter().Add(2).Read();
                };
            }
            """,
            new CompilerOptions(OptimizationLevel: CompilerOptimizationLevel.O0));

        var runBody = ExtractDefinitionBody(llvm, "Run");
        Assert.Contains("ret i32 42", runBody);
        Assert.DoesNotContain("call", runBody);
    }

    [Fact]
    public void ComptimeBlockEvaluatesTraitDefaultReceiverMethodCall()
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
                i32[min max] Value;

                finite law i32[min max] Base(borrow Widget self)
                {
                    return self.Value;
                }
            }

            finite law i32[min max] Run()
            {
                return comptime
                {
                    stack Widget widget = new Widget { Value = 21 };
                    return widget.Doubled();
                };
            }
            """,
            new CompilerOptions(OptimizationLevel: CompilerOptimizationLevel.O0));

        var runBody = ExtractDefinitionBody(llvm, "Run");
        Assert.Contains("ret i32 42", runBody);
        Assert.DoesNotContain("call", runBody);
        Assert.DoesNotContain("vtable", llvm, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("dispatch", llvm, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ComptimeBlockEvaluatesGenericTraitDefaultReceiverMethodCall()
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
                i32[min max] Value;

                finite law i32[min max] Base(borrow Widget self)
                {
                    return self.Value;
                }
            }

            finite law i32[min max] CallIt<T>(borrow T value) where T: Greeter
            {
                return value.Doubled();
            }

            finite law i32[min max] Run()
            {
                return comptime
                {
                    stack Widget widget = new Widget { Value = 21 };
                    return CallIt(widget);
                };
            }
            """,
            new CompilerOptions(OptimizationLevel: CompilerOptimizationLevel.O0));

        var runBody = ExtractDefinitionBody(llvm, "Run");
        Assert.Contains("ret i32 42", runBody);
        Assert.DoesNotContain("call", runBody);
        Assert.DoesNotContain("vtable", llvm, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("dispatch", llvm, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ComptimeExpressionRejectsPlainFunctionCall()
    {
        var result = Compile(
            """
            module Demo

            fn i32[min max] Build()
            {
                return 42;
            }

            finite law i32[min max] Run()
            {
                return comptime Build();
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK3053"
            && diagnostic.Message.Contains("finite/law calls", StringComparison.Ordinal));
    }

    [Fact]
    public void ComptimeExpressionRejectsPlainReceiverMethodCall()
    {
        var result = Compile(
            """
            module Demo

            struct Counter
            {
                i32[min max] Value;

                fn i32[min max] Read(borrow Counter self)
                {
                    return self.Value;
                }
            }

            finite law i32[min max] Run()
            {
                return comptime
                {
                    stack Counter counter = new Counter { Value = 42 };
                    return counter.Read();
                };
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK3053"
            && diagnostic.Message.Contains("finite/law calls", StringComparison.Ordinal));
    }

    [Fact]
    public void ComptimeExpressionCanInferConstStorage()
    {
        var llvm = CompileToLlvm(
            """
            module Demo

            const Answer = comptime Build();

            finite law i32[min max] Build()
            {
                return 42;
            }

            finite law i32[min max] Run()
            {
                return Answer;
            }
            """,
            new CompilerOptions(OptimizationLevel: CompilerOptimizationLevel.O0));

        var runBody = ExtractDefinitionBody(llvm, "Run");
        Assert.Contains("ret i32 42", runBody);
    }

    [Fact]
    public void ComptimeExpressionCanMaterializeFixedArrayConstGlobal()
    {
        var llvm = CompileToLlvm(
            """
            module Demo

            const Values = comptime Build();

            finite law i32[min max][3] Build()
            {
                const i32[min max][3] values =
                {
                    10,
                    20,
                    30
                };
                return values;
            }

            finite law i32[min max] Run()
            {
                return Values[1];
            }
            """,
            new CompilerOptions(OptimizationLevel: CompilerOptimizationLevel.O0));

        Assert.Contains("@Values = local_unnamed_addr constant [3 x i32] [i32 10, i32 20, i32 30]", llvm);
        var runBody = ExtractDefinitionBody(llvm, "Run");
        Assert.Contains("extractvalue [3 x i32]", runBody);
        Assert.DoesNotContain("load i32, ptr @Values", runBody);
        Assert.DoesNotContain("call fastcc [3 x i32] @Build", runBody);
    }

    [Fact]
    public void ComptimeFixedArrayExpressionCanLowerToRuntimeAggregate()
    {
        var llvm = CompileToLlvm(
            """
            module Demo

            finite law i32[min max][3] Build()
            {
                stack i32[min max][3] values =
                {
                    3,
                    5,
                    8
                };
                return values;
            }

            finite law i32[min max][3] Run()
            {
                return comptime Build();
            }
            """,
            new CompilerOptions(OptimizationLevel: CompilerOptimizationLevel.O0));

        var runBody = ExtractDefinitionBody(llvm, "Run");
        Assert.Contains("insertvalue [3 x i32]", runBody);
        Assert.DoesNotContain("call fastcc [3 x i32] @Build", runBody);
    }

    [Fact]
    public void ComptimeExpressionCanMaterializeNamedAggregateForFieldProjection()
    {
        var llvm = CompileToLlvm(
            """
            module Demo

            struct Pair
            {
                i32[min max] Left;
                i32[min max] Right;
            }

            finite law Pair Build()
            {
                return new Pair()
                {
                    Left = 10,
                    Right = 32
                };
            }

            finite law i32[min max] Run()
            {
                return (comptime Build()).Right;
            }
            """,
            new CompilerOptions(OptimizationLevel: CompilerOptimizationLevel.O0));

        var runBody = ExtractDefinitionBody(llvm, "Run");
        Assert.Contains("insertvalue %Pair", runBody);
        Assert.Contains("extractvalue %Pair", runBody);
        Assert.DoesNotContain("call fastcc %Pair @Build", runBody);
    }

    [Fact]
    public void ComptimeBlockCanInitializeNamedAggregateConstGlobal()
    {
        var llvm = CompileToLlvm(
            """
            module Demo

            struct Pair
            {
                i32[min max] Left;
                i32[min max] Right;
            }

            const Pair Origin = comptime
            {
                return new Pair()
                {
                    Left = 3,
                    Right = 9
                };
            };

            finite law i32[min max] Run()
            {
                return Origin.Right;
            }
            """,
            new CompilerOptions(OptimizationLevel: CompilerOptimizationLevel.O0));

        Assert.Contains("@Origin = local_unnamed_addr constant %Pair { i32 3, i32 9 }", llvm);
        var runBody = ExtractDefinitionBody(llvm, "Run");
        Assert.Contains("ret i32 9", runBody);
        Assert.DoesNotContain("load i32, ptr @Origin", runBody);
    }

    [Fact]
    public void ComptimeBlockCanBuildNestedNamedAggregateAndFixedArray()
    {
        var llvm = CompileToLlvm(
            """
            module Demo

            struct Inner
            {
                i32[min max] Value;
            }

            struct Outer
            {
                Inner Item;
                i32[min max][2] Values;
            }

            const Outer Graph = comptime
            {
                return new Outer()
                {
                    Item = new Inner()
                    {
                        Value = 7
                    },
                    Values =
                    {
                        4,
                        5
                    }
                };
            };

            finite law i32[min max] Run()
            {
                return Graph.Item.Value + Graph.Values[1];
            }
            """,
            new CompilerOptions(OptimizationLevel: CompilerOptimizationLevel.O0));

        Assert.Contains("@Graph = local_unnamed_addr constant %Outer { %Inner { i32 7 }, [2 x i32] [i32 4, i32 5] }", llvm);
        var runBody = ExtractDefinitionBody(llvm, "Run");
        Assert.Contains("ret i32 12", runBody);
        Assert.DoesNotContain("load %Outer, ptr @Graph", runBody);
    }

    [Fact]
    public void ComptimeExpressionCanUseRecordPrimaryConstructor()
    {
        var llvm = CompileToLlvm(
            """
            module Demo

            record Point(i32[min max] X)
            {
                i32[min max] Y;
            }

            finite law Point Build()
            {
                return new Point(3)
                {
                    Y = 9
                };
            }

            finite law i32[min max] Run()
            {
                return (comptime Build()).X + (comptime Build()).Y;
            }
            """,
            new CompilerOptions(OptimizationLevel: CompilerOptimizationLevel.O0));

        var runBody = ExtractDefinitionBody(llvm, "Run");
        Assert.Contains("insertvalue %Point", runBody);
        Assert.Contains("extractvalue %Point", runBody);
        Assert.DoesNotContain("call fastcc %Point @Build", runBody);
    }

    [Fact]
    public void ComptimeExpressionCanReadLocalNamedAggregateConst()
    {
        var llvm = CompileToLlvm(
            """
            module Demo

            struct Pair
            {
                i32[min max] Left;
                i32[min max] Right;
            }

            finite law i32[min max] Run()
            {
                const Pair local =
                {
                    Left = 2,
                    Right = 5
                };

                return comptime local.Right;
            }
            """,
            new CompilerOptions(OptimizationLevel: CompilerOptimizationLevel.O0));

        var runBody = ExtractDefinitionBody(llvm, "Run");
        Assert.Contains("ret i32 5", runBody);
    }

    [Fact]
    public void ComptimeExpressionCanMaterializeTupleEnumPayload()
    {
        var llvm = CompileToLlvm(
            """
            module Demo

            enum Token
            {
                End,
                Integer(i32[min max]),
                Move
                {
                    X: i32[min max], Y: i32[min max]
                },
            }

            finite law Token Build()
            {
                return Token.Integer(41);
            }

            finite law i32[min max] Run()
            {
                stack Token token = comptime Build();
                switch (token)
                {
                    case Token.End:
                        return 0;
                    case Token.Integer(var value):
                        return value + 1;
                    case Token.Move
                    {
                        X: var x, Y: var y
                    }:
                        return x + y;
                }
            }
            """,
            new CompilerOptions(OptimizationLevel: CompilerOptimizationLevel.O0));

        var runBody = ExtractDefinitionBody(llvm, "Run");
        Assert.Contains("insertvalue %Token", runBody);
        Assert.DoesNotContain("call fastcc %Token @Build", runBody);
    }

    [Fact]
    public void ComptimeBlockCanInitializeNamedFieldEnumConstGlobal()
    {
        var llvm = CompileToLlvm(
            """
            module Demo

            enum Token
            {
                End,
                Integer(i32[min max]),
                Move
                {
                    X: i32[min max], Y: i32[min max]
                },
            }

            const Token Favorite = comptime
            {
                return Token.Move
                {
                    X: 2, Y: 5
                };
            };

            finite law i32[min max] Run()
            {
                switch (Favorite)
                {
                    case Token.End:
                        return 0;
                    case Token.Integer(var value):
                        return value;
                    case Token.Move
                    {
                        X: var x, Y: var y
                    }:
                        return x + y;
                }
            }
            """,
            new CompilerOptions(OptimizationLevel: CompilerOptimizationLevel.O0));

        Assert.Contains("@Favorite = local_unnamed_addr constant %Token { i8 2, i32 0, i32 2, i32 5 }", llvm);
        var runBody = ExtractDefinitionBody(llvm, "Run");
        Assert.DoesNotContain("load %Token, ptr @Favorite", runBody);
    }

    [Fact]
    public void ComptimeBlockCanExecuteSwitchWithEnumPatternsCapturesAndGuards()
    {
        var llvm = CompileToLlvm(
            """
            module Demo

            enum Token
            {
                End,
                Number(i32[min max]),
                Pair
                {
                    Left: i32[min max], Right: i32[min max]
                },
            }

            finite law i32[min max] Score(Token token)
            {
                switch (token)
                {
                    case Token.End:
                        return 1;
                    case Token.Number(var value) when value > 40:
                        return value + 1;
                    case Token.Number(var value):
                        return value;
                    case Token.Pair
                    {
                        Left: var left, Right: var right
                    }:
                        return left + right;
                }
            }

            finite law i32[min max] Run()
            {
                return comptime Score(Token.Number(41));
            }
            """,
            new CompilerOptions(OptimizationLevel: CompilerOptimizationLevel.O0));

        var runBody = ExtractDefinitionBody(llvm, "Run");
        Assert.Contains("ret i32 42", runBody);
        Assert.DoesNotContain("call fastcc i32 @Score", runBody);
    }

    [Fact]
    public void ComptimeBlockCanExecuteSwitchWithAggregateListRangeOrAndDefaultPatterns()
    {
        var llvm = CompileToLlvm(
            """
            module Demo

            struct Point
            {
                i32[min max] X;
                i32[min max] Y;
            }

            finite law i32[min max] Run()
            {
                return comptime
                {
                    stack mut i32[min max] total = 0;
                    stack Point point = new Point()
                    {
                        X = 3,
                        Y = 29
                    };

                    switch (point)
                    {
                        case Point { X: 1..4, Y: var y }:
                            total += y + 10;
                        default:
                            total += 100;
                    }

                    const i32[min max][3] values =
                    {
                        1,
                        2,
                        3
                    };
                    switch (values)
                    {
                        case [1, 2, var last]:
                            total += last;
                        default:
                            total += 100;
                    }

                    stack i32[min max] small = 1;
                    switch (small)
                    {
                        case 0 | 1:
                            total += 5;
                        case 2..4:
                            total += 7;
                        default:
                            total += 9;
                    }

                    stack i32[min max] large = 8;
                    switch (large)
                    {
                        case 0 | 1:
                            total += 5;
                        case 2..4:
                            total += 7;
                        default:
                            total += 9;
                    }

                    return total;
                };
            }
            """,
            new CompilerOptions(OptimizationLevel: CompilerOptimizationLevel.O0));

        var runBody = ExtractDefinitionBody(llvm, "Run");
        Assert.Contains("ret i32 56", runBody);
        Assert.DoesNotContain(" br ", runBody);
    }

    [Fact]
    public void ComptimeBlockCanExecutePatternIfConditions()
    {
        var llvm = CompileToLlvm(
            """
            module Demo

            enum Token
            {
                End,
                Number(i32[min max])
            }

            struct Point
            {
                i32[min max] X;
                i32[min max] Y;
            }

            finite law i32[min max] Score(Token token, Point point)
            {
                stack mut i32[min max] total = 0;
                if (token is Token.Number(var value))
                {
                    total += value;
                }
                else
                {
                    total += 100;
                }

                if (point is Point { X: 1..4, Y: var y })
                {
                    total += y + 1;
                }
                else
                {
                    total += 100;
                }

                return total;
            }

            finite law i32[min max] Run()
            {
                return comptime
                {
                    stack Point point = new Point()
                    {
                        X = 3,
                        Y = 21
                    };

                    return Score(Token.Number(20), point);
                };
            }
            """,
            new CompilerOptions(OptimizationLevel: CompilerOptimizationLevel.O0));

        var runBody = ExtractDefinitionBody(llvm, "Run");
        Assert.Contains("ret i32 42", runBody);
        Assert.DoesNotContain(" br ", runBody);
    }

    [Fact]
    public void ComptimeBlockCanExecutePatternWhileConditions()
    {
        var llvm = CompileToLlvm(
            """
            module Demo

            enum Step
            {
                More(i32[min max]),
                Done
            }

            finite law Step Next(i32[min max] value)
            {
                if (value < 4)
                {
                    return Step.More(value + 1);
                }

                return Step.Done;
            }

            finite law i32[min max] Run()
            {
                return comptime
                {
                    stack mut i32[min max] value = 0;
                    stack mut i32[min max] sum = 0;
                    while willexit (Next(value) is Step.More(var next))
                    {
                        value = next;
                        if (value == 3)
                        {
                            continue;
                        }

                        sum += value;
                        if (sum > 4)
                        {
                            break;
                        }
                    }

                    return sum;
                };
            }
            """,
            new CompilerOptions(OptimizationLevel: CompilerOptimizationLevel.O0));

        var runBody = ExtractDefinitionBody(llvm, "Run");
        Assert.Contains("ret i32 7", runBody);
        Assert.DoesNotContain(" br ", runBody);
    }

    [Fact]
    public void ComptimeBlockCanBranchOnUnitEnumEquality()
    {
        var llvm = CompileToLlvm(
            """
            module Demo

            enum Color
            {
                Red,
                Green,
                Blue
            }

            finite law i32[min max] Run()
            {
                return comptime
                {
                    const color = Color.Green;
                    if (color == Color.Green)
                    {
                        return 12;
                    }

                    return 0;
                };
            }
            """,
            new CompilerOptions(OptimizationLevel: CompilerOptimizationLevel.O0));

        var runBody = ExtractDefinitionBody(llvm, "Run");
        Assert.Contains("ret i32 12", runBody);
        Assert.DoesNotContain(" br ", runBody);
    }

    [Fact]
    public void ComptimeExpressionCanFoldStructLayoutFacts()
    {
        var llvm = CompileToLlvm(
            """
            module Demo

            struct Pair
            {
                i32[min max] Left;
                i64[min max] Right;
            }

            finite law i64[min max] Run()
            {
                return comptime sizeof(Pair) + comptime alignof(Pair);
            }
            """,
            new CompilerOptions(OptimizationLevel: CompilerOptimizationLevel.O0));

        var runBody = ExtractDefinitionBody(llvm, "Run");
        Assert.Contains("ret i64 24", runBody);
        Assert.DoesNotContain("call fastcc", runBody);
    }

    [Fact]
    public void ComptimeBlockCanFoldEnumLayoutFacts()
    {
        var llvm = CompileToLlvm(
            """
            module Demo

            enum Status
            {
                Ok,
                Err(i32[min max])
            }

            finite law i64[min max] Run()
            {
                return comptime
                {
                    return sizeof(Status) + alignof(Status);
                };
            }
            """,
            new CompilerOptions(OptimizationLevel: CompilerOptimizationLevel.O0));

        var runBody = ExtractDefinitionBody(llvm, "Run");
        Assert.Contains("ret i64 12", runBody);
        Assert.DoesNotContain("call fastcc", runBody);
    }

    [Fact]
    public void ComptimeLawCallCanFoldLayoutFacts()
    {
        var llvm = CompileToLlvm(
            """
            module Demo

            struct Pair
            {
                i32[min max] Left;
                i64[min max] Right;
            }

            finite law i64[min max] LayoutScore()
            {
                return sizeof(Pair) + alignof(Pair);
            }

            finite law i64[min max] Run()
            {
                return comptime LayoutScore();
            }
            """,
            new CompilerOptions(OptimizationLevel: CompilerOptimizationLevel.O0));

        var runBody = ExtractDefinitionBody(llvm, "Run");
        Assert.Contains("ret i64 24", runBody);
        Assert.DoesNotContain("call fastcc i64 @LayoutScore", runBody);
    }

    [Fact]
    public void ComptimeLayoutFactsCanInitializeConstGlobal()
    {
        var llvm = CompileToLlvm(
            """
            module Demo

            struct Pair
            {
                i32[min max] Left;
                i64[min max] Right;
            }

            const u8 PairSize = comptime sizeof(Pair);

            finite law i64[min max] Run()
            {
                return PairSize;
            }
            """,
            new CompilerOptions(OptimizationLevel: CompilerOptimizationLevel.O0));

        Assert.Contains("@PairSize = local_unnamed_addr constant i8 16", llvm);
        var runBody = ExtractDefinitionBody(llvm, "Run");
        Assert.Contains("ret i64 16", runBody);
    }

    [Fact]
    public void ComptimeStructuralFactsFoldToBoolConstants()
    {
        var llvm = CompileToLlvm(
            """
            module Demo

            struct Header
            {
                i32[min max] Tag;
            }

            enum Token
            {
                Name,
                Number
            }

            finite law i32[min max] Run()
            {
                if (comptime System.Compiler.IsStruct<Header>())
                {
                    if (comptime System.Compiler.IsEnum<Token>())
                    {
                        if (comptime System.Compiler.IsInteger<i32[min max]>())
                        {
                            return 42;
                        }
                    }
                }

                return 0;
            }
            """,
            new CompilerOptions(OptimizationLevel: CompilerOptimizationLevel.O0));

        var runBody = ExtractDefinitionBody(llvm, "Run");
        Assert.Contains("ret i32 42", runBody);
        Assert.DoesNotContain("call", runBody);
    }

    [Fact]
    public void ComptimeVisibilityStructuralFactsFoldToBoolConstants()
    {
        var llvm = CompileToLlvm(
            """
            module Demo

            struct ModuleType
            {
                i32[min max] Value;
            }

            internal struct InternalType
            {
                i32[min max] Value;
            }

            public struct PublicType
            {
                internal i32[min max] Hidden;
                public i32[min max] Visible;

                internal finite law i32[min max] HiddenMethod(borrow PublicType self)
                {
                    return self.Hidden;
                }

                public finite law i32[min max] VisibleMethod(borrow PublicType self)
                {
                    return self.Visible;
                }
            }

            export struct ExportType
            {
                public i32[min max] Value;
            }

            struct ModuleMethods
            {
                finite law i32[min max] Local(borrow ModuleMethods self)
                {
                    return 1;
                }
            }

            finite law i32[min max] Run()
            {
                if (comptime System.Compiler.TypeVisibilityIsModule<ModuleType>()
                    && comptime System.Compiler.TypeVisibilityIsInternal<InternalType>()
                    && comptime System.Compiler.TypeVisibilityIsPublic<PublicType>()
                    && comptime System.Compiler.TypeVisibilityIsExport<ExportType>()
                    && comptime !System.Compiler.TypeVisibilityIsPublic<ModuleType>()
                    && comptime System.Compiler.FieldVisibilityIsModule<ModuleType, 0>()
                    && comptime System.Compiler.FieldVisibilityIsInternal<PublicType, 0>()
                    && comptime System.Compiler.FieldVisibilityIsPublic<PublicType, 1>()
                    && comptime !System.Compiler.FieldVisibilityIsExport<ExportType, 0>()
                    && comptime (System.Compiler.MethodName<ModuleMethods, 0>() == "Local")
                    && comptime System.Compiler.MethodVisibilityIsModule<ModuleMethods, 0>()
                    && comptime (System.Compiler.MethodName<PublicType, 0>() == "HiddenMethod")
                    && comptime System.Compiler.MethodVisibilityIsInternal<PublicType, 0>()
                    && comptime (System.Compiler.MethodName<PublicType, 1>() == "VisibleMethod")
                    && comptime System.Compiler.MethodVisibilityIsPublic<PublicType, 1>()
                    && comptime !System.Compiler.MethodVisibilityIsExport<PublicType, 1>())
                {
                    return 42;
                }

                return 0;
            }
            """,
            new CompilerOptions(OptimizationLevel: CompilerOptimizationLevel.O0));

        var runBody = ExtractDefinitionBody(llvm, "Run");
        Assert.Contains("ret i32 42", runBody);
        Assert.DoesNotContain("call", runBody);
    }

    [Fact]
    public void ComptimeVisibilityStructuralFactsRejectOutOfRangeIndices()
    {
        var fieldResult = Compile(
            """
            module Demo

            struct Header
            {
                i32[min max] Tag;
            }

            finite law bool Run()
            {
                return comptime System.Compiler.FieldVisibilityIsModule<Header, 1>();
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(fieldResult.Succeeded);
        Assert.Contains(fieldResult.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK3054"
            && diagnostic.Message.Contains("field index '1' is out of range", StringComparison.Ordinal));

        var methodResult = Compile(
            """
            module Demo

            struct Header
            {
                finite law i32[min max] Tag(borrow Header self)
                {
                    return 0;
                }
            }

            finite law bool Run()
            {
                return comptime System.Compiler.MethodVisibilityIsModule<Header, 1>();
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(methodResult.Succeeded);
        Assert.Contains(methodResult.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK3054"
            && diagnostic.Message.Contains("method index '1' is out of range", StringComparison.Ordinal));
    }

    [Fact]
    public void ComptimeVisibilityStructuralFactsAreRejectedAtRuntime()
    {
        var result = Compile(
            """
            module Demo

            struct Header
            {
                i32[min max] Tag;
            }

            finite law bool Run()
            {
                return System.Compiler.TypeVisibilityIsModule<Header>();
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK3054"
            && diagnostic.Message.Contains("may only be used inside a `comptime` expression or block", StringComparison.Ordinal));
    }

    [Fact]
    public void ComptimeTypeQualifierMetadataFactsFoldToBoolConstants()
    {
        var llvm = CompileToLlvm(
            """
            module Demo

            finite law i32[min max] Run()
            {
                if (comptime System.Compiler.TypeHasQualifiers<borrow mut i32[min max]>()
                    && comptime System.Compiler.TypeBorrowKindIsBorrow<borrow mut i32[min max]>()
                    && comptime System.Compiler.TypeIsMutableView<borrow mut i32[min max]>()
                    && comptime System.Compiler.TypeBorrowKindIsRetBorrow<retborrow i32[min max]>()
                    && comptime System.Compiler.TypeBorrowKindIsStoreBorrow<storeborrow i32[min max]>()
                    && comptime System.Compiler.TypeBorrowKindIsNone<i32[min max]>()
                    && comptime System.Compiler.TypeAccessKindIsShared<shared i32[min max]>()
                    && comptime System.Compiler.TypeAccessKindIsFrozen<frozen i32[min max]>()
                    && comptime System.Compiler.TypeAccessKindIsNone<i32[min max]>()
                    && comptime System.Compiler.TypeInitializationKindIsOut<out i32[min max]>()
                    && comptime System.Compiler.TypeInitializationKindIsInit<init i32[min max]>()
                    && comptime System.Compiler.TypeInitializationKindIsNone<i32[min max]>()
                    && comptime System.Compiler.TypeUnqualifiedTypeIs<borrow mut i32[min max], frozen i32[min max]>()
                    && comptime !System.Compiler.TypeHasQualifiers<i32[min max]>()
                    && comptime !System.Compiler.TypeUnqualifiedTypeIs<borrow i32[min max], u32[0 max]>())
                {
                    return 42;
                }

                return 0;
            }
            """,
            new CompilerOptions(OptimizationLevel: CompilerOptimizationLevel.O0));

        var runBody = ExtractDefinitionBody(llvm, "Run");
        Assert.Contains("ret i32 42", runBody);
        Assert.DoesNotContain("call", runBody);
    }

    [Fact]
    public void ComptimeCallableQualifierMetadataFactsFoldToBoolConstants()
    {
        var llvm = CompileToLlvm(
            """
            module Demo

            struct Box
            {
                i32[min max] Value;
            }

            trait Service
            {
                fn retborrow Box Pick(
                    borrow Self self,
                    retborrow Box value,
                    borrow mut Box target,
                    out i32[min max] code,
                    shared Box sharedBox,
                    frozen Box frozenBox,
                    init i32[min max][] output);
            }

            alias Callback = fnptr<fn retborrow Box(
                retborrow Box,
                borrow mut Box,
                out i32[min max],
                shared Box,
                frozen Box,
                init i32[min max][])>;

            alias Worker = borrow closure<fn retborrow Box(
                retborrow Box,
                borrow mut Box,
                out i32[min max],
                shared Box,
                frozen Box,
                init i32[min max][])>;

            finite law i32[min max] Run()
            {
                if (comptime System.Compiler.FunctionPointerReturnTypeHasQualifiers<Callback>()
                    && comptime System.Compiler.FunctionPointerReturnTypeBorrowKindIsRetBorrow<Callback>()
                    && comptime System.Compiler.FunctionPointerReturnTypeUnqualifiedTypeIs<Callback, Box>()
                    && comptime System.Compiler.FunctionPointerParameterTypeBorrowKindIsRetBorrow<Callback, 0>()
                    && comptime System.Compiler.FunctionPointerParameterTypeBorrowKindIsBorrow<Callback, 1>()
                    && comptime System.Compiler.FunctionPointerParameterTypeIsMutableView<Callback, 1>()
                    && comptime System.Compiler.FunctionPointerParameterTypeInitializationKindIsOut<Callback, 2>()
                    && comptime System.Compiler.FunctionPointerParameterTypeAccessKindIsShared<Callback, 3>()
                    && comptime System.Compiler.FunctionPointerParameterTypeAccessKindIsFrozen<Callback, 4>()
                    && comptime System.Compiler.FunctionPointerParameterTypeInitializationKindIsInit<Callback, 5>()
                    && comptime System.Compiler.FunctionPointerParameterTypeUnqualifiedTypeIs<Callback, i32[min max][], 5>()
                    && comptime System.Compiler.ClosureReturnTypeBorrowKindIsRetBorrow<Worker>()
                    && comptime System.Compiler.ClosureReturnTypeUnqualifiedTypeIs<Worker, Box>()
                    && comptime System.Compiler.ClosureParameterTypeBorrowKindIsRetBorrow<Worker, 0>()
                    && comptime System.Compiler.ClosureParameterTypeBorrowKindIsBorrow<Worker, 1>()
                    && comptime System.Compiler.ClosureParameterTypeIsMutableView<Worker, 1>()
                    && comptime System.Compiler.ClosureParameterTypeInitializationKindIsOut<Worker, 2>()
                    && comptime System.Compiler.ClosureParameterTypeAccessKindIsShared<Worker, 3>()
                    && comptime System.Compiler.ClosureParameterTypeAccessKindIsFrozen<Worker, 4>()
                    && comptime System.Compiler.ClosureParameterTypeInitializationKindIsInit<Worker, 5>()
                    && comptime System.Compiler.MethodReturnTypeBorrowKindIsRetBorrow<Service, 0>()
                    && comptime System.Compiler.MethodReturnTypeUnqualifiedTypeIs<Service, Box, 0>()
                    && comptime System.Compiler.MethodParameterTypeBorrowKindIsBorrow<Service, 0, 0>()
                    && comptime System.Compiler.MethodParameterTypeBorrowKindIsRetBorrow<Service, 0, 1>()
                    && comptime System.Compiler.MethodParameterTypeBorrowKindIsBorrow<Service, 0, 2>()
                    && comptime System.Compiler.MethodParameterTypeIsMutableView<Service, 0, 2>()
                    && comptime System.Compiler.MethodParameterTypeInitializationKindIsOut<Service, 0, 3>()
                    && comptime System.Compiler.MethodParameterTypeAccessKindIsShared<Service, 0, 4>()
                    && comptime System.Compiler.MethodParameterTypeAccessKindIsFrozen<Service, 0, 5>()
                    && comptime System.Compiler.MethodParameterTypeInitializationKindIsInit<Service, 0, 6>()
                    && comptime System.Compiler.MethodParameterTypeUnqualifiedTypeIs<Service, i32[min max][], 0, 6>())
                {
                    return 42;
                }

                return 0;
            }
            """,
            new CompilerOptions(OptimizationLevel: CompilerOptimizationLevel.O0));

        var runBody = ExtractDefinitionBody(llvm, "Run");
        Assert.Contains("ret i32 42", runBody);
        Assert.DoesNotContain("call", runBody);
    }

    [Fact]
    public void ComptimeFieldAndEnumPayloadQualifierMetadataFactsFoldToBoolConstants()
    {
        var llvm = CompileToLlvm(
            """
            module Demo

            struct Box
            {
                i32[min max] Value;
            }

            struct Holder
            {
                Box Plain;
                storeborrow Box Stored;
                shared Box SharedBox;
                frozen Box FrozenBox;
            }

            enum Packet
            {
                Plain(Box),
                Stored(storeborrow Box),
                SharedBox(shared Box),
                FrozenBox(frozen Box)
            }

            finite law i32[min max] Run()
            {
                if (comptime !System.Compiler.FieldTypeHasQualifiers<Holder, 0>()
                    && comptime System.Compiler.FieldTypeBorrowKindIsNone<Holder, 0>()
                    && comptime System.Compiler.FieldTypeAccessKindIsNone<Holder, 0>()
                    && comptime System.Compiler.FieldTypeInitializationKindIsNone<Holder, 0>()
                    && comptime System.Compiler.FieldTypeUnqualifiedTypeIs<Holder, Box, 0>()
                    && comptime System.Compiler.FieldTypeHasQualifiers<Holder, 1>()
                    && comptime System.Compiler.FieldTypeBorrowKindIsStoreBorrow<Holder, 1>()
                    && comptime System.Compiler.FieldTypeUnqualifiedTypeIs<Holder, Box, 1>()
                    && comptime System.Compiler.FieldTypeAccessKindIsShared<Holder, 2>()
                    && comptime System.Compiler.FieldTypeAccessKindIsFrozen<Holder, 3>()
                    && comptime !System.Compiler.EnumVariantPayloadTypeHasQualifiers<Packet, 0, 0>()
                    && comptime System.Compiler.EnumVariantPayloadTypeBorrowKindIsNone<Packet, 0, 0>()
                    && comptime System.Compiler.EnumVariantPayloadTypeAccessKindIsNone<Packet, 0, 0>()
                    && comptime System.Compiler.EnumVariantPayloadTypeInitializationKindIsNone<Packet, 0, 0>()
                    && comptime System.Compiler.EnumVariantPayloadTypeUnqualifiedTypeIs<Packet, Box, 0, 0>()
                    && comptime System.Compiler.EnumVariantPayloadTypeHasQualifiers<Packet, 1, 0>()
                    && comptime System.Compiler.EnumVariantPayloadTypeBorrowKindIsStoreBorrow<Packet, 1, 0>()
                    && comptime System.Compiler.EnumVariantPayloadTypeUnqualifiedTypeIs<Packet, Box, 1, 0>()
                    && comptime System.Compiler.EnumVariantPayloadTypeAccessKindIsShared<Packet, 2, 0>()
                    && comptime System.Compiler.EnumVariantPayloadTypeAccessKindIsFrozen<Packet, 3, 0>())
                {
                    return 42;
                }

                return 0;
            }
            """,
            new CompilerOptions(OptimizationLevel: CompilerOptimizationLevel.O0));

        var runBody = ExtractDefinitionBody(llvm, "Run");
        Assert.Contains("ret i32 42", runBody);
        Assert.DoesNotContain("call", runBody);
    }

    [Fact]
    public void ComptimeStructuralFactsAreRejectedAtRuntime()
    {
        var result = Compile(
            """
            module Demo

            finite law bool Run()
            {
                return System.Compiler.IsInteger<i32[min max]>();
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK3054"
            && diagnostic.Message.Contains("may only be used inside a `comptime` expression or block", StringComparison.Ordinal));

        var typeArgumentResult = Compile(
            """
            module Demo

            struct Buffer<T, comptime u8[1 8] N>
            {
                T[N] Items;
            }

            finite law u64[0 max] Run()
            {
                return System.Compiler.TypeArgumentCount<Buffer<i32[min max], 4>>();
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(typeArgumentResult.Succeeded);
        Assert.Contains(typeArgumentResult.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK3054"
            && diagnostic.Message.Contains("may only be used inside a `comptime` expression or block", StringComparison.Ordinal));

        var qualifierResult = Compile(
            """
            module Demo

            finite law bool Run()
            {
                return System.Compiler.TypeBorrowKindIsBorrow<borrow i32[min max]>();
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(qualifierResult.Succeeded);
        Assert.Contains(qualifierResult.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK3054"
            && diagnostic.Message.Contains("may only be used inside a `comptime` expression or block", StringComparison.Ordinal));
    }

    [Fact]
    public void ComptimeStructuralCountFactsFoldToIntegerConstants()
    {
        var llvm = CompileToLlvm(
            """
            module Demo

            struct Header
            {
                i32[min max] Tag;
                i32[min max] Start;
                i32[min max] Length;
            }

            record Span(i32[min max] Start, i32[min max] Length)
            {
            }

            enum Token
            {
                Name,
                Number,
                End
            }

            finite law u64[0 max] HeaderFields()
            {
                return comptime System.Compiler.FieldCount<Header>();
            }

            finite law u64[0 max] SpanFields()
            {
                return comptime System.Compiler.FieldCount<Span>();
            }

            finite law u64[0 max] TokenVariants()
            {
                return comptime System.Compiler.EnumVariantCount<Token>();
            }
            """,
            new CompilerOptions(OptimizationLevel: CompilerOptimizationLevel.O0));

        var headerBody = ExtractDefinitionBody(llvm, "HeaderFields");
        Assert.Contains("ret i64 3", headerBody);
        Assert.DoesNotContain("call", headerBody);

        var spanBody = ExtractDefinitionBody(llvm, "SpanFields");
        Assert.Contains("ret i64 2", spanBody);
        Assert.DoesNotContain("call", spanBody);

        var tokenBody = ExtractDefinitionBody(llvm, "TokenVariants");
        Assert.Contains("ret i64 3", tokenBody);
        Assert.DoesNotContain("call", tokenBody);
    }

    [Fact]
    public void GenericComptimeLawCanBranchOnStructuralCountFacts()
    {
        var llvm = CompileToLlvm(
            """
            module Demo

            struct Pair
            {
                i32[min max] Left;
                i32[min max] Right;
            }

            enum Token
            {
                Name,
                Number,
                End
            }

            finite law u64[0 max] ShapeScore<T>()
            {
                if (comptime System.Compiler.FieldCount<T>() == 2)
                {
                    return 20;
                }

                if (comptime System.Compiler.EnumVariantCount<T>() == 3)
                {
                    return 30;
                }

                return 1;
            }

            finite law u64[0 max] RunStruct()
            {
                return comptime ShapeScore<Pair>();
            }

            finite law u64[0 max] RunEnum()
            {
                return comptime ShapeScore<Token>();
            }
            """,
            new CompilerOptions(OptimizationLevel: CompilerOptimizationLevel.O0));

        var structBody = ExtractDefinitionBody(llvm, "RunStruct");
        Assert.Contains("ret i64 20", structBody);
        Assert.DoesNotContain("call", structBody);

        var enumBody = ExtractDefinitionBody(llvm, "RunEnum");
        Assert.Contains("ret i64 30", enumBody);
        Assert.DoesNotContain("call", enumBody);
    }

    [Fact]
    public void ComptimeStructuralCountFactsAreRejectedAtRuntime()
    {
        var result = Compile(
            """
            module Demo

            struct Header
            {
                i32[min max] Tag;
            }

            finite law u64[0 max] Run()
            {
                return System.Compiler.FieldCount<Header>();
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK3054"
            && diagnostic.Message.Contains("may only be used inside a `comptime` expression or block", StringComparison.Ordinal));

        var methodResult = Compile(
            """
            module Demo

            struct Header
            {
                fn i32[min max] Read(borrow Header self)
                {
                    return 1;
                }
            }

            finite law ascii Run()
            {
                return System.Compiler.MethodParameterName<Header, 0, 0>();
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(methodResult.Succeeded);
        Assert.Contains(methodResult.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK3054"
            && diagnostic.Message.Contains("may only be used inside a `comptime` expression or block", StringComparison.Ordinal));
    }

    [Fact]
    public void ComptimeTypeLayoutFactsFoldToConstants()
    {
        var llvm = CompileToLlvm(
            """
            module Demo

            [StructLayout(C), Pack(1), Align(4)]
            struct Packet
            {
                u8[0 max] Tag;
                u32[0 max] Length;
            }

            struct Marker
            {
            }

            finite law u64[0 max] TypeSizeOf<T>()
            {
                return comptime System.Compiler.TypeSize<T>();
            }

            finite law u64[0 max] Run()
            {
                return comptime System.Compiler.TypeSize<Packet>()
                    + comptime System.Compiler.TypeAlign<Packet>()
                    + comptime TypeSizeOf<Packet>();
            }

            finite law bool PacketIsZeroSized()
            {
                return comptime System.Compiler.TypeIsZeroSized<Packet>();
            }

            finite law bool MarkerIsZeroSized()
            {
                return comptime System.Compiler.TypeIsZeroSized<Marker>();
            }
            """,
            new CompilerOptions(OptimizationLevel: CompilerOptimizationLevel.O0));

        var runBody = ExtractDefinitionBody(llvm, "Run");
        Assert.Contains("ret i64 20", runBody);
        Assert.DoesNotContain("call", runBody);

        var packetBody = ExtractDefinitionBody(llvm, "PacketIsZeroSized");
        Assert.Contains("ret i1 false", packetBody);
        Assert.DoesNotContain("call", packetBody);

        var markerBody = ExtractDefinitionBody(llvm, "MarkerIsZeroSized");
        Assert.Contains("ret i1 true", markerBody);
        Assert.DoesNotContain("call", markerBody);
    }

    [Fact]
    public void ComptimeTypeLayoutFactsRequireConcreteLayout()
    {
        var result = Compile(
            """
            module Demo

            trait Shape
            {
            }

            finite law u64[0 max] Run()
            {
                return comptime System.Compiler.TypeSize<Shape>();
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK3054"
            && diagnostic.Message.Contains("requires concrete layout for 'Shape'", StringComparison.Ordinal));
    }

    [Fact]
    public void ComptimeTypeLayoutFactsAreRejectedAtRuntime()
    {
        var result = Compile(
            """
            module Demo

            struct Header
            {
                i32[min max] Tag;
            }

            finite law u64[0 max] Run()
            {
                return System.Compiler.TypeSize<Header>();
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK3054"
            && diagnostic.Message.Contains("may only be used inside a `comptime` expression or block", StringComparison.Ordinal));

    }

    [Fact]
    public void ComptimeScalarTypeMetadataFactsFoldToConstants()
    {
        var llvm = CompileToLlvm(
            """
            module Demo

            finite law u64[0 max] IntegerWidth<T>()
            {
                return comptime System.Compiler.TypeIntegerBitWidth<T>();
            }

            finite law u64[0 max] Run()
            {
                return comptime System.Compiler.TypeIntegerBitWidth<i32[-7 10]>()
                    + comptime System.Compiler.TypeFloatBitWidth<f64>()
                    + comptime IntegerWidth<u16[0 max]>();
            }

            finite law bool Signed()
            {
                return comptime System.Compiler.TypeIntegerIsSigned<i32[-7 10]>();
            }

            finite law bool Unsigned()
            {
                return comptime System.Compiler.TypeIntegerIsUnsigned<u16[0 max]>();
            }

            finite law bool FullRange()
            {
                return comptime System.Compiler.TypeIntegerIsFullRange<i8[min max]>()
                    && comptime !System.Compiler.TypeIntegerIsFullRange<i8[-1 1]>()
                    && comptime System.Compiler.TypeIntegerIsFullRange<u16[0 max]>();
            }

            finite law bool RangeMinAt<T, comptime i1024[min max] Value>()
            {
                return comptime System.Compiler.TypeIntegerMinIs<T, comptime Value>();
            }

            finite law bool RangeMaxAt<T, comptime i1024[min max] Value>()
            {
                return comptime System.Compiler.TypeIntegerMaxIs<T, comptime Value>();
            }

            finite law bool RangeBounds()
            {
                return comptime System.Compiler.TypeIntegerMinIs<i32[-7 10], -7>()
                    && comptime System.Compiler.TypeIntegerMaxIs<i32[-7 10], 10>()
                    && comptime !System.Compiler.TypeIntegerMaxIs<i32[-7 10], 11>()
                    && comptime System.Compiler.TypeIntegerMinIs<u32[3 9], 3>()
                    && comptime RangeMinAt<i32[-7 10], -7>()
                    && comptime RangeMaxAt<i32[-7 10], 10>();
            }
            """,
            new CompilerOptions(OptimizationLevel: CompilerOptimizationLevel.O0));

        var runBody = ExtractDefinitionBody(llvm, "Run");
        Assert.Contains("ret i64 112", runBody);
        Assert.DoesNotContain("call", runBody);

        var signedBody = ExtractDefinitionBody(llvm, "Signed");
        Assert.Contains("ret i1 true", signedBody);
        Assert.DoesNotContain("call", signedBody);

        var unsignedBody = ExtractDefinitionBody(llvm, "Unsigned");
        Assert.Contains("ret i1 true", unsignedBody);
        Assert.DoesNotContain("call", unsignedBody);

        var fullRangeBody = ExtractDefinitionBody(llvm, "FullRange");
        Assert.Contains("ret i1 true", fullRangeBody);
        Assert.DoesNotContain("call", fullRangeBody);

        var rangeBoundsBody = ExtractDefinitionBody(llvm, "RangeBounds");
        Assert.Contains("ret i1 true", rangeBoundsBody);
        Assert.DoesNotContain("call", rangeBoundsBody);
    }

    [Fact]
    public void ComptimeScalarTypeMetadataFactsRejectWrongTargets()
    {
        var integerResult = Compile(
            """
            module Demo

            finite law u64[0 max] Run()
            {
                return comptime System.Compiler.TypeIntegerBitWidth<f32>();
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(integerResult.Succeeded);
        Assert.Contains(integerResult.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK3054"
            && diagnostic.Message.Contains("requires an integer type", StringComparison.Ordinal));

        var floatResult = Compile(
            """
            module Demo

            finite law u64[0 max] Run()
            {
                return comptime System.Compiler.TypeFloatBitWidth<i32[min max]>();
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(floatResult.Succeeded);
        Assert.Contains(floatResult.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK3054"
            && diagnostic.Message.Contains("requires a float type", StringComparison.Ordinal));

        var genericResult = Compile(
            """
            module Demo

            finite law u64[0 max] IntegerWidth<T>()
            {
                return comptime System.Compiler.TypeIntegerBitWidth<T>();
            }

            finite law u64[0 max] Run()
            {
                return comptime IntegerWidth<f32>();
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(genericResult.Succeeded);
        Assert.Contains(genericResult.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK3053"
            && diagnostic.Message.Contains("requires an integer type", StringComparison.Ordinal));
    }

    [Fact]
    public void ComptimeScalarTypeMetadataFactsAreRejectedAtRuntime()
    {
        var result = Compile(
            """
            module Demo

            finite law u64[0 max] Run()
            {
                return System.Compiler.TypeIntegerBitWidth<i32[min max]>();
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK3054"
            && diagnostic.Message.Contains("may only be used inside a `comptime` expression or block", StringComparison.Ordinal));

    }

    [Fact]
    public void ComptimeRawPointerMetadataFactsFoldToConstants()
    {
        var llvm = CompileToLlvm(
            """
            module Demo

            record Cell(i32[min max] Value)
            {
            }

            alias ReadCell = rawptr<Cell>;
            alias WriteNumber = rawmutptr<i32[min max]>;

            finite law bool Run()
            {
                return comptime System.Compiler.RawPointerElementTypeIs<ReadCell, Cell>()
                    && comptime System.Compiler.RawPointerElementTypeIsRecord<ReadCell>()
                    && comptime System.Compiler.RawPointerElementTypeHasConcreteLayout<ReadCell>()
                    && comptime System.Compiler.RawPointerIsReadOnly<ReadCell>()
                    && comptime !System.Compiler.RawPointerIsMutable<ReadCell>()
                    && comptime System.Compiler.RawPointerElementTypeIsInteger<WriteNumber>()
                    && comptime System.Compiler.RawPointerIsMutable<WriteNumber>()
                    && comptime !System.Compiler.RawPointerIsReadOnly<WriteNumber>();
            }
            """,
            new CompilerOptions(OptimizationLevel: CompilerOptimizationLevel.O0));

        var runBody = ExtractDefinitionBody(llvm, "Run");
        Assert.Contains("ret i1 true", runBody);
        Assert.DoesNotContain("call", runBody);
    }

    [Fact]
    public void ComptimeRawPointerMetadataFactsRejectWrongTargets()
    {
        var directResult = Compile(
            """
            module Demo

            finite law bool Run()
            {
                return comptime System.Compiler.RawPointerIsMutable<i32[min max]>();
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(directResult.Succeeded);
        Assert.Contains(directResult.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK3054"
            && diagnostic.Message.Contains("requires a raw pointer type", StringComparison.Ordinal));

        var genericResult = Compile(
            """
            module Demo

            finite law bool PointerIsMutable<T>()
            {
                return comptime System.Compiler.RawPointerIsMutable<T>();
            }

            finite law bool Run()
            {
                return comptime PointerIsMutable<i32[min max]>();
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(genericResult.Succeeded);
        Assert.Contains(genericResult.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK3053"
            && diagnostic.Message.Contains("requires a raw pointer type", StringComparison.Ordinal));
    }

    [Fact]
    public void ComptimeRawPointerMetadataFactsAreRejectedAtRuntime()
    {
        var result = Compile(
            """
            module Demo

            finite law bool Run()
            {
                return System.Compiler.RawPointerIsMutable<rawmutptr<i32[min max]>>();
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK3054"
            && diagnostic.Message.Contains("may only be used inside a `comptime` expression or block", StringComparison.Ordinal));
    }

    [Fact]
    public void ComptimeTypeElementMetadataFactsFoldToConstants()
    {
        var llvm = CompileToLlvm(
            """
            module Demo

            record Cell(i32[min max] Value)
            {
            }

            alias CellArray = Cell[4];
            alias CellSlice = Cell[];
            alias NumberStore = dynamic i32[min max];
            alias ReadNumber = rawptr<i32[min max]>;

            finite law u64[0 max] ArrayLength<T, comptime u8[1 8] N>()
            {
                return comptime System.Compiler.TypeFixedArrayLength<T[N]>();
            }

            finite law bool ArrayLengthIs<T, comptime u8[1 8] N>()
            {
                return comptime System.Compiler.TypeFixedArrayLengthIs<T[N], comptime N>();
            }

            finite law u64[0 max] Run()
            {
                if (comptime System.Compiler.TypeElementTypeIs<CellArray, Cell>())
                {
                    if (comptime System.Compiler.TypeElementTypeIsRecord<CellArray>())
                    {
                        if (comptime System.Compiler.TypeElementTypeHasConcreteLayout<CellArray>())
                        {
                            if (comptime System.Compiler.TypeFixedArrayLengthIs<CellArray, 4>())
                            {
                                if (comptime System.Compiler.TypeElementTypeIsRecord<CellSlice>())
                                {
                                    if (comptime System.Compiler.TypeElementTypeIsInteger<NumberStore>())
                                    {
                                        if (comptime System.Compiler.TypeElementTypeIsInteger<ReadNumber>())
                                        {
                                            if (comptime ArrayLengthIs<i32[min max], 5>())
                                            {
                                                return comptime System.Compiler.TypeFixedArrayLength<CellArray>()
                                                    + comptime ArrayLength<i32[min max], 5>();
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }

                return 0;
            }
            """,
            new CompilerOptions(OptimizationLevel: CompilerOptimizationLevel.O0));

        var runBody = ExtractDefinitionBody(llvm, "Run");
        Assert.Contains("ret i64 9", runBody);
        Assert.DoesNotContain("call", runBody);
    }

    [Fact]
    public void ComptimeTypeElementMetadataFactsRejectWrongTargets()
    {
        var elementResult = Compile(
            """
            module Demo

            finite law bool Run()
            {
                return comptime System.Compiler.TypeElementTypeIsInteger<i32[min max]>();
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(elementResult.Succeeded);
        Assert.Contains(elementResult.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK3054"
            && diagnostic.Message.Contains("requires an element-bearing type", StringComparison.Ordinal));

        var lengthResult = Compile(
            """
            module Demo

            finite law u64[0 max] Run()
            {
                return comptime System.Compiler.TypeFixedArrayLength<i32[min max]>();
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(lengthResult.Succeeded);
        Assert.Contains(lengthResult.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK3054"
            && diagnostic.Message.Contains("requires a fixed-array type", StringComparison.Ordinal));

        var genericResult = Compile(
            """
            module Demo

            finite law u64[0 max] Length<T>()
            {
                return comptime System.Compiler.TypeFixedArrayLength<T>();
            }

            finite law u64[0 max] Run()
            {
                return comptime Length<i32[min max]>();
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(genericResult.Succeeded);
        Assert.Contains(genericResult.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK3053"
            && diagnostic.Message.Contains("requires a fixed-array type", StringComparison.Ordinal));
    }

    [Fact]
    public void ComptimeTypeElementMetadataFactsAreRejectedAtRuntime()
    {
        var result = Compile(
            """
            module Demo

            finite law u64[0 max] Run()
            {
                return System.Compiler.TypeFixedArrayLength<i32[min max][3]>();
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK3054"
            && diagnostic.Message.Contains("may only be used inside a `comptime` expression or block", StringComparison.Ordinal));
    }

    [Fact]
    public void ComptimeIndexedFieldLayoutFactsFoldToConstants()
    {
        var llvm = CompileToLlvm(
            """
            module Demo

            [StructLayout(C), Pack(1)]
            struct Packet
            {
                u8[0 max] Tag;
                u32[0 max] Length;
            }

            finite law u64[0 max] FieldOffsetAt<T, comptime u64[0 max] I>()
            {
                return comptime System.Compiler.FieldOffset<T, comptime I>();
            }

            finite law u64[0 max] Run()
            {
                return comptime System.Compiler.FieldOffset<Packet, 1>()
                    + comptime System.Compiler.FieldSize<Packet, 1>()
                    + comptime System.Compiler.FieldAlign<Packet, 1>()
                    + comptime FieldOffsetAt<Packet, 1>();
            }

            finite law bool Misaligned()
            {
                return comptime System.Compiler.FieldIsMisaligned<Packet, 1>();
            }
            """,
            new CompilerOptions(OptimizationLevel: CompilerOptimizationLevel.O0));

        var runBody = ExtractDefinitionBody(llvm, "Run");
        Assert.Contains("ret i64 7", runBody);
        Assert.DoesNotContain("call", runBody);

        var misalignedBody = ExtractDefinitionBody(llvm, "Misaligned");
        Assert.Contains("ret i1 true", misalignedBody);
        Assert.DoesNotContain("call", misalignedBody);
    }

    [Fact]
    public void ComptimeEnumTagFactsFoldToConstants()
    {
        var llvm = CompileToLlvm(
            """
            module Demo

            enum Packet
            {
                Data(u8[0 max]),
                Empty,
                Other(u32[0 max])
            }

            finite law u64[0 max] VariantTagAt<T, comptime u64[0 max] I>()
            {
                return comptime System.Compiler.EnumVariantTag<T, comptime I>();
            }

            finite law u64[0 max] Run()
            {
                return comptime VariantTagAt<Packet, 0>()
                    + comptime System.Compiler.EnumVariantTag<Packet, 1>()
                    + comptime System.Compiler.EnumVariantTag<Packet, 2>()
                    + comptime System.Compiler.EnumTagOffset<Packet>()
                    + comptime System.Compiler.EnumTagSize<Packet>()
                    + comptime System.Compiler.EnumTagAlign<Packet>();
            }

            finite law bool TagMisaligned()
            {
                return comptime System.Compiler.EnumTagIsMisaligned<Packet>();
            }
            """,
            new CompilerOptions(OptimizationLevel: CompilerOptimizationLevel.O0));

        var runBody = ExtractDefinitionBody(llvm, "Run");
        Assert.Contains("ret i64 5", runBody);
        Assert.DoesNotContain("call", runBody);

        var misalignedBody = ExtractDefinitionBody(llvm, "TagMisaligned");
        Assert.Contains("ret i1 false", misalignedBody);
        Assert.DoesNotContain("call", misalignedBody);
    }

    [Fact]
    public void ComptimeEnumPayloadLayoutFactsFoldToConstants()
    {
        var llvm = CompileToLlvm(
            """
            module Demo

            enum Packet
            {
                Empty,
                Data(u8[0 max], u32[0 max])
            }

            finite law u64[0 max] PayloadOffsetAt<T, comptime u64[0 max] V, comptime u64[0 max] P>()
            {
                return comptime System.Compiler.EnumVariantPayloadOffset<T, comptime V, comptime P>();
            }

            finite law u64[0 max] Run()
            {
                return comptime PayloadOffsetAt<Packet, 1, 0>()
                    + comptime System.Compiler.EnumVariantPayloadSize<Packet, 1, 0>()
                    + comptime System.Compiler.EnumVariantPayloadAlign<Packet, 1, 0>()
                    + comptime System.Compiler.EnumVariantPayloadOffset<Packet, 1, 1>()
                    + comptime System.Compiler.EnumVariantPayloadSize<Packet, 1, 1>()
                    + comptime System.Compiler.EnumVariantPayloadAlign<Packet, 1, 1>();
            }

            finite law bool Misaligned()
            {
                if (comptime System.Compiler.EnumVariantPayloadIsMisaligned<Packet, 1, 0>())
                {
                    return true;
                }

                return comptime System.Compiler.EnumVariantPayloadIsMisaligned<Packet, 1, 1>();
            }
            """,
            new CompilerOptions(OptimizationLevel: CompilerOptimizationLevel.O0));

        var runBody = ExtractDefinitionBody(llvm, "Run");
        Assert.Contains("ret i64 15", runBody);
        Assert.DoesNotContain("call", runBody);

        var misalignedBody = ExtractDefinitionBody(llvm, "Misaligned");
        Assert.Contains("ret i1 false", misalignedBody);
        Assert.DoesNotContain("call", misalignedBody);
    }

    [Fact]
    public void ComptimeStructLayoutAttributeFactsFoldToConstants()
    {
        var llvm = CompileToLlvm(
            """
            module Demo

            [StructLayout(C), Pack(1), Align(8)]
            struct Packet
            {
                u8[0 max] Tag;
                u32[0 max] Length;
            }

            [StructLayout(Explicit), Align(4)]
            struct WordParts
            {
                [FieldOffset(0)] u32[0 max] Whole;
                [FieldOffset(0)] u16[0 max] Low;
                [FieldOffset(2)] u16[0 max] High;
            }

            struct AutoPacket
            {
                u8[0 max] Tag;
            }

            finite law u64[0 max] LayoutScore<PacketType, WordPartsType, AutoPacketType>()
            {
                if (comptime System.Compiler.StructLayoutIsC<PacketType>())
                {
                    if (comptime !System.Compiler.StructLayoutIsExplicit<PacketType>())
                    {
                        if (comptime System.Compiler.StructLayoutIsExplicit<WordPartsType>())
                        {
                            if (comptime System.Compiler.StructLayoutIsAuto<AutoPacketType>())
                            {
                                if (comptime System.Compiler.StructHasPack<PacketType>())
                                {
                                    if (comptime System.Compiler.StructHasAlign<PacketType>())
                                    {
                                        if (comptime !System.Compiler.StructHasPack<WordPartsType>())
                                        {
                                            if (comptime System.Compiler.FieldHasExplicitOffset<WordPartsType, 2>())
                                            {
                                                if (comptime !System.Compiler.FieldHasExplicitOffset<PacketType, 0>())
                                                {
                                                    return comptime System.Compiler.StructPack<PacketType>()
                                                        + comptime System.Compiler.StructAlign<PacketType>()
                                                        + comptime System.Compiler.FieldExplicitOffset<WordPartsType, 2>();
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }

                return 0;
            }

            finite law u64[0 max] Run()
            {
                return comptime LayoutScore<Packet, WordParts, AutoPacket>();
            }
            """,
            new CompilerOptions(OptimizationLevel: CompilerOptimizationLevel.O0));

        var runBody = ExtractDefinitionBody(llvm, "Run");
        Assert.Contains("ret i64 11", runBody);
        Assert.DoesNotContain("call", runBody);
    }

    [Fact]
    public void ComptimeThreadSafetyLawAttributeFactsFoldToConstants()
    {
        var llvm = CompileToLlvm(
            """
            module Demo

            struct RawHolder
            {
                [Grant(Transferable)]
                rawptr<i32[min max]> Pointer;
            }

            struct Box<T, comptime u8[1 8] N>
            {
                T Value;
            }

            [Grant(Shareable) where Transferable(Box<T, 4>)]
            struct Shared<T>
            {
                T Value;
            }

            struct Sync<T>
            {
                [Grant(Shareable) where Transferable(Box<T, 4>)]
                T Payload;

                [Deny(Transferable)]
                i32[min max] LocalOnly;
            }

            [Grant(Transferable)]
            [Deny(Shareable)]
            struct Token
            {
                i32[min max] Value;
            }

            finite law bool TypeConditionAt<T, U, comptime u64[0 max] AttributeIndex>()
            {
                return comptime System.Compiler.TypeThreadSafetyLawAttributeConditionTypeIs<T, U, comptime AttributeIndex>();
            }

            finite law bool FieldConditionAt<T, U, comptime u64[0 max] FieldIndex, comptime u64[0 max] AttributeIndex>()
            {
                return comptime System.Compiler.FieldThreadSafetyLawAttributeConditionTypeIs<T, U, comptime FieldIndex, comptime AttributeIndex>();
            }

            finite law i32[min max] Run()
            {
                if (comptime System.Compiler.TypeThreadSafetyLawAttributeCount<Token>() == 2)
                {
                    if (comptime (System.Compiler.TypeThreadSafetyLawAttributeLawName<Token, 0>() == "Transferable"))
                    {
                        if (comptime System.Compiler.TypeThreadSafetyLawAttributeIsGrant<Token, 0>())
                        {
                            if (comptime System.Compiler.TypeThreadSafetyLawAttributeIsDeny<Token, 1>())
                            {
                                if (comptime !System.Compiler.TypeThreadSafetyLawAttributeHasCondition<Token, 0>())
                                {
                                    if (comptime (System.Compiler.TypeThreadSafetyLawAttributeConditionLawName<Token, 0>() == ""))
                                    {
                                        if (comptime System.Compiler.TypeThreadSafetyLawAttributeCount<Shared<RawHolder>>() == 1)
                                        {
                                            if (comptime (System.Compiler.TypeThreadSafetyLawAttributeLawName<Shared<RawHolder>, 0>() == "Shareable")
                                                && comptime System.Compiler.TypeThreadSafetyLawAttributeIsGrant<Shared<RawHolder>, 0>()
                                                && comptime System.Compiler.TypeThreadSafetyLawAttributeHasCondition<Shared<RawHolder>, 0>())
                                            {
                                                if (comptime (System.Compiler.TypeThreadSafetyLawAttributeConditionLawName<Shared<RawHolder>, 0>() == "Transferable")
                                                    && comptime System.Compiler.TypeThreadSafetyLawAttributeConditionTypeIs<Shared<RawHolder>, Box<RawHolder, 4>, 0>()
                                                    && comptime TypeConditionAt<Shared<RawHolder>, Box<RawHolder, 4>, 0>())
                                                {
                                                    if (comptime (System.Compiler.TypeThreadSafetyLawAttributeConditionTypeDisplayName<Shared<RawHolder>, 0>() == "Box<RawHolder, 4>")
                                                        && comptime (System.Compiler.TypeThreadSafetyLawAttributeConditionTypeBaseName<Shared<RawHolder>, 0>() == "Box")
                                                        && comptime (System.Compiler.TypeThreadSafetyLawAttributeConditionTypeModuleName<Shared<RawHolder>, 0>() == "Demo")
                                                        && comptime System.Compiler.TypeThreadSafetyLawAttributeConditionTypeIsNamed<Shared<RawHolder>, 0>()
                                                        && comptime System.Compiler.TypeThreadSafetyLawAttributeConditionTypeIsStruct<Shared<RawHolder>, 0>()
                                                        && comptime System.Compiler.TypeThreadSafetyLawAttributeConditionTypeHasConcreteLayout<Shared<RawHolder>, 0>()
                                                        && comptime System.Compiler.TypeThreadSafetyLawAttributeConditionTypeIsGenericInstantiation<Shared<RawHolder>, 0>()
                                                        && comptime System.Compiler.TypeThreadSafetyLawAttributeConditionTypeArgumentCount<Shared<RawHolder>, 0>() == 1
                                                        && comptime System.Compiler.TypeThreadSafetyLawAttributeConditionTypeComptimeArgumentCount<Shared<RawHolder>, 0>() == 1)
                                                    {
                                                        if (comptime System.Compiler.FieldThreadSafetyLawAttributeCount<Sync<RawHolder>, 0>() == 1)
                                                        {
                                                            if (comptime (System.Compiler.FieldThreadSafetyLawAttributeLawName<Sync<RawHolder>, 0, 0>() == "Shareable"))
                                                            {
                                                                if (comptime System.Compiler.FieldThreadSafetyLawAttributeIsGrant<Sync<RawHolder>, 0, 0>())
                                                                {
                                                                    if (comptime System.Compiler.FieldThreadSafetyLawAttributeHasCondition<Sync<RawHolder>, 0, 0>())
                                                                    {
                                                                        if (comptime (System.Compiler.FieldThreadSafetyLawAttributeConditionLawName<Sync<RawHolder>, 0, 0>() == "Transferable"))
                                                                        {
                                                                            if (comptime System.Compiler.FieldThreadSafetyLawAttributeConditionTypeIs<Sync<RawHolder>, Box<RawHolder, 4>, 0, 0>())
                                                                            {
                                                                                if (comptime FieldConditionAt<Sync<RawHolder>, Box<RawHolder, 4>, 0, 0>())
                                                                                {
                                                                                    if (comptime (System.Compiler.FieldThreadSafetyLawAttributeConditionTypeDisplayName<Sync<RawHolder>, 0, 0>() == "Box<RawHolder, 4>")
                                                                                        && comptime (System.Compiler.FieldThreadSafetyLawAttributeConditionTypeBaseName<Sync<RawHolder>, 0, 0>() == "Box")
                                                                                        && comptime (System.Compiler.FieldThreadSafetyLawAttributeConditionTypeModuleName<Sync<RawHolder>, 0, 0>() == "Demo")
                                                                                        && comptime System.Compiler.FieldThreadSafetyLawAttributeConditionTypeIsNamed<Sync<RawHolder>, 0, 0>()
                                                                                        && comptime System.Compiler.FieldThreadSafetyLawAttributeConditionTypeIsStruct<Sync<RawHolder>, 0, 0>()
                                                                                        && comptime System.Compiler.FieldThreadSafetyLawAttributeConditionTypeHasConcreteLayout<Sync<RawHolder>, 0, 0>()
                                                                                        && comptime System.Compiler.FieldThreadSafetyLawAttributeConditionTypeIsGenericInstantiation<Sync<RawHolder>, 0, 0>()
                                                                                        && comptime System.Compiler.FieldThreadSafetyLawAttributeConditionTypeArgumentCount<Sync<RawHolder>, 0, 0>() == 1
                                                                                        && comptime System.Compiler.FieldThreadSafetyLawAttributeConditionTypeComptimeArgumentCount<Sync<RawHolder>, 0, 0>() == 1)
                                                                                    {
                                                                                        if (comptime System.Compiler.FieldThreadSafetyLawAttributeIsDeny<Sync<RawHolder>, 1, 0>())
                                                                                        {
                                                                                            return 42;
                                                                                        }
                                                                                    }
                                                                                }
                                                                            }
                                                                        }
                                                                    }
                                                                }
                                                            }
                                                        }
                                                    }
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }

                return 0;
            }
            """,
            new CompilerOptions(OptimizationLevel: CompilerOptimizationLevel.O0));

        var runBody = ExtractDefinitionBody(llvm, "Run");
        Assert.Contains("ret i32 42", runBody);
        Assert.DoesNotContain("call", runBody);
    }

    [Fact]
    public void ComptimeMethodThreadSafetyLawPredicateFactsFoldToConstants()
    {
        var llvm = CompileToLlvm(
            """
            module Demo

            struct RawHolder
            {
                [Grant(Transferable)]
                rawptr<i32[min max]> Pointer;
            }

            struct Box<T, comptime u8[1 8] N>
            {
                T Value;
            }

            struct Gate<T>
            {
                fn void Move(T value) where Transferable(Box<T, 4>)
                {
                }
            }

            finite law bool PredicateAt<Owner, U, comptime u64[0 max] MethodIndex, comptime u64[0 max] PredicateIndex>()
            {
                return comptime System.Compiler.MethodThreadSafetyLawPredicateTypeIs<Owner, U, comptime MethodIndex, comptime PredicateIndex>();
            }

            finite law i32[min max] Run()
            {
                if (comptime System.Compiler.MethodThreadSafetyLawPredicateCount<Gate<RawHolder>, 0>() == 1)
                {
                    if (comptime (System.Compiler.MethodThreadSafetyLawPredicateLawName<Gate<RawHolder>, 0, 0>() == "Transferable"))
                    {
                        if (comptime System.Compiler.MethodThreadSafetyLawPredicateTypeIs<Gate<RawHolder>, Box<RawHolder, 4>, 0, 0>())
                        {
                            if (comptime PredicateAt<Gate<RawHolder>, Box<RawHolder, 4>, 0, 0>())
                            {
                                if (comptime (System.Compiler.MethodThreadSafetyLawPredicateTypeDisplayName<Gate<RawHolder>, 0, 0>() == "Box<RawHolder, 4>"))
                                {
                                    if (comptime (System.Compiler.MethodThreadSafetyLawPredicateTypeBaseName<Gate<RawHolder>, 0, 0>() == "Box"))
                                    {
                                        if (comptime (System.Compiler.MethodThreadSafetyLawPredicateTypeModuleName<Gate<RawHolder>, 0, 0>() == "Demo")
                                            && comptime System.Compiler.MethodThreadSafetyLawPredicateTypeIsNamed<Gate<RawHolder>, 0, 0>()
                                            && comptime System.Compiler.MethodThreadSafetyLawPredicateTypeIsStruct<Gate<RawHolder>, 0, 0>()
                                            && comptime System.Compiler.MethodThreadSafetyLawPredicateTypeHasConcreteLayout<Gate<RawHolder>, 0, 0>()
                                            && comptime System.Compiler.MethodThreadSafetyLawPredicateTypeIsGenericInstantiation<Gate<RawHolder>, 0, 0>())
                                        {
                                            if (comptime System.Compiler.MethodThreadSafetyLawPredicateTypeArgumentCount<Gate<RawHolder>, 0, 0>() == 1)
                                            {
                                                if (comptime System.Compiler.MethodThreadSafetyLawPredicateTypeComptimeArgumentCount<Gate<RawHolder>, 0, 0>() == 1)
                                                {
                                                    return 42;
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }

                return 0;
            }
            """,
            new CompilerOptions(OptimizationLevel: CompilerOptimizationLevel.O0));

        var runBody = ExtractDefinitionBody(llvm, "Run");
        Assert.Contains("ret i32 42", runBody);
        Assert.DoesNotContain("call", runBody);
    }

    [Fact]
    public void ComptimeIndexedEnumVariantFactsFoldToConstants()
    {
        var llvm = CompileToLlvm(
            """
            module Demo

            enum Outcome<T>
            {
                [Ok] Got(T),
                [Err] Failed
            }

            finite law i64[min max] Run()
            {
                if (comptime System.Compiler.EnumVariantIsOk<Outcome<i32[min max]>, 0>())
                {
                    if (comptime System.Compiler.EnumVariantIsErr<Outcome<i32[min max]>, 1>())
                    {
                        return comptime System.Compiler.EnumVariantPayloadCount<Outcome<i32[min max]>, 0>()
                            + comptime System.Compiler.EnumVariantPayloadCount<Outcome<i32[min max]>, 1>()
                            + 30;
                    }
                }

                return 0;
            }
            """,
            new CompilerOptions(OptimizationLevel: CompilerOptimizationLevel.O0));

        var runBody = ExtractDefinitionBody(llvm, "Run");
        Assert.Contains("ret i64 31", runBody);
        Assert.DoesNotContain("call", runBody);
    }

    [Fact]
    public void ComptimeIndexedTypeFactsFoldToBoolConstants()
    {
        var llvm = CompileToLlvm(
            """
            module Demo

            struct Header
            {
                i32[min max] Tag;
                bool Active;
            }

            struct Box<T>
            {
                T Value;
            }

            enum Token
            {
                Empty,
                Pair(i32[min max], bool)
            }

            enum Envelope<T>
            {
                Item(T)
            }

            finite law bool FieldAt<T, U, comptime u64[0 max] I>()
            {
                return comptime System.Compiler.FieldTypeIs<T, U, comptime I>();
            }

            finite law bool PayloadAt<T, U, comptime u64[0 max] V, comptime u64[0 max] P>()
            {
                return comptime System.Compiler.EnumVariantPayloadTypeIs<T, U, comptime V, comptime P>();
            }

            finite law i32[min max] Run()
            {
                if (comptime System.Compiler.FieldTypeIs<Header, i32[min max], 0>())
                {
                    if (comptime FieldAt<Header, bool, 1>())
                    {
                        if (comptime System.Compiler.EnumVariantPayloadTypeIs<Token, i32[min max], 1, 0>())
                        {
                            if (comptime PayloadAt<Token, bool, 1, 1>())
                            {
                                if (comptime System.Compiler.FieldTypeIs<Box<i32[min max]>, i32[min max], 0>())
                                {
                                    if (comptime System.Compiler.EnumVariantPayloadTypeIs<Envelope<bool>, bool, 0, 0>())
                                    {
                                        if (comptime !System.Compiler.FieldTypeIs<Header, bool, 0>())
                                        {
                                            return 42;
                                        }
                                    }
                                }
                            }
                        }
                    }
                }

                return 0;
            }
            """,
            new CompilerOptions(OptimizationLevel: CompilerOptimizationLevel.O0));

        var runBody = ExtractDefinitionBody(llvm, "Run");
        Assert.Contains("ret i32 42", runBody);
        Assert.DoesNotContain("call", runBody);
    }

    [Fact]
    public void ComptimeFieldAndPayloadTypePredicateFactsFoldToConstants()
    {
        var llvm = CompileToLlvm(
            """
            module Demo

            record Point(i32[min max] X)
            {
            }

            enum Kind
            {
                One
            }

            struct Header
            {
                bool Active;
                i32[min max] Count;
                i32[min max][4] Values;
                Point Position;
                Kind Tag;
            }

            enum Token
            {
                Flag(bool),
                Number(i32[min max]),
                Values(i32[min max][4]),
                Position(Point),
                Tag(Kind)
            }

            finite law bool FieldIsNumber<T, comptime u64[0 max] I>()
            {
                return comptime System.Compiler.FieldTypeIsInteger<T, comptime I>();
            }

            finite law bool PayloadIsRecord<T, comptime u64[0 max] V, comptime u64[0 max] P>()
            {
                return comptime System.Compiler.EnumVariantPayloadTypeIsRecord<T, comptime V, comptime P>();
            }

            finite law i32[min max] Run()
            {
                if (comptime System.Compiler.FieldTypeIsBool<Header, 0>())
                {
                    if (comptime FieldIsNumber<Header, 1>())
                    {
                        if (comptime System.Compiler.FieldTypeIsFixedArray<Header, 2>())
                        {
                            if (comptime System.Compiler.FieldTypeIsNamed<Header, 3>())
                            {
                                if (comptime System.Compiler.FieldTypeIsRecord<Header, 3>())
                                {
                                    if (comptime System.Compiler.FieldTypeIsEnum<Header, 4>())
                                    {
                                        if (comptime System.Compiler.FieldTypeHasConcreteLayout<Header, 2>())
                                        {
                                            if (comptime !System.Compiler.FieldTypeIsInteger<Header, 0>())
                                            {
                                                if (comptime System.Compiler.EnumVariantPayloadTypeIsBool<Token, 0, 0>())
                                                {
                                                    if (comptime System.Compiler.EnumVariantPayloadTypeIsInteger<Token, 1, 0>())
                                                    {
                                                        if (comptime System.Compiler.EnumVariantPayloadTypeIsFixedArray<Token, 2, 0>())
                                                        {
                                                            if (comptime PayloadIsRecord<Token, 3, 0>())
                                                            {
                                                                if (comptime System.Compiler.EnumVariantPayloadTypeIsEnum<Token, 4, 0>())
                                                                {
                                                                    if (comptime System.Compiler.EnumVariantPayloadTypeHasConcreteLayout<Token, 4, 0>())
                                                                    {
                                                                        if (comptime !System.Compiler.EnumVariantPayloadTypeIsRecord<Token, 4, 0>())
                                                                        {
                                                                            return 42;
                                                                        }
                                                                    }
                                                                }
                                                            }
                                                        }
                                                    }
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }

                return 0;
            }
            """,
            new CompilerOptions(OptimizationLevel: CompilerOptimizationLevel.O0));

        var runBody = ExtractDefinitionBody(llvm, "Run");
        Assert.Contains("ret i32 42", runBody);
        Assert.DoesNotContain("call", runBody);
    }

    [Fact]
    public void ComptimeFieldAndPayloadTypeMetadataFactsFoldToConstants()
    {
        var llvm = CompileToLlvm(
            """
            module Demo

            struct Box<T, comptime u8[1 8] N>
            {
                T[N] Items;
            }

            struct Header
            {
                Box<i32[min max], 4> Value;
                bool Active;
            }

            enum Packet<T, comptime u8[1 8] N>
            {
                Item(Box<T, comptime N>),
                Flag(bool)
            }

            finite law i32[min max] Run()
            {
                if (comptime (System.Compiler.FieldTypeDisplayName<Header, 0>() == "Box<i32, 4>"))
                {
                    if (comptime (System.Compiler.FieldTypeBaseName<Header, 0>() == "Box"))
                    {
                        if (comptime System.Compiler.FieldTypeIsGenericInstantiation<Header, 0>())
                        {
                            if (comptime System.Compiler.FieldTypeArgumentCount<Header, 0>() == 1)
                            {
                                if (comptime System.Compiler.FieldTypeComptimeArgumentCount<Header, 0>() == 1)
                                {
                                    if (comptime (System.Compiler.FieldTypeBaseName<Header, 1>() == ""))
                                    {
                                        if (comptime !System.Compiler.FieldTypeIsGenericInstantiation<Header, 1>())
                                        {
                                            if (comptime (System.Compiler.EnumVariantPayloadTypeDisplayName<Packet<i32[min max], 4>, 0, 0>() == "Box<i32, 4>"))
                                            {
                                                if (comptime (System.Compiler.EnumVariantPayloadTypeBaseName<Packet<i32[min max], 4>, 0, 0>() == "Box"))
                                                {
                                                    if (comptime System.Compiler.EnumVariantPayloadTypeIsGenericInstantiation<Packet<i32[min max], 4>, 0, 0>())
                                                    {
                                                        if (comptime System.Compiler.EnumVariantPayloadTypeArgumentCount<Packet<i32[min max], 4>, 0, 0>() == 1)
                                                        {
                                                            if (comptime System.Compiler.EnumVariantPayloadTypeComptimeArgumentCount<Packet<i32[min max], 4>, 0, 0>() == 1)
                                                            {
                                                                if (comptime (System.Compiler.EnumVariantPayloadTypeBaseName<Packet<i32[min max], 4>, 1, 0>() == ""))
                                                                {
                                                                    if (comptime !System.Compiler.EnumVariantPayloadTypeIsGenericInstantiation<Packet<i32[min max], 4>, 1, 0>())
                                                                    {
                                                                        return 42;
                                                                    }
                                                                }
                                                            }
                                                        }
                                                    }
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }

                return 0;
            }
            """,
            new CompilerOptions(OptimizationLevel: CompilerOptimizationLevel.O0));

        var runBody = ExtractDefinitionBody(llvm, "Run");
        Assert.Contains("ret i32 42", runBody);
        Assert.DoesNotContain("call", runBody);
    }

    [Fact]
    public void ComptimeFunctionPointerStructuralFactsFoldToConstants()
    {
        var llvm = CompileToLlvm(
            """
            module Demo

            record Point(i32[min max] X)
            {
            }

            struct Holder<T, comptime u8[1 8] N>
            {
                T Value;
            }

            enum Kind
            {
                One
            }

            alias Callback = fnptr<ffi(c) finite law Point(i32[min max], rawptr<i8[min max]>, Kind)>;
            alias UnsafeCallback = fnptr<unsafe ffi(c) fn void()>;
            alias GenericCallback = fnptr<fn Holder<i32[min max], 4>(Holder<bool, 2>, rawptr<i8[min max]>)>;
            alias Plain = fnptr<law bool()>;

            finite law bool ParameterIsNumber<T, comptime u64[0 max] I>()
            {
                return comptime System.Compiler.FunctionPointerParameterTypeIsInteger<T, comptime I>();
            }

            finite law i32[min max] Run()
            {
                if (!(comptime System.Compiler.FunctionPointerIsUnsafe<UnsafeCallback>()
                    && comptime !System.Compiler.FunctionPointerIsUnsafe<Plain>()))
                {
                    return 0;
                }

                if (comptime System.Compiler.FunctionPointerParameterCount<Callback>() == 3)
                {
                    if (comptime System.Compiler.FunctionPointerReturnTypeIs<Callback, Point>())
                    {
                        if (comptime System.Compiler.FunctionPointerReturnTypeIsRecord<Callback>())
                        {
                            if (comptime System.Compiler.FunctionPointerReturnTypeHasConcreteLayout<Callback>())
                            {
                                if (comptime System.Compiler.FunctionPointerParameterTypeIs<Callback, i32[min max], 0>())
                                {
                                    if (comptime ParameterIsNumber<Callback, 0>())
                                    {
                                        if (comptime System.Compiler.FunctionPointerParameterTypeIsRawPointer<Callback, 1>())
                                        {
                                            if (comptime System.Compiler.FunctionPointerParameterTypeIsEnum<Callback, 2>())
                                            {
                                                if (comptime System.Compiler.FunctionPointerKindIsFiniteLaw<Callback>())
                                                {
                                                    if (comptime !System.Compiler.FunctionPointerKindIsFinite<Callback>())
                                                    {
                                                        if (comptime !System.Compiler.FunctionPointerKindIsLaw<Callback>())
                                                        {
                                                            if (comptime System.Compiler.FunctionPointerHasFfiAbi<Callback>())
                                                            {
                                                                if (comptime System.Compiler.FunctionPointerAbiIsC<Callback>())
                                                                {
                                                                    if (comptime !System.Compiler.FunctionPointerAbiIsSysV<Callback>())
                                                                    {
                                                                        if (comptime System.Compiler.FunctionPointerParameterCount<Plain>() == 0)
                                                                        {
                                                                            if (comptime System.Compiler.FunctionPointerReturnTypeIsBool<Plain>())
                                                                            {
                                                                                if (comptime System.Compiler.FunctionPointerKindIsLaw<Plain>())
                                                                                {
                                                                                    if (comptime !System.Compiler.FunctionPointerHasFfiAbi<Plain>())
                                                                                    {
                                                                                        if (comptime System.Compiler.FunctionPointerParameterCount<i32[min max]>() == 0)
                                                                                        {
                                                                                            if (comptime (System.Compiler.FunctionPointerReturnTypeDisplayName<GenericCallback>() == "Holder<i32, 4>") && comptime (System.Compiler.FunctionPointerReturnTypeBaseName<GenericCallback>() == "Holder"))
                                                                                            {
                                                                                                if (comptime System.Compiler.FunctionPointerReturnTypeIsGenericInstantiation<GenericCallback>() && comptime System.Compiler.FunctionPointerReturnTypeArgumentCount<GenericCallback>() == 1 && comptime System.Compiler.FunctionPointerReturnTypeComptimeArgumentCount<GenericCallback>() == 1)
                                                                                                {
                                                                                                    if (comptime (System.Compiler.FunctionPointerParameterTypeDisplayName<GenericCallback, 0>() == "Holder<bool, 2>") && comptime (System.Compiler.FunctionPointerParameterTypeBaseName<GenericCallback, 0>() == "Holder"))
                                                                                                    {
                                                                                                        if (comptime System.Compiler.FunctionPointerParameterTypeIsGenericInstantiation<GenericCallback, 0>() && comptime System.Compiler.FunctionPointerParameterTypeArgumentCount<GenericCallback, 0>() == 1 && comptime System.Compiler.FunctionPointerParameterTypeComptimeArgumentCount<GenericCallback, 0>() == 1)
                                                                                                        {
                                                                                                            if (comptime (System.Compiler.FunctionPointerParameterTypeBaseName<GenericCallback, 1>() == ""))
                                                                                                            {
                                                                                                                return 42;
                                                                                                            }
                                                                                                        }
                                                                                                    }
                                                                                                }
                                                                                            }
                                                                                        }
                                                                                    }
                                                                                }
                                                                            }
                                                                        }
                                                                    }
                                                                }
                                                            }
                                                        }
                                                    }
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }

                return 0;
            }
            """,
            new CompilerOptions(OptimizationLevel: CompilerOptimizationLevel.O0));

        var runBody = ExtractDefinitionBody(llvm, "Run");
        Assert.Contains("ret i32 42", runBody);
        Assert.DoesNotContain("call", runBody);
    }

    [Fact]
    public void ComptimeCallableNestedTypeArgumentStructuralFactsFoldToConstants()
    {
        var llvm = CompileToLlvm(
            """
            module Demo

            struct Inner<T, comptime u8[1 8] N>
            {
                T Value;
            }

            struct Holder<T, comptime u8[1 8] N>
            {
                T Value;
            }

            struct Box<T>
            {
                T Value;

                fn Holder<T, 4> Echo(borrow Box<T> self, Holder<T, 4> fallback)
                {
                    return fallback;
                }
            }

            alias Callback = fnptr<fn Holder<Inner<i32[min max], 3>, 4>(Holder<Inner<bool, 2>, 4>)>;
            alias Handler = borrow closure<fn Holder<Inner<i32[min max], 3>, 4>(Holder<Inner<bool, 2>, 4>)>;

            finite law bool FunctionPointerFacts()
            {
                return comptime System.Compiler.FunctionPointerReturnTypeArgumentTypeIs<Callback, Inner<i32[min max], 3>, 0>()
                    && comptime System.Compiler.FunctionPointerReturnTypeArgumentTypeIsStruct<Callback, 0>()
                    && comptime System.Compiler.FunctionPointerReturnTypeArgumentTypeHasConcreteLayout<Callback, 0>()
                    && comptime (System.Compiler.FunctionPointerReturnTypeArgumentTypeDisplayName<Callback, 0>() == "Inner<i32, 3>")
                    && comptime (System.Compiler.FunctionPointerReturnTypeArgumentTypeBaseName<Callback, 0>() == "Inner")
                    && comptime (System.Compiler.FunctionPointerReturnTypeArgumentTypeModuleName<Callback, 0>() == "Demo")
                    && comptime System.Compiler.FunctionPointerReturnTypeArgumentTypeIsGenericInstantiation<Callback, 0>()
                    && comptime System.Compiler.FunctionPointerReturnTypeArgumentTypeArgumentCount<Callback, 0>() == 1
                    && comptime System.Compiler.FunctionPointerReturnTypeArgumentTypeComptimeArgumentCount<Callback, 0>() == 1
                    && comptime System.Compiler.FunctionPointerParameterTypeArgumentTypeIs<Callback, Inner<bool, 2>, 0, 0>()
                    && comptime System.Compiler.FunctionPointerParameterTypeArgumentTypeIsStruct<Callback, 0, 0>()
                    && comptime (System.Compiler.FunctionPointerParameterTypeArgumentTypeBaseName<Callback, 0, 0>() == "Inner");
            }

            finite law bool ClosureFacts()
            {
                return comptime System.Compiler.ClosureReturnTypeArgumentTypeIs<Handler, Inner<i32[min max], 3>, 0>()
                    && comptime System.Compiler.ClosureReturnTypeArgumentTypeIsStruct<Handler, 0>()
                    && comptime System.Compiler.ClosureReturnTypeArgumentTypeHasConcreteLayout<Handler, 0>()
                    && comptime (System.Compiler.ClosureReturnTypeArgumentTypeDisplayName<Handler, 0>() == "Inner<i32, 3>")
                    && comptime (System.Compiler.ClosureReturnTypeArgumentTypeBaseName<Handler, 0>() == "Inner")
                    && comptime (System.Compiler.ClosureReturnTypeArgumentTypeModuleName<Handler, 0>() == "Demo")
                    && comptime System.Compiler.ClosureReturnTypeArgumentTypeIsGenericInstantiation<Handler, 0>()
                    && comptime System.Compiler.ClosureReturnTypeArgumentTypeArgumentCount<Handler, 0>() == 1
                    && comptime System.Compiler.ClosureReturnTypeArgumentTypeComptimeArgumentCount<Handler, 0>() == 1
                    && comptime System.Compiler.ClosureParameterTypeArgumentTypeIs<Handler, Inner<bool, 2>, 0, 0>()
                    && comptime System.Compiler.ClosureParameterTypeArgumentTypeIsStruct<Handler, 0, 0>()
                    && comptime (System.Compiler.ClosureParameterTypeArgumentTypeBaseName<Handler, 0, 0>() == "Inner");
            }

            finite law bool MethodFacts()
            {
                return comptime System.Compiler.MethodReturnTypeArgumentTypeIs<Box<i32[min max]>, i32[min max], 0, 0>()
                    && comptime System.Compiler.MethodReturnTypeArgumentTypeIsInteger<Box<i32[min max]>, 0, 0>()
                    && comptime System.Compiler.MethodReturnTypeArgumentTypeHasConcreteLayout<Box<i32[min max]>, 0, 0>()
                    && comptime (System.Compiler.MethodReturnTypeArgumentTypeDisplayName<Box<i32[min max]>, 0, 0>() == "i32")
                    && comptime (System.Compiler.MethodReturnTypeArgumentTypeBaseName<Box<i32[min max]>, 0, 0>() == "")
                    && comptime (System.Compiler.MethodReturnTypeArgumentTypeModuleName<Box<i32[min max]>, 0, 0>() == "")
                    && comptime !System.Compiler.MethodReturnTypeArgumentTypeIsGenericInstantiation<Box<i32[min max]>, 0, 0>()
                    && comptime System.Compiler.MethodReturnTypeArgumentTypeArgumentCount<Box<i32[min max]>, 0, 0>() == 0
                    && comptime System.Compiler.MethodReturnTypeArgumentTypeComptimeArgumentCount<Box<i32[min max]>, 0, 0>() == 0
                    && comptime System.Compiler.MethodParameterTypeArgumentTypeIs<Box<i32[min max]>, i32[min max], 0, 1, 0>()
                    && comptime System.Compiler.MethodParameterTypeArgumentTypeIsInteger<Box<i32[min max]>, 0, 1, 0>()
                    && comptime (System.Compiler.MethodParameterTypeArgumentTypeBaseName<Box<i32[min max]>, 0, 1, 0>() == "");
            }

            finite law i32[min max] Run()
            {
                if (comptime FunctionPointerFacts()
                    && comptime ClosureFacts()
                    && comptime MethodFacts())
                {
                    return 42;
                }

                return 0;
            }
            """,
            new CompilerOptions(OptimizationLevel: CompilerOptimizationLevel.O0));

        var runBody = ExtractDefinitionBody(llvm, "Run");
        Assert.Contains("ret i32 42", runBody);
        Assert.DoesNotContain("call", runBody);
    }

    [Fact]
    public void ComptimeFunctionPointerMemoryContractFactsFoldToConstants()
    {
        var llvm = CompileToLlvm(
            """
            module Demo

            alias DefaultSafe = fnptr<fn void(rawmutptr<i32[min max]>, rawmutptr<i32[min max]>)>;
            alias Overlapping = fnptr<fn void(rawmutptr<i32[min max]>, rawmutptr<i32[min max]>) where overlap(arg0, arg1)>;
            alias SameRegion = fnptr<fn void(rawmutptr<i32[min max]>, rawmutptr<i32[min max]>) where same(arg0, arg1)>;

            finite law bool ArgsAreDisjoint<T, comptime u64[0 max] L, comptime u64[0 max] R>()
            {
                return comptime System.Compiler.FunctionPointerParametersAreDisjoint<T, comptime L, comptime R>();
            }

            finite law i32[min max] Run()
            {
                if (comptime ArgsAreDisjoint<DefaultSafe, 0, 1>())
                {
                    if (comptime !System.Compiler.FunctionPointerParametersOverlap<DefaultSafe, 0, 1>())
                    {
                        if (comptime !System.Compiler.FunctionPointerParametersAreSame<DefaultSafe, 0, 1>())
                        {
                            if (comptime System.Compiler.FunctionPointerParametersOverlap<Overlapping, 0, 1>())
                            {
                                if (comptime !System.Compiler.FunctionPointerParametersAreDisjoint<Overlapping, 0, 1>())
                                {
                                    if (comptime !System.Compiler.FunctionPointerParametersAreSame<Overlapping, 0, 1>())
                                    {
                                        if (comptime System.Compiler.FunctionPointerParametersAreSame<SameRegion, 0, 1>())
                                        {
                                            if (comptime System.Compiler.FunctionPointerParametersOverlap<SameRegion, 0, 1>())
                                            {
                                                if (comptime !System.Compiler.FunctionPointerParametersAreDisjoint<SameRegion, 0, 1>())
                                                {
                                                    if (comptime System.Compiler.FunctionPointerParametersAreSame<DefaultSafe, 0, 0>())
                                                    {
                                                        if (comptime System.Compiler.FunctionPointerParametersOverlap<DefaultSafe, 0, 0>())
                                                        {
                                                            if (comptime !System.Compiler.FunctionPointerParametersAreDisjoint<DefaultSafe, 0, 0>())
                                                            {
                                                                return 42;
                                                            }
                                                        }
                                                    }
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }

                return 0;
            }
            """,
            new CompilerOptions(OptimizationLevel: CompilerOptimizationLevel.O0));

        var runBody = ExtractDefinitionBody(llvm, "Run");
        Assert.Contains("ret i32 42", runBody);
        Assert.DoesNotContain("call", runBody);
    }

    [Fact]
    public void ComptimeCallableRawPointerElementCountExpressionFactsFoldToConstants()
    {
        var llvm = CompileToLlvm(
            """
            module Demo

            alias Callback = fnptr<fn void(rawptr<i32[min max]>[arg1], u8[1 10], i32[min max])>;
            alias Handler = borrow closure<fn void(rawptr<i32[min max]>[arg1], u8[1 10], i32[min max])>;

            struct Reader
            {
                static unsafe fn void Read(rawptr<i32[min max]>[length] data, u8[1 10] length, i32[min max] fallback)
                {
                    return;
                }
            }

            finite law i32[min max] Run()
            {
                if (comptime System.Compiler.FunctionPointerParameterHasRawPointerElementCountExpression<Callback, 0>()
                    && comptime !System.Compiler.FunctionPointerParameterHasRawPointerElementCountExpression<Callback, 2>()
                    && comptime (System.Compiler.FunctionPointerParameterRawPointerElementCountExpression<Callback, 0>() == "arg1")
                    && comptime (System.Compiler.FunctionPointerParameterRawPointerElementCountExpression<Callback, 2>() == ""))
                {
                    if (comptime System.Compiler.ClosureParameterHasRawPointerElementCountExpression<Handler, 0>()
                        && comptime !System.Compiler.ClosureParameterHasRawPointerElementCountExpression<Handler, 2>()
                        && comptime (System.Compiler.ClosureParameterRawPointerElementCountExpression<Handler, 0>() == "arg1")
                        && comptime (System.Compiler.ClosureParameterRawPointerElementCountExpression<Handler, 2>() == ""))
                    {
                        if (comptime System.Compiler.MethodParameterHasRawPointerElementCountExpression<Reader, 0, 0>()
                            && comptime !System.Compiler.MethodParameterHasRawPointerElementCountExpression<Reader, 0, 2>()
                            && comptime (System.Compiler.MethodParameterRawPointerElementCountExpression<Reader, 0, 0>() == "length")
                            && comptime (System.Compiler.MethodParameterRawPointerElementCountExpression<Reader, 0, 2>() == ""))
                        {
                            return 42;
                        }
                    }
                }

                return 0;
            }
            """,
            new CompilerOptions(OptimizationLevel: CompilerOptimizationLevel.O0));

        var runBody = ExtractDefinitionBody(llvm, "Run");
        Assert.Contains("ret i32 42", runBody);
        Assert.DoesNotContain("call", runBody);
    }

    [Fact]
    public void ComptimeClosureStructuralFactsFoldToConstants()
    {
        var llvm = CompileToLlvm(
            """
            module Demo

            record Point(i32[min max] X)
            {
            }

            struct Holder<T, comptime u8[1 8] N>
            {
                T Value;
            }

            enum Kind
            {
                One
            }

            alias Callback = heap closure<finite law Point(i32[min max], rawptr<i8[min max]>, Kind)>;
            alias GenericCallback = borrow closure<fn Holder<i32[min max], 4>(Holder<bool, 2>, rawptr<i8[min max]>)>;
            alias MutCallback = heap closure<mut fn Point(i32[min max])>;
            alias Plain = borrow closure<law bool()>;
            alias OnceCallback = heap closure<once fn i32[min max]()>;

            finite law bool ParameterIsNumber<T, comptime u64[0 max] I>()
            {
                return comptime System.Compiler.ClosureParameterTypeIsInteger<T, comptime I>();
            }

            finite law i32[min max] Run()
            {
                if (comptime System.Compiler.ClosureParameterCount<Callback>() == 3)
                {
                    if (comptime System.Compiler.ClosureReturnTypeIs<Callback, Point>())
                    {
                        if (comptime System.Compiler.ClosureReturnTypeIsRecord<Callback>())
                        {
                            if (comptime System.Compiler.ClosureReturnTypeHasConcreteLayout<Callback>())
                            {
                                if (comptime System.Compiler.ClosureParameterTypeIs<Callback, i32[min max], 0>())
                                {
                                    if (comptime ParameterIsNumber<Callback, 0>())
                                    {
                                        if (comptime System.Compiler.ClosureParameterTypeIsRawPointer<Callback, 1>())
                                        {
                                            if (comptime System.Compiler.ClosureParameterTypeIsEnum<Callback, 2>())
                                            {
                                                if (comptime System.Compiler.ClosureKindIsFiniteLaw<Callback>())
                                                {
                                                    if (comptime !System.Compiler.ClosureKindIsFinite<Callback>())
                                                    {
                                                        if (comptime !System.Compiler.ClosureKindIsLaw<Callback>())
                                                        {
                                                            if (comptime System.Compiler.ClosureStorageIsHeap<Callback>())
                                                            {
                                                                if (comptime !System.Compiler.ClosureStorageIsBorrow<Callback>())
                                                                {
                                                                    if (comptime System.Compiler.ClosureCallCapabilityIsNormal<Callback>())
                                                                    {
                                                                        if (comptime !System.Compiler.ClosureCallCapabilityIsMut<Callback>())
                                                                        {
                                                                            if (comptime System.Compiler.ClosureParameterCount<Plain>() == 0)
                                                                            {
                                                                                if (comptime System.Compiler.ClosureReturnTypeIsBool<Plain>())
                                                                                {
                                                                                    if (comptime System.Compiler.ClosureKindIsLaw<Plain>())
                                                                                    {
                                                                                        if (comptime System.Compiler.ClosureStorageIsBorrow<Plain>())
                                                                                        {
                                                                                            if (comptime System.Compiler.ClosureCallCapabilityIsNormal<Plain>())
                                                                                            {
                                                                                                if (comptime System.Compiler.ClosureKindIsFn<MutCallback>())
                                                                                                {
                                                                                                    if (comptime System.Compiler.ClosureCallCapabilityIsMut<MutCallback>())
                                                                                                    {
                                                                                                        if (comptime System.Compiler.ClosureKindIsFn<OnceCallback>())
                                                                                                        {
                                                                                                            if (comptime System.Compiler.ClosureCallCapabilityIsOnce<OnceCallback>())
                                                                                                            {
                                                                                                                if (comptime System.Compiler.ClosureStorageIsInline<inline closure<fn i32[min max](i32[min max])>>())
                                                                                                                {
                                                                                                                    if (comptime System.Compiler.ClosureParameterCount<i32[min max]>() == 0)
                                                                                                                    {
                                                                                                                        if (comptime (System.Compiler.ClosureReturnTypeDisplayName<GenericCallback>() == "Holder<i32, 4>") && comptime (System.Compiler.ClosureReturnTypeBaseName<GenericCallback>() == "Holder"))
                                                                                                                        {
                                                                                                                            if (comptime System.Compiler.ClosureReturnTypeIsGenericInstantiation<GenericCallback>() && comptime System.Compiler.ClosureReturnTypeArgumentCount<GenericCallback>() == 1 && comptime System.Compiler.ClosureReturnTypeComptimeArgumentCount<GenericCallback>() == 1)
                                                                                                                            {
                                                                                                                                if (comptime (System.Compiler.ClosureParameterTypeDisplayName<GenericCallback, 0>() == "Holder<bool, 2>") && comptime (System.Compiler.ClosureParameterTypeBaseName<GenericCallback, 0>() == "Holder"))
                                                                                                                                {
                                                                                                                                    if (comptime System.Compiler.ClosureParameterTypeIsGenericInstantiation<GenericCallback, 0>() && comptime System.Compiler.ClosureParameterTypeArgumentCount<GenericCallback, 0>() == 1 && comptime System.Compiler.ClosureParameterTypeComptimeArgumentCount<GenericCallback, 0>() == 1)
                                                                                                                                    {
                                                                                                                                        if (comptime (System.Compiler.ClosureParameterTypeBaseName<GenericCallback, 1>() == ""))
                                                                                                                                        {
                                                                                                                                            return 42;
                                                                                                                                        }
                                                                                                                                    }
                                                                                                                                }
                                                                                                                            }
                                                                                                                        }
                                                                                                                    }
                                                                                                                }
                                                                                                            }
                                                                                                        }
                                                                                                    }
                                                                                                }
                                                                                            }
                                                                                        }
                                                                                    }
                                                                                }
                                                                            }
                                                                        }
                                                                    }
                                                                }
                                                            }
                                                        }
                                                    }
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }

                return 0;
            }
            """,
            new CompilerOptions(OptimizationLevel: CompilerOptimizationLevel.O0));

        var runBody = ExtractDefinitionBody(llvm, "Run");
        Assert.Contains("ret i32 42", runBody);
        Assert.DoesNotContain("call", runBody);
    }

    [Fact]
    public void ComptimeClosureMemoryContractFactsFoldToConstants()
    {
        var llvm = CompileToLlvm(
            """
            module Demo

            alias DefaultSafe = borrow closure<fn void(rawmutptr<i32[min max]>, rawmutptr<i32[min max]>)>;
            alias Overlapping = borrow closure<fn void(rawmutptr<i32[min max]>, rawmutptr<i32[min max]>) where overlap(arg0, arg1)>;
            alias SameRegion = borrow closure<fn void(rawmutptr<i32[min max]>, rawmutptr<i32[min max]>) where same(arg0, arg1)>;

            finite law bool ArgsAreDisjoint<T, comptime u64[0 max] L, comptime u64[0 max] R>()
            {
                return comptime System.Compiler.ClosureParametersAreDisjoint<T, comptime L, comptime R>();
            }

            finite law i32[min max] Run()
            {
                if (comptime ArgsAreDisjoint<DefaultSafe, 0, 1>())
                {
                    if (comptime !System.Compiler.ClosureParametersOverlap<DefaultSafe, 0, 1>())
                    {
                        if (comptime !System.Compiler.ClosureParametersAreSame<DefaultSafe, 0, 1>())
                        {
                            if (comptime System.Compiler.ClosureParametersOverlap<Overlapping, 0, 1>())
                            {
                                if (comptime !System.Compiler.ClosureParametersAreDisjoint<Overlapping, 0, 1>())
                                {
                                    if (comptime !System.Compiler.ClosureParametersAreSame<Overlapping, 0, 1>())
                                    {
                                        if (comptime System.Compiler.ClosureParametersAreSame<SameRegion, 0, 1>())
                                        {
                                            if (comptime System.Compiler.ClosureParametersOverlap<SameRegion, 0, 1>())
                                            {
                                                if (comptime !System.Compiler.ClosureParametersAreDisjoint<SameRegion, 0, 1>())
                                                {
                                                    if (comptime System.Compiler.ClosureParametersAreSame<DefaultSafe, 0, 0>())
                                                    {
                                                        if (comptime System.Compiler.ClosureParametersOverlap<DefaultSafe, 0, 0>())
                                                        {
                                                            if (comptime !System.Compiler.ClosureParametersAreDisjoint<DefaultSafe, 0, 0>())
                                                            {
                                                                return 42;
                                                            }
                                                        }
                                                    }
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }

                return 0;
            }
            """,
            new CompilerOptions(OptimizationLevel: CompilerOptimizationLevel.O0));

        var runBody = ExtractDefinitionBody(llvm, "Run");
        Assert.Contains("ret i32 42", runBody);
        Assert.DoesNotContain("call", runBody);
    }

    [Fact]
    public void ComptimeIndexedNameFactsFoldToTextConstants()
    {
        var llvm = CompileToLlvm(
            """
            module Demo

            struct Header
            {
                i32[min max] Tag;
                bool Active;
            }

            enum Token
            {
                Empty,
                Pair(i32[min max], bool)
            }

            enum Envelope<T>
            {
                Item(T)
            }

            finite law ascii FieldNameAt<T, comptime u64[0 max] I>()
            {
                return comptime System.Compiler.FieldName<T, comptime I>();
            }

            finite law ascii VariantNameAt<T, comptime u64[0 max] I>()
            {
                return comptime System.Compiler.EnumVariantName<T, comptime I>();
            }

            finite law i32[min max] Run()
            {
                if (comptime (System.Compiler.FieldName<Header, 0>() == "Tag"))
                {
                    if (comptime (FieldNameAt<Header, 1>() == "Active"))
                    {
                        if (comptime (System.Compiler.EnumVariantName<Token, 1>() == "Pair"))
                        {
                            if (comptime (VariantNameAt<Envelope<bool>, 0>() == "Item"))
                            {
                                return 42;
                            }
                        }
                    }
                }

                return 0;
            }

            finite law i32[min max] EscapedTextEquality()
            {
                if (comptime ("A" == "\x41"))
                {
                    return 7;
                }

                return 0;
            }
            """,
            new CompilerOptions(OptimizationLevel: CompilerOptimizationLevel.O0));

        var runBody = ExtractDefinitionBody(llvm, "Run");
        Assert.Contains("ret i32 42", runBody);
        Assert.DoesNotContain("call", runBody);

        var escapedBody = ExtractDefinitionBody(llvm, "EscapedTextEquality");
        Assert.Contains("ret i32 7", escapedBody);
        Assert.DoesNotContain("call", escapedBody);
    }

    [Fact]
    public void ComptimeEnumPayloadNameFactsFoldToConstants()
    {
        var llvm = CompileToLlvm(
            """
            module Demo

            enum Node<T>
            {
                Empty,
                Named { left: i32[min max], right: bool },
                Positional(T)
            }

            finite law bool UsesNamedAt<T, comptime u64[0 max] V>()
            {
                return comptime System.Compiler.EnumVariantUsesNamedFields<T, comptime V>();
            }

            finite law bool PayloadHasNameAt<T, comptime u64[0 max] V, comptime u64[0 max] P>()
            {
                return comptime System.Compiler.EnumVariantPayloadHasName<T, comptime V, comptime P>();
            }

            finite law ascii PayloadNameAt<T, comptime u64[0 max] V, comptime u64[0 max] P>()
            {
                return comptime System.Compiler.EnumVariantPayloadName<T, comptime V, comptime P>();
            }

            finite law i32[min max] Run()
            {
                if (comptime UsesNamedAt<Node<bool>, 1>())
                {
                    if (comptime PayloadHasNameAt<Node<bool>, 1, 0>())
                    {
                        if (comptime (PayloadNameAt<Node<bool>, 1, 0>() == "left"))
                        {
                            if (comptime (System.Compiler.EnumVariantPayloadName<Node<bool>, 1, 1>() == "right"))
                            {
                                if (comptime !System.Compiler.EnumVariantUsesNamedFields<Node<bool>, 2>())
                                {
                                    if (comptime !System.Compiler.EnumVariantPayloadHasName<Node<bool>, 2, 0>())
                                    {
                                        if (comptime (System.Compiler.EnumVariantPayloadName<Node<bool>, 2, 0>() == ""))
                                        {
                                            return 42;
                                        }
                                    }
                                }
                            }
                        }
                    }
                }

                return 0;
            }
            """,
            new CompilerOptions(OptimizationLevel: CompilerOptimizationLevel.O0));

        var runBody = ExtractDefinitionBody(llvm, "Run");
        Assert.Contains("ret i32 42", runBody);
        Assert.DoesNotContain("call", runBody);
    }

    [Fact]
    public void ComptimeEnumVariantFunnelFactsFoldToConstants()
    {
        var llvm = CompileToLlvm(
            """
            module Demo

            enum IOError
            {
                Disk
            }

            enum OtherError
            {
                Disk
            }

            enum ParseError
            {
                Io from IOError,
                Syntax
            }

            enum GenericError<T>
            {
                Wrapped from T,
                Other
            }

            finite law bool FunnelAt<T, comptime u64[0 max] I>()
            {
                return comptime System.Compiler.EnumVariantIsErrorFunnel<T, comptime I>();
            }

            finite law bool FunnelAbsorbs<T, U, comptime u64[0 max] I>()
            {
                return comptime System.Compiler.EnumVariantAbsorbsErrorTypeIs<T, U, comptime I>();
            }

            finite law i32[min max] Run()
            {
                if (comptime FunnelAt<ParseError, 0>())
                {
                    if (comptime !System.Compiler.EnumVariantIsErrorFunnel<ParseError, 1>())
                    {
                        if (comptime FunnelAbsorbs<ParseError, IOError, 0>())
                        {
                            if (comptime !System.Compiler.EnumVariantAbsorbsErrorTypeIs<ParseError, OtherError, 0>())
                            {
                                if (comptime System.Compiler.EnumVariantAbsorbsErrorTypeIs<GenericError<IOError>, IOError, 0>())
                                {
                                    if (comptime !FunnelAbsorbs<GenericError<IOError>, OtherError, 0>())
                                    {
                                        return 42;
                                    }
                                }
                            }
                        }
                    }
                }

                return 0;
            }
            """,
            new CompilerOptions(OptimizationLevel: CompilerOptimizationLevel.O0));

        var runBody = ExtractDefinitionBody(llvm, "Run");
        Assert.Contains("ret i32 42", runBody);
        Assert.DoesNotContain("call", runBody);
    }

    [Fact]
    public void ComptimeTypeGenericParameterFactsFoldToConstants()
    {
        var llvm = CompileToLlvm(
            """
            module Demo

            struct Shape<T, U, comptime u8[1 8] N, comptime i32[min max] Tag>
            {
                T[N] Items;
                U Other;
            }

            finite law bool GenericNameAt<T, comptime u64[0 max] I>()
            {
                return comptime (System.Compiler.TypeGenericParameterName<T, comptime I>() == "U");
            }

            finite law bool ComptimeParameterAt<T, U, comptime u64[0 max] I>()
            {
                return comptime System.Compiler.TypeComptimeGenericParameterTypeIs<T, U, comptime I>();
            }

            finite law i32[min max] Run()
            {
                if (comptime System.Compiler.TypeGenericParameterCount<Shape<bool, i32[min max], 4, 9>>() == 2)
                {
                    if (comptime (System.Compiler.TypeGenericParameterName<Shape<bool, i32[min max], 4, 9>, 0>() == "T"))
                    {
                        if (comptime GenericNameAt<Shape<bool, i32[min max], 4, 9>, 1>())
                        {
                            if (comptime System.Compiler.TypeComptimeGenericParameterCount<Shape<bool, i32[min max], 4, 9>>() == 2)
                            {
                                if (comptime (System.Compiler.TypeComptimeGenericParameterName<Shape<bool, i32[min max], 4, 9>, 0>() == "N"))
                                {
                                    if (comptime (System.Compiler.TypeComptimeGenericParameterName<Shape<bool, i32[min max], 4, 9>, 1>() == "Tag"))
                                    {
                                        if (comptime ComptimeParameterAt<Shape<bool, i32[min max], 4, 9>, u8[1 8], 0>())
                                        {
                                            if (comptime System.Compiler.TypeComptimeGenericParameterTypeIs<Shape<bool, i32[min max], 4, 9>, i32[min max], 1>())
                                            {
                                                if (comptime System.Compiler.TypeComptimeGenericParameterTypeIsInteger<Shape<bool, i32[min max], 4, 9>, 0>()
                                                    && comptime System.Compiler.TypeComptimeGenericParameterTypeHasConcreteLayout<Shape<bool, i32[min max], 4, 9>, 0>()
                                                    && comptime (System.Compiler.TypeComptimeGenericParameterTypeDisplayName<Shape<bool, i32[min max], 4, 9>, 0>() == "u8[1 8]")
                                                    && comptime (System.Compiler.TypeComptimeGenericParameterTypeBaseName<Shape<bool, i32[min max], 4, 9>, 0>() == "")
                                                    && comptime !System.Compiler.TypeComptimeGenericParameterTypeIsGenericInstantiation<Shape<bool, i32[min max], 4, 9>, 0>()
                                                    && comptime System.Compiler.TypeComptimeGenericParameterTypeArgumentCount<Shape<bool, i32[min max], 4, 9>, 0>() == 0
                                                    && comptime System.Compiler.TypeComptimeGenericParameterTypeComptimeArgumentCount<Shape<bool, i32[min max], 4, 9>, 0>() == 0)
                                                {
                                                    if (comptime !System.Compiler.TypeComptimeGenericParameterTypeIs<Shape<bool, i32[min max], 4, 9>, u64[0 max], 0>())
                                                    {
                                                        if (comptime System.Compiler.TypeGenericParameterCount<i32[min max]>() == 0)
                                                        {
                                                            if (comptime (System.Compiler.TypeDisplayName<Shape<bool, i32[min max], 4, 9>>() == "Shape<bool, i32, 4, 9>"))
                                                            {
                                                                if (comptime (System.Compiler.TypeBaseName<Shape<bool, i32[min max], 4, 9>>() == "Shape"))
                                                                {
                                                                    if (comptime System.Compiler.TypeIsGenericInstantiation<Shape<bool, i32[min max], 4, 9>>())
                                                                    {
                                                                        if (comptime !System.Compiler.TypeIsGenericInstantiation<i32[min max]>())
                                                                        {
                                                                            if (comptime System.Compiler.TypeArgumentCount<Shape<bool, i32[min max], 4, 9>>() == 2)
                                                                            {
                                                                                if (comptime System.Compiler.TypeComptimeArgumentCount<Shape<bool, i32[min max], 4, 9>>() == 2)
                                                                                {
                                                                                    if (comptime System.Compiler.TypeArgumentTypeIs<Shape<bool, i32[min max], 4, 9>, bool, 0>())
                                                                                    {
                                                                                        if (comptime System.Compiler.TypeArgumentTypeIsInteger<Shape<bool, i32[min max], 4, 9>, 1>())
                                                                                        {
                                                                                            if (comptime !System.Compiler.TypeArgumentTypeIsBool<Shape<bool, i32[min max], 4, 9>, 1>())
                                                                                            {
                                                                                                if (comptime System.Compiler.TypeArgumentTypeHasConcreteLayout<Shape<bool, i32[min max], 4, 9>, 1>()
                                                                                                    && comptime (System.Compiler.TypeArgumentTypeDisplayName<Shape<bool, i32[min max], 4, 9>, 1>() == "i32")
                                                                                                    && comptime (System.Compiler.TypeArgumentTypeBaseName<Shape<bool, i32[min max], 4, 9>, 1>() == "")
                                                                                                    && comptime !System.Compiler.TypeArgumentTypeIsGenericInstantiation<Shape<bool, i32[min max], 4, 9>, 1>()
                                                                                                    && comptime System.Compiler.TypeArgumentTypeArgumentCount<Shape<bool, i32[min max], 4, 9>, 1>() == 0
                                                                                                    && comptime System.Compiler.TypeArgumentTypeComptimeArgumentCount<Shape<bool, i32[min max], 4, 9>, 1>() == 0)
                                                                                                {
                                                                                                    return 42;
                                                                                                }
                                                                                            }
                                                                                        }
                                                                                    }
                                                                                }
                                                                            }
                                                                        }
                                                                    }
                                                                }
                                                            }
                                                        }
                                                    }
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }

                return 0;
            }
            """,
            new CompilerOptions(OptimizationLevel: CompilerOptimizationLevel.O0));

        var runBody = ExtractDefinitionBody(llvm, "Run");
        Assert.Contains("ret i32 42", runBody);
        Assert.DoesNotContain("call", runBody);
    }

    [Fact]
    public void ComptimeTypeComptimeArgumentFactsFoldToConstants()
    {
        var llvm = CompileToLlvm(
            """
            module Demo

            struct Shape<T, comptime u8[1 8] N, comptime i32[-10 10] Tag>
            {
                T[N] Items;
            }

            finite law bool ArgumentTypeAt<T, U, comptime u64[0 max] I>()
            {
                return comptime System.Compiler.TypeComptimeArgumentTypeIs<T, U, comptime I>();
            }

            finite law bool ArgumentValueAt<T, comptime u64[0 max] I, comptime i1024[min max] Value>()
            {
                return comptime System.Compiler.TypeComptimeArgumentValueIs<T, comptime I, comptime Value>();
            }

            finite law i32[min max] Run()
            {
                if (comptime System.Compiler.TypeComptimeArgumentCount<Shape<bool, 4, 9>>() == 2)
                {
                    if (comptime (System.Compiler.TypeComptimeArgumentName<Shape<bool, 4, 9>, 0>() == "N"))
                    {
                        if (comptime (System.Compiler.TypeComptimeArgumentName<Shape<bool, 4, 9>, 1>() == "Tag"))
                        {
                            if (comptime ArgumentTypeAt<Shape<bool, 4, 9>, u8[1 8], 0>())
                            {
                                if (comptime System.Compiler.TypeComptimeArgumentTypeIs<Shape<bool, 4, 9>, i32[-10 10], 1>())
                                {
                                    if (comptime System.Compiler.TypeComptimeArgumentValueIs<Shape<bool, 4, 9>, 0, 4>())
                                    {
                                        if (comptime ArgumentValueAt<Shape<bool, 4, 9>, 1, 9>())
                                        {
                                            if (comptime !System.Compiler.TypeComptimeArgumentValueIs<Shape<bool, 4, 9>, 1, 8>())
                                            {
                                                if (comptime System.Compiler.TypeComptimeArgumentTypeIsInteger<Shape<bool, 4, 9>, 0>()
                                                    && comptime !System.Compiler.TypeComptimeArgumentTypeIsBool<Shape<bool, 4, 9>, 0>()
                                                    && comptime System.Compiler.TypeComptimeArgumentTypeHasConcreteLayout<Shape<bool, 4, 9>, 0>()
                                                    && comptime (System.Compiler.TypeComptimeArgumentTypeDisplayName<Shape<bool, 4, 9>, 0>() == "u8[1 8]")
                                                    && comptime (System.Compiler.TypeComptimeArgumentTypeBaseName<Shape<bool, 4, 9>, 0>() == "")
                                                    && comptime !System.Compiler.TypeComptimeArgumentTypeIsGenericInstantiation<Shape<bool, 4, 9>, 0>()
                                                    && comptime System.Compiler.TypeComptimeArgumentTypeArgumentCount<Shape<bool, 4, 9>, 0>() == 0
                                                    && comptime System.Compiler.TypeComptimeArgumentTypeComptimeArgumentCount<Shape<bool, 4, 9>, 0>() == 0)
                                                {
                                                    return 42;
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }

                return 0;
            }
            """,
            new CompilerOptions(OptimizationLevel: CompilerOptimizationLevel.O0));

        var runBody = ExtractDefinitionBody(llvm, "Run");
        Assert.Contains("ret i32 42", runBody);
        Assert.DoesNotContain("call", runBody);
    }

    [Fact]
    public void ComptimeMethodStructuralFactsFoldToConstants()
    {
        var llvm = CompileToLlvm(
            """
            module Demo

            struct Holder<T, comptime u8[1 8] N>
            {
                T[N] Items;
            }

            struct Box<T>
            {
                T Value;

                fn Holder<T, 4> Echo(borrow Box<T> self, Holder<T, 4> fallback)
                {
                    return fallback;
                }

                static finite law i32[min max] Size()
                {
                    return 7;
                }
            }

            struct Tool
            {
                finite law T Pick<T, comptime u8[1 8] N>(T value) where T: Drawable, Bound<T, comptime N>
                {
                    return value;
                }
            }

            trait Drawable
            {
                finite law i32[min max] Draw(borrow Self self);
            }

            trait Bound<T, comptime u8[1 8] N>
            {
                finite law T ReadBound(borrow Self self);
            }

            trait Reader
            {
                finite law i32[min max] Read(borrow Self self);

                finite law i32[min max] Double(borrow Self self)
                {
                    return self.Read() + self.Read();
                }
            }

            doctrine Inspect
            {
                law i32[min max] Score(borrow Box<i32[min max]> box)
                {
                    return box.Value;
                }
            }

            finite law bool MethodParameterAt<T, U, comptime u64[0 max] M, comptime u64[0 max] P>()
            {
                return comptime System.Compiler.MethodParameterTypeIs<T, U, comptime M, comptime P>();
            }

            finite law i32[min max] Run()
            {
                if (comptime System.Compiler.MethodCount<Box<i32[min max]>>() == 2)
                {
                    if (comptime (System.Compiler.MethodName<Box<i32[min max]>, 0>() == "Echo"))
                    {
                        if (comptime System.Compiler.MethodParameterCount<Box<i32[min max]>, 0>() == 2)
                        {
                            if (comptime (System.Compiler.MethodParameterName<Box<i32[min max]>, 0, 0>() == "self") && comptime (System.Compiler.MethodParameterName<Box<i32[min max]>, 0, 1>() == "fallback"))
                            {
                                if (comptime System.Compiler.MethodReturnTypeIs<Box<i32[min max]>, Holder<i32[min max], 4>, 0>())
                                {
                                    if (comptime MethodParameterAt<Box<i32[min max]>, Holder<i32[min max], 4>, 0, 1>())
                                    {
                                        if (comptime System.Compiler.MethodParameterTypeIsNamed<Box<i32[min max]>, 0, 0>())
                                        {
                                            if (comptime (System.Compiler.MethodReturnTypeDisplayName<Box<i32[min max]>, 0>() == "Holder<i32, 4>") && comptime (System.Compiler.MethodReturnTypeBaseName<Box<i32[min max]>, 0>() == "Holder"))
                                            {
                                                if (comptime System.Compiler.MethodReturnTypeIsGenericInstantiation<Box<i32[min max]>, 0>() && comptime System.Compiler.MethodReturnTypeArgumentCount<Box<i32[min max]>, 0>() == 1 && comptime System.Compiler.MethodReturnTypeComptimeArgumentCount<Box<i32[min max]>, 0>() == 1)
                                                {
                                                    if (comptime (System.Compiler.MethodParameterTypeDisplayName<Box<i32[min max]>, 0, 1>() == "Holder<i32, 4>") && comptime (System.Compiler.MethodParameterTypeBaseName<Box<i32[min max]>, 0, 1>() == "Holder"))
                                                    {
                                                        if (comptime System.Compiler.MethodParameterTypeIsGenericInstantiation<Box<i32[min max]>, 0, 1>() && comptime System.Compiler.MethodParameterTypeArgumentCount<Box<i32[min max]>, 0, 1>() == 1 && comptime System.Compiler.MethodParameterTypeComptimeArgumentCount<Box<i32[min max]>, 0, 1>() == 1)
                                                        {
                                                            if (comptime System.Compiler.MethodKindIsFn<Box<i32[min max]>, 0>() && comptime !System.Compiler.MethodIsStatic<Box<i32[min max]>, 0>())
                                                            {
                                                                if (comptime System.Compiler.MethodKindIsFiniteLaw<Box<i32[min max]>, 1>() && comptime System.Compiler.MethodIsStatic<Box<i32[min max]>, 1>())
                                                                {
                                                                    if (comptime System.Compiler.MethodHasBody<Box<i32[min max]>, 1>() && comptime !System.Compiler.MethodIsUnsafe<Box<i32[min max]>, 1>())
                                                                    {
                                                                        if (comptime System.Compiler.MethodGenericParameterCount<Tool, 0>() == 1)
                                                                        {
                                                                            if (comptime (System.Compiler.MethodGenericParameterName<Tool, 0, 0>() == "T")
                                                                                && comptime System.Compiler.MethodGenericParameterTraitBoundCount<Tool, 0, 0>() == 2
                                                                                && comptime System.Compiler.MethodGenericParameterTraitBoundTypeIs<Tool, Drawable, 0, 0, 0>()
                                                                                && comptime System.Compiler.MethodGenericParameterTraitBoundTypeIsNamed<Tool, 0, 0, 0>()
                                                                                && comptime System.Compiler.MethodGenericParameterTraitBoundTypeIsTrait<Tool, 0, 0, 0>()
                                                                                && comptime !System.Compiler.MethodGenericParameterTraitBoundTypeHasConcreteLayout<Tool, 0, 0, 0>()
                                                                                && comptime (System.Compiler.MethodGenericParameterTraitBoundTypeBaseName<Tool, 0, 0, 1>() == "Bound")
                                                                                && comptime System.Compiler.MethodGenericParameterTraitBoundTypeIsNamed<Tool, 0, 0, 1>()
                                                                                && comptime System.Compiler.MethodGenericParameterTraitBoundTypeIsTrait<Tool, 0, 0, 1>()
                                                                                && comptime !System.Compiler.MethodGenericParameterTraitBoundTypeHasConcreteLayout<Tool, 0, 0, 1>()
                                                                                && comptime System.Compiler.MethodGenericParameterTraitBoundTypeIsGenericInstantiation<Tool, 0, 0, 1>()
                                                                                && comptime System.Compiler.MethodGenericParameterTraitBoundTypeArgumentCount<Tool, 0, 0, 1>() == 1
                                                                                && comptime System.Compiler.MethodGenericParameterTraitBoundTypeComptimeArgumentCount<Tool, 0, 0, 1>() == 1)
                                                                            {
                                                                                if (comptime System.Compiler.MethodComptimeGenericParameterCount<Tool, 0>() == 1)
                                                                                {
                                                                                    if (comptime (System.Compiler.MethodComptimeGenericParameterName<Tool, 0, 0>() == "N"))
                                                                                    {
                                                                                        if (comptime System.Compiler.MethodComptimeGenericParameterTypeIs<Tool, u8[1 8], 0, 0>())
                                                                                        {
                                                                                            if (comptime System.Compiler.MethodComptimeGenericParameterTypeIsInteger<Tool, 0, 0>()
                                                                                                && comptime System.Compiler.MethodComptimeGenericParameterTypeHasConcreteLayout<Tool, 0, 0>()
                                                                                                && comptime (System.Compiler.MethodComptimeGenericParameterTypeDisplayName<Tool, 0, 0>() == "u8[1 8]")
                                                                                                && comptime (System.Compiler.MethodComptimeGenericParameterTypeBaseName<Tool, 0, 0>() == "")
                                                                                                && comptime !System.Compiler.MethodComptimeGenericParameterTypeIsGenericInstantiation<Tool, 0, 0>()
                                                                                                && comptime System.Compiler.MethodComptimeGenericParameterTypeArgumentCount<Tool, 0, 0>() == 0
                                                                                                && comptime System.Compiler.MethodComptimeGenericParameterTypeComptimeArgumentCount<Tool, 0, 0>() == 0)
                                                                                            {
                                                                                                if (comptime System.Compiler.MethodCount<Reader>() == 2)
                                                                                                {
                                                                                                    if (comptime System.Compiler.MethodHasBody<Reader, 0>() && comptime !System.Compiler.MethodHasBody<Reader, 1>())
                                                                                                    {
                                                                                                        if (comptime System.Compiler.MethodCount<Inspect>() == 1)
                                                                                                        {
                                                                                                            if (comptime System.Compiler.MethodKindIsLaw<Inspect, 0>())
                                                                                                            {
                                                                                                                return 42;
                                                                                                            }
                                                                                                        }
                                                                                                    }
                                                                                                }
                                                                                            }
                                                                                        }
                                                                                    }
                                                                                }
                                                                            }
                                                                        }
                                                                    }
                                                                }
                                                            }
                                                        }
                                                    }
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }

                return 0;
            }
            """,
            new CompilerOptions(OptimizationLevel: CompilerOptimizationLevel.O0));

        var runBody = ExtractDefinitionBody(llvm, "Run");
        Assert.Contains("ret i32 42", runBody);
        Assert.DoesNotContain("call", runBody);
    }

    [Fact]
    public void ComptimeIndexedStructuralFactsRejectOutOfRangeIndices()
    {
        var result = Compile(
            """
            module Demo

            struct Header
            {
                i32[min max] Tag;
            }

            finite law u64[0 max] Run()
            {
                return comptime System.Compiler.FieldOffset<Header, 2>();
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK3054"
            && diagnostic.Message.Contains("field index '2' is out of range", StringComparison.Ordinal));
    }

    [Fact]
    public void ComptimeStructLayoutAttributeFactsRejectOutOfRangeIndices()
    {
        var result = Compile(
            """
            module Demo

            [StructLayout(Explicit)]
            struct Header
            {
                [FieldOffset(0)] i32[min max] Tag;
            }

            finite law u64[0 max] Run()
            {
                return comptime System.Compiler.FieldExplicitOffset<Header, 1>();
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK3054"
            && diagnostic.Message.Contains("field index '1' is out of range", StringComparison.Ordinal));
    }

    [Fact]
    public void ComptimeThreadSafetyLawAttributeFactsRejectOutOfRangeIndices()
    {
        var typeResult = Compile(
            """
            module Demo

            [Grant(Transferable)]
            struct Token
            {
                i32[min max] Value;
            }

            finite law ascii Name()
            {
                return comptime System.Compiler.TypeThreadSafetyLawAttributeLawName<Token, 1>();
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(typeResult.Succeeded);
        Assert.Contains(typeResult.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK3054"
            && diagnostic.Message.Contains("thread-safety law attribute index '1' is out of range", StringComparison.Ordinal));

        var fieldResult = Compile(
            """
            module Demo

            struct Token
            {
                [Deny(Shareable)]
                i32[min max] Value;
            }

            finite law ascii Name()
            {
                return comptime System.Compiler.FieldThreadSafetyLawAttributeLawName<Token, 0, 1>();
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(fieldResult.Succeeded);
        Assert.Contains(fieldResult.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK3054"
            && diagnostic.Message.Contains("thread-safety law attribute index '1' is out of range", StringComparison.Ordinal));
    }

    [Fact]
    public void ComptimeMethodThreadSafetyLawPredicateFactsRejectOutOfRangeIndices()
    {
        var result = Compile(
            """
            module Demo

            struct Gate
            {
                fn void Move()
                {
                }
            }

            finite law ascii Name()
            {
                return comptime System.Compiler.MethodThreadSafetyLawPredicateLawName<Gate, 0, 0>();
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK3054"
            && diagnostic.Message.Contains("thread-safety law predicate index '0' is out of range", StringComparison.Ordinal));
    }

    [Fact]
    public void ComptimeIndexedTypeFactsRejectOutOfRangeIndices()
    {
        var fieldResult = Compile(
            """
            module Demo

            struct Header
            {
                i32[min max] Tag;
            }

            finite law bool Field()
            {
                return comptime System.Compiler.FieldTypeIs<Header, i32[min max], 1>();
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(fieldResult.Succeeded);
        Assert.Contains(fieldResult.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK3054"
            && diagnostic.Message.Contains("field index '1' is out of range", StringComparison.Ordinal));

        var payloadResult = Compile(
            """
            module Demo

            enum Token
            {
                Empty,
                Pair(i32[min max])
            }

            finite law bool Payload()
            {
                return comptime System.Compiler.EnumVariantPayloadTypeIs<Token, i32[min max], 1, 1>();
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(payloadResult.Succeeded);
        Assert.Contains(payloadResult.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK3054"
            && diagnostic.Message.Contains("payload index '1' is out of range", StringComparison.Ordinal));

        var fieldPredicateResult = Compile(
            """
            module Demo

            struct Header
            {
                bool Active;
            }

            finite law bool FieldPredicate()
            {
                return comptime System.Compiler.FieldTypeIsBool<Header, 1>();
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(fieldPredicateResult.Succeeded);
        Assert.Contains(fieldPredicateResult.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK3054"
            && diagnostic.Message.Contains("field index '1' is out of range", StringComparison.Ordinal));

        var payloadPredicateResult = Compile(
            """
            module Demo

            enum Token
            {
                Number(i32[min max])
            }

            finite law bool PayloadPredicate()
            {
                return comptime System.Compiler.EnumVariantPayloadTypeIsInteger<Token, 0, 1>();
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(payloadPredicateResult.Succeeded);
        Assert.Contains(payloadPredicateResult.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK3054"
            && diagnostic.Message.Contains("payload index '1' is out of range", StringComparison.Ordinal));

        var fieldMetadataResult = Compile(
            """
            module Demo

            struct Header
            {
                bool Active;
            }

            finite law ascii FieldMetadata()
            {
                return comptime System.Compiler.FieldTypeDisplayName<Header, 1>();
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(fieldMetadataResult.Succeeded);
        Assert.Contains(fieldMetadataResult.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK3054"
            && diagnostic.Message.Contains("field index '1' is out of range", StringComparison.Ordinal));

        var fieldQualifierResult = Compile(
            """
            module Demo

            struct Header
            {
                storeborrow i32[min max] Value;
            }

            finite law bool FieldQualifier()
            {
                return comptime System.Compiler.FieldTypeBorrowKindIsStoreBorrow<Header, 1>();
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(fieldQualifierResult.Succeeded);
        Assert.Contains(fieldQualifierResult.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK3054"
            && diagnostic.Message.Contains("field index '1' is out of range", StringComparison.Ordinal));

        var payloadMetadataResult = Compile(
            """
            module Demo

            enum Token
            {
                Number(i32[min max])
            }

            finite law ascii PayloadMetadata()
            {
                return comptime System.Compiler.EnumVariantPayloadTypeDisplayName<Token, 0, 1>();
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(payloadMetadataResult.Succeeded);
        Assert.Contains(payloadMetadataResult.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK3054"
            && diagnostic.Message.Contains("payload index '1' is out of range", StringComparison.Ordinal));

        var payloadQualifierResult = Compile(
            """
            module Demo

            enum Token
            {
                Number(shared i32[min max])
            }

            finite law bool PayloadQualifier()
            {
                return comptime System.Compiler.EnumVariantPayloadTypeAccessKindIsShared<Token, 0, 1>();
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(payloadQualifierResult.Succeeded);
        Assert.Contains(payloadQualifierResult.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK3054"
            && diagnostic.Message.Contains("payload index '1' is out of range", StringComparison.Ordinal));
    }

    [Fact]
    public void ComptimeTypeComptimeArgumentFactsRejectWrongTargetsAndOutOfRangeIndices()
    {
        var targetResult = Compile(
            """
            module Demo

            finite law ascii Run()
            {
                return comptime System.Compiler.TypeComptimeArgumentName<i32[min max], 0>();
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(targetResult.Succeeded);
        Assert.Contains(targetResult.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK3054"
            && diagnostic.Message.Contains("requires a named type", StringComparison.Ordinal));

        var indexResult = Compile(
            """
            module Demo

            struct Buffer<T, comptime u8[1 8] N>
            {
                T[N] Items;
            }

            finite law ascii Run()
            {
                return comptime System.Compiler.TypeComptimeArgumentName<Buffer<i32[min max], 4>, 1>();
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(indexResult.Succeeded);
        Assert.Contains(indexResult.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK3054"
            && diagnostic.Message.Contains("comptime argument index '1' is out of range", StringComparison.Ordinal));

        var predicateIndexResult = Compile(
            """
            module Demo

            struct Buffer<T, comptime u8[1 8] N>
            {
                T[N] Items;
            }

            finite law bool Run()
            {
                return comptime System.Compiler.TypeComptimeArgumentTypeIsInteger<Buffer<i32[min max], 4>, 1>();
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(predicateIndexResult.Succeeded);
        Assert.Contains(predicateIndexResult.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK3054"
            && diagnostic.Message.Contains("comptime argument index '1' is out of range", StringComparison.Ordinal));
    }

    [Fact]
    public void ComptimeFunctionPointerStructuralFactsRejectWrongTargetsAndOutOfRangeIndices()
    {
        var targetResult = Compile(
            """
            module Demo

            finite law bool Run()
            {
                return comptime System.Compiler.FunctionPointerReturnTypeIsInteger<i32[min max]>();
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(targetResult.Succeeded);
        Assert.Contains(targetResult.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK3054"
            && diagnostic.Message.Contains("requires a function pointer type", StringComparison.Ordinal));

        var metadataTargetResult = Compile(
            """
            module Demo

            finite law ascii Run()
            {
                return comptime System.Compiler.FunctionPointerReturnTypeDisplayName<i32[min max]>();
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(metadataTargetResult.Succeeded);
        Assert.Contains(metadataTargetResult.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK3054"
            && diagnostic.Message.Contains("requires a function pointer type", StringComparison.Ordinal));

        var rawPointerCountTargetResult = Compile(
            """
            module Demo

            finite law bool Run()
            {
                return comptime System.Compiler.FunctionPointerParameterHasRawPointerElementCountExpression<i32[min max], 0>();
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(rawPointerCountTargetResult.Succeeded);
        Assert.Contains(rawPointerCountTargetResult.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK3054"
            && diagnostic.Message.Contains("requires a function pointer type", StringComparison.Ordinal));

        var memoryTargetResult = Compile(
            """
            module Demo

            finite law bool Run()
            {
                return comptime System.Compiler.FunctionPointerParametersOverlap<i32[min max], 0, 1>();
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(memoryTargetResult.Succeeded);
        Assert.Contains(memoryTargetResult.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK3054"
            && diagnostic.Message.Contains("requires a function pointer type", StringComparison.Ordinal));

        var indexResult = Compile(
            """
            module Demo

            alias Callback = fnptr<fn i32[min max](i32[min max])>;

            finite law bool Run()
            {
                return comptime System.Compiler.FunctionPointerParameterTypeIsInteger<Callback, 1>();
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(indexResult.Succeeded);
        Assert.Contains(indexResult.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK3054"
            && diagnostic.Message.Contains("parameter index '1' is out of range", StringComparison.Ordinal));

        var metadataIndexResult = Compile(
            """
            module Demo

            alias Callback = fnptr<fn i32[min max](i32[min max])>;

            finite law ascii Run()
            {
                return comptime System.Compiler.FunctionPointerParameterTypeDisplayName<Callback, 1>();
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(metadataIndexResult.Succeeded);
        Assert.Contains(metadataIndexResult.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK3054"
            && diagnostic.Message.Contains("parameter index '1' is out of range", StringComparison.Ordinal));

        var rawPointerCountIndexResult = Compile(
            """
            module Demo

            alias Callback = fnptr<fn void(rawptr<i32[min max]>[arg1], u8[1 10])>;

            finite law ascii Run()
            {
                return comptime System.Compiler.FunctionPointerParameterRawPointerElementCountExpression<Callback, 2>();
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(rawPointerCountIndexResult.Succeeded);
        Assert.Contains(rawPointerCountIndexResult.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK3054"
            && diagnostic.Message.Contains("parameter index '2' is out of range", StringComparison.Ordinal));

        var memoryIndexResult = Compile(
            """
            module Demo

            alias Callback = fnptr<fn void(rawmutptr<i32[min max]>)>;

            finite law bool Run()
            {
                return comptime System.Compiler.FunctionPointerParametersAreDisjoint<Callback, 0, 1>();
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(memoryIndexResult.Succeeded);
        Assert.Contains(memoryIndexResult.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK3054"
            && diagnostic.Message.Contains("parameter index '1' is out of range", StringComparison.Ordinal));
    }

    [Fact]
    public void ComptimeCallableNestedTypeArgumentStructuralFactsRejectWrongTargetsAndOutOfRangeIndices()
    {
        var targetResult = Compile(
            """
            module Demo

            finite law bool Run()
            {
                return comptime System.Compiler.FunctionPointerReturnTypeArgumentTypeIsInteger<i32[min max], 0>();
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(targetResult.Succeeded);
        Assert.Contains(targetResult.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK3054"
            && diagnostic.Message.Contains("requires a function pointer type", StringComparison.Ordinal));

        var returnArgumentIndexResult = Compile(
            """
            module Demo

            alias Callback = fnptr<fn i32[min max]()>;

            finite law bool Run()
            {
                return comptime System.Compiler.FunctionPointerReturnTypeArgumentTypeIsInteger<Callback, 0>();
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(returnArgumentIndexResult.Succeeded);
        Assert.Contains(returnArgumentIndexResult.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK3054"
            && diagnostic.Message.Contains("type argument index '0' is out of range for return type", StringComparison.Ordinal));

        var closureParameterArgumentIndexResult = Compile(
            """
            module Demo

            struct Box<T>
            {
                T Value;
            }

            alias Handler = borrow closure<fn void(Box<i32[min max]>)>;

            finite law bool Run()
            {
                return comptime System.Compiler.ClosureParameterTypeArgumentTypeIsInteger<Handler, 0, 1>();
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(closureParameterArgumentIndexResult.Succeeded);
        Assert.Contains(closureParameterArgumentIndexResult.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK3054"
            && diagnostic.Message.Contains("type argument index '1' is out of range for parameter slot 0", StringComparison.Ordinal));

        var methodParameterArgumentIndexResult = Compile(
            """
            module Demo

            struct Box<T>
            {
                T Value;

                fn Box<T> Echo(borrow Box<T> self, Box<T> fallback)
                {
                    return fallback;
                }
            }

            finite law bool Run()
            {
                return comptime System.Compiler.MethodParameterTypeArgumentTypeIsInteger<Box<i32[min max]>, 0, 1, 1>();
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(methodParameterArgumentIndexResult.Succeeded);
        Assert.Contains(methodParameterArgumentIndexResult.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK3054"
            && diagnostic.Message.Contains("type argument index '1' is out of range for parameter slot 1", StringComparison.Ordinal));
    }

    [Fact]
    public void ComptimeClosureStructuralFactsRejectWrongTargetsAndOutOfRangeIndices()
    {
        var targetResult = Compile(
            """
            module Demo

            finite law bool Run()
            {
                return comptime System.Compiler.ClosureReturnTypeIsInteger<i32[min max]>();
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(targetResult.Succeeded);
        Assert.Contains(targetResult.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK3054"
            && diagnostic.Message.Contains("requires a closure type", StringComparison.Ordinal));

        var metadataTargetResult = Compile(
            """
            module Demo

            finite law ascii Run()
            {
                return comptime System.Compiler.ClosureReturnTypeDisplayName<i32[min max]>();
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(metadataTargetResult.Succeeded);
        Assert.Contains(metadataTargetResult.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK3054"
            && diagnostic.Message.Contains("requires a closure type", StringComparison.Ordinal));

        var rawPointerCountTargetResult = Compile(
            """
            module Demo

            finite law bool Run()
            {
                return comptime System.Compiler.ClosureParameterHasRawPointerElementCountExpression<i32[min max], 0>();
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(rawPointerCountTargetResult.Succeeded);
        Assert.Contains(rawPointerCountTargetResult.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK3054"
            && diagnostic.Message.Contains("requires a closure type", StringComparison.Ordinal));

        var memoryTargetResult = Compile(
            """
            module Demo

            finite law bool Run()
            {
                return comptime System.Compiler.ClosureParametersOverlap<i32[min max], 0, 1>();
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(memoryTargetResult.Succeeded);
        Assert.Contains(memoryTargetResult.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK3054"
            && diagnostic.Message.Contains("requires a closure type", StringComparison.Ordinal));

        var indexResult = Compile(
            """
            module Demo

            alias Callback = borrow closure<fn i32[min max](i32[min max])>;

            finite law bool Run()
            {
                return comptime System.Compiler.ClosureParameterTypeIsInteger<Callback, 1>();
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(indexResult.Succeeded);
        Assert.Contains(indexResult.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK3054"
            && diagnostic.Message.Contains("parameter index '1' is out of range", StringComparison.Ordinal));

        var metadataIndexResult = Compile(
            """
            module Demo

            alias Callback = borrow closure<fn i32[min max](i32[min max])>;

            finite law ascii Run()
            {
                return comptime System.Compiler.ClosureParameterTypeDisplayName<Callback, 1>();
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(metadataIndexResult.Succeeded);
        Assert.Contains(metadataIndexResult.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK3054"
            && diagnostic.Message.Contains("parameter index '1' is out of range", StringComparison.Ordinal));

        var rawPointerCountIndexResult = Compile(
            """
            module Demo

            alias Callback = borrow closure<fn void(rawptr<i32[min max]>[arg1], u8[1 10])>;

            finite law ascii Run()
            {
                return comptime System.Compiler.ClosureParameterRawPointerElementCountExpression<Callback, 2>();
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(rawPointerCountIndexResult.Succeeded);
        Assert.Contains(rawPointerCountIndexResult.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK3054"
            && diagnostic.Message.Contains("parameter index '2' is out of range", StringComparison.Ordinal));

        var memoryIndexResult = Compile(
            """
            module Demo

            alias Callback = borrow closure<fn void(rawmutptr<i32[min max]>)>;

            finite law bool Run()
            {
                return comptime System.Compiler.ClosureParametersAreDisjoint<Callback, 0, 1>();
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(memoryIndexResult.Succeeded);
        Assert.Contains(memoryIndexResult.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK3054"
            && diagnostic.Message.Contains("parameter index '1' is out of range", StringComparison.Ordinal));
    }

    [Fact]
    public void ComptimeMethodStructuralFactsRejectWrongTargetsAndOutOfRangeIndices()
    {
        var targetResult = Compile(
            """
            module Demo

            finite law ascii Run()
            {
                return comptime System.Compiler.MethodName<i32[min max], 0>();
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(targetResult.Succeeded);
        Assert.Contains(targetResult.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK3054"
            && diagnostic.Message.Contains("requires a named type", StringComparison.Ordinal));

        var methodIndexResult = Compile(
            """
            module Demo

            struct Header
            {
                fn i32[min max] Read(borrow Header self)
                {
                    return 1;
                }
            }

            finite law ascii Run()
            {
                return comptime System.Compiler.MethodName<Header, 1>();
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(methodIndexResult.Succeeded);
        Assert.Contains(methodIndexResult.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK3054"
            && diagnostic.Message.Contains("method index '1' is out of range", StringComparison.Ordinal));

        var moduleNameMethodIndexResult = Compile(
            """
            module Demo

            struct Header
            {
                fn i32[min max] Read(borrow Header self)
                {
                    return 1;
                }
            }

            finite law ascii Run()
            {
                return comptime System.Compiler.MethodModuleName<Header, 1>();
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(moduleNameMethodIndexResult.Succeeded);
        Assert.Contains(moduleNameMethodIndexResult.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK3054"
            && diagnostic.Message.Contains("method index '1' is out of range", StringComparison.Ordinal));

        var parameterIndexResult = Compile(
            """
            module Demo

            struct Header
            {
                fn i32[min max] Read(borrow Header self)
                {
                    return 1;
                }
            }

            finite law bool Run()
            {
                return comptime System.Compiler.MethodParameterTypeIsInteger<Header, 0, 1>();
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(parameterIndexResult.Succeeded);
        Assert.Contains(parameterIndexResult.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK3054"
            && diagnostic.Message.Contains("parameter index '1' is out of range", StringComparison.Ordinal));

        var returnMetadataMethodIndexResult = Compile(
            """
            module Demo

            struct Header
            {
                fn i32[min max] Read(borrow Header self)
                {
                    return 1;
                }
            }

            finite law ascii Run()
            {
                return comptime System.Compiler.MethodReturnTypeDisplayName<Header, 1>();
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(returnMetadataMethodIndexResult.Succeeded);
        Assert.Contains(returnMetadataMethodIndexResult.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK3054"
            && diagnostic.Message.Contains("method index '1' is out of range", StringComparison.Ordinal));

        var parameterMetadataIndexResult = Compile(
            """
            module Demo

            struct Header
            {
                fn i32[min max] Read(borrow Header self)
                {
                    return 1;
                }
            }

            finite law ascii Run()
            {
                return comptime System.Compiler.MethodParameterTypeDisplayName<Header, 0, 1>();
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(parameterMetadataIndexResult.Succeeded);
        Assert.Contains(parameterMetadataIndexResult.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK3054"
            && diagnostic.Message.Contains("parameter index '1' is out of range", StringComparison.Ordinal));

        var rawPointerCountIndexResult = Compile(
            """
            module Demo

            struct Header
            {
                static unsafe fn void Read(rawptr<i32[min max]>[length] data, u8[1 10] length)
                {
                    return;
                }
            }

            finite law ascii Run()
            {
                return comptime System.Compiler.MethodParameterRawPointerElementCountExpression<Header, 0, 2>();
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(rawPointerCountIndexResult.Succeeded);
        Assert.Contains(rawPointerCountIndexResult.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK3054"
            && diagnostic.Message.Contains("parameter index '2' is out of range", StringComparison.Ordinal));

        var parameterNameIndexResult = Compile(
            """
            module Demo

            struct Header
            {
                fn i32[min max] Read(borrow Header self)
                {
                    return 1;
                }
            }

            finite law ascii Run()
            {
                return comptime System.Compiler.MethodParameterName<Header, 0, 1>();
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(parameterNameIndexResult.Succeeded);
        Assert.Contains(parameterNameIndexResult.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK3054"
            && diagnostic.Message.Contains("parameter index '1' is out of range", StringComparison.Ordinal));

        var genericIndexResult = Compile(
            """
            module Demo

            struct Tool
            {
                fn T Pick<T>(T value)
                {
                    return value;
                }
            }

            finite law ascii Run()
            {
                return comptime System.Compiler.MethodGenericParameterName<Tool, 0, 1>();
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(genericIndexResult.Succeeded);
        Assert.Contains(genericIndexResult.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK3054"
            && diagnostic.Message.Contains("generic parameter index '1' is out of range", StringComparison.Ordinal));

        var traitBoundGenericIndexResult = Compile(
            """
            module Demo

            trait Drawable
            {
                finite law i32[min max] Draw(borrow Self self);
            }

            struct Tool
            {
                fn T Pick<T>(T value) where T: Drawable
                {
                    return value;
                }
            }

            finite law u64[0 max] Run()
            {
                return comptime System.Compiler.MethodGenericParameterTraitBoundCount<Tool, 0, 1>();
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(traitBoundGenericIndexResult.Succeeded);
        Assert.Contains(traitBoundGenericIndexResult.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK3054"
            && diagnostic.Message.Contains("generic parameter index '1' is out of range", StringComparison.Ordinal));

        var traitBoundIndexResult = Compile(
            """
            module Demo

            trait Drawable
            {
                finite law i32[min max] Draw(borrow Self self);
            }

            struct Tool
            {
                fn T Pick<T>(T value) where T: Drawable
                {
                    return value;
                }
            }

            finite law bool Run()
            {
                return comptime System.Compiler.MethodGenericParameterTraitBoundTypeIsTrait<Tool, 0, 0, 1>();
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(traitBoundIndexResult.Succeeded);
        Assert.Contains(traitBoundIndexResult.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK3054"
            && diagnostic.Message.Contains("trait-bound index '1' is out of range", StringComparison.Ordinal));
    }

    [Fact]
    public void ComptimeIndexedNameFactsRejectOutOfRangeIndices()
    {
        var fieldResult = Compile(
            """
            module Demo

            struct Header
            {
                i32[min max] Tag;
            }

            finite law ascii Field()
            {
                return comptime System.Compiler.FieldName<Header, 1>();
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(fieldResult.Succeeded);
        Assert.Contains(fieldResult.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK3054"
            && diagnostic.Message.Contains("field index '1' is out of range", StringComparison.Ordinal));

        var variantResult = Compile(
            """
            module Demo

            enum Token
            {
                Empty
            }

            finite law ascii Variant()
            {
                return comptime System.Compiler.EnumVariantName<Token, 1>();
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(variantResult.Succeeded);
        Assert.Contains(variantResult.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK3054"
            && diagnostic.Message.Contains("variant index '1' is out of range", StringComparison.Ordinal));
    }

    [Fact]
    public void ComptimeEnumPayloadNameFactsRejectOutOfRangeIndices()
    {
        var payloadResult = Compile(
            """
            module Demo

            enum Token
            {
                Empty,
                Pair { value: i32[min max] }
            }

            finite law ascii Payload()
            {
                return comptime System.Compiler.EnumVariantPayloadName<Token, 1, 1>();
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(payloadResult.Succeeded);
        Assert.Contains(payloadResult.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK3054"
            && diagnostic.Message.Contains("payload index '1' is out of range", StringComparison.Ordinal));

        var layoutResult = Compile(
            """
            module Demo

            enum Token
            {
                Empty,
                Pair { value: i32[min max] }
            }

            finite law u64[0 max] Payload()
            {
                return comptime System.Compiler.EnumVariantPayloadOffset<Token, 1, 1>();
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(layoutResult.Succeeded);
        Assert.Contains(layoutResult.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK3054"
            && diagnostic.Message.Contains("payload index '1' is out of range", StringComparison.Ordinal));

        var tagResult = Compile(
            """
            module Demo

            enum Token
            {
                Empty
            }

            finite law u64[0 max] Tag()
            {
                return comptime System.Compiler.EnumVariantTag<Token, 1>();
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(tagResult.Succeeded);
        Assert.Contains(tagResult.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK3054"
            && diagnostic.Message.Contains("variant index '1' is out of range", StringComparison.Ordinal));

        var variantResult = Compile(
            """
            module Demo

            enum Token
            {
                Empty
            }

            finite law bool UsesNamed()
            {
                return comptime System.Compiler.EnumVariantUsesNamedFields<Token, 1>();
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(variantResult.Succeeded);
        Assert.Contains(variantResult.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK3054"
            && diagnostic.Message.Contains("variant index '1' is out of range", StringComparison.Ordinal));
    }

    [Fact]
    public void ComptimeEnumVariantFunnelFactsRejectOutOfRangeIndices()
    {
        var result = Compile(
            """
            module Demo

            enum IOError
            {
                Disk
            }

            enum ParseError
            {
                Io from IOError
            }

            finite law bool Run()
            {
                return comptime System.Compiler.EnumVariantIsErrorFunnel<ParseError, 1>();
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK3054"
            && diagnostic.Message.Contains("variant index '1' is out of range", StringComparison.Ordinal));
    }

    [Fact]
    public void ComptimeTypeGenericParameterFactsRejectOutOfRangeIndices()
    {
        var genericResult = Compile(
            """
            module Demo

            struct Box<T>
            {
                T Value;
            }

            finite law ascii Run()
            {
                return comptime System.Compiler.TypeGenericParameterName<Box<i32[min max]>, 1>();
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(genericResult.Succeeded);
        Assert.Contains(genericResult.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK3054"
            && diagnostic.Message.Contains("generic parameter index '1' is out of range", StringComparison.Ordinal));

        var comptimeResult = Compile(
            """
            module Demo

            struct Buffer<T, comptime u8[1 8] N>
            {
                T[N] Items;
            }

            finite law bool Run()
            {
                return comptime System.Compiler.TypeComptimeGenericParameterTypeIsInteger<Buffer<i32[min max], 4>, 1>();
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(comptimeResult.Succeeded);
        Assert.Contains(comptimeResult.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK3054"
            && diagnostic.Message.Contains("comptime generic parameter index '1' is out of range", StringComparison.Ordinal));

        var methodComptimeResult = Compile(
            """
            module Demo

            struct Tool
            {
                finite law T Pick<T, comptime u8[1 8] N>(T value)
                {
                    return value;
                }
            }

            finite law bool Run()
            {
                return comptime System.Compiler.MethodComptimeGenericParameterTypeIsInteger<Tool, 0, 1>();
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(methodComptimeResult.Succeeded);
        Assert.Contains(methodComptimeResult.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK3054"
            && diagnostic.Message.Contains("comptime generic parameter index '1' is out of range", StringComparison.Ordinal));

        var typeArgumentResult = Compile(
            """
            module Demo

            struct Box<T>
            {
                T Value;
            }

            finite law bool Run()
            {
                return comptime System.Compiler.TypeArgumentTypeIsInteger<Box<i32[min max]>, 1>();
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(typeArgumentResult.Succeeded);
        Assert.Contains(typeArgumentResult.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK3054"
            && diagnostic.Message.Contains("type argument index '1' is out of range", StringComparison.Ordinal));

        var typeArgumentMetadataResult = Compile(
            """
            module Demo

            struct Box<T>
            {
                T Value;
            }

            finite law ascii Run()
            {
                return comptime System.Compiler.TypeArgumentTypeDisplayName<Box<i32[min max]>, 1>();
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(typeArgumentMetadataResult.Succeeded);
        Assert.Contains(typeArgumentMetadataResult.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK3054"
            && diagnostic.Message.Contains("type argument index '1' is out of range", StringComparison.Ordinal));
    }

    [Fact]
    public void ComptimeIndexedStructuralFactsAreRejectedAtRuntime()
    {
        var result = Compile(
            """
            module Demo

            struct Header
            {
                i32[min max] Tag;
            }

            finite law u64[0 max] Run()
            {
                return System.Compiler.FieldOffset<Header, 0>();
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK3054"
            && diagnostic.Message.Contains("may only be used inside a `comptime` expression or block", StringComparison.Ordinal));

        var memoryResult = Compile(
            """
            module Demo

            alias Callback = fnptr<fn void(rawmutptr<i32[min max]>, rawmutptr<i32[min max]>)>;

            finite law bool Run()
            {
                return System.Compiler.FunctionPointerParametersAreDisjoint<Callback, 0, 1>();
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(memoryResult.Succeeded);
        Assert.Contains(memoryResult.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK3054"
            && diagnostic.Message.Contains("may only be used inside a `comptime` expression or block", StringComparison.Ordinal));
    }

    [Fact]
    public void ComptimeStructLayoutAttributeFactsAreRejectedAtRuntime()
    {
        var result = Compile(
            """
            module Demo

            [StructLayout(C)]
            struct Header
            {
                i32[min max] Tag;
            }

            finite law bool Run()
            {
                return System.Compiler.StructLayoutIsC<Header>();
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK3054"
            && diagnostic.Message.Contains("may only be used inside a `comptime` expression or block", StringComparison.Ordinal));
    }

    [Fact]
    public void ComptimeThreadSafetyLawAttributeFactsAreRejectedAtRuntime()
    {
        var result = Compile(
            """
            module Demo

            [Grant(Transferable)]
            struct Header
            {
                i32[min max] Tag;
            }

            finite law bool Run()
            {
                return System.Compiler.TypeThreadSafetyLawAttributeConditionTypeIsNamed<Header, 0>();
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK3054"
            && diagnostic.Message.Contains("may only be used inside a `comptime` expression or block", StringComparison.Ordinal));
    }

    [Fact]
    public void ComptimeMethodThreadSafetyLawPredicateFactsAreRejectedAtRuntime()
    {
        var result = Compile(
            """
            module Demo

            struct Gate
            {
                fn void Move() where Transferable(Gate)
                {
                }
            }

            finite law ascii Run()
            {
                return System.Compiler.MethodThreadSafetyLawPredicateTypeDisplayName<Gate, 0, 0>();
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK3054"
            && diagnostic.Message.Contains("may only be used inside a `comptime` expression or block", StringComparison.Ordinal));
    }

    [Fact]
    public void ComptimeIndexedTypeFactsAreRejectedAtRuntime()
    {
        var fieldResult = Compile(
            """
            module Demo

            struct Header
            {
                i32[min max] Tag;
            }

            finite law bool Run()
            {
                return System.Compiler.FieldTypeIs<Header, i32[min max], 0>();
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(fieldResult.Succeeded);
        Assert.Contains(fieldResult.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK3054"
            && diagnostic.Message.Contains("may only be used inside a `comptime` expression or block", StringComparison.Ordinal));

        var fieldQualifierResult = Compile(
            """
            module Demo

            struct Header
            {
                storeborrow i32[min max] Tag;
            }

            finite law bool Run()
            {
                return System.Compiler.FieldTypeBorrowKindIsStoreBorrow<Header, 0>();
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(fieldQualifierResult.Succeeded);
        Assert.Contains(fieldQualifierResult.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK3054"
            && diagnostic.Message.Contains("may only be used inside a `comptime` expression or block", StringComparison.Ordinal));

        var payloadResult = Compile(
            """
            module Demo

            enum Token
            {
                Number(i32[min max])
            }

            finite law bool Run()
            {
                return System.Compiler.EnumVariantPayloadTypeIsInteger<Token, 0, 0>();
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(payloadResult.Succeeded);
        Assert.Contains(payloadResult.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK3054"
            && diagnostic.Message.Contains("may only be used inside a `comptime` expression or block", StringComparison.Ordinal));

        var payloadQualifierResult = Compile(
            """
            module Demo

            enum Token
            {
                Number(shared i32[min max])
            }

            finite law bool Run()
            {
                return System.Compiler.EnumVariantPayloadTypeAccessKindIsShared<Token, 0, 0>();
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(payloadQualifierResult.Succeeded);
        Assert.Contains(payloadQualifierResult.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK3054"
            && diagnostic.Message.Contains("may only be used inside a `comptime` expression or block", StringComparison.Ordinal));
    }

    [Fact]
    public void ComptimeFunctionPointerStructuralFactsAreRejectedAtRuntime()
    {
        var result = Compile(
            """
            module Demo

            alias Callback = fnptr<fn i32[min max](i32[min max])>;

            finite law u64[0 max] Run()
            {
                return System.Compiler.FunctionPointerParameterCount<Callback>();
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK3054"
            && diagnostic.Message.Contains("may only be used inside a `comptime` expression or block", StringComparison.Ordinal));
    }

    [Fact]
    public void ComptimeIndexedNameFactsAreRejectedAtRuntime()
    {
        var result = Compile(
            """
            module Demo

            struct Header
            {
                i32[min max] Tag;
            }

            finite law ascii Run()
            {
                return System.Compiler.FieldName<Header, 0>();
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK3054"
            && diagnostic.Message.Contains("may only be used inside a `comptime` expression or block", StringComparison.Ordinal));
    }

    [Fact]
    public void ComptimeEnumPayloadNameFactsAreRejectedAtRuntime()
    {
        var result = Compile(
            """
            module Demo

            enum Token
            {
                Pair { value: i32[min max] }
            }

            finite law ascii Run()
            {
                return System.Compiler.EnumVariantPayloadName<Token, 0, 0>();
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK3054"
            && diagnostic.Message.Contains("may only be used inside a `comptime` expression or block", StringComparison.Ordinal));
    }

    [Fact]
    public void ComptimeEnumTagFactsAreRejectedAtRuntime()
    {
        var result = Compile(
            """
            module Demo

            enum Token
            {
                Empty
            }

            finite law u64[0 max] Run()
            {
                return System.Compiler.EnumTagSize<Token>();
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK3054"
            && diagnostic.Message.Contains("may only be used inside a `comptime` expression or block", StringComparison.Ordinal));
    }

    [Fact]
    public void ComptimeEnumPayloadLayoutFactsAreRejectedAtRuntime()
    {
        var result = Compile(
            """
            module Demo

            enum Token
            {
                Data(u8[0 max])
            }

            finite law u64[0 max] Run()
            {
                return System.Compiler.EnumVariantPayloadOffset<Token, 0, 0>();
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK3054"
            && diagnostic.Message.Contains("may only be used inside a `comptime` expression or block", StringComparison.Ordinal));
    }

    [Fact]
    public void ComptimeEnumVariantFunnelFactsAreRejectedAtRuntime()
    {
        var result = Compile(
            """
            module Demo

            enum IOError
            {
                Disk
            }

            enum ParseError
            {
                Io from IOError
            }

            finite law bool Run()
            {
                return System.Compiler.EnumVariantAbsorbsErrorTypeIs<ParseError, IOError, 0>();
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK3054"
            && diagnostic.Message.Contains("may only be used inside a `comptime` expression or block", StringComparison.Ordinal));
    }

    [Fact]
    public void ComptimeTypeGenericParameterFactsAreRejectedAtRuntime()
    {
        var result = Compile(
            """
            module Demo

            struct Buffer<T, comptime u8[1 8] N>
            {
                T[N] Items;
            }

            finite law bool Run()
            {
                return System.Compiler.TypeComptimeGenericParameterTypeIsInteger<Buffer<i32[min max], 4>, 0>();
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK3054"
            && diagnostic.Message.Contains("may only be used inside a `comptime` expression or block", StringComparison.Ordinal));

        var typeArgumentMetadataResult = Compile(
            """
            module Demo

            struct Box<T>
            {
                T Value;
            }

            finite law ascii Run()
            {
                return System.Compiler.TypeArgumentTypeDisplayName<Box<i32[min max]>, 0>();
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(typeArgumentMetadataResult.Succeeded);
        Assert.Contains(typeArgumentMetadataResult.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK3054"
            && diagnostic.Message.Contains("may only be used inside a `comptime` expression or block", StringComparison.Ordinal));

        var comptimeArgumentPredicateResult = Compile(
            """
            module Demo

            struct Buffer<T, comptime u8[1 8] N>
            {
                T[N] Items;
            }

            finite law bool Run()
            {
                return System.Compiler.TypeComptimeArgumentTypeIsInteger<Buffer<i32[min max], 4>, 0>();
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(comptimeArgumentPredicateResult.Succeeded);
        Assert.Contains(comptimeArgumentPredicateResult.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK3054"
            && diagnostic.Message.Contains("may only be used inside a `comptime` expression or block", StringComparison.Ordinal));

        var methodResult = Compile(
            """
            module Demo

            trait Drawable
            {
                finite law i32[min max] Draw(borrow Self self);
            }

            struct Tool
            {
                fn T Pick<T>(T value) where T: Drawable
                {
                    return value;
                }
            }

            finite law bool Run()
            {
                return System.Compiler.MethodGenericParameterTraitBoundTypeIsTrait<Tool, 0, 0, 0>();
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(methodResult.Succeeded);
        Assert.Contains(methodResult.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK3054"
            && diagnostic.Message.Contains("may only be used inside a `comptime` expression or block", StringComparison.Ordinal));

        var methodComptimeResult = Compile(
            """
            module Demo

            struct Tool
            {
                finite law T Pick<T, comptime u8[1 8] N>(T value)
                {
                    return value;
                }
            }

            finite law bool Run()
            {
                return System.Compiler.MethodComptimeGenericParameterTypeIsInteger<Tool, 0, 0>();
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(methodComptimeResult.Succeeded);
        Assert.Contains(methodComptimeResult.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK3054"
            && diagnostic.Message.Contains("may only be used inside a `comptime` expression or block", StringComparison.Ordinal));
    }

    [Fact]
    public void ComptimeAssociatedTypeFactsFoldToConstants()
    {
        var llvm = CompileToLlvm(
            """
            module Demo

            struct Payload
            {
                i32[min max] Value;
            }

            struct Holder<T, comptime u8[1 8] N>
            {
                T[N] Items;
            }

            trait Reader
            {
                alias Item;
                alias Code = u64[0 max];
                alias PayloadType = Payload;
                alias ZBuffer = Holder<u8[0 max], 4>;
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

            struct Box<T>
            {
                alias Item = Holder<T, 4>;

                T Value;
            }

            finite law i64[min max] AssociatedScore<Contract, Impl, GenericBox>()
            {
                if (!(comptime System.Compiler.AssociatedTypeCount<Contract>() == 4
                    && comptime System.Compiler.AssociatedTypeCount<Impl>() == 1
                    && comptime (System.Compiler.AssociatedTypeName<Contract, 0>() == "Code")
                    && comptime (System.Compiler.AssociatedTypeName<Contract, 1>() == "Item")
                    && comptime (System.Compiler.AssociatedTypeName<Contract, 2>() == "PayloadType")
                    && comptime (System.Compiler.AssociatedTypeName<Contract, 3>() == "ZBuffer")
                    && comptime System.Compiler.AssociatedTypeHasTarget<Contract, 0>()
                    && comptime !System.Compiler.AssociatedTypeHasTarget<Contract, 1>()))
                {
                    return 1;
                }

                if (!(comptime System.Compiler.AssociatedTypeTargetTypeIs<Contract, u64[0 max], 0>()))
                {
                    return 21;
                }

                if (!(comptime System.Compiler.AssociatedTypeTargetTypeIs<Impl, i32[min max], 0>()))
                {
                    return 22;
                }

                if (!(comptime System.Compiler.AssociatedTypeTargetTypeIs<GenericBox, Holder<i32[min max], 4>, 0>()))
                {
                    return 23;
                }

                if (!(comptime System.Compiler.AssociatedTypeTargetTypeIsInteger<Contract, 0>()))
                {
                    return 24;
                }

                if (comptime System.Compiler.AssociatedTypeTargetTypeIsInteger<Contract, 1>())
                {
                    return 25;
                }

                if (!(comptime System.Compiler.AssociatedTypeTargetTypeIsNamed<Contract, 2>()))
                {
                    return 26;
                }

                if (!(comptime System.Compiler.AssociatedTypeTargetTypeIsStruct<Contract, 2>()))
                {
                    return 27;
                }

                if (!(comptime System.Compiler.AssociatedTypeTargetTypeHasConcreteLayout<Contract, 2>()))
                {
                    return 28;
                }

                if (!(comptime (System.Compiler.AssociatedTypeTargetTypeDisplayName<Contract, 1>() == "")
                    && comptime !System.Compiler.AssociatedTypeTargetTypeIsGenericInstantiation<Contract, 1>()))
                {
                    return 3;
                }

                if (!(comptime (System.Compiler.AssociatedTypeTargetTypeDisplayName<Contract, 3>() == "Holder<u8, 4>")
                    && comptime (System.Compiler.AssociatedTypeTargetTypeBaseName<Contract, 3>() == "Holder")
                    && comptime System.Compiler.AssociatedTypeTargetTypeIsGenericInstantiation<Contract, 3>()
                    && comptime System.Compiler.AssociatedTypeTargetTypeArgumentCount<Contract, 3>() == 1
                    && comptime System.Compiler.AssociatedTypeTargetTypeComptimeArgumentCount<Contract, 3>() == 1))
                {
                    return 4;
                }

                if (!(comptime System.Compiler.AssociatedTypeTargetTypeIsGenericInstantiation<GenericBox, 0>()
                    && comptime System.Compiler.AssociatedTypeTargetTypeArgumentCount<GenericBox, 0>() == 1
                    && comptime System.Compiler.AssociatedTypeTargetTypeComptimeArgumentCount<GenericBox, 0>() == 1
                    && comptime System.Compiler.AssociatedTypeTargetTypeHasConcreteLayout<GenericBox, 0>()))
                {
                    return 5;
                }

                return 42;
            }

            finite law i64[min max] Run()
            {
                return comptime AssociatedScore<Reader, Counter, Box<i32[min max]>>();
            }
            """,
            new CompilerOptions(OptimizationLevel: CompilerOptimizationLevel.O0));

        var runBody = ExtractDefinitionBody(llvm, "Run");
        Assert.Contains("ret i64 42", runBody);
        Assert.DoesNotContain("call", runBody);
    }

    [Fact]
    public void ComptimeAssociatedTypeTargetCategoryFactsRejectOutOfRangeIndices()
    {
        var result = Compile(
            """
            module Demo

            trait Reader
            {
                alias Item;
            }

            finite law bool Run()
            {
                return comptime System.Compiler.AssociatedTypeTargetTypeIsInteger<Reader, 1>();
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK3054"
            && diagnostic.Message.Contains("associated type index '1' is out of range", StringComparison.Ordinal));
    }

    [Fact]
    public void ComptimeAssociatedTypeTargetMetadataFactsRejectOutOfRangeIndices()
    {
        var result = Compile(
            """
            module Demo

            trait Reader
            {
                alias Item = i32[min max];
            }

            finite law ascii Run()
            {
                return comptime System.Compiler.AssociatedTypeTargetTypeDisplayName<Reader, 1>();
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK3054"
            && diagnostic.Message.Contains("associated type index '1' is out of range", StringComparison.Ordinal));
    }

    [Fact]
    public void ComptimeAssociatedTypeFactsRejectOutOfRangeIndices()
    {
        var result = Compile(
            """
            module Demo

            trait Reader
            {
                alias Item;
            }

            finite law ascii Run()
            {
                return comptime System.Compiler.AssociatedTypeName<Reader, 1>();
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK3054"
            && diagnostic.Message.Contains("associated type index '1' is out of range", StringComparison.Ordinal));
    }

    [Fact]
    public void ComptimeAssociatedTypeFactsAreRejectedAtRuntime()
    {
        var result = Compile(
            """
            module Demo

            trait Reader
            {
                alias Item;
            }

            finite law u64[0 max] Run()
            {
                return System.Compiler.AssociatedTypeCount<Reader>();
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK3054"
            && diagnostic.Message.Contains("may only be used inside a `comptime` expression or block", StringComparison.Ordinal));
    }

    [Fact]
    public void ComptimeTraitConformanceFactFoldsToBoolConstant()
    {
        var llvm = CompileToLlvm(
            """
            module Demo

            trait Drawable
            {
                finite law i32[min max] Width(borrow Self self);
            }

            trait Tagged<T, comptime u8[1 8] N>
            {
            }

            struct Widget : Drawable, Tagged<i32[min max], 4>
            {
                i32[min max] W;

                finite law i32[min max] Width(borrow Widget self)
                {
                    return self.W;
                }
            }

            struct Plain
            {
                i32[min max] W;
            }

            finite law i32[min max] Run()
            {
                if (comptime System.Compiler.Implements<Widget, Drawable>())
                {
                    if (comptime System.Compiler.Implements<Widget, Tagged<i32[min max], 4>>())
                    {
                        if (comptime !System.Compiler.Implements<Plain, Drawable>())
                        {
                            if (comptime System.Compiler.ImplementedTraitCount<Widget>() == 2)
                            {
                                if (comptime System.Compiler.ImplementedTraitTypeIs<Widget, Drawable, 0>())
                                {
                                    if (comptime System.Compiler.ImplementedTraitTypeIs<Widget, Tagged<i32[min max], 4>, 1>())
                                    {
                                        if (comptime System.Compiler.ImplementedTraitTypeIsNamed<Widget, 0>()
                                            && comptime System.Compiler.ImplementedTraitTypeIsTrait<Widget, 0>()
                                            && comptime !System.Compiler.ImplementedTraitTypeHasConcreteLayout<Widget, 0>())
                                        {
                                            if (comptime System.Compiler.ImplementedTraitTypeIsNamed<Widget, 1>()
                                                && comptime System.Compiler.ImplementedTraitTypeIsTrait<Widget, 1>()
                                                && comptime !System.Compiler.ImplementedTraitTypeHasConcreteLayout<Widget, 1>())
                                            {
                                                if (comptime (System.Compiler.ImplementedTraitTypeDisplayName<Widget, 1>() == "Tagged<i32, 4>"))
                                                {
                                                    if (comptime (System.Compiler.ImplementedTraitTypeBaseName<Widget, 1>() == "Tagged"))
                                                    {
                                                        if (comptime System.Compiler.ImplementedTraitTypeIsGenericInstantiation<Widget, 1>())
                                                        {
                                                            if (comptime System.Compiler.ImplementedTraitTypeArgumentCount<Widget, 1>() == 1)
                                                            {
                                                                if (comptime System.Compiler.ImplementedTraitTypeComptimeArgumentCount<Widget, 1>() == 1)
                                                                {
                                                                    if (comptime System.Compiler.ImplementedTraitTypeArgumentTypeIs<Widget, i32[min max], 1, 0>()
                                                                        && comptime System.Compiler.ImplementedTraitTypeArgumentTypeIsInteger<Widget, 1, 0>()
                                                                        && comptime System.Compiler.ImplementedTraitTypeArgumentTypeHasConcreteLayout<Widget, 1, 0>()
                                                                        && comptime (System.Compiler.ImplementedTraitTypeArgumentTypeDisplayName<Widget, 1, 0>() == "i32")
                                                                        && comptime (System.Compiler.ImplementedTraitTypeArgumentTypeBaseName<Widget, 1, 0>() == "")
                                                                        && comptime (System.Compiler.ImplementedTraitTypeArgumentTypeModuleName<Widget, 1, 0>() == "")
                                                                        && comptime !System.Compiler.ImplementedTraitTypeArgumentTypeIsGenericInstantiation<Widget, 1, 0>()
                                                                        && comptime System.Compiler.ImplementedTraitTypeArgumentTypeArgumentCount<Widget, 1, 0>() == 0
                                                                        && comptime System.Compiler.ImplementedTraitTypeArgumentTypeComptimeArgumentCount<Widget, 1, 0>() == 0
                                                                        && comptime (System.Compiler.ImplementedTraitTypeComptimeArgumentName<Widget, 1, 0>() == "N")
                                                                        && comptime System.Compiler.ImplementedTraitTypeComptimeArgumentTypeIs<Widget, u8[1 8], 1, 0>()
                                                                        && comptime System.Compiler.ImplementedTraitTypeComptimeArgumentTypeIsInteger<Widget, 1, 0>()
                                                                        && comptime System.Compiler.ImplementedTraitTypeComptimeArgumentTypeHasConcreteLayout<Widget, 1, 0>()
                                                                        && comptime (System.Compiler.ImplementedTraitTypeComptimeArgumentTypeDisplayName<Widget, 1, 0>() == "u8[1 8]")
                                                                        && comptime (System.Compiler.ImplementedTraitTypeComptimeArgumentTypeBaseName<Widget, 1, 0>() == "")
                                                                        && comptime (System.Compiler.ImplementedTraitTypeComptimeArgumentTypeModuleName<Widget, 1, 0>() == "")
                                                                        && comptime !System.Compiler.ImplementedTraitTypeComptimeArgumentTypeIsGenericInstantiation<Widget, 1, 0>()
                                                                        && comptime System.Compiler.ImplementedTraitTypeComptimeArgumentTypeArgumentCount<Widget, 1, 0>() == 0
                                                                        && comptime System.Compiler.ImplementedTraitTypeComptimeArgumentTypeComptimeArgumentCount<Widget, 1, 0>() == 0
                                                                        && comptime System.Compiler.ImplementedTraitTypeComptimeArgumentValueIs<Widget, 1, 0, 4>())
                                                                    {
                                                                        return 42;
                                                                    }
                                                                }
                                                            }
                                                        }
                                                    }
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }

                return 0;
            }
            """,
            new CompilerOptions(OptimizationLevel: CompilerOptimizationLevel.O0));

        var runBody = ExtractDefinitionBody(llvm, "Run");
        Assert.Contains("ret i32 42", runBody);
        Assert.DoesNotContain("call", runBody);
    }

    [Fact]
    public void ComptimeDynTraitStructuralFactsFoldToBoolConstants()
    {
        var llvm = CompileToLlvm(
            """
            module Demo

            dyn trait Speaker
            {
                finite law i32[min max] Speak(borrow Self self);
            }

            dyn trait Listener
            {
                finite law bool Listen(borrow Self self);
            }

            finite law bool IsHeapDyn<T>()
            {
                return comptime System.Compiler.DynTraitIsHeap<T>();
            }

            finite law i32[min max] Run()
            {
                if (comptime System.Compiler.IsDynTrait<borrow dyn Speaker>())
                {
                    if (comptime System.Compiler.IsDynTrait<heap dyn Speaker>())
                    {
                        if (comptime !System.Compiler.IsDynTrait<Speaker>())
                        {
                            if (comptime !System.Compiler.IsDynTrait<i32[min max]>())
                            {
                                if (comptime System.Compiler.DynTraitIsView<borrow dyn Speaker>())
                                {
                                    if (comptime !System.Compiler.DynTraitIsHeap<borrow dyn Speaker>())
                                    {
                                        if (comptime IsHeapDyn<heap dyn Speaker>())
                                        {
                                            if (comptime !System.Compiler.DynTraitIsView<heap dyn Speaker>())
                                            {
                                                if (comptime System.Compiler.DynTraitTargetTypeIs<borrow dyn Speaker, Speaker>())
                                                {
                                                    if (comptime System.Compiler.DynTraitTargetTypeIs<heap dyn Listener, Listener>())
                                                    {
                                                        if (comptime !System.Compiler.DynTraitTargetTypeIs<heap dyn Listener, Speaker>())
                                                        {
                                                            return 42;
                                                        }
                                                    }
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }

                return 0;
            }
            """,
            new CompilerOptions(OptimizationLevel: CompilerOptimizationLevel.O0));

        var runBody = ExtractDefinitionBody(llvm, "Run");
        Assert.Contains("ret i32 42", runBody);
        Assert.DoesNotContain("call", runBody);
    }

    [Fact]
    public void ComptimeDynTraitNestedTypePredicateFactsFoldToBoolConstants()
    {
        var llvm = CompileToLlvm(
            """
            module Demo

            dyn trait Speaker
            {
                finite law i32[min max] Speak(borrow Self self);
            }

            struct Holder<T>
            {
                T Value;
            }

            struct Carrier
            {
                heap dyn Speaker Owned;
            }

            enum Packet
            {
                Owned(heap dyn Speaker),
                Count(i32[min max])
            }

            trait Contract
            {
                alias View = heap dyn Speaker;
            }

            struct Service
            {
                finite law heap dyn Speaker Current(borrow Service self);

                finite law void Observe(borrow Service self, borrow dyn Speaker value);

                finite law Holder<heap dyn Speaker> Wrap(borrow Service self, Holder<heap dyn Speaker> input);
            }

            alias Callback = fnptr<fn heap dyn Speaker(borrow dyn Speaker)>;
            alias Handler = borrow closure<fn heap dyn Speaker(borrow dyn Speaker)>;
            alias Boxed = Holder<heap dyn Speaker>;
            alias NestedCallback = fnptr<fn Holder<heap dyn Speaker>(Holder<heap dyn Speaker>)>;
            alias NestedHandler = borrow closure<fn Holder<heap dyn Speaker>(Holder<heap dyn Speaker>)>;
            alias DynArray = heap dyn Speaker[2];
            alias DynPointer = rawptr<heap dyn Speaker>;

            finite law i32[min max] Run()
            {
                if (comptime System.Compiler.FieldTypeIsDynTrait<Carrier, 0>())
                {
                    if (comptime System.Compiler.EnumVariantPayloadTypeIsDynTrait<Packet, 0, 0>())
                    {
                        if (comptime System.Compiler.AssociatedTypeTargetTypeIsDynTrait<Contract, 0>())
                        {
                            if (comptime System.Compiler.MethodReturnTypeIsDynTrait<Service, 0>())
                            {
                                if (comptime System.Compiler.MethodParameterTypeIsDynTrait<Service, 1, 1>())
                                {
                                    if (comptime System.Compiler.FunctionPointerReturnTypeIsDynTrait<Callback>())
                                    {
                                        if (comptime System.Compiler.FunctionPointerParameterTypeIsDynTrait<Callback, 0>())
                                        {
                                            if (comptime System.Compiler.ClosureReturnTypeIsDynTrait<Handler>())
                                            {
                                                if (comptime System.Compiler.ClosureParameterTypeIsDynTrait<Handler, 0>())
                                                {
                                                    if (comptime System.Compiler.TypeArgumentTypeIsDynTrait<Boxed, 0>())
                                                    {
                                                        if (comptime System.Compiler.TypeElementTypeIsDynTrait<DynArray>())
                                                        {
                                                            if (comptime System.Compiler.RawPointerElementTypeIsDynTrait<DynPointer>())
                                                            {
                                                                if (comptime System.Compiler.FunctionPointerReturnTypeArgumentTypeIsDynTrait<NestedCallback, 0>())
                                                                {
                                                                    if (comptime System.Compiler.FunctionPointerParameterTypeArgumentTypeIsDynTrait<NestedCallback, 0, 0>())
                                                                    {
                                                                        if (comptime System.Compiler.ClosureReturnTypeArgumentTypeIsDynTrait<NestedHandler, 0>())
                                                                        {
                                                                            if (comptime System.Compiler.ClosureParameterTypeArgumentTypeIsDynTrait<NestedHandler, 0, 0>())
                                                                            {
                                                                                if (comptime System.Compiler.MethodReturnTypeArgumentTypeIsDynTrait<Service, 2, 0>())
                                                                                {
                                                                                    if (comptime System.Compiler.MethodParameterTypeArgumentTypeIsDynTrait<Service, 2, 1, 0>())
                                                                                    {
                                                                                        return 42;
                                                                                    }
                                                                                }
                                                                            }
                                                                        }
                                                                    }
                                                                }
                                                            }
                                                        }
                                                    }
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }

                return 0;
            }
            """,
            new CompilerOptions(OptimizationLevel: CompilerOptimizationLevel.O0));

        var runBody = ExtractDefinitionBody(llvm, "Run");
        Assert.Contains("ret i32 42", runBody);
        Assert.DoesNotContain("call", runBody);
    }

    [Fact]
    public void GenericComptimeLawCanBranchOnTraitConformanceFact()
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

            struct Plain
            {
                i32[min max] W;
            }

            finite law i32[min max] ShapeScore<T>()
            {
                if (comptime System.Compiler.Implements<T, Drawable>())
                {
                    return 7;
                }

                return 3;
            }

            finite law i32[min max] RunWidget()
            {
                return comptime ShapeScore<Widget>();
            }

            finite law i32[min max] RunPlain()
            {
                return comptime ShapeScore<Plain>();
            }
            """,
            new CompilerOptions(OptimizationLevel: CompilerOptimizationLevel.O0));

        var widgetBody = ExtractDefinitionBody(llvm, "RunWidget");
        Assert.Contains("ret i32 7", widgetBody);
        Assert.DoesNotContain("call", widgetBody);

        var plainBody = ExtractDefinitionBody(llvm, "RunPlain");
        Assert.Contains("ret i32 3", plainBody);
        Assert.DoesNotContain("call", plainBody);
    }

    [Fact]
    public void ComptimeTraitConformanceFactRequiresTraitArgument()
    {
        var result = Compile(
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

            finite law bool Run()
            {
                return comptime System.Compiler.Implements<Widget, Widget>();
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK3054"
            && diagnostic.Message.Contains("requires a trait type as its second argument", StringComparison.Ordinal));
    }

    [Fact]
    public void ComptimeImplementedTraitStructuralFactsValidateIndexedArguments()
    {
        var indexResult = Compile(
            """
            module Demo

            trait Drawable
            {
            }

            struct Widget : Drawable
            {
                i32[min max] W;
            }

            finite law bool Run()
            {
                return comptime System.Compiler.ImplementedTraitTypeIsTrait<Widget, 1>();
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(indexResult.Succeeded);
        Assert.Contains(indexResult.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK3054"
            && diagnostic.Message.Contains("implemented-trait index '1' is out of range", StringComparison.Ordinal));

        var typeArgumentIndexResult = Compile(
            """
            module Demo

            trait Tagged<T, comptime u8[1 8] N>
            {
            }

            struct Widget : Tagged<i32[min max], 4>
            {
                i32[min max] W;
            }

            finite law bool Run()
            {
                return comptime System.Compiler.ImplementedTraitTypeArgumentTypeIsInteger<Widget, 0, 1>();
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(typeArgumentIndexResult.Succeeded);
        Assert.Contains(typeArgumentIndexResult.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK3054"
            && diagnostic.Message.Contains("implemented-trait type argument index '1' is out of range", StringComparison.Ordinal));

        var comptimeArgumentIndexResult = Compile(
            """
            module Demo

            trait Tagged<T, comptime u8[1 8] N>
            {
            }

            struct Widget : Tagged<i32[min max], 4>
            {
                i32[min max] W;
            }

            finite law bool Run()
            {
                return comptime System.Compiler.ImplementedTraitTypeComptimeArgumentTypeIsInteger<Widget, 0, 1>();
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(comptimeArgumentIndexResult.Succeeded);
        Assert.Contains(comptimeArgumentIndexResult.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK3054"
            && diagnostic.Message.Contains("implemented-trait comptime argument index '1' is out of range", StringComparison.Ordinal));

        var traitArgumentResult = Compile(
            """
            module Demo

            trait Drawable
            {
            }

            struct Widget : Drawable
            {
                i32[min max] W;
            }

            finite law bool Run()
            {
                return comptime System.Compiler.ImplementedTraitTypeIs<Widget, Widget, 0>();
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(traitArgumentResult.Succeeded);
        Assert.Contains(traitArgumentResult.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK3054"
            && diagnostic.Message.Contains("requires a trait type as its second argument", StringComparison.Ordinal));
    }

    [Fact]
    public void ComptimeDynTraitTargetFactRejectsWrongTargetAndSecondArgument()
    {
        var targetResult = Compile(
            """
            module Demo

            dyn trait Speaker
            {
                finite law i32[min max] Speak(borrow Self self);
            }

            finite law bool Run()
            {
                return comptime System.Compiler.DynTraitTargetTypeIs<i32[min max], Speaker>();
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(targetResult.Succeeded);
        Assert.Contains(targetResult.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK3054"
            && diagnostic.Message.Contains("requires a dyn trait object type", StringComparison.Ordinal));

        var secondArgumentResult = Compile(
            """
            module Demo

            dyn trait Speaker
            {
                finite law i32[min max] Speak(borrow Self self);
            }

            struct Widget
            {
                i32[min max] Value;
            }

            finite law bool Run()
            {
                return comptime System.Compiler.DynTraitTargetTypeIs<borrow dyn Speaker, Widget>();
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(secondArgumentResult.Succeeded);
        Assert.Contains(secondArgumentResult.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK3054"
            && diagnostic.Message.Contains("requires a trait type as its second argument", StringComparison.Ordinal));
    }

    [Fact]
    public void ComptimeTraitConformanceFactIsRejectedAtRuntime()
    {
        var result = Compile(
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

            finite law bool Run()
            {
                return System.Compiler.Implements<Widget, Drawable>();
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK3054"
            && diagnostic.Message.Contains("may only be used inside a `comptime` expression or block", StringComparison.Ordinal));
    }

    [Fact]
    public void ComptimeImplementedTraitStructuralFactsAreRejectedAtRuntime()
    {
        var result = Compile(
            """
            module Demo

            trait Tagged<T, comptime u8[1 8] N>
            {
            }

            struct Widget : Tagged<i32[min max], 4>
            {
                i32[min max] W;
            }

            finite law bool Run()
            {
                return System.Compiler.ImplementedTraitTypeArgumentTypeIsInteger<Widget, 0, 0>();
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK3054"
            && diagnostic.Message.Contains("may only be used inside a `comptime` expression or block", StringComparison.Ordinal));
    }

    [Fact]
    public void ComptimeDynTraitStructuralFactsAreRejectedAtRuntime()
    {
        var result = Compile(
            """
            module Demo

            dyn trait Speaker
            {
                finite law i32[min max] Speak(borrow Self self);
            }

            finite law bool Run()
            {
                return System.Compiler.DynTraitIsHeap<heap dyn Speaker>();
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK3054"
            && diagnostic.Message.Contains("may only be used inside a `comptime` expression or block", StringComparison.Ordinal));
    }

    [Fact]
    public void GenericComptimeLawCanBranchOnStructuralFactsAndLayout()
    {
        var llvm = CompileToLlvm(
            """
            module Demo

            struct Pair
            {
                i32[min max] Left;
                i32[min max] Right;
            }

            finite law i64[min max] Score<T>()
            {
                if (comptime System.Compiler.IsInteger<T>())
                {
                    return sizeof(T);
                }

                if (comptime System.Compiler.IsStruct<T>())
                {
                    return sizeof(T) + alignof(T);
                }

                return 0;
            }

            finite law i64[min max] RunInteger()
            {
                return comptime Score<i32[min max]>();
            }

            finite law i64[min max] RunStruct()
            {
                return comptime Score<Pair>();
            }
            """,
            new CompilerOptions(OptimizationLevel: CompilerOptimizationLevel.O0));

        var integerBody = ExtractDefinitionBody(llvm, "RunInteger");
        Assert.Contains("ret i64 4", integerBody);
        Assert.DoesNotContain("call", integerBody);

        var structBody = ExtractDefinitionBody(llvm, "RunStruct");
        Assert.Contains("ret i64 12", structBody);
        Assert.DoesNotContain("call", structBody);
    }

    [Fact]
    public void ComptimeBlockEvaluatesLocalStateAndOuterConst()
    {
        var llvm = CompileToLlvm(
            """
            module Demo

            const Seed = 40;

            finite law i32[min max] Run()
            {
                return comptime
                {
                    stack mut i32[min max] value = Seed;
                    value = value + 2;
                    return value;
                };
            }
            """,
            new CompilerOptions(OptimizationLevel: CompilerOptimizationLevel.O0));

        var runBody = ExtractDefinitionBody(llvm, "Run");
        Assert.Contains("ret i32 42", runBody);
    }

    [Fact]
    public void ComptimeBlockCanInitializeTypedConstGlobal()
    {
        var llvm = CompileToLlvm(
            """
            module Demo

            const u8 Answer = comptime
            {
                const base = 21;
                return base * 2;
            };

            finite law i32[min max] Run()
            {
                return Answer;
            }
            """,
            new CompilerOptions(OptimizationLevel: CompilerOptimizationLevel.O0));

        Assert.Contains("@Answer = local_unnamed_addr constant i8 42", llvm);
        var runBody = ExtractDefinitionBody(llvm, "Run");
        Assert.Contains("ret i32 42", runBody);
    }

    [Fact]
    public void ComptimeBlockCanMaterializeFixedArrayForIndexing()
    {
        var llvm = CompileToLlvm(
            """
            module Demo

            finite law i32[min max] Run()
            {
                return (comptime
                {
                    const i32[min max][3] values =
                    {
                        4,
                        8,
                        15
                    };
                    return values;
                })[2];
            }
            """,
            new CompilerOptions(OptimizationLevel: CompilerOptimizationLevel.O0));

        var runBody = ExtractDefinitionBody(llvm, "Run");
        Assert.Contains("extractvalue [3 x i32]", runBody);
        Assert.Contains("ret i32 %", runBody);
    }

    [Fact]
    public void ComptimeBlockCanGenerateFixedArrayTableWithWillexitForLoop()
    {
        var llvm = CompileToLlvm(
            """
            module Demo

            const Values = comptime
            {
                stack mut i32[min max][4] values;
                for willexit (stack mut i32[min max] index = 0; index < 4; index += 1)
                {
                    values[index] = index * 3;
                }

                return values;
            };

            finite law i32[min max] Run()
            {
                return Values[3];
            }
            """,
            new CompilerOptions(OptimizationLevel: CompilerOptimizationLevel.O0));

        Assert.Contains("@Values = local_unnamed_addr constant [4 x i32] [i32 0, i32 3, i32 6, i32 9]", llvm);
        var runBody = ExtractDefinitionBody(llvm, "Run");
        Assert.DoesNotContain(" br ", runBody);
        Assert.DoesNotContain("call fastcc", runBody);
    }

    [Fact]
    public void ComptimeBlockCanExecuteIndexedForInTraversal()
    {
        var llvm = CompileToLlvm(
            """
            module Demo

            finite law i32[min max] Run()
            {
                return comptime
                {
                    stack i32[min max][3] values =
                    {
                        5,
                        10,
                        20
                    };
                    stack mut i32[min max] total = 0;
                    for willexit (stack i32[min max] index, borrow i32[min max] value in values)
                    {
                        total += value + index;
                    }

                    return total;
                };
            }
            """,
            new CompilerOptions(OptimizationLevel: CompilerOptimizationLevel.O0));

        var runBody = ExtractDefinitionBody(llvm, "Run");
        Assert.Contains("ret i32 38", runBody);
        Assert.DoesNotContain(" br ", runBody);
        Assert.DoesNotContain("call fastcc", runBody);
    }

    [Fact]
    public void ComptimeBlockCanExecuteMutableForInTraversalWithNestedPlaceUpdates()
    {
        var llvm = CompileToLlvm(
            """
            module Demo

            struct Box
            {
                i32[min max] Value;
            }

            finite law i32[min max] Run()
            {
                return comptime
                {
                    stack mut Box[3] boxes =
                    {
                        new Box() { Value = 1 },
                        new Box() { Value = 2 },
                        new Box() { Value = 3 }
                    };

                    for willexit (stack i32[min max] index, borrow mut Box box in boxes)
                    {
                        box.Value += index + 1;
                    }

                    boxes[1].Value += 5;
                    return boxes[0].Value * 100 + boxes[1].Value * 10 + boxes[2].Value;
                };
            }
            """,
            new CompilerOptions(OptimizationLevel: CompilerOptimizationLevel.O0));

        var runBody = ExtractDefinitionBody(llvm, "Run");
        Assert.Contains("ret i32 296", runBody);
        Assert.DoesNotContain(" br ", runBody);
        Assert.DoesNotContain("call fastcc", runBody);
    }

    [Fact]
    public void ComptimeBlockCanUseWillexitWhileBreakContinueAndCompoundAssignment()
    {
        var llvm = CompileToLlvm(
            """
            module Demo

            finite law i32[min max] Run()
            {
                return comptime
                {
                    stack mut i32[min max] value = 0;
                    stack mut i32[min max] index = 0;
                    while willexit (index < 8)
                    {
                        index += 1;
                        if (index == 3)
                        {
                            continue;
                        }

                        value += index;
                        if (value > 10)
                        {
                            break;
                        }
                    }

                    return value;
                };
            }
            """,
            new CompilerOptions(OptimizationLevel: CompilerOptimizationLevel.O0));

        var runBody = ExtractDefinitionBody(llvm, "Run");
        Assert.Contains("ret i32 12", runBody);
        Assert.DoesNotContain(" br ", runBody);
    }

    [Fact]
    public void ComptimeBlockCanExecuteLabeledBreakAndContinue()
    {
        var llvm = CompileToLlvm(
            """
            module Demo

            finite law i32[min max] Run()
            {
                return comptime
                {
                    stack mut i32[min max] total = 0;
                    stack mut i32[min max] outer = 0;
                    outerLoop: while willexit (outer < 4)
                    {
                        outer += 1;
                        stack mut i32[min max] inner = 0;
                        while willexit (inner < 4)
                        {
                            inner += 1;
                            if (inner == 1)
                            {
                                continue;
                            }

                            gate: switch (inner)
                            {
                                default:
                                    if (inner == 2)
                                    {
                                        break gate;
                                    }

                                    total += 0;
                            }

                            if (outer == 2)
                            {
                                if (inner == 2)
                                {
                                    continue outerLoop;
                                }
                            }

                            if (outer == 4)
                            {
                                break outerLoop;
                            }

                            total += outer * 10 + inner;
                        }
                    }

                    return total;
                };
            }
            """,
            new CompilerOptions(OptimizationLevel: CompilerOptimizationLevel.O0));

        var runBody = ExtractDefinitionBody(llvm, "Run");
        Assert.Contains("ret i32 138", runBody);
        Assert.DoesNotContain(" br ", runBody);
        Assert.DoesNotContain("call fastcc", runBody);
    }

    [Fact]
    public void ComptimeWillexitWhileReportsIterationBudgetExhaustion()
    {
        var result = Compile(
            """
            module Demo

            finite law i32[min max] Run()
            {
                return comptime
                {
                    stack mut i32[min max] value = 0;
                    while willexit (true)
                    {
                        value += 1;
                    }

                    return value;
                };
            }
            """,
            new CompilerOptions(
                StopAfterPassId: "type-check",
                MaximumCompileTimeLoopIterations: 8));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK3053"
            && diagnostic.Message.Contains("stopped after 8 iteration(s) of a `while willexit` loop", StringComparison.Ordinal));
    }

    [Fact]
    public void ComptimeWillexitForReportsIterationBudgetExhaustion()
    {
        var result = Compile(
            """
            module Demo

            finite law i32[min max] Run()
            {
                return comptime
                {
                    stack mut i32[min max] value = 0;
                    for willexit (;;)
                    {
                        value += 1;
                    }

                    return value;
                };
            }
            """,
            new CompilerOptions(
                StopAfterPassId: "type-check",
                MaximumCompileTimeLoopIterations: 8));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK3053"
            && diagnostic.Message.Contains("stopped after 8 iteration(s) of a `for willexit` loop", StringComparison.Ordinal));
    }

    [Fact]
    public void ComptimeWillexitForTraversalReportsIterationBudgetExhaustion()
    {
        var result = Compile(
            """
            module Demo

            finite law i32[min max] Run()
            {
                return comptime
                {
                    stack i32[min max][3] values =
                    {
                        1,
                        2,
                        3
                    };
                    stack mut i32[min max] total = 0;
                    for willexit (borrow i32[min max] value in values)
                    {
                        total += value;
                    }

                    return total;
                };
            }
            """,
            new CompilerOptions(
                StopAfterPassId: "type-check",
                MaximumCompileTimeLoopIterations: 2));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK3053"
            && diagnostic.Message.Contains("stopped after 2 iteration(s) of a `for willexit` traversal loop", StringComparison.Ordinal));
    }

    [Fact]
    public void ComptimeRecursiveFunctionCallReportsNonTerminatingEvaluation()
    {
        var result = Compile(
            """
            module Demo

            finite law i32[min max] Spin()
            {
                return comptime Spin();
            }

            finite law i32[min max] Run()
            {
                return comptime Spin();
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK3053"
            && diagnostic.Message.Contains("recursive function call to 'Spin'", StringComparison.Ordinal));
    }

    [Fact]
    public void ComptimeBlockRequiresWillexitLoops()
    {
        var result = Compile(
            """
            module Demo

            finite law i32[min max] Run()
            {
                return comptime
                {
                    stack mut i32[min max] value = 0;
                    while infinite (value < 1)
                    {
                        value += 1;
                    }

                    return value;
                };
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK3053"
            && diagnostic.Message.Contains("`comptime` block must return a value during compilation", StringComparison.Ordinal));
    }

    [Fact]
    public void ComptimeExpressionRejectsRuntimeDependentValue()
    {
        var result = Compile(
            """
            module Demo

            finite law i32[min max] Run(i32[min max] input)
            {
                return comptime input;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK3053"
            && diagnostic.Message.Contains("must evaluate during compilation", StringComparison.Ordinal));
    }

    [Fact]
    public void ComptimeStructuralFactsExposeCSourceAliasIdentity()
    {
        var llvm = CompileToLlvm(
            """
            module Demo

            alias NativeCallback = fnptr<ffi(c) finite law System.C.c_int(rawptr<System.C.c_char>, System.C.c_long)>;
            alias NativeClosure = heap closure<finite law System.C.c_long(System.C.c_int, rawptr<System.C.c_char>)>;

            struct Native
            {
                System.C.c_int Code;

                finite law System.C.c_long Echo(borrow Native self, System.C.c_long value)
                {
                    return value;
                }
            }

            enum NativeToken
            {
                Count(System.C.c_long)
            }

            finite law i32[min max] Run()
            {
                if (comptime System.Compiler.TypeHasCSourceAlias<System.C.c_int>()
                    && comptime (System.Compiler.TypeCSourceAliasName<System.C.c_int>() == "System.C.c_int")
                    && comptime System.Compiler.TypeHasCSourceAlias<System.C.c_void>()
                    && comptime (System.Compiler.TypeCSourceAliasName<System.C.c_void>() == "System.C.c_void")
                    && comptime System.Compiler.RawPointerElementTypeHasCSourceAlias<rawptr<System.C.c_char>>()
                    && comptime (System.Compiler.RawPointerElementTypeCSourceAliasName<rawptr<System.C.c_char>>() == "System.C.c_char")
                    && comptime System.Compiler.TypeElementTypeHasCSourceAlias<System.C.c_char[4]>()
                    && comptime (System.Compiler.TypeElementTypeCSourceAliasName<System.C.c_char[4]>() == "System.C.c_char")
                    && comptime System.Compiler.FunctionPointerReturnTypeHasCSourceAlias<NativeCallback>()
                    && comptime (System.Compiler.FunctionPointerReturnTypeCSourceAliasName<NativeCallback>() == "System.C.c_int")
                    && comptime System.Compiler.FunctionPointerParameterTypeHasCSourceAlias<NativeCallback, 1>()
                    && comptime (System.Compiler.FunctionPointerParameterTypeCSourceAliasName<NativeCallback, 1>() == "System.C.c_long")
                    && comptime System.Compiler.ClosureReturnTypeHasCSourceAlias<NativeClosure>()
                    && comptime (System.Compiler.ClosureReturnTypeCSourceAliasName<NativeClosure>() == "System.C.c_long")
                    && comptime System.Compiler.ClosureParameterTypeHasCSourceAlias<NativeClosure, 0>()
                    && comptime (System.Compiler.ClosureParameterTypeCSourceAliasName<NativeClosure, 0>() == "System.C.c_int")
                    && comptime !System.Compiler.ClosureParameterTypeHasCSourceAlias<NativeClosure, 1>()
                    && comptime (System.Compiler.ClosureParameterTypeCSourceAliasName<NativeClosure, 1>() == "")
                    && comptime System.Compiler.FieldTypeHasCSourceAlias<Native, 0>()
                    && comptime (System.Compiler.FieldTypeCSourceAliasName<Native, 0>() == "System.C.c_int")
                    && comptime System.Compiler.EnumVariantPayloadTypeHasCSourceAlias<NativeToken, 0, 0>()
                    && comptime (System.Compiler.EnumVariantPayloadTypeCSourceAliasName<NativeToken, 0, 0>() == "System.C.c_long")
                    && comptime System.Compiler.MethodReturnTypeHasCSourceAlias<Native, 0>()
                    && comptime (System.Compiler.MethodReturnTypeCSourceAliasName<Native, 0>() == "System.C.c_long")
                    && comptime System.Compiler.MethodParameterTypeHasCSourceAlias<Native, 0, 1>()
                    && comptime (System.Compiler.MethodParameterTypeCSourceAliasName<Native, 0, 1>() == "System.C.c_long")
                    && comptime !System.Compiler.TypeHasCSourceAlias<i32[min max]>()
                    && comptime (System.Compiler.TypeCSourceAliasName<i32[min max]>() == ""))
                {
                    return 42;
                }

                return 0;
            }
            """,
            new CompilerOptions(OptimizationLevel: CompilerOptimizationLevel.O0));

        var runBody = ExtractDefinitionBody(llvm, "Run");
        Assert.Contains("ret i32 42", runBody);
        Assert.DoesNotContain("call", runBody);
    }

    [Fact]
    public void ComptimeStructuralFactsExposeTypeAndMethodModuleNames()
    {
        var llvm = CompileToLlvm(
            """
            module Demo.Core

            struct Header
            {
                i32[min max] Code;
            }

            struct Box<T, comptime u8[1 8] N>
            {
                T[N] Items;
            }

            struct Container
            {
                Box<Header, 4> Value;
            }

            enum Envelope
            {
                Item(Box<Header, 4>)
            }

            alias Callback = fnptr<fn Box<Header, 4>(Box<Header, 4>)>;
            alias Handler = borrow closure<fn Box<Header, 4>(Box<Header, 4>)>;

            trait Contract
            {
                alias Item = Box<Header, 4>;
            }

            trait Drawable
            {
            }

            struct Widget : Drawable
            {
                i32[min max] Width;
            }

            struct Service
            {
                finite law Box<Header, 4> Read(borrow Service self, Box<Header, 4> fallback)
                {
                    return fallback;
                }

                static finite law i32[min max] Reset()
                {
                    return 0;
                }
            }

            alias HeaderAlias = Header;

            finite law i32[min max] Run()
            {
                if (comptime (System.Compiler.TypeModuleName<Header>() == "Demo.Core")
                    && comptime (System.Compiler.TypeModuleName<HeaderAlias>() == "Demo.Core")
                    && comptime (System.Compiler.TypeModuleName<Box<i32[min max], 4>>() == "Demo.Core")
                    && comptime (System.Compiler.TypeModuleName<i32[min max]>() == "")
                    && comptime (System.Compiler.MethodModuleName<Service, 0>() == "Demo.Core")
                    && comptime (System.Compiler.MethodModuleName<Service, 1>() == "Demo.Core")
                    && comptime (System.Compiler.FieldTypeModuleName<Container, 0>() == "Demo.Core")
                    && comptime (System.Compiler.FieldTypeModuleName<Header, 0>() == "")
                    && comptime (System.Compiler.EnumVariantPayloadTypeModuleName<Envelope, 0, 0>() == "Demo.Core")
                    && comptime (System.Compiler.FunctionPointerReturnTypeModuleName<Callback>() == "Demo.Core")
                    && comptime (System.Compiler.FunctionPointerParameterTypeModuleName<Callback, 0>() == "Demo.Core")
                    && comptime (System.Compiler.ClosureReturnTypeModuleName<Handler>() == "Demo.Core")
                    && comptime (System.Compiler.ClosureParameterTypeModuleName<Handler, 0>() == "Demo.Core")
                    && comptime (System.Compiler.MethodReturnTypeModuleName<Service, 0>() == "Demo.Core")
                    && comptime (System.Compiler.MethodParameterTypeModuleName<Service, 0, 1>() == "Demo.Core")
                    && comptime (System.Compiler.AssociatedTypeTargetTypeModuleName<Contract, 0>() == "Demo.Core")
                    && comptime (System.Compiler.ImplementedTraitTypeModuleName<Widget, 0>() == "Demo.Core")
                    && comptime (System.Compiler.TypeArgumentTypeModuleName<Box<Header, 4>, 0>() == "Demo.Core")
                    && comptime (System.Compiler.TypeComptimeGenericParameterTypeModuleName<Box<Header, 4>, 0>() == "")
                    && comptime (System.Compiler.TypeComptimeArgumentTypeModuleName<Box<Header, 4>, 0>() == ""))
                {
                    return 42;
                }

                return 0;
            }
            """,
            new CompilerOptions(OptimizationLevel: CompilerOptimizationLevel.O0));

        var runBody = ExtractDefinitionBody(llvm, "Run");
        Assert.Contains("ret i32 42", runBody);
        Assert.DoesNotContain("call", runBody);
    }

    [Fact]
    public void ComptimeBlockRejectsRuntimeDependentValue()
    {
        var result = Compile(
            """
            module Demo

            finite law i32[min max] Run(i32[min max] input)
            {
                return comptime
                {
                    return input;
                };
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK3053"
            && diagnostic.Message.Contains("could not evaluate return expression 'input'", StringComparison.Ordinal));
    }
}
