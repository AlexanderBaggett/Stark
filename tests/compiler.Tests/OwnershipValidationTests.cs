using Stark.Compiler;

namespace compiler.Tests;

public sealed class OwnershipValidationTests
{
    [Fact]
    public void HeapOwnedValuesAreDroppedAtScopeExit()
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

            finite i32[min max] Run()
            {
                heap Box box = new Box()
                {
                    Value = 1
                };
                return 1;
            }
            """);

        Assert.True(result.Succeeded);
        var ownership = GetOwnership(result);
        Assert.Contains("box", ownership.Functions["Run"].ImplicitDrops);
    }

    [Fact]
    public void MovingOwnedLocalMakesLaterUseInvalid()
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

            finite law void Consume(Box value)
            {
                return;
            }

            finite law i32[min max] Run()
            {
                stack Box box = new Box()
                {
                    Value = 1
                };
                Consume(box);
                return box.Value;
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK4200", "Move error", "was moved and must be reinitialized");
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "STK4200"
                && diagnostic.Severity == DiagnosticSeverity.Info
                && diagnostic.Message.Contains("was moved here", StringComparison.Ordinal));
    }

    [Fact]
    public void MoveDiagnosticsAreNotDuplicatedForTheSameUse()
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

            finite law void Consume(Box value)
            {
                return;
            }

            finite law i32[min max] Run()
            {
                stack Box box = new Box()
                {
                    Value = 1
                };
                Consume(box);
                return box.Value;
            }
            """);

        Assert.False(result.Succeeded);
        Assert.Equal(
            1,
            result.Diagnostics.Count(diagnostic =>
                diagnostic.Code == "STK4200"
                && diagnostic.Severity == DiagnosticSeverity.Error
                && diagnostic.Message.Contains("Move error", StringComparison.Ordinal)));
        Assert.Equal(
            1,
            result.Diagnostics.Count(diagnostic =>
                diagnostic.Code == "STK4200"
                && diagnostic.Severity == DiagnosticSeverity.Info
                && diagnostic.Message.Contains("was moved here", StringComparison.Ordinal)));
    }

    [Fact]
    public void ValueReceiverMethodCallsMoveTheReceiver()
    {
        var result = Compile(
            """
            module Demo

            struct Box
            {
                i32[min max] Value;

                finite law void Consume(Box box)
                {
                    return;
                }

                drop
                {
                }
            }

            finite law i32[min max] Run()
            {
                stack Box box = new Box()
                {
                    Value = 1
                };
                box.Consume();
                return box.Value;
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK4200", "Move error", "was moved and must be reinitialized");
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "STK4200"
                && diagnostic.Severity == DiagnosticSeverity.Info
                && diagnostic.Message.Contains("was moved here", StringComparison.Ordinal));
    }

    [Fact]
    public void CopyValuesRemainUsableAfterAssignment()
    {
        var result = Compile(
            """
            module Demo

            finite law i32[min max] Run()
            {
                stack i32[min max] x = 1;
                stack i32[min max] y = x;
                return x + y;
            }
            """);

        Assert.True(result.Succeeded);
        var ownership = GetOwnership(result);
        Assert.True(ownership.Functions["Run"].OwnershipValid);
        Assert.DoesNotContain("x", ownership.Functions["Run"].Moves);
    }

    [Fact]
    public void ImmutableTextViewsRemainUsableAfterByValueCalls()
    {
        var result = Compile(
            """
            module Demo

            finite law u64[0 2 ** 63 - 1] ReadAscii(ascii value)
            {
                return 1;
            }

            finite law u64[0 2 ** 63 - 1] ReadUnicode(unicode value)
            {
                return 1;
            }

            finite law u64[0 2 ** 63 - 1] Run()
            {
                stack ascii text = "alpha";
                stack unicode wide = (unicode)"beta";
                stack u64[0 2 ** 63 - 1] first = ReadAscii(text);
                stack u64[0 2 ** 63 - 1] second = ReadAscii(text);
                stack u64[0 2 ** 63 - 1] third = ReadUnicode(wide);
                stack u64[0 2 ** 63 - 1] fourth = ReadUnicode(wide);
                return first + second + third + fourth;
            }
            """);

        Assert.True(result.Succeeded);
        var ownership = GetOwnership(result);
        Assert.True(ownership.Functions["Run"].OwnershipValid);
        Assert.DoesNotContain("text", ownership.Functions["Run"].Moves);
        Assert.DoesNotContain("wide", ownership.Functions["Run"].Moves);
    }

    [Fact]
    public void ReassigningOwnedLocalDropsPreviousValue()
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

            finite law i32[min max] Run()
            {
                stack mut Box box = new Box()
                {
                    Value = 1
                };
                box = new Box()
                {
                    Value = 2
                };
                return box.Value;
            }
            """);

        Assert.True(result.Succeeded);
        var ownership = GetOwnership(result);
        Assert.Contains("box", ownership.Functions["Run"].ImplicitDrops);
    }

    [Fact]
    public void ConditionalMoveMakesLaterUseInvalid()
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

            finite law void Consume(Box value)
            {
                return;
            }

            finite law i32[min max] Run()
            {
                stack Box box = new Box()
                {
                    Value = 1
                };
                if (true)
                {
                    Consume(box);
                }

                return box.Value;
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK4200");
    }

    [Fact]
    public void ReturningOwnedLocalMovesItOutInsteadOfDroppingIt()
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

            finite law Box Make()
            {
                stack Box box = new Box()
                {
                    Value = 1
                };
                return box;
            }
            """);

        Assert.True(result.Succeeded);
        var ownership = GetOwnership(result);
        Assert.Contains("box", ownership.Functions["Make"].Moves);
        Assert.DoesNotContain("box", ownership.Functions["Make"].ImplicitDrops);
    }

    [Fact]
    public void MovingOutOfATopLevelFieldIsAllowed()
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

        Assert.True(result.Succeeded);
        var ownership = GetOwnership(result);
        Assert.Contains("value.Name", ownership.Functions["Run"].Moves);
        Assert.Contains("value", ownership.Functions["Run"].ImplicitDrops);
    }

    [Fact]
    public void MovingOutOfANestedFieldIsRejected()
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

            struct NameBox
            {
                Name Value;
            }

            struct Container
            {
                NameBox Name;
            }

            finite law Name Run()
            {
                stack Container value = new Container()
                {
                    Name = new NameBox()
                    {
                        Value = new Name()
                        {
                            Value = 1
                        }
                    }
                };
                return value.Name.Value;
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK4203", "Cannot move out of field or indexed place");
    }

    [Fact]
    public void DoctrineCallsParticipateInOwnershipFlow()
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

            doctrine Sink
            {
                finite law void Consume(Box value)
                {
                    return;
                }
            }

            finite law i32[min max] Run()
            {
                stack Box box = new Box()
                {
                    Value = 1
                };
                Sink.Consume(box);
                return box.Value;
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK4200", "Move error", "was moved and must be reinitialized");
    }

    [Fact]
    public void ExplicitGenericDoctrineCallsParticipateInOwnershipFlow()
    {
        var result = Compile(
            """
            module Demo

            struct Box<T>
            {
                T Value;

                drop
                {
                }
            }

            doctrine Sink<T>
            {
                finite law void Consume(Box<T> value)
                {
                    return;
                }
            }

            finite law i32[min max] Run()
            {
                stack Box<i32[min max]> box = new Box<i32[min max]>()
                {
                    Value = 1
                };
                Sink<i32[min max]>.Consume(box);
                return box.Value;
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK4200", "Move error", "was moved and must be reinitialized");
    }

    [Fact]
    public void RuntimeTextConcatenationConsumesOwnedTextResult()
    {
        var result = Compile(
            """
            import System.Memory
            import System.Text
            module Demo

            fn System.Memory.MemoryResult<System.Text.OwnedAscii> Make();

            fn i32[min max] Run()
            {
                stack System.Memory.MemoryResult<System.Text.OwnedAscii> result = Make();
                stack System.Memory.MemoryResult<System.Text.OwnedAscii> joined = "Score: " + result;
                stack System.Memory.MemoryResult<System.Text.OwnedAscii> second = result;
                return 0;
            }
            """,
            new CompilerOptions(
                ModuleResolver: new InMemoryModuleResolver(
                [
                    (
                        new ResolvedModuleReference("System.Memory", "/virtual/System.Memory.stark", IsExternal: false),
                        """
                        module System.Memory

                        public enum MemoryError
                        {
                            OutOfMemory,
                        }

                        public enum MemoryResult<T>
                        {
                            Ok(T),
                            Err(MemoryError),
                        }
                        """,
                        "/virtual/System.Memory.stark"
                    ),
                    (
                        new ResolvedModuleReference("System.Text", "/virtual/System.Text.stark", IsExternal: false),
                        """
                        import System.Memory
                        module System.Text

                        public struct OwnedAscii
                        {
                            ascii Text;

                            drop
                            {
                            }
                        }

                        public fn System.Memory.MemoryResult<OwnedAscii> ConcatAscii(ascii left, u64[0 2 ** 63 - 1] leftLength, System.Memory.MemoryResult<OwnedAscii> right);
                        """,
                        "/virtual/System.Text.stark"
                    )
                ])));

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK4200", "Move error", "was moved and must be reinitialized");
    }

    [Fact]
    public void DisjointMutableBorrowCallsParticipateInOwnershipFlow()
    {
        var result = Compile(
            """
            module Demo

            struct Box
            {
                i32[min max] Value;
            }

            fn void Touch(borrow mut Box left, borrow mut Box right)
            {
                left.Value = 1;
                right.Value = 2;
                return;
            }

            fn i32[min max] Run(borrow mut Box left, borrow mut Box right)
            {
                Touch(left, right);
                left.Value = left.Value + 1;
                right.Value = right.Value + 1;
                return left.Value + right.Value;
            }
            """);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var ownership = GetOwnership(result);
        Assert.True(ownership.Functions["Run"].OwnershipValid);
        Assert.Empty(ownership.Functions["Run"].Moves);
    }

    private static CompilationResult Compile(string source, CompilerOptions? options = null)
    {
        return DefaultCompilerPipeline.Create().Run(new CompilationInput(source), options);
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
