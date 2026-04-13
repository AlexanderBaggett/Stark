using System.Numerics;
using System.Text;
using System.Globalization;
using Stark.Parsing;
using Stark.Compiler.LlvmIrEmission;

namespace Stark.Compiler;

internal sealed class LlvmIrEmitter
{
    private const string AsciiStringTypeName = "stark_ascii";
    private const string UnicodeStringTypeName = "stark_unicode";
    private const int AggregateScalarizationThresholdBytes = 16;
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
    private readonly bool _isOptimizedBuild;
    private readonly DebugMetadataEmitter _debugInfo;
    private readonly LlvmEmissionContext _emissionContext;
    private readonly LlvmFunctionAttributeBuilder _functionAttributeBuilder;
    private readonly LlvmFunctionSignatureBuilder _functionSignatureBuilder;
    private readonly LlvmBuiltinAndHelperEmitter _builtinAndHelperEmitter;
    private readonly LlvmGlobalInitializerPlanner _globalInitializerPlanner;
    private readonly LlvmModuleSurfaceEmitter _moduleSurfaceEmitter;

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
        bool isOptimizedBuild = false,
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
            isOptimizedBuild,
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
        bool isOptimizedBuild = false,
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
        _isOptimizedBuild = isOptimizedBuild;
        _stringConstants = CollectStringConstants(parseResult, ssa);
        _objectCreationConstructors = typeModel.ObjectCreations
            .GroupBy(static record => new ObjectCreationKey(record.ExpressionText, record.Location.Line, record.Location.Column))
            .ToDictionary(static group => group.Key, static group => group.Last().Constructor);
        _globalSymbols = BuildGlobalSymbolMap();
        _globalsEligibleForLocalUnnamedAddr = BuildGlobalsEligibleForLocalUnnamedAddr();
        _allFunctionEffects = BuildAllFunctionEffects(effectModel, specializationCodegenStrategy);
        _allFunctionSignatures = BuildAllFunctionSignatures(typeModel, ssa);
        _allAbiFunctions = BuildAllAbiFunctions(_allFunctionSignatures, abiModel, _allFunctionEffects, typeModel.NamedTypes, enumLayoutModel.Layouts);
        _publishedConcreteLayouts = BuildPublishedConcreteLayouts(loadedModules);
        _publishedFunctionSemantics = BuildPublishedFunctionSemantics(loadedModules, specializationCodegenStrategy);
        _specializationTemplateNames = BuildSpecializationTemplateNames(specializationCodegenStrategy);
        _functionLocations = BuildFunctionLocationMap(loadedModules, input.FilePath);
        _closedWorldImportedLawClones = BuildClosedWorldImportedLawClones();
        _referencedImportedFunctions = CollectReferencedImportedFunctions(ssa, _closedWorldImportedLawClones.Values);
        _debugInfo = new DebugMetadataEmitter(
            input.FilePath ?? $"{syntaxModel.ModuleName}.stark",
            _isOptimizedBuild,
            TryGetConcreteTypeLayout);
        _emissionContext = LlvmEmissionContextBuilder.Build(
            syntaxModel.ModuleName,
            AsciiStringTypeName,
            UnicodeStringTypeName,
            parseResult,
            loadedModules,
            typeModel,
            enumLayoutModel,
            _stringConstants.Values,
            targetInfo,
            MapType,
            TryGetConcreteTypeLayout,
            ResolveNamedTypeSymbol,
            namedType => TryGetScalarizableNamedAggregateFields(namedType, out var orderedFields) ? orderedFields : null,
            ResolveStringConstant,
            TryGetGlobalAlignmentBytes,
            ResolveGlobalSymbolName,
            IsConstGlobalName,
            ShouldInternalize,
            expression => TryUnwrapSimplePrimaryExpression(expression, out var primaryExpression) ? primaryExpression : null,
            objectCreation => _objectCreationConstructors.TryGetValue(
                    new ObjectCreationKey(
                        objectCreation.GetText(),
                        objectCreation.Start.Line,
                        objectCreation.Start.Column + 1),
                    out var constructor)
                ? constructor
                : null,
            GetAllocatorSizeType,
            () => _debugInfo.Enabled,
            () => _debugInfo.EmptyTupleRef);
        _globalInitializerPlanner = new LlvmGlobalInitializerPlanner(_emissionContext);
        _functionAttributeBuilder = new LlvmFunctionAttributeBuilder(_emissionContext);
        _functionSignatureBuilder = new LlvmFunctionSignatureBuilder(_emissionContext, _functionAttributeBuilder);
        _builtinAndHelperEmitter = new LlvmBuiltinAndHelperEmitter(
            _emissionContext,
            (internalize, function, abiFunction, effects, memoryEffects, parameterEffects)
                => BuildDefinitionSignature(internalize, function, abiFunction, effects, memoryEffects, parameterEffects),
            EnumerateBinaryOperations,
            EscapeInlineAsmString,
            UsesLifetimeMarkers,
            UsesHeapAllocator,
            UsesMemcpyInlineIntrinsic,
            UsesMemsetInlineIntrinsic);
        _moduleSurfaceEmitter = new LlvmModuleSurfaceEmitter(
            _emissionContext,
            _globalsEligibleForLocalUnnamedAddr,
            _globalInitializerPlanner);
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
        _moduleSurfaceEmitter.Emit(builder);
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
        return LlvmSpecializationEmissionPlanner.BuildFunctionLocationMap(loadedModules, rootInputFilePath);
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
        return LlvmSpecializationEmissionPlanner.BuildSpecializationTemplateNames(specializationCodegenStrategy);
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
        return LlvmSpecializationEmissionPlanner.BuildPublishedConcreteLayouts(loadedModules);
    }

    private static IReadOnlyDictionary<string, ImportedFunctionSemanticSummary> BuildPublishedFunctionSemantics(
        LoadedModuleSet loadedModules,
        SpecializationCodegenStrategyModel? specializationCodegenStrategy)
    {
        return LlvmSpecializationEmissionPlanner.BuildPublishedFunctionSemantics(loadedModules, specializationCodegenStrategy);
    }

    private void EmitMaterializedSpecializationDefinitions(
        StringBuilder builder,
        ISet<string> handledFunctionNames,
        Func<string, string, AbiFunctionSignature?> resolveCallAbi)
    {
        LlvmSpecializationEmissionPlanner.EmitMaterializedSpecializationDefinitions(
            builder,
            handledFunctionNames,
            resolveCallAbi,
            _specializationCodegenStrategy,
            _ssa,
            _allFunctionSignatures,
            _allAbiFunctions,
            _allFunctionEffects,
            GetParameterEffects,
            GetFunctionMemoryEffects,
            BuildDeclarationSignature,
            EmitFunctionDefinition,
            (classification, functionName, reason, bodyLoweringKind, supportsDirectCodeGeneration)
                => LogLlvmFallback(
                    classification,
                    functionName,
                    reason,
                    bodyLoweringKind,
                    supportsDirectCodeGeneration,
                    operation: "EmitFunctionDefinition"),
            EscapeIdentifier);
    }

    private IReadOnlyDictionary<string, ImportedLawClonePlan> BuildClosedWorldImportedLawClones()
    {
        return LlvmSpecializationEmissionPlanner.BuildClosedWorldImportedLawClones(
            _loadedModules,
            _ssa,
            _effectModel,
            _syntaxModel,
            _typeModel,
            _enumLayoutModel,
            _closedWorldModel,
            _specializationCodegenStrategy,
            _allFunctionEffects,
            _allFunctionSignatures);
    }

    private void LogSpecializationCodegenStrategies()
    {
        LlvmSpecializationEmissionPlanner.LogSpecializationCodegenStrategies(_logs, _specializationCodegenStrategy);
    }

    private static AbiFunctionSignature BuildSyntheticAbiSignature(
        TypedFunctionSignature function,
        string symbolName,
        bool isFfi,
        IReadOnlyDictionary<string, NamedTypeSymbol> namedTypes,
        IReadOnlyDictionary<string, EnumLayoutSymbol> enumLayouts)
    {
        return LlvmSpecializationEmissionPlanner.BuildSyntheticAbiSignature(function, symbolName, isFfi, namedTypes, enumLayouts);
    }

    private int? TryGetGlobalAlignmentBytes(StarkTypeSymbol type)
    {
        return LlvmAggregateEmissionSupport.TryGetGlobalAlignmentBytes(
            type,
            _targetInfo,
            _typeModel.NamedTypes,
            _enumLayoutModel.Layouts,
            _publishedConcreteLayouts);
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

    private static bool ShouldEmitExternalConstPlaceholder(
        TypedGlobalSymbol global,
        StarkParser.VariableInitializerContext initializer)
    {
        return global.IsConst
            && global.Type.Kind == StarkTypeKind.RawPointer
            && initializer.expression() is { } expression
            && TryUnwrapSimplePrimaryExpression(expression, out var primaryExpression)
            && primaryExpression.literal()?.NULL() is not null;
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
        _builtinAndHelperEmitter.EmitIntrinsicDeclarations(builder, _allFunctionSignatures.Values);
    }

    private void EmitInternalHelperDefinitions(StringBuilder builder)
    {
        _builtinAndHelperEmitter.EmitInternalHelperDefinitions(builder);
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
        var pointerSizeBytes = LlvmAggregateEmissionSupport.TryGetConcreteTypeLayout(
                StarkTypeSymbols.RawPointer(StarkTypeSymbols.Integer(8), isMutable: false),
                _targetInfo,
                _typeModel.NamedTypes,
                _enumLayoutModel.Layouts,
                _publishedConcreteLayouts)
            ?.SizeBytes
            ?? 8;
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

        var bodyEmitter = new LlvmFunctionBodyEmitter(
            functionBuilder,
            function,
            abiFunction,
            resolveCallAbi,
            ssaFunction,
            _emissionContext,
            debugFunction);
        bodyEmitter.Emit();
        functionBuilder.AppendLine("}");
        builder.Append(functionBuilder);
    }

    private bool IsConstGlobalName(string globalName)
    {
        return _typeModel.Globals.TryGetValue(globalName, out var global)
            && global.IsConst;
    }

    private EmittedStringConstant ResolveStringConstant(string literalText, StarkTypeSymbol type)
    {
        if (!_stringConstants.TryGetValue(CreateStringConstantKey(literalText, type), out var constant))
        {
            throw new InvalidOperationException(
                $"Missing string constant for literal '{literalText}' with type '{type.DisplayName}'.");
        }

        return constant;
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
        return _functionSignatureBuilder.BuildDeclarationSignature(
            internalize,
            function,
            abiFunction,
            effects,
            memoryEffects,
            parameterEffects);
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
        return _functionSignatureBuilder.BuildDefinitionSignature(
            internalize,
            function,
            abiFunction,
            effects,
            memoryEffects,
            parameterEffects,
            specializationLinkage);
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
        return _builtinAndHelperEmitter.TryEmitBuiltinFunctionDefinition(
            builder,
            internalize,
            moduleName,
            function,
            abiFunction,
            effects,
            memoryEffects,
            parameterEffects);
    }

    private IReadOnlyDictionary<string, ParameterMemoryEffectSummary>? GetBuiltinParameterEffects(
        string moduleName,
        string functionName,
        TypedFunctionSignature function)
    {
        return _builtinAndHelperEmitter.GetBuiltinParameterEffects(moduleName, functionName, function);
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
        return LlvmAggregateEmissionSupport.TryGetConcreteTypeLayout(
            type,
            _targetInfo,
            _typeModel.NamedTypes,
            _enumLayoutModel.Layouts,
            _publishedConcreteLayouts);
    }

    private NamedTypeSymbol? ResolveNamedTypeSymbol(StarkTypeSymbol type)
    {
        return LlvmAggregateEmissionSupport.ResolveNamedTypeSymbol(type, _typeModel.NamedTypes);
    }

    private bool TryGetScalarizableNamedAggregateFields(
        NamedTypeSymbol namedType,
        out IReadOnlyList<FieldSymbol> orderedFields)
    {
        return LlvmAggregateEmissionSupport.TryGetScalarizableNamedAggregateFields(
            namedType,
            _enumLayoutModel.Layouts,
            out orderedFields);
    }

    private readonly record struct ObjectCreationKey(string Text, int Line, int Column);

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

}
