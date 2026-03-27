using System.Text;

namespace Stark.Compiler;

internal static class ArtifactTextRenderer
{
    public static string Render(MidLevelIrModule mir)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"mir module {mir.ModuleName}");
        builder.AppendLine();

        foreach (var function in mir.Functions)
        {
            builder.AppendLine($"fn {function.Signature}");
            builder.AppendLine($"  has-body: {function.HasBody.ToString().ToLowerInvariant()}");
            builder.AppendLine($"  direct-codegen: {function.SupportsDirectCodeGeneration.ToString().ToLowerInvariant()}");

            if (function.Locals.Count != 0)
            {
                builder.AppendLine("  locals:");
                foreach (var local in function.Locals)
                {
                    var flags = new List<string> { local.StorageClass, local.Type.DisplayName };
                    if (local.IsMutable)
                    {
                        flags.Add("mut");
                    }

                    if (local.IsConstant)
                    {
                        flags.Add("const");
                    }

                    if (local.IsAddressable)
                    {
                        flags.Add("addr");
                    }

                    builder.AppendLine($"    {local.Name}: {string.Join(" ", flags)}");
                }
            }

            if (function.Blocks.Count != 0)
            {
                builder.AppendLine("  blocks:");
                foreach (var block in function.Blocks)
                {
                    builder.AppendLine($"    {block.Label}:");
                    foreach (var statement in block.Statements)
                    {
                        builder.AppendLine($"      {Render(statement)}");
                    }

                    foreach (var line in Render(block.Terminator))
                    {
                        builder.AppendLine($"      {line}");
                    }
                }
            }

            builder.AppendLine();
        }

        return builder.ToString().TrimEnd();
    }

    public static string Render(SsaIrModule ssa)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"ssa module {ssa.ModuleName}");
        builder.AppendLine();

        foreach (var function in ssa.Functions)
        {
            builder.AppendLine($"fn {function.Name}({string.Join(", ", function.Parameters.Select(static parameter => $"{parameter.Type.DisplayName} {parameter.Name}"))}) -> {function.ReturnType.DisplayName}");
            builder.AppendLine($"  has-body: {function.HasBody.ToString().ToLowerInvariant()}");
            builder.AppendLine($"  direct-codegen: {function.SupportsDirectCodeGeneration.ToString().ToLowerInvariant()}");

            if (function.Blocks.Count != 0)
            {
                builder.AppendLine("  blocks:");
                foreach (var block in function.Blocks)
                {
                    builder.AppendLine($"    {block.Label}:");

                    foreach (var phi in block.Phis)
                    {
                        var incoming = string.Join(", ", phi.Incomings.Select(entry => $"[{FormatSsaValue(entry.Value)} from bb{entry.PredecessorBlockId}]"));
                        builder.AppendLine($"      {phi.ResultName} = phi {phi.Type.DisplayName} {incoming}");
                    }

                    foreach (var instruction in block.Instructions)
                    {
                        builder.AppendLine($"      {Render(instruction)}");
                    }

                    foreach (var line in Render(block.Terminator))
                    {
                        builder.AppendLine($"      {line}");
                    }
                }
            }

            builder.AppendLine();
        }

        return builder.ToString().TrimEnd();
    }

    private static string Render(MidLevelIrStatement statement)
    {
        return statement.Kind switch
        {
            MidLevelIrStatementKind.StorageLive => $"storage-live {statement.TargetName}",
            MidLevelIrStatementKind.StorageDead => $"storage-dead {statement.TargetName}",
            MidLevelIrStatementKind.Assign => statement.Text,
            MidLevelIrStatementKind.StoreIndirect => $"store-indirect {statement.Value?.Text ?? "<value>"} -> {statement.Address?.Text ?? "<addr>"}",
            MidLevelIrStatementKind.Evaluate => $"eval {statement.Text}",
            _ => statement.Text
        };
    }

    private static IReadOnlyList<string> Render(MidLevelIrTerminator terminator)
    {
        return terminator.Kind switch
        {
            MidLevelIrTerminatorKind.Goto => [$"goto bb{terminator.Targets[0]}"],
            MidLevelIrTerminatorKind.Branch => [$"branch {terminator.ConditionText ?? terminator.Condition?.Text ?? "<cond>"} -> bb{terminator.Targets[0]}, bb{terminator.Targets[1]}"],
            MidLevelIrTerminatorKind.Switch => RenderMirSwitch(terminator),
            MidLevelIrTerminatorKind.Return => [$"return {terminator.ValueText ?? string.Empty}".TrimEnd()],
            MidLevelIrTerminatorKind.Unreachable => ["unreachable"],
            _ => [$"terminator {terminator.Kind}"]
        };
    }

    private static IReadOnlyList<string> RenderMirSwitch(MidLevelIrTerminator terminator)
    {
        var lines = new List<string>
        {
            $"switch {terminator.ConditionText ?? terminator.Condition?.Text ?? "<cond>"} default -> bb{terminator.DefaultTarget ?? terminator.Targets.LastOrDefault()}"
        };

        if (terminator.SwitchCases is not null)
        {
            foreach (var switchCase in terminator.SwitchCases)
            {
                lines.Add($"case {switchCase.Label} -> bb{switchCase.TargetBlockId}");
            }
        }

        return lines;
    }

    private static string Render(SsaInstruction instruction)
    {
        return instruction switch
        {
            SsaValueInstruction valueInstruction => $"{valueInstruction.ResultName} = {valueInstruction.Value.Text}",
            SsaAllocateLocalInstruction allocateLocal => $"alloca {allocateLocal.LocalName}: {allocateLocal.LocalType.DisplayName}",
            SsaLifetimeStartInstruction lifetimeStart => $"lifetime.start {lifetimeStart.LocalName}",
            SsaLifetimeEndInstruction lifetimeEnd => $"lifetime.end {lifetimeEnd.LocalName}",
            SsaStoreLocalInstruction storeLocal => $"store {FormatSsaValue(storeLocal.Value)} -> {storeLocal.LocalName}",
            SsaCopyMemoryInstruction copyMemory => $"{(copyMemory.TransferKind == SsaMemoryTransferKind.Move ? "move" : "copy")} {FormatSsaValue(copyMemory.SourceAddress)} -> {FormatSsaValue(copyMemory.DestinationAddress)} : {copyMemory.CopyType.DisplayName}",
            SsaStoreIndirectInstruction storeIndirect => $"store {FormatSsaValue(storeIndirect.Value)} -> {FormatSsaValue(storeIndirect.Address)}",
            SsaStoreGlobalInstruction storeGlobal => $"store {FormatSsaValue(storeGlobal.Value)} -> @{storeGlobal.GlobalName}",
            _ => instruction.ToString() ?? instruction.GetType().Name
        };
    }

    private static IReadOnlyList<string> Render(SsaTerminator terminator)
    {
        return terminator.Kind switch
        {
            SsaTerminatorKind.Goto => [$"goto bb{terminator.Targets[0]}"],
            SsaTerminatorKind.Branch => [$"branch {FormatSsaValue(terminator.Condition)} -> bb{terminator.Targets[0]}, bb{terminator.Targets[1]}"],
            SsaTerminatorKind.Switch => RenderSsaSwitch(terminator),
            SsaTerminatorKind.Return => [$"return {FormatSsaValue(terminator.Value)}"],
            SsaTerminatorKind.Unreachable => ["unreachable"],
            _ => [$"terminator {terminator.Kind}"]
        };
    }

    private static IReadOnlyList<string> RenderSsaSwitch(SsaTerminator terminator)
    {
        var lines = new List<string>
        {
            $"switch {FormatSsaValue(terminator.Condition)} default -> bb{terminator.DefaultTarget ?? terminator.Targets.LastOrDefault()}"
        };

        if (terminator.SwitchCases is not null)
        {
            foreach (var switchCase in terminator.SwitchCases)
            {
                lines.Add($"case {FormatSsaValue(switchCase.MatchValue)} -> bb{switchCase.TargetBlockId}");
            }
        }

        return lines;
    }

    private static string FormatSsaValue(SsaValue? value)
    {
        return value switch
        {
            null => "<none>",
            SsaValueReference reference => reference.Name,
            SsaIntegerConstant integer => integer.Value.ToString(),
            SsaFloatConstant floating => floating.LiteralText,
            SsaStringConstant text => text.LiteralText,
            SsaBoolConstant boolean => boolean.Value ? "true" : "false",
            SsaNullConstant => "null",
            SsaGlobalAddressValue globalAddress => $"@{globalAddress.GlobalName}",
            SsaUndefValue => "undef",
            _ => value.Text
        };
    }
}
