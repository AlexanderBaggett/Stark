namespace Stark.Compiler.LlvmIrEmission;

internal sealed class LlvmFunctionSignatureBuilder
{
    private readonly LlvmEmissionContext _context;
    private readonly LlvmFunctionAttributeBuilder _attributeBuilder;

    public LlvmFunctionSignatureBuilder(
        LlvmEmissionContext context,
        LlvmFunctionAttributeBuilder attributeBuilder)
    {
        _context = context;
        _attributeBuilder = attributeBuilder;
    }

    public string BuildDeclarationSignature(
        bool internalize,
        TypedFunctionSignature function,
        AbiFunctionSignature abiFunction,
        FunctionEffectProfile effects,
        FunctionMemoryEffectSummary? memoryEffects,
        IReadOnlyDictionary<string, ParameterMemoryEffectSummary>? parameterEffects)
    {
        var segments = new List<string> { "declare" };

        if (internalize)
        {
            segments.Add("internal");
        }

        if (effects.UseFastCallingConvention)
        {
            segments.Add("fastcc");
        }

        segments.Add(_context.MapType(abiFunction.LlvmReturnType));
        segments.Add($"@{EscapeIdentifier(abiFunction.SymbolName)}({string.Join(", ", abiFunction.Parameters.Select(parameter => _attributeBuilder.RenderAbiParameter(parameter, includeName: false, parameterEffects)))})");

        var attributes = _attributeBuilder.BuildFunctionAttributes(abiFunction, effects, memoryEffects);
        if (!string.IsNullOrWhiteSpace(attributes))
        {
            segments.Add(attributes);
        }

        return string.Join(" ", segments);
    }

    public string BuildDefinitionSignature(
        bool internalize,
        TypedFunctionSignature function,
        AbiFunctionSignature abiFunction,
        FunctionEffectProfile effects,
        FunctionMemoryEffectSummary? memoryEffects,
        IReadOnlyDictionary<string, ParameterMemoryEffectSummary>? parameterEffects,
        MonomorphizationLinkageKind? specializationLinkage = null)
    {
        var segments = new List<string> { "define" };

        if (ResolveDefinitionLinkageKeyword(internalize, specializationLinkage) is { } linkageKeyword)
        {
            segments.Add(linkageKeyword);
        }

        if (effects.UseFastCallingConvention)
        {
            segments.Add("fastcc");
        }

        segments.Add(_context.MapType(abiFunction.LlvmReturnType));
        segments.Add($"@{EscapeIdentifier(abiFunction.SymbolName)}({string.Join(", ", abiFunction.Parameters.Select(parameter => _attributeBuilder.RenderAbiParameter(parameter, includeName: true, parameterEffects)))})");

        var attributes = _attributeBuilder.BuildFunctionAttributes(abiFunction, effects, memoryEffects);
        if (!string.IsNullOrWhiteSpace(attributes))
        {
            segments.Add(attributes);
        }

        if (specializationLinkage == MonomorphizationLinkageKind.LinkOnceOdrComdat)
        {
            segments.Add("comdat");
        }

        return string.Join(" ", segments);
    }

    private static string? ResolveDefinitionLinkageKeyword(
        bool internalize,
        MonomorphizationLinkageKind? specializationLinkage)
    {
        if (internalize)
        {
            return "internal";
        }

        return specializationLinkage == MonomorphizationLinkageKind.LinkOnceOdrComdat
            ? "linkonce_odr"
            : null;
    }

    private static string EscapeIdentifier(string identifier)
    {
        var builder = new System.Text.StringBuilder(identifier.Length);
        foreach (var ch in identifier)
        {
            builder.Append(char.IsLetterOrDigit(ch) || ch == '_' ? ch : '_');
        }

        return builder.ToString();
    }
}
