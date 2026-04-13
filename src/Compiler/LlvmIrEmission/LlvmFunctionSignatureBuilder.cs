namespace Stark.Compiler.LlvmIrEmission;

internal sealed class LlvmFunctionSignatureBuilder
{
    private readonly LlvmFunctionAttributeBuilder _attributeBuilder;

    public LlvmFunctionSignatureBuilder(
        LlvmEmissionContext context,
        LlvmFunctionAttributeBuilder attributeBuilder)
    {
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

        segments.Add(_attributeBuilder.RenderAbiReturnType(abiFunction));
        segments.Add($"@{EscapeIdentifier(abiFunction.SymbolName)}({string.Join(", ", abiFunction.Parameters.Select(parameter => _attributeBuilder.RenderAbiParameter(abiFunction, parameter, includeName: false, parameterEffects)))})");

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

        if (ResolveDefinitionPreemptionKeyword(internalize, specializationLinkage) is { } preemptionKeyword)
        {
            segments.Add(preemptionKeyword);
        }

        if (effects.UseFastCallingConvention)
        {
            segments.Add("fastcc");
        }

        segments.Add(_attributeBuilder.RenderAbiReturnType(abiFunction));
        segments.Add($"@{EscapeIdentifier(abiFunction.SymbolName)}({string.Join(", ", abiFunction.Parameters.Select(parameter => _attributeBuilder.RenderAbiParameter(abiFunction, parameter, includeName: true, parameterEffects)))})");

        var attributes = _attributeBuilder.BuildFunctionAttributes(abiFunction, effects, memoryEffects);
        if (!string.IsNullOrWhiteSpace(attributes))
        {
            segments.Add(attributes);
        }

        if (ResolveDefinitionAddressAttribute(internalize, specializationLinkage) is { } addressAttribute)
        {
            segments.Add(addressAttribute);
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

    private static string? ResolveDefinitionPreemptionKeyword(
        bool internalize,
        MonomorphizationLinkageKind? specializationLinkage)
    {
        return internalize || specializationLinkage == MonomorphizationLinkageKind.LinkOnceOdrComdat
            ? "dso_local"
            : null;
    }

    private static string? ResolveDefinitionAddressAttribute(
        bool internalize,
        MonomorphizationLinkageKind? specializationLinkage)
    {
        if (internalize)
        {
            return "unnamed_addr";
        }

        return specializationLinkage == MonomorphizationLinkageKind.LinkOnceOdrComdat
            ? "local_unnamed_addr"
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
