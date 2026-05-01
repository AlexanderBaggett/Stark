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
    private ISet<string>? _currentFunctionGenericParameters;
    private string? _currentFunctionName;

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
                    ValidateTypeUsage(declaredType, TypeUsage.Global, constantDeclaration.type_() ?? (ParserRuleContext)declarator, isFfiBoundary: false);
                    ValidateConstGlobal(declarator.Identifier().GetText(), declaredType, declarator.variableInitializer());
                }

                continue;
            }

            if (declaration.globalVariableDeclaration() is { } variableDeclaration)
            {
                ValidateGlobalVariableStorageClass(variableDeclaration);
                var declaredType = ResolveType(variableDeclaration.type_());
                ValidateTypeUsage(declaredType, TypeUsage.Global, variableDeclaration.type_(), isFfiBoundary: false);
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
                    "Local 'arena' storage is reserved for allocator-backed region storage, but arena lowering is not implemented yet. Use 'stack' or 'heap' storage for now.",
                    "semantic-validate",
                    Location(context));
                break;
            case LocalStorageClass.Static:
                _context.Diagnostics.Error(
                    "STK4017",
                    "Function-local 'static' storage is not implemented yet. Use a top-level 'static' global for global lifetime storage, or use 'stack'/'heap' for locals.",
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
        StarkParser.VariableInitializerContext initializer)
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

        if (!CanMaterializeFrozenConstInitializer(initializer, declaredType))
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
        return _typeResolver.ResolveType(type, _currentFunctionGenericParameters, _syntaxModel.ModuleName);
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
        var previousFunctionName = _currentFunctionName;
        _currentFunctionGenericParameters = signature.IsGeneric
            ? signature.GenericParams.ToHashSet(StringComparer.Ordinal)
            : null;
        _currentFunctionName = name;

        try
        {
            var scope = ValidationScope.CreateRoot();
            for (var index = 0; index < signature.Parameters.Count; index++)
            {
                var parameter = signature.Parameters[index];
                scope.Declare(new VariableSymbol(
                    parameter.Name,
                    parameter.Type,
                    SymbolOrigin.Parameter,
                    LocalStorageClass.None,
                    IsMutable: false,
                    IsConstant: false,
                    HasConstProvenance: parameter.IsConst));
            }

            CheckBlock(block, scope, syntaxDeclaration.Function, effects, summary, ControlFlowContext.Root);
        }
        finally
        {
            _currentFunctionGenericParameters = previousGenericParameters;
            _currentFunctionName = previousFunctionName;
        }
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
        ValidateTypeUsage(signature.ReturnType, TypeUsage.Return, functionDeclaration.ReturnType, declaration.Modifiers.IsFfi);

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

            ValidateTypeUsage(parameter.Type, TypeUsage.Parameter, parameterContext.type_(), declaration.Modifiers.IsFfi);

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

        var hasFfi = functionDeclaration.Modifiers.Any(static modifier => string.Equals(modifier.GetText(), "ffi", StringComparison.Ordinal));
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
            CheckBlock(unsafeStatement.block(), scope, function, effects, summary, controlFlow);
            return;
        }

        if (statement.localConstantDeclaration() is { } constantDeclaration)
        {
            var declaredType = ResolveLocalConstantDeclarationType(constantDeclaration);
            ValidateTypeUsage(declaredType, TypeUsage.Local, constantDeclaration.type_() ?? (ParserRuleContext)constantDeclaration, isFfiBoundary: false);

            foreach (var declarator in constantDeclaration.constantDeclarators().constantDeclarator())
            {
                var hasConstProvenance = false;
                if (declarator.variableInitializer() is { } initializer)
                {
                    hasConstProvenance = CheckVariableInitializer(initializer, scope, function, effects, summary, declaredType);
                }

                scope.Declare(new VariableSymbol(
                    declarator.Identifier().GetText(),
                    declaredType,
                    SymbolOrigin.Local,
                    LocalStorageClass.None,
                    IsMutable: false,
                    IsConstant: true,
                    HasConstProvenance: hasConstProvenance));
            }

            return;
        }

        if (statement.localVariableDeclaration() is { } localVariable)
        {
            var storageClass = ParseStorageClass(localVariable.storageClass());
            ValidateLocalVariableStorageClass(storageClass, localVariable.storageClass());
            var declaredType = ResolveType(localVariable.type_());
            ValidateTypeUsage(declaredType, TypeUsage.Local, localVariable.type_(), isFfiBoundary: false);

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

                scope.Declare(new VariableSymbol(
                    declarator.Identifier().GetText(),
                    declaredType,
                    SymbolOrigin.Local,
                    storageClass,
                    IsMutable: localVariable.MUT() is not null,
                    IsConstant: false,
                    HasConstProvenance: localVariable.MUT() is null && hasConstProvenance));
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

        if (statement.switchStatement() is { } switchStatement)
        {
            var switchValue = EvaluateExpression(switchStatement.expression(), scope, function, effects, summary, allowFunctionReference: false, ExpressionObservation.Read);
            var switchControlFlow = controlFlow.EnterSwitch();

            foreach (var section in switchStatement.switchSection())
            {
                var sectionScope = new ValidationScope(scope);
                foreach (var label in section.switchLabel())
                {
                    if (label.pattern() is { } pattern)
                    {
                        BindSwitchPattern(pattern, switchValue.Type, sectionScope);
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

            return;
        }

        if (statement.whileStatement() is { } whileStatement)
        {
            ValidateLoopContract(function.Name, whileStatement.loopBehavior().GetText(), whileStatement.expression(), whileStatement.statement(), whileStatement.loopBehavior(), summary);

            if (whileStatement.loopBehavior().GetText() != "willexit")
            {
                summary.DisqualifyFinite();

                if (effects.WillReturn)
                {
                    EffectError(summary, "STK4103", $"Finite function '{function.Name}' may only use 'willexit' loops.", whileStatement.loopBehavior());
                }
            }

            EvaluateExpression(whileStatement.expression(), scope, function, effects, summary, allowFunctionReference: false, ExpressionObservation.Read);
            CheckStatement(whileStatement.statement(), new ValidationScope(scope), function, effects, summary, controlFlow.EnterLoop());
            return;
        }

        if (statement.forStatement() is { } forStatement)
        {
            ValidateLoopContract(function.Name, forStatement.loopBehavior().GetText(), forStatement.forCondition()?.expression(), forStatement.statement(), forStatement.loopBehavior(), summary);

            if (forStatement.loopBehavior().GetText() != "willexit")
            {
                summary.DisqualifyFinite();

                if (effects.WillReturn)
                {
                    EffectError(summary, "STK4103", $"Finite function '{function.Name}' may only use 'willexit' loops.", forStatement.loopBehavior());
                }
            }

            var loopScope = new ValidationScope(scope);

            if (forStatement.forInitializer()?.localForVariableDeclaration() is { } localForDeclaration)
            {
                var storageClass = ParseStorageClass(localForDeclaration.storageClass());
                ValidateLocalVariableStorageClass(storageClass, localForDeclaration.storageClass());
                var declaredType = ResolveType(localForDeclaration.type_());
                ValidateTypeUsage(declaredType, TypeUsage.Local, localForDeclaration.type_(), isFfiBoundary: false);

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

                    loopScope.Declare(new VariableSymbol(
                        declarator.Identifier().GetText(),
                        declaredType,
                        SymbolOrigin.Local,
                        storageClass,
                        IsMutable: localForDeclaration.MUT() is not null,
                        IsConstant: false,
                        HasConstProvenance: localForDeclaration.MUT() is null && hasConstProvenance));
                }
            }
            else if (forStatement.forInitializer()?.expressionList() is { } initializerExpressions)
            {
                foreach (var expression in initializerExpressions.expression())
                {
                    EvaluateExpression(expression, loopScope, function, effects, summary, allowFunctionReference: false, ExpressionObservation.Read);
                }
            }

            if (forStatement.forCondition() is { } condition)
            {
                EvaluateExpression(condition.expression(), loopScope, function, effects, summary, allowFunctionReference: false, ExpressionObservation.Read);
            }

            if (forStatement.forIterator() is { } iterator)
            {
                foreach (var expression in iterator.expressionList().expression())
                {
                    EvaluateExpression(expression, loopScope, function, effects, summary, allowFunctionReference: false, ExpressionObservation.Read);
                }
            }

            CheckStatement(forStatement.statement(), loopScope, function, effects, summary, controlFlow.EnterLoop());
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
            if (!controlFlow.CanBreak)
            {
                EffectError(summary, "STK4113", "'break' requires an enclosing loop or switch.", breakStatement);
            }

            return;
        }

        if (statement.continueStatement() is { } continueStatement)
        {
            if (!controlFlow.CanContinue)
            {
                EffectError(summary, "STK4114", "'continue' requires an enclosing loop.", continueStatement);
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
            TypeCompatibilityFacts.CanAssign);
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
        return new ValidationValue(FindCommonType(whenTrue.Type, whenFalse.Type));
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
                TypeCompatibilityFacts.CanAssign);
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
            TypeCompatibilityFacts.CanAssign);
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
            var targetType = _typeResolver.ResolveConversionType(conversionType, _currentFunctionGenericParameters, _syntaxModel.ModuleName);
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
        var requiresCallableTarget = expression.postfixPart().Any(static part => part.argumentList() is not null);
        var binding = EvaluatePrimaryExpression(expression.primaryExpression(), scope, function, effects, summary, allowFunctionReference || requiresCallableTarget, observation);

        var postfixParts = expression.postfixPart();
        for (var index = 0; index < postfixParts.Length; index++)
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
        if (!isReserve && !isTryReserve)
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
        result = new ValidationValue(isTryReserve ? StarkTypeSymbols.Bool : StarkTypeSymbols.Void);
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

        if (expression.SIZEOF() is not null || expression.ALIGNOF() is not null)
        {
            _ = ResolveType(expression.type_());
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
            return ResolveValue(genericEnumCaseReference.GetText(), scope, function, effects, summary, allowFunctionReference, observation, genericEnumCaseReference.Start);
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
        if (_typeModel.Lambdas.Count == 0)
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
                scope.Declare(new VariableSymbol(
                    parameter.Name,
                    parameter.Type,
                    SymbolOrigin.Parameter,
                    LocalStorageClass.None,
                    IsMutable: false,
                    IsConstant: false,
                    HasConstProvenance: parameter.IsConst));
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

    private Dictionary<string, StarkParser.LambdaExpressionContext> CollectLambdaExpressionsByFunctionName()
    {
        var lambdasByLocation = _typeModel.Lambdas
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
            if (observation == ExpressionObservation.Read)
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
                    HasConstProvenance: globalType.BindingKind == GlobalBindingKind.Const),
                NamedType: ResolveNamedTypeSymbol(globalType.Type),
                IsAddressMutable: isMutable,
                HasConstProvenance: globalType.BindingKind == GlobalBindingKind.Const);
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

        if (_moduleGraph.CanAccessModule(_syntaxModel.ModuleName, name))
        {
            return new ValidationValue(StarkTypeSymbols.Error, NamespaceName: name);
        }

        if (_moduleGraph.CanAccessModuleNamespace(_syntaxModel.ModuleName, name))
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

    private void BindSwitchPattern(StarkParser.PatternContext pattern, StarkTypeSymbol switchType, ValidationScope scope)
    {
        if (pattern.VAR() is not null && pattern.Identifier() is { } capture)
        {
            if (!IsEnumSwitchType(switchType))
            {
                scope.Declare(new VariableSymbol(capture.GetText(), switchType, SymbolOrigin.Local, LocalStorageClass.None, IsMutable: false, IsConstant: false));
            }

            return;
        }

        if (pattern.enumNamedFieldPattern() is { } enumNamedFieldPattern)
        {
            BindEnumNamedFieldPattern(enumNamedFieldPattern, switchType, scope);
            return;
        }

        if (pattern.genericEnumAggregatePattern() is { } genericEnumAggregatePattern)
        {
            TryBindEnumAggregateSwitchPattern(genericEnumAggregatePattern, switchType, scope);
            return;
        }

        if (pattern.aggregatePattern() is { } aggregatePattern)
        {
            if (TryBindEnumAggregateSwitchPattern(aggregatePattern, switchType, scope))
            {
                return;
            }

            BindAggregateSwitchPattern(aggregatePattern, switchType, scope);
        }
    }

    private void BindAggregateSwitchPattern(StarkParser.AggregatePatternContext aggregatePattern, StarkTypeSymbol switchType, ValidationScope scope)
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
            scope.Declare(new VariableSymbol(capture.GetText(), switchType, SymbolOrigin.Local, LocalStorageClass.None, IsMutable: false, IsConstant: false));
            return;
        }

        var fieldPatterns = suffix.pattern();
        if (fieldPatterns.Length != namedType.OrderedFields.Count)
        {
            return;
        }

        for (var index = 0; index < fieldPatterns.Length; index++)
        {
            BindAggregateFieldPattern(fieldPatterns[index], namedType.OrderedFields[index], scope);
        }
    }

    private bool TryBindEnumAggregateSwitchPattern(StarkParser.AggregatePatternContext aggregatePattern, StarkTypeSymbol switchType, ValidationScope scope)
    {
        return TryBindResolvedEnumAggregateSwitchPattern(
            aggregatePattern.simpleType().GetText(),
            aggregatePattern.aggregatePatternSuffix(),
            switchType,
            scope,
            out var matched)
            && matched;
    }

    private bool TryBindEnumAggregateSwitchPattern(StarkParser.GenericEnumAggregatePatternContext aggregatePattern, StarkTypeSymbol switchType, ValidationScope scope)
    {
        return TryBindResolvedEnumAggregateSwitchPattern(
            aggregatePattern.genericEnumCaseReference().GetText(),
            aggregatePattern.aggregatePatternSuffix(),
            switchType,
            scope,
            out var matched)
            && matched;
    }

    private bool TryBindResolvedEnumAggregateSwitchPattern(
        string caseName,
        StarkParser.AggregatePatternSuffixContext? suffix,
        StarkTypeSymbol switchType,
        ValidationScope scope,
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
            scope.Declare(new VariableSymbol(capture.GetText(), switchType, SymbolOrigin.Local, LocalStorageClass.None, IsMutable: false, IsConstant: false));
            return true;
        }

        var fieldPatterns = suffix.pattern();
        if (fieldPatterns.Length != variant.Fields.Count)
        {
            return true;
        }

        for (var index = 0; index < fieldPatterns.Length; index++)
        {
            BindEnumVariantFieldPattern(fieldPatterns[index], variant.Fields[index], scope);
        }

        return true;
    }

    private void BindEnumNamedFieldPattern(
        StarkParser.EnumNamedFieldPatternContext enumNamedFieldPattern,
        StarkTypeSymbol switchType,
        ValidationScope scope)
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
        foreach (var member in enumNamedFieldPattern.enumNamedFieldPatternPayload().namedPatternMember())
        {
            var memberName = member.Identifier().GetText();
            var field = variant.Fields.FirstOrDefault(candidate => string.Equals(candidate.Name, memberName, StringComparison.Ordinal));
            if (field is null || !seenMembers.Add(memberName))
            {
                continue;
            }

            BindEnumVariantFieldPattern(member.pattern(), field, scope);
        }
    }

    private void BindEnumVariantFieldPattern(StarkParser.PatternContext pattern, EnumVariantFieldSymbol field, ValidationScope scope)
    {
        if (pattern.VAR() is not null
            && pattern.Identifier() is { } capture)
        {
            scope.Declare(new VariableSymbol(capture.GetText(), field.Type, SymbolOrigin.Local, LocalStorageClass.None, IsMutable: false, IsConstant: false));
            return;
        }

        if (pattern.enumNamedFieldPattern() is { } enumNamedFieldPattern)
        {
            BindEnumNamedFieldPattern(enumNamedFieldPattern, field.Type, scope);
            return;
        }

        if (pattern.genericEnumAggregatePattern() is { } genericEnumAggregatePattern)
        {
            TryBindEnumAggregateSwitchPattern(genericEnumAggregatePattern, field.Type, scope);
            return;
        }

        if (pattern.aggregatePattern() is { } aggregatePattern)
        {
            if (TryBindEnumAggregateSwitchPattern(aggregatePattern, field.Type, scope))
            {
                return;
            }

            BindAggregateSwitchPattern(aggregatePattern, field.Type, scope);
        }
    }

    private void BindAggregateFieldPattern(StarkParser.PatternContext pattern, FieldSymbol field, ValidationScope scope)
    {
        if (pattern.VAR() is not null
            && pattern.Identifier() is { } capture
            && SupportsAggregateFieldSubpattern(field.Type))
        {
            scope.Declare(new VariableSymbol(capture.GetText(), field.Type, SymbolOrigin.Local, LocalStorageClass.None, IsMutable: false, IsConstant: false));
            return;
        }

        if (pattern.enumNamedFieldPattern() is { } enumNamedFieldPattern)
        {
            BindEnumNamedFieldPattern(enumNamedFieldPattern, field.Type, scope);
            return;
        }

        if (pattern.genericEnumAggregatePattern() is { } genericEnumAggregatePattern
            && TryBindEnumAggregateSwitchPattern(genericEnumAggregatePattern, field.Type, scope))
        {
            return;
        }

        if (pattern.aggregatePattern() is { } enumAggregatePattern
            && TryBindEnumAggregateSwitchPattern(enumAggregatePattern, field.Type, scope))
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
            scope.Declare(new VariableSymbol(wholeCapture.GetText(), field.Type, SymbolOrigin.Local, LocalStorageClass.None, IsMutable: false, IsConstant: false));
            return;
        }

        var fieldPatterns = suffix.pattern();
        if (fieldPatterns.Length != namedType.OrderedFields.Count)
        {
            return;
        }

        for (var index = 0; index < fieldPatterns.Length; index++)
        {
            BindAggregateFieldPattern(fieldPatterns[index], namedType.OrderedFields[index], scope);
        }
    }

    private StarkTypeSymbol ResolvePatternSimpleType(StarkParser.SimpleTypeContext simpleType)
    {
        return _typeResolver.ResolveSimpleType(simpleType, currentModuleName: _syntaxModel.ModuleName);
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

        if (target.OverloadSourceName is { } overloadSourceName)
        {
            if (!TryGetFunctionOverloads(overloadSourceName, out var overloads))
            {
                return new ValidationValue(StarkTypeSymbols.Error);
            }

            var resolution = FunctionOverloadFacts.Resolve(
                overloads,
                target.Receiver?.Type,
                argumentValues.Select(static argument => argument.Type).ToArray(),
                TypeCompatibilityFacts.CanAssign);
            if (!resolution.Succeeded)
            {
                return new ValidationValue(StarkTypeSymbols.Error);
            }

            target = target with
            {
                Function = resolution.Match,
                OverloadSourceName = null,
                Type = resolution.Match!.ReturnType,
                NamedType = ResolveNamedTypeSymbol(resolution.Match.ReturnType)
            };
        }

        if (target.Function is null)
        {
            if (target.Type.Kind == StarkTypeKind.FunctionPointer)
            {
                ValidateIndirectCallKind(target.Type, currentFunction, summary, arguments);
                return new ValidationValue(
                    target.Type.FunctionPointerReturnType ?? StarkTypeSymbols.Error,
                    NamedType: ResolveNamedTypeSymbol(target.Type.FunctionPointerReturnType ?? StarkTypeSymbols.Error));
            }

            return new ValidationValue(StarkTypeSymbols.Error);
        }

        var receiverOffset = target.Receiver is null ? 0 : 1;
        var explicitParameterCount = Math.Max(0, target.Function.Parameters.Count - receiverOffset);

        if (_effectModel.Functions.TryGetValue(target.Function.Name, out var calleeEffects))
        {
            summary.CalledFunctions.Add(target.Function.Name);

            if (calleeEffects.IsFfi)
            {
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
        }

        ValidatePendingCallArguments(target, argumentValues, receiverOffset, explicitParameterCount, summary, arguments);
        summary.PendingCalls.Add(new PendingCall(
            target.Function.Name,
            BuildPendingCallArguments(target, argumentValues, receiverOffset, explicitParameterCount),
            arguments.Start));

        return BuildCallReturnValue(target, argumentValues, receiverOffset, explicitParameterCount);
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
            HasConstProvenance: HasConstProvenance(target));
    }

    private ValidationValue ApplyMemberAccess(ValidationValue target, string memberName)
    {
        if (target.NamespaceName is not null)
        {
            var qualifiedName = $"{target.NamespaceName}.{memberName}";
            if (_moduleGraph.CanAccessModule(_syntaxModel.ModuleName, qualifiedName))
            {
                return new ValidationValue(StarkTypeSymbols.Error, NamespaceName: qualifiedName);
            }

            if (_moduleGraph.CanAccessModuleNamespace(_syntaxModel.ModuleName, qualifiedName))
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
                HasConstProvenance: HasConstProvenance(target));
        }

        var methodSourceName = $"{namedType.Name}.{memberName}";
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
            && _typeModel.Overloads.TryGetValue($"{_syntaxModel.ModuleName}.{sourceName}", out overloads!))
        {
            return true;
        }

        if (!sourceName.Contains('.', StringComparison.Ordinal))
        {
            var importedCandidates = new List<TypedFunctionSignature>();
            foreach (var candidateName in _moduleGraph.EnumerateAccessibleModuleQualifiedNames(_syntaxModel.ModuleName, sourceName))
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
        return string.Equals(_syntaxModel.ModuleName, "System.Text", StringComparison.Ordinal)
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

    private bool TryResolveGlobalBySourceName(string name, out TypedGlobalSymbol global)
    {
        if (_typeModel.Globals.TryGetValue(name, out global!))
        {
            return true;
        }

        if (!name.Contains('.', StringComparison.Ordinal)
            && _typeModel.Globals.TryGetValue($"{_syntaxModel.ModuleName}.{name}", out global!))
        {
            return true;
        }

        if (!name.Contains('.', StringComparison.Ordinal))
        {
            var importedMatches = _moduleGraph.EnumerateAccessibleModuleQualifiedNames(_syntaxModel.ModuleName, name)
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
            && _typeModel.NamedTypes.TryGetValue($"{_syntaxModel.ModuleName}.{typeName}", out namedType!))
        {
            return true;
        }

        if (!typeName.Contains('.', StringComparison.Ordinal))
        {
            var importedMatches = _moduleGraph.EnumerateAccessibleModuleQualifiedNames(_syntaxModel.ModuleName, typeName)
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

            foreach (var summary in _summaries.Values)
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
            }
        }

        foreach (var summary in _summaries.Values)
        {
            var declaredLaw = FunctionKindFacts.IsLaw(summary.DeclaredKind);

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
                    EffectError(summary, "STK4107", $"Finite function '{summary.Name}' may only call finite functions.", pendingCall.Location);
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

    private void ValidateTypeUsage(StarkTypeSymbol type, TypeUsage usage, ParserRuleContext context, bool isFfiBoundary)
    {
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

        if (ContainsNestedRawPointer(type) && (!isFfiBoundary || usage is not (TypeUsage.Parameter or TypeUsage.Return)))
        {
            _context.Diagnostics.Error(
                "STK4006",
                "Pointers to pointers are only permitted on 'ffi' function boundaries through raw pointer types.",
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

        if (_syntaxDeclarations.TryGetValue(type.NamedType, out var declaration)
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

    private static bool IsVisibleMemoryWrite(ValidationValue target)
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

    private static bool TouchesOtherMemory(ValidationValue value)
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

    private static bool AliasedArgumentTouchesOtherMemory(VariableSymbol symbol)
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
            || symbol.Type.InitializationKind != StarkInitializationKind.None;
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

    private static bool IsExternallyVisibleMemory(VariableSymbol symbol)
    {
        if (symbol.Origin == SymbolOrigin.Global)
        {
            return true;
        }

        return symbol.Type.Kind is StarkTypeKind.RawPointer or StarkTypeKind.Slice
            || symbol.Type.BorrowKind != StarkBorrowKind.None
            || symbol.Type.InitializationKind != StarkInitializationKind.None
            || symbol.StorageClass is LocalStorageClass.Heap or LocalStorageClass.Arena or LocalStorageClass.Static;
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

        return StarkTypeSymbols.Integer(widths[^1], value, value);
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

        return disjointGroups.Any(group =>
        {
            var names = group.ParameterNames.ToHashSet(StringComparer.Ordinal);
            return names.Contains(parameter.Name)
                && aliasingParameters.All(names.Contains);
        });
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
        FunctionValidationBuilder summary)
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

                if (ContainsForbiddenInfiniteLoopExit(body))
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
                    && !ContainsStructuralLoopExit(body))
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
        int nestedLoopDepth = 0,
        int nestedSwitchDepth = 0)
    {
        if (statement.returnStatement() is not null)
        {
            return true;
        }

        if (nestedLoopDepth == 0
            && nestedSwitchDepth == 0
            && statement.breakStatement() is not null)
        {
            return true;
        }

        if (statement.block() is { } block)
        {
            return block.statement().Any(child => ContainsStructuralLoopExit(child, nestedLoopDepth, nestedSwitchDepth));
        }

        if (statement.ifStatement() is { } ifStatement)
        {
            return ifStatement.statement().Any(child => ContainsStructuralLoopExit(child, nestedLoopDepth, nestedSwitchDepth));
        }

        if (statement.switchStatement() is { } switchStatement)
        {
            return switchStatement.switchSection()
                .SelectMany(static section => section.statement())
                .Any(child => ContainsStructuralLoopExit(child, nestedLoopDepth, nestedSwitchDepth + 1));
        }

        if (statement.whileStatement() is { } nestedWhile)
        {
            return ContainsStructuralLoopExit(nestedWhile.statement(), nestedLoopDepth + 1, nestedSwitchDepth);
        }

        if (statement.forStatement() is { } nestedFor)
        {
            return ContainsStructuralLoopExit(nestedFor.statement(), nestedLoopDepth + 1, nestedSwitchDepth);
        }

        return false;
    }

    private static bool ContainsForbiddenInfiniteLoopExit(
        StarkParser.StatementContext statement,
        int nestedLoopDepth = 0,
        int nestedSwitchDepth = 0)
    {
        if (statement.returnStatement() is not null)
        {
            return true;
        }

        if (nestedLoopDepth == 0
            && nestedSwitchDepth == 0
            && statement.breakStatement() is not null)
        {
            return true;
        }

        if (statement.block() is { } block)
        {
            return block.statement().Any(child => ContainsForbiddenInfiniteLoopExit(child, nestedLoopDepth, nestedSwitchDepth));
        }

        if (statement.ifStatement() is { } ifStatement)
        {
            return ifStatement.statement().Any(child => ContainsForbiddenInfiniteLoopExit(child, nestedLoopDepth, nestedSwitchDepth));
        }

        if (statement.switchStatement() is { } switchStatement)
        {
            return switchStatement.switchSection()
                .SelectMany(static section => section.statement())
                .Any(child => ContainsForbiddenInfiniteLoopExit(child, nestedLoopDepth, nestedSwitchDepth + 1));
        }

        if (statement.whileStatement() is { } nestedWhile)
        {
            return ContainsForbiddenInfiniteLoopExit(nestedWhile.statement(), nestedLoopDepth + 1, nestedSwitchDepth);
        }

        if (statement.forStatement() is { } nestedFor)
        {
            return ContainsForbiddenInfiniteLoopExit(nestedFor.statement(), nestedLoopDepth + 1, nestedSwitchDepth);
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
        if (!string.Equals(_syntaxModel.ModuleName, "System.Memory", StringComparison.Ordinal))
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
        Local
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
        NamedTypeSymbol? NamedType = null,
        bool IsIndirectStorageAccess = false,
        string? NamespaceName = null,
        ValidationValue? Receiver = null,
        EnumConstructorBinding? EnumConstructor = null,
        bool IsAddressMutable = false,
        bool UsesFrozenProjectionSemantics = false,
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

    private readonly record struct ControlFlowContext(int LoopDepth, int SwitchDepth)
    {
        public static ControlFlowContext Root => new(0, 0);

        public bool CanBreak => LoopDepth > 0 || SwitchDepth > 0;

        public bool CanContinue => LoopDepth > 0;

        public ControlFlowContext EnterLoop() => new(LoopDepth + 1, SwitchDepth);

        public ControlFlowContext EnterSwitch() => new(LoopDepth, SwitchDepth + 1);
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
            var concreteLayout = ConcreteTypeLayoutHelper.TryGetConcreteTypeLayout(parameter.Type, namedTypes, enumLayouts);
            DereferenceableBytes = GuaranteedNonNull && concreteLayout is not null ? concreteLayout.SizeBytes : null;
            AlignmentBytes = GuaranteedNonNull && concreteLayout is not null ? concreteLayout.AlignmentBytes : null;
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

            if (!hasBody && captureKind == ParameterCaptureKind.None)
            {
                captureKind = Type.BorrowKind switch
                {
                    StarkBorrowKind.StoreBorrow => ParameterCaptureKind.Escape,
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
                effects.CaptureKind);
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

        public List<CallMemoryEffectSummary> ResolvedCalls { get; } = [];

        public bool ReadsOtherMemory { get; private set; }

        public bool WritesOtherMemory { get; private set; }

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
                WritesOtherMemory);
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
                WritesOtherMemory);

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
                OptimizationSummary);
        }
    }
}
