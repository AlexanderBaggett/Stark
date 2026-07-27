using Stark.Compiler;

namespace compiler.Tests;

/// <summary>
/// Equality guards feed the dynamic-storage read proof. An
/// <c>if (dyn.Length != k) { return ...; }</c> early-return guard (or an
/// <c>if (dyn.Length == k)</c> true branch) establishes <c>Length == k</c> on the
/// surviving path, which proves the constant indexed reads <c>dyn[0..k-1]</c>; a
/// non-empty check (<c>!= 0</c>) proves <c>dyn[0]</c>. Out-of-bounds constant
/// indices, and reads after a mutating use of the storage, still fail.
/// </summary>
public sealed class EqualityGuardFlowFactTests
{
    private const string Prelude = """
        module Demo

        struct Table
        {
            dynamic i32[min max] Items;
        }

        """;

    [Fact]
    public void InequalityEarlyReturnGuardProvesConstantIndexOnSurvivingPath()
    {
        var result = Compile(Prelude + """
            finite law i32[min max] First(borrow Table self)
            {
                if (self.Items.Length != 1)
                {
                    return -1;
                }

                return self.Items[0];
            }
            """);

        AssertSucceeds(result);
    }

    [Fact]
    public void EqualityTrueBranchProvesConstantIndices()
    {
        var result = Compile(Prelude + """
            finite law i32[min max] Second(borrow Table self)
            {
                if (self.Items.Length == 2)
                {
                    return self.Items[1];
                }

                return -1;
            }
            """);

        AssertSucceeds(result);
    }

    [Fact]
    public void NonEmptyInequalityProvesIndexZero()
    {
        var result = Compile(Prelude + """
            finite law i32[min max] FirstNonEmpty(borrow Table self)
            {
                if (self.Items.Length != 0)
                {
                    return self.Items[0];
                }

                return -1;
            }
            """);

        AssertSucceeds(result);
    }

    [Fact]
    public void EqualityGuardDoesNotProveOutOfBoundsIndex()
    {
        var result = Compile(Prelude + """
            finite law i32[min max] PastEnd(borrow Table self)
            {
                if (self.Items.Length != 1)
                {
                    return -1;
                }

                return self.Items[1];
            }
            """);

        AssertSlotProofError(result, "self.Items[1]");
    }

    [Fact]
    public void EqualityFactDiesOnMutatingUse()
    {
        var result = Compile(Prelude + """
            fn void Clear(mut borrow dynamic i32[min max] items)
            {
            }

            fn i32[min max] AfterMutation(mut borrow Table self)
            {
                if (self.Items.Length != 1)
                {
                    return -1;
                }

                Clear(self.Items);
                return self.Items[0];
            }
            """);

        AssertSlotProofError(result, "self.Items[0]");
    }

    private static void AssertSucceeds(CompilationResult result)
    {
        Assert.True(
            result.Succeeded,
            string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }

    private static void AssertSlotProofError(CompilationResult result, string slot)
    {
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "STK4205"
                && diagnostic.Message.Contains("without a proof that the slot is initialized", StringComparison.Ordinal)
                && diagnostic.Message.Contains(slot, StringComparison.Ordinal));
    }

    private static CompilationResult Compile(string source)
    {
        return DefaultCompilerPipeline.Create().Run(
            new CompilationInput(source),
            new CompilerOptions(EmitLlvmIr: true));
    }
}
