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

    /// <summary>
    /// Lock-in for cross-module imported-generic member-call lowering. Compiling a
    /// root module that calls an imported generic instance method (List&lt;i32&gt;.Push-style,
    /// resolved from a dependency module through <see cref="InMemoryModuleResolver"/>) and
    /// stopping at "lower-mir" must succeed. The in-process binding for an imported
    /// generic member call matches the per-module loweringContext.FilePath against the
    /// BoundOperationKey.FilePath stamped by TypeChecker.Location(). That coupling is
    /// fragile: it works only because Location() uniformly stamps the single root input
    /// path. Any future "F2"-class change that desyncs that stamped path would make this
    /// binding miss and surface a bare STK9999 instead of a clean lowering. This test
    /// fails LOUDLY if that regression returns, rather than letting STK9999 leak out.
    /// </summary>
    [Fact]
    public void ImportedGenericMemberCallLowersAcrossModulesWithoutInvariantViolation()
    {
        var dependencySource = string.Join('\n',
            "module Dep",
            "",
            "public struct Bag<T>",
            "{",
            "    i32[min max] Count;",
            "",
            "    public finite Bag<T> Push(borrow Bag<T> self, T item)",
            "    {",
            "        return new Bag<T>()",
            "        {",
            "            Count = self.Count + 1",
            "        };",
            "    }",
            "",
            "    public finite law i32[min max] Size(borrow Bag<T> self)",
            "    {",
            "        return self.Count;",
            "    }",
            "}",
            "",
            "public finite Bag<T> NewBag<T>()",
            "{",
            "    return new Bag<T>()",
            "    {",
            "        Count = 0",
            "    };",
            "}",
            "");

        var rootSource = string.Join('\n',
            "import Dep",
            "module Demo",
            "",
            "export fn i32[min max] main()",
            "{",
            "    stack Dep.Bag<i32[min max]> bag = Dep.NewBag<i32[min max]>();",
            "    stack Dep.Bag<i32[min max]> pushed = bag.Push(7);",
            "    return pushed.Size();",
            "}",
            "");

        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(rootSource),
            new CompilerOptions(
                StopAfterPassId: "lower-mir",
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
        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "STK9999");
    }

    [Fact]
    public void ConstructorBodyObjectCreationFactsDoNotCollideAcrossSourceFiles()
    {
        // The `self.Field = new();` expressions intentionally share the same line
        // and column in both files. Constructor-body operation facts must bind by
        // constructor body key, not by a null scope plus root-stamped source location.
        var dependencySource = string.Join('\n',
            "",
            "module Dep",
            "",
            "public struct Inner",
            "{",
            "    i32[min max] Value;",
            "",
            "    Inner()",
            "    {",
            "        self.Value = 1;",
            "    }",
            "}",
            "",
            "public struct Box",
            "{",
            "    Inner Field;",
            "",
            "    Box()",
            "    {",
            "        self.Field = new();",
            "    }",
            "}",
            "");
        var rootSource = string.Join('\n',
            "import Dep",
            "module Demo",
            "",
            "struct Inner",
            "{",
            "    bool Flag;",
            "",
            "    Inner()",
            "    {",
            "        self.Flag = true;",
            "    }",
            "}",
            "",
            "struct Box",
            "{",
            "    Inner Field;",
            "",
            "    Box()",
            "    {",
            "        self.Field = new();",
            "    }",
            "}",
            "",
            "export fn i32[min max] main()",
            "{",
            "    stack Box local = new();",
            "    stack Dep.Box imported = new Dep.Box();",
            "    if (!local.Field.Flag)",
            "    {",
            "        return 1;",
            "    }",
            "",
            "    return imported.Field.Value - 1;",
            "}",
            "");

        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(rootSource),
            new CompilerOptions(
                StopAfterPassId: "lower-mir",
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
        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "STK9999");
    }
}
