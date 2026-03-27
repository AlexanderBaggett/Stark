using System.Numerics;
using Antlr4.Runtime;
using Antlr4.Runtime.Tree;
using Stark.Parsing;

namespace Stark.Compiler;

internal sealed class TypeChecker
{
    private static readonly int[] SupportedIntegerLiteralWidths = [8, 16, 24, 32, 48, 64, 96, 128, 192, 256, 384, 512, 768, 1024];

    private readonly CompilerPassContext _context;
    private readonly ParseResult _parseResult;
    private readonly SyntaxModel _syntaxModel;
    private readonly ModuleGraph _moduleGraph;
    private readonly LoadedModuleSet _loadedModules;

    private readonly Dictionary<string, NamedTypeSymbol> _namedTypes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<ConstructorShape>> _constructors = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TypedFunctionSignature> _functions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, VariableSymbol> _globals = new(StringComparer.Ordinal);
    private readonly List<LiteralTypingRecord> _literals = [];
    private StarkTypeResolver? _typeResolver;

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
    }

    public TypeCheckModel Check()
    {
        SeedNamedTypes();
        _typeResolver = new StarkTypeResolver(_context, "type-check", _moduleGraph, _namedTypes);
        PopulateNamedTypeFields();
        BuildConstructorShapes();
        BuildFunctionSignatures();
        CheckGlobalDeclarations();
        CheckFunctionBodies();

        return new TypeCheckModel(
            _syntaxModel.ModuleName,
            _namedTypes,
            _functions,
            _globals.ToDictionary(static pair => pair.Key, static pair => pair.Value.Type, StringComparer.Ordinal),
            _literals);
    }

    private void SeedNamedTypes()
    {
        foreach (var module in _loadedModules.Modules.Values)
        {
            foreach (var declaration in module.SyntaxModel.Declarations)
            {
                if (declaration.Kind is not (DeclarationKind.Struct or DeclarationKind.Record or DeclarationKind.Trait or DeclarationKind.Doctrine))
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
                    _namedTypes[typeName] = BuildStructLikeNamedType(
                        typeName,
                        DeclarationKind.Struct,
                        structDeclaration.structBody().structMember()
                            .Select(static member => member.fieldDeclaration())
                            .Where(static field => field is not null)!,
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
                            AddField(fields, orderedFields, new FieldSymbol(fieldName, fieldType));
                        }
                    }

                    foreach (var field in recordDeclaration.recordBody().recordMember()
                                 .Select(static member => member.fieldDeclaration())
                                 .Where(static field => field is not null)!)
                    {
                        AddFields(fields, orderedFields, field, genericParameters, module.SyntaxModel.ModuleName);
                    }

                    _namedTypes[recordName] = new NamedTypeSymbol(
                        recordName,
                        DeclarationKind.Record,
                        fields,
                        orderedFields);
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
        string currentModuleName)
    {
        var fields = new Dictionary<string, FieldSymbol>(StringComparer.Ordinal);
        var orderedFields = new List<FieldSymbol>();

        foreach (var field in fieldDeclarations)
        {
            AddFields(fields, orderedFields, field, genericParameters: null, currentModuleName);
        }

        return new NamedTypeSymbol(name, kind, fields, orderedFields);
    }

    private void AddFields(
        Dictionary<string, FieldSymbol> fields,
        List<FieldSymbol> orderedFields,
        StarkParser.FieldDeclarationContext fieldDeclaration,
        ISet<string>? genericParameters,
        string currentModuleName)
    {
        var fieldType = ResolveType(fieldDeclaration.type_(), genericParameters, currentModuleName);

        foreach (var declarator in fieldDeclaration.variableDeclarators().variableDeclarator())
        {
            var fieldName = declarator.Identifier().GetText();
            AddField(fields, orderedFields, new FieldSymbol(fieldName, fieldType));
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
        foreach (var module in _loadedModules.Modules.Values)
        {
            foreach (var functionSyntax in DeclaredFunctionSyntaxCollector.Collect(module.ParseResult))
            {
                var localName = functionSyntax.Name;
                var declarationModel = module.SyntaxModel.Declarations.FirstOrDefault(
                    candidate => candidate.Kind == DeclarationKind.Function && string.Equals(candidate.Name, localName, StringComparison.Ordinal));

                if (declarationModel is null || !IsDeclarationVisible(module, declarationModel))
                {
                    continue;
                }

                if (functionSyntax.Modifiers.Any(static modifier => string.Equals(modifier.GetText(), "strictfp", StringComparison.Ordinal)))
                {
                    ReportError(
                        "STK3008",
                        $"Function '{localName}' uses 'strictfp', but strict floating-point lowering is not implemented in the current compiler yet.",
                        functionSyntax.DeclarationContext);
                }

                var genericParameters = GetGenericParameterNames(functionSyntax.TypeParameters);
                var returnType = ResolveReturnType(functionSyntax.ReturnType, genericParameters, module.SyntaxModel.ModuleName);
                var parameters = functionSyntax.ParameterList.parameter()
                    .Select(parameter => new TypedParameterSymbol(
                        parameter.Identifier().GetText(),
                        ResolveType(parameter.type_(), genericParameters, module.SyntaxModel.ModuleName)))
                    .ToArray();

                var qualifiedName = QualifyName(module, localName);
                _functions[qualifiedName] = new TypedFunctionSignature(
                    qualifiedName,
                    returnType,
                    parameters);
            }
        }
    }

    private void BuildConstructorShapes()
    {
        foreach (var module in _loadedModules.Modules.Values)
        {
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
            .Select(parameter => new TypedParameterSymbol(
                parameter.Identifier().GetText(),
                ResolveType(parameter.type_(), genericParameters, currentModuleName)))
            .ToArray();
    }

    private void CheckGlobalDeclarations()
    {
        RegisterImportedGlobals();

        foreach (var declaration in _parseResult.Root.topLevelDeclaration())
        {
            if (declaration.globalConstantDeclaration() is { } constantDeclaration)
            {
                var declaredType = ResolveType(constantDeclaration.type_());
                foreach (var declarator in constantDeclaration.constantDeclarators().constantDeclarator())
                {
                    var valueType = EvaluateExpression(declarator.expression(), Scope.CreateRoot(_globals), allowFunctionReference: false).Type;
                    EnsureAssignmentCompatible(
                        declarator.Identifier().GetText(),
                        declaredType,
                        valueType,
                        declarator.expression(),
                        isConstant: true);
                    _globals[declarator.Identifier().GetText()] = new VariableSymbol(declarator.Identifier().GetText(), declaredType, IsMutable: false, IsConstant: true);
                }

                continue;
            }

            if (declaration.globalVariableDeclaration() is { } variableDeclaration)
            {
                var declaredType = ResolveType(variableDeclaration.type_());
                var isMutable = variableDeclaration.MUT() is not null;

                foreach (var declarator in variableDeclaration.variableDeclarators().variableDeclarator())
                {
                    if (declarator.variableInitializer() is null)
                    {
                        ReportError(
                            "STK3001",
                            $"Variable '{declarator.Identifier().GetText()}' requires an initializer.",
                            declarator);
                        _globals[declarator.Identifier().GetText()] = new VariableSymbol(declarator.Identifier().GetText(), declaredType, IsMutable: isMutable, IsConstant: false);
                        continue;
                    }

                    CheckVariableInitializer(declarator.variableInitializer(), declaredType, Scope.CreateRoot(_globals));
                    _globals[declarator.Identifier().GetText()] = new VariableSymbol(declarator.Identifier().GetText(), declaredType, IsMutable: isMutable, IsConstant: false);
                }
            }
        }
    }

    private void RegisterImportedGlobals()
    {
        foreach (var module in _loadedModules.ImportedModules)
        {
            foreach (var declaration in module.ParseResult.Root.topLevelDeclaration())
            {
                if (declaration.globalConstantDeclaration() is { } constantDeclaration)
                {
                    var declaredType = ResolveType(constantDeclaration.type_(), currentModuleName: module.SyntaxModel.ModuleName);
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
                            IsConstant: true);
                    }

                    continue;
                }

                if (declaration.globalVariableDeclaration() is { } variableDeclaration)
                {
                    var declaredType = ResolveType(variableDeclaration.type_(), currentModuleName: module.SyntaxModel.ModuleName);
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
                            IsConstant: false);
                    }
                }
            }
        }
    }

    private void CheckFunctionBodies()
    {
        foreach (var functionSyntax in DeclaredFunctionSyntaxCollector.Collect(_parseResult))
        {
            if (functionSyntax.Body.block() is not { } block)
            {
                continue;
            }

            if (!_functions.TryGetValue(functionSyntax.Name, out var signature))
            {
                continue;
            }

            var scope = Scope.CreateRoot(_globals);
            foreach (var parameter in signature.Parameters)
            {
                scope.Declare(new VariableSymbol(parameter.Name, parameter.Type, IsMutable: false, IsConstant: false));
            }

            CheckBlock(block, scope, signature.ReturnType);
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
            var declaredType = ResolveType(localConstant.type_());
            foreach (var declarator in localConstant.constantDeclarators().constantDeclarator())
            {
                var valueType = EvaluateExpression(declarator.expression(), scope, allowFunctionReference: false).Type;
                EnsureAssignmentCompatible(
                    declarator.Identifier().GetText(),
                    declaredType,
                    valueType,
                    declarator.expression(),
                    isConstant: true);
                scope.Declare(new VariableSymbol(declarator.Identifier().GetText(), declaredType, IsMutable: false, IsConstant: true));
            }

            return;
        }

        if (statement.localVariableDeclaration() is { } localVariable)
        {
            CheckVariableDeclaration(localVariable.type_(), localVariable.variableDeclarators().variableDeclarator(), localVariable.MUT() is not null, scope);
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
                CheckVariableDeclaration(localForVariableDeclaration.type_(), localForVariableDeclaration.variableDeclarators().variableDeclarator(), localForVariableDeclaration.MUT() is not null, loopScope);
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

    private void BindPattern(StarkParser.PatternContext pattern, StarkTypeSymbol switchType, Scope scope)
    {
        if (pattern.literal() is { } literal)
        {
            var literalType = EvaluateLiteral(literal).Type;
            if (!CanAssign(switchType, literalType))
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
            scope.Declare(new VariableSymbol(pattern.Identifier().GetText(), switchType, IsMutable: false, IsConstant: false));
            return;
        }
    }

    private void CheckVariableDeclaration(
        StarkParser.Type_Context typeContext,
        IEnumerable<StarkParser.VariableDeclaratorContext> declarators,
        bool isMutable,
        Scope scope)
    {
        var declaredType = ResolveType(typeContext);

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

    private void CheckObjectInitializer(
        StarkParser.ObjectInitializerContext objectInitializer,
        StarkTypeSymbol targetType,
        Scope scope,
        ISet<string>? preInitializedMembers)
    {
        if (targetType.Kind != StarkTypeKind.Named)
        {
            ReportError("STK3002", $"Object initializers require a named target type, but got '{targetType.DisplayName}'.", objectInitializer);
            return;
        }

        _namedTypes.TryGetValue(targetType.NamedType!, out var namedType);
        var initializedMembers = preInitializedMembers is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : new HashSet<string>(preInitializedMembers, StringComparer.Ordinal);

        foreach (var initializer in objectInitializer.memberInitializer())
        {
            var valueType = EvaluateExpression(initializer.expression(), scope, allowFunctionReference: false).Type;
            var memberName = initializer.Identifier().GetText();

            if (namedType is null)
            {
                continue;
            }

            if (!namedType.Fields.TryGetValue(memberName, out var field))
            {
                ReportError("STK3005", $"Type '{namedType.Name}' does not contain a field named '{memberName}'.", initializer);
                continue;
            }

            if (!initializedMembers.Add(memberName))
            {
                var duplicateMessage = preInitializedMembers?.Contains(memberName) == true
                    ? $"Object initializer member '{memberName}' is already supplied by the constructor for '{namedType.Name}'."
                    : $"Object initializer member '{memberName}' is assigned more than once.";
                ReportError("STK3006", duplicateMessage, initializer);
                continue;
            }

            EnsureObjectInitializerCompatible(memberName, field.Type, valueType, initializer.expression());
        }
    }

    private void CheckArrayInitializer(StarkParser.ArrayInitializerContext arrayInitializer, StarkTypeSymbol targetType, Scope scope)
    {
        var elementType = targetType.ElementType;
        if (targetType.Kind is not (StarkTypeKind.FixedArray or StarkTypeKind.Slice) || elementType is null)
        {
            ReportError("STK3002", $"Array initializers require an array or slice target type, but got '{targetType.DisplayName}'.", arrayInitializer);
            return;
        }

        foreach (var expression in arrayInitializer.expression())
        {
            var valueType = EvaluateExpression(expression, scope, allowFunctionReference: false).Type;
            EnsureArrayElementCompatible(elementType, valueType, expression);
        }

        if (targetType.Kind == StarkTypeKind.FixedArray
            && targetType.FixedLength is int fixedLength
            && fixedLength != arrayInitializer.expression().Length)
        {
            ReportError(
                "STK3006",
                $"Array initializer provides {arrayInitializer.expression().Length} elements, but '{targetType.DisplayName}' expects {fixedLength}.",
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
            ReportError("STK3007", $"The left side of '{assignmentOperator}' must be assignable.", expression.unaryExpression());
            return new ExpressionBinding(StarkTypeSymbols.Error);
        }

        if (assignmentOperator == "=")
        {
            EnsureAssignmentTargetCompatible(left, right.Type, expression.assignmentExpression());
            return new ExpressionBinding(left.Type, left.IsAssignable, left.NamedType, left.Function, left.NamespaceName, left.DiagnosticName);
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

        return new ExpressionBinding(left.Type, left.IsAssignable, left.NamedType, left.Function, left.NamespaceName, left.DiagnosticName);
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
            if (!IsNumeric(operands[index - 1].Type) || !IsNumeric(operands[index].Type))
            {
                ReportError(
                    "STK3002",
                    $"Operator '{operators[index - 1]}' requires numeric operands.",
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

        var operand = EvaluateUnaryExpression(expression.unaryExpression(), scope, allowFunctionReference: false);
        var op = expression.GetChild(0).GetText();

        return op switch
        {
            "!" => EnsureBooleanUnary(operand, expression),
            "~" => EnsureIntegerUnary(operand, expression, op),
            "-%" => EnsureIntegerUnary(operand, expression, op),
            "+" or "-" => EnsureNumericUnary(operand, expression, op),
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
        if (resultType.Kind != StarkTypeKind.Float)
        {
            ReportError("STK3002", "Operator '**' currently requires at least one floating-point operand.", expression);
            return new ExpressionBinding(StarkTypeSymbols.Error);
        }

        return new ExpressionBinding(resultType);
    }

    private ExpressionBinding EvaluatePostfixExpression(StarkParser.PostfixExpressionContext expression, Scope scope, bool allowFunctionReference)
    {
        var requiresCallableTarget = expression.postfixPart().Any(static part => part.argumentList() is not null);
        var binding = EvaluatePrimaryExpression(expression.primaryExpression(), scope, allowFunctionReference || requiresCallableTarget);

        foreach (var postfixPart in expression.postfixPart())
        {
            if (postfixPart.argumentList() is { } argumentList)
            {
                binding = InvokeCall(binding, argumentList, scope);
                continue;
            }

            if (postfixPart.GetChild(0).GetText() == "[")
            {
                if (postfixPart.expressionList() is not { } expressionList)
                {
                    ReportError("STK3002", "Index access requires at least one index expression.", postfixPart);
                    binding = new ExpressionBinding(StarkTypeSymbols.Error, DiagnosticName: "indexed element");
                    continue;
                }

                binding = ApplyIndex(binding, expressionList, scope, postfixPart);
                continue;
            }

            binding = ApplyMemberAccess(binding, postfixPart.Identifier().GetText(), postfixPart);
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
        var createdType = ResolveType(expression.type_());
        ConstructorShape? matchedConstructor = null;

        if (expression.argumentList() is { } argumentList)
        {
            matchedConstructor = CheckObjectCreationArguments(argumentList, createdType, scope);
        }

        if (expression.objectInitializer() is { } objectInitializer)
        {
            CheckObjectInitializer(objectInitializer, createdType, scope, matchedConstructor?.InitializedMembers);
        }

        return new ExpressionBinding(createdType, NamedType: ResolveNamedTypeSymbol(createdType), DiagnosticName: $"new '{createdType.DisplayName}'");
    }

    private ExpressionBinding InvokeCall(ExpressionBinding target, StarkParser.ArgumentListContext arguments, Scope scope)
    {
        if (target.Function is null)
        {
            ReportError("STK3008", $"{DescribeExpressionTarget(target)} is not callable.", arguments);
            return new ExpressionBinding(StarkTypeSymbols.Error);
        }

        var receiverOffset = target.Receiver is null ? 0 : 1;
        var explicitParameterCount = Math.Max(0, target.Function.Parameters.Count - receiverOffset);

        if (explicitParameterCount != arguments.argument().Length)
        {
            ReportError(
                "STK3009",
                $"Function '{target.Function.Name}' expects {explicitParameterCount} arguments but received {arguments.argument().Length}.",
                arguments);
        }

        if (target.Receiver is not null && target.Function.Parameters.Count != 0)
        {
            EnsureCallArgumentCompatible(
                target.Function.Name,
                1,
                target.Function.Parameters[0].Type,
                target.Receiver.Type,
                arguments);
        }

        for (var index = 0; index < Math.Min(explicitParameterCount, arguments.argument().Length); index++)
        {
            var parameter = target.Function.Parameters[index + receiverOffset];
            var argumentType = EvaluateExpression(arguments.argument(index).expression(), scope, allowFunctionReference: false).Type;
            EnsureCallArgumentCompatible(target.Function.Name, index + receiverOffset + 1, parameter.Type, argumentType, arguments.argument(index).expression());
        }

        return new ExpressionBinding(target.Function.ReturnType, NamedType: ResolveNamedTypeSymbol(target.Function.ReturnType), DiagnosticName: $"call to '{target.Function.Name}'");
    }

    private ExpressionBinding ApplyIndex(ExpressionBinding target, StarkParser.ExpressionListContext indexes, Scope scope, ParserRuleContext context)
    {
        var currentType = target.Type;

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
                currentType = currentType.ElementType;
                continue;
            }

            ReportError("STK3010", $"{DescribeExpressionTarget(target)} is not indexable.", context);
            return new ExpressionBinding(StarkTypeSymbols.Error);
        }

        return new ExpressionBinding(
            currentType,
            IsAssignable: target.IsAssignable,
            NamedType: ResolveNamedTypeSymbol(currentType),
            DiagnosticName: target.DiagnosticName is null ? "indexed element" : $"indexed element of {target.DiagnosticName}");
    }

    private ExpressionBinding ApplyMemberAccess(ExpressionBinding target, string memberName, ParserRuleContext context)
    {
        if (target.NamespaceName is not null)
        {
            var qualifiedName = $"{target.NamespaceName}.{memberName}";
            if (_moduleGraph.HasModule(qualifiedName))
            {
                return new ExpressionBinding(StarkTypeSymbols.Error, NamespaceName: qualifiedName, DiagnosticName: $"module '{qualifiedName}'");
            }

            if (_globals.TryGetValue(qualifiedName, out var global))
            {
                return new ExpressionBinding(
                    global.Type,
                    IsAssignable: global.IsMutable,
                    NamedType: ResolveNamedTypeSymbol(global.Type),
                    DiagnosticName: global.IsConstant ? $"constant '{qualifiedName}'" : $"variable '{qualifiedName}'");
            }

            if (_functions.TryGetValue(qualifiedName, out var function))
            {
                return new ExpressionBinding(function.ReturnType, Function: function, DiagnosticName: $"function '{qualifiedName}'");
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

        if (namedType.Fields.TryGetValue(memberName, out var field))
        {
            return new ExpressionBinding(field.Type, IsAssignable: target.IsAssignable, NamedType: ResolveNamedTypeSymbol(field.Type), DiagnosticName: $"member '{memberName}'");
        }

        if (_functions.TryGetValue($"{namedType.Name}.{memberName}", out var method)
            && method.Parameters.Count != 0)
        {
            return new ExpressionBinding(
                method.ReturnType,
                NamedType: ResolveNamedTypeSymbol(method.ReturnType),
                Function: method,
                DiagnosticName: $"method '{method.Name}'",
                Receiver: target);
        }

        ReportError("STK3005", $"Type '{namedType.Name}' does not contain a field named '{memberName}'.", context);
        return new ExpressionBinding(StarkTypeSymbols.Error);
    }

    private ExpressionBinding ResolveValue(string name, IToken token, Scope scope, bool allowFunctionReference)
    {
        if (scope.TryLookup(name, out var local))
        {
            return new ExpressionBinding(
                local.Type,
                IsAssignable: !local.IsConstant,
                NamedType: ResolveNamedTypeSymbol(local.Type),
                DiagnosticName: local.IsConstant ? $"constant '{name}'" : $"variable '{name}'");
        }

        if (_globals.TryGetValue(name, out var global))
        {
            return new ExpressionBinding(
                global.Type,
                IsAssignable: global.IsMutable,
                NamedType: ResolveNamedTypeSymbol(global.Type),
                DiagnosticName: global.IsConstant ? $"constant '{name}'" : $"variable '{name}'");
        }

        if (_functions.TryGetValue(name, out var function))
        {
            if (!allowFunctionReference)
            {
                ReportError("STK3012", $"Function '{name}' must be called before its value can be used.", token);
                return new ExpressionBinding(StarkTypeSymbols.Error);
            }

            return new ExpressionBinding(function.ReturnType, Function: function, DiagnosticName: $"function '{name}'");
        }

        if (_moduleGraph.HasModule(name))
        {
            return new ExpressionBinding(StarkTypeSymbols.Error, NamespaceName: name, DiagnosticName: $"module '{name}'");
        }

        ReportError("STK3003", $"Unknown symbol '{name}'.", token);
        return new ExpressionBinding(StarkTypeSymbols.Error);
    }

    private ExpressionBinding EvaluateLiteral(StarkParser.LiteralContext literal)
    {
        StarkTypeSymbol type;

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
        }
        else if (literal.CharacterLiteral() is { } charLiteral)
        {
            type = InferCharacterLiteralType(charLiteral.GetText());
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
        return new ExpressionBinding(type);
    }

    private StarkTypeSymbol ResolveReturnType(StarkParser.ReturnTypeContext returnType, ISet<string>? genericParameters, string? currentModuleName = null)
    {
        return _typeResolver!.ResolveReturnType(returnType, genericParameters, currentModuleName);
    }

    private StarkTypeSymbol ResolveType(StarkParser.Type_Context type, ISet<string>? genericParameters = null, string? currentModuleName = null)
    {
        return _typeResolver!.ResolveType(type, genericParameters, currentModuleName);
    }

    private StarkTypeSymbol ResolveQualifiedType(string qualifiedName, ISet<string>? genericParameters, IToken token, string? currentModuleName = null)
    {
        return _typeResolver!.ResolveQualifiedType(qualifiedName, genericParameters, token, currentModuleName);
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

        if (target.Kind == StarkTypeKind.Ascii && source.Kind == StarkTypeKind.Unicode)
        {
            return " An explicit conversion is required to convert 'unicode' text to 'ascii'.";
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

        if (target.Kind == StarkTypeKind.Unicode && source.Kind == StarkTypeKind.Ascii)
        {
            return true;
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

        return AccessRank(source) >= AccessRank(target);
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

        if (left.Kind == StarkTypeKind.Unicode && right.Kind == StarkTypeKind.Ascii)
        {
            return left;
        }

        if (left.Kind == StarkTypeKind.Ascii && right.Kind == StarkTypeKind.Unicode)
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

    private static bool IsNumeric(StarkTypeSymbol type)
    {
        return type.Kind is StarkTypeKind.Integer or StarkTypeKind.Float;
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
        return IsAsciiLiteral(text) ? StarkTypeSymbols.Ascii : StarkTypeSymbols.Unicode;
    }

    private static StarkTypeSymbol InferCharacterLiteralType(string text)
    {
        return IsAsciiLiteral(text) ? StarkTypeSymbols.Ascii : StarkTypeSymbols.Unicode;
    }

    private static bool IsAsciiLiteral(string text)
    {
        var content = text.Length >= 2 ? text[1..^1] : text;
        for (var index = 0; index < content.Length; index++)
        {
            var ch = content[index];
            if (ch == '\\' && index + 1 < content.Length)
            {
                if (content[index + 1] == 'u' && index + 5 < content.Length)
                {
                    var hex = content.Substring(index + 2, 4);
                    var value = Convert.ToInt32(hex, 16);
                    if (value > 0x7F)
                    {
                        return false;
                    }

                    index += 5;
                    continue;
                }

                index++;
                continue;
            }

            if (ch > 0x7F)
            {
                return false;
            }
        }

        return true;
    }

    private NamedTypeSymbol? ResolveNamedTypeSymbol(StarkTypeSymbol type)
    {
        return type.NamedType is not null && _namedTypes.TryGetValue(type.NamedType, out var namedType)
            ? namedType
            : null;
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

    private SourceLocation Location(ParserRuleContext context) => Location(context.Start);

    private SourceLocation Location(IToken token) =>
        new(_context.Input.FilePath, token.Line, token.Column + 1);

    private sealed record VariableSymbol(string Name, StarkTypeSymbol Type, bool IsMutable, bool IsConstant);

    private sealed record ExpressionBinding(
        StarkTypeSymbol Type,
        bool IsAssignable = false,
        NamedTypeSymbol? NamedType = null,
        TypedFunctionSignature? Function = null,
        string? NamespaceName = null,
        string? DiagnosticName = null,
        ExpressionBinding? Receiver = null);

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
