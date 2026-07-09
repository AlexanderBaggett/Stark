using Stark.Compiler;
using System.Text.RegularExpressions;

namespace compiler.Tests;

public sealed class PhiAggregateInsertEmissionTests
{
    [Fact]
    public void BranchUpdatedByValueRowStoresThroughInlinedReplaceWithoutDanglingRegisters()
    {
        // Distilled from selfhost PackageImage's AddParameterRow: a large
        // by-value row is copied out of a table, field-updated differently in
        // two branches (so the row's SSA value at the join is a phi), updated
        // once more after the join, and stored back through an inlined
        // Replace. The final field update is an insert whose base is the phi;
        // deferring it as address-forwarded used to leave every consumer
        // referencing a register that was never emitted ("use of undefined
        // value '%vN'" — the failed selfhost package builds of 2026-07-08).
        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(
                """
                import System.Collections
                module Repro

                struct Row
                {
                    u32[0 max] A;
                    u32[0 max] B;
                    u32[0 max] C;
                    u32[0 max] D;
                    u32[0 max] E;
                    u32[0 max] F;
                    u32[0 max] G;
                    u32[0 max] H;
                    u32[0 max] I;
                    u32[0 max] J;
                    u32[0 max] K;
                    u32[0 max] First;
                    u32[0 max] Count;

                    Row()
                    {
                        self.A = 0;
                        self.B = 0;
                        self.C = 0;
                        self.D = 0;
                        self.E = 0;
                        self.F = 0;
                        self.G = 0;
                        self.H = 0;
                        self.I = 0;
                        self.J = 0;
                        self.K = 0;
                        self.First = 0;
                        self.Count = 0;
                    }
                }

                struct Table
                {
                    System.Collections.List<Row> Rows;

                    Table()
                    {
                        self.Rows = new();
                    }

                    fn u32[0 max] Add(mut borrow Table self, Row row)
                    {
                        switch (self.Rows.Push(row))
                        {
                            case System.Memory.MemoryStatus.Ok:
                                return (u32[0 max])self.Rows.Count();
                            case System.Memory.MemoryStatus.Err(var error):
                                return 0;
                        }
                    }

                    finite law retborrow Row Get(borrow Table self, u32[0 max] index)
                    {
                        return self.Rows.Get((u64[0 2 ** 63 - 1])(index - 1));
                    }

                    finite bool Replace(mut borrow Table self, u32[0 max] index, Row row)
                    {
                        self.Rows.GetMut((u64[0 2 ** 63 - 1])(index - 1)) = row;
                        return true;
                    }
                }

                struct Graph
                {
                    u32[0 max] Salt;
                    Table FunctionRows;

                    Graph()
                    {
                        self.Salt = 0;
                        self.FunctionRows = new();
                    }
                }

                internal fn bool AddParameterRow(mut borrow Graph graph, u32[0 max] functionRow, u32[0 max] added)
                {
                    stack mut Row row = graph.FunctionRows.Get(functionRow);
                    if (row.Count == 0)
                    {
                        row.First = added;
                    }
                    else if (added != row.First + row.Count)
                    {
                        return false;
                    }

                    row.Count = row.Count + 1;
                    if (!graph.FunctionRows.Replace(functionRow, row))
                    {
                        return false;
                    }

                    return true;
                }

                export fn i32[min max] main()
                {
                    stack mut Graph graph = new();
                    stack Row seed = new();
                    stack u32[0 max] index = graph.FunctionRows.Add(seed);
                    if (index == 0)
                    {
                        return 2;
                    }

                    if (!AddParameterRow(graph, index, 1))
                    {
                        return 3;
                    }

                    return 0;
                }
                """,
                "/virtual/PhiInsertRepro.stark"),
            new CompilerOptions(
                EmitLlvmIr: true,
                ModuleResolver: new FileSystemModuleResolver(FindStdlibSourceDirectory())));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.LlvmIrModule, out LlvmIrModule? module) && module is not null);

        AssertNoUndefinedRegisterUses(module!.Text);

        var verify = LlvmModuleLint.ExternalVerify(module.Text);
        if (verify.Status != LlvmExternalVerifyStatus.ToolUnavailable)
        {
            Assert.True(verify.Status == LlvmExternalVerifyStatus.Verified, verify.Detail);
        }
    }

    /// <summary>
    /// Every register consumed as a store VALUE must be defined (or be a
    /// parameter) inside the same function — the exact invariant the
    /// deferred-aggregate bug broke.
    /// </summary>
    private static void AssertNoUndefinedRegisterUses(string moduleText)
    {
        foreach (var body in ExtractFunctionBodies(moduleText))
        {
            var defined = Regex.Matches(body, @"%([A-Za-z0-9_.$]+) = ")
                .Select(static match => match.Groups[1].Value)
                .ToHashSet(StringComparer.Ordinal);
            var defineLine = body[..(body.IndexOf('\n') is var end && end >= 0 ? end : body.Length)];
            foreach (Match parameter in Regex.Matches(defineLine, @"%([A-Za-z0-9_.$]+)"))
            {
                defined.Add(parameter.Groups[1].Value);
            }

            foreach (Match store in Regex.Matches(body, @"store [^,]+ %([A-Za-z0-9_.$]+),"))
            {
                var register = store.Groups[1].Value;
                Assert.True(
                    defined.Contains(register),
                    $"store consumes undefined register %{register}:\n{store.Value}");
            }
        }
    }

    private static IEnumerable<string> ExtractFunctionBodies(string moduleText)
    {
        var lines = moduleText.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        List<string>? body = null;
        foreach (var line in lines)
        {
            if (line.StartsWith("define ", StringComparison.Ordinal))
            {
                body = [line];
                continue;
            }

            if (body is null)
            {
                continue;
            }

            if (line.TrimEnd() == "}")
            {
                yield return string.Join('\n', body);
                body = null;
                continue;
            }

            body.Add(line);
        }
    }

    private static string FindStdlibSourceDirectory()
    {
        var directory = AppContext.BaseDirectory;
        while (directory is not null)
        {
            var candidate = Path.Combine(directory, "stdlib", "src");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = Path.GetDirectoryName(directory);
        }

        throw new InvalidOperationException("stdlib/src not found above test base directory");
    }
}
