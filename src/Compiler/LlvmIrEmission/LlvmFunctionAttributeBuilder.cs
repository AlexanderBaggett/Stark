using System.Globalization;
using System.Numerics;

namespace Stark.Compiler.LlvmIrEmission;

internal sealed class LlvmFunctionAttributeBuilder
{
    private readonly LlvmEmissionContext _context;

    public LlvmFunctionAttributeBuilder(LlvmEmissionContext context)
    {
        _context = context;
    }

    private string MapType(StarkTypeSymbol type) => _context.MapType(type);

    private ConcreteTypeLayout? TryGetConcreteTypeLayout(StarkTypeSymbol type) => _context.TryGetConcreteTypeLayout(type);

    public string RenderAbiParameter(
        AbiFunctionSignature abiFunction,
        AbiParameterSymbol parameter,
        bool includeName,
        IReadOnlyDictionary<string, ParameterMemoryEffectSummary>? parameterEffects)
    {
        var segments = new List<string> { MapType(parameter.LlvmType) };

        if (ShouldMarkNoundef(abiFunction, parameter))
        {
            segments.Add("noundef");
        }

        if (!abiFunction.IsFfi
            && parameter.Kind == AbiParameterKind.Direct
            && LlvmValueRangeFacts.TryBuildRangeAttribute(parameter.SourceType, out var rangeAttribute))
        {
            segments.Add(rangeAttribute);
        }

        segments.AddRange(DeriveAbiParameterAttributes(parameter, ResolveParameterEffects(parameter, parameterEffects)));

        if (includeName)
        {
            segments.Add($"%{EscapeIdentifier(parameter.LlvmName)}");
        }

        return string.Join(" ", segments);
    }

    public IReadOnlyList<string> GetAbiParameterAttributes(
        AbiParameterSymbol parameter,
        IReadOnlyDictionary<string, ParameterMemoryEffectSummary>? parameterEffects)
    {
        return DeriveAbiParameterAttributes(parameter, ResolveParameterEffects(parameter, parameterEffects));
    }

    public string RenderAbiReturnType(AbiFunctionSignature abiFunction, SsaIntegerRangeFact? returnRange = null)
    {
        var segments = new List<string>();

        if (ShouldMarkNoundef(abiFunction))
        {
            segments.Add("noundef");
        }

        if (!abiFunction.IsFfi
            && !abiFunction.ReturnsIndirect
            && abiFunction.SourceReturnType.BorrowKind == StarkBorrowKind.None
            && TryBuildReturnRangeAttribute(abiFunction.SourceReturnType, returnRange, out var rangeAttribute))
        {
            segments.Add(rangeAttribute);
        }

        segments.Add(MapType(abiFunction.LlvmReturnType));

        return string.Join(" ", segments);
    }

    private static bool TryBuildReturnRangeAttribute(
        StarkTypeSymbol returnType,
        SsaIntegerRangeFact? returnRange,
        out string rangeAttribute)
    {
        return returnRange is { } range
            ? LlvmValueRangeFacts.TryBuildRangeAttribute(returnType, range, out rangeAttribute)
            : LlvmValueRangeFacts.TryBuildRangeAttribute(returnType, out rangeAttribute);
    }

    public string BuildFunctionAttributes(
        AbiFunctionSignature abiFunction,
        FunctionEffectProfile effects,
        FunctionMemoryEffectSummary? memoryEffects)
    {
        var attributes = new List<string>();

        if (effects.NoUnwind)
        {
            attributes.Add("nounwind");
        }

        if (effects.WillReturn)
        {
            attributes.Add("willreturn");
        }

        if (effects.MustProgress)
        {
            attributes.Add("mustprogress");
        }

        if (effects.NoSync)
        {
            attributes.Add("nosync");
        }

        if (effects.NoFree)
        {
            attributes.Add("nofree");
        }

        var memoryAttribute = BuildMemoryAttribute(abiFunction, effects, memoryEffects);
        if (!string.IsNullOrWhiteSpace(memoryAttribute))
        {
            attributes.Add(memoryAttribute);
        }

        if (effects.IsHot)
        {
            attributes.Add("hot");
        }

        if (effects.IsCold)
        {
            attributes.Add("cold");
        }

        if (effects.IsStrictFp)
        {
            attributes.Add("strictfp");
        }

        var inlinePreference = effects.BackendOptimizationMode == ModuleBackendOptimizationMode.Opaque
            ? InlinePreference.NoInline
            : effects.InlinePreference;
        if (effects.BackendOptimizationMode == ModuleBackendOptimizationMode.Opaque)
        {
            attributes.Add("optnone");
        }

        attributes.Add(inlinePreference switch
        {
            InlinePreference.Inline => "alwaysinline",
            InlinePreference.NoInline => "noinline",
            _ => "inlinehint"
        });

        return string.Join(" ", attributes);
    }

    public string BuildFunctionPointerCallSiteAttributes(
        AbiFunctionSignature abiFunction,
        StarkFunctionKind functionPointerKind)
    {
        var isFinite = FunctionKindFacts.IsFinite(functionPointerKind);
        var isLaw = FunctionKindFacts.IsLaw(functionPointerKind);
        if (!isLaw)
        {
            return isFinite
                ? "nounwind willreturn mustprogress"
                : string.Empty;
        }

        var baseAttributes = isFinite
            ? "nounwind willreturn mustprogress nosync nofree"
            : "nounwind nosync nofree";
        var memoryAttribute = BuildLawFunctionPointerCallSiteMemoryAttribute(abiFunction);
        if (!string.IsNullOrWhiteSpace(memoryAttribute))
        {
            return $"{baseAttributes} {memoryAttribute}";
        }

        return baseAttributes;
    }

    private static string? BuildLawFunctionPointerCallSiteMemoryAttribute(AbiFunctionSignature abiFunction)
    {
        return GetMemoryAttribute(
            FunctionContractReadsArgumentMemory(abiFunction) || FunctionAbiLoweringReadsArgumentMemory(abiFunction),
            FunctionContractWritesArgumentMemory(abiFunction) || FunctionAbiLoweringWritesArgumentMemory(abiFunction),
            FunctionContractReadsOtherMemory(abiFunction),
            FunctionContractWritesOtherMemory(abiFunction));
    }

    private IReadOnlyList<string> DeriveAbiParameterAttributes(
        AbiParameterSymbol parameter,
        ParameterMemoryEffectSummary? parameterEffects)
    {
        var attributes = new List<string>();

        if (parameter.Kind == AbiParameterKind.SRet)
        {
            attributes.Add("noalias");
            attributes.Add($"sret({MapType(parameter.SourceType)})");
            attributes.Add("nonnull");
            AppendDereferenceableAttributes(attributes, parameter.SourceType);

            return attributes;
        }

        if (parameter.Kind == AbiParameterKind.IndirectIn)
        {
            attributes.Add("nonnull");
            if (AbiLoweringHeuristics.IsByValueIndirectParameter(parameter))
            {
                attributes.Add($"byval({MapType(parameter.SourceType)})");
            }

            attributes.Add("noalias");
            AppendPointerMemoryAccessAttributes(attributes, parameter, parameterEffects);
            AppendCaptureAttribute(attributes, parameterEffects);
            AppendDereferenceableAttributes(attributes, parameter.SourceType);

            return attributes;
        }

        if (parameter.Kind == AbiParameterKind.Direct
            && parameter.LlvmType.Kind == StarkTypeKind.FunctionPointer)
        {
            attributes.Add("nonnull");
            return attributes;
        }

        if (parameter.LlvmType.Kind != StarkTypeKind.RawPointer)
        {
            return attributes;
        }

        AppendBoundedRawPointerRegionAttributes(attributes, parameter);

        if (parameter.SourceType.BorrowKind != StarkBorrowKind.None
            || parameter.SourceType.InitializationKind != StarkInitializationKind.None)
        {
            attributes.Add("nonnull");
            AppendDereferenceableAttributes(attributes, parameter.SourceType);
        }

        if (parameter.SourceType.InitializationKind != StarkInitializationKind.None
            || parameterEffects?.GuaranteedNoAlias == true)
        {
            attributes.Add("noalias");
        }

        AppendPointerMemoryAccessAttributes(attributes, parameter, parameterEffects);
        AppendCaptureAttribute(attributes, parameterEffects);

        // Plain raw pointers remain nullable and may carry arbitrary raw/FFI
        // provenance, so do not infer nonnull or dereferenceable facts here.
        return attributes;
    }

    private void AppendBoundedRawPointerRegionAttributes(List<string> attributes, AbiParameterSymbol parameter)
    {
        if (parameter.Kind != AbiParameterKind.Direct
            || parameter.SourceType.Kind != StarkTypeKind.RawPointer
            || parameter.SourceType.ElementType is not { } elementType
            || string.IsNullOrWhiteSpace(parameter.RawPointerElementCountExpression)
            || !BigInteger.TryParse(parameter.RawPointerElementCountExpression, NumberStyles.None, CultureInfo.InvariantCulture, out var elementCount)
            || elementCount <= BigInteger.Zero
            || TryGetConcreteTypeLayout(elementType) is not { } elementLayout)
        {
            return;
        }

        var byteCount = elementCount * elementLayout.SizeBytes;
        if (byteCount > long.MaxValue)
        {
            return;
        }

        attributes.Add("nonnull");
        attributes.Add($"dereferenceable({byteCount.ToString(CultureInfo.InvariantCulture)})");
        if (elementLayout.AlignmentBytes > 1)
        {
            attributes.Add($"align {elementLayout.AlignmentBytes}");
        }
    }

    private void AppendDereferenceableAttributes(List<string> attributes, StarkTypeSymbol type)
    {
        if (TryGetConcreteTypeLayout(type) is not { } layout)
        {
            return;
        }

        attributes.Add($"dereferenceable({layout.SizeBytes})");
        if (layout.AlignmentBytes > 1)
        {
            attributes.Add($"align {layout.AlignmentBytes}");
        }
    }

    private static ParameterMemoryEffectSummary? ResolveParameterEffects(
        AbiParameterSymbol parameter,
        IReadOnlyDictionary<string, ParameterMemoryEffectSummary>? parameterEffects)
    {
        if (parameterEffects is null
            || parameter.Kind == AbiParameterKind.SRet
            || !parameterEffects.TryGetValue(parameter.SourceName, out var effects))
        {
            return null;
        }

        return effects;
    }

    private static bool ShouldMarkNoundef(
        AbiFunctionSignature abiFunction,
        AbiParameterSymbol parameter)
    {
        if (abiFunction.IsFfi || parameter.LlvmType.Kind == StarkTypeKind.Void)
        {
            return false;
        }

        return true;
    }

    private static bool ShouldMarkNoundef(AbiFunctionSignature abiFunction)
    {
        return !abiFunction.IsFfi
            && !abiFunction.ReturnsIndirect
            && abiFunction.LlvmReturnType.Kind != StarkTypeKind.Void;
    }

    private static void AppendPointerMemoryAccessAttributes(
        List<string> attributes,
        AbiParameterSymbol parameter,
        ParameterMemoryEffectSummary? parameterEffects)
    {
        var contractReads = ParameterContractReadsArgumentMemory(parameter);
        var contractWrites = ParameterContractWritesArgumentMemory(parameter);

        if (parameterEffects is not null)
        {
            var reads = parameterEffects.Reads || ParameterContractReadsMustBePreserved(parameter);
            var writes = parameterEffects.Writes
                || (!parameterEffects.GuaranteedReadOnly && ParameterContractWritesMustBePreserved(parameter));

            if (writes)
            {
                if (!reads)
                {
                    attributes.Add("writeonly");
                }
            }
            else
            {
                attributes.Add("readonly");
            }

            return;
        }

        if (contractWrites)
        {
            if (!contractReads)
            {
                attributes.Add("writeonly");
            }

            return;
        }

        if (contractReads)
        {
            attributes.Add("readonly");
        }
    }

    private static void AppendCaptureAttribute(List<string> attributes, ParameterMemoryEffectSummary? parameterEffects)
    {
        if (parameterEffects is null)
        {
            return;
        }

        if (parameterEffects.CaptureKind == ParameterCaptureKind.None)
        {
            attributes.Add("nocapture");
            return;
        }

        attributes.Add(parameterEffects.CaptureKind switch
        {
            ParameterCaptureKind.Return => parameterEffects.GuaranteedReadOnly
                ? "captures(ret: address, read_provenance)"
                : "captures(ret: address, provenance)",
            ParameterCaptureKind.Escape => parameterEffects.GuaranteedReadOnly
                ? "captures(address, read_provenance)"
                : "captures(address, provenance)",
            _ => "captures(address, provenance)"
        });
    }

    private static string? BuildMemoryAttribute(
        AbiFunctionSignature abiFunction,
        FunctionEffectProfile effects,
        FunctionMemoryEffectSummary? memoryEffects)
    {
        var readsArgumentMemory = memoryEffects is not null
            ? memoryEffects.ReadsArgumentMemory
              || FunctionContractReadsArgumentMemory(abiFunction)
              || FunctionAbiLoweringReadsArgumentMemory(abiFunction)
            : effects.ReadsArgumentMemory || FunctionContractReadsArgumentMemory(abiFunction);
        var writesArgumentMemory = memoryEffects is not null
            ? memoryEffects.WritesArgumentMemory
              || FunctionContractWritesArgumentMemory(abiFunction)
              || FunctionAbiLoweringWritesArgumentMemory(abiFunction)
            : FunctionContractWritesArgumentMemory(abiFunction);

        var readsOtherMemory = (memoryEffects?.ReadsOtherMemory ?? false)
            || FunctionContractReadsOtherMemory(abiFunction);
        var writesOtherMemory = (memoryEffects?.WritesOtherMemory ?? false)
            || FunctionContractWritesOtherMemory(abiFunction);

        if (memoryEffects is null)
        {
            return effects.IsPure
                ? GetMemoryAttribute(readsArgumentMemory, writesArgumentMemory, readsOtherMemory, writesOtherMemory)
                : null;
        }

        return GetMemoryAttribute(readsArgumentMemory, writesArgumentMemory, readsOtherMemory, writesOtherMemory);
    }

    private static string? GetMemoryAttribute(
        bool readsArgumentMemory,
        bool writesArgumentMemory,
        bool readsOtherMemory,
        bool writesOtherMemory)
    {
        var defaultAccess = GetMemoryAccessName(readsOtherMemory, writesOtherMemory);
        var argumentAccess = GetMemoryAccessName(readsArgumentMemory, writesArgumentMemory);

        if (defaultAccess == "readwrite" && argumentAccess == "readwrite")
        {
            return null;
        }

        if (defaultAccess == argumentAccess)
        {
            return $"memory({defaultAccess})";
        }

        if (defaultAccess == "none")
        {
            return $"memory(argmem: {argumentAccess})";
        }

        if (argumentAccess == "none")
        {
            return $"memory({defaultAccess}, argmem: none)";
        }

        return $"memory({defaultAccess}, argmem: {argumentAccess})";
    }

    private static string GetMemoryAccessName(bool reads, bool writes)
    {
        return (reads, writes) switch
        {
            (false, false) => "none",
            (true, false) => "read",
            (false, true) => "write",
            _ => "readwrite"
        };
    }

    private static bool FunctionContractReadsArgumentMemory(AbiFunctionSignature abiFunction)
    {
        return abiFunction.UserParameters.Any(ParameterContractReadsArgumentMemory);
    }

    private static bool FunctionContractWritesArgumentMemory(AbiFunctionSignature abiFunction)
    {
        return abiFunction.ReturnsIndirect
            || abiFunction.UserParameters.Any(ParameterContractWritesArgumentMemory);
    }

    private static bool FunctionContractReadsOtherMemory(AbiFunctionSignature abiFunction)
    {
        return abiFunction.UserParameters.Any(ParameterContractReadsOtherMemory);
    }

    private static bool FunctionContractWritesOtherMemory(AbiFunctionSignature abiFunction)
    {
        return abiFunction.UserParameters.Any(ParameterContractWritesOtherMemory);
    }

    private static bool FunctionAbiLoweringReadsArgumentMemory(AbiFunctionSignature abiFunction)
    {
        return abiFunction.UserParameters.Any(AbiLoweringHeuristics.IsByValueIndirectParameter);
    }

    private static bool FunctionAbiLoweringWritesArgumentMemory(AbiFunctionSignature abiFunction)
    {
        return abiFunction.ReturnsIndirect;
    }

    private static bool ParameterContractReadsArgumentMemory(AbiParameterSymbol parameter)
    {
        if (parameter.Kind == AbiParameterKind.SRet)
        {
            return false;
        }

        if (parameter.Kind == AbiParameterKind.IndirectIn)
        {
            return true;
        }

        return parameter.SourceType.Kind == StarkTypeKind.Slice
            || (parameter.SourceType.InitializationKind == StarkInitializationKind.None
                && (parameter.SourceType.Kind is StarkTypeKind.Ascii or StarkTypeKind.Unicode
                    || parameter.SourceType.BorrowKind != StarkBorrowKind.None
                    || parameter.SourceType.Kind == StarkTypeKind.RawPointer));
    }

    private static bool ParameterContractWritesArgumentMemory(AbiParameterSymbol parameter)
    {
        if (parameter.Kind == AbiParameterKind.SRet)
        {
            return true;
        }

        return parameter.SourceType.InitializationKind != StarkInitializationKind.None
            || (parameter.SourceType.BorrowKind != StarkBorrowKind.None && parameter.SourceType.IsMutableView)
            || (parameter.SourceType.Kind == StarkTypeKind.RawPointer && parameter.SourceType.IsMutablePointer);
    }

    private static bool ParameterContractReadsOtherMemory(AbiParameterSymbol parameter)
    {
        if (parameter.SourceType.InitializationKind != StarkInitializationKind.None)
        {
            return false;
        }

        return parameter.SourceType.Kind is StarkTypeKind.Slice
            or StarkTypeKind.Dynamic
            or StarkTypeKind.Ascii
            or StarkTypeKind.Unicode;
    }

    private static bool ParameterContractWritesOtherMemory(AbiParameterSymbol parameter)
    {
        return parameter.SourceType.Kind is StarkTypeKind.Slice or StarkTypeKind.Dynamic
            && (parameter.SourceType.InitializationKind != StarkInitializationKind.None
                || parameter.SourceType.BorrowKind != StarkBorrowKind.None && parameter.SourceType.IsMutableView);
    }

    private static bool ParameterContractReadsMustBePreserved(AbiParameterSymbol parameter)
    {
        return parameter.SourceType.Kind is StarkTypeKind.RawPointer or StarkTypeKind.Slice;
    }

    private static bool ParameterContractWritesMustBePreserved(AbiParameterSymbol parameter)
    {
        return parameter.Kind == AbiParameterKind.SRet
            || parameter.SourceType.InitializationKind != StarkInitializationKind.None
            || (parameter.SourceType.Kind == StarkTypeKind.RawPointer && parameter.SourceType.IsMutablePointer);
    }

    private static string EscapeIdentifier(string identifier)
    {
        var builder = new System.Text.StringBuilder(identifier.Length);
        foreach (var ch in identifier)
        {
            builder.Append(char.IsLetterOrDigit(ch) || ch == '_' ? ch : '_');
        }

        return builder.ToString();
    }
}
