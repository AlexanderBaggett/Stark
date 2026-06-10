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
        else if (StarkFfiAbiFacts.LlvmCallingConventionName(abiFunction.FfiAbi) is { } callingConvention)
        {
            segments.Add(callingConvention);
        }

        segments.Add(_attributeBuilder.RenderAbiReturnType(abiFunction));
        segments.Add($"@{EscapeIdentifier(abiFunction.SymbolName)}({RenderAbiParameterList(abiFunction, includeNames: false, parameterEffects)})");

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
        MonomorphizationLinkageKind? specializationLinkage = null,
        SsaIntegerRangeFact? returnRange = null)
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
        else if (StarkFfiAbiFacts.LlvmCallingConventionName(abiFunction.FfiAbi) is { } callingConvention)
        {
            segments.Add(callingConvention);
        }

        segments.Add(_attributeBuilder.RenderAbiReturnType(abiFunction, returnRange));
        segments.Add($"@{EscapeIdentifier(abiFunction.SymbolName)}({RenderAbiParameterList(abiFunction, includeNames: true, parameterEffects)})");

        if (ResolveDefinitionAddressAttribute(internalize, specializationLinkage) is { } addressAttribute)
        {
            segments.Add(addressAttribute);
        }

        var attributes = _attributeBuilder.BuildFunctionAttributes(abiFunction, effects, memoryEffects);
        if (!string.IsNullOrWhiteSpace(attributes))
        {
            segments.Add(attributes);
        }

        if (specializationLinkage == MonomorphizationLinkageKind.LinkOnceOdrComdat
            && _context.TargetSupportsComdat)
        {
            segments.Add("comdat");
        }

        return string.Join(" ", segments);
    }

    private string RenderAbiParameterList(
        AbiFunctionSignature abiFunction,
        bool includeNames,
        IReadOnlyDictionary<string, ParameterMemoryEffectSummary>? parameterEffects)
    {
        var parameters = abiFunction.Parameters
            .Select(parameter => _attributeBuilder.RenderAbiParameter(abiFunction, parameter, includeNames, parameterEffects))
            .ToList();

        if (abiFunction.IsVarargs)
        {
            parameters.Add("...");
        }

        return string.Join(", ", parameters);
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
