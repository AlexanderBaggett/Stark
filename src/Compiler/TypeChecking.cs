using System.Globalization;
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

    private enum SwitchCoveragePatternKind
    {
        MatchAll,
        Literal,
        Aggregate,
        EnumCase
    }

    private enum AggregateCoverageFieldKind
    {
        Wildcard,
        Literal,
        NestedAggregate,
        NestedEnum
    }

    private sealed record AggregateCoverageField(
        AggregateCoverageFieldKind Kind,
        string? LiteralKey,
        AggregateCoveragePattern? NestedAggregatePattern,
        EnumCoveragePattern? NestedEnumPattern);

    private sealed record AggregateCoveragePattern(
        string TypeName,
        IReadOnlyList<AggregateCoverageField> Fields);

    private sealed record EnumCoveragePattern(
        string EnumName,
        string VariantName,
        IReadOnlyList<AggregateCoverageField> Fields);

    private sealed record SwitchCoveragePattern(
        SwitchCoveragePatternKind Kind,
        string LabelText,
        ParserRuleContext Context,
        string? LiteralKey,
        AggregateCoveragePattern? AggregatePattern,
        EnumCoveragePattern? EnumPattern);

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
    private readonly Dictionary<string, List<TypedFunctionSignature>> _functionOverloads = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TypedFunctionSignature> _functionInstantiationCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, VariableSymbol> _globals = new(StringComparer.Ordinal);
    private readonly List<LiteralTypingRecord> _literals = [];
    private readonly List<EnumConstructorTypingRecord> _enumConstructors = [];
    private readonly List<EnumCallTypingRecord> _enumCalls = [];
    private readonly List<EnumValueTypingRecord> _enumValues = [];
    private readonly List<EnumPatternTypingRecord> _enumPatterns = [];
    private readonly List<AggregatePatternTypingRecord> _aggregatePatterns = [];
    private readonly List<LocalDeclarationTypingRecord> _localDeclarations = [];
    private readonly List<ConversionTypingRecord> _conversions = [];
    private readonly List<DirectCallTypingRecord> _directCalls = [];
    private readonly List<FunctionPointerPromotionTypingRecord> _functionPointerPromotions = [];
    private readonly List<AddressTakenFunctionTypingRecord> _addressTakenFunctions = [];
    private readonly HashSet<string> _addressTakenFunctionNames = new(StringComparer.Ordinal);
    private readonly List<IndirectCallTypingRecord> _indirectCalls = [];
    private readonly List<LambdaTypingRecord> _lambdas = [];
    private readonly List<LambdaCaptureTypingRecord> _lambdaCaptures = [];
    private readonly List<FieldAccessTypingRecord> _fieldAccesses = [];
    private readonly List<MemberCallTypingRecord> _memberCalls = [];
    private readonly List<ObjectCreationTypingRecord> _objectCreations = [];
    private readonly List<TypeLayoutExpressionTypingRecord> _typeLayoutExpressions = [];
    private readonly List<FunctionInstantiationTriggerRecord> _functionInstantiationTriggers = [];
    private readonly List<DeferredFunctionInstantiationTriggerRecord> _deferredFunctionInstantiationTriggers = [];
    private readonly List<DeferredTypeInstantiationTriggerRecord> _deferredTypeInstantiationTriggers = [];
    private readonly List<TypeInstantiationTriggerRecord> _typeInstantiationTriggers = [];
    private readonly HashSet<StarkParser.AdditiveExpressionContext> _fixedTextStorageConcatExpressions = [];
    private readonly HashSet<StarkParser.LiteralContext> _fixedTextStorageInterpolatedLiterals = [];
    private readonly HashSet<string> _functionInstantiationKeys = new(StringComparer.Ordinal);
    private readonly HashSet<string> _deferredFunctionInstantiationKeys = new(StringComparer.Ordinal);
    private readonly HashSet<string> _deferredTypeInstantiationKeys = new(StringComparer.Ordinal);
    private readonly HashSet<string> _typeInstantiationKeys = new(StringComparer.Ordinal);
    private readonly HashSet<string> _dictionaryKeyConstraintFailures = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<StarkTypeSymbol>> _genericInstantiationArguments = new(StringComparer.Ordinal);
    private readonly HashSet<string> _refreshingConcreteTypes = new(StringComparer.Ordinal);
    private StarkTypeResolver? _typeResolver;
    private ISet<string>? _currentFunctionGenericParameters;
    private string? _currentFunctionName;
    private string? _currentFunctionModuleName;
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
        SeedNamedTypes();
        CollectTypeAliasSources();
        _typeResolver = new StarkTypeResolver(_context, "type-check", _moduleGraph, _namedTypes, _typeAliases, _typeAliasSources);
        CheckTypeAliasDeclarations();
        PopulateNamedTypeFields();
        BuildConstructorShapes();
        BuildFunctionSignatures();
        CheckGlobalDeclarations();
        CheckConstructorBodies();
        CheckFunctionBodies();

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
                    pair.Value.BindingKind ?? (pair.Value.IsMutable ? GlobalBindingKind.Mutable : GlobalBindingKind.Immutable)),
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
            _conversions,
            _directCalls,
            _functionPointerPromotions,
            _indirectCalls,
            _fieldAccesses,
            _memberCalls,
            _typeLayoutExpressions,
            _lambdas,
            _lambdaCaptures,
            _addressTakenFunctions);
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
                _namedTypes[name] = new NamedTypeSymbol(
                    name,
                    declaration.Kind,
                    new Dictionary<string, FieldSymbol>(StringComparer.Ordinal),
                    []);
            }
        }
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
                    _namedTypes[typeName] = BuildStructLikeNamedType(
                        typeName,
                        DeclarationKind.Struct,
                        structDeclaration.structBody().structMember()
                            .Select(static member => member.fieldDeclaration())
                            .Where(static field => field is not null)!,
                        module.SyntaxModel.ModuleName,
                        declarationModel.Visibility,
                        structGenericParams);
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

                    if (recordDeclaration.primaryConstructorParameters() is { } primaryConstructor)
                    {
                        foreach (var parameter in primaryConstructor.parameterList().parameter())
                        {
                            var fieldType = ResolveType(parameter.type_(), genericParameters, module.SyntaxModel.ModuleName);
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

                    foreach (var field in recordDeclaration.recordBody().recordMember()
                                 .Select(static member => member.fieldDeclaration())
                                 .Where(static field => field is not null)!)
                    {
                        AddFields(fields, orderedFields, field, genericParameters, module.SyntaxModel.ModuleName, recordName, declarationModel.Visibility);
                    }

                    _namedTypes[recordName] = new NamedTypeSymbol(
                        recordName,
                        DeclarationKind.Record,
                        fields,
                        orderedFields,
                        GenericParameterNames: genericParameters?.ToList());
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
                    _namedTypes[enumName] = BuildEnumNamedType(
                        enumName,
                        enumDeclaration.enumBody().enumVariantDeclaration(),
                        genericParameters,
                        module.SyntaxModel.ModuleName);
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
                    _namedTypes[traitName] = new NamedTypeSymbol(
                        traitName,
                        DeclarationKind.Trait,
                        new Dictionary<string, FieldSymbol>(StringComparer.Ordinal),
                        []);
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
                    _namedTypes[doctrineName] = new NamedTypeSymbol(
                        doctrineName,
                        DeclarationKind.Doctrine,
                        new Dictionary<string, FieldSymbol>(StringComparer.Ordinal),
                        []);
                }
            }
        }
    }

    private NamedTypeSymbol BuildStructLikeNamedType(
        string name,
        DeclarationKind kind,
        IEnumerable<StarkParser.FieldDeclarationContext> fieldDeclarations,
        string currentModuleName,
        StarkVisibility containingVisibility,
        ISet<string>? genericParameters = null)
    {
        var fields = new Dictionary<string, FieldSymbol>(StringComparer.Ordinal);
        var orderedFields = new List<FieldSymbol>();
        var genericParameterNames = genericParameters?.ToList();
        _namedTypes[name] = new NamedTypeSymbol(
            name,
            kind,
            fields,
            orderedFields,
            GenericParameterNames: genericParameterNames);

        foreach (var field in fieldDeclarations)
        {
            AddFields(fields, orderedFields, field, genericParameters, currentModuleName, name, containingVisibility);
        }

        var namedType = new NamedTypeSymbol(name, kind, fields, orderedFields,
            GenericParameterNames: genericParameterNames);
        RefreshConcreteInstantiationsForTemplate(namedType);
        return namedType;
    }

    private NamedTypeSymbol BuildEnumNamedType(
        string name,
        IEnumerable<StarkParser.EnumVariantDeclarationContext> variantDeclarations,
        ISet<string>? genericParameters,
        string currentModuleName)
    {
        var variants = new List<EnumVariantSymbol>();
        var seenVariantNames = new HashSet<string>(StringComparer.Ordinal);

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

            var payload = variantDeclaration.enumVariantPayload();
            if (payload is null)
            {
                variants.Add(new EnumVariantSymbol(variantName, UsesNamedFields: false, Fields: []));
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
                            ResolveType(fieldDeclaration.type_(), genericParameters, currentModuleName),
                            fieldDeclaration.type_(),
                            $"enum variant field '{fieldName}' in '{name}.{variantName}'")));
                }

                variants.Add(new EnumVariantSymbol(variantName, UsesNamedFields: true, Fields: fields));
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
                            ResolveType(fieldType, genericParameters, currentModuleName),
                            fieldType,
                            $"enum variant field '{name}.{variantName}#{index}'")))
                    .ToArray()));
        }

        return new NamedTypeSymbol(
            name,
            DeclarationKind.Enum,
            new Dictionary<string, FieldSymbol>(StringComparer.Ordinal),
            [],
            EnumVariants: variants,
            GenericParameterNames: genericParameters?.ToList());
    }

    private void AddFields(
        Dictionary<string, FieldSymbol> fields,
        List<FieldSymbol> orderedFields,
        StarkParser.FieldDeclarationContext fieldDeclaration,
        ISet<string>? genericParameters,
        string currentModuleName,
        string containingTypeName,
        StarkVisibility containingVisibility)
    {
        var fieldType = ResolveType(fieldDeclaration.type_(), genericParameters, currentModuleName);
        var fieldVisibility = ResolveFieldVisibility(containingVisibility, fieldDeclaration.visibilityModifier());

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
            AddField(
                fields,
                orderedFields,
                new FieldSymbol(
                    fieldName,
                    ValidateRuntimeValueType(
                        fieldType,
                        fieldDeclaration.type_(),
                        $"field '{fieldName}' in type '{containingTypeName}'"),
                    fieldVisibility,
                    currentModuleName));
        }
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
                var previousGenericParameters = _currentFunctionGenericParameters;
                var previousFunctionModuleName = _currentFunctionModuleName;
                _currentFunctionGenericParameters = genericParameters;
                _currentFunctionModuleName = module.SyntaxModel.ModuleName;

                try
                {
                    var returnType = ResolveReturnType(functionSyntax.ReturnType, genericParameters, module.SyntaxModel.ModuleName);
                    ValidateRuntimeValueType(returnType, functionSyntax.ReturnType, $"the return type of function '{localName}'");
                    var isFfi = functionSyntax.Modifiers.Any(static modifier => string.Equals(modifier.GetText(), "ffi", StringComparison.Ordinal));
                    var isVarargs = functionSyntax.Modifiers.Any(static modifier => string.Equals(modifier.GetText(), "varargs", StringComparison.Ordinal));
                    var isAbiBoundary = isFfi
                        || declarationModel.Visibility == StarkVisibility.Export;
                    if (isAbiBoundary)
                    {
                        ValidateAbiTypeDoesNotDependOnEnum(returnType, functionSyntax.ReturnType, $"the return type of function '{localName}'");
                    }

                    var parameters = new List<TypedParameterSymbol>();
                    foreach (var parameter in functionSyntax.ParameterList.parameter())
                    {
                        var parameterType = ResolveParameterType(parameter.type_(), genericParameters, module.SyntaxModel.ModuleName, out var rawPointerElementCountExpression);
                        ValidateRuntimeValueType(parameterType, parameter.type_(), $"parameter '{parameter.Identifier().GetText()}'");
                        if (isAbiBoundary)
                        {
                            ValidateAbiTypeDoesNotDependOnEnum(parameterType, parameter, $"parameter '{parameter.Identifier().GetText()}'");
                        }

                        parameters.Add(CreateTypedParameterSymbol(parameter, parameterType, rawPointerElementCountExpression));
                    }

                    ValidateParameterContractPrefixes(functionSyntax.ParameterList.parameter());
                    ValidateBoundedRawPointerParameterCounts(functionSyntax.ParameterList.parameter(), parameters);
                    ValidateParameterDisjointContracts(functionSyntax, parameters);

                    if (declarationModel.Function?.Asm is not null)
                    {
                        ValidateAsmSignatureSurface(localName, returnType, functionSyntax.ReturnType, parameters, functionSyntax.ParameterList.parameter(), declarationModel.Function.Asm);
                    }

                    var sourceQualifiedName = QualifyName(module, localName);
                    var qualifiedName = QualifyName(module, functionSyntax.Name);
                    var signature = new TypedFunctionSignature(
                        qualifiedName,
                        returnType,
                        parameters,
                        SourceName: sourceQualifiedName,
                        GenericParameterNames: genericParameterNames.Count == 0 ? null : genericParameterNames.ToArray(),
                        IsStatic: functionSyntax.IsStatic,
                        Kind: functionSyntax.DeclaredKind,
                        IsUnsafe: functionSyntax.Modifiers.Any(static modifier => string.Equals(modifier.GetText(), "unsafe", StringComparison.Ordinal)),
                        IsVarargs: isVarargs,
                        BackendOptimizationMode: declarationModel.Function?.BackendOptimizationMode ?? ModuleBackendOptimizationMode.Default,
                        DisjointParameterGroups: declarationModel.Function?.DisjointGroups);
                    RegisterFunctionSignature(signature, seenOverloadKeys, functionSyntax.DeclarationContext);
                }
                finally
                {
                    _currentFunctionGenericParameters = previousGenericParameters;
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
        IReadOnlyList<TypedParameterSymbol> parameters)
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
        }

        foreach (var clause in GetParameterMemoryContractClauses(functionSyntax.DeclarationContext))
        {
            foreach (var contract in clause.disjointContract())
            {
                var operands = contract.expressionList().expression();
                if (operands.Length < 2)
                {
                    ReportError(
                        "STK3029",
                        "'where disjoint(...)' contracts require at least two parameter or region operands.",
                        contract);
                }

                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var operand in operands)
                {
                    if (!TryGetDisjointContractRootName(operand, out var name, out var regionStart, out var regionLength))
                    {
                        ReportError(
                            "STK3029",
                            "Disjoint contract operands must be parameter names or raw pointer regions of the form 'parameter[start, count]'.",
                            operand);
                        continue;
                    }

                    if (!parameterSymbols.TryGetValue(name, out var symbol))
                    {
                        ReportError(
                            "STK3029",
                            $"Disjoint contract references unknown parameter '{name}'.",
                            operand);
                    }
                    else if (!CanRuntimeDisjointTest(symbol.Type))
                    {
                        ReportError(
                            "STK3029",
                            $"Disjoint contract references parameter '{name}' with non-memory-backed type '{symbol.Type.DisplayName}'. Disjoint contracts require memory-backed parameters such as slices, text views, borrows, initialization views, or raw pointers.",
                            operand);
                    }
                    else if (regionStart is not null
                             && !ValidateRawPointerRegionContractOperand(name, symbol, regionStart, regionLength!, parameterSymbols, operand))
                    {
                        continue;
                    }
                    else if (!seen.Add(name))
                    {
                        ReportError(
                            "STK3029",
                            $"Disjoint contract repeats parameter '{name}'.",
                            operand);
                    }
                }
            }
        }
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

            _currentFunctionGenericParameters = genericParameters;
            _currentFunctionName = null;
            _currentFunctionModuleName = module.SyntaxModel.ModuleName;

            try
            {
                CheckBlock(constructor.block(), scope, StarkTypeSymbols.Void);
            }
            finally
            {
                _currentFunctionGenericParameters = previousGenericParameters;
                _currentFunctionName = previousFunctionName;
                _currentFunctionModuleName = previousFunctionModuleName;
            }
        }
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
                        global.BindingKind);
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
                            BindingKind: GlobalBindingKind.Const);
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
                AddParameterDisjointFacts(scope, signature.DisjointGroups);

                var previousGenericParameters = _currentFunctionGenericParameters;
                var previousFunctionName = _currentFunctionName;
                var previousFunctionModuleName = _currentFunctionModuleName;
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
                _currentFunctionGenericParameters = signature.IsGeneric
                    ? signature.GenericParams.ToHashSet(StringComparer.Ordinal)
                    : null;
                _currentFunctionName = signature.Name;
                _currentFunctionModuleName = module.SyntaxModel.ModuleName;
                if (signature.IsUnsafe)
                {
                    _unsafeDepth++;
                }
                _currentImportedTemplateObjectCreations = hasImportedTemplateSummary ? importedTemplateSummary!.ObjectCreations : null;
                _currentImportedTemplateObjectCreationOrdinals = hasImportedTemplateSummary && importedTemplateSummary!.ObjectCreations.Count > 0
                    ? CollectTrackedObjectCreationOrdinals(block)
                    : null;
                _currentImportedTemplateEnumConstructors = importedTemplateSummary?.EnumConstructors.ToDictionary(
                    static enumConstructor => enumConstructor.Ordinal,
                    static enumConstructor => enumConstructor);
                _currentImportedTemplateEnumConstructorOrdinals = importedTemplateSummary is { EnumConstructors.Count: > 0 }
                    ? CollectTemplateEnumConstructorOrdinals(block)
                    : null;
                _currentImportedTemplateEnumCalls = importedTemplateSummary?.EnumCalls.ToDictionary(
                    static enumCall => enumCall.Ordinal,
                    static enumCall => enumCall);
                _currentImportedTemplateEnumCallOrdinals = importedTemplateSummary is { EnumCalls.Count: > 0 }
                    ? CollectTemplateDirectCallOrdinals(block)
                    : null;
                _currentImportedTemplateEnumValues = importedTemplateSummary?.EnumValues.ToDictionary(
                    static enumValue => enumValue.Ordinal,
                    static enumValue => enumValue);
                _currentImportedTemplateEnumValueOrdinals = importedTemplateSummary is { EnumValues.Count: > 0 }
                    ? CollectTemplateEnumValueOrdinals(block)
                    : null;
                _currentImportedTemplateEnumPatterns = importedTemplateSummary?.EnumPatterns.ToDictionary(
                    static enumPattern => enumPattern.Ordinal,
                    static enumPattern => enumPattern);
                _currentImportedTemplateAggregatePatterns = importedTemplateSummary?.AggregatePatterns.ToDictionary(
                    static aggregatePattern => aggregatePattern.Ordinal,
                    static aggregatePattern => aggregatePattern);
                _currentImportedTemplateEnumPatternOrdinals = importedTemplateSummary is { EnumPatterns.Count: > 0 }
                    || importedTemplateSummary is { AggregatePatterns.Count: > 0 }
                    ? CollectTemplateEnumPatternOrdinals(block)
                    : null;
                // Local declaration facts are keyed to the source coordinates that were
                // present when the package image was produced. Rendered package bodies
                // are parsed from the package image surface, so those coordinates can
                // point at unrelated declarations. Explicit source types are safer here.
                _currentImportedTemplateLocalDeclarations = null;
                _currentImportedTemplateConversions = hasImportedTemplateSummary
                    ? importedTemplateSummary!.Conversions.ToDictionary(
                        static conversion => conversion.Ordinal,
                        static conversion => conversion.TargetType)
                    : null;
                _currentImportedTemplateConversionOrdinals = hasImportedTemplateSummary && importedTemplateSummary!.Conversions.Count > 0
                    ? CollectTemplateConversionOrdinals(block)
                    : null;
                _currentImportedTemplateDirectCalls = importedTemplateSummary?.DirectCalls.ToDictionary(
                    static call => call.Ordinal,
                    static call => call.Signature);
                _currentImportedTemplateDirectCallOrdinals = importedTemplateSummary is { DirectCalls.Count: > 0 }
                    ? CollectTemplateDirectCallOrdinals(block)
                    : null;
                _currentImportedTemplateFieldAccesses = importedTemplateSummary?.FieldAccesses.ToDictionary(
                    static access => access.Ordinal,
                    static access => access);
                _currentImportedTemplateFieldAccessOrdinals = importedTemplateSummary is { FieldAccesses.Count: > 0 }
                    ? CollectTemplateFieldAccessOrdinals(block)
                    : null;
                _currentImportedTemplateMemberCalls = importedTemplateSummary?.MemberCalls.ToDictionary(
                    static call => call.Ordinal,
                    static call => call.Signature);
                _currentImportedTemplateMemberCallOrdinals = importedTemplateSummary is { MemberCalls.Count: > 0 }
                    ? CollectTemplateMemberCallOrdinals(block)
                    : null;

                try
                {
                    CheckBlock(block, scope, signature.ReturnType);
                }
                finally
                {
                    _currentFunctionGenericParameters = previousGenericParameters;
                    _currentFunctionName = previousFunctionName;
                    _currentFunctionModuleName = previousFunctionModuleName;
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

    private void CheckBlock(StarkParser.BlockContext block, Scope parentScope, StarkTypeSymbol returnType)
    {
        var scope = new Scope(parentScope);
        foreach (var statement in block.statement())
        {
            CheckStatement(statement, scope, returnType);
        }
    }

    private void CheckStatement(StarkParser.StatementContext statement, Scope scope, StarkTypeSymbol returnType)
    {
        if (statement.block() is { } block)
        {
            CheckBlock(block, scope, returnType);
            return;
        }

        if (statement.unsafeStatement() is { } unsafeStatement)
        {
            _unsafeDepth++;
            try
            {
                CheckBlock(unsafeStatement.block(), scope, returnType);
            }
            finally
            {
                _unsafeDepth--;
            }

            return;
        }

        if (statement.localConstantDeclaration() is { } localConstant)
        {
            StarkTypeSymbol? recordedDeclarationType = null;
            foreach (var declarator in localConstant.constantDeclarators().constantDeclarator())
            {
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
                    RecordLocalDeclarationType(TemplateLocalDeclarationFacts.ConstantKind, declaredType, localConstant);
                    recordedDeclarationType = declaredType;
                }
                else if (localConstant.type_() is null && recordedDeclarationType != declaredType)
                {
                    ReportError(
                        "STK3002",
                        "Grouped inferred local constants must infer the same type. Split them into separate const declarations.",
                        declarator);
                }

                scope.Declare(new VariableSymbol(
                    declarator.Identifier().GetText(),
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
            if (ifStatement.expression() is { } condition)
            {
                EnsureBoolean(EvaluateExpression(condition, scope, allowFunctionReference: false).Type, condition, "if conditions must be of type 'bool'");
            }
            else if (ifStatement.disjointRuntimeCondition() is { } disjointCondition)
            {
                trueBranchDisjointRoots = CheckDisjointRuntimeCondition(disjointCondition, scope);
            }

            var thenScope = new Scope(scope);
            if (trueBranchDisjointRoots is { Count: >= 2 })
            {
                thenScope.AddDisjointFact(trueBranchDisjointRoots);
            }

            CheckStatement(ifStatement.statement(0), thenScope, returnType);
            if (ifStatement.statement().Length > 1)
            {
                CheckStatement(ifStatement.statement(1), new Scope(scope), returnType);
            }

            return;
        }

        if (statement.switchStatement() is { } switchStatement)
        {
            var switchType = EvaluateExpression(switchStatement.expression(), scope, allowFunctionReference: false).Type;
            ValidateImplementedSwitchShape(switchStatement, switchType);

            foreach (var section in switchStatement.switchSection())
            {
                var sectionScope = new Scope(scope);

                foreach (var label in section.switchLabel())
                {
                    if (label.pattern() is { } pattern)
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
            }

            return;
        }

        if (statement.whileStatement() is { } whileStatement)
        {
            EnsureBoolean(EvaluateExpression(whileStatement.expression(), scope, allowFunctionReference: false).Type, whileStatement.expression(), "while conditions must be of type 'bool'");
            CheckLoopContracts(
                whileStatement.loopContract(),
                whileStatement.statement(),
                scope,
                condition: whileStatement.expression());
            CheckStatement(whileStatement.statement(), new Scope(scope), returnType);
            return;
        }

        if (statement.forStatement() is { } forStatement)
        {
            var loopScope = new Scope(scope);

            if (forStatement.forInitializer()?.localForVariableDeclaration() is { } localForVariableDeclaration)
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

            if (forStatement.forCondition() is { } condition)
            {
                EnsureBoolean(EvaluateExpression(condition.expression(), loopScope, allowFunctionReference: false).Type, condition.expression(), "for conditions must be of type 'bool'");
            }

            if (forStatement.forIterator() is { } iterator)
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
                condition: forStatement.forCondition()?.expression(),
                iteratorExpressions: forStatement.forIterator()?.expressionList().expression());

            CheckStatement(forStatement.statement(), loopScope, returnType);
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
        return type.Kind is StarkTypeKind.RawPointer or StarkTypeKind.Slice or StarkTypeKind.Ascii or StarkTypeKind.Unicode
            || type.BorrowKind != StarkBorrowKind.None
            || type.InitializationKind != StarkInitializationKind.None;
    }

    private void ValidateImplementedSwitchShape(StarkParser.SwitchStatementContext switchStatement, StarkTypeSymbol switchType)
    {
        if (!CanLowerImplementedSwitchType(switchType))
        {
            ReportError(
                "STK3008",
                $"Switch over '{switchType.DisplayName}' is not implemented in the current compiler yet. The current switch subset supports integers, floating-point values, bool, raw pointers, and text literals over ascii/unicode; richer non-text domains remain out of scope for now.",
                switchStatement.expression());
            return;
        }

        foreach (var section in switchStatement.switchSection())
        {
            var captureLabels = 0;
            var aggregateLabels = 0;
            var labelCount = section.switchLabel().Length;

            foreach (var label in section.switchLabel())
            {
                var pattern = label.pattern();
                if (pattern?.VAR() is not null)
                {
                    captureLabels++;
                }

                if (pattern?.aggregatePattern() is not null || pattern?.enumNamedFieldPattern() is not null)
                {
                    aggregateLabels++;
                }
            }

            if (aggregateLabels > 0 && labelCount != 1)
            {
                ReportError(
                    "STK3008",
                    "Switch aggregate patterns must currently appear as the only label in their section.",
                    section);
            }
            else if (captureLabels > 0 && labelCount != 1)
            {
                ReportError(
                    "STK3008",
                    "Switch capture patterns must currently appear as the only label in their section.",
                    section);
            }
        }

        AnalyzeSwitchCoverage(switchStatement, switchType);
    }

    private void AnalyzeSwitchCoverage(StarkParser.SwitchStatementContext switchStatement, StarkTypeSymbol switchType)
    {
        var coveragePatterns = new List<SwitchCoveragePattern>();
        SwitchCoveragePattern? exhaustivePattern = null;
        var boolTrueCovered = false;
        var boolFalseCovered = false;
        var exhaustiveEnumVariants = new HashSet<string>(StringComparer.Ordinal);
        var enumVariantCount = 0;

        if (switchType.Kind == StarkTypeKind.Named
            && switchType.NamedType is not null
            && _namedTypes.TryGetValue(switchType.NamedType, out var switchNamedType)
            && switchNamedType.Kind == DeclarationKind.Enum)
        {
            enumVariantCount = switchNamedType.Variants.Count;
        }

        foreach (var section in switchStatement.switchSection())
        {
            foreach (var label in section.switchLabel())
            {
                var labelText = DescribeSwitchLabel(label);
                _ = TryCreateSwitchCoveragePattern(label, switchType, out var currentPattern);

                if (exhaustivePattern is not null)
                {
                    ReportUnreachableSwitchLabel(label, labelText, exhaustivePattern, switchType, becauseExhaustive: true);
                    continue;
                }

                if (currentPattern is not null)
                {
                    var coveringPattern = coveragePatterns.FirstOrDefault(existing => Covers(existing, currentPattern));
                    if (coveringPattern is not null)
                    {
                        ReportUnreachableSwitchLabel(label, labelText, coveringPattern, switchType, becauseExhaustive: false);
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
            }
        }
    }

    private bool TryCreateSwitchCoveragePattern(
        StarkParser.SwitchLabelContext label,
        StarkTypeSymbol switchType,
        out SwitchCoveragePattern? pattern)
    {
        if (label.DEFAULT() is not null)
        {
            pattern = new SwitchCoveragePattern(
                SwitchCoveragePatternKind.MatchAll,
                "default",
                label,
                LiteralKey: null,
                AggregatePattern: null,
                EnumPattern: null);
            return true;
        }

        var switchPattern = label.pattern();
        if (switchPattern is null)
        {
            pattern = null;
            return false;
        }

        if (switchPattern.DISCARD() is not null || switchPattern.VAR() is not null)
        {
            pattern = new SwitchCoveragePattern(
                SwitchCoveragePatternKind.MatchAll,
                switchPattern.GetText(),
                label,
                LiteralKey: null,
                AggregatePattern: null,
                EnumPattern: null);
            return true;
        }

        if (switchPattern.literal() is { } literal
            && TryCreateLiteralCoverageKey(literal, switchType, out var literalKey))
        {
            pattern = new SwitchCoveragePattern(
                SwitchCoveragePatternKind.Literal,
                literal.GetText(),
                label,
                literalKey,
                AggregatePattern: null,
                EnumPattern: null);
            return true;
        }

        if (switchPattern.enumNamedFieldPattern() is { } enumNamedFieldPattern
            && TryCreateEnumNamedFieldCoveragePattern(enumNamedFieldPattern, switchType, out var enumNamedFieldCoverage))
        {
            pattern = new SwitchCoveragePattern(
                SwitchCoveragePatternKind.EnumCase,
                enumNamedFieldPattern.GetText(),
                label,
                LiteralKey: null,
                AggregatePattern: null,
                EnumPattern: enumNamedFieldCoverage);
            return true;
        }

        if (switchPattern.genericEnumAggregatePattern() is { } genericEnumAggregatePattern
            && TryCreateEnumAggregateCoveragePattern(genericEnumAggregatePattern, switchType, out var genericEnumAggregateCoverage))
        {
            pattern = new SwitchCoveragePattern(
                SwitchCoveragePatternKind.EnumCase,
                genericEnumAggregatePattern.GetText(),
                label,
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
                label,
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
                label,
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

        var members = enumNamedFieldPattern.enumNamedFieldPatternPayload().namedPatternMember();
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

        if (pattern.literal() is { } literal
            && SupportsAggregateFieldSubpattern(fieldType)
            && TryCreateLiteralCoverageKey(literal, fieldType, out var literalKey))
        {
            coverageField = new AggregateCoverageField(
                AggregateCoverageFieldKind.Literal,
                literalKey,
                NestedAggregatePattern: null,
                NestedEnumPattern: null);
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
        StarkParser.SwitchLabelContext label,
        string labelText,
        SwitchCoveragePattern coveringPattern,
        StarkTypeSymbol switchType,
        bool becauseExhaustive)
    {
        var message = becauseExhaustive
            ? $"Switch label '{labelText}' is unreachable because the switch is already exhaustive after the earlier unguarded label '{coveringPattern.LabelText}'."
            : $"Switch label '{labelText}' is unreachable because the earlier unguarded label '{coveringPattern.LabelText}' already covers it.";
        ReportError("STK3019", message, label);

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

        return existing.NestedEnumPattern is not null
            && current.NestedEnumPattern is not null
            && Covers(existing.NestedEnumPattern, current.NestedEnumPattern);
    }

    private static bool IsMatchAllAggregatePattern(AggregateCoveragePattern pattern)
    {
        return pattern.Fields.All(static field => field.Kind == AggregateCoverageFieldKind.Wildcard);
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

        return label.pattern()?.GetText() ?? label.GetText();
    }

    private void BindPattern(StarkParser.PatternContext pattern, StarkTypeSymbol switchType, Scope scope)
    {
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

        if (pattern.VAR() is not null)
        {
            if (IsEnumSwitchType(switchType))
            {
                ReportError(
                    "STK3008",
                    $"Switch over enum '{switchType.DisplayName}' currently supports case patterns, '_', and 'default'. Whole-value capture patterns remain unsupported for enum switch values.",
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

        var members = enumNamedFieldPattern.enumNamedFieldPatternPayload().namedPatternMember();
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
        RecordLocalDeclarationType(declarationKind, declaredType, declarationContext);
        var storageClass = GetLocalDeclarationStorageClass(declarationContext);

        foreach (var declarator in declarators)
        {
            var hasFixedTextStorage = TryValidateFixedTextStorageCapacity(
                declarator,
                declaredType,
                storageClass,
                scope,
                out _);
            if (declarator.variableInitializer() is null)
            {
                if (hasFixedTextStorage)
                {
                    ReportError(
                        "STK3002",
                        $"Fixed text buffer '{declarator.Identifier().GetText()}' needs an initializer, for example `left + right`.",
                        declarator);
                }

                scope.Declare(new VariableSymbol(declarator.Identifier().GetText(), declaredType, IsMutable: isMutable, IsConstant: false));
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
            var provenance = TryCreateImmutableLocalMemoryProvenance(declaredType, isMutable, initializerBinding, scope);
            var hasConstProvenance = !isMutable
                && initializerBinding is not null
                && HasConstProvenance(initializerBinding);
            scope.Declare(new VariableSymbol(
                declarator.Identifier().GetText(),
                declaredType,
                IsMutable: isMutable,
                IsConstant: false,
                HasConstProvenance: hasConstProvenance,
                MemoryRootKey: provenance?.RootKey,
                MemoryRootIsIndependentStorage: provenance?.IsIndependentStorage == true,
                RawPointerElementCountExpression: provenance?.RawPointerElementCountExpression));
        }
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
            ValidateRuntimeTypeDoesNotDependOnEnum(declaredType, typeContext, usage);

            if (validateInitializer)
            {
                CheckVariableInitializer(declarator.variableInitializer(), declaredType, scope);
            }

            if (TryInferCompileTimeConstantStorageType(declarator.variableInitializer(), out var constantType, out var constant))
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
        if (!TryInferCompileTimeConstantStorageType(declarator.variableInitializer(), out var constantType, out var constant)
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
                ReportConstIntegerDemotionIfNeeded(declarator, declaredType, unsignedConstantType, integerTypeToken);
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
        ReportConstIntegerDemotionIfNeeded(declarator, declaredType, resolvedConstantType, integerTypeToken);
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
        if (TryInferCompileTimeConstantStorageType(initializer, out var constantType))
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

    private static bool TryInferCompileTimeConstantStorageType(
        StarkParser.VariableInitializerContext initializer,
        out StarkTypeSymbol type)
    {
        return TryInferCompileTimeConstantStorageType(initializer, out type, out _);
    }

    private static bool TryInferCompileTimeConstantStorageType(
        StarkParser.VariableInitializerContext initializer,
        out StarkTypeSymbol type,
        out CompileTimeConstant constant)
    {
        type = StarkTypeSymbols.Error;
        constant = default;

        if (initializer.expression() is not { } expression
            || !CompileTimeExpressionEvaluator.TryEvaluate(expression, out constant))
        {
            return false;
        }

        type = constant.Kind switch
        {
            CompileTimeConstantKind.Integer => InferConstIntegerStorageType(constant.IntegerValue),
            CompileTimeConstantKind.Float => constant.Type,
            CompileTimeConstantKind.Bool => StarkTypeSymbols.Bool,
            CompileTimeConstantKind.Text => constant.Type,
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
        if (initializer.expression() is not { } expression
            || !CompileTimeExpressionEvaluator.TryEvaluate(
                expression,
                out constant,
                CreateCompileTimeEvaluationServices(scope)))
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

    private CompileTimeEvaluationServices CreateCompileTimeEvaluationServices(Scope scope)
    {
        return new CompileTimeEvaluationServices(
            TryResolveIdentifier: (string name, out CompileTimeConstant constant) =>
                TryResolveCompileTimeConstant(scope, name, out constant));
    }

    private static bool TryResolveCompileTimeConstant(
        Scope scope,
        string name,
        out CompileTimeConstant constant)
    {
        if (scope.TryLookup(name, out var symbol)
            && symbol.BindingKind is null
            && symbol.ConstantValue is { } value)
        {
            constant = value;
            return true;
        }

        constant = default;
        return false;
    }

    private static StarkTypeSymbol InferConstIntegerStorageType(BigInteger value)
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

        return StarkTypeSymbols.Integer(SupportedIntegerLiteralWidths[^1], value, value);
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

        return StarkTypeSymbols.Integer(SupportedIntegerLiteralWidths[^1], value, value, isUnsigned: true);
    }

    private static StarkTypeSymbol ResolveConstIntegerStorageType(IToken integerTypeToken)
    {
        var text = integerTypeToken.Text;
        var isUnsigned = text[0] == 'u';
        var width = int.Parse(text[1..], CultureInfo.InvariantCulture);
        GetIntegerTypeBounds(width, isUnsigned, out var min, out var max);
        return StarkTypeSymbols.Integer(width, min, max, isUnsigned);
    }

    private static void GetIntegerTypeBounds(int width, bool isUnsigned, out BigInteger min, out BigInteger max)
    {
        if (isUnsigned)
        {
            min = BigInteger.Zero;
            max = (BigInteger.One << width) - BigInteger.One;
            return;
        }

        min = -(BigInteger.One << (width - 1));
        max = (BigInteger.One << (width - 1)) - BigInteger.One;
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
        ParserRuleContext declarationContext)
    {
        _localDeclarations.Add(new LocalDeclarationTypingRecord(
            declarationKind,
            type,
            Location(declarationContext),
            _currentFunctionName));
    }

    private void RecordEnumConstructor(
        StarkTypeSymbol enumType,
        string variantName,
        ParserRuleContext constructorContext,
        IReadOnlyList<EnumConstructorMemberTypingRecord>? members = null)
    {
        _enumConstructors.Add(new EnumConstructorTypingRecord(
            enumType,
            variantName,
            Location(constructorContext),
            _currentFunctionName,
            members));
    }

    private void RecordEnumCall(
        StarkTypeSymbol enumType,
        string variantName,
        ParserRuleContext callContext)
    {
        _enumCalls.Add(new EnumCallTypingRecord(
            enumType,
            variantName,
            Location(callContext),
            _currentFunctionName));
    }

    private void RecordEnumValue(
        StarkTypeSymbol enumType,
        string variantName,
        IToken token)
    {
        _enumValues.Add(new EnumValueTypingRecord(
            enumType,
            variantName,
            Location(token),
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
        ParserRuleContext context)
    {
        _aggregatePatterns.Add(new AggregatePatternTypingRecord(
            type,
            Location(context),
            _currentFunctionName));
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
        out ExpressionBinding binding)
    {
        binding = default!;

        if (_currentImportedTemplateDirectCalls is not { Count: > 0 }
            || expression.postfixPart().Length == 0
            || expression.postfixPart()[0].argumentList() is not { } firstArgumentList
            || _currentImportedTemplateDirectCallOrdinals is not { } directCallOrdinals
            || !directCallOrdinals.TryGetValue(firstArgumentList, out var directCallOrdinal)
            || !_currentImportedTemplateDirectCalls.TryGetValue(directCallOrdinal, out var publishedSignature))
        {
            return false;
        }

        var resolvedSignature = CacheFunctionInstantiation(publishedSignature);
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
            MemoryRootIsIndependentStorage: target.MemoryRootIsIndependentStorage);
        return true;
    }

    private bool TryGetPublishedTemplateMemberCallBinding(
        ExpressionBinding receiver,
        StarkParser.ArgumentListContext arguments,
        out ExpressionBinding binding)
    {
        binding = default!;

        if (receiver.NamespaceName is not null
            || _currentImportedTemplateMemberCalls is not { Count: > 0 }
            || _currentImportedTemplateMemberCallOrdinals is not { } memberCallOrdinals
            || !memberCallOrdinals.TryGetValue(arguments, out var memberCallOrdinal)
            || !_currentImportedTemplateMemberCalls.TryGetValue(memberCallOrdinal, out var publishedSignature))
        {
            return false;
        }

        var resolvedSignature = CacheFunctionInstantiation(publishedSignature);
        binding = new ExpressionBinding(
            resolvedSignature.ReturnType,
            NamedType: ResolveNamedTypeSymbol(resolvedSignature.ReturnType),
            Function: resolvedSignature,
            DiagnosticName: $"method '{resolvedSignature.DisplaySourceName}'",
            Receiver: receiver);
        return true;
    }

    private void RecordDirectCall(
        TypedFunctionSignature signature,
        ParserRuleContext callContext)
    {
        _directCalls.Add(new DirectCallTypingRecord(
            signature,
            Location(callContext),
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
        ParserRuleContext callContext)
    {
        _memberCalls.Add(new MemberCallTypingRecord(
            signature,
            Location(callContext),
            _currentFunctionName));
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

    private static LocalMemoryProvenance? TryCreateImmutableLocalMemoryProvenance(
        StarkTypeSymbol declaredType,
        bool isMutable,
        ExpressionBinding? initializerBinding,
        Scope scope)
    {
        if (isMutable
            || initializerBinding?.MemoryRootKey is not { Length: > 0 } rootKey
            || !CanPreserveImmutableLocalMemoryProvenance(declaredType, initializerBinding.Type))
        {
            return null;
        }

        return new LocalMemoryProvenance(
            rootKey,
            initializerBinding.MemoryRootIsIndependentStorage,
            TryGetProvenancePreservingRawPointerCountExpression(rootKey, declaredType, initializerBinding.Type, scope));
    }

    private static bool CanPreserveImmutableLocalMemoryProvenance(
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
        IReadOnlyList<ImportedTemplateObjectInitializerMemberSummary>? publishedMembers = null)
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

        return new ExpressionBinding(resultType);
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
                    TextLiteralKind: convertedOperand.TextLiteralKind);
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
        else
        {
            var requiresCallableTarget = postfixParts.Any(static part => part.argumentList() is not null);
            binding = TryGetPublishedTemplateEnumCallBinding(expression, out var publishedEnumCall)
                ? publishedEnumCall
                : TryGetPublishedTemplateDirectCallBinding(expression, out var publishedBinding)
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
                && TryGetPublishedTemplateMemberCallBinding(binding, memberArguments, out var publishedMemberCall))
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

        return binding;
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

        if (expression.SIZEOF() is not null || expression.ALIGNOF() is not null)
        {
            var kind = expression.SIZEOF() is not null ? "sizeof" : "alignof";
            var targetType = ResolveType(
                expression.type_(),
                _currentFunctionGenericParameters,
                _currentFunctionModuleName);
            _typeLayoutExpressions.Add(new TypeLayoutExpressionTypingRecord(
                kind,
                targetType,
                Location(expression),
                _currentFunctionName));

            var resultType = expression.ALIGNOF() is not null
                ? StarkTypeSymbols.Integer(64, BigInteger.One, new BigInteger(long.MaxValue))
                : StarkTypeSymbols.Integer(64, BigInteger.Zero, new BigInteger(long.MaxValue));

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
            return ResolveGenericEnumCaseReferenceValue(genericEnumCaseReference, allowFunctionReference);
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
                $"Object creation for enum '{namedType.Name}' is not implemented in the current compiler yet. Enum constructors and runtime layout remain undefined.",
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

        if (expression.objectInitializer() is { } objectInitializer)
        {
            initializerMembers = CheckObjectInitializer(
                objectInitializer,
                createdType,
                scope,
                matchedConstructor?.InitializedMembers,
                publishedObjectCreation?.InitializerMembers);
        }

        if (ShouldTrackObjectCreation(expression) || matchedConstructor is not null)
        {
            _objectCreations.Add(new ObjectCreationTypingRecord(
                expression.GetText(),
                createdType,
                matchedConstructor is null
                    ? null
                    : new TypedConstructorShape(
                        createdType.DisplayName,
                        matchedConstructor.Parameters,
                        matchedConstructor.IsPrimaryShape,
                        matchedConstructor.BodyKey),
                Location(expression.Start),
                _currentFunctionName,
                initializerMembers));
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

        if (expectedType?.Kind != StarkTypeKind.FunctionPointer)
        {
            ReportError(
                "STK3008",
                "Lambda expressions require an explicit function-pointer target type in this compiler slice.",
                expression);
            return new ExpressionBinding(StarkTypeSymbols.Error, DiagnosticName: "lambda");
        }

        var parameterTypes = expectedType.FunctionPointerParameterTypes ?? [];
        var returnType = expectedType.FunctionPointerReturnType ?? StarkTypeSymbols.Error;
        var lambdaScope = Scope.CreateRoot(_globals);
        foreach (var captureBinding in captureBindings)
        {
            var capturedLocal = captureBinding.Symbol;
            lambdaScope.Declare(new VariableSymbol(
                capturedLocal.Name,
                GetLambdaCaptureBodyType(capturedLocal.Type, captureBinding.Mode),
                IsMutable: CaptureModeExposesWritableBinding(captureBinding.Mode),
                IsConstant: false));
        }

        var lambdaParameters = expression.lambdaParameterList().parameter();
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
            parameterNames.Add(parameter.Identifier().GetText());
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

            lambdaScope.Declare(new VariableSymbol(parameter.Identifier().GetText(), parameterType, IsMutable: false, IsConstant: false));
        }

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
        }

        if (captureBindings.Count > 0)
        {
            ReportError(
                "STK3008",
                "Capturing lambdas are parsed and capture-checked, but closure environment lowering is not implemented yet. Use a named function item promoted to 'fnptr<...>' for callable values in this compiler slice.",
                (ParserRuleContext?)expression.captureClause() ?? expression);
            return new ExpressionBinding(expectedType, DiagnosticName: "lambda");
        }

        if (TypeContainsOpenCurrentFunctionGenericParameter(expectedType))
        {
            ReportError(
                "STK3008",
                "Non-capturing lambda lowering for open generic function-pointer targets is not implemented yet. Use a named generic function item for this callable value.",
                expression);
            return new ExpressionBinding(expectedType, DiagnosticName: "lambda");
        }

        if (!parametersExactlyMatchTarget)
        {
            ReportError(
                "STK3002",
                $"Lowered non-capturing lambdas require parameter annotations to exactly match target '{expectedType.DisplayName}' so the generated function pointer has an exact ABI signature.",
                expression.lambdaParameterList());
            return new ExpressionBinding(expectedType, DiagnosticName: "lambda");
        }

        if (lambdaParameters.Length == parameterTypes.Count && _currentFunctionName is { } enclosingFunctionName)
        {
            var lambda = new LambdaTypingRecord(
                CallableValueFacts.BuildLambdaFunctionName(enclosingFunctionName, lambdaLocation),
                expectedType,
                parameterNames,
                lambdaLocation,
                enclosingFunctionName);
            _lambdas.Add(lambda);
            _functions.TryAdd(lambda.FunctionName, CallableValueFacts.BuildLambdaSignature(lambda));
        }

        return new ExpressionBinding(expectedType, DiagnosticName: "lambda");
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

                capturedLocals.Add(new LambdaCaptureBinding(capturedLocal, mode));
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
        return string.Equals(mode, "mut", StringComparison.Ordinal)
            || string.Equals(mode, "out", StringComparison.Ordinal)
            || string.Equals(mode, "init", StringComparison.Ordinal);
    }

    private static bool CaptureModeExposesWritableBinding(string mode)
    {
        return RequiresWritableCaptureTarget(mode);
    }

    private static StarkTypeSymbol GetLambdaCaptureBodyType(StarkTypeSymbol type, string mode)
    {
        if (string.Equals(mode, "addr", StringComparison.Ordinal))
        {
            return StarkTypeSymbols.RawPointer(StarkTypeSymbols.FreezeAddressPointeeType(type), isMutable: false);
        }

        if (string.Equals(mode, "shared", StringComparison.Ordinal))
        {
            return StarkTypeSymbols.WithQualifiers(type, accessKind: StarkAccessKind.Shared, isMutableView: false);
        }

        if (string.Equals(mode, "out", StringComparison.Ordinal))
        {
            return StarkTypeSymbols.WithQualifiers(type, initializationKind: StarkInitializationKind.Out);
        }

        if (string.Equals(mode, "init", StringComparison.Ordinal))
        {
            return StarkTypeSymbols.WithQualifiers(type, initializationKind: StarkInitializationKind.Init);
        }

        return type;
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

        StarkTypeSymbol[]? argumentTypes = null;
        ExpressionBinding[]? argumentBindings = null;

        if (target.OverloadSourceName is { } overloadSourceName)
        {
            if (!TryGetFunctionOverloads(overloadSourceName, out var overloads))
            {
                ReportError("STK3008", $"{DescribeExpressionTarget(target)} is not callable.", arguments);
                return new ExpressionBinding(StarkTypeSymbols.Error);
            }

            argumentBindings = EvaluateArguments(arguments, expectedParameters: null, scope);
            argumentTypes = argumentBindings.Select(static argument => argument.Type).ToArray();
            var resolution = FunctionOverloadFacts.Resolve(overloads, target.Receiver?.Type, argumentTypes, CanAssign);
            if (!resolution.Succeeded)
            {
                ReportOverloadResolutionFailure(overloadSourceName, argumentTypes, resolution, arguments);
                return new ExpressionBinding(StarkTypeSymbols.Error);
            }

            var resolvedFunction = CacheFunctionInstantiation(resolution.Match!);
            target = target with
            {
                Function = resolvedFunction,
                OverloadSourceName = null,
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
        }

        if (target.Receiver is null)
        {
            RecordDirectCall(target.Function, arguments);
        }
        else
        {
            RecordMemberCall(target.Function, arguments);
        }

        var returnType = target.Function.ReturnType;
        if (returnType.BorrowKind != StarkBorrowKind.None)
        {
            var valueType = StarkTypeSymbols.BorrowReturnValueType(returnType);
            var isPointerBacked = StarkTypeSymbols.IsPointerBackedBorrowReturn(returnType);
            return new ExpressionBinding(
                valueType,
                IsAssignable: isPointerBacked && returnType.IsMutableView,
                NamedType: ResolveNamedTypeSymbol(valueType),
                DiagnosticName: $"call to '{target.Function.DisplaySourceName}'",
                IsAddressable: true,
                IsAddressMutable: returnType.IsMutableView);
        }

        return new ExpressionBinding(returnType, NamedType: ResolveNamedTypeSymbol(returnType), DiagnosticName: $"call to '{target.Function.DisplaySourceName}'");
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

            if (_unsafeDepth != 0 || countMax <= BigInteger.Zero)
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
                    out var reason))
            {
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
        out string reason)
    {
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
        if (function.DisjointGroups.Count == 0)
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
                TryGetMemoryArgumentRoot(argumentBindings[argumentIndex], expression, scope, out var root)
                || TryGetMemoryArgumentRoot(expression, argumentBindings[argumentIndex].Type, scope, out root)
                ? new DisjointMemoryArgument(expression, root)
                : new DisjointMemoryArgument(expression, null);
        }

        foreach (var group in function.DisjointGroups)
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
                        if (_unsafeDepth == 0)
                        {
                            ReportError(
                                "STK3030",
                                $"Call to '{displayFunctionName}' violates disjoint parameter contract: parameters '{parameterNames[leftIndex]}' and '{parameterNames[rightIndex]}' require a compiler-visible non-overlap proof, but one or both arguments do not have a statically identifiable memory root.",
                                right.Expression);
                        }

                        continue;
                    }

                    if (!DisjointCallArgumentsMayOverlap(
                            leftRoot,
                            rightRoot,
                            scope,
                            requireProof: _unsafeDepth == 0,
                            out var overlapRootKey))
                    {
                        continue;
                    }

                    ReportError(
                        "STK3030",
                        $"Call to '{displayFunctionName}' violates disjoint parameter contract: parameters '{parameterNames[leftIndex]}' and '{parameterNames[rightIndex]}' may receive overlapping memory rooted at '{overlapRootKey}'.",
                        rightRoot.Expression);
                }
            }
        }
    }

    private static void AddParameterDisjointFacts(Scope scope, IReadOnlyList<ParameterDisjointGroup> disjointGroups)
    {
        foreach (var group in disjointGroups)
        {
            scope.AddDisjointFact(group.ParameterNames);
        }
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
        root = default;
        if (binding.MemoryRootKey is not { Length: > 0 } rootKey)
        {
            return false;
        }

        return TryCreateMemoryArgumentRoot(
            rootKey,
            diagnosticContext,
            binding.Type,
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
            && HasProvenIndependentStorageRoot(left)
            && HasProvenIndependentStorageRoot(right);
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
        var startText = NormalizeExpressionText(startExpression.GetText());
        var lengthText = NormalizeExpressionText(lengthExpression.GetText());
        if (!TryResolveMemoryRootIndexRange(startText, scope, out var startMin, out var startMax)
            || !TryResolveMemoryRootIndexRange(lengthText, scope, out _, out var lengthMax)
            || startMin < BigInteger.Zero
            || lengthMax <= BigInteger.Zero)
        {
            return null;
        }

        var rangeMax = startMax + lengthMax - BigInteger.One;
        return rangeMax >= startMin
            ? $"{rootKey}[{startMin.ToString(CultureInfo.InvariantCulture)}..{rangeMax.ToString(CultureInfo.InvariantCulture)}]"
            : null;
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

    private ExpressionBinding InvokeIndirectCall(ExpressionBinding target, StarkParser.ArgumentListContext arguments, Scope scope)
    {
        if (target.Type.FunctionPointerReturnType is not { } returnType
            || target.Type.FunctionPointerParameterTypes is not { } parameterTypes)
        {
            ReportError("STK3008", $"{DescribeExpressionTarget(target)} is not callable.", arguments);
            return new ExpressionBinding(StarkTypeSymbols.Error);
        }

        var expectedParameters = parameterTypes
            .Select((parameterType, index) => new TypedParameterSymbol($"arg{index}", parameterType))
            .ToArray();
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
                target.DiagnosticName ?? "function pointer",
                index + 1,
                expectedParameters[index],
                argumentBindings[index],
                arguments.argument(index).expression());
        }

        _indirectCalls.Add(new IndirectCallTypingRecord(target.Type, Location(arguments), _currentFunctionName));

        if (returnType.BorrowKind != StarkBorrowKind.None)
        {
            var valueType = StarkTypeSymbols.BorrowReturnValueType(returnType);
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
        if (target.Type.Kind is StarkTypeKind.Ascii or StarkTypeKind.Unicode)
        {
            var indexExpressions = indexes.expression();
            if (indexExpressions.Length == 0)
            {
                return new ExpressionBinding(
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

                return new ExpressionBinding(
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

            return new ExpressionBinding(
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
                currentIsAddressMutable = currentType.Kind == StarkTypeKind.Slice
                    ? currentIsAddressMutable
                        && currentType.IsMutableView
                        && currentType.AccessKind != StarkAccessKind.Frozen
                    : currentIsAddressMutable
                        && currentType.AccessKind != StarkAccessKind.Frozen;
                currentType = currentUsesFrozenProjectionSemantics
                    ? StarkTypeSymbols.FreezeReachableView(currentType.ElementType)
                    : ProjectFrozenView(currentType, currentType.ElementType);
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

        return new ExpressionBinding(
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
            MemoryRootIsIndependentStorage: target.MemoryRootIsIndependentStorage);
    }

    private ExpressionBinding ApplyMemberAccess(ExpressionBinding target, string memberName, ParserRuleContext context)
    {
        if (target.NamespaceName is not null)
        {
            var qualifiedName = $"{target.NamespaceName}.{memberName}";
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
                    AssignmentErrorMessage: global.IsMutable
                        ? null
                        : DescribeGlobalRebindingError(qualifiedName, global.BindingKind ?? GlobalBindingKind.Immutable));
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

        var namedType = target.NamedType ?? ResolveNamedTypeSymbol(target.Type);
        if (namedType is null)
        {
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
                MemoryRootIsIndependentStorage: target.MemoryRootIsIndependentStorage);
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

        ReportError("STK3005", $"Type '{namedType.Name}' does not contain a field named '{memberName}'.", context);
        return new ExpressionBinding(StarkTypeSymbols.Error);
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
            .Where(static function => !function.IsUnsafe)
            .ToArray();

        if (candidates.Length == 1)
        {
            var function = candidates[0];
            var location = Location(token);
            _functionPointerPromotions.Add(new FunctionPointerPromotionTypingRecord(
                function,
                targetType,
                location,
                _currentFunctionName));
            RecordAddressTakenFunction(function, location);
            return new ExpressionBinding(
                targetType,
                Function: function,
                DiagnosticName: $"function item '{function.DisplaySourceName}'");
        }

        if (candidates.Length == 0 && matchingCandidates.Any(static function => function.IsUnsafe))
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

    private static StarkTypeSymbol FunctionPointerTypeForSignature(TypedFunctionSignature function)
    {
        return StarkTypeSymbols.FunctionPointer(
            function.Kind,
            function.ReturnType,
            function.Parameters.Select(static parameter => parameter.Type).ToArray());
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
            && _functionOverloads.TryGetValue($"{CurrentFunctionModuleName}.{sourceName}", out candidates))
        {
            overloads = candidates;
            return true;
        }

        if (!sourceName.Contains('.', StringComparison.Ordinal))
        {
            var importedCandidates = new List<TypedFunctionSignature>();
            foreach (var candidateName in _moduleGraph.EnumerateAccessibleModuleQualifiedNames(CurrentFunctionModuleName, sourceName))
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

        if (literal.signedIntegerLiteral() is { } integerLiteral)
        {
            var value = ParseSignedIntegerLiteral(integerLiteral);
            type = InferIntegerLiteralType(value);
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
        return new ExpressionBinding(type, TextLiteral: textLiteral, TextLiteralKind: textLiteralKind);
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
        var bindingResult = new ExpressionBinding(
            type,
            TextLiteral: foldedLiteral,
            TextLiteralKind: TextLiteralKind.String);

        if (expectedType is not null
            && IsTextType(expectedType)
            && CanExplicitlyConvertTextLiteral(expectedType, bindingResult))
        {
            return bindingResult with { Type = expectedType };
        }

        return bindingResult;
    }

    private StarkTypeSymbol ResolveReturnType(StarkParser.ReturnTypeContext returnType, ISet<string>? genericParameters, string? currentModuleName = null)
    {
        return EnsureMonomorphizedType(
            _typeResolver!.ResolveReturnType(returnType, genericParameters ?? _currentFunctionGenericParameters, currentModuleName),
            Location(returnType));
    }

    private StarkTypeSymbol ResolveType(StarkParser.Type_Context type, ISet<string>? genericParameters = null, string? currentModuleName = null)
    {
        return EnsureMonomorphizedType(
            _typeResolver!.ResolveType(type, genericParameters ?? _currentFunctionGenericParameters, currentModuleName),
            Location(type));
    }

    private StarkTypeSymbol ResolveParameterType(
        StarkParser.Type_Context type,
        ISet<string>? genericParameters,
        string? currentModuleName,
        out string? rawPointerElementCountExpression)
    {
        return EnsureMonomorphizedType(
            _typeResolver!.ResolveParameterType(
                type,
                genericParameters ?? _currentFunctionGenericParameters,
                currentModuleName,
                out rawPointerElementCountExpression),
            Location(type));
    }

    private StarkTypeSymbol ResolveQualifiedType(string qualifiedName, ISet<string>? genericParameters, IToken token, string? currentModuleName = null)
    {
        return _typeResolver!.ResolveQualifiedType(qualifiedName, genericParameters ?? _currentFunctionGenericParameters, token, currentModuleName);
    }

    private StarkTypeSymbol ResolveGenericQualifiedName(StarkParser.GenericQualifiedNameContext genericQualifiedName)
    {
        var baseName = genericQualifiedName.qualifiedName().GetText();
        var baseType = ResolveQualifiedType(baseName, genericParameters: null, genericQualifiedName.qualifiedName().Start, CurrentFunctionModuleName);
        if (baseType.Kind == StarkTypeKind.Error)
        {
            return StarkTypeSymbols.Error;
        }

        var typeArguments = genericQualifiedName.typeArgumentList().type_()
            .Select(typeArgument => ResolveType(typeArgument, currentModuleName: CurrentFunctionModuleName))
            .ToArray();
        if (typeArguments.Any(static type => type.Kind == StarkTypeKind.Error))
        {
            return StarkTypeSymbols.Error;
        }

        return EnsureMonomorphizedType(
            StarkTypeSymbols.GenericInstantiation(baseType.NamedType ?? baseName, typeArguments),
            Location(genericQualifiedName));
    }

    private ExpressionBinding ResolveGenericEnumCaseReferenceValue(
        StarkParser.GenericEnumCaseReferenceContext genericEnumCaseReference,
        bool allowFunctionReference)
    {
        if (!TryResolveEnumCaseReference(genericEnumCaseReference, out var enumType, out var enumTypeSymbol, out var variant))
        {
            ReportError("STK3003", $"Unknown symbol '{genericEnumCaseReference.GetText()}'.", genericEnumCaseReference);
            return new ExpressionBinding(StarkTypeSymbols.Error);
        }

        return CreateEnumCaseValueBinding(
            genericEnumCaseReference.GetText(),
            enumTypeSymbol,
            enumType,
            variant,
            genericEnumCaseReference.Start,
            allowFunctionReference);
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
            && strippedType.NamedType is not null
            && strippedType.TypeArguments is not null)
        {
            var monomorphizedArguments = strippedType.TypeArguments
                .Select(argument => EnsureMonomorphizedType(argument))
                .ToArray();
            monomorphizedType = StarkTypeSymbols.WithQualifiers(
                StarkTypeSymbols.GenericInstantiation(
                    StarkTypeSymbols.GetGenericBaseName(strippedType.NamedType),
                    monomorphizedArguments),
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
                StarkTypeKind.FixedArray => StarkTypeSymbols.FixedArray(monomorphizedElement, strippedType.FixedLength),
                StarkTypeKind.Slice => StarkTypeSymbols.Slice(monomorphizedElement),
                StarkTypeKind.RawPointer => StarkTypeSymbols.RawPointer(monomorphizedElement, strippedType.IsMutablePointer),
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
                    parameterTypes.Select(parameter => EnsureMonomorphizedType(parameter)).ToArray()),
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
        if (monomorphizedType.TypeArguments is { Count: > 0 } typeArguments)
        {
            ValidateDictionaryKeyConstraint(monomorphizedType, triggerLocation);
            _genericInstantiationArguments.TryAdd(key, typeArguments.ToArray());
        }

        if (_namedTypes.TryGetValue(key, out var existingNamedType))
        {
            if (monomorphizedType.TypeArguments is { Count: > 0 } refreshTypeArguments
                && TryRefreshIncompleteConcreteType(key, refreshTypeArguments))
            {
                existingNamedType = _namedTypes[key];
            }

            if (monomorphizedType.TypeArguments is { Count: > 0 } existingTypeArguments)
            {
                EnsureConcreteConstructorShapes(key, existingTypeArguments);
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

        if (template.GenericParams.Count != monomorphizedType.TypeArguments!.Count)
        {
            ReportError(
                "STK3019",
                $"Generic type '{baseName}' expects {template.GenericParams.Count} type argument(s) but {monomorphizedType.TypeArguments.Count} were provided.",
                SourceLocation.Synthetic());
            return monomorphizedType;
        }

        var substitution = new Dictionary<string, StarkTypeSymbol>(StringComparer.Ordinal);
        for (var i = 0; i < template.GenericParams.Count; i++)
        {
            substitution[template.GenericParams[i]] = EnsureMonomorphizedType(monomorphizedType.TypeArguments[i]);
        }

        _namedTypes[key] = new NamedTypeSymbol(
            key,
            template.Kind,
            new Dictionary<string, FieldSymbol>(StringComparer.Ordinal),
            [],
            GenericParameterNames: template.GenericParams.ToArray());
        _namedTypes[key] = template.Kind == DeclarationKind.Enum
            ? CreateConcreteEnum(key, template, substitution)
            : CreateConcreteStructLike(key, template, substitution);
        if (_constructors.TryGetValue(baseName, out var templateConstructors))
        {
            _constructors[key] = CreateConcreteConstructors(templateConstructors, substitution);
        }

        if (triggerLocation is { } typeTriggerLocation)
        {
            RecordTypeInstantiationTriggers(monomorphizedType, typeTriggerLocation);
        }

        return monomorphizedType;
    }

    private void ValidateDictionaryKeyConstraint(StarkTypeSymbol dictionaryType, SourceLocation? triggerLocation)
    {
        if (!TryGetDictionaryKeyType(dictionaryType, out var keyType)
            || TypeContainsOpenCurrentFunctionGenericParameter(keyType)
            || IsCompilerProvenDictionaryKey(keyType))
        {
            return;
        }

        var diagnosticKey = $"{dictionaryType.NamedType}|{keyType.DisplayName}|{triggerLocation?.Line ?? 0}|{triggerLocation?.Column ?? 0}";
        if (!_dictionaryKeyConstraintFailures.Add(diagnosticKey))
        {
            return;
        }

        ReportError(
            "STK3023",
            $"Dictionary key type '{keyType.DisplayName}' must satisfy 'System.Collections.DictionaryKey<{keyType.DisplayName}>'. The current compiler can prove that contract for 'bool' and Stark integer key types; add an explicit hash/equality contract before using this key type.",
            triggerLocation ?? SourceLocation.Synthetic());
    }

    private static bool TryGetDictionaryKeyType(StarkTypeSymbol type, out StarkTypeSymbol keyType)
    {
        keyType = StarkTypeSymbols.Error;
        var coreType = StarkTypeSymbols.WithQualifiers(
            type,
            borrowKind: StarkBorrowKind.None,
            accessKind: StarkAccessKind.None,
            initializationKind: StarkInitializationKind.None,
            isMutableView: false);

        if (!StarkTypeSymbols.IsGenericInstantiation(coreType)
            || coreType.NamedType is null
            || coreType.TypeArguments is not { Count: 2 }
            || !string.Equals(StarkTypeSymbols.GetGenericBaseName(coreType.NamedType), "System.Collections.Dictionary", StringComparison.Ordinal))
        {
            return false;
        }

        keyType = StarkTypeSymbols.WithQualifiers(
            coreType.TypeArguments[0],
            borrowKind: StarkBorrowKind.None,
            accessKind: StarkAccessKind.None,
            initializationKind: StarkInitializationKind.None,
            isMutableView: false);
        return true;
    }

    private static bool IsCompilerProvenDictionaryKey(StarkTypeSymbol keyType)
    {
        var coreType = StarkTypeSymbols.WithQualifiers(
            keyType,
            borrowKind: StarkBorrowKind.None,
            accessKind: StarkAccessKind.None,
            initializationKind: StarkInitializationKind.None,
            isMutableView: false);

        return coreType.Kind is StarkTypeKind.Bool or StarkTypeKind.Integer;
    }

    private void RefreshConcreteInstantiationsForTemplate(NamedTypeSymbol template)
    {
        if (!template.IsGeneric || template.OrderedFields.Count == 0)
        {
            return;
        }

        foreach (var (key, typeArguments) in _genericInstantiationArguments.ToArray())
        {
            if (!string.Equals(StarkTypeSymbols.GetGenericBaseName(key), template.Name, StringComparison.Ordinal))
            {
                continue;
            }

            _ = TryRefreshIncompleteConcreteType(key, typeArguments);
        }
    }

    private bool TryRefreshIncompleteConcreteType(string key, IReadOnlyList<StarkTypeSymbol> typeArguments)
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
                || template.GenericParams.Count != typeArguments.Count
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
                substitution[template.GenericParams[i]] = typeArguments[i];
            }

            _namedTypes[key] = template.Kind == DeclarationKind.Enum
                ? CreateConcreteEnum(key, template, substitution)
                : CreateConcreteStructLike(key, template, substitution);
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
        IReadOnlyDictionary<string, StarkTypeSymbol> substitution)
    {
        var concreteVariants = template.Variants
            .Select(variant => new EnumVariantSymbol(
                variant.Name,
                variant.UsesNamedFields,
                variant.Fields
                    .Select(f => new EnumVariantFieldSymbol(f.Position, f.Name, SubstituteType(f.Type, substitution)))
                    .ToArray()))
            .ToList();

        return new NamedTypeSymbol(
            key,
            DeclarationKind.Enum,
            new Dictionary<string, FieldSymbol>(StringComparer.Ordinal),
            [],
            EnumVariants: concreteVariants);
    }

    private NamedTypeSymbol CreateConcreteStructLike(
        string key,
        NamedTypeSymbol template,
        IReadOnlyDictionary<string, StarkTypeSymbol> substitution)
    {
        var concreteFields = new Dictionary<string, FieldSymbol>(StringComparer.Ordinal);
        var concreteOrderedFields = new List<FieldSymbol>();

        foreach (var field in template.OrderedFields)
        {
            var concreteField = field with { Type = SubstituteType(field.Type, substitution) };
            concreteFields[field.Name] = concreteField;
            concreteOrderedFields.Add(concreteField);
        }

        return new NamedTypeSymbol(key, template.Kind, concreteFields, concreteOrderedFields);
    }

    private List<ConstructorShape> CreateConcreteConstructors(
        IReadOnlyList<ConstructorShape> templateConstructors,
        IReadOnlyDictionary<string, StarkTypeSymbol> substitution)
    {
        return templateConstructors
            .Select(constructor => new ConstructorShape(
                constructor.Name,
                constructor.Parameters
                    .Select(parameter => new TypedParameterSymbol(
                        parameter.Name,
                        SubstituteType(parameter.Type, substitution),
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
        foreach (var (key, typeArguments) in _genericInstantiationArguments.ToArray())
        {
            EnsureConcreteConstructorShapes(key, typeArguments);
        }
    }

    private void EnsureConcreteConstructorShapes(string key, IReadOnlyList<StarkTypeSymbol> typeArguments)
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
            || template.GenericParams.Count != typeArguments.Count)
        {
            return;
        }

        var substitution = new Dictionary<string, StarkTypeSymbol>(StringComparer.Ordinal);
        for (var i = 0; i < template.GenericParams.Count; i++)
        {
            substitution[template.GenericParams[i]] = typeArguments[i];
        }

        _constructors[key] = CreateConcreteConstructors(templateConstructors, substitution);
    }

    private StarkTypeSymbol SubstituteType(
        StarkTypeSymbol type,
        IReadOnlyDictionary<string, StarkTypeSymbol> substitution)
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
            else if (StarkTypeSymbols.IsGenericInstantiation(coreType) && coreType.TypeArguments is not null)
            {
                var newArgs = coreType.TypeArguments.Select(a => SubstituteType(a, substitution)).ToArray();
                substitutedCore = EnsureMonomorphizedType(
                    StarkTypeSymbols.GenericInstantiation(
                        StarkTypeSymbols.GetGenericBaseName(name),
                        newArgs));
            }
            else
            {
                substitutedCore = coreType;
            }
        }
        else if (coreType.ElementType is not null)
        {
            var newElement = SubstituteType(coreType.ElementType, substitution);
            if (ReferenceEquals(newElement, coreType.ElementType))
            {
                substitutedCore = coreType;
            }
            else
            {
                substitutedCore = coreType.Kind switch
                {
                    StarkTypeKind.FixedArray => StarkTypeSymbols.FixedArray(newElement, coreType.FixedLength),
                    StarkTypeKind.Slice => StarkTypeSymbols.Slice(newElement),
                    StarkTypeKind.RawPointer => StarkTypeSymbols.RawPointer(newElement, coreType.IsMutablePointer),
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
                SubstituteType(returnType, substitution),
                parameterTypes.Select(parameter => SubstituteType(parameter, substitution)).ToArray());
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
            || !_namedTypes.TryGetValue(enumTypeSymbol.NamedType, out enumType)
            || enumType.Kind != DeclarationKind.Enum
            || !enumType.TryGetVariant(genericEnumCaseReference.Identifier().GetText(), out variant, out _))
        {
            enumType = null!;
            enumTypeSymbol = StarkTypeSymbols.Error;
            variant = null!;
            return false;
        }

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

        if (_namedTypes.TryGetValue(typeName, out namedType!))
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

        namedType = null!;
        return false;
    }

    private string CurrentFunctionModuleName => _currentFunctionModuleName ?? _syntaxModel.ModuleName;

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

        var currentType = operands[0].Type;

        for (var index = 1; index < operands.Count; index++)
        {
            var nextType = operands[index].Type;
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

        var currentType = operands[0].Type;

        for (var index = 1; index < operands.Count; index++)
        {
            var nextType = operands[index].Type;
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
                TextLiteralKind: TextLiteralKind.String);
        }

        if (isFixedTextStorageConcat)
        {
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
                TextLiteralKind: current.TextLiteralKind);
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
            CanAssign);
        if (!resolution.Succeeded)
        {
            return false;
        }

        var signature = CacheFunctionInstantiation(resolution.Match!);
        RecordDirectCall(signature, context);
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

    private ConstructorShape? CheckObjectCreationArguments(
        StarkParser.ArgumentListContext? arguments,
        ParserRuleContext diagnosticContext,
        StarkTypeSymbol createdType,
        Scope scope)
    {
        var suppliedArguments = arguments?.argument() ?? [];
        var argumentCount = suppliedArguments.Length;

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
                return;
            }
        }

        if (CanAssign(parameterType, argumentType))
        {
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
            return;
        }

        ReportError(
            "STK3002",
            $"Argument 1 for '{functionName}' expects '{parameterType.DisplayName}' but found '{receiver.Type.DisplayName}'.{GetExplicitConversionHint(parameterType, receiver.Type)}",
            context);
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
            && local.Type.Kind is StarkTypeKind.Named or StarkTypeKind.FixedArray;
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

    private static StarkTypeSymbol GetConstProvenanceViewType(StarkTypeSymbol type)
    {
        return StarkTypeSymbols.FreezeReachableView(type);
    }

    private static StarkTypeSymbol ProjectProjectionType(ExpressionBinding source, StarkTypeSymbol projectedType)
    {
        return UsesFrozenProjectionSemantics(source)
            ? StarkTypeSymbols.FreezeReachableView(projectedType)
            : ProjectFrozenView(source.Type, projectedType);
    }

    private static StarkTypeSymbol ProjectFrozenView(StarkTypeSymbol sourceType, StarkTypeSymbol projectedType)
    {
        return sourceType.AccessKind == StarkAccessKind.Frozen
            ? StarkTypeSymbols.FreezeReachableView(projectedType)
            : projectedType;
    }

    private bool CanAssign(StarkTypeSymbol target, StarkTypeSymbol source)
    {
        if (target.Kind == StarkTypeKind.Error || source.Kind == StarkTypeKind.Error)
        {
            return true;
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

        if (target.Kind == StarkTypeKind.FunctionPointer && source.Kind == StarkTypeKind.FunctionPointer)
        {
            return TypeCompatibilityFacts.AreFunctionPointerTypesAssignable(target, source);
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
                $"Explicit conversion from '{source.Type.DisplayName}' to '{targetType.DisplayName}' is not supported because text widening and narrowing currently require a compile-time text constant or a future explicit owning-text construction path.";
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
            CanAssign);
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
        if (type.BitWidth is not int bitWidth || bitWidth <= 0)
        {
            min = default;
            max = default;
            return false;
        }

        if (type.RangeMin is not null && type.RangeMax is not null)
        {
            min = type.RangeMin.Value;
            max = type.RangeMax.Value;
            return true;
        }

        if (type.IsUnsigned)
        {
            min = BigInteger.Zero;
            max = (BigInteger.One << bitWidth) - BigInteger.One;
            return true;
        }

        min = -(BigInteger.One << (bitWidth - 1));
        max = (BigInteger.One << (bitWidth - 1)) - BigInteger.One;
        return true;
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
            or StarkTypeKind.Named;
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

        return StarkTypeSymbols.Integer(SupportedIntegerLiteralWidths[^1], value, value);
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

    private StarkTypeSymbol ValidateRuntimeValueType(StarkTypeSymbol type, ParserRuleContext context, string usage)
    {
        if (TryFindCompileTimeOnlyTypeDependency(type, out var dependencyName, out var dependencyKind))
        {
            ReportError(
                "STK3013",
                $"Type '{type.DisplayName}' depends on compile-time-only {DescribeCompileTimeOnlyKind(dependencyKind)} '{dependencyName}', which is not allowed for {usage}. {DescribeNoDynamicDispatchPolicy()}",
                context);
        }

        return type;
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
                $"Type '{type.DisplayName}' depends on enum '{enumName}', but enums are not yet supported in {usage}.",
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
            || signature.TemplateName is null
            || signature.TypeArguments is not { Count: > 0 })
        {
            return signature;
        }

        var key = BuildFunctionInstantiationKey(signature.TemplateName, signature.TypeArguments);
        if (_functionInstantiationCache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        _functionInstantiationCache[key] = signature;
        return signature;
    }

    private void RecordFunctionInstantiationTrigger(TypedFunctionSignature signature, ParserRuleContext context)
    {
        if (!signature.IsGenericInstantiation || signature.TypeArguments is not { Count: > 0 })
        {
            return;
        }

        if (signature.TypeArguments.Any(TypeContainsOpenCurrentFunctionGenericParameter))
        {
            RecordDeferredFunctionInstantiationTrigger(signature, context);
            return;
        }

        var key = BuildFunctionInstantiationKey(signature.TemplateName ?? signature.DisplaySourceName, signature.TypeArguments);
        if (!_functionInstantiationKeys.Add(key))
        {
            return;
        }

        _functionInstantiationTriggers.Add(new FunctionInstantiationTriggerRecord(
            signature.DisplaySourceName,
            signature.TypeArguments.ToArray(),
            signature,
            Location(context)));
    }

    private void RecordDeferredFunctionInstantiationTrigger(TypedFunctionSignature signature, ParserRuleContext context)
    {
        if (_currentFunctionName is null
            || signature.TemplateName is not { } templateName
            || signature.TypeArguments is not { Count: > 0 })
        {
            return;
        }

        var key = $"{_currentFunctionName}|{templateName}|{FunctionOverloadFacts.BuildTypeArgumentKey(signature.TypeArguments)}";
        if (!_deferredFunctionInstantiationKeys.Add(key))
        {
            return;
        }

        _deferredFunctionInstantiationTriggers.Add(new DeferredFunctionInstantiationTriggerRecord(
            _currentFunctionName,
            signature,
            Location(context)));
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
            || coreType.TypeArguments is not { Count: > 0 }
            || TypeContainsOpenCurrentFunctionGenericParameter(coreType))
        {
            if (StarkTypeSymbols.IsGenericInstantiation(coreType)
                && coreType.NamedType is not null
                && coreType.TypeArguments is { Count: > 0 }
                && TypeContainsOpenCurrentFunctionGenericParameter(coreType))
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

        var globalKey = BuildTypeInstantiationKey(coreType.NamedType, coreType.TypeArguments);
        if (!_typeInstantiationKeys.Add(globalKey))
        {
            return;
        }

        _typeInstantiationTriggers.Add(new TypeInstantiationTriggerRecord(
            coreType.NamedType,
            coreType.TypeArguments.ToArray(),
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
            || type.TypeArguments is not { Count: > 0 })
        {
            return;
        }

        var key = $"{_currentFunctionName}|{BuildTypeInstantiationKey(type.NamedType, type.TypeArguments)}";
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

        return coreType.ElementType is not null
            && TypeContainsOpenCurrentFunctionGenericParameter(coreType.ElementType);
    }

    private static string BuildFunctionInstantiationKey(string templateName, IReadOnlyList<StarkTypeSymbol> typeArguments)
    {
        return $"{templateName}|{FunctionOverloadFacts.BuildTypeArgumentKey(typeArguments)}";
    }

    private static string BuildTypeInstantiationKey(string typeName, IReadOnlyList<StarkTypeSymbol> typeArguments)
    {
        return $"{StarkTypeSymbols.GetGenericBaseName(typeName)}|{FunctionOverloadFacts.BuildTypeArgumentKey(typeArguments)}";
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
        string? RawPointerElementCountExpression = null);

    private sealed record LambdaCaptureBinding(
        VariableSymbol Symbol,
        string Mode);

    private sealed record ExpressionBinding(
        StarkTypeSymbol Type,
        bool IsAssignable = false,
        NamedTypeSymbol? NamedType = null,
        TypedFunctionSignature? Function = null,
        string? OverloadSourceName = null,
        string? NamespaceName = null,
        string? DiagnosticName = null,
        ExpressionBinding? Receiver = null,
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
        bool MemoryRootIsIndependentStorage = false);

    private sealed record LocalMemoryProvenance(
        string RootKey,
        bool IsIndependentStorage,
        string? RawPointerElementCountExpression = null);

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

        public Scope(Scope parent)
        {
            Parent = parent;
        }

        private Scope(Dictionary<string, VariableSymbol> globals)
        {
            _globals = globals;
        }

        public Scope? Parent { get; }

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
    }
}
