using System.Numerics;
using Antlr4.Runtime;
using Antlr4.Runtime.Tree;
using Stark.Parsing;

namespace Stark.Compiler;

internal sealed class TypeChecker
{
    private static readonly int[] SupportedIntegerLiteralWidths = [8, 16, 24, 32, 48, 64, 96, 128, 192, 256, 384, 512, 768, 1024];
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
    private readonly List<FieldAccessTypingRecord> _fieldAccesses = [];
    private readonly List<MemberCallTypingRecord> _memberCalls = [];
    private readonly List<ObjectCreationTypingRecord> _objectCreations = [];
    private readonly List<FunctionInstantiationTriggerRecord> _functionInstantiationTriggers = [];
    private readonly List<DeferredFunctionInstantiationTriggerRecord> _deferredFunctionInstantiationTriggers = [];
    private readonly List<DeferredTypeInstantiationTriggerRecord> _deferredTypeInstantiationTriggers = [];
    private readonly List<TypeInstantiationTriggerRecord> _typeInstantiationTriggers = [];
    private readonly HashSet<string> _functionInstantiationKeys = new(StringComparer.Ordinal);
    private readonly HashSet<string> _deferredFunctionInstantiationKeys = new(StringComparer.Ordinal);
    private readonly HashSet<string> _deferredTypeInstantiationKeys = new(StringComparer.Ordinal);
    private readonly HashSet<string> _typeInstantiationKeys = new(StringComparer.Ordinal);
    private StarkTypeResolver? _typeResolver;
    private ISet<string>? _currentFunctionGenericParameters;
    private string? _currentFunctionName;
    private string? _currentFunctionModuleName;
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
            _fieldAccesses,
            _memberCalls);
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
                                        $"field '{fieldName}' in type '{recordName}'")));
                        }
                    }

                    foreach (var field in recordDeclaration.recordBody().recordMember()
                                 .Select(static member => member.fieldDeclaration())
                                 .Where(static field => field is not null)!)
                    {
                        AddFields(fields, orderedFields, field, genericParameters, module.SyntaxModel.ModuleName, recordName);
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
        ISet<string>? genericParameters = null)
    {
        var fields = new Dictionary<string, FieldSymbol>(StringComparer.Ordinal);
        var orderedFields = new List<FieldSymbol>();

        foreach (var field in fieldDeclarations)
        {
            AddFields(fields, orderedFields, field, genericParameters, currentModuleName, name);
        }

        return new NamedTypeSymbol(name, kind, fields, orderedFields,
            GenericParameterNames: genericParameters?.ToList());
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
        string containingTypeName)
    {
        var fieldType = ResolveType(fieldDeclaration.type_(), genericParameters, currentModuleName);

        foreach (var declarator in fieldDeclaration.variableDeclarators().variableDeclarator())
        {
            var fieldName = declarator.Identifier().GetText();
            AddField(
                fields,
                orderedFields,
                new FieldSymbol(
                    fieldName,
                    ValidateRuntimeValueType(
                        fieldType,
                        fieldDeclaration.type_(),
                        $"field '{fieldName}' in type '{containingTypeName}'")));
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
                var returnType = ResolveReturnType(functionSyntax.ReturnType, genericParameters, module.SyntaxModel.ModuleName);
                ValidateRuntimeValueType(returnType, functionSyntax.ReturnType, $"the return type of function '{localName}'");
                var isAbiBoundary = functionSyntax.Modifiers.Any(static modifier => string.Equals(modifier.GetText(), "ffi", StringComparison.Ordinal))
                    || declarationModel.Visibility == StarkVisibility.Export;
                if (isAbiBoundary)
                {
                    ValidateAbiTypeDoesNotDependOnEnum(returnType, functionSyntax.ReturnType, $"the return type of function '{localName}'");
                }

                var parameters = new List<TypedParameterSymbol>();
                foreach (var parameter in functionSyntax.ParameterList.parameter())
                {
                    var parameterType = ResolveType(parameter.type_(), genericParameters, module.SyntaxModel.ModuleName);
                    ValidateRuntimeValueType(parameterType, parameter.type_(), $"parameter '{parameter.Identifier().GetText()}'");
                    if (isAbiBoundary)
                    {
                        ValidateAbiTypeDoesNotDependOnEnum(parameterType, parameter, $"parameter '{parameter.Identifier().GetText()}'");
                    }

                    parameters.Add(new TypedParameterSymbol(parameter.Identifier().GetText(), parameterType));
                }

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
                    GenericParameterNames: genericParameterNames.Count == 0 ? null : genericParameterNames.ToArray());
                RegisterFunctionSignature(signature, seenOverloadKeys, functionSyntax.DeclarationContext);
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
                            .Select(static constructor => new ConstructorShape(
                                constructor.TypeName,
                                constructor.Parameters.ToArray(),
                                constructor.IsPrimaryShape))
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
                IsPrimaryShape: true));
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
                IsPrimaryShape: false));
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
                var parameterType = ResolveType(parameter.type_(), genericParameters, currentModuleName);
                return new TypedParameterSymbol(parameter.Identifier().GetText(), parameterType);
            })
            .ToArray();
    }

    private void CheckGlobalDeclarations()
    {
        RegisterImportedGlobals();

        foreach (var declaration in _parseResult.Root.topLevelDeclaration())
        {
            if (declaration.globalConstantDeclaration() is { } constantDeclaration)
            {
                var declaredType = ValidateRuntimeValueType(
                    ResolveType(constantDeclaration.type_()),
                    constantDeclaration.type_(),
                    "a global constant type");
                ValidateRuntimeTypeDoesNotDependOnEnum(declaredType, constantDeclaration.type_(), "a global constant type");
                foreach (var declarator in constantDeclaration.constantDeclarators().constantDeclarator())
                {
                    CheckVariableInitializer(declarator.variableInitializer(), declaredType, Scope.CreateRoot(_globals));
                    _globals[declarator.Identifier().GetText()] = new VariableSymbol(
                        declarator.Identifier().GetText(),
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
                    ResolveType(variableDeclaration.type_()),
                    variableDeclaration.type_(),
                    "a global variable type");
                ValidateRuntimeTypeDoesNotDependOnEnum(declaredType, variableDeclaration.type_(), "a global variable type");
                var isMutable = variableDeclaration.MUT() is not null;

                foreach (var declarator in variableDeclaration.variableDeclarators().variableDeclarator())
                {
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
                    var declaredType = ValidateRuntimeValueType(
                        ResolveType(constantDeclaration.type_(), currentModuleName: module.SyntaxModel.ModuleName),
                        constantDeclaration.type_(),
                        "a global constant type");
                    ValidateRuntimeTypeDoesNotDependOnEnum(declaredType, constantDeclaration.type_(), "a global constant type");
                    var declarationModel = module.SyntaxModel.Declarations.FirstOrDefault(
                        candidate => candidate.Kind == DeclarationKind.GlobalConstant
                                     && string.Equals(candidate.Name, constantDeclaration.constantDeclarators().constantDeclarator(0).Identifier().GetText(), StringComparison.Ordinal));

                    if (declarationModel is null || !IsDeclarationVisible(module, declarationModel))
                    {
                        continue;
                    }

                    foreach (var declarator in constantDeclaration.constantDeclarators().constantDeclarator())
                    {
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
                    scope.Declare(new VariableSymbol(parameter.Name, parameter.Type, IsMutable: false, IsConstant: false));
                }

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
                _currentFunctionGenericParameters = signature.IsGeneric
                    ? signature.GenericParams.ToHashSet(StringComparer.Ordinal)
                    : null;
                _currentFunctionName = signature.Name;
                _currentFunctionModuleName = module.SyntaxModel.ModuleName;
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
                _currentImportedTemplateLocalDeclarations = hasImportedTemplateSummary
                    ? importedTemplateSummary!.LocalDeclarations.ToDictionary(
                        static local => TemplateLocalDeclarationFacts.BuildLookupKey(local.Kind, local.Line, local.Column),
                        static local => local.Type,
                        StringComparer.Ordinal)
                    : null;
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

        if (statement.localConstantDeclaration() is { } localConstant)
        {
            var declaredType = ValidateRuntimeValueType(
                ResolveLocalDeclarationType(TemplateLocalDeclarationFacts.ConstantKind, localConstant, localConstant.type_()),
                localConstant.type_(),
                "a local constant type");
            RecordLocalDeclarationType(TemplateLocalDeclarationFacts.ConstantKind, declaredType, localConstant);
            foreach (var declarator in localConstant.constantDeclarators().constantDeclarator())
            {
                CheckVariableInitializer(declarator.variableInitializer(), declaredType, scope);
                scope.Declare(new VariableSymbol(declarator.Identifier().GetText(), declaredType, IsMutable: false, IsConstant: true));
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
            EnsureBoolean(EvaluateExpression(ifStatement.expression(), scope, allowFunctionReference: false).Type, ifStatement.expression(), "if conditions must be of type 'bool'");
            CheckStatement(ifStatement.statement(0), new Scope(scope), returnType);
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

            var valueType = EvaluateExpression(returnStatement.expression(), scope, allowFunctionReference: false).Type;
            EnsureReturnCompatible(returnType, valueType, returnStatement.expression());
            return;
        }

        if (statement.expressionStatement() is { } expressionStatement)
        {
            EvaluateExpression(expressionStatement.expression(), scope, allowFunctionReference: false);
        }
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
            return false;
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

        if (suffix is null || suffix.Identifier() is not null)
        {
            return false;
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

    private static bool IsMatchAllEnumPattern(EnumCoveragePattern pattern)
    {
        return pattern.Fields.All(static field => field.Kind == AggregateCoverageFieldKind.Wildcard);
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
            if (switchType.Kind == StarkTypeKind.Named)
            {
                ReportError(
                    "STK3008",
                    $"Switch over '{switchType.DisplayName}' currently supports exact-type aggregate patterns with scalar field subpatterns, plus '_' and 'default'. Whole-value capture patterns remain unsupported for named switch values.",
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
            ReportError(
                "STK3008",
                $"Switch over '{switchType.DisplayName}' currently supports field-level aggregate patterns, but whole-value typed captures like '{aggregatePattern.GetText()}' are not implemented yet.",
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
            ReportError(
                "STK3008",
                $"Enum case pattern '{context.GetText()}' must currently bind payload subpatterns directly, not as a whole-value typed capture.",
                context);
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
            ReportError(
                "STK3008",
                $"Nested aggregate switch pattern '{aggregatePattern.GetText()}' for field '{field.Name}' must currently use field subpatterns, not a whole-value typed capture.",
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
        RecordLocalDeclarationType(declarationKind, declaredType, declarationContext);

        foreach (var declarator in declarators)
        {
            if (declarator.variableInitializer() is null)
            {
                scope.Declare(new VariableSymbol(declarator.Identifier().GetText(), declaredType, IsMutable: isMutable, IsConstant: false));
                continue;
            }

            CheckVariableInitializer(declarator.variableInitializer(), declaredType, scope);
            scope.Declare(new VariableSymbol(declarator.Identifier().GetText(), declaredType, IsMutable: isMutable, IsConstant: false));
        }
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
        var isAssignable = target.IsAssignable && target.Type.AccessKind != StarkAccessKind.Frozen;
        binding = new ExpressionBinding(
            projectedType,
            IsAssignable: isAssignable,
            NamedType: ResolveNamedTypeSymbol(projectedType),
            DiagnosticName: $"member '{publishedFieldAccess.FieldName}'",
            IsAddressable: target.IsAddressable,
            RootGlobalName: target.RootGlobalName,
            RootGlobalBindingKind: target.RootGlobalBindingKind,
            AssignmentErrorMessage: target.RootGlobalBindingKind is not null
                && target.RootGlobalName is not null
                && !isAssignable
                ? DescribeGlobalMutationError(target.RootGlobalName, target.RootGlobalBindingKind.Value, $"member '{publishedFieldAccess.FieldName}'")
                : target.Type.AccessKind == StarkAccessKind.Frozen
                    ? DescribeFrozenMutationError($"member '{publishedFieldAccess.FieldName}'")
                    : target.AssignmentErrorMessage);
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
                && (objectCreation.objectInitializer() is not null
                    || objectCreation.argumentList() is { } argumentList && argumentList.argument().Length > 0))
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

    private void CheckVariableInitializer(StarkParser.VariableInitializerContext initializer, StarkTypeSymbol declaredType, Scope scope)
    {
        if (initializer.expression() is { } expression)
        {
            var valueType = EvaluateExpression(expression, scope, allowFunctionReference: false).Type;
            EnsureAssignmentCompatible(variableName: null, declaredType, valueType, expression, isConstant: false);
            return;
        }

        if (initializer.objectInitializer() is { } objectInitializer)
        {
            CheckObjectInitializer(objectInitializer, declaredType, scope, preInitializedMembers: null);
            return;
        }

        if (initializer.arrayInitializer() is { } arrayInitializer)
        {
            CheckArrayInitializer(arrayInitializer, declaredType, scope);
        }
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
                var valueType = EvaluateExpression(expression, scope, allowFunctionReference: false).Type;
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
                var valueType = EvaluateExpression(expression, scope, allowFunctionReference: false).Type;
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

    private ExpressionBinding EvaluateExpression(StarkParser.ExpressionContext expression, Scope scope, bool allowFunctionReference)
    {
        return EvaluateAssignmentExpression(expression.assignmentExpression(), scope, allowFunctionReference);
    }

    private ExpressionBinding EvaluateAssignmentExpression(StarkParser.AssignmentExpressionContext expression, Scope scope, bool allowFunctionReference)
    {
        if (expression.conditionalExpression() is { } conditionalExpression)
        {
            return EvaluateConditionalExpression(conditionalExpression, scope, allowFunctionReference);
        }

        var left = EvaluateUnaryExpression(expression.unaryExpression(), scope, allowFunctionReference: true);
        var right = EvaluateAssignmentExpression(expression.assignmentExpression(), scope, allowFunctionReference: false);
        var assignmentOperator = expression.assignmentOperator().GetText();

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

    private ExpressionBinding EvaluateConditionalExpression(StarkParser.ConditionalExpressionContext expression, Scope scope, bool allowFunctionReference)
    {
        var condition = EvaluateLogicalOrExpression(expression.logicalOrExpression(), scope, allowFunctionReference);
        if (expression.expression().Length == 0)
        {
            return condition;
        }

        EnsureBoolean(condition.Type, expression.logicalOrExpression(), "Conditional expressions require a boolean condition");

        var whenTrue = EvaluateExpression(expression.expression(0), scope, allowFunctionReference: false);
        var whenFalse = EvaluateExpression(expression.expression(1), scope, allowFunctionReference: false);
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

    private ExpressionBinding EvaluateLogicalOrExpression(StarkParser.LogicalOrExpressionContext expression, Scope scope, bool allowFunctionReference)
    {
        var operands = expression.logicalAndExpression().Select(item => EvaluateLogicalAndExpression(item, scope, allowFunctionReference)).ToArray();
        if (operands.Length == 1)
        {
            return operands[0];
        }

        foreach (var operand in operands)
        {
            EnsureBoolean(operand.Type, expression, "Logical '||' requires boolean operands");
        }

        return new ExpressionBinding(StarkTypeSymbols.Bool);
    }

    private ExpressionBinding EvaluateLogicalAndExpression(StarkParser.LogicalAndExpressionContext expression, Scope scope, bool allowFunctionReference)
    {
        var operands = expression.bitwiseOrExpression().Select(item => EvaluateBitwiseOrExpression(item, scope, allowFunctionReference)).ToArray();
        if (operands.Length == 1)
        {
            return operands[0];
        }

        foreach (var operand in operands)
        {
            EnsureBoolean(operand.Type, expression, "Logical '&&' requires boolean operands");
        }

        return new ExpressionBinding(StarkTypeSymbols.Bool);
    }

    private ExpressionBinding EvaluateBitwiseOrExpression(StarkParser.BitwiseOrExpressionContext expression, Scope scope, bool allowFunctionReference)
    {
        var operands = expression.bitwiseXorExpression().Select(item => EvaluateBitwiseXorExpression(item, scope, allowFunctionReference)).ToArray();
        return EvaluateBinaryChain(operands, ExtractOperators<StarkParser.BitwiseXorExpressionContext>(expression), expression, "Bitwise '|'", requireInteger: true);
    }

    private ExpressionBinding EvaluateBitwiseXorExpression(StarkParser.BitwiseXorExpressionContext expression, Scope scope, bool allowFunctionReference)
    {
        var operands = expression.bitwiseAndExpression().Select(item => EvaluateBitwiseAndExpression(item, scope, allowFunctionReference)).ToArray();
        return EvaluateBinaryChain(operands, ExtractOperators<StarkParser.BitwiseAndExpressionContext>(expression), expression, "Bitwise '^'", requireInteger: true);
    }

    private ExpressionBinding EvaluateBitwiseAndExpression(StarkParser.BitwiseAndExpressionContext expression, Scope scope, bool allowFunctionReference)
    {
        var operands = expression.equalityExpression().Select(item => EvaluateEqualityExpression(item, scope, allowFunctionReference)).ToArray();
        return EvaluateBinaryChain(operands, ExtractOperators<StarkParser.EqualityExpressionContext>(expression), expression, "Bitwise '&'", requireInteger: true);
    }

    private ExpressionBinding EvaluateEqualityExpression(StarkParser.EqualityExpressionContext expression, Scope scope, bool allowFunctionReference)
    {
        var operands = expression.relationalExpression().Select(item => EvaluateRelationalExpression(item, scope, allowFunctionReference)).ToArray();
        var operators = ExtractOperators<StarkParser.RelationalExpressionContext>(expression);
        if (operators.Count == 0)
        {
            return operands[0];
        }

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

    private ExpressionBinding EvaluateRelationalExpression(StarkParser.RelationalExpressionContext expression, Scope scope, bool allowFunctionReference)
    {
        var operands = expression.shiftExpression().Select(item => EvaluateShiftExpression(item, scope, allowFunctionReference)).ToArray();
        var operators = ExtractOperators<StarkParser.ShiftExpressionContext>(expression);
        if (operators.Count == 0)
        {
            return operands[0];
        }

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

    private ExpressionBinding EvaluateShiftExpression(StarkParser.ShiftExpressionContext expression, Scope scope, bool allowFunctionReference)
    {
        var operands = expression.additiveExpression().Select(item => EvaluateAdditiveExpression(item, scope, allowFunctionReference)).ToArray();
        var operators = ExtractOperators<StarkParser.AdditiveExpressionContext>(expression);
        if (operators.Count == 0)
        {
            return operands[0];
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

    private ExpressionBinding EvaluateAdditiveExpression(StarkParser.AdditiveExpressionContext expression, Scope scope, bool allowFunctionReference)
    {
        var operands = expression.multiplicativeExpression().Select(item => EvaluateMultiplicativeExpression(item, scope, allowFunctionReference)).ToArray();
        var operators = ExtractOperators<StarkParser.MultiplicativeExpressionContext>(expression);
        return EvaluateArithmeticChain(operands, operators, expression, "Additive operator");
    }

    private ExpressionBinding EvaluateMultiplicativeExpression(StarkParser.MultiplicativeExpressionContext expression, Scope scope, bool allowFunctionReference)
    {
        var operands = expression.unaryExpression().Select(item => EvaluateUnaryExpression(item, scope, allowFunctionReference)).ToArray();
        var operators = ExtractOperators<StarkParser.UnaryExpressionContext>(expression);
        return EvaluateArithmeticChain(operands, operators, expression, "Multiplicative operator");
    }

    private ExpressionBinding EvaluateUnaryExpression(StarkParser.UnaryExpressionContext expression, Scope scope, bool allowFunctionReference)
    {
        if (expression.powerExpression() is { } powerExpression)
        {
            return EvaluatePowerExpression(powerExpression, scope, allowFunctionReference);
        }

        if (expression.conversionType() is { } conversionType)
        {
            var convertedOperand = EvaluateUnaryExpression(expression.unaryExpression(), scope, allowFunctionReference: false);
            var targetType = TryGetPublishedTemplateConversionType(expression, out var publishedTargetType)
                ? EnsureMonomorphizedType(publishedTargetType, Location(conversionType))
                : _typeResolver!.ResolveConversionType(conversionType);
            EnsureExplicitConversionCompatible(targetType, convertedOperand, expression);
            RecordConversion(targetType, expression);
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

    private ExpressionBinding EvaluatePowerExpression(StarkParser.PowerExpressionContext expression, Scope scope, bool allowFunctionReference)
    {
        var left = EvaluatePostfixExpression(expression.postfixExpression(), scope, allowFunctionReference);
        if (expression.unaryExpression() is not { } rightExpression)
        {
            return left;
        }

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

    private ExpressionBinding EvaluatePostfixExpression(StarkParser.PostfixExpressionContext expression, Scope scope, bool allowFunctionReference)
    {
        var requiresCallableTarget = expression.postfixPart().Any(static part => part.argumentList() is not null);
        var binding = TryGetPublishedTemplateEnumCallBinding(expression, out var publishedEnumCall)
            ? publishedEnumCall
            : TryGetPublishedTemplateDirectCallBinding(expression, out var publishedBinding)
            ? publishedBinding
            : EvaluatePrimaryExpression(expression.primaryExpression(), scope, allowFunctionReference || requiresCallableTarget);

        var postfixParts = expression.postfixPart();
        for (var index = 0; index < postfixParts.Length; index++)
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

        return binding;
    }

    private ExpressionBinding EvaluatePrimaryExpression(StarkParser.PrimaryExpressionContext expression, Scope scope, bool allowFunctionReference)
    {
        if (expression.literal() is { } literal)
        {
            return EvaluateLiteral(literal);
        }

        if (expression.Identifier() is { } identifier)
        {
            return ResolveValue(identifier.GetText(), identifier.Symbol, scope, allowFunctionReference);
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
            return ResolveValue(qualifiedName.GetText(), qualifiedName.Start, scope, allowFunctionReference);
        }

        if (expression.objectCreationExpression() is { } objectCreationExpression)
        {
            return EvaluateObjectCreation(objectCreationExpression, scope);
        }

        return EvaluateExpression(expression.expression(), scope, allowFunctionReference: false);
    }

    private ExpressionBinding EvaluateObjectCreation(StarkParser.ObjectCreationExpressionContext expression, Scope scope)
    {
        TryGetPublishedTemplateObjectCreationSummary(expression, out var publishedObjectCreation);
        var createdType = publishedObjectCreation is not null
            ? EnsureMonomorphizedType(publishedObjectCreation.CreatedType, Location(expression.type_()))
            : ResolveType(expression.type_(), currentModuleName: CurrentFunctionModuleName);
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

        ConstructorShape? matchedConstructor = null;
        IReadOnlyList<ObjectInitializerMemberTypingRecord>? initializerMembers = null;

        if (expression.argumentList() is { } argumentList)
        {
            matchedConstructor = CheckObjectCreationArguments(argumentList, createdType, scope);
        }

        if (expression.objectInitializer() is { } objectInitializer)
        {
            initializerMembers = CheckObjectInitializer(
                objectInitializer,
                createdType,
                scope,
                matchedConstructor?.InitializedMembers,
                publishedObjectCreation?.InitializerMembers);
        }

        if ((expression.argumentList()?.argument().Length ?? 0) > 0
            || expression.objectInitializer() is not null)
        {
            _objectCreations.Add(new ObjectCreationTypingRecord(
                expression.GetText(),
                createdType,
                matchedConstructor is null
                    ? null
                    : new TypedConstructorShape(createdType.DisplayName, matchedConstructor.Parameters, matchedConstructor.IsPrimaryShape),
                Location(expression.Start),
                _currentFunctionName,
                initializerMembers));
        }

        return new ExpressionBinding(createdType, NamedType: ResolveNamedTypeSymbol(createdType), DiagnosticName: $"new '{createdType.DisplayName}'");
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

            var valueType = EvaluateExpression(member.expression(), scope, allowFunctionReference: false).Type;
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

        var argumentTypes = arguments.argument()
            .Select(argument => EvaluateExpression(argument.expression(), scope, allowFunctionReference: false).Type)
            .ToArray();

        if (target.OverloadSourceName is { } overloadSourceName)
        {
            if (!TryGetFunctionOverloads(overloadSourceName, out var overloads))
            {
                ReportError("STK3008", $"{DescribeExpressionTarget(target)} is not callable.", arguments);
                return new ExpressionBinding(StarkTypeSymbols.Error);
            }

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

        // Record use-site generic calls even when the call target did not flow through
        // overload resolution in this invocation (for example, imported typed-template
        // direct/member call facts that already carry a resolved signature).
        RecordFunctionInstantiationTrigger(target.Function, arguments);

        var receiverOffset = target.Receiver is null ? 0 : 1;
        var explicitParameterCount = Math.Max(0, target.Function.Parameters.Count - receiverOffset);

        if (explicitParameterCount != arguments.argument().Length)
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
                target.Function.Parameters[0].Type,
                target.Receiver,
                arguments);
        }

        for (var index = 0; index < Math.Min(explicitParameterCount, argumentTypes.Length); index++)
        {
            var parameter = target.Function.Parameters[index + receiverOffset];
            var argumentType = argumentTypes[index];
            EnsureCallArgumentCompatible(target.Function.DisplaySourceName, index + receiverOffset + 1, parameter.Type, argumentType, arguments.argument(index).expression());
        }

        if (target.Receiver is null)
        {
            RecordDirectCall(target.Function, arguments);
        }
        else
        {
            RecordMemberCall(target.Function, arguments);
        }

        return new ExpressionBinding(target.Function.ReturnType, NamedType: ResolveNamedTypeSymbol(target.Function.ReturnType), DiagnosticName: $"call to '{target.Function.DisplaySourceName}'");
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
            var argumentType = EvaluateExpression(arguments.argument(index).expression(), scope, allowFunctionReference: false).Type;
            if (index >= constructor.Variant.Fields.Count)
            {
                continue;
            }

            var parameterType = constructor.Variant.Fields[index].Type;
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
                    DiagnosticName: target.DiagnosticName is null ? "text slice" : $"text slice of {target.DiagnosticName}");
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
                    DiagnosticName: target.DiagnosticName is null ? "text element" : $"text element of {target.DiagnosticName}");
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
                DiagnosticName: target.DiagnosticName is null ? "text slice" : $"text slice of {target.DiagnosticName}");
        }

        var currentType = target.Type;
        var currentIsAssignable = target.IsAssignable;
        var currentUsesFrozenProjectionSemantics = UsesFrozenProjectionSemantics(target);

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

            if (currentType.Kind is StarkTypeKind.FixedArray or StarkTypeKind.Slice && currentType.ElementType is not null)
            {
                currentIsAssignable &= currentType.AccessKind != StarkAccessKind.Frozen;
                currentType = currentUsesFrozenProjectionSemantics
                    ? StarkTypeSymbols.FreezeReachableView(currentType.ElementType)
                    : ProjectFrozenView(currentType, currentType.ElementType);
                currentUsesFrozenProjectionSemantics = currentType.AccessKind == StarkAccessKind.Frozen;
                continue;
            }

            if (currentType.Kind == StarkTypeKind.RawPointer && currentType.ElementType is not null)
            {
                currentIsAssignable &= currentType.IsMutablePointer;
                currentType = currentType.ElementType;
                currentUsesFrozenProjectionSemantics = currentType.AccessKind == StarkAccessKind.Frozen;
                continue;
            }

            ReportError("STK3010", $"{DescribeExpressionTarget(target)} is not indexable.", context);
            return new ExpressionBinding(StarkTypeSymbols.Error);
        }

        return new ExpressionBinding(
            currentType,
            IsAssignable: currentIsAssignable,
            NamedType: ResolveNamedTypeSymbol(currentType),
            DiagnosticName: target.DiagnosticName is null ? "indexed element" : $"indexed element of {target.DiagnosticName}",
            IsAddressable: target.IsAddressable,
            RootGlobalName: target.RootGlobalName,
            RootGlobalBindingKind: target.RootGlobalBindingKind,
            AssignmentErrorMessage: target.RootGlobalBindingKind is not null
                && target.RootGlobalName is not null
                && !currentIsAssignable
                ? DescribeGlobalMutationError(target.RootGlobalName, target.RootGlobalBindingKind.Value, "indexed element")
                : target.Type.AccessKind == StarkAccessKind.Frozen
                    ? DescribeFrozenMutationError("indexed element")
                : target.AssignmentErrorMessage);
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

        var namedType = target.NamedType ?? ResolveNamedTypeSymbol(target.Type);
        if (namedType is null)
        {
            ReportError("STK3011", $"Cannot access member '{memberName}' on {DescribeExpressionTarget(target)}.", context);
            return new ExpressionBinding(StarkTypeSymbols.Error);
        }

        if (namedType.TryGetField(memberName, out var field, out var fieldIndex))
        {
            RecordFieldAccess(field.Name, fieldIndex, field.Type, context);
            var projectedType = ProjectProjectionType(target, field.Type);
            var isAssignable = target.IsAssignable && target.Type.AccessKind != StarkAccessKind.Frozen;
            return new ExpressionBinding(
                projectedType,
                IsAssignable: isAssignable,
                NamedType: ResolveNamedTypeSymbol(projectedType),
                DiagnosticName: $"member '{memberName}'",
                IsAddressable: target.IsAddressable,
                RootGlobalName: target.RootGlobalName,
                RootGlobalBindingKind: target.RootGlobalBindingKind,
                AssignmentErrorMessage: target.RootGlobalBindingKind is not null
                    && target.RootGlobalName is not null
                    && !isAssignable
                    ? DescribeGlobalMutationError(target.RootGlobalName, target.RootGlobalBindingKind.Value, $"member '{memberName}'")
                    : target.Type.AccessKind == StarkAccessKind.Frozen
                        ? DescribeFrozenMutationError($"member '{memberName}'")
                    : target.AssignmentErrorMessage);
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
            if (methods.Count == 1 && !methods[0].IsGeneric && methods[0].Parameters.Count != 0)
            {
                var method = methods[0];
                return new ExpressionBinding(
                    method.ReturnType,
                    NamedType: ResolveNamedTypeSymbol(method.ReturnType),
                    Function: method,
                    DiagnosticName: $"method '{method.DisplaySourceName}'",
                    Receiver: target);
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

    private ExpressionBinding ResolveValue(string name, IToken token, Scope scope, bool allowFunctionReference)
    {
        if (scope.TryLookup(name, out var local))
        {
            if (local.BindingKind is not null)
            {
                return new ExpressionBinding(
                    local.Type,
                    IsAssignable: local.IsMutable,
                    NamedType: ResolveNamedTypeSymbol(local.Type),
                    DiagnosticName: local.IsConstant ? $"constant '{name}'" : $"variable '{name}'",
                    IsAddressable: true,
                    RootGlobalName: name,
                    RootGlobalBindingKind: local.BindingKind,
                    AssignmentErrorMessage: local.IsMutable
                        ? null
                        : DescribeGlobalRebindingError(name, local.BindingKind.Value));
            }

            return new ExpressionBinding(
                local.Type,
                IsAssignable: !local.IsConstant,
                NamedType: ResolveNamedTypeSymbol(local.Type),
                DiagnosticName: local.IsConstant ? $"constant '{name}'" : $"variable '{name}'",
                IsAddressable: true);
        }

        if (_globals.TryGetValue(name, out var global))
        {
            return new ExpressionBinding(
                global.Type,
                IsAssignable: global.IsMutable,
                NamedType: ResolveNamedTypeSymbol(global.Type),
                DiagnosticName: global.IsConstant ? $"constant '{name}'" : $"variable '{name}'",
                IsAddressable: true,
                RootGlobalName: name,
                RootGlobalBindingKind: global.BindingKind,
                AssignmentErrorMessage: global.IsMutable
                    ? null
                    : DescribeGlobalRebindingError(name, global.BindingKind ?? GlobalBindingKind.Immutable));
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

        if (TryResolveNamedTypeBySourceName(name, out var namedType))
        {
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

        if (TryResolveEnumCaseReference(name, out var enumType, out var enumTypeSymbol, out var variant))
        {
            return CreateEnumCaseValueBinding(name, enumTypeSymbol, enumType, variant, token, allowFunctionReference);
        }

        if (TryResolveNamedTypeBySourceName(name, out namedType) && namedType.Kind == DeclarationKind.Enum)
        {
            return new ExpressionBinding(StarkTypeSymbols.Error, NamespaceName: name, DiagnosticName: $"enum '{name}'");
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

    private bool TryGetFunctionOverloads(string sourceName, out IReadOnlyList<TypedFunctionSignature> overloads)
    {
        if (_functionOverloads.TryGetValue(sourceName, out var candidates))
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

        overloads = [];
        return false;
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

        if (!StarkTypeSymbols.IsGenericInstantiation(monomorphizedType))
        {
            if (triggerLocation is { } nestedTriggerLocation)
            {
                RecordTypeInstantiationTriggers(monomorphizedType, nestedTriggerLocation);
            }

            return monomorphizedType;
        }

        var key = monomorphizedType.NamedType!;
        if (_namedTypes.ContainsKey(key))
        {
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
            var concreteField = new FieldSymbol(field.Name, SubstituteType(field.Type, substitution));
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
                    .Select(parameter => new TypedParameterSymbol(parameter.Name, SubstituteType(parameter.Type, substitution)))
                    .ToArray(),
                constructor.IsPrimaryShape))
            .ToList();
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

    private bool TryResolveNamedTypeBySourceName(string typeName, out NamedTypeSymbol namedType)
    {
        if (_namedTypes.TryGetValue(typeName, out namedType!))
        {
            return true;
        }

        if (!typeName.Contains('.', StringComparison.Ordinal)
            && _namedTypes.TryGetValue($"{CurrentFunctionModuleName}.{typeName}", out namedType!))
        {
            return true;
        }

        namedType = null!;
        return false;
    }

    private string CurrentFunctionModuleName => _currentFunctionModuleName ?? _syntaxModel.ModuleName;

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
            ? StarkTypeSymbols.FreezeReachableView(operand.Type)
            : operand.Type;
        var pointerType = StarkTypeSymbols.RawPointer(pointeeType, operand.IsAssignable);
        return new ExpressionBinding(pointerType, NamedType: ResolveNamedTypeSymbol(pointerType));
    }

    private ExpressionBinding EnsureDereferenceUnary(ExpressionBinding operand, ParserRuleContext context)
    {
        if (operand.Type.Kind != StarkTypeKind.RawPointer || operand.Type.ElementType is null)
        {
            ReportError("STK3002", "Operator '*' requires a raw pointer operand.", context);
            return new ExpressionBinding(StarkTypeSymbols.Error);
        }

        var pointeeType = operand.Type.ElementType;
        return new ExpressionBinding(
            pointeeType,
            IsAssignable: operand.Type.IsMutablePointer && pointeeType.AccessKind != StarkAccessKind.Frozen,
            NamedType: ResolveNamedTypeSymbol(pointeeType),
            DiagnosticName: "dereferenced value",
            IsAddressable: true);
    }

    private void EnsureBoolean(StarkTypeSymbol type, ParserRuleContext context, string message)
    {
        if (type.Kind != StarkTypeKind.Bool && type.Kind != StarkTypeKind.Error)
        {
            ReportError("STK3002", message, context);
        }
    }

    private ConstructorShape? CheckObjectCreationArguments(
        StarkParser.ArgumentListContext arguments,
        StarkTypeSymbol createdType,
        Scope scope)
    {
        if (arguments.argument().Length == 0)
        {
            return null;
        }

        var argumentTypes = new StarkTypeSymbol[arguments.argument().Length];
        for (var index = 0; index < arguments.argument().Length; index++)
        {
            argumentTypes[index] = EvaluateExpression(arguments.argument(index).expression(), scope, allowFunctionReference: false).Type;
        }

        if (createdType.Kind != StarkTypeKind.Named || createdType.NamedType is null)
        {
            ReportError(
                "STK3009",
                $"Type '{createdType.DisplayName}' does not declare constructors and cannot be created with arguments.",
                arguments);
            return null;
        }

        if (!_namedTypes.ContainsKey(createdType.NamedType))
        {
            return null;
        }

        if (!_constructors.TryGetValue(createdType.NamedType, out var constructors) || constructors.Count == 0)
        {
            ReportError(
                "STK3009",
                $"Type '{createdType.DisplayName}' does not declare a constructor that accepts {arguments.argument().Length} argument{Pluralize(arguments.argument().Length)}.",
                arguments);
            return null;
        }

        var arityMatches = constructors
            .Where(candidate => candidate.Parameters.Count == argumentTypes.Length)
            .ToArray();

        if (arityMatches.Length == 0)
        {
            var availableArities = string.Join(", ", constructors.Select(static candidate => candidate.Parameters.Count).Distinct().OrderBy(static value => value));
            ReportError(
                "STK3009",
                $"Type '{createdType.DisplayName}' does not declare a constructor that accepts {argumentTypes.Length} argument{Pluralize(argumentTypes.Length)}. Available constructor arities: {availableArities}.",
                arguments);
            return null;
        }

        var matchedConstructor = arityMatches
            .OrderBy(candidate => CountMismatchedParameters(candidate.Parameters, argumentTypes))
            .First();
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
                arguments.argument(index).expression());
        }

        return hadMismatch ? null : matchedConstructor;
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

        ReportError(
            "STK3002",
            $"Assignment to {target.DiagnosticName ?? "target"} expects '{target.Type.DisplayName}' but found '{valueType.DisplayName}'.{GetExplicitConversionHint(target.Type, valueType)}",
            context);
    }

    private void EnsureReturnCompatible(StarkTypeSymbol returnType, StarkTypeSymbol valueType, ParserRuleContext context)
    {
        if (CanAssign(returnType, valueType))
        {
            return;
        }

        ReportError(
            "STK3002",
            $"Return statement expects '{returnType.DisplayName}' but found '{valueType.DisplayName}'.{GetExplicitConversionHint(returnType, valueType)}",
            context);
    }

    private void EnsureCallArgumentCompatible(
        string functionName,
        int position,
        StarkTypeSymbol parameterType,
        StarkTypeSymbol argumentType,
        ParserRuleContext context)
    {
        if (CanAssign(parameterType, argumentType))
        {
            return;
        }

        ReportError(
            "STK3002",
            $"Argument {position} for '{functionName}' expects '{parameterType.DisplayName}' but found '{argumentType.DisplayName}'.{GetExplicitConversionHint(parameterType, argumentType)}",
            context);
    }

    private void EnsureReceiverArgumentCompatible(
        string functionName,
        StarkTypeSymbol parameterType,
        ExpressionBinding receiver,
        ParserRuleContext context)
    {
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
            && (source.BitWidth > target.BitWidth || !IsRangeContained(source.RangeMin, source.RangeMax, target.RangeMin, target.RangeMax)))
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
        return binding.Type.AccessKind == StarkAccessKind.Frozen
            || binding.RootGlobalBindingKind == GlobalBindingKind.Const;
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
            if (target.BitWidth is null || source.BitWidth is null || source.BitWidth > target.BitWidth)
            {
                return false;
            }

            return IsRangeContained(source.RangeMin, source.RangeMax, target.RangeMin, target.RangeMax);
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
            return StarkTypeSymbols.Integer(Math.Max(left.BitWidth ?? 0, right.BitWidth ?? 0));
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
        GlobalBindingKind? BindingKind = null);

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
        string? RootGlobalName = null,
        GlobalBindingKind? RootGlobalBindingKind = null,
        string? AssignmentErrorMessage = null,
        EnumConstructorBinding? EnumConstructor = null,
        string? TextLiteral = null,
        TextLiteralKind? TextLiteralKind = null);

    private sealed record EnumConstructorBinding(
        string Name,
        EnumVariantSymbol Variant);

    private sealed record ConstructorShape(
        string Name,
        IReadOnlyList<TypedParameterSymbol> Parameters,
        bool IsPrimaryShape)
    {
        public ISet<string>? InitializedMembers =>
            IsPrimaryShape
                ? Parameters.Select(static parameter => parameter.Name).ToHashSet(StringComparer.Ordinal)
                : null;
    }

    private sealed class Scope
    {
        private readonly Dictionary<string, VariableSymbol> _locals = new(StringComparer.Ordinal);
        private readonly Dictionary<string, VariableSymbol>? _globals;

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
    }
}
