namespace Stark.Compiler;

internal sealed class AbiLowerer
{
    private readonly SyntaxModel _syntaxModel;
    private readonly LoadedModuleSet _loadedModules;
    private readonly TypeCheckModel _typeModel;
    private readonly FunctionEffectModel _effectModel;
    private readonly CompilerOptions _options;

    public AbiLowerer(
        SyntaxModel syntaxModel,
        LoadedModuleSet loadedModules,
        TypeCheckModel typeModel,
        FunctionEffectModel effectModel,
        CompilerOptions options)
    {
        _syntaxModel = syntaxModel;
        _loadedModules = loadedModules;
        _typeModel = typeModel;
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
        var (moduleName, sourceName) = SplitFunctionName(function.Name);
        var visibility = LookupVisibility(moduleName, sourceName);
        var parameters = new List<AbiParameterSymbol>();
        var isFfi = effects.IsFfi;
        var returnsIndirect = !isFfi && RequiresIndirectAggregateAbi(function.ReturnType);
        var symbolName = ComputeSymbolName(function.Name, moduleName, sourceName, visibility, isFfi);

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
            var kind = !isFfi && RequiresIndirectAggregateAbi(parameter.Type)
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
            isFfi);
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
            StarkTypeKind.Unicode when !forReturnValue => StarkTypeSymbols.RawPointer(StarkTypeSymbols.Integer(8), isMutable: false),
            _ => type
        };
    }

    private bool RequiresIndirectAggregateAbi(StarkTypeSymbol type)
    {
        return type.Kind switch
        {
            StarkTypeKind.FixedArray => true,
            StarkTypeKind.Named when type.NamedType is not null
                                      && _typeModel.NamedTypes.TryGetValue(type.NamedType, out var namedType)
                                      && namedType.Kind is DeclarationKind.Struct or DeclarationKind.Record => true,
            _ => false
        };
    }

    private (string ModuleName, string SourceName) SplitFunctionName(string functionName)
    {
        var separator = functionName.LastIndexOf('.');
        return separator < 0
            ? (_syntaxModel.ModuleName, functionName)
            : (functionName[..separator], functionName[(separator + 1)..]);
    }

    private StarkVisibility LookupVisibility(string moduleName, string sourceName)
    {
        if (_loadedModules.TryGet(moduleName, out var module) && module is not null)
        {
            var declaration = module.SyntaxModel.Declarations.FirstOrDefault(
                candidate => candidate.Kind == DeclarationKind.Function
                             && string.Equals(candidate.Name, sourceName, StringComparison.Ordinal));
            if (declaration is not null)
            {
                return declaration.Visibility;
            }
        }

        return StarkVisibility.Module;
    }

    private string ComputeSymbolName(
        string qualifiedName,
        string moduleName,
        string sourceName,
        StarkVisibility visibility,
        bool isFfi)
    {
        if (isFfi || visibility == StarkVisibility.Export)
        {
            return sourceName;
        }

        if (qualifiedName.Contains('.', StringComparison.Ordinal))
        {
            return qualifiedName;
        }

        return _options.QualifyModuleSymbols
            ? $"{moduleName}.{sourceName}"
            : sourceName;
    }
}
