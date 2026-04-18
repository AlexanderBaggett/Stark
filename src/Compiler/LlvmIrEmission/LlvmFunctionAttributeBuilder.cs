namespace Stark.Compiler.LlvmIrEmission;

internal sealed class LlvmFunctionAttributeBuilder
{
    private readonly LlvmEmissionContext _context;

    public LlvmFunctionAttributeBuilder(LlvmEmissionContext context)
    {
        _context = context;
    }

    private string MapType(StarkTypeSymbol type) => _context.MapType(type);

    private ConcreteTypeLayout? TryGetConcreteTypeLayout(StarkTypeSymbol type) => _context.TryGetConcreteTypeLayout(type);

    public string RenderAbiParameter(
        AbiFunctionSignature abiFunction,
        AbiParameterSymbol parameter,
        bool includeName,
        IReadOnlyDictionary<string, ParameterMemoryEffectSummary>? parameterEffects)
    {
        var segments = new List<string> { MapType(parameter.LlvmType) };

        if (ShouldMarkNoundef(abiFunction, parameter))
        {
            segments.Add("noundef");
        }

        segments.AddRange(DeriveAbiParameterAttributes(parameter, ResolveParameterEffects(parameter, parameterEffects)));

        if (includeName)
        {
            segments.Add($"%{EscapeIdentifier(parameter.LlvmName)}");
        }

        return string.Join(" ", segments);
    }

    public string RenderAbiReturnType(AbiFunctionSignature abiFunction)
    {
        var segments = new List<string>();

        if (ShouldMarkNoundef(abiFunction))
        {
            segments.Add("noundef");
        }

        segments.Add(MapType(abiFunction.LlvmReturnType));

        return string.Join(" ", segments);
    }

    public string BuildFunctionAttributes(
        AbiFunctionSignature abiFunction,
        FunctionEffectProfile effects,
        FunctionMemoryEffectSummary? memoryEffects)
    {
        var attributes = new List<string>();

        if (effects.NoUnwind)
        {
            attributes.Add("nounwind");
        }

        if (effects.WillReturn)
        {
            attributes.Add("willreturn");
        }

        if (effects.MustProgress)
        {
            attributes.Add("mustprogress");
        }

        if (effects.NoSync)
        {
            attributes.Add("nosync");
        }

        if (effects.NoFree)
        {
            attributes.Add("nofree");
        }

        var memoryAttribute = BuildMemoryAttribute(abiFunction, effects, memoryEffects);
        if (!string.IsNullOrWhiteSpace(memoryAttribute))
        {
            attributes.Add(memoryAttribute);
        }

        if (effects.IsHot)
        {
            attributes.Add("hot");
        }

        if (effects.IsCold)
        {
            attributes.Add("cold");
        }

        if (effects.IsStrictFp)
        {
            attributes.Add("strictfp");
        }

        attributes.Add(effects.InlinePreference switch
        {
            InlinePreference.Inline => "alwaysinline",
            InlinePreference.NoInline => "noinline",
            _ => "inlinehint"
        });

        return string.Join(" ", attributes);
    }

    private IReadOnlyList<string> DeriveAbiParameterAttributes(
        AbiParameterSymbol parameter,
        ParameterMemoryEffectSummary? parameterEffects)
    {
        var attributes = new List<string>();

        if (parameter.Kind == AbiParameterKind.SRet)
        {
            attributes.Add("noalias");
            attributes.Add($"sret({MapType(parameter.SourceType)})");
            attributes.Add("nonnull");
            AppendDereferenceableAttributes(attributes, parameter.SourceType);

            return attributes;
        }

        if (parameter.Kind == AbiParameterKind.IndirectIn)
        {
            attributes.Add("nonnull");
            if (AbiLoweringHeuristics.IsByValueIndirectParameter(parameter))
            {
                attributes.Add($"byval({MapType(parameter.SourceType)})");
            }

            attributes.Add("noalias");
            AppendPointerMemoryAccessAttributes(attributes, parameter, parameterEffects);
            AppendCaptureAttribute(attributes, parameterEffects);
            AppendDereferenceableAttributes(attributes, parameter.SourceType);

            return attributes;
        }

        if (parameter.LlvmType.Kind != StarkTypeKind.RawPointer)
        {
            return attributes;
        }

        if (parameter.SourceType.BorrowKind != StarkBorrowKind.None
            || parameter.SourceType.InitializationKind != StarkInitializationKind.None)
        {
            attributes.Add("nonnull");
            AppendDereferenceableAttributes(attributes, parameter.SourceType);
        }

        if (parameter.SourceType.InitializationKind != StarkInitializationKind.None)
        {
            attributes.Add("noalias");
        }

        AppendPointerMemoryAccessAttributes(attributes, parameter, parameterEffects);
        AppendCaptureAttribute(attributes, parameterEffects);

        // Plain raw pointers remain nullable and may carry arbitrary raw/FFI
        // provenance, so do not infer nonnull or dereferenceable facts here.
        return attributes;
    }

    private void AppendDereferenceableAttributes(List<string> attributes, StarkTypeSymbol type)
    {
        if (TryGetConcreteTypeLayout(type) is not { } layout)
        {
            return;
        }

        attributes.Add($"dereferenceable({layout.SizeBytes})");
        if (layout.AlignmentBytes > 1)
        {
            attributes.Add($"align {layout.AlignmentBytes}");
        }
    }

    private static ParameterMemoryEffectSummary? ResolveParameterEffects(
        AbiParameterSymbol parameter,
        IReadOnlyDictionary<string, ParameterMemoryEffectSummary>? parameterEffects)
    {
        if (parameterEffects is null
            || parameter.Kind == AbiParameterKind.SRet
            || !parameterEffects.TryGetValue(parameter.SourceName, out var effects))
        {
            return null;
        }

        return effects;
    }

    private static bool ShouldMarkNoundef(
        AbiFunctionSignature abiFunction,
        AbiParameterSymbol parameter)
    {
        if (abiFunction.IsFfi || parameter.LlvmType.Kind == StarkTypeKind.Void)
        {
            return false;
        }

        return true;
    }

    private static bool ShouldMarkNoundef(AbiFunctionSignature abiFunction)
    {
        return !abiFunction.IsFfi
            && !abiFunction.ReturnsIndirect
            && abiFunction.LlvmReturnType.Kind != StarkTypeKind.Void;
    }

    private static void AppendPointerMemoryAccessAttributes(
        List<string> attributes,
        AbiParameterSymbol parameter,
        ParameterMemoryEffectSummary? parameterEffects)
    {
        if (parameterEffects is not null)
        {
            if (parameterEffects.Writes)
            {
                if (!parameterEffects.Reads)
                {
                    attributes.Add("writeonly");
                }
            }
            else
            {
                attributes.Add("readonly");
            }

            return;
        }

        if (parameter.SourceType.InitializationKind != StarkInitializationKind.None)
        {
            attributes.Add("writeonly");
            return;
        }

        if (parameter.SourceType.Kind is StarkTypeKind.Ascii or StarkTypeKind.Unicode
            || (parameter.SourceType.Kind == StarkTypeKind.RawPointer && !parameter.SourceType.IsMutablePointer)
            || (parameter.SourceType.BorrowKind != StarkBorrowKind.None && !parameter.SourceType.IsMutableView))
        {
            attributes.Add("readonly");
        }
    }

    private static void AppendCaptureAttribute(List<string> attributes, ParameterMemoryEffectSummary? parameterEffects)
    {
        if (parameterEffects is null)
        {
            return;
        }

        if (parameterEffects.CaptureKind == ParameterCaptureKind.None)
        {
            attributes.Add("nocapture");
            return;
        }

        attributes.Add(parameterEffects.CaptureKind switch
        {
            ParameterCaptureKind.Return => parameterEffects.GuaranteedReadOnly
                ? "captures(ret: address, read_provenance)"
                : "captures(ret: address, provenance)",
            ParameterCaptureKind.Escape => parameterEffects.GuaranteedReadOnly
                ? "captures(address, read_provenance)"
                : "captures(address, provenance)",
            _ => "captures(address, provenance)"
        });
    }

    private static string? BuildMemoryAttribute(
        AbiFunctionSignature abiFunction,
        FunctionEffectProfile effects,
        FunctionMemoryEffectSummary? memoryEffects)
    {
        var readsArgumentMemory = memoryEffects?.ReadsArgumentMemory ?? effects.ReadsArgumentMemory;
        var writesArgumentMemory = memoryEffects?.WritesArgumentMemory ?? false;
        if (abiFunction.ReturnsIndirect)
        {
            writesArgumentMemory = true;
        }

        var readsOtherMemory = memoryEffects?.ReadsOtherMemory ?? false;
        var writesOtherMemory = memoryEffects?.WritesOtherMemory ?? false;

        if (memoryEffects is null)
        {
            return effects.IsPure
                ? GetMemoryAttribute(readsArgumentMemory, writesArgumentMemory, readsOtherMemory, writesOtherMemory)
                : readsArgumentMemory || writesArgumentMemory || readsOtherMemory || writesOtherMemory
                    ? GetMemoryAttribute(readsArgumentMemory, writesArgumentMemory, readsOtherMemory, writesOtherMemory)
                    : null;
        }

        return GetMemoryAttribute(readsArgumentMemory, writesArgumentMemory, readsOtherMemory, writesOtherMemory);
    }

    private static string? GetMemoryAttribute(
        bool readsArgumentMemory,
        bool writesArgumentMemory,
        bool readsOtherMemory,
        bool writesOtherMemory)
    {
        var defaultAccess = GetMemoryAccessName(readsOtherMemory, writesOtherMemory);
        var argumentAccess = GetMemoryAccessName(readsArgumentMemory, writesArgumentMemory);

        if (defaultAccess == "readwrite" && argumentAccess == "readwrite")
        {
            return null;
        }

        if (defaultAccess == argumentAccess)
        {
            return $"memory({defaultAccess})";
        }

        if (defaultAccess == "none")
        {
            return $"memory(argmem: {argumentAccess})";
        }

        if (argumentAccess == "none")
        {
            return $"memory({defaultAccess}, argmem: none)";
        }

        return $"memory({defaultAccess}, argmem: {argumentAccess})";
    }

    private static string GetMemoryAccessName(bool reads, bool writes)
    {
        return (reads, writes) switch
        {
            (false, false) => "none",
            (true, false) => "read",
            (false, true) => "write",
            _ => "readwrite"
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
