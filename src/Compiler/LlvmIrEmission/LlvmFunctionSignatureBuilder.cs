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

        if (abiFunction.UsesTailCallingConvention)
        {
            segments.Add("tailcc");
        }
        else if (effects.UseFastCallingConvention)
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
        SsaIntegerRangeFact? returnRange = null,
        bool forceDsoLocal = false,
        bool forceHiddenVisibility = false,
        string? explicitMemoryAttribute = null)
    {
        var segments = new List<string> { "define" };

        if (ResolveDefinitionLinkageKeyword(internalize, specializationLinkage) is { } linkageKeyword)
        {
            segments.Add(linkageKeyword);
        }

        if (ResolveDefinitionPreemptionKeyword(internalize, specializationLinkage, forceDsoLocal) is { } preemptionKeyword)
        {
            segments.Add(preemptionKeyword);
        }

        if (forceHiddenVisibility && !internalize)
        {
            segments.Add("hidden");
        }

        if (abiFunction.UsesTailCallingConvention)
        {
            segments.Add("tailcc");
        }
        else if (effects.UseFastCallingConvention)
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

        // Function attributes precede section/comdat/alignment clauses in the
        // LLVM function-definition grammar. Assembly memory facts are already
        // validated and rendered by the inline-asm lowering, so preserve that
        // authoritative attribute here instead of appending it to the finished
        // signature after a possible COMDAT clause.
        if (!string.IsNullOrWhiteSpace(explicitMemoryAttribute))
        {
            segments.Add(explicitMemoryAttribute);
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
        var parameters = abiFunction.LlvmParameters
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

        return specializationLinkage switch
        {
            MonomorphizationLinkageKind.LinkOnceOdrComdat => "linkonce_odr",
            MonomorphizationLinkageKind.WeakOdrPreserved => "weak_odr",
            _ => null
        };
    }

    private static string? ResolveDefinitionPreemptionKeyword(
        bool internalize,
        MonomorphizationLinkageKind? specializationLinkage,
        bool forceDsoLocal)
    {
        return internalize
               || forceDsoLocal
               || specializationLinkage == MonomorphizationLinkageKind.LinkOnceOdrComdat
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

        return specializationLinkage switch
        {
            MonomorphizationLinkageKind.LinkOnceOdrComdat => "local_unnamed_addr",
            MonomorphizationLinkageKind.WeakOdrPreserved => "unnamed_addr",
            _ => null
        };
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
