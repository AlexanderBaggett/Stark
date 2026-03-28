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
}
