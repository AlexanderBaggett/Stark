using Stark.Compiler;
using Stark.Parsing;

namespace compiler.Tests;

public sealed class StarkTestRunnerGeneratorTests
{
    [Fact]
    public void PlatformGatesSkipNonMatchingFactsInGeneratedRunner()
    {
        const string source = """
                              module Tests

                              [Fact]
                              [Platform(linux)]
                              fn bool LinuxOnly()
                              {
                                  return true;
                              }

                              [Fact, Platform(windows)]
                              fn bool WindowsOnly()
                              {
                                  return false;
                              }

                              [Fact]
                              [SkipPlatform(linux)]
                              fn bool SkippedOnLinux()
                              {
                                  return false;
                              }

                              [Platform(linux)]
                              struct LinuxGroup
                              {
                                  [Fact]
                                  static fn bool GroupFact()
                                  {
                                      return true;
                                  }

                                  [Fact]
                                  [Platform(windows)]
                                  static fn bool NarrowedOut()
                                  {
                                      return false;
                                  }
                              }
                              """;

        var result = StarkTestRunnerGenerator.Generate(
            source,
            [],
            "x86_64-unknown-linux-gnu");

        Assert.True(result.Success, FormatDiagnostics(result));
        Assert.True(result.GeneratedRunner);
        var runner = result.SourceText!;
        Assert.Contains(", \"LinuxOnly\", LinuxOnly())", runner, StringComparison.Ordinal);
        Assert.Contains(", \"LinuxGroup.GroupFact\", LinuxGroup.GroupFact())", runner, StringComparison.Ordinal);
        Assert.Contains("System.Testing.SkipFact(\"WindowsOnly\", \"target does not match [Platform]\")", runner, StringComparison.Ordinal);
        Assert.Contains("System.Testing.SkipFact(\"SkippedOnLinux\", \"excluded by [SkipPlatform]\")", runner, StringComparison.Ordinal);
        Assert.Contains("System.Testing.SkipFact(\"LinuxGroup.NarrowedOut\", \"target does not match [Platform]\")", runner, StringComparison.Ordinal);
        Assert.DoesNotContain(", \"WindowsOnly\", WindowsOnly())", runner, StringComparison.Ordinal);
        Assert.DoesNotContain(", \"SkippedOnLinux\", SkippedOnLinux())", runner, StringComparison.Ordinal);
        Assert.DoesNotContain(", \"LinuxGroup.NarrowedOut\", LinuxGroup.NarrowedOut())", runner, StringComparison.Ordinal);
    }

    [Fact]
    public void PlatformGatesSkipNonMatchingTheoryRowsInGeneratedRunner()
    {
        const string source = """
                              module Tests

                              [Theory]
                              [Platform(windows)]
                              [InlineData(1)]
                              [InlineData(2)]
                              fn bool WindowsOnly(i32[min max] value)
                              {
                                  return value != 0;
                              }
                              """;

        var result = StarkTestRunnerGenerator.Generate(
            source,
            [],
            "x86_64-unknown-linux-gnu");

        Assert.True(result.Success, FormatDiagnostics(result));
        Assert.True(result.GeneratedRunner);
        var runner = result.SourceText!;
        Assert.Contains("System.Testing.SkipFact(\"WindowsOnly(1)\", \"target does not match [Platform]\")", runner, StringComparison.Ordinal);
        Assert.Contains("System.Testing.SkipFact(\"WindowsOnly(2)\", \"target does not match [Platform]\")", runner, StringComparison.Ordinal);
        Assert.DoesNotContain(", \"WindowsOnly(1)\", WindowsOnly(1))", runner, StringComparison.Ordinal);
        Assert.DoesNotContain(", \"WindowsOnly(2)\", WindowsOnly(2))", runner, StringComparison.Ordinal);
    }

    [Fact]
    public void PlatformGatesAcceptExactTargetTriplesAndArchitectureAliases()
    {
        const string source = """
                              module Tests

                              [Fact]
                              [Platform("aarch64-unknown-linux-gnu")]
                              fn bool ExactTriple()
                              {
                                  return true;
                              }

                              [Fact]
                              [Platform(arm64)]
                              fn bool Arm64Alias()
                              {
                                  return true;
                              }
                              """;

        var result = StarkTestRunnerGenerator.Generate(
            source,
            [],
            "aarch64-unknown-linux-gnu");

        Assert.True(result.Success, FormatDiagnostics(result));
        Assert.True(result.GeneratedRunner);
        var runner = result.SourceText!;
        Assert.Contains(", \"ExactTriple\", ExactTriple())", runner, StringComparison.Ordinal);
        Assert.Contains(", \"Arm64Alias\", Arm64Alias())", runner, StringComparison.Ordinal);
    }

    [Fact]
    public void PlatformGatesReportMalformedSelectors()
    {
        const string source = """
                              module Tests

                              [Fact]
                              [Platform()]
                              fn bool MissingSelector()
                              {
                                  return true;
                              }

                              [Fact]
                              [SkipPlatform(123)]
                              fn bool IntegerSelector()
                              {
                                  return true;
                              }

                              [Fact]
                              [Platform(plan9)]
                              fn bool UnknownSelector()
                              {
                                  return true;
                              }
                              """;

        var result = StarkTestRunnerGenerator.Generate(
            source,
            [],
            "x86_64-unknown-linux-gnu");

        Assert.False(result.Success);
        var diagnostics = FormatDiagnostics(result);
        Assert.Contains("must name at least one platform selector", diagnostics, StringComparison.Ordinal);
        Assert.Contains("selector must be an OS, architecture, os.arch pair, or exact target triple string", diagnostics, StringComparison.Ordinal);
        Assert.Contains("Unsupported [Platform] selector 'plan9'", diagnostics, StringComparison.Ordinal);
    }

    [Fact]
    public void FiltersCanSelectPlatformSkippedFacts()
    {
        const string source = """
                              module Tests

                              [Fact]
                              [Platform(windows)]
                              fn bool WindowsOnly()
                              {
                                  return false;
                              }
                              """;

        var result = StarkTestRunnerGenerator.Generate(
            source,
            ["Windows"],
            "x86_64-unknown-linux-gnu");

        Assert.True(result.Success, FormatDiagnostics(result));
        Assert.True(result.GeneratedRunner);
        Assert.Contains(
            "System.Testing.SkipFact(\"WindowsOnly\", \"target does not match [Platform]\")",
            result.SourceText!,
            StringComparison.Ordinal);
    }

    [Fact]
    public void CollectionsGroupFactsByFirstCollectionOccurrence()
    {
        const string source = """
                              module Tests

                              [Fact]
                              fn bool Before()
                              {
                                  return true;
                              }

                              [Fact]
                              [Collection(Toolchain)]
                              fn bool ToolchainA()
                              {
                                  return true;
                              }

                              [Fact]
                              fn bool Between()
                              {
                                  return true;
                              }

                              [Fact]
                              [Collection("Toolchain")]
                              fn bool ToolchainB()
                              {
                                  return true;
                              }

                              [Serial]
                              struct SerialGroup
                              {
                                  [Fact]
                                  static fn bool First()
                                  {
                                      return true;
                                  }

                                  [Fact]
                                  static fn bool Second()
                                  {
                                      return true;
                                  }
                              }
                              """;

        var result = StarkTestRunnerGenerator.Generate(
            source,
            [],
            "x86_64-unknown-linux-gnu");

        Assert.True(result.Success, FormatDiagnostics(result));
        Assert.True(result.GeneratedRunner);
        var runner = result.SourceText!;
        Assert.Contains("    // Test collection: Toolchain", runner, StringComparison.Ordinal);
        Assert.Contains("    // Test collection: Serial", runner, StringComparison.Ordinal);
        AssertInOrder(
            runner,
            ", \"Before\", Before())",
            "    // Test collection: Toolchain",
            ", \"ToolchainA\", ToolchainA())",
            ", \"ToolchainB\", ToolchainB())",
            ", \"Between\", Between())",
            "    // Test collection: Serial",
            ", \"SerialGroup.First\", SerialGroup.First())",
            ", \"SerialGroup.Second\", SerialGroup.Second())");
    }

    [Fact]
    public void TheoriesExpandInlineDataRowsInGeneratedRunner()
    {
        const string source = """
                              module Tests

                              [Theory]
                              [InlineData(1, 2, 3)]
                              [InlineData(-2, 5, 3)]
                              fn bool Adds(i32[min max] left, i32[min max] right, i32[min max] expected)
                              {
                                  return left + right == expected;
                              }

                              [Collection(TextCases)]
                              [Theory]
                              [InlineData("stark", true)]
                              fn bool TextCase(ascii name, bool expected)
                              {
                                  return expected;
                              }
                              """;

        var result = StarkTestRunnerGenerator.Generate(
            source,
            [],
            "x86_64-unknown-linux-gnu");

        Assert.True(result.Success, FormatDiagnostics(result));
        Assert.True(result.GeneratedRunner);
        var runner = result.SourceText!;
        Assert.Contains(", \"Adds(1, 2, 3)\", Adds(1, 2, 3))", runner, StringComparison.Ordinal);
        Assert.Contains(", \"Adds(-2, 5, 3)\", Adds(-2, 5, 3))", runner, StringComparison.Ordinal);
        Assert.Contains("    // Test collection: TextCases", runner, StringComparison.Ordinal);
        Assert.Contains(
            ", \"TextCase(\\\"stark\\\", true)\", TextCase(\"stark\", true))",
            runner,
            StringComparison.Ordinal);
    }

    [Fact]
    public void FiltersCanSelectTheoryRowsByGeneratedDisplayName()
    {
        const string source = """
                              module Tests

                              [Theory]
                              [InlineData(1, 2, 3)]
                              [InlineData(-2, 5, 3)]
                              fn bool Adds(i32[min max] left, i32[min max] right, i32[min max] expected)
                              {
                                  return left + right == expected;
                              }
                              """;

        var result = StarkTestRunnerGenerator.Generate(
            source,
            ["-2"],
            "x86_64-unknown-linux-gnu");

        Assert.True(result.Success, FormatDiagnostics(result));
        Assert.True(result.GeneratedRunner);
        var runner = result.SourceText!;
        Assert.Contains(", \"Adds(-2, 5, 3)\", Adds(-2, 5, 3))", runner, StringComparison.Ordinal);
        Assert.DoesNotContain(", \"Adds(1, 2, 3)\", Adds(1, 2, 3))", runner, StringComparison.Ordinal);
    }

    [Fact]
    public void TheoriesExpandMemberDataRowsInGeneratedRunner()
    {
        const string source = """
                              module Tests

                              record AddRow(i32[min max] Left, i32[min max] Right, i32[min max] Expected) { }
                              record TextRow(ascii name, bool expected) { }

                              [Theory]
                              [MemberData(AddRows, AddRow, 2, Left, Right, Expected)]
                              fn bool Adds(i32[min max] left, i32[min max] right, i32[min max] expected)
                              {
                                  return left + right == expected;
                              }

                              [Collection(TextCases)]
                              [Theory]
                              [MemberData(TextRows, TextRow, 1)]
                              fn bool TextCase(ascii name, bool expected)
                              {
                                  return expected;
                              }
                              """;

        var result = StarkTestRunnerGenerator.Generate(
            source,
            [],
            "x86_64-unknown-linux-gnu");

        Assert.True(result.Success, FormatDiagnostics(result));
        Assert.True(result.GeneratedRunner);
        var runner = result.SourceText!;
        Assert.Contains("stack AddRow __stark_member_data_0 = AddRows(0);", runner, StringComparison.Ordinal);
        Assert.Contains(
            ", \"Adds[AddRows:0]\", Adds(__stark_member_data_0.Left, __stark_member_data_0.Right, __stark_member_data_0.Expected))",
            runner,
            StringComparison.Ordinal);
        Assert.Contains("stack AddRow __stark_member_data_1 = AddRows(1);", runner, StringComparison.Ordinal);
        Assert.Contains(
            ", \"Adds[AddRows:1]\", Adds(__stark_member_data_1.Left, __stark_member_data_1.Right, __stark_member_data_1.Expected))",
            runner,
            StringComparison.Ordinal);
        Assert.Contains("    // Test collection: TextCases", runner, StringComparison.Ordinal);
        Assert.Contains("stack TextRow __stark_member_data_2 = TextRows(0);", runner, StringComparison.Ordinal);
        Assert.Contains(
            ", \"TextCase[TextRows:0]\", TextCase(__stark_member_data_2.name, __stark_member_data_2.expected))",
            runner,
            StringComparison.Ordinal);
    }

    [Fact]
    public void FiltersCanSelectMemberDataRowsByGeneratedDisplayName()
    {
        const string source = """
                              module Tests

                              record AddRow(i32[min max] Left, i32[min max] Right, i32[min max] Expected) { }

                              [Theory]
                              [MemberData(AddRows, AddRow, 2, Left, Right, Expected)]
                              fn bool Adds(i32[min max] left, i32[min max] right, i32[min max] expected)
                              {
                                  return left + right == expected;
                              }
                              """;

        var result = StarkTestRunnerGenerator.Generate(
            source,
            ["AddRows:1"],
            "x86_64-unknown-linux-gnu");

        Assert.True(result.Success, FormatDiagnostics(result));
        Assert.True(result.GeneratedRunner);
        var runner = result.SourceText!;
        Assert.Contains("stack AddRow __stark_member_data_1 = AddRows(1);", runner, StringComparison.Ordinal);
        Assert.Contains(
            ", \"Adds[AddRows:1]\", Adds(__stark_member_data_1.Left, __stark_member_data_1.Right, __stark_member_data_1.Expected))",
            runner,
            StringComparison.Ordinal);
        Assert.DoesNotContain("AddRows(0)", runner, StringComparison.Ordinal);
        Assert.DoesNotContain("Adds[AddRows:0]", runner, StringComparison.Ordinal);
    }

    [Fact]
    public void CollectionsReportMalformedAndConflictingAttributes()
    {
        const string source = """
                              module Tests

                              [Fact]
                              [Collection()]
                              fn bool MissingCollectionName()
                              {
                                  return true;
                              }

                              [Fact]
                              [Collection(123)]
                              fn bool IntegerCollectionName()
                              {
                                  return true;
                              }

                              [Fact]
                              [Serial(Toolchain)]
                              fn bool SerialWithArgument()
                              {
                                  return true;
                              }

                              [Fact]
                              [Collection(" Toolchain")]
                              fn bool UntrimmedName()
                              {
                                  return true;
                              }

                              """;

        var result = StarkTestRunnerGenerator.Generate(
            source,
            [],
            "x86_64-unknown-linux-gnu");

        Assert.False(result.Success);
        var diagnostics = FormatDiagnostics(result);
        Assert.Contains("[Collection] on generated tests must name at least one collection", diagnostics, StringComparison.Ordinal);
        Assert.Contains("[Collection] name must be a string literal or qualified identifier", diagnostics, StringComparison.Ordinal);
        Assert.Contains("[Serial] on generated tests does not take arguments", diagnostics, StringComparison.Ordinal);
        Assert.Contains("Invalid [Collection] name ' Toolchain'", diagnostics, StringComparison.Ordinal);
    }

    [Fact]
    public void CollectionsUnionAcrossModuleTypeAndMemberAndAcceptVariadicNames()
    {
        const string source = """
                              [Collection("module-wide")]
                              module Tests

                              [Serial]
                              struct SerialGroup
                              {
                                  [Fact]
                                  [Collection(Toolchain, "extra")]
                                  static fn bool UnionMember()
                                  {
                                      return true;
                                  }
                              }
                              """;

        var result = StarkTestRunnerGenerator.Generate(
            source,
            [],
            "x86_64-unknown-linux-gnu");

        Assert.True(result.Success, FormatDiagnostics(result));
        var fact = Assert.Single(result.AllFacts);
        Assert.Equal(["module-wide", "Serial", "Toolchain", "extra"], fact.CollectionNames);
        Assert.Equal("module-wide", fact.PrimaryCollectionName);
        Assert.Contains("System.Testing.CollectionArgumentEquals(argumentIndex, \"module-wide\")", result.SourceText, StringComparison.Ordinal);
        Assert.Contains("--list-collections", result.SourceText, StringComparison.Ordinal);
    }

    [Fact]
    public void TheoriesReportMalformedInlineData()
    {
        const string source = """
                              module Tests

                              [Theory]
                              fn bool MissingRows(i32[min max] value)
                              {
                                  return value == 0;
                              }

                              [Theory]
                              [InlineData(1)]
                              fn bool WrongArity(i32[min max] left, i32[min max] right)
                              {
                                  return left == right;
                              }

                              [Fact]
                              [InlineData(1)]
                              fn bool FactWithInlineData()
                              {
                                  return true;
                              }

                              [Fact]
                              [Theory]
                              [InlineData()]
                              fn bool BothFactAndTheory()
                              {
                                  return true;
                              }
                              """;

        var result = StarkTestRunnerGenerator.Generate(
            source,
            [],
            "x86_64-unknown-linux-gnu");

        Assert.False(result.Success);
        var diagnostics = FormatDiagnostics(result);
        Assert.Contains(
            "[Theory] test 'MissingRows' must declare at least one data row through [InlineData(...)] or [MemberData(...)]",
            diagnostics,
            StringComparison.Ordinal);
        Assert.Contains(
            "[Theory] test 'WrongArity' inline data row 1 has 1 value(s), but the test has 2 parameter(s)",
            diagnostics,
            StringComparison.Ordinal);
        Assert.Contains("[Fact] test 'FactWithInlineData' cannot use [InlineData]", diagnostics, StringComparison.Ordinal);
        Assert.Contains("A test declaration cannot use both [Fact] and [Theory]", diagnostics, StringComparison.Ordinal);
    }

    [Fact]
    public void TheoriesReportMalformedMemberData()
    {
        const string source = """
                              module Tests

                              [Theory]
                              [MemberData()]
                              fn bool MissingArguments(i32[min max] value)
                              {
                                  return value == 0;
                              }

                              [Theory]
                              [MemberData(123, Row, 1)]
                              fn bool BadProvider(i32[min max] value)
                              {
                                  return value == 0;
                              }

                              [Theory]
                              [MemberData(Cases, 123, 1)]
                              fn bool BadRowType(i32[min max] value)
                              {
                                  return value == 0;
                              }

                              [Theory]
                              [MemberData(Cases, Row, -1)]
                              fn bool BadCount(i32[min max] value)
                              {
                                  return value == 0;
                              }

                              [Theory]
                              [MemberData(Cases, Row, 1, Namespace.Field)]
                              fn bool BadField(i32[min max] value)
                              {
                                  return value == 0;
                              }

                              [Theory]
                              [MemberData(Cases, Row, 1, OneField)]
                              fn bool FieldMismatch(i32[min max] left, i32[min max] right)
                              {
                                  return left == right;
                              }

                              [Theory]
                              [MemberData(Cases, Row, 2147483647)]
                              [MemberData(MoreCases, Row, 1)]
                              fn bool TooManyRows(i32[min max] value)
                              {
                                  return value == 0;
                              }

                              [Fact]
                              [MemberData(Cases, Row, 1)]
                              fn bool FactWithMemberData()
                              {
                                  return true;
                              }
                              """;

        var result = StarkTestRunnerGenerator.Generate(
            source,
            [],
            "x86_64-unknown-linux-gnu");

        Assert.False(result.Success);
        var diagnostics = FormatDiagnostics(result);
        Assert.Contains("[MemberData] on [Theory] tests must provide a provider function", diagnostics, StringComparison.Ordinal);
        Assert.Contains("[MemberData] provider function must be a qualified identifier", diagnostics, StringComparison.Ordinal);
        Assert.Contains("[MemberData] row type must be a qualified identifier", diagnostics, StringComparison.Ordinal);
        Assert.Contains("[MemberData] row count must be between 1 and 2147483647", diagnostics, StringComparison.Ordinal);
        Assert.Contains("[MemberData] row field names must be simple identifiers", diagnostics, StringComparison.Ordinal);
        Assert.Contains(
            "[Theory] test 'FieldMismatch' member data source 'Cases' maps 1 field(s), but the test has 2 parameter(s)",
            diagnostics,
            StringComparison.Ordinal);
        Assert.Contains("[Theory] test 'TooManyRows' expands to too many generated rows", diagnostics, StringComparison.Ordinal);
        Assert.Contains("[Fact] test 'FactWithMemberData' cannot use [MemberData]", diagnostics, StringComparison.Ordinal);
    }

    [Fact]
    public void DuplicateExpandedTheoryRowsReportGeneratedEntryCollision()
    {
        const string source = """
                              module Tests

                              [Theory]
                              [InlineData(1)]
                              [InlineData(1)]
                              fn bool SameValue(i32[min max] value)
                              {
                                  return value == 1;
                              }
                              """;

        var result = StarkTestRunnerGenerator.Generate(
            source,
            [],
            "x86_64-unknown-linux-gnu");

        Assert.False(result.Success);
        var diagnostics = FormatDiagnostics(result);
        Assert.Contains("Duplicate generated test entry name 'SameValue(1)'", diagnostics, StringComparison.Ordinal);
    }

    [Fact]
    public void CollectionAttributesRequireFactOnCallables()
    {
        const string source = """
                              module Tests

                              [Collection(Toolchain)]
                              fn bool Helper()
                              {
                                  return true;
                              }

                              [Serial]
                              fn bool SerialHelper()
                              {
                                  return true;
                              }

                              [InlineData(1)]
                              fn bool DataHelper()
                              {
                                  return true;
                              }

                              [MemberData(Cases, Row, 1)]
                              fn bool MemberDataHelper()
                              {
                                  return true;
                              }
                              """;

        var parseResult = StarkSyntax.ParseCompilationUnit(source);
        Assert.True(parseResult.Succeeded);

        var syntaxModel = SyntaxModelFactory.CreateWithDiagnostics(parseResult, targetInfo: null);
        var diagnostics = string.Join(
            Environment.NewLine,
            syntaxModel.Diagnostics.Select(static diagnostic => diagnostic.Message));
        Assert.Contains(
            "Attribute '[Collection]' on function 'Helper' is test scheduling metadata and requires '[Fact]' or '[Theory]'.",
            diagnostics,
            StringComparison.Ordinal);
        Assert.Contains(
            "Attribute '[Serial]' on function 'SerialHelper' is test scheduling metadata and requires '[Fact]' or '[Theory]'.",
            diagnostics,
            StringComparison.Ordinal);
        Assert.Contains(
            "Attribute '[InlineData]' on function 'DataHelper' requires '[Theory]'.",
            diagnostics,
            StringComparison.Ordinal);
        Assert.Contains(
            "Attribute '[MemberData]' on function 'MemberDataHelper' requires '[Theory]'.",
            diagnostics,
            StringComparison.Ordinal);
    }

    private static string FormatDiagnostics(StarkTestRunnerGenerationResult result)
    {
        return string.Join(
            Environment.NewLine,
            result.Diagnostics.Select(diagnostic =>
                $"{diagnostic.Line}:{diagnostic.Column}: {diagnostic.Message}"));
    }

    private static void AssertInOrder(string text, params string[] needles)
    {
        var currentIndex = -1;
        foreach (var needle in needles)
        {
            var nextIndex = text.IndexOf(needle, currentIndex + 1, StringComparison.Ordinal);
            Assert.True(nextIndex >= 0, $"Expected to find '{needle}' after index {currentIndex}.{Environment.NewLine}{text}");
            currentIndex = nextIndex;
        }
    }
}
