namespace Stark.Compiler;

internal sealed class AbiLowerer
{
    private readonly SyntaxModel _syntaxModel;
    private readonly LoadedModuleSet _loadedModules;
    private readonly TypeCheckModel _typeModel;
    private readonly EnumLayoutModel _enumLayoutModel;
    private readonly FunctionEffectModel _effectModel;
    private readonly HighLevelIrModule _hir;
    private readonly CompilerOptions _options;
    private readonly DiagnosticBag _diagnostics;
    private readonly IReadOnlyDictionary<string, FunctionIdentity> _functionIdentities;
    private readonly IReadOnlyDictionary<string, ConcreteTypeLayout> _publishedConcreteLayouts;

    public AbiLowerer(
        SyntaxModel syntaxModel,
        LoadedModuleSet loadedModules,
        TypeCheckModel typeModel,
        EnumLayoutModel enumLayoutModel,
        FunctionEffectModel effectModel,
        HighLevelIrModule hir,
        CompilerOptions options,
        DiagnosticBag diagnostics)
    {
        _syntaxModel = syntaxModel;
        _loadedModules = loadedModules;
        _typeModel = typeModel;
        _enumLayoutModel = enumLayoutModel;
        _effectModel = effectModel;
        _hir = hir;
        _options = options;
        _diagnostics = diagnostics;
        _functionIdentities = BuildFunctionIdentityIndex(loadedModules);
        _publishedConcreteLayouts = BuildPublishedConcreteLayouts(loadedModules);
    }

    public AbiModel Lower()
    {
        var functions = new Dictionary<string, AbiFunctionSignature>(StringComparer.Ordinal);
        var ffiLinkageSignatures = new Dictionary<FfiLinkageKey, FfiLinkageSignature>(FfiLinkageKey.Comparer);
        var ffiSymbolNames = CollectFfiSymbolNames();

        foreach (var function in _typeModel.Functions.Values.OrderBy(static function => function.Name, StringComparer.Ordinal))
        {
            if (!_effectModel.Functions.TryGetValue(function.Name, out var effects))
            {
                continue;
            }

            var abiFunction = LowerFunction(function, effects, ffiSymbolNames);
            functions[function.Name] = abiFunction;
            ValidateFfiLinkageSignature(ffiLinkageSignatures, abiFunction, function.DeclarationLocation);
        }

        foreach (var function in _hir.Functions.OrderBy(static function => function.Name, StringComparer.Ordinal))
        {
            if (functions.ContainsKey(function.Name))
            {
                continue;
            }

            var abiFunction = LowerFunction(function.Signature, function.Effects, ffiSymbolNames);
            functions[function.Name] = abiFunction;
            ValidateFfiLinkageSignature(ffiLinkageSignatures, abiFunction, function.Signature.DeclarationLocation);
        }

        foreach (var module in _loadedModules.ImportedModules)
        {
            if (module.PackageImageFacts is not { } packageImageFacts)
            {
                continue;
            }

            foreach (var (qualifiedName, abiSignature) in packageImageFacts.AbiFunctions)
            {
                functions[qualifiedName] = abiSignature;
                ValidateFfiLinkageSignature(ffiLinkageSignatures, abiSignature, location: null);
            }
        }

        return new AbiModel(_typeModel.ModuleName, functions);
    }

    private AbiFunctionSignature LowerFunction(
        TypedFunctionSignature function,
        FunctionEffectProfile effects,
        IReadOnlySet<string> ffiSymbolNames)
    {
        var (moduleName, sourceName, visibility) = ResolveFunctionIdentity(function.Name);
        var parameters = new List<AbiParameterSymbol>();
        var isFfi = effects.IsFfi;
        CAbiAggregateClassification? ffiReturnClassification = null;
        var hasFfiReturnClassification = isFfi
            && CAbiAggregateClassifier.TryClassify(
                function.ReturnType,
                effects.FfiAbi,
                _options.TargetInfo,
                _typeModel.NamedTypes,
                _enumLayoutModel.Layouts,
                _publishedConcreteLayouts,
                out ffiReturnClassification);
        var returnsIndirect = hasFfiReturnClassification
            ? ffiReturnClassification!.PassKind == CAbiAggregatePassKind.Indirect
            : !isFfi && AbiLoweringHeuristics.RequiresIndirectReturnAbi(function.ReturnType, _typeModel.NamedTypes, _enumLayoutModel.Layouts);
        var isOverloaded = !string.Equals(function.Name, function.DisplaySourceName, StringComparison.Ordinal);
        var symbolName = ComputeSymbolName(
            function.Name,
            moduleName,
            sourceName,
            visibility,
            isFfi,
            isOverloaded,
            function.ExternalLinkName,
            ffiSymbolNames);

        if (returnsIndirect)
        {
            parameters.Add(new AbiParameterSymbol(
                SourceName: "$ret",
                LlvmName: "ret",
                SourceType: function.ReturnType,
                LlvmType: StarkTypeSymbols.RawPointer(function.ReturnType, isMutable: true),
                Kind: AbiParameterKind.SRet));
        }

        foreach (var parameter in function.Parameters)
        {
            CAbiAggregateClassification? ffiParameterClassification = null;
            var hasFfiParameterClassification = isFfi
                && CAbiAggregateClassifier.TryClassify(
                    parameter.Type,
                    effects.FfiAbi,
                    _options.TargetInfo,
                    _typeModel.NamedTypes,
                    _enumLayoutModel.Layouts,
                    _publishedConcreteLayouts,
                    out ffiParameterClassification);
            var kind = hasFfiParameterClassification
                ? ffiParameterClassification!.PassKind == CAbiAggregatePassKind.Indirect
                    ? AbiParameterKind.IndirectIn
                    : AbiParameterKind.Direct
                : !isFfi && AbiLoweringHeuristics.RequiresIndirectParameterAbi(parameter.Type, _typeModel.NamedTypes, _enumLayoutModel.Layouts)
                    ? AbiParameterKind.IndirectIn
                    : AbiParameterKind.Direct;

            var llvmType = hasFfiParameterClassification
                ? ffiParameterClassification!.LlvmType
                : LowerAbiValueType(parameter.Type, isFfi, forReturnValue: false);

            parameters.Add(new AbiParameterSymbol(
                SourceName: parameter.Name,
                LlvmName: $"arg_{parameter.Name}",
                SourceType: parameter.Type,
                LlvmType: kind == AbiParameterKind.Direct
                    ? llvmType
                    : StarkTypeSymbols.RawPointer(parameter.Type, isMutable: false),
                Kind: kind,
                RawPointerElementCountExpression: parameter.RawPointerElementCountExpression,
                LlvmParameterTypes: kind == AbiParameterKind.Direct && hasFfiParameterClassification
                    && ffiParameterClassification!.EffectiveLlvmParameterTypes.Count > 1
                    ? ffiParameterClassification.EffectiveLlvmParameterTypes
                    : null));
        }

        if (effects.IsTailCallable)
        {
            if (isFfi || effects.IsVarargs || effects.FfiAbi is not null)
            {
                ReportTailAbiError(
                    function,
                    "Tail-callable functions cannot use FFI, explicit FFI ABI, or varargs lowering because LLVM 'musttail' requires a Stark-owned tailcc ABI.");
            }

            if (returnsIndirect)
            {
                ReportTailAbiError(
                    function,
                    $"Tail-callable function '{function.DisplaySourceName}' cannot return '{function.ReturnType.DisplayName}' by indirect ABI; choose a direct ABI return type or pass an explicit output destination.");
            }

            var unsupportedIndirectParameters = parameters
                .Where(static parameter => parameter.Kind != AbiParameterKind.Direct
                    && (parameter.Kind != AbiParameterKind.IndirectIn
                        || AbiLoweringHeuristics.IsByValueIndirectParameter(parameter)))
                .ToArray();
            foreach (var parameter in unsupportedIndirectParameters)
            {
                ReportTailAbiError(
                    function,
                    $"Tail-callable function '{function.DisplaySourceName}' cannot pass parameter '{parameter.SourceName}' of type '{parameter.SourceType.DisplayName}' by indirect ABI; LLVM 'musttail' requires ABI-compatible direct parameters.");
            }
        }

        return new AbiFunctionSignature(
            function.Name,
            symbolName,
            function.ReturnType,
            returnsIndirect
                ? StarkTypeSymbols.Void
                : hasFfiReturnClassification
                    ? ffiReturnClassification!.LlvmType
                    : LowerAbiValueType(function.ReturnType, isFfi, forReturnValue: true),
            parameters,
            isFfi,
            SourceName: function.SourceName,
            UsesFastCallingConvention: effects.UseFastCallingConvention,
            IsVarargs: effects.IsVarargs,
            FfiAbi: effects.FfiAbi,
            UsesTailCallingConvention: effects.IsTailCallable);
    }

    private void ReportTailAbiError(TypedFunctionSignature function, string message)
    {
        _diagnostics.Error(
            "STK4121",
            message,
            "lower-abi",
            function.DeclarationLocation ?? SourceLocation.Synthetic());
    }

    private HashSet<string> CollectFfiSymbolNames()
    {
        var symbols = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (functionName, effects) in _effectModel.Functions)
        {
            if (!effects.IsFfi)
            {
                continue;
            }

            var (_, sourceName, _) = ResolveFunctionIdentity(functionName);
            if (_typeModel.Functions.TryGetValue(functionName, out var function)
                && !string.IsNullOrWhiteSpace(function.ExternalLinkName))
            {
                symbols.Add(function.ExternalLinkName);
                continue;
            }

            symbols.Add(sourceName);
        }

        foreach (var function in _hir.Functions)
        {
            if (!function.Effects.IsFfi)
            {
                continue;
            }

            var (_, sourceName, _) = ResolveFunctionIdentity(function.Name);
            symbols.Add(function.Signature.ExternalLinkName ?? sourceName);
        }

        foreach (var module in _loadedModules.ImportedModules)
        {
            if (module.PackageImageFacts is not { } packageImageFacts)
            {
                continue;
            }

            foreach (var abiSignature in packageImageFacts.AbiFunctions.Values)
            {
                if (abiSignature.IsFfi)
                {
                    symbols.Add(abiSignature.SymbolName);
                }
            }
        }

        return symbols;
    }

    private static StarkTypeSymbol LowerAbiValueType(StarkTypeSymbol type, bool isFfi, bool forReturnValue)
    {
        if (!isFfi && forReturnValue)
        {
            return StarkTypeSymbols.BorrowReturnRuntimeType(type);
        }

        if (!isFfi)
        {
            return type;
        }

        return type.Kind switch
        {
            StarkTypeKind.Ascii => StarkTypeSymbols.RawPointer(StarkTypeSymbols.Integer(8), isMutable: false),
            StarkTypeKind.Unicode when !forReturnValue => StarkTypeSymbols.RawPointer(StarkTypeSymbols.Integer(32), isMutable: false),
            _ => type
        };
    }

    private (string ModuleName, string SourceName, StarkVisibility Visibility) ResolveFunctionIdentity(string functionName)
    {
        if (_functionIdentities.TryGetValue(functionName, out var identity))
        {
            return (identity.ModuleName, identity.SourceName, identity.Visibility);
        }

        var separator = functionName.LastIndexOf('.');
        if (separator < 0)
        {
            return (_syntaxModel.ModuleName, functionName, StarkVisibility.Module);
        }

        return (functionName[..separator], functionName[(separator + 1)..], StarkVisibility.Module);
    }

    private static Dictionary<string, FunctionIdentity> BuildFunctionIdentityIndex(LoadedModuleSet loadedModules)
    {
        var identities = new Dictionary<string, FunctionIdentity>(StringComparer.Ordinal);

        foreach (var module in loadedModules.Modules.Values)
        {
            foreach (var declaration in module.SyntaxModel.Declarations)
            {
                if (declaration.Kind != DeclarationKind.Function)
                {
                    continue;
                }

                var resolvedName = FunctionOverloadFacts.QualifyResolvedName(
                    module,
                    FunctionOverloadFacts.GetResolvedLocalName(module.SyntaxModel, declaration));

                identities.TryAdd(
                    resolvedName,
                    new FunctionIdentity(module.SyntaxModel.ModuleName, declaration.Name, declaration.Visibility));
            }
        }

        return identities;
    }

    private static IReadOnlyDictionary<string, ConcreteTypeLayout> BuildPublishedConcreteLayouts(LoadedModuleSet loadedModules)
    {
        var layouts = new Dictionary<string, ConcreteTypeLayout>(StringComparer.Ordinal);

        foreach (var module in loadedModules.ImportedModules)
        {
            if (module.PackageImageFacts is not { } packageImageFacts)
            {
                continue;
            }

            foreach (var (qualifiedName, layout) in packageImageFacts.ConcreteLayouts)
            {
                layouts[qualifiedName] = layout;
            }
        }

        return layouts;
    }

    private readonly record struct FunctionIdentity(
        string ModuleName,
        string SourceName,
        StarkVisibility Visibility);

    private readonly record struct FfiLinkageKey(StarkFfiAbi Abi, string SymbolName)
    {
        public static IEqualityComparer<FfiLinkageKey> Comparer { get; } = new FfiLinkageKeyComparer();

        private sealed class FfiLinkageKeyComparer : IEqualityComparer<FfiLinkageKey>
        {
            public bool Equals(FfiLinkageKey x, FfiLinkageKey y) =>
                x.Abi == y.Abi
                && string.Equals(x.SymbolName, y.SymbolName, StringComparison.Ordinal);

            public int GetHashCode(FfiLinkageKey obj) =>
                HashCode.Combine(obj.Abi, StringComparer.Ordinal.GetHashCode(obj.SymbolName));
        }
    }

    private readonly record struct FfiLinkageSignature(
        AbiFunctionSignature Signature,
        SourceLocation? Location);

    private string ComputeSymbolName(
        string qualifiedName,
        string moduleName,
        string sourceName,
        StarkVisibility visibility,
        bool isFfi,
        bool isOverloaded,
        string? externalLinkName,
        IReadOnlySet<string> ffiSymbolNames)
    {
        // FFI declarations must keep the external import name even when Stark
        // also declares local overloads with the same source name.
        if (isFfi)
        {
            return externalLinkName ?? sourceName;
        }

        if (qualifiedName.StartsWith("__stark_", StringComparison.Ordinal))
        {
            return qualifiedName;
        }

        if (isOverloaded)
        {
            var modulePrefix = string.IsNullOrEmpty(moduleName)
                ? string.Empty
                : $"{moduleName}.";
            return _options.QualifyModuleSymbols
                   && !string.IsNullOrEmpty(modulePrefix)
                   && !qualifiedName.StartsWith(modulePrefix, StringComparison.Ordinal)
                ? $"{modulePrefix}{qualifiedName}"
                : qualifiedName;
        }

        if (visibility == StarkVisibility.Export
            && !sourceName.Contains('.', StringComparison.Ordinal))
        {
            return sourceName;
        }

        if (!_options.QualifyModuleSymbols
            && !qualifiedName.Contains('.', StringComparison.Ordinal)
            && ffiSymbolNames.Contains(sourceName))
        {
            return $"{moduleName}.{sourceName}";
        }

        if (_options.QualifyModuleSymbols)
        {
            return $"{moduleName}.{sourceName}";
        }

        if (qualifiedName.Contains('.', StringComparison.Ordinal))
        {
            // A dotted name is not necessarily module-qualified: an imported
            // method can lower under its module-relative `Type.Method` name
            // (the dot is the type separator). The binary symbol must match
            // the defining library's module-qualified emission, or consumer
            // declares/calls reference a symbol the archive never exported
            // (`@Counter_Reset` vs `@Facade_Counter_Reset`).
            return !string.IsNullOrEmpty(moduleName)
                   && !string.Equals(moduleName, _syntaxModel.ModuleName, StringComparison.Ordinal)
                   && !qualifiedName.StartsWith($"{moduleName}.", StringComparison.Ordinal)
                ? $"{moduleName}.{qualifiedName}"
                : qualifiedName;
        }

        return sourceName;
    }

    private void ValidateFfiLinkageSignature(
        Dictionary<FfiLinkageKey, FfiLinkageSignature> seen,
        AbiFunctionSignature current,
        SourceLocation? location)
    {
        if (!current.IsFfi)
        {
            return;
        }

        var key = new FfiLinkageKey(current.FfiAbi ?? StarkFfiAbi.C, current.SymbolName);
        if (!seen.TryGetValue(key, out var previous))
        {
            seen.Add(key, new FfiLinkageSignature(current, location));
            return;
        }

        if (AreFfiLinkageSignaturesCompatible(previous.Signature, current))
        {
            return;
        }

        _diagnostics.Error(
            "STK4122",
            $"FFI declarations '{previous.Signature.DisplaySourceName}' and '{current.DisplaySourceName}' both link to {StarkFfiAbiFacts.DisplayName(key.Abi)} symbol '{key.SymbolName}' but lower to incompatible LLVM ABI signatures. Use distinct [LinkName(\"...\")] values or make the declarations' return type, parameters, varargs marker, and FFI ABI exactly match.",
            "lower-abi",
            location ?? previous.Location ?? SourceLocation.Synthetic());
    }

    private static bool AreFfiLinkageSignaturesCompatible(
        AbiFunctionSignature left,
        AbiFunctionSignature right)
    {
        if ((left.FfiAbi ?? StarkFfiAbi.C) != (right.FfiAbi ?? StarkFfiAbi.C)
            || left.IsVarargs != right.IsVarargs
            || left.ReturnsIndirect != right.ReturnsIndirect
            || !AbiTypesEqual(left.LlvmReturnType, right.LlvmReturnType)
            || left.Parameters.Count != right.Parameters.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Parameters.Count; index++)
        {
            var leftParameter = left.Parameters[index];
            var rightParameter = right.Parameters[index];
            if (leftParameter.Kind != rightParameter.Kind
                || !AbiTypesEqual(leftParameter.LlvmType, rightParameter.LlvmType)
                || !AbiTypeListsEqual(leftParameter.EffectiveLlvmParameterTypes, rightParameter.EffectiveLlvmParameterTypes))
            {
                return false;
            }
        }

        return true;
    }

    private static bool AbiTypeListsEqual(IReadOnlyList<StarkTypeSymbol> left, IReadOnlyList<StarkTypeSymbol> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Count; index++)
        {
            if (!AbiTypesEqual(left[index], right[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool AbiTypesEqual(StarkTypeSymbol left, StarkTypeSymbol right)
    {
        var normalizedLeft = NormalizeType(left);
        var normalizedRight = NormalizeType(right);
        if (normalizedLeft.Kind != normalizedRight.Kind)
        {
            return false;
        }

        return normalizedLeft.Kind switch
        {
            StarkTypeKind.Integer => normalizedLeft.BitWidth == normalizedRight.BitWidth
                && normalizedLeft.RangeMin == normalizedRight.RangeMin
                && normalizedLeft.RangeMax == normalizedRight.RangeMax
                && normalizedLeft.IsUnsigned == normalizedRight.IsUnsigned,
            StarkTypeKind.Float => normalizedLeft.BitWidth == normalizedRight.BitWidth,
            StarkTypeKind.RawPointer => normalizedLeft.IsMutablePointer == normalizedRight.IsMutablePointer
                && AbiNullableTypesEqual(normalizedLeft.ElementType, normalizedRight.ElementType),
            StarkTypeKind.LlvmVector => normalizedLeft.FixedLength == normalizedRight.FixedLength
                && normalizedLeft.ElementType is not null
                && normalizedRight.ElementType is not null
                && AbiTypesEqual(normalizedLeft.ElementType, normalizedRight.ElementType),
            StarkTypeKind.LlvmStruct => AbiTypeListsEqual(normalizedLeft.TypeArguments ?? [], normalizedRight.TypeArguments ?? []),
            StarkTypeKind.FixedArray => normalizedLeft.FixedLength == normalizedRight.FixedLength
                && string.Equals(normalizedLeft.FixedLengthParameterName, normalizedRight.FixedLengthParameterName, StringComparison.Ordinal)
                && AbiNullableTypesEqual(normalizedLeft.ElementType, normalizedRight.ElementType),
            StarkTypeKind.Slice or StarkTypeKind.Dynamic => AbiNullableTypesEqual(normalizedLeft.ElementType, normalizedRight.ElementType),
            StarkTypeKind.FunctionPointer => normalizedLeft.FunctionPointerKind == normalizedRight.FunctionPointerKind
                && normalizedLeft.FunctionPointerIsTailCallable == normalizedRight.FunctionPointerIsTailCallable
                && normalizedLeft.FunctionPointerAbi == normalizedRight.FunctionPointerAbi
                && normalizedLeft.FunctionPointerIsUnsafe == normalizedRight.FunctionPointerIsUnsafe
                && AbiNullableTypesEqual(normalizedLeft.FunctionPointerReturnType, normalizedRight.FunctionPointerReturnType)
                && AbiTypeListsEqual(normalizedLeft.FunctionPointerParameterTypes ?? [], normalizedRight.FunctionPointerParameterTypes ?? [])
                && StringListsEqual(
                    normalizedLeft.FunctionPointerParameterRawPointerElementCountExpressions,
                    normalizedRight.FunctionPointerParameterRawPointerElementCountExpressions)
                && DisjointGroupListsEqual(normalizedLeft.FunctionPointerDisjointParameterGroups, normalizedRight.FunctionPointerDisjointParameterGroups)
                && OverlapGroupListsEqual(normalizedLeft.FunctionPointerOverlapParameterGroups, normalizedRight.FunctionPointerOverlapParameterGroups)
                && SameGroupListsEqual(normalizedLeft.FunctionPointerSameParameterGroups, normalizedRight.FunctionPointerSameParameterGroups)
                && StringListsEqual(
                    normalizedLeft.FunctionPointerPointeeDeadOnReturnParameterNames,
                    normalizedRight.FunctionPointerPointeeDeadOnReturnParameterNames),
            StarkTypeKind.Closure => normalizedLeft.ClosureStorageKind == normalizedRight.ClosureStorageKind
                && normalizedLeft.ClosureCallCapability == normalizedRight.ClosureCallCapability
                && normalizedLeft.ClosureFunctionKind == normalizedRight.ClosureFunctionKind
                && normalizedLeft.ClosureIsTailCallable == normalizedRight.ClosureIsTailCallable
                && AbiNullableTypesEqual(normalizedLeft.ClosureReturnType, normalizedRight.ClosureReturnType)
                && AbiTypeListsEqual(normalizedLeft.ClosureParameterTypes ?? [], normalizedRight.ClosureParameterTypes ?? [])
                && StringListsEqual(
                    normalizedLeft.ClosureParameterRawPointerElementCountExpressions,
                    normalizedRight.ClosureParameterRawPointerElementCountExpressions)
                && DisjointGroupListsEqual(normalizedLeft.ClosureDisjointParameterGroups, normalizedRight.ClosureDisjointParameterGroups)
                && OverlapGroupListsEqual(normalizedLeft.ClosureOverlapParameterGroups, normalizedRight.ClosureOverlapParameterGroups)
                && SameGroupListsEqual(normalizedLeft.ClosureSameParameterGroups, normalizedRight.ClosureSameParameterGroups)
                && StringListsEqual(
                    normalizedLeft.ClosurePointeeDeadOnReturnParameterNames,
                    normalizedRight.ClosurePointeeDeadOnReturnParameterNames),
            StarkTypeKind.Named => string.Equals(normalizedLeft.NamedType, normalizedRight.NamedType, StringComparison.Ordinal)
                && AbiTypeListsEqual(normalizedLeft.TypeArguments ?? [], normalizedRight.TypeArguments ?? [])
                && ComptimeValueArgumentListsEqual(normalizedLeft.ComptimeValueArguments, normalizedRight.ComptimeValueArguments),
            StarkTypeKind.DynTrait => string.Equals(normalizedLeft.DynTraitName, normalizedRight.DynTraitName, StringComparison.Ordinal)
                && normalizedLeft.DynTraitStorageKind == normalizedRight.DynTraitStorageKind
                && AbiTypeListsEqual(normalizedLeft.TypeArguments ?? [], normalizedRight.TypeArguments ?? [])
                && ComptimeValueArgumentListsEqual(normalizedLeft.ComptimeValueArguments, normalizedRight.ComptimeValueArguments),
            StarkTypeKind.AssociatedType => string.Equals(normalizedLeft.AssociatedTypeName, normalizedRight.AssociatedTypeName, StringComparison.Ordinal)
                && AbiNullableTypesEqual(normalizedLeft.AssociatedTypeOwner, normalizedRight.AssociatedTypeOwner),
            _ => normalizedLeft == normalizedRight
        };
    }

    private static bool AbiNullableTypesEqual(StarkTypeSymbol? left, StarkTypeSymbol? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        return AbiTypesEqual(left, right);
    }

    private static bool ComptimeValueArgumentListsEqual(
        IReadOnlyList<ComptimeValueArgumentSymbol>? left,
        IReadOnlyList<ComptimeValueArgumentSymbol>? right)
    {
        var leftCount = left?.Count ?? 0;
        var rightCount = right?.Count ?? 0;
        if (leftCount != rightCount)
        {
            return false;
        }

        for (var index = 0; index < leftCount; index++)
        {
            var leftValue = left![index];
            var rightValue = right![index];
            if (!string.Equals(leftValue.ParameterName, rightValue.ParameterName, StringComparison.Ordinal)
                || leftValue.IntegerValue != rightValue.IntegerValue
                || leftValue.IsSymbolic != rightValue.IsSymbolic
                || !string.Equals(leftValue.SymbolicSourceName, rightValue.SymbolicSourceName, StringComparison.Ordinal)
                || !AbiTypesEqual(leftValue.Type, rightValue.Type))
            {
                return false;
            }
        }

        return true;
    }

    private static bool DisjointGroupListsEqual(
        IReadOnlyList<ParameterDisjointGroup>? left,
        IReadOnlyList<ParameterDisjointGroup>? right)
    {
        var leftCount = left?.Count ?? 0;
        var rightCount = right?.Count ?? 0;
        if (leftCount != rightCount)
        {
            return false;
        }

        for (var index = 0; index < leftCount; index++)
        {
            var leftGroup = left![index];
            var rightGroup = right![index];
            if (!StringListsEqual(leftGroup.ParameterNames, rightGroup.ParameterNames)
                || !RegionListsEqual(leftGroup.MemoryRegions, rightGroup.MemoryRegions))
            {
                return false;
            }
        }

        return true;
    }

    private static bool OverlapGroupListsEqual(
        IReadOnlyList<ParameterOverlapGroup>? left,
        IReadOnlyList<ParameterOverlapGroup>? right)
    {
        var leftCount = left?.Count ?? 0;
        var rightCount = right?.Count ?? 0;
        if (leftCount != rightCount)
        {
            return false;
        }

        for (var index = 0; index < leftCount; index++)
        {
            if (!StringListsEqual(left![index].ParameterNames, right![index].ParameterNames))
            {
                return false;
            }
        }

        return true;
    }

    private static bool SameGroupListsEqual(
        IReadOnlyList<ParameterSameGroup>? left,
        IReadOnlyList<ParameterSameGroup>? right)
    {
        var leftCount = left?.Count ?? 0;
        var rightCount = right?.Count ?? 0;
        if (leftCount != rightCount)
        {
            return false;
        }

        for (var index = 0; index < leftCount; index++)
        {
            if (!StringListsEqual(left![index].ParameterNames, right![index].ParameterNames))
            {
                return false;
            }
        }

        return true;
    }

    private static bool RegionListsEqual(
        IReadOnlyList<ParameterMemoryRegion>? left,
        IReadOnlyList<ParameterMemoryRegion>? right)
    {
        var leftCount = left?.Count ?? 0;
        var rightCount = right?.Count ?? 0;
        if (leftCount != rightCount)
        {
            return false;
        }

        for (var index = 0; index < leftCount; index++)
        {
            var leftRegion = left![index];
            var rightRegion = right![index];
            if (!string.Equals(leftRegion.ParameterName, rightRegion.ParameterName, StringComparison.Ordinal)
                || !string.Equals(leftRegion.StartExpression, rightRegion.StartExpression, StringComparison.Ordinal)
                || !string.Equals(leftRegion.CountExpression, rightRegion.CountExpression, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static bool StringListsEqual(IReadOnlyList<string?>? left, IReadOnlyList<string?>? right)
    {
        var leftCount = left?.Count ?? 0;
        var rightCount = right?.Count ?? 0;
        if (leftCount != rightCount)
        {
            return false;
        }

        for (var index = 0; index < leftCount; index++)
        {
            if (!string.Equals(left![index], right![index], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static StarkTypeSymbol NormalizeType(StarkTypeSymbol type)
    {
        return StarkTypeSymbols.WithQualifiers(
            type,
            borrowKind: StarkBorrowKind.None,
            accessKind: StarkAccessKind.None,
            initializationKind: StarkInitializationKind.None,
            isMutableView: false);
    }
}
