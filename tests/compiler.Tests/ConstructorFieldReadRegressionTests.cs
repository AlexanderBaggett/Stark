using Stark.Compiler;

namespace compiler.Tests;

/// <summary>
/// Regression for constructor definite-assignment of READS (STK3055). A `self`
/// field holds its pre-construction zero state until the body assigns it, so
/// reading an owning field (dynamic storage, owned text) before assignment reads
/// that zero state instead of the intended value. The constructor body is walked
/// in evaluation order tracking the fields assigned so far (monotonic union, so
/// valid code is never rejected); copyable inline storage — scalars, fixed
/// arrays, copyable aggregates — has a valid zero state and is excluded.
/// </summary>
public sealed class ConstructorFieldReadRegressionTests
{
    [Fact]
    public void ReadingDynamicFieldBeforeAssignmentIsRejected()
    {
        var result = Compile(
            """
            module Demo

            struct Foo
            {
                u64[0 2 ** 63 - 1] Count;
                dynamic i32[min max] Items;

                Foo()
                {
                    self.Count = self.Items.Length;
                    self.Items = new();
                }
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3055", "field 'Items' of 'self' before it is assigned");
    }

    [Fact]
    public void IndexingIntoUnassignedDynamicFieldIsRejected()
    {
        var result = Compile(
            """
            module Demo

            struct Foo
            {
                dynamic i32[min max] Items;

                Foo()
                {
                    self.Items[0] = 5;
                    self.Items = new();
                }
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3055", "field 'Items' of 'self' before it is assigned");
    }

    [Fact]
    public void ReadingAnotherUnassignedOwningFieldIsRejected()
    {
        var result = Compile(
            """
            module Demo

            struct Foo
            {
                dynamic i32[min max] A;
                dynamic i32[min max] B;

                Foo()
                {
                    self.A = self.B;
                    self.B = new();
                }
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3055", "field 'B' of 'self' before it is assigned");
    }

    [Fact]
    public void ReadingDynamicFieldAfterAssignmentIsAllowed()
    {
        var result = Compile(
            """
            module Demo

            struct Foo
            {
                dynamic i32[min max] Items;
                u64[0 2 ** 63 - 1] Count;

                Foo()
                {
                    self.Items = new();
                    self.Count = self.Items.Length;
                }
            }
            """);

        AssertNoConstructorReadError(result);
    }

    [Fact]
    public void WritingFixedArrayElementsIsNotAFieldRead()
    {
        var result = Compile(
            """
            module Demo

            struct Foo
            {
                bool[64] Flags;

                Foo()
                {
                    self.Flags[0] = true;
                }
            }
            """);

        AssertNoConstructorReadError(result);
    }

    [Fact]
    public void ReadingScalarFieldBeforeAssignmentIsAllowed()
    {
        // Scalars have a valid zero state; only owning fields are flagged.
        var result = Compile(
            """
            module Demo

            struct Foo
            {
                u64[0 2 ** 63 - 1] A;
                u64[0 2 ** 63 - 1] B;

                Foo()
                {
                    self.B = self.A;
                    self.A = 5;
                }
            }
            """);

        AssertNoConstructorReadError(result);
    }

    [Fact]
    public void FieldAssignedOnAnyEarlierPathIsNotFlagged()
    {
        // Monotonic union: a field assigned in a branch counts as assigned for the
        // later read, so valid code is conservatively accepted.
        var result = Compile(
            """
            module Demo

            struct Foo
            {
                dynamic i32[min max] Items;
                u64[0 2 ** 63 - 1] Count;

                Foo()
                {
                    if (1 < 2)
                    {
                        self.Items = new();
                    }

                    self.Count = self.Items.Length;
                    self.Items = new();
                }
            }
            """);

        AssertNoConstructorReadError(result);
    }

    private static CompilationResult Compile(string source)
    {
        return DefaultCompilerPipeline.Create().Run(
            new CompilationInput(source),
            new CompilerOptions(StopAfterPassId: "type-check"));
    }

    private static void AssertNoConstructorReadError(CompilationResult result)
    {
        Assert.True(
            result.Succeeded,
            string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "STK3055");
    }

    private static void AssertDiagnostic(CompilationResult result, string code, params string[] messageFragments)
    {
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == code
                && messageFragments.All(fragment => diagnostic.Message.Contains(fragment, StringComparison.Ordinal)));
    }
}
