using Stark.Parsing;

namespace compiler.Tests;

public sealed class ParserConformanceTests
{
    public static TheoryData<string, string> ValidPrograms => new()
    {
        {
            "top level imports module and ordinary parameters",
            """
            import Core.Math
            export import Core.Text
            module Demo.Api

            public fn i32 Sum(i32 left, i32 right,) {
                return left + right;
            }
            """
        },
        {
            "record members support fields methods constructors and generics",
            """
            module Models

            record Pair<T>(T Left, T Right) {
                mut i32 Count;

                Pair(T left, T right) {
                    ;
                }

                fn i32 Width() {
                    return 2;
                }

                drop {
                    ;
                }
            }

            struct Buffer {
                rawptr<i8> Ptr;

                mut drop {
                    self.Ptr = null;
                }
            }
            """
        },
        {
            "alias declarations parse with visibility and generics",
            """
            module Types

            public alias Byte = i8;
            internal alias BufferView<T> = borrow T[];
            """
        },
        {
            "traits and doctrines accept constraints and semicolon bodies",
            """
            module Contracts

            trait Parser<T> {
                law T Parse(ascii text) where T: Node, Printable;
            }

            doctrine Numbers<T> {
                finite law T Clamp(T value, T min, T max) where T: Ordered;
            }
            """
        },
        {
            "complex nested type syntax parses",
            """
            module Types

            fn void Accept(
                borrow rawptr<i8>[] buffers,
                shared Matrix<Vector<i32[4]>> table,
                out i32[4][2] lanes,
                frozen i32[0 255][] levels)
            {
                return;
            }
            """
        },
        {
            "all loop behaviors and empty statements parse",
            """
            module Flow

            fn void Run(bool flag) {
                ;

                while infinite (flag) ;

                while non-deterministic (flag) {
                    break;
                }

                for non-deterministic (; flag; ) ;

                for willexit (stack mut i32 i = 0; i < 4; i += 1, i += 2) {
                    continue;
                }
            }
            """
        },
        {
            "switch sections may contain multiple labels and guards",
            """
            module Branching

            fn i32 Pick(i32 state) {
                switch w99 (state) {
                    case 0:
                    case 1:
                        return 1;
                    case var value when value > 10:
                        return value;
                    default:
                        return 0;
                }
            }
            """
        },
        {
            "postfix chains and trailing comma argument lists parse",
            """
            module Access

            struct Leaf {
                i32 Value;
            }

            struct Node {
                Leaf[2] Leaves;
            }

            fn Node GetNode(i32 index, i32 count,) {
                return new Node();
            }

            fn i32 Read() {
                return GetNode(1, 2,).Leaves[0].Value;
            }
            """
        },
        {
            "assignment precedence and unary operators parse",
            """
            module Operators

            fn i32 Compute(i32 left, i32 middle, i32 right, bool flag) {
                stack mut i32 value = left + middle * right << 1;
                value &= left ^ middle | right;
                value = flag ? value : left;
                return ~value;
            }
            """
        },
        {
            "qualified names may be used directly as expressions",
            """
            import Core.Math
            module Demo

            fn i32 Read() {
                return Core.Math.Constants.Value;
            }
            """
        },
        {
            "object and array initializers allow trailing commas",
            """
            module Init

            struct Item {
                i32 Value;
            }

            fn void Run() {
                stack Item item = new Item() { Value = 1, };
                stack i32[3] numbers = { 1, 2, 3, };
            }
            """
        },
        {
            "ffi raw pointer nesting parses",
            """
            module Interop

            export ffi fn rawptr<rawmutptr<i8>> Transform(rawptr<rawptr<i8>> input);
            """
        },
        {
            "negative range constraints combine with fixed arrays",
            """
            module Ranges

            fn void Accept(i32[-10 10][4] buckets) {
                return;
            }
            """
        }
    };

    public static TheoryData<string, string> InvalidPrograms => new()
    {
        {
            "wildcard imports are not part of the grammar",
            """
            import Core.*
            module Demo
            """
        },
        {
            "top level imports and module declarations do not end in semicolons",
            """
            import Core.Math;
            module Demo;
            """
        },
        {
            "function parameters do not support default values",
            """
            module Demo

            fn i32 Sum(i32 left = 1, i32 right) {
                return left + right;
            }
            """
        },
        {
            "alias declarations require an equals sign and target type",
            """
            module Demo

            alias Bytes;
            """
        },
        {
            "type is no longer the alias declaration keyword",
            """
            module Demo

            type Bytes = i8;
            """
        },
        {
            "doctrine members may not use fn",
            """
            module Demo

            doctrine Numbers {
                fn i32 Identity(i32 value);
            }
            """
        },
        {
            "var patterns require a capture identifier",
            """
            module Demo

            fn i32 Run(i32 value) {
                switch (value) {
                    case var:
                        return 1;
                    default:
                        return 0;
                }
            }
            """
        },
        {
            "where clauses require at least one constrained type",
            """
            module Demo

            fn T Echo<T>(T value) where T: {
                return value;
            }
            """
        },
        {
            "for headers still require both semicolons",
            """
            module Demo

            fn void Run(bool flag) {
                for willexit (stack i32 i = 0) {
                    ;
                }
            }
            """
        },
        {
            "legacy loop behavior alias spelling is no longer accepted",
            """
            module Demo

            fn void Run(bool flag) {
                for nondeterministic (; flag; ) {
                    ;
                }
            }
            """
        },
        {
            "object initializers require an expression on the right side",
            """
            module Demo

            struct Item {
                i32 Value;
            }

            fn void Run() {
                stack Item item = new Item() { Value = };
            }
            """
        },
        {
            "raw pointer types require a closing generic bracket",
            """
            module Demo

            fn void Run(rawptr<i32 value) {
                return;
            }
            """
        },
        {
            "top level declarations accept only one visibility modifier",
            """
            module Demo

            public export fn void Run();
            """
        },
        {
            "loop behavior must use the supported token forms",
            """
            module Demo

            fn void Run(bool flag) {
                while non deterministic (flag) {
                    break;
                }
            }
            """
        },
        {
            "argument lists still require comma separators",
            """
            module Demo

            fn void Write(i32 left, i32 right) {
                return;
            }

            fn void Run() {
                Write(1 2);
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
