using Stark.Parsing;

namespace Stark.Compiler;

internal static class SyntaxModelFactory
{
    public static SyntaxModel Create(ParseResult parseResult)
    {
        var root = parseResult.Root;

        return new SyntaxModel(
            ModuleName: root.moduleDeclaration().qualifiedName().GetText(),
            Imports: root.importDeclaration().Select(CreateImportModel).ToArray(),
            Declarations: root.topLevelDeclaration().Select(CreateDeclarationModel).ToArray());
    }

    private static ImportDeclarationModel CreateImportModel(StarkParser.ImportDeclarationContext importDeclaration)
    {
        return new ImportDeclarationModel(
            importDeclaration.qualifiedName().GetText(),
            importDeclaration.EXPORT() is not null);
    }

    private static TopLevelDeclarationModel CreateDeclarationModel(StarkParser.TopLevelDeclarationContext declaration)
    {
        var visibility = ParseVisibility(declaration.visibilityModifier());

        if (declaration.functionDeclaration() is { } function)
        {
            return new TopLevelDeclarationModel(
                function.Identifier().GetText(),
                DeclarationKind.Function,
                visibility,
                CreateFunctionModel(function));
        }

        if (declaration.structDeclaration() is { } structDeclaration)
        {
            return new TopLevelDeclarationModel(
                structDeclaration.Identifier().GetText(),
                DeclarationKind.Struct,
                visibility,
                null);
        }

        if (declaration.recordDeclaration() is { } recordDeclaration)
        {
            return new TopLevelDeclarationModel(
                recordDeclaration.Identifier().GetText(),
                DeclarationKind.Record,
                visibility,
                null);
        }

        if (declaration.traitDeclaration() is { } traitDeclaration)
        {
            return new TopLevelDeclarationModel(
                traitDeclaration.Identifier().GetText(),
                DeclarationKind.Trait,
                visibility,
                null);
        }

        if (declaration.doctrineDeclaration() is { } doctrineDeclaration)
        {
            return new TopLevelDeclarationModel(
                doctrineDeclaration.Identifier().GetText(),
                DeclarationKind.Doctrine,
                visibility,
                null);
        }

        if (declaration.globalConstantDeclaration() is { } constantDeclaration)
        {
            return new TopLevelDeclarationModel(
                constantDeclaration.constantDeclarators().constantDeclarator(0).Identifier().GetText(),
                DeclarationKind.GlobalConstant,
                visibility,
                null);
        }

        var variableDeclaration = declaration.globalVariableDeclaration()
            ?? throw new InvalidOperationException("Unsupported top-level declaration shape.");

        return new TopLevelDeclarationModel(
            variableDeclaration.variableDeclarators().variableDeclarator(0).Identifier().GetText(),
            DeclarationKind.GlobalVariable,
            visibility,
            null);
    }

    private static FunctionDeclarationModel CreateFunctionModel(StarkParser.FunctionDeclarationContext function)
    {
        var modifiers = function.functionModifier().Select(static modifier => modifier.GetText()).ToHashSet(StringComparer.Ordinal);
        var inlinePreference = modifiers.Contains("inline")
            ? InlinePreference.Inline
            : modifiers.Contains("noinline")
                ? InlinePreference.NoInline
                : InlinePreference.InlineHint;

        return new FunctionDeclarationModel(
            Name: function.Identifier().GetText(),
            Kind: ParseFunctionKind(function.functionKind()),
            ReturnType: function.returnType().GetText(),
            Parameters: function.parameterList().parameter()
                .Select(static parameter => new ParameterModel(
                    parameter.Identifier().GetText(),
                    parameter.type_().GetText()))
                .ToArray(),
            Modifiers: new FunctionModifierSet(
                inlinePreference,
                modifiers.Contains("hot"),
                modifiers.Contains("cold"),
                modifiers.Contains("ffi")),
            HasBody: function.functionBody().block() is not null);
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
}
