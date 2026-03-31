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
    StarkParser.FunctionBodyContext Body)
{
    public bool HasBody => Body.block() is not null;
}

internal static class DeclaredFunctionSyntaxCollector
{
    public static IReadOnlyList<DeclaredFunctionSyntax> Collect(ParseResult parseResult)
    {
        var functions = new List<DeclaredFunctionSyntax>();

        foreach (var declaration in parseResult.Root.topLevelDeclaration())
        {
            var visibility = ParseVisibility(declaration.visibilityModifier());

            if (declaration.functionDeclaration() is { } functionDeclaration)
            {
                functions.Add(CreateTopLevelFunction(functionDeclaration, visibility));
                continue;
            }

            if (declaration.structDeclaration() is { } structDeclaration)
            {
                var typeName = structDeclaration.Identifier().GetText();
                functions.AddRange(
                    structDeclaration.structBody().structMember()
                        .Select(static member => member.methodDeclaration())
                        .Where(static method => method is not null)!
                        .Select(method => CreateMethod(typeName, method, visibility)));
                continue;
            }

            if (declaration.recordDeclaration() is { } recordDeclaration)
            {
                var typeName = recordDeclaration.Identifier().GetText();
                functions.AddRange(
                    recordDeclaration.recordBody().recordMember()
                        .Select(static member => member.methodDeclaration())
                        .Where(static method => method is not null)!
                        .Select(method => CreateMethod(typeName, method, visibility)));
                continue;
            }

            if (declaration.traitDeclaration() is { } traitDeclaration)
            {
                var typeName = traitDeclaration.Identifier().GetText();
                functions.AddRange(
                    traitDeclaration.traitBody().traitMember()
                        .Select(static member => member.traitMethodDeclaration())
                        .Where(static method => method is not null)!
                        .Select(method => CreateTraitMethod(typeName, method, visibility)));
                continue;
            }

            if (declaration.doctrineDeclaration() is { } doctrineDeclaration)
            {
                var typeName = doctrineDeclaration.Identifier().GetText();
                functions.AddRange(
                    doctrineDeclaration.doctrineBody().doctrineMember()
                        .Select(static member => member.doctrineMethodDeclaration())
                        .Where(static method => method is not null)!
                        .Select(method => CreateDoctrineMethod(typeName, method, visibility)));
            }
        }

        return functions;
    }

    private static DeclaredFunctionSyntax CreateTopLevelFunction(
        StarkParser.FunctionDeclarationContext declaration,
        StarkVisibility visibility)
    {
        return new DeclaredFunctionSyntax(
            declaration.Identifier().GetText(),
            ContainingTypeName: null,
            visibility,
            declaration,
            declaration.Identifier().Symbol,
            ParseFunctionKind(declaration.functionKind()),
            declaration.returnType(),
            declaration.parameterList(),
            declaration.typeParameterList(),
            declaration.functionModifier(),
            declaration.functionBody());
    }

    private static DeclaredFunctionSyntax CreateMethod(
        string containingTypeName,
        StarkParser.MethodDeclarationContext declaration,
        StarkVisibility visibility)
    {
        return new DeclaredFunctionSyntax(
            $"{containingTypeName}.{declaration.Identifier().GetText()}",
            containingTypeName,
            visibility,
            declaration,
            declaration.Identifier().Symbol,
            ParseFunctionKind(declaration.functionKind()),
            declaration.returnType(),
            declaration.parameterList(),
            declaration.typeParameterList(),
            declaration.functionModifier(),
            declaration.functionBody());
    }

    private static DeclaredFunctionSyntax CreateTraitMethod(
        string containingTypeName,
        StarkParser.TraitMethodDeclarationContext declaration,
        StarkVisibility visibility)
    {
        return new DeclaredFunctionSyntax(
            $"{containingTypeName}.{declaration.Identifier().GetText()}",
            containingTypeName,
            visibility,
            declaration,
            declaration.Identifier().Symbol,
            ParseFunctionKind(declaration.functionKind()),
            declaration.returnType(),
            declaration.parameterList(),
            declaration.typeParameterList(),
            declaration.functionModifier(),
            declaration.functionBody());
    }

    private static DeclaredFunctionSyntax CreateDoctrineMethod(
        string containingTypeName,
        StarkParser.DoctrineMethodDeclarationContext declaration,
        StarkVisibility visibility)
    {
        return new DeclaredFunctionSyntax(
            $"{containingTypeName}.{declaration.Identifier().GetText()}",
            containingTypeName,
            visibility,
            declaration,
            declaration.Identifier().Symbol,
            ParseDoctrineFunctionKind(declaration.doctrineFunctionKind()),
            declaration.returnType(),
            declaration.parameterList(),
            declaration.typeParameterList(),
            declaration.functionModifier(),
            declaration.functionBody());
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
