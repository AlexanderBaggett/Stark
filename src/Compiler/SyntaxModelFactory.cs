using Stark.Parsing;

namespace Stark.Compiler;

internal static class SyntaxModelFactory
{
    public static SyntaxModel Create(ParseResult parseResult)
    {
        var root = parseResult.Root;
        var declarations = new List<TopLevelDeclarationModel>();

        foreach (var declaration in root.topLevelDeclaration())
        {
            AddDeclarationModels(declarations, declaration);
        }

        return new SyntaxModel(
            ModuleName: root.moduleDeclaration().qualifiedName().GetText(),
            Imports: root.importDeclaration().Select(CreateImportModel).ToArray(),
            Declarations: declarations);
    }

    private static ImportDeclarationModel CreateImportModel(StarkParser.ImportDeclarationContext importDeclaration)
    {
        return new ImportDeclarationModel(
            importDeclaration.qualifiedName().GetText(),
            importDeclaration.EXPORT() is not null);
    }

    private static void AddDeclarationModels(List<TopLevelDeclarationModel> declarations, StarkParser.TopLevelDeclarationContext declaration)
    {
        var visibility = ParseVisibility(declaration.visibilityModifier());

        if (declaration.functionDeclaration() is { } function)
        {
            declarations.Add(new TopLevelDeclarationModel(
                function.Identifier().GetText(),
                DeclarationKind.Function,
                visibility,
                CreateFunctionModel(function.Identifier().GetText(), function.functionKind(), function.returnType(), function.parameterList(), function.functionModifier(), function.functionBody())));
            return;
        }

        if (declaration.structDeclaration() is { } structDeclaration)
        {
            declarations.Add(new TopLevelDeclarationModel(
                structDeclaration.Identifier().GetText(),
                DeclarationKind.Struct,
                visibility,
                null));

            foreach (var method in structDeclaration.structBody().structMember()
                         .Select(static member => member.methodDeclaration())
                         .Where(static method => method is not null)!)
            {
                declarations.Add(new TopLevelDeclarationModel(
                    $"{structDeclaration.Identifier().GetText()}.{method.Identifier().GetText()}",
                    DeclarationKind.Function,
                    visibility,
                    CreateFunctionModel(
                        $"{structDeclaration.Identifier().GetText()}.{method.Identifier().GetText()}",
                        method.functionKind(),
                        method.returnType(),
                        method.parameterList(),
                        method.functionModifier(),
                        method.functionBody())));
            }

            return;
        }

        if (declaration.recordDeclaration() is { } recordDeclaration)
        {
            declarations.Add(new TopLevelDeclarationModel(
                recordDeclaration.Identifier().GetText(),
                DeclarationKind.Record,
                visibility,
                null));

            foreach (var method in recordDeclaration.recordBody().recordMember()
                         .Select(static member => member.methodDeclaration())
                         .Where(static method => method is not null)!)
            {
                declarations.Add(new TopLevelDeclarationModel(
                    $"{recordDeclaration.Identifier().GetText()}.{method.Identifier().GetText()}",
                    DeclarationKind.Function,
                    visibility,
                    CreateFunctionModel(
                        $"{recordDeclaration.Identifier().GetText()}.{method.Identifier().GetText()}",
                        method.functionKind(),
                        method.returnType(),
                        method.parameterList(),
                        method.functionModifier(),
                        method.functionBody())));
            }

            return;
        }

        if (declaration.enumDeclaration() is { } enumDeclaration)
        {
            declarations.Add(new TopLevelDeclarationModel(
                enumDeclaration.Identifier().GetText(),
                DeclarationKind.Enum,
                visibility,
                null));
            return;
        }

        if (declaration.traitDeclaration() is { } traitDeclaration)
        {
            declarations.Add(new TopLevelDeclarationModel(
                traitDeclaration.Identifier().GetText(),
                DeclarationKind.Trait,
                visibility,
                null));
            return;
        }

        if (declaration.doctrineDeclaration() is { } doctrineDeclaration)
        {
            declarations.Add(new TopLevelDeclarationModel(
                doctrineDeclaration.Identifier().GetText(),
                DeclarationKind.Doctrine,
                visibility,
                null));
            return;
        }

        if (declaration.globalConstantDeclaration() is { } constantDeclaration)
        {
            declarations.Add(new TopLevelDeclarationModel(
                constantDeclaration.constantDeclarators().constantDeclarator(0).Identifier().GetText(),
                DeclarationKind.GlobalConstant,
                visibility,
                null));
            return;
        }

        var variableDeclaration = declaration.globalVariableDeclaration()
            ?? throw new InvalidOperationException("Unsupported top-level declaration shape.");

        declarations.Add(new TopLevelDeclarationModel(
            variableDeclaration.variableDeclarators().variableDeclarator(0).Identifier().GetText(),
            DeclarationKind.GlobalVariable,
            visibility,
            null));
    }

    private static FunctionDeclarationModel CreateFunctionModel(
        string name,
        StarkParser.FunctionKindContext functionKind,
        StarkParser.ReturnTypeContext returnType,
        StarkParser.ParameterListContext parameterList,
        IReadOnlyList<StarkParser.FunctionModifierContext> modifiersList,
        StarkParser.FunctionBodyContext functionBody)
    {
        var modifiers = modifiersList.Select(static modifier => modifier.GetText()).ToHashSet(StringComparer.Ordinal);
        var inlinePreference = modifiers.Contains("inline")
            ? InlinePreference.Inline
            : modifiers.Contains("noinline")
                ? InlinePreference.NoInline
                : InlinePreference.InlineHint;

        return new FunctionDeclarationModel(
            Name: name,
            Kind: ParseFunctionKind(functionKind),
            ReturnType: returnType.GetText(),
            Parameters: parameterList.parameter()
                .Select(static parameter => new ParameterModel(
                    parameter.Identifier().GetText(),
                    parameter.type_().GetText()))
                .ToArray(),
            Modifiers: new FunctionModifierSet(
                inlinePreference,
                modifiers.Contains("hot"),
                modifiers.Contains("cold"),
                modifiers.Contains("ffi")),
            HasBody: functionBody.block() is not null);
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
