using System.Numerics;

namespace Stark.Compiler;

internal static class SsaValidatorFixtureCatalog
{
    private static readonly StarkTypeSymbol I32 = StarkTypeSymbols.Integer(32);
    private static readonly StarkTypeSymbol UnsizedInteger = new(StarkTypeKind.Integer, "integer");

    public static bool TryRun(string? fixtureName, out SsaValidatorFixtureRun run, out string error)
    {
        run = SsaValidatorFixtureRun.Empty;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(fixtureName))
        {
            error = "SSA validator fixture requests must provide a fixture name.";
            return false;
        }

        switch (fixtureName)
        {
            case "UndefinedSsaValueReferenceFailsBeforeLlvmEmission":
                run = EvaluateFixture(fixtureName, UndefinedSsaValueReferenceFailsBeforeLlvmEmission(), BuildUndefinedSsaValueReferenceFailsBeforeLlvmEmissionExpectation());
                return true;
            case "UnsupportedSsaConversionFailsBeforeLlvmEmission":
                run = EvaluateFixture(fixtureName, UnsupportedSsaConversionFailsBeforeLlvmEmission(), BuildUnsupportedSsaConversionFailsBeforeLlvmEmissionExpectation());
                return true;
            case "UnsizedIntegerConversionFailsBeforeLlvmEmission":
                run = EvaluateFixture(fixtureName, UnsizedIntegerConversionFailsBeforeLlvmEmission(), BuildUnsizedIntegerConversionFailsBeforeLlvmEmissionExpectation());
                return true;
            case "CompileTimeIntegerConstantFailsBeforeLlvmEmission":
                run = EvaluateFixture(fixtureName, CompileTimeIntegerConstantFailsBeforeLlvmEmission(), BuildCompileTimeIntegerConstantFailsBeforeLlvmEmissionExpectation());
                return true;
            case "IntegerConstantOutsideStorageFailsBeforeLlvmEmission":
                run = EvaluateFixture(fixtureName, IntegerConstantOutsideStorageFailsBeforeLlvmEmission(), BuildIntegerConstantOutsideStorageFailsBeforeLlvmEmissionExpectation());
                return true;
            case "IntegerConstantOutsideEffectiveRangeFailsBeforeLlvmEmission":
                run = EvaluateFixture(fixtureName, IntegerConstantOutsideEffectiveRangeFailsBeforeLlvmEmission(), BuildIntegerConstantOutsideEffectiveRangeFailsBeforeLlvmEmissionExpectation());
                return true;
            case "UnsupportedFloatConversionTargetFailsBeforeLlvmEmission":
                run = EvaluateFixture(fixtureName, UnsupportedFloatConversionTargetFailsBeforeLlvmEmission(), BuildUnsupportedFloatConversionTargetFailsBeforeLlvmEmissionExpectation());
                return true;
            case "DirectCallAbiMismatchFailsBeforeLlvmEmission":
                run = EvaluateFixture(fixtureName, DirectCallAbiMismatchFailsBeforeLlvmEmission(), BuildDirectCallAbiMismatchFailsBeforeLlvmEmissionExpectation());
                return true;
            case "DirectCallIndirectAddressOnDirectParameterFailsBeforeLlvmEmission":
                run = EvaluateFixture(fixtureName, DirectCallIndirectAddressOnDirectParameterFailsBeforeLlvmEmission(), BuildDirectCallIndirectAddressOnDirectParameterFailsBeforeLlvmEmissionExpectation());
                return true;
            case "DirectCallIndirectAddressPointeeMismatchFailsBeforeLlvmEmission":
                run = EvaluateFixture(fixtureName, DirectCallIndirectAddressPointeeMismatchFailsBeforeLlvmEmission(), BuildDirectCallIndirectAddressPointeeMismatchFailsBeforeLlvmEmissionExpectation());
                return true;
            case "DirectCallPromotedUnknownLocalFailsBeforeLlvmEmission":
                run = EvaluateFixture(fixtureName, DirectCallPromotedUnknownLocalFailsBeforeLlvmEmission(), BuildDirectCallPromotedUnknownLocalFailsBeforeLlvmEmissionExpectation());
                return true;
            case "DirectCallIndirectAddressAndPromotedStorageShapesAreAccepted":
                run = EvaluateFixture(fixtureName, DirectCallIndirectAddressAndPromotedStorageShapesAreAccepted(), BuildDirectCallIndirectAddressAndPromotedStorageShapesAreAcceptedExpectation());
                return true;
            case "IndirectCallIndirectAddressPointeeMismatchFailsBeforeLlvmEmission":
                run = EvaluateFixture(fixtureName, IndirectCallIndirectAddressPointeeMismatchFailsBeforeLlvmEmission(), BuildIndirectCallIndirectAddressPointeeMismatchFailsBeforeLlvmEmissionExpectation());
                return true;
            case "IndirectCallLargeByvalAddressShapeIsAccepted":
                run = EvaluateFixture(fixtureName, IndirectCallLargeByvalAddressShapeIsAccepted(), BuildIndirectCallLargeByvalAddressShapeIsAcceptedExpectation());
                return true;
            case "FfiTextReturnFailsBeforeLlvmEmission":
                run = EvaluateFixture(fixtureName, FfiTextReturnFailsBeforeLlvmEmission(), BuildFfiTextReturnFailsBeforeLlvmEmissionExpectation());
                return true;
            case "MirOnlyTempLocalStorageFailsBeforeLlvmEmission":
                run = EvaluateFixture(fixtureName, MirOnlyTempLocalStorageFailsBeforeLlvmEmission(), BuildMirOnlyTempLocalStorageFailsBeforeLlvmEmissionExpectation());
                return true;
            case "NonHeapDeallocationFailsBeforeLlvmEmission":
                run = EvaluateFixture(fixtureName, NonHeapDeallocationFailsBeforeLlvmEmission(), BuildNonHeapDeallocationFailsBeforeLlvmEmissionExpectation());
                return true;
            case "ArenaFrameInstructionsAreAcceptedAroundArenaAllocation":
                run = EvaluateFixture(fixtureName, ArenaFrameInstructionsAreAcceptedAroundArenaAllocation(), BuildArenaFrameInstructionsAreAcceptedAroundArenaAllocationExpectation());
                return true;
            case "ArenaAllocationWithoutFrameScopeFailsBeforeLlvmEmission":
                run = EvaluateFixture(fixtureName, ArenaAllocationWithoutFrameScopeFailsBeforeLlvmEmission(), BuildArenaAllocationWithoutFrameScopeFailsBeforeLlvmEmissionExpectation());
                return true;
            case "MisplacedArenaFrameInstructionsFailBeforeLlvmEmission":
                run = EvaluateFixture(fixtureName, MisplacedArenaFrameInstructionsFailBeforeLlvmEmission(), BuildMisplacedArenaFrameInstructionsFailBeforeLlvmEmissionExpectation());
                return true;
            case "LocalUseWithoutAllocationFailsBeforeLlvmEmission":
                run = EvaluateFixture(fixtureName, LocalUseWithoutAllocationFailsBeforeLlvmEmission(), BuildLocalUseWithoutAllocationFailsBeforeLlvmEmissionExpectation());
                return true;
            case "FunctionMissingAbiFailsBeforeLlvmEmission":
                run = EvaluateFixture(fixtureName, FunctionMissingAbiFailsBeforeLlvmEmission(), BuildFunctionMissingAbiFailsBeforeLlvmEmissionExpectation());
                return true;
            case "DuplicateSsaValueDefinitionFailsBeforeLlvmEmission":
                run = EvaluateFixture(fixtureName, DuplicateSsaValueDefinitionFailsBeforeLlvmEmission(), BuildDuplicateSsaValueDefinitionFailsBeforeLlvmEmissionExpectation());
                return true;
            case "PhiIncomingTypeMismatchFailsBeforeLlvmEmission":
                run = EvaluateFixture(fixtureName, PhiIncomingTypeMismatchFailsBeforeLlvmEmission(), BuildPhiIncomingTypeMismatchFailsBeforeLlvmEmissionExpectation());
                return true;
            case "PhiIncomingFromNonPredecessorFailsBeforeLlvmEmission":
                run = EvaluateFixture(fixtureName, PhiIncomingFromNonPredecessorFailsBeforeLlvmEmission(), BuildPhiIncomingFromNonPredecessorFailsBeforeLlvmEmissionExpectation());
                return true;
            case "PhiMissingIncomingFailsBeforeLlvmEmission":
                run = EvaluateFixture(fixtureName, PhiMissingIncomingFailsBeforeLlvmEmission(), BuildPhiMissingIncomingFailsBeforeLlvmEmissionExpectation());
                return true;
            case "PhiDuplicateIncomingFailsBeforeLlvmEmission":
                run = EvaluateFixture(fixtureName, PhiDuplicateIncomingFailsBeforeLlvmEmission(), BuildPhiDuplicateIncomingFailsBeforeLlvmEmissionExpectation());
                return true;
            case "PhiWithoutIncomingFailsBeforeLlvmEmission":
                run = EvaluateFixture(fixtureName, PhiWithoutIncomingFailsBeforeLlvmEmission(), BuildPhiWithoutIncomingFailsBeforeLlvmEmissionExpectation());
                return true;
            case "FunctionAbiReturnMismatchFailsBeforeLlvmEmission":
                run = EvaluateFixture(fixtureName, FunctionAbiReturnMismatchFailsBeforeLlvmEmission(), BuildFunctionAbiReturnMismatchFailsBeforeLlvmEmissionExpectation());
                return true;
            case "FunctionAbiRetborrowPointerReturnIsAccepted":
                run = EvaluateFixture(fixtureName, FunctionAbiRetborrowPointerReturnIsAccepted(), BuildFunctionAbiRetborrowPointerReturnIsAcceptedExpectation());
                return true;
            case "FunctionAbiParameterCountMismatchFailsBeforeLlvmEmission":
                run = EvaluateFixture(fixtureName, FunctionAbiParameterCountMismatchFailsBeforeLlvmEmission(), BuildFunctionAbiParameterCountMismatchFailsBeforeLlvmEmissionExpectation());
                return true;
            case "FunctionAbiParameterTypeMismatchFailsBeforeLlvmEmission":
                run = EvaluateFixture(fixtureName, FunctionAbiParameterTypeMismatchFailsBeforeLlvmEmission(), BuildFunctionAbiParameterTypeMismatchFailsBeforeLlvmEmissionExpectation());
                return true;
            case "FunctionAbiSretShapeMismatchFailsBeforeLlvmEmission":
                run = EvaluateFixture(fixtureName, FunctionAbiSretShapeMismatchFailsBeforeLlvmEmission(), BuildFunctionAbiSretShapeMismatchFailsBeforeLlvmEmissionExpectation());
                return true;
            case "FunctionAbiSretShapeAccepted":
                run = EvaluateFixture(fixtureName, FunctionAbiSretShapeAccepted(), BuildFunctionAbiSretShapeAcceptedExpectation());
                return true;
            case "AddressOfParameterMissingAbiUserParameterFailsBeforeLlvmEmission":
                run = EvaluateFixture(fixtureName, AddressOfParameterMissingAbiUserParameterFailsBeforeLlvmEmission(), BuildAddressOfParameterMissingAbiUserParameterFailsBeforeLlvmEmissionExpectation());
                return true;
            case "UnsupportedSsaInstructionFailsBeforeLlvmEmission":
                run = EvaluateFixture(fixtureName, UnsupportedSsaInstructionFailsBeforeLlvmEmission(), BuildUnsupportedSsaInstructionFailsBeforeLlvmEmissionExpectation());
                return true;
            case "UnsupportedSsaRValueFailsBeforeLlvmEmission":
                run = EvaluateFixture(fixtureName, UnsupportedSsaRValueFailsBeforeLlvmEmission(), BuildUnsupportedSsaRValueFailsBeforeLlvmEmissionExpectation());
                return true;
            case "UnsupportedSsaValueFailsBeforeLlvmEmission":
                run = EvaluateFixture(fixtureName, UnsupportedSsaValueFailsBeforeLlvmEmission(), BuildUnsupportedSsaValueFailsBeforeLlvmEmissionExpectation());
                return true;
            case "UnsupportedSsaTerminatorFailsBeforeLlvmEmission":
                run = EvaluateFixture(fixtureName, UnsupportedSsaTerminatorFailsBeforeLlvmEmission(), BuildUnsupportedSsaTerminatorFailsBeforeLlvmEmissionExpectation());
                return true;
            case "DynamicStorageElementLayoutFailsBeforeLlvmEmission":
                run = EvaluateFixture(fixtureName, DynamicStorageElementLayoutFailsBeforeLlvmEmission(), BuildDynamicStorageElementLayoutFailsBeforeLlvmEmissionExpectation());
                return true;
            case "DynamicStorageCapacityWidthFailsBeforeLlvmEmission":
                run = EvaluateFixture(fixtureName, DynamicStorageCapacityWidthFailsBeforeLlvmEmission(), BuildDynamicStorageCapacityWidthFailsBeforeLlvmEmissionExpectation());
                return true;
            case "DynamicStorageReserveAddressShapeFailsBeforeLlvmEmission":
                run = EvaluateFixture(fixtureName, DynamicStorageReserveAddressShapeFailsBeforeLlvmEmission(), BuildDynamicStorageReserveAddressShapeFailsBeforeLlvmEmissionExpectation());
                return true;
            case "DynamicStorageMoveAtAddressPointeeMismatchFailsBeforeLlvmEmission":
                run = EvaluateFixture(fixtureName, DynamicStorageMoveAtAddressPointeeMismatchFailsBeforeLlvmEmission(), BuildDynamicStorageMoveAtAddressPointeeMismatchFailsBeforeLlvmEmissionExpectation());
                return true;
            case "ElementAddressBasePointeeMismatchFailsBeforeLlvmEmission":
                run = EvaluateFixture(fixtureName, ElementAddressBasePointeeMismatchFailsBeforeLlvmEmission(), BuildElementAddressBasePointeeMismatchFailsBeforeLlvmEmissionExpectation());
                return true;
            case "ElementAddressResultPointeeMismatchFailsBeforeLlvmEmission":
                run = EvaluateFixture(fixtureName, ElementAddressResultPointeeMismatchFailsBeforeLlvmEmission(), BuildElementAddressResultPointeeMismatchFailsBeforeLlvmEmissionExpectation());
                return true;
            case "UnsupportedUnaryShapeFailsBeforeLlvmEmission":
                run = EvaluateFixture(fixtureName, UnsupportedUnaryShapeFailsBeforeLlvmEmission(), BuildUnsupportedUnaryShapeFailsBeforeLlvmEmissionExpectation());
                return true;
            case "UnsizedIntegerUnaryFailsBeforeLlvmEmission":
                run = EvaluateFixture(fixtureName, UnsizedIntegerUnaryFailsBeforeLlvmEmission(), BuildUnsizedIntegerUnaryFailsBeforeLlvmEmissionExpectation());
                return true;
            case "UnsupportedFloatUnaryFailsBeforeLlvmEmission":
                run = EvaluateFixture(fixtureName, UnsupportedFloatUnaryFailsBeforeLlvmEmission(), BuildUnsupportedFloatUnaryFailsBeforeLlvmEmissionExpectation());
                return true;
            case "BinaryOperandShapeMismatchFailsBeforeLlvmEmission":
                run = EvaluateFixture(fixtureName, BinaryOperandShapeMismatchFailsBeforeLlvmEmission(), BuildBinaryOperandShapeMismatchFailsBeforeLlvmEmissionExpectation());
                return true;
            case "WrappingFloatOperatorFailsBeforeLlvmEmission":
                run = EvaluateFixture(fixtureName, WrappingFloatOperatorFailsBeforeLlvmEmission(), BuildWrappingFloatOperatorFailsBeforeLlvmEmissionExpectation());
                return true;
            case "UnsupportedFloatIntrinsicWidthFailsBeforeLlvmEmission":
                run = EvaluateFixture(fixtureName, UnsupportedFloatIntrinsicWidthFailsBeforeLlvmEmission(), BuildUnsupportedFloatIntrinsicWidthFailsBeforeLlvmEmissionExpectation());
                return true;
            case "ComparisonResultShapeMismatchFailsBeforeLlvmEmission":
                run = EvaluateFixture(fixtureName, ComparisonResultShapeMismatchFailsBeforeLlvmEmission(), BuildComparisonResultShapeMismatchFailsBeforeLlvmEmissionExpectation());
                return true;
            case "FixedArrayOrderedComparisonUnsupportedElementFailsBeforeHelperEmission":
                run = EvaluateFixture(fixtureName, FixedArrayOrderedComparisonUnsupportedElementFailsBeforeHelperEmission(), BuildFixedArrayOrderedComparisonUnsupportedElementFailsBeforeHelperEmissionExpectation());
                return true;
            case "NamedOrderedComparisonUnsupportedFieldFailsBeforeHelperEmission":
                run = EvaluateFixture(fixtureName, NamedOrderedComparisonUnsupportedFieldFailsBeforeHelperEmission(), BuildNamedOrderedComparisonUnsupportedFieldFailsBeforeHelperEmissionExpectation());
                return true;
            case "ExtractFieldResultMismatchFailsBeforeLlvmEmission":
                run = EvaluateFixture(fixtureName, ExtractFieldResultMismatchFailsBeforeLlvmEmission(), BuildExtractFieldResultMismatchFailsBeforeLlvmEmissionExpectation());
                return true;
            case "ExtractFieldNameIndexMismatchFailsBeforeLlvmEmission":
                run = EvaluateFixture(fixtureName, ExtractFieldNameIndexMismatchFailsBeforeLlvmEmission(), BuildExtractFieldNameIndexMismatchFailsBeforeLlvmEmissionExpectation());
                return true;
            case "SliceViewIndexExtractionIsAccepted":
                run = EvaluateFixture(fixtureName, SliceViewIndexExtractionIsAccepted(), BuildSliceViewIndexExtractionIsAcceptedExpectation());
                return true;
            case "SliceViewNoOpRetypeSupportsImmutableComponentExtraction":
                run = EvaluateFixture(fixtureName, SliceViewNoOpRetypeSupportsImmutableComponentExtraction(), BuildSliceViewNoOpRetypeSupportsImmutableComponentExtractionExpectation());
                return true;
            case "TextViewIndexExtractionIsAccepted":
                run = EvaluateFixture(fixtureName, TextViewIndexExtractionIsAccepted(), BuildTextViewIndexExtractionIsAcceptedExpectation());
                return true;
            case "TextViewFieldExtractionIsAccepted":
                run = EvaluateFixture(fixtureName, TextViewFieldExtractionIsAccepted(), BuildTextViewFieldExtractionIsAcceptedExpectation());
                return true;
            case "SelectArmShapeMismatchFailsBeforeLlvmEmission":
                run = EvaluateFixture(fixtureName, SelectArmShapeMismatchFailsBeforeLlvmEmission(), BuildSelectArmShapeMismatchFailsBeforeLlvmEmissionExpectation());
                return true;
            case "UnsupportedSwitchConditionFailsBeforeLlvmEmission":
                run = EvaluateFixture(fixtureName, UnsupportedSwitchConditionFailsBeforeLlvmEmission(), BuildUnsupportedSwitchConditionFailsBeforeLlvmEmissionExpectation());
                return true;
            case "UnsizedIntegerSwitchConditionFailsBeforeLlvmEmission":
                run = EvaluateFixture(fixtureName, UnsizedIntegerSwitchConditionFailsBeforeLlvmEmission(), BuildUnsizedIntegerSwitchConditionFailsBeforeLlvmEmissionExpectation());
                return true;
            case "SwitchCaseShapeMismatchFailsBeforeLlvmEmission":
                run = EvaluateFixture(fixtureName, SwitchCaseShapeMismatchFailsBeforeLlvmEmission(), BuildSwitchCaseShapeMismatchFailsBeforeLlvmEmissionExpectation());
                return true;
            case "SliceCreationShapeMismatchFailsBeforeLlvmEmission":
                run = EvaluateFixture(fixtureName, SliceCreationShapeMismatchFailsBeforeLlvmEmission(), BuildSliceCreationShapeMismatchFailsBeforeLlvmEmissionExpectation());
                return true;
            case "IndirectLoadAddressShapeFailsBeforeLlvmEmission":
                run = EvaluateFixture(fixtureName, IndirectLoadAddressShapeFailsBeforeLlvmEmission(), BuildIndirectLoadAddressShapeFailsBeforeLlvmEmissionExpectation());
                return true;
            case "IndirectStoreAcceptsQualifiedPointeeShape":
                run = EvaluateFixture(fixtureName, IndirectStoreAcceptsQualifiedPointeeShape(), BuildIndirectStoreAcceptsQualifiedPointeeShapeExpectation());
                return true;
            case "CopyMemoryDestinationPointeeMismatchFailsBeforeLlvmEmission":
                run = EvaluateFixture(fixtureName, CopyMemoryDestinationPointeeMismatchFailsBeforeLlvmEmission(), BuildCopyMemoryDestinationPointeeMismatchFailsBeforeLlvmEmissionExpectation());
                return true;
            case "CopyMemorySourcePointeeMismatchFailsBeforeLlvmEmission":
                run = EvaluateFixture(fixtureName, CopyMemorySourcePointeeMismatchFailsBeforeLlvmEmission(), BuildCopyMemorySourcePointeeMismatchFailsBeforeLlvmEmissionExpectation());
                return true;
            case "CopyMemoryWithoutConcreteLayoutFailsBeforeLlvmEmission":
                run = EvaluateFixture(fixtureName, CopyMemoryWithoutConcreteLayoutFailsBeforeLlvmEmission(), BuildCopyMemoryWithoutConcreteLayoutFailsBeforeLlvmEmissionExpectation());
                return true;
            case "CopyMemoryAllowsFixedArrayElementPointers":
                run = EvaluateFixture(fixtureName, CopyMemoryAllowsFixedArrayElementPointers(), BuildCopyMemoryAllowsFixedArrayElementPointersExpectation());
                return true;
            case "ScopedNoAliasProofCarrierAcceptsMatchingParameterRoots":
                run = EvaluateFixture(fixtureName, ScopedNoAliasProofCarrierAcceptsMatchingParameterRoots(), BuildScopedNoAliasProofCarrierAcceptsMatchingParameterRootsExpectation());
                return true;
            case "ScopedNoAliasProofCarrierRootMismatchFailsBeforeLlvmEmission":
                run = EvaluateFixture(fixtureName, ScopedNoAliasProofCarrierRootMismatchFailsBeforeLlvmEmission(), BuildScopedNoAliasProofCarrierRootMismatchFailsBeforeLlvmEmissionExpectation());
                return true;
            case "ScopedNoAliasProofCarrierDuplicateRootsFailBeforeLlvmEmission":
                run = EvaluateFixture(fixtureName, ScopedNoAliasProofCarrierDuplicateRootsFailBeforeLlvmEmission(), BuildScopedNoAliasProofCarrierDuplicateRootsFailBeforeLlvmEmissionExpectation());
                return true;
            case "ScopedNoAliasProofCarrierIdMismatchFailsBeforeLlvmEmission":
                run = EvaluateFixture(fixtureName, ScopedNoAliasProofCarrierIdMismatchFailsBeforeLlvmEmission(), BuildScopedNoAliasProofCarrierIdMismatchFailsBeforeLlvmEmissionExpectation());
                return true;
            case "ScopedNoAliasProofCarrierUnknownParameterRootFailsBeforeLlvmEmission":
                run = EvaluateFixture(fixtureName, ScopedNoAliasProofCarrierUnknownParameterRootFailsBeforeLlvmEmission(), BuildScopedNoAliasProofCarrierUnknownParameterRootFailsBeforeLlvmEmissionExpectation());
                return true;
            case "StringConstantNonTextTypeFailsBeforeLlvmEmission":
                run = EvaluateFixture(fixtureName, StringConstantNonTextTypeFailsBeforeLlvmEmission(), BuildStringConstantNonTextTypeFailsBeforeLlvmEmissionExpectation());
                return true;
            case "TextDataAddressPointeeMismatchFailsBeforeLlvmEmission":
                run = EvaluateFixture(fixtureName, TextDataAddressPointeeMismatchFailsBeforeLlvmEmission(), BuildTextDataAddressPointeeMismatchFailsBeforeLlvmEmissionExpectation());
                return true;
            case "GlobalAddressPointeeMismatchFailsBeforeLlvmEmission":
                run = EvaluateFixture(fixtureName, GlobalAddressPointeeMismatchFailsBeforeLlvmEmission(), BuildGlobalAddressPointeeMismatchFailsBeforeLlvmEmissionExpectation());
                return true;
            case "UnknownGlobalLoadFailsBeforeLlvmEmission":
                run = EvaluateFixture(fixtureName, UnknownGlobalLoadFailsBeforeLlvmEmission(), BuildUnknownGlobalLoadFailsBeforeLlvmEmissionExpectation());
                return true;
            case "KnownGlobalLoadTypeMismatchFailsBeforeLlvmEmission":
                run = EvaluateFixture(fixtureName, KnownGlobalLoadTypeMismatchFailsBeforeLlvmEmission(), BuildKnownGlobalLoadTypeMismatchFailsBeforeLlvmEmissionExpectation());
                return true;
            case "KnownGlobalAddressTypeMismatchFailsBeforeLlvmEmission":
                run = EvaluateFixture(fixtureName, KnownGlobalAddressTypeMismatchFailsBeforeLlvmEmission(), BuildKnownGlobalAddressTypeMismatchFailsBeforeLlvmEmissionExpectation());
                return true;
            case "StoreToImmutableGlobalFailsBeforeLlvmEmission":
                run = EvaluateFixture(fixtureName, StoreToImmutableGlobalFailsBeforeLlvmEmission(), BuildStoreToImmutableGlobalFailsBeforeLlvmEmissionExpectation());
                return true;
            case "StoreGlobalValueMismatchFailsBeforeLlvmEmission":
                run = EvaluateFixture(fixtureName, StoreGlobalValueMismatchFailsBeforeLlvmEmission(), BuildStoreGlobalValueMismatchFailsBeforeLlvmEmissionExpectation());
                return true;
            case "StoreMutableKnownGlobalPassesSsaValidation":
                run = EvaluateFixture(fixtureName, StoreMutableKnownGlobalPassesSsaValidation(), BuildStoreMutableKnownGlobalPassesSsaValidationExpectation());
                return true;
            case "FunctionAddressNonFunctionPointerTypeFailsBeforeLlvmEmission":
                run = EvaluateFixture(fixtureName, FunctionAddressNonFunctionPointerTypeFailsBeforeLlvmEmission(), BuildFunctionAddressNonFunctionPointerTypeFailsBeforeLlvmEmissionExpectation());
                return true;
            case "FunctionAddressMissingAbiFailsBeforeLlvmEmission":
                run = EvaluateFixture(fixtureName, FunctionAddressMissingAbiFailsBeforeLlvmEmission(), BuildFunctionAddressMissingAbiFailsBeforeLlvmEmissionExpectation());
                return true;
            case "FunctionAddressSignatureMismatchFailsBeforeLlvmEmission":
                run = EvaluateFixture(fixtureName, FunctionAddressSignatureMismatchFailsBeforeLlvmEmission(), BuildFunctionAddressSignatureMismatchFailsBeforeLlvmEmissionExpectation());
                return true;
            case "SystemMathBuiltinInvalidSignatureFailsBeforeLlvmEmission":
                run = EvaluateFixture(fixtureName, SystemMathBuiltinInvalidSignatureFailsBeforeLlvmEmission(), BuildSystemMathBuiltinInvalidSignatureFailsBeforeLlvmEmissionExpectation());
                return true;
            case "SystemBitOperationsBuiltinInvalidWidthFailsBeforeLlvmEmission":
                run = EvaluateFixture(fixtureName, SystemBitOperationsBuiltinInvalidWidthFailsBeforeLlvmEmission(), BuildSystemBitOperationsBuiltinInvalidWidthFailsBeforeLlvmEmissionExpectation());
                return true;
            case "SystemMemoryBuiltinInvalidAllocationShapeFailsBeforeLlvmEmission":
                run = EvaluateFixture(fixtureName, SystemMemoryBuiltinInvalidAllocationShapeFailsBeforeLlvmEmission(), BuildSystemMemoryBuiltinInvalidAllocationShapeFailsBeforeLlvmEmissionExpectation());
                return true;
            case "SystemCollectionsDictionaryKeyAsciiKeyPassesBuiltinValidation":
                run = EvaluateFixture(fixtureName, SystemCollectionsDictionaryKeyAsciiKeyPassesBuiltinValidation(), BuildSystemCollectionsDictionaryKeyAsciiKeyPassesBuiltinValidationExpectation());
                return true;
            case "SystemCollectionsDictionaryKeyUnsupportedKeyFailsBeforeLlvmEmission":
                run = EvaluateFixture(fixtureName, SystemCollectionsDictionaryKeyUnsupportedKeyFailsBeforeLlvmEmission(), BuildSystemCollectionsDictionaryKeyUnsupportedKeyFailsBeforeLlvmEmissionExpectation());
                return true;
            case "SystemRuntimeByteSlicePartsMutableMismatchFailsBeforeLlvmEmission":
                run = EvaluateFixture(fixtureName, SystemRuntimeByteSlicePartsMutableMismatchFailsBeforeLlvmEmission(), BuildSystemRuntimeByteSlicePartsMutableMismatchFailsBeforeLlvmEmissionExpectation());
                return true;
            case "ValidSystemBuiltinSignaturesPassSsaValidation":
                run = EvaluateFixture(fixtureName, ValidSystemBuiltinSignaturesPassSsaValidation(), BuildValidSystemBuiltinSignaturesPassSsaValidationExpectation());
                return true;
            case "ExtractIndexOutOfRangeIsUnrepresentable":
                error = "SSA validator fixture 'ExtractIndexOutOfRangeIsUnrepresentable' is a host object-model constructor guard, not a validator input.";
                return false;
            case "InsertIndexValueMismatchIsUnrepresentable":
                error = "SSA validator fixture 'InsertIndexValueMismatchIsUnrepresentable' is a host object-model constructor guard, not a validator input.";
                return false;
            case "IndexOperationFamilyMismatchIsUnrepresentable":
                error = "SSA validator fixture 'IndexOperationFamilyMismatchIsUnrepresentable' is a host object-model constructor guard, not a validator input.";
                return false;
            default:
                error = $"Unknown SSA validator fixture '{fixtureName}'.";
                return false;
        }
    }

    private static SsaValidatorFixtureRun EvaluateFixture(
        string fixtureName,
        IReadOnlyList<CompilerDiagnostic> diagnostics,
        IReadOnlyList<SsaValidatorFixtureExpectation> expectations)
    {
        var failures = new List<string>();
        foreach (var expectation in expectations)
        {
            if (!expectation.IsMetBy(diagnostics))
            {
                failures.Add(expectation.DescribeFailure());
            }
        }

        if (failures.Count == 0)
        {
            return new SsaValidatorFixtureRun(Passed: true, Diagnostics: diagnostics, Failure: null);
        }

        var failedDiagnostics = diagnostics.ToList();
        failedDiagnostics.Add(new CompilerDiagnostic(
            "STKTEST",
            DiagnosticSeverity.Error,
            $"SSA validator fixture '{fixtureName}' did not satisfy its expected diagnostic contract: {string.Join("; ", failures)}.",
            Stage: "host-test-validator-fixture"));
        return new SsaValidatorFixtureRun(Passed: false, Diagnostics: failedDiagnostics, Failure: string.Join("; ", failures));
    }

    private static IReadOnlyList<SsaValidatorFixtureExpectation> BuildUndefinedSsaValueReferenceFailsBeforeLlvmEmissionExpectation()
    {
        return
        [
            SsaValidatorFixtureExpectation.DiagnosticContaining("STK5002", ["value reference '%missing'", "not defined"]),
        ];
    }

    private static IReadOnlyList<SsaValidatorFixtureExpectation> BuildUnsupportedSsaConversionFailsBeforeLlvmEmissionExpectation()
    {
        return
        [
            SsaValidatorFixtureExpectation.DiagnosticContaining("STK5002", ["conversion from 'ascii' to 'unicode'", "not supported by SSA LLVM emission"]),
        ];
    }

    private static IReadOnlyList<SsaValidatorFixtureExpectation> BuildUnsizedIntegerConversionFailsBeforeLlvmEmissionExpectation()
    {
        return
        [
            SsaValidatorFixtureExpectation.DiagnosticContaining("STK5002", ["conversion source type 'integer'", "concrete integer"]),
        ];
    }

    private static IReadOnlyList<SsaValidatorFixtureExpectation> BuildCompileTimeIntegerConstantFailsBeforeLlvmEmissionExpectation()
    {
        return
        [
            SsaValidatorFixtureExpectation.DiagnosticContaining("STK5002", ["integer constant value", "concrete integer storage type", "integer"]),
        ];
    }

    private static IReadOnlyList<SsaValidatorFixtureExpectation> BuildIntegerConstantOutsideStorageFailsBeforeLlvmEmissionExpectation()
    {
        return
        [
            SsaValidatorFixtureExpectation.DiagnosticContaining("STK5002", ["integer constant value '300'", "does not fit storage type 'i8'"]),
        ];
    }

    private static IReadOnlyList<SsaValidatorFixtureExpectation> BuildIntegerConstantOutsideEffectiveRangeFailsBeforeLlvmEmissionExpectation()
    {
        return
        [
            SsaValidatorFixtureExpectation.DiagnosticContaining("STK5002", ["integer constant value '44'", "outside effective range 'i8[0 10]'"]),
        ];
    }

    private static IReadOnlyList<SsaValidatorFixtureExpectation> BuildUnsupportedFloatConversionTargetFailsBeforeLlvmEmissionExpectation()
    {
        return
        [
            SsaValidatorFixtureExpectation.DiagnosticContaining("STK5002", ["conversion target type 'f24'", "supported LLVM float"]),
        ];
    }

    private static IReadOnlyList<SsaValidatorFixtureExpectation> BuildDirectCallAbiMismatchFailsBeforeLlvmEmissionExpectation()
    {
        return
        [
            SsaValidatorFixtureExpectation.DiagnosticContaining("STK5002", ["ABI parameter count mismatch for 'Add'", "expected 2", "got 1"]),
        ];
    }

    private static IReadOnlyList<SsaValidatorFixtureExpectation> BuildDirectCallIndirectAddressOnDirectParameterFailsBeforeLlvmEmissionExpectation()
    {
        return
        [
            SsaValidatorFixtureExpectation.DiagnosticContaining("STK5002", ["call 'Identity' argument 1", "direct ABI parameter", "indirect argument address"]),
        ];
    }

    private static IReadOnlyList<SsaValidatorFixtureExpectation> BuildDirectCallIndirectAddressPointeeMismatchFailsBeforeLlvmEmissionExpectation()
    {
        return
        [
            SsaValidatorFixtureExpectation.DiagnosticContaining("STK5002", ["call 'Consume' argument 1 indirect address pointee", "bool", "i32"]),
        ];
    }

    private static IReadOnlyList<SsaValidatorFixtureExpectation> BuildDirectCallPromotedUnknownLocalFailsBeforeLlvmEmissionExpectation()
    {
        return
        [
            SsaValidatorFixtureExpectation.DiagnosticContaining("STK5002", ["call 'Consume' argument 1", "promotes unknown local or parameter 'missing'"]),
        ];
    }

    private static IReadOnlyList<SsaValidatorFixtureExpectation> BuildDirectCallIndirectAddressAndPromotedStorageShapesAreAcceptedExpectation()
    {
        return [SsaValidatorFixtureExpectation.NoDiagnostics()];
    }

    private static IReadOnlyList<SsaValidatorFixtureExpectation> BuildIndirectCallIndirectAddressPointeeMismatchFailsBeforeLlvmEmissionExpectation()
    {
        return
        [
            SsaValidatorFixtureExpectation.DiagnosticContaining("STK5002", ["indirect call argument 1 indirect address pointee", "bool", "i32[5]"]),
        ];
    }

    private static IReadOnlyList<SsaValidatorFixtureExpectation> BuildIndirectCallLargeByvalAddressShapeIsAcceptedExpectation()
    {
        return [SsaValidatorFixtureExpectation.NoDiagnostics()];
    }

    private static IReadOnlyList<SsaValidatorFixtureExpectation> BuildFfiTextReturnFailsBeforeLlvmEmissionExpectation()
    {
        return
        [
            SsaValidatorFixtureExpectation.DiagnosticContaining("STK5002", ["FFI text-view return", "CurrentName", "raw pointer plus explicit length/status"]),
        ];
    }

    private static IReadOnlyList<SsaValidatorFixtureExpectation> BuildMirOnlyTempLocalStorageFailsBeforeLlvmEmissionExpectation()
    {
        return
        [
            SsaValidatorFixtureExpectation.DiagnosticContaining("STK5002", ["local 'scratch'", "invalid storage class 'temp'"]),
        ];
    }

    private static IReadOnlyList<SsaValidatorFixtureExpectation> BuildNonHeapDeallocationFailsBeforeLlvmEmissionExpectation()
    {
        return
        [
            SsaValidatorFixtureExpectation.DiagnosticContaining("STK5002", ["local 'scratch'", "invalid deallocation storage class 'stack'"]),
        ];
    }

    private static IReadOnlyList<SsaValidatorFixtureExpectation> BuildArenaFrameInstructionsAreAcceptedAroundArenaAllocationExpectation()
    {
        return [SsaValidatorFixtureExpectation.NoDiagnostics()];
    }

    private static IReadOnlyList<SsaValidatorFixtureExpectation> BuildArenaAllocationWithoutFrameScopeFailsBeforeLlvmEmissionExpectation()
    {
        return
        [
            SsaValidatorFixtureExpectation.DiagnosticContaining("STK5002", ["arena-using function", "exactly one arena frame enter", "found 0"]),
            SsaValidatorFixtureExpectation.DiagnosticContaining("STK5002", ["arena-using return block", "arena frame leave before return"]),
        ];
    }

    private static IReadOnlyList<SsaValidatorFixtureExpectation> BuildMisplacedArenaFrameInstructionsFailBeforeLlvmEmissionExpectation()
    {
        return
        [
            SsaValidatorFixtureExpectation.DiagnosticContaining("STK5002", ["arena frame enter", "first SSA instruction in the entry block"]),
        ];
    }

    private static IReadOnlyList<SsaValidatorFixtureExpectation> BuildLocalUseWithoutAllocationFailsBeforeLlvmEmissionExpectation()
    {
        return
        [
            SsaValidatorFixtureExpectation.DiagnosticContaining("STK5002", ["local 'scratch'", "used before it is allocated in SSA"]),
        ];
    }

    private static IReadOnlyList<SsaValidatorFixtureExpectation> BuildFunctionMissingAbiFailsBeforeLlvmEmissionExpectation()
    {
        return
        [
            SsaValidatorFixtureExpectation.DiagnosticContaining("STK5002", ["function 'Run'", "missing ABI lowering"]),
        ];
    }

    private static IReadOnlyList<SsaValidatorFixtureExpectation> BuildDuplicateSsaValueDefinitionFailsBeforeLlvmEmissionExpectation()
    {
        return
        [
            SsaValidatorFixtureExpectation.DiagnosticContaining("STK5002", ["SSA value '%v0'", "defined more than once"]),
        ];
    }

    private static IReadOnlyList<SsaValidatorFixtureExpectation> BuildPhiIncomingTypeMismatchFailsBeforeLlvmEmissionExpectation()
    {
        return
        [
            SsaValidatorFixtureExpectation.DiagnosticContaining("STK5002", ["phi 'v0' incoming value", "bool", "i32"]),
        ];
    }

    private static IReadOnlyList<SsaValidatorFixtureExpectation> BuildPhiIncomingFromNonPredecessorFailsBeforeLlvmEmissionExpectation()
    {
        return
        [
            SsaValidatorFixtureExpectation.DiagnosticContaining("STK5002", ["phi 'v0' incoming predecessor block '2'", "does not branch to block '1'"]),
        ];
    }

    private static IReadOnlyList<SsaValidatorFixtureExpectation> BuildPhiMissingIncomingFailsBeforeLlvmEmissionExpectation()
    {
        return
        [
            SsaValidatorFixtureExpectation.DiagnosticContaining("STK5002", ["phi 'v0'", "missing an incoming value for predecessor block '1'"]),
        ];
    }

    private static IReadOnlyList<SsaValidatorFixtureExpectation> BuildPhiDuplicateIncomingFailsBeforeLlvmEmissionExpectation()
    {
        return
        [
            SsaValidatorFixtureExpectation.DiagnosticContaining("STK5002", ["phi 'v0'", "more than one incoming value for predecessor block '0'"]),
        ];
    }

    private static IReadOnlyList<SsaValidatorFixtureExpectation> BuildPhiWithoutIncomingFailsBeforeLlvmEmissionExpectation()
    {
        return
        [
            SsaValidatorFixtureExpectation.DiagnosticContaining("STK5002", ["phi 'v0'", "requires at least one incoming value"]),
        ];
    }

    private static IReadOnlyList<SsaValidatorFixtureExpectation> BuildFunctionAbiReturnMismatchFailsBeforeLlvmEmissionExpectation()
    {
        return
        [
            SsaValidatorFixtureExpectation.DiagnosticContaining("STK5002", ["function 'Run' ABI source return", "bool", "i32"]),
            SsaValidatorFixtureExpectation.DiagnosticContaining("STK5002", ["function 'Run' ABI LLVM return", "bool", "i32"]),
        ];
    }

    private static IReadOnlyList<SsaValidatorFixtureExpectation> BuildFunctionAbiRetborrowPointerReturnIsAcceptedExpectation()
    {
        return [SsaValidatorFixtureExpectation.NoDiagnostics()];
    }

    private static IReadOnlyList<SsaValidatorFixtureExpectation> BuildFunctionAbiParameterCountMismatchFailsBeforeLlvmEmissionExpectation()
    {
        return
        [
            SsaValidatorFixtureExpectation.DiagnosticContaining("STK5002", ["function 'Run' ABI user parameter count mismatch", "expected 1", "got 0"]),
        ];
    }

    private static IReadOnlyList<SsaValidatorFixtureExpectation> BuildFunctionAbiParameterTypeMismatchFailsBeforeLlvmEmissionExpectation()
    {
        return
        [
            SsaValidatorFixtureExpectation.DiagnosticContaining("STK5002", ["function 'Run' ABI parameter 1 source type", "bool", "i32"]),
        ];
    }

    private static IReadOnlyList<SsaValidatorFixtureExpectation> BuildFunctionAbiSretShapeMismatchFailsBeforeLlvmEmissionExpectation()
    {
        return
        [
            SsaValidatorFixtureExpectation.DiagnosticContaining("STK5002", ["function 'Run' sret source type", "bool", "i32[4]"]),
            SsaValidatorFixtureExpectation.DiagnosticContaining("STK5002", ["function 'Run' sret LLVM parameter pointee", "bool", "i32[4]"]),
        ];
    }

    private static IReadOnlyList<SsaValidatorFixtureExpectation> BuildFunctionAbiSretShapeAcceptedExpectation()
    {
        return [SsaValidatorFixtureExpectation.NoDiagnostics()];
    }

    private static IReadOnlyList<SsaValidatorFixtureExpectation> BuildAddressOfParameterMissingAbiUserParameterFailsBeforeLlvmEmissionExpectation()
    {
        return
        [
            SsaValidatorFixtureExpectation.DiagnosticContaining("STK5002", ["address-of parameter 'value'", "missing ABI user-parameter lowering"]),
        ];
    }

    private static IReadOnlyList<SsaValidatorFixtureExpectation> BuildUnsupportedSsaInstructionFailsBeforeLlvmEmissionExpectation()
    {
        return
        [
            SsaValidatorFixtureExpectation.DiagnosticContaining("STK5002", ["unsupported SSA instruction type", "UnsupportedSsaInstruction"]),
        ];
    }

    private static IReadOnlyList<SsaValidatorFixtureExpectation> BuildUnsupportedSsaRValueFailsBeforeLlvmEmissionExpectation()
    {
        return
        [
            SsaValidatorFixtureExpectation.DiagnosticContaining("STK5002", ["unsupported SSA rvalue type", "UnsupportedSsaRValue"]),
        ];
    }

    private static IReadOnlyList<SsaValidatorFixtureExpectation> BuildUnsupportedSsaValueFailsBeforeLlvmEmissionExpectation()
    {
        return
        [
            SsaValidatorFixtureExpectation.DiagnosticContaining("STK5002", ["unsupported SSA value type", "UnsupportedSsaValue"]),
        ];
    }

    private static IReadOnlyList<SsaValidatorFixtureExpectation> BuildUnsupportedSsaTerminatorFailsBeforeLlvmEmissionExpectation()
    {
        return
        [
            SsaValidatorFixtureExpectation.DiagnosticContaining("STK5002", ["unsupported SSA terminator kind", "999"]),
        ];
    }

    private static IReadOnlyList<SsaValidatorFixtureExpectation> BuildDynamicStorageElementLayoutFailsBeforeLlvmEmissionExpectation()
    {
        return
        [
            SsaValidatorFixtureExpectation.DiagnosticContaining("STK5002", ["dynamic storage allocation", "concrete element layout", "void"]),
        ];
    }

    private static IReadOnlyList<SsaValidatorFixtureExpectation> BuildDynamicStorageCapacityWidthFailsBeforeLlvmEmissionExpectation()
    {
        return
        [
            SsaValidatorFixtureExpectation.DiagnosticContaining("STK5002", ["dynamic storage capacity", "width '128'", "not supported"]),
        ];
    }

    private static IReadOnlyList<SsaValidatorFixtureExpectation> BuildDynamicStorageReserveAddressShapeFailsBeforeLlvmEmissionExpectation()
    {
        return
        [
            SsaValidatorFixtureExpectation.DiagnosticContaining("STK5002", ["dynamic storage Reserve address", "must be a raw pointer", "i32"]),
        ];
    }

    private static IReadOnlyList<SsaValidatorFixtureExpectation> BuildDynamicStorageMoveAtAddressPointeeMismatchFailsBeforeLlvmEmissionExpectation()
    {
        return
        [
            SsaValidatorFixtureExpectation.DiagnosticContaining("STK5002", ["dynamic storage MoveAt address pointee", "dynamic bool", "dynamic i32"]),
        ];
    }

    private static IReadOnlyList<SsaValidatorFixtureExpectation> BuildElementAddressBasePointeeMismatchFailsBeforeLlvmEmissionExpectation()
    {
        return
        [
            SsaValidatorFixtureExpectation.DiagnosticContaining("STK5002", ["element address base pointee", "bool[4]", "i32[4]"]),
        ];
    }

    private static IReadOnlyList<SsaValidatorFixtureExpectation> BuildElementAddressResultPointeeMismatchFailsBeforeLlvmEmissionExpectation()
    {
        return
        [
            SsaValidatorFixtureExpectation.DiagnosticContaining("STK5002", ["element address result pointee", "bool", "i32"]),
        ];
    }

    private static IReadOnlyList<SsaValidatorFixtureExpectation> BuildUnsupportedUnaryShapeFailsBeforeLlvmEmissionExpectation()
    {
        return
        [
            SsaValidatorFixtureExpectation.DiagnosticContaining("STK5002", ["unary operator 'LogicalNot'", "bool operand and bool result"]),
        ];
    }

    private static IReadOnlyList<SsaValidatorFixtureExpectation> BuildUnsizedIntegerUnaryFailsBeforeLlvmEmissionExpectation()
    {
        return
        [
            SsaValidatorFixtureExpectation.DiagnosticContaining("STK5002", ["unary operator 'Negate' result type 'integer'", "concrete integer"]),
        ];
    }

    private static IReadOnlyList<SsaValidatorFixtureExpectation> BuildUnsupportedFloatUnaryFailsBeforeLlvmEmissionExpectation()
    {
        return
        [
            SsaValidatorFixtureExpectation.DiagnosticContaining("STK5002", ["unary operator 'Negate' result type 'f24'", "supported LLVM float"]),
        ];
    }

    private static IReadOnlyList<SsaValidatorFixtureExpectation> BuildBinaryOperandShapeMismatchFailsBeforeLlvmEmissionExpectation()
    {
        return
        [
            SsaValidatorFixtureExpectation.DiagnosticContaining("STK5002", ["binary operator 'Add' right operand", "bool", "i32"]),
        ];
    }

    private static IReadOnlyList<SsaValidatorFixtureExpectation> BuildWrappingFloatOperatorFailsBeforeLlvmEmissionExpectation()
    {
        return
        [
            SsaValidatorFixtureExpectation.DiagnosticContaining("STK5002", ["wrapping integer operator 'WrappingAdd'", "concrete integer", "f32"]),
        ];
    }

    private static IReadOnlyList<SsaValidatorFixtureExpectation> BuildUnsupportedFloatIntrinsicWidthFailsBeforeLlvmEmissionExpectation()
    {
        return
        [
            SsaValidatorFixtureExpectation.DiagnosticContaining("STK5002", ["exponent operator result type 'f24'", "not supported by LLVM emission"]),
        ];
    }

    private static IReadOnlyList<SsaValidatorFixtureExpectation> BuildComparisonResultShapeMismatchFailsBeforeLlvmEmissionExpectation()
    {
        return
        [
            SsaValidatorFixtureExpectation.DiagnosticContaining("STK5002", ["comparison operator 'Equal'", "bool result", "i32"]),
        ];
    }

    private static IReadOnlyList<SsaValidatorFixtureExpectation> BuildFixedArrayOrderedComparisonUnsupportedElementFailsBeforeHelperEmissionExpectation()
    {
        return
        [
            SsaValidatorFixtureExpectation.DiagnosticContaining("STK5002", ["comparison operator 'LessThan'", "fixed-array operand", "not supported by ordered comparison helper lowering"]),
        ];
    }

    private static IReadOnlyList<SsaValidatorFixtureExpectation> BuildNamedOrderedComparisonUnsupportedFieldFailsBeforeHelperEmissionExpectation()
    {
        return
        [
            SsaValidatorFixtureExpectation.DiagnosticContaining("STK5002", ["comparison operator 'GreaterThan'", "named aggregate operand", "not supported by ordered comparison helper lowering"]),
        ];
    }

    private static IReadOnlyList<SsaValidatorFixtureExpectation> BuildExtractFieldResultMismatchFailsBeforeLlvmEmissionExpectation()
    {
        return
        [
            SsaValidatorFixtureExpectation.DiagnosticContaining("STK5002", ["field extraction 'Flag' result", "bool", "i32"]),
        ];
    }

    private static IReadOnlyList<SsaValidatorFixtureExpectation> BuildExtractFieldNameIndexMismatchFailsBeforeLlvmEmissionExpectation()
    {
        return
        [
            SsaValidatorFixtureExpectation.DiagnosticContaining("STK5002", ["field extraction field 'Count' index '0'", "field 'Value'"]),
        ];
    }

    private static IReadOnlyList<SsaValidatorFixtureExpectation> BuildSliceViewIndexExtractionIsAcceptedExpectation()
    {
        return [SsaValidatorFixtureExpectation.NoDiagnostics()];
    }

    private static IReadOnlyList<SsaValidatorFixtureExpectation> BuildSliceViewNoOpRetypeSupportsImmutableComponentExtractionExpectation()
    {
        return [SsaValidatorFixtureExpectation.NoDiagnostics()];
    }

    private static IReadOnlyList<SsaValidatorFixtureExpectation> BuildTextViewIndexExtractionIsAcceptedExpectation()
    {
        return [SsaValidatorFixtureExpectation.NoDiagnostics()];
    }

    private static IReadOnlyList<SsaValidatorFixtureExpectation> BuildTextViewFieldExtractionIsAcceptedExpectation()
    {
        return [SsaValidatorFixtureExpectation.NoDiagnostics()];
    }

    private static IReadOnlyList<SsaValidatorFixtureExpectation> BuildSelectArmShapeMismatchFailsBeforeLlvmEmissionExpectation()
    {
        return
        [
            SsaValidatorFixtureExpectation.DiagnosticContaining("STK5002", ["select false arm", "bool", "i32"]),
        ];
    }

    private static IReadOnlyList<SsaValidatorFixtureExpectation> BuildUnsupportedSwitchConditionFailsBeforeLlvmEmissionExpectation()
    {
        return
        [
            SsaValidatorFixtureExpectation.DiagnosticContaining("STK5002", ["switch condition type 'ascii'", "bool or a concrete integer"]),
        ];
    }

    private static IReadOnlyList<SsaValidatorFixtureExpectation> BuildUnsizedIntegerSwitchConditionFailsBeforeLlvmEmissionExpectation()
    {
        return
        [
            SsaValidatorFixtureExpectation.DiagnosticContaining("STK5002", ["switch condition type 'integer'", "concrete integer"]),
        ];
    }

    private static IReadOnlyList<SsaValidatorFixtureExpectation> BuildSwitchCaseShapeMismatchFailsBeforeLlvmEmissionExpectation()
    {
        return
        [
            SsaValidatorFixtureExpectation.DiagnosticContaining("STK5002", ["switch case 'true' match value", "bool", "i32"]),
        ];
    }

    private static IReadOnlyList<SsaValidatorFixtureExpectation> BuildSliceCreationShapeMismatchFailsBeforeLlvmEmissionExpectation()
    {
        return
        [
            SsaValidatorFixtureExpectation.DiagnosticContaining("STK5002", ["slice creation from local 'values'", "known length"]),
        ];
    }

    private static IReadOnlyList<SsaValidatorFixtureExpectation> BuildIndirectLoadAddressShapeFailsBeforeLlvmEmissionExpectation()
    {
        return
        [
            SsaValidatorFixtureExpectation.DiagnosticContaining("STK5002", ["indirect load address", "must be a raw pointer", "i32"]),
        ];
    }

    private static IReadOnlyList<SsaValidatorFixtureExpectation> BuildIndirectStoreAcceptsQualifiedPointeeShapeExpectation()
    {
        return [SsaValidatorFixtureExpectation.NoDiagnostics()];
    }

    private static IReadOnlyList<SsaValidatorFixtureExpectation> BuildCopyMemoryDestinationPointeeMismatchFailsBeforeLlvmEmissionExpectation()
    {
        return
        [
            SsaValidatorFixtureExpectation.DiagnosticContaining("STK5002", ["copy destination address pointee", "bool", "i32"]),
        ];
    }

    private static IReadOnlyList<SsaValidatorFixtureExpectation> BuildCopyMemorySourcePointeeMismatchFailsBeforeLlvmEmissionExpectation()
    {
        return
        [
            SsaValidatorFixtureExpectation.DiagnosticContaining("STK5002", ["copy source address pointee", "bool", "i32"]),
        ];
    }

    private static IReadOnlyList<SsaValidatorFixtureExpectation> BuildCopyMemoryWithoutConcreteLayoutFailsBeforeLlvmEmissionExpectation()
    {
        return
        [
            SsaValidatorFixtureExpectation.DiagnosticContaining("STK5002", ["copy memory", "concrete non-empty layout", "void"]),
        ];
    }

    private static IReadOnlyList<SsaValidatorFixtureExpectation> BuildCopyMemoryAllowsFixedArrayElementPointersExpectation()
    {
        return [SsaValidatorFixtureExpectation.NoDiagnostics()];
    }

    private static IReadOnlyList<SsaValidatorFixtureExpectation> BuildScopedNoAliasProofCarrierAcceptsMatchingParameterRootsExpectation()
    {
        return [SsaValidatorFixtureExpectation.NoDiagnostics()];
    }

    private static IReadOnlyList<SsaValidatorFixtureExpectation> BuildScopedNoAliasProofCarrierRootMismatchFailsBeforeLlvmEmissionExpectation()
    {
        return
        [
            SsaValidatorFixtureExpectation.DiagnosticContaining("STK5002", ["roots do not match", "runtime-disjoint-0"]),
            SsaValidatorFixtureExpectation.DiagnosticContaining("STK5002", ["unknown parameter memory-root key 'param:other'"]),
        ];
    }

    private static IReadOnlyList<SsaValidatorFixtureExpectation> BuildScopedNoAliasProofCarrierDuplicateRootsFailBeforeLlvmEmissionExpectation()
    {
        return
        [
            SsaValidatorFixtureExpectation.DiagnosticContaining("STK5002", ["contains blank or duplicate memory roots"]),
        ];
    }

    private static IReadOnlyList<SsaValidatorFixtureExpectation> BuildScopedNoAliasProofCarrierIdMismatchFailsBeforeLlvmEmissionExpectation()
    {
        return
        [
            SsaValidatorFixtureExpectation.DiagnosticContaining("STK5002", ["alias proof carrier 'wrong-proof'", "does not match scoped noalias group 'runtime-disjoint-0'"]),
        ];
    }

    private static IReadOnlyList<SsaValidatorFixtureExpectation> BuildScopedNoAliasProofCarrierUnknownParameterRootFailsBeforeLlvmEmissionExpectation()
    {
        return
        [
            SsaValidatorFixtureExpectation.DiagnosticContaining("STK5002", ["unknown parameter memory-root key 'param:missing'"]),
        ];
    }

    private static IReadOnlyList<SsaValidatorFixtureExpectation> BuildStringConstantNonTextTypeFailsBeforeLlvmEmissionExpectation()
    {
        return
        [
            SsaValidatorFixtureExpectation.DiagnosticContaining("STK5002", ["string constant", "ascii/unicode", "i32"]),
        ];
    }

    private static IReadOnlyList<SsaValidatorFixtureExpectation> BuildTextDataAddressPointeeMismatchFailsBeforeLlvmEmissionExpectation()
    {
        return
        [
            SsaValidatorFixtureExpectation.DiagnosticContaining("STK5002", ["text data address result pointee", "i32", "i8"]),
        ];
    }

    private static IReadOnlyList<SsaValidatorFixtureExpectation> BuildGlobalAddressPointeeMismatchFailsBeforeLlvmEmissionExpectation()
    {
        return
        [
            SsaValidatorFixtureExpectation.DiagnosticContaining("STK5002", ["global address result pointee", "i32", "bool"]),
        ];
    }

    private static IReadOnlyList<SsaValidatorFixtureExpectation> BuildUnknownGlobalLoadFailsBeforeLlvmEmissionExpectation()
    {
        return
        [
            SsaValidatorFixtureExpectation.DiagnosticContaining("STK5002", ["global load", "unknown global 'Missing'"]),
        ];
    }

    private static IReadOnlyList<SsaValidatorFixtureExpectation> BuildKnownGlobalLoadTypeMismatchFailsBeforeLlvmEmissionExpectation()
    {
        return
        [
            SsaValidatorFixtureExpectation.DiagnosticContaining("STK5002", ["global load 'Flag' result", "i32", "bool"]),
        ];
    }

    private static IReadOnlyList<SsaValidatorFixtureExpectation> BuildKnownGlobalAddressTypeMismatchFailsBeforeLlvmEmissionExpectation()
    {
        return
        [
            SsaValidatorFixtureExpectation.DiagnosticContaining("STK5002", ["global address 'Flag' pointee", "i32", "bool"]),
        ];
    }

    private static IReadOnlyList<SsaValidatorFixtureExpectation> BuildStoreToImmutableGlobalFailsBeforeLlvmEmissionExpectation()
    {
        return
        [
            SsaValidatorFixtureExpectation.DiagnosticContaining("STK5002", ["global store target 'Limit'", "must be mutable", "Const"]),
        ];
    }

    private static IReadOnlyList<SsaValidatorFixtureExpectation> BuildStoreGlobalValueMismatchFailsBeforeLlvmEmissionExpectation()
    {
        return
        [
            SsaValidatorFixtureExpectation.DiagnosticContaining("STK5002", ["global store 'Counter' value", "bool", "i32"]),
        ];
    }

    private static IReadOnlyList<SsaValidatorFixtureExpectation> BuildStoreMutableKnownGlobalPassesSsaValidationExpectation()
    {
        return [SsaValidatorFixtureExpectation.NoDiagnostics()];
    }

    private static IReadOnlyList<SsaValidatorFixtureExpectation> BuildFunctionAddressNonFunctionPointerTypeFailsBeforeLlvmEmissionExpectation()
    {
        return
        [
            SsaValidatorFixtureExpectation.DiagnosticContaining("STK5002", ["function address 'Run'", "function-pointer type", "rawptr<i32>"]),
        ];
    }

    private static IReadOnlyList<SsaValidatorFixtureExpectation> BuildFunctionAddressMissingAbiFailsBeforeLlvmEmissionExpectation()
    {
        return
        [
            SsaValidatorFixtureExpectation.DiagnosticContaining("STK5002", ["function address target 'Missing'", "missing ABI lowering"]),
        ];
    }

    private static IReadOnlyList<SsaValidatorFixtureExpectation> BuildFunctionAddressSignatureMismatchFailsBeforeLlvmEmissionExpectation()
    {
        return
        [
            SsaValidatorFixtureExpectation.DiagnosticContaining("STK5002", ["function address 'Read' return type", "bool", "i32"]),
            SsaValidatorFixtureExpectation.DiagnosticContaining("STK5002", ["function address 'Read' parameter count mismatch", "expected 1", "got 0"]),
        ];
    }

    private static IReadOnlyList<SsaValidatorFixtureExpectation> BuildSystemMathBuiltinInvalidSignatureFailsBeforeLlvmEmissionExpectation()
    {
        return
        [
            SsaValidatorFixtureExpectation.DiagnosticContaining("STK5002", ["System.Math builtin 'Sin'", "floating-point return type"]),
        ];
    }

    private static IReadOnlyList<SsaValidatorFixtureExpectation> BuildSystemBitOperationsBuiltinInvalidWidthFailsBeforeLlvmEmissionExpectation()
    {
        return
        [
            SsaValidatorFixtureExpectation.DiagnosticContaining("STK5002", ["System.BitOperations builtin 'PopCount'", "supports only 'i32' and 'i64'"]),
        ];
    }

    private static IReadOnlyList<SsaValidatorFixtureExpectation> BuildSystemMemoryBuiltinInvalidAllocationShapeFailsBeforeLlvmEmissionExpectation()
    {
        return
        [
            SsaValidatorFixtureExpectation.DiagnosticContaining("STK5002", ["System.Memory Allocation", "Pointer, ByteLength, Alignment, and Allocator fields"]),
        ];
    }

    private static IReadOnlyList<SsaValidatorFixtureExpectation> BuildSystemCollectionsDictionaryKeyAsciiKeyPassesBuiltinValidationExpectation()
    {
        return [SsaValidatorFixtureExpectation.NoDiagnosticMatching("STK5002", ["System.Collections DictionaryKey builtin"])];
    }

    private static IReadOnlyList<SsaValidatorFixtureExpectation> BuildSystemCollectionsDictionaryKeyUnsupportedKeyFailsBeforeLlvmEmissionExpectation()
    {
        return
        [
            SsaValidatorFixtureExpectation.DiagnosticContaining("STK5002", ["System.Collections DictionaryKey builtin", "does not support key type 'i8[]'"]),
        ];
    }

    private static IReadOnlyList<SsaValidatorFixtureExpectation> BuildSystemRuntimeByteSlicePartsMutableMismatchFailsBeforeLlvmEmissionExpectation()
    {
        return
        [
            SsaValidatorFixtureExpectation.DiagnosticContaining("STK5002", ["System.Runtime byte slice parts builtin 'GetMutableByteSliceParts'", "mut borrow i8[]"]),
        ];
    }

    private static IReadOnlyList<SsaValidatorFixtureExpectation> BuildValidSystemBuiltinSignaturesPassSsaValidationExpectation()
    {
        return [SsaValidatorFixtureExpectation.NoDiagnostics()];
    }

    private static IReadOnlyList<CompilerDiagnostic> UndefinedSsaValueReferenceFailsBeforeLlvmEmission()
    {

        var function = BuildVoidFunction([
            new SsaValueInstruction(
                "v0",
                new SsaBinaryRValue(
                    SsaBinaryOperator.Add,
                    new SsaValueReference("missing", I32),
                    new SsaIntegerConstant(1, I32),
                    I32,
                    "missing + 1"))
        ]);

        var diagnostics = Validate(function);

        return diagnostics;
    }

    private static IReadOnlyList<CompilerDiagnostic> UnsupportedSsaConversionFailsBeforeLlvmEmission()
    {

        var function = BuildVoidFunction([
            new SsaValueInstruction(
                "v0",
                new SsaConvertRValue(
                    new SsaStringConstant("hello", StarkTypeSymbols.Ascii),
                    StarkTypeSymbols.Unicode,
                    "unicode(\"hello\")"))
        ]);

        var diagnostics = Validate(function);

        return diagnostics;
    }

    private static IReadOnlyList<CompilerDiagnostic> UnsizedIntegerConversionFailsBeforeLlvmEmission()
    {

        var function = BuildVoidFunction([
            new SsaValueInstruction(
                "v0",
                new SsaConvertRValue(
                    new SsaIntegerConstant(1, UnsizedInteger),
                    StarkTypeSymbols.Float(32),
                    "f32(1)"))
        ]);

        var diagnostics = Validate(function);

        return diagnostics;
    }

    private static IReadOnlyList<CompilerDiagnostic> CompileTimeIntegerConstantFailsBeforeLlvmEmission()
    {

        var function = BuildReturningFunction(
            StarkTypeSymbols.CompileTimeInteger,
            new SsaIntegerConstant(BigInteger.One << 1024, StarkTypeSymbols.CompileTimeInteger));

        var diagnostics = Validate(function);

        return diagnostics;
    }

    private static IReadOnlyList<CompilerDiagnostic> IntegerConstantOutsideStorageFailsBeforeLlvmEmission()
    {

        var i8 = StarkTypeSymbols.Integer(8);
        var function = BuildReturningFunction(
            i8,
            new SsaIntegerConstant(300, i8));

        var diagnostics = Validate(function);

        return diagnostics;
    }

    private static IReadOnlyList<CompilerDiagnostic> IntegerConstantOutsideEffectiveRangeFailsBeforeLlvmEmission()
    {

        var ranged = StarkTypeSymbols.Integer(8, 0, 10);
        var function = BuildReturningFunction(
            ranged,
            new SsaIntegerConstant(44, ranged));

        var diagnostics = Validate(function);

        return diagnostics;
    }

    private static IReadOnlyList<CompilerDiagnostic> UnsupportedFloatConversionTargetFailsBeforeLlvmEmission()
    {

        var function = BuildVoidFunction([
            new SsaValueInstruction(
                "v0",
                new SsaConvertRValue(
                    new SsaIntegerConstant(1, I32),
                    StarkTypeSymbols.Float(24),
                    "f24(1)"))
        ]);

        var diagnostics = Validate(function);

        return diagnostics;
    }

    private static IReadOnlyList<CompilerDiagnostic> DirectCallAbiMismatchFailsBeforeLlvmEmission()
    {

        var function = BuildVoidFunction([
            new SsaValueInstruction(
                "v0",
                new SsaCallRValue(
                    "Add",
                    [new SsaIntegerConstant(1, I32)],
                    I32,
                    "Add(1)"))
        ]);
        var abi = BuildAbiModel(new AbiFunctionSignature(
            "Add",
            "Add",
            I32,
            I32,
            [
                new AbiParameterSymbol("left", "left", I32, I32, AbiParameterKind.Direct),
                new AbiParameterSymbol("right", "right", I32, I32, AbiParameterKind.Direct)
            ],
            IsFfi: false));

        var diagnostics = Validate(function, abi);

        return diagnostics;
    }

    private static IReadOnlyList<CompilerDiagnostic> DirectCallIndirectAddressOnDirectParameterFailsBeforeLlvmEmission()
    {

        var address = new SsaGlobalAddressValue("value", I32, StarkTypeSymbols.RawPointer(I32, isMutable: true));
        var function = BuildVoidFunction([
            new SsaValueInstruction(
                "v0",
                new SsaCallRValue(
                    "Identity",
                    [new SsaIntegerConstant(1, I32)],
                    I32,
                    "Identity(1)",
                    IndirectArgumentAddresses: [address]))
        ]);
        var abi = BuildAbiModel(new AbiFunctionSignature(
            "Identity",
            "Identity",
            I32,
            I32,
            [new AbiParameterSymbol("value", "value", I32, I32, AbiParameterKind.Direct)],
            IsFfi: false));

        var diagnostics = Validate(function, abi);

        return diagnostics;
    }

    private static IReadOnlyList<CompilerDiagnostic> DirectCallIndirectAddressPointeeMismatchFailsBeforeLlvmEmission()
    {

        var boolPointer = StarkTypeSymbols.RawPointer(StarkTypeSymbols.Bool, isMutable: true);
        var address = new SsaGlobalAddressValue("flag", StarkTypeSymbols.Bool, boolPointer);
        var function = BuildVoidFunction([
            new SsaValueInstruction(
                "v0",
                new SsaCallRValue(
                    "Consume",
                    [new SsaIntegerConstant(1, I32)],
                    I32,
                    "Consume(1)",
                    IndirectArgumentAddresses: [address]))
        ]);
        var abi = BuildAbiModel(new AbiFunctionSignature(
            "Consume",
            "Consume",
            I32,
            I32,
            [
                new AbiParameterSymbol(
                    "value",
                    "value",
                    I32,
                    StarkTypeSymbols.RawPointer(I32, isMutable: false),
                    AbiParameterKind.IndirectIn)
            ],
            IsFfi: false));

        var diagnostics = Validate(function, abi);

        return diagnostics;
    }

    private static IReadOnlyList<CompilerDiagnostic> DirectCallPromotedUnknownLocalFailsBeforeLlvmEmission()
    {

        var function = BuildVoidFunction([
            new SsaValueInstruction(
                "v0",
                new SsaCallRValue(
                    "Consume",
                    [new SsaIntegerConstant(1, I32)],
                    I32,
                    "Consume(1)",
                    IndirectArgumentLocalNames: ["missing"]))
        ]);
        var abi = BuildAbiModel(new AbiFunctionSignature(
            "Consume",
            "Consume",
            I32,
            I32,
            [
                new AbiParameterSymbol(
                    "value",
                    "value",
                    I32,
                    StarkTypeSymbols.RawPointer(I32, isMutable: false),
                    AbiParameterKind.IndirectIn)
            ],
            IsFfi: false));

        var diagnostics = Validate(function, abi);

        return diagnostics;
    }

    private static IReadOnlyList<CompilerDiagnostic> DirectCallIndirectAddressAndPromotedStorageShapesAreAccepted()
    {

        var parameter = new TypedParameterSymbol("input", I32);
        var address = new SsaGlobalAddressValue("value", I32, StarkTypeSymbols.RawPointer(I32, isMutable: true));
        var function = BuildVoidFunction(
            [
                new SsaAllocateLocalInstruction("scratch", I32),
                new SsaValueInstruction(
                    "v0",
                    new SsaCallRValue(
                        "Consume",
                        [new SsaIntegerConstant(1, I32)],
                        I32,
                        "Consume(1)",
                        IndirectArgumentAddresses: [address])),
                new SsaValueInstruction(
                    "v1",
                    new SsaCallRValue(
                        "Consume",
                        [new SsaIntegerConstant(2, I32)],
                        I32,
                        "Consume(2)",
                        IndirectArgumentLocalNames: ["scratch"])),
                new SsaValueInstruction(
                    "v2",
                    new SsaCallRValue(
                        "Consume",
                        [new SsaValueReference("arg_input", I32)],
                        I32,
                        "Consume(input)",
                        IndirectArgumentLocalNames: ["input"]))
            ],
            [parameter]);
        var abi = BuildAbiModel(
            new AbiFunctionSignature(
                "Run",
                "Run",
                StarkTypeSymbols.Void,
                StarkTypeSymbols.Void,
                [new AbiParameterSymbol("input", "arg_input", I32, I32, AbiParameterKind.Direct)],
                IsFfi: false),
            new AbiFunctionSignature(
                "Consume",
                "Consume",
                I32,
                I32,
                [
                    new AbiParameterSymbol(
                        "value",
                        "value",
                        I32,
                        StarkTypeSymbols.RawPointer(I32, isMutable: false),
                        AbiParameterKind.IndirectIn)
                ],
                IsFfi: false));

        var diagnostics = Validate(function, abi);

        return diagnostics;
    }

    private static IReadOnlyList<CompilerDiagnostic> IndirectCallIndirectAddressPointeeMismatchFailsBeforeLlvmEmission()
    {

        var arrayType = StarkTypeSymbols.FixedArray(I32, fixedLength: 5);
        var functionPointerType = StarkTypeSymbols.FunctionPointer(
            StarkFunctionKind.Fn,
            I32,
            [arrayType]);
        var boolPointer = StarkTypeSymbols.RawPointer(StarkTypeSymbols.Bool, isMutable: true);
        var address = new SsaGlobalAddressValue("flag", StarkTypeSymbols.Bool, boolPointer);
        var function = BuildVoidFunction([
            new SsaValueInstruction(
                "v0",
                new SsaIndirectCallRValue(
                    new SsaFunctionAddressValue("Consume", functionPointerType),
                    [new SsaZeroInitializerValue(arrayType)],
                    I32,
                    "op(value)",
                    IndirectArgumentAddresses: [address]))
        ]);

        var diagnostics = Validate(function);

        return diagnostics;
    }

    private static IReadOnlyList<CompilerDiagnostic> IndirectCallLargeByvalAddressShapeIsAccepted()
    {

        var arrayType = StarkTypeSymbols.FixedArray(I32, fixedLength: 5);
        var functionPointerType = StarkTypeSymbols.FunctionPointer(
            StarkFunctionKind.Fn,
            I32,
            [arrayType]);
        var address = new SsaGlobalAddressValue("values", arrayType, StarkTypeSymbols.RawPointer(arrayType, isMutable: true));
        var function = BuildVoidFunction([
            new SsaValueInstruction(
                "v0",
                new SsaIndirectCallRValue(
                    new SsaFunctionAddressValue("Consume", functionPointerType),
                    [new SsaZeroInitializerValue(arrayType)],
                    I32,
                    "op(values)",
                    IndirectArgumentAddresses: [address]))
        ]);
        var abi = BuildAbiModel(new AbiFunctionSignature(
            "Consume",
            "Consume",
            I32,
            I32,
            [
                new AbiParameterSymbol(
                    "value",
                    "value",
                    arrayType,
                    StarkTypeSymbols.RawPointer(arrayType, isMutable: false),
                    AbiParameterKind.IndirectIn)
            ],
            IsFfi: false));

        var diagnostics = Validate(function, abi);

        return diagnostics;
    }

    private static IReadOnlyList<CompilerDiagnostic> FfiTextReturnFailsBeforeLlvmEmission()
    {

        var function = BuildVoidFunction([
            new SsaValueInstruction(
                "v0",
                new SsaCallRValue(
                    "CurrentName",
                    [],
                    StarkTypeSymbols.Ascii,
                    "CurrentName()",
                    SourceReturnType: StarkTypeSymbols.Ascii))
        ]);
        var abi = BuildAbiModel(new AbiFunctionSignature(
            "CurrentName",
            "CurrentName",
            StarkTypeSymbols.Ascii,
            StarkTypeSymbols.RawPointer(StarkTypeSymbols.Integer(8), isMutable: false),
            [],
            IsFfi: true));

        var diagnostics = Validate(function, abi);

        return diagnostics;
    }

    private static IReadOnlyList<CompilerDiagnostic> MirOnlyTempLocalStorageFailsBeforeLlvmEmission()
    {

        var function = BuildVoidFunction([
            new SsaAllocateLocalInstruction("scratch", I32, StorageClass: "temp")
        ]);

        var diagnostics = Validate(function);

        return diagnostics;
    }

    private static IReadOnlyList<CompilerDiagnostic> NonHeapDeallocationFailsBeforeLlvmEmission()
    {

        var function = BuildVoidFunction([
            new SsaAllocateLocalInstruction("scratch", I32),
            new SsaDeallocateLocalInstruction("scratch", I32, StorageClass: "stack")
        ]);

        var diagnostics = Validate(function);

        return diagnostics;
    }

    private static IReadOnlyList<CompilerDiagnostic> ArenaFrameInstructionsAreAcceptedAroundArenaAllocation()
    {

        var function = BuildVoidFunction([
            new SsaArenaFrameEnterInstruction(),
            new SsaAllocateLocalInstruction("scratch", I32, StorageClass: "arena"),
            new SsaArenaFrameLeaveInstruction()
        ]);

        var diagnostics = Validate(function);

        return diagnostics;
    }

    private static IReadOnlyList<CompilerDiagnostic> ArenaAllocationWithoutFrameScopeFailsBeforeLlvmEmission()
    {

        var function = BuildVoidFunction([
            new SsaAllocateLocalInstruction("scratch", I32, StorageClass: "arena")
        ]);

        var diagnostics = Validate(function);

        return diagnostics;
    }

    private static IReadOnlyList<CompilerDiagnostic> MisplacedArenaFrameInstructionsFailBeforeLlvmEmission()
    {

        var function = BuildVoidFunction([
            new SsaAllocateLocalInstruction("scratch", I32, StorageClass: "arena"),
            new SsaArenaFrameEnterInstruction(),
            new SsaArenaFrameLeaveInstruction()
        ]);

        var diagnostics = Validate(function);

        return diagnostics;
    }

    private static IReadOnlyList<CompilerDiagnostic> LocalUseWithoutAllocationFailsBeforeLlvmEmission()
    {

        var function = BuildVoidFunction([
            new SsaStoreLocalInstruction(
                "scratch",
                I32,
                new SsaIntegerConstant(1, I32))
        ]);

        var diagnostics = Validate(function);

        return diagnostics;
    }

    private static IReadOnlyList<CompilerDiagnostic> FunctionMissingAbiFailsBeforeLlvmEmission()
    {

        var function = BuildVoidFunction([]);
        var abi = new AbiModel("Demo", new Dictionary<string, AbiFunctionSignature>(StringComparer.Ordinal));

        var diagnostics = Validate(function, abi);

        return diagnostics;
    }

    private static IReadOnlyList<CompilerDiagnostic> DuplicateSsaValueDefinitionFailsBeforeLlvmEmission()
    {

        var function = BuildVoidFunction([
            new SsaValueInstruction(
                "v0",
                new SsaUseRValue(new SsaIntegerConstant(1, I32))),
            new SsaValueInstruction(
                "v0",
                new SsaUseRValue(new SsaIntegerConstant(2, I32)))
        ]);

        var diagnostics = Validate(function);

        return diagnostics;
    }

    private static IReadOnlyList<CompilerDiagnostic> PhiIncomingTypeMismatchFailsBeforeLlvmEmission()
    {

        var function = BuildVoidMultiBlockFunction(
            new SsaBasicBlock(
                0,
                "entry",
                Phis: [],
                Instructions: [],
                Terminator: new SsaTerminator(SsaTerminatorKind.Goto, [1])),
            new SsaBasicBlock(
                1,
                "join",
                Phis:
                [
                    new SsaPhi(
                        "v0",
                        "value",
                        I32,
                        [new SsaPhiIncoming(0, new SsaBoolConstant(true))])
                ],
                Instructions: [],
                Terminator: new SsaTerminator(SsaTerminatorKind.Return, [])));

        var diagnostics = Validate(function);

        return diagnostics;
    }

    private static IReadOnlyList<CompilerDiagnostic> PhiIncomingFromNonPredecessorFailsBeforeLlvmEmission()
    {

        var function = BuildVoidMultiBlockFunction(
            new SsaBasicBlock(
                0,
                "entry",
                Phis: [],
                Instructions: [],
                Terminator: new SsaTerminator(SsaTerminatorKind.Goto, [1])),
            new SsaBasicBlock(
                1,
                "join",
                Phis:
                [
                    new SsaPhi(
                        "v0",
                        "value",
                        I32,
                        [new SsaPhiIncoming(2, new SsaIntegerConstant(1, I32))])
                ],
                Instructions: [],
                Terminator: new SsaTerminator(SsaTerminatorKind.Return, [])),
            new SsaBasicBlock(
                2,
                "other",
                Phis: [],
                Instructions: [],
                Terminator: new SsaTerminator(SsaTerminatorKind.Return, [])));

        var diagnostics = Validate(function);

        return diagnostics;
    }

    private static IReadOnlyList<CompilerDiagnostic> PhiMissingIncomingFailsBeforeLlvmEmission()
    {

        var function = BuildVoidMultiBlockFunction(
            new SsaBasicBlock(
                0,
                "entry",
                Phis: [],
                Instructions: [],
                Terminator: new SsaTerminator(
                    SsaTerminatorKind.Branch,
                    [2, 1],
                    Condition: new SsaBoolConstant(true))),
            new SsaBasicBlock(
                1,
                "side",
                Phis: [],
                Instructions: [],
                Terminator: new SsaTerminator(SsaTerminatorKind.Goto, [2])),
            new SsaBasicBlock(
                2,
                "join",
                Phis:
                [
                    new SsaPhi(
                        "v0",
                        "value",
                        I32,
                        [new SsaPhiIncoming(0, new SsaIntegerConstant(1, I32))])
                ],
                Instructions: [],
                Terminator: new SsaTerminator(SsaTerminatorKind.Return, [])));

        var diagnostics = Validate(function);

        return diagnostics;
    }

    private static IReadOnlyList<CompilerDiagnostic> PhiDuplicateIncomingFailsBeforeLlvmEmission()
    {

        var function = BuildVoidMultiBlockFunction(
            new SsaBasicBlock(
                0,
                "entry",
                Phis: [],
                Instructions: [],
                Terminator: new SsaTerminator(SsaTerminatorKind.Goto, [1])),
            new SsaBasicBlock(
                1,
                "join",
                Phis:
                [
                    new SsaPhi(
                        "v0",
                        "value",
                        I32,
                        [
                            new SsaPhiIncoming(0, new SsaIntegerConstant(1, I32)),
                            new SsaPhiIncoming(0, new SsaIntegerConstant(2, I32))
                        ])
                ],
                Instructions: [],
                Terminator: new SsaTerminator(SsaTerminatorKind.Return, [])));

        var diagnostics = Validate(function);

        return diagnostics;
    }

    private static IReadOnlyList<CompilerDiagnostic> PhiWithoutIncomingFailsBeforeLlvmEmission()
    {

        var function = BuildVoidMultiBlockFunction(
            new SsaBasicBlock(
                0,
                "entry",
                Phis: [],
                Instructions: [],
                Terminator: new SsaTerminator(SsaTerminatorKind.Goto, [1])),
            new SsaBasicBlock(
                1,
                "join",
                Phis:
                [
                    new SsaPhi(
                        "v0",
                        "value",
                        I32,
                        [])
                ],
                Instructions: [],
                Terminator: new SsaTerminator(SsaTerminatorKind.Return, [])));

        var diagnostics = Validate(function);

        return diagnostics;
    }

    private static IReadOnlyList<CompilerDiagnostic> FunctionAbiReturnMismatchFailsBeforeLlvmEmission()
    {

        var function = BuildReturningFunction(I32, new SsaIntegerConstant(0, I32));
        var abi = BuildAbiModel(new AbiFunctionSignature(
            "Run",
            "Run",
            StarkTypeSymbols.Bool,
            StarkTypeSymbols.Bool,
            [],
            IsFfi: false));

        var diagnostics = Validate(function, abi);

        return diagnostics;
    }

    private static IReadOnlyList<CompilerDiagnostic> FunctionAbiRetborrowPointerReturnIsAccepted()
    {

        var retborrowI32 = I32 with
        {
            BorrowKind = StarkBorrowKind.RetBorrow,
            IsMutableView = true,
            DisplayName = "retborrow mut i32"
        };
        var pointerType = StarkTypeSymbols.RawPointer(I32, isMutable: true);
        var function = BuildReturningFunction(
            retborrowI32,
            new SsaGlobalAddressValue("value", I32, pointerType));
        var abi = BuildAbiModel(new AbiFunctionSignature(
            "Run",
            "Run",
            retborrowI32,
            pointerType,
            [],
            IsFfi: false));

        var diagnostics = Validate(function, abi);

        return diagnostics;
    }

    private static IReadOnlyList<CompilerDiagnostic> FunctionAbiParameterCountMismatchFailsBeforeLlvmEmission()
    {

        var function = BuildVoidFunction([], parameters: [new TypedParameterSymbol("value", I32)]);
        var abi = BuildAbiModel(new AbiFunctionSignature(
            "Run",
            "Run",
            StarkTypeSymbols.Void,
            StarkTypeSymbols.Void,
            [],
            IsFfi: false));

        var diagnostics = Validate(function, abi);

        return diagnostics;
    }

    private static IReadOnlyList<CompilerDiagnostic> FunctionAbiParameterTypeMismatchFailsBeforeLlvmEmission()
    {

        var function = BuildVoidFunction([], parameters: [new TypedParameterSymbol("value", I32)]);
        var abi = BuildAbiModel(new AbiFunctionSignature(
            "Run",
            "Run",
            StarkTypeSymbols.Void,
            StarkTypeSymbols.Void,
            [
                new AbiParameterSymbol(
                    "value",
                    "value",
                    StarkTypeSymbols.Bool,
                    StarkTypeSymbols.Bool,
                    AbiParameterKind.Direct)
            ],
            IsFfi: false));

        var diagnostics = Validate(function, abi);

        return diagnostics;
    }

    private static IReadOnlyList<CompilerDiagnostic> FunctionAbiSretShapeMismatchFailsBeforeLlvmEmission()
    {

        var arrayType = StarkTypeSymbols.FixedArray(I32, fixedLength: 4);
        var function = BuildReturningFunction(arrayType, new SsaZeroInitializerValue(arrayType));
        var abi = BuildAbiModel(new AbiFunctionSignature(
            "Run",
            "Run",
            arrayType,
            StarkTypeSymbols.Void,
            [
                new AbiParameterSymbol(
                    "ret",
                    "ret",
                    StarkTypeSymbols.Bool,
                    StarkTypeSymbols.RawPointer(StarkTypeSymbols.Bool, isMutable: true),
                    AbiParameterKind.SRet)
            ],
            IsFfi: false));

        var diagnostics = Validate(function, abi);

        return diagnostics;
    }

    private static IReadOnlyList<CompilerDiagnostic> FunctionAbiSretShapeAccepted()
    {

        var arrayType = StarkTypeSymbols.FixedArray(I32, fixedLength: 4);
        var function = BuildReturningFunction(arrayType, new SsaZeroInitializerValue(arrayType));
        var abi = BuildAbiModel(new AbiFunctionSignature(
            "Run",
            "Run",
            arrayType,
            StarkTypeSymbols.Void,
            [
                new AbiParameterSymbol(
                    "ret",
                    "ret",
                    arrayType,
                    StarkTypeSymbols.RawPointer(arrayType, isMutable: true),
                    AbiParameterKind.SRet)
            ],
            IsFfi: false));

        var diagnostics = Validate(function, abi);

        return diagnostics;
    }

    private static IReadOnlyList<CompilerDiagnostic> AddressOfParameterMissingAbiUserParameterFailsBeforeLlvmEmission()
    {

        var valueParameter = new TypedParameterSymbol("value", I32);
        var function = BuildVoidFunction(
            [
                new SsaValueInstruction(
                    "v0",
                    new SsaAddressOfParameterRValue(
                        "value",
                        I32,
                        StarkTypeSymbols.RawPointer(I32, isMutable: true),
                        "&value"))
            ],
            [valueParameter]);
        var abi = BuildAbiModel(new AbiFunctionSignature(
            "Run",
            "Run",
            StarkTypeSymbols.Void,
            StarkTypeSymbols.Void,
            [],
            IsFfi: false));

        var diagnostics = Validate(function, abi);

        return diagnostics;
    }

    private static IReadOnlyList<CompilerDiagnostic> UnsupportedSsaInstructionFailsBeforeLlvmEmission()
    {

        var function = BuildVoidFunction([new UnsupportedSsaInstruction()]);

        var diagnostics = Validate(function);

        return diagnostics;
    }

    private static IReadOnlyList<CompilerDiagnostic> UnsupportedSsaRValueFailsBeforeLlvmEmission()
    {

        var function = BuildVoidFunction([
            new SsaValueInstruction("v0", new UnsupportedSsaRValue())
        ]);

        var diagnostics = Validate(function);

        return diagnostics;
    }

    private static IReadOnlyList<CompilerDiagnostic> UnsupportedSsaValueFailsBeforeLlvmEmission()
    {

        var function = BuildVoidFunction([
            new SsaValueInstruction("v0", new SsaUseRValue(new UnsupportedSsaValue()))
        ]);

        var diagnostics = Validate(function);

        return diagnostics;
    }

    private static IReadOnlyList<CompilerDiagnostic> UnsupportedSsaTerminatorFailsBeforeLlvmEmission()
    {

        var function = BuildVoidFunction(
            [],
            terminator: new SsaTerminator((SsaTerminatorKind)999, []));

        var diagnostics = Validate(function);

        return diagnostics;
    }

    private static IReadOnlyList<CompilerDiagnostic> DynamicStorageElementLayoutFailsBeforeLlvmEmission()
    {

        var function = BuildVoidFunction([
            new SsaValueInstruction(
                "v0",
                new SsaDynamicStorageAllocationRValue(
                    new SsaIntegerConstant(1, I32),
                    StarkTypeSymbols.Dynamic(StarkTypeSymbols.Void),
                    DynamicStorageAllocationKind.Runtime,
                    "new dynamic void"))
        ]);

        var diagnostics = Validate(function);

        return diagnostics;
    }

    private static IReadOnlyList<CompilerDiagnostic> DynamicStorageCapacityWidthFailsBeforeLlvmEmission()
    {

        var i128 = StarkTypeSymbols.Integer(128);
        var function = BuildVoidFunction([
            new SsaValueInstruction(
                "v0",
                new SsaDynamicStorageAllocationRValue(
                    new SsaIntegerConstant(1, i128),
                    StarkTypeSymbols.Dynamic(I32),
                    DynamicStorageAllocationKind.Runtime,
                    "new dynamic i32"))
        ]);

        var diagnostics = Validate(function);

        return diagnostics;
    }

    private static IReadOnlyList<CompilerDiagnostic> DynamicStorageReserveAddressShapeFailsBeforeLlvmEmission()
    {

        var function = BuildVoidFunction([
            new SsaValueInstruction(
                "v0",
                new SsaDynamicStorageReserveRValue(
                    new SsaIntegerConstant(0, I32),
                    StarkTypeSymbols.Dynamic(I32),
                    new SsaIntegerConstant(1, I32),
                    DynamicStorageAllocationKind.Runtime,
                    "reserve"))
        ]);

        var diagnostics = Validate(function);

        return diagnostics;
    }

    private static IReadOnlyList<CompilerDiagnostic> DynamicStorageMoveAtAddressPointeeMismatchFailsBeforeLlvmEmission()
    {

        var boolStorage = StarkTypeSymbols.Dynamic(StarkTypeSymbols.Bool);
        var i32Storage = StarkTypeSymbols.Dynamic(I32);
        var function = BuildVoidFunction([
            new SsaValueInstruction(
                "v0",
                new SsaDynamicStorageMoveAtRValue(
                    new SsaGlobalAddressValue("values", boolStorage, StarkTypeSymbols.RawPointer(boolStorage, isMutable: true)),
                    i32Storage,
                    new SsaIntegerConstant(0, I32),
                    I32,
                    "move_at"))
        ]);

        var diagnostics = Validate(function);

        return diagnostics;
    }

    private static IReadOnlyList<CompilerDiagnostic> ElementAddressBasePointeeMismatchFailsBeforeLlvmEmission()
    {

        var boolArray = StarkTypeSymbols.FixedArray(StarkTypeSymbols.Bool, fixedLength: 4);
        var i32Array = StarkTypeSymbols.FixedArray(I32, fixedLength: 4);
        var function = BuildVoidFunction([
            new SsaValueInstruction(
                "v0",
                new SsaElementAddressRValue(
                    new SsaGlobalAddressValue("values", boolArray, StarkTypeSymbols.RawPointer(boolArray, isMutable: true)),
                    i32Array,
                    new SsaIntegerConstant(0, I32),
                    ConstantIndex: null,
                    StarkTypeSymbols.RawPointer(I32, isMutable: true),
                    "values[index]"))
        ]);

        var diagnostics = Validate(function);

        return diagnostics;
    }

    private static IReadOnlyList<CompilerDiagnostic> ElementAddressResultPointeeMismatchFailsBeforeLlvmEmission()
    {

        var arrayType = StarkTypeSymbols.FixedArray(I32, fixedLength: 4);
        var function = BuildVoidFunction([
            new SsaValueInstruction(
                "v0",
                new SsaElementAddressRValue(
                    new SsaGlobalAddressValue("values", arrayType, StarkTypeSymbols.RawPointer(arrayType, isMutable: true)),
                    arrayType,
                    new SsaIntegerConstant(0, I32),
                    ConstantIndex: null,
                    StarkTypeSymbols.RawPointer(StarkTypeSymbols.Bool, isMutable: true),
                    "values[index]"))
        ]);

        var diagnostics = Validate(function);

        return diagnostics;
    }

    private static IReadOnlyList<CompilerDiagnostic> UnsupportedUnaryShapeFailsBeforeLlvmEmission()
    {

        var function = BuildVoidFunction([
            new SsaValueInstruction(
                "v0",
                new SsaUnaryRValue(
                    SsaUnaryOperator.LogicalNot,
                    new SsaIntegerConstant(1, I32),
                    I32,
                    "!1"))
        ]);

        var diagnostics = Validate(function);

        return diagnostics;
    }

    private static IReadOnlyList<CompilerDiagnostic> UnsizedIntegerUnaryFailsBeforeLlvmEmission()
    {

        var function = BuildVoidFunction([
            new SsaValueInstruction(
                "v0",
                new SsaUnaryRValue(
                    SsaUnaryOperator.Negate,
                    new SsaIntegerConstant(1, UnsizedInteger),
                    UnsizedInteger,
                    "-1"))
        ]);

        var diagnostics = Validate(function);

        return diagnostics;
    }

    private static IReadOnlyList<CompilerDiagnostic> UnsupportedFloatUnaryFailsBeforeLlvmEmission()
    {

        var f24 = StarkTypeSymbols.Float(24);
        var function = BuildVoidFunction([
            new SsaValueInstruction(
                "v0",
                new SsaUnaryRValue(
                    SsaUnaryOperator.Negate,
                    new SsaFloatConstant("1.0", f24),
                    f24,
                    "-1.0"))
        ]);

        var diagnostics = Validate(function);

        return diagnostics;
    }

    private static IReadOnlyList<CompilerDiagnostic> BinaryOperandShapeMismatchFailsBeforeLlvmEmission()
    {

        var function = BuildVoidFunction([
            new SsaValueInstruction(
                "v0",
                new SsaBinaryRValue(
                    SsaBinaryOperator.Add,
                    new SsaIntegerConstant(1, I32),
                    new SsaBoolConstant(true),
                    I32,
                    "1 + true"))
        ]);

        var diagnostics = Validate(function);

        return diagnostics;
    }

    private static IReadOnlyList<CompilerDiagnostic> WrappingFloatOperatorFailsBeforeLlvmEmission()
    {

        var f32 = StarkTypeSymbols.Float(32);
        var function = BuildVoidFunction([
            new SsaValueInstruction(
                "v0",
                new SsaBinaryRValue(
                    SsaBinaryOperator.WrappingAdd,
                    new SsaFloatConstant("1.0", f32),
                    new SsaFloatConstant("2.0", f32),
                    f32,
                    "1.0 +% 2.0"))
        ]);

        var diagnostics = Validate(function);

        return diagnostics;
    }

    private static IReadOnlyList<CompilerDiagnostic> UnsupportedFloatIntrinsicWidthFailsBeforeLlvmEmission()
    {

        var f24 = StarkTypeSymbols.Float(24);
        var function = BuildVoidFunction([
            new SsaValueInstruction(
                "v0",
                new SsaBinaryRValue(
                    SsaBinaryOperator.Exponent,
                    new SsaFloatConstant("2.0", f24),
                    new SsaFloatConstant("3.0", f24),
                    f24,
                    "2.0 ** 3.0"))
        ]);

        var diagnostics = Validate(function);

        return diagnostics;
    }

    private static IReadOnlyList<CompilerDiagnostic> ComparisonResultShapeMismatchFailsBeforeLlvmEmission()
    {

        var function = BuildVoidFunction([
            new SsaValueInstruction(
                "v0",
                new SsaBinaryRValue(
                    SsaBinaryOperator.Equal,
                    new SsaIntegerConstant(1, I32),
                    new SsaIntegerConstant(2, I32),
                    I32,
                    "1 == 2"))
        ]);

        var diagnostics = Validate(function);

        return diagnostics;
    }

    private static IReadOnlyList<CompilerDiagnostic> FixedArrayOrderedComparisonUnsupportedElementFailsBeforeHelperEmission()
    {

        var sliceArrayType = StarkTypeSymbols.FixedArray(StarkTypeSymbols.Slice(I32), fixedLength: 2);
        var function = BuildVoidFunction([
            new SsaValueInstruction(
                "v0",
                new SsaBinaryRValue(
                    SsaBinaryOperator.LessThan,
                    new SsaZeroInitializerValue(sliceArrayType),
                    new SsaZeroInitializerValue(sliceArrayType),
                    StarkTypeSymbols.Bool,
                    "left < right"))
        ]);

        var diagnostics = Validate(function);

        return diagnostics;
    }

    private static IReadOnlyList<CompilerDiagnostic> NamedOrderedComparisonUnsupportedFieldFailsBeforeHelperEmission()
    {

        var sliceField = new FieldSymbol("Items", StarkTypeSymbols.Slice(I32));
        var namedType = new NamedTypeSymbol(
            "BadComparable",
            DeclarationKind.Struct,
            new Dictionary<string, FieldSymbol>(StringComparer.Ordinal)
            {
                [sliceField.Name] = sliceField
            },
            [sliceField]);
        var valueType = StarkTypeSymbols.Named("BadComparable");
        var function = BuildVoidFunction([
            new SsaValueInstruction(
                "v0",
                new SsaBinaryRValue(
                    SsaBinaryOperator.GreaterThan,
                    new SsaZeroInitializerValue(valueType),
                    new SsaZeroInitializerValue(valueType),
                    StarkTypeSymbols.Bool,
                    "left > right"))
        ]);

        var diagnostics = Validate(function, typeModel: BuildTypeModel(namedType));

        return diagnostics;
    }

    private static IReadOnlyList<CompilerDiagnostic> ExtractFieldResultMismatchFailsBeforeLlvmEmission()
    {

        var pairType = StarkTypeSymbols.Named("Pair");
        var function = BuildVoidFunction([
            new SsaValueInstruction(
                "v0",
                new SsaExtractFieldRValue(
                    new SsaZeroInitializerValue(pairType),
                    "Flag",
                    1,
                    I32,
                    "pair.Flag"))
        ]);

        var diagnostics = Validate(function, typeModel: BuildTypeModel(BuildPairType()));

        return diagnostics;
    }

    private static IReadOnlyList<CompilerDiagnostic> ExtractFieldNameIndexMismatchFailsBeforeLlvmEmission()
    {

        var pairType = StarkTypeSymbols.Named("Pair");
        var function = BuildVoidFunction([
            new SsaValueInstruction(
                "v0",
                new SsaExtractFieldRValue(
                    new SsaZeroInitializerValue(pairType),
                    "Count",
                    0,
                    I32,
                    "pair.Count"))
        ]);

        var diagnostics = Validate(function, typeModel: BuildTypeModel(BuildPairType()));

        return diagnostics;
    }

    private static IReadOnlyList<CompilerDiagnostic> SliceViewIndexExtractionIsAccepted()
    {

        var sliceType = StarkTypeSymbols.Slice(I32);
        var function = BuildVoidFunction([
            new SsaValueInstruction(
                "v0",
                new SsaExtractIndexRValue(
                    new SsaZeroInitializerValue(sliceType),
                    0,
                    IndexedElementOperationFamily.ViewComponent,
                    StarkTypeSymbols.RawPointer(I32, isMutable: false),
                    "values.Data")),
            new SsaValueInstruction(
                "v1",
                new SsaExtractIndexRValue(
                    new SsaZeroInitializerValue(sliceType),
                    1,
                    IndexedElementOperationFamily.ViewComponent,
                    StarkTypeSymbols.Integer(64),
                    "values.Length"))
        ]);

        var diagnostics = Validate(function);

        return diagnostics;
    }

    private static IReadOnlyList<CompilerDiagnostic> SliceViewNoOpRetypeSupportsImmutableComponentExtraction()
    {

        var mutableSliceType = StarkTypeSymbols.ApplyQualifiers(StarkTypeSymbols.Slice(I32), isMutableView: true);
        var sliceType = StarkTypeSymbols.Slice(I32);
        var function = BuildVoidFunction([
            new SsaValueInstruction(
                "view",
                new SsaConvertRValue(
                    new SsaZeroInitializerValue(mutableSliceType),
                    sliceType,
                    "view:i32[]")),
            new SsaValueInstruction(
                "data",
                new SsaExtractIndexRValue(
                    new SsaValueReference("view", sliceType),
                    0,
                    IndexedElementOperationFamily.ViewComponent,
                    StarkTypeSymbols.RawPointer(I32, isMutable: false),
                    "view.Data"))
        ]);

        var diagnostics = Validate(function);

        return diagnostics;
    }

    private static IReadOnlyList<CompilerDiagnostic> TextViewIndexExtractionIsAccepted()
    {

        var function = BuildVoidFunction([
            new SsaValueInstruction(
                "v0",
                new SsaExtractIndexRValue(
                    new SsaStringConstant("abc", StarkTypeSymbols.Ascii),
                    0,
                    IndexedElementOperationFamily.ViewComponent,
                    StarkTypeSymbols.RawPointer(StarkTypeSymbols.Integer(8), isMutable: false),
                    "text.Data")),
            new SsaValueInstruction(
                "v1",
                new SsaExtractIndexRValue(
                    new SsaStringConstant("abc", StarkTypeSymbols.Ascii),
                    1,
                    IndexedElementOperationFamily.ViewComponent,
                    StarkTypeSymbols.Integer(64),
                    "text.Length"))
        ]);

        var diagnostics = Validate(function);

        return diagnostics;
    }

    private static IReadOnlyList<CompilerDiagnostic> TextViewFieldExtractionIsAccepted()
    {

        var function = BuildVoidFunction([
            new SsaValueInstruction(
                "v0",
                new SsaExtractFieldRValue(
                    new SsaStringConstant("abc", StarkTypeSymbols.Ascii),
                    "data",
                    0,
                    StarkTypeSymbols.RawPointer(StarkTypeSymbols.Integer(8), isMutable: false),
                    "text.data")),
            new SsaValueInstruction(
                "v1",
                new SsaExtractFieldRValue(
                    new SsaStringConstant("abc", StarkTypeSymbols.Ascii),
                    "length",
                    1,
                    StarkTypeSymbols.Integer(64),
                    "text.length"))
        ]);

        var diagnostics = Validate(function);

        return diagnostics;
    }

    private static IReadOnlyList<CompilerDiagnostic> SelectArmShapeMismatchFailsBeforeLlvmEmission()
    {

        var function = BuildVoidFunction([
            new SsaValueInstruction(
                "v0",
                new SsaSelectRValue(
                    new SsaBoolConstant(true),
                    new SsaIntegerConstant(1, I32),
                    new SsaBoolConstant(false),
                    I32,
                    "select"))
        ]);

        var diagnostics = Validate(function);

        return diagnostics;
    }

    private static IReadOnlyList<CompilerDiagnostic> UnsupportedSwitchConditionFailsBeforeLlvmEmission()
    {

        var function = BuildSwitchFunction(
            new SsaStringConstant("value", StarkTypeSymbols.Ascii),
            [new SsaSwitchCase("\"value\"", 1, new SsaStringConstant("value", StarkTypeSymbols.Ascii))]);

        var diagnostics = Validate(function);

        return diagnostics;
    }

    private static IReadOnlyList<CompilerDiagnostic> UnsizedIntegerSwitchConditionFailsBeforeLlvmEmission()
    {

        var function = BuildSwitchFunction(
            new SsaIntegerConstant(0, UnsizedInteger),
            [new SsaSwitchCase("0", 1, new SsaIntegerConstant(0, UnsizedInteger))]);

        var diagnostics = Validate(function);

        return diagnostics;
    }

    private static IReadOnlyList<CompilerDiagnostic> SwitchCaseShapeMismatchFailsBeforeLlvmEmission()
    {

        var function = BuildSwitchFunction(
            new SsaIntegerConstant(0, I32),
            [new SsaSwitchCase("true", 1, new SsaBoolConstant(true))]);

        var diagnostics = Validate(function);

        return diagnostics;
    }

    private static IReadOnlyList<CompilerDiagnostic> SliceCreationShapeMismatchFailsBeforeLlvmEmission()
    {

        var unknownLengthArray = StarkTypeSymbols.FixedArray(I32, fixedLength: null);
        var function = BuildVoidFunction([
            new SsaAllocateLocalInstruction("values", unknownLengthArray),
            new SsaValueInstruction(
                "v0",
                new SsaMakeSliceFromLocalRValue(
                    "values",
                    unknownLengthArray,
                    StarkTypeSymbols.Slice(I32),
                    "values[]"))
        ]);

        var diagnostics = Validate(function);

        return diagnostics;
    }

    private static IReadOnlyList<CompilerDiagnostic> IndirectLoadAddressShapeFailsBeforeLlvmEmission()
    {

        var function = BuildVoidFunction([
            new SsaValueInstruction(
                "v0",
                new SsaLoadIndirectRValue(
                    new SsaIntegerConstant(0, I32),
                    I32,
                    "*0"))
        ]);

        var diagnostics = Validate(function);

        return diagnostics;
    }

    private static IReadOnlyList<CompilerDiagnostic> IndirectStoreAcceptsQualifiedPointeeShape()
    {

        var outBool = StarkTypeSymbols.Bool with
        {
            DisplayName = "out bool",
            InitializationKind = StarkInitializationKind.Out
        };
        var outBoolPointer = StarkTypeSymbols.RawPointer(outBool, isMutable: true);
        var function = BuildVoidFunction([
            new SsaStoreIndirectInstruction(
                new SsaGlobalAddressValue("flag", outBool, outBoolPointer),
                outBool,
                new SsaBoolConstant(true))
        ]);

        var diagnostics = Validate(function);

        return diagnostics;
    }

    private static IReadOnlyList<CompilerDiagnostic> CopyMemoryDestinationPointeeMismatchFailsBeforeLlvmEmission()
    {

        var function = BuildVoidFunction([
            new SsaCopyMemoryInstruction(
                new SsaGlobalAddressValue("flag", StarkTypeSymbols.Bool, StarkTypeSymbols.RawPointer(StarkTypeSymbols.Bool, isMutable: true)),
                new SsaGlobalAddressValue("value", I32, StarkTypeSymbols.RawPointer(I32, isMutable: true)),
                I32)
        ]);

        var diagnostics = Validate(function);

        return diagnostics;
    }

    private static IReadOnlyList<CompilerDiagnostic> CopyMemorySourcePointeeMismatchFailsBeforeLlvmEmission()
    {

        var function = BuildVoidFunction([
            new SsaCopyMemoryInstruction(
                new SsaGlobalAddressValue("destination", I32, StarkTypeSymbols.RawPointer(I32, isMutable: true)),
                new SsaGlobalAddressValue("flag", StarkTypeSymbols.Bool, StarkTypeSymbols.RawPointer(StarkTypeSymbols.Bool, isMutable: true)),
                I32)
        ]);

        var diagnostics = Validate(function);

        return diagnostics;
    }

    private static IReadOnlyList<CompilerDiagnostic> CopyMemoryWithoutConcreteLayoutFailsBeforeLlvmEmission()
    {

        var voidPointer = StarkTypeSymbols.RawPointer(StarkTypeSymbols.Void, isMutable: true);
        var function = BuildVoidFunction([
            new SsaCopyMemoryInstruction(
                new SsaGlobalAddressValue("destination", StarkTypeSymbols.Void, voidPointer),
                new SsaGlobalAddressValue("source", StarkTypeSymbols.Void, voidPointer),
                StarkTypeSymbols.Void)
        ]);

        var diagnostics = Validate(function);

        return diagnostics;
    }

    private static IReadOnlyList<CompilerDiagnostic> CopyMemoryAllowsFixedArrayElementPointers()
    {

        var arrayType = StarkTypeSymbols.FixedArray(I32, fixedLength: 64);
        var elementPointer = StarkTypeSymbols.RawPointer(I32, isMutable: true);
        var function = BuildVoidFunction([
            new SsaCopyMemoryInstruction(
                new SsaGlobalAddressValue("destination", I32, elementPointer),
                new SsaGlobalAddressValue("source", I32, elementPointer),
                arrayType)
        ]);

        var diagnostics = Validate(function);

        return diagnostics;
    }

    private static IReadOnlyList<CompilerDiagnostic> ScopedNoAliasProofCarrierAcceptsMatchingParameterRoots()
    {

        var pointer = StarkTypeSymbols.RawPointer(I32, isMutable: true);
        var group = BuildAliasProofGroup(
            AliasProofCarrierKind.RuntimeDisjointCondition,
            "runtime-disjoint-0",
            ["param:left", "param:right"]);
        var function = BuildVoidFunction(
            [
                new SsaStoreIndirectInstruction(
                    new SsaValueReference("arg_left", pointer),
                    I32,
                    new SsaIntegerConstant(1, I32),
                    ScopedNoAliasGroups: [group])
            ],
            parameters: BuildPointerParameters(pointer));

        var diagnostics = Validate(function, BuildRunPointerAbi(pointer));

        return diagnostics;
    }

    private static IReadOnlyList<CompilerDiagnostic> ScopedNoAliasProofCarrierRootMismatchFailsBeforeLlvmEmission()
    {

        var pointer = StarkTypeSymbols.RawPointer(I32, isMutable: true);
        var group = BuildAliasProofGroup(
            AliasProofCarrierKind.RuntimeDisjointCondition,
            "runtime-disjoint-0",
            ["param:left", "param:right"],
            proofRoots: ["param:left", "param:other"]);
        var function = BuildVoidFunction(
            [
                new SsaStoreIndirectInstruction(
                    new SsaValueReference("arg_left", pointer),
                    I32,
                    new SsaIntegerConstant(1, I32),
                    ScopedNoAliasGroups: [group])
            ],
            parameters: BuildPointerParameters(pointer));

        var diagnostics = Validate(function, BuildRunPointerAbi(pointer));

        return diagnostics;
    }

    private static IReadOnlyList<CompilerDiagnostic> ScopedNoAliasProofCarrierDuplicateRootsFailBeforeLlvmEmission()
    {

        var pointer = StarkTypeSymbols.RawPointer(I32, isMutable: true);
        var group = BuildAliasProofGroup(
            AliasProofCarrierKind.RuntimeDisjointCondition,
            "runtime-disjoint-0",
            ["param:left", "param:left"]);
        var function = BuildVoidFunction(
            [
                new SsaStoreIndirectInstruction(
                    new SsaValueReference("arg_left", pointer),
                    I32,
                    new SsaIntegerConstant(1, I32),
                    ScopedNoAliasGroups: [group])
            ],
            parameters: BuildPointerParameters(pointer));

        var diagnostics = Validate(function, BuildRunPointerAbi(pointer));

        return diagnostics;
    }

    private static IReadOnlyList<CompilerDiagnostic> ScopedNoAliasProofCarrierIdMismatchFailsBeforeLlvmEmission()
    {

        var pointer = StarkTypeSymbols.RawPointer(I32, isMutable: true);
        var group = new ScopedNoAliasGroup(
            "runtime-disjoint-0",
            ["param:left", "param:right"],
            new AliasProofCarrier(
                AliasProofCarrierKind.RuntimeDisjointCondition,
                "wrong-proof",
                ["param:left", "param:right"]));
        var function = BuildVoidFunction(
            [
                new SsaStoreIndirectInstruction(
                    new SsaValueReference("arg_left", pointer),
                    I32,
                    new SsaIntegerConstant(1, I32),
                    ScopedNoAliasGroups: [group])
            ],
            parameters: BuildPointerParameters(pointer));

        var diagnostics = Validate(function, BuildRunPointerAbi(pointer));

        return diagnostics;
    }

    private static IReadOnlyList<CompilerDiagnostic> ScopedNoAliasProofCarrierUnknownParameterRootFailsBeforeLlvmEmission()
    {

        var pointer = StarkTypeSymbols.RawPointer(I32, isMutable: true);
        var group = BuildAliasProofGroup(
            AliasProofCarrierKind.UnsafeAssumeDisjoint,
            "unsafe-assume-disjoint-0",
            ["param:left", "param:missing"]);
        var function = BuildVoidFunction(
            [
                new SsaStoreIndirectInstruction(
                    new SsaValueReference("arg_left", pointer),
                    I32,
                    new SsaIntegerConstant(1, I32),
                    ScopedNoAliasGroups: [group])
            ],
            parameters: BuildPointerParameters(pointer));

        var diagnostics = Validate(function, BuildRunPointerAbi(pointer));

        return diagnostics;
    }

    private static IReadOnlyList<CompilerDiagnostic> StringConstantNonTextTypeFailsBeforeLlvmEmission()
    {

        var function = BuildVoidFunction([
            new SsaValueInstruction(
                "v0",
                new SsaUseRValue(new SsaStringConstant("hello", I32)))
        ]);

        var diagnostics = Validate(function);

        return diagnostics;
    }

    private static IReadOnlyList<CompilerDiagnostic> TextDataAddressPointeeMismatchFailsBeforeLlvmEmission()
    {

        var function = BuildVoidFunction([
            new SsaValueInstruction(
                "v0",
                new SsaUseRValue(new SsaTextDataAddressValue(
                    "hello",
                    StarkTypeSymbols.Ascii,
                    StarkTypeSymbols.RawPointer(I32, isMutable: false))))
        ]);

        var diagnostics = Validate(function);

        return diagnostics;
    }

    private static IReadOnlyList<CompilerDiagnostic> GlobalAddressPointeeMismatchFailsBeforeLlvmEmission()
    {

        var function = BuildVoidFunction([
            new SsaValueInstruction(
                "v0",
                new SsaUseRValue(new SsaGlobalAddressValue(
                    "flag",
                    StarkTypeSymbols.Bool,
                    StarkTypeSymbols.RawPointer(I32, isMutable: true))))
        ]);

        var diagnostics = Validate(function);

        return diagnostics;
    }

    private static IReadOnlyList<CompilerDiagnostic> UnknownGlobalLoadFailsBeforeLlvmEmission()
    {

        var function = BuildVoidFunction([
            new SsaValueInstruction(
                "v0",
                new SsaLoadGlobalRValue("Missing", I32))
        ]);

        var diagnostics = Validate(function, typeModel: BuildTypeModelWithGlobals());

        return diagnostics;
    }

    private static IReadOnlyList<CompilerDiagnostic> KnownGlobalLoadTypeMismatchFailsBeforeLlvmEmission()
    {

        var function = BuildVoidFunction([
            new SsaValueInstruction(
                "v0",
                new SsaLoadGlobalRValue("Flag", I32))
        ]);
        var typeModel = BuildTypeModelWithGlobals(new TypedGlobalSymbol("Flag", StarkTypeSymbols.Bool, GlobalBindingKind.Immutable));

        var diagnostics = Validate(function, typeModel: typeModel);

        return diagnostics;
    }

    private static IReadOnlyList<CompilerDiagnostic> KnownGlobalAddressTypeMismatchFailsBeforeLlvmEmission()
    {

        var function = BuildVoidFunction([
            new SsaValueInstruction(
                "v0",
                new SsaUseRValue(new SsaGlobalAddressValue(
                    "Flag",
                    I32,
                    StarkTypeSymbols.RawPointer(I32, isMutable: true))))
        ]);
        var typeModel = BuildTypeModelWithGlobals(new TypedGlobalSymbol("Flag", StarkTypeSymbols.Bool, GlobalBindingKind.Immutable));

        var diagnostics = Validate(function, typeModel: typeModel);

        return diagnostics;
    }

    private static IReadOnlyList<CompilerDiagnostic> StoreToImmutableGlobalFailsBeforeLlvmEmission()
    {

        var function = BuildVoidFunction([
            new SsaStoreGlobalInstruction(
                "Limit",
                I32,
                new SsaIntegerConstant(1, I32))
        ]);
        var typeModel = BuildTypeModelWithGlobals(new TypedGlobalSymbol("Limit", I32, GlobalBindingKind.Const));

        var diagnostics = Validate(function, typeModel: typeModel);

        return diagnostics;
    }

    private static IReadOnlyList<CompilerDiagnostic> StoreGlobalValueMismatchFailsBeforeLlvmEmission()
    {

        var function = BuildVoidFunction([
            new SsaStoreGlobalInstruction(
                "Counter",
                I32,
                new SsaBoolConstant(true))
        ]);
        var typeModel = BuildTypeModelWithGlobals(new TypedGlobalSymbol("Counter", I32, GlobalBindingKind.Mutable));

        var diagnostics = Validate(function, typeModel: typeModel);

        return diagnostics;
    }

    private static IReadOnlyList<CompilerDiagnostic> StoreMutableKnownGlobalPassesSsaValidation()
    {

        var function = BuildVoidFunction([
            new SsaStoreGlobalInstruction(
                "Counter",
                I32,
                new SsaIntegerConstant(1, I32))
        ]);
        var typeModel = BuildTypeModelWithGlobals(new TypedGlobalSymbol("Counter", I32, GlobalBindingKind.Mutable));

        var diagnostics = Validate(function, typeModel: typeModel);

        return diagnostics;
    }

    private static IReadOnlyList<CompilerDiagnostic> FunctionAddressNonFunctionPointerTypeFailsBeforeLlvmEmission()
    {

        var function = BuildVoidFunction([
            new SsaValueInstruction(
                "v0",
                new SsaUseRValue(new SsaFunctionAddressValue("Run", StarkTypeSymbols.RawPointer(I32, isMutable: false))))
        ]);

        var diagnostics = Validate(function);

        return diagnostics;
    }

    private static IReadOnlyList<CompilerDiagnostic> FunctionAddressMissingAbiFailsBeforeLlvmEmission()
    {

        var functionPointerType = StarkTypeSymbols.FunctionPointer(
            StarkFunctionKind.Fn,
            I32,
            []);
        var function = BuildVoidFunction([
            new SsaValueInstruction(
                "v0",
                new SsaUseRValue(new SsaFunctionAddressValue("Missing", functionPointerType)))
        ]);

        var diagnostics = Validate(function);

        return diagnostics;
    }

    private static IReadOnlyList<CompilerDiagnostic> FunctionAddressSignatureMismatchFailsBeforeLlvmEmission()
    {

        var functionPointerType = StarkTypeSymbols.FunctionPointer(
            StarkFunctionKind.Fn,
            I32,
            [I32]);
        var function = BuildVoidFunction([
            new SsaValueInstruction(
                "v0",
                new SsaUseRValue(new SsaFunctionAddressValue("Read", functionPointerType)))
        ]);
        var abi = BuildAbiModel(new AbiFunctionSignature(
            "Read",
            "Read",
            StarkTypeSymbols.Bool,
            StarkTypeSymbols.Bool,
            [],
            IsFfi: false));

        var diagnostics = Validate(function, abi);

        return diagnostics;
    }

    private static IReadOnlyList<CompilerDiagnostic> SystemMathBuiltinInvalidSignatureFailsBeforeLlvmEmission()
    {

        var typeModel = BuildTypeModelForModule(
            "System.Math",
            [
                new TypedFunctionSignature(
                    "Sin",
                    I32,
                    [new TypedParameterSymbol("value", I32)])
            ]);

        var diagnostics = Validate(BuildVoidFunction([]), typeModel: typeModel);

        return diagnostics;
    }

    private static IReadOnlyList<CompilerDiagnostic> SystemBitOperationsBuiltinInvalidWidthFailsBeforeLlvmEmission()
    {

        var i16 = StarkTypeSymbols.Integer(16);
        var typeModel = BuildTypeModelForModule(
            "System.BitOperations",
            [
                new TypedFunctionSignature(
                    "PopCount",
                    i16,
                    [new TypedParameterSymbol("value", i16)])
            ]);

        var diagnostics = Validate(BuildVoidFunction([]), typeModel: typeModel);

        return diagnostics;
    }

    private static IReadOnlyList<CompilerDiagnostic> SystemMemoryBuiltinInvalidAllocationShapeFailsBeforeLlvmEmission()
    {

        var allocator = BuildNamedType("System.Memory.Allocator", [new FieldSymbol("Kind", StarkTypeSymbols.Integer(8, isUnsigned: true))]);
        var allocation = BuildNamedType(
            "System.Memory.Allocation",
            [
                new FieldSymbol("Pointer", StarkTypeSymbols.RawPointer(StarkTypeSymbols.Integer(8), isMutable: true)),
                new FieldSymbol("ByteLength", StarkTypeSymbols.Integer(64, isUnsigned: true)),
                new FieldSymbol("Alignment", StarkTypeSymbols.Integer(64, isUnsigned: true))
            ]);
        var typeModel = BuildTypeModelForModule(
            "System.Memory",
            [
                new TypedFunctionSignature(
                    "Allocate",
                    StarkTypeSymbols.Named("System.Memory.Allocation"),
                    [
                        new TypedParameterSymbol("allocator", StarkTypeSymbols.Named("System.Memory.Allocator")),
                        new TypedParameterSymbol("byteLength", StarkTypeSymbols.Integer(64, isUnsigned: true)),
                        new TypedParameterSymbol("alignment", StarkTypeSymbols.Integer(64, isUnsigned: true))
                    ])
            ],
            allocator,
            allocation);

        var diagnostics = Validate(BuildVoidFunction([]), typeModel: typeModel);

        return diagnostics;
    }

    private static IReadOnlyList<CompilerDiagnostic> SystemCollectionsDictionaryKeyAsciiKeyPassesBuiltinValidation()
    {

        var typeModel = BuildTypeModelForModule(
            "System.Collections",
            [
                new TypedFunctionSignature(
                    "__stark_mono_fn_System_Collections__System_Collections_DictionaryKey_Hash__ascii",
                    StarkTypeSymbols.Integer(64, isUnsigned: true),
                    [new TypedParameterSymbol("value", StarkTypeSymbols.Ascii with { BorrowKind = StarkBorrowKind.Borrow })],
                    SourceName: "System.Collections.DictionaryKey.Hash",
                    TemplateName: "System.Collections.DictionaryKey.Hash",
                    TypeArguments: [StarkTypeSymbols.Ascii])
            ]);

        var diagnostics = Validate(BuildVoidFunction([]), typeModel: typeModel);

        return diagnostics;
    }

    private static IReadOnlyList<CompilerDiagnostic> SystemCollectionsDictionaryKeyUnsupportedKeyFailsBeforeLlvmEmission()
    {

        var sliceType = StarkTypeSymbols.Slice(StarkTypeSymbols.Integer(8));
        var typeModel = BuildTypeModelForModule(
            "System.Collections",
            [
                new TypedFunctionSignature(
                    "__stark_mono_fn_System_Collections__System_Collections_DictionaryKey_Hash__i8_slice",
                    StarkTypeSymbols.Integer(64, isUnsigned: true),
                    [new TypedParameterSymbol("value", sliceType with { BorrowKind = StarkBorrowKind.Borrow })],
                    SourceName: "System.Collections.DictionaryKey.Hash",
                    TemplateName: "System.Collections.DictionaryKey.Hash",
                    TypeArguments: [sliceType])
            ]);

        var diagnostics = Validate(BuildVoidFunction([]), typeModel: typeModel);

        return diagnostics;
    }

    private static IReadOnlyList<CompilerDiagnostic> SystemRuntimeByteSlicePartsMutableMismatchFailsBeforeLlvmEmission()
    {

        var mutableParts = BuildNamedType(
            "System.Runtime.MutableByteSliceParts",
            [
                new FieldSymbol("Data", StarkTypeSymbols.RawPointer(StarkTypeSymbols.Integer(8), isMutable: true)),
                new FieldSymbol("Length", StarkTypeSymbols.Integer(64, isUnsigned: true))
            ]);
        var typeModel = BuildTypeModelForModule(
            "System.Runtime",
            [
                new TypedFunctionSignature(
                    "GetMutableByteSliceParts",
                    StarkTypeSymbols.Named("System.Runtime.MutableByteSliceParts"),
                    [new TypedParameterSymbol("source", StarkTypeSymbols.Slice(StarkTypeSymbols.Integer(8)) with { BorrowKind = StarkBorrowKind.Borrow })])
            ],
            mutableParts);

        var diagnostics = Validate(BuildVoidFunction([]), typeModel: typeModel);

        return diagnostics;
    }

    private static IReadOnlyList<CompilerDiagnostic> ValidSystemBuiltinSignaturesPassSsaValidation()
    {

        var i64 = StarkTypeSymbols.Integer(64, isUnsigned: true);
        var typeModel = BuildTypeModelForModule(
            "System.BitOperations",
            [
                new TypedFunctionSignature(
                    "RotateLeft",
                    i64,
                    [
                        new TypedParameterSymbol("value", i64),
                        new TypedParameterSymbol("amount", i64)
                    ])
            ]);

        var diagnostics = Validate(BuildVoidFunction([]), typeModel: typeModel);

        return diagnostics;
    }

    private static IReadOnlyList<CompilerDiagnostic> Validate(
        SsaFunction function,
        AbiModel? abiModel = null,
        TypeCheckModel? typeModel = null)
    {
        var state = new CompilationState(new CompilationInput("module Demo"), new CompilerOptions());
        var context = new CompilerPassContext(state);
        new SsaIrValidator(
            context,
            new SsaIrModule("Demo", [function]),
            abiModel ?? BuildAbiModel(),
            typeModel).Validate();
        return context.Diagnostics.Items;
    }

    private static TypeCheckModel BuildTypeModel(params NamedTypeSymbol[] namedTypes)
    {
        return new TypeCheckModel(
            "Demo",
            namedTypes.ToDictionary(static namedType => namedType.Name, StringComparer.Ordinal),
            new Dictionary<string, TypeAliasSymbol>(StringComparer.Ordinal),
            new Dictionary<string, TypedFunctionSignature>(StringComparer.Ordinal),
            new Dictionary<string, TypedGlobalSymbol>(StringComparer.Ordinal),
            [],
            []);
    }

    private static TypeCheckModel BuildTypeModelWithGlobals(params TypedGlobalSymbol[] globals)
    {
        return new TypeCheckModel(
            "Demo",
            new Dictionary<string, NamedTypeSymbol>(StringComparer.Ordinal),
            new Dictionary<string, TypeAliasSymbol>(StringComparer.Ordinal),
            new Dictionary<string, TypedFunctionSignature>(StringComparer.Ordinal),
            globals.ToDictionary(static global => global.Name, StringComparer.Ordinal),
            [],
            []);
    }

    private static TypeCheckModel BuildTypeModelForModule(
        string moduleName,
        IReadOnlyList<TypedFunctionSignature> functions,
        params NamedTypeSymbol[] namedTypes)
    {
        return new TypeCheckModel(
            moduleName,
            namedTypes.ToDictionary(static namedType => namedType.Name, StringComparer.Ordinal),
            new Dictionary<string, TypeAliasSymbol>(StringComparer.Ordinal),
            functions.ToDictionary(static function => function.Name, StringComparer.Ordinal),
            new Dictionary<string, TypedGlobalSymbol>(StringComparer.Ordinal),
            [],
            []);
    }

    private static NamedTypeSymbol BuildNamedType(string name, IReadOnlyList<FieldSymbol> fields)
    {
        return new NamedTypeSymbol(
            name,
            DeclarationKind.Struct,
            fields.ToDictionary(static field => field.Name, StringComparer.Ordinal),
            fields);
    }

    private static NamedTypeSymbol BuildPairType()
    {
        return BuildNamedType(
            "Pair",
            [
                new FieldSymbol("Value", I32),
                new FieldSymbol("Flag", StarkTypeSymbols.Bool)
            ]);
    }

    private static AbiModel BuildAbiModel(params AbiFunctionSignature[] functions)
    {
        var abiFunctions = functions.ToDictionary(static function => function.Name, StringComparer.Ordinal);
        if (!abiFunctions.ContainsKey("Run"))
        {
            abiFunctions["Run"] = new AbiFunctionSignature(
                "Run",
                "Run",
                StarkTypeSymbols.Void,
                StarkTypeSymbols.Void,
                [],
                IsFfi: false);
        }

        return new AbiModel("Demo", abiFunctions);
    }

    private static IReadOnlyList<TypedParameterSymbol> BuildPointerParameters(StarkTypeSymbol pointer)
    {
        return
        [
            new TypedParameterSymbol("left", pointer),
            new TypedParameterSymbol("right", pointer)
        ];
    }

    private static AbiModel BuildRunPointerAbi(StarkTypeSymbol pointer)
    {
        return BuildAbiModel(new AbiFunctionSignature(
            "Run",
            "Run",
            StarkTypeSymbols.Void,
            StarkTypeSymbols.Void,
            [
                new AbiParameterSymbol("left", "arg_left", pointer, pointer, AbiParameterKind.Direct),
                new AbiParameterSymbol("right", "arg_right", pointer, pointer, AbiParameterKind.Direct)
            ],
            IsFfi: false));
    }

    private static ScopedNoAliasGroup BuildAliasProofGroup(
        AliasProofCarrierKind kind,
        string proofId,
        IReadOnlyList<string> roots,
        IReadOnlyList<string>? proofRoots = null)
    {
        return new ScopedNoAliasGroup(
            proofId,
            roots,
            new AliasProofCarrier(kind, proofId, proofRoots ?? roots));
    }

    private static SsaFunction BuildVoidFunction(
        IReadOnlyList<SsaInstruction> instructions,
        IReadOnlyList<TypedParameterSymbol>? parameters = null,
        SsaTerminator? terminator = null)
    {
        return new SsaFunction(
            "Run",
            StarkTypeSymbols.Void,
            Parameters: parameters ?? [],
            HasBody: true,
            SupportsDirectCodeGeneration: true,
            EntryBlockId: 0,
            Blocks:
            [
                new SsaBasicBlock(
                    0,
                    "entry",
                    Phis: [],
                    Instructions: instructions,
                    Terminator: terminator ?? new SsaTerminator(SsaTerminatorKind.Return, []))
            ],
            BodyLoweringKind: FunctionBodyLoweringKind.StarkCfg);
    }

    private static SsaFunction BuildReturningFunction(
        StarkTypeSymbol returnType,
        SsaValue returnValue,
        IReadOnlyList<SsaInstruction>? instructions = null,
        IReadOnlyList<TypedParameterSymbol>? parameters = null)
    {
        return new SsaFunction(
            "Run",
            returnType,
            Parameters: parameters ?? [],
            HasBody: true,
            SupportsDirectCodeGeneration: true,
            EntryBlockId: 0,
            Blocks:
            [
                new SsaBasicBlock(
                    0,
                    "entry",
                    Phis: [],
                    Instructions: instructions ?? [],
                    Terminator: new SsaTerminator(SsaTerminatorKind.Return, [], Value: returnValue))
            ],
            BodyLoweringKind: FunctionBodyLoweringKind.StarkCfg);
    }

    private static SsaFunction BuildVoidMultiBlockFunction(params SsaBasicBlock[] blocks)
    {
        return new SsaFunction(
            "Run",
            StarkTypeSymbols.Void,
            Parameters: [],
            HasBody: true,
            SupportsDirectCodeGeneration: true,
            EntryBlockId: 0,
            Blocks: blocks,
            BodyLoweringKind: FunctionBodyLoweringKind.StarkCfg);
    }

    private static SsaFunction BuildSwitchFunction(
        SsaValue condition,
        IReadOnlyList<SsaSwitchCase> switchCases)
    {
        return new SsaFunction(
            "Run",
            StarkTypeSymbols.Void,
            Parameters: [],
            HasBody: true,
            SupportsDirectCodeGeneration: true,
            EntryBlockId: 0,
            Blocks:
            [
                new SsaBasicBlock(
                    0,
                    "entry",
                    Phis: [],
                    Instructions: [],
                    Terminator: new SsaTerminator(
                        SsaTerminatorKind.Switch,
                        [1, 2],
                        Condition: condition,
                        SwitchCases: switchCases,
                        DefaultTarget: 2)),
                new SsaBasicBlock(
                    1,
                    "case",
                    Phis: [],
                    Instructions: [],
                    Terminator: new SsaTerminator(SsaTerminatorKind.Return, [])),
                new SsaBasicBlock(
                    2,
                    "default",
                    Phis: [],
                    Instructions: [],
                    Terminator: new SsaTerminator(SsaTerminatorKind.Return, []))
            ],
            BodyLoweringKind: FunctionBodyLoweringKind.StarkCfg);
    }

    private sealed record UnsupportedSsaInstruction : SsaInstruction;

    private sealed record UnsupportedSsaRValue()
        : SsaRValue(StarkTypeSymbols.Integer(32), "unsupported-rvalue");

    private sealed record UnsupportedSsaValue()
        : SsaValue(StarkTypeSymbols.Integer(32), "unsupported-value");

    private sealed record SsaValidatorFixtureExpectation(
        SsaValidatorFixtureExpectationKind Kind,
        string Code,
        IReadOnlyList<string> MessageFragments)
    {
        public static SsaValidatorFixtureExpectation NoDiagnostics()
        {
            return new SsaValidatorFixtureExpectation(
                SsaValidatorFixtureExpectationKind.NoDiagnostics,
                string.Empty,
                []);
        }

        public static SsaValidatorFixtureExpectation DiagnosticContaining(
            string code,
            IReadOnlyList<string> messageFragments)
        {
            return new SsaValidatorFixtureExpectation(
                SsaValidatorFixtureExpectationKind.DiagnosticContaining,
                code,
                messageFragments);
        }

        public static SsaValidatorFixtureExpectation NoDiagnosticMatching(
            string code,
            IReadOnlyList<string> messageFragments)
        {
            return new SsaValidatorFixtureExpectation(
                SsaValidatorFixtureExpectationKind.NoDiagnosticMatching,
                code,
                messageFragments);
        }

        public bool IsMetBy(IReadOnlyList<CompilerDiagnostic> diagnostics)
        {
            return Kind switch
            {
                SsaValidatorFixtureExpectationKind.NoDiagnostics => diagnostics.Count == 0,
                SsaValidatorFixtureExpectationKind.DiagnosticContaining => diagnostics.Any(Matches),
                SsaValidatorFixtureExpectationKind.NoDiagnosticMatching => !diagnostics.Any(Matches),
                _ => false
            };
        }

        public string DescribeFailure()
        {
            return Kind switch
            {
                SsaValidatorFixtureExpectationKind.NoDiagnostics => "expected no diagnostics",
                SsaValidatorFixtureExpectationKind.DiagnosticContaining =>
                    $"expected diagnostic {Code} containing {string.Join(", ", MessageFragments.Select(static fragment => $"'{fragment}'"))}",
                SsaValidatorFixtureExpectationKind.NoDiagnosticMatching =>
                    $"expected no diagnostic {Code} containing {string.Join(", ", MessageFragments.Select(static fragment => $"'{fragment}'"))}",
                _ => "unknown expectation"
            };
        }

        private bool Matches(CompilerDiagnostic diagnostic)
        {
            return diagnostic.Code == Code
                && MessageFragments.All(fragment => diagnostic.Message.Contains(fragment, StringComparison.Ordinal));
        }
    }

    private enum SsaValidatorFixtureExpectationKind
    {
        NoDiagnostics,
        DiagnosticContaining,
        NoDiagnosticMatching
    }
}

internal sealed record SsaValidatorFixtureRun(
    bool Passed,
    IReadOnlyList<CompilerDiagnostic> Diagnostics,
    string? Failure)
{
    public static readonly SsaValidatorFixtureRun Empty = new(false, [], null);
}
