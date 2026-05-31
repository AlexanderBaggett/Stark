using System.Collections.Generic;
using Stark.Parsing;

namespace Stark.Compiler;

internal static class FunctionGenericParameterFacts
{
    public static IReadOnlyList<string> GetEffectiveGenericParameterNames(
        LoadedModuleDocument module,
        DeclaredFunctionSyntax functionSyntax)
    {
        var orderedNames = new List<string>();
        var seenNames = new HashSet<string>(StringComparer.Ordinal);

        if (functionSyntax.ContainingTypeName is { } containingTypeName)
        {
            AddContainingTypeGenericParameterNames(module, containingTypeName, orderedNames, seenNames);
        }

        AddTypeParameterNames(functionSyntax.TypeParameters, orderedNames, seenNames);
        return orderedNames;
    }

    public static IReadOnlyList<string> CombineGenericParameterNames(
        IReadOnlyList<string>? first,
        IReadOnlyList<string>? second)
    {
        var orderedNames = new List<string>((first?.Count ?? 0) + (second?.Count ?? 0));
        var seenNames = new HashSet<string>(StringComparer.Ordinal);

        AddGenericParameterNames(first, orderedNames, seenNames);
        AddGenericParameterNames(second, orderedNames, seenNames);
        return orderedNames;
    }

    public static HashSet<string>? ToGenericParameterSet(IReadOnlyList<string> genericParameterNames)
    {
        return genericParameterNames.Count == 0
            ? null
            : genericParameterNames.ToHashSet(StringComparer.Ordinal);
    }

    private static void AddContainingTypeGenericParameterNames(
        LoadedModuleDocument module,
        string containingTypeName,
        List<string> orderedNames,
        HashSet<string> seenNames)
    {
        foreach (var declaration in module.ParseResult.Root.topLevelDeclaration())
        {
            if (declaration.structDeclaration() is { } structDeclaration
                && string.Equals(structDeclaration.Identifier().GetText(), containingTypeName, StringComparison.Ordinal))
            {
                AddTypeParameterNames(structDeclaration.typeParameterList(), orderedNames, seenNames);
                return;
            }

            if (declaration.recordDeclaration() is { } recordDeclaration
                && string.Equals(recordDeclaration.Identifier().GetText(), containingTypeName, StringComparison.Ordinal))
            {
                AddTypeParameterNames(recordDeclaration.typeParameterList(), orderedNames, seenNames);
                return;
            }

            if (declaration.traitDeclaration() is { } traitDeclaration
                && string.Equals(traitDeclaration.Identifier().GetText(), containingTypeName, StringComparison.Ordinal))
            {
                AddTypeParameterNames(traitDeclaration.typeParameterList(), orderedNames, seenNames);
                // `Self` is an implicit type parameter of every trait method, bound to the
                // implementing type during conformance checking and dynamic dispatch. It
                // resolves like an ordinary generic parameter inside the trait declaration,
                // so receivers stay consistent with concrete methods: `borrow Self self`
                // mirrors `borrow Counter self`.
                AddGenericParameterNames(new[] { "Self" }, orderedNames, seenNames);
                return;
            }

            if (declaration.doctrineDeclaration() is { } doctrineDeclaration
                && string.Equals(doctrineDeclaration.Identifier().GetText(), containingTypeName, StringComparison.Ordinal))
            {
                AddTypeParameterNames(doctrineDeclaration.typeParameterList(), orderedNames, seenNames);
                return;
            }
        }
    }

    private static void AddTypeParameterNames(
        StarkParser.TypeParameterListContext? typeParameterList,
        List<string> orderedNames,
        HashSet<string> seenNames)
    {
        if (typeParameterList is null)
        {
            return;
        }

        AddGenericParameterNames(
            typeParameterList.typeParameter()
                .Select(static parameter => parameter.GetText())
                .ToArray(),
            orderedNames,
            seenNames);
    }

    private static void AddGenericParameterNames(
        IReadOnlyList<string>? names,
        List<string> orderedNames,
        HashSet<string> seenNames)
    {
        if (names is null)
        {
            return;
        }

        foreach (var name in names)
        {
            if (seenNames.Add(name))
            {
                orderedNames.Add(name);
            }
        }
    }
}
