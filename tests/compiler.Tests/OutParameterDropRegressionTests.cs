using Stark.Compiler;

namespace compiler.Tests;

/// <summary>
/// Out-parameter drop regression: parameters that require runtime drops were
/// registered active in the exit drop order, so a function that assigned an
/// owned value (here a struct with dynamic storage) into an 'out' parameter
/// freed the caller's buffers right before returning — a use-after-free that
/// corrupted the first elements of the transferred dynamic. The Stage 1
/// lexer's LexOk(result, out stream) helper hit this first.
/// </summary>
public sealed class OutParameterDropRegressionTests
{
    [Fact]
    public void AssigningIntoAnOutParameterDoesNotFreeTheTransferredValueOnReturn()
    {
        const string source = """
            module Demo

            struct Box
            {
                dynamic i64[min max] Items;
            }

            fn bool Fill(out Box target)
            {
                stack mut Box local = new();
                if (!local.Items.TryReserve(2))
                {
                    target = new();
                    return false;
                }

                init local.Items[local.Items.Length] = 7;
                init local.Items[local.Items.Length] = 8;
                target = local;
                return true;
            }

            export fn i32[min max] main()
            {
                stack mut Box box = new();
                if (!Fill(box))
                {
                    return 1;
                }

                return 0;
            }
            """;

        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(source),
            new CompilerOptions(EmitLlvmIr: true));

        Assert.True(
            result.Succeeded,
            string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

        var llvm = result.Artifacts.GetRequired(CompilerArtifactKeys.LlvmIrModule).Text;

        // The success path moves ownership of 'local' into the caller's slot:
        // the block that returns true must not free anything.
        var fillBody = ExtractDefinitionBody(llvm, "@Fill");
        var successBlock = fillBody
            .Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .SelectMany(static section => SplitBlocks(section))
            .Single(static block => block.Contains("ret i1 true", StringComparison.Ordinal));

        Assert.DoesNotContain("__stark_runtime_free", successBlock, StringComparison.Ordinal);
    }

    private static string ExtractDefinitionBody(string llvm, string symbolFragment)
    {
        var lines = llvm.Split('\n');
        var startIndex = Array.FindIndex(
            lines,
            line => line.StartsWith("define", StringComparison.Ordinal)
                && line.Contains(symbolFragment, StringComparison.Ordinal));
        Assert.True(startIndex >= 0, $"No definition containing '{symbolFragment}' was emitted.");

        var endIndex = Array.FindIndex(lines, startIndex, static line => line == "}");
        Assert.True(endIndex > startIndex, $"Definition containing '{symbolFragment}' has no closing brace.");

        return string.Join('\n', lines[startIndex..(endIndex + 1)]);
    }

    private static IEnumerable<string> SplitBlocks(string body)
    {
        var current = new List<string>();
        foreach (var line in body.Split('\n'))
        {
            if (line.EndsWith(":", StringComparison.Ordinal) && current.Count > 0)
            {
                yield return string.Join('\n', current);
                current.Clear();
            }

            current.Add(line);
        }

        if (current.Count > 0)
        {
            yield return string.Join('\n', current);
        }
    }
}
