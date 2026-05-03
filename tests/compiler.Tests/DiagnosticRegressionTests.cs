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

            unsafe fn void Run() {
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

            unsafe fn ascii Run() {
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

            unsafe fn ascii Run() {
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

            unsafe fn void Run() {
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

            unsafe fn i32[-2147483648 2147483647] Run() {
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

            unsafe law i32[-2147483648 2147483647] Read(out i32[-2147483648 2147483647] value) {
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

            unsafe fn Box Take() {
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

            unsafe fn void Run() {
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

            unsafe fn void Run() {
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

            unsafe fn i32[-2147483648 2147483647] Run(Box box) {
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

            unsafe fn i32[-2147483648 2147483647] Run(i32[-2147483648 2147483647] value) {
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

            unsafe fn i32[-2147483648 2147483647] Run(bool value) {
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

            unsafe fn void Run() {
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

            unsafe fn void Run() {
                continue;
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK4114", "'continue' requires an enclosing loop.");
    }

    [Fact]
    public void RuntimeDisjointConditionsCompileWithoutLoweringDiagnostic()
    {
        var result = Compile(
            """
            module Demo

            struct Box {
                i32[-2147483648 2147483647] Value;
            }

            unsafe fn void Run(borrow Box left, borrow Box right) {
                if disjoint(left, right) {
                    return;
                }

                return;
            }
            """);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "STK3026");
    }

    [Fact]
    public void DisjointParameterCallsRejectObviousOverlappingArguments()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn void Touch(disjoint rawmutptr<i32[-2147483648 2147483647]> left, disjoint rawmutptr<i32[-2147483648 2147483647]> right) {
                return;
            }

            unsafe fn void Run(rawmutptr<i32[-2147483648 2147483647]> ptr) {
                Touch(ptr, ptr);
                return;
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3030", "violates disjoint parameter contract", "left", "right", "ptr");
    }

    [Fact]
    public void DisjointParameterPrefixesRejectNonMemoryBackedTypes()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn void Touch(disjoint i32[-2147483648 2147483647] value) {
                return;
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3028", "Parameter 'value'", "disjoint", "memory-backed", "i32");
    }

    [Fact]
    public void WhereDisjointParameterCallsRejectObviousOverlappingArguments()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn void Touch(rawmutptr<i32[-2147483648 2147483647]> left, rawmutptr<i32[-2147483648 2147483647]> right)
                where disjoint(left, right) {
                return;
            }

            unsafe fn void Run(rawmutptr<i32[-2147483648 2147483647]> ptr) {
                Touch(ptr, (ptr));
                return;
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3030", "violates disjoint parameter contract", "left", "right", "ptr");
    }

    [Fact]
    public void WhereDisjointContractsRejectNonMemoryBackedParameters()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn void Touch(i32[-2147483648 2147483647] value, rawmutptr<i32[-2147483648 2147483647]> ptr)
                where disjoint(value, ptr) {
                return;
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3029", "parameter 'value'", "non-memory-backed", "i32");
    }

    [Fact]
    public void UnsafeFunctionDisjointParameterCallsAllowProgrammerProvenRawPointerArguments()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn void Touch(disjoint rawmutptr<i32[-2147483648 2147483647]> left, disjoint rawmutptr<i32[-2147483648 2147483647]> right) {
                return;
            }

            unsafe fn void Run(rawmutptr<i32[-2147483648 2147483647]> maybeLeft, rawmutptr<i32[-2147483648 2147483647]> maybeRight) {
                Touch(maybeLeft, maybeRight);
                return;
            }
            """);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }

    [Fact]
    public void UnsafeFunctionDisjointParameterCallsAllowProgrammerProvenUnrootedArguments()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn rawmutptr<i32[-2147483648 2147483647]> Identity(rawmutptr<i32[-2147483648 2147483647]> ptr) {
                return ptr;
            }

            unsafe fn void Touch(disjoint rawmutptr<i32[-2147483648 2147483647]> left, disjoint rawmutptr<i32[-2147483648 2147483647]> right) {
                return;
            }

            unsafe fn void Run(rawmutptr<i32[-2147483648 2147483647]> maybeLeft, rawmutptr<i32[-2147483648 2147483647]> maybeRight) {
                Touch(Identity(maybeLeft), maybeRight);
                return;
            }
            """);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }

    [Fact]
    public void UnsafeDisjointParameterCallsAllowProgrammerProvenRawPointerArguments()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn void Touch(disjoint rawmutptr<i32[-2147483648 2147483647]> left, disjoint rawmutptr<i32[-2147483648 2147483647]> right) {
                return;
            }

            unsafe fn void Run(rawmutptr<i32[-2147483648 2147483647]> maybeLeft, rawmutptr<i32[-2147483648 2147483647]> maybeRight) {
                unsafe {
                    Touch(maybeLeft, maybeRight);
                }

                return;
            }
            """);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }

    [Fact]
    public void UnsafeDisjointParameterCallsAllowProgrammerProvenUnrootedArguments()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn rawmutptr<i32[-2147483648 2147483647]> Identity(rawmutptr<i32[-2147483648 2147483647]> ptr) {
                return ptr;
            }

            unsafe fn void Touch(disjoint rawmutptr<i32[-2147483648 2147483647]> left, disjoint rawmutptr<i32[-2147483648 2147483647]> right) {
                return;
            }

            unsafe fn void Run(rawmutptr<i32[-2147483648 2147483647]> maybeLeft, rawmutptr<i32[-2147483648 2147483647]> maybeRight) {
                unsafe {
                    Touch(Identity(maybeLeft), Identity(maybeRight));
                }

                return;
            }
            """);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }

    [Fact]
    public void DisjointParameterFactsAllowForwardingToDisjointCallee()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn void Touch(disjoint rawmutptr<i32[-2147483648 2147483647]> left, disjoint rawmutptr<i32[-2147483648 2147483647]> right) {
                return;
            }

            unsafe fn void Run(rawmutptr<i32[-2147483648 2147483647]> left, rawmutptr<i32[-2147483648 2147483647]> right)
                where disjoint(left, right) {
                Touch(left, right);
                return;
            }
            """);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }

    [Fact]
    public void DisjointParameterCallsAllowDistinctMutableBorrowParameters()
    {
        var result = Compile(
            """
            module Demo

            struct Box {
                i32[-2147483648 2147483647] Value;
            }

            unsafe fn void Touch(disjoint borrow mut Box left, disjoint borrow mut Box right) {
                left.Value = 1;
                right.Value = 2;
                return;
            }

            unsafe fn void Run(borrow mut Box left, borrow mut Box right) {
                Touch(left, right);
                return;
            }
            """);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }

    [Fact]
    public void DisjointParameterCallsAllowDistinctOutParameterRoots()
    {
        var result = Compile(
            """
            module Demo

            struct Box {
                i32[-2147483648 2147483647] Value;
            }

            unsafe fn void Touch(disjoint out Box left, disjoint out Box right) {
                left = new Box() { Value = 1 };
                right = new Box() { Value = 2 };
                return;
            }

            unsafe fn void Run(out Box left, out Box right) {
                Touch(left, right);
                return;
            }
            """);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }

    [Fact]
    public void DisjointParameterCallsRejectRepeatedOutParameterRoots()
    {
        var result = Compile(
            """
            module Demo

            struct Box {
                i32[-2147483648 2147483647] Value;
            }

            unsafe fn void Touch(disjoint out Box left, disjoint out Box right) {
                left = new Box() { Value = 1 };
                right = new Box() { Value = 2 };
                return;
            }

            unsafe fn void Run(out Box value) {
                Touch(value, value);
                return;
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3030", "violates disjoint parameter contract", "left", "right", "value");
    }

    [Fact]
    public void DisjointParameterCallsAllowImmutableSliceViewsFromDistinctLocalArrays()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn void Touch(disjoint borrow i32[-2147483648 2147483647][] left, disjoint borrow i32[-2147483648 2147483647][] right) {
                return;
            }

            unsafe fn void Run() {
                stack i32[-2147483648 2147483647][2] leftValues = { 1, 2 };
                stack i32[-2147483648 2147483647][2] rightValues = { 3, 4 };
                stack i32[-2147483648 2147483647][] leftView = leftValues;
                stack i32[-2147483648 2147483647][] rightView = rightValues;
                Touch(leftView, rightView);
                return;
            }
            """);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }

    [Fact]
    public void DisjointParameterCallsRejectSliceViewAndBackingArrayOverlap()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn void Touch(disjoint borrow i32[-2147483648 2147483647][] left, disjoint borrow i32[-2147483648 2147483647][] right) {
                return;
            }

            unsafe fn void Run() {
                stack i32[-2147483648 2147483647][2] values = { 1, 2 };
                stack i32[-2147483648 2147483647][] view = values;
                Touch(view, values);
                return;
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3030", "violates disjoint parameter contract", "left", "right", "values");
    }

    [Fact]
    public void DisjointParameterCallsAllowNonOverlappingTextSliceRanges()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn void Touch(disjoint ascii left, disjoint ascii right) {
                return;
            }

            unsafe fn void Run(ascii text) {
                stack ascii left = text[0, 2];
                stack ascii right = text[2, 2];
                Touch(left, right);
                return;
            }
            """);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }

    [Fact]
    public void DisjointParameterCallsAllowNonOverlappingDynamicTextSliceRanges()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn void Touch(disjoint ascii left, disjoint ascii right) {
                return;
            }

            unsafe fn void Run(ascii text, i32[0 1] leftStart, i32[4 5] rightStart) {
                stack ascii left = text[leftStart, 2];
                stack ascii right = text[rightStart, 2];
                Touch(left, right);
                return;
            }
            """);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }

    [Fact]
    public void DisjointParameterCallsRejectOverlappingTextSliceRanges()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn void Touch(disjoint ascii left, disjoint ascii right) {
                return;
            }

            unsafe fn void Run(ascii text) {
                Touch(text[0, 3], text[2, 1]);
                return;
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3030", "violates disjoint parameter contract", "left", "right", "text");
    }

    [Fact]
    public void DisjointParameterCallsRejectOverlappingDynamicTextSliceRanges()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn void Touch(disjoint ascii left, disjoint ascii right) {
                return;
            }

            unsafe fn void Run(ascii text, i32[0 2] leftStart, i32[4 5] rightStart) {
                stack ascii left = text[leftStart, 3];
                stack ascii right = text[rightStart, 1];
                Touch(left, right);
                return;
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3030", "violates disjoint parameter contract", "left", "right", "text");
    }

    [Fact]
    public void DisjointParameterCallsRejectDistinctReadonlyBorrowParametersWithoutProof()
    {
        var result = Compile(
            """
            module Demo

            struct Box {
                i32[-2147483648 2147483647] Value;
            }

            fn void Touch(disjoint borrow Box left, disjoint borrow Box right) {
                return;
            }

            fn void Run(borrow Box maybeLeft, borrow Box maybeRight) {
                Touch(maybeLeft, maybeRight);
                return;
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3030", "violates disjoint parameter contract", "left", "right", "maybeLeft", "maybeRight");
    }

    [Fact]
    public void DisjointMethodCallsValidateReceiverContracts()
    {
        var result = Compile(
            """
            module Demo

            struct Box {
                i32[-2147483648 2147483647] Value;

                fn void Touch(disjoint borrow mut Box self, disjoint borrow mut Box other) {
                    self.Value = 1;
                    other.Value = 2;
                    return;
                }
            }

            fn void Run(borrow mut Box left, borrow mut Box right) {
                left.Touch(right);
                return;
            }
            """);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }

    [Fact]
    public void DisjointMethodCallsRejectUnprovenReceiverContracts()
    {
        var result = Compile(
            """
            module Demo

            struct Box {
                i32[-2147483648 2147483647] Value;

                fn void Touch(disjoint borrow Box self, disjoint borrow Box other) {
                    return;
                }
            }

            fn void Run(borrow Box maybeLeft, borrow Box maybeRight) {
                maybeLeft.Touch(maybeRight);
                return;
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3030", "violates disjoint parameter contract", "self", "other", "maybeRight");
    }

    [Fact]
    public void DisjointParameterCallsAllowDistinctAddressedLocalStorage()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn void Touch(disjoint rawmutptr<i32[-2147483648 2147483647]> left, disjoint rawmutptr<i32[-2147483648 2147483647]> right) {
                return;
            }

            unsafe fn void Run() {
                stack mut i32[-2147483648 2147483647] left = 1;
                stack mut i32[-2147483648 2147483647] right = 2;
                Touch(&left, &right);
                return;
            }
            """);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }

    [Fact]
    public void DisjointParameterCallsRejectAncestorProjectionArguments()
    {
        var result = Compile(
            """
            module Demo

            struct Pair {
                i32[-2147483648 2147483647] Left;
                i32[-2147483648 2147483647] Right;
            }

            unsafe fn void Touch(disjoint rawmutptr<Pair> whole, disjoint rawmutptr<i32[-2147483648 2147483647]> field) {
                return;
            }

            unsafe fn void Run(borrow mut Pair pair) {
                Touch(&pair, &pair.Left);
                return;
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3030", "violates disjoint parameter contract", "whole", "field", "pair");
    }

    [Fact]
    public void DisjointParameterCallsAllowDistinctProjectionArguments()
    {
        var result = Compile(
            """
            module Demo

            struct Pair {
                i32[-2147483648 2147483647] Left;
                i32[-2147483648 2147483647] Right;
            }

            unsafe fn void Touch(disjoint rawmutptr<i32[-2147483648 2147483647]> left, disjoint rawmutptr<i32[-2147483648 2147483647]> right) {
                return;
            }

            unsafe fn void Run(borrow mut Pair pair) {
                Touch(&pair.Left, &pair.Right);
                return;
            }
            """);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }

    [Fact]
    public void DisjointParameterCallsRejectUnprovenIndexedArguments()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn void Touch(disjoint rawmutptr<i32[-2147483648 2147483647]> left, disjoint rawmutptr<i32[-2147483648 2147483647]> right) {
                return;
            }

            unsafe fn void Run(i32[0 2] leftIndex, i32[0 2] rightIndex) {
                stack mut i32[-2147483648 2147483647][3] values = { 1, 2, 3 };
                Touch(&values[leftIndex], &values[rightIndex]);
                return;
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3030", "violates disjoint parameter contract", "left", "right", "values");
    }

    [Fact]
    public void DisjointParameterCallsAllowDistinctConstantIndexedArguments()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn void Touch(disjoint rawmutptr<i32[-2147483648 2147483647]> left, disjoint rawmutptr<i32[-2147483648 2147483647]> right) {
                return;
            }

            unsafe fn void Run() {
                stack mut i32[-2147483648 2147483647][3] values = { 1, 2, 3 };
                Touch(&values[0], &values[1]);
                return;
            }
            """);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }

    [Fact]
    public void DisjointParameterCallsAllowNonOverlappingIndexRangeArguments()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn void Touch(disjoint rawmutptr<i32[-2147483648 2147483647]> left, disjoint rawmutptr<i32[-2147483648 2147483647]> right) {
                return;
            }

            unsafe fn void Run(i32[0 1] leftIndex, i32[2 3] rightIndex) {
                stack mut i32[-2147483648 2147483647][4] values = { 1, 2, 3, 4 };
                Touch(&values[leftIndex], &values[rightIndex]);
                return;
            }
            """);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }

    [Fact]
    public void DisjointParameterCallsRejectOverlappingIndexRangeArguments()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn void Touch(disjoint rawmutptr<i32[-2147483648 2147483647]> left, disjoint rawmutptr<i32[-2147483648 2147483647]> right) {
                return;
            }

            unsafe fn void Run(i32[0 2] leftIndex, i32[2 3] rightIndex) {
                stack mut i32[-2147483648 2147483647][4] values = { 1, 2, 3, 4 };
                Touch(&values[leftIndex], &values[rightIndex]);
                return;
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3030", "violates disjoint parameter contract", "left", "right", "values");
    }

    [Fact]
    public void RuntimeDisjointTrueBranchSatisfiesDisjointCallContract()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn void Touch(disjoint rawmutptr<i32[-2147483648 2147483647]> left, disjoint rawmutptr<i32[-2147483648 2147483647]> right) {
                return;
            }

            unsafe fn void Run(i32[0 2] leftIndex, i32[0 2] rightIndex) {
                stack mut i32[-2147483648 2147483647][3] values = { 1, 2, 3 };
                if disjoint(&values[leftIndex], &values[rightIndex]) {
                    Touch(&values[leftIndex], &values[rightIndex]);
                }

                return;
            }
            """);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }

    [Fact]
    public void RuntimeDisjointTrueBranchUsesTextSliceLocalBackingRoots()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn void Touch(disjoint ascii left, disjoint ascii right) {
                return;
            }

            unsafe fn void Run(ascii text, i32[0 3] leftStart, i32[0 3] rightStart) {
                stack ascii left = text[leftStart, 2];
                stack ascii right = text[rightStart, 2];
                if disjoint(left, right) {
                    Touch(left, right);
                }

                return;
            }
            """);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }

    [Fact]
    public void RuntimeDisjointTrueBranchUsesRawSliceLocalBackingRoots()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn void Touch(
                disjoint borrow mut i32[-2147483648 2147483647][] left,
                disjoint borrow i32[-2147483648 2147483647][] right) {
                return;
            }

            unsafe fn void Run(
                rawmutptr<i32[-2147483648 2147483647]>[count] left,
                rawptr<i32[-2147483648 2147483647]>[count] right,
                i32[1 10] count) {
                if disjoint(left[0, count], right[0, count]) {
                    unsafe {
                        stack mut mut i32[-2147483648 2147483647][] leftView = slice(left, count);
                        stack i32[-2147483648 2147483647][] rightView = slice(right, count);
                        Touch(leftView, rightView);
                    }
                }

                return;
            }
            """);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }

    [Fact]
    public void RuntimeDisjointTrueBranchCoversDescendantRegions()
    {
        var result = Compile(
            """
            module Demo

            struct Cell {
                i32[-2147483648 2147483647] Value;
            }

            unsafe fn void Touch(disjoint rawmutptr<i32[-2147483648 2147483647]> left, disjoint rawmutptr<i32[-2147483648 2147483647]> right) {
                return;
            }

            unsafe fn void Run(i32[0 2] leftIndex, i32[0 2] rightIndex) {
                stack mut Cell[3] cells = {
                    new Cell() { Value = 1 },
                    new Cell() { Value = 2 },
                    new Cell() { Value = 3 },
                };
                if disjoint(&cells[leftIndex], &cells[rightIndex]) {
                    Touch(&cells[leftIndex].Value, &cells[rightIndex].Value);
                }

                return;
            }
            """);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }

    [Fact]
    public void RuntimeDisjointFalseBranchDoesNotSatisfyDisjointCallContract()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn void Touch(disjoint rawmutptr<i32[-2147483648 2147483647]> left, disjoint rawmutptr<i32[-2147483648 2147483647]> right) {
                return;
            }

            unsafe fn void Run(i32[0 2] leftIndex, i32[0 2] rightIndex) {
                stack mut i32[-2147483648 2147483647][3] values = { 1, 2, 3 };
                if disjoint(&values[leftIndex], &values[rightIndex]) {
                    return;
                } else {
                    Touch(&values[leftIndex], &values[rightIndex]);
                }

                return;
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3030", "violates disjoint parameter contract", "left", "right", "values");
    }

    [Fact]
    public void IndependentScalarOnlyLoopContractsCompile()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn i32[0 10] WhileRun() {
                stack mut i32[0 10] value = 0;
                while willexit independent (value < 4) {
                    const bool shouldStep = true;
                    stack mut i32[0 10] next = value;
                    if (shouldStep) {
                        next += 1;
                    }

                    value = next;
                }

                return value;
            }

            unsafe fn i32[0 10] ForRun() {
                stack mut i32[0 10] value = 0;
                for willexit independent (stack mut i32[0 10] index = 0; index < 4; index += 1) {
                    register i32[0 10] copy = index;
                    value += copy;
                }

                return value;
            }
            """);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }

    [Fact]
    public void IndependentSliceLoopsCompileWithDisjointParameterFacts()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn void Add(
                disjoint borrow i32[-2147483648 2147483647][] left,
                disjoint borrow i32[-2147483648 2147483647][] right,
                disjoint borrow mut i32[-2147483648 2147483647][] output,
                i32[0 10] count) {
                for willexit independent (stack mut i32[0 10] index = 0; index < count; index += 1) {
                    output[index] = left[index] + right[index];
                }

                return;
            }
            """);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }

    [Fact]
    public void IndependentSliceLoopsCompileWithConditionalMemoryBodies()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn void SelectPositive(
                disjoint borrow i32[-2147483648 2147483647][] left,
                disjoint borrow i32[-2147483648 2147483647][] right,
                disjoint borrow mut i32[-2147483648 2147483647][] output,
                i32[0 10] count) {
                for willexit independent (stack mut i32[0 10] index = 0; index < count; index += 1) {
                    if (left[index] > 0) {
                        output[index] = left[index];
                    } else {
                        output[index] = right[index];
                    }
                }

                return;
            }
            """);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }

    [Fact]
    public void IndependentSliceLoopsCompileWithMemberProjectedMemoryAccesses()
    {
        var result = Compile(
            """
            module Demo

            struct Cell {
                i32[-2147483648 2147483647] Value;
            }

            unsafe fn void Copy(
                disjoint borrow Cell[] input,
                disjoint borrow mut Cell[] output,
                i32[0 10] count) {
                for willexit independent (stack mut i32[0 10] index = 0; index < count; index += 1) {
                    output[index].Value = input[index].Value;
                }

                return;
            }
            """);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }

    [Fact]
    public void IndependentBoundedRawPointerLoopsCompileWithRegionContracts()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn void Copy(
                disjoint rawptr<i32[-2147483648 2147483647]>[count] input,
                disjoint rawmutptr<i32[-2147483648 2147483647]>[count] output,
                i32[0 10] count)
                where disjoint(input[0, count], output[0, count]) {
                for willexit independent (stack mut i32[0 10] index = 0; index < count; index += 1) {
                    *(&output[index]) = *(&input[index]);
                }

                return;
            }
            """);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }

    [Fact]
    public void IndependentBoundedRawPointerLoopsCompileWithRuntimeRegionFacts()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn void Copy(
                rawptr<i32[-2147483648 2147483647]>[count] input,
                rawmutptr<i32[-2147483648 2147483647]>[count] output,
                i32[0 10] count) {
                if disjoint(input[0, count], output[0, count]) {
                    for willexit independent (stack mut i32[0 10] index = 0; index < count; index += 1) {
                        *(&output[index]) = *(&input[index]);
                    }
                }

                return;
            }
            """);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }

    [Fact]
    public void IndependentBoundedRawPointerLoopsRejectUnprovenInductionBounds()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn void Copy(
                rawptr<i32[-2147483648 2147483647]>[count] input,
                rawmutptr<i32[-2147483648 2147483647]>[count] output,
                i32[0 10] count,
                i32[0 20] limit)
                where disjoint(input[0, count], output[0, count]) {
                for willexit independent (stack mut i32[0 20] index = 0; index < limit; index += 1) {
                    *(&output[index]) = *(&input[index]);
                }

                return;
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3027", "bounded raw pointer access root", "not proven in range");
    }

    [Fact]
    public void BoundedRawPointerCallsRejectNullWhenCountIsPositive()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn void Read(rawptr<i32[-2147483648 2147483647]>[4] input) {
                return;
            }

            unsafe fn void Run() {
                Read(null);
                return;
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3029", "passes null", "element count is provably positive");
    }

    [Fact]
    public void BoundedRawPointerCallsAllowNullWhenCountIsZero()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn void Read(rawptr<i32[-2147483648 2147483647]>[0] input) {
                return;
            }

            unsafe fn void Run() {
                Read(null);
                return;
            }
            """);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }

    [Fact]
    public void BoundedRawPointerCallsAllowFixedArrayElementWhenStorageCoversCount()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn void Read(rawptr<i32[-2147483648 2147483647]>[4] input) {
                return;
            }

            unsafe fn void Run() {
                stack mut i32[-2147483648 2147483647][5] values = { 1, 2, 3, 4, 5 };
                Read(&values[1]);
                return;
            }
            """);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }

    [Fact]
    public void BoundedRawPointerCallsAllowForwardedBoundedPointerWhenCountMatches()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn void Read(rawptr<i32[-2147483648 2147483647]>[count] input, i32[0 10] count) {
                return;
            }

            unsafe fn void Forward(rawptr<i32[-2147483648 2147483647]>[count] input, i32[0 10] count) {
                Read(input, count);
                return;
            }
            """);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }

    [Fact]
    public void BoundedRawPointerCallsAllowImmutablePointerLocalWithProvenance()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn void Read(rawptr<i32[-2147483648 2147483647]>[count] input, i32[0 10] count) {
                return;
            }

            unsafe fn void Forward(rawptr<i32[-2147483648 2147483647]>[count] input, i32[0 10] count) {
                stack rawptr<i32[-2147483648 2147483647]> localPointer = input;
                Read(localPointer, count);
                return;
            }
            """);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }

    [Fact]
    public void BoundedRawPointerCallsAllowForwardedSubregionsWhenStorageCoversCount()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn void Read(rawptr<i32[-2147483648 2147483647]>[4] input) {
                return;
            }

            unsafe fn void Forward(rawptr<i32[-2147483648 2147483647]>[count] input, i32[6 10] count) {
                Read(&input[2]);
                return;
            }
            """);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }

    [Fact]
    public void BoundedRawPointerCallsAllowSliceDerivedRawRegionsWhenStorageCoversCount()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn void Read(rawptr<i32[-2147483648 2147483647]>[4] input) {
                return;
            }

            unsafe fn void Forward(rawptr<i32[-2147483648 2147483647]>[count] input, i32[5 10] count) {
                unsafe {
                    stack i32[-2147483648 2147483647][] view = slice(input, count);
                    Read(&view[1]);
                }

                return;
            }
            """);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }

    [Fact]
    public void BoundedRawPointerCallsRejectFixedArrayElementWhenStorageIsTooShort()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn void Read(rawptr<i32[-2147483648 2147483647]>[4] input) {
                return;
            }

            unsafe fn void Run() {
                stack mut i32[-2147483648 2147483647][5] values = { 1, 2, 3, 4, 5 };
                Read(&values[3]);
                return;
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3029", "valid for 4 contiguous element", "only proves 2 remaining");
    }

    [Fact]
    public void RawSliceConstructionInsideUnsafeFunctionCompiles()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn void Run(rawmutptr<i32[-2147483648 2147483647]>[count] pointer, i32[0 10] count) {
                stack mut mut i32[-2147483648 2147483647][] view = slice(pointer, count);
                view[0] = 1;
                return;
            }
            """);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }

    [Fact]
    public void UnsafeRawSliceConstructionCompiles()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn i32[-2147483648 2147483647] Read(rawptr<i32[-2147483648 2147483647]>[count] pointer, i32[0 10] count) {
                unsafe {
                    return slice(pointer, count)[0];
                }

                return 0;
            }
            """);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }

    [Fact]
    public void BoundedRawPointerParametersRejectPossiblyNegativeCounts()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn void Run(rawptr<i32[-2147483648 2147483647]>[count] pointer, i32[-1 10] count) {
                return;
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3014", "count 'count'", "provably non-negative");
    }

    [Fact]
    public void RuntimeRawPointerRegionChecksRejectPossiblyNegativeBounds()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn bool Check(
                rawptr<i32[-2147483648 2147483647]>[count] left,
                rawptr<i32[-2147483648 2147483647]>[count] right,
                i32[-1 10] start,
                i32[0 10] count) {
                if disjoint(left[start, count], right[0, count]) {
                    return true;
                }

                return false;
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3025", "start", "provably non-negative");
    }

    [Fact]
    public void RawSliceConstructionRejectsPossiblyNegativeCounts()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn void Run(rawptr<i32[-2147483648 2147483647]> pointer, i32[-1 10] count) {
                unsafe {
                    stack i32[-2147483648 2147483647][] view = slice(pointer, count);
                }

                return;
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3002", "Raw slice construction count", "provably non-negative");
    }

    [Fact]
    public void RawSliceConstructionRejectsNullWhenCountIsPositive()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn void Run() {
                unsafe {
                    stack i32[-2147483648 2147483647][] view = slice((rawptr<i32[-2147483648 2147483647]>)null, 1);
                }

                return;
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3029", "Raw slice construction", "null", "provably positive");
    }

    [Fact]
    public void RawSliceConstructionAllowsNullWhenCountIsZero()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn void Run() {
                unsafe {
                    stack i32[-2147483648 2147483647][] view = slice((rawptr<i32[-2147483648 2147483647]>)null, 0);
                }

                return;
            }
            """);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }

    [Fact]
    public void RawSliceConstructionRejectsHiddenPointerRoots()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn rawptr<i32[-2147483648 2147483647]> Identity(rawptr<i32[-2147483648 2147483647]> pointer) {
                return pointer;
            }

            unsafe fn void Run(rawptr<i32[-2147483648 2147483647]> pointer, i32[0 10] count) {
                unsafe {
                    stack i32[-2147483648 2147483647][] view = slice(Identity(pointer), count);
                }

                return;
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3029", "Raw slice construction", "compiler-visible raw pointer root", "hidden-root");
    }

    [Fact]
    public void RawSliceConstructionRejectsMutableViewStrengthening()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn void Run(rawptr<i32[-2147483648 2147483647]>[count] pointer, i32[0 10] count) {
                unsafe {
                    stack mut mut i32[-2147483648 2147483647][] view = slice(pointer, count);
                }

                return;
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3002", "Assignment expects", "mut i32", "found 'i32");
    }

    [Fact]
    public void IndependentSliceLoopsAllowScalarLawCallsAfterValidatedMemoryReads()
    {
        var result = Compile(
            """
            module Demo

            unsafe finite law i32[-2147483648 2147483647] Scale(i32[-2147483648 2147483647] value) {
                return value * 2;
            }

            unsafe fn void ScaleAll(
                disjoint borrow i32[-2147483648 2147483647][] input,
                disjoint borrow mut i32[-2147483648 2147483647][] output,
                i32[0 10] count) {
                for willexit independent (stack mut i32[0 10] index = 0; index < count; index += 1) {
                    output[index] = Scale(input[index]);
                }

                return;
            }
            """);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }

    [Fact]
    public void IndependentSliceLoopsRejectUnprovenWrittenReadRoots()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn void Add(
                borrow i32[-2147483648 2147483647][] input,
                borrow mut i32[-2147483648 2147483647][] output,
                i32[0 10] count) {
                for willexit independent (stack mut i32[0 10] index = 0; index < count; index += 1) {
                    output[index] = input[index];
                }

                return;
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3027", "Loop 'independent' contracts", "not proven disjoint");
    }

    [Fact]
    public void IndependentSliceLoopsRejectNonInductionIndexes()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn void Copy(
                disjoint borrow i32[-2147483648 2147483647][] input,
                disjoint borrow mut i32[-2147483648 2147483647][] output,
                i32[0 10] count,
                i32[0 10] other) {
                for willexit independent (stack mut i32[0 10] index = 0; index < count; index += 1) {
                    output[index] = input[other];
                }

                return;
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3027", "Loop 'independent' contracts", "induction variable");
    }

    [Fact]
    public void IndependentSliceLoopsRejectAssignmentsToInductionVariable()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn void Copy(
                disjoint borrow i32[-2147483648 2147483647][] input,
                disjoint borrow mut i32[-2147483648 2147483647][] output,
                i32[0 10] count) {
                for willexit independent (stack mut i32[0 10] index = 0; index < count; index += 1) {
                    index += 1;
                    output[index] = input[index];
                }

                return;
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3027", "Loop 'independent' contracts", "induction variable");
    }

    [Fact]
    public void IndependentSliceLoopsRejectCallsWithUnprovenMemoryEffects()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn i32[-2147483648 2147483647] Scale(i32[-2147483648 2147483647] value) {
                return value * 2;
            }

            unsafe fn void ScaleAll(
                disjoint borrow i32[-2147483648 2147483647][] input,
                disjoint borrow mut i32[-2147483648 2147483647][] output,
                i32[0 10] count) {
                for willexit independent (stack mut i32[0 10] index = 0; index < count; index += 1) {
                    output[index] = Scale(input[index]);
                }

                return;
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3027", "Loop 'independent' contracts", "law functions");
    }

    [Fact]
    public void IndependentLoopContractsRejectMemoryBackedLocalDeclarations()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn void Run() {
                while willexit independent (false) {
                    stack mut i32[-2147483648 2147483647][2] values = { 1, 2 };
                }

                return;
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3027", "Loop 'independent' contracts", "local declarations", "scalar local types");
    }

    [Fact]
    public void IndependentSliceLoopsRejectMemoryBackedLocalDeclarations()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn void Copy(
                disjoint borrow i32[-2147483648 2147483647][] input,
                disjoint borrow mut i32[-2147483648 2147483647][] output,
                i32[0 10] count) {
                for willexit independent (stack mut i32[0 10] index = 0; index < count; index += 1) {
                    stack mut i32[-2147483648 2147483647][2] values = { input[index], 0 };
                    output[index] = values[0];
                }

                return;
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3027", "Loop 'independent' contracts", "local declarations", "scalar local types");
    }

    [Fact]
    public void IndependentSliceLoopsRejectNestedLoops()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn void Copy(
                disjoint borrow i32[-2147483648 2147483647][] input,
                disjoint borrow mut i32[-2147483648 2147483647][] output,
                i32[0 10] count) {
                for willexit independent (stack mut i32[0 10] index = 0; index < count; index += 1) {
                    while willexit (false) {
                    }

                    output[index] = input[index];
                }

                return;
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3027", "Loop 'independent' contracts", "nested loops");
    }

    [Fact]
    public void IndependentSliceLoopsRejectEarlyExits()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn void Copy(
                disjoint borrow i32[-2147483648 2147483647][] input,
                disjoint borrow mut i32[-2147483648 2147483647][] output,
                i32[0 10] count) {
                for willexit independent (stack mut i32[0 10] index = 0; index < count; index += 1) {
                    if (input[index] == 0) {
                        return;
                    }

                    output[index] = input[index];
                }

                return;
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3027", "Loop 'independent' contracts", "early exits");
    }

    [Fact]
    public void IndependentLoopContractsRejectMemoryTouchingBodies()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn void Run(rawmutptr<i32[-2147483648 2147483647]> ptr) {
                while willexit independent (false) {
                    *ptr = 1;
                }

                return;
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3027", "Loop 'independent' contracts", "canonical memory-backed subset", "outside the accepted dependency-validation subset");
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
