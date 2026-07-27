using Stark.Compiler;

namespace compiler.Tests;

public sealed class LlvmModuleLintTests
{
    [Fact]
    public void DetectsZeroDereferenceable()
    {
        var violations = LlvmModuleLint.Check(
            """
            define void @f(ptr nonnull dereferenceable(0) %arg) {
              ret void
            }
            """);

        var violation = Assert.Single(violations);
        Assert.Contains("dereferenceable(0)", violation, StringComparison.Ordinal);
    }

    [Fact]
    public void AcceptsPositiveDereferenceable()
    {
        Assert.Empty(LlvmModuleLint.Check(
            """
            define void @f(ptr nonnull dereferenceable(4) %arg) {
              ret void
            }
            """));
    }

    [Fact]
    public void DetectsEmptyOrInvertedInitializesRange()
    {
        var violations = LlvmModuleLint.Check(
            """
            define void @f(ptr initializes((0, 8), (12, 12)) %out) {
              ret void
            }
            """);

        var violation = Assert.Single(violations);
        Assert.Contains("initializes range (12, 12)", violation, StringComparison.Ordinal);
    }

    [Fact]
    public void DetectsCallNoAliasContainingOwnFreshResultScope()
    {
        // The exact self-contradiction behind the 2026-07 sret heisenbug: the
        // call's own sret slot's fresh scope sits in the call's !noalias set,
        // so the post-call reload (which claims that scope) is provably
        // independent of the call and LLVM forwards the stale pre-call bytes.
        var violations = LlvmModuleLint.Check(
            """
            define i1 @scan(ptr %table) {
              call fastcc void @item_at(ptr noalias sret(%Item) align 8 %v27, ptr %table), !noalias !3280
              ret i1 false
            }
            !3272 = distinct !{!3272, !"scan"}
            !3274 = distinct !{!3274, !3272, !"scan.fresh.v27"}
            !3275 = distinct !{!3275, !3272, !"scan.slot.table"}
            !3280 = !{!3274, !3275}
            """);

        var violation = Assert.Single(violations);
        Assert.Contains("fresh scope of its own result %v27", violation, StringComparison.Ordinal);
    }

    [Fact]
    public void DetectsDirectCallResultFreshScopeInOwnNoAliasList()
    {
        var violations = LlvmModuleLint.Check(
            """
            define i32 @run(ptr %table) {
              %v9 = call fastcc i32 @get(ptr %table), !noalias !12
              ret i32 %v9
            }
            !10 = distinct !{!10, !"run"}
            !11 = distinct !{!11, !10, !"run.fresh.v9"}
            !12 = !{!11}
            """);

        var violation = Assert.Single(violations);
        Assert.Contains("fresh scope of its own result %v9", violation, StringComparison.Ordinal);
    }

    [Fact]
    public void AcceptsCallNoAliasReferencingOtherFreshScopes()
    {
        // A DIFFERENT call's fresh scope in this call's !noalias set is the
        // healthy case (the two results really are independent), and the
        // call's own fresh scope belongs in !alias.scope.
        Assert.Empty(LlvmModuleLint.Check(
            """
            define i1 @scan(ptr %table) {
              call fastcc void @item_at(ptr noalias sret(%Item) align 8 %v27, ptr %table), !alias.scope !3281, !noalias !3280
              ret i1 false
            }
            !3272 = distinct !{!3272, !"scan"}
            !3273 = distinct !{!3273, !3272, !"scan.fresh.v14"}
            !3274 = distinct !{!3274, !3272, !"scan.fresh.v27"}
            !3280 = !{!3273}
            !3281 = !{!3274}
            """));
    }

    [Fact]
    public void HealthyScopedNoAliasEmissionSurvivesLint()
    {
        // A real sret + scoped-noalias emission (the shape from the heisenbug
        // regression fixture) must produce zero lint violations: this guards
        // against false positives before the lint gates every debug compile.
        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(
                """
                module Demo

                struct Item
                {
                    u8[0 max] Kind;
                    u32[0 max] Name;
                    u64[0 max] Start;
                    u64[0 max] End;
                    u64[0 max] Extra;

                    Item()
                    {
                        self.Kind = 0;
                        self.Name = 0;
                        self.Start = 0;
                        self.End = 0;
                        self.Extra = 0;
                    }
                }

                struct Table
                {
                    dynamic u8[0 max] Kinds;
                    dynamic u32[0 max] Names;

                    Table()
                    {
                        self.Kinds = new();
                        self.Names = new();
                    }

                    fn bool Push(mut borrow Table self, u8[0 max] kind, u32[0 max] name)
                    {
                        if (!self.Kinds.TryReserve(1) || !self.Names.TryReserve(1))
                        {
                            return false;
                        }

                        init self.Kinds[self.Kinds.Length] = kind;
                        init self.Names[self.Names.Length] = name;
                        return true;
                    }

                    finite law u64[0 2 ** 63 - 1] Count(borrow Table self)
                    {
                        return self.Kinds.Length;
                    }

                    finite law Item ItemAt(borrow Table self, u64[0 2 ** 63 - 1] index)
                    {
                        stack mut Item item = new();
                        if (index >= self.Kinds.Length)
                        {
                            return item;
                        }

                        if (index >= self.Names.Length)
                        {
                            return item;
                        }

                        item.Kind = self.Kinds[index];
                        item.Name = self.Names[index];
                        return item;
                    }
                }

                finite bool Scan(borrow Table table, u32[0 max] want, out u32[0 max] foundIndex, out Item found)
                {
                    foundIndex = 0;
                    found = new();
                    stack u64[0 2 ** 63 - 1] count = table.Count();
                    stack mut u64[0 2 ** 63 - 1] index = 0;
                    while willexit (index < count)
                    {
                        stack Item item = table.ItemAt(index);
                        if (item.Kind == 1 && item.Name == want)
                        {
                            foundIndex = (u32[0 max])index;
                            found = item;
                            return true;
                        }

                        index = index + 1;
                    }

                    return false;
                }

                fn i64[min max] Run()
                {
                    stack mut Table table = new Table();
                    if (!table.Push(2, 7) || !table.Push(1, 9))
                    {
                        return -1;
                    }

                    stack mut u32[0 max] foundIndex = 0;
                    stack mut Item found = new();
                    if (!Scan(table, 9, foundIndex, found))
                    {
                        return 0;
                    }

                    return 1;
                }
                """,
                "/virtual/LintFixture.stark"),
            new CompilerOptions(EmitLlvmIr: true));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.LlvmIrModule, out LlvmIrModule? module) && module is not null);
        Assert.Empty(LlvmModuleLint.Check(module!.Text));
    }

    [Fact]
    public void ExternalVerifierAcceptsHealthyEmission()
    {
        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(
                """
                module Demo

                fn i32[min max] main()
                {
                    return 3;
                }
                """,
                "/virtual/VerifyFixture.stark"),
            new CompilerOptions(EmitLlvmIr: true));

        Assert.True(result.Succeeded);
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.LlvmIrModule, out LlvmIrModule? module) && module is not null);

        var verify = LlvmModuleLint.ExternalVerify(module!.Text);
        if (verify.Status == LlvmExternalVerifyStatus.ToolUnavailable)
        {
            // No `opt` on this machine; the lint tests above still cover the
            // in-process checks.
            return;
        }

        Assert.True(verify.Status == LlvmExternalVerifyStatus.Verified, verify.Detail);
    }

    [Fact]
    public void ExternalVerifierRejectsInvalidModule()
    {
        var verify = LlvmModuleLint.ExternalVerify(
            """
            define void @f(ptr dereferenceable(0) %arg) {
              ret void
            }
            """);

        if (verify.Status == LlvmExternalVerifyStatus.ToolUnavailable)
        {
            return;
        }

        Assert.Equal(LlvmExternalVerifyStatus.Failed, verify.Status);
        Assert.NotEmpty(verify.Detail);
    }
}
