using Stark.Compiler;

namespace compiler.Tests;

public sealed class LlvmIrEmissionTests
{
    [Fact]
    public void StraightLineFunctionEmitsConcreteLlvmBody()
    {
        var result = Compile(
            """
            module Demo

            fn i32 Main() {
                stack mut i32 value = 1;
                value = value + 1;
                return value;
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("define fastcc i32 @Main()", llvm);
        Assert.Contains("add i32", llvm);
        Assert.Contains("ret i32", llvm);
        Assert.DoesNotContain("alloca i32", llvm);
        Assert.DoesNotContain("declare fastcc i32 @Main()", llvm);
    }

    [Fact]
    public void BranchingFunctionEmitsConditionalBranch()
    {
        var result = Compile(
            """
            module Demo

            fn i32 Main() {
                if (true) {
                    return 1;
                }

                return 2;
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("br i1 true, label %bb1, label %bb2", llvm);
        Assert.Contains("ret i32", llvm);
    }

    [Fact]
    public void BranchJoinEmitsPhiNode()
    {
        var result = Compile(
            """
            module Demo

            fn i32 Main(bool flag) {
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
        Assert.Contains("[ %v1, %bb1 ], [ %v2, %bb2 ]", llvm);
    }

    [Fact]
    public void BitwiseXorEmitsConcreteLlvmXorInstruction()
    {
        var result = Compile(
            """
            module Demo

            fn i32 Main(i32 left, i32 right) {
                return left ^ right;
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("define fastcc i32 @Main(i32 %arg_left, i32 %arg_right)", llvm);
        Assert.Contains("xor i32 %arg_left, %arg_right", llvm);
    }

    [Fact]
    public void FloatExponentEmitsLlvmPowIntrinsicCall()
    {
        var result = Compile(
            """
            module Demo

            fn f32 Main() {
                return 2.0 ** 3.0;
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("declare float @llvm.pow.f32(float, float)", llvm);
        Assert.Contains("define fastcc float @Main()", llvm);
        Assert.Contains("call float @llvm.pow.f32(float 2.0, float 3.0)", llvm);
    }

    [Fact]
    public void LoopHeaderEmitsBackedgePhi()
    {
        var result = Compile(
            """
            module Demo

            fn i32 Main() {
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
    public void LiteralSwitchBodyEmitsConcreteLlvmSwitch()
    {
        var result = Compile(
            """
            module Demo

            fn i32 Main() {
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

        Assert.Contains("define fastcc i32 @Main()", llvm);
        Assert.Contains("switch i32", llvm);
        Assert.DoesNotContain("icmp eq i32", llvm);
        Assert.DoesNotContain("declare fastcc i32 @Main()", llvm);
    }

    [Fact]
    public void GuardedSwitchBodyEmitsCompareAndGuardBranches()
    {
        var result = Compile(
            """
            module Demo

            fn i32 Main(i32 value, bool allow) {
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

        Assert.Contains("define fastcc i32 @Main(i32 %arg_value, i1 %arg_allow)", llvm);
        Assert.Contains("icmp eq i32", llvm);
        Assert.Contains("br i1 %arg_allow", llvm);
        Assert.DoesNotContain("declare fastcc i32 @Main(i32, i1)", llvm);
    }

    [Fact]
    public void CaptureSwitchPatternEmitsConcreteBody()
    {
        var result = Compile(
            """
            module Demo

            fn i32 Main(i32 value, bool allow) {
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

        Assert.Contains("define fastcc i32 @Main(i32 %arg_value, i1 %arg_allow)", llvm);
        Assert.Contains("br i1 %arg_allow", llvm);
        Assert.Contains("ret i32 %arg_value", llvm);
        Assert.DoesNotContain("declare fastcc i32 @Main(i32, i1)", llvm);
    }

    [Fact]
    public void HelloWorldStyleFfiPutsEmitsStringGlobalAndMainBody()
    {
        var result = Compile(
            """
            module Hello

            ffi fn i32 puts(ascii s);
            export ffi fn i32 main() {
                puts("Hello, world!\n");
                return 0;
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("@.str.0 = private unnamed_addr constant", llvm);
        Assert.Contains("%stark_ascii = type { ptr, i64 }", llvm);
        Assert.Contains("declare i32 @puts(ptr)", llvm);
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

            fn ascii Main() {
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

            fn i32 Main() {
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
        Assert.DoesNotContain("declare fastcc i32 @Main()", llvm);
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

            fn i32 Main() {
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
        Assert.DoesNotContain("; LLVM body emission pending for Main", llvm);
    }

    [Fact]
    public void RecordTypeUsesConcreteAggregateLayout()
    {
        var result = Compile(
            """
            module Demo

            record Point(i32 X, i32 Y) { }

            fn i32 Main() {
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
    public void InternalAggregateParameterUsesIndirectAbi()
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

            fn i32 Main() {
                return Read(new Box() { Value = 7 });
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("define fastcc i32 @Read(ptr readonly %arg_box)", llvm);
        Assert.Contains("load %Box, ptr %arg_box", llvm);
        Assert.Contains("call i32 @Read(ptr %", llvm);
    }

    [Fact]
    public void InternalAggregateReturnUsesSRetAbi()
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

            fn i32 Main() {
                stack Box box = Make();
                return box.Value;
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("define fastcc void @Make(ptr noalias sret(%Box) %ret)", llvm);
        Assert.Contains("store %Box", llvm);
        Assert.Contains("ret void", llvm);
        Assert.Contains("call void @Make(ptr sret(%Box) %", llvm);
        Assert.Contains("load %Box, ptr %", llvm);
    }

    [Fact]
    public void FixedArrayInitializerAndIndexEmitConcreteArrayIr()
    {
        var result = Compile(
            """
            module Demo

            fn i32 Main() {
                stack i32[3] values = { 1, 2, 3 };
                return values[1];
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("define fastcc i32 @Main()", llvm);
        Assert.Contains("insertvalue [3 x i32] zeroinitializer", llvm);
        Assert.Contains("extractvalue [3 x i32]", llvm);
        Assert.DoesNotContain("declare fastcc i32 @Main()", llvm);
    }

    [Fact]
    public void FixedArrayIndexAssignmentEmitsInsertvalueUpdate()
    {
        var result = Compile(
            """
            module Demo

            fn i32 Main() {
                stack mut i32[3] values = { 1, 2, 3 };
                values[1] = 9;
                return values[1];
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.True(CountOccurrences(llvm, "insertvalue [3 x i32]") >= 2);
        Assert.Contains("extractvalue [3 x i32]", llvm);
    }

    [Fact]
    public void DynamicArrayIndexEmitsAddressBasedLoad()
    {
        var result = Compile(
            """
            module Demo

            fn i32 Main(i32 index) {
                stack i32[3] values = { 1, 2, 3 };
                return values[index];
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("define fastcc i32 @Main(i32 %arg_index)", llvm);
        Assert.Contains("alloca [3 x i32]", llvm);
        Assert.Contains("getelementptr inbounds [3 x i32], ptr %slot_values, i32 0", llvm);
        Assert.Contains("getelementptr inbounds [3 x i32], ptr", llvm);
        Assert.Contains("load i32, ptr", llvm);
        Assert.DoesNotContain("declare fastcc i32 @Main(i32)", llvm);
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
    public void DynamicArrayIndexMutationEmitsAddressBasedStoreAndLoad()
    {
        var result = Compile(
            """
            module Demo

            fn i32 Main(i32 index) {
                stack mut i32[3] values = { 1, 2, 3 };
                values[index] = 9;
                return values[index];
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("define fastcc i32 @Main(i32 %arg_index)", llvm);
        Assert.True(CountOccurrences(llvm, "getelementptr inbounds [3 x i32], ptr") >= 2);
        Assert.Contains("store i32", llvm);
        Assert.Contains("load i32, ptr", llvm);
        Assert.DoesNotContain("declare fastcc i32 @Main(i32)", llvm);
    }

    [Fact]
    public void SliceMutationEmitsIndirectStoreAndLoad()
    {
        var result = Compile(
            """
            module Demo

            fn i32 Main(i32[] view, i32 index) {
                view[index] = 9;
                return view[index];
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("define fastcc i32 @Main({ ptr, i64 } %arg_view, i32 %arg_index)", llvm);
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

            fn i32 Main() {
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

        Assert.Contains("declare fastcc void @Geometry_Make(ptr noalias sret(%Geometry_Box))", llvm);
        Assert.Contains("declare fastcc i32 @Geometry_Read(ptr readonly)", llvm);
        Assert.Contains("call void @Geometry_Make(ptr sret(%Geometry_Box)", llvm);
        Assert.Contains("call i32 @Geometry_Read(ptr", llvm);
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

            fn i32 Main(i32 index) {
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
    public void ConfiguredTargetInfoIsEmittedInHeader()
    {
        var result = Compile(
            """
            module Demo

            fn i32 Main() {
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

            fn i32 Main(bool left, bool right) {
                return left && right ? 1 : 2;
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("define fastcc i32 @Main(i1 %arg_left, i1 %arg_right)", llvm);
        Assert.Contains("phi i1", llvm);
        Assert.Contains("phi i32", llvm);
        Assert.Contains("br i1", llvm);
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
