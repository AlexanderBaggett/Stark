namespace Stark.Compiler;

internal static class GlobalSymbolNaming
{
    public static string ComputeSymbolName(
        string moduleName,
        string sourceName,
        StarkVisibility visibility,
        bool qualifyModuleSymbols,
        bool isImported)
    {
        if (visibility == StarkVisibility.Export)
        {
            return sourceName;
        }

        return isImported || qualifyModuleSymbols
            ? $"{moduleName}.{sourceName}"
            : sourceName;
    }

    public static string ComputeMonomorphizedFunctionSymbolName(
        string ownerModuleName,
        string templateName,
        IReadOnlyList<StarkTypeSymbol> typeArguments)
    {
        return ComputeMonomorphizedSymbolName("fn", ownerModuleName, templateName, typeArguments);
    }

    public static string ComputeMonomorphizedTypeSymbolName(
        string ownerModuleName,
        string templateName,
        IReadOnlyList<StarkTypeSymbol> typeArguments)
    {
        return ComputeMonomorphizedSymbolName("ty", ownerModuleName, templateName, typeArguments);
    }

    private static string ComputeMonomorphizedSymbolName(
        string kind,
        string ownerModuleName,
        string templateName,
        IReadOnlyList<StarkTypeSymbol> typeArguments)
    {
        var builder = $"__stark_mono_{kind}_{SanitizeSymbolComponent(ownerModuleName)}__{SanitizeSymbolComponent(templateName)}";
        foreach (var typeArgument in typeArguments)
        {
            builder += $"__{SanitizeSymbolComponent(FunctionOverloadFacts.BuildCanonicalTypeKey(typeArgument))}";
        }

        return builder;
    }

    private static string SanitizeSymbolComponent(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "_";
        }

        var builder = new System.Text.StringBuilder(text.Length);
        var previousWasUnderscore = false;

        foreach (var ch in text)
        {
            var normalized = char.IsLetterOrDigit(ch) ? ch : '_';
            if (normalized == '_')
            {
                if (previousWasUnderscore)
                {
                    continue;
                }

                previousWasUnderscore = true;
                builder.Append('_');
                continue;
            }

            previousWasUnderscore = false;
            builder.Append(normalized);
        }

        return builder.ToString().Trim('_');
    }
}
