using System.Reflection;
using Stark.Compiler;

namespace compiler.Tests;

public sealed class SsaEmitterCoverageMatrixTests
{
    [Fact]
    public void EveryConcreteSsaNodeHasEmitterCoverageOrValidationRejection()
    {
        var expected = EnumerateRequiredNodeNames().ToArray();
        var matrix = NodeCoverageRows();
        var covered = matrix.Select(static row => row.NodeName).ToHashSet(StringComparer.Ordinal);
        var missing = expected.Where(nodeName => !covered.Contains(nodeName)).Order(StringComparer.Ordinal).ToArray();
        var stale = covered.Where(nodeName => !expected.Contains(nodeName, StringComparer.Ordinal)).Order(StringComparer.Ordinal).ToArray();

        Assert.True(missing.Length == 0, $"Missing SSA emitter coverage rows: {string.Join(", ", missing)}");
        Assert.True(stale.Length == 0, $"Stale SSA emitter coverage rows: {string.Join(", ", stale)}");
        AssertCoverageRowsPointAtRealTests(matrix);
    }

    [Fact]
    public void PositiveLlvmCoverageMatrixKeepsBackendAssumptionTestsVisible()
    {
        AssertCoverageRowsPointAtRealTests(BackendAssumptionCoverageRows());
    }

    private static IReadOnlyList<CoverageRow> NodeCoverageRows()
    {
        return
        [
            Row(nameof(SsaValueReference), CoverageKind.PositiveLlvmEmission, "LlvmIrEmissionTests.StraightLineFunctionEmitsOptimizedLlvmBody"),
            Row(nameof(SsaIntegerConstant), CoverageKind.PositiveLlvmEmission, "LlvmIrEmissionTests.StraightLineFunctionEmitsOptimizedLlvmBody"),
            Row(nameof(SsaFloatConstant), CoverageKind.PositiveLlvmEmission, "LlvmEmitterConversionTests.FloatUseRValueIsEmittedAsAlias"),
            Row(nameof(SsaStringConstant), CoverageKind.PositiveLlvmEmission, "LlvmIrEmissionTests.TextLiteralSwitchEmitsLengthAndByteComparisons"),
            Row(nameof(SsaTextDataAddressValue), CoverageKind.PositiveLlvmEmission, "LlvmIrEmissionTests.FfiStringCallsExtractPointerFromConcreteStringValues"),
            Row(nameof(SsaBoolConstant), CoverageKind.PositiveLlvmEmission, "LlvmIrEmissionTests.ShortCircuitAndTernaryEmitBranchesAndPhi"),
            Row(nameof(SsaNullConstant), CoverageKind.PositiveLlvmEmission, "LlvmEmitterConversionTests.RawPointerToIntegerConversionEmitsPtrtoint"),
            Row(nameof(SsaGlobalAddressValue), CoverageKind.PositiveLlvmEmission, "LlvmIrEmissionTests.ImmutableGlobalAddressesLowerWithoutPointerCasts"),
            Row(nameof(SsaFunctionAddressValue), CoverageKind.PositiveLlvmEmission, "LlvmIrEmissionTests.FunctionPointerCallsEmitFastccIndirectCall"),
            Row(nameof(SsaClosureValue), CoverageKind.PositiveLlvmEmission, "LlvmIrEmissionTests.NonCapturingClosureValuesLowerToInvokeAndEnvironmentPair"),
            Row(nameof(SsaUndefValue), CoverageKind.PositiveLlvmEmission, "LlvmIrEmissionTests.AggregateMoveInvalidatesAddressableSourceStorage"),
            Row(nameof(SsaZeroInitializerValue), CoverageKind.PositiveLlvmEmission, "LlvmIrEmissionTests.PlainObjectCreationWithoutInitializerReturnsZeroInitializedAggregate"),

            Row(nameof(SsaUseRValue), CoverageKind.PositiveLlvmEmission, "LlvmEmitterConversionTests.FloatUseRValueIsEmittedAsAlias", "LlvmEmitterConversionTests.RawPointerUseRValueIsEmittedAsAlias", "LlvmEmitterConversionTests.AggregateUseRValueIsEmittedAsAlias"),
            Row(nameof(SsaUnaryRValue), CoverageKind.PositiveLlvmEmission, "LlvmIrEmissionTests.IntegerAndBoolUnaryOperatorsEmitConcreteLlvmInstructions", "LlvmIrEmissionTests.NonStrictFpFunctionsEmitFastMathFlagsOnRemainingFloatInstructions"),
            Row(nameof(SsaBinaryRValue), CoverageKind.PositiveLlvmEmission, "LlvmIrEmissionTests.OrdinaryIntegerArithmeticEmitsSignedNoWrapFlags", "LlvmIrEmissionTests.BitwiseAndShiftExpressionsEmitConcreteLlvmInstructions", "LlvmIrEmissionTests.NonStrictFpFunctionsEmitFastMathFlagsOnBinaryOpsAndCalls"),
            Row(nameof(SsaSelectRValue), CoverageKind.PositiveLlvmEmission, "LlvmIrEmissionTests.ShortCircuitAndTernaryEmitBranchesAndPhi"),
            Row(nameof(SsaCallRValue), CoverageKind.PositiveLlvmEmission, "LlvmIrEmissionTests.TailPositionDirectCallsEmitTailMarkerWhenAbiCompatible", "LlvmIrEmissionTests.ImportedStarkFunctionUsesQualifiedDependencySymbol", "LlvmIrEmissionTests.ImportedSourceBackedGenericSpecializationsUseLinkOnceOdrComdatDefinitionsAndInlineSmallCalls"),
            Row(nameof(SsaIndirectCallRValue), CoverageKind.PositiveLlvmEmission, "LlvmIrEmissionTests.FunctionPointerCallsEmitFastccIndirectCall", "LlvmIrEmissionTests.FunctionPointerCallsWithLargeByValueParametersUseByvalAbi", "LlvmIrEmissionTests.FunctionPointerCallsWithLargeReturnValuesUseSretAbi"),
            Row(nameof(SsaConvertRValue), CoverageKind.PositiveLlvmEmission, "LlvmEmitterConversionTests.IntegerToRawPointerConversionEmitsInttoptr", "LlvmEmitterConversionTests.RawPointerToRawPointerConversionIsLlvmNoOp", "LlvmIrEmissionTests.StrictFpConversionsUseConstrainedFloatingPointIntrinsics"),
            Row(nameof(SsaExtractFieldRValue), CoverageKind.PositiveLlvmEmission, "LlvmIrEmissionTests.MixedCallMemberAndIndexPostfixChainsEmitCallAndExtracts", "LlvmIrEmissionTests.TextSlicesEmitShiftedAsciiAndUnicodeViews"),
            Row(nameof(SsaInsertFieldRValue), CoverageKind.PositiveLlvmEmission, "LlvmIrEmissionTests.InternalAggregateParameterUsesDirectValueAbi", "LlvmIrEmissionTests.SystemTextOwnedConcatAndViewBuiltinsEmitConcreteDefinitions"),
            Row(nameof(SsaExtractIndexRValue), CoverageKind.PositiveLlvmEmission, "LlvmIrEmissionTests.FixedArrayBackedSliceAndKnownTextSliceCarryProvenGepFlags", "LlvmIrEmissionTests.FixedArrayParameterUsesDirectValueAbi"),
            Row(nameof(SsaInsertIndexRValue), CoverageKind.PositiveLlvmEmission, "LlvmIrEmissionTests.LargeLocalFixedArrayConstantIndexUpdateUsesScalarProjectionAccess", "LlvmIrEmissionTests.LargeEnumPayloadProjectionIntoAddressableLocalUsesAddressCopy"),
            Row(nameof(SsaMakeSliceFromLocalRValue), CoverageKind.PositiveLlvmEmission, "LlvmIrEmissionTests.LocalFixedArrayCanBeCoercedToSliceForCalls", "LlvmIrEmissionTests.FixedArrayBackedSliceAndKnownTextSliceCarryProvenGepFlags"),
            Row(nameof(SsaMakeSliceFromPointerRValue), CoverageKind.PositiveLlvmEmission, "LlvmIrEmissionTests.RawSlicesFromConstPointersPreserveInvariantLoadMetadata", "LlvmIrEmissionTests.RuntimeDisjointSliceConditionUsesViewDataAndLength"),
            Row(nameof(SsaDynamicStorageAllocationRValue), CoverageKind.PositiveLlvmEmission, "LlvmIrEmissionTests.DynamicStorageAllocationIndexInitAndDropEmitRuntimeAllocatorCalls"),
            Row(nameof(SsaDynamicStorageFreeRValue), CoverageKind.PositiveLlvmEmission, "LlvmIrEmissionTests.DynamicStorageAllocationIndexInitAndDropEmitRuntimeAllocatorCalls"),
            Row(nameof(SsaHeapStorageFreeRValue), CoverageKind.PositiveLlvmEmission, "LlvmIrEmissionTests.HeapClosureMoveCaptureDropReleasesOwnedFieldsBeforeEnvironment"),
            Row(nameof(SsaDynamicStorageReserveRValue), CoverageKind.PositiveLlvmEmission, "LlvmIrEmissionTests.DynamicStorageReserveEmitsDirectReallocatePath"),
            Row(nameof(SsaDynamicStorageTryReserveRValue), CoverageKind.PositiveLlvmEmission, "LlvmIrEmissionTests.DynamicStorageTryReserveEmitsDirectFallibleReallocatePath"),
            Row(nameof(SsaDynamicStorageTryReserveCapacityRValue), CoverageKind.PositiveLlvmEmission, "LlvmIrEmissionTests.DynamicStorageTryReserveCapacityEmitsExactFallibleReallocatePath"),
            Row(nameof(SsaDynamicStorageMoveLastRValue), CoverageKind.PositiveLlvmEmission, "LlvmIrEmissionTests.DynamicStorageMoveLastEmitsDirectLengthUpdateAndLoad", "LlvmIrEmissionTests.LargeDynamicStorageMoveLastIntoLocalUsesDestinationSlot"),
            Row(nameof(SsaDynamicStorageMoveAtRValue), CoverageKind.PositiveLlvmEmission, "LlvmIrEmissionTests.DynamicStorageMoveAtEmitsDirectLengthUpdateLoadAndMemmove", "LlvmIrEmissionTests.LargeDynamicStorageMoveAtReturnCopiesBeforeTailShift"),
            Row(nameof(SsaLoadSliceElementRValue), CoverageKind.PositiveLlvmEmission, "LlvmIrEmissionTests.SliceParameterUsesConcreteSliceAbiAndDynamicIndexLoad"),
            Row(nameof(SsaTextSliceRValue), CoverageKind.PositiveLlvmEmission, "LlvmIrEmissionTests.TextSlicesEmitShiftedAsciiAndUnicodeViews"),
            Row(nameof(SsaAddressOfLocalRValue), CoverageKind.PositiveLlvmEmission, "LlvmIrEmissionTests.PointerOperatorsAndExplicitConversionsEmitRawMemoryAccess", "LlvmIrEmissionTests.DynamicArrayIndexEmitsAddressBasedLoad"),
            Row(nameof(SsaAddressOfParameterRValue), CoverageKind.PositiveLlvmEmission, "LlvmIrEmissionTests.FunctionPointerCallsWithBorrowParametersUsePointerAbi", "LlvmIrEmissionTests.IndexedFieldAddressBehindRawPointerEmitsDirectParameterGeps"),
            Row(nameof(SsaFieldAddressRValue), CoverageKind.PositiveLlvmEmission, "LlvmIrEmissionTests.BorrowReceiverIndexedFieldAddressesEmitDirectReceiverGeps", "LlvmIrEmissionTests.MutableBorrowFieldReceiverUsesOriginalFieldAddress"),
            Row(nameof(SsaElementAddressRValue), CoverageKind.PositiveLlvmEmission, "LlvmIrEmissionTests.IndexedRawPointerElementsEmitDirectParameterGeps", "LlvmIrEmissionTests.DynamicArrayIndexEmitsAddressBasedLoad"),
            Row(nameof(SsaSliceElementAddressRValue), CoverageKind.PositiveLlvmEmission, "LlvmIrEmissionTests.SliceMutationEmitsIndirectStoreAndLoad"),
            Row(nameof(SsaLoadIndirectRValue), CoverageKind.PositiveLlvmEmission, "LlvmIrEmissionTests.PointerOperatorsAndExplicitConversionsEmitRawMemoryAccess", "LlvmIrEmissionTests.SliceMutationEmitsIndirectStoreAndLoad"),
            Row(nameof(SsaLoadGlobalRValue), CoverageKind.PositiveLlvmEmission, "LlvmIrEmissionTests.MutableGlobalsEmitRealDefinitionsStoresAndLoads"),
            Row(nameof(SsaLoadLocalRValue), CoverageKind.PositiveLlvmEmission, "LlvmIrEmissionTests.DynamicArrayIndexEmitsAddressBasedLoad"),

            Row(nameof(SsaValueInstruction), CoverageKind.PositiveLlvmEmission, "LlvmIrEmissionTests.StraightLineFunctionEmitsOptimizedLlvmBody"),
            Row(nameof(SsaCallInstruction), CoverageKind.PositiveLlvmEmission, "LlvmIrEmissionTests.DirectVoidStatementCallsWithOutParametersUseCallerStorage", "LlvmIrEmissionTests.SystemTextAsciiToUnicodeLiteralSpecializationRewritesForwardPhiIncomingLabel"),
            Row(nameof(SsaIndirectCallInstruction), CoverageKind.PositiveLlvmEmission, "LlvmIrEmissionTests.FunctionPointerCallsEmitFastccIndirectCall"),
            Row(nameof(SsaAllocateLocalInstruction), CoverageKind.PositiveLlvmEmission, "LlvmIrEmissionTests.DynamicArrayIndexEmitsAddressBasedLoad", "LlvmIrEmissionTests.HeapObjectCreationUsesAllocatorLowering"),
            Row(nameof(SsaLifetimeStartInstruction), CoverageKind.PositiveLlvmEmission, "LlvmIrEmissionTests.DynamicArrayIndexEmitsAddressBasedLoad"),
            Row(nameof(SsaLifetimeEndInstruction), CoverageKind.PositiveLlvmEmission, "LlvmIrEmissionTests.DynamicArrayIndexEmitsAddressBasedLoad"),
            Row(nameof(SsaDeallocateLocalInstruction), CoverageKind.PositiveLlvmEmission, "LlvmIrEmissionTests.HeapObjectCreationUsesAllocatorLowering"),
            Row(nameof(SsaStoreLocalInstruction), CoverageKind.PositiveLlvmEmission, "LlvmIrEmissionTests.PointerOperatorsAndExplicitConversionsEmitRawMemoryAccess", "LlvmIrEmissionTests.DynamicArrayIndexMutationEmitsAddressBasedStoreAndLoad"),
            Row(nameof(SsaStoreIndirectInstruction), CoverageKind.PositiveLlvmEmission, "LlvmIrEmissionTests.PointerOperatorsAndExplicitConversionsEmitRawMemoryAccess", "LlvmIrEmissionTests.SliceMutationEmitsIndirectStoreAndLoad"),
            Row(nameof(SsaCopyMemoryInstruction), CoverageKind.PositiveLlvmEmission, "LlvmIrEmissionTests.LargeAddressableAggregateCopyUsesInlineMemcpy", "LlvmIrEmissionTests.BoundedRawPointerOverlapSafeTemporaryCopyLowersToMemmove"),
            Row(nameof(SsaStoreGlobalInstruction), CoverageKind.PositiveLlvmEmission, "LlvmIrEmissionTests.MutableGlobalsEmitRealDefinitionsStoresAndLoads", "LlvmIrEmissionTests.PointerOperatorsAndExplicitConversionsEmitRawMemoryAccess"),

            Row(nameof(SsaTerminator), CoverageKind.PositiveLlvmEmission, "LlvmIrEmissionTests.StraightLineFunctionEmitsOptimizedLlvmBody"),
            Row(TerminatorName(SsaTerminatorKind.Goto), CoverageKind.PositiveLlvmEmission, "LlvmIrEmissionTests.ShortCircuitAndTernaryEmitBranchesAndPhi"),
            Row(TerminatorName(SsaTerminatorKind.Branch), CoverageKind.PositiveLlvmEmission, "LlvmIrEmissionTests.ShortCircuitAndTernaryEmitBranchesAndPhi"),
            Row(TerminatorName(SsaTerminatorKind.Switch), CoverageKind.PositiveLlvmEmission, "LlvmIrEmissionTests.LargeEnumSwitchLoadsTagWithRangeMetadataAndZeroTagsUnitCase", "LlvmIrEmissionTests.TextLiteralSwitchEmitsLengthAndByteComparisons"),
            Row(TerminatorName(SsaTerminatorKind.Return), CoverageKind.PositiveLlvmEmission, "LlvmIrEmissionTests.StraightLineFunctionEmitsOptimizedLlvmBody"),
            Row(TerminatorName(SsaTerminatorKind.Unreachable), CoverageKind.PositiveLlvmEmission, "LlvmIrEmissionTests.UnrecoverableUnreachablePathsLowerThroughColdTrapHelper")
        ];
    }

    private static IReadOnlyList<CoverageRow> BackendAssumptionCoverageRows()
    {
        return
        [
            Row("optimized raw-pointer loops", CoverageKind.PositiveLlvmEmission, "LlvmIrEmissionTests.IndependentBoundedRawPointerLoopsEmitAccessGroupAndParallelLoopMetadata", "LlvmIrEmissionTests.BoundedRawPointerCopyLoopLowersToMemcpyWhenNoAliasIsProven", "LlvmIrEmissionTests.BoundedRawPointerByteFillLoopLowersToMemset"),
            Row("dynamic storage runtime helpers", CoverageKind.PositiveLlvmEmission, "LlvmIrEmissionTests.DynamicStorageAllocationIndexInitAndDropEmitRuntimeAllocatorCalls", "LlvmIrEmissionTests.DynamicStorageReserveEmitsDirectReallocatePath", "LlvmIrEmissionTests.DynamicStorageTryReserveEmitsDirectFallibleReallocatePath", "LlvmIrEmissionTests.DynamicStorageMoveAtEmitsDirectLengthUpdateLoadAndMemmove"),
            Row("slice and text operations", CoverageKind.PositiveLlvmEmission, "LlvmIrEmissionTests.SliceParameterUsesConcreteSliceAbiAndDynamicIndexLoad", "LlvmIrEmissionTests.SliceMutationEmitsIndirectStoreAndLoad", "LlvmIrEmissionTests.TextSlicesEmitShiftedAsciiAndUnicodeViews", "LlvmIrEmissionTests.SystemTextOwnedConcatAndViewBuiltinsEmitConcreteDefinitions"),
            Row("memory copy move and set", CoverageKind.PositiveLlvmEmission, "LlvmIrEmissionTests.BoundedRawPointerCopyLoopLowersToMemcpyWhenNoAliasIsProven", "LlvmIrEmissionTests.BoundedRawPointerOverlapSafeTemporaryCopyLowersToMemmove", "LlvmIrEmissionTests.BoundedRawPointerByteFillLoopLowersToMemset", "LlvmIrEmissionTests.LargeAddressableAggregateCopyUsesInlineMemcpy"),
            Row("address forms", CoverageKind.PositiveLlvmEmission, "LlvmIrEmissionTests.PointerOperatorsAndExplicitConversionsEmitRawMemoryAccess", "LlvmIrEmissionTests.IndexedFieldAddressBehindRawPointerEmitsDirectParameterGeps", "LlvmIrEmissionTests.IndexedRawPointerElementsEmitDirectParameterGeps", "LlvmIrEmissionTests.SliceMutationEmitsIndirectStoreAndLoad"),
            Row("direct imported monomorphized and ffi calls", CoverageKind.PositiveLlvmEmission, "LlvmIrEmissionTests.TailPositionDirectCallsEmitTailMarkerWhenAbiCompatible", "LlvmIrEmissionTests.ImportedStarkFunctionUsesQualifiedDependencySymbol", "LlvmIrEmissionTests.SourceBackedGenericCallsInlineSmallConcreteMonomorphizedSymbols", "LlvmIrEmissionTests.ImportedSourceBackedGenericSpecializationsUseLinkOnceOdrComdatDefinitionsAndInlineSmallCalls", "LlvmIrEmissionTests.FfiVarargsDeclarationsAndCallsUseLlvmVarargFunctionType"),
            Row("indirect sret byval borrow out init and void calls", CoverageKind.PositiveLlvmEmission, "LlvmIrEmissionTests.FunctionPointerCallsEmitFastccIndirectCall", "LlvmIrEmissionTests.FunctionPointerCallsWithLargeReturnValuesUseSretAbi", "LlvmIrEmissionTests.FunctionPointerCallsWithLargeByValueParametersUseByvalAbi", "LlvmIrEmissionTests.FunctionPointerCallsWithBorrowParametersUsePointerAbi", "LlvmIrEmissionTests.FunctionPointerCallsWithOutParametersUseCallerStorage", "LlvmIrEmissionTests.FunctionPointerCallsWithInitParametersUseCallerStorage", "LlvmIrEmissionTests.DirectVoidStatementCallsWithOutParametersUseCallerStorage")
        ];
    }

    private static void AssertCoverageRowsPointAtRealTests(IReadOnlyList<CoverageRow> rows)
    {
        var testMethods = DiscoverTestMethods();
        foreach (var row in rows)
        {
            Assert.NotEmpty(row.TestNames);
            foreach (var testName in row.TestNames)
            {
                Assert.True(
                    testMethods.Contains(testName),
                    $"Coverage row '{row.NodeName}' points at missing test '{testName}'.");
                Assert.True(
                    TestMatchesCoverageKind(row.Kind, testName),
                    $"Coverage row '{row.NodeName}' has kind '{row.Kind}' but points at '{testName}'.");
            }
        }
    }

    private static bool TestMatchesCoverageKind(CoverageKind kind, string testName)
    {
        return kind switch
        {
            CoverageKind.PositiveLlvmEmission => testName.StartsWith("LlvmIrEmissionTests.", StringComparison.Ordinal)
                                                 || testName.StartsWith("LlvmEmitterConversionTests.", StringComparison.Ordinal),
            CoverageKind.ValidationRejectsBeforeEmission => testName.StartsWith("SsaIrValidationTests.", StringComparison.Ordinal)
                                                            || testName.StartsWith("LoweringContractValidationTests.", StringComparison.Ordinal),
            _ => false
        };
    }

    private static ISet<string> DiscoverTestMethods()
    {
        return typeof(SsaEmitterCoverageMatrixTests)
            .Assembly
            .GetTypes()
            .SelectMany(static type => type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(static method => method.GetCustomAttributes(inherit: false).Any(IsXunitTestAttribute))
                .Select(method => $"{type.Name}.{method.Name}"))
            .ToHashSet(StringComparer.Ordinal);
    }

    private static bool IsXunitTestAttribute(object attribute)
    {
        var attributeName = attribute.GetType().Name;
        return string.Equals(attributeName, "FactAttribute", StringComparison.Ordinal)
               || string.Equals(attributeName, "TheoryAttribute", StringComparison.Ordinal);
    }

    private static IEnumerable<string> EnumerateRequiredNodeNames()
    {
        var compilerAssembly = typeof(SsaInstruction).Assembly;
        foreach (var type in compilerAssembly.GetTypes()
                     .Where(static type => !type.IsAbstract && typeof(SsaValue).IsAssignableFrom(type))
                     .OrderBy(static type => type.Name, StringComparer.Ordinal))
        {
            yield return type.Name;
        }

        foreach (var type in compilerAssembly.GetTypes()
                     .Where(static type => !type.IsAbstract && typeof(SsaRValue).IsAssignableFrom(type))
                     .OrderBy(static type => type.Name, StringComparer.Ordinal))
        {
            yield return type.Name;
        }

        foreach (var type in compilerAssembly.GetTypes()
                     .Where(static type => !type.IsAbstract && typeof(SsaInstruction).IsAssignableFrom(type))
                     .OrderBy(static type => type.Name, StringComparer.Ordinal))
        {
            yield return type.Name;
        }

        yield return nameof(SsaTerminator);

        foreach (var kind in Enum.GetValues<SsaTerminatorKind>().Order())
        {
            yield return TerminatorName(kind);
        }
    }

    private static CoverageRow Row(string nodeName, CoverageKind kind, params string[] testNames)
    {
        return new CoverageRow(nodeName, kind, testNames);
    }

    private static string TerminatorName(SsaTerminatorKind kind)
    {
        return $"{nameof(SsaTerminatorKind)}.{kind}";
    }

    private sealed record CoverageRow(string NodeName, CoverageKind Kind, IReadOnlyList<string> TestNames);

    private enum CoverageKind
    {
        PositiveLlvmEmission,
        ValidationRejectsBeforeEmission
    }
}
