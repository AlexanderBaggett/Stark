using System.Globalization;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using Antlr4.Runtime;
using Antlr4.Runtime.Tree;
using Stark.Parsing;

namespace Stark.Compiler;

internal sealed class TypeChecker
{
    private static readonly int[] SupportedIntegerLiteralWidths = [8, 16, 24, 32, 48, 64, 96, 128, 192, 256, 384, 512, 768, 1024];
    private static readonly StarkTypeSymbol NonNegativeI64Type = StarkTypeSymbols.Integer(64, BigInteger.Zero, (BigInteger.One << 63) - 1);
    private const string BoolTrueCoverageKey = "bool:true";
    private const string BoolFalseCoverageKey = "bool:false";
    private const string IntegerCoverageKeyPrefix = "int:";

    /// <summary>
    /// Switches proven exhaustive by <see cref="AnalyzeSwitchCoverage"/>: every possible
    /// scrutinee value matches some non-guarded label. Consumed by the definite-return
    /// analysis (<see cref="StatementGuaranteesFunctionExit"/>) so an exhaustive switch
    /// whose sections all return counts as returning on all paths.
    /// </summary>
    private readonly HashSet<StarkParser.SwitchStatementContext> _exhaustiveSwitches = [];

    private enum SwitchCoveragePatternKind
    {
        MatchAll,
        Literal,
        Range,
        Aggregate,
        EnumCase,
        List
    }

    private enum AggregateCoverageFieldKind
    {
        Wildcard,
        Literal,
        Range,
        NestedAggregate,
        NestedEnum,
        NestedList
    }

    private readonly record struct RangeCoverageInterval(BigInteger Min, BigInteger Max);

    private sealed record AggregateCoverageField(
        AggregateCoverageFieldKind Kind,
        string? LiteralKey,
        AggregateCoveragePattern? NestedAggregatePattern,
        EnumCoveragePattern? NestedEnumPattern,
        RangeCoverageInterval? RangeInterval = null,
        ListCoveragePattern? NestedListPattern = null);

    private sealed record AggregateCoveragePattern(
        string TypeName,
        IReadOnlyList<AggregateCoverageField> Fields);

    private sealed record EnumCoveragePattern(
        string EnumName,
        string VariantName,
        IReadOnlyList<AggregateCoverageField> Fields);

    private sealed record ListCoveragePattern(
        StarkTypeSymbol ListType,
        int Length,
        bool CanBeExhaustiveForTarget,
        IReadOnlyList<AggregateCoverageField> Elements);

    private sealed record SwitchCoveragePattern(
        SwitchCoveragePatternKind Kind,
        string LabelText,
        ParserRuleContext Context,
        string? LiteralKey,
        AggregateCoveragePattern? AggregatePattern,
        EnumCoveragePattern? EnumPattern,
        RangeCoverageInterval? RangeInterval = null,
        ListCoveragePattern? ListPattern = null);

    private readonly record struct SwitchSourceShape(
        int SectionCount,
        int LabelCount,
        int ExplicitDefaultLabelCount,
        int LoweredDefaultLabelCount,
        int LiteralLabelCount,
        int MatchAllLabelCount,
        int CaptureLabelCount,
        int StructuredPatternLabelCount,
        int GuardedLabelCount);

    private readonly CompilerPassContext _context;
    private readonly ParseResult _parseResult;
    private readonly SyntaxModel _syntaxModel;
    private readonly ModuleGraph _moduleGraph;
    private readonly LoadedModuleSet _loadedModules;

    private readonly Dictionary<string, NamedTypeSymbol> _namedTypes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TypeAliasSymbol> _typeAliases = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TypeAliasResolutionSource> _typeAliasSources = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<ConstructorShape>> _constructors = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TypedFunctionSignature> _functions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DeclaredFunctionSyntax> _functionSyntaxByQualifiedName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<TypedFunctionSignature>> _functionOverloads = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TypedFunctionSignature> _functionInstantiationCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<TypedFunctionSignature>> _compileTimeMethodSignatureCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, VariableSymbol> _globals = new(StringComparer.Ordinal);
    private readonly List<LiteralTypingRecord> _literals = [];
    private readonly List<EnumConstructorTypingRecord> _enumConstructors = [];
    private readonly List<EnumCallTypingRecord> _enumCalls = [];
    private readonly List<EnumValueTypingRecord> _enumValues = [];
    private readonly List<EnumPatternTypingRecord> _enumPatterns = [];
    private readonly List<AggregatePatternTypingRecord> _aggregatePatterns = [];
    private readonly List<LocalDeclarationTypingRecord> _localDeclarations = [];
    private readonly List<LocalStorageCapacityTypingRecord> _localStorageCapacities = [];
    private readonly List<ConversionTypingRecord> _conversions = [];
    private readonly List<TryPropagationTypingRecord> _tryPropagations = [];
    private readonly List<DirectCallTypingRecord> _directCalls = [];
    private readonly List<FunctionPointerPromotionTypingRecord> _functionPointerPromotions = [];
    private readonly List<ClosureFunctionPromotionTypingRecord> _closureFunctionPromotions = [];
    private readonly List<AddressTakenFunctionTypingRecord> _addressTakenFunctions = [];
    private readonly HashSet<string> _addressTakenFunctionNames = new(StringComparer.Ordinal);
    private readonly List<IndirectCallTypingRecord> _indirectCalls = [];
    private readonly List<ClosureCallTypingRecord> _closureCalls = [];
    private readonly List<LambdaTypingRecord> _lambdas = [];
    private readonly List<ClosureLambdaTypingRecord> _closureLambdas = [];
    private readonly List<LambdaCaptureTypingRecord> _lambdaCaptures = [];
    private readonly List<FunctionGlobalReference> _functionGlobalReferences = [];
    private readonly List<FieldAccessTypingRecord> _fieldAccesses = [];
    private readonly List<MemberCallTypingRecord> _memberCalls = [];
    private readonly List<ObjectCreationTypingRecord> _objectCreations = [];
    private readonly List<TypeLayoutExpressionTypingRecord> _typeLayoutExpressions = [];
    private readonly List<IndexAccessTypingRecord> _indexAccesses = [];
    private readonly List<DynamicStorageOperationTypingRecord> _dynamicStorageOperations = [];
    private readonly List<SwitchTypingRecord> _switches = [];
    private readonly List<BoundOperation> _boundOperations = [];
    private readonly List<FunctionInstantiationTriggerRecord> _functionInstantiationTriggers = [];
    private readonly List<DeferredFunctionInstantiationTriggerRecord> _deferredFunctionInstantiationTriggers = [];
    private readonly List<DeferredTypeInstantiationTriggerRecord> _deferredTypeInstantiationTriggers = [];
    private readonly List<TypeInstantiationTriggerRecord> _typeInstantiationTriggers = [];
    private readonly List<(StarkTypeSymbol Type, SourceLocation? Location)> _pendingDictionaryKeyConstraintValidations = [];
    private readonly HashSet<StarkParser.AdditiveExpressionContext> _fixedTextStorageConcatExpressions = [];
    private readonly HashSet<StarkParser.LiteralContext> _fixedTextStorageInterpolatedLiterals = [];
    private readonly HashSet<string> _functionInstantiationKeys = new(StringComparer.Ordinal);
    private readonly HashSet<string> _deferredFunctionInstantiationKeys = new(StringComparer.Ordinal);
    private readonly HashSet<string> _deferredTypeInstantiationKeys = new(StringComparer.Ordinal);
    private readonly HashSet<string> _typeInstantiationKeys = new(StringComparer.Ordinal);
    private readonly HashSet<string> _dictionaryKeyConstraintFailures = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ConcreteGenericTypeArguments> _genericInstantiationArguments = new(StringComparer.Ordinal);
    private readonly HashSet<string> _refreshingConcreteTypes = new(StringComparer.Ordinal);
    private StarkTypeResolver? _typeResolver;
    private ISet<string>? _currentFunctionGenericParameters;
    private IReadOnlyDictionary<string, ComptimeGenericParameterSymbol>? _currentFunctionComptimeGenericParameters;
    private bool _allowCompileTimeOnlyStructuralFactCalls;
    private IReadOnlyList<TypeParameterConstraint> _currentFunctionConstraints = [];
    private IReadOnlyList<ThreadSafetyLawPredicateSymbol> _currentFunctionThreadSafetyLaws = [];
    private string? _currentFunctionName;
    private string? _currentFunctionModuleName;
    private StarkTypeSymbol? _currentFunctionReturnType;
    private bool _insideConstructorBody;
    private int _unsafeDepth;
    private readonly Dictionary<string, ImportedFunctionTemplateSummary> _importedFunctionTemplates;
    private IReadOnlyList<ImportedTemplateObjectCreationSummary>? _currentImportedTemplateObjectCreations;
    private IReadOnlyDictionary<StarkParser.ObjectCreationExpressionContext, int>? _currentImportedTemplateObjectCreationOrdinals;
    private IReadOnlyDictionary<int, ImportedTemplateEnumConstructorSummary>? _currentImportedTemplateEnumConstructors;
    private IReadOnlyDictionary<StarkParser.EnumConstructorExpressionContext, int>? _currentImportedTemplateEnumConstructorOrdinals;
    private IReadOnlyDictionary<int, ImportedTemplateEnumCallSummary>? _currentImportedTemplateEnumCalls;
    private IReadOnlyDictionary<StarkParser.ArgumentListContext, int>? _currentImportedTemplateEnumCallOrdinals;
    private IReadOnlyDictionary<int, ImportedTemplateEnumValueSummary>? _currentImportedTemplateEnumValues;
    private IReadOnlyDictionary<StarkParser.PrimaryExpressionContext, int>? _currentImportedTemplateEnumValueOrdinals;
    private IReadOnlyDictionary<int, ImportedTemplateEnumPatternSummary>? _currentImportedTemplateEnumPatterns;
    private IReadOnlyDictionary<int, ImportedTemplateAggregatePatternSummary>? _currentImportedTemplateAggregatePatterns;
    private IReadOnlyDictionary<ParserRuleContext, int>? _currentImportedTemplateEnumPatternOrdinals;
    private IReadOnlyDictionary<string, StarkTypeSymbol>? _currentImportedTemplateLocalDeclarations;
    private IReadOnlyDictionary<StarkParser.UnaryExpressionContext, int>? _currentImportedTemplateConversionOrdinals;
    private IReadOnlyDictionary<int, StarkTypeSymbol>? _currentImportedTemplateConversions;
    private IReadOnlyDictionary<int, TypedFunctionSignature>? _currentImportedTemplateDirectCalls;
    private IReadOnlyDictionary<StarkParser.ArgumentListContext, int>? _currentImportedTemplateDirectCallOrdinals;
    private IReadOnlyDictionary<int, ImportedTemplateFieldAccessSummary>? _currentImportedTemplateFieldAccesses;
    private IReadOnlyDictionary<StarkParser.PostfixPartContext, int>? _currentImportedTemplateFieldAccessOrdinals;
    private IReadOnlyDictionary<int, TypedFunctionSignature>? _currentImportedTemplateMemberCalls;
    private IReadOnlyDictionary<StarkParser.ArgumentListContext, int>? _currentImportedTemplateMemberCallOrdinals;
    private CompileTimeFunctionEvaluator? _compileTimeFunctionEvaluator;
    private IReadOnlyDictionary<string, EnumLayoutSymbol>? _compileTimeEnumLayouts;
    private ThreadSafetyLawEvaluator? _threadSafetyLawEvaluator;
    private bool _canValidateDictionaryKeyConstraints;

    public TypeChecker(
        CompilerPassContext context,
        ParseResult parseResult,
        SyntaxModel syntaxModel,
        ModuleGraph moduleGraph,
        LoadedModuleSet loadedModules)
    {
        _context = context;
        _parseResult = parseResult;
        _syntaxModel = syntaxModel;
        _moduleGraph = moduleGraph;
        _loadedModules = loadedModules;
        _importedFunctionTemplates = loadedModules.ImportedModules
            .Where(static module => module.PackageImageFacts is { FunctionTemplates.Count: > 0 })
            .SelectMany(static module => module.PackageImageFacts!.FunctionTemplates)
            .ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal);
    }

    public TypeCheckModel Check()
    {
        _typeResolver = new StarkTypeResolver(_context, "type-check", _moduleGraph, _namedTypes, _typeAliases, _typeAliasSources);
        SeedNamedTypes();
        CollectTypeAliasSources();
        CheckTypeAliasDeclarations();
        PopulateNamedTypeFields();
        BuildConstructorShapes();
        BuildFunctionSignatures();
        _canValidateDictionaryKeyConstraints = true;
        ValidatePendingDictionaryKeyConstraints();
        SeedCompilerKnownCGlobals();
        CheckGlobalDeclarations();
        CheckConstructorBodies();
        CheckFunctionBodies();
        CheckImportedModuleNameAmbiguities();
        MaterializeImportedSourceInstantiations();
        ValidateThreadEntryMutableStaticReferences();

        var threadSafetyLawFacts = ComputeThreadSafetyLawFacts();
        ValidateCopyableAssertions();

        return new TypeCheckModel(
            _syntaxModel.ModuleName,
            _namedTypes,
            _typeAliases,
            _functions,
            _globals.ToDictionary(
                static pair => pair.Key,
                static pair => new TypedGlobalSymbol(
                    pair.Value.Name,
                    pair.Value.Type,
                    pair.Value.BindingKind ?? (pair.Value.IsMutable ? GlobalBindingKind.Mutable : GlobalBindingKind.Immutable),
                    pair.Value.ConstantInitializer),
                StringComparer.Ordinal),
            _literals,
            _objectCreations,
            _functionOverloads.ToDictionary(
                static pair => pair.Key,
                static pair => (IReadOnlyList<TypedFunctionSignature>)pair.Value.ToArray(),
                StringComparer.Ordinal),
            _functionInstantiationTriggers,
            _deferredFunctionInstantiationTriggers,
            _deferredTypeInstantiationTriggers,
            _typeInstantiationTriggers,
            _enumConstructors,
            _enumCalls,
            _enumValues,
            _enumPatterns,
            _aggregatePatterns,
            _localDeclarations,
            _localStorageCapacities,
            _conversions,
            _directCalls,
            _functionPointerPromotions,
            _indirectCalls,
            _closureCalls,
            _fieldAccesses,
            _memberCalls,
            _typeLayoutExpressions,
            _lambdas,
            _closureLambdas,
            _lambdaCaptures,
            _addressTakenFunctions,
            _indexAccesses,
            _dynamicStorageOperations,
            _switches,
            _closureFunctionPromotions,
            _boundOperations,
            _tryPropagations,
            threadSafetyLawFacts,
            _constructors.ToDictionary(
                static pair => pair.Key,
                static pair => (IReadOnlyList<TypedConstructorShape>)pair.Value
                    .Select(static shape => new TypedConstructorShape(
                        shape.Name,
                        shape.Parameters,
                        shape.IsPrimaryShape,
                        shape.BodyKey))
                    .ToArray(),
                StringComparer.Ordinal));
    }

    // [Copyable] on a struct, record, or enum asserts structural copyability
    // at the definition, so adding an owning field or a destructor later
    // errors here instead of at every downstream copy site.
    private void ValidateCopyableAssertions()
    {
        foreach (var module in _loadedModules.Modules.Values)
        {
            foreach (var declaration in module.ParseResult.Root.topLevelDeclaration())
            {
                string? typeName = null;
                Antlr4.Runtime.IToken? nameToken = null;
                if (declaration.structDeclaration() is { } structDeclaration)
                {
                    nameToken = structDeclaration.Identifier().Symbol;
                    typeName = structDeclaration.Identifier().GetText();
                }
                else if (declaration.recordDeclaration() is { } recordDeclaration)
                {
                    nameToken = recordDeclaration.Identifier().Symbol;
                    typeName = recordDeclaration.Identifier().GetText();
                }
                else if (declaration.enumDeclaration() is { } enumDeclaration)
                {
                    nameToken = enumDeclaration.Identifier().Symbol;
                    typeName = enumDeclaration.Identifier().GetText();
                }

                if (typeName is null
                    || !declaration.attributeList().Any(static list => list.attribute().Any(static attribute =>
                        attribute.attributeArgument().Length == 0
                        && string.Equals(attribute.qualifiedName().GetText(), ThreadSafetyLawNames.Copyable, StringComparison.Ordinal))))
                {
                    continue;
                }

                var qualifiedName = QualifyName(module, typeName);
                if (!_namedTypes.ContainsKey(qualifiedName))
                {
                    continue;
                }

                var type = StarkTypeSymbols.Named(qualifiedName);
                var fact = GetThreadSafetyLawEvaluator().Evaluate(ThreadSafetyLawNames.Copyable, type);
                if (fact.Holds)
                {
                    continue;
                }

                var failure = fact.FailureReasons.FirstOrDefault();
                var reason = failure?.Message ?? $"Type '{typeName}' is not structurally Copyable.";
                var fieldChain = failure?.Path is { Count: > 0 } path
                    ? $" Responsible field chain: {typeName}.{string.Join(".", path)}."
                    : string.Empty;
                ReportError(
                    "STK3051",
                    $"[Copyable] assertion failed: {reason}{fieldChain}",
                    Location(nameToken!));
            }
        }
    }

    private IReadOnlyDictionary<string, ThreadSafetyLawTypeFacts> ComputeThreadSafetyLawFacts()
    {
        var evaluator = new ThreadSafetyLawEvaluator(
            _namedTypes,
            _syntaxModel.ModuleName,
            (code, message) => ReportError(code, message, SourceLocation.Synthetic()));
        return evaluator.ComputeNamedTypeFacts();
    }

    private void CollectTypeAliasSources()
    {
        foreach (var module in _loadedModules.Modules.Values)
        {
            if (!module.Reference.IsRoot
                && module.PackageImageFacts is { TypeAliases.Count: > 0 } packageImageFacts)
            {
                foreach (var typeAlias in packageImageFacts.TypeAliases.Values)
                {
                    _typeAliases[typeAlias.Name] = typeAlias;
                }

                continue;
            }

            foreach (var declaration in module.ParseResult.Root.topLevelDeclaration())
            {
                if (declaration.typeAliasDeclaration() is not { } typeAliasDeclaration)
                {
                    continue;
                }

                var declarationModel = module.SyntaxModel.Declarations.LastOrDefault(
                    candidate => candidate.Kind == DeclarationKind.TypeAlias
                        && string.Equals(candidate.Name, typeAliasDeclaration.Identifier().GetText(), StringComparison.Ordinal));
                if (declarationModel?.TypeAlias is null)
                {
                    continue;
                }

                var lookupName = QualifyName(module, declarationModel.Name);
                _typeAliasSources[lookupName] = new TypeAliasResolutionSource(
                    lookupName,
                    module.SyntaxModel.ModuleName,
                    declarationModel.Visibility,
                    module.Reference.IsExternal,
                    declarationModel.TypeAlias.GenericParameters,
                    GetComptimeGenericParameters(
                        typeAliasDeclaration.typeParameterList(),
                        declarationModel.TypeAlias.GenericParameters.ToHashSet(StringComparer.Ordinal),
                        module.SyntaxModel.ModuleName),
                    typeAliasDeclaration.type_(),
                    typeAliasDeclaration.Identifier().Symbol);
            }
        }
    }

    private void CheckTypeAliasDeclarations()
    {
        foreach (var source in _typeAliasSources.Values)
        {
            _typeResolver!.TryResolveDeclaredTypeAlias(source.LookupName, source.ModuleName, out _);
        }
    }

    private void SeedCompilerKnownCGlobals()
    {
        if (!StarkCDataModelFacts.TryResolve(_context.Options.TargetInfo, out var cDataModel))
        {
            return;
        }

        _globals[StarkCDataModelFacts.CCharIsSignedGlobalName] = new VariableSymbol(
            StarkCDataModelFacts.CCharIsSignedGlobalName,
            StarkTypeSymbols.Bool,
            IsMutable: false,
            IsConstant: true,
            BindingKind: GlobalBindingKind.Const,
            ConstantValue: CompileTimeConstant.Bool(cDataModel.CharIsSigned),
            HasConstProvenance: true,
            ConstantInitializer: new TypedConstantInitializer(
                TypedConstantInitializerKind.Bool,
                StarkTypeSymbols.Bool,
                BoolValue: cDataModel.CharIsSigned));
    }

    private void SeedNamedTypes()
    {
        foreach (var builtinNamedType in StarkTypeSymbols.BuiltinNamedTypes)
        {
            _namedTypes[builtinNamedType.Name] = builtinNamedType;
        }

        foreach (var module in _loadedModules.Modules.Values)
        {
            foreach (var declaration in module.SyntaxModel.Declarations)
            {
                if (declaration.Kind is not (DeclarationKind.Struct or DeclarationKind.Record or DeclarationKind.Enum or DeclarationKind.Trait or DeclarationKind.Doctrine))
                {
                    continue;
                }

                if (!IsDeclarationVisible(module, declaration))
                {
                    continue;
                }

                var name = QualifyName(module, declaration.Name);
                var seedGenericParameters = GetSeedTypeGenericParameters(module, declaration);
                _namedTypes[name] = new NamedTypeSymbol(
                    name,
                    declaration.Kind,
                    new Dictionary<string, FieldSymbol>(StringComparer.Ordinal),
                    [],
                    GenericParameterNames: seedGenericParameters.GenericParameterNames?.ToList(),
                    ComptimeGenericParameterNames: seedGenericParameters.ComptimeGenericParameters.Count == 0
                        ? null
                        : seedGenericParameters.ComptimeGenericParameters.ToArray(),
                    IsDynTrait: IsDynTraitDeclaration(module, declaration),
                    DeclaringModuleName: module.SyntaxModel.ModuleName,
                    Visibility: declaration.Visibility);
            }
        }
    }

    private (HashSet<string>? GenericParameterNames, IReadOnlyList<ComptimeGenericParameterSymbol> ComptimeGenericParameters) GetSeedTypeGenericParameters(
        LoadedModuleDocument module,
        TopLevelDeclarationModel declaration)
    {
        var typeParameterList = FindTypeParameterList(module, declaration);
        if (typeParameterList is null)
        {
            return (null, []);
        }

        var genericParameterNames = GetGenericParameterNames(typeParameterList);
        return (
            genericParameterNames,
            GetComptimeGenericParameters(typeParameterList, genericParameterNames, module.SyntaxModel.ModuleName));
    }

    private static StarkParser.TypeParameterListContext? FindTypeParameterList(
        LoadedModuleDocument module,
        TopLevelDeclarationModel declaration)
    {
        foreach (var topLevel in module.ParseResult.Root.topLevelDeclaration())
        {
            if (declaration.Kind == DeclarationKind.Struct
                && topLevel.structDeclaration() is { } structDeclaration
                && string.Equals(structDeclaration.Identifier().GetText(), declaration.Name, StringComparison.Ordinal))
            {
                return structDeclaration.typeParameterList();
            }

            if (declaration.Kind == DeclarationKind.Record
                && topLevel.recordDeclaration() is { } recordDeclaration
                && string.Equals(recordDeclaration.Identifier().GetText(), declaration.Name, StringComparison.Ordinal))
            {
                return recordDeclaration.typeParameterList();
            }

            if (declaration.Kind == DeclarationKind.Enum
                && topLevel.enumDeclaration() is { } enumDeclaration
                && string.Equals(enumDeclaration.Identifier().GetText(), declaration.Name, StringComparison.Ordinal))
            {
                return enumDeclaration.typeParameterList();
            }

            if (declaration.Kind == DeclarationKind.Trait
                && topLevel.traitDeclaration() is { } traitDeclaration
                && string.Equals(traitDeclaration.Identifier().GetText(), declaration.Name, StringComparison.Ordinal))
            {
                return traitDeclaration.typeParameterList();
            }

            if (declaration.Kind == DeclarationKind.Doctrine
                && topLevel.doctrineDeclaration() is { } doctrineDeclaration
                && string.Equals(doctrineDeclaration.Identifier().GetText(), declaration.Name, StringComparison.Ordinal))
            {
                return doctrineDeclaration.typeParameterList();
            }
        }

        return null;
    }

    private bool IsDynTraitDeclaration(LoadedModuleDocument module, TopLevelDeclarationModel declaration)
    {
        if (declaration.Kind != DeclarationKind.Trait)
        {
            return false;
        }

        if (!module.Reference.IsRoot
            && module.PackageImageFacts?.NamedTypes.TryGetValue(QualifyName(module, declaration.Name), out var packagedType) == true)
        {
            return packagedType.IsDynTrait;
        }

        return module.ParseResult.Root.topLevelDeclaration()
            .Select(static topLevel => topLevel.traitDeclaration())
            .Any(traitDeclaration => traitDeclaration is not null
                && string.Equals(traitDeclaration.Identifier().GetText(), declaration.Name, StringComparison.Ordinal)
                && traitDeclaration.DYN() is not null);
    }

    private void PopulateNamedTypeFields()
    {
        foreach (var module in _loadedModules.Modules.Values)
        {
            if (!module.Reference.IsRoot
                && module.PackageImageFacts is { NamedTypes.Count: > 0 } packageImageFacts)
            {
                foreach (var declaration in module.SyntaxModel.Declarations)
                {
                    if (declaration.Kind is not (DeclarationKind.Struct or DeclarationKind.Record or DeclarationKind.Enum or DeclarationKind.Trait or DeclarationKind.Doctrine))
                    {
                        continue;
                    }

                    if (!IsDeclarationVisible(module, declaration))
                    {
                        continue;
                    }

                    var qualifiedName = QualifyName(module, declaration.Name);
                    if (packageImageFacts.NamedTypes.TryGetValue(qualifiedName, out var namedType))
                    {
                        _namedTypes[qualifiedName] = namedType;
                    }
                }

                continue;
            }

            foreach (var declaration in module.ParseResult.Root.topLevelDeclaration())
            {
                if (declaration.structDeclaration() is { } structDeclaration)
                {
                    var declarationModel = module.SyntaxModel.Declarations.FirstOrDefault(
                        candidate => candidate.Kind == DeclarationKind.Struct && string.Equals(candidate.Name, structDeclaration.Identifier().GetText(), StringComparison.Ordinal));
                    if (declarationModel is null || !IsDeclarationVisible(module, declarationModel))
                    {
                        continue;
                    }

                    var typeName = QualifyName(module, structDeclaration.Identifier().GetText());
                    var structGenericParams = GetGenericParameterNames(structDeclaration.typeParameterList());
                    var structComptimeParams = GetComptimeGenericParameters(
                        structDeclaration.typeParameterList(),
                        structGenericParams,
                        module.SyntaxModel.ModuleName);
                    var implementedTraits = ResolveBaseTraits(
                        structDeclaration.baseTraitList(),
                        structGenericParams,
                        module.SyntaxModel.ModuleName);
                    _namedTypes[typeName] = BuildStructLikeNamedType(
                        typeName,
                        DeclarationKind.Struct,
                        structDeclaration.structBody().structMember(),
                        module.SyntaxModel.ModuleName,
                        declarationModel.Visibility,
                        structGenericParams,
                        structComptimeParams,
                        implementedTraits.Names,
                        implementedTraits.Types,
                        ResolveStructLayoutMetadata(typeName, declaration.attributeList()),
                        declaration.attributeList());
                    continue;
                }

                if (declaration.recordDeclaration() is { } recordDeclaration)
                {
                    var declarationModel = module.SyntaxModel.Declarations.FirstOrDefault(
                        candidate => candidate.Kind == DeclarationKind.Record && string.Equals(candidate.Name, recordDeclaration.Identifier().GetText(), StringComparison.Ordinal));
                    if (declarationModel is null || !IsDeclarationVisible(module, declarationModel))
                    {
                        continue;
                    }

                    var recordName = QualifyName(module, recordDeclaration.Identifier().GetText());
                    var fields = new Dictionary<string, FieldSymbol>(StringComparer.Ordinal);
                    var orderedFields = new List<FieldSymbol>();
                    var genericParameters = GetGenericParameterNames(recordDeclaration.typeParameterList());
                    var comptimeParameters = GetComptimeGenericParameters(
                        recordDeclaration.typeParameterList(),
                        genericParameters,
                        module.SyntaxModel.ModuleName);
                    var comptimeParameterMap = ToComptimeGenericParameterMap(comptimeParameters);
                    var implementedTraits = ResolveBaseTraits(
                        recordDeclaration.baseTraitList(),
                        genericParameters,
                        module.SyntaxModel.ModuleName);

                    if (recordDeclaration.primaryConstructorParameters() is { } primaryConstructor)
                    {
                        foreach (var parameter in primaryConstructor.parameterList().parameter())
                        {
                            var fieldType = ResolveType(parameter.type_(), genericParameters, module.SyntaxModel.ModuleName, comptimeParameterMap);
                            var fieldName = parameter.Identifier().GetText();
                            AddField(
                                fields,
                                orderedFields,
                                new FieldSymbol(
                                    fieldName,
                                    ValidateRuntimeValueType(
                                        fieldType,
                                        parameter.type_(),
                                        $"field '{fieldName}' in type '{recordName}'"),
                                    InheritedFieldVisibility(declarationModel.Visibility),
                                    module.SyntaxModel.ModuleName));
                        }
                    }

                    foreach (var member in recordDeclaration.recordBody().recordMember())
                    {
                        if (member.fieldDeclaration() is { } field)
                        {
                            AddFields(
                                fields,
                                orderedFields,
                                field,
                                member.attributeList(),
                                genericParameters,
                                module.SyntaxModel.ModuleName,
                                recordName,
                                declarationModel.Visibility,
                                containingLayout: null,
                                comptimeGenericParameters: comptimeParameterMap);
                        }
                    }

                    _namedTypes[recordName] = new NamedTypeSymbol(
                        recordName,
                        DeclarationKind.Record,
                        fields,
                        orderedFields,
                        GenericParameterNames: genericParameters?.ToList(),
                        ComptimeGenericParameterNames: comptimeParameters.Count == 0 ? null : comptimeParameters.ToArray(),
                        ImplementedTraitNames: implementedTraits.Names,
                        ImplementedTraitTypeSymbols: implementedTraits.Types,
                        AssociatedTypeMembers: BuildRecordAssociatedTypes(
                            recordName,
                            recordDeclaration.recordBody().recordMember(),
                            genericParameters,
                            module.SyntaxModel.ModuleName,
                            requireTargetType: true),
                        ThreadSafetyLawAttributes: ResolveThreadSafetyLawAttributes(
                            declaration.attributeList(),
                            genericParameters,
                            module.SyntaxModel.ModuleName,
                            comptimeParameterMap),
                        DeclaringModuleName: module.SyntaxModel.ModuleName,
                        Visibility: declarationModel.Visibility,
                        HasDestructor: recordDeclaration.recordBody().recordMember()
                            .Any(static member => member.destructorDeclaration() is not null));
                    continue;
                }

                if (declaration.enumDeclaration() is { } enumDeclaration)
                {
                    var declarationModel = module.SyntaxModel.Declarations.FirstOrDefault(
                        candidate => candidate.Kind == DeclarationKind.Enum && string.Equals(candidate.Name, enumDeclaration.Identifier().GetText(), StringComparison.Ordinal));
                    if (declarationModel is null || !IsDeclarationVisible(module, declarationModel))
                    {
                        continue;
                    }

                    var enumName = QualifyName(module, enumDeclaration.Identifier().GetText());
                    var genericParameters = GetGenericParameterNames(enumDeclaration.typeParameterList());
                    var comptimeParameters = GetComptimeGenericParameters(
                        enumDeclaration.typeParameterList(),
                        genericParameters,
                        module.SyntaxModel.ModuleName);
                    _namedTypes[enumName] = BuildEnumNamedType(
                        enumName,
                        enumDeclaration.enumBody().enumVariantDeclaration(),
                        genericParameters,
                        comptimeParameters,
                        module.SyntaxModel.ModuleName,
                        declaration.attributeList(),
                        declarationModel.Visibility);
                    continue;
                }

                if (declaration.traitDeclaration() is { } traitDeclaration)
                {
                    var declarationModel = module.SyntaxModel.Declarations.FirstOrDefault(
                        candidate => candidate.Kind == DeclarationKind.Trait && string.Equals(candidate.Name, traitDeclaration.Identifier().GetText(), StringComparison.Ordinal));
                    if (declarationModel is null || !IsDeclarationVisible(module, declarationModel))
                    {
                        continue;
                    }

                    var traitName = QualifyName(module, traitDeclaration.Identifier().GetText());
                    var genericParameters = GetGenericParameterNames(traitDeclaration.typeParameterList());
                    var comptimeParameters = GetComptimeGenericParameters(
                        traitDeclaration.typeParameterList(),
                        genericParameters,
                        module.SyntaxModel.ModuleName);
                    _namedTypes[traitName] = new NamedTypeSymbol(
                        traitName,
                        DeclarationKind.Trait,
                        new Dictionary<string, FieldSymbol>(StringComparer.Ordinal),
                        [],
                        GenericParameterNames: genericParameters?.ToList(),
                        ComptimeGenericParameterNames: comptimeParameters.Count == 0 ? null : comptimeParameters.ToArray(),
                        AssociatedTypeMembers: BuildTraitAssociatedTypes(
                            traitName,
                            traitDeclaration.traitBody().traitMember(),
                            genericParameters,
                            module.SyntaxModel.ModuleName),
                        IsDynTrait: traitDeclaration.DYN() is not null,
                        DeclaringModuleName: module.SyntaxModel.ModuleName,
                        Visibility: declarationModel.Visibility);
                    continue;
                }

                if (declaration.doctrineDeclaration() is { } doctrineDeclaration)
                {
                    var declarationModel = module.SyntaxModel.Declarations.FirstOrDefault(
                        candidate => candidate.Kind == DeclarationKind.Doctrine && string.Equals(candidate.Name, doctrineDeclaration.Identifier().GetText(), StringComparison.Ordinal));
                    if (declarationModel is null || !IsDeclarationVisible(module, declarationModel))
                    {
                        continue;
                    }

                    var doctrineName = QualifyName(module, doctrineDeclaration.Identifier().GetText());
                    var genericParameters = GetGenericParameterNames(doctrineDeclaration.typeParameterList());
                    var comptimeParameters = GetComptimeGenericParameters(
                        doctrineDeclaration.typeParameterList(),
                        genericParameters,
                        module.SyntaxModel.ModuleName);
                    _namedTypes[doctrineName] = new NamedTypeSymbol(
                        doctrineName,
                        DeclarationKind.Doctrine,
                        new Dictionary<string, FieldSymbol>(StringComparer.Ordinal),
                        [],
                        GenericParameterNames: genericParameters?.ToList(),
                        ComptimeGenericParameterNames: comptimeParameters.Count == 0 ? null : comptimeParameters.ToArray(),
                        AssociatedTypeMembers: BuildDoctrineAssociatedTypes(
                            doctrineName,
                            doctrineDeclaration.doctrineBody().doctrineMember(),
                            genericParameters,
                            module.SyntaxModel.ModuleName),
                        DeclaringModuleName: module.SyntaxModel.ModuleName,
                        Visibility: declarationModel.Visibility);
                }
            }
        }
    }

    private NamedTypeSymbol BuildStructLikeNamedType(
        string name,
        DeclarationKind kind,
        IEnumerable<StarkParser.StructMemberContext> members,
        string currentModuleName,
        StarkVisibility containingVisibility,
        ISet<string>? genericParameters = null,
        IReadOnlyList<ComptimeGenericParameterSymbol>? comptimeGenericParameters = null,
        IReadOnlyList<string>? implementedTraitNames = null,
        IReadOnlyList<StarkTypeSymbol>? implementedTraitTypes = null,
        StructLayoutMetadata? layout = null,
        IEnumerable<StarkParser.AttributeListContext>? typeAttributeLists = null)
    {
        var fields = new Dictionary<string, FieldSymbol>(StringComparer.Ordinal);
        var orderedFields = new List<FieldSymbol>();
        var genericParameterNames = genericParameters?.ToList();
        var comptimeParameterList = comptimeGenericParameters ?? [];
        var comptimeParameterMap = ToComptimeGenericParameterMap(comptimeParameterList);
        var hasDestructor = members.Any(static member => member.destructorDeclaration() is not null);
        var threadSafetyLawAttributes = ResolveThreadSafetyLawAttributes(
            typeAttributeLists ?? [],
            genericParameters,
            currentModuleName,
            comptimeParameterMap);
        var associatedTypes = BuildStructAssociatedTypes(
            name,
            members,
            genericParameters,
            currentModuleName,
            requireTargetType: true);
        _namedTypes[name] = new NamedTypeSymbol(
            name,
            kind,
            fields,
            orderedFields,
            GenericParameterNames: genericParameterNames,
            ComptimeGenericParameterNames: comptimeParameterList.Count == 0 ? null : comptimeParameterList.ToArray(),
            ImplementedTraitNames: implementedTraitNames,
            ImplementedTraitTypeSymbols: implementedTraitTypes,
            AssociatedTypeMembers: associatedTypes,
            Layout: layout,
            ThreadSafetyLawAttributes: threadSafetyLawAttributes,
            DeclaringModuleName: currentModuleName,
            Visibility: containingVisibility,
            HasDestructor: hasDestructor);

        foreach (var member in members)
        {
            if (member.fieldDeclaration() is { } field)
            {
                AddFields(
                    fields,
                    orderedFields,
                    field,
                    member.attributeList(),
                    genericParameters,
                    currentModuleName,
                    name,
                    containingVisibility,
                    layout,
                    comptimeParameterMap);
            }
        }

        var namedType = new NamedTypeSymbol(name, kind, fields, orderedFields,
            GenericParameterNames: genericParameterNames,
            ComptimeGenericParameterNames: comptimeParameterList.Count == 0 ? null : comptimeParameterList.ToArray(),
            ImplementedTraitNames: implementedTraitNames,
            ImplementedTraitTypeSymbols: implementedTraitTypes,
            AssociatedTypeMembers: associatedTypes,
            Layout: layout,
            ThreadSafetyLawAttributes: threadSafetyLawAttributes,
            DeclaringModuleName: currentModuleName,
            Visibility: containingVisibility,
            HasDestructor: hasDestructor);
        RefreshConcreteInstantiationsForTemplate(namedType);
        return namedType;
    }

    private IReadOnlyDictionary<string, AssociatedTypeSymbol>? BuildStructAssociatedTypes(
        string ownerName,
        IEnumerable<StarkParser.StructMemberContext> members,
        ISet<string>? genericParameters,
        string currentModuleName,
        bool requireTargetType)
    {
        return BuildAssociatedTypes(
            ownerName,
            members
                .Select(static member => member.associatedTypeDeclaration())
                .Where(static declaration => declaration is not null)!
                .Cast<StarkParser.AssociatedTypeDeclarationContext>(),
            genericParameters,
            currentModuleName,
            allowBareRequirements: !requireTargetType);
    }

    private IReadOnlyDictionary<string, AssociatedTypeSymbol>? BuildRecordAssociatedTypes(
        string ownerName,
        IEnumerable<StarkParser.RecordMemberContext> members,
        ISet<string>? genericParameters,
        string currentModuleName,
        bool requireTargetType)
    {
        return BuildAssociatedTypes(
            ownerName,
            members
                .Select(static member => member.associatedTypeDeclaration())
                .Where(static declaration => declaration is not null)!
                .Cast<StarkParser.AssociatedTypeDeclarationContext>(),
            genericParameters,
            currentModuleName,
            allowBareRequirements: !requireTargetType);
    }

    private IReadOnlyDictionary<string, AssociatedTypeSymbol>? BuildTraitAssociatedTypes(
        string ownerName,
        IEnumerable<StarkParser.TraitMemberContext> members,
        ISet<string>? genericParameters,
        string currentModuleName)
    {
        var traitGenericParameters = ExtendGenericParameters(genericParameters, "Self");
        return BuildAssociatedTypes(
            ownerName,
            members
                .Select(static member => member.associatedTypeDeclaration())
                .Where(static declaration => declaration is not null)!
                .Cast<StarkParser.AssociatedTypeDeclarationContext>(),
            traitGenericParameters,
            currentModuleName,
            allowBareRequirements: true);
    }

    private IReadOnlyDictionary<string, AssociatedTypeSymbol>? BuildDoctrineAssociatedTypes(
        string ownerName,
        IEnumerable<StarkParser.DoctrineMemberContext> members,
        ISet<string>? genericParameters,
        string currentModuleName)
    {
        return BuildAssociatedTypes(
            ownerName,
            members
                .Select(static member => member.associatedTypeDeclaration())
                .Where(static declaration => declaration is not null)!
                .Cast<StarkParser.AssociatedTypeDeclarationContext>(),
            genericParameters,
            currentModuleName,
            allowBareRequirements: false);
    }

    private IReadOnlyDictionary<string, AssociatedTypeSymbol>? BuildAssociatedTypes(
        string ownerName,
        IEnumerable<StarkParser.AssociatedTypeDeclarationContext> declarations,
        ISet<string>? genericParameters,
        string currentModuleName,
        bool allowBareRequirements)
    {
        var result = new Dictionary<string, AssociatedTypeSymbol>(StringComparer.Ordinal);
        foreach (var declaration in declarations)
        {
            var name = declaration.Identifier().GetText();
            if (result.ContainsKey(name))
            {
                ReportError(
                    "STK3051",
                    $"Type '{ownerName}' declares associated type '{name}' more than once.",
                    declaration);
                continue;
            }

            if (declaration.type_() is not { } targetTypeSyntax)
            {
                if (!allowBareRequirements)
                {
                    ReportError(
                        "STK3051",
                        $"Type '{ownerName}' must define associated type '{name}' with '= <type>'; only traits may declare bare associated type requirements.",
                        declaration);
                }

                result[name] = new AssociatedTypeSymbol(name);
                continue;
            }

            var targetType = ResolveType(targetTypeSyntax, genericParameters, currentModuleName);
            result[name] = new AssociatedTypeSymbol(name, targetType);
        }

        return result.Count == 0 ? null : result;
    }

    private static ISet<string>? ExtendGenericParameters(ISet<string>? genericParameters, string name)
    {
        var result = genericParameters is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : new HashSet<string>(genericParameters, StringComparer.Ordinal);
        result.Add(name);
        return result;
    }

    // Resolves each base-list entry to the qualified name and typed shape of the
    // trait it names. Non-trait entries are dropped here; the base-list-must-be-trait
    // contract is reported separately in semantic validation (STK3026).
    private (IReadOnlyList<string>? Names, IReadOnlyList<StarkTypeSymbol>? Types) ResolveBaseTraits(
        StarkParser.BaseTraitListContext? baseTraitList,
        ISet<string>? genericParameters,
        string currentModuleName)
    {
        if (baseTraitList is null)
        {
            return (null, null);
        }

        var names = new List<string>();
        var types = new List<StarkTypeSymbol>();
        foreach (var entry in baseTraitList.type_())
        {
            var resolved = ResolveType(entry, genericParameters, currentModuleName);
            if (resolved.NamedType is { } namedType
                && TryResolveBaseTraitSymbol(namedType, out var symbol)
                && symbol.Kind == DeclarationKind.Trait
                && !names.Contains(namedType))
            {
                names.Add(namedType);
                types.Add(resolved);
            }
        }

        return names.Count > 0 ? (names, types) : (null, null);
    }

    private bool TryResolveBaseTraitSymbol(string name, out NamedTypeSymbol symbol)
    {
        if (_namedTypes.TryGetValue(name, out symbol!))
        {
            return true;
        }

        var baseName = StarkTypeSymbols.GetGenericBaseName(name);
        return !string.Equals(baseName, name, StringComparison.Ordinal)
            && _namedTypes.TryGetValue(baseName, out symbol!);
    }

    // Parses `where T: Trait, ...` clauses into typed constraints so they can be
    // enforced at instantiation sites and used to resolve trait-method calls on
    // the type parameter inside the body. Bound types are resolved in the
    // function's generic scope.
    private IReadOnlyList<TypeParameterConstraint>? ParseTypeParameterConstraints(
        DeclaredFunctionSyntax functionSyntax,
        ISet<string>? genericParameters,
        string currentModuleName)
    {
        var constraintContexts = GetTypeParameterConstraintContexts(functionSyntax.DeclarationContext);
        if (constraintContexts.Length == 0)
        {
            return null;
        }

        var result = new List<TypeParameterConstraint>();
        foreach (var constraintContext in constraintContexts)
        {
            var parameterName = constraintContext.Identifier().GetText();
            var bounds = new List<StarkTypeSymbol>();
            foreach (var boundContext in constraintContext.type_())
            {
                bounds.Add(ResolveType(boundContext, genericParameters, currentModuleName));
            }

            if (bounds.Count > 0)
            {
                result.Add(new TypeParameterConstraint(parameterName, bounds));
            }
        }

        return result.Count > 0 ? result : null;
    }

    private static StarkParser.TypeParameterConstraintsContext[] GetTypeParameterConstraintContexts(ParserRuleContext declarationContext)
    {
        return declarationContext switch
        {
            StarkParser.FunctionDeclarationContext function => function.typeParameterConstraints(),
            StarkParser.MethodDeclarationContext method => method.typeParameterConstraints(),
            StarkParser.TraitMethodDeclarationContext traitMethod => traitMethod.typeParameterConstraints(),
            StarkParser.DoctrineMethodDeclarationContext doctrineMethod => doctrineMethod.typeParameterConstraints(),
            _ => []
        };
    }

    private IReadOnlyList<ThreadSafetyLawPredicateSymbol>? ParseThreadSafetyLawPredicates(
        DeclaredFunctionSyntax functionSyntax,
        ISet<string>? genericParameters,
        string currentModuleName,
        IReadOnlyDictionary<string, ComptimeGenericParameterSymbol>? comptimeGenericParameters)
    {
        List<ThreadSafetyLawPredicateSymbol>? predicates = null;
        foreach (var clause in GetParameterMemoryContractClauses(functionSyntax.DeclarationContext))
        {
            foreach (var contract in clause.parameterMemoryContract())
            {
                if (contract.lawPredicateContract() is not { } predicate
                    || !IsThreadSafetyLawName(predicate.Identifier().GetText()))
                {
                    continue;
                }

                predicates ??= [];
                predicates.Add(new ThreadSafetyLawPredicateSymbol(
                    predicate.Identifier().GetText(),
                    ResolveType(predicate.type_(), genericParameters, currentModuleName, comptimeGenericParameters)));
            }
        }

        return predicates;
    }

    // Inside a trait method body `Self` is implicitly bound by the enclosing trait,
    // so trait-method calls on `self` (e.g. a default body calling another trait
    // method) resolve through that bound via the same generic-dispatch path (CG05).
    private IReadOnlyList<TypeParameterConstraint> WithImplicitTraitSelfConstraint(TypedFunctionSignature signature)
    {
        var name = signature.SourceName ?? signature.Name;
        var lastDot = name.LastIndexOf('.');
        if (lastDot <= 0)
        {
            return signature.Constraints;
        }

        var containingTypeName = name[..lastDot];
        if (!_namedTypes.TryGetValue(containingTypeName, out var containingType)
            || containingType.Kind != DeclarationKind.Trait
            || signature.Constraints.Any(constraint => string.Equals(constraint.ParameterName, "Self", StringComparison.Ordinal)))
        {
            return signature.Constraints;
        }

        var selfBound = containingType.IsGeneric
            ? StarkTypeSymbols.GenericInstantiation(
                containingTypeName,
                containingType.GenericParams.Select(parameter => StarkTypeSymbols.Named(parameter)).ToArray())
            : StarkTypeSymbols.Named(containingTypeName);
        return new List<TypeParameterConstraint>(signature.Constraints)
        {
            new("Self", [selfBound]),
        };
    }

    private NamedTypeSymbol BuildEnumNamedType(
        string name,
        IEnumerable<StarkParser.EnumVariantDeclarationContext> variantDeclarations,
        ISet<string>? genericParameters,
        IReadOnlyList<ComptimeGenericParameterSymbol>? comptimeGenericParameters,
        string currentModuleName,
        IEnumerable<StarkParser.AttributeListContext>? typeAttributeLists = null,
        StarkVisibility visibility = StarkVisibility.Module)
    {
        var variants = new List<EnumVariantSymbol>();
        var variantContexts = new List<StarkParser.EnumVariantDeclarationContext>();
        var seenVariantNames = new HashSet<string>(StringComparer.Ordinal);
        var funnelSourceTypeToVariant = new Dictionary<string, string>(StringComparer.Ordinal);
        var comptimeParameterList = comptimeGenericParameters ?? [];
        var comptimeParameterMap = ToComptimeGenericParameterMap(comptimeParameterList);
        var threadSafetyLawAttributes = ResolveThreadSafetyLawAttributes(
            typeAttributeLists ?? [],
            genericParameters,
            currentModuleName,
            comptimeParameterMap);

        foreach (var variantDeclaration in variantDeclarations)
        {
            var variantName = variantDeclaration.Identifier().GetText();
            if (!seenVariantNames.Add(variantName))
            {
                ReportError(
                    "STK3006",
                    $"Enum '{name}' declares variant '{variantName}' more than once.",
                    variantDeclaration);
                continue;
            }

            // Innate variant attributes: `[Ok]` / `[Err]` declare the propagation roles
            // that `try` consults (doc 11 v2). Anything else on a variant is rejected.
            var role = ResolveEnumVariantRole(variantDeclaration, name, variantName);

            var payload = variantDeclaration.enumVariantPayload();
            if (payload is null)
            {
                variants.Add(new EnumVariantSymbol(variantName, UsesNamedFields: false, Fields: [], Role: role));
                variantContexts.Add(variantDeclaration);
                continue;
            }

            // `Io from IoError` — a single positional-payload variant additionally
            // marked as the canonical error funnel that `try` uses to wrap an
            // `IoError` into this enum. Layout/construction/matching are identical to
            // `Io(IoError)`; only the `AbsorbsErrorType` marker is added.
            if (payload.FROM() is not null)
            {
                var sourceType = ValidateRuntimeValueType(
                    ResolveType(payload.type_(0), genericParameters, currentModuleName, comptimeParameterMap),
                    payload.type_(0),
                    $"enum variant '{name}.{variantName}' from-payload");
                var funnelKey = sourceType.DisplayName;
                if (funnelSourceTypeToVariant.TryGetValue(funnelKey, out var existingVariant))
                {
                    ReportError(
                        "STK3040",
                        $"Enum '{name}' declares more than one `from` funnel for '{sourceType.DisplayName}' "
                            + $"(variants '{existingVariant}' and '{variantName}'). `try` could not pick one; "
                            + "give each absorbed error type a single `from` variant, or convert explicitly.",
                        variantDeclaration);
                }
                else
                {
                    funnelSourceTypeToVariant.Add(funnelKey, variantName);
                }

                variants.Add(new EnumVariantSymbol(
                    variantName,
                    UsesNamedFields: false,
                    Fields: [new EnumVariantFieldSymbol(0, Name: null, sourceType)],
                    AbsorbsErrorType: sourceType,
                    Role: role));
                variantContexts.Add(variantDeclaration);
                continue;
            }

            if (payload.enumVariantFieldDeclaration().Length != 0)
            {
                var fields = new List<EnumVariantFieldSymbol>();
                var seenFieldNames = new HashSet<string>(StringComparer.Ordinal);

                foreach (var (fieldDeclaration, index) in payload.enumVariantFieldDeclaration().Select((field, index) => (field, index)))
                {
                    var fieldName = fieldDeclaration.Identifier().GetText();
                    if (!seenFieldNames.Add(fieldName))
                    {
                        ReportError(
                            "STK3006",
                            $"Enum variant '{variantName}' declares field '{fieldName}' more than once.",
                            fieldDeclaration);
                        continue;
                    }

                    fields.Add(new EnumVariantFieldSymbol(
                        index,
                        fieldName,
                        ValidateRuntimeValueType(
                            ResolveType(fieldDeclaration.type_(), genericParameters, currentModuleName, comptimeParameterMap),
                            fieldDeclaration.type_(),
                            $"enum variant field '{fieldName}' in '{name}.{variantName}'")));
                }

                variants.Add(new EnumVariantSymbol(variantName, UsesNamedFields: true, Fields: fields, Role: role));
                variantContexts.Add(variantDeclaration);
                continue;
            }

            variants.Add(new EnumVariantSymbol(
                variantName,
                UsesNamedFields: false,
                payload.type_()
                    .Select((fieldType, index) => new EnumVariantFieldSymbol(
                        index,
                        Name: null,
                        ValidateRuntimeValueType(
                            ResolveType(fieldType, genericParameters, currentModuleName, comptimeParameterMap),
                            fieldType,
                            $"enum variant field '{name}.{variantName}#{index}'")))
                    .ToArray(),
                Role: role));
            variantContexts.Add(variantDeclaration);
        }

        ValidateEnumPropagationRoles(name, variants, variantContexts);

        return new NamedTypeSymbol(
            name,
            DeclarationKind.Enum,
            new Dictionary<string, FieldSymbol>(StringComparer.Ordinal),
            [],
            EnumVariants: variants,
            GenericParameterNames: genericParameters?.ToList(),
            ComptimeGenericParameterNames: comptimeParameterList.Count == 0 ? null : comptimeParameterList.ToArray(),
            ThreadSafetyLawAttributes: threadSafetyLawAttributes,
            DeclaringModuleName: currentModuleName,
            Visibility: visibility);
    }

    /// <summary>
    /// Reads the innate `[Ok]` / `[Err]` propagation-role attributes from an enum
    /// variant declaration (doc 11 v2). Variant attributes are load-bearing for `try`,
    /// so anything unrecognized is a compile error rather than inert metadata — a typo
    /// like `[Okk]` must not silently produce a non-propagatable enum.
    /// </summary>
    private EnumVariantRole ResolveEnumVariantRole(
        StarkParser.EnumVariantDeclarationContext variantDeclaration,
        string enumName,
        string variantName)
    {
        var role = EnumVariantRole.None;
        foreach (var attributeList in variantDeclaration.attributeList())
        {
            foreach (var attribute in attributeList.attribute())
            {
                var attributeName = attribute.qualifiedName().GetText();
                var resolvedRole = attributeName switch
                {
                    "Ok" => EnumVariantRole.Ok,
                    "Err" => EnumVariantRole.Err,
                    _ => EnumVariantRole.None,
                };

                if (resolvedRole == EnumVariantRole.None)
                {
                    ReportError(
                        "STK3042",
                        $"Unknown attribute '[{attributeName}]' on enum variant '{enumName}.{variantName}'. "
                            + "The attributes recognized on enum variants are [Ok] and [Err].",
                        attribute);
                    continue;
                }

                if (attribute.LPAREN() is not null)
                {
                    ReportError(
                        "STK3042",
                        $"Attribute '[{attributeName}]' on enum variant '{enumName}.{variantName}' takes no arguments.",
                        attribute);
                }

                if (role != EnumVariantRole.None)
                {
                    ReportError(
                        "STK3043",
                        $"Enum variant '{enumName}.{variantName}' declares more than one propagation role; "
                            + "a variant is either [Ok] or [Err], never both.",
                        attribute);
                    continue;
                }

                role = resolvedRole;
            }
        }

        return role;
    }

    /// <summary>
    /// Validates an enum's propagation-role configuration (doc 11 v2). Roles are
    /// optional, but once any variant carries one, the enum must have exactly two
    /// variants — one [Ok] and one [Err] — and each role variant carries zero or one
    /// payload. Violations are STK3043.
    /// </summary>
    private void ValidateEnumPropagationRoles(
        string name,
        IReadOnlyList<EnumVariantSymbol> variants,
        IReadOnlyList<StarkParser.EnumVariantDeclarationContext> variantContexts)
    {
        var okCount = 0;
        var errCount = 0;
        foreach (var variant in variants)
        {
            if (variant.Role == EnumVariantRole.Ok)
            {
                okCount++;
            }
            else if (variant.Role == EnumVariantRole.Err)
            {
                errCount++;
            }
        }

        if (okCount == 0 && errCount == 0)
        {
            return;
        }

        if (variantContexts.Count == 0)
        {
            return;
        }

        var reportContext = variantContexts[0];
        if (variants.Count != 2 || okCount != 1 || errCount != 1)
        {
            ReportError(
                "STK3043",
                $"Enum '{name}' uses propagation role attributes but is not a propagatable shape: it has "
                    + $"{variants.Count} variant(s) with {okCount} [Ok] and {errCount} [Err]. A propagatable "
                    + "enum has exactly two variants, one [Ok] and one [Err].",
                reportContext);
            return;
        }

        for (var index = 0; index < variants.Count && index < variantContexts.Count; index++)
        {
            var variant = variants[index];
            if (variant.Role != EnumVariantRole.None && variant.Fields.Count > 1)
            {
                ReportError(
                    "STK3043",
                    $"Propagation role variant '{name}.{variant.Name}' must carry zero or one payload, "
                        + $"but it has {variant.Fields.Count}.",
                    variantContexts[index]);
            }
        }
    }

    private StructLayoutMetadata? ResolveStructLayoutMetadata(
        string typeName,
        IEnumerable<StarkParser.AttributeListContext> attributeLists)
    {
        StructLayoutKind layoutKind = StructLayoutKind.Auto;
        int? packBytes = null;
        int? alignBytes = null;
        StarkParser.AttributeContext? layoutAttribute = null;
        StarkParser.AttributeContext? packAttribute = null;
        StarkParser.AttributeContext? alignAttribute = null;

        foreach (var attributeList in attributeLists)
        {
            foreach (var attribute in attributeList.attribute())
            {
                var name = attribute.qualifiedName().GetText();
                switch (name)
                {
                    case "StructLayout":
                        if (layoutAttribute is not null)
                        {
                            ReportError(
                                "STK3048",
                                $"Struct '{typeName}' declares [StructLayout(...)] more than once.",
                                attribute);
                            continue;
                        }

                        layoutAttribute = attribute;
                        if (attribute.attributeArgument() is not [var layoutArgument])
                        {
                            ReportError(
                                "STK3048",
                                $"Struct '{typeName}' must spell layout as [StructLayout(C)] or [StructLayout(Explicit)].",
                                attribute);
                            continue;
                        }

                        layoutKind = layoutArgument.GetText() switch
                        {
                            "C" => StructLayoutKind.C,
                            "Explicit" => StructLayoutKind.Explicit,
                            _ => StructLayoutKind.Auto
                        };
                        if (layoutKind == StructLayoutKind.Auto)
                        {
                            ReportError(
                                "STK3048",
                                $"Unsupported StructLayout argument '{layoutArgument.GetText()}' on struct '{typeName}'. Use C or Explicit.",
                                layoutArgument);
                        }

                        continue;
                    case "Pack":
                        if (packAttribute is not null)
                        {
                            ReportError("STK3048", $"Struct '{typeName}' declares [Pack(...)] more than once.", attribute);
                            continue;
                        }

                        packAttribute = attribute;
                        packBytes = ResolvePowerOfTwoLayoutAttribute(typeName, "Pack", attribute);
                        continue;
                    case "Align":
                        if (alignAttribute is not null)
                        {
                            ReportError("STK3048", $"Struct '{typeName}' declares [Align(...)] more than once.", attribute);
                            continue;
                        }

                        alignAttribute = attribute;
                        alignBytes = ResolvePowerOfTwoLayoutAttribute(typeName, "Align", attribute);
                        continue;
                }
            }
        }

        if (packAttribute is not null && layoutKind != StructLayoutKind.C)
        {
            ReportError(
                "STK3048",
                $"Struct '{typeName}' may use [Pack(N)] only with [StructLayout(C)].",
                packAttribute);
        }

        if (alignAttribute is not null && layoutKind is not (StructLayoutKind.C or StructLayoutKind.Explicit))
        {
            ReportError(
                "STK3048",
                $"Struct '{typeName}' may use [Align(N)] only with [StructLayout(C)] or [StructLayout(Explicit)].",
                alignAttribute);
        }

        if (layoutKind == StructLayoutKind.Auto)
        {
            return null;
        }

        return new StructLayoutMetadata(
            layoutKind,
            layoutKind == StructLayoutKind.C ? packBytes : null,
            alignBytes);
    }

    private int? ResolvePowerOfTwoLayoutAttribute(
        string typeName,
        string attributeName,
        StarkParser.AttributeContext attribute)
    {
        if (attribute.attributeArgument() is not [var argument]
            || !BigInteger.TryParse(argument.GetText(), out var value)
            || value <= BigInteger.Zero
            || value > int.MaxValue
            || !IsPowerOfTwo(value))
        {
            ReportError(
                "STK3048",
                $"Struct '{typeName}' attribute [{attributeName}(N)] requires a positive power-of-two integer literal.",
                attribute);
            return null;
        }

        return (int)value;
    }

    private static bool IsPowerOfTwo(BigInteger value)
    {
        return value > BigInteger.Zero && (value & (value - BigInteger.One)) == BigInteger.Zero;
    }

    private IReadOnlyList<ThreadSafetyLawAttributeSymbol>? ResolveThreadSafetyLawAttributes(
        IEnumerable<StarkParser.AttributeListContext> attributeLists,
        ISet<string>? genericParameters,
        string currentModuleName,
        IReadOnlyDictionary<string, ComptimeGenericParameterSymbol>? comptimeGenericParameters = null)
    {
        List<ThreadSafetyLawAttributeSymbol>? attributes = null;
        foreach (var attribute in attributeLists.SelectMany(static list => list.attribute()))
        {
            if (!TryParseThreadSafetyLawAttributeKind(attribute.qualifiedName().GetText(), out var kind)
                || attribute.attributeArgument() is not [var lawArgument]
                || !IsThreadSafetyLawName(lawArgument.GetText()))
            {
                continue;
            }

            ThreadSafetyLawPredicateSymbol? condition = null;
            if (attribute.attributeCondition()?.lawPredicateContract() is { } predicate
                && IsThreadSafetyLawName(predicate.Identifier().GetText()))
            {
                condition = new ThreadSafetyLawPredicateSymbol(
                    predicate.Identifier().GetText(),
                    ResolveType(predicate.type_(), genericParameters, currentModuleName, comptimeGenericParameters));
            }

            attributes ??= [];
            attributes.Add(new ThreadSafetyLawAttributeSymbol(kind, lawArgument.GetText(), condition));
        }

        return attributes;
    }

    private static bool TryParseThreadSafetyLawAttributeKind(string attributeName, out ThreadSafetyLawAttributeKind kind)
    {
        if (string.Equals(attributeName, "Grant", StringComparison.Ordinal))
        {
            kind = ThreadSafetyLawAttributeKind.Grant;
            return true;
        }

        if (string.Equals(attributeName, "Deny", StringComparison.Ordinal))
        {
            kind = ThreadSafetyLawAttributeKind.Deny;
            return true;
        }

        kind = default;
        return false;
    }

    private static bool IsThreadSafetyLawName(string lawName)
    {
        return string.Equals(lawName, "Transferable", StringComparison.Ordinal)
            || string.Equals(lawName, "Shareable", StringComparison.Ordinal)
            || string.Equals(lawName, "Copyable", StringComparison.Ordinal);
    }

    private void AddFields(
        Dictionary<string, FieldSymbol> fields,
        List<FieldSymbol> orderedFields,
        StarkParser.FieldDeclarationContext fieldDeclaration,
        IEnumerable<StarkParser.AttributeListContext> attributeLists,
        ISet<string>? genericParameters,
        string currentModuleName,
        string containingTypeName,
        StarkVisibility containingVisibility,
        StructLayoutMetadata? containingLayout,
        IReadOnlyDictionary<string, ComptimeGenericParameterSymbol>? comptimeGenericParameters = null)
    {
        var fieldType = ResolveType(fieldDeclaration.type_(), genericParameters, currentModuleName, comptimeGenericParameters);
        var fieldVisibility = ResolveFieldVisibility(containingVisibility, fieldDeclaration.visibilityModifier());
        var explicitOffsetBytes = ResolveFieldOffsetBytes(containingTypeName, fieldDeclaration, attributeLists, containingLayout);
        var threadSafetyLawAttributes = ResolveThreadSafetyLawAttributes(
            attributeLists,
            genericParameters,
            currentModuleName,
            comptimeGenericParameters);

        if (fieldDeclaration.visibilityModifier() is not null && IsMoreVisible(fieldVisibility, containingVisibility))
        {
            ReportError(
                "STK3015",
                $"Field visibility '{RenderVisibility(fieldVisibility)}' is more visible than enclosing type visibility '{RenderVisibility(containingVisibility)}'.",
                fieldDeclaration.visibilityModifier()!);
        }

        foreach (var declarator in fieldDeclaration.variableDeclarators().variableDeclarator())
        {
            if (declarator.variableStorageCapacity() is { } capacity)
            {
                ReportStorageCapacityUnsupported(declarator.Identifier().GetText(), "field", capacity);
            }

            var fieldName = declarator.Identifier().GetText();
            var validatedFieldType = ValidateRuntimeValueType(
                fieldType,
                fieldDeclaration.type_(),
                $"field '{fieldName}' in type '{containingTypeName}'");
            if (containingLayout is not null
                && !IsFfiLayoutSafeFieldType(validatedFieldType))
            {
                ReportError(
                    "STK3049",
                    $"Field '{fieldName}' in layout-controlled struct '{containingTypeName}' has type '{validatedFieldType.DisplayName}', which is not FFI-layout safe.",
                    fieldDeclaration.type_());
            }

            AddField(
                fields,
                orderedFields,
                new FieldSymbol(
                    fieldName,
                    validatedFieldType,
                    fieldVisibility,
                    currentModuleName,
                    explicitOffsetBytes,
                    threadSafetyLawAttributes));
        }
    }

    private int? ResolveFieldOffsetBytes(
        string containingTypeName,
        StarkParser.FieldDeclarationContext fieldDeclaration,
        IEnumerable<StarkParser.AttributeListContext> attributeLists,
        StructLayoutMetadata? containingLayout)
    {
        StarkParser.AttributeContext? fieldOffsetAttribute = null;
        foreach (var attributeList in attributeLists)
        {
            foreach (var attribute in attributeList.attribute())
            {
                var attributeName = attribute.qualifiedName().GetText();
                if (!string.Equals(attributeName, "FieldOffset", StringComparison.Ordinal))
                {
                    continue;
                }

                if (fieldOffsetAttribute is not null)
                {
                    ReportError(
                        "STK3048",
                        $"Field in struct '{containingTypeName}' declares [FieldOffset(...)] more than once.",
                        attribute);
                    continue;
                }

                fieldOffsetAttribute = attribute;
            }
        }

        if (containingLayout?.Kind == StructLayoutKind.Explicit)
        {
            if (fieldOffsetAttribute is null)
            {
                ReportError(
                    "STK3048",
                    $"Every field in [StructLayout(Explicit)] struct '{containingTypeName}' must declare [FieldOffset(N)].",
                    fieldDeclaration);
                return null;
            }
        }
        else if (fieldOffsetAttribute is not null)
        {
            ReportError(
                "STK3048",
                $"[FieldOffset(N)] may only be used inside a [StructLayout(Explicit)] struct.",
                fieldOffsetAttribute);
        }

        if (fieldOffsetAttribute is null)
        {
            return null;
        }

        if (fieldDeclaration.variableDeclarators().variableDeclarator().Length != 1)
        {
            ReportError(
                "STK3048",
                "[FieldOffset(N)] applies to exactly one field declarator.",
                fieldDeclaration);
        }

        if (fieldOffsetAttribute.attributeArgument() is not [var argument]
            || !BigInteger.TryParse(argument.GetText(), out var value)
            || value < BigInteger.Zero
            || value > int.MaxValue)
        {
            ReportError(
                "STK3048",
                "[FieldOffset(N)] requires a non-negative integer literal byte offset.",
                fieldOffsetAttribute);
            return null;
        }

        return (int)value;
    }

    private bool IsFfiLayoutSafeFieldType(StarkTypeSymbol type)
    {
        var normalizedType = StarkTypeSymbols.WithQualifiers(
            type,
            borrowKind: StarkBorrowKind.None,
            accessKind: StarkAccessKind.None,
            initializationKind: StarkInitializationKind.None,
            isMutableView: false);
        if (type.BorrowKind != StarkBorrowKind.None
            || type.AccessKind != StarkAccessKind.None
            || type.InitializationKind != StarkInitializationKind.None
            || type.IsMutableView)
        {
            return false;
        }

        return normalizedType.Kind switch
        {
            StarkTypeKind.Bool => true,
            StarkTypeKind.Integer when normalizedType.BitWidth is not null => true,
            StarkTypeKind.Float when normalizedType.BitWidth is not null => true,
            StarkTypeKind.RawPointer or StarkTypeKind.FunctionPointer or StarkTypeKind.Null => true,
            StarkTypeKind.FixedArray when normalizedType.ElementType is not null
                => IsFfiLayoutSafeFieldType(normalizedType.ElementType),
            StarkTypeKind.Named when normalizedType.NamedType is { } namedTypeName
                                     && normalizedType.TypeArguments is not { Count: > 0 }
                                     && _namedTypes.TryGetValue(namedTypeName, out var namedType)
                                     && namedType.Kind == DeclarationKind.Struct
                                     && namedType.Layout?.Kind is StructLayoutKind.C or StructLayoutKind.Explicit
                => true,
            _ => false
        };
    }

    private bool IsMisalignedLayoutFieldProjection(NamedTypeSymbol namedType, string fieldName)
    {
        if (ConcreteTypeLayoutHelper.TryGetConcreteTypeLayout(
                StarkTypeSymbols.Named(namedType.Name),
                _namedTypes,
                enumLayouts: null) is not { } layout
            || !layout.TryGetField(fieldName, out var fieldLayout))
        {
            return false;
        }

        return fieldLayout.IsMisaligned;
    }

    private static void AddField(
        Dictionary<string, FieldSymbol> fields,
        List<FieldSymbol> orderedFields,
        FieldSymbol field)
    {
        fields[field.Name] = field;

        for (var index = 0; index < orderedFields.Count; index++)
        {
            if (string.Equals(orderedFields[index].Name, field.Name, StringComparison.Ordinal))
            {
                orderedFields[index] = field;
                return;
            }
        }

        orderedFields.Add(field);
    }

    private void BuildFunctionSignatures()
    {
        var seenOverloadKeys = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (var module in _loadedModules.Modules.Values)
        {
            if (!module.Reference.IsRoot
                && module.PackageImageFacts is { FunctionSignatures.Count: > 0 } packageImageFacts)
            {
                foreach (var declaration in module.SyntaxModel.Declarations.Where(static declaration => declaration.Kind == DeclarationKind.Function && declaration.Function is not null))
                {
                    if (!IsDeclarationVisible(module, declaration))
                    {
                        continue;
                    }

                    var qualifiedName = FunctionOverloadFacts.QualifyResolvedName(
                        module,
                        FunctionOverloadFacts.GetResolvedLocalName(module.SyntaxModel, declaration));
                    if (!packageImageFacts.FunctionSignatures.TryGetValue(qualifiedName, out var signature))
                    {
                        continue;
                    }

                    RegisterFunctionSignature(signature, seenOverloadKeys, duplicateContext: null);
                }

                continue;
            }

            foreach (var functionSyntax in DeclaredFunctionSyntaxCollector.Collect(module.ParseResult, module.SyntaxModel))
            {
                var localName = functionSyntax.DisplaySourceName;
                if (!FunctionOverloadFacts.TryFindFunctionDeclaration(
                        module.SyntaxModel,
                        localName,
                        FunctionOverloadFacts.BuildOverloadKey(functionSyntax.ParameterList),
                        out var declarationModel))
                {
                    continue;
                }

                if (declarationModel is null || !IsDeclarationVisible(module, declarationModel))
                {
                    continue;
                }

                var genericParameterNames = FunctionGenericParameterFacts.GetEffectiveGenericParameterNames(module, functionSyntax);
                var genericParameters = FunctionGenericParameterFacts.ToGenericParameterSet(genericParameterNames);
                var comptimeGenericParameters = GetComptimeGenericParameters(
                    functionSyntax.TypeParameters,
                    genericParameters,
                    module.SyntaxModel.ModuleName);
                var comptimeGenericParameterMap = ToComptimeGenericParameterMap(comptimeGenericParameters);
                var previousGenericParameters = _currentFunctionGenericParameters;
                var previousComptimeGenericParameters = _currentFunctionComptimeGenericParameters;
                var previousFunctionModuleName = _currentFunctionModuleName;
                _currentFunctionGenericParameters = genericParameters;
                _currentFunctionComptimeGenericParameters = comptimeGenericParameterMap;
                _currentFunctionModuleName = module.SyntaxModel.ModuleName;

                try
                {
                    var returnType = ResolveReturnType(functionSyntax.ReturnType, genericParameters, module.SyntaxModel.ModuleName);
                    ValidateRuntimeValueType(returnType, functionSyntax.ReturnType, $"the return type of function '{localName}'");
                    var isUnsafe = functionSyntax.Modifiers.Any(static modifier => string.Equals(modifier.GetText(), "unsafe", StringComparison.Ordinal));
                    var isFfi = functionSyntax.Modifiers.Any(FfiAbiSyntaxFacts.IsFfiModifier);
                    var isVarargs = functionSyntax.Modifiers.Any(static modifier => string.Equals(modifier.GetText(), "varargs", StringComparison.Ordinal));
                    var isAbiBoundary = isFfi
                        || declarationModel.Visibility == StarkVisibility.Export;
                    var isAsm = declarationModel.Function?.Asm is not null;
                    StarkFfiAbi? ffiAbi = null;
                    if (isFfi)
                    {
                        if (!FfiAbiSyntaxFacts.TryResolveFunctionAbi(
                                functionSyntax.Modifiers,
                                _context.Options.TargetInfo,
                                out var abiResolution,
                                out var abiErrorMessage,
                                out var abiErrorContext))
                        {
                            ReportError("STK3046", abiErrorMessage, abiErrorContext);
                        }
                        else if (isAsm && abiResolution.HasExplicitAbi)
                        {
                            ReportError(
                                "STK3046",
                                $"Asm declaration '{localName}' cannot also specify an FFI ABI. Use bare 'ffi asm(...)' because asm declarations provide register constraints directly.",
                                abiErrorContext);
                        }
                        else if (!isAsm)
                        {
                            ffiAbi = abiResolution.Abi;
                        }
                    }

                    if ((isFfi || isAsm) && !isUnsafe)
                    {
                        ReportError(
                            "STK3024",
                            $"FFI and assembly function '{localName}' must be declared 'unsafe' because callers cross a raw platform or ABI boundary.",
                            functionSyntax.DeclarationContext);
                    }

                    if (isVarargs && isFfi && !FfiAbiSyntaxFacts.AbiSupportsCVarargs(ffiAbi))
                    {
                        ReportError(
                            "STK3046",
                            $"Variadic FFI function '{localName}' requires an ABI that supports C-style varargs on this target.",
                            functionSyntax.DeclarationContext);
                    }

                    if (isFfi && IsTextType(returnType))
                    {
                        ReportError(
                            "STK3008",
                            $"FFI function '{localName}' cannot return text view type '{returnType.DisplayName}'. Return a raw pointer plus explicit length/status from the platform boundary, then construct an explicit Stark text view or owned buffer in wrapper code.",
                            functionSyntax.ReturnType);
                    }

                    if (isAbiBoundary)
                    {
                        ValidateAbiTypeDoesNotDependOnEnum(returnType, functionSyntax.ReturnType, $"the return type of function '{localName}'");
                    }

                    var parameters = new List<TypedParameterSymbol>();
                    foreach (var parameter in functionSyntax.ParameterList.parameter())
                    {
                        var parameterType = ResolveParameterType(parameter.type_(), genericParameters, module.SyntaxModel.ModuleName, out var rawPointerElementCountExpression);
                        ValidateRuntimeValueType(
                            parameterType,
                            parameter.type_(),
                            $"parameter '{parameter.Identifier().GetText()}'",
                            allowDirectInlineClosureParameter: true);
                        if (isAbiBoundary)
                        {
                            ValidateAbiTypeDoesNotDependOnEnum(parameterType, parameter, $"parameter '{parameter.Identifier().GetText()}'");
                        }

                        parameters.Add(CreateTypedParameterSymbol(parameter, parameterType, rawPointerElementCountExpression));
                    }

                    if (!isUnsafe && (ContainsRawPointer(returnType) || parameters.Any(static parameter => ContainsRawPointer(parameter.Type))))
                    {
                        ReportError(
                            "STK3024",
                            $"Function '{localName}' uses raw pointer types and must be declared 'unsafe'. Prefer borrow, slice, dynamic storage, owned handles, or a platform wrapper for safe APIs.",
                            functionSyntax.DeclarationContext);
                    }

                    ValidateParameterContractPrefixes(functionSyntax.ParameterList.parameter());
                    ValidateBoundedRawPointerParameterCounts(functionSyntax.ParameterList.parameter(), parameters);
                    ValidateParameterDisjointContracts(
                        functionSyntax,
                        parameters,
                        allowWholeParameterDisjointContracts: isFfi || isAsm);

                    if (declarationModel.Function?.Asm is not null)
                    {
                        ValidateAsmSignatureSurface(localName, returnType, functionSyntax.ReturnType, parameters, functionSyntax.ParameterList.parameter(), declarationModel.Function.Asm);
                    }

                    var sourceQualifiedName = QualifyName(module, localName);
                    var qualifiedName = QualifyName(module, functionSyntax.Name);
                    var explicitDisjointGroups = declarationModel.Function?.DisjointGroups ?? [];
                    var overlapGroups = declarationModel.Function?.OverlapGroups ?? [];
                    var sameGroups = declarationModel.Function?.SameGroups ?? [];
                    var effectiveDisjointGroups = ParameterMemoryContractFacts.BuildEffectiveDisjointGroups(
                        parameters,
                        explicitDisjointGroups,
                        overlapGroups,
                        sameGroups,
                        applyDefaultNonOverlap: !isFfi && !isAsm);
                    var signature = new TypedFunctionSignature(
                        qualifiedName,
                        returnType,
                        parameters,
                        SourceName: sourceQualifiedName,
                        GenericParameterNames: genericParameterNames.Count == 0 ? null : genericParameterNames.ToArray(),
                        ComptimeGenericParameterNames: comptimeGenericParameters.Count == 0 ? null : comptimeGenericParameters.ToArray(),
                        IsStatic: functionSyntax.IsStatic,
                        Kind: functionSyntax.DeclaredKind,
                        IsUnsafe: isUnsafe,
                        IsVarargs: isVarargs,
                        FfiAbi: ffiAbi,
                        BackendOptimizationMode: declarationModel.Function?.BackendOptimizationMode ?? ModuleBackendOptimizationMode.Default,
                        DisjointParameterGroups: effectiveDisjointGroups,
                        OverlapParameterGroups: overlapGroups,
                        SameParameterGroups: sameGroups,
                        TypeParameterConstraints: ParseTypeParameterConstraints(functionSyntax, genericParameters, module.SyntaxModel.ModuleName),
                        HasBody: functionSyntax.HasBody,
                        ThreadSafetyLawPredicates: ParseThreadSafetyLawPredicates(
                            functionSyntax,
                            genericParameters,
                            module.SyntaxModel.ModuleName,
                            comptimeGenericParameterMap),
                        Visibility: declarationModel.Visibility,
                        ValueParameterContracts: declarationModel.Function?.ValueContracts is { Count: > 0 } valueContracts
                            ? valueContracts
                            : null);
                    RegisterFunctionSignature(signature, seenOverloadKeys, functionSyntax.DeclarationContext);
                    _functionSyntaxByQualifiedName[signature.Name] = functionSyntax;
                }
                finally
                {
                    _currentFunctionGenericParameters = previousGenericParameters;
                    _currentFunctionComptimeGenericParameters = previousComptimeGenericParameters;
                    _currentFunctionModuleName = previousFunctionModuleName;
                }
            }
        }
    }

    private void RegisterFunctionSignature(
        TypedFunctionSignature signature,
        Dictionary<string, HashSet<string>> seenOverloadKeys,
        ParserRuleContext? duplicateContext)
    {
        var sourceQualifiedName = signature.DisplaySourceName;
        var overloadKey = FunctionOverloadFacts.BuildOverloadKey(signature.Parameters.Select(static parameter => parameter.Type.DisplayName));
        if (!seenOverloadKeys.TryGetValue(sourceQualifiedName, out var overloads))
        {
            overloads = new HashSet<string>(StringComparer.Ordinal);
            seenOverloadKeys[sourceQualifiedName] = overloads;
        }

        if (!overloads.Add(overloadKey))
        {
            if (duplicateContext is not null)
            {
                ReportError(
                    "STK3006",
                    $"Function '{sourceQualifiedName}' declares overload '{sourceQualifiedName}{overloadKey}' more than once.",
                    duplicateContext);
            }

            return;
        }

        _functions[signature.Name] = signature;
        if (!_functionOverloads.TryGetValue(sourceQualifiedName, out var collectedOverloads))
        {
            collectedOverloads = [];
            _functionOverloads[sourceQualifiedName] = collectedOverloads;
        }

        collectedOverloads.Add(signature);
    }

    private void BuildConstructorShapes()
    {
        foreach (var module in _loadedModules.Modules.Values)
        {
            if (!module.Reference.IsRoot
                && module.PackageImageFacts is { Constructors.Count: > 0 } packageImageFacts)
            {
                var constructorBodyKeys = BuildImportedConstructorBodyKeyLookup(module);
                foreach (var declaration in module.SyntaxModel.Declarations)
                {
                    if (declaration.Kind is not (DeclarationKind.Struct or DeclarationKind.Record))
                    {
                        continue;
                    }

                    if (!IsDeclarationVisible(module, declaration))
                    {
                        continue;
                    }

                    var qualifiedName = QualifyName(module, declaration.Name);
                    if (packageImageFacts.Constructors.TryGetValue(qualifiedName, out var constructors))
                    {
                        _constructors[qualifiedName] = constructors
                            .Select(constructor => new ConstructorShape(
                                constructor.TypeName,
                                constructor.Parameters.ToArray(),
                                constructor.IsPrimaryShape,
                                constructor.BodyKey ?? TryResolveImportedConstructorBodyKey(
                                    constructorBodyKeys,
                                    qualifiedName,
                                    constructor.Parameters)))
                            .ToList();
                    }
                }

                continue;
            }

            foreach (var declaration in module.ParseResult.Root.topLevelDeclaration())
            {
                if (declaration.structDeclaration() is { } structDeclaration)
                {
                    var declarationModel = module.SyntaxModel.Declarations.FirstOrDefault(
                        candidate => candidate.Kind == DeclarationKind.Struct && string.Equals(candidate.Name, structDeclaration.Identifier().GetText(), StringComparison.Ordinal));
                    if (declarationModel is null || !IsDeclarationVisible(module, declarationModel))
                    {
                        continue;
                    }

                    var typeName = QualifyName(module, structDeclaration.Identifier().GetText());
                    var genericParameters = GetGenericParameterNames(structDeclaration.typeParameterList());
                    RegisterConstructors(
                        typeName,
                        structDeclaration.Identifier().GetText(),
                        genericParameters,
                        structDeclaration.structBody().structMember()
                            .Select(static member => member.constructorDeclaration())
                            .Where(static constructor => constructor is not null)!,
                        primaryConstructorParameters: null,
                        module.SyntaxModel.ModuleName);
                    continue;
                }

                if (declaration.recordDeclaration() is { } recordDeclaration)
                {
                    var declarationModel = module.SyntaxModel.Declarations.FirstOrDefault(
                        candidate => candidate.Kind == DeclarationKind.Record && string.Equals(candidate.Name, recordDeclaration.Identifier().GetText(), StringComparison.Ordinal));
                    if (declarationModel is null || !IsDeclarationVisible(module, declarationModel))
                    {
                        continue;
                    }

                    var typeName = QualifyName(module, recordDeclaration.Identifier().GetText());
                    var genericParameters = GetGenericParameterNames(recordDeclaration.typeParameterList());
                    RegisterConstructors(
                        typeName,
                        recordDeclaration.Identifier().GetText(),
                        genericParameters,
                        recordDeclaration.recordBody().recordMember()
                            .Select(static member => member.constructorDeclaration())
                            .Where(static constructor => constructor is not null)!,
                        recordDeclaration.primaryConstructorParameters(),
                        module.SyntaxModel.ModuleName);
                }
            }
        }

        PopulateConcreteConstructorShapesForKnownGenericInstantiations();
    }

    private List<ImportedConstructorBodyKey> BuildImportedConstructorBodyKeyLookup(LoadedModuleDocument module)
    {
        var constructorBodyKeys = new List<ImportedConstructorBodyKey>();

        foreach (var declaration in module.ParseResult.Root.topLevelDeclaration())
        {
            if (declaration.structDeclaration() is { } structDeclaration)
            {
                var declarationModel = module.SyntaxModel.Declarations.FirstOrDefault(
                    candidate => candidate.Kind == DeclarationKind.Struct && string.Equals(candidate.Name, structDeclaration.Identifier().GetText(), StringComparison.Ordinal));
                if (declarationModel is null || !IsDeclarationVisible(module, declarationModel))
                {
                    continue;
                }

                AddImportedConstructorBodyKeys(
                    constructorBodyKeys,
                    module,
                    QualifyName(module, structDeclaration.Identifier().GetText()),
                    structDeclaration.Identifier().GetText(),
                    GetGenericParameterNames(structDeclaration.typeParameterList()),
                    structDeclaration.structBody().structMember()
                        .Select(static member => member.constructorDeclaration())
                        .Where(static constructor => constructor is not null)!);
                continue;
            }

            if (declaration.recordDeclaration() is { } recordDeclaration)
            {
                var declarationModel = module.SyntaxModel.Declarations.FirstOrDefault(
                    candidate => candidate.Kind == DeclarationKind.Record && string.Equals(candidate.Name, recordDeclaration.Identifier().GetText(), StringComparison.Ordinal));
                if (declarationModel is null || !IsDeclarationVisible(module, declarationModel))
                {
                    continue;
                }

                AddImportedConstructorBodyKeys(
                    constructorBodyKeys,
                    module,
                    QualifyName(module, recordDeclaration.Identifier().GetText()),
                    recordDeclaration.Identifier().GetText(),
                    GetGenericParameterNames(recordDeclaration.typeParameterList()),
                    recordDeclaration.recordBody().recordMember()
                        .Select(static member => member.constructorDeclaration())
                        .Where(static constructor => constructor is not null)!);
            }
        }

        return constructorBodyKeys;
    }

    private void AddImportedConstructorBodyKeys(
        List<ImportedConstructorBodyKey> constructorBodyKeys,
        LoadedModuleDocument module,
        string qualifiedTypeName,
        string localTypeName,
        ISet<string>? genericParameters,
        IEnumerable<StarkParser.ConstructorDeclarationContext> constructors)
    {
        foreach (var constructor in constructors)
        {
            if (!string.Equals(constructor.Identifier().GetText(), localTypeName, StringComparison.Ordinal))
            {
                continue;
            }

            var parameters = BuildTypedParameters(constructor.parameterList().parameter(), genericParameters, module.SyntaxModel.ModuleName);
            constructorBodyKeys.Add(new ImportedConstructorBodyKey(
                qualifiedTypeName,
                parameters,
                BuildConstructorSignatureKey(qualifiedTypeName, parameters),
                BuildConstructorBodyKey(qualifiedTypeName, constructor)));
        }
    }

    private static string? TryResolveImportedConstructorBodyKey(
        IReadOnlyList<ImportedConstructorBodyKey> bodyKeys,
        string qualifiedTypeName,
        IReadOnlyList<TypedParameterSymbol> parameters)
    {
        var signatureKey = BuildConstructorSignatureKey(qualifiedTypeName, parameters);
        foreach (var bodyKey in bodyKeys)
        {
            if (string.Equals(bodyKey.SignatureKey, signatureKey, StringComparison.Ordinal))
            {
                return bodyKey.BodyKey;
            }
        }

        ImportedConstructorBodyKey? match = null;
        foreach (var bodyKey in bodyKeys)
        {
            if (!string.Equals(bodyKey.QualifiedTypeName, qualifiedTypeName, StringComparison.Ordinal)
                || bodyKey.Parameters.Count != parameters.Count
                || !ConstructorParameterTypesAreEquivalent(bodyKey.Parameters, parameters))
            {
                continue;
            }

            if (match is not null)
            {
                return null;
            }

            match = bodyKey;
        }

        return match?.BodyKey;
    }

    private static bool ConstructorParameterTypesAreEquivalent(
        IReadOnlyList<TypedParameterSymbol> left,
        IReadOnlyList<TypedParameterSymbol> right)
    {
        for (var index = 0; index < left.Count; index++)
        {
            var leftType = left[index].Type;
            var rightType = right[index].Type;
            if (!TypeCompatibilityFacts.CanAssign(leftType, rightType)
                || !TypeCompatibilityFacts.CanAssign(rightType, leftType))
            {
                return false;
            }
        }

        return true;
    }

    private static string BuildConstructorSignatureKey(
        string qualifiedTypeName,
        IReadOnlyList<TypedParameterSymbol> parameters)
    {
        return $"{qualifiedTypeName}{FunctionOverloadFacts.BuildOverloadKey(parameters.Select(static parameter => parameter.Type.DisplayName))}";
    }

    private void RegisterConstructors(
        string qualifiedTypeName,
        string localTypeName,
        ISet<string>? genericParameters,
        IEnumerable<StarkParser.ConstructorDeclarationContext> constructorDeclarations,
        StarkParser.PrimaryConstructorParametersContext? primaryConstructorParameters,
        string currentModuleName)
    {
        var constructors = new List<ConstructorShape>();

        if (primaryConstructorParameters is not null)
        {
            constructors.Add(new ConstructorShape(
                localTypeName,
                BuildTypedParameters(primaryConstructorParameters.parameterList().parameter(), genericParameters, currentModuleName),
                IsPrimaryShape: true,
                BodyKey: null));
        }

        foreach (var constructor in constructorDeclarations)
        {
            if (!string.Equals(constructor.Identifier().GetText(), localTypeName, StringComparison.Ordinal))
            {
                continue;
            }

            constructors.Add(new ConstructorShape(
                localTypeName,
                BuildTypedParameters(constructor.parameterList().parameter(), genericParameters, currentModuleName),
                IsPrimaryShape: false,
                BuildConstructorBodyKey(qualifiedTypeName, constructor)));
        }

        if (constructors.Count != 0)
        {
            _constructors[qualifiedTypeName] = constructors;
        }
    }

    private TypedParameterSymbol[] BuildTypedParameters(
        IEnumerable<StarkParser.ParameterContext> parameters,
        ISet<string>? genericParameters,
        string currentModuleName)
    {
        return parameters
            .Select(parameter =>
            {
                var parameterType = ResolveParameterType(parameter.type_(), genericParameters, currentModuleName, out var rawPointerElementCountExpression);
                return CreateTypedParameterSymbol(parameter, parameterType, rawPointerElementCountExpression);
            })
            .ToArray();
    }

    private void ValidateBoundedRawPointerParameterCounts(
        IReadOnlyList<StarkParser.ParameterContext> parameterSyntaxes,
        IReadOnlyList<TypedParameterSymbol> parameters)
    {
        var parameterSymbols = parameters.ToDictionary(static parameter => parameter.Name, StringComparer.Ordinal);
        foreach (var parameterSyntax in parameterSyntaxes)
        {
            var name = parameterSyntax.Identifier().GetText();
            if (!parameterSymbols.TryGetValue(name, out var parameter)
                || parameter.RawPointerElementCountExpression is null
                || !TryGetBoundedRawPointerElementCountExpression(parameterSyntax.type_(), out var countExpression))
            {
                continue;
            }

            ValidateBoundedRawPointerCountExpression(name, countExpression, parameterSymbols);
        }
    }

    private bool ValidateBoundedRawPointerCountExpression(
        string parameterName,
        StarkParser.ExpressionContext expression,
        IReadOnlyDictionary<string, TypedParameterSymbol> parameterSymbols)
    {
        if (TryGetSimpleParameterExpression(expression, out var boundName))
        {
            if (!parameterSymbols.TryGetValue(boundName, out var boundParameter))
            {
                ReportError(
                    "STK3014",
                    $"Bounded raw pointer parameter '{parameterName}' references unknown count parameter '{boundName}'.",
                    expression);
                return false;
            }

            if (boundParameter.Type.Kind != StarkTypeKind.Integer)
            {
                ReportError(
                    "STK3014",
                    $"Bounded raw pointer parameter '{parameterName}' count '{boundName}' must be an integer parameter, but found '{boundParameter.Type.DisplayName}'.",
                    expression);
                return false;
            }

            if (!IsProvablyNonNegativeIntegerType(boundParameter.Type))
            {
                ReportError(
                    "STK3014",
                    $"Bounded raw pointer parameter '{parameterName}' count '{boundName}' must be provably non-negative.",
                    expression);
                return false;
            }

            return true;
        }

        if (CompileTimeExpressionEvaluator.TryEvaluateInteger(
                expression,
                out var constant,
                CreateCompileTimeEvaluationServices(Scope.CreateRoot(_globals))))
        {
            if (constant >= BigInteger.Zero)
            {
                return true;
            }

            ReportError(
                "STK3014",
                $"Bounded raw pointer parameter '{parameterName}' count '{expression.GetText()}' must be non-negative.",
                expression);
            return false;
        }

        ReportError(
            "STK3014",
            $"Bounded raw pointer parameter '{parameterName}' count must be a non-negative integer parameter or compile-time integer constant.",
            expression);
        return false;
    }

    private static bool TryGetBoundedRawPointerElementCountExpression(
        StarkParser.Type_Context type,
        out StarkParser.ExpressionContext countExpression)
    {
        countExpression = null!;
        if (type.nonArrayType().rawPointerType() is null
            || type.arraySuffix() is not [var suffix]
            || suffix.expression() is not { } expression)
        {
            return false;
        }

        countExpression = expression;
        return true;
    }

    private static TypedParameterSymbol CreateTypedParameterSymbol(
        StarkParser.ParameterContext parameter,
        StarkTypeSymbol parameterType,
        string? rawPointerElementCountExpression)
    {
        return new TypedParameterSymbol(
            parameter.Identifier().GetText(),
            parameterType,
            IsDisjoint: ParameterHasPrefix(parameter, StarkParser.DISJOINT),
            IsConst: ParameterHasPrefix(parameter, StarkParser.CONST),
            RawPointerElementCountExpression: rawPointerElementCountExpression);
    }

    private static VariableSymbol CreateParameterVariableSymbol(TypedParameterSymbol parameter)
    {
        return new VariableSymbol(
            parameter.Name,
            parameter.Type,
            IsMutable: false,
            IsConstant: false,
            UsesFrozenProjectionSemantics: parameter.IsConst,
            HasConstProvenance: parameter.IsConst,
            RawPointerElementCountExpression: parameter.RawPointerElementCountExpression);
    }

    private static bool ParameterHasPrefix(StarkParser.ParameterContext parameter, int tokenType)
    {
        return parameter.parameterContractPrefix()
            .Any(prefix => prefix.Start.Type == tokenType);
    }

    private void ValidateParameterContractPrefixes(IReadOnlyList<StarkParser.ParameterContext> parameters)
    {
        foreach (var parameter in parameters)
        {
            var disjointPrefixes = parameter.parameterContractPrefix()
                .Where(static prefix => prefix.Start.Type == StarkParser.DISJOINT)
                .ToArray();
            if (disjointPrefixes.Length > 1)
            {
                ReportError(
                    "STK3028",
                    $"Parameter '{parameter.Identifier().GetText()}' may specify 'disjoint' at most once.",
                    disjointPrefixes[1]);
            }

            var constPrefixes = parameter.parameterContractPrefix()
                .Where(static prefix => prefix.Start.Type == StarkParser.CONST)
                .ToArray();
            if (constPrefixes.Length > 1)
            {
                ReportError(
                    "STK3028",
                    $"Parameter '{parameter.Identifier().GetText()}' may specify 'const' at most once.",
                    constPrefixes[1]);
            }
        }
    }

    private void ValidateParameterDisjointContracts(
        DeclaredFunctionSyntax functionSyntax,
        IReadOnlyList<TypedParameterSymbol> parameters,
        bool allowWholeParameterDisjointContracts)
    {
        var parameterSymbols = new Dictionary<string, TypedParameterSymbol>(StringComparer.Ordinal);
        foreach (var parameter in parameters)
        {
            parameterSymbols.TryAdd(parameter.Name, parameter);
        }

        foreach (var parameter in functionSyntax.ParameterList.parameter())
        {
            var name = parameter.Identifier().GetText();
            if (ParameterHasPrefix(parameter, StarkParser.DISJOINT)
                && parameterSymbols.TryGetValue(name, out var symbol)
                && !CanRuntimeDisjointTest(symbol.Type))
            {
                ReportError(
                    "STK3028",
                    $"Parameter '{name}' may specify 'disjoint' only for memory-backed types such as slices, text views, borrows, initialization views, or raw pointers, but found '{symbol.Type.DisplayName}'.",
                    parameter);
            }
            else if (ParameterHasPrefix(parameter, StarkParser.DISJOINT)
                     && !allowWholeParameterDisjointContracts)
            {
                ReportError(
                    "STK3028",
                    $"Parameter '{name}' no longer needs the whole-parameter 'disjoint' qualifier because Stark memory-backed parameters are non-overlapping by default. Remove 'disjoint'; use 'where overlap({name}, other)' for intentional overlap, 'where same({name}, other)' for identical storage, or 'where disjoint({name}[start, count], other[start, count])' for subregions.",
                    parameter);
            }
        }

        foreach (var clause in GetParameterMemoryContractClauses(functionSyntax.DeclarationContext))
        {
            foreach (var contract in clause.parameterMemoryContract())
            {
                var relationName = GetParameterMemoryContractName(contract);
                var expressionList = GetParameterMemoryContractExpressionList(contract);
                if (expressionList is null)
                {
                    continue;
                }

                var operands = expressionList.expression();
                if (operands.Length < 2)
                {
                    ReportError(
                        "STK3029",
                        $"'where {relationName}(...)' contracts require at least two parameter or region operands.",
                        contract);
                }

                var seen = new HashSet<string>(StringComparer.Ordinal);
                var names = new List<string>();
                var wholeParameterNames = new List<string>();
                var allOperandsAreWholeParameters = true;
                foreach (var operand in operands)
                {
                    if (!TryGetDisjointContractRootName(operand, out var name, out var regionStart, out var regionLength))
                    {
                        ReportError(
                            "STK3029",
                            $"{Capitalize(relationName)} contract operands must be parameter names or raw pointer regions of the form 'parameter[start, count]'.",
                            operand);
                        continue;
                    }

                    var operandKey = regionStart is null
                        ? $"whole:{name}"
                        : $"region:{name}[{NormalizeExpressionText(regionStart.GetText())},{NormalizeExpressionText(regionLength!.GetText())}]";

                    if (regionStart is null)
                    {
                        wholeParameterNames.Add(name);
                    }
                    else
                    {
                        allOperandsAreWholeParameters = false;
                    }

                    if (!parameterSymbols.TryGetValue(name, out var symbol))
                    {
                        ReportError(
                            "STK3029",
                            $"{Capitalize(relationName)} contract references unknown parameter '{name}'.",
                            operand);
                    }
                    else if (!CanRuntimeDisjointTest(symbol.Type))
                    {
                        ReportError(
                            "STK3029",
                            $"{Capitalize(relationName)} contract references parameter '{name}' with non-memory-backed type '{symbol.Type.DisplayName}'. Memory contracts require memory-backed parameters such as slices, text views, borrows, initialization views, or raw pointers.",
                            operand);
                    }
                    else if (regionStart is not null
                             && !ValidateRawPointerRegionContractOperand(name, symbol, regionStart, regionLength!, parameterSymbols, operand))
                    {
                        continue;
                    }
                    else if (!seen.Add(operandKey))
                    {
                        ReportError(
                            "STK3029",
                            regionStart is null
                                ? $"{Capitalize(relationName)} contract repeats parameter '{name}'."
                                : $"{Capitalize(relationName)} contract repeats region '{operand.GetText()}'.",
                            operand);
                    }
                    else
                    {
                        names.Add(name);
                    }
                }

                if (names.Count > 1)
                {
                    ValidateMemoryContractPairConflicts(
                        relationName,
                        allOperandsAreWholeParameters ? wholeParameterNames : [],
                        functionSyntax.DeclarationContext);
                }

                if (!allowWholeParameterDisjointContracts
                    && string.Equals(relationName, "disjoint", StringComparison.Ordinal)
                    && allOperandsAreWholeParameters
                    && wholeParameterNames.Distinct(StringComparer.Ordinal).Count() >= 2)
                {
                    ReportError(
                        "STK3029",
                        $"Whole-parameter 'where disjoint({string.Join(", ", wholeParameterNames)})' is redundant because Stark memory-backed parameters are non-overlapping by default. Remove the clause; use 'where overlap(...)' for intentional overlap, 'where same(...)' for identical storage, or keep 'where disjoint(parameter[start, count], other[start, count])' for subregions.",
                        contract);
                }
            }
        }
    }

    private static string GetParameterMemoryContractName(StarkParser.ParameterMemoryContractContext contract)
    {
        if (contract.disjointContract() is not null)
        {
            return "disjoint";
        }

        if (contract.overlapContract() is not null)
        {
            return "overlap";
        }

        if (contract.sameContract() is not null)
        {
            return "same";
        }

        return "memory";
    }

    private static StarkParser.ExpressionListContext? GetParameterMemoryContractExpressionList(
        StarkParser.ParameterMemoryContractContext contract)
    {
        return contract.disjointContract()?.expressionList()
            ?? contract.overlapContract()?.expressionList()
            ?? contract.sameContract()?.expressionList();
    }

    private void ValidateMemoryContractPairConflicts(
        string currentRelationName,
        IReadOnlyList<string> currentNames,
        ParserRuleContext context)
    {
        if (currentNames.Count < 2)
        {
            return;
        }

        // Conflicting same/overlap/disjoint clauses are easiest to diagnose here while
        // the source declaration is still in front of us. Only whole-parameter
        // relations participate here; subregion disjointness can refine an overlap-safe
        // API without contradicting its whole-parameter overlap contract.
        var currentPairs = EnumerateNamePairs(currentNames).ToArray();
        var relationPairs = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
        {
            ["disjoint"] = [],
            ["overlap"] = [],
            ["same"] = []
        };

        foreach (var clause in GetParameterMemoryContractClauses(context))
        {
            foreach (var contract in clause.parameterMemoryContract())
            {
                var relationName = GetParameterMemoryContractName(contract);
                var expressionList = GetParameterMemoryContractExpressionList(contract);
                if (expressionList is null)
                {
                    continue;
                }

                var names = expressionList.expression()
                    .Select(static expression => TryGetWholeParameterMemoryContractName(expression, out var name)
                        ? name
                        : null)
                    .Where(static name => !string.IsNullOrWhiteSpace(name))
                    .Select(static name => name!)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                foreach (var pairKey in EnumerateNamePairs(names))
                {
                    relationPairs[relationName].Add(pairKey);
                }
            }
        }

        foreach (var pairKey in currentPairs)
        {
            if (string.Equals(currentRelationName, "disjoint", StringComparison.Ordinal)
                && (relationPairs["overlap"].Contains(pairKey) || relationPairs["same"].Contains(pairKey)))
            {
                ReportError(
                    "STK3029",
                    $"Memory contract for parameters '{pairKey.Replace("|", "' and '", StringComparison.Ordinal)}' cannot be both disjoint and overlapping/same-memory.",
                    context);
            }
            else if (string.Equals(currentRelationName, "overlap", StringComparison.Ordinal)
                     && relationPairs["same"].Contains(pairKey))
            {
                ReportError(
                    "STK3029",
                    $"Memory contract for parameters '{pairKey.Replace("|", "' and '", StringComparison.Ordinal)}' cannot be both overlap and same-memory. Use 'same' when identical storage is required.",
                    context);
            }
        }
    }

    private static string Capitalize(string text)
    {
        return string.IsNullOrEmpty(text)
            ? text
            : char.ToUpperInvariant(text[0]) + text[1..];
    }

    private static IReadOnlyList<StarkParser.ParameterMemoryContractClauseContext> GetParameterMemoryContractClauses(
        ParserRuleContext declaration)
    {
        return declaration switch
        {
            StarkParser.FunctionDeclarationContext functionDeclaration => functionDeclaration.parameterMemoryContractClause(),
            StarkParser.MethodDeclarationContext methodDeclaration => methodDeclaration.parameterMemoryContractClause(),
            StarkParser.TraitMethodDeclarationContext traitMethodDeclaration => traitMethodDeclaration.parameterMemoryContractClause(),
            StarkParser.DoctrineMethodDeclarationContext doctrineMethodDeclaration => doctrineMethodDeclaration.parameterMemoryContractClause(),
            _ => []
        };
    }

    private static IEnumerable<string> EnumerateNamePairs(IReadOnlyList<string> names)
    {
        var distinctNames = names
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        for (var leftIndex = 0; leftIndex < distinctNames.Length; leftIndex++)
        {
            for (var rightIndex = leftIndex + 1; rightIndex < distinctNames.Length; rightIndex++)
            {
                yield return BuildNamePairKey(distinctNames[leftIndex], distinctNames[rightIndex]);
            }
        }
    }

    private static string BuildNamePairKey(string left, string right)
    {
        return string.CompareOrdinal(left, right) <= 0
            ? $"{left}|{right}"
            : $"{right}|{left}";
    }

    private static bool TryGetDisjointContractRootName(
        StarkParser.ExpressionContext expression,
        out string rootName,
        out StarkParser.ExpressionContext? regionStart,
        out StarkParser.ExpressionContext? regionLength)
    {
        rootName = string.Empty;
        regionStart = null;
        regionLength = null;

        if (TryGetSimpleParameterExpression(expression, out rootName))
        {
            return true;
        }

        if (!TryGetRawPointerRegionExpression(expression, out rootName, out regionStart, out regionLength))
        {
            return false;
        }

        return true;
    }

    private static bool TryGetWholeParameterMemoryContractName(
        StarkParser.ExpressionContext expression,
        out string rootName)
    {
        rootName = string.Empty;
        if (!TryGetDisjointContractRootName(expression, out var name, out var regionStart, out _)
            || regionStart is not null)
        {
            return false;
        }

        rootName = name;
        return true;
    }

    private bool ValidateRawPointerRegionContractOperand(
        string rootName,
        TypedParameterSymbol symbol,
        StarkParser.ExpressionContext regionStart,
        StarkParser.ExpressionContext regionLength,
        IReadOnlyDictionary<string, TypedParameterSymbol> parameterSymbols,
        ParserRuleContext diagnosticContext)
    {
        if (symbol.Type.Kind != StarkTypeKind.RawPointer)
        {
            ReportError(
                "STK3029",
                $"Disjoint contract region '{diagnosticContext.GetText()}' requires raw pointer parameter '{rootName}', but found '{symbol.Type.DisplayName}'.",
                diagnosticContext);
            return false;
        }

        var validStart = ValidateDisjointRegionIndexContractExpression(regionStart, parameterSymbols);
        var validLength = ValidateDisjointRegionIndexContractExpression(regionLength, parameterSymbols);
        return validStart && validLength;
    }

    private bool ValidateDisjointRegionIndexContractExpression(
        StarkParser.ExpressionContext expression,
        IReadOnlyDictionary<string, TypedParameterSymbol> parameterSymbols)
    {
        if (TryGetSimpleParameterExpression(expression, out var name)
            && parameterSymbols.TryGetValue(name, out var parameter)
            && parameter.Type.Kind == StarkTypeKind.Integer)
        {
            if (IsProvablyNonNegativeIntegerType(parameter.Type))
            {
                return true;
            }

            ReportError(
                "STK3029",
                $"Disjoint raw pointer region bound '{expression.GetText()}' must be provably non-negative.",
                expression);
            return false;
        }

        if (CompileTimeExpressionEvaluator.TryEvaluateInteger(
                expression,
                out var literal,
                CreateCompileTimeEvaluationServices(Scope.CreateRoot(_globals)))
            && literal >= BigInteger.Zero)
        {
            return true;
        }

        ReportError(
            "STK3029",
            $"Disjoint raw pointer region bound '{expression.GetText()}' must be a non-negative integer parameter or compile-time integer constant.",
            expression);
        return false;
    }

    private bool TryCheckRawPointerRegionDisjointOperand(
        StarkParser.ExpressionContext expression,
        Scope scope,
        out string? rootKey,
        out bool matchedRegion)
    {
        rootKey = null;
        matchedRegion = false;

        if (!TryGetRawPointerRegionExpression(expression, out var rootName, out var startExpression, out var lengthExpression))
        {
            return false;
        }

        matchedRegion = true;
        if (!scope.TryLookup(rootName, out var symbol))
        {
            ReportError("STK3025", $"Runtime disjoint region references unknown value '{rootName}'.", expression);
            return false;
        }

        if (symbol.Type.Kind != StarkTypeKind.RawPointer)
        {
            ReportError(
                "STK3025",
                $"Runtime disjoint region '{expression.GetText()}' requires a raw pointer root, but found '{symbol.Type.DisplayName}'.",
                expression);
            return false;
        }

        var startType = EvaluateExpression(startExpression, scope, allowFunctionReference: false).Type;
        var lengthType = EvaluateExpression(lengthExpression, scope, allowFunctionReference: false).Type;
        var valid = true;
        if (startType.Kind != StarkTypeKind.Integer)
        {
            valid = false;
            ReportError(
                "STK3025",
                $"Runtime disjoint raw pointer region start must be an integer, but found '{startType.DisplayName}'.",
                startExpression);
        }

        if (lengthType.Kind != StarkTypeKind.Integer)
        {
            valid = false;
            ReportError(
                "STK3025",
                $"Runtime disjoint raw pointer region length must be an integer, but found '{lengthType.DisplayName}'.",
                lengthExpression);
        }

        if (startType.Kind == StarkTypeKind.Integer
            && !IsProvablyNonNegativeIntegerType(startType))
        {
            valid = false;
            ReportError(
                "STK3025",
                "Runtime disjoint raw pointer region start must be provably non-negative.",
                startExpression);
        }

        if (lengthType.Kind == StarkTypeKind.Integer
            && !IsProvablyNonNegativeIntegerType(lengthType))
        {
            valid = false;
            ReportError(
                "STK3025",
                "Runtime disjoint raw pointer region length must be provably non-negative.",
                lengthExpression);
        }

        if (!valid)
        {
            return false;
        }

        if (TryGetRawPointerRegionExpressionList(expression) is { } expressionList)
        {
            RecordIndexAccess("raw-pointer-region", symbol.Type, symbol.Type, 2, expressionList);
        }

        var baseRootKey = symbol.MemoryRootKey ?? rootName;
        rootKey = AppendMemoryRootTextRangeKey(baseRootKey, startExpression, lengthExpression, scope) ?? baseRootKey;
        return true;
    }

    private void CheckConstructorBodies()
    {
        foreach (var module in _loadedModules.Modules.Values)
        {
            if (module.IsPackageImageImport)
            {
                continue;
            }

            foreach (var declaration in module.ParseResult.Root.topLevelDeclaration())
            {
                if (declaration.structDeclaration() is { } structDeclaration)
                {
                    CheckStructLikeConstructorBodies(
                        module,
                        DeclarationKind.Struct,
                        structDeclaration.Identifier().GetText(),
                        structDeclaration.typeParameterList(),
                        structDeclaration.structBody().structMember()
                            .Select(static member => member.constructorDeclaration())
                            .Where(static constructor => constructor is not null)!);
                    continue;
                }

                if (declaration.recordDeclaration() is { } recordDeclaration)
                {
                    CheckStructLikeConstructorBodies(
                        module,
                        DeclarationKind.Record,
                        recordDeclaration.Identifier().GetText(),
                        recordDeclaration.typeParameterList(),
                        recordDeclaration.recordBody().recordMember()
                            .Select(static member => member.constructorDeclaration())
                            .Where(static constructor => constructor is not null)!);
                }
            }
        }
    }

    private void CheckStructLikeConstructorBodies(
        LoadedModuleDocument module,
        DeclarationKind declarationKind,
        string localTypeName,
        StarkParser.TypeParameterListContext? typeParameterList,
        IEnumerable<StarkParser.ConstructorDeclarationContext> constructors)
    {
        var declarationModel = module.SyntaxModel.Declarations.FirstOrDefault(
            candidate => candidate.Kind == declarationKind && string.Equals(candidate.Name, localTypeName, StringComparison.Ordinal));
        if (declarationModel is null || !IsDeclarationVisible(module, declarationModel))
        {
            return;
        }

        var qualifiedTypeName = QualifyName(module, localTypeName);
        var genericParameters = GetGenericParameterNames(typeParameterList);
        var selfType = StarkTypeSymbols.Named(qualifiedTypeName);
        // Constructor field-read validation needs the struct's field set. Records
        // initialize their primary-constructor fields before any explicit body, so
        // the assigned-so-far analysis below is scoped to structs for now.
        var selfFieldOwner = declarationKind == DeclarationKind.Struct
            && _namedTypes.TryGetValue(qualifiedTypeName, out var resolvedSelfType)
                ? resolvedSelfType
                : null;

        foreach (var constructor in constructors)
        {
            if (!string.Equals(constructor.Identifier().GetText(), localTypeName, StringComparison.Ordinal))
            {
                continue;
            }

            var scope = Scope.CreateRoot(_globals);
            scope.Declare(new VariableSymbol("self", selfType, IsMutable: true, IsConstant: false));

            var parameters = BuildTypedParameters(constructor.parameterList().parameter(), genericParameters, module.SyntaxModel.ModuleName);
            foreach (var parameter in parameters)
            {
                if (string.Equals(parameter.Name, "self", StringComparison.Ordinal))
                {
                    ReportError("STK3006", "Constructor parameters cannot be named 'self'.", constructor);
                    continue;
                }

                scope.Declare(CreateParameterVariableSymbol(parameter));
            }

            var previousGenericParameters = _currentFunctionGenericParameters;
            var previousFunctionName = _currentFunctionName;
            var previousFunctionModuleName = _currentFunctionModuleName;
            var previousInsideConstructorBody = _insideConstructorBody;

            _currentFunctionGenericParameters = genericParameters;
            _currentFunctionName = null;
            _currentFunctionModuleName = module.SyntaxModel.ModuleName;
            _insideConstructorBody = true;

            try
            {
                CheckBlock(constructor.block(), scope, StarkTypeSymbols.Void);
                if (selfFieldOwner is not null)
                {
                    ValidateConstructorFieldReads(constructor.block(), selfFieldOwner);
                }
            }
            finally
            {
                _currentFunctionGenericParameters = previousGenericParameters;
                _currentFunctionName = previousFunctionName;
                _currentFunctionModuleName = previousFunctionModuleName;
                _insideConstructorBody = previousInsideConstructorBody;
            }
        }
    }

    // A `self` field holds its pre-construction zero state until the body assigns
    // it, so reading a field before assignment yields that zero state rather than
    // the intended value. Walk the body in evaluation order tracking the fields
    // assigned so far; a field is "assigned" once any path assigns it (monotonic
    // union — conservative, so valid code is never rejected), and reading a field
    // not yet in that set reports STK3055.
    private void ValidateConstructorFieldReads(StarkParser.BlockContext block, NamedTypeSymbol selfType)
    {
        var copyability = new CopyabilityFacts(_namedTypes);
        WalkConstructorNodeForFieldReads(block, selfType, copyability, new HashSet<string>(StringComparer.Ordinal));
    }

    private void WalkConstructorNodeForFieldReads(
        IParseTree node,
        NamedTypeSymbol selfType,
        CopyabilityFacts copyability,
        HashSet<string> assignedFields)
    {
        switch (node)
        {
            case StarkParser.AssignmentExpressionContext assignment
                when assignment.assignmentOperator()?.GetText() == "="
                    && assignment.assignmentExpression() is { } rightSide:
            {
                // The right side is evaluated before the assignment takes effect.
                WalkConstructorNodeForFieldReads(rightSide, selfType, copyability, assignedFields);

                var target = assignment.unaryExpression();
                if (TryGetSelfFieldDirectTarget(target, selfType, out var initializedField))
                {
                    // `self.Field = ...` initializes the whole field: mark it, and do
                    // not treat the target itself as a read.
                    assignedFields.Add(initializedField);
                }
                else
                {
                    // A deeper or indexed target (`self.Field.X = ...`,
                    // `self.Field[i] = ...`) still reads `self.Field` as its receiver.
                    WalkConstructorNodeForFieldReads(target, selfType, copyability, assignedFields);
                }

                return;
            }

            case StarkParser.PostfixExpressionContext postfix
                when TryGetSelfFieldAccess(postfix, selfType, copyability, out var readField, out var trailingParts):
            {
                if (!assignedFields.Contains(readField))
                {
                    ReportError(
                        "STK3055",
                        $"Constructor reads field '{readField}' of 'self' before it is assigned. Assign 'self.{readField}' before reading it.",
                        postfix);
                }

                // The `self.Field` base is handled; index/argument sub-expressions of
                // the access (e.g. `self.Field[self.Other]`) still need checking.
                foreach (var trailingPart in trailingParts)
                {
                    WalkConstructorNodeForFieldReads(trailingPart, selfType, copyability, assignedFields);
                }

                return;
            }
        }

        if (node is ParserRuleContext rule && rule.children is { } children)
        {
            foreach (var child in children)
            {
                WalkConstructorNodeForFieldReads(child, selfType, copyability, assignedFields);
            }
        }
    }

    private static bool TryGetSelfFieldDirectTarget(
        StarkParser.UnaryExpressionContext target,
        NamedTypeSymbol selfType,
        out string field)
    {
        field = string.Empty;
        if (target.unaryOperator() is not null
            || target.powerExpression()?.postfixExpression() is not { } postfix
            || postfix.primaryExpression()?.Identifier()?.GetText() != "self")
        {
            return false;
        }

        var parts = postfix.postfixPart();
        if (parts.Length != 1
            || parts[0].Identifier() is not { } fieldIdentifier
            || !selfType.Fields.ContainsKey(fieldIdentifier.GetText()))
        {
            return false;
        }

        field = fieldIdentifier.GetText();
        return true;
    }

    private static bool TryGetSelfFieldAccess(
        StarkParser.PostfixExpressionContext postfix,
        NamedTypeSymbol selfType,
        CopyabilityFacts copyability,
        out string field,
        out IReadOnlyList<StarkParser.PostfixPartContext> trailingParts)
    {
        field = string.Empty;
        trailingParts = Array.Empty<StarkParser.PostfixPartContext>();
        if (postfix.primaryExpression()?.Identifier()?.GetText() != "self")
        {
            return false;
        }

        var parts = postfix.postfixPart();
        if (parts.Length == 0
            || parts[0].Identifier() is not { } fieldIdentifier
            || !selfType.Fields.TryGetValue(fieldIdentifier.GetText(), out var fieldSymbol))
        {
            return false;
        }

        // Only owning fields (dynamic storage, owned text, destructor-bearing
        // aggregates) have a meaningless pre-assignment state worth flagging.
        // Inline storage — scalars, fixed arrays, copyable aggregates — reads a
        // valid zero value and is written element-wise, so it is not a read error.
        if (copyability.IsCopyable(fieldSymbol.Type)
            || fieldSymbol.Type.Kind == StarkTypeKind.FixedArray)
        {
            return false;
        }

        field = fieldIdentifier.GetText();
        trailingParts = parts.Skip(1).ToArray();
        return true;
    }

    private void CheckGlobalDeclarations()
    {
        RegisterImportedGlobals();

        foreach (var declaration in _parseResult.Root.topLevelDeclaration())
        {
            if (declaration.globalConstantDeclaration() is { } constantDeclaration)
            {
                foreach (var declarator in constantDeclaration.constantDeclarators().constantDeclarator())
                {
                    var declaredType = ResolveConstantDeclarationType(
                        constantDeclaration.type_(),
                        constantDeclaration.INTEGER_TYPE()?.Symbol,
                        declarator,
                        Scope.CreateRoot(_globals),
                        "a global constant type");
                    _globals[declarator.Identifier().GetText()] = new VariableSymbol(
                        declarator.Identifier().GetText(),
                        declaredType,
                        IsMutable: false,
                        IsConstant: true,
                        BindingKind: GlobalBindingKind.Const,
                        HasConstProvenance: true,
                        ConstantValue: TryEvaluateCompileTimeConstant(
                            declarator.variableInitializer(),
                            Scope.CreateRoot(_globals),
                            declaredType,
                            out var constantValue)
                            ? constantValue
                            : null,
                        ConstantInitializer: TryBuildTypedConstantInitializer(
                            declarator.variableInitializer(),
                            declaredType,
                            Scope.CreateRoot(_globals),
                            out var constantInitializer)
                            ? constantInitializer
                            : null);
                }

                continue;
            }

            if (declaration.globalVariableDeclaration() is { } variableDeclaration)
            {
                var declaredType = ValidateRuntimeValueType(
                    ResolveType(variableDeclaration.type_()),
                    variableDeclaration.type_(),
                    "a global variable type");
                ValidateRuntimeTypeDoesNotDependOnEnum(declaredType, variableDeclaration.type_(), "a global variable type");
                var isMutable = variableDeclaration.MUT() is not null;

                foreach (var declarator in variableDeclaration.variableDeclarators().variableDeclarator())
                {
                    if (declarator.variableStorageCapacity() is { } capacity)
                    {
                        ReportStorageCapacityUnsupported(declarator.Identifier().GetText(), "global variable", capacity);
                    }

                    if (declarator.variableInitializer() is null)
                    {
                        ReportError(
                            "STK3001",
                            $"Variable '{declarator.Identifier().GetText()}' requires an initializer.",
                            declarator);
                        _globals[declarator.Identifier().GetText()] = new VariableSymbol(
                            declarator.Identifier().GetText(),
                            declaredType,
                            IsMutable: isMutable,
                            IsConstant: false,
                            BindingKind: isMutable ? GlobalBindingKind.Mutable : GlobalBindingKind.Immutable);
                        continue;
                    }

                    CheckVariableInitializer(declarator.variableInitializer(), declaredType, Scope.CreateRoot(_globals));
                    _globals[declarator.Identifier().GetText()] = new VariableSymbol(
                        declarator.Identifier().GetText(),
                        declaredType,
                        IsMutable: isMutable,
                        IsConstant: false,
                        BindingKind: isMutable ? GlobalBindingKind.Mutable : GlobalBindingKind.Immutable);
                }
            }
        }
    }

    private void RegisterImportedGlobals()
    {
        foreach (var module in _loadedModules.ImportedModules)
        {
            if (module.PackageImageFacts is { Globals.Count: > 0 } packageImageFacts)
            {
                foreach (var declaration in module.SyntaxModel.Declarations.Where(static declaration =>
                             declaration.Kind is DeclarationKind.GlobalConstant or DeclarationKind.GlobalVariable))
                {
                    if (!IsDeclarationVisible(module, declaration))
                    {
                        continue;
                    }

                    var qualifiedName = QualifyName(module, declaration.Name);
                    if (!packageImageFacts.Globals.TryGetValue(qualifiedName, out var global))
                    {
                        continue;
                    }

                    _globals[qualifiedName] = new VariableSymbol(
                        global.Name,
                        global.Type,
                        global.IsMutable,
                        global.IsConst,
                        global.BindingKind,
                        ConstantInitializer: global.ConstantInitializer);
                }

                continue;
            }

            foreach (var declaration in module.ParseResult.Root.topLevelDeclaration())
            {
                if (declaration.globalConstantDeclaration() is { } constantDeclaration)
                {
                    var declarationModel = module.SyntaxModel.Declarations.FirstOrDefault(
                        candidate => candidate.Kind == DeclarationKind.GlobalConstant
                                     && string.Equals(candidate.Name, constantDeclaration.constantDeclarators().constantDeclarator(0).Identifier().GetText(), StringComparison.Ordinal));

                    if (declarationModel is null || !IsDeclarationVisible(module, declarationModel))
                    {
                        continue;
                    }

                    foreach (var declarator in constantDeclaration.constantDeclarators().constantDeclarator())
                    {
                        var declaredType = ResolveConstantDeclarationType(
                            constantDeclaration.type_(),
                            constantDeclaration.INTEGER_TYPE()?.Symbol,
                            declarator,
                            Scope.CreateRoot(_globals),
                            "a global constant type",
                            currentModuleName: module.SyntaxModel.ModuleName,
                            validateInitializer: false);
                        _globals[QualifyName(module, declarator.Identifier().GetText())] = new VariableSymbol(
                            QualifyName(module, declarator.Identifier().GetText()),
                            declaredType,
                            IsMutable: false,
                            IsConstant: true,
                            BindingKind: GlobalBindingKind.Const,
                            ConstantInitializer: TryBuildTypedConstantInitializer(
                                declarator.variableInitializer(),
                                declaredType,
                                Scope.CreateRoot(_globals),
                                out var constantInitializer)
                                ? constantInitializer
                                : null);
                    }

                    continue;
                }

                if (declaration.globalVariableDeclaration() is { } variableDeclaration)
                {
                    var declaredType = ValidateRuntimeValueType(
                        ResolveType(variableDeclaration.type_(), currentModuleName: module.SyntaxModel.ModuleName),
                        variableDeclaration.type_(),
                        "a global variable type");
                    ValidateRuntimeTypeDoesNotDependOnEnum(declaredType, variableDeclaration.type_(), "a global variable type");
                    var declarationModel = module.SyntaxModel.Declarations.FirstOrDefault(
                        candidate => candidate.Kind == DeclarationKind.GlobalVariable
                                     && string.Equals(candidate.Name, variableDeclaration.variableDeclarators().variableDeclarator(0).Identifier().GetText(), StringComparison.Ordinal));

                    if (declarationModel is null || !IsDeclarationVisible(module, declarationModel))
                    {
                        continue;
                    }

                    foreach (var declarator in variableDeclaration.variableDeclarators().variableDeclarator())
                    {
                        _globals[QualifyName(module, declarator.Identifier().GetText())] = new VariableSymbol(
                            QualifyName(module, declarator.Identifier().GetText()),
                            declaredType,
                            IsMutable: variableDeclaration.MUT() is not null,
                            IsConstant: false,
                            BindingKind: variableDeclaration.MUT() is not null ? GlobalBindingKind.Mutable : GlobalBindingKind.Immutable);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Materializes concrete generic instantiations referenced inside imported
    /// source modules. Imported non-generic function bodies skip body checking
    /// (their own compile as a root module already validated them), so
    /// instantiations that appear only there — locals, constructor expressions,
    /// module-private signatures — would otherwise never reach
    /// <c>EnsureMonomorphizedType</c>: no concrete enum/type registration and no
    /// monomorphization triggers, which crashes MIR lowering of those bodies. The
    /// syntax-level walk resolves every type-argument-bearing reference and runs it
    /// through the normal monomorphization entry point. References mentioning an
    /// in-scope generic parameter belong to generic templates and are skipped (they
    /// are planned at their concrete instantiations); references whose arguments
    /// are comptime values rather than types are also skipped here and still
    /// require a root-visible use today, as do instantiations of an imported
    /// module's own module-private generics (private declarations are not in the
    /// type catalog).
    /// </summary>
    private void MaterializeImportedSourceInstantiations()
    {
        foreach (var module in _loadedModules.Modules.Values)
        {
            if (module.Reference.IsRoot || module.PackageImageFacts is not null)
            {
                continue;
            }

            foreach (var declaration in module.ParseResult.Root.topLevelDeclaration())
            {
                var declaredTypeParameters = CollectDeclaredTypeParameterNames(declaration, names: null);
                MaterializeImportedInstantiationCandidates(declaration, module, declaredTypeParameters);
            }
        }
    }

    private static HashSet<string>? CollectDeclaredTypeParameterNames(
        Antlr4.Runtime.Tree.IParseTree node,
        HashSet<string>? names)
    {
        if (node is StarkParser.TypeParameterListContext typeParameterList)
        {
            foreach (var parameter in typeParameterList.typeParameter())
            {
                (names ??= new HashSet<string>(StringComparer.Ordinal)).Add(parameter.Identifier().GetText());
            }
        }

        for (var index = 0; index < node.ChildCount; index++)
        {
            names = CollectDeclaredTypeParameterNames(node.GetChild(index), names);
        }

        return names;
    }

    private void MaterializeImportedInstantiationCandidates(
        Antlr4.Runtime.Tree.IParseTree node,
        LoadedModuleDocument module,
        HashSet<string>? genericParameters)
    {
        if (node is StarkParser.Type_Context typeContext)
        {
            if (SubtreeHasTypeArgumentList(typeContext)
                && ImportedTypeNamesResolveSilently(typeContext, module.SyntaxModel.ModuleName, genericParameters))
            {
                // Use the raw resolver: imported modules may reference their own
                // package-visible types, which root-context resolution would reject.
                var resolved = _typeResolver.ResolveType(typeContext, genericParameters, module.SyntaxModel.ModuleName);
                MaterializeImportedInstantiationSymbol(resolved, typeContext.Start, module, genericParameters);
            }

            return;
        }

        if (node is StarkParser.GenericQualifiedNameContext genericName)
        {
            MaterializeImportedGenericQualifiedName(genericName, module, genericParameters);
            return;
        }

        for (var index = 0; index < node.ChildCount; index++)
        {
            MaterializeImportedInstantiationCandidates(node.GetChild(index), module, genericParameters);
        }
    }

    /// <summary>
    /// True when every named type mentioned in the subtree resolves without
    /// diagnostics in the given module scope. The materializer trusts imported
    /// modules and must skip silently rather than report — references it cannot
    /// resolve (module-private generics, aliases of them, associated types) keep
    /// today's behavior of requiring a root-visible use.
    /// </summary>
    private bool ImportedTypeNamesResolveSilently(
        Antlr4.Runtime.Tree.IParseTree node,
        string moduleName,
        HashSet<string>? genericParameters)
    {
        if (node is StarkParser.SimpleTypeContext simpleType)
        {
            if (simpleType.builtinType() is not null)
            {
                return true;
            }

            var typeName = simpleType.qualifiedName().GetText();
            if (genericParameters?.Contains(typeName) == true)
            {
                return ImportedChildTypeNamesResolveSilently(node, moduleName, genericParameters);
            }

            var resolvable = _namedTypes.ContainsKey(typeName)
                || _typeAliases.ContainsKey(typeName)
                || (!typeName.Contains('.', StringComparison.Ordinal)
                    && (_namedTypes.ContainsKey($"{moduleName}.{typeName}")
                        || _typeAliases.ContainsKey($"{moduleName}.{typeName}")
                        || _moduleGraph.EnumerateAccessibleModuleQualifiedNames(moduleName, typeName)
                            .Any(candidate => _namedTypes.ContainsKey(candidate) || _typeAliases.ContainsKey(candidate))));
            if (!resolvable)
            {
                return false;
            }

            return ImportedChildTypeNamesResolveSilently(node, moduleName, genericParameters);
        }

        return ImportedChildTypeNamesResolveSilently(node, moduleName, genericParameters);
    }

    private bool ImportedChildTypeNamesResolveSilently(
        Antlr4.Runtime.Tree.IParseTree node,
        string moduleName,
        HashSet<string>? genericParameters)
    {
        for (var index = 0; index < node.ChildCount; index++)
        {
            if (!ImportedTypeNamesResolveSilently(node.GetChild(index), moduleName, genericParameters))
            {
                return false;
            }
        }

        return true;
    }

    private static bool SubtreeHasTypeArgumentList(Antlr4.Runtime.Tree.IParseTree node)
    {
        if (node is StarkParser.TypeArgumentListContext)
        {
            return true;
        }

        for (var index = 0; index < node.ChildCount; index++)
        {
            if (SubtreeHasTypeArgumentList(node.GetChild(index)))
            {
                return true;
            }
        }

        return false;
    }

    private void MaterializeImportedGenericQualifiedName(
        StarkParser.GenericQualifiedNameContext genericName,
        LoadedModuleDocument module,
        HashSet<string>? genericParameters)
    {
        var argumentContexts = genericName.typeArgumentList().genericArgument();
        var argumentSymbols = new List<StarkTypeSymbol>(argumentContexts.Length);
        foreach (var argument in argumentContexts)
        {
            if (argument.type_() is not { } argumentType)
            {
                // Comptime value argument — see the materializer doc comment.
                return;
            }

            if (!ImportedTypeNamesResolveSilently(argumentType, module.SyntaxModel.ModuleName, genericParameters))
            {
                return;
            }

            argumentSymbols.Add(_typeResolver.ResolveType(argumentType, genericParameters, module.SyntaxModel.ModuleName));
        }

        var typeName = genericName.qualifiedName().GetText();
        var moduleName = module.SyntaxModel.ModuleName;
        string? baseName = null;
        if (!typeName.Contains('.', StringComparison.Ordinal)
            && _namedTypes.ContainsKey($"{moduleName}.{typeName}"))
        {
            baseName = $"{moduleName}.{typeName}";
        }
        else if (_namedTypes.ContainsKey(typeName))
        {
            baseName = typeName;
        }
        else if (!typeName.Contains('.', StringComparison.Ordinal)
            && _moduleGraph.EnumerateAccessibleModuleQualifiedNames(moduleName, typeName)
                .Where(_namedTypes.ContainsKey)
                .Take(2)
                .ToArray() is { Length: 1 } importedMatches)
        {
            baseName = importedMatches[0];
        }

        if (baseName is null)
        {
            return;
        }

        MaterializeImportedInstantiationSymbol(
            StarkTypeSymbols.GenericInstantiation(baseName, argumentSymbols),
            genericName.Start,
            module,
            genericParameters);
    }

    private void MaterializeImportedInstantiationSymbol(
        StarkTypeSymbol symbol,
        Antlr4.Runtime.IToken location,
        LoadedModuleDocument module,
        HashSet<string>? genericParameters)
    {
        if (ContainsOpenGenericReference(symbol, genericParameters))
        {
            return;
        }

        if (StarkTypeSymbols.IsGenericInstantiation(symbol)
            || symbol.ElementType is not null)
        {
            EnsureMonomorphizedType(
                symbol,
                new SourceLocation(module.Reference.FilePath, location.Line, location.Column + 1));
        }
    }

    private static bool ContainsOpenGenericReference(StarkTypeSymbol symbol, HashSet<string>? genericParameters)
    {
        if (genericParameters is null)
        {
            return false;
        }

        if (symbol.Kind == StarkTypeKind.Named
            && symbol.NamedType is { } namedType
            && !namedType.Contains('.', StringComparison.Ordinal)
            && genericParameters.Contains(namedType))
        {
            return true;
        }

        if (symbol.FixedLengthParameterName is { } lengthParameter
            && genericParameters.Contains(lengthParameter))
        {
            return true;
        }

        if (symbol.ElementType is { } elementType
            && ContainsOpenGenericReference(elementType, genericParameters))
        {
            return true;
        }

        foreach (var argument in symbol.TypeArguments ?? [])
        {
            if (ContainsOpenGenericReference(argument, genericParameters))
            {
                return true;
            }
        }

        return false;
    }

    private void CheckFunctionBodies()
    {
        foreach (var module in _loadedModules.Modules.Values)
        {
            if (module.IsPackageImageImport)
            {
                continue;
            }

            foreach (var functionSyntax in DeclaredFunctionSyntaxCollector.Collect(module.ParseResult, module.SyntaxModel))
            {
                var qualifiedName = QualifyName(module, functionSyntax.Name);
                if (!_functions.TryGetValue(qualifiedName, out var signature))
                {
                    continue;
                }

                if (!module.Reference.IsRoot && !signature.IsGeneric)
                {
                    continue;
                }

                var hasImportedTemplateSummary = _importedFunctionTemplates.TryGetValue(signature.Name, out var importedTemplateSummary);
                var useImportedTemplateSummary = importedTemplateSummary?.TypedBody is not null;
                if (functionSyntax.Body.block() is not { } block)
                {
                    if (!module.Reference.IsRoot
                        && signature.IsGeneric
                        && importedTemplateSummary?.TypedBody is not null)
                    {
                        // Imported generic bodies can stay declaration-only when the package image
                        // already published a typed template body for downstream checking/lowering.
                        continue;
                    }

                    continue;
                }

                var scope = Scope.CreateRoot(_globals);
                foreach (var parameter in signature.Parameters)
                {
                    scope.Declare(CreateParameterVariableSymbol(parameter));
                }

                foreach (var comptimeParameter in signature.ComptimeGenericParams)
                {
                    var concreteValue = signature.ComptimeValues.FirstOrDefault(value =>
                        string.Equals(value.ParameterName, comptimeParameter.Name, StringComparison.Ordinal));
                    scope.Declare(new VariableSymbol(
                        comptimeParameter.Name,
                        comptimeParameter.Type,
                        IsMutable: false,
                        IsConstant: true,
                        ConstantValue: concreteValue is null
                            ? null
                            : concreteValue.IsSymbolic
                                ? CompileTimeConstant.SymbolicInteger(comptimeParameter.Type)
                                : CompileTimeConstant.Integer(concreteValue.IntegerValue, comptimeParameter.Type)));
                }
                AddParameterDisjointFacts(scope, signature.DisjointGroups);
                AddParameterSameFacts(scope, signature.SameGroups);

                var previousGenericParameters = _currentFunctionGenericParameters;
                var previousComptimeGenericParameters = _currentFunctionComptimeGenericParameters;
                var previousFunctionName = _currentFunctionName;
                var previousFunctionModuleName = _currentFunctionModuleName;
                var previousFunctionReturnType = _currentFunctionReturnType;
                var previousImportedTemplateObjectCreations = _currentImportedTemplateObjectCreations;
                var previousImportedTemplateObjectCreationOrdinals = _currentImportedTemplateObjectCreationOrdinals;
                var previousImportedTemplateEnumConstructors = _currentImportedTemplateEnumConstructors;
                var previousImportedTemplateEnumConstructorOrdinals = _currentImportedTemplateEnumConstructorOrdinals;
                var previousImportedTemplateEnumCalls = _currentImportedTemplateEnumCalls;
                var previousImportedTemplateEnumCallOrdinals = _currentImportedTemplateEnumCallOrdinals;
                var previousImportedTemplateEnumValues = _currentImportedTemplateEnumValues;
                var previousImportedTemplateEnumValueOrdinals = _currentImportedTemplateEnumValueOrdinals;
                var previousImportedTemplateEnumPatterns = _currentImportedTemplateEnumPatterns;
                var previousImportedTemplateAggregatePatterns = _currentImportedTemplateAggregatePatterns;
                var previousImportedTemplateEnumPatternOrdinals = _currentImportedTemplateEnumPatternOrdinals;
                var previousImportedTemplateLocals = _currentImportedTemplateLocalDeclarations;
                var previousImportedTemplateConversionOrdinals = _currentImportedTemplateConversionOrdinals;
                var previousImportedTemplateConversions = _currentImportedTemplateConversions;
                var previousImportedTemplateDirectCalls = _currentImportedTemplateDirectCalls;
                var previousImportedTemplateDirectCallOrdinals = _currentImportedTemplateDirectCallOrdinals;
                var previousImportedTemplateFieldAccesses = _currentImportedTemplateFieldAccesses;
                var previousImportedTemplateFieldAccessOrdinals = _currentImportedTemplateFieldAccessOrdinals;
                var previousImportedTemplateMemberCalls = _currentImportedTemplateMemberCalls;
                var previousImportedTemplateMemberCallOrdinals = _currentImportedTemplateMemberCallOrdinals;
                var previousUnsafeDepth = _unsafeDepth;
                var previousFunctionConstraints = _currentFunctionConstraints;
                var previousFunctionThreadSafetyLaws = _currentFunctionThreadSafetyLaws;
                _currentFunctionGenericParameters = signature.IsGeneric
                    ? signature.GenericParams.ToHashSet(StringComparer.Ordinal)
                    : null;
                _currentFunctionComptimeGenericParameters = signature.ComptimeGenericParams.Count == 0
                    ? null
                    : signature.ComptimeGenericParams.ToDictionary(static parameter => parameter.Name, StringComparer.Ordinal);
                _currentFunctionName = signature.Name;
                _currentFunctionModuleName = module.SyntaxModel.ModuleName;
                _currentFunctionReturnType = signature.ReturnType;
                _currentFunctionConstraints = WithImplicitTraitSelfConstraint(signature);
                _currentFunctionThreadSafetyLaws = signature.ThreadSafetyLaws;
                if (signature.IsUnsafe)
                {
                    _unsafeDepth++;
                }
                _currentImportedTemplateObjectCreations = useImportedTemplateSummary ? importedTemplateSummary!.ObjectCreations : null;
                _currentImportedTemplateObjectCreationOrdinals = useImportedTemplateSummary && importedTemplateSummary!.ObjectCreations.Count > 0
                    ? CollectTrackedObjectCreationOrdinals(block)
                    : null;
                _currentImportedTemplateEnumConstructors = useImportedTemplateSummary
                    ? importedTemplateSummary!.EnumConstructors.ToDictionary(
                    static enumConstructor => enumConstructor.Ordinal,
                    static enumConstructor => enumConstructor)
                    : null;
                _currentImportedTemplateEnumConstructorOrdinals = useImportedTemplateSummary && importedTemplateSummary!.EnumConstructors.Count > 0
                    ? CollectTemplateEnumConstructorOrdinals(block)
                    : null;
                _currentImportedTemplateEnumCalls = useImportedTemplateSummary
                    ? importedTemplateSummary!.EnumCalls.ToDictionary(
                    static enumCall => enumCall.Ordinal,
                    static enumCall => enumCall)
                    : null;
                _currentImportedTemplateEnumCallOrdinals = useImportedTemplateSummary && importedTemplateSummary!.EnumCalls.Count > 0
                    ? CollectTemplateDirectCallOrdinals(block)
                    : null;
                _currentImportedTemplateEnumValues = useImportedTemplateSummary
                    ? importedTemplateSummary!.EnumValues.ToDictionary(
                    static enumValue => enumValue.Ordinal,
                    static enumValue => enumValue)
                    : null;
                _currentImportedTemplateEnumValueOrdinals = useImportedTemplateSummary && importedTemplateSummary!.EnumValues.Count > 0
                    ? CollectTemplateEnumValueOrdinals(block)
                    : null;
                _currentImportedTemplateEnumPatterns = useImportedTemplateSummary
                    ? importedTemplateSummary!.EnumPatterns.ToDictionary(
                    static enumPattern => enumPattern.Ordinal,
                    static enumPattern => enumPattern)
                    : null;
                _currentImportedTemplateAggregatePatterns = useImportedTemplateSummary
                    ? importedTemplateSummary!.AggregatePatterns.ToDictionary(
                    static aggregatePattern => aggregatePattern.Ordinal,
                    static aggregatePattern => aggregatePattern)
                    : null;
                _currentImportedTemplateEnumPatternOrdinals = useImportedTemplateSummary
                    && (importedTemplateSummary!.EnumPatterns.Count > 0
                        || importedTemplateSummary.AggregatePatterns.Count > 0)
                    ? CollectTemplateEnumPatternOrdinals(block)
                    : null;
                // Local declaration facts are keyed to the source coordinates that were
                // present when the package image was produced. Rendered package bodies
                // are parsed from the package image surface, so those coordinates can
                // point at unrelated declarations. Explicit source types are safer here.
                _currentImportedTemplateLocalDeclarations = null;
                _currentImportedTemplateConversions = useImportedTemplateSummary
                    ? importedTemplateSummary!.Conversions.ToDictionary(
                        static conversion => conversion.Ordinal,
                        static conversion => conversion.TargetType)
                    : null;
                _currentImportedTemplateConversionOrdinals = useImportedTemplateSummary && importedTemplateSummary!.Conversions.Count > 0
                    ? CollectTemplateConversionOrdinals(block)
                    : null;
                _currentImportedTemplateDirectCalls = useImportedTemplateSummary
                    ? importedTemplateSummary!.DirectCalls.ToDictionary(
                    static call => call.Ordinal,
                    static call => call.Signature)
                    : null;
                _currentImportedTemplateDirectCallOrdinals = useImportedTemplateSummary && importedTemplateSummary!.DirectCalls.Count > 0
                    ? CollectTemplateDirectCallOrdinals(block, importedTemplateSummary.DirectCalls)
                    : null;
                _currentImportedTemplateFieldAccesses = useImportedTemplateSummary
                    ? importedTemplateSummary!.FieldAccesses.ToDictionary(
                    static access => access.Ordinal,
                    static access => access)
                    : null;
                _currentImportedTemplateFieldAccessOrdinals = useImportedTemplateSummary && importedTemplateSummary!.FieldAccesses.Count > 0
                    ? CollectTemplateFieldAccessOrdinals(block)
                    : null;
                _currentImportedTemplateMemberCalls = useImportedTemplateSummary
                    ? importedTemplateSummary!.MemberCalls.ToDictionary(
                    static call => call.Ordinal,
                    static call => call.Signature)
                    : null;
                _currentImportedTemplateMemberCallOrdinals = useImportedTemplateSummary && importedTemplateSummary!.MemberCalls.Count > 0
                    ? CollectTemplateMemberCallOrdinals(block)
                    : null;

                try
                {
                    CheckBlock(block, scope, signature.ReturnType);
                    ValidateFunctionReturnsOnAllPaths(
                        block,
                        signature.ReturnType,
                        $"Function '{functionSyntax.Name}'",
                        block);
                }
                finally
                {
                    _currentFunctionGenericParameters = previousGenericParameters;
                    _currentFunctionComptimeGenericParameters = previousComptimeGenericParameters;
                    _currentFunctionConstraints = previousFunctionConstraints;
                    _currentFunctionThreadSafetyLaws = previousFunctionThreadSafetyLaws;
                    _currentFunctionName = previousFunctionName;
                    _currentFunctionModuleName = previousFunctionModuleName;
                    _currentFunctionReturnType = previousFunctionReturnType;
                    _currentImportedTemplateObjectCreations = previousImportedTemplateObjectCreations;
                    _currentImportedTemplateObjectCreationOrdinals = previousImportedTemplateObjectCreationOrdinals;
                    _currentImportedTemplateEnumConstructors = previousImportedTemplateEnumConstructors;
                    _currentImportedTemplateEnumConstructorOrdinals = previousImportedTemplateEnumConstructorOrdinals;
                    _currentImportedTemplateEnumCalls = previousImportedTemplateEnumCalls;
                    _currentImportedTemplateEnumCallOrdinals = previousImportedTemplateEnumCallOrdinals;
                    _currentImportedTemplateEnumValues = previousImportedTemplateEnumValues;
                    _currentImportedTemplateEnumValueOrdinals = previousImportedTemplateEnumValueOrdinals;
                    _currentImportedTemplateEnumPatterns = previousImportedTemplateEnumPatterns;
                    _currentImportedTemplateAggregatePatterns = previousImportedTemplateAggregatePatterns;
                    _currentImportedTemplateEnumPatternOrdinals = previousImportedTemplateEnumPatternOrdinals;
                    _currentImportedTemplateLocalDeclarations = previousImportedTemplateLocals;
                    _currentImportedTemplateConversionOrdinals = previousImportedTemplateConversionOrdinals;
                    _currentImportedTemplateConversions = previousImportedTemplateConversions;
                    _currentImportedTemplateDirectCalls = previousImportedTemplateDirectCalls;
                    _currentImportedTemplateDirectCallOrdinals = previousImportedTemplateDirectCallOrdinals;
                    _currentImportedTemplateFieldAccesses = previousImportedTemplateFieldAccesses;
                    _currentImportedTemplateFieldAccessOrdinals = previousImportedTemplateFieldAccessOrdinals;
                    _currentImportedTemplateMemberCalls = previousImportedTemplateMemberCalls;
                    _currentImportedTemplateMemberCallOrdinals = previousImportedTemplateMemberCallOrdinals;
                    _unsafeDepth = previousUnsafeDepth;
                }
            }
        }
    }

    // Imported source-module bodies are not type-checked (their module-private
    // names cannot resolve from this context), and lower-mir has no diagnostic
    // channel — so a bare name that is exported by two of the module's own
    // imports used to crash lowering ("Named operand could not be resolved")
    // instead of reporting an ambiguity. Detect that case here: for each
    // imported source module, find the names exported by >= 2 of its imports
    // and report a bare, unshadowed reference as STK3003. The scan is gated on a
    // non-empty ambiguous-name set, which is empty for almost every module.
    private void CheckImportedModuleNameAmbiguities()
    {
        foreach (var module in _loadedModules.Modules.Values)
        {
            // The root is fully type-checked; package images are pre-checked.
            if (module.Reference.IsRoot || module.IsPackageImageImport)
            {
                continue;
            }

            var ambiguousNames = ComputeAmbiguousImportNames(module);
            if (ambiguousNames.Count == 0)
            {
                continue;
            }

            foreach (var functionSyntax in DeclaredFunctionSyntaxCollector.Collect(module.ParseResult, module.SyntaxModel))
            {
                if (functionSyntax.Body.block() is not { } block)
                {
                    continue;
                }

                // A parameter or local of the same name shadows the imports.
                var shadowedNames = new HashSet<string>(StringComparer.Ordinal);
                foreach (var parameter in functionSyntax.ParameterList.parameter())
                {
                    if (parameter.Identifier()?.GetText() is { } parameterName)
                    {
                        shadowedNames.Add(parameterName);
                    }
                }

                CollectDeclaredLocalNames(block, shadowedNames);
                WalkForAmbiguousImportReferences(module, block, ambiguousNames, shadowedNames);
            }
        }
    }

    // Names a module's own declarations would shadow, or that only one import
    // exports, are not ambiguous. Only the module's OWN declarations are counted
    // for each import (re-exports are not declarations), so a name re-exported
    // through several imports is not double-counted as an ambiguity.
    private Dictionary<string, IReadOnlyList<string>> ComputeAmbiguousImportNames(LoadedModuleDocument module)
    {
        var exportersByName = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        foreach (var import in module.SyntaxModel.Imports)
        {
            if (!_loadedModules.Modules.TryGetValue(import.ModuleName, out var importedModule))
            {
                continue;
            }

            foreach (var declaration in importedModule.SyntaxModel.Declarations)
            {
                if (declaration.Visibility is not (StarkVisibility.Public or StarkVisibility.Export))
                {
                    continue;
                }

                if (!exportersByName.TryGetValue(declaration.Name, out var exporters))
                {
                    exporters = new SortedSet<string>(StringComparer.Ordinal);
                    exportersByName[declaration.Name] = exporters;
                }

                exporters.Add(import.ModuleName);
            }
        }

        var locallyDeclared = module.SyntaxModel.Declarations
            .Select(static declaration => declaration.Name)
            .ToHashSet(StringComparer.Ordinal);

        var ambiguous = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var (name, exporters) in exportersByName)
        {
            if (exporters.Count >= 2 && !locallyDeclared.Contains(name))
            {
                ambiguous[name] = exporters.ToArray();
            }
        }

        return ambiguous;
    }

    private static void CollectDeclaredLocalNames(IParseTree node, ISet<string> names)
    {
        if (node is StarkParser.VariableDeclaratorContext declarator
            && declarator.Identifier()?.GetText() is { } localName)
        {
            names.Add(localName);
        }

        if (node is ParserRuleContext rule && rule.children is { } children)
        {
            foreach (var child in children)
            {
                CollectDeclaredLocalNames(child, names);
            }
        }
    }

    private void WalkForAmbiguousImportReferences(
        LoadedModuleDocument module,
        IParseTree node,
        IReadOnlyDictionary<string, IReadOnlyList<string>> ambiguousNames,
        ISet<string> shadowedNames)
    {
        if (node is StarkParser.PostfixExpressionContext postfix
            && postfix.primaryExpression() is { } primary
            && primary.Identifier()?.GetText() is { } name
            && !shadowedNames.Contains(name)
            && ambiguousNames.TryGetValue(name, out var exporters))
        {
            var candidates = string.Join(", ", exporters.Select(exporter => $"{exporter}.{name}"));
            ReportError(
                "STK3003",
                $"Imported symbol '{name}' is ambiguous between {candidates}. Use a fully qualified name.",
                new SourceLocation(module.Reference.FilePath, primary.Start.Line, primary.Start.Column + 1));
        }

        if (node is ParserRuleContext rule && rule.children is { } children)
        {
            foreach (var child in children)
            {
                WalkForAmbiguousImportReferences(module, child, ambiguousNames, shadowedNames);
            }
        }
    }

    private void ValidateThreadEntryMutableStaticReferences()
    {
        if (_functionGlobalReferences.Count == 0)
        {
            return;
        }

        var reachable = new HashSet<string>(StringComparer.Ordinal);
        var worklist = new Queue<string>();
        foreach (var (entryFunction, _) in EnumerateThreadEntryFunctionSeeds())
        {
            if (reachable.Add(entryFunction))
            {
                worklist.Enqueue(entryFunction);
            }
        }

        if (reachable.Count == 0)
        {
            return;
        }

        var calleesByFunction = BuildDirectCalleesByFunction();
        var globalReferencesByFunction = _functionGlobalReferences
            .GroupBy(static reference => reference.FunctionName, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => (IReadOnlyList<FunctionGlobalReference>)group.ToArray(),
                StringComparer.Ordinal);
        var reported = new HashSet<string>(StringComparer.Ordinal);

        while (worklist.Count != 0)
        {
            var functionName = worklist.Dequeue();
            if (globalReferencesByFunction.TryGetValue(functionName, out var references))
            {
                foreach (var reference in references)
                {
                    ValidateThreadReachableGlobalReference(functionName, reference, reported);
                }
            }

            if (!calleesByFunction.TryGetValue(functionName, out var callees))
            {
                continue;
            }

            foreach (var callee in callees)
            {
                if (reachable.Add(callee))
                {
                    worklist.Enqueue(callee);
                }
            }
        }
    }

    private IEnumerable<(string FunctionName, SourceLocation Location)> EnumerateThreadEntryFunctionSeeds()
    {
        foreach (var promotion in _functionPointerPromotions)
        {
            if (IsThreadEntryFunctionPointerType(promotion.TargetType))
            {
                yield return (promotion.Signature.Name, promotion.Location);
            }
        }

        foreach (var lambda in _lambdas)
        {
            if (IsThreadEntryFunctionPointerType(lambda.FunctionPointerType))
            {
                yield return (lambda.FunctionName, lambda.Location);
            }
        }
    }

    private Dictionary<string, IReadOnlyList<string>> BuildDirectCalleesByFunction()
    {
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        void Add(string? caller, string callee)
        {
            if (caller is null)
            {
                return;
            }

            if (!result.TryGetValue(caller, out var callees))
            {
                callees = [];
                result[caller] = callees;
            }

            callees.Add(callee);
        }

        foreach (var call in _directCalls)
        {
            Add(call.EnclosingFunctionName, call.Signature.Name);
        }

        foreach (var call in _memberCalls)
        {
            Add(call.EnclosingFunctionName, call.Signature.Name);
        }

        return result.ToDictionary(
            static pair => pair.Key,
            static pair => (IReadOnlyList<string>)pair.Value.Distinct(StringComparer.Ordinal).ToArray(),
            StringComparer.Ordinal);
    }

    private void ValidateThreadReachableGlobalReference(
        string reachableFunctionName,
        FunctionGlobalReference reference,
        HashSet<string> reported)
    {
        if (!_globals.TryGetValue(reference.GlobalName, out var global)
            || !global.IsMutable
            || IsSynchronizationBackedMutableStaticType(global.Type))
        {
            return;
        }

        var reportKey = $"{reachableFunctionName}|{reference.GlobalName}";
        if (!reported.Add(reportKey))
        {
            return;
        }

        ReportError(
            "STK3049",
            $"Thread-reachable function '{reachableFunctionName}' touches mutable global '{reference.GlobalName}'. Mutable statics reachable from thread entries must be synchronization-backed; use a System.Threading.Atomic* type for scalar state or System.Threading.Synchronized<T> for guarded aggregate state.",
            reference.Location);
    }

    private static bool IsThreadEntryFunctionPointerType(StarkTypeSymbol type)
    {
        return type.Kind == StarkTypeKind.FunctionPointer
            && type.FunctionPointerKind == StarkFunctionKind.Fn
            && !type.FunctionPointerIsUnsafe
            && type.FunctionPointerReturnType is { Kind: StarkTypeKind.Integer, BitWidth: 32, IsUnsigned: false }
            && type.FunctionPointerParameterTypes is { Count: <= 1 };
    }

    private static bool IsSynchronizationBackedMutableStaticType(StarkTypeSymbol type)
    {
        var coreType = StarkTypeSymbols.WithQualifiers(
            type,
            borrowKind: StarkBorrowKind.None,
            accessKind: StarkAccessKind.None,
            initializationKind: StarkInitializationKind.None,
            isMutableView: false);
        if (coreType.Kind != StarkTypeKind.Named || coreType.NamedType is not { } namedType)
        {
            return false;
        }

        var baseName = StarkTypeSymbols.GetGenericBaseName(namedType);
        if (string.Equals(baseName, "System.Threading.Synchronized", StringComparison.Ordinal)
            || string.Equals(baseName, "Synchronized", StringComparison.Ordinal))
        {
            return true;
        }

        var atomicTypeName = baseName;
        if (atomicTypeName.StartsWith(SystemThreadingAtomicFacts.ModuleName + ".", StringComparison.Ordinal))
        {
            atomicTypeName = atomicTypeName[(SystemThreadingAtomicFacts.ModuleName.Length + 1)..];
        }

        return SystemThreadingAtomicFacts.TryParseAtomicTypeName(
            atomicTypeName,
            out _,
            out _,
            out _);
    }

    private void RecordGlobalReference(string globalName, SourceLocation location)
    {
        if (_currentFunctionName is not { } functionName)
        {
            return;
        }

        _functionGlobalReferences.Add(new FunctionGlobalReference(functionName, globalName, location));
    }

    private Scope CheckBlock(StarkParser.BlockContext block, Scope parentScope, StarkTypeSymbol returnType)
    {
        var scope = new Scope(parentScope);
        foreach (var statement in block.statement())
        {
            CheckStatement(statement, scope, returnType);
        }

        return scope;
    }

    private void CheckStatement(StarkParser.StatementContext statement, Scope scope, StarkTypeSymbol returnType)
    {
        if (statement.block() is { } block)
        {
            var blockScope = CheckBlock(block, scope, returnType);
            scope.InvalidateCurrentFlowMemoryProvenance(blockScope.FlowAssignedOuterLocalNames);
            return;
        }

        if (statement.unsafeStatement() is { } unsafeStatement)
        {
            _unsafeDepth++;
            try
            {
                if (unsafeStatement.block() is { } unsafeBlock)
                {
                    var unsafeScope = CheckBlock(unsafeBlock, scope, returnType);
                    scope.InvalidateCurrentFlowMemoryProvenance(unsafeScope.FlowAssignedOuterLocalNames);
                }
                else if (unsafeStatement.assumeStatement() is { } unsafeAssumeStatement)
                {
                    CheckAssumeStatement(unsafeAssumeStatement, scope, returnType);
                }
            }
            finally
            {
                _unsafeDepth--;
            }

            return;
        }

        if (statement.assumeStatement() is { } assumeStatement)
        {
            CheckAssumeStatement(assumeStatement, scope, returnType);
            return;
        }

        if (statement.localConstantDeclaration() is { } localConstant)
        {
            StarkTypeSymbol? recordedDeclarationType = null;
            var constProvenanceByDeclarator = new Dictionary<string, ConstProvenanceKind>(StringComparer.Ordinal);
            foreach (var declarator in localConstant.constantDeclarators().constantDeclarator())
            {
                var declaratorName = declarator.Identifier().GetText();
                var declaredType = ResolveConstantDeclarationType(
                    localConstant.type_(),
                    localConstant.INTEGER_TYPE()?.Symbol,
                    declarator,
                    scope,
                    "a local constant type",
                    localDeclarationKind: TemplateLocalDeclarationFacts.ConstantKind,
                    localDeclarationContext: localConstant);
                if (recordedDeclarationType is null)
                {
                    RequireUnsafeForRawPointerType(declaredType, "local raw pointer declarations", localConstant.type_() ?? (ParserRuleContext)declarator);
                    recordedDeclarationType = declaredType;
                }
                else if (localConstant.type_() is null && recordedDeclarationType != declaredType)
                {
                    ReportError(
                        "STK3002",
                        "Grouped inferred local constants must infer the same type. Split them into separate const declarations.",
                        declarator);
                }

                constProvenanceByDeclarator[declaratorName] = ConstProvenanceKind.ImmutableBinding;
                scope.Declare(new VariableSymbol(
                    declaratorName,
                    declaredType,
                    IsMutable: false,
                    IsConstant: true,
                    ConstantValue: TryEvaluateCompileTimeConstant(
                        declarator.variableInitializer(),
                        scope,
                        declaredType,
                        out var constantValue)
                        ? constantValue
                        : null));
            }

            if (recordedDeclarationType is not null)
            {
                RecordLocalDeclarationType(
                    TemplateLocalDeclarationFacts.ConstantKind,
                    recordedDeclarationType,
                    localConstant,
                    constProvenanceByDeclarator);
            }

            return;
        }

        if (statement.localVariableDeclaration() is { } localVariable)
        {
            CheckVariableDeclaration(
                TemplateLocalDeclarationFacts.VariableKind,
                localVariable,
                localVariable.type_(),
                localVariable.variableDeclarators().variableDeclarator(),
                localVariable.MUT() is not null,
                scope);
            return;
        }

        if (statement.ifStatement() is { } ifStatement)
        {
            IReadOnlyList<string>? trueBranchDisjointRoots = null;
            StarkTypeSymbol? ifPatternScrutineeType = null;
            if (ifStatement.expression() is { } condition)
            {
                var conditionType = EvaluateExpression(condition, scope, allowFunctionReference: false).Type;
                if (ifStatement.pattern() is not null)
                {
                    // `if (expr is pattern)` — the condition expression is the scrutinee, not a
                    // boolean; the pattern's captures bind into the then-branch only.
                    ifPatternScrutineeType = conditionType;
                }
                else
                {
                    EnsureBoolean(conditionType, condition, "if conditions must be of type 'bool'");
                }
            }
            else if (ifStatement.disjointRuntimeCondition() is { } disjointCondition)
            {
                trueBranchDisjointRoots = CheckDisjointRuntimeCondition(disjointCondition, scope);
            }

            var thenScope = new Scope(scope);
            if (ifPatternScrutineeType is { } ifScrutineeType && ifStatement.pattern() is { } ifPattern)
            {
                BindPattern(ifPattern, ifScrutineeType, thenScope);
            }
            if (trueBranchDisjointRoots is { Count: >= 2 })
            {
                thenScope.AddDisjointFact(trueBranchDisjointRoots);
            }

            CheckStatement(ifStatement.statement(0), thenScope, returnType);
            var assignedOuterLocalNames = new HashSet<string>(
                thenScope.FlowAssignedOuterLocalNames,
                StringComparer.Ordinal);
            if (ifStatement.statement().Length > 1)
            {
                var elseScope = new Scope(scope);
                CheckStatement(ifStatement.statement(1), elseScope, returnType);
                assignedOuterLocalNames.UnionWith(elseScope.FlowAssignedOuterLocalNames);
            }

            scope.InvalidateCurrentFlowMemoryProvenance(assignedOuterLocalNames);
            return;
        }

        var labeledStatement = statement.labeledStatement();
        var statementSwitch = statement.switchStatement() ?? labeledStatement?.switchStatement();
        var statementWhile = statement.whileStatement() ?? labeledStatement?.whileStatement();
        var statementFor = statement.forStatement() ?? labeledStatement?.forStatement();

        if (statementSwitch is { } switchStatement)
        {
            // Imported (package-image) signatures can hand back generic enum instantiations
            // this compilation has not monomorphized yet (e.g. switching directly on
            // `Pkg.Fetch(x)` returning `Pkg.Outcome<i32>`). Ensure the concrete enum exists so
            // coverage analysis sees its variants and pattern binding resolves against it.
            var switchType = EnsureMonomorphizedType(
                EvaluateExpression(switchStatement.expression(), scope, allowFunctionReference: false).Type,
                Location(switchStatement.expression()));
            ValidateImplementedSwitchShape(switchStatement, switchType);
            RecordSwitch(switchStatement, switchType);

            var assignedOuterLocalNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var section in switchStatement.switchSection())
            {
                var sectionScope = new Scope(scope);
                ValidateSwitchSectionPatternCaptures(section, switchType);

                foreach (var label in section.switchLabel())
                {
                    foreach (var pattern in label.pattern())
                    {
                        BindPattern(pattern, switchType, sectionScope);
                    }

                    if (label.whenClause() is { } whenClause)
                    {
                        EnsureBoolean(EvaluateExpression(whenClause.expression(), sectionScope, allowFunctionReference: false).Type, whenClause.expression(), "switch when-clauses must be of type 'bool'");
                    }
                }

                foreach (var nestedStatement in section.statement())
                {
                    CheckStatement(nestedStatement, sectionScope, returnType);
                }

                assignedOuterLocalNames.UnionWith(sectionScope.FlowAssignedOuterLocalNames);
            }

            scope.InvalidateCurrentFlowMemoryProvenance(assignedOuterLocalNames);
            return;
        }

        if (statementWhile is { } whileStatement)
        {
            var whileConditionType = EvaluateExpression(whileStatement.expression(), scope, allowFunctionReference: false).Type;
            var loopBodyScope = new Scope(scope);
            if (whileStatement.pattern() is { } whilePattern)
            {
                // `while ... (expr is pattern)` — the condition is the scrutinee; captures bind
                // into the loop body and are re-bound each iteration.
                BindPattern(whilePattern, whileConditionType, loopBodyScope);
            }
            else
            {
                EnsureBoolean(whileConditionType, whileStatement.expression(), "while conditions must be of type 'bool'");
            }

            CheckLoopContracts(
                whileStatement.loopContract(),
                whileStatement.statement(),
                scope,
                condition: whileStatement.expression());
            CheckStatement(whileStatement.statement(), loopBodyScope, returnType);
            scope.InvalidateCurrentFlowMemoryProvenance(loopBodyScope.FlowAssignedOuterLocalNames);
            return;
        }

        if (statementFor is { } forStatement)
        {
            var loopScope = new Scope(scope);
            var forTraversal = forStatement.forTraversal();

            if (forTraversal is not null)
            {
                CheckForTraversal(forTraversal, loopScope);
            }
            else if (forStatement.forInitializer()?.localForVariableDeclaration() is { } localForVariableDeclaration)
            {
                CheckVariableDeclaration(
                    TemplateLocalDeclarationFacts.ForVariableKind,
                    localForVariableDeclaration,
                    localForVariableDeclaration.type_(),
                    localForVariableDeclaration.variableDeclarators().variableDeclarator(),
                    localForVariableDeclaration.MUT() is not null,
                    loopScope);
            }
            else if (forStatement.forInitializer()?.expressionList() is { } initializerExpressions)
            {
                foreach (var expression in initializerExpressions.expression())
                {
                    EvaluateExpression(expression, loopScope, allowFunctionReference: false);
                }
            }

            if (forTraversal is null && forStatement.forCondition() is { } condition)
            {
                EnsureBoolean(EvaluateExpression(condition.expression(), loopScope, allowFunctionReference: false).Type, condition.expression(), "for conditions must be of type 'bool'");
            }

            if (forTraversal is null && forStatement.forIterator() is { } iterator)
            {
                foreach (var expression in iterator.expressionList().expression())
                {
                    EvaluateExpression(expression, loopScope, allowFunctionReference: false);
                }
            }

            CheckLoopContracts(
                forStatement.loopContract(),
                forStatement.statement(),
                loopScope,
                forStatement: forStatement,
                condition: forTraversal?.expression() ?? forStatement.forCondition()?.expression(),
                iteratorExpressions: forTraversal is null ? forStatement.forIterator()?.expressionList().expression() : null);

            CheckStatement(forStatement.statement(), loopScope, returnType);
            scope.InvalidateCurrentFlowMemoryProvenance(loopScope.FlowAssignedOuterLocalNames);
            return;
        }

        if (statement.returnStatement() is { } returnStatement)
        {
            if (returnStatement.expression() is null)
            {
                if (returnType.Kind != StarkTypeKind.Void)
                {
                    ReportError("STK3002", $"Function must return '{returnType.DisplayName}'.", returnStatement);
                }

                return;
            }

            if (_insideConstructorBody)
            {
                ReportError("STK3002", "Constructor bodies cannot return a value.", returnStatement.expression());
                return;
            }

            if (returnType.Kind == StarkTypeKind.Void)
            {
                ReportError("STK3002", "Void functions cannot return a value.", returnStatement.expression());
                return;
            }

            var value = EvaluateExpression(returnStatement.expression(), scope, allowFunctionReference: false, expectedType: returnType);
            EnsureReturnCompatible(returnType, value, returnStatement.expression());
            return;
        }

        if (statement.expressionStatement() is { } expressionStatement)
        {
            EvaluateExpression(expressionStatement.expression(), scope, allowFunctionReference: false);
        }
    }

    private void CheckAssumeStatement(
        StarkParser.AssumeStatementContext assumeStatement,
        Scope scope,
        StarkTypeSymbol returnType)
    {
        if (_unsafeDepth == 0)
        {
            ReportError(
                "STK3024",
                "Unsafe disjoint assumptions require an unsafe context. Write `unsafe assume disjoint(...) { ... }`, wrap the statement in `unsafe { ... }`, or move it into an `unsafe fn`.",
                assumeStatement);
        }

        var assumedScope = new Scope(scope);
        var assumedDisjointRoots = CheckUnsafeAssumeDisjointCondition(assumeStatement.disjointRuntimeCondition(), scope);
        if (assumedDisjointRoots is { Count: >= 2 })
        {
            assumedScope.AddDisjointFact(assumedDisjointRoots);
        }

        CheckStatement(assumeStatement.statement(), assumedScope, returnType);
        scope.InvalidateCurrentFlowMemoryProvenance(assumedScope.FlowAssignedOuterLocalNames);
    }

    private IReadOnlyList<string>? CheckDisjointRuntimeCondition(StarkParser.DisjointRuntimeConditionContext condition, Scope scope)
    {
        var expressions = condition.expressionList().expression();
        if (expressions.Length < 2)
        {
            ReportError(
                "STK3025",
                "Runtime disjoint checks require at least two operands.",
                condition);
            return null;
        }

        var isValid = true;
        var rootKeys = new List<string>(expressions.Length);
        foreach (var expression in expressions)
        {
            if (TryCheckRawPointerRegionDisjointOperand(expression, scope, out var regionRootKey, out var matchedRegion))
            {
                if (regionRootKey is { Length: > 0 })
                {
                    rootKeys.Add(regionRootKey);
                }

                continue;
            }

            if (matchedRegion)
            {
                isValid = false;
                continue;
            }

            var binding = EvaluateExpression(expression, scope, allowFunctionReference: false);
            if (!CanRuntimeDisjointTest(binding.Type))
            {
                isValid = false;
                ReportError(
                    "STK3025",
                    $"Runtime disjoint checks currently require memory-backed operands such as slices, text views, borrows, or raw pointers, but found '{binding.Type.DisplayName}'.",
                    expression);
            }

            if (TryGetMemoryArgumentRoot(binding, expression, scope, out var root)
                || TryGetMemoryArgumentRoot(expression, binding.Type, scope, out root))
            {
                if (root.AliasRootKeys is { Count: > 0 } aliasRootKeys)
                {
                    rootKeys.AddRange(aliasRootKeys);
                }
                else
                {
                    rootKeys.Add(root.RootKey);
                }
            }
        }

        if (!isValid)
        {
            return null;
        }

        var distinctRootKeys = rootKeys
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return distinctRootKeys.Length >= 2 ? distinctRootKeys : null;
    }

    private IReadOnlyList<string>? CheckUnsafeAssumeDisjointCondition(
        StarkParser.DisjointRuntimeConditionContext condition,
        Scope scope)
    {
        var roots = CheckDisjointRuntimeCondition(condition, scope);
        if (roots is { Count: >= 2 })
        {
            return roots;
        }

        ReportError(
            "STK3031",
            "Unsafe disjoint assumptions must name at least two distinct compiler-visible memory regions. Same-root assumptions, hidden call results, and integer-laundered pointers cannot establish a scoped noalias fact; name visible roots or representable subregions such as 'ptr[0, count]' and 'ptr[count, count]'.",
            condition);
        return null;
    }

    private void CheckForTraversal(StarkParser.ForTraversalContext traversal, Scope loopScope)
    {
        var source = EvaluateExpression(traversal.expression(), loopScope, allowFunctionReference: false);
        if (!TryGetTraversalSource(source, traversal.expression(), out var sourceInfo))
        {
            return;
        }

        if (traversal.traversalIndexBinding() is { } indexBinding)
        {
            var indexType = ResolveType(indexBinding.type_(), _currentFunctionGenericParameters, _currentFunctionModuleName);
            if (indexType.Kind != StarkTypeKind.Integer)
            {
                ReportError(
                    "STK3002",
                    $"for-in traversal index '{indexBinding.Identifier().GetText()}' must have an integer type but found '{indexType.DisplayName}'.",
                    indexBinding.type_());
            }
            else if (!CanAssign(indexType, sourceInfo.IndexRangeType))
            {
                ReportError(
                    "STK3002",
                    $"for-in traversal index '{indexBinding.Identifier().GetText()}' has type '{indexType.DisplayName}', which cannot represent every index produced by source type '{source.Type.DisplayName}'.",
                    indexBinding.type_());
            }

            var storageClassText = indexBinding.storageClass().GetText();
            if (!string.Equals(storageClassText, "stack", StringComparison.Ordinal)
                && !string.Equals(storageClassText, "register", StringComparison.Ordinal))
            {
                ReportError(
                    "STK3002",
                    $"for-in traversal index '{indexBinding.Identifier().GetText()}' must use 'stack' or 'register' storage.",
                    indexBinding.storageClass());
            }

            loopScope.Declare(new VariableSymbol(
                indexBinding.Identifier().GetText(),
                indexType,
                IsMutable: false,
                IsConstant: false));
            RecordLocalDeclarationType(
                TemplateLocalDeclarationFacts.TraversalIndexKind,
                indexType,
                indexBinding);
        }

        var elementBinding = traversal.traversalElementBinding();
        var elementBindingType = ResolveType(elementBinding.type_(), _currentFunctionGenericParameters, _currentFunctionModuleName);
        if (elementBindingType.BorrowKind != StarkBorrowKind.Borrow
            || elementBindingType.InitializationKind != StarkInitializationKind.None)
        {
            ReportError(
                "STK3002",
                $"for-in traversal element '{elementBinding.Identifier().GetText()}' must be declared as 'borrow T' or 'borrow mut T'.",
                elementBinding.type_());
        }

        var elementValueType = StarkTypeSymbols.BorrowReturnValueType(elementBindingType);
        if (!CanAssign(elementValueType, sourceInfo.ElementType))
        {
            ReportError(
                "STK3002",
                $"for-in traversal element '{elementBinding.Identifier().GetText()}' expects '{elementValueType.DisplayName}' but source elements are '{sourceInfo.ElementType.DisplayName}'.",
                elementBinding.type_());
        }

        if (elementBindingType.IsMutableView && !sourceInfo.CanBorrowElementMutably)
        {
            ReportError(
                "STK3002",
                $"for-in traversal element '{elementBinding.Identifier().GetText()}' requests 'borrow mut', but source '{traversal.expression().GetText()}' does not provide mutable element storage.",
                elementBinding.type_());
        }

        loopScope.Declare(new VariableSymbol(
            elementBinding.Identifier().GetText(),
            elementBindingType,
            IsMutable: false,
            IsConstant: false));
        RecordLocalDeclarationType(
            TemplateLocalDeclarationFacts.TraversalElementKind,
            elementBindingType,
            elementBinding);
    }

    private bool TryGetTraversalSource(
        ExpressionBinding source,
        ParserRuleContext context,
        out TraversalSourceInfo info)
    {
        if (source.Type.ElementType is not { } elementType
            || source.Type.Kind is not (StarkTypeKind.FixedArray or StarkTypeKind.Slice or StarkTypeKind.Dynamic))
        {
            ReportError(
                "STK3010",
                $"for-in traversal expects a fixed array, slice, or dynamic storage value but found '{source.Type.DisplayName}'.",
                context);
            info = default!;
            return false;
        }

        var projectedElementType = UsesFrozenProjectionSemantics(source)
            ? StarkTypeSymbols.FreezeReachableView(elementType)
            : ProjectFrozenView(source.Type, elementType);
        var canBorrowMutably = source.Type.Kind switch
        {
            StarkTypeKind.FixedArray => source.IsAddressMutable
                && source.Type.AccessKind != StarkAccessKind.Frozen
                && projectedElementType.AccessKind != StarkAccessKind.Frozen,
            StarkTypeKind.Slice => source.IsAddressMutable
                && (source.Type.IsMutableView || source.Type.InitializationKind != StarkInitializationKind.None)
                && source.Type.AccessKind != StarkAccessKind.Frozen
                && projectedElementType.AccessKind != StarkAccessKind.Frozen,
            StarkTypeKind.Dynamic => source.IsAddressMutable
                && source.Type.AccessKind != StarkAccessKind.Frozen
                && projectedElementType.AccessKind != StarkAccessKind.Frozen,
            _ => false
        };

        var indexRangeType = source.Type.Kind == StarkTypeKind.FixedArray
            && source.Type.FixedLength is int fixedLength
            && fixedLength > 0
                ? StarkTypeSymbols.Integer(64, BigInteger.Zero, new BigInteger(fixedLength - 1))
                : NonNegativeI64Type;
        info = new TraversalSourceInfo(projectedElementType, indexRangeType, canBorrowMutably);
        return true;
    }

    private void CheckLoopContracts(
        IReadOnlyList<StarkParser.LoopContractContext> contracts,
        StarkParser.StatementContext body,
        Scope scope,
        StarkParser.ForStatementContext? forStatement = null,
        StarkParser.ExpressionContext? condition = null,
        IReadOnlyList<StarkParser.ExpressionContext>? iteratorExpressions = null)
    {
        if (contracts.Count == 0)
        {
            return;
        }

        if (TryValidateConservativeIndependentLoop(body, scope, condition, iteratorExpressions, out var reason))
        {
            return;
        }

        if (forStatement is not null
            && TryValidateConservativeIndependentMemoryForLoop(forStatement, scope, out reason))
        {
            return;
        }

        var detail = string.IsNullOrWhiteSpace(reason)
            ? string.Empty
            : $" This loop uses {reason}.";
        foreach (var contract in contracts)
        {
            ReportError(
                "STK3027",
                $"Loop 'independent' contracts currently support scalar-local loops and a canonical memory-backed subset;{detail} This loop is outside the accepted dependency-validation subset.",
                contract);
        }
    }

    private bool TryValidateConservativeIndependentLoop(
        StarkParser.StatementContext body,
        Scope scope,
        StarkParser.ExpressionContext? condition,
        IReadOnlyList<StarkParser.ExpressionContext>? iteratorExpressions,
        out string reason)
    {
        if (condition is not null
            && !TryValidateIndependentPureExpression(condition, scope, out reason))
        {
            return false;
        }

        if (iteratorExpressions is not null)
        {
            foreach (var iteratorExpression in iteratorExpressions)
            {
                if (!TryValidateIndependentLoopExpressionStatement(iteratorExpression, scope, out reason))
                {
                    return false;
                }
            }
        }

        return TryValidateIndependentLoopStatement(body, scope, out reason);
    }

    private bool TryValidateConservativeIndependentMemoryForLoop(
        StarkParser.ForStatementContext? forStatement,
        Scope scope,
        out string reason)
    {
        if (forStatement is null)
        {
            reason = "memory dependency validation is currently implemented only for canonical for loops";
            return false;
        }

        if (!TryGetIndependentForInductionVariable(forStatement, scope, out var inductionName, out reason))
        {
            return false;
        }

        if (forStatement.forCondition()?.expression() is { } condition
            && !TryValidateIndependentPureExpression(condition, scope, out reason))
        {
            return false;
        }

        if (forStatement.forIterator()?.expressionList().expression() is not [var iteratorExpression]
            || !TryValidateIndependentUnitIncrement(iteratorExpression, inductionName, scope, out reason))
        {
            return false;
        }

        var accesses = new List<IndependentLoopMemoryAccess>();
        if (!TryValidateIndependentMemoryLoopStatement(forStatement.statement(), scope, inductionName, accesses, out reason))
        {
            return false;
        }

        if (accesses.Count == 0)
        {
            reason = "no memory accesses were found for memory dependency validation";
            return false;
        }

        if (!TryValidateIndependentRawPointerAccessBounds(forStatement, scope, inductionName, accesses, out reason))
        {
            return false;
        }

        var loopExclusiveUpperBoundText = forStatement.forCondition()?.expression() is { } loopCondition
            && TryGetIndependentLoopExclusiveUpperBound(loopCondition, inductionName, out var upperBoundText)
                ? upperBoundText
                : null;

        return TryValidateIndependentLoopMemoryAccesses(accesses, scope, loopExclusiveUpperBoundText, out reason);
    }

    private bool TryValidateIndependentRawPointerAccessBounds(
        StarkParser.ForStatementContext forStatement,
        Scope scope,
        string inductionName,
        IReadOnlyList<IndependentLoopMemoryAccess> accesses,
        out string reason)
    {
        var rawPointerAccesses = accesses
            .Where(access => scope.TryLookup(access.DisplayName, out var symbol)
                             && symbol.Type.Kind == StarkTypeKind.RawPointer
                             && symbol.RawPointerElementCountExpression is not null)
            .ToArray();
        if (rawPointerAccesses.Length == 0)
        {
            reason = string.Empty;
            return true;
        }

        if (!TryGetIndependentForInitializerExpression(forStatement, out var initializerExpression)
            || !CompileTimeExpressionEvaluator.TryEvaluateInteger(
                initializerExpression,
                out var initialValue,
                CreateCompileTimeEvaluationServices(scope))
            || initialValue != BigInteger.Zero)
        {
            reason = "bounded raw pointer independent loops must start the induction variable at zero";
            return false;
        }

        if (forStatement.forCondition()?.expression() is not { } condition
            || !TryGetIndependentLoopExclusiveUpperBound(condition, inductionName, out var upperBoundText))
        {
            reason = "bounded raw pointer independent loops must use a canonical 'index < count' condition";
            return false;
        }

        foreach (var access in rawPointerAccesses)
        {
            if (!scope.TryLookup(access.DisplayName, out var symbol)
                || symbol.RawPointerElementCountExpression is not { } countExpression
                || !CanProveExclusiveUpperBoundWithinRawPointerCount(upperBoundText, countExpression, scope))
            {
                reason = $"bounded raw pointer access root '{access.DisplayName}' is not proven in range for the loop induction variable";
                return false;
            }
        }

        reason = string.Empty;
        return true;
    }

    private static bool TryGetIndependentForInitializerExpression(
        StarkParser.ForStatementContext forStatement,
        out StarkParser.ExpressionContext expression)
    {
        expression = null!;
        if (forStatement.forInitializer()?.localForVariableDeclaration()?.variableDeclarators().variableDeclarator() is not [var declarator]
            || declarator.variableInitializer()?.expression() is not { } initializerExpression)
        {
            return false;
        }

        expression = initializerExpression;
        return true;
    }

    private static bool TryGetIndependentLoopExclusiveUpperBound(
        StarkParser.ExpressionContext expression,
        string inductionName,
        out string upperBoundText)
    {
        upperBoundText = string.Empty;
        if (!TryGetSingleRelationalExpression(expression, out var relational)
            || relational.shiftExpression() is not [var left, var right]
            || ExtractOperators<StarkParser.ShiftExpressionContext>(relational) is not [var op])
        {
            return false;
        }

        if (op == "<"
            && string.Equals(NormalizeExpressionText(left.GetText()), inductionName, StringComparison.Ordinal))
        {
            upperBoundText = NormalizeExpressionText(right.GetText());
            return true;
        }

        if (op == ">"
            && string.Equals(NormalizeExpressionText(right.GetText()), inductionName, StringComparison.Ordinal))
        {
            upperBoundText = NormalizeExpressionText(left.GetText());
            return true;
        }

        return false;
    }

    private static bool TryGetSingleRelationalExpression(
        StarkParser.ExpressionContext expression,
        out StarkParser.RelationalExpressionContext relational)
    {
        relational = null!;
        var assignment = expression.assignmentExpression();
        if (assignment.assignmentOperator() is not null || assignment.conditionalExpression() is not { } conditional)
        {
            return false;
        }

        if (conditional.expression().Length != 0)
        {
            return false;
        }

        var logicalOr = conditional.logicalOrExpression();
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
        if (ExtractOperators<StarkParser.RelationalExpressionContext>(equality).Count != 0
            || equality.relationalExpression().Length != 1)
        {
            return false;
        }

        relational = equality.relationalExpression(0);
        return true;
    }

    private static bool CanProveExclusiveUpperBoundWithinRawPointerCount(
        string upperBoundText,
        string countExpression,
        Scope scope)
    {
        var normalizedCount = NormalizeExpressionText(countExpression);
        if (string.Equals(upperBoundText, normalizedCount, StringComparison.Ordinal))
        {
            return true;
        }

        return TryResolveMemoryRootIndexRange(upperBoundText, scope, out _, out var upperMax)
            && TryResolveMemoryRootIndexRange(normalizedCount, scope, out var countMin, out _)
            && upperMax <= countMin;
    }

    private bool TryGetIndependentForInductionVariable(
        StarkParser.ForStatementContext forStatement,
        Scope scope,
        out string inductionName,
        out string reason)
    {
        inductionName = string.Empty;
        if (forStatement.forInitializer()?.localForVariableDeclaration() is not { } declaration
            || declaration.MUT() is null
            || declaration.variableDeclarators().variableDeclarator() is not [var declarator])
        {
            reason = "memory-backed independent loops must declare exactly one mutable scalar induction variable in the for initializer";
            return false;
        }

        var storageClass = declaration.storageClass().GetText();
        if (!IsIndependentScalarLocalStorageClass(storageClass))
        {
            reason = "the induction variable must use stack or register scalar storage";
            return false;
        }

        var declaredType = ResolveType(declaration.type_(), _currentFunctionGenericParameters, _currentFunctionModuleName);
        if (!IsIndependentScalarLocalType(declaredType)
            || declaredType.Kind != StarkTypeKind.Integer)
        {
            reason = "the induction variable must use an integer scalar type";
            return false;
        }

        if (declarator.variableInitializer() is not { } initializer)
        {
            reason = "the induction variable must have a pure scalar initializer";
            return false;
        }

        if (!TryValidateIndependentVariableInitializer(initializer, scope, out reason))
        {
            reason = string.IsNullOrWhiteSpace(reason)
                ? "the induction variable must have a pure scalar initializer"
                : reason;
            return false;
        }

        inductionName = declarator.Identifier().GetText();
        return true;
    }

    private bool TryValidateIndependentUnitIncrement(
        StarkParser.ExpressionContext expression,
        string inductionName,
        Scope scope,
        out string reason)
    {
        var assignment = expression.assignmentExpression();
        if (assignment.assignmentOperator()?.GetText() != "+="
            || !TryGetDirectAssignmentTargetName(assignment.unaryExpression(), out var targetName)
            || !string.Equals(targetName, inductionName, StringComparison.Ordinal)
            || !TryValidateIndependentPureAssignmentExpression(assignment.assignmentExpression(), scope, out reason)
            || !IsLiteralOneExpression(assignment.assignmentExpression()))
        {
            reason = "memory-backed independent loops must increment the induction variable by exactly one with 'index += 1'";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private bool TryValidateIndependentMemoryLoopStatement(
        StarkParser.StatementContext statement,
        Scope scope,
        string inductionName,
        List<IndependentLoopMemoryAccess> accesses,
        out string reason)
    {
        if (statement.emptyStatement() is not null)
        {
            reason = string.Empty;
            return true;
        }

        if (statement.block() is { } block)
        {
            var blockScope = new Scope(scope);
            foreach (var nestedStatement in block.statement())
            {
                if (!TryValidateIndependentMemoryLoopStatement(nestedStatement, blockScope, inductionName, accesses, out reason))
                {
                    return false;
                }
            }

            reason = string.Empty;
            return true;
        }

        if (statement.localVariableDeclaration() is { } localVariableDeclaration)
        {
            return TryValidateIndependentMemoryLocalVariableDeclaration(localVariableDeclaration, scope, inductionName, accesses, out reason);
        }

        if (statement.localConstantDeclaration() is { } localConstantDeclaration)
        {
            return TryValidateIndependentLocalConstantDeclaration(localConstantDeclaration, scope, out reason);
        }

        if (statement.expressionStatement() is { } expressionStatement)
        {
            return TryValidateIndependentMemoryLoopExpressionStatement(expressionStatement.expression(), scope, inductionName, accesses, out reason);
        }

        if (statement.ifStatement() is { } ifStatement)
        {
            return TryValidateIndependentMemoryIfStatement(ifStatement, scope, inductionName, accesses, out reason);
        }

        reason = statement switch
        {
            _ when statement.unsafeStatement() is not null => "unsafe blocks are outside the first supported subset",
            _ when statement.assumeStatement() is not null => "unsafe assumptions are outside the first supported subset",
            _ when statement.switchStatement() is not null => "switch statements are outside the first supported subset",
            _ when statement.whileStatement() is not null => "nested loops are outside the first supported subset",
            _ when statement.forStatement() is not null => "nested loops are outside the first supported subset",
            _ when statement.returnStatement() is not null => "early exits are outside the first supported subset",
            _ when statement.breakStatement() is not null => "early exits are outside the first supported subset",
            _ when statement.continueStatement() is not null => "early exits are outside the first supported subset",
            _ => "the loop body uses an unsupported statement form"
        };
        return false;
    }

    private bool TryValidateIndependentMemoryIfStatement(
        StarkParser.IfStatementContext ifStatement,
        Scope scope,
        string inductionName,
        List<IndependentLoopMemoryAccess> accesses,
        out string reason)
    {
        IReadOnlyList<string>? trueBranchDisjointRoots = null;
        if (ifStatement.expression() is { } condition)
        {
            if (!TryValidateIndependentMemoryExpression(condition, scope, inductionName, accesses, out reason))
            {
                return false;
            }
        }
        else if (ifStatement.disjointRuntimeCondition() is { } disjointCondition)
        {
            trueBranchDisjointRoots = CheckDisjointRuntimeCondition(disjointCondition, scope);
        }
        else
        {
            reason = "conditional memory-backed independent loop bodies need a boolean or disjoint condition";
            return false;
        }

        var thenScope = new Scope(scope);
        if (trueBranchDisjointRoots is { Count: >= 2 })
        {
            thenScope.AddDisjointFact(trueBranchDisjointRoots);
        }

        if (!TryValidateIndependentMemoryLoopStatement(ifStatement.statement(0), thenScope, inductionName, accesses, out reason))
        {
            return false;
        }

        if (ifStatement.statement().Length > 1
            && !TryValidateIndependentMemoryLoopStatement(ifStatement.statement(1), new Scope(scope), inductionName, accesses, out reason))
        {
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private bool TryValidateIndependentMemoryLocalVariableDeclaration(
        StarkParser.LocalVariableDeclarationContext declaration,
        Scope scope,
        string inductionName,
        List<IndependentLoopMemoryAccess> accesses,
        out string reason)
    {
        var storageClass = declaration.storageClass().GetText();
        if (!IsIndependentScalarLocalStorageClass(storageClass))
        {
            reason = "local declarations inside independent loops must use stack or register scalar storage";
            return false;
        }

        var declaredType = ResolveType(declaration.type_(), _currentFunctionGenericParameters, _currentFunctionModuleName);
        if (!IsIndependentScalarLocalType(declaredType))
        {
            reason = "local declarations inside independent loops must use scalar local types";
            return false;
        }

        foreach (var declarator in declaration.variableDeclarators().variableDeclarator())
        {
            if (declarator.variableStorageCapacity() is not null)
            {
                reason = "local declarations inside independent loops cannot declare variable-sized storage";
                return false;
            }

            if (declarator.variableInitializer()?.expression() is not { } initializerExpression)
            {
                reason = "memory-backed independent loop locals need scalar expression initializers";
                return false;
            }

            if (!TryValidateIndependentMemoryExpression(initializerExpression, scope, inductionName, accesses, out reason))
            {
                reason = string.IsNullOrWhiteSpace(reason)
                    ? "memory-backed independent loop locals need scalar expression initializers"
                    : reason;
                return false;
            }

            scope.Declare(new VariableSymbol(
                declarator.Identifier().GetText(),
                declaredType,
                IsMutable: declaration.MUT() is not null,
                IsConstant: false));
        }

        reason = string.Empty;
        return true;
    }

    private bool TryValidateIndependentMemoryLoopExpressionStatement(
        StarkParser.ExpressionContext expression,
        Scope scope,
        string inductionName,
        List<IndependentLoopMemoryAccess> accesses,
        out string reason)
    {
        var assignment = expression.assignmentExpression();
        if (assignment.assignmentOperator() is null)
        {
            return TryValidateIndependentMemoryExpression(expression, scope, inductionName, accesses, out reason);
        }

        var targetAccesses = new List<IndependentLoopMemoryAccess>();
        if (!TryValidateIndependentMemoryAssignmentTarget(assignment.unaryExpression(), scope, inductionName, targetAccesses, out reason))
        {
            return false;
        }

        var valueAccesses = new List<IndependentLoopMemoryAccess>();
        if (!TryValidateIndependentMemoryAssignmentExpression(assignment.assignmentExpression(), scope, inductionName, valueAccesses, out reason))
        {
            return false;
        }

        accesses.AddRange(targetAccesses);
        accesses.AddRange(valueAccesses);
        reason = string.Empty;
        return true;
    }

    private bool TryValidateIndependentMemoryAssignmentTarget(
        StarkParser.UnaryExpressionContext target,
        Scope scope,
        string inductionName,
        List<IndependentLoopMemoryAccess> accesses,
        out string reason)
    {
        if (TryCreateIndependentLoopRawPointerDereferenceAccess(target, scope, inductionName, isWrite: true, out var rawPointerAccess, out reason))
        {
            accesses.Add(rawPointerAccess);
            return true;
        }

        if (TryGetDirectAssignmentTargetName(target, out var targetName)
            && string.Equals(targetName, inductionName, StringComparison.Ordinal))
        {
            reason = "memory-backed independent loop bodies cannot assign to the induction variable";
            return false;
        }

        if (TryValidateIndependentAssignmentTarget(target, scope, out reason))
        {
            return true;
        }

        if (target.powerExpression()?.postfixExpression() is { } postfix
            && TryCreateIndependentLoopMemoryAccess(postfix, scope, inductionName, isWrite: true, out var access, out reason))
        {
            accesses.Add(access);
            return true;
        }

        reason = string.IsNullOrWhiteSpace(reason)
            ? "assignments must target mutable scalar locals or memory at the loop induction index"
            : reason;
        return false;
    }

    private bool TryValidateIndependentMemoryAssignmentExpression(
        StarkParser.AssignmentExpressionContext expression,
        Scope scope,
        string inductionName,
        List<IndependentLoopMemoryAccess> accesses,
        out string reason)
    {
        if (expression.assignmentOperator() is not null)
        {
            reason = "nested assignments are outside the first supported subset";
            return false;
        }

        return TryValidateIndependentMemoryTree(expression, scope, inductionName, accesses, out reason);
    }

    private bool TryValidateIndependentMemoryExpression(
        StarkParser.ExpressionContext expression,
        Scope scope,
        string inductionName,
        List<IndependentLoopMemoryAccess> accesses,
        out string reason)
    {
        return TryValidateIndependentMemoryAssignmentExpression(expression.assignmentExpression(), scope, inductionName, accesses, out reason);
    }

    private bool TryValidateIndependentMemoryTree(
        ParserRuleContext context,
        Scope scope,
        string inductionName,
        List<IndependentLoopMemoryAccess> accesses,
        out string reason)
    {
        switch (context)
        {
            case StarkParser.AssignmentExpressionContext assignment when assignment.assignmentOperator() is not null:
                reason = "nested assignments are outside the first supported subset";
                return false;

            case StarkParser.UnaryExpressionContext unary
                when TryCreateIndependentLoopRawPointerDereferenceAccess(unary, scope, inductionName, isWrite: false, out var access, out reason):
                accesses.Add(access);
                return true;

            case StarkParser.UnaryExpressionContext unary when unary.unaryOperator()?.AND() is not null:
                reason = "address-of expressions would introduce memory facts";
                return false;

            case StarkParser.UnaryExpressionContext unary when unary.unaryOperator()?.STAR() is not null:
                reason = "pointer dereferences are memory operations outside the supported independent subset";
                return false;

            case StarkParser.PostfixExpressionContext postfix:
                return TryValidateIndependentMemoryPostfixExpression(postfix, scope, inductionName, accesses, out reason);

            case StarkParser.PrimaryExpressionContext primary when primary.Identifier() is { } identifier:
                return TryValidateIndependentIdentifier(identifier.GetText(), scope, out reason);

            case StarkParser.LiteralContext literal:
                if (literal.StringLiteral() is not null
                    || literal.CharacterLiteral() is not null
                    || literal.NULL() is not null)
                {
                    reason = "only integer, floating-point, and boolean literals are in the first supported subset";
                    return false;
                }

                reason = string.Empty;
                return true;
        }

        for (var i = 0; i < context.ChildCount; i++)
        {
            if (context.GetChild(i) is ParserRuleContext child
                && !TryValidateIndependentMemoryTree(child, scope, inductionName, accesses, out reason))
            {
                return false;
            }
        }

        reason = string.Empty;
        return true;
    }

    private bool TryValidateIndependentMemoryPostfixExpression(
        StarkParser.PostfixExpressionContext postfix,
        Scope scope,
        string inductionName,
        List<IndependentLoopMemoryAccess> accesses,
        out string reason)
    {
        var parts = postfix.postfixPart();
        if (parts.Length == 0)
        {
            if (postfix.primaryExpression().Identifier() is { } identifier)
            {
                return TryValidateIndependentIdentifier(identifier.GetText(), scope, out reason);
            }

            return TryValidateIndependentMemoryTree(postfix.primaryExpression(), scope, inductionName, accesses, out reason);
        }

        if (parts is [var callPart]
            && callPart.argumentList() is { } argumentList
            && postfix.primaryExpression().Identifier()?.GetText() is { } functionName)
        {
            return TryValidateIndependentMemoryLawCall(functionName, argumentList, scope, inductionName, accesses, out reason);
        }

        if (parts.Any(static part => part.argumentList() is not null))
        {
            reason = "calls are outside the first supported independent memory subset";
            return false;
        }

        if (TryCreateIndependentLoopMemoryAccess(postfix, scope, inductionName, isWrite: false, out var access, out reason))
        {
            accesses.Add(access);
            return true;
        }

        if (parts.Any(static part => part.DOT() is not null))
        {
            reason = "member projections in independent memory loops must be rooted at root[index]";
            return false;
        }

        return false;
    }

    private bool TryValidateIndependentMemoryLawCall(
        string functionName,
        StarkParser.ArgumentListContext argumentList,
        Scope scope,
        string inductionName,
        List<IndependentLoopMemoryAccess> accesses,
        out string reason)
    {
        if (!TryGetFunctionOverloads(functionName, out var overloads)
            || overloads.Count == 0
            || overloads.Any(static overload => !FunctionKindFacts.IsLaw(overload.Kind))
            || overloads.Any(static overload => !IsIndependentScalarLocalType(overload.ReturnType)))
        {
            reason = "calls inside memory-backed independent loops must resolve to law functions with scalar return values";
            return false;
        }

        foreach (var argument in argumentList.argument())
        {
            if (!TryValidateIndependentMemoryExpression(argument.expression(), scope, inductionName, accesses, out reason))
            {
                return false;
            }
        }

        reason = string.Empty;
        return true;
    }

    private bool TryCreateIndependentLoopMemoryAccess(
        StarkParser.PostfixExpressionContext postfix,
        Scope scope,
        string inductionName,
        bool isWrite,
        out IndependentLoopMemoryAccess access,
        out string reason)
    {
        access = default;
        var parts = postfix.postfixPart();
        if (postfix.primaryExpression().Identifier()?.GetText() is not { } rootName
            || parts.Length == 0
            || parts[0] is not { } indexPart
            || indexPart.LBRACK() is null
            || indexPart.expressionList()?.expression() is not [var indexExpression])
        {
            reason = "memory accesses in independent loops must use the simple form root[index] or root[index].field";
            return false;
        }

        for (var partIndex = 1; partIndex < parts.Length; partIndex++)
        {
            if (parts[partIndex].DOT() is null
                || parts[partIndex].Identifier()?.GetText() is not { Length: > 0 })
            {
                reason = "memory accesses in independent loops may only project fields after root[index]";
                return false;
            }
        }

        if (!string.Equals(NormalizeExpressionText(indexExpression.GetText()), inductionName, StringComparison.Ordinal))
        {
            reason = "memory accesses in independent loops must use the loop induction variable as their element index";
            return false;
        }

        if (!scope.TryLookup(rootName, out var symbol)
            || !IsIndependentLoopMemoryRootType(symbol))
        {
            reason = $"memory access root '{rootName}' is not a supported memory-backed loop operand";
            return false;
        }

        var rootKey = AppendMemoryRootIndexKey(symbol.MemoryRootKey ?? rootName, indexExpression)
            ?? $"{symbol.MemoryRootKey ?? rootName}[{NormalizeExpressionText(indexExpression.GetText())}]";
        for (var partIndex = 1; partIndex < parts.Length; partIndex++)
        {
            rootKey = $"{rootKey}.{parts[partIndex].Identifier().GetText()}";
        }

        access = new IndependentLoopMemoryAccess(
            rootKey,
            rootName,
            isWrite,
            postfix);
        reason = string.Empty;
        return true;
    }

    private bool TryCreateIndependentLoopRawPointerDereferenceAccess(
        StarkParser.UnaryExpressionContext expression,
        Scope scope,
        string inductionName,
        bool isWrite,
        out IndependentLoopMemoryAccess access,
        out string reason)
    {
        access = default;
        if (expression.unaryOperator()?.STAR() is null
            || expression.unaryExpression() is not { } addressOfExpression
            || !TryGetAddressOfIndexedPostfixExpression(addressOfExpression, out var postfix))
        {
            reason = string.Empty;
            return false;
        }

        if (!TryCreateIndependentLoopMemoryAccess(postfix, scope, inductionName, isWrite, out access, out reason))
        {
            return false;
        }

        if (!scope.TryLookup(access.DisplayName, out var symbol)
            || symbol.Type.Kind != StarkTypeKind.RawPointer
            || symbol.RawPointerElementCountExpression is null)
        {
            reason = $"raw pointer root '{access.DisplayName}' must be a bounded raw pointer parameter";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private static bool TryValidateIndependentLoopMemoryAccesses(
        IReadOnlyList<IndependentLoopMemoryAccess> accesses,
        Scope scope,
        string? loopExclusiveUpperBoundText,
        out string reason)
    {
        for (var leftIndex = 0; leftIndex < accesses.Count; leftIndex++)
        {
            var left = accesses[leftIndex];
            for (var rightIndex = leftIndex + 1; rightIndex < accesses.Count; rightIndex++)
            {
                var right = accesses[rightIndex];
                if (!left.IsWrite && !right.IsWrite)
                {
                    continue;
                }

                var leftRootKey = GetIndependentLoopAccessComparisonRootKey(left, scope, loopExclusiveUpperBoundText);
                var rightRootKey = GetIndependentLoopAccessComparisonRootKey(right, scope, loopExclusiveUpperBoundText);
                if (string.Equals(leftRootKey, rightRootKey, StringComparison.Ordinal)
                    || scope.HasDisjointFact(leftRootKey, rightRootKey))
                {
                    continue;
                }

                reason = $"memory roots '{left.DisplayName}' and '{right.DisplayName}' are not proven disjoint";
                return false;
            }
        }

        reason = string.Empty;
        return true;
    }

    private static string GetIndependentLoopAccessComparisonRootKey(
        IndependentLoopMemoryAccess access,
        Scope scope,
        string? loopExclusiveUpperBoundText)
    {
        if (loopExclusiveUpperBoundText is null
            || !scope.TryLookup(access.DisplayName, out var symbol)
            || symbol.Type.Kind != StarkTypeKind.RawPointer
            || symbol.RawPointerElementCountExpression is null
            || !TryParseMemoryRootPath(access.RootKey, out var path)
            || !TryBuildZeroBasedExclusiveRangeRootKey(path.BaseName, loopExclusiveUpperBoundText, scope, out var rangeRootKey))
        {
            return access.RootKey;
        }

        return $"{rangeRootKey}{BuildMemoryRootSuffix(path, startSegmentIndex: 1)}";
    }

    private static bool TryBuildZeroBasedExclusiveRangeRootKey(
        string rootKey,
        string exclusiveUpperBoundText,
        Scope scope,
        out string rangeRootKey)
    {
        rangeRootKey = string.Empty;
        if (!TryResolveMemoryRootIndexRange(exclusiveUpperBoundText, scope, out _, out var upperMax)
            || upperMax <= BigInteger.Zero)
        {
            return false;
        }

        rangeRootKey = $"{rootKey}[0..{(upperMax - BigInteger.One).ToString(CultureInfo.InvariantCulture)}]";
        return true;
    }

    private static bool TryGetDirectAssignmentTargetName(StarkParser.UnaryExpressionContext target, out string name)
    {
        name = string.Empty;
        if (target.unaryOperator() is not null
            || target.powerExpression()?.postfixExpression() is not { } postfix
            || postfix.postfixPart().Length != 0
            || postfix.primaryExpression().Identifier()?.GetText() is not { } identifier)
        {
            return false;
        }

        name = identifier;
        return true;
    }

    private static bool IsLiteralOneExpression(StarkParser.AssignmentExpressionContext expression)
    {
        return string.Equals(NormalizeExpressionText(expression.GetText()), "1", StringComparison.Ordinal);
    }

    private static bool IsIndependentLoopMemoryRootType(VariableSymbol symbol)
    {
        var type = symbol.Type;
        if (type.Kind == StarkTypeKind.RawPointer)
        {
            return symbol.RawPointerElementCountExpression is not null;
        }

        return type.Kind is StarkTypeKind.Slice or StarkTypeKind.Ascii or StarkTypeKind.Unicode or StarkTypeKind.FixedArray
            || type.BorrowKind != StarkBorrowKind.None
            || type.InitializationKind != StarkInitializationKind.None;
    }

    private bool TryValidateIndependentLoopStatement(
        StarkParser.StatementContext statement,
        Scope scope,
        out string reason)
    {
        if (statement.emptyStatement() is not null)
        {
            reason = string.Empty;
            return true;
        }

        if (statement.block() is { } block)
        {
            var blockScope = new Scope(scope);
            foreach (var nestedStatement in block.statement())
            {
                if (!TryValidateIndependentLoopStatement(nestedStatement, blockScope, out reason))
                {
                    return false;
                }
            }

            reason = string.Empty;
            return true;
        }

        if (statement.localVariableDeclaration() is { } localVariableDeclaration)
        {
            return TryValidateIndependentLocalVariableDeclaration(localVariableDeclaration, scope, out reason);
        }

        if (statement.localConstantDeclaration() is { } localConstantDeclaration)
        {
            return TryValidateIndependentLocalConstantDeclaration(localConstantDeclaration, scope, out reason);
        }

        if (statement.expressionStatement() is { } expressionStatement)
        {
            return TryValidateIndependentLoopExpressionStatement(expressionStatement.expression(), scope, out reason);
        }

        if (statement.ifStatement() is { } ifStatement)
        {
            if (ifStatement.disjointRuntimeCondition() is not null)
            {
                reason = "runtime disjoint tests are memory-backed checks";
                return false;
            }

            if (ifStatement.expression() is not { } condition)
            {
                reason = "runtime disjoint tests are memory-backed checks";
                return false;
            }

            if (!TryValidateIndependentPureExpression(condition, scope, out reason))
            {
                return false;
            }

            if (!TryValidateIndependentLoopStatement(ifStatement.statement(0), new Scope(scope), out reason))
            {
                return false;
            }

            if (ifStatement.statement().Length > 1
                && !TryValidateIndependentLoopStatement(ifStatement.statement(1), new Scope(scope), out reason))
            {
                return false;
            }

            reason = string.Empty;
            return true;
        }

        reason = statement switch
        {
            _ when statement.unsafeStatement() is not null => "unsafe blocks are outside the first supported subset",
            _ when statement.assumeStatement() is not null => "unsafe assumptions are outside the first supported subset",
            _ when statement.switchStatement() is not null => "switch statements are outside the first supported subset",
            _ when statement.whileStatement() is not null => "nested loops are outside the first supported subset",
            _ when statement.forStatement() is not null => "nested loops are outside the first supported subset",
            _ when statement.returnStatement() is not null => "early exits are outside the first supported subset",
            _ when statement.breakStatement() is not null => "early exits are outside the first supported subset",
            _ when statement.continueStatement() is not null => "early exits are outside the first supported subset",
            _ => "the loop body uses an unsupported statement form"
        };
        return false;
    }

    private bool TryValidateIndependentLocalVariableDeclaration(
        StarkParser.LocalVariableDeclarationContext declaration,
        Scope scope,
        out string reason)
    {
        var storageClass = declaration.storageClass().GetText();
        if (!IsIndependentScalarLocalStorageClass(storageClass))
        {
            reason = "local declarations inside independent loops must use stack or register scalar storage";
            return false;
        }

        var declaredType = ResolveType(declaration.type_(), _currentFunctionGenericParameters, _currentFunctionModuleName);
        if (!IsIndependentScalarLocalType(declaredType))
        {
            reason = "local declarations inside independent loops must use scalar local types";
            return false;
        }

        foreach (var declarator in declaration.variableDeclarators().variableDeclarator())
        {
            if (declarator.variableStorageCapacity() is not null)
            {
                reason = "local declarations inside independent loops cannot declare variable-sized storage";
                return false;
            }

            if (declarator.variableInitializer() is { } initializer
                && !TryValidateIndependentVariableInitializer(initializer, scope, out reason))
            {
                return false;
            }

            scope.Declare(new VariableSymbol(
                declarator.Identifier().GetText(),
                declaredType,
                IsMutable: declaration.MUT() is not null,
                IsConstant: false));
        }

        reason = string.Empty;
        return true;
    }

    private bool TryValidateIndependentLocalConstantDeclaration(
        StarkParser.LocalConstantDeclarationContext declaration,
        Scope scope,
        out string reason)
    {
        if (declaration.type_() is null && declaration.INTEGER_TYPE() is null)
        {
            reason = "local constants inside independent loops must declare a scalar type explicitly";
            return false;
        }

        var declaredType = declaration.type_() is { } type
            ? ResolveType(type, _currentFunctionGenericParameters, _currentFunctionModuleName)
            : ResolveConstIntegerStorageType(declaration.INTEGER_TYPE()!.Symbol);
        if (!IsIndependentScalarLocalType(declaredType))
        {
            reason = "local constants inside independent loops must use scalar local types";
            return false;
        }

        foreach (var declarator in declaration.constantDeclarators().constantDeclarator())
        {
            if (!TryValidateIndependentVariableInitializer(declarator.variableInitializer(), scope, out reason))
            {
                return false;
            }

            scope.Declare(new VariableSymbol(
                declarator.Identifier().GetText(),
                declaredType,
                IsMutable: false,
                IsConstant: true));
        }

        reason = string.Empty;
        return true;
    }

    private bool TryValidateIndependentVariableInitializer(
        StarkParser.VariableInitializerContext initializer,
        Scope scope,
        out string reason)
    {
        if (initializer.expression() is { } expression)
        {
            return TryValidateIndependentPureExpression(expression, scope, out reason);
        }

        reason = "object and array initializers are outside the first supported subset";
        return false;
    }

    private bool TryValidateIndependentLoopExpressionStatement(
        StarkParser.ExpressionContext expression,
        Scope scope,
        out string reason)
    {
        var assignment = expression.assignmentExpression();
        if (assignment.assignmentOperator() is null)
        {
            return TryValidateIndependentPureExpression(expression, scope, out reason);
        }

        if (!TryValidateIndependentAssignmentTarget(assignment.unaryExpression(), scope, out reason))
        {
            return false;
        }

        return TryValidateIndependentPureAssignmentExpression(assignment.assignmentExpression(), scope, out reason);
    }

    private bool TryValidateIndependentAssignmentTarget(
        StarkParser.UnaryExpressionContext target,
        Scope scope,
        out string reason)
    {
        if (target.unaryOperator()?.AND() is not null)
        {
            reason = "address-of expressions would introduce memory facts";
            return false;
        }

        if (target.unaryOperator()?.STAR() is not null)
        {
            reason = "pointer dereferences are memory operations";
            return false;
        }

        if (target.powerExpression()?.postfixExpression() is not { } postfix
            || postfix.postfixPart().Length != 0
            || postfix.primaryExpression().Identifier()?.GetText() is not { } name)
        {
            reason = "assignments must target mutable scalar locals directly";
            return false;
        }

        if (!scope.TryLookup(name, out var symbol)
            || symbol.BindingKind is not null
            || !symbol.IsMutable
            || !IsIndependentScalarLocalType(symbol.Type))
        {
            reason = "assignments must target mutable scalar locals directly";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private bool TryValidateIndependentPureExpression(
        StarkParser.ExpressionContext expression,
        Scope scope,
        out string reason)
    {
        return TryValidateIndependentPureAssignmentExpression(expression.assignmentExpression(), scope, out reason);
    }

    private bool TryValidateIndependentPureAssignmentExpression(
        StarkParser.AssignmentExpressionContext expression,
        Scope scope,
        out string reason)
    {
        if (expression.assignmentOperator() is not null)
        {
            reason = "nested assignments are outside the first supported subset";
            return false;
        }

        return TryValidateIndependentPureTree(expression, scope, out reason);
    }

    private bool TryValidateIndependentPureTree(
        ParserRuleContext context,
        Scope scope,
        out string reason)
    {
        switch (context)
        {
            case StarkParser.AssignmentExpressionContext assignment when assignment.assignmentOperator() is not null:
                reason = "nested assignments are outside the first supported subset";
                return false;

            case StarkParser.UnaryExpressionContext unary when unary.unaryOperator()?.AND() is not null:
                reason = "address-of expressions would introduce memory facts";
                return false;

            case StarkParser.UnaryExpressionContext unary when unary.unaryOperator()?.STAR() is not null:
                reason = "pointer dereferences are memory operations";
                return false;

            case StarkParser.PostfixPartContext postfix when postfix.argumentList() is not null:
                reason = "calls are outside the first supported subset";
                return false;

            case StarkParser.PostfixPartContext postfix when postfix.LBRACK() is not null:
                reason = "indexed access is a memory projection";
                return false;

            case StarkParser.PostfixPartContext postfix when postfix.DOT() is not null:
                reason = "member access is a memory projection";
                return false;

            case StarkParser.PrimaryExpressionContext primary when primary.SIZEOF() is not null || primary.ALIGNOF() is not null:
                reason = string.Empty;
                return true;

            case StarkParser.PrimaryExpressionContext primary when primary.lambdaExpression() is not null:
                reason = "lambda expressions are outside the first supported subset";
                return false;

            case StarkParser.PrimaryExpressionContext primary when primary.objectCreationExpression() is not null:
                reason = "object creation is outside the first supported subset";
                return false;

            case StarkParser.PrimaryExpressionContext primary when primary.enumConstructorExpression() is not null
                                                             || primary.genericEnumCaseReference() is not null:
                reason = "enum construction is outside the first supported subset";
                return false;

            case StarkParser.PrimaryExpressionContext primary when primary.qualifiedName() is not null:
                reason = "qualified names are outside the first supported subset";
                return false;

            case StarkParser.PrimaryExpressionContext primary when primary.Identifier() is { } identifier:
                return TryValidateIndependentIdentifier(identifier.GetText(), scope, out reason);

            case StarkParser.LiteralContext literal:
                if (literal.StringLiteral() is not null
                    || literal.CharacterLiteral() is not null
                    || literal.NULL() is not null)
                {
                    reason = "only integer, floating-point, and boolean literals are in the first supported subset";
                    return false;
                }

                reason = string.Empty;
                return true;
        }

        for (var i = 0; i < context.ChildCount; i++)
        {
            if (context.GetChild(i) is ParserRuleContext child
                && !TryValidateIndependentPureTree(child, scope, out reason))
            {
                return false;
            }
        }

        reason = string.Empty;
        return true;
    }

    private static bool TryValidateIndependentIdentifier(
        string name,
        Scope scope,
        out string reason)
    {
        if (!scope.TryLookup(name, out var symbol)
            || symbol.BindingKind is not null
            || !IsIndependentScalarLocalType(symbol.Type))
        {
            reason = "expressions must use scalar local values only";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private static bool IsIndependentScalarLocalType(StarkTypeSymbol type)
    {
        return type.BorrowKind == StarkBorrowKind.None
            && type.AccessKind == StarkAccessKind.None
            && type.InitializationKind == StarkInitializationKind.None
            && type.Kind is StarkTypeKind.Bool or StarkTypeKind.Integer or StarkTypeKind.Float;
    }

    private static bool IsIndependentScalarLocalStorageClass(string storageClass)
    {
        return string.Equals(storageClass, "stack", StringComparison.Ordinal)
            || string.Equals(storageClass, "register", StringComparison.Ordinal);
    }

    private static bool CanRuntimeDisjointTest(StarkTypeSymbol type)
    {
        return ParameterMemoryContractFacts.IsMemoryBacked(type);
    }

    private void ValidateImplementedSwitchShape(StarkParser.SwitchStatementContext switchStatement, StarkTypeSymbol switchType)
    {
        if (!CanLowerImplementedSwitchType(switchType))
        {
            ReportError(
                "STK3008",
                $"Switch expression type '{switchType.DisplayName}' is not a valid switch domain. Stark switch domains are integers, floating-point values, bool, raw pointers, ascii/unicode text literals, named aggregates, enum case patterns, fixed arrays, slices, and dynamic storage.",
                switchStatement.expression());
            return;
        }

        AnalyzeSwitchCoverage(switchStatement, switchType);
    }

    private void ValidateSwitchSectionPatternCaptures(StarkParser.SwitchSectionContext section, StarkTypeSymbol switchType)
    {
        Dictionary<string, StarkTypeSymbol>? expectedCaptures = null;
        ParserRuleContext? expectedContext = null;

        foreach (var label in section.switchLabel())
        {
            if (label.DEFAULT() is not null)
            {
                var captures = new Dictionary<string, StarkTypeSymbol>(StringComparer.Ordinal);
                if (!ValidateSameSwitchSectionCaptures(
                        expectedCaptures,
                        ref expectedContext,
                        captures,
                        label,
                        out expectedCaptures))
                {
                    return;
                }

                continue;
            }

            foreach (var pattern in label.pattern())
            {
                if (!ValidateNoDuplicateSwitchPatternCaptures(pattern))
                {
                    continue;
                }

                if (!TryCollectPatternCaptures(pattern, switchType, out var captures))
                {
                    continue;
                }

                if (!ValidateSameSwitchSectionCaptures(
                        expectedCaptures,
                        ref expectedContext,
                        captures,
                        pattern,
                        out expectedCaptures))
                {
                    return;
                }
            }
        }
    }

    private bool ValidateNoDuplicateSwitchPatternCaptures(StarkParser.PatternContext pattern)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return ValidateNoDuplicateSwitchPatternCapturesCore(pattern, seen);
    }

    private bool ValidateNoDuplicateSwitchPatternCapturesCore(IParseTree node, HashSet<string> seen)
    {
        var isValid = true;
        if (node is StarkParser.PatternContext pattern
            && pattern.VAR() is not null
            && pattern.Identifier() is { } patternCapture
            && !seen.Add(patternCapture.GetText()))
        {
            ReportError(
                "STK3008",
                $"Switch pattern capture '{patternCapture.GetText()}' is bound more than once in the same pattern. Use distinct capture names or discard the duplicate field with '_'.",
                patternCapture.Symbol);
            isValid = false;
        }

        if (node is StarkParser.AggregatePatternSuffixContext suffix
            && suffix.Identifier() is { } wholeCapture
            && !seen.Add(wholeCapture.GetText()))
        {
            ReportError(
                "STK3008",
                $"Switch pattern capture '{wholeCapture.GetText()}' is bound more than once in the same pattern. Use distinct capture names or discard the duplicate field with '_'.",
                wholeCapture.Symbol);
            isValid = false;
        }

        for (var index = 0; index < node.ChildCount; index++)
        {
            if (!ValidateNoDuplicateSwitchPatternCapturesCore(node.GetChild(index), seen))
            {
                isValid = false;
            }
        }

        return isValid;
    }

    private bool ValidateSameSwitchSectionCaptures(
        Dictionary<string, StarkTypeSymbol>? expectedCaptures,
        ref ParserRuleContext? expectedContext,
        Dictionary<string, StarkTypeSymbol> captures,
        ParserRuleContext context,
        out Dictionary<string, StarkTypeSymbol> updatedExpectedCaptures)
    {
        if (expectedCaptures is null)
        {
            updatedExpectedCaptures = captures;
            expectedContext = context;
            return true;
        }

        updatedExpectedCaptures = expectedCaptures;
        if (HaveSameSwitchSectionCaptures(expectedCaptures, captures))
        {
            return true;
        }

        var expectedNames = FormatSwitchCaptureNames(expectedCaptures);
        var actualNames = FormatSwitchCaptureNames(captures);
        ReportError(
            "STK3008",
            $"Switch labels that share a body must bind the same capture names with the same types. "
                + $"Earlier label binds {expectedNames}; this label binds {actualNames}. Split the labels into separate sections or make their captures match.",
            context);

        if (expectedContext is not null)
        {
            ReportInfo(
                "STK3020",
                $"This earlier switch label established the capture set {expectedNames} for the shared body.",
                expectedContext);
        }

        return false;
    }

    private static bool HaveSameSwitchSectionCaptures(
        IReadOnlyDictionary<string, StarkTypeSymbol> expected,
        IReadOnlyDictionary<string, StarkTypeSymbol> actual)
    {
        if (expected.Count != actual.Count)
        {
            return false;
        }

        foreach (var (name, expectedType) in expected)
        {
            if (!actual.TryGetValue(name, out var actualType)
                || !StarkTypeSymbolsHaveSameIdentity(expectedType, actualType))
            {
                return false;
            }
        }

        return true;
    }

    private static string FormatSwitchCaptureNames(IReadOnlyDictionary<string, StarkTypeSymbol> captures)
    {
        if (captures.Count == 0)
        {
            return "no captures";
        }

        return string.Join(
            ", ",
            captures
                .OrderBy(static capture => capture.Key, StringComparer.Ordinal)
                .Select(static capture => $"'{capture.Key}: {capture.Value.DisplayName}'"));
    }

    private bool TryCollectPatternCaptures(
        StarkParser.PatternContext pattern,
        StarkTypeSymbol valueType,
        out Dictionary<string, StarkTypeSymbol> captures)
    {
        captures = new Dictionary<string, StarkTypeSymbol>(StringComparer.Ordinal);
        return TryCollectPatternCaptures(pattern, valueType, captures);
    }

    private bool TryCollectPatternCaptures(
        StarkParser.PatternContext pattern,
        StarkTypeSymbol valueType,
        Dictionary<string, StarkTypeSymbol> captures)
    {
        if (pattern.literal() is not null || pattern.rangePattern() is not null || pattern.DISCARD() is not null)
        {
            return true;
        }

        if (pattern.listPattern() is { } listPattern)
        {
            return TryCollectListPatternCaptures(listPattern, valueType, captures);
        }

        if (pattern.VAR() is not null)
        {
            return IsEnumSwitchType(valueType)
                || TryAddPatternCapture(captures, pattern.Identifier().GetText(), valueType);
        }

        if (pattern.enumNamedFieldPattern() is { } enumNamedFieldPattern)
        {
            return TryCollectEnumNamedFieldPatternCaptures(enumNamedFieldPattern, valueType, captures);
        }

        if (pattern.genericEnumAggregatePattern() is { } genericEnumAggregatePattern)
        {
            return TryCollectGenericEnumAggregatePatternCaptures(genericEnumAggregatePattern, valueType, captures);
        }

        if (pattern.aggregatePattern() is { } aggregatePattern)
        {
            if (TryCollectEnumAggregatePatternCaptures(aggregatePattern, valueType, captures))
            {
                return true;
            }

            return TryCollectAggregatePatternCaptures(aggregatePattern, valueType, captures);
        }

        return true;
    }

    private bool TryCollectAggregatePatternCaptures(
        StarkParser.AggregatePatternContext aggregatePattern,
        StarkTypeSymbol valueType,
        Dictionary<string, StarkTypeSymbol> captures)
    {
        StarkTypeSymbol patternType;
        NamedTypeSymbol namedType;

        if (TryGetPublishedTemplateAggregatePattern(
                aggregatePattern,
                out var publishedPatternType,
                out var publishedNamedType))
        {
            patternType = publishedPatternType;
            namedType = publishedNamedType;
        }
        else
        {
            patternType = ResolveSimpleType(aggregatePattern.simpleType());
            if (valueType.Kind != StarkTypeKind.Named
                || patternType.Kind != StarkTypeKind.Named
                || valueType.NamedType is null
                || patternType.NamedType is null
                || !string.Equals(valueType.NamedType, patternType.NamedType, StringComparison.Ordinal)
                || !_namedTypes.TryGetValue(valueType.NamedType, out namedType!))
            {
                return false;
            }
        }

        if (valueType.Kind != StarkTypeKind.Named
            || patternType.Kind != StarkTypeKind.Named
            || valueType.NamedType is null
            || patternType.NamedType is null
            || !string.Equals(valueType.NamedType, patternType.NamedType, StringComparison.Ordinal)
            || namedType.Kind == DeclarationKind.Enum)
        {
            return false;
        }

        var suffix = aggregatePattern.aggregatePatternSuffix();
        if (suffix is null)
        {
            return true;
        }

        if (suffix.Identifier() is { } wholeCapture)
        {
            return TryAddPatternCapture(captures, wholeCapture.GetText(), valueType);
        }

        if (suffix.namedPatternPayload() is { } namedPayload)
        {
            return TryCollectAggregateNamedFieldPatternCaptures(namedPayload, namedType, captures);
        }

        var fieldPatterns = suffix.pattern();
        if (fieldPatterns.Length != namedType.OrderedFields.Count)
        {
            return false;
        }

        for (var index = 0; index < fieldPatterns.Length; index++)
        {
            if (!TryCollectPatternCaptures(fieldPatterns[index], namedType.OrderedFields[index].Type, captures))
            {
                return false;
            }
        }

        return true;
    }

    private bool TryCollectAggregateNamedFieldPatternCaptures(
        StarkParser.NamedPatternPayloadContext namedPayload,
        NamedTypeSymbol namedType,
        Dictionary<string, StarkTypeSymbol> captures)
    {
        var members = namedPayload.namedPatternMember();
        if (members.Length != namedType.OrderedFields.Count)
        {
            return false;
        }

        var seenMembers = new HashSet<int>();
        foreach (var member in members)
        {
            var memberName = member.Identifier().GetText();
            var fieldIndex = FindOrderedAggregateFieldIndex(namedType, memberName);
            if (fieldIndex < 0 || !seenMembers.Add(fieldIndex))
            {
                return false;
            }

            if (!TryCollectPatternCaptures(member.pattern(), namedType.OrderedFields[fieldIndex].Type, captures))
            {
                return false;
            }
        }

        return seenMembers.Count == namedType.OrderedFields.Count;
    }

    private static int FindOrderedAggregateFieldIndex(NamedTypeSymbol namedType, string fieldName)
    {
        for (var index = 0; index < namedType.OrderedFields.Count; index++)
        {
            if (string.Equals(namedType.OrderedFields[index].Name, fieldName, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }

    private static bool TryGetListPatternElementType(
        StarkTypeSymbol valueType,
        out StarkTypeSymbol elementType,
        out int? fixedLength)
    {
        elementType = StarkTypeSymbols.Error;
        fixedLength = null;

        if (valueType.Kind is not (StarkTypeKind.FixedArray or StarkTypeKind.Slice or StarkTypeKind.Dynamic)
            || valueType.ElementType is not { } resolvedElementType)
        {
            return false;
        }

        elementType = resolvedElementType;
        fixedLength = valueType.Kind == StarkTypeKind.FixedArray ? valueType.FixedLength : null;
        return true;
    }

    private bool TryResolveAggregatePropertyPatternTarget(
        string patternTypeName,
        StarkTypeSymbol valueType,
        out NamedTypeSymbol namedType)
    {
        namedType = null!;
        return valueType.Kind == StarkTypeKind.Named
            && valueType.NamedType is not null
            && TryResolveNamedTypeBySourceName(patternTypeName, out namedType)
            && namedType.Kind != DeclarationKind.Enum
            && string.Equals(valueType.NamedType, namedType.Name, StringComparison.Ordinal);
    }

    private bool TryCollectListPatternCaptures(
        StarkParser.ListPatternContext listPattern,
        StarkTypeSymbol valueType,
        Dictionary<string, StarkTypeSymbol> captures)
    {
        if (!TryGetListPatternElementType(valueType, out var elementType, out _))
        {
            return false;
        }

        var elementPatterns = listPattern.pattern();
        if (valueType.Kind == StarkTypeKind.FixedArray
            && valueType.FixedLength is int fixedLength
            && elementPatterns.Length != fixedLength)
        {
            return false;
        }

        foreach (var elementPattern in elementPatterns)
        {
            if (!TryCollectPatternCaptures(elementPattern, elementType, captures))
            {
                return false;
            }
        }

        return true;
    }

    private bool TryCollectEnumAggregatePatternCaptures(
        StarkParser.AggregatePatternContext aggregatePattern,
        StarkTypeSymbol valueType,
        Dictionary<string, StarkTypeSymbol> captures)
    {
        if (TryGetPublishedTemplateEnumPattern(
                aggregatePattern,
                out _,
                out var publishedEnumTypeSymbol,
                out var publishedEnumType,
                out var publishedVariant))
        {
            return TryCollectResolvedEnumAggregatePatternCaptures(
                aggregatePattern.aggregatePatternSuffix(),
                valueType,
                publishedEnumTypeSymbol,
                publishedEnumType,
                publishedVariant,
                captures);
        }

        var caseName = aggregatePattern.simpleType().GetText();
        return TryResolveEnumCaseReference(caseName, out var enumType, out var enumTypeSymbol, out var variant)
               && TryCollectResolvedEnumAggregatePatternCaptures(
                   aggregatePattern.aggregatePatternSuffix(),
                   valueType,
                   enumTypeSymbol,
                   enumType,
                   variant,
                   captures);
    }

    private bool TryCollectGenericEnumAggregatePatternCaptures(
        StarkParser.GenericEnumAggregatePatternContext genericEnumAggregatePattern,
        StarkTypeSymbol valueType,
        Dictionary<string, StarkTypeSymbol> captures)
    {
        if (TryGetPublishedTemplateEnumPattern(
                genericEnumAggregatePattern,
                out _,
                out var publishedEnumTypeSymbol,
                out var publishedEnumType,
                out var publishedVariant))
        {
            return TryCollectResolvedEnumAggregatePatternCaptures(
                genericEnumAggregatePattern.aggregatePatternSuffix(),
                valueType,
                publishedEnumTypeSymbol,
                publishedEnumType,
                publishedVariant,
                captures);
        }

        return TryResolveEnumCaseReference(genericEnumAggregatePattern.genericEnumCaseReference(), out var enumType, out var enumTypeSymbol, out var variant)
               && TryCollectResolvedEnumAggregatePatternCaptures(
                   genericEnumAggregatePattern.aggregatePatternSuffix(),
                   valueType,
                   enumTypeSymbol,
                   enumType,
                   variant,
                   captures);
    }

    private bool TryCollectResolvedEnumAggregatePatternCaptures(
        StarkParser.AggregatePatternSuffixContext? suffix,
        StarkTypeSymbol valueType,
        StarkTypeSymbol enumTypeSymbol,
        NamedTypeSymbol enumType,
        EnumVariantSymbol variant,
        Dictionary<string, StarkTypeSymbol> captures)
    {
        if (valueType.Kind != StarkTypeKind.Named
            || valueType.NamedType is null
            || !string.Equals(valueType.NamedType, enumType.Name, StringComparison.Ordinal)
            || variant.UsesNamedFields)
        {
            return false;
        }

        if (variant.IsUnit)
        {
            return suffix is null;
        }

        if (suffix is null)
        {
            return false;
        }

        if (suffix.Identifier() is { } wholeCapture)
        {
            return TryAddPatternCapture(captures, wholeCapture.GetText(), valueType);
        }

        var fieldPatterns = suffix.pattern();
        if (fieldPatterns.Length != variant.Fields.Count)
        {
            return false;
        }

        for (var index = 0; index < fieldPatterns.Length; index++)
        {
            if (!TryCollectPatternCaptures(fieldPatterns[index], variant.Fields[index].Type, captures))
            {
                return false;
            }
        }

        return true;
    }

    private bool TryCollectEnumNamedFieldPatternCaptures(
        StarkParser.EnumNamedFieldPatternContext enumNamedFieldPattern,
        StarkTypeSymbol valueType,
        Dictionary<string, StarkTypeSymbol> captures)
    {
        ImportedTemplateEnumPatternSummary? publishedPattern = null;
        NamedTypeSymbol enumType;
        EnumVariantSymbol variant;

        if (TryGetPublishedTemplateEnumPattern(
                enumNamedFieldPattern,
                out var publishedSummary,
                out _,
                out var publishedEnumType,
                out var publishedVariant))
        {
            publishedPattern = publishedSummary;
            enumType = publishedEnumType;
            variant = publishedVariant;
        }
        else if (!TryResolveEnumCaseTarget(enumNamedFieldPattern.enumCaseTarget(), out _, out enumType, out _, out variant))
        {
            return TryResolveAggregatePropertyPatternTarget(
                    enumNamedFieldPattern.enumCaseTarget().GetText(),
                    valueType,
                    out var aggregateType)
                && TryCollectAggregateNamedFieldPatternCaptures(
                    enumNamedFieldPattern.namedPatternPayload(),
                    aggregateType,
                    captures);
        }

        if (valueType.Kind != StarkTypeKind.Named
            || valueType.NamedType is null
            || !string.Equals(valueType.NamedType, enumType.Name, StringComparison.Ordinal)
            || !variant.UsesNamedFields)
        {
            return false;
        }

        var members = enumNamedFieldPattern.namedPatternPayload().namedPatternMember();
        var seenMembers = new HashSet<int>();
        for (var memberOrdinal = 0; memberOrdinal < members.Length; memberOrdinal++)
        {
            var member = members[memberOrdinal];
            EnumVariantFieldSymbol? field;

            if (publishedPattern is { Members.Count: > 0 } && memberOrdinal < publishedPattern.Members.Count)
            {
                var publishedMember = publishedPattern.Members[memberOrdinal];
                field = publishedMember.FieldIndex >= 0 && publishedMember.FieldIndex < variant.Fields.Count
                    ? variant.Fields[publishedMember.FieldIndex]
                    : null;
            }
            else
            {
                var memberName = member.Identifier().GetText();
                field = variant.Fields.FirstOrDefault(candidate => string.Equals(candidate.Name, memberName, StringComparison.Ordinal));
            }

            if (field is null
                || !seenMembers.Add(field.Position)
                || !TryCollectPatternCaptures(member.pattern(), field.Type, captures))
            {
                return false;
            }
        }

        return seenMembers.Count == variant.Fields.Count;
    }

    private static bool TryAddPatternCapture(
        Dictionary<string, StarkTypeSymbol> captures,
        string name,
        StarkTypeSymbol type)
    {
        if (captures.TryGetValue(name, out var existingType))
        {
            return StarkTypeSymbolsHaveSameIdentity(existingType, type);
        }

        captures.Add(name, type);
        return true;
    }

    private static bool StarkTypeSymbolsHaveSameIdentity(StarkTypeSymbol left, StarkTypeSymbol right)
    {
        return left == right
            || string.Equals(left.DisplayName, right.DisplayName, StringComparison.Ordinal)
            && string.Equals(left.NamedType, right.NamedType, StringComparison.Ordinal);
    }

    private void RecordSwitch(StarkParser.SwitchStatementContext switchStatement, StarkTypeSymbol switchType)
    {
        var shape = InspectSwitchShape(switchStatement);
        var family = ClassifySwitchFamily(switchType, shape);
        var location = Location(switchStatement);
        _switches.Add(new SwitchTypingRecord(
            family,
            switchType,
            shape.SectionCount,
            shape.LabelCount,
            shape.ExplicitDefaultLabelCount,
            shape.LoweredDefaultLabelCount,
            shape.LiteralLabelCount,
            shape.MatchAllLabelCount,
            shape.CaptureLabelCount,
            shape.StructuredPatternLabelCount,
            shape.GuardedLabelCount,
            location,
            _currentFunctionName));
        _boundOperations.Add(new BoundSwitchDispatchOperation(
            family,
            switchType,
            shape.SectionCount,
            shape.LabelCount,
            shape.ExplicitDefaultLabelCount,
            shape.LoweredDefaultLabelCount,
            shape.LiteralLabelCount,
            shape.MatchAllLabelCount,
            shape.CaptureLabelCount,
            shape.StructuredPatternLabelCount,
            shape.GuardedLabelCount,
            location,
            _currentFunctionName));
    }

    private static string ClassifySwitchFamily(StarkTypeSymbol switchType, SwitchSourceShape shape)
    {
        if (CanUseFastLiteralSwitch(shape))
        {
            if (switchType.Kind is StarkTypeKind.Integer or StarkTypeKind.Bool)
            {
                return SwitchLoweringFamilies.Native;
            }

            if (switchType.Kind is StarkTypeKind.Ascii or StarkTypeKind.Unicode)
            {
                return SwitchLoweringFamilies.PartitionedText;
            }
        }

        return SwitchLoweringFamilies.Guarded;
    }

    private static bool CanUseFastLiteralSwitch(SwitchSourceShape shape)
    {
        return shape.LoweredDefaultLabelCount <= 1
            && shape.GuardedLabelCount == 0
            && shape.CaptureLabelCount == 0
            && shape.StructuredPatternLabelCount == 0
            && shape.LiteralLabelCount > 0
            && shape.LabelCount - shape.LoweredDefaultLabelCount == shape.LiteralLabelCount;
    }

    private static SwitchSourceShape InspectSwitchShape(StarkParser.SwitchStatementContext switchStatement)
    {
        var sectionCount = switchStatement.switchSection().Length;
        var labelCount = 0;
        var explicitDefaultLabelCount = 0;
        var loweredDefaultLabelCount = 0;
        var literalLabelCount = 0;
        var matchAllLabelCount = 0;
        var captureLabelCount = 0;
        var structuredPatternLabelCount = 0;
        var guardedLabelCount = 0;

        foreach (var section in switchStatement.switchSection())
        {
            foreach (var label in section.switchLabel())
            {
                if (label.DEFAULT() is not null)
                {
                    labelCount++;
                    explicitDefaultLabelCount++;
                    loweredDefaultLabelCount++;
                    continue;
                }

                foreach (var pattern in label.pattern())
                {
                    labelCount++;
                    if (label.whenClause() is not null)
                    {
                        guardedLabelCount++;
                    }

                    if (pattern.literal() is not null)
                    {
                        literalLabelCount++;
                        continue;
                    }

                    if (pattern.DISCARD() is not null)
                    {
                        matchAllLabelCount++;
                        if (label.whenClause() is null)
                        {
                            loweredDefaultLabelCount++;
                        }

                        continue;
                    }

                    if (pattern.VAR() is not null)
                    {
                        matchAllLabelCount++;
                        captureLabelCount++;
                        continue;
                    }

                    if (pattern.aggregatePattern() is not null
                        || pattern.enumNamedFieldPattern() is not null
                        || pattern.genericEnumAggregatePattern() is not null
                        || pattern.listPattern() is not null)
                    {
                        structuredPatternLabelCount++;
                    }
                }
            }
        }

        return new SwitchSourceShape(
            sectionCount,
            labelCount,
            explicitDefaultLabelCount,
            loweredDefaultLabelCount,
            literalLabelCount,
            matchAllLabelCount,
            captureLabelCount,
            structuredPatternLabelCount,
            guardedLabelCount);
    }

    private void AnalyzeSwitchCoverage(StarkParser.SwitchStatementContext switchStatement, StarkTypeSymbol switchType)
    {
        var coveragePatterns = new List<SwitchCoveragePattern>();
        SwitchCoveragePattern? exhaustivePattern = null;
        var boolTrueCovered = false;
        var boolFalseCovered = false;
        var exhaustiveEnumVariants = new HashSet<string>(StringComparer.Ordinal);
        var enumVariantCount = 0;
        NamedTypeSymbol? switchEnumType = null;
        var coveredIntegerIntervals = new List<RangeCoverageInterval>();
        var coveredIntegerValueCount = BigInteger.Zero;

        if (switchType.Kind == StarkTypeKind.Named
            && switchType.NamedType is not null
            && _namedTypes.TryGetValue(switchType.NamedType, out var switchNamedType)
            && switchNamedType.Kind == DeclarationKind.Enum)
        {
            switchEnumType = switchNamedType;
            enumVariantCount = switchNamedType.Variants.Count;
        }

        foreach (var section in switchStatement.switchSection())
        {
            foreach (var label in section.switchLabel())
            {
                if (label.DEFAULT() is not null)
                {
                    var defaultPattern = new SwitchCoveragePattern(
                        SwitchCoveragePatternKind.MatchAll,
                        "default",
                        label,
                        LiteralKey: null,
                        AggregatePattern: null,
                        EnumPattern: null);

                    if (exhaustivePattern is not null)
                    {
                        ReportUnreachableSwitchLabel(label, "default", exhaustivePattern, switchType, becauseExhaustive: true);
                        continue;
                    }

                    var coveringPattern = coveragePatterns.FirstOrDefault(existing => Covers(existing, defaultPattern));
                    if (coveringPattern is not null)
                    {
                        ReportUnreachableSwitchLabel(label, "default", coveringPattern, switchType, becauseExhaustive: false);
                        continue;
                    }

                    if (label.whenClause() is null)
                    {
                        coveragePatterns.Add(defaultPattern);
                        exhaustivePattern = defaultPattern;
                    }

                    continue;
                }

                foreach (var switchPattern in label.pattern())
                {
                    var labelText = switchPattern.GetText();
                    _ = TryCreateSwitchCoveragePattern(switchPattern, switchType, out var currentPattern);

                    if (exhaustivePattern is not null)
                    {
                        ReportUnreachableSwitchLabel(switchPattern, labelText, exhaustivePattern, switchType, becauseExhaustive: true);
                        continue;
                    }

                    if (currentPattern is not null)
                    {
                        var coveringPattern = coveragePatterns.FirstOrDefault(existing => Covers(existing, currentPattern));
                        if (coveringPattern is not null)
                        {
                            ReportUnreachableSwitchLabel(switchPattern, labelText, coveringPattern, switchType, becauseExhaustive: false);
                            continue;
                        }
                    }

                    if (label.whenClause() is not null || currentPattern is null)
                    {
                        continue;
                    }

                    coveragePatterns.Add(currentPattern);
                    if (currentPattern.Kind == SwitchCoveragePatternKind.MatchAll)
                    {
                        exhaustivePattern = currentPattern;
                        continue;
                    }

                    if (currentPattern.Kind == SwitchCoveragePatternKind.Aggregate
                        && currentPattern.AggregatePattern is not null
                        && IsMatchAllAggregatePattern(currentPattern.AggregatePattern))
                    {
                        exhaustivePattern = currentPattern;
                        continue;
                    }

                    if (currentPattern.Kind == SwitchCoveragePatternKind.EnumCase
                        && currentPattern.EnumPattern is not null
                        && IsMatchAllEnumPattern(currentPattern.EnumPattern))
                    {
                        exhaustiveEnumVariants.Add(currentPattern.EnumPattern.VariantName);
                        if (enumVariantCount != 0 && exhaustiveEnumVariants.Count == enumVariantCount)
                        {
                            exhaustivePattern = currentPattern;
                            continue;
                        }
                    }

                    if (currentPattern.Kind == SwitchCoveragePatternKind.List
                        && currentPattern.ListPattern is not null
                        && currentPattern.ListPattern.CanBeExhaustiveForTarget
                        && IsMatchAllListPattern(currentPattern.ListPattern))
                    {
                        exhaustivePattern = currentPattern;
                        continue;
                    }

                    if (currentPattern.Kind == SwitchCoveragePatternKind.Literal
                        && switchType.Kind == StarkTypeKind.Bool)
                    {
                        boolTrueCovered |= string.Equals(currentPattern.LiteralKey, BoolTrueCoverageKey, StringComparison.Ordinal);
                        boolFalseCovered |= string.Equals(currentPattern.LiteralKey, BoolFalseCoverageKey, StringComparison.Ordinal);
                        if (boolTrueCovered && boolFalseCovered)
                        {
                            exhaustivePattern = currentPattern;
                        }
                    }

                    // Integer switches can be exhaustive by covering every value of the
                    // scrutinee's (possibly ranged) type, e.g. `u8[0 3]` with cases 0..3
                    // or a range pattern such as `case 0..3:`.
                    if ((currentPattern.Kind is SwitchCoveragePatternKind.Literal or SwitchCoveragePatternKind.Range)
                        && switchType.Kind == StarkTypeKind.Integer
                        && currentPattern.RangeInterval is { } currentIntegerInterval
                        && StarkTypeSymbols.TryGetEffectiveIntegerBounds(switchType, out var rangeMin, out var rangeMax))
                    {
                        AddIntegerCoverageInterval(
                            coveredIntegerIntervals,
                            ClipRangeToDomain(currentIntegerInterval, rangeMin, rangeMax));
                        coveredIntegerValueCount = CountIntegerCoverageValues(coveredIntegerIntervals);
                        if (coveredIntegerValueCount == rangeMax - rangeMin + 1)
                        {
                            exhaustivePattern = currentPattern;
                        }
                    }
                }
            }
        }

        // Every switch must be exhaustive: a value that matches no arm has nowhere to go
        // (a hidden trap at best, undefined fall-through at worst), which is exactly the
        // kind of invalid state Stark makes unrepresentable. Cover the whole domain or
        // declare the remainder explicitly with `default`.
        if (exhaustivePattern is not null)
        {
            _exhaustiveSwitches.Add(switchStatement);
            return;
        }

        ReportError(
            "STK3044",
            BuildNonExhaustiveSwitchMessage(switchType, switchEnumType, exhaustiveEnumVariants, coveredIntegerValueCount, boolTrueCovered, boolFalseCovered),
            switchStatement.expression());
    }

    private static string BuildNonExhaustiveSwitchMessage(
        StarkTypeSymbol switchType,
        NamedTypeSymbol? switchEnumType,
        IReadOnlySet<string> coveredEnumVariants,
        BigInteger coveredIntegerValueCount,
        bool boolTrueCovered,
        bool boolFalseCovered)
    {
        if (switchEnumType is not null)
        {
            var missingVariants = switchEnumType.Variants
                .Select(static variant => variant.Name)
                .Where(variant => !coveredEnumVariants.Contains(variant))
                .ToArray();
            var missingText = missingVariants.Length <= 4
                ? string.Join(", ", missingVariants.Select(static variant => $"'{variant}'"))
                : string.Join(", ", missingVariants.Take(4).Select(static variant => $"'{variant}'")) + $", … ({missingVariants.Length} total)";
            return $"Switch over '{switchType.DisplayName}' is not exhaustive: variant(s) {missingText} are not covered. "
                + "Add the missing case(s) or a `default` arm.";
        }

        if (switchType.Kind == StarkTypeKind.Bool)
        {
            var missing = !boolTrueCovered && !boolFalseCovered ? "'true' and 'false'" : boolTrueCovered ? "'false'" : "'true'";
            return $"Switch over 'bool' is not exhaustive: {missing} not covered. Add the missing case(s) or a `default` arm.";
        }

        if (switchType.Kind == StarkTypeKind.Integer
            && StarkTypeSymbols.TryGetEffectiveIntegerBounds(switchType, out var rangeMin, out var rangeMax))
        {
            var rangeSize = rangeMax - rangeMin + 1;
            return $"Switch over '{switchType.DisplayName}' is not exhaustive: {coveredIntegerValueCount} of {rangeSize} possible value(s) covered. "
                + "Cover the full range or add a `default` arm.";
        }

        return $"Switch over '{switchType.DisplayName}' is not exhaustive: values of this type cannot be enumerated by cases. "
            + "Add a `default` arm or a match-all pattern (`var`/`_`).";
    }

    private static RangeCoverageInterval ClipRangeToDomain(
        RangeCoverageInterval interval,
        BigInteger domainMin,
        BigInteger domainMax)
    {
        return new RangeCoverageInterval(
            BigInteger.Max(interval.Min, domainMin),
            BigInteger.Min(interval.Max, domainMax));
    }

    private static void AddIntegerCoverageInterval(
        List<RangeCoverageInterval> intervals,
        RangeCoverageInterval interval)
    {
        if (interval.Min > interval.Max)
        {
            return;
        }

        intervals.Add(interval);
        intervals.Sort(static (left, right) =>
        {
            var minComparison = left.Min.CompareTo(right.Min);
            return minComparison != 0 ? minComparison : left.Max.CompareTo(right.Max);
        });

        var writeIndex = 0;
        for (var readIndex = 0; readIndex < intervals.Count; readIndex++)
        {
            var current = intervals[readIndex];
            if (writeIndex == 0)
            {
                intervals[writeIndex++] = current;
                continue;
            }

            var previous = intervals[writeIndex - 1];
            if (current.Min <= previous.Max + BigInteger.One)
            {
                intervals[writeIndex - 1] = previous with
                {
                    Max = BigInteger.Max(previous.Max, current.Max)
                };
                continue;
            }

            intervals[writeIndex++] = current;
        }

        if (writeIndex < intervals.Count)
        {
            intervals.RemoveRange(writeIndex, intervals.Count - writeIndex);
        }
    }

    private static BigInteger CountIntegerCoverageValues(IReadOnlyList<RangeCoverageInterval> intervals)
    {
        var count = BigInteger.Zero;
        foreach (var interval in intervals)
        {
            count += interval.Max - interval.Min + BigInteger.One;
        }

        return count;
    }

    /// <summary>
    /// Definite-return analysis: a function with a non-void return type must return on
    /// every control-flow path. Falling off the end of the body has no value to return —
    /// previously that was silent undefined behavior. Reported as STK3045.
    /// </summary>
    private void ValidateFunctionReturnsOnAllPaths(
        StarkParser.BlockContext block,
        StarkTypeSymbol returnType,
        string functionDescription,
        ParserRuleContext context)
    {
        if (returnType.Kind is StarkTypeKind.Void or StarkTypeKind.Error)
        {
            return;
        }

        if (BlockGuaranteesFunctionExit(block))
        {
            return;
        }

        ReportError(
            "STK3045",
            $"{functionDescription} returns '{returnType.DisplayName}' but control can reach the end of the body without "
                + "returning a value. End every path with `return`, an exhaustive `switch` whose sections all return, an "
                + "`if`/`else` whose branches both return, or an `infinite` loop.",
            context);
    }

    private bool BlockGuaranteesFunctionExit(StarkParser.BlockContext block)
    {
        var statements = block.statement();
        return statements.Length != 0 && StatementGuaranteesFunctionExit(statements[^1]);
    }

    /// <summary>
    /// True when control cannot flow past <paramref name="statement"/> to the next
    /// statement in sequence: it returns, every branch/section of it returns, or it loops
    /// forever. Conservative — anything unproven counts as falling through.
    /// </summary>
    private bool StatementGuaranteesFunctionExit(StarkParser.StatementContext statement)
    {
        if (statement.returnStatement() is not null)
        {
            return true;
        }

        if (statement.block() is { } nestedBlock)
        {
            return BlockGuaranteesFunctionExit(nestedBlock);
        }

        if (statement.unsafeStatement() is { } unsafeStatement)
        {
            if (unsafeStatement.block() is { } unsafeBlock)
            {
                return BlockGuaranteesFunctionExit(unsafeBlock);
            }

            return unsafeStatement.assumeStatement() is { } unsafeAssumeStatement
                && StatementGuaranteesFunctionExit(unsafeAssumeStatement.statement());
        }

        // `assume disjoint(...) stmt` always executes its body — the assumption is a
        // declared fact, not a runtime condition — so it exits iff its body exits.
        if (statement.assumeStatement() is { } assumeStatement)
        {
            return StatementGuaranteesFunctionExit(assumeStatement.statement());
        }

        if (statement.ifStatement() is { } ifStatement)
        {
            return ifStatement.statement().Length > 1
                && StatementGuaranteesFunctionExit(ifStatement.statement(0))
                && StatementGuaranteesFunctionExit(ifStatement.statement(1));
        }

        var labeledStatement = statement.labeledStatement();
        var statementSwitch = statement.switchStatement() ?? labeledStatement?.switchStatement();
        var statementWhile = statement.whileStatement() ?? labeledStatement?.whileStatement();
        var statementFor = statement.forStatement() ?? labeledStatement?.forStatement();
        var statementLabel = labeledStatement?.Identifier().GetText();

        if (statementSwitch is { } switchStatement)
        {
            if (!_exhaustiveSwitches.Contains(switchStatement))
            {
                return false;
            }

            foreach (var section in switchStatement.switchSection())
            {
                var sectionStatements = section.statement();
                if (sectionStatements.Length == 0 || !StatementGuaranteesFunctionExit(sectionStatements[^1]))
                {
                    return false;
                }
            }

            return true;
        }

        // A loop only falls through when its condition can turn false or its body can
        // `break`. That makes two never-falling-through shapes: `infinite` loops (which
        // additionally forbid structural exits), and the loop-until-return idiom — a
        // literal `true` condition (or no `for` condition) with no top-level break, whose
        // only exits are `return`s.
        if (statementWhile is { } whileStatement)
        {
            var whileAlwaysRepeats = whileStatement.loopBehavior().INFINITE() is not null
                || (whileStatement.pattern() is null
                    && string.Equals(whileStatement.expression().GetText(), "true", StringComparison.Ordinal));
            return whileAlwaysRepeats && !LoopBodyContainsTopLevelBreak(whileStatement.statement(), statementLabel);
        }

        if (statementFor is { } forStatement)
        {
            var forCondition = forStatement.forCondition();
            var forAlwaysRepeats = forStatement.loopBehavior().INFINITE() is not null
                || forCondition is null
                || string.Equals(forCondition.GetText(), "true", StringComparison.Ordinal);
            return forAlwaysRepeats && !LoopBodyContainsTopLevelBreak(forStatement.statement(), statementLabel);
        }

        return false;
    }

    /// <summary>
    /// True when the loop body contains a `break` that targets this loop (i.e. not nested
    /// inside an inner loop), meaning the loop can exit and control can flow past it.
    /// </summary>
    private static bool LoopBodyContainsTopLevelBreak(StarkParser.StatementContext body, string? loopLabel)
    {
        return ContainsTopLevelBreak(body, allowUnlabeledBreak: true);

        bool ContainsTopLevelBreak(Antlr4.Runtime.Tree.IParseTree node, bool allowUnlabeledBreak)
        {
            if (node is StarkParser.BreakStatementContext breakStatement)
            {
                var breakLabel = breakStatement.Identifier()?.GetText();
                return breakLabel is null
                    ? allowUnlabeledBreak
                    : string.Equals(breakLabel, loopLabel, StringComparison.Ordinal);
            }

            var childBreaksCanTargetCurrentLoopWithoutLabel = allowUnlabeledBreak
                && node is not StarkParser.WhileStatementContext
                && node is not StarkParser.ForStatementContext
                && node is not StarkParser.SwitchStatementContext;

            for (var index = 0; index < node.ChildCount; index++)
            {
                if (ContainsTopLevelBreak(node.GetChild(index), childBreaksCanTargetCurrentLoopWithoutLabel))
                {
                    return true;
                }
            }

            return false;
        }
    }

    private bool TryCreateSwitchCoveragePattern(
        StarkParser.PatternContext switchPattern,
        StarkTypeSymbol switchType,
        out SwitchCoveragePattern? pattern)
    {
        if (switchPattern.DISCARD() is not null || switchPattern.VAR() is not null)
        {
            pattern = new SwitchCoveragePattern(
                SwitchCoveragePatternKind.MatchAll,
                switchPattern.GetText(),
                switchPattern,
                LiteralKey: null,
                AggregatePattern: null,
                EnumPattern: null);
            return true;
        }

        if (switchPattern.rangePattern() is { } rangePattern
            && TryCreateRangeCoverageInterval(rangePattern, switchType, out var rangeInterval))
        {
            pattern = new SwitchCoveragePattern(
                SwitchCoveragePatternKind.Range,
                rangePattern.GetText(),
                switchPattern,
                LiteralKey: null,
                AggregatePattern: null,
                EnumPattern: null,
                rangeInterval);
            return true;
        }

        if (switchPattern.literal() is { } literal
            && TryCreateLiteralCoverageKey(literal, switchType, out var literalKey))
        {
            var integerInterval = TryCreateIntegerLiteralCoverageInterval(literal, switchType, out var literalInterval)
                ? literalInterval
                : (RangeCoverageInterval?)null;
            pattern = new SwitchCoveragePattern(
                SwitchCoveragePatternKind.Literal,
                literal.GetText(),
                switchPattern,
                literalKey,
                AggregatePattern: null,
                EnumPattern: null,
                integerInterval);
            return true;
        }

        if (switchPattern.listPattern() is { } listPattern
            && TryCreateListCoveragePattern(listPattern, switchType, out var listCoverage))
        {
            pattern = new SwitchCoveragePattern(
                SwitchCoveragePatternKind.List,
                listPattern.GetText(),
                switchPattern,
                LiteralKey: null,
                AggregatePattern: null,
                EnumPattern: null,
                ListPattern: listCoverage);
            return true;
        }

        if (switchPattern.enumNamedFieldPattern() is { } enumNamedFieldPattern
            && TryCreateEnumNamedFieldCoveragePattern(enumNamedFieldPattern, switchType, out var enumNamedFieldCoverage))
        {
            pattern = new SwitchCoveragePattern(
                SwitchCoveragePatternKind.EnumCase,
                enumNamedFieldPattern.GetText(),
                switchPattern,
                LiteralKey: null,
                AggregatePattern: null,
                EnumPattern: enumNamedFieldCoverage);
            return true;
        }

        if (switchPattern.enumNamedFieldPattern() is { } aggregatePropertyPattern
            && TryCreateAggregatePropertyCoveragePattern(aggregatePropertyPattern, switchType, out var aggregatePropertyCoverage))
        {
            pattern = new SwitchCoveragePattern(
                SwitchCoveragePatternKind.Aggregate,
                aggregatePropertyPattern.GetText(),
                switchPattern,
                LiteralKey: null,
                aggregatePropertyCoverage,
                EnumPattern: null);
            return true;
        }

        if (switchPattern.genericEnumAggregatePattern() is { } genericEnumAggregatePattern
            && TryCreateEnumAggregateCoveragePattern(genericEnumAggregatePattern, switchType, out var genericEnumAggregateCoverage))
        {
            pattern = new SwitchCoveragePattern(
                SwitchCoveragePatternKind.EnumCase,
                genericEnumAggregatePattern.GetText(),
                switchPattern,
                LiteralKey: null,
                AggregatePattern: null,
                EnumPattern: genericEnumAggregateCoverage);
            return true;
        }

        if (switchPattern.aggregatePattern() is { } enumAggregatePattern
            && TryCreateEnumAggregateCoveragePattern(enumAggregatePattern, switchType, out var enumAggregateCoverage))
        {
            pattern = new SwitchCoveragePattern(
                SwitchCoveragePatternKind.EnumCase,
                enumAggregatePattern.GetText(),
                switchPattern,
                LiteralKey: null,
                AggregatePattern: null,
                EnumPattern: enumAggregateCoverage);
            return true;
        }

        if (switchPattern.aggregatePattern() is { } aggregatePattern
            && TryCreateAggregateCoveragePattern(aggregatePattern, switchType, out var aggregateCoverage))
        {
            pattern = new SwitchCoveragePattern(
                SwitchCoveragePatternKind.Aggregate,
                aggregatePattern.GetText(),
                switchPattern,
                LiteralKey: null,
                aggregateCoverage,
                EnumPattern: null);
            return true;
        }

        pattern = null;
        return false;
    }

    private bool TryCreateAggregateCoveragePattern(
        StarkParser.AggregatePatternContext aggregatePattern,
        StarkTypeSymbol switchType,
        out AggregateCoveragePattern? coveragePattern)
    {
        coveragePattern = null;

        if (TryGetPublishedTemplateAggregatePattern(
                aggregatePattern,
                out var publishedAggregateType,
                out var publishedNamedType))
        {
            return TryCreateResolvedAggregateCoveragePattern(
                aggregatePattern,
                switchType,
                publishedAggregateType,
                publishedNamedType,
                out coveragePattern);
        }

        var patternType = ResolveSimpleType(aggregatePattern.simpleType());
        if (switchType.Kind != StarkTypeKind.Named
            || patternType.Kind != StarkTypeKind.Named
            || switchType.NamedType is null
            || patternType.NamedType is null
            || !string.Equals(switchType.NamedType, patternType.NamedType, StringComparison.Ordinal)
            || !_namedTypes.TryGetValue(switchType.NamedType, out var namedType))
        {
            return false;
        }

        return TryCreateResolvedAggregateCoveragePattern(
            aggregatePattern,
            switchType,
            patternType,
            namedType,
            out coveragePattern);
    }

    private bool TryCreateResolvedAggregateCoveragePattern(
        StarkParser.AggregatePatternContext aggregatePattern,
        StarkTypeSymbol switchType,
        StarkTypeSymbol patternType,
        NamedTypeSymbol namedType,
        out AggregateCoveragePattern? coveragePattern)
    {
        coveragePattern = null;

        if (switchType.Kind != StarkTypeKind.Named
            || patternType.Kind != StarkTypeKind.Named
            || switchType.NamedType is null
            || patternType.NamedType is null
            || !string.Equals(switchType.NamedType, patternType.NamedType, StringComparison.Ordinal)
            || namedType.Kind == DeclarationKind.Enum)
        {
            return false;
        }

        var suffix = aggregatePattern.aggregatePatternSuffix();
        if (suffix is null || suffix.Identifier() is not null)
        {
            coveragePattern = CreateMatchAllAggregateCoveragePattern(namedType);
            return true;
        }

        if (suffix.namedPatternPayload() is { } namedPayload)
        {
            return TryCreateAggregateNamedFieldCoveragePattern(
                namedPayload,
                namedType,
                out coveragePattern);
        }

        var fieldPatterns = suffix.pattern();
        if (fieldPatterns.Length != namedType.OrderedFields.Count)
        {
            return false;
        }

        var coverageFields = new AggregateCoverageField[fieldPatterns.Length];
        for (var index = 0; index < fieldPatterns.Length; index++)
        {
            if (TryCreateStructuredCoverageField(fieldPatterns[index], namedType.OrderedFields[index].Type, out var coverageField, allowAnyCaptureWildcard: false))
            {
                coverageFields[index] = coverageField;
                continue;
            }

            return false;
        }

        coveragePattern = new AggregateCoveragePattern(namedType.Name, coverageFields);
        return true;
    }

    private bool TryCreateEnumAggregateCoveragePattern(
        StarkParser.AggregatePatternContext aggregatePattern,
        StarkTypeSymbol switchType,
        out EnumCoveragePattern? coveragePattern)
    {
        coveragePattern = null;

        if (TryGetPublishedTemplateEnumPattern(
                aggregatePattern,
                out _,
                out _,
                out var publishedEnumType,
                out var publishedVariant))
        {
            return TryCreateResolvedEnumAggregateCoveragePattern(
                aggregatePattern.aggregatePatternSuffix(),
                switchType,
                publishedEnumType,
                publishedVariant,
                out coveragePattern);
        }

        var caseName = aggregatePattern.simpleType().GetText();
        return switchType.Kind == StarkTypeKind.Named
               && switchType.NamedType is not null
               && TryResolveEnumCaseReference(caseName, out var enumType, out _, out var variant)
               && TryCreateResolvedEnumAggregateCoveragePattern(
                   aggregatePattern.aggregatePatternSuffix(),
                   switchType,
                   enumType,
                   variant,
                   out coveragePattern);
    }

    private bool TryCreateEnumAggregateCoveragePattern(
        StarkParser.GenericEnumAggregatePatternContext genericEnumAggregatePattern,
        StarkTypeSymbol switchType,
        out EnumCoveragePattern? coveragePattern)
    {
        coveragePattern = null;

        if (TryGetPublishedTemplateEnumPattern(
                genericEnumAggregatePattern,
                out _,
                out _,
                out var publishedEnumType,
                out var publishedVariant))
        {
            return TryCreateResolvedEnumAggregateCoveragePattern(
                genericEnumAggregatePattern.aggregatePatternSuffix(),
                switchType,
                publishedEnumType,
                publishedVariant,
                out coveragePattern);
        }

        return switchType.Kind == StarkTypeKind.Named
               && switchType.NamedType is not null
               && TryResolveEnumCaseReference(genericEnumAggregatePattern.genericEnumCaseReference(), out var enumType, out _, out var variant)
               && TryCreateResolvedEnumAggregateCoveragePattern(
                   genericEnumAggregatePattern.aggregatePatternSuffix(),
                   switchType,
                   enumType,
                   variant,
                   out coveragePattern);
    }

    private bool TryCreateResolvedEnumAggregateCoveragePattern(
        StarkParser.AggregatePatternSuffixContext? suffix,
        StarkTypeSymbol switchType,
        NamedTypeSymbol enumType,
        EnumVariantSymbol variant,
        out EnumCoveragePattern? coveragePattern)
    {
        coveragePattern = null;

        if (switchType.NamedType is null
            || !string.Equals(switchType.NamedType, enumType.Name, StringComparison.Ordinal)
            || variant.UsesNamedFields)
        {
            return false;
        }

        if (variant.IsUnit)
        {
            if (suffix is not null)
            {
                return false;
            }

            coveragePattern = new EnumCoveragePattern(enumType.Name, variant.Name, []);
            return true;
        }

        if (suffix is null)
        {
            return false;
        }

        if (suffix.Identifier() is not null)
        {
            coveragePattern = new EnumCoveragePattern(
                enumType.Name,
                variant.Name,
                variant.Fields
                    .Select(static _ => new AggregateCoverageField(
                        AggregateCoverageFieldKind.Wildcard,
                        LiteralKey: null,
                        NestedAggregatePattern: null,
                        NestedEnumPattern: null))
                    .ToArray());
            return true;
        }

        var fieldPatterns = suffix.pattern();
        if (fieldPatterns.Length != variant.Fields.Count)
        {
            return false;
        }

        var coverageFields = new AggregateCoverageField[fieldPatterns.Length];
        for (var index = 0; index < fieldPatterns.Length; index++)
        {
            if (!TryCreateStructuredCoverageField(fieldPatterns[index], variant.Fields[index].Type, out var coverageField, allowAnyCaptureWildcard: true))
            {
                return false;
            }

            coverageFields[index] = coverageField;
        }

        coveragePattern = new EnumCoveragePattern(enumType.Name, variant.Name, coverageFields);
        return true;
    }

    private bool TryCreateEnumNamedFieldCoveragePattern(
        StarkParser.EnumNamedFieldPatternContext enumNamedFieldPattern,
        StarkTypeSymbol switchType,
        out EnumCoveragePattern? coveragePattern)
    {
        coveragePattern = null;

        if (TryGetPublishedTemplateEnumPattern(
                enumNamedFieldPattern,
                out var publishedEnumPattern,
                out _,
                out var publishedEnumType,
                out var publishedVariant))
        {
            return TryCreateResolvedEnumNamedFieldCoveragePattern(
                enumNamedFieldPattern,
                switchType,
                publishedEnumType,
                publishedVariant,
                publishedEnumPattern,
                out coveragePattern);
        }

        var caseName = enumNamedFieldPattern.enumCaseTarget().GetText();
        if (switchType.Kind != StarkTypeKind.Named
            || switchType.NamedType is null
            || !TryResolveEnumCaseTarget(enumNamedFieldPattern.enumCaseTarget(), out _, out var enumType, out _, out var variant))
        {
            return false;
        }

        return TryCreateResolvedEnumNamedFieldCoveragePattern(
            enumNamedFieldPattern,
            switchType,
            enumType,
            variant,
            publishedPattern: null,
            out coveragePattern);
    }

    private bool TryCreateAggregateNamedFieldCoveragePattern(
        StarkParser.NamedPatternPayloadContext namedPayload,
        NamedTypeSymbol namedType,
        out AggregateCoveragePattern? coveragePattern)
    {
        coveragePattern = null;
        var members = namedPayload.namedPatternMember();
        if (members.Length != namedType.OrderedFields.Count)
        {
            return false;
        }

        var coverageFields = new AggregateCoverageField[namedType.OrderedFields.Count];
        var seenMembers = new HashSet<int>();
        foreach (var member in members)
        {
            var memberName = member.Identifier().GetText();
            var fieldIndex = FindOrderedAggregateFieldIndex(namedType, memberName);
            if (fieldIndex < 0
                || !seenMembers.Add(fieldIndex)
                || !TryCreateStructuredCoverageField(member.pattern(), namedType.OrderedFields[fieldIndex].Type, out var coverageField, allowAnyCaptureWildcard: false))
            {
                return false;
            }

            coverageFields[fieldIndex] = coverageField;
        }

        if (seenMembers.Count != namedType.OrderedFields.Count)
        {
            return false;
        }

        coveragePattern = new AggregateCoveragePattern(namedType.Name, coverageFields);
        return true;
    }

    private bool TryCreateAggregatePropertyCoveragePattern(
        StarkParser.EnumNamedFieldPatternContext enumNamedFieldPattern,
        StarkTypeSymbol switchType,
        out AggregateCoveragePattern? coveragePattern)
    {
        coveragePattern = null;
        return TryResolveAggregatePropertyPatternTarget(
                enumNamedFieldPattern.enumCaseTarget().GetText(),
                switchType,
                out var namedType)
            && TryCreateAggregateNamedFieldCoveragePattern(
                enumNamedFieldPattern.namedPatternPayload(),
                namedType,
                out coveragePattern);
    }

    private bool TryCreateResolvedEnumNamedFieldCoveragePattern(
        StarkParser.EnumNamedFieldPatternContext enumNamedFieldPattern,
        StarkTypeSymbol switchType,
        NamedTypeSymbol enumType,
        EnumVariantSymbol variant,
        ImportedTemplateEnumPatternSummary? publishedPattern,
        out EnumCoveragePattern? coveragePattern)
    {
        coveragePattern = null;

        if (switchType.Kind != StarkTypeKind.Named
            || switchType.NamedType is null
            || !string.Equals(switchType.NamedType, enumType.Name, StringComparison.Ordinal)
            || !variant.UsesNamedFields)
        {
            return false;
        }

        var members = enumNamedFieldPattern.namedPatternPayload().namedPatternMember();
        if (members.Length != variant.Fields.Count
            || publishedPattern is { Members.Count: > 0 } && members.Length != publishedPattern.Members.Count)
        {
            return false;
        }

        var coverageFields = new AggregateCoverageField[variant.Fields.Count];
        var seenMembers = new HashSet<int>();
        for (var memberOrdinal = 0; memberOrdinal < members.Length; memberOrdinal++)
        {
            var member = members[memberOrdinal];
            EnumVariantFieldSymbol? field;

            if (publishedPattern is { Members.Count: > 0 } && memberOrdinal < publishedPattern.Members.Count)
            {
                var publishedMember = publishedPattern.Members[memberOrdinal];
                field = publishedMember.FieldIndex >= 0 && publishedMember.FieldIndex < variant.Fields.Count
                    ? variant.Fields[publishedMember.FieldIndex]
                    : null;
            }
            else
            {
                var memberName = member.Identifier().GetText();
                field = variant.Fields.FirstOrDefault(candidate => string.Equals(candidate.Name, memberName, StringComparison.Ordinal));
            }

            if (field is null
                || field.Name is null
                || !seenMembers.Add(field.Position)
                || !TryCreateStructuredCoverageField(member.pattern(), field.Type, out var coverageField, allowAnyCaptureWildcard: true))
            {
                return false;
            }

            coverageFields[field.Position] = coverageField;
        }

        if (seenMembers.Count != variant.Fields.Count)
        {
            return false;
        }

        coveragePattern = new EnumCoveragePattern(enumType.Name, variant.Name, coverageFields);
        return true;
    }

    private bool TryCreateStructuredCoverageField(
        StarkParser.PatternContext pattern,
        StarkTypeSymbol fieldType,
        out AggregateCoverageField coverageField,
        bool allowAnyCaptureWildcard)
    {
        if (pattern.DISCARD() is not null)
        {
            coverageField = new AggregateCoverageField(
                AggregateCoverageFieldKind.Wildcard,
                LiteralKey: null,
                NestedAggregatePattern: null,
                NestedEnumPattern: null);
            return true;
        }

        if (pattern.VAR() is not null)
        {
            if (!allowAnyCaptureWildcard && !SupportsAggregateFieldSubpattern(fieldType))
            {
                coverageField = default!;
                return false;
            }

            coverageField = new AggregateCoverageField(
                AggregateCoverageFieldKind.Wildcard,
                LiteralKey: null,
                NestedAggregatePattern: null,
                NestedEnumPattern: null);
            return true;
        }

        if (pattern.rangePattern() is { } rangePattern
            && TryCreateRangeCoverageInterval(rangePattern, fieldType, out var rangeInterval))
        {
            coverageField = new AggregateCoverageField(
                AggregateCoverageFieldKind.Range,
                LiteralKey: null,
                NestedAggregatePattern: null,
                NestedEnumPattern: null,
                rangeInterval);
            return true;
        }

        if (pattern.literal() is { } literal
            && SupportsAggregateFieldSubpattern(fieldType)
            && TryCreateLiteralCoverageKey(literal, fieldType, out var literalKey))
        {
            var integerInterval = TryCreateIntegerLiteralCoverageInterval(literal, fieldType, out var literalInterval)
                ? literalInterval
                : (RangeCoverageInterval?)null;
            coverageField = new AggregateCoverageField(
                AggregateCoverageFieldKind.Literal,
                literalKey,
                NestedAggregatePattern: null,
                NestedEnumPattern: null,
                integerInterval);
            return true;
        }

        if (pattern.listPattern() is { } nestedListPattern
            && TryCreateListCoveragePattern(nestedListPattern, fieldType, out var listCoverage)
            && listCoverage is not null)
        {
            if (listCoverage.CanBeExhaustiveForTarget && IsMatchAllListPattern(listCoverage))
            {
                coverageField = new AggregateCoverageField(
                    AggregateCoverageFieldKind.Wildcard,
                    LiteralKey: null,
                    NestedAggregatePattern: null,
                    NestedEnumPattern: null);
                return true;
            }

            coverageField = new AggregateCoverageField(
                AggregateCoverageFieldKind.NestedList,
                LiteralKey: null,
                NestedAggregatePattern: null,
                NestedEnumPattern: null,
                NestedListPattern: listCoverage);
            return true;
        }

        if (pattern.enumNamedFieldPattern() is { } nestedEnumNamedFieldPattern
            && fieldType.Kind == StarkTypeKind.Named
            && TryCreateEnumNamedFieldCoveragePattern(nestedEnumNamedFieldPattern, fieldType, out var nestedEnumNamedPattern)
            && nestedEnumNamedPattern is not null)
        {
            if (IsMatchAllEnumPattern(nestedEnumNamedPattern))
            {
                coverageField = new AggregateCoverageField(
                    AggregateCoverageFieldKind.Wildcard,
                    LiteralKey: null,
                    NestedAggregatePattern: null,
                    NestedEnumPattern: null);
                return true;
            }

            coverageField = new AggregateCoverageField(
                AggregateCoverageFieldKind.NestedEnum,
                LiteralKey: null,
                NestedAggregatePattern: null,
                NestedEnumPattern: nestedEnumNamedPattern);
            return true;
        }

        if (pattern.aggregatePattern() is { } nestedEnumAggregatePattern
            && fieldType.Kind == StarkTypeKind.Named
            && TryCreateEnumAggregateCoveragePattern(nestedEnumAggregatePattern, fieldType, out var nestedEnumPattern)
            && nestedEnumPattern is not null)
        {
            if (IsMatchAllEnumPattern(nestedEnumPattern))
            {
                coverageField = new AggregateCoverageField(
                    AggregateCoverageFieldKind.Wildcard,
                    LiteralKey: null,
                    NestedAggregatePattern: null,
                    NestedEnumPattern: null);
                return true;
            }

            coverageField = new AggregateCoverageField(
                AggregateCoverageFieldKind.NestedEnum,
                LiteralKey: null,
                NestedAggregatePattern: null,
                NestedEnumPattern: nestedEnumPattern);
            return true;
        }

        if (pattern.genericEnumAggregatePattern() is { } nestedGenericEnumAggregatePattern
            && fieldType.Kind == StarkTypeKind.Named
            && TryCreateEnumAggregateCoveragePattern(nestedGenericEnumAggregatePattern, fieldType, out var nestedGenericEnumPattern)
            && nestedGenericEnumPattern is not null)
        {
            if (IsMatchAllEnumPattern(nestedGenericEnumPattern))
            {
                coverageField = new AggregateCoverageField(
                    AggregateCoverageFieldKind.Wildcard,
                    LiteralKey: null,
                    NestedAggregatePattern: null,
                    NestedEnumPattern: null);
                return true;
            }

            coverageField = new AggregateCoverageField(
                AggregateCoverageFieldKind.NestedEnum,
                LiteralKey: null,
                NestedAggregatePattern: null,
                NestedEnumPattern: nestedGenericEnumPattern);
            return true;
        }

        if (pattern.aggregatePattern() is { } nestedAggregatePattern
            && fieldType.Kind == StarkTypeKind.Named
            && TryCreateAggregateCoveragePattern(nestedAggregatePattern, fieldType, out var nestedAggregateCoverage)
            && nestedAggregateCoverage is not null)
        {
            if (IsMatchAllAggregatePattern(nestedAggregateCoverage))
            {
                coverageField = new AggregateCoverageField(
                    AggregateCoverageFieldKind.Wildcard,
                    LiteralKey: null,
                    NestedAggregatePattern: null,
                    NestedEnumPattern: null);
                return true;
            }

            coverageField = new AggregateCoverageField(
                AggregateCoverageFieldKind.NestedAggregate,
                LiteralKey: null,
                NestedAggregatePattern: nestedAggregateCoverage,
                NestedEnumPattern: null);
            return true;
        }

        coverageField = default!;
        return false;
    }

    private bool TryCreateListCoveragePattern(
        StarkParser.ListPatternContext listPattern,
        StarkTypeSymbol targetType,
        out ListCoveragePattern? coveragePattern)
    {
        coveragePattern = null;
        if (!TryGetListPatternElementType(targetType, out var elementType, out var fixedLength))
        {
            return false;
        }

        var elementPatterns = listPattern.pattern();
        if (fixedLength is int requiredLength && elementPatterns.Length != requiredLength)
        {
            return false;
        }

        var coverageElements = new AggregateCoverageField[elementPatterns.Length];
        for (var index = 0; index < elementPatterns.Length; index++)
        {
            if (!TryCreateStructuredCoverageField(elementPatterns[index], elementType, out var elementCoverage, allowAnyCaptureWildcard: false))
            {
                return false;
            }

            coverageElements[index] = elementCoverage;
        }

        coveragePattern = new ListCoveragePattern(
            targetType,
            elementPatterns.Length,
            CanBeExhaustiveForTarget: fixedLength is int knownLength && knownLength == elementPatterns.Length,
            coverageElements);
        return true;
    }

    private bool TryCreateRangeCoverageInterval(
        StarkParser.RangePatternContext rangePattern,
        StarkTypeSymbol targetType,
        out RangeCoverageInterval interval)
    {
        interval = default;
        if (targetType.Kind != StarkTypeKind.Integer
            || !TryGetRangePatternBounds(rangePattern, out var min, out var max)
            || min > max)
        {
            return false;
        }

        if (StarkTypeSymbols.TryGetEffectiveIntegerBounds(targetType, out var targetMin, out var targetMax)
            && (max < targetMin || min > targetMax))
        {
            return false;
        }

        interval = new RangeCoverageInterval(min, max);
        return true;
    }

    private static bool TryCreateIntegerLiteralCoverageInterval(
        StarkParser.LiteralContext literal,
        StarkTypeSymbol targetType,
        out RangeCoverageInterval interval)
    {
        interval = default;
        if (targetType.Kind != StarkTypeKind.Integer
            || literal.signedIntegerLiteral() is not { } integerLiteral)
        {
            return false;
        }

        var value = ParseSignedIntegerLiteral(integerLiteral);
        interval = new RangeCoverageInterval(value, value);
        return true;
    }

    private static bool TryCreateLiteralCoverageKey(
        StarkParser.LiteralContext literal,
        StarkTypeSymbol targetType,
        out string literalKey)
    {
        literalKey = string.Empty;

        if (targetType.Kind == StarkTypeKind.Bool)
        {
            if (literal.TRUE() is not null)
            {
                literalKey = BoolTrueCoverageKey;
                return true;
            }

            if (literal.FALSE() is not null)
            {
                literalKey = BoolFalseCoverageKey;
                return true;
            }

            return false;
        }

        if (targetType.Kind == StarkTypeKind.Integer
            && literal.signedIntegerLiteral() is { } integerLiteral)
        {
            literalKey = $"int:{ParseSignedIntegerLiteral(integerLiteral)}";
            return true;
        }

        if (targetType.Kind == StarkTypeKind.Float
            && literal.FloatLiteral() is not null)
        {
            literalKey = $"float:{literal.GetText()}";
            return true;
        }

        if (targetType.Kind == StarkTypeKind.RawPointer
            && literal.NULL() is not null)
        {
            literalKey = "rawptr:null";
            return true;
        }

        if (targetType.Kind is StarkTypeKind.Ascii or StarkTypeKind.Unicode
            && (literal.StringLiteral() is not null || literal.CharacterLiteral() is not null))
        {
            literalKey = $"{targetType.Kind.ToString().ToLowerInvariant()}:{literal.GetText()}";
            return true;
        }

        return false;
    }

    private void ReportUnreachableSwitchLabel(
        ParserRuleContext context,
        string labelText,
        SwitchCoveragePattern coveringPattern,
        StarkTypeSymbol switchType,
        bool becauseExhaustive)
    {
        var message = becauseExhaustive
            ? $"Switch label '{labelText}' is unreachable because the switch is already exhaustive after the earlier unguarded label '{coveringPattern.LabelText}'."
            : $"Switch label '{labelText}' is unreachable because the earlier unguarded label '{coveringPattern.LabelText}' already covers it.";
        ReportError("STK3019", message, context);

        var note = becauseExhaustive
            ? $"Switch coverage becomes exhaustive here for '{switchType.DisplayName}'."
            : $"This unguarded switch label already covers the later label '{labelText}'.";
        ReportInfo("STK3020", note, coveringPattern.Context);
    }

    private static bool Covers(SwitchCoveragePattern existing, SwitchCoveragePattern current)
    {
        if (existing.Kind == SwitchCoveragePatternKind.MatchAll)
        {
            return true;
        }

        if (existing.RangeInterval is { } existingRange
            && current.RangeInterval is { } currentRange)
        {
            return existingRange.Min <= currentRange.Min && existingRange.Max >= currentRange.Max;
        }

        if (existing.Kind != current.Kind)
        {
            return false;
        }

        if (existing.Kind == SwitchCoveragePatternKind.Literal)
        {
            return string.Equals(existing.LiteralKey, current.LiteralKey, StringComparison.Ordinal);
        }

        if (existing.Kind == SwitchCoveragePatternKind.EnumCase)
        {
            return existing.EnumPattern is not null
                && current.EnumPattern is not null
                && Covers(existing.EnumPattern, current.EnumPattern);
        }

        if (existing.Kind == SwitchCoveragePatternKind.List)
        {
            return existing.ListPattern is not null
                && current.ListPattern is not null
                && Covers(existing.ListPattern, current.ListPattern);
        }

        return existing.AggregatePattern is not null
            && current.AggregatePattern is not null
            && Covers(existing.AggregatePattern, current.AggregatePattern);
    }

    private static bool Covers(AggregateCoveragePattern existing, AggregateCoveragePattern current)
    {
        if (!string.Equals(existing.TypeName, current.TypeName, StringComparison.Ordinal)
            || existing.Fields.Count != current.Fields.Count)
        {
            return false;
        }

        for (var index = 0; index < existing.Fields.Count; index++)
        {
            if (!Covers(existing.Fields[index], current.Fields[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool Covers(EnumCoveragePattern existing, EnumCoveragePattern current)
    {
        if (!string.Equals(existing.EnumName, current.EnumName, StringComparison.Ordinal)
            || !string.Equals(existing.VariantName, current.VariantName, StringComparison.Ordinal)
            || existing.Fields.Count != current.Fields.Count)
        {
            return false;
        }

        for (var index = 0; index < existing.Fields.Count; index++)
        {
            if (!Covers(existing.Fields[index], current.Fields[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool Covers(AggregateCoverageField existing, AggregateCoverageField current)
    {
        if (existing.Kind == AggregateCoverageFieldKind.Wildcard)
        {
            return true;
        }

        if (existing.RangeInterval is { } existingRange
            && current.RangeInterval is { } currentRange)
        {
            return existingRange.Min <= currentRange.Min && existingRange.Max >= currentRange.Max;
        }

        if (existing.Kind != current.Kind)
        {
            return false;
        }

        if (existing.Kind == AggregateCoverageFieldKind.Literal)
        {
            return string.Equals(existing.LiteralKey, current.LiteralKey, StringComparison.Ordinal);
        }

        if (existing.Kind == AggregateCoverageFieldKind.NestedAggregate)
        {
            return existing.NestedAggregatePattern is not null
                && current.NestedAggregatePattern is not null
                && Covers(existing.NestedAggregatePattern, current.NestedAggregatePattern);
        }

        if (existing.Kind == AggregateCoverageFieldKind.NestedEnum)
        {
            return existing.NestedEnumPattern is not null
                && current.NestedEnumPattern is not null
                && Covers(existing.NestedEnumPattern, current.NestedEnumPattern);
        }

        return existing.NestedListPattern is not null
            && current.NestedListPattern is not null
            && Covers(existing.NestedListPattern, current.NestedListPattern);
    }

    private static bool IsMatchAllAggregatePattern(AggregateCoveragePattern pattern)
    {
        return pattern.Fields.All(static field => field.Kind == AggregateCoverageFieldKind.Wildcard);
    }

    private static bool Covers(ListCoveragePattern existing, ListCoveragePattern current)
    {
        if (existing.Length != current.Length
            || !StarkTypeSymbolsHaveSameIdentity(existing.ListType, current.ListType)
            || existing.Elements.Count != current.Elements.Count)
        {
            return false;
        }

        for (var index = 0; index < existing.Elements.Count; index++)
        {
            if (!Covers(existing.Elements[index], current.Elements[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsMatchAllListPattern(ListCoveragePattern pattern)
    {
        return pattern.Elements.All(static element => element.Kind == AggregateCoverageFieldKind.Wildcard);
    }

    private static AggregateCoveragePattern CreateMatchAllAggregateCoveragePattern(NamedTypeSymbol namedType)
    {
        return new AggregateCoveragePattern(
            namedType.Name,
            namedType.OrderedFields
                .Select(static _ => new AggregateCoverageField(
                    AggregateCoverageFieldKind.Wildcard,
                    LiteralKey: null,
                    NestedAggregatePattern: null,
                    NestedEnumPattern: null))
                .ToArray());
    }

    private static bool IsMatchAllEnumPattern(EnumCoveragePattern pattern)
    {
        return pattern.Fields.All(static field => field.Kind == AggregateCoverageFieldKind.Wildcard);
    }

    private bool IsEnumSwitchType(StarkTypeSymbol switchType)
    {
        return switchType.Kind == StarkTypeKind.Named
            && switchType.NamedType is not null
            && _namedTypes.TryGetValue(switchType.NamedType, out var namedType)
            && namedType.Kind == DeclarationKind.Enum;
    }

    private static string DescribeSwitchLabel(StarkParser.SwitchLabelContext label)
    {
        if (label.DEFAULT() is not null)
        {
            return "default";
        }

        var patterns = label.pattern();
        return patterns.Length == 0
            ? label.GetText()
            : string.Join(" | ", patterns.Select(static pattern => pattern.GetText()));
    }

    private void BindPattern(StarkParser.PatternContext pattern, StarkTypeSymbol switchType, Scope scope)
    {
        if (pattern.rangePattern() is { } rangePattern)
        {
            BindRangePattern(rangePattern, switchType, "switch expression");
            return;
        }

        if (pattern.literal() is { } literal)
        {
            var literalBinding = EvaluateLiteral(literal);
            var literalType = literalBinding.Type;
            if (!CanAssignPatternLiteral(switchType, literalBinding))
            {
                ReportError(
                    "STK3002",
                    $"Switch pattern '{literal.GetText()}' expects '{switchType.DisplayName}' but found '{literalType.DisplayName}'.{GetExplicitConversionHint(switchType, literalType)}",
                    literal);
            }
            return;
        }

        if (pattern.listPattern() is { } listPattern)
        {
            BindListPattern(listPattern, switchType, scope, "switch expression");
            return;
        }

        if (pattern.VAR() is not null)
        {
            if (IsEnumSwitchType(switchType))
            {
                ReportError(
                    "STK3008",
                    $"Switch over enum '{switchType.DisplayName}' cannot use a whole-value capture pattern. Match an enum case, '_', or 'default' instead.",
                    pattern);
                return;
            }

            scope.Declare(new VariableSymbol(pattern.Identifier().GetText(), switchType, IsMutable: false, IsConstant: false));
            return;
        }

        if (pattern.enumNamedFieldPattern() is { } enumNamedFieldPattern)
        {
            BindEnumNamedFieldPattern(enumNamedFieldPattern, switchType, scope);
            return;
        }

        if (pattern.genericEnumAggregatePattern() is { } genericEnumAggregatePattern)
        {
            BindEnumAggregatePattern(genericEnumAggregatePattern, switchType, scope);
            return;
        }

        if (pattern.aggregatePattern() is { } aggregatePattern)
        {
            if (TryBindEnumAggregatePattern(aggregatePattern, switchType, scope))
            {
                return;
            }

            BindAggregatePattern(aggregatePattern, switchType, scope);
        }
    }

    private void BindRangePattern(
        StarkParser.RangePatternContext rangePattern,
        StarkTypeSymbol targetType,
        string targetDescription)
    {
        if (targetType.Kind != StarkTypeKind.Integer)
        {
            ReportError(
                "STK3008",
                $"Range pattern '{rangePattern.GetText()}' requires an integer target, but the {targetDescription} has type '{targetType.DisplayName}'.",
                rangePattern);
            return;
        }

        if (!TryGetRangePatternBounds(rangePattern, out var min, out var max))
        {
            ReportError("STK3008", $"Range pattern '{rangePattern.GetText()}' could not resolve its integer endpoints.", rangePattern);
            return;
        }

        if (min > max)
        {
            ReportError(
                "STK3008",
                $"Range pattern '{rangePattern.GetText()}' has lower bound {min} greater than upper bound {max}.",
                rangePattern);
            return;
        }

        if (StarkTypeSymbols.TryGetEffectiveIntegerBounds(targetType, out var targetMin, out var targetMax)
            && (max < targetMin || min > targetMax))
        {
            ReportError(
                "STK3008",
                $"Range pattern '{rangePattern.GetText()}' cannot match '{targetType.DisplayName}' because it does not overlap the target range [{targetMin}, {targetMax}].",
                rangePattern);
        }
    }

    private void BindAggregatePattern(StarkParser.AggregatePatternContext aggregatePattern, StarkTypeSymbol switchType, Scope scope)
    {
        StarkTypeSymbol patternType;
        NamedTypeSymbol namedType;

        if (TryGetPublishedTemplateAggregatePattern(
                aggregatePattern,
                out var publishedPatternType,
                out var publishedNamedType))
        {
            patternType = publishedPatternType;
            namedType = publishedNamedType;
        }
        else
        {
            patternType = ResolveSimpleType(aggregatePattern.simpleType());
            if (switchType.Kind != StarkTypeKind.Named
                || patternType.Kind != StarkTypeKind.Named
                || switchType.NamedType is null
                || patternType.NamedType is null
                || !string.Equals(switchType.NamedType, patternType.NamedType, StringComparison.Ordinal))
            {
                ReportError(
                    "STK3008",
                    $"Switch aggregate pattern '{aggregatePattern.GetText()}' must exactly match the named switch type '{switchType.DisplayName}'.",
                    aggregatePattern);
                return;
            }

            if (!_namedTypes.TryGetValue(switchType.NamedType, out namedType!))
            {
                ReportError(
                    "STK3008",
                    $"Switch aggregate pattern '{aggregatePattern.GetText()}' could not resolve field information for '{switchType.DisplayName}'.",
                    aggregatePattern);
                return;
            }
        }

        if (switchType.Kind != StarkTypeKind.Named
            || patternType.Kind != StarkTypeKind.Named
            || switchType.NamedType is null
            || patternType.NamedType is null
            || !string.Equals(switchType.NamedType, patternType.NamedType, StringComparison.Ordinal))
        {
            ReportError(
                "STK3008",
                $"Switch aggregate pattern '{aggregatePattern.GetText()}' must exactly match the named switch type '{switchType.DisplayName}'.",
                aggregatePattern);
            return;
        }

        if (namedType.Kind == DeclarationKind.Enum)
        {
            ReportError(
                "STK3008",
                $"Switch over enum '{switchType.DisplayName}' must use dot-qualified enum case patterns such as '{switchType.DisplayName}.Case'.",
                aggregatePattern);
            return;
        }

        RecordAggregatePattern(switchType, aggregatePattern);

        var suffix = aggregatePattern.aggregatePatternSuffix();
        if (suffix is null)
        {
            return;
        }

        if (suffix.Identifier() is not null)
        {
            scope.Declare(new VariableSymbol(suffix.Identifier().GetText(), switchType, IsMutable: false, IsConstant: false));
            return;
        }

        if (suffix.namedPatternPayload() is { } namedPayload)
        {
            BindAggregateNamedFieldPatternPayload(
                aggregatePattern.GetText(),
                namedType,
                namedPayload,
                scope,
                aggregatePattern);
            return;
        }

        var fieldPatterns = suffix.pattern();
        if (fieldPatterns.Length != namedType.OrderedFields.Count)
        {
            ReportError(
                "STK3008",
                $"Switch aggregate pattern '{aggregatePattern.GetText()}' expects {namedType.OrderedFields.Count} field subpattern{Pluralize(namedType.OrderedFields.Count)} for '{namedType.Name}' but found {fieldPatterns.Length}.",
                aggregatePattern);
            return;
        }

        for (var index = 0; index < fieldPatterns.Length; index++)
        {
            BindAggregateFieldPattern(fieldPatterns[index], namedType.OrderedFields[index], scope);
        }
    }

    private bool TryBindEnumAggregatePattern(StarkParser.AggregatePatternContext aggregatePattern, StarkTypeSymbol switchType, Scope scope)
    {
        if (TryGetPublishedTemplateEnumPattern(
                aggregatePattern,
                out _,
                out var publishedEnumTypeSymbol,
                out var publishedEnumType,
                out var publishedVariant))
        {
            BindResolvedEnumAggregatePattern(
                aggregatePattern.GetText(),
                aggregatePattern,
                aggregatePattern.aggregatePatternSuffix(),
                switchType,
                scope,
                publishedEnumTypeSymbol,
                publishedEnumType,
                publishedVariant);
            return true;
        }

        var caseName = aggregatePattern.simpleType().GetText();
        if (!TryResolveEnumCaseReference(caseName, out var enumType, out var enumTypeSymbol, out var variant))
        {
            return false;
        }

        BindResolvedEnumAggregatePattern(caseName, aggregatePattern, aggregatePattern.aggregatePatternSuffix(), switchType, scope, enumTypeSymbol, enumType, variant);
        return true;
    }

    private void BindEnumAggregatePattern(
        StarkParser.GenericEnumAggregatePatternContext genericEnumAggregatePattern,
        StarkTypeSymbol switchType,
        Scope scope)
    {
        var caseName = genericEnumAggregatePattern.GetText();
        if (TryGetPublishedTemplateEnumPattern(
                genericEnumAggregatePattern,
                out _,
                out var publishedEnumTypeSymbol,
                out var publishedEnumType,
                out var publishedVariant))
        {
            BindResolvedEnumAggregatePattern(
                caseName,
                genericEnumAggregatePattern,
                genericEnumAggregatePattern.aggregatePatternSuffix(),
                switchType,
                scope,
                publishedEnumTypeSymbol,
                publishedEnumType,
                publishedVariant);
            return;
        }

        if (!TryResolveEnumCaseReference(genericEnumAggregatePattern.genericEnumCaseReference(), out var enumType, out var enumTypeSymbol, out var variant))
        {
            ReportError("STK3003", $"Unknown symbol '{caseName}'.", genericEnumAggregatePattern);
            return;
        }

        BindResolvedEnumAggregatePattern(
            caseName,
            genericEnumAggregatePattern,
            genericEnumAggregatePattern.aggregatePatternSuffix(),
            switchType,
            scope,
            enumTypeSymbol,
            enumType,
            variant);
    }

    private void BindResolvedEnumAggregatePattern(
        string caseName,
        ParserRuleContext context,
        StarkParser.AggregatePatternSuffixContext? suffix,
        StarkTypeSymbol switchType,
        Scope scope,
        StarkTypeSymbol enumTypeSymbol,
        NamedTypeSymbol enumType,
        EnumVariantSymbol variant)
    {
        if (switchType.Kind != StarkTypeKind.Named
            || switchType.NamedType is null
            || !string.Equals(switchType.NamedType, enumType.Name, StringComparison.Ordinal))
        {
            ReportError(
                "STK3008",
                $"Switch enum case pattern '{context.GetText()}' must exactly match the enum switch type '{switchType.DisplayName}'.",
                context);
            return;
        }

        if (variant.UsesNamedFields)
        {
            ReportError(
                "STK3008",
                $"Enum case pattern '{caseName}' must use a named-field payload pattern.",
                context);
            return;
        }

        RecordEnumPattern(enumTypeSymbol, variant.Name, context);

        if (variant.IsUnit)
        {
            if (suffix is not null)
            {
                ReportError(
                    "STK3008",
                    $"Unit-like enum case pattern '{caseName}' may not bind payload subpatterns.",
                    context);
            }

            return;
        }

        if (suffix is null)
        {
            ReportError(
                "STK3009",
                $"Enum case pattern '{caseName}' expects {variant.Fields.Count} payload subpattern{Pluralize(variant.Fields.Count)}.",
                context);
            return;
        }

        if (suffix.Identifier() is not null)
        {
            scope.Declare(new VariableSymbol(suffix.Identifier().GetText(), switchType, IsMutable: false, IsConstant: false));
            return;
        }

        var fieldPatterns = suffix.pattern();
        if (fieldPatterns.Length != variant.Fields.Count)
        {
            ReportError(
                "STK3009",
                $"Enum case pattern '{context.GetText()}' expects {variant.Fields.Count} payload subpattern{Pluralize(variant.Fields.Count)} but found {fieldPatterns.Length}.",
                context);
            return;
        }

        for (var index = 0; index < fieldPatterns.Length; index++)
        {
            BindEnumVariantFieldPattern(fieldPatterns[index], variant.Fields[index], scope);
        }
    }

    private void BindEnumNamedFieldPattern(
        StarkParser.EnumNamedFieldPatternContext enumNamedFieldPattern,
        StarkTypeSymbol switchType,
        Scope scope)
    {
        var caseName = enumNamedFieldPattern.enumCaseTarget().GetText();
        if (TryGetPublishedTemplateEnumPattern(
                enumNamedFieldPattern,
                out var publishedEnumPattern,
                out var publishedEnumTypeSymbol,
                out var publishedEnumType,
                out var publishedVariant))
        {
            BindResolvedEnumNamedFieldPattern(
                caseName,
                enumNamedFieldPattern,
                switchType,
                scope,
                publishedEnumTypeSymbol,
                publishedEnumType,
                publishedVariant,
                publishedEnumPattern);
            return;
        }

        if (!TryResolveEnumCaseTarget(enumNamedFieldPattern.enumCaseTarget(), out _, out var enumType, out var enumTypeSymbol, out var variant))
        {
            if (TryResolveAggregatePropertyPatternTarget(caseName, switchType, out var aggregateType))
            {
                RecordAggregatePattern(switchType, enumNamedFieldPattern);
                BindAggregateNamedFieldPatternPayload(
                    enumNamedFieldPattern.GetText(),
                    aggregateType,
                    enumNamedFieldPattern.namedPatternPayload(),
                    scope,
                    enumNamedFieldPattern);
                return;
            }

            ReportError("STK3003", $"Unknown symbol '{caseName}'.", enumNamedFieldPattern);
            return;
        }

        BindResolvedEnumNamedFieldPattern(caseName, enumNamedFieldPattern, switchType, scope, enumTypeSymbol, enumType, variant, publishedPattern: null);
    }

    private void BindResolvedEnumNamedFieldPattern(
        string caseName,
        StarkParser.EnumNamedFieldPatternContext enumNamedFieldPattern,
        StarkTypeSymbol switchType,
        Scope scope,
        StarkTypeSymbol enumTypeSymbol,
        NamedTypeSymbol enumType,
        EnumVariantSymbol variant,
        ImportedTemplateEnumPatternSummary? publishedPattern)
    {

        if (switchType.Kind != StarkTypeKind.Named
            || switchType.NamedType is null
            || !string.Equals(switchType.NamedType, enumType.Name, StringComparison.Ordinal))
        {
            ReportError(
                "STK3008",
                $"Switch enum case pattern '{enumNamedFieldPattern.GetText()}' must exactly match the enum switch type '{switchType.DisplayName}'.",
                enumNamedFieldPattern);
            return;
        }

        if (!variant.UsesNamedFields)
        {
            ReportError(
                "STK3008",
                variant.IsUnit
                    ? $"Enum case '{caseName}' is unit-like and may not use a named-field pattern."
                    : $"Enum case '{caseName}' is tuple-like and must use positional subpatterns, not a named-field pattern.",
                enumNamedFieldPattern);
            return;
        }

        var members = enumNamedFieldPattern.namedPatternPayload().namedPatternMember();
        var seenMembers = new HashSet<int>();
        var recordedMembers = new List<EnumPatternMemberTypingRecord>(members.Length);
        for (var memberOrdinal = 0; memberOrdinal < members.Length; memberOrdinal++)
        {
            var member = members[memberOrdinal];
            var memberName = member.Identifier().GetText();
            EnumVariantFieldSymbol? field;

            if (publishedPattern is { Members.Count: > 0 } && memberOrdinal < publishedPattern.Members.Count)
            {
                var publishedMember = publishedPattern.Members[memberOrdinal];
                memberName = publishedMember.FieldName;
                field = publishedMember.FieldIndex >= 0 && publishedMember.FieldIndex < variant.Fields.Count
                    ? variant.Fields[publishedMember.FieldIndex]
                    : null;
            }
            else
            {
                field = variant.Fields.FirstOrDefault(candidate => string.Equals(candidate.Name, memberName, StringComparison.Ordinal));
            }

            if (field is null)
            {
                ReportError("STK3005", $"Enum case '{caseName}' does not contain a field named '{memberName}'.", member);
                continue;
            }

            if (!seenMembers.Add(field.Position))
            {
                ReportError("STK3006", $"Enum case pattern member '{memberName}' for '{caseName}' is specified more than once.", member);
                continue;
            }

            recordedMembers.Add(new EnumPatternMemberTypingRecord(memberName, field.Position, field.Type));
            BindEnumVariantFieldPattern(member.pattern(), field, scope);
        }

        foreach (var field in variant.Fields)
        {
            if (!seenMembers.Contains(field.Position))
            {
                ReportError("STK3009", $"Enum case pattern '{caseName}' requires member '{field.Name}'.", enumNamedFieldPattern);
            }
        }

        RecordEnumPattern(enumTypeSymbol, variant.Name, enumNamedFieldPattern, recordedMembers);
    }

    private void BindEnumVariantFieldPattern(StarkParser.PatternContext pattern, EnumVariantFieldSymbol field, Scope scope)
    {
        var fieldName = field.Name ?? $"#{field.Position}";
        if (pattern.DISCARD() is not null)
        {
            return;
        }

        if (pattern.rangePattern() is { } rangePattern)
        {
            BindRangePattern(rangePattern, field.Type, $"enum case payload field '{fieldName}'");
            return;
        }

        if (pattern.listPattern() is { } listPattern)
        {
            BindListPattern(listPattern, field.Type, scope, $"enum case payload field '{fieldName}'");
            return;
        }

        if (pattern.VAR() is not null)
        {
            scope.Declare(new VariableSymbol(pattern.Identifier().GetText(), field.Type, IsMutable: false, IsConstant: false));
            return;
        }

        if (pattern.literal() is { } literal)
        {
            if (!SupportsAggregateFieldSubpattern(field.Type))
            {
                ReportError(
                    "STK3008",
                    $"Enum case payload field '{fieldName}' of type '{field.Type.DisplayName}' cannot currently be matched with a literal in an enum switch pattern. Enum field subpatterns currently support only scalar and text-view field types.",
                    pattern);
                return;
            }

            var literalType = EvaluateLiteral(literal).Type;
            if (!CanAssignPatternLiteral(field.Type, new ExpressionBinding(
                    literalType,
                    TextLiteral: literal.GetText(),
                    TextLiteralKind: literal.StringLiteral() is not null ? TextLiteralKind.String : TextLiteralKind.Character)))
            {
                ReportError(
                    "STK3002",
                    $"Enum case field pattern '{literal.GetText()}' expects '{field.Type.DisplayName}' for field '{fieldName}' but found '{literalType.DisplayName}'.{GetExplicitConversionHint(field.Type, literalType)}",
                    literal);
            }

            return;
        }

        if (pattern.enumNamedFieldPattern() is not null || pattern.aggregatePattern() is not null)
        {
            BindPattern(pattern, field.Type, scope);
        }

        if (pattern.genericEnumAggregatePattern() is not null)
        {
            BindPattern(pattern, field.Type, scope);
        }
    }

    private void BindAggregateFieldPattern(StarkParser.PatternContext pattern, FieldSymbol field, Scope scope)
    {
        if (pattern.DISCARD() is not null)
        {
            return;
        }

        if (pattern.rangePattern() is { } rangePattern)
        {
            BindRangePattern(rangePattern, field.Type, $"field '{field.Name}'");
            return;
        }

        if (pattern.listPattern() is { } listPattern)
        {
            BindListPattern(listPattern, field.Type, scope, $"field '{field.Name}'");
            return;
        }

        if (pattern.enumNamedFieldPattern() is { } enumNamedFieldPattern)
        {
            BindEnumNamedFieldPattern(enumNamedFieldPattern, field.Type, scope);
            return;
        }

        if (pattern.genericEnumAggregatePattern() is { } genericEnumAggregatePattern)
        {
            BindEnumAggregatePattern(genericEnumAggregatePattern, field.Type, scope);
            return;
        }

        if (pattern.aggregatePattern() is { } aggregatePattern)
        {
            if (TryBindEnumAggregatePattern(aggregatePattern, field.Type, scope))
            {
                return;
            }

            BindNestedAggregateFieldPattern(aggregatePattern, field, scope);
            return;
        }

        if (pattern.VAR() is not null)
        {
            if (!SupportsAggregateFieldSubpattern(field.Type))
            {
                ReportError(
                    "STK3008",
                    $"Field '{field.Name}' of type '{field.Type.DisplayName}' cannot currently be captured in an aggregate switch pattern. Aggregate field subpatterns currently support only scalar and text-view field types.",
                    pattern);
                return;
            }

            scope.Declare(new VariableSymbol(pattern.Identifier().GetText(), field.Type, IsMutable: false, IsConstant: false));
            return;
        }

        if (pattern.literal() is { } literal)
        {
            if (!SupportsAggregateFieldSubpattern(field.Type))
            {
                ReportError(
                    "STK3008",
                    $"Field '{field.Name}' of type '{field.Type.DisplayName}' cannot currently be matched with a literal in an aggregate switch pattern. Aggregate field subpatterns currently support only scalar and text-view field types.",
                    pattern);
                return;
            }

            var literalType = EvaluateLiteral(literal).Type;
            if (!CanAssignPatternLiteral(field.Type, new ExpressionBinding(
                    literalType,
                    TextLiteral: literal.GetText(),
                    TextLiteralKind: literal.StringLiteral() is not null ? TextLiteralKind.String : TextLiteralKind.Character)))
            {
                ReportError(
                    "STK3002",
                    $"Switch field pattern '{literal.GetText()}' expects '{field.Type.DisplayName}' for field '{field.Name}' but found '{literalType.DisplayName}'.{GetExplicitConversionHint(field.Type, literalType)}",
                    literal);
            }
        }
    }

    private void BindAggregateNamedFieldPatternPayload(
        string patternText,
        NamedTypeSymbol namedType,
        StarkParser.NamedPatternPayloadContext namedPayload,
        Scope scope,
        ParserRuleContext context)
    {
        var members = namedPayload.namedPatternMember();
        if (members.Length != namedType.OrderedFields.Count)
        {
            ReportError(
                "STK3008",
                $"Switch aggregate property pattern '{patternText}' expects {namedType.OrderedFields.Count} named field subpattern{Pluralize(namedType.OrderedFields.Count)} for '{namedType.Name}' but found {members.Length}.",
                context);
            return;
        }

        var seenMembers = new HashSet<int>();
        var recordedMembers = new List<AggregatePatternMemberTypingRecord>(members.Length);
        foreach (var member in members)
        {
            var memberName = member.Identifier().GetText();
            var fieldIndex = FindOrderedAggregateFieldIndex(namedType, memberName);
            if (fieldIndex < 0)
            {
                ReportError("STK3005", $"Aggregate type '{namedType.Name}' does not contain a field named '{memberName}'.", member);
                continue;
            }

            if (!seenMembers.Add(fieldIndex))
            {
                ReportError("STK3006", $"Aggregate property pattern member '{memberName}' for '{namedType.Name}' is specified more than once.", member);
                continue;
            }

            recordedMembers.Add(new AggregatePatternMemberTypingRecord(
                memberName,
                fieldIndex,
                namedType.OrderedFields[fieldIndex].Type));
            BindAggregateFieldPattern(member.pattern(), namedType.OrderedFields[fieldIndex], scope);
        }

        foreach (var field in namedType.OrderedFields)
        {
            var fieldIndex = FindOrderedAggregateFieldIndex(namedType, field.Name);
            if (!seenMembers.Contains(fieldIndex))
            {
                ReportError("STK3009", $"Aggregate property pattern '{patternText}' requires member '{field.Name}'.", context);
            }
        }

        RecordAggregatePattern(StarkTypeSymbols.Named(namedType.Name), context, recordedMembers);
    }

    private void BindListPattern(
        StarkParser.ListPatternContext listPattern,
        StarkTypeSymbol targetType,
        Scope scope,
        string targetDescription)
    {
        if (!TryGetListPatternElementType(targetType, out var elementType, out var fixedLength))
        {
            ReportError(
                "STK3008",
                $"List pattern '{listPattern.GetText()}' requires a fixed array, slice, or dynamic storage target, but the {targetDescription} has type '{targetType.DisplayName}'.",
                listPattern);
            return;
        }

        var elementPatterns = listPattern.pattern();
        if (fixedLength is int requiredLength && elementPatterns.Length != requiredLength)
        {
            ReportError(
                "STK3008",
                $"List pattern '{listPattern.GetText()}' expects exactly {requiredLength} element subpattern{Pluralize(requiredLength)} for fixed-array target '{targetType.DisplayName}' but found {elementPatterns.Length}.",
                listPattern);
            return;
        }

        for (var index = 0; index < elementPatterns.Length; index++)
        {
            BindListElementPattern(elementPatterns[index], elementType, index, scope);
        }
    }

    private void BindListElementPattern(
        StarkParser.PatternContext pattern,
        StarkTypeSymbol elementType,
        int elementIndex,
        Scope scope)
    {
        if (pattern.DISCARD() is not null)
        {
            return;
        }

        if (pattern.rangePattern() is { } rangePattern)
        {
            BindRangePattern(rangePattern, elementType, $"list element #{elementIndex}");
            return;
        }

        if (pattern.listPattern() is { } nestedListPattern)
        {
            BindListPattern(nestedListPattern, elementType, scope, $"list element #{elementIndex}");
            return;
        }

        if (pattern.enumNamedFieldPattern() is { } enumNamedFieldPattern)
        {
            BindEnumNamedFieldPattern(enumNamedFieldPattern, elementType, scope);
            return;
        }

        if (pattern.genericEnumAggregatePattern() is { } genericEnumAggregatePattern)
        {
            BindEnumAggregatePattern(genericEnumAggregatePattern, elementType, scope);
            return;
        }

        if (pattern.aggregatePattern() is { } aggregatePattern)
        {
            if (TryBindEnumAggregatePattern(aggregatePattern, elementType, scope))
            {
                return;
            }

            if (elementType.Kind != StarkTypeKind.Named || elementType.NamedType is null)
            {
                ReportError(
                    "STK3008",
                    $"List element #{elementIndex} of '{elementType.DisplayName}' must use a literal, '_', 'var', list, enum, or aggregate subpattern.",
                    aggregatePattern);
                return;
            }

            var patternType = ResolveSimpleType(aggregatePattern.simpleType());
            if (patternType.Kind != StarkTypeKind.Named
                || patternType.NamedType is null
                || !string.Equals(elementType.NamedType, patternType.NamedType, StringComparison.Ordinal))
            {
                ReportError(
                    "STK3008",
                    $"Nested aggregate switch pattern '{aggregatePattern.GetText()}' must exactly match list element #{elementIndex} of type '{elementType.DisplayName}'.",
                    aggregatePattern);
                return;
            }

            BindAggregatePattern(aggregatePattern, elementType, scope);
            return;
        }

        if (pattern.VAR() is not null)
        {
            if (!SupportsAggregateFieldSubpattern(elementType))
            {
                ReportError(
                    "STK3008",
                    $"List element #{elementIndex} of type '{elementType.DisplayName}' cannot currently be captured in a list switch pattern. List element subpatterns currently support only scalar and text-view element types for direct capture.",
                    pattern);
                return;
            }

            scope.Declare(new VariableSymbol(pattern.Identifier().GetText(), elementType, IsMutable: false, IsConstant: false));
            return;
        }

        if (pattern.literal() is { } literal)
        {
            if (!SupportsAggregateFieldSubpattern(elementType))
            {
                ReportError(
                    "STK3008",
                    $"List element #{elementIndex} of type '{elementType.DisplayName}' cannot currently be matched with a literal in a list switch pattern. List element subpatterns currently support only scalar and text-view element types for literal matching.",
                    pattern);
                return;
            }

            var literalType = EvaluateLiteral(literal).Type;
            if (!CanAssignPatternLiteral(elementType, new ExpressionBinding(
                    literalType,
                    TextLiteral: literal.GetText(),
                    TextLiteralKind: literal.StringLiteral() is not null ? TextLiteralKind.String : TextLiteralKind.Character)))
            {
                ReportError(
                    "STK3002",
                    $"List element pattern '{literal.GetText()}' expects '{elementType.DisplayName}' for element #{elementIndex} but found '{literalType.DisplayName}'.{GetExplicitConversionHint(elementType, literalType)}",
                    literal);
            }
        }
    }

    private void BindNestedAggregateFieldPattern(StarkParser.AggregatePatternContext aggregatePattern, FieldSymbol field, Scope scope)
    {
        if (field.Type.Kind != StarkTypeKind.Named || field.Type.NamedType is null)
        {
            ReportError(
                "STK3008",
                $"Field '{field.Name}' of '{field.Type.DisplayName}' must currently use a literal, '_', or 'var' subpattern.",
                aggregatePattern);
            return;
        }

        var patternType = ResolveSimpleType(aggregatePattern.simpleType());
        if (patternType.Kind != StarkTypeKind.Named
            || patternType.NamedType is null
            || !string.Equals(field.Type.NamedType, patternType.NamedType, StringComparison.Ordinal))
        {
            ReportError(
                "STK3008",
                $"Nested aggregate switch pattern '{aggregatePattern.GetText()}' must exactly match field '{field.Name}' of type '{field.Type.DisplayName}'.",
                aggregatePattern);
            return;
        }

        var namedType = ResolveNamedTypeSymbol(field.Type);
        if (namedType is null)
        {
            ReportError(
                "STK3008",
                $"Nested aggregate switch pattern '{aggregatePattern.GetText()}' could not resolve field information for '{field.Type.DisplayName}'.",
                aggregatePattern);
            return;
        }

        RecordAggregatePattern(field.Type, aggregatePattern);

        var suffix = aggregatePattern.aggregatePatternSuffix();
        if (suffix is null)
        {
            return;
        }

        if (suffix.Identifier() is not null)
        {
            scope.Declare(new VariableSymbol(suffix.Identifier().GetText(), field.Type, IsMutable: false, IsConstant: false));
            return;
        }

        if (suffix.namedPatternPayload() is { } namedPayload)
        {
            BindAggregateNamedFieldPatternPayload(
                aggregatePattern.GetText(),
                namedType,
                namedPayload,
                scope,
                aggregatePattern);
            return;
        }

        var fieldPatterns = suffix.pattern();
        if (fieldPatterns.Length != namedType.OrderedFields.Count)
        {
            ReportError(
                "STK3008",
                $"Nested aggregate switch pattern '{aggregatePattern.GetText()}' expects {namedType.OrderedFields.Count} field subpattern{Pluralize(namedType.OrderedFields.Count)} for '{namedType.Name}' but found {fieldPatterns.Length}.",
                aggregatePattern);
            return;
        }

        for (var index = 0; index < fieldPatterns.Length; index++)
        {
            BindAggregateFieldPattern(fieldPatterns[index], namedType.OrderedFields[index], scope);
        }
    }

    private StarkTypeSymbol ResolveSimpleType(StarkParser.SimpleTypeContext simpleType)
    {
        return EnsureMonomorphizedType(
            _typeResolver!.ResolveSimpleType(simpleType, currentModuleName: CurrentFunctionModuleName),
            Location(simpleType));
    }

    private static bool SupportsAggregateFieldSubpattern(StarkTypeSymbol type)
    {
        return type.Kind is StarkTypeKind.Bool
            or StarkTypeKind.Integer
            or StarkTypeKind.Float
            or StarkTypeKind.RawPointer
            or StarkTypeKind.Ascii
            or StarkTypeKind.Unicode;
    }

    private void CheckVariableDeclaration(
        string declarationKind,
        ParserRuleContext declarationContext,
        StarkParser.Type_Context typeContext,
        IEnumerable<StarkParser.VariableDeclaratorContext> declarators,
        bool isMutable,
        Scope scope)
    {
        var declaredType = ValidateRuntimeValueType(
            ResolveLocalDeclarationType(declarationKind, declarationContext, typeContext),
            typeContext,
            "a local variable type");
        RequireUnsafeForRawPointerType(declaredType, "local raw pointer declarations", typeContext);
        var storageClass = GetLocalDeclarationStorageClass(declarationContext);
        var constProvenanceByDeclarator = new Dictionary<string, ConstProvenanceKind>(StringComparer.Ordinal);

        foreach (var declarator in declarators)
        {
            var declaratorName = declarator.Identifier().GetText();
            var hasFixedTextStorage = TryValidateFixedTextStorageCapacity(
                declarator,
                declaredType,
                storageClass,
                scope,
                out var fixedTextStorageCapacity);
            if (hasFixedTextStorage)
            {
                RecordLocalStorageCapacity(declarationKind, declarator, fixedTextStorageCapacity);
            }

            if (declarator.variableInitializer() is null)
            {
                if (hasFixedTextStorage)
                {
                    ReportError(
                        "STK3002",
                        $"Fixed text buffer '{declarator.Identifier().GetText()}' needs an initializer, for example `left + right`.",
                        declarator);
                }

                constProvenanceByDeclarator[declaratorName] = isMutable
                    ? ConstProvenanceKind.None
                    : ConstProvenanceKind.ImmutableBinding;
                scope.Declare(new VariableSymbol(declaratorName, declaredType, IsMutable: isMutable, IsConstant: false));
                continue;
            }

            if (hasFixedTextStorage
                && !TryMarkFixedTextStorageInitializer(declarator.variableInitializer(), declaredType))
            {
                ReportError(
                    "STK3002",
                    $"Fixed text buffer '{declarator.Identifier().GetText()}' needs a text-building initializer such as `left + right` or `$\"Score: {{score}}\"`. For a direct assignment, remove the `[capacity]` part.",
                    declarator.variableInitializer());
            }

            var initializer = declarator.variableInitializer()!;
            var initializerBinding = CheckVariableInitializer(initializer, declaredType, scope);
            var provenance = TryCreateLocalDeclarationMemoryProvenance(declaredType, initializerBinding, scope);
            var constProvenance = GetLocalDeclarationConstProvenance(isMutable, initializerBinding);
            constProvenanceByDeclarator[declaratorName] = constProvenance;
            var hasConstProvenance = ConstProvenanceFacts.HasPermanentConstProvenance(constProvenance);
            scope.Declare(new VariableSymbol(
                declaratorName,
                declaredType,
                IsMutable: isMutable,
                IsConstant: false,
                HasConstProvenance: hasConstProvenance,
                MemoryRootKey: provenance?.RootKey,
                MemoryRootIsIndependentStorage: provenance?.IsIndependentStorage == true,
                RawPointerElementCountExpression: provenance?.RawPointerElementCountExpression));
        }

        RecordLocalDeclarationType(declarationKind, declaredType, declarationContext, constProvenanceByDeclarator);
    }

    private static string GetLocalDeclarationStorageClass(ParserRuleContext declarationContext)
    {
        return declarationContext switch
        {
            StarkParser.LocalVariableDeclarationContext local => local.storageClass().GetText(),
            StarkParser.LocalForVariableDeclarationContext localFor => localFor.storageClass().GetText(),
            _ => string.Empty
        };
    }

    private bool TryValidateFixedTextStorageCapacity(
        StarkParser.VariableDeclaratorContext declarator,
        StarkTypeSymbol declaredType,
        string storageClass,
        Scope scope,
        out int capacity)
    {
        capacity = 0;
        if (declarator.variableStorageCapacity() is not { } capacityContext)
        {
            return false;
        }

        var name = declarator.Identifier().GetText();
        var isValid = true;
        if (!string.Equals(storageClass, "stack", StringComparison.Ordinal))
        {
            ReportError(
                "STK3002",
                $"Fixed text buffer '{name}' must use stack storage. Write `stack Ascii {name}[4096]` or `stack Unicode {name}[4096]`.",
                capacityContext);
            isValid = false;
        }

        if (!IsTextBufferType(declaredType))
        {
            ReportError(
                "STK3002",
                $"The `[capacity]` after '{name}' is only for stack Ascii or Unicode text buffers.",
                capacityContext);
            isValid = false;
        }

        if (!CompileTimeExpressionEvaluator.TryEvaluateInteger(
                capacityContext.expression(),
                out var capacityValue,
                CreateCompileTimeEvaluationServices(scope)))
        {
            ReportError(
                "STK3002",
                $"Text buffer '{name}' needs a capacity Stark can know at compile time, such as `[4096]`.",
                capacityContext.expression());
            return false;
        }

        if (capacityValue <= 0)
        {
            ReportError(
                "STK3002",
                $"Text buffer '{name}' needs a capacity greater than zero.",
                capacityContext.expression());
            return false;
        }

        if (capacityValue > int.MaxValue)
        {
            ReportError(
                "STK3002",
                $"Text buffer '{name}' capacity is too large for a single fixed buffer. Use a smaller capacity or an explicit library API.",
                capacityContext.expression());
            return false;
        }

        capacity = (int)capacityValue;
        return isValid;
    }

    private bool TryMarkFixedTextStorageInitializer(
        StarkParser.VariableInitializerContext initializer,
        StarkTypeSymbol declaredType)
    {
        if (!IsTextBufferType(declaredType)
            || initializer.expression() is not { } expression)
        {
            return false;
        }

        if (TryGetStandaloneInterpolatedTextLiteral(expression) is { } interpolatedLiteral)
        {
            _fixedTextStorageInterpolatedLiterals.Add(interpolatedLiteral);
            return true;
        }

        if (TryGetStandaloneAdditiveExpression(expression) is not { } additive)
        {
            return false;
        }

        var operators = ExtractOperators<StarkParser.MultiplicativeExpressionContext>(additive);
        if (operators.Count == 0 || operators.Any(static item => item != "+"))
        {
            return false;
        }

        _fixedTextStorageConcatExpressions.Add(additive);
        return true;
    }

    private void ReportStorageCapacityUnsupported(string name, string declarationKind, ParserRuleContext context)
    {
        ReportError(
            "STK3002",
            $"The `[capacity]` after '{name}' is only for stack Ascii or Unicode local variables right now, not for a {declarationKind}.",
            context);
    }

    private StarkTypeSymbol ResolveConstantDeclarationType(
        StarkParser.Type_Context? typeContext,
        IToken? explicitConstIntegerTypeToken,
        StarkParser.ConstantDeclaratorContext declarator,
        Scope scope,
        string usage,
        string? currentModuleName = null,
        bool validateInitializer = true,
        string? localDeclarationKind = null,
        ParserRuleContext? localDeclarationContext = null)
    {
        if (explicitConstIntegerTypeToken is not null)
        {
            return ResolveExplicitConstIntegerDeclarationType(
                explicitConstIntegerTypeToken,
                declarator,
                scope,
                usage,
                validateInitializer);
        }

        if (typeContext is not null)
        {
            if (IsScalarIntegerType(typeContext))
            {
                ReportError(
                    "STK3002",
                    $"Constant '{declarator.Identifier().GetText()}' already has one exact value, so it does not need an integer range. Write `{BuildUntypedConstSuggestion(declarator)}` to let Stark choose the size, or use a bare width such as `const i32 {declarator.Identifier().GetText()} = {GetInitializerSuggestionText(declarator)};`.",
                    typeContext);
            }

            var declaredType = localDeclarationKind is not null && localDeclarationContext is not null
                ? ResolveLocalDeclarationType(localDeclarationKind, localDeclarationContext, typeContext)
                : ResolveType(typeContext, currentModuleName: currentModuleName ?? CurrentFunctionModuleName);
            declaredType = ValidateRuntimeValueType(declaredType, typeContext, usage);

            if (validateInitializer)
            {
                CheckVariableInitializer(declarator.variableInitializer(), declaredType, scope);
            }

            if (TryInferCompileTimeConstantStorageType(declarator.variableInitializer(), scope, out var constantType, out var constant))
            {
                if (constantType.Kind is StarkTypeKind.Integer or StarkTypeKind.Float)
                {
                    return ResolveExplicitConstNumericStorageType(
                        declarator,
                        declaredType,
                        constantType,
                        constant,
                        typeContext,
                        suppressWarnings: IsScalarIntegerType(typeContext));
                }

                if (CanAssign(declaredType, constantType))
                {
                    return constantType;
                }
            }

            return declaredType;
        }

        var inferredType = InferConstantDeclarationType(declarator, scope, usage);
        return ValidateRuntimeValueType(inferredType, declarator, usage);
    }

    private StarkTypeSymbol ResolveExplicitConstIntegerDeclarationType(
        IToken integerTypeToken,
        StarkParser.ConstantDeclaratorContext declarator,
        Scope scope,
        string usage,
        bool validateInitializer)
    {
        var declaredType = ResolveConstIntegerStorageType(integerTypeToken);
        if (!TryInferCompileTimeConstantStorageType(declarator.variableInitializer(), scope, out var constantType, out var constant)
            || constantType.Kind != StarkTypeKind.Integer)
        {
            if (validateInitializer)
            {
                CheckVariableInitializer(declarator.variableInitializer(), declaredType, scope);
            }

            return ValidateRuntimeValueType(declaredType, declarator, usage);
        }

        if (!CanAssign(declaredType, constantType))
        {
            if (declaredType.IsUnsigned
                && TryGetEffectiveIntegerRange(declaredType, out var declaredMin, out var declaredMax)
                && constant.IntegerValue >= declaredMin
                && constant.IntegerValue <= declaredMax)
            {
                var unsignedConstantType = InferConstUnsignedIntegerStorageType(constant.IntegerValue);
                if (!ReportStrictConstIntegerStorageErrorIfNeeded(declarator, declaredType, constant.IntegerValue, integerTypeToken))
                {
                    ReportConstIntegerDemotionIfNeeded(declarator, declaredType, unsignedConstantType, integerTypeToken);
                }

                return ValidateRuntimeValueType(unsignedConstantType, declarator, usage);
            }

            ReportError(
                "STK3002",
                $"Constant '{declarator.Identifier().GetText()}' has value {constant.IntegerValue}, which does not fit in {integerTypeToken.Text}. Use a larger width, or write `{BuildUntypedConstSuggestion(declarator)}` and Stark will choose one.",
                integerTypeToken);
            return constantType;
        }

        var resolvedConstantType = declaredType.IsUnsigned
            ? InferConstUnsignedIntegerStorageType(constant.IntegerValue)
            : constantType;
        if (!ReportStrictConstIntegerStorageErrorIfNeeded(declarator, declaredType, constant.IntegerValue, integerTypeToken))
        {
            ReportConstIntegerDemotionIfNeeded(declarator, declaredType, resolvedConstantType, integerTypeToken);
        }

        return ValidateRuntimeValueType(resolvedConstantType, declarator, usage);
    }

    private StarkTypeSymbol ResolveExplicitConstNumericStorageType(
        StarkParser.ConstantDeclaratorContext declarator,
        StarkTypeSymbol declaredType,
        StarkTypeSymbol constantType,
        CompileTimeConstant constant,
        ParserRuleContext reportContext,
        bool suppressWarnings)
    {
        if (declaredType.Kind == StarkTypeKind.Integer && constantType.Kind == StarkTypeKind.Integer)
        {
            if (!CanAssign(declaredType, constantType))
            {
                ReportError(
                    "STK3002",
                    $"Constant '{declarator.Identifier().GetText()}' has value {constant.IntegerValue}, which does not fit in {FormatConstStorageName(declaredType)}. Use a larger width, or write `{BuildUntypedConstSuggestion(declarator)}` and Stark will choose one.",
                    reportContext);
            }

            if (!suppressWarnings)
            {
                ReportConstIntegerDemotionIfNeeded(declarator, declaredType, constantType, reportContext);
            }

            return constantType;
        }

        if (declaredType.Kind == StarkTypeKind.Float && constantType.Kind == StarkTypeKind.Float)
        {
            return ResolveExplicitConstFloatStorageType(declarator, declaredType, constantType, constant, reportContext, suppressWarnings);
        }

        return constantType;
    }

    private StarkTypeSymbol ResolveExplicitConstFloatStorageType(
        StarkParser.ConstantDeclaratorContext declarator,
        StarkTypeSymbol declaredType,
        StarkTypeSymbol constantType,
        CompileTimeConstant constant,
        ParserRuleContext reportContext,
        bool suppressWarnings)
    {
        if (declaredType.BitWidth == constantType.BitWidth)
        {
            return constantType;
        }

        if (declaredType.BitWidth == 32 && constantType.BitWidth == 64)
        {
            if (!CanRepresentExactlyAsFloat32(constant.FloatValue))
            {
                ReportError(
                    "STK3002",
                    $"Constant '{declarator.Identifier().GetText()}' is written as f32, but {GetInitializerSuggestionText(declarator)} cannot be stored as f32 without changing it. Use f64, or write `{BuildUntypedConstSuggestion(declarator)}`.",
                    reportContext);
                return declaredType;
            }

            if (!suppressWarnings)
            {
                ReportWarning(
                    "STK3025",
                    $"Constant '{declarator.Identifier().GetText()}' fits exactly in f32. Stark will store it as f32; add an `f` suffix if you want the number to say that directly.",
                    reportContext);
            }

            return declaredType;
        }

        if (declaredType.BitWidth > constantType.BitWidth)
        {
            if (!suppressWarnings)
            {
                ReportWarning(
                    "STK3025",
                    $"Constant '{declarator.Identifier().GetText()}' is written as {FormatConstStorageName(constantType)}, so Stark will store it as {FormatConstStorageName(constantType)} instead of {FormatConstStorageName(declaredType)}.",
                    reportContext);
            }

            return constantType;
        }

        return constantType;
    }

    private StarkTypeSymbol InferConstantDeclarationType(
        StarkParser.ConstantDeclaratorContext declarator,
        Scope scope,
        string usage)
    {
        var initializer = declarator.variableInitializer();
        if (TryInferCompileTimeConstantStorageType(initializer, scope, out var constantType))
        {
            return constantType;
        }

        if (initializer.expression() is { } expression)
        {
            return EvaluateExpression(expression, scope, allowFunctionReference: false).Type;
        }

        ReportError(
            "STK3002",
            $"Cannot infer {usage} for constant '{declarator.Identifier().GetText()}'. Add an explicit non-scalar type for aggregate or array initializers.",
            declarator);
        return StarkTypeSymbols.Error;
    }

    private bool TryInferCompileTimeConstantStorageType(
        StarkParser.VariableInitializerContext initializer,
        Scope scope,
        out StarkTypeSymbol type)
    {
        return TryInferCompileTimeConstantStorageType(initializer, scope, out type, out _);
    }

    private bool TryInferCompileTimeConstantStorageType(
        StarkParser.VariableInitializerContext initializer,
        Scope scope,
        out StarkTypeSymbol type,
        out CompileTimeConstant constant)
    {
        type = StarkTypeSymbols.Error;
        constant = default;

        if (!TryEvaluateCompileTimeConstant(initializer, scope, targetType: null, out constant))
        {
            return false;
        }

        type = constant.Kind switch
        {
            CompileTimeConstantKind.Integer => InferConstIntegerStorageType(constant.IntegerValue),
            CompileTimeConstantKind.Float => constant.Type,
            CompileTimeConstantKind.Bool => StarkTypeSymbols.Bool,
            CompileTimeConstantKind.Text => constant.Type,
            CompileTimeConstantKind.FixedArray => constant.Type,
            CompileTimeConstantKind.NamedAggregate => constant.Type,
            CompileTimeConstantKind.EnumAggregate => constant.Type,
            _ => StarkTypeSymbols.Error
        };

        return type.Kind != StarkTypeKind.Error;
    }

    private bool TryEvaluateCompileTimeConstant(
        StarkParser.VariableInitializerContext initializer,
        Scope scope,
        StarkTypeSymbol? targetType,
        out CompileTimeConstant constant)
    {
        constant = default;
        if (initializer.expression() is { } expression)
        {
            var resolver = (TryResolveCompileTimeIdentifier)((string name, out CompileTimeConstant value) =>
                TryResolveCompileTimeConstant(scope, name, out value));
            var evaluated = IsComptimeBlockExpression(expression, out var block)
                ? CompileTimeEvaluator.TryEvaluateBlock(
                    block,
                    CurrentFunctionModuleName,
                    targetType,
                    out constant,
                    resolver)
                : CompileTimeEvaluator.TryEvaluateExpression(
                    expression,
                    CurrentFunctionModuleName,
                    state: null,
                    activeCalls: null,
                    out constant,
                    resolver);
            if (!evaluated)
            {
                return false;
            }

            if (targetType is not null
                && CompileTimeExpressionEvaluator.TryCoerce(constant, targetType, out var coerced))
            {
                constant = coerced;
            }

            return true;
        }

        if (targetType is null)
        {
            return false;
        }

        if (initializer.arrayInitializer() is { } arrayInitializer)
        {
            return TryEvaluateCompileTimeArrayInitializer(arrayInitializer, scope, targetType, out constant);
        }

        return initializer.objectInitializer() is { } objectInitializer
            && TryEvaluateCompileTimeObjectInitializer(objectInitializer, scope, targetType, out constant);
    }

    private bool TryEvaluateCompileTimeArrayInitializer(
        StarkParser.ArrayInitializerContext arrayInitializer,
        Scope scope,
        StarkTypeSymbol targetType,
        out CompileTimeConstant constant)
    {
        constant = default;
        if (targetType.Kind != StarkTypeKind.FixedArray
            || targetType.ElementType is not { } elementType
            || targetType.FixedLength is not int fixedLength
            || arrayInitializer.variableInitializer().Length > fixedLength)
        {
            return false;
        }

        var elements = new CompileTimeConstant[fixedLength];
        var initializedCount = arrayInitializer.variableInitializer().Length;
        for (var index = 0; index < initializedCount; index++)
        {
            if (!TryEvaluateCompileTimeConstant(
                    arrayInitializer.variableInitializer(index),
                    scope,
                    elementType,
                    out var element)
                || !CompileTimeExpressionEvaluator.TryCoerce(element, elementType, out elements[index]))
            {
                return false;
            }
        }

        for (var index = initializedCount; index < fixedLength; index++)
        {
            if (!TryCreateZeroCompileTimeConstant(elementType, out elements[index]))
            {
                return false;
            }
        }

        constant = CompileTimeConstant.FixedArray(elements, targetType);
        return true;
    }

    private bool TryEvaluateCompileTimeObjectInitializer(
        StarkParser.ObjectInitializerContext objectInitializer,
        Scope scope,
        StarkTypeSymbol targetType,
        out CompileTimeConstant constant)
    {
        constant = default;
        if (targetType.Kind != StarkTypeKind.Named
            || ResolveNamedTypeSymbol(targetType) is not { } namedType
            || !TryCreateZeroCompileTimeConstant(targetType, out var current))
        {
            return false;
        }

        var initializedFields = new HashSet<string>(StringComparer.Ordinal);
        foreach (var initializer in objectInitializer.memberInitializer())
        {
            var fieldName = initializer.Identifier().GetText();
            if (!initializedFields.Add(fieldName)
                || !namedType.TryGetField(fieldName, out var field, out var fieldIndex))
            {
                return false;
            }

            var fieldType = field.Type;
            if (!TryEvaluateCompileTimeConstant(
                    initializer.variableInitializer(),
                    scope,
                    fieldType,
                    out var member)
                || !CompileTimeExpressionEvaluator.TryCoerce(member, fieldType, out var coercedMember)
                || !TryWithCompileTimeNamedAggregateField(current, fieldIndex, coercedMember, out current))
            {
                return false;
            }
        }

        constant = current;
        return true;
    }

    private bool TryCreateZeroCompileTimeConstant(
        StarkTypeSymbol type,
        out CompileTimeConstant constant)
    {
        constant = default;
        switch (type.Kind)
        {
            case StarkTypeKind.Integer:
                constant = CompileTimeConstant.Integer(BigInteger.Zero, type);
                return true;
            case StarkTypeKind.Float:
                constant = CompileTimeConstant.Float(0, type);
                return true;
            case StarkTypeKind.Bool:
                constant = CompileTimeConstant.Bool(false);
                return true;
            case StarkTypeKind.RawPointer:
                constant = CompileTimeConstant.Null(type);
                return true;
            case StarkTypeKind.FixedArray when type.ElementType is { } elementType && type.FixedLength is int fixedLength:
                var elements = new CompileTimeConstant[fixedLength];
                for (var index = 0; index < fixedLength; index++)
                {
                    if (!TryCreateZeroCompileTimeConstant(elementType, out elements[index]))
                    {
                        return false;
                    }
                }

                constant = CompileTimeConstant.FixedArray(elements, type);
                return true;
            case StarkTypeKind.Named when ResolveNamedTypeSymbol(type) is { Kind: DeclarationKind.Enum }:
                return false;
            case StarkTypeKind.Named when ResolveNamedTypeSymbol(type) is { } namedType:
                var fieldValues = new CompileTimeConstant[namedType.OrderedFields.Count];
                for (var index = 0; index < namedType.OrderedFields.Count; index++)
                {
                    if (!TryCreateZeroCompileTimeConstant(namedType.OrderedFields[index].Type, out fieldValues[index]))
                    {
                        return false;
                    }
                }

                constant = CompileTimeConstant.NamedAggregate(fieldValues, type);
                return true;
            default:
                return false;
        }
    }

    private static bool TryWithCompileTimeNamedAggregateField(
        CompileTimeConstant aggregate,
        int fieldIndex,
        CompileTimeConstant fieldValue,
        out CompileTimeConstant updated)
    {
        updated = default;
        if (aggregate.Kind != CompileTimeConstantKind.NamedAggregate
            || fieldIndex < 0
            || fieldIndex >= aggregate.Elements.Count)
        {
            return false;
        }

        var elements = aggregate.Elements.ToArray();
        elements[fieldIndex] = fieldValue;
        updated = CompileTimeConstant.NamedAggregate(elements, aggregate.Type);
        return true;
    }

    private static bool IsComptimeBlockExpression(
        StarkParser.ExpressionContext expression,
        [NotNullWhen(true)] out StarkParser.BlockContext? block)
    {
        block = null;
        var assignment = expression.assignmentExpression();
        if (assignment.conditionalExpression()?.logicalOrExpression().logicalAndExpression().Length != 1)
        {
            return false;
        }

        if (assignment.assignmentOperator() is not null
            || assignment.INIT() is not null
            || assignment.unaryExpression() is not null
            || assignment.assignmentExpression() is not null)
        {
            return false;
        }

        var postfix = TryGetSimplePostfixExpression(expression);
        var primary = postfix?.postfixPart().Length == 0
            ? postfix.primaryExpression()
            : null;
        block = primary?.COMPTIME() is not null ? primary.block() : null;
        return block is not null;
    }

    private CompileTimeFunctionEvaluator CompileTimeEvaluator =>
        _compileTimeFunctionEvaluator ??= new CompileTimeFunctionEvaluator(
            TryGetFunctionOverloads,
            TryResolveFunctionSignature,
            TryGetFunctionDeclaration,
            (StarkParser.Type_Context typeContext, string moduleName, ISet<string>? genericParameters, IReadOnlyDictionary<string, ComptimeGenericParameterSymbol>? comptimeGenericParameters) =>
                ResolveType(typeContext, genericParameters, moduleName, comptimeGenericParameters),
            (StarkParser.ConversionTypeContext typeContext, string moduleName, ISet<string>? genericParameters, IReadOnlyDictionary<string, ComptimeGenericParameterSymbol>? comptimeGenericParameters) =>
                _typeResolver!.ResolveConversionType(typeContext, genericParameters, moduleName, comptimeGenericParameters),
            tryResolveLocalDeclarationType: null,
            tryResolveNamedType: TryResolveCompileTimeNamedType,
            tryResolveTraitConformance: TryResolveCompileTimeTraitConformance,
            resolveMethodSignatures: ResolveCompileTimeMethodSignatures,
            tryResolveObjectCreation: TryResolveCompileTimeObjectCreation,
            tryResolveEnumConstructor: TryResolveCompileTimeEnumConstructor,
            tryResolveEnumCall: TryResolveCompileTimeEnumCall,
            tryResolveEnumValue: TryResolveCompileTimeEnumValue,
            tryEvaluateTypeLayout: TryEvaluateCompileTimeTypeLayout,
            tryResolveConcreteLayout: TryResolveCompileTimeConcreteLayout,
            maximumLoopIterations: _context.Options.MaximumCompileTimeLoopIterations);

    private bool TryResolveCompileTimeNamedType(
        StarkTypeSymbol type,
        out NamedTypeSymbol namedType)
    {
        if (!TryResolveCompileTimeNamedTypeInModule(type, CurrentFunctionModuleName, out namedType))
        {
            return false;
        }

        if (namedType is not null
            && namedType.ImplementedTraits.Count == 0
            && TryResolveSourceImplementedTraitNames(type, namedType, out var implementedTraits))
        {
            namedType = namedType with { ImplementedTraitNames = implementedTraits };
        }

        return namedType is not null;
    }

    private bool TryResolveSourceImplementedTraitNames(
        StarkTypeSymbol type,
        NamedTypeSymbol namedType,
        out IReadOnlyList<string> implementedTraits)
    {
        implementedTraits = [];
        if (namedType.Kind is not (DeclarationKind.Struct or DeclarationKind.Record))
        {
            return false;
        }

        var namedTypeName = StarkTypeSymbols.GetGenericBaseName(namedType.Name);
        var sourceTypeName = type.NamedType is { } typeName
            ? StarkTypeSymbols.GetGenericBaseName(typeName)
            : namedTypeName;

        foreach (var module in _loadedModules.Modules.Values)
        {
            foreach (var declaration in module.ParseResult.Root.topLevelDeclaration())
            {
                StarkParser.BaseTraitListContext? baseTraitList;
                StarkParser.TypeParameterListContext? typeParameterList;
                string localName;
                if (declaration.structDeclaration() is { } structDeclaration)
                {
                    localName = structDeclaration.Identifier().GetText();
                    baseTraitList = structDeclaration.baseTraitList();
                    typeParameterList = structDeclaration.typeParameterList();
                }
                else if (declaration.recordDeclaration() is { } recordDeclaration)
                {
                    localName = recordDeclaration.Identifier().GetText();
                    baseTraitList = recordDeclaration.baseTraitList();
                    typeParameterList = recordDeclaration.typeParameterList();
                }
                else
                {
                    continue;
                }

                var qualifiedName = QualifyName(module, localName);
                if (!string.Equals(namedTypeName, qualifiedName, StringComparison.Ordinal)
                    && !string.Equals(namedTypeName, localName, StringComparison.Ordinal)
                    && !string.Equals(sourceTypeName, qualifiedName, StringComparison.Ordinal)
                    && !string.Equals(sourceTypeName, localName, StringComparison.Ordinal))
                {
                    continue;
                }

                var genericParameters = GetGenericParameterNames(typeParameterList);
                if (ResolveBaseTraits(baseTraitList, genericParameters, module.SyntaxModel.ModuleName).Names is { Count: > 0 } traits)
                {
                    implementedTraits = traits;
                    return true;
                }

                return false;
            }
        }

        return false;
    }

    private bool TryResolveCompileTimeTraitConformance(
        StarkTypeSymbol targetType,
        StarkTypeSymbol traitType,
        string moduleName,
        out bool implements)
    {
        implements = false;
        if (!TryResolveCompileTimeNamedTypeInModule(traitType, moduleName, out var traitSymbol)
            || traitSymbol.Kind != DeclarationKind.Trait)
        {
            return false;
        }

        if (!TryResolveCompileTimeNamedTypeInModule(targetType, moduleName, out var targetSymbol))
        {
            implements = false;
            return true;
        }

        if (CompileTimeTraitNameMatches(targetSymbol.ImplementedTraits, traitType, traitSymbol))
        {
            implements = true;
            return true;
        }

        if (TryResolveSourceImplementedTraitNames(targetType, targetSymbol, out var sourceTraits))
        {
            implements = CompileTimeTraitNameMatches(sourceTraits, traitType, traitSymbol);
            return true;
        }

        implements = false;
        return true;
    }

    private IReadOnlyList<TypedFunctionSignature> ResolveCompileTimeMethodSignatures(
        StarkTypeSymbol ownerType,
        string moduleName)
    {
        var cacheKey = $"{moduleName}|{ownerType.DisplayName}";
        if (_compileTimeMethodSignatureCache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        if (!TryResolveCompileTimeNamedTypeInModule(ownerType, moduleName, out var ownerSymbol))
        {
            _compileTimeMethodSignatureCache[cacheKey] = [];
            return [];
        }

        var methods = CompileTimeStructuralFacts.GetOrderedMethodSignatures(
            ownerType,
            ownerSymbol,
            _functionOverloads.Values.SelectMany(static overloads => overloads));
        _compileTimeMethodSignatureCache[cacheKey] = methods;
        return methods;
    }

    private bool TryResolveCompileTimeNamedTypeInModule(
        StarkTypeSymbol type,
        string moduleName,
        out NamedTypeSymbol namedType)
    {
        namedType = null!;
        if (type.Kind != StarkTypeKind.Named || type.NamedType is not { } typeName)
        {
            return false;
        }

        var baseName = StarkTypeSymbols.GetGenericBaseName(typeName);
        if (_namedTypes.TryGetValue(baseName, out namedType!))
        {
            return true;
        }

        if (!baseName.Contains('.', StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(moduleName)
            && _namedTypes.TryGetValue($"{moduleName}.{baseName}", out namedType!))
        {
            return true;
        }

        if (!baseName.Contains('.', StringComparison.Ordinal))
        {
            var importedMatches = _moduleGraph.EnumerateAccessibleModuleQualifiedNames(moduleName, baseName)
                .Where(_namedTypes.ContainsKey)
                .ToArray();
            if (importedMatches.Length == 1)
            {
                namedType = _namedTypes[importedMatches[0]];
                return true;
            }
        }

        return false;
    }

    private static bool CompileTimeTraitNameMatches(
        IReadOnlyList<string> implementedTraits,
        StarkTypeSymbol traitType,
        NamedTypeSymbol traitSymbol)
    {
        var sourceName = traitType.NamedType;
        foreach (var implementedTrait in implementedTraits)
        {
            if (string.Equals(implementedTrait, traitSymbol.Name, StringComparison.Ordinal)
                || (sourceName is not null && string.Equals(implementedTrait, sourceName, StringComparison.Ordinal))
                || string.Equals(LastNameSegment(implementedTrait), LastNameSegment(traitSymbol.Name), StringComparison.Ordinal)
                || (sourceName is not null
                    && string.Equals(LastNameSegment(implementedTrait), LastNameSegment(sourceName), StringComparison.Ordinal)))
            {
                return true;
            }
        }

        return false;
    }

    private static string LastNameSegment(string name)
    {
        var baseName = StarkTypeSymbols.GetGenericBaseName(name);
        var separator = baseName.LastIndexOf('.');
        return separator < 0 ? baseName : baseName[(separator + 1)..];
    }

    private bool TryResolveCompileTimeConcreteLayout(
        StarkTypeSymbol targetType,
        out ConcreteTypeLayout layout)
    {
        layout = null!;
        if (targetType.Kind == StarkTypeKind.Error)
        {
            return false;
        }

        _compileTimeEnumLayouts ??= EnumLayoutBuilder.Build(_syntaxModel.ModuleName, _namedTypes).Layouts;
        layout = ConcreteTypeLayoutHelper.TryGetConcreteTypeLayout(targetType, _namedTypes, _compileTimeEnumLayouts)!;
        return layout is not null;
    }

    private bool TryResolveCompileTimeObjectCreation(
        StarkParser.ObjectCreationExpressionContext expression,
        out CompileTimeObjectCreation objectCreation)
    {
        var location = Location(expression);
        var record = _objectCreations.LastOrDefault(candidate =>
            SourceLocationStartsAt(candidate.Location, location));
        if (record is null)
        {
            objectCreation = null!;
            return false;
        }

        objectCreation = new CompileTimeObjectCreation(
            record.CreatedType,
            record.Constructor,
            record.Members);
        return true;
    }

    private bool TryResolveCompileTimeEnumConstructor(
        StarkParser.EnumConstructorExpressionContext expression,
        out CompileTimeEnumConstruction enumConstruction)
    {
        var location = Location(expression);
        var record = _enumConstructors.LastOrDefault(candidate =>
            SourceLocationStartsAt(candidate.Location, location));
        if (record is not null)
        {
            enumConstruction = new CompileTimeEnumConstruction(
                record.EnumType,
                record.VariantName,
                record.Members);
            return true;
        }

        if (TryResolveEnumCaseTarget(
                expression.enumCaseTarget(),
                out _,
                out _,
                out var enumTypeSymbol,
                out var variant))
        {
            enumConstruction = new CompileTimeEnumConstruction(enumTypeSymbol, variant.Name);
            return true;
        }

        enumConstruction = null!;
        return false;
    }

    private bool TryResolveCompileTimeEnumCall(
        StarkParser.PostfixExpressionContext expression,
        string caseName,
        StarkParser.ArgumentListContext arguments,
        out CompileTimeEnumConstruction enumConstruction)
    {
        var location = Location(arguments);
        var record = _enumCalls.LastOrDefault(candidate =>
            SourceLocationStartsAt(candidate.Location, location));
        if (record is not null)
        {
            enumConstruction = new CompileTimeEnumConstruction(
                record.EnumType,
                record.VariantName);
            return true;
        }

        if (expression.primaryExpression().genericEnumCaseReference() is { } genericEnumCaseReference
            && TryResolveEnumCaseReference(genericEnumCaseReference, out _, out var genericEnumTypeSymbol, out var genericVariant))
        {
            enumConstruction = new CompileTimeEnumConstruction(genericEnumTypeSymbol, genericVariant.Name);
            return true;
        }

        if (TryResolveEnumCaseReference(caseName, out _, out var enumTypeSymbol, out var variant))
        {
            enumConstruction = new CompileTimeEnumConstruction(enumTypeSymbol, variant.Name);
            return true;
        }

        enumConstruction = null!;
        return false;
    }

    private bool TryResolveCompileTimeEnumValue(
        ParserRuleContext expression,
        string caseName,
        out CompileTimeEnumConstruction enumConstruction)
    {
        var location = Location(expression);
        var record = _enumValues.LastOrDefault(candidate =>
            SourceLocationStartsAt(candidate.Location, location));
        if (record is not null)
        {
            enumConstruction = new CompileTimeEnumConstruction(
                record.EnumType,
                record.VariantName);
            return true;
        }

        if (expression is StarkParser.GenericEnumCaseReferenceContext genericEnumCaseReference
            && TryResolveEnumCaseReference(genericEnumCaseReference, out _, out var genericEnumTypeSymbol, out var genericVariant)
            && genericVariant.IsUnit)
        {
            enumConstruction = new CompileTimeEnumConstruction(genericEnumTypeSymbol, genericVariant.Name);
            return true;
        }

        if (TryResolveEnumCaseReference(caseName, out _, out var enumTypeSymbol, out var variant)
            && variant.IsUnit)
        {
            enumConstruction = new CompileTimeEnumConstruction(enumTypeSymbol, variant.Name);
            return true;
        }

        enumConstruction = null!;
        return false;
    }

    private bool TryEvaluateCompileTimeTypeLayout(
        BoundLayoutQueryKind kind,
        StarkTypeSymbol targetType,
        out CompileTimeConstant constant)
    {
        constant = default;
        if (targetType.Kind == StarkTypeKind.Error)
        {
            return false;
        }

        _compileTimeEnumLayouts ??= EnumLayoutBuilder.Build(_syntaxModel.ModuleName, _namedTypes).Layouts;
        if (ConcreteTypeLayoutHelper.TryGetConcreteTypeLayout(targetType, _namedTypes, _compileTimeEnumLayouts) is not { } layout)
        {
            return false;
        }

        var resultType = TypeLayoutQueryFacts.GetResultType(kind);
        constant = CompileTimeConstant.Integer(
            TypeLayoutQueryFacts.GetResultValue(kind, layout),
            resultType);
        return true;
    }

    private static bool SourceLocationStartsAt(SourceLocation left, SourceLocation right)
    {
        return left.Line == right.Line
            && left.Column == right.Column
            && (left.FilePath is null
                || right.FilePath is null
                || string.Equals(left.FilePath, right.FilePath, StringComparison.Ordinal));
    }

    private bool TryGetFunctionDeclaration(TypedFunctionSignature signature, out DeclaredFunctionSyntax declaration)
    {
        if (_functionSyntaxByQualifiedName.TryGetValue(signature.Name, out declaration!))
        {
            return true;
        }

        if (signature.TemplateName is not null
            && _functionSyntaxByQualifiedName.TryGetValue(signature.TemplateName, out declaration!))
        {
            return true;
        }

        declaration = null!;
        return false;
    }

    private bool TryResolveFunctionSignature(
        string name,
        string currentModuleName,
        out TypedFunctionSignature signature)
    {
        if (!name.Contains('.', StringComparison.Ordinal)
            && _functions.TryGetValue($"{currentModuleName}.{name}", out signature!))
        {
            return true;
        }

        if (_functions.TryGetValue(name, out signature!))
        {
            return true;
        }

        if (TryGetFunctionOverloads(name, currentModuleName, out var overloads) && overloads.Count == 1)
        {
            signature = overloads[0];
            return true;
        }

        signature = null!;
        return false;
    }

    private bool TryBuildTypedConstantInitializer(
        StarkParser.VariableInitializerContext initializer,
        StarkTypeSymbol targetType,
        Scope scope,
        out TypedConstantInitializer typedInitializer)
    {
        typedInitializer = default!;

        if (initializer.expression() is not null
            && TryEvaluateCompileTimeConstant(initializer, scope, targetType, out var constant)
            && TryBuildTypedConstantInitializer(constant, targetType, out typedInitializer))
        {
            return true;
        }

        if (initializer.arrayInitializer() is not { } arrayInitializer
            || targetType.Kind != StarkTypeKind.FixedArray
            || targetType.ElementType is not { } elementType
            || targetType.FixedLength is not int fixedLength
            || arrayInitializer.variableInitializer().Length != fixedLength)
        {
            return false;
        }

        var elements = new TypedConstantInitializer[fixedLength];
        for (var index = 0; index < fixedLength; index++)
        {
            if (!TryBuildTypedConstantInitializer(
                    arrayInitializer.variableInitializer(index),
                    elementType,
                    scope,
                    out var elementInitializer))
            {
                typedInitializer = default!;
                return false;
            }

            elements[index] = elementInitializer;
        }

        typedInitializer = new TypedConstantInitializer(
            TypedConstantInitializerKind.FixedArray,
            targetType,
            Elements: elements);
        return true;
    }

    private bool TryBuildTypedConstantInitializer(
        CompileTimeConstant constant,
        StarkTypeSymbol targetType,
        out TypedConstantInitializer typedInitializer)
    {
        var candidate = constant.Kind switch
        {
            CompileTimeConstantKind.Integer => new TypedConstantInitializer(
                TypedConstantInitializerKind.Integer,
                targetType,
                IntegerValue: constant.IntegerValue),
            CompileTimeConstantKind.Float => new TypedConstantInitializer(
                TypedConstantInitializerKind.Float,
                targetType,
                FloatLiteralText: constant.FloatValue.ToString("R", CultureInfo.InvariantCulture)),
            CompileTimeConstantKind.Bool => new TypedConstantInitializer(
                TypedConstantInitializerKind.Bool,
                targetType,
                BoolValue: constant.BoolValue),
            CompileTimeConstantKind.Text when constant.TextLiteral is not null => new TypedConstantInitializer(
                TypedConstantInitializerKind.Text,
                targetType,
                TextLiteralText: constant.TextLiteral),
            CompileTimeConstantKind.Null => new TypedConstantInitializer(
                TypedConstantInitializerKind.Null,
                targetType),
            CompileTimeConstantKind.FixedArray => TryBuildTypedFixedArrayConstantInitializer(
                constant,
                targetType),
            CompileTimeConstantKind.NamedAggregate => TryBuildTypedNamedAggregateConstantInitializer(
                constant,
                targetType),
            CompileTimeConstantKind.EnumAggregate => TryBuildTypedEnumAggregateConstantInitializer(
                constant,
                targetType),
            _ => default!
        };

        typedInitializer = candidate!;
        return candidate is not null;
    }

    private TypedConstantInitializer? TryBuildTypedFixedArrayConstantInitializer(
        CompileTimeConstant constant,
        StarkTypeSymbol targetType)
    {
        if (targetType.Kind != StarkTypeKind.FixedArray
            || targetType.ElementType is not { } elementType
            || targetType.FixedLength is not int fixedLength
            || constant.Elements.Count != fixedLength)
        {
            return null;
        }

        var elements = new TypedConstantInitializer[fixedLength];
        for (var index = 0; index < fixedLength; index++)
        {
            if (!TryBuildTypedConstantInitializer(constant.Elements[index], elementType, out var elementInitializer))
            {
                return null;
            }

            elements[index] = elementInitializer;
        }

        return new TypedConstantInitializer(
            TypedConstantInitializerKind.FixedArray,
            targetType,
            Elements: elements);
    }

    private TypedConstantInitializer? TryBuildTypedNamedAggregateConstantInitializer(
        CompileTimeConstant constant,
        StarkTypeSymbol targetType)
    {
        if (targetType.Kind != StarkTypeKind.Named
            || ResolveNamedTypeSymbol(targetType) is not { } namedType
            || constant.Elements.Count != namedType.OrderedFields.Count)
        {
            return null;
        }

        var elements = new TypedConstantInitializer[namedType.OrderedFields.Count];
        for (var index = 0; index < namedType.OrderedFields.Count; index++)
        {
            var fieldType = namedType.OrderedFields[index].Type;
            if (!TryBuildTypedConstantInitializer(constant.Elements[index], fieldType, out var fieldInitializer))
            {
                return null;
            }

            elements[index] = fieldInitializer;
        }

        return new TypedConstantInitializer(
            TypedConstantInitializerKind.NamedAggregate,
            targetType,
            Elements: elements);
    }

    private TypedConstantInitializer? TryBuildTypedEnumAggregateConstantInitializer(
        CompileTimeConstant constant,
        StarkTypeSymbol targetType)
    {
        if (targetType.Kind != StarkTypeKind.Named
            || ResolveNamedTypeSymbol(targetType) is not { Kind: DeclarationKind.Enum } namedType
            || constant.VariantName is not { } variantName
            || !namedType.TryGetVariant(variantName, out var variant, out _)
            || constant.Elements.Count != variant.Fields.Count)
        {
            return null;
        }

        var elements = new TypedConstantInitializer[variant.Fields.Count];
        for (var index = 0; index < variant.Fields.Count; index++)
        {
            var fieldType = variant.Fields[index].Type;
            if (!TryBuildTypedConstantInitializer(constant.Elements[index], fieldType, out var fieldInitializer))
            {
                return null;
            }

            elements[index] = fieldInitializer;
        }

        return new TypedConstantInitializer(
            TypedConstantInitializerKind.EnumAggregate,
            targetType,
            VariantName: variantName,
            Elements: elements);
    }

    private CompileTimeEvaluationServices CreateCompileTimeEvaluationServices(Scope scope)
    {
        return new CompileTimeEvaluationServices(
            TryResolveIdentifier: (string name, out CompileTimeConstant constant) =>
                TryResolveCompileTimeConstant(scope, name, out constant),
            TryEvaluatePostfixExpression: (StarkParser.PostfixExpressionContext expression, CompileTimeEvaluationServices _, out CompileTimeConstant constant) =>
                TryResolveCompileTimePostfixConstant(scope, expression, out constant),
            TryEvaluateTypeLayoutExpression: (StarkParser.PrimaryExpressionContext expression, out CompileTimeConstant constant) =>
            {
                var kind = expression.ALIGNOF() is not null
                    ? BoundLayoutQueryKind.AlignOf
                    : BoundLayoutQueryKind.SizeOf;
                var targetType = ResolveType(
                    expression.type_(),
                    _currentFunctionGenericParameters,
                    CurrentFunctionModuleName,
                    _currentFunctionComptimeGenericParameters);
                return TryEvaluateCompileTimeTypeLayout(kind, targetType, out constant);
            },
            TryResolveConversionType: (StarkParser.ConversionTypeContext type, out StarkTypeSymbol resolved) =>
            {
                resolved = _typeResolver!.ResolveConversionType(
                    type,
                    _currentFunctionGenericParameters,
                    CurrentFunctionModuleName,
                    _currentFunctionComptimeGenericParameters);
                return resolved.Kind != StarkTypeKind.Error;
            });
    }

    private bool TryResolveCompileTimePostfixConstant(
        Scope scope,
        StarkParser.PostfixExpressionContext expression,
        out CompileTimeConstant constant)
    {
        constant = default;

        var parts = new List<string>(expression.postfixPart().Length + 1);
        CompileTimeConstant? current = null;
        if (expression.primaryExpression().Identifier()?.GetText() is { } identifier)
        {
            parts.Add(identifier);
        }
        else if (expression.primaryExpression().qualifiedName()?.GetText() is { } qualifiedName)
        {
            parts.Add(qualifiedName);
        }
        else
        {
            return false;
        }

        foreach (var postfixPart in expression.postfixPart())
        {
            if (postfixPart.DOT() is null
                || postfixPart.Identifier()?.GetText() is not { } memberName)
            {
                return false;
            }

            if (current is not null)
            {
                if (!TryGetCompileTimeNamedAggregateField(current.Value, memberName, out var fieldValue))
                {
                    return false;
                }

                current = fieldValue;
                parts.Clear();
                continue;
            }

            if (TryResolveCompileTimeConstant(scope, string.Join(".", parts), out var resolved)
                && TryGetCompileTimeNamedAggregateField(resolved, memberName, out var projected))
            {
                current = projected;
                parts.Clear();
                continue;
            }

            parts.Add(memberName);
        }

        if (current is not null)
        {
            constant = current.Value;
            return true;
        }

        return TryResolveCompileTimeConstant(scope, string.Join(".", parts), out constant);
    }

    private bool TryGetCompileTimeNamedAggregateField(
        CompileTimeConstant aggregate,
        string fieldName,
        out CompileTimeConstant fieldValue)
    {
        fieldValue = default;
        if (aggregate.Kind != CompileTimeConstantKind.NamedAggregate
            || ResolveNamedTypeSymbol(aggregate.Type) is not { } namedType
            || !namedType.TryGetField(fieldName, out _, out var fieldIndex)
            || fieldIndex < 0
            || fieldIndex >= aggregate.Elements.Count)
        {
            return false;
        }

        fieldValue = aggregate.Elements[fieldIndex];
        return true;
    }

    private bool TryEvaluateCompileTimeIntegerExpression(
        ParserRuleContext expression,
        Scope scope,
        StarkTypeSymbol? expectedType,
        out ExpressionBinding binding)
    {
        binding = null!;
        if (!CompileTimeExpressionEvaluator.TryEvaluate(
                expression,
                out var constant,
                CreateCompileTimeEvaluationServices(scope))
            || constant.Kind != CompileTimeConstantKind.Integer)
        {
            return false;
        }

        if (expectedType is not null
            && CompileTimeExpressionEvaluator.TryCoerce(constant, expectedType, out var coerced))
        {
            constant = coerced;
        }

        binding = new ExpressionBinding(constant.Type);
        return true;
    }

    private static bool TryResolveCompileTimeConstant(
        Scope scope,
        string name,
        out CompileTimeConstant constant)
    {
        if (scope.TryLookup(name, out var symbol)
            && symbol.BindingKind is null or GlobalBindingKind.Const
            && symbol.ConstantValue is { } value)
        {
            constant = value;
            return true;
        }

        constant = default;
        return false;
    }

    private bool TryResolveOpenComptimeGenericConstant(
        string name,
        out CompileTimeConstant constant)
    {
        if (_currentFunctionComptimeGenericParameters is { Count: > 0 } parameters
            && parameters.TryGetValue(name, out var parameter)
            && parameter.Type.Kind == StarkTypeKind.Integer)
        {
            constant = CompileTimeConstant.SymbolicInteger(parameter.Type);
            return true;
        }

        constant = default;
        return false;
    }

    private CompileTimeFunctionEvaluationState? CreateOpenComptimeGenericEvaluationState()
    {
        if (_currentFunctionGenericParameters is not { Count: > 0 }
            && _currentFunctionComptimeGenericParameters is not { Count: > 0 })
        {
            return null;
        }

        var state = new CompileTimeFunctionEvaluationState();
        state.SetGenericContext(
            _currentFunctionGenericParameters,
            typeSubstitution: null,
            _currentFunctionComptimeGenericParameters,
            comptimeValueSubstitution: null);
        return state;
    }

    private static StarkTypeSymbol InferConstIntegerStorageType(BigInteger value)
    {
        if (IntegerRangeStorageFacts.TryGetSmallestTypeForRange(value, value, out var type))
        {
            return type;
        }

        return StarkTypeSymbols.CompileTimeInteger;
    }

    private static StarkTypeSymbol InferConstUnsignedIntegerStorageType(BigInteger value)
    {
        foreach (var width in SupportedIntegerLiteralWidths)
        {
            var max = (BigInteger.One << width) - BigInteger.One;
            if (value >= BigInteger.Zero && value <= max)
            {
                return StarkTypeSymbols.Integer(width, value, value, isUnsigned: true);
            }
        }

        return StarkTypeSymbols.CompileTimeInteger;
    }

    private static StarkTypeSymbol ResolveConstIntegerStorageType(IToken integerTypeToken)
    {
        var text = integerTypeToken.Text;
        var isUnsigned = text[0] == 'u';
        var width = int.Parse(text[1..], CultureInfo.InvariantCulture);
        IntegerRangeStorageFacts.GetIntegerTypeBounds(width, isUnsigned, out var min, out var max);
        return StarkTypeSymbols.Integer(width, min, max, isUnsigned);
    }

    private static bool IsScalarIntegerType(StarkParser.Type_Context typeContext)
    {
        return typeContext.arraySuffix().Length == 0
            && typeContext.nonArrayType().integerType() is not null;
    }

    private void ReportConstIntegerDemotionIfNeeded(
        StarkParser.ConstantDeclaratorContext declarator,
        StarkTypeSymbol declaredType,
        StarkTypeSymbol constantType,
        ParserRuleContext context)
    {
        if (declaredType.BitWidth == constantType.BitWidth)
        {
            return;
        }

        ReportWarning(
            "STK3025",
            $"Constant '{declarator.Identifier().GetText()}' fits in {FormatConstStorageName(constantType)}, so Stark will store it as {FormatConstStorageName(constantType)} instead of {FormatConstStorageName(declaredType)}. You can write `{BuildUntypedConstSuggestion(declarator)}` to let Stark pick this automatically.",
            context);
    }

    private bool ReportStrictConstIntegerStorageErrorIfNeeded(
        StarkParser.ConstantDeclaratorContext declarator,
        StarkTypeSymbol declaredType,
        BigInteger value,
        IToken token)
    {
        if (!_context.Options.EnforceIntegerRangeStorageRules
            || !IntegerRangeStorageFacts.TryGetSmallestTypeForRange(value, value, out var suggestedType)
            || declaredType.BitWidth == suggestedType.BitWidth && declaredType.IsUnsigned == suggestedType.IsUnsigned)
        {
            return false;
        }

        ReportError(
            "STK3014",
            $"Constant '{declarator.Identifier().GetText()}' is written as {token.Text}, but value {value} fits in {FormatConstStorageName(suggestedType)}. Use `{FormatConstStorageName(suggestedType)}` or write `{BuildUntypedConstSuggestion(declarator)}` and Stark will choose it.",
            token);
        return true;
    }

    private void ReportConstIntegerDemotionIfNeeded(
        StarkParser.ConstantDeclaratorContext declarator,
        StarkTypeSymbol declaredType,
        StarkTypeSymbol constantType,
        IToken token)
    {
        if (declaredType.BitWidth == constantType.BitWidth)
        {
            return;
        }

        ReportWarning(
            "STK3025",
            $"Constant '{declarator.Identifier().GetText()}' fits in {FormatConstStorageName(constantType)}, so Stark will store it as {FormatConstStorageName(constantType)} instead of {token.Text}. You can write `{BuildUntypedConstSuggestion(declarator)}` to let Stark pick this automatically.",
            token);
    }

    private static bool CanRepresentExactlyAsFloat32(double value)
    {
        var single = (float)value;
        return !float.IsInfinity(single) && (double)single == value;
    }

    private static string FormatConstStorageName(StarkTypeSymbol type)
    {
        return type.Kind switch
        {
            StarkTypeKind.Integer when type.BitWidth is int width => $"{(type.IsUnsigned ? "u" : "i")}{width}",
            StarkTypeKind.Float when type.BitWidth is int width => $"f{width}",
            _ => type.DisplayName
        };
    }

    private static string BuildUntypedConstSuggestion(StarkParser.ConstantDeclaratorContext declarator)
    {
        return $"const {declarator.Identifier().GetText()} = {GetInitializerSuggestionText(declarator)};";
    }

    private static string GetInitializerSuggestionText(StarkParser.ConstantDeclaratorContext declarator)
    {
        var initializer = declarator.variableInitializer();
        if (initializer.expression() is { } expression)
        {
            return expression.GetText();
        }

        var text = initializer.GetText();
        return text.StartsWith('=') ? text[1..] : text;
    }

    private StarkTypeSymbol ResolveLocalDeclarationType(
        string declarationKind,
        ParserRuleContext declarationContext,
        StarkParser.Type_Context typeContext)
    {
        return TryGetPublishedTemplateLocalDeclarationType(declarationKind, declarationContext, out var publishedType)
            ? EnsureMonomorphizedType(publishedType, Location(typeContext))
            : ResolveType(typeContext, currentModuleName: CurrentFunctionModuleName);
    }

    private bool TryGetPublishedTemplateObjectCreationSummary(
        StarkParser.ObjectCreationExpressionContext expression,
        out ImportedTemplateObjectCreationSummary? summary)
    {
        summary = null;

        if (_currentImportedTemplateObjectCreations is not { Count: > 0 } objectCreations
            || _currentImportedTemplateObjectCreationOrdinals is not { } objectCreationOrdinals
            || !objectCreationOrdinals.TryGetValue(expression, out var objectCreationOrdinal)
            || objectCreationOrdinal >= objectCreations.Count)
        {
            return false;
        }

        summary = objectCreations[objectCreationOrdinal];
        return true;
    }

    private bool TryGetPublishedTemplateEnumConstructorSummary(
        StarkParser.EnumConstructorExpressionContext expression,
        out ImportedTemplateEnumConstructorSummary? summary)
    {
        summary = null;

        if (_currentImportedTemplateEnumConstructors is not { Count: > 0 } enumConstructors
            || _currentImportedTemplateEnumConstructorOrdinals is not { } enumConstructorOrdinals
            || !enumConstructorOrdinals.TryGetValue(expression, out var enumConstructorOrdinal)
            || !enumConstructors.TryGetValue(enumConstructorOrdinal, out summary))
        {
            return false;
        }

        return true;
    }

    private bool TryGetPublishedTemplateConversionType(
        StarkParser.UnaryExpressionContext expression,
        out StarkTypeSymbol type)
    {
        if (_currentImportedTemplateConversions is not { Count: > 0 } conversions
            || _currentImportedTemplateConversionOrdinals is not { } conversionOrdinals
            || !conversionOrdinals.TryGetValue(expression, out var conversionOrdinal)
            || !conversions.TryGetValue(conversionOrdinal, out type!))
        {
            type = StarkTypeSymbols.Error;
            return false;
        }

        return true;
    }

    private bool TryGetPublishedTemplateLocalDeclarationType(
        string declarationKind,
        ParserRuleContext declarationContext,
        out StarkTypeSymbol type)
    {
        if (_currentImportedTemplateLocalDeclarations is not null
            && _currentImportedTemplateLocalDeclarations.TryGetValue(
                TemplateLocalDeclarationFacts.BuildLookupKey(
                    declarationKind,
                    declarationContext.Start.Line,
                    declarationContext.Start.Column + 1),
                out type!))
        {
            return true;
        }

        type = StarkTypeSymbols.Error;
        return false;
    }

    private void RecordLocalDeclarationType(
        string declarationKind,
        StarkTypeSymbol type,
        ParserRuleContext declarationContext,
        IReadOnlyDictionary<string, ConstProvenanceKind>? constProvenanceByDeclarator = null)
    {
        _localDeclarations.Add(new LocalDeclarationTypingRecord(
            declarationKind,
            type,
            Location(declarationContext),
            _currentFunctionName,
            constProvenanceByDeclarator));
    }

    private void RecordLocalStorageCapacity(
        string declarationKind,
        StarkParser.VariableDeclaratorContext declarator,
        int capacity)
    {
        _localStorageCapacities.Add(new LocalStorageCapacityTypingRecord(
            declarationKind,
            declarator.Identifier().GetText(),
            capacity,
            Location(declarator),
            _currentFunctionName));
    }

    private void RecordEnumConstructor(
        StarkTypeSymbol enumType,
        string variantName,
        ParserRuleContext constructorContext,
        IReadOnlyList<EnumConstructorMemberTypingRecord>? members = null)
    {
        var location = Location(constructorContext);
        _enumConstructors.Add(new EnumConstructorTypingRecord(
            enumType,
            variantName,
            location,
            _currentFunctionName,
            members));
        _boundOperations.Add(new BoundEnumConstructionOperation(
            enumType,
            variantName,
            members,
            location,
            _currentFunctionName));
    }

    private void RecordEnumCall(
        StarkTypeSymbol enumType,
        string variantName,
        ParserRuleContext callContext)
    {
        var location = Location(callContext);
        _enumCalls.Add(new EnumCallTypingRecord(
            enumType,
            variantName,
            location,
            _currentFunctionName));
        _boundOperations.Add(new BoundEnumCallOperation(
            enumType,
            variantName,
            location,
            _currentFunctionName));
    }

    private void RecordEnumValue(
        StarkTypeSymbol enumType,
        string variantName,
        IToken token)
    {
        var location = Location(token);
        _enumValues.Add(new EnumValueTypingRecord(
            enumType,
            variantName,
            location,
            _currentFunctionName));
        _boundOperations.Add(new BoundEnumValueOperation(
            enumType,
            variantName,
            location,
            _currentFunctionName));
    }

    private void RecordEnumPattern(
        StarkTypeSymbol enumType,
        string variantName,
        ParserRuleContext context,
        IReadOnlyList<EnumPatternMemberTypingRecord>? members = null)
    {
        _enumPatterns.Add(new EnumPatternTypingRecord(
            enumType,
            variantName,
            Location(context),
            _currentFunctionName,
            members));
    }

    private void RecordAggregatePattern(
        StarkTypeSymbol type,
        ParserRuleContext context,
        IReadOnlyList<AggregatePatternMemberTypingRecord>? members = null)
    {
        _aggregatePatterns.Add(new AggregatePatternTypingRecord(
            type,
            Location(context),
            _currentFunctionName,
            members));
    }

    private bool TryGetPublishedTemplateEnumPattern(
        ParserRuleContext context,
        out ImportedTemplateEnumPatternSummary summary,
        out StarkTypeSymbol enumTypeSymbol,
        out NamedTypeSymbol enumType,
        out EnumVariantSymbol variant)
    {
        summary = null!;
        enumTypeSymbol = StarkTypeSymbols.Error;
        enumType = null!;
        variant = null!;

        if (_currentImportedTemplateEnumPatterns is not { Count: > 0 }
            || _currentImportedTemplateEnumPatternOrdinals is not { } enumPatternOrdinals
            || !enumPatternOrdinals.TryGetValue(context, out var enumPatternOrdinal)
            || !_currentImportedTemplateEnumPatterns.TryGetValue(enumPatternOrdinal, out var publishedEnumPattern))
        {
            return false;
        }

        summary = publishedEnumPattern;
        enumTypeSymbol = EnsureMonomorphizedType(publishedEnumPattern.EnumType, Location(context));
        enumType = ResolveNamedTypeSymbol(enumTypeSymbol)!;
        if (enumType is null
            || !enumType.TryGetVariant(publishedEnumPattern.VariantName, out variant, out _))
        {
            summary = null!;
            enumTypeSymbol = StarkTypeSymbols.Error;
            enumType = null!;
            variant = null!;
            return false;
        }

        return true;
    }

    private bool TryGetPublishedTemplateAggregatePattern(
        StarkParser.AggregatePatternContext context,
        out StarkTypeSymbol type,
        out NamedTypeSymbol namedType)
    {
        type = StarkTypeSymbols.Error;
        namedType = null!;

        if (_currentImportedTemplateAggregatePatterns is not { Count: > 0 }
            || _currentImportedTemplateEnumPatternOrdinals is not { } patternOrdinals
            || !patternOrdinals.TryGetValue(context, out var patternOrdinal)
            || !_currentImportedTemplateAggregatePatterns.TryGetValue(patternOrdinal, out var publishedAggregatePattern))
        {
            return false;
        }

        type = EnsureMonomorphizedType(publishedAggregatePattern.Type, Location(context));
        namedType = ResolveNamedTypeSymbol(type)!;
        if (type.Kind != StarkTypeKind.Named
            || namedType is null)
        {
            type = StarkTypeSymbols.Error;
            namedType = null!;
            return false;
        }

        return true;
    }

    private bool TryGetPublishedTemplateEnumValueBinding(
        StarkParser.PrimaryExpressionContext expression,
        bool allowFunctionReference,
        out ExpressionBinding binding)
    {
        binding = default!;

        if (_currentImportedTemplateEnumValues is not { Count: > 0 }
            || _currentImportedTemplateEnumValueOrdinals is not { } enumValueOrdinals
            || !enumValueOrdinals.TryGetValue(expression, out var enumValueOrdinal)
            || !_currentImportedTemplateEnumValues.TryGetValue(enumValueOrdinal, out var publishedEnumValue))
        {
            return false;
        }

        var enumTypeSymbol = EnsureMonomorphizedType(publishedEnumValue.EnumType, Location(expression));
        var enumType = ResolveNamedTypeSymbol(enumTypeSymbol);
        if (enumType is null
            || !enumType.TryGetVariant(publishedEnumValue.VariantName, out var variant, out _))
        {
            return false;
        }

        binding = CreateEnumCaseValueBinding(
            $"{enumTypeSymbol.DisplayName}.{publishedEnumValue.VariantName}",
            enumTypeSymbol,
            enumType,
            variant,
            expression.Start,
            allowFunctionReference);
        return true;
    }

    private bool TryGetPublishedTemplateEnumCallBinding(
        StarkParser.PostfixExpressionContext expression,
        out ExpressionBinding binding)
    {
        binding = default!;

        if (_currentImportedTemplateEnumCalls is not { Count: > 0 }
            || expression.postfixPart().Length == 0
            || expression.postfixPart()[0].argumentList() is not { } firstArgumentList
            || _currentImportedTemplateEnumCallOrdinals is not { } enumCallOrdinals
            || !enumCallOrdinals.TryGetValue(firstArgumentList, out var enumCallOrdinal)
            || !_currentImportedTemplateEnumCalls.TryGetValue(enumCallOrdinal, out var publishedEnumCall))
        {
            return false;
        }

        var enumTypeSymbol = EnsureMonomorphizedType(publishedEnumCall.EnumType, Location(expression.primaryExpression()));
        var enumType = ResolveNamedTypeSymbol(enumTypeSymbol);
        if (enumType is null
            || !enumType.TryGetVariant(publishedEnumCall.VariantName, out var variant, out _))
        {
            return false;
        }

        binding = CreateEnumCaseValueBinding(
            $"{enumTypeSymbol.DisplayName}.{publishedEnumCall.VariantName}",
            enumTypeSymbol,
            enumType,
            variant,
            expression.primaryExpression().Start,
            allowFunctionReference: true);
        return true;
    }

    private bool TryGetPublishedTemplateDirectCallBinding(
        StarkParser.PostfixExpressionContext expression,
        StarkTypeSymbol? expectedType,
        out ExpressionBinding binding)
    {
        binding = default!;

        if (_currentImportedTemplateDirectCalls is not { Count: > 0 }
            || expression.postfixPart().Length == 0
            || expression.postfixPart()[0].argumentList() is not { } firstArgumentList
            || _currentImportedTemplateDirectCallOrdinals is not { } directCallOrdinals
            || !directCallOrdinals.TryGetValue(firstArgumentList, out var directCallOrdinal)
            || !_currentImportedTemplateDirectCalls.TryGetValue(directCallOrdinal, out var publishedSignature)
            || !IsPublishedDirectCallCompatible(expression, publishedSignature))
        {
            return false;
        }

        var resolvedSignature = CacheFunctionInstantiation(publishedSignature);
        if (expectedType is not null && !TypeCompatibilityFacts.CanAssign(expectedType, resolvedSignature.ReturnType))
        {
            return false;
        }

        binding = new ExpressionBinding(
            resolvedSignature.ReturnType,
            NamedType: ResolveNamedTypeSymbol(resolvedSignature.ReturnType),
            Function: resolvedSignature,
            DiagnosticName: $"function '{resolvedSignature.DisplaySourceName}'");
        return true;
    }

    private bool TryApplyPublishedTemplateFieldAccess(
        ExpressionBinding target,
        StarkParser.PostfixPartContext postfixPart,
        out ExpressionBinding binding)
    {
        binding = default!;

        if (target.NamespaceName is not null
            || _currentImportedTemplateFieldAccesses is not { Count: > 0 }
            || _currentImportedTemplateFieldAccessOrdinals is not { } fieldAccessOrdinals
            || !fieldAccessOrdinals.TryGetValue(postfixPart, out var fieldAccessOrdinal)
            || !_currentImportedTemplateFieldAccesses.TryGetValue(fieldAccessOrdinal, out var publishedFieldAccess))
        {
            return false;
        }

        var fieldType = EnsureMonomorphizedType(publishedFieldAccess.FieldType, Location(postfixPart));
        var projectedType = ProjectProjectionType(target, fieldType);
        var isAddressMutable = CanMutateAddressProjection(target, projectedType);
        var isAssignable = isAddressMutable;
        binding = new ExpressionBinding(
            projectedType,
            IsAssignable: isAssignable,
            NamedType: ResolveNamedTypeSymbol(projectedType),
            DiagnosticName: $"member '{publishedFieldAccess.FieldName}'",
            IsAddressable: target.IsAddressable,
            IsAddressMutable: isAddressMutable,
            RootGlobalName: target.RootGlobalName,
            RootGlobalBindingKind: target.RootGlobalBindingKind,
            UsesFrozenProjectionSemantics: UsesFrozenProjectionSemantics(target),
            HasConstProvenance: HasConstProvenance(target),
            AssignmentErrorMessage: target.RootGlobalBindingKind is not null
                && target.RootGlobalName is not null
                && !isAssignable
                ? DescribeGlobalMutationError(target.RootGlobalName, target.RootGlobalBindingKind.Value, $"member '{publishedFieldAccess.FieldName}'")
                : target.Type.AccessKind == StarkAccessKind.Frozen
                    ? DescribeFrozenMutationError($"member '{publishedFieldAccess.FieldName}'")
                    : target.AssignmentErrorMessage,
            MemoryRootKey: target.MemoryRootKey is { } memoryRootKey
                ? $"{memoryRootKey}.{publishedFieldAccess.FieldName}"
                : null,
            MemoryRootIsIndependentStorage: target.MemoryRootIsIndependentStorage,
            IsMisalignedFieldProjection: target.IsMisalignedFieldProjection
                || target.NamedType is { } targetNamedType
                    && IsMisalignedLayoutFieldProjection(targetNamedType, publishedFieldAccess.FieldName));
        return true;
    }

    private bool TryGetPublishedTemplateMemberCallBinding(
        ExpressionBinding receiver,
        string memberName,
        StarkParser.ArgumentListContext arguments,
        StarkTypeSymbol? expectedType,
        out ExpressionBinding binding)
    {
        binding = default!;

        if (receiver.NamespaceName is not null
            || _currentImportedTemplateMemberCalls is not { Count: > 0 }
            || _currentImportedTemplateMemberCallOrdinals is not { } memberCallOrdinals
            || !memberCallOrdinals.TryGetValue(arguments, out var memberCallOrdinal)
            || !_currentImportedTemplateMemberCalls.TryGetValue(memberCallOrdinal, out var publishedSignature)
            || !IsPublishedMemberCallCompatible(memberName, publishedSignature))
        {
            return false;
        }

        var resolvedSignature = CacheFunctionInstantiation(publishedSignature);
        if (expectedType is not null && !TypeCompatibilityFacts.CanAssign(expectedType, resolvedSignature.ReturnType))
        {
            return false;
        }

        binding = new ExpressionBinding(
            resolvedSignature.ReturnType,
            NamedType: ResolveNamedTypeSymbol(resolvedSignature.ReturnType),
            Function: resolvedSignature,
            DiagnosticName: $"method '{resolvedSignature.DisplaySourceName}'",
            Receiver: receiver);
        return true;
    }

    private static bool IsPublishedDirectCallCompatible(
        StarkParser.PostfixExpressionContext expression,
        TypedFunctionSignature signature)
    {
        var calleeText = expression.primaryExpression()?.GetText();
        return string.IsNullOrWhiteSpace(calleeText)
            || IsPublishedCallNameCompatible(calleeText!, signature.SourceName)
            || IsPublishedCallNameCompatible(calleeText!, signature.TemplateName)
            || IsPublishedCallNameCompatible(calleeText!, signature.Name);
    }

    private static bool IsPublishedMemberCallCompatible(string memberName, TypedFunctionSignature signature)
    {
        return IsPublishedMemberNameCompatible(memberName, signature.SourceName)
            || IsPublishedMemberNameCompatible(memberName, signature.TemplateName)
            || IsPublishedMemberNameCompatible(memberName, signature.Name);
    }

    private static bool IsPublishedCallNameCompatible(string callText, string? publishedName)
    {
        if (string.IsNullOrWhiteSpace(publishedName))
        {
            return false;
        }

        var normalizedCall = NormalizePublishedCallName(callText);
        var normalizedPublished = NormalizePublishedCallName(publishedName);
        return string.Equals(normalizedCall, normalizedPublished, StringComparison.Ordinal)
            || normalizedPublished.EndsWith($".{normalizedCall}", StringComparison.Ordinal);
    }

    private static bool IsPublishedMemberNameCompatible(string memberName, string? publishedName)
    {
        if (string.IsNullOrWhiteSpace(publishedName))
        {
            return false;
        }

        return string.Equals(
            NormalizePublishedCallName(memberName),
            GetPublishedCallLastSegment(publishedName),
            StringComparison.Ordinal);
    }

    private static string GetPublishedCallLastSegment(string name)
    {
        var normalized = NormalizePublishedCallName(name);
        var lastDot = normalized.LastIndexOf('.');
        return lastDot < 0 ? normalized : normalized[(lastDot + 1)..];
    }

    private static string NormalizePublishedCallName(string name)
    {
        var normalized = name.Trim();
        var genericMarker = normalized.IndexOf("#(", StringComparison.Ordinal);
        if (genericMarker >= 0)
        {
            normalized = normalized[..genericMarker];
        }

        var genericTypeMarker = normalized.IndexOf('<');
        if (genericTypeMarker >= 0)
        {
            normalized = normalized[..genericTypeMarker];
        }

        return normalized;
    }

    private void RecordDirectCall(
        TypedFunctionSignature signature,
        ParserRuleContext callContext,
        IReadOnlyList<CallArgumentTypingRecord>? arguments = null)
    {
        var location = Location(callContext);
        _directCalls.Add(new DirectCallTypingRecord(
            signature,
            location,
            _currentFunctionName,
            arguments));
        _boundOperations.Add(new BoundDirectCallOperation(
            signature,
            arguments,
            location,
            _currentFunctionName));
    }

    private void RecordConversion(
        StarkTypeSymbol targetType,
        ParserRuleContext conversionContext)
    {
        _conversions.Add(new ConversionTypingRecord(
            targetType,
            Location(conversionContext),
            _currentFunctionName));
    }

    private void RecordFieldAccess(
        string fieldName,
        int fieldIndex,
        StarkTypeSymbol fieldType,
        ParserRuleContext fieldAccessContext)
    {
        _fieldAccesses.Add(new FieldAccessTypingRecord(
            fieldName,
            fieldIndex,
            fieldType,
            Location(fieldAccessContext),
            _currentFunctionName));
    }

    private void RecordMemberCall(
        TypedFunctionSignature signature,
        ParserRuleContext callContext,
        IReadOnlyList<CallArgumentTypingRecord>? arguments = null)
    {
        var location = Location(callContext);
        _memberCalls.Add(new MemberCallTypingRecord(
            signature,
            location,
            _currentFunctionName,
            arguments));
        var receiver = arguments?.FirstOrDefault(static argument => argument.IsReceiver);
        _boundOperations.Add(new BoundMemberCallOperation(
            signature,
            receiver?.ArgumentType ?? signature.Parameters.FirstOrDefault()?.Type ?? StarkTypeSymbols.Error,
            receiver?.ArgumentIsAddressable ?? false,
            receiver?.ArgumentIsMutable ?? false,
            arguments,
            location,
            _currentFunctionName));
    }

    private void RecordIndexAccess(
        string kind,
        StarkTypeSymbol sourceType,
        StarkTypeSymbol resultType,
        int indexCount,
        ParserRuleContext context)
    {
        var location = Location(context);
        _indexAccesses.Add(new IndexAccessTypingRecord(
            kind,
            sourceType,
            resultType,
            indexCount,
            location,
            _currentFunctionName));
        _boundOperations.Add(new BoundIndexAccessOperation(
            ClassifyBoundIndexAccess(kind),
            kind,
            sourceType,
            resultType,
            indexCount,
            location,
            _currentFunctionName));
    }

    private void RecordDynamicStorageOperation(
        string operationName,
        ExpressionBinding receiver,
        StarkTypeSymbol resultType,
        int argumentCount,
        ParserRuleContext context)
    {
        var location = Location(context);
        _dynamicStorageOperations.Add(new DynamicStorageOperationTypingRecord(
            operationName,
            receiver.Type,
            resultType,
            argumentCount,
            location,
            _currentFunctionName,
            ReceiverIsAddressable: receiver.IsAddressable,
            ReceiverIsMutable: receiver.IsAddressMutable));
        _boundOperations.Add(new BoundDynamicStorageOperation(
            operationName,
            receiver.Type,
            resultType,
            argumentCount,
            receiver.IsAddressable,
            receiver.IsAddressMutable,
            location,
            _currentFunctionName));
    }

    private static BoundIndexAccessKind ClassifyBoundIndexAccess(string kind)
    {
        return kind switch
        {
            "element" => BoundIndexAccessKind.Element,
            "text-element" => BoundIndexAccessKind.TextElement,
            "text-slice" => BoundIndexAccessKind.TextSlice,
            "dynamic-element" => BoundIndexAccessKind.DynamicElement,
            "dynamic-slice" => BoundIndexAccessKind.DynamicSlice,
            "raw-pointer-region" => BoundIndexAccessKind.RawPointerRegion,
            _ => kind.Contains("slice", StringComparison.Ordinal)
                ? BoundIndexAccessKind.Slice
                : BoundIndexAccessKind.Element
        };
    }

    private IReadOnlyList<CallArgumentTypingRecord> BuildCallArgumentRecords(
        IReadOnlyList<TypedParameterSymbol> parameters,
        ExpressionBinding? receiver,
        IReadOnlyList<ExpressionBinding> explicitArguments,
        int receiverOffset)
    {
        var records = new List<CallArgumentTypingRecord>(Math.Min(parameters.Count, explicitArguments.Count + receiverOffset));
        if (receiver is not null && parameters.Count > 0)
        {
            records.Add(BuildCallArgumentRecord(
                parameterIndex: 0,
                sourceArgumentIndex: -1,
                parameters[0],
                receiver,
                isReceiver: true));
        }

        var explicitParameterCount = Math.Max(0, parameters.Count - receiverOffset);
        for (var index = 0; index < Math.Min(explicitParameterCount, explicitArguments.Count); index++)
        {
            var parameterIndex = index + receiverOffset;
            records.Add(BuildCallArgumentRecord(
                parameterIndex,
                index,
                parameters[parameterIndex],
                explicitArguments[index],
                isReceiver: false));
        }

        return records;
    }

    private static CallArgumentTypingRecord BuildCallArgumentRecord(
        int parameterIndex,
        int sourceArgumentIndex,
        TypedParameterSymbol parameter,
        ExpressionBinding argument,
        bool isReceiver)
    {
        return new CallArgumentTypingRecord(
            parameterIndex,
            sourceArgumentIndex,
            parameter.Type,
            argument.Type,
            isReceiver,
            RequiresAddressableCallArgument(parameter, isReceiver),
            RequiresMutableCallArgument(parameter, isReceiver),
            parameter.IsConst,
            argument.IsAddressable,
            argument.IsAddressMutable,
            HasConstArgumentProvenance(argument));
    }

    private static bool RequiresAddressableCallArgument(TypedParameterSymbol parameter, bool isReceiver)
    {
        if (isReceiver)
        {
            return false;
        }

        if (parameter.Type.Kind == StarkTypeKind.Closure)
        {
            return false;
        }

        return !parameter.IsConst
            && (parameter.Type.InitializationKind != StarkInitializationKind.None
                || parameter.Type.BorrowKind != StarkBorrowKind.None);
    }

    private static bool RequiresMutableCallArgument(TypedParameterSymbol parameter, bool isReceiver)
    {
        if (isReceiver)
        {
            return false;
        }

        if (parameter.Type.Kind == StarkTypeKind.Closure)
        {
            return false;
        }

        return !parameter.IsConst
            && (parameter.Type.InitializationKind != StarkInitializationKind.None
                || parameter.Type.BorrowKind != StarkBorrowKind.None && parameter.Type.IsMutableView);
    }

    private static IReadOnlyDictionary<StarkParser.ObjectCreationExpressionContext, int> CollectTrackedObjectCreationOrdinals(
        ParserRuleContext body)
    {
        var ordinals = new Dictionary<StarkParser.ObjectCreationExpressionContext, int>();
        var nextOrdinal = 0;
        Collect(body);
        return ordinals;

        void Collect(Antlr4.Runtime.Tree.IParseTree current)
        {
            if (current is StarkParser.ObjectCreationExpressionContext objectCreation
                && ShouldTrackObjectCreation(objectCreation))
            {
                ordinals[objectCreation] = nextOrdinal++;
            }

            for (var index = 0; index < current.ChildCount; index++)
            {
                Collect(current.GetChild(index));
            }
        }
    }

    private static IReadOnlyDictionary<StarkParser.ArgumentListContext, int> CollectTemplateDirectCallOrdinals(
        ParserRuleContext body)
    {
        var ordinals = new Dictionary<StarkParser.ArgumentListContext, int>();
        var nextOrdinal = 0;
        Collect(body);
        return ordinals;

        void Collect(Antlr4.Runtime.Tree.IParseTree current)
        {
            if (current is StarkParser.PostfixExpressionContext postfixExpression
                && postfixExpression.postfixPart().Length > 0
                && postfixExpression.postfixPart()[0].argumentList() is { } argumentList)
            {
                ordinals[argumentList] = nextOrdinal++;
            }

            for (var index = 0; index < current.ChildCount; index++)
            {
                Collect(current.GetChild(index));
            }
        }
    }

    private static IReadOnlyDictionary<StarkParser.ArgumentListContext, int> CollectTemplateDirectCallOrdinals(
        ParserRuleContext body,
        IReadOnlyList<ImportedTemplateDirectCallSummary> directCalls)
    {
        var ordinals = new Dictionary<StarkParser.ArgumentListContext, int>();
        var orderedCalls = directCalls.OrderBy(static call => call.Ordinal).ToArray();
        var nextCallIndex = 0;
        Collect(body);
        return ordinals;

        void Collect(Antlr4.Runtime.Tree.IParseTree current)
        {
            if (current is StarkParser.PostfixExpressionContext postfixExpression
                && postfixExpression.postfixPart().Length > 0
                && postfixExpression.postfixPart()[0].argumentList() is { } argumentList
                && TryFindCompatibleImportedDirectCallOrdinal(
                    postfixExpression,
                    orderedCalls,
                    ref nextCallIndex,
                    out var ordinal))
            {
                ordinals[argumentList] = ordinal;
            }

            for (var index = 0; index < current.ChildCount; index++)
            {
                Collect(current.GetChild(index));
            }
        }
    }

    private static bool TryFindCompatibleImportedDirectCallOrdinal(
        StarkParser.PostfixExpressionContext expression,
        IReadOnlyList<ImportedTemplateDirectCallSummary> orderedCalls,
        ref int nextCallIndex,
        out int ordinal)
    {
        for (var index = nextCallIndex; index < orderedCalls.Count; index++)
        {
            var call = orderedCalls[index];
            if (!IsPublishedDirectCallCompatible(expression, call.Signature))
            {
                continue;
            }

            nextCallIndex = index + 1;
            ordinal = call.Ordinal;
            return true;
        }

        ordinal = -1;
        return false;
    }

    private static IReadOnlyDictionary<StarkParser.EnumConstructorExpressionContext, int> CollectTemplateEnumConstructorOrdinals(
        ParserRuleContext body)
    {
        var ordinals = new Dictionary<StarkParser.EnumConstructorExpressionContext, int>();
        var nextOrdinal = 0;
        Collect(body);
        return ordinals;

        void Collect(Antlr4.Runtime.Tree.IParseTree current)
        {
            if (current is StarkParser.EnumConstructorExpressionContext enumConstructor)
            {
                ordinals[enumConstructor] = nextOrdinal++;
            }

            for (var index = 0; index < current.ChildCount; index++)
            {
                Collect(current.GetChild(index));
            }
        }
    }

    private static IReadOnlyDictionary<StarkParser.PrimaryExpressionContext, int> CollectTemplateEnumValueOrdinals(
        ParserRuleContext body)
    {
        var ordinals = new Dictionary<StarkParser.PrimaryExpressionContext, int>();
        var nextOrdinal = 0;
        Collect(body);
        return ordinals;

        void Collect(Antlr4.Runtime.Tree.IParseTree current)
        {
            if (current is StarkParser.PrimaryExpressionContext primaryExpression
                && (primaryExpression.genericEnumCaseReference() is not null
                    || primaryExpression.qualifiedName() is not null))
            {
                ordinals[primaryExpression] = nextOrdinal++;
            }

            for (var index = 0; index < current.ChildCount; index++)
            {
                Collect(current.GetChild(index));
            }
        }
    }

    private static IReadOnlyDictionary<ParserRuleContext, int> CollectTemplateEnumPatternOrdinals(
        ParserRuleContext body)
    {
        var ordinals = new Dictionary<ParserRuleContext, int>();
        var nextOrdinal = 0;
        Collect(body);
        return ordinals;

        void Collect(Antlr4.Runtime.Tree.IParseTree current)
        {
            if (current is StarkParser.EnumNamedFieldPatternContext enumNamedFieldPattern)
            {
                ordinals[enumNamedFieldPattern] = nextOrdinal++;
            }
            else if (current is StarkParser.GenericEnumAggregatePatternContext genericEnumAggregatePattern)
            {
                ordinals[genericEnumAggregatePattern] = nextOrdinal++;
            }
            else if (current is StarkParser.AggregatePatternContext aggregatePattern)
            {
                ordinals[aggregatePattern] = nextOrdinal++;
            }

            for (var index = 0; index < current.ChildCount; index++)
            {
                Collect(current.GetChild(index));
            }
        }
    }

    private static IReadOnlyDictionary<StarkParser.UnaryExpressionContext, int> CollectTemplateConversionOrdinals(
        ParserRuleContext body)
    {
        var ordinals = new Dictionary<StarkParser.UnaryExpressionContext, int>();
        var nextOrdinal = 0;
        Collect(body);
        return ordinals;

        void Collect(Antlr4.Runtime.Tree.IParseTree current)
        {
            if (current is StarkParser.UnaryExpressionContext unaryExpression
                && unaryExpression.conversionType() is not null)
            {
                ordinals[unaryExpression] = nextOrdinal++;
            }

            for (var index = 0; index < current.ChildCount; index++)
            {
                Collect(current.GetChild(index));
            }
        }
    }

    private static IReadOnlyDictionary<StarkParser.PostfixPartContext, int> CollectTemplateFieldAccessOrdinals(
        ParserRuleContext body)
    {
        var ordinals = new Dictionary<StarkParser.PostfixPartContext, int>();
        var nextOrdinal = 0;
        Collect(body);
        return ordinals;

        void Collect(Antlr4.Runtime.Tree.IParseTree current)
        {
            if (current is StarkParser.PostfixExpressionContext postfixExpression)
            {
                foreach (var postfixPart in postfixExpression.postfixPart())
                {
                    if (postfixPart.Identifier() is not null)
                    {
                        ordinals[postfixPart] = nextOrdinal++;
                    }
                }
            }

            for (var index = 0; index < current.ChildCount; index++)
            {
                Collect(current.GetChild(index));
            }
        }
    }

    private static IReadOnlyDictionary<StarkParser.ArgumentListContext, int> CollectTemplateMemberCallOrdinals(
        ParserRuleContext body)
    {
        var ordinals = new Dictionary<StarkParser.ArgumentListContext, int>();
        var nextOrdinal = 0;
        Collect(body);
        return ordinals;

        void Collect(Antlr4.Runtime.Tree.IParseTree current)
        {
            if (current is StarkParser.PostfixExpressionContext postfixExpression)
            {
                var postfixParts = postfixExpression.postfixPart();
                for (var index = 0; index + 1 < postfixParts.Length; index++)
                {
                    if (postfixParts[index].Identifier() is not null
                        && postfixParts[index + 1].argumentList() is { } argumentList)
                    {
                        ordinals[argumentList] = nextOrdinal++;
                    }
                }
            }

            for (var index = 0; index < current.ChildCount; index++)
            {
                Collect(current.GetChild(index));
            }
        }
    }

    private ExpressionBinding? CheckVariableInitializer(StarkParser.VariableInitializerContext initializer, StarkTypeSymbol declaredType, Scope scope)
    {
        if (initializer.expression() is { } expression)
        {
            var value = EvaluateExpression(expression, scope, allowFunctionReference: false, expectedType: declaredType);
            EnsureAssignmentCompatible(variableName: null, declaredType, value.Type, expression, isConstant: false);
            return value;
        }

        if (initializer.objectInitializer() is { } objectInitializer)
        {
            CheckObjectInitializer(objectInitializer, declaredType, scope, preInitializedMembers: null);
            return null;
        }

        if (initializer.arrayInitializer() is { } arrayInitializer)
        {
            CheckArrayInitializer(arrayInitializer, declaredType, scope);
        }

        return null;
    }

    private static LocalMemoryProvenance? TryCreateLocalDeclarationMemoryProvenance(
        StarkTypeSymbol declaredType,
        ExpressionBinding? initializerBinding,
        Scope scope)
    {
        if (initializerBinding is null)
        {
            return null;
        }

        return TryCreateLocalMemoryProvenance(declaredType, initializerBinding, scope);
    }

    private static LocalMemoryProvenance? TryCreateLocalMemoryProvenance(
        StarkTypeSymbol declaredType,
        ExpressionBinding initializerBinding,
        Scope scope)
    {
        if (initializerBinding.MemoryRootKey is not { Length: > 0 } rootKey
            || !CanPreserveLocalMemoryProvenance(declaredType, initializerBinding.Type))
        {
            return null;
        }

        return new LocalMemoryProvenance(
            rootKey,
            initializerBinding.MemoryRootIsIndependentStorage,
            TryGetProvenancePreservingRawPointerCountExpression(rootKey, declaredType, initializerBinding.Type, scope));
    }

    private static bool CanPreserveLocalMemoryProvenance(
        StarkTypeSymbol declaredType,
        StarkTypeSymbol initializerType)
    {
        if (declaredType.Kind is StarkTypeKind.Slice or StarkTypeKind.Ascii or StarkTypeKind.Unicode)
        {
            return initializerType.Kind is StarkTypeKind.FixedArray
                or StarkTypeKind.Slice
                or StarkTypeKind.Ascii
                or StarkTypeKind.Unicode;
        }

        return declaredType.Kind == StarkTypeKind.RawPointer
            && initializerType.Kind == StarkTypeKind.RawPointer;
    }

    private static bool CanCarryLocalMemoryProvenance(StarkTypeSymbol type)
    {
        return type.Kind is StarkTypeKind.RawPointer
            or StarkTypeKind.Slice
            or StarkTypeKind.Ascii
            or StarkTypeKind.Unicode;
    }

    private static string? TryGetProvenancePreservingRawPointerCountExpression(
        string rootKey,
        StarkTypeSymbol declaredType,
        StarkTypeSymbol initializerType,
        Scope scope)
    {
        if (declaredType.Kind != StarkTypeKind.RawPointer
            || initializerType.Kind != StarkTypeKind.RawPointer
            || !TryParseMemoryRootPath(rootKey, out var path)
            || path.Segments.Count != 0
            || !scope.TryLookup(path.BaseName, out var rootSymbol)
            || rootSymbol.Type.Kind != StarkTypeKind.RawPointer)
        {
            return null;
        }

        return rootSymbol.RawPointerElementCountExpression;
    }

    private IReadOnlyList<ObjectInitializerMemberTypingRecord> CheckObjectInitializer(
        StarkParser.ObjectInitializerContext objectInitializer,
        StarkTypeSymbol targetType,
        Scope scope,
        ISet<string>? preInitializedMembers,
        IReadOnlyList<ImportedTemplateObjectInitializerMemberSummary>? publishedMembers = null,
        bool requireExplicitNonZeroInitializers = true)
    {
        if (targetType.Kind != StarkTypeKind.Named)
        {
            ReportError("STK3002", $"Object initializers require a named target type, but got '{targetType.DisplayName}'.", objectInitializer);
            return [];
        }

        _namedTypes.TryGetValue(targetType.NamedType!, out var namedType);
        var initializedMembers = preInitializedMembers is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : new HashSet<string>(preInitializedMembers, StringComparer.Ordinal);
        var recordedMembers = new List<ObjectInitializerMemberTypingRecord>(objectInitializer.memberInitializer().Length);

        for (var index = 0; index < objectInitializer.memberInitializer().Length; index++)
        {
            var initializer = objectInitializer.memberInitializer(index);
            var memberName = initializer.Identifier().GetText();
            var fieldType = StarkTypeSymbols.Error;
            var fieldIndex = -1;

            if (publishedMembers is { Count: > 0 } && index < publishedMembers.Count)
            {
                var publishedMember = publishedMembers[index];
                memberName = publishedMember.FieldName;
                fieldIndex = publishedMember.FieldIndex;
                fieldType = EnsureMonomorphizedType(publishedMember.FieldType, Location(initializer));
            }
            else
            {
                if (namedType is null)
                {
                    continue;
                }

                if (!namedType.TryGetField(memberName, out var field, out fieldIndex))
                {
                    ReportError("STK3005", $"Type '{namedType.Name}' does not contain a field named '{memberName}'.", initializer);
                    continue;
                }

                if (!IsFieldAccessible(field))
                {
                    ReportError(
                        "STK3015",
                        $"Field '{memberName}' is {RenderVisibility(field.Visibility)} and is not visible from module '{CurrentFunctionModuleName}'.",
                        initializer);
                    continue;
                }

                fieldType = field.Type;
            }

            if (!initializedMembers.Add(memberName))
            {
                var duplicateMessage = preInitializedMembers?.Contains(memberName) == true
                    ? $"Object initializer member '{memberName}' is already supplied by the constructor for '{namedType?.Name ?? targetType.DisplayName}'."
                    : $"Object initializer member '{memberName}' is assigned more than once.";
                ReportError("STK3006", duplicateMessage, initializer);
                continue;
            }

            recordedMembers.Add(new ObjectInitializerMemberTypingRecord(memberName, fieldIndex, fieldType));

            if (initializer.variableInitializer().expression() is { } expression)
            {
                var valueType = EvaluateExpression(expression, scope, allowFunctionReference: false, expectedType: fieldType).Type;
                EnsureObjectInitializerCompatible(memberName, fieldType, valueType, expression);
                continue;
            }

            CheckVariableInitializer(initializer.variableInitializer(), fieldType, scope);
        }

        if (requireExplicitNonZeroInitializers)
        {
            ValidateExplicitNonZeroInitializers(
                targetType,
                initializedMembers,
                objectInitializer,
                "object initializer");
        }

        return recordedMembers;
    }

    private void CheckArrayInitializer(StarkParser.ArrayInitializerContext arrayInitializer, StarkTypeSymbol targetType, Scope scope)
    {
        var elementType = targetType.ElementType;
        if (targetType.Kind != StarkTypeKind.FixedArray || elementType is null)
        {
            var message = targetType.Kind == StarkTypeKind.Slice
                ? $"Array initializers require a fixed-size array target, but got '{targetType.DisplayName}'. Form a slice explicitly from backing storage instead."
                : $"Array initializers require a fixed-size array target, but got '{targetType.DisplayName}'.";
            ReportError("STK3002", message, arrayInitializer);
            return;
        }

        foreach (var initializer in arrayInitializer.variableInitializer())
        {
            if (initializer.expression() is { } expression)
            {
                var valueType = EvaluateExpression(expression, scope, allowFunctionReference: false, expectedType: elementType).Type;
                EnsureArrayElementCompatible(elementType, valueType, expression);
                continue;
            }

            CheckVariableInitializer(initializer, elementType, scope);
        }

        if (targetType.Kind == StarkTypeKind.FixedArray
            && targetType.FixedLength is int fixedLength
            && arrayInitializer.variableInitializer().Length > fixedLength)
        {
            ReportError(
                "STK3006",
                $"Array initializer provides {arrayInitializer.variableInitializer().Length} elements, but '{targetType.DisplayName}' expects at most {fixedLength}.",
                arrayInitializer);
        }

        if (targetType.Kind == StarkTypeKind.FixedArray
            && targetType.FixedLength is int requiredLength
            && TypeRequiresExplicitNonZeroInitializer(elementType)
            && arrayInitializer.variableInitializer().Length != requiredLength)
        {
            ReportError(
                "STK3009",
                $"Array initializer for '{targetType.DisplayName}' must provide all {requiredLength} element{Pluralize(requiredLength)} because omitted function-pointer elements would otherwise be initialized to null.",
                arrayInitializer);
        }
    }

    private void ValidateExplicitNonZeroInitializers(
        StarkTypeSymbol targetType,
        ISet<string>? initializedMembers,
        ParserRuleContext diagnosticContext,
        string initializerKind)
    {
        if (targetType.Kind != StarkTypeKind.Named
            || targetType.NamedType is null
            || !_namedTypes.TryGetValue(targetType.NamedType, out var namedType))
        {
            return;
        }

        foreach (var field in namedType.OrderedFields)
        {
            if (initializedMembers?.Contains(field.Name) == true
                || !TypeRequiresExplicitNonZeroInitializer(field.Type))
            {
                continue;
            }

            ReportError(
                "STK3009",
                $"Field '{field.Name}' of '{namedType.Name}' must be explicitly initialized in this {initializerKind} because '{field.Type.DisplayName}' contains a function pointer and function pointers cannot be null.",
                diagnosticContext);
        }
    }

    private bool TypeRequiresExplicitNonZeroInitializer(StarkTypeSymbol type)
        => TypeRequiresExplicitNonZeroInitializer(type, new HashSet<string>(StringComparer.Ordinal));

    private bool TypeRequiresExplicitNonZeroInitializer(
        StarkTypeSymbol type,
        ISet<string> activeNamedTypes)
    {
        var normalized = StarkTypeSymbols.WithQualifiers(
            type,
            borrowKind: StarkBorrowKind.None,
            accessKind: StarkAccessKind.None,
            initializationKind: StarkInitializationKind.None,
            isMutableView: false);

        if (normalized.Kind == StarkTypeKind.FunctionPointer)
        {
            return true;
        }

        if (normalized.Kind == StarkTypeKind.FixedArray
            && normalized.ElementType is not null)
        {
            return TypeRequiresExplicitNonZeroInitializer(normalized.ElementType, activeNamedTypes);
        }

        if (normalized.Kind != StarkTypeKind.Named
            || normalized.NamedType is null
            || !_namedTypes.TryGetValue(normalized.NamedType, out var namedType))
        {
            return false;
        }

        if (!activeNamedTypes.Add(namedType.Name))
        {
            return false;
        }

        try
        {
            return namedType.OrderedFields.Any(field => TypeRequiresExplicitNonZeroInitializer(field.Type, activeNamedTypes));
        }
        finally
        {
            activeNamedTypes.Remove(namedType.Name);
        }
    }

    private ExpressionBinding EvaluateExpression(
        StarkParser.ExpressionContext expression,
        Scope scope,
        bool allowFunctionReference,
        StarkTypeSymbol? expectedType = null)
    {
        return EvaluateAssignmentExpression(expression.assignmentExpression(), scope, allowFunctionReference, expectedType);
    }

    private ExpressionBinding EvaluateAssignmentExpression(
        StarkParser.AssignmentExpressionContext expression,
        Scope scope,
        bool allowFunctionReference,
        StarkTypeSymbol? expectedType = null)
    {
        if (expression.INIT() is not null
            && expression.ASSIGN() is not null
            && expression.assignmentOperator() is null)
        {
            var initTarget = MakeInitDestinationBinding(
                EvaluateUnaryExpression(expression.unaryExpression(), scope, allowFunctionReference: true),
                expression.unaryExpression());
            var storageType = StarkTypeSymbols.WithQualifiers(initTarget.Type, initializationKind: StarkInitializationKind.None);
            var initValue = EvaluateAssignmentExpression(
                expression.assignmentExpression(),
                scope,
                allowFunctionReference: false,
                storageType);

            if (!initTarget.IsAssignable)
            {
                ReportError(
                    "STK3007",
                    initTarget.AssignmentErrorMessage ?? "The left side of 'init =' must be an initialization destination.",
                    expression.unaryExpression());
                return new ExpressionBinding(StarkTypeSymbols.Error);
            }

            EnsureAssignmentTargetCompatible(initTarget, initValue.Type, expression.assignmentExpression());
            return initTarget;
        }

        if (expression.conditionalExpression() is { } conditionalExpression)
        {
            return EvaluateConditionalExpression(conditionalExpression, scope, allowFunctionReference, expectedType);
        }

        var left = EvaluateUnaryExpression(expression.unaryExpression(), scope, allowFunctionReference: true);
        var assignmentOperator = expression.assignmentOperator().GetText();
        var rightExpectedType = assignmentOperator == "=" ? left.Type : null;
        var right = EvaluateAssignmentExpression(
            expression.assignmentExpression(),
            scope,
            allowFunctionReference: false,
            rightExpectedType);

        if (!left.IsAssignable)
        {
            ReportError(
                "STK3007",
                left.AssignmentErrorMessage ?? $"The left side of '{assignmentOperator}' must be assignable.",
                expression.unaryExpression());
            return new ExpressionBinding(StarkTypeSymbols.Error);
        }

        if (assignmentOperator == "=")
        {
            EnsureAssignmentTargetCompatible(left, right.Type, expression.assignmentExpression());
            UpdateAssignedLocalMemoryProvenance(expression.unaryExpression(), left, right, scope);
            return left;
        }

        if (IsExplicitArithmeticAssignmentOperator(assignmentOperator))
        {
            if (left.Type.Kind != StarkTypeKind.Integer || right.Type.Kind != StarkTypeKind.Integer)
            {
                ReportError("STK3002", $"Operator '{assignmentOperator}' requires integer operands.", expression);
                return new ExpressionBinding(StarkTypeSymbols.Error);
            }
        }
        else if (IsBitwiseAssignmentOperator(assignmentOperator))
        {
            if (left.Type.Kind != StarkTypeKind.Integer || right.Type.Kind != StarkTypeKind.Integer)
            {
                ReportError("STK3002", $"Operator '{assignmentOperator}' requires integer operands.", expression);
                return new ExpressionBinding(StarkTypeSymbols.Error);
            }
        }
        else if (!IsNumeric(left.Type) || !IsNumeric(right.Type))
        {
            ReportError("STK3002", $"Operator '{assignmentOperator}' requires numeric operands.", expression);
            return new ExpressionBinding(StarkTypeSymbols.Error);
        }

        if (!CanAssign(left.Type, right.Type))
        {
            ReportError(
                "STK3002",
                $"Operator '{assignmentOperator}' cannot assign '{right.Type.DisplayName}' to '{left.Type.DisplayName}'.{GetExplicitConversionHint(left.Type, right.Type)}",
                expression.assignmentExpression());
        }

        return left;
    }

    private static void UpdateAssignedLocalMemoryProvenance(
        StarkParser.UnaryExpressionContext targetExpression,
        ExpressionBinding target,
        ExpressionBinding value,
        Scope scope)
    {
        if (!TryGetDirectAssignmentTargetName(targetExpression, out var targetName)
            || !CanCarryLocalMemoryProvenance(target.Type))
        {
            return;
        }

        if (TryCreateLocalMemoryProvenance(target.Type, value, scope) is { } provenance)
        {
            scope.SetCurrentFlowMemoryProvenance(
                targetName,
                provenance.RootKey,
                provenance.IsIndependentStorage,
                provenance.RawPointerElementCountExpression);
            return;
        }

        scope.ClearCurrentFlowMemoryProvenance(targetName);
    }

    private ExpressionBinding EvaluateConditionalExpression(
        StarkParser.ConditionalExpressionContext expression,
        Scope scope,
        bool allowFunctionReference,
        StarkTypeSymbol? expectedType)
    {
        if (expression.expression().Length == 0)
        {
            return EvaluateLogicalOrExpression(expression.logicalOrExpression(), scope, allowFunctionReference, expectedType);
        }

        var condition = EvaluateLogicalOrExpression(expression.logicalOrExpression(), scope, allowFunctionReference, expectedType: null);
        EnsureBoolean(condition.Type, expression.logicalOrExpression(), "Conditional expressions require a boolean condition");

        var whenTrue = EvaluateExpression(expression.expression(0), scope, allowFunctionReference: false, expectedType);
        var whenFalse = EvaluateExpression(expression.expression(1), scope, allowFunctionReference: false, expectedType);
        var resultType = FindCommonType(whenTrue.Type, whenFalse.Type);
        if (resultType.Kind == StarkTypeKind.Error)
        {
            ReportError(
                "STK3002",
                $"Conditional branches '{whenTrue.Type.DisplayName}' and '{whenFalse.Type.DisplayName}' are incompatible.",
                expression);
        }

        return new ExpressionBinding(
            resultType,
            HasConstProvenance: HasConstProvenance(whenTrue) && HasConstProvenance(whenFalse));
    }

    private ExpressionBinding EvaluateLogicalOrExpression(
        StarkParser.LogicalOrExpressionContext expression,
        Scope scope,
        bool allowFunctionReference,
        StarkTypeSymbol? expectedType)
    {
        var expressions = expression.logicalAndExpression();
        if (expressions.Length == 1)
        {
            return EvaluateLogicalAndExpression(expressions[0], scope, allowFunctionReference, expectedType);
        }

        var operands = expressions.Select(item => EvaluateLogicalAndExpression(item, scope, allowFunctionReference, expectedType: null)).ToArray();

        foreach (var operand in operands)
        {
            EnsureBoolean(operand.Type, expression, "Logical '||' requires boolean operands");
        }

        return new ExpressionBinding(StarkTypeSymbols.Bool);
    }

    private ExpressionBinding EvaluateLogicalAndExpression(
        StarkParser.LogicalAndExpressionContext expression,
        Scope scope,
        bool allowFunctionReference,
        StarkTypeSymbol? expectedType)
    {
        var expressions = expression.bitwiseOrExpression();
        if (expressions.Length == 1)
        {
            return EvaluateBitwiseOrExpression(expressions[0], scope, allowFunctionReference, expectedType);
        }

        var operands = expressions.Select(item => EvaluateBitwiseOrExpression(item, scope, allowFunctionReference, expectedType: null)).ToArray();

        foreach (var operand in operands)
        {
            EnsureBoolean(operand.Type, expression, "Logical '&&' requires boolean operands");
        }

        return new ExpressionBinding(StarkTypeSymbols.Bool);
    }

    private ExpressionBinding EvaluateBitwiseOrExpression(
        StarkParser.BitwiseOrExpressionContext expression,
        Scope scope,
        bool allowFunctionReference,
        StarkTypeSymbol? expectedType)
    {
        var expressions = expression.bitwiseXorExpression();
        if (expressions.Length == 1)
        {
            return EvaluateBitwiseXorExpression(expressions[0], scope, allowFunctionReference, expectedType);
        }

        var operands = expressions.Select(item => EvaluateBitwiseXorExpression(item, scope, allowFunctionReference, expectedType: null)).ToArray();
        return EvaluateBinaryChain(operands, ExtractOperators<StarkParser.BitwiseXorExpressionContext>(expression), expression, "Bitwise '|'", requireInteger: true);
    }

    private ExpressionBinding EvaluateBitwiseXorExpression(
        StarkParser.BitwiseXorExpressionContext expression,
        Scope scope,
        bool allowFunctionReference,
        StarkTypeSymbol? expectedType)
    {
        var expressions = expression.bitwiseAndExpression();
        if (expressions.Length == 1)
        {
            return EvaluateBitwiseAndExpression(expressions[0], scope, allowFunctionReference, expectedType);
        }

        var operands = expressions.Select(item => EvaluateBitwiseAndExpression(item, scope, allowFunctionReference, expectedType: null)).ToArray();
        return EvaluateBinaryChain(operands, ExtractOperators<StarkParser.BitwiseAndExpressionContext>(expression), expression, "Bitwise '^'", requireInteger: true);
    }

    private ExpressionBinding EvaluateBitwiseAndExpression(
        StarkParser.BitwiseAndExpressionContext expression,
        Scope scope,
        bool allowFunctionReference,
        StarkTypeSymbol? expectedType)
    {
        var expressions = expression.equalityExpression();
        if (expressions.Length == 1)
        {
            return EvaluateEqualityExpression(expressions[0], scope, allowFunctionReference, expectedType);
        }

        var operands = expressions.Select(item => EvaluateEqualityExpression(item, scope, allowFunctionReference, expectedType: null)).ToArray();
        return EvaluateBinaryChain(operands, ExtractOperators<StarkParser.EqualityExpressionContext>(expression), expression, "Bitwise '&'", requireInteger: true);
    }

    private ExpressionBinding EvaluateEqualityExpression(
        StarkParser.EqualityExpressionContext expression,
        Scope scope,
        bool allowFunctionReference,
        StarkTypeSymbol? expectedType)
    {
        var expressions = expression.relationalExpression();
        var operators = ExtractOperators<StarkParser.RelationalExpressionContext>(expression);
        if (operators.Count == 0)
        {
            return EvaluateRelationalExpression(expressions[0], scope, allowFunctionReference, expectedType);
        }

        var operands = expressions.Select(item => EvaluateRelationalExpression(item, scope, allowFunctionReference, expectedType: null)).ToArray();

        for (var index = 1; index < operands.Length; index++)
        {
            if (!AreComparable(operands[index - 1].Type, operands[index].Type))
            {
                ReportError(
                    "STK3002",
                    $"Operator '{operators[index - 1]}' cannot compare '{operands[index - 1].Type.DisplayName}' and '{operands[index].Type.DisplayName}'.",
                    expression);
            }
        }

        return new ExpressionBinding(StarkTypeSymbols.Bool);
    }

    private ExpressionBinding EvaluateRelationalExpression(
        StarkParser.RelationalExpressionContext expression,
        Scope scope,
        bool allowFunctionReference,
        StarkTypeSymbol? expectedType)
    {
        var expressions = expression.shiftExpression();
        var operators = ExtractOperators<StarkParser.ShiftExpressionContext>(expression);
        if (operators.Count == 0)
        {
            return EvaluateShiftExpression(expressions[0], scope, allowFunctionReference, expectedType);
        }

        var operands = expressions.Select(item => EvaluateShiftExpression(item, scope, allowFunctionReference, expectedType: null)).ToArray();

        for (var index = 1; index < operands.Length; index++)
        {
            if (!IsOrderedComparable(operands[index - 1].Type, operands[index].Type))
            {
                ReportError(
                    "STK3002",
                    $"Operator '{operators[index - 1]}' requires ordered-comparable operands.",
                    expression);
            }
        }

        return new ExpressionBinding(StarkTypeSymbols.Bool);
    }

    private ExpressionBinding EvaluateShiftExpression(
        StarkParser.ShiftExpressionContext expression,
        Scope scope,
        bool allowFunctionReference,
        StarkTypeSymbol? expectedType)
    {
        var expressions = expression.additiveExpression();
        var operators = ExtractOperators<StarkParser.AdditiveExpressionContext>(expression);
        if (operators.Count == 0)
        {
            return EvaluateAdditiveExpression(expressions[0], scope, allowFunctionReference, expectedType);
        }

        var operands = expressions.Select(item => EvaluateAdditiveExpression(item, scope, allowFunctionReference, expectedType: null)).ToArray();

        if (TryEvaluateCompileTimeIntegerExpression(expression, scope, expectedType, out var constantBinding))
        {
            return constantBinding;
        }

        var resultType = operands[0].Type;
        for (var index = 1; index < operands.Length; index++)
        {
            if (resultType.Kind != StarkTypeKind.Integer || operands[index].Type.Kind != StarkTypeKind.Integer)
            {
                ReportError("STK3002", $"Operator '{operators[index - 1]}' requires integer operands.", expression);
                return new ExpressionBinding(StarkTypeSymbols.Error);
            }
        }

        return new ExpressionBinding(resultType);
    }

    private ExpressionBinding EvaluateAdditiveExpression(
        StarkParser.AdditiveExpressionContext expression,
        Scope scope,
        bool allowFunctionReference,
        StarkTypeSymbol? expectedType)
    {
        var expressions = expression.multiplicativeExpression();
        var operators = ExtractOperators<StarkParser.MultiplicativeExpressionContext>(expression);
        if (operators.Count == 0)
        {
            return EvaluateMultiplicativeExpression(expressions[0], scope, allowFunctionReference, expectedType);
        }

        var operands = expressions.Select(item => EvaluateMultiplicativeExpression(item, scope, allowFunctionReference, expectedType: null)).ToArray();
        if (operands.Any(static operand => IsTextLikeForConcatenation(operand.Type)))
        {
            return EvaluateTextConcatenationChain(operands, operators, expression, expectedType);
        }

        if (TryEvaluateCompileTimeIntegerExpression(expression, scope, expectedType, out var constantBinding))
        {
            return constantBinding;
        }

        return EvaluateArithmeticChain(operands, operators, expression, "Additive operator");
    }

    private ExpressionBinding EvaluateMultiplicativeExpression(
        StarkParser.MultiplicativeExpressionContext expression,
        Scope scope,
        bool allowFunctionReference,
        StarkTypeSymbol? expectedType)
    {
        var expressions = expression.unaryExpression();
        var operators = ExtractOperators<StarkParser.UnaryExpressionContext>(expression);
        if (operators.Count == 0)
        {
            return EvaluateUnaryExpression(expressions[0], scope, allowFunctionReference, expectedType);
        }

        var operands = expressions.Select(item => EvaluateUnaryExpression(item, scope, allowFunctionReference, expectedType: null)).ToArray();
        if (TryEvaluateCompileTimeIntegerExpression(expression, scope, expectedType, out var constantBinding))
        {
            return constantBinding;
        }

        return EvaluateArithmeticChain(operands, operators, expression, "Multiplicative operator");
    }

    private ExpressionBinding EvaluateUnaryExpression(
        StarkParser.UnaryExpressionContext expression,
        Scope scope,
        bool allowFunctionReference,
        StarkTypeSymbol? expectedType = null)
    {
        if (expression.powerExpression() is { } powerExpression)
        {
            return EvaluatePowerExpression(powerExpression, scope, allowFunctionReference, expectedType);
        }

        if (expression.conversionType() is { } conversionType)
        {
            var convertedOperand = EvaluateUnaryExpression(expression.unaryExpression(), scope, allowFunctionReference: false);
            var targetType = TryGetPublishedTemplateConversionType(expression, out var publishedTargetType)
                ? EnsureMonomorphizedType(publishedTargetType, Location(conversionType))
                : _typeResolver!.ResolveConversionType(
                    conversionType,
                    _currentFunctionGenericParameters,
                    _currentFunctionModuleName);
            if (IsRawPointerConversion(targetType, convertedOperand.Type))
            {
                RequireUnsafeContext(
                    $"Raw pointer conversion from '{convertedOperand.Type.DisplayName}' to '{targetType.DisplayName}'",
                    expression);
            }

            if (StarkTypeSymbols.IsCompileTimeInteger(convertedOperand.Type)
                && CompileTimeExpressionEvaluator.TryEvaluate(
                    expression.unaryExpression(),
                    out var compileTimeOperand,
                    CreateCompileTimeEvaluationServices(scope))
                && compileTimeOperand.Kind == CompileTimeConstantKind.Integer)
            {
                if (targetType.Kind == StarkTypeKind.Integer)
                {
                    if (!CompileTimeExpressionEvaluator.TryCoerce(compileTimeOperand, targetType, out _))
                    {
                        ReportError(
                            "STK3002",
                            $"Compile-time integer value {compileTimeOperand.IntegerValue} does not fit in '{targetType.DisplayName}'. Use a wider integer type or keep the expression compile-time-only.",
                            expression);
                    }
                }
                else
                {
                    ReportError(
                        "STK3002",
                        $"Compile-time integer value {compileTimeOperand.IntegerValue} must be converted to a concrete integer type before converting to '{targetType.DisplayName}'.",
                        expression);
                }
            }

            EnsureExplicitConversionCompatible(targetType, convertedOperand, expression);
            RecordConversion(targetType, expression);
            if (convertedOperand.TextLiteral is not null
                && convertedOperand.TextLiteralKind is not null
                && CanExplicitlyConvertTextLiteral(targetType, convertedOperand))
            {
                return new ExpressionBinding(
                    targetType,
                    NamedType: ResolveNamedTypeSymbol(targetType),
                    TextLiteral: convertedOperand.TextLiteral,
                    TextLiteralKind: convertedOperand.TextLiteralKind,
                    HasConstProvenance: HasConstProvenance(convertedOperand),
                    MemoryRootKey: convertedOperand.MemoryRootKey,
                    MemoryRootIsIndependentStorage: convertedOperand.MemoryRootIsIndependentStorage);
            }

            if (targetType.Kind == StarkTypeKind.RawPointer
                && convertedOperand.Type.Kind == StarkTypeKind.RawPointer)
            {
                return new ExpressionBinding(
                    targetType,
                    NamedType: ResolveNamedTypeSymbol(targetType),
                    UsesFrozenProjectionSemantics: UsesFrozenProjectionSemantics(convertedOperand)
                        || targetType.ElementType is { AccessKind: StarkAccessKind.Frozen },
                    HasConstProvenance: HasConstProvenance(convertedOperand),
                    MemoryRootKey: convertedOperand.MemoryRootKey,
                    MemoryRootIsIndependentStorage: convertedOperand.MemoryRootIsIndependentStorage);
            }

            return new ExpressionBinding(targetType, NamedType: ResolveNamedTypeSymbol(targetType));
        }

        if (expression.INIT() is not null && expression.unaryOperator() is null)
        {
            var initOperand = EvaluateUnaryExpression(expression.unaryExpression(), scope, allowFunctionReference: false, expectedType);
            return MakeInitDestinationBinding(initOperand, expression);
        }

        if (expression.TRY() is not null)
        {
            return EvaluateTryExpression(expression, scope, expectedType);
        }

        if (expression.COMPTIME() is not null)
        {
            return EvaluateComptimeExpression(expression, scope, expectedType);
        }

        var operand = EvaluateUnaryExpression(expression.unaryExpression(), scope, allowFunctionReference: false);
        var op = expression.unaryOperator()?.GetText() ?? expression.GetChild(0).GetText();

        return op switch
        {
            "!" => EnsureBooleanUnary(operand, expression),
            "~" => EnsureIntegerUnary(operand, expression, op),
            "-%" => EnsureIntegerUnary(operand, expression, op),
            "+" or "-" => EnsureNumericUnary(operand, expression, op),
            "&" => EnsureAddressOfUnary(operand, expression),
            "*" => EnsureDereferenceUnary(operand, expression),
            _ => new ExpressionBinding(StarkTypeSymbols.Error)
        };
    }

    private ExpressionBinding EvaluateComptimeExpression(
        StarkParser.UnaryExpressionContext expression,
        Scope scope,
        StarkTypeSymbol? expectedType)
    {
        var previousAllowCompileTimeOnlyStructuralFactCalls = _allowCompileTimeOnlyStructuralFactCalls;
        _allowCompileTimeOnlyStructuralFactCalls = true;
        ExpressionBinding operandBinding;
        try
        {
            operandBinding = EvaluateUnaryExpression(
                expression.unaryExpression(),
                scope,
                allowFunctionReference: false,
                expectedType);
        }
        finally
        {
            _allowCompileTimeOnlyStructuralFactCalls = previousAllowCompileTimeOnlyStructuralFactCalls;
        }

        if (operandBinding.Type.Kind == StarkTypeKind.Error)
        {
            return new ExpressionBinding(StarkTypeSymbols.Error);
        }

        CompileTimeEvaluator.ClearFailure();
        if (!CompileTimeEvaluator.TryEvaluateExpressionNode(
                expression.unaryExpression(),
                CurrentFunctionModuleName,
                state: CreateOpenComptimeGenericEvaluationState(),
                activeCalls: null,
                out var constant,
                (string name, out CompileTimeConstant value) =>
                    TryResolveCompileTimeConstant(scope, name, out value)
                    || TryResolveOpenComptimeGenericConstant(name, out value)))
        {
            ReportError(
                "STK3053",
                BuildComptimeEvaluationFailureMessage(
                    "`comptime` expression must evaluate during compilation. Use literals, const values, comptime generic values, or finite/law calls whose bodies stay within the supported compile-time subset."),
                expression);
            return new ExpressionBinding(StarkTypeSymbols.Error);
        }

        if (expectedType is not null
            && CompileTimeExpressionEvaluator.TryCoerce(constant, expectedType, out var coerced))
        {
            constant = coerced;
        }

        return new ExpressionBinding(
            constant.Type,
            NamedType: ResolveNamedTypeSymbol(constant.Type),
            HasConstProvenance: true);
    }

    private ExpressionBinding EvaluateTryExpression(
        StarkParser.UnaryExpressionContext expression,
        Scope scope,
        StarkTypeSymbol? expectedType)
    {
        // Position rule (doc 11 §4.6): `try` may only appear where its early return sits
        // at a statement boundary — the whole initializer of a binding, the whole right
        // side of an assignment, the operand of `return`, or a bare expression statement.
        // That keeps the divert greppable and makes the drop set on the error path
        // identical to a normal `return`.
        if (!IsPropagationBoundaryPosition(expression))
        {
            ReportError(
                "STK3037",
                "`try` may only appear as the whole initializer of a binding, the whole right side of an "
                    + "assignment, the operand of `return`, or a bare expression statement. Bind the fallible "
                    + "call to a local first, then `try` that local.",
                expression);
        }

        var operandBinding = EvaluateUnaryExpression(expression.unaryExpression(), scope, allowFunctionReference: false);
        var operandType = operandBinding.Type;
        if (operandType.Kind == StarkTypeKind.Error)
        {
            return new ExpressionBinding(StarkTypeSymbols.Error);
        }

        // Imported (package-image) signatures hand back generic instantiations that this
        // compilation may not have monomorphized yet — e.g. `try Pkg.Fetch(x)` where Fetch
        // returns Pkg.FetchOutcome<i32> and no consumer type annotation names that
        // instantiation. Ensure the concrete enum exists (threading the imported template's
        // [Ok]/[Err] roles and `from` funnels) and is registered for enum-layout planning.
        operandType = EnsureMonomorphizedType(operandType, Location(expression));

        // The operand must be a propagatable enum: exactly two variants carrying the
        // [Ok]/[Err] roles (doc 11 v2). Recognition is by role — never by type name and
        // never by stdlib identity.
        if (!TryResolvePropagationRoles(operandType, out var operandRoles))
        {
            ReportError(
                "STK3039",
                $"`try` requires a propagatable operand: an enum with one `[Ok]` and one `[Err]` variant. "
                    + $"'{operandType.DisplayName}' has no propagation roles.",
                expression);
            return new ExpressionBinding(StarkTypeSymbols.Error);
        }

        var successBinding = operandRoles.SuccessPayloadType is { } successType
            ? new ExpressionBinding(successType, NamedType: ResolveNamedTypeSymbol(successType))
            : new ExpressionBinding(StarkTypeSymbols.Void);

        // The enclosing function must also return a propagatable enum so the early
        // return can construct its [Err] variant.
        var returnType = _currentFunctionReturnType is { } declaredReturnType
            ? EnsureMonomorphizedType(declaredReturnType, Location(expression))
            : null;
        if (returnType is null || !TryResolvePropagationRoles(returnType, out var enclosingRoles))
        {
            ReportError(
                "STK3038",
                $"`try` requires the enclosing function to return a propagatable enum (one `[Ok]` and one "
                    + $"`[Err]` variant), but it returns '{(returnType?.DisplayName ?? "void")}'. Change the "
                    + "return type or handle the failure with `switch`.",
                expression);
            return successBinding;
        }

        // The failure payloads must be connected: identical types, a `from` funnel on
        // the enclosing failure payload's enum, or both unit (no payload). Mixing a
        // payload failure with a unit failure is rejected — an error value is never
        // silently discarded and never invented.
        string? funnelVariant = null;
        if (operandRoles.FailurePayloadType is { } operandFailure
            && enclosingRoles.FailurePayloadType is { } enclosingFailure)
        {
            // Failure payload enums may themselves be imported and/or generic; the funnel
            // lookup needs their concrete definitions (with `from` markers) in the type table.
            funnelVariant = ResolveErrorFunnelVariant(
                EnsureMonomorphizedType(operandFailure, Location(expression)),
                EnsureMonomorphizedType(enclosingFailure, Location(expression)),
                expression);
        }
        else if (operandRoles.FailurePayloadType is not null || enclosingRoles.FailurePayloadType is not null)
        {
            var operandFailureText = operandRoles.FailurePayloadType is { } operandPayload
                ? $"fails with '{operandPayload.DisplayName}'"
                : "fails without a payload";
            var enclosingFailureText = enclosingRoles.FailurePayloadType is { } enclosingPayload
                ? $"fails with '{enclosingPayload.DisplayName}'"
                : "fails without a payload";
            ReportError(
                "STK3038",
                $"`try` cannot propagate here: the operand '{operandType.DisplayName}' {operandFailureText}, "
                    + $"but the enclosing '{returnType.DisplayName}' {enclosingFailureText}. An error value is "
                    + "never silently discarded or invented; convert explicitly or handle with `switch`.",
                expression);
            return successBinding;
        }

        RecordTryPropagation(new TryPropagationTypingRecord(
            Location(expression),
            operandType,
            operandRoles.OkVariantName,
            operandRoles.ErrVariantName,
            operandRoles.SuccessPayloadType,
            operandRoles.FailurePayloadType,
            returnType,
            enclosingRoles.ErrVariantName,
            enclosingRoles.FailurePayloadType,
            funnelVariant,
            _currentFunctionName));

        return successBinding;
    }

    private void RecordTryPropagation(TryPropagationTypingRecord record) => _tryPropagations.Add(record);

    /// <summary>
    /// Resolves how the operand's error type funnels into the enclosing error type for a
    /// cross-layer `try`. Returns <c>null</c> when no conversion is needed (the error types
    /// match); otherwise the name of the `from` funnel variant on the enclosing error enum.
    /// Reports STK3041 when the types differ and no funnel exists.
    /// </summary>
    private string? ResolveErrorFunnelVariant(
        StarkTypeSymbol operandErrorType,
        StarkTypeSymbol enclosingErrorType,
        StarkParser.UnaryExpressionContext expression)
    {
        if (SameErrorType(operandErrorType, enclosingErrorType))
        {
            return null;
        }

        if (ResolveNamedTypeSymbol(enclosingErrorType) is { } enclosingNamed)
        {
            foreach (var variant in enclosingNamed.Variants)
            {
                if (variant.AbsorbsErrorType is { } absorbed && SameErrorType(absorbed, operandErrorType))
                {
                    return variant.Name;
                }
            }
        }

        ReportError(
            "STK3041",
            $"`try` cannot convert error '{operandErrorType.DisplayName}' into '{enclosingErrorType.DisplayName}': "
                + $"'{enclosingErrorType.DisplayName}' declares no `from {operandErrorType.DisplayName}` funnel variant. "
                + $"Add one (e.g. `SomeVariant from {operandErrorType.DisplayName}`), convert explicitly, or handle the error with `switch`.",
            expression);
        return null;
    }

    private static bool SameErrorType(StarkTypeSymbol a, StarkTypeSymbol b)
        => string.Equals(a.NamedType ?? a.DisplayName, b.NamedType ?? b.DisplayName, StringComparison.Ordinal);

    /// <summary>
    /// The resolved [Ok]/[Err] propagation roles of an enum type (doc 11 v2): the role
    /// variant names plus the success/failure payload types (substituted for generic
    /// instantiations; <c>null</c> when the role variant carries no payload).
    /// </summary>
    private readonly record struct PropagationRoles(
        string OkVariantName,
        string ErrVariantName,
        StarkTypeSymbol? SuccessPayloadType,
        StarkTypeSymbol? FailurePayloadType);

    /// <summary>
    /// Resolves a type's [Ok]/[Err] propagation roles. Returns false when the type is
    /// not an enum, has no role-marked variants, or is malformed (which
    /// <c>ValidateEnumPropagationRoles</c> already diagnosed at its declaration).
    /// Recognition is purely role-based: any two-variant enum with one [Ok] and one
    /// [Err] qualifies, regardless of its name or where it is declared.
    /// </summary>
    private bool TryResolvePropagationRoles(StarkTypeSymbol type, out PropagationRoles roles)
    {
        roles = default;
        if (type.Kind != StarkTypeKind.Named)
        {
            return false;
        }

        var namedType = ResolveNamedTypeSymbol(type);
        if (namedType is null || namedType.Variants.Count != 2)
        {
            return false;
        }

        EnumVariantSymbol? okVariant = null;
        EnumVariantSymbol? errVariant = null;
        foreach (var variant in namedType.Variants)
        {
            if (variant.Role == EnumVariantRole.Ok)
            {
                okVariant = variant;
            }
            else if (variant.Role == EnumVariantRole.Err)
            {
                errVariant = variant;
            }
        }

        if (okVariant is null
            || errVariant is null
            || okVariant.Fields.Count > 1
            || errVariant.Fields.Count > 1)
        {
            return false;
        }

        var successPayload = okVariant.Fields.Count == 1
            ? SubstitutePropagationPayloadType(okVariant.Fields[0].Type, namedType, type)
            : null;
        var failurePayload = errVariant.Fields.Count == 1
            ? SubstitutePropagationPayloadType(errVariant.Fields[0].Type, namedType, type)
            : null;

        roles = new PropagationRoles(okVariant.Name, errVariant.Name, successPayload, failurePayload);
        return true;
    }

    /// <summary>
    /// Substitutes generic parameters in a role variant's payload type using the
    /// instantiation's type arguments. Concrete enums produced by
    /// <c>CreateConcreteEnum</c> already carry substituted field types, making this a
    /// no-op; the substitution covers paths that resolve to the generic template.
    /// </summary>
    private StarkTypeSymbol SubstitutePropagationPayloadType(
        StarkTypeSymbol payloadType,
        NamedTypeSymbol enumDefinition,
        StarkTypeSymbol instantiatedType)
    {
        if (!enumDefinition.IsGeneric || instantiatedType.TypeArguments is not { Count: > 0 } typeArguments)
        {
            return payloadType;
        }

        var substitution = new Dictionary<string, StarkTypeSymbol>(StringComparer.Ordinal);
        var genericParameters = enumDefinition.GenericParams;
        for (var index = 0; index < genericParameters.Count && index < typeArguments.Count; index++)
        {
            substitution[genericParameters[index]] = typeArguments[index];
        }

        return SubstituteType(payloadType, substitution);
    }

    /// <summary>
    /// True when this `try` unary expression sits at a propagation boundary: walking up,
    /// every expression-precedence wrapper is a pure pass-through (applies no operator),
    /// and the first structural ancestor is a binding initializer, an assignment whose
    /// whole right side is the try, a `return`, or an expression statement.
    /// </summary>
    private static bool IsPropagationBoundaryPosition(StarkParser.UnaryExpressionContext tryExpression)
    {
        RuleContext? previous = tryExpression;
        var current = tryExpression.Parent as RuleContext;
        while (current is not null)
        {
            switch (current)
            {
                case StarkParser.ReturnStatementContext:
                case StarkParser.ExpressionStatementContext:
                case StarkParser.VariableInitializerContext:
                    return true;
                case StarkParser.AssignmentExpressionContext assignment:
                    // `lhs = try ...;` / `lhs op= try ...;` — boundary when the try is the whole
                    // right side. A pure pass-through (single child) just continues upward.
                    if (assignment.ChildCount == 1)
                    {
                        break;
                    }

                    if (assignment.assignmentExpression() is { } rhs && ReferenceEquals(rhs, previous))
                    {
                        break;
                    }

                    return false;
                case StarkParser.ExpressionContext:
                case StarkParser.ConditionalExpressionContext:
                case StarkParser.LogicalOrExpressionContext:
                case StarkParser.LogicalAndExpressionContext:
                case StarkParser.BitwiseOrExpressionContext:
                case StarkParser.BitwiseXorExpressionContext:
                case StarkParser.BitwiseAndExpressionContext:
                case StarkParser.EqualityExpressionContext:
                case StarkParser.RelationalExpressionContext:
                case StarkParser.ShiftExpressionContext:
                case StarkParser.AdditiveExpressionContext:
                case StarkParser.MultiplicativeExpressionContext:
                    if (current.ChildCount != 1)
                    {
                        return false;
                    }

                    break;
                default:
                    // argumentList, powerExpression (as an exponent), a unary operator/cast
                    // wrapper, object/array initializers, etc. — the try is nested inside a
                    // larger expression, not at a statement boundary.
                    return false;
            }

            previous = current;
            current = current.Parent as RuleContext;
        }

        return false;
    }

    private ExpressionBinding EvaluatePowerExpression(
        StarkParser.PowerExpressionContext expression,
        Scope scope,
        bool allowFunctionReference,
        StarkTypeSymbol? expectedType)
    {
        if (expression.unaryExpression() is not { } rightExpression)
        {
            return EvaluatePostfixExpression(expression.postfixExpression(), scope, allowFunctionReference, expectedType);
        }

        var left = EvaluatePostfixExpression(expression.postfixExpression(), scope, allowFunctionReference, expectedType: null);
        var right = EvaluateUnaryExpression(rightExpression, scope, allowFunctionReference: false);
        if (!IsNumeric(left.Type) || !IsNumeric(right.Type))
        {
            ReportError("STK3002", "Operator '**' requires numeric operands.", expression);
            return new ExpressionBinding(StarkTypeSymbols.Error);
        }

        var resultType = FindCommonType(left.Type, right.Type);
        if (resultType.Kind is not (StarkTypeKind.Float or StarkTypeKind.Integer))
        {
            ReportError("STK3002", "Operator '**' requires integer or floating-point operands.", expression);
            return new ExpressionBinding(StarkTypeSymbols.Error);
        }

        if (TryEvaluateCompileTimeIntegerExpression(expression, scope, expectedType, out var constantBinding))
        {
            return constantBinding;
        }

        return new ExpressionBinding(resultType);
    }

    private ExpressionBinding EvaluatePostfixExpression(
        StarkParser.PostfixExpressionContext expression,
        Scope scope,
        bool allowFunctionReference,
        StarkTypeSymbol? expectedType)
    {
        var postfixParts = expression.postfixPart();
        var firstUnhandledPostfixIndex = 0;
        ExpressionBinding binding;
        if (TryEvaluateUnsafeRawSliceConstructionPrefix(
                expression,
                scope,
                out var rawSliceBinding,
                out firstUnhandledPostfixIndex))
        {
            binding = rawSliceBinding;
        }
        else if (TryEvaluateUnsafeDynTraitFromPartsConstructionPrefix(
                     expression,
                     scope,
                     expectedType,
                     out var dynTraitFromPartsBinding,
                     out firstUnhandledPostfixIndex))
        {
            binding = dynTraitFromPartsBinding;
        }
        else
        {
            var requiresCallableTarget = postfixParts.Any(static part => part.argumentList() is not null);
            binding = TryGetPublishedTemplateEnumCallBinding(expression, out var publishedEnumCall)
                ? publishedEnumCall
                : TryGetPublishedTemplateDirectCallBinding(expression, expectedType, out var publishedBinding)
                ? publishedBinding
                : EvaluatePrimaryExpression(
                    expression.primaryExpression(),
                    scope,
                    allowFunctionReference || requiresCallableTarget,
                    postfixParts.Length == 0 ? expectedType : null);
        }

        for (var index = firstUnhandledPostfixIndex; index < postfixParts.Length; index++)
        {
            var postfixPart = postfixParts[index];
            if (postfixPart.argumentList() is { } argumentList)
            {
                binding = InvokeCall(binding, argumentList, scope);
                continue;
            }

            if (postfixPart.GetChild(0).GetText() == "[")
            {
                if (postfixPart.expressionList() is not { } expressionList)
                {
                    if (binding.Type.Kind is StarkTypeKind.Ascii or StarkTypeKind.Unicode)
                    {
                        binding = new ExpressionBinding(
                            binding.Type,
                            IsAssignable: false,
                            NamedType: ResolveNamedTypeSymbol(binding.Type),
                            DiagnosticName: binding.DiagnosticName is null ? "text slice" : $"text slice of {binding.DiagnosticName}");
                        continue;
                    }

                    ReportError("STK3002", "Index access requires at least one index expression.", postfixPart);
                    binding = new ExpressionBinding(StarkTypeSymbols.Error, DiagnosticName: "indexed element");
                    continue;
                }

                binding = ApplyIndex(binding, expressionList, scope, postfixPart);
                continue;
            }

            if (index + 1 < postfixParts.Length
                && postfixParts[index + 1].argumentList() is { } memberArguments
                && TryInvokeDynamicStorageMemberCall(binding, postfixPart.Identifier().GetText(), memberArguments, scope, postfixPart, out var dynamicMemberCall))
            {
                binding = dynamicMemberCall;
                index++;
                continue;
            }

            if (index + 1 < postfixParts.Length
                && postfixParts[index + 1].argumentList() is { } publishedMemberArguments
                && TryGetPublishedTemplateMemberCallBinding(binding, postfixPart.Identifier().GetText(), publishedMemberArguments, expectedType, out var publishedMemberCall))
            {
                binding = publishedMemberCall;
                continue;
            }

            binding = TryApplyPublishedTemplateFieldAccess(binding, postfixPart, out var publishedFieldAccess)
                ? publishedFieldAccess
                : ApplyMemberAccess(binding, postfixPart.Identifier().GetText(), postfixPart);
        }

        if (expectedType?.Kind == StarkTypeKind.FunctionPointer
            && binding.Type.Kind != StarkTypeKind.FunctionPointer)
        {
            if (binding.Function is { } function)
            {
                return ResolveFunctionPointerPromotion(
                    function.DisplaySourceName,
                    [function],
                    expectedType,
                    expression.Start);
            }

            if (binding.OverloadSourceName is { } overloadSourceName
                && TryGetFunctionOverloads(overloadSourceName, out var overloads))
            {
                return ResolveFunctionPointerPromotion(
                    overloadSourceName,
                    overloads,
                    expectedType,
                    expression.Start);
            }
        }

        if (expectedType?.Kind == StarkTypeKind.Closure
            && binding.Type.Kind != StarkTypeKind.Closure)
        {
            if (binding.Function is { } function)
            {
                return ResolveClosureFunctionPromotion(
                    function.DisplaySourceName,
                    [function],
                    expectedType,
                    expression.Start);
            }

            if (binding.OverloadSourceName is { } overloadSourceName
                && TryGetFunctionOverloads(overloadSourceName, out var overloads))
            {
                return ResolveClosureFunctionPromotion(
                    overloadSourceName,
                    overloads,
                    expectedType,
                    expression.Start);
            }
        }

        return binding;
    }

    private bool TryInvokeDynamicStorageMemberCall(
        ExpressionBinding receiver,
        string memberName,
        StarkParser.ArgumentListContext arguments,
        Scope scope,
        ParserRuleContext context,
        out ExpressionBinding result)
    {
        result = null!;
        if (receiver.Type.Kind != StarkTypeKind.Dynamic)
        {
            return false;
        }

        if (string.Equals(memberName, "MoveLast", StringComparison.Ordinal))
        {
            ReportDynamicStorageOwnerDiagnostic(receiver, "MoveLast", context);

            var moveLastArguments = arguments.argument();
            if (moveLastArguments.Length != 0)
            {
                ReportError(
                    "STK3009",
                    $"Dynamic storage MoveLast expects no arguments but received {moveLastArguments.Length}.",
                    arguments);
            }

            var elementType = receiver.Type.ElementType ?? StarkTypeSymbols.Error;
            result = new ExpressionBinding(
                elementType,
                NamedType: ResolveNamedTypeSymbol(elementType),
                DiagnosticName: "dynamic storage MoveLast result");
            RecordDynamicStorageOperation("MoveLast", receiver, elementType, moveLastArguments.Length, arguments);
            return true;
        }

        if (string.Equals(memberName, "MoveAt", StringComparison.Ordinal))
        {
            ReportDynamicStorageOwnerDiagnostic(receiver, "MoveAt", context);

            var moveAtArguments = arguments.argument();
            if (moveAtArguments.Length != 1)
            {
                ReportError(
                    "STK3009",
                    $"Dynamic storage MoveAt expects one index argument but received {moveAtArguments.Length}.",
                    arguments);
            }
            else
            {
                var index = EvaluateExpression(
                    moveAtArguments[0].expression(),
                    scope,
                    allowFunctionReference: false,
                    NonNegativeI64Type);
                if (index.Type.Kind != StarkTypeKind.Integer)
                {
                    ReportError(
                        "STK3002",
                        $"Dynamic storage MoveAt index must be an integer, but found '{index.Type.DisplayName}'.{GetExplicitConversionHint(StarkTypeSymbols.Integer(64), index.Type)}",
                        moveAtArguments[0].expression());
                }
                else if (!IsProvablyNonNegativeIntegerType(index.Type))
                {
                    ReportError(
                        "STK3002",
                        "Dynamic storage MoveAt index must be provably non-negative.",
                        moveAtArguments[0].expression());
                }
            }

            var elementType = receiver.Type.ElementType ?? StarkTypeSymbols.Error;
            result = new ExpressionBinding(
                elementType,
                NamedType: ResolveNamedTypeSymbol(elementType),
                DiagnosticName: "dynamic storage MoveAt result");
            RecordDynamicStorageOperation("MoveAt", receiver, elementType, moveAtArguments.Length, arguments);
            return true;
        }

        var isReserve = string.Equals(memberName, "Reserve", StringComparison.Ordinal);
        var isTryReserve = string.Equals(memberName, "TryReserve", StringComparison.Ordinal);
        var isTryReserveCapacity = string.Equals(memberName, "TryReserveCapacity", StringComparison.Ordinal);
        if (!isReserve && !isTryReserve && !isTryReserveCapacity)
        {
            return false;
        }

        var methodResultType = isReserve ? StarkTypeSymbols.Void : StarkTypeSymbols.Bool;
        result = new ExpressionBinding(methodResultType, DiagnosticName: $"dynamic storage {memberName}");

        ReportDynamicStorageOwnerDiagnostic(receiver, memberName, context);

        var suppliedArguments = arguments.argument();
        if (suppliedArguments.Length != 1)
        {
            var argumentName = isTryReserveCapacity ? "target-capacity" : "additional-capacity";
            ReportError(
                "STK3009",
                $"Dynamic storage {memberName} expects one {argumentName} argument but received {suppliedArguments.Length}.",
                arguments);
            return true;
        }

        var capacityOperand = EvaluateExpression(
            suppliedArguments[0].expression(),
            scope,
            allowFunctionReference: false,
            NonNegativeI64Type);
        var capacityDescription = isTryReserveCapacity ? "target capacity" : "additional capacity";
        if (capacityOperand.Type.Kind != StarkTypeKind.Integer)
        {
            ReportError(
                "STK3002",
                $"Dynamic storage {memberName} {capacityDescription} must be an integer, but found '{capacityOperand.Type.DisplayName}'.{GetExplicitConversionHint(StarkTypeSymbols.Integer(64), capacityOperand.Type)}",
                suppliedArguments[0].expression());
        }
        else if (!IsProvablyNonNegativeIntegerType(capacityOperand.Type))
        {
            ReportError(
                "STK3002",
                $"Dynamic storage {memberName} {capacityDescription} must be provably non-negative.",
                suppliedArguments[0].expression());
        }

        RecordDynamicStorageOperation(memberName, receiver, methodResultType, suppliedArguments.Length, arguments);
        return true;
    }

    private void ReportDynamicStorageOwnerDiagnostic(
        ExpressionBinding receiver,
        string memberName,
        ParserRuleContext context)
    {
        if (receiver.IsAddressable && receiver.IsAddressMutable)
        {
            return;
        }

        ReportError(
            "STK3007",
            receiver.AssignmentErrorMessage
                ?? $"Dynamic storage {memberName} requires a mutable addressable dynamic owner, but {DescribeExpressionTarget(receiver)} is not a mutable addressable owner.",
            context);
    }

    private bool TryEvaluateUnsafeRawSliceConstructionPrefix(
        StarkParser.PostfixExpressionContext expression,
        Scope scope,
        out ExpressionBinding binding,
        out int firstUnhandledPostfixIndex)
    {
        binding = null!;
        firstUnhandledPostfixIndex = 0;
        if (!string.Equals(expression.primaryExpression().Identifier()?.GetText(), "slice", StringComparison.Ordinal)
            || expression.postfixPart().Length == 0
            || expression.postfixPart()[0] is not { } callPart
            || callPart.argumentList() is not { } arguments)
        {
            return false;
        }

        firstUnhandledPostfixIndex = 1;
        if (_unsafeDepth == 0)
        {
            ReportError("STK3024", "Unsafe raw slice construction 'slice(pointer, count)' requires an unsafe context.", callPart);
        }

        var argumentList = arguments.argument();
        if (argumentList.Length != 2)
        {
            ReportError(
                "STK3009",
                $"Raw slice construction expects 2 arguments but received {argumentList.Length}.",
                arguments);
            binding = new ExpressionBinding(StarkTypeSymbols.Error);
            firstUnhandledPostfixIndex = expression.postfixPart().Length;
            return true;
        }

        var pointer = EvaluateExpression(argumentList[0].expression(), scope, allowFunctionReference: false);
        var length = EvaluateExpression(argumentList[1].expression(), scope, allowFunctionReference: false);
        BigInteger lengthMin = default;
        BigInteger lengthMax = default;
        var lengthHasRange = length.Type.Kind == StarkTypeKind.Integer
            && TryGetEffectiveIntegerRange(length.Type, out lengthMin, out lengthMax);
        var pointerType = UsesFrozenProjectionSemantics(pointer)
            ? StarkTypeSymbols.FreezeReachableView(pointer.Type)
            : pointer.Type;
        var pointerIsCompileTimeNull = IsCompileTimeNullExpression(argumentList[0].expression(), scope, pointerType);
        if (pointer.Type.Kind == StarkTypeKind.Null)
        {
            if (lengthHasRange && lengthMin > BigInteger.Zero)
            {
                ReportError(
                    "STK3029",
                    "Raw slice construction cannot use null with a provably positive element count.",
                    argumentList[0].expression());
            }
            else
            {
                ReportError(
                    "STK3002",
                    $"Raw slice construction expects a raw pointer as its first argument, but found '{pointer.Type.DisplayName}'.",
                    argumentList[0].expression());
            }

            binding = new ExpressionBinding(StarkTypeSymbols.Error);
            firstUnhandledPostfixIndex = expression.postfixPart().Length;
            return true;
        }

        if (pointerType.Kind != StarkTypeKind.RawPointer
            || pointerType.ElementType is not { } elementType)
        {
            ReportError(
                "STK3002",
                $"Raw slice construction expects a raw pointer as its first argument, but found '{pointer.Type.DisplayName}'.",
                argumentList[0].expression());
            binding = new ExpressionBinding(StarkTypeSymbols.Error);
            firstUnhandledPostfixIndex = expression.postfixPart().Length;
            return true;
        }

        if (length.Type.Kind != StarkTypeKind.Integer)
        {
            ReportError(
                "STK3002",
                $"Raw slice construction expects an integer count as its second argument, but found '{length.Type.DisplayName}'.",
                argumentList[1].expression());
        }
        else if (!IsProvablyNonNegativeIntegerType(length.Type))
        {
            ReportError(
                "STK3002",
                "Raw slice construction count must be provably non-negative.",
                argumentList[1].expression());
        }

        if (pointerIsCompileTimeNull
            && lengthHasRange
            && lengthMin > BigInteger.Zero)
        {
            ReportError(
                "STK3029",
                "Raw slice construction cannot use null with a provably positive element count.",
                argumentList[0].expression());
            binding = new ExpressionBinding(StarkTypeSymbols.Error);
            firstUnhandledPostfixIndex = expression.postfixPart().Length;
            return true;
        }

        if (pointer.MemoryRootKey is null
            && !(pointerIsCompileTimeNull
                 && lengthHasRange
                 && lengthMax == BigInteger.Zero))
        {
            ReportError(
                "STK3029",
                "Raw slice construction requires a compiler-visible raw pointer root; calls, integer casts, and other hidden-root expressions cannot produce a provenance-preserving slice.",
                argumentList[0].expression());
            binding = new ExpressionBinding(StarkTypeSymbols.Error);
            firstUnhandledPostfixIndex = expression.postfixPart().Length;
            return true;
        }

        var hasFrozenSliceProvenance = UsesFrozenProjectionSemantics(pointer)
            || elementType.AccessKind == StarkAccessKind.Frozen;
        var sliceElementType = hasFrozenSliceProvenance
            ? StarkTypeSymbols.WithQualifiers(elementType, accessKind: StarkAccessKind.None, isMutableView: false)
            : elementType;
        var sliceType = StarkTypeSymbols.ApplyQualifiers(
            StarkTypeSymbols.Slice(sliceElementType),
            isMutableView: pointerType.IsMutablePointer);
        if (hasFrozenSliceProvenance)
        {
            sliceType = StarkTypeSymbols.FreezeReachableView(sliceType);
        }

        binding = new ExpressionBinding(
            sliceType,
            IsAssignable: false,
            NamedType: ResolveNamedTypeSymbol(sliceType),
            DiagnosticName: $"raw slice '{expression.GetText()}'",
            UsesFrozenProjectionSemantics: hasFrozenSliceProvenance,
            HasConstProvenance: HasConstProvenance(pointer),
            MemoryRootKey: pointer.MemoryRootKey,
            MemoryRootIsIndependentStorage: pointer.MemoryRootIsIndependentStorage);
        return true;
    }

    private bool TryEvaluateUnsafeDynTraitFromPartsConstructionPrefix(
        StarkParser.PostfixExpressionContext expression,
        Scope scope,
        StarkTypeSymbol? expectedType,
        out ExpressionBinding binding,
        out int firstUnhandledPostfixIndex)
    {
        binding = null!;
        firstUnhandledPostfixIndex = 0;
        if (!TryGetDynTraitFromPartsOperationName(expression, out var operationName)
            || expression.postfixPart().Length == 0
            || expression.postfixPart()[0] is not { } callPart
            || callPart.argumentList() is not { } arguments)
        {
            return false;
        }

        firstUnhandledPostfixIndex = 1;
        if (_unsafeDepth == 0)
        {
            ReportError(
                "STK3024",
                $"Unsafe dynamic trait object construction '{operationName}(context, vtable)' requires an unsafe context.",
                callPart);
        }

        var argumentList = arguments.argument();
        if (argumentList.Length != 2)
        {
            ReportError(
                "STK3009",
                $"Dynamic trait object construction '{operationName}' expects 2 arguments but received {argumentList.Length}.",
                arguments);
            binding = new ExpressionBinding(StarkTypeSymbols.Error);
            firstUnhandledPostfixIndex = expression.postfixPart().Length;
            return true;
        }

        var isOwning = string.Equals(operationName, "dynbox", StringComparison.Ordinal);
        if (!TryResolveDynTraitFromPartsTargetType(expression, expectedType, isOwning, out var targetType))
        {
            binding = new ExpressionBinding(StarkTypeSymbols.Error);
            firstUnhandledPostfixIndex = expression.postfixPart().Length;
            return true;
        }

        var contextType = StarkTypeSymbols.RawPointer(StarkTypeSymbols.Integer(8), isMutable: true);
        var vtableType = StarkTypeSymbols.DynTraitVtablePointerForTraitObject(targetType);
        var contextArgument = EvaluateExpression(argumentList[0].expression(), scope, allowFunctionReference: false, contextType);
        var vtableArgument = EvaluateExpression(argumentList[1].expression(), scope, allowFunctionReference: false, vtableType);

        if (!CanAssign(contextType, contextArgument.Type))
        {
            ReportError(
                "STK3002",
                $"Dynamic trait object construction '{operationName}' expects '{contextType.DisplayName}' as its context argument, but found '{contextArgument.Type.DisplayName}'.",
                argumentList[0].expression());
        }

        if (!CanAssign(vtableType, vtableArgument.Type))
        {
            ReportError(
                "STK3002",
                $"Dynamic trait object construction '{operationName}' expects '{vtableType.DisplayName}' as its vtable argument, but found '{vtableArgument.Type.DisplayName}'.",
                argumentList[1].expression());
        }

        _boundOperations.Add(new BoundDynTraitFromPartsOperation(
            operationName,
            targetType,
            contextType,
            vtableType,
            Location(arguments),
            _currentFunctionName));

        binding = new ExpressionBinding(
            targetType,
            NamedType: ResolveNamedTypeSymbol(targetType),
            DiagnosticName: $"dynamic trait object '{operationName}'");
        return true;
    }

    private bool TryResolveDynTraitFromPartsTargetType(
        StarkParser.PostfixExpressionContext expression,
        StarkTypeSymbol? expectedType,
        bool isOwning,
        out StarkTypeSymbol targetType)
    {
        targetType = StarkTypeSymbols.Error;
        var storageKind = isOwning ? StarkDynTraitStorageKind.Heap : StarkDynTraitStorageKind.View;

        if (TryResolveExplicitDynTraitFromPartsTypeArgument(expression, storageKind, out targetType))
        {
            return targetType.Kind != StarkTypeKind.Error;
        }

        if (expectedType?.Kind == StarkTypeKind.DynTrait)
        {
            if (expectedType.DynTraitStorageKind != storageKind)
            {
                ReportError(
                    "STK3002",
                    isOwning
                        ? $"'dynbox' constructs 'heap dyn' trait objects, but the expected type is '{expectedType.DisplayName}'."
                        : $"'dynview' constructs borrowed 'dyn' trait object views, but the expected type is '{expectedType.DisplayName}'.",
                    expression.primaryExpression());
                return false;
            }

            targetType = expectedType;
            return true;
        }

        ReportError(
            "STK3019",
            isOwning
                ? "'dynbox' requires an explicit target trait, for example 'dynbox<Module.Resolver>(context, vtable)', or an expected 'heap dyn Trait' type."
                : "'dynview' requires an explicit target trait, for example 'dynview<Module.Resolver>(context, vtable)', or an expected 'borrow dyn Trait' type.",
            expression.primaryExpression());
        return false;
    }

    private bool TryResolveExplicitDynTraitFromPartsTypeArgument(
        StarkParser.PostfixExpressionContext expression,
        StarkDynTraitStorageKind storageKind,
        out StarkTypeSymbol targetType)
    {
        targetType = StarkTypeSymbols.Error;
        var genericQualifiedName = expression.primaryExpression().genericQualifiedName();
        if (genericQualifiedName is null)
        {
            return false;
        }

        var genericArguments = GenericArgumentSyntaxFacts.Resolve(
            genericQualifiedName.typeArgumentList(),
            ["T"],
            [],
            typeArgument => ResolveType(typeArgument, currentModuleName: CurrentFunctionModuleName),
            ReportError,
            visibleComptimeParameters: _currentFunctionComptimeGenericParameters);
        if (genericArguments.TypeArguments.Count != 1
            || genericArguments.TypeArguments[0].Kind == StarkTypeKind.Error)
        {
            return true;
        }

        targetType = BuildDynTraitFromPartsTargetType(
            genericArguments.TypeArguments[0],
            storageKind,
            genericQualifiedName);
        return true;
    }

    private StarkTypeSymbol BuildDynTraitFromPartsTargetType(
        StarkTypeSymbol declaredType,
        StarkDynTraitStorageKind storageKind,
        ParserRuleContext context)
    {
        if (declaredType.Kind == StarkTypeKind.DynTrait)
        {
            if (declaredType.DynTraitStorageKind != storageKind)
            {
                ReportError(
                    "STK3002",
                    storageKind == StarkDynTraitStorageKind.Heap
                        ? $"'dynbox' requires a 'heap dyn' target, but found '{declaredType.DisplayName}'."
                        : $"'dynview' requires a borrowed 'dyn' target, but found '{declaredType.DisplayName}'.",
                    context);
                return StarkTypeSymbols.Error;
            }

            return storageKind == StarkDynTraitStorageKind.View && declaredType.BorrowKind == StarkBorrowKind.None
                ? StarkTypeSymbols.ApplyQualifiers(declaredType, borrowKind: StarkBorrowKind.Borrow, isMutableView: declaredType.IsMutableView)
                : declaredType;
        }

        if (declaredType.Kind != StarkTypeKind.Named
            || declaredType.NamedType is not { } traitName
            || !_namedTypes.TryGetValue(traitName, out var traitSymbol)
            || traitSymbol.Kind != DeclarationKind.Trait)
        {
            ReportError(
                "STK3035",
                $"Dynamic trait object construction requires a trait target, but found '{declaredType.DisplayName}'.",
                context);
            return StarkTypeSymbols.Error;
        }

        if (!traitSymbol.IsDynTrait)
        {
            var simpleName = traitName.LastIndexOf('.') is var dot && dot >= 0 ? traitName[(dot + 1)..] : traitName;
            ReportError(
                "STK3035",
                $"Trait '{simpleName}' is static-only and cannot form a trait object. Declare it as 'dyn trait {simpleName}' to opt into dynamic dispatch, or use an enum for a closed set of cases.",
                context);
            return StarkTypeSymbols.Error;
        }

        var dynType = StarkTypeSymbols.DynTrait(traitName, storageKind, declaredType.TypeArguments);
        return storageKind == StarkDynTraitStorageKind.View
            ? StarkTypeSymbols.ApplyQualifiers(dynType, borrowKind: StarkBorrowKind.Borrow)
            : dynType;
    }

    private static bool TryGetDynTraitFromPartsOperationName(
        StarkParser.PostfixExpressionContext expression,
        out string operationName)
    {
        operationName = expression.primaryExpression().genericQualifiedName()?.qualifiedName().GetText()
            ?? expression.primaryExpression().Identifier()?.GetText()
            ?? string.Empty;
        return string.Equals(operationName, "dynview", StringComparison.Ordinal)
            || string.Equals(operationName, "dynbox", StringComparison.Ordinal);
    }

    private bool IsCompileTimeNullExpression(
        StarkParser.ExpressionContext expression,
        Scope scope,
        StarkTypeSymbol targetType)
    {
        if (CompileTimeExpressionEvaluator.TryEvaluate(
                expression,
                out var constant,
                CreateCompileTimeEvaluationServices(scope))
            && targetType.Kind == StarkTypeKind.RawPointer
            && CompileTimeExpressionEvaluator.TryCoerce(constant, targetType, out var coerced))
        {
            constant = coerced;

            if (constant.Kind == CompileTimeConstantKind.Null)
            {
                return true;
            }
        }

        var normalizedText = NormalizeExpressionText(expression.GetText());
        return string.Equals(normalizedText, "null", StringComparison.Ordinal)
            || normalizedText.EndsWith(")null", StringComparison.Ordinal);
    }

    private ExpressionBinding EvaluatePrimaryExpression(
        StarkParser.PrimaryExpressionContext expression,
        Scope scope,
        bool allowFunctionReference,
        StarkTypeSymbol? expectedType)
    {
        if (expression.literal() is { } literal)
        {
            if (literal.DOLLAR() is not null && literal.StringLiteral() is not null)
            {
                return EvaluateInterpolatedTextLiteral(literal, scope, expectedType);
            }

            return EvaluateLiteral(literal);
        }

        if (expression.COMPTIME() is not null && expression.block() is { } block)
        {
            return EvaluateComptimeBlockExpression(expression, block, scope, expectedType);
        }

        if (expression.SIZEOF() is not null || expression.ALIGNOF() is not null)
        {
            var kind = expression.SIZEOF() is not null ? "sizeof" : "alignof";
            var targetType = ResolveType(
                expression.type_(),
                _currentFunctionGenericParameters,
                _currentFunctionModuleName);
            var location = Location(expression);
            _typeLayoutExpressions.Add(new TypeLayoutExpressionTypingRecord(
                kind,
                targetType,
                location,
                _currentFunctionName));

            var queryKind = expression.ALIGNOF() is not null ? BoundLayoutQueryKind.AlignOf : BoundLayoutQueryKind.SizeOf;
            var resultType = TypeLayoutQueryFacts.GetResultType(queryKind);
            _boundOperations.Add(new BoundLayoutQueryOperation(
                queryKind,
                targetType,
                resultType,
                location,
                _currentFunctionName));

            return new ExpressionBinding(
                resultType,
                DiagnosticName: $"{kind}({targetType.DisplayName})");
        }

        if (expression.Identifier() is { } identifier)
        {
            return ResolveValue(identifier.GetText(), identifier.Symbol, scope, allowFunctionReference, expectedType);
        }

        if (expression.lambdaExpression() is { } lambdaExpression)
        {
            return EvaluateLambdaExpression(lambdaExpression, scope, expectedType);
        }

        if (expression.enumConstructorExpression() is { } enumConstructorExpression)
        {
            return EvaluateEnumConstructorExpression(enumConstructorExpression, scope);
        }

        if (TryGetPublishedTemplateEnumValueBinding(expression, allowFunctionReference, out var publishedEnumValue))
        {
            return publishedEnumValue;
        }

        if (expression.genericEnumCaseReference() is { } genericEnumCaseReference)
        {
            return ResolveGenericMemberReferenceValue(genericEnumCaseReference, allowFunctionReference);
        }

        if (expression.genericQualifiedName() is { } genericQualifiedName)
        {
            return ResolveGenericQualifiedNameValue(genericQualifiedName, scope, allowFunctionReference, expectedType);
        }

        if (expression.qualifiedName() is { } qualifiedName)
        {
            return ResolveValue(qualifiedName.GetText(), qualifiedName.Start, scope, allowFunctionReference, expectedType);
        }

        if (expression.objectCreationExpression() is { } objectCreationExpression)
        {
            return EvaluateObjectCreation(objectCreationExpression, scope, expectedType);
        }

        return EvaluateExpression(expression.expression(), scope, allowFunctionReference: false, expectedType);
    }

    private ExpressionBinding EvaluateComptimeBlockExpression(
        StarkParser.PrimaryExpressionContext expression,
        StarkParser.BlockContext block,
        Scope scope,
        StarkTypeSymbol? expectedType)
    {
        var previousInsideConstructorBody = _insideConstructorBody;
        var previousAllowCompileTimeOnlyStructuralFactCalls = _allowCompileTimeOnlyStructuralFactCalls;
        _insideConstructorBody = false;
        _allowCompileTimeOnlyStructuralFactCalls = true;
        try
        {
            CheckBlock(block, scope, expectedType ?? StarkTypeSymbols.Error);
        }
        finally
        {
            _insideConstructorBody = previousInsideConstructorBody;
            _allowCompileTimeOnlyStructuralFactCalls = previousAllowCompileTimeOnlyStructuralFactCalls;
        }

        CompileTimeEvaluator.ClearFailure();
        if (!CompileTimeEvaluator.TryEvaluateBlock(
                block,
                CurrentFunctionModuleName,
                expectedType,
                out var constant,
                (string name, out CompileTimeConstant value) =>
                    TryResolveCompileTimeConstant(scope, name, out value)
                    || TryResolveOpenComptimeGenericConstant(name, out value),
                initialState: CreateOpenComptimeGenericEvaluationState()))
        {
            ReportError(
                "STK3053",
                BuildComptimeEvaluationFailureMessage(
                    "`comptime` block must return a value during compilation using literals, const values, comptime generic values, local compile-time state, or finite/law calls whose bodies stay within the supported compile-time subset."),
                expression);
            return new ExpressionBinding(StarkTypeSymbols.Error);
        }

        return new ExpressionBinding(
            constant.Type,
            NamedType: ResolveNamedTypeSymbol(constant.Type),
            HasConstProvenance: true);
    }

    private string BuildComptimeEvaluationFailureMessage(string fallback)
    {
        return CompileTimeEvaluator.LastFailure is { } failure
            ? failure.Message
            : fallback;
    }

    private ExpressionBinding EvaluateObjectCreation(
        StarkParser.ObjectCreationExpressionContext expression,
        Scope scope,
        StarkTypeSymbol? expectedType)
    {
        TryGetPublishedTemplateObjectCreationSummary(expression, out var publishedObjectCreation);
        var createdType = ResolveObjectCreationType(expression, publishedObjectCreation, expectedType);
        var namedType = ResolveNamedTypeSymbol(createdType);
        if (namedType is not null
            && namedType.Kind is DeclarationKind.Doctrine or DeclarationKind.Trait)
        {
            ReportError(
                "STK3013",
                $"Cannot create an instance of compile-time-only {DescribeCompileTimeOnlyKind(namedType.Kind)} '{namedType.Name}'. {DescribeNoDynamicDispatchPolicy()}",
                expression);
            return new ExpressionBinding(StarkTypeSymbols.Error);
        }

        if (namedType?.Kind == DeclarationKind.Enum)
        {
            ReportError(
                "STK3008",
                $"Cannot create enum '{namedType.Name}' with object creation syntax. Use an enum constructor such as '{namedType.Name}.Variant(...)'.",
                expression);
            return new ExpressionBinding(StarkTypeSymbols.Error);
        }

        if (createdType.Kind == StarkTypeKind.Error)
        {
            return new ExpressionBinding(StarkTypeSymbols.Error);
        }

        ConstructorShape? matchedConstructor = null;
        IReadOnlyList<ObjectInitializerMemberTypingRecord>? initializerMembers = null;

        matchedConstructor = CheckObjectCreationArguments(expression.argumentList(), expression, createdType, scope);

        if (createdType.Kind == StarkTypeKind.Dynamic
            && expression.objectInitializer() is { } dynamicObjectInitializer)
        {
            ReportError(
                "STK3008",
                "Dynamic storage creation does not support object initializers. Use `new()` or `new(capacity)` and initialize elements through indexed `init` assignments.",
                dynamicObjectInitializer);
        }
        else if (expression.objectInitializer() is { } objectInitializer)
        {
            initializerMembers = CheckObjectInitializer(
                objectInitializer,
                createdType,
                scope,
                matchedConstructor?.InitializedMembers,
                publishedObjectCreation?.InitializerMembers,
                requireExplicitNonZeroInitializers: matchedConstructor is null || matchedConstructor.IsPrimaryShape);
        }
        else if (matchedConstructor is null)
        {
            ValidateExplicitNonZeroInitializers(
                createdType,
                initializedMembers: null,
                expression,
                "object creation");
        }

        if (ShouldTrackObjectCreation(expression) || matchedConstructor is not null)
        {
            var typedConstructor = matchedConstructor is null
                ? null
                : new TypedConstructorShape(
                    createdType.DisplayName,
                    matchedConstructor.Parameters,
                    matchedConstructor.IsPrimaryShape,
                    matchedConstructor.BodyKey);
            var location = Location(expression.Start);
            _objectCreations.Add(new ObjectCreationTypingRecord(
                expression.GetText(),
                createdType,
                typedConstructor,
                location,
                _currentFunctionName,
                initializerMembers));
            _boundOperations.Add(new BoundObjectCreationOperation(
                expression.GetText(),
                createdType,
                typedConstructor,
                initializerMembers,
                location,
                _currentFunctionName));
        }

        return new ExpressionBinding(createdType, NamedType: ResolveNamedTypeSymbol(createdType), DiagnosticName: $"new '{createdType.DisplayName}'");
    }

    private static bool ShouldTrackObjectCreation(StarkParser.ObjectCreationExpressionContext expression)
    {
        return expression.type_() is null
            || expression.objectInitializer() is not null
            || expression.argumentList() is { } argumentList && argumentList.argument().Length > 0;
    }

    private StarkTypeSymbol ResolveObjectCreationType(
        StarkParser.ObjectCreationExpressionContext expression,
        ImportedTemplateObjectCreationSummary? publishedObjectCreation,
        StarkTypeSymbol? expectedType)
    {
        if (publishedObjectCreation is not null)
        {
            return EnsureMonomorphizedType(publishedObjectCreation.CreatedType, Location(expression.Start));
        }

        if (expression.type_() is { } explicitType)
        {
            return ResolveType(explicitType, currentModuleName: CurrentFunctionModuleName);
        }

        if (expectedType is null || expectedType.Kind is StarkTypeKind.Error or StarkTypeKind.Void)
        {
            ReportError(
                "STK3002",
                "Target-typed object creation requires an expected named target type. Use an explicit type such as 'new TypeName(...)' when the target type is not known.",
                expression);
            return StarkTypeSymbols.Error;
        }

        if (expectedType.Kind != StarkTypeKind.Named || expectedType.NamedType is null)
        {
            if (expectedType.Kind == StarkTypeKind.Dynamic && expectedType.ElementType is not null)
            {
                return expectedType;
            }

            ReportError(
                "STK3002",
                $"Target-typed object creation requires a named target type, but got '{expectedType.DisplayName}'.",
                expression);
            return StarkTypeSymbols.Error;
        }

        return expectedType;
    }

    private ExpressionBinding EvaluateLambdaExpression(
        StarkParser.LambdaExpressionContext expression,
        Scope scope,
        StarkTypeSymbol? expectedType)
    {
        var lambdaLocation = Location(expression);
        var captureBindings = ValidateLambdaCaptures(expression.captureClause(), scope, lambdaLocation);

        if (expectedType?.Kind is not (StarkTypeKind.FunctionPointer or StarkTypeKind.Closure))
        {
            ReportError(
                "STK3008",
                "Lambda expressions require an explicit function-pointer or closure target type.",
                expression);
            return new ExpressionBinding(StarkTypeSymbols.Error, DiagnosticName: "lambda");
        }

        ValidateLambdaStoragePrefix(expression, expectedType);

        var parameterTypes = GetCallableParameterTypes(expectedType);
        var returnType = GetCallableReturnType(expectedType);
        var lambdaScope = Scope.CreateRoot(_globals);
        foreach (var captureBinding in captureBindings)
        {
            var capturedLocal = captureBinding.Symbol;
            lambdaScope.Declare(new VariableSymbol(
                capturedLocal.Name,
                CallableValueFacts.GetLambdaCaptureBodyType(capturedLocal.Type, captureBinding.Mode),
                IsMutable: CallableValueFacts.LambdaCaptureModeExposesWritableBinding(captureBinding.Mode),
                IsConstant: false));
        }

        var lambdaParameters = expression.lambdaParameterList().parameter();
        var lambdaParameterNames = lambdaParameters
            .Select(static parameter => parameter.Identifier().GetText())
            .ToArray();
        var parameterNames = new List<string>(lambdaParameters.Length);
        var parametersExactlyMatchTarget = true;

        if (lambdaParameters.Length != parameterTypes.Count)
        {
            ReportError(
                "STK3009",
                $"Lambda target '{expectedType.DisplayName}' expects {parameterTypes.Count} parameter{Pluralize(parameterTypes.Count)} but the lambda declares {lambdaParameters.Length}.",
                expression.lambdaParameterList());
        }

        for (var index = 0; index < lambdaParameters.Length; index++)
        {
            var parameter = lambdaParameters[index];
            var parameterName = lambdaParameterNames[index];
            parameterNames.Add(parameterName);
            if (captureBindings.Any(capture => string.Equals(capture.Symbol.Name, parameterName, StringComparison.Ordinal)))
            {
                ReportError(
                    "STK3006",
                    $"Lambda parameter '{parameterName}' reuses a captured name. Rename the parameter or remove the capture so capture bindings and lambda parameters stay distinct.",
                    parameter.Identifier().Symbol);
            }

            var parameterType = ResolveType(parameter.type_(), currentModuleName: CurrentFunctionModuleName);
            ValidateRuntimeValueType(parameterType, parameter.type_(), $"lambda parameter '{parameter.Identifier().GetText()}'");
            if (index < parameterTypes.Count && !CanAssign(parameterType, parameterTypes[index]))
            {
                ReportError(
                    "STK3002",
                    $"Lambda parameter {index + 1} expects a type that can accept '{parameterTypes[index].DisplayName}' from target '{expectedType.DisplayName}' but found '{parameterType.DisplayName}'.",
                    parameter.type_());
            }
            else if (index < parameterTypes.Count && !Equals(parameterType, parameterTypes[index]))
            {
                parametersExactlyMatchTarget = false;
            }

            lambdaScope.Declare(new VariableSymbol(
                parameterName,
                parameterType,
                IsMutable: false,
                IsConstant: false,
                RawPointerElementCountExpression: index < parameterTypes.Count
                    ? MapFunctionPointerRawPointerElementCountExpressionToParameterNames(
                        GetCallableParameterRawPointerElementCountExpression(expectedType, index),
                        lambdaParameterNames)
                    : null));
        }

        LambdaTypingRecord? lambda = null;
        ClosureLambdaTypingRecord? closureLambda = null;
        if (lambdaParameters.Length == parameterTypes.Count && _currentFunctionName is { } enclosingFunctionName)
        {
            var functionName = CallableValueFacts.BuildLambdaFunctionName(enclosingFunctionName, lambdaLocation);
            if (expectedType.Kind == StarkTypeKind.FunctionPointer)
            {
                lambda = new LambdaTypingRecord(
                    functionName,
                    expectedType,
                    parameterNames,
                    lambdaLocation,
                    enclosingFunctionName);
            }
            else
            {
                var captureFields = BuildClosureCaptureFields(captureBindings);
                var environmentTypeName = CallableValueFacts.BuildClosureEnvironmentTypeName(functionName);
                if (captureFields.Count > 0)
                {
                    _namedTypes[environmentTypeName] = CallableValueFacts.BuildClosureEnvironmentNamedType(
                        environmentTypeName,
                        captureFields);
                }

                closureLambda = new ClosureLambdaTypingRecord(
                    functionName,
                    expectedType,
                    CallableValueFacts.BuildClosureEnvironmentPointerType(expectedType),
                    parameterNames,
                    lambdaLocation,
                    enclosingFunctionName,
                    environmentTypeName,
                    captureFields);
            }
        }

        var previousFunctionName = _currentFunctionName;
        var typeBodyAsLambdaFunction =
            (lambda is not null && captureBindings.Count == 0 || closureLambda is not null)
            && !TypeContainsOpenCurrentFunctionGenericParameter(expectedType)
            && parametersExactlyMatchTarget;
        if (typeBodyAsLambdaFunction)
        {
            _currentFunctionName = lambda?.FunctionName ?? closureLambda!.FunctionName;
        }

        try
        {
            if (expression.expression() is { } bodyExpression)
            {
                var bodyValue = EvaluateExpression(bodyExpression, lambdaScope, allowFunctionReference: false, expectedType: returnType);
                if (!CanAssign(returnType, bodyValue.Type))
                {
                    ReportError(
                        "STK3002",
                        $"Lambda body expects '{returnType.DisplayName}' but found '{bodyValue.Type.DisplayName}'.{GetExplicitConversionHint(returnType, bodyValue.Type)}",
                        bodyExpression);
                }
            }
            else if (expression.block() is { } block)
            {
                CheckBlock(block, lambdaScope, returnType);
                ValidateFunctionReturnsOnAllPaths(block, returnType, "Lambda", block);
            }
        }
        finally
        {
            _currentFunctionName = previousFunctionName;
        }

        if (captureBindings.Count > 0)
        {
            if (expectedType.Kind == StarkTypeKind.FunctionPointer)
            {
                ReportError(
                    "STK3008",
                    "A lambda converted to 'fnptr<...>' cannot capture local state because function pointers do not carry closure storage. Use a named function item or pass captured state explicitly.",
                    (ParserRuleContext?)expression.captureClause() ?? expression);
                return new ExpressionBinding(expectedType, DiagnosticName: "lambda");
            }

            ValidateClosureCaptureLegality(expectedType, captureBindings, expression);
        }

        if (TypeContainsOpenCurrentFunctionGenericParameter(expectedType))
        {
            ReportError(
                "STK3008",
                $"A lambda converted to '{expectedType.DisplayName}' requires a concrete target type. Use a named generic function item or instantiate the target type before converting the lambda.",
                expression);
            return new ExpressionBinding(expectedType, DiagnosticName: "lambda");
        }

        if (!parametersExactlyMatchTarget)
        {
            ReportError(
                "STK3002",
                $"Lowered lambdas require parameter annotations to exactly match target '{expectedType.DisplayName}' so the generated callable has an exact ABI signature.",
                expression.lambdaParameterList());
            return new ExpressionBinding(expectedType, DiagnosticName: "lambda");
        }

        if (lambda is not null)
        {
            _lambdas.Add(lambda);
            _functions.TryAdd(lambda.FunctionName, CallableValueFacts.BuildLambdaSignature(lambda));
        }

        if (closureLambda is not null)
        {
            _closureLambdas.Add(closureLambda);
            _functions.TryAdd(closureLambda.FunctionName, CallableValueFacts.BuildClosureLambdaSignature(closureLambda));
        }

        return new ExpressionBinding(expectedType, DiagnosticName: "lambda");
    }

    private static IReadOnlyList<ClosureCaptureFieldSymbol> BuildClosureCaptureFields(
        IReadOnlyList<LambdaCaptureBinding> captureBindings)
    {
        if (captureBindings.Count == 0)
        {
            return [];
        }

        return captureBindings
            .Select(static capture =>
            {
                var sourceType = capture.Symbol.Type;
                var bodyType = CallableValueFacts.GetLambdaCaptureBodyType(sourceType, capture.Mode);
                return new ClosureCaptureFieldSymbol(
                    capture.Symbol.Name,
                    capture.Symbol.Name,
                    capture.Mode,
                    capture.IsUnsafe,
                    sourceType,
                    bodyType,
                    CallableValueFacts.GetLambdaCaptureFieldType(sourceType, capture.Mode),
                    CallableValueFacts.GetLambdaCaptureStorageKind(capture.Mode));
            })
            .ToArray();
    }

    private void ValidateLambdaStoragePrefix(StarkParser.LambdaExpressionContext expression, StarkTypeSymbol expectedType)
    {
        var hasHeapPrefix = expression.lambdaStoragePrefix()?.HEAP() is not null;
        if (!hasHeapPrefix)
        {
            if (expectedType.Kind == StarkTypeKind.Closure
                && expectedType.ClosureStorageKind == StarkClosureStorageKind.Heap)
            {
                ReportError(
                    "STK3008",
                    "A lambda converted to a heap closure must use the explicit 'heap' lambda prefix, for example `heap capture(...) (...) => ...`.",
                    expression);
            }

            return;
        }

        if (expectedType.Kind != StarkTypeKind.Closure
            || expectedType.ClosureStorageKind != StarkClosureStorageKind.Heap)
        {
            ReportError(
                "STK3008",
                "The 'heap' lambda prefix is only valid when the target type is `heap closure<...>`.",
                expression.lambdaStoragePrefix() ?? (ParserRuleContext)expression);
        }
    }

    private static IReadOnlyList<StarkTypeSymbol> GetCallableParameterTypes(StarkTypeSymbol callableType)
    {
        return callableType.Kind == StarkTypeKind.Closure
            ? callableType.ClosureParameterTypes ?? []
            : callableType.FunctionPointerParameterTypes ?? [];
    }

    private static StarkTypeSymbol GetCallableReturnType(StarkTypeSymbol callableType)
    {
        return callableType.Kind == StarkTypeKind.Closure
            ? callableType.ClosureReturnType ?? StarkTypeSymbols.Error
            : callableType.FunctionPointerReturnType ?? StarkTypeSymbols.Error;
    }

    private static string? GetCallableParameterRawPointerElementCountExpression(
        StarkTypeSymbol callableType,
        int parameterIndex)
    {
        return callableType.Kind == StarkTypeKind.Closure
            ? StarkTypeSymbols.GetClosureParameterRawPointerElementCountExpression(callableType, parameterIndex)
            : StarkTypeSymbols.GetFunctionPointerParameterRawPointerElementCountExpression(callableType, parameterIndex);
    }

    private void ValidateClosureCaptureLegality(
        StarkTypeSymbol closureType,
        IReadOnlyList<LambdaCaptureBinding> captureBindings,
        StarkParser.LambdaExpressionContext expression)
    {
        if (closureType.Kind != StarkTypeKind.Closure
            || closureType.ClosureStorageKind != StarkClosureStorageKind.Heap)
        {
            return;
        }

        foreach (var capture in captureBindings)
        {
            if (capture.Symbol.Type.BorrowKind is StarkBorrowKind.Borrow or StarkBorrowKind.RetBorrow)
            {
                ReportError(
                    "STK3008",
                    $"Heap closure capture '{capture.Symbol.Name}' has non-escaping type '{capture.Symbol.Type.DisplayName}'. Heap closures may retain only owned values, raw capabilities, or explicit 'storeborrow' views; use a borrowed or inline closure for non-owning local captures.",
                    (ParserRuleContext?)expression.captureClause() ?? expression);
                continue;
            }

            if (IsHeapClosureSafeCaptureMode(capture.Mode))
            {
                continue;
            }

            ReportError(
                "STK3008",
                $"Heap closure capture mode '{capture.Mode}' would retain a borrowed view of local storage. Heap closures may capture with 'copy', 'move', or an explicit unsafe shared capture; use a borrowed or inline closure for non-owning local captures.",
                (ParserRuleContext?)expression.captureClause() ?? expression);
        }
    }

    private static bool IsHeapClosureSafeCaptureMode(string mode)
    {
        return string.Equals(mode, "copy", StringComparison.Ordinal)
            || string.Equals(mode, "move", StringComparison.Ordinal)
            || string.Equals(mode, "shared", StringComparison.Ordinal);
    }

    private IReadOnlyList<LambdaCaptureBinding> ValidateLambdaCaptures(
        StarkParser.CaptureClauseContext? captureClause,
        Scope scope,
        SourceLocation lambdaLocation)
    {
        if (captureClause is null)
        {
            return [];
        }

        if (!string.Equals(captureClause.Identifier().GetText(), "capture", StringComparison.Ordinal))
        {
            ReportError("STK3008", $"Unknown lambda capture clause '{captureClause.Identifier().GetText()}'.", captureClause.Identifier().Symbol);
        }

        var seenCaptures = new HashSet<string>(StringComparer.Ordinal);
        var capturedLocals = new List<LambdaCaptureBinding>();
        foreach (var capture in captureClause.captureBinding())
        {
            var mode = capture.captureMode().GetText();
            var name = capture.Identifier().GetText();
            var hasUnsafeKeyword = capture.UNSAFE() is not null;
            var isUnsafeMode = string.Equals(mode, "addr", StringComparison.Ordinal)
                || string.Equals(mode, "shared", StringComparison.Ordinal);
            var isSafeMode = string.Equals(mode, "copy", StringComparison.Ordinal)
                || string.Equals(mode, "move", StringComparison.Ordinal)
                || string.Equals(mode, "read", StringComparison.Ordinal)
                || string.Equals(mode, "mut", StringComparison.Ordinal)
                || string.Equals(mode, "out", StringComparison.Ordinal)
                || string.Equals(mode, "init", StringComparison.Ordinal);

            if (!isSafeMode && !isUnsafeMode)
            {
                ReportError("STK3008", $"Unknown lambda capture mode '{mode}'.", capture.captureMode());
            }

            if (isUnsafeMode && !hasUnsafeKeyword)
            {
                ReportError("STK3024", $"Capture mode '{mode}' must be written as 'unsafe {mode}'.", capture.captureMode());
            }

            if (hasUnsafeKeyword && !isUnsafeMode)
            {
                ReportError("STK3024", $"Only 'addr' and 'shared' capture modes may be marked unsafe.", capture);
            }

            if (hasUnsafeKeyword && _unsafeDepth == 0)
            {
                ReportError("STK3024", $"Capture mode 'unsafe {mode}' requires an unsafe context.", capture);
            }

            if (!seenCaptures.Add(name))
            {
                ReportError("STK3006", $"Lambda capture '{name}' is listed more than once.", capture.Identifier().Symbol);
            }

            if (!scope.TryLookup(name, out var capturedLocal))
            {
                ReportError("STK3003", $"Unknown captured local '{name}'.", capture.Identifier().Symbol);
            }
            else
            {
                if (string.Equals(mode, "copy", StringComparison.Ordinal) && !CanCopyIntoLambdaEnvironment(capturedLocal.Type))
                {
                    ReportError(
                        "STK3002",
                        $"Capture mode 'copy' cannot copy '{name}' because '{capturedLocal.Type.DisplayName}' is an owned or move-only value. Use 'move' to transfer ownership into the closure, or 'read' to capture read-only access.",
                        capture);
                }

                if (RequiresWritableCaptureTarget(mode) && !CanFormMutableAddressFromLocal(capturedLocal))
                {
                    ReportError(
                        "STK3002",
                        $"Capture mode '{mode}' needs '{name}' to be a writable local, such as a 'mut' local or mutable destination.",
                        capture);
                }

                capturedLocals.Add(new LambdaCaptureBinding(capturedLocal, mode, hasUnsafeKeyword));
                _lambdaCaptures.Add(new LambdaCaptureTypingRecord(
                    name,
                    mode,
                    hasUnsafeKeyword,
                    capturedLocal.Type,
                    Location(capture),
                    lambdaLocation,
                    _currentFunctionName));
            }
        }

        return capturedLocals;
    }

    private static bool RequiresWritableCaptureTarget(string mode)
    {
        return CallableValueFacts.LambdaCaptureModeExposesWritableBinding(mode);
    }

    private static bool CanCopyIntoLambdaEnvironment(StarkTypeSymbol type)
    {
        if (type.Kind is StarkTypeKind.Error or StarkTypeKind.Void)
        {
            return false;
        }

        if (type.BorrowKind != StarkBorrowKind.None)
        {
            return !type.IsMutableView;
        }

        return type.Kind is
            StarkTypeKind.Bool or
            StarkTypeKind.Integer or
            StarkTypeKind.Float or
            StarkTypeKind.Ascii or
            StarkTypeKind.Unicode or
            StarkTypeKind.RawPointer or
            StarkTypeKind.FunctionPointer or
            StarkTypeKind.Null;
    }

    private ExpressionBinding EvaluateEnumConstructorExpression(
        StarkParser.EnumConstructorExpressionContext expression,
        Scope scope)
    {
        TryGetPublishedTemplateEnumConstructorSummary(expression, out var publishedEnumConstructor);

        string constructorName;
        NamedTypeSymbol? enumType;
        StarkTypeSymbol enumTypeSymbol;
        EnumVariantSymbol? variant;
        if (publishedEnumConstructor is not null)
        {
            enumTypeSymbol = EnsureMonomorphizedType(publishedEnumConstructor.EnumType, Location(expression.enumCaseTarget()));
            enumType = ResolveNamedTypeSymbol(enumTypeSymbol);
            variant = enumType is not null && enumType.TryGetVariant(publishedEnumConstructor.VariantName, out var resolvedVariant, out _)
                ? resolvedVariant
                : null;
            constructorName = $"{enumTypeSymbol.DisplayName}.{publishedEnumConstructor.VariantName}";
        }
        else if (TryResolveEnumCaseTarget(expression.enumCaseTarget(), out _, out var resolvedEnumType, out var resolvedEnumTypeSymbol, out var resolvedVariant))
        {
            enumType = resolvedEnumType;
            enumTypeSymbol = resolvedEnumTypeSymbol;
            variant = resolvedVariant;
            constructorName = expression.enumCaseTarget().GetText();
        }
        else
        {
            ReportError("STK3003", $"Unknown symbol '{expression.enumCaseTarget().GetText()}'.", expression);
            return new ExpressionBinding(StarkTypeSymbols.Error);
        }

        if (enumType is null || variant is null)
        {
            ReportError("STK3003", $"Unknown symbol '{constructorName}'.", expression);
            return new ExpressionBinding(StarkTypeSymbols.Error);
        }

        if (!variant.UsesNamedFields)
        {
            ReportError(
                "STK3008",
                variant.IsUnit
                    ? $"Enum case '{constructorName}' is unit-like and may not use a named-field initializer."
                    : $"Enum case '{constructorName}' is tuple-like and must use positional arguments, not a named-field initializer.",
                expression);
            return new ExpressionBinding(StarkTypeSymbols.Error);
        }

        var hasErrors = false;
        var seenMemberIndexes = new HashSet<int>();
        var recordedMembers = new List<EnumConstructorMemberTypingRecord>(expression.enumConstructorInitializer().enumConstructorMember().Length);

        for (var memberOrdinal = 0; memberOrdinal < expression.enumConstructorInitializer().enumConstructorMember().Length; memberOrdinal++)
        {
            var member = expression.enumConstructorInitializer().enumConstructorMember(memberOrdinal);
            var memberName = member.Identifier().GetText();
            StarkTypeSymbol? fieldType = null;
            var fieldIndex = -1;

            if (publishedEnumConstructor is not null && memberOrdinal < publishedEnumConstructor.Members.Count)
            {
                var publishedMember = publishedEnumConstructor.Members[memberOrdinal];
                memberName = publishedMember.FieldName;
                fieldIndex = publishedMember.FieldIndex;
                fieldType = EnsureMonomorphizedType(publishedMember.FieldType, Location(member.expression()));
            }
            else
            {
                for (var fieldOrdinal = 0; fieldOrdinal < variant.Fields.Count; fieldOrdinal++)
                {
                    var field = variant.Fields[fieldOrdinal];
                    if (!string.Equals(field.Name, memberName, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    fieldType = field.Type;
                    fieldIndex = fieldOrdinal;
                    break;
                }
            }

            if (fieldType is null
                || fieldIndex < 0
                || fieldIndex >= variant.Fields.Count)
            {
                ReportError("STK3005", $"Enum case '{constructorName}' does not contain a field named '{memberName}'.", member);
                hasErrors = true;
                continue;
            }

            if (!seenMemberIndexes.Add(fieldIndex))
            {
                ReportError("STK3006", $"Enum constructor member '{memberName}' for '{constructorName}' is assigned more than once.", member);
                hasErrors = true;
                continue;
            }

            var valueType = EvaluateExpression(member.expression(), scope, allowFunctionReference: false, expectedType: fieldType).Type;
            recordedMembers.Add(new EnumConstructorMemberTypingRecord(memberName, fieldIndex, fieldType));

            if (!CanAssign(fieldType, valueType))
            {
                hasErrors = true;
                ReportError(
                    "STK3002",
                    $"Enum constructor member '{memberName}' for '{constructorName}' expects '{fieldType.DisplayName}' but found '{valueType.DisplayName}'.{GetExplicitConversionHint(fieldType, valueType)}",
                    member.expression());
            }
        }

        for (var fieldIndex = 0; fieldIndex < variant.Fields.Count; fieldIndex++)
        {
            var field = variant.Fields[fieldIndex];
            if (field.Name is not null && !seenMemberIndexes.Contains(fieldIndex))
            {
                ReportError("STK3009", $"Enum constructor '{constructorName}' requires member '{field.Name}'.", expression);
                hasErrors = true;
            }
        }

        if (hasErrors)
        {
            return new ExpressionBinding(StarkTypeSymbols.Error);
        }

        RecordEnumConstructor(enumTypeSymbol, variant.Name, expression, recordedMembers);
        return new ExpressionBinding(enumTypeSymbol, NamedType: enumType, DiagnosticName: $"enum constructor '{constructorName}'");
    }

    private ExpressionBinding InvokeCall(ExpressionBinding target, StarkParser.ArgumentListContext arguments, Scope scope)
    {
        if (target.EnumConstructor is not null)
        {
            return InvokeEnumConstructor(target, arguments, scope);
        }

        if (target.Function is null && target.Type.Kind == StarkTypeKind.FunctionPointer)
        {
            return InvokeIndirectCall(target, arguments, scope);
        }

        if (target.Function is null && target.Type.Kind == StarkTypeKind.Closure)
        {
            return InvokeClosureCall(target, arguments, scope);
        }

        StarkTypeSymbol[]? argumentTypes = null;
        ExpressionBinding[]? argumentBindings = null;

        var overloadSourceName = target.OverloadSourceName;
        if (overloadSourceName is not null || target.OverloadCandidates is { Count: > 0 })
        {
            var displayOverloadName = target.OverloadSourceName ?? target.DiagnosticName ?? "overload group";
            var overloads = target.OverloadCandidates;
            if (overloads is null && !TryGetFunctionOverloads(overloadSourceName!, out overloads))
            {
                ReportError("STK3008", $"{DescribeExpressionTarget(target)} is not callable.", arguments);
                return new ExpressionBinding(StarkTypeSymbols.Error);
            }

            argumentBindings = EvaluateArguments(arguments, expectedParameters: null, scope);
            argumentTypes = argumentBindings.Select(static argument => argument.Type).ToArray();
            var resolution = FunctionOverloadFacts.Resolve(
                overloads,
                target.Receiver?.Type,
                argumentTypes,
                CanAssign,
                ResolveAssociatedTypeForSubstitution);
            if (!resolution.Succeeded)
            {
                ReportOverloadResolutionFailure(displayOverloadName, argumentTypes, resolution, arguments);
                return new ExpressionBinding(StarkTypeSymbols.Error);
            }

            var resolvedFunction = CacheFunctionInstantiation(resolution.Match!);
            target = target with
            {
                Function = resolvedFunction,
                OverloadSourceName = null,
                OverloadCandidates = null,
                Type = resolvedFunction.ReturnType,
                NamedType = ResolveNamedTypeSymbol(resolvedFunction.ReturnType),
                DiagnosticName = $"function '{resolvedFunction.DisplaySourceName}'"
            };
        }

        if (target.Function is null)
        {
            ReportError("STK3008", $"{DescribeExpressionTarget(target)} is not callable.", arguments);
            return new ExpressionBinding(StarkTypeSymbols.Error);
        }

        if (CompileTimeStructuralFacts.IsSignature(target.Function))
        {
            if (arguments.argument().Length != 0)
            {
                ReportError(
                    "STK3009",
                    $"Compile-time structural fact '{target.Function.DisplaySourceName}' expects 0 arguments but received {arguments.argument().Length}.",
                    arguments);
                return new ExpressionBinding(StarkTypeSymbols.Error);
            }

            if (!_allowCompileTimeOnlyStructuralFactCalls)
            {
                ReportError(
                    "STK3054",
                    $"Compile-time structural fact '{target.Function.DisplaySourceName}' may only be used inside a `comptime` expression or block.",
                    arguments);
                return new ExpressionBinding(StarkTypeSymbols.Error);
            }

            return new ExpressionBinding(
                target.Function.ReturnType,
                DiagnosticName: $"compile-time structural fact '{target.Function.DisplaySourceName}'");
        }

        if (target.Function.IsUnsafe && _unsafeDepth == 0)
        {
            ReportError(
                "STK3024",
                $"Unsafe function '{target.Function.DisplaySourceName}' requires an unsafe context.",
                arguments);
        }

        // Record use-site generic calls even when the call target did not flow through
        // overload resolution in this invocation (for example, imported typed-template
        // direct/member call facts that already carry a resolved signature).
        RecordFunctionInstantiationTrigger(target.Function, arguments);

        var receiverOffset = target.Receiver is null ? 0 : 1;
        var explicitParameterCount = Math.Max(0, target.Function.Parameters.Count - receiverOffset);
        argumentBindings ??= EvaluateArguments(
            arguments,
            target.Function.Parameters.Skip(receiverOffset).ToArray(),
            scope);
        argumentTypes ??= argumentBindings.Select(static argument => argument.Type).ToArray();

        if (target.Function.IsVarargs)
        {
            if (arguments.argument().Length < explicitParameterCount)
            {
                ReportError(
                    "STK3009",
                    $"Function '{target.Function.DisplaySourceName}' expects at least {explicitParameterCount} arguments but received {arguments.argument().Length}.",
                    arguments);
            }
        }
        else if (explicitParameterCount != arguments.argument().Length)
        {
            ReportError(
                "STK3009",
                $"Function '{target.Function.DisplaySourceName}' expects {explicitParameterCount} arguments but received {arguments.argument().Length}.",
                arguments);
        }

        if (target.Receiver is not null && target.Function.Parameters.Count != 0)
        {
            EnsureReceiverArgumentCompatible(
                target.Function.DisplaySourceName,
                target.Function.Parameters[0],
                target.Receiver,
                arguments);
        }

        for (var index = 0; index < Math.Min(explicitParameterCount, argumentTypes.Length); index++)
        {
            var parameter = target.Function.Parameters[index + receiverOffset];
            var argument = argumentBindings[index];
            EnsureCallArgumentCompatible(target.Function.DisplaySourceName, index + receiverOffset + 1, parameter, argument, arguments.argument(index).expression());
        }

        ValidateBoundedRawPointerCallArguments(
            target.Function,
            receiverOffset,
            arguments,
            argumentBindings,
            target.Function.DisplaySourceName,
            scope);

        ValidateDisjointCallArguments(
            target.Function,
            target.Receiver,
            receiverOffset,
            arguments,
            argumentBindings,
            target.Function.DisplaySourceName,
            scope);

        ValidateThreadSafetyLawCallPredicates(
            target.Function,
            target.Function.DisplaySourceName,
            arguments);

        if (target.Function.IsVarargs)
        {
            for (var index = explicitParameterCount; index < argumentTypes.Length; index++)
            {
                var argumentType = argumentTypes[index];
                if (!IsCVarargsStableArgumentType(argumentType))
                {
                    ReportError(
                        "STK3009",
                        $"Extra argument {index + 1} to '{target.Function.DisplaySourceName}' uses '{argumentType.DisplayName}', which is not safe to pass through C-style varargs as-is. Use i32/u32 or wider integers, f64, raw pointers, or text. Cast f32 to f64 and small integers to i32/u32 before passing them.",
                        arguments.argument(index).expression());
                }
            }

            ValidateCVarargsFormatStringArguments(
                target.Function,
                receiverOffset,
                explicitParameterCount,
                arguments,
                argumentBindings,
                scope);
        }

        var callArgumentRecords = BuildCallArgumentRecords(
            target.Function.Parameters,
            target.Receiver,
            argumentBindings,
            receiverOffset);

        if (target.Receiver is null)
        {
            RecordDirectCall(target.Function, arguments, callArgumentRecords);
        }
        else
        {
            RecordMemberCall(target.Function, arguments, callArgumentRecords);
        }

        var returnType = target.Function.ReturnType;
        var resultHasMemoryRoot = TryGetCallResultMemoryRoot(
            target.Function,
            target.Receiver,
            argumentBindings,
            out var resultMemoryRootKey,
            out var resultMemoryRootIsIndependentStorage);
        if (returnType.BorrowKind != StarkBorrowKind.None)
        {
            var valueType = GetBorrowReturnExpressionValueType(returnType);
            var isPointerBacked = StarkTypeSymbols.IsPointerBackedBorrowReturn(returnType);
            return new ExpressionBinding(
                valueType,
                IsAssignable: isPointerBacked && returnType.IsMutableView,
                NamedType: ResolveNamedTypeSymbol(valueType),
                DiagnosticName: $"call to '{target.Function.DisplaySourceName}'",
                IsAddressable: true,
                IsAddressMutable: returnType.IsMutableView,
                MemoryRootKey: resultHasMemoryRoot ? resultMemoryRootKey : null,
                MemoryRootIsIndependentStorage: resultHasMemoryRoot && resultMemoryRootIsIndependentStorage);
        }

        return new ExpressionBinding(
            returnType,
            NamedType: ResolveNamedTypeSymbol(returnType),
            DiagnosticName: $"call to '{target.Function.DisplaySourceName}'",
            MemoryRootKey: resultHasMemoryRoot ? resultMemoryRootKey : null,
            MemoryRootIsIndependentStorage: resultHasMemoryRoot && resultMemoryRootIsIndependentStorage);
    }

    private static bool TryGetCallResultMemoryRoot(
        TypedFunctionSignature function,
        ExpressionBinding? receiver,
        IReadOnlyList<ExpressionBinding> argumentBindings,
        out string? rootKey,
        out bool isIndependentStorage)
    {
        rootKey = null;
        isIndependentStorage = false;

        if (function.ReturnType.BorrowKind != StarkBorrowKind.None
            || function.ReturnType.Kind is StarkTypeKind.Ascii or StarkTypeKind.Unicode
                && IsTextViewConversion(function.DisplaySourceName)
            || function.ReturnType.Kind == StarkTypeKind.RawPointer
                && IsRawPointerViewConversion(function.DisplaySourceName))
        {
            var source = receiver ?? (argumentBindings.Count > 0 ? argumentBindings[0] : null);
            if (source?.MemoryRootKey is { Length: > 0 } sourceRootKey)
            {
                rootKey = sourceRootKey;
                isIndependentStorage = source.MemoryRootIsIndependentStorage;
                return true;
            }
        }

        return false;
    }

    private static bool IsTextViewConversion(string sourceName)
    {
        return string.Equals(sourceName, "System.Text.AsciiView", StringComparison.Ordinal)
            || string.Equals(sourceName, "System.Text.UnicodeView", StringComparison.Ordinal)
            || string.Equals(sourceName, "AsciiView", StringComparison.Ordinal)
            || string.Equals(sourceName, "UnicodeView", StringComparison.Ordinal)
            || sourceName.EndsWith(".AsciiView", StringComparison.Ordinal)
            || sourceName.EndsWith(".UnicodeView", StringComparison.Ordinal)
            || sourceName.EndsWith(".View", StringComparison.Ordinal);
    }

    private static bool IsRawPointerViewConversion(string sourceName)
    {
        return string.Equals(sourceName, "AsciiData", StringComparison.Ordinal)
            || string.Equals(sourceName, "UnicodeData", StringComparison.Ordinal)
            || string.Equals(sourceName, "ReadPointer", StringComparison.Ordinal)
            || string.Equals(sourceName, "WritePointer", StringComparison.Ordinal)
            || string.Equals(sourceName, "ReadWritePointer", StringComparison.Ordinal)
            || sourceName.EndsWith(".AsciiData", StringComparison.Ordinal)
            || sourceName.EndsWith(".UnicodeData", StringComparison.Ordinal)
            || sourceName.EndsWith(".ReadPointer", StringComparison.Ordinal)
            || sourceName.EndsWith(".WritePointer", StringComparison.Ordinal)
            || sourceName.EndsWith(".ReadWritePointer", StringComparison.Ordinal);
    }

    private void ValidateBoundedRawPointerCallArguments(
        TypedFunctionSignature function,
        int receiverOffset,
        StarkParser.ArgumentListContext arguments,
        IReadOnlyList<ExpressionBinding> argumentBindings,
        string displayFunctionName,
        Scope scope)
    {
        var explicitArguments = arguments.argument();
        for (var argumentIndex = 0; argumentIndex < Math.Min(explicitArguments.Length, argumentBindings.Count); argumentIndex++)
        {
            var parameterIndex = argumentIndex + receiverOffset;
            if (parameterIndex < 0 || parameterIndex >= function.Parameters.Count)
            {
                continue;
            }

            var parameter = function.Parameters[parameterIndex];
            if (parameter.Type.Kind != StarkTypeKind.RawPointer
                || parameter.RawPointerElementCountExpression is not { Length: > 0 } countExpression)
            {
                continue;
            }

            var argument = argumentBindings[argumentIndex];
            if (!TryResolveRawPointerParameterCountRange(
                    countExpression,
                    function,
                    receiverOffset,
                    argumentBindings,
                    out var countMin,
                    out var countMax))
            {
                continue;
            }

            if (argument.Type.Kind == StarkTypeKind.Null)
            {
                if (countMin > BigInteger.Zero)
                {
                    ReportError(
                        "STK3029",
                        $"Call to '{displayFunctionName}' passes null for bounded raw pointer parameter '{parameter.Name}', but its element count is provably positive.",
                        explicitArguments[argumentIndex].expression());
                }

                continue;
            }

            if (countMax <= BigInteger.Zero)
            {
                continue;
            }

            var requestedCountExpression = TryResolveRawPointerParameterCountArgumentText(
                countExpression,
                function,
                receiverOffset,
                explicitArguments);
            if (!TryProveBoundedRawPointerArgumentStorage(
                    argument,
                    explicitArguments[argumentIndex].expression(),
                    scope,
                    requestedCountExpression,
                    countMax,
                    out var isProvablyTooShort,
                    out var reason))
            {
                if (_unsafeDepth != 0 && !isProvablyTooShort)
                {
                    continue;
                }

                ReportError(
                    "STK3029",
                    $"Call to '{displayFunctionName}' passes argument {argumentIndex + 1} to bounded raw pointer parameter '{parameter.Name}', but safe code must prove the argument is valid for {countMax.ToString(CultureInfo.InvariantCulture)} contiguous element(s). {reason}",
                    explicitArguments[argumentIndex].expression());
            }
        }
    }

    private static string? TryResolveRawPointerParameterCountArgumentText(
        string countExpression,
        TypedFunctionSignature function,
        int receiverOffset,
        IReadOnlyList<StarkParser.ArgumentContext> explicitArguments)
    {
        var normalized = NormalizeExpressionText(countExpression);
        if (BigInteger.TryParse(normalized, NumberStyles.None, CultureInfo.InvariantCulture, out _))
        {
            return normalized;
        }

        var parameterIndex = -1;
        for (var index = 0; index < function.Parameters.Count; index++)
        {
            if (string.Equals(function.Parameters[index].Name, normalized, StringComparison.Ordinal))
            {
                parameterIndex = index;
                break;
            }
        }

        var argumentIndex = parameterIndex - receiverOffset;
        return argumentIndex >= 0 && argumentIndex < explicitArguments.Count
            ? NormalizeExpressionText(explicitArguments[argumentIndex].expression().GetText())
            : null;
    }

    private static bool TryProveBoundedRawPointerArgumentStorage(
        ExpressionBinding argument,
        ParserRuleContext diagnosticContext,
        Scope scope,
        string? requestedCountExpression,
        BigInteger requestedCountMax,
        out bool isProvablyTooShort,
        out string reason)
    {
        isProvablyTooShort = false;
        reason = string.Empty;
        if (argument.MemoryRootKey is not { Length: > 0 } rootKey
            || !TryParseMemoryRootPath(rootKey, out var path)
            || !scope.TryLookup(path.BaseName, out var rootSymbol))
        {
            reason = "The argument does not have a compiler-visible storage root; use a bounded raw pointer parameter, a fixed-array element address, or wrap the assertion in an unsafe block.";
            return false;
        }

        path = ResolveMemoryRootPathIndexRanges(path, scope);
        if (TryGetFixedArrayRemainingElementCount(rootSymbol.Type, path, out var fixedArrayRemaining))
        {
            if (fixedArrayRemaining >= requestedCountMax)
            {
                return true;
            }

            isProvablyTooShort = true;
            reason = $"The fixed-array root '{path.BaseName}' only proves {fixedArrayRemaining.ToString(CultureInfo.InvariantCulture)} remaining contiguous element(s).";
            return false;
        }

        if (rootSymbol.Type.Kind == StarkTypeKind.RawPointer
            && rootSymbol.RawPointerElementCountExpression is { Length: > 0 } sourceCountExpression
            && TryProveBoundedRawPointerCountCoversRequest(
                sourceCountExpression,
                path,
                scope,
                requestedCountExpression,
                requestedCountMax))
        {
            return true;
        }

        reason = $"The storage rooted at '{path.BaseName}' is not proven to cover the requested bounded raw pointer region.";
        return false;
    }

    private static bool TryGetFixedArrayRemainingElementCount(
        StarkTypeSymbol rootType,
        MemoryRootPath path,
        out BigInteger remainingCount)
    {
        remainingCount = default;
        if (rootType.Kind != StarkTypeKind.FixedArray
            || rootType.FixedLength is not int fixedLength
            || path.Segments.Count != 1
            || path.Segments[0] is not { Kind: MemoryRootSegmentKind.Index, RangeMax: { } indexMax })
        {
            return false;
        }

        remainingCount = new BigInteger(fixedLength) - indexMax;
        if (remainingCount < BigInteger.Zero)
        {
            remainingCount = BigInteger.Zero;
        }

        return true;
    }

    private static bool TryProveBoundedRawPointerCountCoversRequest(
        string sourceCountExpression,
        MemoryRootPath path,
        Scope scope,
        string? requestedCountExpression,
        BigInteger requestedCountMax)
    {
        var normalizedSourceCount = NormalizeExpressionText(sourceCountExpression);
        if (path.Segments.Count == 0)
        {
            return string.Equals(normalizedSourceCount, requestedCountExpression, StringComparison.Ordinal)
                || TryResolveRawPointerCountExpressionRange(normalizedSourceCount, scope, out var sourceMin, out _)
                    && sourceMin >= requestedCountMax;
        }

        if (path.Segments.Count != 1
            || path.Segments[0] is not { Kind: MemoryRootSegmentKind.Index, RangeMax: { } indexMax })
        {
            return false;
        }

        if (indexMax == BigInteger.Zero
            && string.Equals(normalizedSourceCount, requestedCountExpression, StringComparison.Ordinal))
        {
            return true;
        }

        return TryResolveRawPointerCountExpressionRange(normalizedSourceCount, scope, out var rangedSourceMin, out _)
            && rangedSourceMin - indexMax >= requestedCountMax;
    }

    private static bool TryResolveRawPointerCountExpressionRange(
        string countExpression,
        Scope scope,
        out BigInteger min,
        out BigInteger max)
    {
        var normalized = NormalizeExpressionText(countExpression);
        if (BigInteger.TryParse(normalized, NumberStyles.None, CultureInfo.InvariantCulture, out var literal))
        {
            min = literal;
            max = literal;
            return true;
        }

        if (TryReadIdentifier(normalized, 0, out var identifier, out var end)
            && end == normalized.Length
            && scope.TryLookup(identifier, out var symbol)
            && symbol.Type.Kind == StarkTypeKind.Integer
            && TryGetEffectiveIntegerRange(symbol.Type, out min, out max))
        {
            return true;
        }

        min = default;
        max = default;
        return false;
    }

    private static bool TryResolveRawPointerParameterCountRange(
        string countExpression,
        TypedFunctionSignature function,
        int receiverOffset,
        IReadOnlyList<ExpressionBinding> argumentBindings,
        out BigInteger min,
        out BigInteger max)
    {
        var normalized = NormalizeExpressionText(countExpression);
        if (BigInteger.TryParse(normalized, NumberStyles.None, CultureInfo.InvariantCulture, out var literal))
        {
            min = literal;
            max = literal;
            return true;
        }

        var parameterIndex = -1;
        for (var index = 0; index < function.Parameters.Count; index++)
        {
            if (string.Equals(function.Parameters[index].Name, normalized, StringComparison.Ordinal))
            {
                parameterIndex = index;
                break;
            }
        }

        if (parameterIndex < receiverOffset)
        {
            min = default;
            max = default;
            return false;
        }

        var argumentIndex = parameterIndex - receiverOffset;
        if (argumentIndex < 0
            || argumentIndex >= argumentBindings.Count
            || !TryGetEffectiveIntegerRange(argumentBindings[argumentIndex].Type, out min, out max))
        {
            min = default;
            max = default;
            return false;
        }

        return true;
    }

    private void ValidateDisjointCallArguments(
        TypedFunctionSignature function,
        ExpressionBinding? receiver,
        int receiverOffset,
        StarkParser.ArgumentListContext arguments,
        IReadOnlyList<ExpressionBinding> argumentBindings,
        string displayFunctionName,
        Scope scope)
    {
        if (function.DisjointGroups.Count == 0 && function.SameGroups.Count == 0)
        {
            return;
        }

        var explicitArguments = arguments.argument();
        if (explicitArguments.Length == 0)
        {
            return;
        }

        var memoryArgumentsByParameterName = new Dictionary<string, DisjointMemoryArgument>(StringComparer.Ordinal);
        if (receiver is not null
            && receiverOffset == 1
            && function.Parameters.Count > 0
            && CanRuntimeDisjointTest(function.Parameters[0].Type))
        {
            memoryArgumentsByParameterName[function.Parameters[0].Name] = TryGetMemoryArgumentRoot(
                receiver,
                arguments,
                scope,
                out var receiverRoot)
                ? new DisjointMemoryArgument(arguments, receiverRoot)
                : new DisjointMemoryArgument(arguments, null);
        }

        for (var argumentIndex = 0; argumentIndex < explicitArguments.Length; argumentIndex++)
        {
            var parameterIndex = argumentIndex + receiverOffset;
            if (parameterIndex < 0
                || parameterIndex >= function.Parameters.Count
                || argumentIndex >= argumentBindings.Count)
            {
                continue;
            }

            var parameter = function.Parameters[parameterIndex];
            if (!CanRuntimeDisjointTest(parameter.Type))
            {
                continue;
            }

            var expression = explicitArguments[argumentIndex].expression();
            memoryArgumentsByParameterName[parameter.Name] =
                TryGetMemoryArgumentRoot(argumentBindings[argumentIndex], expression, scope, parameter.Type, out var root)
                || TryGetMemoryArgumentRoot(expression, parameter.Type, scope, out root)
                ? new DisjointMemoryArgument(expression, root)
                : new DisjointMemoryArgument(expression, null);
        }

        foreach (var group in function.SameGroups)
        {
            var parameterNames = group.ParameterNames
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            for (var leftIndex = 0; leftIndex < parameterNames.Length; leftIndex++)
            {
                if (!memoryArgumentsByParameterName.TryGetValue(parameterNames[leftIndex], out var left))
                {
                    continue;
                }

                for (var rightIndex = leftIndex + 1; rightIndex < parameterNames.Length; rightIndex++)
                {
                    if (!memoryArgumentsByParameterName.TryGetValue(parameterNames[rightIndex], out var right))
                    {
                        continue;
                    }

                    if (left.Root is not { } leftRoot
                        || right.Root is not { } rightRoot)
                    {
                        ReportError(
                            "STK3030",
                            $"Call to '{displayFunctionName}' violates same-memory parameter contract: parameters '{parameterNames[leftIndex]}' and '{parameterNames[rightIndex]}' require a compiler-visible same-region proof, but one or both arguments do not have a statically identifiable memory root.",
                            right.Expression);
                        continue;
                    }

                    if (MemoryCallArgumentsAreSame(leftRoot, rightRoot, scope))
                    {
                        continue;
                    }

                    ReportError(
                        "STK3030",
                        $"Call to '{displayFunctionName}' violates same-memory parameter contract: parameters '{parameterNames[leftIndex]}' and '{parameterNames[rightIndex]}' must receive the same memory region.",
                        rightRoot.Expression);
                }
            }
        }

        foreach (var group in function.DisjointGroups)
        {
            if (group.HasSubregions)
            {
                ValidateDisjointCallRegionGroup(
                    function,
                    receiverOffset,
                    arguments,
                    argumentBindings,
                    memoryArgumentsByParameterName,
                    group,
                    displayFunctionName,
                    scope);
                continue;
            }

            var parameterNames = group.ParameterNames
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            for (var leftIndex = 0; leftIndex < parameterNames.Length; leftIndex++)
            {
                if (!memoryArgumentsByParameterName.TryGetValue(parameterNames[leftIndex], out var left))
                {
                    continue;
                }

                for (var rightIndex = leftIndex + 1; rightIndex < parameterNames.Length; rightIndex++)
                {
                    if (!memoryArgumentsByParameterName.TryGetValue(parameterNames[rightIndex], out var right))
                    {
                        continue;
                    }

                    if (left.Root is not { } leftRoot
                        || right.Root is not { } rightRoot)
                    {
                        ReportError(
                            "STK3030",
                            $"Call to '{displayFunctionName}' violates disjoint parameter contract (default non-overlap): parameters '{parameterNames[leftIndex]}' and '{parameterNames[rightIndex]}' require a compiler-visible non-overlap proof, but one or both arguments do not have a statically identifiable memory root. Pass distinct storage, call an overlap-safe API, add 'where overlap(...)' to the callee, guard this call with 'if disjoint(...)', or use 'unsafe assume disjoint(...)' for trusted external facts.",
                            right.Expression);

                        continue;
                    }

                    if (!DisjointCallArgumentsMayOverlap(
                            leftRoot,
                            rightRoot,
                            scope,
                            requireProof: true,
                            out var overlapRootKey))
                    {
                        continue;
                    }

                    ReportError(
                        "STK3030",
                        $"Call to '{displayFunctionName}' violates disjoint parameter contract (default non-overlap): parameters '{parameterNames[leftIndex]}' and '{parameterNames[rightIndex]}' may receive overlapping memory rooted at '{overlapRootKey}'. Use distinct storage, call an overlap-safe API, or add 'where overlap(...)'/'where same(...)' to the callee when that aliasing is intentional.",
                        rightRoot.Expression);
                }
            }
        }
    }

    private void ValidateDisjointCallRegionGroup(
        TypedFunctionSignature function,
        int receiverOffset,
        StarkParser.ArgumentListContext arguments,
        IReadOnlyList<ExpressionBinding> argumentBindings,
        IReadOnlyDictionary<string, DisjointMemoryArgument> wholeMemoryArgumentsByParameterName,
        ParameterDisjointGroup group,
        string displayFunctionName,
        Scope scope)
    {
        var regions = group.MemoryRegions
            .Where(static region => !string.IsNullOrWhiteSpace(region.ParameterName))
            .ToArray();
        for (var leftIndex = 0; leftIndex < regions.Length; leftIndex++)
        {
            if (!TryBuildDisjointCallRegionArgument(
                    function,
                    receiverOffset,
                    arguments,
                    argumentBindings,
                    wholeMemoryArgumentsByParameterName,
                    regions[leftIndex],
                    scope,
                    out var left))
            {
                ReportError(
                    "STK3030",
                    $"Call to '{displayFunctionName}' violates disjoint subregion parameter contract: region '{regions[leftIndex].DisplayText}' requires a compiler-visible memory root and non-negative bounded range proof.",
                    TryGetDisjointRegionDiagnosticContext(function, receiverOffset, arguments, regions[leftIndex]));
                continue;
            }

            for (var rightIndex = leftIndex + 1; rightIndex < regions.Length; rightIndex++)
            {
                if (!TryBuildDisjointCallRegionArgument(
                        function,
                        receiverOffset,
                        arguments,
                        argumentBindings,
                        wholeMemoryArgumentsByParameterName,
                        regions[rightIndex],
                        scope,
                        out var right))
                {
                    ReportError(
                        "STK3030",
                        $"Call to '{displayFunctionName}' violates disjoint subregion parameter contract: region '{regions[rightIndex].DisplayText}' requires a compiler-visible memory root and non-negative bounded range proof.",
                        TryGetDisjointRegionDiagnosticContext(function, receiverOffset, arguments, regions[rightIndex]));
                    continue;
                }

                if (left.Root is not { } leftRoot || right.Root is not { } rightRoot)
                {
                    continue;
                }

                if (!DisjointCallArgumentsMayOverlap(
                        leftRoot,
                        rightRoot,
                        scope,
                        requireProof: true,
                        out var overlapRootKey))
                {
                    continue;
                }

                ReportError(
                    "STK3030",
                    $"Call to '{displayFunctionName}' violates disjoint subregion parameter contract: regions '{regions[leftIndex].DisplayText}' and '{regions[rightIndex].DisplayText}' may overlap at '{overlapRootKey}'.",
                    rightRoot.Expression);
            }
        }
    }

    private bool TryBuildDisjointCallRegionArgument(
        TypedFunctionSignature function,
        int receiverOffset,
        StarkParser.ArgumentListContext arguments,
        IReadOnlyList<ExpressionBinding> argumentBindings,
        IReadOnlyDictionary<string, DisjointMemoryArgument> wholeMemoryArgumentsByParameterName,
        ParameterMemoryRegion region,
        Scope scope,
        out DisjointMemoryArgument argument)
    {
        argument = null!;
        if (!wholeMemoryArgumentsByParameterName.TryGetValue(region.ParameterName, out var wholeArgument)
            || wholeArgument.Root is not { } wholeRoot)
        {
            return false;
        }

        if (region.IsWholeParameter)
        {
            argument = wholeArgument;
            return true;
        }

        if (region.StartExpression is not { Length: > 0 } startExpression
            || region.CountExpression is not { Length: > 0 } countExpression
            || !TryResolveParameterRegionExpressionRange(startExpression, function, receiverOffset, argumentBindings, out var startMin, out var startMax)
            || !TryResolveParameterRegionExpressionRange(countExpression, function, receiverOffset, argumentBindings, out _, out var countMax)
            || countMax <= BigInteger.Zero
            || !TryBuildMemoryRootRangeKey(wholeRoot.RootKey, startMin, startMax, countMax, out var regionRootKey)
            || !TryCreateMemoryArgumentRoot(
                regionRootKey,
                wholeRoot.Expression,
                wholeRoot.ArgumentType,
                wholeRoot.WasAddressOf,
                wholeRoot.HasProvenIndependentStorage,
                scope,
                out var regionRoot))
        {
            return false;
        }

        argument = new DisjointMemoryArgument(wholeArgument.Expression, regionRoot);
        return true;
    }

    private static ParserRuleContext TryGetDisjointRegionDiagnosticContext(
        TypedFunctionSignature function,
        int receiverOffset,
        StarkParser.ArgumentListContext arguments,
        ParameterMemoryRegion region)
    {
        for (var parameterIndex = 0; parameterIndex < function.Parameters.Count; parameterIndex++)
        {
            if (!string.Equals(function.Parameters[parameterIndex].Name, region.ParameterName, StringComparison.Ordinal))
            {
                continue;
            }

            var argumentIndex = parameterIndex - receiverOffset;
            if (argumentIndex >= 0 && argumentIndex < arguments.argument().Length)
            {
                return arguments.argument(argumentIndex).expression();
            }
        }

        return arguments;
    }

    private void ValidateThreadSafetyLawCallPredicates(
        TypedFunctionSignature function,
        string functionName,
        ParserRuleContext context)
    {
        foreach (var predicate in function.ThreadSafetyLaws)
        {
            var predicateType = EnsureMonomorphizedType(predicate.Type, Location(context));
            if (TypeContainsOpenCurrentFunctionGenericParameter(predicateType)
                || TypeContainsOpenCurrentFunctionComptimeParameter(predicateType))
            {
                if (CurrentFunctionCarriesThreadSafetyLawPredicate(predicate.LawName, predicateType))
                {
                    continue;
                }

                ReportError(
                    "STK3049",
                    FormatOpenThreadSafetyLawFailure(functionName, predicate.LawName, predicateType),
                    context);
                continue;
            }

            var fact = GetThreadSafetyLawEvaluator().Evaluate(predicate.LawName, predicateType);
            if (fact.Holds)
            {
                continue;
            }

            ReportError(
                "STK3049",
                FormatThreadSafetyLawFailure(functionName, predicate.LawName, predicateType, fact),
                context);
        }
    }

    private bool CurrentFunctionCarriesThreadSafetyLawPredicate(string lawName, StarkTypeSymbol type)
    {
        return _currentFunctionThreadSafetyLaws.Any(predicate =>
            string.Equals(predicate.LawName, lawName, StringComparison.Ordinal)
            && StarkTypeSymbolsHaveSameIdentity(predicate.Type, type));
    }

    private ThreadSafetyLawEvaluator GetThreadSafetyLawEvaluator()
    {
        return _threadSafetyLawEvaluator ??= new ThreadSafetyLawEvaluator(
            _namedTypes,
            _syntaxModel.ModuleName,
            (code, message) => ReportError(code, message, SourceLocation.Synthetic()));
    }

    private static string FormatThreadSafetyLawFailure(
        string functionName,
        string lawName,
        StarkTypeSymbol type,
        ThreadSafetyLawFact fact)
    {
        var failure = fact.FailureReasons.FirstOrDefault();
        var reason = failure?.Message ?? $"Type '{type.DisplayName}' does not satisfy {lawName}.";
        var fieldChain = failure?.Path is { Count: > 0 } path
            ? $" Responsible field chain: {type.DisplayName}.{string.Join(".", path)}."
            : string.Empty;
        return $"Function '{functionName}' requires where {lawName}({type.DisplayName}), but the type does not satisfy that law. {reason}{fieldChain}";
    }

    private string FormatOpenThreadSafetyLawFailure(
        string functionName,
        string lawName,
        StarkTypeSymbol type)
    {
        var enclosing = _currentFunctionName is { Length: > 0 } name
            ? $" Enclosing generic function '{name}' must declare `where {lawName}({type.DisplayName})` or avoid this call for unconstrained '{type.DisplayName}'."
            : string.Empty;
        return $"Function '{functionName}' requires where {lawName}({type.DisplayName}), but that open generic law predicate is not available at this call site.{enclosing}";
    }

    private static bool TryResolveParameterRegionExpressionRange(
        string expressionText,
        TypedFunctionSignature function,
        int receiverOffset,
        IReadOnlyList<ExpressionBinding> argumentBindings,
        out BigInteger min,
        out BigInteger max)
    {
        return TryResolveRawPointerParameterCountRange(
            expressionText,
            function,
            receiverOffset,
            argumentBindings,
            out min,
            out max);
    }

    private static bool MemoryCallArgumentsAreSame(MemoryArgumentRoot left, MemoryArgumentRoot right, Scope scope)
    {
        foreach (var leftRootKey in GetDisjointQueryRootKeys(left))
        {
            foreach (var rightRootKey in GetDisjointQueryRootKeys(right))
            {
                if (string.Equals(leftRootKey, rightRootKey, StringComparison.Ordinal)
                    || scope.HasSameFact(leftRootKey, rightRootKey))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static void AddParameterDisjointFacts(Scope scope, IReadOnlyList<ParameterDisjointGroup> disjointGroups)
    {
        foreach (var group in disjointGroups)
        {
            if (group.HasSubregions)
            {
                var rootKeys = group.MemoryRegions
                    .Select(region => TryBuildParameterMemoryRegionRootKey(region, scope, out var rootKey) ? rootKey : null)
                    .Where(static rootKey => !string.IsNullOrWhiteSpace(rootKey))
                    .Select(static rootKey => rootKey!)
                    .ToArray();
                scope.AddDisjointFact(rootKeys);
                continue;
            }

            scope.AddDisjointFact(group.ParameterNames);
        }
    }

    private static void AddParameterSameFacts(Scope scope, IReadOnlyList<ParameterSameGroup> sameGroups)
    {
        foreach (var group in sameGroups)
        {
            scope.AddSameFact(group.ParameterNames);
        }
    }

    private static bool TryBuildParameterMemoryRegionRootKey(
        ParameterMemoryRegion region,
        Scope scope,
        out string rootKey)
    {
        rootKey = string.Empty;
        if (!scope.TryLookup(region.ParameterName, out var symbol))
        {
            return false;
        }

        var baseRootKey = symbol.MemoryRootKey ?? region.ParameterName;
        if (region.IsWholeParameter)
        {
            rootKey = baseRootKey;
            return true;
        }

        if (region.StartExpression is not { Length: > 0 } startExpression
            || region.CountExpression is not { Length: > 0 } countExpression
            || !TryBuildMemoryRootRangeKey(baseRootKey, startExpression, countExpression, scope, out rootKey))
        {
            return false;
        }

        return true;
    }

    private static bool TryGetObviousMemoryArgumentRootKey(
        StarkParser.ExpressionContext expression,
        out string rootKey)
    {
        rootKey = NormalizeExpressionText(expression.GetText());
        while (rootKey.Length > 1 && rootKey[0] == '&')
        {
            rootKey = NormalizeExpressionText(rootKey[1..]);
        }

        return IsSimpleMemoryRootText(rootKey);
    }

    private static bool TryGetMemoryArgumentRoot(
        StarkParser.ExpressionContext expression,
        StarkTypeSymbol argumentType,
        Scope scope,
        out MemoryArgumentRoot root)
    {
        root = default;
        if (!TryGetObviousMemoryArgumentRootKey(expression, out var rootKey))
        {
            return false;
        }

        var normalizedExpressionText = NormalizeExpressionText(expression.GetText());
        var wasAddressOf = normalizedExpressionText.Length > 1 && normalizedExpressionText[0] == '&';
        return TryCreateMemoryArgumentRoot(
            rootKey,
            expression,
            argumentType,
            wasAddressOf,
            hasProvenIndependentStorage: false,
            scope,
            out root);
    }

    private static bool TryGetMemoryArgumentRoot(
        ExpressionBinding binding,
        ParserRuleContext diagnosticContext,
        Scope scope,
        out MemoryArgumentRoot root)
    {
        return TryGetMemoryArgumentRoot(binding, diagnosticContext, scope, binding.Type, out root);
    }

    private static bool TryGetMemoryArgumentRoot(
        ExpressionBinding binding,
        ParserRuleContext diagnosticContext,
        Scope scope,
        StarkTypeSymbol proofType,
        out MemoryArgumentRoot root)
    {
        root = default;
        if (binding.MemoryRootKey is not { Length: > 0 } rootKey)
        {
            return false;
        }

        return TryCreateMemoryArgumentRoot(
            rootKey,
            diagnosticContext,
            proofType,
            wasAddressOf: false,
            binding.MemoryRootIsIndependentStorage,
            scope,
            out root);
    }

    private static bool TryCreateMemoryArgumentRoot(
        string rootKey,
        ParserRuleContext diagnosticContext,
        StarkTypeSymbol argumentType,
        bool wasAddressOf,
        bool hasProvenIndependentStorage,
        Scope scope,
        out MemoryArgumentRoot root)
    {
        root = default;
        var baseName = TryParseMemoryRootPath(rootKey, out var parsedPath)
            ? parsedPath.BaseName
            : TryReadIdentifier(rootKey, 0, out var parsedBaseName, out _)
                ? parsedBaseName
                : string.Empty;
        if (baseName.Length == 0)
        {
            return false;
        }

        var aliasRootKeys = new List<string>();
        if (diagnosticContext is StarkParser.ExpressionContext expression
            && TryGetObviousMemoryArgumentRootKey(expression, out var expressionRootKey)
            && !string.Equals(expressionRootKey, rootKey, StringComparison.Ordinal))
        {
            aliasRootKeys.Add(expressionRootKey);
        }

        root = new MemoryArgumentRoot(
            rootKey,
            baseName,
            diagnosticContext,
            argumentType,
            wasAddressOf,
            scope.TryLookup(baseName, out _),
            hasProvenIndependentStorage,
            ResolveMemoryRootPathIndexRanges(
                string.IsNullOrEmpty(parsedPath.BaseName)
                    ? new MemoryRootPath(baseName, [])
                    : parsedPath,
                scope),
            aliasRootKeys.Count == 0 ? null : aliasRootKeys);
        return true;
    }

    private static bool DisjointCallArgumentsMayOverlap(
        MemoryArgumentRoot left,
        MemoryArgumentRoot right,
        Scope scope,
        bool requireProof,
        out string overlapRootKey)
    {
        if (HasDisjointFact(scope, left, right))
        {
            overlapRootKey = string.Empty;
            return false;
        }

        if (MemoryArgumentRootsAreProvenDisjoint(left, right))
        {
            overlapRootKey = string.Empty;
            return false;
        }

        if (ObviousMemoryArgumentRootsMayOverlap(left.RootKey, right.RootKey, out overlapRootKey))
        {
            return true;
        }

        if (HaveProvenIndependentStorageRoots(left, right))
        {
            overlapRootKey = string.Empty;
            return false;
        }

        if (!requireProof)
        {
            overlapRootKey = string.Empty;
            return false;
        }

        overlapRootKey = $"{left.RootKey} or {right.RootKey}";
        return true;
    }

    private static bool HasDisjointFact(Scope scope, MemoryArgumentRoot left, MemoryArgumentRoot right)
    {
        foreach (var leftRootKey in GetDisjointQueryRootKeys(left))
        {
            foreach (var rightRootKey in GetDisjointQueryRootKeys(right))
            {
                if (scope.HasDisjointFact(leftRootKey, rightRootKey))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static IEnumerable<string> GetDisjointQueryRootKeys(MemoryArgumentRoot root)
    {
        yield return root.RootKey;
        if (root.AliasRootKeys is null)
        {
            yield break;
        }

        foreach (var aliasRootKey in root.AliasRootKeys)
        {
            yield return aliasRootKey;
        }
    }

    private static bool HaveProvenIndependentStorageRoots(MemoryArgumentRoot left, MemoryArgumentRoot right)
    {
        return !string.Equals(left.BaseName, right.BaseName, StringComparison.Ordinal)
            && (HasProvenIndependentStorageRoot(left) || HasProvenIndependentStorageRoot(right));
    }

    private static bool HasProvenIndependentStorageRoot(MemoryArgumentRoot root)
    {
        if (root.HasProvenIndependentStorage)
        {
            return true;
        }

        if (!root.BaseHasNamedStorage)
        {
            return false;
        }

        if (root.WasAddressOf)
        {
            return true;
        }

        if (root.ArgumentType.InitializationKind != StarkInitializationKind.None)
        {
            return true;
        }

        if (root.ArgumentType.BorrowKind != StarkBorrowKind.None)
        {
            return root.ArgumentType.IsMutableView;
        }

        return root.ArgumentType.Kind is StarkTypeKind.Named or StarkTypeKind.FixedArray;
    }

    private static bool ObviousMemoryArgumentRootsMayOverlap(
        string leftRootKey,
        string rightRootKey,
        out string overlapRootKey)
    {
        if (string.Equals(leftRootKey, "null", StringComparison.Ordinal)
            || string.Equals(rightRootKey, "null", StringComparison.Ordinal))
        {
            overlapRootKey = string.Empty;
            return false;
        }

        if (TryParseMemoryRootPath(leftRootKey, out var leftPath)
            && TryParseMemoryRootPath(rightRootKey, out var rightPath))
        {
            return MemoryRootPathsMayOverlap(leftPath, rightPath, out overlapRootKey);
        }

        if (string.Equals(leftRootKey, rightRootKey, StringComparison.Ordinal))
        {
            overlapRootKey = leftRootKey;
            return true;
        }

        if (IsMemoryRootAncestor(leftRootKey, rightRootKey))
        {
            overlapRootKey = leftRootKey;
            return true;
        }

        if (IsMemoryRootAncestor(rightRootKey, leftRootKey))
        {
            overlapRootKey = rightRootKey;
            return true;
        }

        overlapRootKey = string.Empty;
        return false;
    }

    private static bool MemoryArgumentRootsAreProvenDisjoint(
        MemoryArgumentRoot left,
        MemoryArgumentRoot right)
    {
        return string.Equals(left.Path.BaseName, right.Path.BaseName, StringComparison.Ordinal)
            && !MemoryRootPathsMayOverlap(left.Path, right.Path, out _);
    }

    private static bool MemoryRootPathsMayOverlap(
        MemoryRootPath left,
        MemoryRootPath right,
        out string overlapRootKey)
    {
        if (!string.Equals(left.BaseName, right.BaseName, StringComparison.Ordinal))
        {
            overlapRootKey = string.Empty;
            return false;
        }

        var sharedSegmentCount = Math.Min(left.Segments.Count, right.Segments.Count);
        for (var index = 0; index < sharedSegmentCount; index++)
        {
            var leftSegment = left.Segments[index];
            var rightSegment = right.Segments[index];
            if (leftSegment.Equals(rightSegment))
            {
                continue;
            }

            if (leftSegment.Kind == MemoryRootSegmentKind.Field
                && rightSegment.Kind == MemoryRootSegmentKind.Field)
            {
                overlapRootKey = string.Empty;
                return false;
            }

            if (leftSegment.Kind == MemoryRootSegmentKind.Index
                && rightSegment.Kind == MemoryRootSegmentKind.Index
                && MemoryRootIndexRangesAreDisjoint(leftSegment, rightSegment))
            {
                overlapRootKey = string.Empty;
                return false;
            }

            overlapRootKey = BuildMemoryRootPrefix(left, index);
            return true;
        }

        overlapRootKey = BuildMemoryRootPrefix(left, sharedSegmentCount);
        return true;
    }

    private static bool IsMemoryRootAncestor(string ancestorRootKey, string descendantRootKey)
    {
        return descendantRootKey.Length > ancestorRootKey.Length
            && descendantRootKey.StartsWith(ancestorRootKey, StringComparison.Ordinal)
            && descendantRootKey[ancestorRootKey.Length] is '.' or '[';
    }

    private static bool IsSameOrDescendantMemoryRoot(string candidateRootKey, string ancestorRootKey)
    {
        return string.Equals(candidateRootKey, ancestorRootKey, StringComparison.Ordinal)
            || IsMemoryRootAncestor(ancestorRootKey, candidateRootKey);
    }

    private static bool TryParseMemoryRootPath(string rootKey, out MemoryRootPath path)
    {
        path = default;
        if (!TryReadIdentifier(rootKey, 0, out var baseName, out var position))
        {
            return false;
        }

        var segments = new List<MemoryRootSegment>();
        while (position < rootKey.Length)
        {
            if (rootKey[position] == '.')
            {
                if (!TryReadIdentifier(rootKey, position + 1, out var fieldName, out position))
                {
                    return false;
                }

                segments.Add(new MemoryRootSegment(MemoryRootSegmentKind.Field, fieldName));
                continue;
            }

            if (rootKey[position] == '[')
            {
                var closeBracket = rootKey.IndexOf(']', position + 1);
                if (closeBracket <= position + 1)
                {
                    return false;
                }

                var indexText = rootKey[(position + 1)..closeBracket];
                if (indexText.Contains('[', StringComparison.Ordinal)
                    || indexText.Contains(']', StringComparison.Ordinal))
                {
                    return false;
                }

                if (TryParseNonNegativeInteger(indexText, out var literalIndex))
                {
                    segments.Add(new MemoryRootSegment(
                        MemoryRootSegmentKind.Index,
                        indexText,
                        literalIndex,
                        literalIndex));
                }
                else if (TryParseMemoryRootIndexRangeText(indexText, out var rangeMin, out var rangeMax))
                {
                    segments.Add(new MemoryRootSegment(
                        MemoryRootSegmentKind.Index,
                        indexText,
                        rangeMin,
                        rangeMax));
                }
                else
                {
                    segments.Add(new MemoryRootSegment(MemoryRootSegmentKind.Index, indexText));
                }

                position = closeBracket + 1;
                continue;
            }

            return false;
        }

        path = new MemoryRootPath(baseName, segments);
        return true;
    }

    private static MemoryRootPath ResolveMemoryRootPathIndexRanges(MemoryRootPath path, Scope scope)
    {
        if (path.Segments.Count == 0)
        {
            return path;
        }

        var segments = new List<MemoryRootSegment>(path.Segments.Count);
        foreach (var segment in path.Segments)
        {
            if (segment.Kind == MemoryRootSegmentKind.Index
                && segment.RangeMin is null
                && TryResolveMemoryRootIndexRange(segment.Text, scope, out var rangeMin, out var rangeMax))
            {
                segments.Add(segment with { RangeMin = rangeMin, RangeMax = rangeMax });
                continue;
            }

            segments.Add(segment);
        }

        return new MemoryRootPath(path.BaseName, segments);
    }

    private static bool TryResolveMemoryRootIndexRange(
        string text,
        Scope scope,
        out BigInteger rangeMin,
        out BigInteger rangeMax)
    {
        if (TryParseNonNegativeInteger(text, out var literalIndex))
        {
            rangeMin = literalIndex;
            rangeMax = literalIndex;
            return true;
        }

        if (TryReadIdentifier(text, 0, out var identifier, out var end)
            && end == text.Length
            && scope.TryLookup(identifier, out var symbol)
            && symbol.Type.Kind == StarkTypeKind.Integer
            && symbol.Type.RangeMin is { } symbolRangeMin
            && symbol.Type.RangeMax is { } symbolRangeMax)
        {
            rangeMin = symbolRangeMin;
            rangeMax = symbolRangeMax;
            return true;
        }

        rangeMin = BigInteger.Zero;
        rangeMax = BigInteger.Zero;
        return false;
    }

    private static bool MemoryRootIndexRangesAreDisjoint(
        MemoryRootSegment left,
        MemoryRootSegment right)
    {
        return left.RangeMin is { } leftMin
            && left.RangeMax is { } leftMax
            && right.RangeMin is { } rightMin
            && right.RangeMax is { } rightMax
            && (leftMax < rightMin || rightMax < leftMin);
    }

    private static bool TryReadIdentifier(
        string text,
        int start,
        out string identifier,
        out int end)
    {
        identifier = string.Empty;
        end = start;
        if (start >= text.Length || !(char.IsLetter(text[start]) || text[start] == '_'))
        {
            return false;
        }

        end = start + 1;
        while (end < text.Length && (char.IsLetterOrDigit(text[end]) || text[end] == '_'))
        {
            end++;
        }

        identifier = text[start..end];
        return true;
    }

    private static bool TryParseNonNegativeInteger(string text, out BigInteger value)
    {
        return BigInteger.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryParseMemoryRootIndexRangeText(
        string text,
        out BigInteger rangeMin,
        out BigInteger rangeMax)
    {
        rangeMin = BigInteger.Zero;
        rangeMax = BigInteger.Zero;
        var separator = text.IndexOf("..", StringComparison.Ordinal);
        if (separator <= 0 || separator >= text.Length - 2)
        {
            return false;
        }

        return BigInteger.TryParse(text[..separator], NumberStyles.None, CultureInfo.InvariantCulture, out rangeMin)
            && BigInteger.TryParse(text[(separator + 2)..], NumberStyles.None, CultureInfo.InvariantCulture, out rangeMax)
            && rangeMin >= BigInteger.Zero
            && rangeMax >= rangeMin;
    }

    private static string BuildMemoryRootPrefix(MemoryRootPath path, int segmentCount)
    {
        var builder = new System.Text.StringBuilder(path.BaseName);
        for (var index = 0; index < segmentCount && index < path.Segments.Count; index++)
        {
            var segment = path.Segments[index];
            if (segment.Kind == MemoryRootSegmentKind.Field)
            {
                builder.Append('.');
                builder.Append(segment.Text);
            }
            else
            {
                builder.Append('[');
                builder.Append(segment.Text);
                builder.Append(']');
            }
        }

        return builder.ToString();
    }

    private static string BuildMemoryRootSuffix(MemoryRootPath path, int startSegmentIndex)
    {
        if (startSegmentIndex >= path.Segments.Count)
        {
            return string.Empty;
        }

        var builder = new System.Text.StringBuilder();
        for (var index = Math.Max(0, startSegmentIndex); index < path.Segments.Count; index++)
        {
            var segment = path.Segments[index];
            if (segment.Kind == MemoryRootSegmentKind.Field)
            {
                builder.Append('.');
                builder.Append(segment.Text);
            }
            else
            {
                builder.Append('[');
                builder.Append(segment.Text);
                builder.Append(']');
            }
        }

        return builder.ToString();
    }

    private readonly record struct MemoryRootPath(
        string BaseName,
        IReadOnlyList<MemoryRootSegment> Segments);

    private readonly record struct MemoryRootSegment(
        MemoryRootSegmentKind Kind,
        string Text,
        BigInteger? RangeMin = null,
        BigInteger? RangeMax = null);

    private readonly record struct MemoryArgumentRoot(
        string RootKey,
        string BaseName,
        ParserRuleContext Expression,
        StarkTypeSymbol ArgumentType,
        bool WasAddressOf,
        bool BaseHasNamedStorage,
        bool HasProvenIndependentStorage,
        MemoryRootPath Path,
        IReadOnlyList<string>? AliasRootKeys = null);

    private readonly record struct IndependentLoopMemoryAccess(
        string RootKey,
        string DisplayName,
        bool IsWrite,
        ParserRuleContext Expression);

    private sealed record DisjointMemoryArgument(
        ParserRuleContext Expression,
        MemoryArgumentRoot? Root);

    private enum MemoryRootSegmentKind
    {
        Field,
        Index
    }

    private static string NormalizeExpressionText(string text)
    {
        while (text.Length >= 2 && text[0] == '(' && text[^1] == ')' && HasSingleOuterParentheses(text))
        {
            text = text[1..^1];
        }

        return text;
    }

    private static bool TryGetSimpleParameterExpression(StarkParser.ExpressionContext expression, out string name)
    {
        name = string.Empty;
        if (TryGetSimplePostfixExpression(expression) is not { } postfix
            || postfix.postfixPart().Length != 0
            || postfix.primaryExpression().Identifier()?.GetText() is not { } identifier)
        {
            return false;
        }

        name = identifier;
        return true;
    }

    private static string? MapFunctionPointerRawPointerElementCountExpressionToParameterNames(
        string? expression,
        IReadOnlyList<string> parameterNames)
    {
        if (string.IsNullOrWhiteSpace(expression)
            || !expression.StartsWith("arg", StringComparison.Ordinal)
            || !int.TryParse(expression[3..], out var parameterIndex)
            || parameterIndex < 0
            || parameterIndex >= parameterNames.Count)
        {
            return expression;
        }

        return parameterNames[parameterIndex];
    }

    private static bool TryGetRawPointerRegionExpression(
        StarkParser.ExpressionContext expression,
        out string rootName,
        out StarkParser.ExpressionContext startExpression,
        out StarkParser.ExpressionContext lengthExpression)
    {
        rootName = string.Empty;
        startExpression = null!;
        lengthExpression = null!;

        if (TryGetSimplePostfixExpression(expression) is not { } postfix
            || postfix.primaryExpression().Identifier()?.GetText() is not { } identifier
            || postfix.postfixPart() is not [var indexPart]
            || indexPart.LBRACK() is null
            || indexPart.expressionList()?.expression() is not [var start, var length])
        {
            return false;
        }

        rootName = identifier;
        startExpression = start;
        lengthExpression = length;
        return true;
    }

    private static StarkParser.ExpressionListContext? TryGetRawPointerRegionExpressionList(StarkParser.ExpressionContext expression)
    {
        if (TryGetSimplePostfixExpression(expression) is not { } postfix
            || postfix.postfixPart() is not [var indexPart])
        {
            return null;
        }

        return indexPart.expressionList();
    }

    private static StarkParser.PostfixExpressionContext? TryGetSimplePostfixExpression(StarkParser.ExpressionContext expression)
    {
        return TryGetSimpleUnaryExpression(expression) is { } unary
            ? TryGetSimplePostfixExpression(unary)
            : null;
    }

    private static StarkParser.UnaryExpressionContext? TryGetSimpleUnaryExpression(StarkParser.ExpressionContext expression)
    {
        var assignment = expression.assignmentExpression();
        if (assignment.assignmentOperator() is not null || assignment.conditionalExpression() is not { } conditional)
        {
            return null;
        }

        if (conditional.expression().Length != 0)
        {
            return null;
        }

        var logicalOr = conditional.logicalOrExpression();
        if (logicalOr.logicalAndExpression().Length != 1)
        {
            return null;
        }

        var logicalAnd = logicalOr.logicalAndExpression(0);
        if (logicalAnd.bitwiseOrExpression().Length != 1)
        {
            return null;
        }

        var bitwiseOr = logicalAnd.bitwiseOrExpression(0);
        if (bitwiseOr.bitwiseXorExpression().Length != 1)
        {
            return null;
        }

        var bitwiseXor = bitwiseOr.bitwiseXorExpression(0);
        if (bitwiseXor.bitwiseAndExpression().Length != 1)
        {
            return null;
        }

        var bitwiseAnd = bitwiseXor.bitwiseAndExpression(0);
        if (bitwiseAnd.equalityExpression().Length != 1)
        {
            return null;
        }

        var equality = bitwiseAnd.equalityExpression(0);
        if (equality.relationalExpression().Length != 1)
        {
            return null;
        }

        var relational = equality.relationalExpression(0);
        if (relational.shiftExpression().Length != 1)
        {
            return null;
        }

        var shift = relational.shiftExpression(0);
        if (shift.additiveExpression().Length != 1)
        {
            return null;
        }

        var additive = shift.additiveExpression(0);
        if (additive.multiplicativeExpression().Length != 1)
        {
            return null;
        }

        var multiplicative = additive.multiplicativeExpression(0);
        if (multiplicative.unaryExpression().Length != 1)
        {
            return null;
        }

        return multiplicative.unaryExpression(0);
    }

    private static StarkParser.PostfixExpressionContext? TryGetSimplePostfixExpression(StarkParser.UnaryExpressionContext expression)
    {
        if (expression.powerExpression() is not { } powerExpression
            || powerExpression.unaryExpression() is not null)
        {
            return null;
        }

        return powerExpression.postfixExpression();
    }

    private static bool TryGetAddressOfIndexedPostfixExpression(
        StarkParser.UnaryExpressionContext expression,
        out StarkParser.PostfixExpressionContext postfix)
    {
        postfix = null!;
        if (expression.unaryOperator()?.AND() is not null
            && expression.unaryExpression() is { } indexedExpression
            && TryGetSimplePostfixExpression(indexedExpression) is { } indexedPostfix)
        {
            postfix = indexedPostfix;
            return true;
        }

        if (expression.powerExpression()?.postfixExpression() is { } parenthesizedPostfix
            && parenthesizedPostfix.postfixPart().Length == 0
            && parenthesizedPostfix.primaryExpression().expression() is { } parenthesizedExpression
            && TryGetSimpleUnaryExpression(parenthesizedExpression) is { } parenthesizedUnary)
        {
            return TryGetAddressOfIndexedPostfixExpression(parenthesizedUnary, out postfix);
        }

        return false;
    }

    private static string? AppendMemoryRootIndexKey(string rootKey, StarkParser.ExpressionContext indexExpression)
    {
        var indexText = NormalizeExpressionText(indexExpression.GetText());
        return IsSimpleMemoryRootIndexText(indexText)
            ? $"{rootKey}[{indexText}]"
            : null;
    }

    private static string? AppendMemoryRootTextRangeKey(
        string rootKey,
        StarkParser.ExpressionContext startExpression,
        StarkParser.ExpressionContext lengthExpression,
        Scope scope)
    {
        return TryBuildMemoryRootRangeKey(
            rootKey,
            NormalizeExpressionText(startExpression.GetText()),
            NormalizeExpressionText(lengthExpression.GetText()),
            scope,
            out var rangeRootKey)
                ? rangeRootKey
                : null;
    }

    private static bool TryBuildMemoryRootRangeKey(
        string rootKey,
        string startText,
        string lengthText,
        Scope scope,
        out string rangeRootKey)
    {
        rangeRootKey = string.Empty;
        if (!TryResolveMemoryRootIndexRange(startText, scope, out var startMin, out var startMax)
            || !TryResolveMemoryRootIndexRange(lengthText, scope, out _, out var lengthMax)
            || startMin < BigInteger.Zero
            || lengthMax <= BigInteger.Zero)
        {
            return false;
        }

        return TryBuildMemoryRootRangeKey(rootKey, startMin, startMax, lengthMax, out rangeRootKey);
    }

    private static bool TryBuildMemoryRootRangeKey(
        string rootKey,
        BigInteger startMin,
        BigInteger startMax,
        BigInteger lengthMax,
        out string rangeRootKey)
    {
        rangeRootKey = string.Empty;
        var rangeMax = startMax + lengthMax - BigInteger.One;
        if (rangeMax < startMin)
        {
            return false;
        }

        if (TryParseMemoryRootPath(rootKey, out var path)
            && path.Segments.Count > 0
            && path.Segments[^1] is { Kind: MemoryRootSegmentKind.Index, RangeMin: { } baseMin, RangeMax: { } baseMax })
        {
            var prefix = BuildMemoryRootPrefix(path, path.Segments.Count - 1);
            startMin += baseMin;
            rangeMax += baseMax;
            rangeRootKey = $"{prefix}[{startMin.ToString(CultureInfo.InvariantCulture)}..{rangeMax.ToString(CultureInfo.InvariantCulture)}]";
            return true;
        }

        rangeRootKey = $"{rootKey}[{startMin.ToString(CultureInfo.InvariantCulture)}..{rangeMax.ToString(CultureInfo.InvariantCulture)}]";
        return true;
    }

    private static bool IsSimpleMemoryRootIndexText(string text)
    {
        return !string.IsNullOrWhiteSpace(text)
            && text.All(static character => char.IsLetterOrDigit(character) || character is '_' or '.');
    }

    private static bool IsSimpleMemoryRootText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)
            || !(char.IsLetter(text[0]) || text[0] == '_'))
        {
            return false;
        }

        return text.All(static character =>
            char.IsLetterOrDigit(character)
            || character is '_' or '.' or '[' or ']');
    }

    private static bool HasSingleOuterParentheses(string text)
    {
        var depth = 0;
        for (var index = 0; index < text.Length; index++)
        {
            switch (text[index])
            {
                case '(':
                    depth++;
                    break;
                case ')':
                    depth--;
                    if (depth == 0 && index < text.Length - 1)
                    {
                        return false;
                    }

                    break;
            }
        }

        return depth == 0;
    }

    private static bool IsCVarargsStableArgumentType(StarkTypeSymbol type)
    {
        type = StarkTypeSymbols.WithQualifiers(
            type,
            borrowKind: StarkBorrowKind.None,
            accessKind: StarkAccessKind.None,
            initializationKind: StarkInitializationKind.None,
            isMutableView: false);
        return type.Kind switch
        {
            StarkTypeKind.Integer => type.BitWidth >= 32,
            StarkTypeKind.Float => type.BitWidth == 64,
            StarkTypeKind.RawPointer => true,
            StarkTypeKind.Ascii or StarkTypeKind.Unicode => true,
            _ => false
        };
    }

    private void ValidateCVarargsFormatStringArguments(
        TypedFunctionSignature function,
        int receiverOffset,
        int explicitParameterCount,
        StarkParser.ArgumentListContext arguments,
        IReadOnlyList<ExpressionBinding> argumentBindings,
        Scope scope)
    {
        if (explicitParameterCount == 0
            || argumentBindings.Count == 0
            || receiverOffset >= function.Parameters.Count
            || !IsCVarargsFormatParameter(function.Parameters[receiverOffset])
            || !TryDecodeCVarargsFormatString(arguments.argument(0).expression(), argumentBindings[0], scope, out var format))
        {
            return;
        }

        var consumedExtraArguments = 0;
        foreach (var expectation in EnumeratePrintfArgumentExpectations(format))
        {
            var argumentIndex = explicitParameterCount + consumedExtraArguments;
            consumedExtraArguments++;

            if (!expectation.RequiresCString)
            {
                continue;
            }

            if (argumentIndex >= argumentBindings.Count)
            {
                ReportError(
                    "STK3009",
                    $"Format string for '{function.DisplaySourceName}' expects a C string argument for '%s' at argument {argumentIndex + 1}, but the call provides only {argumentBindings.Count} argument(s).",
                    arguments);
                continue;
            }

            var argument = argumentBindings[argumentIndex];
            if (IsCCharRawPointerType(argument.Type))
            {
                continue;
            }

            ReportError(
                "STK3009",
                $"C varargs '%s' argument {argumentIndex + 1} to '{function.DisplaySourceName}' must be rawptr<System.C.c_char> or rawmutptr<System.C.c_char>, but found '{argument.Type.DisplayName}'. Convert Stark text with System.C.FromAscii(...) and pass OwnedCStr.Data().",
                arguments.argument(argumentIndex).expression());
        }
    }

    private bool TryDecodeCVarargsFormatString(
        StarkParser.ExpressionContext expression,
        ExpressionBinding binding,
        Scope scope,
        out string format)
    {
        if (binding.TextLiteral is { } literal
            && binding.TextLiteralKind is { } literalKind
            && TextLiteralDecoder.TryDecode(literal, literalKind, out var decoded, out _))
        {
            format = decoded.Value;
            return true;
        }

        CompileTimeEvaluator.ClearFailure();
        if (CompileTimeEvaluator.TryEvaluateExpressionNode(
                expression,
                CurrentFunctionModuleName,
                state: null,
                activeCalls: null,
                out var constant,
                (string name, out CompileTimeConstant value) => TryResolveCompileTimeConstant(scope, name, out value))
            && constant.Kind == CompileTimeConstantKind.Text
            && constant.TextLiteral is { } constantLiteral
            && TextLiteralDecoder.TryDecode(
                constantLiteral,
                constantLiteral.StartsWith('\'') ? TextLiteralKind.Character : TextLiteralKind.String,
                out var decodedConstant,
                out _))
        {
            format = decodedConstant.Value;
            return true;
        }

        format = string.Empty;
        return false;
    }

    private static bool IsCVarargsFormatParameter(TypedParameterSymbol parameter)
    {
        return (string.Equals(parameter.Name, "format", StringComparison.Ordinal)
                || string.Equals(parameter.Name, "fmt", StringComparison.Ordinal))
            && (parameter.Type.Kind is StarkTypeKind.Ascii or StarkTypeKind.Unicode
                || IsCCharRawPointerType(parameter.Type));
    }

    private static IEnumerable<PrintfArgumentExpectation> EnumeratePrintfArgumentExpectations(string format)
    {
        for (var index = 0; index < format.Length; index++)
        {
            if (format[index] != '%')
            {
                continue;
            }

            index++;
            if (index >= format.Length)
            {
                yield break;
            }

            if (format[index] == '%')
            {
                continue;
            }

            while (index < format.Length && IsPrintfFlag(format[index]))
            {
                index++;
            }

            if (index < format.Length && format[index] == '*')
            {
                yield return new PrintfArgumentExpectation(RequiresCString: false);
                index++;
            }
            else
            {
                while (index < format.Length && char.IsDigit(format[index]))
                {
                    index++;
                }
            }

            if (index < format.Length && format[index] == '.')
            {
                index++;
                if (index < format.Length && format[index] == '*')
                {
                    yield return new PrintfArgumentExpectation(RequiresCString: false);
                    index++;
                }
                else
                {
                    while (index < format.Length && char.IsDigit(format[index]))
                    {
                        index++;
                    }
                }
            }

            var lengthModifierStart = index;
            index = SkipPrintfLengthModifier(format, index);
            if (index >= format.Length)
            {
                yield break;
            }

            var conversion = format[index];
            if (conversion == '%')
            {
                continue;
            }

            if (IsPrintfConversionSpecifier(conversion))
            {
                yield return new PrintfArgumentExpectation(
                    RequiresCString: conversion == 's' && index == lengthModifierStart);
            }
        }
    }

    private static bool IsPrintfFlag(char character)
    {
        return character is '-' or '+' or ' ' or '#' or '0' or '\'';
    }

    private static int SkipPrintfLengthModifier(string format, int index)
    {
        if (index >= format.Length)
        {
            return index;
        }

        return format[index] switch
        {
            'h' when index + 1 < format.Length && format[index + 1] == 'h' => index + 2,
            'l' when index + 1 < format.Length && format[index + 1] == 'l' => index + 2,
            'h' or 'l' or 'j' or 'z' or 't' or 'L' => index + 1,
            'I' when index + 2 < format.Length
                     && (format[index + 1], format[index + 2]) is ('3', '2') or ('6', '4') => index + 3,
            'I' => index + 1,
            _ => index
        };
    }

    private static bool IsPrintfConversionSpecifier(char character)
    {
        return character is 'd' or 'i' or 'u' or 'o' or 'x' or 'X'
            or 'f' or 'F' or 'e' or 'E' or 'g' or 'G' or 'a' or 'A'
            or 'c' or 's' or 'p' or 'n';
    }

    private static bool IsCCharRawPointerType(StarkTypeSymbol type)
    {
        type = StarkTypeSymbols.WithQualifiers(
            type,
            borrowKind: StarkBorrowKind.None,
            accessKind: StarkAccessKind.None,
            initializationKind: StarkInitializationKind.None,
            isMutableView: false);

        return type.Kind == StarkTypeKind.RawPointer
            && type.ElementType is { } elementType
            && string.Equals(
                StarkTypeSymbols.WithQualifiers(
                    elementType,
                    borrowKind: StarkBorrowKind.None,
                    accessKind: StarkAccessKind.None,
                    initializationKind: StarkInitializationKind.None,
                    isMutableView: false).CSourceAliasName,
                StarkCDataModelFacts.QualifyAliasName("c_char"),
                StringComparison.Ordinal);
    }

    private readonly record struct PrintfArgumentExpectation(bool RequiresCString);

    private ExpressionBinding InvokeIndirectCall(ExpressionBinding target, StarkParser.ArgumentListContext arguments, Scope scope)
    {
        if (target.Type.FunctionPointerReturnType is not { } returnType
            || target.Type.FunctionPointerParameterTypes is not { } parameterTypes)
        {
            ReportError("STK3008", $"{DescribeExpressionTarget(target)} is not callable.", arguments);
            return new ExpressionBinding(StarkTypeSymbols.Error);
        }

        if (target.Type.FunctionPointerIsUnsafe && _unsafeDepth == 0)
        {
            ReportError(
                "STK3024",
                $"Unsafe function pointer '{target.Type.DisplayName}' requires an unsafe context.",
                arguments);
        }

        var expectedParameters = parameterTypes
            .Select((parameterType, index) => new TypedParameterSymbol($"arg{index}", parameterType))
            .ToArray();
        var displayTargetName = target.DiagnosticName ?? "function pointer";
        var functionPointerSignature = new TypedFunctionSignature(
            displayTargetName,
            returnType,
            expectedParameters,
            SourceName: displayTargetName,
            Kind: target.Type.FunctionPointerKind ?? StarkFunctionKind.Fn,
            DisjointParameterGroups: target.Type.FunctionPointerDisjointParameterGroups ?? [],
            OverlapParameterGroups: target.Type.FunctionPointerOverlapParameterGroups ?? [],
            SameParameterGroups: target.Type.FunctionPointerSameParameterGroups ?? []);
        var argumentBindings = EvaluateArguments(arguments, expectedParameters, scope);

        if (parameterTypes.Count != arguments.argument().Length)
        {
            ReportError(
                "STK3009",
                $"{DescribeExpressionTarget(target)} expects {parameterTypes.Count} arguments but received {arguments.argument().Length}.",
                arguments);
        }

        for (var index = 0; index < Math.Min(parameterTypes.Count, argumentBindings.Length); index++)
        {
            EnsureCallArgumentCompatible(
                displayTargetName,
                index + 1,
                expectedParameters[index],
                argumentBindings[index],
                arguments.argument(index).expression());
        }

        ValidateBoundedRawPointerCallArguments(
            functionPointerSignature,
            receiverOffset: 0,
            arguments,
            argumentBindings,
            displayTargetName,
            scope);

        ValidateDisjointCallArguments(
            functionPointerSignature,
            receiver: null,
            receiverOffset: 0,
            arguments,
            argumentBindings,
            displayTargetName,
            scope);

        var callArguments = BuildCallArgumentRecords(expectedParameters, null, argumentBindings, 0);
        var location = Location(arguments);
        _indirectCalls.Add(new IndirectCallTypingRecord(
            target.Type,
            location,
            _currentFunctionName,
            callArguments));
        _boundOperations.Add(new BoundFunctionPointerCallOperation(
            target.Type,
            callArguments,
            location,
            _currentFunctionName));

        if (returnType.BorrowKind != StarkBorrowKind.None)
        {
            var valueType = GetBorrowReturnExpressionValueType(returnType);
            var isPointerBacked = StarkTypeSymbols.IsPointerBackedBorrowReturn(returnType);
            return new ExpressionBinding(
                valueType,
                IsAssignable: isPointerBacked && returnType.IsMutableView,
                NamedType: ResolveNamedTypeSymbol(valueType),
                DiagnosticName: $"indirect call through {DescribeExpressionTarget(target)}",
                IsAddressable: true,
                IsAddressMutable: returnType.IsMutableView);
        }

        return new ExpressionBinding(returnType, NamedType: ResolveNamedTypeSymbol(returnType), DiagnosticName: $"indirect call through {DescribeExpressionTarget(target)}");
    }

    private ExpressionBinding InvokeClosureCall(ExpressionBinding target, StarkParser.ArgumentListContext arguments, Scope scope)
    {
        if (target.Type.ClosureReturnType is not { } returnType
            || target.Type.ClosureParameterTypes is not { } parameterTypes)
        {
            ReportError("STK3008", $"{DescribeExpressionTarget(target)} is not callable.", arguments);
            return new ExpressionBinding(StarkTypeSymbols.Error);
        }

        var expectedParameters = parameterTypes
            .Select((parameterType, index) => new TypedParameterSymbol(
                $"arg{index}",
                parameterType,
                RawPointerElementCountExpression: StarkTypeSymbols.GetClosureParameterRawPointerElementCountExpression(target.Type, index)))
            .ToArray();
        var displayTargetName = target.DiagnosticName ?? "closure";
        var closureSignature = new TypedFunctionSignature(
            displayTargetName,
            returnType,
            expectedParameters,
            SourceName: displayTargetName,
            Kind: target.Type.ClosureFunctionKind ?? StarkFunctionKind.Fn,
            DisjointParameterGroups: target.Type.ClosureDisjointParameterGroups ?? [],
            OverlapParameterGroups: target.Type.ClosureOverlapParameterGroups ?? [],
            SameParameterGroups: target.Type.ClosureSameParameterGroups ?? []);
        var argumentBindings = EvaluateArguments(arguments, expectedParameters, scope);

        if (parameterTypes.Count != arguments.argument().Length)
        {
            ReportError(
                "STK3009",
                $"{DescribeExpressionTarget(target)} expects {parameterTypes.Count} arguments but received {arguments.argument().Length}.",
                arguments);
        }

        if (target.Type.ClosureCallCapability == StarkClosureCallCapability.Mut
            && target.Type.ClosureStorageKind != StarkClosureStorageKind.Inline
            && !target.IsAddressMutable)
        {
            ReportError(
                "STK3008",
                $"Mutable closure call through {DescribeExpressionTarget(target)} requires mutable access to the closure value.",
                arguments);
        }

        for (var index = 0; index < Math.Min(parameterTypes.Count, argumentBindings.Length); index++)
        {
            EnsureCallArgumentCompatible(
                displayTargetName,
                index + 1,
                expectedParameters[index],
                argumentBindings[index],
                arguments.argument(index).expression());
        }

        ValidateBoundedRawPointerCallArguments(
            closureSignature,
            receiverOffset: 0,
            arguments,
            argumentBindings,
            displayTargetName,
            scope);

        ValidateDisjointCallArguments(
            closureSignature,
            receiver: null,
            receiverOffset: 0,
            arguments,
            argumentBindings,
            displayTargetName,
            scope);

        var callArguments = BuildCallArgumentRecords(expectedParameters, null, argumentBindings, 0);
        var location = Location(arguments);
        _closureCalls.Add(new ClosureCallTypingRecord(
            target.Type,
            location,
            _currentFunctionName,
            callArguments));
        _boundOperations.Add(new BoundClosureCallOperation(
            target.Type,
            callArguments,
            location,
            _currentFunctionName));

        if (returnType.BorrowKind != StarkBorrowKind.None)
        {
            var valueType = GetBorrowReturnExpressionValueType(returnType);
            var isPointerBacked = StarkTypeSymbols.IsPointerBackedBorrowReturn(returnType);
            return new ExpressionBinding(
                valueType,
                IsAssignable: isPointerBacked && returnType.IsMutableView,
                NamedType: ResolveNamedTypeSymbol(valueType),
                DiagnosticName: $"closure call through {DescribeExpressionTarget(target)}",
                IsAddressable: true,
                IsAddressMutable: returnType.IsMutableView);
        }

        return new ExpressionBinding(returnType, NamedType: ResolveNamedTypeSymbol(returnType), DiagnosticName: $"closure call through {DescribeExpressionTarget(target)}");
    }

    private static StarkTypeSymbol GetBorrowReturnExpressionValueType(StarkTypeSymbol returnType)
    {
        var valueType = StarkTypeSymbols.BorrowReturnValueType(returnType);
        return StarkTypeSymbols.IsPointerBackedBorrowReturn(returnType)
               && valueType.AccessKind == StarkAccessKind.Frozen
            ? StarkTypeSymbols.FreezeReachableView(valueType)
            : valueType;
    }

    private ExpressionBinding InvokeEnumConstructor(ExpressionBinding target, StarkParser.ArgumentListContext arguments, Scope scope)
    {
        var constructor = target.EnumConstructor!;
        if (constructor.Variant.IsUnit || constructor.Variant.UsesNamedFields)
        {
            ReportError("STK3008", $"Enum constructor '{constructor.Name}' is not callable with positional arguments.", arguments);
            return new ExpressionBinding(StarkTypeSymbols.Error);
        }

        var expectedCount = constructor.Variant.Fields.Count;
        var receivedCount = arguments.argument().Length;
        var hasErrors = false;

        if (expectedCount != receivedCount)
        {
            ReportError(
                "STK3009",
                $"Enum constructor '{constructor.Name}' expects {expectedCount} arguments but received {receivedCount}.",
                arguments);
            hasErrors = true;
        }

        for (var index = 0; index < arguments.argument().Length; index++)
        {
            var parameterType = index < constructor.Variant.Fields.Count
                ? constructor.Variant.Fields[index].Type
                : null;
            var argumentType = EvaluateExpression(arguments.argument(index).expression(), scope, allowFunctionReference: false, expectedType: parameterType).Type;
            if (index >= constructor.Variant.Fields.Count)
            {
                continue;
            }

            parameterType = constructor.Variant.Fields[index].Type;
            if (!CanAssign(parameterType, argumentType))
            {
                hasErrors = true;
                ReportError(
                    "STK3002",
                    $"Argument {index + 1} for enum constructor '{constructor.Name}' expects '{parameterType.DisplayName}' but found '{argumentType.DisplayName}'.{GetExplicitConversionHint(parameterType, argumentType)}",
                    arguments.argument(index).expression());
            }
        }

        if (hasErrors)
        {
            return new ExpressionBinding(StarkTypeSymbols.Error);
        }

        RecordEnumCall(target.Type, constructor.Variant.Name, arguments);
        return new ExpressionBinding(target.Type, NamedType: target.NamedType, DiagnosticName: $"enum constructor '{constructor.Name}'");
    }

    private ExpressionBinding ApplyIndex(ExpressionBinding target, StarkParser.ExpressionListContext indexes, Scope scope, ParserRuleContext context)
    {
        if (target.Type.Kind == StarkTypeKind.Dynamic && target.Type.ElementType is not null)
        {
            return ApplyDynamicIndex(target, indexes, scope, context);
        }

        if (target.Type.Kind is StarkTypeKind.Ascii or StarkTypeKind.Unicode)
        {
            var indexExpressions = indexes.expression();
            if (indexExpressions.Length == 0)
            {
                var result = new ExpressionBinding(
                    target.Type,
                    IsAssignable: false,
                    NamedType: ResolveNamedTypeSymbol(target.Type),
                    DiagnosticName: target.DiagnosticName is null ? "text slice" : $"text slice of {target.DiagnosticName}",
                    RootGlobalName: target.RootGlobalName,
                    RootGlobalBindingKind: target.RootGlobalBindingKind,
                    UsesFrozenProjectionSemantics: UsesFrozenProjectionSemantics(target),
                    HasConstProvenance: HasConstProvenance(target),
                    MemoryRootKey: target.MemoryRootKey,
                    MemoryRootIsIndependentStorage: target.MemoryRootIsIndependentStorage);
                RecordIndexAccess("text-slice", target.Type, result.Type, 0, indexes);
                return result;
            }

            if (indexExpressions.Length == 1)
            {
                var indexType = EvaluateExpression(indexExpressions[0], scope, allowFunctionReference: false).Type;
                if (indexType.Kind != StarkTypeKind.Integer)
                {
                    ReportError(
                        "STK3002",
                        $"Text indexing on {DescribeExpressionTarget(target)} expects an integer index operand but found '{indexType.DisplayName}'.{GetExplicitConversionHint(StarkTypeSymbols.Integer(32), indexType)}",
                        indexExpressions[0]);
                }

                var result = new ExpressionBinding(
                    target.Type,
                    IsAssignable: false,
                    NamedType: ResolveNamedTypeSymbol(target.Type),
                    DiagnosticName: target.DiagnosticName is null ? "text element" : $"text element of {target.DiagnosticName}",
                    RootGlobalName: target.RootGlobalName,
                    RootGlobalBindingKind: target.RootGlobalBindingKind,
                    UsesFrozenProjectionSemantics: UsesFrozenProjectionSemantics(target),
                    HasConstProvenance: HasConstProvenance(target),
                    MemoryRootKey: target.MemoryRootKey is { } elementMemoryRootKey
                        ? AppendMemoryRootIndexKey(elementMemoryRootKey, indexExpressions[0])
                        : null,
                    MemoryRootIsIndependentStorage: target.MemoryRootIsIndependentStorage);
                RecordIndexAccess("text-element", target.Type, result.Type, 1, indexes);
                return result;
            }

            if (indexExpressions.Length != 2)
            {
                ReportError("STK3008", "Text indexing currently supports exactly one integer index or two integer expressions: start and length.", context);
                return new ExpressionBinding(StarkTypeSymbols.Error, DiagnosticName: "text slice");
            }

            foreach (var indexExpression in indexExpressions)
            {
                var indexType = EvaluateExpression(indexExpression, scope, allowFunctionReference: false).Type;
                if (indexType.Kind != StarkTypeKind.Integer)
                {
                    ReportError(
                        "STK3002",
                        $"Text slicing on {DescribeExpressionTarget(target)} expects integer start/length operands but found '{indexType.DisplayName}'.{GetExplicitConversionHint(StarkTypeSymbols.Integer(32), indexType)}",
                        indexExpression);
                }
            }

            var textSlice = new ExpressionBinding(
                target.Type,
                IsAssignable: false,
                NamedType: ResolveNamedTypeSymbol(target.Type),
                DiagnosticName: target.DiagnosticName is null ? "text slice" : $"text slice of {target.DiagnosticName}",
                RootGlobalName: target.RootGlobalName,
                RootGlobalBindingKind: target.RootGlobalBindingKind,
                UsesFrozenProjectionSemantics: UsesFrozenProjectionSemantics(target),
                HasConstProvenance: HasConstProvenance(target),
                MemoryRootKey: target.MemoryRootKey is { } sliceMemoryRootKey
                    ? AppendMemoryRootTextRangeKey(sliceMemoryRootKey, indexExpressions[0], indexExpressions[1], scope)
                    : null,
                MemoryRootIsIndependentStorage: target.MemoryRootIsIndependentStorage);
            RecordIndexAccess("text-slice", target.Type, textSlice.Type, 2, indexes);
            return textSlice;
        }

        var currentType = target.Type;
        var currentIsAddressMutable = target.IsAddressMutable;
        var currentUsesFrozenProjectionSemantics = UsesFrozenProjectionSemantics(target);
        var currentHasConstProvenance = HasConstProvenance(target);
        var currentMemoryRootKey = target.MemoryRootKey;

        foreach (var indexExpression in indexes.expression())
        {
            var indexType = EvaluateExpression(indexExpression, scope, allowFunctionReference: false).Type;
            if (indexType.Kind != StarkTypeKind.Integer)
            {
                ReportError(
                    "STK3002",
                    $"Index access on {DescribeExpressionTarget(target)} expects an integer index but found '{indexType.DisplayName}'.{GetExplicitConversionHint(StarkTypeSymbols.Integer(32), indexType)}",
                    indexExpression);
            }

            currentMemoryRootKey = currentMemoryRootKey is null
                ? null
                : AppendMemoryRootIndexKey(currentMemoryRootKey, indexExpression);

            if (currentType.Kind is StarkTypeKind.FixedArray or StarkTypeKind.Slice && currentType.ElementType is not null)
            {
                var containerInitializationKind = currentType.InitializationKind;
                currentIsAddressMutable = currentType.Kind == StarkTypeKind.Slice
                    ? currentIsAddressMutable
                        && (currentType.IsMutableView || currentType.InitializationKind != StarkInitializationKind.None)
                        && currentType.AccessKind != StarkAccessKind.Frozen
                    : currentIsAddressMutable
                        && currentType.AccessKind != StarkAccessKind.Frozen;
                currentType = currentUsesFrozenProjectionSemantics
                    ? StarkTypeSymbols.FreezeReachableView(currentType.ElementType)
                    : ProjectFrozenView(currentType, currentType.ElementType);
                currentType = ApplyProjectedInitializationKind(currentType, containerInitializationKind);
                currentIsAddressMutable &= currentType.AccessKind != StarkAccessKind.Frozen;
                currentUsesFrozenProjectionSemantics = currentUsesFrozenProjectionSemantics
                    || currentType.AccessKind == StarkAccessKind.Frozen;
                continue;
            }

            if (currentType.Kind == StarkTypeKind.RawPointer && currentType.ElementType is not null)
            {
                currentIsAddressMutable = currentType.IsMutablePointer && !currentUsesFrozenProjectionSemantics;
                currentType = currentUsesFrozenProjectionSemantics
                    ? StarkTypeSymbols.FreezeReachableView(currentType.ElementType)
                    : currentType.ElementType;
                currentIsAddressMutable &= currentType.AccessKind != StarkAccessKind.Frozen;
                currentUsesFrozenProjectionSemantics = currentUsesFrozenProjectionSemantics
                    || currentType.AccessKind == StarkAccessKind.Frozen;
                continue;
            }

            ReportError("STK3010", $"{DescribeExpressionTarget(target)} is not indexable.", context);
            return new ExpressionBinding(StarkTypeSymbols.Error);
        }

        var indexed = new ExpressionBinding(
            currentType,
            IsAssignable: currentIsAddressMutable,
            NamedType: ResolveNamedTypeSymbol(currentType),
            DiagnosticName: target.DiagnosticName is null ? "indexed element" : $"indexed element of {target.DiagnosticName}",
            IsAddressable: target.IsAddressable,
            IsAddressMutable: currentIsAddressMutable,
            RootGlobalName: target.RootGlobalName,
            RootGlobalBindingKind: target.RootGlobalBindingKind,
            AssignmentErrorMessage: target.RootGlobalBindingKind is not null
                && target.RootGlobalName is not null
                && !currentIsAddressMutable
                ? DescribeGlobalMutationError(target.RootGlobalName, target.RootGlobalBindingKind.Value, "indexed element")
                : target.Type.AccessKind == StarkAccessKind.Frozen
                    ? DescribeFrozenMutationError("indexed element")
                : target.AssignmentErrorMessage,
            UsesFrozenProjectionSemantics: currentUsesFrozenProjectionSemantics,
            HasConstProvenance: currentHasConstProvenance,
            MemoryRootKey: currentMemoryRootKey,
            MemoryRootIsIndependentStorage: target.MemoryRootIsIndependentStorage,
            IsMisalignedFieldProjection: target.IsMisalignedFieldProjection);
        RecordIndexAccess("element", target.Type, indexed.Type, indexes.expression().Length, indexes);
        return indexed;
    }

    private ExpressionBinding ApplyMemberAccess(ExpressionBinding target, string memberName, ParserRuleContext context)
    {
        if (target.NamespaceName is not null)
        {
            var qualifiedName = $"{target.NamespaceName}.{memberName}";
            if (IsCompilerKnownNamespace(qualifiedName))
            {
                return new ExpressionBinding(StarkTypeSymbols.Error, NamespaceName: qualifiedName, DiagnosticName: $"module namespace '{qualifiedName}'");
            }

            if (_moduleGraph.CanAccessModule(CurrentFunctionModuleName, qualifiedName))
            {
                return new ExpressionBinding(StarkTypeSymbols.Error, NamespaceName: qualifiedName, DiagnosticName: $"module '{qualifiedName}'");
            }

            if (_moduleGraph.CanAccessModuleNamespace(CurrentFunctionModuleName, qualifiedName))
            {
                return new ExpressionBinding(StarkTypeSymbols.Error, NamespaceName: qualifiedName, DiagnosticName: $"module namespace '{qualifiedName}'");
            }

            if (_globals.TryGetValue(qualifiedName, out var global))
            {
                return new ExpressionBinding(
                    global.Type,
                    IsAssignable: global.IsMutable,
                    NamedType: ResolveNamedTypeSymbol(global.Type),
                    DiagnosticName: global.IsConstant ? $"constant '{qualifiedName}'" : $"variable '{qualifiedName}'",
                    IsAddressable: true,
                    IsAddressMutable: global.IsMutable,
                    RootGlobalName: qualifiedName,
                    RootGlobalBindingKind: global.BindingKind,
                    HasConstProvenance: global.BindingKind == GlobalBindingKind.Const,
                    AssignmentErrorMessage: global.IsMutable
                        ? null
                        : DescribeGlobalRebindingError(qualifiedName, global.BindingKind ?? GlobalBindingKind.Immutable),
                    MemoryRootKey: qualifiedName,
                    MemoryRootIsIndependentStorage: IsLocalBindingIndependentStorage(global));
            }

            if (TryGetFunctionOverloads(qualifiedName, out var namespaceFunctions))
            {
                if (IsTraitMethodFunctionName(qualifiedName))
                {
                    ReportError(
                        "STK3013",
                        $"Trait method '{qualifiedName}' is a compile-time-only contract and cannot be called directly.",
                        context);
                    return new ExpressionBinding(StarkTypeSymbols.Error);
                }

                if (!TryFilterDirectCallableTypeMemberFunctions(qualifiedName, namespaceFunctions, context, out namespaceFunctions))
                {
                    return new ExpressionBinding(StarkTypeSymbols.Error);
                }

                if (namespaceFunctions.Count == 1 && !namespaceFunctions[0].IsGeneric)
                {
                    var function = namespaceFunctions[0];
                    return new ExpressionBinding(function.ReturnType, Function: function, DiagnosticName: $"function '{qualifiedName}'");
                }

                return new ExpressionBinding(
                    StarkTypeSymbols.Error,
                    DiagnosticName: $"overload group '{qualifiedName}'",
                    OverloadSourceName: qualifiedName);
            }

            if (TryResolveNamedTypeBySourceName(qualifiedName, out var qualifiedType))
            {
                if (qualifiedType.Kind is DeclarationKind.Struct or DeclarationKind.Record)
                {
                    return new ExpressionBinding(
                        StarkTypeSymbols.Error,
                        NamespaceName: qualifiedName,
                        NamedType: qualifiedType,
                        DiagnosticName: $"type '{qualifiedName}'");
                }

                if (qualifiedType.Kind == DeclarationKind.Enum)
                {
                    return new ExpressionBinding(StarkTypeSymbols.Error, NamespaceName: qualifiedName, DiagnosticName: $"enum '{qualifiedName}'");
                }

                if (qualifiedType.Kind == DeclarationKind.Doctrine)
                {
                    return new ExpressionBinding(
                        StarkTypeSymbols.Named(qualifiedType.Name),
                        NamedType: qualifiedType,
                        DiagnosticName: $"doctrine '{qualifiedName}'");
                }

                if (qualifiedType.Kind == DeclarationKind.Trait)
                {
                    ReportError(
                        "STK3013",
                        $"Trait '{qualifiedName}' is compile-time-only and cannot be used as a runtime value. {DescribeNoDynamicDispatchPolicy()}",
                        context);
                    return new ExpressionBinding(StarkTypeSymbols.Error);
                }
            }

            if (TryResolveEnumCaseReference(qualifiedName, out var enumType, out var enumTypeSymbol, out var variant))
            {
                if (variant.IsUnit)
                {
                    RecordEnumValue(enumTypeSymbol, variant.Name, context.Start);
                    return new ExpressionBinding(enumTypeSymbol, NamedType: enumType, DiagnosticName: $"enum case '{qualifiedName}'");
                }

                return new ExpressionBinding(
                    enumTypeSymbol,
                    NamedType: enumType,
                    DiagnosticName: $"enum constructor '{qualifiedName}'",
                    EnumConstructor: new EnumConstructorBinding(qualifiedName, variant));
            }

            ReportError("STK3003", $"Unknown symbol '{qualifiedName}'.", context);
            return new ExpressionBinding(StarkTypeSymbols.Error);
        }

        if (TryApplyValueTextConversionMemberAccess(target, memberName, context, out var valueTextConversion))
        {
            return valueTextConversion;
        }

        if (target.Type.Kind == StarkTypeKind.Dynamic)
        {
            return ApplyDynamicMemberAccess(target, memberName, context);
        }

        if (TryApplyKnownViewMemberAccess(target, memberName, context, out var knownViewMember))
        {
            return knownViewMember;
        }

        if (target.Type.Kind == StarkTypeKind.DynTrait)
        {
            return ApplyDynTraitMemberAccess(target, memberName, context);
        }

        var namedType = target.NamedType ?? ResolveNamedTypeSymbol(target.Type);
        if (namedType is null)
        {
            if (TryResolveTraitBoundMemberCall(target, memberName, out var traitBoundCall))
            {
                return traitBoundCall;
            }

            ReportError("STK3011", $"Cannot access member '{memberName}' on {DescribeExpressionTarget(target)}.", context);
            return new ExpressionBinding(StarkTypeSymbols.Error);
        }

        if (namedType.TryGetField(memberName, out var field, out var fieldIndex))
        {
            if (!IsFieldAccessible(field))
            {
                ReportError(
                    "STK3015",
                    $"Field '{memberName}' is {RenderVisibility(field.Visibility)} and is not visible from module '{CurrentFunctionModuleName}'.",
                    context);
                return new ExpressionBinding(StarkTypeSymbols.Error);
            }

            RecordFieldAccess(field.Name, fieldIndex, field.Type, context);
            var projectedType = ProjectProjectionType(target, field.Type);
            var isAddressMutable = CanMutateAddressProjection(target, projectedType);
            var isAssignable = isAddressMutable;
            return new ExpressionBinding(
                projectedType,
                IsAssignable: isAssignable,
                NamedType: ResolveNamedTypeSymbol(projectedType),
                DiagnosticName: $"member '{memberName}'",
                IsAddressable: target.IsAddressable,
                IsAddressMutable: isAddressMutable,
                RootGlobalName: target.RootGlobalName,
                RootGlobalBindingKind: target.RootGlobalBindingKind,
                AssignmentErrorMessage: target.RootGlobalBindingKind is not null
                    && target.RootGlobalName is not null
                    && !isAssignable
                    ? DescribeGlobalMutationError(target.RootGlobalName, target.RootGlobalBindingKind.Value, $"member '{memberName}'")
                    : target.Type.AccessKind == StarkAccessKind.Frozen
                        ? DescribeFrozenMutationError($"member '{memberName}'")
                    : target.AssignmentErrorMessage,
                UsesFrozenProjectionSemantics: UsesFrozenProjectionSemantics(target),
                HasConstProvenance: HasConstProvenance(target),
                MemoryRootKey: target.MemoryRootKey is { } memoryRootKey
                    ? $"{memoryRootKey}.{memberName}"
                    : null,
                MemoryRootIsIndependentStorage: target.MemoryRootIsIndependentStorage,
                IsMisalignedFieldProjection: target.IsMisalignedFieldProjection
                    || IsMisalignedLayoutFieldProjection(namedType, field.Name));
        }

        var methodSourceName = $"{StarkTypeSymbols.GetGenericBaseName(namedType.Name)}.{memberName}";
        if (namedType.Kind == DeclarationKind.Doctrine
            && TryGetFunctionOverloads(methodSourceName, out var doctrineMethods))
        {
            if (doctrineMethods.Count == 1 && !doctrineMethods[0].IsGeneric)
            {
                var doctrineMethod = doctrineMethods[0];
                return new ExpressionBinding(
                    doctrineMethod.ReturnType,
                    NamedType: ResolveNamedTypeSymbol(doctrineMethod.ReturnType),
                    Function: doctrineMethod,
                    DiagnosticName: $"doctrine method '{doctrineMethod.DisplaySourceName}'");
            }

            return new ExpressionBinding(
                StarkTypeSymbols.Error,
                DiagnosticName: $"doctrine overload group '{methodSourceName}'",
                OverloadSourceName: methodSourceName);
        }

        if (namedType.Kind == DeclarationKind.Trait
            && TryGetFunctionOverloads(methodSourceName, out _))
        {
            ReportError(
                "STK3013",
                $"Trait method '{methodSourceName}' is a compile-time-only contract and cannot be called directly.",
                context);
            return new ExpressionBinding(StarkTypeSymbols.Error);
        }

        if (TryGetFunctionOverloads(methodSourceName, out var methods))
        {
            var instanceMethods = methods.Where(static method => !method.IsStatic).ToArray();
            if (instanceMethods.Length == 1 && !instanceMethods[0].IsGeneric && instanceMethods[0].Parameters.Count != 0)
            {
                var method = instanceMethods[0];
                return new ExpressionBinding(
                    method.ReturnType,
                    NamedType: ResolveNamedTypeSymbol(method.ReturnType),
                    Function: method,
                    DiagnosticName: $"method '{method.DisplaySourceName}'",
                    Receiver: target);
            }

            if (instanceMethods.Length == 0 && methods.Any(static method => method.IsStatic))
            {
                ReportError(
                    "STK3014",
                    $"Static member function '{methodSourceName}' must be called through the type name, not through an instance.",
                    context);
                return new ExpressionBinding(StarkTypeSymbols.Error);
            }

            return new ExpressionBinding(
                StarkTypeSymbols.Error,
                DiagnosticName: $"method overload group '{methodSourceName}'",
                Receiver: target,
                OverloadSourceName: methodSourceName);
        }

        if (namedType.Kind == DeclarationKind.Doctrine)
        {
            ReportError("STK3005", $"Doctrine '{namedType.Name}' does not declare a method named '{memberName}'.", context);
            return new ExpressionBinding(StarkTypeSymbols.Error);
        }

        // Default-member fallback (Phase B): a type that implements a trait but does
        // not override one of its default methods dispatches to the trait's default
        // body, instantiated with Self = this type via ordinary overload resolution.
        if (TryResolveTraitDefaultMemberCall(namedType, target, memberName, out var traitDefaultCall))
        {
            return traitDefaultCall;
        }

        var methodSyntaxHint = TryDescribeMethodSyntaxFreeFunctionHint(memberName, target.Type, out var freeFunctionHint)
            ? freeFunctionHint
            : string.Empty;
        ReportError("STK3005", $"Type '{namedType.Name}' does not contain a field named '{memberName}'.{methodSyntaxHint}", context);
        return new ExpressionBinding(StarkTypeSymbols.Error);
    }

    // `value.Fn(...)` where `Fn` is not a member but a free function whose first
    // parameter accepts the receiver type is a common method-syntax slip: Stark
    // has no UFCS, so methods must live inside the type body. Surface that as a
    // hint rather than a bare "does not contain a field named".
    private bool TryDescribeMethodSyntaxFreeFunctionHint(
        string memberName,
        StarkTypeSymbol receiverType,
        out string hint)
    {
        hint = string.Empty;
        if (!TryGetFunctionOverloads(memberName, out var overloads))
        {
            return false;
        }

        foreach (var overload in overloads)
        {
            if (overload.Parameters.Count == 0)
            {
                continue;
            }

            var firstParameterType = overload.Parameters[0].Type;
            var matchesReceiver = firstParameterType.Kind == StarkTypeKind.Named && receiverType.Kind == StarkTypeKind.Named
                ? string.Equals(firstParameterType.NamedType, receiverType.NamedType, StringComparison.Ordinal)
                : firstParameterType.Kind == receiverType.Kind;
            if (matchesReceiver)
            {
                hint = $" '{memberName}' is a free function — call it as '{memberName}(...)' with the receiver as the first argument. Methods are declared inside the type body.";
                return true;
            }
        }

        return false;
    }

    private bool TryApplyKnownViewMemberAccess(
        ExpressionBinding target,
        string memberName,
        ParserRuleContext context,
        out ExpressionBinding result)
    {
        result = null!;
        if (!string.Equals(memberName, "Length", StringComparison.Ordinal)
            || target.Type.Kind is not (StarkTypeKind.FixedArray or StarkTypeKind.Slice))
        {
            return false;
        }

        var memberType = target.Type.Kind == StarkTypeKind.FixedArray
            && target.Type.FixedLength is int fixedLength
                ? StarkTypeSymbols.Integer(64, new BigInteger(fixedLength), new BigInteger(fixedLength))
                : NonNegativeI64Type;
        RecordFieldAccess(memberName, 1, memberType, context);
        result = new ExpressionBinding(
            memberType,
            IsAssignable: false,
            NamedType: ResolveNamedTypeSymbol(memberType),
            DiagnosticName: target.Type.Kind == StarkTypeKind.FixedArray
                ? "fixed array length"
                : "slice length",
            RootGlobalName: target.RootGlobalName,
            RootGlobalBindingKind: target.RootGlobalBindingKind,
            HasConstProvenance: target.Type.Kind == StarkTypeKind.FixedArray || HasConstProvenance(target),
            MemoryRootKey: target.MemoryRootKey is { } memoryRootKey
                ? $"{memoryRootKey}.{memberName}"
                : null,
            MemoryRootIsIndependentStorage: target.MemoryRootIsIndependentStorage);
        return true;
    }

    // CG05: resolves `value.Member(...)` where `value` is a generic type parameter
    // constrained by `where T: Trait`. Binds the call to the trait method with
    // `Self` (and any trait type arguments) substituted to the type parameter so
    // the generic body type-checks. The bound target keeps the trait-method name,
    // which MIR lowering rebinds to the concrete implementation per specialization
    // Resolves `receiver.Member(...)` where the receiver is a `dyn Trait` trait
    // object: the call binds to the trait method's signature (so it type-checks and
    // yields the right return type) while MIR lowers it to an indirect vtable call.
    // `Self` is substituted to the trait-object type; the method must be object-safe.
    private ExpressionBinding ApplyDynTraitMemberAccess(ExpressionBinding target, string memberName, ParserRuleContext context)
    {
        if (target.Type.DynTraitName is not { } traitName)
        {
            ReportError("STK3011", $"Cannot access member '{memberName}' on {DescribeExpressionTarget(target)}.", context);
            return new ExpressionBinding(StarkTypeSymbols.Error);
        }

        if (TryApplyDynTraitRepresentationMemberAccess(target, memberName, context, out var representationMember))
        {
            return representationMember;
        }

        var traitSimpleName = traitName.LastIndexOf('.') is var dot && dot >= 0 ? traitName[(dot + 1)..] : traitName;
        var methodSourceName = $"{StarkTypeSymbols.GetGenericBaseName(traitName)}.{memberName}";
        if (!TryGetFunctionOverloads(methodSourceName, out var methods)
            || methods.Where(static method => !method.IsStatic).ToArray() is not { Length: 1 } instanceMethods)
        {
            ReportError(
                "STK3011",
                $"'dyn {traitSimpleName}' has no instance method '{memberName}' to dispatch.",
                context);
            return new ExpressionBinding(StarkTypeSymbols.Error);
        }

        var traitMethod = instanceMethods[0];
        if (!DynTraitFacts.IsObjectSafeInstanceMethod(traitMethod))
        {
            ReportError(
                "STK3036",
                $"Trait method '{methodSourceName}' is not object-safe and cannot be called through 'dyn {traitSimpleName}'.",
                context);
            return new ExpressionBinding(StarkTypeSymbols.Error);
        }

        // Substitute `Self` -> the trait-object type, plus the trait's own type
        // arguments, so the bound signature's parameter/return types are concrete.
        var substitution = new Dictionary<string, StarkTypeSymbol>(StringComparer.Ordinal)
        {
            ["Self"] = target.Type,
        };
        if (_namedTypes.TryGetValue(traitName, out var traitSymbol) && target.Type.TypeArguments is { } traitArguments)
        {
            var traitParameters = traitSymbol.GenericParams;
            for (var index = 0; index < traitParameters.Count && index < traitArguments.Count; index++)
            {
                substitution[traitParameters[index]] = traitArguments[index];
            }
        }

        var resolvedMethod = traitMethod with
        {
            ReturnType = FunctionOverloadFacts.SubstituteType(traitMethod.ReturnType, substitution, ResolveAssociatedTypeForSubstitution),
            Parameters = traitMethod.Parameters
                .Select(parameter => parameter with { Type = FunctionOverloadFacts.SubstituteType(parameter.Type, substitution, ResolveAssociatedTypeForSubstitution) })
                .ToArray(),
            GenericParameterNames = null,
        };

        return new ExpressionBinding(
            resolvedMethod.ReturnType,
            NamedType: ResolveNamedTypeSymbol(resolvedMethod.ReturnType),
            Function: resolvedMethod,
            DiagnosticName: $"dynamic trait method '{traitMethod.DisplaySourceName}'",
            Receiver: target);
    }

    private bool TryApplyDynTraitRepresentationMemberAccess(
        ExpressionBinding target,
        string memberName,
        ParserRuleContext context,
        out ExpressionBinding binding)
    {
        binding = default!;
        var fieldType = memberName switch
        {
            "Context" => StarkTypeSymbols.RawPointer(StarkTypeSymbols.Integer(8), isMutable: true),
            "Vtable" => StarkTypeSymbols.DynTraitVtablePointerForTraitObject(target.Type),
            _ => StarkTypeSymbols.Error
        };
        if (fieldType.Kind == StarkTypeKind.Error)
        {
            return false;
        }

        RequireUnsafeContext($"Dynamic trait object representation member '.{memberName}'", context);
        RecordFieldAccess(memberName, string.Equals(memberName, "Context", StringComparison.Ordinal) ? 0 : 1, fieldType, context);
        binding = new ExpressionBinding(
            fieldType,
            NamedType: ResolveNamedTypeSymbol(fieldType),
            DiagnosticName: $"dynamic trait object member '{memberName}'",
            HasConstProvenance: target.HasConstProvenance,
            MemoryRootIsIndependentStorage: false);
        return true;
    }

    // (a direct call, not dynamic dispatch).
    private bool TryResolveTraitBoundMemberCall(
        ExpressionBinding target,
        string memberName,
        out ExpressionBinding binding)
    {
        binding = new ExpressionBinding(StarkTypeSymbols.Error);

        var parameterName = target.Type.NamedType;
        if (parameterName is null
            || _currentFunctionGenericParameters?.Contains(parameterName) != true
            || _currentFunctionConstraints.Count == 0)
        {
            return false;
        }

        foreach (var constraint in _currentFunctionConstraints)
        {
            if (!string.Equals(constraint.ParameterName, parameterName, StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var bound in constraint.BoundTraits)
            {
                if (bound.NamedType is not { } boundName)
                {
                    continue;
                }

                var methodSourceName = $"{StarkTypeSymbols.GetGenericBaseName(boundName)}.{memberName}";
                if (!TryGetFunctionOverloads(methodSourceName, out var methods))
                {
                    continue;
                }

                var instanceMethods = methods.Where(static method => !method.IsStatic).ToArray();
                if (instanceMethods.Length != 1)
                {
                    continue;
                }

                var traitMethod = instanceMethods[0];

                // A default method (one with a body) is dispatched by instantiating
                // the default over `Self` = this type parameter through ordinary
                // overload resolution; the per-type default body is materialized via
                // a deferred instantiation trigger. An abstract method instead falls
                // through to the pre-substituted form and is rerouted to the concrete
                // override during MIR lowering (CG06).
                if (traitMethod.HasBody)
                {
                    binding = new ExpressionBinding(
                        StarkTypeSymbols.Error,
                        DiagnosticName: $"trait default method '{methodSourceName}'",
                        Receiver: target,
                        OverloadSourceName: methodSourceName);
                    return true;
                }

                // Substitute `Self` -> the type parameter, plus the trait's own type
                // arguments (e.g. `Equatable<i32>` binds the trait's parameter to i32).
                var substitution = new Dictionary<string, StarkTypeSymbol>(StringComparer.Ordinal)
                {
                    ["Self"] = StarkTypeSymbols.Named(parameterName),
                };

                var boundSymbol = ResolveNamedTypeSymbol(bound);
                if (boundSymbol is not null && bound.TypeArguments is { } boundArguments)
                {
                    var traitParameters = boundSymbol.GenericParams;
                    for (var index = 0; index < traitParameters.Count && index < boundArguments.Count; index++)
                    {
                        substitution[traitParameters[index]] = boundArguments[index];
                    }
                }

                var resolvedMethod = traitMethod with
                {
                    ReturnType = FunctionOverloadFacts.SubstituteType(traitMethod.ReturnType, substitution, ResolveAssociatedTypeForSubstitution),
                    Parameters = traitMethod.Parameters
                        .Select(parameter => parameter with { Type = FunctionOverloadFacts.SubstituteType(parameter.Type, substitution, ResolveAssociatedTypeForSubstitution) })
                        .ToArray(),
                    GenericParameterNames = null,
                };

                binding = new ExpressionBinding(
                    resolvedMethod.ReturnType,
                    NamedType: ResolveNamedTypeSymbol(resolvedMethod.ReturnType),
                    Function: resolvedMethod,
                    DiagnosticName: $"trait method '{traitMethod.DisplaySourceName}'",
                    Receiver: target);
                return true;
            }
        }

        return false;
    }

    // Phase B: resolves `value.Member(...)` to a trait default method when the
    // value's concrete type implements a trait that defines `Member` but does not
    // override it. Binds through ordinary overload resolution so `Self` is inferred
    // from the receiver and the default body is monomorphized for the concrete type
    // (a direct call). An abstract (non-default) unimplemented method is reported as
    // a conformance failure (STK3032) elsewhere.
    private bool TryResolveTraitDefaultMemberCall(
        NamedTypeSymbol implementingType,
        ExpressionBinding target,
        string memberName,
        out ExpressionBinding binding)
    {
        binding = new ExpressionBinding(StarkTypeSymbols.Error);

        foreach (var traitName in implementingType.ImplementedTraits)
        {
            var methodSourceName = $"{StarkTypeSymbols.GetGenericBaseName(traitName)}.{memberName}";
            if (TryGetFunctionOverloads(methodSourceName, out var methods)
                && methods.Any(static method => !method.IsStatic))
            {
                binding = new ExpressionBinding(
                    StarkTypeSymbols.Error,
                    DiagnosticName: $"trait default method '{methodSourceName}'",
                    Receiver: target,
                    OverloadSourceName: methodSourceName);
                return true;
            }
        }

        return false;
    }

    private ExpressionBinding ApplyDynamicMemberAccess(ExpressionBinding target, string memberName, ParserRuleContext context)
    {
        int fieldIndex;
        if (string.Equals(memberName, "Length", StringComparison.Ordinal))
        {
            fieldIndex = 1;
        }
        else if (string.Equals(memberName, "Capacity", StringComparison.Ordinal))
        {
            fieldIndex = 2;
        }
        else
        {
            ReportError("STK3005", $"Type '{target.Type.DisplayName}' does not contain a field named '{memberName}'.", context);
            return new ExpressionBinding(StarkTypeSymbols.Error);
        }

        var memberType = NonNegativeI64Type;
        RecordFieldAccess(memberName, fieldIndex, memberType, context);
        return new ExpressionBinding(
            memberType,
            IsAssignable: false,
            NamedType: ResolveNamedTypeSymbol(memberType),
            DiagnosticName: string.Equals(memberName, "Length", StringComparison.Ordinal)
                ? "dynamic storage length"
                : "dynamic storage capacity",
            RootGlobalName: target.RootGlobalName,
            RootGlobalBindingKind: target.RootGlobalBindingKind,
            HasConstProvenance: HasConstProvenance(target),
            MemoryRootKey: target.MemoryRootKey is { } memoryRootKey
                ? $"{memoryRootKey}.{memberName}"
                : null,
            MemoryRootIsIndependentStorage: target.MemoryRootIsIndependentStorage);
    }

    private ExpressionBinding ApplyDynamicIndex(
        ExpressionBinding target,
        StarkParser.ExpressionListContext indexes,
        Scope scope,
        ParserRuleContext context)
    {
        var indexExpressions = indexes.expression();
        if (indexExpressions.Length is not (1 or 2))
        {
            ReportError("STK3008", "Dynamic storage indexing supports either one integer index or two integer expressions: start and count.", context);
            return new ExpressionBinding(StarkTypeSymbols.Error);
        }

        foreach (var indexExpression in indexExpressions)
        {
            var indexType = EvaluateExpression(indexExpression, scope, allowFunctionReference: false).Type;
            if (indexType.Kind != StarkTypeKind.Integer)
            {
                ReportError(
                    "STK3002",
                    $"Dynamic storage indexing on {DescribeExpressionTarget(target)} expects integer operands but found '{indexType.DisplayName}'.{GetExplicitConversionHint(StarkTypeSymbols.Integer(32), indexType)}",
                    indexExpression);
            }
        }

        var elementType = UsesFrozenProjectionSemantics(target)
            ? StarkTypeSymbols.FreezeReachableView(target.Type.ElementType!)
            : ProjectFrozenView(target.Type, target.Type.ElementType!);
        var isAddressMutable = target.IsAddressMutable
            && target.Type.AccessKind != StarkAccessKind.Frozen
            && elementType.AccessKind != StarkAccessKind.Frozen;
        var memoryRootKey = target.MemoryRootKey;

        if (indexExpressions.Length == 1)
        {
            var element = new ExpressionBinding(
                elementType,
                IsAssignable: isAddressMutable,
                NamedType: ResolveNamedTypeSymbol(elementType),
                DiagnosticName: target.DiagnosticName is null ? "dynamic storage element" : $"dynamic storage element of {target.DiagnosticName}",
                IsAddressable: target.IsAddressable,
                IsAddressMutable: isAddressMutable,
                RootGlobalName: target.RootGlobalName,
                RootGlobalBindingKind: target.RootGlobalBindingKind,
                AssignmentErrorMessage: target.RootGlobalBindingKind is not null
                    && target.RootGlobalName is not null
                    && !isAddressMutable
                    ? DescribeGlobalMutationError(target.RootGlobalName, target.RootGlobalBindingKind.Value, "dynamic storage element")
                    : target.Type.AccessKind == StarkAccessKind.Frozen
                        ? DescribeFrozenMutationError("dynamic storage element")
                    : target.AssignmentErrorMessage,
                UsesFrozenProjectionSemantics: UsesFrozenProjectionSemantics(target)
                    || elementType.AccessKind == StarkAccessKind.Frozen,
                HasConstProvenance: HasConstProvenance(target),
                MemoryRootKey: memoryRootKey is { } elementMemoryRootKey
                    ? AppendMemoryRootIndexKey(elementMemoryRootKey, indexExpressions[0])
                    : null,
                MemoryRootIsIndependentStorage: target.MemoryRootIsIndependentStorage);
            RecordIndexAccess("dynamic-element", target.Type, element.Type, 1, indexes);
            return element;
        }

        var sliceType = StarkTypeSymbols.ApplyQualifiers(
            StarkTypeSymbols.Slice(elementType),
            isMutableView: isAddressMutable);
        var range = new ExpressionBinding(
            sliceType,
            IsAssignable: false,
            NamedType: ResolveNamedTypeSymbol(sliceType),
            DiagnosticName: target.DiagnosticName is null ? "dynamic storage range" : $"dynamic storage range of {target.DiagnosticName}",
            IsAddressable: true,
            IsAddressMutable: isAddressMutable,
            RootGlobalName: target.RootGlobalName,
            RootGlobalBindingKind: target.RootGlobalBindingKind,
            UsesFrozenProjectionSemantics: UsesFrozenProjectionSemantics(target)
                || elementType.AccessKind == StarkAccessKind.Frozen,
            HasConstProvenance: HasConstProvenance(target),
            MemoryRootKey: memoryRootKey is { } rangeMemoryRootKey
                ? AppendMemoryRootTextRangeKey(rangeMemoryRootKey, indexExpressions[0], indexExpressions[1], scope)
                : null,
            MemoryRootIsIndependentStorage: target.MemoryRootIsIndependentStorage);
        RecordIndexAccess("dynamic-slice", target.Type, range.Type, 2, indexes);
        return range;
    }

    private bool TryApplyValueTextConversionMemberAccess(
        ExpressionBinding target,
        string memberName,
        ParserRuleContext context,
        out ExpressionBinding binding)
    {
        binding = default!;

        if (!TryGetValueTextConversionSourceName(memberName, out var sourceName)
            || !TryGetFunctionOverloads(sourceName, out var overloads))
        {
            return false;
        }

        var candidates = overloads
            .Where(static overload => !overload.IsStatic)
            .Where(overload => overload.Parameters.Count != 0
                && FunctionOverloadFacts.CanBindReceiver(overload.Parameters[0].Type, target.Type, CanAssign))
            .ToArray();
        if (candidates.Length == 0)
        {
            return false;
        }

        if (candidates.Length == 1 && !candidates[0].IsGeneric)
        {
            var method = candidates[0];
            binding = new ExpressionBinding(
                method.ReturnType,
                NamedType: ResolveNamedTypeSymbol(method.ReturnType),
                Function: method,
                DiagnosticName: $"method '{memberName}'",
                Receiver: target);
            return true;
        }

        binding = new ExpressionBinding(
            StarkTypeSymbols.Error,
            DiagnosticName: $"method overload group '{sourceName}'",
            Receiver: target,
            OverloadSourceName: sourceName);
        return true;
    }

    private static bool TryGetValueTextConversionSourceName(string memberName, out string sourceName)
    {
        sourceName = memberName switch
        {
            "ToAscii" => "System.Text.ToAscii",
            "ToUnicode" => "System.Text.ToUnicode",
            _ => string.Empty
        };

        return sourceName.Length != 0;
    }

    private ExpressionBinding ResolveValue(
        string name,
        IToken token,
        Scope scope,
        bool allowFunctionReference,
        StarkTypeSymbol? expectedType)
    {
        if (scope.TryLookup(name, out var local))
        {
            var localUsesReadonlyProjectionSemantics = local.UsesFrozenProjectionSemantics
                || local.HasConstProvenance
                || local.BindingKind == GlobalBindingKind.Const;
            var expressionType = localUsesReadonlyProjectionSemantics
                ? GetConstProvenanceViewType(local.Type)
                : local.Type;
            var hasConstProvenance = local.HasConstProvenance
                || local.BindingKind == GlobalBindingKind.Const;
            var memoryRootKey = local.MemoryRootKey ?? name;
            var memoryRootIsIndependentStorage = local.MemoryRootIsIndependentStorage
                || IsLocalBindingIndependentStorage(local);
            if (local.BindingKind is not null)
            {
                RecordGlobalReference(local.Name, Location(token));
                return new ExpressionBinding(
                    expressionType,
                    IsAssignable: local.IsMutable,
                    NamedType: ResolveNamedTypeSymbol(expressionType),
                    DiagnosticName: local.IsConstant ? $"constant '{name}'" : $"variable '{name}'",
                    IsAddressable: true,
                    IsAddressMutable: local.IsMutable,
                    RootGlobalName: name,
                    RootGlobalBindingKind: local.BindingKind,
                    HasConstProvenance: hasConstProvenance,
                    AssignmentErrorMessage: local.IsMutable
                        ? null
                        : DescribeGlobalRebindingError(name, local.BindingKind.Value),
                    MemoryRootKey: memoryRootKey,
                    MemoryRootIsIndependentStorage: memoryRootIsIndependentStorage);
            }

            var canAssignLocal = CanAssignToLocal(local);
            return new ExpressionBinding(
                expressionType,
                IsAssignable: canAssignLocal,
                NamedType: ResolveNamedTypeSymbol(expressionType),
                DiagnosticName: local.IsConstant ? $"constant '{name}'" : $"variable '{name}'",
                IsAddressable: true,
                IsAddressMutable: CanFormMutableAddressFromLocal(local),
                AssignmentErrorMessage: canAssignLocal
                    ? null
                    : local.IsConstant
                        ? $"Cannot assign to constant '{name}'."
                        : $"Cannot assign to immutable local '{name}'.",
                UsesFrozenProjectionSemantics: localUsesReadonlyProjectionSemantics,
                HasConstProvenance: hasConstProvenance,
                MemoryRootKey: memoryRootKey,
                MemoryRootIsIndependentStorage: memoryRootIsIndependentStorage);
        }

        if (TryResolveGlobalBySourceName(name, out var global, out var ambiguousGlobalNames))
        {
            RecordGlobalReference(global.Name, Location(token));
            return new ExpressionBinding(
                global.Type,
                IsAssignable: global.IsMutable,
                NamedType: ResolveNamedTypeSymbol(global.Type),
                DiagnosticName: global.IsConstant ? $"constant '{name}'" : $"variable '{name}'",
                IsAddressable: true,
                IsAddressMutable: global.IsMutable,
                RootGlobalName: global.Name,
                RootGlobalBindingKind: global.BindingKind,
                HasConstProvenance: global.BindingKind == GlobalBindingKind.Const,
                AssignmentErrorMessage: global.IsMutable
                    ? null
                    : DescribeGlobalRebindingError(global.Name, global.BindingKind ?? GlobalBindingKind.Immutable),
                MemoryRootKey: name,
                MemoryRootIsIndependentStorage: IsLocalBindingIndependentStorage(global));
        }

        if (ambiguousGlobalNames.Count > 0)
        {
            ReportError(
                "STK3003",
                $"Imported symbol '{name}' is ambiguous between {string.Join(", ", ambiguousGlobalNames)}. Use a fully qualified name.",
                token);
            return new ExpressionBinding(StarkTypeSymbols.Error);
        }

        if (TryGetFunctionOverloads(name, out var functions))
        {
            if (IsTraitMethodFunctionName(name))
            {
                ReportError(
                    "STK3013",
                    $"Trait method '{name}' is a compile-time-only contract and cannot be called directly.",
                    token);
                return new ExpressionBinding(StarkTypeSymbols.Error);
            }

            if (!TryFilterDirectCallableTypeMemberFunctions(name, functions, token, out functions))
            {
                return new ExpressionBinding(StarkTypeSymbols.Error);
            }

            if (expectedType?.Kind == StarkTypeKind.FunctionPointer)
            {
                return ResolveFunctionPointerPromotion(name, functions, expectedType, token);
            }

            if (expectedType?.Kind == StarkTypeKind.Closure)
            {
                return ResolveClosureFunctionPromotion(name, functions, expectedType, token);
            }

            if (!allowFunctionReference)
            {
                ReportError(
                    "STK3012",
                    functions.Count == 1
                        ? $"Function '{name}' must be called before its value can be used."
                        : $"Overload group '{name}' must be called before its value can be used.",
                    token);
                return new ExpressionBinding(StarkTypeSymbols.Error);
            }

            if (functions.Count == 1 && !functions[0].IsGeneric)
            {
                var function = functions[0];
                return new ExpressionBinding(function.ReturnType, Function: function, DiagnosticName: $"function '{name}'");
            }

            return new ExpressionBinding(
                StarkTypeSymbols.Error,
                DiagnosticName: $"overload group '{name}'",
                OverloadSourceName: name);
        }

        if (TryResolveNamedTypeBySourceName(name, out var namedType, out var ambiguousTypeNames))
        {
            if (namedType.Kind is DeclarationKind.Struct or DeclarationKind.Record)
            {
                return new ExpressionBinding(
                    StarkTypeSymbols.Error,
                    NamespaceName: namedType.Name,
                    NamedType: namedType,
                    DiagnosticName: $"type '{name}'");
            }

            if (namedType.Kind == DeclarationKind.Doctrine)
            {
                if (!allowFunctionReference)
                {
                    ReportError(
                        "STK3013",
                        $"Doctrine '{name}' is compile-time-only and cannot be used as a runtime value. {DescribeNoDynamicDispatchPolicy()}",
                        token);
                    return new ExpressionBinding(StarkTypeSymbols.Error);
                }

                return new ExpressionBinding(
                    StarkTypeSymbols.Named(namedType.Name),
                    NamedType: namedType,
                    DiagnosticName: $"doctrine '{name}'");
            }

            if (namedType.Kind == DeclarationKind.Trait)
            {
                if (!allowFunctionReference)
                {
                    ReportError(
                        "STK3013",
                        $"Trait '{name}' is compile-time-only and cannot be used as a runtime value. {DescribeNoDynamicDispatchPolicy()}",
                        token);
                    return new ExpressionBinding(StarkTypeSymbols.Error);
                }

                return new ExpressionBinding(
                    StarkTypeSymbols.Named(namedType.Name),
                    NamedType: namedType,
                    DiagnosticName: $"trait '{name}'");
            }
        }

        if (ambiguousTypeNames.Count > 0)
        {
            ReportError(
                "STK3003",
                $"Imported type name '{name}' is ambiguous between {string.Join(", ", ambiguousTypeNames)}. Use a fully qualified name.",
                token);
            return new ExpressionBinding(StarkTypeSymbols.Error);
        }

        if (TryResolveEnumCaseReference(name, out var enumType, out var enumTypeSymbol, out var variant))
        {
            return CreateEnumCaseValueBinding(name, enumTypeSymbol, enumType, variant, token, allowFunctionReference);
        }

        if (TryResolveNamedTypeBySourceName(name, out namedType) && namedType.Kind == DeclarationKind.Enum)
        {
            return new ExpressionBinding(StarkTypeSymbols.Error, NamespaceName: namedType.Name, DiagnosticName: $"enum '{name}'");
        }

        if (IsCompilerKnownNamespace(name))
        {
            return new ExpressionBinding(StarkTypeSymbols.Error, NamespaceName: name, DiagnosticName: $"module namespace '{name}'");
        }

        if (_moduleGraph.CanAccessModule(CurrentFunctionModuleName, name))
        {
            return new ExpressionBinding(StarkTypeSymbols.Error, NamespaceName: name, DiagnosticName: $"module '{name}'");
        }

        if (_moduleGraph.CanAccessModuleNamespace(CurrentFunctionModuleName, name))
        {
            return new ExpressionBinding(StarkTypeSymbols.Error, NamespaceName: name, DiagnosticName: $"module namespace '{name}'");
        }

        ReportError("STK3003", $"Unknown symbol '{name}'.", token);
        return new ExpressionBinding(StarkTypeSymbols.Error);
    }

    private ExpressionBinding ResolveFunctionPointerPromotion(
        string name,
        IReadOnlyList<TypedFunctionSignature> functions,
        StarkTypeSymbol targetType,
        IToken token)
    {
        var matchingCandidates = functions
            .Where(static function => !function.IsGeneric)
            .Where(function => TypeCompatibilityFacts.AreFunctionPointerTypesAssignable(targetType, FunctionPointerTypeForSignature(function)))
            .ToArray();
        var candidates = matchingCandidates
            .Where(function => !function.IsUnsafe || targetType.FunctionPointerIsUnsafe)
            .ToArray();
        var unsafeRequirementCandidates = !targetType.FunctionPointerIsUnsafe
            ? functions
                .Where(static function => !function.IsGeneric && function.IsUnsafe)
                .Where(function => TypeCompatibilityFacts.AreFunctionPointerTypesAssignable(
                    BuildUnsafeFunctionPointerType(targetType),
                    FunctionPointerTypeForSignature(function)))
                .ToArray()
            : [];

        if (candidates.Length == 1)
        {
            var function = CacheFunctionInstantiation(candidates[0]);
            var location = Location(token);
            _functionPointerPromotions.Add(new FunctionPointerPromotionTypingRecord(
                function,
                targetType,
                location,
                _currentFunctionName));
            RecordFunctionInstantiationTrigger(function, location);
            RecordAddressTakenFunction(function, location);
            return new ExpressionBinding(
                targetType,
                Function: function,
                DiagnosticName: $"function item '{function.DisplaySourceName}'");
        }

        if (candidates.Length == 0 && (matchingCandidates.Any(static function => function.IsUnsafe) || unsafeRequirementCandidates.Length > 0))
        {
            ReportError(
                "STK3024",
                $"Unsafe function item '{name}' cannot be promoted to ordinary function pointer '{targetType.DisplayName}' because that pointer type does not carry an unsafe requirement. Call the function directly inside an unsafe block, or wrap it in a safe function that checks the required invariants.",
                token);
            return new ExpressionBinding(StarkTypeSymbols.Error);
        }

        if (candidates.Length == 0)
        {
            ReportError(
                "STK3002",
                $"Function item '{name}' cannot be promoted to '{targetType.DisplayName}'.",
                token);
            return new ExpressionBinding(StarkTypeSymbols.Error);
        }

        ReportError(
            "STK3002",
            $"Function item '{name}' is ambiguous for target '{targetType.DisplayName}'.",
            token);
        return new ExpressionBinding(StarkTypeSymbols.Error);
    }

    private static StarkTypeSymbol BuildUnsafeFunctionPointerType(StarkTypeSymbol type)
    {
        if (type.FunctionPointerKind is not { } functionKind
            || type.FunctionPointerReturnType is not { } returnType
            || type.FunctionPointerParameterTypes is not { } parameterTypes)
        {
            return type;
        }

        return StarkTypeSymbols.FunctionPointer(
            functionKind,
            returnType,
            parameterTypes,
            type.FunctionPointerDisjointParameterGroups,
            type.FunctionPointerOverlapParameterGroups,
            type.FunctionPointerSameParameterGroups,
            type.FunctionPointerParameterRawPointerElementCountExpressions,
            type.FunctionPointerAbi,
            isUnsafe: true);
    }

    private ExpressionBinding ResolveClosureFunctionPromotion(
        string name,
        IReadOnlyList<TypedFunctionSignature> functions,
        StarkTypeSymbol targetType,
        IToken token)
    {
        var matchingCandidates = functions
            .Where(static function => !function.IsGeneric)
            .Where(function => TypeCompatibilityFacts.AreClosureTypesAssignable(
                targetType,
                TypeCompatibilityFacts.ClosureTypeForSignature(
                    function,
                    targetType.ClosureStorageKind,
                    targetType.ClosureCallCapability)))
            .ToArray();
        var candidates = matchingCandidates
            .Where(function => !function.IsUnsafe || _unsafeDepth != 0)
            .ToArray();

        if (candidates.Length == 1)
        {
            var function = CacheFunctionInstantiation(candidates[0]);
            var location = Location(token);
            var enclosingFunctionName = _currentFunctionName ?? "module";
            var adapterFunctionName = CallableValueFacts.BuildClosureFunctionAdapterName(enclosingFunctionName, location);
            var promotion = new ClosureFunctionPromotionTypingRecord(
                function,
                targetType,
                adapterFunctionName,
                location,
                _currentFunctionName);
            _closureFunctionPromotions.Add(promotion);
            RecordFunctionInstantiationTrigger(function, location);
            _functions.TryAdd(adapterFunctionName, CallableValueFacts.BuildClosureFunctionAdapterSignature(promotion));
            return new ExpressionBinding(
                targetType,
                Function: function,
                DiagnosticName: $"function item '{function.DisplaySourceName}'");
        }

        if (candidates.Length == 0 && matchingCandidates.Any(static function => function.IsUnsafe))
        {
            ReportError(
                "STK3024",
                $"Unsafe function item '{name}' cannot be promoted to closure '{targetType.DisplayName}' because that closure type does not carry an unsafe requirement. Call the function directly inside an unsafe block, or wrap it in a safe function that checks the required invariants.",
                token);
            return new ExpressionBinding(StarkTypeSymbols.Error);
        }

        if (candidates.Length == 0)
        {
            ReportError(
                "STK3002",
                $"Function item '{name}' cannot be promoted to closure '{targetType.DisplayName}'.",
                token);
            return new ExpressionBinding(StarkTypeSymbols.Error);
        }

        ReportError(
            "STK3002",
            $"Function item '{name}' is ambiguous for closure target '{targetType.DisplayName}'.",
            token);
        return new ExpressionBinding(StarkTypeSymbols.Error);
    }

    private static StarkTypeSymbol FunctionPointerTypeForSignature(TypedFunctionSignature function)
    {
        return TypeCompatibilityFacts.FunctionPointerTypeForSignature(function);
    }

    private void RecordAddressTakenFunction(TypedFunctionSignature function, SourceLocation location)
    {
        if (!_addressTakenFunctionNames.Add(function.Name))
        {
            return;
        }

        _addressTakenFunctions.Add(new AddressTakenFunctionTypingRecord(
            function,
            location,
            _currentFunctionName));
    }

    private bool TryFilterDirectCallableTypeMemberFunctions(
        string sourceName,
        IReadOnlyList<TypedFunctionSignature> functions,
        IToken token,
        out IReadOnlyList<TypedFunctionSignature> callableFunctions)
    {
        callableFunctions = functions;

        if (!IsStructOrRecordMemberFunctionSourceName(sourceName, out var typeName))
        {
            return true;
        }

        var staticFunctions = functions.Where(static function => function.IsStatic).ToArray();
        if (staticFunctions.Length > 0)
        {
            callableFunctions = staticFunctions;
            return true;
        }

        ReportError(
            "STK3014",
            $"Instance member function '{sourceName}' must be called through a value of type '{typeName}'.",
            token);
        callableFunctions = [];
        return false;
    }

    private bool TryFilterDirectCallableTypeMemberFunctions(
        string sourceName,
        IReadOnlyList<TypedFunctionSignature> functions,
        ParserRuleContext context,
        out IReadOnlyList<TypedFunctionSignature> callableFunctions)
    {
        callableFunctions = functions;

        if (!IsStructOrRecordMemberFunctionSourceName(sourceName, out var typeName))
        {
            return true;
        }

        var staticFunctions = functions.Where(static function => function.IsStatic).ToArray();
        if (staticFunctions.Length > 0)
        {
            callableFunctions = staticFunctions;
            return true;
        }

        ReportError(
            "STK3014",
            $"Instance member function '{sourceName}' must be called through a value of type '{typeName}'.",
            context);
        callableFunctions = [];
        return false;
    }

    private bool IsStructOrRecordMemberFunctionSourceName(string sourceName, out string typeName)
    {
        typeName = string.Empty;
        var separator = sourceName.LastIndexOf('.');
        if (separator <= 0)
        {
            return false;
        }

        typeName = sourceName[..separator];
        return TryResolveNamedTypeBySourceName(typeName, out var namedType)
            && namedType.Kind is DeclarationKind.Struct or DeclarationKind.Record;
    }

    private bool TryGetFunctionOverloads(string sourceName, out IReadOnlyList<TypedFunctionSignature> overloads)
    {
        return TryGetFunctionOverloads(sourceName, CurrentFunctionModuleName, out overloads);
    }

    private bool TryGetFunctionOverloads(
        string sourceName,
        string currentModuleName,
        out IReadOnlyList<TypedFunctionSignature> overloads)
    {
        if (_functionOverloads.TryGetValue(sourceName, out var candidates))
        {
            overloads = candidates;
            return true;
        }

        if (TryResolveTypeQualifiedMemberSourceName(sourceName, out var resolvedMemberSourceName)
            && _functionOverloads.TryGetValue(resolvedMemberSourceName, out candidates))
        {
            overloads = candidates;
            return true;
        }

        if (!sourceName.Contains('.', StringComparison.Ordinal)
            && _functionOverloads.TryGetValue($"{currentModuleName}.{sourceName}", out candidates))
        {
            overloads = candidates;
            return true;
        }

        if (!sourceName.Contains('.', StringComparison.Ordinal))
        {
            var importedCandidates = new List<TypedFunctionSignature>();
            foreach (var candidateName in _moduleGraph.EnumerateAccessibleModuleQualifiedNames(currentModuleName, sourceName))
            {
                if (_functionOverloads.TryGetValue(candidateName, out candidates))
                {
                    importedCandidates.AddRange(candidates);
                }
            }

            if (importedCandidates.Count > 0)
            {
                overloads = importedCandidates;
                return true;
            }
        }

        overloads = [];
        return false;
    }

    private bool TryResolveTypeQualifiedMemberSourceName(string sourceName, out string resolvedSourceName)
    {
        resolvedSourceName = string.Empty;
        var separator = sourceName.LastIndexOf('.');
        if (separator <= 0)
        {
            return false;
        }

        var qualifier = sourceName[..separator];
        if (!TryResolveNamedTypeBySourceName(qualifier, out var namedType))
        {
            return false;
        }

        resolvedSourceName = $"{StarkTypeSymbols.GetGenericBaseName(namedType.Name)}.{sourceName[(separator + 1)..]}";
        return !string.Equals(resolvedSourceName, sourceName, StringComparison.Ordinal);
    }

    private void ReportOverloadResolutionFailure(
        string sourceName,
        IReadOnlyList<StarkTypeSymbol> argumentTypes,
        OverloadResolutionResult resolution,
        ParserRuleContext context)
    {
        var argumentsText = $"({string.Join(", ", argumentTypes.Select(static type => type.DisplayName))})";
        if (resolution.Failure == OverloadResolutionFailureKind.NoMatch)
        {
            ReportError(
                "STK3021",
                $"No overload of '{sourceName}' matches argument types {argumentsText}. Available overloads: {string.Join(", ", resolution.Candidates.Select(FunctionOverloadFacts.FormatSignature))}.",
                context);
            return;
        }

        if (resolution.Failure == OverloadResolutionFailureKind.Ambiguous)
        {
            ReportError(
                "STK3022",
                $"Call to overloaded function '{sourceName}' is ambiguous for argument types {argumentsText}. Matching overloads: {string.Join(", ", resolution.Candidates.Select(FunctionOverloadFacts.FormatSignature))}.",
                context);
        }
    }

    private ExpressionBinding EvaluateLiteral(StarkParser.LiteralContext literal)
    {
        StarkTypeSymbol type;
        string? textLiteral = null;
        TextLiteralKind? textLiteralKind = null;
        BigInteger? integerLiteralValue = null;

        if (literal.signedIntegerLiteral() is { } integerLiteral)
        {
            var value = ParseSignedIntegerLiteral(integerLiteral);
            type = InferIntegerLiteralType(value);
            integerLiteralValue = value;
        }
        else if (literal.FloatLiteral() is not null)
        {
            type = StarkTypeSymbols.Float(32);
        }
        else if (literal.StringLiteral() is { } stringLiteral)
        {
            type = InferStringLiteralType(stringLiteral.GetText());
            textLiteral = stringLiteral.GetText();
            textLiteralKind = TextLiteralKind.String;
        }
        else if (literal.CharacterLiteral() is { } charLiteral)
        {
            type = InferCharacterLiteralType(charLiteral.GetText());
            textLiteral = charLiteral.GetText();
            textLiteralKind = TextLiteralKind.Character;
        }
        else if (literal.TRUE() is not null || literal.FALSE() is not null)
        {
            type = StarkTypeSymbols.Bool;
        }
        else
        {
            type = StarkTypeSymbols.Null;
        }

        _literals.Add(new LiteralTypingRecord(literal.GetText(), type, Location(literal)));
        var textLiteralHasMemoryRoot = textLiteral is not null && type.Kind is (StarkTypeKind.Ascii or StarkTypeKind.Unicode);
        return new ExpressionBinding(
            type,
            TextLiteral: textLiteral,
            TextLiteralKind: textLiteralKind,
            HasConstProvenance: textLiteralHasMemoryRoot,
            MemoryRootKey: textLiteralHasMemoryRoot ? BuildLiteralMemoryRootKey(literal) : null,
            MemoryRootIsIndependentStorage: textLiteralHasMemoryRoot,
            IntegerLiteralValue: integerLiteralValue);
    }

    private ExpressionBinding EvaluateInterpolatedTextLiteral(
        StarkParser.LiteralContext literal,
        Scope scope,
        StarkTypeSymbol? expectedType)
    {
        var stringLiteral = literal.StringLiteral();
        if (stringLiteral is null)
        {
            return new ExpressionBinding(StarkTypeSymbols.Error);
        }

        if (!InterpolatedText.TryParse(stringLiteral.GetText(), out var segments, out var diagnostics))
        {
            foreach (var diagnostic in diagnostics)
            {
                ReportError("STK3002", diagnostic.Message, literal);
            }

            return new ExpressionBinding(StarkTypeSymbols.Error);
        }

        var hasError = false;
        var isFixedTextStorageInterpolation = expectedType is not null
            && IsTextBufferType(expectedType)
            && _fixedTextStorageInterpolatedLiterals.Contains(literal);
        foreach (var hole in segments.OfType<InterpolatedTextHoleSegment>())
        {
            var binding = EvaluateExpression(
                hole.Expression,
                scope,
                allowFunctionReference: false);
            hasError |= binding.Type.Kind == StarkTypeKind.Error;

            if (isFixedTextStorageInterpolation
                && binding.Type.Kind != StarkTypeKind.Error
                && !CanUseFixedTextInterpolationHole(expectedType!, binding.Type, out var diagnostic))
            {
                ReportError("STK3002", diagnostic, hole.Expression);
                hasError = true;
            }
        }

        if (hasError)
        {
            return new ExpressionBinding(StarkTypeSymbols.Error);
        }

        if (isFixedTextStorageInterpolation)
        {
            _boundOperations.Add(new BoundTextInterpolationOperation(
                expectedType!,
                segments.Count,
                segments.OfType<InterpolatedTextHoleSegment>().Count(),
                UsesFixedStorage: true,
                Location(literal),
                _currentFunctionName));
            return new ExpressionBinding(
                expectedType!,
                NamedType: ResolveNamedTypeSymbol(expectedType!),
                DiagnosticName: "fixed-capacity interpolated text");
        }

        if (!InterpolatedText.TryFold(
                segments,
                CreateCompileTimeEvaluationServices(scope),
                out var foldedLiteral,
                out var foldDiagnostic))
        {
            ReportError("STK3002", foldDiagnostic.Message, literal);
            return new ExpressionBinding(StarkTypeSymbols.Error);
        }

        var type = InferStringLiteralType(foldedLiteral);
        _literals.Add(new LiteralTypingRecord(literal.GetText(), type, Location(literal)));
        _boundOperations.Add(new BoundTextInterpolationOperation(
            type,
            segments.Count,
            segments.OfType<InterpolatedTextHoleSegment>().Count(),
            UsesFixedStorage: false,
            Location(literal),
            _currentFunctionName));
        var textLiteralHasMemoryRoot = type.Kind is StarkTypeKind.Ascii or StarkTypeKind.Unicode;
        var bindingResult = new ExpressionBinding(
            type,
            TextLiteral: foldedLiteral,
            TextLiteralKind: TextLiteralKind.String,
            HasConstProvenance: textLiteralHasMemoryRoot,
            MemoryRootKey: textLiteralHasMemoryRoot ? BuildLiteralMemoryRootKey(literal) : null,
            MemoryRootIsIndependentStorage: textLiteralHasMemoryRoot);

        if (expectedType is not null
            && IsTextType(expectedType)
            && CanExplicitlyConvertTextLiteral(expectedType, bindingResult))
        {
            return bindingResult with { Type = expectedType };
        }

        return bindingResult;
    }

    private static string BuildLiteralMemoryRootKey(StarkParser.LiteralContext literal)
    {
        return $"__literal_{literal.Start.Line.ToString(CultureInfo.InvariantCulture)}_{(literal.Start.Column + 1).ToString(CultureInfo.InvariantCulture)}";
    }

    private StarkTypeSymbol ResolveReturnType(
        StarkParser.ReturnTypeContext returnType,
        ISet<string>? genericParameters,
        string? currentModuleName = null,
        IReadOnlyDictionary<string, ComptimeGenericParameterSymbol>? comptimeGenericParameters = null)
    {
        return EnsureMonomorphizedType(
            _typeResolver!.ResolveReturnType(
                returnType,
                genericParameters ?? _currentFunctionGenericParameters,
                currentModuleName,
                comptimeGenericParameters ?? _currentFunctionComptimeGenericParameters),
            Location(returnType));
    }

    private StarkTypeSymbol ResolveType(
        StarkParser.Type_Context type,
        ISet<string>? genericParameters = null,
        string? currentModuleName = null,
        IReadOnlyDictionary<string, ComptimeGenericParameterSymbol>? comptimeGenericParameters = null)
    {
        return EnsureMonomorphizedType(
            _typeResolver!.ResolveType(
                type,
                genericParameters ?? _currentFunctionGenericParameters,
                currentModuleName,
                comptimeGenericParameters ?? _currentFunctionComptimeGenericParameters),
            Location(type));
    }

    private StarkTypeSymbol ResolveParameterType(
        StarkParser.Type_Context type,
        ISet<string>? genericParameters,
        string? currentModuleName,
        out string? rawPointerElementCountExpression,
        IReadOnlyDictionary<string, ComptimeGenericParameterSymbol>? comptimeGenericParameters = null)
    {
        return EnsureMonomorphizedType(
            _typeResolver!.ResolveParameterType(
                type,
                genericParameters ?? _currentFunctionGenericParameters,
                currentModuleName,
                out rawPointerElementCountExpression,
                comptimeGenericParameters ?? _currentFunctionComptimeGenericParameters),
            Location(type));
    }

    private StarkTypeSymbol ResolveQualifiedType(string qualifiedName, ISet<string>? genericParameters, IToken token, string? currentModuleName = null)
    {
        return _typeResolver!.ResolveQualifiedType(qualifiedName, genericParameters ?? _currentFunctionGenericParameters, token, currentModuleName);
    }

    private StarkTypeSymbol ResolveGenericQualifiedName(StarkParser.GenericQualifiedNameContext genericQualifiedName)
    {
        var baseName = genericQualifiedName.qualifiedName().GetText();
        if (_typeResolver!.TryResolveGenericTypeAlias(
                baseName,
                CurrentFunctionModuleName,
                genericQualifiedName.qualifiedName().Start,
                genericQualifiedName.typeArgumentList(),
                _currentFunctionGenericParameters,
                _currentFunctionComptimeGenericParameters,
                out var aliasType))
        {
            return aliasType.Kind == StarkTypeKind.Error
                ? StarkTypeSymbols.Error
                : EnsureMonomorphizedType(aliasType, Location(genericQualifiedName));
        }

        var baseType = ResolveQualifiedType(baseName, genericParameters: null, genericQualifiedName.qualifiedName().Start, CurrentFunctionModuleName);
        if (baseType.Kind == StarkTypeKind.Error)
        {
            return StarkTypeSymbols.Error;
        }

        if (!_namedTypes.TryGetValue(baseType.NamedType ?? baseName, out var namedType))
        {
            ReportError("STK3004", $"Unknown generic type '{baseName}'.", genericQualifiedName);
            return StarkTypeSymbols.Error;
        }

        var genericArguments = GenericArgumentSyntaxFacts.Resolve(
            genericQualifiedName.typeArgumentList(),
            namedType.GenericParams,
            namedType.ComptimeGenericParams,
            typeArgument => ResolveType(typeArgument, currentModuleName: CurrentFunctionModuleName),
            ReportError,
            visibleComptimeParameters: _currentFunctionComptimeGenericParameters);
        if (genericArguments.TypeArguments.Any(static type => type.Kind == StarkTypeKind.Error))
        {
            return StarkTypeSymbols.Error;
        }

        return EnsureMonomorphizedType(
            StarkTypeSymbols.GenericInstantiation(
                baseType.NamedType ?? baseName,
                genericArguments.TypeArguments,
                genericArguments.ComptimeValueArguments),
            Location(genericQualifiedName));
    }

    private ExpressionBinding ResolveGenericQualifiedNameValue(
        StarkParser.GenericQualifiedNameContext genericQualifiedName,
        Scope scope,
        bool allowFunctionReference,
        StarkTypeSymbol? expectedType)
    {
        var baseName = genericQualifiedName.qualifiedName().GetText();
        if (TryResolveCompileTimeStructuralFactReference(genericQualifiedName, baseName, scope, out var structuralFactBinding))
        {
            if (!allowFunctionReference)
            {
                ReportError(
                    "STK3054",
                    $"Compile-time structural fact '{baseName}' may only be used inside a `comptime` expression or block.",
                    genericQualifiedName);
                return new ExpressionBinding(StarkTypeSymbols.Error);
            }

            return structuralFactBinding;
        }

        if (TryGetFunctionOverloads(baseName, out var overloads))
        {
            if (!allowFunctionReference
                && expectedType?.Kind is not (StarkTypeKind.FunctionPointer or StarkTypeKind.Closure))
            {
                ReportError("STK3012", $"Function '{baseName}' must be called before its value can be used.", genericQualifiedName);
                return new ExpressionBinding(StarkTypeSymbols.Error);
            }

            var syntaxArgumentCount = genericQualifiedName.typeArgumentList().genericArgument().Length;
            var matchingCandidates = overloads
                .Where(candidate => candidate.GenericParams.Count + candidate.ComptimeGenericParams.Count == syntaxArgumentCount)
                .ToArray();
            if (matchingCandidates.Length == 0)
            {
                ReportError(
                    "STK3019",
                    $"No overload of '{baseName}' accepts {syntaxArgumentCount} explicit generic argument(s).",
                    genericQualifiedName.typeArgumentList());
                return new ExpressionBinding(StarkTypeSymbols.Error);
            }

            var instantiatedCandidates = new List<TypedFunctionSignature>(matchingCandidates.Length);
            foreach (var candidate in matchingCandidates)
            {
                var genericArguments = GenericArgumentSyntaxFacts.Resolve(
                    genericQualifiedName.typeArgumentList(),
                    candidate.GenericParams,
                    candidate.ComptimeGenericParams,
                    typeArgument => ResolveType(typeArgument, currentModuleName: CurrentFunctionModuleName),
                    ReportError,
                    CreateCompileTimeEvaluationServices(scope),
                    _currentFunctionComptimeGenericParameters);
                if (genericArguments.TypeArguments.Any(static type => type.Kind == StarkTypeKind.Error))
                {
                    continue;
                }

                instantiatedCandidates.Add(FunctionOverloadFacts.InstantiateSignature(
                    candidate,
                    genericArguments.TypeArguments,
                    candidate.Name,
                    ResolveAssociatedTypeForSubstitution,
                    genericArguments.ComptimeValueArguments));
            }

            if (instantiatedCandidates.Count == 0)
            {
                return new ExpressionBinding(StarkTypeSymbols.Error);
            }

            if (expectedType?.Kind == StarkTypeKind.FunctionPointer && instantiatedCandidates.Count == 1)
            {
                return ResolveFunctionPointerPromotion(baseName, instantiatedCandidates, expectedType, genericQualifiedName.Start);
            }

            if (expectedType?.Kind == StarkTypeKind.Closure && instantiatedCandidates.Count == 1)
            {
                return ResolveClosureFunctionPromotion(baseName, instantiatedCandidates, expectedType, genericQualifiedName.Start);
            }

            return instantiatedCandidates.Count == 1
                ? new ExpressionBinding(
                    instantiatedCandidates[0].ReturnType,
                    Function: CacheFunctionInstantiation(instantiatedCandidates[0]),
                    DiagnosticName: $"function '{instantiatedCandidates[0].DisplaySourceName}'")
                : new ExpressionBinding(
                    StarkTypeSymbols.Error,
                    DiagnosticName: $"overload group '{baseName}'",
                    OverloadCandidates: instantiatedCandidates.ToArray());
        }

        var targetType = ResolveGenericQualifiedName(genericQualifiedName);
        var namedType = ResolveNamedTypeSymbol(targetType);
        return new ExpressionBinding(
            StarkTypeSymbols.Error,
            NamespaceName: targetType.NamedType,
            NamedType: namedType,
            DiagnosticName: $"type '{targetType.DisplayName}'");
    }

    private bool TryResolveCompileTimeStructuralFactReference(
        StarkParser.GenericQualifiedNameContext genericQualifiedName,
        string baseName,
        Scope scope,
        out ExpressionBinding binding)
    {
        binding = default!;
        if (!CompileTimeStructuralFacts.TryGetFactKind(baseName, out _))
        {
            return false;
        }

        if (!CompileTimeStructuralFacts.TryResolveArguments(
                baseName,
                genericQualifiedName,
                typeArgument => ResolveType(
                    typeArgument,
                    _currentFunctionGenericParameters,
                    CurrentFunctionModuleName,
                    _currentFunctionComptimeGenericParameters),
                ReportError,
                CreateCompileTimeEvaluationServices(scope),
                _currentFunctionComptimeGenericParameters,
                comptimeValueSubstitution: null,
                out var structuralArguments))
        {
            binding = new ExpressionBinding(StarkTypeSymbols.Error);
            return true;
        }

        if (HasErrorStructuralFactTypeArgument(structuralArguments)
            || !ValidateCompileTimeStructuralFactArguments(baseName, structuralArguments, genericQualifiedName))
        {
            binding = new ExpressionBinding(StarkTypeSymbols.Error);
            return true;
        }

        CompileTimeStructuralFacts.TryCreateSignature(baseName, structuralArguments, out var signature);
        binding = new ExpressionBinding(
            signature.ReturnType,
            Function: signature,
            DiagnosticName: $"compile-time structural fact '{baseName}'");
        return true;
    }

    private static bool HasErrorStructuralFactTypeArgument(CompileTimeStructuralFactArguments arguments)
    {
        return arguments.TargetType.Kind == StarkTypeKind.Error
            || arguments.AdditionalTypeArguments.Any(static argument => argument.Kind == StarkTypeKind.Error);
    }

    private bool ValidateStructuralFactTypeArgumentIndex(
        string factName,
        StarkTypeSymbol type,
        BigInteger index,
        string subjectDescription,
        ParserRuleContext context)
    {
        var coreType = StarkTypeSymbols.WithQualifiers(
            type,
            borrowKind: StarkBorrowKind.None,
            accessKind: StarkAccessKind.None,
            initializationKind: StarkInitializationKind.None,
            isMutableView: false);
        var typeArgumentCount = coreType.TypeArguments?.Count ?? 0;
        if (index >= BigInteger.Zero && index < typeArgumentCount)
        {
            return true;
        }

        ReportError(
            "STK3054",
            $"Compile-time structural fact '{factName}' type argument index '{index}' is out of range for {subjectDescription} with {typeArgumentCount} type argument(s).",
            context);
        return false;
    }

    private bool ValidateCompileTimeStructuralFactArguments(
        string factName,
        CompileTimeStructuralFactArguments arguments,
        ParserRuleContext context)
    {
        if (!CompileTimeStructuralFacts.TryGetFactKind(factName, out var kind)
            || arguments.ComptimeValueArguments.Any(static argument => argument.IsSymbolic)
            || arguments.TargetType.Kind == StarkTypeKind.Error
            || arguments.AdditionalTypeArguments.Any(static argument => argument.Kind == StarkTypeKind.Error))
        {
            return true;
        }

        if (kind is CompileTimeStructuralFactKind.Implements
            or CompileTimeStructuralFactKind.ImplementedTraitTypeIs)
        {
            if (arguments.AdditionalTypeArguments.Count != 1)
            {
                return true;
            }

            var traitType = arguments.AdditionalTypeArguments[0];
            if (ResolveNamedTypeDefinitionSymbol(traitType) is not { } traitSymbol)
            {
                return true;
            }

            if (traitSymbol.Kind != DeclarationKind.Trait)
            {
                ReportError(
                    "STK3054",
                    $"Compile-time structural fact '{factName}' requires a trait type as its second argument, but found '{traitType.DisplayName}'.",
                    context);
                return false;
            }
        }

        if (CompileTimeStructuralFacts.IsImplementedTraitIndexedFact(kind))
        {
            if ((ResolveNamedTypeSymbol(arguments.TargetType) ?? ResolveNamedTypeDefinitionSymbol(arguments.TargetType)) is not { } namedType)
            {
                ReportError(
                    "STK3054",
                    $"Compile-time structural fact '{factName}' requires a named type, but found '{arguments.TargetType.DisplayName}'.",
                    context);
                return false;
            }

            var index = arguments.ComptimeValueArguments[0].IntegerValue;
            if (index < BigInteger.Zero || index >= namedType.ImplementedTraits.Count)
            {
                ReportError(
                    "STK3054",
                    $"Compile-time structural fact '{factName}' implemented-trait index '{index}' is out of range for '{arguments.TargetType.DisplayName}' with {namedType.ImplementedTraits.Count} implemented trait(s).",
                    context);
                return false;
            }

            if (CompileTimeStructuralFacts.IsImplementedTraitTypeArgumentIndexedFact(kind))
            {
                var argumentIndex = arguments.ComptimeValueArguments[1].IntegerValue;
                var implementedTraitTypeArgumentCount =
                    (int)index < namedType.ImplementedTraitTypes.Count
                        ? namedType.ImplementedTraitTypes[(int)index].TypeArguments?.Count ?? 0
                        : 0;
                if (argumentIndex < BigInteger.Zero || argumentIndex >= implementedTraitTypeArgumentCount)
                {
                    ReportError(
                        "STK3054",
                        $"Compile-time structural fact '{factName}' implemented-trait type argument index '{argumentIndex}' is out of range for trait slot {index} of '{arguments.TargetType.DisplayName}' with {implementedTraitTypeArgumentCount} type argument(s).",
                        context);
                    return false;
                }
            }

            if (CompileTimeStructuralFacts.IsImplementedTraitComptimeArgumentIndexedFact(kind))
            {
                var argumentIndex = arguments.ComptimeValueArguments[1].IntegerValue;
                var implementedTraitComptimeArgumentCount =
                    (int)index < namedType.ImplementedTraitTypes.Count
                        ? namedType.ImplementedTraitTypes[(int)index].ComptimeValueArguments?.Count ?? 0
                        : 0;
                if (argumentIndex < BigInteger.Zero || argumentIndex >= implementedTraitComptimeArgumentCount)
                {
                    ReportError(
                        "STK3054",
                        $"Compile-time structural fact '{factName}' implemented-trait comptime argument index '{argumentIndex}' is out of range for trait slot {index} of '{arguments.TargetType.DisplayName}' with {implementedTraitComptimeArgumentCount} comptime argument(s).",
                        context);
                    return false;
                }
            }
        }

        if (kind is CompileTimeStructuralFactKind.TypeSize
            or CompileTimeStructuralFactKind.TypeAlign
            or CompileTimeStructuralFactKind.TypeIsZeroSized)
        {
            if (!TryResolveCompileTimeConcreteLayout(arguments.TargetType, out _))
            {
                if (arguments.TargetType.Kind is StarkTypeKind.Named or StarkTypeKind.AssociatedType
                    && ResolveNamedTypeSymbol(arguments.TargetType) is null)
                {
                    return true;
                }

                ReportError(
                    "STK3054",
                    $"Compile-time structural fact '{factName}' requires concrete layout for '{arguments.TargetType.DisplayName}'.",
                    context);
                return false;
            }
        }

        if (CompileTimeStructuralFacts.RequiresIntegerTarget(kind)
            || CompileTimeStructuralFacts.RequiresFloatTarget(kind))
        {
            var targetType = StarkTypeSymbols.WithQualifiers(
                arguments.TargetType,
                borrowKind: StarkBorrowKind.None,
                accessKind: StarkAccessKind.None,
                initializationKind: StarkInitializationKind.None,
                isMutableView: false);
            var expectsInteger = CompileTimeStructuralFacts.RequiresIntegerTarget(kind);
            var hasExpectedTarget = expectsInteger
                ? targetType.Kind == StarkTypeKind.Integer && targetType.BitWidth is not null
                : targetType.Kind == StarkTypeKind.Float && targetType.BitWidth is not null;
            if (!hasExpectedTarget)
            {
                if (targetType.Kind is StarkTypeKind.Named or StarkTypeKind.AssociatedType
                    && ResolveNamedTypeSymbol(targetType) is null)
                {
                    return true;
                }

                ReportError(
                    "STK3054",
                    $"Compile-time structural fact '{factName}' requires {(expectsInteger ? "an integer" : "a float")} type, but found '{arguments.TargetType.DisplayName}'.",
                    context);
                return false;
            }
        }

        if (CompileTimeStructuralFacts.RequiresRawPointerTarget(kind))
        {
            var targetType = StarkTypeSymbols.WithQualifiers(
                arguments.TargetType,
                borrowKind: StarkBorrowKind.None,
                accessKind: StarkAccessKind.None,
                initializationKind: StarkInitializationKind.None,
                isMutableView: false);
            if (targetType.Kind != StarkTypeKind.RawPointer)
            {
                if (targetType.Kind is StarkTypeKind.Named or StarkTypeKind.AssociatedType
                    && ResolveNamedTypeSymbol(targetType) is null)
                {
                    return true;
                }

                ReportError(
                    "STK3054",
                    $"Compile-time structural fact '{factName}' requires a raw pointer type, but found '{arguments.TargetType.DisplayName}'.",
                    context);
                return false;
            }
        }

        if (CompileTimeStructuralFacts.RequiresElementTypeTarget(kind))
        {
            var targetType = StarkTypeSymbols.WithQualifiers(
                arguments.TargetType,
                borrowKind: StarkBorrowKind.None,
                accessKind: StarkAccessKind.None,
                initializationKind: StarkInitializationKind.None,
                isMutableView: false);
            if (targetType.ElementType is null)
            {
                if (targetType.Kind is StarkTypeKind.Named or StarkTypeKind.AssociatedType
                    && ResolveNamedTypeSymbol(targetType) is null)
                {
                    return true;
                }

                ReportError(
                    "STK3054",
                    $"Compile-time structural fact '{factName}' requires an element-bearing type, but found '{arguments.TargetType.DisplayName}'.",
                    context);
                return false;
            }
        }

        if (CompileTimeStructuralFacts.RequiresFixedArrayTarget(kind))
        {
            var targetType = StarkTypeSymbols.WithQualifiers(
                arguments.TargetType,
                borrowKind: StarkBorrowKind.None,
                accessKind: StarkAccessKind.None,
                initializationKind: StarkInitializationKind.None,
                isMutableView: false);
            if (targetType.Kind != StarkTypeKind.FixedArray)
            {
                if (targetType.Kind is StarkTypeKind.Named or StarkTypeKind.AssociatedType
                    && ResolveNamedTypeSymbol(targetType) is null)
                {
                    return true;
                }

                ReportError(
                    "STK3054",
                    $"Compile-time structural fact '{factName}' requires a fixed-array type, but found '{arguments.TargetType.DisplayName}'.",
                    context);
                return false;
            }
        }

        if (kind == CompileTimeStructuralFactKind.DynTraitTargetTypeIs)
        {
            var targetType = StarkTypeSymbols.WithQualifiers(
                arguments.TargetType,
                borrowKind: StarkBorrowKind.None,
                accessKind: StarkAccessKind.None,
                initializationKind: StarkInitializationKind.None,
                isMutableView: false);
            if (targetType.Kind != StarkTypeKind.DynTrait)
            {
                if (targetType.Kind is StarkTypeKind.Named or StarkTypeKind.AssociatedType
                    && ResolveNamedTypeSymbol(targetType) is null)
                {
                    return true;
                }

                ReportError(
                    "STK3054",
                    $"Compile-time structural fact '{factName}' requires a dyn trait object type, but found '{arguments.TargetType.DisplayName}'.",
                    context);
                return false;
            }

            if (arguments.AdditionalTypeArguments.Count != 1)
            {
                return true;
            }

            var traitType = arguments.AdditionalTypeArguments[0];
            if (ResolveNamedTypeDefinitionSymbol(traitType) is not { } traitSymbol)
            {
                return true;
            }

            if (traitSymbol.Kind != DeclarationKind.Trait)
            {
                ReportError(
                    "STK3054",
                    $"Compile-time structural fact '{factName}' requires a trait type as its second argument, but found '{traitType.DisplayName}'.",
                    context);
                return false;
            }
        }

        if (kind != CompileTimeStructuralFactKind.MethodCount
            && CompileTimeStructuralFacts.IsMethodIndexedFact(kind))
        {
            var targetType = StarkTypeSymbols.WithQualifiers(
                arguments.TargetType,
                borrowKind: StarkBorrowKind.None,
                accessKind: StarkAccessKind.None,
                initializationKind: StarkInitializationKind.None,
                isMutableView: false);
            if (targetType.Kind != StarkTypeKind.Named)
            {
                ReportError(
                    "STK3054",
                    $"Compile-time structural fact '{factName}' requires a named type, but found '{arguments.TargetType.DisplayName}'.",
                    context);
                return false;
            }

            if (!TryResolveCompileTimeNamedTypeInModule(targetType, CurrentFunctionModuleName, out _))
            {
                return true;
            }

            var methods = ResolveCompileTimeMethodSignatures(targetType, CurrentFunctionModuleName);
            var methodIndex = arguments.ComptimeValueArguments[0].IntegerValue;
            if (methodIndex < BigInteger.Zero || methodIndex >= methods.Count)
            {
                ReportError(
                    "STK3054",
                    $"Compile-time structural fact '{factName}' method index '{methodIndex}' is out of range for '{arguments.TargetType.DisplayName}' with {methods.Count} method slot(s).",
                    context);
                return false;
            }

            var method = methods[(int)methodIndex];
            if (kind == CompileTimeStructuralFactKind.MethodParameterName
                || kind == CompileTimeStructuralFactKind.MethodParameterTypeIs
                || CompileTimeStructuralFacts.IsMethodParameterTypePredicate(kind)
                || CompileTimeStructuralFacts.IsMethodParameterTypeMetadataFact(kind)
                || CompileTimeStructuralFacts.IsMethodParameterTypeArgumentFact(kind)
                || CompileTimeStructuralFacts.IsMethodParameterRawPointerElementCountExpressionFact(kind))
            {
                var parameterIndex = arguments.ComptimeValueArguments[1].IntegerValue;
                if (parameterIndex < BigInteger.Zero || parameterIndex >= method.Parameters.Count)
                {
                    ReportError(
                        "STK3054",
                        $"Compile-time structural fact '{factName}' parameter index '{parameterIndex}' is out of range for method slot {methodIndex} of '{arguments.TargetType.DisplayName}' with {method.Parameters.Count} parameter(s).",
                        context);
                    return false;
                }
            }

            if (CompileTimeStructuralFacts.IsMethodReturnTypeArgumentFact(kind))
            {
                var argumentIndex = arguments.ComptimeValueArguments[1].IntegerValue;
                if (!ValidateStructuralFactTypeArgumentIndex(
                        factName,
                        method.ReturnType,
                        argumentIndex,
                        $"return type of method slot {methodIndex} of '{arguments.TargetType.DisplayName}'",
                        context))
                {
                    return false;
                }
            }

            if (CompileTimeStructuralFacts.IsMethodParameterTypeArgumentFact(kind))
            {
                var parameterIndex = arguments.ComptimeValueArguments[1].IntegerValue;
                var argumentIndex = arguments.ComptimeValueArguments[2].IntegerValue;
                if (!ValidateStructuralFactTypeArgumentIndex(
                        factName,
                        method.Parameters[(int)parameterIndex].Type,
                        argumentIndex,
                        $"parameter slot {parameterIndex} of method slot {methodIndex} of '{arguments.TargetType.DisplayName}'",
                        context))
                {
                    return false;
                }
            }

            if (CompileTimeStructuralFacts.IsMethodParameterMemoryFact(kind))
            {
                for (var position = 1; position <= 2; position++)
                {
                    var parameterIndex = arguments.ComptimeValueArguments[position].IntegerValue;
                    if (parameterIndex < BigInteger.Zero || parameterIndex >= method.Parameters.Count)
                    {
                        ReportError(
                            "STK3054",
                            $"Compile-time structural fact '{factName}' parameter index '{parameterIndex}' is out of range for method slot {methodIndex} of '{arguments.TargetType.DisplayName}' with {method.Parameters.Count} parameter(s).",
                            context);
                        return false;
                    }
                }
            }

            if (kind is CompileTimeStructuralFactKind.MethodGenericParameterName
                or CompileTimeStructuralFactKind.MethodGenericParameterTraitBoundCount
                or CompileTimeStructuralFactKind.MethodComptimeGenericParameterName
                or CompileTimeStructuralFactKind.MethodComptimeGenericParameterTypeIs
                || CompileTimeStructuralFacts.IsMethodComptimeGenericParameterTypePredicate(kind)
                || CompileTimeStructuralFacts.IsMethodComptimeGenericParameterTypeMetadataFact(kind))
            {
                var genericParameterIndex = arguments.ComptimeValueArguments[1].IntegerValue;
                var genericParameterCount = kind is CompileTimeStructuralFactKind.MethodComptimeGenericParameterName
                    or CompileTimeStructuralFactKind.MethodComptimeGenericParameterTypeIs
                    || CompileTimeStructuralFacts.IsMethodComptimeGenericParameterTypePredicate(kind)
                    || CompileTimeStructuralFacts.IsMethodComptimeGenericParameterTypeMetadataFact(kind)
                        ? method.ComptimeGenericParams.Count
                        : method.GenericParams.Count;
                var genericParameterKind = kind is CompileTimeStructuralFactKind.MethodComptimeGenericParameterName
                    or CompileTimeStructuralFactKind.MethodComptimeGenericParameterTypeIs
                    || CompileTimeStructuralFacts.IsMethodComptimeGenericParameterTypePredicate(kind)
                    || CompileTimeStructuralFacts.IsMethodComptimeGenericParameterTypeMetadataFact(kind)
                        ? "comptime generic parameter"
                        : "generic parameter";
                if (genericParameterIndex < BigInteger.Zero || genericParameterIndex >= genericParameterCount)
                {
                    ReportError(
                        "STK3054",
                        $"Compile-time structural fact '{factName}' {genericParameterKind} index '{genericParameterIndex}' is out of range for method slot {methodIndex} of '{arguments.TargetType.DisplayName}' with {genericParameterCount} {genericParameterKind}(s).",
                        context);
                    return false;
                }
            }

            if (CompileTimeStructuralFacts.IsMethodGenericParameterTraitBoundIndexedFact(kind))
            {
                var genericParameterIndex = arguments.ComptimeValueArguments[1].IntegerValue;
                if (genericParameterIndex < BigInteger.Zero || genericParameterIndex >= method.GenericParams.Count)
                {
                    ReportError(
                        "STK3054",
                        $"Compile-time structural fact '{factName}' generic parameter index '{genericParameterIndex}' is out of range for method slot {methodIndex} of '{arguments.TargetType.DisplayName}' with {method.GenericParams.Count} generic parameter(s).",
                        context);
                    return false;
                }

                var parameterName = method.GenericParams[(int)genericParameterIndex];
                var boundCount = 0;
                foreach (var constraint in method.Constraints)
                {
                    if (string.Equals(constraint.ParameterName, parameterName, StringComparison.Ordinal))
                    {
                        boundCount = constraint.BoundTraits.Count;
                        break;
                    }
                }

                var boundIndex = arguments.ComptimeValueArguments[2].IntegerValue;
                if (boundIndex < BigInteger.Zero || boundIndex >= boundCount)
                {
                    ReportError(
                        "STK3054",
                        $"Compile-time structural fact '{factName}' trait-bound index '{boundIndex}' is out of range for generic parameter '{parameterName}' of method slot {methodIndex} of '{arguments.TargetType.DisplayName}' with {boundCount} trait bound(s).",
                        context);
                    return false;
                }
            }

            if (CompileTimeStructuralFacts.IsMethodThreadSafetyLawPredicateIndexedFact(kind))
            {
                var predicateIndex = arguments.ComptimeValueArguments[1].IntegerValue;
                if (predicateIndex < BigInteger.Zero || predicateIndex >= method.ThreadSafetyLaws.Count)
                {
                    ReportError(
                        "STK3054",
                        $"Compile-time structural fact '{factName}' thread-safety law predicate index '{predicateIndex}' is out of range for method slot {methodIndex} of '{arguments.TargetType.DisplayName}' with {method.ThreadSafetyLaws.Count} predicate(s).",
                        context);
                    return false;
                }
            }
        }

        if (kind == CompileTimeStructuralFactKind.FunctionPointerReturnTypeIs
            || kind == CompileTimeStructuralFactKind.FunctionPointerParameterTypeIs
            || CompileTimeStructuralFacts.IsFunctionPointerReturnTypePredicate(kind)
            || CompileTimeStructuralFacts.IsFunctionPointerParameterTypePredicate(kind)
            || CompileTimeStructuralFacts.IsFunctionPointerReturnTypeMetadataFact(kind)
            || CompileTimeStructuralFacts.IsFunctionPointerParameterTypeMetadataFact(kind)
            || CompileTimeStructuralFacts.IsFunctionPointerReturnTypeArgumentFact(kind)
            || CompileTimeStructuralFacts.IsFunctionPointerParameterTypeArgumentFact(kind)
            || CompileTimeStructuralFacts.IsFunctionPointerParameterRawPointerElementCountExpressionFact(kind)
            || CompileTimeStructuralFacts.IsFunctionPointerParameterMemoryFact(kind)
            || kind == CompileTimeStructuralFactKind.FunctionPointerIsUnsafe)
        {
            var targetType = StarkTypeSymbols.WithQualifiers(
                arguments.TargetType,
                borrowKind: StarkBorrowKind.None,
                accessKind: StarkAccessKind.None,
                initializationKind: StarkInitializationKind.None,
                isMutableView: false);
            if (targetType.Kind != StarkTypeKind.FunctionPointer)
            {
                if (targetType.Kind is StarkTypeKind.Named or StarkTypeKind.AssociatedType
                    && ResolveNamedTypeSymbol(targetType) is null)
                {
                    return true;
                }

                ReportError(
                    "STK3054",
                    $"Compile-time structural fact '{factName}' requires a function pointer type, but found '{arguments.TargetType.DisplayName}'.",
                    context);
                return false;
            }

            if (kind == CompileTimeStructuralFactKind.FunctionPointerParameterTypeIs
                || CompileTimeStructuralFacts.IsFunctionPointerParameterTypePredicate(kind)
                || CompileTimeStructuralFacts.IsFunctionPointerParameterTypeMetadataFact(kind)
                || CompileTimeStructuralFacts.IsFunctionPointerParameterTypeArgumentFact(kind)
                || CompileTimeStructuralFacts.IsFunctionPointerParameterRawPointerElementCountExpressionFact(kind))
            {
                var index = arguments.ComptimeValueArguments[0].IntegerValue;
                var parameterCount = targetType.FunctionPointerParameterTypes?.Count ?? 0;
                if (index < BigInteger.Zero || index >= parameterCount)
                {
                    ReportError(
                        "STK3054",
                        $"Compile-time structural fact '{factName}' parameter index '{index}' is out of range for '{arguments.TargetType.DisplayName}' with {parameterCount} parameter(s).",
                        context);
                    return false;
                }
            }

            if (CompileTimeStructuralFacts.IsFunctionPointerReturnTypeArgumentFact(kind)
                && targetType.FunctionPointerReturnType is { } returnType)
            {
                var argumentIndex = arguments.ComptimeValueArguments[0].IntegerValue;
                if (!ValidateStructuralFactTypeArgumentIndex(
                        factName,
                        returnType,
                        argumentIndex,
                        $"return type of '{arguments.TargetType.DisplayName}'",
                        context))
                {
                    return false;
                }
            }

            if (CompileTimeStructuralFacts.IsFunctionPointerParameterTypeArgumentFact(kind)
                && targetType.FunctionPointerParameterTypes is { } parameterTypes)
            {
                var parameterIndex = arguments.ComptimeValueArguments[0].IntegerValue;
                var argumentIndex = arguments.ComptimeValueArguments[1].IntegerValue;
                if (!ValidateStructuralFactTypeArgumentIndex(
                        factName,
                        parameterTypes[(int)parameterIndex],
                        argumentIndex,
                        $"parameter slot {parameterIndex} of '{arguments.TargetType.DisplayName}'",
                        context))
                {
                    return false;
                }
            }

            if (CompileTimeStructuralFacts.IsFunctionPointerParameterMemoryFact(kind))
            {
                var parameterCount = targetType.FunctionPointerParameterTypes?.Count ?? 0;
                for (var position = 0; position < 2; position++)
                {
                    var index = arguments.ComptimeValueArguments[position].IntegerValue;
                    if (index < BigInteger.Zero || index >= parameterCount)
                    {
                        ReportError(
                            "STK3054",
                            $"Compile-time structural fact '{factName}' parameter index '{index}' is out of range for '{arguments.TargetType.DisplayName}' with {parameterCount} parameter(s).",
                            context);
                        return false;
                    }
                }
            }
        }

        if (kind == CompileTimeStructuralFactKind.ClosureReturnTypeIs
            || kind == CompileTimeStructuralFactKind.ClosureParameterTypeIs
            || kind is CompileTimeStructuralFactKind.ClosureKindIsFn
                or CompileTimeStructuralFactKind.ClosureKindIsFinite
                or CompileTimeStructuralFactKind.ClosureKindIsLaw
                or CompileTimeStructuralFactKind.ClosureKindIsFiniteLaw
                or CompileTimeStructuralFactKind.ClosureStorageIsBorrow
                or CompileTimeStructuralFactKind.ClosureStorageIsHeap
                or CompileTimeStructuralFactKind.ClosureStorageIsInline
                or CompileTimeStructuralFactKind.ClosureCallCapabilityIsNormal
                or CompileTimeStructuralFactKind.ClosureCallCapabilityIsMut
                or CompileTimeStructuralFactKind.ClosureCallCapabilityIsOnce
            || CompileTimeStructuralFacts.IsClosureReturnTypePredicate(kind)
            || CompileTimeStructuralFacts.IsClosureParameterTypePredicate(kind)
            || CompileTimeStructuralFacts.IsClosureReturnTypeMetadataFact(kind)
            || CompileTimeStructuralFacts.IsClosureParameterTypeMetadataFact(kind)
            || CompileTimeStructuralFacts.IsClosureReturnTypeArgumentFact(kind)
            || CompileTimeStructuralFacts.IsClosureParameterTypeArgumentFact(kind)
            || CompileTimeStructuralFacts.IsClosureParameterRawPointerElementCountExpressionFact(kind)
            || CompileTimeStructuralFacts.IsClosureParameterMemoryFact(kind))
        {
            var targetType = StarkTypeSymbols.WithQualifiers(
                arguments.TargetType,
                borrowKind: StarkBorrowKind.None,
                accessKind: StarkAccessKind.None,
                initializationKind: StarkInitializationKind.None,
                isMutableView: false);
            if (targetType.Kind != StarkTypeKind.Closure)
            {
                if (targetType.Kind is StarkTypeKind.Named or StarkTypeKind.AssociatedType
                    && ResolveNamedTypeSymbol(targetType) is null)
                {
                    return true;
                }

                ReportError(
                    "STK3054",
                    $"Compile-time structural fact '{factName}' requires a closure type, but found '{arguments.TargetType.DisplayName}'.",
                    context);
                return false;
            }

            if (kind == CompileTimeStructuralFactKind.ClosureParameterTypeIs
                || CompileTimeStructuralFacts.IsClosureParameterTypePredicate(kind)
                || CompileTimeStructuralFacts.IsClosureParameterTypeMetadataFact(kind)
                || CompileTimeStructuralFacts.IsClosureParameterTypeArgumentFact(kind)
                || CompileTimeStructuralFacts.IsClosureParameterRawPointerElementCountExpressionFact(kind))
            {
                var index = arguments.ComptimeValueArguments[0].IntegerValue;
                var parameterCount = targetType.ClosureParameterTypes?.Count ?? 0;
                if (index < BigInteger.Zero || index >= parameterCount)
                {
                    ReportError(
                        "STK3054",
                        $"Compile-time structural fact '{factName}' parameter index '{index}' is out of range for '{arguments.TargetType.DisplayName}' with {parameterCount} parameter(s).",
                        context);
                    return false;
                }
            }

            if (CompileTimeStructuralFacts.IsClosureReturnTypeArgumentFact(kind)
                && targetType.ClosureReturnType is { } returnType)
            {
                var argumentIndex = arguments.ComptimeValueArguments[0].IntegerValue;
                if (!ValidateStructuralFactTypeArgumentIndex(
                        factName,
                        returnType,
                        argumentIndex,
                        $"return type of '{arguments.TargetType.DisplayName}'",
                        context))
                {
                    return false;
                }
            }

            if (CompileTimeStructuralFacts.IsClosureParameterTypeArgumentFact(kind)
                && targetType.ClosureParameterTypes is { } parameterTypes)
            {
                var parameterIndex = arguments.ComptimeValueArguments[0].IntegerValue;
                var argumentIndex = arguments.ComptimeValueArguments[1].IntegerValue;
                if (!ValidateStructuralFactTypeArgumentIndex(
                        factName,
                        parameterTypes[(int)parameterIndex],
                        argumentIndex,
                        $"parameter slot {parameterIndex} of '{arguments.TargetType.DisplayName}'",
                        context))
                {
                    return false;
                }
            }

            if (CompileTimeStructuralFacts.IsClosureParameterMemoryFact(kind))
            {
                var parameterCount = targetType.ClosureParameterTypes?.Count ?? 0;
                for (var position = 0; position < 2; position++)
                {
                    var index = arguments.ComptimeValueArguments[position].IntegerValue;
                    if (index < BigInteger.Zero || index >= parameterCount)
                    {
                        ReportError(
                            "STK3054",
                            $"Compile-time structural fact '{factName}' parameter index '{index}' is out of range for '{arguments.TargetType.DisplayName}' with {parameterCount} parameter(s).",
                            context);
                        return false;
                    }
                }
            }
        }

        if (kind is CompileTimeStructuralFactKind.FieldOffset
            or CompileTimeStructuralFactKind.FieldSize
            or CompileTimeStructuralFactKind.FieldAlign
            or CompileTimeStructuralFactKind.FieldIsMisaligned)
        {
            if (ResolveNamedTypeSymbol(arguments.TargetType) is not { } namedType)
            {
                return true;
            }

            if (namedType.Kind is not (DeclarationKind.Struct or DeclarationKind.Record))
            {
                ReportError(
                    "STK3054",
                    $"Compile-time structural fact '{factName}' requires a struct or record type, but found '{arguments.TargetType.DisplayName}'.",
                    context);
                return false;
            }

            var index = arguments.ComptimeValueArguments[0].IntegerValue;
            if (index < BigInteger.Zero || index >= namedType.OrderedFields.Count)
            {
                ReportError(
                    "STK3054",
                    $"Compile-time structural fact '{factName}' field index '{index}' is out of range for '{arguments.TargetType.DisplayName}' with {namedType.OrderedFields.Count} field(s).",
                    context);
                return false;
            }

            if (!TryResolveCompileTimeConcreteLayout(arguments.TargetType, out _))
            {
                ReportError(
                    "STK3054",
                    $"Compile-time structural fact '{factName}' requires concrete layout for '{arguments.TargetType.DisplayName}'.",
                    context);
                return false;
            }
        }

        if ((kind is CompileTimeStructuralFactKind.FieldTypeIs
            or CompileTimeStructuralFactKind.FieldName
            or CompileTimeStructuralFactKind.FieldHasExplicitOffset
            or CompileTimeStructuralFactKind.FieldExplicitOffset)
            || CompileTimeStructuralFacts.IsFieldTypePredicate(kind)
            || CompileTimeStructuralFacts.IsFieldTypeMetadataFact(kind)
            || CompileTimeStructuralFacts.IsFieldVisibilityFact(kind))
        {
            if (ResolveNamedTypeSymbol(arguments.TargetType) is not { } namedType)
            {
                return true;
            }

            if (namedType.Kind is not (DeclarationKind.Struct or DeclarationKind.Record))
            {
                ReportError(
                    "STK3054",
                    $"Compile-time structural fact '{factName}' requires a struct or record type, but found '{arguments.TargetType.DisplayName}'.",
                    context);
                return false;
            }

            var index = arguments.ComptimeValueArguments[0].IntegerValue;
            if (index < BigInteger.Zero || index >= namedType.OrderedFields.Count)
            {
                ReportError(
                    "STK3054",
                    $"Compile-time structural fact '{factName}' field index '{index}' is out of range for '{arguments.TargetType.DisplayName}' with {namedType.OrderedFields.Count} field(s).",
                    context);
                return false;
            }
        }

        if (CompileTimeStructuralFacts.IsTypeThreadSafetyLawAttributeIndexedFact(kind))
        {
            if (ResolveNamedTypeSymbol(arguments.TargetType) is not { } namedType)
            {
                ReportError(
                    "STK3054",
                    $"Compile-time structural fact '{factName}' requires a named type, but found '{arguments.TargetType.DisplayName}'.",
                    context);
                return false;
            }

            var index = arguments.ComptimeValueArguments[0].IntegerValue;
            if (index < BigInteger.Zero || index >= namedType.ThreadSafetyLaws.Count)
            {
                ReportError(
                    "STK3054",
                    $"Compile-time structural fact '{factName}' thread-safety law attribute index '{index}' is out of range for '{arguments.TargetType.DisplayName}' with {namedType.ThreadSafetyLaws.Count} attribute(s).",
                    context);
                return false;
            }
        }

        if (CompileTimeStructuralFacts.IsFieldThreadSafetyLawAttributeFact(kind))
        {
            if (ResolveNamedTypeSymbol(arguments.TargetType) is not { } namedType)
            {
                return true;
            }

            if (namedType.Kind is not (DeclarationKind.Struct or DeclarationKind.Record))
            {
                ReportError(
                    "STK3054",
                    $"Compile-time structural fact '{factName}' requires a struct or record type, but found '{arguments.TargetType.DisplayName}'.",
                    context);
                return false;
            }

            var fieldIndex = arguments.ComptimeValueArguments[0].IntegerValue;
            if (fieldIndex < BigInteger.Zero || fieldIndex >= namedType.OrderedFields.Count)
            {
                ReportError(
                    "STK3054",
                    $"Compile-time structural fact '{factName}' field index '{fieldIndex}' is out of range for '{arguments.TargetType.DisplayName}' with {namedType.OrderedFields.Count} field(s).",
                    context);
                return false;
            }

            if (CompileTimeStructuralFacts.IsFieldThreadSafetyLawAttributeIndexedFact(kind))
            {
                var field = namedType.OrderedFields[(int)fieldIndex];
                var attributeIndex = arguments.ComptimeValueArguments[1].IntegerValue;
                if (attributeIndex < BigInteger.Zero || attributeIndex >= field.ThreadSafetyLaws.Count)
                {
                    ReportError(
                        "STK3054",
                        $"Compile-time structural fact '{factName}' thread-safety law attribute index '{attributeIndex}' is out of range for field '{field.Name}' of '{arguments.TargetType.DisplayName}' with {field.ThreadSafetyLaws.Count} attribute(s).",
                        context);
                    return false;
                }
            }
        }

        if ((kind is CompileTimeStructuralFactKind.AssociatedTypeName
            or CompileTimeStructuralFactKind.AssociatedTypeHasTarget
            or CompileTimeStructuralFactKind.AssociatedTypeTargetTypeIs)
            || CompileTimeStructuralFacts.IsAssociatedTypeTargetTypePredicate(kind)
            || CompileTimeStructuralFacts.IsAssociatedTypeTargetTypeMetadataFact(kind))
        {
            if (ResolveNamedTypeSymbol(arguments.TargetType) is not { } namedType)
            {
                return true;
            }

            var associatedTypes = namedType.AssociatedTypes.Values
                .OrderBy(static associatedType => associatedType.Name, StringComparer.Ordinal)
                .ToArray();
            var index = arguments.ComptimeValueArguments[0].IntegerValue;
            if (index < BigInteger.Zero || index >= associatedTypes.Length)
            {
                ReportError(
                    "STK3054",
                    $"Compile-time structural fact '{factName}' associated type index '{index}' is out of range for '{arguments.TargetType.DisplayName}' with {associatedTypes.Length} associated type(s).",
                    context);
                return false;
            }
        }

        if (kind is CompileTimeStructuralFactKind.TypeGenericParameterName
            or CompileTimeStructuralFactKind.TypeComptimeGenericParameterName
            or CompileTimeStructuralFactKind.TypeComptimeGenericParameterTypeIs
            || CompileTimeStructuralFacts.IsTypeComptimeGenericParameterTypePredicate(kind)
            || CompileTimeStructuralFacts.IsTypeComptimeGenericParameterTypeMetadataFact(kind))
        {
            if (ResolveNamedTypeDefinitionSymbol(arguments.TargetType) is not { } namedType)
            {
                ReportError(
                    "STK3054",
                    $"Compile-time structural fact '{factName}' requires a named type, but found '{arguments.TargetType.DisplayName}'.",
                    context);
                return false;
            }

            var index = arguments.ComptimeValueArguments[0].IntegerValue;
            var parameterCount = kind == CompileTimeStructuralFactKind.TypeGenericParameterName
                ? namedType.GenericParams.Count
                : namedType.ComptimeGenericParams.Count;
            var parameterKind = kind == CompileTimeStructuralFactKind.TypeGenericParameterName
                ? "generic parameter"
                : "comptime generic parameter";
            if (index < BigInteger.Zero || index >= parameterCount)
            {
                ReportError(
                    "STK3054",
                    $"Compile-time structural fact '{factName}' {parameterKind} index '{index}' is out of range for '{arguments.TargetType.DisplayName}' with {parameterCount} {parameterKind}(s).",
                    context);
                return false;
            }
        }

        if (kind == CompileTimeStructuralFactKind.TypeArgumentTypeIs
            || CompileTimeStructuralFacts.IsTypeArgumentTypePredicate(kind)
            || CompileTimeStructuralFacts.IsTypeArgumentTypeMetadataFact(kind))
        {
            var coreType = StarkTypeSymbols.WithQualifiers(
                arguments.TargetType,
                borrowKind: StarkBorrowKind.None,
                accessKind: StarkAccessKind.None,
                initializationKind: StarkInitializationKind.None,
                isMutableView: false);
            if (coreType.Kind != StarkTypeKind.Named)
            {
                ReportError(
                    "STK3054",
                    $"Compile-time structural fact '{factName}' requires a named type, but found '{arguments.TargetType.DisplayName}'.",
                    context);
                return false;
            }

            var typeArgumentCount = coreType.TypeArguments?.Count ?? 0;
            var index = arguments.ComptimeValueArguments[0].IntegerValue;
            if (index < BigInteger.Zero || index >= typeArgumentCount)
            {
                ReportError(
                    "STK3054",
                    $"Compile-time structural fact '{factName}' type argument index '{index}' is out of range for '{arguments.TargetType.DisplayName}' with {typeArgumentCount} type argument(s).",
                    context);
                return false;
            }
        }

        if (kind is CompileTimeStructuralFactKind.TypeComptimeArgumentName
            or CompileTimeStructuralFactKind.TypeComptimeArgumentTypeIs
            or CompileTimeStructuralFactKind.TypeComptimeArgumentValueIs
            || CompileTimeStructuralFacts.IsTypeComptimeArgumentTypePredicate(kind)
            || CompileTimeStructuralFacts.IsTypeComptimeArgumentTypeMetadataFact(kind))
        {
            var coreType = StarkTypeSymbols.WithQualifiers(
                arguments.TargetType,
                borrowKind: StarkBorrowKind.None,
                accessKind: StarkAccessKind.None,
                initializationKind: StarkInitializationKind.None,
                isMutableView: false);
            if (coreType.Kind != StarkTypeKind.Named)
            {
                ReportError(
                    "STK3054",
                    $"Compile-time structural fact '{factName}' requires a named type, but found '{arguments.TargetType.DisplayName}'.",
                    context);
                return false;
            }

            var comptimeArgumentCount = coreType.ComptimeValueArguments?.Count ?? 0;
            var index = arguments.ComptimeValueArguments[0].IntegerValue;
            if (index < BigInteger.Zero || index >= comptimeArgumentCount)
            {
                ReportError(
                    "STK3054",
                    $"Compile-time structural fact '{factName}' comptime argument index '{index}' is out of range for '{arguments.TargetType.DisplayName}' with {comptimeArgumentCount} comptime argument(s).",
                    context);
                return false;
            }
        }

        if (kind is CompileTimeStructuralFactKind.EnumVariantPayloadCount
            or CompileTimeStructuralFactKind.EnumVariantTag
            or CompileTimeStructuralFactKind.EnumVariantIsOk
            or CompileTimeStructuralFactKind.EnumVariantIsErr
            or CompileTimeStructuralFactKind.EnumVariantIsErrorFunnel
            or CompileTimeStructuralFactKind.EnumVariantAbsorbsErrorTypeIs
            or CompileTimeStructuralFactKind.EnumVariantName
            or CompileTimeStructuralFactKind.EnumVariantUsesNamedFields)
        {
            if (ResolveNamedTypeSymbol(arguments.TargetType) is not { } namedType)
            {
                return true;
            }

            if (namedType.Kind != DeclarationKind.Enum)
            {
                ReportError(
                    "STK3054",
                    $"Compile-time structural fact '{factName}' requires an enum type, but found '{arguments.TargetType.DisplayName}'.",
                    context);
                return false;
            }

            var index = arguments.ComptimeValueArguments[0].IntegerValue;
            if (index < BigInteger.Zero || index >= namedType.Variants.Count)
            {
                ReportError(
                    "STK3054",
                    $"Compile-time structural fact '{factName}' variant index '{index}' is out of range for '{arguments.TargetType.DisplayName}' with {namedType.Variants.Count} variant(s).",
                    context);
                return false;
            }
        }

        if (CompileTimeStructuralFacts.IsEnumTagLayoutFact(kind))
        {
            if (ResolveNamedTypeSymbol(arguments.TargetType) is not { } namedType)
            {
                return true;
            }

            if (namedType.Kind != DeclarationKind.Enum)
            {
                ReportError(
                    "STK3054",
                    $"Compile-time structural fact '{factName}' requires an enum type, but found '{arguments.TargetType.DisplayName}'.",
                    context);
                return false;
            }

            if (!TryResolveCompileTimeConcreteLayout(arguments.TargetType, out _))
            {
                ReportError(
                    "STK3054",
                    $"Compile-time structural fact '{factName}' requires concrete layout for '{arguments.TargetType.DisplayName}'.",
                    context);
                return false;
            }
        }

        if ((kind is CompileTimeStructuralFactKind.EnumVariantPayloadTypeIs
            or CompileTimeStructuralFactKind.EnumVariantPayloadHasName
            or CompileTimeStructuralFactKind.EnumVariantPayloadName)
            || CompileTimeStructuralFacts.IsEnumVariantPayloadLayoutFact(kind)
            || CompileTimeStructuralFacts.IsEnumVariantPayloadTypePredicate(kind)
            || CompileTimeStructuralFacts.IsEnumVariantPayloadTypeMetadataFact(kind))
        {
            if (ResolveNamedTypeSymbol(arguments.TargetType) is not { } namedType)
            {
                return true;
            }

            if (namedType.Kind != DeclarationKind.Enum)
            {
                ReportError(
                    "STK3054",
                    $"Compile-time structural fact '{factName}' requires an enum type, but found '{arguments.TargetType.DisplayName}'.",
                    context);
                return false;
            }

            var variantIndex = arguments.ComptimeValueArguments[0].IntegerValue;
            if (variantIndex < BigInteger.Zero || variantIndex >= namedType.Variants.Count)
            {
                ReportError(
                    "STK3054",
                    $"Compile-time structural fact '{factName}' variant index '{variantIndex}' is out of range for '{arguments.TargetType.DisplayName}' with {namedType.Variants.Count} variant(s).",
                    context);
                return false;
            }

            var payloadIndex = arguments.ComptimeValueArguments[1].IntegerValue;
            var variant = namedType.Variants[(int)variantIndex];
            if (payloadIndex < BigInteger.Zero || payloadIndex >= variant.Fields.Count)
            {
                ReportError(
                    "STK3054",
                    $"Compile-time structural fact '{factName}' payload index '{payloadIndex}' is out of range for variant '{variant.Name}' of '{arguments.TargetType.DisplayName}' with {variant.Fields.Count} payload field(s).",
                    context);
                return false;
            }

            if (CompileTimeStructuralFacts.IsEnumVariantPayloadLayoutFact(kind)
                && !TryResolveCompileTimeConcreteLayout(arguments.TargetType, out _))
            {
                ReportError(
                    "STK3054",
                    $"Compile-time structural fact '{factName}' requires concrete layout for '{arguments.TargetType.DisplayName}'.",
                    context);
                return false;
            }
        }

        return true;
    }

    private StarkTypeSymbol? ResolveAssociatedTypeForSubstitution(StarkTypeSymbol ownerType, string associatedTypeName)
    {
        return AssociatedTypeFacts.TryResolveAssociatedType(ownerType, associatedTypeName, _namedTypes, out var targetType)
            ? EnsureMonomorphizedType(targetType)
            : null;
    }

    private ExpressionBinding ResolveGenericMemberReferenceValue(
        StarkParser.GenericEnumCaseReferenceContext genericEnumCaseReference,
        bool allowFunctionReference)
    {
        if (TryResolveEnumCaseReference(genericEnumCaseReference, out var enumType, out var enumTypeSymbol, out var variant))
        {
            return CreateEnumCaseValueBinding(
                genericEnumCaseReference.GetText(),
                enumTypeSymbol,
                enumType,
                variant,
                genericEnumCaseReference.Start,
                allowFunctionReference);
        }

        var targetType = ResolveGenericQualifiedName(genericEnumCaseReference.genericQualifiedName());
        var namedType = ResolveNamedTypeSymbol(targetType);
        if (namedType?.Kind is DeclarationKind.Doctrine or DeclarationKind.Trait)
        {
            return ApplyMemberAccess(
                new ExpressionBinding(
                    targetType,
                    NamedType: namedType,
                    DiagnosticName: namedType.Kind == DeclarationKind.Doctrine
                        ? $"doctrine '{targetType.DisplayName}'"
                        : $"trait '{targetType.DisplayName}'"),
                genericEnumCaseReference.Identifier().GetText(),
                genericEnumCaseReference);
        }

        if (namedType is not null && namedType.Kind is DeclarationKind.Struct or DeclarationKind.Record)
        {
            return ApplyMemberAccess(
                new ExpressionBinding(
                    StarkTypeSymbols.Error,
                    NamespaceName: targetType.NamedType,
                    NamedType: namedType,
                    DiagnosticName: $"type '{targetType.DisplayName}'"),
                genericEnumCaseReference.Identifier().GetText(),
                genericEnumCaseReference);
        }

        if (targetType.Kind != StarkTypeKind.Error)
        {
            ReportError("STK3003", $"Unknown symbol '{genericEnumCaseReference.GetText()}'.", genericEnumCaseReference);
            return new ExpressionBinding(StarkTypeSymbols.Error);
        }

        return new ExpressionBinding(StarkTypeSymbols.Error);
    }

    private ExpressionBinding CreateEnumCaseValueBinding(
        string caseName,
        StarkTypeSymbol enumTypeSymbol,
        NamedTypeSymbol enumType,
        EnumVariantSymbol variant,
        IToken token,
        bool allowFunctionReference)
    {
        if (variant.IsUnit)
        {
            RecordEnumValue(enumTypeSymbol, variant.Name, token);
            return new ExpressionBinding(enumTypeSymbol, NamedType: enumType, DiagnosticName: $"enum case '{caseName}'");
        }

        if (variant.UsesNamedFields)
        {
            ReportError("STK3008", $"Enum constructor '{caseName}' must use a named-field initializer before its value can be used.", token);
            return new ExpressionBinding(StarkTypeSymbols.Error);
        }

        if (!allowFunctionReference)
        {
            ReportError("STK3012", $"Enum constructor '{caseName}' must be called before its value can be used.", token);
            return new ExpressionBinding(StarkTypeSymbols.Error);
        }

        return new ExpressionBinding(
            enumTypeSymbol,
            NamedType: enumType,
            DiagnosticName: $"enum constructor '{caseName}'",
            EnumConstructor: new EnumConstructorBinding(caseName, variant));
    }

    private StarkTypeSymbol EnsureMonomorphizedType(StarkTypeSymbol type, SourceLocation? triggerLocation = null)
    {
        var monomorphizedType = type;
        var strippedType = StarkTypeSymbols.WithQualifiers(
            type,
            borrowKind: StarkBorrowKind.None,
            accessKind: StarkAccessKind.None,
            initializationKind: StarkInitializationKind.None,
            isMutableView: false);

        if (strippedType.Kind == StarkTypeKind.Named
            && StarkTypeSymbols.IsGenericInstantiation(strippedType)
            && strippedType.NamedType is not null)
        {
            var monomorphizedArguments = (strippedType.TypeArguments ?? [])
                .Select(argument => EnsureMonomorphizedType(argument))
                .ToArray();
            monomorphizedType = StarkTypeSymbols.WithQualifiers(
                StarkTypeSymbols.GenericInstantiation(
                    StarkTypeSymbols.GetGenericBaseName(strippedType.NamedType),
                    monomorphizedArguments,
                    strippedType.ComptimeValueArguments),
                borrowKind: type.BorrowKind,
                accessKind: type.AccessKind,
                initializationKind: type.InitializationKind,
                isMutableView: type.IsMutableView);
        }
        else if (strippedType.ElementType is not null)
        {
            var monomorphizedElement = EnsureMonomorphizedType(strippedType.ElementType);
            var rebuiltCore = strippedType.Kind switch
            {
                StarkTypeKind.FixedArray => StarkTypeSymbols.FixedArray(monomorphizedElement, strippedType.FixedLength, strippedType.FixedLengthParameterName),
                StarkTypeKind.Slice => StarkTypeSymbols.Slice(monomorphizedElement),
                StarkTypeKind.RawPointer => StarkTypeSymbols.RawPointer(monomorphizedElement, strippedType.IsMutablePointer),
                StarkTypeKind.Dynamic => StarkTypeSymbols.Dynamic(monomorphizedElement),
                _ => strippedType
            };
            monomorphizedType = StarkTypeSymbols.WithQualifiers(
                rebuiltCore,
                borrowKind: type.BorrowKind,
                accessKind: type.AccessKind,
                initializationKind: type.InitializationKind,
                isMutableView: type.IsMutableView);
        }
        else if (strippedType.Kind == StarkTypeKind.FunctionPointer
            && strippedType.FunctionPointerKind is { } functionKind
            && strippedType.FunctionPointerReturnType is { } returnType
            && strippedType.FunctionPointerParameterTypes is { } parameterTypes)
        {
            monomorphizedType = StarkTypeSymbols.WithQualifiers(
                StarkTypeSymbols.FunctionPointer(
                    functionKind,
                    EnsureMonomorphizedType(returnType),
                    parameterTypes.Select(parameter => EnsureMonomorphizedType(parameter)).ToArray(),
                    strippedType.FunctionPointerDisjointParameterGroups,
                    strippedType.FunctionPointerOverlapParameterGroups,
                    strippedType.FunctionPointerSameParameterGroups,
                    strippedType.FunctionPointerParameterRawPointerElementCountExpressions,
                    strippedType.FunctionPointerAbi,
                    strippedType.FunctionPointerIsUnsafe),
                borrowKind: type.BorrowKind,
                accessKind: type.AccessKind,
                initializationKind: type.InitializationKind,
                isMutableView: type.IsMutableView);
        }
        else if (strippedType.Kind == StarkTypeKind.Closure
            && strippedType.ClosureFunctionKind is { } closureFunctionKind
            && strippedType.ClosureReturnType is { } closureReturnType
            && strippedType.ClosureParameterTypes is { } closureParameterTypes)
        {
            monomorphizedType = StarkTypeSymbols.WithQualifiers(
                StarkTypeSymbols.Closure(
                    strippedType.ClosureStorageKind,
                    strippedType.ClosureCallCapability,
                    closureFunctionKind,
                    EnsureMonomorphizedType(closureReturnType),
                    closureParameterTypes.Select(parameter => EnsureMonomorphizedType(parameter)).ToArray(),
                    strippedType.ClosureDisjointParameterGroups,
                    strippedType.ClosureOverlapParameterGroups,
                    strippedType.ClosureSameParameterGroups,
                    strippedType.ClosureParameterRawPointerElementCountExpressions),
                borrowKind: type.BorrowKind,
                accessKind: type.AccessKind,
                initializationKind: type.InitializationKind,
                isMutableView: type.IsMutableView);
        }

        if (!StarkTypeSymbols.IsGenericInstantiation(monomorphizedType))
        {
            if (triggerLocation is { } nestedTriggerLocation)
            {
                RecordTypeInstantiationTriggers(monomorphizedType, nestedTriggerLocation);
            }

            return monomorphizedType;
        }

        var key = monomorphizedType.NamedType!;
        var concreteTypeArguments = monomorphizedType.TypeArguments ?? [];
        var concreteValueArguments = monomorphizedType.ComptimeValueArguments ?? [];
        if (concreteTypeArguments.Count > 0 || concreteValueArguments.Count > 0)
        {
            ValidateDictionaryKeyConstraint(monomorphizedType, triggerLocation);
            _genericInstantiationArguments.TryAdd(
                key,
                new ConcreteGenericTypeArguments(
                    concreteTypeArguments.ToArray(),
                    concreteValueArguments.Count == 0 ? null : concreteValueArguments.ToArray()));
        }

        if (_namedTypes.TryGetValue(key, out var existingNamedType))
        {
            if (TryRefreshIncompleteConcreteType(
                    key,
                    new ConcreteGenericTypeArguments(
                        concreteTypeArguments.ToArray(),
                        concreteValueArguments.Count == 0 ? null : concreteValueArguments.ToArray())))
            {
                existingNamedType = _namedTypes[key];
            }

            if (concreteTypeArguments.Count > 0 || concreteValueArguments.Count > 0)
            {
                EnsureConcreteConstructorShapes(
                    key,
                    new ConcreteGenericTypeArguments(
                        concreteTypeArguments.ToArray(),
                        concreteValueArguments.Count == 0 ? null : concreteValueArguments.ToArray()));
            }

            if (triggerLocation is { } existingTriggerLocation)
            {
                RecordTypeInstantiationTriggers(monomorphizedType, existingTriggerLocation);
            }

            return monomorphizedType;
        }

        var baseName = StarkTypeSymbols.GetGenericBaseName(key);
        if (!_namedTypes.TryGetValue(baseName, out var template)
            && !TryResolveNamedTypeBySourceName(baseName, out template))
        {
            return monomorphizedType;
        }

        if (!template.IsGeneric)
        {
            ReportError("STK3019", $"Type '{baseName}' is not generic and does not accept type arguments.", SourceLocation.Synthetic());
            return monomorphizedType;
        }

        if (template.GenericParams.Count != concreteTypeArguments.Count
            || template.ComptimeGenericParams.Count != concreteValueArguments.Count)
        {
            ReportError(
                "STK3019",
                $"Generic type '{baseName}' expects {template.GenericParams.Count} type argument(s) and {template.ComptimeGenericParams.Count} comptime value argument(s) but {concreteTypeArguments.Count} type argument(s) and {concreteValueArguments.Count} comptime value argument(s) were provided.",
                SourceLocation.Synthetic());
            return monomorphizedType;
        }

        var substitution = new Dictionary<string, StarkTypeSymbol>(StringComparer.Ordinal);
        for (var i = 0; i < template.GenericParams.Count; i++)
        {
            substitution[template.GenericParams[i]] = EnsureMonomorphizedType(concreteTypeArguments[i]);
        }
        var valueSubstitution = BuildNamedTypeComptimeValueSubstitution(template, concreteValueArguments);

        _namedTypes[key] = new NamedTypeSymbol(
            key,
            template.Kind,
            new Dictionary<string, FieldSymbol>(StringComparer.Ordinal),
            [],
            GenericParameterNames: template.GenericParams.ToArray(),
            ComptimeGenericParameterNames: template.ComptimeGenericParams.ToArray(),
            IsDynTrait: template.IsDynTrait,
            Layout: template.Layout,
            DeclaringModuleName: template.DeclaringModuleName,
            Visibility: template.Visibility);
        _namedTypes[key] = template.Kind == DeclarationKind.Enum
            ? CreateConcreteEnum(key, template, substitution, valueSubstitution)
            : CreateConcreteStructLike(key, template, substitution, valueSubstitution);
        if (_constructors.TryGetValue(baseName, out var templateConstructors))
        {
            _constructors[key] = CreateConcreteConstructors(templateConstructors, substitution, valueSubstitution);
        }

        if (triggerLocation is { } typeTriggerLocation)
        {
            RecordTypeInstantiationTriggers(monomorphizedType, typeTriggerLocation);
        }

        return monomorphizedType;
    }

    private void ValidateDictionaryKeyConstraint(StarkTypeSymbol dictionaryType, SourceLocation? triggerLocation)
    {
        if (!SystemCollectionsDictionaryKeyFacts.TryGetDictionaryKeyType(dictionaryType, out var keyType)
            || TypeContainsOpenCurrentFunctionGenericParameter(keyType))
        {
            return;
        }

        if (!_canValidateDictionaryKeyConstraints)
        {
            _pendingDictionaryKeyConstraintValidations.Add((dictionaryType, triggerLocation));
            return;
        }

        if (SystemCollectionsDictionaryKeyFacts.TryResolveContract(
                keyType,
                sourceName => TryGetFunctionOverloads(sourceName, CurrentFunctionModuleName, out var candidates) ? candidates : null,
                out var contract,
                out var contractDiagnostic))
        {
            RecordDictionaryKeyContractInstantiationTriggers(
                contract,
                triggerLocation ?? SourceLocation.Synthetic());
            return;
        }

        var diagnosticKey = $"{dictionaryType.NamedType}|{keyType.DisplayName}|{triggerLocation?.Line ?? 0}|{triggerLocation?.Column ?? 0}";
        if (!_dictionaryKeyConstraintFailures.Add(diagnosticKey))
        {
            return;
        }

        ReportError(
            "STK3023",
            $"{FormatDictionaryKeyCollectionUse(dictionaryType, keyType)} collection use requires a compile-time DictionaryKey contract for key type '{keyType.DisplayName}'. {contractDiagnostic}",
            triggerLocation ?? SourceLocation.Synthetic());
    }

    private void ValidatePendingDictionaryKeyConstraints()
    {
        foreach (var (type, location) in _pendingDictionaryKeyConstraintValidations)
        {
            ValidateDictionaryKeyConstraint(type, location);
        }

        _pendingDictionaryKeyConstraintValidations.Clear();
    }

    private static string FormatDictionaryKeyCollectionUse(
        StarkTypeSymbol dictionaryType,
        StarkTypeSymbol keyType)
    {
        var normalizedDictionaryType = SystemCollectionsDictionaryKeyFacts.NormalizeType(dictionaryType);
        var collectionBaseName = normalizedDictionaryType.NamedType is null
            ? string.Empty
            : StarkTypeSymbols.GetGenericBaseName(normalizedDictionaryType.NamedType);

        return collectionBaseName is SystemCollectionsDictionaryKeyFacts.HashSetTypeName
            ? $"HashSet<{keyType.DisplayName}>"
            : $"Dictionary<{keyType.DisplayName}, V>";
    }

    private void RecordDictionaryKeyContractInstantiationTriggers(
        SystemCollectionsDictionaryKeyContract contract,
        SourceLocation location)
    {
        RecordDictionaryKeyContractInstantiationTrigger(contract.HashFunction, location);
        RecordDictionaryKeyContractInstantiationTrigger(contract.EqualsFunction, location);
    }

    private void RecordDictionaryKeyContractInstantiationTrigger(
        TypedFunctionSignature? signature,
        SourceLocation location)
    {
        if (signature is null
            || !signature.IsGenericInstantiation
            || signature.TemplateName is not { } templateName
            || signature.TypeArguments is not { Count: > 0 } typeArguments
            || typeArguments.Any(TypeContainsOpenCurrentFunctionGenericParameter)
            || SignatureContainsOpenCurrentFunctionComptimeParameter(signature))
        {
            return;
        }

        var key = BuildFunctionInstantiationKey(templateName, typeArguments, signature.ComptimeValueArguments);
        if (!_functionInstantiationKeys.Add(key))
        {
            return;
        }

        _functionInstantiationTriggers.Add(new FunctionInstantiationTriggerRecord(
            signature.DisplaySourceName,
            typeArguments.ToArray(),
            signature.ComptimeValueArguments?.ToArray(),
            signature,
            location));
    }

    private void RefreshConcreteInstantiationsForTemplate(NamedTypeSymbol template)
    {
        if (!template.IsGeneric || template.OrderedFields.Count == 0)
        {
            return;
        }

        foreach (var (key, arguments) in _genericInstantiationArguments.ToArray())
        {
            if (!string.Equals(StarkTypeSymbols.GetGenericBaseName(key), template.Name, StringComparison.Ordinal))
            {
                continue;
            }

            _ = TryRefreshIncompleteConcreteType(key, arguments);
        }
    }

    private bool TryRefreshIncompleteConcreteType(string key, ConcreteGenericTypeArguments arguments)
    {
        if (!_refreshingConcreteTypes.Add(key))
        {
            return false;
        }

        try
        {
            var baseName = StarkTypeSymbols.GetGenericBaseName(key);
            if (!_namedTypes.TryGetValue(baseName, out var template)
                && !TryResolveNamedTypeBySourceName(baseName, out template))
            {
                return false;
            }

            if (!template.IsGeneric
                || template.GenericParams.Count != arguments.TypeArguments.Count
                || template.ComptimeGenericParams.Count != arguments.ComptimeValueArguments.Count
                || (template.Kind is DeclarationKind.Struct or DeclarationKind.Record && template.OrderedFields.Count == 0))
            {
                return false;
            }

            if (_namedTypes.TryGetValue(key, out var existing)
                && template.Kind is DeclarationKind.Struct or DeclarationKind.Record
                && existing.OrderedFields.Count >= template.OrderedFields.Count)
            {
                return false;
            }

            var substitution = new Dictionary<string, StarkTypeSymbol>(StringComparer.Ordinal);
            for (var i = 0; i < template.GenericParams.Count; i++)
            {
                substitution[template.GenericParams[i]] = arguments.TypeArguments[i];
            }
            var valueSubstitution = BuildNamedTypeComptimeValueSubstitution(template, arguments.ComptimeValueArguments);

            _namedTypes[key] = template.Kind == DeclarationKind.Enum
                ? CreateConcreteEnum(key, template, substitution, valueSubstitution)
                : CreateConcreteStructLike(key, template, substitution, valueSubstitution);
            return true;
        }
        finally
        {
            _refreshingConcreteTypes.Remove(key);
        }
    }

    private NamedTypeSymbol CreateConcreteEnum(
        string key,
        NamedTypeSymbol template,
        IReadOnlyDictionary<string, StarkTypeSymbol> substitution,
        IReadOnlyDictionary<string, BigInteger> valueSubstitution)
    {
        // Carry the propagation `Role` ([Ok]/[Err]) and the `from` funnel marker through
        // monomorphization: `try` consults them on the *instantiated* enum (e.g.
        // `Result<i32, ParseError>`), so dropping them here would silently disable
        // propagation for every generic enum.
        var concreteVariants = template.Variants
            .Select(variant => new EnumVariantSymbol(
                variant.Name,
                variant.UsesNamedFields,
                variant.Fields
                    .Select(f => new EnumVariantFieldSymbol(f.Position, f.Name, SubstituteType(f.Type, substitution, valueSubstitution)))
                    .ToArray(),
                AbsorbsErrorType: variant.AbsorbsErrorType is { } absorbed
                    ? SubstituteType(absorbed, substitution, valueSubstitution)
                    : null,
                Role: variant.Role))
            .ToList();

        return new NamedTypeSymbol(
            key,
            DeclarationKind.Enum,
            new Dictionary<string, FieldSymbol>(StringComparer.Ordinal),
            [],
            EnumVariants: concreteVariants,
            ThreadSafetyLawAttributes: SubstituteThreadSafetyLawAttributes(
                template.ThreadSafetyLaws,
                substitution,
                valueSubstitution),
            DeclaringModuleName: template.DeclaringModuleName,
            Visibility: template.Visibility);
    }

    private NamedTypeSymbol CreateConcreteStructLike(
        string key,
        NamedTypeSymbol template,
        IReadOnlyDictionary<string, StarkTypeSymbol> substitution,
        IReadOnlyDictionary<string, BigInteger> valueSubstitution)
    {
        var concreteFields = new Dictionary<string, FieldSymbol>(StringComparer.Ordinal);
        var concreteOrderedFields = new List<FieldSymbol>();

        foreach (var field in template.OrderedFields)
        {
            var concreteField = field with
            {
                Type = SubstituteType(field.Type, substitution, valueSubstitution),
                ThreadSafetyLawAttributes = SubstituteThreadSafetyLawAttributes(
                    field.ThreadSafetyLaws,
                    substitution,
                    valueSubstitution)
            };
            concreteFields[field.Name] = concreteField;
            concreteOrderedFields.Add(concreteField);
        }

        return new NamedTypeSymbol(
            key,
            template.Kind,
            concreteFields,
            concreteOrderedFields,
            ImplementedTraitNames: template.ImplementedTraits,
            ImplementedTraitTypeSymbols: template.ImplementedTraitTypes.Count == 0
                ? null
                : template.ImplementedTraitTypes
                    .Select(type => SubstituteType(type, substitution, valueSubstitution))
                    .ToArray(),
            IsDynTrait: template.IsDynTrait,
            Layout: template.Layout,
            ThreadSafetyLawAttributes: SubstituteThreadSafetyLawAttributes(
                template.ThreadSafetyLaws,
                substitution,
                valueSubstitution),
            DeclaringModuleName: template.DeclaringModuleName,
            Visibility: template.Visibility,
            HasDestructor: template.HasDestructor);
    }

    private List<ConstructorShape> CreateConcreteConstructors(
        IReadOnlyList<ConstructorShape> templateConstructors,
        IReadOnlyDictionary<string, StarkTypeSymbol> substitution,
        IReadOnlyDictionary<string, BigInteger> valueSubstitution)
    {
        return templateConstructors
            .Select(constructor => new ConstructorShape(
                constructor.Name,
                constructor.Parameters
                    .Select(parameter => new TypedParameterSymbol(
                        parameter.Name,
                        SubstituteType(parameter.Type, substitution, valueSubstitution),
                        parameter.IsDisjoint,
                        parameter.IsConst,
                        parameter.RawPointerElementCountExpression))
                    .ToArray(),
                constructor.IsPrimaryShape,
                constructor.BodyKey))
            .ToList();
    }

    private void PopulateConcreteConstructorShapesForKnownGenericInstantiations()
    {
        foreach (var (key, arguments) in _genericInstantiationArguments.ToArray())
        {
            EnsureConcreteConstructorShapes(key, arguments);
        }
    }

    private void EnsureConcreteConstructorShapes(string key, ConcreteGenericTypeArguments arguments)
    {
        if (_constructors.ContainsKey(key))
        {
            return;
        }

        var baseName = StarkTypeSymbols.GetGenericBaseName(key);
        if (!_constructors.TryGetValue(baseName, out var templateConstructors)
            || templateConstructors.Count == 0
            || !_namedTypes.TryGetValue(baseName, out var template)
            || !template.IsGeneric
            || template.GenericParams.Count != arguments.TypeArguments.Count
            || template.ComptimeGenericParams.Count != arguments.ComptimeValueArguments.Count)
        {
            return;
        }

        var substitution = new Dictionary<string, StarkTypeSymbol>(StringComparer.Ordinal);
        for (var i = 0; i < template.GenericParams.Count; i++)
        {
            substitution[template.GenericParams[i]] = arguments.TypeArguments[i];
        }
        var valueSubstitution = BuildNamedTypeComptimeValueSubstitution(template, arguments.ComptimeValueArguments);

        _constructors[key] = CreateConcreteConstructors(templateConstructors, substitution, valueSubstitution);
    }

    private StarkTypeSymbol SubstituteType(
        StarkTypeSymbol type,
        IReadOnlyDictionary<string, StarkTypeSymbol> substitution,
        IReadOnlyDictionary<string, BigInteger>? valueSubstitution = null)
    {
        var coreType = StarkTypeSymbols.WithQualifiers(
            type,
            borrowKind: StarkBorrowKind.None,
            accessKind: StarkAccessKind.None,
            initializationKind: StarkInitializationKind.None,
            isMutableView: false);
        StarkTypeSymbol substitutedCore;

        if (coreType.Kind == StarkTypeKind.Named && coreType.NamedType is { } name)
        {
            if (substitution.TryGetValue(name, out var substituted))
            {
                substitutedCore = StarkTypeSymbols.WithQualifiers(
                    substituted,
                    borrowKind: StarkBorrowKind.None,
                    accessKind: StarkAccessKind.None,
                    initializationKind: StarkInitializationKind.None,
                    isMutableView: false);
            }
            else if (StarkTypeSymbols.IsGenericInstantiation(coreType))
            {
                var newArgs = (coreType.TypeArguments ?? []).Select(a => SubstituteType(a, substitution, valueSubstitution)).ToArray();
                var newValues = FunctionOverloadFacts.SubstituteComptimeValues(coreType.ComptimeValueArguments, valueSubstitution);
                substitutedCore = EnsureMonomorphizedType(
                    StarkTypeSymbols.GenericInstantiation(
                        StarkTypeSymbols.GetGenericBaseName(name),
                        newArgs,
                        newValues));
            }
            else
            {
                substitutedCore = coreType;
            }
        }
        else if (coreType.Kind == StarkTypeKind.AssociatedType
            && coreType.AssociatedTypeOwner is not null
            && coreType.AssociatedTypeName is not null)
        {
            var substitutedOwner = SubstituteType(coreType.AssociatedTypeOwner, substitution, valueSubstitution);
            substitutedCore = AssociatedTypeFacts.TryResolveAssociatedType(
                    substitutedOwner,
                    coreType.AssociatedTypeName,
                    _namedTypes,
                    out var associatedTarget)
                ? StarkTypeSymbols.WithQualifiers(
                    EnsureMonomorphizedType(associatedTarget),
                    borrowKind: StarkBorrowKind.None,
                    accessKind: StarkAccessKind.None,
                    initializationKind: StarkInitializationKind.None,
                    isMutableView: false)
                : StarkTypeSymbols.AssociatedType(substitutedOwner, coreType.AssociatedTypeName);
        }
        else if (coreType.ElementType is not null)
        {
            var newElement = SubstituteType(coreType.ElementType, substitution, valueSubstitution);
            var fixedLength = coreType.FixedLength;
            var fixedLengthParameterName = coreType.FixedLengthParameterName;
            if (fixedLengthParameterName is not null
                && valueSubstitution is not null
                && valueSubstitution.TryGetValue(fixedLengthParameterName, out var concreteLength)
                && concreteLength >= BigInteger.Zero
                && concreteLength <= int.MaxValue)
            {
                fixedLength = (int)concreteLength;
                fixedLengthParameterName = null;
            }

            if (ReferenceEquals(newElement, coreType.ElementType)
                && fixedLength == coreType.FixedLength
                && string.Equals(fixedLengthParameterName, coreType.FixedLengthParameterName, StringComparison.Ordinal))
            {
                substitutedCore = coreType;
            }
            else
            {
                substitutedCore = coreType.Kind switch
                {
                    StarkTypeKind.FixedArray => StarkTypeSymbols.FixedArray(newElement, fixedLength, fixedLengthParameterName),
                    StarkTypeKind.Slice => StarkTypeSymbols.Slice(newElement),
                    StarkTypeKind.RawPointer => StarkTypeSymbols.RawPointer(newElement, coreType.IsMutablePointer),
                    StarkTypeKind.Dynamic => StarkTypeSymbols.Dynamic(newElement),
                    _ => coreType
                };
            }
        }
        else if (coreType.Kind == StarkTypeKind.FunctionPointer
            && coreType.FunctionPointerKind is { } functionKind
            && coreType.FunctionPointerReturnType is { } returnType
            && coreType.FunctionPointerParameterTypes is { } parameterTypes)
        {
            substitutedCore = StarkTypeSymbols.FunctionPointer(
                functionKind,
                SubstituteType(returnType, substitution, valueSubstitution),
                parameterTypes.Select(parameter => SubstituteType(parameter, substitution, valueSubstitution)).ToArray(),
                coreType.FunctionPointerDisjointParameterGroups,
                coreType.FunctionPointerOverlapParameterGroups,
                coreType.FunctionPointerSameParameterGroups,
                coreType.FunctionPointerParameterRawPointerElementCountExpressions,
                coreType.FunctionPointerAbi,
                coreType.FunctionPointerIsUnsafe);
        }
        else if (coreType.Kind == StarkTypeKind.Closure
            && coreType.ClosureFunctionKind is { } closureFunctionKind
            && coreType.ClosureReturnType is { } closureReturnType
            && coreType.ClosureParameterTypes is { } closureParameterTypes)
        {
            substitutedCore = StarkTypeSymbols.Closure(
                coreType.ClosureStorageKind,
                coreType.ClosureCallCapability,
                closureFunctionKind,
                SubstituteType(closureReturnType, substitution, valueSubstitution),
                closureParameterTypes.Select(parameter => SubstituteType(parameter, substitution, valueSubstitution)).ToArray(),
                coreType.ClosureDisjointParameterGroups,
                coreType.ClosureOverlapParameterGroups,
                coreType.ClosureSameParameterGroups,
                coreType.ClosureParameterRawPointerElementCountExpressions);
        }
        else
        {
            substitutedCore = coreType;
        }

        return StarkTypeSymbols.WithQualifiers(
            substitutedCore,
            borrowKind: type.BorrowKind,
            accessKind: type.AccessKind,
            initializationKind: type.InitializationKind,
            isMutableView: type.IsMutableView);
    }

    private IReadOnlyList<ThreadSafetyLawAttributeSymbol>? SubstituteThreadSafetyLawAttributes(
        IReadOnlyList<ThreadSafetyLawAttributeSymbol> attributes,
        IReadOnlyDictionary<string, StarkTypeSymbol> substitution,
        IReadOnlyDictionary<string, BigInteger>? valueSubstitution = null)
    {
        if (attributes.Count == 0)
        {
            return null;
        }

        return attributes
            .Select(attribute => attribute.Condition is null
                ? attribute
                : attribute with
                {
                    Condition = attribute.Condition with
                    {
                        Type = SubstituteType(attribute.Condition.Type, substitution, valueSubstitution)
                    }
                })
            .ToArray();
    }

    private static IReadOnlyDictionary<string, BigInteger> BuildNamedTypeComptimeValueSubstitution(
        NamedTypeSymbol template,
        IReadOnlyList<ComptimeValueArgumentSymbol> valueArguments)
    {
        if (template.ComptimeGenericParams.Count == 0)
        {
            return new Dictionary<string, BigInteger>(StringComparer.Ordinal);
        }

        var substitution = new Dictionary<string, BigInteger>(StringComparer.Ordinal);
        for (var index = 0; index < template.ComptimeGenericParams.Count && index < valueArguments.Count; index++)
        {
            var parameter = template.ComptimeGenericParams[index];
            var argument = valueArguments[index];
            if (argument.IsSymbolic)
            {
                continue;
            }

            substitution[parameter.Name] = argument.IntegerValue;
        }

        return substitution;
    }

    private void ReportError(string code, string message, SourceLocation location)
    {
        _context.Diagnostics.Error(code, message, "type-check", location);
    }

    private bool TryResolveEnumCaseReference(
        string name,
        out NamedTypeSymbol enumType,
        out StarkTypeSymbol enumTypeSymbol,
        out EnumVariantSymbol variant)
    {
        enumType = null!;
        enumTypeSymbol = StarkTypeSymbols.Error;
        variant = null!;

        var separator = name.LastIndexOf('.');
        if (separator <= 0)
        {
            return false;
        }

        var enumTypeName = name[..separator];
        var variantName = name[(separator + 1)..];
        if (!TryResolveNamedTypeBySourceName(enumTypeName, out enumType)
            || enumType.Kind != DeclarationKind.Enum
            || !enumType.TryGetVariant(variantName, out variant, out _))
        {
            enumType = null!;
            variant = null!;
            return false;
        }

        enumTypeSymbol = StarkTypeSymbols.Named(enumType.Name);
        return true;
    }

    private bool TryResolveEnumCaseReference(
        StarkParser.GenericEnumCaseReferenceContext genericEnumCaseReference,
        out NamedTypeSymbol enumType,
        out StarkTypeSymbol enumTypeSymbol,
        out EnumVariantSymbol variant)
    {
        enumType = null!;
        enumTypeSymbol = StarkTypeSymbols.Error;
        variant = null!;

        enumTypeSymbol = ResolveGenericQualifiedName(genericEnumCaseReference.genericQualifiedName());
        if (enumTypeSymbol.Kind != StarkTypeKind.Named
            || enumTypeSymbol.NamedType is null
            || !_namedTypes.TryGetValue(enumTypeSymbol.NamedType, out var resolvedEnumType)
            || resolvedEnumType.Kind != DeclarationKind.Enum
            || !resolvedEnumType.TryGetVariant(genericEnumCaseReference.Identifier().GetText(), out var resolvedVariant, out _))
        {
            enumType = null!;
            enumTypeSymbol = StarkTypeSymbols.Error;
            variant = null!;
            return false;
        }

        enumType = resolvedEnumType;
        variant = resolvedVariant;
        return true;
    }

    private bool TryResolveEnumCaseTarget(
        StarkParser.EnumCaseTargetContext enumCaseTarget,
        out string caseName,
        out NamedTypeSymbol enumType,
        out StarkTypeSymbol enumTypeSymbol,
        out EnumVariantSymbol variant)
    {
        caseName = enumCaseTarget.GetText();
        if (enumCaseTarget.genericEnumCaseReference() is { } genericEnumCaseReference)
        {
            return TryResolveEnumCaseReference(genericEnumCaseReference, out enumType, out enumTypeSymbol, out variant);
        }

        return TryResolveEnumCaseReference(enumCaseTarget.dottedName().GetText(), out enumType, out enumTypeSymbol, out variant);
    }

    private bool TryResolveGlobalBySourceName(
        string name,
        out VariableSymbol global,
        out IReadOnlyList<string> ambiguousImportedNames)
    {
        ambiguousImportedNames = [];

        if (_globals.TryGetValue(name, out global!))
        {
            return true;
        }

        if (!name.Contains('.', StringComparison.Ordinal)
            && _globals.TryGetValue($"{CurrentFunctionModuleName}.{name}", out global!))
        {
            return true;
        }

        if (!name.Contains('.', StringComparison.Ordinal))
        {
            var importedMatches = _moduleGraph.EnumerateAccessibleModuleQualifiedNames(CurrentFunctionModuleName, name)
                .Where(_globals.ContainsKey)
                .ToArray();
            if (importedMatches.Length == 1)
            {
                global = _globals[importedMatches[0]];
                return true;
            }

            if (importedMatches.Length > 1)
            {
                ambiguousImportedNames = importedMatches;
            }
        }

        global = null!;
        return false;
    }

    private bool TryResolveNamedTypeBySourceName(string typeName, out NamedTypeSymbol namedType)
    {
        return TryResolveNamedTypeBySourceName(typeName, out namedType, out _);
    }

    private bool TryResolveNamedTypeBySourceName(
        string typeName,
        out NamedTypeSymbol namedType,
        out IReadOnlyList<string> ambiguousImportedNames)
    {
        ambiguousImportedNames = [];

        if (!typeName.Contains('.', StringComparison.Ordinal)
            && string.Equals(CurrentFunctionModuleName, _syntaxModel.ModuleName, StringComparison.Ordinal)
            && _namedTypes.TryGetValue(typeName, out namedType!))
        {
            return true;
        }

        if (!typeName.Contains('.', StringComparison.Ordinal)
            && _namedTypes.TryGetValue($"{CurrentFunctionModuleName}.{typeName}", out namedType!))
        {
            return true;
        }

        if (!typeName.Contains('.', StringComparison.Ordinal))
        {
            var importedMatches = _moduleGraph.EnumerateAccessibleModuleQualifiedNames(CurrentFunctionModuleName, typeName)
                .Where(_namedTypes.ContainsKey)
                .ToArray();
            if (importedMatches.Length == 1)
            {
                namedType = _namedTypes[importedMatches[0]];
                return true;
            }

            if (importedMatches.Length > 1)
            {
                ambiguousImportedNames = importedMatches;
            }
        }

        if (_namedTypes.TryGetValue(typeName, out namedType!))
        {
            return true;
        }

        namedType = null!;
        return false;
    }

    private string CurrentFunctionModuleName => _currentFunctionModuleName ?? _syntaxModel.ModuleName;

    private static bool IsCompilerKnownNamespace(string name)
    {
        return string.Equals(name, "System", StringComparison.Ordinal)
            || string.Equals(name, StarkCDataModelFacts.ModuleName, StringComparison.Ordinal);
    }

    private string GetSystemTextFunctionName(string name)
    {
        return string.Equals(CurrentFunctionModuleName, "System.Text", StringComparison.Ordinal)
            ? name
            : $"System.Text.{name}";
    }

    private static StarkParser.AdditiveExpressionContext? TryGetStandaloneAdditiveExpression(StarkParser.ExpressionContext expression)
    {
        var assignment = expression.assignmentExpression();
        if (assignment.assignmentOperator() is not null || assignment.conditionalExpression() is not { } conditional)
        {
            return null;
        }

        if (conditional.expression().Length != 0)
        {
            return null;
        }

        var logicalOr = conditional.logicalOrExpression();
        if (logicalOr.logicalAndExpression().Length != 1)
        {
            return null;
        }

        var logicalAnd = logicalOr.logicalAndExpression(0);
        if (logicalAnd.bitwiseOrExpression().Length != 1)
        {
            return null;
        }

        var bitwiseOr = logicalAnd.bitwiseOrExpression(0);
        if (bitwiseOr.bitwiseXorExpression().Length != 1)
        {
            return null;
        }

        var bitwiseXor = bitwiseOr.bitwiseXorExpression(0);
        if (bitwiseXor.bitwiseAndExpression().Length != 1)
        {
            return null;
        }

        var bitwiseAnd = bitwiseXor.bitwiseAndExpression(0);
        if (bitwiseAnd.equalityExpression().Length != 1)
        {
            return null;
        }

        var equality = bitwiseAnd.equalityExpression(0);
        if (equality.relationalExpression().Length != 1)
        {
            return null;
        }

        var relational = equality.relationalExpression(0);
        if (relational.shiftExpression().Length != 1)
        {
            return null;
        }

        var shift = relational.shiftExpression(0);
        return shift.additiveExpression().Length == 1
            ? shift.additiveExpression(0)
            : null;
    }

    private static StarkParser.LiteralContext? TryGetStandaloneInterpolatedTextLiteral(StarkParser.ExpressionContext expression)
    {
        var additive = TryGetStandaloneAdditiveExpression(expression);
        if (additive is null || additive.multiplicativeExpression().Length != 1)
        {
            return null;
        }

        var multiplicative = additive.multiplicativeExpression(0);
        if (multiplicative.unaryExpression().Length != 1)
        {
            return null;
        }

        var unary = multiplicative.unaryExpression(0);
        if (unary.powerExpression() is not { } power
            || power.unaryExpression() is not null
            || power.postfixExpression() is not { } postfix
            || postfix.postfixPart().Length != 0)
        {
            return null;
        }

        var literal = postfix.primaryExpression().literal();
        return literal?.DOLLAR() is not null && literal.StringLiteral() is not null
            ? literal
            : null;
    }

    private static IReadOnlyList<string> ExtractOperators<TOperand>(ParserRuleContext context)
        where TOperand : ParserRuleContext
    {
        var operators = new List<string>();
        var builder = new System.Text.StringBuilder();

        for (var index = 0; index < context.ChildCount; index++)
        {
            var child = context.GetChild(index);
            if (child is TOperand)
            {
                if (builder.Length > 0)
                {
                    operators.Add(builder.ToString());
                    builder.Clear();
                }

                continue;
            }

            builder.Append(child.GetText());
        }

        return operators;
    }

    // A bare integer literal is typed as the smallest signed singleton range that
    // holds its value (e.g. `1` is `i8[1 1]`). Reconciling that against a runtime
    // ranged operand in `FindCommonType` misses the same-type fast path and merges
    // to a full-width, opposite-signed default (`u64[...] + 1` becomes `i64`),
    // which then demands a narrowing cast. When the chain has a single ranged
    // integer "anchor" shared by every non-literal integer operand, let each
    // fitting literal adopt that anchor so the literal stops dragging the
    // expression's type. Non-fitting literals keep their own type (a genuine
    // out-of-range value still requires an explicit cast).
    private static StarkTypeSymbol[] ResolveIntegerLiteralOperandTypes(IReadOnlyList<ExpressionBinding> operands)
    {
        StarkTypeSymbol? anchor = null;
        var hasNonLiteralInteger = false;
        foreach (var operand in operands)
        {
            if (operand.IntegerLiteralValue is not null)
            {
                continue;
            }

            if (operand.Type.Kind != StarkTypeKind.Integer || operand.Type.BitWidth is null)
            {
                continue;
            }

            hasNonLiteralInteger = true;
            anchor = anchor is null ? operand.Type : FindCommonType(anchor, operand.Type);
        }

        var effective = new StarkTypeSymbol[operands.Count];
        for (var index = 0; index < operands.Count; index++)
        {
            effective[index] = operands[index].Type;
        }

        if (!hasNonLiteralInteger
            || anchor is not { Kind: StarkTypeKind.Integer, BitWidth: int anchorWidth })
        {
            return effective;
        }

        // Adopt a PLAIN integer of the anchor's numeric shape (width/range/sign),
        // stripping any out/init/borrow/frozen qualifiers the anchor operand
        // carries: arithmetic on a qualified operand yields a plain value, so the
        // literal must not inherit those qualifiers (else e.g. `outParam * 2`
        // would type as `out u64` and reject the surrounding conversion).
        var cleanAnchor = StarkTypeSymbols.Integer(
            anchorWidth,
            anchor.RangeMin,
            anchor.RangeMax,
            anchor.IsUnsigned);
        if (!StarkTypeSymbols.TryGetEffectiveIntegerBounds(cleanAnchor, out var min, out var max))
        {
            return effective;
        }

        for (var index = 0; index < operands.Count; index++)
        {
            if (operands[index].IntegerLiteralValue is { } literalValue
                && literalValue >= min
                && literalValue <= max)
            {
                effective[index] = cleanAnchor;
            }
        }

        return effective;
    }

    private ExpressionBinding EvaluateBinaryChain(
        IReadOnlyList<ExpressionBinding> operands,
        IReadOnlyList<string> operators,
        ParserRuleContext context,
        string operatorFamily,
        bool requireInteger = false)
    {
        if (operators.Count == 0)
        {
            return operands[0];
        }

        var effectiveTypes = ResolveIntegerLiteralOperandTypes(operands);
        var currentType = effectiveTypes[0];

        for (var index = 1; index < operands.Count; index++)
        {
            var nextType = effectiveTypes[index];
            if (requireInteger)
            {
                if (currentType.Kind != StarkTypeKind.Integer || nextType.Kind != StarkTypeKind.Integer)
                {
                    ReportError("STK3002", $"{operatorFamily} requires integer operands.", context);
                    return new ExpressionBinding(StarkTypeSymbols.Error);
                }
            }
            else if (!IsNumeric(currentType) || !IsNumeric(nextType))
            {
                ReportError("STK3002", $"{operatorFamily} requires numeric operands.", context);
                return new ExpressionBinding(StarkTypeSymbols.Error);
            }

            currentType = FindCommonType(currentType, nextType);
            if (currentType.Kind == StarkTypeKind.Error)
            {
                ReportError(
                    "STK3002",
                    $"Operator '{operators[index - 1]}' is not defined for '{operands[index - 1].Type.DisplayName}' and '{nextType.DisplayName}'.",
                    context);
                return new ExpressionBinding(StarkTypeSymbols.Error);
            }
        }

        return new ExpressionBinding(currentType);
    }

    private ExpressionBinding EvaluateArithmeticChain(
        IReadOnlyList<ExpressionBinding> operands,
        IReadOnlyList<string> operators,
        ParserRuleContext context,
        string operatorFamily)
    {
        if (operators.Count == 0)
        {
            return operands[0];
        }

        var effectiveTypes = ResolveIntegerLiteralOperandTypes(operands);
        var currentType = effectiveTypes[0];

        for (var index = 1; index < operands.Count; index++)
        {
            var nextType = effectiveTypes[index];
            var operatorText = operators[index - 1];

            if (IsExplicitArithmeticOperator(operatorText))
            {
                if (currentType.Kind != StarkTypeKind.Integer || nextType.Kind != StarkTypeKind.Integer)
                {
                    ReportError("STK3002", $"Operator '{operatorText}' requires integer operands.", context);
                    return new ExpressionBinding(StarkTypeSymbols.Error);
                }
            }
            else if (!IsNumeric(currentType) || !IsNumeric(nextType))
            {
                ReportError("STK3002", $"{operatorFamily} requires numeric operands.", context);
                return new ExpressionBinding(StarkTypeSymbols.Error);
            }

            currentType = FindCommonType(currentType, nextType);
            if (currentType.Kind == StarkTypeKind.Error)
            {
                ReportError(
                    "STK3002",
                    $"Operator '{operatorText}' is not defined for '{operands[index - 1].Type.DisplayName}' and '{nextType.DisplayName}'.",
                    context);
                return new ExpressionBinding(StarkTypeSymbols.Error);
            }
        }

        return new ExpressionBinding(currentType);
    }

    private ExpressionBinding EvaluateTextConcatenationChain(
        IReadOnlyList<ExpressionBinding> operands,
        IReadOnlyList<string> operators,
        ParserRuleContext context,
        StarkTypeSymbol? expectedType)
    {
        var current = operands[0];
        var isFixedTextStorageConcat = expectedType is not null
            && IsTextBufferType(expectedType)
            && context is StarkParser.AdditiveExpressionContext additive
            && _fixedTextStorageConcatExpressions.Contains(additive);
        if (current.Type.Kind == StarkTypeKind.Error)
        {
            return new ExpressionBinding(StarkTypeSymbols.Error);
        }

        if (!IsTextLikeForConcatenation(current.Type))
        {
            ReportError(
                "STK3002",
                $"Text concatenation needs text on both sides of '+', but the left side is '{current.Type.DisplayName}'. Convert the value to text first.",
                context);
            return new ExpressionBinding(StarkTypeSymbols.Error);
        }

        if (isFixedTextStorageConcat && !CanUseFixedTextConcatSource(expectedType!, current.Type))
        {
            ReportError(
                "STK3002",
                $"Fixed {expectedType!.DisplayName} text buffers can only join matching text values. The left side is '{current.Type.DisplayName}'.",
                context);
            return new ExpressionBinding(StarkTypeSymbols.Error);
        }

        for (var index = 1; index < operands.Count; index++)
        {
            var operatorText = operators[index - 1];
            var next = operands[index];
            if (next.Type.Kind == StarkTypeKind.Error)
            {
                return new ExpressionBinding(StarkTypeSymbols.Error);
            }

            if (operatorText != "+")
            {
                ReportError("STK3002", $"Text can be joined with '+', but not with '{operatorText}'.", context);
                return new ExpressionBinding(StarkTypeSymbols.Error);
            }

            if (!IsTextLikeForConcatenation(next.Type))
            {
                if (!isFixedTextStorageConcat
                    && operatorText == "+"
                    && TryResolveRuntimeTextConcatenation(current, next, context, out var runtimeConcat))
                {
                    current = runtimeConcat;
                    continue;
                }

                ReportError(
                    "STK3002",
                    $"Text concatenation needs text on both sides of '+', but the right side is '{next.Type.DisplayName}'. Convert the value to text first.",
                    context);
                return new ExpressionBinding(StarkTypeSymbols.Error);
            }

            if (isFixedTextStorageConcat)
            {
                if (!CanUseFixedTextConcatSource(expectedType!, next.Type))
                {
                    ReportError(
                        "STK3002",
                        $"Fixed {expectedType!.DisplayName} text buffers can only join matching text values. The right side is '{next.Type.DisplayName}'.",
                        context);
                    return new ExpressionBinding(StarkTypeSymbols.Error);
                }

                current = new ExpressionBinding(GetFixedTextConcatViewType(expectedType!));
                continue;
            }

            if (current.TextLiteral is null || current.TextLiteralKind is null || next.TextLiteral is null || next.TextLiteralKind is null)
            {
                if (IsTextBufferType(current.Type) || IsTextBufferType(next.Type))
                {
                    ReportError(
                        "STK3002",
                        "Runtime Ascii and Unicode buffers need a destination capacity for '+'. Write a stack buffer such as `stack Ascii combined[4096] = left + right;`, or call System.Text.TryConcatAscii/TryConcatUnicode yourself.",
                        context);
                    return new ExpressionBinding(StarkTypeSymbols.Error);
                }

                ReportError(
                    "STK3002",
                    "Only compile-time text constants can use '+' for now. For runtime text, use System.Text.TryConcatAscii or System.Text.TryConcatUnicode with caller-owned storage.",
                    context);
                return new ExpressionBinding(StarkTypeSymbols.Error);
            }

            if (!TextLiteralDecoder.TryConcatenateAsStringLiteral(
                    current.TextLiteral,
                    current.TextLiteralKind.Value,
                    next.TextLiteral,
                    next.TextLiteralKind.Value,
                    out var literalText))
            {
                ReportError("STK3002", "Text concatenation could not decode one of the text constants.", context);
                return new ExpressionBinding(StarkTypeSymbols.Error);
            }

            current = new ExpressionBinding(
                FindCommonTextType(current.Type, next.Type),
                TextLiteral: literalText,
                TextLiteralKind: TextLiteralKind.String,
                HasConstProvenance: HasConstProvenance(current) && HasConstProvenance(next));
        }

        if (isFixedTextStorageConcat)
        {
            _boundOperations.Add(new BoundTextBuildOperation(
                "concat",
                expectedType!,
                operands.Count,
                UsesFixedStorage: true,
                Location(context),
                _currentFunctionName));
            return new ExpressionBinding(
                expectedType!,
                NamedType: ResolveNamedTypeSymbol(expectedType!),
                DiagnosticName: "fixed-capacity text concatenation");
        }

        if (expectedType is not null
            && IsTextType(expectedType)
            && CanExplicitlyConvertTextLiteral(expectedType, current))
        {
            return new ExpressionBinding(
                expectedType,
                TextLiteral: current.TextLiteral,
                TextLiteralKind: current.TextLiteralKind,
                HasConstProvenance: HasConstProvenance(current));
        }

        return current;
    }

    private bool TryResolveRuntimeTextConcatenation(
        ExpressionBinding left,
        ExpressionBinding right,
        ParserRuleContext context,
        out ExpressionBinding result)
    {
        result = default!;

        if (!IsTextType(left.Type))
        {
            return false;
        }

        if (left.TextLiteral is null || left.TextLiteralKind is null)
        {
            return false;
        }

        var sourceName = GetSystemTextFunctionName(left.Type.Kind == StarkTypeKind.Unicode
            ? "ConcatUnicode"
            : "ConcatAscii");
        if (!TryGetFunctionOverloads(sourceName, out var overloads))
        {
            return false;
        }

        var resolution = FunctionOverloadFacts.Resolve(
            overloads,
            receiverType: null,
            [left.Type, NonNegativeI64Type, right.Type],
            CanAssign,
            ResolveAssociatedTypeForSubstitution);
        if (!resolution.Succeeded)
        {
            return false;
        }

        var signature = CacheFunctionInstantiation(resolution.Match!);
        RecordDirectCall(signature, context);
        _boundOperations.Add(new BoundTextBuildOperation(
            "runtime-concat",
            signature.ReturnType,
            OperandCount: 2,
            UsesFixedStorage: false,
            Location(context),
            _currentFunctionName));
        result = new ExpressionBinding(
            signature.ReturnType,
            NamedType: ResolveNamedTypeSymbol(signature.ReturnType),
            DiagnosticName: $"runtime text concatenation");
        return true;
    }

    private ExpressionBinding EnsureBooleanUnary(ExpressionBinding operand, ParserRuleContext context)
    {
        EnsureBoolean(operand.Type, context, "Logical negation requires a 'bool' operand");
        return new ExpressionBinding(StarkTypeSymbols.Bool);
    }

    private ExpressionBinding EnsureIntegerUnary(ExpressionBinding operand, ParserRuleContext context, string op)
    {
        if (operand.Type.Kind != StarkTypeKind.Integer)
        {
            ReportError("STK3002", $"Operator '{op}' requires an integer operand.", context);
            return new ExpressionBinding(StarkTypeSymbols.Error);
        }

        return new ExpressionBinding(operand.Type);
    }

    private ExpressionBinding EnsureNumericUnary(ExpressionBinding operand, ParserRuleContext context, string op)
    {
        if (!IsNumeric(operand.Type))
        {
            ReportError("STK3002", $"Operator '{op}' requires a numeric operand.", context);
            return new ExpressionBinding(StarkTypeSymbols.Error);
        }

        return new ExpressionBinding(operand.Type);
    }

    private ExpressionBinding EnsureAddressOfUnary(ExpressionBinding operand, ParserRuleContext context)
    {
        RequireUnsafeContext("Raw pointer address-of operator '&'", context);

        if (!operand.IsAddressable)
        {
            ReportError("STK3002", "Operator '&' requires an addressable value.", context);
            return new ExpressionBinding(StarkTypeSymbols.Error);
        }

        var pointeeType = UsesFrozenProjectionSemantics(operand)
            ? StarkTypeSymbols.FreezeAddressPointeeType(operand.Type)
            : operand.Type;
        var pointerType = StarkTypeSymbols.RawPointer(pointeeType, operand.IsAddressMutable);
        return new ExpressionBinding(
            pointerType,
            NamedType: ResolveNamedTypeSymbol(pointerType),
            HasConstProvenance: HasConstProvenance(operand),
            MemoryRootKey: operand.MemoryRootKey,
            MemoryRootIsIndependentStorage: operand.MemoryRootIsIndependentStorage
                || operand.MemoryRootKey is not null);
    }

    private ExpressionBinding EnsureDereferenceUnary(ExpressionBinding operand, ParserRuleContext context)
    {
        RequireUnsafeContext("Raw pointer dereference operator '*'", context);

        if (operand.Type.Kind != StarkTypeKind.RawPointer || operand.Type.ElementType is null)
        {
            ReportError("STK3002", "Operator '*' requires a raw pointer operand.", context);
            return new ExpressionBinding(StarkTypeSymbols.Error);
        }

        var pointeeType = operand.Type.ElementType;
        var resultType = pointeeType.AccessKind == StarkAccessKind.Frozen
            ? StarkTypeSymbols.FreezeReachableView(pointeeType)
            : pointeeType;
        var isAddressMutable = operand.Type.IsMutablePointer && pointeeType.AccessKind != StarkAccessKind.Frozen;
        return new ExpressionBinding(
            resultType,
            IsAssignable: isAddressMutable,
            NamedType: ResolveNamedTypeSymbol(resultType),
            DiagnosticName: "dereferenced value",
            IsAddressable: true,
            IsAddressMutable: isAddressMutable,
            AssignmentErrorMessage: isAddressMutable
                ? null
                : pointeeType.AccessKind == StarkAccessKind.Frozen
                    ? DescribeFrozenMutationError("dereferenced value")
                    : null,
            UsesFrozenProjectionSemantics: UsesFrozenProjectionSemantics(operand)
                || pointeeType.AccessKind == StarkAccessKind.Frozen,
            HasConstProvenance: HasConstProvenance(operand),
            MemoryRootKey: operand.MemoryRootKey,
            MemoryRootIsIndependentStorage: operand.MemoryRootIsIndependentStorage);
    }

    private void EnsureBoolean(StarkTypeSymbol type, ParserRuleContext context, string message)
    {
        if (type.Kind != StarkTypeKind.Bool && type.Kind != StarkTypeKind.Error)
        {
            ReportError("STK3002", message, context);
        }
    }

    private ExpressionBinding MakeInitDestinationBinding(ExpressionBinding operand, ParserRuleContext context)
    {
        if (!operand.IsAddressable)
        {
            ReportError(
                "STK3002",
                $"Initialization destination '{operand.DiagnosticName ?? operand.Type.DisplayName}' must be addressable storage.",
                context);
            return new ExpressionBinding(StarkTypeSymbols.Error);
        }

        if (!operand.IsAddressMutable)
        {
            ReportError(
                "STK3002",
                operand.AssignmentErrorMessage
                    ?? $"Initialization destination '{operand.DiagnosticName ?? operand.Type.DisplayName}' must be mutable.",
                context);
            return new ExpressionBinding(StarkTypeSymbols.Error);
        }

        var initType = StarkTypeSymbols.WithQualifiers(
            operand.Type,
            initializationKind: StarkInitializationKind.Init,
            isMutableView: operand.Type.Kind == StarkTypeKind.Slice || operand.Type.IsMutableView);
        return operand with
        {
            Type = initType,
            IsAssignable = true,
            NamedType = ResolveNamedTypeSymbol(initType),
            IsAddressable = true,
            IsAddressMutable = true,
            DiagnosticName = operand.DiagnosticName is null
                ? "initialization destination"
                : $"initialization destination for {operand.DiagnosticName}"
        };
    }

    private ConstructorShape? CheckObjectCreationArguments(
        StarkParser.ArgumentListContext? arguments,
        ParserRuleContext diagnosticContext,
        StarkTypeSymbol createdType,
        Scope scope)
    {
        var suppliedArguments = arguments?.argument() ?? [];
        var argumentCount = suppliedArguments.Length;

        if (createdType.Kind == StarkTypeKind.Dynamic)
        {
            if (argumentCount > 1)
            {
                ReportError(
                    "STK3009",
                    $"Dynamic storage creation expects zero arguments or one capacity argument, but received {argumentCount}.",
                    diagnosticContext);
                return null;
            }

            if (argumentCount == 1)
            {
                var capacity = EvaluateExpression(
                    suppliedArguments[0].expression(),
                    scope,
                    allowFunctionReference: false,
                    NonNegativeI64Type);
                if (capacity.Type.Kind != StarkTypeKind.Integer)
                {
                    ReportError(
                        "STK3002",
                        $"Dynamic storage capacity must be an integer, but found '{capacity.Type.DisplayName}'.{GetExplicitConversionHint(StarkTypeSymbols.Integer(64), capacity.Type)}",
                        suppliedArguments[0].expression());
                }
                else if (!IsProvablyNonNegativeIntegerType(capacity.Type))
                {
                    ReportError(
                        "STK3002",
                        "Dynamic storage capacity must be provably non-negative.",
                        suppliedArguments[0].expression());
                }
            }

            return null;
        }

        if (createdType.Kind != StarkTypeKind.Named || createdType.NamedType is null)
        {
            if (argumentCount == 0)
            {
                return null;
            }

            ReportError(
                "STK3009",
                $"Type '{createdType.DisplayName}' does not declare constructors and cannot be created with arguments.",
                diagnosticContext);
            return null;
        }

        if (!_namedTypes.ContainsKey(createdType.NamedType))
        {
            return null;
        }

        if (!_constructors.TryGetValue(createdType.NamedType, out var constructors) || constructors.Count == 0)
        {
            if (argumentCount == 0)
            {
                return null;
            }

            ReportError(
                "STK3009",
                $"Type '{createdType.DisplayName}' does not declare a constructor that accepts {argumentCount} argument{Pluralize(argumentCount)}.",
                diagnosticContext);
            return null;
        }

        var arityMatches = constructors
            .Where(candidate => candidate.Parameters.Count == argumentCount)
            .ToArray();

        if (arityMatches.Length == 0)
        {
            var availableArities = string.Join(", ", constructors.Select(static candidate => candidate.Parameters.Count).Distinct().OrderBy(static value => value));
            ReportError(
                "STK3009",
                $"Type '{createdType.DisplayName}' does not declare a constructor that accepts {argumentCount} argument{Pluralize(argumentCount)}. Available constructor arities: {availableArities}.",
                diagnosticContext);
            return null;
        }

        StarkTypeSymbol[] argumentTypes;
        ConstructorShape matchedConstructor;
        if (arityMatches.Length == 1)
        {
            matchedConstructor = arityMatches[0];
            argumentTypes = arguments is null
                ? []
                : EvaluateArgumentTypes(arguments, matchedConstructor.Parameters, scope);
        }
        else
        {
            argumentTypes = arguments is null
                ? []
                : EvaluateArgumentTypes(arguments, expectedParameters: null, scope);
            matchedConstructor = arityMatches
                .OrderBy(candidate => CountMismatchedParameters(candidate.Parameters, argumentTypes))
                .First();
        }

        var hadMismatch = false;

        for (var index = 0; index < matchedConstructor.Parameters.Count; index++)
        {
            var parameter = matchedConstructor.Parameters[index];
            var argumentType = argumentTypes[index];
            if (CanAssign(parameter.Type, argumentType))
            {
                continue;
            }

            hadMismatch = true;
            ReportError(
                "STK3002",
                $"Constructor argument {index + 1} for '{createdType.DisplayName}' expects '{parameter.Type.DisplayName}' but found '{argumentType.DisplayName}'.{GetExplicitConversionHint(parameter.Type, argumentType)}",
                suppliedArguments[index].expression());
        }

        return hadMismatch ? null : matchedConstructor;
    }

    private StarkTypeSymbol[] EvaluateArgumentTypes(
        StarkParser.ArgumentListContext arguments,
        IReadOnlyList<TypedParameterSymbol>? expectedParameters,
        Scope scope)
    {
        return EvaluateArguments(arguments, expectedParameters, scope)
            .Select(static argument => argument.Type)
            .ToArray();
    }

    private ExpressionBinding[] EvaluateArguments(
        StarkParser.ArgumentListContext arguments,
        IReadOnlyList<TypedParameterSymbol>? expectedParameters,
        Scope scope)
    {
        var argumentBindings = new ExpressionBinding[arguments.argument().Length];
        for (var index = 0; index < arguments.argument().Length; index++)
        {
            var expectedType = expectedParameters is not null && index < expectedParameters.Count
                ? GetExpectedParameterExpressionType(expectedParameters[index])
                : null;
            argumentBindings[index] = EvaluateExpression(
                arguments.argument(index).expression(),
                scope,
                allowFunctionReference: false,
                expectedType);
        }

        return argumentBindings;
    }

    private static StarkTypeSymbol GetExpectedParameterExpressionType(TypedParameterSymbol parameter)
    {
        return parameter.IsConst
            ? StarkTypeSymbols.FreezeReachableView(parameter.Type)
            : parameter.Type;
    }

    private int CountMismatchedParameters(IReadOnlyList<TypedParameterSymbol> parameters, IReadOnlyList<StarkTypeSymbol> arguments)
    {
        var mismatches = 0;
        for (var index = 0; index < parameters.Count; index++)
        {
            if (!CanAssign(parameters[index].Type, arguments[index]))
            {
                mismatches++;
            }
        }

        return mismatches;
    }

    private void EnsureAssignmentCompatible(
        string? variableName,
        StarkTypeSymbol targetType,
        StarkTypeSymbol valueType,
        ParserRuleContext context,
        bool isConstant)
    {
        if (CanAssign(targetType, valueType))
        {
            return;
        }

        var subject = variableName is null
            ? "Assignment"
            : isConstant
                ? $"Assignment to constant '{variableName}'"
                : $"Assignment to variable '{variableName}'";
        ReportError("STK3002", $"{subject} expects '{targetType.DisplayName}' but found '{valueType.DisplayName}'.{GetExplicitConversionHint(targetType, valueType)}", context);
    }

    private void EnsureAssignmentTargetCompatible(ExpressionBinding target, StarkTypeSymbol valueType, ParserRuleContext context)
    {
        if (CanAssign(target.Type, valueType))
        {
            return;
        }

        if (target.Type.InitializationKind != StarkInitializationKind.None)
        {
            var storageType = StarkTypeSymbols.WithQualifiers(target.Type, initializationKind: StarkInitializationKind.None);
            var valueStorageType = valueType.InitializationKind == StarkInitializationKind.None
                ? valueType
                : StarkTypeSymbols.WithQualifiers(valueType, initializationKind: StarkInitializationKind.None);
            if (CanAssign(storageType, valueStorageType))
            {
                return;
            }
        }

        ReportError(
            "STK3002",
            $"Assignment to {target.DiagnosticName ?? "target"} expects '{target.Type.DisplayName}' but found '{valueType.DisplayName}'.{GetExplicitConversionHint(target.Type, valueType)}",
            context);
    }

    private void EnsureReturnCompatible(StarkTypeSymbol returnType, ExpressionBinding value, ParserRuleContext context)
    {
        var valueType = value.Type;
        if (CanAssign(returnType, valueType))
        {
            if (returnType.BorrowKind != StarkBorrowKind.None
                && TryRejectMisalignedSafeBorrow("Return statement", value, context))
            {
                return;
            }

            return;
        }

        if (returnType.BorrowKind != StarkBorrowKind.None
            && !StarkTypeSymbols.IsPointerBackedBorrowReturn(returnType)
            && CanAssign(StarkTypeSymbols.BorrowReturnValueType(returnType), valueType))
        {
            return;
        }

        if (returnType.BorrowKind != StarkBorrowKind.None
            && StarkTypeSymbols.IsPointerBackedBorrowReturn(returnType)
            && value.IsAddressable
            && (!returnType.IsMutableView || value.IsAddressMutable))
        {
            var returnedStorageType = StarkTypeSymbols.BorrowReturnValueType(returnType);
            if (CanAssign(returnedStorageType, valueType))
            {
                if (TryRejectMisalignedSafeBorrow("Return statement", value, context))
                {
                    return;
                }

                return;
            }
        }

        ReportError(
            "STK3002",
            $"Return statement expects '{returnType.DisplayName}' but found '{valueType.DisplayName}'.{GetExplicitConversionHint(returnType, valueType)}",
            context);
    }

    private void EnsureCallArgumentCompatible(
        string functionName,
        int position,
        TypedParameterSymbol parameter,
        ExpressionBinding argument,
        ParserRuleContext context)
    {
        var parameterType = parameter.Type;
        if (parameter.IsConst)
        {
            EnsureConstCallArgumentCompatible(functionName, position, parameter, argument, context);
            return;
        }

        var argumentType = argument.Type;
        if (parameterType.InitializationKind != StarkInitializationKind.None)
        {
            if (!argument.IsAddressable)
            {
                ReportError(
                    "STK3002",
                    $"Argument {position} for '{functionName}' must be an addressable storage location because parameter type '{parameterType.DisplayName}' writes through it.",
                    context);
                return;
            }

            if (!argument.IsAddressMutable)
            {
                ReportError(
                    "STK3002",
                    $"Argument {position} for '{functionName}' must be mutable because parameter type '{parameterType.DisplayName}' writes through it.",
                    context);
                return;
            }

            var parameterStorageType = StarkTypeSymbols.WithQualifiers(parameterType, initializationKind: StarkInitializationKind.None);
            var argumentStorageType = argumentType.InitializationKind == StarkInitializationKind.None
                ? argumentType
                : StarkTypeSymbols.WithQualifiers(argumentType, initializationKind: StarkInitializationKind.None);
            if (CanAssign(parameterStorageType, argumentStorageType))
            {
                if (TryRejectMisalignedSafeBorrow($"Argument {position} for '{functionName}'", argument, context))
                {
                    return;
                }

                return;
            }
        }

        if (CanAssign(parameterType, argumentType))
        {
            if (parameterType.BorrowKind != StarkBorrowKind.None
                && TryRejectMisalignedSafeBorrow($"Argument {position} for '{functionName}'", argument, context))
            {
                return;
            }

            return;
        }

        if (parameterType.IsMutableView && argument.IsAddressMutable)
        {
            var mutableArgumentType = StarkTypeSymbols.WithQualifiers(argumentType, isMutableView: true);
            if (CanAssign(parameterType, mutableArgumentType))
            {
                return;
            }
        }

        if (parameterType.BorrowKind != StarkBorrowKind.None)
        {
            if (!argument.IsAddressable)
            {
                ReportError(
                    "STK3002",
                    $"Argument {position} for '{functionName}' must be an addressable storage location because parameter type '{parameterType.DisplayName}' borrows from it.",
                    context);
                return;
            }

            if (parameterType.IsMutableView && !argument.IsAddressMutable)
            {
                ReportError(
                    "STK3002",
                    $"Argument {position} for '{functionName}' must be mutable because parameter type '{parameterType.DisplayName}' borrows it mutably.",
                    context);
                return;
            }

            var parameterStorageType = StarkTypeSymbols.WithQualifiers(
                parameterType,
                borrowKind: StarkBorrowKind.None);
            var argumentStorageType = StarkTypeSymbols.WithQualifiers(
                argumentType,
                borrowKind: StarkBorrowKind.None);
            if (parameterType.IsMutableView && argument.IsAddressMutable)
            {
                argumentStorageType = StarkTypeSymbols.WithQualifiers(
                    argumentStorageType,
                    isMutableView: true);
            }

            if (CanAssign(parameterStorageType, argumentStorageType))
            {
                if (TryRejectMisalignedSafeBorrow($"Argument {position} for '{functionName}'", argument, context))
                {
                    return;
                }

                return;
            }
        }

        ReportError(
            "STK3002",
            $"Argument {position} for '{functionName}' expects '{parameterType.DisplayName}' but found '{argumentType.DisplayName}'.{GetExplicitConversionHint(parameterType, argumentType)}",
            context);
    }

    private void EnsureConstCallArgumentCompatible(
        string functionName,
        int position,
        TypedParameterSymbol parameter,
        ExpressionBinding argument,
        ParserRuleContext context)
    {
        var parameterType = GetExpectedParameterExpressionType(parameter);
        if (!HasConstArgumentProvenance(argument))
        {
            ReportError(
                "STK3031",
                $"Argument {position} for '{functionName}' must have const provenance because parameter '{parameter.Name}' is declared const.",
                context);
            return;
        }

        var argumentType = GetConstProvenanceViewType(argument.Type);
        if (CanAssign(parameterType, argumentType))
        {
            if (parameter.Type.BorrowKind != StarkBorrowKind.None
                && TryRejectMisalignedSafeBorrow($"Argument {position} for '{functionName}'", argument, context))
            {
                return;
            }

            return;
        }

        ReportError(
            "STK3002",
            $"Argument {position} for '{functionName}' expects const-compatible '{parameterType.DisplayName}' but found '{argument.Type.DisplayName}'.{GetExplicitConversionHint(parameterType, argumentType)}",
            context);
    }

    private void EnsureReceiverArgumentCompatible(
        string functionName,
        TypedParameterSymbol parameter,
        ExpressionBinding receiver,
        ParserRuleContext context)
    {
        var parameterType = parameter.Type;
        if (parameter.IsConst)
        {
            EnsureConstCallArgumentCompatible(functionName, 1, parameter, receiver, context);
            return;
        }

        if (FunctionOverloadFacts.CanBindReceiver(parameterType, receiver.Type, CanAssign))
        {
            if (parameterType.BorrowKind != StarkBorrowKind.None
                && TryRejectMisalignedSafeBorrow($"Receiver for '{functionName}'", receiver, context))
            {
                return;
            }

            return;
        }

        ReportError(
            "STK3002",
            $"Argument 1 for '{functionName}' expects '{parameterType.DisplayName}' but found '{receiver.Type.DisplayName}'.{GetExplicitConversionHint(parameterType, receiver.Type)}",
            context);
    }

    private bool TryRejectMisalignedSafeBorrow(string subject, ExpressionBinding value, ParserRuleContext context)
    {
        if (!value.IsMisalignedFieldProjection)
        {
            return false;
        }

        var target = value.DiagnosticName ?? "packed field projection";
        ReportError(
            "STK3049",
            $"{subject} cannot form a safe borrow to {target} because its packed layout may make the address misaligned. Copy the value to aligned storage or use an explicit unsafe raw pointer path.",
            context);
        return true;
    }

    private void EnsureObjectInitializerCompatible(
        string memberName,
        StarkTypeSymbol memberType,
        StarkTypeSymbol valueType,
        ParserRuleContext context)
    {
        if (CanAssign(memberType, valueType))
        {
            return;
        }

        ReportError(
            "STK3002",
            $"Object initializer member '{memberName}' expects '{memberType.DisplayName}' but found '{valueType.DisplayName}'.{GetExplicitConversionHint(memberType, valueType)}",
            context);
    }

    private void EnsureArrayElementCompatible(StarkTypeSymbol elementType, StarkTypeSymbol valueType, ParserRuleContext context)
    {
        if (CanAssign(elementType, valueType))
        {
            return;
        }

        ReportError(
            "STK3002",
            $"Array initializer element expects '{elementType.DisplayName}' but found '{valueType.DisplayName}'.{GetExplicitConversionHint(elementType, valueType)}",
            context);
    }

    private void EnsureExplicitConversionCompatible(StarkTypeSymbol targetType, ExpressionBinding source, ParserRuleContext context)
    {
        if (CanExplicitlyConvert(targetType, source))
        {
            return;
        }

        if (targetType.Kind == StarkTypeKind.RawPointer
            && targetType.IsMutablePointer
            && source.Type.Kind == StarkTypeKind.RawPointer
            && !source.Type.IsMutablePointer)
        {
            ReportError(
                "STK3002",
                $"Explicit conversion from '{source.Type.DisplayName}' to '{targetType.DisplayName}' is not supported because explicit conversions may not strengthen pointer mutability.",
                context);
            return;
        }

        if (targetType.Kind == StarkTypeKind.Integer
            && source.Type.Kind == StarkTypeKind.RawPointer
            && !source.Type.IsMutablePointer)
        {
            ReportError(
                "STK3002",
                $"Explicit conversion from '{source.Type.DisplayName}' to '{targetType.DisplayName}' is not supported because it would erase readonly pointer provenance.",
                context);
            return;
        }

        if (TryDescribeTextExplicitConversionFailure(targetType, source, out var textConversionMessage))
        {
            ReportError("STK3002", textConversionMessage, context);
            return;
        }

        ReportError(
            "STK3002",
            $"Explicit conversion from '{source.Type.DisplayName}' to '{targetType.DisplayName}' is not supported.",
            context);
    }

    private void RequireUnsafeForRawPointerType(StarkTypeSymbol type, string subject, ParserRuleContext context)
    {
        if (!ContainsRawPointer(type))
        {
            return;
        }

        RequireUnsafeContext($"{subject} using '{type.DisplayName}'", context);
    }

    private void RequireUnsafeContext(string subject, ParserRuleContext context)
    {
        if (_unsafeDepth != 0)
        {
            return;
        }

        ReportError(
            "STK3024",
            $"{subject} requires an unsafe context. Wrap the operation in `unsafe {{ ... }}` or move it into an `unsafe fn`; prefer borrow, slice, dynamic storage, owned handles, or a platform wrapper when a raw pointer is not required.",
            context);
    }

    private static bool IsRawPointerConversion(StarkTypeSymbol targetType, StarkTypeSymbol sourceType)
    {
        return targetType.Kind == StarkTypeKind.RawPointer
            || sourceType.Kind == StarkTypeKind.RawPointer;
    }

    private static bool ContainsRawPointer(StarkTypeSymbol type)
    {
        return type.Kind == StarkTypeKind.RawPointer
            || type.ElementType is not null && ContainsRawPointer(type.ElementType)
            || type.FunctionPointerParameterTypes is { Count: > 0 } && type.FunctionPointerParameterTypes.Any(ContainsRawPointer)
            || type.FunctionPointerReturnType is not null && ContainsRawPointer(type.FunctionPointerReturnType)
            || type.ClosureParameterTypes is { Count: > 0 } && type.ClosureParameterTypes.Any(ContainsRawPointer)
            || type.ClosureReturnType is not null && ContainsRawPointer(type.ClosureReturnType)
            || type.TypeArguments is { Count: > 0 } && type.TypeArguments.Any(ContainsRawPointer);
    }

    private static string GetExplicitConversionHint(StarkTypeSymbol target, StarkTypeSymbol source)
    {
        if (target.Kind == StarkTypeKind.Error || source.Kind == StarkTypeKind.Error)
        {
            return string.Empty;
        }

        if (target.Kind == StarkTypeKind.Integer && source.Kind == StarkTypeKind.Float)
        {
            return " An explicit conversion is required to convert a floating-point value to an integer.";
        }

        if (target.Kind == StarkTypeKind.Integer
            && source.Kind == StarkTypeKind.Integer
            && (source.BitWidth > target.BitWidth || !HasContainedEffectiveIntegerRange(source, target)))
        {
            return " An explicit narrowing conversion is required.";
        }

        if (target.Kind == StarkTypeKind.Float && source.Kind == StarkTypeKind.Float && source.BitWidth > target.BitWidth)
        {
            return " An explicit narrowing conversion is required.";
        }

        if (IsTextType(target) && IsTextType(source))
        {
            return " An explicit text conversion is currently only available for compile-time text constants.";
        }

        return string.Empty;
    }

    private static string Pluralize(int count) => count == 1 ? string.Empty : "s";

    private static string DescribeExpressionTarget(ExpressionBinding binding)
    {
        if (binding.NamespaceName is not null)
        {
            return $"module '{binding.NamespaceName}'";
        }

        return binding.DiagnosticName is not null
            ? $"{binding.DiagnosticName} of type '{binding.Type.DisplayName}'"
            : $"expression of type '{binding.Type.DisplayName}'";
    }

    private static bool CanFormMutableAddressFromLocal(VariableSymbol local)
    {
        return !local.IsConstant
            && !local.UsesFrozenProjectionSemantics
            && !local.HasConstProvenance
            && local.Type.AccessKind != StarkAccessKind.Frozen
            && (local.IsMutable || local.Type.IsMutableView || local.Type.InitializationKind != StarkInitializationKind.None);
    }

    private static bool CanAssignToLocal(VariableSymbol local)
    {
        return !local.IsConstant
            && !local.HasConstProvenance
            && (local.IsMutable || local.Type.InitializationKind != StarkInitializationKind.None);
    }

    private static bool IsLocalBindingIndependentStorage(VariableSymbol local)
    {
        return local.Type.BorrowKind == StarkBorrowKind.None
            && local.Type.InitializationKind == StarkInitializationKind.None
            && local.Type.Kind is StarkTypeKind.Bool
                or StarkTypeKind.Integer
                or StarkTypeKind.Float
                or StarkTypeKind.FixedArray
                or StarkTypeKind.Dynamic
                or StarkTypeKind.FunctionPointer
                or StarkTypeKind.Named;
    }

    private static bool CanMutateAddressProjection(ExpressionBinding target, StarkTypeSymbol projectedType)
    {
        return target.IsAddressMutable
            && target.Type.AccessKind != StarkAccessKind.Frozen
            && projectedType.AccessKind != StarkAccessKind.Frozen;
    }

    private static string DescribeGlobalRebindingError(string name, GlobalBindingKind bindingKind)
    {
        return bindingKind switch
        {
            GlobalBindingKind.Const => $"Cannot rebind constant global '{name}'.",
            GlobalBindingKind.Immutable => $"Cannot rebind immutable global '{name}'.",
            GlobalBindingKind.Mutable => $"Global '{name}' is assignable.",
            _ => $"Cannot assign to global '{name}'."
        };
    }

    private static string DescribeGlobalMutationError(string name, GlobalBindingKind bindingKind, string targetDescription)
    {
        return bindingKind switch
        {
            GlobalBindingKind.Const => $"Cannot mutate {targetDescription} of constant global '{name}'.",
            GlobalBindingKind.Immutable => $"Cannot mutate {targetDescription} through immutable global '{name}'.",
            GlobalBindingKind.Mutable => $"Global '{name}' is assignable.",
            _ => $"Cannot mutate {targetDescription} of global '{name}'."
        };
    }

    private static string DescribeFrozenMutationError(string targetDescription)
    {
        return $"Cannot mutate {targetDescription} through a frozen value.";
    }

    private static bool UsesFrozenProjectionSemantics(ExpressionBinding binding)
    {
        return binding.UsesFrozenProjectionSemantics
            || binding.HasConstProvenance
            || binding.Type.AccessKind == StarkAccessKind.Frozen
            || binding.RootGlobalBindingKind == GlobalBindingKind.Const;
    }

    private static bool HasConstArgumentProvenance(ExpressionBinding binding)
    {
        return HasConstProvenance(binding);
    }

    private static bool HasConstProvenance(ExpressionBinding binding)
    {
        return binding.HasConstProvenance
            || binding.RootGlobalBindingKind == GlobalBindingKind.Const;
    }

    private static ConstProvenanceKind GetLocalDeclarationConstProvenance(
        bool isMutable,
        ExpressionBinding? initializer)
    {
        if (isMutable)
        {
            return ConstProvenanceKind.None;
        }

        if (initializer is null)
        {
            return ConstProvenanceKind.ImmutableBinding;
        }

        if (HasConstProvenance(initializer))
        {
            return ConstProvenanceKind.PermanentConst;
        }

        if (initializer.UsesFrozenProjectionSemantics
            || initializer.Type.AccessKind == StarkAccessKind.Frozen)
        {
            return ConstProvenanceKind.FrozenBorrow;
        }

        if (initializer.Type.Kind == StarkTypeKind.RawPointer
            && !initializer.Type.IsMutablePointer)
        {
            return ConstProvenanceKind.ReadonlyRawPointer;
        }

        if ((initializer.Type.Kind == StarkTypeKind.Slice
                || initializer.Type.Kind == StarkTypeKind.Ascii
                || initializer.Type.Kind == StarkTypeKind.Unicode)
            && !initializer.Type.IsMutableView)
        {
            return ConstProvenanceKind.TemporaryReadonlyView;
        }

        return ConstProvenanceKind.ImmutableBinding;
    }

    private static StarkTypeSymbol GetConstProvenanceViewType(StarkTypeSymbol type)
    {
        return StarkTypeSymbols.FreezeReachableView(type);
    }

    private static StarkTypeSymbol ProjectProjectionType(ExpressionBinding source, StarkTypeSymbol projectedType)
    {
        var projected = UsesFrozenProjectionSemantics(source)
            ? StarkTypeSymbols.FreezeReachableView(projectedType)
            : ProjectFrozenView(source.Type, projectedType);
        return ApplyProjectedInitializationKind(projected, source.Type.InitializationKind);
    }

    private static StarkTypeSymbol ApplyProjectedInitializationKind(
        StarkTypeSymbol projectedType,
        StarkInitializationKind initializationKind)
    {
        return initializationKind == StarkInitializationKind.None
            ? projectedType
            : StarkTypeSymbols.WithQualifiers(
                projectedType,
                initializationKind: initializationKind,
                isMutableView: projectedType.Kind == StarkTypeKind.Slice || projectedType.IsMutableView);
    }

    private static StarkTypeSymbol ProjectFrozenView(StarkTypeSymbol sourceType, StarkTypeSymbol projectedType)
    {
        return sourceType.AccessKind == StarkAccessKind.Frozen
            ? StarkTypeSymbols.FreezeReachableView(projectedType)
            : projectedType;
    }

    // Whether a value coerces into a `dyn Trait` slot: another trait object over
    // the same trait (reborrow), or a concrete type that implements the `dyn trait`.
    private bool CanAssignToDynTrait(StarkTypeSymbol dynTarget, StarkTypeSymbol source)
    {
        if (dynTarget.DynTraitName is not { } traitName)
        {
            return false;
        }

        if (source.Kind == StarkTypeKind.DynTrait)
        {
            return string.Equals(
                StarkTypeSymbols.GetGenericBaseName(source.DynTraitName ?? string.Empty),
                StarkTypeSymbols.GetGenericBaseName(traitName),
                StringComparison.Ordinal);
        }

        var concreteTypeName = source.Kind switch
        {
            StarkTypeKind.Named => source.NamedType,
            StarkTypeKind.RawPointer when source.ElementType is { NamedType: { } named } => named,
            _ => null
        };
        if (concreteTypeName is null
            || !_namedTypes.TryGetValue(concreteTypeName, out var concreteType)
            || !_namedTypes.TryGetValue(traitName, out var traitType)
            || !traitType.IsDynTrait)
        {
            return false;
        }

        var traitBaseName = StarkTypeSymbols.GetGenericBaseName(traitName);
        foreach (var implemented in concreteType.ImplementedTraits)
        {
            if (string.Equals(StarkTypeSymbols.GetGenericBaseName(implemented), traitBaseName, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private bool CanAssign(StarkTypeSymbol target, StarkTypeSymbol source)
    {
        if (target.Kind == StarkTypeKind.Error || source.Kind == StarkTypeKind.Error)
        {
            return true;
        }

        // A conforming concrete value (or another trait object over the same trait)
        // coerces into a `dyn Trait` slot. The storage prefix on the slot discloses
        // the cost, so this is the only implicit path into a trait object.
        if (target.Kind == StarkTypeKind.DynTrait)
        {
            return CanAssignToDynTrait(target, source);
        }

        if (!AreQualifiersAssignable(target, source))
        {
            return false;
        }

        if (Equals(target, source))
        {
            return true;
        }

        if (target.Kind == StarkTypeKind.Integer && source.Kind == StarkTypeKind.Integer)
        {
            if (target.BitWidth is null || source.BitWidth is null)
            {
                return false;
            }

            return HasContainedEffectiveIntegerRange(source, target);
        }

        if (target.Kind == StarkTypeKind.Float && source.Kind == StarkTypeKind.Float)
        {
            return source.BitWidth <= target.BitWidth;
        }

        if (target.Kind == StarkTypeKind.Float && source.Kind == StarkTypeKind.Integer)
        {
            return true;
        }

        if (target.Kind == StarkTypeKind.RawPointer && source.Kind == StarkTypeKind.RawPointer)
        {
            if (target.ElementType is null || source.ElementType is null)
            {
                return target.IsMutablePointer == source.IsMutablePointer;
            }

            if (target.IsMutablePointer && !source.IsMutablePointer)
            {
                return false;
            }

            return CanAssign(target.ElementType, source.ElementType);
        }

        if (target.Kind == StarkTypeKind.RawPointer && source.Kind == StarkTypeKind.Null)
        {
            return true;
        }

        if (target.Kind == StarkTypeKind.Slice && source.Kind == StarkTypeKind.FixedArray && target.ElementType is not null && source.ElementType is not null)
        {
            return CanAssign(target.ElementType, source.ElementType);
        }

        if (target.Kind == StarkTypeKind.FixedArray && source.Kind == StarkTypeKind.FixedArray)
        {
            return target.FixedLength == source.FixedLength
                && target.ElementType is not null
                && source.ElementType is not null
                && CanAssign(target.ElementType, source.ElementType);
        }

        if (target.Kind == StarkTypeKind.Slice && source.Kind == StarkTypeKind.Slice)
        {
            return target.ElementType is not null
                && source.ElementType is not null
                && CanAssign(target.ElementType, source.ElementType);
        }

        if (target.Kind == StarkTypeKind.Dynamic && source.Kind == StarkTypeKind.Dynamic)
        {
            return target.ElementType is not null
                && source.ElementType is not null
                && CanAssign(target.ElementType, source.ElementType)
                && CanAssign(source.ElementType, target.ElementType);
        }

        if (target.Kind == StarkTypeKind.FunctionPointer && source.Kind == StarkTypeKind.FunctionPointer)
        {
            return TypeCompatibilityFacts.AreFunctionPointerTypesAssignable(target, source);
        }

        if (target.Kind == StarkTypeKind.Closure && source.Kind == StarkTypeKind.Closure)
        {
            return TypeCompatibilityFacts.AreClosureTypesAssignable(target, source);
        }

        return target.Kind == StarkTypeKind.Named
            && source.Kind == StarkTypeKind.Named
            && string.Equals(target.NamedType, source.NamedType, StringComparison.Ordinal);
    }

    private bool CanExplicitlyConvert(StarkTypeSymbol target, ExpressionBinding source)
    {
        if (CanAssign(target, source.Type))
        {
            return true;
        }

        if (!AreQualifiersAssignable(target, source.Type)
            && !(target.Kind == StarkTypeKind.RawPointer && source.Type.Kind == StarkTypeKind.RawPointer)
            && !(target.Kind == StarkTypeKind.RawPointer && source.Type.Kind == StarkTypeKind.Null))
        {
            return false;
        }

        if (target.Kind == StarkTypeKind.Integer && source.Type.Kind == StarkTypeKind.Integer)
        {
            return true;
        }

        if (target.Kind == StarkTypeKind.Float && source.Type.Kind == StarkTypeKind.Float)
        {
            return true;
        }

        if ((target.Kind == StarkTypeKind.Integer && source.Type.Kind == StarkTypeKind.Float)
            || (target.Kind == StarkTypeKind.Float && source.Type.Kind == StarkTypeKind.Integer))
        {
            return true;
        }

        if (IsTextType(target) && IsTextType(source.Type))
        {
            return CanExplicitlyConvertTextLiteral(target, source);
        }

        if ((target.Kind == StarkTypeKind.RawPointer && source.Type.Kind == StarkTypeKind.Integer)
            || (target.Kind == StarkTypeKind.RawPointer && source.Type.Kind == StarkTypeKind.Null))
        {
            return true;
        }

        if (target.Kind == StarkTypeKind.Integer && source.Type.Kind == StarkTypeKind.RawPointer)
        {
            return source.Type.IsMutablePointer;
        }

        if (target.Kind == StarkTypeKind.RawPointer && source.Type.Kind == StarkTypeKind.RawPointer)
        {
            if (WouldEraseFrozenProvenance(target, source.Type))
            {
                return false;
            }

            if (target.IsMutablePointer && !source.Type.IsMutablePointer)
            {
                return false;
            }

            return true;
        }

        if (target.Kind == StarkTypeKind.Slice
            && source.Type.Kind == StarkTypeKind.FixedArray
            && source.IsAddressable
            && target.ElementType is not null
            && source.Type.ElementType is not null)
        {
            return CanAssign(target.ElementType, source.Type.ElementType)
                || CanExplicitlyConvert(target.ElementType, new ExpressionBinding(source.Type.ElementType));
        }

        return false;
    }

    private static bool CanExplicitlyConvertTextLiteral(StarkTypeSymbol target, ExpressionBinding source)
    {
        if (!IsTextType(target)
            || !IsTextType(source.Type)
            || source.TextLiteral is null
            || source.TextLiteralKind is null)
        {
            return false;
        }

        if (target.Kind == StarkTypeKind.Unicode && source.Type.Kind == StarkTypeKind.Ascii)
        {
            return true;
        }

        if (target.Kind == source.Type.Kind)
        {
            return true;
        }

        return target.Kind == StarkTypeKind.Ascii
            && source.Type.Kind == StarkTypeKind.Unicode
            && TextLiteralDecoder.CanUseUtf8Storage(source.TextLiteral, source.TextLiteralKind.Value);
    }

    private static bool TryDescribeTextExplicitConversionFailure(
        StarkTypeSymbol targetType,
        ExpressionBinding source,
        out string message)
    {
        message = string.Empty;
        if (!IsTextType(targetType) || !IsTextType(source.Type))
        {
            return false;
        }

        if (source.TextLiteral is null || source.TextLiteralKind is null)
        {
            message =
                $"Explicit conversion from '{source.Type.DisplayName}' to '{targetType.DisplayName}' requires a compile-time text constant. For runtime text transcoding, write into caller-owned storage with System.Text APIs.";
            return true;
        }

        return false;
    }

    private bool CanAssignPatternLiteral(StarkTypeSymbol target, ExpressionBinding source)
    {
        if (CanAssign(target, source.Type))
        {
            return true;
        }

        return target.Kind == StarkTypeKind.Unicode
            && source.Type.Kind == StarkTypeKind.Ascii
            && source.TextLiteral is not null
            && source.TextLiteralKind is not null
            && TextLiteralDecoder.CanUseUtf8Storage(source.TextLiteral, source.TextLiteralKind.Value);
    }

    private static bool IsTextType(StarkTypeSymbol type)
    {
        return type.Kind is StarkTypeKind.Ascii or StarkTypeKind.Unicode;
    }

    private static bool IsTextBufferType(StarkTypeSymbol type)
    {
        return type.Kind == StarkTypeKind.Named
            && type.NamedType is StarkTypeSymbols.OwnedAsciiName or StarkTypeSymbols.OwnedUnicodeName;
    }

    private static bool IsTextLikeForConcatenation(StarkTypeSymbol type)
    {
        return IsTextType(type) || IsTextBufferType(type);
    }

    private static bool IsAsciiConcatSource(StarkTypeSymbol type)
    {
        return type.Kind == StarkTypeKind.Ascii
            || type.Kind == StarkTypeKind.Named
                && string.Equals(type.NamedType, StarkTypeSymbols.OwnedAsciiName, StringComparison.Ordinal);
    }

    private static bool IsUnicodeConcatSource(StarkTypeSymbol type)
    {
        return type.Kind == StarkTypeKind.Unicode
            || type.Kind == StarkTypeKind.Named
                && string.Equals(type.NamedType, StarkTypeSymbols.OwnedUnicodeName, StringComparison.Ordinal);
    }

    private static bool CanUseFixedTextConcatSource(StarkTypeSymbol destination, StarkTypeSymbol source)
    {
        return destination.NamedType switch
        {
            StarkTypeSymbols.OwnedAsciiName => IsAsciiConcatSource(source),
            StarkTypeSymbols.OwnedUnicodeName => IsUnicodeConcatSource(source),
            _ => false
        };
    }

    private bool CanUseFixedTextInterpolationHole(
        StarkTypeSymbol destination,
        StarkTypeSymbol source,
        out string diagnostic)
    {
        diagnostic = string.Empty;
        if (CanUseFixedTextConcatSource(destination, source))
        {
            return true;
        }

        if (!TextFormattingFacts.TryGetFixedBufferFormatInfo(destination, source, out var formatInfo))
        {
            diagnostic = $"Interpolated text does not know how to format '{source.DisplayName}' yet. Convert the value to text first, or use bool, integer, or floating-point values.";
            return false;
        }

        var sourceName = GetSystemTextFunctionName(formatInfo.FunctionName);
        if (!TryGetFunctionOverloads(sourceName, out var overloads))
        {
            diagnostic = $"Interpolated text needs '{sourceName}' to format '{source.DisplayName}', but that function is not available.";
            return false;
        }

        var resolution = FunctionOverloadFacts.Resolve(
            overloads,
            receiverType: null,
            [StarkTypeSymbols.RawPointer(destination, isMutable: true), source],
            CanAssign,
            ResolveAssociatedTypeForSubstitution);
        if (resolution.Succeeded)
        {
            return true;
        }

        diagnostic = $"Interpolated text found '{source.DisplayName}', but it cannot be passed to '{sourceName}'. Use an explicit conversion before putting it inside '{{...}}'.";
        return false;
    }

    private static StarkTypeSymbol GetFixedTextConcatViewType(StarkTypeSymbol destination)
    {
        return destination.NamedType == StarkTypeSymbols.OwnedUnicodeName
            ? StarkTypeSymbols.Unicode
            : StarkTypeSymbols.Ascii;
    }

    private static StarkTypeSymbol FindCommonTextType(StarkTypeSymbol left, StarkTypeSymbol right)
    {
        return left.Kind == StarkTypeKind.Unicode || right.Kind == StarkTypeKind.Unicode
            ? StarkTypeSymbols.Unicode
            : StarkTypeSymbols.Ascii;
    }

    private static bool AreQualifiersAssignable(StarkTypeSymbol target, StarkTypeSymbol source)
    {
        if (!IsBorrowAssignable(target.BorrowKind, source.BorrowKind))
        {
            return false;
        }

        if (!IsAccessAssignable(target.AccessKind, source.AccessKind))
        {
            return false;
        }

        if (target.InitializationKind != source.InitializationKind)
        {
            return false;
        }

        if (target.IsMutableView && !source.IsMutableView)
        {
            return false;
        }

        return true;
    }

    private static bool IsBorrowAssignable(StarkBorrowKind target, StarkBorrowKind source)
    {
        if (target == StarkBorrowKind.None || source == StarkBorrowKind.None)
        {
            return target == source;
        }

        return BorrowRank(source) >= BorrowRank(target);
    }

    private static bool IsAccessAssignable(StarkAccessKind target, StarkAccessKind source)
    {
        if (target == source)
        {
            return true;
        }

        if (source == StarkAccessKind.Frozen || target == StarkAccessKind.Frozen)
        {
            return false;
        }

        return AccessRank(source) >= AccessRank(target);
    }

    private static bool WouldEraseFrozenProvenance(StarkTypeSymbol target, StarkTypeSymbol source)
    {
        if (source.AccessKind == StarkAccessKind.Frozen && target.AccessKind != StarkAccessKind.Frozen)
        {
            return true;
        }

        if (target.Kind == StarkTypeKind.RawPointer
            && source.Kind == StarkTypeKind.RawPointer
            && target.ElementType is not null
            && source.ElementType is not null)
        {
            return WouldEraseFrozenProvenance(target.ElementType, source.ElementType);
        }

        if ((target.Kind == StarkTypeKind.FixedArray || target.Kind == StarkTypeKind.Slice)
            && source.Kind == target.Kind
            && target.ElementType is not null
            && source.ElementType is not null)
        {
            return WouldEraseFrozenProvenance(target.ElementType, source.ElementType);
        }

        return false;
    }

    private static int BorrowRank(StarkBorrowKind kind)
    {
        return kind switch
        {
            StarkBorrowKind.Borrow => 1,
            StarkBorrowKind.RetBorrow => 2,
            StarkBorrowKind.StoreBorrow => 3,
            _ => 0
        };
    }

    private static int AccessRank(StarkAccessKind kind)
    {
        return kind switch
        {
            StarkAccessKind.Shared => 1,
            StarkAccessKind.Frozen => 2,
            _ => 0
        };
    }

    private static bool IsRangeContained(BigInteger? sourceMin, BigInteger? sourceMax, BigInteger? targetMin, BigInteger? targetMax)
    {
        if (targetMin is null || targetMax is null)
        {
            return true;
        }

        if (sourceMin is null || sourceMax is null)
        {
            return false;
        }

        return sourceMin >= targetMin && sourceMax <= targetMax;
    }

    private static bool HasContainedEffectiveIntegerRange(StarkTypeSymbol source, StarkTypeSymbol target)
    {
        if (!TryGetEffectiveIntegerRange(source, out var sourceMin, out var sourceMax)
            || !TryGetEffectiveIntegerRange(target, out var targetMin, out var targetMax))
        {
            return false;
        }

        return IsRangeContained(sourceMin, sourceMax, targetMin, targetMax);
    }

    private static bool TryGetEffectiveIntegerRange(StarkTypeSymbol type, out BigInteger min, out BigInteger max)
    {
        return StarkTypeSymbols.TryGetEffectiveIntegerBounds(type, out min, out max);
    }

    private static bool IsProvablyNonNegativeIntegerType(StarkTypeSymbol type)
    {
        return TryGetEffectiveIntegerRange(type, out var min, out _)
            && min >= BigInteger.Zero;
    }

    private static StarkTypeSymbol FindCommonType(StarkTypeSymbol left, StarkTypeSymbol right)
    {
        if (left.Kind == StarkTypeKind.Error || right.Kind == StarkTypeKind.Error)
        {
            return StarkTypeSymbols.Error;
        }

        if (left.DisplayName == right.DisplayName)
        {
            return left;
        }

        if (left.Kind == StarkTypeKind.Integer && right.Kind == StarkTypeKind.Integer)
        {
            if (StarkTypeSymbols.IsCompileTimeInteger(left)
                || StarkTypeSymbols.IsCompileTimeInteger(right))
            {
                return StarkTypeSymbols.CompileTimeInteger;
            }

            return StarkTypeSymbols.Integer(
                Math.Max(left.BitWidth ?? 0, right.BitWidth ?? 0),
                isUnsigned: left.IsUnsigned && right.IsUnsigned);
        }

        if (left.Kind == StarkTypeKind.Float && right.Kind == StarkTypeKind.Float)
        {
            return StarkTypeSymbols.Float(Math.Max(left.BitWidth ?? 32, right.BitWidth ?? 32));
        }

        if (left.Kind == StarkTypeKind.Float && right.Kind == StarkTypeKind.Integer)
        {
            return left;
        }

        if (left.Kind == StarkTypeKind.Integer && right.Kind == StarkTypeKind.Float)
        {
            return right;
        }

        return StarkTypeSymbols.Error;
    }

    private static bool AreComparable(StarkTypeSymbol left, StarkTypeSymbol right)
    {
        if (left.Kind == StarkTypeKind.Error || right.Kind == StarkTypeKind.Error)
        {
            return true;
        }

        if (left.Kind == StarkTypeKind.Void || right.Kind == StarkTypeKind.Void)
        {
            return false;
        }

        if (left.Kind == StarkTypeKind.Null && right.Kind == StarkTypeKind.RawPointer)
        {
            return true;
        }

        if (right.Kind == StarkTypeKind.Null && left.Kind == StarkTypeKind.RawPointer)
        {
            return true;
        }

        return FindCommonType(left, right).Kind != StarkTypeKind.Error
            || (left.Kind == StarkTypeKind.Named && right.Kind == StarkTypeKind.Named && left.NamedType == right.NamedType)
            || (left.Kind == StarkTypeKind.RawPointer && right.Kind == StarkTypeKind.RawPointer);
    }

    private static bool CanLowerImplementedSwitchType(StarkTypeSymbol type)
    {
        return type.Kind is StarkTypeKind.Error
            or StarkTypeKind.Integer
            or StarkTypeKind.Float
            or StarkTypeKind.Bool
            or StarkTypeKind.RawPointer
            or StarkTypeKind.Ascii
            or StarkTypeKind.Unicode
            or StarkTypeKind.Named
            or StarkTypeKind.FixedArray
            or StarkTypeKind.Slice
            or StarkTypeKind.Dynamic;
    }

    private static bool IsNumeric(StarkTypeSymbol type)
    {
        return type.Kind is StarkTypeKind.Integer or StarkTypeKind.Float;
    }

    private bool IsOrderedComparable(StarkTypeSymbol left, StarkTypeSymbol right)
    {
        var commonType = FindCommonType(left, right);
        return IsOrderedComparable(commonType);
    }

    private bool IsOrderedComparable(StarkTypeSymbol type)
    {
        if (type.Kind is StarkTypeKind.Integer
            or StarkTypeKind.Float
            or StarkTypeKind.Bool
            or StarkTypeKind.RawPointer
            or StarkTypeKind.Ascii
            or StarkTypeKind.Unicode)
        {
            return true;
        }

        if (type.Kind == StarkTypeKind.FixedArray
            && type.ElementType is not null
            && type.FixedLength is int)
        {
            return IsOrderedComparable(type.ElementType);
        }

        if (type.Kind != StarkTypeKind.Named
            || ResolveNamedTypeSymbol(type) is not { } namedType)
        {
            return false;
        }

        if (namedType.Kind is DeclarationKind.Struct or DeclarationKind.Record)
        {
            return AreOrderedComparable(namedType.OrderedFields);
        }

        if (namedType.Kind == DeclarationKind.Enum)
        {
            return AreOrderedComparableEnumVariants(namedType.Variants);
        }

        return false;
    }

    private bool AreOrderedComparable(IEnumerable<FieldSymbol> fields)
    {
        foreach (var field in fields)
        {
            if (!IsOrderedComparable(field.Type))
            {
                return false;
            }
        }

        return true;
    }

    private bool AreOrderedComparableEnumVariants(IEnumerable<EnumVariantSymbol> variants)
    {
        foreach (var variant in variants)
        {
            foreach (var field in variant.Fields)
            {
                if (!IsOrderedComparable(field.Type))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool IsBitwiseAssignmentOperator(string assignmentOperator)
    {
        return assignmentOperator is "&=" or "|=" or "^=";
    }

    private static bool IsExplicitArithmeticOperator(string op)
    {
        return op is "+%" or "-%" or "*%" or "+|" or "-|" or "*|";
    }

    private static bool IsExplicitArithmeticAssignmentOperator(string assignmentOperator)
    {
        return assignmentOperator is "+%=" or "-%=" or "*%=" or "+|=" or "-|=" or "*|=";
    }

    private static BigInteger ParseSignedIntegerLiteral(StarkParser.SignedIntegerLiteralContext literal)
    {
        var value = BigInteger.Parse(literal.IntegerLiteral().GetText());
        return literal.MINUS() is null ? value : -value;
    }

    private static bool TryGetRangePatternBounds(
        StarkParser.RangePatternContext rangePattern,
        out BigInteger min,
        out BigInteger max)
    {
        var endpoints = rangePattern.signedIntegerLiteral();
        if (endpoints.Length != 2)
        {
            min = BigInteger.Zero;
            max = BigInteger.Zero;
            return false;
        }

        min = ParseSignedIntegerLiteral(endpoints[0]);
        max = ParseSignedIntegerLiteral(endpoints[1]);
        return true;
    }

    private static StarkTypeSymbol InferIntegerLiteralType(BigInteger value)
    {
        foreach (var width in SupportedIntegerLiteralWidths)
        {
            var min = -(BigInteger.One << (width - 1));
            var max = (BigInteger.One << (width - 1)) - BigInteger.One;
            if (value >= min && value <= max)
            {
                return StarkTypeSymbols.Integer(width, value, value);
            }
        }

        return StarkTypeSymbols.CompileTimeInteger;
    }

    private static StarkTypeSymbol InferStringLiteralType(string text)
    {
        return TextLiteralDecoder.CanUseUtf8Storage(text, TextLiteralKind.String)
            ? StarkTypeSymbols.Ascii
            : StarkTypeSymbols.Unicode;
    }

    private static StarkTypeSymbol InferCharacterLiteralType(string text)
    {
        return TextLiteralDecoder.CanUseUtf8Storage(text, TextLiteralKind.Character)
            ? StarkTypeSymbols.Ascii
            : StarkTypeSymbols.Unicode;
    }

    private NamedTypeSymbol? ResolveNamedTypeSymbol(StarkTypeSymbol type)
    {
        return type.NamedType is not null && _namedTypes.TryGetValue(type.NamedType, out var namedType)
            ? namedType
            : null;
    }

    private NamedTypeSymbol? ResolveNamedTypeDefinitionSymbol(StarkTypeSymbol type)
    {
        var coreType = StarkTypeSymbols.WithQualifiers(
            type,
            borrowKind: StarkBorrowKind.None,
            accessKind: StarkAccessKind.None,
            initializationKind: StarkInitializationKind.None,
            isMutableView: false);
        if (coreType.Kind != StarkTypeKind.Named || coreType.NamedType is not { } typeName)
        {
            return null;
        }

        if (StarkTypeSymbols.IsGenericInstantiation(coreType))
        {
            var baseName = StarkTypeSymbols.GetGenericBaseName(typeName);
            if (_namedTypes.TryGetValue(baseName, out var templateType))
            {
                return templateType;
            }
        }

        return ResolveNamedTypeSymbol(coreType);
    }

    private bool IsTraitMethodFunctionName(string functionName)
    {
        var separator = functionName.LastIndexOf('.');
        if (separator <= 0)
        {
            return false;
        }

        var containingTypeName = functionName[..separator];
        return TryResolveNamedTypeBySourceName(containingTypeName, out var namedType)
            && namedType.Kind == DeclarationKind.Trait;
    }

    private StarkTypeSymbol ValidateRuntimeValueType(
        StarkTypeSymbol type,
        ParserRuleContext context,
        string usage,
        bool allowDirectInlineClosureParameter = false)
    {
        if (TryFindCompileTimeOnlyTypeDependency(type, out var dependencyName, out var dependencyKind))
        {
            ReportError(
                "STK3013",
                $"Type '{type.DisplayName}' depends on compile-time-only {DescribeCompileTimeOnlyKind(dependencyKind)} '{dependencyName}', which is not allowed for {usage}. {DescribeNoDynamicDispatchPolicy()}",
                context);
        }

        if (StarkTypeSymbols.IsCompileTimeInteger(type))
        {
            ReportError(
                "STK3013",
                $"Type 'integer' is compile-time-only and cannot be used as runtime storage for {usage}. Convert the value to a concrete integer type whose range can hold it.",
                context);
        }

        if (TryFindInvalidCVoidUse(type, out var invalidCVoidType))
        {
            ReportError(
                "STK3050",
                $"Type '{invalidCVoidType.DisplayName}' is an incomplete C pointee type and is valid only as the direct pointee of rawptr<System.C.c_void> or rawmutptr<System.C.c_void>. Use Stark 'void' for functions that return no value.",
                context);
        }

        if (TryFindInvalidDynTraitVtableUse(type, out var invalidVtableType))
        {
            ReportError(
                "STK3035",
                $"Type '{invalidVtableType.DisplayName}' is a compiler-owned dyn-trait vtable and is valid only as the direct pointee of readonly rawptr<{invalidVtableType.DisplayName}>. User code may carry vtable pointers but cannot store or construct vtables by value.",
                context);
        }

        if (ContainsInlineClosureType(type)
            && !(allowDirectInlineClosureParameter && IsDirectInlineClosureType(type)))
        {
            ReportError(
                "STK3008",
                $"Inline closure type '{type.DisplayName}' is only valid as a function parameter directly because it is a specialization contract, not runtime storage. Use `borrow closure<...>` or `heap closure<...>` when {usage} needs a value.",
                context);
        }

        return type;
    }

    private static bool TryFindInvalidDynTraitVtableUse(StarkTypeSymbol type, out StarkTypeSymbol invalidVtableType)
    {
        return TryFindInvalidDynTraitVtableUse(type, isDirectRawPointerPointee: false, out invalidVtableType);
    }

    private static bool TryFindInvalidDynTraitVtableUse(
        StarkTypeSymbol type,
        bool isDirectRawPointerPointee,
        out StarkTypeSymbol invalidVtableType)
    {
        if (StarkTypeSymbols.IsDynTraitVtableType(type))
        {
            invalidVtableType = StarkTypeSymbols.WithQualifiers(
                type,
                borrowKind: StarkBorrowKind.None,
                accessKind: StarkAccessKind.None,
                initializationKind: StarkInitializationKind.None,
                isMutableView: false);
            return !isDirectRawPointerPointee;
        }

        if (type.Kind == StarkTypeKind.RawPointer && type.ElementType is not null)
        {
            if (type.IsMutablePointer && StarkTypeSymbols.IsDynTraitVtableType(type.ElementType))
            {
                invalidVtableType = StarkTypeSymbols.WithQualifiers(
                    type.ElementType,
                    borrowKind: StarkBorrowKind.None,
                    accessKind: StarkAccessKind.None,
                    initializationKind: StarkInitializationKind.None,
                    isMutableView: false);
                return true;
            }

            return TryFindInvalidDynTraitVtableUse(
                type.ElementType,
                isDirectRawPointerPointee: !type.IsMutablePointer,
                out invalidVtableType);
        }

        if (type.ElementType is not null
            && TryFindInvalidDynTraitVtableUse(type.ElementType, isDirectRawPointerPointee: false, out invalidVtableType))
        {
            return true;
        }

        if (type.FunctionPointerReturnType is not null
            && TryFindInvalidDynTraitVtableUse(type.FunctionPointerReturnType, isDirectRawPointerPointee: false, out invalidVtableType))
        {
            return true;
        }

        if (type.FunctionPointerParameterTypes is { Count: > 0 })
        {
            foreach (var parameterType in type.FunctionPointerParameterTypes)
            {
                if (TryFindInvalidDynTraitVtableUse(parameterType, isDirectRawPointerPointee: false, out invalidVtableType))
                {
                    return true;
                }
            }
        }

        if (type.ClosureReturnType is not null
            && TryFindInvalidDynTraitVtableUse(type.ClosureReturnType, isDirectRawPointerPointee: false, out invalidVtableType))
        {
            return true;
        }

        if (type.ClosureParameterTypes is { Count: > 0 })
        {
            foreach (var parameterType in type.ClosureParameterTypes)
            {
                if (TryFindInvalidDynTraitVtableUse(parameterType, isDirectRawPointerPointee: false, out invalidVtableType))
                {
                    return true;
                }
            }
        }

        if (type.TypeArguments is { Count: > 0 })
        {
            foreach (var argumentType in type.TypeArguments)
            {
                if (TryFindInvalidDynTraitVtableUse(argumentType, isDirectRawPointerPointee: false, out invalidVtableType))
                {
                    return true;
                }
            }
        }

        invalidVtableType = StarkTypeSymbols.Error;
        return false;
    }

    private static bool IsDirectInlineClosureType(StarkTypeSymbol type)
    {
        return type.Kind == StarkTypeKind.Closure
            && type.ClosureStorageKind == StarkClosureStorageKind.Inline;
    }

    private static bool ContainsInlineClosureType(StarkTypeSymbol type)
    {
        return type.Kind == StarkTypeKind.Closure
            && type.ClosureStorageKind == StarkClosureStorageKind.Inline
            || type.ElementType is not null && ContainsInlineClosureType(type.ElementType)
            || type.FunctionPointerParameterTypes is { Count: > 0 } && type.FunctionPointerParameterTypes.Any(ContainsInlineClosureType)
            || type.FunctionPointerReturnType is not null && ContainsInlineClosureType(type.FunctionPointerReturnType)
            || type.ClosureParameterTypes is { Count: > 0 } && type.ClosureParameterTypes.Any(ContainsInlineClosureType)
            || type.ClosureReturnType is not null && ContainsInlineClosureType(type.ClosureReturnType)
            || type.TypeArguments is { Count: > 0 } && type.TypeArguments.Any(ContainsInlineClosureType);
    }

    private static bool TryFindInvalidCVoidUse(StarkTypeSymbol type, out StarkTypeSymbol invalidCVoidType)
    {
        return TryFindInvalidCVoidUse(type, isDirectRawPointerPointee: false, out invalidCVoidType);
    }

    private static bool TryFindInvalidCVoidUse(
        StarkTypeSymbol type,
        bool isDirectRawPointerPointee,
        out StarkTypeSymbol invalidCVoidType)
    {
        if (type.Kind == StarkTypeKind.CVoid)
        {
            invalidCVoidType = type;
            return !isDirectRawPointerPointee;
        }

        if (type.Kind == StarkTypeKind.RawPointer && type.ElementType is not null)
        {
            return TryFindInvalidCVoidUse(
                type.ElementType,
                isDirectRawPointerPointee: true,
                out invalidCVoidType);
        }

        if (type.ElementType is not null
            && TryFindInvalidCVoidUse(type.ElementType, isDirectRawPointerPointee: false, out invalidCVoidType))
        {
            return true;
        }

        if (type.FunctionPointerReturnType is not null
            && TryFindInvalidCVoidUse(type.FunctionPointerReturnType, isDirectRawPointerPointee: false, out invalidCVoidType))
        {
            return true;
        }

        foreach (var parameterType in type.FunctionPointerParameterTypes ?? [])
        {
            if (TryFindInvalidCVoidUse(parameterType, isDirectRawPointerPointee: false, out invalidCVoidType))
            {
                return true;
            }
        }

        if (type.ClosureReturnType is not null
            && TryFindInvalidCVoidUse(type.ClosureReturnType, isDirectRawPointerPointee: false, out invalidCVoidType))
        {
            return true;
        }

        foreach (var parameterType in type.ClosureParameterTypes ?? [])
        {
            if (TryFindInvalidCVoidUse(parameterType, isDirectRawPointerPointee: false, out invalidCVoidType))
            {
                return true;
            }
        }

        foreach (var typeArgument in type.TypeArguments ?? [])
        {
            if (TryFindInvalidCVoidUse(typeArgument, isDirectRawPointerPointee: false, out invalidCVoidType))
            {
                return true;
            }
        }

        invalidCVoidType = StarkTypeSymbols.Error;
        return false;
    }

    private void ValidateAsmSignatureSurface(
        string functionName,
        StarkTypeSymbol returnType,
        StarkParser.ReturnTypeContext returnContext,
        IReadOnlyList<TypedParameterSymbol> parameters,
        IReadOnlyList<StarkParser.ParameterContext> parameterContexts,
        AsmFunctionModel asmFunction)
    {
        if (!IsSupportedAsmValueType(returnType, allowVoid: true))
        {
            ReportError(
                "STK3008",
                $"Asm function '{functionName}' currently supports only integer scalars, floating-point scalars, raw pointers, and 'void' at the ABI boundary, but found return type '{returnType.DisplayName}'.",
                returnContext);
        }

        var parametersByName = new Dictionary<string, TypedParameterSymbol>(StringComparer.Ordinal);
        var parameterContextsByName = new Dictionary<string, StarkParser.ParameterContext>(StringComparer.Ordinal);
        for (var index = 0; index < parameters.Count; index++)
        {
            var parameter = parameters[index];
            parametersByName[parameter.Name] = parameter;
            parameterContextsByName[parameter.Name] = parameterContexts[index];

            if (IsSupportedAsmValueType(parameter.Type, allowVoid: false))
            {
                continue;
            }

            ReportError(
                "STK3008",
                $"Asm function '{functionName}' currently supports only integer scalars, floating-point scalars, and raw pointers on parameters, but parameter '{parameter.Name}' has type '{parameter.Type.DisplayName}'.",
                parameterContexts[index].type_());
        }

        if (returnType.Kind != StarkTypeKind.Void && IsSupportedAsmValueType(returnType, allowVoid: false))
        {
            var returnBinding = asmFunction.Outputs.FirstOrDefault(static output => output.BindsReturnValue);
            if (returnBinding is not null)
            {
                ValidateAsmRegisterBinding(
                    functionName,
                    asmFunction.Architecture,
                    returnBinding.RegisterName,
                    returnType,
                    "return value",
                    returnContext);
            }
        }

        foreach (var input in asmFunction.Inputs)
        {
            if (!parametersByName.TryGetValue(input.ValueName, out var parameter)
                || !parameterContextsByName.TryGetValue(input.ValueName, out var parameterContext)
                || !IsSupportedAsmValueType(parameter.Type, allowVoid: false))
            {
                continue;
            }

            ValidateAsmRegisterBinding(
                functionName,
                asmFunction.Architecture,
                input.RegisterName,
                parameter.Type,
                $"parameter '{input.ValueName}'",
                parameterContext.type_());
        }

        foreach (var output in asmFunction.Outputs)
        {
            if (output.BindsReturnValue
                || !parametersByName.TryGetValue(output.ValueName, out var parameter)
                || !parameterContextsByName.TryGetValue(output.ValueName, out var parameterContext)
                || !IsSupportedAsmValueType(parameter.Type, allowVoid: false))
            {
                continue;
            }

            ValidateAsmRegisterBinding(
                functionName,
                asmFunction.Architecture,
                output.RegisterName,
                parameter.Type,
                $"parameter '{output.ValueName}'",
                parameterContext.type_());
        }
    }

    private static bool IsSupportedAsmValueType(StarkTypeSymbol type, bool allowVoid)
    {
        if (type.Kind == StarkTypeKind.Error)
        {
            return true;
        }

        if (allowVoid && type.Kind == StarkTypeKind.Void)
        {
            return true;
        }

        if (type.BorrowKind != StarkBorrowKind.None
            || type.AccessKind != StarkAccessKind.None)
        {
            return false;
        }

        return type.Kind is StarkTypeKind.Integer or StarkTypeKind.Float or StarkTypeKind.RawPointer;
    }

    private void ValidateAsmRegisterBinding(
        string functionName,
        StarkAsmArchitecture architecture,
        string registerName,
        StarkTypeSymbol valueType,
        string valueDescription,
        ParserRuleContext context)
    {
        if (!TryGetExpectedAsmRegisterClass(valueType, out var expectedRegisterClass)
            || !StarkAsmRegisterFacts.TryGetRegisterClass(architecture, registerName, out var actualRegisterClass)
            || actualRegisterClass == expectedRegisterClass)
        {
            return;
        }

        ReportError(
            "STK3008",
            $"Asm function '{functionName}' binds {valueDescription} of type '{valueType.DisplayName}' to register '{registerName}', but '{registerName}' is a {DescribeAsmRegisterClass(actualRegisterClass)} register. {DescribeAsmRegisterExpectation(expectedRegisterClass, architecture)}",
            context);
    }

    private static bool TryGetExpectedAsmRegisterClass(StarkTypeSymbol type, out StarkAsmRegisterClass registerClass)
    {
        switch (type.Kind)
        {
            case StarkTypeKind.Integer:
            case StarkTypeKind.RawPointer:
                registerClass = StarkAsmRegisterClass.GeneralPurpose;
                return true;
            case StarkTypeKind.Float:
                registerClass = StarkAsmRegisterClass.FloatingPoint;
                return true;
            default:
                registerClass = StarkAsmRegisterClass.Unknown;
                return false;
        }
    }

    private static string DescribeAsmRegisterClass(StarkAsmRegisterClass registerClass)
    {
        return registerClass switch
        {
            StarkAsmRegisterClass.GeneralPurpose => "general-purpose",
            StarkAsmRegisterClass.FloatingPoint => "floating-point",
            _ => "unknown"
        };
    }

    private static string DescribeAsmRegisterExpectation(StarkAsmRegisterClass registerClass, StarkAsmArchitecture architecture)
    {
        var architectureName = architecture switch
        {
            StarkAsmArchitecture.X86_64 => "x86_64",
            StarkAsmArchitecture.AArch64 => "aarch64",
            StarkAsmArchitecture.RiscV64 => "riscv64",
            StarkAsmArchitecture.X86 => "x86",
            StarkAsmArchitecture.Arm32 => "arm",
            _ => "the active target"
        };

        return registerClass == StarkAsmRegisterClass.FloatingPoint
            ? $"Floating-point values must use a floating-point register on {architectureName}."
            : $"Integer and raw-pointer values must use a general-purpose register on {architectureName}.";
    }

    private void ValidateRuntimeTypeDoesNotDependOnEnum(StarkTypeSymbol type, ParserRuleContext context, string usage)
    {
        if (TryFindEnumDependency(type, out var enumName))
        {
            ReportError(
                "STK3008",
                $"Type '{type.DisplayName}' depends on enum '{enumName}', but enum-dependent runtime layout is not valid in {usage}.",
                context);
        }
    }

    private void ValidateAbiTypeDoesNotDependOnEnum(StarkTypeSymbol type, ParserRuleContext context, string usage)
    {
        if (TryFindEnumDependency(type, out var enumName))
        {
            ReportError(
                "STK3008",
                $"Type '{type.DisplayName}' depends on enum '{enumName}', but Stark enums cannot cross FFI/export boundaries for {usage}.",
                context);
        }
    }

    private bool TryFindEnumDependency(StarkTypeSymbol type, out string enumName)
    {
        return TryFindEnumDependency(type, out enumName, new HashSet<string>(StringComparer.Ordinal));
    }

    private bool TryFindEnumDependency(
        StarkTypeSymbol type,
        out string enumName,
        ISet<string> activeNamedTypes)
    {
        if (type.Kind == StarkTypeKind.Named
            && type.NamedType is not null
            && _namedTypes.TryGetValue(type.NamedType, out var namedType)
            && namedType.Kind == DeclarationKind.Enum)
        {
            enumName = namedType.Name;
            return true;
        }

        if (type.Kind == StarkTypeKind.Named
            && type.NamedType is not null
            && _namedTypes.TryGetValue(type.NamedType, out var aggregateType))
        {
            if (!activeNamedTypes.Add(aggregateType.Name))
            {
                enumName = string.Empty;
                return false;
            }

            try
            {
                foreach (var field in aggregateType.OrderedFields)
                {
                    if (TryFindEnumDependency(field.Type, out enumName, activeNamedTypes))
                    {
                        return true;
                    }
                }
            }
            finally
            {
                activeNamedTypes.Remove(aggregateType.Name);
            }
        }

        if (type.ElementType is not null)
        {
            return TryFindEnumDependency(type.ElementType, out enumName, activeNamedTypes);
        }

        enumName = string.Empty;
        return false;
    }

    private bool TryFindCompileTimeOnlyTypeDependency(
        StarkTypeSymbol type,
        out string dependencyName,
        out DeclarationKind dependencyKind)
    {
        return TryFindCompileTimeOnlyTypeDependency(type, out dependencyName, out dependencyKind, new HashSet<string>(StringComparer.Ordinal));
    }

    private bool TryFindCompileTimeOnlyTypeDependency(
        StarkTypeSymbol type,
        out string dependencyName,
        out DeclarationKind dependencyKind,
        ISet<string> activeNamedTypes)
    {
        if (type.Kind == StarkTypeKind.Named
            && type.NamedType is not null
            && _namedTypes.TryGetValue(type.NamedType, out var namedType)
            && namedType.Kind is DeclarationKind.Doctrine or DeclarationKind.Trait)
        {
            dependencyName = namedType.Name;
            dependencyKind = namedType.Kind;
            return true;
        }

        if (type.Kind == StarkTypeKind.Named
            && type.NamedType is not null
            && _namedTypes.TryGetValue(type.NamedType, out var aggregateType))
        {
            if (!activeNamedTypes.Add(aggregateType.Name))
            {
                dependencyName = string.Empty;
                dependencyKind = default;
                return false;
            }

            try
            {
                foreach (var field in aggregateType.OrderedFields)
                {
                    if (TryFindCompileTimeOnlyTypeDependency(field.Type, out dependencyName, out dependencyKind, activeNamedTypes))
                    {
                        return true;
                    }
                }

                foreach (var variant in aggregateType.Variants)
                {
                    foreach (var field in variant.Fields)
                    {
                        if (TryFindCompileTimeOnlyTypeDependency(field.Type, out dependencyName, out dependencyKind, activeNamedTypes))
                        {
                            return true;
                        }
                    }
                }
            }
            finally
            {
                activeNamedTypes.Remove(aggregateType.Name);
            }
        }

        if (type.ElementType is not null)
        {
            return TryFindCompileTimeOnlyTypeDependency(type.ElementType, out dependencyName, out dependencyKind, activeNamedTypes);
        }

        dependencyName = string.Empty;
        dependencyKind = default;
        return false;
    }

    private TypedFunctionSignature CacheFunctionInstantiation(TypedFunctionSignature signature)
    {
        if (!signature.IsGenericInstantiation
            || signature.TemplateName is null)
        {
            return signature;
        }

        var key = BuildFunctionInstantiationKey(
            signature.TemplateName,
            signature.TypeArguments ?? [],
            signature.ComptimeValueArguments);
        if (_functionInstantiationCache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        _functionInstantiationCache[key] = signature;
        return signature;
    }

    private void RecordFunctionInstantiationTrigger(TypedFunctionSignature signature, ParserRuleContext context)
        => RecordFunctionInstantiationTrigger(signature, Location(context));

    private void RecordFunctionInstantiationTrigger(TypedFunctionSignature signature, SourceLocation location)
    {
        if (!signature.IsGenericInstantiation)
        {
            return;
        }

        if ((signature.TypeArguments ?? []).Any(TypeContainsOpenCurrentFunctionGenericParameter)
            || SignatureContainsOpenCurrentFunctionComptimeParameter(signature))
        {
            RecordDeferredFunctionInstantiationTrigger(signature, location);
            return;
        }

        var key = BuildFunctionInstantiationKey(
            signature.TemplateName ?? signature.DisplaySourceName,
            signature.TypeArguments ?? [],
            signature.ComptimeValueArguments);
        if (!_functionInstantiationKeys.Add(key))
        {
            return;
        }

        _functionInstantiationTriggers.Add(new FunctionInstantiationTriggerRecord(
            signature.DisplaySourceName,
            (signature.TypeArguments ?? []).ToArray(),
            signature.ComptimeValueArguments?.ToArray(),
            signature,
            location));
    }

    private void RecordDeferredFunctionInstantiationTrigger(TypedFunctionSignature signature, SourceLocation location)
    {
        if (_currentFunctionName is null
            || signature.TemplateName is not { } templateName
            || !signature.IsGenericInstantiation)
        {
            return;
        }

        var key = $"{_currentFunctionName}|{templateName}|{FunctionOverloadFacts.BuildInstantiationArgumentKey(signature.TypeArguments, signature.ComptimeValueArguments)}";
        if (!_deferredFunctionInstantiationKeys.Add(key))
        {
            return;
        }

        _deferredFunctionInstantiationTriggers.Add(new DeferredFunctionInstantiationTriggerRecord(
            _currentFunctionName,
            signature,
            location));
    }

    private void RecordTypeInstantiationTriggers(StarkTypeSymbol type, SourceLocation location)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var activeNamedTypes = new HashSet<string>(StringComparer.Ordinal);
        RecordTypeInstantiationTriggers(type, location, seen, activeNamedTypes);
    }

    private void RecordTypeInstantiationTriggers(
        StarkTypeSymbol type,
        SourceLocation location,
        ISet<string> seen,
        ISet<string> activeNamedTypes)
    {
        var coreType = StarkTypeSymbols.WithQualifiers(
            type,
            borrowKind: StarkBorrowKind.None,
            accessKind: StarkAccessKind.None,
            initializationKind: StarkInitializationKind.None,
            isMutableView: false);

        if (coreType.TypeArguments is { Count: > 0 })
        {
            foreach (var typeArgument in coreType.TypeArguments)
            {
                RecordTypeInstantiationTriggers(typeArgument, location, seen, activeNamedTypes);
            }
        }

        if (coreType.ElementType is not null)
        {
            RecordTypeInstantiationTriggers(coreType.ElementType, location, seen, activeNamedTypes);
        }

        if (!StarkTypeSymbols.IsGenericInstantiation(coreType)
            && coreType.Kind == StarkTypeKind.Named
            && coreType.NamedType is { } namedTypeName
            && _namedTypes.TryGetValue(namedTypeName, out var namedType))
        {
            if (!activeNamedTypes.Add(namedTypeName))
            {
                return;
            }

            try
            {
                foreach (var field in namedType.OrderedFields)
                {
                    RecordTypeInstantiationTriggers(field.Type, location, seen, activeNamedTypes);
                }

                foreach (var variant in namedType.Variants)
                {
                    foreach (var field in variant.Fields)
                    {
                        RecordTypeInstantiationTriggers(field.Type, location, seen, activeNamedTypes);
                    }
                }
            }
            finally
            {
                activeNamedTypes.Remove(namedTypeName);
            }
        }

        if (!StarkTypeSymbols.IsGenericInstantiation(coreType)
            || coreType.NamedType is null
            || TypeContainsOpenCurrentFunctionGenericParameter(coreType)
            || TypeContainsOpenCurrentFunctionComptimeParameter(coreType))
        {
            if (StarkTypeSymbols.IsGenericInstantiation(coreType)
                && coreType.NamedType is not null
                && (TypeContainsOpenCurrentFunctionGenericParameter(coreType)
                    || TypeContainsOpenCurrentFunctionComptimeParameter(coreType)))
            {
                RecordDeferredTypeInstantiationTrigger(coreType, location);
            }

            return;
        }

        var localKey = $"{coreType.NamedType}@{location.FilePath}:{location.Line}:{location.Column}";
        if (!seen.Add(localKey))
        {
            return;
        }

        var globalKey = BuildTypeInstantiationKey(
            coreType.NamedType,
            coreType.TypeArguments ?? [],
            coreType.ComptimeValueArguments);
        if (!_typeInstantiationKeys.Add(globalKey))
        {
            return;
        }

        _typeInstantiationTriggers.Add(new TypeInstantiationTriggerRecord(
            coreType.NamedType,
            (coreType.TypeArguments ?? []).ToArray(),
            coreType.ComptimeValueArguments?.ToArray(),
            location));

        if (_namedTypes.TryGetValue(coreType.NamedType, out var instantiatedType))
        {
            if (!activeNamedTypes.Add(coreType.NamedType))
            {
                return;
            }

            try
            {
                foreach (var field in instantiatedType.OrderedFields)
                {
                    RecordTypeInstantiationTriggers(field.Type, location, seen, activeNamedTypes);
                }

                foreach (var variant in instantiatedType.Variants)
                {
                    foreach (var field in variant.Fields)
                    {
                        RecordTypeInstantiationTriggers(field.Type, location, seen, activeNamedTypes);
                    }
                }
            }
            finally
            {
                activeNamedTypes.Remove(coreType.NamedType);
            }
        }
    }

    private void RecordDeferredTypeInstantiationTrigger(StarkTypeSymbol type, SourceLocation location)
    {
        if (_currentFunctionName is null
            || !StarkTypeSymbols.IsGenericInstantiation(type)
            || type.NamedType is null
            || (type.TypeArguments is not { Count: > 0 }
                && type.ComptimeValueArguments is not { Count: > 0 }))
        {
            return;
        }

        var key = $"{_currentFunctionName}|{BuildTypeInstantiationKey(type.NamedType, type.TypeArguments ?? [], type.ComptimeValueArguments)}";
        if (!_deferredTypeInstantiationKeys.Add(key))
        {
            return;
        }

        _deferredTypeInstantiationTriggers.Add(new DeferredTypeInstantiationTriggerRecord(
            _currentFunctionName,
            type,
            location));
    }

    private bool TypeContainsOpenCurrentFunctionGenericParameter(StarkTypeSymbol type)
    {
        if (_currentFunctionGenericParameters is not { Count: > 0 })
        {
            return false;
        }

        var coreType = StarkTypeSymbols.WithQualifiers(
            type,
            borrowKind: StarkBorrowKind.None,
            accessKind: StarkAccessKind.None,
            initializationKind: StarkInitializationKind.None,
            isMutableView: false);

        if (coreType.Kind == StarkTypeKind.Named && coreType.NamedType is { } name)
        {
            if (_currentFunctionGenericParameters.Contains(name))
            {
                return true;
            }

            if (coreType.TypeArguments is { Count: > 0 })
            {
                return coreType.TypeArguments.Any(TypeContainsOpenCurrentFunctionGenericParameter);
            }
        }

        if (coreType.Kind == StarkTypeKind.FunctionPointer)
        {
            return coreType.FunctionPointerReturnType is not null
                && TypeContainsOpenCurrentFunctionGenericParameter(coreType.FunctionPointerReturnType)
                || coreType.FunctionPointerParameterTypes is { Count: > 0 }
                && coreType.FunctionPointerParameterTypes.Any(TypeContainsOpenCurrentFunctionGenericParameter);
        }

        if (coreType.Kind == StarkTypeKind.Closure)
        {
            return coreType.ClosureReturnType is not null
                && TypeContainsOpenCurrentFunctionGenericParameter(coreType.ClosureReturnType)
                || coreType.ClosureParameterTypes is { Count: > 0 }
                && coreType.ClosureParameterTypes.Any(TypeContainsOpenCurrentFunctionGenericParameter);
        }

        if (coreType.Kind == StarkTypeKind.AssociatedType)
        {
            return true;
        }

        return coreType.ElementType is not null
            && TypeContainsOpenCurrentFunctionGenericParameter(coreType.ElementType);
    }

    private bool TypeContainsOpenCurrentFunctionComptimeParameter(StarkTypeSymbol type)
    {
        return _currentFunctionComptimeGenericParameters is { Count: > 0 } parameters
            && FunctionOverloadFacts.ContainsComptimeValueParameter(type, parameters.Keys);
    }

    private bool SignatureContainsOpenCurrentFunctionComptimeParameter(TypedFunctionSignature signature)
    {
        if (_currentFunctionComptimeGenericParameters is not { Count: > 0 })
        {
            return false;
        }

        return TypeContainsOpenCurrentFunctionComptimeParameter(signature.ReturnType)
            || signature.Parameters.Any(parameter => TypeContainsOpenCurrentFunctionComptimeParameter(parameter.Type))
            || (signature.TypeArguments ?? []).Any(TypeContainsOpenCurrentFunctionComptimeParameter)
            || (signature.ComptimeValueArguments ?? []).Any(ValueArgumentContainsOpenCurrentFunctionComptimeParameter);
    }

    private bool ValueArgumentContainsOpenCurrentFunctionComptimeParameter(ComptimeValueArgumentSymbol argument)
    {
        return argument.IsSymbolic
            && _currentFunctionComptimeGenericParameters is { Count: > 0 } parameters
            && parameters.ContainsKey(argument.SourceName);
    }

    private static string BuildFunctionInstantiationKey(
        string templateName,
        IReadOnlyList<StarkTypeSymbol> typeArguments,
        IReadOnlyList<ComptimeValueArgumentSymbol>? valueArguments = null)
    {
        return $"{templateName}|{FunctionOverloadFacts.BuildInstantiationArgumentKey(typeArguments, valueArguments)}";
    }

    private static string BuildTypeInstantiationKey(
        string typeName,
        IReadOnlyList<StarkTypeSymbol> typeArguments,
        IReadOnlyList<ComptimeValueArgumentSymbol>? valueArguments = null)
    {
        return $"{StarkTypeSymbols.GetGenericBaseName(typeName)}|{FunctionOverloadFacts.BuildInstantiationArgumentKey(typeArguments, valueArguments)}";
    }

    private static string DescribeCompileTimeOnlyKind(DeclarationKind kind)
    {
        return kind switch
        {
            DeclarationKind.Doctrine => "doctrine",
            DeclarationKind.Trait => "trait",
            _ => "type"
        };
    }

    private static string DescribeNoDynamicDispatchPolicy()
    {
        return "Stark v1.x has no runtime dispatch values for traits or doctrines.";
    }

    private HashSet<string>? GetGenericParameterNames(StarkParser.TypeParameterListContext? typeParameterList)
    {
        return _typeResolver!.GetGenericParameterNames(typeParameterList);
    }

    private IReadOnlyList<ComptimeGenericParameterSymbol> GetComptimeGenericParameters(
        StarkParser.TypeParameterListContext? typeParameterList,
        ISet<string>? genericParameters,
        string currentModuleName)
    {
        if (typeParameterList is null)
        {
            return [];
        }

        var parameters = new List<ComptimeGenericParameterSymbol>();
        foreach (var parameter in typeParameterList.typeParameter())
        {
            if (parameter.COMPTIME() is null)
            {
                continue;
            }

            var parameterName = parameter.Identifier().GetText();
            var parameterType = ResolveType(parameter.type_(), genericParameters, currentModuleName);
            if (parameterType.Kind != StarkTypeKind.Error && parameterType.Kind != StarkTypeKind.Integer)
            {
                ReportError(
                    "STK3050",
                    $"Comptime generic parameter '{parameterName}' must currently use a range-typed integer type, but found '{parameterType.DisplayName}'.",
                    parameter.type_());
                parameterType = StarkTypeSymbols.Error;
            }

            parameters.Add(new ComptimeGenericParameterSymbol(parameterName, parameterType));
        }

        return parameters;
    }

    private static IReadOnlyDictionary<string, ComptimeGenericParameterSymbol>? ToComptimeGenericParameterMap(
        IReadOnlyList<ComptimeGenericParameterSymbol> parameters)
    {
        return parameters.Count == 0
            ? null
            : parameters.ToDictionary(static parameter => parameter.Name, StringComparer.Ordinal);
    }

    private bool IsDeclarationVisible(LoadedModuleDocument module, TopLevelDeclarationModel declaration)
    {
        if (module.Reference.IsRoot)
        {
            return true;
        }

        return declaration.Visibility switch
        {
            StarkVisibility.Module => false,
            StarkVisibility.Internal => !module.Reference.IsExternal,
            StarkVisibility.Public => true,
            StarkVisibility.Export => true,
            _ => false
        };
    }

    private static StarkVisibility ResolveFieldVisibility(
        StarkVisibility containingVisibility,
        StarkParser.VisibilityModifierContext? explicitVisibility)
    {
        return explicitVisibility is null
            ? InheritedFieldVisibility(containingVisibility)
            : ParseVisibility(explicitVisibility);
    }

    private static StarkVisibility InheritedFieldVisibility(StarkVisibility containingVisibility)
    {
        return containingVisibility == StarkVisibility.Export
            ? StarkVisibility.Public
            : containingVisibility;
    }

    private static StarkVisibility ParseVisibility(StarkParser.VisibilityModifierContext visibilityModifier)
    {
        return visibilityModifier.GetText() switch
        {
            "internal" => StarkVisibility.Internal,
            "public" => StarkVisibility.Public,
            "export" => StarkVisibility.Export,
            _ => StarkVisibility.Module
        };
    }

    private static bool IsMoreVisible(StarkVisibility candidate, StarkVisibility containing)
    {
        return VisibilityRank(candidate) > VisibilityRank(containing);
    }

    private static int VisibilityRank(StarkVisibility visibility)
    {
        return visibility switch
        {
            StarkVisibility.Module => 0,
            StarkVisibility.Internal => 1,
            StarkVisibility.Public => 2,
            StarkVisibility.Export => 3,
            _ => 0
        };
    }

    private static string RenderVisibility(StarkVisibility visibility)
    {
        return visibility.ToString().ToLowerInvariant();
    }

    private bool IsFieldAccessible(FieldSymbol field)
    {
        return field.Visibility switch
        {
            StarkVisibility.Module => string.Equals(field.DeclaringModuleName, CurrentFunctionModuleName, StringComparison.Ordinal),
            StarkVisibility.Internal => field.DeclaringModuleName is null || IsSamePackageField(field),
            StarkVisibility.Public => true,
            StarkVisibility.Export => true,
            _ => false
        };
    }

    private bool IsSamePackageField(FieldSymbol field)
    {
        return field.DeclaringModuleName is null
            || !_loadedModules.TryGet(field.DeclaringModuleName, out var declaringModule)
            || declaringModule is null
            || !declaringModule.Reference.IsExternal;
    }

    private string QualifyName(LoadedModuleDocument module, string localName)
    {
        return module.Reference.IsRoot
            ? localName
            : $"{module.SyntaxModel.ModuleName}.{localName}";
    }

    private void ReportError(string code, string message, ParserRuleContext context)
    {
        _context.Diagnostics.Error(code, message, "type-check", Location(context));
    }

    private void ReportError(string code, string message, IToken token)
    {
        _context.Diagnostics.Error(code, message, "type-check", Location(token));
    }

    private void ReportWarning(string code, string message, ParserRuleContext context)
    {
        _context.Diagnostics.Warning(code, message, "type-check", Location(context));
    }

    private void ReportWarning(string code, string message, IToken token)
    {
        _context.Diagnostics.Warning(code, message, "type-check", Location(token));
    }

    private void ReportInfo(string code, string message, ParserRuleContext context)
    {
        _context.Diagnostics.Info(code, message, "type-check", Location(context));
    }

    private SourceLocation Location(ParserRuleContext context) => Location(context.Start, context.Stop);

    private SourceLocation Location(IToken token) => Location(token, token);

    private SourceLocation Location(IToken start, IToken? stop)
    {
        var resolvedStop = stop ?? start;
        var (endLine, endColumn) = GetTokenEndPosition(resolvedStop);
        return new SourceLocation(_context.Input.FilePath, start.Line, start.Column + 1, endLine, endColumn);
    }

    private static (int Line, int Column) GetTokenEndPosition(IToken token)
    {
        var tokenText = token.Text;
        if (string.IsNullOrEmpty(tokenText))
        {
            return (token.Line, token.Column + 1);
        }

        var normalizedText = tokenText.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        var lines = normalizedText.Split('\n');
        if (lines.Length == 1)
        {
            return (token.Line, token.Column + Math.Max(lines[0].Length, 1));
        }

        return (token.Line + lines.Length - 1, Math.Max(lines[^1].Length, 1));
    }

    private sealed record VariableSymbol(
        string Name,
        StarkTypeSymbol Type,
        bool IsMutable,
        bool IsConstant,
        GlobalBindingKind? BindingKind = null,
        CompileTimeConstant? ConstantValue = null,
        bool UsesFrozenProjectionSemantics = false,
        bool HasConstProvenance = false,
        string? MemoryRootKey = null,
        bool MemoryRootIsIndependentStorage = false,
        string? RawPointerElementCountExpression = null,
        TypedConstantInitializer? ConstantInitializer = null);

    private sealed record LambdaCaptureBinding(
        VariableSymbol Symbol,
        string Mode,
        bool IsUnsafe);

    private sealed record ConcreteGenericTypeArguments(
        IReadOnlyList<StarkTypeSymbol> TypeArguments,
        IReadOnlyList<ComptimeValueArgumentSymbol>? ComptimeValueArgumentNames = null)
    {
        public IReadOnlyList<ComptimeValueArgumentSymbol> ComptimeValueArguments =>
            ComptimeValueArgumentNames ?? [];
    }

    private sealed record ExpressionBinding(
        StarkTypeSymbol Type,
        bool IsAssignable = false,
        NamedTypeSymbol? NamedType = null,
        TypedFunctionSignature? Function = null,
        string? OverloadSourceName = null,
        string? NamespaceName = null,
        string? DiagnosticName = null,
        ExpressionBinding? Receiver = null,
        IReadOnlyList<TypedFunctionSignature>? OverloadCandidates = null,
        bool IsAddressable = false,
        bool IsAddressMutable = false,
        string? RootGlobalName = null,
        GlobalBindingKind? RootGlobalBindingKind = null,
        string? AssignmentErrorMessage = null,
        EnumConstructorBinding? EnumConstructor = null,
        string? TextLiteral = null,
        TextLiteralKind? TextLiteralKind = null,
        bool UsesFrozenProjectionSemantics = false,
        bool HasConstProvenance = false,
        string? MemoryRootKey = null,
        bool MemoryRootIsIndependentStorage = false,
        bool IsMisalignedFieldProjection = false,
        BigInteger? IntegerLiteralValue = null);

    private sealed record TraversalSourceInfo(
        StarkTypeSymbol ElementType,
        StarkTypeSymbol IndexRangeType,
        bool CanBorrowElementMutably);

    private sealed record LocalMemoryProvenance(
        string RootKey,
        bool IsIndependentStorage,
        string? RawPointerElementCountExpression = null);

    private sealed record FunctionGlobalReference(
        string FunctionName,
        string GlobalName,
        SourceLocation Location);

    private sealed record EnumConstructorBinding(
        string Name,
        EnumVariantSymbol Variant);

    private sealed record ConstructorShape(
        string Name,
        IReadOnlyList<TypedParameterSymbol> Parameters,
        bool IsPrimaryShape,
        string? BodyKey)
    {
        public ISet<string>? InitializedMembers =>
            IsPrimaryShape
                ? Parameters.Select(static parameter => parameter.Name).ToHashSet(StringComparer.Ordinal)
                : null;
    }

    private sealed record ImportedConstructorBodyKey(
        string QualifiedTypeName,
        IReadOnlyList<TypedParameterSymbol> Parameters,
        string SignatureKey,
        string BodyKey);

    private static string BuildConstructorBodyKey(string qualifiedTypeName, StarkParser.ConstructorDeclarationContext constructor)
    {
        return $"{qualifiedTypeName}@{constructor.Start.Line}:{constructor.Start.Column + 1}";
    }

    private sealed class Scope
    {
        private readonly Dictionary<string, VariableSymbol> _locals = new(StringComparer.Ordinal);
        private readonly Dictionary<string, VariableSymbol>? _globals;
        private readonly List<IReadOnlyList<string>> _disjointFacts = [];
        private readonly List<IReadOnlyList<string>> _sameFacts = [];
        private readonly HashSet<string> _flowAssignedOuterLocalNames = new(StringComparer.Ordinal);

        public Scope(Scope parent)
        {
            Parent = parent;
        }

        private Scope(Dictionary<string, VariableSymbol> globals)
        {
            _globals = globals;
        }

        public Scope? Parent { get; }

        public IReadOnlyCollection<string> FlowAssignedOuterLocalNames => _flowAssignedOuterLocalNames;

        public static Scope CreateRoot(Dictionary<string, VariableSymbol> globals) => new(globals);

        public void AddDisjointFact(IReadOnlyList<string> rootKeys)
        {
            var distinctRootKeys = rootKeys
                .Where(static rootKey => !string.IsNullOrWhiteSpace(rootKey))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (distinctRootKeys.Length >= 2)
            {
                _disjointFacts.Add(distinctRootKeys);
            }
        }

        public bool HasDisjointFact(string leftRootKey, string rightRootKey)
        {
            return _disjointFacts.Any(group => ContainsCoveredRoot(group, leftRootKey)
                                               && ContainsCoveredRoot(group, rightRootKey)
                                               && !CoveredBySameFactRoot(group, leftRootKey, rightRootKey))
                || Parent?.HasDisjointFact(leftRootKey, rightRootKey) == true;
        }

        public void AddSameFact(IReadOnlyList<string> rootKeys)
        {
            var distinctRootKeys = rootKeys
                .Where(static rootKey => !string.IsNullOrWhiteSpace(rootKey))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (distinctRootKeys.Length >= 2)
            {
                _sameFacts.Add(distinctRootKeys);
            }
        }

        public bool HasSameFact(string leftRootKey, string rightRootKey)
        {
            return _sameFacts.Any(group => ContainsSameFactPair(group, leftRootKey, rightRootKey))
                || Parent?.HasSameFact(leftRootKey, rightRootKey) == true;
        }

        public void Declare(VariableSymbol symbol)
        {
            _locals[symbol.Name] = symbol;
        }

        public bool TryLookup(string name, out VariableSymbol symbol)
        {
            if (_locals.TryGetValue(name, out symbol!))
            {
                return true;
            }

            if (Parent is not null)
            {
                return Parent.TryLookup(name, out symbol);
            }

            if (_globals is not null && _globals.TryGetValue(name, out symbol!))
            {
                return true;
            }

            symbol = default!;
            return false;
        }

        public void SetCurrentFlowMemoryProvenance(
            string name,
            string? memoryRootKey,
            bool memoryRootIsIndependentStorage,
            string? rawPointerElementCountExpression)
        {
            if (_locals.TryGetValue(name, out var local))
            {
                _locals[name] = local with
                {
                    MemoryRootKey = memoryRootKey,
                    MemoryRootIsIndependentStorage = memoryRootIsIndependentStorage,
                    RawPointerElementCountExpression = rawPointerElementCountExpression
                };
                return;
            }

            if (Parent?.TryLookupLocal(name, out var outerLocal) == true)
            {
                _locals[name] = outerLocal with
                {
                    MemoryRootKey = memoryRootKey,
                    MemoryRootIsIndependentStorage = memoryRootIsIndependentStorage,
                    RawPointerElementCountExpression = rawPointerElementCountExpression
                };
                _flowAssignedOuterLocalNames.Add(name);
            }
        }

        public void ClearCurrentFlowMemoryProvenance(string name)
        {
            SetCurrentFlowMemoryProvenance(
                name,
                memoryRootKey: null,
                memoryRootIsIndependentStorage: false,
                rawPointerElementCountExpression: null);
        }

        public void InvalidateCurrentFlowMemoryProvenance(IEnumerable<string> names)
        {
            foreach (var name in names)
            {
                ClearCurrentFlowMemoryProvenance(name);
            }
        }

        private bool TryLookupLocal(string name, out VariableSymbol symbol)
        {
            if (_locals.TryGetValue(name, out symbol!))
            {
                return true;
            }

            if (Parent is not null)
            {
                return Parent.TryLookupLocal(name, out symbol);
            }

            symbol = default!;
            return false;
        }

        private static bool ContainsCoveredRoot(IReadOnlyList<string> group, string rootKey)
        {
            return group.Any(factRootKey => IsSameOrDescendantMemoryRoot(rootKey, factRootKey));
        }

        private static bool CoveredBySameFactRoot(
            IReadOnlyList<string> group,
            string leftRootKey,
            string rightRootKey)
        {
            return group.Any(factRootKey => IsSameOrDescendantMemoryRoot(leftRootKey, factRootKey)
                                            && IsSameOrDescendantMemoryRoot(rightRootKey, factRootKey));
        }

        private static bool ContainsSameFactPair(IReadOnlyList<string> group, string leftRootKey, string rightRootKey)
        {
            for (var leftIndex = 0; leftIndex < group.Count; leftIndex++)
            {
                for (var rightIndex = 0; rightIndex < group.Count; rightIndex++)
                {
                    if (leftIndex == rightIndex)
                    {
                        continue;
                    }

                    if (TryGetDescendantSuffix(leftRootKey, group[leftIndex], out var leftSuffix)
                        && TryGetDescendantSuffix(rightRootKey, group[rightIndex], out var rightSuffix)
                        && string.Equals(leftSuffix, rightSuffix, StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool TryGetDescendantSuffix(string rootKey, string ancestorRootKey, out string suffix)
        {
            if (string.Equals(rootKey, ancestorRootKey, StringComparison.Ordinal))
            {
                suffix = string.Empty;
                return true;
            }

            if (IsMemoryRootAncestor(ancestorRootKey, rootKey))
            {
                suffix = rootKey[ancestorRootKey.Length..];
                return true;
            }

            suffix = string.Empty;
            return false;
        }
    }
}
