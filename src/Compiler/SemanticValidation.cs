using System.Numerics;
using Antlr4.Runtime;
using Antlr4.Runtime.Tree;
using Stark.Parsing;

namespace Stark.Compiler;

internal sealed class SemanticValidator
{
    private static readonly StarkTypeSymbol NonNegativeI64Type = StarkTypeSymbols.Integer(64, BigInteger.Zero, (BigInteger.One << 63) - 1);

    private readonly CompilerPassContext _context;
    private readonly ParseResult _parseResult;
    private readonly SyntaxModel _syntaxModel;
    private readonly ModuleGraph _moduleGraph;
    private readonly LoadedModuleSet _loadedModules;
    private readonly FunctionEffectModel _effectModel;
    private readonly TypeCheckModel _typeModel;
    private readonly EnumLayoutModel _enumLayoutModel;
    private readonly IReadOnlyDictionary<string, ImportedFunctionSemanticSummary> _importedFunctionSemantics;
    private readonly StarkTypeResolver _typeResolver;
    private readonly Dictionary<string, TopLevelDeclarationModel> _syntaxDeclarations;
    private readonly Dictionary<string, DeclaredFunctionSyntax> _functionDeclarations;
    private readonly IReadOnlyDictionary<ObjectCreationKey, ObjectCreationTypingRecord> _objectCreations;
    private readonly Dictionary<string, FunctionValidationBuilder> _summaries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FunctionValidationBuilder> _destructorSummaries = new(StringComparer.Ordinal);
    private ISet<string>? _currentFunctionGenericParameters;
    private IReadOnlyDictionary<string, ComptimeGenericParameterSymbol>? _currentFunctionComptimeGenericParameters;
    private string? _currentFunctionName;
    private string? _currentModuleName;

    private string CurrentModuleName => _currentModuleName ?? _syntaxModel.ModuleName;

    public SemanticValidator(
        CompilerPassContext context,
        ParseResult parseResult,
        SyntaxModel syntaxModel,
        ModuleGraph moduleGraph,
        LoadedModuleSet loadedModules,
        FunctionEffectModel effectModel,
        TypeCheckModel typeModel,
        EnumLayoutModel enumLayoutModel)
    {
        _context = context;
        _parseResult = parseResult;
        _syntaxModel = syntaxModel;
        _moduleGraph = moduleGraph;
        _loadedModules = loadedModules;
        _effectModel = effectModel;
        _typeModel = typeModel;
        _enumLayoutModel = enumLayoutModel;
        _importedFunctionSemantics = loadedModules.ImportedModules
            .Where(static module => module.PackageImageFacts is not null)
            .SelectMany(static module => module.PackageImageFacts!.FunctionSemantics)
            .ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal);
        _typeResolver = new StarkTypeResolver(context, "semantic-validate", moduleGraph, typeModel.NamedTypes, typeModel.TypeAliases);
        _syntaxDeclarations = syntaxModel.Declarations.ToDictionary(
            declaration => declaration.Function is null
                ? declaration.Name
                : FunctionOverloadFacts.GetResolvedLocalName(syntaxModel, declaration),
            StringComparer.Ordinal);
        _functionDeclarations = DeclaredFunctionSyntaxCollector.Collect(parseResult, syntaxModel)
            .ToDictionary(static declaration => declaration.Name, StringComparer.Ordinal);
        _objectCreations = typeModel.ObjectCreations
            .GroupBy(static record => new ObjectCreationKey(record.ExpressionText, record.Location.Line, record.Location.Column))
            .ToDictionary(static group => group.Key, static group => group.Last());
    }

    public SemanticValidationModel Validate()
    {
        ValidateGlobalDeclarations();
        ValidateTypeDeclarations();
        ValidateBaseTraitLists();
        ValidateDynTraitDeclarations();
        ValidateDestructorDeclarations();

        foreach (var function in _functionDeclarations.Values)
        {
            ValidateFunction(function);
        }

        ValidateLambdaFunctions();
        FinalizeMemoryEffectsAndValidateCalls();
        InferEffectiveFunctionKindsAndValidateDeclaredContracts();

        return new SemanticValidationModel(
            _syntaxModel.ModuleName,
            _summaries.ToDictionary(
                static pair => pair.Key,
                static pair => pair.Value.Build(),
                StringComparer.Ordinal));
    }

    private void ValidateGlobalDeclarations()
    {
        foreach (var declaration in _parseResult.Root.topLevelDeclaration())
        {
            if (declaration.globalConstantDeclaration() is { } constantDeclaration)
            {
                foreach (var declarator in constantDeclaration.constantDeclarators().constantDeclarator())
                {
                    var name = declarator.Identifier().GetText();
                    var declaredType = _typeModel.Globals.TryGetValue(name, out var global)
                        ? global.Type
                        : constantDeclaration.type_() is { } typeContext
                            ? ResolveType(typeContext)
                            : StarkTypeSymbols.Error;
                    ValidateTypeUsage(constantDeclaration.type_() ?? (ParserRuleContext)declarator, declaredType, TypeUsage.Global);
                    ValidateConstGlobal(
                        declarator.Identifier().GetText(),
                        declaredType,
                        declarator.variableInitializer(),
                        global?.ConstantInitializer);
                }

                continue;
            }

            if (declaration.globalVariableDeclaration() is { } variableDeclaration)
            {
                ValidateGlobalVariableStorageClass(variableDeclaration);
                var declaredType = ResolveType(variableDeclaration.type_());
                ValidateTypeUsage(variableDeclaration.type_(), declaredType, TypeUsage.Global);
            }
        }
    }

    private void ValidateGlobalVariableStorageClass(StarkParser.GlobalVariableDeclarationContext declaration)
    {
        var storageClass = declaration.storageClass().GetText();
        if (storageClass == "static")
        {
            return;
        }

        _context.Diagnostics.Error(
            "STK4015",
            $"Top-level global variables must use 'static' storage. Storage class '{storageClass}' is only valid for local variables.",
            "semantic-validate",
            Location(declaration.storageClass()));
    }

    private void ValidateLocalVariableStorageClass(LocalStorageClass storageClass, ParserRuleContext context)
    {
        switch (storageClass)
        {
            case LocalStorageClass.Arena:
                _context.Diagnostics.Error(
                    "STK4017",
                    "Local 'arena' storage is reserved for allocator-backed region storage and is not a valid executable local storage class. Use 'stack' or 'heap' storage.",
                    "semantic-validate",
                    Location(context));
                break;
            case LocalStorageClass.Static:
                _context.Diagnostics.Error(
                    "STK4017",
                    "Function-local 'static' storage is not a valid local storage class. Use a top-level 'static' global for global lifetime storage, or use 'stack'/'heap' for locals.",
                    "semantic-validate",
                    Location(context));
                break;
        }
    }

    private void ValidateDestructorDeclarations()
    {
        foreach (var module in _loadedModules.Modules.Values)
        {
            var destructors = DeclaredDestructorSyntaxCollector.Collect(module)
                .GroupBy(static destructor => destructor.QualifiedTypeName, StringComparer.Ordinal);

            foreach (var group in destructors)
            {
                ValidateDestructorDeclarations(group.Key, group.ToArray());
            }
        }
    }

    private void ValidateDestructorDeclarations(
        string qualifiedTypeName,
        IReadOnlyList<DeclaredDestructorSyntax> destructors)
    {
        if (destructors.Count == 0)
        {
            return;
        }

        if (destructors.Count > 1)
        {
            foreach (var duplicate in destructors.Skip(1))
            {
                _context.Diagnostics.Error(
                    "STK4012",
                    $"Type '{qualifiedTypeName}' declares more than one destructor block. Stark currently allows at most one 'drop' or 'mut drop' block per type.",
                    "semantic-validate",
                    Location(duplicate.Declaration.Start));
            }
        }

        var destructor = destructors[0];
        var mutatesSelf = BlockMutatesSelf(destructor.Body);

        if (!destructor.IsMutable && mutatesSelf)
        {
            _context.Diagnostics.Error(
                "STK4011",
                $"Read-only destructor 'drop' on '{qualifiedTypeName}' may not mutate 'self'. Use 'mut drop' if destructor state rewrites are required.",
                "semantic-validate",
                Location(destructor.Declaration.Start));
        }

        if (destructor.IsMutable && !mutatesSelf)
        {
            _context.Diagnostics.Warning(
                "STK4010",
                $"Destructor 'mut drop' on '{qualifiedTypeName}' does not mutate 'self'. Use 'drop' instead.",
                "semantic-validate",
                Location(destructor.Declaration.Start));
        }

        if (ContainsReturnStatement(destructor.Body))
        {
            _context.Diagnostics.Error(
                "STK4014",
                $"Destructor block on '{qualifiedTypeName}' may not use 'return' because destructors are not ordinary functions.",
                "semantic-validate",
                Location(destructor.Declaration.Start));
        }

        AnalyzeDestructorMemoryEffects(qualifiedTypeName, destructor);
    }

    private void AnalyzeDestructorMemoryEffects(string qualifiedTypeName, DeclaredDestructorSyntax destructor)
    {
        if (!_typeModel.NamedTypes.TryGetValue(qualifiedTypeName, out _))
        {
            return;
        }

        var summary = new FunctionValidationBuilder($"{qualifiedTypeName}.drop");
        var selfType = StarkTypeSymbols.Named(qualifiedTypeName);
        var selfParameter = new TypedParameterSymbol("self", selfType, IsConst: !destructor.IsMutable);
        summary.Configure(StarkTypeSymbols.Void, hasBody: true, StarkFunctionKind.Fn);
        summary.SetParameters([selfParameter], [], _typeModel.NamedTypes, _enumLayoutModel.Layouts);

        var declaration = new FunctionDeclarationModel(
            summary.Name,
            StarkFunctionKind.Fn,
            StarkTypeSymbols.Void.DisplayName,
            [new ParameterModel("self", selfType.DisplayName, IsConst: !destructor.IsMutable)],
            new FunctionModifierSet(
                InlinePreference.InlineHint,
                HasExplicitInlinePreference: false,
                IsHot: false,
                IsCold: false,
                IsFfi: false,
                IsVarargs: false,
                IsStrictFp: false),
            HasBody: true);
        var effects = new FunctionEffectProfile(
            summary.Name,
            StarkFunctionKind.Fn,
            ReadsArgumentMemory: true,
            IsPure: false,
            NoSync: false,
            NoFree: false,
            NoUnwind: false,
            WillReturn: true,
            MustProgress: true,
            UseFastCallingConvention: true,
            IsFfi: false,
            IsVarargs: false,
            IsHot: false,
            IsCold: false,
            InlinePreference: InlinePreference.InlineHint,
            IsStrictFp: false);
        var scope = ValidationScope.CreateRoot();
        scope.Declare(new VariableSymbol(
            "self",
            selfType,
            SymbolOrigin.Parameter,
            LocalStorageClass.None,
            IsMutable: destructor.IsMutable,
            IsConstant: false,
            HasConstProvenance: !destructor.IsMutable));

        var previousGenericParameters = _currentFunctionGenericParameters;
        var previousFunctionName = _currentFunctionName;
        var previousModuleName = _currentModuleName;
        _currentFunctionGenericParameters = null;
        _currentFunctionName = summary.Name;
        _currentModuleName = destructor.ModuleName;

        try
        {
            CheckBlock(destructor.Body, scope, declaration, effects, summary, ControlFlowContext.Root);
        }
        finally
        {
            _currentFunctionGenericParameters = previousGenericParameters;
            _currentFunctionName = previousFunctionName;
            _currentModuleName = previousModuleName;
        }

        _destructorSummaries[qualifiedTypeName] = summary;
    }

    private static bool BlockMutatesSelf(StarkParser.BlockContext block)
    {
        return ContainsSelfMutation(block);
    }

    private static bool ContainsSelfMutation(ParserRuleContext context)
    {
        if (context is StarkParser.AssignmentExpressionContext assignment
            && assignment.unaryExpression() is { } target
            && IsSelfMutationTarget(target))
        {
            return true;
        }

        if (context is StarkParser.PostfixExpressionContext postfix
            && IsSelfRootedMutatingCall(postfix))
        {
            return true;
        }

        for (var index = 0; index < context.ChildCount; index++)
        {
            if (context.GetChild(index) is ParserRuleContext child && ContainsSelfMutation(child))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsReturnStatement(ParserRuleContext context)
    {
        if (context is StarkParser.ReturnStatementContext)
        {
            return true;
        }

        for (var index = 0; index < context.ChildCount; index++)
        {
            if (context.GetChild(index) is ParserRuleContext child && ContainsReturnStatement(child))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsSelfMutationTarget(StarkParser.UnaryExpressionContext target)
    {
        var postfix = target.powerExpression()?.postfixExpression();
        if (postfix is null)
        {
            return false;
        }

        var primary = postfix.primaryExpression();
        return primary?.Identifier() is { } identifier
            && string.Equals(identifier.GetText(), "self", StringComparison.Ordinal);
    }

    private static bool IsSelfRootedMutatingCall(StarkParser.PostfixExpressionContext expression)
    {
        var primary = expression.primaryExpression();
        if (primary?.Identifier() is not { } identifier
            || !string.Equals(identifier.GetText(), "self", StringComparison.Ordinal))
        {
            return false;
        }

        // This warning is heuristic-only. Treat self-rooted calls as mutation-capable so
        // mutating helpers like `self.Close()` do not trigger a false "use drop instead"
        // warning in otherwise valid destructors.
        return expression.postfixPart().Any(static part => part.argumentList() is not null);
    }

    private void ValidateConstGlobal(
        string name,
        StarkTypeSymbol declaredType,
        StarkParser.VariableInitializerContext initializer,
        TypedConstantInitializer? typedInitializer)
    {
        if (declaredType.Kind == StarkTypeKind.Error)
        {
            return;
        }

        if (IsExternalConstRawPointerPlaceholder(declaredType, initializer))
        {
            return;
        }

        if (!TryValidateFrozenConstGlobalType(
                declaredType,
                name,
                new HashSet<string>(StringComparer.Ordinal),
                out var failingPath,
                out var failureReason))
        {
            _context.Diagnostics.Error(
                "STK4007",
                $"Const global '{name}' must be a fully frozen object graph. Reachable path '{failingPath}' {failureReason}.",
                "semantic-validate",
                Location(initializer.Start));
            return;
        }

        if (typedInitializer is null && !CanMaterializeFrozenConstInitializer(initializer, declaredType))
        {
            _context.Diagnostics.Error(
                "STK4008",
                $"Const global '{name}' must use a frozen initializer that can be materialized as static data.",
                "semantic-validate",
                Location(initializer.Start));
        }
    }

    private StarkTypeSymbol ResolveType(StarkParser.Type_Context type)
    {
        return ResolveType(type, _currentFunctionGenericParameters, _currentFunctionComptimeGenericParameters);
    }

    private StarkTypeSymbol ResolveType(StarkParser.Type_Context type, ISet<string>? genericParameters)
    {
        return ResolveType(type, genericParameters, _currentFunctionComptimeGenericParameters);
    }

    private StarkTypeSymbol ResolveType(
        StarkParser.Type_Context type,
        ISet<string>? genericParameters,
        IReadOnlyDictionary<string, ComptimeGenericParameterSymbol>? comptimeGenericParameters)
    {
        return _typeResolver.ResolveType(type, genericParameters, CurrentModuleName, comptimeGenericParameters);
    }

    private IReadOnlyDictionary<string, ComptimeGenericParameterSymbol>? GetNamedTypeComptimeGenericParameterMap(string localName)
    {
        return TryGetCurrentModuleNamedType(localName, out var namedType)
            ? ToComptimeGenericParameterMap(namedType.ComptimeGenericParams)
            : null;
    }

    private IReadOnlyDictionary<string, ComptimeGenericParameterSymbol>? GetTypeAliasComptimeGenericParameterMap(string localName)
    {
        return TryGetCurrentModuleTypeAlias(localName, out var typeAlias)
            ? ToComptimeGenericParameterMap(typeAlias.ComptimeGenericParams)
            : null;
    }

    private bool TryGetCurrentModuleNamedType(string localName, out NamedTypeSymbol namedType)
    {
        if (_typeModel.NamedTypes.TryGetValue(localName, out namedType!))
        {
            return true;
        }

        return _typeModel.NamedTypes.TryGetValue($"{CurrentModuleName}.{localName}", out namedType!);
    }

    private bool TryGetCurrentModuleTypeAlias(string localName, out TypeAliasSymbol typeAlias)
    {
        if (_typeModel.TypeAliases.TryGetValue(localName, out typeAlias!))
        {
            return true;
        }

        return _typeModel.TypeAliases.TryGetValue($"{CurrentModuleName}.{localName}", out typeAlias!);
    }

    private static IReadOnlyDictionary<string, ComptimeGenericParameterSymbol>? ToComptimeGenericParameterMap(
        IReadOnlyList<ComptimeGenericParameterSymbol> parameters)
    {
        return parameters.Count == 0
            ? null
            : parameters.ToDictionary(static parameter => parameter.Name, StringComparer.Ordinal);
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

            if (coreType.TypeArguments is { Count: > 0 }
                && coreType.TypeArguments.Any(TypeContainsOpenCurrentFunctionGenericParameter))
            {
                return true;
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

        return coreType.ElementType is not null
            && TypeContainsOpenCurrentFunctionGenericParameter(coreType.ElementType);
    }

    private StarkTypeSymbol ResolveLocalConstantDeclarationType(StarkParser.LocalConstantDeclarationContext declaration)
    {
        var key = TemplateLocalDeclarationFacts.BuildLookupKey(
            TemplateLocalDeclarationFacts.ConstantKind,
            declaration.Start.Line,
            declaration.Start.Column + 1);
        var typedDeclaration = _typeModel.LocalDeclarations.LastOrDefault(record =>
            string.Equals(record.EnclosingFunctionName, _currentFunctionName, StringComparison.Ordinal)
            && TemplateLocalDeclarationFacts.BuildLookupKey(record.Kind, record.Location) == key);
        if (typedDeclaration is not null)
        {
            return typedDeclaration.Type;
        }

        return declaration.type_() is { } typeContext
            ? ResolveType(typeContext)
            : StarkTypeSymbols.Error;
    }

    private void DeclareVariable(
        ValidationScope scope,
        VariableSymbol symbol,
        FunctionValidationBuilder summary,
        IToken location)
    {
        scope.Declare(symbol);
        if (RequiresRuntimeDrop(symbol.Type, new HashSet<string>(StringComparer.Ordinal)))
        {
            summary.AddPotentialDropType(symbol.Type, location);
        }
    }

    private void ValidateFunction(DeclaredFunctionSyntax functionDeclaration)
    {
        var name = functionDeclaration.Name;
        if (!_typeModel.Functions.TryGetValue(name, out var signature)
            || !_effectModel.Functions.TryGetValue(name, out var effects)
            || !_syntaxDeclarations.TryGetValue(name, out var syntaxDeclaration)
            || syntaxDeclaration.Function is null)
        {
            return;
        }

        var summary = GetOrCreateSummary(name);
        summary.Configure(signature.ReturnType, syntaxDeclaration.Function.HasBody, syntaxDeclaration.Function.Kind);
        summary.SetParameters(signature.Parameters, signature.DisjointGroups, _typeModel.NamedTypes, _enumLayoutModel.Layouts);
        ApplyBuiltinDeclarationMemoryEffects(syntaxDeclaration.Function, summary);
        ValidateFunctionSignature(functionDeclaration, syntaxDeclaration.Function, signature, effects, summary);

        if (functionDeclaration.Body.block() is not { } block)
        {
            return;
        }

        summary.SetOptimizationSummary(FunctionOptimizationSummaryBuilder.Build(block));

        var previousGenericParameters = _currentFunctionGenericParameters;
        var previousComptimeGenericParameters = _currentFunctionComptimeGenericParameters;
        var previousFunctionName = _currentFunctionName;
        _currentFunctionGenericParameters = signature.IsGeneric
            ? signature.GenericParams.ToHashSet(StringComparer.Ordinal)
            : null;
        _currentFunctionComptimeGenericParameters = signature.ComptimeGenericParams is { Count: > 0 }
            ? signature.ComptimeGenericParams.ToDictionary(static parameter => parameter.Name, StringComparer.Ordinal)
            : null;
        _currentFunctionName = name;

        try
        {
            var scope = ValidationScope.CreateRoot();
            for (var index = 0; index < signature.Parameters.Count; index++)
            {
                var parameter = signature.Parameters[index];
                DeclareVariable(
                    scope,
                    new VariableSymbol(
                        parameter.Name,
                        parameter.Type,
                        SymbolOrigin.Parameter,
                        LocalStorageClass.None,
                        IsMutable: false,
                        IsConstant: false,
                        HasConstProvenance: parameter.IsConst),
                    summary,
                    functionDeclaration.ParameterList.parameter(index).Start);
            }

            CheckBlock(block, scope, syntaxDeclaration.Function, effects, summary, ControlFlowContext.Root);
        }
        finally
        {
            _currentFunctionGenericParameters = previousGenericParameters;
            _currentFunctionComptimeGenericParameters = previousComptimeGenericParameters;
            _currentFunctionName = previousFunctionName;
        }
    }

    private void ValidateTypeDeclarations()
    {
        if (!_context.Options.EnforceIntegerRangeStorageRules)
        {
            return;
        }

        foreach (var declaration in _parseResult.Root.topLevelDeclaration())
        {
            if (declaration.typeAliasDeclaration() is { } typeAliasDeclaration)
            {
                var genericParameters = _typeResolver.GetGenericParameterNames(typeAliasDeclaration.typeParameterList());
                var comptimeGenericParameters = GetTypeAliasComptimeGenericParameterMap(typeAliasDeclaration.Identifier().GetText());
                var aliasedType = ResolveType(typeAliasDeclaration.type_(), genericParameters, comptimeGenericParameters);
                ValidateTypeUsage(typeAliasDeclaration.type_(), aliasedType, TypeUsage.Alias);
                continue;
            }

            if (declaration.structDeclaration() is { } structDeclaration)
            {
                var genericParameters = _typeResolver.GetGenericParameterNames(structDeclaration.typeParameterList());
                var comptimeGenericParameters = GetNamedTypeComptimeGenericParameterMap(structDeclaration.Identifier().GetText());
                var isPlatformAbiBoundary = IsPlatformAbiDeclaration(structDeclaration.Identifier().GetText());
                foreach (var field in structDeclaration.structBody().structMember()
                             .Select(static member => member.fieldDeclaration())
                             .Where(static field => field is not null)!)
                {
                    ValidateFieldDeclarationType(field, genericParameters, comptimeGenericParameters, isPlatformAbiBoundary);
                }

                continue;
            }

            if (declaration.recordDeclaration() is { } recordDeclaration)
            {
                var genericParameters = _typeResolver.GetGenericParameterNames(recordDeclaration.typeParameterList());
                var comptimeGenericParameters = GetNamedTypeComptimeGenericParameterMap(recordDeclaration.Identifier().GetText());
                var isPlatformAbiBoundary = IsPlatformAbiDeclaration(recordDeclaration.Identifier().GetText());

                if (recordDeclaration.primaryConstructorParameters() is { } primaryConstructor)
                {
                    foreach (var parameter in primaryConstructor.parameterList().parameter())
                    {
                        var parameterType = ResolveType(parameter.type_(), genericParameters, comptimeGenericParameters);
                        ValidateTypeUsage(parameter.type_(), parameterType, TypeUsage.Field, isPlatformAbiBoundary: isPlatformAbiBoundary);
                    }
                }

                foreach (var field in recordDeclaration.recordBody().recordMember()
                             .Select(static member => member.fieldDeclaration())
                             .Where(static field => field is not null)!)
                {
                    ValidateFieldDeclarationType(field, genericParameters, comptimeGenericParameters, isPlatformAbiBoundary);
                }

                continue;
            }

            if (declaration.enumDeclaration() is { } enumDeclaration)
            {
                var genericParameters = _typeResolver.GetGenericParameterNames(enumDeclaration.typeParameterList());
                var comptimeGenericParameters = GetNamedTypeComptimeGenericParameterMap(enumDeclaration.Identifier().GetText());
                foreach (var variant in enumDeclaration.enumBody().enumVariantDeclaration())
                {
                    var payload = variant.enumVariantPayload();
                    if (payload is null)
                    {
                        continue;
                    }

                    foreach (var field in payload.enumVariantFieldDeclaration())
                    {
                        var fieldType = ResolveType(field.type_(), genericParameters, comptimeGenericParameters);
                        ValidateTypeUsage(field.type_(), fieldType, TypeUsage.Field);
                    }

                    foreach (var fieldTypeContext in payload.type_())
                    {
                        var fieldType = ResolveType(fieldTypeContext, genericParameters, comptimeGenericParameters);
                        ValidateTypeUsage(fieldTypeContext, fieldType, TypeUsage.Field);
                    }
                }
            }
        }
    }

    // A base list (`struct X : A, B`) names the traits a type implements. Stark
    // has no class-style inheritance, so every base-list entry must resolve to a
    // trait; inheriting from a struct, record, enum, or doctrine is rejected
    // here rather than at the parser, which now accepts the `: ...` syntax for
    // trait implementation. Full member conformance is validated separately.
    // A `dyn trait` promises that every instance method can be dispatched through a
    // fat pointer. Each instance method must therefore be object-safe: a
    // `borrow Self`/`mut borrow Self` receiver, no method-level generics, and no
    // by-value `Self` in parameter or return position. Static (no-self) members are
    // excluded from the vtable and are always allowed.
    private void ValidateDynTraitDeclarations()
    {
        foreach (var declaration in _parseResult.Root.topLevelDeclaration())
        {
            if (declaration.traitDeclaration() is not { } traitDeclaration
                || traitDeclaration.DYN() is null)
            {
                continue;
            }

            var traitName = traitDeclaration.Identifier().GetText();
            if (_typeModel.NamedTypes.TryGetValue(traitName, out var traitSymbol)
                && traitSymbol.AssociatedTypes.Count > 0)
            {
                _context.Diagnostics.Error(
                    "STK3036",
                    $"Dyn trait '{traitName}' declares associated types. Trait objects cannot hide associated-type bindings; keep the trait static-only or add an explicit dyn-object contract first.",
                    "semantic-validate",
                    Location(traitDeclaration));
            }

            foreach (var member in traitDeclaration.traitBody().traitMember())
            {
                if (member.traitMethodDeclaration() is not { } method)
                {
                    continue;
                }

                var methodName = method.Identifier().GetText();
                if (!_typeModel.Functions.TryGetValue($"{traitName}.{methodName}", out var signature)
                    || DynTraitFacts.TryValidateDynTraitMethod(signature, out var reason))
                {
                    continue;
                }

                _context.Diagnostics.Error(
                    "STK3036",
                    $"Trait method '{traitName}.{methodName}' is not object-safe and cannot appear in 'dyn trait {traitName}': {reason}.",
                    "semantic-validate",
                    Location(method));
            }
        }
    }

    private void ValidateBaseTraitLists()
    {
        var traitRequiredMethods = CollectTraitRequiredMethods();

        foreach (var declaration in _parseResult.Root.topLevelDeclaration())
        {
            StarkParser.BaseTraitListContext? baseList;
            ISet<string>? genericParameters;
            string ownerName;
            HashSet<string> implementedMethodNames;

            if (declaration.structDeclaration() is { } structDeclaration)
            {
                baseList = structDeclaration.baseTraitList();
                genericParameters = _typeResolver.GetGenericParameterNames(structDeclaration.typeParameterList());
                ownerName = structDeclaration.Identifier().GetText();
                implementedMethodNames = CollectMemberMethodNames(
                    structDeclaration.structBody().structMember()
                        .Select(static member => member.methodDeclaration()));
            }
            else if (declaration.recordDeclaration() is { } recordDeclaration)
            {
                baseList = recordDeclaration.baseTraitList();
                genericParameters = _typeResolver.GetGenericParameterNames(recordDeclaration.typeParameterList());
                ownerName = recordDeclaration.Identifier().GetText();
                implementedMethodNames = CollectMemberMethodNames(
                    recordDeclaration.recordBody().recordMember()
                        .Select(static member => member.methodDeclaration()));
            }
            else
            {
                continue;
            }

            if (baseList is null)
            {
                continue;
            }

            foreach (var entry in baseList.type_())
            {
                var resolved = ResolveType(entry, genericParameters);
                var named = ResolveNamedTypeSymbol(resolved);
                if (named is null)
                {
                    // Unknown or unresolved base names are reported by type
                    // resolution; do not double-report them here.
                    continue;
                }

                if (named.Kind != DeclarationKind.Trait)
                {
                    _context.Diagnostics.Error(
                        "STK3026",
                        $"'{ownerName}' cannot inherit from '{named.Name}'. Stark has no class-style inheritance; only traits may appear in a base list.",
                        "semantic-validate",
                        Location(entry));
                    continue;
                }

                _typeModel.NamedTypes.TryGetValue(ownerName, out var ownerType);
                foreach (var requiredAssociatedType in named.AssociatedTypes.Values.Where(static associatedType => associatedType.IsRequired))
                {
                    if (ownerType is null
                        || !ownerType.AssociatedTypes.TryGetValue(requiredAssociatedType.Name, out var implementationAssociatedType)
                        || implementationAssociatedType.TargetType is null)
                    {
                        _context.Diagnostics.Error(
                            "STK3052",
                            $"'{ownerName}' does not define associated type '{requiredAssociatedType.Name}' required by trait '{named.Name}'. Add 'alias {requiredAssociatedType.Name} = <type>;' to '{ownerName}'.",
                            "semantic-validate",
                            Location(entry));
                    }
                }

                // Member conformance is model-driven so imported source modules
                // and package-backed modules use the same trait signatures as
                // same-module declarations.
                if (traitRequiredMethods.TryGetValue(named.Name, out var requiredMethods))
                {
                    foreach (var requiredMethod in requiredMethods)
                    {
                        var requiredMethodName = LastNameSegment(requiredMethod.Name);
                        if (!implementedMethodNames.Contains(requiredMethodName))
                        {
                            _context.Diagnostics.Error(
                                "STK3032",
                                $"'{ownerName}' does not implement trait method '{requiredMethod.DisplaySourceName}' required by trait '{named.Name}'.",
                                "semantic-validate",
                                Location(entry));
                            continue;
                        }

                        if (_typeModel.Functions.TryGetValue($"{ownerName}.{requiredMethodName}", out var implSignature))
                        {
                            if (requiredMethod.Parameters.Count != implSignature.Parameters.Count)
                            {
                                _context.Diagnostics.Error(
                                    "STK3033",
                                    $"'{ownerName}.{requiredMethodName}' does not match trait method '{requiredMethod.DisplaySourceName}': the trait requires {requiredMethod.Parameters.Count} parameter(s) but the implementation has {implSignature.Parameters.Count}.",
                                    "semantic-validate",
                                    Location(entry));
                            }
                            else if (!TypeCompatibilityFacts.FunctionKindSatisfies(implSignature.Kind, requiredMethod.Kind))
                            {
                                _context.Diagnostics.Error(
                                    "STK3033",
                                    $"'{ownerName}.{requiredMethodName}' must be '{DescribeFunctionKind(requiredMethod.Kind)}' to satisfy trait method '{requiredMethod.DisplaySourceName}'.",
                                    "semantic-validate",
                                    Location(entry));
                            }
                            else if (!TraitMethodSignatureConforms(requiredMethod, implSignature, named, resolved))
                            {
                                _context.Diagnostics.Error(
                                    "STK3033",
                                    $"'{ownerName}.{requiredMethodName}' does not match the parameter or return types of trait method '{requiredMethod.DisplaySourceName}' (after substituting 'Self' and trait type arguments).",
                                    "semantic-validate",
                                    Location(entry));
                            }
                        }
                    }
                }
            }
        }
    }

    // Collects required (non-default) instance methods for every loaded trait.
    // A trait method with a body is a default and is not required of implementers;
    // one with no body is required. Type checking has already built typed
    // signatures for source imports and package-backed imports, so this stays
    // cross-module without re-walking imported parse trees.
    private Dictionary<string, List<TypedFunctionSignature>> CollectTraitRequiredMethods()
    {
        var result = new Dictionary<string, List<TypedFunctionSignature>>(StringComparer.Ordinal);
        foreach (var function in _typeModel.Functions.Values.OrderBy(static function => function.Name, StringComparer.Ordinal))
        {
            if (function.IsStatic || function.HasBody)
            {
                continue;
            }

            var lastDot = function.Name.LastIndexOf('.');
            if (lastDot <= 0)
            {
                continue;
            }

            var containingTypeName = function.Name[..lastDot];
            if (!_typeModel.NamedTypes.TryGetValue(containingTypeName, out var containingType)
                || containingType.Kind != DeclarationKind.Trait)
            {
                continue;
            }

            if (!result.TryGetValue(containingType.Name, out var methods))
            {
                methods = [];
                result[containingType.Name] = methods;
            }

            methods.Add(function);
        }

        return result;
    }

    private static HashSet<string> CollectMemberMethodNames(
        IEnumerable<StarkParser.MethodDeclarationContext?> methods)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var method in methods)
        {
            if (method is not null)
            {
                names.Add(method.Identifier().GetText());
            }
        }

        return names;
    }

    private static string LastNameSegment(string qualifiedName)
    {
        var lastDot = qualifiedName.LastIndexOf('.');
        return lastDot >= 0 ? qualifiedName[(lastDot + 1)..] : qualifiedName;
    }

    private static string DescribeFunctionKind(StarkFunctionKind kind)
    {
        return (FunctionKindFacts.IsFinite(kind), FunctionKindFacts.IsLaw(kind)) switch
        {
            (true, true) => "finite law",
            (false, true) => "law",
            (true, false) => "finite",
            _ => "fn"
        };
    }

    // Verifies the implementing method's parameter and return types match the
    // trait method's after substituting the trait's type parameters with the
    // base-list type arguments and `Self` with the implementing type (taken from
    // the impl's own receiver, so name/qualifier forms stay consistent).
    private bool TraitMethodSignatureConforms(
        TypedFunctionSignature traitSignature,
        TypedFunctionSignature implSignature,
        NamedTypeSymbol traitType,
        StarkTypeSymbol resolvedTrait)
    {
        var substitution = new Dictionary<string, StarkTypeSymbol>(StringComparer.Ordinal);
        var traitTypeParams = traitType.GenericParams;
        if (resolvedTrait.TypeArguments is { } traitArgs)
        {
            for (var index = 0; index < traitTypeParams.Count && index < traitArgs.Count; index++)
            {
                substitution[traitTypeParams[index]] = traitArgs[index];
            }
        }

        if (implSignature.Parameters.Count > 0)
        {
            substitution["Self"] = implSignature.Parameters[0].Type;
        }

        StarkTypeSymbol? ResolveAssociatedType(StarkTypeSymbol ownerType, string associatedTypeName)
        {
            if (AssociatedTypeFacts.TryResolveAssociatedType(ownerType, associatedTypeName, _typeModel.NamedTypes, out var targetType))
            {
                return targetType;
            }

            if (traitType.AssociatedTypes.TryGetValue(associatedTypeName, out var traitAssociatedType)
                && traitAssociatedType.TargetType is not null)
            {
                return FunctionOverloadFacts.SubstituteType(
                    traitAssociatedType.TargetType,
                    substitution,
                    ResolveAssociatedType);
            }

            return null;
        }

        for (var index = 0; index < traitSignature.Parameters.Count; index++)
        {
            var expected = FunctionOverloadFacts.SubstituteType(
                traitSignature.Parameters[index].Type,
                substitution,
                ResolveAssociatedType);
            if (!TraitTypesEquivalent(expected, implSignature.Parameters[index].Type))
            {
                return false;
            }
        }

        var expectedReturn = FunctionOverloadFacts.SubstituteType(
            traitSignature.ReturnType,
            substitution,
            ResolveAssociatedType);
        return TraitTypesEquivalent(expectedReturn, implSignature.ReturnType);
    }

    // Structural type comparison for conformance. Ignores incidental fields such
    // as DisplayName and compares only semantically meaningful shape: kind, named
    // identity, width/range/sign, pointer/borrow/access/init qualifiers, element
    // type, type arguments, and function-pointer/closure parts.
    private static bool TraitTypesEquivalent(StarkTypeSymbol left, StarkTypeSymbol right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left.Kind != right.Kind
            || !string.Equals(left.NamedType, right.NamedType, StringComparison.Ordinal)
            || left.BitWidth != right.BitWidth
            || left.IsUnsigned != right.IsUnsigned
            || left.RangeMin != right.RangeMin
            || left.RangeMax != right.RangeMax
            || left.FixedLength != right.FixedLength
            || left.IsMutablePointer != right.IsMutablePointer
            || left.BorrowKind != right.BorrowKind
            || left.AccessKind != right.AccessKind
            || left.InitializationKind != right.InitializationKind
            || left.IsMutableView != right.IsMutableView
            || left.FunctionPointerKind != right.FunctionPointerKind
            || left.FunctionPointerAbi != right.FunctionPointerAbi
            || left.FunctionPointerIsUnsafe != right.FunctionPointerIsUnsafe
            || left.ClosureFunctionKind != right.ClosureFunctionKind
            || left.ClosureStorageKind != right.ClosureStorageKind
            || left.ClosureCallCapability != right.ClosureCallCapability
            || !string.Equals(left.AssociatedTypeName, right.AssociatedTypeName, StringComparison.Ordinal))
        {
            return false;
        }

        if (!OptionalTypeEquivalent(left.ElementType, right.ElementType)
            || !OptionalTypeEquivalent(left.FunctionPointerReturnType, right.FunctionPointerReturnType)
            || !OptionalTypeEquivalent(left.ClosureReturnType, right.ClosureReturnType)
            || !OptionalTypeEquivalent(left.AssociatedTypeOwner, right.AssociatedTypeOwner))
        {
            return false;
        }

        return TypeListEquivalent(left.TypeArguments, right.TypeArguments)
            && TypeListEquivalent(left.FunctionPointerParameterTypes, right.FunctionPointerParameterTypes)
            && TypeListEquivalent(left.ClosureParameterTypes, right.ClosureParameterTypes);
    }

    private static bool OptionalTypeEquivalent(StarkTypeSymbol? left, StarkTypeSymbol? right)
    {
        if (left is null)
        {
            return right is null;
        }

        return right is not null && TraitTypesEquivalent(left, right);
    }

    private static bool TypeListEquivalent(
        IReadOnlyList<StarkTypeSymbol>? left,
        IReadOnlyList<StarkTypeSymbol>? right)
    {
        if (left is null)
        {
            return right is null || right.Count == 0;
        }

        if (right is null)
        {
            return left.Count == 0;
        }

        if (left.Count != right.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Count; index++)
        {
            if (!TraitTypesEquivalent(left[index], right[index]))
            {
                return false;
            }
        }

        return true;
    }

    private void ValidateFieldDeclarationType(
        StarkParser.FieldDeclarationContext field,
        ISet<string>? genericParameters,
        IReadOnlyDictionary<string, ComptimeGenericParameterSymbol>? comptimeGenericParameters,
        bool isPlatformAbiBoundary)
    {
        var fieldType = ResolveType(field.type_(), genericParameters, comptimeGenericParameters);
        ValidateTypeUsage(field.type_(), fieldType, TypeUsage.Field, isPlatformAbiBoundary: isPlatformAbiBoundary);
    }

    private void ValidateFunctionSignature(
        DeclaredFunctionSyntax functionDeclaration,
        FunctionDeclarationModel declaration,
        TypedFunctionSignature signature,
        FunctionEffectProfile effects,
        FunctionValidationBuilder summary)
    {
        ValidateFunctionModifiers(functionDeclaration, summary);
        ValidatePublicSafeApiDoesNotExposeRawAllocation(functionDeclaration, declaration, signature, summary);
        var isPlatformAbiBoundary = IsPlatformAbiDeclaration(functionDeclaration, declaration);
        ValidateTypeUsage(
            functionDeclaration.ReturnType,
            signature.ReturnType,
            TypeUsage.Return,
            isFfiBoundary: declaration.Modifiers.IsFfi,
            isPlatformAbiBoundary: isPlatformAbiBoundary);

        if (signature.ReturnType.BorrowKind == StarkBorrowKind.Borrow)
        {
            BorrowError(
                summary,
                "STK4000",
                $"Function '{signature.Name}' cannot return a plain 'borrow' value. Use 'retborrow' or 'storeborrow'.",
                functionDeclaration.ReturnType);
        }

        if (signature.ReturnType.InitializationKind != StarkInitializationKind.None)
        {
            summary.DisqualifyLaw();

            if (effects.IsPure)
            {
                EffectError(summary, "STK4100", $"Law '{signature.Name}' cannot return an 'out' or 'init' type.", functionDeclaration.ReturnType);
            }
        }

        for (var index = 0; index < functionDeclaration.ParameterList.parameter().Length; index++)
        {
            var parameterContext = functionDeclaration.ParameterList.parameter(index);
            var parameter = signature.Parameters[index];

            ValidateTypeUsage(
                parameterContext.type_(),
                parameter.Type,
                TypeUsage.Parameter,
                isFfiBoundary: declaration.Modifiers.IsFfi,
                isPlatformAbiBoundary: isPlatformAbiBoundary);

            if (parameter.Type.InitializationKind != StarkInitializationKind.None)
            {
                summary.DisqualifyLaw();

                if (effects.IsPure)
                {
                    EffectError(
                        summary,
                        "STK4101",
                        $"Law '{signature.Name}' cannot declare 'out' or 'init' parameters.",
                        parameterContext.type_());
                }
            }
        }
    }

    private void ValidatePublicSafeApiDoesNotExposeRawAllocation(
        DeclaredFunctionSyntax functionDeclaration,
        FunctionDeclarationModel declaration,
        TypedFunctionSignature signature,
        FunctionValidationBuilder summary)
    {
        if (declaration.Modifiers.IsFfi
            || !IsPublicSafeSurface(functionDeclaration.Visibility)
            || !LooksLikeRawAllocationApi(functionDeclaration.DisplaySourceName)
            || !SignatureContainsRawPointer(signature))
        {
            return;
        }

        EffectError(
            summary,
            "STK4118",
            $"Public safe API '{functionDeclaration.DisplaySourceName}' exposes raw allocation through raw pointer types. Keep raw allocation behind internal/FFI-adjacent runtime APIs and expose a safe owner type instead.",
            functionDeclaration.DeclarationContext);
    }

    private void ValidateFunctionModifiers(DeclaredFunctionSyntax functionDeclaration, FunctionValidationBuilder summary)
    {
        ValidateMemberVisibility(functionDeclaration, summary);

        if (functionDeclaration.IsStatic
            && functionDeclaration.DeclarationContext is not StarkParser.MethodDeclarationContext)
        {
            EffectError(
                summary,
                "STK4115",
                "Function modifier 'static' is only valid on member functions inside 'struct' or 'record' declarations.",
                functionDeclaration.DeclarationContext);
        }

        var inlineModifiers = functionDeclaration.Modifiers
            .Where(static modifier =>
            {
                var text = modifier.GetText();
                return text is "inline" or "noinline" or "inlinehint";
            })
            .Select(static modifier => modifier.GetText())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (inlineModifiers.Length > 1)
        {
            EffectError(
                summary,
                "STK4109",
                $"Function '{functionDeclaration.DisplaySourceName}' may use at most one of 'inline', 'noinline', or 'inlinehint'. Found: {string.Join(", ", inlineModifiers.Select(static modifier => $"'{modifier}'"))}.",
                functionDeclaration.DeclarationContext);
        }

        var hasHot = functionDeclaration.Modifiers.Any(static modifier => string.Equals(modifier.GetText(), "hot", StringComparison.Ordinal));
        var hasCold = functionDeclaration.Modifiers.Any(static modifier => string.Equals(modifier.GetText(), "cold", StringComparison.Ordinal));

        if (hasHot && hasCold)
        {
            EffectError(
                summary,
                "STK4110",
                $"Function '{functionDeclaration.DisplaySourceName}' may not combine 'hot' and 'cold'.",
                functionDeclaration.DeclarationContext);
        }

        var hasVarargs = functionDeclaration.Modifiers.Any(static modifier => string.Equals(modifier.GetText(), "varargs", StringComparison.Ordinal));
        if (!hasVarargs)
        {
            return;
        }

        var hasFfi = functionDeclaration.Modifiers.Any(FfiAbiSyntaxFacts.IsFfiModifier);
        if (!hasFfi)
        {
            EffectError(
                summary,
                "STK4119",
                $"Function '{functionDeclaration.DisplaySourceName}' uses 'varargs', which is only available for 'ffi' functions. Write 'ffi varargs fn' for C-style variadic imports.",
                functionDeclaration.DeclarationContext);
        }

        if (functionDeclaration.HasBody)
        {
            EffectError(
                summary,
                "STK4119",
                $"Function '{functionDeclaration.DisplaySourceName}' uses 'varargs', so it must be an FFI declaration ending with ';', not a Stark function body.",
                functionDeclaration.DeclarationContext);
        }
    }

    private void ValidateMemberVisibility(DeclaredFunctionSyntax functionDeclaration, FunctionValidationBuilder summary)
    {
        if (functionDeclaration.DeclarationContext is not StarkParser.MethodDeclarationContext
            || functionDeclaration.EnclosingTypeVisibility is not { } enclosingVisibility)
        {
            return;
        }

        if (IsMoreVisible(functionDeclaration.Visibility, enclosingVisibility))
        {
            EffectError(
                summary,
                "STK4116",
                $"Member function '{functionDeclaration.DisplaySourceName}' has visibility '{RenderVisibility(functionDeclaration.Visibility)}', which is more visible than its enclosing type visibility '{RenderVisibility(enclosingVisibility)}'.",
                functionDeclaration.DeclarationContext);
        }

        if (functionDeclaration.Visibility == StarkVisibility.Export && !functionDeclaration.HasExplicitVisibility)
        {
            EffectError(
                summary,
                "STK4117",
                $"Member function '{functionDeclaration.DisplaySourceName}' must write 'export' explicitly to become ABI-visible.",
                functionDeclaration.DeclarationContext);
        }
    }

    private static bool IsMoreVisible(StarkVisibility memberVisibility, StarkVisibility enclosingVisibility)
    {
        return VisibilityRank(memberVisibility) > VisibilityRank(enclosingVisibility);
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

    private static bool IsPublicSafeSurface(StarkVisibility visibility)
    {
        return visibility is StarkVisibility.Public or StarkVisibility.Export;
    }

    private static bool SignatureContainsRawPointer(TypedFunctionSignature signature)
    {
        return ContainsRawPointer(signature.ReturnType)
            || signature.Parameters.Any(static parameter => ContainsRawPointer(parameter.Type));
    }

    private static bool LooksLikeRawAllocationApi(string name)
    {
        var simpleName = name.Split('.').Last();
        return simpleName.Contains("Alloc", StringComparison.OrdinalIgnoreCase)
            || simpleName.Contains("Free", StringComparison.OrdinalIgnoreCase)
            || simpleName.Contains("Dealloc", StringComparison.OrdinalIgnoreCase)
            || simpleName.Contains("Realloc", StringComparison.OrdinalIgnoreCase);
    }

    private void CheckBlock(
        StarkParser.BlockContext block,
        ValidationScope parentScope,
        FunctionDeclarationModel function,
        FunctionEffectProfile effects,
        FunctionValidationBuilder summary,
        ControlFlowContext controlFlow)
    {
        var scope = new ValidationScope(parentScope);
        foreach (var statement in block.statement())
        {
            CheckStatement(statement, scope, function, effects, summary, controlFlow);
        }
    }

    private void CheckStatement(
        StarkParser.StatementContext statement,
        ValidationScope scope,
        FunctionDeclarationModel function,
        FunctionEffectProfile effects,
        FunctionValidationBuilder summary,
        ControlFlowContext controlFlow)
    {
        if (statement.block() is { } nestedBlock)
        {
            CheckBlock(nestedBlock, scope, function, effects, summary, controlFlow);
            return;
        }

        if (statement.unsafeStatement() is { } unsafeStatement)
        {
            if (unsafeStatement.block() is { } unsafeBlock)
            {
                CheckBlock(unsafeBlock, scope, function, effects, summary, controlFlow);
            }
            else if (unsafeStatement.assumeStatement() is { } unsafeAssumeStatement)
            {
                CheckAssumeStatement(unsafeAssumeStatement, scope, function, effects, summary, controlFlow);
            }

            return;
        }

        if (statement.assumeStatement() is { } assumeStatement)
        {
            CheckAssumeStatement(assumeStatement, scope, function, effects, summary, controlFlow);
            return;
        }

        if (statement.localConstantDeclaration() is { } constantDeclaration)
        {
            var declaredType = ResolveLocalConstantDeclarationType(constantDeclaration);
            ValidateTypeUsage(constantDeclaration.type_() ?? (ParserRuleContext)constantDeclaration, declaredType, TypeUsage.Local);

            foreach (var declarator in constantDeclaration.constantDeclarators().constantDeclarator())
            {
                var hasConstProvenance = false;
                if (declarator.variableInitializer() is { } initializer)
                {
                    hasConstProvenance = CheckVariableInitializer(initializer, scope, function, effects, summary, declaredType);
                }

                DeclareVariable(
                    scope,
                    new VariableSymbol(
                        declarator.Identifier().GetText(),
                        declaredType,
                        SymbolOrigin.Local,
                        LocalStorageClass.None,
                        IsMutable: false,
                        IsConstant: true,
                        HasConstProvenance: hasConstProvenance),
                    summary,
                    declarator.Identifier().Symbol);
            }

            return;
        }

        if (statement.localVariableDeclaration() is { } localVariable)
        {
            var storageClass = ParseStorageClass(localVariable.storageClass());
            ValidateLocalVariableStorageClass(storageClass, localVariable.storageClass());
            var declaredType = ResolveType(localVariable.type_());
            ValidateTypeUsage(localVariable.type_(), declaredType, TypeUsage.Local);

            if (storageClass is LocalStorageClass.Heap or LocalStorageClass.Arena or LocalStorageClass.Static)
            {
                summary.DisqualifyLaw();

                if (effects.IsPure)
                {
                    EffectError(summary, "STK4102", $"Law '{function.Name}' cannot allocate or publish local '{storageClass.ToString().ToLowerInvariant()}' storage.", localVariable.storageClass());
                }
            }

            foreach (var declarator in localVariable.variableDeclarators().variableDeclarator())
            {
                var hasConstProvenance = false;
                if (declarator.variableInitializer() is { } initializer)
                {
                    hasConstProvenance = CheckVariableInitializer(initializer, scope, function, effects, summary, declaredType);
                }

                DeclareVariable(
                    scope,
                    new VariableSymbol(
                        declarator.Identifier().GetText(),
                        declaredType,
                        SymbolOrigin.Local,
                        storageClass,
                        IsMutable: localVariable.MUT() is not null,
                        IsConstant: false,
                        HasConstProvenance: localVariable.MUT() is null && hasConstProvenance),
                    summary,
                    declarator.Identifier().Symbol);
            }

            return;
        }

        if (statement.ifStatement() is { } ifStatement)
        {
            if (ifStatement.expression() is { } condition)
            {
                EvaluateExpression(condition, scope, function, effects, summary, allowFunctionReference: false, ExpressionObservation.Read);
            }
            else if (ifStatement.disjointRuntimeCondition() is { } disjointCondition)
            {
                foreach (var expression in disjointCondition.expressionList().expression())
                {
                    if (TryEvaluateRawPointerRegionExpression(expression, scope, function, effects, summary))
                    {
                        continue;
                    }

                    EvaluateExpression(expression, scope, function, effects, summary, allowFunctionReference: false, ExpressionObservation.Read);
                }
            }

            CheckStatement(ifStatement.statement(0), new ValidationScope(scope), function, effects, summary, controlFlow);
            if (ifStatement.statement().Length > 1)
            {
                CheckStatement(ifStatement.statement(1), new ValidationScope(scope), function, effects, summary, controlFlow);
            }

            return;
        }

        if (statement.labeledStatement() is { } labeledStatement)
        {
            CheckLabeledStatement(labeledStatement, scope, function, effects, summary, controlFlow);
            return;
        }

        if (statement.switchStatement() is { } switchStatement)
        {
            CheckSwitchStatement(switchStatement, scope, function, effects, summary, controlFlow, labelName: null);
            return;
        }

        if (statement.whileStatement() is { } whileStatement)
        {
            CheckWhileStatement(whileStatement, scope, function, effects, summary, controlFlow, labelName: null);
            return;
        }

        if (statement.forStatement() is { } forStatement)
        {
            CheckForStatement(forStatement, scope, function, effects, summary, controlFlow, labelName: null);
            return;
        }

        if (statement.returnStatement() is { } returnStatement)
        {
            if (returnStatement.expression() is not null)
            {
                var returnedValue = EvaluateExpression(
                    returnStatement.expression(),
                    scope,
                    function,
                    effects,
                    summary,
                    allowFunctionReference: false,
                    ExpressionObservation.Read);
                RecordReturnCapture(returnedValue, function, summary);
            }

            return;
        }

        if (statement.breakStatement() is { } breakStatement)
        {
            var labelName = breakStatement.Identifier()?.GetText();
            if (labelName is null)
            {
                if (!controlFlow.CanBreak)
                {
                    EffectError(summary, "STK4113", "'break' requires an enclosing loop or switch.", breakStatement);
                }
            }
            else if (!controlFlow.CanBreakToLabel(labelName))
            {
                EffectError(summary, "STK4113", $"'break {labelName}' requires an enclosing loop or switch labeled '{labelName}'.", breakStatement);
            }

            return;
        }

        if (statement.continueStatement() is { } continueStatement)
        {
            var labelName = continueStatement.Identifier()?.GetText();
            if (labelName is null)
            {
                if (!controlFlow.CanContinue)
                {
                    EffectError(summary, "STK4114", "'continue' requires an enclosing loop.", continueStatement);
                }
            }
            else if (!controlFlow.CanContinueToLabel(labelName))
            {
                EffectError(summary, "STK4114", $"'continue {labelName}' requires an enclosing loop labeled '{labelName}'.", continueStatement);
            }

            return;
        }

        if (statement.expressionStatement() is { } expressionStatement)
        {
            EvaluateExpression(
                expressionStatement.expression(),
                scope,
                function,
                effects,
                summary,
                allowFunctionReference: false,
                ExpressionObservation.Read);
        }
    }

    private void CheckLabeledStatement(
        StarkParser.LabeledStatementContext labeledStatement,
        ValidationScope scope,
        FunctionDeclarationModel function,
        FunctionEffectProfile effects,
        FunctionValidationBuilder summary,
        ControlFlowContext controlFlow)
    {
        var labelName = labeledStatement.Identifier().GetText();
        if (controlFlow.HasLabel(labelName))
        {
            EffectError(summary, "STK4120", $"Control-flow label '{labelName}' is already active in this scope.", labeledStatement.Identifier().Symbol);
        }

        if (labeledStatement.switchStatement() is { } switchStatement)
        {
            CheckSwitchStatement(switchStatement, scope, function, effects, summary, controlFlow, labelName);
            return;
        }

        if (labeledStatement.whileStatement() is { } whileStatement)
        {
            CheckWhileStatement(whileStatement, scope, function, effects, summary, controlFlow, labelName);
            return;
        }

        if (labeledStatement.forStatement() is { } forStatement)
        {
            CheckForStatement(forStatement, scope, function, effects, summary, controlFlow, labelName);
        }
    }

    private void CheckSwitchStatement(
        StarkParser.SwitchStatementContext switchStatement,
        ValidationScope scope,
        FunctionDeclarationModel function,
        FunctionEffectProfile effects,
        FunctionValidationBuilder summary,
        ControlFlowContext controlFlow,
        string? labelName)
    {
        var switchValue = EvaluateExpression(switchStatement.expression(), scope, function, effects, summary, allowFunctionReference: false, ExpressionObservation.Read);
        var switchControlFlow = controlFlow.EnterSwitch(labelName);

        foreach (var section in switchStatement.switchSection())
        {
            var sectionScope = new ValidationScope(scope);
            foreach (var label in section.switchLabel())
            {
                foreach (var pattern in label.pattern())
                {
                    BindSwitchPattern(pattern, switchValue.Type, sectionScope, summary);
                }

                if (label.whenClause() is { } whenClause)
                {
                    EvaluateExpression(whenClause.expression(), sectionScope, function, effects, summary, allowFunctionReference: false, ExpressionObservation.Read);
                }
            }

            foreach (var nestedStatement in section.statement())
            {
                CheckStatement(nestedStatement, sectionScope, function, effects, summary, switchControlFlow);
            }
        }
    }

    private void CheckWhileStatement(
        StarkParser.WhileStatementContext whileStatement,
        ValidationScope scope,
        FunctionDeclarationModel function,
        FunctionEffectProfile effects,
        FunctionValidationBuilder summary,
        ControlFlowContext controlFlow,
        string? labelName)
    {
        ValidateLoopContract(function.Name, whileStatement.loopBehavior().GetText(), whileStatement.expression(), whileStatement.statement(), whileStatement.loopBehavior(), summary, labelName);

        if (whileStatement.loopBehavior().GetText() != "willexit")
        {
            summary.DisqualifyFinite();

            if (effects.WillReturn)
            {
                EffectError(summary, "STK4103", $"Finite function '{function.Name}' may only use 'willexit' loops.", whileStatement.loopBehavior());
            }
        }

        EvaluateExpression(whileStatement.expression(), scope, function, effects, summary, allowFunctionReference: false, ExpressionObservation.Read);
        CheckStatement(whileStatement.statement(), new ValidationScope(scope), function, effects, summary, controlFlow.EnterLoop(labelName));
    }

    private void CheckForStatement(
        StarkParser.ForStatementContext forStatement,
        ValidationScope scope,
        FunctionDeclarationModel function,
        FunctionEffectProfile effects,
        FunctionValidationBuilder summary,
        ControlFlowContext controlFlow,
        string? labelName)
    {
        var forTraversal = forStatement.forTraversal();
        ValidateLoopContract(function.Name, forStatement.loopBehavior().GetText(), forTraversal?.expression() ?? forStatement.forCondition()?.expression(), forStatement.statement(), forStatement.loopBehavior(), summary, labelName);

        if (forStatement.loopBehavior().GetText() != "willexit")
        {
            summary.DisqualifyFinite();

            if (effects.WillReturn)
            {
                EffectError(summary, "STK4103", $"Finite function '{function.Name}' may only use 'willexit' loops.", forStatement.loopBehavior());
            }
        }

        var loopScope = new ValidationScope(scope);

        if (forTraversal is not null)
        {
            CheckForTraversal(forTraversal, loopScope, function, effects, summary);
        }
        else if (forStatement.forInitializer()?.localForVariableDeclaration() is { } localForDeclaration)
        {
            var storageClass = ParseStorageClass(localForDeclaration.storageClass());
            ValidateLocalVariableStorageClass(storageClass, localForDeclaration.storageClass());
            var declaredType = ResolveType(localForDeclaration.type_());
            ValidateTypeUsage(localForDeclaration.type_(), declaredType, TypeUsage.Local);

            if (storageClass is LocalStorageClass.Heap or LocalStorageClass.Arena or LocalStorageClass.Static)
            {
                summary.DisqualifyLaw();

                if (effects.IsPure)
                {
                    EffectError(summary, "STK4102", $"Law '{function.Name}' cannot allocate or publish local '{storageClass.ToString().ToLowerInvariant()}' storage.", localForDeclaration.storageClass());
                }
            }

            foreach (var declarator in localForDeclaration.variableDeclarators().variableDeclarator())
            {
                var hasConstProvenance = false;
                if (declarator.variableInitializer() is { } initializer)
                {
                    hasConstProvenance = CheckVariableInitializer(initializer, loopScope, function, effects, summary, declaredType);
                }

                DeclareVariable(
                    loopScope,
                    new VariableSymbol(
                        declarator.Identifier().GetText(),
                        declaredType,
                        SymbolOrigin.Local,
                        storageClass,
                        IsMutable: localForDeclaration.MUT() is not null,
                        IsConstant: false,
                        HasConstProvenance: localForDeclaration.MUT() is null && hasConstProvenance),
                    summary,
                    declarator.Identifier().Symbol);
            }
        }
        else if (forStatement.forInitializer()?.expressionList() is { } initializerExpressions)
        {
            foreach (var expression in initializerExpressions.expression())
            {
                EvaluateExpression(expression, loopScope, function, effects, summary, allowFunctionReference: false, ExpressionObservation.Read);
            }
        }

        if (forTraversal is null && forStatement.forCondition() is { } condition)
        {
            EvaluateExpression(condition.expression(), loopScope, function, effects, summary, allowFunctionReference: false, ExpressionObservation.Read);
        }

        if (forTraversal is null && forStatement.forIterator() is { } iterator)
        {
            foreach (var expression in iterator.expressionList().expression())
            {
                EvaluateExpression(expression, loopScope, function, effects, summary, allowFunctionReference: false, ExpressionObservation.Read);
            }
        }

        CheckStatement(forStatement.statement(), loopScope, function, effects, summary, controlFlow.EnterLoop(labelName));
    }

    private void CheckForTraversal(
        StarkParser.ForTraversalContext traversal,
        ValidationScope loopScope,
        FunctionDeclarationModel function,
        FunctionEffectProfile effects,
        FunctionValidationBuilder summary)
    {
        EvaluateExpression(traversal.expression(), loopScope, function, effects, summary, allowFunctionReference: false, ExpressionObservation.Read);

        if (traversal.traversalIndexBinding() is { } indexBinding)
        {
            var storageClass = ParseStorageClass(indexBinding.storageClass());
            ValidateLocalVariableStorageClass(storageClass, indexBinding.storageClass());
            var indexType = ResolveType(indexBinding.type_());
            ValidateTypeUsage(indexBinding.type_(), indexType, TypeUsage.Local);

            DeclareVariable(
                loopScope,
                new VariableSymbol(
                    indexBinding.Identifier().GetText(),
                    indexType,
                    SymbolOrigin.Local,
                    storageClass,
                    IsMutable: false,
                    IsConstant: false),
                summary,
                indexBinding.Identifier().Symbol);
        }

        var elementBinding = traversal.traversalElementBinding();
        var elementType = ResolveType(elementBinding.type_());
        ValidateTypeUsage(elementBinding.type_(), elementType, TypeUsage.Local);
        DeclareVariable(
            loopScope,
            new VariableSymbol(
                elementBinding.Identifier().GetText(),
                elementType,
                SymbolOrigin.Local,
                LocalStorageClass.None,
                IsMutable: false,
                IsConstant: false),
            summary,
            elementBinding.Identifier().Symbol);
    }

    private bool CheckVariableInitializer(
        StarkParser.VariableInitializerContext initializer,
        ValidationScope scope,
        FunctionDeclarationModel function,
        FunctionEffectProfile effects,
        FunctionValidationBuilder summary,
        StarkTypeSymbol? expectedType = null)
    {
        if (initializer.expression() is { } expression)
        {
            if (expectedType is not null
                && IsTextBufferType(expectedType)
                && TryGetStandaloneInterpolatedTextLiteral(expression) is { } interpolatedLiteral)
            {
                CheckFixedTextStorageInterpolation(interpolatedLiteral, expectedType, scope, function, effects, summary);
                return false;
            }

            var value = EvaluateExpression(expression, scope, function, effects, summary, allowFunctionReference: false, ExpressionObservation.Read);
            if (expectedType is not null)
            {
                ValidateRegisterStorageBackedUse(value, expectedType, expression);
                if (expectedType.Kind == StarkTypeKind.Dynamic)
                {
                    RecordDynamicStorageRuntimeEffects(function, effects, summary, expression);
                }
            }

            return HasConstProvenance(value);
        }

        if (initializer.objectInitializer() is { } objectInitializer)
        {
            var allMembersHaveConstProvenance = objectInitializer.memberInitializer().Length > 0;
            foreach (var memberInitializer in objectInitializer.memberInitializer())
            {
                allMembersHaveConstProvenance &= CheckVariableInitializer(memberInitializer.variableInitializer(), scope, function, effects, summary);
            }

            return allMembersHaveConstProvenance;
        }

        if (initializer.arrayInitializer() is { } arrayInitializer)
        {
            var allItemsHaveConstProvenance = arrayInitializer.variableInitializer().Length > 0;
            foreach (var item in arrayInitializer.variableInitializer())
            {
                allItemsHaveConstProvenance &= CheckVariableInitializer(item, scope, function, effects, summary);
            }

            return allItemsHaveConstProvenance;
        }

        return false;
    }

    private void CheckFixedTextStorageInterpolation(
        StarkParser.LiteralContext literal,
        StarkTypeSymbol destinationType,
        ValidationScope scope,
        FunctionDeclarationModel function,
        FunctionEffectProfile effects,
        FunctionValidationBuilder summary)
    {
        if (literal.StringLiteral() is not { } interpolatedString
            || !InterpolatedText.TryParse(interpolatedString.GetText(), out var segments, out _))
        {
            return;
        }

        var viewType = GetFixedTextStorageViewType(destinationType);
        if (segments.Count > 0)
        {
            _ = TryRecordHiddenSystemTextCall(
                GetSystemTextFunctionName(viewType.Kind == StarkTypeKind.Unicode ? "TryConcatUnicode" : "TryConcatAscii"),
                [StarkTypeSymbols.RawPointer(destinationType, isMutable: true), viewType, viewType],
                summary,
                literal);
        }

        foreach (var hole in segments.OfType<InterpolatedTextHoleSegment>())
        {
            var value = EvaluateExpression(
                hole.Expression,
                scope,
                function,
                effects,
                summary,
                allowFunctionReference: false,
                ExpressionObservation.Read);
            if (CanUseFixedTextConcatSource(destinationType, value.Type))
            {
                continue;
            }

            if (TextFormattingFacts.TryGetFixedBufferFormatInfo(destinationType, value.Type, out var formatInfo))
            {
                _ = TryRecordHiddenSystemTextCall(
                    GetSystemTextFunctionName(formatInfo.FunctionName),
                    [StarkTypeSymbols.RawPointer(destinationType, isMutable: true), value.Type],
                    summary,
                    hole.Expression);
            }
        }
    }

    private bool TryRecordHiddenSystemTextCall(
        string sourceName,
        IReadOnlyList<StarkTypeSymbol> argumentTypes,
        FunctionValidationBuilder summary,
        ParserRuleContext context)
    {
        if (!TryGetFunctionOverloads(sourceName, out var overloads))
        {
            return false;
        }

        var resolution = FunctionOverloadFacts.Resolve(
            overloads,
            receiverType: null,
            argumentTypes,
            TypeCompatibilityFacts.CanAssign,
            ResolveAssociatedTypeForSubstitution);
        if (!resolution.Succeeded)
        {
            return false;
        }

        var signature = resolution.Match!;
        if (_effectModel.Functions.ContainsKey(signature.Name))
        {
            summary.CalledFunctions.Add(signature.Name);
        }

        summary.PendingCalls.Add(new PendingCall(signature.Name, [], context.Start));
        return true;
    }

    private ValidationValue EvaluateExpression(
        StarkParser.ExpressionContext expression,
        ValidationScope scope,
        FunctionDeclarationModel function,
        FunctionEffectProfile effects,
        FunctionValidationBuilder summary,
        bool allowFunctionReference,
        ExpressionObservation observation)
    {
        return EvaluateAssignmentExpression(expression.assignmentExpression(), scope, function, effects, summary, allowFunctionReference, observation);
    }

    private ValidationValue EvaluateAssignmentExpression(
        StarkParser.AssignmentExpressionContext expression,
        ValidationScope scope,
        FunctionDeclarationModel function,
        FunctionEffectProfile effects,
        FunctionValidationBuilder summary,
        bool allowFunctionReference,
        ExpressionObservation observation)
    {
        if (expression.INIT() is not null
            && expression.ASSIGN() is not null
            && expression.assignmentOperator() is null)
        {
            var initLeft = EvaluateUnaryExpression(
                expression.unaryExpression(),
                scope,
                function,
                effects,
                summary,
                allowFunctionReference: true,
                ExpressionObservation.WriteTarget);
            var initType = StarkTypeSymbols.WithQualifiers(initLeft.Type, initializationKind: StarkInitializationKind.Init);
            var initTarget = initLeft with { Type = initType, IsAssignable = true };
            _ = EvaluateAssignmentExpression(
                expression.assignmentExpression(),
                scope,
                function,
                effects,
                summary,
                allowFunctionReference: false,
                ExpressionObservation.Read);

            RecordObservedMemoryWrite(initTarget, summary);
            if (IsVisibleMemoryWrite(initTarget))
            {
                summary.DisqualifyLaw();

                if (effects.IsPure)
                {
                    EffectError(summary, "STK4104", $"Law '{function.Name}' cannot perform externally visible writes.", expression.unaryExpression());
                }
            }

            return new ValidationValue(initTarget.Type);
        }

        if (expression.conditionalExpression() is { } conditionalExpression)
        {
            return EvaluateConditionalExpression(conditionalExpression, scope, function, effects, summary, allowFunctionReference, observation);
        }

        var left = EvaluateUnaryExpression(
            expression.unaryExpression(),
            scope,
            function,
            effects,
            summary,
            allowFunctionReference: true,
            ExpressionObservation.WriteTarget);
        var right = EvaluateAssignmentExpression(
            expression.assignmentExpression(),
            scope,
            function,
            effects,
            summary,
            allowFunctionReference: false,
            ExpressionObservation.Read);

        RecordObservedMemoryWrite(left, summary);

        if (IsVisibleMemoryWrite(left))
        {
            summary.DisqualifyLaw();

            if (effects.IsPure)
            {
                EffectError(summary, "STK4104", $"Law '{function.Name}' cannot perform externally visible writes.", expression.unaryExpression());
            }
        }

        return new ValidationValue(left.Type);
    }

    private void CheckAssumeStatement(
        StarkParser.AssumeStatementContext assumeStatement,
        ValidationScope scope,
        FunctionDeclarationModel function,
        FunctionEffectProfile effects,
        FunctionValidationBuilder summary,
        ControlFlowContext controlFlow)
    {
        foreach (var expression in assumeStatement.disjointRuntimeCondition().expressionList().expression())
        {
            if (TryEvaluateRawPointerRegionExpression(expression, scope, function, effects, summary))
            {
                continue;
            }

            EvaluateExpression(expression, scope, function, effects, summary, allowFunctionReference: false, ExpressionObservation.Read);
        }

        CheckStatement(assumeStatement.statement(), new ValidationScope(scope), function, effects, summary, controlFlow);
    }

    private bool TryEvaluateRawPointerRegionExpression(
        StarkParser.ExpressionContext expression,
        ValidationScope scope,
        FunctionDeclarationModel function,
        FunctionEffectProfile effects,
        FunctionValidationBuilder summary)
    {
        if (!TryGetRawPointerRegionExpression(expression, out _, out var startExpression, out var lengthExpression))
        {
            return false;
        }

        EvaluateExpression(startExpression, scope, function, effects, summary, allowFunctionReference: false, ExpressionObservation.Read);
        EvaluateExpression(lengthExpression, scope, function, effects, summary, allowFunctionReference: false, ExpressionObservation.Read);
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
        if (expression.unaryOperator() is not null
            || expression.conversionType() is not null
            || expression.unaryExpression() is not null
            || expression.powerExpression() is not { } powerExpression
            || powerExpression.unaryExpression() is not null)
        {
            return null;
        }

        return powerExpression.postfixExpression();
    }

    private ValidationValue EvaluateConditionalExpression(
        StarkParser.ConditionalExpressionContext expression,
        ValidationScope scope,
        FunctionDeclarationModel function,
        FunctionEffectProfile effects,
        FunctionValidationBuilder summary,
        bool allowFunctionReference,
        ExpressionObservation observation)
    {
        var condition = EvaluateLogicalOrExpression(expression.logicalOrExpression(), scope, function, effects, summary, allowFunctionReference, ExpressionObservation.Read);
        if (expression.expression().Length == 0)
        {
            return condition;
        }

        var whenTrue = EvaluateExpression(expression.expression(0), scope, function, effects, summary, allowFunctionReference: false, observation);
        var whenFalse = EvaluateExpression(expression.expression(1), scope, function, effects, summary, allowFunctionReference: false, observation);
        return new ValidationValue(
            FindCommonType(whenTrue.Type, whenFalse.Type),
            HasConstProvenance: HasConstProvenance(whenTrue) && HasConstProvenance(whenFalse));
    }

    private ValidationValue EvaluateLogicalOrExpression(
        StarkParser.LogicalOrExpressionContext expression,
        ValidationScope scope,
        FunctionDeclarationModel function,
        FunctionEffectProfile effects,
        FunctionValidationBuilder summary,
        bool allowFunctionReference,
        ExpressionObservation observation)
    {
        var operands = expression.logicalAndExpression()
            .Select(item => EvaluateLogicalAndExpression(item, scope, function, effects, summary, allowFunctionReference, observation))
            .ToArray();

        return operands.Length == 1 ? operands[0] : new ValidationValue(StarkTypeSymbols.Bool);
    }

    private ValidationValue EvaluateLogicalAndExpression(
        StarkParser.LogicalAndExpressionContext expression,
        ValidationScope scope,
        FunctionDeclarationModel function,
        FunctionEffectProfile effects,
        FunctionValidationBuilder summary,
        bool allowFunctionReference,
        ExpressionObservation observation)
    {
        var operands = expression.bitwiseOrExpression()
            .Select(item => EvaluateBitwiseOrExpression(item, scope, function, effects, summary, allowFunctionReference, observation))
            .ToArray();

        return operands.Length == 1 ? operands[0] : new ValidationValue(StarkTypeSymbols.Bool);
    }

    private ValidationValue EvaluateBitwiseOrExpression(
        StarkParser.BitwiseOrExpressionContext expression,
        ValidationScope scope,
        FunctionDeclarationModel function,
        FunctionEffectProfile effects,
        FunctionValidationBuilder summary,
        bool allowFunctionReference,
        ExpressionObservation observation)
    {
        return EvaluateBinaryChain(
            expression.bitwiseXorExpression(),
            item => EvaluateBitwiseXorExpression(item, scope, function, effects, summary, allowFunctionReference, observation));
    }

    private ValidationValue EvaluateBitwiseXorExpression(
        StarkParser.BitwiseXorExpressionContext expression,
        ValidationScope scope,
        FunctionDeclarationModel function,
        FunctionEffectProfile effects,
        FunctionValidationBuilder summary,
        bool allowFunctionReference,
        ExpressionObservation observation)
    {
        return EvaluateBinaryChain(
            expression.bitwiseAndExpression(),
            item => EvaluateBitwiseAndExpression(item, scope, function, effects, summary, allowFunctionReference, observation));
    }

    private ValidationValue EvaluateBitwiseAndExpression(
        StarkParser.BitwiseAndExpressionContext expression,
        ValidationScope scope,
        FunctionDeclarationModel function,
        FunctionEffectProfile effects,
        FunctionValidationBuilder summary,
        bool allowFunctionReference,
        ExpressionObservation observation)
    {
        return EvaluateBinaryChain(
            expression.equalityExpression(),
            item => EvaluateEqualityExpression(item, scope, function, effects, summary, allowFunctionReference, observation));
    }

    private ValidationValue EvaluateEqualityExpression(
        StarkParser.EqualityExpressionContext expression,
        ValidationScope scope,
        FunctionDeclarationModel function,
        FunctionEffectProfile effects,
        FunctionValidationBuilder summary,
        bool allowFunctionReference,
        ExpressionObservation observation)
    {
        var operands = expression.relationalExpression()
            .Select(item => EvaluateRelationalExpression(item, scope, function, effects, summary, allowFunctionReference, observation))
            .ToArray();

        return operands.Length == 1 ? operands[0] : new ValidationValue(StarkTypeSymbols.Bool);
    }

    private ValidationValue EvaluateRelationalExpression(
        StarkParser.RelationalExpressionContext expression,
        ValidationScope scope,
        FunctionDeclarationModel function,
        FunctionEffectProfile effects,
        FunctionValidationBuilder summary,
        bool allowFunctionReference,
        ExpressionObservation observation)
    {
        var operands = expression.shiftExpression()
            .Select(item => EvaluateShiftExpression(item, scope, function, effects, summary, allowFunctionReference, observation))
            .ToArray();

        return operands.Length == 1 ? operands[0] : new ValidationValue(StarkTypeSymbols.Bool);
    }

    private ValidationValue EvaluateShiftExpression(
        StarkParser.ShiftExpressionContext expression,
        ValidationScope scope,
        FunctionDeclarationModel function,
        FunctionEffectProfile effects,
        FunctionValidationBuilder summary,
        bool allowFunctionReference,
        ExpressionObservation observation)
    {
        return EvaluateBinaryChain(
            expression.additiveExpression(),
            item => EvaluateAdditiveExpression(item, scope, function, effects, summary, allowFunctionReference, observation));
    }

    private ValidationValue EvaluateAdditiveExpression(
        StarkParser.AdditiveExpressionContext expression,
        ValidationScope scope,
        FunctionDeclarationModel function,
        FunctionEffectProfile effects,
        FunctionValidationBuilder summary,
        bool allowFunctionReference,
        ExpressionObservation observation)
    {
        var operands = expression.multiplicativeExpression();
        var operators = ExtractOperators<StarkParser.MultiplicativeExpressionContext>(expression);
        if (operators.Count == 0)
        {
            return EvaluateMultiplicativeExpression(operands[0], scope, function, effects, summary, allowFunctionReference, observation);
        }

        var current = EvaluateMultiplicativeExpression(operands[0], scope, function, effects, summary, allowFunctionReference, observation);
        for (var index = 1; index < operands.Length; index++)
        {
            var next = EvaluateMultiplicativeExpression(operands[index], scope, function, effects, summary, allowFunctionReference, observation);
            if (operators[index - 1] == "+"
                && TryResolveRuntimeTextConcatenation(current, next, summary, expression, out var runtimeConcat))
            {
                current = runtimeConcat;
                continue;
            }

            current = IsTextType(current.Type) && IsTextType(next.Type) && operators[index - 1] == "+"
                ? new ValidationValue(FindCommonTextType(current.Type, next.Type))
                : new ValidationValue(FindCommonType(current.Type, next.Type));
        }

        return current;
    }

    private bool TryResolveRuntimeTextConcatenation(
        ValidationValue left,
        ValidationValue right,
        FunctionValidationBuilder summary,
        ParserRuleContext context,
        out ValidationValue result)
    {
        result = default!;

        if ((IsTextBufferType(left.Type) || IsTextBufferType(right.Type))
            && IsTextLikeForConcatenation(left.Type)
            && IsTextLikeForConcatenation(right.Type))
        {
            var useUnicode = IsUnicodeConcatSource(left.Type) || IsUnicodeConcatSource(right.Type);
            var destinationType = useUnicode ? StarkTypeSymbols.OwnedUnicode : StarkTypeSymbols.OwnedAscii;
            var viewType = useUnicode ? StarkTypeSymbols.Unicode : StarkTypeSymbols.Ascii;
            var concatSourceName = GetSystemTextFunctionName(useUnicode
                ? "TryConcatUnicode"
                : "TryConcatAscii");
            if (!TryGetFunctionOverloads(concatSourceName, out var concatOverloads))
            {
                return false;
            }

            var concatResolution = FunctionOverloadFacts.Resolve(
                concatOverloads,
                receiverType: null,
                [StarkTypeSymbols.RawPointer(destinationType, isMutable: true), viewType, viewType],
                TypeCompatibilityFacts.CanAssign,
                ResolveAssociatedTypeForSubstitution);
            if (!concatResolution.Succeeded)
            {
                return false;
            }

            var concatSignature = concatResolution.Match!;
            if (_effectModel.Functions.ContainsKey(concatSignature.Name))
            {
                summary.CalledFunctions.Add(concatSignature.Name);
            }

            summary.PendingCalls.Add(new PendingCall(concatSignature.Name, [], context.Start));
            result = new ValidationValue(viewType, NamedType: ResolveNamedTypeSymbol(viewType));
            return true;
        }

        if (!IsTextType(left.Type))
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
            TypeCompatibilityFacts.CanAssign,
            ResolveAssociatedTypeForSubstitution);
        if (!resolution.Succeeded)
        {
            return false;
        }

        var signature = resolution.Match!;
        if (_effectModel.Functions.ContainsKey(signature.Name))
        {
            summary.CalledFunctions.Add(signature.Name);
        }

        summary.PendingCalls.Add(new PendingCall(signature.Name, [], context.Start));
        result = new ValidationValue(signature.ReturnType, NamedType: ResolveNamedTypeSymbol(signature.ReturnType));
        return true;
    }

    private ValidationValue EvaluateMultiplicativeExpression(
        StarkParser.MultiplicativeExpressionContext expression,
        ValidationScope scope,
        FunctionDeclarationModel function,
        FunctionEffectProfile effects,
        FunctionValidationBuilder summary,
        bool allowFunctionReference,
        ExpressionObservation observation)
    {
        return EvaluateBinaryChain(
            expression.unaryExpression(),
            item => EvaluateUnaryExpression(item, scope, function, effects, summary, allowFunctionReference, observation));
    }

    private ValidationValue EvaluateUnaryExpression(
        StarkParser.UnaryExpressionContext expression,
        ValidationScope scope,
        FunctionDeclarationModel function,
        FunctionEffectProfile effects,
        FunctionValidationBuilder summary,
        bool allowFunctionReference,
        ExpressionObservation observation)
    {
        if (expression.powerExpression() is { } powerExpression)
        {
            return EvaluatePowerExpression(powerExpression, scope, function, effects, summary, allowFunctionReference, observation);
        }

        if (expression.conversionType() is { } conversionType)
        {
            var operand = EvaluateUnaryExpression(expression.unaryExpression(), scope, function, effects, summary, allowFunctionReference: false, ExpressionObservation.Read);
            var targetType = _typeResolver.ResolveConversionType(conversionType, _currentFunctionGenericParameters, CurrentModuleName);
            ValidateTypeUsage(conversionType, targetType, TypeUsage.Conversion);
            return CreateConvertedValidationValue(targetType, operand, expression);
        }

        if (expression.INIT() is not null && expression.unaryOperator() is null)
        {
            var operand = EvaluateUnaryExpression(expression.unaryExpression(), scope, function, effects, summary, allowFunctionReference: false, ExpressionObservation.WriteTarget);
            return operand with
            {
                Type = StarkTypeSymbols.WithQualifiers(
                    operand.Type,
                    initializationKind: StarkInitializationKind.Init,
                    isMutableView: operand.Type.Kind == StarkTypeKind.Slice || operand.Type.IsMutableView),
                IsAssignable = true,
                IsAddressMutable = true
            };
        }

        var op = expression.unaryOperator()?.GetText() ?? expression.GetChild(0).GetText();

        if (op == "&")
        {
            var operand = EvaluateUnaryExpression(expression.unaryExpression(), scope, function, effects, summary, allowFunctionReference: false, ExpressionObservation.WriteTarget);
            return CreateAddressOfValidationValue(operand, context: expression);
        }

        if (op == "*")
        {
            var operand = EvaluateUnaryExpression(expression.unaryExpression(), scope, function, effects, summary, allowFunctionReference: false, ExpressionObservation.Read);
            var result = CreateDereferenceValidationValue(operand);
            if (observation == ExpressionObservation.Read)
            {
                RecordObservedMemoryRead(result, summary);
            }

            return result;
        }

        return EvaluateUnaryExpression(expression.unaryExpression(), scope, function, effects, summary, allowFunctionReference: false, observation);
    }

    private ValidationValue EvaluatePowerExpression(
        StarkParser.PowerExpressionContext expression,
        ValidationScope scope,
        FunctionDeclarationModel function,
        FunctionEffectProfile effects,
        FunctionValidationBuilder summary,
        bool allowFunctionReference,
        ExpressionObservation observation)
    {
        var left = EvaluatePostfixExpression(expression.postfixExpression(), scope, function, effects, summary, allowFunctionReference, observation);
        if (expression.unaryExpression() is not { } rightExpression)
        {
            return left;
        }

        var right = EvaluateUnaryExpression(rightExpression, scope, function, effects, summary, allowFunctionReference: false, ExpressionObservation.Read);
        return new ValidationValue(FindCommonType(left.Type, right.Type));
    }

    private ValidationValue EvaluatePostfixExpression(
        StarkParser.PostfixExpressionContext expression,
        ValidationScope scope,
        FunctionDeclarationModel function,
        FunctionEffectProfile effects,
        FunctionValidationBuilder summary,
        bool allowFunctionReference,
        ExpressionObservation observation)
    {
        var postfixParts = expression.postfixPart();
        var firstUnhandledPostfixIndex = 0;
        ValidationValue binding;
        if (TryEvaluateDynTraitFromPartsConstructionPrefix(
                expression,
                scope,
                function,
                effects,
                summary,
                out binding,
                out firstUnhandledPostfixIndex))
        {
        }
        else
        {
            var requiresCallableTarget = postfixParts.Any(static part => part.argumentList() is not null);
            binding = EvaluatePrimaryExpression(expression.primaryExpression(), scope, function, effects, summary, allowFunctionReference || requiresCallableTarget, observation);
        }

        for (var index = firstUnhandledPostfixIndex; index < postfixParts.Length; index++)
        {
            var postfixPart = postfixParts[index];
            if (postfixPart.argumentList() is { } argumentList)
            {
                binding = InvokeCall(binding, argumentList, scope, function, effects, summary);
                continue;
            }

            if (postfixPart.GetChild(0).GetText() == "[")
            {
                if (postfixPart.expressionList() is { } expressionList)
                {
                    binding = ApplyIndex(binding, expressionList, scope, function, effects, summary);
                }

                continue;
            }

            if (index + 1 < postfixParts.Length
                && postfixParts[index + 1].argumentList() is { } memberArguments
                && TryEvaluateDynamicStorageMemberCall(binding, postfixPart.Identifier().GetText(), memberArguments, scope, function, effects, summary, out var dynamicMemberCall))
            {
                binding = dynamicMemberCall;
                index++;
                continue;
            }

            binding = ApplyMemberAccess(binding, postfixPart.Identifier().GetText());
        }

        if (observation == ExpressionObservation.Read)
        {
            RecordObservedMemoryRead(binding, summary);
        }

        return binding;
    }

    private bool TryEvaluateDynTraitFromPartsConstructionPrefix(
        StarkParser.PostfixExpressionContext expression,
        ValidationScope scope,
        FunctionDeclarationModel function,
        FunctionEffectProfile effects,
        FunctionValidationBuilder summary,
        out ValidationValue binding,
        out int firstUnhandledPostfixIndex)
    {
        binding = default!;
        firstUnhandledPostfixIndex = 0;
        if (!TryGetDynTraitFromPartsOperationName(expression, out var operationName)
            || expression.postfixPart().Length == 0
            || expression.postfixPart()[0] is not { } callPart
            || callPart.argumentList() is not { } argumentList)
        {
            return false;
        }

        firstUnhandledPostfixIndex = 1;
        foreach (var argument in argumentList.argument())
        {
            EvaluateExpression(argument.expression(), scope, function, effects, summary, allowFunctionReference: false, ExpressionObservation.Read);
        }

        var storageKind = string.Equals(operationName, "dynbox", StringComparison.Ordinal)
            ? StarkDynTraitStorageKind.Heap
            : StarkDynTraitStorageKind.View;
        binding = TryResolveExplicitDynTraitFromPartsTargetType(expression, storageKind, out var targetType)
            ? new ValidationValue(targetType, NamedType: ResolveNamedTypeSymbol(targetType))
            : new ValidationValue(StarkTypeSymbols.Error);
        return true;
    }

    private bool TryResolveExplicitDynTraitFromPartsTargetType(
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
            ResolveType,
            static (_, _, _) => { },
            visibleComptimeParameters: _currentFunctionComptimeGenericParameters);
        if (genericArguments.TypeArguments.Count != 1
            || genericArguments.TypeArguments[0].Kind == StarkTypeKind.Error)
        {
            return false;
        }

        return TryBuildDynTraitFromPartsTargetType(genericArguments.TypeArguments[0], storageKind, out targetType);
    }

    private bool TryBuildDynTraitFromPartsTargetType(
        StarkTypeSymbol declaredType,
        StarkDynTraitStorageKind storageKind,
        out StarkTypeSymbol targetType)
    {
        targetType = StarkTypeSymbols.Error;
        if (declaredType.Kind == StarkTypeKind.DynTrait)
        {
            if (declaredType.DynTraitStorageKind != storageKind)
            {
                return false;
            }

            targetType = storageKind == StarkDynTraitStorageKind.View && declaredType.BorrowKind == StarkBorrowKind.None
                ? StarkTypeSymbols.ApplyQualifiers(declaredType, borrowKind: StarkBorrowKind.Borrow, isMutableView: declaredType.IsMutableView)
                : declaredType;
            return true;
        }

        if (declaredType.Kind != StarkTypeKind.Named
            || declaredType.NamedType is not { } traitName
            || !_typeModel.NamedTypes.TryGetValue(traitName, out var traitSymbol)
            || traitSymbol.Kind != DeclarationKind.Trait
            || !traitSymbol.IsDynTrait)
        {
            return false;
        }

        var dynType = StarkTypeSymbols.DynTrait(traitName, storageKind, declaredType.TypeArguments);
        targetType = storageKind == StarkDynTraitStorageKind.View
            ? StarkTypeSymbols.ApplyQualifiers(dynType, borrowKind: StarkBorrowKind.Borrow)
            : dynType;
        return true;
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

    private bool TryEvaluateDynamicStorageMemberCall(
        ValidationValue receiver,
        string memberName,
        StarkParser.ArgumentListContext arguments,
        ValidationScope scope,
        FunctionDeclarationModel function,
        FunctionEffectProfile effects,
        FunctionValidationBuilder summary,
        out ValidationValue result)
    {
        result = null!;
        if (receiver.Type.Kind != StarkTypeKind.Dynamic)
        {
            return false;
        }

        var isReserve = string.Equals(memberName, "Reserve", StringComparison.Ordinal);
        var isTryReserve = string.Equals(memberName, "TryReserve", StringComparison.Ordinal);
        var isTryReserveCapacity = string.Equals(memberName, "TryReserveCapacity", StringComparison.Ordinal);
        if (!isReserve && !isTryReserve && !isTryReserveCapacity)
        {
            if (!string.Equals(memberName, "MoveLast", StringComparison.Ordinal)
                && !string.Equals(memberName, "MoveAt", StringComparison.Ordinal))
            {
                return false;
            }

            foreach (var argument in arguments.argument())
            {
                EvaluateExpression(
                    argument.expression(),
                    scope,
                    function,
                    effects,
                    summary,
                    allowFunctionReference: false,
                    ExpressionObservation.Read);
            }

            RecordObservedMemoryRead(receiver, summary);
            RecordObservedMemoryWrite(receiver, summary);
            RecordDynamicStorageMutationEffects(function, effects, summary, arguments);
            result = new ValidationValue(
                receiver.Type.ElementType ?? StarkTypeSymbols.Error,
                NamedType: ResolveNamedTypeSymbol(receiver.Type.ElementType ?? StarkTypeSymbols.Error));
            return true;
        }

        foreach (var argument in arguments.argument())
        {
            EvaluateExpression(
                argument.expression(),
                scope,
                function,
                effects,
                summary,
                allowFunctionReference: false,
                ExpressionObservation.Read);
        }

        RecordObservedMemoryRead(receiver, summary);
        RecordObservedMemoryWrite(receiver, summary);
        RecordDynamicStorageRuntimeEffects(function, effects, summary, arguments);
        result = new ValidationValue(isReserve ? StarkTypeSymbols.Void : StarkTypeSymbols.Bool);
        return true;
    }

    private void RecordDynamicStorageMutationEffects(
        FunctionDeclarationModel function,
        FunctionEffectProfile effects,
        FunctionValidationBuilder summary,
        ParserRuleContext context)
    {
        summary.DisqualifyLaw();
        summary.MarkOtherMemoryRead();
        summary.MarkOtherMemoryWrite();

        if (effects.IsPure)
        {
            EffectError(summary, "STK4104", $"Law '{function.Name}' cannot mutate dynamic storage.", context);
        }
    }

    private ValidationValue EvaluatePrimaryExpression(
        StarkParser.PrimaryExpressionContext expression,
        ValidationScope scope,
        FunctionDeclarationModel function,
        FunctionEffectProfile effects,
        FunctionValidationBuilder summary,
        bool allowFunctionReference,
        ExpressionObservation observation)
    {
        if (expression.literal() is { } literal)
        {
            return new ValidationValue(EvaluateLiteralType(literal));
        }

        if (expression.COMPTIME() is not null && expression.block() is not null)
        {
            return new ValidationValue(StarkTypeSymbols.Error);
        }

        if (expression.SIZEOF() is not null || expression.ALIGNOF() is not null)
        {
            ValidateTypeLayoutExpression(expression);
            return new ValidationValue(
                expression.ALIGNOF() is not null
                    ? StarkTypeSymbols.Integer(64, BigInteger.One, new BigInteger(long.MaxValue))
                    : StarkTypeSymbols.Integer(64, BigInteger.Zero, new BigInteger(long.MaxValue)));
        }

        if (expression.Identifier() is { } identifier)
        {
            return ResolveValue(identifier.GetText(), scope, function, effects, summary, allowFunctionReference, observation, identifier.Symbol);
        }

        if (expression.lambdaExpression() is { } lambdaExpression)
        {
            return EvaluateLambdaExpression(lambdaExpression, scope, function, effects, summary);
        }

        if (expression.enumConstructorExpression() is { } enumConstructorExpression)
        {
            return EvaluateEnumConstructorExpression(enumConstructorExpression, scope, function, effects, summary);
        }

        if (expression.genericEnumCaseReference() is { } genericEnumCaseReference)
        {
            return ResolveGenericMemberReference(
                genericEnumCaseReference,
                scope,
                function,
                effects,
                summary,
                allowFunctionReference,
                observation);
        }

        if (expression.genericQualifiedName() is { } genericQualifiedName)
        {
            return ResolveGenericQualifiedNameValue(genericQualifiedName, allowFunctionReference);
        }

        if (expression.qualifiedName() is { } qualifiedName)
        {
            return ResolveValue(qualifiedName.GetText(), scope, function, effects, summary, allowFunctionReference, observation, qualifiedName.Start);
        }

        if (expression.objectCreationExpression() is { } objectCreationExpression)
        {
            return EvaluateObjectCreation(objectCreationExpression, scope, function, effects, summary);
        }

        return EvaluateExpression(expression.expression(), scope, function, effects, summary, allowFunctionReference: false, observation);
    }

    private void ValidateTypeLayoutExpression(StarkParser.PrimaryExpressionContext expression)
    {
        var targetType = ResolveType(expression.type_());
        if (targetType.Kind == StarkTypeKind.Error
            || TypeContainsOpenCurrentFunctionGenericParameter(targetType))
        {
            return;
        }

        if (ConcreteTypeLayoutHelper.TryGetConcreteTypeLayout(targetType, _typeModel.NamedTypes, _enumLayoutModel.Layouts) is not null)
        {
            return;
        }

        var kind = expression.ALIGNOF() is not null ? "alignof" : "sizeof";
        _context.Diagnostics.Error(
            "STK3008",
            $"{kind} requires a concrete runtime layout, but '{targetType.DisplayName}' has no concrete layout in this context.",
            "semantic-validate",
            Location(expression.type_() ?? (ParserRuleContext)expression));
    }

    private ValidationValue EvaluateLambdaExpression(
        StarkParser.LambdaExpressionContext expression,
        ValidationScope scope,
        FunctionDeclarationModel function,
        FunctionEffectProfile effects,
        FunctionValidationBuilder summary)
    {
        return new ValidationValue(StarkTypeSymbols.Error);
    }

    private void ValidateLambdaFunctions()
    {
        if (_typeModel.Lambdas.Count == 0 && _typeModel.ClosureLambdas.Count == 0)
        {
            return;
        }

        var lambdaContexts = CollectLambdaExpressionsByFunctionName();
        foreach (var lambda in _typeModel.Lambdas)
        {
            if (!lambdaContexts.TryGetValue(lambda.FunctionName, out var expression))
            {
                continue;
            }

            var signature = _typeModel.Functions.TryGetValue(lambda.FunctionName, out var typedSignature)
                ? typedSignature
                : CallableValueFacts.BuildLambdaSignature(lambda);
            var effects = CallableValueFacts.BuildLambdaEffectProfile(lambda);
            var declaration = new FunctionDeclarationModel(
                lambda.FunctionName,
                signature.Kind,
                signature.ReturnType.DisplayName,
                signature.Parameters
                    .Select(static parameter => new ParameterModel(parameter.Name, parameter.Type.DisplayName))
                    .ToArray(),
                new FunctionModifierSet(
                    InlinePreference.InlineHint,
                    HasExplicitInlinePreference: false,
                    IsHot: false,
                    IsCold: false,
                    IsFfi: false,
                    IsVarargs: false,
                    IsStrictFp: false),
                HasBody: true);

            var summary = GetOrCreateSummary(lambda.FunctionName);
            summary.Configure(signature.ReturnType, hasBody: true, signature.Kind);
            summary.SetParameters(signature.Parameters, signature.DisjointGroups, _typeModel.NamedTypes, _enumLayoutModel.Layouts);

            var scope = ValidationScope.CreateRoot();
            foreach (var parameter in signature.Parameters)
            {
                DeclareVariable(
                    scope,
                    new VariableSymbol(
                        parameter.Name,
                        parameter.Type,
                        SymbolOrigin.Parameter,
                        LocalStorageClass.None,
                        IsMutable: false,
                        IsConstant: false,
                        HasConstProvenance: parameter.IsConst),
                    summary,
                    expression.Start);
            }

            if (expression.expression() is { } bodyExpression)
            {
                var returnedValue = EvaluateExpression(
                    bodyExpression,
                    scope,
                    declaration,
                    effects,
                    summary,
                    allowFunctionReference: false,
                    ExpressionObservation.Read);
                RecordReturnCapture(returnedValue, declaration, summary);
            }
            else if (expression.block() is { } block)
            {
                summary.SetOptimizationSummary(FunctionOptimizationSummaryBuilder.Build(block));
                CheckBlock(block, scope, declaration, effects, summary, ControlFlowContext.Root);
            }
        }

        foreach (var lambda in _typeModel.ClosureLambdas)
        {
            if (!lambdaContexts.TryGetValue(lambda.FunctionName, out var expression))
            {
                continue;
            }

            var signature = _typeModel.Functions.TryGetValue(lambda.FunctionName, out var typedSignature)
                ? typedSignature
                : CallableValueFacts.BuildClosureLambdaSignature(lambda);
            var effects = CallableValueFacts.BuildClosureLambdaEffectProfile(lambda);
            var declaration = new FunctionDeclarationModel(
                lambda.FunctionName,
                signature.Kind,
                signature.ReturnType.DisplayName,
                signature.Parameters
                    .Select(static parameter => new ParameterModel(parameter.Name, parameter.Type.DisplayName))
                    .ToArray(),
                new FunctionModifierSet(
                    InlinePreference.InlineHint,
                    HasExplicitInlinePreference: false,
                    IsHot: false,
                    IsCold: false,
                    IsFfi: false,
                    IsVarargs: false,
                    IsStrictFp: false),
                HasBody: true);

            var summary = GetOrCreateSummary(lambda.FunctionName);
            summary.Configure(signature.ReturnType, hasBody: true, signature.Kind);
            summary.SetParameters(signature.Parameters, signature.DisjointGroups, _typeModel.NamedTypes, _enumLayoutModel.Layouts);

            var scope = ValidationScope.CreateRoot();
            DeclareLambdaCaptures(scope, summary, lambda.Location, lambda.EnclosingFunctionName, expression.Start);
            foreach (var parameter in signature.Parameters)
            {
                DeclareVariable(
                    scope,
                    new VariableSymbol(
                        parameter.Name,
                        parameter.Type,
                        SymbolOrigin.Parameter,
                        LocalStorageClass.None,
                        IsMutable: false,
                        IsConstant: false,
                        HasConstProvenance: parameter.IsConst),
                    summary,
                    expression.Start);
            }

            if (expression.expression() is { } bodyExpression)
            {
                var returnedValue = EvaluateExpression(
                    bodyExpression,
                    scope,
                    declaration,
                    effects,
                    summary,
                    allowFunctionReference: false,
                    ExpressionObservation.Read);
                RecordReturnCapture(returnedValue, declaration, summary);
            }
            else if (expression.block() is { } block)
            {
                summary.SetOptimizationSummary(FunctionOptimizationSummaryBuilder.Build(block));
                CheckBlock(block, scope, declaration, effects, summary, ControlFlowContext.Root);
            }
        }
    }

    private void DeclareLambdaCaptures(
        ValidationScope scope,
        FunctionValidationBuilder summary,
        SourceLocation lambdaLocation,
        string? enclosingFunctionName,
        IToken fallbackToken)
    {
        foreach (var capture in _typeModel.LambdaCaptures.Where(capture =>
                     SameLocation(capture.LambdaLocation, lambdaLocation)
                     && string.Equals(capture.EnclosingFunctionName, enclosingFunctionName, StringComparison.Ordinal)))
        {
            DeclareVariable(
                scope,
                new VariableSymbol(
                    capture.Name,
                    CallableValueFacts.GetLambdaCaptureBodyType(capture.Type, capture.Mode),
                    SymbolOrigin.Local,
                    LocalStorageClass.None,
                    IsMutable: CallableValueFacts.LambdaCaptureModeExposesWritableBinding(capture.Mode),
                    IsConstant: false),
                summary,
                fallbackToken);
        }
    }

    private static bool SameLocation(SourceLocation left, SourceLocation right)
    {
        return left.Line == right.Line
            && left.Column == right.Column
            && string.Equals(left.FilePath, right.FilePath, StringComparison.Ordinal);
    }

    private Dictionary<string, StarkParser.LambdaExpressionContext> CollectLambdaExpressionsByFunctionName()
    {
        var lambdasByLocation = _typeModel.Lambdas
            .Select(static lambda => (lambda.FunctionName, lambda.Location))
            .Concat(_typeModel.ClosureLambdas.Select(static lambda => (lambda.FunctionName, lambda.Location)))
            .GroupBy(static lambda => $"{lambda.Location.Line}:{lambda.Location.Column}")
            .ToDictionary(
                static group => group.Key,
                static group => group.ToArray(),
                StringComparer.Ordinal);
        var contexts = new Dictionary<string, StarkParser.LambdaExpressionContext>(StringComparer.Ordinal);

        Collect(_parseResult.Root);
        return contexts;

        void Collect(IParseTree current)
        {
            if (current is StarkParser.LambdaExpressionContext lambdaExpression)
            {
                var key = $"{lambdaExpression.Start.Line}:{lambdaExpression.Start.Column + 1}";
                if (lambdasByLocation.TryGetValue(key, out var matchingLambdas)
                    && matchingLambdas.Length == 1)
                {
                    contexts[matchingLambdas[0].FunctionName] = lambdaExpression;
                }
            }

            for (var index = 0; index < current.ChildCount; index++)
            {
                Collect(current.GetChild(index));
            }
        }
    }

    private ValidationValue EvaluateObjectCreation(
        StarkParser.ObjectCreationExpressionContext expression,
        ValidationScope scope,
        FunctionDeclarationModel function,
        FunctionEffectProfile effects,
        FunctionValidationBuilder summary)
    {
        var createdType = expression.type_() is { } explicitType
            ? ResolveType(explicitType)
            : TryGetObjectCreationTyping(expression, out var objectCreationTyping)
                ? objectCreationTyping.CreatedType
                : StarkTypeSymbols.Error;

        if (expression.type_() is { } explicitObjectType)
        {
            ValidateTypeUsage(explicitObjectType, createdType, TypeUsage.Conversion);
        }

        if (expression.argumentList() is { } argumentList)
        {
            foreach (var argument in argumentList.argument())
            {
                EvaluateExpression(argument.expression(), scope, function, effects, summary, allowFunctionReference: false, ExpressionObservation.Read);
            }
        }

        if (expression.objectInitializer() is { } objectInitializer)
        {
            foreach (var memberInitializer in objectInitializer.memberInitializer())
            {
                CheckVariableInitializer(memberInitializer.variableInitializer(), scope, function, effects, summary);
            }
        }

        if (createdType.Kind == StarkTypeKind.Dynamic)
        {
            RecordDynamicStorageRuntimeEffects(function, effects, summary, expression);
        }

        return new ValidationValue(createdType, NamedType: ResolveNamedTypeSymbol(createdType));
    }

    private ValidationValue ResolveGenericMemberReference(
        StarkParser.GenericEnumCaseReferenceContext genericMemberReference,
        ValidationScope scope,
        FunctionDeclarationModel function,
        FunctionEffectProfile effects,
        FunctionValidationBuilder summary,
        bool allowFunctionReference,
        ExpressionObservation observation)
    {
        if (TryResolveEnumCaseReference(genericMemberReference, out var enumType, out var enumTypeSymbol, out var variant))
        {
            if (variant.IsUnit)
            {
                return new ValidationValue(enumTypeSymbol, NamedType: enumType);
            }

            return new ValidationValue(
                enumTypeSymbol,
                NamedType: enumType,
                EnumConstructor: new EnumConstructorBinding(genericMemberReference.GetText(), variant));
        }

        var targetType = ResolveGenericQualifiedName(genericMemberReference.genericQualifiedName());
        var namedType = ResolveNamedTypeSymbol(targetType);
        if (namedType?.Kind is DeclarationKind.Doctrine or DeclarationKind.Trait)
        {
            return ApplyMemberAccess(
                new ValidationValue(targetType, NamedType: namedType),
                genericMemberReference.Identifier().GetText());
        }

        return ResolveValue(
            genericMemberReference.GetText(),
            scope,
            function,
            effects,
            summary,
            allowFunctionReference,
            observation,
            genericMemberReference.Start);
    }

    private void RecordDynamicStorageRuntimeEffects(
        FunctionDeclarationModel function,
        FunctionEffectProfile effects,
        FunctionValidationBuilder summary,
        ParserRuleContext context)
    {
        summary.DisqualifyLaw();
        summary.MarkOtherMemoryRead();
        summary.MarkOtherMemoryWrite();

        if (effects.IsPure)
        {
            EffectError(summary, "STK4104", $"Law '{function.Name}' cannot allocate or free dynamic storage.", context);
        }
    }

    private ValidationValue EvaluateEnumConstructorExpression(
        StarkParser.EnumConstructorExpressionContext expression,
        ValidationScope scope,
        FunctionDeclarationModel function,
        FunctionEffectProfile effects,
        FunctionValidationBuilder summary)
    {
        var constructorName = expression.enumCaseTarget().GetText();
        if (!TryResolveEnumCaseReference(constructorName, out var enumType, out var enumTypeSymbol, out var variant))
        {
            return new ValidationValue(StarkTypeSymbols.Error);
        }

        foreach (var member in expression.enumConstructorInitializer().enumConstructorMember())
        {
            EvaluateExpression(
                member.expression(),
                scope,
                function,
                effects,
                summary,
                allowFunctionReference: false,
                ExpressionObservation.Read);
        }

        return variant.UsesNamedFields
            ? new ValidationValue(enumTypeSymbol, NamedType: enumType)
            : new ValidationValue(StarkTypeSymbols.Error);
    }

    private ValidationValue ResolveValue(
        string name,
        ValidationScope scope,
        FunctionDeclarationModel function,
        FunctionEffectProfile effects,
        FunctionValidationBuilder summary,
        bool allowFunctionReference,
        ExpressionObservation observation,
        IToken token)
    {
        if (scope.TryLookup(name, out var local))
        {
            return new ValidationValue(
                local.Type,
                IsAssignable: CanAssignToLocal(local),
                RootSymbol: local,
                NamedType: ResolveNamedTypeSymbol(local.Type),
                IsAddressMutable: CanFormMutableAddressFromLocal(local),
                HasConstProvenance: local.HasConstProvenance);
        }

        if (TryResolveGlobalBySourceName(name, out var globalType))
        {
            var isConstGlobal = globalType.BindingKind == GlobalBindingKind.Const;
            if (observation == ExpressionObservation.Read && !isConstGlobal)
            {
                summary.DisqualifyLaw();

                if (effects.IsPure)
                {
                    EffectError(summary, "STK4105", $"Law '{function.Name}' cannot read global state.", token);
                }
            }

            var isMutable = globalType.IsMutable;

            return new ValidationValue(
                globalType.Type,
                IsAssignable: isMutable,
                RootSymbol: new VariableSymbol(
                    globalType.Name,
                    globalType.Type,
                    SymbolOrigin.Global,
                    LocalStorageClass.Static,
                    isMutable,
                    IsConstant: !isMutable,
                    BindingKind: globalType.BindingKind,
                    HasConstProvenance: isConstGlobal),
                NamedType: ResolveNamedTypeSymbol(globalType.Type),
                IsAddressMutable: isMutable,
                HasConstProvenance: isConstGlobal);
        }

        if (TryGetFunctionOverloads(name, out var targetFunctions))
        {
            targetFunctions = FilterDirectCallableTypeMemberFunctions(name, targetFunctions);
            if (targetFunctions.Count == 0)
            {
                return new ValidationValue(StarkTypeSymbols.Error);
            }

            if (!allowFunctionReference)
            {
                return new ValidationValue(StarkTypeSymbols.Error);
            }

            if (targetFunctions.Count == 1 && !targetFunctions[0].IsGeneric)
            {
                return new ValidationValue(targetFunctions[0].ReturnType, Function: targetFunctions[0]);
            }

            return new ValidationValue(StarkTypeSymbols.Error, OverloadSourceName: name);
        }

        if (TryResolveNamedTypeBySourceName(name, out var namedType))
        {
            if (namedType.Kind is DeclarationKind.Struct or DeclarationKind.Record)
            {
                return new ValidationValue(StarkTypeSymbols.Error, NamespaceName: namedType.Name, NamedType: namedType);
            }

            if (namedType.Kind == DeclarationKind.Doctrine && allowFunctionReference)
            {
                return new ValidationValue(StarkTypeSymbols.Named(namedType.Name), NamedType: namedType);
            }

            if (namedType.Kind is DeclarationKind.Doctrine or DeclarationKind.Trait)
            {
                return new ValidationValue(StarkTypeSymbols.Error);
            }
        }

        if (TryResolveNamedTypeBySourceName(name, out namedType) && namedType.Kind == DeclarationKind.Enum)
        {
            return new ValidationValue(StarkTypeSymbols.Error, NamespaceName: namedType.Name);
        }

            if (_moduleGraph.CanAccessModule(CurrentModuleName, name))
        {
            return new ValidationValue(StarkTypeSymbols.Error, NamespaceName: name);
        }

            if (_moduleGraph.CanAccessModuleNamespace(CurrentModuleName, name))
        {
            return new ValidationValue(StarkTypeSymbols.Error, NamespaceName: name);
        }

        if (TryResolveEnumCaseReference(name, out var enumType, out var enumTypeSymbol, out var variant))
        {
            if (variant.IsUnit)
            {
                return new ValidationValue(enumTypeSymbol, NamedType: enumType);
            }

            if (!variant.UsesNamedFields && allowFunctionReference)
            {
                return new ValidationValue(
                    enumTypeSymbol,
                    NamedType: enumType,
                    EnumConstructor: new EnumConstructorBinding(name, variant));
            }

            return new ValidationValue(StarkTypeSymbols.Error);
        }

        return new ValidationValue(StarkTypeSymbols.Error);
    }

    private void BindSwitchPattern(
        StarkParser.PatternContext pattern,
        StarkTypeSymbol switchType,
        ValidationScope scope,
        FunctionValidationBuilder summary)
    {
        if (pattern.VAR() is not null && pattern.Identifier() is { } capture)
        {
            if (!IsEnumSwitchType(switchType))
            {
                DeclareVariable(
                    scope,
                    new VariableSymbol(capture.GetText(), switchType, SymbolOrigin.Local, LocalStorageClass.None, IsMutable: false, IsConstant: false),
                    summary,
                    capture.Symbol);
            }

            return;
        }

        if (pattern.enumNamedFieldPattern() is { } enumNamedFieldPattern)
        {
            BindEnumNamedFieldPattern(enumNamedFieldPattern, switchType, scope, summary);
            return;
        }

        if (pattern.genericEnumAggregatePattern() is { } genericEnumAggregatePattern)
        {
            TryBindEnumAggregateSwitchPattern(genericEnumAggregatePattern, switchType, scope, summary);
            return;
        }

        if (pattern.aggregatePattern() is { } aggregatePattern)
        {
            if (TryBindEnumAggregateSwitchPattern(aggregatePattern, switchType, scope, summary))
            {
                return;
            }

            BindAggregateSwitchPattern(aggregatePattern, switchType, scope, summary);
        }
    }

    private void BindAggregateSwitchPattern(
        StarkParser.AggregatePatternContext aggregatePattern,
        StarkTypeSymbol switchType,
        ValidationScope scope,
        FunctionValidationBuilder summary)
    {
        var patternType = ResolvePatternSimpleType(aggregatePattern.simpleType());
        if (switchType.Kind != StarkTypeKind.Named
            || patternType.Kind != StarkTypeKind.Named
            || switchType.NamedType is null
            || patternType.NamedType is null
            || !string.Equals(switchType.NamedType, patternType.NamedType, StringComparison.Ordinal)
            || !_typeModel.NamedTypes.TryGetValue(switchType.NamedType, out var namedType))
        {
            return;
        }

        var suffix = aggregatePattern.aggregatePatternSuffix();
        if (suffix is null)
        {
            return;
        }

        if (suffix.Identifier() is { } capture)
        {
            DeclareVariable(
                scope,
                new VariableSymbol(capture.GetText(), switchType, SymbolOrigin.Local, LocalStorageClass.None, IsMutable: false, IsConstant: false),
                summary,
                capture.Symbol);
            return;
        }

        var fieldPatterns = suffix.pattern();
        if (fieldPatterns.Length != namedType.OrderedFields.Count)
        {
            return;
        }

        for (var index = 0; index < fieldPatterns.Length; index++)
        {
            BindAggregateFieldPattern(fieldPatterns[index], namedType.OrderedFields[index], scope, summary);
        }
    }

    private bool TryBindEnumAggregateSwitchPattern(
        StarkParser.AggregatePatternContext aggregatePattern,
        StarkTypeSymbol switchType,
        ValidationScope scope,
        FunctionValidationBuilder summary)
    {
        return TryBindResolvedEnumAggregateSwitchPattern(
            aggregatePattern.simpleType().GetText(),
            aggregatePattern.aggregatePatternSuffix(),
            switchType,
            scope,
            summary,
            out var matched)
            && matched;
    }

    private bool TryBindEnumAggregateSwitchPattern(
        StarkParser.GenericEnumAggregatePatternContext aggregatePattern,
        StarkTypeSymbol switchType,
        ValidationScope scope,
        FunctionValidationBuilder summary)
    {
        return TryBindResolvedEnumAggregateSwitchPattern(
            aggregatePattern.genericEnumCaseReference().GetText(),
            aggregatePattern.aggregatePatternSuffix(),
            switchType,
            scope,
            summary,
            out var matched)
            && matched;
    }

    private bool TryBindResolvedEnumAggregateSwitchPattern(
        string caseName,
        StarkParser.AggregatePatternSuffixContext? suffix,
        StarkTypeSymbol switchType,
        ValidationScope scope,
        FunctionValidationBuilder summary,
        out bool matched)
    {
        matched = false;
        if (!TryResolveEnumCaseReference(caseName, out var enumType, out _, out var variant))
        {
            return false;
        }

        matched = true;
        if (switchType.Kind != StarkTypeKind.Named
            || switchType.NamedType is null
            || !string.Equals(switchType.NamedType, enumType.Name, StringComparison.Ordinal)
            || variant.UsesNamedFields)
        {
            return true;
        }

        if (variant.IsUnit || suffix is null)
        {
            return true;
        }

        if (suffix.Identifier() is { } capture)
        {
            DeclareVariable(
                scope,
                new VariableSymbol(capture.GetText(), switchType, SymbolOrigin.Local, LocalStorageClass.None, IsMutable: false, IsConstant: false),
                summary,
                capture.Symbol);
            return true;
        }

        var fieldPatterns = suffix.pattern();
        if (fieldPatterns.Length != variant.Fields.Count)
        {
            return true;
        }

        for (var index = 0; index < fieldPatterns.Length; index++)
        {
            BindEnumVariantFieldPattern(fieldPatterns[index], variant.Fields[index], scope, summary);
        }

        return true;
    }

    private void BindEnumNamedFieldPattern(
        StarkParser.EnumNamedFieldPatternContext enumNamedFieldPattern,
        StarkTypeSymbol switchType,
        ValidationScope scope,
        FunctionValidationBuilder summary)
    {
        if (!TryResolveEnumCaseReference(enumNamedFieldPattern.enumCaseTarget().GetText(), out var enumType, out _, out var variant)
            || switchType.Kind != StarkTypeKind.Named
            || switchType.NamedType is null
            || !string.Equals(switchType.NamedType, enumType.Name, StringComparison.Ordinal)
            || !variant.UsesNamedFields)
        {
            return;
        }

        var seenMembers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var member in enumNamedFieldPattern.namedPatternPayload().namedPatternMember())
        {
            var memberName = member.Identifier().GetText();
            var field = variant.Fields.FirstOrDefault(candidate => string.Equals(candidate.Name, memberName, StringComparison.Ordinal));
            if (field is null || !seenMembers.Add(memberName))
            {
                continue;
            }

            BindEnumVariantFieldPattern(member.pattern(), field, scope, summary);
        }
    }

    private void BindEnumVariantFieldPattern(
        StarkParser.PatternContext pattern,
        EnumVariantFieldSymbol field,
        ValidationScope scope,
        FunctionValidationBuilder summary)
    {
        if (pattern.VAR() is not null
            && pattern.Identifier() is { } capture)
        {
            DeclareVariable(
                scope,
                new VariableSymbol(capture.GetText(), field.Type, SymbolOrigin.Local, LocalStorageClass.None, IsMutable: false, IsConstant: false),
                summary,
                capture.Symbol);
            return;
        }

        if (pattern.enumNamedFieldPattern() is { } enumNamedFieldPattern)
        {
            BindEnumNamedFieldPattern(enumNamedFieldPattern, field.Type, scope, summary);
            return;
        }

        if (pattern.genericEnumAggregatePattern() is { } genericEnumAggregatePattern)
        {
            TryBindEnumAggregateSwitchPattern(genericEnumAggregatePattern, field.Type, scope, summary);
            return;
        }

        if (pattern.aggregatePattern() is { } aggregatePattern)
        {
            if (TryBindEnumAggregateSwitchPattern(aggregatePattern, field.Type, scope, summary))
            {
                return;
            }

            BindAggregateSwitchPattern(aggregatePattern, field.Type, scope, summary);
        }
    }

    private void BindAggregateFieldPattern(
        StarkParser.PatternContext pattern,
        FieldSymbol field,
        ValidationScope scope,
        FunctionValidationBuilder summary)
    {
        if (pattern.VAR() is not null
            && pattern.Identifier() is { } capture
            && SupportsAggregateFieldSubpattern(field.Type))
        {
            DeclareVariable(
                scope,
                new VariableSymbol(capture.GetText(), field.Type, SymbolOrigin.Local, LocalStorageClass.None, IsMutable: false, IsConstant: false),
                summary,
                capture.Symbol);
            return;
        }

        if (pattern.enumNamedFieldPattern() is { } enumNamedFieldPattern)
        {
            BindEnumNamedFieldPattern(enumNamedFieldPattern, field.Type, scope, summary);
            return;
        }

        if (pattern.genericEnumAggregatePattern() is { } genericEnumAggregatePattern
            && TryBindEnumAggregateSwitchPattern(genericEnumAggregatePattern, field.Type, scope, summary))
        {
            return;
        }

        if (pattern.aggregatePattern() is { } enumAggregatePattern
            && TryBindEnumAggregateSwitchPattern(enumAggregatePattern, field.Type, scope, summary))
        {
            return;
        }

        if (pattern.aggregatePattern() is not { } aggregatePattern
            || field.Type.Kind != StarkTypeKind.Named
            || field.Type.NamedType is null
            || !_typeModel.NamedTypes.TryGetValue(field.Type.NamedType, out var namedType))
        {
            return;
        }

        var patternType = ResolvePatternSimpleType(aggregatePattern.simpleType());
        if (patternType.Kind != StarkTypeKind.Named
            || patternType.NamedType is null
            || !string.Equals(field.Type.NamedType, patternType.NamedType, StringComparison.Ordinal))
        {
            return;
        }

        var suffix = aggregatePattern.aggregatePatternSuffix();
        if (suffix is null)
        {
            return;
        }

        if (suffix.Identifier() is { } wholeCapture)
        {
            DeclareVariable(
                scope,
                new VariableSymbol(wholeCapture.GetText(), field.Type, SymbolOrigin.Local, LocalStorageClass.None, IsMutable: false, IsConstant: false),
                summary,
                wholeCapture.Symbol);
            return;
        }

        var fieldPatterns = suffix.pattern();
        if (fieldPatterns.Length != namedType.OrderedFields.Count)
        {
            return;
        }

        for (var index = 0; index < fieldPatterns.Length; index++)
        {
            BindAggregateFieldPattern(fieldPatterns[index], namedType.OrderedFields[index], scope, summary);
        }
    }

    private StarkTypeSymbol ResolvePatternSimpleType(StarkParser.SimpleTypeContext simpleType)
    {
        return _typeResolver.ResolveSimpleType(simpleType, currentModuleName: CurrentModuleName);
    }

    private bool IsEnumSwitchType(StarkTypeSymbol switchType)
    {
        return switchType.Kind == StarkTypeKind.Named
            && switchType.NamedType is not null
            && _typeModel.NamedTypes.TryGetValue(switchType.NamedType, out var namedType)
            && namedType.Kind == DeclarationKind.Enum;
    }

    private static bool SupportsAggregateFieldSubpattern(StarkTypeSymbol type)
    {
        return type.Kind is StarkTypeKind.Bool
            or StarkTypeKind.Integer
            or StarkTypeKind.Float
            or StarkTypeKind.RawPointer;
    }

    private ValidationValue InvokeCall(
        ValidationValue target,
        StarkParser.ArgumentListContext arguments,
        ValidationScope scope,
        FunctionDeclarationModel currentFunction,
        FunctionEffectProfile currentEffects,
        FunctionValidationBuilder summary)
    {
        if (target.EnumConstructor is not null)
        {
            foreach (var argument in arguments.argument())
            {
                EvaluateExpression(
                    argument.expression(),
                    scope,
                    currentFunction,
                    currentEffects,
                    summary,
                    allowFunctionReference: false,
                    ExpressionObservation.Read);
            }

            return new ValidationValue(target.Type, NamedType: target.NamedType);
        }

        var argumentValues = arguments.argument()
            .Select(argument => EvaluateExpression(
                argument.expression(),
                scope,
                currentFunction,
                currentEffects,
                summary,
                allowFunctionReference: false,
                ExpressionObservation.Read))
            .ToArray();

        var overloadSourceName = target.OverloadSourceName;
        if (overloadSourceName is not null || target.OverloadCandidates is { Count: > 0 })
        {
            IReadOnlyList<TypedFunctionSignature> overloads;
            if (target.OverloadCandidates is { Count: > 0 } overloadCandidates)
            {
                overloads = overloadCandidates;
            }
            else if (!TryGetFunctionOverloads(overloadSourceName!, out overloads))
            {
                return UnresolvedCallValue(summary);
            }

            var resolution = FunctionOverloadFacts.Resolve(
                overloads,
                target.Receiver?.Type,
                argumentValues.Select(static argument => argument.Type).ToArray(),
                TypeCompatibilityFacts.CanAssign,
                ResolveAssociatedTypeForSubstitution);
            if (!resolution.Succeeded)
            {
                return UnresolvedCallValue(summary);
            }

            target = target with
            {
                Function = resolution.Match,
                OverloadSourceName = null,
                OverloadCandidates = null,
                Type = resolution.Match!.ReturnType,
                NamedType = ResolveNamedTypeSymbol(resolution.Match.ReturnType)
            };
        }

        if (target.Function is null)
        {
            if (target.Type.Kind == StarkTypeKind.FunctionPointer)
            {
                summary.MarkOpaqueCall();
                ValidateIndirectCallKind(target.Type, currentFunction, summary, arguments);
                return new ValidationValue(
                    target.Type.FunctionPointerReturnType ?? StarkTypeSymbols.Error,
                    NamedType: ResolveNamedTypeSymbol(target.Type.FunctionPointerReturnType ?? StarkTypeSymbols.Error));
            }

            if (target.Type.Kind == StarkTypeKind.Closure)
            {
                summary.MarkOpaqueCall();
                ValidateClosureCallKind(target.Type, currentFunction, summary, arguments);
                return new ValidationValue(
                    target.Type.ClosureReturnType ?? StarkTypeSymbols.Error,
                    NamedType: ResolveNamedTypeSymbol(target.Type.ClosureReturnType ?? StarkTypeSymbols.Error));
            }

            return UnresolvedCallValue(summary);
        }

        var receiverOffset = target.Receiver is null ? 0 : 1;
        var explicitParameterCount = Math.Max(0, target.Function.Parameters.Count - receiverOffset);

        if (CompileTimeStructuralFacts.IsSignature(target.Function))
        {
            return new ValidationValue(
                target.Function.ReturnType,
                NamedType: ResolveNamedTypeSymbol(target.Function.ReturnType));
        }

        if (_effectModel.Functions.TryGetValue(target.Function.Name, out var calleeEffects))
        {
            summary.CalledFunctions.Add(target.Function.Name);

            if (calleeEffects.IsFfi)
            {
                summary.MarkOpaqueCall();

                if (target.Receiver is not null
                    && target.Function.Parameters.Count != 0
                    && target.Receiver.Type.BorrowKind != StarkBorrowKind.None)
                {
                    BorrowError(summary, "STK4001", $"Safe borrows may not cross an 'ffi' boundary. Argument 1 to '{target.Function.DisplaySourceName}' must use a raw pointer form instead.", arguments);
                }

                for (var index = 0; index < argumentValues.Length; index++)
                {
                    var argumentValue = argumentValues[index];
                    if (argumentValue.Type.BorrowKind != StarkBorrowKind.None)
                    {
                        BorrowError(summary, "STK4001", $"Safe borrows may not cross an 'ffi' boundary. Argument {index + receiverOffset + 1} to '{target.Function.DisplaySourceName}' must use a raw pointer form instead.", arguments.argument(index));
                    }
                }

                summary.PendingCalls.Add(new PendingCall(
                    target.Function.Name,
                    BuildPendingCallArguments(target, argumentValues, receiverOffset, explicitParameterCount),
                    arguments.Start));
                return new ValidationValue(target.Function.ReturnType, NamedType: ResolveNamedTypeSymbol(target.Function.ReturnType));
            }

            if (!target.Function.HasBody && !_importedFunctionSemantics.ContainsKey(target.Function.Name))
            {
                summary.MarkOpaqueCall();
            }
        }

        ValidatePendingCallArguments(target, argumentValues, receiverOffset, explicitParameterCount, summary, arguments);

        if (target.Receiver?.Type.Kind == StarkTypeKind.DynTrait)
        {
            summary.MarkOpaqueCall();

            // A dynamic dispatch invokes an unknown concrete implementation that
            // accesses the object behind the trait object's data pointer -- memory
            // that is NOT this function's argument memory. Recording the abstract
            // trait method as a precise callee would let the enclosing function be
            // marked `argmemonly`, which LLVM would miscompile. Apply conservative
            // effects instead, preserving `law` purity (every conforming impl shares
            // the trait method's kind, so a `law` method never writes memory).
            var dynamicDispatchIsLaw = FunctionKindFacts.IsLaw(target.Function.Kind);
            summary.ApplyFunctionMemoryEffects(new FunctionMemoryEffectSummary(
                ReadsArgumentMemory: true,
                WritesArgumentMemory: !dynamicDispatchIsLaw,
                CapturesArgumentMemory: false,
                ReadsOtherMemory: true,
                WritesOtherMemory: !dynamicDispatchIsLaw));
            return BuildCallReturnValue(target, argumentValues, receiverOffset, explicitParameterCount);
        }

        summary.PendingCalls.Add(new PendingCall(
            target.Function.Name,
            BuildPendingCallArguments(target, argumentValues, receiverOffset, explicitParameterCount),
            arguments.Start));

        return BuildCallReturnValue(target, argumentValues, receiverOffset, explicitParameterCount);
    }

    /// <summary>
    /// An unresolvable callee must not leave the enclosing function looking pure:
    /// claiming memory(none)/argmemonly for a body that actually performs a call
    /// would let LLVM delete or reorder the call's effects. Destructor bodies hit
    /// this routinely — their generic 'self' member calls only resolve during MIR
    /// lowering — and a dropped Close()/free() that the optimizer erases is a
    /// miscompile, so the summary degrades to conservative memory effects.
    /// </summary>
    private static ValidationValue UnresolvedCallValue(FunctionValidationBuilder summary)
    {
        summary.MarkOpaqueCall();
        summary.ApplyFunctionMemoryEffects(new FunctionMemoryEffectSummary(
            ReadsArgumentMemory: true,
            WritesArgumentMemory: true,
            CapturesArgumentMemory: false,
            ReadsOtherMemory: true,
            WritesOtherMemory: true));
        return new ValidationValue(StarkTypeSymbols.Error);
    }

    private ValidationValue BuildCallReturnValue(
        ValidationValue target,
        IReadOnlyList<ValidationValue> argumentValues,
        int receiverOffset,
        int explicitParameterCount)
    {
        if (target.Function is null)
        {
            return new ValidationValue(StarkTypeSymbols.Error);
        }

        var returnType = target.Function.ReturnType;
        if (returnType.BorrowKind == StarkBorrowKind.None)
        {
            return new ValidationValue(returnType, NamedType: ResolveNamedTypeSymbol(returnType));
        }

        var returnedRoot = TryInferBorrowedCallReturnRoot(
            target,
            argumentValues,
            receiverOffset,
            explicitParameterCount);
        if (returnedRoot is null)
        {
            return new ValidationValue(returnType, NamedType: ResolveNamedTypeSymbol(returnType));
        }

        var source = returnedRoot.Value;
        var isAddressMutable = CanMutateReturnedBorrow(returnType, source.Value);
        return new ValidationValue(
            returnType,
            IsAssignable: isAddressMutable && returnType.ElementType is null,
            RootSymbol: source.Value.RootSymbol,
            NamedType: ResolveNamedTypeSymbol(returnType),
            IsIndirectStorageAccess: true,
            IsAddressMutable: isAddressMutable,
            UsesFrozenProjectionSemantics: UsesFrozenProjectionSemantics(source.Value)
                || returnType.AccessKind == StarkAccessKind.Frozen,
            HasConstProvenance: HasConstProvenance(source.Value));
    }

    private static (int ParameterIndex, ValidationValue Value)? TryInferBorrowedCallReturnRoot(
        ValidationValue target,
        IReadOnlyList<ValidationValue> argumentValues,
        int receiverOffset,
        int explicitParameterCount)
    {
        if (target.Function is null)
        {
            return null;
        }

        if (target.Receiver is { RootSymbol: not null } receiver
            && target.Function.Parameters.Count != 0
            && CanAliasCalleeParameterMemory(target.Function.Parameters[0].Type))
        {
            return (0, receiver);
        }

        (int ParameterIndex, ValidationValue Value)? candidate = null;
        for (var index = 0; index < Math.Min(explicitParameterCount, argumentValues.Count); index++)
        {
            var parameterIndex = index + receiverOffset;
            if (argumentValues[index].RootSymbol is null
                || parameterIndex >= target.Function.Parameters.Count
                || !CanAliasCalleeParameterMemory(target.Function.Parameters[parameterIndex].Type))
            {
                continue;
            }

            if (candidate is not null)
            {
                return null;
            }

            candidate = (parameterIndex, argumentValues[index]);
        }

        return candidate;
    }

    private static bool CanMutateReturnedBorrow(StarkTypeSymbol returnType, ValidationValue source)
    {
        if (returnType.AccessKind == StarkAccessKind.Frozen)
        {
            return false;
        }

        return source.IsAddressMutable
            && (returnType.IsMutableView
                || returnType.IsMutablePointer
                || returnType.InitializationKind != StarkInitializationKind.None);
    }

    private void ValidatePendingCallArguments(
        ValidationValue target,
        IReadOnlyList<ValidationValue> argumentValues,
        int receiverOffset,
        int explicitParameterCount,
        FunctionValidationBuilder summary,
        StarkParser.ArgumentListContext arguments)
    {
        if (target.Function is null)
        {
            return;
        }

        if (target.Receiver is not null && target.Function.Parameters.Count != 0)
        {
            var receiverParameter = target.Function.Parameters[0];
            ValidateRegisterStorageBackedUse(target.Receiver, receiverParameter.Type, arguments);
            ValidateBorrowArgumentFlow(target.Receiver.Type, receiverParameter.Type, target.Function.DisplaySourceName, 0, summary, arguments);
        }

        for (var index = 0; index < Math.Min(explicitParameterCount, argumentValues.Count); index++)
        {
            var parameter = target.Function.Parameters[index + receiverOffset];
            var argumentValue = argumentValues[index];
            ValidateRegisterStorageBackedUse(argumentValue, parameter.Type, arguments.argument(index));
            ValidateBorrowArgumentFlow(argumentValue.Type, parameter.Type, target.Function.DisplaySourceName, index + receiverOffset, summary, arguments.argument(index));
        }
    }

    private IReadOnlyList<PendingCallArgument> BuildPendingCallArguments(
        ValidationValue target,
        IReadOnlyList<ValidationValue> argumentValues,
        int receiverOffset,
        int explicitParameterCount)
    {
        if (target.Function is null)
        {
            return [];
        }

        var pendingArguments = new List<PendingCallArgument>();
        if (target.Receiver is not null && target.Function.Parameters.Count != 0)
        {
            pendingArguments.Add(CreatePendingCallArgument(0, target.Receiver, target.Function.Parameters[0], target.Function.ReturnType));
        }

        for (var index = 0; index < Math.Min(explicitParameterCount, argumentValues.Count); index++)
        {
            var parameter = target.Function.Parameters[index + receiverOffset];
            pendingArguments.Add(CreatePendingCallArgument(index + receiverOffset, argumentValues[index], parameter, target.Function.ReturnType));
        }

        return pendingArguments;
    }

    private void ValidateIndirectCallKind(
        StarkTypeSymbol functionPointerType,
        FunctionDeclarationModel currentFunction,
        FunctionValidationBuilder summary,
        ParserRuleContext location)
    {
        var pointerKind = functionPointerType.FunctionPointerKind ?? StarkFunctionKind.Fn;
        if (FunctionKindFacts.IsLaw(currentFunction.Kind) && !FunctionKindFacts.IsLaw(pointerKind))
        {
            summary.DisqualifyLaw();
            EffectError(
                summary,
                "STK4106",
                $"Law '{currentFunction.Name}' may only call law-compatible function pointers.",
                location);
        }

        if (FunctionKindFacts.IsFinite(currentFunction.Kind) && !FunctionKindFacts.IsFinite(pointerKind))
        {
            summary.DisqualifyFinite();
            EffectError(
                summary,
                "STK4107",
                $"Finite function '{currentFunction.Name}' may only call finite-compatible function pointers.",
                location);
        }
    }

    private void ValidateClosureCallKind(
        StarkTypeSymbol closureType,
        FunctionDeclarationModel currentFunction,
        FunctionValidationBuilder summary,
        ParserRuleContext location)
    {
        var closureKind = closureType.ClosureFunctionKind ?? StarkFunctionKind.Fn;
        if (FunctionKindFacts.IsLaw(currentFunction.Kind) && !FunctionKindFacts.IsLaw(closureKind))
        {
            summary.DisqualifyLaw();
            EffectError(
                summary,
                "STK4106",
                $"Law '{currentFunction.Name}' may only call law-compatible closures.",
                location);
        }

        if (FunctionKindFacts.IsFinite(currentFunction.Kind) && !FunctionKindFacts.IsFinite(closureKind))
        {
            summary.DisqualifyFinite();
            EffectError(
                summary,
                "STK4107",
                $"Finite function '{currentFunction.Name}' may only call finite-compatible closures.",
                location);
        }
    }

    private void ValidateBorrowArgumentFlow(
        StarkTypeSymbol argumentType,
        StarkTypeSymbol parameterType,
        string calleeName,
        int argumentIndex,
        FunctionValidationBuilder summary,
        ParserRuleContext context)
    {
        if (argumentType.BorrowKind == StarkBorrowKind.Borrow
            && parameterType.BorrowKind != StarkBorrowKind.Borrow)
        {
            BorrowError(summary, "STK4002", $"Non-escaping borrows may only be passed to 'borrow' parameters. Argument {argumentIndex + 1} to '{calleeName}' would escape too far.", context);
        }

        if (argumentType.BorrowKind == StarkBorrowKind.RetBorrow
            && parameterType.BorrowKind != StarkBorrowKind.Borrow)
        {
            BorrowError(summary, "STK4003", $"A 'retborrow' value may only be passed to a non-escaping 'borrow' parameter unless it is returned directly.", context);
        }

        if (parameterType.Kind == StarkTypeKind.RawPointer && argumentType.BorrowKind != StarkBorrowKind.None)
        {
            BorrowError(summary, "STK4001", $"Safe borrows may not be converted implicitly to raw pointers when calling '{calleeName}'.", context);
        }
    }

    private ValidationValue ApplyIndex(
        ValidationValue target,
        StarkParser.ExpressionListContext indexes,
        ValidationScope scope,
        FunctionDeclarationModel function,
        FunctionEffectProfile effects,
        FunctionValidationBuilder summary)
    {
        if (target.Type.Kind == StarkTypeKind.Dynamic && target.Type.ElementType is not null)
        {
            var indexExpressions = indexes.expression();
            foreach (var indexExpression in indexExpressions)
            {
                EvaluateExpression(indexExpression, scope, function, effects, summary, allowFunctionReference: false, ExpressionObservation.Read);
            }

            var elementType = UsesFrozenProjectionSemantics(target)
                ? StarkTypeSymbols.FreezeReachableView(target.Type.ElementType)
                : ProjectFrozenView(target.Type, target.Type.ElementType);
            var isAddressMutable = target.IsAddressMutable
                && target.Type.AccessKind != StarkAccessKind.Frozen
                && elementType.AccessKind != StarkAccessKind.Frozen;
            var resultType = indexExpressions.Length == 2
                ? StarkTypeSymbols.ApplyQualifiers(StarkTypeSymbols.Slice(elementType), isMutableView: isAddressMutable)
                : elementType;
            return new ValidationValue(
                resultType,
                IsAssignable: indexExpressions.Length == 1 && isAddressMutable,
                RootSymbol: target.RootSymbol,
                NamedType: ResolveNamedTypeSymbol(resultType),
                IsIndirectStorageAccess: true,
                IsAddressMutable: isAddressMutable,
                UsesFrozenProjectionSemantics: UsesFrozenProjectionSemantics(target)
                    || elementType.AccessKind == StarkAccessKind.Frozen,
                ReadsIndirectStorageForAddress: target.ReadsIndirectStorageForAddress
                    || target.Type.BorrowKind == StarkBorrowKind.StoreBorrow,
                HasConstProvenance: HasConstProvenance(target));
        }

        if (target.Type.Kind is StarkTypeKind.Ascii or StarkTypeKind.Unicode)
        {
            foreach (var indexExpression in indexes.expression())
            {
                EvaluateExpression(indexExpression, scope, function, effects, summary, allowFunctionReference: false, ExpressionObservation.Read);
            }

            return new ValidationValue(
                target.Type,
                NamedType: ResolveNamedTypeSymbol(target.Type));
        }

        var currentType = target.Type;
        var currentIsAddressMutable = target.IsAddressMutable;
        var currentUsesFrozenProjectionSemantics = UsesFrozenProjectionSemantics(target);
        foreach (var indexExpression in indexes.expression())
        {
            EvaluateExpression(indexExpression, scope, function, effects, summary, allowFunctionReference: false, ExpressionObservation.Read);
            if (currentType.ElementType is null)
            {
                currentType = StarkTypeSymbols.Error;
                continue;
            }

            currentIsAddressMutable = currentType.Kind == StarkTypeKind.RawPointer
                ? currentType.IsMutablePointer
                : currentIsAddressMutable && currentType.AccessKind != StarkAccessKind.Frozen;
            currentType = currentUsesFrozenProjectionSemantics
                ? StarkTypeSymbols.FreezeReachableView(currentType.ElementType)
                : ProjectFrozenView(currentType, currentType.ElementType);
            currentIsAddressMutable &= currentType.AccessKind != StarkAccessKind.Frozen;
            currentUsesFrozenProjectionSemantics = currentUsesFrozenProjectionSemantics
                || currentType.AccessKind == StarkAccessKind.Frozen;
        }

        return new ValidationValue(
            currentType,
            IsAssignable: currentIsAddressMutable,
            RootSymbol: target.RootSymbol,
            NamedType: ResolveNamedTypeSymbol(currentType),
            IsIndirectStorageAccess: true,
            IsAddressMutable: currentIsAddressMutable,
            UsesFrozenProjectionSemantics: currentUsesFrozenProjectionSemantics,
            ReadsIndirectStorageForAddress: target.ReadsIndirectStorageForAddress
                || target.Type.BorrowKind == StarkBorrowKind.StoreBorrow,
            HasConstProvenance: HasConstProvenance(target));
    }

    private ValidationValue ApplyMemberAccess(ValidationValue target, string memberName)
    {
        if (target.NamespaceName is not null)
        {
            var qualifiedName = $"{target.NamespaceName}.{memberName}";
            if (_moduleGraph.CanAccessModule(CurrentModuleName, qualifiedName))
            {
                return new ValidationValue(StarkTypeSymbols.Error, NamespaceName: qualifiedName);
            }

            if (_moduleGraph.CanAccessModuleNamespace(CurrentModuleName, qualifiedName))
            {
                return new ValidationValue(StarkTypeSymbols.Error, NamespaceName: qualifiedName);
            }

            if (_typeModel.Globals.TryGetValue(qualifiedName, out var globalType))
            {
                var isMutable = globalType.IsMutable;
                return new ValidationValue(
                    globalType.Type,
                    IsAssignable: isMutable,
                    RootSymbol: new VariableSymbol(
                        qualifiedName,
                        globalType.Type,
                        SymbolOrigin.Global,
                        LocalStorageClass.Static,
                        isMutable,
                        IsConstant: !isMutable,
                        BindingKind: globalType.BindingKind,
                        HasConstProvenance: globalType.BindingKind == GlobalBindingKind.Const),
                    NamedType: ResolveNamedTypeSymbol(globalType.Type),
                    IsAddressMutable: isMutable,
                    HasConstProvenance: globalType.BindingKind == GlobalBindingKind.Const);
            }

            if (TryGetFunctionOverloads(qualifiedName, out var namespaceFunctions))
            {
                namespaceFunctions = FilterDirectCallableTypeMemberFunctions(qualifiedName, namespaceFunctions);
                if (namespaceFunctions.Count == 0)
                {
                    return new ValidationValue(StarkTypeSymbols.Error);
                }

                if (namespaceFunctions.Count == 1 && !namespaceFunctions[0].IsGeneric)
                {
                    return new ValidationValue(namespaceFunctions[0].ReturnType, Function: namespaceFunctions[0]);
                }

                return new ValidationValue(StarkTypeSymbols.Error, OverloadSourceName: qualifiedName);
            }

            if (TryResolveNamedTypeBySourceName(qualifiedName, out var qualifiedType))
            {
                if (qualifiedType.Kind is DeclarationKind.Struct or DeclarationKind.Record)
                {
                    return new ValidationValue(StarkTypeSymbols.Error, NamespaceName: qualifiedName, NamedType: qualifiedType);
                }

                if (qualifiedType.Kind == DeclarationKind.Enum)
                {
                    return new ValidationValue(StarkTypeSymbols.Error, NamespaceName: qualifiedName);
                }

                if (qualifiedType.Kind == DeclarationKind.Doctrine)
                {
                    return new ValidationValue(StarkTypeSymbols.Named(qualifiedType.Name), NamedType: qualifiedType);
                }

                if (qualifiedType.Kind == DeclarationKind.Trait)
                {
                    return new ValidationValue(StarkTypeSymbols.Error);
                }
            }

            if (TryResolveEnumCaseReference(qualifiedName, out var enumType, out var enumTypeSymbol, out var variant))
            {
                if (variant.IsUnit)
                {
                    return new ValidationValue(enumTypeSymbol, NamedType: enumType);
                }

                return new ValidationValue(
                    enumTypeSymbol,
                    NamedType: enumType,
                    EnumConstructor: new EnumConstructorBinding(qualifiedName, variant));
            }
        }

        if (TryApplyValueTextConversionMemberAccess(target, memberName, out var valueTextConversion))
        {
            return valueTextConversion;
        }

        if (target.Type.Kind == StarkTypeKind.Dynamic)
        {
            if (string.Equals(memberName, "Length", StringComparison.Ordinal)
                || string.Equals(memberName, "Capacity", StringComparison.Ordinal))
            {
                return new ValidationValue(
                    NonNegativeI64Type,
                    RootSymbol: target.RootSymbol,
                    NamedType: ResolveNamedTypeSymbol(NonNegativeI64Type),
                    IsIndirectStorageAccess: true,
                    IsAddressMutable: false,
                    UsesFrozenProjectionSemantics: UsesFrozenProjectionSemantics(target),
                    HasConstProvenance: HasConstProvenance(target));
            }

            return new ValidationValue(StarkTypeSymbols.Error);
        }

        if (target.Type.Kind == StarkTypeKind.DynTrait)
        {
            return ApplyDynTraitMemberAccess(target, memberName);
        }

        var namedType = target.NamedType ?? ResolveNamedTypeSymbol(target.Type);
        if (namedType is null)
        {
            return new ValidationValue(StarkTypeSymbols.Error);
        }

        if (namedType.Fields.TryGetValue(memberName, out var field))
        {
            var projectedType = ProjectProjectionType(target, field.Type);
            return new ValidationValue(
                projectedType,
                IsAssignable: CanMutateAddressProjection(target, projectedType),
                RootSymbol: target.RootSymbol,
                NamedType: ResolveNamedTypeSymbol(projectedType),
                IsIndirectStorageAccess: true,
                IsAddressMutable: CanMutateAddressProjection(target, projectedType),
                UsesFrozenProjectionSemantics: UsesFrozenProjectionSemantics(target),
                ReadsIndirectStorageForAddress: target.ReadsIndirectStorageForAddress
                    || target.Type.BorrowKind == StarkBorrowKind.StoreBorrow,
                HasConstProvenance: HasConstProvenance(target));
        }

        var methodSourceName = $"{StarkTypeSymbols.GetGenericBaseName(namedType.Name)}.{memberName}";
        if (namedType.Kind == DeclarationKind.Doctrine
            && TryGetFunctionOverloads(methodSourceName, out var doctrineMethods))
        {
            return doctrineMethods.Count == 1 && !doctrineMethods[0].IsGeneric
                ? new ValidationValue(
                    doctrineMethods[0].ReturnType,
                    Function: doctrineMethods[0],
                    NamedType: ResolveNamedTypeSymbol(doctrineMethods[0].ReturnType))
                : new ValidationValue(StarkTypeSymbols.Error, OverloadSourceName: methodSourceName);
        }

        if (TryGetFunctionOverloads(methodSourceName, out var methods))
        {
            var instanceMethods = methods.Where(static method => !method.IsStatic).ToArray();
            if (instanceMethods.Length == 1 && !instanceMethods[0].IsGeneric && instanceMethods[0].Parameters.Count != 0)
            {
                return new ValidationValue(
                    instanceMethods[0].ReturnType,
                    Function: instanceMethods[0],
                    NamedType: ResolveNamedTypeSymbol(instanceMethods[0].ReturnType),
                    Receiver: target);
            }

            return instanceMethods.Length == 0
                ? new ValidationValue(StarkTypeSymbols.Error)
                : new ValidationValue(StarkTypeSymbols.Error, Receiver: target, OverloadSourceName: methodSourceName);
        }

        return new ValidationValue(
            StarkTypeSymbols.Error);
    }

    // Resolves `receiver.Member(...)` on a `dyn Trait` receiver to the trait
    // method's signature (with `Self` bound to the trait-object type), mirroring
    // type checking so the call validates and its effects are analyzed. The
    // resulting binding carries the dyn receiver, which the call analysis uses to
    // apply conservative (dynamic-dispatch) memory effects.
    private ValidationValue ApplyDynTraitMemberAccess(ValidationValue target, string memberName)
    {
        if (target.Type.DynTraitName is not { } traitName)
        {
            return new ValidationValue(StarkTypeSymbols.Error);
        }

        if (TryApplyDynTraitRepresentationMemberAccess(target, memberName, out var representationMember))
        {
            return representationMember;
        }

        var methodSourceName = $"{StarkTypeSymbols.GetGenericBaseName(traitName)}.{memberName}";
        if (!TryGetFunctionOverloads(methodSourceName, out var methods)
            || methods.Where(static method => !method.IsStatic).ToArray() is not { Length: 1 } instanceMethods)
        {
            return new ValidationValue(StarkTypeSymbols.Error);
        }

        var traitMethod = instanceMethods[0];
        var substitution = new Dictionary<string, StarkTypeSymbol>(StringComparer.Ordinal)
        {
            ["Self"] = target.Type,
        };
        if (_typeModel.NamedTypes.TryGetValue(traitName, out var traitSymbol) && target.Type.TypeArguments is { } traitArguments)
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

        return new ValidationValue(
            resolvedMethod.ReturnType,
            Function: resolvedMethod,
            NamedType: ResolveNamedTypeSymbol(resolvedMethod.ReturnType),
            Receiver: target);
    }

    private bool TryApplyDynTraitRepresentationMemberAccess(
        ValidationValue target,
        string memberName,
        out ValidationValue binding)
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

        binding = new ValidationValue(
            fieldType,
            NamedType: ResolveNamedTypeSymbol(fieldType),
            IsIndirectStorageAccess: true,
            IsAddressMutable: false,
            HasConstProvenance: target.HasConstProvenance);
        return true;
    }

    private StarkTypeSymbol? ResolveAssociatedTypeForSubstitution(StarkTypeSymbol ownerType, string associatedTypeName)
    {
        return AssociatedTypeFacts.TryResolveAssociatedType(ownerType, associatedTypeName, _typeModel.NamedTypes, out var targetType)
            ? targetType
            : null;
    }

    private bool TryApplyValueTextConversionMemberAccess(
        ValidationValue target,
        string memberName,
        out ValidationValue value)
    {
        value = default!;

        if (!TryGetValueTextConversionSourceName(memberName, out var sourceName)
            || !TryGetFunctionOverloads(sourceName, out var overloads))
        {
            return false;
        }

        var candidates = overloads
            .Where(static overload => !overload.IsStatic)
            .Where(overload => overload.Parameters.Count != 0
                && FunctionOverloadFacts.CanBindReceiver(overload.Parameters[0].Type, target.Type, TypeCompatibilityFacts.CanAssign))
            .ToArray();
        if (candidates.Length == 0)
        {
            return false;
        }

        if (candidates.Length == 1 && !candidates[0].IsGeneric)
        {
            value = new ValidationValue(
                candidates[0].ReturnType,
                Function: candidates[0],
                NamedType: ResolveNamedTypeSymbol(candidates[0].ReturnType),
                Receiver: target);
            return true;
        }

        value = new ValidationValue(StarkTypeSymbols.Error, Receiver: target, OverloadSourceName: sourceName);
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

    private ValidationValue CreateConvertedValidationValue(StarkTypeSymbol targetType, ValidationValue operand, ParserRuleContext context)
    {
        ValidateRegisterStorageBackedUse(operand, targetType, context);

        return PreservesStorageView(targetType, operand.Type)
            ? new ValidationValue(
                targetType,
                RootSymbol: operand.RootSymbol,
                NamedType: ResolveNamedTypeSymbol(targetType),
                IsIndirectStorageAccess: operand.IsIndirectStorageAccess,
                UsesFrozenProjectionSemantics: operand.UsesFrozenProjectionSemantics,
                HasConstProvenance: HasConstProvenance(operand))
            : new ValidationValue(targetType, NamedType: ResolveNamedTypeSymbol(targetType));
    }

    private void ValidateRegisterStorageBackedUse(ValidationValue value, StarkTypeSymbol targetType, ParserRuleContext context)
    {
        if (value.RootSymbol is not { Origin: SymbolOrigin.Local, StorageClass: LocalStorageClass.Register } registerLocal
            || !RequiresStableStorage(value.Type, targetType))
        {
            return;
        }

        _context.Diagnostics.Error(
            "STK4016",
            $"Register local '{registerLocal.Name}' cannot be used where stable storage is required. Use 'stack' storage when a borrow, out/init destination, or slice view is required.",
            "semantic-validate",
            Location(context));
    }

    private static bool RequiresStableStorage(StarkTypeSymbol sourceType, StarkTypeSymbol targetType)
    {
        return targetType.BorrowKind != StarkBorrowKind.None
            || targetType.InitializationKind != StarkInitializationKind.None
            || targetType.Kind == StarkTypeKind.Slice && sourceType.Kind == StarkTypeKind.FixedArray;
    }

    private bool TryGetFunctionOverloads(string sourceName, out IReadOnlyList<TypedFunctionSignature> overloads)
    {
        if (_typeModel.Overloads.TryGetValue(sourceName, out overloads!))
        {
            return true;
        }

        if (TryResolveTypeQualifiedMemberSourceName(sourceName, out var resolvedMemberSourceName)
            && _typeModel.Overloads.TryGetValue(resolvedMemberSourceName, out overloads!))
        {
            return true;
        }

        if (!sourceName.Contains('.', StringComparison.Ordinal)
            && _typeModel.Overloads.TryGetValue($"{CurrentModuleName}.{sourceName}", out overloads!))
        {
            return true;
        }

        if (!sourceName.Contains('.', StringComparison.Ordinal))
        {
            var importedCandidates = new List<TypedFunctionSignature>();
            foreach (var candidateName in _moduleGraph.EnumerateAccessibleModuleQualifiedNames(CurrentModuleName, sourceName))
            {
                if (_typeModel.Overloads.TryGetValue(candidateName, out var candidates))
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

    private string GetSystemTextFunctionName(string name)
    {
        return string.Equals(CurrentModuleName, "System.Text", StringComparison.Ordinal)
            ? name
            : $"System.Text.{name}";
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

    private IReadOnlyList<TypedFunctionSignature> FilterDirectCallableTypeMemberFunctions(
        string sourceName,
        IReadOnlyList<TypedFunctionSignature> functions)
    {
        return IsStructOrRecordMemberFunctionSourceName(sourceName)
            ? functions.Where(static function => function.IsStatic).ToArray()
            : functions;
    }

    private bool IsStructOrRecordMemberFunctionSourceName(string sourceName)
    {
        var separator = sourceName.LastIndexOf('.');
        if (separator <= 0)
        {
            return false;
        }

        var typeName = sourceName[..separator];
        return TryResolveNamedTypeBySourceName(typeName, out var namedType)
            && namedType.Kind is DeclarationKind.Struct or DeclarationKind.Record;
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
            || !_typeModel.NamedTypes.TryGetValue(enumTypeSymbol.NamedType, out var resolvedEnumType)
            || resolvedEnumType.Kind != DeclarationKind.Enum
            || !resolvedEnumType.TryGetVariant(genericEnumCaseReference.Identifier().GetText(), out var resolvedVariant, out _))
        {
            enumTypeSymbol = StarkTypeSymbols.Error;
            return false;
        }

        enumType = resolvedEnumType;
        variant = resolvedVariant;
        return true;
    }

    private StarkTypeSymbol ResolveGenericQualifiedName(StarkParser.GenericQualifiedNameContext genericQualifiedName)
    {
        var baseName = genericQualifiedName.qualifiedName().GetText();
        if (_typeResolver.TryResolveGenericTypeAlias(
                baseName,
                CurrentModuleName,
                genericQualifiedName.qualifiedName().Start,
                genericQualifiedName.typeArgumentList(),
                _currentFunctionGenericParameters,
                _currentFunctionComptimeGenericParameters,
                out var aliasType))
        {
            return aliasType;
        }

        var baseType = _typeResolver.ResolveQualifiedType(
            baseName,
            _currentFunctionGenericParameters,
            genericQualifiedName.qualifiedName().Start,
            CurrentModuleName);
        if (baseType.Kind == StarkTypeKind.Error)
        {
            return StarkTypeSymbols.Error;
        }

        if (!_typeModel.NamedTypes.TryGetValue(baseType.NamedType ?? baseName, out var namedType))
        {
            return StarkTypeSymbols.Error;
        }

        var arguments = GenericArgumentSyntaxFacts.Resolve(
            genericQualifiedName.typeArgumentList(),
            namedType.GenericParams,
            namedType.ComptimeGenericParams,
            ResolveType,
            static (_, _, _) => { });
        if (arguments.TypeArguments.Any(static type => type.Kind == StarkTypeKind.Error))
        {
            return StarkTypeSymbols.Error;
        }

        return StarkTypeSymbols.GenericInstantiation(
            baseType.NamedType ?? baseName,
            arguments.TypeArguments,
            arguments.ComptimeValueArguments);
    }

    private ValidationValue ResolveGenericQualifiedNameValue(
        StarkParser.GenericQualifiedNameContext genericQualifiedName,
        bool allowFunctionReference)
    {
        var baseName = genericQualifiedName.qualifiedName().GetText();
        if (CompileTimeStructuralFacts.TryGetFactKind(baseName, out _))
        {
            if (!allowFunctionReference
                || !CompileTimeStructuralFacts.TryResolveArguments(
                    baseName,
                    genericQualifiedName,
                    ResolveType,
                    static (_, _, _) => { },
                    default,
                    _currentFunctionComptimeGenericParameters,
                    comptimeValueSubstitution: null,
                    out var structuralArguments))
            {
                return new ValidationValue(StarkTypeSymbols.Error);
            }

            if (structuralArguments.TargetType.Kind == StarkTypeKind.Error
                || structuralArguments.AdditionalTypeArguments.Any(static argument => argument.Kind == StarkTypeKind.Error))
            {
                return new ValidationValue(StarkTypeSymbols.Error);
            }

            CompileTimeStructuralFacts.TryCreateSignature(baseName, structuralArguments, out var signature);
            return new ValidationValue(
                signature.ReturnType,
                Function: signature,
                NamedType: ResolveNamedTypeSymbol(signature.ReturnType));
        }

        if (TryGetFunctionOverloads(baseName, out var overloads))
        {
            var syntaxArgumentCount = genericQualifiedName.typeArgumentList().genericArgument().Length;
            var instantiatedCandidates = new List<TypedFunctionSignature>(overloads.Count);
            foreach (var candidate in overloads)
            {
                if (candidate.GenericParams.Count + candidate.ComptimeGenericParams.Count != syntaxArgumentCount)
                {
                    continue;
                }

                var arguments = GenericArgumentSyntaxFacts.Resolve(
                    genericQualifiedName.typeArgumentList(),
                    candidate.GenericParams,
                    candidate.ComptimeGenericParams,
                    ResolveType,
                    static (_, _, _) => { },
                    visibleComptimeParameters: _currentFunctionComptimeGenericParameters);
                if (arguments.TypeArguments.Count != candidate.GenericParams.Count
                    || arguments.ComptimeValueArguments.Count != candidate.ComptimeGenericParams.Count
                    || arguments.TypeArguments.Any(static type => type.Kind == StarkTypeKind.Error))
                {
                    continue;
                }

                instantiatedCandidates.Add(FunctionOverloadFacts.InstantiateSignature(
                    candidate,
                    arguments.TypeArguments,
                    candidate.Name,
                    ResolveAssociatedTypeForSubstitution,
                    arguments.ComptimeValueArguments));
            }

            if (instantiatedCandidates.Count > 0 && !allowFunctionReference)
            {
                return new ValidationValue(StarkTypeSymbols.Error);
            }

            if (instantiatedCandidates.Count == 1)
            {
                var signature = instantiatedCandidates[0];
                return new ValidationValue(
                    signature.ReturnType,
                    Function: signature,
                    NamedType: ResolveNamedTypeSymbol(signature.ReturnType));
            }

            if (instantiatedCandidates.Count > 1)
            {
                return new ValidationValue(
                    StarkTypeSymbols.Error,
                    OverloadSourceName: baseName,
                    OverloadCandidates: instantiatedCandidates);
            }
        }

        var targetType = ResolveGenericQualifiedName(genericQualifiedName);
        var namedType = ResolveNamedTypeSymbol(targetType);
        if (namedType?.Kind is DeclarationKind.Struct or DeclarationKind.Record)
        {
            return new ValidationValue(
                StarkTypeSymbols.Error,
                NamespaceName: targetType.NamedType,
                NamedType: namedType);
        }

        if (namedType?.Kind == DeclarationKind.Enum)
        {
            return new ValidationValue(
                StarkTypeSymbols.Error,
                NamespaceName: targetType.NamedType,
                NamedType: namedType);
        }

        return new ValidationValue(targetType, NamedType: namedType);
    }

    private bool TryResolveGlobalBySourceName(string name, out TypedGlobalSymbol global)
    {
        if (_typeModel.Globals.TryGetValue(name, out global!))
        {
            return true;
        }

        if (!name.Contains('.', StringComparison.Ordinal)
            && _typeModel.Globals.TryGetValue($"{CurrentModuleName}.{name}", out global!))
        {
            return true;
        }

        if (!name.Contains('.', StringComparison.Ordinal))
        {
            var importedMatches = _moduleGraph.EnumerateAccessibleModuleQualifiedNames(CurrentModuleName, name)
                .Where(_typeModel.Globals.ContainsKey)
                .ToArray();
            if (importedMatches.Length == 1)
            {
                global = _typeModel.Globals[importedMatches[0]];
                return true;
            }
        }

        global = null!;
        return false;
    }

    private bool TryResolveNamedTypeBySourceName(string typeName, out NamedTypeSymbol namedType)
    {
        if (_typeModel.NamedTypes.TryGetValue(typeName, out namedType!))
        {
            return true;
        }

        if (!typeName.Contains('.', StringComparison.Ordinal)
            && _typeModel.NamedTypes.TryGetValue($"{CurrentModuleName}.{typeName}", out namedType!))
        {
            return true;
        }

        if (!typeName.Contains('.', StringComparison.Ordinal))
        {
            var importedMatches = _moduleGraph.EnumerateAccessibleModuleQualifiedNames(CurrentModuleName, typeName)
                .Where(_typeModel.NamedTypes.ContainsKey)
                .ToArray();
            if (importedMatches.Length == 1)
            {
                namedType = _typeModel.NamedTypes[importedMatches[0]];
                return true;
            }
        }

        namedType = null!;
        return false;
    }

    private ValidationValue CreateAddressOfValidationValue(ValidationValue operand, ParserRuleContext context)
    {
        if (operand.RootSymbol is null)
        {
            return new ValidationValue(StarkTypeSymbols.Error);
        }

        if (operand.RootSymbol.Origin == SymbolOrigin.Local
            && operand.RootSymbol.StorageClass == LocalStorageClass.Register)
        {
            _context.Diagnostics.Error(
                "STK4016",
                $"Register local '{operand.RootSymbol.Name}' cannot be addressed. Use 'stack' storage when a stable address or raw pointer is required.",
                "semantic-validate",
                Location(context));
            return new ValidationValue(StarkTypeSymbols.Error);
        }

        var pointeeType = UsesFrozenProjectionSemantics(operand)
            ? StarkTypeSymbols.FreezeAddressPointeeType(operand.Type)
            : operand.Type;
        var pointerType = StarkTypeSymbols.RawPointer(pointeeType, operand.IsAddressMutable);
        return new ValidationValue(
            pointerType,
            RootSymbol: operand.RootSymbol,
            NamedType: ResolveNamedTypeSymbol(pointerType),
            IsIndirectStorageAccess: true,
            HasConstProvenance: HasConstProvenance(operand));
    }

    private ValidationValue CreateDereferenceValidationValue(ValidationValue operand)
    {
        if (operand.Type.Kind != StarkTypeKind.RawPointer || operand.Type.ElementType is null)
        {
            return new ValidationValue(StarkTypeSymbols.Error);
        }

        var pointeeType = operand.Type.ElementType;
        return new ValidationValue(
            pointeeType,
            IsAssignable: operand.Type.IsMutablePointer && pointeeType.AccessKind != StarkAccessKind.Frozen,
            RootSymbol: operand.RootSymbol,
            NamedType: ResolveNamedTypeSymbol(pointeeType),
            IsIndirectStorageAccess: true,
            IsAddressMutable: operand.Type.IsMutablePointer && pointeeType.AccessKind != StarkAccessKind.Frozen,
            HasConstProvenance: HasConstProvenance(operand));
    }

    private PendingCallArgument CreatePendingCallArgument(
        int argumentIndex,
        ValidationValue argumentValue,
        TypedParameterSymbol calleeParameter,
        StarkTypeSymbol calleeReturnType)
    {
        var aliasing = CanAliasCalleeParameterMemory(calleeParameter.Type);
        var callerParameterName = aliasing && argumentValue.RootSymbol?.Origin == SymbolOrigin.Parameter
            ? argumentValue.RootSymbol.Name
            : null;
        var fallbackEffects = DeriveFallbackArgumentEffects(calleeParameter, calleeReturnType, hasBody: false);
        return new PendingCallArgument(argumentIndex, callerParameterName, calleeParameter.Name, aliasing, argumentValue.RootSymbol, fallbackEffects);
    }

    private void RecordObservedMemoryRead(ValidationValue value, FunctionValidationBuilder summary)
    {
        if (value.RootSymbol is null)
        {
            return;
        }

        if (value.RootSymbol.Origin == SymbolOrigin.Parameter
            && (value.IsIndirectStorageAccess || value.Type.Kind == StarkTypeKind.Dynamic))
        {
            summary.MarkParameterRead(value.RootSymbol.Name);
            return;
        }

        if (TouchesOtherMemory(value))
        {
            summary.MarkOtherMemoryRead();
        }
    }

    private void RecordObservedMemoryWrite(ValidationValue value, FunctionValidationBuilder summary)
    {
        if (value.RootSymbol is null)
        {
            return;
        }

        if (value.RootSymbol.Origin == SymbolOrigin.Parameter
            && (value.IsIndirectStorageAccess || value.Type.Kind == StarkTypeKind.Dynamic))
        {
            if (value.ReadsIndirectStorageForAddress)
            {
                summary.MarkParameterRead(value.RootSymbol.Name);
            }

            summary.MarkParameterWrite(value.RootSymbol.Name);
            return;
        }

        if (TouchesOtherMemory(value))
        {
            summary.MarkOtherMemoryWrite();
        }
    }

    private void RecordReturnCapture(ValidationValue value, FunctionDeclarationModel function, FunctionValidationBuilder summary)
    {
        if (value.RootSymbol?.Origin != SymbolOrigin.Parameter)
        {
            return;
        }

        var captureKind = function.ReturnType switch
        {
            var returnType when returnType.Contains("storeborrow", StringComparison.Ordinal) => ParameterCaptureKind.Escape,
            var returnType when returnType.Contains("retborrow", StringComparison.Ordinal) => ParameterCaptureKind.Return,
            _ => ParameterCaptureKind.None
        };

        if (captureKind != ParameterCaptureKind.None)
        {
            summary.MarkParameterCapture(value.RootSymbol.Name, captureKind);
        }
    }

    private void FinalizeMemoryEffectsAndValidateCalls()
    {
        var changed = true;
        while (changed)
        {
            changed = false;

            foreach (var summary in AllEffectSummaries())
            {
                foreach (var pendingCall in summary.PendingCalls)
                {
                    changed |= summary.ApplyFunctionMemoryEffects(GetCallMemoryEffects(pendingCall));

                    foreach (var argument in pendingCall.Arguments)
                    {
                        if (!argument.AliasesCalleeMemory)
                        {
                            continue;
                        }

                        var propagated = GetCallArgumentEffects(pendingCall.CalleeName, argument.CalleeParameterName, argument.FallbackEffects);
                        if (argument.CallerParameterName is not null)
                        {
                            changed |= summary.ApplyArgumentEffects(argument.CallerParameterName, propagated);
                            continue;
                        }

                        if (argument.RootSymbol is not null && AliasedArgumentTouchesOtherMemory(argument.RootSymbol))
                        {
                            changed |= summary.ApplyAliasedOtherMemoryEffects(propagated);
                        }
                    }
                }

                foreach (var potentialDrop in summary.PotentialDropTypes)
                {
                    if (TryGetDropMemoryEffects(
                            potentialDrop.Type,
                            new HashSet<string>(StringComparer.Ordinal),
                            out var dropEffects))
                    {
                        changed |= summary.ApplyFunctionMemoryEffects(dropEffects);
                    }
                }
            }
        }

        foreach (var summary in _summaries.Values)
        {
            var declaredLaw = FunctionKindFacts.IsLaw(summary.DeclaredKind);
            ValidateImplicitDropEffects(summary, declaredLaw);

            foreach (var pendingCall in summary.PendingCalls)
            {
                foreach (var argument in pendingCall.Arguments)
                {
                    if (argument.RootSymbol is null || !IsExternallyVisibleMemory(argument.RootSymbol))
                    {
                        continue;
                    }

                    var propagated = GetCallArgumentEffects(pendingCall.CalleeName, argument.CalleeParameterName, argument.FallbackEffects);
                    if (!propagated.Writes && propagated.CaptureKind == ParameterCaptureKind.None)
                    {
                        continue;
                    }

                    if (!propagated.Writes && propagated.CaptureKind == ParameterCaptureKind.Return)
                    {
                        continue;
                    }

                    summary.DisqualifyLaw();

                    if (!declaredLaw)
                    {
                        continue;
                    }

                    var operation = propagated.Writes && propagated.CaptureKind != ParameterCaptureKind.None
                        ? "perform externally visible writes or captures"
                        : propagated.Writes
                            ? "perform externally visible writes"
                            : "capture externally visible memory";
                    EffectError(
                        summary,
                        "STK4104",
                        $"Law '{summary.Name}' cannot {operation} through call '{pendingCall.CalleeName}'.",
                        pendingCall.Location);
                }
            }

            summary.BuildResolvedCalls(call =>
                BuildResolvedCallSummary(call));
        }
    }

    private IEnumerable<FunctionValidationBuilder> AllEffectSummaries()
    {
        foreach (var summary in _summaries.Values)
        {
            yield return summary;
        }

        foreach (var summary in _destructorSummaries.Values)
        {
            yield return summary;
        }
    }

    private void ValidateImplicitDropEffects(FunctionValidationBuilder summary, bool declaredLaw)
    {
        foreach (var potentialDrop in summary.PotentialDropTypes)
        {
            if (!TryGetDropMemoryEffects(
                    potentialDrop.Type,
                    new HashSet<string>(StringComparer.Ordinal),
                    out var dropEffects)
                || !dropEffects.ReadsOtherMemory && !dropEffects.WritesOtherMemory)
            {
                continue;
            }

            summary.DisqualifyLaw();
            if (!declaredLaw || !summary.MarkImplicitDropDiagnosticReported(potentialDrop.Type))
            {
                continue;
            }

            var code = dropEffects.WritesOtherMemory ? "STK4104" : "STK4105";
            var operation = dropEffects.WritesOtherMemory
                ? "perform externally visible writes"
                : "read externally visible memory";
            EffectError(
                summary,
                code,
                $"Law '{summary.Name}' cannot implicitly {operation} by dropping '{potentialDrop.Type.DisplayName}'. The destructor for that type or a nested owned field has externally visible memory effects.",
                potentialDrop.Location);
        }
    }

    private ArgumentEffects GetCallArgumentEffects(string calleeName, string calleeParameterName, ArgumentEffects fallback)
    {
        if (_summaries.TryGetValue(calleeName, out var summary)
            && summary.TryGetParameter(calleeParameterName, out var parameter))
        {
            return parameter.GetEffectiveEffects(summary.HasBody);
        }

        if (_importedFunctionSemantics.TryGetValue(calleeName, out var importedSummary)
            && importedSummary.Parameters is not null)
        {
            var importedParameter = importedSummary.Parameters.FirstOrDefault(parameter =>
                string.Equals(parameter.Name, calleeParameterName, StringComparison.Ordinal));
            if (importedParameter is not null)
            {
                return new ArgumentEffects(
                    importedParameter.Reads,
                    importedParameter.Writes,
                    importedParameter.CaptureKind);
            }
        }

        return fallback;
    }

    private CallMemoryEffectSummary BuildResolvedCallSummary(PendingCall call)
    {
        var argumentSummaries = call.Arguments
            .OrderBy(static argument => argument.ArgumentIndex)
            .Select(argument =>
            {
                var effects = GetCallArgumentEffects(call.CalleeName, argument.CalleeParameterName, argument.FallbackEffects);
                return new CallArgumentMemoryEffectSummary(
                    argument.ArgumentIndex,
                    argument.CallerParameterName,
                    argument.CalleeParameterName,
                    effects.Reads,
                    effects.Writes,
                    effects.CaptureKind);
            })
            .ToArray();

        return new CallMemoryEffectSummary(
            call.CalleeName,
            GetCallMemoryEffects(call),
            argumentSummaries);
    }

    private FunctionMemoryEffectSummary GetCallMemoryEffects(PendingCall call)
    {
        if (_summaries.TryGetValue(call.CalleeName, out var summary))
        {
            var memoryEffects = summary.GetCurrentMemoryEffects();
            return _effectModel.Functions.TryGetValue(call.CalleeName, out var ffiEffects)
                   && ffiEffects.IsFfi
                   && !ffiEffects.IsPure
                ? memoryEffects with
                {
                    ReadsOtherMemory = true,
                    WritesOtherMemory = true
                }
                : memoryEffects;
        }

        if (_importedFunctionSemantics.TryGetValue(call.CalleeName, out var importedSummary)
            && importedSummary.MemoryEffects is not null)
        {
            return importedSummary.MemoryEffects;
        }

        if (_effectModel.Functions.TryGetValue(call.CalleeName, out var effects))
        {
            if (effects.IsPure)
            {
                return new FunctionMemoryEffectSummary(
                    ReadsArgumentMemory: effects.ReadsArgumentMemory,
                    WritesArgumentMemory: false,
                    CapturesArgumentMemory: false);
            }

            return new FunctionMemoryEffectSummary(
                ReadsArgumentMemory: effects.ReadsArgumentMemory,
                WritesArgumentMemory: effects.ReadsArgumentMemory,
                CapturesArgumentMemory: false,
                ReadsOtherMemory: true,
                WritesOtherMemory: true);
        }

        return new FunctionMemoryEffectSummary(
            ReadsArgumentMemory: false,
            WritesArgumentMemory: false,
            CapturesArgumentMemory: false,
            ReadsOtherMemory: true,
            WritesOtherMemory: true);
    }

    private bool TryGetDropMemoryEffects(
        StarkTypeSymbol type,
        ISet<string> activeNamedTypes,
        out FunctionMemoryEffectSummary effects)
    {
        effects = new FunctionMemoryEffectSummary(false, false, false);

        if (type.BorrowKind != StarkBorrowKind.None
            || type.InitializationKind != StarkInitializationKind.None
            || type.Kind == StarkTypeKind.Error)
        {
            return false;
        }

        if (type.Kind == StarkTypeKind.Dynamic)
        {
            effects = new FunctionMemoryEffectSummary(
                ReadsArgumentMemory: false,
                WritesArgumentMemory: false,
                CapturesArgumentMemory: false,
                ReadsOtherMemory: true,
                WritesOtherMemory: true);
            return true;
        }

        if (type.Kind == StarkTypeKind.FixedArray && type.ElementType is not null)
        {
            return TryGetDropMemoryEffects(type.ElementType, activeNamedTypes, out effects);
        }

        if (type.Kind != StarkTypeKind.Named || type.NamedType is null)
        {
            return false;
        }

        var namedTypeName = type.NamedType;
        if (!activeNamedTypes.Add(namedTypeName))
        {
            return false;
        }

        try
        {
            var found = false;
            var combined = new FunctionMemoryEffectSummary(false, false, false);
            if (TryGetDestructorSummary(namedTypeName, out var destructorSummary))
            {
                combined = CombineMemoryEffects(combined, destructorSummary.GetCurrentMemoryEffects());
                found = true;
            }

            if (_typeModel.NamedTypes.TryGetValue(namedTypeName, out var namedType))
            {
                foreach (var field in namedType.OrderedFields)
                {
                    if (TryGetDropMemoryEffects(field.Type, activeNamedTypes, out var fieldEffects))
                    {
                        combined = CombineMemoryEffects(combined, fieldEffects);
                        found = true;
                    }
                }

                foreach (var variant in namedType.Variants)
                {
                    foreach (var field in variant.Fields)
                    {
                        if (TryGetDropMemoryEffects(field.Type, activeNamedTypes, out var fieldEffects))
                        {
                            combined = CombineMemoryEffects(combined, fieldEffects);
                            found = true;
                        }
                    }
                }
            }

            effects = combined;
            return found;
        }
        finally
        {
            activeNamedTypes.Remove(namedTypeName);
        }
    }

    private bool TryGetDestructorSummary(string namedTypeName, out FunctionValidationBuilder summary)
    {
        if (_destructorSummaries.TryGetValue(namedTypeName, out summary!))
        {
            return true;
        }

        var genericBaseName = StarkTypeSymbols.GetGenericBaseName(namedTypeName);
        return !string.Equals(genericBaseName, namedTypeName, StringComparison.Ordinal)
            && _destructorSummaries.TryGetValue(genericBaseName, out summary!);
    }

    private static FunctionMemoryEffectSummary CombineMemoryEffects(
        FunctionMemoryEffectSummary left,
        FunctionMemoryEffectSummary right)
    {
        return new FunctionMemoryEffectSummary(
            left.ReadsArgumentMemory || right.ReadsArgumentMemory,
            left.WritesArgumentMemory || right.WritesArgumentMemory,
            left.CapturesArgumentMemory || right.CapturesArgumentMemory,
            left.ReadsOtherMemory || right.ReadsOtherMemory,
            left.WritesOtherMemory || right.WritesOtherMemory,
            left.InitializesArgumentMemory || right.InitializesArgumentMemory,
            left.HasPointeeDeadOnReturnArgument || right.HasPointeeDeadOnReturnArgument);
    }

    private void InferEffectiveFunctionKindsAndValidateDeclaredContracts()
    {
        var effectiveLaw = _summaries.Values.ToDictionary(
            static summary => summary.Name,
            static summary => summary.DirectLawCompatible,
            StringComparer.Ordinal);
        var effectiveFinite = _summaries.Values.ToDictionary(
            static summary => summary.Name,
            static summary => summary.DirectFiniteCompatible,
            StringComparer.Ordinal);

        var changed = true;
        while (changed)
        {
            changed = false;

            foreach (var summary in _summaries.Values.Where(static summary => summary.HasBody))
            {
                if (effectiveLaw[summary.Name]
                    && summary.CalledFunctions.Any(callee => !IsEffectiveLaw(callee, effectiveLaw)))
                {
                    effectiveLaw[summary.Name] = false;
                    changed = true;
                }

                if (effectiveFinite[summary.Name]
                    && summary.CalledFunctions.Any(callee => !IsEffectiveFinite(callee, effectiveFinite)))
                {
                    effectiveFinite[summary.Name] = false;
                    changed = true;
                }
            }
        }

        var finiteCycles = FindFiniteCycles(effectiveFinite);

        foreach (var function in finiteCycles)
        {
            effectiveFinite[function] = false;
        }

        foreach (var summary in _summaries.Values)
        {
            summary.SetEffectiveKind(
                summary.HasBody
                    ? FunctionKindFacts.Combine(effectiveLaw[summary.Name], effectiveFinite[summary.Name])
                    : summary.DeclaredKind);
        }

        foreach (var summary in _summaries.Values)
        {
            var declaredLaw = FunctionKindFacts.IsLaw(summary.DeclaredKind);
            var declaredFinite = FunctionKindFacts.IsFinite(summary.DeclaredKind);

            foreach (var pendingCall in summary.PendingCalls)
            {
                if (declaredLaw && !IsEffectiveLaw(pendingCall.CalleeName, effectiveLaw))
                {
                    EffectError(summary, "STK4106", $"Law '{summary.Name}' may only call other laws.", pendingCall.Location);
                }

                if (declaredFinite
                    && !IsEffectiveFinite(pendingCall.CalleeName, effectiveFinite)
                    && !(finiteCycles.Contains(summary.Name) && finiteCycles.Contains(pendingCall.CalleeName)))
                {
                    EffectError(summary, "STK4107", $"Finite function '{summary.Name}' may only call finite functions, but calls non-finite function '{pendingCall.CalleeName}'.", pendingCall.Location);
                }
            }
        }

        foreach (var function in finiteCycles)
        {
            if (_functionDeclarations.TryGetValue(function, out var declaration)
                && _summaries.TryGetValue(function, out var summary)
                && FunctionKindFacts.IsFinite(summary.DeclaredKind))
            {
                EffectError(summary, "STK4108", $"Finite function '{function}' participates in a recursive call cycle and cannot be proven finite.", declaration.NameToken);
            }
        }
    }

    private bool IsEffectiveLaw(string functionName, IReadOnlyDictionary<string, bool> effectiveLaw)
    {
        if (_summaries.TryGetValue(functionName, out var summary))
        {
            return summary.HasBody
                ? effectiveLaw[functionName]
                : FunctionKindFacts.IsLaw(summary.DeclaredKind);
        }

        return _effectModel.Functions.TryGetValue(functionName, out var effects) && effects.IsPure;
    }

    private bool IsEffectiveFinite(string functionName, IReadOnlyDictionary<string, bool> effectiveFinite)
    {
        if (_summaries.TryGetValue(functionName, out var summary))
        {
            return summary.HasBody
                ? effectiveFinite[functionName]
                : FunctionKindFacts.IsFinite(summary.DeclaredKind);
        }

        return _effectModel.Functions.TryGetValue(functionName, out var effects) && effects.WillReturn && effects.MustProgress;
    }

    private HashSet<string> FindFiniteCycles(IReadOnlyDictionary<string, bool> effectiveFinite)
    {
        var visited = new Dictionary<string, VisitState>(StringComparer.Ordinal);
        var stack = new List<string>();
        var cyclic = new HashSet<string>(StringComparer.Ordinal);

        foreach (var function in effectiveFinite.Where(static pair => pair.Value).Select(static pair => pair.Key))
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

            if (_summaries.TryGetValue(function, out var summary) && summary.HasBody)
            {
                foreach (var callee in summary.CalledFunctions.Where(callee => IsEffectiveFinite(callee, effectiveFinite)))
                {
                    Visit(callee);
                }
            }

            stack.RemoveAt(stack.Count - 1);
            visited[function] = VisitState.Visited;
        }
    }

    private void ValidateTypeUsage(
        ParserRuleContext context,
        StarkTypeSymbol type,
        TypeUsage usage,
        bool isFfiBoundary = false,
        bool isPlatformAbiBoundary = false)
    {
        ValidateIntegerRangeStorageRules(type, usage, context, isFfiBoundary, isPlatformAbiBoundary);

        if (usage == TypeUsage.Global && type.InitializationKind != StarkInitializationKind.None)
        {
            _context.Diagnostics.Error(
                "STK4004",
                $"'{type.InitializationKind.ToString().ToLowerInvariant()}' types are not valid for global storage.",
                "semantic-validate",
                Location(context.Start));
        }

        if (usage == TypeUsage.Local
            && type.InitializationKind != StarkInitializationKind.None
            && type.Kind != StarkTypeKind.Slice)
        {
            _context.Diagnostics.Error(
                "STK4004",
                $"Local '{type.InitializationKind.ToString().ToLowerInvariant()}' views must be slice views such as '{type.InitializationKind.ToString().ToLowerInvariant()} T[]'.",
                "semantic-validate",
                Location(context.Start));
        }

        if (usage == TypeUsage.Return && type.InitializationKind != StarkInitializationKind.None)
        {
            _context.Diagnostics.Error(
                "STK4004",
                "Function return types may not use 'out' or 'init'.",
                "semantic-validate",
                Location(context.Start));
        }

        if (usage == TypeUsage.Global && (type.BorrowKind is StarkBorrowKind.Borrow or StarkBorrowKind.RetBorrow))
        {
            _context.Diagnostics.Error(
                "STK4005",
                $"Global declarations may not use '{type.BorrowKind.ToString().ToLowerInvariant()}' because those borrows are not allowed to escape globally.",
                "semantic-validate",
                Location(context.Start));
        }

        if (usage == TypeUsage.Field && TryFindNonStorableBorrowType(type, includeTopLevel: true, out var nonStorableFieldBorrow))
        {
            _context.Diagnostics.Error(
                "STK4005",
                $"Field declarations may not store '{nonStorableFieldBorrow.DisplayName}' because 'borrow' values cannot be stored and 'retborrow' values may escape only through a return. Use 'storeborrow' only for deliberately stored borrowed views, or store an owned value instead.",
                "semantic-validate",
                Location(context.Start));
        }

        if (usage == TypeUsage.Global && TryFindNonStorableBorrowType(type, includeTopLevel: false, out var nonStorableGlobalBorrow))
        {
            _context.Diagnostics.Error(
                "STK4005",
                $"Global declaration type '{type.DisplayName}' contains '{nonStorableGlobalBorrow.DisplayName}', but non-escaping borrows are not allowed to escape globally. Use 'storeborrow' only for deliberately stored borrowed views, or store an owned value instead.",
                "semantic-validate",
                Location(context.Start));
        }

        if (usage == TypeUsage.Return && TryFindNonStorableBorrowType(type, includeTopLevel: false, out var nonStorableReturnBorrow))
        {
            _context.Diagnostics.Error(
                "STK4005",
                $"Return type '{type.DisplayName}' contains '{nonStorableReturnBorrow.DisplayName}', but 'borrow' values cannot be returned and nested 'retborrow' values cannot be lifetime-checked through owned aggregate storage. Return the borrow directly or use an owned value instead.",
                "semantic-validate",
                Location(context.Start));
        }

        if (ContainsNestedRawPointer(type)
            && !((isFfiBoundary && usage is TypeUsage.Parameter or TypeUsage.Return)
                || (isPlatformAbiBoundary && usage == TypeUsage.Field)))
        {
            _context.Diagnostics.Error(
                "STK4006",
                "Pointers to pointers are only permitted on 'ffi' function boundaries or fields of '[Platform]' ABI aggregates through raw pointer types.",
                "semantic-validate",
                Location(context.Start));
        }

        if (TryFindCompileTimeOnlyTypeDependency(type, out var dependencyName, out var dependencyKind))
        {
            _context.Diagnostics.Error(
                "STK4009",
                $"Type '{type.DisplayName}' depends on compile-time-only {DescribeCompileTimeOnlyKind(dependencyKind)} '{dependencyName}', which is not allowed in {DescribeTypeUsage(usage)}. {DescribeNoDynamicDispatchPolicy()}",
                "semantic-validate",
                Location(context.Start));
        }

        if (TryFindInvalidCVoidUse(type, out var invalidCVoidType))
        {
            _context.Diagnostics.Error(
                "STK3050",
                $"Type '{invalidCVoidType.DisplayName}' is an incomplete C pointee type and is valid only as the direct pointee of rawptr<System.C.c_void> or rawmutptr<System.C.c_void>. Use Stark 'void' for functions that return no value.",
                "semantic-validate",
                Location(context.Start));
        }

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

    private static bool TryFindNonStorableBorrowType(
        StarkTypeSymbol type,
        bool includeTopLevel,
        out StarkTypeSymbol nonStorableBorrow)
    {
        if (includeTopLevel && type.BorrowKind is StarkBorrowKind.Borrow or StarkBorrowKind.RetBorrow)
        {
            nonStorableBorrow = type;
            return true;
        }

        if (type.ElementType is not null
            && TryFindNonStorableBorrowType(type.ElementType, includeTopLevel: true, out nonStorableBorrow))
        {
            return true;
        }

        if (type.TypeArguments is not null)
        {
            foreach (var typeArgument in type.TypeArguments)
            {
                if (TryFindNonStorableBorrowType(typeArgument, includeTopLevel: true, out nonStorableBorrow))
                {
                    return true;
                }
            }
        }

        nonStorableBorrow = StarkTypeSymbols.Error;
        return false;
    }

    private bool IsPlatformAbiDeclaration(string localDeclarationName)
    {
        return _syntaxDeclarations.TryGetValue(localDeclarationName, out var declaration)
            && HasPlatformAttribute(declaration.Attributes);
    }

    private bool IsPlatformAbiDeclaration(
        DeclaredFunctionSyntax functionDeclaration,
        FunctionDeclarationModel declaration)
    {
        return HasPlatformAttribute(declaration.Attributes)
            || functionDeclaration.ContainingTypeName is not null
                && IsPlatformAbiDeclaration(functionDeclaration.ContainingTypeName);
    }

    private static bool HasPlatformAttribute(IReadOnlyList<ModuleAttributeModel>? attributes)
    {
        return attributes is not null
            && attributes.Any(static attribute => string.Equals(attribute.Name, "Platform", StringComparison.Ordinal));
    }

    private void ValidateIntegerRangeStorageRules(
        StarkTypeSymbol type,
        TypeUsage usage,
        ParserRuleContext context,
        bool isFfiBoundary,
        bool isPlatformAbiBoundary)
    {
        if (!_context.Options.EnforceIntegerRangeStorageRules
            || type.Kind == StarkTypeKind.Error
            || isFfiBoundary && usage is TypeUsage.Parameter or TypeUsage.Return
            || isPlatformAbiBoundary && usage is TypeUsage.Parameter or TypeUsage.Return or TypeUsage.Field)
        {
            return;
        }

        if (!TryFindIntegerRangeStorageViolation(type, out var violatingType, out var suggestedType, out var violationKind))
        {
            return;
        }

        var message = violationKind switch
        {
            IntegerRangeStorageViolationKind.NonNegativeSigned =>
                $"Integer range '{violatingType.DisplayName}' is non-negative but uses signed storage. Use `{suggestedType.DisplayName}` instead, or keep the signed form only on an ffi boundary or a '[Platform]' ABI declaration.",
            IntegerRangeStorageViolationKind.WiderThanNeeded =>
                $"Integer range '{violatingType.DisplayName}' uses wider storage than required. Use `{suggestedType.DisplayName}` instead, or keep the wider form only on an ffi boundary or a '[Platform]' ABI declaration.",
            _ =>
                $"Integer range '{violatingType.DisplayName}' is not using its canonical storage type. Use `{suggestedType.DisplayName}` instead."
        };

        _context.Diagnostics.Error(
            "STK3014",
            message,
            "semantic-validate",
            Location(context.Start));
    }

    private static bool TryFindIntegerRangeStorageViolation(
        StarkTypeSymbol type,
        out StarkTypeSymbol violatingType,
        out StarkTypeSymbol suggestedType,
        out IntegerRangeStorageViolationKind violationKind)
    {
        if (IntegerRangeStorageFacts.TryGetStorageViolation(type, out suggestedType, out violationKind))
        {
            violatingType = type;
            return true;
        }

        if (type.ElementType is not null
            && TryFindIntegerRangeStorageViolation(type.ElementType, out violatingType, out suggestedType, out violationKind))
        {
            return true;
        }

        if (type.FunctionPointerReturnType is not null
            && TryFindIntegerRangeStorageViolation(type.FunctionPointerReturnType, out violatingType, out suggestedType, out violationKind))
        {
            return true;
        }

        if (type.FunctionPointerParameterTypes is not null)
        {
            foreach (var parameterType in type.FunctionPointerParameterTypes)
            {
                if (TryFindIntegerRangeStorageViolation(parameterType, out violatingType, out suggestedType, out violationKind))
                {
                    return true;
                }
            }
        }

        if (type.ClosureReturnType is not null
            && TryFindIntegerRangeStorageViolation(type.ClosureReturnType, out violatingType, out suggestedType, out violationKind))
        {
            return true;
        }

        if (type.ClosureParameterTypes is not null)
        {
            foreach (var parameterType in type.ClosureParameterTypes)
            {
                if (TryFindIntegerRangeStorageViolation(parameterType, out violatingType, out suggestedType, out violationKind))
                {
                    return true;
                }
            }
        }

        if (type.TypeArguments is not null)
        {
            foreach (var typeArgument in type.TypeArguments)
            {
                if (TryFindIntegerRangeStorageViolation(typeArgument, out violatingType, out suggestedType, out violationKind))
                {
                    return true;
                }
            }
        }

        violatingType = StarkTypeSymbols.Error;
        suggestedType = StarkTypeSymbols.Error;
        violationKind = IntegerRangeStorageViolationKind.None;
        return false;
    }

    private bool RequiresRuntimeDrop(StarkTypeSymbol type, ISet<string> activeNamedTypes)
    {
        if (type.BorrowKind != StarkBorrowKind.None
            || type.InitializationKind != StarkInitializationKind.None)
        {
            return false;
        }

        if (type.Kind == StarkTypeKind.Dynamic)
        {
            return true;
        }

        if (type.Kind == StarkTypeKind.FixedArray && type.ElementType is not null)
        {
            return RequiresRuntimeDrop(type.ElementType, activeNamedTypes);
        }

        if (type.Kind != StarkTypeKind.Named
            || type.NamedType is null
            || !_typeModel.NamedTypes.TryGetValue(type.NamedType, out var namedType))
        {
            return false;
        }

        if (TryGetDestructorSummary(type.NamedType, out _)
            || _syntaxDeclarations.TryGetValue(type.NamedType, out var declaration)
            && declaration.Destructor is not null)
        {
            return true;
        }

        if (!activeNamedTypes.Add(namedType.Name))
        {
            return false;
        }

        try
        {
            foreach (var field in namedType.OrderedFields)
            {
                if (RequiresRuntimeDrop(field.Type, activeNamedTypes))
                {
                    return true;
                }
            }

            foreach (var variant in namedType.Variants)
            {
                foreach (var field in variant.Fields)
                {
                    if (RequiresRuntimeDrop(field.Type, activeNamedTypes))
                    {
                        return true;
                    }
                }
            }

            return false;
        }
        finally
        {
            activeNamedTypes.Remove(namedType.Name);
        }
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
            && _typeModel.NamedTypes.TryGetValue(type.NamedType, out var namedType)
            && namedType.Kind is DeclarationKind.Doctrine or DeclarationKind.Trait)
        {
            dependencyName = namedType.Name;
            dependencyKind = namedType.Kind;
            return true;
        }

        if (type.Kind == StarkTypeKind.Named
            && type.NamedType is not null
            && _typeModel.NamedTypes.TryGetValue(type.NamedType, out var aggregateType))
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

    private static string DescribeTypeUsage(TypeUsage usage)
    {
        return usage switch
        {
            TypeUsage.Global => "global declarations",
            TypeUsage.Parameter => "function parameters",
            TypeUsage.Return => "function return types",
            TypeUsage.Local => "local declarations",
            TypeUsage.Field => "field declarations",
            TypeUsage.Alias => "type aliases",
            TypeUsage.Conversion => "conversion type positions",
            _ => "runtime type positions"
        };
    }

    private bool TryValidateFrozenConstGlobalType(
        StarkTypeSymbol type,
        string path,
        ISet<string> visitingNamedTypes,
        out string failingPath,
        out string failureReason)
    {
        failingPath = path;
        failureReason = string.Empty;

        if (type.Kind == StarkTypeKind.Error)
        {
            return true;
        }

        if (type.BorrowKind != StarkBorrowKind.None)
        {
            failureReason = $"uses borrow-qualified type '{type.DisplayName}'";
            return false;
        }

        if (type.AccessKind == StarkAccessKind.Shared)
        {
            failureReason = $"uses shared access type '{type.DisplayName}'";
            return false;
        }

        if (type.InitializationKind != StarkInitializationKind.None)
        {
            failureReason = $"uses initialization-qualified type '{type.DisplayName}'";
            return false;
        }

        if (type.IsMutableView)
        {
            failureReason = $"uses mutable-view type '{type.DisplayName}'";
            return false;
        }

        switch (type.Kind)
        {
            case StarkTypeKind.RawPointer:
                failureReason = $"uses raw pointer type '{type.DisplayName}'";
                return false;
            case StarkTypeKind.FixedArray:
            case StarkTypeKind.Slice:
                if (type.ElementType is null)
                {
                    return true;
                }

                return TryValidateFrozenConstGlobalType(type.ElementType, $"{path}[]", visitingNamedTypes, out failingPath, out failureReason);
            case StarkTypeKind.Named:
                if (type.NamedType is null || !_typeModel.NamedTypes.TryGetValue(type.NamedType, out var namedType))
                {
                    return true;
                }

                if (!visitingNamedTypes.Add(namedType.Name))
                {
                    return true;
                }

                foreach (var field in namedType.OrderedFields)
                {
                    if (!TryValidateFrozenConstGlobalType(field.Type, $"{path}.{field.Name}", visitingNamedTypes, out failingPath, out failureReason))
                    {
                        visitingNamedTypes.Remove(namedType.Name);
                        return false;
                    }
                }

                visitingNamedTypes.Remove(namedType.Name);
                return true;
            default:
                return true;
        }
    }

    private bool CanMaterializeFrozenConstInitializer(
        StarkParser.VariableInitializerContext initializer,
        StarkTypeSymbol targetType)
    {
        if (targetType.Kind == StarkTypeKind.Error)
        {
            return true;
        }

        if (initializer.expression() is { } expression)
        {
            return CanMaterializeFrozenConstExpression(expression, targetType);
        }

        if (initializer.objectInitializer() is { } objectInitializer)
        {
            return CanMaterializeFrozenConstObjectInitializer(objectInitializer, targetType);
        }

        if (initializer.arrayInitializer() is { } arrayInitializer)
        {
            return CanMaterializeFrozenConstArrayInitializer(arrayInitializer, targetType);
        }

        return false;
    }

    private bool CanMaterializeFrozenConstExpression(
        StarkParser.ExpressionContext expression,
        StarkTypeSymbol targetType)
    {
        if (CompileTimeExpressionEvaluator.TryEvaluate(expression, out var constant)
            && CompileTimeExpressionEvaluator.TryCoerce(constant, targetType, out _))
        {
            return true;
        }

        if (!TryUnwrapSimplePrimaryExpression(expression, out var primaryExpression))
        {
            return false;
        }

        if (primaryExpression.literal() is not null)
        {
            return true;
        }

        if (primaryExpression.objectCreationExpression() is { } objectCreation)
        {
            return CanMaterializeFrozenConstObjectCreation(objectCreation, targetType);
        }

        if (primaryExpression.expression() is { } groupedExpression)
        {
            return CanMaterializeFrozenConstExpression(groupedExpression, targetType);
        }

        return false;
    }

    private bool CanMaterializeFrozenConstObjectCreation(
        StarkParser.ObjectCreationExpressionContext objectCreation,
        StarkTypeSymbol targetType)
    {
        var namedType = ResolveNamedTypeSymbol(targetType);
        if (namedType is null)
        {
            return false;
        }

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
                if (!namedType.TryGetField(parameter.Name, out var field, out _)
                    || !CanMaterializeFrozenConstExpression(arguments[index].expression(), field.Type))
                {
                    return false;
                }
            }
        }

        if (objectCreation.objectInitializer() is { } objectInitializer
            && !CanMaterializeFrozenConstObjectInitializer(objectInitializer, targetType))
        {
            return false;
        }

        return true;
    }

    private bool TryGetObjectCreationConstructor(
        StarkParser.ObjectCreationExpressionContext objectCreation,
        out TypedConstructorShape? constructor)
    {
        if (TryGetObjectCreationTyping(objectCreation, out var typing))
        {
            constructor = typing.Constructor;
            return true;
        }

        constructor = null;
        return false;
    }

    private bool TryGetObjectCreationTyping(
        StarkParser.ObjectCreationExpressionContext objectCreation,
        out ObjectCreationTypingRecord typing)
    {
        return _objectCreations.TryGetValue(
            new ObjectCreationKey(
                objectCreation.GetText(),
                objectCreation.Start.Line,
                objectCreation.Start.Column + 1),
            out typing!);
    }

    private bool CanMaterializeFrozenConstObjectInitializer(
        StarkParser.ObjectInitializerContext objectInitializer,
        StarkTypeSymbol targetType)
    {
        var namedType = ResolveNamedTypeSymbol(targetType);
        if (namedType is null)
        {
            return false;
        }

        foreach (var memberInitializer in objectInitializer.memberInitializer())
        {
            var memberName = memberInitializer.Identifier().GetText();
            if (!namedType.Fields.TryGetValue(memberName, out var field))
            {
                return false;
            }

            if (!CanMaterializeFrozenConstInitializer(memberInitializer.variableInitializer(), field.Type))
            {
                return false;
            }
        }

        return true;
    }

    private bool CanMaterializeFrozenConstArrayInitializer(
        StarkParser.ArrayInitializerContext arrayInitializer,
        StarkTypeSymbol targetType)
    {
        if (targetType.Kind != StarkTypeKind.FixedArray
            || targetType.ElementType is null)
        {
            return false;
        }

        if (targetType.FixedLength is int fixedLength
            && fixedLength != arrayInitializer.variableInitializer().Length)
        {
            return false;
        }

        foreach (var item in arrayInitializer.variableInitializer())
        {
            if (!CanMaterializeFrozenConstInitializer(item, targetType.ElementType))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsExternalConstRawPointerPlaceholder(
        StarkTypeSymbol type,
        StarkParser.VariableInitializerContext initializer)
    {
        return type.Kind == StarkTypeKind.RawPointer
            && !type.IsMutablePointer
            && type.BorrowKind == StarkBorrowKind.None
            && type.AccessKind == StarkAccessKind.None
            && type.InitializationKind == StarkInitializationKind.None
            && !type.IsMutableView
            && initializer.expression() is { } expression
            && TryUnwrapSimplePrimaryExpression(expression, out var primaryExpression)
            && primaryExpression.literal()?.NULL() is not null;
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

    private static bool ContainsNestedRawPointer(StarkTypeSymbol type)
    {
        if (type.Kind == StarkTypeKind.RawPointer)
        {
            if (type.ElementType?.Kind == StarkTypeKind.RawPointer)
            {
                return true;
            }

            return type.ElementType is not null && ContainsNestedRawPointer(type.ElementType);
        }

        return type.ElementType is not null && ContainsNestedRawPointer(type.ElementType);
    }

    private static bool ContainsRawPointer(StarkTypeSymbol type)
    {
        return type.Kind == StarkTypeKind.RawPointer
            || type.ElementType is not null && ContainsRawPointer(type.ElementType);
    }

    private bool IsVisibleMemoryWrite(ValidationValue target)
    {
        if (target.RootSymbol is null)
        {
            return false;
        }

        if (target.RootSymbol.Origin == SymbolOrigin.Global)
        {
            return true;
        }

        return target.IsIndirectStorageAccess && IsExternallyVisibleMemory(target.RootSymbol);
    }

    private bool TouchesOtherMemory(ValidationValue value)
    {
        if (value.RootSymbol is null)
        {
            return false;
        }

        if (value.RootSymbol.Origin == SymbolOrigin.Global)
        {
            return true;
        }

        if (!value.IsIndirectStorageAccess || value.RootSymbol.Origin == SymbolOrigin.Parameter)
        {
            return false;
        }

        return AliasedArgumentTouchesOtherMemory(value.RootSymbol);
    }

    private bool AliasedArgumentTouchesOtherMemory(VariableSymbol symbol)
    {
        if (symbol.Origin == SymbolOrigin.Global)
        {
            return true;
        }

        if (symbol.Origin == SymbolOrigin.Parameter)
        {
            return false;
        }

        if (symbol.StorageClass is LocalStorageClass.Heap or LocalStorageClass.Arena or LocalStorageClass.Static)
        {
            return true;
        }

        return symbol.Type.Kind is StarkTypeKind.RawPointer or StarkTypeKind.Slice
            || symbol.Type.BorrowKind != StarkBorrowKind.None
            || symbol.Type.InitializationKind != StarkInitializationKind.None
            || CanReachStoredBorrow(symbol.Type);
    }

    private static bool PreservesStorageView(StarkTypeSymbol target, StarkTypeSymbol source)
    {
        if (target.Kind == StarkTypeKind.RawPointer && source.Kind == StarkTypeKind.RawPointer)
        {
            return true;
        }

        if (target.Kind == StarkTypeKind.Slice && source.Kind == StarkTypeKind.FixedArray)
        {
            return true;
        }

        return false;
    }

    private bool IsExternallyVisibleMemory(VariableSymbol symbol)
    {
        if (symbol.Origin == SymbolOrigin.Global)
        {
            return true;
        }

        return symbol.Type.Kind is StarkTypeKind.RawPointer or StarkTypeKind.Slice
            || symbol.Type.BorrowKind != StarkBorrowKind.None
            || symbol.Type.InitializationKind != StarkInitializationKind.None
            || symbol.StorageClass is LocalStorageClass.Heap or LocalStorageClass.Arena or LocalStorageClass.Static
            || CanReachStoredBorrow(symbol.Type);
    }

    private bool CanReachStoredBorrow(StarkTypeSymbol type)
    {
        return CanReachStoredBorrow(type, new HashSet<string>(StringComparer.Ordinal));
    }

    private bool CanReachStoredBorrow(StarkTypeSymbol type, HashSet<string> visitedNamedTypes)
    {
        if (type.BorrowKind == StarkBorrowKind.StoreBorrow)
        {
            return true;
        }

        var valueType = StarkTypeSymbols.BorrowReturnValueType(type);
        if (valueType.Kind == StarkTypeKind.FixedArray && valueType.ElementType is not null)
        {
            return CanReachStoredBorrow(valueType.ElementType, visitedNamedTypes);
        }

        if (valueType.Kind != StarkTypeKind.Named
            || valueType.NamedType is not { } namedTypeName
            || !visitedNamedTypes.Add(namedTypeName)
            || !_typeModel.NamedTypes.TryGetValue(namedTypeName, out var namedType))
        {
            return false;
        }

        foreach (var field in namedType.OrderedFields)
        {
            if (CanReachStoredBorrow(field.Type, visitedNamedTypes))
            {
                return true;
            }
        }

        return false;
    }

    private StarkTypeSymbol EvaluateLiteralType(StarkParser.LiteralContext literal)
    {
        if (literal.signedIntegerLiteral() is { } integerLiteral)
        {
            return InferIntegerLiteralType(ParseSignedIntegerLiteral(integerLiteral));
        }

        if (literal.FloatLiteral() is not null)
        {
            return StarkTypeSymbols.Float(32);
        }

        if (literal.StringLiteral() is { } stringLiteral)
        {
            return TextLiteralDecoder.CanUseUtf8Storage(stringLiteral.GetText(), TextLiteralKind.String)
                ? StarkTypeSymbols.Ascii
                : StarkTypeSymbols.Unicode;
        }

        if (literal.CharacterLiteral() is { } characterLiteral)
        {
            return TextLiteralDecoder.CanUseUtf8Storage(characterLiteral.GetText(), TextLiteralKind.Character)
                ? StarkTypeSymbols.Ascii
                : StarkTypeSymbols.Unicode;
        }

        if (literal.TRUE() is not null || literal.FALSE() is not null)
        {
            return StarkTypeSymbols.Bool;
        }

        return StarkTypeSymbols.Null;
    }

    private static ValidationValue EvaluateBinaryChain<TContext>(
        IEnumerable<TContext> operands,
        Func<TContext, ValidationValue> evaluate)
        where TContext : ParserRuleContext
    {
        ValidationValue? current = null;

        foreach (var operand in operands)
        {
            var binding = evaluate(operand);
            current = current is null
                ? binding
                : new ValidationValue(FindCommonType(current.Type, binding.Type));
        }

        return current ?? new ValidationValue(StarkTypeSymbols.Error);
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

    private static StarkParser.LiteralContext? TryGetStandaloneInterpolatedTextLiteral(StarkParser.ExpressionContext expression)
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

    private static bool IsUnicodeConcatSource(StarkTypeSymbol type)
    {
        return type.Kind == StarkTypeKind.Unicode
            || type.Kind == StarkTypeKind.Named
                && string.Equals(type.NamedType, StarkTypeSymbols.OwnedUnicodeName, StringComparison.Ordinal);
    }

    private static bool IsAsciiConcatSource(StarkTypeSymbol type)
    {
        return type.Kind == StarkTypeKind.Ascii
            || type.Kind == StarkTypeKind.Named
                && string.Equals(type.NamedType, StarkTypeSymbols.OwnedAsciiName, StringComparison.Ordinal);
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

    private static StarkTypeSymbol GetFixedTextStorageViewType(StarkTypeSymbol textType)
    {
        return textType.NamedType == StarkTypeSymbols.OwnedUnicodeName
            ? StarkTypeSymbols.Unicode
            : StarkTypeSymbols.Ascii;
    }

    private static StarkTypeSymbol FindCommonTextType(StarkTypeSymbol left, StarkTypeSymbol right)
    {
        return left.Kind == StarkTypeKind.Unicode || right.Kind == StarkTypeKind.Unicode
            ? StarkTypeSymbols.Unicode
            : StarkTypeSymbols.Ascii;
    }

    private static StarkTypeSymbol FindCommonType(StarkTypeSymbol left, StarkTypeSymbol right)
    {
        if (left.Kind == StarkTypeKind.Error || right.Kind == StarkTypeKind.Error)
        {
            return StarkTypeSymbols.Error;
        }

        if (Equals(left, right))
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

    private static BigInteger ParseSignedIntegerLiteral(StarkParser.SignedIntegerLiteralContext literal)
    {
        var value = BigInteger.Parse(literal.IntegerLiteral().GetText());
        return literal.MINUS() is null ? value : -value;
    }

    private static StarkTypeSymbol InferIntegerLiteralType(BigInteger value)
    {
        var widths = new[] { 8, 16, 24, 32, 48, 64, 96, 128, 192, 256, 384, 512, 768, 1024 };
        foreach (var width in widths)
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

    private static StarkTypeSymbol ProjectFrozenView(StarkTypeSymbol sourceType, StarkTypeSymbol projectedType)
    {
        return sourceType.AccessKind == StarkAccessKind.Frozen
            ? StarkTypeSymbols.FreezeReachableView(projectedType)
            : projectedType;
    }

    private static bool UsesFrozenProjectionSemantics(ValidationValue value)
    {
        return value.UsesFrozenProjectionSemantics
            || value.HasConstProvenance
            || value.Type.AccessKind == StarkAccessKind.Frozen
            || value.RootSymbol?.BindingKind == GlobalBindingKind.Const;
    }

    private static bool HasConstProvenance(ValidationValue value)
    {
        return value.HasConstProvenance
            || value.RootSymbol is { HasConstProvenance: true }
            || value.RootSymbol?.BindingKind == GlobalBindingKind.Const;
    }

    private static StarkTypeSymbol ProjectProjectionType(ValidationValue source, StarkTypeSymbol projectedType)
    {
        return UsesFrozenProjectionSemantics(source)
            ? StarkTypeSymbols.FreezeReachableView(projectedType)
            : ProjectFrozenView(source.Type, projectedType);
    }

    private static bool CanFormMutableAddressFromLocal(VariableSymbol local)
    {
        return !local.IsConstant
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

    private static bool CanMutateAddressProjection(ValidationValue target, StarkTypeSymbol projectedType)
    {
        return target.IsAddressMutable
            && target.Type.AccessKind != StarkAccessKind.Frozen
            && projectedType.AccessKind != StarkAccessKind.Frozen;
    }

    private static bool IsMemoryBackedType(StarkTypeSymbol type)
    {
        return type.Kind switch
        {
            StarkTypeKind.RawPointer => true,
            StarkTypeKind.FixedArray => true,
            StarkTypeKind.Slice => true,
            StarkTypeKind.Dynamic => true,
            StarkTypeKind.Ascii => true,
            StarkTypeKind.Unicode => true,
            StarkTypeKind.Named => true,
            _ => type.BorrowKind != StarkBorrowKind.None || type.InitializationKind != StarkInitializationKind.None
        };
    }

    private static bool DeriveGuaranteedNonNull(StarkTypeSymbol type)
    {
        return type.BorrowKind != StarkBorrowKind.None || type.InitializationKind != StarkInitializationKind.None;
    }

    private static bool DeriveGuaranteedReadOnly(StarkTypeSymbol type)
    {
        if (type.InitializationKind != StarkInitializationKind.None)
        {
            return false;
        }

        return (type.Kind == StarkTypeKind.RawPointer && !type.IsMutablePointer)
            || type.Kind is StarkTypeKind.Ascii or StarkTypeKind.Unicode
            || (type.BorrowKind != StarkBorrowKind.None && !type.IsMutableView)
            || type.AccessKind is StarkAccessKind.Shared or StarkAccessKind.Frozen;
    }

    private static bool DeriveGuaranteedNoAlias(
        TypedParameterSymbol parameter,
        IReadOnlyList<TypedParameterSymbol> parameters,
        IReadOnlyList<ParameterDisjointGroup> disjointGroups)
    {
        if (parameter.Type.InitializationKind != StarkInitializationKind.None)
        {
            return true;
        }

        if (!CanAliasCalleeParameterMemory(parameter.Type))
        {
            return false;
        }

        var aliasingParameters = parameters
            .Where(static candidate => CanAliasCalleeParameterMemory(candidate.Type))
            .Select(static candidate => candidate.Name)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (aliasingParameters.Length <= 1)
        {
            return false;
        }

        var pairwiseDisjoint = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in disjointGroups)
        {
            if (group.HasSubregions)
            {
                continue;
            }

            var names = group.ParameterNames
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            for (var leftIndex = 0; leftIndex < names.Length; leftIndex++)
            {
                for (var rightIndex = leftIndex + 1; rightIndex < names.Length; rightIndex++)
                {
                    pairwiseDisjoint.Add(BuildParameterPairKey(names[leftIndex], names[rightIndex]));
                }
            }
        }

        return aliasingParameters
            .Where(candidate => !string.Equals(candidate, parameter.Name, StringComparison.Ordinal))
            .All(candidate => pairwiseDisjoint.Contains(BuildParameterPairKey(parameter.Name, candidate)));
    }

    private static string BuildParameterPairKey(string left, string right)
    {
        return string.CompareOrdinal(left, right) <= 0
            ? $"{left}|{right}"
            : $"{right}|{left}";
    }

    private static bool CanAliasCalleeParameterMemory(StarkTypeSymbol parameterType)
    {
        return parameterType.BorrowKind != StarkBorrowKind.None
            || parameterType.InitializationKind != StarkInitializationKind.None
            || parameterType.Kind is StarkTypeKind.RawPointer or StarkTypeKind.Slice or StarkTypeKind.Ascii or StarkTypeKind.Unicode;
    }

    private static ArgumentEffects DeriveFallbackArgumentEffects(
        TypedParameterSymbol parameter,
        StarkTypeSymbol calleeReturnType,
        bool hasBody)
    {
        var isAliasing = CanAliasCalleeParameterMemory(parameter.Type);
        var guaranteedReadOnly = parameter.IsConst || DeriveGuaranteedReadOnly(parameter.Type);
        var guaranteedWriteOnly = parameter.Type.InitializationKind != StarkInitializationKind.None;
        var reads = isAliasing && !guaranteedWriteOnly;
        var writes = guaranteedWriteOnly
            || (!hasBody && isAliasing && !guaranteedReadOnly);
        var captureKind = ParameterCaptureKind.None;

        if (parameter.Type.BorrowKind == StarkBorrowKind.StoreBorrow)
        {
            captureKind = ParameterCaptureKind.Escape;
        }
        else if (parameter.Type.BorrowKind == StarkBorrowKind.RetBorrow
                 && calleeReturnType.BorrowKind != StarkBorrowKind.None)
        {
            captureKind = calleeReturnType.BorrowKind == StarkBorrowKind.StoreBorrow
                ? ParameterCaptureKind.Escape
                : ParameterCaptureKind.Return;
        }

        if (hasBody)
        {
            reads = false;
            writes = false;
            captureKind = ParameterCaptureKind.None;
        }

        return new ArgumentEffects(reads, writes, captureKind);
    }

    private NamedTypeSymbol? ResolveNamedTypeSymbol(StarkTypeSymbol type)
    {
        return type.NamedType is not null && _typeModel.NamedTypes.TryGetValue(type.NamedType, out var namedType)
            ? namedType
            : null;
    }

    private bool TryIsMutableGlobal(string name)
    {
        return _typeModel.Globals.TryGetValue(name, out var global) && global.IsMutable;
    }

    private FunctionValidationBuilder GetOrCreateSummary(string functionName)
    {
        if (_summaries.TryGetValue(functionName, out var existing))
        {
            return existing;
        }

        var created = new FunctionValidationBuilder(functionName);
        _summaries[functionName] = created;
        return created;
    }

    private void BorrowError(FunctionValidationBuilder summary, string code, string message, ParserRuleContext context)
    {
        summary.BorrowingValid = false;
        _context.Diagnostics.Error(code, message, "semantic-validate", Location(context));
    }

    private void BorrowError(FunctionValidationBuilder summary, string code, string message, IToken token)
    {
        summary.BorrowingValid = false;
        _context.Diagnostics.Error(code, message, "semantic-validate", Location(token));
    }

    private void EffectError(FunctionValidationBuilder summary, string code, string message, ParserRuleContext context)
    {
        summary.EffectsValid = false;
        _context.Diagnostics.Error(code, message, "semantic-validate", Location(context));
    }

    private void EffectError(FunctionValidationBuilder summary, string code, string message, IToken token)
    {
        summary.EffectsValid = false;
        _context.Diagnostics.Error(code, message, "semantic-validate", Location(token));
    }

    private void ValidateLoopContract(
        string functionName,
        string loopBehavior,
        StarkParser.ExpressionContext? condition,
        StarkParser.StatementContext body,
        ParserRuleContext loopBehaviorContext,
        FunctionValidationBuilder summary,
        string? labelName = null)
    {
        switch (loopBehavior)
        {
            case "infinite":
                if (!IsStaticallyUnconditionalLoopCondition(condition))
                {
                    EffectError(
                        summary,
                        "STK4111",
                        $"Loop in function '{functionName}' is marked 'infinite' and must use a statically unconditional condition ('true' for 'while' or an omitted condition for 'for').",
                        loopBehaviorContext);
                }

                if (ContainsForbiddenInfiniteLoopExit(body, labelName))
                {
                    EffectError(
                        summary,
                        "STK4111",
                        $"Loop in function '{functionName}' is marked 'infinite' and may not contain a structural exit from the current loop or function.",
                        loopBehaviorContext);
                }

                break;

            case "willexit":
                if (IsStaticallyUnconditionalLoopCondition(condition)
                    && !ContainsStructuralLoopExit(body, labelName))
                {
                    EffectError(
                        summary,
                        "STK4112",
                        $"Loop in function '{functionName}' is marked 'willexit' with an unconditional condition and must contain at least one structural 'break' or 'return' in its body.",
                        loopBehaviorContext);
                }

                break;
        }
    }

    private static bool IsStaticallyUnconditionalLoopCondition(StarkParser.ExpressionContext? condition)
    {
        return condition is null || string.Equals(condition.GetText(), "true", StringComparison.Ordinal);
    }

    private static bool ContainsStructuralLoopExit(
        StarkParser.StatementContext statement,
        string? targetLabel,
        int nestedLoopDepth = 0,
        int nestedSwitchDepth = 0)
    {
        if (statement.returnStatement() is not null)
        {
            return true;
        }

        if (statement.breakStatement() is { } breakStatement)
        {
            var breakLabel = breakStatement.Identifier()?.GetText();
            if (targetLabel is not null && string.Equals(breakLabel, targetLabel, StringComparison.Ordinal))
            {
                return true;
            }

            if (breakLabel is null
                && nestedLoopDepth == 0
                && nestedSwitchDepth == 0)
            {
                return true;
            }
        }

        if (statement.block() is { } block)
        {
            return block.statement().Any(child => ContainsStructuralLoopExit(child, targetLabel, nestedLoopDepth, nestedSwitchDepth));
        }

        if (statement.ifStatement() is { } ifStatement)
        {
            return ifStatement.statement().Any(child => ContainsStructuralLoopExit(child, targetLabel, nestedLoopDepth, nestedSwitchDepth));
        }

        if (statement.labeledStatement() is { } labeledStatement)
        {
            if (labeledStatement.switchStatement() is { } labeledSwitch)
            {
                return labeledSwitch.switchSection()
                    .SelectMany(static section => section.statement())
                    .Any(child => ContainsStructuralLoopExit(child, targetLabel, nestedLoopDepth, nestedSwitchDepth + 1));
            }

            if (labeledStatement.whileStatement() is { } labeledWhile)
            {
                return ContainsStructuralLoopExit(labeledWhile.statement(), targetLabel, nestedLoopDepth + 1, nestedSwitchDepth);
            }

            if (labeledStatement.forStatement() is { } labeledFor)
            {
                return ContainsStructuralLoopExit(labeledFor.statement(), targetLabel, nestedLoopDepth + 1, nestedSwitchDepth);
            }
        }

        if (statement.switchStatement() is { } switchStatement)
        {
            return switchStatement.switchSection()
                .SelectMany(static section => section.statement())
                .Any(child => ContainsStructuralLoopExit(child, targetLabel, nestedLoopDepth, nestedSwitchDepth + 1));
        }

        if (statement.whileStatement() is { } nestedWhile)
        {
            return ContainsStructuralLoopExit(nestedWhile.statement(), targetLabel, nestedLoopDepth + 1, nestedSwitchDepth);
        }

        if (statement.forStatement() is { } nestedFor)
        {
            return ContainsStructuralLoopExit(nestedFor.statement(), targetLabel, nestedLoopDepth + 1, nestedSwitchDepth);
        }

        return false;
    }

    private static bool ContainsForbiddenInfiniteLoopExit(
        StarkParser.StatementContext statement,
        string? targetLabel,
        int nestedLoopDepth = 0,
        int nestedSwitchDepth = 0)
    {
        if (statement.returnStatement() is not null)
        {
            return true;
        }

        if (statement.breakStatement() is { } breakStatement)
        {
            var breakLabel = breakStatement.Identifier()?.GetText();
            if (targetLabel is not null && string.Equals(breakLabel, targetLabel, StringComparison.Ordinal))
            {
                return true;
            }

            if (breakLabel is null
                && nestedLoopDepth == 0
                && nestedSwitchDepth == 0)
            {
                return true;
            }
        }

        if (statement.block() is { } block)
        {
            return block.statement().Any(child => ContainsForbiddenInfiniteLoopExit(child, targetLabel, nestedLoopDepth, nestedSwitchDepth));
        }

        if (statement.ifStatement() is { } ifStatement)
        {
            return ifStatement.statement().Any(child => ContainsForbiddenInfiniteLoopExit(child, targetLabel, nestedLoopDepth, nestedSwitchDepth));
        }

        if (statement.labeledStatement() is { } labeledStatement)
        {
            if (labeledStatement.switchStatement() is { } labeledSwitch)
            {
                return labeledSwitch.switchSection()
                    .SelectMany(static section => section.statement())
                    .Any(child => ContainsForbiddenInfiniteLoopExit(child, targetLabel, nestedLoopDepth, nestedSwitchDepth + 1));
            }

            if (labeledStatement.whileStatement() is { } labeledWhile)
            {
                return ContainsForbiddenInfiniteLoopExit(labeledWhile.statement(), targetLabel, nestedLoopDepth + 1, nestedSwitchDepth);
            }

            if (labeledStatement.forStatement() is { } labeledFor)
            {
                return ContainsForbiddenInfiniteLoopExit(labeledFor.statement(), targetLabel, nestedLoopDepth + 1, nestedSwitchDepth);
            }
        }

        if (statement.switchStatement() is { } switchStatement)
        {
            return switchStatement.switchSection()
                .SelectMany(static section => section.statement())
                .Any(child => ContainsForbiddenInfiniteLoopExit(child, targetLabel, nestedLoopDepth, nestedSwitchDepth + 1));
        }

        if (statement.whileStatement() is { } nestedWhile)
        {
            return ContainsForbiddenInfiniteLoopExit(nestedWhile.statement(), targetLabel, nestedLoopDepth + 1, nestedSwitchDepth);
        }

        if (statement.forStatement() is { } nestedFor)
        {
            return ContainsForbiddenInfiniteLoopExit(nestedFor.statement(), targetLabel, nestedLoopDepth + 1, nestedSwitchDepth);
        }

        return false;
    }

    private void ApplyBuiltinDeclarationMemoryEffects(
        FunctionDeclarationModel declaration,
        FunctionValidationBuilder summary)
    {
        if (!IsSystemMemoryAllocatorBuiltin(declaration))
        {
            return;
        }

        summary.MarkOtherMemoryRead();
        summary.MarkOtherMemoryWrite();
    }

    private bool IsSystemMemoryAllocatorBuiltin(FunctionDeclarationModel declaration)
    {
        if (!string.Equals(CurrentModuleName, "System.Memory", StringComparison.Ordinal))
        {
            return false;
        }

        var sourceName = declaration.Name;
        const string qualifiedPrefix = "System.Memory.";
        if (sourceName.StartsWith(qualifiedPrefix, StringComparison.Ordinal))
        {
            sourceName = sourceName[qualifiedPrefix.Length..];
        }

        return sourceName is "Allocate" or "Reallocate" or "Free";
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

    private static LocalStorageClass ParseStorageClass(StarkParser.StorageClassContext context)
    {
        return context.GetText() switch
        {
            "stack" => LocalStorageClass.Stack,
            "heap" => LocalStorageClass.Heap,
            "register" => LocalStorageClass.Register,
            "static" => LocalStorageClass.Static,
            "arena" => LocalStorageClass.Arena,
            _ => LocalStorageClass.None
        };
    }

    private enum TypeUsage
    {
        Global,
        Parameter,
        Return,
        Local,
        Field,
        Alias,
        Conversion
    }

    private enum ExpressionObservation
    {
        Read,
        WriteTarget
    }

    private enum VisitState
    {
        Visiting,
        Visited
    }

    private enum SymbolOrigin
    {
        Local,
        Parameter,
        Global
    }

    private enum LocalStorageClass
    {
        None,
        Stack,
        Heap,
        Register,
        Static,
        Arena
    }

    private sealed record VariableSymbol(
        string Name,
        StarkTypeSymbol Type,
        SymbolOrigin Origin,
        LocalStorageClass StorageClass,
        bool IsMutable,
        bool IsConstant,
        GlobalBindingKind? BindingKind = null,
        bool HasConstProvenance = false);

    private sealed record ValidationValue(
        StarkTypeSymbol Type,
        bool IsAssignable = false,
        VariableSymbol? RootSymbol = null,
        TypedFunctionSignature? Function = null,
        string? OverloadSourceName = null,
        IReadOnlyList<TypedFunctionSignature>? OverloadCandidates = null,
        NamedTypeSymbol? NamedType = null,
        bool IsIndirectStorageAccess = false,
        string? NamespaceName = null,
        ValidationValue? Receiver = null,
        EnumConstructorBinding? EnumConstructor = null,
        bool IsAddressMutable = false,
        bool UsesFrozenProjectionSemantics = false,
        bool ReadsIndirectStorageForAddress = false,
        bool HasConstProvenance = false);

    private sealed record EnumConstructorBinding(
        string Name,
        EnumVariantSymbol Variant);

    private readonly record struct ObjectCreationKey(string Text, int Line, int Column);

    private sealed class ValidationScope
    {
        private readonly Dictionary<string, VariableSymbol> _locals = new(StringComparer.Ordinal);

        public ValidationScope(ValidationScope parent)
        {
            Parent = parent;
        }

        private ValidationScope()
        {
        }

        public ValidationScope? Parent { get; }

        public static ValidationScope CreateRoot() => new();

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

            symbol = default!;
            return false;
        }
    }

    private readonly record struct ControlFlowContext(
        int LoopDepth,
        int SwitchDepth,
        IReadOnlyList<ControlFlowLabel> Labels)
    {
        public static ControlFlowContext Root => new(0, 0, []);

        public bool CanBreak => LoopDepth > 0 || SwitchDepth > 0;

        public bool CanContinue => LoopDepth > 0;

        public bool HasLabel(string labelName) =>
            Labels.Any(label => string.Equals(label.Name, labelName, StringComparison.Ordinal));

        public bool CanBreakToLabel(string labelName) =>
            TryFindLabel(labelName, out _);

        public bool CanContinueToLabel(string labelName) =>
            TryFindLabel(labelName, out var label) && label.Kind == ControlFlowLabelKind.Loop;

        public ControlFlowContext EnterLoop(string? labelName = null) =>
            new(LoopDepth + 1, SwitchDepth, AddLabel(labelName, ControlFlowLabelKind.Loop));

        public ControlFlowContext EnterSwitch(string? labelName = null) =>
            new(LoopDepth, SwitchDepth + 1, AddLabel(labelName, ControlFlowLabelKind.Switch));

        private bool TryFindLabel(string labelName, out ControlFlowLabel found)
        {
            for (var index = Labels.Count - 1; index >= 0; index--)
            {
                var label = Labels[index];
                if (string.Equals(label.Name, labelName, StringComparison.Ordinal))
                {
                    found = label;
                    return true;
                }
            }

            found = default;
            return false;
        }

        private IReadOnlyList<ControlFlowLabel> AddLabel(string? labelName, ControlFlowLabelKind kind)
        {
            if (labelName is null)
            {
                return Labels;
            }

            var labels = new ControlFlowLabel[Labels.Count + 1];
            for (var index = 0; index < Labels.Count; index++)
            {
                labels[index] = Labels[index];
            }

            labels[^1] = new ControlFlowLabel(labelName, kind);
            return labels;
        }
    }

    private readonly record struct ControlFlowLabel(string Name, ControlFlowLabelKind Kind);

    private enum ControlFlowLabelKind
    {
        Loop,
        Switch
    }

    private sealed record ArgumentEffects(
        bool Reads,
        bool Writes,
        ParameterCaptureKind CaptureKind);

    private sealed record PendingCallArgument(
        int ArgumentIndex,
        string? CallerParameterName,
        string CalleeParameterName,
        bool AliasesCalleeMemory,
        VariableSymbol? RootSymbol,
        ArgumentEffects FallbackEffects);

    private sealed record PendingCall(
        string CalleeName,
        IReadOnlyList<PendingCallArgument> Arguments,
        IToken Location);

    private sealed record PotentialDropType(
        StarkTypeSymbol Type,
        IToken Location);

    private sealed class ParameterSummaryBuilder
    {
        public ParameterSummaryBuilder(
            TypedParameterSymbol parameter,
            IReadOnlyList<TypedParameterSymbol> parameters,
            IReadOnlyList<ParameterDisjointGroup> disjointGroups,
            IReadOnlyDictionary<string, NamedTypeSymbol> namedTypes,
            IReadOnlyDictionary<string, EnumLayoutSymbol> enumLayouts)
        {
            Name = parameter.Name;
            Type = parameter.Type;
            IsMemoryBacked = IsMemoryBackedType(parameter.Type);
            GuaranteedNonNull = DeriveGuaranteedNonNull(parameter.Type);
            GuaranteedReadOnly = parameter.IsConst || DeriveGuaranteedReadOnly(parameter.Type);
            GuaranteedWriteOnly = parameter.Type.InitializationKind != StarkInitializationKind.None;
            GuaranteedNoAlias = DeriveGuaranteedNoAlias(parameter, parameters, disjointGroups);
            var layoutType = GetParameterDereferenceableLayoutType(parameter.Type);
            var concreteLayout = ConcreteTypeLayoutHelper.TryGetConcreteTypeLayout(layoutType, namedTypes, enumLayouts);
            DereferenceableBytes = GuaranteedNonNull && concreteLayout is not null ? concreteLayout.SizeBytes : null;
            AlignmentBytes = GuaranteedNonNull && concreteLayout is not null ? concreteLayout.AlignmentBytes : null;
            InitializationRanges = TryBuildDestinationInitializationRanges(parameter.Type, concreteLayout, out var initializationRanges)
                ? initializationRanges
                : [];
        }

        public string Name { get; }

        public StarkTypeSymbol Type { get; }

        public bool IsMemoryBacked { get; }

        public bool GuaranteedNonNull { get; }

        public bool GuaranteedReadOnly { get; }

        public bool GuaranteedWriteOnly { get; }

        public bool GuaranteedNoAlias { get; }

        public int? DereferenceableBytes { get; }

        public int? AlignmentBytes { get; }

        public IReadOnlyList<ParameterInitializationRangeSummary> InitializationRanges { get; }

        public bool PointeeDeadOnReturn { get; } = false;

        public bool Reads { get; set; }

        public bool Writes { get; set; }

        public ParameterCaptureKind CaptureKind { get; set; }

        public ArgumentEffects GetEffectiveEffects(bool hasBody)
        {
            var reads = Reads || (!hasBody && IsMemoryBacked && !GuaranteedWriteOnly);
            var writes = Writes
                || (!hasBody && GuaranteedWriteOnly)
                || (!hasBody && IsMemoryBacked && !GuaranteedReadOnly);
            var captureKind = CaptureKind;

            if (captureKind == ParameterCaptureKind.None && Type.BorrowKind == StarkBorrowKind.StoreBorrow)
            {
                captureKind = ParameterCaptureKind.Escape;
            }
            else if (!hasBody && captureKind == ParameterCaptureKind.None)
            {
                captureKind = Type.BorrowKind switch
                {
                    StarkBorrowKind.RetBorrow => ParameterCaptureKind.Return,
                    _ => ParameterCaptureKind.None
                };
            }

            return new ArgumentEffects(reads, writes, captureKind);
        }

        public ParameterMemoryEffectSummary Build(bool hasBody)
        {
            var effects = GetEffectiveEffects(hasBody);
            return new ParameterMemoryEffectSummary(
                Name,
                Type.DisplayName,
                IsMemoryBacked,
                GuaranteedNonNull,
                GuaranteedReadOnly,
                GuaranteedWriteOnly,
                GuaranteedNoAlias,
                DereferenceableBytes,
                AlignmentBytes,
                effects.Reads,
                effects.Writes,
                effects.CaptureKind,
                InitializationRanges,
                PointeeDeadOnReturn);
        }

        public bool Apply(ArgumentEffects effects)
        {
            var changed = false;
            if (effects.Reads && !Reads)
            {
                Reads = true;
                changed = true;
            }

            if (effects.Writes && !Writes)
            {
                Writes = true;
                changed = true;
            }

            if ((int)effects.CaptureKind > (int)CaptureKind)
            {
                CaptureKind = effects.CaptureKind;
                changed = true;
            }

            return changed;
        }

        private static StarkTypeSymbol GetParameterDereferenceableLayoutType(StarkTypeSymbol type)
        {
            var storageType = StarkTypeSymbols.IsPointerBackedBorrowType(type)
                ? StarkTypeSymbols.BorrowReturnValueType(type)
                : type;
            return storageType.BorrowKind != StarkBorrowKind.None
                   || storageType.InitializationKind != StarkInitializationKind.None
                ? StarkTypeSymbols.WithQualifiers(
                    storageType,
                    borrowKind: StarkBorrowKind.None,
                    accessKind: StarkAccessKind.None,
                    initializationKind: StarkInitializationKind.None,
                    isMutableView: false)
                : storageType;
        }

        private static bool TryBuildDestinationInitializationRanges(
            StarkTypeSymbol type,
            ConcreteTypeLayout? concreteLayout,
            out IReadOnlyList<ParameterInitializationRangeSummary> ranges)
        {
            ranges = [];
            if (type.InitializationKind == StarkInitializationKind.None
                || concreteLayout is not { SizeBytes: > 0 })
            {
                return false;
            }

            var storageType = GetParameterDereferenceableLayoutType(type);
            if (type.InitializationKind == StarkInitializationKind.Init
                && storageType.Kind == StarkTypeKind.Slice)
            {
                return false;
            }

            ranges = [new ParameterInitializationRangeSummary(0, concreteLayout.SizeBytes)];
            return true;
        }
    }

    private sealed class FunctionValidationBuilder
    {
        public FunctionValidationBuilder(string name)
        {
            Name = name;
        }

        public string Name { get; }

        public StarkTypeSymbol ReturnType { get; private set; } = StarkTypeSymbols.Error;

        public StarkFunctionKind DeclaredKind { get; private set; } = StarkFunctionKind.Fn;

        public StarkFunctionKind EffectiveKind { get; private set; } = StarkFunctionKind.Fn;

        public bool HasBody { get; private set; }

        public bool DirectLawCompatible { get; private set; }

        public bool DirectFiniteCompatible { get; private set; }

        public bool EffectsValid { get; set; } = true;

        public bool BorrowingValid { get; set; } = true;

        public HashSet<string> CalledFunctions { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, ParameterSummaryBuilder> Parameters { get; } = new(StringComparer.Ordinal);

        public List<PendingCall> PendingCalls { get; } = [];

        public List<PotentialDropType> PotentialDropTypes { get; } = [];

        public List<CallMemoryEffectSummary> ResolvedCalls { get; } = [];

        private HashSet<string> ReportedImplicitDropEffectTypes { get; } = new(StringComparer.Ordinal);

        public bool ReadsOtherMemory { get; private set; }

        public bool WritesOtherMemory { get; private set; }

        public bool HasOpaqueCall { get; private set; }

        public FunctionOptimizationSummary? OptimizationSummary { get; private set; }

        public void Configure(StarkTypeSymbol returnType, bool hasBody, StarkFunctionKind declaredKind)
        {
            ReturnType = returnType;
            DeclaredKind = declaredKind;
            HasBody = hasBody;
            DirectLawCompatible = hasBody;
            DirectFiniteCompatible = hasBody;
            EffectiveKind = hasBody ? StarkFunctionKind.Fn : declaredKind;
        }

        public void SetParameters(
            IReadOnlyList<TypedParameterSymbol> parameters,
            IReadOnlyList<ParameterDisjointGroup> disjointGroups,
            IReadOnlyDictionary<string, NamedTypeSymbol> namedTypes,
            IReadOnlyDictionary<string, EnumLayoutSymbol> enumLayouts)
        {
            foreach (var parameter in parameters)
            {
                Parameters[parameter.Name] = new ParameterSummaryBuilder(parameter, parameters, disjointGroups, namedTypes, enumLayouts);
            }
        }

        public void SetOptimizationSummary(FunctionOptimizationSummary? summary)
        {
            OptimizationSummary = summary;
        }

        public void AddPotentialDropType(StarkTypeSymbol type, IToken location)
        {
            PotentialDropTypes.Add(new PotentialDropType(type, location));
        }

        public bool MarkImplicitDropDiagnosticReported(StarkTypeSymbol type)
        {
            return ReportedImplicitDropEffectTypes.Add(type.DisplayName);
        }

        public void MarkParameterRead(string name)
        {
            if (Parameters.TryGetValue(name, out var parameter))
            {
                parameter.Reads = true;
            }
        }

        public void MarkParameterWrite(string name)
        {
            if (Parameters.TryGetValue(name, out var parameter))
            {
                parameter.Writes = true;
            }
        }

        public void MarkParameterCapture(string name, ParameterCaptureKind captureKind)
        {
            if (Parameters.TryGetValue(name, out var parameter) && (int)captureKind > (int)parameter.CaptureKind)
            {
                parameter.CaptureKind = captureKind;
            }
        }

        public void MarkOtherMemoryRead()
        {
            ReadsOtherMemory = true;
        }

        public void MarkOtherMemoryWrite()
        {
            WritesOtherMemory = true;
        }

        public void MarkOpaqueCall()
        {
            HasOpaqueCall = true;
        }

        public bool ApplyArgumentEffects(string parameterName, ArgumentEffects effects)
        {
            return Parameters.TryGetValue(parameterName, out var parameter) && parameter.Apply(effects);
        }

        public bool ApplyAliasedOtherMemoryEffects(ArgumentEffects effects)
        {
            var changed = false;

            if (effects.Reads && !ReadsOtherMemory)
            {
                ReadsOtherMemory = true;
                changed = true;
            }

            if (effects.Writes && !WritesOtherMemory)
            {
                WritesOtherMemory = true;
                changed = true;
            }

            return changed;
        }

        public bool ApplyFunctionMemoryEffects(FunctionMemoryEffectSummary effects)
        {
            var changed = false;

            if (effects.ReadsOtherMemory && !ReadsOtherMemory)
            {
                ReadsOtherMemory = true;
                changed = true;
            }

            if (effects.WritesOtherMemory && !WritesOtherMemory)
            {
                WritesOtherMemory = true;
                changed = true;
            }

            return changed;
        }

        public bool TryGetParameter(string name, out ParameterSummaryBuilder parameter)
        {
            return Parameters.TryGetValue(name, out parameter!);
        }

        public void BuildResolvedCalls(Func<PendingCall, CallMemoryEffectSummary> projector)
        {
            ResolvedCalls.Clear();
            ResolvedCalls.AddRange(PendingCalls.Select(projector));
        }

        public void DisqualifyLaw()
        {
            DirectLawCompatible = false;
        }

        public void DisqualifyFinite()
        {
            DirectFiniteCompatible = false;
        }

        public void SetEffectiveKind(StarkFunctionKind kind)
        {
            EffectiveKind = kind;
        }

        public FunctionMemoryEffectSummary GetCurrentMemoryEffects()
        {
            var parameterEffects = Parameters.Values
                .Select(parameter => parameter.GetEffectiveEffects(HasBody))
                .ToArray();

            return new FunctionMemoryEffectSummary(
                parameterEffects.Any(static parameter => parameter.Reads),
                parameterEffects.Any(static parameter => parameter.Writes),
                parameterEffects.Any(static parameter => parameter.CaptureKind != ParameterCaptureKind.None),
                ReadsOtherMemory,
                WritesOtherMemory,
                Parameters.Values.Any(static parameter => parameter.InitializationRanges.Count > 0),
                Parameters.Values.Any(static parameter => parameter.PointeeDeadOnReturn));
        }

        public FunctionValidationSummary Build()
        {
            var parameterSummaries = Parameters.Values
                .OrderBy(static parameter => parameter.Name, StringComparer.Ordinal)
                .Select(parameter => parameter.Build(HasBody))
                .ToArray();
            var memoryEffects = new FunctionMemoryEffectSummary(
                parameterSummaries.Any(static parameter => parameter.Reads),
                parameterSummaries.Any(static parameter => parameter.Writes),
                parameterSummaries.Any(static parameter => parameter.CaptureKind != ParameterCaptureKind.None),
                ReadsOtherMemory,
                WritesOtherMemory,
                parameterSummaries.Any(static parameter => parameter.InitializationRanges is { Count: > 0 }),
                parameterSummaries.Any(static parameter => parameter.PointeeDeadOnReturn));

            return new FunctionValidationSummary(
                Name,
                DeclaredKind,
                EffectiveKind,
                EffectsValid,
                BorrowingValid,
                CalledFunctions.OrderBy(static item => item, StringComparer.Ordinal).ToArray(),
                memoryEffects,
                parameterSummaries,
                ResolvedCalls.ToArray(),
                OptimizationSummary,
                HasBody,
                HasOpaqueCall);
        }
    }
}
