using Stark.Compiler;

namespace compiler.Tests;

public sealed class LlvmIrEmissionTests
{
    [Fact]
    public void StraightLineFunctionEmitsOptimizedLlvmBody()
    {
        var result = Compile(
            """
            module Demo

            finite law i32 Run() {
                stack mut i32 value = 1;
                value = value + 1;
                return value;
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("define fastcc i32 @Run()", llvm);
        Assert.Contains("ret i32 2", llvm);
        Assert.DoesNotContain("add i32", llvm);
        Assert.DoesNotContain("alloca i32", llvm);
        Assert.DoesNotContain("declare fastcc i32 @Run()", llvm);
    }

    [Fact]
    public void ConstantBranchConditionsFoldToUnconditionalBranches()
    {
        var result = Compile(
            """
            module Demo

            finite law i32 Run() {
                if (true) {
                    return 1;
                }

                return 2;
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("br label %bb1", llvm);
        Assert.Contains("ret i32 1", llvm);
        Assert.DoesNotContain("br i1", llvm);
    }

    [Fact]
    public void BranchJoinEmitsPhiNode()
    {
        var result = Compile(
            """
            module Demo

            finite law i32 Run(bool flag) {
                stack mut i32 value = 0;
                if (flag) {
                    value = 1;
                } else {
                    value = 2;
                }

                return value;
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("phi i32", llvm);
        Assert.Contains("[ 1, %bb1 ], [ 2, %bb2 ]", llvm);
    }

    [Fact]
    public void GlobalsUseVisibilityAwareLinkageAndConstantKinds()
    {
        var result = Compile(
            """
            module Globals

            public const i32 Answer = 42;
            internal static rawptr<i8> Buffer = null;
            export static mut rawptr<i8> Visible = null;

            finite law i32 Run() {
                return 0;
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("; visibility: public", llvm);
        Assert.Contains("@Answer = constant i32 42", llvm);
        Assert.Contains("; visibility: internal", llvm);
        Assert.Contains("@Buffer = constant ptr null", llvm);
        Assert.Contains("; visibility: export", llvm);
        Assert.Contains("@Visible = global ptr null", llvm);
    }

    [Fact]
    public void MutableGlobalsEmitRealDefinitionsStoresAndLoads()
    {
        var result = Compile(
            """
            module Demo

            static mut i32 Counter = 0;

            finite i32 Run() {
                Counter = 7;
                return Counter;
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("@Counter = global i32 0", llvm);
        Assert.Contains("store i32 7, ptr @Counter", llvm);
        Assert.Contains("load i32, ptr @Counter", llvm);
    }

    [Fact]
    public void LibraryBuildQualifiesRootGlobalSymbolsAndPreservesExportNames()
    {
        var result = Compile(
            """
            module Math

            public const i32 Answer = 42;
            internal static mut i32 Counter = 0;
            static i32 Hidden = 1;
            export static mut i32 Visible = 0;

            finite i32 Run() {
                Counter = 7;
                return Counter;
            }
            """,
            new CompilerOptions(
                EmitLlvmIr: true,
                QualifyModuleSymbols: true));

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("@Math_Answer = constant i32 42", llvm);
        Assert.Contains("@Math_Counter = global i32 0", llvm);
        Assert.Contains("@Math_Hidden = internal constant i32 1", llvm);
        Assert.Contains("@Visible = global i32 0", llvm);
        Assert.Contains("store i32 7, ptr @Math_Counter", llvm);
        Assert.Contains("load i32, ptr @Math_Counter", llvm);
    }

    [Fact]
    public void AggregateAndArrayGlobalsEmitConcreteInitializers()
    {
        var result = Compile(
            """
            module Demo

            struct Pair {
                i32 Left;
                i32 Right;
            }

            const Pair Origin = new Pair() { Left = 1, Right = 2 };
            static i32[3] Values = { 4, 7, 9 };

            finite i32 Run() {
                return Origin.Right + Values[1];
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("%Pair = type { i32, i32 }", llvm);
        Assert.Contains("@Origin = constant %Pair { i32 1, i32 2 }", llvm);
        Assert.Contains("@Values = constant [3 x i32] [i32 4, i32 7, i32 9]", llvm);
    }

    [Fact]
    public void RecordPrimaryConstructorGlobalsEmitConcreteInitializers()
    {
        var result = Compile(
            """
            module Demo

            record Point(i32 X) {
                i32 Y;
            }

            const Point Origin = new Point(3) { Y = 9 };

            finite i32 Run() {
                return Origin.Y;
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("%Point = type { i32, i32 }", llvm);
        Assert.Contains("@Origin = constant %Point { i32 3, i32 9 }", llvm);
    }

    [Fact]
    public void ConstFixedArrayGlobalsEmitFrozenDefinitions()
    {
        var result = Compile(
            """
            module Demo

            const i32[3] Values = { 4, 7, 9 };

            finite i32 Run() {
                return Values[1];
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("@Values = constant [3 x i32] [i32 4, i32 7, i32 9]", llvm);
    }

    [Fact]
    public void NestedConstObjectGraphsEmitConcreteConstantInitializers()
    {
        var result = Compile(
            """
            module Demo

            struct Inner {
                i32 Value;
            }

            struct Outer {
                Inner Item;
                ascii Label;
            }

            const Outer Graph = new Outer() {
                Item = new Inner() { Value = 7 },
                Label = "ok"
            };

            finite i32 Run() {
                return Graph.Item.Value;
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("%stark_ascii = type { ptr, i64 }", llvm);
        Assert.Contains("%Inner = type { i32 }", llvm);
        Assert.Contains("%Outer = type { %Inner, %stark_ascii }", llvm);
        Assert.Contains("@Graph = constant %Outer { %Inner { i32 7 }, %stark_ascii { ptr getelementptr inbounds (", llvm);
        Assert.Contains("i64 2 } }", llvm);
    }

    [Fact]
    public void NestedAggregateLiteralsFoldIntoFrozenGlobalInitializers()
    {
        var result = Compile(
            """
            module Demo

            struct Inner {
                i32[2] Pair;
            }

            struct Outer {
                Inner Node;
                i32[3] View;
            }

            const Outer Frozen = {
                Node = { Pair = { 4, 7 } },
                View = { 1, 2, 3 }
            };

            finite i32 Run() {
                return Frozen.Node.Pair[1] + Frozen.View[0];
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("%Inner = type { [2 x i32] }", llvm);
        Assert.Contains("%Outer = type { %Inner, [3 x i32] }", llvm);
        Assert.Contains("@Frozen = constant %Outer { %Inner { [2 x i32] [i32 4, i32 7] }, [3 x i32] [i32 1, i32 2, i32 3] }", llvm);
    }

    [Fact]
    public void MutableAggregateGlobalsEmitConcreteInitializersAndStores()
    {
        var result = Compile(
            """
            module Demo

            struct Pair {
                i32 Left;
                i32 Right;
            }

            static mut Pair Current = new Pair() { Left = 5, Right = 8 };
            static mut i32[3] Values = { 1, 2, 3 };

            finite i32 Run() {
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
        Assert.Contains("load %Pair, ptr @Current", llvm);
        Assert.Contains("store %Pair", llvm);
        Assert.Contains("ptr @Current", llvm);
        Assert.Contains("load [3 x i32], ptr @Values", llvm);
        Assert.Contains("store [3 x i32]", llvm);
        Assert.Contains("ptr @Values", llvm);
    }

    [Fact]
    public void AggregateArrayFieldsEmitConcreteInitializers()
    {
        var result = Compile(
            """
            module Demo

            struct Buffer {
                i32[2] Values;
            }

            static Buffer Shared = { Values = { 5, 8 } };

            finite i32 Run() {
                return Shared.Values[1];
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("%Buffer = type { [2 x i32] }", llvm);
        Assert.Contains("@Shared = constant %Buffer { [2 x i32] [i32 5, i32 8] }", llvm);
    }

    [Fact]
    public void RawPointerConstNullGlobalsRemainExternalPlaceholders()
    {
        var result = Compile(
            """
            module Demo

            const rawptr<i8> stdout = null;

            finite law i32 Run() {
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

            fn i32 Run(i32 left, i32 right) {
                return left ^ right;
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("define fastcc i32 @Run(i32 %arg_left, i32 %arg_right)", llvm);
        Assert.Contains("xor i32 %arg_left, %arg_right", llvm);
    }

    [Fact]
    public void BitwiseAndShiftExpressionsEmitConcreteLlvmInstructions()
    {
        var result = Compile(
            """
            module Demo

            fn i32 Run(i32 left, i32 middle, i32 right, i32 mask) {
                return left | middle ^ right & mask << 1 >> 1;
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("shl i32 %arg_mask, 1", llvm);
        Assert.Contains("ashr i32", llvm);
        Assert.Contains("and i32 %arg_right", llvm);
        Assert.Contains("xor i32 %arg_middle", llvm);
        Assert.Contains("or i32 %arg_left", llvm);
    }

    [Fact]
    public void WrappingArithmeticEmitsConcreteLlvmInstructions()
    {
        var result = Compile(
            """
            module Demo

            fn i32 Run(i32 left, i32 right) {
                return -%left +% right *% 2;
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("sub i32 0, %arg_left", llvm);
        Assert.Contains("mul i32 %arg_right, 2", llvm);
        Assert.Contains("add i32", llvm);
    }

    [Fact]
    public void SaturatingArithmeticEmitsWideClampSequence()
    {
        var result = Compile(
            """
            module Demo

            fn i32 Run(i32 left, i32 right) {
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
    }

    [Fact]
    public void ExplicitIntegerArithmeticConstantsFoldBeforeLlvmEmission()
    {
        var result = Compile(
            """
            module Demo

            fn i32 Wrap() {
                return 2147483647 +% 1;
            }

            fn i32 SatAdd() {
                return 2147483647 +| 1;
            }

            fn i32 SatMul() {
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

            fn f32 Run() {
                return 2.0 ** 3.0;
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("define fastcc float @Run()", llvm);
        Assert.Contains("ret float 8", llvm);
        Assert.DoesNotContain("@llvm.pow.f32", llvm);
    }

    [Fact]
    public void LoopHeaderEmitsBackedgePhi()
    {
        var result = Compile(
            """
            module Demo

            fn i32 Run() {
                stack mut i32 i = 0;
                while willexit (i < 4) {
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

            fn i32 Run() {
                stack i32 value = 1;
                switch (value) {
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

            fn i32 Run(i32 value, bool allow) {
                switch (value) {
                    case 1 when allow:
                        return 1;
                    default:
                        return 2;
                }
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("define fastcc i32 @Run(i32 %arg_value, i1 %arg_allow)", llvm);
        Assert.Contains("icmp eq i32", llvm);
        Assert.Contains("br i1 %arg_allow", llvm);
        Assert.DoesNotContain("declare fastcc i32 @Run(i32, i1)", llvm);
    }

    [Fact]
    public void CaptureSwitchPatternEmitsConcreteBody()
    {
        var result = Compile(
            """
            module Demo

            fn i32 Run(i32 value, bool allow) {
                switch (value) {
                    case var capture when allow:
                        return capture;
                    default:
                        return 0;
                }
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("define fastcc i32 @Run(i32 %arg_value, i1 %arg_allow)", llvm);
        Assert.Contains("br i1 %arg_allow", llvm);
        Assert.Contains("ret i32 %arg_value", llvm);
        Assert.DoesNotContain("declare fastcc i32 @Run(i32, i1)", llvm);
    }

    [Fact]
    public void MultiLabelGuardedSwitchEmitsDecisionTreeBody()
    {
        var result = Compile(
            """
            module Demo

            fn i32 Run(i32 value, bool allow) {
                switch (value) {
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

        Assert.Contains("define fastcc i32 @Run(i32 %arg_value, i1 %arg_allow)", llvm);
        Assert.Contains("icmp eq i32 %arg_value, 1", llvm);
        Assert.Contains("icmp eq i32 %arg_value, 2", llvm);
        Assert.Contains("br i1 %arg_allow", llvm);
        Assert.DoesNotContain("switch i32", llvm);
        Assert.DoesNotContain("declare fastcc i32 @Run(i32, i1)", llvm);
    }

    [Fact]
    public void ComparisonChainEmitsShortCircuitBranchesAndSingleSharedEvaluation()
    {
        var result = Compile(
            """
            module Demo

            fn i32 Next() {
                return 1;
            }

            fn bool Run() {
                return 0 < Next() < 3;
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Equal(1, CountOccurrences(llvm, "call i32 @Next()"));
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

            fn bool Run(f32 low, f32 value, f32 high) {
                return low < value <= high;
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("fcmp olt float", llvm);
        Assert.Contains("fcmp ole float", llvm);
        Assert.Contains("br i1", llvm);
    }

    [Fact]
    public void TextLiteralSwitchEmitsLengthAndByteComparisons()
    {
        var result = Compile(
            """
            module Demo

            fn i32 Run(ascii value, bool allow) {
                switch (value) {
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
        Assert.Contains("br i1 %arg_allow", llvm);
        Assert.DoesNotContain("switch %stark_ascii", llvm);
        Assert.DoesNotContain("declare fastcc i32 @Run(%stark_ascii, i1)", llvm);
    }

    [Fact]
    public void LargeTextLiteralSwitchEmitsLengthPartitionedDispatch()
    {
        var result = Compile(
            """
            module Demo

            fn i32 Run(ascii value) {
                switch (value) {
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

            fn i32 Run(unicode value) {
                switch (value) {
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

        Assert.Contains("define fastcc i32 @Run(%stark_unicode %arg_value)", llvm);
        Assert.Contains("extractvalue %stark_unicode %arg_value, 0", llvm);
        Assert.Contains("load i8, ptr", llvm);
        Assert.DoesNotContain("declare fastcc i32 @Run(%stark_unicode)", llvm);
    }

    [Fact]
    public void HelloWorldStyleFfiPutsEmitsStringGlobalAndMainBody()
    {
        var result = Compile(
            """
            module Hello

            ffi fn i32 puts(ascii text);
            export ffi fn i32 main() {
                puts("Hello, world!\n");
                return 0;
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("@.str.0 = private unnamed_addr constant", llvm);
        Assert.Contains("%stark_ascii = type { ptr, i64 }", llvm);
        Assert.Contains("declare i32 @puts(ptr readonly)", llvm);
        Assert.Contains("define i32 @main()", llvm);
        Assert.Contains("call i32 @puts(ptr getelementptr inbounds ([15 x i8], ptr @.str.0, i32 0, i32 0))", llvm);
    }

    [Fact]
    public void InternalStringFunctionsUseConcreteStringAbi()
    {
        var result = Compile(
            """
            module Demo

            finite law ascii Echo(ascii text) {
                return text;
            }

            finite law ascii Run() {
                return Echo("Hi");
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("%stark_ascii = type { ptr, i64 }", llvm);
        Assert.Contains("define fastcc %stark_ascii @Echo(%stark_ascii %arg_text)", llvm);
        Assert.Contains("ret %stark_ascii %arg_text", llvm);
        Assert.Contains("call %stark_ascii @Echo(%stark_ascii { ptr getelementptr inbounds ([3 x i8], ptr @.str.0, i32 0, i32 0), i64 2 })", llvm);
    }

    [Fact]
    public void CharacterLiteralsEmitConcreteStringValues()
    {
        var result = Compile(
            """
            module Demo

            finite law ascii AsciiChar() {
                return 'a';
            }

            finite law unicode UnicodeChar() {
                return '\u03B1';
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("%stark_ascii = type { ptr, i64 }", llvm);
        Assert.Contains("%stark_unicode = type { ptr, i64 }", llvm);
        Assert.Contains("@.str.0 = private unnamed_addr constant [2 x i8] c\"a\\00\"", llvm);
        Assert.Contains("@.str.1 = private unnamed_addr constant [3 x i8] c\"\\CE\\B1\\00\"", llvm);
        Assert.Contains("define fastcc %stark_ascii @AsciiChar()", llvm);
        Assert.Contains("define fastcc %stark_unicode @UnicodeChar()", llvm);
        Assert.Contains("ret %stark_ascii { ptr getelementptr inbounds ([2 x i8], ptr @.str.0, i32 0, i32 0), i64 1 }", llvm);
        Assert.Contains("ret %stark_unicode { ptr getelementptr inbounds ([3 x i8], ptr @.str.1, i32 0, i32 0), i64 2 }", llvm);
    }

    [Fact]
    public void UnicodeStringLiteralsUseUtf8ByteLengthInRuntimeValues()
    {
        var result = Compile(
            """
            module Demo

            finite law unicode Greek() {
                return "\u03B1";
            }

            finite law unicode Accented() {
                return "\xC9";
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("%stark_unicode = type { ptr, i64 }", llvm);
        Assert.Contains("@.str.0 = private unnamed_addr constant [3 x i8] c\"\\CE\\B1\\00\"", llvm);
        Assert.Contains("@.str.1 = private unnamed_addr constant [3 x i8] c\"\\C3\\89\\00\"", llvm);
        Assert.Contains("ret %stark_unicode { ptr getelementptr inbounds ([3 x i8], ptr @.str.0, i32 0, i32 0), i64 2 }", llvm);
        Assert.Contains("ret %stark_unicode { ptr getelementptr inbounds ([3 x i8], ptr @.str.1, i32 0, i32 0), i64 2 }", llvm);
    }

    [Fact]
    public void PlainFnsEmitInferredPureAndFiniteAttributes()
    {
        var result = Compile(
            """
            module Demo

            struct Box {
                i32 Value;
            }

            fn i32 Add(i32 left, i32 right) {
                return left + right;
            }

            fn i32 Read(borrow Box box) {
                return box.Value;
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("define fastcc i32 @Add(i32 %arg_left, i32 %arg_right) nounwind willreturn mustprogress nosync nofree memory(none)", llvm);
        Assert.Contains("memory(argmem: read)", llvm);
    }

    [Fact]
    public void EscapedTextLiteralsEmitDecodedBytes()
    {
        var result = Compile(
            """
            module Demo

            finite law ascii Controls() {
                return "\0\b\t\n\f\r\\\"\'";
            }

            finite law ascii HexChar() {
                return '\x41';
            }

            finite law unicode Wide() {
                return "\xC9";
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("@.str.0 = private unnamed_addr constant [10 x i8] c\"\\00\\08\\09\\0A\\0C\\0D\\5C\\22'\\00\"", llvm);
        Assert.Contains("@.str.1 = private unnamed_addr constant [2 x i8] c\"A\\00\"", llvm);
        Assert.Contains("@.str.2 = private unnamed_addr constant [3 x i8] c\"\\C3\\89\\00\"", llvm);
    }

    [Fact]
    public void FfiStringCallsExtractPointerFromConcreteStringValues()
    {
        var result = Compile(
            """
            module Demo

            ffi fn i32 puts(ascii text);

            fn ascii Message() {
                return "Hello";
            }

            export ffi fn i32 main() {
                stack ascii message = Message();
                puts(message);
                return 0;
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("%stark_ascii = type { ptr, i64 }", llvm);
        Assert.Contains("define fastcc %stark_ascii @Message()", llvm);
        Assert.Contains("define i32 @main()", llvm);
        Assert.Contains("call %stark_ascii @Message()", llvm);
        Assert.Contains("extractvalue %stark_ascii", llvm);
        Assert.Contains("call i32 @puts(ptr %", llvm);
    }

    [Fact]
    public void StructFieldAccessEmitsConcreteAggregateTypeAndExtractvalue()
    {
        var result = Compile(
            """
            module Demo

            struct Box {
                i32 Value;
            }

            fn i32 Run() {
                stack Box box = new Box() { Value = 41 };
                return box.Value;
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("%Box = type { i32 }", llvm);
        Assert.Contains("insertvalue %Box zeroinitializer, i32", llvm);
        Assert.Contains("extractvalue %Box", llvm);
        Assert.Contains("ret i32", llvm);
        Assert.DoesNotContain("declare fastcc i32 @Run()", llvm);
    }

    [Fact]
    public void FieldAssignmentEmitsAggregateInsertvalueUpdate()
    {
        var result = Compile(
            """
            module Demo

            struct Box {
                i32 Value;
            }

            fn i32 Run() {
                stack mut Box box = new Box() { Value = 1 };
                box.Value = 2;
                return box.Value;
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("%Box = type { i32 }", llvm);
        Assert.True(CountOccurrences(llvm, "insertvalue %Box") >= 2);
        Assert.Contains("extractvalue %Box", llvm);
        Assert.DoesNotContain("; LLVM body emission pending for Run", llvm);
    }

    [Fact]
    public void RegisterObjectCreationKeepsDirectAggregateLlvmLowering()
    {
        var result = Compile(
            """
            module Demo

            struct Box {
                i32 Value;
            }

            fn i32 Run() {
                register Box box = new Box() { Value = 7 };
                return box.Value;
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("define fastcc i32 @Run()", llvm);
        Assert.Contains("extractvalue %Box", llvm);
        Assert.DoesNotContain("alloca %Box", llvm);
        Assert.DoesNotContain("; LLVM body emission pending for Run", llvm);
        Assert.DoesNotContain("; LLVM body emission fallback for Run", llvm);
    }

    [Fact]
    public void HeapObjectCreationFallsBackUntilAllocatorLoweringExists()
    {
        var result = Compile(
            """
            module Demo

            struct Box {
                i32 Value;
            }

            fn i32 Run() {
                heap Box box = new Box() { Value = 7 };
                return box.Value;
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("; LLVM body emission fallback for Run: Local storage class 'heap' is not yet supported for LLVM body emission.", llvm);
        Assert.Contains("declare fastcc i32 @Run()", llvm);
    }

    [Fact]
    public void MixedCallMemberAndIndexPostfixChainsEmitCallAndExtracts()
    {
        var result = Compile(
            """
            module Demo

            struct Cell {
                i32 Value;
            }

            struct Holder {
                Cell[2] Cells;
            }

            fn Holder Make() {
                return new Holder() {
                    Cells = { new Cell() { Value = 3 }, new Cell() { Value = 5 } }
                };
            }

            fn i32 Run() {
                return Make().Cells[1].Value;
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("%Cell = type { i32 }", llvm);
        Assert.Contains("%Holder = type { [2 x %Cell] }", llvm);
        Assert.Contains("call %Holder @Make()", llvm);
        Assert.Contains("extractvalue %Holder", llvm);
        Assert.Contains("extractvalue [2 x %Cell]", llvm);
        Assert.Contains("extractvalue %Cell", llvm);
    }

    [Fact]
    public void RecordTypeUsesConcreteAggregateLayout()
    {
        var result = Compile(
            """
            module Demo

            record Point(i32 X, i32 Y) { }

            fn i32 Run() {
                stack Point point = new Point() { X = 3, Y = 4 };
                return point.Y;
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("%Point = type { i32, i32 }", llvm);
        Assert.Contains("insertvalue %Point zeroinitializer, i32", llvm);
        Assert.Contains("insertvalue %Point", llvm);
        Assert.Contains("extractvalue %Point", llvm);
    }

    [Fact]
    public void PlainObjectCreationWithoutInitializerReturnsZeroInitializedAggregate()
    {
        var result = Compile(
            """
            module Demo

            struct Box {
                i32 Value;
            }

            fn Box Make() {
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

            record Point(i32 X) {
                i32 Y;
            }

            fn Point Make() {
                return new Point(3) { Y = 9 };
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

            struct Box {
                i32 Value;
            }

            fn i32 Read(Box box) {
                return box.Value;
            }

            fn i32 Run() {
                return Read(new Box() { Value = 7 });
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("define fastcc i32 @Read(%Box %arg_box)", llvm);
        Assert.Contains("extractvalue %Box %arg_box, 0", llvm);
        Assert.Contains("call i32 @Read(%Box", llvm);
        Assert.DoesNotContain("load %Box, ptr %arg_box", llvm);
    }

    [Fact]
    public void BorrowedPaddedAggregateEmitsDerivedAlignmentAndLayoutFacts()
    {
        var result = Compile(
            """
            module Demo

            struct Pair {
                i8 Tag;
                i32 Value;
            }

            fn void Touch(borrow Pair pair) {
                return;
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("define fastcc void @Touch(ptr nonnull noalias readonly dereferenceable(8) align 4 %arg_pair)", llvm);
    }

    [Fact]
    public void SmallAddressableAggregateCopyUsesDirectLoadStore()
    {
        var result = Compile(
            """
            module Demo

            struct Pair {
                i32 Left;
                i32 Right;
            }

            fn i32 Run() {
                stack Pair source = new Pair() { Left = 1, Right = 2 };
                stack mut Pair dest = new Pair() { Left = 0, Right = 0 };
                stack rawptr<Pair> sourcePtr = &source;
                stack rawptr<Pair> destPtr = &dest;
                dest = source;
                return dest.Right;
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("%Pair = type { i32, i32 }", llvm);
        Assert.Contains("getelementptr inbounds %Pair, ptr %slot_source", llvm);
        Assert.Contains("getelementptr inbounds %Pair, ptr %slot_dest", llvm);
        Assert.Contains("load %Pair, ptr %v", llvm);
        Assert.Contains("store %Pair %abi_copy_load_", llvm);
        Assert.DoesNotContain("@llvm.memcpy.p0.p0.i64", llvm);
    }

    [Fact]
    public void LargeAddressableAggregateCopyUsesMemcpy()
    {
        var result = Compile(
            """
            module Demo

            struct Large {
                i32 A0;
                i32 A1;
                i32 A2;
                i32 A3;
                i32 A4;
                i32 A5;
                i32 A6;
                i32 A7;
                i32 A8;
            }

            fn i32 Run() {
                stack Large source = new Large() {
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
                stack mut Large dest = new Large() {
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

        Assert.Contains("declare void @llvm.memcpy.p0.p0.i64", llvm);
        Assert.Contains("call void @llvm.memcpy.p0.p0.i64(ptr %v", llvm);
        Assert.Contains("i64 36, i1 false)", llvm);
    }

    [Fact]
    public void AggregateMoveInvalidatesAddressableSourceStorage()
    {
        var result = Compile(
            """
            module Demo

            struct Pair {
                i32 Left;
                i32 Right;
            }

            fn i32 Run() {
                stack Pair source = new Pair() { Left = 1, Right = 2 };
                stack mut Pair dest = new Pair() { Left = 0, Right = 0 };
                stack rawptr<Pair> sourcePtr = &source;
                stack rawptr<Pair> destPtr = &dest;
                dest = source;
                source = new Pair() { Left = 3, Right = 4 };
                return source.Right + dest.Right;
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("store %Pair undef, ptr %slot_source", llvm);
    }

    [Fact]
    public void AddressableAggregateConditionalUsesSingleAggregateAlloca()
    {
        var result = Compile(
            """
            module Demo

            struct Pair {
                i32 Left;
                i32 Right;
            }

            fn i32 Run(bool flag) {
                stack Pair value = flag ? new Pair() { Left = 1, Right = 2 } : new Pair() { Left = 3, Right = 4 };
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

            struct Box {
                i32 Value;
            }

            fn Box Make() {
                return new Box() { Value = 7 };
            }

            fn i32 Run() {
                stack Box box = Make();
                return box.Value;
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("define fastcc %Box @Make()", llvm);
        Assert.Contains("ret %Box", llvm);
        Assert.Contains("call %Box @Make()", llvm);
        Assert.DoesNotContain("sret(%Box)", llvm);
    }

    [Fact]
    public void AggregateBranchJoinEmitsByValuePhiNode()
    {
        var result = Compile(
            """
            module Demo

            struct Box {
                i32 Value;
            }

            fn i32 Run(bool flag) {
                stack mut Box box = new Box() { Value = 0 };
                if (flag) {
                    box = new Box() { Value = 1 };
                } else {
                    box = new Box() { Value = 2 };
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

            struct Box {
                i32 Value;

                fn i32 Read(Box box) {
                    return box.Value;
                }
            }

            fn i32 Run() {
                stack Box box = new Box() { Value = 7 };
                return box.Read();
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("define fastcc i32 @Box_Read(%Box %arg_box)", llvm);
        Assert.Contains("extractvalue %Box %arg_box, 0", llvm);
        Assert.Contains("call i32 @Box_Read(%Box", llvm);
        Assert.DoesNotContain("load %Box, ptr %arg_box", llvm);
    }

    [Fact]
    public void BorrowReceiverMethodsLowerToPointerReceiverCalls()
    {
        var result = Compile(
            """
            module Demo

            struct Box {
                i32 Value;

                fn i32 Read(borrow Box box) {
                    return box.Value;
                }
            }

            fn i32 Run(borrow Box box) {
                return box.Read();
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("define fastcc i32 @Box_Read(ptr nonnull noalias readonly dereferenceable(4) align 4 %arg_box)", llvm);
        Assert.Contains("define fastcc i32 @Run(ptr nonnull noalias readonly dereferenceable(4) align 4 %arg_box)", llvm);
        Assert.Contains("call i32 @Box_Read(ptr %", llvm);
    }

    [Fact]
    public void FixedArrayInitializerAndIndexEmitConcreteArrayIr()
    {
        var result = Compile(
            """
            module Demo

            fn i32 Run() {
                stack i32[3] values = { 1, 2, 3 };
                return values[1];
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("define fastcc i32 @Run()", llvm);
        Assert.Contains("insertvalue [3 x i32] zeroinitializer", llvm);
        Assert.Contains("extractvalue [3 x i32]", llvm);
        Assert.DoesNotContain("alloca [3 x i32]", llvm);
        Assert.DoesNotContain("declare fastcc i32 @Run()", llvm);
    }

    [Fact]
    public void FixedArrayIndexAssignmentEmitsInsertvalueUpdate()
    {
        var result = Compile(
            """
            module Demo

            fn i32 Run() {
                stack mut i32[3] values = { 1, 2, 3 };
                values[1] = 9;
                return values[1];
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.True(CountOccurrences(llvm, "insertvalue [3 x i32]") >= 2);
        Assert.Contains("extractvalue [3 x i32]", llvm);
        Assert.DoesNotContain("alloca [3 x i32]", llvm);
    }

    [Fact]
    public void DynamicArrayIndexEmitsAddressBasedLoad()
    {
        var result = Compile(
            """
            module Demo

            fn i32 Run(i32 index) {
                stack i32[3] values = { 1, 2, 3 };
                return values[index];
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("define fastcc i32 @Run(i32 %arg_index)", llvm);
        Assert.Contains("alloca [3 x i32]", llvm);
        Assert.Contains("declare void @llvm.lifetime.start.p0(i64 immarg, ptr nocapture)", llvm);
        Assert.Contains("declare void @llvm.lifetime.end.p0(i64 immarg, ptr nocapture)", llvm);
        Assert.Contains("call void @llvm.lifetime.start.p0(i64 12, ptr %slot_values)", llvm);
        Assert.Contains("call void @llvm.lifetime.end.p0(i64 12, ptr %slot_values)", llvm);
        Assert.Contains("getelementptr inbounds [3 x i32], ptr %slot_values, i32 0", llvm);
        Assert.Contains("getelementptr inbounds [3 x i32], ptr", llvm);
        Assert.Contains("load i32, ptr", llvm);
        Assert.DoesNotContain("declare fastcc i32 @Run(i32)", llvm);
    }

    [Fact]
    public void SliceParameterUsesConcreteSliceAbiAndDynamicIndexLoad()
    {
        var result = Compile(
            """
            module Demo

            fn i32 Read(i32[] view, i32 index) {
                return view[index];
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("define fastcc i32 @Read({ ptr, i64 } %arg_view, i32 %arg_index)", llvm);
        Assert.Contains("extractvalue { ptr, i64 } %arg_view, 0", llvm);
        Assert.Contains("getelementptr inbounds i32, ptr", llvm);
        Assert.Contains("load i32, ptr", llvm);
    }

    [Fact]
    public void FixedArrayParameterUsesDirectValueAbi()
    {
        var result = Compile(
            """
            module Demo

            fn i32 Read(i32[2] values) {
                return values[0] + values[1];
            }

            fn i32 Run() {
                stack i32[2] values = { 4, 7 };
                return Read(values);
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("define fastcc i32 @Read([2 x i32] %arg_values)", llvm);
        Assert.Contains("extractvalue [2 x i32] %arg_values, 0", llvm);
        Assert.Contains("extractvalue [2 x i32] %arg_values, 1", llvm);
        Assert.Contains("call i32 @Read([2 x i32]", llvm);
    }

    [Fact]
    public void FixedArrayReturnUsesDirectValueAbi()
    {
        var result = Compile(
            """
            module Demo

            fn i32[2] Make() {
                stack i32[2] values = { 4, 7 };
                return values;
            }

            fn i32 Run() {
                stack i32[2] values = Make();
                return values[1];
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("define fastcc [2 x i32] @Make()", llvm);
        Assert.Contains("call [2 x i32] @Make()", llvm);
        Assert.DoesNotContain("sret([2 x i32])", llvm);
    }

    [Fact]
    public void DynamicArrayIndexMutationEmitsAddressBasedStoreAndLoad()
    {
        var result = Compile(
            """
            module Demo

            fn i32 Run(i32 index) {
                stack mut i32[3] values = { 1, 2, 3 };
                values[index] = 9;
                return values[index];
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("define fastcc i32 @Run(i32 %arg_index)", llvm);
        Assert.True(CountOccurrences(llvm, "getelementptr inbounds [3 x i32], ptr") >= 2);
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

            fn i32 Run(i32[] view, i32 index) {
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

            fn i32 Run() {
                return Math.Add(3, 4);
            }
            """,
            new CompilerOptions(
                ModuleResolver: new InMemoryModuleResolver(
                [
                    (
                        new ResolvedModuleReference("Math", "/virtual/Math.stark", IsExternal: false),
                        """
                        module Math

                        public finite law i32 Add(i32 left, i32 right) {
                            return left + right;
                        }
                        """,
                        "/virtual/Math.stark"
                    )
                ])));

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("declare fastcc i32 @Math_Add(i32, i32)", llvm);
        Assert.Contains("call i32 @Math_Add(i32", llvm);
    }

    [Fact]
    public void ImportedGlobalsUseQualifiedDependencySymbols()
    {
        var result = Compile(
            """
            import Math
            module Demo

            fn i32 Run() {
                Math.Counter = 7;
                return Math.Counter + Math.Answer;
            }
            """,
            new CompilerOptions(
                ModuleResolver: new InMemoryModuleResolver(
                [
                    (
                        new ResolvedModuleReference("Math", "/virtual/Math.stark", IsExternal: false),
                        """
                        module Math

                        public const i32 Answer = 3;
                        public static mut i32 Counter = 1;
                        """,
                        "/virtual/Math.stark"
                    )
                ])));

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("; imported declaration: Math.Answer", llvm);
        Assert.Contains("@Math_Answer = external constant i32", llvm);
        Assert.Contains("; imported declaration: Math.Counter", llvm);
        Assert.Contains("@Math_Counter = external global i32", llvm);
        Assert.Contains("store i32 7, ptr @Math_Counter", llvm);
        Assert.Contains("load i32, ptr @Math_Counter", llvm);
        Assert.Contains("load i32, ptr @Math_Answer", llvm);
    }

    [Fact]
    public void ImmutableGlobalAddressesLowerWithoutPointerCasts()
    {
        var result = Compile(
            """
            module Demo

            struct Box {
                i32 Value;
            }

            static i32 Counter = 0;
            static Box Current = new Box() { Value = 5 };

            fn rawptr<i32> CounterPtr() {
                return &Counter;
            }

            fn rawptr<i32> FieldPtr() {
                return &(Current.Value);
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("@Counter = constant i32 0", llvm);
        Assert.Contains("@Current = constant %Box { i32 5 }", llvm);
        Assert.Contains("ret ptr @Counter", llvm);
        Assert.Contains("getelementptr inbounds %Box, ptr @Current, i32 0, i32 0", llvm);
        Assert.Equal(0, CountOccurrences(llvm, "getelementptr inbounds i8"));
    }

    [Fact]
    public void ImportedAggregateFunctionsUseCrossModuleAbiDeclarations()
    {
        var result = Compile(
            """
            import Geometry
            module Demo

            export ffi fn i32 main() {
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

                        public struct Box {
                            i32 Value;
                        }

                        public fn Box Make() {
                            return new Box() { Value = 7 };
                        }

                        public fn i32 Read(Box box) {
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
        Assert.Contains("call %Geometry_Box @Geometry_Make()", llvm);
        Assert.Contains("call i32 @Geometry_Read(%Geometry_Box", llvm);
    }

    [Fact]
    public void LibraryBuildQualifiesPublicRootSymbols()
    {
        var result = Compile(
            """
            module Math

            public finite law i32 Add(i32 left, i32 right) {
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

            fn i32 Helper() {
                return 7;
            }
            """,
            new CompilerOptions(
                EmitLlvmIr: true,
                QualifyModuleSymbols: true));

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("define internal fastcc i32 @Demo_Helper()", llvm);
    }

    [Fact]
    public void LocalFixedArrayCanBeCoercedToSliceForCalls()
    {
        var result = Compile(
            """
            module Demo

            fn i32 Read(i32[] view, i32 index) {
                return view[index];
            }

            fn i32 Run(i32 index) {
                stack i32[3] values = { 4, 7, 9 };
                return Read(values, index);
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("alloca [3 x i32]", llvm);
        Assert.Contains("store [3 x i32]", llvm);
        Assert.Contains("getelementptr inbounds [3 x i32], ptr %slot_values, i32 0, i32 0", llvm);
        Assert.Contains("insertvalue { ptr, i64 } zeroinitializer, ptr", llvm);
        Assert.Contains("call i32 @Read({ ptr, i64 }", llvm);
    }

    [Fact]
    public void BorrowedAggregateCallReusesPromotedLocalSlot()
    {
        var result = Compile(
            """
            module Demo

            struct Box {
                i32 Value;
            }

            fn void Touch(borrow Box box) {
                return;
            }

            fn void Forward(borrow Box box) {
                stack borrow Box alias = box;
                Touch(alias);
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("define fastcc void @Touch(ptr nonnull noalias readonly dereferenceable(4) align 4 %arg_box)", llvm);
        Assert.Contains("define fastcc void @Forward(ptr nonnull noalias readonly dereferenceable(4) align 4 %arg_box)", llvm);
        Assert.Contains("%slot_alias = alloca %Box", llvm);
        Assert.Contains("call void @Touch(ptr %slot_alias)", llvm);
        Assert.DoesNotContain("callarg_box", llvm);
    }

    [Fact]
    public void ConfiguredTargetInfoIsEmittedInHeader()
    {
        var result = Compile(
            """
            module Demo

            fn i32 Run() {
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

            fn i32 Run(bool left, bool right) {
                return left && right ? 1 : 2;
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("define fastcc i32 @Run(i1 %arg_left, i1 %arg_right)", llvm);
        Assert.Contains("phi i1", llvm);
        Assert.Contains("phi i32", llvm);
        Assert.Contains("br i1", llvm);
    }

    [Fact]
    public void PointerOperatorsAndExplicitConversionsEmitRawMemoryAccess()
    {
        var result = Compile(
            """
            module Demo

            static mut i32 Counter = 0;

            fn i32 Run(i64 bits) {
                stack mut i32 value = 1;
                stack rawmutptr<i32> ptr = &value;
                stack rawptr<i32> readonlyPtr = (rawptr<i32>)ptr;
                *ptr = (i32)bits;
                Counter = *readonlyPtr;
                return *(&Counter);
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("trunc i64 %arg_bits to i32", llvm);
        Assert.Contains("getelementptr inbounds i32, ptr %slot_value, i32 0", llvm);
        Assert.Contains("getelementptr inbounds i8, ptr %", llvm);
        Assert.Contains("store i32", llvm);
        Assert.Contains("ptr @Counter", llvm);
        Assert.Contains("load i32, ptr @Counter", llvm);
    }

    private static CompilationResult Compile(string source, CompilerOptions? options = null)
    {
        return DefaultCompilerPipeline.Create().Run(new CompilationInput(source), options);
    }

    private static string GetLlvm(CompilationResult result)
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
}
