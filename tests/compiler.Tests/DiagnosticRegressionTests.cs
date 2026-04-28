using Stark.Compiler;

namespace compiler.Tests;

public sealed class DiagnosticRegressionTests
{
    [Fact]
    public void MalformedSyntaxProducesAStableParseDiagnostic()
    {
        var result = Compile(
            """
            module Demo

            fn void Run() {
                stack i32[-2147483648 2147483647] value = 1
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK1000", "missing ';'");
    }

    [Fact]
    public void InvalidEscapeSequencesProduceStableParseDiagnostics()
    {
        var result = Compile(
            """
            module Demo

            fn ascii Run() {
                return "\q";
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK1000", "Invalid escape sequence '\\q' in string literal.");
    }

    [Fact]
    public void CharacterLiteralsRequireExactlyOneDecodedCharacter()
    {
        var result = Compile(
            """
            module Demo

            fn ascii Run() {
                return 'ab';
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK1000", "Character literals must decode to exactly one character.");
    }

    [Fact]
    public void SelfImportsProduceAStableFrontEndDiagnostic()
    {
        var result = Compile(
            """
            import Demo
            module Demo

            fn void Run() {
                return;
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK2001", "cannot import itself");
    }

    [Fact]
    public void MissingMembersProduceTypeDiagnostics()
    {
        var result = Compile(
            """
            module Demo

            struct Box {
                i32[-2147483648 2147483647] Value;
            }

            fn i32[-2147483648 2147483647] Run() {
                stack Box box = new Box() { Value = 1 };
                return box.Missing;
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3005", "does not contain a field named 'Missing'");
    }

    [Fact]
    public void LawsRejectOutParameters()
    {
        var result = Compile(
            """
            module Demo

            law i32[-2147483648 2147483647] Read(out i32[-2147483648 2147483647] value) {
                return 0;
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK4101", "cannot declare 'out' or 'init' parameters");
    }

    [Fact]
    public void StaticOwnedValuesCannotBeMovedOut()
    {
        var result = Compile(
            """
            module Demo

            struct Box {
                i32[-2147483648 2147483647] Value;
            }

            static Box Current = new Box() { Value = 1 };

            fn Box Take() {
                return Current;
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK4204", "Cannot move out of global or static storage 'Current'");
    }

    [Fact]
    public void ImmutableGlobalsRejectRebindingWithSpecificDiagnostic()
    {
        var result = Compile(
            """
            module Demo

            static i32[-2147483648 2147483647] Answer = 42;

            fn void Run() {
                Answer = 7;
                return;
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3007", "Cannot rebind immutable global 'Answer'.");
    }

    [Fact]
    public void ConstGlobalsRejectMutationWithSpecificDiagnostic()
    {
        var result = Compile(
            """
            module Demo

            struct Box {
                i32[-2147483648 2147483647] Value;
            }

            const Box Current = new Box() { Value = 1 };

            fn void Run() {
                Current.Value = 2;
                return;
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3007", "Cannot mutate member 'Value' of constant global 'Current'.");
    }

    [Fact]
    public void ConstGlobalsExplainWhyReachableStateIsNotFrozen()
    {
        var result = Compile(
            """
            module Demo

            struct Holder {
                rawptr<i8[-128 127]> Ptr;
            }

            const Holder Current = new Holder() { Ptr = null };
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK4007", "fully frozen object graph", "Current.Ptr", "raw pointer type");
    }

    [Fact]
    public void ConstGlobalsExplainWhenInitializersCannotLowerAsStaticData()
    {
        var result = Compile(
            """
            module Demo

            fn i32[-2147483648 2147483647] Source();

            const i32 Answer = Source();
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK4008", "frozen initializer", "materialized as static data");
    }

    [Fact]
    public void NamedAggregateWholeValueTypedCaptureMarksFollowingDefaultUnreachable()
    {
        var result = Compile(
            """
            module Demo

            struct Box {
                i32[-2147483648 2147483647] Value;
            }

            fn i32[-2147483648 2147483647] Run(Box box) {
                switch (box) {
                    case Box capture:
                        return 1;
                    default:
                        return 0;
                }
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3019", "Switch label 'default' is unreachable", "already exhaustive");
        AssertDiagnostic(result, "STK3020", "Switch coverage becomes exhaustive here for 'Box'.");
    }

    [Fact]
    public void CapturePatternsMixedWithOtherLabelsProduceAnExplicitDiagnostic()
    {
        var result = Compile(
            """
            module Demo

            fn i32[-2147483648 2147483647] Run(i32[-2147483648 2147483647] value) {
                switch (value) {
                    case var capture:
                    case 1:
                        return 1;
                    default:
                        return 0;
                }
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3008", "Switch capture patterns must currently appear as the only label in their section");
    }

    [Fact]
    public void UnreachableSwitchLabelsPointBackToTheCoveringArm()
    {
        var result = Compile(
            """
            module Demo

            fn i32[-2147483648 2147483647] Run(bool value) {
                switch (value) {
                    case true:
                        return 1;
                    case false:
                        return 0;
                    default:
                        return 2;
                }
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3019", "Switch label 'default' is unreachable", "already exhaustive", "earlier unguarded label 'false'");
        AssertDiagnostic(result, "STK3020", "Switch coverage becomes exhaustive here for 'bool'.");
    }

    [Fact]
    public void BreakOutsideLoopOrSwitchProducesAStableSemanticDiagnostic()
    {
        var result = Compile(
            """
            module Demo

            fn void Run() {
                break;
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK4113", "'break' requires an enclosing loop or switch.");
    }

    [Fact]
    public void ContinueOutsideLoopProducesAStableSemanticDiagnostic()
    {
        var result = Compile(
            """
            module Demo

            fn void Run() {
                continue;
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK4114", "'continue' requires an enclosing loop.");
    }

    private static CompilationResult Compile(string source)
    {
        return DefaultCompilerPipeline.Create().Run(new CompilationInput(source));
    }

    private static void AssertDiagnostic(CompilationResult result, string code, params string[] messageFragments)
    {
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == code
                && messageFragments.All(fragment => diagnostic.Message.Contains(fragment, StringComparison.Ordinal)));
    }
}
