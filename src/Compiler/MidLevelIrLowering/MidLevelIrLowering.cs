using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;
using Antlr4.Runtime;
using Stark.Parsing;

namespace Stark.Compiler;

internal sealed partial class MidLevelIrLowerer(
    CompilerPassContext context,
    LoadedModuleSet loadedModules,
    ModuleGraph moduleGraph,
    TypeCheckModel typeModel,
    EnumLayoutModel enumLayoutModel)
{
    private static readonly int[] SupportedConstIntegerWidths = [8, 16, 24, 32, 48, 64, 96, 128, 192, 256, 384, 512, 768, 1024];
    private readonly CompilerLogBag _logs = context.Logs;
    private readonly TypeCheckModel _typeModel = typeModel;
    private readonly EnumLayoutModel _enumLayoutModel = enumLayoutModel;
    private readonly Dictionary<string, FunctionLoweringContext> _functionsByName = CollectFunctionsByQualifiedName(loadedModules, typeModel);
    private readonly Dictionary<string, ConstructorLoweringContext> _constructorsByBodyKey = CollectConstructorsByBodyKey(loadedModules);
    private readonly Dictionary<string, DestructorLoweringContext> _destructorsByTypeName = CollectDestructorsByTypeName(loadedModules);
    private readonly StarkTypeResolver _typeResolver = new(context, "lower-mir", moduleGraph, typeModel.NamedTypes, typeModel.TypeAliases);
    private readonly Dictionary<string, TypedFunctionSignature> _fallbackFunctions = CollectFallbackFunctionSignatures(context, moduleGraph, typeModel.NamedTypes, typeModel.TypeAliases, typeModel.Globals, loadedModules);
    private readonly Dictionary<string, TypedGlobalSymbol> _fallbackGlobals = CollectFallbackGlobals(context, moduleGraph, typeModel.NamedTypes, typeModel.TypeAliases, typeModel.Globals, loadedModules);
    private readonly Dictionary<LiteralKey, StarkTypeSymbol> _literalTypes = typeModel.Literals
            .GroupBy(static literal => new LiteralKey(literal.LiteralText, literal.Location.Line, literal.Location.Column))
            .ToDictionary(static group => group.Key, static group => group.Last().Type);
    private readonly Dictionary<ObjectCreationKey, TypedConstructorShape?> _objectCreationConstructors = typeModel.ObjectCreations
            .GroupBy(static record => new ObjectCreationKey(record.ExpressionText, record.Location.Line, record.Location.Column))
            .ToDictionary(static group => group.Key, static group => group.Last().Constructor);
    private readonly Dictionary<string, ImportedFunctionTemplateSummary> _importedFunctionTemplates = loadedModules.ImportedModules
            .Where(static module => module.PackageImageFacts is { FunctionTemplates.Count: > 0 })
            .SelectMany(static module => module.PackageImageFacts!.FunctionTemplates)
            .ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal);
    private static readonly IReadOnlyDictionary<string, StarkTypeSymbol> EmptyTypeSubstitution =
        new Dictionary<string, StarkTypeSymbol>(StringComparer.Ordinal);
    private Dictionary<string, string> _materializedSpecializationSymbols = new(StringComparer.Ordinal);

    public MidLevelIrModule Lower(HighLevelIrModule hir)
    {
        _materializedSpecializationSymbols = CollectMaterializedSpecializationSymbols(hir);
        var functions = hir.Functions
            .Select(LowerFunction)
            .ToArray();

        return new MidLevelIrModule(hir.ModuleName, functions, hir.AddressTakenFunctions);
    }

    private MidLevelIrFunction LowerFunction(HighLevelIrFunction function)
    {
        var loweringTemplateName = function.BodyTemplateName ?? function.Name;
        _importedFunctionTemplates.TryGetValue(loweringTemplateName, out var importedTemplateSummary);
        var keepOpenGenericTemplateDeclarationBodyless =
            ShouldKeepOpenGenericTemplateDeclarationBodyless(function, importedTemplateSummary);

        if (function.BodyLoweringKind == FunctionBodyLoweringKind.AsmBypass)
        {
            return new MidLevelIrFunction(
                function.Name,
                BuildSignature(function.Signature),
                function.Signature.ReturnType,
                function.Signature.Parameters,
                function.HasBody,
                SupportsDirectCodeGeneration: false,
                EntryBlockId: 0,
                Locals: [],
                Blocks: [],
                BodyLoweringKind: function.BodyLoweringKind);
        }

        if (keepOpenGenericTemplateDeclarationBodyless)
        {
            return new MidLevelIrFunction(
                function.Name,
                BuildSignature(function.Signature),
                function.Signature.ReturnType,
                function.Signature.Parameters,
                function.HasBody,
                SupportsDirectCodeGeneration: false,
                EntryBlockId: 0,
                Locals: [],
                Blocks: [],
                BodyLoweringKind: function.BodyLoweringKind);
        }

        if (!_functionsByName.TryGetValue(loweringTemplateName, out var loweringContext))
        {
            if (function.HasBody && !keepOpenGenericTemplateDeclarationBodyless)
            {
                _logs.GapWarning(
                    "lowering",
                    "missing-function-body",
                    $"MIR lowering could not find a parsed body for '{function.Name}', so LLVM can only emit a declaration for that function.",
                    featureTag: "missing-function-body",
                    reason: "parsed-body-not-found",
                    stage: "lower-mir",
                    symbolName: function.Name,
                    operation: "LowerFunction",
                    location: SourceLocation.Synthetic(),
                    outcome: CompilerLogOutcome.Bypassed,
                    data: CompilerLogData.Create(
                        ("module", _typeModel.ModuleName),
                        ("function", function.Name),
                        ("bodyLoweringKind", function.BodyLoweringKind.ToString())));
            }

            return new MidLevelIrFunction(
                function.Name,
                BuildSignature(function.Signature),
                function.Signature.ReturnType,
                function.Signature.Parameters,
                function.HasBody,
                SupportsDirectCodeGeneration: false,
                EntryBlockId: 0,
                Locals: [],
                Blocks: [],
                BodyLoweringKind: function.BodyLoweringKind);
        }

        var body = loweringContext.ParsedBody;
        var lambdaExpression = loweringContext.LambdaExpression;
        var functionLocation = loweringContext.Location;

        using var builder = new FunctionMirBuilder(
            function,
            loweringContext.ModuleName,
            _typeModel,
            _enumLayoutModel,
            moduleGraph,
            _typeResolver,
            _functionsByName,
            _constructorsByBodyKey,
            _destructorsByTypeName,
            _logs,
            loweringContext.FilePath,
            functionLocation,
            _fallbackFunctions,
            _fallbackGlobals,
            _literalTypes,
            _objectCreationConstructors,
            importedTemplateSummary,
            _materializedSpecializationSymbols,
            function.GenericTypeSubstitution,
            useImportedTemplateLocalDeclarationFacts: body is null);

        _logs.Info(
            "lowering",
            "symbol-lowering-started",
            $"Lowering MIR for '{function.Name}'.",
            stage: "lower-mir",
            symbolName: function.Name,
            operation: "LowerFunction",
            location: functionLocation,
            data: CompilerLogData.Create(
                ("module", loweringContext.ModuleName),
                ("bodyLoweringKind", function.BodyLoweringKind.ToString())),
            kind: CompilerLogKind.Symbol,
            outcome: CompilerLogOutcome.Continued,
            verbosity: CompilerLogVerbosity.Verbose);

        var loweredTypedTemplateBody = body is null
            && lambdaExpression is null
            && importedTemplateSummary?.TypedBody is { } typedBody
            && builder.TryLowerImportedTypedTemplateBody(typedBody);
        if (!loweredTypedTemplateBody)
        {
            if (body is null && lambdaExpression is null)
            {
                if (function.HasBody && !keepOpenGenericTemplateDeclarationBodyless)
                {
                    _logs.GapWarning(
                        "lowering",
                        "missing-function-body",
                        $"MIR lowering could not find a parsed body for '{function.Name}', so LLVM can only emit a declaration for that function.",
                        featureTag: "missing-function-body",
                        reason: "parsed-body-not-found",
                        stage: "lower-mir",
                        symbolName: function.Name,
                        operation: "LowerFunction",
                        location: functionLocation,
                        outcome: CompilerLogOutcome.Bypassed,
                        data: CompilerLogData.Create(
                            ("module", loweringContext.ModuleName),
                            ("function", function.Name),
                            ("bodyLoweringKind", function.BodyLoweringKind.ToString())));
                }

                return new MidLevelIrFunction(
                    function.Name,
                    BuildSignature(function.Signature),
                    function.Signature.ReturnType,
                    function.Signature.Parameters,
                    function.HasBody,
                    SupportsDirectCodeGeneration: false,
                    EntryBlockId: 0,
                    Locals: [],
                    Blocks: [],
                    BodyLoweringKind: function.BodyLoweringKind);
            }

            if (lambdaExpression is not null)
            {
                builder.LowerLambda(lambdaExpression);
            }
            else
            {
                builder.Lower(body!);
            }
        }

        _logs.Info(
            "lowering",
            "symbol-lowering-completed",
            $"Finished MIR lowering for '{function.Name}'.",
            stage: "lower-mir",
            symbolName: function.Name,
            operation: "LowerFunction",
            location: functionLocation,
            data: CompilerLogData.Create(
                ("module", loweringContext.ModuleName),
                ("bodyLoweringKind", function.BodyLoweringKind.ToString()),
                ("supportsDirectCodeGeneration", builder.SupportsDirectCodeGeneration.ToString())),
            kind: CompilerLogKind.Symbol,
            outcome: builder.SupportsDirectCodeGeneration
                ? CompilerLogOutcome.Continued
                : CompilerLogOutcome.Bypassed,
            verbosity: CompilerLogVerbosity.Verbose);

        return new MidLevelIrFunction(
            function.Name,
            BuildSignature(function.Signature),
            function.Signature.ReturnType,
            function.Signature.Parameters,
            function.HasBody,
            builder.SupportsDirectCodeGeneration,
            builder.EntryBlockId,
            builder.Locals,
            builder.Blocks,
            function.BodyLoweringKind,
            functionLocation);
    }

    private static bool ShouldKeepOpenGenericTemplateDeclarationBodyless(
        HighLevelIrFunction function,
        ImportedFunctionTemplateSummary? importedTemplateSummary)
    {
        return function.GenericTypeSubstitution is null
            && function.Signature.IsGeneric
            && (function.HasBody || importedTemplateSummary?.TypedBody is not null);
    }

    private static Dictionary<string, string> CollectMaterializedSpecializationSymbols(HighLevelIrModule hir)
    {
        var symbols = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var function in hir.Functions)
        {
            if (function.Signature.TemplateName is not { } templateName
                || function.Signature.TypeArguments is not { Count: > 0 } typeArguments)
            {
                continue;
            }

            symbols[BuildMaterializedSpecializationKey(templateName, typeArguments)] = function.Name;
        }

        return symbols;
    }

    private static string BuildMaterializedSpecializationKey(
        string templateName,
        IReadOnlyList<StarkTypeSymbol> typeArguments)
    {
        return $"{templateName}|{FunctionOverloadFacts.BuildTypeArgumentKey(typeArguments)}";
    }

    private static string BuildSignature(TypedFunctionSignature function)
    {
        return $"{function.ReturnType.DisplayName} {function.Name}({string.Join(", ", function.Parameters.Select(static parameter => $"{parameter.Type.DisplayName} {parameter.Name}"))})";
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

    private static bool ShouldTrackObjectCreation(StarkParser.ObjectCreationExpressionContext expression)
    {
        return expression.type_() is null
            || expression.objectInitializer() is not null
            || expression.argumentList() is { } argumentList && argumentList.argument().Length > 0;
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
            switch (current)
            {
                case StarkParser.EnumNamedFieldPatternContext enumNamedFieldPattern:
                    ordinals[enumNamedFieldPattern] = nextOrdinal++;
                    break;
                case StarkParser.GenericEnumAggregatePatternContext genericEnumAggregatePattern:
                    ordinals[genericEnumAggregatePattern] = nextOrdinal++;
                    break;
                case StarkParser.AggregatePatternContext aggregatePattern:
                    ordinals[aggregatePattern] = nextOrdinal++;
                    break;
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

    private static Dictionary<string, FunctionLoweringContext> CollectFunctionsByQualifiedName(
        LoadedModuleSet loadedModules,
        TypeCheckModel typeModel)
    {
        var functions = new Dictionary<string, FunctionLoweringContext>(StringComparer.Ordinal);
        var lambdaContexts = CollectLambdaExpressionsByFunctionName(loadedModules, typeModel);

        foreach (var module in loadedModules.Modules.Values)
        {
            foreach (var declaration in DeclaredFunctionSyntaxCollector.Collect(module.ParseResult, module.SyntaxModel))
            {
                var qualifiedName = module.Reference.IsRoot
                    ? declaration.Name
                    : $"{module.SyntaxModel.ModuleName}.{declaration.Name}";
                functions[qualifiedName] = new FunctionLoweringContext(
                    module.SyntaxModel.ModuleName,
                    module.Reference.FilePath,
                    declaration,
                    new SourceLocation(
                        module.Reference.FilePath,
                        declaration.NameToken.Line,
                        declaration.NameToken.Column + 1),
                    declaration.Body.block(),
                    LambdaExpression: null);
            }

            if (module.Reference.IsRoot
                || module.PackageImageFacts is not { FunctionTemplates.Count: > 0 } packageImageFacts)
            {
                continue;
            }

            foreach (var declaration in module.SyntaxModel.Declarations.Where(static declaration => declaration.Function is not null))
            {
                var qualifiedName = FunctionOverloadFacts.QualifyResolvedName(
                    module,
                    FunctionOverloadFacts.GetResolvedLocalName(module.SyntaxModel, declaration));
                if (functions.ContainsKey(qualifiedName)
                    || !packageImageFacts.FunctionTemplates.TryGetValue(qualifiedName, out var templateSummary)
                    || templateSummary.TypedBody is null)
                {
                    continue;
                }

                functions[qualifiedName] = new FunctionLoweringContext(
                    module.SyntaxModel.ModuleName,
                    module.Reference.FilePath,
                    ParsedDeclaration: null,
                    Location: SourceLocation.Synthetic(module.Reference.FilePath),
                    ParsedBody: null,
                    LambdaExpression: null);
            }
        }

        foreach (var lambda in typeModel.Lambdas)
        {
            if (!lambdaContexts.TryGetValue(lambda.FunctionName, out var lambdaExpression))
            {
                continue;
            }

            functions[lambda.FunctionName] = new FunctionLoweringContext(
                loadedModules.RootModuleName,
                lambda.Location.FilePath,
                ParsedDeclaration: null,
                Location: lambda.Location,
                ParsedBody: null,
                LambdaExpression: lambdaExpression);
        }

        return functions;
    }

    private static Dictionary<string, StarkParser.LambdaExpressionContext> CollectLambdaExpressionsByFunctionName(
        LoadedModuleSet loadedModules,
        TypeCheckModel typeModel)
    {
        var lambdasByLocation = typeModel.Lambdas
            .GroupBy(static lambda => $"{lambda.Location.Line}:{lambda.Location.Column}")
            .ToDictionary(
                static group => group.Key,
                static group => group.ToArray(),
                StringComparer.Ordinal);
        var contexts = new Dictionary<string, StarkParser.LambdaExpressionContext>(StringComparer.Ordinal);

        foreach (var module in loadedModules.Modules.Values)
        {
            Collect(module.ParseResult.Root);
        }

        return contexts;

        void Collect(Antlr4.Runtime.Tree.IParseTree current)
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

    private static Dictionary<string, ConstructorLoweringContext> CollectConstructorsByBodyKey(LoadedModuleSet loadedModules)
    {
        var constructors = new Dictionary<string, ConstructorLoweringContext>(StringComparer.Ordinal);

        foreach (var module in loadedModules.Modules.Values)
        {
            foreach (var declaration in module.ParseResult.Root.topLevelDeclaration())
            {
                if (declaration.structDeclaration() is { } structDeclaration)
                {
                    CollectStructLikeConstructors(
                        constructors,
                        module,
                        structDeclaration.Identifier().GetText(),
                        structDeclaration.structBody().structMember()
                            .Select(static member => member.constructorDeclaration())
                            .Where(static constructor => constructor is not null)!);
                    continue;
                }

                if (declaration.recordDeclaration() is { } recordDeclaration)
                {
                    CollectStructLikeConstructors(
                        constructors,
                        module,
                        recordDeclaration.Identifier().GetText(),
                        recordDeclaration.recordBody().recordMember()
                            .Select(static member => member.constructorDeclaration())
                            .Where(static constructor => constructor is not null)!);
                }
            }
        }

        return constructors;
    }

    private static void CollectStructLikeConstructors(
        Dictionary<string, ConstructorLoweringContext> constructors,
        LoadedModuleDocument module,
        string localTypeName,
        IEnumerable<StarkParser.ConstructorDeclarationContext> constructorDeclarations)
    {
        var qualifiedTypeName = QualifyName(module, localTypeName);
        foreach (var constructor in constructorDeclarations)
        {
            if (!string.Equals(constructor.Identifier().GetText(), localTypeName, StringComparison.Ordinal))
            {
                continue;
            }

            var bodyKey = BuildConstructorBodyKey(qualifiedTypeName, constructor);
            constructors[bodyKey] = new ConstructorLoweringContext(
                bodyKey,
                qualifiedTypeName,
                module.SyntaxModel.ModuleName,
                module.Reference.FilePath,
                constructor,
                constructor.block());
        }
    }

    private static Dictionary<string, DestructorLoweringContext> CollectDestructorsByTypeName(LoadedModuleSet loadedModules)
    {
        var destructors = new Dictionary<string, DestructorLoweringContext>(StringComparer.Ordinal);

        foreach (var module in loadedModules.Modules.Values)
        {
            foreach (var declaration in DeclaredDestructorSyntaxCollector.Collect(module))
            {
                destructors[declaration.QualifiedTypeName] = new DestructorLoweringContext(
                    declaration.QualifiedTypeName,
                    declaration.ModuleName,
                    declaration.IsMutable,
                    declaration.Body);
            }
        }

        return destructors;
    }

    private static Dictionary<string, TypedFunctionSignature> CollectFallbackFunctionSignatures(
        CompilerPassContext context,
        ModuleGraph moduleGraph,
        IReadOnlyDictionary<string, NamedTypeSymbol> namedTypes,
        IReadOnlyDictionary<string, TypeAliasSymbol> typeAliases,
        IReadOnlyDictionary<string, TypedGlobalSymbol> typedGlobals,
        LoadedModuleSet loadedModules)
    {
        var resolver = new StarkTypeResolver(context, "lower-mir", moduleGraph, namedTypes, typeAliases);
        var functions = new Dictionary<string, TypedFunctionSignature>(StringComparer.Ordinal);

        foreach (var module in loadedModules.Modules.Values)
        {
            if (!module.Reference.IsRoot
                && module.PackageImageFacts is { FunctionSignatures.Count: > 0 } packageImageFacts)
            {
                foreach (var declaration in module.SyntaxModel.Declarations.Where(static declaration => declaration.Function is not null))
                {
                    var qualifiedName = FunctionOverloadFacts.QualifyResolvedName(
                        module,
                        FunctionOverloadFacts.GetResolvedLocalName(module.SyntaxModel, declaration));
                    if (packageImageFacts.FunctionSignatures.TryGetValue(qualifiedName, out var signature))
                    {
                        functions[qualifiedName] = signature;
                    }
                }

                continue;
            }

            foreach (var declaration in DeclaredFunctionSyntaxCollector.Collect(module.ParseResult, module.SyntaxModel))
            {
                var genericParameterNames = FunctionGenericParameterFacts.GetEffectiveGenericParameterNames(module, declaration);
                var genericParameters = FunctionGenericParameterFacts.ToGenericParameterSet(genericParameterNames);
                var qualifiedName = QualifyName(module, declaration.Name);
                FunctionOverloadFacts.TryFindFunctionDeclaration(
                    module.SyntaxModel,
                    declaration.DisplaySourceName,
                    FunctionOverloadFacts.BuildOverloadKey(declaration.ParameterList),
                    out var declarationModel);
                var parameters = declaration.ParameterList.parameter()
                    .Select(parameter => new TypedParameterSymbol(
                        parameter.Identifier().GetText(),
                        resolver.ResolveParameterType(parameter.type_(), genericParameters, module.SyntaxModel.ModuleName, out var rawPointerElementCountExpression),
                        parameter.parameterContractPrefix().Any(static prefix => prefix.Start.Type == StarkParser.DISJOINT),
                        parameter.parameterContractPrefix().Any(static prefix => prefix.Start.Type == StarkParser.CONST),
                        rawPointerElementCountExpression))
                    .ToArray();
                functions[qualifiedName] = new TypedFunctionSignature(
                    qualifiedName,
                    resolver.ResolveReturnType(declaration.ReturnType, genericParameters, module.SyntaxModel.ModuleName),
                    parameters,
                    SourceName: FunctionOverloadFacts.QualifySourceName(module, declaration.DisplaySourceName),
                    GenericParameterNames: genericParameterNames.Count == 0 ? null : genericParameterNames.ToArray(),
                    IsStatic: declaration.IsStatic,
                    IsVarargs: declaration.Modifiers.Any(static modifier => string.Equals(modifier.GetText(), "varargs", StringComparison.Ordinal)),
                    DisjointParameterGroups: declarationModel?.Function?.DisjointGroups);
            }
        }

        return functions;
    }

    private static Dictionary<string, TypedGlobalSymbol> CollectFallbackGlobals(
        CompilerPassContext context,
        ModuleGraph moduleGraph,
        IReadOnlyDictionary<string, NamedTypeSymbol> namedTypes,
        IReadOnlyDictionary<string, TypeAliasSymbol> typeAliases,
        IReadOnlyDictionary<string, TypedGlobalSymbol> typedGlobals,
        LoadedModuleSet loadedModules)
    {
        var resolver = new StarkTypeResolver(context, "lower-mir", moduleGraph, namedTypes, typeAliases);
        var globals = new Dictionary<string, TypedGlobalSymbol>(StringComparer.Ordinal);

        foreach (var module in loadedModules.Modules.Values)
        {
            foreach (var declaration in module.ParseResult.Root.topLevelDeclaration())
            {
                if (declaration.globalConstantDeclaration() is { } constantDeclaration)
                {
                    foreach (var declarator in constantDeclaration.constantDeclarators().constantDeclarator())
                    {
                        var qualifiedName = QualifyName(module, declarator.Identifier().GetText());
                        var declaredType = typedGlobals.TryGetValue(qualifiedName, out var typedGlobal)
                            ? typedGlobal.Type
                            : constantDeclaration.INTEGER_TYPE() is { } integerType
                                ? ResolveFallbackConstIntegerType(integerType.Symbol)
                            : constantDeclaration.type_() is { } typeContext
                                ? resolver.ResolveType(typeContext, currentModuleName: module.SyntaxModel.ModuleName)
                                : InferFallbackConstType(declarator);
                        globals[qualifiedName] = new TypedGlobalSymbol(qualifiedName, declaredType, GlobalBindingKind.Const);
                    }

                    continue;
                }

                if (declaration.globalVariableDeclaration() is not { } variableDeclaration)
                {
                    continue;
                }

                var declaredVariableType = resolver.ResolveType(variableDeclaration.type_(), currentModuleName: module.SyntaxModel.ModuleName);
                var bindingKind = variableDeclaration.MUT() is not null
                    ? GlobalBindingKind.Mutable
                    : GlobalBindingKind.Immutable;

                foreach (var declarator in variableDeclaration.variableDeclarators().variableDeclarator())
                {
                    var qualifiedName = QualifyName(module, declarator.Identifier().GetText());
                    globals[qualifiedName] = new TypedGlobalSymbol(qualifiedName, declaredVariableType, bindingKind);
                }
            }
        }

        return globals;
    }

    private static StarkTypeSymbol InferFallbackConstType(StarkParser.ConstantDeclaratorContext declarator)
    {
        if (declarator.variableInitializer().expression() is not { } expression
            || !CompileTimeExpressionEvaluator.TryEvaluate(expression, out var constant))
        {
            return StarkTypeSymbols.Error;
        }

        return constant.Kind switch
        {
            CompileTimeConstantKind.Integer => InferFallbackConstIntegerType(constant.IntegerValue),
            CompileTimeConstantKind.Float => constant.Type,
            CompileTimeConstantKind.Bool => StarkTypeSymbols.Bool,
            CompileTimeConstantKind.Text => constant.Type,
            CompileTimeConstantKind.Null => StarkTypeSymbols.Null,
            _ => StarkTypeSymbols.Error
        };
    }

    private static StarkTypeSymbol InferFallbackConstIntegerType(BigInteger value)
    {
        foreach (var width in SupportedConstIntegerWidths)
        {
            var min = -(BigInteger.One << (width - 1));
            var max = (BigInteger.One << (width - 1)) - BigInteger.One;
            if (value >= min && value <= max)
            {
                return StarkTypeSymbols.Integer(width, value, value);
            }
        }

        return StarkTypeSymbols.Integer(SupportedConstIntegerWidths[^1], value, value);
    }

    private static StarkTypeSymbol ResolveFallbackConstIntegerType(IToken integerTypeToken)
    {
        var text = integerTypeToken.Text;
        var isUnsigned = text[0] == 'u';
        var width = int.Parse(text[1..], CultureInfo.InvariantCulture);
        if (isUnsigned)
        {
            return StarkTypeSymbols.Integer(width, BigInteger.Zero, (BigInteger.One << width) - BigInteger.One, isUnsigned: true);
        }

        return StarkTypeSymbols.Integer(
            width,
            -(BigInteger.One << (width - 1)),
            (BigInteger.One << (width - 1)) - BigInteger.One);
    }

    private static string QualifyName(LoadedModuleDocument module, string localName)
    {
        return module.Reference.IsRoot
            ? localName
            : $"{module.SyntaxModel.ModuleName}.{localName}";
    }

    private static string BuildConstructorBodyKey(string qualifiedTypeName, StarkParser.ConstructorDeclarationContext constructor)
    {
        return $"{qualifiedTypeName}@{constructor.Start.Line}:{constructor.Start.Column + 1}";
    }

    private sealed record FunctionLoweringContext(
        string ModuleName,
        string? FilePath,
        DeclaredFunctionSyntax? ParsedDeclaration,
        SourceLocation Location,
        StarkParser.BlockContext? ParsedBody,
        StarkParser.LambdaExpressionContext? LambdaExpression);
    private sealed record ConstructorLoweringContext(
        string BodyKey,
        string QualifiedTypeName,
        string ModuleName,
        string? FilePath,
        StarkParser.ConstructorDeclarationContext Declaration,
        StarkParser.BlockContext Body);
    private sealed record DestructorLoweringContext(
        string QualifiedTypeName,
        string ModuleName,
        bool IsMutable,
        StarkParser.BlockContext Body);

    private readonly record struct LiteralKey(string Text, int Line, int Column);
    private readonly record struct ObjectCreationKey(string Text, int Line, int Column);

}
