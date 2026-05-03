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

            unsafe fn void Bump(i32[-2147483648 2147483647] value) {
                Counter = Counter + value;
                return;
            }

            struct Buffer {
                i32[-2147483648 2147483647] Value;

                drop {
                    Bump(self.Value);
                }
            }

            unsafe fn void Run() {
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
    public void EarlyReturnBranchDropDoesNotSuppressFallthroughScopeDrop()
    {
        var result = Compile(
            """
            module Demo

            static mut i32[-2147483648 2147483647] Counter = 0;

            unsafe fn void Bump(i32[-2147483648 2147483647] value) {
                Counter = Counter + value;
                return;
            }

            struct Buffer {
                i32[-2147483648 2147483647] Value;

                drop {
                    Bump(self.Value);
                }
            }

            unsafe fn void Run(bool fail) {
                stack Buffer box = new Buffer() { Value = 4 };
                if (fail) {
                    return;
                }

                return;
            }
            """);

        Assert.True(result.Succeeded);

        var function = Assert.Single(GetMir(result).Functions, static function => function.Name == "Run");
        var dropCalls = function.Blocks
            .SelectMany(static block => block.Statements)
            .Count(static statement => statement.Value is MidLevelIrCallRValue { FunctionName: "Bump" });

        Assert.Equal(2, dropCalls);
    }

    [Fact]
    public void ReassigningADestructibleLocalLowersTheOldDropBeforeOverwrite()
    {
        var result = Compile(
            """
            module Demo

            static mut i32[-2147483648 2147483647] Counter = 0;

            unsafe fn void Bump(i32[-2147483648 2147483647] value) {
                Counter = Counter + value;
                return;
            }

            struct Buffer {
                i32[-2147483648 2147483647] Value;

                drop {
                    Bump(self.Value);
                }
            }

            unsafe fn void Run() {
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
    public void DynamicStorageDropsInitializedElementsBeforeFreeingBackingStorage()
    {
        var result = Compile(
            """
            module Demo

            static mut i32[-2147483648 2147483647] Counter = 0;

            unsafe fn void Bump(i32[-2147483648 2147483647] value) {
                Counter = Counter + value;
                return;
            }

            struct Token {
                i32[-2147483648 2147483647] Value;

                drop {
                    Bump(self.Value);
                }
            }

            unsafe fn void Run() {
                stack mut dynamic Token values = new(2);
                init values[0] = new Token() { Value = 1 };
                init values[1] = new Token() { Value = 2 };
                return;
            }
            """);

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

        var function = Assert.Single(GetMir(result).Functions, static function => function.Name == "Run");
        Assert.Contains(function.Blocks, static block => block.Label.Contains("dynamic_drop_cond", StringComparison.Ordinal));
        Assert.Contains(function.Blocks, static block => block.Label.Contains("dynamic_drop_body", StringComparison.Ordinal));

        var statements = function.Blocks.SelectMany(static block => block.Statements).ToArray();
        Assert.Contains(statements, static statement => statement.Value is MidLevelIrCallRValue { FunctionName: "Bump" });
        Assert.Contains(statements, static statement => statement.Value is MidLevelIrDynamicStorageFreeRValue);
    }

    [Fact]
    public void EnumPayloadCaptureDropsMovedPayloadOnlyOnce()
    {
        var result = Compile(
            """
            module Demo

            static mut i32[-2147483648 2147483647] Counter = 0;

            unsafe fn void Bump(i32[-2147483648 2147483647] value) {
                Counter = Counter + value;
                return;
            }

            struct Resource {
                i32[-2147483648 2147483647] Value;

                drop {
                    Bump(self.Value);
                }
            }

            enum Slot {
                Free,
                Occupied(Resource),
            }

            unsafe fn void Run() {
                stack Slot slot = Slot.Occupied(new Resource() { Value = 7 });
                switch (slot) {
                    case Slot.Occupied(var payload):
                        stack Resource value = payload;
                    case Slot.Free:
                }

                return;
            }
            """);

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

        var function = Assert.Single(GetMir(result).Functions, static function => function.Name == "Run");
        var bumpCalls = function.Blocks
            .SelectMany(static block => block.Statements)
            .Count(static statement => statement.Value is MidLevelIrCallRValue { FunctionName: "Bump" });

        Assert.Equal(1, bumpCalls);
    }

    [Fact]
    public void PrimaryConstructorArgumentsMoveIntoReturnedOwnerDropState()
    {
        var result = Compile(
            """
            module Demo

            static mut i32[-2147483648 2147483647] Counter = 0;

            unsafe fn void Bump(i32[-2147483648 2147483647] value) {
                Counter = Counter + value;
                return;
            }

            struct Resource {
                i32[-2147483648 2147483647] Value;

                drop {
                    Bump(self.Value);
                }
            }

            record Box(Resource Item) {
            }

            unsafe fn void Run() {
                stack Resource owned = new Resource() { Value = 5 };
                stack Box box = new Box(owned);
                return;
            }
            """);

        Assert.True(result.Succeeded);

        var function = Assert.Single(GetMir(result).Functions, static function => function.Name == "Run");
        var bumpCalls = function.Blocks
            .SelectMany(static block => block.Statements)
            .Count(static statement => statement.Value is MidLevelIrCallRValue { FunctionName: "Bump" });

        Assert.Equal(1, bumpCalls);
    }

    [Fact]
    public void ExplicitConstructorArgumentsMoveIntoReturnedOwnerDropState()
    {
        var result = Compile(
            """
            module Demo

            static mut i32[-2147483648 2147483647] Counter = 0;

            unsafe fn void Bump(i32[-2147483648 2147483647] value) {
                Counter = Counter + value;
                return;
            }

            struct Resource {
                i32[-2147483648 2147483647] Value;

                drop {
                    Bump(self.Value);
                }
            }

            struct Box {
                Resource Item;

                Box(Resource item) {
                    self.Item = item;
                }
            }

            unsafe fn void Run() {
                stack Resource owned = new Resource() { Value = 5 };
                stack Box box = new Box(owned);
                return;
            }
            """);

        Assert.True(result.Succeeded);

        var function = Assert.Single(GetMir(result).Functions, static function => function.Name == "Run");
        var bumpCalls = function.Blocks
            .SelectMany(static block => block.Statements)
            .Count(static statement => statement.Value is MidLevelIrCallRValue { FunctionName: "Bump" });

        Assert.Equal(1, bumpCalls);
    }

    [Fact]
    public void FixedArrayElementsCascadeRuntimeDrops()
    {
        var result = Compile(
            """
            module Demo

            static mut i32[-2147483648 2147483647] Counter = 0;

            unsafe fn void Bump(i32[-2147483648 2147483647] value) {
                Counter = Counter + value;
                return;
            }

            struct Resource {
                i32[-2147483648 2147483647] Value;

                drop {
                    Bump(self.Value);
                }
            }

            unsafe fn void Run() {
                stack Resource first = new Resource() { Value = 1 };
                stack Resource second = new Resource() { Value = 2 };
                stack Resource[2] resources = { first, second };
                return;
            }
            """);

        Assert.True(result.Succeeded);

        var function = Assert.Single(GetMir(result).Functions, static function => function.Name == "Run");
        var bumpCalls = function.Blocks
            .SelectMany(static block => block.Statements)
            .Count(static statement => statement.Value is MidLevelIrCallRValue { FunctionName: "Bump" });

        Assert.Equal(2, bumpCalls);
    }

    [Fact]
    public void HeapBackedFixedArrayElementsDropBeforeStorageDead()
    {
        var result = Compile(
            """
            module Demo

            static mut i32[-2147483648 2147483647] Counter = 0;

            unsafe fn void Bump(i32[-2147483648 2147483647] value) {
                Counter = Counter + value;
                return;
            }

            struct Resource {
                i32[-2147483648 2147483647] Value;

                drop {
                    Bump(self.Value);
                }
            }

            unsafe fn void Run() {
                stack Resource first = new Resource() { Value = 1 };
                stack Resource second = new Resource() { Value = 2 };
                heap Resource[2] resources = { first, second };
                return;
            }
            """);

        Assert.True(result.Succeeded);

        var function = Assert.Single(GetMir(result).Functions, static function => function.Name == "Run");
        var statements = function.Blocks.SelectMany(static block => block.Statements).ToArray();
        var bumpCallIndexes = statements
            .Select((statement, index) => (statement, index))
            .Where(static item => item.statement.Value is MidLevelIrCallRValue { FunctionName: "Bump" })
            .Select(static item => item.index)
            .ToArray();
        var storageDeadIndex = Array.FindIndex(
            statements,
            static statement => statement.Kind == MidLevelIrStatementKind.StorageDead && statement.TargetName == "resources");

        Assert.Equal(2, bumpCallIndexes.Length);
        Assert.True(storageDeadIndex > bumpCallIndexes[^1]);
    }

    [Fact]
    public void ImportedTypeDestructorsResolveHelpersInTheirDefiningModule()
    {
        var result = Compile(
            """
            import Lib
            module Demo

            unsafe fn void Run() {
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

                        unsafe fn void Bump(i32[-2147483648 2147483647] value) {
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

            unsafe fn void Bump(i32[-2147483648 2147483647] value) {
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

            unsafe fn void Run() {
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
