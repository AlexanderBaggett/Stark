using Stark.Compiler;

namespace compiler.Tests;

public sealed class FunctionSemanticsTests
{
    [Fact]
    public void PlainFnsReportEffectiveKindsWhenTheyCanBeStrengthened()
    {
        var result = Compile(
            """
            module Demo

            struct Box {
                i32[-2147483648 2147483647] Value;
            }

            unsafe fn i32[-2147483648 2147483647] Add(i32[-2147483648 2147483647] left, i32[-2147483648 2147483647] right) {
                return left + right;
            }

            unsafe fn void Touch(borrow mut Box box) {
                box.Value = 1;
                return;
            }
            """);

        Assert.True(result.Succeeded);
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.SemanticValidation, out SemanticValidationModel? validation));
        Assert.NotNull(validation);

        var add = validation.Functions["Add"];
        Assert.Equal(StarkFunctionKind.Fn, add.DeclaredKind);
        Assert.Equal(StarkFunctionKind.FiniteLaw, add.EffectiveKind);
        Assert.True(add.CanStrengthenKind);

        var touch = validation.Functions["Touch"];
        Assert.Equal(StarkFunctionKind.Fn, touch.DeclaredKind);
        Assert.Equal(StarkFunctionKind.Finite, touch.EffectiveKind);
        Assert.True(touch.CanStrengthenKind);
    }

    [Fact]
    public void PlainFnsWithWillexitLoopsCanStillStrengthenToFiniteKinds()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn i32[-2147483648 2147483647] Count() {
                stack mut i32[-2147483648 2147483647] value = 0;

                while willexit (value < 3) {
                    value += 1;
                }

                return value;
            }
            """);

        Assert.True(result.Succeeded);
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.SemanticValidation, out SemanticValidationModel? validation));
        Assert.NotNull(validation);

        var count = validation.Functions["Count"];
        Assert.Equal(StarkFunctionKind.Fn, count.DeclaredKind);
        Assert.Equal(StarkFunctionKind.FiniteLaw, count.EffectiveKind);
        Assert.True(count.CanStrengthenKind);
    }

    [Fact]
    public void LawFunctionsRejectPlainFunctionPointerCalls()
    {
        var result = Compile(
            """
            module Demo

            unsafe law i32[-2147483648 2147483647] Bad(fnptr<fn i32[-2147483648 2147483647]()> op) {
                return op();
            }
            """);

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK4106"
                && diagnostic.Message.Contains("law-compatible function pointers", StringComparison.Ordinal));
    }

    [Fact]
    public void FunctionKindObligationsAllowMatchingFunctionPointerCalls()
    {
        var result = Compile(
            """
            module Demo

            unsafe finite i32[-2147483648 2147483647] UseFinite(fnptr<finite i32[-2147483648 2147483647]()> op) {
                return op();
            }

            unsafe law i32[-2147483648 2147483647] UseLaw(fnptr<law i32[-2147483648 2147483647]()> op) {
                return op();
            }

            unsafe finite law i32[-2147483648 2147483647] UseStrict(fnptr<finite law i32[-2147483648 2147483647]()> op) {
                return op();
            }
            """);

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.SemanticValidation, out SemanticValidationModel? validation));
        Assert.NotNull(validation);

        Assert.Equal(StarkFunctionKind.Finite, validation.Functions["UseFinite"].DeclaredKind);
        Assert.Equal(StarkFunctionKind.Law, validation.Functions["UseLaw"].DeclaredKind);
        Assert.Equal(StarkFunctionKind.FiniteLaw, validation.Functions["UseStrict"].DeclaredKind);
    }

    [Fact]
    public void NonCapturingLawLambdasGetSeparateSemanticSummaries()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn i32[-2147483648 2147483647] Pure() {
                return 1;
            }

            unsafe fn i32[-2147483648 2147483647] Run() {
                stack fnptr<law i32[-2147483648 2147483647]()> op = () => Pure();
                return op();
            }
            """);

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.SemanticValidation, out SemanticValidationModel? validation));
        Assert.NotNull(validation);

        var lambda = Assert.Single(validation.Functions.Values, static function => function.Name.StartsWith("Run.__lambda_", StringComparison.Ordinal));
        Assert.Equal(StarkFunctionKind.Law, lambda.DeclaredKind);
        Assert.True(lambda.EffectiveKind is StarkFunctionKind.Law or StarkFunctionKind.FiniteLaw);
    }

    [Fact]
    public void NonCapturingLawLambdaBodiesRejectNonLawCalls()
    {
        var result = Compile(
            """
            module Demo

            static i32[-2147483648 2147483647] Counter = 1;

            unsafe fn i32[-2147483648 2147483647] Impure() {
                return Counter;
            }

            unsafe fn void Run() {
                stack fnptr<law i32[-2147483648 2147483647]()> op = () => Impure();
                return;
            }
            """);

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK4106"
                && diagnostic.Message.Contains("Run.__lambda_", StringComparison.Ordinal)
                && diagnostic.Message.Contains("may only call other laws", StringComparison.Ordinal));
    }

    [Fact]
    public void SemanticValidationSummariesCaptureParameterGuaranteesAndReturnCaptures()
    {
        var result = Compile(
            """
            module Demo

            struct Box {
                i32[-2147483648 2147483647] Value;
            }

            unsafe fn retborrow Box Echo(retborrow Box value) {
                return value;
            }
            """);

        Assert.True(result.Succeeded);
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.SemanticValidation, out SemanticValidationModel? validation));
        Assert.NotNull(validation);

        var echo = validation.Functions["Echo"];
        Assert.NotNull(echo.MemoryEffects);
        Assert.True(echo.MemoryEffects!.CapturesArgumentMemory);
        var parameter = Assert.Single(echo.Parameters!);
        Assert.Equal("value", parameter.Name);
        Assert.True(parameter.IsMemoryBacked);
        Assert.True(parameter.GuaranteedNonNull);
        Assert.True(parameter.GuaranteedReadOnly);
        Assert.Equal(4, parameter.DereferenceableBytes);
        Assert.Equal(4, parameter.AlignmentBytes);
        Assert.Equal(ParameterCaptureKind.Return, parameter.CaptureKind);
    }

    [Fact]
    public void SemanticValidationSummariesDeriveConcreteLayoutFactsForPaddedAggregates()
    {
        var result = Compile(
            """
            module Demo

            struct Pair {
                i8[-128 127] Tag;
                i32[-2147483648 2147483647] Value;
            }

            unsafe fn void Inspect(borrow Pair pair) {
                return;
            }
            """);

        Assert.True(result.Succeeded);
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.SemanticValidation, out SemanticValidationModel? validation));
        Assert.NotNull(validation);

        var parameter = Assert.Single(validation.Functions["Inspect"].Parameters!);
        Assert.True(parameter.IsMemoryBacked);
        Assert.True(parameter.GuaranteedNonNull);
        Assert.Equal(8, parameter.DereferenceableBytes);
        Assert.Equal(4, parameter.AlignmentBytes);
    }

    [Fact]
    public void DisjointParameterContractsFlowIntoSemanticNoAliasFacts()
    {
        var result = Compile(
            """
            module Demo

            struct Box {
                i32[-2147483648 2147483647] Value;
            }

            unsafe fn void TouchPrefix(disjoint borrow mut Box left, disjoint borrow mut Box right) {
                left.Value = 1;
                right.Value = 2;
                return;
            }

            unsafe fn void TouchWhere(borrow mut Box left, borrow mut Box right) where disjoint(left, right) {
                left.Value = 1;
                right.Value = 2;
                return;
            }
            """);

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.SemanticValidation, out SemanticValidationModel? validation));
        Assert.NotNull(validation);

        var prefixParameters = validation.Functions["TouchPrefix"].Parameters!.ToDictionary(static parameter => parameter.Name, StringComparer.Ordinal);
        Assert.True(prefixParameters["left"].GuaranteedNoAlias);
        Assert.True(prefixParameters["right"].GuaranteedNoAlias);

        var whereParameters = validation.Functions["TouchWhere"].Parameters!.ToDictionary(static parameter => parameter.Name, StringComparer.Ordinal);
        Assert.True(whereParameters["left"].GuaranteedNoAlias);
        Assert.True(whereParameters["right"].GuaranteedNoAlias);
    }

    [Fact]
    public void ConstParameterQualifierFlowsIntoSemanticReadonlyFacts()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn void Inspect(const rawmutptr<i32[-2147483648 2147483647]> ptr) {
                return;
            }
            """);

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.SemanticValidation, out SemanticValidationModel? validation));
        Assert.NotNull(validation);

        var parameter = Assert.Single(validation.Functions["Inspect"].Parameters!);
        Assert.Equal("ptr", parameter.Name);
        Assert.True(parameter.GuaranteedReadOnly);
    }

    [Fact]
    public void TransitiveCallEffectsFlowIntoSemanticSummaries()
    {
        var result = Compile(
            """
            module Demo

            struct Box {
                i32[-2147483648 2147483647] Value;
            }

            unsafe fn void Touch(borrow mut Box box) {
                box.Value = 1;
                return;
            }

            unsafe fn void Outer(borrow mut Box box) {
                Touch(box);
                return;
            }
            """);

        Assert.True(result.Succeeded);
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.SemanticValidation, out SemanticValidationModel? validation));
        Assert.NotNull(validation);

        var outer = validation.Functions["Outer"];
        Assert.NotNull(outer.MemoryEffects);
        Assert.True(outer.MemoryEffects!.WritesArgumentMemory);
        var parameter = Assert.Single(outer.Parameters!);
        Assert.True(parameter.Writes);

        var call = Assert.Single(outer.Calls!);
        Assert.Equal("Touch", call.CalleeName);
        var argument = Assert.Single(call.Arguments);
        Assert.Equal("box", argument.CallerParameterName);
        Assert.Equal("box", argument.CalleeParameterName);
        Assert.True(argument.Writes);
    }

    [Fact]
    public void SemanticValidationSummariesDistinguishArgumentAndOtherMemory()
    {
        var result = Compile(
            """
            module Demo

            struct Box {
                i32[-2147483648 2147483647] Value;
            }

            static mut i32[-2147483648 2147483647] Counter = 0;

            unsafe fn i32[-2147483648 2147483647] ReadGlobal() {
                return Counter;
            }

            unsafe fn void TouchArg(borrow mut Box box) {
                box.Value = 1;
                return;
            }
            """);

        Assert.True(result.Succeeded);
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.SemanticValidation, out SemanticValidationModel? validation));
        Assert.NotNull(validation);

        var readGlobal = validation.Functions["ReadGlobal"];
        Assert.NotNull(readGlobal.MemoryEffects);
        Assert.True(readGlobal.MemoryEffects!.ReadsOtherMemory);
        Assert.False(readGlobal.MemoryEffects.WritesOtherMemory);
        Assert.False(readGlobal.MemoryEffects.ReadsArgumentMemory);

        var touchArg = validation.Functions["TouchArg"];
        Assert.NotNull(touchArg.MemoryEffects);
        Assert.True(touchArg.MemoryEffects!.WritesArgumentMemory);
        Assert.False(touchArg.MemoryEffects.ReadsOtherMemory);
        Assert.False(touchArg.MemoryEffects.WritesOtherMemory);
    }

    [Fact]
    public void SystemMemoryAllocatorDeclarationSummariesIncludeAllocatorState()
    {
        var result = Compile(
            """
            module System.Memory

            public struct Allocator {
                u8[0 127] Kind;
            }

            internal struct Allocation {
                rawmutptr<i8[-128 127]> Pointer;
                i64[0 max] ByteLength;
                i64[1 max] Alignment;
                Allocator Allocator;
            }

            internal unsafe fn Allocation Allocate(Allocator allocator, i64[0 max] byteLength, i64[1 max] alignment);
            internal unsafe fn Allocation Reallocate(Allocation allocation, i64[0 max] byteLength, i64[1 max] alignment);
            internal unsafe fn void Free(Allocation allocation);
            """);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.SemanticValidation, out SemanticValidationModel? validation));
        Assert.NotNull(validation);

        foreach (var functionName in new[] { "Allocate", "Reallocate", "Free" })
        {
            var memoryEffects = validation.Functions[functionName].MemoryEffects;
            Assert.NotNull(memoryEffects);
            Assert.True(memoryEffects!.ReadsOtherMemory);
            Assert.True(memoryEffects.WritesOtherMemory);
        }
    }

    [Fact]
    public void DynamicStorageOperationsOnBorrowedParametersWriteArgumentMemory()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn bool Reserve(mut borrow dynamic i32[0 max] values) {
                return values.TryReserve(8);
            }

            unsafe fn bool Append(mut borrow dynamic i32[0 max] values) {
                if (!values.TryReserve(1)) {
                    return false;
                }

                init values[values.Length] = 1;
                return true;
            }
            """);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.SemanticValidation, out SemanticValidationModel? validation));
        Assert.NotNull(validation);

        foreach (var functionName in new[] { "Reserve", "Append" })
        {
            var function = validation.Functions[functionName];
            Assert.NotNull(function.MemoryEffects);
            Assert.True(function.MemoryEffects!.ReadsArgumentMemory);
            Assert.True(function.MemoryEffects.WritesArgumentMemory);
            Assert.NotNull(function.Parameters);
            var parameter = Assert.Single(function.Parameters!);
            Assert.True(parameter.Reads);
            Assert.True(parameter.Writes);
        }
    }

    [Fact]
    public void LawsCanCallPlainFnsThatInferAsLaws()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn i32[-2147483648 2147483647] Add(i32[-2147483648 2147483647] left, i32[-2147483648 2147483647] right) {
                return left + right;
            }

            unsafe law i32[-2147483648 2147483647] Use() {
                return Add(1, 2);
            }
            """);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void FiniteFunctionsCanCallPlainFnsThatInferAsFinite()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn i32[-2147483648 2147483647] Step(i32[-2147483648 2147483647] value) {
                return value + 1;
            }

            unsafe finite i32[-2147483648 2147483647] Use(i32[-2147483648 2147483647] value) {
                return Step(value);
            }
            """);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void RuntimeTextConcatenationPreservesFunctionKindObligations()
    {
        var result = Compile(
            """
            import System.Memory
            import System.Text
            module Demo

            unsafe finite law System.Memory.MemoryResult<System.Text.OwnedAscii> Bad(System.Memory.MemoryResult<System.Text.OwnedAscii> result) {
                return "Score: " + result;
            }
            """,
            new CompilerOptions(
                ModuleResolver: new InMemoryModuleResolver(
                [
                    (
                        new ResolvedModuleReference("System.Memory", "/virtual/System.Memory.stark", IsExternal: false),
                        """
                        module System.Memory

                        public enum MemoryError {
                            OutOfMemory,
                        }

                        public enum MemoryResult<T> {
                            Ok(T),
                            Err(MemoryError),
                        }
                        """,
                        "/virtual/System.Memory.stark"
                    ),
                    (
                        new ResolvedModuleReference("System.Text", "/virtual/System.Text.stark", IsExternal: false),
                        """
                        import System.Memory
                        module System.Text

                        public struct OwnedAscii {
                            ascii Text;
                        }

                        public unsafe fn System.Memory.MemoryResult<OwnedAscii> ConcatAscii(ascii left, i64[0 max] leftLength, System.Memory.MemoryResult<OwnedAscii> right);
                        """,
                        "/virtual/System.Text.stark"
                    )
                ])));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "STK4106");
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "STK4107");
    }

    [Fact]
    public void PureCallsInheritExternallyVisibleSliceWrites()
    {
        var result = Compile(
            """
            module Demo

            unsafe law void Touch(mut i32[-2147483648 2147483647][] view) {
                view[0] = 1;
                return;
            }

            unsafe law void Outer(mut i32[-2147483648 2147483647][] view) {
                Touch(view);
                return;
            }
            """);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "STK4104" && diagnostic.Message.Contains("Touch", StringComparison.Ordinal));
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "STK4104" && diagnostic.Message.Contains("through call 'Touch'", StringComparison.Ordinal));
    }

    [Fact]
    public void StaticMemberFunctionsTypeCheckAndPreserveFunctionKinds()
    {
        var result = Compile(
            """
            module Demo

            struct Allocator {
                static unsafe finite law Allocator Default() {
                    return new();
                }

                unsafe finite law bool IsDefault(borrow Allocator self) {
                    return true;
                }
            }

            unsafe fn Allocator UseDefault() {
                return Allocator.Default();
            }
            """);

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheck));
        Assert.NotNull(typeCheck);
        Assert.True(typeCheck.Functions["Allocator.Default"].IsStatic);

        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.SemanticValidation, out SemanticValidationModel? validation));
        Assert.NotNull(validation);
        var defaultMethod = validation.Functions["Allocator.Default"];
        Assert.Equal(StarkFunctionKind.FiniteLaw, defaultMethod.DeclaredKind);
        Assert.Equal(StarkFunctionKind.FiniteLaw, defaultMethod.EffectiveKind);
    }

    [Fact]
    public void InstanceMemberFunctionsCannotBeCalledThroughTypeName()
    {
        var result = Compile(
            """
            module Demo

            struct Box {
                i32[min max] Value;

                unsafe finite law i32[min max] Read(borrow Box self) {
                    return self.Value;
                }
            }

            unsafe fn i32[min max] Read(Box box) {
                return Box.Read(box);
            }
            """);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "STK3014" && diagnostic.Message.Contains("Box.Read", StringComparison.Ordinal));
    }

    [Fact]
    public void StaticMemberFunctionsCannotBeCalledThroughInstance()
    {
        var result = Compile(
            """
            module Demo

            struct Utility {
                static unsafe finite law i32[min max] Value() {
                    return 1;
                }
            }

            unsafe fn i32[min max] Read(Utility utility) {
                return utility.Value();
            }
            """);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "STK3014" && diagnostic.Message.Contains("Utility.Value", StringComparison.Ordinal));
    }

    [Fact]
    public void StaticModifierIsRejectedOutsideStructAndRecordMemberFunctions()
    {
        var result = Compile(
            """
            module Demo

            static unsafe fn void Bad() {
                return;
            }
            """);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "STK4115");
    }

    [Fact]
    public void LawBodiesRejectMutableExternalStateObservationAllocationAndVisibleWrites()
    {
        var result = Compile(
            """
            module Demo

            static mut i32[min max] Counter = 0;

            unsafe law i32[min max] ReadGlobal() {
                return Counter;
            }

            unsafe law void Allocate() {
                heap i32[min max] value = 0;
                return;
            }

            unsafe law void WriteGlobal() {
                Counter = 1;
                return;
            }
            """);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "STK4105" && diagnostic.Message.Contains("ReadGlobal", StringComparison.Ordinal));
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "STK4102" && diagnostic.Message.Contains("Allocate", StringComparison.Ordinal));
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "STK4104" && diagnostic.Message.Contains("WriteGlobal", StringComparison.Ordinal));
    }

    [Fact]
    public void MemberFunctionVisibilityInheritsNarrowsAndAvoidsAccidentalExport()
    {
        var result = Compile(
            """
            module Demo

            export struct Api {
                unsafe fn void SourceVisible() {
                    return;
                }

                internal unsafe fn void RuntimeOnly() {
                    return;
                }

                export unsafe fn void AbiVisible() {
                    return;
                }
            }
            """);

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.SyntaxModel, out SyntaxModel? syntaxModel));
        Assert.NotNull(syntaxModel);

        Assert.Equal(StarkVisibility.Public, Assert.Single(syntaxModel.Declarations, static declaration => declaration.Name == "Api.SourceVisible").Visibility);
        Assert.Equal(StarkVisibility.Internal, Assert.Single(syntaxModel.Declarations, static declaration => declaration.Name == "Api.RuntimeOnly").Visibility);
        Assert.Equal(StarkVisibility.Export, Assert.Single(syntaxModel.Declarations, static declaration => declaration.Name == "Api.AbiVisible").Visibility);
    }

    [Fact]
    public void MemberFunctionVisibilityCannotExceedEnclosingTypeVisibility()
    {
        var result = Compile(
            """
            module Demo

            internal struct Hidden {
                public unsafe fn void Leak() {
                    return;
                }
            }
            """);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "STK4116" && diagnostic.Message.Contains("Hidden.Leak", StringComparison.Ordinal));
    }

    [Fact]
    public void ExportMemberFunctionsRequireExportEnclosingTypes()
    {
        var result = Compile(
            """
            module Demo

            public struct Api {
                export unsafe fn void AbiVisible() {
                    return;
                }
            }
            """);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "STK4116" && diagnostic.Message.Contains("Api.AbiVisible", StringComparison.Ordinal));
    }

    private static CompilationResult Compile(string source, CompilerOptions? options = null)
    {
        return DefaultCompilerPipeline.Create().Run(new CompilationInput(source), options);
    }
}
