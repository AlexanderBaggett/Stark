using Stark.Compiler;

namespace compiler.Tests;

/// <summary>
/// Lowering-contract fact lookup regression: typed-call facts are keyed by
/// (enclosing function, line, column), and constructor-body records carry a
/// null enclosing function. The lookup used to fall back to the null key
/// after a primary miss, so a member call in the root could collide with an
/// imported constructor's direct call at the same line and column in a
/// DIFFERENT file and fail STK5003 with a bogus arity mismatch. The test
/// collections feature hit this first (a tagged runner materialized the
/// OwnedCStr constructor, planting a null-keyed record under a fact's call
/// coordinates).
/// </summary>
public sealed class LoweringContractFactKeyRegressionTests
{
    [Fact]
    public void MemberCallsDoNotCollideWithImportedConstructorCallsAtTheSameCoordinates()
    {
        // Both call argument lists must start at the same line and column in
        // their respective files. Line 9 carries the calls; pad the shorter
        // prefix so the '(' columns align exactly.
        const string dependencyCallPrefix = "        self.Value = SeedValue";
        const string rootCallPrefixCore = "        stack i64[min max] n = holder.CountValue";
        var width = Math.Max(dependencyCallPrefix.Length, rootCallPrefixCore.Length);
        var dependencySource = string.Join('\n',
            "module Dep",
            "",
            "public struct Holder",
            "{",
            "    i64[min max] Value;",
            "",
            "    Holder()",
            "    {",
            dependencyCallPrefix.PadRight(width) + "();",
            "    }",
            "",
            "    public finite law i64[min max] CountValue(borrow Holder self)",
            "    {",
            "        return self.Value;",
            "    }",
            "}",
            "",
            "public finite law i64[min max] SeedValue()",
            "{",
            "    return 7;",
            "}",
            "");
        var rootSource = string.Join('\n',
            "import Dep",
            "module Demo",
            "",
            "fn i64[min max] ReadHolder(borrow Dep.Holder holder)",
            "{",
            "    if (holder.CountValue() < 0)",
            "    {",
            "        return 0;",
            rootCallPrefixCore.PadRight(width) + "();",
            "        return n;",
            "    }",
            "",
            "    return holder.CountValue();",
            "}",
            "",
            "export fn i32[min max] main()",
            "{",
            "    stack Dep.Holder holder = new Dep.Holder();",
            "    if (ReadHolder(holder) < 0)",
            "    {",
            "        return 1;",
            "    }",
            "",
            "    return 0;",
            "}",
            "");

        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(rootSource),
            new CompilerOptions(
                StopAfterPassId: "validate-lowering-contract",
                ModuleResolver: new InMemoryModuleResolver(
                [
                    (
                        new ResolvedModuleReference("Dep", "/virtual/Dep.stark", IsExternal: false),
                        dependencySource,
                        "/virtual/Dep.stark"
                    )
                ])));

        Assert.True(
            result.Succeeded,
            string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }
}
