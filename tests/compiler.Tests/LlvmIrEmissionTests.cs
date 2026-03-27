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

            fn i32 Run() {
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

            fn i32 Run() {
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

            fn i32 Run(bool flag) {
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
            export static rawptr<i8> Visible = null;

            fn i32 Run() {
                return 0;
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("; visibility: public", llvm);
        Assert.Contains("@Answer = external constant i32", llvm);
        Assert.Contains("; visibility: internal", llvm);
        Assert.Contains("@Buffer = external global ptr", llvm);
        Assert.Contains("; visibility: export", llvm);
        Assert.Contains("@Visible = external global ptr", llvm);
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

            fn ascii Echo(ascii text) {
                return text;
            }

            fn ascii Run() {
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

            fn ascii AsciiChar() {
                return 'a';
            }

            fn unicode UnicodeChar() {
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

            static i32 Counter = 0;

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
