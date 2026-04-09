using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;
using Antlr4.Runtime;
using Stark.Parsing;

namespace Stark.Compiler;

internal sealed class MidLevelIrLowerer(
    CompilerPassContext context,
    LoadedModuleSet loadedModules,
    ModuleGraph moduleGraph,
    TypeCheckModel typeModel,
    EnumLayoutModel enumLayoutModel)
{
    private readonly CompilerLogBag _logs = context.Logs;
    private readonly TypeCheckModel _typeModel = typeModel;
    private readonly EnumLayoutModel _enumLayoutModel = enumLayoutModel;
    private readonly Dictionary<string, FunctionLoweringContext> _functionsByName = CollectFunctionsByQualifiedName(loadedModules);
    private readonly Dictionary<string, DestructorLoweringContext> _destructorsByTypeName = CollectDestructorsByTypeName(loadedModules);
    private readonly StarkTypeResolver _typeResolver = new(context, "lower-mir", moduleGraph, typeModel.NamedTypes, typeModel.TypeAliases);
    private readonly Dictionary<string, TypedFunctionSignature> _fallbackFunctions = CollectFallbackFunctionSignatures(context, moduleGraph, typeModel.NamedTypes, typeModel.TypeAliases, loadedModules);
    private readonly Dictionary<string, TypedGlobalSymbol> _fallbackGlobals = CollectFallbackGlobals(context, moduleGraph, typeModel.NamedTypes, typeModel.TypeAliases, loadedModules);
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

        return new MidLevelIrModule(hir.ModuleName, functions);
    }

    private MidLevelIrFunction LowerFunction(HighLevelIrFunction function)
    {
        var loweringTemplateName = function.BodyTemplateName ?? function.Name;
        _importedFunctionTemplates.TryGetValue(loweringTemplateName, out var importedTemplateSummary);
        var keepImportedGenericTemplateDeclarationBodyless =
            ShouldKeepImportedGenericTemplateDeclarationBodyless(function, importedTemplateSummary);

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

        if (!_functionsByName.TryGetValue(loweringTemplateName, out var loweringContext))
        {
            if (function.HasBody && !keepImportedGenericTemplateDeclarationBodyless)
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

        var body = loweringContext.Declaration.Body.block();
        var functionLocation = new SourceLocation(
            loweringContext.FilePath,
            loweringContext.Declaration.NameToken.Line,
            loweringContext.Declaration.NameToken.Column + 1);

        using var builder = new FunctionMirBuilder(
            function,
            loweringContext.ModuleName,
            _typeModel,
            _enumLayoutModel,
            _typeResolver,
            _functionsByName,
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
            function.GenericTypeSubstitution);

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

        var loweredTypedTemplateBody = importedTemplateSummary?.TypedBody is { } typedBody
            && builder.TryLowerImportedTypedTemplateBody(typedBody);
        if (!loweredTypedTemplateBody)
        {
            if (body is null)
            {
                if (function.HasBody && !keepImportedGenericTemplateDeclarationBodyless)
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

            builder.Lower(body);
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

    private static bool ShouldKeepImportedGenericTemplateDeclarationBodyless(
        HighLevelIrFunction function,
        ImportedFunctionTemplateSummary? importedTemplateSummary)
    {
        return function.GenericTypeSubstitution is null
            && function.Signature.IsGeneric
            && importedTemplateSummary?.TypedBody is not null;
    }

    private static Dictionary<string, string> CollectMaterializedSpecializationSymbols(HighLevelIrModule hir)
    {
        var symbols = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var function in hir.Functions)
        {
            if (!function.HasBody
                || function.Signature.TemplateName is not { } templateName
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

    private static Dictionary<string, FunctionLoweringContext> CollectFunctionsByQualifiedName(LoadedModuleSet loadedModules)
    {
        var functions = new Dictionary<string, FunctionLoweringContext>(StringComparer.Ordinal);

        foreach (var module in loadedModules.Modules.Values)
        {
            foreach (var declaration in DeclaredFunctionSyntaxCollector.Collect(module.ParseResult, module.SyntaxModel))
            {
                var qualifiedName = module.Reference.IsRoot
                    ? declaration.Name
                    : $"{module.SyntaxModel.ModuleName}.{declaration.Name}";
                functions[qualifiedName] = new FunctionLoweringContext(module.SyntaxModel.ModuleName, module.Reference.FilePath, declaration);
            }
        }

        return functions;
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
        LoadedModuleSet loadedModules)
    {
        var resolver = new StarkTypeResolver(context, "lower-mir", moduleGraph, namedTypes, typeAliases);
        var functions = new Dictionary<string, TypedFunctionSignature>(StringComparer.Ordinal);

        foreach (var module in loadedModules.Modules.Values)
        {
            foreach (var declaration in DeclaredFunctionSyntaxCollector.Collect(module.ParseResult, module.SyntaxModel))
            {
                var genericParameters = resolver.GetGenericParameterNames(declaration.TypeParameters);
                var qualifiedName = QualifyName(module, declaration.Name);
                var parameters = declaration.ParameterList.parameter()
                    .Select(parameter => new TypedParameterSymbol(
                        parameter.Identifier().GetText(),
                        resolver.ResolveType(parameter.type_(), genericParameters, module.SyntaxModel.ModuleName)))
                    .ToArray();
                functions[qualifiedName] = new TypedFunctionSignature(
                    qualifiedName,
                    resolver.ResolveReturnType(declaration.ReturnType, genericParameters, module.SyntaxModel.ModuleName),
                    parameters,
                    SourceName: FunctionOverloadFacts.QualifySourceName(module, declaration.DisplaySourceName),
                    GenericParameterNames: genericParameters?.ToArray());
            }
        }

        return functions;
    }

    private static Dictionary<string, TypedGlobalSymbol> CollectFallbackGlobals(
        CompilerPassContext context,
        ModuleGraph moduleGraph,
        IReadOnlyDictionary<string, NamedTypeSymbol> namedTypes,
        IReadOnlyDictionary<string, TypeAliasSymbol> typeAliases,
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
                    var declaredType = resolver.ResolveType(constantDeclaration.type_(), currentModuleName: module.SyntaxModel.ModuleName);
                    foreach (var declarator in constantDeclaration.constantDeclarators().constantDeclarator())
                    {
                        var qualifiedName = QualifyName(module, declarator.Identifier().GetText());
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

    private static string QualifyName(LoadedModuleDocument module, string localName)
    {
        return module.Reference.IsRoot
            ? localName
            : $"{module.SyntaxModel.ModuleName}.{localName}";
    }

    private sealed record FunctionLoweringContext(string ModuleName, string? FilePath, DeclaredFunctionSyntax Declaration);
    private sealed record DestructorLoweringContext(
        string QualifiedTypeName,
        string ModuleName,
        bool IsMutable,
        StarkParser.BlockContext Body);

    private readonly record struct LiteralKey(string Text, int Line, int Column);
    private readonly record struct ObjectCreationKey(string Text, int Line, int Column);

    private sealed class FunctionMirBuilder : IDisposable
    {
        private enum AggregatePatternFieldKind
        {
            Discard,
            Literal,
            Capture,
            Nested
        }

        private sealed record LowerableAggregateFieldPattern(
            string FieldName,
            string StorageFieldName,
            int FieldIndex,
            StarkTypeSymbol FieldType,
            AggregatePatternFieldKind Kind,
            string Text,
            StarkParser.LiteralContext? Literal,
            string? CaptureName,
            LowerableAggregatePattern? NestedPattern,
            ImportedTemplateTypedBodyExpressionSummary? ImportedLiteralExpression);

        private sealed record LowerableAggregatePattern(
            string TypeName,
            string? EnumVariantName,
            IReadOnlyList<LowerableAggregateFieldPattern> FieldPatterns,
            string? WholeCaptureName);

        private sealed record PendingSwitchBinding(string Name, MidLevelIrOperand Source);

        private sealed record LowerableSwitchLabel(
            string LabelText,
            StarkParser.LiteralContext? Literal,
            StarkParser.ExpressionContext? GuardExpression,
            bool IsDefault,
            bool IsMatchAll,
            string? CaptureName,
            LowerableAggregatePattern? AggregatePattern,
            ImportedTemplateTypedBodyExpressionSummary? ImportedLiteralExpression = null,
            ImportedTemplateTypedBodyExpressionSummary? ImportedGuardExpression = null);

        private sealed record LowerableSwitchSection(
            StarkParser.SwitchSectionContext Section,
            IReadOnlyList<LowerableSwitchLabel> Labels);

        private sealed record PartitionedTextSwitchLabel(
            LowerableSwitchLabel Label,
            int TargetBlockId,
            int[] Units,
            int Order);

        private enum PlacePathKind
        {
            Field,
            ConstantArrayIndex,
            DynamicArrayIndex,
            RawPointerIndex,
            SliceIndex
        }

        private sealed record PlacePathSegment(
            PlacePathKind Kind,
            string? FieldName,
            int? ConstantIndex,
            MidLevelIrOperand? IndexOperand,
            StarkTypeSymbol ParentType,
            StarkTypeSymbol SegmentType);

        private sealed record PlaceTarget(
            string? RootName,
            MidLevelIrOperand? RootAddress,
            StarkTypeSymbol RootType,
            StarkTypeSymbol Type,
            IReadOnlyList<PlacePathSegment> Path,
            bool UsesAddressModel,
            bool IsAddressMutable);

        private sealed record LoweredAssignment(
            string Text,
            string? TargetName,
            StarkTypeSymbol TargetType,
            MidLevelIrRValue? DirectValue,
            MidLevelIrOperand ResultValue,
            MidLevelIrOperand? Address,
            bool ReplacesWholeValue);

        private sealed class ScopeFrame
        {
            public List<(string Name, StarkTypeSymbol Type)> Locals { get; } = [];
        }

        private sealed record CompileTimeBinding(CompileTimeConstant Value, bool IsMutable);

        private sealed record CompileTimeScopeEntry(
            string Name,
            bool HadPreviousBinding,
            CompileTimeBinding? PreviousBinding);

        private sealed class CompileTimeEvaluationState
        {
            private readonly Dictionary<string, CompileTimeBinding> _bindings = new(StringComparer.Ordinal);
            private readonly Stack<List<CompileTimeScopeEntry>> _scopes = new();

            public void PushScope()
            {
                _scopes.Push([]);
            }

            public void PopScope()
            {
                if (_scopes.Count == 0)
                {
                    return;
                }

                foreach (var entry in _scopes.Pop().AsEnumerable().Reverse())
                {
                    if (entry.HadPreviousBinding && entry.PreviousBinding is not null)
                    {
                        _bindings[entry.Name] = entry.PreviousBinding;
                    }
                    else
                    {
                        _bindings.Remove(entry.Name);
                    }
                }
            }

            public void Declare(string name, CompileTimeConstant value, bool isMutable)
            {
                if (_scopes.Count == 0)
                {
                    PushScope();
                }

                var hadPreviousBinding = _bindings.TryGetValue(name, out var previousBinding);
                _scopes.Peek().Add(new CompileTimeScopeEntry(name, hadPreviousBinding, previousBinding));
                _bindings[name] = new CompileTimeBinding(value, isMutable);
            }

            public bool TryResolve(string name, out CompileTimeConstant value)
            {
                if (_bindings.TryGetValue(name, out var binding))
                {
                    value = binding.Value;
                    return true;
                }

                value = default;
                return false;
            }

            public bool TryAssign(string name, CompileTimeConstant value)
            {
                if (!_bindings.TryGetValue(name, out var binding) || !binding.IsMutable)
                {
                    return false;
                }

                _bindings[name] = binding with { Value = value };
                return true;
            }
        }

        private sealed class DestructorContext : IDisposable
        {
            private readonly FunctionMirBuilder _builder;
            private readonly string? _previousModuleName;
            private readonly string _aliasName;
            private readonly string? _previousAlias;
            private readonly bool _hadAlias;

            public DestructorContext(
                FunctionMirBuilder builder,
                string? previousModuleName,
                string aliasName,
                string? previousAlias,
                bool hadAlias)
            {
                _builder = builder;
                _previousModuleName = previousModuleName;
                _aliasName = aliasName;
                _previousAlias = previousAlias;
                _hadAlias = hadAlias;
            }

            public void Dispose()
            {
                _builder._moduleNameOverride = _previousModuleName;
                if (_hadAlias)
                {
                    _builder._nameAliases[_aliasName] = _previousAlias!;
                }
                else
                {
                    _builder._nameAliases.Remove(_aliasName);
                }
            }
        }

        private readonly HighLevelIrFunction _function;
        private readonly string _currentModuleName;
        private readonly TypeCheckModel _typeModel;
        private readonly EnumLayoutModel _enumLayoutModel;
        private readonly StarkTypeResolver _typeResolver;
        private readonly IReadOnlyDictionary<string, FunctionLoweringContext> _functionsByName;
        private readonly IReadOnlyDictionary<string, DestructorLoweringContext> _destructorsByTypeName;
        private readonly CompilerLogBag _logs;
        private readonly string? _moduleFilePath;
        private readonly SourceLocation _functionLocation;
        private readonly IReadOnlyDictionary<string, TypedFunctionSignature> _fallbackFunctions;
        private readonly IReadOnlyDictionary<string, TypedGlobalSymbol> _fallbackGlobals;
        private readonly IReadOnlyDictionary<LiteralKey, StarkTypeSymbol> _literalTypes;
        private readonly IReadOnlyDictionary<ObjectCreationKey, TypedConstructorShape?> _objectCreationConstructors;
        private readonly ImportedFunctionTemplateSummary? _importedTemplateSummary;
        private readonly IReadOnlyDictionary<int, ImportedTemplateEnumConstructorSummary> _importedTemplateEnumConstructors;
        private readonly IReadOnlyDictionary<int, ImportedTemplateEnumCallSummary> _importedTemplateEnumCalls;
        private readonly IReadOnlyDictionary<int, ImportedTemplateEnumValueSummary> _importedTemplateEnumValues;
        private readonly IReadOnlyDictionary<int, ImportedTemplateEnumPatternSummary> _importedTemplateEnumPatterns;
        private readonly IReadOnlyDictionary<int, ImportedTemplateAggregatePatternSummary> _importedTemplateAggregatePatterns;
        private readonly IReadOnlyDictionary<string, StarkTypeSymbol> _importedTemplateLocalDeclarations;
        private readonly IReadOnlyDictionary<int, StarkTypeSymbol> _importedTemplateConversions;
        private readonly IReadOnlyDictionary<int, TypedFunctionSignature> _importedTemplateDirectCalls;
        private readonly IReadOnlyDictionary<int, ImportedTemplateFieldAccessSummary> _importedTemplateFieldAccesses;
        private readonly IReadOnlyDictionary<int, TypedFunctionSignature> _importedTemplateMemberCalls;
        private readonly IReadOnlyDictionary<string, string> _materializedSpecializationSymbols;
        private readonly ISet<string>? _genericParameterNames;
        private readonly IReadOnlyDictionary<string, StarkTypeSymbol>? _genericTypeSubstitution;
        private readonly HashSet<string> _unsupportedLogKeys = new(StringComparer.Ordinal);
        private readonly IDisposable _logScope;
        private readonly List<MidLevelIrLocal> _locals = [];
        private readonly Dictionary<string, MidLevelIrLocal> _localsByName = new(StringComparer.Ordinal);
        private readonly Dictionary<string, TypedParameterSymbol> _parametersByName;
        private readonly Dictionary<string, bool> _runtimeDropStates = new(StringComparer.Ordinal);
        private readonly List<string> _parameterDropOrder = [];
        private readonly Dictionary<string, string> _nameAliases = new(StringComparer.Ordinal);
        private readonly List<BasicBlockBuilder> _blocks = [];
        private readonly Stack<LoopTargets> _loops = [];
        private readonly Stack<BreakTargets> _breakTargets = [];
        private readonly Stack<ScopeFrame> _scopes = [];
        private string? _moduleNameOverride;
        private SourceLocation? _currentStatementLocation;
        private IReadOnlyDictionary<StarkParser.ObjectCreationExpressionContext, int>? _importedObjectCreationOrdinals;
        private IReadOnlyDictionary<StarkParser.EnumConstructorExpressionContext, int>? _importedEnumConstructorOrdinals;
        private IReadOnlyDictionary<StarkParser.ArgumentListContext, int>? _importedEnumCallOrdinals;
        private IReadOnlyDictionary<StarkParser.PrimaryExpressionContext, int>? _importedEnumValueOrdinals;
        private IReadOnlyDictionary<ParserRuleContext, int>? _importedEnumPatternOrdinals;
        private IReadOnlyDictionary<StarkParser.UnaryExpressionContext, int>? _importedConversionOrdinals;
        private IReadOnlyDictionary<StarkParser.ArgumentListContext, int>? _importedDirectCallOrdinals;
        private IReadOnlyDictionary<StarkParser.PostfixPartContext, int>? _importedFieldAccessOrdinals;
        private IReadOnlyDictionary<StarkParser.ArgumentListContext, int>? _importedMemberCallOrdinals;
        private int _nextBlockId;
        private int _nextTempId;

        public FunctionMirBuilder(
            HighLevelIrFunction function,
            string currentModuleName,
            TypeCheckModel typeModel,
            EnumLayoutModel enumLayoutModel,
            StarkTypeResolver typeResolver,
            IReadOnlyDictionary<string, FunctionLoweringContext> functionsByName,
            IReadOnlyDictionary<string, DestructorLoweringContext> destructorsByTypeName,
            CompilerLogBag logs,
            string? moduleFilePath,
            SourceLocation functionLocation,
            IReadOnlyDictionary<string, TypedFunctionSignature> fallbackFunctions,
            IReadOnlyDictionary<string, TypedGlobalSymbol> fallbackGlobals,
            IReadOnlyDictionary<LiteralKey, StarkTypeSymbol> literalTypes,
            IReadOnlyDictionary<ObjectCreationKey, TypedConstructorShape?> objectCreationConstructors,
            ImportedFunctionTemplateSummary? importedTemplateSummary,
            IReadOnlyDictionary<string, string> materializedSpecializationSymbols,
            IReadOnlyDictionary<string, StarkTypeSymbol>? genericTypeSubstitution)
        {
            _function = function;
            _currentModuleName = currentModuleName;
            _typeModel = typeModel;
            _enumLayoutModel = enumLayoutModel;
            _typeResolver = typeResolver;
            _functionsByName = functionsByName;
            _destructorsByTypeName = destructorsByTypeName;
            _logs = logs;
            _moduleFilePath = moduleFilePath;
            _functionLocation = functionLocation;
            _fallbackFunctions = fallbackFunctions;
            _fallbackGlobals = fallbackGlobals;
            _literalTypes = literalTypes;
            _objectCreationConstructors = objectCreationConstructors;
            _importedTemplateSummary = importedTemplateSummary;
            _importedTemplateEnumConstructors = importedTemplateSummary?.EnumConstructors.ToDictionary(
                static enumConstructor => enumConstructor.Ordinal,
                static enumConstructor => enumConstructor)
                ?? new Dictionary<int, ImportedTemplateEnumConstructorSummary>();
            _importedTemplateEnumCalls = importedTemplateSummary?.EnumCalls.ToDictionary(
                static enumCall => enumCall.Ordinal,
                static enumCall => enumCall)
                ?? new Dictionary<int, ImportedTemplateEnumCallSummary>();
            _importedTemplateEnumValues = importedTemplateSummary?.EnumValues.ToDictionary(
                static enumValue => enumValue.Ordinal,
                static enumValue => enumValue)
                ?? new Dictionary<int, ImportedTemplateEnumValueSummary>();
            _importedTemplateEnumPatterns = importedTemplateSummary?.EnumPatterns.ToDictionary(
                static enumPattern => enumPattern.Ordinal,
                static enumPattern => enumPattern)
                ?? new Dictionary<int, ImportedTemplateEnumPatternSummary>();
            _importedTemplateAggregatePatterns = importedTemplateSummary?.AggregatePatterns.ToDictionary(
                static aggregatePattern => aggregatePattern.Ordinal,
                static aggregatePattern => aggregatePattern)
                ?? new Dictionary<int, ImportedTemplateAggregatePatternSummary>();
            _importedTemplateLocalDeclarations = importedTemplateSummary?.LocalDeclarations.ToDictionary(
                static local => TemplateLocalDeclarationFacts.BuildLookupKey(local.Kind, local.Line, local.Column),
                static local => local.Type,
                StringComparer.Ordinal)
                ?? new Dictionary<string, StarkTypeSymbol>(StringComparer.Ordinal);
            _importedTemplateConversions = importedTemplateSummary?.Conversions.ToDictionary(
                static conversion => conversion.Ordinal,
                static conversion => conversion.TargetType)
                ?? new Dictionary<int, StarkTypeSymbol>();
            _importedTemplateDirectCalls = importedTemplateSummary?.DirectCalls.ToDictionary(
                static call => call.Ordinal,
                static call => call.Signature)
                ?? new Dictionary<int, TypedFunctionSignature>();
            _importedTemplateFieldAccesses = importedTemplateSummary?.FieldAccesses.ToDictionary(
                static access => access.Ordinal,
                static access => access)
                ?? new Dictionary<int, ImportedTemplateFieldAccessSummary>();
            _importedTemplateMemberCalls = importedTemplateSummary?.MemberCalls.ToDictionary(
                static call => call.Ordinal,
                static call => call.Signature)
                ?? new Dictionary<int, TypedFunctionSignature>();
            _materializedSpecializationSymbols = materializedSpecializationSymbols;
            _genericParameterNames = function.Signature.IsGeneric
                ? function.Signature.GenericParams.ToHashSet(StringComparer.Ordinal)
                : genericTypeSubstitution is { Count: > 0 }
                    ? genericTypeSubstitution.Keys.ToHashSet(StringComparer.Ordinal)
                    : null;
            _genericTypeSubstitution = genericTypeSubstitution is { Count: > 0 }
                ? new Dictionary<string, StarkTypeSymbol>(genericTypeSubstitution, StringComparer.Ordinal)
                : null;
            _parametersByName = function.Signature.Parameters.ToDictionary(static parameter => parameter.Name, StringComparer.Ordinal);
            foreach (var parameter in function.Signature.Parameters)
            {
                if (!RequiresRuntimeDrop(parameter.Type))
                {
                    continue;
                }

                _runtimeDropStates[parameter.Name] = true;
                _parameterDropOrder.Add(parameter.Name);
            }

            _logScope = _logs.PushContext(
                stage: "lower-mir",
                symbolName: function.Name,
                location: functionLocation);
            CurrentBlock = CreateBlock("entry");
        }

        public bool SupportsDirectCodeGeneration { get; private set; } = true;

        public int EntryBlockId => 0;

        public IReadOnlyList<MidLevelIrLocal> Locals => _locals;

        public IReadOnlyList<MidLevelIrBasicBlock> Blocks => _blocks
            .Select(static block => block.Build())
            .ToArray();

        private BasicBlockBuilder CurrentBlock { get; set; }
        private string CurrentModuleName => _moduleNameOverride ?? _currentModuleName;

        public void Lower(StarkParser.BlockContext body)
        {
            _importedObjectCreationOrdinals = _importedTemplateSummary is { ObjectCreations.Count: > 0 }
                ? CollectTrackedObjectCreationOrdinals(body)
                : null;
            _importedEnumConstructorOrdinals = _importedTemplateSummary is { EnumConstructors.Count: > 0 }
                ? CollectTemplateEnumConstructorOrdinals(body)
                : null;
            _importedEnumCallOrdinals = _importedTemplateSummary is { EnumCalls.Count: > 0 }
                ? CollectTemplateDirectCallOrdinals(body)
                : null;
            _importedEnumValueOrdinals = _importedTemplateSummary is { EnumValues.Count: > 0 }
                ? CollectTemplateEnumValueOrdinals(body)
                : null;
            _importedEnumPatternOrdinals = _importedTemplateSummary is { } importedTemplateSummary
                && (importedTemplateSummary.EnumPatterns.Count > 0 || importedTemplateSummary.AggregatePatterns.Count > 0)
                ? CollectTemplateEnumPatternOrdinals(body)
                : null;
            _importedConversionOrdinals = _importedTemplateSummary is { Conversions.Count: > 0 }
                ? CollectTemplateConversionOrdinals(body)
                : null;
            _importedDirectCallOrdinals = _importedTemplateSummary is { DirectCalls.Count: > 0 }
                ? CollectTemplateDirectCallOrdinals(body)
                : null;
            _importedFieldAccessOrdinals = _importedTemplateSummary is { FieldAccesses.Count: > 0 }
                ? CollectTemplateFieldAccessOrdinals(body)
                : null;
            _importedMemberCallOrdinals = _importedTemplateSummary is { MemberCalls.Count: > 0 }
                ? CollectTemplateMemberCallOrdinals(body)
                : null;
            LowerBlock(body);

            if (!CurrentBlock.HasTerminator)
            {
                CurrentBlock.Terminator = _function.Signature.ReturnType.Kind == StarkTypeKind.Void
                    ? new MidLevelIrTerminator(MidLevelIrTerminatorKind.Return, Targets: [], Location: _functionLocation)
                    : new MidLevelIrTerminator(MidLevelIrTerminatorKind.Unreachable, Targets: [], Location: _functionLocation);
            }
        }

        public bool TryLowerImportedTypedTemplateBody(ImportedTemplateTypedBodySummary typedBody)
        {
            if (!TryLowerImportedTypedTemplateStatementList(typedBody.Statements, createScope: true))
            {
                return false;
            }

            if (!CurrentBlock.HasTerminator)
            {
                CurrentBlock.Terminator = _function.Signature.ReturnType.Kind == StarkTypeKind.Void
                    ? new MidLevelIrTerminator(MidLevelIrTerminatorKind.Return, Targets: [], Location: _functionLocation)
                    : new MidLevelIrTerminator(MidLevelIrTerminatorKind.Unreachable, Targets: [], Location: _functionLocation);
            }

            return true;
        }

        private bool TryLowerImportedTypedTemplateStatementList(
            IReadOnlyList<ImportedTemplateTypedBodyStatementSummary> statements,
            bool createScope)
        {
            if (createScope)
            {
                _scopes.Push(new ScopeFrame());
            }

            try
            {
                foreach (var statement in statements)
                {
                    if (!TryLowerImportedTypedTemplateStatement(statement))
                    {
                        return false;
                    }
                }
            }
            finally
            {
                if (createScope)
                {
                    var scope = _scopes.Pop();
                    EmitStorageDead(scope);
                }
            }

            return true;
        }

        public void Dispose()
        {
            _logScope.Dispose();
        }

        private bool TryLowerImportedTypedTemplateStatement(ImportedTemplateTypedBodyStatementSummary statement)
        {
            var previousStatementLocation = _currentStatementLocation;
            _currentStatementLocation = _functionLocation;

            try
            {
                if (CurrentBlock.HasTerminator)
                {
                    CurrentBlock = CreateBlock("dead");
                }

                switch (statement.Kind)
                {
                    case ImportedTemplateTypedBodyStatementKind.LocalVariableDeclaration:
                        return TryLowerImportedTypedTemplateLocalVariable(statement);
                    case ImportedTemplateTypedBodyStatementKind.ExpressionStatement:
                        return TryLowerImportedTypedTemplateExpressionStatement(statement);
                    case ImportedTemplateTypedBodyStatementKind.Assignment:
                        return TryLowerImportedTypedTemplateAssignment(statement);
                    case ImportedTemplateTypedBodyStatementKind.Switch:
                        return TryLowerImportedTypedTemplateSwitch(statement);
                    case ImportedTemplateTypedBodyStatementKind.For:
                        return TryLowerImportedTypedTemplateFor(statement);
                    case ImportedTemplateTypedBodyStatementKind.While:
                        return TryLowerImportedTypedTemplateWhile(statement);
                    case ImportedTemplateTypedBodyStatementKind.If:
                        return TryLowerImportedTypedTemplateIf(statement);
                    case ImportedTemplateTypedBodyStatementKind.Break:
                        return TryLowerImportedTypedTemplateBreak();
                    case ImportedTemplateTypedBodyStatementKind.Continue:
                        return TryLowerImportedTypedTemplateContinue();
                    case ImportedTemplateTypedBodyStatementKind.Return:
                        return TryLowerImportedTypedTemplateReturn(statement);
                    default:
                        return false;
                }
            }
            finally
            {
                _currentStatementLocation = previousStatementLocation;
            }
        }

        private bool TryLowerImportedTypedTemplateLocalVariable(ImportedTemplateTypedBodyStatementSummary statement)
        {
            if (statement.Name is null
                || statement.StorageClass is null
                || statement.Type is not { } statementType)
            {
                return false;
            }

            var declaredType = ApplyGenericSubstitution(statementType);
            var name = statement.Name;
            RegisterLocal(name, declaredType, statement.StorageClass, statement.IsMutable, statement.IsConstant);
            TrackDeclaredLocal(name, declaredType);
            Emit(MidLevelIrStatementKind.StorageLive, name, name, declaredType);
            InitializeRuntimeDropState(name, declaredType, isActive: false);

            if (statement.Expression is null)
            {
                return true;
            }

            var initializer = LowerImportedTypedTemplateExpression(statement.Expression, declaredType);
            if (initializer is null)
            {
                return false;
            }

            EmitOperandAssignment(new MidLevelIrLocalOperand(name, declaredType), initializer, initializer.Text);
            RecordMoveFromOperand(initializer, declaredType);
            SetRuntimeDropState(name, isActive: true);
            return true;
        }

        private bool TryLowerImportedTypedTemplateExpressionStatement(ImportedTemplateTypedBodyStatementSummary statement)
        {
            if (statement.Expression is not { } expression)
            {
                return false;
            }

            if (expression.Kind == ImportedTemplateTypedBodyExpressionKind.DirectCall)
            {
                if (!TryBuildImportedTypedTemplateDirectCall(expression, out var directCall))
                {
                    return false;
                }

                Emit(MidLevelIrStatementKind.Evaluate, RenderImportedTypedTemplateExpression(expression), value: directCall);
                return true;
            }

            if (expression.Kind == ImportedTemplateTypedBodyExpressionKind.MemberCall)
            {
                if (!TryBuildImportedTypedTemplateMemberCall(expression, out var memberCall))
                {
                    return false;
                }

                Emit(MidLevelIrStatementKind.Evaluate, RenderImportedTypedTemplateExpression(expression), value: memberCall);
                return true;
            }

            if (TryLowerImportedTypedTemplateConditionalCallStatement(expression))
            {
                return true;
            }

            var operand = LowerImportedTypedTemplateExpression(expression, expectedType: null);
            if (operand is null)
            {
                return false;
            }

            Emit(
                MidLevelIrStatementKind.Evaluate,
                RenderImportedTypedTemplateExpression(expression),
                value: new MidLevelIrUseRValue(operand));
            return true;
        }

        private MidLevelIrOperand? LowerImportedTypedTemplateArrayInitializer(
            ImportedTemplateTypedBodyExpressionSummary expression,
            StarkTypeSymbol? expectedType)
        {
            var targetType = expectedType ?? expression.Type;
            if (targetType is null
                || targetType.Kind != StarkTypeKind.FixedArray
                || targetType.ElementType is null
                || targetType.FixedLength is not int fixedLength)
            {
                return null;
            }

            MidLevelIrOperand current = new MidLevelIrZeroInitializerOperand(targetType);
            var elementCount = Math.Min(fixedLength, expression.Args.Count);

            for (var index = 0; index < elementCount; index++)
            {
                var element = LowerImportedTypedTemplateExpression(expression.Args[index], targetType.ElementType);
                if (element is null)
                {
                    return null;
                }

                var updated = EmitTemporary(
                    new MidLevelIrInsertIndexRValue(
                        current,
                        index,
                        element,
                        targetType,
                        $"{current.Text}[{RenderImportedTypedTemplateExpression(expression.Args[index])}]"),
                    "insertindex");
                if (updated is null)
                {
                    return null;
                }

                current = updated;
            }

            return current;
        }

        private MidLevelIrOperand? LowerImportedTypedTemplateObjectInitializerExpression(
            ImportedTemplateTypedBodyExpressionSummary expression,
            StarkTypeSymbol? expectedType)
        {
            var targetType = expectedType ?? (expression.Type is { } publishedType ? ApplyGenericSubstitution(publishedType) : null);
            if (targetType is null
                || expression.Members.Count != expression.Args.Count
                || !TryBuildImportedTypedTemplateObjectInitializerMembers(targetType, expression, out var initializerMembers))
            {
                return null;
            }

            return LowerImportedTypedTemplateObjectInitializer(
                targetType,
                new MidLevelIrZeroInitializerOperand(targetType),
                initializerMembers,
                expression.Args);
        }

        private bool TryBuildImportedTypedTemplateObjectInitializerMembers(
            StarkTypeSymbol targetType,
            ImportedTemplateTypedBodyExpressionSummary expression,
            out IReadOnlyList<ImportedTemplateObjectInitializerMemberSummary> initializerMembers)
        {
            initializerMembers = [];

            if (!TryResolveImportedTypedTemplateNamedType(targetType, out var namedType, out var substitution))
            {
                return false;
            }

            var builtMembers = new List<ImportedTemplateObjectInitializerMemberSummary>(expression.Members.Count);
            foreach (var memberName in expression.Members)
            {
                if (!namedType.TryGetField(memberName, out var field, out var fieldIndex))
                {
                    return false;
                }

                var fieldType = substitution.Count == 0
                    ? field.Type
                    : FunctionOverloadFacts.SubstituteType(field.Type, substitution);
                builtMembers.Add(new ImportedTemplateObjectInitializerMemberSummary(
                    memberName,
                    fieldIndex,
                    fieldType));
            }

            initializerMembers = builtMembers;
            return true;
        }

        private bool TryResolveImportedTypedTemplateNamedType(
            StarkTypeSymbol targetType,
            out NamedTypeSymbol namedType,
            out IReadOnlyDictionary<string, StarkTypeSymbol> substitution)
        {
            namedType = null!;
            substitution = EmptyTypeSubstitution;

            if (targetType.Kind != StarkTypeKind.Named
                || targetType.NamedType is null)
            {
                return false;
            }

            if (!_typeModel.NamedTypes.TryGetValue(targetType.NamedType, out namedType!))
            {
                var baseName = StarkTypeSymbols.GetGenericBaseName(targetType.NamedType);
                if (!_typeModel.NamedTypes.TryGetValue(baseName, out namedType!))
                {
                    return false;
                }
            }

            if (targetType.TypeArguments is not { Count: > 0 } || namedType.GenericParams.Count == 0)
            {
                substitution = EmptyTypeSubstitution;
                return true;
            }

            if (namedType.GenericParams.Count != targetType.TypeArguments.Count)
            {
                return false;
            }

            var builtSubstitution = new Dictionary<string, StarkTypeSymbol>(namedType.GenericParams.Count, StringComparer.Ordinal);
            for (var index = 0; index < namedType.GenericParams.Count; index++)
            {
                builtSubstitution[namedType.GenericParams[index]] = targetType.TypeArguments[index];
            }

            substitution = builtSubstitution;
            return true;
        }

        private bool TryLowerImportedTypedTemplateConditionalCallStatement(
            ImportedTemplateTypedBodyExpressionSummary expression)
        {
            if (expression.Kind != ImportedTemplateTypedBodyExpressionKind.Conditional
                || expression.Args.Count != 3
                || !CanLowerImportedTypedTemplateConditionalCallStatementBranch(expression.Args[1])
                || !CanLowerImportedTypedTemplateConditionalCallStatementBranch(expression.Args[2]))
            {
                return false;
            }

            var condition = LowerImportedTypedTemplateExpression(expression.Args[0], StarkTypeSymbols.Bool);
            if (condition is null)
            {
                return false;
            }

            var thenBlock = CreateBlock("typed_cond_true");
            var elseBlock = CreateBlock("typed_cond_false");
            var joinBlock = CreateBlock("typed_cond_join");

            CurrentBlock.Terminator = new MidLevelIrTerminator(
                MidLevelIrTerminatorKind.Branch,
                [thenBlock.Id, elseBlock.Id],
                ConditionText: RenderImportedTypedTemplateExpression(expression.Args[0]),
                Condition: condition);

            CurrentBlock = thenBlock;
            if (!TryLowerImportedTypedTemplateConditionalCallStatementBranch(expression.Args[1]))
            {
                return false;
            }

            EnsureGoto(joinBlock.Id);

            CurrentBlock = elseBlock;
            if (!TryLowerImportedTypedTemplateConditionalCallStatementBranch(expression.Args[2]))
            {
                return false;
            }

            EnsureGoto(joinBlock.Id);

            CurrentBlock = joinBlock;
            return true;
        }

        private bool TryLowerImportedTypedTemplateConditionalCallStatementBranch(
            ImportedTemplateTypedBodyExpressionSummary expression)
        {
            if (expression.Kind == ImportedTemplateTypedBodyExpressionKind.DirectCall)
            {
                if (!TryBuildImportedTypedTemplateDirectCall(expression, out var directCall))
                {
                    return false;
                }

                Emit(MidLevelIrStatementKind.Evaluate, RenderImportedTypedTemplateExpression(expression), value: directCall);
                return true;
            }

            if (expression.Kind == ImportedTemplateTypedBodyExpressionKind.MemberCall)
            {
                if (!TryBuildImportedTypedTemplateMemberCall(expression, out var memberCall))
                {
                    return false;
                }

                Emit(MidLevelIrStatementKind.Evaluate, RenderImportedTypedTemplateExpression(expression), value: memberCall);
                return true;
            }

            return TryLowerImportedTypedTemplateConditionalCallStatement(expression);
        }

        private static bool CanLowerImportedTypedTemplateConditionalCallStatementBranch(
            ImportedTemplateTypedBodyExpressionSummary expression)
        {
            if (expression.Kind is ImportedTemplateTypedBodyExpressionKind.DirectCall or ImportedTemplateTypedBodyExpressionKind.MemberCall)
            {
                return true;
            }

            return expression.Kind == ImportedTemplateTypedBodyExpressionKind.Conditional
                && expression.Args.Count == 3
                && CanLowerImportedTypedTemplateConditionalCallStatementBranch(expression.Args[1])
                && CanLowerImportedTypedTemplateConditionalCallStatementBranch(expression.Args[2]);
        }

        private bool TryLowerImportedTypedTemplateAssignment(ImportedTemplateTypedBodyStatementSummary statement)
        {
            if (statement.Expression is null
                || !TryBuildImportedTypedTemplateAssignment(
                    statement.Name,
                    statement.TargetExpression,
                    statement.AssignmentOperator,
                    statement.Expression,
                    out var assignment))
            {
                return false;
            }

            EmitAssignment(assignment);
            return true;
        }

        private MidLevelIrOperand? LowerImportedTypedTemplateAssignmentExpression(
            ImportedTemplateTypedBodyExpressionSummary expression)
        {
            if (expression.Args.Count != 1
                || !TryBuildImportedTypedTemplateAssignment(
                    expression.Name,
                    expression.TargetExpression,
                    expression.AssignmentOperator,
                    expression.Args[0],
                    out var assignment))
            {
                return null;
            }

            EmitAssignment(assignment);
            return assignment.ResultValue;
        }

        private bool TryBuildImportedTypedTemplateAssignment(
            string? targetName,
            ImportedTemplateTypedBodyExpressionSummary? targetExpression,
            string? assignmentOperatorText,
            ImportedTemplateTypedBodyExpressionSummary valueExpression,
            out LoweredAssignment assignment)
        {
            assignment = default!;

            var assignmentOperator = string.IsNullOrEmpty(assignmentOperatorText)
                ? "="
                : assignmentOperatorText;
            PlaceTarget target;
            string assignmentTargetText;

            if (targetExpression is not null)
            {
                if (!TryResolveImportedTypedTemplateAssignmentTarget(targetExpression, out target))
                {
                    return false;
                }

                assignmentTargetText = RenderImportedTypedTemplateExpression(targetExpression);
            }
            else
            {
                if (targetName is not { } name
                    || !_localsByName.TryGetValue(name, out var local)
                    || local.IsConstant)
                {
                    return false;
                }

                target = new PlaceTarget(
                    name,
                    RootAddress: null,
                    local.Type,
                    local.Type,
                    Path: [],
                    UsesAddressModel: false,
                    IsAddressMutable: CanMutateThroughType(local.Type));
                assignmentTargetText = name;
            }

            if (target.RootName is { } rootName
                && _localsByName.TryGetValue(rootName, out var localBinding)
                && localBinding.IsConstant)
            {
                return false;
            }

            var assignmentText = $"{assignmentTargetText} {assignmentOperator} {RenderImportedTypedTemplateExpression(valueExpression)}";
            MidLevelIrOperand assignedValue;
            if (assignmentOperator == "=")
            {
                var loweredAssignedValue = LowerImportedTypedTemplateExpression(valueExpression, target.Type);
                if (loweredAssignedValue is null)
                {
                    return false;
                }

                assignedValue = loweredAssignedValue;
            }
            else
            {
                var currentValue = ReadPlace(target);
                var right = LowerImportedTypedTemplateExpression(valueExpression, currentValue.Type);
                if (right is null)
                {
                    return false;
                }

                var commonType = FindCommonType(currentValue.Type, right.Type);
                var leftValue = CoerceOperand(currentValue, commonType);
                var rightValue = CoerceOperand(right, commonType);
                if (leftValue is null || rightValue is null)
                {
                    return false;
                }

                var temp = EmitTemporary(
                    new MidLevelIrBinaryRValue(
                        MapAssignmentOperator(assignmentOperator),
                        leftValue,
                        rightValue,
                        commonType,
                        assignmentText),
                    "compound");
                if (temp is null)
                {
                    return false;
                }

                assignedValue = CoerceOperand(temp, target.Type) ?? temp;
            }

            assignment = BuildAssignment(target, assignedValue, assignmentText);
            return true;
        }

        private bool TryResolveImportedTypedTemplateAssignmentTarget(
            ImportedTemplateTypedBodyExpressionSummary expression,
            out PlaceTarget target)
        {
            target = default!;

            if (!TryResolveImportedTypedTemplateAssignmentTargetCore(expression, out target, out var rootOperand))
            {
                return false;
            }

            if (!target.UsesAddressModel
                && rootOperand is not null
                && IsBorrowParameterRoot(rootOperand))
            {
                target = target with { UsesAddressModel = true };
            }

            return true;
        }

        private bool TryResolveImportedTypedTemplateAssignmentTargetCore(
            ImportedTemplateTypedBodyExpressionSummary expression,
            out PlaceTarget target,
            out MidLevelIrOperand? rootOperand)
        {
            target = default!;
            rootOperand = null;

            if (expression.Kind == ImportedTemplateTypedBodyExpressionKind.NameReference)
            {
                if (expression.Name is null)
                {
                    return false;
                }

                var operand = ResolveNamedOperand(expression.Name);
                if (operand is null)
                {
                    return false;
                }

                target = new PlaceTarget(
                    operand.Text,
                    RootAddress: null,
                    operand.Type,
                    operand.Type,
                    Path: [],
                    UsesAddressModel: false,
                    IsAddressMutable: GetAddressMutability(operand));
                rootOperand = operand;
                return true;
            }

            if (expression.Kind == ImportedTemplateTypedBodyExpressionKind.UnaryOperation
                && string.Equals(expression.Name, "*", StringComparison.Ordinal)
                && expression.Args.Count == 1)
            {
                var address = LowerImportedTypedTemplateExpression(expression.Args[0], expectedType: null);
                if (address is null
                    || address.Type.Kind != StarkTypeKind.RawPointer
                    || !address.Type.IsMutablePointer
                    || address.Type.ElementType is not { } elementType
                    || !CanMutateThroughType(elementType))
                {
                    return false;
                }

                target = new PlaceTarget(
                    RootName: null,
                    RootAddress: address,
                    RootType: elementType,
                    Type: elementType,
                    Path: [],
                    UsesAddressModel: true,
                    IsAddressMutable: true);
                return true;
            }

            if (expression.Kind == ImportedTemplateTypedBodyExpressionKind.FieldAccess)
            {
                if (expression.Ordinal is not { } ordinal
                    || expression.Args.Count != 1
                    || !_importedTemplateFieldAccesses.TryGetValue(ordinal, out var publishedFieldAccess)
                    || !TryResolveImportedTypedTemplateAssignmentTargetCore(expression.Args[0], out target, out rootOperand))
                {
                    return false;
                }

                var fieldType = ProjectFrozenView(target.Type, ApplyGenericSubstitution(publishedFieldAccess.FieldType));
                var updatedPath = target.Path.ToList();
                updatedPath.Add(new PlacePathSegment(
                    PlacePathKind.Field,
                    publishedFieldAccess.FieldName,
                    publishedFieldAccess.FieldIndex,
                    IndexOperand: null,
                    ParentType: target.Type,
                    SegmentType: fieldType));
                target = target with
                {
                    Type = fieldType,
                    Path = updatedPath
                };
                return true;
            }

            if (expression.Kind == ImportedTemplateTypedBodyExpressionKind.IndexAccess)
            {
                if (expression.Args.Count < 2
                    || !TryResolveImportedTypedTemplateAssignmentTargetCore(expression.Args[0], out target, out rootOperand))
                {
                    return false;
                }

                var updatedPath = target.Path.ToList();
                var currentType = target.Type;
                var usesAddressModel = target.UsesAddressModel;
                var supportsAddressModel = target.RootAddress is not null
                    || (rootOperand is not null && SupportsAddressModel(rootOperand));

                for (var argumentIndex = 1; argumentIndex < expression.Args.Count; argumentIndex++)
                {
                    var index = LowerImportedTypedTemplateExpression(expression.Args[argumentIndex], expectedType: null);
                    if (index is null || index.Type.Kind != StarkTypeKind.Integer)
                    {
                        return false;
                    }

                    if (currentType.Kind == StarkTypeKind.FixedArray && currentType.ElementType is not null)
                    {
                        if (TryResolveImportedTypedTemplateConstantIndex(index, out var constantIndex))
                        {
                            var constantElementType = ProjectFrozenView(currentType, currentType.ElementType);
                            updatedPath.Add(new PlacePathSegment(
                                PlacePathKind.ConstantArrayIndex,
                                FieldName: null,
                                ConstantIndex: constantIndex,
                                IndexOperand: null,
                                ParentType: currentType,
                                SegmentType: constantElementType));
                            currentType = constantElementType;
                            continue;
                        }

                        if (!supportsAddressModel)
                        {
                            return false;
                        }

                        var dynamicElementType = ProjectFrozenView(currentType, currentType.ElementType);
                        updatedPath.Add(new PlacePathSegment(
                            PlacePathKind.DynamicArrayIndex,
                            FieldName: null,
                            ConstantIndex: null,
                            IndexOperand: index,
                            ParentType: currentType,
                            SegmentType: dynamicElementType));
                        currentType = dynamicElementType;
                        usesAddressModel = true;
                        supportsAddressModel = true;
                        continue;
                    }

                    if (currentType.Kind == StarkTypeKind.Slice && currentType.ElementType is not null)
                    {
                        var sliceElementType = ProjectFrozenView(currentType, currentType.ElementType);
                        updatedPath.Add(new PlacePathSegment(
                            PlacePathKind.SliceIndex,
                            FieldName: null,
                            ConstantIndex: null,
                            IndexOperand: index,
                            ParentType: currentType,
                            SegmentType: sliceElementType));
                        currentType = sliceElementType;
                        usesAddressModel = true;
                        supportsAddressModel = true;
                        continue;
                    }

                    if (currentType.Kind == StarkTypeKind.RawPointer && currentType.ElementType is not null)
                    {
                        updatedPath.Add(new PlacePathSegment(
                            PlacePathKind.RawPointerIndex,
                            FieldName: null,
                            ConstantIndex: null,
                            IndexOperand: index,
                            ParentType: currentType,
                            SegmentType: currentType.ElementType));
                        currentType = currentType.ElementType;
                        usesAddressModel = true;
                        supportsAddressModel = true;
                        continue;
                    }

                    return false;
                }

                target = target with
                {
                    Type = currentType,
                    Path = updatedPath,
                    UsesAddressModel = usesAddressModel
                };
                return true;
            }

            return false;
        }

        private bool TryLowerImportedTypedTemplateSwitch(ImportedTemplateTypedBodyStatementSummary statement)
        {
            if (statement.Expression is null
                || statement.SwitchCases is not { Count: > 0 })
            {
                return false;
            }

            var switchValue = LowerImportedTypedTemplateExpression(statement.Expression, expectedType: null);
            if (switchValue is null || !CanLowerSwitchType(switchValue.Type))
            {
                return false;
            }

            var defaultSectionCount = statement.SwitchCases.Count(static switchCase => switchCase.Kind == ImportedTemplateTypedSwitchCaseKind.Default);
            if (defaultSectionCount > 1)
            {
                return false;
            }

            var sections = new (ImportedTemplateTypedSwitchCaseSummary Case, IReadOnlyList<LowerableSwitchLabel> Labels, BasicBlockBuilder EntryBlock, BasicBlockBuilder BodyBlock)[statement.SwitchCases.Count];
            for (var index = 0; index < statement.SwitchCases.Count; index++)
            {
                var switchCase = statement.SwitchCases[index];
                if (!TryBuildImportedTypedTemplateSwitchLabel(switchCase, out var label))
                {
                    return false;
                }

                sections[index] = (
                    switchCase,
                    [label],
                    CreateBlock($"typed_switch_test_{index}"),
                    CreateBlock($"typed_switch_case_{index}"));
            }

            var exitBlock = CreateBlock("typed_switch_exit");
            var defaultTarget = sections
                .Where(static section => section.Labels.Any(static label => label.IsDefault && label.GuardExpression is null && label.ImportedGuardExpression is null && label.CaptureName is null))
                .Select(static section => section.BodyBlock.Id)
                .FirstOrDefault(exitBlock.Id);

            if (!TryRegisterSwitchCaptureLocals(sections.Select(static section => section.Labels), switchValue.Type))
            {
                return false;
            }

            if (sections.Length == 0)
            {
                CurrentBlock.Terminator = new MidLevelIrTerminator(MidLevelIrTerminatorKind.Goto, [defaultTarget]);
            }
            else
            {
                CurrentBlock.Terminator = new MidLevelIrTerminator(MidLevelIrTerminatorKind.Goto, [sections[0].EntryBlock.Id]);

                for (var index = 0; index < sections.Length; index++)
                {
                    CurrentBlock = sections[index].EntryBlock;
                    var nextTarget = index + 1 < sections.Length
                        ? sections[index + 1].EntryBlock.Id
                        : defaultTarget;
                    if (!EmitSwitchSectionDecision(
                            sections[index].Labels,
                            switchValue,
                            sections[index].BodyBlock.Id,
                            nextTarget,
                            RenderImportedTypedTemplateExpression(statement.Expression),
                            index))
                    {
                        return false;
                    }
                }
            }

            _breakTargets.Push(new BreakTargets(exitBlock.Id, _scopes.Count));
            try
            {
                foreach (var section in sections)
                {
                    CurrentBlock = section.BodyBlock;
                    if (!TryLowerImportedTypedTemplateStatementList(section.Case.Statements, createScope: false))
                    {
                        return false;
                    }

                    if (!CurrentBlock.HasTerminator)
                    {
                        EnsureGoto(exitBlock.Id);
                    }
                }
            }
            finally
            {
                _breakTargets.Pop();
            }

            CurrentBlock = exitBlock;
            return true;
        }

        private bool TryBuildImportedTypedTemplateSwitchLabel(
            ImportedTemplateTypedSwitchCaseSummary switchCase,
            out LowerableSwitchLabel label)
        {
            label = null!;

            switch (switchCase.Kind)
            {
                case ImportedTemplateTypedSwitchCaseKind.Literal:
                    if (switchCase.Expression is null)
                    {
                        return false;
                    }

                    label = new LowerableSwitchLabel(
                        RenderImportedTypedTemplateExpression(switchCase.Expression),
                        Literal: null,
                        GuardExpression: null,
                        IsDefault: false,
                        IsMatchAll: false,
                        CaptureName: null,
                        AggregatePattern: null,
                        ImportedLiteralExpression: switchCase.Expression,
                        ImportedGuardExpression: switchCase.GuardExpression);
                    return true;

                case ImportedTemplateTypedSwitchCaseKind.MatchAll:
                    label = new LowerableSwitchLabel(
                        switchCase.Name is null ? "_" : $"var {switchCase.Name}",
                        Literal: null,
                        GuardExpression: null,
                        IsDefault: false,
                        IsMatchAll: true,
                        CaptureName: switchCase.Name,
                        AggregatePattern: null,
                        ImportedLiteralExpression: null,
                        ImportedGuardExpression: switchCase.GuardExpression);
                    return true;

                case ImportedTemplateTypedSwitchCaseKind.Default:
                    if (switchCase.Name is not null
                        || switchCase.Expression is not null
                        || switchCase.GuardExpression is not null)
                    {
                        return false;
                    }

                    label = new LowerableSwitchLabel(
                        "default",
                        Literal: null,
                        GuardExpression: null,
                        IsDefault: true,
                        IsMatchAll: true,
                        CaptureName: null,
                        AggregatePattern: null);
                    return true;

                case ImportedTemplateTypedSwitchCaseKind.EnumPattern:
                case ImportedTemplateTypedSwitchCaseKind.AggregatePattern:
                    if (!TryBuildImportedTypedTemplateSwitchPattern(switchCase, out var aggregatePattern)
                        || aggregatePattern is null
                        || aggregatePattern.WholeCaptureName is not null)
                    {
                        return false;
                    }

                    label = new LowerableSwitchLabel(
                        "typed-switch-pattern",
                        Literal: null,
                        GuardExpression: null,
                        IsDefault: false,
                        IsMatchAll: false,
                        CaptureName: null,
                        AggregatePattern: aggregatePattern,
                        ImportedLiteralExpression: null,
                        ImportedGuardExpression: switchCase.GuardExpression);
                    return true;

                default:
                    return false;
            }
        }

        private bool TryBuildImportedTypedTemplateSwitchPattern(
            ImportedTemplateTypedSwitchCaseSummary switchCase,
            out LowerableAggregatePattern? aggregatePattern)
        {
            aggregatePattern = null;

            switch (switchCase.Kind)
            {
                case ImportedTemplateTypedSwitchCaseKind.EnumPattern:
                    return TryBuildImportedTypedTemplateEnumSwitchPattern(switchCase, out aggregatePattern);
                case ImportedTemplateTypedSwitchCaseKind.AggregatePattern:
                    return TryBuildImportedTypedTemplateAggregateSwitchPattern(switchCase, out aggregatePattern);
                default:
                    return false;
            }
        }

        private bool TryBuildImportedTypedTemplateEnumSwitchPattern(
            ImportedTemplateTypedSwitchCaseSummary switchCase,
            out LowerableAggregatePattern? aggregatePattern)
        {
            aggregatePattern = null;

            return switchCase.Ordinal is { } ordinal
                && TryBuildImportedTypedTemplateEnumSwitchPattern(
                    ordinal,
                    switchCase.Members,
                    out aggregatePattern);
        }

        private bool TryBuildImportedTypedTemplateEnumSwitchPattern(
            int ordinal,
            IReadOnlyList<ImportedTemplateTypedSwitchFieldPatternSummary> memberPatterns,
            out LowerableAggregatePattern? aggregatePattern)
        {
            aggregatePattern = null;

            if (!_importedTemplateEnumPatterns.TryGetValue(ordinal, out var publishedEnumPattern))
            {
                return false;
            }

            var enumType = ApplyGenericSubstitution(publishedEnumPattern.EnumType);
            if (enumType.Kind != StarkTypeKind.Named
                || enumType.NamedType is null
                || !_enumLayoutModel.Layouts.TryGetValue(enumType.NamedType, out var enumLayout)
                || !enumLayout.TryGetVariant(publishedEnumPattern.VariantName, out var enumVariant))
            {
                return false;
            }

            if (enumVariant.UsesNamedFields)
            {
                if (publishedEnumPattern.Members.Count != enumVariant.Fields.Count
                    || memberPatterns.Count != publishedEnumPattern.Members.Count)
                {
                    return false;
                }

                var fieldPatterns = new LowerableAggregateFieldPattern[memberPatterns.Count];
                for (var memberOrdinal = 0; memberOrdinal < memberPatterns.Count; memberOrdinal++)
                {
                    var publishedMember = publishedEnumPattern.Members[memberOrdinal];
                    if (publishedMember.FieldIndex < 0 || publishedMember.FieldIndex >= enumVariant.Fields.Count)
                    {
                        return false;
                    }

                    var field = enumVariant.Fields[publishedMember.FieldIndex];
                    if (!TryBuildImportedTypedTemplateSwitchFieldPattern(
                            memberPatterns[memberOrdinal],
                            publishedMember.FieldName,
                            field.StorageFieldName,
                            field.StorageFieldIndex,
                            ApplyGenericSubstitution(publishedMember.FieldType),
                            out fieldPatterns[memberOrdinal]))
                    {
                        return false;
                    }
                }

                aggregatePattern = new LowerableAggregatePattern(
                    enumType.NamedType,
                    publishedEnumPattern.VariantName,
                    fieldPatterns,
                    WholeCaptureName: null);
                return true;
            }

            if (memberPatterns.Count != enumVariant.Fields.Count)
            {
                return false;
            }

            var tupleFieldPatterns = new LowerableAggregateFieldPattern[memberPatterns.Count];
            for (var fieldIndex = 0; fieldIndex < memberPatterns.Count; fieldIndex++)
            {
                var field = enumVariant.Fields[fieldIndex];
                if (!TryBuildImportedTypedTemplateSwitchFieldPattern(
                        memberPatterns[fieldIndex],
                        field.SourceFieldName ?? field.SourcePosition.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        field.StorageFieldName,
                        field.StorageFieldIndex,
                        ApplyGenericSubstitution(field.Type),
                        out tupleFieldPatterns[fieldIndex]))
                {
                    return false;
                }
            }

            aggregatePattern = new LowerableAggregatePattern(
                enumType.NamedType,
                publishedEnumPattern.VariantName,
                tupleFieldPatterns,
                WholeCaptureName: null);
            return true;
        }

        private bool TryBuildImportedTypedTemplateAggregateSwitchPattern(
            ImportedTemplateTypedSwitchCaseSummary switchCase,
            out LowerableAggregatePattern? aggregatePattern)
        {
            aggregatePattern = null;

            return switchCase.Ordinal is { } ordinal
                && TryBuildImportedTypedTemplateAggregateSwitchPattern(
                    ordinal,
                    switchCase.Members,
                    out aggregatePattern);
        }

        private bool TryBuildImportedTypedTemplateAggregateSwitchPattern(
            int ordinal,
            IReadOnlyList<ImportedTemplateTypedSwitchFieldPatternSummary> memberPatterns,
            out LowerableAggregatePattern? aggregatePattern)
        {
            aggregatePattern = null;

            if (!_importedTemplateAggregatePatterns.TryGetValue(ordinal, out var publishedAggregatePattern))
            {
                return false;
            }

            var aggregateType = ApplyGenericSubstitution(publishedAggregatePattern.Type);
            if (aggregateType.Kind != StarkTypeKind.Named
                || aggregateType.NamedType is null
                || !_typeModel.NamedTypes.TryGetValue(aggregateType.NamedType, out var namedType))
            {
                return false;
            }

            if (memberPatterns.Count != 0 && memberPatterns.Count != namedType.OrderedFields.Count)
            {
                return false;
            }

            var fieldPatterns = new LowerableAggregateFieldPattern[memberPatterns.Count];
            for (var fieldIndex = 0; fieldIndex < memberPatterns.Count; fieldIndex++)
            {
                var field = namedType.OrderedFields[fieldIndex];
                if (!TryBuildImportedTypedTemplateSwitchFieldPattern(
                        memberPatterns[fieldIndex],
                        field.Name,
                        field.Name,
                        fieldIndex,
                        ApplyGenericSubstitution(field.Type),
                        out fieldPatterns[fieldIndex]))
                {
                    return false;
                }
            }

            aggregatePattern = new LowerableAggregatePattern(
                aggregateType.NamedType,
                EnumVariantName: null,
                fieldPatterns,
                WholeCaptureName: null);
            return true;
        }

        private bool TryBuildImportedTypedTemplateSwitchFieldPattern(
            ImportedTemplateTypedSwitchFieldPatternSummary fieldPattern,
            string fieldName,
            string storageFieldName,
            int fieldIndex,
            StarkTypeSymbol fieldType,
            out LowerableAggregateFieldPattern parsedFieldPattern)
        {
            if (fieldPattern.Kind == ImportedTemplateTypedSwitchFieldPatternKind.Discard)
            {
                parsedFieldPattern = new LowerableAggregateFieldPattern(
                    fieldName,
                    storageFieldName,
                    fieldIndex,
                    fieldType,
                    AggregatePatternFieldKind.Discard,
                    "_",
                    Literal: null,
                    CaptureName: null,
                    NestedPattern: null,
                    ImportedLiteralExpression: null);
                return true;
            }

            if (fieldPattern.Kind == ImportedTemplateTypedSwitchFieldPatternKind.Capture
                && fieldPattern.Name is not null)
            {
                parsedFieldPattern = new LowerableAggregateFieldPattern(
                    fieldName,
                    storageFieldName,
                    fieldIndex,
                    fieldType,
                    AggregatePatternFieldKind.Capture,
                    $"var {fieldPattern.Name}",
                    Literal: null,
                    CaptureName: fieldPattern.Name,
                    NestedPattern: null,
                    ImportedLiteralExpression: null);
                return true;
            }

            if (fieldPattern.Kind == ImportedTemplateTypedSwitchFieldPatternKind.Literal
                && fieldPattern.Expression is { Kind: ImportedTemplateTypedBodyExpressionKind.Literal } literalExpression)
            {
                parsedFieldPattern = new LowerableAggregateFieldPattern(
                    fieldName,
                    storageFieldName,
                    fieldIndex,
                    fieldType,
                    AggregatePatternFieldKind.Literal,
                    RenderImportedTypedTemplateExpression(literalExpression),
                    Literal: null,
                    CaptureName: null,
                    NestedPattern: null,
                    ImportedLiteralExpression: literalExpression);
                return true;
            }

            if (fieldPattern.Kind == ImportedTemplateTypedSwitchFieldPatternKind.EnumPattern
                && fieldPattern.Ordinal is { } enumOrdinal
                && TryBuildImportedTypedTemplateEnumSwitchPattern(enumOrdinal, fieldPattern.Members, out var nestedEnumPattern)
                && nestedEnumPattern is not null
                && nestedEnumPattern.WholeCaptureName is null)
            {
                parsedFieldPattern = new LowerableAggregateFieldPattern(
                    fieldName,
                    storageFieldName,
                    fieldIndex,
                    fieldType,
                    AggregatePatternFieldKind.Nested,
                    "typed-nested-enum-pattern",
                    Literal: null,
                    CaptureName: null,
                    NestedPattern: nestedEnumPattern,
                    ImportedLiteralExpression: null);
                return true;
            }

            if (fieldPattern.Kind == ImportedTemplateTypedSwitchFieldPatternKind.AggregatePattern
                && fieldPattern.Ordinal is { } aggregateOrdinal
                && TryBuildImportedTypedTemplateAggregateSwitchPattern(aggregateOrdinal, fieldPattern.Members, out var nestedAggregatePattern)
                && nestedAggregatePattern is not null
                && nestedAggregatePattern.WholeCaptureName is null)
            {
                parsedFieldPattern = new LowerableAggregateFieldPattern(
                    fieldName,
                    storageFieldName,
                    fieldIndex,
                    fieldType,
                    AggregatePatternFieldKind.Nested,
                    "typed-nested-aggregate-pattern",
                    Literal: null,
                    CaptureName: null,
                    NestedPattern: nestedAggregatePattern,
                    ImportedLiteralExpression: null);
                return true;
            }

            parsedFieldPattern = default!;
            return false;
        }

        private bool TryLowerImportedTypedTemplateIf(ImportedTemplateTypedBodyStatementSummary statement)
        {
            if (statement.Expression is null)
            {
                return false;
            }

            var condition = LowerImportedTypedTemplateExpression(statement.Expression, StarkTypeSymbols.Bool);
            if (condition is null)
            {
                return false;
            }

            var thenBlock = CreateBlock("typed_if_then");
            var hasElse = statement.ElseBranch.Count > 0;
            var elseBlock = hasElse ? CreateBlock("typed_if_else") : null;
            var joinBlock = CreateBlock("typed_if_join");

            CurrentBlock.Terminator = new MidLevelIrTerminator(
                MidLevelIrTerminatorKind.Branch,
                [thenBlock.Id, elseBlock?.Id ?? joinBlock.Id],
                ConditionText: RenderImportedTypedTemplateExpression(statement.Expression),
                Condition: condition);

            CurrentBlock = thenBlock;
            if (!TryLowerImportedTypedTemplateStatementList(statement.ThenBranch, createScope: true))
            {
                return false;
            }

            if (!CurrentBlock.HasTerminator)
            {
                EnsureGoto(joinBlock.Id);
            }

            if (elseBlock is not null)
            {
                CurrentBlock = elseBlock;
                if (!TryLowerImportedTypedTemplateStatementList(statement.ElseBranch, createScope: true))
                {
                    return false;
                }

                if (!CurrentBlock.HasTerminator)
                {
                    EnsureGoto(joinBlock.Id);
                }
            }

            CurrentBlock = joinBlock;
            return true;
        }

        private bool TryLowerImportedTypedTemplateBreak()
        {
            if (_breakTargets.Count == 0)
            {
                return false;
            }

            var breakTarget = _breakTargets.Peek();
            EmitStorageDeadBeyondDepth(breakTarget.ScopeDepth);
            CurrentBlock.Terminator = new MidLevelIrTerminator(MidLevelIrTerminatorKind.Goto, [breakTarget.Target]);
            return true;
        }

        private bool TryLowerImportedTypedTemplateContinue()
        {
            if (_loops.Count == 0)
            {
                return false;
            }

            var loop = _loops.Peek();
            EmitStorageDeadBeyondDepth(loop.ScopeDepth);
            CurrentBlock.Terminator = new MidLevelIrTerminator(MidLevelIrTerminatorKind.Goto, [loop.ContinueTarget]);
            return true;
        }

        private bool TryLowerImportedTypedTemplateWhile(ImportedTemplateTypedBodyStatementSummary statement)
        {
            if (statement.Expression is null)
            {
                return false;
            }

            var conditionBlock = CreateBlock("typed_while_cond");
            var bodyBlock = CreateBlock("typed_while_body");
            var exitBlock = CreateBlock("typed_while_exit");

            EnsureGoto(conditionBlock.Id);

            CurrentBlock = conditionBlock;
            var condition = LowerImportedTypedTemplateExpression(statement.Expression, StarkTypeSymbols.Bool);
            if (condition is null)
            {
                return false;
            }

            CurrentBlock.Terminator = new MidLevelIrTerminator(
                MidLevelIrTerminatorKind.Branch,
                [bodyBlock.Id, exitBlock.Id],
                ConditionText: RenderImportedTypedTemplateExpression(statement.Expression),
                Condition: condition);

            _loops.Push(new LoopTargets(conditionBlock.Id, exitBlock.Id, _scopes.Count));
            _breakTargets.Push(new BreakTargets(exitBlock.Id, _scopes.Count));
            CurrentBlock = bodyBlock;
            try
            {
                if (!TryLowerImportedTypedTemplateStatementList(statement.Body, createScope: true))
                {
                    return false;
                }

                if (!CurrentBlock.HasTerminator)
                {
                    EnsureGoto(conditionBlock.Id);
                }
            }
            finally
            {
                _breakTargets.Pop();
                _loops.Pop();
            }

            CurrentBlock = exitBlock;
            return true;
        }

        private bool TryLowerImportedTypedTemplateFor(ImportedTemplateTypedBodyStatementSummary statement)
        {
            _scopes.Push(new ScopeFrame());
            try
            {
                if (!TryLowerImportedTypedTemplateStatementList(statement.Initializer, createScope: false))
                {
                    return false;
                }

                var conditionBlock = CreateBlock("typed_for_cond");
                var bodyBlock = CreateBlock("typed_for_body");
                var iteratorBlock = CreateBlock("typed_for_iter");
                var exitBlock = CreateBlock("typed_for_exit");

                EnsureGoto(conditionBlock.Id);

                CurrentBlock = conditionBlock;
                if (statement.Expression is null)
                {
                    return false;
                }

                var condition = LowerImportedTypedTemplateExpression(statement.Expression, StarkTypeSymbols.Bool);
                if (condition is null)
                {
                    return false;
                }

                CurrentBlock.Terminator = new MidLevelIrTerminator(
                    MidLevelIrTerminatorKind.Branch,
                    [bodyBlock.Id, exitBlock.Id],
                    ConditionText: RenderImportedTypedTemplateExpression(statement.Expression),
                    Condition: condition);

                _loops.Push(new LoopTargets(iteratorBlock.Id, exitBlock.Id, _scopes.Count));
                _breakTargets.Push(new BreakTargets(exitBlock.Id, _scopes.Count));
                CurrentBlock = bodyBlock;
                try
                {
                    if (!TryLowerImportedTypedTemplateStatementList(statement.Body, createScope: true))
                    {
                        return false;
                    }

                    if (!CurrentBlock.HasTerminator)
                    {
                        EnsureGoto(iteratorBlock.Id);
                    }

                    CurrentBlock = iteratorBlock;
                    if (!TryLowerImportedTypedTemplateStatementList(statement.Iterator, createScope: false))
                    {
                        return false;
                    }

                    if (!CurrentBlock.HasTerminator)
                    {
                        EnsureGoto(conditionBlock.Id);
                    }
                }
                finally
                {
                    _breakTargets.Pop();
                    _loops.Pop();
                }

                CurrentBlock = exitBlock;
                return true;
            }
            finally
            {
                var scope = _scopes.Pop();
                EmitStorageDead(scope);
            }
        }

        private bool TryLowerImportedTypedTemplateReturn(ImportedTemplateTypedBodyStatementSummary statement)
        {
            if (statement.Expression is null)
            {
                if (_function.Signature.ReturnType.Kind != StarkTypeKind.Void)
                {
                    return false;
                }

                EmitStorageDeadBeyondDepth(0);
                CurrentBlock.Terminator = new MidLevelIrTerminator(
                    MidLevelIrTerminatorKind.Return,
                    Targets: []);
                return true;
            }

            var operand = LowerImportedTypedTemplateExpression(statement.Expression, _function.Signature.ReturnType);
            if (operand is null)
            {
                return false;
            }

            RecordMoveFromOperand(operand, _function.Signature.ReturnType);
            EmitStorageDeadBeyondDepth(0);
            CurrentBlock.Terminator = new MidLevelIrTerminator(
                MidLevelIrTerminatorKind.Return,
                Targets: [],
                ValueText: operand.Text,
                Value: operand);
            return true;
        }

        private MidLevelIrOperand? LowerImportedTypedTemplateExpression(
            ImportedTemplateTypedBodyExpressionSummary expression,
            StarkTypeSymbol? expectedType)
        {
            switch (expression.Kind)
            {
                case ImportedTemplateTypedBodyExpressionKind.NameReference:
                {
                    if (expression.Name is null)
                    {
                        return null;
                    }

                    var operand = ResolveNamedOperand(expression.Name);
                    return operand is null || expectedType is null
                        ? operand
                        : CoerceOperand(operand, expectedType);
                }

                case ImportedTemplateTypedBodyExpressionKind.Literal:
                {
                    var result = LowerImportedTypedTemplateLiteral(expression);
                    return result is null || expectedType is null
                        ? result
                        : CoerceOperand(result, expectedType);
                }

                case ImportedTemplateTypedBodyExpressionKind.ArrayInitializer:
                {
                    var result = LowerImportedTypedTemplateArrayInitializer(expression, expectedType);
                    return result is null || expectedType is null
                        ? result
                        : CoerceOperand(result, expectedType);
                }

                case ImportedTemplateTypedBodyExpressionKind.ObjectInitializer:
                {
                    var result = LowerImportedTypedTemplateObjectInitializerExpression(expression, expectedType);
                    return result is null || expectedType is null
                        ? result
                        : CoerceOperand(result, expectedType);
                }

                case ImportedTemplateTypedBodyExpressionKind.Assignment:
                {
                    var result = LowerImportedTypedTemplateAssignmentExpression(expression);
                    return result is null || expectedType is null
                        ? result
                        : CoerceOperand(result, expectedType);
                }

                case ImportedTemplateTypedBodyExpressionKind.Conversion:
                {
                    var result = LowerImportedTypedTemplateConversion(expression, expectedType);
                    return result is null || expectedType is null
                        ? result
                        : CoerceOperand(result, expectedType);
                }

                case ImportedTemplateTypedBodyExpressionKind.UnaryOperation:
                {
                    var result = LowerImportedTypedTemplateUnary(expression, expectedType);
                    return result is null || expectedType is null
                        ? result
                        : CoerceOperand(result, expectedType);
                }

                case ImportedTemplateTypedBodyExpressionKind.BinaryOperation:
                {
                    var result = LowerImportedTypedTemplateBinary(expression, expectedType);
                    return result is null || expectedType is null
                        ? result
                        : CoerceOperand(result, expectedType);
                }

                case ImportedTemplateTypedBodyExpressionKind.ComparisonChain:
                {
                    var result = LowerImportedTypedTemplateComparisonChain(expression, expectedType);
                    return result is null || expectedType is null
                        ? result
                        : CoerceOperand(result, expectedType);
                }

                case ImportedTemplateTypedBodyExpressionKind.Conditional:
                {
                    var result = LowerImportedTypedTemplateConditional(expression, expectedType);
                    return result is null || expectedType is null
                        ? result
                        : CoerceOperand(result, expectedType);
                }

                case ImportedTemplateTypedBodyExpressionKind.ObjectCreation:
                {
                    var result = LowerImportedTypedTemplateObjectCreation(expression);
                    return result is null || expectedType is null
                        ? result
                        : CoerceOperand(result, expectedType);
                }

                case ImportedTemplateTypedBodyExpressionKind.EnumConstructor:
                {
                    var result = LowerImportedTypedTemplateEnumConstructor(expression);
                    return result is null || expectedType is null
                        ? result
                        : CoerceOperand(result, expectedType);
                }

                case ImportedTemplateTypedBodyExpressionKind.EnumCall:
                {
                    var result = LowerImportedTypedTemplateEnumCall(expression);
                    return result is null || expectedType is null
                        ? result
                        : CoerceOperand(result, expectedType);
                }

                case ImportedTemplateTypedBodyExpressionKind.EnumValue:
                {
                    var result = LowerImportedTypedTemplateEnumValue(expression);
                    return result is null || expectedType is null
                        ? result
                        : CoerceOperand(result, expectedType);
                }

                case ImportedTemplateTypedBodyExpressionKind.DirectCall:
                {
                    if (!TryBuildImportedTypedTemplateDirectCall(expression, out var call))
                    {
                        return null;
                    }

                    if (call.Type.Kind == StarkTypeKind.Void)
                    {
                        return null;
                    }

                    var result = EmitTemporary(call, "call");
                    return result is null || expectedType is null
                        ? result
                        : CoerceOperand(result, expectedType);
                }

                case ImportedTemplateTypedBodyExpressionKind.IndexAccess:
                {
                    var result = LowerImportedTypedTemplateIndexAccess(expression);
                    return result is null || expectedType is null
                        ? result
                        : CoerceOperand(result, expectedType);
                }

                case ImportedTemplateTypedBodyExpressionKind.FieldAccess:
                {
                    if (expression.Ordinal is not { } ordinal
                        || expression.Args.Count != 1)
                    {
                        return null;
                    }

                    var receiver = LowerImportedTypedTemplateExpression(expression.Args[0], expectedType: null);
                    if (receiver is null
                        || !_importedTemplateFieldAccesses.TryGetValue(ordinal, out var publishedFieldAccess))
                    {
                        return null;
                    }

                    var result = LowerKnownFieldAccess(
                        receiver,
                        publishedFieldAccess.FieldName,
                        publishedFieldAccess.FieldIndex,
                        ApplyGenericSubstitution(publishedFieldAccess.FieldType),
                        publishedFieldAccess.FieldName);
                    return expectedType is null
                        ? result
                        : CoerceOperand(result, expectedType);
                }

                case ImportedTemplateTypedBodyExpressionKind.MemberCall:
                {
                    if (!TryBuildImportedTypedTemplateMemberCall(expression, out var memberCall))
                    {
                        return null;
                    }

                    if (memberCall.Type.Kind == StarkTypeKind.Void)
                    {
                        return null;
                    }

                    var result = EmitTemporary(memberCall, "call");
                    return result is null || expectedType is null
                        ? result
                        : CoerceOperand(result, expectedType);
                }

                default:
                    return null;
            }
        }

        private MidLevelIrOperand? LowerImportedTypedTemplateIndexAccess(
            ImportedTemplateTypedBodyExpressionSummary expression)
        {
            if (expression.Args.Count < 1)
            {
                return null;
            }

            var target = LowerImportedTypedTemplateExpression(expression.Args[0], expectedType: null);
            if (target is null)
            {
                return null;
            }

            if (target.Type.Kind is StarkTypeKind.Ascii or StarkTypeKind.Unicode)
            {
                if (expression.Args.Count == 1)
                {
                    return target;
                }

                if (expression.Args.Count == 2)
                {
                    var start = LowerImportedTypedTemplateExpression(expression.Args[1], expectedType: null);
                    if (start is null || start.Type.Kind != StarkTypeKind.Integer)
                    {
                        return null;
                    }

                    return LowerTextSlice(
                        target,
                        start,
                        new MidLevelIrIntegerConstantOperand(BigInteger.One, StarkTypeSymbols.Integer(64)),
                        $"{target.Text}[{RenderImportedTypedTemplateExpression(expression.Args[1])}]");
                }

                if (expression.Args.Count != 3)
                {
                    MarkUnsupported(reason: "Imported typed template-body text postfix brackets currently support full-view, single-index, or start-and-length access.");
                    return null;
                }

                var sliceStart = LowerImportedTypedTemplateExpression(expression.Args[1], expectedType: null);
                var sliceLength = LowerImportedTypedTemplateExpression(expression.Args[2], expectedType: null);
                if (sliceStart is null
                    || sliceLength is null
                    || sliceStart.Type.Kind != StarkTypeKind.Integer
                    || sliceLength.Type.Kind != StarkTypeKind.Integer)
                {
                    return null;
                }

                return LowerTextSlice(
                    target,
                    sliceStart,
                    sliceLength,
                    $"{target.Text}[{RenderImportedTypedTemplateExpression(expression.Args[1])}, {RenderImportedTypedTemplateExpression(expression.Args[2])}]");
            }

            var current = target;
            for (var argumentIndex = 1; argumentIndex < expression.Args.Count; argumentIndex++)
            {
                var index = LowerImportedTypedTemplateExpression(expression.Args[argumentIndex], expectedType: null);
                if (index is null || index.Type.Kind != StarkTypeKind.Integer)
                {
                    return null;
                }

                if (current.Type.Kind == StarkTypeKind.FixedArray && current.Type.ElementType is not null)
                {
                    if (TryResolveImportedTypedTemplateConstantIndex(index, out var constantIndex))
                    {
                        var elementType = ProjectFrozenView(current.Type, current.Type.ElementType);
                        var extracted = EmitTemporary(
                            new MidLevelIrExtractIndexRValue(
                                current,
                                constantIndex,
                                elementType,
                                $"{current.Text}[{constantIndex}]"),
                            "index");
                        if (extracted is null)
                        {
                            return null;
                        }

                        current = extracted;
                        continue;
                    }

                    var projectedElementType = ProjectFrozenView(current.Type, current.Type.ElementType);
                    var baseAddress = TryCreateDynamicFixedArrayBaseAddress(current);
                    if (baseAddress is null)
                    {
                        MarkUnsupported(reason: "Dynamic fixed-array indexing from imported typed template bodies currently requires an addressable fixed-array source.");
                        return null;
                    }

                    var elementAddress = EmitTemporary(
                        new MidLevelIrElementAddressRValue(
                            baseAddress,
                            current.Type,
                            index,
                            ConstantIndex: null,
                            AddressType(projectedElementType, isMutable: CanMutateThroughType(current.Type)),
                            $"{current.Text}[{index.Text}]"),
                        "addr");
                    if (elementAddress is null)
                    {
                        return null;
                    }

                    var loaded = EmitTemporary(
                        new MidLevelIrLoadIndirectRValue(
                            elementAddress,
                            projectedElementType,
                            $"{current.Text}[{index.Text}]"),
                        "load");
                    if (loaded is null)
                    {
                        return null;
                    }

                    current = loaded;
                    continue;
                }

                if (current.Type.Kind == StarkTypeKind.Slice && current.Type.ElementType is not null)
                {
                    var elementType = ProjectFrozenView(current.Type, current.Type.ElementType);
                    var elementAddress = EmitTemporary(
                        new MidLevelIrSliceElementAddressRValue(
                            current,
                            index,
                            AddressType(elementType, current.Type.IsMutableView && CanMutateThroughType(current.Type)),
                            $"{current.Text}[{index.Text}]"),
                        "addr");
                    if (elementAddress is null)
                    {
                        return null;
                    }

                    var loaded = EmitTemporary(
                        new MidLevelIrLoadIndirectRValue(
                            elementAddress,
                            elementType,
                            $"{current.Text}[{index.Text}]"),
                        "load");
                    if (loaded is null)
                    {
                        return null;
                    }

                    current = loaded;
                    continue;
                }

                if (current.Type.Kind == StarkTypeKind.RawPointer && current.Type.ElementType is not null)
                {
                    var elementType = current.Type.ElementType;
                    var elementAddress = EmitTemporary(
                        new MidLevelIrElementAddressRValue(
                            current,
                            elementType,
                            index,
                            ConstantIndex: null,
                            AddressType(elementType, current.Type.IsMutablePointer && CanMutateThroughType(elementType)),
                            $"{current.Text}[{index.Text}]"),
                        "addr");
                    if (elementAddress is null)
                    {
                        return null;
                    }

                    var loaded = EmitTemporary(
                        new MidLevelIrLoadIndirectRValue(
                            elementAddress,
                            elementType,
                            $"{current.Text}[{index.Text}]"),
                        "load");
                    if (loaded is null)
                    {
                        return null;
                    }

                    current = loaded;
                    continue;
                }

                MarkUnsupported(reason: "Imported typed template-body indexing is currently limited to fixed arrays, raw pointers, and slices, and text slicing with two integer indices.");
                return null;
            }

            return current;
        }

        private static bool TryResolveImportedTypedTemplateConstantIndex(
            MidLevelIrOperand operand,
            out int constantIndex)
        {
            constantIndex = 0;

            if (operand is not MidLevelIrIntegerConstantOperand integerConstant
                || integerConstant.Value < 0
                || integerConstant.Value > int.MaxValue)
            {
                return false;
            }

            constantIndex = (int)integerConstant.Value;
            return true;
        }

        private MidLevelIrOperand? TryCreateDynamicFixedArrayBaseAddress(MidLevelIrOperand source)
        {
            if (source.Type.Kind != StarkTypeKind.FixedArray)
            {
                return null;
            }

            var directAddress = source switch
            {
                MidLevelIrLocalOperand local => CreateAddressOfLocal(local.Name, local.Type),
                MidLevelIrParameterOperand parameter => CreateAddressOfParameter(parameter.Name, parameter.Type),
                MidLevelIrGlobalOperand global => CreateAddressOfGlobal(global.Name, global.Type),
                _ => null
            };
            if (directAddress is not null)
            {
                return directAddress;
            }

            // Spill non-addressable fixed-array temporaries so dynamic indexing can still
            // lower through address-based element access.
            var spilled = EmitTemporary(new MidLevelIrUseRValue(source), "indexbase");
            return spilled is MidLevelIrLocalOperand localSpill
                ? CreateAddressOfLocal(localSpill.Name, localSpill.Type)
                : null;
        }

        private MidLevelIrOperand? LowerImportedTypedTemplateLiteral(
            ImportedTemplateTypedBodyExpressionSummary expression)
        {
            if (expression.LiteralText is null || expression.Type is null)
            {
                return null;
            }

            return CreateLiteralOperand(expression.LiteralText, ApplyGenericSubstitution(expression.Type));
        }

        private MidLevelIrOperand? LowerImportedTypedTemplateConversion(
            ImportedTemplateTypedBodyExpressionSummary expression,
            StarkTypeSymbol? expectedType)
        {
            if (expression.Type is not { } publishedType
                || expression.Args.Count != 1)
            {
                return null;
            }

            var operand = LowerImportedTypedTemplateExpression(expression.Args[0], expectedType: null);
            if (operand is null)
            {
                return null;
            }

            var targetType = ApplyGenericSubstitution(publishedType);
            var converted = CoerceOperand(operand, targetType);
            return expectedType is null ? converted : CoerceOperand(converted, expectedType);
        }

        private MidLevelIrOperand? LowerImportedTypedTemplateUnary(
            ImportedTemplateTypedBodyExpressionSummary expression,
            StarkTypeSymbol? expectedType)
        {
            if (expression.Name is not { } operatorText
                || expression.Args.Count != 1)
            {
                return null;
            }

            var text = RenderImportedTypedTemplateExpression(expression);
            if (operatorText == "&")
            {
                var address = LowerImportedTypedTemplateAddressOfUnary(expression.Args[0]);
                return expectedType is null ? address : CoerceOperand(address, expectedType);
            }

            var operand = LowerImportedTypedTemplateExpression(expression.Args[0], expectedType: null);
            if (operand is null)
            {
                return null;
            }

            MidLevelIrOperand? result = operatorText switch
            {
                "+" => operand,
                "-" => EmitTemporary(
                    new MidLevelIrUnaryRValue(MidLevelIrUnaryOperator.Negate, operand, operand.Type, text),
                    "neg"),
                "-%" => EmitTemporary(
                    new MidLevelIrBinaryRValue(
                        MidLevelIrBinaryOperator.WrappingSubtract,
                        new MidLevelIrIntegerConstantOperand(BigInteger.Zero, operand.Type),
                        operand,
                        operand.Type,
                        text),
                    "wrapneg"),
                "!" => EmitTemporary(
                    new MidLevelIrUnaryRValue(
                        MidLevelIrUnaryOperator.LogicalNot,
                        CoerceOperand(operand, StarkTypeSymbols.Bool) ?? operand,
                        StarkTypeSymbols.Bool,
                        text),
                    "not"),
                "~" => EmitTemporary(
                    new MidLevelIrUnaryRValue(MidLevelIrUnaryOperator.BitwiseNot, operand, operand.Type, text),
                    "bitnot"),
                "*" => LowerImportedTypedTemplateDereferenceUnary(operand, text),
                _ => null
            };

            return expectedType is null ? result : CoerceOperand(result, expectedType);
        }

        private MidLevelIrOperand? LowerImportedTypedTemplateAddressOfUnary(
            ImportedTemplateTypedBodyExpressionSummary operandExpression)
        {
            if (!TryResolveImportedTypedTemplateAssignmentTarget(operandExpression, out var target))
            {
                return null;
            }

            return BuildAddress(target);
        }

        private MidLevelIrOperand? LowerImportedTypedTemplateDereferenceUnary(MidLevelIrOperand operand, string text)
        {
            if (operand.Type.Kind != StarkTypeKind.RawPointer || operand.Type.ElementType is null)
            {
                return null;
            }

            return EmitTemporary(
                new MidLevelIrLoadIndirectRValue(
                    operand,
                    operand.Type.ElementType,
                    text),
                "load");
        }

        private MidLevelIrOperand? LowerImportedTypedTemplateBinary(
            ImportedTemplateTypedBodyExpressionSummary expression,
            StarkTypeSymbol? expectedType)
        {
            if (expression.Name is not { } operatorText
                || expression.Args.Count != 2)
            {
                return null;
            }

            if (operatorText is "&&" or "||")
            {
                return LowerImportedTypedTemplateShortCircuitBinary(expression, operatorText, expectedType);
            }

            var left = LowerImportedTypedTemplateExpression(expression.Args[0], expectedType: null);
            var right = LowerImportedTypedTemplateExpression(expression.Args[1], expectedType: null);
            if (left is null || right is null)
            {
                return null;
            }

            var text = RenderImportedTypedTemplateExpression(expression);
            MidLevelIrOperand? result;
            if (operatorText is "==" or "!=" or "<" or "<=" or ">" or ">=")
            {
                result = EmitPairComparison(left, right, operatorText, text);
            }
            else
            {
                var resultType = FindCommonType(left.Type, right.Type);
                if (resultType.Kind == StarkTypeKind.Error)
                {
                    MarkUnsupported();
                    return null;
                }

                if (operatorText is "&" or "^" or "|" or "<<" or ">>"
                    && resultType.Kind != StarkTypeKind.Integer)
                {
                    MarkUnsupported();
                    return null;
                }

                var coercedLeft = CoerceOperand(left, resultType);
                var coercedRight = CoerceOperand(right, resultType);
                if (coercedLeft is null || coercedRight is null)
                {
                    return null;
                }

                result = EmitTemporary(
                    new MidLevelIrBinaryRValue(
                        MapBinaryOperator(operatorText),
                        coercedLeft,
                        coercedRight,
                        resultType,
                        text),
                    "bin");
            }

            return expectedType is null ? result : CoerceOperand(result, expectedType);
        }

        private MidLevelIrOperand? LowerImportedTypedTemplateComparisonChain(
            ImportedTemplateTypedBodyExpressionSummary expression,
            StarkTypeSymbol? expectedType)
        {
            if (expression.Args.Count < 2 || expression.Operators.Count != expression.Args.Count - 1)
            {
                return null;
            }

            var left = LowerImportedTypedTemplateExpression(expression.Args[0], expectedType: null);
            if (left is null)
            {
                return null;
            }

            if (expression.Operators.Count == 1)
            {
                var right = LowerImportedTypedTemplateExpression(expression.Args[1], expectedType: null);
                if (right is null)
                {
                    return null;
                }

                var comparison = EmitPairComparison(left, right, expression.Operators[0], RenderImportedTypedTemplateExpression(expression));
                return expectedType is null ? comparison : CoerceOperand(comparison, expectedType);
            }

            var result = CreateTemporaryLocal(StarkTypeSymbols.Bool, "typed_cmpchain");
            var joinBlock = CreateBlock("typed_cmpchain_join");
            var currentLeft = left;

            for (var index = 0; index < expression.Operators.Count; index++)
            {
                var right = LowerImportedTypedTemplateExpression(expression.Args[index + 1], expectedType: null);
                if (right is null)
                {
                    return null;
                }

                var comparisonText =
                    $"{RenderImportedTypedTemplateExpression(expression.Args[index])} {expression.Operators[index]} {RenderImportedTypedTemplateExpression(expression.Args[index + 1])}";
                var comparison = EmitPairComparison(currentLeft, right, expression.Operators[index], comparisonText);
                if (comparison is null)
                {
                    return null;
                }

                if (index == expression.Operators.Count - 1)
                {
                    EmitOperandAssignment(result, comparison, comparison.Text);
                    EnsureGoto(joinBlock.Id);
                    break;
                }

                var nextBlock = CreateBlock($"typed_cmpchain_next_{index + 1}");
                var falseBlock = CreateBlock($"typed_cmpchain_false_{index}");
                CurrentBlock.Terminator = new MidLevelIrTerminator(
                    MidLevelIrTerminatorKind.Branch,
                    [nextBlock.Id, falseBlock.Id],
                    ConditionText: comparison.Text,
                    Condition: comparison);

                CurrentBlock = falseBlock;
                EmitOperandAssignment(result, new MidLevelIrBoolConstantOperand(false), "false");
                EnsureGoto(joinBlock.Id);

                CurrentBlock = nextBlock;
                currentLeft = right;
            }

            CurrentBlock = joinBlock;
            return expectedType is null ? result : CoerceOperand(result, expectedType);
        }

        private MidLevelIrOperand? LowerImportedTypedTemplateShortCircuitBinary(
            ImportedTemplateTypedBodyExpressionSummary expression,
            string operatorText,
            StarkTypeSymbol? expectedType)
        {
            if (expression.Args.Count != 2)
            {
                return null;
            }

            var left = CoerceOperand(
                LowerImportedTypedTemplateExpression(expression.Args[0], expectedType: null),
                StarkTypeSymbols.Bool);
            if (left is null)
            {
                return null;
            }

            var result = CreateTemporaryLocal(StarkTypeSymbols.Bool, operatorText == "&&" ? "typed_and" : "typed_or");
            var shortCircuitBlock = CreateBlock(operatorText == "&&" ? "typed_and_short" : "typed_or_short");
            var rhsBlock = CreateBlock(operatorText == "&&" ? "typed_and_rhs" : "typed_or_rhs");
            var joinBlock = CreateBlock(operatorText == "&&" ? "typed_and_join" : "typed_or_join");

            CurrentBlock.Terminator = operatorText == "&&"
                ? new MidLevelIrTerminator(
                    MidLevelIrTerminatorKind.Branch,
                    [rhsBlock.Id, shortCircuitBlock.Id],
                    ConditionText: RenderImportedTypedTemplateExpression(expression.Args[0]),
                    Condition: left)
                : new MidLevelIrTerminator(
                    MidLevelIrTerminatorKind.Branch,
                    [shortCircuitBlock.Id, rhsBlock.Id],
                    ConditionText: RenderImportedTypedTemplateExpression(expression.Args[0]),
                    Condition: left);

            CurrentBlock = shortCircuitBlock;
            EmitOperandAssignment(
                result,
                new MidLevelIrBoolConstantOperand(operatorText == "||"),
                operatorText == "||" ? "true" : "false");
            EnsureGoto(joinBlock.Id);

            CurrentBlock = rhsBlock;
            var right = CoerceOperand(
                LowerImportedTypedTemplateExpression(expression.Args[1], expectedType: null),
                StarkTypeSymbols.Bool);
            if (right is null)
            {
                return null;
            }

            EmitOperandAssignment(result, right, RenderImportedTypedTemplateExpression(expression.Args[1]));
            EnsureGoto(joinBlock.Id);

            CurrentBlock = joinBlock;
            return expectedType is null ? result : CoerceOperand(result, expectedType);
        }

        private MidLevelIrOperand? LowerImportedTypedTemplateConditional(
            ImportedTemplateTypedBodyExpressionSummary expression,
            StarkTypeSymbol? expectedType)
        {
            if (expression.Args.Count != 3)
            {
                return null;
            }

            var condition = LowerImportedTypedTemplateExpression(expression.Args[0], StarkTypeSymbols.Bool);
            if (condition is null)
            {
                return null;
            }

            var thenBlock = CreateBlock("typed_cond_true");
            var elseBlock = CreateBlock("typed_cond_false");
            var joinBlock = CreateBlock("typed_cond_join");

            CurrentBlock.Terminator = new MidLevelIrTerminator(
                MidLevelIrTerminatorKind.Branch,
                [thenBlock.Id, elseBlock.Id],
                ConditionText: RenderImportedTypedTemplateExpression(expression.Args[0]),
                Condition: condition);

            CurrentBlock = thenBlock;
            var trueValue = LowerImportedTypedTemplateExpression(expression.Args[1], expectedType);
            var trueBlock = CurrentBlock;
            if (trueValue is null)
            {
                return null;
            }

            CurrentBlock = elseBlock;
            var falseValue = LowerImportedTypedTemplateExpression(expression.Args[2], expectedType);
            var falseBlock = CurrentBlock;
            if (falseValue is null)
            {
                return null;
            }

            var resultType = expectedType ?? FindCommonType(trueValue.Type, falseValue.Type);
            if (resultType.Kind == StarkTypeKind.Error)
            {
                MarkUnsupported();
                return null;
            }

            var result = CreateTemporaryLocal(resultType, "typed_cond");

            CurrentBlock = trueBlock;
            var coercedTrue = CoerceOperand(trueValue, resultType);
            if (coercedTrue is null)
            {
                return null;
            }

            EmitOperandAssignment(result, coercedTrue, RenderImportedTypedTemplateExpression(expression.Args[1]));
            EnsureGoto(joinBlock.Id);

            CurrentBlock = falseBlock;
            var coercedFalse = CoerceOperand(falseValue, resultType);
            if (coercedFalse is null)
            {
                return null;
            }

            EmitOperandAssignment(result, coercedFalse, RenderImportedTypedTemplateExpression(expression.Args[2]));
            EnsureGoto(joinBlock.Id);

            CurrentBlock = joinBlock;
            return result;
        }

        private bool TryBuildImportedTypedTemplateDirectCall(
            ImportedTemplateTypedBodyExpressionSummary expression,
            out MidLevelIrCallRValue call)
        {
            call = default!;

            if (expression.Ordinal is not { } ordinal
                || !_importedTemplateDirectCalls.TryGetValue(ordinal, out var publishedSignature))
            {
                return false;
            }

            var signature = ApplyGenericSubstitution(publishedSignature);
            var loweredArguments = new List<MidLevelIrOperand>(expression.Args.Count);
            for (var index = 0; index < expression.Args.Count; index++)
            {
                var parameterType = index < signature.Parameters.Count
                    ? signature.Parameters[index].Type
                    : null;
                var argument = LowerImportedTypedTemplateExpression(expression.Args[index], parameterType);
                if (argument is null)
                {
                    return false;
                }

                loweredArguments.Add(argument);
            }

            return TryBuildCall(
                signature.Name,
                signature,
                receiver: null,
                text: RenderImportedTypedTemplateExpression(expression),
                out call,
                loweredExplicitArguments: loweredArguments);
        }

        private bool TryBuildImportedTypedTemplateMemberCall(
            ImportedTemplateTypedBodyExpressionSummary expression,
            out MidLevelIrCallRValue call)
        {
            call = default!;

            if (expression.Ordinal is not { } ordinal
                || expression.Args.Count == 0
                || !_importedTemplateMemberCalls.TryGetValue(ordinal, out var publishedSignature))
            {
                return false;
            }

            var receiver = LowerImportedTypedTemplateExpression(expression.Args[0], expectedType: null);
            if (receiver is null)
            {
                return false;
            }

            var signature = ApplyGenericSubstitution(publishedSignature);
            var loweredArguments = new List<MidLevelIrOperand>(expression.Args.Count - 1);
            for (var index = 1; index < expression.Args.Count; index++)
            {
                var parameterType = index < signature.Parameters.Count
                    ? signature.Parameters[index].Type
                    : null;
                var argument = LowerImportedTypedTemplateExpression(expression.Args[index], parameterType);
                if (argument is null)
                {
                    return false;
                }

                loweredArguments.Add(argument);
            }

            return TryBuildCall(
                signature.Name,
                signature,
                receiver,
                text: RenderImportedTypedTemplateExpression(expression),
                out call,
                loweredExplicitArguments: loweredArguments);
        }

        private MidLevelIrOperand? LowerImportedTypedTemplateObjectCreation(
            ImportedTemplateTypedBodyExpressionSummary expression)
        {
            if (expression.Ordinal is not { } ordinal
                || _importedTemplateSummary is not { ObjectCreations.Count: > 0 } importedTemplateSummary
                || ordinal < 0
                || ordinal >= importedTemplateSummary.ObjectCreations.Count)
            {
                return null;
            }

            var publishedObjectCreation = importedTemplateSummary.ObjectCreations[ordinal];
            var createdType = ApplyGenericSubstitution(publishedObjectCreation.CreatedType);
            MidLevelIrOperand current = new MidLevelIrZeroInitializerOperand(createdType);
            var argumentOffset = 0;

            if (publishedObjectCreation.Constructor is { } constructor)
            {
                current = LowerImportedTypedTemplatePrimaryConstructorObjectCreation(
                    createdType,
                    constructor,
                    expression.Args.Take(constructor.Parameters.Count).ToArray());
                if (current is null)
                {
                    return null;
                }

                argumentOffset = constructor.Parameters.Count;
            }

            if (publishedObjectCreation.InitializerMembers.Count != expression.Args.Count - argumentOffset)
            {
                return publishedObjectCreation.InitializerMembers.Count == 0 && expression.Args.Count == argumentOffset
                    ? current
                    : null;
            }

            if (publishedObjectCreation.InitializerMembers.Count == 0)
            {
                return current;
            }

            return LowerImportedTypedTemplateObjectInitializer(
                createdType,
                current,
                publishedObjectCreation.InitializerMembers,
                expression.Args.Skip(argumentOffset).ToArray());
        }

        private MidLevelIrOperand? LowerImportedTypedTemplateEnumCall(
            ImportedTemplateTypedBodyExpressionSummary expression)
        {
            if (expression.Ordinal is not { } ordinal
                || !_importedTemplateEnumCalls.TryGetValue(ordinal, out var publishedEnumCall))
            {
                return null;
            }

            var publishedEnumType = ApplyGenericSubstitution(publishedEnumCall.EnumType);
            var publishedCaseName = $"{publishedEnumType.DisplayName}.{publishedEnumCall.VariantName}";
            if (!TryResolveEnumCaseReference(publishedCaseName, out var enumType, out var layout, out var variant)
                || variant.UsesNamedFields
                || variant.Fields.Count != expression.Args.Count)
            {
                MarkUnsupported();
                return null;
            }

            var loweredArguments = new MidLevelIrOperand[variant.Fields.Count];
            for (var index = 0; index < variant.Fields.Count; index++)
            {
                var field = variant.Fields[index];
                var argument = LowerImportedTypedTemplateExpression(expression.Args[index], field.Type);
                if (argument is null)
                {
                    return null;
                }

                var coerced = CoerceOperand(argument, field.Type);
                if (coerced is null)
                {
                    return null;
                }

                loweredArguments[index] = coerced;
            }

            return LowerDirectTagEnumConstructor(enumType, layout, variant, loweredArguments, RenderImportedTypedTemplateExpression(expression));
        }

        private MidLevelIrOperand? LowerImportedTypedTemplateEnumConstructor(
            ImportedTemplateTypedBodyExpressionSummary expression)
        {
            if (expression.Ordinal is not { } ordinal
                || !_importedTemplateEnumConstructors.TryGetValue(ordinal, out var publishedEnumConstructor))
            {
                return null;
            }

            var enumType = ApplyGenericSubstitution(publishedEnumConstructor.EnumType);
            if (!TryGetEnumLayout(enumType, out var layout)
                || !layout.TryGetVariant(publishedEnumConstructor.VariantName, out var variant)
                || !variant.UsesNamedFields
                || publishedEnumConstructor.Members.Count != expression.Args.Count)
            {
                MarkUnsupported();
                return null;
            }

            var orderedValues = new MidLevelIrOperand[variant.Fields.Count];
            var assigned = new bool[variant.Fields.Count];

            for (var memberOrdinal = 0; memberOrdinal < publishedEnumConstructor.Members.Count; memberOrdinal++)
            {
                var publishedMember = publishedEnumConstructor.Members[memberOrdinal];
                if (publishedMember.FieldIndex < 0
                    || publishedMember.FieldIndex >= variant.Fields.Count)
                {
                    MarkUnsupported();
                    return null;
                }

                var layoutField = variant.Fields[publishedMember.FieldIndex];
                var value = LowerImportedTypedTemplateExpression(expression.Args[memberOrdinal], ApplyGenericSubstitution(publishedMember.FieldType));
                if (value is null)
                {
                    return null;
                }

                var coerced = CoerceOperand(value, layoutField.Type);
                if (coerced is null)
                {
                    return null;
                }

                orderedValues[publishedMember.FieldIndex] = coerced;
                assigned[publishedMember.FieldIndex] = true;
            }

            if (assigned.Any(static value => !value))
            {
                MarkUnsupported();
                return null;
            }

            return LowerDirectTagEnumConstructor(enumType, layout, variant, orderedValues, RenderImportedTypedTemplateExpression(expression));
        }

        private MidLevelIrOperand? LowerImportedTypedTemplateEnumValue(
            ImportedTemplateTypedBodyExpressionSummary expression)
        {
            if (expression.Ordinal is not { } ordinal
                || !_importedTemplateEnumValues.TryGetValue(ordinal, out var publishedEnumValue)
                || expression.Args.Count != 0)
            {
                return null;
            }

            var publishedEnumType = ApplyGenericSubstitution(publishedEnumValue.EnumType);
            var publishedCaseName = $"{publishedEnumType.DisplayName}.{publishedEnumValue.VariantName}";
            if (!TryResolveEnumCaseReference(publishedCaseName, out var enumType, out var layout, out var variant)
                || variant.Fields.Count != 0)
            {
                MarkUnsupported();
                return null;
            }

            return LowerDirectTagEnumConstructor(enumType, layout, variant, [], publishedCaseName);
        }

        private MidLevelIrOperand? LowerImportedTypedTemplatePrimaryConstructorObjectCreation(
            StarkTypeSymbol createdType,
            TypedConstructorShape constructor,
            IReadOnlyList<ImportedTemplateTypedBodyExpressionSummary> arguments)
        {
            if (createdType.Kind != StarkTypeKind.Named
                || createdType.NamedType is null
                || !_typeModel.NamedTypes.TryGetValue(createdType.NamedType, out var namedType)
                || !constructor.IsPrimaryShape
                || constructor.Parameters.Count != arguments.Count)
            {
                MarkUnsupported();
                return null;
            }

            MidLevelIrOperand current = new MidLevelIrZeroInitializerOperand(createdType);
            for (var index = 0; index < constructor.Parameters.Count; index++)
            {
                var parameter = constructor.Parameters[index];
                if (!namedType.TryGetField(parameter.Name, out var field, out var fieldIndex))
                {
                    MarkUnsupported();
                    return null;
                }

                var loweredArgument = LowerImportedTypedTemplateExpression(arguments[index], ApplyGenericSubstitution(parameter.Type));
                if (loweredArgument is null)
                {
                    return null;
                }

                var fieldValue = CoerceOperand(loweredArgument, ApplyGenericSubstitution(field.Type));
                if (fieldValue is null)
                {
                    return null;
                }

                var updated = EmitTemporary(
                    new MidLevelIrInsertFieldRValue(
                        current,
                        field.Name,
                        fieldIndex,
                        fieldValue,
                        createdType,
                        $"{current.Text}.{field.Name} = {RenderImportedTypedTemplateExpression(arguments[index])}"),
                    "insertfield");
                if (updated is null)
                {
                    return null;
                }

                current = updated;
            }

            return current;
        }

        private MidLevelIrOperand? LowerImportedTypedTemplateObjectInitializer(
            StarkTypeSymbol targetType,
            MidLevelIrOperand seed,
            IReadOnlyList<ImportedTemplateObjectInitializerMemberSummary> initializerMembers,
            IReadOnlyList<ImportedTemplateTypedBodyExpressionSummary> arguments)
        {
            if (initializerMembers.Count != arguments.Count)
            {
                return null;
            }

            var current = seed;
            for (var index = 0; index < initializerMembers.Count; index++)
            {
                var publishedMember = initializerMembers[index];
                var fieldType = ApplyGenericSubstitution(publishedMember.FieldType);
                var value = LowerImportedTypedTemplateExpression(arguments[index], fieldType);
                if (value is null)
                {
                    return null;
                }

                var updated = EmitTemporary(
                    new MidLevelIrInsertFieldRValue(
                        current,
                        publishedMember.FieldName,
                        publishedMember.FieldIndex,
                        value,
                        targetType,
                        $"{current.Text}.{publishedMember.FieldName} = {RenderImportedTypedTemplateExpression(arguments[index])}"),
                    "insertfield");
                if (updated is null)
                {
                    return null;
                }

                current = updated;
            }

            return current;
        }

        private static string RenderImportedTypedTemplateExpression(ImportedTemplateTypedBodyExpressionSummary expression)
        {
            return expression.Kind switch
            {
                ImportedTemplateTypedBodyExpressionKind.NameReference => expression.Name ?? string.Empty,
                ImportedTemplateTypedBodyExpressionKind.Literal => expression.LiteralText ?? string.Empty,
                ImportedTemplateTypedBodyExpressionKind.ArrayInitializer => expression.Args.Count == 0
                    ? "{}"
                    : $"{{ {string.Join(", ", expression.Args.Select(RenderImportedTypedTemplateExpression))} }}",
                ImportedTemplateTypedBodyExpressionKind.ObjectInitializer => RenderImportedTypedTemplateObjectInitializer(expression),
                ImportedTemplateTypedBodyExpressionKind.Assignment => RenderImportedTypedTemplateAssignmentExpression(expression),
                ImportedTemplateTypedBodyExpressionKind.Conversion => expression.Type is { } conversionType
                    && expression.Args.Count == 1
                    ? $"({conversionType.DisplayName}){RenderImportedTypedTemplateExpression(expression.Args[0])}"
                    : "conversion",
                ImportedTemplateTypedBodyExpressionKind.UnaryOperation => expression.Name is { } unaryOperator
                    && expression.Args.Count == 1
                    ? $"{unaryOperator}{RenderImportedTypedTemplateExpression(expression.Args[0])}"
                    : "unary",
                ImportedTemplateTypedBodyExpressionKind.BinaryOperation => expression.Name is { } binaryOperator
                    && expression.Args.Count == 2
                    ? $"{RenderImportedTypedTemplateExpression(expression.Args[0])} {binaryOperator} {RenderImportedTypedTemplateExpression(expression.Args[1])}"
                    : "binary",
                ImportedTemplateTypedBodyExpressionKind.ComparisonChain => RenderImportedTypedTemplateComparisonChain(expression),
                ImportedTemplateTypedBodyExpressionKind.Conditional => expression.Args.Count == 3
                    ? $"{RenderImportedTypedTemplateExpression(expression.Args[0])} ? {RenderImportedTypedTemplateExpression(expression.Args[1])} : {RenderImportedTypedTemplateExpression(expression.Args[2])}"
                    : "conditional",
                ImportedTemplateTypedBodyExpressionKind.ObjectCreation => $"new #{expression.Ordinal}({string.Join(", ", expression.Args.Select(RenderImportedTypedTemplateExpression))})",
                ImportedTemplateTypedBodyExpressionKind.EnumConstructor => $"enumctor#{expression.Ordinal}({string.Join(", ", expression.Args.Select(RenderImportedTypedTemplateExpression))})",
                ImportedTemplateTypedBodyExpressionKind.EnumCall => $"enumcall#{expression.Ordinal}({string.Join(", ", expression.Args.Select(RenderImportedTypedTemplateExpression))})",
                ImportedTemplateTypedBodyExpressionKind.EnumValue => $"enumvalue#{expression.Ordinal}",
                ImportedTemplateTypedBodyExpressionKind.DirectCall => $"{expression.Ordinal}({string.Join(", ", expression.Args.Select(RenderImportedTypedTemplateExpression))})",
                ImportedTemplateTypedBodyExpressionKind.IndexAccess => expression.Args.Count >= 1
                    ? $"{RenderImportedTypedTemplateExpression(expression.Args[0])}[{string.Join(", ", expression.Args.Skip(1).Select(RenderImportedTypedTemplateExpression))}]"
                    : "index",
                ImportedTemplateTypedBodyExpressionKind.FieldAccess => $"{RenderImportedTypedTemplateExpression(expression.Args[0])}.{expression.Ordinal}",
                ImportedTemplateTypedBodyExpressionKind.MemberCall => $"{RenderImportedTypedTemplateExpression(expression.Args[0])}.{expression.Ordinal}({string.Join(", ", expression.Args.Skip(1).Select(RenderImportedTypedTemplateExpression))})",
                _ => string.Empty
            };
        }

        private static string RenderImportedTypedTemplateObjectInitializer(ImportedTemplateTypedBodyExpressionSummary expression)
        {
            if (expression.Members.Count != expression.Args.Count)
            {
                return "objectinit";
            }

            var parts = new string[expression.Members.Count];
            for (var index = 0; index < expression.Members.Count; index++)
            {
                parts[index] = $"{expression.Members[index]} = {RenderImportedTypedTemplateExpression(expression.Args[index])}";
            }

            return $"{{ {string.Join(", ", parts)} }}";
        }

        private static string RenderImportedTypedTemplateAssignmentExpression(ImportedTemplateTypedBodyExpressionSummary expression)
        {
            if (expression.Args.Count != 1)
            {
                return "assignment";
            }

            var targetText = expression.TargetExpression is not null
                ? RenderImportedTypedTemplateExpression(expression.TargetExpression)
                : expression.Name;
            if (string.IsNullOrEmpty(targetText))
            {
                return "assignment";
            }

            var assignmentOperator = string.IsNullOrEmpty(expression.AssignmentOperator)
                ? "="
                : expression.AssignmentOperator;
            return $"{targetText} {assignmentOperator} {RenderImportedTypedTemplateExpression(expression.Args[0])}";
        }

        private static string RenderImportedTypedTemplateComparisonChain(ImportedTemplateTypedBodyExpressionSummary expression)
        {
            if (expression.Args.Count < 2 || expression.Operators.Count != expression.Args.Count - 1)
            {
                return "cmpchain";
            }

            var builder = new StringBuilder(RenderImportedTypedTemplateExpression(expression.Args[0]));
            for (var index = 0; index < expression.Operators.Count; index++)
            {
                builder.Append(' ');
                builder.Append(expression.Operators[index]);
                builder.Append(' ');
                builder.Append(RenderImportedTypedTemplateExpression(expression.Args[index + 1]));
            }

            return builder.ToString();
        }

        private void LowerBlock(StarkParser.BlockContext block)
        {
            _scopes.Push(new ScopeFrame());

            foreach (var statement in block.statement())
            {
                LowerStatement(statement);
            }

            var scope = _scopes.Pop();
            EmitStorageDead(scope);
        }

        private void LowerStatement(StarkParser.StatementContext statement)
        {
            var previousStatementLocation = _currentStatementLocation;
            _currentStatementLocation = CreateSourceLocation(statement.Start) ?? _functionLocation;

            try
            {
            if (CurrentBlock.HasTerminator)
            {
                CurrentBlock = CreateBlock("dead");
            }

            if (statement.block() is { } block)
            {
                LowerBlock(block);
                return;
            }

            if (statement.localConstantDeclaration() is { } localConstant)
            {
                LowerConstantDeclaration(localConstant);
                return;
            }

            if (statement.localVariableDeclaration() is { } localVariable)
            {
                LowerVariableDeclaration(localVariable);
                return;
            }

            if (statement.ifStatement() is { } ifStatement)
            {
                LowerIf(ifStatement);
                return;
            }

            if (statement.switchStatement() is { } switchStatement)
            {
                LowerSwitch(switchStatement);
                return;
            }

            if (statement.whileStatement() is { } whileStatement)
            {
                LowerWhile(whileStatement);
                return;
            }

            if (statement.forStatement() is { } forStatement)
            {
                LowerFor(forStatement);
                return;
            }

            if (statement.returnStatement() is { } returnStatement)
            {
                LowerReturn(returnStatement);
                return;
            }

            if (statement.breakStatement() is not null)
            {
                if (_breakTargets.Count == 0)
                {
                    MarkUnsupported(statement.breakStatement(), "'break' requires an enclosing loop or switch.");
                    return;
                }

                var breakTarget = _breakTargets.Peek();
                EmitStorageDeadBeyondDepth(breakTarget.ScopeDepth);
                CurrentBlock.Terminator = new MidLevelIrTerminator(MidLevelIrTerminatorKind.Goto, [breakTarget.Target]);
                return;
            }

            if (statement.continueStatement() is not null)
            {
                if (_loops.Count == 0)
                {
                    MarkUnsupported(statement.continueStatement(), "'continue' requires an enclosing loop.");
                    return;
                }

                var loop = _loops.Peek();
                EmitStorageDeadBeyondDepth(loop.ScopeDepth);
                CurrentBlock.Terminator = new MidLevelIrTerminator(MidLevelIrTerminatorKind.Goto, [loop.ContinueTarget]);
                return;
            }

            if (statement.expressionStatement() is { } expressionStatement)
            {
                LowerExpressionStatement(expressionStatement.expression());
            }
            }
            finally
            {
                _currentStatementLocation = previousStatementLocation;
            }
        }

        private void LowerConstantDeclaration(StarkParser.LocalConstantDeclarationContext declaration)
        {
            var declaredType = TryResolvePublishedLocalDeclarationType(TemplateLocalDeclarationFacts.ConstantKind, declaration, out var publishedType)
                ? publishedType
                : ResolveTypeWithGenericSubstitution(declaration.type_(), CurrentModuleName);
            foreach (var declarator in declaration.constantDeclarators().constantDeclarator())
            {
                var name = declarator.Identifier().GetText();
                RegisterLocal(name, declaredType, storageClass: "local", isMutable: false, isConstant: true);
                TrackDeclaredLocal(name, declaredType);
                Emit(MidLevelIrStatementKind.StorageLive, name, name, declaredType);
                LowerVariableInitializer(name, declaredType, declarator.variableInitializer());
                InitializeRuntimeDropState(name, declaredType, isActive: true);
            }
        }

        private void LowerVariableDeclaration(StarkParser.LocalVariableDeclarationContext declaration)
        {
            var declaredType = TryResolvePublishedLocalDeclarationType(TemplateLocalDeclarationFacts.VariableKind, declaration, out var publishedType)
                ? publishedType
                : ResolveTypeWithGenericSubstitution(declaration.type_(), CurrentModuleName);
            var storageClass = declaration.storageClass().GetText();

            foreach (var declarator in declaration.variableDeclarators().variableDeclarator())
            {
                var name = declarator.Identifier().GetText();
                RegisterLocal(name, declaredType, storageClass, declaration.MUT() is not null, isConstant: false);
                TrackDeclaredLocal(name, declaredType);
                Emit(MidLevelIrStatementKind.StorageLive, name, name, declaredType);
                InitializeRuntimeDropState(name, declaredType, isActive: false);

                if (declarator.variableInitializer() is { } initializer)
                {
                    LowerVariableInitializer(name, declaredType, initializer);
                    SetRuntimeDropState(name, isActive: true);
                }
            }
        }

        private void LowerVariableInitializer(string name, StarkTypeSymbol declaredType, StarkParser.VariableInitializerContext initializer)
        {
            if (initializer.expression() is { } expression)
            {
                EmitAssignmentFromExpression(name, declaredType, expression, expression.GetText());
                return;
            }

            if (initializer.objectInitializer() is { } objectInitializer)
            {
                var value = LowerObjectInitializer(declaredType, objectInitializer);
                if (value is null)
                {
                    MarkUnsupported(initializer, "Object initializer lowered without a materialized MIR value.");
                    Emit(MidLevelIrStatementKind.Assign, $"{name} = {FormatInitializer(initializer)}", name, declaredType);
                    return;
                }

                Emit(MidLevelIrStatementKind.Assign, $"{name} = {FormatInitializer(initializer)}", name, declaredType, new MidLevelIrUseRValue(value));
                return;
            }

            if (initializer.arrayInitializer() is { } arrayInitializer)
            {
                var value = LowerArrayInitializer(declaredType, arrayInitializer);
                if (value is null)
                {
                    MarkUnsupported(initializer, "Array initializer lowered without a materialized MIR value.");
                    Emit(MidLevelIrStatementKind.Assign, $"{name} = {FormatInitializer(initializer)}", name, declaredType);
                    return;
                }

                Emit(MidLevelIrStatementKind.Assign, $"{name} = {FormatInitializer(initializer)}", name, declaredType, new MidLevelIrUseRValue(value));
                return;
            }

            MarkUnsupported(initializer, "Unsupported variable initializer shape.");
            Emit(MidLevelIrStatementKind.Assign, $"{name} = {FormatInitializer(initializer)}", name, declaredType);
        }

        private MidLevelIrOperand? LowerInitializerToOperand(StarkParser.VariableInitializerContext initializer, StarkTypeSymbol targetType)
        {
            if (initializer.expression() is { } expression)
            {
                return LowerExpressionToOperand(expression, targetType);
            }

            if (initializer.objectInitializer() is { } objectInitializer)
            {
                return LowerObjectInitializer(targetType, objectInitializer);
            }

            if (initializer.arrayInitializer() is { } arrayInitializer)
            {
                return LowerArrayInitializer(targetType, arrayInitializer);
            }

            MarkUnsupported();
            return null;
        }

        private void LowerReturn(StarkParser.ReturnStatementContext returnStatement)
        {
            if (returnStatement.expression() is null)
            {
                EmitStorageDeadBeyondDepth(0);
                CurrentBlock.Terminator = new MidLevelIrTerminator(
                    MidLevelIrTerminatorKind.Return,
                    Targets: [],
                    ValueText: null,
                    Value: null);
                return;
            }

            var operand = LowerExpressionToOperand(returnStatement.expression(), _function.Signature.ReturnType);
            RecordMoveFromOperand(operand, _function.Signature.ReturnType);
            EmitStorageDeadBeyondDepth(0);
            CurrentBlock.Terminator = new MidLevelIrTerminator(
                MidLevelIrTerminatorKind.Return,
                Targets: [],
                ValueText: returnStatement.expression().GetText(),
                Value: operand);
        }

        private void LowerExpressionStatement(StarkParser.ExpressionContext expression)
        {
            if (TryLowerExpressionStatementCore(expression))
            {
                return;
            }

            MarkUnsupported(expression, "Expression statement could not be lowered to an assignment, rvalue, or operand.");
            Emit(MidLevelIrStatementKind.Evaluate, expression.GetText());
        }

        private bool TryLowerExpressionStatementCore(StarkParser.ExpressionContext expression)
        {
            if (TryLowerAssignmentExpression(expression.assignmentExpression(), out var assignment))
            {
                EmitAssignment(assignment);
                return true;
            }

            if (TryLowerConditionalCallStatement(expression))
            {
                return true;
            }

            if (TryLowerExpressionAsRValue(expression, out var value))
            {
                Emit(MidLevelIrStatementKind.Evaluate, expression.GetText(), value: value);
                return true;
            }

            if (LowerExpressionToOperand(expression) is { } operand)
            {
                Emit(MidLevelIrStatementKind.Evaluate, expression.GetText(), value: new MidLevelIrUseRValue(operand));
                return true;
            }

            return false;
        }

        private bool TryLowerConditionalCallStatement(StarkParser.ExpressionContext expression)
        {
            if (!TryGetTernaryConditionalExpression(expression, out var conditionalExpression)
                || !CanLowerConditionalCallStatementBranch(conditionalExpression.expression(0))
                || !CanLowerConditionalCallStatementBranch(conditionalExpression.expression(1)))
            {
                return false;
            }

            var condition = LowerLogicalOrExpression(conditionalExpression.logicalOrExpression(), StarkTypeSymbols.Bool);
            if (condition is null)
            {
                return false;
            }

            var thenBlock = CreateBlock("cond_true");
            var elseBlock = CreateBlock("cond_false");
            var joinBlock = CreateBlock("cond_join");

            CurrentBlock.Terminator = new MidLevelIrTerminator(
                MidLevelIrTerminatorKind.Branch,
                [thenBlock.Id, elseBlock.Id],
                ConditionText: conditionalExpression.logicalOrExpression().GetText(),
                Condition: condition);

            CurrentBlock = thenBlock;
            if (!TryLowerConditionalCallStatementBranch(conditionalExpression.expression(0)))
            {
                return false;
            }

            EnsureGoto(joinBlock.Id);

            CurrentBlock = elseBlock;
            if (!TryLowerConditionalCallStatementBranch(conditionalExpression.expression(1)))
            {
                return false;
            }

            EnsureGoto(joinBlock.Id);

            CurrentBlock = joinBlock;
            return true;
        }

        private bool TryLowerConditionalCallStatementBranch(StarkParser.ExpressionContext expression)
        {
            if (TryLowerExpressionAsRValue(expression, out var value))
            {
                Emit(MidLevelIrStatementKind.Evaluate, expression.GetText(), value: value);
                return true;
            }

            return TryLowerConditionalCallStatement(expression);
        }

        private static bool CanLowerConditionalCallStatementBranch(StarkParser.ExpressionContext expression)
        {
            if (TryGetSimplePostfixExpression(expression) is { } postfixExpression
                && postfixExpression.postfixPart().Length > 0
                && postfixExpression.postfixPart()[^1].argumentList() is not null)
            {
                return true;
            }

            return TryGetTernaryConditionalExpression(expression, out var conditionalExpression)
                && CanLowerConditionalCallStatementBranch(conditionalExpression.expression(0))
                && CanLowerConditionalCallStatementBranch(conditionalExpression.expression(1));
        }

        private static bool TryGetTernaryConditionalExpression(
            StarkParser.ExpressionContext expression,
            [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out StarkParser.ConditionalExpressionContext? conditionalExpression)
        {
            conditionalExpression = null;
            var assignmentExpression = expression.assignmentExpression();
            if (assignmentExpression.assignmentOperator() is not null
                || assignmentExpression.conditionalExpression() is not { } conditional
                || conditional.expression().Length != 2)
            {
                return false;
            }

            conditionalExpression = conditional;
            return true;
        }

        private bool TryLowerAssignmentExpression(
            StarkParser.AssignmentExpressionContext expression,
            out LoweredAssignment assignment)
        {
            assignment = default!;

            if (expression.assignmentOperator() is null)
            {
                return false;
            }

            if (TryResolveIndirectPointerAssignmentTarget(expression.unaryExpression(), out var pointerAddress, out var pointeeType))
            {
                assignment = LowerIndirectPointerAssignment(expression, pointerAddress, pointeeType);
                return true;
            }

            if (!TryResolveAssignmentTarget(expression.unaryExpression(), out var target))
            {
                return false;
            }

            var assignmentText = $"{expression.unaryExpression().GetText()} {expression.assignmentOperator().GetText()} {expression.assignmentExpression().GetText()}";

            if (expression.assignmentOperator().GetText() == "=")
            {
                var assignedValue = LowerAssignmentExpressionToOperand(expression.assignmentExpression(), target.Type);
                if (assignedValue is null)
                {
                    MarkUnsupported();
                    return true;
                }

                assignment = BuildAssignment(target, assignedValue, assignmentText);
                return true;
            }

            var currentValue = ReadPlace(target);
            var right = LowerAssignmentExpressionToOperand(expression.assignmentExpression(), currentValue.Type);
            if (right is null)
            {
                MarkUnsupported();
                return true;
            }

            var @operator = MapAssignmentOperator(expression.assignmentOperator().GetText());

            var commonType = FindCommonType(currentValue.Type, right.Type);
            var leftValue = CoerceOperand(currentValue, commonType);
            var rightValue = CoerceOperand(right, commonType);
            if (leftValue is null || rightValue is null)
            {
                MarkUnsupported();
                return true;
            }

            var temp = EmitTemporary(
                new MidLevelIrBinaryRValue(@operator, leftValue, rightValue, commonType, assignmentText),
                "compound");

            assignment = temp is null
                ? default!
                : BuildAssignment(target, CoerceOperand(temp, target.Type) ?? temp, assignmentText);
            if (temp is null)
            {
                MarkUnsupported();
            }

            return true;
        }

        private LoweredAssignment LowerIndirectPointerAssignment(
            StarkParser.AssignmentExpressionContext expression,
            MidLevelIrOperand address,
            StarkTypeSymbol pointeeType)
        {
            var assignmentText = $"{expression.unaryExpression().GetText()} {expression.assignmentOperator().GetText()} {expression.assignmentExpression().GetText()}";

            if (expression.assignmentOperator().GetText() == "=")
            {
                var assignedValue = LowerAssignmentExpressionToOperand(expression.assignmentExpression(), pointeeType);
                if (assignedValue is null)
                {
                    MarkUnsupported();
                    return default;
                }

                return new LoweredAssignment(
                    assignmentText,
                    TargetName: null,
                    pointeeType,
                    DirectValue: null,
                    ResultValue: assignedValue,
                    Address: address,
                    ReplacesWholeValue: false);
            }

            var currentValue = EmitTemporary(
                new MidLevelIrLoadIndirectRValue(address, pointeeType, $"{address.Text}:load"),
                "load");
            if (currentValue is null)
            {
                MarkUnsupported();
                return default;
            }

            var right = LowerAssignmentExpressionToOperand(expression.assignmentExpression(), currentValue.Type);
            if (right is null)
            {
                MarkUnsupported();
                return default;
            }

            var @operator = MapAssignmentOperator(expression.assignmentOperator().GetText());

            var commonType = FindCommonType(currentValue.Type, right.Type);
            var leftValue = CoerceOperand(currentValue, commonType);
            var rightValue = CoerceOperand(right, commonType);
            if (leftValue is null || rightValue is null)
            {
                MarkUnsupported();
                return default;
            }

            var temp = EmitTemporary(
                new MidLevelIrBinaryRValue(@operator, leftValue, rightValue, commonType, assignmentText),
                "compound");
            if (temp is null)
            {
                MarkUnsupported();
                return default;
            }

            return new LoweredAssignment(
                assignmentText,
                TargetName: null,
                pointeeType,
                DirectValue: null,
                ResultValue: CoerceOperand(temp, pointeeType) ?? temp,
                Address: address,
                ReplacesWholeValue: false);
        }

        private bool TryResolveIndirectPointerAssignmentTarget(
            StarkParser.UnaryExpressionContext expression,
            out MidLevelIrOperand address,
            out StarkTypeSymbol pointeeType)
        {
            address = default!;
            pointeeType = StarkTypeSymbols.Error;

            if (expression.conversionType() is not null || expression.powerExpression() is not null)
            {
                return false;
            }

            if (!string.Equals(expression.unaryOperator()?.GetText(), "*", StringComparison.Ordinal))
            {
                return false;
            }

            var loweredAddress = LowerUnaryExpression(expression.unaryExpression(), expectedType: null);
            if (loweredAddress is null
                || loweredAddress.Type.Kind != StarkTypeKind.RawPointer
                || loweredAddress.Type.ElementType is null)
            {
                return false;
            }

            address = loweredAddress;
            pointeeType = loweredAddress.Type.ElementType;
            return true;
        }

        private void LowerIf(StarkParser.IfStatementContext ifStatement)
        {
            var thenBlock = CreateBlock("if_then");
            var elseBlock = ifStatement.statement().Length > 1 ? CreateBlock("if_else") : null;
            var joinBlock = CreateBlock("if_join");
            var condition = LowerExpressionToOperand(ifStatement.expression(), StarkTypeSymbols.Bool);

            CurrentBlock.Terminator = new MidLevelIrTerminator(
                MidLevelIrTerminatorKind.Branch,
                elseBlock is null ? [thenBlock.Id, joinBlock.Id] : [thenBlock.Id, elseBlock.Id],
                ConditionText: ifStatement.expression().GetText(),
                Condition: condition);

            CurrentBlock = thenBlock;
            LowerStatement(ifStatement.statement(0));
            EnsureGoto(joinBlock.Id);

            if (elseBlock is not null)
            {
                CurrentBlock = elseBlock;
                LowerStatement(ifStatement.statement(1));
                EnsureGoto(joinBlock.Id);
            }

            CurrentBlock = joinBlock;
        }

        private void LowerSwitch(StarkParser.SwitchStatementContext switchStatement)
        {
            var switchValue = LowerExpressionToOperand(switchStatement.expression());
            if (switchValue is null)
            {
                MarkUnsupported(switchStatement, "Switch expression could not be lowered.");
                return;
            }

            var lowered = switchValue.Type.Kind switch
            {
                StarkTypeKind.Integer or StarkTypeKind.Bool =>
                    TryLowerNativeSwitch(switchStatement, switchValue)
                    || TryLowerGuardedSwitch(switchStatement, switchValue),
                StarkTypeKind.Ascii or StarkTypeKind.Unicode =>
                    TryLowerPartitionedTextSwitch(switchStatement, switchValue)
                    || TryLowerGuardedSwitch(switchStatement, switchValue),
                _ => TryLowerGuardedSwitch(switchStatement, switchValue)
            };

            if (lowered)
            {
                return;
            }

            MarkUnsupported(switchStatement, "Switch shape is outside the current direct MIR lowering subset.");

            var exitBlock = CreateBlock("switch_exit");
            var sectionBlocks = switchStatement.switchSection()
                .Select((section, index) => (Section: section, Block: CreateBlock($"switch_case_{index}")))
                .ToArray();

            var cases = new List<MidLevelIrSwitchCase>();
            foreach (var (section, block) in sectionBlocks)
            {
                foreach (var label in section.switchLabel())
                {
                    var labelText = label.DEFAULT() is not null ? "default" : label.GetText();
                    cases.Add(new MidLevelIrSwitchCase(labelText, block.Id));
                }
            }

            CurrentBlock.Terminator = new MidLevelIrTerminator(
                MidLevelIrTerminatorKind.Switch,
                sectionBlocks.Select(static item => item.Block.Id).Append(exitBlock.Id).ToArray(),
                ConditionText: switchStatement.expression().GetText(),
                SwitchCases: cases);

            _breakTargets.Push(new BreakTargets(exitBlock.Id, _scopes.Count));
            try
            {
                foreach (var (section, block) in sectionBlocks)
                {
                    CurrentBlock = block;
                    foreach (var nested in section.statement())
                    {
                        LowerStatement(nested);
                    }

                    EnsureGoto(exitBlock.Id);
                }
            }
            finally
            {
                _breakTargets.Pop();
            }

            CurrentBlock = exitBlock;
        }

        private bool TryLowerNativeSwitch(
            StarkParser.SwitchStatementContext switchStatement,
            MidLevelIrOperand switchValue)
        {
            if (!TryParseLowerableSwitchSections(switchStatement, out var parsedSections, out var defaultSectionCount))
            {
                return false;
            }

            if (!CanUseNativeSwitchType(switchValue.Type) || defaultSectionCount > 1)
            {
                return false;
            }

            var allLabels = parsedSections
                .SelectMany(static section => section.Labels)
                .ToArray();

            if (allLabels.Any(static label => label.IsMatchAll && !label.IsDefault))
            {
                return false;
            }

            var nativeLabels = allLabels
                .Where(static label => !label.IsMatchAll)
                .ToArray();

            if (nativeLabels.Length == 0
                || nativeLabels.Any(static label => label.GuardExpression is not null || label.Literal is null || label.CaptureName is not null))
            {
                return false;
            }

            var sections = parsedSections
                .Select((section, index) => (section.Section, section.Labels, Block: CreateBlock($"switch_case_{index}")))
                .ToArray();
            var exitBlock = CreateBlock("switch_exit");
            var switchCases = new List<MidLevelIrSwitchCase>();
            int? defaultTarget = null;

            foreach (var section in sections)
            {
                foreach (var label in section.Labels)
                {
                    if (label.IsDefault)
                    {
                        defaultTarget ??= section.Block.Id;
                        continue;
                    }

                    if (label.GuardExpression is not null || label.Literal is null)
                    {
                        return false;
                    }

                    var matchValue = LowerSwitchCaseLiteral(label.Literal, switchValue.Type);
                    if (matchValue is null || !CanUseNativeSwitchCase(matchValue.Type, switchValue.Type))
                    {
                        return false;
                    }

                    switchCases.Add(new MidLevelIrSwitchCase(label.LabelText, section.Block.Id, matchValue));
                }
            }

            var resolvedDefaultTarget = defaultTarget ?? exitBlock.Id;
            if (switchCases.Count == 0)
            {
                return false;
            }

            var targets = switchCases
                .Select(static item => item.TargetBlockId)
                .Append(resolvedDefaultTarget)
                .Distinct()
                .ToArray();

            CurrentBlock.Terminator = new MidLevelIrTerminator(
                MidLevelIrTerminatorKind.Switch,
                targets,
                ConditionText: switchStatement.expression().GetText(),
                Condition: switchValue,
                SwitchCases: switchCases,
                DefaultTarget: resolvedDefaultTarget);

            _breakTargets.Push(new BreakTargets(exitBlock.Id, _scopes.Count));
            try
            {
                foreach (var section in sections)
                {
                    CurrentBlock = section.Block;
                    foreach (var nested in section.Section.statement())
                    {
                        LowerStatement(nested);
                    }

                    EnsureGoto(exitBlock.Id);
                }
            }
            finally
            {
                _breakTargets.Pop();
            }

            CurrentBlock = exitBlock;
            return true;
        }

        private bool TryLowerPartitionedTextSwitch(
            StarkParser.SwitchStatementContext switchStatement,
            MidLevelIrOperand switchValue)
        {
            if (!TryParseLowerableSwitchSections(switchStatement, out var parsedSections, out var defaultSectionCount))
            {
                return false;
            }

            if (!CanUsePartitionedTextSwitchType(switchValue.Type)
                || defaultSectionCount > 1)
            {
                return false;
            }

            var allLabels = parsedSections
                .SelectMany(static section => section.Labels)
                .ToArray();
            if (allLabels.Any(static label => label.IsMatchAll && !label.IsDefault))
            {
                return false;
            }

            var textLabels = allLabels
                .Where(static label => !label.IsDefault)
                .ToArray();
            if (textLabels.Length == 0
                || textLabels.Any(static label => label.GuardExpression is not null || label.CaptureName is not null || label.Literal is null))
            {
                return false;
            }

            var sections = parsedSections
                .Select((section, index) => (section.Section, section.Labels, Block: CreateBlock($"switch_case_{index}")))
                .ToArray();
            var exitBlock = CreateBlock("switch_exit");
            var defaultTarget = sections
                .Where(static section => section.Labels.Any(static label => label.IsDefault))
                .Select(static section => section.Block.Id)
                .FirstOrDefault(exitBlock.Id);

            if (!TryExtractTextSwitchComponents(switchValue, out var dataPointer, out var length))
            {
                return false;
            }

            var flattenedLabels = new List<PartitionedTextSwitchLabel>();
            var order = 0;
            foreach (var section in sections)
            {
                foreach (var label in section.Labels)
                {
                    if (label.IsDefault || label.Literal is null)
                    {
                        continue;
                    }

                    flattenedLabels.Add(new PartitionedTextSwitchLabel(
                        label,
                        section.Block.Id,
                        DecodeTextLiteralUnits(label.Literal.GetText(), switchValue.Type),
                        order++));
                }
            }

            if (flattenedLabels.Count == 0)
            {
                return false;
            }

            var lengthType = StarkTypeSymbols.Integer(64);
            var lengthGroups = flattenedLabels
                .GroupBy(static label => label.Units.Length)
                .OrderBy(static group => group.Key)
                .Select(group => (
                    Length: group.Key,
                    Labels: group.OrderBy(static label => label.Order).ToArray(),
                    Block: CreateBlock($"switch_len_{group.Key}")))
                .ToArray();

            var switchCases = lengthGroups
                .Select(group => new MidLevelIrSwitchCase(
                    group.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    group.Block.Id,
                    new MidLevelIrIntegerConstantOperand(new BigInteger(group.Length), lengthType)))
                .ToList();
            var targets = switchCases
                .Select(static item => item.TargetBlockId)
                .Append(defaultTarget)
                .Distinct()
                .ToArray();

            CurrentBlock.Terminator = new MidLevelIrTerminator(
                MidLevelIrTerminatorKind.Switch,
                targets,
                ConditionText: $"{switchStatement.expression().GetText()}.length",
                Condition: length,
                SwitchCases: switchCases,
                DefaultTarget: defaultTarget);

            foreach (var group in lengthGroups)
            {
                CurrentBlock = group.Block;
                if (!EmitPartitionedTextLengthDecision(dataPointer, group.Labels, defaultTarget, switchStatement.expression().GetText()))
                {
                    return false;
                }
            }

            _breakTargets.Push(new BreakTargets(exitBlock.Id, _scopes.Count));
            try
            {
                foreach (var section in sections)
                {
                    CurrentBlock = section.Block;
                    foreach (var nested in section.Section.statement())
                    {
                        LowerStatement(nested);
                    }

                    EnsureGoto(exitBlock.Id);
                }
            }
            finally
            {
                _breakTargets.Pop();
            }

            CurrentBlock = exitBlock;
            return true;
        }

        private bool TryLowerGuardedSwitch(
            StarkParser.SwitchStatementContext switchStatement,
            MidLevelIrOperand switchValue)
        {
            if (!TryParseLowerableSwitchSections(switchStatement, out var parsedSections, out var defaultSectionCount))
            {
                return false;
            }

            if (!CanLowerSwitchType(switchValue.Type) || defaultSectionCount > 1)
            {
                return false;
            }

            var sections = parsedSections
                .Select((section, index) => (
                    section.Section,
                    section.Labels,
                    EntryBlock: CreateBlock($"switch_test_{index}"),
                    BodyBlock: CreateBlock($"switch_case_{index}")))
                .ToArray();
            var exitBlock = CreateBlock("switch_exit");
            var defaultTarget = sections
                .Where(static section => section.Labels.Any(static label => label.IsDefault && label.GuardExpression is null && label.ImportedGuardExpression is null && label.CaptureName is null))
                .Select(static section => section.BodyBlock.Id)
                .FirstOrDefault(exitBlock.Id);

            if (!TryRegisterSwitchCaptureLocals(sections.Select(static section => section.Labels), switchValue.Type))
            {
                return false;
            }

            if (sections.Length == 0)
            {
                CurrentBlock.Terminator = new MidLevelIrTerminator(MidLevelIrTerminatorKind.Goto, [defaultTarget]);
            }
            else
            {
                CurrentBlock.Terminator = new MidLevelIrTerminator(
                    MidLevelIrTerminatorKind.Goto,
                    [sections[0].EntryBlock.Id]);

                for (var index = 0; index < sections.Length; index++)
                {
                    CurrentBlock = sections[index].EntryBlock;
                    var nextSectionTarget = index + 1 < sections.Length ? sections[index + 1].EntryBlock.Id : defaultTarget;

                    if (!EmitSwitchSectionDecision(
                        sections[index].Labels,
                        switchValue,
                        sections[index].BodyBlock.Id,
                        nextSectionTarget,
                        switchStatement.expression().GetText(),
                        index))
                    {
                        return false;
                    }
                }
            }

            _breakTargets.Push(new BreakTargets(exitBlock.Id, _scopes.Count));
            try
            {
                foreach (var section in sections)
                {
                    CurrentBlock = section.BodyBlock;
                    foreach (var nested in section.Section.statement())
                    {
                        LowerStatement(nested);
                    }

                    EnsureGoto(exitBlock.Id);
                }
            }
            finally
            {
                _breakTargets.Pop();
            }

            CurrentBlock = exitBlock;
            return true;
        }

        private bool EmitSwitchSectionDecision(
            IReadOnlyList<LowerableSwitchLabel> labels,
            MidLevelIrOperand switchValue,
            int targetBlockId,
            int nextSectionTarget,
            string switchText,
            int sectionIndex)
        {
            if (labels.Count == 0)
            {
                CurrentBlock.Terminator = new MidLevelIrTerminator(MidLevelIrTerminatorKind.Goto, [nextSectionTarget]);
                return true;
            }

            var decisionBlocks = new BasicBlockBuilder[labels.Count];
            decisionBlocks[0] = CurrentBlock;
            for (var index = 1; index < labels.Count; index++)
            {
                decisionBlocks[index] = CreateBlock($"switch_test_{sectionIndex}_{index}");
            }

            for (var index = 0; index < labels.Count; index++)
            {
                CurrentBlock = decisionBlocks[index];
                var label = labels[index];
                var nextTarget = index + 1 < labels.Count ? decisionBlocks[index + 1].Id : nextSectionTarget;

                if (label.IsMatchAll)
                {
                    if (!EmitSwitchMatchTransition(label, switchValue, targetBlockId, nextTarget))
                    {
                        return false;
                    }

                    continue;
                }

                if (label.AggregatePattern is { } aggregatePattern)
                {
                    if (!EmitAggregateSwitchPatternTransition(label, aggregatePattern, switchValue, targetBlockId, nextTarget, sectionIndex, index))
                    {
                        return false;
                    }

                    continue;
                }

                MidLevelIrOperand? condition;
                if (label.Literal is not null)
                {
                    condition = EmitSwitchLiteralComparison(
                        switchValue,
                        label.Literal,
                        $"switch {switchText} == {label.LabelText}");
                }
                else if (label.ImportedLiteralExpression is not null)
                {
                    condition = EmitImportedTypedTemplateSwitchLiteralComparison(
                        switchValue,
                        label.ImportedLiteralExpression,
                        $"switch {switchText} == {label.LabelText}");
                }
                else
                {
                    return false;
                }

                if (condition is null)
                {
                    return false;
                }

                if (label.GuardExpression is null && label.ImportedGuardExpression is null && label.CaptureName is null)
                {
                    CurrentBlock.Terminator = new MidLevelIrTerminator(
                        MidLevelIrTerminatorKind.Branch,
                        [targetBlockId, nextTarget],
                        ConditionText: label.LabelText,
                        Condition: condition);
                    continue;
                }

                var matchBlock = CreateBlock($"switch_match_{sectionIndex}_{index}");
                CurrentBlock.Terminator = new MidLevelIrTerminator(
                    MidLevelIrTerminatorKind.Branch,
                    [matchBlock.Id, nextTarget],
                    ConditionText: label.LabelText,
                    Condition: condition);

                CurrentBlock = matchBlock;
                if (!EmitSwitchMatchTransition(label, switchValue, targetBlockId, nextTarget))
                {
                    return false;
                }
            }

            return true;
        }

        private bool TryParseAggregatePattern(StarkParser.AggregatePatternContext aggregatePattern, out LowerableAggregatePattern? parsedAggregatePattern)
        {
            parsedAggregatePattern = null;

            if (TryResolvePublishedEnumPatternSummary(aggregatePattern, out var publishedEnumPattern))
            {
                return TryParsePublishedEnumPattern(
                    aggregatePattern.aggregatePatternSuffix(),
                    publishedEnumPattern,
                    out parsedAggregatePattern);
            }

            if (TryResolvePublishedAggregatePatternSummary(aggregatePattern, out var publishedAggregatePattern))
            {
                var publishedPatternType = ApplyGenericSubstitution(publishedAggregatePattern.Type);
                if (publishedPatternType.Kind != StarkTypeKind.Named
                    || publishedPatternType.NamedType is null
                    || !_typeModel.NamedTypes.TryGetValue(publishedPatternType.NamedType, out var publishedNamedType))
                {
                    return false;
                }

                return TryParseResolvedAggregatePattern(
                    publishedPatternType,
                    publishedNamedType,
                    aggregatePattern.aggregatePatternSuffix(),
                    out parsedAggregatePattern);
            }

            var patternName = aggregatePattern.simpleType().GetText();
            if (TryResolveEnumCaseReference(patternName, out var enumType, out _, out var enumVariant))
            {
                if (enumVariant.UsesNamedFields)
                {
                    return false;
                }

                var enumSuffix = aggregatePattern.aggregatePatternSuffix();
                if (enumVariant.Fields.Count == 0)
                {
                    if (enumSuffix is not null)
                    {
                        return false;
                    }

                    parsedAggregatePattern = new LowerableAggregatePattern(enumType.NamedType!, enumVariant.Name, [], WholeCaptureName: null);
                    return true;
                }

                if (enumSuffix is null)
                {
                    return false;
                }

                if (enumSuffix.Identifier() is { } enumCapture)
                {
                    parsedAggregatePattern = new LowerableAggregatePattern(enumType.NamedType!, enumVariant.Name, [], enumCapture.GetText());
                    return true;
                }

                var enumFieldPatterns = enumSuffix.pattern();
                if (enumFieldPatterns.Length != enumVariant.Fields.Count)
                {
                    return false;
                }

                var parsedEnumFieldPatterns = new LowerableAggregateFieldPattern[enumFieldPatterns.Length];
                for (var index = 0; index < enumFieldPatterns.Length; index++)
                {
                    var field = enumVariant.Fields[index];
                    if (!TryParseStructuredFieldPattern(
                            enumFieldPatterns[index],
                            field.SourceFieldName ?? field.SourcePosition.ToString(),
                            field.StorageFieldName,
                            field.StorageFieldIndex,
                            field.Type,
                            out var parsedFieldPattern))
                    {
                        return false;
                    }

                    parsedEnumFieldPatterns[index] = parsedFieldPattern;
                }

                parsedAggregatePattern = new LowerableAggregatePattern(enumType.NamedType!, enumVariant.Name, parsedEnumFieldPatterns, WholeCaptureName: null);
                return true;
            }

            var patternType = _typeResolver.ResolveSimpleType(aggregatePattern.simpleType(), currentModuleName: CurrentModuleName);
            if (patternType.Kind != StarkTypeKind.Named
                || patternType.NamedType is null
                || !_typeModel.NamedTypes.TryGetValue(patternType.NamedType, out var namedType))
            {
                return false;
            }

            return TryParseResolvedAggregatePattern(
                patternType,
                namedType,
                aggregatePattern.aggregatePatternSuffix(),
                out parsedAggregatePattern);
        }

        private bool TryParseResolvedAggregatePattern(
            StarkTypeSymbol patternType,
            NamedTypeSymbol namedType,
            StarkParser.AggregatePatternSuffixContext? suffix,
            out LowerableAggregatePattern? parsedAggregatePattern)
        {
            parsedAggregatePattern = null;

            if (suffix is null)
            {
                parsedAggregatePattern = new LowerableAggregatePattern(patternType.NamedType!, EnumVariantName: null, [], WholeCaptureName: null);
                return true;
            }

            if (suffix.Identifier() is { } capture)
            {
                parsedAggregatePattern = new LowerableAggregatePattern(patternType.NamedType!, EnumVariantName: null, [], capture.GetText());
                return true;
            }

            var fieldPatterns = suffix.pattern();
            if (fieldPatterns.Length != namedType.OrderedFields.Count)
            {
                return false;
            }

            var parsedFieldPatterns = new LowerableAggregateFieldPattern[fieldPatterns.Length];
            for (var index = 0; index < fieldPatterns.Length; index++)
            {
                var field = namedType.OrderedFields[index];
                if (!TryParseStructuredFieldPattern(fieldPatterns[index], field.Name, field.Name, index, field.Type, out var parsedFieldPattern))
                {
                    return false;
                }

                parsedFieldPatterns[index] = parsedFieldPattern;
            }

            parsedAggregatePattern = new LowerableAggregatePattern(patternType.NamedType!, EnumVariantName: null, parsedFieldPatterns, WholeCaptureName: null);
            return true;
        }

        private bool TryParseAggregatePattern(StarkParser.GenericEnumAggregatePatternContext genericEnumAggregatePattern, out LowerableAggregatePattern? parsedAggregatePattern)
        {
            parsedAggregatePattern = null;

            if (TryResolvePublishedEnumPatternSummary(genericEnumAggregatePattern, out var publishedEnumPattern))
            {
                return TryParsePublishedEnumPattern(
                    genericEnumAggregatePattern.aggregatePatternSuffix(),
                    publishedEnumPattern,
                    out parsedAggregatePattern);
            }

            if (!TryResolveEnumCaseReference(genericEnumAggregatePattern.genericEnumCaseReference(), out var enumType, out _, out var enumVariant)
                || enumVariant.UsesNamedFields)
            {
                return false;
            }

            var enumSuffix = genericEnumAggregatePattern.aggregatePatternSuffix();
            if (enumVariant.Fields.Count == 0)
            {
                if (enumSuffix is not null)
                {
                    return false;
                }

                parsedAggregatePattern = new LowerableAggregatePattern(enumType.NamedType!, enumVariant.Name, [], WholeCaptureName: null);
                return true;
            }

            if (enumSuffix is null)
            {
                return false;
            }

            if (enumSuffix.Identifier() is { } enumCapture)
            {
                parsedAggregatePattern = new LowerableAggregatePattern(enumType.NamedType!, enumVariant.Name, [], enumCapture.GetText());
                return true;
            }

            var enumFieldPatterns = enumSuffix.pattern();
            if (enumFieldPatterns.Length != enumVariant.Fields.Count)
            {
                return false;
            }

            var parsedEnumFieldPatterns = new LowerableAggregateFieldPattern[enumFieldPatterns.Length];
            for (var index = 0; index < enumFieldPatterns.Length; index++)
            {
                var field = enumVariant.Fields[index];
                if (!TryParseStructuredFieldPattern(
                        enumFieldPatterns[index],
                        field.SourceFieldName ?? field.SourcePosition.ToString(),
                        field.StorageFieldName,
                        field.StorageFieldIndex,
                        field.Type,
                        out var parsedFieldPattern))
                {
                    return false;
                }

                parsedEnumFieldPatterns[index] = parsedFieldPattern;
            }

            parsedAggregatePattern = new LowerableAggregatePattern(enumType.NamedType!, enumVariant.Name, parsedEnumFieldPatterns, WholeCaptureName: null);
            return true;
        }

        private bool TryParseEnumNamedFieldPattern(StarkParser.EnumNamedFieldPatternContext enumNamedFieldPattern, out LowerableAggregatePattern? parsedAggregatePattern)
        {
            parsedAggregatePattern = null;

            if (TryResolvePublishedEnumPatternSummary(enumNamedFieldPattern, out var publishedEnumPattern))
            {
                return TryParsePublishedEnumNamedFieldPattern(enumNamedFieldPattern, publishedEnumPattern, out parsedAggregatePattern);
            }

            if (!TryResolveEnumCaseTarget(enumNamedFieldPattern.enumCaseTarget(), out _, out var enumType, out _, out var enumVariant)
                || !enumVariant.UsesNamedFields)
            {
                return false;
            }

            var members = enumNamedFieldPattern.enumNamedFieldPatternPayload().namedPatternMember();
            if (members.Length != enumVariant.Fields.Count)
            {
                return false;
            }

            var parsedFieldPatterns = new LowerableAggregateFieldPattern[enumVariant.Fields.Count];
            var seenMembers = new HashSet<string>(StringComparer.Ordinal);
            foreach (var member in members)
            {
                var memberName = member.Identifier().GetText();
                var field = enumVariant.Fields.FirstOrDefault(candidate => string.Equals(candidate.SourceFieldName, memberName, StringComparison.Ordinal));
                if (field is null
                    || field.SourceFieldName is null
                    || !seenMembers.Add(memberName)
                    || !TryParseStructuredFieldPattern(
                        member.pattern(),
                        field.SourceFieldName,
                        field.StorageFieldName,
                        field.StorageFieldIndex,
                        field.Type,
                        out var parsedFieldPattern))
                {
                    return false;
                }

                parsedFieldPatterns[field.SourcePosition] = parsedFieldPattern;
            }

            if (seenMembers.Count != enumVariant.Fields.Count)
            {
                return false;
            }

            parsedAggregatePattern = new LowerableAggregatePattern(enumType.NamedType!, enumVariant.Name, parsedFieldPatterns, WholeCaptureName: null);
            return true;
        }

        private bool TryParsePublishedEnumPattern(
            StarkParser.AggregatePatternSuffixContext? enumSuffix,
            ImportedTemplateEnumPatternSummary publishedEnumPattern,
            out LowerableAggregatePattern? parsedAggregatePattern)
        {
            parsedAggregatePattern = null;

            var publishedEnumType = ApplyGenericSubstitution(publishedEnumPattern.EnumType);
            var publishedCaseName = $"{publishedEnumType.DisplayName}.{publishedEnumPattern.VariantName}";
            if (!TryResolveEnumCaseReference(publishedCaseName, out var enumType, out _, out var enumVariant)
                || enumVariant.UsesNamedFields)
            {
                return false;
            }

            if (enumVariant.Fields.Count == 0)
            {
                if (enumSuffix is not null)
                {
                    return false;
                }

                parsedAggregatePattern = new LowerableAggregatePattern(enumType.NamedType!, enumVariant.Name, [], WholeCaptureName: null);
                return true;
            }

            if (enumSuffix is null)
            {
                return false;
            }

            if (enumSuffix.Identifier() is { } enumCapture)
            {
                parsedAggregatePattern = new LowerableAggregatePattern(enumType.NamedType!, enumVariant.Name, [], enumCapture.GetText());
                return true;
            }

            var enumFieldPatterns = enumSuffix.pattern();
            if (enumFieldPatterns.Length != enumVariant.Fields.Count)
            {
                return false;
            }

            var parsedEnumFieldPatterns = new LowerableAggregateFieldPattern[enumFieldPatterns.Length];
            for (var index = 0; index < enumFieldPatterns.Length; index++)
            {
                var field = enumVariant.Fields[index];
                if (!TryParseStructuredFieldPattern(
                        enumFieldPatterns[index],
                        field.SourceFieldName ?? field.SourcePosition.ToString(),
                        field.StorageFieldName,
                        field.StorageFieldIndex,
                        field.Type,
                        out var parsedFieldPattern))
                {
                    return false;
                }

                parsedEnumFieldPatterns[index] = parsedFieldPattern;
            }

            parsedAggregatePattern = new LowerableAggregatePattern(enumType.NamedType!, enumVariant.Name, parsedEnumFieldPatterns, WholeCaptureName: null);
            return true;
        }

        private bool TryParsePublishedEnumNamedFieldPattern(
            StarkParser.EnumNamedFieldPatternContext enumNamedFieldPattern,
            ImportedTemplateEnumPatternSummary publishedEnumPattern,
            out LowerableAggregatePattern? parsedAggregatePattern)
        {
            parsedAggregatePattern = null;

            var publishedEnumType = ApplyGenericSubstitution(publishedEnumPattern.EnumType);
            var publishedCaseName = $"{publishedEnumType.DisplayName}.{publishedEnumPattern.VariantName}";
            if (!TryResolveEnumCaseReference(publishedCaseName, out var enumType, out _, out var enumVariant)
                || !enumVariant.UsesNamedFields)
            {
                return false;
            }

            var members = enumNamedFieldPattern.enumNamedFieldPatternPayload().namedPatternMember();
            if (members.Length != enumVariant.Fields.Count
                || publishedEnumPattern.Members.Count > 0 && members.Length != publishedEnumPattern.Members.Count)
            {
                return false;
            }

            var parsedFieldPatterns = new LowerableAggregateFieldPattern[enumVariant.Fields.Count];
            var seenMembers = new HashSet<int>();
            for (var memberOrdinal = 0; memberOrdinal < members.Length; memberOrdinal++)
            {
                var member = members[memberOrdinal];
                var memberName = member.Identifier().GetText();
                EnumVariantLayoutFieldSymbol? field;

                if (publishedEnumPattern.Members.Count > 0 && memberOrdinal < publishedEnumPattern.Members.Count)
                {
                    var publishedMember = publishedEnumPattern.Members[memberOrdinal];
                    memberName = publishedMember.FieldName;
                    field = publishedMember.FieldIndex >= 0 && publishedMember.FieldIndex < enumVariant.Fields.Count
                        ? enumVariant.Fields[publishedMember.FieldIndex]
                        : null;
                }
                else
                {
                    field = enumVariant.Fields.FirstOrDefault(candidate => string.Equals(candidate.SourceFieldName, memberName, StringComparison.Ordinal));
                }

                if (field is null
                    || field.SourceFieldName is null
                    || !seenMembers.Add(field.SourcePosition)
                    || !TryParseStructuredFieldPattern(
                        member.pattern(),
                        field.SourceFieldName,
                        field.StorageFieldName,
                        field.StorageFieldIndex,
                        field.Type,
                        out var parsedFieldPattern))
                {
                    return false;
                }

                parsedFieldPatterns[field.SourcePosition] = parsedFieldPattern;
            }

            if (seenMembers.Count != enumVariant.Fields.Count)
            {
                return false;
            }

            parsedAggregatePattern = new LowerableAggregatePattern(enumType.NamedType!, enumVariant.Name, parsedFieldPatterns, WholeCaptureName: null);
            return true;
        }

        private bool TryParseStructuredFieldPattern(
            StarkParser.PatternContext pattern,
            string fieldName,
            string storageFieldName,
            int fieldIndex,
            StarkTypeSymbol fieldType,
            out LowerableAggregateFieldPattern parsedFieldPattern)
        {
            if (pattern.DISCARD() is not null)
            {
                parsedFieldPattern = new LowerableAggregateFieldPattern(
                    fieldName,
                    storageFieldName,
                    fieldIndex,
                    fieldType,
                    AggregatePatternFieldKind.Discard,
                    pattern.GetText(),
                    Literal: null,
                    CaptureName: null,
                    NestedPattern: null,
                    ImportedLiteralExpression: null);
                return true;
            }

            if (pattern.VAR() is not null)
            {
                parsedFieldPattern = new LowerableAggregateFieldPattern(
                    fieldName,
                    storageFieldName,
                    fieldIndex,
                    fieldType,
                    AggregatePatternFieldKind.Capture,
                    pattern.GetText(),
                    Literal: null,
                    CaptureName: pattern.Identifier()?.GetText(),
                    NestedPattern: null,
                    ImportedLiteralExpression: null);
                return true;
            }

            if (pattern.literal() is { } literal)
            {
                parsedFieldPattern = new LowerableAggregateFieldPattern(
                    fieldName,
                    storageFieldName,
                    fieldIndex,
                    fieldType,
                    AggregatePatternFieldKind.Literal,
                    literal.GetText(),
                    literal,
                    CaptureName: null,
                    NestedPattern: null,
                    ImportedLiteralExpression: null);
                return true;
            }

            if (pattern.enumNamedFieldPattern() is { } nestedEnumNamedFieldPattern)
            {
                if (!TryParseEnumNamedFieldPattern(nestedEnumNamedFieldPattern, out var parsedNestedPattern)
                    || parsedNestedPattern is null
                    || parsedNestedPattern.WholeCaptureName is not null)
                {
                    parsedFieldPattern = default!;
                    return false;
                }

                parsedFieldPattern = new LowerableAggregateFieldPattern(
                    fieldName,
                    storageFieldName,
                    fieldIndex,
                    fieldType,
                    AggregatePatternFieldKind.Nested,
                    nestedEnumNamedFieldPattern.GetText(),
                    Literal: null,
                    CaptureName: null,
                    NestedPattern: parsedNestedPattern,
                    ImportedLiteralExpression: null);
                return true;
            }

            if (pattern.aggregatePattern() is { } nestedAggregatePattern)
            {
                if (!TryParseAggregatePattern(nestedAggregatePattern, out var parsedNestedPattern)
                    || parsedNestedPattern is null
                    || parsedNestedPattern.WholeCaptureName is not null)
                {
                    parsedFieldPattern = default!;
                    return false;
                }

                parsedFieldPattern = new LowerableAggregateFieldPattern(
                    fieldName,
                    storageFieldName,
                    fieldIndex,
                    fieldType,
                    AggregatePatternFieldKind.Nested,
                    nestedAggregatePattern.GetText(),
                    Literal: null,
                    CaptureName: null,
                    NestedPattern: parsedNestedPattern,
                    ImportedLiteralExpression: null);
                return true;
            }

            if (pattern.genericEnumAggregatePattern() is { } nestedGenericEnumAggregatePattern)
            {
                if (!TryParseAggregatePattern(nestedGenericEnumAggregatePattern, out var parsedNestedPattern)
                    || parsedNestedPattern is null
                    || parsedNestedPattern.WholeCaptureName is not null)
                {
                    parsedFieldPattern = default!;
                    return false;
                }

                parsedFieldPattern = new LowerableAggregateFieldPattern(
                    fieldName,
                    storageFieldName,
                    fieldIndex,
                    fieldType,
                    AggregatePatternFieldKind.Nested,
                    nestedGenericEnumAggregatePattern.GetText(),
                    Literal: null,
                    CaptureName: null,
                    NestedPattern: parsedNestedPattern,
                    ImportedLiteralExpression: null);
                return true;
            }

            parsedFieldPattern = default!;
            return false;
        }

        private bool TryParseLowerableSwitchSections(
            StarkParser.SwitchStatementContext switchStatement,
            out List<LowerableSwitchSection> sections,
            out int defaultSectionCount)
        {
            sections = [];
            defaultSectionCount = 0;

            foreach (var section in switchStatement.switchSection())
            {
                var labels = new List<LowerableSwitchLabel>();

                foreach (var label in section.switchLabel())
                {
                    if (label.DEFAULT() is not null)
                    {
                        labels.Add(new LowerableSwitchLabel("default", null, null, IsDefault: true, IsMatchAll: true, CaptureName: null, AggregatePattern: null));
                        defaultSectionCount++;
                        continue;
                    }

                    var pattern = label.pattern();
                    if (pattern is null)
                    {
                        return false;
                    }

                    if (pattern.DISCARD() is not null)
                    {
                        if (label.whenClause() is null)
                        {
                            labels.Add(new LowerableSwitchLabel(pattern.GetText(), null, null, IsDefault: true, IsMatchAll: true, CaptureName: null, AggregatePattern: null));
                            defaultSectionCount++;
                            continue;
                        }

                        labels.Add(new LowerableSwitchLabel(
                            pattern.GetText(),
                            Literal: null,
                            GuardExpression: label.whenClause()?.expression(),
                            IsDefault: false,
                            IsMatchAll: true,
                            CaptureName: null,
                            AggregatePattern: null));
                        continue;
                    }

                    if (pattern.VAR() is not null)
                    {
                        labels.Add(new LowerableSwitchLabel(
                            pattern.GetText(),
                            Literal: null,
                            GuardExpression: label.whenClause()?.expression(),
                            IsDefault: false,
                            IsMatchAll: true,
                            CaptureName: pattern.Identifier()?.GetText(),
                            AggregatePattern: null));
                        continue;
                    }

                    if (pattern.enumNamedFieldPattern() is { } enumNamedFieldPattern)
                    {
                        if (!TryParseEnumNamedFieldPattern(enumNamedFieldPattern, out var parsedEnumNamedFieldPattern)
                            || parsedEnumNamedFieldPattern is null)
                        {
                            return false;
                        }

                        labels.Add(new LowerableSwitchLabel(
                            enumNamedFieldPattern.GetText(),
                            Literal: null,
                            GuardExpression: label.whenClause()?.expression(),
                            IsDefault: false,
                            IsMatchAll: false,
                            CaptureName: null,
                            AggregatePattern: parsedEnumNamedFieldPattern));
                        continue;
                    }

                    if (pattern.aggregatePattern() is { } aggregatePattern)
                    {
                        if (!TryParseAggregatePattern(aggregatePattern, out var parsedAggregatePattern)
                            || parsedAggregatePattern is null)
                        {
                            return false;
                        }

                        labels.Add(new LowerableSwitchLabel(
                            aggregatePattern.GetText(),
                            Literal: null,
                            GuardExpression: label.whenClause()?.expression(),
                            IsDefault: false,
                            IsMatchAll: false,
                            CaptureName: null,
                            AggregatePattern: parsedAggregatePattern));
                        continue;
                    }

                    if (pattern.genericEnumAggregatePattern() is { } genericEnumAggregatePattern)
                    {
                        if (!TryParseAggregatePattern(genericEnumAggregatePattern, out var parsedAggregatePattern)
                            || parsedAggregatePattern is null)
                        {
                            return false;
                        }

                        labels.Add(new LowerableSwitchLabel(
                            genericEnumAggregatePattern.GetText(),
                            Literal: null,
                            GuardExpression: label.whenClause()?.expression(),
                            IsDefault: false,
                            IsMatchAll: false,
                            CaptureName: null,
                            AggregatePattern: parsedAggregatePattern));
                        continue;
                    }

                    if (pattern.literal() is not { } literal)
                    {
                        return false;
                    }

                    labels.Add(new LowerableSwitchLabel(
                        literal.GetText(),
                        literal,
                        label.whenClause()?.expression(),
                        IsDefault: false,
                        IsMatchAll: false,
                        CaptureName: null,
                        AggregatePattern: null));
                }

                sections.Add(new LowerableSwitchSection(section, labels));
            }

            return true;
        }

        private bool TryRegisterSwitchCaptureLocals(
            IEnumerable<IReadOnlyList<LowerableSwitchLabel>> sectionLabels,
            StarkTypeSymbol switchType)
        {
            foreach (var labels in sectionLabels)
            {
                var aggregateLabels = labels.Where(static label => label.AggregatePattern is not null).ToArray();
                if (aggregateLabels.Length != 0)
                {
                    if (aggregateLabels.Length != 1 || labels.Count != 1)
                    {
                        return false;
                    }

                    var aggregatePattern = aggregateLabels[0].AggregatePattern!;
                    if (aggregatePattern.WholeCaptureName is not null)
                    {
                        return false;
                    }

                    if (!TryRegisterAggregatePatternCaptureLocals(aggregatePattern))
                    {
                        return false;
                    }

                    continue;
                }

                var captureLabels = labels.Where(static label => label.CaptureName is not null).ToArray();
                if (captureLabels.Length == 0)
                {
                    continue;
                }

                if (captureLabels.Length != 1 || labels.Count != 1)
                {
                    return false;
                }

                var captureName = captureLabels[0].CaptureName!;
                if (_localsByName.ContainsKey(captureName) || _parametersByName.ContainsKey(captureName))
                {
                    return false;
                }

                RegisterLocal(captureName, switchType, storageClass: "match", isMutable: false, isConstant: false);
            }

            return true;
        }

        private bool TryRegisterAggregatePatternCaptureLocals(LowerableAggregatePattern aggregatePattern)
        {
            foreach (var fieldPattern in aggregatePattern.FieldPatterns)
            {
                if (fieldPattern.Kind == AggregatePatternFieldKind.Capture)
                {
                    if (fieldPattern.CaptureName is null
                        || _localsByName.ContainsKey(fieldPattern.CaptureName)
                        || _parametersByName.ContainsKey(fieldPattern.CaptureName))
                    {
                        return false;
                    }

                    RegisterLocal(fieldPattern.CaptureName, fieldPattern.FieldType, storageClass: "match", isMutable: false, isConstant: false);
                    continue;
                }

                if (fieldPattern.Kind == AggregatePatternFieldKind.Nested)
                {
                    if (fieldPattern.NestedPattern is null
                        || fieldPattern.NestedPattern.WholeCaptureName is not null
                        || !TryRegisterAggregatePatternCaptureLocals(fieldPattern.NestedPattern))
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private bool EmitSwitchMatchTransition(LowerableSwitchLabel label, MidLevelIrOperand switchValue, int targetBlockId, int nextTarget)
        {
            IReadOnlyList<PendingSwitchBinding> bindings = label.CaptureName is null
                ? []
                : [new PendingSwitchBinding(label.CaptureName, switchValue)];

            return EmitSwitchBindingsAndGuard(label.GuardExpression, label.ImportedGuardExpression, bindings, targetBlockId, nextTarget);
        }

        private bool EmitAggregateSwitchPatternTransition(
            LowerableSwitchLabel label,
            LowerableAggregatePattern aggregatePattern,
            MidLevelIrOperand switchValue,
            int targetBlockId,
            int nextTarget,
            int sectionIndex,
            int labelIndex)
        {
            if (aggregatePattern.WholeCaptureName is not null)
            {
                return false;
            }

            if (switchValue.Type.Kind != StarkTypeKind.Named
                || switchValue.Type.NamedType is null
                || !string.Equals(switchValue.Type.NamedType, aggregatePattern.TypeName, StringComparison.Ordinal))
            {
                return false;
            }

            var bindings = new List<PendingSwitchBinding>();
            var matchBlock = CreateBlock($"switch_agg_match_{sectionIndex}_{labelIndex}");
            if (!EmitAggregatePatternDecision(
                aggregatePattern,
                switchValue,
                matchBlock.Id,
                nextTarget,
                bindings,
                $"{sectionIndex}_{labelIndex}"))
            {
                return false;
            }

            CurrentBlock = matchBlock;
            return EmitSwitchBindingsAndGuard(label.GuardExpression, label.ImportedGuardExpression, bindings, targetBlockId, nextTarget);
        }

        private bool EmitAggregatePatternDecision(
            LowerableAggregatePattern aggregatePattern,
            MidLevelIrOperand switchValue,
            int successTarget,
            int failureTarget,
            List<PendingSwitchBinding> bindings,
            string pathTag)
        {
            var fieldPatterns = aggregatePattern.FieldPatterns;
            if (aggregatePattern.EnumVariantName is { } enumVariantName)
            {
                if (!_enumLayoutModel.Layouts.TryGetValue(aggregatePattern.TypeName, out var enumLayout)
                    || !enumLayout.TryGetVariant(enumVariantName, out var enumVariant))
                {
                    return false;
                }

                BasicBlockBuilder? payloadEntryBlock = null;
                var successAfterTag = successTarget;
                if (fieldPatterns.Count != 0)
                {
                    payloadEntryBlock = CreateBlock($"switch_enum_match_{pathTag}");
                    successAfterTag = payloadEntryBlock.Id;
                }

                var tagValue = LowerKnownFieldAccess(switchValue, enumLayout.TagField.Name, fieldIndex: 0, enumLayout.TagField.Type, "$tag");
                var expectedTag = new MidLevelIrIntegerConstantOperand(new BigInteger(enumVariant.TagValue), enumLayout.TagField.Type);
                var condition = EmitResolvedEqualityComparison(tagValue, expectedTag, $"switch {switchValue.Text} is {aggregatePattern.TypeName}.{enumVariantName}");

                CurrentBlock.Terminator = new MidLevelIrTerminator(
                    MidLevelIrTerminatorKind.Branch,
                    [successAfterTag, failureTarget],
                    ConditionText: $"{aggregatePattern.TypeName}.{enumVariantName}",
                    Condition: condition);

                if (fieldPatterns.Count == 0)
                {
                    return true;
                }

                CurrentBlock = payloadEntryBlock!;
            }

            if (fieldPatterns.Count == 0)
            {
                CurrentBlock.Terminator = new MidLevelIrTerminator(MidLevelIrTerminatorKind.Goto, [successTarget]);
                return true;
            }

            var decisionBlocks = new BasicBlockBuilder[fieldPatterns.Count];
            decisionBlocks[0] = CurrentBlock;
            for (var index = 1; index < fieldPatterns.Count; index++)
            {
                decisionBlocks[index] = CreateBlock($"switch_agg_test_{pathTag}_{index}");
            }

            for (var index = 0; index < fieldPatterns.Count; index++)
            {
                CurrentBlock = decisionBlocks[index];
                var fieldPattern = fieldPatterns[index];
                var nextTarget = index + 1 < fieldPatterns.Count ? decisionBlocks[index + 1].Id : successTarget;

                if (fieldPattern.Kind == AggregatePatternFieldKind.Discard)
                {
                    CurrentBlock.Terminator = new MidLevelIrTerminator(MidLevelIrTerminatorKind.Goto, [nextTarget]);
                    continue;
                }

                var fieldValue = LowerKnownFieldAccess(switchValue, fieldPattern.StorageFieldName, fieldPattern.FieldIndex, fieldPattern.FieldType, fieldPattern.FieldName);
                if (fieldPattern.Kind == AggregatePatternFieldKind.Capture)
                {
                    bindings.Add(new PendingSwitchBinding(fieldPattern.CaptureName!, fieldValue));
                    CurrentBlock.Terminator = new MidLevelIrTerminator(MidLevelIrTerminatorKind.Goto, [nextTarget]);
                    continue;
                }

                if (fieldPattern.Kind == AggregatePatternFieldKind.Nested)
                {
                    if (fieldPattern.NestedPattern is null
                        || !EmitAggregatePatternDecision(
                            fieldPattern.NestedPattern,
                            fieldValue,
                            nextTarget,
                            failureTarget,
                            bindings,
                            $"{pathTag}_{index}"))
                    {
                        return false;
                    }

                    continue;
                }

                var condition = fieldPattern.ImportedLiteralExpression is { } importedLiteralExpression
                    ? EmitImportedTypedTemplateSwitchLiteralComparison(
                        fieldValue,
                        importedLiteralExpression,
                        $"switch {switchValue.Text}.{fieldPattern.FieldName} == {fieldPattern.Text}")
                    : EmitSwitchLiteralComparison(
                        fieldValue,
                        fieldPattern.Literal!,
                        $"switch {switchValue.Text}.{fieldPattern.FieldName} == {fieldPattern.Text}");
                if (condition is null)
                {
                    return false;
                }

                CurrentBlock.Terminator = new MidLevelIrTerminator(
                    MidLevelIrTerminatorKind.Branch,
                    [nextTarget, failureTarget],
                    ConditionText: fieldPattern.Text,
                    Condition: condition);
            }

            return true;
        }

        private bool EmitSwitchBindingsAndGuard(
            StarkParser.ExpressionContext? guardExpression,
            ImportedTemplateTypedBodyExpressionSummary? importedGuardExpression,
            IReadOnlyList<PendingSwitchBinding> bindings,
            int targetBlockId,
            int nextTarget)
        {
            if (bindings.Count != 0 && (guardExpression is not null || importedGuardExpression is not null))
            {
                var bindBlock = CreateBlock("switch_bind");
                CurrentBlock.Terminator = new MidLevelIrTerminator(MidLevelIrTerminatorKind.Goto, [bindBlock.Id]);
                CurrentBlock = bindBlock;
            }

            foreach (var binding in bindings)
            {
                var capture = new MidLevelIrLocalOperand(binding.Name, binding.Source.Type);
                EmitOperandAssignment(capture, binding.Source, binding.Source.Text);
            }

            if (guardExpression is null && importedGuardExpression is null)
            {
                CurrentBlock.Terminator = new MidLevelIrTerminator(MidLevelIrTerminatorKind.Goto, [targetBlockId]);
                return true;
            }

            MidLevelIrOperand? guard;
            string conditionText;
            if (guardExpression is not null)
            {
                guard = LowerExpressionToOperand(guardExpression, StarkTypeSymbols.Bool);
                conditionText = guardExpression.GetText();
            }
            else
            {
                guard = LowerImportedTypedTemplateExpression(importedGuardExpression!, StarkTypeSymbols.Bool);
                conditionText = RenderImportedTypedTemplateExpression(importedGuardExpression!);
            }

            if (guard is null)
            {
                return false;
            }

            CurrentBlock.Terminator = new MidLevelIrTerminator(
                MidLevelIrTerminatorKind.Branch,
                [targetBlockId, nextTarget],
                ConditionText: conditionText,
                Condition: guard);
            return true;
        }

        private void LowerWhile(StarkParser.WhileStatementContext whileStatement)
        {
            var conditionBlock = CreateBlock($"while_{whileStatement.loopBehavior().GetText()}_cond");
            var bodyBlock = CreateBlock("while_body");
            var exitBlock = CreateBlock("while_exit");

            EnsureGoto(conditionBlock.Id);

            CurrentBlock = conditionBlock;
            CurrentBlock.Terminator = new MidLevelIrTerminator(
                MidLevelIrTerminatorKind.Branch,
                [bodyBlock.Id, exitBlock.Id],
                ConditionText: whileStatement.expression().GetText(),
                Condition: LowerExpressionToOperand(whileStatement.expression(), StarkTypeSymbols.Bool));

            _loops.Push(new LoopTargets(conditionBlock.Id, exitBlock.Id, _scopes.Count));
            _breakTargets.Push(new BreakTargets(exitBlock.Id, _scopes.Count));
            CurrentBlock = bodyBlock;
            try
            {
                LowerStatement(whileStatement.statement());
            }
            finally
            {
                _breakTargets.Pop();
                _loops.Pop();
            }
            EnsureGoto(conditionBlock.Id);

            CurrentBlock = exitBlock;
        }

        private void LowerFor(StarkParser.ForStatementContext forStatement)
        {
            if (forStatement.forInitializer()?.localForVariableDeclaration() is { } localForVariableDeclaration)
            {
                var declaredType = TryResolvePublishedLocalDeclarationType(TemplateLocalDeclarationFacts.ForVariableKind, localForVariableDeclaration, out var publishedType)
                    ? publishedType
                    : ResolveTypeWithGenericSubstitution(localForVariableDeclaration.type_(), CurrentModuleName);
                var storageClass = localForVariableDeclaration.storageClass().GetText();

                foreach (var declarator in localForVariableDeclaration.variableDeclarators().variableDeclarator())
                {
                    var name = declarator.Identifier().GetText();
                    RegisterLocal(name, declaredType, storageClass, localForVariableDeclaration.MUT() is not null, isConstant: false);
                    TrackDeclaredLocal(name, declaredType);
                    Emit(MidLevelIrStatementKind.StorageLive, name, name, declaredType);
                    InitializeRuntimeDropState(name, declaredType, isActive: false);
                    if (declarator.variableInitializer() is { } initializer)
                    {
                        LowerVariableInitializer(name, declaredType, initializer);
                        SetRuntimeDropState(name, isActive: true);
                    }
                }
            }
            else if (forStatement.forInitializer()?.expressionList() is { } initializerExpressions)
            {
                foreach (var expression in initializerExpressions.expression())
                {
                    LowerExpressionStatement(expression);
                }
            }

            var conditionBlock = CreateBlock($"for_{forStatement.loopBehavior().GetText()}_cond");
            var bodyBlock = CreateBlock("for_body");
            var iteratorBlock = CreateBlock("for_iter");
            var exitBlock = CreateBlock("for_exit");

            EnsureGoto(conditionBlock.Id);

            CurrentBlock = conditionBlock;
            if (forStatement.forCondition() is { } condition)
            {
                CurrentBlock.Terminator = new MidLevelIrTerminator(
                    MidLevelIrTerminatorKind.Branch,
                    [bodyBlock.Id, exitBlock.Id],
                    ConditionText: condition.expression().GetText(),
                    Condition: LowerExpressionToOperand(condition.expression(), StarkTypeSymbols.Bool));
            }
            else
            {
                CurrentBlock.Terminator = new MidLevelIrTerminator(MidLevelIrTerminatorKind.Goto, [bodyBlock.Id]);
            }

            _loops.Push(new LoopTargets(iteratorBlock.Id, exitBlock.Id, _scopes.Count));
            _breakTargets.Push(new BreakTargets(exitBlock.Id, _scopes.Count));
            CurrentBlock = bodyBlock;
            try
            {
                LowerStatement(forStatement.statement());
            }
            finally
            {
                _breakTargets.Pop();
                _loops.Pop();
            }
            EnsureGoto(iteratorBlock.Id);

            CurrentBlock = iteratorBlock;
            if (forStatement.forIterator() is { } iterator)
            {
                foreach (var expression in iterator.expressionList().expression())
                {
                    LowerExpressionStatement(expression);
                }
            }

            EnsureGoto(conditionBlock.Id);
            CurrentBlock = exitBlock;
        }

        private MidLevelIrOperand? LowerExpressionToOperand(StarkParser.ExpressionContext expression, StarkTypeSymbol? expectedType = null)
        {
            if (TryEvaluateCompileTimeExpression(expression, CurrentModuleName, state: null, activeCalls: null, out var constant))
            {
                if (expectedType is not null
                    && CompileTimeExpressionEvaluator.TryCoerce(constant, expectedType, out var coerced))
                {
                    return CreateCompileTimeOperand(coerced);
                }

                return expectedType is null
                    ? CreateCompileTimeOperand(constant)
                    : CoerceOperand(CreateCompileTimeOperand(constant), expectedType);
            }

            var operand = LowerAssignmentExpressionToOperand(expression.assignmentExpression(), expectedType);
            return expectedType is null ? operand : CoerceOperand(operand, expectedType);
        }

        private bool TryEvaluateCompileTimeExpression(
            StarkParser.ExpressionContext expression,
            string moduleName,
            CompileTimeEvaluationState? state,
            HashSet<string>? activeCalls,
            out CompileTimeConstant constant)
        {
            activeCalls ??= new HashSet<string>(StringComparer.Ordinal);
            TryResolveCompileTimeIdentifier? nameResolver = state is null
                ? null
                : new TryResolveCompileTimeIdentifier(state.TryResolve);
            TryEvaluateCompileTimePostfixExpression postfixResolver =
                (StarkParser.PostfixExpressionContext postfix, CompileTimeEvaluationServices _, out CompileTimeConstant value) =>
                    TryEvaluateCompileTimeLawCall(postfix, moduleName, state, activeCalls, out value);
            var services = new CompileTimeEvaluationServices(
                TryResolveIdentifier: nameResolver,
                TryEvaluatePostfixExpression: postfixResolver);
            return CompileTimeExpressionEvaluator.TryEvaluate(expression, out constant, services);
        }

        private bool TryEvaluateCompileTimeInteger(
            StarkParser.ExpressionContext expression,
            string moduleName,
            CompileTimeEvaluationState? state,
            HashSet<string>? activeCalls,
            out BigInteger value)
        {
            activeCalls ??= new HashSet<string>(StringComparer.Ordinal);
            TryResolveCompileTimeIdentifier? nameResolver = state is null
                ? null
                : new TryResolveCompileTimeIdentifier(state.TryResolve);
            TryEvaluateCompileTimePostfixExpression postfixResolver =
                (StarkParser.PostfixExpressionContext postfix, CompileTimeEvaluationServices _, out CompileTimeConstant constant) =>
                    TryEvaluateCompileTimeLawCall(postfix, moduleName, state, activeCalls, out constant);
            var services = new CompileTimeEvaluationServices(
                TryResolveIdentifier: nameResolver,
                TryEvaluatePostfixExpression: postfixResolver);
            return CompileTimeExpressionEvaluator.TryEvaluateInteger(expression, out value, services);
        }

        private bool TryEvaluateCompileTimeLawCall(
            StarkParser.PostfixExpressionContext expression,
            string moduleName,
            CompileTimeEvaluationState? state,
            HashSet<string> activeCalls,
            out CompileTimeConstant constant)
        {
            constant = default;

            if (expression.postfixPart().Length == 0
                || expression.postfixPart()[^1].argumentList() is not { } finalArguments)
            {
                return false;
            }

            string? currentName = expression.primaryExpression().Identifier()?.GetText()
                ?? expression.primaryExpression().qualifiedName()?.GetText();
            if (currentName is null)
            {
                return false;
            }

            for (var index = 0; index < expression.postfixPart().Length; index++)
            {
                var postfixPart = expression.postfixPart()[index];
                if (postfixPart.argumentList() is { } arguments)
                {
                    return index == expression.postfixPart().Length - 1
                        && currentName is not null
                        && ReferenceEquals(arguments, finalArguments)
                        && TryEvaluateCompileTimeCallByName(currentName, moduleName, arguments, state, activeCalls, out constant);
                }

                if (postfixPart.expressionList() is not null)
                {
                    return false;
                }

                var memberName = postfixPart.Identifier()?.GetText();
                if (memberName is null)
                {
                    return false;
                }

                currentName = $"{currentName}.{memberName}";
            }

            return false;
        }

        private bool TryEvaluateCompileTimeCallByName(
            string functionName,
            string moduleName,
            StarkParser.ArgumentListContext arguments,
            CompileTimeEvaluationState? state,
            HashSet<string> activeCalls,
            out CompileTimeConstant constant)
        {
            constant = default;

            var argumentConstants = new List<CompileTimeConstant>(arguments.argument().Length);
            foreach (var argument in arguments.argument())
            {
                if (!TryEvaluateCompileTimeExpression(argument.expression(), moduleName, state, activeCalls, out var argumentConstant))
                {
                    return false;
                }

                argumentConstants.Add(argumentConstant);
            }

            TypedFunctionSignature signature;
            if (TryGetFunctionOverloads(functionName, moduleName, out var overloads))
            {
                var resolution = FunctionOverloadFacts.Resolve(
                    overloads,
                    receiverType: null,
                    argumentConstants.Select(static argument => argument.Type).ToArray(),
                    TypeCompatibilityFacts.CanAssign);
                if (!resolution.Succeeded)
                {
                    return false;
                }

                signature = resolution.Match!;
            }
            else if (!TryResolveFunctionSignature(functionName, moduleName, out signature))
            {
                return false;
            }

            if (arguments.argument().Length != signature.Parameters.Count
                || !_functionsByName.TryGetValue(signature.Name, out var functionContext)
                || !functionContext.Declaration.HasBody
                || functionContext.Declaration.Body.block() is not { } body
                || !FunctionKindFacts.IsLaw(functionContext.Declaration.DeclaredKind)
                || functionContext.Declaration.TypeParameters is not null)
            {
                return false;
            }

            var coercedArguments = new List<CompileTimeConstant>(argumentConstants.Count);
            for (var index = 0; index < argumentConstants.Count; index++)
            {
                if (!CompileTimeExpressionEvaluator.TryCoerce(argumentConstants[index], signature.Parameters[index].Type, out var coerced))
                {
                    return false;
                }

                coercedArguments.Add(coerced);
            }

            return TryExecuteCompileTimeFunction(signature, functionContext, body, coercedArguments, activeCalls, out constant);
        }

        private bool TryExecuteCompileTimeFunction(
            TypedFunctionSignature signature,
            FunctionLoweringContext functionContext,
            StarkParser.BlockContext body,
            IReadOnlyList<CompileTimeConstant> arguments,
            HashSet<string> activeCalls,
            out CompileTimeConstant constant)
        {
            constant = default;

            if (!activeCalls.Add(signature.Name))
            {
                return false;
            }

            var state = new CompileTimeEvaluationState();
            state.PushScope();
            try
            {
                for (var index = 0; index < signature.Parameters.Count; index++)
                {
                    state.Declare(signature.Parameters[index].Name, arguments[index], isMutable: false);
                }

                if (!TryExecuteCompileTimeBlock(body, functionContext.ModuleName, state, activeCalls, signature.ReturnType, out var returned, out var returnValue)
                    || !returned
                    || signature.ReturnType.Kind == StarkTypeKind.Void)
                {
                    return false;
                }

                if (!CompileTimeExpressionEvaluator.TryCoerce(returnValue, signature.ReturnType, out constant))
                {
                    return false;
                }

                return true;
            }
            finally
            {
                state.PopScope();
                activeCalls.Remove(signature.Name);
            }
        }

        private bool TryExecuteCompileTimeBlock(
            StarkParser.BlockContext block,
            string moduleName,
            CompileTimeEvaluationState state,
            HashSet<string> activeCalls,
            StarkTypeSymbol returnType,
            out bool returned,
            out CompileTimeConstant returnValue)
        {
            returned = false;
            returnValue = default;
            state.PushScope();
            try
            {
                foreach (var statement in block.statement())
                {
                    if (!TryExecuteCompileTimeStatement(statement, moduleName, state, activeCalls, returnType, out returned, out returnValue))
                    {
                        return false;
                    }

                    if (returned)
                    {
                        return true;
                    }
                }

                return true;
            }
            finally
            {
                state.PopScope();
            }
        }

        private bool TryExecuteCompileTimeScopedStatement(
            StarkParser.StatementContext statement,
            string moduleName,
            CompileTimeEvaluationState state,
            HashSet<string> activeCalls,
            StarkTypeSymbol returnType,
            out bool returned,
            out CompileTimeConstant returnValue)
        {
            returned = false;
            returnValue = default;
            state.PushScope();
            try
            {
                return TryExecuteCompileTimeStatement(statement, moduleName, state, activeCalls, returnType, out returned, out returnValue);
            }
            finally
            {
                state.PopScope();
            }
        }

        private bool TryExecuteCompileTimeStatement(
            StarkParser.StatementContext statement,
            string moduleName,
            CompileTimeEvaluationState state,
            HashSet<string> activeCalls,
            StarkTypeSymbol returnType,
            out bool returned,
            out CompileTimeConstant returnValue)
        {
            returned = false;
            returnValue = default;

            if (statement.block() is { } block)
            {
                return TryExecuteCompileTimeBlock(block, moduleName, state, activeCalls, returnType, out returned, out returnValue);
            }

            if (statement.localConstantDeclaration() is { } localConstant)
            {
                var declaredType = ResolveTypeWithGenericSubstitution(localConstant.type_(), moduleName);
                foreach (var declarator in localConstant.constantDeclarators().constantDeclarator())
                {
                    if (declarator.variableInitializer()?.expression() is not { } initializerExpression
                        || !TryEvaluateCompileTimeExpression(initializerExpression, moduleName, state, activeCalls, out var initializer)
                        || !CompileTimeExpressionEvaluator.TryCoerce(initializer, declaredType, out var coerced))
                    {
                        return false;
                    }

                    state.Declare(declarator.Identifier().GetText(), coerced, isMutable: false);
                }

                return true;
            }

            if (statement.localVariableDeclaration() is { } localVariable)
            {
                var declaredType = ResolveTypeWithGenericSubstitution(localVariable.type_(), moduleName);
                foreach (var declarator in localVariable.variableDeclarators().variableDeclarator())
                {
                    if (declarator.variableInitializer()?.expression() is not { } initializerExpression
                        || !TryEvaluateCompileTimeExpression(initializerExpression, moduleName, state, activeCalls, out var initializer)
                        || !CompileTimeExpressionEvaluator.TryCoerce(initializer, declaredType, out var coerced))
                    {
                        return false;
                    }

                    state.Declare(declarator.Identifier().GetText(), coerced, isMutable: localVariable.MUT() is not null);
                }

                return true;
            }

            if (statement.ifStatement() is { } ifStatement)
            {
                if (!TryEvaluateCompileTimeExpression(ifStatement.expression(), moduleName, state, activeCalls, out var condition)
                    || condition.Kind != CompileTimeConstantKind.Bool)
                {
                    return false;
                }

                if (!condition.BoolValue)
                {
                    return ifStatement.statement().Length < 2
                        || TryExecuteCompileTimeScopedStatement(ifStatement.statement(1), moduleName, state, activeCalls, returnType, out returned, out returnValue);
                }

                return TryExecuteCompileTimeScopedStatement(ifStatement.statement(0), moduleName, state, activeCalls, returnType, out returned, out returnValue);
            }

            if (statement.returnStatement() is { } returnStatement)
            {
                returned = true;
                if (returnStatement.expression() is null)
                {
                    return returnType.Kind == StarkTypeKind.Void;
                }

                if (!TryEvaluateCompileTimeExpression(returnStatement.expression(), moduleName, state, activeCalls, out var computed)
                    || !CompileTimeExpressionEvaluator.TryCoerce(computed, returnType, out returnValue))
                {
                    returned = false;
                    return false;
                }

                return true;
            }

            if (statement.expressionStatement() is { } expressionStatement)
            {
                return TryHandleCompileTimeAssignmentStatement(expressionStatement.expression(), moduleName, state, activeCalls)
                    || TryEvaluateCompileTimeExpression(expressionStatement.expression(), moduleName, state, activeCalls, out _);
            }

            return false;
        }

        private bool TryHandleCompileTimeAssignmentStatement(
            StarkParser.ExpressionContext expression,
            string moduleName,
            CompileTimeEvaluationState state,
            HashSet<string> activeCalls)
        {
            var assignment = expression.assignmentExpression();
            if (assignment.assignmentOperator() is null
                || assignment.assignmentOperator().GetText() != "="
                || assignment.unaryExpression() is not { } unaryExpression
                || !TryResolveCompileTimeAssignmentTarget(unaryExpression, out var targetName)
                || !state.TryResolve(targetName, out var targetValue)
                || !TryEvaluateCompileTimeAssignmentExpression(assignment.assignmentExpression(), moduleName, state, activeCalls, out var assignedValue)
                || !CompileTimeExpressionEvaluator.TryCoerce(assignedValue, targetValue.Type, out var coerced))
            {
                return false;
            }

            return state.TryAssign(targetName, coerced);
        }

        private bool TryEvaluateCompileTimeAssignmentExpression(
            StarkParser.AssignmentExpressionContext expression,
            string moduleName,
            CompileTimeEvaluationState? state,
            HashSet<string>? activeCalls,
            out CompileTimeConstant constant)
        {
            activeCalls ??= new HashSet<string>(StringComparer.Ordinal);
            TryResolveCompileTimeIdentifier? nameResolver = state is null
                ? null
                : new TryResolveCompileTimeIdentifier(state.TryResolve);
            TryEvaluateCompileTimePostfixExpression postfixResolver =
                (StarkParser.PostfixExpressionContext postfix, CompileTimeEvaluationServices _, out CompileTimeConstant value) =>
                    TryEvaluateCompileTimeLawCall(postfix, moduleName, state, activeCalls, out value);
            var services = new CompileTimeEvaluationServices(
                TryResolveIdentifier: nameResolver,
                TryEvaluatePostfixExpression: postfixResolver);
            return CompileTimeExpressionEvaluator.TryEvaluate(expression, out constant, services);
        }

        private static bool TryResolveCompileTimeAssignmentTarget(
            StarkParser.UnaryExpressionContext expression,
            out string name)
        {
            name = string.Empty;

            if (TryGetSimplePostfixExpression(expression) is not { } postfix
                || postfix.postfixPart().Length != 0
                || postfix.primaryExpression().Identifier() is not { } identifier)
            {
                return false;
            }

            name = identifier.GetText();
            return true;
        }

        private MidLevelIrOperand? LowerAssignmentExpressionToOperand(
            StarkParser.AssignmentExpressionContext expression,
            StarkTypeSymbol? expectedType = null)
        {
            if (expression.conditionalExpression() is { } conditionalExpression)
            {
                return LowerConditionalExpression(conditionalExpression, expectedType);
            }

            if (!TryLowerAssignmentExpression(expression, out var assignment))
            {
                MarkUnsupported();
                return null;
            }

            EmitAssignment(assignment);
            return assignment.ResultValue;
        }

        private MidLevelIrOperand? LowerConditionalExpression(
            StarkParser.ConditionalExpressionContext expression,
            StarkTypeSymbol? expectedType)
        {
            if (expression.expression().Length == 0)
            {
                return LowerLogicalOrExpression(expression.logicalOrExpression(), expectedType);
            }

            if (expression.expression().Length != 2)
            {
                MarkUnsupported();
                return null;
            }

            var condition = LowerLogicalOrExpression(expression.logicalOrExpression(), StarkTypeSymbols.Bool);
            if (condition is null)
            {
                return null;
            }

            var thenBlock = CreateBlock("cond_true");
            var elseBlock = CreateBlock("cond_false");
            var joinBlock = CreateBlock("cond_join");

            CurrentBlock.Terminator = new MidLevelIrTerminator(
                MidLevelIrTerminatorKind.Branch,
                [thenBlock.Id, elseBlock.Id],
                ConditionText: expression.logicalOrExpression().GetText(),
                Condition: condition);

            CurrentBlock = thenBlock;
            var trueValue = LowerExpressionToOperand(expression.expression(0), expectedType);
            var trueBlock = CurrentBlock;
            if (trueValue is null)
            {
                return null;
            }

            CurrentBlock = elseBlock;
            var falseValue = LowerExpressionToOperand(expression.expression(1), expectedType);
            var falseBlock = CurrentBlock;
            if (falseValue is null)
            {
                return null;
            }

            var resultType = expectedType ?? FindCommonType(trueValue.Type, falseValue.Type);
            if (resultType.Kind == StarkTypeKind.Error)
            {
                MarkUnsupported();
                return null;
            }

            var result = CreateTemporaryLocal(resultType, "cond");

            CurrentBlock = trueBlock;
            var coercedTrue = CoerceOperand(trueValue, resultType);
            if (coercedTrue is null)
            {
                return null;
            }

            EmitOperandAssignment(result, coercedTrue, expression.expression(0).GetText());
            EnsureGoto(joinBlock.Id);

            CurrentBlock = falseBlock;
            var coercedFalse = CoerceOperand(falseValue, resultType);
            if (coercedFalse is null)
            {
                return null;
            }

            EmitOperandAssignment(result, coercedFalse, expression.expression(1).GetText());
            EnsureGoto(joinBlock.Id);

            CurrentBlock = joinBlock;
            return result;
        }

        private MidLevelIrOperand? LowerLogicalOrExpression(
            StarkParser.LogicalOrExpressionContext expression,
            StarkTypeSymbol? expectedType)
        {
            if (expression.logicalAndExpression().Length == 1)
            {
                return LowerLogicalAndExpression(expression.logicalAndExpression(0), expectedType);
            }

            return LowerShortCircuitBooleanChain(
                expression.logicalAndExpression(),
                item => LowerLogicalAndExpression(item, StarkTypeSymbols.Bool),
                shortCircuitOnTrue: true,
                resultHint: "or");
        }

        private MidLevelIrOperand? LowerLogicalAndExpression(
            StarkParser.LogicalAndExpressionContext expression,
            StarkTypeSymbol? expectedType)
        {
            if (expression.bitwiseOrExpression().Length == 1)
            {
                return LowerBitwiseOrExpression(expression.bitwiseOrExpression(0), expectedType);
            }

            return LowerShortCircuitBooleanChain(
                expression.bitwiseOrExpression(),
                item => LowerBitwiseOrExpression(item, StarkTypeSymbols.Bool),
                shortCircuitOnTrue: false,
                resultHint: "and");
        }

        private MidLevelIrOperand? LowerBitwiseOrExpression(
            StarkParser.BitwiseOrExpressionContext expression,
            StarkTypeSymbol? expectedType)
        {
            var operands = expression.bitwiseXorExpression();
            var operators = ExtractOperators<StarkParser.BitwiseXorExpressionContext>(expression);
            return LowerBinaryChain(
                operands,
                operators,
                item => LowerBitwiseXorExpression(item, expectedType),
                MapBinaryOperator,
                requireInteger: true,
                expectedType);
        }

        private MidLevelIrOperand? LowerBitwiseXorExpression(
            StarkParser.BitwiseXorExpressionContext expression,
            StarkTypeSymbol? expectedType)
        {
            var operands = expression.bitwiseAndExpression();
            var operators = ExtractOperators<StarkParser.BitwiseAndExpressionContext>(expression);
            return LowerBinaryChain(
                operands,
                operators,
                item => LowerBitwiseAndExpression(item, expectedType),
                MapBinaryOperator,
                requireInteger: true,
                expectedType);
        }

        private MidLevelIrOperand? LowerBitwiseAndExpression(
            StarkParser.BitwiseAndExpressionContext expression,
            StarkTypeSymbol? expectedType)
        {
            var operands = expression.equalityExpression();
            var operators = ExtractOperators<StarkParser.EqualityExpressionContext>(expression);
            return LowerBinaryChain(
                operands,
                operators,
                item => LowerEqualityExpression(item, expectedType),
                MapBinaryOperator,
                requireInteger: true,
                expectedType);
        }

        private MidLevelIrOperand? LowerEqualityExpression(
            StarkParser.EqualityExpressionContext expression,
            StarkTypeSymbol? expectedType)
        {
            var operands = expression.relationalExpression();
            var operators = ExtractOperators<StarkParser.RelationalExpressionContext>(expression);
            return LowerComparisonChain(
                operands,
                operators,
                item => LowerRelationalExpression(item, expectedType));
        }

        private MidLevelIrOperand? LowerRelationalExpression(
            StarkParser.RelationalExpressionContext expression,
            StarkTypeSymbol? expectedType)
        {
            var operands = expression.shiftExpression();
            var operators = ExtractOperators<StarkParser.ShiftExpressionContext>(expression);
            return LowerComparisonChain(
                operands,
                operators,
                item => LowerShiftExpression(item, expectedType));
        }

        private MidLevelIrOperand? LowerShiftExpression(
            StarkParser.ShiftExpressionContext expression,
            StarkTypeSymbol? expectedType)
        {
            var operands = expression.additiveExpression();
            var operators = ExtractOperators<StarkParser.AdditiveExpressionContext>(expression);
            return LowerBinaryChain(
                operands,
                operators,
                item => LowerAdditiveExpression(item, expectedType),
                MapBinaryOperator,
                requireInteger: true,
                expectedType);
        }

        private MidLevelIrOperand? LowerAdditiveExpression(
            StarkParser.AdditiveExpressionContext expression,
            StarkTypeSymbol? expectedType)
        {
            var operands = expression.multiplicativeExpression();
            var operators = ExtractOperators<StarkParser.MultiplicativeExpressionContext>(expression);
            return LowerBinaryChain(
                operands,
                operators,
                item => LowerMultiplicativeExpression(item, expectedType),
                MapBinaryOperator,
                requireInteger: false,
                expectedType);
        }

        private MidLevelIrOperand? LowerMultiplicativeExpression(
            StarkParser.MultiplicativeExpressionContext expression,
            StarkTypeSymbol? expectedType)
        {
            var operands = expression.unaryExpression();
            var operators = ExtractOperators<StarkParser.UnaryExpressionContext>(expression);
            return LowerBinaryChain(
                operands,
                operators,
                item => LowerUnaryExpression(item, expectedType),
                MapBinaryOperator,
                requireInteger: false,
                expectedType);
        }

        private MidLevelIrOperand? LowerUnaryExpression(StarkParser.UnaryExpressionContext expression, StarkTypeSymbol? expectedType)
        {
            if (expression.powerExpression() is { } powerExpression)
            {
                return LowerPowerExpression(powerExpression, expectedType);
            }

            if (expression.conversionType() is { } conversionType)
            {
                var targetType = TryResolvePublishedConversionType(expression, out var publishedTargetType)
                    ? publishedTargetType
                    : ApplyGenericSubstitution(_typeResolver.ResolveConversionType(conversionType, _genericParameterNames, CurrentModuleName));
                var convertedOperand = LowerUnaryExpression(expression.unaryExpression(), expectedType: null);
                if (convertedOperand is null)
                {
                    return null;
                }

                var converted = CoerceOperand(convertedOperand, targetType);
                return expectedType is null ? converted : CoerceOperand(converted, expectedType);
            }

            var op = expression.unaryOperator()?.GetText() ?? expression.GetChild(0).GetText();
            if (op == "&")
            {
                var address = LowerAddressOfUnary(expression.unaryExpression());
                return expectedType is null ? address : CoerceOperand(address, expectedType);
            }

            var operand = LowerUnaryExpression(expression.unaryExpression(), expectedType: null);
            if (operand is null)
            {
                return null;
            }

            var result = op switch
            {
                "+" => operand,
                "-" => EmitTemporary(
                    new MidLevelIrUnaryRValue(MidLevelIrUnaryOperator.Negate, operand, operand.Type, expression.GetText()),
                    "neg"),
                "-%" => EmitTemporary(
                    new MidLevelIrBinaryRValue(
                        MidLevelIrBinaryOperator.WrappingSubtract,
                        new MidLevelIrIntegerConstantOperand(BigInteger.Zero, operand.Type),
                        operand,
                        operand.Type,
                        expression.GetText()),
                    "wrapneg"),
                "!" => EmitTemporary(
                    new MidLevelIrUnaryRValue(MidLevelIrUnaryOperator.LogicalNot, CoerceOperand(operand, StarkTypeSymbols.Bool) ?? operand, StarkTypeSymbols.Bool, expression.GetText()),
                    "not"),
                "~" => EmitTemporary(
                    new MidLevelIrUnaryRValue(MidLevelIrUnaryOperator.BitwiseNot, operand, operand.Type, expression.GetText()),
                    "bitnot"),
                "*" => LowerDereferenceUnary(expression, operand),
                _ => UnsupportedOperand()
            };

            return expectedType is null ? result : CoerceOperand(result, expectedType);
        }

        private MidLevelIrOperand? LowerAddressOfUnary(StarkParser.UnaryExpressionContext operandExpression)
        {
            if (operandExpression.conversionType() is null
                && operandExpression.powerExpression() is null
                && string.Equals(operandExpression.unaryOperator()?.GetText(), "*", StringComparison.Ordinal))
            {
                return LowerUnaryExpression(operandExpression.unaryExpression(), expectedType: null);
            }

            if (!TryResolveAssignmentTarget(operandExpression, out var target))
            {
                MarkUnsupported();
                return null;
            }

            return BuildAddress(target);
        }

        private MidLevelIrOperand? LowerDereferenceUnary(StarkParser.UnaryExpressionContext expression, MidLevelIrOperand operand)
        {
            if (operand.Type.Kind != StarkTypeKind.RawPointer || operand.Type.ElementType is null)
            {
                MarkUnsupported();
                return null;
            }

            return EmitTemporary(
                new MidLevelIrLoadIndirectRValue(
                    operand,
                    operand.Type.ElementType,
                    expression.GetText()),
                "load");
        }

        private MidLevelIrOperand? LowerPowerExpression(StarkParser.PowerExpressionContext expression, StarkTypeSymbol? expectedType)
        {
            var left = LowerPostfixExpression(expression.postfixExpression(), expectedType: null);
            if (left is null)
            {
                return null;
            }

            if (expression.unaryExpression() is not { } rightExpression)
            {
                return expectedType is null ? left : CoerceOperand(left, expectedType);
            }

            var right = LowerUnaryExpression(rightExpression, expectedType: null);
            if (right is null)
            {
                return null;
            }

            var resultType = FindCommonType(left.Type, right.Type);
            if (resultType.Kind is not (StarkTypeKind.Float or StarkTypeKind.Integer))
            {
                MarkUnsupported();
                return null;
            }

            var coercedLeft = CoerceOperand(left, resultType);
            var coercedRight = CoerceOperand(right, resultType);
            if (coercedLeft is null || coercedRight is null)
            {
                return null;
            }

            var result = EmitTemporary(
                new MidLevelIrBinaryRValue(
                    MidLevelIrBinaryOperator.Exponent,
                    coercedLeft,
                    coercedRight,
                    resultType,
                    expression.GetText()),
                "pow");

            if (result is null)
            {
                return null;
            }

            return expectedType is null ? result : CoerceOperand(result, expectedType);
        }

        private MidLevelIrOperand? LowerPostfixExpression(StarkParser.PostfixExpressionContext expression, StarkTypeSymbol? expectedType)
        {
            if (TryLowerCallExpression(expression, out var call))
            {
                if (call.Type.Kind == StarkTypeKind.Void)
                {
                    MarkUnsupported();
                    return null;
                }

                return EmitTemporary(call, "call");
            }

            if (!TryLowerPostfixOperand(expression, out var current))
            {
                return null;
            }

            return expectedType is null ? current : CoerceOperand(current, expectedType);
        }

        private bool TryLowerPostfixOperand(
            StarkParser.PostfixExpressionContext expression,
            out MidLevelIrOperand? result)
        {
            result = null;

            if (!TryInitializePostfixState(expression.primaryExpression(), out var currentValue, out var currentName))
            {
                return false;
            }

            for (var index = 0; index < expression.postfixPart().Length; index++)
            {
                var postfixPart = expression.postfixPart()[index];

                if (postfixPart.argumentList() is { } argumentList)
                {
                    if (TryLowerPublishedEnumCall(argumentList, out var publishedEnumCall))
                    {
                        currentValue = publishedEnumCall;
                        currentName = null;
                        if (currentValue is null)
                        {
                            return false;
                        }

                        continue;
                    }

                    if (currentName is null)
                    {
                        return false;
                    }

                    if (TryLowerEnumConstructorCall(currentName, argumentList, $"{currentName}{argumentList.GetText()}", out var enumConstructorValue))
                    {
                        currentValue = enumConstructorValue;
                        currentName = null;
                        if (currentValue is null)
                        {
                            return false;
                        }

                        continue;
                    }

                    if (!TryBuildCall(currentName, argumentList, $"{currentName}{argumentList.GetText()}", out var directCall))
                    {
                        return false;
                    }

                    if (directCall.Type.Kind == StarkTypeKind.Void)
                    {
                        MarkUnsupported();
                        return false;
                    }

                    currentValue = EmitTemporary(directCall, "call");
                    currentName = null;
                    if (currentValue is null)
                    {
                        return false;
                    }

                    continue;
                }

                if (postfixPart.GetChild(0).GetText() == "[")
                {
                    if (currentValue is null)
                    {
                        if (currentName is null)
                        {
                            return false;
                        }

                        currentValue = ResolveNamedOperand(currentName);
                        currentName = null;
                        if (currentValue is null)
                        {
                            return false;
                        }
                    }

                    if (postfixPart.expressionList() is { } expressionList)
                    {
                        currentValue = LowerIndexAccess(currentValue, expressionList);
                        if (currentValue is null)
                        {
                            return false;
                        }
                    }
                    else if (currentValue.Type.Kind is not StarkTypeKind.Ascii and not StarkTypeKind.Unicode)
                    {
                        MarkUnsupported(reason: "Index access currently requires at least one index expression.");
                        return false;
                    }

                    continue;
                }

                var memberName = postfixPart.Identifier()?.GetText();
                if (memberName is null)
                {
                    return false;
                }

                if (currentValue is not null
                    && index + 1 < expression.postfixPart().Length
                    && expression.postfixPart()[index + 1].argumentList() is { } memberArguments)
                {
                    if (!(TryBuildPublishedMemberCall(currentValue, memberArguments, $"{currentValue.Text}.{memberName}{memberArguments.GetText()}", out var memberCall)
                          || TryBuildMemberCall(currentValue, memberName, memberArguments, $"{currentValue.Text}.{memberName}{memberArguments.GetText()}", out memberCall)))
                    {
                        return false;
                    }

                    if (memberCall.Type.Kind == StarkTypeKind.Void)
                    {
                        MarkUnsupported();
                        return false;
                    }

                    currentValue = EmitTemporary(memberCall, "call");
                    currentName = null;
                    if (currentValue is null)
                    {
                        return false;
                    }

                    index++;
                    continue;
                }

                if (currentValue is not null)
                {
                    currentValue = TryLowerPublishedFieldAccess(currentValue, postfixPart, out var publishedFieldAccess)
                        ? publishedFieldAccess
                        : LowerFieldAccess(currentValue, memberName);
                    if (currentValue is null)
                    {
                        return false;
                    }

                    continue;
                }

                if (currentName is null)
                {
                    return false;
                }

                var qualifiedName = $"{currentName}.{memberName}";
                currentValue = TryResolveNamedValueOperand(qualifiedName);
                if (currentValue is not null)
                {
                    currentName = null;
                }
                else
                {
                    currentName = qualifiedName;
                }
            }

            if (currentValue is null)
            {
                if (currentName is null)
                {
                    return false;
                }

                currentValue = ResolveNamedOperand(currentName);
                if (currentValue is null)
                {
                    return false;
                }
            }

            result = currentValue;
            return true;
        }

        private bool TryInitializePostfixState(
            StarkParser.PrimaryExpressionContext expression,
            out MidLevelIrOperand? currentValue,
            out string? currentName)
        {
            currentValue = null;
            currentName = null;

            if (TryLowerPublishedEnumValue(expression, out currentValue))
            {
                currentName = null;
                return currentValue is not null;
            }

            if (expression.Identifier() is { } identifier)
            {
                currentValue = TryResolveNamedValueOperand(identifier.GetText());
                currentName = currentValue is null ? identifier.GetText() : null;
                return true;
            }

            if (expression.qualifiedName() is { } qualifiedName)
            {
                currentValue = TryResolveNamedValueOperand(qualifiedName.GetText());
                currentName = currentValue is null ? qualifiedName.GetText() : null;
                return true;
            }

            if (expression.genericEnumCaseReference() is { } genericEnumCaseReference)
            {
                if (!TryBuildGenericEnumCaseName(genericEnumCaseReference, out var genericEnumCaseName))
                {
                    return false;
                }

                currentValue = TryResolveNamedValueOperand(genericEnumCaseName);
                currentName = currentValue is null ? genericEnumCaseName : null;
                return true;
            }

            currentValue = LowerPrimaryExpression(expression, expectedType: null);
            return currentValue is not null;
        }

        private MidLevelIrOperand? LowerPrimaryExpression(StarkParser.PrimaryExpressionContext expression, StarkTypeSymbol? expectedType)
        {
            if (expression.literal() is { } literal)
            {
                return LowerLiteral(literal, expectedType);
            }

            if (expression.Identifier() is { } identifier)
            {
                return ResolveNamedOperand(identifier.GetText());
            }

            if (expression.enumConstructorExpression() is { } enumConstructorExpression)
            {
                return LowerEnumConstructorExpression(enumConstructorExpression, expectedType);
            }

            if (TryLowerPublishedEnumValue(expression, out var publishedEnumValue))
            {
                return publishedEnumValue is null || expectedType is null ? publishedEnumValue : CoerceOperand(publishedEnumValue, expectedType);
            }

            if (expression.genericEnumCaseReference() is { } genericEnumCaseReference)
            {
                return !TryBuildGenericEnumCaseName(genericEnumCaseReference, out var genericEnumCaseName)
                    ? null
                    : ResolveNamedOperand(genericEnumCaseName);
            }

            if (expression.qualifiedName() is { } qualifiedName)
            {
                return ResolveNamedOperand(qualifiedName.GetText());
            }

            if (expression.objectCreationExpression() is { } objectCreationExpression)
            {
                return LowerObjectCreationExpression(objectCreationExpression, expectedType);
            }

            return LowerExpressionToOperand(expression.expression(), expectedType);
        }

        private MidLevelIrOperand? LowerObjectCreationExpression(
            StarkParser.ObjectCreationExpressionContext expression,
            StarkTypeSymbol? expectedType)
        {
            TryGetPublishedObjectCreationSummary(expression, out var publishedObjectCreation);
            var createdType = publishedObjectCreation is not null
                ? ApplyGenericSubstitution(publishedObjectCreation.CreatedType)
                : ResolveTypeWithGenericSubstitution(expression.type_(), CurrentModuleName);
            MidLevelIrOperand current = new MidLevelIrZeroInitializerOperand(createdType);

            if (expression.argumentList() is { } argumentList && argumentList.argument().Length != 0)
            {
                var initializedFromConstructor = LowerPrimaryConstructorObjectCreation(expression, createdType, argumentList);
                if (initializedFromConstructor is null)
                {
                    return null;
                }

                current = initializedFromConstructor;
            }

            if (expression.objectInitializer() is { } objectInitializer)
            {
                var initialized = LowerObjectInitializer(
                    createdType,
                    current,
                    objectInitializer,
                    publishedObjectCreation?.InitializerMembers);
                if (initialized is null)
                {
                    return null;
                }

                current = initialized;
            }

            return expectedType is null ? current : CoerceOperand(current, expectedType);
        }

        private MidLevelIrOperand? LowerObjectInitializer(StarkTypeSymbol targetType, StarkParser.ObjectInitializerContext objectInitializer)
        {
            return LowerObjectInitializer(targetType, new MidLevelIrZeroInitializerOperand(targetType), objectInitializer, publishedInitializerMembers: null);
        }

        private MidLevelIrOperand? LowerObjectInitializer(
            StarkTypeSymbol targetType,
            MidLevelIrOperand seed,
            StarkParser.ObjectInitializerContext objectInitializer,
            IReadOnlyList<ImportedTemplateObjectInitializerMemberSummary>? publishedInitializerMembers)
        {
            if (targetType.Kind != StarkTypeKind.Named
                || targetType.NamedType is null)
            {
                MarkUnsupported();
                return null;
            }

            _typeModel.NamedTypes.TryGetValue(targetType.NamedType, out var namedType);
            var current = seed;

            for (var index = 0; index < objectInitializer.memberInitializer().Length; index++)
            {
                var initializer = objectInitializer.memberInitializer(index);
                var fieldName = initializer.Identifier().GetText();
                var fieldType = StarkTypeSymbols.Error;
                var fieldIndex = -1;

                if (publishedInitializerMembers is { Count: > 0 } && index < publishedInitializerMembers.Count)
                {
                    var publishedMember = publishedInitializerMembers[index];
                    fieldName = publishedMember.FieldName;
                    fieldIndex = publishedMember.FieldIndex;
                    fieldType = ApplyGenericSubstitution(publishedMember.FieldType);
                }
                else if (namedType is null
                         || !namedType.TryGetField(fieldName, out var field, out fieldIndex))
                {
                    MarkUnsupported();
                    return null;
                }
                else
                {
                    fieldType = field.Type;
                }

                var memberInitializer = initializer.variableInitializer();
                var value = LowerInitializerToOperand(memberInitializer, fieldType);
                if (value is null)
                {
                    return null;
                }

                var updated = EmitTemporary(
                    new MidLevelIrInsertFieldRValue(
                        current,
                        fieldName,
                        fieldIndex,
                        value,
                        targetType,
                        $"{current.Text}.{fieldName} = {memberInitializer.GetText()}"),
                    "insertfield");
                if (updated is null)
                {
                    return null;
                }

                current = updated;
            }

            return current;
        }

        private MidLevelIrOperand? LowerPrimaryConstructorObjectCreation(
            StarkParser.ObjectCreationExpressionContext expression,
            StarkTypeSymbol createdType,
            StarkParser.ArgumentListContext argumentList)
        {
            if (createdType.Kind != StarkTypeKind.Named
                || createdType.NamedType is null
                || !_typeModel.NamedTypes.TryGetValue(createdType.NamedType, out var namedType)
                || !TryGetMatchedObjectCreationConstructor(expression, out var constructor)
                || constructor is null
                || !constructor.IsPrimaryShape
                || constructor.Parameters.Count != argumentList.argument().Length)
            {
                MarkUnsupported();
                return null;
            }

            MidLevelIrOperand current = new MidLevelIrZeroInitializerOperand(createdType);

            for (var index = 0; index < constructor.Parameters.Count; index++)
            {
                var parameter = constructor.Parameters[index];
                if (!namedType.TryGetField(parameter.Name, out var field, out var fieldIndex))
                {
                    MarkUnsupported();
                    return null;
                }

                var loweredArgument = LowerExpressionToOperand(argumentList.argument(index).expression(), parameter.Type);
                if (loweredArgument is null)
                {
                    return null;
                }

                var fieldValue = CoerceOperand(loweredArgument, field.Type);
                if (fieldValue is null)
                {
                    return null;
                }

                var updated = EmitTemporary(
                    new MidLevelIrInsertFieldRValue(
                        current,
                        field.Name,
                        fieldIndex,
                        fieldValue,
                        createdType,
                        $"{current.Text}.{field.Name} = {argumentList.argument(index).GetText()}"),
                    "insertfield");
                if (updated is null)
                {
                    return null;
                }

                current = updated;
            }

            return current;
        }

        private MidLevelIrOperand? LowerEnumConstructorExpression(
            StarkParser.EnumConstructorExpressionContext expression,
            StarkTypeSymbol? expectedType)
        {
            string constructorName;
            StarkTypeSymbol enumType;
            EnumLayoutSymbol layout;
            EnumVariantLayoutSymbol variant;
            ImportedTemplateEnumConstructorSummary? publishedEnumConstructor = null;

            if (TryGetPublishedEnumConstructorSummary(expression, out var publishedSummary)
                && publishedSummary is not null)
            {
                publishedEnumConstructor = publishedSummary;
                enumType = ApplyGenericSubstitution(publishedEnumConstructor.EnumType);
                constructorName = $"{enumType.DisplayName}.{publishedEnumConstructor.VariantName}";

                if (!TryGetEnumLayout(enumType, out layout)
                    || !layout.TryGetVariant(publishedEnumConstructor.VariantName, out variant))
                {
                    MarkUnsupported();
                    return null;
                }
            }
            else
            {
                constructorName = expression.enumCaseTarget().GetText();
                if (!TryResolveEnumCaseTarget(expression.enumCaseTarget(), out _, out enumType, out layout, out variant))
                {
                    MarkUnsupported();
                    return null;
                }
            }

            if (!variant.UsesNamedFields)
            {
                MarkUnsupported();
                return null;
            }

            var memberValues = new Dictionary<int, MidLevelIrOperand>();
            for (var memberOrdinal = 0; memberOrdinal < expression.enumConstructorInitializer().enumConstructorMember().Length; memberOrdinal++)
            {
                var member = expression.enumConstructorInitializer().enumConstructorMember(memberOrdinal);
                var memberName = member.Identifier().GetText();
                EnumVariantLayoutFieldSymbol? layoutField = null;
                var fieldIndex = -1;

                if (publishedEnumConstructor is not null && memberOrdinal < publishedEnumConstructor.Members.Count)
                {
                    var publishedMember = publishedEnumConstructor.Members[memberOrdinal];
                    memberName = publishedMember.FieldName;
                    fieldIndex = publishedMember.FieldIndex;
                    if (fieldIndex >= 0 && fieldIndex < variant.Fields.Count)
                    {
                        layoutField = variant.Fields[fieldIndex];
                    }
                }
                else
                {
                    for (var fieldOrdinal = 0; fieldOrdinal < variant.Fields.Count; fieldOrdinal++)
                    {
                        var candidate = variant.Fields[fieldOrdinal];
                        if (!string.Equals(candidate.SourceFieldName, memberName, StringComparison.Ordinal))
                        {
                            continue;
                        }

                        layoutField = candidate;
                        fieldIndex = fieldOrdinal;
                        break;
                    }
                }

                if (layoutField is null)
                {
                    MarkUnsupported();
                    return null;
                }

                var value = LowerExpressionToOperand(member.expression(), layoutField.Type);
                if (value is null)
                {
                    return null;
                }

                var coerced = CoerceOperand(value, layoutField.Type);
                if (coerced is null)
                {
                    return null;
                }

                memberValues[fieldIndex] = coerced;
            }

            var orderedValues = new MidLevelIrOperand[variant.Fields.Count];
            for (var index = 0; index < variant.Fields.Count; index++)
            {
                if (!memberValues.TryGetValue(index, out var value))
                {
                    MarkUnsupported();
                    return null;
                }

                orderedValues[index] = value;
            }

            var lowered = LowerDirectTagEnumConstructor(enumType, layout, variant, orderedValues, expression.GetText());
            return lowered is null || expectedType is null ? lowered : CoerceOperand(lowered, expectedType);
        }

        private bool TryLowerPublishedEnumCall(
            StarkParser.ArgumentListContext arguments,
            out MidLevelIrOperand? value)
        {
            value = null;

            if (!TryResolvePublishedEnumCallSummary(arguments, out var publishedEnumCall))
            {
                return false;
            }

            var publishedEnumType = ApplyGenericSubstitution(publishedEnumCall.EnumType);
            var publishedCaseName = $"{publishedEnumType.DisplayName}.{publishedEnumCall.VariantName}";
            if (!TryResolveEnumCaseReference(publishedCaseName, out var enumType, out var layout, out var variant)
                || variant.UsesNamedFields)
            {
                MarkUnsupported();
                return true;
            }

            if (variant.Fields.Count != arguments.argument().Length)
            {
                MarkUnsupported();
                return true;
            }

            var loweredArguments = new MidLevelIrOperand[variant.Fields.Count];
            for (var index = 0; index < variant.Fields.Count; index++)
            {
                var field = variant.Fields[index];
                var argument = LowerExpressionToOperand(arguments.argument(index).expression(), field.Type);
                if (argument is null)
                {
                    return true;
                }

                var coerced = CoerceOperand(argument, field.Type);
                if (coerced is null)
                {
                    return true;
                }

                loweredArguments[index] = coerced;
            }

            value = LowerDirectTagEnumConstructor(enumType, layout, variant, loweredArguments, $"{publishedCaseName}{arguments.GetText()}");
            return true;
        }

        private bool TryLowerPublishedEnumValue(
            StarkParser.PrimaryExpressionContext expression,
            out MidLevelIrOperand? value)
        {
            value = null;

            if (!TryResolvePublishedEnumValueSummary(expression, out var publishedEnumValue))
            {
                return false;
            }

            var publishedEnumType = ApplyGenericSubstitution(publishedEnumValue.EnumType);
            var publishedCaseName = $"{publishedEnumType.DisplayName}.{publishedEnumValue.VariantName}";
            if (!TryResolveEnumCaseReference(publishedCaseName, out var enumType, out var layout, out var variant)
                || variant.Fields.Count != 0)
            {
                MarkUnsupported();
                return true;
            }

            value = LowerDirectTagEnumConstructor(enumType, layout, variant, [], publishedCaseName);
            return true;
        }

        private bool TryLowerEnumConstructorCall(
            string constructorName,
            StarkParser.ArgumentListContext arguments,
            string text,
            out MidLevelIrOperand? value)
        {
            value = null;

            if (!TryResolveEnumCaseReference(constructorName, out var enumType, out var layout, out var variant)
                || variant.UsesNamedFields)
            {
                return false;
            }

            if (variant.Fields.Count != arguments.argument().Length)
            {
                MarkUnsupported();
                return true;
            }

            var loweredArguments = new MidLevelIrOperand[variant.Fields.Count];
            for (var index = 0; index < variant.Fields.Count; index++)
            {
                var field = variant.Fields[index];
                var argument = LowerExpressionToOperand(arguments.argument(index).expression(), field.Type);
                if (argument is null)
                {
                    value = null;
                    return true;
                }

                var coerced = CoerceOperand(argument, field.Type);
                if (coerced is null)
                {
                    value = null;
                    return true;
                }

                loweredArguments[index] = coerced;
            }

            value = LowerDirectTagEnumConstructor(enumType, layout, variant, loweredArguments, text);
            return true;
        }

        private MidLevelIrOperand? LowerDirectTagEnumConstructor(
            StarkTypeSymbol enumType,
            EnumLayoutSymbol layout,
            EnumVariantLayoutSymbol variant,
            IReadOnlyList<MidLevelIrOperand> payloadValues,
            string text)
        {
            MidLevelIrOperand current = new MidLevelIrZeroInitializerOperand(enumType);
            var tagValue = new MidLevelIrIntegerConstantOperand(new BigInteger(variant.TagValue), layout.TagField.Type);

            var withTag = EmitTemporary(
                new MidLevelIrInsertFieldRValue(
                    current,
                    layout.TagField.Name,
                    0,
                    tagValue,
                    enumType,
                    $"{text}.$tag = {variant.TagValue}"),
                "enumtag");
            if (withTag is null)
            {
                return null;
            }

            current = withTag;

            for (var index = 0; index < variant.Fields.Count; index++)
            {
                var field = variant.Fields[index];
                var updated = EmitTemporary(
                    new MidLevelIrInsertFieldRValue(
                        current,
                        field.StorageFieldName,
                        field.StorageFieldIndex,
                        payloadValues[index],
                        enumType,
                        field.SourceFieldName is null
                            ? $"{text}[{index}] = {payloadValues[index].Text}"
                            : $"{text}.{field.SourceFieldName} = {payloadValues[index].Text}"),
                    "enumfield");
                if (updated is null)
                {
                    return null;
                }

                current = updated;
            }

            return current;
        }

        private bool TryGetPublishedEnumConstructorSummary(
            StarkParser.EnumConstructorExpressionContext expression,
            out ImportedTemplateEnumConstructorSummary? summary)
        {
            if (_importedEnumConstructorOrdinals is null
                || !_importedEnumConstructorOrdinals.TryGetValue(expression, out var ordinal)
                || !_importedTemplateEnumConstructors.TryGetValue(ordinal, out var publishedSummary))
            {
                summary = null;
                return false;
            }

            summary = publishedSummary;
            return true;
        }

        private bool TryGetMatchedObjectCreationConstructor(
            StarkParser.ObjectCreationExpressionContext expression,
            out TypedConstructorShape? constructor)
        {
            if (TryGetPublishedObjectCreationSummary(expression, out var importedObjectCreation))
            {
                constructor = importedObjectCreation.Constructor;
                return true;
            }

            return _objectCreationConstructors.TryGetValue(
                new ObjectCreationKey(
                    expression.GetText(),
                    expression.Start.Line,
                    expression.Start.Column + 1),
                out constructor);
        }

        private bool TryGetPublishedObjectCreationSummary(
            StarkParser.ObjectCreationExpressionContext expression,
            out ImportedTemplateObjectCreationSummary importedObjectCreation)
        {
            importedObjectCreation = default!;

            if (_importedTemplateSummary is not { ObjectCreations.Count: > 0 } importedTemplateSummary
                || _importedObjectCreationOrdinals is null
                || !_importedObjectCreationOrdinals.TryGetValue(expression, out var ordinal)
                || ordinal >= importedTemplateSummary.ObjectCreations.Count)
            {
                return false;
            }

            importedObjectCreation = importedTemplateSummary.ObjectCreations[ordinal];
            return true;
        }

        private MidLevelIrOperand? LowerArrayInitializer(StarkTypeSymbol targetType, StarkParser.ArrayInitializerContext arrayInitializer)
        {
            if (targetType.Kind != StarkTypeKind.FixedArray
                || targetType.ElementType is null
                || targetType.FixedLength is not int fixedLength)
            {
                MarkUnsupported();
                return null;
            }

            MidLevelIrOperand current = new MidLevelIrZeroInitializerOperand(targetType);
            var elementCount = Math.Min(fixedLength, arrayInitializer.variableInitializer().Length);

            for (var index = 0; index < elementCount; index++)
            {
                var elementInitializer = arrayInitializer.variableInitializer(index);
                var value = LowerInitializerToOperand(elementInitializer, targetType.ElementType);
                if (value is null)
                {
                    return null;
                }

                var updated = EmitTemporary(
                    new MidLevelIrInsertIndexRValue(
                        current,
                        index,
                        value,
                        targetType,
                        $"{current.Text}[{index}] = {elementInitializer.GetText()}"),
                    "insertindex");
                if (updated is null)
                {
                    return null;
                }

                current = updated;
            }

            return current;
        }

        private MidLevelIrOperand? LowerFieldAccess(MidLevelIrOperand target, string memberName)
        {
            if (!TryResolveField(target.Type, memberName, out var field, out var fieldIndex))
            {
                MarkUnsupported();
                return null;
            }

            var projectedType = ProjectFrozenView(target.Type, field.Type);

            return EmitTemporary(
                new MidLevelIrExtractFieldRValue(
                    target,
                    field.Name,
                    fieldIndex,
                    projectedType,
                    $"{target.Text}.{field.Name}"),
                "field");
        }

        private MidLevelIrOperand LowerKnownFieldAccess(
            MidLevelIrOperand target,
            string fieldName,
            int fieldIndex,
            StarkTypeSymbol fieldType,
            string displayFieldName)
        {
            var projectedType = ProjectFrozenView(target.Type, fieldType);
            return EmitRequiredTemporary(
                new MidLevelIrExtractFieldRValue(
                    target,
                    fieldName,
                    fieldIndex,
                    projectedType,
                    $"{target.Text}.{displayFieldName}"),
                "field");
        }

        private MidLevelIrOperand? LowerIndexAccess(MidLevelIrOperand target, StarkParser.ExpressionListContext indexes)
        {
            if (CanUsePartitionedTextSwitchType(target.Type))
            {
                return LowerTextAccess(target, indexes);
            }

            var current = target;

            foreach (var indexExpression in indexes.expression())
            {
                if (current.Type.Kind == StarkTypeKind.FixedArray && current.Type.ElementType is not null)
                {
                    if (TryResolveConstantArrayIndex(current.Type, indexExpression, out var constantIndex, out var resolvedElementType))
                    {
                        var elementType = ProjectFrozenView(current.Type, resolvedElementType);
                        var extracted = EmitTemporary(
                            new MidLevelIrExtractIndexRValue(
                                current,
                                constantIndex,
                                elementType,
                                $"{current.Text}[{constantIndex}]"),
                            "index");
                        if (extracted is null)
                        {
                            return null;
                        }

                        current = extracted;
                        continue;
                    }

                    if (current.Type.ElementType is null)
                    {
                        MarkUnsupported(indexes, "Dynamic fixed-array indexing currently requires an addressable fixed-array source.");
                        return null;
                    }

                    var projectedElementType = ProjectFrozenView(current.Type, current.Type.ElementType);
                    var index = LowerExpressionToOperand(indexExpression);
                    if (index is null || index.Type.Kind != StarkTypeKind.Integer)
                    {
                        MarkUnsupported(indexExpression, "Dynamic fixed-array indexing requires an integer index operand.");
                        return null;
                    }

                    var baseAddress = TryCreateDynamicFixedArrayBaseAddress(current);
                    if (baseAddress is null)
                    {
                        MarkUnsupported(indexes, "Dynamic fixed-array indexing currently requires an addressable fixed-array source.");
                        return null;
                    }

                    var elementAddress = EmitTemporary(
                        new MidLevelIrElementAddressRValue(
                            baseAddress,
                            current.Type,
                            index,
                            ConstantIndex: null,
                            AddressType(projectedElementType, isMutable: CanMutateThroughType(current.Type)),
                            $"{current.Text}[{indexExpression.GetText()}]"),
                        "addr");
                    if (elementAddress is null)
                    {
                        return null;
                    }

                    var loaded = EmitTemporary(
                        new MidLevelIrLoadIndirectRValue(
                            elementAddress,
                            projectedElementType,
                            $"{current.Text}[{indexExpression.GetText()}]"),
                        "load");
                    if (loaded is null)
                    {
                        return null;
                    }

                    current = loaded;
                    continue;
                }

                if (current.Type.Kind == StarkTypeKind.Slice && current.Type.ElementType is not null)
                {
                    var elementType = ProjectFrozenView(current.Type, current.Type.ElementType);
                    var index = LowerExpressionToOperand(indexExpression);
                    if (index is null || index.Type.Kind != StarkTypeKind.Integer)
                    {
                        MarkUnsupported(indexExpression, "Slice indexing requires an integer index operand.");
                        return null;
                    }

                    var elementAddress = EmitTemporary(
                        new MidLevelIrSliceElementAddressRValue(
                            current,
                            index,
                            AddressType(elementType, current.Type.IsMutableView && CanMutateThroughType(current.Type)),
                            $"{current.Text}[{indexExpression.GetText()}]"),
                        "addr");
                    if (elementAddress is null)
                    {
                        return null;
                    }

                    var loaded = EmitTemporary(
                        new MidLevelIrLoadIndirectRValue(
                            elementAddress,
                            elementType,
                            $"{current.Text}[{indexExpression.GetText()}]"),
                        "load");
                    if (loaded is null)
                    {
                        return null;
                    }

                    current = loaded;
                    continue;
                }

                if (current.Type.Kind == StarkTypeKind.RawPointer && current.Type.ElementType is not null)
                {
                    var elementType = current.Type.ElementType;
                    var index = LowerExpressionToOperand(indexExpression);
                    if (index is null || index.Type.Kind != StarkTypeKind.Integer)
                    {
                        MarkUnsupported(indexExpression, "Raw pointer indexing requires an integer index operand.");
                        return null;
                    }

                    var elementAddress = EmitTemporary(
                        new MidLevelIrElementAddressRValue(
                            current,
                            elementType,
                            index,
                            ConstantIndex: null,
                            AddressType(elementType, current.Type.IsMutablePointer && CanMutateThroughType(elementType)),
                            $"{current.Text}[{indexExpression.GetText()}]"),
                        "addr");
                    if (elementAddress is null)
                    {
                        return null;
                    }

                    var loaded = EmitTemporary(
                        new MidLevelIrLoadIndirectRValue(
                            elementAddress,
                            elementType,
                            $"{current.Text}[{indexExpression.GetText()}]"),
                        "load");
                    if (loaded is null)
                    {
                        return null;
                    }

                    current = loaded;
                    continue;
                }

                MarkUnsupported(indexes, "Indexing is only supported for fixed arrays, raw pointers, slices, ascii, and unicode values.");
                return null;
            }

            return current;
        }

        private MidLevelIrOperand? LowerTextAccess(MidLevelIrOperand target, StarkParser.ExpressionListContext indexes)
        {
            var indexExpressions = indexes.expression();
            if (indexExpressions.Length == 0)
            {
                return target;
            }

            if (indexExpressions.Length == 1)
            {
                var start = LowerExpressionToOperand(indexExpressions[0]);
                if (start is null || start.Type.Kind != StarkTypeKind.Integer)
                {
                    MarkUnsupported(indexes, "Text indexing currently requires an integer index operand.");
                    return null;
                }

                return LowerTextSlice(
                    target,
                    start,
                    new MidLevelIrIntegerConstantOperand(BigInteger.One, StarkTypeSymbols.Integer(64)),
                    $"{target.Text}[{indexExpressions[0].GetText()}]");
            }

            if (indexExpressions.Length != 2)
            {
                MarkUnsupported(indexes, "Text indexing currently requires exactly one integer index or two integer indices.");
                return null;
            }

            var sliceStart = LowerExpressionToOperand(indexExpressions[0]);
            var sliceLength = LowerExpressionToOperand(indexExpressions[1]);
            if (sliceStart is null
                || sliceLength is null
                || sliceStart.Type.Kind != StarkTypeKind.Integer
                || sliceLength.Type.Kind != StarkTypeKind.Integer)
            {
                MarkUnsupported(indexes, "Text slicing currently requires integer start and length operands.");
                return null;
            }

            return LowerTextSlice(
                target,
                sliceStart,
                sliceLength,
                $"{target.Text}[{indexExpressions[0].GetText()}, {indexExpressions[1].GetText()}]");
        }

        private MidLevelIrOperand? LowerTextSlice(
            MidLevelIrOperand target,
            MidLevelIrOperand start,
            MidLevelIrOperand length,
            string text)
        {
            var coercedStart = CoerceOperand(start, StarkTypeSymbols.Integer(64));
            var coercedLength = CoerceOperand(length, StarkTypeSymbols.Integer(64));
            if (coercedStart is null || coercedLength is null)
            {
                return null;
            }

            return EmitTemporary(
                new MidLevelIrTextSliceRValue(
                    target,
                    coercedStart,
                    coercedLength,
                    target.Type,
                    text),
                "slice");
        }

        private bool TryLowerCallExpression(StarkParser.PostfixExpressionContext expression, out MidLevelIrCallRValue call)
        {
            call = default!;

            if (expression.postfixPart().Length == 0
                || expression.postfixPart()[^1].argumentList() is not { } arguments)
            {
                return false;
            }

            if (!TryInitializePostfixState(expression.primaryExpression(), out var currentValue, out var currentName))
            {
                return false;
            }

            for (var index = 0; index < expression.postfixPart().Length; index++)
            {
                var postfixPart = expression.postfixPart()[index];

                if (postfixPart.argumentList() is { } argumentList)
                {
                    if (currentName is null
                        || !TryBuildCall(currentName, argumentList, $"{currentName}{argumentList.GetText()}", out var directCall))
                    {
                        return false;
                    }

                    if (index == expression.postfixPart().Length - 1)
                    {
                        call = directCall;
                        return true;
                    }

                    if (directCall.Type.Kind == StarkTypeKind.Void)
                    {
                        return false;
                    }

                    currentValue = EmitTemporary(directCall, "call");
                    currentName = null;
                    if (currentValue is null)
                    {
                        return false;
                    }

                    continue;
                }

                if (postfixPart.expressionList() is { } expressionList)
                {
                    if (currentValue is null)
                    {
                        return false;
                    }

                    currentValue = LowerIndexAccess(currentValue, expressionList);
                    if (currentValue is null)
                    {
                        return false;
                    }

                    continue;
                }

                var memberName = postfixPart.Identifier()?.GetText();
                if (memberName is null)
                {
                    return false;
                }

                if (currentValue is not null
                    && index + 1 < expression.postfixPart().Length
                    && expression.postfixPart()[index + 1].argumentList() is { } memberArguments)
                {
                    if (!TryBuildMemberCall(currentValue, memberName, memberArguments, $"{currentValue.Text}.{memberName}{memberArguments.GetText()}", out var memberCall))
                    {
                        return false;
                    }

                    if (index + 1 == expression.postfixPart().Length - 1)
                    {
                        call = memberCall;
                        return true;
                    }

                    if (memberCall.Type.Kind == StarkTypeKind.Void)
                    {
                        return false;
                    }

                    currentValue = EmitTemporary(memberCall, "call");
                    currentName = null;
                    if (currentValue is null)
                    {
                        return false;
                    }

                    index++;
                    continue;
                }

                if (currentValue is not null)
                {
                    currentValue = LowerFieldAccess(currentValue, memberName);
                    if (currentValue is null)
                    {
                        return false;
                    }

                    continue;
                }

                if (currentName is null)
                {
                    return false;
                }

                currentName = $"{currentName}.{memberName}";
            }

            return false;
        }

        private bool TryBuildCall(
            string functionName,
            StarkParser.ArgumentListContext arguments,
            string text,
            out MidLevelIrCallRValue call)
        {
            call = default!;

            if (TryGetFunctionOverloads(functionName, out var overloads))
            {
                return TryBuildOverloadedCall(overloads, receiver: null, arguments, text, out call);
            }

            if (!TryResolveFunctionSignature(functionName, out var signature))
            {
                if (!TryResolvePublishedDirectCallSignature(arguments, out signature))
                {
                    return false;
                }
            }

            return TryBuildCall(signature.Name, signature, receiver: null, arguments, text, out call);
        }

        private bool TryBuildMemberCall(
            MidLevelIrOperand receiver,
            string memberName,
            StarkParser.ArgumentListContext arguments,
            string text,
            out MidLevelIrCallRValue call)
        {
            call = default!;

            if (receiver.Type.NamedType is not { } namedTypeName)
            {
                return false;
            }

            var sourceName = $"{namedTypeName}.{memberName}";
            if (TryGetFunctionOverloads(sourceName, out var overloads))
            {
                return TryBuildOverloadedCall(overloads, receiver, arguments, text, out call);
            }

            if (!TryResolveFunctionSignature(sourceName, out var signature)
                || signature.Parameters.Count == 0)
            {
                return false;
            }

            return TryBuildCall(signature.Name, signature, receiver, arguments, text, out call);
        }

        private bool TryBuildPublishedMemberCall(
            MidLevelIrOperand receiver,
            StarkParser.ArgumentListContext arguments,
            string text,
            out MidLevelIrCallRValue call)
        {
            call = default!;

            if (_importedMemberCallOrdinals is not { } memberCallOrdinals
                || !memberCallOrdinals.TryGetValue(arguments, out var memberCallOrdinal)
                || !_importedTemplateMemberCalls.TryGetValue(memberCallOrdinal, out var publishedSignature))
            {
                return false;
            }

            var signature = ApplyGenericSubstitution(publishedSignature);
            return TryBuildCall(signature.Name, signature, receiver, arguments, text, out call);
        }

        private bool TryBuildOverloadedCall(
            IReadOnlyList<TypedFunctionSignature> overloads,
            MidLevelIrOperand? receiver,
            StarkParser.ArgumentListContext arguments,
            string text,
            out MidLevelIrCallRValue call)
        {
            call = default!;

            var loweredArguments = new List<MidLevelIrOperand>(arguments.argument().Length);
            foreach (var argument in arguments.argument())
            {
                var lowered = LowerExpressionToOperand(argument.expression(), expectedType: null);
                if (lowered is null)
                {
                    return false;
                }

                loweredArguments.Add(lowered);
            }

            var resolution = FunctionOverloadFacts.Resolve(
                overloads,
                receiver?.Type,
                loweredArguments.Select(static argument => argument.Type).ToArray(),
                TypeCompatibilityFacts.CanAssign);
            if (!resolution.Succeeded)
            {
                return false;
            }

            return TryBuildCall(
                resolution.Match!.Name,
                resolution.Match,
                receiver,
                arguments,
                text,
                out call,
                loweredArguments);
        }

        private bool TryBuildCall(
            string functionName,
            TypedFunctionSignature signature,
            MidLevelIrOperand? receiver,
            StarkParser.ArgumentListContext arguments,
            string text,
            out MidLevelIrCallRValue call,
            IReadOnlyList<MidLevelIrOperand>? loweredExplicitArguments = null)
        {
            var explicitArguments = new List<MidLevelIrOperand>(Math.Max(
                loweredExplicitArguments?.Count ?? 0,
                arguments.argument().Length));
            if (loweredExplicitArguments is not null)
            {
                explicitArguments.AddRange(loweredExplicitArguments);
            }
            else
            {
                foreach (var argument in arguments.argument())
                {
                    var lowered = LowerExpressionToOperand(argument.expression(), expectedType: null);
                    if (lowered is null)
                    {
                        call = default!;
                        return false;
                    }

                    explicitArguments.Add(lowered);
                }
            }

            return TryBuildCall(functionName, signature, receiver, text, out call, explicitArguments);
        }

        private bool TryBuildCall(
            string functionName,
            TypedFunctionSignature signature,
            MidLevelIrOperand? receiver,
            string text,
            out MidLevelIrCallRValue call,
            IReadOnlyList<MidLevelIrOperand> loweredExplicitArguments)
        {
            call = default!;

            var loweredArguments = new List<MidLevelIrOperand>();
            var indirectArgumentLocals = new List<string?>();
            var receiverOffset = receiver is null ? 0 : 1;
            var explicitParameterCount = Math.Max(0, signature.Parameters.Count - receiverOffset);

            if (receiver is not null)
            {
                var receiverOperand = CoerceOperand(receiver, signature.Parameters[0].Type);
                if (receiverOperand is null)
                {
                    return false;
                }

                loweredArguments.Add(receiverOperand);
                indirectArgumentLocals.Add(ResolveIndirectArgumentLocal(signature.Parameters[0].Type, receiverOperand));
                RecordMoveFromOperand(receiverOperand, signature.Parameters[0].Type);
            }

            for (var index = 0; index < Math.Min(loweredExplicitArguments.Count, explicitParameterCount); index++)
            {
                var parameterType = signature.Parameters[index + receiverOffset].Type;
                var argument = CoerceOperand(loweredExplicitArguments[index], parameterType);
                if (argument is null)
                {
                    return false;
                }

                loweredArguments.Add(argument);
                indirectArgumentLocals.Add(ResolveIndirectArgumentLocal(parameterType, argument));
                RecordMoveFromOperand(argument, parameterType);
            }

            if (loweredExplicitArguments.Count != explicitParameterCount)
            {
                return false;
            }

            var loweredFunctionName = ResolveCallTargetName(functionName, signature);
            call = new MidLevelIrCallRValue(
                loweredFunctionName,
                loweredArguments,
                signature.ReturnType,
                text,
                indirectArgumentLocals);
            return true;
        }

        private string ResolveCallTargetName(string fallbackFunctionName, TypedFunctionSignature signature)
        {
            if (!signature.IsGenericInstantiation
                || signature.TemplateName is not { } templateName
                || signature.TypeArguments is not { Count: > 0 } typeArguments)
            {
                return fallbackFunctionName;
            }

            var specializationKey = MidLevelIrLowerer.BuildMaterializedSpecializationKey(templateName, typeArguments);
            return _materializedSpecializationSymbols.TryGetValue(specializationKey, out var materializedSymbol)
                ? materializedSymbol
                : fallbackFunctionName;
        }

        private MidLevelIrOperand? LowerLiteral(StarkParser.LiteralContext literal, StarkTypeSymbol? expectedType)
        {
            var literalType = LookupLiteralType(literal);
            var operand = CreateLiteralOperand(literal.GetText(), literalType);
            return expectedType is null ? operand : CoerceOperand(operand, expectedType);
        }

        private static MidLevelIrOperand CreateCompileTimeOperand(CompileTimeConstant constant)
        {
            return constant.Kind switch
            {
                CompileTimeConstantKind.Integer => new MidLevelIrIntegerConstantOperand(constant.IntegerValue, constant.Type),
                CompileTimeConstantKind.Float => new MidLevelIrFloatConstantOperand(CompileTimeExpressionEvaluator.FormatFloatLiteral(constant), constant.Type),
                CompileTimeConstantKind.Bool => new MidLevelIrBoolConstantOperand(constant.BoolValue),
                CompileTimeConstantKind.Null => new MidLevelIrNullOperand(constant.Type),
                CompileTimeConstantKind.Text when constant.TextLiteral is not null => new MidLevelIrStringConstantOperand(constant.TextLiteral, constant.Type),
                _ => throw new InvalidOperationException($"Unsupported compile-time constant kind '{constant.Kind}'.")
            };
        }

        private StarkTypeSymbol LookupLiteralType(StarkParser.LiteralContext literal)
        {
            var key = new LiteralKey(literal.GetText(), literal.Start.Line, literal.Start.Column + 1);
            return _literalTypes.TryGetValue(key, out var type)
                ? type
                : literal.TRUE() is not null || literal.FALSE() is not null
                    ? StarkTypeSymbols.Bool
                    : literal.NULL() is not null
                        ? StarkTypeSymbols.Null
                        : literal.FloatLiteral() is not null
                            ? StarkTypeSymbols.Float(32)
                            : literal.StringLiteral() is not null
                                ? InferTextLiteralType(literal.GetText(), TextLiteralKind.String)
                                : literal.CharacterLiteral() is not null
                                    ? InferTextLiteralType(literal.GetText(), TextLiteralKind.Character)
                                    : InferIntegerLiteralType(ParseIntegerLiteral(literal.signedIntegerLiteral()!));
        }

        private static MidLevelIrOperand CreateLiteralOperand(string literalText, StarkTypeSymbol type)
        {
            if (literalText.Length > 0 && literalText[0] == '\'')
            {
                return new MidLevelIrStringConstantOperand(literalText, type);
            }

            if (literalText.Length > 0 && literalText[0] == '"')
            {
                return new MidLevelIrStringConstantOperand(literalText, type);
            }

            if (string.Equals(literalText, "true", StringComparison.Ordinal))
            {
                return new MidLevelIrBoolConstantOperand(true);
            }

            if (string.Equals(literalText, "false", StringComparison.Ordinal))
            {
                return new MidLevelIrBoolConstantOperand(false);
            }

            if (string.Equals(literalText, "null", StringComparison.Ordinal))
            {
                return new MidLevelIrNullOperand(type);
            }

            if (type.Kind == StarkTypeKind.Float)
            {
                return new MidLevelIrFloatConstantOperand(literalText, type);
            }

            return new MidLevelIrIntegerConstantOperand(ParseIntegerLiteralText(literalText), type);
        }

        private static StarkTypeSymbol InferTextLiteralType(string text, TextLiteralKind kind)
        {
            return TextLiteralDecoder.CanUseUtf8Storage(text, kind)
                ? StarkTypeSymbols.Ascii
                : StarkTypeSymbols.Unicode;
        }

        private MidLevelIrOperand? ResolveNamedOperand(string name)
        {
            var operand = TryResolveNamedValueOperand(name);
            if (operand is not null)
            {
                return operand;
            }

            if (TryResolveFunctionSignature(name, out _))
            {
                MarkUnsupported();
                return null;
            }

            MarkUnsupported();
            return null;
        }

        private MidLevelIrOperand? TryResolveNamedValueOperand(string name)
        {
            if (_nameAliases.TryGetValue(name, out var aliasedName))
            {
                name = aliasedName;
            }

            if (_localsByName.TryGetValue(name, out var local))
            {
                return new MidLevelIrLocalOperand(local.Name, local.Type);
            }

            if (_parametersByName.TryGetValue(name, out var parameter))
            {
                return new MidLevelIrParameterOperand(parameter.Name, parameter.Type);
            }

            if (TryResolveGlobal(name, out var global))
            {
                return new MidLevelIrGlobalOperand(global.Name, global.Type);
            }

            if (TryResolveEnumCaseReference(name, out var enumType, out var enumLayout, out var variant) && variant.Fields.Count == 0)
            {
                return LowerDirectTagEnumConstructor(enumType, enumLayout, variant, [], name);
            }

            return null;
        }

        private bool TryGetFunctionOverloads(string sourceName, out IReadOnlyList<TypedFunctionSignature> overloads)
        {
            return TryGetFunctionOverloads(sourceName, CurrentModuleName, out overloads);
        }

        private bool TryGetFunctionOverloads(string sourceName, string currentModuleName, out IReadOnlyList<TypedFunctionSignature> overloads)
        {
            if (!sourceName.Contains('.', StringComparison.Ordinal)
                && _typeModel.Overloads.TryGetValue($"{currentModuleName}.{sourceName}", out overloads!))
            {
                return true;
            }

            if (_typeModel.Overloads.TryGetValue(sourceName, out overloads!))
            {
                return true;
            }

            overloads = [];
            return false;
        }

        private bool TryResolveFunctionSignature(string name, out TypedFunctionSignature signature)
        {
            return TryResolveFunctionSignature(name, CurrentModuleName, out signature);
        }

        private bool TryResolveFunctionSignature(string name, string currentModuleName, out TypedFunctionSignature signature)
        {
            if (!name.Contains('.', StringComparison.Ordinal)
                && _typeModel.Functions.TryGetValue($"{currentModuleName}.{name}", out signature!))
            {
                return true;
            }

            if (_typeModel.Functions.TryGetValue(name, out signature!))
            {
                return true;
            }

            if (TryGetFunctionOverloads(name, currentModuleName, out var overloads) && overloads.Count == 1)
            {
                signature = overloads[0];
                return true;
            }

            if (!name.Contains('.', StringComparison.Ordinal)
                && _fallbackFunctions.TryGetValue($"{currentModuleName}.{name}", out signature!))
            {
                return true;
            }

            return _fallbackFunctions.TryGetValue(name, out signature!);
        }

        private bool TryResolveGlobal(string name, out TypedGlobalSymbol global)
        {
            if (!name.Contains('.', StringComparison.Ordinal)
                && _typeModel.Globals.TryGetValue($"{CurrentModuleName}.{name}", out global!))
            {
                return true;
            }

            if (_typeModel.Globals.TryGetValue(name, out global!))
            {
                return true;
            }

            if (!name.Contains('.', StringComparison.Ordinal)
                && _fallbackGlobals.TryGetValue($"{CurrentModuleName}.{name}", out global!))
            {
                return true;
            }

            return _fallbackGlobals.TryGetValue(name, out global!);
        }

        private StarkTypeSymbol ResolveTypeWithGenericSubstitution(
            StarkParser.Type_Context type,
            string? moduleName)
        {
            return ApplyGenericSubstitution(
                _typeResolver.ResolveType(type, _genericParameterNames, moduleName));
        }

        private bool TryResolvePublishedLocalDeclarationType(
            string declarationKind,
            ParserRuleContext declarationContext,
            out StarkTypeSymbol type)
        {
            if (_importedTemplateLocalDeclarations.TryGetValue(
                    TemplateLocalDeclarationFacts.BuildLookupKey(
                        declarationKind,
                        declarationContext.Start.Line,
                        declarationContext.Start.Column + 1),
                    out var publishedType))
            {
                type = ApplyGenericSubstitution(publishedType);
                return true;
            }

            type = StarkTypeSymbols.Error;
            return false;
        }

        private bool TryResolvePublishedDirectCallSignature(
            StarkParser.ArgumentListContext arguments,
            out TypedFunctionSignature signature)
        {
            if (_importedDirectCallOrdinals is { } directCallOrdinals
                && directCallOrdinals.TryGetValue(arguments, out var directCallOrdinal)
                && _importedTemplateDirectCalls.TryGetValue(directCallOrdinal, out var publishedSignature))
            {
                signature = ApplyGenericSubstitution(publishedSignature);
                return true;
            }

            signature = null!;
            return false;
        }

        private bool TryResolvePublishedEnumCallSummary(
            StarkParser.ArgumentListContext arguments,
            out ImportedTemplateEnumCallSummary summary)
        {
            if (_importedEnumCallOrdinals is { } enumCallOrdinals
                && enumCallOrdinals.TryGetValue(arguments, out var enumCallOrdinal)
                && _importedTemplateEnumCalls.TryGetValue(enumCallOrdinal, out var publishedSummary))
            {
                summary = publishedSummary;
                return true;
            }

            summary = null!;
            return false;
        }

        private bool TryResolvePublishedEnumValueSummary(
            StarkParser.PrimaryExpressionContext expression,
            out ImportedTemplateEnumValueSummary summary)
        {
            if (_importedEnumValueOrdinals is { } enumValueOrdinals
                && enumValueOrdinals.TryGetValue(expression, out var enumValueOrdinal)
                && _importedTemplateEnumValues.TryGetValue(enumValueOrdinal, out var publishedSummary))
            {
                summary = publishedSummary;
                return true;
            }

            summary = null!;
            return false;
        }

        private bool TryResolvePublishedEnumPatternSummary(
            ParserRuleContext patternContext,
            out ImportedTemplateEnumPatternSummary summary)
        {
            if (_importedEnumPatternOrdinals is { } enumPatternOrdinals
                && enumPatternOrdinals.TryGetValue(patternContext, out var enumPatternOrdinal)
                && _importedTemplateEnumPatterns.TryGetValue(enumPatternOrdinal, out var publishedSummary))
            {
                summary = publishedSummary;
                return true;
            }

            summary = null!;
            return false;
        }

        private bool TryResolvePublishedAggregatePatternSummary(
            StarkParser.AggregatePatternContext patternContext,
            out ImportedTemplateAggregatePatternSummary summary)
        {
            if (_importedEnumPatternOrdinals is { } patternOrdinals
                && patternOrdinals.TryGetValue(patternContext, out var patternOrdinal)
                && _importedTemplateAggregatePatterns.TryGetValue(patternOrdinal, out var publishedSummary))
            {
                summary = publishedSummary;
                return true;
            }

            summary = null!;
            return false;
        }

        private bool TryResolvePublishedConversionType(
            StarkParser.UnaryExpressionContext expression,
            out StarkTypeSymbol type)
        {
            if (_importedConversionOrdinals is { } conversionOrdinals
                && conversionOrdinals.TryGetValue(expression, out var conversionOrdinal)
                && _importedTemplateConversions.TryGetValue(conversionOrdinal, out var publishedType))
            {
                type = ApplyGenericSubstitution(publishedType);
                return true;
            }

            type = StarkTypeSymbols.Error;
            return false;
        }

        private bool TryLowerPublishedFieldAccess(
            MidLevelIrOperand target,
            StarkParser.PostfixPartContext postfixPart,
            out MidLevelIrOperand? fieldValue)
        {
            fieldValue = null;

            if (_importedFieldAccessOrdinals is not { } fieldAccessOrdinals
                || !fieldAccessOrdinals.TryGetValue(postfixPart, out var fieldAccessOrdinal)
                || !_importedTemplateFieldAccesses.TryGetValue(fieldAccessOrdinal, out var publishedFieldAccess))
            {
                return false;
            }

            fieldValue = LowerKnownFieldAccess(
                target,
                publishedFieldAccess.FieldName,
                publishedFieldAccess.FieldIndex,
                ApplyGenericSubstitution(publishedFieldAccess.FieldType),
                publishedFieldAccess.FieldName);
            return true;
        }

        private StarkTypeSymbol ApplyGenericSubstitution(StarkTypeSymbol type)
        {
            return _genericTypeSubstitution is { Count: > 0 }
                ? FunctionOverloadFacts.SubstituteType(type, _genericTypeSubstitution)
                : type;
        }

        private TypedFunctionSignature ApplyGenericSubstitution(TypedFunctionSignature signature)
        {
            if (_genericTypeSubstitution is not { Count: > 0 })
            {
                return signature;
            }

            return signature with
            {
                ReturnType = ApplyGenericSubstitution(signature.ReturnType),
                Parameters = signature.Parameters
                    .Select(parameter => new TypedParameterSymbol(
                        parameter.Name,
                        ApplyGenericSubstitution(parameter.Type)))
                    .ToArray(),
                TypeArguments = signature.TypeArguments is { Count: > 0 }
                    ? signature.TypeArguments.Select(ApplyGenericSubstitution).ToArray()
                    : null
            };
        }

        private StarkTypeSymbol ResolveGenericQualifiedName(StarkParser.GenericQualifiedNameContext genericQualifiedName)
        {
            var baseName = genericQualifiedName.qualifiedName().GetText();
            var baseType = ApplyGenericSubstitution(
                _typeResolver.ResolveQualifiedType(baseName, _genericParameterNames, genericQualifiedName.qualifiedName().Start, CurrentModuleName));
            if (baseType.Kind == StarkTypeKind.Error)
            {
                return StarkTypeSymbols.Error;
            }

            var typeArguments = genericQualifiedName.typeArgumentList().type_()
                .Select(typeArgument => ResolveTypeWithGenericSubstitution(typeArgument, CurrentModuleName))
                .ToArray();
            if (typeArguments.Any(static type => type.Kind == StarkTypeKind.Error))
            {
                return StarkTypeSymbols.Error;
            }

            return StarkTypeSymbols.GenericInstantiation(baseType.NamedType ?? baseName, typeArguments);
        }

        private bool TryBuildGenericEnumCaseName(
            StarkParser.GenericEnumCaseReferenceContext genericEnumCaseReference,
            out string name)
        {
            name = string.Empty;

            var enumType = ResolveGenericQualifiedName(genericEnumCaseReference.genericQualifiedName());
            if (enumType.Kind != StarkTypeKind.Named || enumType.NamedType is null)
            {
                return false;
            }

            name = $"{enumType.NamedType}.{genericEnumCaseReference.Identifier().GetText()}";
            return true;
        }

        private bool TryResolveEnumCaseReference(
            string name,
            out StarkTypeSymbol enumType,
            out EnumLayoutSymbol layout,
            out EnumVariantLayoutSymbol variant)
        {
            enumType = StarkTypeSymbols.Error;
            layout = null!;
            variant = null!;

            var separator = name.LastIndexOf('.');
            if (separator <= 0)
            {
                return false;
            }

            var enumTypeName = name[..separator];
            var variantName = name[(separator + 1)..];
            if (!TryResolveNamedTypeBySourceName(enumTypeName, out var namedType)
                || namedType.Kind != DeclarationKind.Enum
                || !_enumLayoutModel.Layouts.TryGetValue(namedType.Name, out layout)
                || !layout.TryGetVariant(variantName, out variant))
            {
                layout = null!;
                variant = null!;
                return false;
            }

            enumType = StarkTypeSymbols.Named(namedType.Name);
            return true;
        }

        private bool TryResolveEnumCaseReference(
            StarkParser.GenericEnumCaseReferenceContext genericEnumCaseReference,
            out StarkTypeSymbol enumType,
            out EnumLayoutSymbol layout,
            out EnumVariantLayoutSymbol variant)
        {
            enumType = StarkTypeSymbols.Error;
            layout = null!;
            variant = null!;

            return TryBuildGenericEnumCaseName(genericEnumCaseReference, out var name)
                && TryResolveEnumCaseReference(name, out enumType, out layout, out variant);
        }

        private bool TryResolveEnumCaseTarget(
            StarkParser.EnumCaseTargetContext enumCaseTarget,
            out string caseName,
            out StarkTypeSymbol enumType,
            out EnumLayoutSymbol layout,
            out EnumVariantLayoutSymbol variant)
        {
            caseName = enumCaseTarget.GetText();
            enumType = StarkTypeSymbols.Error;
            layout = null!;
            variant = null!;

            if (enumCaseTarget.genericEnumCaseReference() is { } genericEnumCaseReference)
            {
                return TryResolveEnumCaseReference(genericEnumCaseReference, out enumType, out layout, out variant);
            }

            return TryResolveEnumCaseReference(enumCaseTarget.dottedName().GetText(), out enumType, out layout, out variant);
        }

        private bool TryResolveNamedTypeBySourceName(string typeName, out NamedTypeSymbol namedType)
        {
            if (!typeName.Contains('.', StringComparison.Ordinal)
                && _typeModel.NamedTypes.TryGetValue($"{CurrentModuleName}.{typeName}", out namedType!))
            {
                return true;
            }

            if (_typeModel.NamedTypes.TryGetValue(typeName, out namedType!))
            {
                return true;
            }

            if (!typeName.Contains('.', StringComparison.Ordinal)
                && _typeModel.NamedTypes.TryGetValue($"{CurrentModuleName}.{typeName}", out namedType!))
            {
                return true;
            }

            namedType = null!;
            return false;
        }

        private bool TryResolveAssignmentTarget(StarkParser.UnaryExpressionContext expression, out PlaceTarget target)
        {
            target = default!;

            if (expression.powerExpression() is not { } powerExpression
                || powerExpression.unaryExpression() is not null
                || powerExpression.postfixExpression() is not { } postfixExpression)
            {
                return false;
            }

            if (postfixExpression.primaryExpression().expression() is { } groupedExpression
                && TryExtractSimpleUnaryExpression(groupedExpression, out var groupedUnary))
            {
                if (TryResolvePointerBackedAssignmentTarget(postfixExpression, groupedUnary, out target))
                {
                    return true;
                }

                return TryResolveAssignmentTarget(groupedUnary, out target);
            }

            if (!TryInitializePostfixState(postfixExpression.primaryExpression(), out var root, out var currentName))
            {
                return false;
            }

            var path = new List<PlacePathSegment>();
            var currentType = root?.Type;
            var supportsAddressModel = SupportsAddressModel(root);
            var usesAddressModel = false;

            foreach (var postfixPart in postfixExpression.postfixPart())
            {
                if (postfixPart.argumentList() is not null)
                {
                    return false;
                }

                if (currentType is null)
                {
                    var memberName = postfixPart.Identifier()?.GetText();
                    if (currentName is null || memberName is null)
                    {
                        return false;
                    }

                    var qualifiedName = $"{currentName}.{memberName}";
                    root = TryResolveNamedValueOperand(qualifiedName);
                    if (root is null)
                    {
                        currentName = qualifiedName;
                        continue;
                    }

                    currentName = null;
                    currentType = root.Type;
                    supportsAddressModel = SupportsAddressModel(root);
                    continue;
                }

                if (postfixPart.expressionList() is { } expressionList)
                {
                    foreach (var indexExpression in expressionList.expression())
                    {
                        if (currentType.Kind == StarkTypeKind.FixedArray
                            && TryResolveConstantArrayIndex(currentType, indexExpression, out var constantIndex, out var elementType))
                        {
                            elementType = ProjectFrozenView(currentType, elementType);
                            path.Add(new PlacePathSegment(
                                PlacePathKind.ConstantArrayIndex,
                                FieldName: null,
                                ConstantIndex: constantIndex,
                                IndexOperand: null,
                                ParentType: currentType,
                                SegmentType: elementType));
                            currentType = elementType;
                            continue;
                        }

                        if (currentType.Kind == StarkTypeKind.FixedArray && supportsAddressModel)
                        {
                            if (currentType.ElementType is null)
                            {
                                return false;
                            }

                            var indexOperand = LowerExpressionToOperand(indexExpression);
                            if (indexOperand is null || indexOperand.Type.Kind != StarkTypeKind.Integer)
                            {
                                return false;
                            }

                            var dynamicElementType = ProjectFrozenView(currentType, currentType.ElementType);
                            path.Add(new PlacePathSegment(
                                PlacePathKind.DynamicArrayIndex,
                                FieldName: null,
                                ConstantIndex: null,
                                IndexOperand: indexOperand,
                                ParentType: currentType,
                                SegmentType: dynamicElementType));
                            currentType = dynamicElementType;
                            usesAddressModel = true;
                            supportsAddressModel = true;
                            continue;
                        }

                        if (currentType.Kind == StarkTypeKind.Slice && currentType.ElementType is not null)
                        {
                            var indexOperand = LowerExpressionToOperand(indexExpression);
                            if (indexOperand is null || indexOperand.Type.Kind != StarkTypeKind.Integer)
                            {
                                return false;
                            }

                            var sliceElementType = ProjectFrozenView(currentType, currentType.ElementType);
                            path.Add(new PlacePathSegment(
                                PlacePathKind.SliceIndex,
                                FieldName: null,
                                ConstantIndex: null,
                                IndexOperand: indexOperand,
                                ParentType: currentType,
                                SegmentType: sliceElementType));
                            currentType = sliceElementType;
                            usesAddressModel = true;
                            supportsAddressModel = true;
                            continue;
                        }

                        if (currentType.Kind == StarkTypeKind.RawPointer && currentType.ElementType is not null)
                        {
                            var indexOperand = LowerExpressionToOperand(indexExpression);
                            if (indexOperand is null || indexOperand.Type.Kind != StarkTypeKind.Integer)
                            {
                                return false;
                            }

                            path.Add(new PlacePathSegment(
                                PlacePathKind.RawPointerIndex,
                                FieldName: null,
                                ConstantIndex: null,
                                IndexOperand: indexOperand,
                                ParentType: currentType,
                                SegmentType: currentType.ElementType));
                            currentType = currentType.ElementType;
                            usesAddressModel = true;
                            supportsAddressModel = true;
                            continue;
                        }

                        return false;
                    }

                    continue;
                }

                if (!TryResolveField(currentType, postfixPart.Identifier().GetText(), out var field, out var fieldIndex))
                {
                    return false;
                }

                var projectedType = ProjectFrozenView(currentType, field.Type);
                path.Add(new PlacePathSegment(
                    PlacePathKind.Field,
                    postfixPart.Identifier().GetText(),
                    fieldIndex,
                    IndexOperand: null,
                    ParentType: currentType,
                    SegmentType: projectedType));
                currentType = projectedType;
                supportsAddressModel = supportsAddressModel || usesAddressModel;
            }

            if (root is null)
            {
                if (currentName is null)
                {
                    return false;
                }

                root = ResolveNamedOperand(currentName);
                if (root is null)
                {
                    return false;
                }

                currentType = root.Type;
            }

            if (IsBorrowParameterRoot(root))
            {
                usesAddressModel = true;
            }

            var targetType = currentType ?? root.Type;
            target = new PlaceTarget(root.Text, RootAddress: null, root.Type, targetType, path, usesAddressModel, GetAddressMutability(root));
            return true;
        }

        private bool TryResolvePointerBackedAssignmentTarget(
            StarkParser.PostfixExpressionContext postfixExpression,
            StarkParser.UnaryExpressionContext groupedUnary,
            out PlaceTarget target)
        {
            target = default!;

            if (!TryInitializePointerPlaceRoot(groupedUnary, out var rootAddress, out var rootType, out var rootAddressIsMutable))
            {
                return false;
            }

            var path = new List<PlacePathSegment>();
            var currentType = rootType;

            foreach (var postfixPart in postfixExpression.postfixPart())
            {
                if (postfixPart.argumentList() is not null)
                {
                    return false;
                }

                if (postfixPart.expressionList() is { } expressionList)
                {
                    foreach (var indexExpression in expressionList.expression())
                    {
                        if (currentType.Kind == StarkTypeKind.FixedArray
                            && TryResolveConstantArrayIndex(currentType, indexExpression, out var constantIndex, out var elementType))
                        {
                            elementType = ProjectFrozenView(currentType, elementType);
                            path.Add(new PlacePathSegment(
                                PlacePathKind.ConstantArrayIndex,
                                FieldName: null,
                                ConstantIndex: constantIndex,
                                IndexOperand: null,
                                ParentType: currentType,
                                SegmentType: elementType));
                            currentType = elementType;
                            continue;
                        }

                        if (currentType.Kind == StarkTypeKind.FixedArray && currentType.ElementType is not null)
                        {
                            var indexOperand = LowerExpressionToOperand(indexExpression);
                            if (indexOperand is null || indexOperand.Type.Kind != StarkTypeKind.Integer)
                            {
                                return false;
                            }

                            var dynamicElementType = ProjectFrozenView(currentType, currentType.ElementType);
                            path.Add(new PlacePathSegment(
                                PlacePathKind.DynamicArrayIndex,
                                FieldName: null,
                                ConstantIndex: null,
                                IndexOperand: indexOperand,
                                ParentType: currentType,
                                SegmentType: dynamicElementType));
                            currentType = dynamicElementType;
                            continue;
                        }

                        if (currentType.Kind == StarkTypeKind.Slice && currentType.ElementType is not null)
                        {
                            var indexOperand = LowerExpressionToOperand(indexExpression);
                            if (indexOperand is null || indexOperand.Type.Kind != StarkTypeKind.Integer)
                            {
                                return false;
                            }

                            var sliceElementType = ProjectFrozenView(currentType, currentType.ElementType);
                            path.Add(new PlacePathSegment(
                                PlacePathKind.SliceIndex,
                                FieldName: null,
                                ConstantIndex: null,
                                IndexOperand: indexOperand,
                                ParentType: currentType,
                                SegmentType: sliceElementType));
                            currentType = sliceElementType;
                            continue;
                        }

                        if (currentType.Kind == StarkTypeKind.RawPointer && currentType.ElementType is not null)
                        {
                            var indexOperand = LowerExpressionToOperand(indexExpression);
                            if (indexOperand is null || indexOperand.Type.Kind != StarkTypeKind.Integer)
                            {
                                return false;
                            }

                            path.Add(new PlacePathSegment(
                                PlacePathKind.RawPointerIndex,
                                FieldName: null,
                                ConstantIndex: null,
                                IndexOperand: indexOperand,
                                ParentType: currentType,
                                SegmentType: currentType.ElementType));
                            currentType = currentType.ElementType;
                            continue;
                        }

                        return false;
                    }

                    continue;
                }

                var memberName = postfixPart.Identifier()?.GetText();
                if (memberName is null
                    || !TryResolveField(currentType, memberName, out var field, out var fieldIndex))
                {
                    return false;
                }

                var projectedType = ProjectFrozenView(currentType, field.Type);
                path.Add(new PlacePathSegment(
                    PlacePathKind.Field,
                    memberName,
                    fieldIndex,
                    IndexOperand: null,
                    ParentType: currentType,
                    SegmentType: projectedType));
                currentType = projectedType;
            }

            target = new PlaceTarget(
                RootName: null,
                RootAddress: rootAddress,
                RootType: rootType,
                Type: currentType,
                Path: path,
                UsesAddressModel: true,
                IsAddressMutable: rootAddressIsMutable);
            return true;
        }

        private static bool TryExtractSimpleUnaryExpression(
            StarkParser.ExpressionContext expression,
            out StarkParser.UnaryExpressionContext unaryExpression)
        {
            unaryExpression = default!;

            if (expression.assignmentExpression() is not { } assignmentExpression
                || assignmentExpression.unaryExpression() is not null
                || assignmentExpression.assignmentOperator() is not null
                || assignmentExpression.conditionalExpression() is not { } conditionalExpression
                || conditionalExpression.expression().Length != 0)
            {
                return false;
            }

            return TryExtractSimpleUnaryExpression(conditionalExpression.logicalOrExpression(), out unaryExpression);
        }

        private static bool TryExtractSimpleUnaryExpression(
            StarkParser.LogicalOrExpressionContext expression,
            out StarkParser.UnaryExpressionContext unaryExpression)
        {
            unaryExpression = default!;
            return expression.logicalAndExpression().Length == 1
                && TryExtractSimpleUnaryExpression(expression.logicalAndExpression(0), out unaryExpression);
        }

        private static bool TryExtractSimpleUnaryExpression(
            StarkParser.LogicalAndExpressionContext expression,
            out StarkParser.UnaryExpressionContext unaryExpression)
        {
            unaryExpression = default!;
            return expression.bitwiseOrExpression().Length == 1
                && TryExtractSimpleUnaryExpression(expression.bitwiseOrExpression(0), out unaryExpression);
        }

        private static bool TryExtractSimpleUnaryExpression(
            StarkParser.BitwiseOrExpressionContext expression,
            out StarkParser.UnaryExpressionContext unaryExpression)
        {
            unaryExpression = default!;
            return expression.bitwiseXorExpression().Length == 1
                && TryExtractSimpleUnaryExpression(expression.bitwiseXorExpression(0), out unaryExpression);
        }

        private static bool TryExtractSimpleUnaryExpression(
            StarkParser.BitwiseXorExpressionContext expression,
            out StarkParser.UnaryExpressionContext unaryExpression)
        {
            unaryExpression = default!;
            return expression.bitwiseAndExpression().Length == 1
                && TryExtractSimpleUnaryExpression(expression.bitwiseAndExpression(0), out unaryExpression);
        }

        private static bool TryExtractSimpleUnaryExpression(
            StarkParser.BitwiseAndExpressionContext expression,
            out StarkParser.UnaryExpressionContext unaryExpression)
        {
            unaryExpression = default!;
            return expression.equalityExpression().Length == 1
                && TryExtractSimpleUnaryExpression(expression.equalityExpression(0), out unaryExpression);
        }

        private static bool TryExtractSimpleUnaryExpression(
            StarkParser.EqualityExpressionContext expression,
            out StarkParser.UnaryExpressionContext unaryExpression)
        {
            unaryExpression = default!;
            return expression.relationalExpression().Length == 1
                && TryExtractSimpleUnaryExpression(expression.relationalExpression(0), out unaryExpression);
        }

        private static bool TryExtractSimpleUnaryExpression(
            StarkParser.RelationalExpressionContext expression,
            out StarkParser.UnaryExpressionContext unaryExpression)
        {
            unaryExpression = default!;
            return expression.shiftExpression().Length == 1
                && TryExtractSimpleUnaryExpression(expression.shiftExpression(0), out unaryExpression);
        }

        private static bool TryExtractSimpleUnaryExpression(
            StarkParser.ShiftExpressionContext expression,
            out StarkParser.UnaryExpressionContext unaryExpression)
        {
            unaryExpression = default!;
            return expression.additiveExpression().Length == 1
                && TryExtractSimpleUnaryExpression(expression.additiveExpression(0), out unaryExpression);
        }

        private static bool TryExtractSimpleUnaryExpression(
            StarkParser.AdditiveExpressionContext expression,
            out StarkParser.UnaryExpressionContext unaryExpression)
        {
            unaryExpression = default!;
            return expression.multiplicativeExpression().Length == 1
                && TryExtractSimpleUnaryExpression(expression.multiplicativeExpression(0), out unaryExpression);
        }

        private static bool TryExtractSimpleUnaryExpression(
            StarkParser.MultiplicativeExpressionContext expression,
            out StarkParser.UnaryExpressionContext unaryExpression)
        {
            unaryExpression = default!;
            return expression.unaryExpression().Length == 1
                && (unaryExpression = expression.unaryExpression(0)) is not null;
        }

        private MidLevelIrOperand ReadPlace(PlaceTarget target)
        {
            if (target.UsesAddressModel)
            {
                var address = BuildAddress(target);
                if (address is null)
                {
                    MarkUnsupported();
                    return target.RootName is not null
                        ? ResolveNamedOperand(target.RootName) ?? new MidLevelIrLocalOperand(target.RootName, target.RootType)
                        : new MidLevelIrZeroInitializerOperand(target.Type);
                }

                return EmitTemporary(
                           new MidLevelIrLoadIndirectRValue(address, target.Type, $"{target.RootName}:load"),
                           "load")
                       ?? address;
            }

            if (target.RootName is null)
            {
                MarkUnsupported();
                return new MidLevelIrZeroInitializerOperand(target.Type);
            }

            var current = ResolveNamedOperand(target.RootName) ?? new MidLevelIrLocalOperand(target.RootName, target.RootType);
            foreach (var segment in target.Path)
            {
                var extracted = segment.Kind == PlacePathKind.ConstantArrayIndex
                    ? LowerConstantIndexAccess(current, segment.ConstantIndex!.Value, segment.SegmentType)
                    : LowerFieldAccess(current, segment.FieldName!);
                if (extracted is null)
                {
                    MarkUnsupported();
                    return current;
                }

                current = extracted;
            }

            return current;
        }

        private LoweredAssignment BuildAssignment(PlaceTarget target, MidLevelIrOperand value, string text)
        {
            var assignedValue = CoerceOperand(value, target.Type) ?? value;
            if (target.UsesAddressModel)
            {
                var address = BuildAddress(target);
                return new LoweredAssignment(
                    text,
                    TargetName: null,
                    target.Type,
                    DirectValue: null,
                    ResultValue: assignedValue,
                    Address: address,
                    ReplacesWholeValue: false);
            }

            if (target.Path.Count == 0)
            {
                if (target.RootName is null)
                {
                    MarkUnsupported();
                    return new LoweredAssignment(
                        text,
                        TargetName: null,
                        target.RootType,
                        DirectValue: null,
                        ResultValue: assignedValue,
                        Address: null,
                        ReplacesWholeValue: false);
                }

                return new LoweredAssignment(
                    text,
                    target.RootName,
                    target.RootType,
                    new MidLevelIrUseRValue(assignedValue),
                    assignedValue,
                    Address: null,
                    ReplacesWholeValue: true);
            }

            if (target.RootName is null)
            {
                MarkUnsupported();
                return new LoweredAssignment(
                    text,
                    TargetName: null,
                    target.RootType,
                    DirectValue: null,
                    ResultValue: assignedValue,
                    Address: null,
                    ReplacesWholeValue: false);
            }

            var root = ResolveNamedOperand(target.RootName) ?? new MidLevelIrLocalOperand(target.RootName, target.RootType);
            var updatedRoot = ApplyAggregatePathUpdate(root, target.Path, 0, assignedValue, text);
            return new LoweredAssignment(
                text,
                target.RootName,
                target.RootType,
                updatedRoot is null ? null : new MidLevelIrUseRValue(updatedRoot),
                assignedValue,
                Address: null,
                ReplacesWholeValue: false);
        }

        private MidLevelIrOperand? ApplyAggregatePathUpdate(
            MidLevelIrOperand aggregate,
            IReadOnlyList<PlacePathSegment> path,
            int depth,
            MidLevelIrOperand value,
            string text)
        {
            var segment = path[depth];
            if (depth == path.Count - 1)
            {
                var coercedValue = CoerceOperand(value, segment.SegmentType);
                if (coercedValue is null)
                {
                    return null;
                }

                return segment.Kind == PlacePathKind.ConstantArrayIndex
                    ? EmitTemporary(
                        new MidLevelIrInsertIndexRValue(
                            aggregate,
                            segment.ConstantIndex!.Value,
                            coercedValue,
                            aggregate.Type,
                            text),
                        "setindex")
                    : EmitTemporary(
                        new MidLevelIrInsertFieldRValue(
                            aggregate,
                            segment.FieldName!,
                            segment.ConstantIndex!.Value,
                            coercedValue,
                            aggregate.Type,
                            text),
                        "setfield");
            }

            var nested = segment.Kind == PlacePathKind.ConstantArrayIndex
                ? LowerConstantIndexAccess(aggregate, segment.ConstantIndex!.Value, segment.SegmentType)
                : LowerFieldAccess(aggregate, segment.FieldName!);
            if (nested is null)
            {
                return null;
            }

            var updatedNested = ApplyAggregatePathUpdate(nested, path, depth + 1, value, text);
            if (updatedNested is null)
            {
                return null;
            }

            return segment.Kind == PlacePathKind.ConstantArrayIndex
                ? EmitTemporary(
                    new MidLevelIrInsertIndexRValue(
                        aggregate,
                        segment.ConstantIndex!.Value,
                        updatedNested,
                        aggregate.Type,
                        text),
                    "setindex")
                : EmitTemporary(
                    new MidLevelIrInsertFieldRValue(
                        aggregate,
                        segment.FieldName!,
                        segment.ConstantIndex!.Value,
                        updatedNested,
                        aggregate.Type,
                        text),
                    "setfield");
        }

        private MidLevelIrOperand? BuildAddress(PlaceTarget target)
        {
            MidLevelIrOperand? currentValue = target.RootName is null ? null : ResolveNamedOperand(target.RootName);
            var currentAddressIsMutable = target.IsAddressMutable;
            MidLevelIrOperand? currentAddress = target.RootAddress
                ?? currentValue switch
                {
                    MidLevelIrLocalOperand local => CreateAddressOfLocal(local.Name, local.Type),
                    MidLevelIrParameterOperand parameter => CreateAddressOfParameter(parameter.Name, parameter.Type),
                    MidLevelIrGlobalOperand global => CreateAddressOfGlobal(global.Name, global.Type),
                    _ => null
                };
            var currentType = target.RootType;

            foreach (var segment in target.Path)
            {
                switch (segment.Kind)
                {
                    case PlacePathKind.Field:
                        if (currentAddress is null)
                        {
                            return null;
                        }

                        var fieldAddressIsMutable = currentAddressIsMutable && CanMutateThroughType(segment.SegmentType);
                        currentAddress = EmitTemporary(
                            new MidLevelIrFieldAddressRValue(
                                currentAddress,
                                currentType,
                                segment.FieldName!,
                                segment.ConstantIndex!.Value,
                                AddressType(segment.SegmentType, fieldAddressIsMutable),
                                $"{currentAddress.Text}.{segment.FieldName}"),
                            "addr");
                        currentType = segment.SegmentType;
                        currentAddressIsMutable = fieldAddressIsMutable;
                        currentValue = null;
                        break;
                    case PlacePathKind.ConstantArrayIndex:
                    case PlacePathKind.DynamicArrayIndex:
                        if (currentAddress is null)
                        {
                            return null;
                        }

                        var elementAddressIsMutable = currentAddressIsMutable && CanMutateThroughType(segment.SegmentType);
                        currentAddress = EmitTemporary(
                            new MidLevelIrElementAddressRValue(
                                currentAddress,
                                currentType,
                                segment.IndexOperand,
                                segment.ConstantIndex,
                                AddressType(segment.SegmentType, elementAddressIsMutable),
                                $"{currentAddress.Text}[{segment.ConstantIndex?.ToString() ?? segment.IndexOperand?.Text ?? "?"}]"),
                            "addr");
                        currentType = segment.SegmentType;
                        currentAddressIsMutable = elementAddressIsMutable;
                        currentValue = null;
                        break;
                    case PlacePathKind.RawPointerIndex:
                        var pointerValue = currentValue;
                        if (pointerValue is null && currentAddress is not null)
                        {
                            pointerValue = EmitTemporary(
                                new MidLevelIrLoadIndirectRValue(currentAddress, currentType, $"{currentAddress.Text}:load"),
                                "load");
                        }

                        if (pointerValue is null
                            || pointerValue.Type.Kind != StarkTypeKind.RawPointer
                            || pointerValue.Type.ElementType is null
                            || segment.IndexOperand is null)
                        {
                            return null;
                        }

                        currentAddressIsMutable = pointerValue.Type.IsMutablePointer && CanMutateThroughType(segment.SegmentType);
                        currentAddress = EmitTemporary(
                            new MidLevelIrElementAddressRValue(
                                pointerValue,
                                segment.SegmentType,
                                segment.IndexOperand,
                                ConstantIndex: null,
                                AddressType(segment.SegmentType, currentAddressIsMutable),
                                $"{pointerValue.Text}[{segment.IndexOperand.Text}]"),
                            "addr");
                        currentType = segment.SegmentType;
                        currentValue = null;
                        break;
                    case PlacePathKind.SliceIndex:
                        var sliceValue = currentValue;
                        if (sliceValue is null && currentAddress is not null)
                        {
                            sliceValue = EmitTemporary(
                                new MidLevelIrLoadIndirectRValue(currentAddress, currentType, $"{currentAddress.Text}:load"),
                                "load");
                        }

                        if (sliceValue is null || segment.IndexOperand is null)
                        {
                            return null;
                        }

                        var sliceElementAddressIsMutable = currentAddressIsMutable && CanMutateThroughType(segment.SegmentType);
                        currentAddress = EmitTemporary(
                            new MidLevelIrSliceElementAddressRValue(
                                sliceValue,
                                segment.IndexOperand,
                                AddressType(segment.SegmentType, sliceElementAddressIsMutable),
                                $"{sliceValue.Text}[{segment.IndexOperand.Text}]"),
                            "addr");
                        currentType = segment.SegmentType;
                        currentAddressIsMutable = sliceElementAddressIsMutable;
                        currentValue = null;
                        break;
                }

                if (currentAddress is null)
                {
                    return null;
                }
            }

            return currentAddress;
        }

        private bool TryResolveField(StarkTypeSymbol targetType, string memberName, out FieldSymbol field, out int fieldIndex)
        {
            field = default!;
            fieldIndex = -1;

            if (targetType.Kind != StarkTypeKind.Named
                || targetType.NamedType is null
                || !_typeModel.NamedTypes.TryGetValue(targetType.NamedType, out var namedType))
            {
                return false;
            }

            return namedType.TryGetField(memberName, out field, out fieldIndex);
        }

        private MidLevelIrOperand? LowerConstantIndexAccess(MidLevelIrOperand target, int constantIndex, StarkTypeSymbol elementType)
        {
            return EmitTemporary(
                new MidLevelIrExtractIndexRValue(
                    target,
                    constantIndex,
                    elementType,
                    $"{target.Text}[{constantIndex}]"),
                "index");
        }

        private bool TryResolveConstantArrayIndex(
            StarkTypeSymbol targetType,
            StarkParser.ExpressionContext expression,
            out int constantIndex,
            out StarkTypeSymbol elementType)
        {
            constantIndex = -1;
            elementType = StarkTypeSymbols.Error;

            if (targetType.Kind != StarkTypeKind.FixedArray
                || targetType.ElementType is null
                || targetType.FixedLength is not int fixedLength)
            {
                return false;
            }

            if (!TryEvaluateCompileTimeInteger(expression, CurrentModuleName, state: null, activeCalls: null, out var parsed))
            {
                return false;
            }
            if (parsed < 0 || parsed > int.MaxValue)
            {
                return false;
            }

            constantIndex = (int)parsed;
            if (constantIndex >= fixedLength)
            {
                return false;
            }

            elementType = targetType.ElementType;
            return true;
        }

        private static StarkParser.LiteralContext? TryGetSimpleLiteral(StarkParser.ExpressionContext expression)
        {
            if (TryGetSimplePostfixExpression(expression) is not { } postfix || postfix.postfixPart().Length != 0)
            {
                return null;
            }

            return postfix.primaryExpression().literal();
        }

        private MidLevelIrOperand? LowerBinaryChain<TOperandContext>(
            IReadOnlyList<TOperandContext> operands,
            IReadOnlyList<string> operators,
            Func<TOperandContext, MidLevelIrOperand?> lowerOperand,
            Func<string, MidLevelIrBinaryOperator> mapOperator,
            bool requireInteger,
            StarkTypeSymbol? expectedType)
            where TOperandContext : ParserRuleContext
        {
            var current = lowerOperand(operands[0]);
            if (current is null)
            {
                return null;
            }

            if (operators.Count == 0)
            {
                return expectedType is null ? current : CoerceOperand(current, expectedType);
            }

            for (var index = 1; index < operands.Count; index++)
            {
                var next = lowerOperand(operands[index]);
                if (next is null)
                {
                    return null;
                }

                var resultType = FindCommonType(current.Type, next.Type);
                if (requireInteger && resultType.Kind != StarkTypeKind.Integer)
                {
                    MarkUnsupported();
                    return null;
                }

                var left = CoerceOperand(current, resultType);
                var right = CoerceOperand(next, resultType);
                if (left is null || right is null)
                {
                    return null;
                }

                current = EmitTemporary(
                    new MidLevelIrBinaryRValue(mapOperator(operators[index - 1]), left, right, resultType, operators[index - 1]),
                    "bin");

                if (current is null)
                {
                    return null;
                }
            }

            return expectedType is null ? current : CoerceOperand(current, expectedType);
        }

        private MidLevelIrOperand? LowerComparisonChain<TOperandContext>(
            IReadOnlyList<TOperandContext> operands,
            IReadOnlyList<string> operators,
            Func<TOperandContext, MidLevelIrOperand?> lowerOperand)
            where TOperandContext : ParserRuleContext
        {
            var left = lowerOperand(operands[0]);
            if (left is null)
            {
                return null;
            }

            if (operators.Count == 0)
            {
                return left;
            }

            var currentLeft = left;
            if (operators.Count == 1 && operands.Count == 2)
            {
                var right = lowerOperand(operands[1]);
                return right is null
                    ? null
                    : EmitPairComparison(currentLeft, right, operators[0], $"{operands[0].GetText()} {operators[0]} {operands[1].GetText()}");
            }

            var result = CreateTemporaryLocal(StarkTypeSymbols.Bool, "cmpchain");
            var joinBlock = CreateBlock("cmpchain_join");

            for (var index = 0; index < operators.Count; index++)
            {
                var right = lowerOperand(operands[index + 1]);
                if (right is null)
                {
                    return null;
                }

                var comparison = EmitPairComparison(
                    currentLeft,
                    right,
                    operators[index],
                    $"{operands[index].GetText()} {operators[index]} {operands[index + 1].GetText()}");
                if (comparison is null)
                {
                    return null;
                }

                if (index == operators.Count - 1)
                {
                    EmitOperandAssignment(result, comparison, comparison.Text);
                    EnsureGoto(joinBlock.Id);
                    break;
                }

                var nextBlock = CreateBlock($"cmpchain_next_{index + 1}");
                var falseBlock = CreateBlock($"cmpchain_false_{index}");
                CurrentBlock.Terminator = new MidLevelIrTerminator(
                    MidLevelIrTerminatorKind.Branch,
                    [nextBlock.Id, falseBlock.Id],
                    ConditionText: comparison.Text,
                    Condition: comparison);

                CurrentBlock = falseBlock;
                EmitOperandAssignment(result, new MidLevelIrBoolConstantOperand(false), "false");
                EnsureGoto(joinBlock.Id);

                CurrentBlock = nextBlock;
                currentLeft = right;
            }

            CurrentBlock = joinBlock;
            return result;
        }

        private MidLevelIrOperand? EmitPairComparison(
            MidLevelIrOperand left,
            MidLevelIrOperand right,
            string operatorText,
            string text)
        {
            var operandType = FindCommonType(left.Type, right.Type);
            if (operandType.Kind == StarkTypeKind.Error)
            {
                MarkUnsupported();
                return null;
            }

            var coercedLeft = CoerceOperand(left, operandType);
            var coercedRight = CoerceOperand(right, operandType);
            if (coercedLeft is null || coercedRight is null)
            {
                return null;
            }

            return EmitTemporary(
                new MidLevelIrBinaryRValue(
                    MapBinaryOperator(operatorText),
                    coercedLeft,
                    coercedRight,
                    StarkTypeSymbols.Bool,
                    text),
                "cmp");
        }

        private MidLevelIrOperand? CoerceOperand(MidLevelIrOperand? operand, StarkTypeSymbol targetType)
        {
            if (operand is null || targetType.Kind == StarkTypeKind.Error || operand.Type.Kind == StarkTypeKind.Error)
            {
                return operand;
            }

            if (operand.Type == targetType)
            {
                return operand;
            }

            if (operand.Type.Kind == StarkTypeKind.Null && targetType.Kind == StarkTypeKind.RawPointer)
            {
                return new MidLevelIrNullOperand(targetType);
            }

            if (operand.Type.Kind == StarkTypeKind.Integer && targetType.Kind == StarkTypeKind.Integer)
            {
                return EmitTemporary(
                    new MidLevelIrConvertRValue(operand, targetType, $"{operand.Text}:{targetType.DisplayName}"),
                    "intcast");
            }

            if (operand.Type.Kind == StarkTypeKind.Integer && targetType.Kind == StarkTypeKind.Float)
            {
                return EmitTemporary(
                    new MidLevelIrConvertRValue(operand, targetType, $"{operand.Text}:{targetType.DisplayName}"),
                    "numcast");
            }

            if (operand.Type.Kind == StarkTypeKind.Float && targetType.Kind == StarkTypeKind.Integer)
            {
                return EmitTemporary(
                    new MidLevelIrConvertRValue(operand, targetType, $"{operand.Text}:{targetType.DisplayName}"),
                    "intcast");
            }

            if (operand.Type.Kind == StarkTypeKind.Float && targetType.Kind == StarkTypeKind.Float)
            {
                return EmitTemporary(
                    new MidLevelIrConvertRValue(operand, targetType, $"{operand.Text}:{targetType.DisplayName}"),
                    "floatcast");
            }

            if ((operand.Type.Kind == StarkTypeKind.Integer && targetType.Kind == StarkTypeKind.RawPointer)
                || (operand.Type.Kind == StarkTypeKind.RawPointer && targetType.Kind == StarkTypeKind.Integer))
            {
                return EmitTemporary(
                    new MidLevelIrConvertRValue(operand, targetType, $"{operand.Text}:{targetType.DisplayName}"),
                    targetType.Kind == StarkTypeKind.RawPointer ? "ptrcast" : "intcast");
            }

            if (operand.Type.Kind == StarkTypeKind.RawPointer && targetType.Kind == StarkTypeKind.RawPointer)
            {
                return EmitTemporary(
                    new MidLevelIrConvertRValue(operand, targetType, $"{operand.Text}:{targetType.DisplayName}"),
                    "ptrcast");
            }

            if ((operand.Type.Kind == StarkTypeKind.Ascii && targetType.Kind == StarkTypeKind.Unicode)
                || (operand.Type.Kind == StarkTypeKind.Unicode && targetType.Kind == StarkTypeKind.Ascii))
            {
                if (TryConvertTextLiteral(operand, targetType, out var convertedTextLiteral))
                {
                    return convertedTextLiteral;
                }

                return EmitTemporary(
                    new MidLevelIrConvertRValue(operand, targetType, $"{operand.Text}:{targetType.DisplayName}"),
                    "textcast");
            }

            if (operand.Type.Kind == StarkTypeKind.FixedArray
                && targetType.Kind == StarkTypeKind.Slice
                && operand is MidLevelIrLocalOperand localOperand)
            {
                EnsureAddressableLocal(localOperand.Name);
                return EmitTemporary(
                    new MidLevelIrMakeSliceFromLocalRValue(
                        localOperand.Name,
                        operand.Type,
                        targetType,
                        $"{localOperand.Name}:slice"),
                    "slice");
            }

            if (HasSameStorageType(operand.Type, targetType))
            {
                return operand;
            }

            if (targetType.Kind == StarkTypeKind.Bool && operand.Type.Kind == StarkTypeKind.Bool)
            {
                return operand;
            }

            return operand;
        }

        private MidLevelIrOperand? LowerShortCircuitBooleanChain<TOperandContext>(
            IReadOnlyList<TOperandContext> operands,
            Func<TOperandContext, MidLevelIrOperand?> lowerOperand,
            bool shortCircuitOnTrue,
            string resultHint)
            where TOperandContext : ParserRuleContext
        {
            var result = CreateTemporaryLocal(StarkTypeSymbols.Bool, resultHint);
            var joinBlock = CreateBlock($"{resultHint}_join");

            for (var index = 0; index < operands.Count - 1; index++)
            {
                var operand = CoerceOperand(lowerOperand(operands[index]), StarkTypeSymbols.Bool);
                if (operand is null)
                {
                    return null;
                }

                var shortCircuitBlock = CreateBlock($"{resultHint}_short_{index}");
                var nextBlock = CreateBlock($"{resultHint}_rhs_{index + 1}");

                CurrentBlock.Terminator = shortCircuitOnTrue
                    ? new MidLevelIrTerminator(
                        MidLevelIrTerminatorKind.Branch,
                        [shortCircuitBlock.Id, nextBlock.Id],
                        ConditionText: operands[index].GetText(),
                        Condition: operand)
                    : new MidLevelIrTerminator(
                        MidLevelIrTerminatorKind.Branch,
                        [nextBlock.Id, shortCircuitBlock.Id],
                        ConditionText: operands[index].GetText(),
                        Condition: operand);

                CurrentBlock = shortCircuitBlock;
                EmitOperandAssignment(result, new MidLevelIrBoolConstantOperand(shortCircuitOnTrue), shortCircuitOnTrue ? "true" : "false");
                EnsureGoto(joinBlock.Id);

                CurrentBlock = nextBlock;
            }

            var lastOperand = CoerceOperand(lowerOperand(operands[^1]), StarkTypeSymbols.Bool);
            if (lastOperand is null)
            {
                return null;
            }

            EmitOperandAssignment(result, lastOperand, operands[^1].GetText());
            EnsureGoto(joinBlock.Id);

            CurrentBlock = joinBlock;
            return result;
        }

        private MidLevelIrOperand? EmitEqualityComparison(MidLevelIrOperand left, MidLevelIrOperand right, string text)
        {
            var compareType = FindCommonType(left.Type, right.Type);
            if (compareType.Kind is not (StarkTypeKind.Integer or StarkTypeKind.Float or StarkTypeKind.Bool or StarkTypeKind.RawPointer))
            {
                MarkUnsupported();
                return null;
            }

            var coercedLeft = CoerceOperand(left, compareType);
            var coercedRight = CoerceOperand(right, compareType);
            if (coercedLeft is null || coercedRight is null)
            {
                return null;
            }

            return EmitTemporary(
                new MidLevelIrBinaryRValue(
                    MidLevelIrBinaryOperator.Equal,
                    coercedLeft,
                    coercedRight,
                    StarkTypeSymbols.Bool,
                    text),
                "cmp");
        }

        private MidLevelIrOperand EmitResolvedEqualityComparison(MidLevelIrOperand left, MidLevelIrOperand right, string text)
        {
            return EmitRequiredTemporary(
                new MidLevelIrBinaryRValue(
                    MidLevelIrBinaryOperator.Equal,
                    left,
                    right,
                    StarkTypeSymbols.Bool,
                    text),
                "cmp");
        }

        private MidLevelIrOperand? EmitSwitchLiteralComparison(
            MidLevelIrOperand switchValue,
            StarkParser.LiteralContext literal,
            string text)
        {
            var literalOperand = LowerSwitchCaseLiteral(literal, switchValue.Type);
            if (literalOperand is null)
            {
                return null;
            }

            if (switchValue.Type.Kind is StarkTypeKind.Ascii or StarkTypeKind.Unicode)
            {
                if (literalOperand is not MidLevelIrStringConstantOperand stringLiteral)
                {
                    MarkUnsupported();
                    return null;
                }

                return EmitTextLiteralComparison(switchValue, stringLiteral, text);
            }

            return EmitEqualityComparison(switchValue, literalOperand, text);
        }

        private MidLevelIrOperand? EmitImportedTypedTemplateSwitchLiteralComparison(
            MidLevelIrOperand switchValue,
            ImportedTemplateTypedBodyExpressionSummary literalExpression,
            string text)
        {
            var literalOperand = LowerImportedTypedTemplateExpression(literalExpression, switchValue.Type);
            if (literalOperand is null)
            {
                return null;
            }

            if (switchValue.Type.Kind is StarkTypeKind.Ascii or StarkTypeKind.Unicode)
            {
                if (literalOperand is not MidLevelIrStringConstantOperand stringLiteral)
                {
                    MarkUnsupported();
                    return null;
                }

                return EmitTextLiteralComparison(switchValue, stringLiteral, text);
            }

            return EmitEqualityComparison(switchValue, literalOperand, text);
        }

        private bool EmitPartitionedTextLengthDecision(
            MidLevelIrOperand dataPointer,
            IReadOnlyList<PartitionedTextSwitchLabel> labels,
            int defaultTarget,
            string switchText)
        {
            if (labels.Count == 0)
            {
                CurrentBlock.Terminator = new MidLevelIrTerminator(MidLevelIrTerminatorKind.Goto, [defaultTarget]);
                return true;
            }

            var decisionBlocks = new BasicBlockBuilder[labels.Count];
            decisionBlocks[0] = CurrentBlock;
            for (var index = 1; index < labels.Count; index++)
            {
                decisionBlocks[index] = CreateBlock($"textcmp_len_{labels[0].Units.Length}_{index}");
            }

            for (var index = 0; index < labels.Count; index++)
            {
                CurrentBlock = decisionBlocks[index];
                var label = labels[index];
                var nextTarget = index + 1 < labels.Count ? decisionBlocks[index + 1].Id : defaultTarget;

                if (!EmitTextLiteralMatchTransition(
                    dataPointer,
                    label.Units,
                    label.TargetBlockId,
                    nextTarget,
                    $"switch {switchText} == {label.Label.LabelText}"))
                {
                    return false;
                }
            }

            return true;
        }

        private MidLevelIrOperand? EmitTextLiteralComparison(
            MidLevelIrOperand switchValue,
            MidLevelIrStringConstantOperand literal,
            string text)
        {
            var units = DecodeTextLiteralUnits(literal.LiteralText, switchValue.Type);
            if (!TryExtractTextSwitchComponents(switchValue, out var dataPointer, out var length))
            {
                return null;
            }

            var unitType = GetTextUnitType(switchValue.Type);
            var lengthType = StarkTypeSymbols.Integer(64);
            var lengthMatches = EmitPairComparison(
                length,
                new MidLevelIrIntegerConstantOperand(new BigInteger(units.Length), lengthType),
                "==",
                $"{text}:length");
            if (lengthMatches is null || units.Length == 0)
            {
                return lengthMatches;
            }

            var result = CreateTemporaryLocal(StarkTypeSymbols.Bool, "textcmp");
            var compareBlock = CreateBlock("textcmp_byte_0");
            var falseBlock = CreateBlock("textcmp_false");
            var joinBlock = CreateBlock("textcmp_join");

            CurrentBlock.Terminator = new MidLevelIrTerminator(
                MidLevelIrTerminatorKind.Branch,
                [compareBlock.Id, falseBlock.Id],
                ConditionText: lengthMatches.Text,
                Condition: lengthMatches);

            CurrentBlock = falseBlock;
            EmitOperandAssignment(result, new MidLevelIrBoolConstantOperand(false), "false");
            EnsureGoto(joinBlock.Id);

            CurrentBlock = compareBlock;

            for (var index = 0; index < units.Length; index++)
            {
                var unitAddress = EmitTemporary(
                    new MidLevelIrElementAddressRValue(
                        dataPointer,
                        unitType,
                        Index: null,
                        ConstantIndex: index,
                        AddressType(unitType, isMutable: false),
                        $"{switchValue.Text}.data[{index}]"),
                    "addr");
                if (unitAddress is null)
                {
                    return null;
                }

                var loadedUnit = EmitTemporary(
                    new MidLevelIrLoadIndirectRValue(
                        unitAddress,
                        unitType,
                        $"{switchValue.Text}.data[{index}]"),
                    "load");
                if (loadedUnit is null)
                {
                    return null;
                }

                var unitMatches = EmitPairComparison(
                    loadedUnit,
                    CreateTextUnitConstant(units[index], unitType),
                    "==",
                    $"{text}:unit{index}");
                if (unitMatches is null)
                {
                    return null;
                }

                if (index == units.Length - 1)
                {
                    EmitOperandAssignment(result, unitMatches, unitMatches.Text);
                    EnsureGoto(joinBlock.Id);
                    break;
                }

                var nextByteBlock = CreateBlock($"textcmp_byte_{index + 1}");
                CurrentBlock.Terminator = new MidLevelIrTerminator(
                    MidLevelIrTerminatorKind.Branch,
                    [nextByteBlock.Id, falseBlock.Id],
                    ConditionText: unitMatches.Text,
                    Condition: unitMatches);
                CurrentBlock = nextByteBlock;
            }

            CurrentBlock = joinBlock;
            return result;
        }

        private bool TryExtractTextSwitchComponents(
            MidLevelIrOperand switchValue,
            out MidLevelIrOperand dataPointer,
            out MidLevelIrOperand length)
        {
            dataPointer = null!;
            length = null!;

            if (!CanUsePartitionedTextSwitchType(switchValue.Type))
            {
                MarkUnsupported();
                return false;
            }

            var unitType = GetTextUnitType(switchValue.Type);
            var dataPointerType = StarkTypeSymbols.RawPointer(unitType, isMutable: false);
            var lengthType = StarkTypeSymbols.Integer(64);

            var extractedDataPointer = EmitTemporary(
                new MidLevelIrExtractFieldRValue(
                    switchValue,
                    "data",
                    0,
                    dataPointerType,
                    $"{switchValue.Text}.data"),
                "strdata");
            var extractedLength = EmitTemporary(
                new MidLevelIrExtractFieldRValue(
                    switchValue,
                    "length",
                    1,
                    lengthType,
                    $"{switchValue.Text}.length"),
                "strlen");
            if (extractedDataPointer is null || extractedLength is null)
            {
                return false;
            }

            dataPointer = extractedDataPointer;
            length = extractedLength;
            return true;
        }

        private bool EmitTextLiteralMatchTransition(
            MidLevelIrOperand dataPointer,
            int[] units,
            int targetBlockId,
            int nextTarget,
            string text)
        {
            if (units.Length == 0)
            {
                CurrentBlock.Terminator = new MidLevelIrTerminator(MidLevelIrTerminatorKind.Goto, [targetBlockId]);
                return true;
            }

            var unitType = dataPointer.Type.ElementType ?? throw new InvalidOperationException("Text switch data pointer requires an element type.");
            for (var index = 0; index < units.Length; index++)
            {
                var unitAddress = EmitTemporary(
                    new MidLevelIrElementAddressRValue(
                        dataPointer,
                        unitType,
                        Index: null,
                        ConstantIndex: index,
                        AddressType(unitType, isMutable: false),
                        $"{dataPointer.Text}[{index}]"),
                    "addr");
                if (unitAddress is null)
                {
                    return false;
                }

                var loadedUnit = EmitTemporary(
                    new MidLevelIrLoadIndirectRValue(
                        unitAddress,
                        unitType,
                        $"{dataPointer.Text}[{index}]"),
                    "load");
                if (loadedUnit is null)
                {
                    return false;
                }

                var unitMatches = EmitPairComparison(
                    loadedUnit,
                    CreateTextUnitConstant(units[index], unitType),
                    "==",
                    $"{text}:unit{index}");
                if (unitMatches is null)
                {
                    return false;
                }

                if (index == units.Length - 1)
                {
                    CurrentBlock.Terminator = new MidLevelIrTerminator(
                        MidLevelIrTerminatorKind.Branch,
                        [targetBlockId, nextTarget],
                        ConditionText: unitMatches.Text,
                        Condition: unitMatches);
                    return true;
                }

                var nextByteBlock = CreateBlock($"textcmp_byte_{index + 1}");
                CurrentBlock.Terminator = new MidLevelIrTerminator(
                    MidLevelIrTerminatorKind.Branch,
                    [nextByteBlock.Id, nextTarget],
                    ConditionText: unitMatches.Text,
                    Condition: unitMatches);
                CurrentBlock = nextByteBlock;
            }

            return true;
        }

        private MidLevelIrOperand? LowerSwitchCaseLiteral(StarkParser.LiteralContext literal, StarkTypeSymbol switchType)
        {
            if (switchType.Kind == StarkTypeKind.Integer && literal.signedIntegerLiteral() is { } integerLiteral)
            {
                return new MidLevelIrIntegerConstantOperand(ParseIntegerLiteral(integerLiteral), switchType);
            }

            if (switchType.Kind == StarkTypeKind.Bool)
            {
                if (literal.TRUE() is not null)
                {
                    return new MidLevelIrBoolConstantOperand(true);
                }

                if (literal.FALSE() is not null)
                {
                    return new MidLevelIrBoolConstantOperand(false);
                }
            }

            if (switchType.Kind == StarkTypeKind.Float && literal.FloatLiteral() is { } floatLiteral)
            {
                return new MidLevelIrFloatConstantOperand(floatLiteral.GetText(), switchType);
            }

            if (switchType.Kind == StarkTypeKind.RawPointer && literal.NULL() is not null)
            {
                return new MidLevelIrNullOperand(switchType);
            }

            if (switchType.Kind is StarkTypeKind.Ascii or StarkTypeKind.Unicode
                && (literal.StringLiteral() is not null || literal.CharacterLiteral() is not null))
            {
                return new MidLevelIrStringConstantOperand(literal.GetText(), switchType);
            }

            return LowerLiteral(literal, switchType);
        }

        private MidLevelIrOperand? EmitTemporary(MidLevelIrRValue value, string hint)
        {
            var name = AllocateTemporaryName(hint);
            RegisterLocal(name, value.Type, storageClass: "temp", isMutable: false, isConstant: false);
            Emit(MidLevelIrStatementKind.Assign, $"{name} = {value.Text}", name, value.Type, value);
            return new MidLevelIrLocalOperand(name, value.Type);
        }

        private MidLevelIrOperand EmitRequiredTemporary(MidLevelIrRValue value, string hint)
        {
            return EmitTemporary(value, hint)!;
        }

        private MidLevelIrLocalOperand CreateTemporaryLocal(StarkTypeSymbol type, string hint)
        {
            var name = AllocateTemporaryName(hint);
            RegisterLocal(name, type, storageClass: "temp", isMutable: false, isConstant: false);
            return new MidLevelIrLocalOperand(name, type);
        }

        private void EmitOperandAssignment(MidLevelIrLocalOperand target, MidLevelIrOperand value, string text)
        {
            Emit(
                MidLevelIrStatementKind.Assign,
                $"{target.Name} = {text}",
                target.Name,
                target.Type,
                new MidLevelIrUseRValue(value));
        }

        private bool TryLowerExpressionAsRValue(StarkParser.ExpressionContext expression, out MidLevelIrRValue value)
        {
            value = default!;
            if (TryGetSimplePostfixExpression(expression) is { } postfix
                && TryLowerCallExpression(postfix, out var call))
            {
                value = call;
                return true;
            }

            return false;
        }

        private static StarkParser.PostfixExpressionContext? TryGetSimplePostfixExpression(StarkParser.ExpressionContext expression)
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

            return TryGetSimplePostfixExpression(multiplicative.unaryExpression(0));
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

        private void EmitAssignmentFromExpression(
            string targetName,
            StarkTypeSymbol targetType,
            StarkParser.ExpressionContext expression,
            string text)
        {
            var operand = LowerExpressionToOperand(expression, targetType);
            if (operand is null)
            {
                MarkUnsupported(expression, $"Variable initializer '{text}' could not be lowered to a MIR operand.");
                Emit(MidLevelIrStatementKind.Assign, $"{targetName} = {text}", targetName, targetType);
                return;
            }

            Emit(MidLevelIrStatementKind.Assign, $"{targetName} = {text}", targetName, targetType, new MidLevelIrUseRValue(operand));
            RecordMoveFromOperand(operand, targetType);
        }

        private void RegisterLocal(string name, StarkTypeSymbol type, string storageClass, bool isMutable, bool isConstant)
        {
            if (_localsByName.ContainsKey(name))
            {
                return;
            }

            var local = new MidLevelIrLocal(
                name,
                type,
                storageClass,
                isMutable,
                isConstant,
                IsAddressable: ShouldAddressLocal(type, storageClass),
                Location: _currentStatementLocation ?? _functionLocation);
            _locals.Add(local);
            _localsByName[name] = local;
        }

        private void TrackDeclaredLocal(string name, StarkTypeSymbol type)
        {
            if (_scopes.Count == 0)
            {
                return;
            }

            _scopes.Peek().Locals.Add((name, type));
        }

        private void InitializeRuntimeDropState(string name, StarkTypeSymbol type, bool isActive)
        {
            if (!RequiresRuntimeDrop(type))
            {
                return;
            }

            _runtimeDropStates[name] = isActive;
        }

        private void SetRuntimeDropState(string name, bool isActive)
        {
            if (_runtimeDropStates.ContainsKey(name))
            {
                _runtimeDropStates[name] = isActive;
            }
        }

        private void EmitRuntimeDropIfActive(string name, StarkTypeSymbol type)
        {
            if (!_runtimeDropStates.TryGetValue(name, out var isActive) || !isActive)
            {
                return;
            }

            EmitRuntimeDropFromNamedValue(name, type);
            _runtimeDropStates[name] = false;
        }

        private void RecordMoveFromOperand(MidLevelIrOperand? operand, StarkTypeSymbol destinationType)
        {
            if (operand is null
                || destinationType.BorrowKind != StarkBorrowKind.None)
            {
                return;
            }

            switch (operand)
            {
                case MidLevelIrLocalOperand localOperand when _runtimeDropStates.ContainsKey(localOperand.Name):
                    _runtimeDropStates[localOperand.Name] = false;
                    break;
                case MidLevelIrParameterOperand parameterOperand when _runtimeDropStates.ContainsKey(parameterOperand.Name):
                    _runtimeDropStates[parameterOperand.Name] = false;
                    break;
            }
        }

        private bool RequiresRuntimeDrop(StarkTypeSymbol type)
        {
            return RequiresRuntimeDrop(type, new HashSet<string>(StringComparer.Ordinal));
        }

        private bool RequiresRuntimeDrop(StarkTypeSymbol type, HashSet<string> visiting)
        {
            if (type.BorrowKind != StarkBorrowKind.None)
            {
                return false;
            }

            if (type.Kind != StarkTypeKind.Named || type.NamedType is null)
            {
                return false;
            }

            if (!visiting.Add(type.NamedType))
            {
                return false;
            }

            try
            {
                if (TryGetDestructor(type, out _))
                {
                    return true;
                }

                if (TryGetEnumLayout(type, out var layout))
                {
                    foreach (var variant in layout.Variants.Values)
                    {
                        foreach (var field in variant.Fields)
                        {
                            if (RequiresRuntimeDrop(field.Type, visiting))
                            {
                                return true;
                            }
                        }
                    }

                    return false;
                }

                if (!_typeModel.NamedTypes.TryGetValue(type.NamedType, out var namedType)
                    || namedType.Kind is not (DeclarationKind.Struct or DeclarationKind.Record))
                {
                    return false;
                }

                foreach (var field in namedType.OrderedFields)
                {
                    if (RequiresRuntimeDrop(field.Type, visiting))
                    {
                        return true;
                    }
                }

                return false;
            }
            finally
            {
                visiting.Remove(type.NamedType);
            }
        }

        private bool TryGetDestructor(StarkTypeSymbol type, out DestructorLoweringContext destructor)
        {
            destructor = default!;

            if (type.NamedType is null)
            {
                return false;
            }

            var key = StarkTypeSymbols.GetGenericBaseName(type.NamedType);
            return _destructorsByTypeName.TryGetValue(key, out destructor!);
        }

        private bool TryGetEnumLayout(StarkTypeSymbol type, out EnumLayoutSymbol layout)
        {
            layout = default!;

            if (type.NamedType is null)
            {
                return false;
            }

            if (_enumLayoutModel.Layouts.TryGetValue(type.NamedType, out layout!))
            {
                return true;
            }

            var key = StarkTypeSymbols.GetGenericBaseName(type.NamedType);
            return _enumLayoutModel.Layouts.TryGetValue(key, out layout!);
        }

        private void EmitRuntimeDropFromNamedValue(string name, StarkTypeSymbol type)
        {
            var source = ResolveNamedOperand(name);
            if (source is null)
            {
                return;
            }

            EmitRuntimeDropFromOperand(source, type);
        }

        private void EmitRuntimeDropFromOperand(MidLevelIrOperand operand, StarkTypeSymbol type)
        {
            if (!RequiresRuntimeDrop(type))
            {
                return;
            }

            var temporary = CreateTemporaryLocal(type, "drop");
            EmitOperandAssignment(temporary, operand, operand.Text);

            if (TryGetDestructor(type, out var destructor))
            {
                using var destructorContext = PushDestructorContext(destructor.ModuleName, "self", temporary.Name);
                LowerBlock(destructor.Body);
            }

            if (TryGetEnumLayout(type, out var layout))
            {
                EmitEnumPayloadDrops(temporary, type, layout, new HashSet<string>(StringComparer.Ordinal));
                return;
            }

            EmitStructFieldDrops(temporary, type, new HashSet<string>(StringComparer.Ordinal));
        }

        private void EmitStructFieldDrops(
            MidLevelIrLocalOperand aggregate,
            StarkTypeSymbol type,
            HashSet<string> visiting)
        {
            if (type.Kind != StarkTypeKind.Named
                || type.NamedType is null
                || !_typeModel.NamedTypes.TryGetValue(type.NamedType, out var namedType)
                || namedType.Kind is not (DeclarationKind.Struct or DeclarationKind.Record)
                || !visiting.Add(type.NamedType))
            {
                return;
            }

            for (var index = namedType.OrderedFields.Count - 1; index >= 0; index--)
            {
                var field = namedType.OrderedFields[index];
                if (!RequiresRuntimeDrop(field.Type))
                {
                    continue;
                }

                var fieldValue = LowerKnownFieldAccess(aggregate, field.Name, index, field.Type, field.Name);
                EmitRuntimeDropFromOperand(fieldValue, field.Type);
            }

            visiting.Remove(type.NamedType);
        }

        private void EmitEnumPayloadDrops(
            MidLevelIrLocalOperand aggregate,
            StarkTypeSymbol type,
            EnumLayoutSymbol layout,
            HashSet<string> visiting)
        {
            if (!visiting.Add(layout.EnumName))
            {
                return;
            }

            try
            {
                var dropVariants = layout.Variants.Values
                    .Select(variant => (
                        Variant: variant,
                        Fields: variant.Fields
                            .Where(field => RequiresRuntimeDrop(field.Type, visiting))
                            .ToArray()))
                    .Where(static item => item.Fields.Length > 0)
                    .OrderBy(static item => item.Variant.TagValue)
                    .ToArray();
                if (dropVariants.Length == 0)
                {
                    return;
                }

                var tagValue = LowerKnownFieldAccess(aggregate, layout.TagField.Name, 0, layout.TagField.Type, "$tag");
                var joinBlock = CreateBlock("enum_drop_join");
                BasicBlockBuilder? nextDecisionBlock = CurrentBlock;

                for (var variantIndex = 0; variantIndex < dropVariants.Length; variantIndex++)
                {
                    if (nextDecisionBlock is null)
                    {
                        break;
                    }

                    CurrentBlock = nextDecisionBlock;

                    var (variant, fields) = dropVariants[variantIndex];
                    var matchBlock = CreateBlock($"enum_drop_{variant.Name}");
                    var fallthroughBlock = variantIndex == dropVariants.Length - 1
                        ? null
                        : CreateBlock($"enum_drop_next_{variantIndex}");
                    var expectedTag = new MidLevelIrIntegerConstantOperand(new BigInteger(variant.TagValue), layout.TagField.Type);
                    var condition = EmitResolvedEqualityComparison(tagValue, expectedTag, $"{aggregate.Text}.$tag == {variant.TagValue}");

                    CurrentBlock.Terminator = new MidLevelIrTerminator(
                        MidLevelIrTerminatorKind.Branch,
                        [matchBlock.Id, fallthroughBlock?.Id ?? joinBlock.Id],
                        ConditionText: $"{layout.EnumName}.{variant.Name}",
                        Condition: condition);

                    CurrentBlock = matchBlock;
                    for (var fieldIndex = fields.Length - 1; fieldIndex >= 0; fieldIndex--)
                    {
                        var field = fields[fieldIndex];
                        var displayName = field.SourceFieldName ?? $"[{field.SourcePosition}]";
                        var fieldValue = LowerKnownFieldAccess(
                            aggregate,
                            field.StorageFieldName,
                            field.StorageFieldIndex,
                            field.Type,
                            displayName);
                        EmitRuntimeDropFromOperand(fieldValue, field.Type);
                    }

                    EnsureGoto(joinBlock.Id);
                    nextDecisionBlock = fallthroughBlock;
                }

                CurrentBlock = joinBlock;
            }
            finally
            {
                visiting.Remove(layout.EnumName);
            }
        }

        private IDisposable PushDestructorContext(string moduleName, string aliasName, string localName)
        {
            var previousModuleName = _moduleNameOverride;
            var hadAlias = _nameAliases.TryGetValue(aliasName, out var previousAlias);
            _moduleNameOverride = moduleName;
            _nameAliases[aliasName] = localName;
            return new DestructorContext(this, previousModuleName, aliasName, previousAlias, hadAlias);
        }

        private void EmitStorageDead(ScopeFrame scope)
        {
            if (CurrentBlock.HasTerminator)
            {
                return;
            }

            var locals = scope.Locals.ToArray();
            for (var index = locals.Length - 1; index >= 0; index--)
            {
                var (name, type) = locals[index];
                EmitRuntimeDropIfActive(name, type);
                Emit(MidLevelIrStatementKind.StorageDead, name, name, type);
            }
        }

        private void EmitStorageDeadBeyondDepth(int depth)
        {
            if (CurrentBlock.HasTerminator)
            {
                return;
            }

            var scopesToDrop = _scopes
                .Take(Math.Max(0, _scopes.Count - depth))
                .ToArray();
            foreach (var scope in scopesToDrop)
            {
                var locals = scope.Locals.ToArray();
                for (var index = locals.Length - 1; index >= 0; index--)
                {
                    var (name, type) = locals[index];
                    EmitRuntimeDropIfActive(name, type);
                    Emit(MidLevelIrStatementKind.StorageDead, name, name, type);
                }
            }

            if (depth != 0)
            {
                return;
            }

            for (var index = _parameterDropOrder.Count - 1; index >= 0; index--)
            {
                var name = _parameterDropOrder[index];
                if (_parametersByName.TryGetValue(name, out var parameter))
                {
                    EmitRuntimeDropIfActive(name, parameter.Type);
                }
            }
        }

        private string? ResolveIndirectArgumentLocal(StarkTypeSymbol parameterType, MidLevelIrOperand argument)
        {
            if (!RequiresIndirectArgument(parameterType))
            {
                return null;
            }

            switch (argument)
            {
                case MidLevelIrLocalOperand localOperand:
                    EnsureAddressableLocal(localOperand.Name);
                    return localOperand.Name;
                case MidLevelIrParameterOperand parameterOperand when RequiresIndirectArgument(parameterOperand.Type):
                    return parameterOperand.Name;
                default:
                    return null;
            }
        }

        private static bool RequiresIndirectArgument(StarkTypeSymbol type)
        {
            return type.BorrowKind != StarkBorrowKind.None
                || type.InitializationKind != StarkInitializationKind.None;
        }

        private void EmitAssignment(LoweredAssignment assignment)
        {
            if (assignment.ReplacesWholeValue
                && assignment.TargetName is not null)
            {
                EmitRuntimeDropIfActive(assignment.TargetName, assignment.TargetType);
            }

            if (assignment.Address is not null)
            {
                Emit(
                    MidLevelIrStatementKind.StoreIndirect,
                    assignment.Text,
                    targetType: assignment.TargetType,
                    value: new MidLevelIrUseRValue(assignment.ResultValue),
                    address: assignment.Address);
                return;
            }

            Emit(MidLevelIrStatementKind.Assign, assignment.Text, assignment.TargetName, assignment.TargetType, value: assignment.DirectValue);
            if (assignment.ReplacesWholeValue
                && assignment.TargetName is not null)
            {
                SetRuntimeDropState(assignment.TargetName, isActive: true);
            }

            RecordMoveFromOperand(assignment.ResultValue, assignment.TargetType);
        }

        private void Emit(
            MidLevelIrStatementKind kind,
            string text,
            string? targetName = null,
            StarkTypeSymbol? targetType = null,
            MidLevelIrRValue? value = null,
            MidLevelIrOperand? address = null)
        {
            CurrentBlock.Statements.Add(new MidLevelIrStatement(
                kind,
                text,
                targetName,
                targetType,
                address,
                value,
                _currentStatementLocation ?? _functionLocation));
        }

        private void EnsureGoto(int targetBlockId)
        {
            if (!CurrentBlock.HasTerminator)
            {
                CurrentBlock.Terminator = new MidLevelIrTerminator(MidLevelIrTerminatorKind.Goto, [targetBlockId]);
            }
        }

        private string AllocateTemporaryName(string hint)
        {
            var name = $"$tmp{_nextTempId}_{hint}";
            _nextTempId++;
            return name;
        }

        private BasicBlockBuilder CreateBlock(string label)
        {
            var block = new BasicBlockBuilder(
                _nextBlockId,
                $"bb{_nextBlockId}_{label}",
                () => _currentStatementLocation ?? _functionLocation);
            _nextBlockId++;
            _blocks.Add(block);
            return block;
        }

        private void MarkUnsupported(
            ParserRuleContext? syntax = null,
            string? reason = null,
            string? featureTag = null,
            [CallerMemberName] string caller = "")
        {
            SupportsDirectCodeGeneration = false;

            var location = CreateSourceLocation(syntax?.Start) ?? _functionLocation;
            var logKey = string.Join(
                "|",
                caller,
                CurrentBlock.Id.ToString(),
                location.Line.ToString(),
                location.Column.ToString(),
                reason ?? string.Empty);

            if (!_unsupportedLogKeys.Add(logKey))
            {
                return;
            }

            var resolvedFeatureTag = featureTag ?? CreateFeatureTag(caller);
            var message = reason ?? $"Direct MIR lowering stopped in '{caller}'.";

            _logs.GapWarning(
                "lowering",
                "unsupported-lowering",
                message,
                featureTag: resolvedFeatureTag,
                reason: reason,
                operation: caller,
                location: location,
                outcome: CompilerLogOutcome.Unsupported,
                data: CompilerLogData.Create(
                        ("module", CurrentModuleName),
                    ("function", _function.Name),
                    ("bodyLoweringKind", _function.BodyLoweringKind.ToString()),
                    ("blockId", CurrentBlock.Id.ToString()),
                    ("blockLabel", CurrentBlock.Label),
                    ("syntaxText", TruncateForLog(syntax?.GetText()))));
        }

        private MidLevelIrOperand? UnsupportedOperand()
        {
            MarkUnsupported();
            return null;
        }

        private SourceLocation? CreateSourceLocation(IToken? token)
        {
            return token is null
                ? null
                : new SourceLocation(_moduleFilePath, token.Line, token.Column + 1);
        }

        private static string? TruncateForLog(string? text, int maxLength = 120)
        {
            if (string.IsNullOrWhiteSpace(text) || text.Length <= maxLength)
            {
                return text;
            }

            return $"{text[..maxLength]}...";
        }

        private static string CreateFeatureTag(string caller)
        {
            if (string.IsNullOrWhiteSpace(caller))
            {
                return "mir-lowering-gap";
            }

            var builder = new StringBuilder();
            for (var index = 0; index < caller.Length; index++)
            {
                var current = caller[index];
                if (char.IsUpper(current) && index > 0)
                {
                    builder.Append('-');
                }

                builder.Append(char.ToLowerInvariant(current));
            }

            return builder.ToString();
        }

        private static string FormatInitializer(StarkParser.VariableInitializerContext initializer)
        {
            if (initializer.expression() is { } expression)
            {
                return expression.GetText();
            }

            if (initializer.objectInitializer() is { } objectInitializer)
            {
                return objectInitializer.GetText();
            }

            return initializer.arrayInitializer()?.GetText() ?? "<init>";
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

        private static MidLevelIrBinaryOperator MapBinaryOperator(string text)
        {
            return text switch
            {
                "+" => MidLevelIrBinaryOperator.Add,
                "-" => MidLevelIrBinaryOperator.Subtract,
                "*" => MidLevelIrBinaryOperator.Multiply,
                "**" => MidLevelIrBinaryOperator.Exponent,
                "+%" => MidLevelIrBinaryOperator.WrappingAdd,
                "-%" => MidLevelIrBinaryOperator.WrappingSubtract,
                "*%" => MidLevelIrBinaryOperator.WrappingMultiply,
                "+|" => MidLevelIrBinaryOperator.SaturatingAdd,
                "-|" => MidLevelIrBinaryOperator.SaturatingSubtract,
                "*|" => MidLevelIrBinaryOperator.SaturatingMultiply,
                "/" => MidLevelIrBinaryOperator.Divide,
                "%" => MidLevelIrBinaryOperator.Modulo,
                "&" => MidLevelIrBinaryOperator.BitwiseAnd,
                "^" => MidLevelIrBinaryOperator.BitwiseXor,
                "|" => MidLevelIrBinaryOperator.BitwiseOr,
                "<<" => MidLevelIrBinaryOperator.ShiftLeft,
                ">>" => MidLevelIrBinaryOperator.ShiftRight,
                "==" => MidLevelIrBinaryOperator.Equal,
                "!=" => MidLevelIrBinaryOperator.NotEqual,
                "<" => MidLevelIrBinaryOperator.LessThan,
                "<=" => MidLevelIrBinaryOperator.LessThanOrEqual,
                ">" => MidLevelIrBinaryOperator.GreaterThan,
                ">=" => MidLevelIrBinaryOperator.GreaterThanOrEqual,
                _ => throw new InvalidOperationException($"Unsupported binary operator '{text}'.")
            };
        }

        private static MidLevelIrBinaryOperator MapAssignmentOperator(string text)
        {
            return text switch
            {
                "+=" => MidLevelIrBinaryOperator.Add,
                "-=" => MidLevelIrBinaryOperator.Subtract,
                "*=" => MidLevelIrBinaryOperator.Multiply,
                "+%=" => MidLevelIrBinaryOperator.WrappingAdd,
                "-%=" => MidLevelIrBinaryOperator.WrappingSubtract,
                "*%=" => MidLevelIrBinaryOperator.WrappingMultiply,
                "+|=" => MidLevelIrBinaryOperator.SaturatingAdd,
                "-|=" => MidLevelIrBinaryOperator.SaturatingSubtract,
                "*|=" => MidLevelIrBinaryOperator.SaturatingMultiply,
                "/=" => MidLevelIrBinaryOperator.Divide,
                "%=" => MidLevelIrBinaryOperator.Modulo,
                "&=" => MidLevelIrBinaryOperator.BitwiseAnd,
                "^=" => MidLevelIrBinaryOperator.BitwiseXor,
                "|=" => MidLevelIrBinaryOperator.BitwiseOr,
                _ => throw new InvalidOperationException($"Unsupported assignment operator '{text}'.")
            };
        }

        private static StarkTypeSymbol FindCommonType(StarkTypeSymbol left, StarkTypeSymbol right)
        {
            if (left.Kind == StarkTypeKind.Error || right.Kind == StarkTypeKind.Error)
            {
                return StarkTypeSymbols.Error;
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

            if (left.Kind == StarkTypeKind.Bool && right.Kind == StarkTypeKind.Bool)
            {
                return StarkTypeSymbols.Bool;
            }

            if (left.Kind == StarkTypeKind.RawPointer && right.Kind == StarkTypeKind.Null)
            {
                return left;
            }

            if (left.Kind == StarkTypeKind.Null && right.Kind == StarkTypeKind.RawPointer)
            {
                return right;
            }

            return left.DisplayName == right.DisplayName
                ? left
                : StarkTypeSymbols.Error;
        }

        private static bool HasSameStorageType(StarkTypeSymbol left, StarkTypeSymbol right)
        {
            if (left.Kind != right.Kind)
            {
                return false;
            }

            return left.Kind switch
            {
                StarkTypeKind.Integer => left.BitWidth == right.BitWidth,
                StarkTypeKind.Float => left.BitWidth == right.BitWidth,
                StarkTypeKind.RawPointer => true,
                _ => left.DisplayName == right.DisplayName
            };
        }

        private bool IsAddressableLocal(string name)
        {
            return _localsByName.TryGetValue(name, out var local) && local.IsAddressable;
        }

        private static bool SupportsAddressModel(MidLevelIrOperand? operand)
        {
            return operand is MidLevelIrLocalOperand or MidLevelIrParameterOperand or MidLevelIrGlobalOperand or MidLevelIrGlobalAddressOperand;
        }

        private bool IsBorrowParameterRoot(MidLevelIrOperand? operand)
        {
            return operand is MidLevelIrParameterOperand parameter
                && _parametersByName.TryGetValue(parameter.Name, out var parameterBinding)
                && parameterBinding.Type.BorrowKind != StarkBorrowKind.None;
        }

        private bool TryInitializePointerPlaceRoot(
            StarkParser.UnaryExpressionContext expression,
            out MidLevelIrOperand address,
            out StarkTypeSymbol rootType,
            out bool isAddressMutable)
        {
            address = default!;
            rootType = StarkTypeSymbols.Error;
            isAddressMutable = false;

            if (expression.conversionType() is not null
                || expression.powerExpression() is not null
                || !string.Equals(expression.unaryOperator()?.GetText(), "*", StringComparison.Ordinal))
            {
                return false;
            }

            var loweredPointer = LowerUnaryExpression(expression.unaryExpression(), expectedType: null);
            if (loweredPointer is null
                || loweredPointer.Type.Kind != StarkTypeKind.RawPointer
                || loweredPointer.Type.ElementType is null)
            {
                return false;
            }

            address = loweredPointer;
            rootType = loweredPointer.Type.ElementType;
            isAddressMutable = loweredPointer.Type.IsMutablePointer && CanMutateThroughType(rootType);
            return true;
        }

        private void EnsureAddressableLocal(string name)
        {
            if (!_localsByName.TryGetValue(name, out var local) || local.IsAddressable)
            {
                return;
            }

            var addressableLocal = local with { IsAddressable = true };
            _localsByName[name] = addressableLocal;

            for (var index = 0; index < _locals.Count; index++)
            {
                if (string.Equals(_locals[index].Name, name, StringComparison.Ordinal))
                {
                    _locals[index] = addressableLocal;
                    break;
                }
            }
        }

        private MidLevelIrOperand? CreateAddressOfLocal(string name, StarkTypeSymbol type)
        {
            EnsureAddressableLocal(name);
            var isMutable = _localsByName.TryGetValue(name, out var local)
                ? !local.IsConstant && CanMutateThroughType(local.Type)
                : true;
            return EmitTemporary(
                new MidLevelIrAddressOfLocalRValue(name, type, AddressType(type, isMutable), $"&{name}"),
                "addr");
        }

        private MidLevelIrOperand? CreateAddressOfParameter(string name, StarkTypeSymbol type)
        {
            var isMutable = _parametersByName.TryGetValue(name, out var parameter)
                ? CanMutateThroughType(parameter.Type)
                : true;
            return EmitTemporary(
                new MidLevelIrAddressOfParameterRValue(name, type, AddressType(type, isMutable), $"&{name}"),
                "addr");
        }

        private MidLevelIrOperand CreateAddressOfGlobal(string name, StarkTypeSymbol type)
        {
            var isMutable = _typeModel.Globals.TryGetValue(name, out var global)
                ? global.IsMutable && CanMutateThroughType(global.Type)
                : true;
            return new MidLevelIrGlobalAddressOperand(name, type, AddressType(type, isMutable));
        }

        private static bool ShouldAddressLocal(StarkTypeSymbol type, string storageClass)
        {
            if (storageClass == "heap")
            {
                return true;
            }

            return storageClass is "arena" or "static"
                && type.Kind is StarkTypeKind.Named or StarkTypeKind.FixedArray;
        }

        private static StarkTypeSymbol AddressType(StarkTypeSymbol pointeeType, bool isMutable)
        {
            return StarkTypeSymbols.RawPointer(pointeeType, isMutable);
        }

        private bool GetAddressMutability(MidLevelIrOperand operand)
        {
            return operand switch
                {
                    MidLevelIrLocalOperand local => _localsByName.TryGetValue(local.Name, out var localBinding)
                    ? !localBinding.IsConstant && CanMutateThroughType(localBinding.Type)
                    : true,
                MidLevelIrGlobalOperand global => _typeModel.Globals.TryGetValue(global.Name, out var globalBinding)
                    ? globalBinding.IsMutable && CanMutateThroughType(globalBinding.Type)
                    : true,
                MidLevelIrParameterOperand parameter => CanMutateThroughType(parameter.Type),
                MidLevelIrGlobalAddressOperand globalAddress => globalAddress.Type.IsMutablePointer,
                _ => true
            };
        }

        private static StarkTypeSymbol ProjectFrozenView(StarkTypeSymbol sourceType, StarkTypeSymbol projectedType)
        {
            return sourceType.AccessKind == StarkAccessKind.Frozen
                ? StarkTypeSymbols.FreezeReachableView(projectedType)
                : projectedType;
        }

        private static bool CanMutateThroughType(StarkTypeSymbol type) => type.AccessKind != StarkAccessKind.Frozen;

        private static bool CanLowerSwitchType(StarkTypeSymbol type)
        {
            return type.Kind is StarkTypeKind.Integer
                or StarkTypeKind.Float
                or StarkTypeKind.Bool
                or StarkTypeKind.RawPointer
                or StarkTypeKind.Ascii
                or StarkTypeKind.Unicode
                or StarkTypeKind.Named;
        }

        private static bool CanUsePartitionedTextSwitchType(StarkTypeSymbol type)
        {
            return type.Kind is StarkTypeKind.Ascii or StarkTypeKind.Unicode;
        }

        private static bool CanUseNativeSwitchType(StarkTypeSymbol type)
        {
            return type.Kind is StarkTypeKind.Integer or StarkTypeKind.Bool;
        }

        private static bool CanUseNativeSwitchCase(StarkTypeSymbol caseType, StarkTypeSymbol switchType)
        {
            return CanUseNativeSwitchType(caseType) && HasSameStorageType(caseType, switchType);
        }

        private static BigInteger ParseIntegerLiteral(StarkParser.SignedIntegerLiteralContext literal)
        {
            var value = BigInteger.Parse(literal.IntegerLiteral().GetText());
            return literal.MINUS() is null ? value : -value;
        }

        private static BigInteger ParseIntegerLiteralText(string literalText)
        {
            return BigInteger.Parse(literalText);
        }

        private static BigInteger ToSignedByteValue(byte value)
        {
            return value <= sbyte.MaxValue
                ? new BigInteger(value)
                : new BigInteger(unchecked((sbyte)value));
        }

        private static bool TryConvertTextLiteral(
            MidLevelIrOperand operand,
            StarkTypeSymbol targetType,
            out MidLevelIrOperand converted)
        {
            converted = null!;
            if (operand is not MidLevelIrStringConstantOperand textConstant)
            {
                return false;
            }

            if (targetType.Kind == StarkTypeKind.Unicode && operand.Type.Kind == StarkTypeKind.Ascii)
            {
                converted = new MidLevelIrStringConstantOperand(textConstant.LiteralText, targetType);
                return true;
            }

            if (targetType.Kind == StarkTypeKind.Ascii
                && operand.Type.Kind == StarkTypeKind.Unicode
                && TextLiteralDecoder.CanUseUtf8Storage(
                    textConstant.LiteralText,
                    textConstant.LiteralText.StartsWith('\'') ? TextLiteralKind.Character : TextLiteralKind.String))
            {
                converted = new MidLevelIrStringConstantOperand(textConstant.LiteralText, targetType);
                return true;
            }

            return false;
        }

        private static int[] DecodeTextLiteralUnits(string literalText, StarkTypeSymbol textType)
        {
            var kind = literalText.StartsWith('\'')
                ? TextLiteralKind.Character
                : TextLiteralKind.String;

            return textType.Kind switch
            {
                StarkTypeKind.Ascii => TextLiteralDecoder.DecodeUtf8BytesOrFallback(literalText, kind)
                    .Select(static value => (int)value)
                    .ToArray(),
                StarkTypeKind.Unicode => TextLiteralDecoder.DecodeUtf32CodeUnitsOrFallback(literalText, kind),
                _ => throw new InvalidOperationException($"Text literal decoding requires an ascii/unicode target, but found '{textType.DisplayName}'.")
            };
        }

        private static StarkTypeSymbol GetTextUnitType(StarkTypeSymbol textType)
        {
            return textType.Kind switch
            {
                StarkTypeKind.Ascii => StarkTypeSymbols.Integer(8),
                StarkTypeKind.Unicode => StarkTypeSymbols.Integer(32),
                _ => throw new InvalidOperationException($"Text unit type requires an ascii/unicode value, but found '{textType.DisplayName}'.")
            };
        }

        private static MidLevelIrIntegerConstantOperand CreateTextUnitConstant(int value, StarkTypeSymbol unitType)
        {
            return unitType.BitWidth == 8
                ? new MidLevelIrIntegerConstantOperand(ToSignedByteValue((byte)value), unitType)
                : new MidLevelIrIntegerConstantOperand(new BigInteger(value), unitType);
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

        private sealed class BasicBlockBuilder
        {
            private readonly Func<SourceLocation?> _locationProvider;
            private MidLevelIrTerminator? _terminator;

            public BasicBlockBuilder(int id, string label, Func<SourceLocation?> locationProvider)
            {
                Id = id;
                Label = label;
                _locationProvider = locationProvider;
            }

            public int Id { get; }

            public string Label { get; }

            public List<MidLevelIrStatement> Statements { get; } = [];

            public MidLevelIrTerminator? Terminator
            {
                get => _terminator;
                set => _terminator = value is null || value.Location is not null
                    ? value
                    : value with { Location = _locationProvider() };
            }

            public bool HasTerminator => Terminator is not null;

            public MidLevelIrBasicBlock Build()
            {
                return new MidLevelIrBasicBlock(
                    Id,
                    Label,
                    Statements.ToArray(),
                    Terminator ?? new MidLevelIrTerminator(
                        MidLevelIrTerminatorKind.Unreachable,
                        Targets: [],
                        Location: _locationProvider()));
            }
        }

        private readonly record struct LoopTargets(int ContinueTarget, int BreakTarget, int ScopeDepth);
        private readonly record struct BreakTargets(int Target, int ScopeDepth);
    }
}
