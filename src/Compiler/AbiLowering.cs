namespace Stark.Compiler;

internal sealed class AbiLowerer
{
    private readonly SyntaxModel _syntaxModel;
    private readonly LoadedModuleSet _loadedModules;
    private readonly TypeCheckModel _typeModel;
    private readonly EnumLayoutModel _enumLayoutModel;
    private readonly FunctionEffectModel _effectModel;
    private readonly CompilerOptions _options;

    public AbiLowerer(
        SyntaxModel syntaxModel,
        LoadedModuleSet loadedModules,
        TypeCheckModel typeModel,
        EnumLayoutModel enumLayoutModel,
        FunctionEffectModel effectModel,
        CompilerOptions options)
    {
        _syntaxModel = syntaxModel;
        _loadedModules = loadedModules;
        _typeModel = typeModel;
        _enumLayoutModel = enumLayoutModel;
        _effectModel = effectModel;
        _options = options;
    }

    public AbiModel Lower()
    {
        var functions = new Dictionary<string, AbiFunctionSignature>(StringComparer.Ordinal);

        foreach (var function in _typeModel.Functions.Values.OrderBy(static function => function.Name, StringComparer.Ordinal))
        {
            if (!_effectModel.Functions.TryGetValue(function.Name, out var effects))
            {
                continue;
            }

            functions[function.Name] = LowerFunction(function, effects);
        }

        return new AbiModel(_typeModel.ModuleName, functions);
    }

    private AbiFunctionSignature LowerFunction(TypedFunctionSignature function, FunctionEffectProfile effects)
    {
        var (moduleName, sourceName, visibility) = ResolveFunctionIdentity(function.Name);
        var parameters = new List<AbiParameterSymbol>();
        var isFfi = effects.IsFfi;
        var returnsIndirect = !isFfi
            && AbiLoweringHeuristics.RequiresIndirectReturnAbi(function.ReturnType, _typeModel.NamedTypes, _enumLayoutModel.Layouts);
        var isOverloaded = !string.Equals(function.Name, function.DisplaySourceName, StringComparison.Ordinal);
        var symbolName = ComputeSymbolName(function.Name, moduleName, sourceName, visibility, isFfi, isOverloaded);

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
            UsesFastCallingConvention: effects.UseFastCallingConvention);
    }

    private static StarkTypeSymbol LowerAbiValueType(StarkTypeSymbol type, bool isFfi, bool forReturnValue)
    {
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
        bool isOverloaded)
    {
        // FFI declarations must keep the external import name even when Stark
        // also declares local overloads with the same source name.
        if (isFfi)
        {
            return sourceName;
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
