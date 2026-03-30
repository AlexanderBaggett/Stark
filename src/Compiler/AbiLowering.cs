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
        var (moduleName, sourceName, visibility) = ResolveFunctionIdentity(function.Name);
        var parameters = new List<AbiParameterSymbol>();
        var isFfi = effects.IsFfi;
        var returnsIndirect = !isFfi && RequiresIndirectReturnAbi(function.ReturnType);
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
            var kind = !isFfi && RequiresIndirectParameterAbi(parameter.Type)
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

    private static bool RequiresIndirectParameterAbi(StarkTypeSymbol type)
    {
        return type.BorrowKind != StarkBorrowKind.None
            || type.InitializationKind != StarkInitializationKind.None;
    }

    private static bool RequiresIndirectReturnAbi(StarkTypeSymbol type)
    {
        return false;
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

                if (!string.Equals(QualifyName(module, declaration.Name), functionName, StringComparison.Ordinal))
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
        bool isFfi)
    {
        if (isFfi)
        {
            return sourceName;
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
