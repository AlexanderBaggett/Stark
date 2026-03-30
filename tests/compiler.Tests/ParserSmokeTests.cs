using Stark.Parsing;

namespace compiler.Tests;

public sealed class ParserSmokeTests
{
    public static TheoryData<string, string> ValidPrograms => new()
    {
        {
            "minimal module",
            """
            module SomeModule
            """
        },
        {
            "imports and function modifiers",
            """
            import Core.Text
            export import Core.Math
            module Demo.Api

            public finite law i32 Add(i32 left, i32 right);
            internal inline hot fn void Trace(rawptr<i8> message);
            export ffi fn void Send(rawmutptr<i8> buffer);
            """
        },
        {
            "struct record trait and doctrine declarations",
            """
            module Shapes

            public struct Widget {
                i32 Value;
                Widget() { }
                fn void Reset() { return; }
            }

            public record Point(i32 X, i32 Y) { }

            public trait Comparable<T> {
                law i32 Compare(T other);
            }

            public doctrine Numbers<T> {
                finite law T Clamp(T value);
            }
            """
        },
        {
            "rust style enum declarations constructors and patterns",
            """
            module Variants

            public enum Result<T, E> {
                Ok(T),
                Err(E),
            }

            enum Token {
                End,
                Integer(i32),
                Move { X: i32, Y: i32 },
            }

            fn i32 Use(Result<i32, ascii> result, Token token) {
                stack Token end = Token.End;
                stack Token integer = Token.Integer(5);
                stack Token moveToken = Token.Move { X: 1, Y: 2 };

                switch (token) {
                    case Token.End:
                        return 0;
                    case Token.Integer(var value):
                        return value;
                    case Token.Move { X: var x, Y: var y }:
                        return x + y;
                }
            }
            """
        },
        {
            "qualified types pointers and constrained ranges",
            """
            module Memory

            public const i32 Answer = 42;
            internal static rawptr<i8> Buffer = null;
            export static i32[0 255] Limit = 255;

            public fn void Accept(
                borrow i8[] input,
                frozen ascii name,
                shared i32[0 10] state,
                out i8[16] output,
                init rawmutptr<i8> rawBuffer)
            {
                return;
            }
            """
        },
        {
            "blocks statements loops and expressions",
            """
            module Flow

            struct Widget {
                i32 Value;
            }

            fn i32 Run() {
                const i32 start = 0;
                stack mut i32 counter = start;
                stack Widget item = new Widget() { Value = 1 };
                stack i32[3] values = {1, 2, 3};
                stack f32 power = 2.0 ** 3.0;

                if w9 (counter == 0) {
                    counter += item.Value;
                } else {
                    counter = counter ? counter : 1;
                }

                counter ^= 1;

                while willexit (counter < 10) {
                    counter += 1;
                }

                for willexit (stack mut i32 i = 0; i < 3; i += 1) {
                    switch w1 (i) {
                        case 0:
                            continue;
                        case var value when value > 0:
                            counter += value;
                            break;
                        default:
                            break;
                    }
                }

                return counter;
            }
            """
        },
        {
            "aggregate switch patterns",
            """
            module Patterns

            record Pair(i32 Left, i32 Right) { }

            fn i32 Run(Pair value) {
                switch (value) {
                    case Pair(1, var right):
                        return right;
                    case Pair(_, _):
                        return 0;
                }
            }
            """
        },
        {
            "nested aggregate switch patterns",
            """
            module Patterns

            record Pair(i32 Left, i32 Right) { }
            record Outer(Pair Values, i32 Tail) { }

            fn i32 Run(Outer value) {
                switch (value) {
                    case Outer(Pair(1, var right), var tail):
                        return right + tail;
                    case Outer(Pair(_, _), _):
                        return 0;
                }
            }
            """
        },
        {
            "const aggregate literals and nested array members",
            """
            module Globals

            struct Inner {
                i32[2] Pair;
            }

            struct Outer {
                Inner Node;
                i32[3] View;
            }

            const Outer Frozen = {
                Node = { Pair = { 4, 7 } },
                View = { 1, 2, 3 }
            };

            static Outer Shared = {
                Node = { Pair = { 8, 9 } },
                View = { 5, 6, 7 }
            };
            """
        },
        {
            "generic function with constraint",
            """
            module GenericDemo

            public inlinehint finite T Echo<T>(T value) where T: Cloneable {
                return value;
            }
            """
        },
        {
            "strict floating point and explicit arithmetic operator spellings",
            """
            module Operators

            strictfp fn f32 Precise(f32 left, f32 right) {
                return left + right;
            }

            fn i32 Wrap(i32 left, i32 right) {
                left +%= right;
                return -%left +% right *% 2;
            }

            fn i32 Saturate(i32 left, i32 right) {
                left +|= right;
                return left +| right *| 2;
            }
            """
        },
        {
            "explicit conversions and raw pointer operators",
            """
            module UnsafeDemo

            struct Box {
                i32 Value;
            }

            static i32 Counter = 0;

            fn i32 Run(rawmutptr<i32> input, i64 bits) {
                stack mut Box box = new Box() { Value = 1 };
                *(&(box.Value)) = (i32)bits;
                stack rawptr<i32> alias = (rawptr<i32>)input;
                Counter = *alias;
                return *(&Counter);
            }
            """
        }
    };

    public static TheoryData<string, string> InvalidPrograms => new()
    {
        {
            "imports must come before module declaration",
            """
            module Demo
            import Core
            """
        },
        {
            "finite law keyword order is fixed",
            """
            module Demo

            law finite i32 Add(i32 left, i32 right);
            """
        },
        {
            "while loops require an explicit loop behavior",
            """
            module Demo

            fn void Run() {
                while (true) { }
            }
            """
        },
        {
            "compilation units require a module declaration",
            """
            public fn void Run();
            """
        },
        {
            "local variables require an explicit storage class",
            """
            module Demo

            fn void Run() {
                i32 value = 0;
            }
            """
        },
        {
            "doctorine spelling is no longer accepted",
            """
            module Demo

            public doctorine Numbers<T> {
                law T Clamp(T value);
            }
            """
        },
        {
            "class-style inheritance is not part of Stark",
            """
            module Demo

            struct Widget : BaseWidget {
            }
            """
        },
        {
            "constructor base initializer is not part of Stark",
            """
            module Demo

            struct Widget {
                i32 Value;
                Widget(i32 value) : base(value) {
                    return;
                }
            }
            """
        },
        {
            "prefix fixed array syntax is no longer accepted",
            """
            module Demo

            fn void Run() {
                stack [i32; 3] values = {1, 2, 3};
            }
            """
        },
        {
            "property switch patterns are not part of Stark",
            """
            module Demo

            struct Boxed {
                i32 Value;
            }

            fn i32 Run(Boxed value) {
                switch (value) {
                    case Boxed { Value: 1 }:
                        return 1;
                    default:
                        return 0;
                }
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
