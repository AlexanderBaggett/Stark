using System.Numerics;
using System.Text;
using Stark.Parsing;

namespace Stark.Compiler;

internal sealed class LlvmIrEmitter
{
    private const string AsciiStringTypeName = "stark_ascii";
    private const string UnicodeStringTypeName = "stark_unicode";
    private const int AggregateMemcpyThresholdBytes = 32;
    private readonly CompilationInput _input;
    private readonly ParseResult _parseResult;
    private readonly SyntaxModel _syntaxModel;
    private readonly LoadedModuleSet _loadedModules;
    private readonly FunctionEffectModel _effectModel;
    private readonly TypeCheckModel _typeModel;
    private readonly EnumLayoutModel _enumLayoutModel;
    private readonly SemanticValidationModel? _semanticValidation;
    private readonly ClosedWorldOptimizationModel? _closedWorldModel;
    private readonly CompilerLogBag? _logs;
    private readonly AbiModel _abiModel;
    private readonly SsaIrModule _ssa;
    private readonly LlvmTargetInfo? _targetInfo;
    private readonly bool _internalizeModulePrivate;
    private readonly IReadOnlyDictionary<string, string> _globalSymbols;
    private readonly IReadOnlyDictionary<StringConstantKey, EmittedStringConstant> _stringConstants;
    private readonly IReadOnlyDictionary<ObjectCreationKey, TypedConstructorShape?> _objectCreationConstructors;
    private readonly IReadOnlyDictionary<string, TypedFunctionSignature> _allFunctionSignatures;
    private readonly IReadOnlyDictionary<string, AbiFunctionSignature> _allAbiFunctions;
    private readonly IReadOnlyDictionary<string, SourceLocation> _functionLocations;
    private readonly IReadOnlyDictionary<string, ImportedLawClonePlan> _closedWorldImportedLawClones;
    private int _syntheticGlobalInitializerIndex;

    public LlvmIrEmitter(
        CompilationInput input,
        ParseResult parseResult,
        SyntaxModel syntaxModel,
        FunctionEffectModel effectModel,
        TypeCheckModel typeModel,
        EnumLayoutModel enumLayoutModel,
        AbiModel abiModel,
        SsaIrModule ssa,
        LlvmTargetInfo? targetInfo = null,
        bool internalizeModulePrivate = false,
        SemanticValidationModel? semanticValidation = null,
        ClosedWorldOptimizationModel? closedWorldModel = null,
        CompilerLogBag? logs = null)
        : this(
            input,
            parseResult,
            syntaxModel,
            CreateRootLoadedModules(parseResult, syntaxModel, input.FilePath),
            effectModel,
            typeModel,
            enumLayoutModel,
            abiModel,
            ssa,
            targetInfo,
            internalizeModulePrivate,
            semanticValidation,
            closedWorldModel,
            logs)
    {
    }

    public LlvmIrEmitter(
        CompilationInput input,
        ParseResult parseResult,
        SyntaxModel syntaxModel,
        LoadedModuleSet loadedModules,
        FunctionEffectModel effectModel,
        TypeCheckModel typeModel,
        EnumLayoutModel enumLayoutModel,
        AbiModel abiModel,
        SsaIrModule ssa,
        LlvmTargetInfo? targetInfo = null,
        bool internalizeModulePrivate = false,
        SemanticValidationModel? semanticValidation = null,
        ClosedWorldOptimizationModel? closedWorldModel = null,
        CompilerLogBag? logs = null)
    {
        _input = input;
        _parseResult = parseResult;
        _syntaxModel = syntaxModel;
        _loadedModules = loadedModules;
        _effectModel = effectModel;
        _typeModel = typeModel;
        _enumLayoutModel = enumLayoutModel;
        _semanticValidation = semanticValidation;
        _closedWorldModel = closedWorldModel;
        _logs = logs;
        _abiModel = abiModel;
        _ssa = ssa;
        _targetInfo = targetInfo;
        _internalizeModulePrivate = internalizeModulePrivate;
        _globalSymbols = BuildGlobalSymbolMap();
        _stringConstants = CollectStringConstants(parseResult, ssa);
        _objectCreationConstructors = typeModel.ObjectCreations
            .GroupBy(static record => new ObjectCreationKey(record.ExpressionText, record.Location.Line, record.Location.Column))
            .ToDictionary(static group => group.Key, static group => group.Last().Constructor);
        _allFunctionSignatures = BuildAllFunctionSignatures(typeModel, ssa);
        _allAbiFunctions = BuildAllAbiFunctions(_allFunctionSignatures, abiModel, effectModel);
        _functionLocations = BuildFunctionLocationMap(loadedModules, input.FilePath);
        _closedWorldImportedLawClones = BuildClosedWorldImportedLawClones();
    }

    public LlvmIrModule Emit()
    {
        var builder = new StringBuilder();
        builder.AppendLine($"; ModuleID = '{_syntaxModel.ModuleName}'");
        builder.AppendLine($"source_filename = \"{EscapeFileName(_input.FilePath ?? $"{_syntaxModel.ModuleName}.stark")}\"");

        if (!string.IsNullOrWhiteSpace(_targetInfo?.DataLayout))
        {
            builder.AppendLine($"target datalayout = \"{EscapeFileName(_targetInfo!.DataLayout!)}\"");
        }

        builder.AppendLine($"target triple = \"{EscapeFileName(_targetInfo?.Triple ?? "unknown-unknown-unknown")}\"");
        builder.AppendLine();
        builder.AppendLine("; LLVM IR for the currently supported Stark SSA subset.");
        builder.AppendLine("; Unsupported constructs still fall back to declarations.");
        builder.AppendLine();

        EmitBuiltinTypeDefinitions(builder);
        EmitNamedTypeDefinitions(builder);
        EmitStringConstants(builder);
        EmitGlobals(builder);
        EmitIntrinsicDeclarations(builder);

        var rootFunctionNames = new HashSet<string>(
            _syntaxModel.Declarations
                .Where(static declaration => declaration.Function is not null)
                .Select(declaration => FunctionOverloadFacts.GetResolvedLocalName(_syntaxModel, declaration)),
            StringComparer.Ordinal);
        var resolveCallAbi = CreateCallAbiResolver();

        foreach (var declaration in _syntaxModel.Declarations.Where(static declaration => declaration.Function is not null))
        {
            var function = declaration.Function!;
            var resolvedName = FunctionOverloadFacts.GetResolvedLocalName(_syntaxModel, declaration);
            var effects = _effectModel.Functions[resolvedName];
            var signature = _typeModel.Functions[resolvedName];
            var abiSignature = _abiModel.Functions[resolvedName];
            var ssaFunction = _ssa.Functions.FirstOrDefault(item => item.Name == resolvedName);
            var parameterEffects = GetRootParameterEffects(resolvedName, function.HasBody)
                ?? GetBuiltinParameterEffects(_syntaxModel.ModuleName, resolvedName, signature);

            builder.AppendLine($"; visibility: {declaration.Visibility.ToString().ToLowerInvariant()}");

            var definitionInternalize = ShouldInternalize(declaration.Visibility);
            if (function.Asm is not null)
            {
                if (TryEmitAsmFunctionDefinition(
                        builder,
                        definitionInternalize,
                        signature,
                        abiSignature,
                        effects,
                        function.Asm,
                        parameterEffects,
                        out var asmFailureReason))
                {
                    builder.AppendLine();
                    continue;
                }

                builder.AppendLine($"; LLVM asm body emission fallback for {resolvedName}: {asmFailureReason}");
                LogLlvmFallback(
                    "llvm-asm-fallback",
                    resolvedName,
                    asmFailureReason,
                    FunctionBodyLoweringKind.AsmBypass,
                    supportsDirectCodeGeneration: false,
                    operation: "EmitAsmFunctionDefinition");
                builder.AppendLine(BuildDeclarationSignature(false, signature, abiSignature, effects, parameterEffects));
                builder.AppendLine();
                continue;
            }

            if (!function.HasBody
                && TryEmitBuiltinFunctionDefinition(
                    builder,
                    definitionInternalize,
                    _syntaxModel.ModuleName,
                    signature,
                    abiSignature,
                    effects,
                    parameterEffects))
            {
                builder.AppendLine();
                continue;
            }

            if (function.HasBody
                && ssaFunction is not null
                && ssaFunction.SupportsDirectCodeGeneration)
            {
                try
                {
                    EmitFunctionDefinition(builder, definitionInternalize, signature, abiSignature, effects, ssaFunction, parameterEffects, resolveCallAbi);
                    builder.AppendLine();
                    continue;
                }
                catch (UnsupportedBodyEmissionException exception)
                {
                    builder.AppendLine($"; LLVM body emission fallback for {resolvedName}: {exception.Message}");
                    LogLlvmFallback(
                        "llvm-body-fallback",
                        resolvedName,
                        exception.Message,
                        ssaFunction.BodyLoweringKind,
                        ssaFunction.SupportsDirectCodeGeneration,
                        operation: "EmitFunctionDefinition");
                }
            }
            else if (function.HasBody)
            {
                builder.AppendLine($"; LLVM body emission pending for {resolvedName}");
                LogLlvmFallback(
                    "llvm-body-pending",
                    resolvedName,
                    "SSA lowering did not leave this function in a direct-codegen-capable form, so LLVM emitted only a declaration.",
                    ssaFunction?.BodyLoweringKind ?? FunctionBodyLoweringKind.DeclarationOnly,
                    ssaFunction?.SupportsDirectCodeGeneration ?? false,
                    operation: "EmitFunctionDefinition");
            }

            builder.AppendLine(BuildDeclarationSignature(false, signature, abiSignature, effects, parameterEffects));
            builder.AppendLine();
        }

        foreach (var clone in _closedWorldImportedLawClones.Values.OrderBy(static clone => clone.FunctionName, StringComparer.Ordinal))
        {
            builder.AppendLine($"; closed-world imported law clone: {clone.FunctionName}");
            EmitFunctionDefinition(
                builder,
                internalize: true,
                clone.Signature,
                clone.AbiSignature,
                clone.Effects,
                clone.SsaFunction,
                parameterEffects: null,
                resolveCallAbi);
            builder.AppendLine();
        }

        foreach (var abiFunction in _abiModel.Functions.Values
                     .Where(function => !rootFunctionNames.Contains(function.Name)
                                        )
                     .OrderBy(static function => function.Name, StringComparer.Ordinal))
        {
            if (!_typeModel.Functions.TryGetValue(abiFunction.Name, out var signature)
                || !_effectModel.Functions.TryGetValue(abiFunction.Name, out var effects))
            {
                continue;
            }

            var parameterEffects = GetBuiltinParameterEffects(moduleName: string.Empty, abiFunction.Name, signature);
            if (TryEmitBuiltinFunctionDefinition(
                    builder,
                    internalize: true,
                    moduleName: string.Empty,
                    signature,
                    abiFunction,
                    effects,
                    parameterEffects))
            {
                builder.AppendLine();
                continue;
            }

            builder.AppendLine($"; imported declaration: {abiFunction.Name}");
            builder.AppendLine(BuildDeclarationSignature(false, signature, abiFunction, effects, parameterEffects));
            builder.AppendLine();
        }

        return new LlvmIrModule(_syntaxModel.ModuleName, builder.ToString().TrimEnd());
    }

    private void LogLlvmFallback(
        string eventId,
        string functionName,
        string reason,
        FunctionBodyLoweringKind bodyLoweringKind,
        bool supportsDirectCodeGeneration,
        string operation)
    {
        _logs?.GapWarning(
            "codegen",
            eventId,
            $"LLVM emitted only a declaration for '{functionName}': {reason}",
            featureTag: eventId,
            reason: reason,
            stage: "emit-llvm",
            symbolName: functionName,
            operation: operation,
            location: _functionLocations.TryGetValue(functionName, out var location)
                ? location
                : SourceLocation.Synthetic(_input.FilePath),
            outcome: CompilerLogOutcome.Bypassed,
            data: CompilerLogData.Create(
                ("module", _syntaxModel.ModuleName),
                ("function", functionName),
                ("bodyLoweringKind", bodyLoweringKind.ToString()),
                ("supportsDirectCodeGeneration", supportsDirectCodeGeneration.ToString()),
                ("targetTriple", _targetInfo?.Triple)));
    }

    private static IReadOnlyDictionary<string, SourceLocation> BuildFunctionLocationMap(LoadedModuleSet loadedModules, string? rootInputFilePath)
    {
        var locations = new Dictionary<string, SourceLocation>(StringComparer.Ordinal);

        foreach (var module in loadedModules.Modules.Values)
        {
            var filePath = module.Reference.FilePath ?? rootInputFilePath;
            foreach (var declaration in DeclaredFunctionSyntaxCollector.Collect(module.ParseResult, module.SyntaxModel))
            {
                var qualifiedName = module.Reference.IsRoot
                    ? declaration.Name
                    : $"{module.SyntaxModel.ModuleName}.{declaration.Name}";
                locations[qualifiedName] = new SourceLocation(
                    filePath,
                    declaration.NameToken.Line,
                    declaration.NameToken.Column + 1);
            }
        }

        return locations;
    }

    private Func<string, string, AbiFunctionSignature?> CreateCallAbiResolver()
    {
        return (callerName, functionName) =>
        {
            if (_closedWorldImportedLawClones.TryGetValue(functionName, out var clone)
                && _effectModel.Functions.TryGetValue(callerName, out var callerEffects)
                && FunctionKindFacts.IsLaw(callerEffects.Kind))
            {
                return clone.AbiSignature;
            }

            return _allAbiFunctions.TryGetValue(functionName, out var abiFunction)
                ? abiFunction
                : null;
        };
    }

    private static IReadOnlyDictionary<string, TypedFunctionSignature> BuildAllFunctionSignatures(
        TypeCheckModel typeModel,
        SsaIrModule ssa)
    {
        var functions = typeModel.Functions.ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value,
            StringComparer.Ordinal);

        foreach (var function in ssa.Functions)
        {
            functions.TryAdd(
                function.Name,
                new TypedFunctionSignature(
                    function.Name,
                    function.ReturnType,
                    function.Parameters,
                    SourceName: function.Name));
        }

        return functions;
    }

    private static IReadOnlyDictionary<string, AbiFunctionSignature> BuildAllAbiFunctions(
        IReadOnlyDictionary<string, TypedFunctionSignature> allFunctionSignatures,
        AbiModel abiModel,
        FunctionEffectModel effectModel)
    {
        var functions = abiModel.Functions.ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value,
            StringComparer.Ordinal);

        foreach (var function in allFunctionSignatures.Values)
        {
            if (functions.ContainsKey(function.Name))
            {
                continue;
            }

            var isFfi = effectModel.Functions.TryGetValue(function.Name, out var effects) && effects.IsFfi;
            functions[function.Name] = BuildSyntheticAbiSignature(function, function.Name, isFfi);
        }

        return functions;
    }

    private IReadOnlyDictionary<string, ImportedLawClonePlan> BuildClosedWorldImportedLawClones()
    {
        var importedDeclarations = CollectImportedFunctionDeclarations();
        if (importedDeclarations.Count == 0)
        {
            return new Dictionary<string, ImportedLawClonePlan>(StringComparer.Ordinal);
        }

        var ssaByName = _ssa.Functions.ToDictionary(static function => function.Name, StringComparer.Ordinal);
        var callsByFunction = CollectCallsByFunction(_ssa);
        var recursiveImportedLawFunctions = FindRecursiveFunctions(
            callsByFunction,
            functionName => importedDeclarations.ContainsKey(functionName)
                && _effectModel.Functions.TryGetValue(functionName, out var effects)
                && FunctionKindFacts.IsLaw(effects.Kind));
        var rootLawFunctions = _syntaxModel.Declarations
            .Where(static declaration => declaration.Function is not null)
            .Select(declaration => FunctionOverloadFacts.GetResolvedLocalName(_syntaxModel, declaration))
            .Where(name => _effectModel.Functions.TryGetValue(name, out var effects) && FunctionKindFacts.IsLaw(effects.Kind))
            .ToArray();
        var clones = new Dictionary<string, ImportedLawClonePlan>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Queue<string>();

        foreach (var rootFunction in rootLawFunctions)
        {
            if (!callsByFunction.TryGetValue(rootFunction, out var callees))
            {
                continue;
            }

            foreach (var callee in callees)
            {
                EnqueueIfEligible(callee);
            }
        }

        while (pending.Count != 0)
        {
            var functionName = pending.Dequeue();
            if (clones.ContainsKey(functionName))
            {
                continue;
            }

            var signature = _allFunctionSignatures[functionName];
            var effects = BuildLawCloneEffectProfile(functionName);
            var cloneAbi = BuildSyntheticAbiSignature(signature, GetImportedLawCloneSymbolName(functionName), isFfi: false);
            clones[functionName] = new ImportedLawClonePlan(functionName, signature, cloneAbi, effects, ssaByName[functionName]);

            if (!callsByFunction.TryGetValue(functionName, out var importedCallees))
            {
                continue;
            }

            foreach (var callee in importedCallees)
            {
                EnqueueIfEligible(callee);
            }
        }

        return clones;

        void EnqueueIfEligible(string functionName)
        {
            if (!visited.Add(functionName)
                || !IsImportedLawCloneEligible(functionName, importedDeclarations, ssaByName, recursiveImportedLawFunctions))
            {
                return;
            }

            pending.Enqueue(functionName);
        }
    }

    private Dictionary<string, TopLevelDeclarationModel> CollectImportedFunctionDeclarations()
    {
        var declarations = new Dictionary<string, TopLevelDeclarationModel>(StringComparer.Ordinal);

        foreach (var module in _loadedModules.ImportedModules.Where(static module => !module.Reference.IsExternal))
        {
            foreach (var declaration in module.SyntaxModel.Declarations.Where(static declaration => declaration.Function is not null))
            {
                var qualifiedName = FunctionOverloadFacts.QualifyResolvedName(
                    module,
                    FunctionOverloadFacts.GetResolvedLocalName(module.SyntaxModel, declaration));
                declarations[qualifiedName] = declaration;
            }
        }

        return declarations;
    }

    private FunctionEffectProfile BuildLawCloneEffectProfile(string functionName)
    {
        var effects = _effectModel.Functions[functionName];
        return effects.InlinePreference == InlinePreference.Inline
            ? effects
            : effects with { InlinePreference = InlinePreference.Inline };
    }

    private bool IsImportedLawCloneEligible(
        string functionName,
        IReadOnlyDictionary<string, TopLevelDeclarationModel> importedDeclarations,
        IReadOnlyDictionary<string, SsaFunction> ssaByName,
        ISet<string> recursiveImportedLawFunctions)
    {
        return importedDeclarations.TryGetValue(functionName, out var declaration)
            && declaration.Function is { HasBody: true } function
            && declaration.Visibility != StarkVisibility.Export
            && !function.Modifiers.IsFfi
            && !function.Modifiers.IsCold
            && function.Modifiers.InlinePreference != InlinePreference.NoInline
            && (!function.Modifiers.HasExplicitInlinePreference || function.Modifiers.InlinePreference == InlinePreference.Inline)
            && !recursiveImportedLawFunctions.Contains(functionName)
            && _effectModel.Functions.TryGetValue(functionName, out var effects)
            && FunctionKindFacts.IsLaw(effects.Kind)
            && !effects.IsFfi
            && !effects.IsCold
            && _allFunctionSignatures.ContainsKey(functionName)
            && ssaByName.TryGetValue(functionName, out var ssaFunction)
            && ssaFunction.HasBody
            && IsClosedWorldLawCloneEnabled(functionName)
            && ssaFunction.SupportsDirectCodeGeneration;
    }

    private bool IsClosedWorldLawCloneEnabled(string functionName)
    {
        if (_closedWorldModel?.Functions.TryGetValue(functionName, out var optimization) is not true)
        {
            return true;
        }

        return optimization.SelectionOrder.Contains(ClosedWorldCallLoweringStrategy.LawCallerSpecializedClone);
    }

    private static Dictionary<string, HashSet<string>> CollectCallsByFunction(SsaIrModule ssa)
    {
        var callsByFunction = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (var function in ssa.Functions)
        {
            var callees = new HashSet<string>(StringComparer.Ordinal);
            foreach (var block in function.Blocks)
            {
                foreach (var instruction in block.Instructions.OfType<SsaValueInstruction>())
                {
                    if (instruction.Value is SsaCallRValue call)
                    {
                        callees.Add(call.FunctionName);
                    }
                }
            }

            callsByFunction[function.Name] = callees;
        }

        return callsByFunction;
    }

    private static HashSet<string> FindRecursiveFunctions(
        IReadOnlyDictionary<string, HashSet<string>> callGraph,
        Func<string, bool> include)
    {
        var visited = new Dictionary<string, VisitState>(StringComparer.Ordinal);
        var stack = new List<string>();
        var cyclic = new HashSet<string>(StringComparer.Ordinal);

        foreach (var function in callGraph.Keys.Where(include))
        {
            Visit(function);
        }

        return cyclic;

        void Visit(string function)
        {
            if (visited.TryGetValue(function, out var state))
            {
                if (state == VisitState.Visiting)
                {
                    var cycleStart = stack.LastIndexOf(function);
                    if (cycleStart >= 0)
                    {
                        foreach (var item in stack.Skip(cycleStart))
                        {
                            cyclic.Add(item);
                        }
                    }
                }

                return;
            }

            visited[function] = VisitState.Visiting;
            stack.Add(function);

            if (callGraph.TryGetValue(function, out var callees))
            {
                foreach (var callee in callees.Where(include))
                {
                    Visit(callee);
                }
            }

            stack.RemoveAt(stack.Count - 1);
            visited[function] = VisitState.Visited;
        }
    }

    private static AbiFunctionSignature BuildSyntheticAbiSignature(
        TypedFunctionSignature function,
        string symbolName,
        bool isFfi)
    {
        var returnsIndirect = !isFfi && SyntheticAbiRequiresIndirectReturn(function.ReturnType);
        var parameters = new List<AbiParameterSymbol>();

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
            var kind = !isFfi && SyntheticAbiRequiresIndirectParameter(parameter.Type)
                ? AbiParameterKind.IndirectIn
                : AbiParameterKind.Direct;

            parameters.Add(new AbiParameterSymbol(
                SourceName: parameter.Name,
                LlvmName: $"arg_{parameter.Name}",
                SourceType: parameter.Type,
                LlvmType: kind == AbiParameterKind.Direct
                    ? SyntheticLowerAbiValueType(parameter.Type, isFfi, forReturnValue: false)
                    : StarkTypeSymbols.RawPointer(parameter.Type, isMutable: false),
                Kind: kind));
        }

        return new AbiFunctionSignature(
            function.Name,
            symbolName,
            function.ReturnType,
            returnsIndirect
                ? StarkTypeSymbols.Void
                : SyntheticLowerAbiValueType(function.ReturnType, isFfi, forReturnValue: true),
            parameters,
            isFfi,
            SourceName: function.SourceName);
    }

    private static StarkTypeSymbol SyntheticLowerAbiValueType(StarkTypeSymbol type, bool isFfi, bool forReturnValue)
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

    private static bool SyntheticAbiRequiresIndirectParameter(StarkTypeSymbol type)
    {
        return type.BorrowKind != StarkBorrowKind.None
            || type.InitializationKind != StarkInitializationKind.None;
    }

    private static bool SyntheticAbiRequiresIndirectReturn(StarkTypeSymbol type)
    {
        return false;
    }

    private static string GetModuleName(string functionName)
    {
        var separator = functionName.LastIndexOf('.');
        return separator < 0 ? string.Empty : functionName[..separator];
    }

    private static string GetImportedLawCloneSymbolName(string functionName)
    {
        return $"__stark_law_clone_{functionName}";
    }

    private void EmitGlobals(StringBuilder builder)
    {
        foreach (var declaration in _parseResult.Root.topLevelDeclaration())
        {
            var visibility = ParseVisibility(declaration.visibilityModifier());

            if (declaration.globalConstantDeclaration() is { } constantDeclaration)
            {
                foreach (var declarator in constantDeclaration.constantDeclarators().constantDeclarator())
                {
                    var name = declarator.Identifier().GetText();
                    if (!_typeModel.Globals.TryGetValue(name, out var global))
                    {
                        continue;
                    }

                    var symbolName = ResolveGlobalSymbolName(name);
                    if (ShouldEmitExternalConstPlaceholder(global, declarator.variableInitializer()))
                    {
                        builder.AppendLine($"; visibility: {visibility.ToString().ToLowerInvariant()}");
                        builder.AppendLine($"@{EscapeIdentifier(symbolName)} = external constant {MapType(global.Type)}");
                        builder.AppendLine();
                        continue;
                    }

                    if (!TryPlanVariableInitializer(declarator.variableInitializer(), global.Type, isFrozen: true, out var initializer))
                    {
                        builder.AppendLine($"; visibility: {visibility.ToString().ToLowerInvariant()}");
                        builder.AppendLine($"@{EscapeIdentifier(symbolName)} = external constant {MapType(global.Type)}");
                        builder.AppendLine();
                        continue;
                    }

                    EmitGlobalInitializerPrelude(builder, initializer);
                    builder.AppendLine($"; visibility: {visibility.ToString().ToLowerInvariant()}");
                    builder.AppendLine(BuildGlobalDefinition(symbolName, visibility, global, initializer.Rendered));
                    builder.AppendLine();
                }

                continue;
            }

            if (declaration.globalVariableDeclaration() is not { } variableDeclaration)
            {
                continue;
            }

            foreach (var declarator in variableDeclaration.variableDeclarators().variableDeclarator())
            {
                var name = declarator.Identifier().GetText();
                if (!_typeModel.Globals.TryGetValue(name, out var global))
                {
                    continue;
                }

                var symbolName = ResolveGlobalSymbolName(name);
                if (declarator.variableInitializer() is null
                    || !TryPlanVariableInitializer(declarator.variableInitializer(), global.Type, isFrozen: false, out var initializer))
                {
                    builder.AppendLine($"; visibility: {visibility.ToString().ToLowerInvariant()}");
                    var storage = global.IsMutable ? "global" : "constant";
                    builder.AppendLine($"@{EscapeIdentifier(symbolName)} = external {storage} {MapType(global.Type)}");
                    builder.AppendLine();
                    continue;
                }

                EmitGlobalInitializerPrelude(builder, initializer);
                builder.AppendLine($"; visibility: {visibility.ToString().ToLowerInvariant()}");
                builder.AppendLine(BuildGlobalDefinition(symbolName, visibility, global, initializer.Rendered));
                builder.AppendLine();
            }
        }

        EmitImportedGlobalDeclarations(builder);
    }

    private void EmitImportedGlobalDeclarations(StringBuilder builder)
    {
        foreach (var module in _loadedModules.ImportedModules.OrderBy(static module => module.SyntaxModel.ModuleName, StringComparer.Ordinal))
        {
            foreach (var declaration in module.ParseResult.Root.topLevelDeclaration())
            {
                var visibility = ParseVisibility(declaration.visibilityModifier());

                if (declaration.globalConstantDeclaration() is { } constantDeclaration)
                {
                    foreach (var declarator in constantDeclaration.constantDeclarators().constantDeclarator())
                    {
                        EmitImportedGlobalDeclaration(builder, module, visibility, declarator.Identifier().GetText());
                    }

                    continue;
                }

                if (declaration.globalVariableDeclaration() is not { } variableDeclaration)
                {
                    continue;
                }

                foreach (var declarator in variableDeclaration.variableDeclarators().variableDeclarator())
                {
                    EmitImportedGlobalDeclaration(builder, module, visibility, declarator.Identifier().GetText());
                }
            }
        }
    }

    private void EmitImportedGlobalDeclaration(
        StringBuilder builder,
        LoadedModuleDocument module,
        StarkVisibility visibility,
        string sourceName)
    {
        var qualifiedName = $"{module.SyntaxModel.ModuleName}.{sourceName}";
        if (!_typeModel.Globals.TryGetValue(qualifiedName, out var global))
        {
            return;
        }

        var symbolName = ResolveGlobalSymbolName(qualifiedName);
        var storage = global.IsMutable ? "global" : "constant";
        builder.AppendLine($"; imported declaration: {qualifiedName}");
        builder.AppendLine($"; visibility: {visibility.ToString().ToLowerInvariant()}");
        builder.AppendLine($"@{EscapeIdentifier(symbolName)} = external {storage} {MapType(global.Type)}");
        builder.AppendLine();
    }

    private string BuildGlobalDefinition(string symbolName, StarkVisibility visibility, TypedGlobalSymbol global, string initializer)
    {
        var segments = new List<string> { $"@{EscapeIdentifier(symbolName)}", "=" };

        if (ShouldInternalize(visibility))
        {
            segments.Add("internal");
        }

        segments.Add(global.IsMutable ? "global" : "constant");
        segments.Add(MapType(global.Type));
        segments.Add(initializer);
        return string.Join(" ", segments);
    }

    private IReadOnlyDictionary<string, string> BuildGlobalSymbolMap()
    {
        var symbols = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var declaration in _parseResult.Root.topLevelDeclaration())
        {
            var visibility = ParseVisibility(declaration.visibilityModifier());

            if (declaration.globalConstantDeclaration() is { } constantDeclaration)
            {
                foreach (var declarator in constantDeclaration.constantDeclarators().constantDeclarator())
                {
                    var sourceName = declarator.Identifier().GetText();
                    if (!_typeModel.Globals.TryGetValue(sourceName, out var global))
                    {
                        continue;
                    }

                    symbols[sourceName] = ShouldEmitExternalConstPlaceholder(global, declarator.variableInitializer())
                        ? sourceName
                        : GlobalSymbolNaming.ComputeSymbolName(
                            _syntaxModel.ModuleName,
                            sourceName,
                            visibility,
                            _internalizeModulePrivate,
                            isImported: false);
                }

                continue;
            }

            if (declaration.globalVariableDeclaration() is not { } variableDeclaration)
            {
                continue;
            }

            foreach (var declarator in variableDeclaration.variableDeclarators().variableDeclarator())
            {
                var sourceName = declarator.Identifier().GetText();
                symbols[sourceName] = GlobalSymbolNaming.ComputeSymbolName(
                    _syntaxModel.ModuleName,
                    sourceName,
                    visibility,
                    _internalizeModulePrivate,
                    isImported: false);
            }
        }

        foreach (var module in _loadedModules.ImportedModules)
        {
            foreach (var declaration in module.ParseResult.Root.topLevelDeclaration())
            {
                var visibility = ParseVisibility(declaration.visibilityModifier());

                if (declaration.globalConstantDeclaration() is { } constantDeclaration)
                {
                    foreach (var declarator in constantDeclaration.constantDeclarators().constantDeclarator())
                    {
                        var sourceName = declarator.Identifier().GetText();
                        var qualifiedName = $"{module.SyntaxModel.ModuleName}.{sourceName}";
                        if (!_typeModel.Globals.TryGetValue(qualifiedName, out var global))
                        {
                            continue;
                        }

                        symbols[qualifiedName] = ShouldEmitExternalConstPlaceholder(global, declarator.variableInitializer())
                            ? sourceName
                            : GlobalSymbolNaming.ComputeSymbolName(
                                module.SyntaxModel.ModuleName,
                                sourceName,
                                visibility,
                                qualifyModuleSymbols: false,
                                isImported: true);
                    }

                    continue;
                }

                if (declaration.globalVariableDeclaration() is not { } variableDeclaration)
                {
                    continue;
                }

                foreach (var declarator in variableDeclaration.variableDeclarators().variableDeclarator())
                {
                    AddImportedGlobalSymbol(symbols, module.SyntaxModel.ModuleName, visibility, declarator.Identifier().GetText());
                }
            }
        }

        foreach (var globalName in _typeModel.Globals.Keys)
        {
            symbols.TryAdd(globalName, globalName);
        }

        return symbols;
    }

    private void AddImportedGlobalSymbol(
        Dictionary<string, string> symbols,
        string moduleName,
        StarkVisibility visibility,
        string sourceName)
    {
        var qualifiedName = $"{moduleName}.{sourceName}";
        if (!_typeModel.Globals.ContainsKey(qualifiedName))
        {
            return;
        }

        symbols[qualifiedName] = GlobalSymbolNaming.ComputeSymbolName(
            moduleName,
            sourceName,
            visibility,
            qualifyModuleSymbols: false,
            isImported: true);
    }

    private string ResolveGlobalSymbolName(string globalName)
    {
        return _globalSymbols.TryGetValue(globalName, out var symbolName)
            ? symbolName
            : globalName;
    }

    private void EmitIntrinsicDeclarations(StringBuilder builder)
    {
        var declarations = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var binary in EnumerateBinaryOperations()
                     .Where(static binary => binary.Operator == SsaBinaryOperator.Exponent && binary.Type.Kind == StarkTypeKind.Float))
        {
            var llvmType = MapType(binary.Type);
            var suffix = GetFloatIntrinsicSuffix(binary.Type);
            declarations.Add($"declare {llvmType} @llvm.pow.{suffix}({llvmType}, {llvmType})");
        }

        if (UsesLifetimeMarkers())
        {
            declarations.Add("declare void @llvm.lifetime.start.p0(i64 immarg, ptr nocapture)");
            declarations.Add("declare void @llvm.lifetime.end.p0(i64 immarg, ptr nocapture)");
        }

        if (UsesMemcpyIntrinsic())
        {
            declarations.Add("declare void @llvm.memcpy.p0.p0.i64(ptr nocapture writeonly, ptr nocapture readonly, i64, i1 immarg)");
        }

        foreach (var declaration in declarations)
        {
            builder.AppendLine(declaration);
        }

        if (declarations.Count != 0)
        {
            builder.AppendLine();
        }
    }

    private bool UsesLifetimeMarkers()
    {
        return _ssa.Functions
            .SelectMany(static function => function.Blocks)
            .SelectMany(static block => block.Instructions)
            .Any(static instruction => instruction is SsaLifetimeStartInstruction or SsaLifetimeEndInstruction);
    }

    private bool UsesMemcpyIntrinsic()
    {
        return _ssa.Functions
            .SelectMany(static function => function.Blocks)
            .SelectMany(static block => block.Instructions)
            .OfType<SsaCopyMemoryInstruction>()
            .Any(copy => TryGetConcreteTypeLayout(copy.CopyType) is { } layout && layout.SizeBytes > AggregateMemcpyThresholdBytes)
            || UsesBuiltinMemcpyIntrinsic();
    }

    private bool UsesBuiltinMemcpyIntrinsic()
    {
        if (string.Equals(_syntaxModel.ModuleName, "System.Text", StringComparison.Ordinal)
            && _syntaxModel.Declarations
                .Where(static declaration => declaration.Function is { HasBody: false })
                .Select(declaration => FunctionOverloadFacts.GetResolvedLocalName(_syntaxModel, declaration))
                .Any(name => TryGetSystemTextBuiltin(_syntaxModel.ModuleName, name, out var builtinKind)
                    && builtinKind is SystemTextBuiltinKind.TryConcatAscii or SystemTextBuiltinKind.TryConcatUnicode))
        {
            return true;
        }

        return _allAbiFunctions.Keys.Any(name =>
            TryGetSystemTextBuiltin(moduleName: string.Empty, name, out var builtinKind)
            && builtinKind is SystemTextBuiltinKind.TryConcatAscii or SystemTextBuiltinKind.TryConcatUnicode);
    }

    private static void EmitBuiltinTypeDefinitions(StringBuilder builder)
    {
        builder.AppendLine($"%{AsciiStringTypeName} = type {{ ptr, i64 }}");
        builder.AppendLine($"%{UnicodeStringTypeName} = type {{ ptr, i64 }}");
        builder.AppendLine();
    }

    private void EmitNamedTypeDefinitions(StringBuilder builder)
    {
        var emittedAny = false;

        foreach (var namedType in _typeModel.NamedTypes.Values
                     .Where(type => type.Kind is DeclarationKind.Struct or DeclarationKind.Record
                                    || (type.Kind == DeclarationKind.Enum && _enumLayoutModel.Layouts.ContainsKey(type.Name)))
                     .OrderBy(static type => type.Name, StringComparer.Ordinal))
        {
            emittedAny = true;
            var fieldsSource = namedType.Kind == DeclarationKind.Enum
                ? _enumLayoutModel.Layouts[namedType.Name].OrderedFields
                : namedType.OrderedFields;
            var fields = fieldsSource.Count == 0
                ? string.Empty
                : string.Join(", ", fieldsSource.Select(field => MapType(field.Type)));
            builder.AppendLine($"%{EscapeIdentifier(namedType.Name)} = type {{ {fields} }}");
        }

        if (emittedAny)
        {
            builder.AppendLine();
        }
    }

    private void EmitStringConstants(StringBuilder builder)
    {
        foreach (var constant in _stringConstants.Values.OrderBy(static item => item.SymbolName, StringComparer.Ordinal))
        {
            builder.AppendLine($"@{constant.SymbolName} = private unnamed_addr constant {constant.ArrayType} {constant.Initializer}");
        }

        if (_stringConstants.Count != 0)
        {
            builder.AppendLine();
        }
    }

    private void EmitFunctionDefinition(
        StringBuilder builder,
        bool internalize,
        TypedFunctionSignature function,
        AbiFunctionSignature abiFunction,
        FunctionEffectProfile effects,
        SsaFunction ssaFunction,
        IReadOnlyDictionary<string, ParameterMemoryEffectSummary>? parameterEffects,
        Func<string, string, AbiFunctionSignature?> resolveCallAbi)
    {
        var functionBuilder = new StringBuilder();
        functionBuilder.AppendLine(BuildDefinitionSignature(internalize, function, abiFunction, effects, parameterEffects));
        functionBuilder.AppendLine("{");

        var bodyEmitter = new FunctionBodyEmitter(
            functionBuilder,
            function,
            abiFunction,
            resolveCallAbi,
            ssaFunction,
            _stringConstants,
            ResolveGlobalSymbolName,
            MapType,
            TryGetConcreteTypeLayout);
        bodyEmitter.Emit();
        functionBuilder.AppendLine("}");
        builder.Append(functionBuilder);
    }

    private bool TryEmitAsmFunctionDefinition(
        StringBuilder builder,
        bool internalize,
        TypedFunctionSignature function,
        AbiFunctionSignature abiFunction,
        FunctionEffectProfile effects,
        AsmFunctionModel asmFunction,
        IReadOnlyDictionary<string, ParameterMemoryEffectSummary>? parameterEffects,
        out string failureReason)
    {
        if (abiFunction.ReturnsIndirect)
        {
            failureReason = "v1 asm body emission does not support indirect return ABIs.";
            return false;
        }

        if (asmFunction.Outputs.Any(static output => !output.BindsReturnValue))
        {
            failureReason = "v1 asm body emission currently supports only direct return bindings and no out/init parameter outputs.";
            return false;
        }

        if (function.ReturnType.Kind == StarkTypeKind.Void)
        {
            if (asmFunction.Outputs.Count != 0)
            {
                failureReason = "void asm functions cannot bind a return register.";
                return false;
            }
        }
        else if (asmFunction.Outputs.Count != 1)
        {
            failureReason = "non-void asm functions must bind exactly one return register.";
            return false;
        }

        foreach (var parameter in abiFunction.UserParameters)
        {
            if (parameter.Kind != AbiParameterKind.Direct)
            {
                failureReason = $"v1 asm body emission requires direct ABI parameters, but '{parameter.SourceName}' lowers indirectly.";
                return false;
            }
        }

        var functionBuilder = new StringBuilder();
        functionBuilder.AppendLine(BuildDefinitionSignature(internalize, function, abiFunction, effects, parameterEffects));
        functionBuilder.AppendLine("{");
        EmitAsmFunctionBody(functionBuilder, function, abiFunction, asmFunction);
        functionBuilder.AppendLine("}");
        builder.Append(functionBuilder);

        failureReason = string.Empty;
        return true;
    }

    private void EmitAsmFunctionBody(
        StringBuilder builder,
        TypedFunctionSignature function,
        AbiFunctionSignature abiFunction,
        AsmFunctionModel asmFunction)
    {
        var abiParametersByName = abiFunction.UserParameters.ToDictionary(static parameter => parameter.SourceName, StringComparer.Ordinal);
        var outputOperand = asmFunction.Outputs.SingleOrDefault(static output => output.BindsReturnValue);
        var constraintFragments = new List<string>();
        var argumentFragments = new List<string>();
        string? returnRegister = null;

        if (outputOperand is not null)
        {
            returnRegister = StarkAsmRegisterFacts.Normalize(outputOperand.RegisterName);
            constraintFragments.Add($"={{{returnRegister}}}");
        }

        foreach (var input in asmFunction.Inputs)
        {
            if (!abiParametersByName.TryGetValue(input.ValueName, out var parameter))
            {
                throw new InvalidOperationException($"Missing ABI parameter '{input.ValueName}' for asm declaration '{function.Name}'.");
            }

            var inputRegister = StarkAsmRegisterFacts.Normalize(input.RegisterName);
            constraintFragments.Add(string.Equals(returnRegister, inputRegister, StringComparison.Ordinal)
                ? "0"
                : $"{{{inputRegister}}}");
            argumentFragments.Add($"{MapType(parameter.LlvmType)} %{EscapeIdentifier(parameter.LlvmName)}");
        }

        foreach (var clobber in BuildAsmConstraintClobbers(asmFunction))
        {
            constraintFragments.Add($"~{{{clobber}}}");
        }

        var escapedTemplate = EscapeInlineAsmString(asmFunction.TemplateText);
        var escapedConstraints = EscapeInlineAsmString(string.Join(",", constraintFragments));

        builder.AppendLine("entry:");
        if (function.ReturnType.Kind == StarkTypeKind.Void)
        {
            builder.AppendLine(
                $"  call void asm sideeffect \"{escapedTemplate}\", \"{escapedConstraints}\"({string.Join(", ", argumentFragments)})");
            builder.AppendLine("  ret void");
            return;
        }

        var llvmReturnType = MapType(abiFunction.LlvmReturnType);
        builder.AppendLine(
            $"  %asm_result = call {llvmReturnType} asm sideeffect \"{escapedTemplate}\", \"{escapedConstraints}\"({string.Join(", ", argumentFragments)})");
        builder.AppendLine($"  ret {llvmReturnType} %asm_result");
    }

    private static IReadOnlyList<string> BuildAsmConstraintClobbers(AsmFunctionModel asmFunction)
    {
        var clobbers = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Add(string name)
        {
            var normalized = StarkAsmRegisterFacts.Normalize(name);
            if (seen.Add(normalized))
            {
                clobbers.Add(normalized);
            }
        }

        foreach (var clobber in asmFunction.Clobbers)
        {
            Add(clobber);
        }

        Add("memory");

        if (asmFunction.Architecture is StarkAsmArchitecture.X86_64 or StarkAsmArchitecture.X86)
        {
            Add("dirflag");
            Add("fpsr");
            Add("flags");
        }

        return clobbers;
    }

    private string BuildDeclarationSignature(
        bool internalize,
        TypedFunctionSignature function,
        AbiFunctionSignature abiFunction,
        FunctionEffectProfile effects,
        IReadOnlyDictionary<string, ParameterMemoryEffectSummary>? parameterEffects)
    {
        var segments = new List<string> { "declare" };

        if (internalize)
        {
            segments.Add("internal");
        }

        if (effects.UseFastCallingConvention)
        {
            segments.Add("fastcc");
        }

        segments.Add(MapType(abiFunction.LlvmReturnType));
        segments.Add($"@{EscapeIdentifier(abiFunction.SymbolName)}({string.Join(", ", abiFunction.Parameters.Select(parameter => RenderAbiParameter(parameter, includeName: false, parameterEffects)))})");

        var attributes = BuildFunctionAttributes(effects);
        if (!string.IsNullOrWhiteSpace(attributes))
        {
            segments.Add(attributes);
        }

        return string.Join(" ", segments);
    }

    private string BuildDefinitionSignature(
        bool internalize,
        TypedFunctionSignature function,
        AbiFunctionSignature abiFunction,
        FunctionEffectProfile effects,
        IReadOnlyDictionary<string, ParameterMemoryEffectSummary>? parameterEffects)
    {
        var segments = new List<string> { "define" };

        if (internalize)
        {
            segments.Add("internal");
        }

        if (effects.UseFastCallingConvention)
        {
            segments.Add("fastcc");
        }

        segments.Add(MapType(abiFunction.LlvmReturnType));
        segments.Add($"@{EscapeIdentifier(abiFunction.SymbolName)}({string.Join(", ", abiFunction.Parameters.Select(parameter => RenderAbiParameter(parameter, includeName: true, parameterEffects)))})");

        var attributes = BuildFunctionAttributes(effects);
        if (!string.IsNullOrWhiteSpace(attributes))
        {
            segments.Add(attributes);
        }

        return string.Join(" ", segments);
    }

    private bool TryEmitBuiltinFunctionDefinition(
        StringBuilder builder,
        bool internalize,
        string moduleName,
        TypedFunctionSignature function,
        AbiFunctionSignature abiFunction,
        FunctionEffectProfile effects,
        IReadOnlyDictionary<string, ParameterMemoryEffectSummary>? parameterEffects)
    {
        if (!TryGetSystemTextBuiltin(moduleName, function.Name, out var builtinKind))
        {
            return false;
        }

        builder.AppendLine(BuildDefinitionSignature(internalize, function, abiFunction, effects, parameterEffects) + " {");
        switch (builtinKind)
        {
            case SystemTextBuiltinKind.AsciiView:
                EmitOwnedTextViewBuiltin(builder, abiFunction, StarkTypeSymbols.Ascii);
                break;
            case SystemTextBuiltinKind.UnicodeView:
                EmitOwnedTextViewBuiltin(builder, abiFunction, StarkTypeSymbols.Unicode);
                break;
            case SystemTextBuiltinKind.AsciiData:
            case SystemTextBuiltinKind.UnicodeData:
                EmitTextViewDataBuiltin(builder, abiFunction);
                break;
            case SystemTextBuiltinKind.AsciiLength:
            case SystemTextBuiltinKind.UnicodeLength:
                EmitTextViewLengthBuiltin(builder, abiFunction);
                break;
            case SystemTextBuiltinKind.TryConcatAscii:
                EmitOwnedTextConcatBuiltin(builder, abiFunction, StarkTypeSymbols.Integer(8), StarkTypeSymbols.Ascii);
                break;
            case SystemTextBuiltinKind.TryConcatUnicode:
                EmitOwnedTextConcatBuiltin(builder, abiFunction, StarkTypeSymbols.Integer(32), StarkTypeSymbols.Unicode);
                break;
            default:
                throw new InvalidOperationException($"Unsupported System.Text builtin '{builtinKind}'.");
        }

        builder.AppendLine("}");
        return true;
    }

    private void EmitOwnedTextViewBuiltin(
        StringBuilder builder,
        AbiFunctionSignature abiFunction,
        StarkTypeSymbol viewType)
    {
        if (abiFunction.UserParameters.Count != 1)
        {
            throw new InvalidOperationException($"System.Text view builtin '{abiFunction.Name}' expects exactly one user parameter.");
        }

        var sourceParameter = abiFunction.UserParameters[0];
        var aggregateType = MapType(sourceParameter.SourceType);
        var resultType = MapType(viewType);
        var sourceValue = $"%{EscapeIdentifier(sourceParameter.LlvmName)}";

        builder.AppendLine("entry:");
        builder.AppendLine($"  %view_data = extractvalue {aggregateType} {sourceValue}, 0");
        builder.AppendLine($"  %view_length = extractvalue {aggregateType} {sourceValue}, 1");
        builder.AppendLine($"  %view_with_ptr = insertvalue {resultType} zeroinitializer, ptr %view_data, 0");
        builder.AppendLine($"  %view_result = insertvalue {resultType} %view_with_ptr, i64 %view_length, 1");
        builder.AppendLine($"  ret {resultType} %view_result");
    }

    private void EmitTextViewDataBuiltin(
        StringBuilder builder,
        AbiFunctionSignature abiFunction)
    {
        if (abiFunction.UserParameters.Count != 1)
        {
            throw new InvalidOperationException($"System.Text data builtin '{abiFunction.Name}' expects exactly one user parameter.");
        }

        var sourceParameter = abiFunction.UserParameters[0];
        var aggregateType = MapType(sourceParameter.SourceType);
        var resultType = MapType(abiFunction.LlvmReturnType);
        var sourceValue = $"%{EscapeIdentifier(sourceParameter.LlvmName)}";

        builder.AppendLine("entry:");
        builder.AppendLine($"  %view_data = extractvalue {aggregateType} {sourceValue}, 0");
        builder.AppendLine($"  ret {resultType} %view_data");
    }

    private void EmitTextViewLengthBuiltin(
        StringBuilder builder,
        AbiFunctionSignature abiFunction)
    {
        if (abiFunction.UserParameters.Count != 1)
        {
            throw new InvalidOperationException($"System.Text length builtin '{abiFunction.Name}' expects exactly one user parameter.");
        }

        var sourceParameter = abiFunction.UserParameters[0];
        var aggregateType = MapType(sourceParameter.SourceType);
        var resultType = MapType(abiFunction.LlvmReturnType);
        var sourceValue = $"%{EscapeIdentifier(sourceParameter.LlvmName)}";

        builder.AppendLine("entry:");
        builder.AppendLine($"  %view_length = extractvalue {aggregateType} {sourceValue}, 1");
        builder.AppendLine($"  ret {resultType} %view_length");
    }

    private void EmitOwnedTextConcatBuiltin(
        StringBuilder builder,
        AbiFunctionSignature abiFunction,
        StarkTypeSymbol unitType,
        StarkTypeSymbol viewType)
    {
        if (abiFunction.UserParameters.Count != 3)
        {
            throw new InvalidOperationException($"System.Text concat builtin '{abiFunction.Name}' expects exactly three user parameters.");
        }

        var destinationParameter = abiFunction.UserParameters[0];
        var leftParameter = abiFunction.UserParameters[1];
        var rightParameter = abiFunction.UserParameters[2];
        var aggregateType = destinationParameter.SourceType.ElementType is not null
            ? MapType(destinationParameter.SourceType.ElementType)
            : throw new InvalidOperationException($"System.Text concat builtin '{abiFunction.Name}' requires a raw pointer destination to an owning text aggregate.");
        var viewLlvmType = MapType(viewType);
        var unitLlvmType = MapType(unitType);
        var destinationPointer = $"%{EscapeIdentifier(destinationParameter.LlvmName)}";
        var leftValue = $"%{EscapeIdentifier(leftParameter.LlvmName)}";
        var rightValue = $"%{EscapeIdentifier(rightParameter.LlvmName)}";

        builder.AppendLine("entry:");
        builder.AppendLine($"  %concat_data_addr = getelementptr inbounds {aggregateType}, ptr {destinationPointer}, i32 0, i32 0");
        builder.AppendLine($"  %concat_length_addr = getelementptr inbounds {aggregateType}, ptr {destinationPointer}, i32 0, i32 1");
        builder.AppendLine($"  %concat_capacity_addr = getelementptr inbounds {aggregateType}, ptr {destinationPointer}, i32 0, i32 2");
        builder.AppendLine("  %concat_data = load ptr, ptr %concat_data_addr");
        builder.AppendLine("  %concat_capacity = load i64, ptr %concat_capacity_addr");
        builder.AppendLine($"  %concat_left_data = extractvalue {viewLlvmType} {leftValue}, 0");
        builder.AppendLine($"  %concat_left_length = extractvalue {viewLlvmType} {leftValue}, 1");
        builder.AppendLine($"  %concat_right_data = extractvalue {viewLlvmType} {rightValue}, 0");
        builder.AppendLine($"  %concat_right_length = extractvalue {viewLlvmType} {rightValue}, 1");
        builder.AppendLine("  %concat_required = add i64 %concat_left_length, %concat_right_length");
        builder.AppendLine("  %concat_has_capacity = icmp ule i64 %concat_required, %concat_capacity");
        builder.AppendLine("  %concat_needs_storage = icmp ne i64 %concat_required, 0");
        builder.AppendLine("  %concat_has_data = icmp ne ptr %concat_data, null");
        builder.AppendLine("  %concat_storage_ready = select i1 %concat_needs_storage, i1 %concat_has_data, i1 true");
        builder.AppendLine("  %concat_success = and i1 %concat_has_capacity, %concat_storage_ready");
        builder.AppendLine("  br i1 %concat_success, label %concat_copy_left_check, label %concat_fail");
        builder.AppendLine("concat_fail:");
        builder.AppendLine("  ret i1 false");
        builder.AppendLine("concat_copy_left_check:");
        builder.AppendLine("  %concat_left_nonempty = icmp ne i64 %concat_left_length, 0");
        builder.AppendLine("  br i1 %concat_left_nonempty, label %concat_copy_left_prepare, label %concat_after_left");
        builder.AppendLine("concat_copy_left_prepare:");
        builder.AppendLine($"  %concat_left_bytes = {RenderTextMemcpyLength(unitType, "%concat_left_length")}");
        builder.AppendLine("  call void @llvm.memcpy.p0.p0.i64(ptr %concat_data, ptr %concat_left_data, i64 %concat_left_bytes, i1 false)");
        builder.AppendLine("  br label %concat_after_left");
        builder.AppendLine("concat_after_left:");
        builder.AppendLine("  %concat_right_nonempty = icmp ne i64 %concat_right_length, 0");
        builder.AppendLine("  br i1 %concat_right_nonempty, label %concat_copy_right_prepare, label %concat_finish");
        builder.AppendLine("concat_copy_right_prepare:");
        builder.AppendLine($"  %concat_right_dest = getelementptr inbounds {unitLlvmType}, ptr %concat_data, i64 %concat_left_length");
        builder.AppendLine($"  %concat_right_bytes = {RenderTextMemcpyLength(unitType, "%concat_right_length")}");
        builder.AppendLine("  call void @llvm.memcpy.p0.p0.i64(ptr %concat_right_dest, ptr %concat_right_data, i64 %concat_right_bytes, i1 false)");
        builder.AppendLine("  br label %concat_finish");
        builder.AppendLine("concat_finish:");
        builder.AppendLine("  store i64 %concat_required, ptr %concat_length_addr");
        builder.AppendLine("  ret i1 true");
    }

    private static string RenderTextMemcpyLength(StarkTypeSymbol unitType, string lengthValue)
    {
        return unitType.Kind == StarkTypeKind.Integer && unitType.BitWidth == 32
            ? $"shl i64 {lengthValue}, 2"
            : $"add i64 {lengthValue}, 0";
    }

    private IReadOnlyDictionary<string, ParameterMemoryEffectSummary>? GetBuiltinParameterEffects(
        string moduleName,
        string functionName,
        TypedFunctionSignature function)
    {
        if (!TryGetSystemTextBuiltin(moduleName, functionName, out var builtinKind))
        {
            return null;
        }

        return builtinKind switch
        {
            SystemTextBuiltinKind.AsciiView or SystemTextBuiltinKind.UnicodeView
                or SystemTextBuiltinKind.AsciiData or SystemTextBuiltinKind.UnicodeData
                or SystemTextBuiltinKind.AsciiLength or SystemTextBuiltinKind.UnicodeLength
                => function.Parameters.ToDictionary(
                    static parameter => parameter.Name,
                    static parameter => new ParameterMemoryEffectSummary(
                        parameter.Name,
                        parameter.Type.DisplayName,
                        IsMemoryBacked: true,
                        GuaranteedNonNull: true,
                        GuaranteedReadOnly: true,
                        GuaranteedWriteOnly: false,
                        GuaranteedNoAlias: true,
                        DereferenceableBytes: null,
                        AlignmentBytes: null,
                        Reads: true,
                        Writes: false,
                        CaptureKind: ParameterCaptureKind.None),
                    StringComparer.Ordinal),
            SystemTextBuiltinKind.TryConcatAscii or SystemTextBuiltinKind.TryConcatUnicode
                => function.Parameters.ToDictionary(
                    static parameter => parameter.Name,
                    static parameter => new ParameterMemoryEffectSummary(
                        parameter.Name,
                        parameter.Type.DisplayName,
                        IsMemoryBacked: parameter.Name == "destination",
                        GuaranteedNonNull: false,
                        GuaranteedReadOnly: false,
                        GuaranteedWriteOnly: false,
                        GuaranteedNoAlias: false,
                        DereferenceableBytes: null,
                        AlignmentBytes: null,
                        Reads: parameter.Name == "destination",
                        Writes: parameter.Name == "destination",
                        CaptureKind: ParameterCaptureKind.None),
                    StringComparer.Ordinal),
            _ => null
        };
    }

    private static bool TryGetSystemTextBuiltin(
        string moduleName,
        string functionName,
        out SystemTextBuiltinKind builtinKind)
    {
        builtinKind = default;

        string sourceName;
        if (functionName.Contains('.', StringComparison.Ordinal))
        {
            const string prefix = "System.Text.";
            if (!functionName.StartsWith(prefix, StringComparison.Ordinal))
            {
                return false;
            }

            sourceName = functionName[prefix.Length..];
        }
        else
        {
            if (!string.Equals(moduleName, "System.Text", StringComparison.Ordinal))
            {
                return false;
            }

            sourceName = functionName;
        }

        builtinKind = sourceName switch
        {
            "AsciiView" => SystemTextBuiltinKind.AsciiView,
            "UnicodeView" => SystemTextBuiltinKind.UnicodeView,
            "AsciiData" => SystemTextBuiltinKind.AsciiData,
            "UnicodeData" => SystemTextBuiltinKind.UnicodeData,
            "AsciiLength" => SystemTextBuiltinKind.AsciiLength,
            "UnicodeLength" => SystemTextBuiltinKind.UnicodeLength,
            "TryConcatAscii" => SystemTextBuiltinKind.TryConcatAscii,
            "TryConcatUnicode" => SystemTextBuiltinKind.TryConcatUnicode,
            _ => default
        };

        return sourceName is
            "AsciiView" or "UnicodeView"
            or "AsciiData" or "UnicodeData"
            or "AsciiLength" or "UnicodeLength"
            or "TryConcatAscii" or "TryConcatUnicode";
    }

    private string RenderAbiParameter(
        AbiParameterSymbol parameter,
        bool includeName,
        IReadOnlyDictionary<string, ParameterMemoryEffectSummary>? parameterEffects)
    {
        var segments = new List<string> { MapType(parameter.LlvmType) };
        segments.AddRange(DeriveAbiParameterAttributes(parameter, ResolveParameterEffects(parameter, parameterEffects)));

        if (includeName)
        {
            segments.Add($"%{EscapeIdentifier(parameter.LlvmName)}");
        }

        return string.Join(" ", segments);
    }

    private IReadOnlyList<string> DeriveAbiParameterAttributes(AbiParameterSymbol parameter, ParameterMemoryEffectSummary? parameterEffects)
    {
        var attributes = new List<string>();

        if (parameter.Kind == AbiParameterKind.SRet)
        {
            attributes.Add("noalias");
            attributes.Add($"sret({MapType(parameter.SourceType)})");
            attributes.Add("nonnull");
            if (TryGetConcreteTypeLayout(parameter.SourceType) is { } sretLayout)
            {
                attributes.Add($"dereferenceable({sretLayout.SizeBytes})");
                if (sretLayout.AlignmentBytes > 1)
                {
                    attributes.Add($"align {sretLayout.AlignmentBytes}");
                }
            }

            return attributes;
        }

        if (parameter.Kind == AbiParameterKind.IndirectIn)
        {
            attributes.Add("nonnull");
            attributes.Add("noalias");
            AppendPointerMemoryAccessAttributes(attributes, parameter, parameterEffects);
            AppendNoCaptureAttribute(attributes, parameterEffects);

            if (TryGetConcreteTypeLayout(parameter.SourceType) is { } indirectLayout)
            {
                attributes.Add($"dereferenceable({indirectLayout.SizeBytes})");
                if (indirectLayout.AlignmentBytes > 1)
                {
                    attributes.Add($"align {indirectLayout.AlignmentBytes}");
                }
            }

            return attributes;
        }

        if (parameter.LlvmType.Kind != StarkTypeKind.RawPointer)
        {
            return attributes;
        }

        if (parameter.SourceType.BorrowKind != StarkBorrowKind.None || parameter.SourceType.InitializationKind != StarkInitializationKind.None)
        {
            attributes.Add("nonnull");
        }

        if (parameter.SourceType.InitializationKind != StarkInitializationKind.None)
        {
            attributes.Add("noalias");
        }

        AppendPointerMemoryAccessAttributes(attributes, parameter, parameterEffects);
        AppendNoCaptureAttribute(attributes, parameterEffects);

        return attributes;
    }

    private IReadOnlyDictionary<string, ParameterMemoryEffectSummary>? GetRootParameterEffects(string functionName, bool hasBody)
    {
        if (!hasBody
            || _semanticValidation is null
            || !_semanticValidation.Functions.TryGetValue(functionName, out var validation)
            || validation.Parameters is null)
        {
            return null;
        }

        return validation.Parameters.ToDictionary(static parameter => parameter.Name, StringComparer.Ordinal);
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

    private static void AppendPointerMemoryAccessAttributes(
        List<string> attributes,
        AbiParameterSymbol parameter,
        ParameterMemoryEffectSummary? parameterEffects)
    {
        if (parameterEffects is not null)
        {
            if (parameterEffects.Writes)
            {
                if (!parameterEffects.Reads)
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

        if (parameter.SourceType.InitializationKind != StarkInitializationKind.None)
        {
            attributes.Add("writeonly");
            return;
        }

        if (parameter.SourceType.Kind is StarkTypeKind.Ascii or StarkTypeKind.Unicode
            || (parameter.SourceType.Kind == StarkTypeKind.RawPointer && !parameter.SourceType.IsMutablePointer)
            || (parameter.SourceType.BorrowKind != StarkBorrowKind.None && !parameter.SourceType.IsMutableView))
        {
            attributes.Add("readonly");
        }
    }

    private static void AppendNoCaptureAttribute(List<string> attributes, ParameterMemoryEffectSummary? parameterEffects)
    {
        if (parameterEffects?.CaptureKind == ParameterCaptureKind.None)
        {
            attributes.Add("nocapture");
        }
    }

    private static string BuildFunctionAttributes(FunctionEffectProfile effects)
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

        if (effects.IsPure)
        {
            attributes.Add(effects.ReadsArgumentMemory ? "memory(argmem: read)" : "memory(none)");
        }

        if (effects.IsHot)
        {
            attributes.Add("hot");
        }

        if (effects.IsCold)
        {
            attributes.Add("cold");
        }

        attributes.Add(effects.InlinePreference switch
        {
            InlinePreference.Inline => "alwaysinline",
            InlinePreference.NoInline => "noinline",
            _ => "inlinehint"
        });

        return string.Join(" ", attributes);
    }

    private bool ShouldInternalize(StarkVisibility visibility) => _internalizeModulePrivate && visibility == StarkVisibility.Module;

    private IEnumerable<SsaBinaryRValue> EnumerateBinaryOperations()
    {
        return _ssa.Functions
            .SelectMany(static function => function.Blocks)
            .SelectMany(static block => block.Instructions)
            .OfType<SsaValueInstruction>()
            .Select(static instruction => instruction.Value)
            .OfType<SsaBinaryRValue>();
    }

    private string MapType(StarkTypeSymbol type)
    {
        return type.Kind switch
        {
            StarkTypeKind.Void => "void",
            StarkTypeKind.Bool => "i1",
            StarkTypeKind.Integer => $"i{type.BitWidth}",
            StarkTypeKind.Float when type.BitWidth == 16 => "half",
            StarkTypeKind.Float when type.BitWidth == 32 => "float",
            StarkTypeKind.Float when type.BitWidth == 64 => "double",
            StarkTypeKind.Float when type.BitWidth == 80 => "x86_fp80",
            StarkTypeKind.Float when type.BitWidth == 128 => "fp128",
            StarkTypeKind.RawPointer => "ptr",
            StarkTypeKind.FixedArray when type.ElementType is not null && type.FixedLength is int fixedLength => $"[{fixedLength} x {MapType(type.ElementType)}]",
            StarkTypeKind.Slice => "{ ptr, i64 }",
            StarkTypeKind.Ascii => $"%{AsciiStringTypeName}",
            StarkTypeKind.Unicode => $"%{UnicodeStringTypeName}",
            StarkTypeKind.Named when type.NamedType is not null
                                     && _typeModel.NamedTypes.TryGetValue(type.NamedType, out var namedType)
                                     && (namedType.Kind is DeclarationKind.Struct or DeclarationKind.Record
                                         || (namedType.Kind == DeclarationKind.Enum && _enumLayoutModel.Layouts.ContainsKey(namedType.Name)))
                => $"%{EscapeIdentifier(type.NamedType)}",
            StarkTypeKind.Named => "ptr",
            StarkTypeKind.Null => "ptr",
            _ => "ptr"
        };
    }

    private static string GetFloatIntrinsicSuffix(StarkTypeSymbol type)
    {
        return type.BitWidth switch
        {
            16 => "f16",
            32 => "f32",
            64 => "f64",
            80 => "f80",
            128 => "f128",
            _ => throw new InvalidOperationException($"Unsupported float intrinsic width '{type.BitWidth}'.")
        };
    }

    private static string EscapeFileName(string filePath)
    {
        return filePath.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
    }

    private static string EscapeInlineAsmString(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (ch >= 0x20 && ch <= 0x7E && ch is not '\\' and not '"')
            {
                builder.Append(ch);
                continue;
            }

            builder.Append('\\');
            builder.Append(((int)ch).ToString("X2"));
        }

        return builder.ToString();
    }

    private static string EscapeIdentifier(string identifier)
    {
        var builder = new StringBuilder(identifier.Length);
        foreach (var ch in identifier)
        {
            builder.Append(char.IsLetterOrDigit(ch) || ch == '_' ? ch : '_');
        }

        return builder.ToString();
    }

    private static LoadedModuleSet CreateRootLoadedModules(ParseResult parseResult, SyntaxModel syntaxModel, string? filePath)
    {
        return new LoadedModuleSet(
            syntaxModel.ModuleName,
            new Dictionary<string, LoadedModuleDocument>(StringComparer.Ordinal)
            {
                [syntaxModel.ModuleName] = new(
                    new ResolvedModuleReference(syntaxModel.ModuleName, filePath, IsExternal: false, IsRoot: true),
                    parseResult,
                    syntaxModel)
            });
    }

    private static IReadOnlyDictionary<StringConstantKey, EmittedStringConstant> CollectStringConstants(ParseResult parseResult, SsaIrModule ssa)
    {
        var result = new Dictionary<StringConstantKey, EmittedStringConstant>();
        var index = 0;

        AddGlobalStringConstants(parseResult, result, ref index);

        foreach (var function in ssa.Functions)
        {
            foreach (var block in function.Blocks)
            {
                foreach (var phi in block.Phis)
                {
                    foreach (var incoming in phi.Incomings)
                    {
                        AddStringConstant(incoming.Value, result, ref index);
                    }
                }

                foreach (var instruction in block.Instructions)
                {
                    switch (instruction)
                    {
                        case SsaValueInstruction valueInstruction:
                            AddStringConstant(valueInstruction.Value, result, ref index);
                            break;
                        case SsaStoreGlobalInstruction storeGlobal:
                            AddStringConstant(storeGlobal.Value, result, ref index);
                            break;
                    }
                }

                AddStringConstant(block.Terminator.Condition, result, ref index);
                AddStringConstant(block.Terminator.Value, result, ref index);
            }
        }

        return result;
    }

    private static void AddGlobalStringConstants(ParseResult parseResult, Dictionary<StringConstantKey, EmittedStringConstant> constants, ref int index)
    {
        foreach (var declaration in parseResult.Root.topLevelDeclaration())
        {
            if (declaration.globalConstantDeclaration() is { } constantDeclaration)
            {
                foreach (var declarator in constantDeclaration.constantDeclarators().constantDeclarator())
                {
                    AddStringConstant(declarator.variableInitializer(), constants, ref index);
                }

                continue;
            }

            if (declaration.globalVariableDeclaration() is not { } variableDeclaration)
            {
                continue;
            }

            foreach (var declarator in variableDeclaration.variableDeclarators().variableDeclarator())
            {
                AddStringConstant(declarator.variableInitializer(), constants, ref index);
            }
        }
    }

    private static void AddStringConstant(object? source, Dictionary<StringConstantKey, EmittedStringConstant> constants, ref int index)
    {
        switch (source)
        {
            case null:
                return;
            case SsaStringConstant text:
                AddStringLiteral(text.LiteralText, text.Type, constants, ref index);
                return;
            case SsaUseRValue use:
                AddStringConstant(use.Value, constants, ref index);
                return;
            case SsaUnaryRValue unary:
                AddStringConstant(unary.Operand, constants, ref index);
                return;
            case SsaBinaryRValue binary:
                AddStringConstant(binary.Left, constants, ref index);
                AddStringConstant(binary.Right, constants, ref index);
                return;
            case SsaCallRValue call:
                foreach (var argument in call.Arguments)
                {
                    AddStringConstant(argument, constants, ref index);
                }

                return;
            case SsaConvertRValue convert:
                AddStringConstant(convert.Operand, constants, ref index);
                return;
            case SsaExtractFieldRValue extract:
                AddStringConstant(extract.Target, constants, ref index);
                return;
            case SsaInsertFieldRValue insert:
                AddStringConstant(insert.Target, constants, ref index);
                AddStringConstant(insert.Value, constants, ref index);
                return;
            case SsaExtractIndexRValue extractIndex:
                AddStringConstant(extractIndex.Target, constants, ref index);
                return;
            case SsaInsertIndexRValue insertIndex:
                AddStringConstant(insertIndex.Target, constants, ref index);
                AddStringConstant(insertIndex.Value, constants, ref index);
                return;
            case SsaLoadSliceElementRValue loadSlice:
                AddStringConstant(loadSlice.Slice, constants, ref index);
                AddStringConstant(loadSlice.Index, constants, ref index);
                return;
            case SsaTextSliceRValue textSlice:
                AddStringConstant(textSlice.TextValue, constants, ref index);
                AddStringConstant(textSlice.Start, constants, ref index);
                AddStringConstant(textSlice.Length, constants, ref index);
                return;
            case SsaFieldAddressRValue fieldAddress:
                AddStringConstant(fieldAddress.Address, constants, ref index);
                return;
            case SsaElementAddressRValue elementAddress:
                AddStringConstant(elementAddress.Address, constants, ref index);
                AddStringConstant(elementAddress.Index, constants, ref index);
                return;
            case SsaSliceElementAddressRValue sliceElementAddress:
                AddStringConstant(sliceElementAddress.Slice, constants, ref index);
                AddStringConstant(sliceElementAddress.Index, constants, ref index);
                return;
            case SsaLoadIndirectRValue loadIndirect:
                AddStringConstant(loadIndirect.Address, constants, ref index);
                return;
            case StarkParser.VariableInitializerContext initializer:
                if (initializer.expression() is { } initializerExpression)
                {
                    AddStringConstant(initializerExpression, constants, ref index);
                    return;
                }

                if (initializer.objectInitializer() is { } initializerObject)
                {
                    AddStringConstant(initializerObject, constants, ref index);
                    return;
                }

                if (initializer.arrayInitializer() is { } initializerArray)
                {
                    AddStringConstant(initializerArray, constants, ref index);
                }

                return;
            case StarkParser.ExpressionContext expression:
                if (TryUnwrapSimplePrimaryExpression(expression, out var unwrappedPrimaryExpression))
                {
                    AddStringConstant(unwrappedPrimaryExpression, constants, ref index);
                }

                return;
            case StarkParser.PrimaryExpressionContext primaryExpression:
                if (primaryExpression.literal() is { } literal)
                {
                    AddStringConstant(literal, constants, ref index);
                    return;
                }

                if (primaryExpression.objectCreationExpression() is { } creation)
                {
                    AddStringConstant(creation, constants, ref index);
                    return;
                }

                if (primaryExpression.expression() is { } groupedExpression)
                {
                    AddStringConstant(groupedExpression, constants, ref index);
                }

                return;
            case StarkParser.ObjectCreationExpressionContext objectCreation:
                foreach (var argument in objectCreation.argumentList()?.argument() ?? [])
                {
                    AddStringConstant(argument.expression(), constants, ref index);
                }

                AddStringConstant(objectCreation.objectInitializer(), constants, ref index);
                return;
            case StarkParser.ObjectInitializerContext objectInitializer:
                foreach (var memberInitializer in objectInitializer.memberInitializer())
                {
                    AddStringConstant(memberInitializer.variableInitializer(), constants, ref index);
                }

                return;
            case StarkParser.ArrayInitializerContext arrayInitializer:
                foreach (var element in arrayInitializer.variableInitializer())
                {
                    AddStringConstant(element, constants, ref index);
                }

                return;
            case StarkParser.LiteralContext parseLiteral:
                if (parseLiteral.StringLiteral() is { } literalString)
                {
                    AddStringLiteral(literalString.GetText(), constants, ref index);
                    return;
                }

                if (parseLiteral.CharacterLiteral() is { } characterLiteral)
                {
                    AddStringLiteral(characterLiteral.GetText(), constants, ref index);
                }

                return;
        }
    }

    private static bool TryUnwrapSimplePrimaryExpression(StarkParser.ExpressionContext expression, out StarkParser.PrimaryExpressionContext primaryExpression)
    {
        primaryExpression = null!;

        if (expression.assignmentExpression().conditionalExpression() is not { } conditionalExpression
            || conditionalExpression.QUESTION() is not null)
        {
            return false;
        }

        var logicalOr = conditionalExpression.logicalOrExpression();
        if (logicalOr.logicalAndExpression().Length != 1)
        {
            return false;
        }

        var logicalAnd = logicalOr.logicalAndExpression(0);
        if (logicalAnd.bitwiseOrExpression().Length != 1)
        {
            return false;
        }

        var bitwiseOr = logicalAnd.bitwiseOrExpression(0);
        if (bitwiseOr.bitwiseXorExpression().Length != 1)
        {
            return false;
        }

        var bitwiseXor = bitwiseOr.bitwiseXorExpression(0);
        if (bitwiseXor.bitwiseAndExpression().Length != 1)
        {
            return false;
        }

        var bitwiseAnd = bitwiseXor.bitwiseAndExpression(0);
        if (bitwiseAnd.equalityExpression().Length != 1)
        {
            return false;
        }

        var equality = bitwiseAnd.equalityExpression(0);
        if (equality.relationalExpression().Length != 1)
        {
            return false;
        }

        var relational = equality.relationalExpression(0);
        if (relational.shiftExpression().Length != 1)
        {
            return false;
        }

        var shift = relational.shiftExpression(0);
        if (shift.additiveExpression().Length != 1)
        {
            return false;
        }

        var additive = shift.additiveExpression(0);
        if (additive.multiplicativeExpression().Length != 1)
        {
            return false;
        }

        var multiplicative = additive.multiplicativeExpression(0);
        if (multiplicative.unaryExpression().Length != 1)
        {
            return false;
        }

        var unary = multiplicative.unaryExpression(0);
        if (unary.powerExpression() is not { } powerExpression
            || powerExpression.unaryExpression() is not null
            || powerExpression.postfixExpression() is not { } postfixExpression
            || postfixExpression.postfixPart().Length != 0
            || postfixExpression.primaryExpression() is not { } primary)
        {
            return false;
        }

        primaryExpression = primary;
        return true;
    }

    private bool TryPlanVariableInitializer(
        StarkParser.VariableInitializerContext initializer,
        StarkTypeSymbol targetType,
        bool isFrozen,
        out GlobalInitializerPlan plan)
    {
        plan = null!;

        if (initializer.expression() is { } expression)
        {
            return TryPlanGlobalExpression(expression, targetType, isFrozen, out plan);
        }

        if (initializer.objectInitializer() is { } objectInitializer)
        {
            return TryPlanObjectInitializer(objectInitializer, targetType, isFrozen, out plan);
        }

        if (initializer.arrayInitializer() is { } arrayInitializer)
        {
            return TryPlanArrayInitializer(arrayInitializer, targetType, isFrozen, out plan);
        }

        return false;
    }

    private bool TryPlanGlobalExpression(
        StarkParser.ExpressionContext expression,
        StarkTypeSymbol targetType,
        bool isFrozen,
        out GlobalInitializerPlan plan)
    {
        plan = null!;

        if (!TryUnwrapSimplePrimaryExpression(expression, out var primaryExpression))
        {
            return false;
        }

        if (primaryExpression.literal() is { } literal)
        {
            return TryPlanLiteralInitializer(literal, targetType, out plan);
        }

        if (primaryExpression.objectCreationExpression() is { } objectCreation)
        {
            return TryPlanObjectCreationInitializer(objectCreation, targetType, isFrozen, out plan);
        }

        if (primaryExpression.expression() is { } groupedExpression)
        {
            return TryPlanGlobalExpression(groupedExpression, targetType, isFrozen, out plan);
        }

        return false;
    }

    private bool TryPlanLiteralInitializer(
        StarkParser.LiteralContext literal,
        StarkTypeSymbol targetType,
        out GlobalInitializerPlan plan)
    {
        plan = null!;
        var rendered = string.Empty;

        if (literal.signedIntegerLiteral() is { } integerLiteral)
        {
            rendered = ParseSignedIntegerLiteral(integerLiteral).ToString();
        }
        else if (literal.FloatLiteral() is { } floatLiteral)
        {
            rendered = floatLiteral.GetText();
        }
        else if (literal.TRUE() is not null)
        {
            rendered = "true";
        }
        else if (literal.FALSE() is not null)
        {
            rendered = "false";
        }
        else if (literal.NULL() is not null)
        {
            rendered = "null";
        }
        else if (literal.StringLiteral() is { } stringLiteral)
        {
            rendered = FormatGlobalStringConstantValue(stringLiteral.GetText(), targetType);
        }
        else if (literal.CharacterLiteral() is { } characterLiteral)
        {
            rendered = FormatGlobalStringConstantValue(characterLiteral.GetText(), targetType);
        }
        else
        {
            return false;
        }

        plan = new GlobalInitializerPlan(rendered, []);
        return true;
    }

    private bool TryPlanObjectCreationInitializer(
        StarkParser.ObjectCreationExpressionContext objectCreation,
        StarkTypeSymbol targetType,
        bool isFrozen,
        out GlobalInitializerPlan plan)
    {
        plan = null!;

        var namedType = ResolveNamedTypeSymbol(targetType);
        if (namedType is null)
        {
            return false;
        }

        var preludeDefinitions = new List<string>();
        var fieldValues = new Dictionary<string, string>(StringComparer.Ordinal);
        var arguments = objectCreation.argumentList()?.argument() ?? [];

        if (arguments.Length != 0)
        {
            if (!TryGetObjectCreationConstructor(objectCreation, out var constructor)
                || constructor is null
                || !constructor.IsPrimaryShape
                || arguments.Length != constructor.Parameters.Count)
            {
                return false;
            }

            for (var index = 0; index < arguments.Length; index++)
            {
                var parameter = constructor.Parameters[index];
                if (!namedType.TryGetField(parameter.Name, out var field, out _))
                {
                    return false;
                }

                if (!TryPlanGlobalExpression(arguments[index].expression(), field.Type, isFrozen, out var argumentPlan))
                {
                    return false;
                }

                preludeDefinitions.AddRange(argumentPlan.PreludeDefinitions);
                fieldValues[field.Name] = argumentPlan.Rendered;
            }
        }

        if (objectCreation.objectInitializer() is { } objectInitializer
            && !TryCollectObjectInitializerMembers(objectInitializer, namedType, isFrozen, fieldValues, preludeDefinitions))
        {
            return false;
        }

        plan = new GlobalInitializerPlan(FormatNamedAggregateInitializer(namedType, fieldValues), preludeDefinitions);
        return true;
    }

    private bool TryGetObjectCreationConstructor(
        StarkParser.ObjectCreationExpressionContext objectCreation,
        out TypedConstructorShape? constructor)
    {
        return _objectCreationConstructors.TryGetValue(
            new ObjectCreationKey(
                objectCreation.GetText(),
                objectCreation.Start.Line,
                objectCreation.Start.Column + 1),
            out constructor);
    }

    private bool TryPlanObjectInitializer(
        StarkParser.ObjectInitializerContext objectInitializer,
        StarkTypeSymbol targetType,
        bool isFrozen,
        out GlobalInitializerPlan plan)
    {
        plan = null!;

        var namedType = ResolveNamedTypeSymbol(targetType);
        if (namedType is null)
        {
            return false;
        }

        var preludeDefinitions = new List<string>();
        var fieldValues = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!TryCollectObjectInitializerMembers(objectInitializer, namedType, isFrozen, fieldValues, preludeDefinitions))
        {
            return false;
        }

        plan = new GlobalInitializerPlan(FormatNamedAggregateInitializer(namedType, fieldValues), preludeDefinitions);
        return true;
    }

    private bool TryCollectObjectInitializerMembers(
        StarkParser.ObjectInitializerContext objectInitializer,
        NamedTypeSymbol namedType,
        bool isFrozen,
        IDictionary<string, string> fieldValues,
        ICollection<string> preludeDefinitions)
    {
        foreach (var memberInitializer in objectInitializer.memberInitializer())
        {
            var memberName = memberInitializer.Identifier().GetText();
            if (!namedType.Fields.TryGetValue(memberName, out var field))
            {
                return false;
            }

            if (!TryPlanVariableInitializer(memberInitializer.variableInitializer(), field.Type, isFrozen, out var memberPlan))
            {
                return false;
            }

            foreach (var prelude in memberPlan.PreludeDefinitions)
            {
                preludeDefinitions.Add(prelude);
            }

            fieldValues[memberName] = memberPlan.Rendered;
        }

        return true;
    }

    private string FormatNamedAggregateInitializer(
        NamedTypeSymbol namedType,
        IReadOnlyDictionary<string, string> fieldValues)
    {
        var fieldInitializers = namedType.OrderedFields
            .Select(field => $"{MapType(field.Type)} {(fieldValues.TryGetValue(field.Name, out var value) ? value : FormatZeroInitializer(field.Type))}");
        return $"{{ {string.Join(", ", fieldInitializers)} }}";
    }

    private bool TryPlanArrayInitializer(
        StarkParser.ArrayInitializerContext arrayInitializer,
        StarkTypeSymbol targetType,
        bool isFrozen,
        out GlobalInitializerPlan plan)
    {
        plan = null!;

        if (targetType.Kind != StarkTypeKind.FixedArray
            || targetType.ElementType is null
            || targetType.FixedLength is not int fixedLength
            || arrayInitializer.variableInitializer().Length != fixedLength)
        {
            return false;
        }

        var preludeDefinitionsForArray = new List<string>();
        var elements = new List<string>(fixedLength);
        foreach (var initializer in arrayInitializer.variableInitializer())
        {
            if (!TryPlanVariableInitializer(initializer, targetType.ElementType, isFrozen, out var elementPlan))
            {
                return false;
            }

            preludeDefinitionsForArray.AddRange(elementPlan.PreludeDefinitions);
            elements.Add($"{MapType(targetType.ElementType)} {elementPlan.Rendered}");
        }

        plan = new GlobalInitializerPlan($"[{string.Join(", ", elements)}]", preludeDefinitionsForArray);
        return true;
    }

    private string FormatZeroInitializer(StarkTypeSymbol type)
    {
        return type.Kind switch
        {
            StarkTypeKind.Integer => "0",
            StarkTypeKind.Float => "0.0",
            StarkTypeKind.Bool => "false",
            StarkTypeKind.RawPointer => "null",
            StarkTypeKind.Ascii or StarkTypeKind.Unicode or StarkTypeKind.FixedArray or StarkTypeKind.Slice or StarkTypeKind.Named => "zeroinitializer",
            _ => "zeroinitializer"
        };
    }

    private bool ShouldEmitExternalConstPlaceholder(TypedGlobalSymbol global, StarkParser.VariableInitializerContext initializer)
    {
        return global.IsConst
            && global.Type.Kind == StarkTypeKind.RawPointer
            && initializer.expression() is { } expression
            && TryUnwrapSimplePrimaryExpression(expression, out var primaryExpression)
            && primaryExpression.literal()?.NULL() is not null;
    }

    private void EmitGlobalInitializerPrelude(StringBuilder builder, GlobalInitializerPlan plan)
    {
        foreach (var prelude in plan.PreludeDefinitions)
        {
            builder.AppendLine(prelude);
            builder.AppendLine();
        }
    }

    private string AllocateSyntheticGlobalInitializerSymbol(string kind)
    {
        return $".global_init_{kind}_{_syntheticGlobalInitializerIndex++}";
    }

    private string BuildSyntheticGlobalDefinition(
        string symbolName,
        bool isConstant,
        bool unnamedAddr,
        string llvmType,
        string initializer)
    {
        var segments = new List<string> { $"@{EscapeIdentifier(symbolName)}", "=", "private" };

        if (unnamedAddr)
        {
            segments.Add("unnamed_addr");
        }

        segments.Add(isConstant ? "constant" : "global");
        segments.Add(llvmType);
        segments.Add(initializer);
        return string.Join(" ", segments);
    }

    private string FormatGlobalStringConstantValue(string literalText, StarkTypeSymbol targetType)
    {
        var pointer = FormatStringDataPointer(literalText, targetType);
        var constant = _stringConstants[CreateStringConstantKey(literalText, targetType)];
        return $"{{ ptr {pointer}, i64 {constant.DataLength} }}";
    }

    private string FormatStringDataPointer(string literalText, StarkTypeSymbol type)
    {
        if (!_stringConstants.TryGetValue(CreateStringConstantKey(literalText, type), out var constant))
        {
            throw new InvalidOperationException($"Missing string constant for literal '{literalText}' with type '{type.DisplayName}'.");
        }

        return $"getelementptr inbounds ({constant.ArrayType}, ptr @{constant.SymbolName}, i32 0, i32 0)";
    }

    private static StarkVisibility ParseVisibility(StarkParser.VisibilityModifierContext? visibilityModifier)
    {
        return visibilityModifier?.GetText() switch
        {
            "internal" => StarkVisibility.Internal,
            "public" => StarkVisibility.Public,
            "export" => StarkVisibility.Export,
            _ => StarkVisibility.Module
        };
    }

    private static BigInteger ParseSignedIntegerLiteral(StarkParser.SignedIntegerLiteralContext literal)
    {
        var text = literal.GetText().Replace("_", string.Empty, StringComparison.Ordinal);
        return BigInteger.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void AddStringLiteral(string literalText, Dictionary<StringConstantKey, EmittedStringConstant> constants, ref int index)
    {
        var kind = GetTextLiteralKind(literalText);
        if (TextLiteralDecoder.CanUseUtf8Storage(literalText, kind))
        {
            AddStringLiteral(literalText, StarkTypeSymbols.Ascii, constants, ref index);
        }

        AddStringLiteral(literalText, StarkTypeSymbols.Unicode, constants, ref index);
    }

    private static void AddStringLiteral(
        string literalText,
        StarkTypeSymbol type,
        Dictionary<StringConstantKey, EmittedStringConstant> constants,
        ref int index)
    {
        var key = CreateStringConstantKey(literalText, type);
        if (constants.ContainsKey(key))
        {
            return;
        }

        switch (type.Kind)
        {
            case StarkTypeKind.Ascii:
            {
                var bytes = DecodeAsciiStringLiteral(literalText);
                var terminated = new byte[bytes.Length + 1];
                bytes.CopyTo(terminated, 0);

                constants[key] = new EmittedStringConstant(
                    SymbolName: $".str.{index++}",
                    ArrayType: $"[{terminated.Length} x i8]",
                    Initializer: EncodeLlvmByteString(terminated),
                    DataLength: bytes.Length);
                return;
            }
            case StarkTypeKind.Unicode:
            {
                var codeUnits = DecodeUnicodeStringLiteral(literalText);
                var terminated = new int[codeUnits.Length + 1];
                codeUnits.CopyTo(terminated, 0);

                constants[key] = new EmittedStringConstant(
                    SymbolName: $".str.{index++}",
                    ArrayType: $"[{terminated.Length} x i32]",
                    Initializer: EncodeLlvmI32Array(terminated),
                    DataLength: codeUnits.Length);
                return;
            }
            default:
                throw new InvalidOperationException($"String constants require an ascii/unicode type, but found '{type.DisplayName}'.");
        }
    }

    private static byte[] DecodeAsciiStringLiteral(string literalText)
    {
        var kind = GetTextLiteralKind(literalText);
        return TextLiteralDecoder.DecodeUtf8BytesOrFallback(literalText, kind);
    }

    private static int[] DecodeUnicodeStringLiteral(string literalText)
    {
        var kind = GetTextLiteralKind(literalText);
        return TextLiteralDecoder.DecodeUtf32CodeUnitsOrFallback(literalText, kind);
    }

    private static TextLiteralKind GetTextLiteralKind(string literalText)
    {
        return literalText.StartsWith('\'')
            ? TextLiteralKind.Character
            : TextLiteralKind.String;
    }

    private static string EncodeLlvmByteString(byte[] bytes)
    {
        var builder = new StringBuilder();
        builder.Append("c\"");
        foreach (var value in bytes)
        {
            if (value >= 0x20 && value <= 0x7E && value is not (byte)'\\' and not (byte)'"')
            {
                builder.Append((char)value);
            }
            else
            {
                builder.Append('\\');
                builder.Append(value.ToString("X2"));
            }
        }

        builder.Append('"');
        return builder.ToString();
    }

    private static string EncodeLlvmI32Array(int[] values)
    {
        return $"[{string.Join(", ", values.Select(static value => $"i32 {value}"))}]";
    }

    private ConcreteTypeLayout? TryGetConcreteTypeLayout(StarkTypeSymbol type)
    {
        return ConcreteTypeLayoutHelper.TryGetConcreteTypeLayout(type, _typeModel.NamedTypes, _enumLayoutModel.Layouts);
    }

    private NamedTypeSymbol? ResolveNamedTypeSymbol(StarkTypeSymbol type)
    {
        return type.NamedType is not null && _typeModel.NamedTypes.TryGetValue(type.NamedType, out var namedType)
            ? namedType
            : null;
    }

    private sealed class FunctionBodyEmitter
    {
        private readonly StringBuilder _builder;
        private readonly TypedFunctionSignature _function;
        private readonly AbiFunctionSignature _abiFunction;
        private readonly Func<string, string, AbiFunctionSignature?> _resolveCallAbi;
        private readonly SsaFunction _ssaFunction;
        private readonly IReadOnlyDictionary<StringConstantKey, EmittedStringConstant> _stringConstants;
        private readonly Func<string, string> _mapGlobalSymbolName;
        private readonly Func<StarkTypeSymbol, string> _mapType;
        private readonly Func<StarkTypeSymbol, ConcreteTypeLayout?> _tryGetConcreteTypeLayout;
        private readonly HashSet<string> _allocatedLocalSlots = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _materializedParameters = new(StringComparer.Ordinal);
        private int _nextAbiTempId;

        public FunctionBodyEmitter(
            StringBuilder builder,
            TypedFunctionSignature function,
            AbiFunctionSignature abiFunction,
            Func<string, string, AbiFunctionSignature?> resolveCallAbi,
            SsaFunction ssaFunction,
            IReadOnlyDictionary<StringConstantKey, EmittedStringConstant> stringConstants,
            Func<string, string> mapGlobalSymbolName,
            Func<StarkTypeSymbol, string> mapType,
            Func<StarkTypeSymbol, ConcreteTypeLayout?> tryGetConcreteTypeLayout)
        {
            _builder = builder;
            _function = function;
            _abiFunction = abiFunction;
            _resolveCallAbi = resolveCallAbi;
            _ssaFunction = ssaFunction;
            _stringConstants = stringConstants;
            _mapGlobalSymbolName = mapGlobalSymbolName;
            _mapType = mapType;
            _tryGetConcreteTypeLayout = tryGetConcreteTypeLayout;
        }

        public void Emit()
        {
            if (_ssaFunction.Blocks.Count == 0)
            {
                EmitFallbackTerminal();
                return;
            }

            foreach (var block in _ssaFunction.Blocks)
            {
                AppendLine($"{FormatBlockLabel(block.Id)}:");

                if (block.Id == _ssaFunction.EntryBlockId)
                {
                    EmitEntryParameterMaterialization();
                }

                foreach (var phi in block.Phis)
                {
                    EmitPhi(phi);
                }

                foreach (var instruction in block.Instructions)
                {
                    EmitInstruction(instruction);
                }

                EmitTerminator(block.Terminator);
                AppendLine(string.Empty);
            }
        }

        private void EmitPhi(SsaPhi phi)
        {
            var incoming = string.Join(
                ", ",
                phi.Incomings.Select(entry => $"[ {FormatValue(entry.Value)}, %{FormatBlockLabel(entry.PredecessorBlockId)} ]"));
            AppendLine($"  %{EscapeIdentifier(phi.ResultName)} = phi {MapType(phi.Type)} {incoming}");
        }

        private void EmitInstruction(SsaInstruction instruction)
        {
            switch (instruction)
            {
                case SsaValueInstruction valueInstruction:
                    EmitValueInstruction(valueInstruction);
                    return;
                case SsaAllocateLocalInstruction allocateLocal:
                    EmitAllocateLocal(allocateLocal);
                    return;
                case SsaLifetimeStartInstruction lifetimeStart:
                    EmitLifetimeStart(lifetimeStart);
                    return;
                case SsaLifetimeEndInstruction lifetimeEnd:
                    EmitLifetimeEnd(lifetimeEnd);
                    return;
                case SsaStoreLocalInstruction storeLocal:
                    EmitStoreLocal(storeLocal);
                    return;
                case SsaCopyMemoryInstruction copyMemory:
                    EmitCopyMemory(copyMemory);
                    return;
                case SsaStoreIndirectInstruction storeIndirect:
                    EmitStoreIndirect(storeIndirect);
                    return;
                case SsaStoreGlobalInstruction storeGlobal:
                    AppendLine(
                        $"  store {MapType(storeGlobal.GlobalType)} {FormatValue(storeGlobal.Value)}, ptr @{EscapeIdentifier(_mapGlobalSymbolName(storeGlobal.GlobalName))}");
                    return;
                default:
                    throw new UnsupportedBodyEmissionException($"Unsupported SSA instruction '{instruction.GetType().Name}'.");
            }
        }

        private void EmitValueInstruction(SsaValueInstruction instruction)
        {
            var result = $"%{EscapeIdentifier(instruction.ResultName)}";
            switch (instruction.Value)
            {
                case SsaUseRValue use:
                    AppendLine($"  {result} = add {MapType(use.Type)} {FormatValue(use.Value)}, 0");
                    return;
                case SsaLoadGlobalRValue load:
                    AppendLine($"  {result} = load {MapType(load.Type)}, ptr @{EscapeIdentifier(_mapGlobalSymbolName(load.GlobalName))}");
                    return;
                case SsaLoadLocalRValue loadLocal:
                    EnsureLocalSlotExists(loadLocal.LocalName, loadLocal.Type);
                    AppendLine($"  {result} = load {MapType(loadLocal.Type)}, ptr %{EscapeIdentifier($"slot_{loadLocal.LocalName}")}");
                    return;
                case SsaConvertRValue convert:
                    EmitConvert(result, convert);
                    return;
                case SsaExtractFieldRValue extract:
                    AppendLine($"  {result} = extractvalue {MapType(extract.Target.Type)} {FormatValue(extract.Target)}, {extract.FieldIndex}");
                    return;
                case SsaInsertFieldRValue insert:
                    AppendLine($"  {result} = insertvalue {MapType(insert.Target.Type)} {FormatValue(insert.Target)}, {MapType(insert.Value.Type)} {FormatValue(insert.Value)}, {insert.FieldIndex}");
                    return;
                case SsaExtractIndexRValue extractIndex:
                    AppendLine($"  {result} = extractvalue {MapType(extractIndex.Target.Type)} {FormatValue(extractIndex.Target)}, {extractIndex.ElementIndex}");
                    return;
                case SsaInsertIndexRValue insertIndex:
                    AppendLine($"  {result} = insertvalue {MapType(insertIndex.Target.Type)} {FormatValue(insertIndex.Target)}, {MapType(insertIndex.Value.Type)} {FormatValue(insertIndex.Value)}, {insertIndex.ElementIndex}");
                    return;
                case SsaMakeSliceFromLocalRValue makeSlice:
                    EmitMakeSliceFromLocal(result, makeSlice);
                    return;
                case SsaLoadSliceElementRValue loadSlice:
                    EmitLoadSliceElement(result, loadSlice);
                    return;
                case SsaTextSliceRValue textSlice:
                    EmitTextSlice(result, textSlice);
                    return;
                case SsaAddressOfLocalRValue addressOfLocal:
                    EmitAddressOfLocal(result, addressOfLocal);
                    return;
                case SsaAddressOfParameterRValue addressOfParameter:
                    EmitAddressOfParameter(result, addressOfParameter);
                    return;
                case SsaFieldAddressRValue fieldAddress:
                    EmitFieldAddress(result, fieldAddress);
                    return;
                case SsaElementAddressRValue elementAddress:
                    EmitElementAddress(result, elementAddress);
                    return;
                case SsaSliceElementAddressRValue sliceElementAddress:
                    EmitSliceElementAddress(result, sliceElementAddress);
                    return;
                case SsaLoadIndirectRValue loadIndirect:
                    AppendLine($"  {result} = load {MapType(loadIndirect.Type)}, ptr {FormatValue(loadIndirect.Address)}");
                    return;
                case SsaUnaryRValue unary:
                    EmitUnary(result, unary);
                    return;
                case SsaBinaryRValue binary:
                    EmitBinary(result, binary);
                    return;
                case SsaCallRValue call:
                    EmitCall(result, call);
                    return;
                default:
                    throw new UnsupportedBodyEmissionException($"Unsupported SSA rvalue '{instruction.Value.GetType().Name}'.");
            }
        }

        private void EmitConvert(string result, SsaConvertRValue convert)
        {
            var sourceType = convert.Operand.Type;
            var targetType = convert.TargetType;

            if (sourceType.Kind == StarkTypeKind.Integer && targetType.Kind == StarkTypeKind.Integer)
            {
                if (sourceType.BitWidth == targetType.BitWidth)
                {
                    AppendLine($"  {result} = add {MapType(targetType)} {FormatValue(convert.Operand)}, 0");
                    return;
                }

                var opcode = sourceType.BitWidth < targetType.BitWidth ? "sext" : "trunc";
                AppendLine($"  {result} = {opcode} {MapType(sourceType)} {FormatValue(convert.Operand)} to {MapType(targetType)}");
                return;
            }

            if (sourceType.Kind == StarkTypeKind.Integer && targetType.Kind == StarkTypeKind.Float)
            {
                AppendLine($"  {result} = sitofp {MapType(sourceType)} {FormatValue(convert.Operand)} to {MapType(targetType)}");
                return;
            }

            if (sourceType.Kind == StarkTypeKind.Float && targetType.Kind == StarkTypeKind.Integer)
            {
                AppendLine($"  {result} = fptosi {MapType(sourceType)} {FormatValue(convert.Operand)} to {MapType(targetType)}");
                return;
            }

            if (sourceType.Kind == StarkTypeKind.Float && targetType.Kind == StarkTypeKind.Float)
            {
                if (sourceType.BitWidth == targetType.BitWidth)
                {
                    AppendLine($"  {result} = fadd {MapType(targetType)} {FormatValue(convert.Operand)}, 0.0");
                    return;
                }

                var opcode = sourceType.BitWidth < targetType.BitWidth ? "fpext" : "fptrunc";
                AppendLine($"  {result} = {opcode} {MapType(sourceType)} {FormatValue(convert.Operand)} to {MapType(targetType)}");
                return;
            }

            if (sourceType.Kind == StarkTypeKind.Integer && targetType.Kind == StarkTypeKind.RawPointer)
            {
                AppendLine($"  {result} = inttoptr {MapType(sourceType)} {FormatValue(convert.Operand)} to ptr");
                return;
            }

            if (sourceType.Kind == StarkTypeKind.RawPointer && targetType.Kind == StarkTypeKind.RawPointer)
            {
                AppendLine($"  {result} = getelementptr inbounds i8, ptr {FormatValue(convert.Operand)}, i64 0");
                return;
            }

            if (sourceType.Kind == StarkTypeKind.RawPointer && targetType.Kind == StarkTypeKind.Integer)
            {
                AppendLine($"  {result} = ptrtoint ptr {FormatValue(convert.Operand)} to {MapType(targetType)}");
                return;
            }

            throw new UnsupportedBodyEmissionException(
                $"Unsupported SSA conversion from '{sourceType.DisplayName}' to '{targetType.DisplayName}'.");
        }

        private void EmitUnary(string result, SsaUnaryRValue unary)
        {
            switch (unary.Operator)
            {
                case SsaUnaryOperator.Negate when unary.Type.Kind == StarkTypeKind.Integer:
                    AppendLine($"  {result} = sub {MapType(unary.Type)} 0, {FormatValue(unary.Operand)}");
                    return;
                case SsaUnaryOperator.Negate when unary.Type.Kind == StarkTypeKind.Float:
                    AppendLine($"  {result} = fneg {MapType(unary.Type)} {FormatValue(unary.Operand)}");
                    return;
                case SsaUnaryOperator.LogicalNot:
                    AppendLine($"  {result} = xor i1 {FormatValue(unary.Operand)}, true");
                    return;
                case SsaUnaryOperator.BitwiseNot:
                    AppendLine($"  {result} = xor {MapType(unary.Type)} {FormatValue(unary.Operand)}, -1");
                    return;
                default:
                    throw new UnsupportedBodyEmissionException($"Unsupported SSA unary operator '{unary.Operator}'.");
            }
        }

        private void EmitBinary(string result, SsaBinaryRValue binary)
        {
            if (binary.Type.Kind == StarkTypeKind.Integer)
            {
                if (binary.Operator is SsaBinaryOperator.SaturatingAdd or SsaBinaryOperator.SaturatingSubtract or SsaBinaryOperator.SaturatingMultiply)
                {
                    EmitSaturatingIntegerBinary(result, binary);
                    return;
                }

                var opcode = binary.Operator switch
                {
                    SsaBinaryOperator.Add => "add",
                    SsaBinaryOperator.Subtract => "sub",
                    SsaBinaryOperator.Multiply => "mul",
                    SsaBinaryOperator.WrappingAdd => "add",
                    SsaBinaryOperator.WrappingSubtract => "sub",
                    SsaBinaryOperator.WrappingMultiply => "mul",
                    SsaBinaryOperator.Divide => "sdiv",
                    SsaBinaryOperator.Modulo => "srem",
                    SsaBinaryOperator.BitwiseAnd => "and",
                    SsaBinaryOperator.BitwiseXor => "xor",
                    SsaBinaryOperator.BitwiseOr => "or",
                    SsaBinaryOperator.ShiftLeft => "shl",
                    SsaBinaryOperator.ShiftRight => "ashr",
                    _ => string.Empty
                };

                if (!string.IsNullOrEmpty(opcode))
                {
                    AppendLine($"  {result} = {opcode} {MapType(binary.Left.Type)} {FormatValue(binary.Left)}, {FormatValue(binary.Right)}");
                    return;
                }
            }

            if (binary.Type.Kind == StarkTypeKind.Float)
            {
                var opcode = binary.Operator switch
                {
                    SsaBinaryOperator.Add => "fadd",
                    SsaBinaryOperator.Subtract => "fsub",
                    SsaBinaryOperator.Multiply => "fmul",
                    SsaBinaryOperator.Divide => "fdiv",
                    SsaBinaryOperator.Modulo => "frem",
                    _ => string.Empty
                };

                if (!string.IsNullOrEmpty(opcode))
                {
                    AppendLine($"  {result} = {opcode} {MapType(binary.Left.Type)} {FormatValue(binary.Left)}, {FormatValue(binary.Right)}");
                    return;
                }

                if (binary.Operator == SsaBinaryOperator.Exponent)
                {
                    EmitFloatExponent(result, binary);
                    return;
                }
            }

            if (binary.Type.Kind == StarkTypeKind.Bool)
            {
                if (binary.Left.Type.Kind == StarkTypeKind.Integer || binary.Left.Type.Kind == StarkTypeKind.Bool)
                {
                    var predicate = binary.Operator switch
                    {
                        SsaBinaryOperator.Equal => "eq",
                        SsaBinaryOperator.NotEqual => "ne",
                        SsaBinaryOperator.LessThan => "slt",
                        SsaBinaryOperator.LessThanOrEqual => "sle",
                        SsaBinaryOperator.GreaterThan => "sgt",
                        SsaBinaryOperator.GreaterThanOrEqual => "sge",
                        _ => string.Empty
                    };

                    if (!string.IsNullOrEmpty(predicate))
                    {
                        AppendLine($"  {result} = icmp {predicate} {MapType(binary.Left.Type)} {FormatValue(binary.Left)}, {FormatValue(binary.Right)}");
                        return;
                    }
                }

                if (binary.Left.Type.Kind == StarkTypeKind.Float)
                {
                    var predicate = binary.Operator switch
                    {
                        SsaBinaryOperator.Equal => "oeq",
                        SsaBinaryOperator.NotEqual => "one",
                        SsaBinaryOperator.LessThan => "olt",
                        SsaBinaryOperator.LessThanOrEqual => "ole",
                        SsaBinaryOperator.GreaterThan => "ogt",
                        SsaBinaryOperator.GreaterThanOrEqual => "oge",
                        _ => string.Empty
                    };

                    if (!string.IsNullOrEmpty(predicate))
                    {
                        AppendLine($"  {result} = fcmp {predicate} {MapType(binary.Left.Type)} {FormatValue(binary.Left)}, {FormatValue(binary.Right)}");
                        return;
                    }
                }

                if (binary.Left.Type.Kind == StarkTypeKind.RawPointer)
                {
                    var predicate = binary.Operator switch
                    {
                        SsaBinaryOperator.Equal => "eq",
                        SsaBinaryOperator.NotEqual => "ne",
                        _ => string.Empty
                    };

                    if (!string.IsNullOrEmpty(predicate))
                    {
                        AppendLine($"  {result} = icmp {predicate} ptr {FormatValue(binary.Left)}, {FormatValue(binary.Right)}");
                        return;
                    }
                }
            }

            throw new UnsupportedBodyEmissionException(
                $"Unsupported SSA binary operator '{binary.Operator}' for '{binary.Left.Type.DisplayName}'.");
        }

        private void EmitSaturatingIntegerBinary(string result, SsaBinaryRValue binary)
        {
            if (binary.Type.BitWidth is not int bitWidth || bitWidth <= 0)
            {
                throw new UnsupportedBodyEmissionException($"Saturating integer operator '{binary.Operator}' requires a concrete integer bit width.");
            }

            var narrowType = MapType(binary.Type);
            var wideTypeSymbol = StarkTypeSymbols.Integer(bitWidth * 2);
            var wideType = MapType(wideTypeSymbol);
            var wideOpcode = binary.Operator switch
            {
                SsaBinaryOperator.SaturatingAdd => "add",
                SsaBinaryOperator.SaturatingSubtract => "sub",
                SsaBinaryOperator.SaturatingMultiply => "mul",
                _ => throw new UnsupportedBodyEmissionException($"Unsupported saturating integer operator '{binary.Operator}'.")
            };

            var leftWide = $"%{EscapeIdentifier(CreateAbiTempName("sat_left"))}";
            var rightWide = $"%{EscapeIdentifier(CreateAbiTempName("sat_right"))}";
            var valueWide = $"%{EscapeIdentifier(CreateAbiTempName("sat_value"))}";
            var aboveMax = $"%{EscapeIdentifier(CreateAbiTempName("sat_above"))}";
            var belowMin = $"%{EscapeIdentifier(CreateAbiTempName("sat_below"))}";
            var clampHigh = $"%{EscapeIdentifier(CreateAbiTempName("sat_clamp_high"))}";
            var clamped = $"%{EscapeIdentifier(CreateAbiTempName("sat_clamped"))}";

            GetSignedIntegerBounds(bitWidth, out var minValue, out var maxValue);

            AppendLine($"  {leftWide} = sext {narrowType} {FormatValue(binary.Left)} to {wideType}");
            AppendLine($"  {rightWide} = sext {narrowType} {FormatValue(binary.Right)} to {wideType}");
            AppendLine($"  {valueWide} = {wideOpcode} {wideType} {leftWide}, {rightWide}");
            AppendLine($"  {aboveMax} = icmp sgt {wideType} {valueWide}, {maxValue}");
            AppendLine($"  {belowMin} = icmp slt {wideType} {valueWide}, {minValue}");
            AppendLine($"  {clampHigh} = select i1 {aboveMax}, {wideType} {maxValue}, {wideType} {valueWide}");
            AppendLine($"  {clamped} = select i1 {belowMin}, {wideType} {minValue}, {wideType} {clampHigh}");
            AppendLine($"  {result} = trunc {wideType} {clamped} to {narrowType}");
        }

        private void EmitFloatExponent(string result, SsaBinaryRValue binary)
        {
            var llvmType = MapType(binary.Left.Type);
            var intrinsicName = $"@llvm.pow.{LlvmIrEmitter.GetFloatIntrinsicSuffix(binary.Left.Type)}";
            AppendLine($"  {result} = call {llvmType} {intrinsicName}({llvmType} {FormatValue(binary.Left)}, {llvmType} {FormatValue(binary.Right)})");
        }

        private static void GetSignedIntegerBounds(int bitWidth, out BigInteger minValue, out BigInteger maxValue)
        {
            minValue = -(BigInteger.One << (bitWidth - 1));
            maxValue = (BigInteger.One << (bitWidth - 1)) - 1;
        }

        private void EmitCall(string result, SsaCallRValue call)
        {
            var abiCallee = _resolveCallAbi(_function.Name, call.FunctionName);
            if (abiCallee is null)
            {
                throw new UnsupportedBodyEmissionException($"Missing ABI lowering for call target '{call.FunctionName}'.");
            }

            if (IsStringType(call.Type) && abiCallee.LlvmReturnType.Kind == StarkTypeKind.RawPointer)
            {
                throw new UnsupportedBodyEmissionException(
                    $"FFI string returns are not yet supported for '{call.FunctionName}'.");
            }

            var arguments = new List<string>();
            string? indirectReturnSlot = null;

            if (abiCallee.ReturnsIndirect)
            {
                indirectReturnSlot = $"%{EscapeIdentifier(CreateAbiTempName("callret_slot"))}";
                AppendLine($"  {indirectReturnSlot} = alloca {MapType(call.Type)}");
                arguments.Add($"ptr sret({MapType(call.Type)}) {indirectReturnSlot}");
            }

            var userParameters = abiCallee.UserParameters;
            if (userParameters.Count != call.Arguments.Count)
            {
                throw new UnsupportedBodyEmissionException(
                    $"ABI parameter count mismatch for '{call.FunctionName}': expected {userParameters.Count}, got {call.Arguments.Count}.");
            }

            for (var index = 0; index < userParameters.Count; index++)
            {
                var parameter = userParameters[index];
                var argument = call.Arguments[index];

                if (parameter.Kind == AbiParameterKind.Direct)
                {
                    arguments.Add(RenderDirectArgument(parameter, argument));
                    continue;
                }

                var promotedLocal = call.IndirectArgumentLocalNames is not null && index < call.IndirectArgumentLocalNames.Count
                    ? call.IndirectArgumentLocalNames[index]
                    : null;
                if (!string.IsNullOrWhiteSpace(promotedLocal))
                {
                    var promotedParameter = _abiFunction.UserParameters.FirstOrDefault(
                        candidate => string.Equals(candidate.SourceName, promotedLocal, StringComparison.Ordinal));
                    if (promotedParameter is not null)
                    {
                        if (promotedParameter.Kind == AbiParameterKind.IndirectIn)
                        {
                            arguments.Add($"ptr %{EscapeIdentifier(promotedParameter.LlvmName)}");
                        }
                        else
                        {
                            EnsureParameterSlotExists(promotedParameter, promotedParameter.SourceType);
                            arguments.Add($"ptr %{EscapeIdentifier($"slot_param_{promotedParameter.SourceName}")}");
                        }

                        continue;
                    }

                    EnsureLocalSlotExists(promotedLocal!, parameter.SourceType);
                    arguments.Add($"ptr %{EscapeIdentifier($"slot_{promotedLocal}")}");
                    continue;
                }

                var tempSlot = $"%{EscapeIdentifier(CreateAbiTempName($"callarg_{parameter.SourceName}"))}";
                AppendLine($"  {tempSlot} = alloca {MapType(parameter.SourceType)}");
                AppendLine($"  store {MapType(parameter.SourceType)} {FormatValue(argument)}, ptr {tempSlot}");
                arguments.Add($"ptr {tempSlot}");
            }

            var renderedArguments = string.Join(", ", arguments);

            if (abiCallee.ReturnsIndirect)
            {
                AppendLine($"  call void @{EscapeIdentifier(abiCallee.SymbolName)}({renderedArguments})");
                AppendLine($"  {result} = load {MapType(call.Type)}, ptr {indirectReturnSlot}");
                return;
            }

            if (call.Type.Kind == StarkTypeKind.Void)
            {
                AppendLine($"  call void @{EscapeIdentifier(abiCallee.SymbolName)}({renderedArguments})");
                return;
            }

            AppendLine($"  {result} = call {MapType(abiCallee.LlvmReturnType)} @{EscapeIdentifier(abiCallee.SymbolName)}({renderedArguments})");
        }

        private void EmitAllocateLocal(SsaAllocateLocalInstruction allocateLocal)
        {
            if (allocateLocal.StorageClass is not "stack")
            {
                throw new UnsupportedBodyEmissionException(
                    $"Local storage class '{allocateLocal.StorageClass}' is not yet supported for LLVM body emission.");
            }

            var slotName = EscapeIdentifier($"slot_{allocateLocal.LocalName}");
            if (_allocatedLocalSlots.Add(slotName))
            {
                AppendLine($"  %{slotName} = alloca {MapType(allocateLocal.LocalType)}");
            }
        }

        private void EmitLifetimeStart(SsaLifetimeStartInstruction lifetimeStart)
        {
            EmitLifetimeMarker("start", lifetimeStart.LocalName, lifetimeStart.LocalType);
        }

        private void EmitLifetimeEnd(SsaLifetimeEndInstruction lifetimeEnd)
        {
            EmitLifetimeMarker("end", lifetimeEnd.LocalName, lifetimeEnd.LocalType);
        }

        private void EmitLifetimeMarker(string phase, string localName, StarkTypeSymbol localType)
        {
            if (_tryGetConcreteTypeLayout(localType) is not { } layout)
            {
                return;
            }

            EnsureLocalSlotExists(localName, localType);
            AppendLine($"  call void @llvm.lifetime.{phase}.p0(i64 {layout.SizeBytes}, ptr %{EscapeIdentifier($"slot_{localName}")})");
        }

        private void EmitStoreLocal(SsaStoreLocalInstruction storeLocal)
        {
            EnsureLocalSlotExists(storeLocal.LocalName, storeLocal.LocalType);
            AppendLine($"  store {MapType(storeLocal.LocalType)} {FormatValue(storeLocal.Value)}, ptr %{EscapeIdentifier($"slot_{storeLocal.LocalName}")}");
        }

        private void EmitCopyMemory(SsaCopyMemoryInstruction copyMemory)
        {
            if (_tryGetConcreteTypeLayout(copyMemory.CopyType) is { } layout
                && layout.SizeBytes > AggregateMemcpyThresholdBytes)
            {
                AppendLine(
                    $"  call void @llvm.memcpy.p0.p0.i64(ptr {FormatValue(copyMemory.DestinationAddress)}, ptr {FormatValue(copyMemory.SourceAddress)}, i64 {layout.SizeBytes}, i1 false)");
                return;
            }

            var loadedValue = $"%{EscapeIdentifier(CreateAbiTempName("copy_load"))}";
            AppendLine($"  {loadedValue} = load {MapType(copyMemory.CopyType)}, ptr {FormatValue(copyMemory.SourceAddress)}");
            AppendLine($"  store {MapType(copyMemory.CopyType)} {loadedValue}, ptr {FormatValue(copyMemory.DestinationAddress)}");
        }

        private void EmitStoreIndirect(SsaStoreIndirectInstruction storeIndirect)
        {
            AppendLine($"  store {MapType(storeIndirect.ValueType)} {FormatValue(storeIndirect.Value)}, ptr {FormatValue(storeIndirect.Address)}");
        }

        private void EmitMakeSliceFromLocal(string result, SsaMakeSliceFromLocalRValue makeSlice)
        {
            EnsureLocalSlotExists(makeSlice.LocalName, makeSlice.SourceType);

            if (makeSlice.SourceType.Kind != StarkTypeKind.FixedArray
                || makeSlice.SourceType.ElementType is null
                || makeSlice.SourceType.FixedLength is not int fixedLength)
            {
                throw new UnsupportedBodyEmissionException($"Slice creation from '{makeSlice.SourceType.DisplayName}' is not supported.");
            }

            var slotName = $"%{EscapeIdentifier($"slot_{makeSlice.LocalName}")}";
            var elementPointer = $"%{EscapeIdentifier($"{result.TrimStart('%')}_data")}";
            var withPointer = $"%{EscapeIdentifier($"{result.TrimStart('%')}_p0")}";

            AppendLine($"  {elementPointer} = getelementptr inbounds {MapType(makeSlice.SourceType)}, ptr {slotName}, i32 0, i32 0");
            AppendLine($"  {withPointer} = insertvalue {MapType(makeSlice.Type)} zeroinitializer, ptr {elementPointer}, 0");
            AppendLine($"  {result} = insertvalue {MapType(makeSlice.Type)} {withPointer}, i64 {fixedLength}, 1");
        }

        private void EmitLoadSliceElement(string result, SsaLoadSliceElementRValue loadSlice)
        {
            var dataPointer = $"%{EscapeIdentifier($"{result.TrimStart('%')}_data")}";
            var elementPointer = $"%{EscapeIdentifier($"{result.TrimStart('%')}_ptr")}";

            AppendLine($"  {dataPointer} = extractvalue {MapType(loadSlice.Slice.Type)} {FormatValue(loadSlice.Slice)}, 0");
            AppendLine($"  {elementPointer} = getelementptr inbounds {MapType(loadSlice.Type)}, ptr {dataPointer}, {MapType(loadSlice.Index.Type)} {FormatValue(loadSlice.Index)}");
            AppendLine($"  {result} = load {MapType(loadSlice.Type)}, ptr {elementPointer}");
        }

        private void EmitTextSlice(string result, SsaTextSliceRValue textSlice)
        {
            var dataPointer = $"%{EscapeIdentifier($"{result.TrimStart('%')}_data")}";
            var slicedPointer = $"%{EscapeIdentifier($"{result.TrimStart('%')}_ptr")}";
            var withPointer = $"%{EscapeIdentifier($"{result.TrimStart('%')}_p0")}";
            var unitType = GetTextUnitType(textSlice.TextValue.Type);

            AppendLine($"  {dataPointer} = extractvalue {MapType(textSlice.TextValue.Type)} {FormatValue(textSlice.TextValue)}, 0");
            AppendLine($"  {slicedPointer} = getelementptr inbounds {MapType(unitType)}, ptr {dataPointer}, {MapType(textSlice.Start.Type)} {FormatValue(textSlice.Start)}");
            AppendLine($"  {withPointer} = insertvalue {MapType(textSlice.Type)} zeroinitializer, ptr {slicedPointer}, 0");
            AppendLine($"  {result} = insertvalue {MapType(textSlice.Type)} {withPointer}, {MapType(textSlice.Length.Type)} {FormatValue(textSlice.Length)}, 1");
        }

        private void EmitAddressOfLocal(string result, SsaAddressOfLocalRValue addressOfLocal)
        {
            EnsureLocalSlotExists(addressOfLocal.LocalName, addressOfLocal.PointeeType);
            AppendLine($"  {result} = getelementptr inbounds {MapType(addressOfLocal.PointeeType)}, ptr %{EscapeIdentifier($"slot_{addressOfLocal.LocalName}")}, i32 0");
        }

        private void EmitAddressOfParameter(string result, SsaAddressOfParameterRValue addressOfParameter)
        {
            var parameter = _abiFunction.UserParameters.FirstOrDefault(
                candidate => string.Equals(candidate.SourceName, addressOfParameter.ParameterName, StringComparison.Ordinal));
            if (parameter is null)
            {
                throw new UnsupportedBodyEmissionException($"Unknown SSA parameter '{addressOfParameter.ParameterName}' for address emission.");
            }

            if (parameter.Kind == AbiParameterKind.IndirectIn)
            {
                AppendLine(
                    $"  {result} = getelementptr inbounds {MapType(addressOfParameter.PointeeType)}, ptr %{EscapeIdentifier(parameter.LlvmName)}, i32 0");
                return;
            }

            EnsureParameterSlotExists(parameter, addressOfParameter.PointeeType);
            AppendLine(
                $"  {result} = getelementptr inbounds {MapType(addressOfParameter.PointeeType)}, ptr %{EscapeIdentifier($"slot_param_{parameter.SourceName}")}, i32 0");
        }

        private void EmitFieldAddress(string result, SsaFieldAddressRValue fieldAddress)
        {
            AppendLine($"  {result} = getelementptr inbounds {MapType(fieldAddress.AggregateType)}, ptr {FormatValue(fieldAddress.Address)}, i32 0, i32 {fieldAddress.FieldIndex}");
        }

        private void EmitElementAddress(string result, SsaElementAddressRValue elementAddress)
        {
            if (elementAddress.AggregateType.Kind == StarkTypeKind.FixedArray)
            {
                var indexValue = elementAddress.ConstantIndex is int constantIndex
                    ? constantIndex.ToString()
                    : $"{MapType(elementAddress.Index!.Type)} {FormatValue(elementAddress.Index)}";

                if (elementAddress.ConstantIndex is int fixedArrayConstantIndex)
                {
                    AppendLine($"  {result} = getelementptr inbounds {MapType(elementAddress.AggregateType)}, ptr {FormatValue(elementAddress.Address)}, i32 0, i32 {fixedArrayConstantIndex}");
                }
                else
                {
                    AppendLine($"  {result} = getelementptr inbounds {MapType(elementAddress.AggregateType)}, ptr {FormatValue(elementAddress.Address)}, i32 0, {indexValue}");
                }

                return;
            }

            if (elementAddress.ConstantIndex is int scalarConstant)
            {
                AppendLine($"  {result} = getelementptr inbounds {MapType(elementAddress.AggregateType)}, ptr {FormatValue(elementAddress.Address)}, i32 {scalarConstant}");
                return;
            }

            if (elementAddress.Index is null)
            {
                throw new UnsupportedBodyEmissionException("Element address is missing its dynamic index.");
            }

            AppendLine($"  {result} = getelementptr inbounds {MapType(elementAddress.AggregateType)}, ptr {FormatValue(elementAddress.Address)}, {MapType(elementAddress.Index.Type)} {FormatValue(elementAddress.Index)}");
        }

        private void EmitSliceElementAddress(string result, SsaSliceElementAddressRValue sliceElementAddress)
        {
            var dataPointer = $"%{EscapeIdentifier($"{result.TrimStart('%')}_data")}";
            var elementType = sliceElementAddress.Type.ElementType ?? throw new UnsupportedBodyEmissionException("Slice element address requires a raw pointer element type.");

            AppendLine($"  {dataPointer} = extractvalue {MapType(sliceElementAddress.Slice.Type)} {FormatValue(sliceElementAddress.Slice)}, 0");
            AppendLine($"  {result} = getelementptr inbounds {MapType(elementType)}, ptr {dataPointer}, {MapType(sliceElementAddress.Index.Type)} {FormatValue(sliceElementAddress.Index)}");
        }

        private void EmitTerminator(SsaTerminator terminator)
        {
            switch (terminator.Kind)
            {
                case SsaTerminatorKind.Goto:
                    AppendLine($"  br label %{FormatBlockLabel(terminator.Targets[0])}");
                    return;
                case SsaTerminatorKind.Branch:
                    if (terminator.Condition is null)
                    {
                        throw new UnsupportedBodyEmissionException("SSA branch is missing a condition.");
                    }

                    AppendLine(
                        $"  br i1 {FormatValue(terminator.Condition)}, label %{FormatBlockLabel(terminator.Targets[0])}, label %{FormatBlockLabel(terminator.Targets[1])}");
                    return;
                case SsaTerminatorKind.Switch:
                    if (terminator.Condition is null || terminator.DefaultTarget is null)
                    {
                        throw new UnsupportedBodyEmissionException("SSA switch is missing its condition or default target.");
                    }

                    if (terminator.SwitchCases is null || terminator.SwitchCases.Count == 0)
                    {
                        AppendLine($"  br label %{FormatBlockLabel(terminator.DefaultTarget.Value)}");
                        return;
                    }

                    var switchCases = string.Join(
                        " ",
                        terminator.SwitchCases.Select(
                            switchCase => $"{MapType(switchCase.MatchValue.Type)} {FormatValue(switchCase.MatchValue)}, label %{FormatBlockLabel(switchCase.TargetBlockId)}"));

                    AppendLine(
                        $"  switch {MapType(terminator.Condition.Type)} {FormatValue(terminator.Condition)}, label %{FormatBlockLabel(terminator.DefaultTarget.Value)} [ {switchCases} ]");
                    return;
                case SsaTerminatorKind.Return:
                    if (_abiFunction.ReturnsIndirect)
                    {
                        if (terminator.Value is null || _abiFunction.ReturnBufferParameter is null)
                        {
                            throw new UnsupportedBodyEmissionException("SSA aggregate return is missing its value or sret parameter.");
                        }

                        AppendLine($"  store {MapType(_function.ReturnType)} {FormatValue(terminator.Value)}, ptr %{EscapeIdentifier(_abiFunction.ReturnBufferParameter.LlvmName)}");
                        AppendLine("  ret void");
                        return;
                    }

                    if (_function.ReturnType.Kind == StarkTypeKind.Void)
                    {
                        AppendLine("  ret void");
                        return;
                    }

                    if (terminator.Value is null)
                    {
                        throw new UnsupportedBodyEmissionException("SSA return is missing a return value.");
                    }

                    AppendLine($"  ret {MapType(_function.ReturnType)} {FormatValue(terminator.Value)}");
                    return;
                case SsaTerminatorKind.Unreachable:
                    AppendLine("  unreachable");
                    return;
                default:
                    throw new UnsupportedBodyEmissionException($"Unsupported SSA terminator '{terminator.Kind}'.");
            }
        }

        private void EmitFallbackTerminal()
        {
            if (_abiFunction.ReturnsIndirect || _function.ReturnType.Kind == StarkTypeKind.Void)
            {
                AppendLine("  ret void");
                return;
            }

            throw new UnsupportedBodyEmissionException("SSA function body has no blocks.");
        }

        private static string FormatBlockLabel(int blockId) => $"bb{blockId}";

        private string FormatValue(SsaValue value)
        {
            return value switch
            {
                SsaValueReference reference => FormatValueReference(reference),
                SsaIntegerConstant integer => integer.Value.ToString(),
                SsaFloatConstant floating => floating.LiteralText,
                SsaStringConstant text => FormatStringConstantValue(text),
                SsaBoolConstant boolean => boolean.Value ? "true" : "false",
                SsaNullConstant => "null",
                SsaGlobalAddressValue globalAddress => $"@{EscapeIdentifier(_mapGlobalSymbolName(globalAddress.GlobalName))}",
                SsaZeroInitializerValue => "zeroinitializer",
                SsaUndefValue => "undef",
                _ => throw new UnsupportedBodyEmissionException($"Unsupported SSA value '{value.GetType().Name}'.")
            };
        }

        private string RenderDirectArgument(AbiParameterSymbol parameter, SsaValue argument)
        {
            if (parameter.LlvmType.Kind == StarkTypeKind.RawPointer && IsStringType(parameter.SourceType))
            {
                return $"ptr {ExtractStringDataPointer(argument)}";
            }

            return $"{MapType(parameter.LlvmType)} {FormatValue(argument)}";
        }

        private string FormatStringConstantValue(SsaStringConstant text)
        {
            var pointer = FormatStringDataPointer(text.LiteralText, text.Type);
            var constant = _stringConstants[CreateStringConstantKey(text.LiteralText, text.Type)];
            return $"{{ ptr {pointer}, i64 {constant.DataLength} }}";
        }

        private string ExtractStringDataPointer(SsaValue value)
        {
            if (!IsStringType(value.Type))
            {
                throw new UnsupportedBodyEmissionException($"Value '{value.Text}' is not a lowered string.");
            }

            if (value is SsaStringConstant stringConstant)
            {
                return FormatStringDataPointer(stringConstant.LiteralText, stringConstant.Type);
            }

            var tempName = $"%{EscapeIdentifier(CreateAbiTempName("str_data"))}";
            AppendLine($"  {tempName} = extractvalue {MapType(value.Type)} {FormatValue(value)}, 0");
            return tempName;
        }

        private string FormatStringDataPointer(string literalText, StarkTypeSymbol type)
        {
            if (!_stringConstants.TryGetValue(CreateStringConstantKey(literalText, type), out var constant))
            {
                throw new UnsupportedBodyEmissionException($"Missing string constant for literal '{literalText}' with type '{type.DisplayName}'.");
            }

            return $"getelementptr inbounds ({constant.ArrayType}, ptr @{constant.SymbolName}, i32 0, i32 0)";
        }

        private void EnsureLocalSlotExists(string localName, StarkTypeSymbol localType)
        {
            var slotName = EscapeIdentifier($"slot_{localName}");
            if (_allocatedLocalSlots.Add(slotName))
            {
                AppendLine($"  %{slotName} = alloca {MapType(localType)}");
            }
        }

        private void EnsureParameterSlotExists(AbiParameterSymbol parameter, StarkTypeSymbol parameterType)
        {
            var slotName = EscapeIdentifier($"slot_param_{parameter.SourceName}");
            if (_allocatedLocalSlots.Add(slotName))
            {
                AppendLine($"  %{slotName} = alloca {MapType(parameterType)}");

                var incomingValue = _materializedParameters.TryGetValue(parameter.LlvmName, out var materialized)
                    ? materialized
                    : $"%{EscapeIdentifier(parameter.LlvmName)}";
                AppendLine($"  store {MapType(parameterType)} {incomingValue}, ptr %{slotName}");
            }
        }

        private void EmitEntryParameterMaterialization()
        {
            foreach (var parameter in _abiFunction.UserParameters)
            {
                if (parameter.Kind != AbiParameterKind.IndirectIn)
                {
                    continue;
                }

                var materializedName = $"%{EscapeIdentifier(CreateAbiTempName($"arg_{parameter.SourceName}_value"))}";
                AppendLine($"  {materializedName} = load {MapType(parameter.SourceType)}, ptr %{EscapeIdentifier(parameter.LlvmName)}");
                _materializedParameters[parameter.LlvmName] = materializedName;
            }
        }

        private string FormatValueReference(SsaValueReference reference)
        {
            return _materializedParameters.TryGetValue(reference.Name, out var materialized)
                ? materialized
                : $"%{EscapeIdentifier(reference.Name)}";
        }

        private string CreateAbiTempName(string purpose) => $"abi_{purpose}_{_nextAbiTempId++}";

        private string MapType(StarkTypeSymbol type) => _mapType(type);

        private static bool IsStringType(StarkTypeSymbol type)
        {
            return type.Kind is StarkTypeKind.Ascii or StarkTypeKind.Unicode;
        }

        private static StarkTypeSymbol GetTextUnitType(StarkTypeSymbol textType)
        {
            return textType.Kind switch
            {
                StarkTypeKind.Ascii => StarkTypeSymbols.Integer(8),
                StarkTypeKind.Unicode => StarkTypeSymbols.Integer(32),
                _ => throw new UnsupportedBodyEmissionException($"Text operations require an ascii/unicode value, but found '{textType.DisplayName}'.")
            };
        }

        private void AppendLine(string text) => _builder.AppendLine(text);
    }

    private sealed class UnsupportedBodyEmissionException : Exception
    {
        public UnsupportedBodyEmissionException(string message)
            : base(message)
        {
        }
    }

    private readonly record struct ObjectCreationKey(string Text, int Line, int Column);

    private sealed record GlobalInitializerPlan(string Rendered, IReadOnlyList<string> PreludeDefinitions);

    private sealed record ImportedLawClonePlan(
        string FunctionName,
        TypedFunctionSignature Signature,
        AbiFunctionSignature AbiSignature,
        FunctionEffectProfile Effects,
        SsaFunction SsaFunction);

    private enum VisitState
    {
        Visiting,
        Visited
    }

    private enum SystemTextBuiltinKind
    {
        AsciiView,
        UnicodeView,
        AsciiData,
        UnicodeData,
        AsciiLength,
        UnicodeLength,
        TryConcatAscii,
        TryConcatUnicode
    }

    private static StringConstantKey CreateStringConstantKey(string literalText, StarkTypeSymbol type)
    {
        if (type.Kind is not (StarkTypeKind.Ascii or StarkTypeKind.Unicode))
        {
            throw new InvalidOperationException($"String constant key requires an ascii/unicode type, but found '{type.DisplayName}'.");
        }

        return new StringConstantKey(literalText, type.Kind);
    }

    private readonly record struct StringConstantKey(string LiteralText, StarkTypeKind TypeKind);

    private sealed record EmittedStringConstant(string SymbolName, string ArrayType, string Initializer, int DataLength);
}
