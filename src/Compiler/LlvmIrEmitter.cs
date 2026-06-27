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
    private const int AggregateInlineMemcpyThresholdBytes = 256;

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
    private readonly SsaValueFactModel? _ssaValueFacts;
    private readonly CompilerLogBag? _logs;
    private readonly AbiModel _abiModel;
    private readonly SsaIrModule _ssa;
    // Name -> SSA function index, so per-function emission does O(1) lookups instead of
    // an O(N) linear scan of _ssa.Functions (which made whole-module emission O(N^2)).
    private readonly Dictionary<string, SsaFunction> _ssaFunctionsByName;
    private readonly HashSet<string> _ssaFunctionBodyNames;
    private Dictionary<string, ClosureLambdaTypingRecord>? _capturingClosureLambdasByName;
    private readonly LlvmTargetInfo? _targetInfo;
    private readonly bool _internalizeModulePrivate;
    private readonly bool _enableOptimizedRawPointerLoopIntrinsics;
    private readonly IReadOnlyDictionary<string, string> _globalSymbols;
    private readonly IReadOnlySet<string> _globalsEligibleForLocalUnnamedAddr;
    private readonly IReadOnlyDictionary<string, ImportedGlobalDeclarationPlan> _importedCloneReferencedGlobals;
    private readonly IReadOnlyDictionary<StringConstantKey, EmittedStringConstant> _stringConstants;
    private readonly IReadOnlyDictionary<ObjectCreationKey, TypedConstructorShape?> _objectCreationConstructors;
    private readonly IReadOnlyDictionary<string, FunctionEffectProfile> _allFunctionEffects;
    private readonly IReadOnlyDictionary<string, TypedFunctionSignature> _allFunctionSignatures;
    private readonly IReadOnlyDictionary<string, AbiFunctionSignature> _allAbiFunctions;
    private readonly IReadOnlySet<string>? _importedInlineCloneSeedFunctions;
    private readonly IReadOnlySet<string>? _ownedFunctionDefinitionFilter;
    private readonly IReadOnlyDictionary<string, ConcreteTypeLayout> _publishedConcreteLayouts;
    private readonly IReadOnlyDictionary<string, ImportedFunctionSemanticSummary> _publishedFunctionSemantics;
    private readonly IReadOnlyDictionary<string, string> _specializationTemplateNames;
    private readonly IReadOnlyDictionary<string, SourceLocation> _functionLocations;
    private readonly IReadOnlyDictionary<string, ImportedLawClonePlan> _closedWorldImportedLawClones;
    private readonly IReadOnlyDictionary<string, ImportedInlineBodyPlan> _closedWorldImportedInlineBodies;
    private readonly IReadOnlySet<string> _referencedImportedFunctions;
    private readonly bool _isOptimizedBuild;
    private readonly bool _emitFallbackDeclarationsForSourceBodies;
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
        bool enableOptimizedRawPointerLoopIntrinsics = false,
        SemanticValidationModel? semanticValidation = null,
        ClosedWorldOptimizationModel? closedWorldModel = null,
        SpecializationCodegenStrategyModel? specializationCodegenStrategy = null,
        CompilerLogBag? logs = null,
        SsaValueFactModel? ssaValueFacts = null,
        IReadOnlySet<string>? importedInlineCloneSeedFunctions = null,
        bool emitFallbackDeclarationsForSourceBodies = true)
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
            enableOptimizedRawPointerLoopIntrinsics,
            semanticValidation,
            closedWorldModel,
            specializationCodegenStrategy,
            logs,
            ssaValueFacts,
            importedInlineCloneSeedFunctions,
            emitFallbackDeclarationsForSourceBodies)
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
        bool enableOptimizedRawPointerLoopIntrinsics = false,
        SemanticValidationModel? semanticValidation = null,
        ClosedWorldOptimizationModel? closedWorldModel = null,
        SpecializationCodegenStrategyModel? specializationCodegenStrategy = null,
        CompilerLogBag? logs = null,
        SsaValueFactModel? ssaValueFacts = null,
        IReadOnlySet<string>? importedInlineCloneSeedFunctions = null,
        bool emitFallbackDeclarationsForSourceBodies = true)
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
        _ssaValueFacts = ssaValueFacts;
        _logs = logs;
        _abiModel = abiModel;
        _ssa = ssa;
        _ssaFunctionsByName = new Dictionary<string, SsaFunction>(StringComparer.Ordinal);
        _ssaFunctionBodyNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var ssaFunctionByName in ssa.Functions)
        {
            // First occurrence wins, matching the prior FirstOrDefault(by name) semantics.
            _ssaFunctionsByName.TryAdd(ssaFunctionByName.Name, ssaFunctionByName);
            // Any same-named function with a body marks the name as having an SSA body,
            // matching the prior `_ssa.Functions.Any(name == X && HasBody)` semantics.
            if (ssaFunctionByName.HasBody)
            {
                _ssaFunctionBodyNames.Add(ssaFunctionByName.Name);
            }
        }
        _targetInfo = targetInfo;
        _internalizeModulePrivate = internalizeModulePrivate;
        _enableOptimizedRawPointerLoopIntrinsics = enableOptimizedRawPointerLoopIntrinsics;
        _isOptimizedBuild = isOptimizedBuild;
        _emitFallbackDeclarationsForSourceBodies = emitFallbackDeclarationsForSourceBodies;
        _stringConstants = CollectStringConstants(parseResult, ssa);
        _objectCreationConstructors = typeModel.ObjectCreations
            .GroupBy(static record => new ObjectCreationKey(record.EnclosingFunctionName, record.ExpressionText, record.Location.FilePath, record.Location.Line, record.Location.Column))
            .ToDictionary(static group => group.Key, static group => group.Last().Constructor);
        _publishedConcreteLayouts = BuildPublishedConcreteLayouts(loadedModules);
        _allFunctionEffects = BuildAllFunctionEffects(effectModel, typeModel, ssa, specializationCodegenStrategy);
        _allFunctionSignatures = BuildAllFunctionSignatures(typeModel, ssa, specializationCodegenStrategy);
        _allAbiFunctions = BuildAllAbiFunctions(
            _allFunctionSignatures,
            abiModel,
            _allFunctionEffects,
            typeModel.NamedTypes,
            enumLayoutModel.Layouts,
            targetInfo,
            _publishedConcreteLayouts);
        _importedInlineCloneSeedFunctions = importedInlineCloneSeedFunctions;
        _ownedFunctionDefinitionFilter = BuildOwnedFunctionDefinitionFilter();
        _publishedFunctionSemantics = BuildPublishedFunctionSemantics(loadedModules, specializationCodegenStrategy);
        _specializationTemplateNames = BuildSpecializationTemplateNames(specializationCodegenStrategy);
        _functionLocations = BuildFunctionLocationMap(loadedModules, input.FilePath);
        _closedWorldImportedLawClones = BuildClosedWorldImportedLawClones();
        _closedWorldImportedInlineBodies = BuildClosedWorldImportedInlineBodies();
        _importedCloneReferencedGlobals = BuildImportedCloneReferencedGlobalDeclarations();
        _globalSymbols = BuildGlobalSymbolMap();
        _globalsEligibleForLocalUnnamedAddr = BuildGlobalsEligibleForLocalUnnamedAddr();
        _referencedImportedFunctions = CollectReferencedImportedFunctions(
            ssa,
            _closedWorldImportedLawClones.Values,
            _closedWorldImportedInlineBodies.Values,
            _ownedFunctionDefinitionFilter);
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
            IsImmutableGlobalName,
            ShouldInternalize,
            expression => TryUnwrapSimplePrimaryExpression(expression, out var primaryExpression) ? primaryExpression : null,
            objectCreation => _objectCreationConstructors.TryGetValue(
                    new ObjectCreationKey(
                        null,
                        objectCreation.GetText(),
                        _input.FilePath,
                        objectCreation.Start.Line,
                        objectCreation.Start.Column + 1),
                    out var constructor)
                ? constructor
                : null,
            GetAllocatorSizeType,
            () => _debugInfo.Enabled,
            () => _debugInfo.EmptyTupleRef,
            type => _debugInfo.GetValueRangeMetadataRef(type),
            (type, range) => _debugInfo.GetValueRangeMetadataRef(type, range),
            (key, displayName) => _debugInfo.GetTbaaTypeDescriptorRef(key, displayName),
            (key, displayName, fields) => _debugInfo.GetTbaaStructTypeDescriptorRef(key, displayName, fields),
            (baseTypeDescriptorRef, accessTypeDescriptorRef, offsetBytes) => _debugInfo.GetTbaaAccessTagRef(
                baseTypeDescriptorRef,
                accessTypeDescriptorRef,
                offsetBytes),
            (key, displayName) => _debugInfo.GetAliasScopeDomainRef(key, displayName),
            (key, domainRef, displayName) => _debugInfo.GetAliasScopeRef(key, domainRef, displayName),
            items => _debugInfo.GetMetadataTupleRef(items),
            (key, buildBody) => _debugInfo.GetSelfReferentialMetadataRef(key, buildBody),
            functionName => _allFunctionEffects.TryGetValue(functionName, out var effects) ? effects : null);
        _globalInitializerPlanner = new LlvmGlobalInitializerPlanner(_emissionContext);
        _functionAttributeBuilder = new LlvmFunctionAttributeBuilder(_emissionContext);
        _functionSignatureBuilder = new LlvmFunctionSignatureBuilder(_emissionContext, _functionAttributeBuilder);
        _builtinAndHelperEmitter = new LlvmBuiltinAndHelperEmitter(
            _emissionContext,
            (internalize, function, abiFunction, effects, memoryEffects, parameterEffects)
                => BuildDefinitionSignature(internalize, function, abiFunction, effects, memoryEffects, parameterEffects),
            ResolveFunctionAbi,
            EnumerateBinaryOperations,
            () => _ssa.Functions,
            EscapeInlineAsmString,
            UsesLifetimeMarkers,
            UsesInvariantStartIntrinsic,
            UsesHeapAllocator,
            UsesUnreachableTrapHelper,
            UsesAssumeIntrinsic,
            UsesMemcpyIntrinsic,
            UsesMemmoveIntrinsic,
            UsesMemcpyInlineIntrinsic,
            UsesMemsetIntrinsic,
            UsesMemsetInlineIntrinsic);
        _moduleSurfaceEmitter = new LlvmModuleSurfaceEmitter(
            _emissionContext,
            _globalsEligibleForLocalUnnamedAddr,
            _importedCloneReferencedGlobals,
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
        builder.AppendLine("; Body emission fallback is reported by the compiler pipeline.");
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
            var ssaFunction = _ssaFunctionsByName.GetValueOrDefault(resolvedName);
            var parameterEffects = GetParameterEffects(resolvedName, function.HasBody && !effects.IsFfi)
                ?? GetBuiltinParameterEffects(_syntaxModel.ModuleName, resolvedName, signature);
            var memoryEffects = GetFunctionMemoryEffects(resolvedName, function.HasBody && !effects.IsFfi);

            builder.AppendLine($"; visibility: {declaration.Visibility.ToString().ToLowerInvariant()}");

            if (IsTraitContractFunction(signature))
            {
                builder.AppendLine($"; trait contract: {resolvedName}");
                builder.AppendLine($"; declaration omitted for trait contract '{resolvedName}' because traits have no runtime callable surface.");
                builder.AppendLine();
                continue;
            }

            if (IsOpenGenericTemplate(signature))
            {
                builder.AppendLine($"; open generic template: {resolvedName}");
                builder.AppendLine($"; declaration omitted for open generic template '{resolvedName}' because only concrete instantiations have a runtime ABI.");
                builder.AppendLine();
                continue;
            }

            if (_ownedFunctionDefinitionFilter is not null
                && !_ownedFunctionDefinitionFilter.Contains(resolvedName))
            {
                // This function is outside this module's owned (hot-path) emission
                // surface, so we would normally emit only a declaration and rely on the
                // linked dependency to provide the body. But if the dependency pruned
                // this symbol from its own emission (e.g. a `public` helper that nothing
                // in the dependency's reachable graph references, such as a
                // `System.Testing` text forwarder used only by test code), that bare
                // declaration links to nothing. When the body was lowered into this
                // module, emit it as a `linkonce_odr` (weak, deduplicated) definition
                // instead: a real strong definition in any linked object still wins, but
                // otherwise the weak copy keeps cross-module calls resolvable. Weak
                // definitions that get fully inlined are discarded, so this does not
                // bloat the common case.
                if (function.HasBody
                    && ssaFunction is { SupportsDirectCodeGeneration: true })
                {
                    try
                    {
                        EmitFunctionDefinition(
                            builder,
                            internalize: false,
                            availableExternally: false,
                            signature,
                            abiSignature,
                            effects,
                            memoryEffects,
                            ssaFunction,
                            parameterEffects,
                            resolveCallAbi,
                            specializationLinkage: MonomorphizationLinkageKind.WeakOdrPreserved);
                        builder.AppendLine();
                        continue;
                    }
                    catch (UnsupportedBodyEmissionException)
                    {
                        // Fall through to the pruned-declaration path below.
                    }
                }

                builder.AppendLine($"; source dependency body pruned: {resolvedName}");
                builder.AppendLine(BuildDeclarationSignature(false, signature, abiSignature, effects, memoryEffects, parameterEffects));
                builder.AppendLine();
                continue;
            }

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
                if (!_emitFallbackDeclarationsForSourceBodies)
                {
                    builder.AppendLine($"; declaration omitted for source asm body '{resolvedName}' because LLVM body fallback is disabled for accepted source functions.");
                    builder.AppendLine();
                    continue;
                }

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
                    EmitFunctionDefinition(
                        builder,
                        definitionInternalize,
                        availableExternally: false,
                        signature,
                        abiSignature,
                        effects,
                        memoryEffects,
                        ssaFunction,
                        parameterEffects,
                        resolveCallAbi);
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
                    if (!_emitFallbackDeclarationsForSourceBodies)
                    {
                        builder.AppendLine($"; declaration omitted for source body '{resolvedName}' because LLVM body fallback is disabled for accepted source functions.");
                        builder.AppendLine();
                        continue;
                    }
                }
            }
            else if (function.HasBody && !IsOpenGenericTemplate(signature))
            {
                builder.AppendLine($"; LLVM body emission pending for {resolvedName}");
                LogLlvmFallback(
                    "llvm-body-pending",
                    resolvedName,
                    "SSA lowering did not leave this function in a direct-codegen-capable form, so LLVM emitted only a declaration.",
                    ssaFunction?.BodyLoweringKind ?? FunctionBodyLoweringKind.DeclarationOnly,
                    ssaFunction?.SupportsDirectCodeGeneration ?? false,
                    operation: "EmitFunctionDefinition");
                if (!_emitFallbackDeclarationsForSourceBodies)
                {
                    builder.AppendLine($"; declaration omitted for source body '{resolvedName}' because LLVM body fallback is disabled for accepted source functions.");
                    builder.AppendLine();
                    continue;
                }
            }

            builder.AppendLine(BuildDeclarationSignature(false, signature, abiSignature, effects, memoryEffects, parameterEffects));
            builder.AppendLine();
        }

        EmitMaterializedSpecializationDefinitions(builder, handledFunctionNames, resolveCallAbi);

        var syntheticLambdaNames = _typeModel.Lambdas
            .Select(static lambda => lambda.FunctionName)
            .Concat(_typeModel.ClosureLambdas.Select(static lambda => lambda.FunctionName))
            .Concat(_typeModel.ClosureLambdas
                .Where(static lambda => lambda.ClosureType.ClosureStorageKind == StarkClosureStorageKind.Heap && lambda.HasCaptures)
                .Select(static lambda => CallableValueFacts.BuildClosureDropFunctionName(lambda.FunctionName)))
            .Concat(_typeModel.ClosureLambdas.Any(static lambda => lambda.ClosureType.ClosureStorageKind == StarkClosureStorageKind.Heap && !lambda.HasCaptures)
                ? [CallableValueFacts.EmptyClosureDropFunctionName]
                : [])
            .Concat(_typeModel.ClosureFunctionPromotions.Select(static promotion => promotion.AdapterFunctionName))
            .Concat(_typeModel.NamedTypes.Values
                .Where(type => type.Kind is DeclarationKind.Struct or DeclarationKind.Record
                    && !type.IsGeneric
                    && type.ImplementedTraits.Any(traitName =>
                        _typeModel.NamedTypes.TryGetValue(traitName, out var traitType) && traitType.IsDynTrait))
                .Select(static type => DynTraitFacts.BuildDropThunkName(type.Name)))
            .ToHashSet(StringComparer.Ordinal);

        foreach (var clone in _closedWorldImportedLawClones.Values.OrderBy(static clone => clone.FunctionName, StringComparer.Ordinal))
        {
            var parameterEffects = GetParameterEffects(clone.FunctionName, hasBody: false);
            var memoryEffects = GetFunctionMemoryEffects(clone.FunctionName, hasBody: false);
            builder.AppendLine($"; closed-world imported law clone: {clone.FunctionName}");
            EmitFunctionDefinition(
                builder,
                internalize: true,
                availableExternally: false,
                clone.Signature,
                clone.AbiSignature,
                clone.Effects,
                memoryEffects,
                clone.SsaFunction,
                parameterEffects,
                resolveCallAbi);
            builder.AppendLine();
        }

        foreach (var clone in _closedWorldImportedInlineBodies.Values.OrderBy(static clone => clone.FunctionName, StringComparer.Ordinal))
        {
            var parameterEffects = GetParameterEffects(clone.FunctionName, hasBody: true);
            var memoryEffects = GetFunctionMemoryEffects(clone.FunctionName, hasBody: true);
            builder.AppendLine($"; closed-world imported inline body: {clone.FunctionName}");
            EmitFunctionDefinition(
                builder,
                internalize: true,
                availableExternally: false,
                clone.Signature,
                clone.AbiSignature,
                clone.Effects,
                memoryEffects,
                clone.SsaFunction,
                parameterEffects,
                resolveCallAbi);
            builder.AppendLine();
        }

        var emittedDeclarationSymbols = new HashSet<string>(StringComparer.Ordinal);
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

            if (IsTraitContractFunction(signature))
            {
                continue;
            }

            if (IsOpenGenericTemplate(signature))
            {
                continue;
            }

            var ssaFunction = _ssaFunctionsByName.GetValueOrDefault(abiFunction.Name);
            var hasBody = ssaFunction is { HasBody: true };
            var parameterEffects = GetParameterEffects(abiFunction.Name, hasBody)
                ?? GetBuiltinParameterEffects(moduleName: string.Empty, abiFunction.Name, signature);
            var memoryEffects = GetFunctionMemoryEffects(abiFunction.Name, hasBody);
            if (_ownedFunctionDefinitionFilter is not null
                && hasBody
                && IsOwnedModuleFunctionName(abiFunction.Name)
                && !_ownedFunctionDefinitionFilter.Contains(abiFunction.Name))
            {
                continue;
            }

            if (syntheticLambdaNames.Contains(abiFunction.Name)
                && ssaFunction is null)
            {
                continue;
            }

            if (syntheticLambdaNames.Contains(abiFunction.Name)
                && ssaFunction is { HasBody: true, SupportsDirectCodeGeneration: true })
            {
                builder.AppendLine($"; synthetic definition: {abiFunction.Name}");
                EmitFunctionDefinition(
                    builder,
                    internalize: true,
                    availableExternally: false,
                    signature,
                    abiFunction,
                    effects,
                    memoryEffects,
                    ssaFunction,
                    parameterEffects,
                    resolveCallAbi);
                builder.AppendLine();
                continue;
            }

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

            // Distinct Stark FFI declarations (for example per-platform modules) can share one
            // binary symbol; LLVM allows only a single declaration per symbol in a module.
            if (!emittedDeclarationSymbols.Add(abiFunction.SymbolName))
            {
                builder.AppendLine($"; imported declaration merged into earlier declaration of '@{abiFunction.SymbolName}': {abiFunction.Name}");
                builder.AppendLine();
                continue;
            }

            builder.AppendLine($"; imported declaration: {abiFunction.Name}");
            builder.AppendLine(BuildDeclarationSignature(false, signature, abiFunction, effects, memoryEffects, parameterEffects));
            builder.AppendLine();
        }

        EmitReferencedImportedFunctionDeclarations(builder, handledFunctionNames, emittedDeclarationSymbols);

        _debugInfo.EmitModuleMetadata(builder);

        return new LlvmIrModule(_syntaxModel.ModuleName, builder.ToString().TrimEnd(), _ssa.AddressTakenFunctions);
    }

    private void EmitReferencedImportedFunctionDeclarations(
        StringBuilder builder,
        ISet<string> handledFunctionNames,
        ISet<string> emittedDeclarationSymbols)
    {
        foreach (var functionName in _referencedImportedFunctions.OrderBy(static name => name, StringComparer.Ordinal))
        {
            if (handledFunctionNames.Contains(functionName)
                || _abiModel.Functions.ContainsKey(functionName)
                || !_allAbiFunctions.TryGetValue(functionName, out var abiFunction)
                || !_allFunctionSignatures.TryGetValue(functionName, out var signature)
                || !_allFunctionEffects.TryGetValue(functionName, out var effects))
            {
                continue;
            }

            if (IsTraitContractFunction(signature)
                || IsOpenGenericTemplate(signature))
            {
                continue;
            }

            var parameterEffects = GetParameterEffects(functionName, hasBody: false)
                ?? GetBuiltinParameterEffects(moduleName: string.Empty, functionName, signature);
            var memoryEffects = GetFunctionMemoryEffects(functionName, hasBody: false);
            if (TryEmitBuiltinFunctionDefinition(
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

            if (!emittedDeclarationSymbols.Add(abiFunction.SymbolName))
            {
                builder.AppendLine($"; referenced imported declaration merged into earlier declaration of '@{abiFunction.SymbolName}': {functionName}");
                builder.AppendLine();
                continue;
            }

            builder.AppendLine($"; referenced imported declaration: {functionName}");
            builder.AppendLine(BuildDeclarationSignature(false, signature, abiFunction, effects, memoryEffects, parameterEffects));
            builder.AppendLine();
        }
    }

    private static bool IsOpenGenericTemplate(TypedFunctionSignature signature) =>
        signature.IsGeneric && !signature.IsGenericInstantiation;

    private static bool IsOwnedModuleFunctionName(string functionName) =>
        !functionName.Contains('.', StringComparison.Ordinal);

    private bool IsTraitContractFunction(TypedFunctionSignature signature)
    {
        var sourceName = signature.DisplaySourceName;
        var separatorIndex = sourceName.LastIndexOf('.');
        if (separatorIndex <= 0)
        {
            return false;
        }

        var containingTypeName = sourceName[..separatorIndex];
        return (_typeModel.NamedTypes.TryGetValue(containingTypeName, out var namedType)
                && namedType.Kind == DeclarationKind.Trait)
            || _syntaxModel.Declarations.Any(declaration =>
                declaration.Kind == DeclarationKind.Trait
                && string.Equals(declaration.Name, containingTypeName, StringComparison.Ordinal));
    }

    private static IReadOnlySet<string> CollectReferencedImportedFunctions(
        SsaIrModule ssa,
        IEnumerable<ImportedLawClonePlan> importedLawClones,
        IEnumerable<ImportedInlineBodyPlan> importedInlineBodies,
        IReadOnlySet<string>? ownedFunctionDefinitionFilter)
    {
        var referencedFunctions = new HashSet<string>(StringComparer.Ordinal);

        foreach (var function in ssa.Functions)
        {
            if (ownedFunctionDefinitionFilter is not null
                && !ownedFunctionDefinitionFilter.Contains(function.Name))
            {
                continue;
            }

            CollectReferencedFunctions(function, referencedFunctions);
        }

        foreach (var clone in importedLawClones)
        {
            CollectReferencedFunctions(clone.SsaFunction, referencedFunctions);
        }

        foreach (var clone in importedInlineBodies)
        {
            CollectReferencedFunctions(clone.SsaFunction, referencedFunctions);
        }

        return referencedFunctions;
    }

    private static void CollectReferencedFunctions(SsaFunction function, ISet<string> referencedFunctions)
    {
        foreach (var block in function.Blocks)
        {
            foreach (var phi in block.Phis)
            {
                foreach (var incoming in phi.Incomings)
                {
                    CollectReferencedFunctions(incoming.Value, referencedFunctions);
                }
            }

            foreach (var instruction in block.Instructions)
            {
                if (instruction is SsaValueInstruction { Value: SsaCallRValue call })
                {
                    referencedFunctions.Add(call.FunctionName);
                }
                else if (instruction is SsaCallInstruction statementCall)
                {
                    referencedFunctions.Add(statementCall.FunctionName);
                }

                foreach (var value in EnumerateInstructionOperands(instruction))
                {
                    CollectReferencedFunctions(value, referencedFunctions);
                }
            }

            foreach (var value in EnumerateTerminatorOperands(block.Terminator))
            {
                CollectReferencedFunctions(value, referencedFunctions);
            }
        }
    }

    private static void CollectReferencedFunctions(SsaValue value, ISet<string> referencedFunctions)
    {
        switch (value)
        {
            case SsaFunctionAddressValue functionAddress:
                referencedFunctions.Add(functionAddress.FunctionName);
                break;
            case SsaClosureValue closure:
                referencedFunctions.Add(closure.InvokeFunctionName);
                break;
        }
    }

    private static void CollectReferencedGlobalUses(
        SsaFunction function,
        IDictionary<string, ReferencedGlobalUse> referencedGlobals)
    {
        foreach (var block in function.Blocks)
        {
            foreach (var phi in block.Phis)
            {
                foreach (var incoming in phi.Incomings)
                {
                    CollectReferencedGlobalUses(incoming.Value, referencedGlobals);
                }
            }

            foreach (var instruction in block.Instructions)
            {
                CollectReferencedGlobalUses(instruction, referencedGlobals);
            }

            foreach (var value in EnumerateTerminatorOperands(block.Terminator))
            {
                CollectReferencedGlobalUses(value, referencedGlobals);
            }
        }
    }

    private static void CollectReferencedGlobalUses(
        SsaInstruction instruction,
        IDictionary<string, ReferencedGlobalUse> referencedGlobals)
    {
        switch (instruction)
        {
            case SsaValueInstruction valueInstruction:
                CollectReferencedGlobalUses(valueInstruction.Value, referencedGlobals);
                break;
            case SsaStoreGlobalInstruction storeGlobal:
                AddReferencedGlobalUse(storeGlobal.GlobalName, storeGlobal.GlobalType, referencedGlobals);
                CollectReferencedGlobalUses(storeGlobal.Value, referencedGlobals);
                break;
            default:
                foreach (var value in EnumerateInstructionOperands(instruction))
                {
                    CollectReferencedGlobalUses(value, referencedGlobals);
                }

                break;
        }
    }

    private static void CollectReferencedGlobalUses(
        SsaRValue value,
        IDictionary<string, ReferencedGlobalUse> referencedGlobals)
    {
        if (value is SsaLoadGlobalRValue loadGlobal)
        {
            AddReferencedGlobalUse(loadGlobal.GlobalName, loadGlobal.Type, referencedGlobals);
        }

        foreach (var operand in EnumerateRValueOperands(value))
        {
            CollectReferencedGlobalUses(operand, referencedGlobals);
        }
    }

    private static void CollectReferencedGlobalUses(
        SsaValue value,
        IDictionary<string, ReferencedGlobalUse> referencedGlobals)
    {
        if (value is SsaGlobalAddressValue globalAddress)
        {
            AddReferencedGlobalUse(globalAddress.GlobalName, globalAddress.PointeeType, referencedGlobals);
        }
    }

    private static void AddReferencedGlobalUse(
        string globalName,
        StarkTypeSymbol type,
        IDictionary<string, ReferencedGlobalUse> referencedGlobals)
    {
        referencedGlobals.TryAdd(globalName, new ReferencedGlobalUse(type));
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
            outcome: CompilerLogOutcome.Unsupported,
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
        TypeCheckModel typeModel,
        SsaIrModule ssa,
        SpecializationCodegenStrategyModel? specializationCodegenStrategy)
    {
        var functions = effectModel.Functions.ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value,
            StringComparer.Ordinal);

        foreach (var adapter in typeModel.ClosureFunctionPromotions)
        {
            functions.TryAdd(
                adapter.AdapterFunctionName,
                CallableValueFacts.BuildClosureFunctionAdapterEffectProfile(adapter));
        }

        foreach (var function in ssa.Functions)
        {
            if (string.Equals(function.Name, CallableValueFacts.EmptyClosureDropFunctionName, StringComparison.Ordinal)
                || function.Name.EndsWith(".__drop", StringComparison.Ordinal)
                || DynTraitFacts.IsVtableReferencedRoot(function.Name))
            {
                functions.TryAdd(function.Name, CallableValueFacts.BuildClosureDropEffectProfile(function.Name));
            }
        }

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

            if (_closedWorldImportedInlineBodies.TryGetValue(functionName, out var inlineBody))
            {
                return inlineBody.AbiSignature;
            }

            return _allAbiFunctions.TryGetValue(functionName, out var abiFunction)
                ? abiFunction
                : null;
        };
    }

    private AbiFunctionSignature? ResolveFunctionAbi(TypedFunctionSignature signature)
    {
        if (_allAbiFunctions.TryGetValue(signature.Name, out var abiFunction))
        {
            return abiFunction;
        }

        if (!signature.IsGenericInstantiation
            || signature.TemplateName is not { } templateName
            || ((signature.TypeArguments is null || signature.TypeArguments.Count == 0)
                && (signature.ComptimeValueArguments is null || signature.ComptimeValueArguments.Count == 0)))
        {
            return null;
        }

        var instantiationKey = FunctionOverloadFacts.BuildInstantiationArgumentKey(
            signature.TypeArguments,
            signature.ComptimeValueArguments);
        foreach (var candidate in _allFunctionSignatures.Values)
        {
            if (!candidate.IsGenericInstantiation
                || candidate.TemplateName is null
                || !string.Equals(candidate.TemplateName, templateName, StringComparison.Ordinal)
                || !string.Equals(
                    FunctionOverloadFacts.BuildInstantiationArgumentKey(
                        candidate.TypeArguments,
                        candidate.ComptimeValueArguments),
                    instantiationKey,
                    StringComparison.Ordinal))
            {
                continue;
            }

            if (_allAbiFunctions.TryGetValue(candidate.Name, out abiFunction))
            {
                return abiFunction;
            }
        }

        return null;
    }

    private static IReadOnlyDictionary<string, TypedFunctionSignature> BuildAllFunctionSignatures(
        TypeCheckModel typeModel,
        SsaIrModule ssa,
        SpecializationCodegenStrategyModel? specializationCodegenStrategy)
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
                    SourceName: function.Name,
                    DisjointParameterGroups: function.DisjointGroups,
                    SameParameterGroups: function.SameGroups));
        }

        foreach (var strategy in specializationCodegenStrategy?.Functions ?? [])
        {
            if (!typeModel.Functions.TryGetValue(strategy.TemplateName, out var templateSignature))
            {
                continue;
            }

            functions[strategy.SymbolName] = FunctionOverloadFacts.InstantiateSignature(
                templateSignature,
                strategy.TypeArguments,
                strategy.SymbolName,
                (ownerType, associatedTypeName) => ResolveAssociatedTypeForEmission(ownerType, associatedTypeName, typeModel.NamedTypes),
                strategy.ComptimeValueArguments);
        }

        return functions;
    }

    private static StarkTypeSymbol? ResolveAssociatedTypeForEmission(
        StarkTypeSymbol ownerType,
        string associatedTypeName,
        IReadOnlyDictionary<string, NamedTypeSymbol> namedTypes)
    {
        return AssociatedTypeFacts.TryResolveAssociatedType(
            ownerType,
            associatedTypeName,
            namedTypes,
            out var targetType)
                ? targetType
                : null;
    }

    private static IReadOnlyDictionary<string, AbiFunctionSignature> BuildAllAbiFunctions(
        IReadOnlyDictionary<string, TypedFunctionSignature> allFunctionSignatures,
        AbiModel abiModel,
        IReadOnlyDictionary<string, FunctionEffectProfile> allFunctionEffects,
        IReadOnlyDictionary<string, NamedTypeSymbol> namedTypes,
        IReadOnlyDictionary<string, EnumLayoutSymbol> enumLayouts,
        LlvmTargetInfo? targetInfo,
        IReadOnlyDictionary<string, ConcreteTypeLayout> publishedConcreteLayouts)
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

            allFunctionEffects.TryGetValue(function.Name, out var effects);
            var isFfi = effects?.IsFfi == true;
            var isVarargs = effects?.IsVarargs == true || function.IsVarargs;
            functions[function.Name] = BuildSyntheticAbiSignature(
                function,
                function.Name,
                isFfi,
                namedTypes,
                enumLayouts,
                isVarargs,
                effects?.FfiAbi ?? function.FfiAbi,
                targetInfo,
                publishedConcreteLayouts);
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
            _loadedModules,
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
            EscapeIdentifier,
            _emitFallbackDeclarationsForSourceBodies,
            _emissionContext.TargetSupportsComdat);
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

    private IReadOnlyDictionary<string, ImportedInlineBodyPlan> BuildClosedWorldImportedInlineBodies()
    {
        return LlvmSpecializationEmissionPlanner.BuildClosedWorldImportedInlineBodies(
            _loadedModules,
            _ssa,
            _syntaxModel,
            _typeModel,
            _enumLayoutModel,
            _allFunctionEffects,
            _allFunctionSignatures,
            _importedInlineCloneSeedFunctions,
            _specializationTemplateNames);
    }

    private IReadOnlySet<string>? BuildOwnedFunctionDefinitionFilter()
    {
        if (_importedInlineCloneSeedFunctions is null)
        {
            return null;
        }

        var localFunctionNames = _ssa.Functions
            .Where(static function => function.HasBody)
            .Select(static function => function.Name)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var declaration in _syntaxModel.Declarations.Where(static declaration => declaration.Function is not null))
        {
            localFunctionNames.Add(FunctionOverloadFacts.GetResolvedLocalName(_syntaxModel, declaration));
        }

        var callsByFunction = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var function in _ssa.Functions)
        {
            var callees = new HashSet<string>(StringComparer.Ordinal);
            foreach (var block in function.Blocks)
            {
                foreach (var phi in block.Phis)
                {
                    foreach (var incoming in phi.Incomings)
                    {
                        AddLocalFunctionReference(callees, localFunctionNames, incoming.Value);
                    }
                }

                foreach (var instruction in block.Instructions)
                {
                    if (instruction is SsaValueInstruction { Value: SsaCallRValue call })
                    {
                        AddLocalCallee(callees, localFunctionNames, NormalizeOwnedFunctionName(call.FunctionName));
                    }
                    else if (instruction is SsaCallInstruction statementCall)
                    {
                        AddLocalCallee(callees, localFunctionNames, NormalizeOwnedFunctionName(statementCall.FunctionName));
                    }

                    foreach (var value in EnumerateInstructionOperands(instruction))
                    {
                        AddLocalFunctionReference(callees, localFunctionNames, value);
                    }
                }

                foreach (var value in EnumerateTerminatorOperands(block.Terminator))
                {
                    AddLocalFunctionReference(callees, localFunctionNames, value);
                }
            }

            callsByFunction[function.Name] = callees;
        }

        var reachable = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Queue<string>();
        foreach (var seed in _importedInlineCloneSeedFunctions)
        {
            var localSeed = NormalizeOwnedFunctionName(seed);
            if (localFunctionNames.Contains(localSeed))
            {
                pending.Enqueue(localSeed);
            }
        }

        while (pending.Count != 0)
        {
            var functionName = pending.Dequeue();
            if (!reachable.Add(functionName)
                || !callsByFunction.TryGetValue(functionName, out var callees))
            {
                continue;
            }

            foreach (var callee in callees)
            {
                pending.Enqueue(callee);
            }
        }

        return reachable;
    }

    private string NormalizeOwnedFunctionName(string functionName)
    {
        var modulePrefix = $"{_syntaxModel.ModuleName}.";
        return functionName.StartsWith(modulePrefix, StringComparison.Ordinal)
            ? functionName[modulePrefix.Length..]
            : functionName;
    }

    private static void AddLocalCallee(
        ISet<string> callees,
        IReadOnlySet<string> localFunctionNames,
        string functionName)
    {
        if (localFunctionNames.Contains(functionName))
        {
            callees.Add(functionName);
        }
    }

    private void AddLocalFunctionReference(
        ISet<string> callees,
        IReadOnlySet<string> localFunctionNames,
        SsaValue value)
    {
        switch (value)
        {
            case SsaFunctionAddressValue functionAddress:
                AddLocalCallee(callees, localFunctionNames, NormalizeOwnedFunctionName(functionAddress.FunctionName));
                break;
            case SsaClosureValue closure:
                AddLocalCallee(callees, localFunctionNames, NormalizeOwnedFunctionName(closure.InvokeFunctionName));
                break;
        }
    }

    private IReadOnlyDictionary<string, ImportedGlobalDeclarationPlan> BuildImportedCloneReferencedGlobalDeclarations()
    {
        var referencedGlobals = new Dictionary<string, ReferencedGlobalUse>(StringComparer.Ordinal);
        foreach (var function in _ssa.Functions)
        {
            CollectReferencedGlobalUses(function, referencedGlobals);
        }

        foreach (var clone in _closedWorldImportedLawClones.Values)
        {
            CollectReferencedGlobalUses(clone.SsaFunction, referencedGlobals);
        }

        foreach (var clone in _closedWorldImportedInlineBodies.Values)
        {
            CollectReferencedGlobalUses(clone.SsaFunction, referencedGlobals);
        }

        if (referencedGlobals.Count == 0)
        {
            return new Dictionary<string, ImportedGlobalDeclarationPlan>(StringComparer.Ordinal);
        }

        var importedSourceGlobals = BuildImportedGlobalSourceLookup();
        var globals = new Dictionary<string, ImportedGlobalDeclarationPlan>(StringComparer.Ordinal);

        foreach (var (qualifiedName, reference) in referencedGlobals.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            if (_typeModel.Globals.ContainsKey(qualifiedName)
                || !importedSourceGlobals.TryGetValue(qualifiedName, out var source))
            {
                continue;
            }

            var global = new TypedGlobalSymbol(
                qualifiedName,
                reference.Type,
                source.BindingKind);
            globals[qualifiedName] = new ImportedGlobalDeclarationPlan(
                qualifiedName,
                source.ModuleName,
                source.SourceName,
                source.Visibility,
                global,
                source.Initializer);
        }

        return globals;
    }

    private IReadOnlyDictionary<string, ImportedGlobalSourceInfo> BuildImportedGlobalSourceLookup()
    {
        var globals = new Dictionary<string, ImportedGlobalSourceInfo>(StringComparer.Ordinal);

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
                        globals[qualifiedName] = new ImportedGlobalSourceInfo(
                            module.SyntaxModel.ModuleName,
                            sourceName,
                            visibility,
                            GlobalBindingKind.Const,
                            declarator.variableInitializer());
                    }

                    continue;
                }

                if (declaration.globalVariableDeclaration() is not { } variableDeclaration)
                {
                    continue;
                }

                var bindingKind = variableDeclaration.MUT() is not null
                    ? GlobalBindingKind.Mutable
                    : GlobalBindingKind.Immutable;
                foreach (var declarator in variableDeclaration.variableDeclarators().variableDeclarator())
                {
                    var sourceName = declarator.Identifier().GetText();
                    var qualifiedName = $"{module.SyntaxModel.ModuleName}.{sourceName}";
                    globals[qualifiedName] = new ImportedGlobalSourceInfo(
                        module.SyntaxModel.ModuleName,
                        sourceName,
                        visibility,
                        bindingKind,
                        declarator.variableInitializer());
                }
            }
        }

        return globals;
    }

    private sealed record ReferencedGlobalUse(StarkTypeSymbol Type);

    private sealed record ImportedGlobalSourceInfo(
        string ModuleName,
        string SourceName,
        StarkVisibility Visibility,
        GlobalBindingKind BindingKind,
        StarkParser.VariableInitializerContext? Initializer);

    private void LogSpecializationCodegenStrategies()
    {
        LlvmSpecializationEmissionPlanner.LogSpecializationCodegenStrategies(_logs, _specializationCodegenStrategy);
    }

    private static AbiFunctionSignature BuildSyntheticAbiSignature(
        TypedFunctionSignature function,
        string symbolName,
        bool isFfi,
        IReadOnlyDictionary<string, NamedTypeSymbol> namedTypes,
        IReadOnlyDictionary<string, EnumLayoutSymbol> enumLayouts,
        bool isVarargs = false,
        StarkFfiAbi? ffiAbi = null,
        LlvmTargetInfo? targetInfo = null,
        IReadOnlyDictionary<string, ConcreteTypeLayout>? publishedConcreteLayouts = null)
    {
        return LlvmSpecializationEmissionPlanner.BuildSyntheticAbiSignature(
            function,
            symbolName,
            isFfi,
            namedTypes,
            enumLayouts,
            isVarargs,
            ffiAbi,
            targetInfo,
            publishedConcreteLayouts);
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
        var addressTakenGlobals = CollectExplicitGlobalAddressNames(
            _ssa.Functions
                .Concat(_closedWorldImportedLawClones.Values.Select(static clone => clone.SsaFunction))
                .Concat(_closedWorldImportedInlineBodies.Values.Select(static clone => clone.SsaFunction)));
        var eligible = new HashSet<string>(StringComparer.Ordinal);

        void TryAddEligibleGlobal(
            string globalName,
            TypedGlobalSymbol global,
            StarkParser.VariableInitializerContext? initializer)
        {
            if (global.IsMutable
                || addressTakenGlobals.Contains(globalName)
                || (initializer is not null && ShouldEmitExternalConstPlaceholder(global, initializer)))
            {
                return;
            }

            eligible.Add(globalName);
        }

        foreach (var declaration in _parseResult.Root.topLevelDeclaration())
        {
            if (declaration.globalConstantDeclaration() is { } constantDeclaration)
            {
                foreach (var declarator in constantDeclaration.constantDeclarators().constantDeclarator())
                {
                    var name = declarator.Identifier().GetText();
                    if (!_typeModel.Globals.TryGetValue(name, out var global)
                        || declarator.variableInitializer() is null)
                    {
                        continue;
                    }

                    TryAddEligibleGlobal(name, global, declarator.variableInitializer());
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
                    || declarator.variableInitializer() is null)
                {
                    continue;
                }

                TryAddEligibleGlobal(name, global, declarator.variableInitializer());
            }
        }

        foreach (var module in _loadedModules.ImportedModules)
        {
            foreach (var declaration in module.ParseResult.Root.topLevelDeclaration())
            {
                if (declaration.globalConstantDeclaration() is { } constantDeclaration)
                {
                    foreach (var declarator in constantDeclaration.constantDeclarators().constantDeclarator())
                    {
                        var qualifiedName = $"{module.SyntaxModel.ModuleName}.{declarator.Identifier().GetText()}";
                        if (!TryGetGlobal(qualifiedName, out var global))
                        {
                            continue;
                        }

                        TryAddEligibleGlobal(qualifiedName, global, declarator.variableInitializer());
                    }

                    continue;
                }

                if (declaration.globalVariableDeclaration() is not { } variableDeclaration)
                {
                    continue;
                }

                foreach (var declarator in variableDeclaration.variableDeclarators().variableDeclarator())
                {
                    var qualifiedName = $"{module.SyntaxModel.ModuleName}.{declarator.Identifier().GetText()}";
                    if (!TryGetGlobal(qualifiedName, out var global)
                        || declarator.variableInitializer() is null)
                    {
                        continue;
                    }

                    TryAddEligibleGlobal(qualifiedName, global, declarator.variableInitializer());
                }
            }
        }

        return eligible;
    }

    private static IReadOnlySet<string> CollectExplicitGlobalAddressNames(IEnumerable<SsaFunction> functions)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);

        foreach (var function in functions)
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
            SsaCallInstruction call => call.IndirectArgumentAddresses is { Count: > 0 }
                ? call.Arguments.Concat(call.IndirectArgumentAddresses.OfType<SsaValue>())
                : call.Arguments,
            SsaIndirectCallInstruction call => call.IndirectArgumentAddresses is { Count: > 0 }
                ? call.Arguments.Prepend(call.Target).Concat(call.IndirectArgumentAddresses.OfType<SsaValue>())
                : call.Arguments.Prepend(call.Target),
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
            SsaSelectRValue select => [select.Condition, select.WhenTrue, select.WhenFalse],
            SsaCallRValue call => call.IndirectArgumentAddresses is { Count: > 0 }
                ? call.Arguments.Concat(call.IndirectArgumentAddresses.OfType<SsaValue>())
                : call.Arguments,
            SsaIndirectCallRValue indirectCall => indirectCall.IndirectArgumentAddresses is { Count: > 0 }
                ? indirectCall.Arguments.Prepend(indirectCall.Target).Concat(indirectCall.IndirectArgumentAddresses.OfType<SsaValue>())
                : indirectCall.Arguments.Prepend(indirectCall.Target),
            SsaConvertRValue convert => [convert.Operand],
            SsaExtractFieldRValue extractField => [extractField.Target],
            SsaInsertFieldRValue insertField => [insertField.Target, insertField.Value],
            SsaExtractIndexRValue extractIndex => [extractIndex.Target],
            SsaInsertIndexRValue insertIndex => [insertIndex.Target, insertIndex.Value],
            SsaDynVTableSlotRValue vtableSlot => [vtableSlot.VtablePointer],
            SsaMakeSliceFromPointerRValue slice => [slice.Pointer, slice.Length],
            SsaDynamicStorageAllocationRValue allocation => [allocation.Capacity],
            SsaDynamicStorageFreeRValue free => [free.Storage],
            SsaHeapStorageFreeRValue free => [free.Pointer],
            SsaDynamicStorageReserveRValue reserve => [reserve.StorageAddress, reserve.AdditionalCapacity],
            SsaDynamicStorageTryReserveRValue reserve => [reserve.StorageAddress, reserve.AdditionalCapacity],
            SsaDynamicStorageTryReserveCapacityRValue reserve => [reserve.StorageAddress, reserve.TargetCapacity],
            SsaDynamicStorageMoveLastRValue moveLast => [moveLast.StorageAddress],
            SsaDynamicStorageMoveAtRValue moveAt => [moveAt.StorageAddress, moveAt.Index],
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
                        if (!TryGetGlobal(qualifiedName, out var global))
                        {
                            continue;
                        }

                        var isImportedCloneConst = _importedCloneReferencedGlobals.ContainsKey(qualifiedName)
                            && !_typeModel.Globals.ContainsKey(qualifiedName);
                        symbols[qualifiedName] = !isImportedCloneConst && ShouldEmitExternalConstPlaceholder(global, declarator.variableInitializer())
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

        foreach (var global in _importedCloneReferencedGlobals.Values)
        {
            symbols.TryAdd(
                global.QualifiedName,
                GlobalSymbolNaming.ComputeSymbolName(
                    global.ModuleName,
                    global.SourceName,
                    global.Visibility,
                    qualifyModuleSymbols: false,
                    isImported: true));
        }

        return symbols;
    }

    private static bool ShouldEmitExternalConstPlaceholder(
        TypedGlobalSymbol global,
        StarkParser.VariableInitializerContext? initializer)
    {
        return global.IsConst
            && global.Type.Kind == StarkTypeKind.RawPointer
            && initializer?.expression() is { } expression
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
        if (!TryGetGlobal(qualifiedName, out _))
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

    private bool TryGetGlobal(string globalName, out TypedGlobalSymbol global)
    {
        if (_typeModel.Globals.TryGetValue(globalName, out global!))
        {
            return true;
        }

        if (_importedCloneReferencedGlobals.TryGetValue(globalName, out var importedGlobal))
        {
            global = importedGlobal.Global;
            return true;
        }

        return false;
    }

    private string ResolveGlobalSymbolName(string globalName)
    {
        return _globalSymbols.TryGetValue(globalName, out var symbolName)
            ? symbolName
            : globalName;
    }

    private void EmitIntrinsicDeclarations(StringBuilder builder)
    {
        _builtinAndHelperEmitter.EmitIntrinsicDeclarations(builder, EnumerateBuiltinDefinitionSignatures());
    }

    private void EmitInternalHelperDefinitions(StringBuilder builder)
    {
        _builtinAndHelperEmitter.EmitInternalHelperDefinitions(builder, EnumerateBuiltinDefinitionSignatures());
    }

    private IEnumerable<TypedFunctionSignature> EnumerateBuiltinDefinitionSignatures()
    {
        foreach (var declaration in _syntaxModel.Declarations.Where(static declaration => declaration.Function is not null))
        {
            var function = declaration.Function!;
            if (function.HasBody)
            {
                continue;
            }

            var resolvedName = FunctionOverloadFacts.GetResolvedLocalName(_syntaxModel, declaration);
            if (_typeModel.Functions.TryGetValue(resolvedName, out var signature))
            {
                yield return signature;
            }
        }

        foreach (var functionName in _referencedImportedFunctions)
        {
            if (_allFunctionSignatures.TryGetValue(functionName, out var signature))
            {
                yield return signature;
            }
        }
    }

    private bool UsesLifetimeMarkers()
    {
        return _ssa.Functions
            .SelectMany(static function => function.Blocks)
            .SelectMany(static block => block.Instructions)
            .Any(static instruction => instruction is SsaLifetimeStartInstruction or SsaLifetimeEndInstruction);
    }

    private bool UsesInvariantStartIntrinsic()
    {
        return _ssa.Functions
            .SelectMany(static function => function.Blocks)
            .SelectMany(static block => block.Instructions)
            .Any(static instruction => instruction is SsaAllocateLocalInstruction { IsImmutable: true });
    }

    private bool UsesMemcpyInlineIntrinsic()
    {
        return _ssa.Functions
            .SelectMany(static function => function.Blocks)
            .SelectMany(static block => block.Instructions)
            .OfType<SsaCopyMemoryInstruction>()
            .Any(copy => TryGetConcreteTypeLayout(copy.CopyType) is { } layout
                && layout.SizeBytes > AggregateMemcpyThresholdBytes
                && layout.SizeBytes <= AggregateInlineMemcpyThresholdBytes)
            || UsesBuiltinMemcpyInlineIntrinsic();
    }

    private bool UsesBuiltinMemcpyInlineIntrinsic()
    {
        return false;
    }

    private bool UsesMemcpyIntrinsic()
    {
        return _enableOptimizedRawPointerLoopIntrinsics
            && _ssa.Functions.Any(function => LlvmFunctionBodyEmitter.MayEmitOptimizedRawPointerMemcpyIntrinsic(
                function,
                TryGetConcreteTypeLayout,
                GetParameterEffects(function.Name, hasBody: true)))
            || UsesLargeAggregateMemcpyIntrinsic();
    }

    private bool UsesLargeAggregateMemcpyIntrinsic()
    {
        return _ssa.Functions
            .SelectMany(static function => function.Blocks)
            .SelectMany(static block => block.Instructions)
            .Any(UsesLargeAggregateMemcpyIntrinsic);
    }

    private bool UsesLargeAggregateMemcpyIntrinsic(SsaInstruction instruction)
    {
        return instruction switch
        {
            SsaCopyMemoryInstruction { CopyType: var type } => IsLargeAggregateMemcpyType(type),
            SsaStoreLocalInstruction { LocalType: var type, Value: not SsaZeroInitializerValue } => IsLargeAggregateMemcpyType(type),
            SsaStoreIndirectInstruction { ValueType: var type, Value: not SsaZeroInitializerValue } => IsLargeAggregateMemcpyType(type),
            _ => false
        };
    }

    private bool IsLargeAggregateMemcpyType(StarkTypeSymbol type)
    {
        var normalizedType = NormalizeAggregateType(type);
        return TryGetConcreteTypeLayout(normalizedType) is { } layout
            && layout.SizeBytes > AggregateInlineMemcpyThresholdBytes
            && normalizedType.Kind is StarkTypeKind.FixedArray or StarkTypeKind.Named;
    }

    private bool UsesMemmoveIntrinsic()
    {
        return _enableOptimizedRawPointerLoopIntrinsics
            && _ssa.Functions.Any(function => LlvmFunctionBodyEmitter.MayEmitOptimizedRawPointerMemmoveIntrinsic(
                function,
                TryGetConcreteTypeLayout));
    }

    private bool UsesMemsetIntrinsic()
    {
        return _enableOptimizedRawPointerLoopIntrinsics
            && _ssa.Functions.Any(function => LlvmFunctionBodyEmitter.MayEmitOptimizedRawPointerMemsetIntrinsic(
                function,
                TryGetConcreteTypeLayout,
                GetParameterEffects(function.Name, hasBody: true)));
    }

    private bool UsesMemsetInlineIntrinsic()
    {
        return _ssa.Functions
            .SelectMany(static function => function.Blocks)
            .SelectMany(static block => block.Instructions)
            .Any(UsesLargeZeroInitializedAggregateStore)
            || _allAbiFunctions.Values.Any(static function => function.UserParameters.Any(static parameter => parameter.IsExpandedDirectParameter));
    }

    private bool UsesHeapAllocator()
    {
        return _ssa.Functions
            .SelectMany(static function => function.Blocks)
            .SelectMany(static block => block.Instructions)
            .Any(static instruction => instruction is SsaAllocateLocalInstruction { StorageClass: "heap" }
                or SsaDeallocateLocalInstruction { StorageClass: "heap" }
                || instruction is SsaValueInstruction { Value: SsaHeapStorageFreeRValue });
    }

    private bool UsesUnreachableTrapHelper()
    {
        return _ssa.Functions
            .SelectMany(static function => function.Blocks)
            .Any(static block => block.Terminator.Kind == SsaTerminatorKind.Unreachable);
    }

    private bool UsesAssumeIntrinsic()
    {
        return _ssa.Functions.Any(function =>
            LlvmFunctionBodyEmitter.MayEmitAssumeIntrinsic(
                function,
                _ssaValueFacts?.Functions.GetValueOrDefault(function.Name)));
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
        var normalizedType = NormalizeAggregateType(valueType);

        if (TryGetConcreteTypeLayout(normalizedType) is not { } layout
            || layout.SizeBytes <= AggregateScalarizationThresholdBytes)
        {
            return false;
        }

        return normalizedType.Kind is StarkTypeKind.FixedArray or StarkTypeKind.Named;
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

    private void EmitFunctionDefinition(
        StringBuilder builder,
        bool internalize,
        bool availableExternally,
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
        var effectiveEffects = AdjustDefinitionEffectsForBody(effects, ssaFunction);
        var effectiveMemoryEffects = AdjustDefinitionMemoryEffectsForBodyAndAbiLowering(memoryEffects, ssaFunction);
        var debugFunction = TryCreateDebugFunctionContext(function, abiFunction, ssaFunction);
        var valueFacts = TryGetSsaValueFacts(ssaFunction);
        functionBuilder.AppendLine(AppendFunctionDebugScope(
            BuildDefinitionSignatureCore(
                internalize,
                availableExternally,
                function,
                abiFunction,
                effectiveEffects,
                effectiveMemoryEffects,
                parameterEffects,
                specializationLinkage,
                TryGetReturnIntegerRange(abiFunction, ssaFunction, valueFacts)),
            debugFunction));
        functionBuilder.AppendLine("{");

        var bodyEmitter = new LlvmFunctionBodyEmitter(
            functionBuilder,
            function,
            abiFunction,
            resolveCallAbi,
            ssaFunction,
            _emissionContext,
            debugFunction,
            valueFacts,
            parameterEffects,
            effects.IsStrictFp,
            GetParameterEffects,
            GetFunctionMemoryEffects,
            _enableOptimizedRawPointerLoopIntrinsics);
        bodyEmitter.Emit();
        functionBuilder.AppendLine("}");
        builder.Append(functionBuilder);
    }

    private SsaFunctionFactModel? TryGetSsaValueFacts(SsaFunction ssaFunction)
    {
        return _ssaValueFacts is not null
               && _ssaValueFacts.Functions.TryGetValue(ssaFunction.Name, out var facts)
            ? facts
            : null;
    }

    private static SsaIntegerRangeFact? TryGetReturnIntegerRange(
        AbiFunctionSignature abiFunction,
        SsaFunction ssaFunction,
        SsaFunctionFactModel? facts)
    {
        if (facts is null
            || abiFunction.IsFfi
            || abiFunction.ReturnsIndirect
            || abiFunction.SourceReturnType.Kind != StarkTypeKind.Integer)
        {
            return null;
        }

        var ranges = new List<SsaIntegerRangeFact>();
        var sawNonConstantReturn = false;
        foreach (var returnValue in ssaFunction.Blocks
                     .Where(static block => block.Terminator.Kind == SsaTerminatorKind.Return)
                     .Select(static block => block.Terminator.Value))
        {
            if (returnValue is null)
            {
                continue;
            }

            if (!TryGetIntegerRange(returnValue, facts, out var range))
            {
                return null;
            }

            sawNonConstantReturn |= returnValue is not SsaIntegerConstant;
            ranges.Add(range);
        }

        return ranges.Count == 0 || !sawNonConstantReturn
            ? null
            : new SsaIntegerRangeFact(
                ranges.Min(static range => range.Min),
                ranges.Max(static range => range.Max));
    }

    private static bool TryGetIntegerRange(
        SsaValue value,
        SsaFunctionFactModel facts,
        out SsaIntegerRangeFact range)
    {
        if (value is SsaIntegerConstant integer)
        {
            range = new SsaIntegerRangeFact(integer.Value, integer.Value);
            return true;
        }

        if (value is SsaValueReference reference
            && facts.Values.TryGetValue(reference.Name, out var valueFacts)
            && valueFacts.IntegerRangeKind == SsaFactLatticeKind.Known
            && valueFacts.IntegerRange is { } knownRange)
        {
            range = knownRange;
            return true;
        }

        range = default!;
        return false;
    }

    private bool IsImmutableGlobalName(string globalName)
    {
        return TryGetGlobal(globalName, out var global)
            && !global.IsMutable;
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

    private static FunctionEffectProfile AdjustDefinitionEffectsForBody(
        FunctionEffectProfile effects,
        SsaFunction ssaFunction)
    {
        return ContainsAllocatorAccess(ssaFunction)
            ? effects with
            {
                Kind = StarkFunctionKind.Fn,
                IsPure = false,
                NoSync = false,
                NoFree = false
            }
            : effects;
    }

    private FunctionMemoryEffectSummary? AdjustDefinitionMemoryEffectsForBodyAndAbiLowering(
        FunctionMemoryEffectSummary? memoryEffects,
        SsaFunction ssaFunction)
    {
        // LLVM memory effects are about externally observable memory. Private
        // alloca scratch introduced for sret/byval lowering can stay under the
        // source memory contract; heap/dynamic runtime calls cannot.
        if (!ContainsAllocatorAccess(ssaFunction))
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

    private static bool ContainsAllocatorAccess(SsaFunction ssaFunction)
    {
        return ssaFunction.Blocks
            .SelectMany(static block => block.Instructions)
            .Any(static instruction => instruction switch
            {
                SsaAllocateLocalInstruction { StorageClass: "heap" }
                    or SsaDeallocateLocalInstruction { StorageClass: "heap" } => true,
                SsaValueInstruction { Value: SsaDynamicStorageAllocationRValue
                    or SsaDynamicStorageFreeRValue
                    or SsaHeapStorageFreeRValue
                    or SsaIndirectCallRValue { MayFree: true }
                    or SsaDynamicStorageReserveRValue
                    or SsaDynamicStorageTryReserveRValue
                    or SsaDynamicStorageTryReserveCapacityRValue
                    or SsaDynamicStorageMoveLastRValue
                    or SsaDynamicStorageMoveAtRValue } => true,
                SsaValueInstruction { Value: SsaCallRValue { FunctionName: var functionName } }
                    => IsClosureDropFunctionName(functionName),
                SsaCallInstruction { FunctionName: var functionName }
                    => IsClosureDropFunctionName(functionName),
                SsaIndirectCallInstruction { MayFree: true } => true,
                _ => false
            });
    }

    private static bool IsClosureDropFunctionName(string functionName)
    {
        return string.Equals(functionName, CallableValueFacts.EmptyClosureDropFunctionName, StringComparison.Ordinal)
            || functionName.EndsWith(".__drop", StringComparison.Ordinal);
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
        functionBuilder.AppendLine(BuildDefinitionSignatureCore(internalize, availableExternally: false, function, abiFunction, effects, memoryEffects, parameterEffects));
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
        return BuildDefinitionSignatureCore(
            internalize,
            availableExternally: false,
            function,
            abiFunction,
            effects,
            memoryEffects,
            parameterEffects,
            specializationLinkage,
            returnRange: null);
    }

    private string BuildDefinitionSignatureCore(
        bool internalize,
        bool availableExternally,
        TypedFunctionSignature function,
        AbiFunctionSignature abiFunction,
        FunctionEffectProfile effects,
        FunctionMemoryEffectSummary? memoryEffects,
        IReadOnlyDictionary<string, ParameterMemoryEffectSummary>? parameterEffects,
        MonomorphizationLinkageKind? specializationLinkage = null,
        SsaIntegerRangeFact? returnRange = null)
    {
        var signature = _functionSignatureBuilder.BuildDefinitionSignature(
            internalize,
            function,
            abiFunction,
            effects,
            memoryEffects,
            parameterEffects,
            specializationLinkage,
            returnRange);

        return availableExternally
            ? PrefixAvailableExternally(signature)
            : signature;
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
        Dictionary<string, ParameterMemoryEffectSummary>? parameterEffects = null;
        if (hasBody
            && HasSsaBody(functionName)
            && TryGetRootValidationSummary(functionName, out var validation)
            && validation.Parameters is not null)
        {
            parameterEffects = validation.Parameters.ToDictionary(static parameter => parameter.Name, StringComparer.Ordinal);
        }
        else if (_publishedFunctionSemantics.TryGetValue(functionName, out var imported)
                 && imported.Parameters is not null)
        {
            parameterEffects = imported.Parameters.ToDictionary(static parameter => parameter.Name, StringComparer.Ordinal);
        }

        if (hasBody
            && TryBuildClosureEnvironmentParameterEffects(
                functionName,
                parameterEffects is not null
                    && parameterEffects.TryGetValue(CallableValueFacts.ClosureEnvironmentParameterName, out var existing)
                        ? existing
                        : null,
                out var closureEnvironmentEffects))
        {
            parameterEffects ??= new Dictionary<string, ParameterMemoryEffectSummary>(StringComparer.Ordinal);
            parameterEffects[CallableValueFacts.ClosureEnvironmentParameterName] = closureEnvironmentEffects;
        }

        return parameterEffects;
    }

    private ClosureLambdaTypingRecord? GetCapturingClosureLambda(string functionName)
    {
        // O(1) lookup over a lazily built index, replacing a per-call linear
        // FirstOrDefault scan of _typeModel.ClosureLambdas. The index keeps the first
        // capturing lambda (with a non-empty environment type) per function name, matching
        // the prior FirstOrDefault first-match semantics.
        _capturingClosureLambdasByName ??= BuildCapturingClosureLambdaIndex();
        return _capturingClosureLambdasByName.GetValueOrDefault(functionName);
    }

    private Dictionary<string, ClosureLambdaTypingRecord> BuildCapturingClosureLambdaIndex()
    {
        var index = new Dictionary<string, ClosureLambdaTypingRecord>(StringComparer.Ordinal);
        foreach (var candidate in _typeModel.ClosureLambdas)
        {
            if (candidate.HasCaptures
                && !string.IsNullOrEmpty(candidate.FunctionName)
                && !string.IsNullOrWhiteSpace(candidate.EnvironmentTypeName))
            {
                index.TryAdd(candidate.FunctionName, candidate);
            }
        }

        return index;
    }

    private bool TryBuildClosureEnvironmentParameterEffects(
        string functionName,
        ParameterMemoryEffectSummary? existing,
        out ParameterMemoryEffectSummary effects)
    {
        effects = default!;
        var lambda = GetCapturingClosureLambda(functionName);
        if (lambda is null)
        {
            return false;
        }

        var environmentType = new StarkTypeSymbol(
            StarkTypeKind.Named,
            lambda.EnvironmentTypeName!,
            NamedType: lambda.EnvironmentTypeName);
        if (ConcreteTypeLayoutHelper.TryGetConcreteTypeLayout(
                environmentType,
                _typeModel.NamedTypes,
                _enumLayoutModel.Layouts,
                _publishedConcreteLayouts) is not { } layout
            || layout.SizeBytes <= 0)
        {
            return false;
        }

        effects = new ParameterMemoryEffectSummary(
            CallableValueFacts.ClosureEnvironmentParameterName,
            lambda.EnvironmentParameterType.DisplayName,
            IsMemoryBacked: true,
            GuaranteedNonNull: true,
            GuaranteedReadOnly: existing?.GuaranteedReadOnly ?? !lambda.EnvironmentParameterType.IsMutablePointer,
            GuaranteedWriteOnly: existing?.GuaranteedWriteOnly ?? false,
            GuaranteedNoAlias: true,
            DereferenceableBytes: layout.SizeBytes,
            AlignmentBytes: layout.AlignmentBytes,
            Reads: existing?.Reads ?? true,
            Writes: existing?.Writes ?? false,
            CaptureKind: ParameterCaptureKind.None);
        return true;
    }

    private FunctionMemoryEffectSummary? GetFunctionMemoryEffects(string functionName, bool hasBody)
    {
        if (hasBody
            && HasSsaBody(functionName)
            && TryGetRootValidationSummary(functionName, out var validation))
        {
            return validation.MemoryEffects;
        }

        return _publishedFunctionSemantics.TryGetValue(functionName, out var imported)
            ? imported.MemoryEffects
            : null;
    }

    private bool HasSsaBody(string functionName)
    {
        // O(1) lookup over a precomputed set of names whose SSA function has a body,
        // replacing a per-call linear scan of _ssa.Functions (which made callers such as
        // GetParameterEffects O(N) and, inside per-function .Any() scans, O(N^2)).
        return _ssaFunctionBodyNames.Contains(functionName);
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
        if (StarkTypeSymbols.IsPointerBackedBorrowType(type))
        {
            return "ptr";
        }

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
            StarkTypeKind.CVaList => "ptr",
            StarkTypeKind.RawPointer => "ptr",
            StarkTypeKind.FunctionPointer => "ptr",
            StarkTypeKind.LlvmVector when type.ElementType is not null && type.FixedLength is int vectorLength => $"<{vectorLength} x {MapType(type.ElementType)}>",
            StarkTypeKind.LlvmStruct when type.TypeArguments is { Count: > 0 } fields => $"{{ {string.Join(", ", fields.Select(MapType))} }}",
            StarkTypeKind.Closure => type.ClosureStorageKind == StarkClosureStorageKind.Heap
                ? "{ ptr, ptr, ptr }"
                : "{ ptr, ptr }",
            // A `dyn Trait` value is a two-word fat pointer { data_ptr, vtable_ptr }.
            // `heap dyn` owns the data behind data_ptr but the value layout is identical.
            StarkTypeKind.DynTrait => "{ ptr, ptr }",
            StarkTypeKind.FixedArray when type.ElementType is not null && type.FixedLength is int fixedLength => $"[{fixedLength} x {MapType(type.ElementType)}]",
            StarkTypeKind.Slice => "{ ptr, i64 }",
            StarkTypeKind.Dynamic => "{ ptr, i64, i64 }",
            StarkTypeKind.Ascii => $"%{AsciiStringTypeName}",
            StarkTypeKind.Unicode => $"%{UnicodeStringTypeName}",
            StarkTypeKind.Named when type.NamedType is not null
                                     && _typeModel.NamedTypes.TryGetValue(type.NamedType, out var namedType)
                                     && (namedType.Kind is DeclarationKind.Struct or DeclarationKind.Record
                                         || (namedType.Kind == DeclarationKind.Enum && _enumLayoutModel.Layouts.ContainsKey(namedType.Name)))
                => $"%{EscapeIdentifier(type.NamedType)}",
            StarkTypeKind.Named when ResolveNamedTypeSymbol(type) is { } resolvedNamedType
                                     && TryGetScalarizableNamedAggregateFields(resolvedNamedType, out var orderedFields)
                => $"{{ {string.Join(", ", orderedFields.Select(field => MapType(field.Type)))} }}",
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
                        case SsaCallInstruction call:
                            foreach (var argument in call.Arguments)
                            {
                                AddStringConstant(argument, result, ref index);
                            }

                            foreach (var address in call.IndirectArgumentAddresses?.OfType<SsaValue>() ?? [])
                            {
                                AddStringConstant(address, result, ref index);
                            }

                            AddAsciiToUnicodeLiteralMemcpyConstant(call, result, ref index);
                            break;
                        case SsaIndirectCallInstruction call:
                            AddStringConstant(call.Target, result, ref index);
                            foreach (var argument in call.Arguments)
                            {
                                AddStringConstant(argument, result, ref index);
                            }

                            foreach (var address in call.IndirectArgumentAddresses?.OfType<SsaValue>() ?? [])
                            {
                                AddStringConstant(address, result, ref index);
                            }

                            break;
                        case SsaCopyMemoryInstruction copyMemory:
                            AddStringConstant(copyMemory.DestinationAddress, result, ref index);
                            AddStringConstant(copyMemory.SourceAddress, result, ref index);
                            break;
                        case SsaStoreIndirectInstruction storeIndirect:
                            AddStringConstant(storeIndirect.Address, result, ref index);
                            AddStringConstant(storeIndirect.Value, result, ref index);
                            break;
                        case SsaStoreLocalInstruction storeLocal:
                            AddStringConstant(storeLocal.Value, result, ref index);
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
            case SsaTextDataAddressValue textData:
                AddStringLiteral(textData.LiteralText, textData.TextType, constants, ref index);
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
            case SsaSelectRValue select:
                AddStringConstant(select.Condition, constants, ref index);
                AddStringConstant(select.WhenTrue, constants, ref index);
                AddStringConstant(select.WhenFalse, constants, ref index);
                return;
            case SsaCallRValue call:
                foreach (var argument in call.Arguments)
                {
                    AddStringConstant(argument, constants, ref index);
                }

                foreach (var address in call.IndirectArgumentAddresses ?? [])
                {
                    AddStringConstant(address, constants, ref index);
                }

                AddAsciiToUnicodeLiteralMemcpyConstant(call, constants, ref index);
                return;
            case SsaIndirectCallRValue indirectCall:
                AddStringConstant(indirectCall.Target, constants, ref index);
                foreach (var argument in indirectCall.Arguments)
                {
                    AddStringConstant(argument, constants, ref index);
                }

                foreach (var address in indirectCall.IndirectArgumentAddresses?.OfType<SsaValue>() ?? [])
                {
                    AddStringConstant(address, constants, ref index);
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
                if (parseLiteral.DOLLAR() is not null)
                {
                    return;
                }

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

    private static void AddAsciiToUnicodeLiteralMemcpyConstant(
        ISsaDirectCallOperation call,
        Dictionary<StringConstantKey, EmittedStringConstant> constants,
        ref int index)
    {
        if (!IsPotentialTryConvertAsciiToUnicodeCall(call.FunctionName)
            || call.Arguments is not
            [
                _,
                SsaStringConstant
                {
                    Type.Kind: StarkTypeKind.Ascii
                } source
            ]
            || !TextLiteralDecoder.TryDecode(
                source.LiteralText,
                GetTextLiteralKind(source.LiteralText),
                out var decoded,
                out _)
            || !decoded.IsAscii
            || decoded.Utf8Bytes.Length < LlvmTextOptimizationConstants.AsciiToUnicodeLiteralMemcpyThresholdCodeUnits)
        {
            return;
        }

        AddStringLiteral(source.LiteralText, StarkTypeSymbols.Unicode, constants, ref index);
    }

    private static bool IsPotentialTryConvertAsciiToUnicodeCall(string functionName)
    {
        return string.Equals(functionName, "TryConvertAsciiToUnicode", StringComparison.Ordinal)
               || string.Equals(functionName, "System.Text.TryConvertAsciiToUnicode", StringComparison.Ordinal)
               || functionName.EndsWith(".TryConvertAsciiToUnicode", StringComparison.Ordinal);
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

    private readonly record struct ObjectCreationKey(string? ScopeName, string Text, string? FilePath, int Line, int Column);

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
            AlignmentBytes: GetReadonlyLiteralDataAlignmentBytes(terminated.Length, naturalAlignmentBytes: 1));
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
            AlignmentBytes: GetReadonlyLiteralDataAlignmentBytes(checked(terminated.Length * 4), naturalAlignmentBytes: 4));
    }

    private static int GetReadonlyLiteralDataAlignmentBytes(int sizeBytes, int naturalAlignmentBytes)
    {
        return sizeBytes >= 16
            ? Math.Max(naturalAlignmentBytes, 16)
            : naturalAlignmentBytes;
    }

    private readonly record struct StringConstantKey(
        StarkTypeKind TypeKind,
        string ArrayType,
        string Initializer,
        int DataLength,
        int AlignmentBytes);

    private static string PrefixAvailableExternally(string signature)
    {
        const string definePrefix = "define ";
        if (!signature.StartsWith(definePrefix, StringComparison.Ordinal))
        {
            return $"available_externally {signature}";
        }

        var remainder = signature[definePrefix.Length..];
        if (remainder.StartsWith("internal ", StringComparison.Ordinal))
        {
            remainder = remainder["internal ".Length..];
        }
        else if (remainder.StartsWith("linkonce_odr ", StringComparison.Ordinal))
        {
            remainder = remainder["linkonce_odr ".Length..];
        }

        return $"define available_externally {remainder}";
    }

}
