using Stark.Compiler;

namespace compiler.Tests;

public sealed partial class MidLevelIrLoweringTests
{
    [Fact]
    public void DestructorBlocksLowerBeforeStorageDeadAtScopeExit()
    {
        var result = Compile(
            """
            module Demo

            static mut i32[-2147483648 2147483647] Counter = 0;

            fn void Bump(i32[-2147483648 2147483647] value) {
                Counter = Counter + value;
                return;
            }

            struct Buffer {
                i32[-2147483648 2147483647] Value;

                drop {
                    Bump(self.Value);
                }
            }

            fn void Run() {
                stack Buffer box = new Buffer() { Value = 4 };
                return;
            }
            """);

        Assert.True(result.Succeeded);

        var function = Assert.Single(GetMir(result).Functions, static function => function.Name == "Run");
        var statements = function.Blocks.SelectMany(static block => block.Statements).ToArray();
        var callIndex = Array.FindIndex(
            statements,
            static statement => statement.Value is MidLevelIrCallRValue { FunctionName: "Bump" });
        var storageDeadIndex = Array.FindIndex(
            statements,
            static statement => statement.Kind == MidLevelIrStatementKind.StorageDead && statement.TargetName == "box");

        Assert.True(callIndex >= 0);
        Assert.True(storageDeadIndex > callIndex);
    }

    [Fact]
    public void ReassigningADestructibleLocalLowersTheOldDropBeforeOverwrite()
    {
        var result = Compile(
            """
            module Demo

            static mut i32[-2147483648 2147483647] Counter = 0;

            fn void Bump(i32[-2147483648 2147483647] value) {
                Counter = Counter + value;
                return;
            }

            struct Buffer {
                i32[-2147483648 2147483647] Value;

                drop {
                    Bump(self.Value);
                }
            }

            fn void Run() {
                stack mut Buffer box = new Buffer() { Value = 1 };
                box = new Buffer() { Value = 7 };
                return;
            }
            """);

        Assert.True(result.Succeeded);

        var function = Assert.Single(GetMir(result).Functions, static function => function.Name == "Run");
        var statements = function.Blocks.SelectMany(static block => block.Statements).ToArray();
        var boxAssignments = statements
            .Select((statement, index) => (statement, index))
            .Where(static item => item.statement.Kind == MidLevelIrStatementKind.Assign && item.statement.TargetName == "box")
            .ToArray();
        var dropCalls = statements
            .Select((statement, index) => (statement, index))
            .Where(static item => item.statement.Value is MidLevelIrCallRValue { FunctionName: "Bump" })
            .ToArray();

        Assert.Equal(2, boxAssignments.Length);
        Assert.True(dropCalls.Length >= 2);
        Assert.True(dropCalls[0].index > boxAssignments[0].index);
        Assert.True(dropCalls[0].index < boxAssignments[1].index);
    }

    [Fact]
    public void ImportedTypeDestructorsResolveHelpersInTheirDefiningModule()
    {
        var result = Compile(
            """
            import Lib
            module Demo

            fn void Run() {
                stack Lib.Buffer box = new Lib.Buffer() { Value = 4 };
                return;
            }
            """,
            new CompilerOptions(
                ModuleResolver: new InMemoryModuleResolver(
                [
                    (
                        new ResolvedModuleReference("Lib", "/virtual/Lib.stark", IsExternal: false),
                        """
                        module Lib

                        fn void Bump(i32[-2147483648 2147483647] value) {
                            return;
                        }

                        public struct Buffer {
                            i32[-2147483648 2147483647] Value;

                            drop {
                                Bump(self.Value);
                            }
                        }
                        """,
                        "/virtual/Lib.stark"
                    )
                ])));

        Assert.True(result.Succeeded);

        var function = Assert.Single(GetMir(result).Functions, static function => function.Name == "Run");
        var statements = function.Blocks.SelectMany(static block => block.Statements).ToArray();
        Assert.Contains(
            statements,
            static statement => statement.Value is MidLevelIrCallRValue { FunctionName: "Lib.Bump" });
    }

    [Fact]
    public void EnumPayloadDropsLowerThroughActiveTagDispatch()
    {
        var result = Compile(
            """
            module Demo

            static mut i32[-2147483648 2147483647] Counter = 0;

            fn void Bump(i32[-2147483648 2147483647] value) {
                Counter = Counter + value;
                return;
            }

            struct Resource {
                i32[-2147483648 2147483647] Value;

                drop {
                    Bump(self.Value);
                }
            }

            enum Token {
                End,
                Text(Resource),
            }

            fn void Run() {
                stack Token token = Token.Text(new Resource() { Value = 4 });
                return;
            }
            """);

        Assert.True(result.Succeeded);

        var function = Assert.Single(GetMir(result).Functions, static function => function.Name == "Run");
        var statements = function.Blocks.SelectMany(static block => block.Statements).ToArray();

        Assert.Contains(function.Blocks, static block => block.Label.Contains("enum_drop_", StringComparison.Ordinal));
        Assert.Contains(
            statements,
            static statement => statement.Value is MidLevelIrCallRValue { FunctionName: "Bump" });
        Assert.Contains(
            statements,
            static statement => statement.Kind == MidLevelIrStatementKind.StorageDead && statement.TargetName == "token");
    }
}
