using System.Globalization;
using System.Numerics;

namespace Stark.Compiler.LlvmIrEmission;

internal sealed class LlvmFunctionAttributeBuilder
{
    private const string CapturesNoneAttribute = "captures(none)";

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

        segments.AddRange(DeriveAbiParameterAttributes(abiFunction, parameter, ResolveParameterEffects(parameter, parameterEffects)));

        if (includeName)
        {
            segments.Add($"%{EscapeIdentifier(parameter.LlvmName)}");
        }

        return string.Join(" ", segments);
    }

    public IReadOnlyList<string> GetAbiParameterAttributes(
        AbiParameterSymbol parameter,
        IReadOnlyDictionary<string, ParameterMemoryEffectSummary>? parameterEffects,
        AbiFunctionSignature? abiFunction = null)
    {
        return DeriveAbiParameterAttributes(abiFunction, parameter, ResolveParameterEffects(parameter, parameterEffects));
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

        AppendReturnPointerAttributes(segments, abiFunction);

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

    private void AppendReturnPointerAttributes(List<string> attributes, AbiFunctionSignature abiFunction)
    {
        if (abiFunction.IsFfi || abiFunction.ReturnsIndirect)
        {
            return;
        }

        if (abiFunction.LlvmReturnType.Kind == StarkTypeKind.FunctionPointer)
        {
            attributes.Add("nonnull");
            return;
        }

        if (abiFunction.LlvmReturnType.Kind != StarkTypeKind.RawPointer
            || !StarkTypeSymbols.IsPointerBackedBorrowReturn(abiFunction.SourceReturnType))
        {
            return;
        }

        attributes.Add("nonnull");
        AppendDereferenceableAttributes(attributes, StarkTypeSymbols.BorrowReturnValueType(abiFunction.SourceReturnType));
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

        if (effects.NoRecurse)
        {
            attributes.Add("norecurse");
        }

        // Stark's x86-64 baseline includes cmpxchg16b (every x86-64 CPU since ~2006):
        // 128-bit atomics (System.Threading.AtomicI96/I128/U96/U128) lower to it. The
        // feature must be stamped on every function — not just the atomic builtins — so
        // the inliner can move atomic operations freely and link-time LTO codegen never
        // falls back to __atomic_* libcalls that freestanding Stark binaries cannot
        // link. (This mirrors how clang applies -m feature flags to every function it
        // emits; toolchain-level flags cannot do this job because they do not survive
        // the IR-to-bitcode-to-LTO pipeline.)
        if (StarkAsmArchitectureFacts.ResolveActiveArchitecture(_context.TargetInfo) == StarkAsmArchitecture.X86_64)
        {
            attributes.Add("\"target-features\"=\"+cx16\"");
        }

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
        AbiFunctionSignature? abiFunction,
        AbiParameterSymbol parameter,
        ParameterMemoryEffectSummary? parameterEffects)
    {
        var attributes = new List<string>();

        if (parameter.Kind == AbiParameterKind.SRet)
        {
            if (!CanReachStoredBorrow(parameter.SourceType))
            {
                attributes.Add("noalias");
            }

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

            if (!CanReachStoredBorrow(parameter.SourceType))
            {
                attributes.Add("noalias");
            }

            AppendPointerMemoryAccessAttributes(attributes, parameter, parameterEffects);
            AppendDestinationInitializationAttributes(attributes, abiFunction, parameter, parameterEffects);
            AppendPointeeDeadOnReturnAttribute(attributes, parameterEffects);
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

        AppendBoundedRawPointerRegionAttributes(attributes, parameter, abiFunction);
        AppendParameterEffectPointerExtentAttributes(attributes, parameterEffects);

        if (parameter.SourceType.BorrowKind != StarkBorrowKind.None
            || parameter.SourceType.InitializationKind != StarkInitializationKind.None)
        {
            AddUniqueAttribute(attributes, "nonnull", insertionIndex: 0);
            AppendDereferenceableAttributes(attributes, parameter.SourceType);
        }

        if (parameter.SourceType.InitializationKind != StarkInitializationKind.None
            || parameterEffects?.GuaranteedNoAlias == true)
        {
            if (!CanReachStoredBorrow(parameter.SourceType))
            {
                attributes.Add("noalias");
            }
        }

        AppendPointerMemoryAccessAttributes(attributes, parameter, parameterEffects);
        AppendDestinationInitializationAttributes(attributes, abiFunction, parameter, parameterEffects);
        AppendPointeeDeadOnReturnAttribute(attributes, parameterEffects);
        AppendCaptureAttribute(attributes, parameterEffects);

        // Plain raw pointers remain nullable and may carry arbitrary raw/FFI
        // provenance unless semantic/lowering facts explicitly prove stronger
        // attributes.
        return attributes;
    }

    private static void AppendParameterEffectPointerExtentAttributes(
        List<string> attributes,
        ParameterMemoryEffectSummary? parameterEffects)
    {
        if (parameterEffects is null)
        {
            return;
        }

        if (parameterEffects.GuaranteedNonNull)
        {
            AddUniqueAttribute(attributes, "nonnull", insertionIndex: 0);
        }

        if (parameterEffects.DereferenceableBytes is > 0)
        {
            AddOrStrengthenDereferenceableAttribute(attributes, parameterEffects.DereferenceableBytes.Value);
        }

        if (parameterEffects.AlignmentBytes is > 1)
        {
            AddOrStrengthenAlignAttribute(attributes, parameterEffects.AlignmentBytes.Value);
        }
    }

    private void AppendBoundedRawPointerRegionAttributes(
        List<string> attributes,
        AbiParameterSymbol parameter,
        AbiFunctionSignature? abiFunction)
    {
        if (parameter.Kind != AbiParameterKind.Direct
            || parameter.SourceType.Kind != StarkTypeKind.RawPointer
            || parameter.SourceType.ElementType is not { } elementType
            || string.IsNullOrWhiteSpace(parameter.RawPointerElementCountExpression)
            || TryGetConcreteTypeLayout(elementType) is not { } elementLayout)
        {
            return;
        }

        if (BigInteger.TryParse(parameter.RawPointerElementCountExpression, NumberStyles.None, CultureInfo.InvariantCulture, out var elementCount)
            && elementCount > BigInteger.Zero)
        {
            AddUniqueAttribute(attributes, "nonnull", insertionIndex: 0);
            var byteCount = elementCount * elementLayout.SizeBytes;
            if (byteCount <= long.MaxValue)
            {
                AddOrStrengthenDereferenceableAttribute(attributes, byteCount);
            }

            if (elementLayout.AlignmentBytes > 1)
            {
                AddOrStrengthenAlignAttribute(attributes, elementLayout.AlignmentBytes);
            }

            return;
        }

        if (!IsBoundedRawPointerCountProvenPositive(parameter.RawPointerElementCountExpression, abiFunction))
        {
            return;
        }

        AddUniqueAttribute(attributes, "nonnull", insertionIndex: 0);
        if (TryGetBoundedRawPointerMinimumByteCount(
                parameter.RawPointerElementCountExpression,
                abiFunction,
                elementLayout,
                out var minimumByteCount))
        {
            AddOrStrengthenDereferenceableAttribute(attributes, minimumByteCount);
        }

        if (elementLayout.AlignmentBytes > 1)
        {
            AddOrStrengthenAlignAttribute(attributes, elementLayout.AlignmentBytes);
        }
    }

    private static bool IsBoundedRawPointerCountProvenPositive(
        string countExpression,
        AbiFunctionSignature? abiFunction)
    {
        if (abiFunction is null)
        {
            return false;
        }

        var countParameter = abiFunction.UserParameters.FirstOrDefault(
            candidate => string.Equals(candidate.SourceName, countExpression, StringComparison.Ordinal));
        return countParameter?.SourceType.Kind == StarkTypeKind.Integer
            && countParameter.SourceType.RangeMin is { } min
            && min > BigInteger.Zero;
    }

    private static bool TryGetBoundedRawPointerMinimumByteCount(
        string countExpression,
        AbiFunctionSignature? abiFunction,
        ConcreteTypeLayout elementLayout,
        out BigInteger minimumByteCount)
    {
        minimumByteCount = default;
        if (abiFunction is null || elementLayout.SizeBytes <= 0)
        {
            return false;
        }

        var countParameter = abiFunction.UserParameters.FirstOrDefault(
            candidate => string.Equals(candidate.SourceName, countExpression, StringComparison.Ordinal));
        if (countParameter?.SourceType.Kind != StarkTypeKind.Integer
            || countParameter.SourceType.RangeMin is not { } min
            || min <= BigInteger.Zero)
        {
            return false;
        }

        minimumByteCount = min * elementLayout.SizeBytes;
        return minimumByteCount <= long.MaxValue;
    }

    private void AppendDereferenceableAttributes(List<string> attributes, StarkTypeSymbol type)
    {
        var storageType = GetPointerBackedStorageType(type);
        if (TryGetConcreteTypeLayout(storageType) is not { } layout)
        {
            return;
        }

        AddOrStrengthenDereferenceableAttribute(attributes, layout.SizeBytes);
        if (layout.AlignmentBytes > 1)
        {
            AddOrStrengthenAlignAttribute(attributes, layout.AlignmentBytes);
        }
    }

    private void AppendDestinationInitializationAttributes(
        List<string> attributes,
        AbiFunctionSignature? abiFunction,
        AbiParameterSymbol parameter,
        ParameterMemoryEffectSummary? parameterEffects)
    {
        if (abiFunction?.IsFfi == true
            || parameter.Kind == AbiParameterKind.SRet
            || !string.Equals(MapType(parameter.LlvmType), "ptr", StringComparison.Ordinal)
            || !TryGetDestinationInitializationRanges(parameter, parameterEffects, out var ranges))
        {
            return;
        }

        var highWaterMark = ranges.Max(static range => range.EndByte);
        if (highWaterMark > 0)
        {
            AddOrStrengthenDereferenceableAttribute(attributes, highWaterMark);
        }

        AddUniqueAttribute(attributes, "writable");
        attributes.Add(RenderInitializesAttribute(ranges));
    }

    private static void AppendPointeeDeadOnReturnAttribute(
        List<string> attributes,
        ParameterMemoryEffectSummary? parameterEffects)
    {
        if (parameterEffects?.PointeeDeadOnReturn == true)
        {
            AddUniqueAttribute(attributes, "dead_on_return");
        }
    }

    private bool TryGetDestinationInitializationRanges(
        AbiParameterSymbol parameter,
        ParameterMemoryEffectSummary? parameterEffects,
        out IReadOnlyList<ParameterInitializationRangeSummary> ranges)
    {
        if (TryNormalizeInitializationRanges(parameterEffects?.InitializationRanges, out ranges))
        {
            return true;
        }

        if (parameterEffects?.GuaranteedWriteOnly == true
            && parameterEffects.DereferenceableBytes is > 0
            && parameter.SourceType.Kind != StarkTypeKind.Slice)
        {
            ranges = [new ParameterInitializationRangeSummary(0, parameterEffects.DereferenceableBytes.Value)];
            return true;
        }

        if (!TryBuildFullDestinationInitializationRange(parameter.SourceType, out var fullRange))
        {
            ranges = [];
            return false;
        }

        ranges = [fullRange];
        return true;
    }

    private bool TryBuildFullDestinationInitializationRange(
        StarkTypeSymbol type,
        out ParameterInitializationRangeSummary range)
    {
        range = default!;
        if (type.InitializationKind == StarkInitializationKind.None)
        {
            return false;
        }

        var storageType = GetPointerBackedStorageType(type);
        if (type.InitializationKind == StarkInitializationKind.Init
            && storageType.Kind == StarkTypeKind.Slice)
        {
            return false;
        }

        if (TryGetConcreteTypeLayout(storageType) is not { SizeBytes: > 0 } layout)
        {
            return false;
        }

        range = new ParameterInitializationRangeSummary(0, layout.SizeBytes);
        return true;
    }

    private static bool TryNormalizeInitializationRanges(
        IReadOnlyList<ParameterInitializationRangeSummary>? candidateRanges,
        out IReadOnlyList<ParameterInitializationRangeSummary> ranges)
    {
        ranges = [];
        if (candidateRanges is not { Count: > 0 })
        {
            return false;
        }

        var normalized = candidateRanges
            .Where(static range => range.StartByte < range.EndByte)
            .OrderBy(static range => range.StartByte)
            .ToArray();
        if (normalized.Length == 0)
        {
            return false;
        }

        for (var index = 1; index < normalized.Length; index++)
        {
            if (normalized[index].StartByte <= normalized[index - 1].EndByte)
            {
                return false;
            }
        }

        ranges = normalized;
        return true;
    }

    private static string RenderInitializesAttribute(IReadOnlyList<ParameterInitializationRangeSummary> ranges)
    {
        var renderedRanges = ranges.Select(static range =>
            $"({range.StartByte.ToString(CultureInfo.InvariantCulture)}, {range.EndByte.ToString(CultureInfo.InvariantCulture)})");
        return $"initializes({string.Join(", ", renderedRanges)})";
    }

    private static StarkTypeSymbol GetPointerBackedStorageType(StarkTypeSymbol type)
    {
        return type.BorrowKind != StarkBorrowKind.None
               || type.InitializationKind != StarkInitializationKind.None
            ? StarkTypeSymbols.WithQualifiers(
                type,
                borrowKind: StarkBorrowKind.None,
                accessKind: StarkAccessKind.None,
                initializationKind: StarkInitializationKind.None,
                isMutableView: false)
            : type;
    }

    private static void AddUniqueAttribute(List<string> attributes, string attribute, int? insertionIndex = null)
    {
        if (attributes.Contains(attribute, StringComparer.Ordinal))
        {
            return;
        }

        attributes.Insert(Math.Clamp(insertionIndex ?? attributes.Count, 0, attributes.Count), attribute);
    }

    private static void AddOrStrengthenDereferenceableAttribute(List<string> attributes, BigInteger byteCount)
    {
        // LLVM rejects `dereferenceable(0)` outright, and a zero extent carries
        // no information: an unresolved instantiated-receiver layout must drop
        // the hint rather than poison the module (seen as 460 invalid
        // attributes in the selfhost.Ir dependency build, 2026-07-07).
        if (byteCount <= BigInteger.Zero)
        {
            return;
        }

        var replacement = $"dereferenceable({byteCount.ToString(CultureInfo.InvariantCulture)})";
        for (var index = 0; index < attributes.Count; index++)
        {
            if (!TryParseDereferenceableAttribute(attributes[index], out var existingByteCount))
            {
                continue;
            }

            if (byteCount > existingByteCount)
            {
                attributes[index] = replacement;
            }

            return;
        }

        attributes.Add(replacement);
    }

    private static bool TryParseDereferenceableAttribute(string attribute, out BigInteger byteCount)
    {
        const string prefix = "dereferenceable(";
        byteCount = default;
        return attribute.StartsWith(prefix, StringComparison.Ordinal)
            && attribute.EndsWith(')')
            && BigInteger.TryParse(
                attribute[prefix.Length..^1],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out byteCount);
    }

    private static void AddOrStrengthenAlignAttribute(List<string> attributes, int alignmentBytes)
    {
        var replacement = $"align {alignmentBytes.ToString(CultureInfo.InvariantCulture)}";
        for (var index = 0; index < attributes.Count; index++)
        {
            if (!attributes[index].StartsWith("align ", StringComparison.Ordinal)
                || !int.TryParse(attributes[index][6..], NumberStyles.None, CultureInfo.InvariantCulture, out var existingAlignmentBytes))
            {
                continue;
            }

            if (alignmentBytes > existingAlignmentBytes)
            {
                attributes[index] = replacement;
            }

            return;
        }

        attributes.Add(replacement);
    }

    private bool CanReachStoredBorrow(StarkTypeSymbol type)
    {
        return CanReachStoredBorrow(type, new HashSet<string>(StringComparer.Ordinal));
    }

    private bool CanReachStoredBorrow(StarkTypeSymbol type, HashSet<string> visitedNamedTypes)
    {
        if (type.BorrowKind == StarkBorrowKind.StoreBorrow)
        {
            return true;
        }

        var valueType = StarkTypeSymbols.BorrowReturnValueType(type);
        if (valueType.Kind == StarkTypeKind.FixedArray && valueType.ElementType is not null)
        {
            return CanReachStoredBorrow(valueType.ElementType, visitedNamedTypes);
        }

        if (valueType.Kind != StarkTypeKind.Named
            || valueType.NamedType is not { } namedTypeName
            || !visitedNamedTypes.Add(namedTypeName))
        {
            return false;
        }

        var namedType = _context.ResolveNamedTypeSymbol(valueType);
        if (namedType is null
            && !_context.TypeModel.NamedTypes.TryGetValue(namedTypeName, out namedType))
        {
            return false;
        }

        foreach (var field in namedType.OrderedFields)
        {
            if (CanReachStoredBorrow(field.Type, visitedNamedTypes))
            {
                return true;
            }
        }

        return false;
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
            attributes.Add(CapturesNoneAttribute);
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
