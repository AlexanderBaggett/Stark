using Stark.Compiler;

namespace compiler.Tests;

/// <summary>
/// Type-checking contract for the `try` error-propagation expression under the v2 role
/// model (docs/Self-host-Prep/11-error-propagation.md): propagatable types are any
/// two-variant enums carrying the innate `[Ok]`/`[Err]` role attributes — never
/// recognized by type name and never by stdlib identity. Covers the accepted shapes
/// (same-error, cross-family + `from` funnel, unit failures), the position rule, and
/// every misuse diagnostic (STK3037-STK3043).
/// </summary>
public sealed class TryPropagationDiagnosticsTests
{
    private const string Prelude =
        """
        module Demo

        enum LowErr { Disk }
        enum HighErr { Low from LowErr, Other }

        // Role-annotated propagatable enums with deliberately non-conventional names:
        // recognition must work purely from the [Ok]/[Err] roles.
        enum FetchOutcome<T>
        {
            [Ok] Got(T),
            [Err] Failed(LowErr),
        }

        enum LoadOutcome<T>
        {
            [Ok] Loaded(T),
            [Err] Broke(HighErr),
        }

        enum Lookup<T>
        {
            [Ok] Hit(T),
            [Err] Miss,
        }

        fn FetchOutcome<i32[min max]> Fetch(i32[min max] x)
        {
            return FetchOutcome<i32[min max]>.Got(x + 1);
        }

        fn Lookup<i32[min max]> Find(i32[min max] x)
        {
            return Lookup<i32[min max]>.Hit(x);
        }
        """;

    [Fact]
    public void TrySameErrorTypePropagationTypeChecks()
    {
        var result = Compile(Prelude +
            """

            fn FetchOutcome<i32[min max]> Same(i32[min max] x)
            {
                stack i32[min max] n = try Fetch(x);
                return FetchOutcome<i32[min max]>.Got(n + 1);
            }
            """);

        AssertSucceeded(result);
    }

    [Fact]
    public void TryCrossFamilyPropagationUsesFromFunnel()
    {
        // The operand (FetchOutcome, fails with LowErr) and the enclosing function
        // (LoadOutcome, fails with HighErr) are different enums; the funnel
        // `Low from LowErr` on HighErr connects them.
        var result = Compile(Prelude +
            """

            fn LoadOutcome<i32[min max]> Cross(i32[min max] x)
            {
                stack i32[min max] n = try Fetch(x);
                return LoadOutcome<i32[min max]>.Loaded(n + 1);
            }
            """);

        AssertSucceeded(result);
    }

    [Fact]
    public void TryUnitFailurePropagationTypeChecks()
    {
        // Lookup's [Err] Miss has no payload; it propagates into another no-payload [Err].
        var result = Compile(Prelude +
            """

            fn Lookup<i32[min max]> Chained(i32[min max] x)
            {
                stack i32[min max] n = try Find(x);
                return Lookup<i32[min max]>.Hit(n + 1);
            }
            """);

        AssertSucceeded(result);
    }

    [Fact]
    public void TryAsAssignmentRightSideAndExpressionStatementAreBoundaries()
    {
        var result = Compile(Prelude +
            """

            fn FetchOutcome<i32[min max]> Boundaries(i32[min max] x)
            {
                stack mut i32[min max] acc = 0;
                acc = try Fetch(x);      // assignment right side: a boundary
                try Fetch(acc);          // bare expression statement: a boundary (value discarded)
                return FetchOutcome<i32[min max]>.Got(acc);
            }
            """);

        AssertSucceeded(result);
    }

    [Fact]
    public void TryNestedInsideCallArgumentIsRejected()
    {
        var result = Compile(Prelude +
            """

            fn FetchOutcome<i32[min max]> Nested(i32[min max] x)
            {
                return Fetch(try Fetch(x));
            }
            """);

        AssertDiagnostic(result, "STK3037");
    }

    [Fact]
    public void TryInFunctionWithoutPropagatableReturnIsRejected()
    {
        var result = Compile(Prelude +
            """

            fn i32[min max] BadReturn(i32[min max] x)
            {
                stack i32[min max] n = try Fetch(x);
                return n;
            }
            """);

        AssertDiagnostic(result, "STK3038");
    }

    [Fact]
    public void TryOnEnumWithoutRolesIsRejected()
    {
        // Plain has the right *shape* but carries no [Ok]/[Err] roles, so it is not
        // propagatable: roles are opt-in, never inferred.
        var result = Compile(Prelude +
            """

            enum Plain<T> { Ok(T), Err(LowErr) }

            fn Plain<i32[min max]> Get() { return Plain<i32[min max]>.Ok(1); }

            fn Plain<i32[min max]> BadOperand()
            {
                stack i32[min max] n = try Get();
                return Plain<i32[min max]>.Ok(n);
            }
            """);

        AssertDiagnostic(result, "STK3039");
    }

    [Fact]
    public void TryMixingPayloadAndUnitFailuresIsRejected()
    {
        // Lookup fails without a payload; LoadOutcome fails with HighErr. Neither
        // discarding nor inventing an error value is allowed.
        var result = Compile(Prelude +
            """

            fn LoadOutcome<i32[min max]> Mixed(i32[min max] x)
            {
                stack i32[min max] n = try Find(x);
                return LoadOutcome<i32[min max]>.Loaded(n);
            }
            """);

        AssertDiagnostic(result, "STK3038");
    }

    [Fact]
    public void CrossFamilyTryWithoutFromFunnelIsRejected()
    {
        var result = Compile(
            """
            module Demo

            enum AErr { A }
            enum BErr { B }

            enum AOutcome<T> { [Ok] Got(T), [Err] Failed(AErr) }
            enum BOutcome<T> { [Ok] Got(T), [Err] Failed(BErr) }

            fn AOutcome<i32[min max]> Get() { return AOutcome<i32[min max]>.Got(1); }

            fn BOutcome<i32[min max]> NoFunnel()
            {
                stack i32[min max] n = try Get();
                return BOutcome<i32[min max]>.Got(n);
            }
            """);

        AssertDiagnostic(result, "STK3041");
    }

    [Fact]
    public void DuplicateFromFunnelForOneSourceTypeIsRejected()
    {
        var result = Compile(
            """
            module Demo

            enum LowErr { Disk }
            enum Ambiguous { A from LowErr, B from LowErr }
            """);

        AssertDiagnostic(result, "STK3040");
    }

    [Fact]
    public void UnknownVariantAttributeIsRejected()
    {
        var result = Compile(
            """
            module Demo

            enum Outcome<T>
            {
                [Okk] Got(T),
                [Err] Failed(i32[min max]),
            }
            """);

        AssertDiagnostic(result, "STK3042");
    }

    [Fact]
    public void RoleAttributeWithArgumentsIsRejected()
    {
        var result = Compile(
            """
            module Demo

            enum Outcome<T>
            {
                [Ok(1)] Got(T),
                [Err] Failed(i32[min max]),
            }
            """);

        AssertDiagnostic(result, "STK3042");
    }

    [Fact]
    public void RolesOnThreeVariantEnumAreRejected()
    {
        var result = Compile(
            """
            module Demo

            enum Outcome<T>
            {
                [Ok] Got(T),
                [Err] Failed(i32[min max]),
                Pending,
            }
            """);

        AssertDiagnostic(result, "STK3043");
    }

    [Fact]
    public void SingleRoleWithoutItsPairIsRejected()
    {
        var result = Compile(
            """
            module Demo

            enum Outcome<T>
            {
                [Ok] Got(T),
                Failed(i32[min max]),
            }
            """);

        AssertDiagnostic(result, "STK3043");
    }

    [Fact]
    public void RoleVariantWithMultiplePayloadsIsRejected()
    {
        var result = Compile(
            """
            module Demo

            enum Outcome
            {
                [Ok] Got(i32[min max], i32[min max]),
                [Err] Failed(i32[min max]),
            }
            """);

        AssertDiagnostic(result, "STK3043");
    }

    private static CompilationResult Compile(string source)
    {
        return DefaultCompilerPipeline.Create().Run(
            new CompilationInput(source),
            new CompilerOptions(StopAfterPassId: "semantic-validate"));
    }

    private static void AssertSucceeded(CompilationResult result)
    {
        Assert.True(
            result.Succeeded,
            string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }

    private static void AssertDiagnostic(CompilationResult result, string code)
    {
        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == code);
    }
}
