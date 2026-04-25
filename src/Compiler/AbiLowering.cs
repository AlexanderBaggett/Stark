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

    public AbiLowerer(
        SyntaxModel syntaxModel,
        LoadedModuleSet loadedModules,
        TypeCheckModel typeModel,
        EnumLayoutModel enumLayoutModel,
        FunctionEffectModel effectModel,
        HighLevelIrModule hir,
        CompilerOptions options)
    {
        _syntaxModel = syntaxModel;
        _loadedModules = loadedModules;
        _typeModel = typeModel;
        _enumLayoutModel = enumLayoutModel;
        _effectModel = effectModel;
        _hir = hir;
        _options = options;
    }

    public AbiModel Lower()
    {
        var functions = new Dictionary<string, AbiFunctionSignature>(StringComparer.Ordinal);
        var ffiSymbolNames = CollectFfiSymbolNames();

        foreach (var function in _typeModel.Functions.Values.OrderBy(static function => function.Name, StringComparer.Ordinal))
        {
            if (!_effectModel.Functions.TryGetValue(function.Name, out var effects))
            {
                continue;
            }

            functions[function.Name] = LowerFunction(function, effects, ffiSymbolNames);
        }

        foreach (var function in _hir.Functions.OrderBy(static function => function.Name, StringComparer.Ordinal))
        {
            if (functions.ContainsKey(function.Name))
            {
                continue;
            }

            functions[function.Name] = LowerFunction(function.Signature, function.Effects, ffiSymbolNames);
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
        var returnsIndirect = !isFfi
            && AbiLoweringHeuristics.RequiresIndirectReturnAbi(function.ReturnType, _typeModel.NamedTypes, _enumLayoutModel.Layouts);
        var isOverloaded = !string.Equals(function.Name, function.DisplaySourceName, StringComparison.Ordinal);
        var symbolName = ComputeSymbolName(
            function.Name,
            moduleName,
            sourceName,
            visibility,
            isFfi,
            isOverloaded,
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
            var kind = !isFfi && AbiLoweringHeuristics.RequiresIndirectParameterAbi(parameter.Type, _typeModel.NamedTypes, _enumLayoutModel.Layouts)
                ? AbiParameterKind.IndirectIn
                : AbiParameterKind.Direct;

            var llvmType = LowerAbiValueType(parameter.Type, isFfi, forReturnValue: false);

            parameters.Add(new AbiParameterSymbol(
                SourceName: parameter.Name,
                LlvmName: $"arg_{parameter.Name}",
                SourceType: parameter.Type,
                LlvmType: kind == AbiParameterKind.Direct
                    ? llvmType
                    : StarkTypeSymbols.RawPointer(parameter.Type, isMutable: false),
                Kind: kind));
        }

        return new AbiFunctionSignature(
            function.Name,
            symbolName,
            function.ReturnType,
            returnsIndirect ? StarkTypeSymbols.Void : LowerAbiValueType(function.ReturnType, isFfi, forReturnValue: true),
            parameters,
            isFfi,
            SourceName: function.SourceName,
            UsesFastCallingConvention: effects.UseFastCallingConvention,
            IsVarargs: effects.IsVarargs);
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
            symbols.Add(sourceName);
        }

        foreach (var function in _hir.Functions)
        {
            if (!function.Effects.IsFfi)
            {
                continue;
            }

            var (_, sourceName, _) = ResolveFunctionIdentity(function.Name);
            symbols.Add(sourceName);
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
        foreach (var module in _loadedModules.Modules.Values)
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
                if (!string.Equals(resolvedName, functionName, StringComparison.Ordinal))
                {
                    continue;
                }

                return (module.SyntaxModel.ModuleName, declaration.Name, declaration.Visibility);
            }
        }

        var separator = functionName.LastIndexOf('.');
        if (separator < 0)
        {
            return (_syntaxModel.ModuleName, functionName, StarkVisibility.Module);
        }

        return (functionName[..separator], functionName[(separator + 1)..], StarkVisibility.Module);
    }

    private string ComputeSymbolName(
        string qualifiedName,
        string moduleName,
        string sourceName,
        StarkVisibility visibility,
        bool isFfi,
        bool isOverloaded,
        IReadOnlySet<string> ffiSymbolNames)
    {
        // FFI declarations must keep the external import name even when Stark
        // also declares local overloads with the same source name.
        if (isFfi)
        {
            return sourceName;
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
            return qualifiedName;
        }

        return sourceName;
    }

    private string QualifyName(LoadedModuleDocument module, string localName)
    {
        return module.Reference.IsRoot
            ? localName
            : $"{module.SyntaxModel.ModuleName}.{localName}";
    }
}
