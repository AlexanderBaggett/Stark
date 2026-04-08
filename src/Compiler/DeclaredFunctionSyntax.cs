using Antlr4.Runtime;
using Stark.Parsing;

namespace Stark.Compiler;

internal sealed record DeclaredFunctionSyntax(
    string Name,
    string? ContainingTypeName,
    StarkVisibility Visibility,
    ParserRuleContext DeclarationContext,
    IToken NameToken,
    StarkFunctionKind DeclaredKind,
    StarkParser.ReturnTypeContext ReturnType,
    StarkParser.ParameterListContext ParameterList,
    StarkParser.TypeParameterListContext? TypeParameters,
    IReadOnlyList<StarkParser.FunctionModifierContext> Modifiers,
    StarkParser.FunctionBodyContext Body,
    string? SourceName = null)
{
    public bool HasBody => Body.block() is not null;

    public string DisplaySourceName => SourceName ?? Name;
}

internal sealed record DeclaredDestructorSyntax(
    string QualifiedTypeName,
    string LocalTypeName,
    string ModuleName,
    bool IsMutable,
    StarkParser.DestructorDeclarationContext Declaration,
    StarkParser.BlockContext Body);

internal static class DeclaredFunctionSyntaxCollector
{
    public static IReadOnlyList<DeclaredFunctionSyntax> Collect(ParseResult parseResult)
    {
        return Collect(parseResult, syntaxModel: null);
    }

    public static IReadOnlyList<DeclaredFunctionSyntax> Collect(ParseResult parseResult, SyntaxModel? syntaxModel)
    {
        var functions = new List<DeclaredFunctionSyntax>();
        var selectedFunctionsByIdentity = syntaxModel?.Declarations
            .Where(static declaration => declaration.Kind == DeclarationKind.Function && declaration.Function is not null)
            .GroupBy(static declaration => declaration.Name, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => group
                    .SelectMany(static declaration => FunctionOverloadFacts.GetDeclaredOverloadKeys(declaration)
                        .Select(overloadKey => CreateFunctionIdentity(
                            overloadKey,
                            declaration.Function!.Asm?.ArchitectureText)))
                    .ToHashSet(StringComparer.Ordinal),
                StringComparer.Ordinal);

        foreach (var declaration in parseResult.Root.topLevelDeclaration())
        {
            var visibility = ParseVisibility(declaration.visibilityModifier());

            if (declaration.functionDeclaration() is { } functionDeclaration)
            {
                if (!ShouldIncludeTopLevelFunction(selectedFunctionsByIdentity, functionDeclaration))
                {
                    continue;
                }

                functions.Add(CreateTopLevelFunction(functionDeclaration, visibility, syntaxModel));
                continue;
            }

            if (declaration.structDeclaration() is { } structDeclaration)
            {
                var typeName = structDeclaration.Identifier().GetText();
                functions.AddRange(
                    structDeclaration.structBody().structMember()
                        .Select(static member => member.methodDeclaration())
                        .Where(static method => method is not null)!
                        .Where(method => ShouldIncludeMemberFunction(
                            selectedFunctionsByIdentity,
                            typeName,
                            method!.Identifier().GetText(),
                            method.parameterList()))
                        .Select(method => CreateMethod(typeName, method!, visibility, syntaxModel)));
                continue;
            }

            if (declaration.recordDeclaration() is { } recordDeclaration)
            {
                var typeName = recordDeclaration.Identifier().GetText();
                functions.AddRange(
                    recordDeclaration.recordBody().recordMember()
                        .Select(static member => member.methodDeclaration())
                        .Where(static method => method is not null)!
                        .Where(method => ShouldIncludeMemberFunction(
                            selectedFunctionsByIdentity,
                            typeName,
                            method!.Identifier().GetText(),
                            method.parameterList()))
                        .Select(method => CreateMethod(typeName, method!, visibility, syntaxModel)));
                continue;
            }

            if (declaration.traitDeclaration() is { } traitDeclaration)
            {
                var typeName = traitDeclaration.Identifier().GetText();
                functions.AddRange(
                    traitDeclaration.traitBody().traitMember()
                        .Select(static member => member.traitMethodDeclaration())
                        .Where(static method => method is not null)!
                        .Where(method => ShouldIncludeMemberFunction(
                            selectedFunctionsByIdentity,
                            typeName,
                            method!.Identifier().GetText(),
                            method.parameterList()))
                        .Select(method => CreateTraitMethod(typeName, method!, visibility, syntaxModel)));
                continue;
            }

            if (declaration.doctrineDeclaration() is { } doctrineDeclaration)
            {
                var typeName = doctrineDeclaration.Identifier().GetText();
                functions.AddRange(
                    doctrineDeclaration.doctrineBody().doctrineMember()
                        .Select(static member => member.doctrineMethodDeclaration())
                        .Where(static method => method is not null)!
                        .Where(method => ShouldIncludeMemberFunction(
                            selectedFunctionsByIdentity,
                            typeName,
                            method!.Identifier().GetText(),
                            method.parameterList()))
                        .Select(method => CreateDoctrineMethod(typeName, method!, visibility, syntaxModel)));
            }
        }

        return functions;
    }

    private static bool ShouldIncludeTopLevelFunction(
        IReadOnlyDictionary<string, HashSet<string>>? selectedFunctionsByIdentity,
        StarkParser.FunctionDeclarationContext declaration)
    {
        if (selectedFunctionsByIdentity is null)
        {
            return true;
        }

        var name = declaration.Identifier().GetText();
        if (!selectedFunctionsByIdentity.TryGetValue(name, out var selected))
        {
            return false;
        }

        var identity = CreateFunctionIdentity(
            FunctionOverloadFacts.BuildOverloadKey(declaration.parameterList()),
            declaration.asmSpecifier()?.Identifier().GetText());
        return selected.Contains(identity);
    }

    private static bool ShouldIncludeMemberFunction(
        IReadOnlyDictionary<string, HashSet<string>>? selectedFunctionsByIdentity,
        string containingTypeName,
        string methodName,
        StarkParser.ParameterListContext parameterList)
    {
        if (selectedFunctionsByIdentity is null)
        {
            return true;
        }

        var sourceName = $"{containingTypeName}.{methodName}";
        return selectedFunctionsByIdentity.TryGetValue(sourceName, out var selected)
            && selected.Contains(CreateFunctionIdentity(
                FunctionOverloadFacts.BuildOverloadKey(parameterList),
                architectureText: null));
    }

    private static DeclaredFunctionSyntax CreateTopLevelFunction(
        StarkParser.FunctionDeclarationContext declaration,
        StarkVisibility visibility,
        SyntaxModel? syntaxModel)
    {
        var sourceName = declaration.Identifier().GetText();
        return new DeclaredFunctionSyntax(
            ResolveFunctionName(syntaxModel, sourceName, declaration.parameterList()),
            ContainingTypeName: null,
            visibility,
            declaration,
            declaration.Identifier().Symbol,
            ParseFunctionKind(declaration.functionKind()),
            declaration.returnType(),
            declaration.parameterList(),
            declaration.typeParameterList(),
            declaration.functionModifier(),
            declaration.functionBody(),
            SourceName: sourceName);
    }

    private static DeclaredFunctionSyntax CreateMethod(
        string containingTypeName,
        StarkParser.MethodDeclarationContext declaration,
        StarkVisibility visibility,
        SyntaxModel? syntaxModel)
    {
        var sourceName = $"{containingTypeName}.{declaration.Identifier().GetText()}";
        return new DeclaredFunctionSyntax(
            ResolveFunctionName(syntaxModel, sourceName, declaration.parameterList()),
            containingTypeName,
            visibility,
            declaration,
            declaration.Identifier().Symbol,
            ParseFunctionKind(declaration.functionKind()),
            declaration.returnType(),
            declaration.parameterList(),
            declaration.typeParameterList(),
            declaration.functionModifier(),
            declaration.functionBody(),
            SourceName: sourceName);
    }

    private static DeclaredFunctionSyntax CreateTraitMethod(
        string containingTypeName,
        StarkParser.TraitMethodDeclarationContext declaration,
        StarkVisibility visibility,
        SyntaxModel? syntaxModel)
    {
        var sourceName = $"{containingTypeName}.{declaration.Identifier().GetText()}";
        return new DeclaredFunctionSyntax(
            ResolveFunctionName(syntaxModel, sourceName, declaration.parameterList()),
            containingTypeName,
            visibility,
            declaration,
            declaration.Identifier().Symbol,
            ParseFunctionKind(declaration.functionKind()),
            declaration.returnType(),
            declaration.parameterList(),
            declaration.typeParameterList(),
            declaration.functionModifier(),
            declaration.functionBody(),
            SourceName: sourceName);
    }

    private static DeclaredFunctionSyntax CreateDoctrineMethod(
        string containingTypeName,
        StarkParser.DoctrineMethodDeclarationContext declaration,
        StarkVisibility visibility,
        SyntaxModel? syntaxModel)
    {
        var sourceName = $"{containingTypeName}.{declaration.Identifier().GetText()}";
        return new DeclaredFunctionSyntax(
            ResolveFunctionName(syntaxModel, sourceName, declaration.parameterList()),
            containingTypeName,
            visibility,
            declaration,
            declaration.Identifier().Symbol,
            ParseDoctrineFunctionKind(declaration.doctrineFunctionKind()),
            declaration.returnType(),
            declaration.parameterList(),
            declaration.typeParameterList(),
            declaration.functionModifier(),
            declaration.functionBody(),
            SourceName: sourceName);
    }

    private static string ResolveFunctionName(
        SyntaxModel? syntaxModel,
        string sourceName,
        StarkParser.ParameterListContext parameterList)
    {
        if (syntaxModel is null)
        {
            return sourceName;
        }

        var overloadKey = FunctionOverloadFacts.BuildOverloadKey(parameterList);
        return FunctionOverloadFacts.TryFindFunctionDeclaration(syntaxModel, sourceName, overloadKey, out var declaration)
            ? FunctionOverloadFacts.GetResolvedLocalName(syntaxModel, declaration)
            : FunctionOverloadFacts.GetResolvedLocalName(syntaxModel, sourceName, overloadKey);
    }

    private static string CreateFunctionIdentity(string overloadKey, string? architectureText)
    {
        return $"{overloadKey}|{architectureText ?? string.Empty}";
    }

    private static StarkVisibility ParseVisibility(StarkParser.VisibilityModifierContext? visibilityModifier)
    {
        if (visibilityModifier is null)
        {
            return StarkVisibility.Module;
        }

        return visibilityModifier.GetText() switch
        {
            "internal" => StarkVisibility.Internal,
            "public" => StarkVisibility.Public,
            "export" => StarkVisibility.Export,
            _ => StarkVisibility.Module
        };
    }

    private static StarkFunctionKind ParseFunctionKind(StarkParser.FunctionKindContext functionKind)
    {
        return functionKind.GetText() switch
        {
            "fn" => StarkFunctionKind.Fn,
            "finite" => StarkFunctionKind.Finite,
            "law" => StarkFunctionKind.Law,
            "finitelaw" => StarkFunctionKind.FiniteLaw,
            _ => throw new InvalidOperationException($"Unsupported function kind '{functionKind.GetText()}'.")
        };
    }

    private static StarkFunctionKind ParseDoctrineFunctionKind(StarkParser.DoctrineFunctionKindContext functionKind)
    {
        return functionKind.GetText() switch
        {
            "law" => StarkFunctionKind.Law,
            "finitelaw" => StarkFunctionKind.FiniteLaw,
            _ => throw new InvalidOperationException($"Unsupported doctrine function kind '{functionKind.GetText()}'.")
        };
    }
}

internal static class DeclaredDestructorSyntaxCollector
{
    public static IReadOnlyList<DeclaredDestructorSyntax> Collect(LoadedModuleDocument module)
    {
        var destructors = new List<DeclaredDestructorSyntax>();

        foreach (var declaration in module.ParseResult.Root.topLevelDeclaration())
        {
            if (declaration.structDeclaration() is { } structDeclaration)
            {
                AddDestructors(
                    destructors,
                    module,
                    structDeclaration.Identifier().GetText(),
                    structDeclaration.structBody().structMember()
                        .Select(static member => member.destructorDeclaration())
                        .Where(static destructor => destructor is not null)!
                        .Cast<StarkParser.DestructorDeclarationContext>());
                continue;
            }

            if (declaration.recordDeclaration() is { } recordDeclaration)
            {
                AddDestructors(
                    destructors,
                    module,
                    recordDeclaration.Identifier().GetText(),
                    recordDeclaration.recordBody().recordMember()
                        .Select(static member => member.destructorDeclaration())
                        .Where(static destructor => destructor is not null)!
                        .Cast<StarkParser.DestructorDeclarationContext>());
            }
        }

        return destructors;
    }

    private static void AddDestructors(
        List<DeclaredDestructorSyntax> destructors,
        LoadedModuleDocument module,
        string localTypeName,
        IEnumerable<StarkParser.DestructorDeclarationContext> declarations)
    {
        foreach (var declaration in declarations)
        {
            destructors.Add(new DeclaredDestructorSyntax(
                QualifiedTypeName: module.Reference.IsRoot ? localTypeName : $"{module.SyntaxModel.ModuleName}.{localTypeName}",
                LocalTypeName: localTypeName,
                ModuleName: module.SyntaxModel.ModuleName,
                IsMutable: declaration.MUT() is not null,
                Declaration: declaration,
                Body: declaration.block()));
        }
    }
}
