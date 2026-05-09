using Stark.Compiler;

namespace compiler.Tests;

public sealed class OwnershipRoadmapRegressionTests
{
    [Fact]
    public void MovedOwnedLocalCanBeReinitializedBeforeLaterRead()
    {
        var result = Compile(
            """
            module Demo

            struct Box {
                i32[min max] Value;
            }

            fn void Consume(Box value) {
                return;
            }

            fn i32[min max] Run() {
                stack mut Box box = new Box() { Value = 1 };
                Consume(box);
                box = new Box() { Value = 2 };
                return box.Value;
            }
            """);

        Assert.True(result.Succeeded);
        var ownership = GetOwnership(result);
        Assert.True(ownership.Functions["Run"].OwnershipValid);
        Assert.Contains("box", ownership.Functions["Run"].Moves);
        Assert.Contains("box", ownership.Functions["Run"].ImplicitDrops);
    }

    [Fact]
    public void BranchReinitializationKeepsOwnedLocalAvailable()
    {
        var result = Compile(
            """
            module Demo

            struct Box {
                i32[min max] Value;
            }

            fn void Consume(Box value) {
                return;
            }

            fn i32[min max] Run() {
                stack mut Box box = new Box() { Value = 1 };

                if (true) {
                    Consume(box);
                    box = new Box() { Value = 2 };
                }

                return box.Value;
            }
            """);

        Assert.True(result.Succeeded);
        var ownership = GetOwnership(result);
        Assert.True(ownership.Functions["Run"].OwnershipValid);
    }

    [Fact]
    public void LoopReinitializationKeepsOwnedLocalAvailable()
    {
        var result = Compile(
            """
            module Demo

            struct Box {
                i32[min max] Value;
            }

            fn void Consume(Box value) {
                return;
            }

            fn i32[min max] Run() {
                stack mut Box box = new Box() { Value = 1 };
                stack mut i32[min max] count = 0;

                while willexit (count < 1) {
                    Consume(box);
                    box = new Box() { Value = 2 };
                    count = count + 1;
                }

                return box.Value;
            }
            """);

        Assert.True(result.Succeeded);
        var ownership = GetOwnership(result);
        Assert.True(ownership.Functions["Run"].OwnershipValid);
    }

    [Fact]
    public void BranchMergeRequiresReinitializationOnEveryPath()
    {
        var result = Compile(
            """
            module Demo

            struct Box {
                i32[min max] Value;
            }

            fn void Consume(Box value) {
                return;
            }

            fn i32[min max] Run() {
                stack mut Box box = new Box() { Value = 1 };

                if (true) {
                    Consume(box);
                } else {
                    box = new Box() { Value = 2 };
                }

                return box.Value;
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK4200", "Control-flow error", "not available on every path");
    }

    [Fact]
    public void EnumWithOnlyCopyPayloadsDoesNotRecordImplicitDropAtScopeExit()
    {
        var result = Compile(
            """
            module Demo

            enum Token {
                End,
                Integer(i32[min max]),
            }

            fn i32[min max] Run(bool choose) {
                stack mut Token token;

                if (choose) {
                    token = Token.End;
                } else {
                    token = Token.Integer(1);
                }

                return 0;
            }
            """);

        Assert.True(result.Succeeded);
        var ownership = GetOwnership(result);
        Assert.True(ownership.Functions["Run"].OwnershipValid);
        Assert.Empty(ownership.Functions["Run"].ImplicitDrops);
    }

    [Fact]
    public void EnumWithOnlyCopyPayloadsMayRemainUninitializedAtScopeExit()
    {
        var result = Compile(
            """
            module Demo

            enum Token {
                End,
                Integer(i32[min max]),
            }

            fn i32[min max] Run(bool choose) {
                stack mut Token token;

                if (choose) {
                    token = Token.End;
                }

                return 0;
            }
            """);

        Assert.True(result.Succeeded);
        var ownership = GetOwnership(result);
        Assert.True(ownership.Functions["Run"].OwnershipValid);
        Assert.Empty(ownership.Functions["Run"].ImplicitDrops);
    }

    [Fact]
    public void TupleEnumConstructorCallsRemainOwnedAcrossScopeExit()
    {
        var result = Compile(
            """
            module Demo

            struct Payload {
                ascii Text;
            }

            enum Token {
                End,
                Text(Payload),
            }

            fn i32[min max] Run() {
                stack Token token = Token.Text(new Payload() { Text = "hello" });
                return 0;
            }
            """);

        Assert.True(result.Succeeded);
        var ownership = GetOwnership(result);
        Assert.True(ownership.Functions["Run"].OwnershipValid);
        Assert.Contains("token.Text", ownership.Functions["Run"].ImplicitDrops);
    }

    [Fact]
    public void ConditionalEnumConstructorsOnlyDropOwnedCases()
    {
        var result = Compile(
            """
            module Demo

            struct Payload {
                ascii Text;
            }

            enum Token {
                End,
                Text(Payload),
            }

            fn i32[min max] Run(bool choose) {
                stack Token token = choose ? Token.Text(new Payload() { Text = "hello" }) : Token.End;
                return 0;
            }
            """);

        Assert.True(result.Succeeded);
        var ownership = GetOwnership(result);
        Assert.True(ownership.Functions["Run"].OwnershipValid);
        Assert.Contains("token.Text", ownership.Functions["Run"].ImplicitDrops);
        Assert.DoesNotContain("token.End", ownership.Functions["Run"].ImplicitDrops);
    }

    [Fact]
    public void EnumInitializedOnOnlyOnePathWithOwnedPayloadIsRejectedAtScopeExit()
    {
        var result = Compile(
            """
            module Demo

            struct Payload {
                ascii Text;
            }

            enum Token {
                Empty,
                Text(Payload),
            }

            fn i32[min max] Run(bool choose) {
                stack mut Token token;

                if (choose) {
                    token = Token.Text(new Payload() { Text = "hello" });
                }

                return 0;
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK4205", "Drop error", "cannot drop 'token'", "enum values must be initialized on every path");
    }

    [Fact]
    public void OwnedEnumPayloadCaptureLeavesNoDropWhenOnlyUnitCaseRemains()
    {
        var result = Compile(
            """
            module Demo

            struct Box {
                i32[min max] Value;
            }

            enum Token {
                Empty,
                Full(Box),
            }

            fn void Consume(Box box) {
                return;
            }

            fn i32[min max] Run(bool choose) {
                stack mut Token token = choose ? Token.Full(new Box() { Value = 1 }) : Token.Empty;

                switch (token) {
                    case Token.Full(var box):
                        Consume(box);
                    case Token.Empty:
                        ;
                }

                return 0;
            }
            """);

        Assert.True(result.Succeeded);
        var ownership = GetOwnership(result);
        Assert.True(ownership.Functions["Run"].OwnershipValid);
        Assert.Contains("token", ownership.Functions["Run"].Moves);
        Assert.Empty(ownership.Functions["Run"].ImplicitDrops);
    }

    [Fact]
    public void OwnedEnumPayloadCaptureCanBeReinitializedBeforeScopeExit()
    {
        var result = Compile(
            """
            module Demo

            struct Box {
                i32[min max] Value;
            }

            enum Token {
                Empty,
                Full(Box),
            }

            fn void Consume(Box box) {
                return;
            }

            fn i32[min max] Run(bool choose) {
                stack mut Token token = choose ? Token.Full(new Box() { Value = 1 }) : Token.Empty;

                switch (token) {
                    case Token.Full(var box):
                        Consume(box);
                        token = Token.Empty;
                    case Token.Empty:
                        ;
                }

                return 0;
            }
            """);

        Assert.True(result.Succeeded);
        var ownership = GetOwnership(result);
        Assert.True(ownership.Functions["Run"].OwnershipValid);
        Assert.Contains("token", ownership.Functions["Run"].Moves);
        Assert.Empty(ownership.Functions["Run"].ImplicitDrops);
    }

    [Fact]
    public void OwnedEnumPayloadCaptureCannotMoveOutOfFieldPlace()
    {
        var result = Compile(
            """
            module Demo

            struct Box {
                i32[min max] Value;
            }

            enum Token {
                Empty,
                Full(Box),
            }

            struct Wrapper {
                Token Value;
            }

            fn void Consume(Box box) {
                return;
            }

            fn i32[min max] Run() {
                stack Wrapper wrapper = new Wrapper() { Value = Token.Full(new Box() { Value = 1 }) };

                switch (wrapper.Value) {
                    case Token.Full(var box):
                        Consume(box);
                    case Token.Empty:
                        ;
                }

                return 0;
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK4203", "Cannot move out of field or indexed place of type 'Token'");
    }

    [Fact]
    public void ReassigningOwnedEnumDropsOnlyThePreviousOwnedCase()
    {
        var result = Compile(
            """
            module Demo

            struct Payload {
                ascii Text;
            }

            enum Token {
                End,
                Text(Payload),
            }

            fn i32[min max] Run() {
                stack mut Token token = Token.Text(new Payload() { Text = "hello" });
                token = Token.End;
                return 0;
            }
            """);

        Assert.True(result.Succeeded);
        var ownership = GetOwnership(result);
        Assert.True(ownership.Functions["Run"].OwnershipValid);
        Assert.Contains("token.Text", ownership.Functions["Run"].ImplicitDrops);
        Assert.DoesNotContain("token.End", ownership.Functions["Run"].ImplicitDrops);
    }

    [Fact]
    public void SwitchingOnEnumParameterNarrowsActiveCaseForDropAnalysis()
    {
        var result = Compile(
            """
            module Demo

            struct Payload {
                ascii Text;
            }

            enum Token {
                Empty,
                Text(Payload),
            }

            fn void Consume(Payload text) {
                return;
            }

            fn i32[min max] Run(Token token) {
                switch (token) {
                    case Token.Text(var text):
                        Consume(text);
                    case Token.Empty:
                        ;
                }

                return 0;
            }
            """);

        Assert.True(result.Succeeded);
        var ownership = GetOwnership(result);
        Assert.True(ownership.Functions["Run"].OwnershipValid);
        Assert.Contains("token", ownership.Functions["Run"].Moves);
        Assert.Empty(ownership.Functions["Run"].ImplicitDrops);
    }

    [Fact]
    public void UninitializedOwnedLocalIsNotDroppedAtScopeExit()
    {
        var result = Compile(
            """
            module Demo

            struct Box {
                i32[min max] Value;
            }

            fn i32[min max] Run() {
                stack Box box;
                return 1;
            }
            """);

        Assert.True(result.Succeeded);
        var ownership = GetOwnership(result);
        Assert.DoesNotContain("box", ownership.Functions["Run"].ImplicitDrops);
    }

    [Fact]
    public void ReadingUninitializedOwnedLocalProducesInitializationDiagnostic()
    {
        var result = Compile(
            """
            module Demo

            struct Box {
                i32[min max] Value;
            }

            fn i32[min max] Run() {
                stack Box box;
                return box.Value;
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK4205", "Initialization error", "field 'Value' of 'box' is not initialized yet");
    }

    [Fact]
    public void FieldAssignmentsCanFullyInitializeAnAggregateLocal()
    {
        var result = Compile(
            """
            module Demo

            struct Pair {
                i32[min max] Left;
                i32[min max] Right;
            }

            fn i32[min max] Run() {
                stack mut Pair pair;
                pair.Left = 1;
                pair.Right = 2;
                return pair.Left + pair.Right;
            }
            """);

        Assert.True(result.Succeeded);
        var ownership = GetOwnership(result);
        Assert.True(ownership.Functions["Run"].OwnershipValid);
    }

    [Fact]
    public void PartiallyInitializedAggregateCannotBeConsumedAsAWholeValue()
    {
        var result = Compile(
            """
            module Demo

            struct Pair {
                i32[min max] Left;
                i32[min max] Right;
            }

            fn void Consume(Pair value) {
                return;
            }

            fn i32[min max] Run() {
                stack mut Pair pair;
                pair.Left = 1;
                Consume(pair);
                return 0;
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK4205", "value 'pair' is not fully initialized", "Missing fields: Right");
    }

    [Fact]
    public void PartiallyInitializedAggregateIsRejectedAtScopeExit()
    {
        var result = Compile(
            """
            module Demo

            struct Pair {
                i32[min max] Left;
                i32[min max] Right;
            }

            fn i32[min max] Run() {
                stack mut Pair pair;
                pair.Left = 1;
                return 0;
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK4205", "Drop error", "cannot drop 'pair'", "Missing fields: Right");
    }

    [Fact]
    public void WholeAggregateUseAfterFieldMoveReportsPartialMove()
    {
        var result = Compile(
            """
            module Demo

            struct Payload {
                ascii Text;
            }

            struct Pair {
                Payload Left;
                Payload Right;
            }

            fn void Consume(Pair value) {
                return;
            }

            fn i32[min max] Run() {
                stack mut Pair pair = new Pair() { Left = new Payload() { Text = "a" }, Right = new Payload() { Text = "b" } };
                stack Payload left = pair.Left;
                Consume(pair);
                return 0;
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK4200", "Move error", "value 'pair' is partially moved", "Left (moved)");
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "STK4200"
                && diagnostic.Severity == DiagnosticSeverity.Info
                && diagnostic.Message.Contains("Field 'Left' of 'pair' was moved here.", StringComparison.Ordinal));
    }

    [Fact]
    public void ReinitializingMovedFieldRestoresWholeAggregateAvailability()
    {
        var result = Compile(
            """
            module Demo

            struct Payload {
                ascii Text;
            }

            struct Pair {
                Payload Left;
                Payload Right;
            }

            fn void Consume(Pair value) {
                return;
            }

            fn i32[min max] Run() {
                stack mut Pair pair = new Pair() { Left = new Payload() { Text = "a" }, Right = new Payload() { Text = "b" } };
                stack Payload left = pair.Left;
                pair.Left = new Payload() { Text = "c" };
                Consume(pair);
                return 0;
            }
            """);

        Assert.True(result.Succeeded);
        var ownership = GetOwnership(result);
        Assert.True(ownership.Functions["Run"].OwnershipValid);
    }

    [Fact]
    public void BranchMergesPartialFieldMoveStateAcrossPaths()
    {
        var result = Compile(
            """
            module Demo

            struct Payload {
                ascii Text;
            }

            struct Pair {
                Payload Left;
                Payload Right;
            }

            fn i32[min max] Run(bool choose) {
                stack mut Pair pair = new Pair() { Left = new Payload() { Text = "a" }, Right = new Payload() { Text = "b" } };

                if (choose) {
                    stack Payload left = pair.Left;
                }

                stack Payload current = pair.Left;
                return 0;
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK4200", "Control-flow error", "field 'Left' of 'pair' is not available on every path");
    }

    [Fact]
    public void BranchReinitializationAfterFieldMoveKeepsFieldAvailable()
    {
        var result = Compile(
            """
            module Demo

            struct Payload {
                ascii Text;
            }

            struct Pair {
                Payload Left;
                Payload Right;
            }

            fn i32[min max] Run(bool choose) {
                stack mut Pair pair = new Pair() { Left = new Payload() { Text = "a" }, Right = new Payload() { Text = "b" } };

                if (choose) {
                    stack Payload left = pair.Left;
                    pair.Left = new Payload() { Text = "c" };
                }

                stack Payload current = pair.Left;
                return 0;
            }
            """);

        Assert.True(result.Succeeded);
        var ownership = GetOwnership(result);
        Assert.True(ownership.Functions["Run"].OwnershipValid);
    }

    [Fact]
    public void BranchesMergeDefiniteAggregateFieldInitialization()
    {
        var result = Compile(
            """
            module Demo

            struct Pair {
                i32[min max] Left;
                i32[min max] Right;
            }

            fn i32[min max] Run(bool choose) {
                stack mut Pair pair;

                if (choose) {
                    pair.Left = 1;
                } else {
                    pair.Left = 2;
                }

                pair.Right = 3;
                return pair.Left + pair.Right;
            }
            """);

        Assert.True(result.Succeeded);
        var ownership = GetOwnership(result);
        Assert.True(ownership.Functions["Run"].OwnershipValid);
    }

    [Fact]
    public void BranchesReportFieldAvailabilityWhenOnlySomePathsInitializeIt()
    {
        var result = Compile(
            """
            module Demo

            struct Pair {
                i32[min max] Left;
                i32[min max] Right;
            }

            fn i32[min max] Run(bool choose) {
                stack mut Pair pair;

                if (choose) {
                    pair.Left = 1;
                } else {
                    pair.Right = 2;
                }

                return pair.Left;
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK4200", "Control-flow error", "field 'Left' of 'pair' is not available on every path");
    }

    [Fact]
    public void ReturningBorrowFromUnknownSourceReportsLifetimeDiagnostic()
    {
        var result = Compile(
            """
            module Demo

            fn retborrow i32[min max] Source();

            fn retborrow i32[min max] Leak() {
                return Source();
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK4202", "Lifetime error", "unknown source lifetime", "could not be proven");
    }

    [Fact]
    public void ReturningBorrowFromBranchSpecificCallsStillReportsLifetimeDiagnostic()
    {
        var result = Compile(
            """
            module Demo

            fn retborrow i32[min max] Source();
            fn retborrow i32[min max] Other();

            fn retborrow i32[min max] Leak(bool choose) {
                if (choose) {
                    return Source();
                }

                return Other();
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK4202", "Lifetime error", "could not be proven");
    }

    [Fact]
    public void AssigningBorrowFromUnknownSourceReportsDestinationLifetime()
    {
        var result = Compile(
            """
            module Demo

            fn retborrow i32[min max] Source();

            fn void Run() {
                stack mut retborrow i32[min max] borrowAlias = Source();
                borrowAlias = Source();

                return;
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3002", "Assignment expects 'retborrow i32'", "found 'i32'");
    }

    [Fact]
    public void AssigningBorrowFromInnerScopeToOuterScopeReportsEscapeDiagnostic()
    {
        var result = Compile(
            """
            module Demo

            fn retborrow i32[min max] Source();

            fn void Run() {
                stack mut retborrow i32[min max] outer;

                {
                    stack retborrow i32[min max] innerAlias = Source();
                    outer = innerAlias;
                }

                return;
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3002", "Assignment expects 'retborrow i32'", "found 'i32'");
    }

    [Fact]
    public void ReturningBorrowFromInnerScopeReportsEscapeDiagnostic()
    {
        var result = Compile(
            """
            module Demo

            fn retborrow i32[min max] Source();

            fn retborrow i32[min max] Run() {
                {
                    stack retborrow i32[min max] borrowAlias = Source();
                    return borrowAlias;
                }
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3002", "Assignment expects 'retborrow i32'", "found 'i32'");
    }

    [Fact]
    public void DynamicInitAssignmentsTrackDensePrefix()
    {
        var result = Compile(
            """
            module Demo

            fn u32[0 2 ** 31 - 1] Run() {
                stack mut dynamic u32[0 2 ** 31 - 1] values = new(4);
                init values[0] = 10;
                init values[1] = 20;
                return values[1];
            }
            """);

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }

    [Fact]
    public void DynamicInitAssignmentRejectsDensePrefixHole()
    {
        var result = Compile(
            """
            module Demo

            fn void Run() {
                stack mut dynamic u32[0 2 ** 31 - 1] values = new(4);
                init values[1] = 20;
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK4205", "init assignment to dynamic storage 'values[1]'", "next spare slot");
    }

    [Fact]
    public void DynamicAppendByLengthIsAcceptedForUnknownPrefix()
    {
        var result = Compile(
            """
            module Demo

            struct Buffer {
                dynamic u32[0 2 ** 31 - 1] Items;
            }

            fn void Push(mut borrow Buffer self, u32[0 2 ** 31 - 1] value) {
                init self.Items[self.Items.Length] = value;
            }
            """);

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }

    [Fact]
    public void DynamicInitSliceAssignmentsTrackSequentialSlots()
    {
        var result = Compile(
            """
            module Demo

            fn u32[0 2 ** 31 - 1] Run() {
                stack mut dynamic u32[0 2 ** 31 - 1] values = new(4);
                stack init u32[0 2 ** 31 - 1][] spare = init values[values.Length, 2];
                init spare[0] = 10;
                init spare[1] = 20;
                return values[1];
            }
            """);

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }

    [Fact]
    public void DynamicInitSliceIndependentInductionLoopTracksRuntimeSlots()
    {
        var result = Compile(
            """
            module Demo

            fn u64[0 2 ** 63 - 1] Run(u64[0 2 ** 63 - 1] count) {
                stack mut dynamic u64[0 2 ** 63 - 1] values = new(8);
                stack init u64[0 2 ** 63 - 1][] spare = init values[values.Length, count];
                for willexit independent (stack mut u64[0 2 ** 63 - 1] index = 0; index < count; index += 1) {
                    init spare[index] = index;
                }

                return values.Length;
            }
            """);

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }

    [Fact]
    public void DynamicInitSliceIndependentInductionLoopRejectsRepeatedSlotProof()
    {
        var result = Compile(
            """
            module Demo

            fn void Run(u64[0 2 ** 63 - 1] count) {
                stack mut dynamic u64[0 2 ** 63 - 1] values = new(8);
                stack init u64[0 2 ** 63 - 1][] spare = init values[values.Length, count];
                for willexit independent (stack mut u64[0 2 ** 63 - 1] index = 0; index < count; index += 1) {
                    init spare[index] = index;
                    init spare[index] = index;
                }
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK4205", "repeats the same dynamic loop slot proof");
    }

    [Fact]
    public void DynamicInitSliceRejectsOutOfOrderSlotInitialization()
    {
        var result = Compile(
            """
            module Demo

            fn void Run() {
                stack mut dynamic u32[0 2 ** 31 - 1] values = new(4);
                stack init u32[0 2 ** 31 - 1][] spare = init values[values.Length, 2];
                init spare[1] = 20;
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK4205", "expected slot 0 but found slot 1");
    }

    [Fact]
    public void DynamicNonTailMoveRequiresSparseSlotProof()
    {
        var result = Compile(
            """
            module Demo

            struct Token {
                ascii Text;
            }

            fn void Run() {
                stack mut dynamic Token values = new(2);
                init values[0] = new Token() { Text = "a" };
                stack Token token = values[0];
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK4203", "Cannot move a non-tail dynamic storage slot", "sparse initialized-slot proof");
    }

    [Fact]
    public void UnsafeDynamicSparseSlotProofAllowsReadInsideProofBoundary()
    {
        var result = Compile(
            """
            module Demo

            fn u32[0 2 ** 31 - 1] Read(dynamic u32[0 2 ** 31 - 1] values, u32[0 2 ** 31 - 1] index) {
                unsafe {
                    return values[index];
                }
            }
            """);

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }

    [Fact]
    public void UnsafeDynamicSparseInitProofAllowsUseInsideProofBoundaryOnly()
    {
        var result = Compile(
            """
            module Demo

            fn u32[0 2 ** 31 - 1] Run() {
                stack mut dynamic u32[0 2 ** 31 - 1] values = new(4);
                unsafe {
                    init values[2] = 30;
                    return values[2];
                }
            }
            """);

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }

    [Fact]
    public void UnsafeDynamicSparseInitProofDoesNotLeakIntoSafeCode()
    {
        var result = Compile(
            """
            module Demo

            fn u32[0 2 ** 31 - 1] Run() {
                stack mut dynamic u32[0 2 ** 31 - 1] values = new(4);
                unsafe {
                    init values[2] = 30;
                }

                return values[2];
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK4205", "cannot read dynamic storage slot 'values[2]'", "proof");
    }

    [Fact]
    public void UnsafeDynamicSparseProofAllowsNonTailMoveInsideProofBoundary()
    {
        var result = Compile(
            """
            module Demo

            struct Token {
                ascii Text;
            }

            fn ascii Run() {
                stack mut dynamic Token values = new(2);
                init values[0] = new Token() { Text = "a" };
                init values[1] = new Token() { Text = "b" };
                unsafe {
                    stack Token token = values[0];
                    return token.Text;
                }
            }
            """);

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }

    private static CompilationResult Compile(string source)
    {
        return DefaultCompilerPipeline.Create().Run(new CompilationInput(source));
    }

    private static OwnershipValidationModel GetOwnership(CompilationResult result)
    {
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.OwnershipValidation, out OwnershipValidationModel? ownership));
        Assert.NotNull(ownership);
        return ownership;
    }

    private static void AssertDiagnostic(CompilationResult result, string code, params string[] messageFragments)
    {
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == code
                && messageFragments.All(fragment => diagnostic.Message.Contains(fragment, StringComparison.Ordinal)));
    }
}
