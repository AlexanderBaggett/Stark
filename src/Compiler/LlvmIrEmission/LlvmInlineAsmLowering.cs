namespace Stark.Compiler.LlvmIrEmission;

internal sealed record LlvmInlineAsmPlan(
    string EscapedTemplate,
    string EscapedConstraints,
    IReadOnlyList<int> InputParameterIndices,
    string MemoryAttributeSuffix);

internal static class LlvmInlineAsmLowering
{
    public static bool TryCreatePlan(
        AbiFunctionSignature abiFunction,
        AsmFunctionModel asmFunction,
        out LlvmInlineAsmPlan plan,
        out string failureReason)
    {
        plan = default!;

        if (abiFunction.ReturnsIndirect)
        {
            failureReason = "v1 asm lowering does not support indirect return ABIs.";
            return false;
        }

        if (asmFunction.Outputs.Any(static output => !output.BindsReturnValue))
        {
            failureReason = "v1 asm lowering currently supports only direct return bindings and no out/init parameter outputs.";
            return false;
        }

        if (abiFunction.SourceReturnType.Kind == StarkTypeKind.Void)
        {
            if (asmFunction.Outputs.Count != 0)
            {
                failureReason = "void asm functions cannot bind a return register.";
                return false;
            }
        }
        else if (asmFunction.Outputs.Count != 1)
        {
            failureReason = "non-void asm functions must bind exactly one return register.";
            return false;
        }

        var userParameters = abiFunction.UserParameters;
        foreach (var parameter in userParameters)
        {
            if (parameter.Kind != AbiParameterKind.Direct || parameter.IsExpandedDirectParameter)
            {
                failureReason = $"v1 asm lowering requires scalar direct ABI parameters, but '{parameter.SourceName}' has a non-scalar or indirect ABI shape.";
                return false;
            }
        }

        var parameterIndicesByName = userParameters
            .Select(static (parameter, index) => (parameter.SourceName, Index: index))
            .ToDictionary(static item => item.SourceName, static item => item.Index, StringComparer.Ordinal);
        var constraintFragments = new List<string>();
        var inputParameterIndices = new List<int>(asmFunction.Inputs.Count);
        string? returnRegister = null;

        if (asmFunction.Outputs.SingleOrDefault(static output => output.BindsReturnValue) is { } outputOperand)
        {
            returnRegister = StarkAsmRegisterFacts.Normalize(outputOperand.RegisterName);
            constraintFragments.Add($"={{{returnRegister}}}");
        }

        foreach (var input in asmFunction.Inputs)
        {
            if (!parameterIndicesByName.TryGetValue(input.ValueName, out var parameterIndex))
            {
                failureReason = $"Missing ABI parameter '{input.ValueName}' for asm input binding.";
                return false;
            }

            var inputRegister = StarkAsmRegisterFacts.Normalize(input.RegisterName);
            constraintFragments.Add(string.Equals(returnRegister, inputRegister, StringComparison.Ordinal)
                ? "0"
                : $"{{{inputRegister}}}");
            inputParameterIndices.Add(parameterIndex);
        }

        foreach (var clobber in BuildConstraintClobbers(asmFunction))
        {
            constraintFragments.Add($"~{{{clobber}}}");
        }

        plan = new LlvmInlineAsmPlan(
            EscapeString(asmFunction.TemplateText),
            EscapeString(string.Join(",", constraintFragments)),
            inputParameterIndices,
            BuildMemoryAttributeSuffix(asmFunction.MemoryEffects));
        failureReason = string.Empty;
        return true;
    }

    public static string EscapeString(string text)
    {
        var builder = new System.Text.StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (ch >= 0x20 && ch <= 0x7E && ch is not '\\' and not '"')
            {
                builder.Append(ch);
                continue;
            }

            builder.Append('\\');
            builder.Append(((int)ch).ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }

    private static IReadOnlyList<string> BuildConstraintClobbers(AsmFunctionModel asmFunction)
    {
        var clobbers = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Add(string name)
        {
            var normalized = StarkAsmRegisterFacts.Normalize(name);
            if (seen.Add(normalized))
            {
                clobbers.Add(normalized);
            }
        }

        foreach (var clobber in asmFunction.Clobbers)
        {
            Add(clobber);
        }

        // An omitted memory clause deliberately remains the conservative case.
        // Explicit memory operands are real LLVM call operands and therefore can
        // use argmem effects without a target-independent ~{memory} barrier.
        if (asmFunction.MemoryEffects is null)
        {
            Add("memory");
        }

        if (asmFunction.Architecture is StarkAsmArchitecture.X86_64 or StarkAsmArchitecture.X86)
        {
            Add("dirflag");
            Add("fpsr");
            Add("flags");
        }

        return clobbers;
    }

    private static string BuildMemoryAttributeSuffix(AsmMemoryEffectModel? memoryEffects)
    {
        if (memoryEffects is null)
        {
            return string.Empty;
        }

        var reads = memoryEffects.Operands.Any(static operand =>
            operand.AccessKind is StarkAsmMemoryAccessKind.Read or StarkAsmMemoryAccessKind.ReadWrite);
        var writes = memoryEffects.Operands.Any(static operand =>
            operand.AccessKind is StarkAsmMemoryAccessKind.Write or StarkAsmMemoryAccessKind.ReadWrite);
        var access = (reads, writes) switch
        {
            (false, false) => "none",
            (true, false) => "read",
            (false, true) => "write",
            _ => "readwrite"
        };

        return access == "none"
            ? " memory(none)"
            : $" memory(argmem: {access})";
    }
}
