using System.Numerics;
using System.Text;
using System.Globalization;
using System.IO;
using Stark.Parsing;

namespace Stark.Compiler;

internal sealed class LlvmIrEmitter
{
    private const string AsciiStringTypeName = "stark_ascii";
    private const string UnicodeStringTypeName = "stark_unicode";
    private const string AsciiEqualityHelperName = "__stark_ascii_equal";
    private const string UnicodeEqualityHelperName = "__stark_unicode_equal";
    private const string AsciiCompareHelperName = "__stark_ascii_compare";
    private const string UnicodeCompareHelperName = "__stark_unicode_compare";
    private const string FixedArrayCompareHelperNamePrefix = "__stark_fixed_array_compare_";
    private const string ScalarizedAggregateCompareHelperNamePrefix = "__stark_named_compare_";
    private const string IntegerExponentHelperNamePrefix = "__stark_int_pow_i";
    private const int AggregateScalarizationThresholdBytes = 16;
    private const int AggregateScalarizationMaxLeafCount = 4;
    private const int AggregateMemcpyThresholdBytes = 32;

    private sealed record AggregateScalarLeaf(IReadOnlyList<int> Indices, StarkTypeSymbol Type);

    private readonly CompilationInput _input;
    private readonly ParseResult _parseResult;
    private readonly SyntaxModel _syntaxModel;
    private readonly LoadedModuleSet _loadedModules;
    private readonly FunctionEffectModel _effectModel;
    private readonly TypeCheckModel _typeModel;
    private readonly EnumLayoutModel _enumLayoutModel;
    private readonly SemanticValidationModel? _semanticValidation;
    private readonly ClosedWorldOptimizationModel? _closedWorldModel;
    private readonly SpecializationCodegenStrategyModel? _specializationCodegenStrategy;
    private readonly CompilerLogBag? _logs;
    private readonly AbiModel _abiModel;
    private readonly SsaIrModule _ssa;
    private readonly LlvmTargetInfo? _targetInfo;
    private readonly bool _internalizeModulePrivate;
    private readonly IReadOnlyDictionary<string, string> _globalSymbols;
    private readonly IReadOnlySet<string> _globalsEligibleForLocalUnnamedAddr;
    private readonly IReadOnlyDictionary<StringConstantKey, EmittedStringConstant> _stringConstants;
    private readonly IReadOnlyDictionary<ObjectCreationKey, TypedConstructorShape?> _objectCreationConstructors;
    private readonly IReadOnlyDictionary<string, FunctionEffectProfile> _allFunctionEffects;
    private readonly IReadOnlyDictionary<string, TypedFunctionSignature> _allFunctionSignatures;
    private readonly IReadOnlyDictionary<string, AbiFunctionSignature> _allAbiFunctions;
    private readonly IReadOnlyDictionary<string, ConcreteTypeLayout> _publishedConcreteLayouts;
    private readonly IReadOnlyDictionary<string, ImportedFunctionSemanticSummary> _publishedFunctionSemantics;
    private readonly IReadOnlyDictionary<string, string> _specializationTemplateNames;
    private readonly IReadOnlyDictionary<string, SourceLocation> _functionLocations;
    private readonly IReadOnlyDictionary<string, ImportedLawClonePlan> _closedWorldImportedLawClones;
    private readonly IReadOnlySet<string> _referencedImportedFunctions;
    private readonly DebugMetadataEmitter _debugInfo;
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
        SpecializationCodegenStrategyModel? specializationCodegenStrategy = null,
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
            specializationCodegenStrategy,
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
        SpecializationCodegenStrategyModel? specializationCodegenStrategy = null,
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
        _specializationCodegenStrategy = specializationCodegenStrategy;
        _logs = logs;
        _abiModel = abiModel;
        _ssa = ssa;
        _targetInfo = targetInfo;
        _internalizeModulePrivate = internalizeModulePrivate;
        _globalSymbols = BuildGlobalSymbolMap();
        _globalsEligibleForLocalUnnamedAddr = BuildGlobalsEligibleForLocalUnnamedAddr();
        _stringConstants = CollectStringConstants(parseResult, ssa);
        _objectCreationConstructors = typeModel.ObjectCreations
            .GroupBy(static record => new ObjectCreationKey(record.ExpressionText, record.Location.Line, record.Location.Column))
            .ToDictionary(static group => group.Key, static group => group.Last().Constructor);
        _allFunctionEffects = BuildAllFunctionEffects(effectModel, specializationCodegenStrategy);
        _allFunctionSignatures = BuildAllFunctionSignatures(typeModel, ssa);
        _allAbiFunctions = BuildAllAbiFunctions(_allFunctionSignatures, abiModel, _allFunctionEffects, typeModel.NamedTypes, enumLayoutModel.Layouts);
        _publishedConcreteLayouts = BuildPublishedConcreteLayouts(loadedModules);
        _publishedFunctionSemantics = BuildPublishedFunctionSemantics(loadedModules);
        _specializationTemplateNames = BuildSpecializationTemplateNames(specializationCodegenStrategy);
        _functionLocations = BuildFunctionLocationMap(loadedModules, input.FilePath);
        _closedWorldImportedLawClones = BuildClosedWorldImportedLawClones();
        _referencedImportedFunctions = CollectReferencedImportedFunctions(ssa, _closedWorldImportedLawClones.Values);
        _debugInfo = new DebugMetadataEmitter(
            input.FilePath ?? $"{syntaxModel.ModuleName}.stark",
            TryGetConcreteTypeLayout);
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

        LogSpecializationCodegenStrategies();
        EmitBuiltinTypeDefinitions(builder);
        EmitNamedTypeDefinitions(builder);
        EmitStringConstants(builder);
        EmitGlobals(builder);
        EmitIntrinsicDeclarations(builder);
        EmitInternalHelperDefinitions(builder);

        var handledFunctionNames = new HashSet<string>(
            _syntaxModel.Declarations
                .Where(static declaration => declaration.Function is not null)
                .Select(declaration => FunctionOverloadFacts.GetResolvedLocalName(_syntaxModel, declaration)),
            StringComparer.Ordinal);
        var resolveCallAbi = CreateCallAbiResolver();

        foreach (var declaration in _syntaxModel.Declarations.Where(static declaration => declaration.Function is not null))
        {
            var function = declaration.Function!;
            var resolvedName = FunctionOverloadFacts.GetResolvedLocalName(_syntaxModel, declaration);
            var effects = _allFunctionEffects[resolvedName];
            var signature = _typeModel.Functions[resolvedName];
            var abiSignature = _abiModel.Functions[resolvedName];
            var ssaFunction = _ssa.Functions.FirstOrDefault(item => item.Name == resolvedName);
            var parameterEffects = GetParameterEffects(resolvedName, function.HasBody && !effects.IsFfi)
                ?? GetBuiltinParameterEffects(_syntaxModel.ModuleName, resolvedName, signature);
            var memoryEffects = GetFunctionMemoryEffects(resolvedName, function.HasBody && !effects.IsFfi);

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
                        memoryEffects,
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
                builder.AppendLine(BuildDeclarationSignature(false, signature, abiSignature, effects, memoryEffects, parameterEffects));
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
                    memoryEffects,
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
                    EmitFunctionDefinition(builder, definitionInternalize, signature, abiSignature, effects, memoryEffects, ssaFunction, parameterEffects, resolveCallAbi);
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

            builder.AppendLine(BuildDeclarationSignature(false, signature, abiSignature, effects, memoryEffects, parameterEffects));
            builder.AppendLine();
        }

        EmitMaterializedSpecializationDefinitions(builder, handledFunctionNames, resolveCallAbi);

        foreach (var clone in _closedWorldImportedLawClones.Values.OrderBy(static clone => clone.FunctionName, StringComparer.Ordinal))
        {
            var parameterEffects = GetParameterEffects(clone.FunctionName, hasBody: false);
            var memoryEffects = GetFunctionMemoryEffects(clone.FunctionName, hasBody: false);
            builder.AppendLine($"; closed-world imported law clone: {clone.FunctionName}");
            EmitFunctionDefinition(
                builder,
                internalize: true,
                clone.Signature,
                clone.AbiSignature,
                clone.Effects,
                memoryEffects,
                clone.SsaFunction,
                parameterEffects,
                resolveCallAbi);
            builder.AppendLine();
        }

        foreach (var abiFunction in _abiModel.Functions.Values
                     .Where(function => !handledFunctionNames.Contains(function.Name)
                                        )
                     .OrderBy(static function => function.Name, StringComparer.Ordinal))
        {
            if (!_allFunctionSignatures.TryGetValue(abiFunction.Name, out var signature)
                || !_allFunctionEffects.TryGetValue(abiFunction.Name, out var effects))
            {
                continue;
            }

            var parameterEffects = GetParameterEffects(abiFunction.Name, hasBody: false)
                ?? GetBuiltinParameterEffects(moduleName: string.Empty, abiFunction.Name, signature);
            var memoryEffects = GetFunctionMemoryEffects(abiFunction.Name, hasBody: false);
            if (_referencedImportedFunctions.Contains(abiFunction.Name)
                && TryEmitBuiltinFunctionDefinition(
                    builder,
                    internalize: true,
                    moduleName: string.Empty,
                    signature,
                    abiFunction,
                    effects,
                    memoryEffects,
                    parameterEffects))
            {
                builder.AppendLine();
                continue;
            }

            builder.AppendLine($"; imported declaration: {abiFunction.Name}");
            builder.AppendLine(BuildDeclarationSignature(false, signature, abiFunction, effects, memoryEffects, parameterEffects));
            builder.AppendLine();
        }

        _debugInfo.EmitModuleMetadata(builder);

        return new LlvmIrModule(_syntaxModel.ModuleName, builder.ToString().TrimEnd());
    }

    private static IReadOnlySet<string> CollectReferencedImportedFunctions(
        SsaIrModule ssa,
        IEnumerable<ImportedLawClonePlan> importedLawClones)
    {
        var referencedFunctions = new HashSet<string>(StringComparer.Ordinal);

        foreach (var function in ssa.Functions)
        {
            CollectReferencedFunctions(function, referencedFunctions);
        }

        foreach (var clone in importedLawClones)
        {
            CollectReferencedFunctions(clone.SsaFunction, referencedFunctions);
        }

        return referencedFunctions;
    }

    private static void CollectReferencedFunctions(SsaFunction function, ISet<string> referencedFunctions)
    {
        foreach (var block in function.Blocks)
        {
            foreach (var instruction in block.Instructions)
            {
                if (instruction is SsaValueInstruction { Value: SsaCallRValue call })
                {
                    referencedFunctions.Add(call.FunctionName);
                }
            }
        }
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

    private static IReadOnlyDictionary<string, FunctionEffectProfile> BuildAllFunctionEffects(
        FunctionEffectModel effectModel,
        SpecializationCodegenStrategyModel? specializationCodegenStrategy)
    {
        var functions = effectModel.Functions.ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value,
            StringComparer.Ordinal);

        if (specializationCodegenStrategy is null)
        {
            return functions;
        }

        foreach (var strategy in specializationCodegenStrategy.Functions)
        {
            if (!functions.TryGetValue(strategy.TemplateName, out var templateEffects))
            {
                continue;
            }

            functions.TryAdd(
                strategy.SymbolName,
                templateEffects with { Name = strategy.SymbolName });
        }

        return functions;
    }

    private static IReadOnlyDictionary<string, string> BuildSpecializationTemplateNames(
        SpecializationCodegenStrategyModel? specializationCodegenStrategy)
    {
        if (specializationCodegenStrategy is null)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        return specializationCodegenStrategy.Functions.ToDictionary(
            static function => function.SymbolName,
            static function => function.TemplateName,
            StringComparer.Ordinal);
    }

    private Func<string, string, AbiFunctionSignature?> CreateCallAbiResolver()
    {
        return (callerName, functionName) =>
        {
            if (_closedWorldImportedLawClones.TryGetValue(functionName, out var clone)
                && _allFunctionEffects.TryGetValue(callerName, out var callerEffects)
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
        IReadOnlyDictionary<string, FunctionEffectProfile> allFunctionEffects,
        IReadOnlyDictionary<string, NamedTypeSymbol> namedTypes,
        IReadOnlyDictionary<string, EnumLayoutSymbol> enumLayouts)
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

            var isFfi = allFunctionEffects.TryGetValue(function.Name, out var effects) && effects.IsFfi;
            functions[function.Name] = BuildSyntheticAbiSignature(function, function.Name, isFfi, namedTypes, enumLayouts);
        }

        return functions;
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

    private static IReadOnlyDictionary<string, ImportedFunctionSemanticSummary> BuildPublishedFunctionSemantics(LoadedModuleSet loadedModules)
    {
        var semantics = new Dictionary<string, ImportedFunctionSemanticSummary>(StringComparer.Ordinal);

        foreach (var module in loadedModules.ImportedModules)
        {
            if (module.PackageImageFacts is not { } packageImageFacts)
            {
                continue;
            }

            foreach (var (qualifiedName, summary) in packageImageFacts.FunctionSemantics)
            {
                semantics[qualifiedName] = summary;
            }
        }

        return semantics;
    }

    private void EmitMaterializedSpecializationDefinitions(
        StringBuilder builder,
        ISet<string> handledFunctionNames,
        Func<string, string, AbiFunctionSignature?> resolveCallAbi)
    {
        if (_specializationCodegenStrategy is null)
        {
            return;
        }

        var ssaByName = _ssa.Functions.ToDictionary(static function => function.Name, StringComparer.Ordinal);

        foreach (var strategy in _specializationCodegenStrategy.Functions.OrderBy(static function => function.SymbolName, StringComparer.Ordinal))
        {
            if (handledFunctionNames.Contains(strategy.SymbolName)
                || !_allFunctionSignatures.TryGetValue(strategy.SymbolName, out var signature)
                || !_allAbiFunctions.TryGetValue(strategy.SymbolName, out var abiSignature)
                || !_allFunctionEffects.TryGetValue(strategy.SymbolName, out var effects))
            {
                continue;
            }

            handledFunctionNames.Add(strategy.SymbolName);
            ssaByName.TryGetValue(strategy.SymbolName, out var ssaFunction);
            var hasBody = ssaFunction is { HasBody: true };
            var parameterEffects = GetParameterEffects(strategy.SymbolName, hasBody && !effects.IsFfi);
            var memoryEffects = GetFunctionMemoryEffects(strategy.SymbolName, hasBody && !effects.IsFfi);
            var definitionInternalize = strategy.Linkage == MonomorphizationLinkageKind.InternalSingleOwner;

            builder.AppendLine($"; specialization template: {strategy.TemplateName}");
            builder.AppendLine($"; specialization linkage: {strategy.Linkage}");

            if (hasBody && ssaFunction!.SupportsDirectCodeGeneration)
            {
                try
                {
                    if (strategy.Linkage == MonomorphizationLinkageKind.LinkOnceOdrComdat)
                    {
                        builder.AppendLine($"${EscapeIdentifier(strategy.SymbolName)} = comdat any");
                    }

                    EmitFunctionDefinition(
                        builder,
                        definitionInternalize,
                        signature,
                        abiSignature,
                        effects,
                        memoryEffects,
                        ssaFunction,
                        parameterEffects,
                        resolveCallAbi,
                        strategy.Linkage);
                    builder.AppendLine();
                    continue;
                }
                catch (UnsupportedBodyEmissionException exception)
                {
                    builder.AppendLine($"; LLVM body emission fallback for {strategy.SymbolName}: {exception.Message}");
                    LogLlvmFallback(
                        "llvm-body-fallback",
                        strategy.SymbolName,
                        exception.Message,
                        ssaFunction.BodyLoweringKind,
                        ssaFunction.SupportsDirectCodeGeneration,
                        operation: "EmitFunctionDefinition");
                }
            }
            else if (hasBody)
            {
                builder.AppendLine($"; LLVM body emission pending for {strategy.SymbolName}");
                LogLlvmFallback(
                    "llvm-body-pending",
                    strategy.SymbolName,
                    "SSA lowering did not leave this function in a direct-codegen-capable form, so LLVM emitted only a declaration.",
                    ssaFunction!.BodyLoweringKind,
                    ssaFunction.SupportsDirectCodeGeneration,
                    operation: "EmitFunctionDefinition");
            }

            builder.AppendLine(BuildDeclarationSignature(definitionInternalize, signature, abiSignature, effects, memoryEffects, parameterEffects));
            builder.AppendLine();
        }
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
            var cloneAbi = BuildSyntheticAbiSignature(signature, GetImportedLawCloneSymbolName(functionName), isFfi: false, _typeModel.NamedTypes, _enumLayoutModel.Layouts);
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

    private void LogSpecializationCodegenStrategies()
    {
        if (_logs is null || _specializationCodegenStrategy is null)
        {
            return;
        }

        foreach (var strategy in _specializationCodegenStrategy.Functions)
        {
            _logs.Info(
                "decision",
                "specialization-codegen-strategy",
                $"Emit path for specialization '{strategy.SymbolName}' is '{strategy.StrategyKind}'.",
                stage: "emit-llvm",
                symbolName: strategy.SymbolName,
                operation: "specialization-codegen-strategy",
                location: strategy.FirstUseLocation,
                data: CompilerLogData.Create(
                    ("template", strategy.TemplateName),
                    ("linkage", strategy.Linkage.ToString()),
                    ("supportsAbiFallback", strategy.SupportsAbiFallback.ToString()),
                    ("strategy", strategy.StrategyKind.ToString())),
                kind: CompilerLogKind.Decision,
                outcome: CompilerLogOutcome.Continued,
                verbosity: CompilerLogVerbosity.Verbose);
        }
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
        bool isFfi,
        IReadOnlyDictionary<string, NamedTypeSymbol> namedTypes,
        IReadOnlyDictionary<string, EnumLayoutSymbol> enumLayouts)
    {
        var returnsIndirect = !isFfi && AbiLoweringHeuristics.RequiresIndirectReturnAbi(function.ReturnType, namedTypes, enumLayouts);
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
            var kind = !isFfi && AbiLoweringHeuristics.RequiresIndirectParameterAbi(parameter.Type, namedTypes, enumLayouts)
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
            SourceName: function.SourceName,
            UsesFastCallingConvention: !isFfi);
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
                    builder.AppendLine(BuildGlobalDefinition(name, symbolName, visibility, global, initializer.Rendered));
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
                builder.AppendLine(BuildGlobalDefinition(name, symbolName, visibility, global, initializer.Rendered));
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

    private string BuildGlobalDefinition(string globalName, string symbolName, StarkVisibility visibility, TypedGlobalSymbol global, string initializer)
    {
        var segments = new List<string> { $"@{EscapeIdentifier(symbolName)}", "=" };

        if (ShouldInternalize(visibility))
        {
            segments.Add("internal");
        }

        if (GetGlobalAddressAttribute(globalName, visibility, global) is { } addressAttribute)
        {
            segments.Add(addressAttribute);
        }

        segments.Add(global.IsMutable ? "global" : "constant");
        segments.Add(MapType(global.Type));
        segments.Add(initializer);
        var definition = string.Join(" ", segments);
        if (TryGetGlobalAlignmentBytes(global.Type) is int alignmentBytes && alignmentBytes > 1)
        {
            definition += $", align {alignmentBytes}";
        }

        return definition;
    }

    private string? GetGlobalAddressAttribute(string globalName, StarkVisibility visibility, TypedGlobalSymbol global)
    {
        if (global.IsMutable
            || !_globalsEligibleForLocalUnnamedAddr.Contains(globalName))
        {
            return null;
        }

        return ShouldInternalize(visibility)
            ? "unnamed_addr"
            : "local_unnamed_addr";
    }

    private int? TryGetGlobalAlignmentBytes(StarkTypeSymbol type)
    {
        return TryGetTargetAwareTypeLayout(type, new HashSet<string>(StringComparer.Ordinal))?.AlignmentBytes
            ?? TryGetConcreteTypeLayout(type)?.AlignmentBytes;
    }

    private ConcreteTypeLayout? TryGetTargetAwareTypeLayout(
        StarkTypeSymbol type,
        ISet<string> activeNamedTypes)
    {
        var normalizedType = type with
        {
            BorrowKind = StarkBorrowKind.None,
            AccessKind = StarkAccessKind.None,
            InitializationKind = StarkInitializationKind.None,
            IsMutableView = false
        };

        return normalizedType.Kind switch
        {
            StarkTypeKind.Bool => new ConcreteTypeLayout(1, 1),
            StarkTypeKind.Integer when normalizedType.BitWidth is int bitWidth
                => TryGetTargetAwareScalarLayout(bitWidth, isFloat: false),
            StarkTypeKind.Float when normalizedType.BitWidth is int bitWidth
                => TryGetTargetAwareScalarLayout(bitWidth, isFloat: true),
            StarkTypeKind.RawPointer or StarkTypeKind.Null => TryGetTargetAwarePointerLayout(),
            StarkTypeKind.Ascii or StarkTypeKind.Unicode or StarkTypeKind.Slice => TryGetTargetAwareViewLayout(),
            StarkTypeKind.FixedArray when normalizedType.ElementType is not null && normalizedType.FixedLength is int fixedLength
                => TryGetTargetAwareFixedArrayLayout(normalizedType.ElementType, fixedLength, activeNamedTypes),
            StarkTypeKind.Named when normalizedType.NamedType is not null
                                     && _typeModel.NamedTypes.TryGetValue(normalizedType.NamedType, out var namedType)
                                     && namedType.Kind is DeclarationKind.Struct or DeclarationKind.Record
                => TryGetTargetAwareNamedTypeLayout(namedType, activeNamedTypes),
            StarkTypeKind.Named when normalizedType.NamedType is not null
                                     && _typeModel.NamedTypes.TryGetValue(normalizedType.NamedType, out var enumType)
                                     && enumType.Kind == DeclarationKind.Enum
                                     && _enumLayoutModel.Layouts.TryGetValue(normalizedType.NamedType, out var enumLayout)
                => TryGetTargetAwareEnumTypeLayout(enumLayout, activeNamedTypes),
            StarkTypeKind.Named => TryGetTargetAwarePointerLayout(),
            _ => null
        };
    }

    private ConcreteTypeLayout? TryGetTargetAwareScalarLayout(int bitWidth, bool isFloat)
    {
        if (bitWidth <= 0)
        {
            return new ConcreteTypeLayout(0, 1);
        }

        var sizeBytes = checked((bitWidth + 7) / 8);
        var alignmentBytes = TryGetTargetAwareScalarAlignmentBytes(bitWidth, isFloat);
        return alignmentBytes is null
            ? null
            : new ConcreteTypeLayout(sizeBytes, alignmentBytes.Value);
    }

    private ConcreteTypeLayout? TryGetTargetAwarePointerLayout()
    {
        var pointerSizeBytes = TryGetTargetPointerSizeBytes();
        var pointerAlignmentBytes = TryGetTargetPointerAlignmentBytes();
        if (pointerSizeBytes is null || pointerAlignmentBytes is null)
        {
            return null;
        }

        return new ConcreteTypeLayout(pointerSizeBytes.Value, pointerAlignmentBytes.Value);
    }

    private ConcreteTypeLayout? TryGetTargetAwareViewLayout()
    {
        var pointerLayout = TryGetTargetAwarePointerLayout();
        var lengthLayout = TryGetTargetAwareScalarLayout(64, isFloat: false);
        if (pointerLayout is null || lengthLayout is null)
        {
            return null;
        }

        var alignmentBytes = Math.Max(pointerLayout.AlignmentBytes, lengthLayout.AlignmentBytes);
        var sizeBytes = AlignTo(pointerLayout.SizeBytes, lengthLayout.AlignmentBytes);
        sizeBytes = checked(sizeBytes + lengthLayout.SizeBytes);
        sizeBytes = AlignTo(sizeBytes, alignmentBytes);
        return new ConcreteTypeLayout(sizeBytes, alignmentBytes);
    }

    private ConcreteTypeLayout? TryGetTargetAwareFixedArrayLayout(
        StarkTypeSymbol elementType,
        int fixedLength,
        ISet<string> activeNamedTypes)
    {
        var elementLayout = TryGetTargetAwareTypeLayout(elementType, activeNamedTypes);
        if (elementLayout is null)
        {
            return null;
        }

        try
        {
            return new ConcreteTypeLayout(
                checked(elementLayout.SizeBytes * fixedLength),
                fixedLength == 0 ? 1 : elementLayout.AlignmentBytes);
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    private ConcreteTypeLayout? TryGetTargetAwareNamedTypeLayout(
        NamedTypeSymbol namedType,
        ISet<string> activeNamedTypes)
    {
        if (!activeNamedTypes.Add(namedType.Name))
        {
            return null;
        }

        try
        {
            return TryGetTargetAwareAggregateLayout(namedType.OrderedFields.Select(static field => field.Type), activeNamedTypes);
        }
        finally
        {
            activeNamedTypes.Remove(namedType.Name);
        }
    }

    private ConcreteTypeLayout? TryGetTargetAwareEnumTypeLayout(
        EnumLayoutSymbol enumLayout,
        ISet<string> activeNamedTypes)
    {
        if (!activeNamedTypes.Add(enumLayout.EnumName))
        {
            return null;
        }

        try
        {
            return TryGetTargetAwareAggregateLayout(enumLayout.OrderedFields.Select(static field => field.Type), activeNamedTypes);
        }
        finally
        {
            activeNamedTypes.Remove(enumLayout.EnumName);
        }
    }

    private ConcreteTypeLayout? TryGetTargetAwareAggregateLayout(
        IEnumerable<StarkTypeSymbol> fieldTypes,
        ISet<string> activeNamedTypes)
    {
        try
        {
            var sizeBytes = 0;
            var alignmentBytes = 1;

            foreach (var fieldType in fieldTypes)
            {
                var fieldLayout = TryGetTargetAwareTypeLayout(fieldType, activeNamedTypes);
                if (fieldLayout is null)
                {
                    return null;
                }

                sizeBytes = AlignTo(sizeBytes, fieldLayout.AlignmentBytes);
                sizeBytes = checked(sizeBytes + fieldLayout.SizeBytes);
                alignmentBytes = Math.Max(alignmentBytes, fieldLayout.AlignmentBytes);
            }

            sizeBytes = AlignTo(sizeBytes, alignmentBytes);
            return new ConcreteTypeLayout(sizeBytes, alignmentBytes);
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    private int? TryGetTargetAwareScalarAlignmentBytes(int bitWidth, bool isFloat)
    {
        if (TryGetScalarAlignmentBytesFromDataLayout(bitWidth, isFloat) is { } fromLayout)
        {
            return fromLayout;
        }

        return TryGetTripleArchitecture() switch
        {
            StarkAsmArchitecture.X86_64 or StarkAsmArchitecture.AArch64 or StarkAsmArchitecture.RiscV64
                => bitWidth switch
                {
                    <= 8 => 1,
                    <= 16 => 2,
                    <= 32 => 4,
                    <= 64 => 8,
                    <= 128 => 16,
                    _ => 1
                },
            StarkAsmArchitecture.X86
                => bitWidth switch
                {
                    <= 8 => 1,
                    <= 16 => 2,
                    <= 32 => 4,
                    64 when isFloat => 4,
                    <= 64 => 4,
                    <= 128 => 16,
                    _ => 1
                },
            StarkAsmArchitecture.Arm32
                => bitWidth switch
                {
                    <= 8 => 1,
                    <= 16 => 2,
                    <= 32 => 4,
                    <= 64 => 8,
                    <= 128 => 16,
                    _ => 1
                },
            _ => null
        };
    }

    private int? TryGetTargetPointerSizeBytes()
    {
        if (TryGetPointerLayoutFromDataLayout(out var sizeBytes, out _))
        {
            return sizeBytes;
        }

        return TryGetTripleArchitecture() switch
        {
            StarkAsmArchitecture.X86_64 or StarkAsmArchitecture.AArch64 or StarkAsmArchitecture.RiscV64 => 8,
            StarkAsmArchitecture.X86 or StarkAsmArchitecture.Arm32 => 4,
            _ => null
        };
    }

    private int? TryGetTargetPointerAlignmentBytes()
    {
        if (TryGetPointerLayoutFromDataLayout(out _, out var alignmentBytes))
        {
            return alignmentBytes;
        }

        return TryGetTripleArchitecture() switch
        {
            StarkAsmArchitecture.X86_64 or StarkAsmArchitecture.AArch64 or StarkAsmArchitecture.RiscV64 => 8,
            StarkAsmArchitecture.X86 or StarkAsmArchitecture.Arm32 => 4,
            _ => null
        };
    }

    private bool TryGetPointerLayoutFromDataLayout(out int sizeBytes, out int alignmentBytes)
    {
        sizeBytes = 0;
        alignmentBytes = 0;

        var dataLayout = _targetInfo?.DataLayout;
        if (string.IsNullOrWhiteSpace(dataLayout))
        {
            return false;
        }

        foreach (var token in dataLayout.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!token.StartsWith("p:", StringComparison.Ordinal)
                && !token.StartsWith("p0:", StringComparison.Ordinal))
            {
                continue;
            }

            var parts = token.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length < 3
                || !int.TryParse(parts[1], out var sizeBits)
                || !int.TryParse(parts[2], out var alignBits))
            {
                continue;
            }

            sizeBytes = BitsToBytes(sizeBits);
            alignmentBytes = BitsToBytes(alignBits);
            return sizeBytes > 0 && alignmentBytes > 0;
        }

        return false;
    }

    private int? TryGetScalarAlignmentBytesFromDataLayout(int bitWidth, bool isFloat)
    {
        var dataLayout = _targetInfo?.DataLayout;
        if (string.IsNullOrWhiteSpace(dataLayout))
        {
            return null;
        }

        var prefix = $"{(isFloat ? 'f' : 'i')}{bitWidth}:";
        foreach (var token in dataLayout.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!token.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            var parts = token.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length < 2 || !int.TryParse(parts[1], out var alignBits))
            {
                continue;
            }

            return BitsToBytes(alignBits);
        }

        return null;
    }

    private StarkAsmArchitecture TryGetTripleArchitecture()
    {
        var triple = _targetInfo?.Triple;
        if (string.IsNullOrWhiteSpace(triple))
        {
            return StarkAsmArchitecture.Unknown;
        }

        var architecture = triple.Split('-', 2, StringSplitOptions.TrimEntries)[0];
        if (architecture.StartsWith("x86_64", StringComparison.OrdinalIgnoreCase)
            || architecture.StartsWith("amd64", StringComparison.OrdinalIgnoreCase))
        {
            return StarkAsmArchitecture.X86_64;
        }

        if (architecture.StartsWith("i386", StringComparison.OrdinalIgnoreCase)
            || architecture.StartsWith("i486", StringComparison.OrdinalIgnoreCase)
            || architecture.StartsWith("i586", StringComparison.OrdinalIgnoreCase)
            || architecture.StartsWith("i686", StringComparison.OrdinalIgnoreCase)
            || string.Equals(architecture, "x86", StringComparison.OrdinalIgnoreCase))
        {
            return StarkAsmArchitecture.X86;
        }

        if (architecture.StartsWith("aarch64", StringComparison.OrdinalIgnoreCase)
            || architecture.StartsWith("arm64", StringComparison.OrdinalIgnoreCase))
        {
            return StarkAsmArchitecture.AArch64;
        }

        if (architecture.StartsWith("riscv64", StringComparison.OrdinalIgnoreCase))
        {
            return StarkAsmArchitecture.RiscV64;
        }

        if (architecture.StartsWith("arm", StringComparison.OrdinalIgnoreCase))
        {
            return StarkAsmArchitecture.Arm32;
        }

        return StarkAsmArchitecture.Unknown;
    }

    private static int BitsToBytes(int bitCount)
    {
        return bitCount <= 0 ? 0 : (bitCount + 7) / 8;
    }

    private static int AlignTo(int value, int alignment)
    {
        if (alignment <= 1)
        {
            return value;
        }

        var remainder = value % alignment;
        return remainder == 0 ? value : checked(value + alignment - remainder);
    }

    private IReadOnlySet<string> BuildGlobalsEligibleForLocalUnnamedAddr()
    {
        var addressTakenGlobals = CollectExplicitGlobalAddressNames(_ssa);
        var eligible = new HashSet<string>(StringComparer.Ordinal);

        foreach (var declaration in _parseResult.Root.topLevelDeclaration())
        {
            if (declaration.globalConstantDeclaration() is { } constantDeclaration)
            {
                foreach (var declarator in constantDeclaration.constantDeclarators().constantDeclarator())
                {
                    var name = declarator.Identifier().GetText();
                    if (!_typeModel.Globals.TryGetValue(name, out var global)
                        || global.IsMutable
                        || addressTakenGlobals.Contains(name)
                        || ShouldEmitExternalConstPlaceholder(global, declarator.variableInitializer()))
                    {
                        continue;
                    }

                    eligible.Add(name);
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
                if (!_typeModel.Globals.TryGetValue(name, out var global)
                    || global.IsMutable
                    || declarator.variableInitializer() is null
                    || addressTakenGlobals.Contains(name))
                {
                    continue;
                }

                eligible.Add(name);
            }
        }

        return eligible;
    }

    private static IReadOnlySet<string> CollectExplicitGlobalAddressNames(SsaIrModule module)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);

        foreach (var function in module.Functions)
        {
            foreach (var block in function.Blocks)
            {
                foreach (var phi in block.Phis)
                {
                    foreach (var incoming in phi.Incomings)
                    {
                        CollectExplicitGlobalAddressNames(incoming.Value, names);
                    }
                }

                foreach (var instruction in block.Instructions)
                {
                    foreach (var value in EnumerateInstructionOperands(instruction))
                    {
                        CollectExplicitGlobalAddressNames(value, names);
                    }
                }

                foreach (var value in EnumerateTerminatorOperands(block.Terminator))
                {
                    CollectExplicitGlobalAddressNames(value, names);
                }
            }
        }

        return names;
    }

    private static void CollectExplicitGlobalAddressNames(SsaValue value, ISet<string> names)
    {
        if (value is SsaGlobalAddressValue globalAddress)
        {
            names.Add(globalAddress.GlobalName);
        }
    }

    private static IEnumerable<SsaValue> EnumerateInstructionOperands(SsaInstruction instruction)
    {
        return instruction switch
        {
            SsaValueInstruction valueInstruction => EnumerateRValueOperands(valueInstruction.Value),
            SsaLifetimeStartInstruction => [],
            SsaLifetimeEndInstruction => [],
            SsaDeallocateLocalInstruction => [],
            SsaStoreLocalInstruction storeLocal => [storeLocal.Value],
            SsaCopyMemoryInstruction copyMemory => [copyMemory.DestinationAddress, copyMemory.SourceAddress],
            SsaStoreIndirectInstruction storeIndirect => [storeIndirect.Address, storeIndirect.Value],
            SsaStoreGlobalInstruction storeGlobal => [storeGlobal.Value],
            _ => []
        };
    }

    private static IEnumerable<SsaValue> EnumerateRValueOperands(SsaRValue value)
    {
        return value switch
        {
            SsaUseRValue use => [use.Value],
            SsaUnaryRValue unary => [unary.Operand],
            SsaBinaryRValue binary => [binary.Left, binary.Right],
            SsaCallRValue call => call.Arguments,
            SsaConvertRValue convert => [convert.Operand],
            SsaExtractFieldRValue extractField => [extractField.Target],
            SsaInsertFieldRValue insertField => [insertField.Target, insertField.Value],
            SsaExtractIndexRValue extractIndex => [extractIndex.Target],
            SsaInsertIndexRValue insertIndex => [insertIndex.Target, insertIndex.Value],
            SsaLoadSliceElementRValue loadSlice => [loadSlice.Slice, loadSlice.Index],
            SsaTextSliceRValue textSlice => [textSlice.TextValue, textSlice.Start, textSlice.Length],
            SsaFieldAddressRValue fieldAddress => [fieldAddress.Address],
            SsaElementAddressRValue elementAddress when elementAddress.Index is not null => [elementAddress.Address, elementAddress.Index],
            SsaElementAddressRValue elementAddress => [elementAddress.Address],
            SsaSliceElementAddressRValue sliceElementAddress => [sliceElementAddress.Slice, sliceElementAddress.Index],
            SsaLoadIndirectRValue loadIndirect => [loadIndirect.Address],
            _ => []
        };
    }

    private static IEnumerable<SsaValue> EnumerateTerminatorOperands(SsaTerminator terminator)
    {
        if (terminator.Condition is not null)
        {
            yield return terminator.Condition;
        }

        if (terminator.Value is not null)
        {
            yield return terminator.Value;
        }

        if (terminator.SwitchCases is null)
        {
            yield break;
        }

        foreach (var switchCase in terminator.SwitchCases)
        {
            yield return switchCase.MatchValue;
        }
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

        foreach (var declaration in EnumerateSystemMathIntrinsicDeclarations())
        {
            declarations.Add(declaration);
        }

        foreach (var declaration in EnumerateSystemBitOperationsIntrinsicDeclarations())
        {
            declarations.Add(declaration);
        }

        if (UsesLifetimeMarkers())
        {
            declarations.Add("declare void @llvm.lifetime.start.p0(i64 immarg, ptr nocapture)");
            declarations.Add("declare void @llvm.lifetime.end.p0(i64 immarg, ptr nocapture)");
        }

        if (UsesHeapAllocator())
        {
            declarations.Add($"declare ptr @malloc({GetAllocatorSizeType()})");
            declarations.Add("declare void @free(ptr)");
        }

        if (UsesMemcpyInlineIntrinsic())
        {
            declarations.Add("declare void @llvm.memcpy.inline.p0.p0.i64(ptr nocapture writeonly, ptr nocapture readonly, i64 immarg, i1 immarg)");
        }

        if (UsesMemsetInlineIntrinsic())
        {
            declarations.Add("declare void @llvm.memset.inline.p0.i64(ptr nocapture writeonly, i8, i64 immarg, i1 immarg)");
        }

        if (_debugInfo.Enabled)
        {
            declarations.Add("declare void @llvm.dbg.declare(metadata, metadata, metadata)");
            declarations.Add("declare void @llvm.dbg.value(metadata, metadata, metadata)");
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

    private IEnumerable<string> EnumerateSystemMathIntrinsicDeclarations()
    {
        foreach (var signature in _allFunctionSignatures.Values)
        {
            if (!TryResolveSystemMathBuiltin(_syntaxModel.ModuleName, signature, out var builtinKind))
            {
                continue;
            }

            if (!IsLlvmIntrinsicSystemMathBuiltin(builtinKind))
            {
                continue;
            }

            yield return BuildSystemMathIntrinsicDeclaration(signature, builtinKind);
        }
    }

    private IEnumerable<string> EnumerateSystemBitOperationsIntrinsicDeclarations()
    {
        foreach (var signature in _allFunctionSignatures.Values)
        {
            if (!TryResolveSystemBitOperationsBuiltin(_syntaxModel.ModuleName, signature, out var builtinKind))
            {
                continue;
            }

            yield return BuildSystemBitOperationsIntrinsicDeclaration(signature, builtinKind);
        }
    }

    private void EmitInternalHelperDefinitions(StringBuilder builder)
    {
        EmitTextEqualityHelperDefinition(builder, StarkTypeSymbols.Ascii, AsciiEqualityHelperName);
        builder.AppendLine();
        EmitTextEqualityHelperDefinition(builder, StarkTypeSymbols.Unicode, UnicodeEqualityHelperName);
        builder.AppendLine();
        EmitTextComparisonHelperDefinition(builder, StarkTypeSymbols.Ascii, AsciiCompareHelperName);
        builder.AppendLine();
        EmitTextComparisonHelperDefinition(builder, StarkTypeSymbols.Unicode, UnicodeCompareHelperName);
        builder.AppendLine();

        foreach (var fixedArrayType in CollectFixedArrayOrderedComparisonTypes())
        {
            EmitFixedArrayOrderedComparisonHelperDefinition(builder, fixedArrayType);
            builder.AppendLine();
        }

        var namedAggregateCompareTypes = new Dictionary<string, StarkTypeSymbol>(StringComparer.Ordinal);
        foreach (var binary in EnumerateBinaryOperations())
        {
            if (binary.Type.Kind != StarkTypeKind.Bool)
            {
                continue;
            }

            if (binary.Operator is not (
                    SsaBinaryOperator.LessThan
                    or SsaBinaryOperator.LessThanOrEqual
                    or SsaBinaryOperator.GreaterThan
                    or SsaBinaryOperator.GreaterThanOrEqual))
            {
                continue;
            }

            CollectScalarizedNamedAggregateOrderedComparisonTypes(binary.Left.Type, namedAggregateCompareTypes);
        }

        foreach (var namedAggregateType in namedAggregateCompareTypes.Values
                     .OrderBy(static type => type.DisplayName, StringComparer.Ordinal)
                     .ToArray())
        {
            EmitScalarizedNamedAggregateOrderedComparisonHelperDefinition(builder, namedAggregateType);
            builder.AppendLine();
        }

        foreach (var bitWidth in CollectIntegerExponentBitWidths())
        {
            EmitIntegerExponentHelperDefinition(builder, bitWidth);
            builder.AppendLine();
        }

        void CollectScalarizedNamedAggregateOrderedComparisonTypes(
            StarkTypeSymbol type,
            Dictionary<string, StarkTypeSymbol> collected)
        {
            if (type.Kind != StarkTypeKind.Named)
            {
                return;
            }

            var namedType = ResolveNamedTypeSymbol(type);
            if (namedType is null
                || namedType.Kind is not (DeclarationKind.Struct or DeclarationKind.Record or DeclarationKind.Enum)
                || !TryGetScalarizableAggregateLeaves(
                    type,
                    requireRepresentationPreserving: false,
                    ignoreScalarizationThresholds: true,
                    allowTextLeaves: true,
                    allowSliceLeaves: false,
                    out _))
            {
                return;
            }

            var helperName = GetScalarizedAggregateOrderedComparisonHelperName(type);
            collected.TryAdd(helperName, type);
        }

        void EmitScalarizedNamedAggregateOrderedComparisonHelperDefinition(
            StringBuilder helperBuilder,
            StarkTypeSymbol aggregateType)
        {
            if (aggregateType.Kind != StarkTypeKind.Named
                || ResolveNamedTypeSymbol(aggregateType) is not { } aggregateNamedType
                || aggregateNamedType.Kind is not (DeclarationKind.Struct or DeclarationKind.Record or DeclarationKind.Enum)
                || !TryGetScalarizableAggregateLeaves(
                    aggregateType,
                    requireRepresentationPreserving: false,
                    ignoreScalarizationThresholds: true,
                    allowTextLeaves: true,
                    allowSliceLeaves: false,
                    out var leaves))
            {
                throw new InvalidOperationException(
                    $"Named aggregate ordered comparison helper requires a scalarizable named aggregate type, but found '{aggregateType.DisplayName}'.");
            }

            var helperName = GetScalarizedAggregateOrderedComparisonHelperName(aggregateType);
            var aggregateLlvmType = MapType(aggregateType);

            helperBuilder.AppendLine($"define internal i32 @{EscapeIdentifier(helperName)}({aggregateLlvmType} %left, {aggregateLlvmType} %right) {{");
            helperBuilder.AppendLine("entry:");
            if (leaves.Count == 0)
            {
                helperBuilder.AppendLine("  ret i32 0");
                helperBuilder.AppendLine("}");
                return;
            }

            helperBuilder.AppendLine("  br label %compare_0");

            for (var index = 0; index < leaves.Count; index++)
            {
                helperBuilder.AppendLine();
                helperBuilder.AppendLine($"compare_{index}:");

                var leftValue = EmitAggregateLeafValueExtraction(
                    helperBuilder,
                    aggregateType,
                    "%left",
                    leaves[index].Indices,
                    $"namedcmp_left_{index}");
                var rightValue = EmitAggregateLeafValueExtraction(
                    helperBuilder,
                    aggregateType,
                    "%right",
                    leaves[index].Indices,
                    $"namedcmp_right_{index}");

                if (!TryEmitOrderedComparisonValue(
                        helperBuilder,
                        leaves[index].Type,
                        leftValue,
                        rightValue,
                        index,
                        $"check_greater_{index}",
                        index == leaves.Count - 1 ? "return_equal" : $"compare_{index + 1}"))
                {
                    throw new UnsupportedBodyEmissionException(
                        $"Unsupported ordered comparison leaf type '{leaves[index].Type.DisplayName}' in named aggregate helper.");
                }
            }

            helperBuilder.AppendLine("return_equal:");
            helperBuilder.AppendLine("  ret i32 0");
            helperBuilder.AppendLine("return_less:");
            helperBuilder.AppendLine("  ret i32 -1");
            helperBuilder.AppendLine("return_greater:");
            helperBuilder.AppendLine("  ret i32 1");
            helperBuilder.AppendLine("}");
        }
    }

    private IReadOnlyList<int> CollectIntegerExponentBitWidths()
    {
        return _ssa.Functions
            .SelectMany(static function => function.Blocks)
            .SelectMany(static block => block.Instructions)
            .OfType<SsaValueInstruction>()
            .Select(instruction => instruction.Value)
            .OfType<SsaBinaryRValue>()
            .Where(static binary => binary.Operator == SsaBinaryOperator.Exponent && binary.Type.Kind == StarkTypeKind.Integer && binary.Type.BitWidth is int)
            .Select(static binary => binary.Type.BitWidth!.Value)
            .Distinct()
            .OrderBy(static bitWidth => bitWidth)
            .ToArray();
    }

    private IReadOnlyList<StarkTypeSymbol> CollectFixedArrayOrderedComparisonTypes()
    {
        var collected = new Dictionary<string, StarkTypeSymbol>(StringComparer.Ordinal);

        foreach (var binary in EnumerateBinaryOperations())
        {
            if (binary.Type.Kind != StarkTypeKind.Bool)
            {
                continue;
            }

            if (binary.Operator is not (
                    SsaBinaryOperator.LessThan
                    or SsaBinaryOperator.LessThanOrEqual
                    or SsaBinaryOperator.GreaterThan
                    or SsaBinaryOperator.GreaterThanOrEqual))
            {
                continue;
            }

            CollectFixedArrayOrderedComparisonTypes(binary.Left.Type, collected);
        }

        return collected.Values
            .OrderBy(static type => type.DisplayName, StringComparer.Ordinal)
            .ToArray();
    }

    private IReadOnlyList<StarkTypeSymbol> CollectScalarizedNamedAggregateOrderedComparisonTypes()
    {
        var collected = new Dictionary<string, StarkTypeSymbol>(StringComparer.Ordinal);

        foreach (var binary in EnumerateBinaryOperations())
        {
            if (binary.Type.Kind != StarkTypeKind.Bool)
            {
                continue;
            }

            if (binary.Operator is not (
                    SsaBinaryOperator.LessThan
                    or SsaBinaryOperator.LessThanOrEqual
                    or SsaBinaryOperator.GreaterThan
                    or SsaBinaryOperator.GreaterThanOrEqual))
            {
                continue;
            }

            CollectScalarizedNamedAggregateOrderedComparisonTypes(binary.Left.Type, collected);
        }

        return collected.Values
            .OrderBy(static type => type.DisplayName, StringComparer.Ordinal)
            .ToArray();
    }

    private void CollectFixedArrayOrderedComparisonTypes(
        StarkTypeSymbol type,
        Dictionary<string, StarkTypeSymbol> collected)
    {
        if (type.Kind != StarkTypeKind.FixedArray
            || type.ElementType is null
            || type.FixedLength is not int)
        {
            return;
        }

        var helperName = GetFixedArrayOrderedComparisonHelperName(type);
        if (!collected.TryAdd(helperName, type))
        {
            return;
        }

        CollectFixedArrayOrderedComparisonTypes(type.ElementType, collected);
    }

    private void CollectScalarizedNamedAggregateOrderedComparisonTypes(
        StarkTypeSymbol type,
        Dictionary<string, StarkTypeSymbol> collected)
    {
        if (type.Kind != StarkTypeKind.Named
            || !SupportsScalarizedAggregateOrderedComparison(type)
            || !TryGetScalarizableAggregateLeaves(
                type,
                requireRepresentationPreserving: false,
                ignoreScalarizationThresholds: true,
                allowTextLeaves: true,
                allowSliceLeaves: false,
                out _))
        {
            return;
        }

        var helperName = GetScalarizedAggregateOrderedComparisonHelperName(type);
        collected.TryAdd(helperName, type);
    }

    private bool SupportsScalarizedAggregateOrderedComparison(StarkTypeSymbol rootType)
    {
        return rootType.Kind switch
        {
            StarkTypeKind.FixedArray => true,
            StarkTypeKind.Named => ResolveNamedTypeSymbol(rootType) is { } namedType
                && (namedType.Kind is DeclarationKind.Struct or DeclarationKind.Record
                    || (namedType.Kind == DeclarationKind.Enum && namedType.EnumVariants is { Count: > 0 })),
            _ => false
        };
    }

    private bool TryGetScalarizableAggregateLeaves(
        StarkTypeSymbol type,
        bool requireRepresentationPreserving,
        bool ignoreScalarizationThresholds,
        bool allowTextLeaves,
        bool allowSliceLeaves,
        out IReadOnlyList<AggregateScalarLeaf> leaves)
    {
        leaves = Array.Empty<AggregateScalarLeaf>();

        if (TryGetConcreteTypeLayout(NormalizeAggregateType(type)) is not { } layout
            || layout.SizeBytes <= 0
            || (!ignoreScalarizationThresholds && layout.SizeBytes > AggregateScalarizationThresholdBytes))
        {
            return false;
        }

        var collectedLeaves = new List<AggregateScalarLeaf>();
        if (!TryCollectScalarizableAggregateLeaves(
                NormalizeAggregateType(type),
                requireRepresentationPreserving,
                allowTextLeaves,
                allowSliceLeaves,
                [],
                collectedLeaves))
        {
            return false;
        }

        if (collectedLeaves.Count == 0
            || (!ignoreScalarizationThresholds && collectedLeaves.Count > AggregateScalarizationMaxLeafCount))
        {
            return false;
        }

        leaves = collectedLeaves;
        return true;
    }

    private bool TryCollectScalarizableAggregateLeaves(
        StarkTypeSymbol type,
        bool requireRepresentationPreserving,
        bool allowTextLeaves,
        bool allowSliceLeaves,
        List<int> path,
        List<AggregateScalarLeaf> leaves)
    {
        var normalizedType = NormalizeAggregateType(type);
        switch (normalizedType.Kind)
        {
            case StarkTypeKind.Bool:
            case StarkTypeKind.Integer:
            case StarkTypeKind.Float:
            case StarkTypeKind.RawPointer:
                leaves.Add(new AggregateScalarLeaf([.. path], normalizedType));
                return true;
            case StarkTypeKind.Ascii when allowTextLeaves:
            case StarkTypeKind.Unicode when allowTextLeaves:
                leaves.Add(new AggregateScalarLeaf([.. path], normalizedType));
                return true;
            case StarkTypeKind.Slice when allowSliceLeaves:
                leaves.Add(new AggregateScalarLeaf([.. path], normalizedType));
                return true;
            case StarkTypeKind.FixedArray when normalizedType.ElementType is not null && normalizedType.FixedLength is int fixedLength:
                for (var index = 0; index < fixedLength; index++)
                {
                    path.Add(index);
                    if (!TryCollectScalarizableAggregateLeaves(
                            normalizedType.ElementType,
                            requireRepresentationPreserving,
                            allowTextLeaves,
                            allowSliceLeaves,
                            path,
                            leaves))
                    {
                        path.RemoveAt(path.Count - 1);
                        return false;
                    }

                    path.RemoveAt(path.Count - 1);
                }

                return true;
            case StarkTypeKind.Named:
            {
                var namedType = ResolveNamedTypeSymbol(normalizedType);
                if (namedType is null
                    || !TryGetScalarizableNamedAggregateFields(namedType, out var orderedFields))
                {
                    return false;
                }

                var sizeBytes = 0;
                var alignmentBytes = 1;
                for (var index = 0; index < orderedFields.Count; index++)
                {
                    var field = orderedFields[index];
                    var fieldLayout = TryGetConcreteTypeLayout(NormalizeAggregateType(field.Type));
                    if (fieldLayout is null)
                    {
                        return false;
                    }

                    var alignedOffset = AlignTo(sizeBytes, fieldLayout.AlignmentBytes);
                    if (requireRepresentationPreserving && alignedOffset != sizeBytes)
                    {
                        return false;
                    }

                    path.Add(index);
                    if (!TryCollectScalarizableAggregateLeaves(
                            field.Type,
                            requireRepresentationPreserving,
                            allowTextLeaves,
                            allowSliceLeaves,
                            path,
                            leaves))
                    {
                        path.RemoveAt(path.Count - 1);
                        return false;
                    }

                    path.RemoveAt(path.Count - 1);
                    sizeBytes = checked(alignedOffset + fieldLayout.SizeBytes);
                    alignmentBytes = Math.Max(alignmentBytes, fieldLayout.AlignmentBytes);
                }

                if (requireRepresentationPreserving && AlignTo(sizeBytes, alignmentBytes) != sizeBytes)
                {
                    return false;
                }

                return true;
            }
            default:
                return false;
        }
    }

    private string EmitAggregateLeafValueExtraction(
        StringBuilder builder,
        StarkTypeSymbol rootType,
        string rootValue,
        IReadOnlyList<int> indices,
        string purpose)
    {
        if (indices.Count == 0)
        {
            return rootValue;
        }

        var currentValue = rootValue;
        var currentType = NormalizeAggregateType(rootType);
        var step = 0;

        foreach (var index in indices)
        {
            var nextType = GetAggregateElementType(currentType, index)
                ?? throw new UnsupportedBodyEmissionException(
                    $"Cannot extract aggregate leaf for '{rootType.DisplayName}'.");
            var extracted = $"%{EscapeIdentifier($"{purpose}_{step++}")}";
            builder.AppendLine($"  {extracted} = extractvalue {MapType(currentType)} {currentValue}, {index}");
            currentValue = extracted;
            currentType = NormalizeAggregateType(nextType);
        }

        return currentValue;
    }

    private void EmitTextEqualityHelperDefinition(
        StringBuilder builder,
        StarkTypeSymbol textType,
        string helperName)
    {
        var textLlvmType = MapType(textType);
        var unitType = textType.Kind switch
        {
            StarkTypeKind.Ascii => StarkTypeSymbols.Integer(8),
            StarkTypeKind.Unicode => StarkTypeSymbols.Integer(32),
            _ => throw new InvalidOperationException($"Text equality helper requires an ascii/unicode type, but found '{textType.DisplayName}'.")
        };
        var unitLlvmType = MapType(unitType);

        builder.AppendLine($"define internal i1 @{EscapeIdentifier(helperName)}({textLlvmType} %left, {textLlvmType} %right) {{");
        builder.AppendLine("entry:");
        builder.AppendLine($"  %left_data = extractvalue {textLlvmType} %left, 0");
        builder.AppendLine($"  %left_length = extractvalue {textLlvmType} %left, 1");
        builder.AppendLine($"  %right_data = extractvalue {textLlvmType} %right, 0");
        builder.AppendLine($"  %right_length = extractvalue {textLlvmType} %right, 1");
        builder.AppendLine("  %length_equal = icmp eq i64 %left_length, %right_length");
        builder.AppendLine("  br i1 %length_equal, label %loop_header, label %return_false");
        builder.AppendLine();
        builder.AppendLine("loop_header:");
        builder.AppendLine("  %textcmp_index = phi i64 [ 0, %entry ], [ %textcmp_next, %loop_continue ]");
        builder.AppendLine("  %textcmp_done = icmp eq i64 %textcmp_index, %left_length");
        builder.AppendLine("  br i1 %textcmp_done, label %return_true, label %loop_body");
        builder.AppendLine();
        builder.AppendLine("loop_body:");
        builder.AppendLine($"  %left_unit_ptr = getelementptr inbounds {unitLlvmType}, ptr %left_data, i64 %textcmp_index");
        builder.AppendLine($"  %right_unit_ptr = getelementptr inbounds {unitLlvmType}, ptr %right_data, i64 %textcmp_index");
        builder.AppendLine($"  %left_unit = load {unitLlvmType}, ptr %left_unit_ptr");
        builder.AppendLine($"  %right_unit = load {unitLlvmType}, ptr %right_unit_ptr");
        builder.AppendLine($"  %unit_equal = icmp eq {unitLlvmType} %left_unit, %right_unit");
        builder.AppendLine("  br i1 %unit_equal, label %loop_continue, label %return_false");
        builder.AppendLine();
        builder.AppendLine("loop_continue:");
        builder.AppendLine("  %textcmp_next = add i64 %textcmp_index, 1");
        builder.AppendLine("  br label %loop_header");
        builder.AppendLine();
        builder.AppendLine("return_false:");
        builder.AppendLine("  ret i1 false");
        builder.AppendLine();
        builder.AppendLine("return_true:");
        builder.AppendLine("  ret i1 true");
        builder.AppendLine("}");
    }

    private void EmitTextComparisonHelperDefinition(
        StringBuilder builder,
        StarkTypeSymbol textType,
        string helperName)
    {
        var textLlvmType = MapType(textType);
        var unitType = textType.Kind switch
        {
            StarkTypeKind.Ascii => StarkTypeSymbols.Integer(8),
            StarkTypeKind.Unicode => StarkTypeSymbols.Integer(32),
            _ => throw new InvalidOperationException($"Text comparison helper requires an ascii/unicode type, but found '{textType.DisplayName}'.")
        };
        var unitLlvmType = MapType(unitType);

        builder.AppendLine($"define internal i32 @{EscapeIdentifier(helperName)}({textLlvmType} %left, {textLlvmType} %right) {{");
        builder.AppendLine("entry:");
        builder.AppendLine($"  %left_data = extractvalue {textLlvmType} %left, 0");
        builder.AppendLine($"  %left_length = extractvalue {textLlvmType} %left, 1");
        builder.AppendLine($"  %right_data = extractvalue {textLlvmType} %right, 0");
        builder.AppendLine($"  %right_length = extractvalue {textLlvmType} %right, 1");
        builder.AppendLine("  %left_shorter = icmp ult i64 %left_length, %right_length");
        builder.AppendLine("  %min_length = select i1 %left_shorter, i64 %left_length, i64 %right_length");
        builder.AppendLine("  br label %loop_header");
        builder.AppendLine();
        builder.AppendLine("loop_header:");
        builder.AppendLine("  %textord_index = phi i64 [ 0, %entry ], [ %textord_next, %loop_continue ]");
        builder.AppendLine("  %textord_done = icmp eq i64 %textord_index, %min_length");
        builder.AppendLine("  br i1 %textord_done, label %length_compare, label %loop_body");
        builder.AppendLine();
        builder.AppendLine("loop_body:");
        builder.AppendLine($"  %left_unit_ptr = getelementptr inbounds {unitLlvmType}, ptr %left_data, i64 %textord_index");
        builder.AppendLine($"  %right_unit_ptr = getelementptr inbounds {unitLlvmType}, ptr %right_data, i64 %textord_index");
        builder.AppendLine($"  %left_unit = load {unitLlvmType}, ptr %left_unit_ptr");
        builder.AppendLine($"  %right_unit = load {unitLlvmType}, ptr %right_unit_ptr");
        builder.AppendLine($"  %unit_less = icmp ult {unitLlvmType} %left_unit, %right_unit");
        builder.AppendLine("  br i1 %unit_less, label %return_less, label %check_greater");
        builder.AppendLine();
        builder.AppendLine("check_greater:");
        builder.AppendLine($"  %unit_greater = icmp ugt {unitLlvmType} %left_unit, %right_unit");
        builder.AppendLine("  br i1 %unit_greater, label %return_greater, label %loop_continue");
        builder.AppendLine();
        builder.AppendLine("loop_continue:");
        builder.AppendLine("  %textord_next = add i64 %textord_index, 1");
        builder.AppendLine("  br label %loop_header");
        builder.AppendLine();
        builder.AppendLine("length_compare:");
        builder.AppendLine("  %length_equal = icmp eq i64 %left_length, %right_length");
        builder.AppendLine("  br i1 %length_equal, label %return_equal, label %length_decide");
        builder.AppendLine();
        builder.AppendLine("length_decide:");
        builder.AppendLine("  %length_less = icmp ult i64 %left_length, %right_length");
        builder.AppendLine("  br i1 %length_less, label %return_less, label %return_greater");
        builder.AppendLine();
        builder.AppendLine("return_less:");
        builder.AppendLine("  ret i32 -1");
        builder.AppendLine();
        builder.AppendLine("return_greater:");
        builder.AppendLine("  ret i32 1");
        builder.AppendLine();
        builder.AppendLine("return_equal:");
        builder.AppendLine("  ret i32 0");
        builder.AppendLine("}");
    }

    private void EmitFixedArrayOrderedComparisonHelperDefinition(StringBuilder builder, StarkTypeSymbol fixedArrayType)
    {
        if (fixedArrayType.Kind != StarkTypeKind.FixedArray
            || fixedArrayType.ElementType is null
            || fixedArrayType.FixedLength is not int fixedLength)
        {
            throw new InvalidOperationException($"Fixed-array ordered comparison helper requires a fixed array type, but found '{fixedArrayType.DisplayName}'.");
        }

        var helperName = GetFixedArrayOrderedComparisonHelperName(fixedArrayType);
        var arrayLlvmType = MapType(fixedArrayType);

        builder.AppendLine($"define internal i32 @{EscapeIdentifier(helperName)}({arrayLlvmType} %left, {arrayLlvmType} %right) {{");
        builder.AppendLine("entry:");
        if (fixedLength == 0)
        {
            builder.AppendLine("  ret i32 0");
            builder.AppendLine("}");
            return;
        }

        builder.AppendLine("  br label %compare_0");

        for (var index = 0; index < fixedLength; index++)
        {
            EmitFixedArrayOrderedComparisonElement(
                builder,
                fixedArrayType,
                index,
                index == fixedLength - 1);
        }

        builder.AppendLine("return_equal:");
        builder.AppendLine("  ret i32 0");
        builder.AppendLine("return_less:");
        builder.AppendLine("  ret i32 -1");
        builder.AppendLine("return_greater:");
        builder.AppendLine("  ret i32 1");
        builder.AppendLine("}");
    }

    private void EmitScalarizedNamedAggregateOrderedComparisonHelperDefinition(
        StringBuilder builder,
        StarkTypeSymbol aggregateType)
    {
        if (aggregateType.Kind != StarkTypeKind.Named
            || !SupportsScalarizedAggregateOrderedComparison(aggregateType))
        {
            throw new InvalidOperationException(
                $"Named aggregate ordered comparison helper requires a scalarizable named aggregate type, but found '{aggregateType.DisplayName}'.");
        }

        if (!TryGetScalarizableAggregateLeaves(
                aggregateType,
                requireRepresentationPreserving: false,
                ignoreScalarizationThresholds: true,
                allowTextLeaves: true,
                allowSliceLeaves: false,
                out var leaves))
        {
            throw new InvalidOperationException(
                $"Named aggregate ordered comparison helper requires a scalarizable aggregate shape for '{aggregateType.DisplayName}'.");
        }

        var helperName = GetScalarizedAggregateOrderedComparisonHelperName(aggregateType);
        var aggregateLlvmType = MapType(aggregateType);

        builder.AppendLine($"define internal i32 @{EscapeIdentifier(helperName)}({aggregateLlvmType} %left, {aggregateLlvmType} %right) {{");
        builder.AppendLine("entry:");
        if (leaves.Count == 0)
        {
            builder.AppendLine("  ret i32 0");
            builder.AppendLine("}");
            return;
        }

        builder.AppendLine("  br label %compare_0");

        for (var index = 0; index < leaves.Count; index++)
        {
            EmitScalarizedNamedAggregateOrderedComparisonLeaf(
                builder,
                aggregateType,
                leaves[index],
                index,
                index == leaves.Count - 1);
        }

        builder.AppendLine("return_equal:");
        builder.AppendLine("  ret i32 0");
        builder.AppendLine("return_less:");
        builder.AppendLine("  ret i32 -1");
        builder.AppendLine("return_greater:");
        builder.AppendLine("  ret i32 1");
        builder.AppendLine("}");
    }

    private void EmitScalarizedNamedAggregateOrderedComparisonLeaf(
        StringBuilder builder,
        StarkTypeSymbol rootType,
        AggregateScalarLeaf leaf,
        int index,
        bool isLastElement)
    {
        var compareBlock = $"compare_{index}";
        var checkGreaterBlock = $"check_greater_{index}";
        var nextBlock = isLastElement ? "return_equal" : $"compare_{index + 1}";

        builder.AppendLine();
        builder.AppendLine($"{compareBlock}:");
        var leftValue = EmitAggregateLeafValueExtraction(builder, rootType, "%left", leaf.Indices, $"namedcmp_left_{index}");
        var rightValue = EmitAggregateLeafValueExtraction(builder, rootType, "%right", leaf.Indices, $"namedcmp_right_{index}");

        if (TryEmitOrderedComparisonValue(
                builder,
                leaf.Type,
                leftValue,
                rightValue,
                index,
                checkGreaterBlock,
                nextBlock))
        {
            return;
        }

        throw new UnsupportedBodyEmissionException(
            $"Unsupported ordered comparison leaf type '{leaf.Type.DisplayName}' in named aggregate helper.");
    }

    private void EmitFixedArrayOrderedComparisonElement(
        StringBuilder builder,
        StarkTypeSymbol rootType,
        int index,
        bool isLastElement)
    {
        var elementType = rootType.ElementType
            ?? throw new InvalidOperationException($"Fixed-array ordered comparison helper requires a comparable element at index {index} for '{rootType.DisplayName}'.");
        var compareBlock = $"compare_{index}";
        var checkGreaterBlock = $"check_greater_{index}";
        var nextBlock = isLastElement ? "return_equal" : $"compare_{index + 1}";

        builder.AppendLine();
        builder.AppendLine($"{compareBlock}:");
        builder.AppendLine($"  %fixedcmp_left_{index} = extractvalue {MapType(rootType)} %left, {index}");
        builder.AppendLine($"  %fixedcmp_right_{index} = extractvalue {MapType(rootType)} %right, {index}");

        if (TryEmitOrderedComparisonValue(
                builder,
                elementType,
                $"%fixedcmp_left_{index}",
                $"%fixedcmp_right_{index}",
                index,
                checkGreaterBlock,
                nextBlock))
        {
            return;
        }

        throw new UnsupportedBodyEmissionException(
            $"Unsupported ordered comparison element type '{elementType.DisplayName}' in fixed-array helper.");
    }

    private bool TryEmitOrderedComparisonValue(
        StringBuilder builder,
        StarkTypeSymbol operandType,
        string left,
        string right,
        int index,
        string checkGreaterBlock,
        string nextBlock)
    {
        switch (operandType.Kind)
        {
            case StarkTypeKind.Integer when operandType.BitWidth is not null:
            {
                builder.AppendLine($"  %fixedcmp_less_{index} = icmp slt {MapType(operandType)} {left}, {right}");
                builder.AppendLine($"  br i1 %fixedcmp_less_{index}, label %return_less, label %{checkGreaterBlock}");
                builder.AppendLine();
                builder.AppendLine($"{checkGreaterBlock}:");
                builder.AppendLine($"  %fixedcmp_greater_{index} = icmp sgt {MapType(operandType)} {left}, {right}");
                builder.AppendLine($"  br i1 %fixedcmp_greater_{index}, label %return_greater, label %{nextBlock}");
                return true;
            }
            case StarkTypeKind.Float:
            {
                builder.AppendLine($"  %fixedcmp_less_{index} = fcmp olt {MapType(operandType)} {left}, {right}");
                builder.AppendLine($"  br i1 %fixedcmp_less_{index}, label %return_less, label %{checkGreaterBlock}");
                builder.AppendLine();
                builder.AppendLine($"{checkGreaterBlock}:");
                builder.AppendLine($"  %fixedcmp_greater_{index} = fcmp ogt {MapType(operandType)} {left}, {right}");
                builder.AppendLine($"  br i1 %fixedcmp_greater_{index}, label %return_greater, label %{nextBlock}");
                return true;
            }
            case StarkTypeKind.Bool:
            case StarkTypeKind.RawPointer:
            {
                var compareType = operandType.Kind == StarkTypeKind.RawPointer ? "ptr" : MapType(operandType);
                builder.AppendLine($"  %fixedcmp_less_{index} = icmp ult {compareType} {left}, {right}");
                builder.AppendLine($"  br i1 %fixedcmp_less_{index}, label %return_less, label %{checkGreaterBlock}");
                builder.AppendLine();
                builder.AppendLine($"{checkGreaterBlock}:");
                builder.AppendLine($"  %fixedcmp_greater_{index} = icmp ugt {compareType} {left}, {right}");
                builder.AppendLine($"  br i1 %fixedcmp_greater_{index}, label %return_greater, label %{nextBlock}");
                return true;
            }
            case StarkTypeKind.Ascii:
            case StarkTypeKind.Unicode:
            {
                var helperName = operandType.Kind == StarkTypeKind.Ascii
                    ? AsciiCompareHelperName
                    : UnicodeCompareHelperName;
                var compareResult = $"%fixedcmp_text_{index}";
                builder.AppendLine($"  {compareResult} = call i32 @{EscapeIdentifier(helperName)}({MapType(operandType)} {left}, {MapType(operandType)} {right})");
                builder.AppendLine($"  %fixedcmp_less_{index} = icmp slt i32 {compareResult}, 0");
                builder.AppendLine($"  br i1 %fixedcmp_less_{index}, label %return_less, label %{checkGreaterBlock}");
                builder.AppendLine();
                builder.AppendLine($"{checkGreaterBlock}:");
                builder.AppendLine($"  %fixedcmp_greater_{index} = icmp sgt i32 {compareResult}, 0");
                builder.AppendLine($"  br i1 %fixedcmp_greater_{index}, label %return_greater, label %{nextBlock}");
                return true;
            }
            case StarkTypeKind.FixedArray when operandType.ElementType is not null && operandType.FixedLength is int:
            {
                var helperName = GetFixedArrayOrderedComparisonHelperName(operandType);
                var compareResult = $"%fixedcmp_nested_{index}";
                builder.AppendLine($"  {compareResult} = call i32 @{EscapeIdentifier(helperName)}({MapType(operandType)} {left}, {MapType(operandType)} {right})");
                builder.AppendLine($"  %fixedcmp_less_{index} = icmp slt i32 {compareResult}, 0");
                builder.AppendLine($"  br i1 %fixedcmp_less_{index}, label %return_less, label %{checkGreaterBlock}");
                builder.AppendLine();
                builder.AppendLine($"{checkGreaterBlock}:");
                builder.AppendLine($"  %fixedcmp_greater_{index} = icmp sgt i32 {compareResult}, 0");
                builder.AppendLine($"  br i1 %fixedcmp_greater_{index}, label %return_greater, label %{nextBlock}");
                return true;
            }
            default:
                return false;
        }
    }

    private void EmitIntegerExponentHelperDefinition(StringBuilder builder, int bitWidth)
    {
        var integerType = StarkTypeSymbols.Integer(bitWidth);
        var llvmType = MapType(integerType);
        var helperName = GetIntegerExponentHelperName(bitWidth);

        builder.AppendLine($"define internal {llvmType} @{EscapeIdentifier(helperName)}({llvmType} %base, {llvmType} %exponent) {{");
        builder.AppendLine("entry:");
        builder.AppendLine($"  %negative = icmp slt {llvmType} %exponent, 0");
        builder.AppendLine("  br i1 %negative, label %return_zero, label %loop_header");
        builder.AppendLine();
        builder.AppendLine("loop_header:");
        builder.AppendLine($"  %pow_result = phi {llvmType} [ 1, %entry ], [ %pow_next, %loop_body ]");
        builder.AppendLine($"  %pow_exp = phi {llvmType} [ %exponent, %entry ], [ %pow_exp_next, %loop_body ]");
        builder.AppendLine($"  %pow_done = icmp eq {llvmType} %pow_exp, 0");
        builder.AppendLine("  br i1 %pow_done, label %return_result, label %loop_body");
        builder.AppendLine();
        builder.AppendLine("loop_body:");
        builder.AppendLine($"  %pow_next = mul {llvmType} %pow_result, %base");
        builder.AppendLine($"  %pow_exp_next = sub {llvmType} %pow_exp, 1");
        builder.AppendLine("  br label %loop_header");
        builder.AppendLine();
        builder.AppendLine("return_zero:");
        builder.AppendLine($"  ret {llvmType} 0");
        builder.AppendLine();
        builder.AppendLine("return_result:");
        builder.AppendLine($"  ret {llvmType} %pow_result");
        builder.AppendLine("}");
    }

    private string BuildSystemMathIntrinsicDeclaration(
        TypedFunctionSignature function,
        SystemMathBuiltinKind builtinKind)
    {
        var arity = GetSystemMathIntrinsicArity(builtinKind);
        var scalarType = ValidateSystemMathBuiltinSignature(function, builtinKind, arity);
        var intrinsicName = $"@llvm.{GetSystemMathIntrinsicBaseName(builtinKind)}.{GetFloatIntrinsicSuffix(scalarType)}";
        var llvmType = MapType(scalarType);

        if (builtinKind == SystemMathBuiltinKind.SinCos)
        {
            var pairType = $"{{ {llvmType}, {llvmType} }}";
            return $"declare {pairType} {intrinsicName}({llvmType})";
        }

        return $"declare {llvmType} {intrinsicName}({string.Join(", ", Enumerable.Repeat(llvmType, arity))})";
    }

    private string BuildSystemBitOperationsIntrinsicDeclaration(
        TypedFunctionSignature function,
        SystemBitOperationsBuiltinKind builtinKind)
    {
        var surfaceArity = GetSystemBitOperationsSurfaceArity(builtinKind);
        var scalarType = ValidateSystemBitOperationsBuiltinSignature(function, builtinKind, surfaceArity);
        var intrinsicName = $"@llvm.{GetSystemBitOperationsIntrinsicBaseName(builtinKind)}.i{scalarType.BitWidth}";
        var llvmType = MapType(scalarType);

        return builtinKind switch
        {
            SystemBitOperationsBuiltinKind.LeadingZeroCount or SystemBitOperationsBuiltinKind.TrailingZeroCount
                => $"declare {llvmType} {intrinsicName}({llvmType}, i1 immarg)",
            SystemBitOperationsBuiltinKind.PopCount
                => $"declare {llvmType} {intrinsicName}({llvmType})",
            SystemBitOperationsBuiltinKind.RotateLeft or SystemBitOperationsBuiltinKind.RotateRight
                => $"declare {llvmType} {intrinsicName}({llvmType}, {llvmType}, {llvmType})",
            _ => throw new InvalidOperationException($"Unsupported System.BitOperations builtin '{builtinKind}'.")
        };
    }

    private bool UsesLifetimeMarkers()
    {
        return _ssa.Functions
            .SelectMany(static function => function.Blocks)
            .SelectMany(static block => block.Instructions)
            .Any(static instruction => instruction is SsaLifetimeStartInstruction or SsaLifetimeEndInstruction);
    }

    private bool UsesMemcpyInlineIntrinsic()
    {
        return _ssa.Functions
            .SelectMany(static function => function.Blocks)
            .SelectMany(static block => block.Instructions)
            .OfType<SsaCopyMemoryInstruction>()
            .Any(copy => TryGetConcreteTypeLayout(copy.CopyType) is { } layout && layout.SizeBytes > AggregateMemcpyThresholdBytes)
            || UsesBuiltinMemcpyInlineIntrinsic();
    }

    private bool UsesBuiltinMemcpyInlineIntrinsic()
    {
        return false;
    }

    private bool UsesMemsetInlineIntrinsic()
    {
        return _ssa.Functions
            .SelectMany(static function => function.Blocks)
            .SelectMany(static block => block.Instructions)
            .Any(UsesLargeZeroInitializedAggregateStore);
    }

    private bool UsesHeapAllocator()
    {
        return _ssa.Functions
            .SelectMany(static function => function.Blocks)
            .SelectMany(static block => block.Instructions)
            .Any(static instruction => instruction is SsaAllocateLocalInstruction { StorageClass: "heap" }
                or SsaDeallocateLocalInstruction { StorageClass: "heap" });
    }

    private string GetAllocatorSizeType()
    {
        var pointerSizeBytes = TryGetTargetPointerSizeBytes() ?? 8;
        return $"i{pointerSizeBytes * 8}";
    }

    private bool UsesLargeZeroInitializedAggregateStore(SsaInstruction instruction)
    {
        return instruction switch
        {
            SsaStoreLocalInstruction { LocalType: var type, Value: SsaZeroInitializerValue } => ShouldUseInlineAggregateZeroFill(type),
            SsaStoreIndirectInstruction { ValueType: var type, Value: SsaZeroInitializerValue } => ShouldUseInlineAggregateZeroFill(type),
            _ => false
        };
    }

    private bool ShouldUseInlineAggregateZeroFill(StarkTypeSymbol valueType)
    {
        var normalizedType = valueType with
        {
            BorrowKind = StarkBorrowKind.None,
            AccessKind = StarkAccessKind.None,
            InitializationKind = StarkInitializationKind.None,
            IsMutableView = false
        };

        if (TryGetConcreteTypeLayout(normalizedType) is not { } layout
            || layout.SizeBytes <= AggregateScalarizationThresholdBytes)
        {
            return false;
        }

        return normalizedType.Kind is StarkTypeKind.FixedArray or StarkTypeKind.Named;
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
            builder.Append($"@{constant.SymbolName} = private unnamed_addr constant {constant.ArrayType} {constant.Initializer}");
            if (constant.AlignmentBytes > 1)
            {
                builder.Append($", align {constant.AlignmentBytes}");
            }

            builder.AppendLine();
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
        FunctionMemoryEffectSummary? memoryEffects,
        SsaFunction ssaFunction,
        IReadOnlyDictionary<string, ParameterMemoryEffectSummary>? parameterEffects,
        Func<string, string, AbiFunctionSignature?> resolveCallAbi,
        MonomorphizationLinkageKind? specializationLinkage = null)
    {
        var functionBuilder = new StringBuilder();
        var effectiveMemoryEffects = AdjustDefinitionMemoryEffectsForAbiLowering(memoryEffects, ssaFunction, resolveCallAbi);
        var debugFunction = TryCreateDebugFunctionContext(function, abiFunction, ssaFunction);
        functionBuilder.AppendLine(AppendFunctionDebugScope(
            BuildDefinitionSignature(internalize, function, abiFunction, effects, effectiveMemoryEffects, parameterEffects, specializationLinkage),
            debugFunction));
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
            TryGetConcreteTypeLayout,
            ResolveNamedTypeSymbol,
            _enumLayoutModel.Layouts,
            GetAllocatorSizeType(),
            debugFunction);
        bodyEmitter.Emit();
        functionBuilder.AppendLine("}");
        builder.Append(functionBuilder);
    }

    private DebugFunctionContext? TryCreateDebugFunctionContext(
        TypedFunctionSignature function,
        AbiFunctionSignature abiFunction,
        SsaFunction ssaFunction)
    {
        if (!_debugInfo.Enabled || !ssaFunction.HasBody)
        {
            return null;
        }

        var location = ResolveDebugLocation(
            ssaFunction.Location
            ?? (_functionLocations.TryGetValue(function.Name, out var functionLocation) ? functionLocation : null));
        return _debugInfo.CreateFunctionContext(function.DisplaySourceName, abiFunction.SymbolName, location, function);
    }

    private string AppendFunctionDebugScope(string signature, DebugFunctionContext? debugFunction)
    {
        return debugFunction is null
            ? signature
            : $"{signature} !dbg {debugFunction.SubprogramRef}";
    }

    private SourceLocation ResolveDebugLocation(SourceLocation? location)
    {
        var filePath = string.IsNullOrWhiteSpace(location?.FilePath)
            ? _input.FilePath ?? $"{_syntaxModel.ModuleName}.stark"
            : location!.FilePath!;
        var line = location is { Line: > 0 } ? location.Line : 1;
        var column = location is { Column: > 0 } ? location.Column : 1;
        return new SourceLocation(filePath, line, column);
    }

    private FunctionMemoryEffectSummary? AdjustDefinitionMemoryEffectsForAbiLowering(
        FunctionMemoryEffectSummary? memoryEffects,
        SsaFunction ssaFunction,
        Func<string, string, AbiFunctionSignature?> resolveCallAbi)
    {
        if (!RequiresSyntheticStackTemporaries(ssaFunction, resolveCallAbi))
        {
            return memoryEffects;
        }

        return (memoryEffects ?? new FunctionMemoryEffectSummary(
                ReadsArgumentMemory: false,
                WritesArgumentMemory: false,
                CapturesArgumentMemory: false))
            with
            {
                ReadsOtherMemory = true,
                WritesOtherMemory = true
            };
    }

    private static bool RequiresSyntheticStackTemporaries(
        SsaFunction ssaFunction,
        Func<string, string, AbiFunctionSignature?> resolveCallAbi)
    {
        foreach (var call in ssaFunction.Blocks
                     .SelectMany(static block => block.Instructions)
                     .OfType<SsaValueInstruction>()
                     .Select(static instruction => instruction.Value)
                     .OfType<SsaCallRValue>())
        {
            var calleeAbi = resolveCallAbi(ssaFunction.Name, call.FunctionName);
            if (calleeAbi is null)
            {
                continue;
            }

            if (calleeAbi.ReturnsIndirect
                || calleeAbi.UserParameters.Any(AbiLoweringHeuristics.IsByValueIndirectParameter))
            {
                return true;
            }
        }

        return false;
    }

    private bool TryEmitAsmFunctionDefinition(
        StringBuilder builder,
        bool internalize,
        TypedFunctionSignature function,
        AbiFunctionSignature abiFunction,
        FunctionEffectProfile effects,
        FunctionMemoryEffectSummary? memoryEffects,
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
        functionBuilder.AppendLine(BuildDefinitionSignature(internalize, function, abiFunction, effects, memoryEffects, parameterEffects));
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
        FunctionMemoryEffectSummary? memoryEffects,
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

        var attributes = BuildFunctionAttributes(abiFunction, effects, memoryEffects);
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
        FunctionMemoryEffectSummary? memoryEffects,
        IReadOnlyDictionary<string, ParameterMemoryEffectSummary>? parameterEffects,
        MonomorphizationLinkageKind? specializationLinkage = null)
    {
        var segments = new List<string> { "define" };

        if (ResolveDefinitionLinkageKeyword(internalize, specializationLinkage) is { } linkageKeyword)
        {
            segments.Add(linkageKeyword);
        }

        if (effects.UseFastCallingConvention)
        {
            segments.Add("fastcc");
        }

        segments.Add(MapType(abiFunction.LlvmReturnType));
        segments.Add($"@{EscapeIdentifier(abiFunction.SymbolName)}({string.Join(", ", abiFunction.Parameters.Select(parameter => RenderAbiParameter(parameter, includeName: true, parameterEffects)))})");

        var attributes = BuildFunctionAttributes(abiFunction, effects, memoryEffects);
        if (!string.IsNullOrWhiteSpace(attributes))
        {
            segments.Add(attributes);
        }

        if (specializationLinkage == MonomorphizationLinkageKind.LinkOnceOdrComdat)
        {
            segments.Add("comdat");
        }

        return string.Join(" ", segments);
    }

    private static string? ResolveDefinitionLinkageKeyword(
        bool internalize,
        MonomorphizationLinkageKind? specializationLinkage)
    {
        if (internalize)
        {
            return "internal";
        }

        return specializationLinkage == MonomorphizationLinkageKind.LinkOnceOdrComdat
            ? "linkonce_odr"
            : null;
    }

    private bool TryEmitBuiltinFunctionDefinition(
        StringBuilder builder,
        bool internalize,
        string moduleName,
        TypedFunctionSignature function,
        AbiFunctionSignature abiFunction,
        FunctionEffectProfile effects,
        FunctionMemoryEffectSummary? memoryEffects,
        IReadOnlyDictionary<string, ParameterMemoryEffectSummary>? parameterEffects)
    {
        if (TryResolveSystemMathBuiltin(moduleName, function, out var systemMathBuiltinKind))
        {
            builder.AppendLine(BuildDefinitionSignature(internalize, function, abiFunction, effects, memoryEffects, parameterEffects) + " {");
            EmitSystemMathBuiltin(builder, function, abiFunction, systemMathBuiltinKind);
            builder.AppendLine("}");
            return true;
        }

        if (TryResolveSystemBitOperationsBuiltin(moduleName, function, out var systemBitOperationsBuiltinKind))
        {
            builder.AppendLine(BuildDefinitionSignature(internalize, function, abiFunction, effects, memoryEffects, parameterEffects) + " {");
            EmitSystemBitOperationsBuiltin(builder, function, abiFunction, systemBitOperationsBuiltinKind);
            builder.AppendLine("}");
            return true;
        }

        if (!TryGetSystemTextBuiltin(moduleName, function.Name, out var builtinKind))
        {
            return false;
        }

        if (!string.Equals(_syntaxModel.ModuleName, "System.Text", StringComparison.Ordinal)
            && builtinKind is SystemTextBuiltinKind.TryConcatAscii or SystemTextBuiltinKind.TryConcatUnicode)
        {
            return false;
        }

        builder.AppendLine(BuildDefinitionSignature(internalize, function, abiFunction, effects, memoryEffects, parameterEffects) + " {");
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

    private void EmitSystemMathBuiltin(
        StringBuilder builder,
        TypedFunctionSignature function,
        AbiFunctionSignature abiFunction,
        SystemMathBuiltinKind builtinKind)
    {
        var arity = GetSystemMathIntrinsicArity(builtinKind);
        var scalarType = ValidateSystemMathBuiltinSignature(function, builtinKind, arity);

        if (abiFunction.UserParameters.Count != arity)
        {
            throw new InvalidOperationException($"System.Math builtin '{abiFunction.Name}' expects exactly {arity} user parameter(s).");
        }

        if (IsHardwareAsmSystemMathBuiltin(builtinKind))
        {
            EmitSystemMathHardwareBuiltin(builder, function, abiFunction, builtinKind, scalarType);
            return;
        }

        var llvmType = MapType(scalarType);
        var intrinsicName = $"@llvm.{GetSystemMathIntrinsicBaseName(builtinKind)}.{GetFloatIntrinsicSuffix(scalarType)}";

        if (builtinKind == SystemMathBuiltinKind.SinCos)
        {
            EmitSystemMathSinCosBuiltin(builder, function, abiFunction, intrinsicName, scalarType);
            return;
        }

        builder.AppendLine("entry:");
        if (arity == 1)
        {
            var value = $"%{EscapeIdentifier(abiFunction.UserParameters[0].LlvmName)}";
            builder.AppendLine($"  %math_result = call {llvmType} {intrinsicName}({llvmType} {value})");
            builder.AppendLine($"  ret {llvmType} %math_result");
            return;
        }

        var left = $"%{EscapeIdentifier(abiFunction.UserParameters[0].LlvmName)}";
        var right = $"%{EscapeIdentifier(abiFunction.UserParameters[1].LlvmName)}";
        builder.AppendLine($"  %math_result = call {llvmType} {intrinsicName}({llvmType} {left}, {llvmType} {right})");
        builder.AppendLine($"  ret {llvmType} %math_result");
    }

    private void EmitSystemBitOperationsBuiltin(
        StringBuilder builder,
        TypedFunctionSignature function,
        AbiFunctionSignature abiFunction,
        SystemBitOperationsBuiltinKind builtinKind)
    {
        var surfaceArity = GetSystemBitOperationsSurfaceArity(builtinKind);
        var scalarType = ValidateSystemBitOperationsBuiltinSignature(function, builtinKind, surfaceArity);
        if (abiFunction.UserParameters.Count != surfaceArity)
        {
            throw new InvalidOperationException($"System.BitOperations builtin '{abiFunction.Name}' expects exactly {surfaceArity} user parameter(s).");
        }

        var llvmType = MapType(scalarType);
        var intrinsicName = $"@llvm.{GetSystemBitOperationsIntrinsicBaseName(builtinKind)}.i{scalarType.BitWidth}";
        var value = $"%{EscapeIdentifier(abiFunction.UserParameters[0].LlvmName)}";

        builder.AppendLine("entry:");
        switch (builtinKind)
        {
            case SystemBitOperationsBuiltinKind.LeadingZeroCount:
            case SystemBitOperationsBuiltinKind.TrailingZeroCount:
                builder.AppendLine($"  %bit_result = call {llvmType} {intrinsicName}({llvmType} {value}, i1 false)");
                break;
            case SystemBitOperationsBuiltinKind.PopCount:
                builder.AppendLine($"  %bit_result = call {llvmType} {intrinsicName}({llvmType} {value})");
                break;
            case SystemBitOperationsBuiltinKind.RotateLeft:
            case SystemBitOperationsBuiltinKind.RotateRight:
            {
                var amount = $"%{EscapeIdentifier(abiFunction.UserParameters[1].LlvmName)}";
                builder.AppendLine($"  %bit_result = call {llvmType} {intrinsicName}({llvmType} {value}, {llvmType} {value}, {llvmType} {amount})");
                break;
            }
            default:
                throw new InvalidOperationException($"Unsupported System.BitOperations builtin '{builtinKind}'.");
        }

        builder.AppendLine($"  ret {llvmType} %bit_result");
    }

    private void EmitSystemMathHardwareBuiltin(
        StringBuilder builder,
        TypedFunctionSignature function,
        AbiFunctionSignature abiFunction,
        SystemMathBuiltinKind builtinKind,
        StarkTypeSymbol scalarType)
    {
        if (builtinKind == SystemMathBuiltinKind.FusedMultiplyAdd)
        {
            EmitSystemMathFusedMultiplyAddHardwareBuiltin(builder, function, abiFunction, scalarType);
            return;
        }

        if (abiFunction.UserParameters.Count != 1)
        {
            throw new InvalidOperationException($"System.Math builtin '{function.Name}' expects exactly 1 user parameter.");
        }

        var architecture = StarkAsmArchitectureFacts.ResolveActiveArchitecture(_targetInfo);
        var template = GetSystemMathHardwareAsmTemplate(builtinKind, scalarType, architecture);
        var constraints = GetSystemMathHardwareAsmConstraints(scalarType, architecture);
        var llvmType = MapType(scalarType);
        var value = $"%{EscapeIdentifier(abiFunction.UserParameters[0].LlvmName)}";

        builder.AppendLine("entry:");
        builder.AppendLine(
            $"  %math_result = call {llvmType} asm \"{EscapeInlineAsmString(template)}\", \"{EscapeInlineAsmString(constraints)}\"({llvmType} {value})");
        builder.AppendLine($"  ret {llvmType} %math_result");
    }

    private void EmitSystemMathFusedMultiplyAddHardwareBuiltin(
        StringBuilder builder,
        TypedFunctionSignature function,
        AbiFunctionSignature abiFunction,
        StarkTypeSymbol scalarType)
    {
        if (abiFunction.UserParameters.Count != 3)
        {
            throw new InvalidOperationException($"System.Math builtin '{function.Name}' expects exactly 3 user parameters.");
        }

        var architecture = StarkAsmArchitectureFacts.ResolveActiveArchitecture(_targetInfo);
        var template = GetSystemMathFusedMultiplyAddAsmTemplate(scalarType, architecture);
        var constraints = GetSystemMathFusedMultiplyAddAsmConstraints(scalarType, architecture);
        var llvmType = MapType(scalarType);
        var left = $"%{EscapeIdentifier(abiFunction.UserParameters[0].LlvmName)}";
        var right = $"%{EscapeIdentifier(abiFunction.UserParameters[1].LlvmName)}";
        var addend = $"%{EscapeIdentifier(abiFunction.UserParameters[2].LlvmName)}";

        builder.AppendLine("entry:");
        builder.AppendLine(
            $"  %math_result = call {llvmType} asm \"{EscapeInlineAsmString(template)}\", \"{EscapeInlineAsmString(constraints)}\"({llvmType} {left}, {llvmType} {right}, {llvmType} {addend})");
        builder.AppendLine($"  ret {llvmType} %math_result");
    }

    private static string GetSystemMathFusedMultiplyAddAsmTemplate(
        StarkTypeSymbol scalarType,
        StarkAsmArchitecture architecture)
    {
        return architecture switch
        {
            StarkAsmArchitecture.X86_64 or StarkAsmArchitecture.X86 => scalarType.BitWidth switch
            {
                32 => "vfmadd213ss %xmm2, %xmm1, %xmm0",
                64 => "vfmadd213sd %xmm2, %xmm1, %xmm0",
                _ => throw new InvalidOperationException(
                    $"System.Math FusedMultiplyAdd single-instruction lowering currently supports only f32 and f64, but found '{scalarType.DisplayName}'.")
            },
            StarkAsmArchitecture.AArch64 => scalarType.BitWidth switch
            {
                32 => "fmadd s0, s0, s1, s2",
                64 => "fmadd d0, d0, d1, d2",
                _ => throw new InvalidOperationException(
                    $"System.Math FusedMultiplyAdd single-instruction lowering currently supports only f32 and f64, but found '{scalarType.DisplayName}'.")
            },
            _ => throw new InvalidOperationException(
                $"System.Math builtin '{SystemMathBuiltinKind.FusedMultiplyAdd}' currently has single-instruction lowering only on x86/x64 and aarch64 targets, but the active target is '{DescribeAsmArchitecture(architecture)}'.")
        };
    }

    private static string GetSystemMathFusedMultiplyAddAsmConstraints(
        StarkTypeSymbol scalarType,
        StarkAsmArchitecture architecture)
    {
        return architecture switch
        {
            StarkAsmArchitecture.X86_64 or StarkAsmArchitecture.X86 => "={xmm0},0,{xmm1},{xmm2}",
            StarkAsmArchitecture.AArch64 => scalarType.BitWidth switch
            {
                32 => "={s0},0,{s1},{s2}",
                64 => "={d0},0,{d1},{d2}",
                _ => throw new InvalidOperationException(
                    $"System.Math FusedMultiplyAdd single-instruction lowering currently supports only f32 and f64, but found '{scalarType.DisplayName}'.")
            },
            _ => throw new InvalidOperationException(
                $"System.Math builtin '{SystemMathBuiltinKind.FusedMultiplyAdd}' currently has single-instruction lowering only on x86/x64 and aarch64 targets, but the active target is '{DescribeAsmArchitecture(architecture)}'.")
        };
    }

    private static string GetSystemMathHardwareAsmTemplate(
        SystemMathBuiltinKind builtinKind,
        StarkTypeSymbol scalarType,
        StarkAsmArchitecture architecture)
    {
        return architecture switch
        {
            StarkAsmArchitecture.X86_64 or StarkAsmArchitecture.X86 => GetX86SystemMathHardwareAsmTemplate(builtinKind, scalarType),
            StarkAsmArchitecture.AArch64 => GetAArch64SystemMathHardwareAsmTemplate(builtinKind, scalarType),
            _ => throw new InvalidOperationException(
                $"System.Math builtin '{builtinKind}' currently has single-instruction lowering only on x86/x64 and aarch64 targets, but the active target is '{DescribeAsmArchitecture(architecture)}'.")
        };
    }

    private static string GetSystemMathHardwareAsmConstraints(
        StarkTypeSymbol scalarType,
        StarkAsmArchitecture architecture)
    {
        return architecture switch
        {
            StarkAsmArchitecture.X86_64 or StarkAsmArchitecture.X86 => "={xmm0},0",
            StarkAsmArchitecture.AArch64 => scalarType.BitWidth switch
            {
                32 => "={s0},0",
                64 => "={d0},0",
                _ => throw new InvalidOperationException(
                    $"System.Math single-instruction lowering currently supports only f32 and f64, but found '{scalarType.DisplayName}'.")
            },
            _ => throw new InvalidOperationException(
                $"System.Math single-instruction lowering currently supports only x86/x64 and aarch64 targets, but the active target is '{DescribeAsmArchitecture(architecture)}'.")
        };
    }

    private static string GetX86SystemMathHardwareAsmTemplate(
        SystemMathBuiltinKind builtinKind,
        StarkTypeSymbol scalarType)
    {
        return scalarType.BitWidth switch
        {
            32 => builtinKind switch
            {
                SystemMathBuiltinKind.Sqrt => "sqrtss %xmm0, %xmm0",
                SystemMathBuiltinKind.ReciprocalEstimate => "rcpss %xmm0, %xmm0",
                SystemMathBuiltinKind.ReciprocalSqrtEstimate => "rsqrtss %xmm0, %xmm0",
                SystemMathBuiltinKind.Ceiling => "roundss $$2, %xmm0, %xmm0",
                SystemMathBuiltinKind.Floor => "roundss $$1, %xmm0, %xmm0",
                SystemMathBuiltinKind.Truncate => "roundss $$3, %xmm0, %xmm0",
                SystemMathBuiltinKind.Round => "roundss $$0, %xmm0, %xmm0",
                _ => throw new InvalidOperationException($"Unsupported x86/x64 hardware System.Math builtin '{builtinKind}'.")
            },
            64 => builtinKind switch
            {
                SystemMathBuiltinKind.Sqrt => "sqrtsd %xmm0, %xmm0",
                SystemMathBuiltinKind.Ceiling => "roundsd $$2, %xmm0, %xmm0",
                SystemMathBuiltinKind.Floor => "roundsd $$1, %xmm0, %xmm0",
                SystemMathBuiltinKind.Truncate => "roundsd $$3, %xmm0, %xmm0",
                SystemMathBuiltinKind.Round => "roundsd $$0, %xmm0, %xmm0",
                _ => throw new InvalidOperationException($"Unsupported x86/x64 hardware System.Math builtin '{builtinKind}'.")
            },
            _ => throw new InvalidOperationException(
                $"System.Math single-instruction lowering currently supports only f32 and f64, but found '{scalarType.DisplayName}'.")
        };
    }

    private static string GetAArch64SystemMathHardwareAsmTemplate(
        SystemMathBuiltinKind builtinKind,
        StarkTypeSymbol scalarType)
    {
        var register = scalarType.BitWidth switch
        {
            32 => "s0",
            64 => "d0",
            _ => throw new InvalidOperationException(
                $"System.Math single-instruction lowering currently supports only f32 and f64, but found '{scalarType.DisplayName}'.")
        };

        var opcode = builtinKind switch
        {
            SystemMathBuiltinKind.Sqrt => "fsqrt",
            SystemMathBuiltinKind.ReciprocalEstimate => "frecpe",
            SystemMathBuiltinKind.ReciprocalSqrtEstimate => "frsqrte",
            SystemMathBuiltinKind.Ceiling => "frintp",
            SystemMathBuiltinKind.Floor => "frintm",
            SystemMathBuiltinKind.Truncate => "frintz",
            SystemMathBuiltinKind.Round => "frintn",
            _ => throw new InvalidOperationException($"Unsupported aarch64 hardware System.Math builtin '{builtinKind}'.")
        };

        return $"{opcode} {register}, {register}";
    }

    private void EmitSystemMathSinCosBuiltin(
        StringBuilder builder,
        TypedFunctionSignature function,
        AbiFunctionSignature abiFunction,
        string intrinsicName,
        StarkTypeSymbol scalarType)
    {
        var signature = ValidateSystemMathSinCosBuiltinSignature(function);
        var scalarLlvmType = MapType(scalarType);
        var pairType = $"{{ {scalarLlvmType}, {scalarLlvmType} }}";
        var resultType = MapType(function.ReturnType);
        var value = $"%{EscapeIdentifier(abiFunction.UserParameters[0].LlvmName)}";

        builder.AppendLine("entry:");
        builder.AppendLine($"  %math_pair = call {pairType} {intrinsicName}({scalarLlvmType} {value})");
        builder.AppendLine($"  %math_sin = extractvalue {pairType} %math_pair, 0");
        builder.AppendLine($"  %math_cos = extractvalue {pairType} %math_pair, 1");
        builder.AppendLine($"  %math_with_sin = insertvalue {resultType} zeroinitializer, {scalarLlvmType} %math_sin, {signature.SinFieldIndex}");
        builder.AppendLine($"  %math_result = insertvalue {resultType} %math_with_sin, {scalarLlvmType} %math_cos, {signature.CosFieldIndex}");
        builder.AppendLine($"  ret {resultType} %math_result");
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
        builder.AppendLine("  br i1 %concat_left_nonempty, label %concat_copy_left_loop, label %concat_after_left");
        builder.AppendLine("concat_copy_left_loop:");
        builder.AppendLine("  %concat_left_index = phi i64 [ 0, %concat_copy_left_check ], [ %concat_left_next, %concat_copy_left_loop ]");
        builder.AppendLine($"  %concat_left_src = getelementptr inbounds {unitLlvmType}, ptr %concat_left_data, i64 %concat_left_index");
        builder.AppendLine($"  %concat_left_dst = getelementptr inbounds {unitLlvmType}, ptr %concat_data, i64 %concat_left_index");
        builder.AppendLine($"  %concat_left_unit = load {unitLlvmType}, ptr %concat_left_src");
        builder.AppendLine($"  store {unitLlvmType} %concat_left_unit, ptr %concat_left_dst");
        builder.AppendLine("  %concat_left_next = add i64 %concat_left_index, 1");
        builder.AppendLine("  %concat_left_more = icmp ult i64 %concat_left_next, %concat_left_length");
        builder.AppendLine("  br i1 %concat_left_more, label %concat_copy_left_loop, label %concat_after_left");
        builder.AppendLine("concat_after_left:");
        builder.AppendLine("  %concat_right_nonempty = icmp ne i64 %concat_right_length, 0");
        builder.AppendLine("  br i1 %concat_right_nonempty, label %concat_copy_right_prepare, label %concat_finish");
        builder.AppendLine("concat_copy_right_prepare:");
        builder.AppendLine($"  %concat_right_dest = getelementptr inbounds {unitLlvmType}, ptr %concat_data, i64 %concat_left_length");
        builder.AppendLine("  br label %concat_copy_right_loop");
        builder.AppendLine("concat_copy_right_loop:");
        builder.AppendLine("  %concat_right_index = phi i64 [ 0, %concat_copy_right_prepare ], [ %concat_right_next, %concat_copy_right_loop ]");
        builder.AppendLine($"  %concat_right_src = getelementptr inbounds {unitLlvmType}, ptr %concat_right_data, i64 %concat_right_index");
        builder.AppendLine($"  %concat_right_dst = getelementptr inbounds {unitLlvmType}, ptr %concat_right_dest, i64 %concat_right_index");
        builder.AppendLine($"  %concat_right_unit = load {unitLlvmType}, ptr %concat_right_src");
        builder.AppendLine($"  store {unitLlvmType} %concat_right_unit, ptr %concat_right_dst");
        builder.AppendLine("  %concat_right_next = add i64 %concat_right_index, 1");
        builder.AppendLine("  %concat_right_more = icmp ult i64 %concat_right_next, %concat_right_length");
        builder.AppendLine("  br i1 %concat_right_more, label %concat_copy_right_loop, label %concat_finish");
        builder.AppendLine("concat_finish:");
        builder.AppendLine("  store i64 %concat_required, ptr %concat_length_addr");
        builder.AppendLine("  ret i1 true");
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

    private static bool TryGetSystemMathBuiltin(
        string moduleName,
        string functionName,
        out SystemMathBuiltinKind builtinKind)
    {
        builtinKind = default;

        string sourceName;
        if (functionName.Contains('.', StringComparison.Ordinal))
        {
            const string prefix = "System.Math.";
            if (!functionName.StartsWith(prefix, StringComparison.Ordinal))
            {
                return false;
            }

            sourceName = functionName[prefix.Length..];
        }
        else
        {
            if (!string.Equals(moduleName, "System.Math", StringComparison.Ordinal))
            {
                return false;
            }

            sourceName = functionName;
        }

        builtinKind = sourceName switch
        {
            "Sin" => SystemMathBuiltinKind.Sin,
            "Cos" => SystemMathBuiltinKind.Cos,
            "Tan" => SystemMathBuiltinKind.Tan,
            "Exp" => SystemMathBuiltinKind.Exp,
            "Exp2" => SystemMathBuiltinKind.Exp2,
            "Log" => SystemMathBuiltinKind.Log,
            "Log2" => SystemMathBuiltinKind.Log2,
            "Log10" => SystemMathBuiltinKind.Log10,
            "Asin" => SystemMathBuiltinKind.Asin,
            "Acos" => SystemMathBuiltinKind.Acos,
            "Atan" => SystemMathBuiltinKind.Atan,
            "Atan2" => SystemMathBuiltinKind.Atan2,
            "Pow" => SystemMathBuiltinKind.Pow,
            "Sinh" => SystemMathBuiltinKind.Sinh,
            "Cosh" => SystemMathBuiltinKind.Cosh,
            "Tanh" => SystemMathBuiltinKind.Tanh,
            "SinCos" => SystemMathBuiltinKind.SinCos,
            "Sqrt" => SystemMathBuiltinKind.Sqrt,
            "FusedMultiplyAdd" => SystemMathBuiltinKind.FusedMultiplyAdd,
            "ReciprocalEstimate" => SystemMathBuiltinKind.ReciprocalEstimate,
            "ReciprocalSqrtEstimate" => SystemMathBuiltinKind.ReciprocalSqrtEstimate,
            "Ceiling" => SystemMathBuiltinKind.Ceiling,
            "Floor" => SystemMathBuiltinKind.Floor,
            "Truncate" => SystemMathBuiltinKind.Truncate,
            "Round" => SystemMathBuiltinKind.Round,
            "Min" => SystemMathBuiltinKind.Min,
            "Max" => SystemMathBuiltinKind.Max,
            _ => default
        };

        return sourceName is
            "Sin" or "Cos" or "Tan"
            or "Exp" or "Exp2"
            or "Log" or "Log2" or "Log10"
            or "Asin" or "Acos" or "Atan" or "Atan2"
            or "Pow"
            or "Sinh" or "Cosh" or "Tanh"
            or "SinCos"
            or "Sqrt" or "FusedMultiplyAdd" or "ReciprocalEstimate" or "ReciprocalSqrtEstimate"
            or "Ceiling" or "Floor" or "Truncate" or "Round"
            or "Min" or "Max";
    }

    private static bool TryResolveSystemMathBuiltin(
        string moduleName,
        TypedFunctionSignature function,
        out SystemMathBuiltinKind builtinKind)
    {
        return TryGetSystemMathBuiltin(moduleName, function.DisplaySourceName, out builtinKind)
            || TryGetSystemMathBuiltin(moduleName: string.Empty, function.Name, out builtinKind);
    }

    private static int GetSystemMathIntrinsicArity(SystemMathBuiltinKind builtinKind)
    {
        return builtinKind switch
        {
            SystemMathBuiltinKind.Atan2 or SystemMathBuiltinKind.Pow => 2,
            SystemMathBuiltinKind.FusedMultiplyAdd => 3,
            SystemMathBuiltinKind.Min or SystemMathBuiltinKind.Max => 2,
            _ => 1
        };
    }

    private static bool IsLlvmIntrinsicSystemMathBuiltin(SystemMathBuiltinKind builtinKind)
    {
        return builtinKind is
            SystemMathBuiltinKind.Sin
            or SystemMathBuiltinKind.Cos
            or SystemMathBuiltinKind.Tan
            or SystemMathBuiltinKind.Exp
            or SystemMathBuiltinKind.Exp2
            or SystemMathBuiltinKind.Log
            or SystemMathBuiltinKind.Log2
            or SystemMathBuiltinKind.Log10
            or SystemMathBuiltinKind.Asin
            or SystemMathBuiltinKind.Acos
            or SystemMathBuiltinKind.Atan
            or SystemMathBuiltinKind.Atan2
            or SystemMathBuiltinKind.Pow
            or SystemMathBuiltinKind.Sinh
            or SystemMathBuiltinKind.Cosh
            or SystemMathBuiltinKind.Tanh
            or SystemMathBuiltinKind.SinCos
            or SystemMathBuiltinKind.Min
            or SystemMathBuiltinKind.Max;
    }

    private static bool IsHardwareAsmSystemMathBuiltin(SystemMathBuiltinKind builtinKind)
    {
        return builtinKind is
            SystemMathBuiltinKind.Sqrt
            or SystemMathBuiltinKind.FusedMultiplyAdd
            or SystemMathBuiltinKind.ReciprocalEstimate
            or SystemMathBuiltinKind.ReciprocalSqrtEstimate
            or SystemMathBuiltinKind.Ceiling
            or SystemMathBuiltinKind.Floor
            or SystemMathBuiltinKind.Truncate
            or SystemMathBuiltinKind.Round;
    }

    private static string GetSystemMathIntrinsicBaseName(SystemMathBuiltinKind builtinKind)
    {
        return builtinKind switch
        {
            SystemMathBuiltinKind.Sin => "sin",
            SystemMathBuiltinKind.Cos => "cos",
            SystemMathBuiltinKind.Tan => "tan",
            SystemMathBuiltinKind.Exp => "exp",
            SystemMathBuiltinKind.Exp2 => "exp2",
            SystemMathBuiltinKind.Log => "log",
            SystemMathBuiltinKind.Log2 => "log2",
            SystemMathBuiltinKind.Log10 => "log10",
            SystemMathBuiltinKind.Asin => "asin",
            SystemMathBuiltinKind.Acos => "acos",
            SystemMathBuiltinKind.Atan => "atan",
            SystemMathBuiltinKind.Atan2 => "atan2",
            SystemMathBuiltinKind.Pow => "pow",
            SystemMathBuiltinKind.Sinh => "sinh",
            SystemMathBuiltinKind.Cosh => "cosh",
            SystemMathBuiltinKind.Tanh => "tanh",
            SystemMathBuiltinKind.SinCos => "sincos",
            SystemMathBuiltinKind.Min => "minnum",
            SystemMathBuiltinKind.Max => "maxnum",
            _ => throw new InvalidOperationException($"Unsupported System.Math builtin '{builtinKind}'.")
        };
    }

    private static bool TryGetSystemBitOperationsBuiltin(
        string moduleName,
        string functionName,
        out SystemBitOperationsBuiltinKind builtinKind)
    {
        builtinKind = default;

        string sourceName;
        if (functionName.Contains('.', StringComparison.Ordinal))
        {
            const string prefix = "System.BitOperations.";
            if (!functionName.StartsWith(prefix, StringComparison.Ordinal))
            {
                return false;
            }

            sourceName = functionName[prefix.Length..];
        }
        else
        {
            if (!string.Equals(moduleName, "System.BitOperations", StringComparison.Ordinal))
            {
                return false;
            }

            sourceName = functionName;
        }

        builtinKind = sourceName switch
        {
            "LeadingZeroCount" => SystemBitOperationsBuiltinKind.LeadingZeroCount,
            "TrailingZeroCount" => SystemBitOperationsBuiltinKind.TrailingZeroCount,
            "PopCount" => SystemBitOperationsBuiltinKind.PopCount,
            "RotateLeft" => SystemBitOperationsBuiltinKind.RotateLeft,
            "RotateRight" => SystemBitOperationsBuiltinKind.RotateRight,
            _ => default
        };

        return sourceName is
            "LeadingZeroCount"
            or "TrailingZeroCount"
            or "PopCount"
            or "RotateLeft"
            or "RotateRight";
    }

    private static bool TryResolveSystemBitOperationsBuiltin(
        string moduleName,
        TypedFunctionSignature function,
        out SystemBitOperationsBuiltinKind builtinKind)
    {
        return TryGetSystemBitOperationsBuiltin(moduleName, function.DisplaySourceName, out builtinKind)
            || TryGetSystemBitOperationsBuiltin(moduleName: string.Empty, function.Name, out builtinKind);
    }

    private static int GetSystemBitOperationsSurfaceArity(SystemBitOperationsBuiltinKind builtinKind)
    {
        return builtinKind switch
        {
            SystemBitOperationsBuiltinKind.RotateLeft or SystemBitOperationsBuiltinKind.RotateRight => 2,
            _ => 1
        };
    }

    private static string GetSystemBitOperationsIntrinsicBaseName(SystemBitOperationsBuiltinKind builtinKind)
    {
        return builtinKind switch
        {
            SystemBitOperationsBuiltinKind.LeadingZeroCount => "ctlz",
            SystemBitOperationsBuiltinKind.TrailingZeroCount => "cttz",
            SystemBitOperationsBuiltinKind.PopCount => "ctpop",
            SystemBitOperationsBuiltinKind.RotateLeft => "fshl",
            SystemBitOperationsBuiltinKind.RotateRight => "fshr",
            _ => throw new InvalidOperationException($"Unsupported System.BitOperations builtin '{builtinKind}'.")
        };
    }

    private static string DescribeAsmArchitecture(StarkAsmArchitecture architecture)
    {
        return architecture switch
        {
            StarkAsmArchitecture.X86_64 => "x86_64",
            StarkAsmArchitecture.AArch64 => "aarch64",
            StarkAsmArchitecture.RiscV64 => "riscv64",
            StarkAsmArchitecture.X86 => "x86",
            StarkAsmArchitecture.Arm32 => "arm",
            _ => "unknown"
        };
    }

    private StarkTypeSymbol ValidateSystemMathBuiltinSignature(
        TypedFunctionSignature function,
        SystemMathBuiltinKind builtinKind,
        int arity)
    {
        if (builtinKind == SystemMathBuiltinKind.SinCos)
        {
            return ValidateSystemMathSinCosBuiltinSignature(function).ScalarType;
        }

        if (function.ReturnType.Kind != StarkTypeKind.Float)
        {
            throw new InvalidOperationException($"System.Math builtin '{function.Name}' requires a floating-point return type.");
        }

        if (function.Parameters.Count != arity)
        {
            throw new InvalidOperationException($"System.Math builtin '{function.Name}' expects exactly {arity} parameter(s).");
        }

        foreach (var parameter in function.Parameters)
        {
            if (parameter.Type.Kind != StarkTypeKind.Float
                || parameter.Type.BitWidth != function.ReturnType.BitWidth)
            {
                throw new InvalidOperationException(
                $"System.Math builtin '{function.Name}' requires all parameters to match the floating-point return type '{function.ReturnType.DisplayName}'.");
            }
        }

        if ((builtinKind is SystemMathBuiltinKind.ReciprocalEstimate or SystemMathBuiltinKind.ReciprocalSqrtEstimate)
            && function.ReturnType.BitWidth != 32)
        {
            throw new InvalidOperationException(
                $"System.Math builtin '{function.Name}' currently supports only 'f32' because the shared single-instruction surface is single-precision.");
        }

        return function.ReturnType;
    }

    private StarkTypeSymbol ValidateSystemBitOperationsBuiltinSignature(
        TypedFunctionSignature function,
        SystemBitOperationsBuiltinKind builtinKind,
        int arity)
    {
        if (function.ReturnType.Kind != StarkTypeKind.Integer)
        {
            throw new InvalidOperationException($"System.BitOperations builtin '{function.Name}' requires an integer return type.");
        }

        if (function.ReturnType.BitWidth is not (32 or 64))
        {
            throw new InvalidOperationException(
                $"System.BitOperations builtin '{function.Name}' currently supports only 'i32' and 'i64', but found '{function.ReturnType.DisplayName}'.");
        }

        if (function.Parameters.Count != arity)
        {
            throw new InvalidOperationException($"System.BitOperations builtin '{function.Name}' expects exactly {arity} parameter(s).");
        }

        foreach (var parameter in function.Parameters)
        {
            if (parameter.Type.Kind != StarkTypeKind.Integer
                || parameter.Type.BitWidth != function.ReturnType.BitWidth)
            {
                throw new InvalidOperationException(
                    $"System.BitOperations builtin '{function.Name}' requires all parameters to match the integer return type '{function.ReturnType.DisplayName}'.");
            }
        }

        return function.ReturnType;
    }

    private SystemMathSinCosSignature ValidateSystemMathSinCosBuiltinSignature(TypedFunctionSignature function)
    {
        if (function.Parameters.Count != 1)
        {
            throw new InvalidOperationException($"System.Math builtin '{function.Name}' expects exactly 1 parameter.");
        }

        var scalarType = function.Parameters[0].Type;
        if (scalarType.Kind != StarkTypeKind.Float)
        {
            throw new InvalidOperationException($"System.Math builtin '{function.Name}' requires a floating-point input parameter.");
        }

        var namedType = ResolveNamedTypeSymbol(function.ReturnType);
        if (namedType is null
            || namedType.Kind is not (DeclarationKind.Struct or DeclarationKind.Record)
            || namedType.OrderedFields.Count != 2
            || !namedType.TryGetField("Sin", out var sinField, out var sinFieldIndex)
            || !namedType.TryGetField("Cos", out var cosField, out var cosFieldIndex)
            || sinField.Type.Kind != StarkTypeKind.Float
            || cosField.Type.Kind != StarkTypeKind.Float
            || sinField.Type.BitWidth != scalarType.BitWidth
            || cosField.Type.BitWidth != scalarType.BitWidth)
        {
            throw new InvalidOperationException(
                $"System.Math builtin '{function.Name}' requires a two-field struct/record return type with 'Sin' and 'Cos' fields matching the floating-point parameter type '{scalarType.DisplayName}'.");
        }

        return new SystemMathSinCosSignature(scalarType, sinFieldIndex, cosFieldIndex);
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
            if (AbiLoweringHeuristics.IsByValueIndirectParameter(parameter))
            {
                attributes.Add($"byval({MapType(parameter.SourceType)})");
            }

            attributes.Add("noalias");
            AppendPointerMemoryAccessAttributes(attributes, parameter, parameterEffects);
            AppendCaptureAttribute(attributes, parameterEffects);

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
        AppendCaptureAttribute(attributes, parameterEffects);

        return attributes;
    }

    private IReadOnlyDictionary<string, ParameterMemoryEffectSummary>? GetParameterEffects(string functionName, bool hasBody)
    {
        if (hasBody
            && TryGetRootValidationSummary(functionName, out var validation)
            && validation.Parameters is not null)
        {
            return validation.Parameters.ToDictionary(static parameter => parameter.Name, StringComparer.Ordinal);
        }

        if (!_publishedFunctionSemantics.TryGetValue(functionName, out var imported)
            || imported.Parameters is null)
        {
            return null;
        }

        return imported.Parameters.ToDictionary(static parameter => parameter.Name, StringComparer.Ordinal);
    }

    private FunctionMemoryEffectSummary? GetFunctionMemoryEffects(string functionName, bool hasBody)
    {
        if (hasBody
            && TryGetRootValidationSummary(functionName, out var validation))
        {
            return validation.MemoryEffects;
        }

        return _publishedFunctionSemantics.TryGetValue(functionName, out var imported)
            ? imported.MemoryEffects
            : null;
    }

    private bool TryGetRootValidationSummary(string functionName, out FunctionValidationSummary validation)
    {
        validation = default!;

        if (_semanticValidation is null)
        {
            return false;
        }

        if (_semanticValidation.Functions.TryGetValue(functionName, out validation!))
        {
            return true;
        }

        return _specializationTemplateNames.TryGetValue(functionName, out var templateName)
            && _semanticValidation.Functions.TryGetValue(templateName, out validation!);
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

    private static string BuildFunctionAttributes(
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

        attributes.Add(effects.InlinePreference switch
        {
            InlinePreference.Inline => "alwaysinline",
            InlinePreference.NoInline => "noinline",
            _ => "inlinehint"
        });

        return string.Join(" ", attributes);
    }

    private static string? BuildMemoryAttribute(
        AbiFunctionSignature abiFunction,
        FunctionEffectProfile effects,
        FunctionMemoryEffectSummary? memoryEffects)
    {
        var readsArgumentMemory = memoryEffects?.ReadsArgumentMemory ?? effects.ReadsArgumentMemory;
        var writesArgumentMemory = memoryEffects?.WritesArgumentMemory ?? false;
        if (abiFunction.ReturnsIndirect)
        {
            writesArgumentMemory = true;
        }

        var readsOtherMemory = memoryEffects?.ReadsOtherMemory ?? false;
        var writesOtherMemory = memoryEffects?.WritesOtherMemory ?? false;

        if (memoryEffects is null)
        {
            return effects.IsPure
                ? GetMemoryAttribute(readsArgumentMemory, writesArgumentMemory, readsOtherMemory, writesOtherMemory)
                : readsArgumentMemory || writesArgumentMemory || readsOtherMemory || writesOtherMemory
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

    private static string GetIntegerExponentHelperName(int bitWidth)
    {
        return $"{IntegerExponentHelperNamePrefix}{bitWidth}";
    }

    private static string GetFixedArrayOrderedComparisonHelperName(StarkTypeSymbol fixedArrayType)
    {
        return $"{FixedArrayCompareHelperNamePrefix}{EscapeIdentifier(fixedArrayType.DisplayName)}";
    }

    private static string GetScalarizedAggregateOrderedComparisonHelperName(StarkTypeSymbol aggregateType)
    {
        return $"{ScalarizedAggregateCompareHelperNamePrefix}{EscapeIdentifier(aggregateType.DisplayName)}";
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

        if (CompileTimeExpressionEvaluator.TryEvaluate(expression, out var constant)
            && CompileTimeExpressionEvaluator.TryCoerce(constant, targetType, out var coerced)
            && TryPlanCompileTimeConstant(coerced, targetType, out plan))
        {
            return true;
        }

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

    private bool TryPlanCompileTimeConstant(
        CompileTimeConstant constant,
        StarkTypeSymbol targetType,
        out GlobalInitializerPlan plan)
    {
        plan = null!;
        string rendered;

        switch (constant.Kind)
        {
            case CompileTimeConstantKind.Integer:
                rendered = constant.IntegerValue.ToString();
                break;
            case CompileTimeConstantKind.Float:
                rendered = CompileTimeExpressionEvaluator.FormatFloatLiteral(constant);
                break;
            case CompileTimeConstantKind.Bool:
                rendered = constant.BoolValue ? "true" : "false";
                break;
            case CompileTimeConstantKind.Null:
                rendered = "null";
                break;
            case CompileTimeConstantKind.Text when constant.TextLiteral is not null:
                rendered = FormatGlobalStringConstantValue(constant.TextLiteral, targetType);
                break;
            default:
                return false;
        }

        plan = new GlobalInitializerPlan(rendered, []);
        return true;
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

        constants[key] = new EmittedStringConstant(
            SymbolName: $".str.{index++}",
            ArrayType: key.ArrayType,
            Initializer: key.Initializer,
            DataLength: key.DataLength,
            AlignmentBytes: key.AlignmentBytes);
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
        var normalizedType = type with
        {
            BorrowKind = StarkBorrowKind.None,
            AccessKind = StarkAccessKind.None,
            InitializationKind = StarkInitializationKind.None,
            IsMutableView = false
        };

        if (normalizedType.Kind == StarkTypeKind.Named
            && normalizedType.NamedType is { } namedType
            && normalizedType.TypeArguments is not { Count: > 0 }
            && _publishedConcreteLayouts.TryGetValue(namedType, out var publishedLayout))
        {
            return publishedLayout;
        }

        if (TryGetTargetAwareTypeLayout(normalizedType, new HashSet<string>(StringComparer.Ordinal)) is { } targetAwareLayout)
        {
            return targetAwareLayout;
        }

        return ConcreteTypeLayoutHelper.TryGetConcreteTypeLayout(type, _typeModel.NamedTypes, _enumLayoutModel.Layouts);
    }

    private NamedTypeSymbol? ResolveNamedTypeSymbol(StarkTypeSymbol type)
    {
        return type.NamedType is not null && _typeModel.NamedTypes.TryGetValue(type.NamedType, out var namedType)
            ? namedType
            : null;
    }

    private static StarkTypeSymbol NormalizeAggregateType(StarkTypeSymbol type)
    {
        return type with
        {
            BorrowKind = StarkBorrowKind.None,
            AccessKind = StarkAccessKind.None,
            InitializationKind = StarkInitializationKind.None,
            IsMutableView = false
        };
    }

    private StarkTypeSymbol? GetAggregateElementType(StarkTypeSymbol type, int index)
    {
        var normalizedType = NormalizeAggregateType(type);
        return normalizedType.Kind switch
        {
            StarkTypeKind.FixedArray when normalizedType.ElementType is not null => normalizedType.ElementType,
            StarkTypeKind.Named when ResolveNamedTypeSymbol(normalizedType) is { } namedType
                                       && TryGetScalarizableNamedAggregateFields(namedType, out var orderedFields)
                                       && index >= 0
                                       && index < orderedFields.Count
                => orderedFields[index].Type,
            _ => null
        };
    }

    private bool TryGetScalarizableNamedAggregateFields(
        NamedTypeSymbol namedType,
        out IReadOnlyList<FieldSymbol> orderedFields)
    {
        if (namedType.Kind is DeclarationKind.Struct or DeclarationKind.Record)
        {
            orderedFields = namedType.OrderedFields;
            return true;
        }

        if (namedType.Kind == DeclarationKind.Enum
            && _enumLayoutModel.Layouts.TryGetValue(namedType.Name, out var enumLayout))
        {
            orderedFields = enumLayout.OrderedFields;
            return true;
        }

        orderedFields = Array.Empty<FieldSymbol>();
        return false;
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
        private readonly Func<StarkTypeSymbol, NamedTypeSymbol?> _resolveNamedTypeSymbol;
        private readonly IReadOnlyDictionary<string, EnumLayoutSymbol> _enumLayouts;
        private readonly string _allocatorSizeType;
        private readonly DebugFunctionContext? _debugFunction;
        private readonly HashSet<string> _referencedValueNames;
        private readonly HashSet<string> _allocatedLocalSlots = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _localStorageClasses;
        private readonly Dictionary<string, string> _materializedParameters = new(StringComparer.Ordinal);
        private SourceLocation? _currentDebugLocation;
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
            Func<StarkTypeSymbol, ConcreteTypeLayout?> tryGetConcreteTypeLayout,
            Func<StarkTypeSymbol, NamedTypeSymbol?> resolveNamedTypeSymbol,
            IReadOnlyDictionary<string, EnumLayoutSymbol> enumLayouts,
            string allocatorSizeType,
            DebugFunctionContext? debugFunction)
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
            _resolveNamedTypeSymbol = resolveNamedTypeSymbol;
            _enumLayouts = enumLayouts;
            _allocatorSizeType = allocatorSizeType;
            _debugFunction = debugFunction;
            _referencedValueNames = CollectReferencedValueNames(ssaFunction);
            _localStorageClasses = CollectLocalStorageClasses(ssaFunction);
        }

        public void Emit()
        {
            if (_ssaFunction.Blocks.Count == 0)
            {
                _currentDebugLocation = _ssaFunction.Location;
                EmitFallbackTerminal();
                return;
            }

            foreach (var block in _ssaFunction.Blocks)
            {
                AppendLine($"{FormatBlockLabel(block.Id)}:");

                if (block.Id == _ssaFunction.EntryBlockId)
                {
                    _currentDebugLocation = _ssaFunction.Location;
                    EmitEntryParameterMaterialization();
                    EmitEntryParameterDebugInfo();
                }

                foreach (var phi in block.Phis)
                {
                    _currentDebugLocation = phi.Location ?? _ssaFunction.Location;
                    EmitPhi(phi);
                }

                foreach (var instruction in block.Instructions)
                {
                    _currentDebugLocation = GetInstructionLocation(instruction) ?? _ssaFunction.Location;
                    EmitInstruction(instruction);
                }

                _currentDebugLocation = block.Terminator.Location ?? _ssaFunction.Location;
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
                case SsaDeallocateLocalInstruction deallocateLocal:
                    EmitDeallocateLocal(deallocateLocal);
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

            if (binary.Operator == SsaBinaryOperator.Exponent)
            {
                if (binary.Type.Kind == StarkTypeKind.Float)
                {
                    EmitFloatExponent(result, binary);
                    return;
                }

                if (binary.Type.Kind == StarkTypeKind.Integer)
                {
                    EmitIntegerExponent(result, binary);
                    return;
                }

                throw new UnsupportedBodyEmissionException(
                    $"Unsupported exponent operator type '{binary.Type.DisplayName}'.");
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
            }

            if (binary.Type.Kind == StarkTypeKind.Bool)
            {
                if (binary.Left.Type.Kind == StarkTypeKind.Integer || binary.Left.Type.Kind == StarkTypeKind.Bool)
                {
                    var predicate = binary.Operator switch
                    {
                        SsaBinaryOperator.Equal => "eq",
                        SsaBinaryOperator.NotEqual => "ne",
                        SsaBinaryOperator.LessThan => binary.Left.Type.Kind == StarkTypeKind.Bool ? "ult" : "slt",
                        SsaBinaryOperator.LessThanOrEqual => binary.Left.Type.Kind == StarkTypeKind.Bool ? "ule" : "sle",
                        SsaBinaryOperator.GreaterThan => binary.Left.Type.Kind == StarkTypeKind.Bool ? "ugt" : "sgt",
                        SsaBinaryOperator.GreaterThanOrEqual => binary.Left.Type.Kind == StarkTypeKind.Bool ? "uge" : "sge",
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
                        SsaBinaryOperator.LessThan => "ult",
                        SsaBinaryOperator.LessThanOrEqual => "ule",
                        SsaBinaryOperator.GreaterThan => "ugt",
                        SsaBinaryOperator.GreaterThanOrEqual => "uge",
                        _ => string.Empty
                    };

                    if (!string.IsNullOrEmpty(predicate))
                    {
                        AppendLine($"  {result} = icmp {predicate} ptr {FormatValue(binary.Left)}, {FormatValue(binary.Right)}");
                        return;
                    }
                }

                if (TryEmitTextEquality(result, binary))
                {
                    return;
                }

                if (TryEmitTextOrderedComparison(result, binary))
                {
                    return;
                }

                if (TryEmitFixedArrayOrderedComparison(result, binary))
                {
                    return;
                }

                if (TryEmitScalarizedNamedAggregateOrderedComparison(result, binary))
                {
                    return;
                }

                if (TryEmitSliceEquality(
                        result,
                        binary.Operator,
                        binary.Left.Type,
                        FormatValue(binary.Left),
                        FormatValue(binary.Right)))
                {
                    return;
                }

                if (TryEmitScalarizedAggregateEquality(result, binary))
                {
                    return;
                }
            }

            throw new UnsupportedBodyEmissionException(
                $"Unsupported SSA binary operator '{binary.Operator}' for '{binary.Left.Type.DisplayName}'.");
        }

        private bool TryEmitScalarizedAggregateEquality(string result, SsaBinaryRValue binary)
        {
            if (binary.Operator is not (SsaBinaryOperator.Equal or SsaBinaryOperator.NotEqual))
            {
                return false;
            }

            var rootType = NormalizeAggregateType(binary.Left.Type);
            if (!SupportsScalarizedAggregateEquality(rootType))
            {
                return false;
            }

            if (!TryGetScalarizableAggregateLeaves(
                    rootType,
                    requireRepresentationPreserving: false,
                    ignoreScalarizationThresholds: true,
                    allowTextLeaves: true,
                    allowSliceLeaves: true,
                    out var leaves))
            {
                return false;
            }

            if (leaves.Count == 1)
            {
                return TryEmitScalarizedAggregateLeafComparison(
                    result,
                    binary.Operator,
                    binary.Left,
                    binary.Right,
                    rootType,
                    leaves[0],
                    out _);
            }

            string accumulator;
            if (!TryEmitScalarizedAggregateLeafComparison(
                    $"%{EscapeIdentifier(CreateAbiTempName("aggcmp_leaf"))}",
                    binary.Operator,
                    binary.Left,
                    binary.Right,
                    rootType,
                    leaves[0],
                    out accumulator))
            {
                return false;
            }

            for (var index = 1; index < leaves.Count; index++)
            {
                if (!TryEmitScalarizedAggregateLeafComparison(
                        $"%{EscapeIdentifier(CreateAbiTempName("aggcmp_leaf"))}",
                        binary.Operator,
                        binary.Left,
                        binary.Right,
                        rootType,
                        leaves[index],
                        out var leafComparison))
                {
                    return false;
                }

                var merged = index == leaves.Count - 1
                    ? result
                    : $"%{EscapeIdentifier(CreateAbiTempName("aggcmp_merge"))}";
                var opcode = binary.Operator == SsaBinaryOperator.Equal ? "and" : "or";
                AppendLine($"  {merged} = {opcode} i1 {accumulator}, {leafComparison}");
                accumulator = merged;
            }

            return true;
        }

        private bool TryEmitTextEquality(string result, SsaBinaryRValue binary)
        {
            if (binary.Operator is not (SsaBinaryOperator.Equal or SsaBinaryOperator.NotEqual))
            {
                return false;
            }

            var operandType = NormalizeAggregateType(binary.Left.Type);
            var rightType = NormalizeAggregateType(binary.Right.Type);
            if (operandType.Kind is not (StarkTypeKind.Ascii or StarkTypeKind.Unicode)
                || rightType.Kind != operandType.Kind)
            {
                return false;
            }

            return TryEmitTextEqualityHelperCall(
                result,
                binary.Operator,
                operandType,
                FormatValue(binary.Left),
                FormatValue(binary.Right));
        }

        private bool TryEmitTextOrderedComparison(string result, SsaBinaryRValue binary)
        {
            if (binary.Operator is not (
                    SsaBinaryOperator.LessThan
                    or SsaBinaryOperator.LessThanOrEqual
                    or SsaBinaryOperator.GreaterThan
                    or SsaBinaryOperator.GreaterThanOrEqual))
            {
                return false;
            }

            var operandType = NormalizeAggregateType(binary.Left.Type);
            var rightType = NormalizeAggregateType(binary.Right.Type);
            if (operandType.Kind is not (StarkTypeKind.Ascii or StarkTypeKind.Unicode)
                || rightType.Kind != operandType.Kind)
            {
                return false;
            }

            return TryEmitTextOrderedComparisonHelperCall(
                result,
                binary.Operator,
                operandType,
                FormatValue(binary.Left),
                FormatValue(binary.Right));
        }

        private bool TryEmitFixedArrayOrderedComparison(string result, SsaBinaryRValue binary)
        {
            if (binary.Operator is not (
                    SsaBinaryOperator.LessThan
                    or SsaBinaryOperator.LessThanOrEqual
                    or SsaBinaryOperator.GreaterThan
                    or SsaBinaryOperator.GreaterThanOrEqual))
            {
                return false;
            }

            var leftType = binary.Left.Type;
            var rightType = binary.Right.Type;
            if (leftType.Kind != StarkTypeKind.FixedArray
                || rightType.Kind != StarkTypeKind.FixedArray
                || leftType.ElementType is null
                || rightType.ElementType is null
                || leftType.FixedLength != rightType.FixedLength)
            {
                return false;
            }

            var helperName = GetFixedArrayOrderedComparisonHelperName(leftType);
            var compareResult = $"%{EscapeIdentifier(CreateAbiTempName("fixedcmp_root"))}";
            var predicate = binary.Operator switch
            {
                SsaBinaryOperator.LessThan => "slt",
                SsaBinaryOperator.LessThanOrEqual => "sle",
                SsaBinaryOperator.GreaterThan => "sgt",
                SsaBinaryOperator.GreaterThanOrEqual => "sge",
                _ => string.Empty
            };

            if (predicate.Length == 0)
            {
                return false;
            }

            AppendLine(
                $"  {compareResult} = call i32 @{EscapeIdentifier(helperName)}({MapType(leftType)} {FormatValue(binary.Left)}, {MapType(rightType)} {FormatValue(binary.Right)})");
            AppendLine($"  {result} = icmp {predicate} i32 {compareResult}, 0");
            return true;
        }

        private bool TryEmitScalarizedNamedAggregateOrderedComparison(string result, SsaBinaryRValue binary)
        {
            if (binary.Operator is not (
                    SsaBinaryOperator.LessThan
                    or SsaBinaryOperator.LessThanOrEqual
                    or SsaBinaryOperator.GreaterThan
                    or SsaBinaryOperator.GreaterThanOrEqual))
            {
                return false;
            }

            var leftType = NormalizeAggregateType(binary.Left.Type);
            var rightType = NormalizeAggregateType(binary.Right.Type);
            if (leftType.Kind != StarkTypeKind.Named
                || rightType.Kind != StarkTypeKind.Named
                || leftType.NamedType != rightType.NamedType
                || !SupportsScalarizedAggregateOrderedComparison(leftType))
            {
                return false;
            }

            if (!TryGetScalarizableAggregateLeaves(
                    leftType,
                    requireRepresentationPreserving: false,
                    ignoreScalarizationThresholds: true,
                    allowTextLeaves: true,
                    allowSliceLeaves: false,
                    out _))
            {
                return false;
            }

            var helperName = GetScalarizedAggregateOrderedComparisonHelperName(leftType);
            var compareResult = $"%{EscapeIdentifier(CreateAbiTempName("namedcmp_root"))}";
            var predicate = binary.Operator switch
            {
                SsaBinaryOperator.LessThan => "slt",
                SsaBinaryOperator.LessThanOrEqual => "sle",
                SsaBinaryOperator.GreaterThan => "sgt",
                SsaBinaryOperator.GreaterThanOrEqual => "sge",
                _ => string.Empty
            };

            if (predicate.Length == 0)
            {
                return false;
            }

            AppendLine(
                $"  {compareResult} = call i32 @{EscapeIdentifier(helperName)}({MapType(leftType)} {FormatValue(binary.Left)}, {MapType(rightType)} {FormatValue(binary.Right)})");
            AppendLine($"  {result} = icmp {predicate} i32 {compareResult}, 0");
            return true;
        }

        private bool SupportsScalarizedAggregateEquality(StarkTypeSymbol rootType)
        {
            return rootType.Kind switch
            {
                StarkTypeKind.FixedArray => true,
                StarkTypeKind.Named => _resolveNamedTypeSymbol(rootType) is { } namedType
                    && (namedType.Kind is DeclarationKind.Struct or DeclarationKind.Record
                        || (namedType.Kind == DeclarationKind.Enum && _enumLayouts.ContainsKey(namedType.Name))),
                _ => false
            };
        }

        private bool SupportsScalarizedAggregateOrderedComparison(StarkTypeSymbol rootType)
        {
            return rootType.Kind switch
            {
                StarkTypeKind.Named => _resolveNamedTypeSymbol(rootType) is { } namedType
                    && (namedType.Kind is DeclarationKind.Struct or DeclarationKind.Record
                        || (namedType.Kind == DeclarationKind.Enum && _enumLayouts.ContainsKey(namedType.Name))),
                _ => false
            };
        }

        private bool TryEmitScalarizedAggregateLeafComparison(
            string result,
            SsaBinaryOperator operatorKind,
            SsaValue left,
            SsaValue right,
            StarkTypeSymbol rootType,
            AggregateScalarLeaf leaf,
            out string emittedResult)
        {
            var leftValue = EmitScalarizedAggregateLeafValue(left, rootType, leaf.Indices, leaf.Type);
            var rightValue = EmitScalarizedAggregateLeafValue(right, rootType, leaf.Indices, leaf.Type);
            emittedResult = result;
            return TryEmitLeafEqualityComparison(result, operatorKind, leaf.Type, leftValue, rightValue);
        }

        private bool TryEmitLeafEqualityComparison(
            string result,
            SsaBinaryOperator operatorKind,
            StarkTypeSymbol operandType,
            string left,
            string right)
        {
            operandType = NormalizeAggregateType(operandType);
            switch (operandType.Kind)
            {
                case StarkTypeKind.Integer:
                case StarkTypeKind.Bool:
                {
                    var predicate = operatorKind switch
                    {
                        SsaBinaryOperator.Equal => "eq",
                        SsaBinaryOperator.NotEqual => "ne",
                        _ => string.Empty
                    };

                    if (predicate.Length == 0)
                    {
                        return false;
                    }

                    AppendLine($"  {result} = icmp {predicate} {MapType(operandType)} {left}, {right}");
                    return true;
                }
                case StarkTypeKind.Float:
                {
                    var predicate = operatorKind switch
                    {
                        SsaBinaryOperator.Equal => "oeq",
                        SsaBinaryOperator.NotEqual => "one",
                        _ => string.Empty
                    };

                    if (predicate.Length == 0)
                    {
                        return false;
                    }

                    AppendLine($"  {result} = fcmp {predicate} {MapType(operandType)} {left}, {right}");
                    return true;
                }
                case StarkTypeKind.RawPointer:
                {
                    var predicate = operatorKind switch
                    {
                        SsaBinaryOperator.Equal => "eq",
                        SsaBinaryOperator.NotEqual => "ne",
                        _ => string.Empty
                    };

                    if (predicate.Length == 0)
                    {
                        return false;
                    }

                    AppendLine($"  {result} = icmp {predicate} ptr {left}, {right}");
                    return true;
                }
                case StarkTypeKind.Ascii:
                case StarkTypeKind.Unicode:
                    return TryEmitTextEqualityHelperCall(result, operatorKind, operandType, left, right);
                case StarkTypeKind.Slice:
                    return TryEmitSliceEquality(result, operatorKind, operandType, left, right);
                default:
                    return false;
            }
        }

        private bool TryEmitTextEqualityHelperCall(
            string result,
            SsaBinaryOperator operatorKind,
            StarkTypeSymbol operandType,
            string left,
            string right)
        {
            operandType = NormalizeAggregateType(operandType);
            if (operandType.Kind is not (StarkTypeKind.Ascii or StarkTypeKind.Unicode)
                || operatorKind is not (SsaBinaryOperator.Equal or SsaBinaryOperator.NotEqual))
            {
                return false;
            }

            var helperName = operandType.Kind == StarkTypeKind.Ascii
                ? AsciiEqualityHelperName
                : UnicodeEqualityHelperName;
            var equalityResult = operatorKind == SsaBinaryOperator.Equal
                ? result
                : $"%{EscapeIdentifier(CreateAbiTempName("textcmp_eq"))}";

            AppendLine(
                $"  {equalityResult} = call i1 @{EscapeIdentifier(helperName)}({MapType(operandType)} {left}, {MapType(operandType)} {right})");

            if (operatorKind == SsaBinaryOperator.NotEqual)
            {
                AppendLine($"  {result} = xor i1 {equalityResult}, true");
            }

            return true;
        }

        private bool TryEmitTextOrderedComparisonHelperCall(
            string result,
            SsaBinaryOperator operatorKind,
            StarkTypeSymbol operandType,
            string left,
            string right)
        {
            operandType = NormalizeAggregateType(operandType);
            if (operandType.Kind is not (StarkTypeKind.Ascii or StarkTypeKind.Unicode)
                || operatorKind is not (
                    SsaBinaryOperator.LessThan
                    or SsaBinaryOperator.LessThanOrEqual
                    or SsaBinaryOperator.GreaterThan
                    or SsaBinaryOperator.GreaterThanOrEqual))
            {
                return false;
            }

            var helperName = operandType.Kind == StarkTypeKind.Ascii
                ? AsciiCompareHelperName
                : UnicodeCompareHelperName;
            var compareResult = $"%{EscapeIdentifier(CreateAbiTempName("textcmp_order"))}";
            var predicate = operatorKind switch
            {
                SsaBinaryOperator.LessThan => "slt",
                SsaBinaryOperator.LessThanOrEqual => "sle",
                SsaBinaryOperator.GreaterThan => "sgt",
                SsaBinaryOperator.GreaterThanOrEqual => "sge",
                _ => string.Empty
            };

            if (predicate.Length == 0)
            {
                return false;
            }

            AppendLine(
                $"  {compareResult} = call i32 @{EscapeIdentifier(helperName)}({MapType(operandType)} {left}, {MapType(operandType)} {right})");
            AppendLine($"  {result} = icmp {predicate} i32 {compareResult}, 0");
            return true;
        }

        private bool TryEmitSliceEquality(
            string result,
            SsaBinaryOperator operatorKind,
            StarkTypeSymbol operandType,
            string left,
            string right)
        {
            operandType = NormalizeAggregateType(operandType);
            if (operandType.Kind != StarkTypeKind.Slice
                || operatorKind is not (SsaBinaryOperator.Equal or SsaBinaryOperator.NotEqual))
            {
                return false;
            }

            var sliceType = MapType(operandType);
            var predicate = operatorKind == SsaBinaryOperator.Equal ? "eq" : "ne";
            var mergeOpcode = operatorKind == SsaBinaryOperator.Equal ? "and" : "or";
            var leftPointer = $"%{EscapeIdentifier(CreateAbiTempName("slicecmp_left_ptr"))}";
            var rightPointer = $"%{EscapeIdentifier(CreateAbiTempName("slicecmp_right_ptr"))}";
            var leftLength = $"%{EscapeIdentifier(CreateAbiTempName("slicecmp_left_len"))}";
            var rightLength = $"%{EscapeIdentifier(CreateAbiTempName("slicecmp_right_len"))}";
            var pointerComparison = $"%{EscapeIdentifier(CreateAbiTempName("slicecmp_ptr"))}";
            var lengthComparison = $"%{EscapeIdentifier(CreateAbiTempName("slicecmp_len"))}";

            AppendLine($"  {leftPointer} = extractvalue {sliceType} {left}, 0");
            AppendLine($"  {rightPointer} = extractvalue {sliceType} {right}, 0");
            AppendLine($"  {leftLength} = extractvalue {sliceType} {left}, 1");
            AppendLine($"  {rightLength} = extractvalue {sliceType} {right}, 1");
            AppendLine($"  {pointerComparison} = icmp {predicate} ptr {leftPointer}, {rightPointer}");
            AppendLine($"  {lengthComparison} = icmp {predicate} i64 {leftLength}, {rightLength}");
            AppendLine($"  {result} = {mergeOpcode} i1 {pointerComparison}, {lengthComparison}");
            return true;
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

        private void EmitIntegerExponent(string result, SsaBinaryRValue binary)
        {
            var bitWidth = binary.Type.BitWidth ?? throw new UnsupportedBodyEmissionException(
                $"Integer exponent operator '{binary.Type.DisplayName}' is missing a bit width.");
            var llvmType = MapType(binary.Type);
            var helperName = GetIntegerExponentHelperName(bitWidth);
            AppendLine(
                $"  {result} = call {llvmType} @{EscapeIdentifier(helperName)}({llvmType} {FormatValue(binary.Left)}, {llvmType} {FormatValue(binary.Right)})");
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
                arguments.Add(RenderSRetArgumentPointer(call.Type, indirectReturnSlot));
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
                            arguments.Add(RenderIndirectArgumentPointer(parameter, $"%{EscapeIdentifier(promotedParameter.LlvmName)}"));
                        }
                        else
                        {
                            EnsureParameterSlotExists(promotedParameter, promotedParameter.SourceType);
                            arguments.Add(RenderIndirectArgumentPointer(parameter, $"%{EscapeIdentifier($"slot_param_{promotedParameter.SourceName}")}"));
                        }

                        continue;
                    }

                    EnsureLocalSlotExists(promotedLocal!, parameter.SourceType);
                    arguments.Add(RenderIndirectArgumentPointer(parameter, $"%{EscapeIdentifier($"slot_{promotedLocal}")}"));
                    continue;
                }

                var tempSlot = $"%{EscapeIdentifier(CreateAbiTempName($"callarg_{parameter.SourceName}"))}";
                AppendLine($"  {tempSlot} = alloca {MapType(parameter.SourceType)}");
                if (!TryEmitScalarizedAggregateStore(tempSlot, parameter.SourceType, argument))
                {
                    AppendLine($"  store {MapType(parameter.SourceType)} {FormatValue(argument)}, ptr {tempSlot}");
                }

                arguments.Add(RenderIndirectArgumentPointer(parameter, tempSlot));
            }

            var renderedArguments = string.Join(", ", arguments);
            var callPrefix = abiCallee.UsesFastCallingConvention ? "call fastcc" : "call";

            if (abiCallee.ReturnsIndirect)
            {
                AppendLine($"  {callPrefix} void @{EscapeIdentifier(abiCallee.SymbolName)}({renderedArguments})");
                AppendLine($"  {result} = load {MapType(call.Type)}, ptr {indirectReturnSlot}");
                return;
            }

            if (call.Type.Kind == StarkTypeKind.Void)
            {
                AppendLine($"  {callPrefix} void @{EscapeIdentifier(abiCallee.SymbolName)}({renderedArguments})");
                return;
            }

            AppendLine($"  {result} = {callPrefix} {MapType(abiCallee.LlvmReturnType)} @{EscapeIdentifier(abiCallee.SymbolName)}({renderedArguments})");
        }

        private void EmitAllocateLocal(SsaAllocateLocalInstruction allocateLocal)
        {
            EnsureLocalSlotExists(allocateLocal.LocalName, allocateLocal.LocalType);
            EmitLocalDebugDeclare(
                $"%{EscapeIdentifier($"slot_{allocateLocal.LocalName}")}",
                allocateLocal.LocalName,
                allocateLocal.LocalType,
                allocateLocal.Location);
        }

        private void EmitLifetimeStart(SsaLifetimeStartInstruction lifetimeStart)
        {
            EmitLifetimeMarker("start", lifetimeStart.LocalName, lifetimeStart.LocalType);
        }

        private void EmitLifetimeEnd(SsaLifetimeEndInstruction lifetimeEnd)
        {
            EmitLifetimeMarker("end", lifetimeEnd.LocalName, lifetimeEnd.LocalType);
        }

        private void EmitDeallocateLocal(SsaDeallocateLocalInstruction deallocateLocal)
        {
            if (deallocateLocal.StorageClass != "heap")
            {
                throw new UnsupportedBodyEmissionException(
                    $"Local storage class '{deallocateLocal.StorageClass}' is not yet supported for LLVM deallocation.");
            }

            var slotName = $"%{EscapeIdentifier($"slot_{deallocateLocal.LocalName}")}";
            AppendLine($"  call void @free(ptr {slotName})");
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
            var slot = $"%{EscapeIdentifier($"slot_{storeLocal.LocalName}")}";
            if (TryEmitInlineAggregateZeroFill(slot, storeLocal.LocalType, storeLocal.Value))
            {
                return;
            }

            if (!TryEmitScalarizedAggregateStore(slot, storeLocal.LocalType, storeLocal.Value))
            {
                AppendLine($"  store {MapType(storeLocal.LocalType)} {FormatValue(storeLocal.Value)}, ptr {slot}");
            }
        }

        private void EmitCopyMemory(SsaCopyMemoryInstruction copyMemory)
        {
            if (TryEmitScalarizedAggregateCopy(copyMemory.DestinationAddress, copyMemory.SourceAddress, copyMemory.CopyType))
            {
                return;
            }

            if (_tryGetConcreteTypeLayout(copyMemory.CopyType) is { } layout
                && layout.SizeBytes > AggregateMemcpyThresholdBytes)
            {
                AppendLine(
                    $"  call void @llvm.memcpy.inline.p0.p0.i64(ptr {FormatValue(copyMemory.DestinationAddress)}, ptr {FormatValue(copyMemory.SourceAddress)}, i64 {layout.SizeBytes}, i1 false)");
                return;
            }

            var loadedValue = $"%{EscapeIdentifier(CreateAbiTempName("copy_load"))}";
            AppendLine($"  {loadedValue} = load {MapType(copyMemory.CopyType)}, ptr {FormatValue(copyMemory.SourceAddress)}");
            AppendLine($"  store {MapType(copyMemory.CopyType)} {loadedValue}, ptr {FormatValue(copyMemory.DestinationAddress)}");
        }

        private void EmitStoreIndirect(SsaStoreIndirectInstruction storeIndirect)
        {
            if (TryEmitInlineAggregateZeroFill(FormatValue(storeIndirect.Address), storeIndirect.ValueType, storeIndirect.Value))
            {
                return;
            }

            if (!TryEmitScalarizedAggregateStore(FormatValue(storeIndirect.Address), storeIndirect.ValueType, storeIndirect.Value))
            {
                AppendLine($"  store {MapType(storeIndirect.ValueType)} {FormatValue(storeIndirect.Value)}, ptr {FormatValue(storeIndirect.Address)}");
            }
        }

        private bool TryEmitInlineAggregateZeroFill(string destinationAddress, StarkTypeSymbol valueType, SsaValue value)
        {
            if (value is not SsaZeroInitializerValue
                || !ShouldEmitInlineAggregateZeroFill(valueType)
                || _tryGetConcreteTypeLayout(valueType) is not { } layout)
            {
                return false;
            }

            AppendLine($"  call void @llvm.memset.inline.p0.i64(ptr {destinationAddress}, i8 0, i64 {layout.SizeBytes}, i1 false)");
            return true;
        }

        private bool ShouldEmitInlineAggregateZeroFill(StarkTypeSymbol valueType)
        {
            if (_tryGetConcreteTypeLayout(NormalizeAggregateType(valueType)) is not { } layout
                || layout.SizeBytes <= AggregateScalarizationThresholdBytes)
            {
                return false;
            }

            return valueType.Kind is StarkTypeKind.FixedArray or StarkTypeKind.Named;
        }

        private bool TryEmitScalarizedAggregateCopy(SsaValue destinationAddress, SsaValue sourceAddress, StarkTypeSymbol copyType)
        {
            if (!TryGetScalarizableAggregateLeaves(
                    copyType,
                    requireRepresentationPreserving: true,
                    ignoreScalarizationThresholds: false,
                    allowTextLeaves: false,
                    allowSliceLeaves: false,
                    out var leaves))
            {
                return false;
            }

            foreach (var leaf in leaves)
            {
                var sourceLeafAddress = EmitScalarizedAggregateLeafAddress(sourceAddress, copyType, leaf.Indices, "copy_src");
                var loadedLeaf = $"%{EscapeIdentifier(CreateAbiTempName("copy_scalar_load"))}";
                AppendLine($"  {loadedLeaf} = load {MapType(leaf.Type)}, ptr {sourceLeafAddress}");
                var destinationLeafAddress = EmitScalarizedAggregateLeafAddress(destinationAddress, copyType, leaf.Indices, "copy_dest");
                AppendLine($"  store {MapType(leaf.Type)} {loadedLeaf}, ptr {destinationLeafAddress}");
            }

            return true;
        }

        private bool TryEmitScalarizedAggregateStore(string destinationAddress, StarkTypeSymbol valueType, SsaValue value)
        {
            if (!TryGetScalarizableAggregateLeaves(
                    valueType,
                    requireRepresentationPreserving: true,
                    ignoreScalarizationThresholds: false,
                    allowTextLeaves: false,
                    allowSliceLeaves: false,
                    out var leaves))
            {
                return false;
            }

            foreach (var leaf in leaves)
            {
                var leafValue = EmitScalarizedAggregateLeafValue(value, valueType, leaf.Indices, leaf.Type);
                var leafAddress = EmitScalarizedAggregateLeafAddress(destinationAddress, valueType, leaf.Indices, "store_dest");
                AppendLine($"  store {MapType(leaf.Type)} {leafValue}, ptr {leafAddress}");
            }

            return true;
        }

        private bool TryGetScalarizableAggregateLeaves(
            StarkTypeSymbol type,
            bool requireRepresentationPreserving,
            bool ignoreScalarizationThresholds,
            bool allowTextLeaves,
            bool allowSliceLeaves,
            out IReadOnlyList<AggregateScalarLeaf> leaves)
        {
            leaves = Array.Empty<AggregateScalarLeaf>();

            if (_tryGetConcreteTypeLayout(NormalizeAggregateType(type)) is not { } layout
                || layout.SizeBytes <= 0
                || (!ignoreScalarizationThresholds && layout.SizeBytes > AggregateScalarizationThresholdBytes))
            {
                return false;
            }

            var collectedLeaves = new List<AggregateScalarLeaf>();
            if (!TryCollectScalarizableAggregateLeaves(
                    NormalizeAggregateType(type),
                    requireRepresentationPreserving,
                    allowTextLeaves,
                    allowSliceLeaves,
                    [],
                    collectedLeaves))
            {
                return false;
            }

            if (collectedLeaves.Count == 0
                || (!ignoreScalarizationThresholds && collectedLeaves.Count > AggregateScalarizationMaxLeafCount))
            {
                return false;
            }

            leaves = collectedLeaves;
            return true;
        }

        private bool TryCollectScalarizableAggregateLeaves(
            StarkTypeSymbol type,
            bool requireRepresentationPreserving,
            bool allowTextLeaves,
            bool allowSliceLeaves,
            List<int> path,
            List<AggregateScalarLeaf> leaves)
        {
            var normalizedType = NormalizeAggregateType(type);
            switch (normalizedType.Kind)
            {
                case StarkTypeKind.Bool:
                case StarkTypeKind.Integer:
                case StarkTypeKind.Float:
                case StarkTypeKind.RawPointer:
                    leaves.Add(new AggregateScalarLeaf([.. path], normalizedType));
                    return true;
                case StarkTypeKind.Ascii when allowTextLeaves:
                case StarkTypeKind.Unicode when allowTextLeaves:
                    leaves.Add(new AggregateScalarLeaf([.. path], normalizedType));
                    return true;
                case StarkTypeKind.Slice when allowSliceLeaves:
                    leaves.Add(new AggregateScalarLeaf([.. path], normalizedType));
                    return true;
                case StarkTypeKind.FixedArray when normalizedType.ElementType is not null && normalizedType.FixedLength is int fixedLength:
                    for (var index = 0; index < fixedLength; index++)
                    {
                        path.Add(index);
                        if (!TryCollectScalarizableAggregateLeaves(
                                normalizedType.ElementType,
                                requireRepresentationPreserving,
                                allowTextLeaves,
                                allowSliceLeaves,
                                path,
                                leaves))
                        {
                            path.RemoveAt(path.Count - 1);
                            return false;
                        }

                        path.RemoveAt(path.Count - 1);
                    }

                    return true;
                case StarkTypeKind.Named:
                {
                    var namedType = _resolveNamedTypeSymbol(normalizedType);
                    if (namedType is null
                        || !TryGetScalarizableNamedAggregateFields(namedType, out var orderedFields))
                    {
                        return false;
                    }

                    var sizeBytes = 0;
                    var alignmentBytes = 1;
                    for (var index = 0; index < orderedFields.Count; index++)
                    {
                        var field = orderedFields[index];
                        var fieldLayout = _tryGetConcreteTypeLayout(NormalizeAggregateType(field.Type));
                        if (fieldLayout is null)
                        {
                            return false;
                        }

                        var alignedOffset = AlignTo(sizeBytes, fieldLayout.AlignmentBytes);
                        if (requireRepresentationPreserving && alignedOffset != sizeBytes)
                        {
                            return false;
                        }

                        path.Add(index);
                        if (!TryCollectScalarizableAggregateLeaves(
                                field.Type,
                                requireRepresentationPreserving,
                                allowTextLeaves,
                                allowSliceLeaves,
                                path,
                                leaves))
                        {
                            path.RemoveAt(path.Count - 1);
                            return false;
                        }

                        path.RemoveAt(path.Count - 1);
                        sizeBytes = checked(alignedOffset + fieldLayout.SizeBytes);
                        alignmentBytes = Math.Max(alignmentBytes, fieldLayout.AlignmentBytes);
                    }

                    if (requireRepresentationPreserving && AlignTo(sizeBytes, alignmentBytes) != sizeBytes)
                    {
                        return false;
                    }

                    return true;
                }
                default:
                    return false;
            }
        }

        private string EmitScalarizedAggregateLeafValue(
            SsaValue value,
            StarkTypeSymbol rootType,
            IReadOnlyList<int> indices,
            StarkTypeSymbol leafType)
        {
            if (value is SsaZeroInitializerValue)
            {
                return FormatZeroInitializer(leafType);
            }

            if (value is SsaUndefValue)
            {
                return "undef";
            }

            var currentValue = FormatValue(value);
            var currentType = NormalizeAggregateType(rootType);

            foreach (var index in indices)
            {
                var nextType = GetAggregateElementType(currentType, index)
                    ?? throw new UnsupportedBodyEmissionException(
                        $"Cannot scalarize aggregate leaf '{value.Text}' for '{rootType.DisplayName}'.");
                var extracted = $"%{EscapeIdentifier(CreateAbiTempName("scalar_extract"))}";
                AppendLine($"  {extracted} = extractvalue {MapType(currentType)} {currentValue}, {index}");
                currentValue = extracted;
                currentType = NormalizeAggregateType(nextType);
            }

            return currentValue;
        }

        private string EmitAggregateLeafValueExtraction(
            StringBuilder builder,
            StarkTypeSymbol rootType,
            string rootValue,
            IReadOnlyList<int> indices,
            string purpose)
        {
            if (indices.Count == 0)
            {
                return rootValue;
            }

            var currentValue = rootValue;
            var currentType = NormalizeAggregateType(rootType);

            foreach (var index in indices)
            {
                var nextType = GetAggregateElementType(currentType, index)
                    ?? throw new UnsupportedBodyEmissionException(
                        $"Cannot extract aggregate leaf for '{rootType.DisplayName}'.");
                var extracted = $"%{EscapeIdentifier(CreateAbiTempName(purpose))}";
                builder.AppendLine($"  {extracted} = extractvalue {MapType(currentType)} {currentValue}, {index}");
                currentValue = extracted;
                currentType = NormalizeAggregateType(nextType);
            }

            return currentValue;
        }

        private string EmitScalarizedAggregateLeafAddress(
            SsaValue baseAddress,
            StarkTypeSymbol rootType,
            IReadOnlyList<int> indices,
            string purpose)
        {
            return EmitScalarizedAggregateLeafAddress(FormatValue(baseAddress), rootType, indices, purpose);
        }

        private string EmitScalarizedAggregateLeafAddress(
            string baseAddress,
            StarkTypeSymbol rootType,
            IReadOnlyList<int> indices,
            string purpose)
        {
            if (indices.Count == 0)
            {
                return baseAddress;
            }

            var leafAddress = $"%{EscapeIdentifier(CreateAbiTempName(purpose))}";
            var gepIndices = string.Join(", ", indices.Select(static index => $"i32 {index}"));
            AppendLine($"  {leafAddress} = getelementptr inbounds {MapType(rootType)}, ptr {baseAddress}, i32 0, {gepIndices}");
            return leafAddress;
        }

        private StarkTypeSymbol? GetAggregateElementType(StarkTypeSymbol type, int index)
        {
            var normalizedType = NormalizeAggregateType(type);
            return normalizedType.Kind switch
            {
                StarkTypeKind.FixedArray when normalizedType.ElementType is not null => normalizedType.ElementType,
                StarkTypeKind.Named when _resolveNamedTypeSymbol(normalizedType) is { } namedType
                                           && TryGetScalarizableNamedAggregateFields(namedType, out var orderedFields)
                                           && index >= 0
                                           && index < orderedFields.Count
                    => orderedFields[index].Type,
                _ => null
            };
        }

        private bool TryGetScalarizableNamedAggregateFields(
            NamedTypeSymbol namedType,
            out IReadOnlyList<FieldSymbol> orderedFields)
        {
            if (namedType.Kind is DeclarationKind.Struct or DeclarationKind.Record)
            {
                orderedFields = namedType.OrderedFields;
                return true;
            }

            if (namedType.Kind == DeclarationKind.Enum
                && _enumLayouts.TryGetValue(namedType.Name, out var enumLayout))
            {
                orderedFields = enumLayout.OrderedFields;
                return true;
            }

            orderedFields = Array.Empty<FieldSymbol>();
            return false;
        }

        private static StarkTypeSymbol NormalizeAggregateType(StarkTypeSymbol type)
        {
            return type with
            {
                BorrowKind = StarkBorrowKind.None,
                AccessKind = StarkAccessKind.None,
                InitializationKind = StarkInitializationKind.None,
                IsMutableView = false
            };
        }

        private static int AlignTo(int value, int alignment)
        {
            if (alignment <= 1)
            {
                return value;
            }

            var remainder = value % alignment;
            return remainder == 0 ? value : checked(value + alignment - remainder);
        }

        private static string FormatZeroInitializer(StarkTypeSymbol type)
        {
            var normalizedType = NormalizeAggregateType(type);
            return normalizedType.Kind switch
            {
                StarkTypeKind.Integer => "0",
                StarkTypeKind.Float => "0.0",
                StarkTypeKind.Bool => "false",
                StarkTypeKind.RawPointer => "null",
                _ => "zeroinitializer"
            };
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
                SsaFloatConstant floating => FormatFloatLiteral(floating),
                SsaStringConstant text => FormatStringConstantValue(text),
                SsaBoolConstant boolean => boolean.Value ? "true" : "false",
                SsaNullConstant => "null",
                SsaGlobalAddressValue globalAddress => $"@{EscapeIdentifier(_mapGlobalSymbolName(globalAddress.GlobalName))}",
                SsaZeroInitializerValue => "zeroinitializer",
                SsaUndefValue => "undef",
                _ => throw new UnsupportedBodyEmissionException($"Unsupported SSA value '{value.GetType().Name}'.")
            };
        }

        private static string FormatFloatLiteral(SsaFloatConstant floating)
        {
            if (!double.TryParse(
                    floating.LiteralText,
                    NumberStyles.Float | NumberStyles.AllowThousands,
                    CultureInfo.InvariantCulture,
                    out var parsed))
            {
                throw new UnsupportedBodyEmissionException(
                    $"Unable to parse floating-point literal '{floating.LiteralText}' for LLVM emission.");
            }

            if (double.IsNaN(parsed) || double.IsInfinity(parsed))
            {
                var bits = floating.Type.BitWidth == 32
                    ? BitConverter.DoubleToUInt64Bits((double)(float)parsed)
                    : BitConverter.DoubleToUInt64Bits(parsed);
                return $"0x{bits:X16}";
            }

            var rendered = floating.Type.BitWidth == 32
                ? ((double)(float)parsed).ToString("R", CultureInfo.InvariantCulture)
                : parsed.ToString("R", CultureInfo.InvariantCulture);

            return rendered.Contains('.', StringComparison.Ordinal)
                || rendered.Contains('E', StringComparison.Ordinal)
                || rendered.Contains('e', StringComparison.Ordinal)
                ? rendered
                : rendered + ".0";
        }

        private string RenderDirectArgument(AbiParameterSymbol parameter, SsaValue argument)
        {
            if (parameter.LlvmType.Kind == StarkTypeKind.RawPointer && IsStringType(parameter.SourceType))
            {
                return $"ptr {ExtractStringDataPointer(argument)}";
            }

            return $"{MapType(parameter.LlvmType)} {FormatValue(argument)}";
        }

        private string RenderIndirectArgumentPointer(AbiParameterSymbol parameter, string pointerValue)
        {
            var segments = new List<string> { "ptr" };

            if (AbiLoweringHeuristics.IsByValueIndirectParameter(parameter))
            {
                segments.Add($"byval({MapType(parameter.SourceType)})");
                if (_tryGetConcreteTypeLayout(parameter.SourceType) is { AlignmentBytes: > 1 } layout)
                {
                    segments.Add($"align {layout.AlignmentBytes}");
                }
            }

            segments.Add(pointerValue);
            return string.Join(" ", segments);
        }

        private string RenderSRetArgumentPointer(StarkTypeSymbol returnType, string pointerValue)
        {
            var segments = new List<string> { "ptr", $"sret({MapType(returnType)})" };
            if (_tryGetConcreteTypeLayout(returnType) is { AlignmentBytes: > 1 } layout)
            {
                segments.Add($"align {layout.AlignmentBytes}");
            }

            segments.Add(pointerValue);
            return string.Join(" ", segments);
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
            if (!_allocatedLocalSlots.Add(slotName))
            {
                return;
            }

            switch (GetLocalStorageClass(localName))
            {
                case "stack":
                    AppendLine($"  %{slotName} = alloca {MapType(localType)}");
                    return;
                case "heap":
                    EmitHeapAllocateLocalSlot(slotName, localType);
                    return;
                default:
                    throw new UnsupportedBodyEmissionException(
                        $"Local storage class '{GetLocalStorageClass(localName)}' is not yet supported for LLVM body emission.");
            }
        }

        private void EmitHeapAllocateLocalSlot(string slotName, StarkTypeSymbol localType)
        {
            var sizePointer = $"%{EscapeIdentifier(CreateAbiTempName("heap_size_ptr"))}";
            var sizeValue = $"%{EscapeIdentifier(CreateAbiTempName("heap_size"))}";
            AppendLine($"  {sizePointer} = getelementptr {MapType(localType)}, ptr null, i32 1");
            AppendLine($"  {sizeValue} = ptrtoint ptr {sizePointer} to {_allocatorSizeType}");
            AppendLine($"  %{slotName} = call ptr @malloc({_allocatorSizeType} {sizeValue})");
        }

        private string GetLocalStorageClass(string localName)
        {
            return _localStorageClasses.TryGetValue(localName, out var storageClass)
                ? storageClass
                : "stack";
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

                if (!_referencedValueNames.Contains(parameter.LlvmName))
                {
                    continue;
                }

                var materializedName = $"%{EscapeIdentifier(CreateAbiTempName($"arg_{parameter.SourceName}_value"))}";
                AppendLine($"  {materializedName} = load {MapType(parameter.SourceType)}, ptr %{EscapeIdentifier(parameter.LlvmName)}");
                _materializedParameters[parameter.LlvmName] = materializedName;
            }
        }

        private void EmitEntryParameterDebugInfo()
        {
            if (_debugFunction is null)
            {
                return;
            }

            for (var index = 0; index < _abiFunction.UserParameters.Count; index++)
            {
                var parameter = _abiFunction.UserParameters[index];
                var variableRef = _debugFunction.GetParameterVariableRef(parameter.SourceName, parameter.SourceType, index + 1);

                if (parameter.Kind == AbiParameterKind.IndirectIn)
                {
                    AppendLine($"  call void @llvm.dbg.declare(metadata ptr %{EscapeIdentifier(parameter.LlvmName)}, metadata {variableRef}, metadata !DIExpression())");
                    continue;
                }

                AppendLine(
                    $"  call void @llvm.dbg.value(metadata {MapType(parameter.LlvmType)} %{EscapeIdentifier(parameter.LlvmName)}, metadata {variableRef}, metadata !DIExpression())");
            }
        }

        private void EmitLocalDebugDeclare(string slotName, string localName, StarkTypeSymbol localType, SourceLocation? location)
        {
            if (_debugFunction is null)
            {
                return;
            }

            var variableRef = _debugFunction.GetLocalVariableRef(localName, localType, location ?? _ssaFunction.Location);
            AppendLine($"  call void @llvm.dbg.declare(metadata ptr {slotName}, metadata {variableRef}, metadata !DIExpression())");
        }

        private static HashSet<string> CollectReferencedValueNames(SsaFunction function)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);

            foreach (var block in function.Blocks)
            {
                foreach (var phi in block.Phis)
                {
                    foreach (var incoming in phi.Incomings)
                    {
                        VisitValue(incoming.Value);
                    }
                }

                foreach (var instruction in block.Instructions)
                {
                    VisitInstruction(instruction);
                }

                VisitValue(block.Terminator.Condition);
                VisitValue(block.Terminator.Value);

                if (block.Terminator.SwitchCases is not null)
                {
                    foreach (var switchCase in block.Terminator.SwitchCases)
                    {
                        VisitValue(switchCase.MatchValue);
                    }
                }
            }

            return names;

            void VisitInstruction(SsaInstruction instruction)
            {
                switch (instruction)
                {
                    case SsaValueInstruction valueInstruction:
                        VisitRValue(valueInstruction.Value);
                        break;
                    case SsaStoreLocalInstruction storeLocal:
                        VisitValue(storeLocal.Value);
                        break;
                    case SsaStoreIndirectInstruction storeIndirect:
                        VisitValue(storeIndirect.Address);
                        VisitValue(storeIndirect.Value);
                        break;
                    case SsaCopyMemoryInstruction copyMemory:
                        VisitValue(copyMemory.DestinationAddress);
                        VisitValue(copyMemory.SourceAddress);
                        break;
                    case SsaStoreGlobalInstruction storeGlobal:
                        VisitValue(storeGlobal.Value);
                        break;
                }
            }

            void VisitRValue(SsaRValue value)
            {
                switch (value)
                {
                    case SsaUseRValue use:
                        VisitValue(use.Value);
                        break;
                    case SsaUnaryRValue unary:
                        VisitValue(unary.Operand);
                        break;
                    case SsaBinaryRValue binary:
                        VisitValue(binary.Left);
                        VisitValue(binary.Right);
                        break;
                    case SsaCallRValue call:
                        foreach (var argument in call.Arguments)
                        {
                            VisitValue(argument);
                        }

                        break;
                    case SsaConvertRValue convert:
                        VisitValue(convert.Operand);
                        break;
                    case SsaExtractFieldRValue extractField:
                        VisitValue(extractField.Target);
                        break;
                    case SsaInsertFieldRValue insertField:
                        VisitValue(insertField.Target);
                        VisitValue(insertField.Value);
                        break;
                    case SsaExtractIndexRValue extractIndex:
                        VisitValue(extractIndex.Target);
                        break;
                    case SsaInsertIndexRValue insertIndex:
                        VisitValue(insertIndex.Target);
                        VisitValue(insertIndex.Value);
                        break;
                    case SsaLoadSliceElementRValue loadSlice:
                        VisitValue(loadSlice.Slice);
                        VisitValue(loadSlice.Index);
                        break;
                    case SsaTextSliceRValue textSlice:
                        VisitValue(textSlice.TextValue);
                        VisitValue(textSlice.Start);
                        VisitValue(textSlice.Length);
                        break;
                    case SsaFieldAddressRValue fieldAddress:
                        VisitValue(fieldAddress.Address);
                        break;
                    case SsaElementAddressRValue elementAddress:
                        VisitValue(elementAddress.Address);
                        VisitValue(elementAddress.Index);
                        break;
                    case SsaSliceElementAddressRValue sliceElementAddress:
                        VisitValue(sliceElementAddress.Slice);
                        VisitValue(sliceElementAddress.Index);
                        break;
                    case SsaLoadIndirectRValue loadIndirect:
                        VisitValue(loadIndirect.Address);
                        break;
                }
            }

            void VisitValue(SsaValue? value)
            {
                if (value is SsaValueReference reference)
                {
                    names.Add(reference.Name);
                }
            }
        }

        private static Dictionary<string, string> CollectLocalStorageClasses(SsaFunction function)
        {
            var storageClasses = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var block in function.Blocks)
            {
                foreach (var instruction in block.Instructions)
                {
                    if (instruction is SsaAllocateLocalInstruction allocateLocal)
                    {
                        storageClasses[allocateLocal.LocalName] = allocateLocal.StorageClass;
                    }
                }
            }

            return storageClasses;
        }

        private string FormatValueReference(SsaValueReference reference)
        {
            return _materializedParameters.TryGetValue(reference.Name, out var materialized)
                ? materialized
                : $"%{EscapeIdentifier(reference.Name)}";
        }

        private string CreateAbiTempName(string purpose) => $"abi_{purpose}_{_nextAbiTempId++}";

        private string MapType(StarkTypeSymbol type) => _mapType(type);

        private static SourceLocation? GetInstructionLocation(SsaInstruction instruction)
        {
            return instruction switch
            {
                SsaValueInstruction valueInstruction => valueInstruction.Location,
                SsaAllocateLocalInstruction allocateLocal => allocateLocal.Location,
                SsaLifetimeStartInstruction lifetimeStart => lifetimeStart.Location,
                SsaLifetimeEndInstruction lifetimeEnd => lifetimeEnd.Location,
                SsaDeallocateLocalInstruction deallocateLocal => deallocateLocal.Location,
                SsaStoreLocalInstruction storeLocal => storeLocal.Location,
                SsaCopyMemoryInstruction copyMemory => copyMemory.Location,
                SsaStoreIndirectInstruction storeIndirect => storeIndirect.Location,
                SsaStoreGlobalInstruction storeGlobal => storeGlobal.Location,
                _ => null
            };
        }

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

        private void AppendLine(string text)
        {
            if (_debugFunction is not null
                && _currentDebugLocation is not null
                && ShouldAttachDebugLocation(text))
            {
                text = $"{text}, !dbg {_debugFunction.GetLocationRef(_currentDebugLocation)}";
            }

            _builder.AppendLine(text);
        }

        private static bool ShouldAttachDebugLocation(string text)
        {
            if (string.IsNullOrWhiteSpace(text)
                || !text.StartsWith("  ", StringComparison.Ordinal))
            {
                return false;
            }

            var trimmed = text.TrimStart();
            return !trimmed.StartsWith(';')
                && !trimmed.StartsWith('}');
        }
    }

    private sealed class DebugMetadataEmitter
    {
        private readonly string _defaultSourcePath;
        private readonly Func<StarkTypeSymbol, ConcreteTypeLayout?> _tryGetConcreteTypeLayout;
        private readonly List<string> _definitions = [];
        private readonly Dictionary<string, string> _fileRefs = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _typeRefs = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _tupleRefs = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _subroutineTypeRefs = new(StringComparer.Ordinal);
        private readonly string _compileUnitRef;
        private readonly string _debugInfoVersionRef;
        private readonly string _dwarfVersionRef;
        private readonly string _defaultFileRef;
        private readonly string _emptyTupleRef;
        private bool _hasFunctions;
        private int _nextMetadataId;

        public DebugMetadataEmitter(
            string defaultSourcePath,
            Func<StarkTypeSymbol, ConcreteTypeLayout?> tryGetConcreteTypeLayout)
        {
            _defaultSourcePath = string.IsNullOrWhiteSpace(defaultSourcePath)
                ? "module.stark"
                : defaultSourcePath;
            _tryGetConcreteTypeLayout = tryGetConcreteTypeLayout;
            _defaultFileRef = GetFileRef(_defaultSourcePath);
            _emptyTupleRef = CreateMetadata("!{}");
            _compileUnitRef = CreateMetadata(
                $"distinct !DICompileUnit(language: DW_LANG_C, file: {_defaultFileRef}, producer: \"Stark Compiler\", isOptimized: false, runtimeVersion: 0, emissionKind: FullDebug)");
            _debugInfoVersionRef = CreateMetadata("!{i32 2, !\"Debug Info Version\", i32 3}");
            _dwarfVersionRef = CreateMetadata("!{i32 7, !\"Dwarf Version\", i32 5}");
        }

        public bool Enabled => true;

        public DebugFunctionContext CreateFunctionContext(
            string sourceName,
            string linkageName,
            SourceLocation location,
            TypedFunctionSignature function)
        {
            _hasFunctions = true;

            var normalizedLocation = ResolveLocation(location);
            var fileRef = GetFileRef(normalizedLocation.FilePath);
            var subroutineTypeRef = GetSubroutineTypeRef(function);
            var subprogramRef = CreateMetadata(
                $"distinct !DISubprogram(name: \"{EscapeMetadataString(sourceName)}\", linkageName: \"{EscapeMetadataString(linkageName)}\", scope: {fileRef}, file: {fileRef}, line: {normalizedLocation.Line}, type: {subroutineTypeRef}, scopeLine: {normalizedLocation.Line}, spFlags: DISPFlagDefinition, unit: {_compileUnitRef}, retainedNodes: {_emptyTupleRef})");

            return new DebugFunctionContext(this, subprogramRef, fileRef, normalizedLocation);
        }

        public void EmitModuleMetadata(StringBuilder builder)
        {
            if (!_hasFunctions)
            {
                return;
            }

            builder.AppendLine($"!llvm.dbg.cu = !{{{_compileUnitRef}}}");
            builder.AppendLine($"!llvm.module.flags = !{{{_debugInfoVersionRef}, {_dwarfVersionRef}}}");
            foreach (var definition in _definitions)
            {
                builder.AppendLine(definition);
            }
        }

        public SourceLocation ResolveLocation(SourceLocation? location)
        {
            var filePath = string.IsNullOrWhiteSpace(location?.FilePath)
                ? _defaultSourcePath
                : location!.FilePath!;
            var line = location is { Line: > 0 } ? location.Line : 1;
            var column = location is { Column: > 0 } ? location.Column : 1;
            return new SourceLocation(filePath, line, column);
        }

        public string GetFileRef(string? filePath)
        {
            var normalizedPath = string.IsNullOrWhiteSpace(filePath)
                ? _defaultSourcePath
                : filePath!;
            if (_fileRefs.TryGetValue(normalizedPath, out var existing))
            {
                return existing;
            }

            var fileName = Path.GetFileName(normalizedPath);
            var directory = Path.GetDirectoryName(normalizedPath);
            if (string.IsNullOrWhiteSpace(fileName))
            {
                fileName = normalizedPath;
            }

            if (string.IsNullOrWhiteSpace(directory))
            {
                directory = ".";
            }

            var fileRef = CreateMetadata(
                $"!DIFile(filename: \"{EscapeMetadataString(fileName)}\", directory: \"{EscapeMetadataString(directory)}\")");
            _fileRefs[normalizedPath] = fileRef;
            return fileRef;
        }

        public string GetTypeRef(StarkTypeSymbol type)
        {
            if (type.Kind == StarkTypeKind.Void)
            {
                return "null";
            }

            var key = type.DisplayName;
            if (_typeRefs.TryGetValue(key, out var existing))
            {
                return existing;
            }

            var typeRef = type.Kind switch
            {
                StarkTypeKind.Bool => CreateMetadata("!DIBasicType(name: \"bool\", size: 1, encoding: DW_ATE_boolean)"),
                StarkTypeKind.Integer when type.BitWidth is int bitWidth
                    => CreateMetadata($"!DIBasicType(name: \"{EscapeMetadataString(type.DisplayName)}\", size: {bitWidth}, encoding: DW_ATE_signed)"),
                StarkTypeKind.Float when type.BitWidth is int bitWidth
                    => CreateMetadata($"!DIBasicType(name: \"{EscapeMetadataString(type.DisplayName)}\", size: {bitWidth}, encoding: DW_ATE_float)"),
                StarkTypeKind.RawPointer => CreatePointerTypeRef(type),
                StarkTypeKind.FixedArray => CreateFixedArrayTypeRef(type),
                StarkTypeKind.Slice => CreateOpaqueCompositeTypeRef(type.DisplayName, type),
                StarkTypeKind.Ascii => CreateOpaqueCompositeTypeRef(type.DisplayName, type),
                StarkTypeKind.Unicode => CreateOpaqueCompositeTypeRef(type.DisplayName, type),
                StarkTypeKind.Named => CreateOpaqueCompositeTypeRef(type.DisplayName, type),
                StarkTypeKind.Null => CreatePointerTypeRef(StarkTypeSymbols.RawPointer(StarkTypeSymbols.Integer(8), isMutable: false)),
                _ => CreateOpaqueCompositeTypeRef(type.DisplayName, type)
            };

            _typeRefs[key] = typeRef;
            return typeRef;
        }

        public string CreateLocationRef(SourceLocation location, string scopeRef)
        {
            var normalizedLocation = ResolveLocation(location);
            return CreateMetadata(
                $"!DILocation(line: {normalizedLocation.Line}, column: {normalizedLocation.Column}, scope: {scopeRef})");
        }

        public string CreateParameterVariableRef(
            string name,
            StarkTypeSymbol type,
            int argIndex,
            string scopeRef,
            string fileRef,
            int line)
        {
            return CreateMetadata(
                $"!DILocalVariable(name: \"{EscapeMetadataString(name)}\", arg: {argIndex}, scope: {scopeRef}, file: {fileRef}, line: {line}, type: {GetTypeRef(type)})");
        }

        public string CreateLocalVariableRef(
            string name,
            StarkTypeSymbol type,
            string scopeRef,
            string fileRef,
            int line)
        {
            return CreateMetadata(
                $"!DILocalVariable(name: \"{EscapeMetadataString(name)}\", scope: {scopeRef}, file: {fileRef}, line: {line}, type: {GetTypeRef(type)})");
        }

        private string CreatePointerTypeRef(StarkTypeSymbol pointerType)
        {
            var pointeeRef = pointerType.ElementType is null
                ? "null"
                : GetTypeRef(pointerType.ElementType);
            var pointerBits = (_tryGetConcreteTypeLayout(pointerType)?.SizeBytes ?? 8) * 8;
            return CreateMetadata(
                $"!DIDerivedType(tag: DW_TAG_pointer_type, baseType: {pointeeRef}, size: {pointerBits})");
        }

        private string CreateFixedArrayTypeRef(StarkTypeSymbol arrayType)
        {
            if (arrayType.ElementType is null || arrayType.FixedLength is not int fixedLength)
            {
                return CreateOpaqueCompositeTypeRef(arrayType.DisplayName, arrayType);
            }

            var subrangeRef = CreateMetadata($"!DISubrange(count: {fixedLength})");
            var elementsRef = GetTupleRef([subrangeRef]);
            var sizeBits = (_tryGetConcreteTypeLayout(arrayType)?.SizeBytes ?? 0) * 8;
            return CreateMetadata(
                $"!DICompositeType(tag: DW_TAG_array_type, baseType: {GetTypeRef(arrayType.ElementType)}, size: {sizeBits}, elements: {elementsRef})");
        }

        private string CreateOpaqueCompositeTypeRef(string name, StarkTypeSymbol type)
        {
            var sizeBits = (_tryGetConcreteTypeLayout(type)?.SizeBytes ?? 0) * 8;
            return CreateMetadata(
                $"!DICompositeType(tag: DW_TAG_structure_type, name: \"{EscapeMetadataString(name)}\", file: {_defaultFileRef}, size: {sizeBits}, elements: {_emptyTupleRef})");
        }

        private string GetSubroutineTypeRef(TypedFunctionSignature function)
        {
            var key = $"{function.ReturnType.DisplayName}({string.Join(",", function.Parameters.Select(static parameter => parameter.Type.DisplayName))})";
            if (_subroutineTypeRefs.TryGetValue(key, out var existing))
            {
                return existing;
            }

            var typeRefs = new List<string> { GetTypeRef(function.ReturnType) };
            typeRefs.AddRange(function.Parameters.Select(parameter => GetTypeRef(parameter.Type)));
            var tupleRef = GetTupleRef(typeRefs);
            var subroutineRef = CreateMetadata($"!DISubroutineType(types: {tupleRef})");
            _subroutineTypeRefs[key] = subroutineRef;
            return subroutineRef;
        }

        private string GetTupleRef(IReadOnlyList<string> items)
        {
            var key = string.Join("|", items);
            if (_tupleRefs.TryGetValue(key, out var existing))
            {
                return existing;
            }

            var tupleRef = CreateMetadata("!{" + string.Join(", ", items) + "}");
            _tupleRefs[key] = tupleRef;
            return tupleRef;
        }

        private string CreateMetadata(string body)
        {
            var reference = "!" + _nextMetadataId++;
            _definitions.Add(reference + " = " + body);
            return reference;
        }

        private static string EscapeMetadataString(string value)
        {
            return EscapeFileName(value).Replace("\n", "\\0A", StringComparison.Ordinal);
        }
    }

    private sealed class DebugFunctionContext
    {
        private readonly DebugMetadataEmitter _owner;
        private readonly string _fileRef;
        private readonly Dictionary<(int Line, int Column), string> _locationRefs = [];
        private readonly Dictionary<(string Name, int ArgIndex), string> _parameterRefs = [];
        private readonly Dictionary<(string Name, int Line, int Column), string> _localRefs = [];

        public DebugFunctionContext(
            DebugMetadataEmitter owner,
            string subprogramRef,
            string fileRef,
            SourceLocation functionLocation)
        {
            _owner = owner;
            SubprogramRef = subprogramRef;
            _fileRef = fileRef;
            FunctionLocation = functionLocation;
        }

        public string SubprogramRef { get; }

        public SourceLocation FunctionLocation { get; }

        public string GetLocationRef(SourceLocation? location)
        {
            var normalizedLocation = _owner.ResolveLocation(location ?? FunctionLocation);
            var key = (normalizedLocation.Line, normalizedLocation.Column);
            if (_locationRefs.TryGetValue(key, out var existing))
            {
                return existing;
            }

            var locationRef = _owner.CreateLocationRef(normalizedLocation, SubprogramRef);
            _locationRefs[key] = locationRef;
            return locationRef;
        }

        public string GetParameterVariableRef(string name, StarkTypeSymbol type, int argIndex)
        {
            var key = (name, argIndex);
            if (_parameterRefs.TryGetValue(key, out var existing))
            {
                return existing;
            }

            var variableRef = _owner.CreateParameterVariableRef(
                name,
                type,
                argIndex,
                SubprogramRef,
                _fileRef,
                FunctionLocation.Line);
            _parameterRefs[key] = variableRef;
            return variableRef;
        }

        public string GetLocalVariableRef(string name, StarkTypeSymbol type, SourceLocation? location)
        {
            var normalizedLocation = _owner.ResolveLocation(location ?? FunctionLocation);
            var key = (name, normalizedLocation.Line, normalizedLocation.Column);
            if (_localRefs.TryGetValue(key, out var existing))
            {
                return existing;
            }

            var variableRef = _owner.CreateLocalVariableRef(
                name,
                type,
                SubprogramRef,
                _owner.GetFileRef(normalizedLocation.FilePath),
                normalizedLocation.Line);
            _localRefs[key] = variableRef;
            return variableRef;
        }
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

    private enum SystemMathBuiltinKind
    {
        Sin,
        Cos,
        Tan,
        Exp,
        Exp2,
        Log,
        Log2,
        Log10,
        Asin,
        Acos,
        Atan,
        Atan2,
        Pow,
        Sinh,
        Cosh,
        Tanh,
        SinCos,
        Sqrt,
        FusedMultiplyAdd,
        ReciprocalEstimate,
        ReciprocalSqrtEstimate,
        Ceiling,
        Floor,
        Truncate,
        Round,
        Min,
        Max
    }

    private enum SystemBitOperationsBuiltinKind
    {
        LeadingZeroCount,
        TrailingZeroCount,
        PopCount,
        RotateLeft,
        RotateRight
    }

    private readonly record struct SystemMathSinCosSignature(
        StarkTypeSymbol ScalarType,
        int SinFieldIndex,
        int CosFieldIndex);

    private static StringConstantKey CreateStringConstantKey(string literalText, StarkTypeSymbol type)
    {
        if (type.Kind is not (StarkTypeKind.Ascii or StarkTypeKind.Unicode))
        {
            throw new InvalidOperationException($"String constant key requires an ascii/unicode type, but found '{type.DisplayName}'.");
        }

        return type.Kind switch
        {
            StarkTypeKind.Ascii => CreateAsciiStringConstantKey(literalText),
            StarkTypeKind.Unicode => CreateUnicodeStringConstantKey(literalText),
            _ => throw new InvalidOperationException($"String constant key requires an ascii/unicode type, but found '{type.DisplayName}'.")
        };
    }

    private static StringConstantKey CreateAsciiStringConstantKey(string literalText)
    {
        var bytes = DecodeAsciiStringLiteral(literalText);
        var terminated = new byte[bytes.Length + 1];
        bytes.CopyTo(terminated, 0);

        return new StringConstantKey(
            StarkTypeKind.Ascii,
            $"[{terminated.Length} x i8]",
            EncodeLlvmByteString(terminated),
            bytes.Length,
            AlignmentBytes: 1);
    }

    private static StringConstantKey CreateUnicodeStringConstantKey(string literalText)
    {
        var codeUnits = DecodeUnicodeStringLiteral(literalText);
        var terminated = new int[codeUnits.Length + 1];
        codeUnits.CopyTo(terminated, 0);

        return new StringConstantKey(
            StarkTypeKind.Unicode,
            $"[{terminated.Length} x i32]",
            EncodeLlvmI32Array(terminated),
            codeUnits.Length,
            AlignmentBytes: 4);
    }

    private readonly record struct StringConstantKey(
        StarkTypeKind TypeKind,
        string ArrayType,
        string Initializer,
        int DataLength,
        int AlignmentBytes);

    private sealed record EmittedStringConstant(string SymbolName, string ArrayType, string Initializer, int DataLength, int AlignmentBytes);
}
