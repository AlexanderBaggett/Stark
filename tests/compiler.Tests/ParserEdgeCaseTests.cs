using Stark.Parsing;

namespace compiler.Tests;

public sealed class ParserEdgeCaseTests
{
    public static TheoryData<string, string> ValidPrograms => new()
    {
        {
            "nested generics and negative range constraints parse",
            """
            module Types

            fn Matrix<Vector<rawptr<i8>>> Project(
                Matrix<Vector<rawptr<i8>>> table,
                i32[-10 10][] ranges)
            {
                return table;
            }
            """
        },
        {
            "trailing separators survive across calls object initializers and declarations",
            """
            module Trailing

            struct Node {
                i32 Value;
            }

            fn Node Build(Node node, i32 count,) {
                return new Node() { Value = count, };
            }

            fn i32 Read() {
                return Build(new Node(), 1,).Value;
            }
            """
        },
        {
            "loop behavior spellings remain parseable in nested loops",
            """
            module Flow

            fn void Run(bool flag) {
                while infinite (flag) {
                    for nondeterministic (stack i32 i = 0; i < 1; i += 1) {
                        continue;
                    }
                }
            }
            """
        },
        {
            "nested postfix chains with indexing and member access parse",
            """
            module Access

            struct Leaf {
                i32 Value;
            }

            struct Node {
                Leaf[2] Leaves;
            }

            fn i32 Read() {
                return new Node().Leaves[0].Value;
            }
            """
        },
        {
            "deeply nested generic arguments with range-bearing integers parse",
            """
            module Ranges

            fn rawptr<Vector<i32[-2147483648 2147483647]>> Make(
                rawptr<Vector<i32[-10 10]>> input)
            {
                return input;
            }
            """
        }
    };

    public static TheoryData<string, string> InvalidPrograms => new()
    {
        {
            "argument lists still reject doubled separators",
            """
            module Demo

            fn void Write(i32 left, i32 right) {
                return;
            }

            fn void Run() {
                Write(1, 2,,);
            }
            """
        },
        {
            "nested generic brackets must balance",
            """
            module Demo

            fn void Run(rawptr<rawmutptr<i8> value) {
                return;
            }
            """
        },
        {
            "range constraints must contain exactly two bounds",
            """
            module Demo

            fn void Run(i32[-10 10 20] value) {
                return;
            }
            """
        },
        {
            "postfix chains cannot contain empty member segments",
            """
            module Demo

            fn void Run() {
                return new Node()..Value;
            }
            """
        },
        {
            "loop behavior spellings are tokenized not identifier based",
            """
            module Demo

            fn void Run(bool flag) {
                while non_deterministic (flag) {
                    break;
                }
            }
            """
        },
        {
            "object initializer members still require a single expression",
            """
            module Demo

            struct Item {
                i32 Value;
            }

            fn void Run() {
                stack Item item = new Item() { Value = 1,, };
            }
            """
        }
    };

    [Theory]
    [MemberData(nameof(ValidPrograms))]
    public void ValidProgramsParse(string scenario, string source)
    {
        var result = StarkSyntax.ParseCompilationUnit(source);

        Assert.True(
            result.Succeeded,
            $"{scenario} should parse successfully, but reported {result.SyntaxErrorCount} syntax error(s).");
        Assert.Equal(0, result.SyntaxErrorCount);
    }

    [Theory]
    [MemberData(nameof(InvalidPrograms))]
    public void InvalidProgramsDoNotParse(string scenario, string source)
    {
        var result = StarkSyntax.ParseCompilationUnit(source);

        Assert.False(
            result.Succeeded,
            $"{scenario} should fail to parse, but the parser reported success.");
        Assert.True(
            result.SyntaxErrorCount > 0,
            $"{scenario} should report at least one syntax error.");
    }
}
