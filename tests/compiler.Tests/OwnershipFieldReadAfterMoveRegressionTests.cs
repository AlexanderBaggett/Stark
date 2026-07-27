using Stark.Compiler;

namespace compiler.Tests;

/// <summary>
/// Field-read-after-move regressions: dynamic storage header reads
/// (`moved.Field.Length` / `.Capacity`) used to bypass the moved-root check
/// because the member-access evaluation returned a plain scalar with no
/// variable attached, so the outer use check had nothing to inspect.
/// Whole-value uses always errored; the gap was specific to the
/// Length/Capacity special case. The check must also keep legitimate
/// constructor flows working: reading an already-assigned dynamic field's
/// header mid-construction stays legal.
/// </summary>
public sealed class OwnershipFieldReadAfterMoveRegressionTests
{
    private const string Prelude =
        """
        module Demo

        struct Payload
        {
            dynamic i64[min max] Items;
            i64[min max] Count;

            Payload()
            {
                self.Items = new();
                self.Count = 0;
            }
        }

        fn bool Consume(Payload payload)
        {
            return true;
        }

        """;

    [Fact]
    public void DynamicLengthReadOnDefinitelyMovedRootErrors()
    {
        var result = Compile(
            Prelude +
            """
            fn i64[min max] Build()
            {
                stack mut Payload payload = new();
                stack bool sink = Consume(payload);
                return payload.Items.Length;
            }
            """);

        AssertMoveError(result);
    }

    [Fact]
    public void DynamicCapacityReadOnDefinitelyMovedRootErrors()
    {
        var result = Compile(
            Prelude +
            """
            fn i64[min max] Build()
            {
                stack mut Payload payload = new();
                stack bool sink = Consume(payload);
                return payload.Items.Capacity;
            }
            """);

        AssertMoveError(result);
    }

    [Fact]
    public void DynamicLengthReadOnMaybeMovedRootErrors()
    {
        var result = Compile(
            Prelude +
            """
            fn i64[min max] Build(bool flag)
            {
                stack mut Payload payload = new();
                if (flag)
                {
                    stack bool sink = Consume(payload);
                }

                return payload.Items.Length;
            }
            """);

        AssertMoveError(result);
    }

    [Fact]
    public void DynamicMemberCallOnMovedReceiverErrors()
    {
        var result = Compile(
            Prelude +
            """
            fn i64[min max] Build()
            {
                stack mut Payload payload = new();
                stack bool sink = Consume(payload);
                if (!payload.Items.TryReserve(1))
                {
                    return 0;
                }

                return 1;
            }
            """);

        AssertMoveError(result);
    }

    [Fact]
    public void ScalarFieldReadOnMovedRootErrors()
    {
        var result = Compile(
            Prelude +
            """
            fn i64[min max] Build()
            {
                stack mut Payload payload = new();
                stack bool sink = Consume(payload);
                return payload.Count;
            }
            """);

        AssertMoveError(result);
    }

    [Fact]
    public void ConstructorReadsAssignedDynamicFieldHeaderLegally()
    {
        var result = Compile(
            """
            module Demo

            struct Payload
            {
                dynamic i64[min max] Items;
                i64[min max] Count;

                Payload()
                {
                    self.Items = new();
                    self.Count = self.Items.Length;
                }
            }

            fn i64[min max] Build()
            {
                stack Payload payload = new();
                return payload.Count;
            }
            """);

        Assert.True(
            result.Succeeded,
            string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }

    private static void AssertMoveError(CompilationResult result)
    {
        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "STK4200");
    }

    private static CompilationResult Compile(string source)
    {
        return DefaultCompilerPipeline.Create().Run(new CompilationInput(source));
    }
}
