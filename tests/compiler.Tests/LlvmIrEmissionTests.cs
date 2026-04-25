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

            finite law i32[-2147483648 2147483647] Run() {
                stack mut i32[-2147483648 2147483647] value = 1;
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
    public void FunctionPointerCallsEmitFastccIndirectCall()
    {
        var result = Compile(
            """
            module Demo

            fn i32[-2147483648 2147483647] Add(i32[-2147483648 2147483647] left, i32[-2147483648 2147483647] right) {
                return left + right;
            }

            fn i32[-2147483648 2147483647] Run() {
                stack fnptr<fn i32[-2147483648 2147483647](i32[-2147483648 2147483647], i32[-2147483648 2147483647])> op = Add;
                return op(40, 2);
            }
            """,
            options: new CompilerOptions(OptimizationLevel: CompilerOptimizationLevel.O0));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);

        Assert.Contains("call fastcc i32 @Add(", llvm);
    }

    [Fact]
    public void SmallRangeUnsignedIntegerOperationsUseUnsignedLlvmOpcodes()
    {
        var result = Compile(
            """
            module Demo

            fn bool Less(u8[0 127] left, u8[0 127] right) {
                return left < right;
            }

            fn u8[0 127] Divide(u8[0 127] left, u8[0 127] right) {
                return left / right;
            }

            fn u8[0 127] Remainder(u8[0 127] left, u8[0 127] right) {
                return left % right;
            }

            fn u8[0 127] ShiftDown(u8[0 127] left, u8[0 127] right) {
                return left >> right;
            }
            """,
            options: new CompilerOptions(OptimizationLevel: CompilerOptimizationLevel.O0));

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

            fn i32[-2147483648 2147483647] Run() {
                stack fnptr<fn i32[-2147483648 2147483647](i32[-2147483648 2147483647])> increment = (i32[-2147483648 2147483647] value) => value + 1;
                return increment(41);
            }
            """,
            options: new CompilerOptions(OptimizationLevel: CompilerOptimizationLevel.O0));

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

            fn i32[-2147483648 2147483647] Apply(fnptr<fn i32[-2147483648 2147483647](i32[-2147483648 2147483647])> op) {
                return op(41);
            }

            fn i32[-2147483648 2147483647] Run() {
                return Apply((i32[-2147483648 2147483647] value) => value + 1);
            }
            """,
            options: new CompilerOptions(OptimizationLevel: CompilerOptimizationLevel.O0));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);

        Assert.Contains("; synthetic definition: Run.__lambda_", llvm);
        Assert.Contains("call fastcc i32 @Apply(ptr @Run___lambda_", llvm);
    }

    [Fact]
    public void DebugMetadataIsEmittedForFunctionsParametersAndStackLocals()
    {
        var result = Compile(
            """
            module Demo

            fn i32[-2147483648 2147483647] Run(i32[-2147483648 2147483647] input) {
                stack mut i32[-2147483648 2147483647] value = input;
                stack rawmutptr<i32[-2147483648 2147483647]> ptr = &value;
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
        Assert.Contains("!DILocation(line: 4, column:", llvm);
        Assert.Contains(", !dbg !", llvm);
    }

    [Fact]
    public void DebugMetadataMarksOptimizedAndUnoptimizedBuildsAccurately()
    {
        var optimized = Compile(
            """
            module Demo

            fn i32[-2147483648 2147483647] Run() {
                return 1;
            }
            """);
        var debugFriendly = Compile(
            """
            module Demo

            fn i32[-2147483648 2147483647] Run() {
                return 1;
            }
            """,
            options: new CompilerOptions(OptimizationLevel: CompilerOptimizationLevel.O0));

        Assert.True(optimized.Succeeded);
        Assert.True(debugFriendly.Succeeded);

        Assert.Contains("isOptimized: true", GetLlvm(optimized));
        Assert.Contains("isOptimized: false", GetLlvm(debugFriendly));
    }

    [Fact]
    public void ConstantBranchConditionsCanFoldAllTheWayToReturn()
    {
        var result = Compile(
            """
            module Demo

            finite law i32[-2147483648 2147483647] Run() {
                if (true) {
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

            finite law i32[-2147483648 2147483647] Run(bool flag) {
                stack mut i32[-2147483648 2147483647] value = 0;
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

        Assert.Contains("br i1 %arg_flag, label %bb1, label %bb2", llvm);
        Assert.Contains("ret i32 1", llvm);
        Assert.Contains("ret i32 2", llvm);
        Assert.DoesNotContain("phi i32", llvm);
    }

    [Fact]
    public void IfWeightAnnotationEmitsBranchWeightMetadata()
    {
        var result = Compile(
            """
            module Demo

            finite law i32[-2147483648 2147483647] Run(bool flag) {
                stack mut i32[-2147483648 2147483647] value = 0;
                if w99 (flag) {
                    value = 1;
                } else {
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

            finite law i32[-2147483648 2147483647] Pick(i32[0 5] value) {
                switch w75 (value) {
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

        Assert.Matches(@"switch i32 %arg_value, label %bb\d+ \[ .* \], !prof !\d+", llvm);
        Assert.Matches(@"!\d+ = !\{!""branch_weights"", i32 25, i32 15, i32 15, i32 15, i32 15, i32 15\}", llvm);
    }

    [Fact]
    public void ColdCallBranchTargetsInferUnlikelyBranchWeightMetadata()
    {
        var result = Compile(
            """
            module Demo

            cold noinline fn i32[-2147483648 2147483647] Slow(i32[-2147483648 2147483647] value) {
                return value + 1;
            }

            finite law i32[-2147483648 2147483647] Run(bool fail, i32[-2147483648 2147483647] value) {
                if (fail) {
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
            internal static rawptr<i8[-128 127]> Buffer = null;
            export static mut rawptr<i8[-128 127]> Visible = null;

            finite law i32[-2147483648 2147483647] Run() {
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

            static mut i32[-2147483648 2147483647] Counter = 0;

            finite i32[-2147483648 2147483647] Run() {
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

            public const Answer = 42;
            internal static mut i32[-2147483648 2147483647] Counter = 0;
            static i32[-2147483648 2147483647] Hidden = 1;
            export static mut i32[-2147483648 2147483647] Visible = 0;

            finite i32[-2147483648 2147483647] Run() {
                Counter = 7;
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
        Assert.Contains("store i32 7, ptr @Math_Counter", llvm);
        Assert.Contains("load i32, ptr @Math_Counter", llvm);
    }

    [Fact]
    public void RootFunctionSymbolIsQualifiedWhenItWouldCollideWithFfiImport()
    {
        var result = Compile(
            """
            import Win32
            module System.Runtime.Platform

            internal fn void ExitProcess(i32[-2147483648 2147483647] code) {
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

                        internal ffi fn void ExitProcess(i32[-2147483648 2147483647] code);
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

            public ffi varargs fn i32[min max] printf(ascii format);

            export ffi fn i32[min max] main() {
                stack i32[min max] score = 42;
                return printf("score=%d", score);
            }
            """,
            new CompilerOptions(
                EmitLlvmIr: true,
                OptimizationLevel: CompilerOptimizationLevel.O0));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvm(result);

        Assert.Contains("declare i32 @printf(ptr readonly, ...)", llvm);
        Assert.Contains("call i32 (ptr, ...) @printf(", llvm);
        Assert.Contains("i32 %", llvm);
    }

    [Fact]
    public void InternalizedImmutableGlobalsUseUnnamedAddrOnlyWhenAddressesStayInsignificant()
    {
        var result = Compile(
            """
            module Demo

            static i32[-2147483648 2147483647] Hidden = 1;
            static i32[-2147483648 2147483647] Exposed = 2;

            fn rawptr<i32[-2147483648 2147483647]> ExposedPtr() {
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

            struct Pair {
                i32[-2147483648 2147483647] Left;
                i32[-2147483648 2147483647] Right;
            }

            const Pair Origin = new Pair() { Left = 1, Right = 2 };
            static i32[-2147483648 2147483647][3] Values = { 4, 7, 9 };

            finite i32[-2147483648 2147483647] Run() {
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
    public void GlobalsAndUnicodeStringConstantsEmitConcreteAlignment()
    {
        var result = Compile(
            """
            module Demo

            struct Pair {
                i32[-2147483648 2147483647] Left;
                i32[-2147483648 2147483647] Right;
            }

            static mut i32[-2147483648 2147483647] Counter = 0;
            const Pair Origin = new Pair() { Left = 1, Right = 2 };
            const i32[-2147483648 2147483647][3] Values = { 4, 7, 9 };

            finite law unicode Greek() {
                return (unicode)"\u03B1";
            }

            finite i32[-2147483648 2147483647] Run() {
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

            fn ascii Label() {
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

            static rawptr<i8[-128 127]> Buffer = null;
            const ascii Label = "Hi";

            finite i32[-2147483648 2147483647] Run() {
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
            static rawptr<i8[-128 127]> Buffer = null;
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
        Assert.Contains("@Value = local_unnamed_addr constant double 3.5, align 4", llvm);
        Assert.Contains("@FloatValue = local_unnamed_addr constant float 3.5, align 4", llvm);
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

            struct Pair {
                i32[-2147483648 2147483647] Left;
                i32[-2147483648 2147483647] Right;
            }

            internal const Pair Origin = new Pair() { Left = 1, Right = 2 };
            internal static i32[-2147483648 2147483647][3] Values = { 4, 7, 9 };

            finite i32[-2147483648 2147483647] Run() {
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
            const i32[-2147483648 2147483647][1 + 2] Values = { 4, 7, 9 };

            finite i32[-2147483648 2147483647] Run() {
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

            record Point(i32[-2147483648 2147483647] X) {
                i32[-2147483648 2147483647] Y;
            }

            const Point Origin = new Point(3) { Y = 9 };

            finite i32[-2147483648 2147483647] Run() {
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

            const i32[-2147483648 2147483647][3] Values = { 4, 7, 9 };

            finite i32[-2147483648 2147483647] Run() {
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

            struct Inner {
                i32[-2147483648 2147483647] Value;
            }

            struct Outer {
                Inner Item;
                ascii Label;
            }

            const Outer Graph = new Outer() {
                Item = new Inner() { Value = 7 },
                Label = "ok"
            };

            finite i32[-2147483648 2147483647] Run() {
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

            struct Inner {
                i32[-2147483648 2147483647][2] Pair;
            }

            struct Outer {
                Inner Node;
                i32[-2147483648 2147483647][3] View;
            }

            const Outer Frozen = {
                Node = { Pair = { 4, 7 } },
                View = { 1, 2, 3 }
            };

            finite i32[-2147483648 2147483647] Run() {
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

            struct Pair {
                i32[-2147483648 2147483647] Left;
                i32[-2147483648 2147483647] Right;
            }

            static mut Pair Current = new Pair() { Left = 5, Right = 8 };
            static mut i32[-2147483648 2147483647][3] Values = { 1, 2, 3 };

            finite i32[-2147483648 2147483647] Run() {
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
                i32[-2147483648 2147483647][2] Values;
            }

            static Buffer Shared = { Values = { 5, 8 } };

            finite i32[-2147483648 2147483647] Run() {
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

            struct Box {
                i32[-2147483648 2147483647] Value;
            }

            static Box Current = new Box() { Value = 5 };
            const Box Frozen = new Box() { Value = 7 };
            static i32[-2147483648 2147483647][3] Values = { 1, 2, 3 };
            static mut i32[-2147483648 2147483647] Counter = 1;

            fn i32[-2147483648 2147483647] ReadStatic() {
                return Current.Value;
            }

            fn i32[-2147483648 2147483647] ReadConst() {
                return Frozen.Value;
            }

            fn i32[-2147483648 2147483647] ReadIndex(i32[0 2] index) {
                return Values[index];
            }

            fn i32[-2147483648 2147483647] ReadMutable() {
                return Counter;
            }
            """);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);

        Assert.Matches(@"load %Box, ptr @Current, align 4, !invariant\.load !\d+", llvm);
        Assert.Matches(@"load %Box, ptr @Frozen, align 4, !invariant\.load !\d+", llvm);
        Assert.Matches(@"load i32, ptr %v\d+, align 4, !invariant\.load !\d+", llvm);
        Assert.DoesNotContain("load i32, ptr @Counter, !invariant.load", llvm);
    }

    [Fact]
    public void OnceInitializedReadonlyStackStorageEmitsInvariantStartAndLoadMetadata()
    {
        var result = Compile(
            """
            module Demo

            fn void Observe(rawptr<i32[-2147483648 2147483647]> ptr) {
                stack i32[-2147483648 2147483647] ignored = *ptr;
                return;
            }

            fn i32[-2147483648 2147483647] ReadOnce(i32[-2147483648 2147483647] input) {
                stack i32[-2147483648 2147483647] value = input;
                stack rawptr<i32[-2147483648 2147483647]> ptr = &value;
                return *ptr;
            }

            fn i32[-2147483648 2147483647] ReadEscaped(i32[-2147483648 2147483647] input) {
                stack i32[-2147483648 2147483647] escaped = input;
                Observe(&escaped);
                return *(&escaped);
            }

            fn i32[-2147483648 2147483647] ReadMutable(i32[-2147483648 2147483647] input) {
                stack mut i32[-2147483648 2147483647] mutable = input;
                stack rawmutptr<i32[-2147483648 2147483647]> ptr = &mutable;
                *ptr = input + 1;
                return *ptr;
            }
            """,
            options: new CompilerOptions(OptimizationLevel: CompilerOptimizationLevel.O0));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);

        Assert.Contains("declare ptr @llvm.invariant.start.p0(i64 immarg, ptr nocapture)", llvm);
        Assert.Matches(@"call ptr @llvm\.invariant\.start\.p0\(i64 4, ptr %slot_value\)", llvm);
        Assert.Matches(@"load i32, ptr %v\d+, align 4, !invariant\.load !\d+", llvm);
        Assert.DoesNotContain("call ptr @llvm.invariant.start.p0(i64 4, ptr %slot_escaped)", llvm);
        Assert.DoesNotContain("call ptr @llvm.invariant.start.p0(i64 4, ptr %slot_mutable)", llvm);

        var mutableDefinitionIndex = llvm.IndexOf("define fastcc noundef i32 @ReadMutable", StringComparison.Ordinal);
        Assert.True(mutableDefinitionIndex >= 0);
        Assert.DoesNotContain("!invariant.load", llvm[mutableDefinitionIndex..]);
    }

    [Fact]
    public void FrozenRawPointerLoadsEmitInvariantMetadata()
    {
        var result = Compile(
            """
            module Demo

            struct Big {
                i64[-9223372036854775808 9223372036854775807] A;
                i64[-9223372036854775808 9223372036854775807] B;
                i64[-9223372036854775808 9223372036854775807] C;
            }

            fn i64[-9223372036854775808 9223372036854775807] Read(rawptr<frozen Big> box) {
                return (*box).B;
            }
            """);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);

        Assert.Matches(@"load %Big, ptr %arg_box, !invariant\.load !\d+", llvm);
    }

    [Fact]
    public void TypedScalarLoadsAndStoresEmitConservativeStarkTbaa()
    {
        var result = Compile(
            """
            module Demo

            fn i32[0 10] Run(i32[0 9] input) {
                stack mut i32[0 10] value = input;
                return *(&value);
            }
            """,
            options: new CompilerOptions(OptimizationLevel: CompilerOptimizationLevel.O0));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);

        Assert.Contains("!\"Stark TBAA\"", llvm);
        Assert.Contains("!\"stark.i32\"", llvm);
        Assert.Matches(@"store i32 %v\d+, ptr %slot_value, align 4, !tbaa !\d+", llvm);
        Assert.Matches(@"load i32, ptr %v\d+, align 4, !range !\d+, !tbaa !\d+", llvm);
    }

    [Fact]
    public void TypedFieldAndFixedArrayElementLoadsEmitStructPathTbaa()
    {
        var result = Compile(
            """
            module Demo

            struct Buffer {
                i32[0 100] Header;
                i32[0 100][4] Values;
            }

            fn i32[0 100] ReadHeader(borrow Buffer buffer) {
                return *(&buffer.Header);
            }

            fn i32[0 100] ReadElement(borrow Buffer buffer) {
                return *(&buffer.Values[2]);
            }
            """,
            options: new CompilerOptions(OptimizationLevel: CompilerOptimizationLevel.O0));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);

        Assert.Contains("!\"Stark TBAA\"", llvm);
        Assert.Contains("!\"stark.Buffer\"", llvm);
        Assert.Contains("!\"stark.array.i32[0 100][4]\"", llvm);
        Assert.Matches(@"load i32, ptr %v\d+, !range !\d+, !tbaa !\d+", llvm);
        Assert.Matches(@"!\d+ = !\{!\d+, !\d+, i64 0\}", llvm);
        Assert.Matches(@"!\d+ = !\{!\d+, !\d+, i64 12\}", llvm);
    }

    [Fact]
    public void RawPointerAndPointerIntegerEscapesSuppressTbaa()
    {
        var rawPointerResult = Compile(
            """
            module Demo

            fn i32[0 10] Read(rawptr<i32[0 10]> ptr) {
                return *ptr;
            }
            """,
            options: new CompilerOptions(OptimizationLevel: CompilerOptimizationLevel.O0));

        Assert.True(rawPointerResult.Succeeded, string.Join(Environment.NewLine, rawPointerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var rawPointerLlvm = GetLlvmRaw(rawPointerResult);

        Assert.DoesNotContain("!tbaa", rawPointerLlvm);
        Assert.DoesNotContain("!\"Stark TBAA\"", rawPointerLlvm);

        var rawCastResult = Compile(
            """
            module Demo

            fn i32[0 10] ReadCast() {
                stack mut i32[0 10] value = 1;
                stack rawmutptr<i32[0 10]> mutablePtr = &value;
                stack rawptr<i32[0 10]> readonlyPtr = (rawptr<i32[0 10]>)mutablePtr;
                return *readonlyPtr;
            }
            """,
            options: new CompilerOptions(OptimizationLevel: CompilerOptimizationLevel.O0));

        Assert.True(rawCastResult.Succeeded, string.Join(Environment.NewLine, rawCastResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var rawCastLlvm = GetLlvmRaw(rawCastResult);

        Assert.Contains("getelementptr inbounds nuw i32, ptr %slot_value, i32 0", rawCastLlvm);
        Assert.DoesNotContain("getelementptr inbounds nuw i8", rawCastLlvm);
        Assert.DoesNotContain("ptrtoint", rawCastLlvm);
        Assert.DoesNotContain("inttoptr", rawCastLlvm);
        Assert.DoesNotContain("!tbaa", rawCastLlvm);

        var escapedResult = Compile(
            """
            module Demo

            fn i32[0 10] ReadEscaped() {
                stack mut i32[0 10] value = 1;
                stack rawmutptr<i32[0 10]> ptr = &value;
                if ((i64[-9223372036854775808 9223372036854775807])ptr == 0) {
                    return 0;
                }

                return value;
            }
            """,
            options: new CompilerOptions(OptimizationLevel: CompilerOptimizationLevel.O0));

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

            struct Box {
                i32[0 100] Value;
            }

            fn i32[0 100] Sum(borrow mut Box left, borrow mut Box right) {
                left.Value = 7;
                return right.Value;
            }
            """,
            options: new CompilerOptions(OptimizationLevel: CompilerOptimizationLevel.O0));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);

        Assert.Contains("!\"stark.noalias.Sum\"", llvm);
        Assert.Matches(@"store i32 .* ptr %v\d+, !tbaa !\d+, !alias\.scope !\d+, !noalias !\d+", llvm);
        Assert.Matches(@"load %Box, ptr %v\d+, !alias\.scope !\d+, !noalias !\d+", llvm);
        Assert.Matches(@"!\d+ = distinct !\{!\d+, !""stark\.noalias\.Sum""\}", llvm);
        Assert.Matches(@"!\d+ = distinct !\{!\d+, !\d+, !""stark\.noalias\.Sum\.param\.left""\}", llvm);
        Assert.Matches(@"!\d+ = distinct !\{!\d+, !\d+, !""stark\.noalias\.Sum\.param\.right""\}", llvm);
    }

    [Fact]
    public void RawPointerMemoryAccessesDoNotEmitScopedNoAliasMetadata()
    {
        var result = Compile(
            """
            module Demo

            fn i32[0 100] Read(rawptr<i32[0 100]> left, rawptr<i32[0 100]> right) {
                return *left + *right;
            }
            """,
            options: new CompilerOptions(OptimizationLevel: CompilerOptimizationLevel.O0));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);

        Assert.DoesNotContain("!alias.scope", llvm);
        Assert.DoesNotContain("!noalias", llvm);
        Assert.DoesNotContain("!\"stark.noalias", llvm);
    }

    [Fact]
    public void RawPointerConstNullGlobalsRemainExternalPlaceholders()
    {
        var result = Compile(
            """
            module Demo

            const rawptr<i8[-128 127]> stdout = null;

            finite law i32[-2147483648 2147483647] Run() {
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

            fn i32[-2147483648 2147483647] Run(i32[-2147483648 2147483647] left, i32[-2147483648 2147483647] right) {
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

            fn i32[-2147483648 2147483647] Run(i32[-2147483648 2147483647] left, i32[-2147483648 2147483647] middle, i32[-2147483648 2147483647] right, i32[-2147483648 2147483647] mask) {
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
    public void OrdinaryIntegerArithmeticEmitsSignedNoWrapFlags()
    {
        var result = Compile(
            """
            module Demo

            fn i32[-2147483648 2147483647] Add(i32[-2147483648 2147483647] left, i32[-2147483648 2147483647] right) {
                return left + right;
            }

            fn i32[-2147483648 2147483647] Sub(i32[-2147483648 2147483647] left, i32[-2147483648 2147483647] right) {
                return left - right;
            }

            fn i32[-2147483648 2147483647] Mul(i32[-2147483648 2147483647] left, i32[-2147483648 2147483647] right) {
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
    public void NonNegativeConstrainedOrdinaryArithmeticEmitsUnsignedNoWrapWhenProven()
    {
        var result = Compile(
            """
            module Demo

            fn i32[-2147483648 2147483647] Add(i32[0 10] left, i32[0 20] right) {
                return left + right;
            }

            fn i32[-2147483648 2147483647] Sub(i32[20 30] left, i32[0 10] right) {
                return left - right;
            }

            fn i32[-2147483648 2147483647] Mul(i32[0 10] left, i32[0 5] right) {
                return left * right;
            }
            """);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvm(result);

        Assert.Contains("add nuw nsw i32", llvm);
        Assert.Contains("sub nuw nsw i32", llvm);
        Assert.Contains("mul nuw nsw i32", llvm);
    }

    [Fact]
    public void OrdinaryIntegerArithmeticDoesNotEmitUnsignedNoWrapWhenNegativeValuesArePossible()
    {
        var result = Compile(
            """
            module Demo

            fn i32[-2147483648 2147483647] Add(i32[-1 10] left, i32[0 20] right) {
                return left + right;
            }
            """);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvm(result);

        Assert.Contains("add nsw i32", llvm);
        Assert.DoesNotContain("add nuw", llvm);
    }

    [Fact]
    public void ProvenShiftLeftRangesEmitNoWrapFlags()
    {
        var result = Compile(
            """
            module Demo

            fn i32[-2147483648 2147483647] Both(i32[0 1073741823] value) {
                return value << 1;
            }

            fn i32[-2147483648 2147483647] UnsignedOnly(i32[0 2147483647] value) {
                return value << 1;
            }

            fn i32[-2147483648 2147483647] SignedOnly(i32[-4 -1] value) {
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

            fn u64[0 max] Divide(u64[0 max] left, u64[0 max] right) {
                return left / right;
            }

            fn u64[0 max] Remainder(u64[0 max] left, u64[0 max] right) {
                return left % right;
            }

            fn bool Greater(u64[0 max] left, u64[0 max] right) {
                return left > right;
            }

            fn u64[0 max] ShiftRight(u64[0 max] value) {
                return value >> 1;
            }

            fn u64[0 max] Widen(u32[0 max] value) {
                return (u64[0 max])value;
            }

            fn f64 ToFloat(u64[0 max] value) {
                return (f64)value;
            }

            fn u64[0 max] FromFloat(f64 value) {
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

            fn i32[-2147483648 2147483647] ShiftBack(i32[-536870912 536870911] value) {
                return (value << 2) >> 1;
            }

            fn i32[-2147483648 2147483647] DivideProduct(i32[-1000 1000] value) {
                return (value * 6) / 3;
            }

            fn i32[-2147483648 2147483647] DivideShift(i32[-1000 1000] value) {
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

            fn i32[-2147483648 2147483647] ShiftLeft(i32[-2147483648 2147483647] value) {
                return value << 1;
            }

            fn i32[-2147483648 2147483647] ShiftRight(i32[-2147483648 2147483647] value) {
                return value >> 1;
            }

            fn i32[-2147483648 2147483647] Divide(i32[-2147483648 2147483647] value, i32[2 2] divisor) {
                return value / divisor;
            }
            """);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvm(result);

        Assert.Contains("shl i32", llvm);
        Assert.Contains("ashr i32", llvm);
        Assert.Contains("sdiv i32", llvm);
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

            fn i32[-2147483648 2147483647] Run(i32[-2147483648 2147483647] left, i32[-2147483648 2147483647] right) {
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

            fn i32[-2147483648 2147483647] Run(i32[-2147483648 2147483647] left, i32[-2147483648 2147483647] right) {
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

            fn i32[-2147483648 2147483647] Wrap(i32[0 10] left, i32[0 5] right) {
                return left +% right *% 2 -% 1;
            }

            fn i32[-2147483648 2147483647] Sat(i32[0 10] left, i32[0 5] right) {
                return left +| right *| 2 -| 1;
            }
            """);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvm(result);

        Assert.Contains("mul i32", llvm);
        Assert.Contains("add i32", llvm);
        Assert.Contains("sub i32", llvm);
        Assert.Contains("mul i64", llvm);
        Assert.Contains("add i64", llvm);
        Assert.Contains("sub i64", llvm);
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

            fn i32[-2147483648 2147483647] Wrap() {
                return 2147483647 +% 1;
            }

            fn i32[-2147483648 2147483647] SatAdd() {
                return 2147483647 +| 1;
            }

            fn i32[-2147483648 2147483647] SatMul() {
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
    public void IntegerExponentExpressionsEmitInternalPowHelpers()
    {
        var result = Compile(
            """
            module Demo

            fn i32[-2147483648 2147483647] Run(i32[-2147483648 2147483647] left, i32[-2147483648 2147483647] right) {
                return left ** right;
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        var helperHeader = ExtractDefinitionHeader(llvm, "__stark_int_pow_i32");
        Assert.Contains("define internal dso_local i32 @__stark_int_pow_i32(i32 %base, i32 %exponent)", helperHeader);
        Assert.Contains("unnamed_addr", helperHeader);
        Assert.Contains("call i32 @__stark_int_pow_i32(", llvm);
        Assert.DoesNotContain("@llvm.pow.i32", llvm);
    }

    [Fact]
    public void FloatLiteralArgumentsEmitLlvmDecimalConstants()
    {
        var result = Compile(
            """
            module Demo

            fn f64 Echo(f64 value) {
                return value;
            }

            fn i32[-2147483648 2147483647] Run() {
                if (Echo(0.0) != 0.0) {
                    return 1;
                }

                if (Echo(3.0) != 3.0) {
                    return 2;
                }

                return 0;
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("call fast fastcc double @Echo(double 0.0)", llvm);
        Assert.Contains("fcmp fast one double %", llvm);
        Assert.Contains(", 0.0", llvm);
        Assert.Contains("call fast fastcc double @Echo(double 3.0)", llvm);
        Assert.Contains(", 3.0", llvm);
        Assert.DoesNotContain("call fast fastcc double @Echo(double 0)", llvm);
        Assert.DoesNotContain("call fast fastcc double @Echo(double 3)", llvm);
    }

    [Fact]
    public void LoopHeaderEmitsBackedgePhi()
    {
        var result = Compile(
            """
            module Demo

            fn i32[-2147483648 2147483647] Run() {
                stack mut i32[-2147483648 2147483647] i = 0;
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

            fn i32[-2147483648 2147483647] Run() {
                stack i32[-2147483648 2147483647] value = 1;
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

            fn i32[-2147483648 2147483647] Run(i32[-2147483648 2147483647] value, bool allow) {
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
    public void EnumSwitchExpressionCallEmitsSingleEvaluation()
    {
        var result = Compile(
            """
            module Demo

            enum Status {
                Ok,
                Err(i32[-2147483648 2147483647]),
            }

            fn Status Next() {
                return Status.Ok;
            }

            fn i32[-2147483648 2147483647] Run() {
                switch (Next()) {
                    case Status.Ok:
                        return 1;
                    case Status.Err(var error):
                        return error;
                }
            }
            """);

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

            enum Token {
                Full(i32[-2147483648 2147483647], i32[-2147483648 2147483647], i32[-2147483648 2147483647], i32[-2147483648 2147483647], i32[-2147483648 2147483647]),
                Empty,
            }

            fn Token MakeEmpty() {
                return Token.Empty;
            }

            fn Token MakeFull(i32[-2147483648 2147483647] value) {
                return Token.Full(value, value, value, value, value);
            }

            fn i32[-2147483648 2147483647] Score(Token token) {
                switch (token) {
                    case Token.Empty:
                        return 0;
                    case Token.Full(var first, _, _, _, _):
                        return first;
                }
            }
            """,
            options: new CompilerOptions(OptimizationLevel: CompilerOptimizationLevel.O0));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);

        Assert.Contains("%Token = type { i8, i32, i32, i32, i32, i32 }", llvm);
        Assert.Contains("insertvalue %Token zeroinitializer, i8 0, 0", llvm);
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

            fn i32[-2147483648 2147483647] Run(i32[-2147483648 2147483647] value, bool allow) {
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

            fn i32[-2147483648 2147483647] Run(i32[-2147483648 2147483647] value, bool allow) {
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
        Assert.True(CountOccurrences(llvm, "icmp eq i32") >= 2);
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

            fn i32[-2147483648 2147483647] Next() {
                return 1;
            }

            fn bool Run() {
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

            fn bool Run(f32 low, f32 value, f32 high) {
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

            fn i32[-2147483648 2147483647] Run(ascii value, bool allow) {
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

            fn i32[-2147483648 2147483647] Run(ascii value) {
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

            fn i32[-2147483648 2147483647] Run(unicode value) {
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
        Assert.Contains("load i32, ptr", llvm);
        Assert.DoesNotContain("declare fastcc i32 @Run(%stark_unicode)", llvm);
    }

    [Fact]
    public void TextSlicesEmitShiftedAsciiAndUnicodeViews()
    {
        var result = Compile(
            """
            module Demo

            fn ascii SliceAscii(ascii text, i32[-2147483648 2147483647] start, i32[-2147483648 2147483647] length) {
                return text[start, length];
            }

            fn unicode SliceUnicode(unicode text, i32[-2147483648 2147483647] start, i32[-2147483648 2147483647] length) {
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

            fn unicode Run() {
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

            fn ascii Run() {
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

            fn ascii Run() {
                return $"Score: {100}";
            }
            """);

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvm(result);

        Assert.Contains("private unnamed_addr constant [11 x i8] c\"Score: 100\\00\"", llvm);
        Assert.DoesNotContain("Score: {100}", llvm);
    }

    [Fact]
    public void SystemTextOwnedConcatAndViewBuiltinsEmitConcreteDefinitions()
    {
        var result = Compile(
            """
            module System.Text

            public finite law ascii AsciiView(Ascii source);
            public finite law unicode UnicodeView(Unicode source);
            public fn bool TryConcatAscii(rawmutptr<Ascii> destination, ascii left, ascii right);
            public fn bool TryConcatUnicode(rawmutptr<Unicode> destination, unicode left, unicode right);

            public fn unicode Run() {
                stack mut i8[-128 127][16] asciiBuffer = { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
                stack mut Ascii ownedAscii = new Ascii() {
                    Data = &asciiBuffer[0],
                    Length = 0,
                    Capacity = 16
                };

                stack mut i32[-2147483648 2147483647][8] unicodeBuffer = { 0, 0, 0, 0, 0, 0, 0, 0 };
                stack mut Unicode ownedUnicode = new Unicode() {
                    Data = &unicodeBuffer[0],
                    Length = 0,
                    Capacity = 8
                };
                if (!TryConcatAscii(&ownedAscii, "Stark", " IO")) {
                    return (unicode)"";
                }

                if (!TryConcatUnicode(&ownedUnicode, (unicode)"Hi", (unicode)" \u03B1")) {
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
        Assert.Contains("%concat_left_index = phi i64", llvm);
        Assert.Contains("load i8, ptr %concat_left_src", llvm);
        Assert.Contains("store i32 %concat_right_unit, ptr %concat_right_dst", llvm);
        Assert.DoesNotContain("@llvm.memcpy", llvm);
        Assert.Contains("getelementptr inbounds nuw i32, ptr %concat_data, i64 %concat_left_length", llvm);
        Assert.Contains("call fastcc %stark_unicode @UnicodeView(", llvm);
        Assert.DoesNotContain("declare fastcc i1 @TryConcatAscii(", llvm);
        Assert.DoesNotContain("declare fastcc i1 @TryConcatUnicode(", llvm);
    }

    [Fact]
    public void SystemMathBuiltinsEmitConcreteDefinitionsAndLlvmIntrinsics()
    {
        var result = Compile(
            """
            module System.Math

            public struct SinCosF64 {
                f64 Sin;
                f64 Cos;
            }

            public finite law f32 Sin(f32 value);
            public finite law f64 Cos(f64 value);
            public finite law f64 Atan2(f64 y, f64 x);
            public finite law f64 Pow(f64 value, f64 exponent);
            public finite law f32 Tanh(f32 value);
            public finite law SinCosF64 SinCos(f64 value);
            public finite law f32 Min(f32 left, f32 right);
            public finite law f64 Max(f64 left, f64 right);
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

            public finite law i32[-2147483648 2147483647] LeadingZeroCount(i32[-2147483648 2147483647] value);
            public finite law i64[-9223372036854775808 9223372036854775807] TrailingZeroCount(i64[-9223372036854775808 9223372036854775807] value);
            public finite law i32[-2147483648 2147483647] PopCount(i32[-2147483648 2147483647] value);
            public finite law i64[-9223372036854775808 9223372036854775807] RotateLeft(i64[-9223372036854775808 9223372036854775807] value, i64[-9223372036854775808 9223372036854775807] amount);
            public finite law i32[-2147483648 2147483647] RotateRight(i32[-2147483648 2147483647] value, i32[-2147483648 2147483647] amount);
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

            public finite law f64 Sqrt(f64 value);
            public finite law f32 FusedMultiplyAdd(f32 left, f32 right, f32 addend);
            public finite law f64 FusedMultiplyAdd(f64 left, f64 right, f64 addend);
            public finite law f32 ReciprocalEstimate(f32 value);
            public finite law f32 ReciprocalSqrtEstimate(f32 value);
            public finite law f32 Ceiling(f32 value);
            public finite law f64 Floor(f64 value);
            public finite law f64 Truncate(f64 value);
            public finite law f64 Round(f64 value);
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

            public finite law f64 Sqrt(f64 value);
            public finite law f32 FusedMultiplyAdd(f32 left, f32 right, f32 addend);
            public finite law f64 FusedMultiplyAdd(f64 left, f64 right, f64 addend);
            public finite law f32 ReciprocalEstimate(f32 value);
            public finite law f32 ReciprocalSqrtEstimate(f32 value);
            public finite law f32 Ceiling(f32 value);
            public finite law f64 Floor(f64 value);
            public finite law f64 Truncate(f64 value);
            public finite law f64 Round(f64 value);
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

            fn f64 Run(f64 value) {
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

                        public finite law f64 Sqrt(f64 value);
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

            fn i32[-2147483648 2147483647] Run() {
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

                        public finite law f64 Sqrt(f64 value);
                        public finite law f64 Round(f64 value);
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

            fn i32[-2147483648 2147483647] Run(i32[-2147483648 2147483647] value) {
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

                        public finite law i32[-2147483648 2147483647] PopCount(i32[-2147483648 2147483647] value);
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

            ffi fn i32[-2147483648 2147483647] puts(ascii text);
            export ffi fn i32[-2147483648 2147483647] main() {
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
    public void InternalStringFunctionsUseConcreteStringAbi()
    {
        var result = Compile(
            """
            module Demo

            finite law ascii Echo(ascii text) {
                return text;
            }

            finite law ascii Run(ascii input) {
                return Echo(input);
            }
            """);

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

            finite law ascii AsciiChar() {
                return 'a';
            }

            finite law unicode UnicodeChar() {
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

            finite law unicode Greek() {
                return (unicode)"\u03B1";
            }

            finite law unicode Accented() {
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

            finite law ascii Run() {
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

            finite law unicode One() {
                return (unicode)"\u03B1";
            }

            finite law unicode Two() {
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

            struct Box {
                i32[-2147483648 2147483647] Value;
            }

            fn i32[-2147483648 2147483647] Add(i32[-2147483648 2147483647] left, i32[-2147483648 2147483647] right) {
                return left + right;
            }

            fn i32[-2147483648 2147483647] Read(borrow Box box) {
                return box.Value;
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("define fastcc i32 @Add(i32 %arg_left, i32 %arg_right) nounwind willreturn mustprogress nosync nofree memory(none)", llvm);
        Assert.Contains("memory(argmem: read)", llvm);
    }

    [Fact]
    public void BorrowParametersEmitCapturesAttributes()
    {
        var result = Compile(
            """
            module Demo

            struct Box {
                i32[-2147483648 2147483647] Value;
            }

            fn retborrow Box Echo(retborrow Box value) {
                return value;
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("captures(ret: address, read_provenance)", llvm);
    }

    [Fact]
    public void RetborrowScalarReturnsUsePointerAbiAndCanBeWrittenThrough()
    {
        var result = Compile(
            """
            module Demo

            struct Box {
                i32[-2147483648 2147483647] Value;

                fn retborrow mut i32[-2147483648 2147483647] Field(mut borrow Box self) {
                    return self.Value;
                }
            }

            fn i32[-2147483648 2147483647] Run() {
                stack mut Box box = new Box() { Value = 0 };
                box.Field() = 7;
                return box.Field();
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvmRaw(result);
        var fieldHeader = ExtractDefinitionHeader(llvm, "Box_Field");

        Assert.Contains("define fastcc noundef ptr @Box_Field(ptr noundef", fieldHeader);
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

            fn i32[-2147483648 2147483647] Run(i32[-2147483648 2147483647] value, bool allow) {
                if (allow) {
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

            struct Box {
                i32[-2147483648 2147483647] Value;
            }

            fn i32[-2147483648 2147483647] Read(borrow Box box) {
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

            record Pair(i32[0 10] Left, i32[0 10] Right) { }
            record Big(
                i64[-9223372036854775808 9223372036854775807] A,
                i64[-9223372036854775808 9223372036854775807] B,
                i64[-9223372036854775808 9223372036854775807] C) { }

            fn rawptr<i8[-128 127]> Pick(rawptr<i8[-128 127]> ptr) {
                return ptr;
            }

            fn ascii Echo(ascii text) {
                return text;
            }

            fn Pair Keep(Pair pair) {
                return pair;
            }

            fn Big Copy(Big value) {
                return value;
            }
            """,
            options: new CompilerOptions(OptimizationLevel: CompilerOptimizationLevel.O0));

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

            struct Box {
                i32[-2147483648 2147483647] Value;
            }

            fn i32[-2147483648 2147483647] ReadBorrow(borrow Box box) {
                return box.Value;
            }

            fn i32[-2147483648 2147483647] ReadView(borrow i32[-2147483648 2147483647][] view, i32[-2147483648 2147483647] index) {
                return view[index];
            }

            fn void RawInternal(rawptr<Box> ptr) {
                return;
            }

            ffi fn void RawForeign(rawptr<Box> ptr);
            """);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);

        Assert.Contains(
            "define fastcc noundef i32 @ReadBorrow(ptr noundef nonnull noalias readonly nocapture dereferenceable(4) align 4 %arg_box)",
            ExtractDefinitionHeader(llvm, "ReadBorrow"));
        Assert.Contains(
            "define fastcc noundef i32 @ReadView(ptr noundef nonnull noalias readonly nocapture dereferenceable(16) align 8 %arg_view, i32 noundef %arg_index)",
            ExtractDefinitionHeader(llvm, "ReadView"));

        var rawHeader = ExtractDefinitionHeader(llvm, "RawInternal");
        Assert.Contains("ptr noundef readonly nocapture %arg_ptr", rawHeader);
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

            fn void Fill(rawmutptr<i32[-2147483648 2147483647]> ptr);

            fn void Run(rawmutptr<i32[-2147483648 2147483647]> ptr) {
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

            ffi fn i32[-2147483648 2147483647] Fill(rawmutptr<i32[-2147483648 2147483647]> ptr);

            fn i32[-2147483648 2147483647] Run(rawmutptr<i32[-2147483648 2147483647]> ptr) {
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

            fn i32[0 10] Bounded() {
                return 7;
            }

            fn i32[0 10] ReadThroughPointer(i32[0 10] input) {
                stack mut i32[0 10] value = input;
                stack rawmutptr<i32[0 10]> ptr = &value;
                return *ptr;
            }

            fn i32[0 10] CallBounded() {
                return Bounded();
            }
            """,
            options: new CompilerOptions(OptimizationLevel: CompilerOptimizationLevel.O0));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);

        Assert.Matches(@"load i32, ptr %v\d+, align 4, !range !\d+", llvm);
        Assert.Matches(@"call fastcc i32 @Bounded\(\), !range !\d+", llvm);
        Assert.Contains("!{i32 0, i32 11}", llvm);
    }

    [Fact]
    public void ConstrainedIntegerAbiSurfacesEmitRangeAttributes()
    {
        var result = Compile(
            """
            module Demo

            fn i32[0 10] Bounded(i32[0 10] input) {
                return input;
            }

            fn i32[0 10] CallBounded(i32[0 10] input) {
                return Bounded(input);
            }
            """,
            options: new CompilerOptions(OptimizationLevel: CompilerOptimizationLevel.O0));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);

        Assert.Contains("define fastcc noundef range(i32 0, 11) i32 @Bounded(i32 noundef range(i32 0, 11) %arg_input)", llvm);
        Assert.Contains("define fastcc noundef range(i32 0, 11) i32 @CallBounded(i32 noundef range(i32 0, 11) %arg_input)", llvm);
        Assert.Matches(@"call fastcc i32 @Bounded\(i32 range\(i32 0, 11\) %arg_input\), !range !\d+", llvm);
        Assert.Contains("!{i32 0, i32 11}", llvm);
    }

    [Fact]
    public void BranchRefinedIntegerComparisonsEmitTargetedAssumes()
    {
        var result = Compile(
            """
            module Demo

            fn i32[-2147483648 2147483647] Range(i32[-2147483648 2147483647] value) {
                if (value >= 0) {
                    return value;
                }

                return 0;
            }
            """,
            options: new CompilerOptions(OptimizationLevel: CompilerOptimizationLevel.O0));

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

            fn i32[-2147483648 2147483647] Read(rawptr<i32[-2147483648 2147483647]> ptr) {
                if (ptr != null) {
                    return *ptr;
                }

                return 0;
            }
            """,
            options: new CompilerOptions(OptimizationLevel: CompilerOptimizationLevel.O0));

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

            fn i64[-9223372036854775808 9223372036854775807] ReadIfSame(rawptr<i64[-9223372036854775808 9223372036854775807]> ptr) {
                stack i64[-9223372036854775808 9223372036854775807] value = 9;
                if (ptr == &value) {
                    return *ptr;
                }

                return 0;
            }
            """,
            options: new CompilerOptions(OptimizationLevel: CompilerOptimizationLevel.O0));

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

            fn i32[0 10] BoundaryOnly(i32[0 10] value) {
                return value;
            }

            fn i32[-2147483648 2147483647] Plain(bool flag, i32[-2147483648 2147483647] value) {
                if (flag) {
                    return value;
                }

                return 0;
            }
            """,
            options: new CompilerOptions(OptimizationLevel: CompilerOptimizationLevel.O0));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);

        Assert.DoesNotContain("@llvm.assume", llvm);
        Assert.Contains("define fastcc noundef range(i32 0, 11) i32 @BoundaryOnly(i32 noundef range(i32 0, 11) %arg_value)", llvm);
    }

    [Fact]
    public void SignedConstrainedIntegerLoadsEmitWrappingRangeMetadata()
    {
        var result = Compile(
            """
            module Demo

            fn i32[-10 10] ReadThroughPointer(i32[-10 10] input) {
                stack mut i32[-10 10] value = input;
                stack rawmutptr<i32[-10 10]> ptr = &value;
                return *ptr;
            }
            """,
            options: new CompilerOptions(OptimizationLevel: CompilerOptimizationLevel.O0));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);

        Assert.Matches(@"load i32, ptr %v\d+, align 4, !range !\d+", llvm);
        Assert.Contains("!{i32 -10, i32 11}", llvm);
    }

    [Fact]
    public void MemoryAttributesDistinguishArgumentAndOtherMemoryEffects()
    {
        var result = Compile(
            """
            module Demo

            struct Box {
                i32[-2147483648 2147483647] Value;
            }

            static mut i32[-2147483648 2147483647] Counter = 0;

            fn i32[-2147483648 2147483647] ReadGlobal() {
                return Counter;
            }

            fn void TouchArg(borrow mut Box box) {
                box.Value = 1;
                return;
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("define fastcc i32 @ReadGlobal()", llvm);
        Assert.Contains("memory(read, argmem: none)", llvm);
        Assert.Contains("define fastcc void @TouchArg(ptr nonnull noalias writeonly nocapture dereferenceable(4) align 4 %arg_box)", llvm);
        Assert.Contains("memory(argmem: readwrite)", llvm);
        Assert.DoesNotContain("load %Box, ptr %arg_box", llvm);
    }

    [Fact]
    public void MemoryAttributesKeepArgumentEffectsWhenSyntheticTemporariesAreNeeded()
    {
        var result = Compile(
            """
            module Demo

            struct Big {
                i64[-9223372036854775808 9223372036854775807] A;
                i64[-9223372036854775808 9223372036854775807] B;
                i64[-9223372036854775808 9223372036854775807] C;
                i64[-9223372036854775808 9223372036854775807] D;
            }

            struct Box {
                Big Value;
            }

            fn Big MakeBig() {
                return new Big() {
                    A = 1,
                    B = 2,
                    C = 3,
                    D = 4
                };
            }

            fn void Store(borrow mut Box box) {
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

            finite law ascii Controls() {
                return "\0\b\t\n\f\r\\\"\'";
            }

            finite law ascii HexChar() {
                return '\x41';
            }

            finite law unicode Wide() {
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

            ffi fn i32[-2147483648 2147483647] puts(ascii text);

            fn ascii Message() {
                return "Hello";
            }

            export ffi fn i32[-2147483648 2147483647] main() {
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

            struct Box {
                i32[-2147483648 2147483647] Value;
            }

            fn i32[-2147483648 2147483647] Run() {
                stack Box box = new Box() { Value = 41 };
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

            struct Box {
                i32[-2147483648 2147483647] Value;
            }

            fn i32[-2147483648 2147483647] Run() {
                stack mut Box box = new Box() { Value = 1 };
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

            struct Box {
                i32[-2147483648 2147483647] Value;
            }

            fn i32[-2147483648 2147483647] Run() {
                register Box box = new Box() { Value = 7 };
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

            fn f32 Add(f32 left, f32 right) {
                return left + right;
            }

            fn f32 Run(f32 left, f32 right) {
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

            fn i32[-2147483648 2147483647] Step(i32[-2147483648 2147483647] value) {
                return value + 1;
            }

            fn i32[-2147483648 2147483647] Run(i32[-2147483648 2147483647] value) {
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

            fn f32 Narrow(f64 value) {
                return (f32)value;
            }

            fn f64 Promote(f32 value) {
                return (f64)value;
            }

            fn f32 Negate(f32 value) {
                return -value;
            }

            fn f32 Pick(bool flag, f32 left, f32 right) {
                stack mut f32 value = left;
                if (flag) {
                    value = right;
                }

                return value;
            }
            """,
            options: new CompilerOptions(OptimizationLevel: CompilerOptimizationLevel.O0));

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

            fn f32 Accumulate(f32 acc, f32 left, f32 right) {
                return acc + left * right;
            }

            fn f32 AccumulateProductFirst(f32 acc, f32 left, f32 right) {
                return left * right + acc;
            }

            fn f32 SubtractAddend(f32 acc, f32 left, f32 right) {
                return left * right - acc;
            }

            fn f32 SubtractProduct(f32 acc, f32 left, f32 right) {
                return acc - left * right;
            }
            """,
            options: new CompilerOptions(OptimizationLevel: CompilerOptimizationLevel.O0));

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

            strictfp fn f32 Add(f32 left, f32 right) {
                return left + right;
            }

            strictfp fn f32 Run(f32 left, f32 right) {
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
    public void StrictFpMultiplyAddExpressionsDoNotUseContractionIntrinsic()
    {
        var result = Compile(
            """
            module Demo

            strictfp fn f32 Accumulate(f32 acc, f32 left, f32 right) {
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

            strictfp fn f64 Run(f32 small, f64 wide, i32[-2147483648 2147483647] integer) {
                stack f64 promoted = (f64)small;
                stack f32 narrowed = (f32)wide;
                stack f64 fromInteger = (f64)integer;
                stack i32[-2147483648 2147483647] backToInteger = (i32[-2147483648 2147483647])narrowed;
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
        var result = Compile(
            """
            module Demo

            fn i32[-2147483648 2147483647] Run(bool fail) {
                if (fail) {
                    return 1;
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

            struct Box {
                i32[-2147483648 2147483647] Value;
            }

            fn i32[-2147483648 2147483647] Run() {
                heap Box box = new Box() { Value = 7 };
                return box.Value;
            }
            """,
            options: new CompilerOptions(TargetInfo: new LlvmTargetInfo("x86_64-unknown-linux-gnu", null)));

        Assert.True(result.Succeeded);
        var llvm = GetLlvmRaw(result);

        Assert.DoesNotContain("@malloc(", llvm);
        Assert.DoesNotContain("@realloc(", llvm);
        Assert.DoesNotContain("@free(", llvm);
        Assert.Contains("define internal dso_local noalias nonnull noundef ptr @__stark_heap_alloc(i64 noundef %size, i64 noundef allocalign %alignment) unnamed_addr allocsize(0) allockind(\"alloc,uninitialized,aligned\") nounwind", llvm);
        Assert.Contains("define internal dso_local void @__stark_heap_free(ptr %ptr) unnamed_addr nounwind", llvm);
        Assert.Contains("define internal dso_local noalias nonnull noundef ptr @__stark_runtime_alloc(i64 noundef %size, i64 noundef allocalign %alignment) unnamed_addr allocsize(0) allockind(\"alloc,uninitialized,aligned\") nounwind", llvm);
        Assert.Contains("define internal dso_local ptr @__stark_os_allocate(i64 noundef %size) unnamed_addr nounwind", llvm);
        Assert.Contains("@__stark_alloc_lock = internal global i32 0, align 4", llvm);
        Assert.Contains("@__stark_alloc_bucket_16 = internal global ptr null, align 8", llvm);
        Assert.Contains("@__stark_alloc_bucket_4096 = internal global ptr null, align 8", llvm);
        Assert.Contains("define internal dso_local void @__stark_alloc_lock_acquire() unnamed_addr nounwind", llvm);
        Assert.Contains("atomicrmw xchg ptr @__stark_alloc_lock, i32 1 acquire, align 4", llvm);
        Assert.Contains("store atomic i32 0, ptr @__stark_alloc_lock release, align 4", llvm);
        Assert.Contains("%bucket_alignment_ok = icmp ule i64 %effective_alignment, 16", llvm);
        Assert.Contains("%bucket_size_ok = icmp ule i64 %requested_size, 4096", llvm);
        Assert.Contains("br i1 %can_bucket, label %bucket_select_16, label %large_allocate", llvm);
        Assert.Contains("%payload_size = phi i64 [16, %bucket_16_allocate]", llvm);
        Assert.Contains("[%requested_size, %large_allocate]", llvm);
        Assert.Contains("%block_alignment = phi i64 [16, %bucket_16_allocate]", llvm);
        Assert.Contains("[%effective_alignment, %large_allocate]", llvm);
        Assert.Contains("%bucket_size = phi i64 [16, %bucket_16_allocate]", llvm);
        Assert.Contains("[0, %large_allocate]", llvm);
        Assert.Contains("store i64 %bucket_size, ptr %bucket_size_slot, align 8", llvm);
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
    public void SystemMemoryInternalAllocatorBuiltinsLowerToRuntimeAllocatorContract()
    {
        var result = Compile(
            """
            module System.Memory

            public struct Allocator {
                u8[0 127] Kind;

                static finite law Allocator Default() {
                    return new Allocator() {
                        Kind = 0
                    };
                }

                finite law bool IsDefault(borrow Allocator self) {
                    return self.Kind == 0;
                }
            }

            internal struct Allocation {
                rawmutptr<i8[-128 127]> Pointer;
                i64[0 max] ByteLength;
                i64[1 max] Alignment;
                Allocator Allocator;
            }

            internal fn Allocation Allocate(Allocator allocator, i64[0 max] byteLength, i64[1 max] alignment);
            internal fn Allocation Reallocate(Allocation allocation, i64[0 max] byteLength, i64[1 max] alignment);
            internal fn void Free(Allocation allocation);

            export ffi fn i32[-2147483648 2147483647] main() {
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
        Assert.Contains("define internal dso_local noalias nonnull noundef ptr @__stark_runtime_alloc(i64 noundef %size, i64 noundef allocalign %alignment) unnamed_addr allocsize(0) allockind(\"alloc,uninitialized,aligned\") nounwind", llvm);
        Assert.Contains("define internal dso_local nonnull noundef ptr @__stark_runtime_realloc(ptr %old_ptr, i64 noundef %old_size, i64 noundef %new_size, i64 noundef allocalign %alignment) unnamed_addr allocsize(2) allockind(\"realloc,aligned\") nounwind", llvm);
        Assert.DoesNotContain("define internal dso_local noalias nonnull noundef ptr @__stark_runtime_realloc", llvm);
        Assert.Contains("define internal dso_local void @__stark_runtime_free(ptr %ptr) unnamed_addr nounwind", llvm);
        Assert.Contains("define internal dso_local ptr @__stark_os_allocate(i64 noundef %size) unnamed_addr nounwind", llvm);
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

            struct Box {
                i32[-2147483648 2147483647] Value;
            }

            fn i32[-2147483648 2147483647] Run() {
                heap Box box = new Box() { Value = 7 };
                return box.Value;
            }
            """,
            options: new CompilerOptions(TargetInfo: new LlvmTargetInfo("x86_64-pc-windows-msvc", null)));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);

        Assert.Contains("declare ptr @GetProcessHeap() nounwind", llvm);
        Assert.Contains("declare ptr @HeapAlloc(ptr, i32, i64) nounwind", llvm);
        Assert.Contains("declare i32 @HeapFree(ptr, i32, ptr) nounwind", llvm);
        Assert.Contains("call ptr @HeapAlloc(ptr %heap, i32 0, i64 %size)", llvm);
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

            struct Box {
                i32[-2147483648 2147483647] Value;
            }

            fn i32[-2147483648 2147483647] Run() {
                heap Box box = new Box() { Value = 7 };
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

            public struct Allocator {
                u8[0 127] Kind;

                static finite law Allocator Default() {
                    return new Allocator() {
                        Kind = 0
                    };
                }

                finite law bool IsDefault(borrow Allocator self) {
                    return self.Kind == 0;
                }
            }

            internal struct Allocation {
                rawmutptr<i8[-128 127]> Pointer;
                i64[0 max] ByteLength;
                i64[1 max] Alignment;
                Allocator Allocator;
            }

            internal fn Allocation Allocate(Allocator allocator, i64[0 max] byteLength, i64[1 max] alignment);
            internal fn Allocation Reallocate(Allocation allocation, i64[0 max] byteLength, i64[1 max] alignment);
            internal fn void Free(Allocation allocation);

            export ffi fn i32[-2147483648 2147483647] main() {
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

            fn i32[-2147483648 2147483647] Run(i32[-2147483648 2147483647] input) {
                stack mut i32[-2147483648 2147483647] value = input;
                stack rawmutptr<i32[-2147483648 2147483647]> ptr = &value;
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
    public void OwnedScalarArrayStoragePreservesVectorizationFriendlyAlignment()
    {
        var result = Compile(
            """
            module Demo

            fn i32[-2147483648 2147483647] ReadStack(
                i32[-2147483648 2147483647][4] input,
                i32[0 3] index) {
                stack mut i32[-2147483648 2147483647][4] stackValues = input;
                return stackValues[index];
            }

            fn i32[-2147483648 2147483647] ReadHeap(
                i32[-2147483648 2147483647][4] input,
                i32[0 3] index) {
                heap i32[-2147483648 2147483647][4] heapValues = input;
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

            struct Big {
                i64[-9223372036854775808 9223372036854775807] A;
                i64[-9223372036854775808 9223372036854775807] B;
                i64[-9223372036854775808 9223372036854775807] C;
            }

            fn Big Make() {
                return new Big() { A = 1, B = 2, C = 3 };
            }

            fn i64[-9223372036854775808 9223372036854775807] Run(bool flag) {
                if (flag) {
                    stack mut i32[-2147483648 2147483647] value = 41;
                    stack rawmutptr<i32[-2147483648 2147483647]> ptr = &value;
                    stack Big made = Make();
                    return (i64[-9223372036854775808 9223372036854775807])*ptr + made.A;
                }

                return 0;
            }
            """,
            options: new CompilerOptions(OptimizationLevel: CompilerOptimizationLevel.O0));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvmRaw(result);
        var runIndex = llvm.IndexOf("define fastcc noundef i64 @Run", StringComparison.Ordinal);
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

            record Pair(i32[-2147483648 2147483647] Left, i32[-2147483648 2147483647] Right) { }

            fn bool Equal(Pair left, Pair right) {
                return left == right;
            }

            fn bool NotEqual(Pair left, Pair right) {
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

            fn bool Equal(i32[-2147483648 2147483647][2] left, i32[-2147483648 2147483647][2] right) {
                return left == right;
            }

            fn bool NotEqual(i32[-2147483648 2147483647][2] left, i32[-2147483648 2147483647][2] right) {
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

            enum Token {
                None,
                Number(i32[-2147483648 2147483647]),
            }

            fn bool Equal(Token left, Token right) {
                return left == right;
            }

            fn bool NotEqual(Token left, Token right) {
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

            record Many(i32[-2147483648 2147483647] A, i32[-2147483648 2147483647] B, i32[-2147483648 2147483647] C, i32[-2147483648 2147483647] D, i32[-2147483648 2147483647] E) { }

            fn bool Equal(Many left, Many right) {
                return left == right;
            }

            fn bool NotEqual(Many left, Many right) {
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

            record Many(i32[-2147483648 2147483647] A, i32[-2147483648 2147483647] B, i32[-2147483648 2147483647] C, i32[-2147483648 2147483647] D, i32[-2147483648 2147483647] E) { }

            fn bool Less(Many left, Many right) {
                return left < right;
            }

            fn bool LessOrEqual(Many left, Many right) {
                return left <= right;
            }

            fn bool Greater(Many left, Many right) {
                return left > right;
            }

            fn bool GreaterOrEqual(Many left, Many right) {
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

            enum Token {
                None,
                Many(i32[-2147483648 2147483647], i32[-2147483648 2147483647], i32[-2147483648 2147483647], i32[-2147483648 2147483647], i32[-2147483648 2147483647]),
            }

            fn bool Less(Token left, Token right) {
                return left < right;
            }

            fn bool LessOrEqual(Token left, Token right) {
                return left <= right;
            }

            fn bool Greater(Token left, Token right) {
                return left > right;
            }

            fn bool GreaterOrEqual(Token left, Token right) {
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

            fn bool Equal(i32[-2147483648 2147483647][5] left, i32[-2147483648 2147483647][5] right) {
                return left == right;
            }

            fn bool NotEqual(i32[-2147483648 2147483647][5] left, i32[-2147483648 2147483647][5] right) {
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

            enum Token {
                None,
                Many(i32[-2147483648 2147483647], i32[-2147483648 2147483647], i32[-2147483648 2147483647], i32[-2147483648 2147483647], i32[-2147483648 2147483647]),
            }

            fn bool Equal(Token left, Token right) {
                return left == right;
            }

            fn bool NotEqual(Token left, Token right) {
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

            fn bool SameAscii(ascii left, ascii right) {
                return left == right;
            }

            fn bool DifferentUnicode(unicode left, unicode right) {
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

            record Label(ascii Tag, unicode Word) { }

            fn bool Same(Label left, Label right) {
                return left == right;
            }

            fn bool Different(Label left, Label right) {
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

            fn bool Same(i32[-2147483648 2147483647][] left, i32[-2147483648 2147483647][] right) {
                return left == right;
            }

            fn bool Different(i32[-2147483648 2147483647][] left, i32[-2147483648 2147483647][] right) {
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

            record Window(i32[-2147483648 2147483647][] Items, i32[-2147483648 2147483647] Count) { }

            fn bool Same(Window left, Window right) {
                return left == right;
            }

            fn bool Different(Window left, Window right) {
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

            struct Cell {
                i32[-2147483648 2147483647] Value;
            }

            struct Holder {
                Cell[2] Cells;
            }

            fn Holder Make() {
                return new Holder() {
                    Cells = { new Cell() { Value = 3 }, new Cell() { Value = 5 } }
                };
            }

            fn i32[-2147483648 2147483647] Run() {
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

            record Point(i32[-2147483648 2147483647] X, i32[-2147483648 2147483647] Y) { }

            fn i32[-2147483648 2147483647] Run() {
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

            struct Box {
                i32[-2147483648 2147483647] Value;
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

            record Point(i32[-2147483648 2147483647] X) {
                i32[-2147483648 2147483647] Y;
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
                i32[-2147483648 2147483647] Value;
            }

            fn i32[-2147483648 2147483647] Read(Box box) {
                return box.Value;
            }

            fn i32[-2147483648 2147483647] Run() {
                return Read(new Box() { Value = 7 });
            }
            """);

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

            struct Pair {
                i8[-128 127] Tag;
                i32[-2147483648 2147483647] Value;
            }

            fn void Touch(borrow Pair pair) {
                return;
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("define fastcc void @Touch(ptr nonnull noalias readonly nocapture dereferenceable(8) align 4 %arg_pair)", llvm);
    }

    [Fact]
    public void TypeAliasesPreserveTheUnderlyingAggregateAbi()
    {
        var result = Compile(
            """
            module Demo

            struct Pair {
                i64[-9223372036854775808 9223372036854775807] Left;
                i64[-9223372036854775808 9223372036854775807] Right;
            }

            alias PairAlias = Pair;

            fn PairAlias Step(PairAlias value) {
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

            struct Pair {
                i32[-2147483648 2147483647] Left;
                i32[-2147483648 2147483647] Right;
            }

            fn i32[-2147483648 2147483647] Run() {
                stack mut Pair source = new Pair() { Left = 1, Right = 2 };
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

            struct Pair {
                i8[-128 127] Tag;
                i32[-2147483648 2147483647] Value;
            }

            fn i32[-2147483648 2147483647] Run() {
                stack Pair source = new Pair() { Tag = 1, Value = 2 };
                stack mut Pair dest = new Pair() { Tag = 0, Value = 0 };
                stack rawptr<Pair> sourcePtr = &source;
                stack rawptr<Pair> destPtr = &dest;
                dest = source;
                return dest.Value;
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("%Pair = type { i8, i32 }", llvm);
        Assert.Contains("load %Pair, ptr %v", llvm);
        Assert.Contains("store %Pair %abi_copy_load_", llvm);
        Assert.DoesNotContain("@llvm.memcpy.p0.p0.i64", llvm);
    }

    [Fact]
    public void LargeAddressableAggregateCopyUsesInlineMemcpy()
    {
        var result = Compile(
            """
            module Demo

            struct Large {
                i32[-2147483648 2147483647] A0;
                i32[-2147483648 2147483647] A1;
                i32[-2147483648 2147483647] A2;
                i32[-2147483648 2147483647] A3;
                i32[-2147483648 2147483647] A4;
                i32[-2147483648 2147483647] A5;
                i32[-2147483648 2147483647] A6;
                i32[-2147483648 2147483647] A7;
                i32[-2147483648 2147483647] A8;
            }

            fn i32[-2147483648 2147483647] Run() {
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

        Assert.Contains("declare void @llvm.memcpy.inline.p0.p0.i64", llvm);
        Assert.Contains("call void @llvm.memcpy.inline.p0.p0.i64(ptr align 4 %v", llvm);
        Assert.Contains("i64 36, i1 false)", llvm);
    }

    [Fact]
    public void LargeZeroInitializedAggregateStoresUseInlineMemset()
    {
        var result = Compile(
            """
            module Demo

            struct Buffer {
                i32[-2147483648 2147483647][16] Data;
            }

            ffi fn void Consume(rawptr<Buffer> buffer);

            fn void Run() {
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

            struct Pair {
                i32[-2147483648 2147483647] Left;
                i32[-2147483648 2147483647] Right;
            }

            fn i32[-2147483648 2147483647] Run() {
                stack mut Pair source = new Pair() { Left = 1, Right = 2 };
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

        Assert.Equal(2, llvm.Split('\n').Count(static line => line.Contains("store i32 undef, ptr %abi_store_dest_", StringComparison.Ordinal)));
    }

    [Fact]
    public void AddressableAggregateConditionalUsesSingleAggregateAlloca()
    {
        var result = Compile(
            """
            module Demo

            struct Pair {
                i32[-2147483648 2147483647] Left;
                i32[-2147483648 2147483647] Right;
            }

            fn i32[-2147483648 2147483647] Run(bool flag) {
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
                i32[-2147483648 2147483647] Value;
            }

            fn Box Make() {
                return new Box() { Value = 7 };
            }

            fn i32[-2147483648 2147483647] Run() {
                stack Box box = Make();
                return box.Value;
            }
            """);

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

            struct Big {
                i64[-9223372036854775808 9223372036854775807] A;
                i64[-9223372036854775808 9223372036854775807] B;
                i64[-9223372036854775808 9223372036854775807] C;
            }

            fn Big Make() {
                return new Big() { A = 1, B = 2, C = 3 };
            }

            fn i64[-9223372036854775808 9223372036854775807] Run() {
                stack Big value = Make();
                return (i64[-9223372036854775808 9223372036854775807])value.C;
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("define fastcc void @Make(ptr noalias sret(%Big) nonnull dereferenceable(24) align 8 %ret)", llvm);
        Assert.Contains("call fastcc void @Make(ptr sret(%Big)", llvm);
        Assert.DoesNotContain("define fastcc %Big @Make()", llvm);
        Assert.DoesNotContain("call fastcc %Big @Make()", llvm);
    }

    [Fact]
    public void LargeAggregateInitializerReturnMaterializesDirectlyIntoSRetBuffer()
    {
        var result = Compile(
            """
            module Demo

            struct Big {
                i64[-9223372036854775808 9223372036854775807] A;
                i64[-9223372036854775808 9223372036854775807] B;
                i64[-9223372036854775808 9223372036854775807] C;
            }

            fn Big Make() {
                return new Big() { A = 1, B = 2, C = 3 };
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
    public void LargeAggregateForwardReturnCopiesFromIndirectCallResultSlot()
    {
        var result = Compile(
            """
            module Demo

            struct Big {
                i64[-9223372036854775808 9223372036854775807] A;
                i64[-9223372036854775808 9223372036854775807] B;
                i64[-9223372036854775808 9223372036854775807] C;
                i64[-9223372036854775808 9223372036854775807] D;
                i64[-9223372036854775808 9223372036854775807] E;
            }

            fn Big Make() {
                return new Big() { A = 1, B = 2, C = 3, D = 4, E = 5 };
            }

            fn Big Forward() {
                return Make();
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("define fastcc void @Forward(ptr noalias sret(%Big) nonnull dereferenceable(40) align 8 %ret)", llvm);
        Assert.Contains("call fastcc void @Make(ptr sret(%Big)", llvm);
        Assert.True(
            Regex.IsMatch(
                llvm,
                @"call void @llvm\.memcpy\.inline\.p0\.p0\.i64\(ptr(?: align \d+)? %ret, ptr(?: align \d+)? %abi_callret_slot_",
                RegexOptions.CultureInvariant),
            "Expected Forward to copy the indirect call result slot into the sret buffer.");
        Assert.DoesNotContain("load %Big, ptr %abi_callret_slot_", llvm);
        Assert.DoesNotContain("store %Big", llvm);
    }

    [Fact]
    public void LargeAggregateReturnOfIndirectParameterSkipsEntryLoad()
    {
        var result = Compile(
            """
            module Demo

            struct Big {
                i64[-9223372036854775808 9223372036854775807] A;
                i64[-9223372036854775808 9223372036854775807] B;
                i64[-9223372036854775808 9223372036854775807] C;
                i64[-9223372036854775808 9223372036854775807] D;
            }

            fn Big Forward(Big value) {
                return value;
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("define fastcc void @Forward(ptr noalias sret(%Big) nonnull dereferenceable(32) align 8 %ret, ptr nonnull byval(%Big) noalias readonly nocapture dereferenceable(32) align 8 %arg_value)", llvm);
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

            struct Big {
                i64[-9223372036854775808 9223372036854775807] A;
                i64[-9223372036854775808 9223372036854775807] B;
                i64[-9223372036854775808 9223372036854775807] C;
                i64[-9223372036854775808 9223372036854775807] D;
            }

            fn Big Step(Big value) {
                return value;
            }

            fn Big Forward(Big value) {
                return Step(value);
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("define fastcc void @Forward(ptr noalias sret(%Big) nonnull dereferenceable(32) align 8 %ret, ptr nonnull byval(%Big) noalias readonly nocapture dereferenceable(32) align 8 %arg_value)", llvm);
        Assert.Contains("call fastcc void @Step(ptr sret(%Big)", llvm);
        Assert.Contains("ptr byval(%Big) align 8 %arg_value", llvm);
        Assert.True(
            Regex.IsMatch(
                llvm,
                @"call void @llvm\.memcpy\.inline\.p0\.p0\.i64\(ptr(?: align \d+)? %ret, ptr(?: align \d+)? %abi_callret_slot_",
                RegexOptions.CultureInvariant),
            "Expected Forward to copy from the indirect callee result slot into the sret buffer.");
        Assert.DoesNotContain("load %Big, ptr %abi_callret_slot_", llvm);
        Assert.DoesNotContain("load %Big, ptr %arg_value", llvm);
        Assert.DoesNotContain("%abi_callarg_value", llvm);
    }

    [Fact]
    public void LargeAggregateParametersUseByValueIndirectAbi()
    {
        var result = Compile(
            """
            module Demo

            struct Big {
                i64[-9223372036854775808 9223372036854775807] A;
                i64[-9223372036854775808 9223372036854775807] B;
                i64[-9223372036854775808 9223372036854775807] C;
            }

            fn i64[-9223372036854775808 9223372036854775807] Read(Big value) {
                return value.A + value.C;
            }

            fn i64[-9223372036854775808 9223372036854775807] Run() {
                return Read(new Big() { A = 1, B = 2, C = 3 });
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("define fastcc i64 @Read(", llvm);
        Assert.Contains("ptr nonnull byval(%Big) noalias readonly nocapture dereferenceable(24) align 8 %arg_value", llvm);
        Assert.Contains("memory(argmem: read)", llvm);
        Assert.Contains("call fastcc i64 @Read(ptr byval(%Big) align 8", llvm);
        Assert.DoesNotContain("define fastcc i64 @Read(%Big", llvm);
    }

    [Fact]
    public void AggregateCallReturnRegressionBenchmarkKeepsSmallAggregatesOnDirectAbi()
    {
        var result = Compile(
            """
            module Demo

            struct Pair {
                i32[-2147483648 2147483647] Left;
                i32[-2147483648 2147483647] Right;
            }

            fn Pair Step(Pair value, i32[-2147483648 2147483647] delta) {
                return new Pair() {
                    Left = value.Left + delta,
                    Right = value.Right + delta
                };
            }

            fn i32[-2147483648 2147483647] Run() {
                stack Pair current = Step(
                    Step(
                        Step(new Pair() { Left = 1, Right = 2 }, 1),
                        1),
                    1);
                return current.Left + current.Right;
            }
            """);

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

            struct Big {
                i64[-9223372036854775808 9223372036854775807] A;
                i64[-9223372036854775808 9223372036854775807] B;
                i64[-9223372036854775808 9223372036854775807] C;
                i64[-9223372036854775808 9223372036854775807] D;
            }

            fn Big Step(Big value, i64[-9223372036854775808 9223372036854775807] delta) {
                return new Big() {
                    A = value.A + delta,
                    B = value.B + delta,
                    C = value.C + delta,
                    D = value.D + delta
                };
            }

            fn i64[-9223372036854775808 9223372036854775807] Run() {
                stack Big current = Step(
                    Step(
                        new Big() { A = 1, B = 2, C = 3, D = 4 },
                        1),
                    1);
                return current.A + current.D;
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("define fastcc void @Step(ptr noalias sret(%Big) nonnull dereferenceable(32) align 8 %ret, ptr nonnull byval(%Big) noalias readonly nocapture dereferenceable(32) align 8 %arg_value, i64 %arg_delta)", llvm);
        Assert.Contains("call fastcc void @Step(ptr sret(%Big)", llvm);
        Assert.Contains("ptr byval(%Big) align 8", llvm);
        Assert.DoesNotContain("define fastcc %Big @Step(", llvm);
    }

    [Fact]
    public void AggregateBranchJoinEmitsByValuePhiNode()
    {
        var result = Compile(
            """
            module Demo

            struct Box {
                i32[-2147483648 2147483647] Value;
            }

            fn i32[-2147483648 2147483647] Run(bool flag) {
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
                i32[-2147483648 2147483647] Value;

                fn i32[-2147483648 2147483647] Read(Box box) {
                    return box.Value;
                }
            }

            fn i32[-2147483648 2147483647] Run() {
                stack Box box = new Box() { Value = 7 };
                return box.Read();
            }
            """);

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

            struct Box {
                i32[-2147483648 2147483647] Value;

                fn i32[-2147483648 2147483647] Read(borrow Box box) {
                    return box.Value;
                }
            }

            fn i32[-2147483648 2147483647] Run(borrow Box box) {
                return box.Read();
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("define fastcc i32 @Box_Read(ptr nonnull noalias readonly nocapture dereferenceable(4) align 4 %arg_box)", llvm);
        Assert.Contains("define fastcc i32 @Run(ptr nonnull noalias readonly nocapture dereferenceable(4) align 4 %arg_box)", llvm);
        Assert.Contains("call fastcc i32 @Box_Read(ptr %", llvm);
        Assert.DoesNotContain("tail call fastcc i32 @Box_Read", llvm);
    }

    [Fact]
    public void IndexedFieldAddressBehindRawPointerEmitsDirectParameterGeps()
    {
        var result = Compile(
            """
            module Demo

            struct Buffer {
                i8[-128 127][16] Storage;
                i64[-9223372036854775808 9223372036854775807] WritePos;
            }

            fn i32[-2147483648 2147483647] Touch(rawmutptr<Buffer> buffer, i64[-9223372036854775808 9223372036854775807] index, i8[-128 127] value) {
                *(&(*buffer).Storage[index]) = value;
                return (i32[-2147483648 2147483647])*(&(*buffer).Storage[index]);
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("define fastcc i32 @Touch(ptr nocapture %arg_buffer, i64 %arg_index, i8 %arg_value)", llvm);
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

            fn i32[-2147483648 2147483647] Touch(rawmutptr<i8[-128 127]> data, i64[-9223372036854775808 9223372036854775807] index, i8[-128 127] value) {
                *(&data[index]) = value;
                return (i32[-2147483648 2147483647])*(&data[index]);
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("define fastcc i32 @Touch(ptr nocapture %arg_data, i64 %arg_index, i8 %arg_value)", llvm);
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

            struct Buffer {
                i8[-128 127][16] Storage;

                fn void Put(borrow mut Buffer self, i64[-9223372036854775808 9223372036854775807] index, i8[-128 127] value) {
                    *(&self.Storage[index]) = value;
                    return;
                }

                fn i32[-2147483648 2147483647] Read(borrow Buffer self, i64[-9223372036854775808 9223372036854775807] index) {
                    return (i32[-2147483648 2147483647])*(&self.Storage[index]);
                }
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("define fastcc void @Buffer_Put(ptr nonnull noalias nocapture dereferenceable(16) %arg_self, i64 %arg_index, i8 %arg_value)", llvm);
        Assert.Contains("define fastcc i32 @Buffer_Read(ptr nonnull noalias readonly nocapture dereferenceable(16) %arg_self, i64 %arg_index)", llvm);
        Assert.Contains("getelementptr inbounds nuw %Buffer, ptr %arg_self, i32 0", llvm);
        Assert.Contains("getelementptr inbounds nuw %Buffer, ptr %v0, i32 0, i32 0", llvm);
        Assert.DoesNotContain("alloca %Buffer", llvm);
    }

    [Fact]
    public void MutableBorrowedAggregateWriterEmitsWriteOnlyNoCaptureParameterFacts()
    {
        var result = Compile(
            """
            module Demo

            struct Box {
                i32[-2147483648 2147483647] Value;
            }

            fn void Touch(borrow mut Box box) {
                box.Value = 7;
                return;
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("define fastcc void @Touch(ptr nonnull noalias writeonly nocapture dereferenceable(4) align 4 %arg_box)", llvm);
        Assert.DoesNotContain("define fastcc void @Touch(ptr nonnull noalias readonly", llvm);
    }

    [Fact]
    public void FixedArrayInitializerAndIndexCanFoldToScalarReturn()
    {
        var result = Compile(
            """
            module Demo

            fn i32[-2147483648 2147483647] Run() {
                stack i32[-2147483648 2147483647][3] values = { 1, 2, 3 };
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

            fn i32[-2147483648 2147483647] Run() {
                stack mut i32[-2147483648 2147483647][3] values = { 1, 2, 3 };
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

            fn i32[-2147483648 2147483647] Run(i32[-2147483648 2147483647] index) {
                stack i32[-2147483648 2147483647][3] values = { 1, 2, 3 };
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
        Assert.Contains("getelementptr inbounds nuw [3 x i32], ptr %slot_values, i32 0", llvm);
        Assert.Matches(@"getelementptr \[3 x i32\], ptr %v\d+, i32 0, i32 %arg_index", llvm);
        Assert.DoesNotContain("getelementptr inbounds [3 x i32], ptr %v", llvm);
        Assert.DoesNotContain("getelementptr inbounds nuw [3 x i32], ptr %v", llvm);
        Assert.Contains("load i32, ptr", llvm);
        Assert.DoesNotContain("declare fastcc i32 @Run(i32)", llvm);
    }

    [Fact]
    public void SliceParameterUsesConcreteSliceAbiAndDynamicIndexLoad()
    {
        var result = Compile(
            """
            module Demo

            fn i32[-2147483648 2147483647] Read(i32[-2147483648 2147483647][] view, i32[-2147483648 2147483647] index) {
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

            fn i32[-2147483648 2147483647] ReadView(i32[-2147483648 2147483647][3] values, i32[0 2] index) {
                stack i32[-2147483648 2147483647][3] local = values;
                stack i32[-2147483648 2147483647][] view = local;
                return view[index];
            }

            fn ascii SliceLiteral(i32[0 4] start, i32[0 4] length) {
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
    public void FixedArrayParameterUsesDirectValueAbi()
    {
        var result = Compile(
            """
            module Demo

            fn i32[-2147483648 2147483647] Read(i32[-2147483648 2147483647][2] values) {
                return values[0] + values[1];
            }

            fn i32[-2147483648 2147483647] Run() {
                stack i32[-2147483648 2147483647][2] values = { 4, 7 };
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

            fn i32[-2147483648 2147483647] Read(i32[-2147483648 2147483647][3] values, i32[-2147483648 2147483647] index) {
                return values[index];
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("define fastcc i32 @Read([3 x i32] %arg_values, i32 %arg_index)", llvm);
        Assert.Contains("%slot_param_values = alloca [3 x i32]", llvm);
        Assert.Contains("store [3 x i32] %arg_values, ptr %slot_param_values", llvm);
        Assert.Contains("getelementptr inbounds nuw [3 x i32], ptr %slot_param_values, i32 0", llvm);
        Assert.Matches(@"getelementptr \[3 x i32\], ptr %v\d+, i32 0, i32 %arg_index", llvm);
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

            fn i32[-2147483648 2147483647] Bounded(i32[0 2] index) {
                stack i32[-2147483648 2147483647][3] values = { 1, 2, 3 };
                return values[index];
            }

            fn i32[-2147483648 2147483647] Unbounded(i32[-2147483648 2147483647] index) {
                stack i32[-2147483648 2147483647][3] values = { 1, 2, 3 };
                return values[index];
            }
            """);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvm(result);

        Assert.Matches(@"getelementptr inbounds nuw \[3 x i32\], ptr %v\d+, i32 0, i32 %arg_index", llvm);
        Assert.Matches(@"getelementptr \[3 x i32\], ptr %v\d+, i32 0, i32 %arg_index", llvm);
        Assert.Single(Regex.Matches(llvm, @"getelementptr inbounds nuw \[3 x i32\], ptr %v\d+, i32 0, i32 %arg_index").Cast<Match>());
        Assert.DoesNotContain("getelementptr inbounds [3 x i32], ptr %v", llvm);
    }

    [Fact]
    public void FixedArrayReturnUsesDirectValueAbi()
    {
        var result = Compile(
            """
            module Demo

            fn i32[-2147483648 2147483647][2] Make() {
                stack i32[-2147483648 2147483647][2] values = { 4, 7 };
                return values;
            }

            fn i32[-2147483648 2147483647] Run() {
                stack i32[-2147483648 2147483647][2] values = Make();
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

            fn i32[-2147483648 2147483647] Run(i32[-2147483648 2147483647] index) {
                stack mut i32[-2147483648 2147483647][3] values = { 1, 2, 3 };
                values[index] = 9;
                return values[index];
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("define fastcc i32 @Run(i32 %arg_index)", llvm);
        Assert.Contains("getelementptr inbounds nuw [3 x i32], ptr %slot_values, i32 0", llvm);
        Assert.Matches(@"getelementptr \[3 x i32\], ptr %v\d+, i32 0, i32 %arg_index", llvm);
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

            fn i32[-2147483648 2147483647] Run(mut i32[-2147483648 2147483647][] view, i32[-2147483648 2147483647] index) {
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

            ffi fn void Touch();

            fn i32[-2147483648 2147483647] Run(i32[-2147483648 2147483647] left, i32[-2147483648 2147483647] right) {
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

                        public finite law i32[-2147483648 2147483647] Add(i32[-2147483648 2147483647] left, i32[-2147483648 2147483647] right) {
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

            public ffi asm(x86_64) fn i64[-9223372036854775808 9223372036854775807] Syscall2(i64[-9223372036854775808 9223372036854775807] number, rawptr<i8[-128 127]> path)
                in("rax") number,
                in("rdi") path,
                out("rax") return,
                clobber("rcx", "r11")
            {
                "syscall"
            }

            fn i64[-9223372036854775808 9223372036854775807] Run(rawptr<i8[-128 127]> path) {
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

            fn i64[-9223372036854775808 9223372036854775807] Run(rawptr<i8[-128 127]> path) {
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

                        public ffi asm(x86_64) fn i64[-9223372036854775808 9223372036854775807] Syscall2(i64[-9223372036854775808 9223372036854775807] number, rawptr<i8[-128 127]> path)
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
        Assert.Contains("call i64 @Syscall2(i64 2, ptr %arg_path)", llvm);
        Assert.DoesNotContain("; imported asm definition: Syscall.Syscall2", llvm);
        Assert.DoesNotContain("define i64 @Syscall2", llvm);
    }

    [Fact]
    public void RootAsmFunctionsEmitFloatingPointRegisterBindings()
    {
        var result = Compile(
            """
            module Demo

            public ffi asm(x86_64) fn f64 Identity(f64 value)
                in("xmm0") value,
                out("xmm0") return
            {
                "nop"
            }

            fn f64 Run(f64 value) {
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
    public void ImportedGlobalsUseQualifiedDependencySymbols()
    {
        var result = Compile(
            """
            import Math
            module Demo

            fn i32[-2147483648 2147483647] Run() {
                Math.Counter = 7;
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
                        public static i32[-2147483648 2147483647] Hidden = 2;
                        public static mut i32[-2147483648 2147483647] Counter = 1;
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
        Assert.Contains("store i32 7, ptr @Math_Counter", llvm);
        Assert.Contains("load i32, ptr @Math_Counter", llvm);
        Assert.Contains("load i8, ptr @Math_Answer", llvm);
    }

    [Fact]
    public void ImportedReadonlyScalarArrayDeclarationsCarryVectorizationFriendlyAlignment()
    {
        var result = Compile(
            """
            import Tables
            module Demo

            fn i32[-2147483648 2147483647] Read(i32[0 3] index) {
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

                        public static i32[-2147483648 2147483647][4] Lookup = { 1, 2, 3, 4 };
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

                public static i32[-2147483648 2147483647][3] Values = { 1, 2, 3 };
                public static mut i32[-2147483648 2147483647] Counter = 1;
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

                    fn i32[-2147483648 2147483647] Read(i32[0 2] index) {
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

            struct Box {
                i32[-2147483648 2147483647] Value;
            }

            static i32[-2147483648 2147483647] Counter = 0;
            static Box Current = new Box() { Value = 5 };

            fn rawptr<i32[-2147483648 2147483647]> CounterPtr() {
                return &Counter;
            }

            fn rawptr<i32[-2147483648 2147483647]> FieldPtr() {
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

            export ffi fn i32[-2147483648 2147483647] main() {
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
                            i32[-2147483648 2147483647] Value;
                        }

                        public fn Box Make() {
                            return new Box() { Value = 7 };
                        }

                        public fn i32[-2147483648 2147483647] Read(Box box) {
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

            law i32[-2147483648 2147483647] Use() {
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

                        law i32[-2147483648 2147483647] LawOnly() {
                            return 1;
                        }

                        public law i32[-2147483648 2147483647] UseLaw() {
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

            law i32[-2147483648 2147483647] UseLawClone() {
                return Math.UseLaw();
            }

            fn i32[-2147483648 2147483647] UseDirect() {
                Touch();
                return Math.UseLaw();
            }

            ffi fn void Touch();
            """,
            new CompilerOptions(
                ModuleResolver: new InMemoryModuleResolver(
                [
                    (
                        new ResolvedModuleReference("Math", "/virtual/Math.stark", IsExternal: false),
                        """
                        module Math

                        public law i32[-2147483648 2147483647] UseLaw() {
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

            law i32[-2147483648 2147483647] UseLawClone(i32[-2147483648 2147483647] left, i32[-2147483648 2147483647] right) {
                return Math.Numbers.Add(left, right);
            }

            fn i32[-2147483648 2147483647] UseDirect(i32[-2147483648 2147483647] left, i32[-2147483648 2147483647] right) {
                Touch();
                return Math.Numbers.Add(left, right);
            }

            ffi fn void Touch();
            """,
            new CompilerOptions(
                ModuleResolver: new InMemoryModuleResolver(
                [
                    (
                        new ResolvedModuleReference("Math", "/virtual/Math.stark", IsExternal: false),
                        """
                        module Math

                        public doctrine Numbers {
                            finite law i32[-2147483648 2147483647] Add(i32[-2147483648 2147483647] left, i32[-2147483648 2147483647] right) {
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

            law i32[-2147483648 2147483647] UseLawClone(i32[-2147483648 2147483647] left, i32[-2147483648 2147483647] right) {
                return Math.Add(left, right);
            }

            fn i32[-2147483648 2147483647] UseDirect(i32[-2147483648 2147483647] left, i32[-2147483648 2147483647] right) {
                Touch();
                return Math.Add(left, right);
            }

            ffi fn void Touch();
            """,
            new CompilerOptions(
                ModuleResolver: new InMemoryModuleResolver(
                [
                    (
                        new ResolvedModuleReference("Math", "/virtual/Math.stark", IsExternal: false),
                        """
                        module Math

                        public finite law i32[-2147483648 2147483647] Add(i32[-2147483648 2147483647] left, i32[-2147483648 2147483647] right) {
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

            fn i32[-2147483648 2147483647] Use(i32[-2147483648 2147483647] value) {
                Touch();
                return Math.UseLaw(value);
            }

            ffi fn void Touch();
            """,
            new CompilerOptions(
                ModuleResolver: new InMemoryModuleResolver(
                [
                    (
                        new ResolvedModuleReference("Math", "/virtual/Math.stark", IsExternal: false),
                        """
                        module Math

                        law i32[-2147483648 2147483647] LawOnly(i32[-2147483648 2147483647] value) {
                            return value + 1;
                        }

                        public law i32[-2147483648 2147483647] UseLaw(i32[-2147483648 2147483647] value) {
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

            law i32[-2147483648 2147483647] Use(i32[-2147483648 2147483647] value) {
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

                        public inlinehint law i32[-2147483648 2147483647] UseLaw(i32[-2147483648 2147483647] value) {
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

            fn i32[-2147483648 2147483647] Add(i32[-2147483648 2147483647] left, i32[-2147483648 2147483647] right) {
                return left + right;
            }

            law i32[-2147483648 2147483647] Use() {
                return Add(1, 2);
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("define fastcc i32 @Add(i32 %arg_left, i32 %arg_right) nounwind willreturn mustprogress nosync nofree memory(none) alwaysinline", llvm);
        Assert.Contains("define fastcc i32 @Use() nounwind willreturn mustprogress nosync nofree memory(none) alwaysinline", llvm);
        Assert.Contains("call fastcc i32 @Add(i32 1, i32 2)", llvm);
    }

    [Fact]
    public void ModulePrivateDirectCallForwardersEmitAlwaysInline()
    {
        var result = Compile(
            """
            module Demo

            fn i32[-2147483648 2147483647] Core(i32[-2147483648 2147483647] value) {
                return value + 1;
            }

            fn i32[-2147483648 2147483647] Forward(i32[-2147483648 2147483647] value) {
                return Core(value);
            }

            fn i32[-2147483648 2147483647] Use(i32[-2147483648 2147483647] value) {
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
        Assert.Contains("call fastcc i32 @Forward(i32 %arg_value)", llvm);
    }

    [Fact]
    public void ModulePrivateMemberCallForwardersEmitAlwaysInline()
    {
        var result = Compile(
            """
            module Demo

            record Box(i32[-2147483648 2147483647] Value) {
                fn i32[-2147483648 2147483647] Bump(borrow Box self, i32[-2147483648 2147483647] delta) {
                    return self.Value + delta;
                }
            }

            fn i32[-2147483648 2147483647] Forward(borrow Box box, i32[-2147483648 2147483647] delta) {
                return box.Bump(delta);
            }

            fn i32[-2147483648 2147483647] Use(borrow Box box, i32[-2147483648 2147483647] delta) {
                return Forward(box, delta);
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.True(
            System.Text.RegularExpressions.Regex.IsMatch(
                llvm,
                @"define[^\r\n]*@Forward\([^\r\n]*alwaysinline",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant),
            "Expected the module-private member-call forwarder to emit with alwaysinline.");
        Assert.Contains("call fastcc i32 @Forward(", llvm, StringComparison.Ordinal);
    }

    [Fact]
    public void ModulePrivateFieldAccessWrappersEmitAlwaysInline()
    {
        var result = Compile(
            """
            module Demo

            record Inner(i32[-2147483648 2147483647] Value) { }
            record Box(Inner Inner) { }

            fn i32[-2147483648 2147483647] Read(borrow Box box) {
                return box.Inner.Value;
            }

            fn i32[-2147483648 2147483647] Use(borrow Box box) {
                return Read(box);
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.True(
            System.Text.RegularExpressions.Regex.IsMatch(
                llvm,
                @"define[^\r\n]*@Read\([^\r\n]*alwaysinline",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant),
            "Expected the module-private field-access wrapper to emit with alwaysinline.");
        Assert.Contains("call fastcc i32 @Read(", llvm, StringComparison.Ordinal);
    }

    [Fact]
    public void ModulePrivateIndexAccessWrappersEmitAlwaysInline()
    {
        var result = Compile(
            """
            module Demo

            record Box(i32[-2147483648 2147483647] Value) { }

            fn i32[-2147483648 2147483647] Read(Box[2] boxes, i32[-2147483648 2147483647] index) {
                return boxes[index].Value;
            }

            fn i32[-2147483648 2147483647] Use(Box[2] boxes, i32[-2147483648 2147483647] index) {
                return Read(boxes, index);
            }
            """);

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

            record Inner(i32[-2147483648 2147483647] Value) { }
            record Box(Inner Inner) { }

            fn i64[-9223372036854775808 9223372036854775807] Read(borrow Box box) {
                return (i64[-9223372036854775808 9223372036854775807])box.Inner.Value;
            }

            fn i64[-9223372036854775808 9223372036854775807] Use(borrow Box box) {
                return Read(box);
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.True(
            System.Text.RegularExpressions.Regex.IsMatch(
                llvm,
                @"define[^\r\n]*@Read\([^\r\n]*alwaysinline",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant),
            "Expected the module-private conversion wrapper to emit with alwaysinline.");
        Assert.Contains("call fastcc i64 @Read(", llvm, StringComparison.Ordinal);
    }

    [Fact]
    public void ModulePrivateAddressOfWrappersEmitAlwaysInline()
    {
        var result = Compile(
            """
            module Demo

            record Buffer(i32[-2147483648 2147483647][2] Values) { }

            fn rawptr<i32[-2147483648 2147483647]> Pin(borrow Buffer buffer, i32[-2147483648 2147483647] index) {
                return &buffer.Values[index];
            }

            fn rawptr<i32[-2147483648 2147483647]> Use(borrow Buffer buffer, i32[-2147483648 2147483647] index) {
                return Pin(buffer, index);
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.True(
            System.Text.RegularExpressions.Regex.IsMatch(
                llvm,
                @"define[^\r\n]*@Pin\([^\r\n]*alwaysinline",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant),
            "Expected the module-private address-of wrapper to emit with alwaysinline.");
        Assert.Contains("call fastcc ptr @Pin(", llvm, StringComparison.Ordinal);
    }

    [Fact]
    public void ModulePrivateBinaryOperatorWrappersEmitAlwaysInline()
    {
        var result = Compile(
            """
            module Demo

            record Inner(i32[-2147483648 2147483647] Value) { }
            record Box(Inner Inner) { }

            fn i32[-2147483648 2147483647] AddDelta(borrow Box box, i32[-2147483648 2147483647] delta) {
                return box.Inner.Value + delta;
            }

            fn i32[-2147483648 2147483647] Use(borrow Box box, i32[-2147483648 2147483647] delta) {
                return AddDelta(box, delta);
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.True(
            System.Text.RegularExpressions.Regex.IsMatch(
                llvm,
                @"define[^\r\n]*@AddDelta\([^\r\n]*alwaysinline",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant),
            "Expected the module-private binary-operator wrapper to emit with alwaysinline.");
        Assert.Contains("call fastcc i32 @AddDelta(", llvm, StringComparison.Ordinal);
    }

    [Fact]
    public void ModulePrivateComparisonWrappersEmitAlwaysInline()
    {
        var result = Compile(
            """
            module Demo

            record Inner(i32[-2147483648 2147483647] Value) { }
            record Box(Inner Inner) { }

            fn bool IsBelow(borrow Box box, i32[-2147483648 2147483647] limit) {
                return box.Inner.Value < limit;
            }

            fn bool Use(borrow Box box, i32[-2147483648 2147483647] limit) {
                return IsBelow(box, limit);
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.True(
            System.Text.RegularExpressions.Regex.IsMatch(
                llvm,
                @"define[^\r\n]*@IsBelow\([^\r\n]*alwaysinline",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant),
            "Expected the module-private comparison wrapper to emit with alwaysinline.");
        Assert.Contains("call fastcc i1 @IsBelow(", llvm, StringComparison.Ordinal);
    }

    [Fact]
    public void ModulePrivateTerminalIfSelectionWrappersEmitAlwaysInline()
    {
        var result = Compile(
            """
            module Demo

            fn i32[-2147483648 2147483647] ChooseBranch(bool takeLeft, bool takeMiddle, i32[-2147483648 2147483647] left, i32[-2147483648 2147483647] middle, i32[-2147483648 2147483647] right) {
                if (takeLeft) {
                    return left;
                } else if (takeMiddle) {
                    return middle;
                } else {
                    return right;
                }
            }

            fn i32[-2147483648 2147483647] Use(bool takeLeft, bool takeMiddle, i32[-2147483648 2147483647] left, i32[-2147483648 2147483647] middle, i32[-2147483648 2147483647] right) {
                return ChooseBranch(takeLeft, takeMiddle, left, middle, right);
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.True(
            System.Text.RegularExpressions.Regex.IsMatch(
                llvm,
                @"define[^\r\n]*@ChooseBranch\([^\r\n]*alwaysinline",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant),
            "Expected the module-private terminal-if selection wrapper to emit with alwaysinline.");
        Assert.Contains("call fastcc i32 @ChooseBranch(", llvm, StringComparison.Ordinal);
    }

    [Fact]
    public void ModulePrivateTerminalSwitchSelectionWrappersEmitAlwaysInline()
    {
        var result = Compile(
            """
            module Demo

            fn i32[-2147483648 2147483647] ChooseSwitch(i32[-2147483648 2147483647] selector, i32[-2147483648 2147483647] left, i32[-2147483648 2147483647] middle, i32[-2147483648 2147483647] right) {
                switch (selector) {
                    case 0:
                        return left;
                    case 1:
                        return middle;
                    default:
                        return right;
                }
            }

            fn i32[-2147483648 2147483647] Use(i32[-2147483648 2147483647] selector, i32[-2147483648 2147483647] left, i32[-2147483648 2147483647] middle, i32[-2147483648 2147483647] right) {
                return ChooseSwitch(selector, left, middle, right);
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.True(
            System.Text.RegularExpressions.Regex.IsMatch(
                llvm,
                @"define[^\r\n]*@ChooseSwitch\([^\r\n]*alwaysinline",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant),
            "Expected the module-private terminal-switch selection wrapper to emit with alwaysinline.");
        Assert.Contains("call fastcc i32 @ChooseSwitch(", llvm, StringComparison.Ordinal);
    }

    [Fact]
    public void ModulePrivateObjectConstructionWrappersEmitAlwaysInline()
    {
        var result = Compile(
            """
            module Demo

            struct Inner {
                i32[-2147483648 2147483647] Value;
            }

            struct Outer {
                Inner Item;
                i32[-2147483648 2147483647] Count;
            }

            fn Outer Wrap(i32[-2147483648 2147483647] value, i32[-2147483648 2147483647] count) {
                return new Outer() {
                    Item = { Value = value },
                    Count = count
                };
            }

            fn Outer Use(i32[-2147483648 2147483647] value, i32[-2147483648 2147483647] count) {
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

            enum Boxed {
                None,
                Value { Data: i32[-2147483648 2147483647], Tag: i32[-2147483648 2147483647] },
            }

            fn Boxed Wrap(i32[-2147483648 2147483647] value, i32[-2147483648 2147483647] tag) {
                return Boxed.Value { Data: value, Tag: tag };
            }

            fn Boxed Use(i32[-2147483648 2147483647] value, i32[-2147483648 2147483647] tag) {
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

            record Inner(i32[-2147483648 2147483647] Value) { }
            record Box(Inner Inner) { }

            fn i32[-2147483648 2147483647] Bump(borrow Box box, i32[-2147483648 2147483647] delta) {
                stack mut i32[-2147483648 2147483647] current = box.Inner.Value;
                current += delta;
                return current;
            }

            fn i32[-2147483648 2147483647] Use(borrow Box box, i32[-2147483648 2147483647] delta) {
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

            hot fn i32[-2147483648 2147483647] Add(i32[-2147483648 2147483647] left, i32[-2147483648 2147483647] right) {
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

            public finite law i32[-2147483648 2147483647] Add(i32[-2147483648 2147483647] left, i32[-2147483648 2147483647] right) {
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

            fn i32[-2147483648 2147483647] Helper() {
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
    public void SourceBackedGenericCallsEmitConcreteMonomorphizedSymbols()
    {
        var result = Compile(
            """
            module Demo

            fn T Identity<T>(T value) {
                return value;
            }

            fn i32[-2147483648 2147483647] Run(i32[-2147483648 2147483647] value) {
                return Identity(value);
            }
            """);

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvm(result);

        Assert.Contains("define internal dso_local fastcc i32 @__stark_mono_fn_Demo__Identity__i32(", llvm);
        Assert.DoesNotContain("declare internal fastcc i32 @__stark_mono_fn_Demo__Identity__i32(", llvm);
        Assert.Contains("call fastcc i32 @__stark_mono_fn_Demo__Identity__i32(", llvm);
        Assert.DoesNotContain("call fastcc i32 @Identity(", llvm);
    }

    [Fact]
    public void NestedSourceBackedGenericCallsEmitTransitiveConcreteMonomorphizedSymbols()
    {
        var result = Compile(
            """
            module Demo

            fn T Identity<T>(T value) {
                return value;
            }

            fn T Forward<T>(T value) {
                return Identity(value);
            }

            fn i32[-2147483648 2147483647] Run(i32[-2147483648 2147483647] value) {
                return Forward(value);
            }
            """);

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvm(result);

        Assert.Contains("define internal dso_local fastcc i32 @__stark_mono_fn_Demo__Forward__i32(", llvm);
        Assert.Contains("define internal dso_local fastcc i32 @__stark_mono_fn_Demo__Identity__i32(", llvm);
        Assert.Contains("call fastcc i32 @__stark_mono_fn_Demo__Forward__i32(", llvm);
        Assert.Contains("call fastcc i32 @__stark_mono_fn_Demo__Identity__i32(", llvm);
        Assert.DoesNotContain("call fastcc i32 @Forward(", llvm);
        Assert.DoesNotContain("call fastcc i32 @Identity(", llvm);
    }

    [Fact]
    public void ImportedSourceBackedGenericSpecializationsUseLinkOnceOdrComdatDefinitions()
    {
        var result = Compile(
            """
            import Facade
            module Demo

            fn i32[-2147483648 2147483647] Run(i32[-2147483648 2147483647] value) {
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

                        public fn T Identity<T>(T value) {
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
        Assert.Contains("call fastcc i32 @__stark_mono_fn_Facade__Facade_Identity__i32(", llvm);
        Assert.DoesNotContain("define internal dso_local fastcc i32 @__stark_mono_fn_Facade__Facade_Identity__i32(", llvm);
    }

    [Fact]
    public void ImportedSourceBackedMutableBorrowGenericSpecializationsDoNotDenyArgumentMemoryAccess()
    {
        var result = Compile(
            """
            import Facade
            module Demo

            fn void Run() {
                stack mut Facade.Box<i32[-2147483648 2147483647]> box = new Facade.Box<i32[-2147483648 2147483647]>() { Value = 1 };
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

                        public struct Box<T> {
                            T Value;

                            public fn void Store(mut borrow Box<T> self, T value) {
                                TouchOther();
                                self.Value = value;
                                return;
                            }
                        }

                        public ffi fn void TouchOther();
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
    public void ManifestBackedGenericCallsEmitConcreteMonomorphizedSymbolsFromPackageImageTemplates()
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

                public fn T Identity<T>(T value) {
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

                    fn i32[-2147483648 2147483647] Run(i32[-2147483648 2147483647] value) {
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
            Assert.Contains("define internal dso_local fastcc i32 @__stark_law_clone___stark_mono_fn_Demo__Facade_Identity__i32(", llvm);
            Assert.Contains("call fastcc i32 @__stark_law_clone___stark_mono_fn_Demo__Facade_Identity__i32(", llvm);
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
    public void ManifestBackedNestedGenericCallsEmitTransitiveConcreteMonomorphizedSymbolsFromPackageImageTemplates()
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

                public fn T Identity<T>(T value) {
                    return value;
                }

                public fn T Forward<T>(T value) {
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

                    fn i32[-2147483648 2147483647] Run(i32[-2147483648 2147483647] value) {
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
            Assert.Contains("define internal dso_local fastcc i32 @__stark_law_clone___stark_mono_fn_Demo__Facade_Forward__i32(", llvm);
            Assert.Contains("define internal dso_local fastcc i32 @__stark_law_clone___stark_mono_fn_Demo__Facade_Identity__i32(", llvm);
            Assert.Contains("call fastcc i32 @__stark_law_clone___stark_mono_fn_Demo__Facade_Forward__i32(", llvm);
            Assert.Contains("call fastcc i32 @__stark_law_clone___stark_mono_fn_Demo__Facade_Identity__i32(", llvm);
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

                public fn T Identity<T>(T value) {
                    return value;
                }

                public fn T Forward<T>(T value) {
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

                    fn i32[-2147483648 2147483647] Run(i32[-2147483648 2147483647] left, i32[-2147483648 2147483647] right) {
                        stack i32[-2147483648 2147483647] first = Facade.Forward(left);
                        stack i32[-2147483648 2147483647] second = Facade.Forward(right);
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
            Assert.Equal(1, CountOccurrences(llvm, "define internal dso_local fastcc i32 @__stark_law_clone___stark_mono_fn_Demo__Facade_Forward__i32("));
            Assert.Equal(1, CountOccurrences(llvm, "define internal dso_local fastcc i32 @__stark_law_clone___stark_mono_fn_Demo__Facade_Identity__i32("));
            Assert.Equal(0, CountOccurrences(llvm, "available_externally"));
            Assert.Equal(2, CountOccurrences(llvm, "call fastcc i32 @__stark_law_clone___stark_mono_fn_Demo__Facade_Forward__i32("));
            Assert.Equal(2, CountOccurrences(llvm, "call fastcc i32 @__stark_law_clone___stark_mono_fn_Demo__Facade_Identity__i32("));
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

            fn i32[-2147483648 2147483647] Read(i32[-2147483648 2147483647][] view, i32[-2147483648 2147483647] index) {
                return view[index];
            }

            fn i32[-2147483648 2147483647] Run(i32[-2147483648 2147483647] index) {
                stack i32[-2147483648 2147483647][3] values = { 4, 7, 9 };
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
    public void BorrowedAggregateCallReusesPromotedLocalSlot()
    {
        var result = Compile(
            """
            module Demo

            struct Box {
                i32[-2147483648 2147483647] Value;
            }

            fn void Touch(borrow Box box) {
                return;
            }

            fn void Forward(borrow Box box) {
                stack borrow Box aliasBox = box;
                Touch(aliasBox);
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("define fastcc void @Touch(ptr nonnull noalias readonly nocapture dereferenceable(4) align 4 %arg_box)", llvm);
        Assert.Contains("define fastcc void @Forward(ptr nonnull noalias readonly nocapture dereferenceable(4) align 4 %arg_box)", llvm);
        Assert.Contains("%slot_aliasBox = alloca %Box", llvm);
        Assert.Contains("getelementptr inbounds nuw %Box, ptr %slot_aliasBox", llvm);
        Assert.Contains("call fastcc void @Touch(ptr %", llvm);
        Assert.DoesNotContain("callarg_box", llvm);
    }

    [Fact]
    public void MutableBorrowReceiverForwardingReusesOriginalParameterPointer()
    {
        var result = Compile(
            """
            module Demo

            struct Counter {
                i32[-2147483648 2147483647] Value;

                fn void Reset(borrow mut Counter self) {
                    self.Value = 0;
                    return;
                }

                fn void ResetThenAdd(borrow mut Counter self, i32[-2147483648 2147483647] value) {
                    self.Reset();
                    self.Value += value;
                    return;
                }
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("define fastcc void @Counter_Reset(ptr nonnull noalias writeonly nocapture dereferenceable(4) align 4 %arg_self)", llvm);
        Assert.Contains("define fastcc void @Counter_ResetThenAdd(ptr nonnull noalias writeonly nocapture dereferenceable(4) align 4 %arg_self, i32 %arg_value)", llvm);
        Assert.Contains("call fastcc void @Counter_Reset(ptr %arg_self)", llvm);
        Assert.DoesNotContain("callarg_self", llvm);
    }

    [Fact]
    public void MutableBorrowFieldReceiverUsesOriginalFieldAddress()
    {
        var result = Compile(
            """
            module Demo

            struct Inner {
                i32[-2147483648 2147483647] Value;

                fn void Set(borrow mut Inner self, i32[-2147483648 2147483647] value) {
                    self.Value = value;
                    return;
                }
            }

            struct Outer {
                Inner Item;

                fn void SetItem(borrow mut Outer self, i32[-2147483648 2147483647] value) {
                    self.Item.Set(value);
                    return;
                }
            }
            """,
            options: new CompilerOptions(OptimizationLevel: CompilerOptimizationLevel.O0));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static d => d.ToString())));
        var llvm = GetLlvm(result);

        Assert.Contains("define fastcc void @Inner_Set(ptr nonnull noalias writeonly nocapture dereferenceable(4) align 4 %arg_self", llvm);
        Assert.Contains("call fastcc void @Inner_Set(ptr %", llvm);
        Assert.DoesNotContain("call fastcc void @Inner_Set(ptr %slot_", llvm);
        Assert.DoesNotContain("alloca %Inner", llvm);
    }

    [Fact]
    public void RawPointerLoopBackedgeCastsMaterializePhiIncomingValues()
    {
        var result = Compile(
            """
            module Demo

            struct Node {
                rawmutptr<Node> Next;
            }

            fn rawmutptr<Node> Walk(rawmutptr<Node> head) {
                stack mut rawmutptr<Node> current = head;
                while willexit (current != null) {
                    stack rawmutptr<Node> next = (*current).Next;
                    stack rawmutptr<i8[-128 127]> erased = (rawmutptr<i8[-128 127]>)next;
                    current = (rawmutptr<Node>)erased;
                }

                return current;
            }
            """,
            options: new CompilerOptions(OptimizationLevel: CompilerOptimizationLevel.O0));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static d => d.ToString())));
        var llvm = GetLlvm(result);

        Assert.Contains("phi ptr", llvm);
        Assert.Contains("select i1 true, ptr", llvm);
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

                public struct Counter {
                    i32[-2147483648 2147483647] Value;

                    fn void Reset(borrow mut Counter self) {
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

                    fn void Run() {
                        stack mut Facade.Counter counter = new Facade.Counter() { Value = 1 };
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
                "declare fastcc void @Facade_Counter_Reset(ptr nonnull noalias writeonly nocapture dereferenceable(4) align 4)",
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

            struct Box {
                i32[-2147483648 2147483647] Value;
            }

            doctrine Inspect {
                finite law i32[-2147483648 2147483647] Read(borrow mut Box box) {
                    return box.Value;
                }
            }

            finite law i32[-2147483648 2147483647] Run(borrow mut Box box) {
                return Inspect.Read(box);
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("define fastcc i32 @Inspect_Read(ptr nonnull noalias readonly nocapture dereferenceable(4) align 4 %arg_box)", llvm);
        Assert.Contains("define fastcc i32 @Run(ptr nonnull noalias readonly nocapture dereferenceable(4) align 4 %arg_box)", llvm);
        Assert.Contains("call fastcc i32 @Inspect_Read(ptr %", llvm);
    }

    [Fact]
    public void ConfiguredTargetInfoIsEmittedInHeader()
    {
        var result = Compile(
            """
            module Demo

            fn i32[-2147483648 2147483647] Run() {
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

            fn i32[-2147483648 2147483647] Run(bool left, bool right) {
                return left && right ? 1 : 2;
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("define fastcc i32 @Run(i1 %arg_left, i1 %arg_right)", llvm);
        Assert.Contains("phi i1", llvm);
        Assert.Contains("br i1", llvm);
        Assert.Contains("ret i32 1", llvm);
        Assert.Contains("ret i32 2", llvm);
    }

    [Fact]
    public void PointerOperatorsAndExplicitConversionsEmitRawMemoryAccess()
    {
        var result = Compile(
            """
            module Demo

            static mut i32[-2147483648 2147483647] Counter = 0;

            fn i32[-2147483648 2147483647] Run(i64[-9223372036854775808 9223372036854775807] bits) {
                stack mut i32[-2147483648 2147483647] value = 1;
                stack rawmutptr<i32[-2147483648 2147483647]> ptr = &value;
                stack rawptr<i32[-2147483648 2147483647]> readonlyPtr = (rawptr<i32[-2147483648 2147483647]>)ptr;
                *ptr = (i32[-2147483648 2147483647])bits;
                Counter = *readonlyPtr;
                return *(&Counter);
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("trunc i64 %arg_bits to i32", llvm);
        Assert.Contains("getelementptr inbounds nuw i32, ptr %slot_value, i32 0", llvm);
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

    private static string NormalizeLlvm(string llvm)
    {
        var normalized = Regex.Replace(llvm, @"\bnoundef\b", string.Empty, RegexOptions.CultureInvariant);
        return Regex.Replace(
            normalized,
            @"^(?:define|declare)\b[^\r\n]*$",
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
