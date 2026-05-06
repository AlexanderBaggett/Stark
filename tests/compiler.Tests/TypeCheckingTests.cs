using System.Numerics;
using Stark.Compiler;

namespace compiler.Tests;

public sealed class TypeCheckingTests
{
    [Fact]
    public void IntegerExponentiationTypeChecks()
    {
        var result = Compile(
            """
            module Demo

            finite law i32[-2147483648 2147483647] Run() {
                return 2 ** 3;
            }
            """);

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }

    [Fact]
    public void FunctionItemsPromoteToExplicitFunctionPointersAndIndirectCallsTypeCheck()
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
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
        var promotion = Assert.Single(typeCheckModel.FunctionPointerPromotions);
        Assert.Equal("Add", promotion.Signature.Name);
        Assert.Equal(StarkTypeKind.FunctionPointer, promotion.TargetType.Kind);
        var addressTaken = Assert.Single(typeCheckModel.AddressTakenFunctions);
        Assert.Equal("Add", addressTaken.Signature.Name);
        Assert.Equal(StarkFunctionKind.Fn, addressTaken.Signature.Kind);
        var indirectCall = Assert.Single(typeCheckModel.IndirectCalls);
        Assert.Equal(StarkTypeKind.FunctionPointer, indirectCall.FunctionPointerType.Kind);
    }

    [Fact]
    public void FunctionItemPromotionPreservesFunctionKindFacts()
    {
        var result = Compile(
            """
            module Demo

            finite law i32[-2147483648 2147483647] Always() {
                return 7;
            }

            fn void Run() {
                stack fnptr<fn i32[-2147483648 2147483647]()> plain = Always;
                stack fnptr<finite i32[-2147483648 2147483647]()> finiteOnly = Always;
                stack fnptr<law i32[-2147483648 2147483647]()> lawOnly = Always;
                stack fnptr<finite law i32[-2147483648 2147483647]()> strict = Always;
                return;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
        Assert.Equal(4, typeCheckModel.FunctionPointerPromotions.Count);
        Assert.All(typeCheckModel.FunctionPointerPromotions, static promotion => Assert.Equal("Always", promotion.Signature.Name));
        var addressTaken = Assert.Single(typeCheckModel.AddressTakenFunctions);
        Assert.Equal("Always", addressTaken.Signature.Name);
        Assert.Equal(StarkFunctionKind.FiniteLaw, addressTaken.Signature.Kind);
    }

    [Fact]
    public void FunctionItemsPromoteFromEachDeclaredFunctionKind()
    {
        var result = Compile(
            """
            module Demo

            fn i32[-2147483648 2147483647] Plain() {
                return 1;
            }

            finite i32[-2147483648 2147483647] FiniteOnly() {
                return 2;
            }

            law i32[-2147483648 2147483647] LawOnly() {
                return 3;
            }

            finite law i32[-2147483648 2147483647] Strict() {
                return 4;
            }

            fn void Run() {
                stack fnptr<fn i32[-2147483648 2147483647]()> plain = Plain;
                stack fnptr<finite i32[-2147483648 2147483647]()> finiteOnly = FiniteOnly;
                stack fnptr<law i32[-2147483648 2147483647]()> lawOnly = LawOnly;
                stack fnptr<finite law i32[-2147483648 2147483647]()> strict = Strict;
                return;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);

        Assert.Equal(4, typeCheckModel.FunctionPointerPromotions.Count);
        Assert.Equal(4, typeCheckModel.AddressTakenFunctions.Count);
        Assert.Contains(typeCheckModel.AddressTakenFunctions, static addressTaken => addressTaken.Signature.Name == "Plain" && addressTaken.Signature.Kind == StarkFunctionKind.Fn);
        Assert.Contains(typeCheckModel.AddressTakenFunctions, static addressTaken => addressTaken.Signature.Name == "FiniteOnly" && addressTaken.Signature.Kind == StarkFunctionKind.Finite);
        Assert.Contains(typeCheckModel.AddressTakenFunctions, static addressTaken => addressTaken.Signature.Name == "LawOnly" && addressTaken.Signature.Kind == StarkFunctionKind.Law);
        Assert.Contains(typeCheckModel.AddressTakenFunctions, static addressTaken => addressTaken.Signature.Name == "Strict" && addressTaken.Signature.Kind == StarkFunctionKind.FiniteLaw);
    }

    [Fact]
    public void FunctionItemPromotionRejectsUnsatisfiedFunctionKindObligations()
    {
        var result = Compile(
            """
            module Demo

            fn i32[-2147483648 2147483647] Plain() {
                return 1;
            }

            finite i32[-2147483648 2147483647] FiniteOnly() {
                return 2;
            }

            law i32[-2147483648 2147483647] LawOnly() {
                return 3;
            }

            fn void Run() {
                stack fnptr<finite i32[-2147483648 2147483647]()> needsFinite = Plain;
                stack fnptr<law i32[-2147483648 2147483647]()> needsLaw = Plain;
                stack fnptr<finite law i32[-2147483648 2147483647]()> needsBothFromFinite = FiniteOnly;
                stack fnptr<finite law i32[-2147483648 2147483647]()> needsBothFromLaw = LawOnly;
                return;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3002"
                && diagnostic.Message.Contains("Function item 'Plain' cannot be promoted", StringComparison.Ordinal)
                && diagnostic.Message.Contains("finite", StringComparison.Ordinal));
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3002"
                && diagnostic.Message.Contains("Function item 'Plain' cannot be promoted", StringComparison.Ordinal)
                && diagnostic.Message.Contains("law", StringComparison.Ordinal));
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3002"
                && diagnostic.Message.Contains("Function item 'FiniteOnly' cannot be promoted", StringComparison.Ordinal)
                && diagnostic.Message.Contains("finite law", StringComparison.Ordinal));
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3002"
                && diagnostic.Message.Contains("Function item 'LawOnly' cannot be promoted", StringComparison.Ordinal)
                && diagnostic.Message.Contains("finite law", StringComparison.Ordinal));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
        Assert.Empty(typeCheckModel.AddressTakenFunctions);
    }

    [Fact]
    public void FunctionItemsPromoteInReturnAndArgumentTargetPositions()
    {
        var result = Compile(
            """
            module Demo

            fn i32[-2147483648 2147483647] Target() {
                return 41;
            }

            fn fnptr<fn i32[-2147483648 2147483647]()> Factory() {
                return Target;
            }

            fn i32[-2147483648 2147483647] Apply(fnptr<fn i32[-2147483648 2147483647]()> callback) {
                return callback() + 1;
            }

            fn i32[-2147483648 2147483647] Run() {
                return Apply(Target);
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
        Assert.Equal(2, typeCheckModel.FunctionPointerPromotions.Count);
        Assert.All(typeCheckModel.FunctionPointerPromotions, static promotion => Assert.Equal("Target", promotion.Signature.Name));
        var addressTaken = Assert.Single(typeCheckModel.AddressTakenFunctions);
        Assert.Equal("Target", addressTaken.Signature.Name);
        Assert.Equal(StarkFunctionKind.Fn, addressTaken.Signature.Kind);
        var indirectCall = Assert.Single(typeCheckModel.IndirectCalls);
        Assert.Equal(StarkTypeKind.FunctionPointer, indirectCall.FunctionPointerType.Kind);
    }

    [Fact]
    public void OverloadedFunctionItemPromotionsPreserveDistinctAddressTakenFacts()
    {
        var result = Compile(
            """
            module Demo

            fn i32[-2147483648 2147483647] Pick() {
                return 1;
            }

            fn i32[-2147483648 2147483647] Pick(i32[-2147483648 2147483647] value) {
                return value;
            }

            fn i32[-2147483648 2147483647] Run() {
                stack fnptr<fn i32[-2147483648 2147483647]()> first = Pick;
                stack fnptr<fn i32[-2147483648 2147483647](i32[-2147483648 2147483647])> second = Pick;
                return first() + second(2);
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
        Assert.Equal(2, typeCheckModel.FunctionPointerPromotions.Count);
        Assert.All(typeCheckModel.FunctionPointerPromotions, static promotion => Assert.Equal("Pick", promotion.Signature.DisplaySourceName));
        Assert.Equal(2, typeCheckModel.AddressTakenFunctions.Count);
        Assert.All(typeCheckModel.AddressTakenFunctions, static addressTaken => Assert.Equal("Pick", addressTaken.Signature.DisplaySourceName));
        Assert.Equal(
            2,
            typeCheckModel.AddressTakenFunctions
                .Select(static addressTaken => addressTaken.Signature.Name)
                .Distinct(StringComparer.Ordinal)
                .Count());
    }

    [Fact]
    public void FunctionPointerPromotionRejectsAbiMismatchedFunctionItems()
    {
        var result = Compile(
            """
            module Demo

            fn i64[-9223372036854775808 9223372036854775807] Wide(i64[-9223372036854775808 9223372036854775807] value) {
                return value;
            }

            fn void Run() {
                stack fnptr<fn i32[-2147483648 2147483647](i32[-2147483648 2147483647])> op = Wide;
                return;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3002"
                && diagnostic.Message.Contains("cannot be promoted", StringComparison.Ordinal));
    }

    [Fact]
    public void NonCapturingLambdasTypeCheckAsExplicitFunctionPointers()
    {
        var result = Compile(
            """
            module Demo

            fn i32[-2147483648 2147483647] Apply(fnptr<fn i32[-2147483648 2147483647](i32[-2147483648 2147483647])> op) {
                return op(41);
            }

            fn i32[-2147483648 2147483647] Run() {
                stack fnptr<fn i32[-2147483648 2147483647](i32[-2147483648 2147483647])> increment = (i32[-2147483648 2147483647] value) => value + 1;
                return Apply((i32[-2147483648 2147483647] value) => value + 1);
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
        Assert.Equal(2, typeCheckModel.Lambdas.Count);
        Assert.All(typeCheckModel.Lambdas, static lambda => Assert.Equal(StarkFunctionKind.Fn, lambda.FunctionPointerType.FunctionPointerKind));
    }

    [Fact]
    public void NonCapturingLambdasCannotUseOuterLocalsWithoutCaptureList()
    {
        var result = Compile(
            """
            module Demo

            fn void Run() {
                stack i32[-2147483648 2147483647] offset = 1;
                stack fnptr<fn i32[-2147483648 2147483647](i32[-2147483648 2147483647])> increment = (i32[-2147483648 2147483647] value) => value + offset;
                return;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3003"
                && diagnostic.Message.Contains("Unknown symbol 'offset'", StringComparison.Ordinal));
    }

    [Fact]
    public void CapturingLambdaSyntaxIsCheckedButNotLoweredYet()
    {
        var result = Compile(
            """
            module Demo

            fn void Run() {
                stack i32[-2147483648 2147483647] offset = 1;
                stack fnptr<fn i32[-2147483648 2147483647](i32[-2147483648 2147483647])> increment = capture(copy offset) (i32[-2147483648 2147483647] value) => value + offset;
                return;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3008"
                && diagnostic.Message.Contains("Capturing lambdas", StringComparison.Ordinal));
    }

    [Fact]
    public void ExplicitCaptureListDoesNotExposeUnlistedOuterLocals()
    {
        var result = Compile(
            """
            module Demo

            fn void Run() {
                stack i32[-2147483648 2147483647] offset = 1;
                stack i32[-2147483648 2147483647] secret = 2;
                stack fnptr<fn i32[-2147483648 2147483647](i32[-2147483648 2147483647])> increment = capture(copy offset) (i32[-2147483648 2147483647] value) => value + secret;
                return;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3003"
                && diagnostic.Message.Contains("Unknown symbol 'secret'", StringComparison.Ordinal));
    }

    [Fact]
    public void ExplicitCaptureListsRejectDuplicateCapturedLocals()
    {
        var result = Compile(
            """
            module Demo

            fn void Run() {
                stack i32[-2147483648 2147483647] value = 1;
                stack fnptr<fn i32[-2147483648 2147483647]()> callback = capture(copy value, read value) () => value;
                return;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3006"
                && diagnostic.Message.Contains("Lambda capture 'value' is listed more than once", StringComparison.Ordinal));
    }

    [Fact]
    public void ExplicitCaptureListsReportUnknownClauseNamesAndModes()
    {
        var result = Compile(
            """
            module Demo

            fn void Run() {
                stack i32[-2147483648 2147483647] value = 1;
                stack fnptr<fn i32[-2147483648 2147483647]()> wrongClause = captures(copy value) () => value;
                stack fnptr<fn i32[-2147483648 2147483647]()> wrongMode = capture(clone value) () => value;
                return;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3008"
                && diagnostic.Message.Contains("Unknown lambda capture clause 'captures'", StringComparison.Ordinal));
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3008"
                && diagnostic.Message.Contains("Unknown lambda capture mode 'clone'", StringComparison.Ordinal));
    }

    [Fact]
    public void UnsafeLambdaCaptureModesRequireUnsafeContext()
    {
        var bad = Compile(
            """
            module Demo

            fn void Run(i32[-2147483648 2147483647] token) {
                stack fnptr<fn i32[-2147483648 2147483647]()> callback = capture(unsafe addr token) () => 1;
                return;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(bad.Succeeded);
        Assert.Contains(
            bad.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3024"
                && diagnostic.Message.Contains("requires an unsafe context", StringComparison.Ordinal));

        var good = Compile(
            """
            module Demo

            fn void Run(i32[-2147483648 2147483647] token, i32[-2147483648 2147483647] sharedState) {
                unsafe {
                    stack fnptr<fn i32[-2147483648 2147483647]()> callback = capture(unsafe addr token, unsafe shared sharedState) () => 1;
                }

                return;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(good.Succeeded);
        Assert.DoesNotContain(
            good.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3024");
        Assert.Contains(
            good.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3008"
                && diagnostic.Message.Contains("Capturing lambdas", StringComparison.Ordinal));
    }

    [Fact]
    public void UnsafeLambdaCaptureModesRequireExplicitUnsafeMarkerAndRejectSafeModeMarkers()
    {
        var result = Compile(
            """
            module Demo

            fn void Run(
                i32[-2147483648 2147483647] token,
                i32[-2147483648 2147483647] sharedState,
                i32[-2147483648 2147483647] copyValue) {
                unsafe {
                    stack fnptr<fn i32[-2147483648 2147483647]()> byAddress = capture(addr token) () => 1;
                    stack fnptr<fn i32[-2147483648 2147483647]()> sharedCallback = capture(shared sharedState) () => 2;
                    stack fnptr<fn i32[-2147483648 2147483647]()> copied = capture(unsafe copy copyValue) () => 3;
                }

                return;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3024"
                && diagnostic.Message.Contains("Capture mode 'addr' must be written as 'unsafe addr'", StringComparison.Ordinal));
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3024"
                && diagnostic.Message.Contains("Capture mode 'shared' must be written as 'unsafe shared'", StringComparison.Ordinal));
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3024"
                && diagnostic.Message.Contains("Only 'addr' and 'shared' capture modes may be marked unsafe", StringComparison.Ordinal));
    }

    [Fact]
    public void LambdaCaptureModeFactsArePreservedInTypeCheckModel()
    {
        var result = Compile(
            """
            module Demo

            fn void Run(
                i32[-2147483648 2147483647] copyValue,
                i32[-2147483648 2147483647] readValue,
                i32[-2147483648 2147483647] moveValue,
                i32[-2147483648 2147483647] addrValue,
                i32[-2147483648 2147483647] sharedValue) {
                unsafe {
                    stack fnptr<fn i32[-2147483648 2147483647]()> callback =
                        capture(copy copyValue, read readValue, move moveValue, unsafe addr addrValue, unsafe shared sharedValue) () => copyValue + readValue;
                }

                return;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3008"
                && diagnostic.Message.Contains("Capturing lambdas", StringComparison.Ordinal));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
        Assert.Equal(5, typeCheckModel.LambdaCaptures.Count);

        AssertCapture(typeCheckModel, "copyValue", "copy", isUnsafe: false);
        AssertCapture(typeCheckModel, "readValue", "read", isUnsafe: false);
        AssertCapture(typeCheckModel, "moveValue", "move", isUnsafe: false);
        AssertCapture(typeCheckModel, "addrValue", "addr", isUnsafe: true);
        AssertCapture(typeCheckModel, "sharedValue", "shared", isUnsafe: true);
        Assert.Single(typeCheckModel.LambdaCaptures.Select(static capture => capture.LambdaLocation).Distinct());
    }

    [Fact]
    public void CopyLambdaCapturesRejectMoveOnlyBindings()
    {
        var result = Compile(
            """
            module Demo

            struct Token {
                i32[-2147483648 2147483647] Value;
            }

            fn void Run() {
                stack Token token = new Token() { Value = 1 };
                stack fnptr<fn i32[-2147483648 2147483647]()> callback =
                    capture(copy token) () => token.Value;
                return;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3002"
                && diagnostic.Message.Contains("Capture mode 'copy' cannot copy 'token'", StringComparison.Ordinal)
                && diagnostic.Message.Contains("Use 'move' to transfer ownership", StringComparison.Ordinal));
    }

    [Fact]
    public void CopyLambdaCapturesAllowTextViews()
    {
        var result = Compile(
            """
            module Demo

            fn void Run() {
                stack ascii label = "Score: ";
                stack unicode word = "Ready";
                stack fnptr<fn i32[-2147483648 2147483647]()> callback =
                    capture(copy label, copy word) () => 1;
                return;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.DoesNotContain(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3002"
                && diagnostic.Message.Contains("Capture mode 'copy' cannot copy", StringComparison.Ordinal));
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3008"
                && diagnostic.Message.Contains("Capturing lambdas", StringComparison.Ordinal));
    }

    [Fact]
    public void ReadAndCopyLambdaCapturesDoNotExposeWritableBindings()
    {
        var result = Compile(
            """
            module Demo

            fn void Run() {
                stack mut i32[-2147483648 2147483647] readValue = 1;
                stack mut i32[-2147483648 2147483647] copyValue = 2;
                stack fnptr<fn void()> readCallback = capture(read readValue) () => {
                    readValue = 3;
                    return;
                };
                stack fnptr<fn void()> copyCallback = capture(copy copyValue) () => {
                    copyValue = 4;
                    return;
                };
                return;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3007"
                && diagnostic.Message.Contains("Cannot assign to immutable local 'readValue'", StringComparison.Ordinal));
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3007"
                && diagnostic.Message.Contains("Cannot assign to immutable local 'copyValue'", StringComparison.Ordinal));
    }

    [Fact]
    public void MutLambdaCapturesExposeWritableBindingsInLambdaBody()
    {
        var result = Compile(
            """
            module Demo

            fn void Run() {
                stack mut i32[-2147483648 2147483647] value = 1;
                stack fnptr<fn void()> callback = capture(mut value) () => {
                    value = 2;
                    return;
                };
                return;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.DoesNotContain(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3007");
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3008"
                && diagnostic.Message.Contains("Capturing lambdas", StringComparison.Ordinal));
    }

    [Fact]
    public void OutAndInitLambdaCapturesRejectReadsInLambdaBody()
    {
        var result = Compile(
            """
            module Demo

            fn void Run() {
                stack mut i32[-2147483648 2147483647] outValue = 1;
                stack mut i32[-2147483648 2147483647] initValue = 2;
                stack fnptr<fn void()> outCallback = capture(out outValue) () => {
                    stack i32[-2147483648 2147483647] readOut = outValue;
                    return;
                };
                stack fnptr<fn void()> initCallback = capture(init initValue) () => {
                    stack i32[-2147483648 2147483647] readInit = initValue;
                    return;
                };
                return;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3002"
                && diagnostic.Message.Contains("Assignment expects 'i32", StringComparison.Ordinal)
                && diagnostic.Message.Contains("out i32", StringComparison.Ordinal));
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3002"
                && diagnostic.Message.Contains("Assignment expects 'i32", StringComparison.Ordinal)
                && diagnostic.Message.Contains("init i32", StringComparison.Ordinal));
    }

    [Fact]
    public void OutAndInitLambdaCapturesAllowWritesInLambdaBody()
    {
        var result = Compile(
            """
            module Demo

            fn void Run() {
                stack mut i32[-2147483648 2147483647] outValue = 1;
                stack mut i32[-2147483648 2147483647] initValue = 2;
                stack fnptr<fn void()> callback = capture(out outValue, init initValue) () => {
                    outValue = 3;
                    initValue = 4;
                    return;
                };
                return;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.DoesNotContain(
            result.Diagnostics,
            static diagnostic => diagnostic.Code is "STK3002" or "STK3007");
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3008"
                && diagnostic.Message.Contains("Capturing lambdas", StringComparison.Ordinal));
    }

    [Fact]
    public void UnsafeAddrLambdaCapturesExposeReadonlyAddressNotCapturedValue()
    {
        var result = Compile(
            """
            module Demo

            fn void Run(i32[-2147483648 2147483647] token) {
                unsafe {
                    stack fnptr<fn void()> badRead = capture(unsafe addr token) () => {
                        stack i32[-2147483648 2147483647] value = token;
                        return;
                    };

                    stack fnptr<fn void()> goodAddress = capture(unsafe addr token) () => {
                        stack rawptr<frozen i32[-2147483648 2147483647]> address = token;
                        return;
                    };
                }

                return;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3002"
                && diagnostic.Message.Contains("Assignment expects 'i32", StringComparison.Ordinal)
                && diagnostic.Message.Contains("rawptr<frozen i32", StringComparison.Ordinal));
        Assert.DoesNotContain(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3002"
                && diagnostic.Message.Contains("address", StringComparison.Ordinal));
    }

    [Fact]
    public void UnsafeSharedLambdaCapturesExposeSharedReadOnlyBindings()
    {
        var result = Compile(
            """
            module Demo

            fn void Run(i32[-2147483648 2147483647] sharedValue) {
                unsafe {
                    stack fnptr<fn void()> callback = capture(unsafe shared sharedValue) () => {
                        stack shared i32[-2147483648 2147483647] value = sharedValue;
                        sharedValue = 3;
                        return;
                    };
                }

                return;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.DoesNotContain(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3002"
                && diagnostic.Message.Contains("value", StringComparison.Ordinal));
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3007"
                && diagnostic.Message.Contains("Cannot assign to immutable local 'sharedValue'", StringComparison.Ordinal));
    }

    [Fact]
    public void WritableLambdaCaptureModesRequireWritableBindings()
    {
        var bad = Compile(
            """
            module Demo

            fn void Run(
                i32[-2147483648 2147483647] mutValue,
                i32[-2147483648 2147483647] outValue,
                i32[-2147483648 2147483647] initValue) {
                stack fnptr<fn i32[-2147483648 2147483647]()> callback =
                    capture(mut mutValue, out outValue, init initValue) () => 1;
                return;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(bad.Succeeded);
        Assert.Contains(
            bad.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3002"
                && diagnostic.Message.Contains("Capture mode 'mut' needs 'mutValue'", StringComparison.Ordinal));
        Assert.Contains(
            bad.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3002"
                && diagnostic.Message.Contains("Capture mode 'out' needs 'outValue'", StringComparison.Ordinal));
        Assert.Contains(
            bad.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3002"
                && diagnostic.Message.Contains("Capture mode 'init' needs 'initValue'", StringComparison.Ordinal));

        var good = Compile(
            """
            module Demo

            fn void Run() {
                stack mut i32[-2147483648 2147483647] mutValue = 1;
                stack mut i32[-2147483648 2147483647] outValue = 2;
                stack mut i32[-2147483648 2147483647] initValue = 3;
                stack fnptr<fn i32[-2147483648 2147483647]()> callback =
                    capture(mut mutValue, out outValue, init initValue) () => 1;
                return;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(good.Succeeded);
        Assert.DoesNotContain(
            good.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3002");
        Assert.Contains(
            good.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3008"
                && diagnostic.Message.Contains("Capturing lambdas", StringComparison.Ordinal));
    }

    [Fact]
    public void UnsafeFunctionsRequireUnsafeContextDuringTypeChecking()
    {
        var bad = Compile(
            """
            module Demo

            unsafe fn void Touch();

            fn void Run() {
                Touch();
                return;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(bad.Succeeded);
        Assert.Contains(
            bad.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3024"
                && diagnostic.Message.Contains("Unsafe function 'Touch' requires an unsafe context", StringComparison.Ordinal));

        var good = Compile(
            """
            module Demo

            unsafe fn void Touch();

            fn void Run() {
                unsafe {
                    Touch();
                }

                return;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(good.Succeeded, string.Join(", ", good.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }

    [Fact]
    public void RawPointerSignaturesRequireUnsafeFunctions()
    {
        var result = Compile(
            """
            module Demo

            fn void Touch(rawptr<i32[-2147483648 2147483647]> pointer);
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3024"
                && diagnostic.Message.Contains("uses raw pointer types and must be declared 'unsafe'", StringComparison.Ordinal));
    }

    [Fact]
    public void RawPointerLocalOperationsRequireUnsafeContext()
    {
        var result = Compile(
            """
            module Demo

            fn void Run() {
                stack mut i32[-2147483648 2147483647] value = 1;
                stack rawmutptr<i32[-2147483648 2147483647]> pointer = &value;
                *pointer = 2;
                return;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3024"
                && diagnostic.Message.Contains("local raw pointer declarations", StringComparison.Ordinal));
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3024"
                && diagnostic.Message.Contains("Raw pointer address-of operator", StringComparison.Ordinal));
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3024"
                && diagnostic.Message.Contains("Raw pointer dereference operator", StringComparison.Ordinal));
    }

    [Fact]
    public void UnsafeBlocksPermitRawPointerLocalOperations()
    {
        var result = Compile(
            """
            module Demo

            fn i32[-2147483648 2147483647] Run() {
                stack mut i32[-2147483648 2147483647] value = 1;
                unsafe {
                    stack rawmutptr<i32[-2147483648 2147483647]> pointer = &value;
                    *pointer = 2;
                }

                return value;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }

    [Fact]
    public void FfiDeclarationsRequireUnsafeModifier()
    {
        var result = Compile(
            """
            module Demo

            ffi fn void NativeCall();
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3024"
                && diagnostic.Message.Contains("FFI and assembly function 'NativeCall' must be declared 'unsafe'", StringComparison.Ordinal));
    }

    [Fact]
    public void UnsafeFunctionItemsDoNotPromoteToOrdinaryFunctionPointers()
    {
        var outsideUnsafe = Compile(
            """
            module Demo

            unsafe fn i32[-2147483648 2147483647] Touch() {
                return 1;
            }

            fn void Run() {
                stack fnptr<fn i32[-2147483648 2147483647]()> callback = Touch;
                return;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(outsideUnsafe.Succeeded);
        Assert.Contains(
            outsideUnsafe.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3024"
                && diagnostic.Message.Contains("cannot be promoted to ordinary function pointer", StringComparison.Ordinal));
        Assert.True(outsideUnsafe.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? outsideUnsafeModel));
        Assert.NotNull(outsideUnsafeModel);
        Assert.Empty(outsideUnsafeModel.AddressTakenFunctions);

        var insideUnsafe = Compile(
            """
            module Demo

            unsafe fn i32[-2147483648 2147483647] Touch() {
                return 1;
            }

            fn void Run() {
                unsafe {
                    stack fnptr<fn i32[-2147483648 2147483647]()> callback = Touch;
                }

                return;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(insideUnsafe.Succeeded, string.Join(", ", insideUnsafe.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(insideUnsafe.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? insideUnsafeModel));
        Assert.NotNull(insideUnsafeModel);
        Assert.Single(insideUnsafeModel.AddressTakenFunctions);
    }

    [Fact]
    public void UnsafeFunctionItemsDoNotPromoteInReturnOrArgumentTargetPositions()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn i32[-2147483648 2147483647] Touch() {
                return 1;
            }

            fn fnptr<fn i32[-2147483648 2147483647]()> Factory() {
                return Touch;
            }

            fn void Register(fnptr<fn i32[-2147483648 2147483647]()> callback) {
                return;
            }

            fn void Run() {
                Register(Touch);
                return;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
        Assert.Empty(typeCheckModel.AddressTakenFunctions);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3024"
                && diagnostic.Message.Contains("cannot be promoted to ordinary function pointer", StringComparison.Ordinal));
        Assert.True(
            result.Diagnostics.Count(static diagnostic => diagnostic.Code == "STK3024") >= 2,
            string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }

    [Fact]
    public void TypeRelativeIntegerRangeEndpointsResolveAgainstContainingIntegerType()
    {
        var result = Compile(
            """
            module Demo

            fn i32[min max] Signed(i32[min max] value) {
                return value;
            }

            fn i64[0 max] NonNegative(i64[0 max] value) {
                return value;
            }

            fn u8[min 127] BytePrefix(u8[min 127] value) {
                return value;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);

        var signed = typeCheckModel.Functions["Signed"];
        AssertIntegerRange(signed.ReturnType, 32, new BigInteger(int.MinValue), new BigInteger(int.MaxValue));
        AssertIntegerRange(signed.Parameters[0].Type, 32, new BigInteger(int.MinValue), new BigInteger(int.MaxValue));

        var nonNegative = typeCheckModel.Functions["NonNegative"];
        AssertIntegerRange(nonNegative.ReturnType, 64, BigInteger.Zero, new BigInteger(long.MaxValue));
        AssertIntegerRange(nonNegative.Parameters[0].Type, 64, BigInteger.Zero, new BigInteger(long.MaxValue));

        var bytePrefix = typeCheckModel.Functions["BytePrefix"];
        AssertIntegerRange(bytePrefix.ReturnType, 8, BigInteger.Zero, new BigInteger(127), isUnsigned: true);
        AssertIntegerRange(bytePrefix.Parameters[0].Type, 8, BigInteger.Zero, new BigInteger(127), isUnsigned: true);
        Assert.Equal("u8[0 127]", bytePrefix.ReturnType.DisplayName);
    }

    [Fact]
    public void ImportedModulePublicMembersResolveByFinalName()
    {
        var result = Compile(
            """
            import Lib.Foundation
            module Demo

            fn u32[0 2147483647] Use() {
                stack Box box = new() { Value = Identity(Answer) };
                stack Status status = Status.Ok;
                switch (status) {
                    case Status.Ok:
                        return box.Value;
                    case Status.Err:
                        return Worker.Value();
                }
            }
            """,
            new CompilerOptions(
                ModuleResolver: new InMemoryModuleResolver(
                [
                    (
                        new ResolvedModuleReference("Lib.Foundation", "Lib/Foundation.stark"),
                        """
                        module Lib.Foundation

                        public const Answer = 41;

                        public struct Box {
                            u32[0 2147483647] Value;
                        }

                        public enum Status {
                            Ok,
                            Err
                        }

                        public struct Worker {
                            static finite law u32[0 2147483647] Value() {
                                return 7;
                            }
                        }

                        public fn u32[0 2147483647] Identity(u32[0 2147483647] value) {
                            return value;
                        }
                        """,
                        "Lib/Foundation.stark")
                ])));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }

    [Fact]
    public void ScalarConstDeclarationsInferSmallestExactNumericTypes()
    {
        var result = Compile(
            """
            module Demo

            const BoardWidth = 80;
            const u8 BoardWidthTyped = 80;
            const BoardWidthWide = 80;
            const Negative = -129;
            const BigCount = 2 ** 16;
            const u8 UnsignedSmall = 80;
            const u32 UnsignedWide = 4294967295;
            const SmallFloat = 3.5;
            const FloatLiteral = 3.5f;
            const f32 ExplicitFloat = 3.5;
            const f64 ExplicitSmallFloat = 3.5f;

            finite law u8[0 max] UseBoardWidth() {
                return BoardWidth;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);

        AssertIntegerRange(typeCheckModel.Globals["BoardWidth"].Type, 8, new BigInteger(80), new BigInteger(80), isUnsigned: true);
        AssertIntegerRange(typeCheckModel.Globals["BoardWidthTyped"].Type, 8, new BigInteger(80), new BigInteger(80), isUnsigned: true);
        AssertIntegerRange(typeCheckModel.Globals["BoardWidthWide"].Type, 8, new BigInteger(80), new BigInteger(80), isUnsigned: true);
        AssertIntegerRange(typeCheckModel.Globals["Negative"].Type, 16, new BigInteger(-129), new BigInteger(-129));
        AssertIntegerRange(typeCheckModel.Globals["BigCount"].Type, 24, new BigInteger(65536), new BigInteger(65536), isUnsigned: true);
        AssertIntegerRange(typeCheckModel.Globals["UnsignedSmall"].Type, 8, new BigInteger(80), new BigInteger(80), isUnsigned: true);
        AssertIntegerRange(typeCheckModel.Globals["UnsignedWide"].Type, 32, BigInteger.Parse("4294967295"), BigInteger.Parse("4294967295"), isUnsigned: true);
        Assert.Equal(StarkTypeKind.Float, typeCheckModel.Globals["SmallFloat"].Type.Kind);
        Assert.Equal(64, typeCheckModel.Globals["SmallFloat"].Type.BitWidth);
        Assert.Equal(StarkTypeKind.Float, typeCheckModel.Globals["FloatLiteral"].Type.Kind);
        Assert.Equal(32, typeCheckModel.Globals["FloatLiteral"].Type.BitWidth);
        Assert.Equal(StarkTypeKind.Float, typeCheckModel.Globals["ExplicitFloat"].Type.Kind);
        Assert.Equal(32, typeCheckModel.Globals["ExplicitFloat"].Type.BitWidth);
        Assert.Equal(StarkTypeKind.Float, typeCheckModel.Globals["ExplicitSmallFloat"].Type.Kind);
        Assert.Equal(32, typeCheckModel.Globals["ExplicitSmallFloat"].Type.BitWidth);

        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Severity == DiagnosticSeverity.Warning
            && diagnostic.Message.Contains("ExplicitFloat", StringComparison.Ordinal)
            && diagnostic.Message.Contains("f32", StringComparison.Ordinal));
        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Severity == DiagnosticSeverity.Warning
            && diagnostic.Message.Contains("ExplicitSmallFloat", StringComparison.Ordinal)
            && diagnostic.Message.Contains("f32", StringComparison.Ordinal));
    }

    [Fact]
    public void StrictIntegerRangesRejectExplicitScalarConstWrongWidthOrSign()
    {
        var result = Compile(
            """
            module Demo

            const i32 PositiveSigned = 80;
            const u32 PositiveWide = 80;
            const i32 NegativeWide = -1;
            const u8 CorrectUnsigned = 255;
            const i8 CorrectSigned = -1;
            """,
            new CompilerOptions(
                StopAfterPassId: "type-check",
                EnforceIntegerRangeStorageRules: true));

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3014"
                && diagnostic.Message.Contains("PositiveSigned", StringComparison.Ordinal)
                && diagnostic.Message.Contains("i32", StringComparison.Ordinal)
                && diagnostic.Message.Contains("u8", StringComparison.Ordinal));
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3014"
                && diagnostic.Message.Contains("PositiveWide", StringComparison.Ordinal)
                && diagnostic.Message.Contains("u32", StringComparison.Ordinal)
                && diagnostic.Message.Contains("u8", StringComparison.Ordinal));
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3014"
                && diagnostic.Message.Contains("NegativeWide", StringComparison.Ordinal)
                && diagnostic.Message.Contains("i32", StringComparison.Ordinal)
                && diagnostic.Message.Contains("i8", StringComparison.Ordinal));
        Assert.DoesNotContain(
            result.Diagnostics,
            static diagnostic => diagnostic.Message.Contains("CorrectUnsigned", StringComparison.Ordinal)
                || diagnostic.Message.Contains("CorrectSigned", StringComparison.Ordinal));
    }

    [Fact]
    public void ScalarConstDeclarationsReportFriendlyNumericTypeDiagnostics()
    {
        var result = Compile(
            """
            module Demo

            const i32[min max] Ranged = 80;
            const i8 TooSmall = 200;
            const f32 TooPrecise = 0.1;
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "STK3002"
            && diagnostic.Message.Contains("already has one exact value", StringComparison.Ordinal)
            && diagnostic.Message.Contains("does not need an integer range", StringComparison.Ordinal));
        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "STK3002"
            && diagnostic.Message.Contains("TooSmall", StringComparison.Ordinal)
            && diagnostic.Message.Contains("does not fit in i8", StringComparison.Ordinal));
        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "STK3002"
            && diagnostic.Message.Contains("TooPrecise", StringComparison.Ordinal)
            && diagnostic.Message.Contains("without changing it", StringComparison.Ordinal));
    }

    [Fact]
    public void DictionaryAllowsCompilerProvenKeyTypes()
    {
        var result = Compile(
            """
            import System.Collections
            module Demo

            fn void Use(Dictionary<i32[0 max], bool> integers, Dictionary<bool, i32[0 max]> flags) {
                return;
            }
            """,
            new CompilerOptions(
                StopAfterPassId: "type-check",
                ModuleResolver: new InMemoryModuleResolver(
                [
                    (
                        new ResolvedModuleReference("System.Collections", "System/Collections.stark"),
                        """
                        module System.Collections

                        public struct Dictionary<K, V> {
                            K Key;
                            V Value;
                        }
                        """,
                        "System/Collections.stark")
                ])));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "STK3023");
    }

    [Fact]
    public void DictionaryRejectsUnprovenKeyTypes()
    {
        var result = Compile(
            """
            import System.Collections
            module Demo

            struct Box {
                i32[0 max] Value;
            }

            fn void Use(Dictionary<Box, i32[0 max]> boxes) {
                return;
            }
            """,
            new CompilerOptions(
                StopAfterPassId: "type-check",
                ModuleResolver: new InMemoryModuleResolver(
                [
                    (
                        new ResolvedModuleReference("System.Collections", "System/Collections.stark"),
                        """
                        module System.Collections

                        public struct Dictionary<K, V> {
                            K Key;
                            V Value;
                        }
                        """,
                        "System/Collections.stark")
                ])));

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3023"
                && diagnostic.Message.Contains("Dictionary key type 'Box'", StringComparison.Ordinal)
                && diagnostic.Message.Contains("System.Collections.DictionaryKey<Box>", StringComparison.Ordinal));
    }

    [Fact]
    public void DictionaryRejectsUnprovenKeyTypesAfterGenericMonomorphization()
    {
        var result = Compile(
            """
            import System.Collections
            module Demo

            struct Box {
                i32[0 max] Value;
            }

            fn void Use<K>(K key) {
                stack Dictionary<K, i32[0 max]> values = new();
                return;
            }

            fn void Bad(Box key) {
                Use(key);
                return;
            }
            """,
            new CompilerOptions(
                StopAfterPassId: "monomorphization-plan",
                ModuleResolver: new InMemoryModuleResolver(
                [
                    (
                        new ResolvedModuleReference("System.Collections", "System/Collections.stark"),
                        """
                        module System.Collections

                        public struct Dictionary<K, V> {
                            K Key;
                            V Value;
                        }
                        """,
                        "System/Collections.stark")
                ])));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "STK3023");
    }

    [Fact]
    public void AmbiguousImportedTypeFinalNamesRequireQualification()
    {
        var result = Compile(
            """
            import Left
            import Right
            module Demo

            fn i32[0 max] Use() {
                stack Value value = new() { X = 1 };
                return value.X;
            }
            """,
            new CompilerOptions(
                StopAfterPassId: "type-check",
                ModuleResolver: new InMemoryModuleResolver(
                [
                    (
                        new ResolvedModuleReference("Left", "Left.stark"),
                        """
                        module Left

                        public struct Value {
                            i32[0 max] X;
                        }
                        """,
                        "Left.stark"),
                    (
                        new ResolvedModuleReference("Right", "Right.stark"),
                        """
                        module Right

                        public struct Value {
                            i32[0 max] X;
                        }
                        """,
                        "Right.stark")
                ])));

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3004"
                && diagnostic.Message.Contains("Imported type name 'Value'", StringComparison.Ordinal)
                && diagnostic.Message.Contains("Left.Value", StringComparison.Ordinal)
                && diagnostic.Message.Contains("Right.Value", StringComparison.Ordinal));
    }

    [Fact]
    public void ConstantArithmeticIntegerRangeEndpointsResolveAtCompileTime()
    {
        var result = Compile(
            """
            module Demo

            fn u48[10 ** 2 10 ** 10] DecimalPowers(u48[10 ** 2 10 ** 10] value) {
                return value;
            }

            fn u24[2 ** 4 2 ** 16] BinaryPowers(u24[2 ** 4 2 ** 16] value) {
                return value;
            }

            fn u32[1024 * 1024 1024 * 1024 * 1024] Sizes(u32[1024 * 1024 1024 * 1024 * 1024] value) {
                return value;
            }

            fn u8[(1 + 2) * 3 20 / 2 + 1] MixedArithmetic(u8[(1 + 2) * 3 20 / 2 + 1] value) {
                return value;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);

        var decimalPowers = typeCheckModel.Functions["DecimalPowers"];
        AssertIntegerRange(decimalPowers.ReturnType, 48, new BigInteger(100), BigInteger.Parse("10000000000"), isUnsigned: true);
        AssertIntegerRange(decimalPowers.Parameters[0].Type, 48, new BigInteger(100), BigInteger.Parse("10000000000"), isUnsigned: true);

        var binaryPowers = typeCheckModel.Functions["BinaryPowers"];
        AssertIntegerRange(binaryPowers.ReturnType, 24, new BigInteger(16), new BigInteger(65536), isUnsigned: true);
        AssertIntegerRange(binaryPowers.Parameters[0].Type, 24, new BigInteger(16), new BigInteger(65536), isUnsigned: true);

        var sizes = typeCheckModel.Functions["Sizes"];
        AssertIntegerRange(sizes.ReturnType, 32, new BigInteger(1048576), new BigInteger(1073741824), isUnsigned: true);
        AssertIntegerRange(sizes.Parameters[0].Type, 32, new BigInteger(1048576), new BigInteger(1073741824), isUnsigned: true);

        var mixedArithmetic = typeCheckModel.Functions["MixedArithmetic"];
        AssertIntegerRange(mixedArithmetic.ReturnType, 8, new BigInteger(9), new BigInteger(11), isUnsigned: true);
        AssertIntegerRange(mixedArithmetic.Parameters[0].Type, 8, new BigInteger(9), new BigInteger(11), isUnsigned: true);
    }

    [Fact]
    public void StrictIntegerRangesRejectSignedEndpointsOutsideBaseType()
    {
        var result = Compile(
            """
            module Demo

            fn i32[10**2 10**10] TooWide(i8[-200 0] value) {
                return 0;
            }
            """,
            new CompilerOptions(
                StopAfterPassId: "type-check",
                EnforceIntegerRangeStorageRules: true));

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3014"
                && diagnostic.Message.Contains("i32", StringComparison.Ordinal)
                && diagnostic.Message.Contains("between", StringComparison.Ordinal)
                && diagnostic.Message.Contains("u48[100 10000000000]", StringComparison.Ordinal));
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3014"
                && diagnostic.Message.Contains("i8", StringComparison.Ordinal)
                && diagnostic.Message.Contains("between", StringComparison.Ordinal)
                && diagnostic.Message.Contains("i16[-200 0]", StringComparison.Ordinal));
    }

    [Fact]
    public void UnsupportedIntegerRangeEndpointIdentifiersAreRejected()
    {
        var result = Compile(
            """
            module Demo

            fn i32[foo max] Bad() {
                return 0;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "STK3014"
                && diagnostic.Message.Contains("Integer range endpoint 'foo'", StringComparison.Ordinal));
    }

    [Fact]
    public void ConstantArithmeticIntegerRangeEndpointOverflowIsRejected()
    {
        var result = Compile(
            """
            module Demo

            fn i32[0 2 ** 2048] Bad() {
                return 0;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "STK3014"
                && diagnostic.Message.Contains("overflowed", StringComparison.Ordinal));
    }

    [Fact]
    public void ConstantArithmeticIntegerRangeEndpointDivisionByZeroIsRejected()
    {
        var result = Compile(
            """
            module Demo

            fn i32[0 10 / 0] Bad() {
                return 0;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "STK3014"
                && diagnostic.Message.Contains("divide by zero", StringComparison.Ordinal));
    }

    [Fact]
    public void ReversedTypeRelativeIntegerRangeEndpointsAreRejected()
    {
        var result = Compile(
            """
            module Demo

            fn i32[max min] Bad() {
                return 0;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "STK3014"
                && diagnostic.Message.Contains("lower bound", StringComparison.Ordinal)
                && diagnostic.Message.Contains("upper bound", StringComparison.Ordinal));
    }

    [Fact]
    public void UnsignedIntegerRangeEndpointsBelowZeroAreRejected()
    {
        var result = Compile(
            """
            module Demo

            fn u8[-1 10] Bad(u8[-1 10] value) {
                return value;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "STK3014"
                && diagnostic.Message.Contains("u8", StringComparison.Ordinal)
                && diagnostic.Message.Contains("0", StringComparison.Ordinal)
                && diagnostic.Message.Contains("255", StringComparison.Ordinal));
    }

    [Fact]
    public void ReversedConstantArithmeticIntegerRangeEndpointsAreRejected()
    {
        var result = Compile(
            """
            module Demo

            fn i32[2 ** 8 2 ** 4] Bad() {
                return 0;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "STK3014"
                && diagnostic.Message.Contains("lower bound", StringComparison.Ordinal)
                && diagnostic.Message.Contains("upper bound", StringComparison.Ordinal));
    }

    [Fact]
    public void TypeRelativeIntegerEndpointNamesAreRejectedOutsideIntegerRanges()
    {
        var result = Compile(
            """
            module Demo

            fn i32[min max] Bad() {
                return min;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "STK3003"
                && diagnostic.Message.Contains("Unknown symbol 'min'", StringComparison.Ordinal));
    }

    [Fact]
    public void BitwiseXorRequiresIntegerOperands()
    {
        var result = Compile(
            """
            module Demo

            finite law f32 Run() {
                return 1.0 ^ 2.0;
            }
            """);

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "STK3002"
                && diagnostic.Message.Contains("integer operands", StringComparison.Ordinal));
    }

    [Fact]
    public void ExplicitArithmeticOperatorsTypeCheckWithoutPlaceholderDiagnostics()
    {
        var result = Compile(
            """
            module Demo

            finite law i32[-2147483648 2147483647] Run(i32[-2147483648 2147483647] left, i32[-2147483648 2147483647] right) {
                stack mut i32[-2147483648 2147483647] value = left;
                value +%= right;
                stack i32[-2147483648 2147483647] product = left *| right;
                return -%value +% product +| 3;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded);
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "STK3008");
    }

    [Fact]
    public void StrictFpModifierTypeChecksNowThatLoweringExists()
    {
        var result = Compile(
            """
            module Demo

            strictfp finite law f32 Run(f32 left, f32 right) {
                return left + right;
            }
            """);

        Assert.True(result.Succeeded);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "STK3008");
    }

    [Fact]
    public void FloatingPointArithmeticChainsTypeCheckAcrossMixedNumericOperands()
    {
        var result = Compile(
            """
            module Demo

            strictfp finite law f64 Run(f32 left, i32[-2147483648 2147483647] middle, f64 right, f32 divisor) {
                return left + middle * right / divisor - 1.0;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
    }

    [Fact]
    public void ExplicitConversionsPointerOperatorsAndSliceViewsTypeCheck()
    {
        var result = Compile(
            """
            module Demo

            unsafe finite law i32[-2147483648 2147483647] Run(i64[-9223372036854775808 9223372036854775807] bits, ascii text) {
                stack mut i32[-2147483648 2147483647] value = 7;
                stack rawmutptr<i32[-2147483648 2147483647]> ptr = &value;
                stack rawptr<i32[-2147483648 2147483647]> readonlyPtr = (rawptr<i32[-2147483648 2147483647]>)ptr;
                *ptr = (i32[-2147483648 2147483647])bits;
                stack i64[-9223372036854775808 9223372036854775807] address = (i64[-9223372036854775808 9223372036854775807])ptr;
                stack rawmutptr<i32[-2147483648 2147483647]> roundTrip = (rawmutptr<i32[-2147483648 2147483647]>)address;
                stack i32[-2147483648 2147483647][2] values = { 1, 2 };
                stack i32[-2147483648 2147483647][] view = (i32[-2147483648 2147483647][])values;
                return *roundTrip + view[0];
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded);
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
    }

    [Fact]
    public void TextEscapeLiteralsPreferUtf8BackedAsciiUnlessExplicitlyConverted()
    {
        var result = Compile(
            """
            module Demo

            finite law ascii AsciiString() {
                return "\0\b\t\n\f\r\\\"\'";
            }

            finite law ascii AsciiChar() {
                return '\x41';
            }

            finite law unicode UnicodeString() {
                return (unicode)"\xC9";
            }

            finite law unicode UnicodeChar() {
                return (unicode)'\u03B1';
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded);
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
    }

    [Fact]
    public void ConstGlobalAggregateProjectionsCanBindToFrozenParameters()
    {
        var result = Compile(
            """
            module Demo

            struct Box {
                i32[-2147483648 2147483647] Value;
            }

            struct Holder {
                Box Item;
            }

            const Holder Current = new Holder() { Item = new Box() { Value = 7 } };

            fn i32[-2147483648 2147483647] Read(frozen Box box) {
                return 7;
            }

            fn i32[-2147483648 2147483647] Run() {
                return Read(Current.Item);
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }

    [Fact]
    public void ExplicitLiteralTextConversionsTypeCheck()
    {
        var result = Compile(
            """
            module Demo

            finite law unicode Widen() {
                return (unicode)"Hello";
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded);
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
    }

    [Fact]
    public void CompileTimeTextConcatenationTypeChecksAsTextLiteral()
    {
        var result = Compile(
            """
            module Demo

            finite law ascii Label() {
                return "Score: " + "100";
            }

            finite law unicode WideLabel() {
                return "Score: " + "100";
            }

            finite law unicode ExplicitWideLabel() {
                return (unicode)"Score: " + (unicode)"100";
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
    }

    [Fact]
    public void CompileTimeInterpolatedTextTypeChecksAsTextLiteral()
    {
        var result = Compile(
            """
            module Demo

            finite law ascii Label() {
                const score = 100;
                return $"Score: {score}, ready: {true}";
            }

            finite law unicode WideLabel() {
                return $"Score: {100}";
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
    }

    [Fact]
    public void FixedCapacityRuntimeInterpolatedTextTypeChecksWithKnownFormatter()
    {
        var result = Compile(
            """
            module System.Text

            public finite law ascii AsciiView(Ascii source);
            public unsafe fn bool TryConcatAscii(rawmutptr<Ascii> destination, ascii left, ascii right);
            public unsafe fn bool TryFormatI32Ascii(rawmutptr<Ascii> destination, i32[-2147483648 2147483647] value);

            fn Ascii Label(i32[-2147483648 2147483647] score) {
                stack Ascii label[64] = $"Score: {score}";
                return label;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }

    [Fact]
    public void FixedCapacityTextConcatenationTypeChecksForAsciiAndUnicodeBuffers()
    {
        var result = Compile(
            """
            module System.Text

            public finite law ascii AsciiView(Ascii source);
            public finite law unicode UnicodeView(Unicode source);
            public unsafe fn bool TryConcatAscii(rawmutptr<Ascii> destination, ascii left, ascii right);
            public unsafe fn bool TryConcatUnicode(rawmutptr<Unicode> destination, unicode left, unicode right);

            fn Ascii JoinAscii(Ascii left, ascii right) {
                stack Ascii combined[64] = left + right;
                return combined;
            }

            fn Unicode JoinUnicode(Unicode left, unicode right) {
                stack Unicode combined[64] = left + right;
                return combined;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }

    [Fact]
    public void EmptyTextSlicesTypeCheckAsSameTextKind()
    {
        var result = Compile(
            """
            module Demo

            fn ascii SliceAscii(ascii text) {
                return text[];
            }

            fn unicode SliceUnicode(unicode text) {
                return text[];
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
    }

    [Fact]
    public void FixedArrayLengthsAcceptConstantArithmeticExpressions()
    {
        var result = Compile(
            """
            module Demo

            fn i32[-2147483648 2147483647] Run(i32[-2147483648 2147483647][1 + 2] values) {
                return values[2];
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded);
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
    }

    [Fact]
    public void FixedArrayInitializersCanOmitTrailingElements()
    {
        var result = Compile(
            """
            module Demo

            fn i32[-2147483648 2147483647] Run() {
                stack i32[-2147483648 2147483647][3] values = { 1, 2 };
                return values[0] + values[1] + values[2];
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
    }

    [Fact]
    public void ScalarizableNamedAggregatesAreOrderedComparable()
    {
        var result = Compile(
            """
            module Demo

            record Many(i32[-2147483648 2147483647] A, i32[-2147483648 2147483647] B, i32[-2147483648 2147483647] C, i32[-2147483648 2147483647] D, i32[-2147483648 2147483647] E) { }

            fn bool Less(Many left, Many right) {
                return left < right;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
    }

    [Fact]
    public void ScalarizableEnumsAreOrderedComparable()
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
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
    }

    [Fact]
    public void ExplicitNonAsciiLiteralToAsciiConversionTypeChecks()
    {
        var result = Compile(
            """
            module Demo

            finite law ascii Run() {
                return (ascii)"\u03B1";
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded);
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
    }

    [Fact]
    public void FrozenReachableViewsTypeCheckAsReadonlyAliases()
    {
        var result = Compile(
            """
            module Demo

            struct Box {
                i32[-2147483648 2147483647] Value;
            }

            struct PtrBox {
                rawmutptr<i32[-2147483648 2147483647]> Ptr;
            }

            unsafe finite law void Run(frozen Box box, frozen PtrBox ptrBox) {
                stack rawptr<frozen i32[-2147483648 2147483647]> valuePtr = &box.Value;
                stack rawptr<frozen i32[-2147483648 2147483647]> readonlyPtr = ptrBox.Ptr;
                stack bool same = *valuePtr == *readonlyPtr;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded);
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
    }

    [Fact]
    public void ConstParameterProvenanceTypeChecksAsReadonlyAliases()
    {
        var result = Compile(
            """
            module Demo

            struct PtrBox {
                rawmutptr<i32[-2147483648 2147483647]> Ptr;
            }

            unsafe fn void Inspect(const PtrBox box, const rawmutptr<i32[-2147483648 2147483647]> ptr) {
                stack rawptr<frozen i32[-2147483648 2147483647]> fieldPtr = box.Ptr;
                stack rawptr<frozen i32[-2147483648 2147483647]> directPtr = ptr;
                stack i32[-2147483648 2147483647] value = *ptr;
                stack bool same = *fieldPtr == *directPtr;
                return;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
    }

    [Fact]
    public void ConstParametersCanBeForwardedToConstParameters()
    {
        var result = Compile(
            """
            module Demo

            struct Box {
                i32[-2147483648 2147483647] Value;
            }

            fn void Inspect(const Box box) {
                return;
            }

            fn void Forward(const Box box) {
                Inspect(box);
                return;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }

    [Fact]
    public void ConstRawPointerProvenanceFlowsThroughImmutableLocal()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn void Inspect(const rawmutptr<i32[-2147483648 2147483647]> ptr) {
                return;
            }

            unsafe fn void Forward(const rawmutptr<i32[-2147483648 2147483647]> ptr) {
                stack rawptr<frozen i32[-2147483648 2147483647]> local = ptr;
                Inspect(local);
                return;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }

    [Fact]
    public void ConstRawSliceProvenanceFlowsThroughImmutableLocal()
    {
        var result = Compile(
            """
            module Demo

            fn void Inspect(const i32[-2147483648 2147483647][] view) {
                return;
            }

            unsafe fn void Forward(
                const rawmutptr<i32[-2147483648 2147483647]>[count] pointer,
                i32[1 10] count) {
                unsafe {
                    stack frozen i32[-2147483648 2147483647][] view = slice(pointer, count);
                    Inspect(view);
                }

                return;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }

    [Fact]
    public void AggregateSwitchPatternsTypeCheckOnScalarFields()
    {
        var result = Compile(
            """
            module Demo

            record Pair(i32[-2147483648 2147483647] Left, i32[-2147483648 2147483647] Right) { }

            finite law i32[-2147483648 2147483647] Run(Pair value) {
                switch (value) {
                    case Pair(1, var right):
                        return right;
                    case Pair(_, _):
                        return 0;
                }
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded);
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
    }

    [Fact]
    public void NamedAggregateWholeValueSwitchCapturesTypeCheck()
    {
        var result = Compile(
            """
            module Demo

            record Pair(i32[-2147483648 2147483647] Left, i32[-2147483648 2147483647] Right) { }

            finite law i32[-2147483648 2147483647] Run(Pair value) {
                switch (value) {
                    case var whole:
                        return whole.Left;
                }
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded);
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
    }

    [Fact]
    public void NestedAggregateWholeValueSwitchCapturesTypeCheck()
    {
        var result = Compile(
            """
            module Demo

            record Pair(i32[-2147483648 2147483647] Left, i32[-2147483648 2147483647] Right) { }
            record Outer(Pair Values, i32[-2147483648 2147483647] Tail) { }

            finite law i32[-2147483648 2147483647] Run(Outer value) {
                switch (value) {
                    case Outer(Pair capture, var tail):
                        return capture.Right + tail;
                }
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded);
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
    }

    [Fact]
    public void EnumWholeValueSwitchCapturesTypeCheck()
    {
        var result = Compile(
            """
            module Demo

            enum Token {
                Empty,
                Pair(i32[-2147483648 2147483647], i32[-2147483648 2147483647]),
            }

            finite law i32[-2147483648 2147483647] Run(Token value) {
                switch (value) {
                    case Token.Pair capture:
                        switch (capture) {
                            case Token.Pair(var left, var right):
                                return left + right;
                            default:
                                return 0;
                        }
                    default:
                        return -1;
                }
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded);
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
    }

    [Fact]
    public void GuardedSwitchLabelsDoNotContributeToReachabilityCoverage()
    {
        var result = Compile(
            """
            module Demo

            finite law i32[-2147483648 2147483647] Run(bool value, bool allow) {
                switch (value) {
                    case true when allow:
                        return 1;
                    case true:
                        return 2;
                    default:
                        return 0;
                }
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "STK3019");
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
    }

    [Fact]
    public void NestedAggregateSwitchPatternsTypeCheckOnScalarLeaves()
    {
        var result = Compile(
            """
            module Demo

            record Pair(i32[-2147483648 2147483647] Left, i32[-2147483648 2147483647] Right) { }
            record Outer(Pair Values, i32[-2147483648 2147483647] Tail) { }

            finite law i32[-2147483648 2147483647] Run(Outer value) {
                switch (value) {
                    case Outer(Pair(1, var right), var tail):
                        return right + tail;
                    default:
                        return 0;
                }
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded);
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
    }

    [Fact]
    public void EnumSwitchPatternsTypeCheckOnCasePayloadCaptures()
    {
        var result = Compile(
            """
            module Demo

            enum Token {
                End,
                Integer(i32[-2147483648 2147483647]),
                Move { X: i32[-2147483648 2147483647], Y: i32[-2147483648 2147483647] },
            }

            finite law i32[-2147483648 2147483647] Run(Token token) {
                switch (token) {
                    case Token.End:
                        return 0;
                    case Token.Integer(var value):
                        return value;
                    case Token.Move { X: var x, Y: var y }:
                        return x + y;
                }
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded);
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
    }

    // ---- Generic type instantiation ----

    [Fact]
    public void GenericEnumInstantiationTypeChecks()
    {
        var result = Compile(
            """
            module Demo

            enum Option<T> {
                None,
                Some(T),
            }

            finite law bool HasValue(Option<i32[-2147483648 2147483647]> opt) {
                switch (opt) {
                    case Option<i32[-2147483648 2147483647]>.None:
                        return false;
                    case Option<i32[-2147483648 2147483647]>.Some(var value):
                        return value > 0;
                }
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static d => d.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
        Assert.True(typeCheckModel.NamedTypes.ContainsKey("Option"), "generic template should be registered");
        Assert.True(typeCheckModel.NamedTypes.ContainsKey("Option<i32>"), "monomorphized type should be registered");
        var monomorphized = typeCheckModel.NamedTypes["Option<i32>"];
        Assert.Equal(DeclarationKind.Enum, monomorphized.Kind);
        Assert.Equal(2, monomorphized.Variants.Count);
        Assert.True(typeCheckModel.NamedTypes["Option"].IsGeneric, "template should be marked generic");
    }

    [Fact]
    public void GenericRecordInstantiationTypeChecks()
    {
        var result = Compile(
            """
            module Demo

            record Pair<A, B>(A First, B Second) { }

            finite law i32[-2147483648 2147483647] Sum(Pair<i32[-2147483648 2147483647], i32[-2147483648 2147483647]> pair) {
                return pair.First + pair.Second;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static d => d.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
        Assert.True(typeCheckModel.NamedTypes.ContainsKey("Pair<i32,i32>"), "monomorphized pair should be registered");
        var concrete = typeCheckModel.NamedTypes["Pair<i32,i32>"];
        Assert.Equal(2, concrete.OrderedFields.Count);
        Assert.Equal(StarkTypeKind.Integer, concrete.OrderedFields[0].Type.Kind);
        Assert.Equal(StarkTypeKind.Integer, concrete.OrderedFields[1].Type.Kind);
    }

    [Fact]
    public void GenericRecordPrimaryConstructorInstantiationTypeChecks()
    {
        var result = Compile(
            """
            module Demo

            record Pair<A, B>(A First, B Second) { }

            finite law i32[-2147483648 2147483647] Sum() {
                stack Pair<i32[-2147483648 2147483647], i32[-2147483648 2147483647]> pair = new Pair<i32[-2147483648 2147483647], i32[-2147483648 2147483647]>(3, 4);
                return pair.First + pair.Second;
            }
            """);

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static d => d.ToString())));
    }

    [Fact]
    public void GenericTypeUsesRecordConcreteInstantiationTriggers()
    {
        var result = Compile(
            """
            module Demo

            record Pair<A, B>(A First, B Second) { }

            fn bool Accept(Pair<i32[-2147483648 2147483647], bool> pair) {
                return pair.Second;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static d => d.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);

        var trigger = Assert.Single(typeCheckModel.TypeTriggers);
        Assert.Equal("Pair<i32,bool>", trigger.TypeName);
        Assert.Equal(["i32", "bool"], trigger.TypeArguments.Select(static type => type.DisplayName));
    }

    [Fact]
    public void NestedGenericTypesInsideContainersMonomorphizeAndRecordTriggers()
    {
        var result = Compile(
            """
            module Demo

            record Pair<A, B>(A First, B Second) { }

            unsafe fn i32[-2147483648 2147483647] Read(rawptr<Pair<i32[-2147483648 2147483647], bool>> ptr) {
                if ((*ptr).Second) {
                    return (*ptr).First;
                }

                return 0;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static d => d.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
        Assert.True(typeCheckModel.NamedTypes.ContainsKey("Pair<i32,bool>"));
        Assert.Contains(typeCheckModel.TypeTriggers, static trigger => trigger.TypeName == "Pair<i32,bool>");
    }

    [Fact]
    public void GenericFunctionBodiesCanUseTheirTypeParametersInLocalTypes()
    {
        var result = Compile(
            """
            module Demo

            fn T Identity<T>(T value) {
                stack T copy = value;
                return copy;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static d => d.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
        Assert.True(typeCheckModel.Functions.TryGetValue("Identity", out var signature));
        Assert.True(signature.IsGeneric);
        Assert.Equal(["T"], signature.GenericParams);
        Assert.Equal("T", signature.ReturnType.DisplayName);
        Assert.Equal("T", Assert.Single(signature.Parameters).Type.DisplayName);
    }

    [Fact]
    public void GenericMethodBodiesCanUseTheirTypeParametersInLocalTypes()
    {
        var result = Compile(
            """
            module Demo

            struct Box {
                fn T Echo<T>(T value) {
                    stack T copy = value;
                    return copy;
                }
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static d => d.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
        Assert.True(typeCheckModel.Functions.TryGetValue("Box.Echo", out var signature));
        Assert.True(signature.IsGeneric);
        Assert.Equal(["T"], signature.GenericParams);
        Assert.Equal("T", signature.ReturnType.DisplayName);
        Assert.Equal("T", Assert.Single(signature.Parameters).Type.DisplayName);
    }

    [Fact]
    public void GenericFunctionCallsRecordConcreteInstantiationTriggers()
    {
        var result = Compile(
            """
            module Demo

            fn T Identity<T>(T value) {
                return value;
            }

            fn i32[-2147483648 2147483647] Run() {
                stack i32[-2147483648 2147483647] value = 42;
                return Identity(value);
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static d => d.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);

        var trigger = Assert.Single(typeCheckModel.InstantiationTriggers);
        Assert.Equal("Identity", trigger.FunctionName);
        Assert.Equal(["i32"], trigger.TypeArguments.Select(static type => type.DisplayName));
        Assert.True(trigger.Signature.IsGenericInstantiation);
        Assert.Equal("i32", trigger.Signature.ReturnType.DisplayName);
        Assert.Equal("i32", Assert.Single(trigger.Signature.Parameters).Type.DisplayName);
    }

    [Fact]
    public void GenericMethodCallsRecordConcreteInstantiationTriggers()
    {
        var result = Compile(
            """
            module Demo

            struct Box {
                fn T Echo<T>(borrow Box self, T value) {
                    return value;
                }
            }

            fn i32[-2147483648 2147483647] Run() {
                stack Box box = new Box();
                stack i32[-2147483648 2147483647] value = 42;
                return box.Echo(value);
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static d => d.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);

        var trigger = Assert.Single(typeCheckModel.InstantiationTriggers);
        Assert.Equal("Box.Echo", trigger.FunctionName);
        Assert.Equal(["i32"], trigger.TypeArguments.Select(static type => type.DisplayName));
        Assert.True(trigger.Signature.IsGenericInstantiation);
        Assert.Equal("i32", trigger.Signature.ReturnType.DisplayName);
        Assert.Equal(2, trigger.Signature.Parameters.Count);
        Assert.Equal("borrow Box", trigger.Signature.Parameters[0].Type.DisplayName);
        Assert.Equal("i32", trigger.Signature.Parameters[1].Type.DisplayName);
    }

    [Fact]
    public void GenericMethodsOnGenericTypesRecordConcreteInstantiationTriggers()
    {
        var result = Compile(
            """
            module Demo

            struct Box<T> {
                T Value;

                fn T Echo(borrow Box<T> self, T value) {
                    return value;
                }
            }

            fn i32[-2147483648 2147483647] Run() {
                stack Box<i32[-2147483648 2147483647]> box = new Box<i32[-2147483648 2147483647]>() { Value = 1 };
                stack i32[-2147483648 2147483647] value = 42;
                return box.Echo(value);
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static d => d.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);

        var trigger = Assert.Single(typeCheckModel.InstantiationTriggers);
        Assert.Equal("Box.Echo", trigger.FunctionName);
        Assert.Equal(["i32"], trigger.TypeArguments.Select(static type => type.DisplayName));
        Assert.True(trigger.Signature.IsGenericInstantiation);
        Assert.Equal("i32", trigger.Signature.ReturnType.DisplayName);
        Assert.Equal(2, trigger.Signature.Parameters.Count);
        Assert.Equal("borrow Box<i32>", trigger.Signature.Parameters[0].Type.DisplayName);
        Assert.Equal("i32", trigger.Signature.Parameters[1].Type.DisplayName);
    }

    [Fact]
    public void GenericNestedMemberOutCallsInferStorageType()
    {
        var result = Compile(
            """
            module Demo

            struct Cell<T> {
                T Value;

                fn bool TryTake(mut borrow Cell<T> self, out T value) {
                    value = self.Value;
                    return true;
                }
            }

            struct Owner<T> {
                Cell<T> Inner;

                fn bool TryTake(mut borrow Owner<T> self, out T value) {
                    return self.Inner.TryTake(value);
                }
            }

            fn bool Run(mut borrow Owner<i32[0 max]> owner, out i32[0 max] value) {
                return owner.TryTake(value);
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static d => d.ToString())));
    }

    [Fact]
    public void RepeatedGenericFunctionCallsReuseOneCachedInstantiationTrigger()
    {
        var result = Compile(
            """
            module Demo

            fn T Identity<T>(T value) {
                return value;
            }

            fn i32[-2147483648 2147483647] Run(i32[-2147483648 2147483647] left, i32[-2147483648 2147483647] right) {
                return Identity(left) + Identity(right);
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static d => d.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);

        var trigger = Assert.Single(typeCheckModel.InstantiationTriggers);
        Assert.Equal("Identity", trigger.FunctionName);
        Assert.Equal(["i32"], trigger.TypeArguments.Select(static type => type.DisplayName));
    }

    [Fact]
    public void RepeatedGenericTypeUsesReuseOneCachedInstantiationTrigger()
    {
        var result = Compile(
            """
            module Demo

            record Pair<T>(T Value) { }

            fn i32[-2147483648 2147483647] Add(Pair<i32[-2147483648 2147483647]> left, Pair<i32[-2147483648 2147483647]> right) {
                return left.Value + right.Value;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static d => d.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);

        var trigger = Assert.Single(typeCheckModel.TypeTriggers);
        Assert.Equal("Pair<i32>", trigger.TypeName);
        Assert.Equal(["i32"], trigger.TypeArguments.Select(static type => type.DisplayName));
    }

    [Fact]
    public void ConcreteOverloadsBeatMatchingGenericInstantiationTriggers()
    {
        var result = Compile(
            """
            module Demo

            fn i32[-2147483648 2147483647] Parse(i32[-2147483648 2147483647] value) {
                return value;
            }

            fn T Parse<T>(T value) {
                return value;
            }

            fn i32[-2147483648 2147483647] Run() {
                stack i32[-2147483648 2147483647] value = 42;
                return Parse(value);
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static d => d.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
        Assert.Empty(typeCheckModel.InstantiationTriggers);
    }

    [Fact]
    public void TypeAliasesResolveToTheirUnderlyingTypes()
    {
        var result = Compile(
            """
            module Demo

            alias Byte = i8[-128 127];

            fn Byte Inc(Byte value) {
                return value + 1;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static d => d.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
        Assert.True(typeCheckModel.TypeAliases.ContainsKey("Byte"));
        Assert.True(typeCheckModel.Functions.TryGetValue("Inc", out var signature));
        Assert.Equal("i8", signature.ReturnType.DisplayName);
        Assert.Equal("i8", Assert.Single(signature.Parameters).Type.DisplayName);
    }

    [Fact]
    public void GenericTypeAliasesSubstituteIntoTheirUnderlyingTypes()
    {
        var result = Compile(
            """
            module Demo

            alias Ptr<T> = rawptr<T>;

            unsafe fn i32[-2147483648 2147483647] Read(Ptr<i32[-2147483648 2147483647]> value) {
                return *value;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static d => d.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
        Assert.True(typeCheckModel.TypeAliases.ContainsKey("Ptr"));
        var parameter = Assert.Single(typeCheckModel.Functions["Read"].Parameters).Type;
        Assert.Equal(StarkTypeKind.RawPointer, parameter.Kind);
        Assert.NotNull(parameter.ElementType);
        Assert.Equal("i32", parameter.ElementType!.DisplayName);
    }

    [Fact]
    public void GenericTypeWithWrongArgCountIsAnError()
    {
        var result = Compile(
            """
            module Demo

            enum Option<T> {
                None,
                Some(T),
            }

            finite law void Bad(Option<i32[-2147483648 2147483647], bool> opt) {
                return;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, static d => d.Code == "STK3019");
    }

    [Fact]
    public void GenericEnumVariantFieldTypeIsSubstituted()
    {
        var result = Compile(
            """
            module Demo

            enum Result<T, E> {
                Ok(T),
                Err(E),
            }

            finite law i32[-2147483648 2147483647] Unwrap(Result<i32[-2147483648 2147483647], bool> res) {
                switch (res) {
                    case Result<i32[-2147483648 2147483647], bool>.Ok(var value):
                        return value;
                    case Result<i32[-2147483648 2147483647], bool>.Err(var err):
                        if (err) {
                            return -1;
                        }
                        return -1;
                }
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static d => d.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
        Assert.True(typeCheckModel.NamedTypes.ContainsKey("Result<i32,bool>"));
        var concrete = typeCheckModel.NamedTypes["Result<i32,bool>"];
        var okVariant = concrete.Variants.Single(static v => v.Name == "Ok");
        Assert.Equal(StarkTypeKind.Integer, okVariant.Fields[0].Type.Kind);
        var errVariant = concrete.Variants.Single(static v => v.Name == "Err");
        Assert.Equal(StarkTypeKind.Bool, errVariant.Fields[0].Type.Kind);
    }

    [Fact]
    public void NonGenericTypeWithTypeArgumentsIsAnError()
    {
        var result = Compile(
            """
            module Demo

            record Point(i32[-2147483648 2147483647] X, i32[-2147483648 2147483647] Y) { }

            finite law void Bad(Point<i32[-2147483648 2147483647]> p) {
                return;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, static d => d.Code == "STK3019");
    }

    [Fact]
    public void TopLevelOverloadGroupsRegisterDistinctFunctionsAndResolveCalls()
    {
        var result = Compile(
            """
            module Demo

            fn i32[-2147483648 2147483647] Parse(i32[-2147483648 2147483647] value) {
                return value;
            }

            fn bool Parse(bool value) {
                return value;
            }

            fn i32[-2147483648 2147483647] Run() {
                return Parse(42);
            }

            fn bool RunBool() {
                return Parse(true);
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static d => d.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
        Assert.True(typeCheckModel.Overloads.TryGetValue("Parse", out var overloads));
        Assert.Equal(2, overloads.Count);
        Assert.Equal(
            2,
            typeCheckModel.Functions.Keys.Count(static name => name.StartsWith("Parse#(", StringComparison.Ordinal)));
    }

    [Fact]
    public void NumericOverloadResolutionPrefersExactAndNarrowSafeMatches()
    {
        var result = Compile(
            """
            module Demo

            fn bool PickFloat(f64 value) {
                return true;
            }

            fn i32[-2147483648 2147483647] PickFloat(f32 value) {
                return 1;
            }

            fn bool PickInteger(u32[0 max] value) {
                return true;
            }

            fn i32[-2147483648 2147483647] PickInteger(i48[-140737488355328 140737488355327] value) {
                return 2;
            }

            fn i32[-2147483648 2147483647] PickInteger(i64[-9223372036854775808 9223372036854775807] value) {
                return 3;
            }

            fn i32[-2147483648 2147483647] PickInteger(f64 value) {
                return 4;
            }

            fn bool RunFloat() {
                stack f64 value = 3.5;
                return PickFloat(value);
            }

            fn bool RunInteger() {
                stack u32[0 max] value = (u32[0 max])42;
                return PickInteger(value);
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static d => d.ToString())));
    }

    [Fact]
    public void MethodOverloadGroupsRegisterDistinctFunctionsAndResolveCalls()
    {
        var result = Compile(
            """
            module Demo

            struct Counter {
                i32[-2147483648 2147483647] Value;

                fn i32[-2147483648 2147483647] Scale(borrow Counter self, i32[-2147483648 2147483647] factor) {
                    return self.Value * factor;
                }

                fn i32[-2147483648 2147483647] Scale(borrow Counter self, bool doubleIt) {
                    if (doubleIt) {
                        return self.Value * 2;
                    }

                    return self.Value;
                }
            }

            fn i32[-2147483648 2147483647] Run() {
                stack Counter counter = new Counter() { Value = 3 };
                return counter.Scale(4);
            }

            fn i32[-2147483648 2147483647] RunBool() {
                stack Counter counter = new Counter() { Value = 3 };
                return counter.Scale(true);
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static d => d.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
        Assert.True(typeCheckModel.Overloads.TryGetValue("Counter.Scale", out var overloads));
        Assert.Equal(2, overloads.Count);
        Assert.Equal(
            2,
            typeCheckModel.Functions.Keys.Count(static name => name.StartsWith("Counter.Scale#(", StringComparison.Ordinal)));
    }

    [Fact]
    public void TargetTypedObjectCreationResolvesFromDestinationType()
    {
        var result = Compile(
            """
            module Demo

            struct Box {
                i32[min max] Value;

                Box() {
                    self.Value = 0;
                }

                Box(i32[min max] value) {
                    self.Value = value;
                }
            }

            fn Box Make(i32[min max] value) {
                return new(value);
            }

            fn i32[min max] Run(i32[min max] value) {
                stack Box empty = new();
                stack Box initialized = new() { Value = value };
                stack mut Box assigned = new(value);
                assigned = new(value);
                return assigned.Value + empty.Value + initialized.Value;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static d => d.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);

        var objectCreations = typeCheckModel.ObjectCreations.ToArray();
        Assert.Equal(5, objectCreations.Length);
        Assert.All(objectCreations, static objectCreation => Assert.Equal("Box", objectCreation.CreatedType.DisplayName));
        Assert.Equal(5, objectCreations.Count(static objectCreation => objectCreation.Constructor is not null));
        Assert.Single(objectCreations, static objectCreation => objectCreation.Members.Count == 1);
    }

    [Fact]
    public void TargetTypedObjectCreationResolvesAllocatorTakingConstructorOverload()
    {
        var result = Compile(
            """
            module Demo

            struct Allocator {
                i32[0 255] Tag;
            }

            struct List {
                i32[0 max] Capacity;

                List() {
                    self.Capacity = 0;
                }

                List(Allocator allocator) {
                    self.Capacity = allocator.Tag;
                }
            }

            fn List MakeDefault() {
                return new();
            }

            fn List MakeCustom(Allocator allocator) {
                return new(allocator);
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static d => d.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);

        var constructors = typeCheckModel.ObjectCreations
            .Where(static objectCreation => objectCreation.CreatedType.DisplayName == "List")
            .Select(static objectCreation => objectCreation.Constructor)
            .ToArray();

        Assert.Contains(constructors, static constructor => constructor is { Parameters.Count: 0 });
        Assert.Contains(constructors, static constructor => constructor is { Parameters.Count: 1 }
            && constructor.Parameters[0].Type.DisplayName == "Allocator");
    }

    [Fact]
    public void TargetTypedObjectCreationRequiresNamedDestinationType()
    {
        var result = Compile(
            """
            module Demo

            fn void MissingTarget() {
                new();
            }

            fn void NonNamedTarget() {
                stack i32[min max] value = new();
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3002"
                && diagnostic.Message.Contains("requires an expected named target type", StringComparison.Ordinal));
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3002"
                && diagnostic.Message.Contains("requires a named target type", StringComparison.Ordinal)
                && diagnostic.Message.Contains("i32", StringComparison.Ordinal));
    }

    [Fact]
    public void DynamicStorageCreationCapacityAndInitIndexTypeCheck()
    {
        var result = Compile(
            """
            module Demo

            fn i64[0 max] Run() {
                stack mut dynamic i32[0 max] values = new(4);
                values.Reserve(8);
                init values[0] = 7;
                return values.Length + values.Capacity;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }

    [Fact]
    public void DynamicStorageTryReserveReturnsBool()
    {
        var result = Compile(
            """
            module Demo

            fn bool Run() {
                stack mut dynamic i32[0 max] values = new(4);
                stack bool grew = values.TryReserve(8);
                return grew;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }

    [Fact]
    public void DynamicStorageTryReserveCapacityReturnsBool()
    {
        var result = Compile(
            """
            module Demo

            fn bool Run() {
                stack mut dynamic i32[0 max] values = new(4);
                stack bool grew = values.TryReserveCapacity(8);
                return grew;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }

    [Fact]
    public void DynamicStorageReserveRequiresMutableOwnerAndNonNegativeAdditionalCapacity()
    {
        var result = Compile(
            """
            module Demo

            fn void ImmutableOwner() {
                stack dynamic i32[0 max] values = new(4);
                values.Reserve(8);
            }

            fn void NegativeAdditionalCapacity() {
                stack mut dynamic i32[0 max] values = new(4);
                values.Reserve(-1);
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3007"
                && diagnostic.Message.Contains("Cannot assign to immutable local 'values'", StringComparison.Ordinal));
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3002"
                && diagnostic.Message.Contains("must be provably non-negative", StringComparison.Ordinal));
    }

    [Fact]
    public void DynamicStorageMoveLastReturnsElementType()
    {
        var result = Compile(
            """
            module Demo

            fn i32[0 max] Run() {
                stack mut dynamic i32[0 max] values = new(1);
                init values[0] = 42;
                stack i32[0 max] moved = values.MoveLast();
                return moved;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }

    [Fact]
    public void DynamicStorageMoveAtReturnsElementType()
    {
        var result = Compile(
            """
            module Demo

            fn i32[0 max] Run() {
                stack mut dynamic i32[0 max] values = new(2);
                init values[0] = 10;
                init values[1] = 20;
                stack i32[0 max] moved = values.MoveAt(0);
                return moved + values[0];
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }

    [Fact]
    public void DynamicStorageMoveLastRequiresMutableOwnerAndNoArguments()
    {
        var result = Compile(
            """
            module Demo

            fn void ImmutableOwner() {
                stack dynamic i32[0 max] values = new(1);
                values.MoveLast();
            }

            fn void ExtraArgument() {
                stack mut dynamic i32[0 max] values = new(1);
                values.MoveLast(1);
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3007"
                && diagnostic.Message.Contains("Cannot assign to immutable local 'values'", StringComparison.Ordinal));
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3009"
                && diagnostic.Message.Contains("MoveLast expects no arguments", StringComparison.Ordinal));
    }

    [Fact]
    public void DynamicStorageSpareRangeCanBindInitSliceView()
    {
        var result = Compile(
            """
            module Demo

            fn void Run() {
                stack mut dynamic i32[0 max] values = new(4);
                stack init i32[0 max][] spare = init values[0, 4];
                init spare[0] = 1;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }

    [Fact]
    public void InitSliceElementsAreWriteOnlyUntilInitialized()
    {
        var good = Compile(
            """
            module Demo

            fn void Fill(init i32[0 max][] destination, i32[0 max] value) {
                init destination[0] = value;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(good.Succeeded, string.Join(", ", good.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

        var bad = Compile(
            """
            module Demo

            fn i32[0 max] Read(init i32[0 max][] destination) {
                return destination[0];
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(bad.Succeeded);
        Assert.Contains(
            bad.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3002"
                && diagnostic.Message.Contains("found 'init i32", StringComparison.Ordinal));
    }

    [Fact]
    public void TextLiteralsHaveConstProvenanceForConstParameters()
    {
        var result = Compile(
            """
            module Demo

            fn bool AcceptAscii(const ascii value) {
                return true;
            }

            fn bool AcceptUnicode(const unicode value) {
                return true;
            }

            fn bool Run() {
                return AcceptAscii("alpha")
                    && AcceptAscii("al" + "pha")
                    && AcceptAscii((ascii)"alpha")
                    && AcceptUnicode((unicode)"alpha");
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }

    private static void AssertIntegerRange(StarkTypeSymbol type, int bitWidth, BigInteger min, BigInteger max, bool isUnsigned = false)
    {
        Assert.Equal(StarkTypeKind.Integer, type.Kind);
        Assert.Equal(bitWidth, type.BitWidth);
        Assert.Equal((BigInteger?)min, type.RangeMin);
        Assert.Equal((BigInteger?)max, type.RangeMax);
        Assert.Equal(isUnsigned, type.IsUnsigned);
    }

    private static void AssertCapture(TypeCheckModel typeCheckModel, string name, string mode, bool isUnsafe)
    {
        var capture = Assert.Single(typeCheckModel.LambdaCaptures, item => item.Name == name);
        Assert.Equal(mode, capture.Mode);
        Assert.Equal(isUnsafe, capture.IsUnsafe);
        Assert.Equal(StarkTypeKind.Integer, capture.Type.Kind);
        Assert.Equal("Run", capture.EnclosingFunctionName);
    }

    private static CompilationResult Compile(string source, CompilerOptions? options = null)
    {
        return DefaultCompilerPipeline.Create().Run(new CompilationInput(source), options);
    }
}
