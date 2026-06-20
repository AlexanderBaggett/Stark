using System.Globalization;
using System.Numerics;
using System.Text;
using Stark.Parsing;

namespace Stark.Compiler.LlvmIrEmission;

internal sealed partial class LlvmFunctionBodyEmitter
{
    private static string FormatUnsignedI64Constant(ulong value)
    {
        return value <= long.MaxValue
            ? value.ToString(CultureInfo.InvariantCulture)
            : unchecked((long)value).ToString(CultureInfo.InvariantCulture);
    }

    private string FormatValue(SsaValue value)
    {
        return value switch
        {
            SsaValueReference reference => FormatValueReference(reference),
            SsaIntegerConstant integer => integer.Value.ToString(),
            SsaFloatConstant floating => FormatFloatLiteral(floating),
            SsaStringConstant text => FormatStringConstantValue(text),
            SsaTextDataAddressValue textData => FormatStringDataPointer(textData.LiteralText, textData.TextType),
            SsaBoolConstant boolean => boolean.Value ? "true" : "false",
            SsaNullConstant => "null",
            SsaGlobalAddressValue globalAddress => $"@{EscapeIdentifier(ResolveGlobalSymbolName(globalAddress.GlobalName))}",
            SsaFunctionAddressValue functionAddress => $"@{EscapeIdentifier(ResolveFunctionAddressSymbolName(functionAddress.FunctionName))}",
            SsaClosureValue closure => FormatClosureValue(closure),
            SsaZeroInitializerValue => "zeroinitializer",
            SsaUndefValue => "undef",
            _ => throw new UnsupportedBodyEmissionException($"Unsupported SSA value '{value.GetType().Name}'.")
        };
    }

    private string ResolveFunctionAddressSymbolName(string functionName)
    {
        return _resolveCallAbi(_function.Name, functionName)?.SymbolName ?? functionName;
    }

    private string FormatClosureValue(SsaClosureValue closure)
    {
        var invokePointer = $"ptr @{EscapeIdentifier(ResolveFunctionAddressSymbolName(closure.InvokeFunctionName))}";
        if (closure.Type.ClosureStorageKind != StarkClosureStorageKind.Heap)
        {
            return $"{{ {invokePointer}, ptr null }}";
        }

        return $"{{ {invokePointer}, ptr null, ptr @{EscapeIdentifier(ResolveFunctionAddressSymbolName(CallableValueFacts.EmptyClosureDropFunctionName))} }}";
    }

    private string GetInvariantLoadMetadataSuffix(string globalName)
    {
        return IsImmutableGlobalName(globalName)
            ? $", !invariant.load {EmptyMetadataRef}"
            : string.Empty;
    }

    private string GetInvariantLoadMetadataSuffixForAggregateSource(SsaValue value)
    {
        return IsPermanentInvariantAggregateSource(value, new HashSet<string>(StringComparer.Ordinal))
            ? $", !invariant.load {EmptyMetadataRef}"
            : string.Empty;
    }

    private string GetInvariantLoadMetadataSuffix(SsaValue address)
    {
        return IsPermanentInvariantMemoryReference(address, new HashSet<string>(StringComparer.Ordinal))
            ? $", !invariant.load {EmptyMetadataRef}"
            : string.Empty;
    }

    private string GetInvariantLocalLoadMetadataSuffix(string localName)
    {
        return string.Empty;
    }

    private string GetValueRangeMetadataSuffix(StarkTypeSymbol type)
    {
        return _context.GetValueRangeMetadataRef(type) is { } rangeMetadataRef
            ? $", !range {rangeMetadataRef}"
            : string.Empty;
    }

    private string GetValueRangeMetadataSuffix(string valueName, StarkTypeSymbol type)
    {
        if (_valueFacts.TryGetValue(valueName, out var facts)
            && facts.IntegerRangeKind == SsaFactLatticeKind.Known
            && facts.IntegerRange is { } range
            && _context.GetValueRangeMetadataRef(type, range) is { } factRangeMetadataRef)
        {
            return $", !range {factRangeMetadataRef}";
        }

        return GetValueRangeMetadataSuffix(type);
    }

    private IReadOnlyDictionary<string, string> BuildSameParameterCanonicalRootKeys()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var aliasClass in BuildSameParameterAliasClasses())
        {
            foreach (var parameterName in aliasClass.ParameterNames)
            {
                map[CreateScopedAliasParameterRootKey(parameterName)] = aliasClass.Root.Key;
            }
        }

        return map;
    }

    private IReadOnlyList<SameParameterAliasClass> BuildSameParameterAliasClasses()
    {
        if (_function.SameGroups.Count == 0)
        {
            return [];
        }

        var memoryBackedParameters = _abiFunction.UserParameters
            .Where(static parameter => ParameterMemoryContractFacts.IsMemoryBacked(parameter.SourceType))
            .Select(static parameter => parameter.SourceName)
            .ToHashSet(StringComparer.Ordinal);
        if (memoryBackedParameters.Count < 2)
        {
            return [];
        }

        var parent = memoryBackedParameters.ToDictionary(static name => name, static name => name, StringComparer.Ordinal);
        foreach (var group in _function.SameGroups)
        {
            var names = group.ParameterNames
                .Where(memoryBackedParameters.Contains)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (names.Length < 2)
            {
                continue;
            }

            var first = names[0];
            for (var index = 1; index < names.Length; index++)
            {
                Union(first, names[index]);
            }
        }

        return memoryBackedParameters
            .GroupBy(Find, StringComparer.Ordinal)
            .Select(static group => group.Order(StringComparer.Ordinal).ToArray())
            .Where(static names => names.Length >= 2)
            .Select(static names =>
            {
                var classKey = "same-param:" + string.Join("+", names.Select(CreateScopedAliasParameterRootKey));
                var displayName = "same." + string.Join(".", names.Select(static name => $"param.{name}"));
                return new SameParameterAliasClass(
                    new ScopedNoAliasRoot(classKey, displayName),
                    names.ToHashSet(StringComparer.Ordinal));
            })
            .ToArray();

        string Find(string name)
        {
            var current = name;
            while (!string.Equals(parent[current], current, StringComparison.Ordinal))
            {
                current = parent[current];
            }

            var root = current;
            current = name;
            while (!string.Equals(parent[current], current, StringComparison.Ordinal))
            {
                var next = parent[current];
                parent[current] = root;
                current = next;
            }

            return root;
        }

        void Union(string left, string right)
        {
            var leftRoot = Find(left);
            var rightRoot = Find(right);
            if (string.Equals(leftRoot, rightRoot, StringComparison.Ordinal))
            {
                return;
            }

            if (string.CompareOrdinal(leftRoot, rightRoot) <= 0)
            {
                parent[rightRoot] = leftRoot;
            }
            else
            {
                parent[leftRoot] = rightRoot;
            }
        }
    }

    private string CanonicalizeScopedNoAliasRootKey(string rootKey)
    {
        if (_sameParameterCanonicalRootKeys.TryGetValue(rootKey, out var canonicalRootKey))
        {
            return canonicalRootKey;
        }

        if (TryParseScopedNoAliasRegionRootKey(rootKey, out var baseRootKey, out var min, out var max)
            && _sameParameterCanonicalRootKeys.TryGetValue(baseRootKey, out var canonicalBaseRootKey))
        {
            var rangeText = min == max
                ? min.ToString(CultureInfo.InvariantCulture)
                : $"{min.ToString(CultureInfo.InvariantCulture)}..{max.ToString(CultureInfo.InvariantCulture)}";
            return $"{canonicalBaseRootKey}[{rangeText}]";
        }

        return rootKey;
    }

    private ScopedNoAliasMetadataModel? BuildScopedNoAliasMetadata(
        IReadOnlyDictionary<string, ParameterMemoryEffectSummary>? parameterEffects)
    {
        var roots = new Dictionary<string, ScopedNoAliasRoot>(StringComparer.Ordinal);
        foreach (var sameClass in BuildEligibleSameParameterAliasClasses(parameterEffects))
        {
            roots.TryAdd(sameClass.Root.Key, sameClass.Root);
        }

        foreach (var parameter in _abiFunction.Parameters)
        {
            if (TryCreateScopedNoAliasParameterRoot(parameter, parameterEffects, out var root))
            {
                var canonicalKey = CanonicalizeScopedNoAliasRootKey(root.Key);
                if (string.Equals(canonicalKey, root.Key, StringComparison.Ordinal))
                {
                    roots.TryAdd(root.Key, root);
                }
            }
        }

        foreach (var root in CollectFreshResultScopedNoAliasRoots())
        {
            roots.TryAdd(root.Key, root);
        }

        foreach (var root in CollectDynamicLocalScopedNoAliasRoots())
        {
            roots.TryAdd(root.Key, root);
        }

        if (roots.Count < 2)
        {
            return null;
        }

        var domainKey = $"function:{_abiFunction.SymbolName}";
        var domainRef = _context.GetAliasScopeDomainRef(domainKey, $"stark.noalias.{_abiFunction.SymbolName}");
        var scopeRefs = roots.Values.ToDictionary(
            static root => root.Key,
            root => _context.GetAliasScopeRef(
                $"{domainKey}:{root.Key}",
                domainRef,
                $"stark.noalias.{_abiFunction.SymbolName}.{root.DisplayName}"),
            StringComparer.Ordinal);

        var accessMetadata = new Dictionary<string, ScopedNoAliasAccessMetadata>(StringComparer.Ordinal);
        foreach (var root in roots.Values)
        {
            var aliasScopeListRef = _context.GetMetadataTupleRef([scopeRefs[root.Key]]);
            var noAliasScopeRefs = scopeRefs
                .Where(scope => !string.Equals(scope.Key, root.Key, StringComparison.Ordinal))
                .Select(static scope => scope.Value)
                .ToArray();
            var noAliasListRef = noAliasScopeRefs.Length == 0
                ? null
                : _context.GetMetadataTupleRef(noAliasScopeRefs);
            accessMetadata[root.Key] = new ScopedNoAliasAccessMetadata(aliasScopeListRef, noAliasListRef);
        }

        return new ScopedNoAliasMetadataModel(accessMetadata, scopeRefs);
    }

    private IReadOnlyList<SameParameterAliasClass> BuildEligibleSameParameterAliasClasses(
        IReadOnlyDictionary<string, ParameterMemoryEffectSummary>? parameterEffects)
    {
        var classes = BuildSameParameterAliasClasses();
        if (classes.Count == 0)
        {
            return [];
        }

        var memoryBackedParameterNames = _abiFunction.UserParameters
            .Where(static parameter => ParameterMemoryContractFacts.IsMemoryBacked(parameter.SourceType))
            .Select(static parameter => parameter.SourceName)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (memoryBackedParameterNames.Length <= 1)
        {
            return [];
        }

        return classes
            .Where(aliasClass => IsEligibleSameParameterAliasClass(aliasClass, memoryBackedParameterNames, parameterEffects))
            .ToArray();
    }

    private bool IsEligibleSameParameterAliasClass(
        SameParameterAliasClass aliasClass,
        IReadOnlyList<string> memoryBackedParameterNames,
        IReadOnlyDictionary<string, ParameterMemoryEffectSummary>? parameterEffects)
    {
        if (aliasClass.ParameterNames.Count < 2)
        {
            return false;
        }

        foreach (var parameterName in aliasClass.ParameterNames)
        {
            if (!TryGetAbiUserParameter(parameterName, out var parameter)
                || !CanCreateScopedNoAliasParameterRoot(parameter, parameterEffects))
            {
                return false;
            }
        }

        foreach (var otherName in memoryBackedParameterNames)
        {
            if (aliasClass.ParameterNames.Contains(otherName))
            {
                continue;
            }

            foreach (var parameterName in aliasClass.ParameterNames)
            {
                if (!DisjointGroupsContainPair(_function.DisjointGroups, parameterName, otherName))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private bool TryCreateScopedNoAliasParameterRoot(
        AbiParameterSymbol parameter,
        IReadOnlyDictionary<string, ParameterMemoryEffectSummary>? parameterEffects,
        out ScopedNoAliasRoot root)
    {
        root = default;

        var rootKey = CreateScopedAliasParameterRootKey(parameter.SourceName);
        if (_scopedNoAliasUnsafeAddressRoots.Contains(rootKey)
            || !IsScopedNoAliasParameter(parameter, parameterEffects))
        {
            return false;
        }

        var displayName = parameter.Kind == AbiParameterKind.SRet
            ? "sret"
            : $"param.{parameter.SourceName}";
        root = new ScopedNoAliasRoot(rootKey, displayName);
        return true;
    }

    private bool CanCreateScopedNoAliasParameterRoot(
        AbiParameterSymbol parameter,
        IReadOnlyDictionary<string, ParameterMemoryEffectSummary>? parameterEffects)
    {
        var rootKey = CreateScopedAliasParameterRootKey(parameter.SourceName);
        return !_scopedNoAliasUnsafeAddressRoots.Contains(rootKey)
            && ParameterMemoryContractFacts.IsMemoryBacked(parameter.SourceType)
            && (parameter.Kind != AbiParameterKind.IndirectIn
                || parameter.SourceType.InitializationKind != StarkInitializationKind.None
                || parameter.SourceType.BorrowKind != StarkBorrowKind.None
                || AbiLoweringHeuristics.IsByValueIndirectParameter(parameter)
                || TryGetParameterEffect(parameter, parameterEffects, out _));
    }

    private bool IsScopedNoAliasParameter(
        AbiParameterSymbol parameter,
        IReadOnlyDictionary<string, ParameterMemoryEffectSummary>? parameterEffects)
    {
        if (parameter.Kind == AbiParameterKind.SRet)
        {
            return true;
        }

        if (parameter.Kind != AbiParameterKind.IndirectIn)
        {
            return TryGetParameterEffect(parameter, parameterEffects, out var directEffects)
                && directEffects.IsMemoryBacked
                && directEffects.GuaranteedNoAlias;
        }

        return parameter.SourceType.InitializationKind != StarkInitializationKind.None
            || (parameter.SourceType.BorrowKind != StarkBorrowKind.None && parameter.SourceType.IsMutableView)
            || AbiLoweringHeuristics.IsByValueIndirectParameter(parameter)
            || (TryGetParameterEffect(parameter, parameterEffects, out var indirectEffects)
                && indirectEffects.IsMemoryBacked
                && indirectEffects.GuaranteedNoAlias);
    }

    private static bool TryGetParameterEffect(
        AbiParameterSymbol parameter,
        IReadOnlyDictionary<string, ParameterMemoryEffectSummary>? parameterEffects,
        out ParameterMemoryEffectSummary effects)
    {
        if (parameterEffects is not null
            && (parameterEffects.TryGetValue(parameter.SourceName, out effects!)
                || parameterEffects.TryGetValue(parameter.LlvmName, out effects!)))
        {
            return true;
        }

        effects = default!;
        return false;
    }

    private IReadOnlyList<ScopedNoAliasRoot> CollectFreshResultScopedNoAliasRoots()
    {
        var roots = new List<ScopedNoAliasRoot>();
        foreach (var block in _ssaFunction.Blocks)
        {
            foreach (var instruction in block.Instructions)
            {
                if (instruction is not SsaValueInstruction { Value: SsaCallRValue call } valueInstruction)
                {
                    continue;
                }

                var abiCallee = _resolveCallAbi(_function.Name, call.FunctionName);
                if (abiCallee?.ReturnsIndirect == true)
                {
                    roots.Add(new ScopedNoAliasRoot(
                        CreateScopedAliasFreshResultRootKey(valueInstruction.ResultName),
                        $"fresh.{valueInstruction.ResultName}"));
                }
            }
        }

        return roots;
    }

    private IReadOnlyList<ScopedNoAliasRoot> CollectDynamicLocalScopedNoAliasRoots()
    {
        var roots = new List<ScopedNoAliasRoot>();
        foreach (var instruction in _ssaFunction.Blocks.SelectMany(static block => block.Instructions))
        {
            if (instruction is not SsaAllocateLocalInstruction { LocalType.Kind: StarkTypeKind.Dynamic } allocateLocal)
            {
                continue;
            }

            var rootKey = CreateScopedAliasDynamicLocalRootKey(allocateLocal.LocalName);
            if (_scopedNoAliasUnsafeAddressRoots.Contains(rootKey))
            {
                continue;
            }

            roots.Add(new ScopedNoAliasRoot(
                rootKey,
                $"dynamic.{allocateLocal.LocalName}"));
        }

        return roots;
    }

    private string GetScopedNoAliasMetadataSuffix(
        SsaValue address,
        IReadOnlyList<ScopedNoAliasGroup>? scopedNoAliasGroups = null)
    {
        if (TryResolveScopedNoAliasRoot(
                address,
                new HashSet<string>(StringComparer.Ordinal),
                out var runtimeRootKey,
                BuildScopedNoAliasRootSet(scopedNoAliasGroups))
            && GetRuntimeScopedNoAliasMetadataSuffix(runtimeRootKey, scopedNoAliasGroups) is { Length: > 0 } runtimeSuffix)
        {
            return runtimeSuffix;
        }

        return TryResolveScopedNoAliasRoot(
                address,
                new HashSet<string>(StringComparer.Ordinal),
                out var rootKey)
            ? GetScopedNoAliasMetadataSuffix(rootKey)
            : string.Empty;
    }

    private string GetOptimizedRawPointerLoopScopedNoAliasMetadataSuffix(params SsaValue?[] accessedRoots)
    {
        if (_scopedNoAliasMetadata is null)
        {
            return string.Empty;
        }

        var rootKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var root in accessedRoots)
        {
            if (root is not null
                && TryResolveScopedNoAliasRoot(
                    root,
                    new HashSet<string>(StringComparer.Ordinal),
                    out var rootKey))
            {
                rootKeys.Add(rootKey);
            }
        }

        if (rootKeys.Count == 0)
        {
            return string.Empty;
        }

        var aliasScopeRefs = rootKeys
            .Select(rootKey => _scopedNoAliasMetadata.ScopeRefs.TryGetValue(rootKey, out var scopeRef) ? scopeRef : null)
            .Where(static scopeRef => scopeRef is not null)
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (aliasScopeRefs.Length == 0)
        {
            return string.Empty;
        }

        var suffix = $", !alias.scope {_context.GetMetadataTupleRef(aliasScopeRefs)}";
        var noAliasScopeRefs = _scopedNoAliasMetadata.ScopeRefs
            .Where(scope => !rootKeys.Contains(scope.Key))
            .Select(static scope => scope.Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (noAliasScopeRefs.Length != 0)
        {
            suffix += $", !noalias {_context.GetMetadataTupleRef(noAliasScopeRefs)}";
        }

        return suffix;
    }

    private string GetScopedNoAliasMetadataSuffixForSlice(
        SsaValue slice,
        IReadOnlyList<ScopedNoAliasGroup>? scopedNoAliasGroups = null)
    {
        if (TryResolveScopedNoAliasSliceRoot(
                slice,
                new HashSet<string>(StringComparer.Ordinal),
                out var runtimeRootKey,
                BuildScopedNoAliasRootSet(scopedNoAliasGroups))
            && GetRuntimeScopedNoAliasMetadataSuffix(runtimeRootKey, scopedNoAliasGroups) is { Length: > 0 } runtimeSuffix)
        {
            return runtimeSuffix;
        }

        return TryResolveScopedNoAliasSliceRoot(
                slice,
                new HashSet<string>(StringComparer.Ordinal),
                out var rootKey)
            ? GetScopedNoAliasMetadataSuffix(rootKey)
            : string.Empty;
    }

    private string GetScopedNoAliasMetadataSuffix(string rootKey)
    {
        if (_scopedNoAliasMetadata is null
            || !_scopedNoAliasMetadata.Accesses.TryGetValue(rootKey, out var metadata)
            || metadata.NoAliasListRef is null)
        {
            return string.Empty;
        }

        return $", !alias.scope {metadata.AliasScopeListRef}, !noalias {metadata.NoAliasListRef}";
    }

    private string GetRuntimeScopedNoAliasMetadataSuffix(
        string rootKey,
        IReadOnlyList<ScopedNoAliasGroup>? scopedNoAliasGroups)
    {
        if (scopedNoAliasGroups is null || scopedNoAliasGroups.Count == 0)
        {
            return string.Empty;
        }

        var aliasScopeRefs = new List<string>();
        var noAliasScopeRefs = new List<string>();
        foreach (var group in scopedNoAliasGroups)
        {
            var rootKeys = group.RootKeys
                .Where(static key => !string.IsNullOrWhiteSpace(key))
                .Select(CanonicalizeScopedNoAliasRootKey)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (rootKeys.Length < 2 || !rootKeys.Contains(rootKey, StringComparer.Ordinal))
            {
                continue;
            }

            var domainKey = $"runtime-disjoint:{_abiFunction.SymbolName}:{group.ScopeId}";
            var domainRef = _context.GetAliasScopeDomainRef(
                domainKey,
                $"stark.noalias.{_abiFunction.SymbolName}.{group.ScopeId}");
            foreach (var candidateRootKey in rootKeys)
            {
                var scopeRef = _context.GetAliasScopeRef(
                    $"{domainKey}:{candidateRootKey}",
                    domainRef,
                    $"stark.noalias.{_abiFunction.SymbolName}.{group.ScopeId}.{FormatScopedNoAliasRootDisplayName(candidateRootKey)}");
                if (string.Equals(candidateRootKey, rootKey, StringComparison.Ordinal))
                {
                    aliasScopeRefs.Add(scopeRef);
                }
                else
                {
                    noAliasScopeRefs.Add(scopeRef);
                }
            }
        }

        if (aliasScopeRefs.Count == 0 || noAliasScopeRefs.Count == 0)
        {
            return string.Empty;
        }

        var aliasScopeListRef = _context.GetMetadataTupleRef(aliasScopeRefs.Distinct(StringComparer.Ordinal).ToArray());
        var noAliasListRef = _context.GetMetadataTupleRef(noAliasScopeRefs.Distinct(StringComparer.Ordinal).ToArray());
        return $", !alias.scope {aliasScopeListRef}, !noalias {noAliasListRef}";
    }

    private ISet<string>? BuildScopedNoAliasRootSet(IReadOnlyList<ScopedNoAliasGroup>? groups)
    {
        return groups is null || groups.Count == 0
            ? null
            : groups
                .SelectMany(static group => group.RootKeys)
                .Where(static key => !string.IsNullOrWhiteSpace(key))
                .Select(CanonicalizeScopedNoAliasRootKey)
                .ToHashSet(StringComparer.Ordinal);
    }

    private static string FormatScopedNoAliasRootDisplayName(string rootKey)
    {
        return rootKey.Replace(':', '.');
    }

    private bool TryResolveScopedNoAliasRoot(
        SsaValue address,
        ISet<string> visitedValueNames,
        out string rootKey,
        ISet<string>? allowedRootKeys = null)
    {
        switch (address)
        {
            case SsaValueReference reference:
                if (!visitedValueNames.Add(reference.Name))
                {
                    rootKey = string.Empty;
                    return false;
                }

                if (_valueDefinitions.TryGetValue(reference.Name, out var definition))
                {
                    return TryResolveScopedNoAliasRoot(definition, visitedValueNames, out rootKey, allowedRootKeys);
                }

                return TryResolveScopedNoAliasParameterReference(reference.Name, out rootKey, allowedRootKeys);
            default:
                rootKey = string.Empty;
                return false;
        }
    }

    private bool TryResolveScopedNoAliasRoot(
        SsaRValue address,
        ISet<string> visitedValueNames,
        out string rootKey,
        ISet<string>? allowedRootKeys = null)
    {
        switch (address)
        {
            case SsaUseRValue use:
                return TryResolveScopedNoAliasRoot(use.Value, visitedValueNames, out rootKey, allowedRootKeys);
            case SsaExtractFieldRValue { FieldName: "Data", Target.Type.Kind: StarkTypeKind.Dynamic } extractField:
                return TryResolveScopedNoAliasDynamicRoot(extractField.Target, visitedValueNames, out rootKey, allowedRootKeys);
            case SsaAddressOfParameterRValue addressOfParameter:
                return TryUseScopedNoAliasRoot(
                    CreateScopedAliasParameterRootKey(addressOfParameter.ParameterName),
                    out rootKey,
                    allowedRootKeys);
            case SsaFieldAddressRValue fieldAddress:
                return TryResolveScopedNoAliasRoot(fieldAddress.Address, visitedValueNames, out rootKey, allowedRootKeys);
            case SsaElementAddressRValue elementAddress:
                if (TryResolveScopedNoAliasElementRegionRoot(elementAddress, visitedValueNames, out rootKey, allowedRootKeys))
                {
                    return true;
                }

                return TryResolveScopedNoAliasRoot(elementAddress.Address, visitedValueNames, out rootKey, allowedRootKeys);
            case SsaSliceElementAddressRValue sliceElementAddress:
                return TryResolveScopedNoAliasSliceRoot(sliceElementAddress.Slice, visitedValueNames, out rootKey, allowedRootKeys);
            default:
                rootKey = string.Empty;
                return false;
        }
    }

    private bool TryResolveScopedNoAliasElementRegionRoot(
        SsaElementAddressRValue elementAddress,
        ISet<string> visitedValueNames,
        out string rootKey,
        ISet<string>? allowedRootKeys)
    {
        rootKey = string.Empty;
        if (allowedRootKeys is null || allowedRootKeys.Count == 0)
        {
            return false;
        }

        if (!TryResolveScopedAliasRootUnchecked(
                elementAddress.Address,
                new HashSet<string>(visitedValueNames, StringComparer.Ordinal),
                out var baseRootKey)
            || !TryGetElementAddressIndexRange(elementAddress, out var indexMin, out var indexMax))
        {
            return false;
        }

        var candidateRootKey = indexMin == indexMax
            ? $"{baseRootKey}[{indexMin.ToString(CultureInfo.InvariantCulture)}]"
            : $"{baseRootKey}[{indexMin.ToString(CultureInfo.InvariantCulture)}..{indexMax.ToString(CultureInfo.InvariantCulture)}]";
        return TryUseScopedNoAliasRoot(candidateRootKey, out rootKey, allowedRootKeys);
    }

    private bool TryResolveScopedAliasRootUnchecked(
        SsaValue address,
        ISet<string> visitedValueNames,
        out string rootKey)
    {
        switch (address)
        {
            case SsaValueReference reference:
                if (!visitedValueNames.Add(reference.Name))
                {
                    rootKey = string.Empty;
                    return false;
                }

                if (_valueDefinitions.TryGetValue(reference.Name, out var definition))
                {
                    return TryResolveScopedAliasRootUnchecked(definition, visitedValueNames, out rootKey);
                }

                if (TryResolveScopedAliasParameterRootUnchecked(reference.Name, out rootKey))
                {
                    return true;
                }

                rootKey = string.Empty;
                return false;
            default:
                rootKey = string.Empty;
                return false;
        }
    }

    private bool TryResolveScopedAliasRootUnchecked(
        SsaRValue address,
        ISet<string> visitedValueNames,
        out string rootKey)
    {
        switch (address)
        {
            case SsaUseRValue use:
                return TryResolveScopedAliasRootUnchecked(use.Value, visitedValueNames, out rootKey);
            case SsaAddressOfParameterRValue addressOfParameter:
                rootKey = CreateScopedAliasParameterRootKey(addressOfParameter.ParameterName);
                return true;
            case SsaFieldAddressRValue fieldAddress:
                return TryResolveScopedAliasRootUnchecked(fieldAddress.Address, visitedValueNames, out rootKey);
            case SsaElementAddressRValue elementAddress:
                return TryResolveScopedAliasRootUnchecked(elementAddress.Address, visitedValueNames, out rootKey);
            default:
                rootKey = string.Empty;
                return false;
        }
    }

    private bool TryResolveScopedAliasParameterRootUnchecked(string parameterName, out string rootKey)
    {
        var parameter = _abiFunction.Parameters.FirstOrDefault(
            candidate => string.Equals(candidate.SourceName, parameterName, StringComparison.Ordinal)
                || string.Equals(candidate.LlvmName, parameterName, StringComparison.Ordinal));
        if (parameter is not null)
        {
            rootKey = CreateScopedAliasParameterRootKey(parameter.SourceName);
            return true;
        }

        if (parameterName.StartsWith("arg_", StringComparison.Ordinal) && parameterName.Length > 4)
        {
            rootKey = CreateScopedAliasParameterRootKey(parameterName[4..]);
            return true;
        }

        rootKey = string.Empty;
        return false;
    }

    private bool TryGetElementAddressIndexRange(
        SsaElementAddressRValue elementAddress,
        out BigInteger indexMin,
        out BigInteger indexMax)
    {
        if (elementAddress.ConstantIndex is { } constantIndex)
        {
            indexMin = constantIndex;
            indexMax = constantIndex;
            return true;
        }

        if (elementAddress.Index is { } index
            && TryGetIntegerValueRange(index, new HashSet<string>(StringComparer.Ordinal), out indexMin, out indexMax)
            && indexMin >= BigInteger.Zero)
        {
            return true;
        }

        indexMin = default;
        indexMax = default;
        return false;
    }

    private bool TryResolveScopedNoAliasDynamicRoot(
        SsaValue value,
        ISet<string> visitedValueNames,
        out string rootKey,
        ISet<string>? allowedRootKeys = null)
    {
        switch (value)
        {
            case SsaValueReference reference:
                if (!visitedValueNames.Add(reference.Name))
                {
                    rootKey = string.Empty;
                    return false;
                }

                if (_valueDefinitions.TryGetValue(reference.Name, out var definition))
                {
                    return TryResolveScopedNoAliasDynamicRoot(definition, visitedValueNames, out rootKey, allowedRootKeys);
                }

                rootKey = string.Empty;
                return false;
            default:
                rootKey = string.Empty;
                return false;
        }
    }

    private bool TryResolveScopedNoAliasDynamicRoot(
        SsaRValue value,
        ISet<string> visitedValueNames,
        out string rootKey,
        ISet<string>? allowedRootKeys = null)
    {
        switch (value)
        {
            case SsaUseRValue use:
                return TryResolveScopedNoAliasDynamicRoot(use.Value, visitedValueNames, out rootKey, allowedRootKeys);
            case SsaLoadLocalRValue { Type.Kind: StarkTypeKind.Dynamic } loadLocal:
                return TryUseScopedNoAliasRoot(
                    CreateScopedAliasDynamicLocalRootKey(loadLocal.LocalName),
                    out rootKey,
                    allowedRootKeys);
            default:
                rootKey = string.Empty;
                return false;
        }
    }

    private bool TryResolveScopedNoAliasSliceRoot(
        SsaValue slice,
        ISet<string> visitedValueNames,
        out string rootKey,
        ISet<string>? allowedRootKeys = null)
    {
        switch (slice)
        {
            case SsaValueReference reference:
                if (!visitedValueNames.Add(reference.Name))
                {
                    rootKey = string.Empty;
                    return false;
                }

                if (_valueDefinitions.TryGetValue(reference.Name, out var definition))
                {
                    return TryResolveScopedNoAliasSliceRoot(definition, visitedValueNames, out rootKey, allowedRootKeys);
                }

                return TryResolveScopedNoAliasParameterReference(reference.Name, out rootKey, allowedRootKeys);
            default:
                rootKey = string.Empty;
                return false;
        }
    }

    private bool TryResolveScopedNoAliasSliceRoot(
        SsaRValue slice,
        ISet<string> visitedValueNames,
        out string rootKey,
        ISet<string>? allowedRootKeys = null)
    {
        switch (slice)
        {
            case SsaUseRValue use:
                return TryResolveScopedNoAliasSliceRoot(use.Value, visitedValueNames, out rootKey, allowedRootKeys);
            case SsaMakeSliceFromPointerRValue makeSlice:
                if (TryResolveScopedNoAliasSliceRegionRoot(makeSlice, visitedValueNames, out rootKey, allowedRootKeys))
                {
                    return true;
                }

                return TryResolveScopedNoAliasRoot(makeSlice.Pointer, visitedValueNames, out rootKey, allowedRootKeys);
            case SsaLoadLocalRValue loadLocal
                when TryResolveSingleStoreLocalValue(loadLocal.LocalName, out var storedValue):
                return TryResolveScopedNoAliasSliceRoot(storedValue, visitedValueNames, out rootKey, allowedRootKeys);
            case SsaTextSliceRValue textSlice:
                return TryResolveScopedNoAliasSliceRoot(textSlice.TextValue, visitedValueNames, out rootKey, allowedRootKeys);
            case SsaLoadIndirectRValue loadIndirect:
                return TryResolveScopedNoAliasRoot(loadIndirect.Address, visitedValueNames, out rootKey, allowedRootKeys);
            default:
                rootKey = string.Empty;
                return false;
        }
    }

    private bool TryResolveScopedNoAliasSliceRegionRoot(
        SsaMakeSliceFromPointerRValue makeSlice,
        ISet<string> visitedValueNames,
        out string rootKey,
        ISet<string>? allowedRootKeys)
    {
        rootKey = string.Empty;
        if (allowedRootKeys is null || allowedRootKeys.Count == 0)
        {
            return false;
        }

        if (!TryResolveScopedAliasRootUnchecked(
                makeSlice.Pointer,
                new HashSet<string>(visitedValueNames, StringComparer.Ordinal),
                out var baseRootKey)
            || !TryGetIntegerValueRange(makeSlice.Length, new HashSet<string>(StringComparer.Ordinal), out _, out var lengthMax)
            || lengthMax <= BigInteger.Zero)
        {
            return false;
        }

        var candidateRootKey = $"{baseRootKey}[0..{(lengthMax - BigInteger.One).ToString(CultureInfo.InvariantCulture)}]";
        return TryUseScopedNoAliasRoot(candidateRootKey, out rootKey, allowedRootKeys);
    }

    private bool TryResolveScopedNoAliasParameterReference(
        string parameterName,
        out string rootKey,
        ISet<string>? allowedRootKeys = null)
    {
        var parameter = _abiFunction.Parameters.FirstOrDefault(
            candidate => string.Equals(candidate.SourceName, parameterName, StringComparison.Ordinal)
                || string.Equals(candidate.LlvmName, parameterName, StringComparison.Ordinal));
        if (parameter is not null)
        {
            return TryUseScopedNoAliasRoot(CreateScopedAliasParameterRootKey(parameter.SourceName), out rootKey, allowedRootKeys);
        }

        if (parameterName.StartsWith("arg_", StringComparison.Ordinal) && parameterName.Length > 4)
        {
            return TryUseScopedNoAliasRoot(CreateScopedAliasParameterRootKey(parameterName[4..]), out rootKey, allowedRootKeys);
        }

        rootKey = string.Empty;
        return false;
    }

    private bool TryUseScopedNoAliasRoot(
        string candidateRootKey,
        out string rootKey,
        ISet<string>? allowedRootKeys = null)
    {
        var canonicalRootKey = CanonicalizeScopedNoAliasRootKey(candidateRootKey);
        if (allowedRootKeys?.Contains(canonicalRootKey) == true
            || _scopedNoAliasMetadata is not null
                && _scopedNoAliasMetadata.Accesses.ContainsKey(canonicalRootKey))
        {
            rootKey = canonicalRootKey;
            return true;
        }

        if (allowedRootKeys is not null
            && TryFindCoveringScopedNoAliasRegionRoot(canonicalRootKey, allowedRootKeys, out rootKey))
        {
            return true;
        }

        rootKey = string.Empty;
        return false;
    }

    private static bool TryFindCoveringScopedNoAliasRegionRoot(
        string candidateRootKey,
        ISet<string> allowedRootKeys,
        out string rootKey)
    {
        rootKey = string.Empty;
        if (!TryParseScopedNoAliasRegionRootKey(candidateRootKey, out var candidateBase, out var candidateMin, out var candidateMax))
        {
            return false;
        }

        foreach (var allowedRootKey in allowedRootKeys)
        {
            if (TryParseScopedNoAliasRegionRootKey(allowedRootKey, out var allowedBase, out var allowedMin, out var allowedMax)
                && string.Equals(candidateBase, allowedBase, StringComparison.Ordinal)
                && candidateMin >= allowedMin
                && candidateMax <= allowedMax)
            {
                rootKey = allowedRootKey;
                return true;
            }
        }

        return false;
    }

    private static bool TryParseScopedNoAliasRegionRootKey(
        string rootKey,
        out string baseRootKey,
        out BigInteger min,
        out BigInteger max)
    {
        baseRootKey = string.Empty;
        min = default;
        max = default;
        if (rootKey.Length < 4 || rootKey[^1] != ']')
        {
            return false;
        }

        var openBracket = rootKey.LastIndexOf('[', rootKey.Length - 2);
        if (openBracket <= 0)
        {
            return false;
        }

        var rangeText = rootKey[(openBracket + 1)..^1];
        var separator = rangeText.IndexOf("..", StringComparison.Ordinal);
        if (separator > 0)
        {
            if (!BigInteger.TryParse(rangeText[..separator], NumberStyles.None, CultureInfo.InvariantCulture, out min)
                || !BigInteger.TryParse(rangeText[(separator + 2)..], NumberStyles.None, CultureInfo.InvariantCulture, out max))
            {
                return false;
            }
        }
        else if (BigInteger.TryParse(rangeText, NumberStyles.None, CultureInfo.InvariantCulture, out var index))
        {
            min = index;
            max = index;
        }
        else
        {
            return false;
        }

        if (min < BigInteger.Zero || max < min)
        {
            return false;
        }

        baseRootKey = rootKey[..openBracket];
        return true;
    }

    private string GetDirectTbaaMetadataSuffix(string rootKey, StarkTypeSymbol accessType)
    {
        if (_tbaaUnsafeAddressRoots.Contains(rootKey) || !CanUseTbaaAsAccessType(accessType))
        {
            return string.Empty;
        }

        return $", !tbaa {GetSimpleTbaaAccessTagRef(accessType)}";
    }

    private string GetTbaaMetadataSuffix(SsaValue address, StarkTypeSymbol accessType)
    {
        if (!CanUseTbaaAsAccessType(accessType)
            || !TryResolveTbaaAddressAccess(
                address,
                new HashSet<string>(StringComparer.Ordinal),
                out var access))
        {
            return string.Empty;
        }

        var tagRef = access.UseStructPath
            ? GetStructPathTbaaAccessTagRef(access.RootType, accessType, access.OffsetBytes)
            : GetSimpleTbaaAccessTagRef(accessType);
        return $", !tbaa {tagRef}";
    }

    private string GetSliceElementTbaaMetadataSuffix(SsaValue slice, StarkTypeSymbol elementType)
    {
        if (!CanUseTbaaAsAccessType(elementType)
            || !IsTbaaSafeSliceValue(slice, new HashSet<string>(StringComparer.Ordinal)))
        {
            return string.Empty;
        }

        return $", !tbaa {GetSimpleTbaaAccessTagRef(elementType)}";
    }

    private string GetSimpleTbaaAccessTagRef(StarkTypeSymbol accessType)
    {
        var accessDescriptorRef = GetTbaaTypeDescriptorRef(accessType);
        return _context.GetTbaaAccessTagRef(accessDescriptorRef, accessDescriptorRef, 0);
    }

    private string GetStructPathTbaaAccessTagRef(StarkTypeSymbol rootType, StarkTypeSymbol accessType, long offsetBytes)
    {
        var rootDescriptorRef = GetTbaaTypeDescriptorRef(rootType);
        var accessDescriptorRef = GetTbaaTypeDescriptorRef(accessType);
        return _context.GetTbaaAccessTagRef(rootDescriptorRef, accessDescriptorRef, offsetBytes);
    }

    private string GetTbaaTypeDescriptorRef(StarkTypeSymbol type)
    {
        return GetTbaaTypeDescriptorRef(type, new HashSet<string>(StringComparer.Ordinal));
    }

    private string GetTbaaTypeDescriptorRef(StarkTypeSymbol type, ISet<string> activeTypeKeys)
    {
        var normalizedType = NormalizeTbaaStorageType(type);
        var key = GetTbaaTypeKey(normalizedType);
        var displayName = GetTbaaTypeDisplayName(normalizedType);

        if (!activeTypeKeys.Add(key))
        {
            return _context.GetTbaaTypeDescriptorRef(key, displayName);
        }

        try
        {
            switch (normalizedType.Kind)
            {
                case StarkTypeKind.FixedArray
                    when normalizedType.ElementType is not null
                         && normalizedType.FixedLength is int fixedLength
                         && fixedLength is > 0 and <= TbaaFixedArrayFieldLimit
                         && TryGetConcreteTypeLayout(normalizedType.ElementType) is { } elementLayout:
                {
                    var elementDescriptorRef = GetTbaaTypeDescriptorRef(normalizedType.ElementType, activeTypeKeys);
                    var fields = Enumerable.Range(0, fixedLength)
                        .Select(index => (elementDescriptorRef, OffsetBytes: (long)index * elementLayout.SizeBytes))
                        .ToArray();
                    return _context.GetTbaaStructTypeDescriptorRef(key, displayName, fields);
                }
                case StarkTypeKind.Slice:
                case StarkTypeKind.Ascii:
                case StarkTypeKind.Unicode:
                    if (TryBuildViewTbaaFields(normalizedType, activeTypeKeys, out var viewFields))
                    {
                        return _context.GetTbaaStructTypeDescriptorRef(key, displayName, viewFields);
                    }

                    break;
                case StarkTypeKind.Named:
                    if (TryBuildNamedAggregateTbaaFields(normalizedType, activeTypeKeys, out var aggregateFields))
                    {
                        return _context.GetTbaaStructTypeDescriptorRef(key, displayName, aggregateFields);
                    }

                    break;
            }

            return _context.GetTbaaTypeDescriptorRef(key, displayName);
        }
        finally
        {
            activeTypeKeys.Remove(key);
        }
    }

    private bool TryBuildViewTbaaFields(
        StarkTypeSymbol viewType,
        ISet<string> activeTypeKeys,
        out IReadOnlyList<(string TypeDescriptorRef, long OffsetBytes)> fields)
    {
        fields = Array.Empty<(string TypeDescriptorRef, long OffsetBytes)>();

        var elementType = viewType.Kind switch
        {
            StarkTypeKind.Ascii => StarkTypeSymbols.Integer(8),
            StarkTypeKind.Unicode => StarkTypeSymbols.Integer(32),
            _ => viewType.ElementType
        };
        if (elementType is null)
        {
            return false;
        }

        var pointerType = StarkTypeSymbols.RawPointer(elementType, isMutable: false);
        var lengthType = StarkTypeSymbols.Integer(64);
        var pointerLayout = TryGetConcreteTypeLayout(pointerType);
        var lengthLayout = TryGetConcreteTypeLayout(lengthType);
        if (pointerLayout is null || lengthLayout is null)
        {
            return false;
        }

        var lengthOffset = AlignTo(pointerLayout.SizeBytes, lengthLayout.AlignmentBytes);
        fields =
        [
            (GetTbaaTypeDescriptorRef(pointerType, activeTypeKeys), 0L),
            (GetTbaaTypeDescriptorRef(lengthType, activeTypeKeys), (long)lengthOffset)
        ];
        return true;
    }

    private bool TryBuildNamedAggregateTbaaFields(
        StarkTypeSymbol aggregateType,
        ISet<string> activeTypeKeys,
        out IReadOnlyList<(string TypeDescriptorRef, long OffsetBytes)> fields)
    {
        fields = Array.Empty<(string TypeDescriptorRef, long OffsetBytes)>();

        var namedType = ResolveNamedTypeSymbol(aggregateType);
        if (namedType is null || !TryGetScalarizableNamedAggregateFields(namedType, out var orderedFields))
        {
            return false;
        }

        var aggregateLayout = TryGetConcreteTypeLayout(aggregateType);
        var sizeBytes = 0;
        var collectedFields = new List<(string TypeDescriptorRef, long OffsetBytes)>(orderedFields.Count);
        foreach (var field in orderedFields)
        {
            if (aggregateLayout is { FieldLayouts.Count: > 0 })
            {
                if (!aggregateLayout.TryGetField(field.Name, out var concreteFieldLayout))
                {
                    return false;
                }

                collectedFields.Add((GetTbaaTypeDescriptorRef(field.Type, activeTypeKeys), concreteFieldLayout.OffsetBytes));
                continue;
            }

            if (TryGetConcreteTypeLayout(field.Type) is not { } fieldLayout)
            {
                return false;
            }

            var offsetBytes = AlignTo(sizeBytes, fieldLayout.AlignmentBytes);
            collectedFields.Add((GetTbaaTypeDescriptorRef(field.Type, activeTypeKeys), offsetBytes));
            sizeBytes = checked(offsetBytes + fieldLayout.SizeBytes);
        }

        fields = collectedFields;
        return true;
    }

    private bool TryResolveTbaaAddressAccess(
        SsaValue address,
        ISet<string> visitedValueNames,
        out TbaaAddressAccess access)
    {
        switch (address)
        {
            case SsaValueReference reference:
                if (!visitedValueNames.Add(reference.Name))
                {
                    access = default;
                    return false;
                }

                if (_valueDefinitions.TryGetValue(reference.Name, out var definition))
                {
                    return TryResolveTbaaAddressAccess(definition, visitedValueNames, out access);
                }

                return TryResolveTbaaParameterReference(reference, out access);
            case SsaGlobalAddressValue globalAddress:
                return TryCreateTbaaRootAccess(
                    CreateTbaaGlobalRootKey(globalAddress.GlobalName),
                    globalAddress.PointeeType,
                    out access);
            default:
                access = default;
                return false;
        }
    }

    private bool TryResolveTbaaAddressAccess(
        SsaRValue address,
        ISet<string> visitedValueNames,
        out TbaaAddressAccess access)
    {
        switch (address)
        {
            case SsaUseRValue use:
                return TryResolveTbaaAddressAccess(use.Value, visitedValueNames, out access);
            case SsaAddressOfLocalRValue addressOfLocal:
                return TryCreateTbaaRootAccess(
                    CreateTbaaLocalRootKey(addressOfLocal.LocalName),
                    addressOfLocal.PointeeType,
                    out access);
            case SsaAddressOfParameterRValue addressOfParameter:
                return TryCreateTbaaRootAccess(
                    CreateTbaaParameterRootKey(addressOfParameter.ParameterName),
                    GetTbaaParameterAddressRootType(addressOfParameter.PointeeType),
                    out access);
            case SsaFieldAddressRValue fieldAddress:
                return TryResolveTbaaAggregateElementAccess(
                    fieldAddress.Address,
                    fieldAddress.AggregateType,
                    fieldAddress.FieldIndex,
                    visitedValueNames,
                    out access);
            case SsaElementAddressRValue { AggregateType.Kind: StarkTypeKind.FixedArray } elementAddress
                when elementAddress.ConstantIndex is int constantIndex:
                return TryResolveTbaaAggregateElementAccess(
                    elementAddress.Address,
                    elementAddress.AggregateType,
                    constantIndex,
                    visitedValueNames,
                    out access);
            case SsaElementAddressRValue { AggregateType.Kind: StarkTypeKind.FixedArray } elementAddress:
            {
                if (!TryResolveTbaaAddressAccess(elementAddress.Address, visitedValueNames, out var baseAccess)
                    || elementAddress.AggregateType.ElementType is null
                    || !CanEmitTbaaForType(elementAddress.AggregateType.ElementType))
                {
                    access = default;
                    return false;
                }

                access = new TbaaAddressAccess(elementAddress.AggregateType.ElementType, 0, UseStructPath: false);
                return true;
            }
            case SsaSliceElementAddressRValue sliceElementAddress:
            {
                if (!IsTbaaSafeSliceValue(sliceElementAddress.Slice, visitedValueNames)
                    || sliceElementAddress.Type.ElementType is not { } elementType
                    || !CanEmitTbaaForType(elementType))
                {
                    access = default;
                    return false;
                }

                access = new TbaaAddressAccess(elementType, 0, UseStructPath: false);
                return true;
            }
            default:
                access = default;
                return false;
        }
    }

    private bool TryResolveTbaaAggregateElementAccess(
        SsaValue aggregateAddress,
        StarkTypeSymbol aggregateType,
        int elementIndex,
        ISet<string> visitedValueNames,
        out TbaaAddressAccess access)
    {
        if (!TryResolveTbaaAddressAccess(aggregateAddress, visitedValueNames, out var baseAccess)
            || !TryGetAggregateElementOffsetBytes(aggregateType, elementIndex, out var elementType, out var elementOffsetBytes))
        {
            access = default;
            return false;
        }

        if (!CanUseStructPathTbaaForAggregateElement(aggregateType))
        {
            access = new TbaaAddressAccess(elementType, 0, UseStructPath: false);
            return true;
        }

        access = new TbaaAddressAccess(
            baseAccess.RootType,
            checked(baseAccess.OffsetBytes + elementOffsetBytes),
            UseStructPath: true);
        return true;
    }

    private bool TryResolveTbaaParameterReference(SsaValueReference reference, out TbaaAddressAccess access)
    {
        var parameter = _abiFunction.UserParameters.FirstOrDefault(
            candidate => string.Equals(candidate.LlvmName, reference.Name, StringComparison.Ordinal)
                || string.Equals(candidate.SourceName, reference.Name, StringComparison.Ordinal));
        if (parameter is null
            || parameter.Kind != AbiParameterKind.IndirectIn
            || parameter.SourceType.Kind == StarkTypeKind.RawPointer)
        {
            access = default;
            return false;
        }

        return TryCreateTbaaRootAccess(
            CreateTbaaParameterRootKey(parameter.SourceName),
            GetTbaaParameterAddressRootType(parameter.SourceType),
            out access);
    }

    private static StarkTypeSymbol GetTbaaParameterAddressRootType(StarkTypeSymbol parameterType)
    {
        return StarkTypeSymbols.IsPointerBackedBorrowType(parameterType)
            ? StarkTypeSymbols.BorrowReturnValueType(parameterType)
            : parameterType;
    }

    private bool TryCreateTbaaRootAccess(string rootKey, StarkTypeSymbol rootType, out TbaaAddressAccess access)
    {
        if (_tbaaUnsafeAddressRoots.Contains(rootKey) || !CanEmitTbaaForType(rootType))
        {
            access = default;
            return false;
        }

        access = new TbaaAddressAccess(rootType, 0, UseStructPath: false);
        return true;
    }

    private bool TryGetAggregateElementOffsetBytes(
        StarkTypeSymbol aggregateType,
        int elementIndex,
        out StarkTypeSymbol elementType,
        out long offsetBytes)
    {
        elementType = StarkTypeSymbols.Error;
        offsetBytes = 0;

        var normalizedType = NormalizeAggregateType(aggregateType);
        switch (normalizedType.Kind)
        {
            case StarkTypeKind.FixedArray
                when elementIndex >= 0
                     && normalizedType.ElementType is not null
                     && normalizedType.FixedLength is int fixedLength
                     && elementIndex < fixedLength
                     && TryGetConcreteTypeLayout(normalizedType.ElementType) is { } elementLayout:
                elementType = normalizedType.ElementType;
                offsetBytes = (long)elementIndex * elementLayout.SizeBytes;
                return true;
            case StarkTypeKind.Named
                when ResolveNamedTypeSymbol(normalizedType) is { } namedType
                     && TryGetScalarizableNamedAggregateFields(namedType, out var orderedFields)
                     && elementIndex >= 0
                     && elementIndex < orderedFields.Count:
            {
                if (TryGetConcreteTypeLayout(normalizedType) is { FieldLayouts.Count: > 0 } aggregateLayout)
                {
                    var field = orderedFields[elementIndex];
                    if (aggregateLayout.TryGetField(field.Name, out var concreteFieldLayout))
                    {
                        elementType = field.Type;
                        offsetBytes = concreteFieldLayout.OffsetBytes;
                        return true;
                    }
                }

                var sizeBytes = 0;
                for (var index = 0; index <= elementIndex; index++)
                {
                    var field = orderedFields[index];
                    var fieldLayout = TryGetConcreteTypeLayout(field.Type);
                    if (fieldLayout is null)
                    {
                        return false;
                    }

                    var fieldOffsetBytes = AlignTo(sizeBytes, fieldLayout.AlignmentBytes);
                    if (index == elementIndex)
                    {
                        elementType = field.Type;
                        offsetBytes = fieldOffsetBytes;
                        return true;
                    }

                    sizeBytes = checked(fieldOffsetBytes + fieldLayout.SizeBytes);
                }

                return false;
            }
            default:
                return false;
        }
    }

    private static bool CanUseStructPathTbaaForAggregateElement(StarkTypeSymbol aggregateType)
    {
        var normalizedType = NormalizeAggregateType(aggregateType);
        return normalizedType.Kind switch
        {
            StarkTypeKind.FixedArray => normalizedType.FixedLength is >= 0 and <= TbaaFixedArrayFieldLimit,
            StarkTypeKind.Named => true,
            _ => false
        };
    }

    private bool IsTbaaSafeSliceValue(SsaValue slice, ISet<string> visitedValueNames)
    {
        switch (slice)
        {
            case SsaStringConstant:
                return true;
            case SsaValueReference reference:
                if (!visitedValueNames.Add(reference.Name))
                {
                    return false;
                }

                if (_valueDefinitions.TryGetValue(reference.Name, out var definition))
                {
                    return IsTbaaSafeSliceValue(definition, visitedValueNames);
                }

                var parameter = _abiFunction.UserParameters.FirstOrDefault(
                    candidate => string.Equals(candidate.LlvmName, reference.Name, StringComparison.Ordinal)
                        || string.Equals(candidate.SourceName, reference.Name, StringComparison.Ordinal));
                return parameter is { SourceType.Kind: StarkTypeKind.Slice or StarkTypeKind.Ascii or StarkTypeKind.Unicode }
                    && !_tbaaUnsafeAddressRoots.Contains(CreateTbaaParameterRootKey(parameter.SourceName));
            default:
                return false;
        }
    }

    private bool IsTbaaSafeSliceValue(SsaRValue slice, ISet<string> visitedValueNames)
    {
        return slice switch
        {
            SsaUseRValue use => IsTbaaSafeSliceValue(use.Value, visitedValueNames),
            SsaMakeSliceFromLocalRValue makeSlice
                => !_tbaaUnsafeAddressRoots.Contains(CreateTbaaLocalRootKey(makeSlice.LocalName)),
            SsaMakeSliceFromPointerRValue => false,
            SsaTextSliceRValue textSlice => IsTbaaSafeSliceValue(textSlice.TextValue, visitedValueNames),
            _ => false
        };
    }

    private static bool CanEmitTbaaForType(StarkTypeSymbol type)
    {
        return NormalizeTbaaStorageType(type).Kind is
            StarkTypeKind.Bool
            or StarkTypeKind.Ascii
            or StarkTypeKind.Unicode
            or StarkTypeKind.Integer
            or StarkTypeKind.Float
            or StarkTypeKind.FixedArray
            or StarkTypeKind.Slice
            or StarkTypeKind.Named;
    }

    private static bool CanUseTbaaAsAccessType(StarkTypeSymbol type)
    {
        return NormalizeTbaaStorageType(type).Kind is
            StarkTypeKind.Bool
            or StarkTypeKind.Integer
            or StarkTypeKind.Float
            or StarkTypeKind.RawPointer
            or StarkTypeKind.FunctionPointer;
    }

    private static string GetTbaaTypeKey(StarkTypeSymbol type)
    {
        var normalizedType = NormalizeTbaaStorageType(type);
        return normalizedType.Kind switch
        {
            StarkTypeKind.Bool => "bool",
            StarkTypeKind.Integer => $"integer:{normalizedType.BitWidth}",
            StarkTypeKind.Float => $"float:{normalizedType.BitWidth}",
            StarkTypeKind.RawPointer => "rawptr",
            StarkTypeKind.FunctionPointer => "fnptr",
            StarkTypeKind.Ascii => "text:ascii",
            StarkTypeKind.Unicode => "text:unicode",
            StarkTypeKind.FixedArray => $"array:{normalizedType.FixedLength}:{GetTbaaTypeKey(normalizedType.ElementType ?? StarkTypeSymbols.Error)}",
            StarkTypeKind.Slice => $"slice:{GetTbaaTypeKey(normalizedType.ElementType ?? StarkTypeSymbols.Error)}",
            StarkTypeKind.Named => $"named:{normalizedType.NamedType ?? normalizedType.DisplayName}",
            _ => normalizedType.DisplayName
        };
    }

    private static string GetTbaaTypeDisplayName(StarkTypeSymbol type)
    {
        var normalizedType = NormalizeTbaaStorageType(type);
        return normalizedType.Kind switch
        {
            StarkTypeKind.Bool => "stark.bool",
            StarkTypeKind.Integer => $"stark.i{normalizedType.BitWidth}",
            StarkTypeKind.Float => $"stark.f{normalizedType.BitWidth}",
            StarkTypeKind.RawPointer => "stark.ptr",
            StarkTypeKind.FunctionPointer => "stark.fnptr",
            StarkTypeKind.Ascii => "stark.text.ascii",
            StarkTypeKind.Unicode => "stark.text.unicode",
            StarkTypeKind.FixedArray => $"stark.array.{normalizedType.DisplayName}",
            StarkTypeKind.Slice => $"stark.slice.{normalizedType.DisplayName}",
            StarkTypeKind.Named => $"stark.{normalizedType.NamedType ?? normalizedType.DisplayName}",
            _ => $"stark.{normalizedType.DisplayName}"
        };
    }

    private static StarkTypeSymbol NormalizeTbaaStorageType(StarkTypeSymbol type)
    {
        return NormalizeAggregateType(StarkTypeSymbols.BorrowReturnRuntimeType(type));
    }

    private static string CreateTbaaLocalRootKey(string localName) => $"local:{localName}";

    private static string CreateTbaaParameterRootKey(string parameterName) => $"param:{parameterName}";

    private static string CreateTbaaGlobalRootKey(string globalName) => $"global:{globalName}";

    private static string CreateScopedAliasParameterRootKey(string parameterName) => $"param:{parameterName}";

    private static string CreateScopedAliasFreshResultRootKey(string resultName) => $"fresh-result:{resultName}";

    private static string CreateScopedAliasDynamicLocalRootKey(string localName) => $"dynamic-local:{localName}";

    private bool IsPermanentInvariantMemoryReference(SsaValue value, ISet<string> visitedValueNames)
    {
        return value switch
        {
            SsaTextDataAddressValue => true,
            SsaGlobalAddressValue globalAddress => IsImmutableGlobalName(globalAddress.GlobalName),
            SsaValueReference reference => ResolvePermanentInvariantMemoryReference(reference, visitedValueNames),
            _ => false
        };
    }

    private bool ResolvePermanentInvariantMemoryReference(SsaValueReference reference, ISet<string> visitedValueNames)
    {
        if (!visitedValueNames.Add(reference.Name))
        {
            return false;
        }

        if (!_valueDefinitions.TryGetValue(reference.Name, out var definition))
        {
            return false;
        }

        return definition switch
        {
            SsaUseRValue use => IsPermanentInvariantMemoryReference(use.Value, visitedValueNames),
            SsaFieldAddressRValue fieldAddress => IsPermanentInvariantMemoryReference(fieldAddress.Address, visitedValueNames),
            SsaElementAddressRValue elementAddress => IsPermanentInvariantMemoryReference(elementAddress.Address, visitedValueNames),
            SsaSliceElementAddressRValue sliceElementAddress => IsPermanentInvariantMemoryReference(sliceElementAddress.Slice, visitedValueNames),
            SsaMakeSliceFromPointerRValue makeSlice => IsPermanentInvariantMemoryReference(makeSlice.Pointer, visitedValueNames),
            SsaLoadLocalRValue loadLocal when TryResolveSingleStoreLocalValue(loadLocal.LocalName, out var storedValue)
                => IsPermanentInvariantMemoryReference(storedValue, visitedValueNames),
            SsaTextSliceRValue textSlice => IsPermanentInvariantMemoryReference(textSlice.TextValue, visitedValueNames),
            SsaConvertRValue convert when convert.Operand.Type.Kind == StarkTypeKind.RawPointer
                                        && convert.TargetType.Kind == StarkTypeKind.RawPointer
                => IsPermanentInvariantMemoryReference(convert.Operand, visitedValueNames),
            _ => false
        };
    }

    private bool IsPermanentInvariantAggregateSource(SsaValue value, ISet<string> visitedValueNames)
    {
        return value switch
        {
            SsaGlobalAddressValue globalAddress => IsImmutableGlobalName(globalAddress.GlobalName),
            SsaValueReference reference when visitedValueNames.Add(reference.Name)
                                           && _valueDefinitions.TryGetValue(reference.Name, out var definition)
                => definition switch
                {
                    SsaUseRValue use => IsPermanentInvariantAggregateSource(use.Value, visitedValueNames),
                    SsaLoadGlobalRValue loadGlobal => IsImmutableGlobalName(loadGlobal.GlobalName),
                    SsaLoadIndirectRValue loadIndirect => IsPermanentInvariantMemoryReference(loadIndirect.Address, visitedValueNames),
                    _ => false
                },
            _ => IsPermanentInvariantMemoryReference(value, visitedValueNames)
        };
    }

    private static bool IsFrozenReadonlyPointer(StarkTypeSymbol type)
    {
        return type.Kind == StarkTypeKind.RawPointer
            && !type.IsMutablePointer
            && type.ElementType is { } pointeeType
            && IsFrozenReadonlyType(pointeeType);
    }

    private static bool IsFrozenReadonlyView(StarkTypeSymbol type)
    {
        return type.Kind == StarkTypeKind.Slice
            && !type.IsMutableView
            && (type.AccessKind == StarkAccessKind.Frozen
                || type.ElementType is { AccessKind: StarkAccessKind.Frozen });
    }

    private static bool IsFrozenReadonlyType(StarkTypeSymbol type)
    {
        return type.AccessKind == StarkAccessKind.Frozen
            || IsFrozenReadonlyPointer(type)
            || IsFrozenReadonlyView(type);
    }

    private void EmitInvariantStartForLocalIfNeeded(string? localName, StarkTypeSymbol localType)
    {
        if (localName is null
            || !_invariantLocalNames.Contains(localName)
            || TryGetConcreteTypeLayout(localType) is not { } layout)
        {
            return;
        }

        var tokenName = EscapeIdentifier(CreateAbiTempName("invariant"));
        AppendLine($"  %{tokenName} = call ptr @llvm.invariant.start.p0(i64 {layout.SizeBytes}, ptr {GetLocalSlotPointer(localName)})");
    }

    private bool TryResolveLocalAddressRoot(SsaValue address, out string localName)
    {
        return TryResolveLocalAddressRoot(address, new HashSet<string>(StringComparer.Ordinal), out localName);
    }

    private bool TryResolveLocalAddressRoot(
        SsaValue address,
        ISet<string> visitedValueNames,
        out string localName)
    {
        switch (address)
        {
            case SsaValueReference reference:
                if (!visitedValueNames.Add(reference.Name)
                    || !_valueDefinitions.TryGetValue(reference.Name, out var definition))
                {
                    localName = string.Empty;
                    return false;
                }

                return definition switch
                {
                    SsaUseRValue use => TryResolveLocalAddressRoot(use.Value, visitedValueNames, out localName),
                    SsaAddressOfLocalRValue addressOfLocal => ReturnLocalName(addressOfLocal.LocalName, out localName),
                    SsaFieldAddressRValue fieldAddress => TryResolveLocalAddressRoot(fieldAddress.Address, visitedValueNames, out localName),
                    SsaElementAddressRValue elementAddress => TryResolveLocalAddressRoot(elementAddress.Address, visitedValueNames, out localName),
                    SsaConvertRValue convert when convert.Operand.Type.Kind == StarkTypeKind.RawPointer
                                                && convert.TargetType.Kind == StarkTypeKind.RawPointer
                        => TryResolveLocalAddressRoot(convert.Operand, visitedValueNames, out localName),
                    _ => ReturnNoLocalName(out localName)
                };
            default:
                localName = string.Empty;
                return false;
        }
    }

    private static bool ReturnLocalName(string value, out string localName)
    {
        localName = value;
        return true;
    }

    private static bool ReturnNoLocalName(out string localName)
    {
        localName = string.Empty;
        return false;
    }
}
