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

            struct Box
            {
                i32[min max] Value;

                drop
                {
                }
            }

            fn void Consume(Box value)
            {
                return;
            }

            fn i32[min max] Run()
            {
                stack mut Box box = new Box()
                {
                    Value = 1
                };
                Consume(box);
                box = new Box()
                {
                    Value = 2
                };
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
    public void OwnershipSummaryExposesTypedRootEventsForOptimization()
    {
        var result = Compile(
            """
            module Demo

            struct Box
            {
                i32[min max] Value;

                drop
                {
                }
            }

            fn void Consume(Box value)
            {
                return;
            }

            unsafe fn i32[min max] Run()
            {
                stack mut Box box = new Box()
                {
                    Value = 1
                };
                stack rawptr<Box> pointer = &box;
                box = new Box()
                {
                    Value = 2
                };
                Consume(box);
                box = new Box()
                {
                    Value = 3
                };
                return box.Value;
            }
            """);

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var run = GetOwnership(result).Functions["Run"];
        Assert.Contains(
            run.Events,
            static ev => ev.Kind == OwnershipEventKind.AddressTaken
                         && ev.Place.DisplayName == "box"
                         && ev.Place.Type.NamedType == "Box");
        Assert.Contains(run.Events, static ev => ev.Kind == OwnershipEventKind.AssignmentDrop && ev.Place.DisplayName == "box");
        Assert.Contains(run.Events, static ev => ev.Kind == OwnershipEventKind.Move && ev.Place.DisplayName == "box");
        Assert.Contains(run.Events, static ev => ev.Kind == OwnershipEventKind.Reinitialize && ev.Place.DisplayName == "box");

        var box = Assert.Single(run.Roots, static root => root.Name == "box");
        Assert.Equal(OwnershipStorageRootKind.Local, box.RootKind);
        Assert.True(box.IsMutable);
        Assert.True(box.RequiresDrop);
        Assert.True(box.IsAddressTaken);
        Assert.True(box.HasRawPointerEscape);
        Assert.True(box.HasAssignmentDrop);
        Assert.True(box.HasMove);
        Assert.True(box.HasReinitialization);
        Assert.Equal(OwnershipRootAvailabilityKind.Initialized, box.FinalAvailability);

        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
        Assert.NotNull(mir);
        var mirRun = Assert.Single(mir.Functions, static function => function.Name == "Run");
        Assert.NotNull(mirRun.Ownership);
        Assert.Contains(mirRun.Ownership.Events, static ev => ev.Kind == OwnershipEventKind.AddressTaken && ev.Place.DisplayName == "box");

        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.SsaIr, out SsaIrModule? ssa));
        Assert.NotNull(ssa);
        var ssaRun = Assert.Single(ssa.Functions, static function => function.Name == "Run");
        Assert.NotNull(ssaRun.Ownership);
        Assert.Contains(ssaRun.Ownership.Roots, static root => root.Name == "box" && root.HasRawPointerEscape);
    }

    [Fact]
    public void OwnershipSummaryExposesPartialFieldMovesForOptimization()
    {
        var result = Compile(
            """
            module Demo

            struct Name
            {
                i32[min max] Value;

                drop
                {
                }
            }

            struct Container
            {
                Name Name;
                Name Label;
            }

            finite law Name Run()
            {
                stack Container value = new Container()
                {
                    Name = new Name()
                    {
                        Value = 1
                    },
                    Label = new Name()
                    {
                        Value = 2
                    }
                };
                return value.Name;
            }
            """);

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var run = GetOwnership(result).Functions["Run"];
        Assert.Contains(
            run.Events,
            static ev => ev.Kind == OwnershipEventKind.FieldMove
                         && ev.Place.RootName == "value"
                         && ev.Place.Projections.SequenceEqual(["Name"])
                         && ev.Place.Type.NamedType == "Name");

        var value = Assert.Single(run.Roots, static root => root.Name == "value");
        Assert.True(value.HasMove);
        Assert.True(value.HasPartialMove);
        Assert.True(value.HasImplicitDrop);
        Assert.Equal(OwnershipRootAvailabilityKind.PartiallyInitialized, value.FinalAvailability);
    }

    [Fact]
    public void BranchReinitializationKeepsOwnedLocalAvailable()
    {
        var result = Compile(
            """
            module Demo

            struct Box
            {
                i32[min max] Value;
            }

            fn void Consume(Box value)
            {
                return;
            }

            fn i32[min max] Run()
            {
                stack mut Box box = new Box()
                {
                    Value = 1
                };

                if (true)
                {
                    Consume(box);
                    box = new Box()
                    {
                        Value = 2
                    };
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

            struct Box
            {
                i32[min max] Value;
            }

            fn void Consume(Box value)
            {
                return;
            }

            fn i32[min max] Run()
            {
                stack mut Box box = new Box()
                {
                    Value = 1
                };
                stack mut i32[min max] count = 0;

                while willexit (count < 1)
                {
                    Consume(box);
                    box = new Box()
                    {
                        Value = 2
                    };
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

            struct Box
            {
                i32[min max] Value;

                drop
                {
                }
            }

            fn void Consume(Box value)
            {
                return;
            }

            fn i32[min max] Run()
            {
                stack mut Box box = new Box()
                {
                    Value = 1
                };

                if (true)
                {
                    Consume(box);
                }
                else
                {
                    box = new Box()
                    {
                        Value = 2
                    };
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

            enum Token
            {
                End,
                Integer(i32[min max]),
            }

            fn i32[min max] Run(bool choose)
            {
                stack mut Token token;

                if (choose)
                {
                    token = Token.End;
                }
                else
                {
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

            enum Token
            {
                End,
                Integer(i32[min max]),
            }

            fn i32[min max] Run(bool choose)
            {
                stack mut Token token;

                if (choose)
                {
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

            struct Payload
            {
                ascii Text;

                drop
                {
                }
            }

            enum Token
            {
                End,
                Text(Payload),
            }

            fn i32[min max] Run()
            {
                stack Token token = Token.Text(new Payload()
                {
                    Text = "hello"
                });
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

            struct Payload
            {
                ascii Text;

                drop
                {
                }
            }

            enum Token
            {
                End,
                Text(Payload),
            }

            fn i32[min max] Run(bool choose)
            {
                stack Token token = choose ? Token.Text(new Payload()
                {
                    Text = "hello"
                }
                )
                : Token.End;
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

            struct Payload
            {
                ascii Text;

                drop
                {
                }
            }

            enum Token
            {
                Empty,
                Text(Payload),
            }

            fn i32[min max] Run(bool choose)
            {
                stack mut Token token;

                if (choose)
                {
                    token = Token.Text(new Payload()
                    {
                        Text = "hello"
                    });
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

            struct Box
            {
                i32[min max] Value;

                drop
                {
                }
            }

            enum Token
            {
                Empty,
                Full(Box),
            }

            fn void Consume(Box box)
            {
                return;
            }

            fn i32[min max] Run(bool choose)
            {
                stack mut Token token = choose ? Token.Full(new Box()
                {
                    Value = 1
                }
                )
                : Token.Empty;

                switch (token)
                {
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

            struct Box
            {
                i32[min max] Value;

                drop
                {
                }
            }

            enum Token
            {
                Empty,
                Full(Box),
            }

            fn void Consume(Box box)
            {
                return;
            }

            fn i32[min max] Run(bool choose)
            {
                stack mut Token token = choose ? Token.Full(new Box()
                {
                    Value = 1
                }
                )
                : Token.Empty;

                switch (token)
                {
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

            struct Box
            {
                i32[min max] Value;

                drop
                {
                }
            }

            enum Token
            {
                Empty,
                Full(Box),
            }

            struct Wrapper
            {
                Token Value;
            }

            fn void Consume(Box box)
            {
                return;
            }

            fn i32[min max] Run()
            {
                stack Wrapper wrapper = new Wrapper()
                {
                    Value = Token.Full(new Box()
                    {
                        Value = 1
                    }
                    )
                };

                switch (wrapper.Value)
                {
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

            struct Payload
            {
                ascii Text;

                drop
                {
                }
            }

            enum Token
            {
                End,
                Text(Payload),
            }

            fn i32[min max] Run()
            {
                stack mut Token token = Token.Text(new Payload()
                {
                    Text = "hello"
                });
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

            struct Payload
            {
                ascii Text;

                drop
                {
                }
            }

            enum Token
            {
                Empty,
                Text(Payload),
            }

            fn void Consume(Payload text)
            {
                return;
            }

            fn i32[min max] Run(Token token)
            {
                switch (token)
                {
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

            struct Box
            {
                i32[min max] Value;
            }

            fn i32[min max] Run()
            {
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

            struct Box
            {
                i32[min max] Value;
            }

            fn i32[min max] Run()
            {
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

            struct Pair
            {
                i32[min max] Left;
                i32[min max] Right;
            }

            fn i32[min max] Run()
            {
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

            struct Pair
            {
                i32[min max] Left;
                i32[min max] Right;
            }

            fn void Consume(Pair value)
            {
                return;
            }

            fn i32[min max] Run()
            {
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

            struct Pair
            {
                i32[min max] Left;
                i32[min max] Right;

                drop
                {
                }
            }

            fn i32[min max] Run()
            {
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

            struct Payload
            {
                ascii Text;

                drop
                {
                }
            }

            struct Pair
            {
                Payload Left;
                Payload Right;
            }

            fn void Consume(Pair value)
            {
                return;
            }

            fn i32[min max] Run()
            {
                stack mut Pair pair = new Pair()
                {
                    Left = new Payload()
                    {
                        Text = "a"
                    },
                    Right = new Payload()
                    {
                        Text = "b"
                    }
                };
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

            struct Payload
            {
                ascii Text;
            }

            struct Pair
            {
                Payload Left;
                Payload Right;
            }

            fn void Consume(Pair value)
            {
                return;
            }

            fn i32[min max] Run()
            {
                stack mut Pair pair = new Pair()
                {
                    Left = new Payload()
                    {
                        Text = "a"
                    },
                    Right = new Payload()
                    {
                        Text = "b"
                    }
                };
                stack Payload left = pair.Left;
                pair.Left = new Payload()
                {
                    Text = "c"
                };
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

            struct Payload
            {
                ascii Text;

                drop
                {
                }
            }

            struct Pair
            {
                Payload Left;
                Payload Right;
            }

            fn i32[min max] Run(bool choose)
            {
                stack mut Pair pair = new Pair()
                {
                    Left = new Payload()
                    {
                        Text = "a"
                    },
                    Right = new Payload()
                    {
                        Text = "b"
                    }
                };

                if (choose)
                {
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

            struct Payload
            {
                ascii Text;
            }

            struct Pair
            {
                Payload Left;
                Payload Right;
            }

            fn i32[min max] Run(bool choose)
            {
                stack mut Pair pair = new Pair()
                {
                    Left = new Payload()
                    {
                        Text = "a"
                    },
                    Right = new Payload()
                    {
                        Text = "b"
                    }
                };

                if (choose)
                {
                    stack Payload left = pair.Left;
                    pair.Left = new Payload()
                    {
                        Text = "c"
                    };
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

            struct Pair
            {
                i32[min max] Left;
                i32[min max] Right;
            }

            fn i32[min max] Run(bool choose)
            {
                stack mut Pair pair;

                if (choose)
                {
                    pair.Left = 1;
                }
                else
                {
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

            struct Pair
            {
                i32[min max] Left;
                i32[min max] Right;
            }

            fn i32[min max] Run(bool choose)
            {
                stack mut Pair pair;

                if (choose)
                {
                    pair.Left = 1;
                }
                else
                {
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

            fn retborrow i32[min max] Leak()
            {
                return Source();
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK4202", "Lifetime error", "unknown source lifetime", "could not be proven");
    }

    [Fact]
    public void StoredBorrowClosureFieldsRejectTemporaryEnvironments()
    {
        var result = Compile(
            """
            module Demo

            struct Holder
            {
                storeborrow closure<fn i32[min max]()> Callback;
            }

            fn Holder Bad()
            {
                stack i32[min max] value = 1;
                return new Holder()
                {
                    Callback = capture(copy value) () => value
                };
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK4202", "cannot store a temporary borrow", "stored field 'Callback'");
    }

    [Fact]
    public void MoveCapturesConsumeSourceBindingAtClosureCreation()
    {
        var result = Compile(
            """
            module Demo

            struct Box
            {
                i32[min max] Value;
            }

            fn i32[min max] Run()
            {
                stack Box box = new Box()
                {
                    Value = 7
                };
                stack heap closure<fn i32[min max]()> callback = heap capture(move box) () => box.Value;
                return box.Value;
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK4200", "Value 'box'", "moved");
    }

    [Fact]
    public void OutAndInitClosureCapturesMustBeAssignedOnEveryReturnPath()
    {
        var result = Compile(
            """
            module Demo

            inline fn void RunOut(inline closure<mut fn void(bool)> body)
            {
                body(true);
                return;
            }

            inline fn void RunInit(inline closure<mut fn void(bool)> body)
            {
                body(true);
                return;
            }

            fn void BadOut(bool choose)
            {
                stack mut i32[min max] value = 0;
                RunOut(capture(out value) (bool flag) =>
                {
                    if (flag)
                    {
                        value = 1;
                        return;
                    }

                    return;
                });
                return;
            }

            fn void BadInit(bool choose)
            {
                stack mut i32[min max] value = 0;
                RunInit(capture(init value) (bool flag) =>
                {
                    if (flag)
                    {
                        value = 1;
                        return;
                    }

                    return;
                });
                return;
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK4205", "closure capture 'value' with mode 'out'", "every successful return path");
        AssertDiagnostic(result, "STK4205", "closure capture 'value' with mode 'init'", "every successful return path");
    }

    [Fact]
    public void OnceClosureCallsConsumeTheClosureValue()
    {
        var result = Compile(
            """
            module Demo

            inline fn i32[min max] RunInlineTwice(inline closure<once fn i32[min max]()> producer)
            {
                stack i32[min max] first = producer();
                return producer();
            }

            fn i32[min max] RunHeapTwice(heap closure<once fn i32[min max]()> producer)
            {
                stack i32[min max] first = producer();
                return producer();
            }

            fn i32[min max] RunBorrowTwice(borrow closure<once fn i32[min max]()> producer)
            {
                stack i32[min max] first = producer();
                return producer();
            }

            fn i32[min max] UseInline()
            {
                return RunInlineTwice(() => 1);
            }

            fn i32[min max] UseHeap()
            {
                stack heap closure<once fn i32[min max]()> producer = heap () => 1;
                return RunHeapTwice(producer);
            }

            fn i32[min max] UseBorrow()
            {
                return RunBorrowTwice(() => 1);
            }
            """);

        Assert.False(result.Succeeded);
        Assert.True(
            result.Diagnostics.Count(static diagnostic => diagnostic.Code == "STK4200"
                && diagnostic.Message.Contains("Value 'producer'", StringComparison.Ordinal)
                && diagnostic.Message.Contains("moved", StringComparison.Ordinal)) >= 3,
            string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }

    [Fact]
    public void ReturningBorrowFromBranchSpecificCallsStillReportsLifetimeDiagnostic()
    {
        var result = Compile(
            """
            module Demo

            fn retborrow i32[min max] Source();
            fn retborrow i32[min max] Other();

            fn retborrow i32[min max] Leak(bool choose)
            {
                if (choose)
                {
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

            fn void Run()
            {
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

            fn void Run()
            {
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

            fn retborrow i32[min max] Run()
            {
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

            fn u32[0 2 ** 31 - 1] Run()
            {
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

            fn void Run()
            {
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

            struct Buffer
            {
                dynamic u32[0 2 ** 31 - 1] Items;
            }

            fn void Push(mut borrow Buffer self, u32[0 2 ** 31 - 1] value)
            {
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

            fn u32[0 2 ** 31 - 1] Run()
            {
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

            fn u64[0 2 ** 63 - 1] Run(u64[0 2 ** 63 - 1] count)
            {
                stack mut dynamic u64[0 2 ** 63 - 1] values = new(8);
                stack init u64[0 2 ** 63 - 1][] spare = init values[values.Length, count];
                for willexit independent (stack mut u64[0 2 ** 63 - 1] index = 0; index < count; index += 1)
                {
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

            fn void Run(u64[0 2 ** 63 - 1] count)
            {
                stack mut dynamic u64[0 2 ** 63 - 1] values = new(8);
                stack init u64[0 2 ** 63 - 1][] spare = init values[values.Length, count];
                for willexit independent (stack mut u64[0 2 ** 63 - 1] index = 0; index < count; index += 1)
                {
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

            fn void Run()
            {
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

            struct Token
            {
                ascii Text;

                drop
                {
                }
            }

            fn void Run()
            {
                stack mut dynamic Token values = new(2);
                init values[0] = new Token()
                {
                    Text = "a"
                };
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

            fn u32[0 2 ** 31 - 1] Read(dynamic u32[0 2 ** 31 - 1] values, u32[0 2 ** 31 - 1] index)
            {
                unsafe
                {
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

            fn u32[0 2 ** 31 - 1] Run()
            {
                stack mut dynamic u32[0 2 ** 31 - 1] values = new(4);
                unsafe
                {
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

            fn u32[0 2 ** 31 - 1] Run()
            {
                stack mut dynamic u32[0 2 ** 31 - 1] values = new(4);
                unsafe
                {
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

            struct Token
            {
                ascii Text;
            }

            fn ascii Run()
            {
                stack mut dynamic Token values = new(2);
                init values[0] = new Token()
                {
                    Text = "a"
                };
                init values[1] = new Token()
                {
                    Text = "b"
                };
                unsafe
                {
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
